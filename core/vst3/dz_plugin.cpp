// STUDIO S8: the VST3 plugin host, a process of its own (spec §20.2) so a plugin that crashes or
// hangs never takes the app down.
//   dz_plugin list                -> one installed .vst3 bundle per line (the standard VST3 folders,
//                                    plus the folders in DISSONANCIA_VST3_PATH, ':'- or ';'-separated)
//   dz_plugin scan <bundle.vst3>  -> one JSON line per audio effect class in it:
//                                    {"cid":…,"name":…,"vendor":…,"version":…,"sub":…,"sdk":…}
//   dz_plugin render <bundle.vst3> <cid> <in.wav> <out.wav> [--state-in f] [--state-out f]
//                    [--param id=normalized]... [--block frames]
//                                 -> the file through the effect, offline (kOffline, 32-bit float,
//                                    fixed block), the plugin's latency compensated (its first
//                                    samples dropped, the input padded); "progress x" lines on stdout; the plugin's state
//                                    is restored from / saved to its own binary blob
//   dz_plugin params <bundle.vst3> <cid> [--state-in f]
//                                 -> one JSON line per parameter, as the controller has it after
//                                    the state: {"id","title","units","default","value","steps","flags","text"}
//   dz_plugin edit <bundle.vst3> <cid> <state> [--param id=normalized]...
//                                 -> the plugin's own editor in a native window, until it is
//                                    closed; then the edits go to the processor, its state is saved
//                                    to <state>, and every edited parameter is printed as
//                                    "param <id> <value>" (exit 4: the plugin has no editor)
// The app runs one scan per bundle (a crashing bundle loses only itself) and caches the result.
#include "miniaudio.h"
#include "pluginterfaces/vst/ivstaudioprocessor.h"
#include "pluginterfaces/vst/ivsteditcontroller.h"
#include "pluginterfaces/vst/ivstprocesscontext.h"
#include "public.sdk/source/common/memorystream.h"
#include "public.sdk/source/vst/hosting/hostclasses.h"
#include "public.sdk/source/vst/hosting/module.h"
#include "public.sdk/source/vst/hosting/parameterchanges.h"
#include "public.sdk/source/vst/hosting/plugprovider.h"
#include "public.sdk/source/vst/hosting/processdata.h"
#include "editor_window.hpp"

#include <map>

#include <algorithm>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <iterator>
#include <vector>
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
using namespace Steinberg;
using namespace Steinberg::Vst;

int fail(const std::string& what) { std::fprintf(stderr, "%s\n", what.c_str()); return 3; }

std::string utf8(const String128 s) {
    std::string o;
    for (int i = 0; i < 128 && s[i]; i++) {
        const char16_t c = char16_t(s[i]);
        if (c < 0x80) o += char(c);
        else if (c < 0x800) { o += char(0xC0 | (c >> 6)); o += char(0x80 | (c & 0x3F)); }
        else { o += char(0xE0 | (c >> 12)); o += char(0x80 | ((c >> 6) & 0x3F)); o += char(0x80 | (c & 0x3F)); }
    }
    return o;
}

// The plugin loaded and initialized, its state restored (shared by render and params).
struct Loaded {
    std::shared_ptr<VST3::Hosting::Module> module;
    IPtr<PlugProvider> provider;
    IPtr<IComponent> component;
    IPtr<IEditController> controller;
};

int load(const std::string& bundle, const std::string& cid, const std::string& stateIn, Loaded& out) {
    std::string error;
    out.module = VST3::Hosting::Module::create(bundle, error);
    if (!out.module) return fail(error);
    auto factory = out.module->getFactory();
    auto classes = factory.classInfos();
    auto info = std::find_if(classes.begin(), classes.end(), [&](auto& c) { return c.ID().toString() == cid; });
    if (info == classes.end()) return fail("no class " + cid + " in " + bundle);
    static IPtr<HostApplication> host = owned(new HostApplication);
    PluginContextFactory::instance().setPluginContext(host);
    out.provider = owned(new PlugProvider(factory, *info, true));
    if (!out.provider->initialize()) return fail("the plugin did not initialize");
    out.component = out.provider->getComponentPtr();
    out.controller = out.provider->getControllerPtr();
    if (!stateIn.empty()) {
        std::ifstream f(stateIn, std::ios::binary);
        std::vector<char> bytes((std::istreambuf_iterator<char>(f)), {});
        if (!bytes.empty()) {
            auto ms = owned(new MemoryStream(bytes.data(), TSize(bytes.size())));
            if (out.component->setState(ms) != kResultOk) return fail("the plugin refused its state");
            int64 pos;
            ms->seek(0, IBStream::kIBSeekSet, &pos);
            if (out.controller) out.controller->setComponentState(ms);
        }
    }
    return 0;
}

int params(int argc, char** argv) {
    std::string stateIn = argc > 5 && std::string(argv[4]) == "--state-in" ? argv[5] : "";
    Loaded p;
    if (int r = load(argv[2], argv[3], stateIn, p)) return r;
    if (!p.controller) return fail("the plugin has no controller");
    for (int32 i = 0; i < p.controller->getParameterCount(); i++) {
        ParameterInfo pi{};
        if (p.controller->getParameterInfo(i, pi) != kResultOk) continue;
        const ParamValue v = p.controller->getParamNormalized(pi.id);
        String128 text{};
        p.controller->getParamStringByValue(pi.id, v, text);
        std::printf("{\"id\":%u,\"title\":%s,\"units\":%s,\"default\":%.9g,\"value\":%.9g,\"steps\":%d,\"flags\":%d,\"text\":%s}\n", pi.id,
                    json(utf8(pi.title)).c_str(), json(utf8(pi.units)).c_str(), pi.defaultNormalizedValue, v, pi.stepCount, pi.flags, json(utf8(text)).c_str());
    }
    return 0;
}

// The editor's changes as a host receives them (IComponentHandler::performEdit).
struct EditSink final : IComponentHandler {
    std::map<ParamID, ParamValue> edits;
    tresult PLUGIN_API beginEdit(ParamID) override { return kResultOk; }
    tresult PLUGIN_API performEdit(ParamID id, ParamValue v) override { edits[id] = v; return kResultOk; }
    tresult PLUGIN_API endEdit(ParamID) override { return kResultOk; }
    tresult PLUGIN_API restartComponent(int32) override { return kResultOk; }
    tresult PLUGIN_API queryInterface(const TUID iid, void** obj) override {
        if (FUnknownPrivate::iidEqual(iid, IComponentHandler::iid) || FUnknownPrivate::iidEqual(iid, FUnknown::iid)) { *obj = this; return kResultOk; }
        *obj = nullptr;
        return kNoInterface;
    }
    uint32 PLUGIN_API addRef() override { return 1000; }
    uint32 PLUGIN_API release() override { return 1000; }
};

int edit(int argc, char** argv) {
    const std::string bundle = argv[2], cid = argv[3], state = argv[4];
    std::map<ParamID, ParamValue> set;   // the panel's values, under the editor's
    for (int i = 5; i + 1 < argc; i += 2)
        if (std::string(argv[i]) == "--param" && std::strchr(argv[i + 1], '='))
            set[ParamID(std::strtoul(argv[i + 1], nullptr, 10))] = std::atof(std::strchr(argv[i + 1], '=') + 1);
    Loaded p;
    if (int r = load(bundle, cid, state, p)) return r;
    if (!p.controller) return fail("the plugin has no controller");
    for (auto& [id, v] : set) p.controller->setParamNormalized(id, v);
    EditSink sink;
    p.controller->setComponentHandler(&sink);
    IPtr<IPlugView> view = owned(p.controller->createView(ViewType::kEditor));
    if (!view) { p.controller->setComponentHandler(nullptr); std::fprintf(stderr, "the plugin has no editor\n"); return 4; }
    std::string error, title = "Dissonância · " + p.provider->getClassInfo().name();
    const bool shown = run_editor_window(view, title, error);
    view = nullptr;
    p.controller->setComponentHandler(nullptr);
    if (!shown) return fail(error);

    // The processor learns the edits (one empty block of parameter changes), then saves its state.
    FUnknownPtr<IAudioProcessor> proc(p.component);
    for (auto& [id, v] : sink.edits) set[id] = v;
    if (proc && !set.empty()) {
        ProcessSetup setup{kOffline, kSample32, 512, 48000};
        proc->setupProcessing(setup);
        p.component->setActive(true);
        proc->setProcessing(true);
        HostProcessData data;
        data.prepare(*p.component, 512, kSample32);
        ParameterChanges changes(int32(set.size()) + 1);
        for (auto& [id, v] : set) {
            int32 qi, pi;
            if (auto* q = changes.addParameterData(id, qi)) q->addPoint(0, v, pi);
        }
        data.numSamples = 0;
        data.inputParameterChanges = &changes;
        proc->process(data);
        proc->setProcessing(false);
        p.component->setActive(false);
    }
    auto ms = owned(new MemoryStream);
    if (p.component->getState(ms) == kResultOk) {
        std::ofstream f(state, std::ios::binary);
        f.write(ms->getData(), std::streamsize(ms->getSize()));
    }
    for (auto& [id, v] : sink.edits) std::printf("param %u %.9g\n", id, v);
    return 0;
}

int render(int argc, char** argv) {
    const std::string bundle = argv[2], cid = argv[3], inPath = argv[4], outPath = argv[5];
    std::string stateIn, stateOut;
    std::vector<std::pair<ParamID, ParamValue>> params;
    int32 block = 1024;
    for (int i = 6; i + 1 < argc; i += 2) {
        const std::string k = argv[i], v = argv[i + 1];
        if (k == "--state-in") stateIn = v;
        else if (k == "--state-out") stateOut = v;
        else if (k == "--block") block = std::clamp(std::atoi(v.c_str()), 32, 8192);
        else if (k == "--param" && v.find('=') != std::string::npos)
            params.push_back({ParamID(std::strtoul(v.c_str(), nullptr, 10)), std::atof(v.c_str() + v.find('=') + 1)});
        else return fail("unknown option " + k);
    }

    // Input: the file at its own rate and channels.
    ma_decoder dec;
    ma_decoder_config dc = ma_decoder_config_init(ma_format_f32, 0, 0);
    if (ma_decoder_init_file(inPath.c_str(), &dc, &dec) != MA_SUCCESS) return fail("cannot read " + inPath);
    const uint32_t fileCh = dec.outputChannels, rate = dec.outputSampleRate;
    std::vector<float> in;
    {
        std::vector<float> buf(4096 * fileCh);
        ma_uint64 got;
        while (ma_decoder_read_pcm_frames(&dec, buf.data(), 4096, &got) == MA_SUCCESS && got > 0) in.insert(in.end(), buf.begin(), buf.begin() + std::ptrdiff_t(got * fileCh));
        ma_decoder_uninit(&dec);
    }
    const size_t frames = in.size() / std::max<uint32_t>(1, fileCh);

    // The plugin: component + controller (connected), as the SDK's hosts do.
    Loaded loaded;
    if (int r = load(bundle, cid, stateIn, loaded)) return r;
    auto component = loaded.component;
    auto controller = loaded.controller;
    FUnknownPtr<IAudioProcessor> proc(component);
    if (!proc) return fail("not an audio processor");

    // Main buses: stereo if the plugin takes it, else its own channel counts.
    SpeakerArrangement st = SpeakerArr::kStereo;
    proc->setBusArrangements(&st, 1, &st, 1);
    BusInfo bi{};
    int32 inCh = component->getBusInfo(kAudio, kInput, 0, bi) == kResultOk ? bi.channelCount : 0;
    int32 outCh = component->getBusInfo(kAudio, kOutput, 0, bi) == kResultOk ? bi.channelCount : 0;
    if (inCh <= 0 || outCh <= 0) return fail("not an effect with audio in and out");
    component->activateBus(kAudio, kInput, 0, true);
    component->activateBus(kAudio, kOutput, 0, true);
    ProcessSetup setup{kOffline, kSample32, block, double(rate)};
    if (proc->setupProcessing(setup) != kResultOk) return fail("the plugin refused offline 32-bit processing");
    component->setActive(true);
    proc->setProcessing(true);

    HostProcessData data;
    data.prepare(*component, block, kSample32);
    ParameterChanges changes(int32(params.size()) + 1);
    for (auto& [id, v] : params) {
        int32 qi, pi;
        if (auto* q = changes.addParameterData(id, qi)) q->addPoint(0, v, pi);
        if (controller) controller->setParamNormalized(id, v);
    }
    ProcessContext context{};
    context.sampleRate = rate;
    data.processContext = &context;
    // Latency: run `latency` frames past the end (silence in) and drop as many from the start, so
    // the render lines up with the take sample for sample. Asked after the parameters and state
    // are in (a latency switch changes it): one empty block carries the parameter changes first.
    data.numSamples = 0;
    data.inputParameterChanges = &changes;
    proc->process(data);
    data.inputParameterChanges = nullptr;
    const size_t latency = proc->getLatencySamples(), total = frames + latency;
    std::vector<float> out(frames * size_t(outCh), 0.f);
    for (size_t pos = 0; pos < total; pos += size_t(block)) {
        const int32 n = int32(std::min<size_t>(size_t(block), total - pos));
        for (int32 c = 0; c < inCh; c++) {
            float* b = data.inputs[0].channelBuffers32[c];
            const uint32_t src = std::min<uint32_t>(uint32_t(c), fileCh - 1);
            for (int32 i = 0; i < n; i++) {
                const size_t at = pos + size_t(i);
                if (at >= frames) { b[i] = 0.f; continue; }
                if (inCh == 1 && fileCh > 1) {   // a mono plugin on a stereo channel hears both sides
                    float sum = 0;
                    for (uint32_t k = 0; k < fileCh; k++) sum += in[at * fileCh + k];
                    b[i] = sum / float(fileCh);
                } else b[i] = in[at * fileCh + src];
            }
        }
        data.numSamples = n;
        context.projectTimeSamples = TSamples(pos);
        if (proc->process(data) != kResultOk) return fail("process failed");
        for (int32 c = 0; c < outCh; c++)
            for (int32 i = 0; i < n; i++)
                if (size_t at = pos + size_t(i); at >= latency) out[(at - latency) * size_t(outCh) + size_t(c)] = data.outputs[0].channelBuffers32[c][i];
        if ((pos / size_t(block)) % 64 == 0) { std::printf("progress %.3f\n", double(pos) / double(total)); std::fflush(stdout); }
    }
    std::fprintf(stderr, "latency %zu\n", latency);
    proc->setProcessing(false);
    component->setActive(false);

    if (!stateOut.empty()) {
        auto ms = owned(new MemoryStream);
        if (component->getState(ms) == kResultOk) {
            std::ofstream f(stateOut, std::ios::binary);
            f.write(ms->getData(), std::streamsize(ms->getSize()));
        }
    }
    ma_encoder enc;
    ma_encoder_config ec = ma_encoder_config_init(ma_encoding_format_wav, ma_format_f32, uint32_t(outCh), rate);
    if (ma_encoder_init_file(outPath.c_str(), &ec, &enc) != MA_SUCCESS) return fail("cannot write " + outPath);
    ma_encoder_write_pcm_frames(&enc, out.data(), frames, nullptr);
    ma_encoder_uninit(&enc);
    std::printf("progress 1\n");
    return 0;
}
}  // namespace

int main(int argc, char** argv) {
    const std::string cmd = argc > 1 ? argv[1] : "";
    if (cmd == "list") return list();
    if (cmd == "scan" && argc > 2) return scan(argv[2]);
    if (cmd == "render" && argc > 5) return render(argc, argv);
    if (cmd == "params" && argc > 3) return params(argc, argv);
    if (cmd == "edit" && argc > 4) return edit(argc, argv);
    std::fprintf(stderr, "usage: dz_plugin list | scan <bundle.vst3> | render <bundle.vst3> <cid> <in.wav> <out.wav> [options]\n");
    return 1;
}
