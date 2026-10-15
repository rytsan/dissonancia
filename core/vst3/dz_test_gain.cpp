// STUDIO S8 test fixture: "DZ Test Gain", a single-component VST3 effect (stereo in / out) with
// two parameters — gain, normalized 0..1 = -24..+24 dB (0.5 = unity), and a latency switch that
// delays the output by 64 samples and reports it, as a look-ahead plugin does — and its state. Built only
// for the host's tests: the scanner, the offline render and the state round trip measure against it.
#include "base/source/fstreamer.h"
#include "pluginterfaces/base/ibstream.h"
#include "pluginterfaces/vst/ivstparameterchanges.h"
#include "public.sdk/source/main/pluginfactory.h"
#include "public.sdk/source/vst/vstsinglecomponenteffect.h"

#include <algorithm>
#include <cmath>
#include <cstring>

using namespace Steinberg;
using namespace Steinberg::Vst;

namespace {
const FUID kTestGainUID(0x6F1A3C21, 0x4D2B47E8, 0x9A1C5B3E, 0x7D0E2F41);
constexpr ParamID kGain = 0, kLatency = 1;
constexpr int32 kDelay = 64;
double to_lin(double norm) { return std::pow(10.0, (norm * 48 - 24) / 20); }

class TestGain : public SingleComponentEffect {
public:
    static FUnknown* createInstance(void*) { return static_cast<IAudioProcessor*>(new TestGain); }

    tresult PLUGIN_API initialize(FUnknown* context) SMTG_OVERRIDE {
        tresult r = SingleComponentEffect::initialize(context);
        if (r != kResultOk) return r;
        addAudioInput(STR16("Stereo In"), SpeakerArr::kStereo);
        addAudioOutput(STR16("Stereo Out"), SpeakerArr::kStereo);
        parameters.addParameter(STR16("Gain"), STR16("dB"), 0, 0.5, ParameterInfo::kCanAutomate, kGain);
        parameters.addParameter(STR16("Latency"), nullptr, 1, 0, 0, kLatency);
        return kResultOk;
    }

    tresult PLUGIN_API process(ProcessData& data) SMTG_OVERRIDE {
        if (auto* changes = data.inputParameterChanges)
            for (int32 i = 0; i < changes->getParameterCount(); i++)
                if (auto* q = changes->getParameterData(i)) {
                    int32 offset;
                    ParamValue v;
                    if (q->getPoint(q->getPointCount() - 1, offset, v) != kResultTrue) continue;
                    if (q->getParameterId() == kGain) gain_ = v;
                    if (q->getParameterId() == kLatency) latency_ = v > 0.5;
                    setParamNormalized(q->getParameterId(), v);
                }
        if (data.numInputs == 0 || data.numOutputs == 0) return kResultOk;
        const float g = float(to_lin(gain_));
        for (int32 c = 0; c < data.outputs[0].numChannels && c < 2; c++) {
            const float* in = data.inputs[0].channelBuffers32[c];
            float* out = data.outputs[0].channelBuffers32[c];
            for (int32 i = 0; i < data.numSamples; i++) {
                const float x = in[i] * g;
                if (!latency_) { out[i] = x; continue; }
                out[i] = line_[c][pos_[c]];   // the sample from kDelay ago
                line_[c][pos_[c]] = x;
                pos_[c] = (pos_[c] + 1) % kDelay;
            }
        }
        data.outputs[0].silenceFlags = 0;
        return kResultOk;
    }

    tresult PLUGIN_API setState(IBStream* state) SMTG_OVERRIDE {
        IBStreamer s(state, kLittleEndian);
        double v;
        if (!s.readDouble(v)) return kResultFalse;
        gain_ = v;
        setParamNormalized(kGain, v);
        int8 l = 0;
        if (s.readInt8(l)) { latency_ = l != 0; setParamNormalized(kLatency, latency_ ? 1 : 0); }   // absent in a 1.0 state
        return kResultOk;
    }
    tresult PLUGIN_API getState(IBStream* state) SMTG_OVERRIDE {
        IBStreamer s(state, kLittleEndian);
        return s.writeDouble(gain_) && s.writeInt8(latency_ ? 1 : 0) ? kResultOk : kResultFalse;
    }
    uint32 PLUGIN_API getLatencySamples() SMTG_OVERRIDE { return latency_ ? kDelay : 0; }
    tresult PLUGIN_API setActive(TBool on) SMTG_OVERRIDE {
        for (auto& l : line_) std::fill(std::begin(l), std::end(l), 0.f);
        pos_[0] = pos_[1] = 0;
        return SingleComponentEffect::setActive(on);
    }
    tresult PLUGIN_API setupProcessing(ProcessSetup& setup) SMTG_OVERRIDE { return SingleComponentEffect::setupProcessing(setup); }
    tresult PLUGIN_API canProcessSampleSize(int32 size) SMTG_OVERRIDE { return size == kSample32 ? kResultTrue : kResultFalse; }

private:
    double gain_ = 0.5;
    bool latency_ = false;
    float line_[2][kDelay]{};
    int32 pos_[2]{};
};
}  // namespace

BEGIN_FACTORY_DEF("Dissonancia", "https://github.com/rytsan/dissonancia", "")
DEF_CLASS2(INLINE_UID_FROM_FUID(kTestGainUID), PClassInfo::kManyInstances, kVstAudioEffectClass, "DZ Test Gain", 0, "Fx|Tools", "1.0.0",
           kVstVersionString, TestGain::createInstance)
END_FACTORY
