// STUDIO S8: the VST3 plugin host, a process of its own (spec §20.2) so a plugin that crashes or
// hangs never takes the app down.
//   dz_plugin list                -> one installed .vst3 bundle per line (the standard VST3 folders,
//                                    plus the folders in DISSONANCIA_VST3_PATH, ':'- or ';'-separated)
//   dz_plugin scan <bundle.vst3>  -> one JSON line per audio effect class in it:
//                                    {"cid":…,"name":…,"vendor":…,"version":…,"sub":…,"sdk":…}
//   dz_plugin render <bundle.vst3> <cid> <in.wav> <out.wav> [--state-in f] [--state-out f]
//                    [--param id=normalized]... [--block frames]
//                                 -> the file through the effect, offline (kOffline, 32-bit float,
//                                    fixed block); "progress x" lines on stdout; the plugin's state
//                                    is restored from / saved to its own binary blob
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

#include <algorithm>
#include <cstdio>
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
    std::string error;
    auto module = VST3::Hosting::Module::create(bundle, error);
    if (!module) return fail(error);
    auto factory = module->getFactory();
    auto classes = factory.classInfos();
    auto info = std::find_if(classes.begin(), classes.end(), [&](auto& c) { return c.ID().toString() == cid; });
    if (info == classes.end()) return fail("no class " + cid + " in " + bundle);
    static IPtr<HostApplication> host = owned(new HostApplication);
    PluginContextFactory::instance().setPluginContext(host);
    auto provider = owned(new PlugProvider(factory, *info, true));
    if (!provider->initialize()) return fail("the plugin did not initialize");
    auto component = provider->getComponentPtr();
    auto controller = provider->getControllerPtr();
    FUnknownPtr<IAudioProcessor> proc(component);
    if (!proc) return fail("not an audio processor");

    if (!stateIn.empty()) {
        std::ifstream f(stateIn, std::ios::binary);
        std::vector<char> bytes((std::istreambuf_iterator<char>(f)), {});
        auto ms = owned(new MemoryStream(bytes.data(), TSize(bytes.size())));
        if (component->setState(ms) != kResultOk) return fail("the plugin refused its state");
        int64 pos;
        ms->seek(0, IBStream::kIBSeekSet, &pos);
        if (controller) controller->setComponentState(ms);
    }

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
    std::vector<float> out(frames * size_t(outCh), 0.f);
    for (size_t pos = 0; pos < frames; pos += size_t(block)) {
        const int32 n = int32(std::min<size_t>(size_t(block), frames - pos));
        for (int32 c = 0; c < inCh; c++) {
            float* b = data.inputs[0].channelBuffers32[c];
            const uint32_t src = std::min<uint32_t>(uint32_t(c), fileCh - 1);
            for (int32 i = 0; i < n; i++) b[i] = in[(pos + size_t(i)) * fileCh + src];
        }
        data.numSamples = n;
        data.inputParameterChanges = pos == 0 ? &changes : nullptr;
        context.projectTimeSamples = TSamples(pos);
        if (proc->process(data) != kResultOk) return fail("process failed");
        for (int32 c = 0; c < outCh; c++)
            for (int32 i = 0; i < n; i++) out[(pos + size_t(i)) * size_t(outCh) + size_t(c)] = data.outputs[0].channelBuffers32[c][i];
        if ((pos / size_t(block)) % 64 == 0) { std::printf("progress %.3f\n", double(pos) / double(frames)); std::fflush(stdout); }
    }
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
    std::fprintf(stderr, "usage: dz_plugin list | scan <bundle.vst3> | render <bundle.vst3> <cid> <in.wav> <out.wav> [options]\n");
    return 1;
}
