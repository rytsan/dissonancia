// Phase 3 AI feedback helper (R4.5, R4.7): a local base model (GGUF, the user's choice) continues the
// coach document for one take; the app runs it as its own process, so a crash never reaches it.
//   dz_coach facts session.json                       the fact lines (no model)
//   dz_coach prompt session.json [examples]           the document the model continues
//   dz_coach run model.gguf session.json [options]    the checked report on stdout
//   dz_coach eval model.gguf dir [options]            R4.6: the model scored on the sessions in dir
//   dz_coach serve model.gguf [options]               kept open: a session JSON path per line in, its report and "@@end ..." out
// options: --examples dir (default: the coach/examples next to the binary, else the source tree's),
// --gpu layers|auto (default auto: as many as the free GPU memory holds; 0: CPU), --seed n, --tries n
// (default 3), --verbose.
//   dz_coach probe model.gguf                         this machine with that model: GPU, RAM, layers on the GPU, verdict
// Exit codes: 0 a report that passed the check, 3 the best one after the tries failed it, 1 an error.
#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <regex>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

#include "coach.hpp"
#include "gguf.h"
#include "llama.h"
#ifdef _WIN32
#include <windows.h>
#endif

namespace fs = std::filesystem;
using namespace dz::coach;

namespace {

std::string slurp(const fs::path& p) {
    std::ifstream f(p, std::ios::binary);
    if (!f) throw std::runtime_error("cannot read " + p.string());
    std::stringstream s;
    s << f.rdbuf();
    return s.str();
}

// Worked examples: dir/*.json (a session) with dir/*.txt (its report, written by us).
std::vector<Example> examples(const fs::path& dir) {
    std::vector<fs::path> files;
    for (const auto& e : fs::directory_iterator(dir))
        if (e.path().extension() == ".json") files.push_back(e.path());
    std::sort(files.begin(), files.end());
    std::vector<Example> out;
    for (const fs::path& j : files) out.push_back({j.stem().string(), facts(slurp(j)), slurp(fs::path(j).replace_extension(".txt"))});
    return out;
}

// The repertoire beside the examples (examples/../exercises.txt), empty when missing.
Exercises repertoire(const fs::path& examplesDir) {
    const fs::path p = examplesDir.parent_path() / "exercises.txt";
    return fs::exists(p) ? parse_exercises(slurp(p)) : Exercises{};
}

// This take's document: the closest examples from the bank, then the take with its repertoire.
std::string document(const fs::path& examplesDir, const std::vector<Example>& bank, const Facts& f) {
    return prompt(select(bank, f), f, repertoire(examplesDir));
}

fs::path default_examples(const char* argv0) {
    const fs::path here = fs::absolute(argv0).parent_path() / "coach" / "examples";
    if (fs::exists(here)) return here;
#ifdef DZ_COACH_DIR
    return fs::path(DZ_COACH_DIR) / "examples";
#else
    return here;
#endif
}

bool verbose = false;

// The machine, read at run time: the GPUs ggml found (their free memory), the RAM, the model's size
// and layers (from its GGUF header, without loading it), and how many layers fit on the GPU.
struct Plan {
    std::string gpu;          // the GPU used, "" = none
    size_t gpuFree = 0, gpuTotal = 0, ram = 0, ramFree = 0, model = 0;
    int layers = 0;           // the model's blocks
    int gpuLayers = 0;        // n_gpu_layers to load with (layers + 1: all, the output too)
    size_t experts = 0;       // bytes of a mixture of experts' expert tensors (0: not one)
    int cpuExperts = 0;       // MoE: every layer on the GPU but the experts of the first ones, kept on the CPU
    std::string verdict;      // gpu | partial | cpu | too-big
};

size_t ram_available(size_t& total) {
#ifdef _WIN32
    MEMORYSTATUSEX m{sizeof m};
    if (!GlobalMemoryStatusEx(&m)) return 0;
    total = size_t(m.ullTotalPhys);
    return size_t(m.ullAvailPhys);
#else
    std::ifstream f("/proc/meminfo");
    size_t avail = 0;
    for (std::string k; f >> k;) {
        size_t kb = 0;
        f >> kb;
        if (k == "MemTotal:") total = kb * 1024;
        if (k == "MemAvailable:") avail = kb * 1024;
        f.ignore(64, '\n');
    }
    return avail;
#endif
}

// gpu < 0: as many layers as the free GPU memory holds, after ~1.5 GB for the context and the
// compute buffers (a 3k-token context; measured 0.5 GB of compute on CPU).
Plan plan(const std::string& modelPath, int gpu) {
    Plan p;
    p.model = size_t(fs::file_size(modelPath));
    gguf_init_params gp{true, nullptr};
    if (gguf_context* g = gguf_init_from_file(modelPath.c_str(), gp)) {
        const int64_t a = gguf_find_key(g, "general.architecture");
        if (a >= 0) {
            const int64_t b = gguf_find_key(g, (std::string(gguf_get_val_str(g, a)) + ".block_count").c_str());
            if (b >= 0) p.layers = int(gguf_get_val_u32(g, b));
        }
        for (int64_t i = 0; i < gguf_get_n_tensors(g); i++)
            if (std::strstr(gguf_get_tensor_name(g, i), "_exps")) p.experts += gguf_get_tensor_size(g, i);
        gguf_free(g);
    }
    for (size_t i = 0; i < ggml_backend_dev_count(); i++) {
        ggml_backend_dev_t d = ggml_backend_dev_get(i);
        if (ggml_backend_dev_type(d) != GGML_BACKEND_DEVICE_TYPE_GPU) continue;
        size_t free = 0, total = 0;
        ggml_backend_dev_memory(d, &free, &total);
        if (free > p.gpuFree) p.gpuFree = free, p.gpuTotal = total, p.gpu = ggml_backend_dev_description(d);
    }
    p.ramFree = ram_available(p.ram);
    constexpr size_t kReserve = size_t(1536) << 20;   // 1 GB: 22 layers of experts on the CPU, 8.2 against 7.7 tokens/s; kept wide for what else takes GPU memory
    if (gpu >= 0) p.gpuLayers = gpu;
    else if (!p.gpu.empty() && p.layers > 0 && p.gpuFree > kReserve) {
        const size_t room = p.gpuFree - kReserve;
        p.gpuLayers = std::min(p.layers + 1, int(room / (p.model / size_t(p.layers + 1))));
        // A mixture of experts that does not fit: everything but the experts goes to the GPU (attention
        // and the shared weights run every token), and only as many layers' experts as fit stay there.
        if (p.gpuLayers <= p.layers && p.experts > 0 && p.model - p.experts < room) {
            const size_t perLayer = p.experts / size_t(p.layers);
            const size_t over = p.model - room;
            p.cpuExperts = std::min(p.layers, int((over + perLayer - 1) / perLayer));
            p.gpuLayers = p.layers + 1;
        }
    }
    double onGpu = p.layers > 0 ? std::min(1.0, double(p.gpuLayers) / (p.layers + 1)) : 0;
    if (p.cpuExperts > 0) onGpu = 1 - double(p.experts) * p.cpuExperts / p.layers / double(p.model);
    const double onCpu = double(p.model) * (1 - onGpu);
    p.verdict = p.ramFree > 0 && onCpu > 0.9 * double(p.ramFree) ? "too-big" : onGpu >= 1 ? "gpu" : onGpu > 0 ? "partial" : "cpu";
    return p;
}

struct Generated {
    Checked best;
    std::string raw;   // the best try as written
    int tries = 0;
    bool ok = false;
    double seconds = 0;   // reading the prompt and writing the tries
    double promptSeconds = 0;
    int prompt = 0, reused = 0, written = 0;   // tokens: the prompt, its part already read, the tries' output
};

// One model, loaded once; a context per take.
struct Coach {
    llama_model* model = nullptr;
    const llama_vocab* vocab = nullptr;

    bool load(const std::string& path, int gpu) {
        const Plan p = plan(path, gpu);
        std::fprintf(stderr, "plan: %s, %d of %d layers on the GPU%s (%s)\n", p.verdict.c_str(), p.gpuLayers, p.layers + 1,
                     p.cpuExperts ? (", the experts of " + std::to_string(p.cpuExperts) + " on the CPU").c_str() : "", p.gpu.empty() ? "no GPU" : p.gpu.c_str());
        llama_backend_init();
        llama_model_params mp = llama_model_default_params();
        mp.n_gpu_layers = p.gpuLayers;
        // MoE: the first layers' experts on the CPU ("blk.N.ffn_up_exps" and the like; not the shared experts).
        static std::string pattern;
        static llama_model_tensor_buft_override overrides[2] = {};
        if (p.cpuExperts > 0) {
            pattern = "^blk\\.(";
            for (int i = 0; i < p.cpuExperts; i++) pattern += (i ? "|" : "") + std::to_string(i);
            pattern += ")\\.ffn_(up|down|gate|gate_up)_exps";
            overrides[0] = {pattern.c_str(), ggml_backend_dev_buffer_type(ggml_backend_dev_by_type(GGML_BACKEND_DEVICE_TYPE_CPU))};
            overrides[1] = {nullptr, nullptr};
            mp.tensor_buft_overrides = overrides;
        }
        model = llama_model_load_from_file(path.c_str(), mp);
        if (model) vocab = llama_model_get_vocab(model);
        return model != nullptr;
    }
    void release() {
        if (ctx) llama_free(ctx), ctx = nullptr;
        if (model) llama_model_free(model), model = nullptr;
    }

    // The report continued from doc, checked; up to `tries` times until one passes.
    // One context, kept. The document is read in two parts: the header and the examples, then this
    // take. The state after the first part is kept (a snapshot: a hybrid model's recurrent state cannot
    // be cut back to a prefix), so a report with the same examples reads only its take.
    llama_context* ctx = nullptr;
    uint32_t nCtx = 0;
    std::vector<llama_token> shared;      // the first part's tokens, as last read
    std::vector<uint8_t> sharedState, promptState;

    std::vector<llama_token> tokenize(const std::string& text, bool first) {
        const int n = -llama_tokenize(vocab, text.c_str(), int32_t(text.size()), nullptr, 0, first, false);
        std::vector<llama_token> t(static_cast<size_t>(std::max(0, n)));
        llama_tokenize(vocab, text.c_str(), int32_t(text.size()), t.data(), n, first, false);
        return t;
    }
    std::vector<uint8_t> save() {
        std::vector<uint8_t> b(llama_state_seq_get_size(ctx, 0));
        b.resize(llama_state_seq_get_data(ctx, b.data(), b.size(), 0));
        return b;
    }
    bool restore(const std::vector<uint8_t>& b) {
        llama_memory_clear(llama_get_memory(ctx), true);
        return !b.empty() && llama_state_seq_set_data(ctx, b.data(), b.size(), 0) == b.size();
    }

    Generated generate(const std::string& doc, const Facts& f, uint32_t seed, int tries) {
        Generated g;
        const size_t split = doc.rfind("\n### Take ");
        const std::vector<llama_token> a = tokenize(doc.substr(0, split + 1), true), b = tokenize(doc.substr(split + 1), false);
        const int n = int(a.size() + b.size());
        constexpr int kMaxReport = 600;   // tokens
        const auto t0 = std::chrono::steady_clock::now();
        if (!ctx || nCtx < uint32_t(n + kMaxReport + 16)) {
            if (ctx) llama_free(ctx);
            llama_context_params cp = llama_context_default_params();
            cp.n_ctx = nCtx = std::max<uint32_t>(4096, uint32_t(n + kMaxReport + 16));
            cp.n_batch = cp.n_ctx;
            // The physical cores (half the logical CPUs): the experts on the CPU are bound by memory
            // bandwidth; every logical CPU contends for it (10: 6.6 tokens/s, 5: 8.1, ggml's 4: 7.7).
            cp.n_threads = cp.n_threads_batch = int(std::max(1u, std::thread::hardware_concurrency() / 2));
            ctx = llama_init_from_model(model, cp);
            shared.clear();
        }
        bool ok = ctx != nullptr;
        if (ok && a == shared && restore(sharedState)) g.reused = int(a.size());
        else if (ok) {
            llama_memory_clear(llama_get_memory(ctx), true);
            std::vector<llama_token> first = a;
            ok = llama_decode(ctx, llama_batch_get_one(first.data(), int32_t(first.size()))) == 0;
            if (ok) shared = a, sharedState = save();
        }
        std::vector<llama_token> rest = b;
        ok = ok && llama_decode(ctx, llama_batch_get_one(rest.data(), int32_t(rest.size()))) == 0;
        if (!ok) {
            std::fprintf(stderr, "cannot read the prompt (%d tokens)\n", n);
            shared.clear();
            return g;
        }
        promptState = save();
        g.prompt = n;
        g.promptSeconds = std::chrono::duration<double>(std::chrono::steady_clock::now() - t0).count();
        for (int t = 0; t < tries; t++) {
            if (t && !restore(promptState)) break;   // back to the end of the prompt
            llama_sampler* s = llama_sampler_chain_init(llama_sampler_chain_default_params());
            llama_sampler_chain_add(s, llama_sampler_init_grammar(vocab, grammar(f).c_str(), "root"));
            llama_sampler_chain_add(s, llama_sampler_init_top_p(0.9f, 1));
            llama_sampler_chain_add(s, llama_sampler_init_temp(0.6f));
            llama_sampler_chain_add(s, llama_sampler_init_dist(seed + uint32_t(t)));
            std::string out;
            for (int k = 0; k < kMaxReport; k++) {
                llama_token tok = llama_sampler_sample(s, ctx, -1);
                if (llama_vocab_is_eog(vocab, tok)) break;
                char piece[256];
                const int len = llama_token_to_piece(vocab, tok, piece, sizeof piece, 0, false);
                if (len > 0) out.append(piece, size_t(len));
                g.written++;
                if (llama_decode(ctx, llama_batch_get_one(&tok, 1)) != 0) break;
            }
            llama_sampler_free(s);
            const Checked c = check(out, f);
            g.tries = t + 1;
            if (verbose) std::fprintf(stderr, "--- try %d: %d items, %d dropped\n%s", t + 1, c.items, c.dropped, out.c_str());
            if (t == 0 || (c.ok && !g.best.ok) || c.dropped < g.best.dropped) g.best = c, g.raw = out;
            if (c.ok) { g.ok = true; break; }
        }
        g.seconds = std::chrono::duration<double>(std::chrono::steady_clock::now() - t0).count();
        std::fprintf(stderr, "time: prompt %d tokens (%d reused) %.1f s, report %d tokens %.1f s (%.1f tokens/s)\n", g.prompt, g.reused, g.promptSeconds,
                     g.written, g.seconds - g.promptSeconds, g.written / std::max(1e-3, g.seconds - g.promptSeconds));
        return g;
    }
};

}  // namespace

int main(int argc, char** argv) {
    if (argc < 3) {
        std::fprintf(stderr, "usage: dz_coach facts|prompt session.json [examples] | dz_coach run|eval model.gguf session.json|dir [--examples dir] [--gpu n] [--seed n] [--tries n] [--verbose]\n");
        return 1;
    }
    llama_log_set([](ggml_log_level level, const char* text, void*) { if (verbose || level >= GGML_LOG_LEVEL_ERROR) std::fputs(text, stderr); }, nullptr);
    ggml_backend_load_all_from_path(fs::absolute(argv[0]).parent_path().string().c_str());   // the backends this machine can run
    try {
        const std::string mode = argv[1];
        if (mode == "probe") {   // what this machine does with the model, before loading it
            const Plan p = plan(argv[2], -1);
            std::printf("gpu %s\ngpu_free_mb %zu\ngpu_total_mb %zu\nram_mb %zu\nram_free_mb %zu\nmodel_mb %zu\nexperts_mb %zu\nlayers %d\ngpu_layers %d\ncpu_experts %d\nverdict %s\n",
                        p.gpu.empty() ? "-" : p.gpu.c_str(), p.gpuFree >> 20, p.gpuTotal >> 20, p.ram >> 20, p.ramFree >> 20, p.model >> 20, p.experts >> 20,
                        p.layers + 1, p.gpuLayers, p.cpuExperts, p.verdict.c_str());
            return 0;
        }
        if (mode == "facts") {
            for (const std::string& l : facts(slurp(argv[2])).lines) std::printf("%s\n", l.c_str());
            return 0;
        }
        if (mode == "prompt") {
            const fs::path ex = argc > 3 ? fs::path(argv[3]) : default_examples(argv[0]);
            std::fputs(document(ex, examples(ex), facts(slurp(argv[2]))).c_str(), stdout);
            return 0;
        }
        if (((mode == "run" || mode == "eval") && argc > 3) || (mode == "serve" && argc > 2)) {
            fs::path ex = default_examples(argv[0]);
            int gpu = -1, tries = mode == "eval" ? 1 : 3;
            uint32_t seed = 1;
            for (int i = mode == "serve" ? 3 : 4; i < argc; i++) {
                const std::string a = argv[i];
                if (a == "--verbose") verbose = true;
                else if (i + 1 < argc && a == "--examples") ex = argv[++i];
                else if (i + 1 < argc && a == "--gpu") gpu = std::strcmp(argv[i + 1], "auto") == 0 ? (i++, -1) : std::atoi(argv[++i]);
                else if (i + 1 < argc && a == "--seed") seed = uint32_t(std::atoi(argv[++i]));
                else if (i + 1 < argc && a == "--tries") tries = std::max(1, std::atoi(argv[++i]));
            }
            const std::vector<Example> shots = examples(ex);
            Coach coach;
            if (!coach.load(argv[2], gpu)) { std::fprintf(stderr, "cannot load %s\n", argv[2]); return 1; }
            if (mode == "serve") {
                // The app keeps it open: one session JSON path per line in, its report out, then
                // "@@end <exit code> <items> <dropped> <seconds>"; the model is loaded once.
                std::printf("@@ready\n");
                std::fflush(stdout);
                for (std::string line; std::getline(std::cin, line);) {
                    if (line.empty()) continue;
                    int code = 1, items = 0, dropped = 0;
                    double secs = 0;
                    try {
                        const Facts f = facts(slurp(line));
                        const Generated g = coach.generate(document(ex, shots, f), f, seed, tries);
                        for (const std::string& d : g.best.droppedItems) std::fprintf(stderr, "dropped %s\n", d.c_str());
                        std::fputs(g.best.report.c_str(), stdout);
                        code = g.tries == 0 ? 1 : g.ok ? 0 : 3, items = g.best.items, dropped = g.best.dropped, secs = g.seconds;
                    } catch (const std::exception& e) {
                        std::fprintf(stderr, "dz_coach: %s\n", e.what());
                    }
                    std::printf("@@end %d %d %d %.0f\n", code, items, dropped, secs);
                    std::fflush(stdout);
                }
                return 0;
            }
            if (mode == "run") {
                const Facts f = facts(slurp(argv[3]));
                const Generated g = coach.generate(document(ex, shots, f), f, seed, tries);
                for (const std::string& d : g.best.droppedItems) std::fprintf(stderr, "dropped %s\n", d.c_str());
                std::fputs(g.best.report.c_str(), stdout);
                std::fprintf(stderr, "checked: %d items, %d dropped, %d tries, %s, %.0f s\n", g.best.items, g.best.dropped, g.tries, g.ok ? "ok" : "failed", g.seconds);
                return g.tries == 0 ? 1 : g.ok ? 0 : 3;
            }
            // R4.6: the model on the fixed sessions in a directory (one try each, as written).
            // passed: the check kept every section and dropped at most a third (the report is used as
            // it came, no second try); invented: items with a number the facts do not hold; dropped:
            // those and the rest the check removed (a fact to work on told as going well, a repeat, a
            // comparison with an earlier take); cited: kept items that quote a value; low confidence
            // said: on a take with low confidence, the report has its Confiança section.
            std::vector<fs::path> files;
            for (const auto& e : fs::directory_iterator(argv[3]))
                if (e.path().extension() == ".json") files.push_back(e.path());
            std::sort(files.begin(), files.end());
            int passed = 0, items = 0, invented = 0, dropped = 0, cited = 0, kept = 0, low = 0, hedged = 0;
            static const std::regex digit(R"(\d)");
            for (const fs::path& p : files) {
                const Facts f = facts(slurp(p));
                const Generated g = coach.generate(document(ex, shots, f), f, seed, tries);
                const Checked& c = g.best;
                passed += g.ok, items += c.items, invented += c.invented, dropped += c.dropped;
                std::istringstream lines(c.report);
                for (std::string l; std::getline(lines, l);)
                    if (l.rfind("- ", 0) == 0) kept++, cited += std::regex_search(l, digit);
                const bool hedge = c.hedged;
                if (f.lowConfidence) low++, hedged += hedge;
                std::printf("== %s: %s, %d items, %d dropped%s\n%s", p.filename().string().c_str(), g.ok ? "passed" : "FAILED", c.items, c.dropped,
                            f.lowConfidence ? (hedge ? ", low confidence said" : ", low confidence NOT said") : "", g.raw.c_str());
                for (const std::string& d : c.droppedItems) std::printf("   dropped %s\n", d.c_str());
            }
            std::printf("eval %zu takes: passed %d, items %d, invented %d (%.0f %%), dropped %d, cited %d of %d kept, low confidence said %d of %d\n",
                        files.size(), passed, items, invented, items ? 100.0 * invented / items : 0.0, dropped, cited, kept, hedged, low);
            return 0;
        }
    } catch (const std::exception& e) {
        std::fprintf(stderr, "dz_coach: %s\n", e.what());
        return 1;
    }
    std::fprintf(stderr, "dz_coach: unknown mode %s\n", argv[1]);
    return 1;
}
