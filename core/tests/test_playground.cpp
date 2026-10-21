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

namespace {
std::vector<size_t> onsets(const std::vector<float>& y, size_t gap = 2000) {
    std::vector<size_t> o;
    size_t last = 0;
    bool any = false;
    for (size_t i = 0; i < y.size(); i++)
        if (std::fabs(y[i]) > 1e-4f) {
            if (!any || i - last > gap) o.push_back(i);
            last = i;
            any = true;
        }
    return o;
}
// A song: blocks with pad 0 on the given sixteenths (stride 16 per bar).
struct SongDef {
    std::vector<PgBlock> blocks;
    std::vector<uint8_t> steps;
    std::vector<PgNote> notes;
    std::vector<PgEntry> order;
    void block(uint32_t id, uint32_t bars, std::vector<int> pad0) {
        blocks.push_back({id, bars, uint32_t(steps.size()), uint32_t(notes.size()), 0});
        size_t at = steps.size();
        steps.resize(steps.size() + 16 * bars * 16, 0);
        for (int k : pad0) steps[at + size_t(k)] = 127;
    }
    int send(Playground& pg) {
        std::string err;
        return pg.song(blocks.data(), int(blocks.size()), steps.data(), int(steps.size()), notes.data(), int(notes.size()), order.data(), int(order.size()), err);
    }
};
// Frames of output with the input (mono) from `input(frame)`.
template <class F>
std::vector<float> run_in(Engine& e, size_t frames, uint64_t& pos, F input) {
    std::vector<float> in(480), out(480), y;
    for (size_t done = 0; done < frames; done += 480, pos += 480) {
        for (size_t i = 0; i < 480; i++) in[i] = input(pos + i);
        std::fill(out.begin(), out.end(), 0.f);
        e.on_audio(in.data(), out.data(), 480);
        y.insert(y.end(), out.begin(), out.end());
    }
    return y;
}
}  // namespace

TEST_CASE("playground: pads, the sampler on MIDI notes, a replaced sample") {
    Engine e;
    REQUIRE(e.start(session(), headless(), nullptr, nullptr) == ANA_OK);
    auto& pg = e.playground();
    std::string err;
    REQUIRE(pg.load(0, wav("dz_pad0.wav", tone(1000, 0.02)).c_str(), 48000, err) == ANA_OK);
    CHECK(pg.load(3, "missing.wav", 48000, err) == ANA_ERR_IO);
    PlaygroundConfig c{};
    c.samplerRoot = 69;
    pg.set(c);
    pg.hit(0, 1.f);   // plays from the next callback, at unity (centre pan = unity, mono out = L/2 + R/2)
    auto y = run(e, 4800);
    float pk = 0;
    for (float v : y) pk = std::max(pk, std::fabs(v));
    CHECK(pk == Approx(0.5).margin(0.01));
    CHECK(first_sound(y) < 10);
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
    pg.midi_from_api(0x99, 36, 100);                        // pads from MIDI: note 36 + k
    CHECK(first_sound(run(e, 960)) < 10);
    pg.hit(0, 1.f);                                         // replaced while it plays: the voice stops
    run(e, 480);
    REQUIRE(pg.load(0, "", 48000, err) == ANA_OK);
    y = run(e, 4800);
    CHECK(first_sound(y) == y.size());
    e.stop();
    std::remove("dz_smp.wav");
}

TEST_CASE("playground song: blocks on a timeline from the next bar, a launch on the next bar, overdub, bounce") {
    Engine e;
    REQUIRE(e.start(session(), headless(), nullptr, nullptr) == ANA_OK);
    auto& pg = e.playground();
    std::string err;
    REQUIRE(pg.load(0, wav("dz_pad0.wav", tone(1000, 0.02)).c_str(), 48000, err) == ANA_OK);
    // 120 bpm 4/4: a sixteenth 6000 frames, a bar 96000. A (1 bar): sixteenths 0 and 4. B (2 bars):
    // the first sixteenth of its second bar. Timeline: A x 2, B.
    SongDef sd;
    sd.block(1, 1, {0, 4});
    sd.block(2, 2, {16});
    sd.order = {{0, 2}, {1, 1}};
    REQUIRE(sd.send(pg) == ANA_OK);
    uint64_t pos = 0;
    run_in(e, 4800, pos, [](uint64_t) { return 0.f; });
    pg.play(0, false);   // from the next bar: 96000
    auto y = run_in(e, 480000 + 96000 - 4800, pos, [](uint64_t) { return 0.f; });
    auto o = onsets(y);
    std::vector<size_t> want = {96000, 120000, 192000, 216000, 384000, 480000, 504000};   // A, A, B (its 2nd bar), A again
    REQUIRE(o.size() == want.size());
    for (size_t i = 0; i < o.size(); i++) CHECK(o[i] + 4800 == want[i] + 1);   // + 1: the tone starts at sin 0
    PlaygroundStatus st{};
    pg.status(st);
    CHECK(st.playing);
    CHECK(st.entry == 0);

    // Launched mid-bar: B looping from the next bar, its first bar silent, its second one sounds.
    run_in(e, 30000, pos, [](uint64_t) { return 0.f; });   // into the bar from 576000
    const uint64_t launched = pos, next = (pos / 96000 + 1) * 96000;
    pg.play(1, true);
    y = run_in(e, next - pos + 96000 * 4, pos, [](uint64_t) { return 0.f; });
    o = onsets(y);
    REQUIRE(o.size() >= 2);
    CHECK(o[0] + launched == next + 96000 + 1);       // B from the next bar: its second bar
    CHECK(o[1] + launched == next + 3 * 96000 + 1);   // and again: B loops
    pg.status(st);
    CHECK(st.looping);
    CHECK(st.entry == 1);
    pg.stop();
    run_in(e, 480, pos, [](uint64_t) { return 0.f; });
    pg.status(st);
    CHECK_FALSE(st.playing);

    // Overdub: A (now empty) looping; REC records the input into its loop and a pad hit at the
    // nearest sixteenth; the next pass plays the loop back.
    sd = {};
    sd.block(1, 1, {});
    sd.order = {{0, 1}};
    REQUIRE(sd.send(pg) == ANA_OK);
    REQUIRE(pg.record(true, e.beat_frames_now() * 4) == ANA_OK);
    pg.play(0, true);
    const uint64_t bar0 = (pos / 96000 + 1) * 96000;
    run_in(e, bar0 - pos, pos, [](uint64_t) { return 0.f; });
    auto sine = [](uint64_t f) { return 0.2f * float(std::sin(2 * kPi * 300 * double(f) / 48000)); };
    run_in(e, 6000 * 3 - 960, pos, sine);   // just before sixteenth 3
    pg.hit(0, 1.f);                         // REC: lands on sixteenth 3
    run_in(e, 96000 - (6000 * 3 - 960), pos, sine);
    PgRecorded r[8];
    REQUIRE(pg.recorded(r, 8) == 1);
    CHECK(r[0].blockId == 1);
    CHECK(r[0].kind == 0);
    CHECK(r[0].key == 0);
    CHECK(r[0].step == 3);
    REQUIRE(pg.record(false, 0) == ANA_OK);
    REQUIRE(pg.load(0, "", 48000, err) == ANA_OK);   // only the loop in the output now
    y = run_in(e, 96000, pos, [](uint64_t) { return 0.f; });
    double worst = 0;
    for (size_t i = 0; i < y.size(); i++) worst = std::max(worst, double(std::fabs(y[i] - sine(bar0 + i))));
    CHECK(worst < 1e-4);
    REQUIRE(pg.loop_file(1, "dz_loop.wav", true, 48000, err) == ANA_OK);

    // Bounce: the timeline once, offline: A x 2, B, plus tails.
    REQUIRE(pg.load(0, wav("dz_pad0.wav", tone(1000, 0.02)).c_str(), 48000, err) == ANA_OK);
    REQUIRE(pg.loop_file(1, "", false, 48000, err) == ANA_OK);   // the loop cleared
    sd = {};
    sd.block(1, 1, {0, 4});
    sd.block(2, 2, {16});
    sd.order = {{0, 2}, {1, 1}};
    REQUIRE(sd.send(pg) == ANA_OK);
    REQUIRE(pg.bounce("dz_bounce.wav", 48000, e.beat_frames_now(), 4, err) == ANA_OK);
    ma_decoder dec;
    ma_decoder_config dc = ma_decoder_config_init(ma_format_f32, 1, 48000);
    REQUIRE(ma_decoder_init_file("dz_bounce.wav", &dc, &dec) == MA_SUCCESS);
    ma_uint64 len = 0;
    ma_decoder_get_length_in_pcm_frames(&dec, &len);
    std::vector<float> b(static_cast<size_t>(len));
    ma_decoder_read_pcm_frames(&dec, b.data(), len, nullptr);
    ma_decoder_uninit(&dec);
    CHECK(len == 4 * 96000 + 96000);
    o = onsets(b);
    want = {0, 24000, 96000, 120000, 288000};
    REQUIRE(o.size() == want.size());
    for (size_t i = 0; i < o.size(); i++) CHECK(o[i] == want[i] + 1);
    e.stop();
    for (auto f : {"dz_pad0.wav", "dz_loop.wav", "dz_bounce.wav"}) std::remove(f);
}

TEST_CASE("pedalboard: the input monitored through pedals, a tempo delay, the pads through a cable, what REC records, a loop silent") {
    Engine e;
    REQUIRE(e.start(session(), headless(), nullptr, nullptr) == ANA_OK);
    auto& pg = e.playground();
    std::string err;
    uint64_t pos = 0;
    auto pedal = [](uint16_t id, PedalKind k, std::initializer_list<float> knobs, bool on = true) {
        PedalParams p{};
        p.id = id; p.kind = k; p.on = on;
        int i = 0;
        for (float v : knobs) p.knob[i++] = v;
        return p;
    };
    // Nothing patched: the input does not reach the output.
    auto x = [](uint64_t f) { return 0.3f * float(std::sin(2 * kPi * 200 * double(f) / 48000)); };
    auto y = run_in(e, 4800, pos, x);
    CHECK(first_sound(y) == y.size());
    // ENTRADA -> drive (bypassed) -> SAÍDA: the input as it is.
    Pedalboard b{};
    b.pedal[0] = pedal(1, PedalKind::Drive, {0.5f, 0.5f, 0.8f, 1}, false);
    b.pedals = 1;
    b.cable[0] = {0, 8, {}, 1};
    b.cable[1] = {9, 2, {}, 1};
    b.cables = 2;
    REQUIRE(pg.pedalboard(b, err) == ANA_OK);
    const uint64_t p0 = pos;
    y = run_in(e, 4800, pos, x);
    double worst = 0;
    for (size_t i = 0; i < y.size(); i++) worst = std::max(worst, double(std::fabs(y[i] - x(p0 + i))));
    CHECK(worst < 1e-5);
    // A delay of 4 sixteenths (120 bpm: 24000 frames), no feedback, wet only above the dry: a click comes back once.
    b.pedal[0] = pedal(2, PedalKind::Delay, {3 / 15.f, 0, 1, 1});
    REQUIRE(pg.pedalboard(b, err) == ANA_OK);
    const uint64_t c0 = pos + 100;
    y = run_in(e, 48000, pos, [&](uint64_t f) { return f == c0 ? 1.f : 0.f; });
    auto o = onsets(y, 100);
    REQUIRE(o.size() == 2);
    CHECK(o[0] + (c0 - 100) == c0);
    CHECK(o[1] + (c0 - 100) == Approx(double(c0 + 24000)).margin(1));
    // The pads through a cable at half: PADS -> SAÍDA x 0.5 (the pads no longer go straight out).
    REQUIRE(pg.load(0, wav("dz_pad0.wav", tone(1000, 0.02)).c_str(), 48000, err) == ANA_OK);
    b.pedals = 0;
    b.cable[0] = {1, 2, {}, 0.5f};
    b.cables = 1;
    REQUIRE(pg.pedalboard(b, err) == ANA_OK);
    pg.hit(0, 1.f);
    y = run_in(e, 4800, pos, [](uint64_t) { return 0.f; });
    float pk = 0;
    for (float v : y) pk = std::max(pk, std::fabs(v));
    CHECK(pk == Approx(0.25).margin(0.01));
    // GRAVAR: ENTRADA -> a low-pass at its lowest -> GRAVAR; a 4 kHz input records (almost) silent.
    SongDef sd;
    sd.block(1, 1, {});
    sd.order = {{0, 1}};
    REQUIRE(sd.send(pg) == ANA_OK);
    b.pedal[0] = pedal(3, PedalKind::Filter, {0, 0, 0, 0});
    b.pedals = 1;
    b.cable[0] = {0, 8, {}, 1};
    b.cable[1] = {9, 3, {}, 1};
    b.cables = 2;
    REQUIRE(pg.pedalboard(b, err) == ANA_OK);
    REQUIRE(pg.record(true, e.beat_frames_now() * 4) == ANA_OK);
    pg.play(0, true);
    auto hi = [](uint64_t f) { return 0.5f * float(std::sin(2 * kPi * 4000 * double(f) / 48000)); };
    run_in(e, (pos / 96000 + 1) * 96000 - pos + 96000, pos, hi);
    REQUIRE(pg.record(false, 0) == ANA_OK);
    y = run_in(e, 96000, pos, [](uint64_t) { return 0.f; });   // the loop played back: filtered
    pk = 0;
    for (size_t i = 1000; i < y.size(); i++) pk = std::max(pk, std::fabs(y[i]));
    CHECK(pk < 0.5f * 0.01f);
    pg.stop();
    // A loop: a pedal fed by its own output is silent; ENTRADA -> SAÍDA still plays.
    b.pedal[0] = pedal(4, PedalKind::Drive, {0, 0.5f, 0.8f, 0});
    b.cable[0] = {9, 8, {}, 1};
    b.cable[1] = {9, 2, {}, 1};
    b.cable[2] = {0, 2, {}, 1};
    b.cables = 3;
    REQUIRE(pg.pedalboard(b, err) == ANA_OK);
    const uint64_t p1 = pos;
    y = run_in(e, 4800, pos, x);
    worst = 0;
    for (size_t i = 0; i < y.size(); i++) worst = std::max(worst, double(std::fabs(y[i] - x(p1 + i))));
    CHECK(worst < 1e-5);
    b.cable[0] = {2, 8, {}, 1};   // from an input jack: refused
    CHECK(pg.pedalboard(b, err) == ANA_ERR_ARG);
    e.stop();
    std::remove("dz_pad0.wav");
}
