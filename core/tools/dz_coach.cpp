// Phase 3 AI feedback helper (R4.5, R4.7): a local base model (GGUF, the user's choice) continues the
// coach document for one take; the app runs it as its own process, so a crash never reaches it.
//   dz_coach facts session.json                       the fact lines (no model)
//   dz_coach prompt session.json [examples]           the document the model continues
//   dz_coach run model.gguf session.json [options]    the checked report on stdout
//   dz_coach eval model.gguf dir [options]            R4.6: the model scored on the sessions in dir
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
#include <regex>
#include <sstream>
#include <string>
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
    constexpr size_t kReserve = size_t(1536) << 20;
    if (gpu >= 0) p.gpuLayers = gpu;
    else if (!p.gpu.empty() && p.layers > 0 && p.gpuFree > kReserve)
        p.gpuLayers = std::min(p.layers + 1, int((p.gpuFree - kReserve) / (p.model / size_t(p.layers + 1))));
    const double onGpu = p.layers > 0 ? std::min(1.0, double(p.gpuLayers) / (p.layers + 1)) : 0;
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
};

// One model, loaded once; a context per take.
struct Coach {
    llama_model* model = nullptr;
    const llama_vocab* vocab = nullptr;

    bool load(const std::string& path, int gpu) {
        const Plan p = plan(path, gpu);
        std::fprintf(stderr, "plan: %s, %d of %d layers on the GPU (%s)\n", p.verdict.c_str(), p.gpuLayers, p.layers + 1, p.gpu.empty() ? "no GPU" : p.gpu.c_str());
        llama_backend_init();
        llama_model_params mp = llama_model_default_params();
        mp.n_gpu_layers = p.gpuLayers;
        model = llama_model_load_from_file(path.c_str(), mp);
        if (model) vocab = llama_model_get_vocab(model);
        return model != nullptr;
    }
    ~Coach() { if (model) llama_model_free(model); }

    // The report continued from doc, checked; up to `tries` times until one passes.
    Generated generate(const std::string& doc, const Facts& f, uint32_t seed, int tries) {
        Generated g;
        const int n = -llama_tokenize(vocab, doc.c_str(), int32_t(doc.size()), nullptr, 0, true, false);
        std::vector<llama_token> tokens(static_cast<size_t>(n));
        llama_tokenize(vocab, doc.c_str(), int32_t(doc.size()), tokens.data(), n, true, false);
        constexpr int kMaxReport = 600;   // tokens
        llama_context_params cp = llama_context_default_params();
        cp.n_ctx = uint32_t(n + kMaxReport + 16);
        cp.n_batch = cp.n_ctx;
        llama_context* ctx = llama_init_from_model(model, cp);
        const auto t0 = std::chrono::steady_clock::now();
        if (!ctx || llama_decode(ctx, llama_batch_get_one(tokens.data(), n)) != 0) {
            std::fprintf(stderr, "cannot read the prompt (%d tokens)\n", n);
            if (ctx) llama_free(ctx);
            return g;
        }
        for (int t = 0; t < tries; t++) {
            if (t) llama_memory_seq_rm(llama_get_memory(ctx), 0, n, -1);   // back to the end of the prompt
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
        llama_free(ctx);
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
            std::printf("gpu %s\ngpu_free_mb %zu\ngpu_total_mb %zu\nram_mb %zu\nram_free_mb %zu\nmodel_mb %zu\nlayers %d\ngpu_layers %d\nverdict %s\n",
                        p.gpu.empty() ? "-" : p.gpu.c_str(), p.gpuFree >> 20, p.gpuTotal >> 20, p.ram >> 20, p.ramFree >> 20, p.model >> 20, p.layers + 1,
                        p.gpuLayers, p.verdict.c_str());
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
        if ((mode == "run" || mode == "eval") && argc > 3) {
            fs::path ex = default_examples(argv[0]);
            int gpu = -1, tries = mode == "eval" ? 1 : 3;
            uint32_t seed = 1;
            for (int i = 4; i < argc; i++) {
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
