#include <catch2/catch_approx.hpp>
#include <catch2/catch_test_macros.hpp>
#include <cmath>
#include <cstdio>
#include <string>
#include <vector>

#include "engine.hpp"
#include "miniaudio.h"

using namespace dz;
using Catch::Approx;

namespace {
constexpr double kPi = 3.14159265358979323846;

std::string wav(const char* path, const std::vector<float>& mono) {
    ma_encoder enc;
    ma_encoder_config c = ma_encoder_config_init(ma_encoding_format_wav, ma_format_f32, 1, 48000);
    REQUIRE(ma_encoder_init_file(path, &c, &enc) == MA_SUCCESS);
    ma_encoder_write_pcm_frames(&enc, mono.data(), mono.size(), nullptr);
    ma_encoder_uninit(&enc);
    return path;
}
std::vector<float> tone(double hz, double sec, float amp = 0.5f) {
    std::vector<float> x(size_t(sec * 48000));
    for (size_t i = 0; i < x.size(); i++) x[i] = amp * float(std::sin(2 * kPi * hz * double(i) / 48000));
    return x;
}
SessionConfig session() {
    SessionConfig s{};
    s.mode = AnalysisMode::VoiceMono;
    s.quality = AudioQuality::Balanced;
    s.referenceA4 = 440;
    s.meter = {4, 4};
    s.bpm = 120;
    s.metronome = 0;   // the click off: only the playground in the output (its grid still runs)
    return s;
}
AudioDeviceConfig headless() {
    AudioDeviceConfig d{};
    d.captureDevice = -1;
    d.sampleRate = 48000;
    d.clickOutput = 1;
    return d;
}
// `frames` of output (mono, headless) in 480-frame callbacks.
std::vector<float> run(Engine& e, size_t frames) {
    std::vector<float> in(480, 0.f), out(480), y;
    for (size_t done = 0; done < frames; done += 480) {
        std::fill(out.begin(), out.end(), 0.f);
        e.on_audio(in.data(), out.data(), 480);
        y.insert(y.end(), out.begin(), out.end());
    }
    return y;
}
size_t first_sound(const std::vector<float>& y, size_t from = 0) {
    for (size_t i = from; i < y.size(); i++) if (std::fabs(y[i]) > 1e-4f) return i;
    return y.size();
}
}  // namespace

TEST_CASE("playground: a pad hit, the sequencer on the metronome's sixteenths, the sampler on MIDI notes") {
    Engine e;
    REQUIRE(e.start(session(), headless(), nullptr, nullptr) == ANA_OK);
    auto& pg = e.playground();
    std::string err;
    REQUIRE(pg.load(0, wav("dz_pad0.wav", tone(1000, 0.02)).c_str(), 48000, err) == ANA_OK);
    CHECK(pg.load(3, "missing.wav", 48000, err) == ANA_ERR_IO);
    PlaygroundConfig c{};
    c.samplerRoot = 69;
    pg.set(c);

    // A hit plays from the next callback, at unity (centre pan = unity, mono out = L/2 + R/2).
    pg.hit(0, 1.f);
    auto y = run(e, 4800);
    float pk = 0;
    for (float v : y) pk = std::max(pk, std::fabs(v));
    CHECK(pk == Approx(0.5).margin(0.01));
    CHECK(first_sound(y) < 10);

    // Sequencer: pad 0 on steps 0 and 4 of the bar. 120 bpm: a sixteenth is 6000 frames, the bar
    // 96000; the engine's clock is at 4800 now, so the next hits land on frames 24000 and 96000.
    c.steps[0] = 0b10001;
    c.sequencerOn = 1;
    pg.set(c);
    y = run(e, 96000 + 4800 - 4800);   // frames 4800 .. 96000
    CHECK(first_sound(y) == 24000 - 4800 + 1);   // + 1: the tone starts at sin 0
    CHECK(first_sound(y, 24000 - 4800 + 2000) == 96000 - 4800 + 1);
    PlaygroundStatus st{};
    pg.status(st);
    CHECK(st.hits[0] >= 3);
    CHECK(st.step == 0);
    c.sequencerOn = 0;
    pg.set(c);
    run(e, 4800);

    // Sampler: a 440 Hz tone recorded as A4 (69); note 81 plays it an octave up; note off fades it.
    REQUIRE(pg.load(Playground::kSampler, wav("dz_smp.wav", tone(440, 1.0)).c_str(), 48000, err) == ANA_OK);
    pg.midi_from_api(0x90, 81, 127);
    y = run(e, 9600);
    int zc = 0;
    for (size_t i = 1000; i < y.size(); i++) zc += (y[i] >= 0) != (y[i - 1] >= 0);
    CHECK(zc == Approx(2 * 880 * (9600 - 1000) / 48000.0).margin(4));
    pg.midi_from_api(0x80, 81, 0);
    y = run(e, 4800);
    CHECK(first_sound(y, 480 + 1440 + 480) == y.size());   // silent after the 30 ms release
    // Pads from MIDI: note 36 + k.
    pg.midi_from_api(0x99, 36, 100);
    y = run(e, 960);
    CHECK(first_sound(y) < 10);

    // A pad replaced while it plays: the voice stops, nothing reads the old sample.
    pg.hit(0, 1.f);
    run(e, 480);
    REQUIRE(pg.load(0, "", 48000, err) == ANA_OK);
    y = run(e, 4800);
    CHECK(first_sound(y) == y.size());
    e.stop();
    std::remove("dz_pad0.wav");
    std::remove("dz_smp.wav");
}
