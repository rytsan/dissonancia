// Phase 3 AI feedback helper (R4.5, R4.7): a local base model (GGUF, the user's choice) continues the
// coach document for one take; the app runs it as its own process, so a crash never reaches it.
//   dz_coach facts session.json                       the fact lines (no model)
//   dz_coach prompt session.json [examples]           the document the model continues
//   dz_coach run model.gguf session.json [options]    the checked report on stdout
//   dz_coach eval model.gguf dir [options]            R4.6: the model scored on the sessions in dir
// options: --examples dir (default: the coach/examples next to the binary, else the source tree's),
// --gpu layers (default 0: CPU), --seed n, --tries n (default 3), --verbose.
// Exit codes: 0 a report that passed the check, 3 the best one after the tries failed it, 1 an error.
#include <algorithm>
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
#include "llama.h"

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
    for (const fs::path& j : files) out.push_back({facts(slurp(j)), slurp(fs::path(j).replace_extension(".txt"))});
    return out;
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

struct Generated {
    Checked best;
    std::string raw;   // the best try as written
    int tries = 0;
    bool ok = false;
};

// One model, loaded once; a context per take.
struct Coach {
    llama_model* model = nullptr;
    const llama_vocab* vocab = nullptr;

    bool load(const std::string& path, int gpu) {
        llama_log_set([](ggml_log_level level, const char* text, void*) { if (verbose || level >= GGML_LOG_LEVEL_ERROR) std::fputs(text, stderr); }, nullptr);
        llama_backend_init();
        llama_model_params mp = llama_model_default_params();
        mp.n_gpu_layers = gpu;
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
        if (!ctx || llama_decode(ctx, llama_batch_get_one(tokens.data(), n)) != 0) {
            std::fprintf(stderr, "cannot read the prompt (%d tokens)\n", n);
            if (ctx) llama_free(ctx);
            return g;
        }
        for (int t = 0; t < tries; t++) {
            if (t) llama_memory_seq_rm(llama_get_memory(ctx), 0, n, -1);   // back to the end of the prompt
            llama_sampler* s = llama_sampler_chain_init(llama_sampler_chain_default_params());
            llama_sampler_chain_add(s, llama_sampler_init_grammar(vocab, kGrammar, "root"));
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
    try {
        const std::string mode = argv[1];
        if (mode == "facts") {
            for (const std::string& l : facts(slurp(argv[2])).lines) std::printf("%s\n", l.c_str());
            return 0;
        }
        if (mode == "prompt") {
            std::fputs(prompt(examples(argc > 3 ? fs::path(argv[3]) : default_examples(argv[0])), facts(slurp(argv[2]))).c_str(), stdout);
            return 0;
        }
        if ((mode == "run" || mode == "eval") && argc > 3) {
            fs::path ex = default_examples(argv[0]);
            int gpu = 0, tries = mode == "eval" ? 1 : 3;
            uint32_t seed = 1;
            for (int i = 4; i < argc; i++) {
                const std::string a = argv[i];
                if (a == "--verbose") verbose = true;
                else if (i + 1 < argc && a == "--examples") ex = argv[++i];
                else if (i + 1 < argc && a == "--gpu") gpu = std::atoi(argv[++i]);
                else if (i + 1 < argc && a == "--seed") seed = uint32_t(std::atoi(argv[++i]));
                else if (i + 1 < argc && a == "--tries") tries = std::max(1, std::atoi(argv[++i]));
            }
            const std::vector<Example> shots = examples(ex);
            Coach coach;
            if (!coach.load(argv[2], gpu)) { std::fprintf(stderr, "cannot load %s\n", argv[2]); return 1; }
            if (mode == "run") {
                const Facts f = facts(slurp(argv[3]));
                const Generated g = coach.generate(prompt(shots, f), f, seed, tries);
                for (const std::string& d : g.best.droppedItems) std::fprintf(stderr, "dropped (a number not in the facts): %s\n", d.c_str());
                std::fputs(g.best.report.c_str(), stdout);
                std::fprintf(stderr, "checked: %d items, %d dropped, %d tries, %s\n", g.best.items, g.best.dropped, g.tries, g.ok ? "ok" : "failed");
                return g.tries == 0 ? 1 : g.ok ? 0 : 3;
            }
            // R4.6: the model on the fixed sessions in a directory (one try each, as written).
            // passed: the check kept every section and dropped at most a third; invented: items
            // dropped for a number the facts do not hold; cited: kept items that quote a value;
            // hedged: on a take with low confidence, the report says so.
            std::vector<fs::path> files;
            for (const auto& e : fs::directory_iterator(argv[3]))
                if (e.path().extension() == ".json") files.push_back(e.path());
            std::sort(files.begin(), files.end());
            int passed = 0, items = 0, invented = 0, cited = 0, kept = 0, low = 0, hedged = 0;
            static const std::regex digit(R"(\d)");
            for (const fs::path& p : files) {
                const Facts f = facts(slurp(p));
                const Generated g = coach.generate(prompt(shots, f), f, seed, tries);
                const Checked& c = g.best;
                passed += g.ok, items += c.items, invented += c.dropped;
                std::istringstream lines(c.report);
                for (std::string l; std::getline(lines, l);)
                    if (l.rfind("- ", 0) == 0) kept++, cited += std::regex_search(l, digit);
                const bool hedge = c.report.find("confiança") != std::string::npos;
                if (f.lowConfidence) low++, hedged += hedge;
                std::printf("== %s: %s, %d items, %d invented%s\n%s", p.filename().string().c_str(), g.ok ? "passed" : "FAILED", c.items, c.dropped,
                            f.lowConfidence ? (hedge ? ", low confidence said" : ", low confidence NOT said") : "", g.raw.c_str());
                for (const std::string& d : c.droppedItems) std::printf("   invented: %s\n", d.c_str());
            }
            std::printf("eval %zu takes: passed %d, items %d, invented %d (%.0f %%), cited %d of %d kept, low confidence said %d of %d\n", files.size(), passed,
                        items, invented, items ? 100.0 * invented / items : 0.0, cited, kept, hedged, low);
            return 0;
        }
    } catch (const std::exception& e) {
        std::fprintf(stderr, "dz_coach: %s\n", e.what());
        return 1;
    }
    std::fprintf(stderr, "dz_coach: unknown mode %s\n", argv[1]);
    return 1;
}
