// STUDIO S8: the VST3 plugin host, a process of its own (spec §20.2) so a plugin that crashes or
// hangs never takes the app down. Step 1, scanning:
//   dz_plugin list                -> one installed .vst3 bundle per line (the standard VST3 folders,
//                                    plus the folders in DISSONANCIA_VST3_PATH, ':'- or ';'-separated)
//   dz_plugin scan <bundle.vst3>  -> one JSON line per audio effect class in it:
//                                    {"cid":…,"name":…,"vendor":…,"version":…,"sub":…,"sdk":…}
// The app runs one scan per bundle (a crashing bundle loses only itself) and caches the result.
#include "public.sdk/source/vst/hosting/module.h"
#include "pluginterfaces/vst/ivstaudioprocessor.h"

#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <string>

namespace {
std::string json(const std::string& s) {
    std::string o = "\"";
    for (unsigned char c : s) {
        if (c == '"' || c == '\\') { o += '\\'; o += char(c); }
        else if (c < 0x20) { char b[8]; std::snprintf(b, sizeof b, "\\u%04x", c); o += b; }
        else o += char(c);
    }
    return o + "\"";
}

int list() {
    auto paths = VST3::Hosting::Module::getModulePaths();
    if (const char* extra = std::getenv("DISSONANCIA_VST3_PATH")) {
        std::string s = extra;
        for (size_t a = 0; a <= s.size();) {
            size_t b = s.find_first_of(":;", a);
            if (b == std::string::npos) b = s.size();
            std::error_code ec;
            const std::filesystem::path dir = s.substr(a, b - a);
            if (!dir.empty() && std::filesystem::is_directory(dir, ec))
                for (auto& e : std::filesystem::recursive_directory_iterator(dir, ec))
                    if (e.path().extension() == ".vst3") paths.push_back(e.path().string());
            a = b + 1;
        }
    }
    for (auto& p : paths) std::printf("%s\n", p.c_str());
    return 0;
}

int scan(const char* path) {
    std::string error;
    auto module = VST3::Hosting::Module::create(path, error);
    if (!module) { std::fprintf(stderr, "%s\n", error.c_str()); return 2; }
    for (auto& ci : module->getFactory().classInfos())
        if (ci.category() == kVstAudioEffectClass)
            std::printf("{\"cid\":%s,\"name\":%s,\"vendor\":%s,\"version\":%s,\"sub\":%s,\"sdk\":%s}\n", json(ci.ID().toString()).c_str(), json(ci.name()).c_str(),
                        json(ci.vendor()).c_str(), json(ci.version()).c_str(), json(ci.subCategoriesString()).c_str(), json(ci.sdkVersion()).c_str());
    return 0;
}
}  // namespace

int main(int argc, char** argv) {
    const std::string cmd = argc > 1 ? argv[1] : "";
    if (cmd == "list") return list();
    if (cmd == "scan" && argc > 2) return scan(argv[2]);
    std::fprintf(stderr, "usage: dz_plugin list | scan <bundle.vst3>\n");
    return 1;
}
