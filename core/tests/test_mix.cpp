#include <catch2/catch_approx.hpp>
#include <catch2/catch_test_macros.hpp>
#include <chrono>
#include <cmath>
#include <thread>
#include <cstdio>
#include <string>
#include <vector>

#include "miniaudio.h"
#include "mixdsp.hpp"
#include "player.hpp"

using namespace dz;
using Catch::Approx;

namespace {
constexpr double kPi = 3.14159265358979323846;
constexpr uint32_t kRate = 48000;

std::string wav(const char* path, const std::vector<float>& mono, uint32_t rate = kRate) {
    ma_encoder enc;
    ma_encoder_config c = ma_encoder_config_init(ma_encoding_format_wav, ma_format_f32, 1, rate);
    REQUIRE(ma_encoder_init_file(path, &c, &enc) == MA_SUCCESS);
    ma_encoder_write_pcm_frames(&enc, mono.data(), mono.size(), nullptr);
    ma_encoder_uninit(&enc);
    return path;
}
std::vector<float> sine(double hz, double amp, double sec = 1.0, uint32_t rate = kRate) {
    std::vector<float> x(size_t(sec * rate));
    for (size_t i = 0; i < x.size(); i++) x[i] = float(amp * std::sin(2 * kPi * hz * double(i) / rate));
    return x;
}
// Plays the whole take through the mixer and returns the stereo output.
std::vector<float> play(Player& p) {
    PlayerInfo info{};
    p.info(info);
    std::vector<float> out(size_t(info.frames) * 2);
    p.seek(0);
    p.play();
    for (size_t pos = 0; pos < info.frames; pos += 512) p.render(out.data() + pos * 2, uint32_t(std::min<uint64_t>(512, info.frames - pos)), 2);
    p.stop();
    return out;
}
double peak_db(const std::vector<float>& y, size_t from, int ch = 0) {
    float m = 0;
    for (size_t i = from; i < y.size() / 2; i++) m = std::max(m, std::fabs(y[2 * i + size_t(ch)]));
    return 20 * std::log10(std::max(m, 1e-9f));
}
MixGraph graph() { MixGraph g; mix::graph_defaults(g); return g; }
int unit(MixGraph& g, UnitKind kind, uint16_t id) {
    UnitParams u;
    mix::unit_defaults(u);
    u.kind = kind; u.on = 1; u.id = id;
    g.unit[g.units] = u;
    return g.units++;
}
void cable(MixGraph& g, JackKind fk, int from, JackKind tk, int to, float gain = 1) { g.cable[g.cables++] = {fk, uint8_t(from), tk, uint8_t(to), gain}; }
// Track t's SAI through the units in order to its VOLTA (its inserts).
void inserts(MixGraph& g, int t, std::initializer_list<int> units) {
    JackKind fk = JackKind::TrackOut;
    int from = t;
    for (int u : units) { cable(g, fk, from, JackKind::UnitIn, u); fk = JackKind::UnitOut; from = u; }
    cable(g, fk, from, JackKind::TrackReturn, t);
}
int zero_crossings(const std::vector<float>& y, size_t from) {
    int zc = 0;
    for (size_t i = from + 1; i < y.size() / 2; i++) zc += (y[2 * i] >= 0) != (y[2 * i - 2] >= 0);
    return zc;
}
}  // namespace

TEST_CASE("mix: a strip with its defaults is transparent; filters, EQ, pan do what they say") {
    Player p;
    REQUIRE(p.load(wav("dz_mix_a.wav", sine(1000, 0.5)).c_str()) == ANA_OK);
    CHECK(peak_db(play(p), 4800) == Approx(20 * std::log10(0.5)).margin(0.05));   // defaults: nothing on, unity

    auto g = graph();
    const int f = unit(g, UnitKind::Filter, 1), eq = unit(g, UnitKind::Eq, 2);
    g.unit[f].hpOn = 1; g.unit[f].hpHz = 4000; g.unit[f].steep = 1;   // 1 kHz two octaves under a 24 dB/oct high-pass
    g.unit[eq].on = 0;
    inserts(g, 0, {f, eq});
    p.set_mix(g);
    CHECK(peak_db(play(p), 4800) < -6 - 40);
    g.unit[f].on = 0;
    g.unit[eq].on = 1; g.unit[eq].eq[1] = {1000, 6, 1.0f, EqType::Peak, 1, {}};   // +6 dB bell at the tone
    p.set_mix(g);
    CHECK(peak_db(play(p), 4800) == Approx(-6.02 + 6).margin(0.3));
    g.unit[eq].on = 0;
    g.track[0].pan = -1;                          // hard left, constant power: centre = unity, left = +3 dB
    p.set_mix(g);
    auto y = play(p);
    CHECK(peak_db(y, 4800, 0) == Approx(-6.02 + 3.01).margin(0.1));
    CHECK(peak_db(y, 4800, 1) < -80);
    std::remove("dz_mix_a.wav");
}

TEST_CASE("mix: gate, compressor, limiter") {
    Player p;
    // 0.5 s at -6 dBFS then 0.5 s at -60 dBFS.
    auto x = sine(440, 0.5, 0.5);
    auto q = sine(440, 0.001, 0.5);
    x.insert(x.end(), q.begin(), q.end());
    REQUIRE(p.load(wav("dz_mix_b.wav", x).c_str()) == ANA_OK);
    auto g = graph();
    const int gate = unit(g, UnitKind::Gate, 1), comp = unit(g, UnitKind::Comp, 2);
    g.unit[gate].gateThresholdDb = -40; g.unit[gate].gateRangeDb = 40; g.unit[gate].gateReleaseMs = 20;
    g.unit[comp].on = 0;
    inserts(g, 0, {gate, comp});
    p.set_mix(g);
    auto y = play(p);
    CHECK(peak_db(y, size_t(0.75 * kRate)) < -60 - 30);   // the quiet half is gated 40 dB down
    CHECK(peak_db(y, size_t(0.1 * kRate)) == Approx(-6.02).margin(0.2));

    g.unit[gate].on = 0;
    auto& cu = g.unit[comp];
    cu.on = 1; cu.compThresholdDb = -18; cu.compRatio = 4; cu.compAttackMs = 1; cu.compReleaseMs = 50;
    p.set_mix(g);
    y = play(p);
    // -6 dBFS in, 12 dB over the threshold at 4:1: 9 dB of reduction -> about -15 dBFS.
    const double settled = peak_db(std::vector<float>(y.begin() + size_t(0.3 * kRate) * 2, y.begin() + size_t(0.45 * kRate) * 2), 0);
    CHECK(settled == Approx(-15.0).margin(0.8));
    MixMeters m{};
    p.meters(m);
    CHECK(m.tracks == 1);

    cu.on = 0;
    g.track[0].faderDb = 12;                      // +12 dB into a -1 dBFS limiter
    g.master.limiterOn = 1; g.master.limiterCeilingDb = -1;
    p.set_mix(g);
    y = play(p);
    CHECK(peak_db(y, 0) <= -1.0 + 0.01);
    std::remove("dz_mix_b.wav");
}

TEST_CASE("mix: stems as tracks, mute / solo, tap before the fader, bounce") {
    Player p;
    REQUIRE(p.load(wav("dz_mix_take.wav", sine(220, 0.25)).c_str()) == ANA_OK);
    // A stem at another rate is resampled to the take's rate and length.
    REQUIRE(p.add_track(wav("dz_mix_stem.wav", sine(880, 0.25, 1.0, 44100), 44100).c_str()) == 1);
    CHECK(p.tracks() == 2);
    auto g = graph();
    g.track[1].solo = 1;
    p.set_mix(g);
    auto y = play(p);
    // Only the 880 Hz stem: zero crossings ~ 1760 per second.
    CHECK(zero_crossings(y, 4800) == Approx(1760 * 0.9).margin(40));

    // The tap is before the fader: a -20 dB fader does not change it, an insert does.
    g.track[1].solo = 0; g.track[1].faderDb = -20;
    const int trim = unit(g, UnitKind::Trim, 1);
    g.unit[trim].trimDb = -6;
    inserts(g, 1, {trim});
    p.set_mix(g);
    REQUIRE(p.render_tap(0b10, "dz_mix_tap.wav") == ANA_OK);
    Player t;
    REQUIRE(t.load("dz_mix_tap.wav") == ANA_OK);
    std::vector<float> mn(64), mx(64);
    t.peaks(4800, 48000, 64, mn.data(), mx.data());
    CHECK(20 * std::log10(*std::max_element(mx.begin(), mx.end())) == Approx(20 * std::log10(0.25) - 6).margin(0.3));

    // Bounce: mute the take, the stem at -20 dB fader and -6 dB trim.
    g.track[0].mute = 1;
    p.set_mix(g);
    REQUIRE(p.bounce("dz_mix_bounce.wav") == ANA_OK);
    Player bnc;
    REQUIRE(bnc.load("dz_mix_bounce.wav") == ANA_OK);
    bnc.peaks(4800, 48000, 64, mn.data(), mx.data());
    CHECK(20 * std::log10(*std::max_element(mx.begin(), mx.end())) == Approx(20 * std::log10(0.25) - 26).margin(0.5));

    // An edit changes the take's length: the stems no longer align and are dropped.
    EditSegment seg[1] = {{0, 24000, 0.f, {}}};
    REQUIRE(p.apply_edits(seg, 1, 0, 0, 1.f) == ANA_OK);
    CHECK(p.tracks() == 1);
    for (const char* f : {"dz_mix_take.wav", "dz_mix_stem.wav", "dz_mix_tap.wav", "dz_mix_bounce.wav"}) std::remove(f);
}

namespace {
std::vector<float> tone(double amp, double sec) { return sine(1000, amp, sec); }
MixMeters meters_after(Player& p) {
    play(p);
    MixMeters m{};
    p.meters(m);   // play() stops at the end: integrated, range and true peak hold
    return m;
}
}  // namespace

TEST_CASE("meters: loudness as EBU Tech 3341 / 3342, true peak between samples") {
    Player p;
    // Tech 3341 case 1: 1 kHz at -23 dBFS in both channels reads -23 LUFS.
    REQUIRE(p.load(wav("dz_lufs_a.wav", tone(std::pow(10, -23 / 20.0), 20)).c_str()) == ANA_OK);
    auto m = meters_after(p);
    CHECK(m.lufsIntegrated == Approx(-23).margin(0.1));
    // Case 3-like: quiet parts 13 dB under are dropped by the relative gate.
    auto x = tone(std::pow(10, -36 / 20.0), 10), loud = tone(std::pow(10, -23 / 20.0), 20);
    x.insert(x.end(), loud.begin(), loud.end());
    x.insert(x.end(), x.begin(), x.begin() + 480000);
    REQUIRE(p.load(wav("dz_lufs_b.wav", x).c_str()) == ANA_OK);
    m = meters_after(p);
    CHECK(m.lufsIntegrated == Approx(-23).margin(0.2));
    // Tech 3342 case 1: 20 s at -20 dBFS then 20 s at -30 dBFS: LRA 10 LU.
    x = tone(std::pow(10, -20 / 20.0), 20);
    auto soft = tone(std::pow(10, -30 / 20.0), 20);
    x.insert(x.end(), soft.begin(), soft.end());
    REQUIRE(p.load(wav("dz_lufs_c.wav", x).c_str()) == ANA_OK);
    m = meters_after(p);
    CHECK(m.loudnessRange == Approx(10).margin(1));
    // fs / 4 at 45°: every sample at 0.707 of the crest; the true peak finds the crest.
    std::vector<float> q(48000);
    for (size_t i = 0; i < q.size(); i++) q[i] = float(0.5 * std::sin(kPi / 2 * double(i) + kPi / 4));
    REQUIRE(p.load(wav("dz_tp.wav", q).c_str()) == ANA_OK);
    m = meters_after(p);
    CHECK(m.masterPeakDb[0] < -8.5f);   // sample peak -9.03
    CHECK(m.truePeakDb[0] == Approx(-6.02).margin(0.6));
    for (auto f : {"dz_lufs_a.wav", "dz_lufs_b.wav", "dz_lufs_c.wav", "dz_tp.wav"}) std::remove(f);
}

TEST_CASE("meters: levels between the units, the scope ring") {
    Player p;
    REQUIRE(p.load(wav("dz_stage.wav", sine(1000, 0.25, 0.5)).c_str()) == ANA_OK);
    auto g = graph();
    const int trim = unit(g, UnitKind::Trim, 7), eq = unit(g, UnitKind::Eq, 8);
    g.unit[trim].trimDb = 6;
    g.unit[eq].on = 0;
    inserts(g, 0, {trim, eq});
    p.set_mix(g);
    PlayerInfo info{};
    p.info(info);
    std::vector<float> out(size_t(info.frames) * 2);
    p.seek(0);
    p.play();
    p.render(out.data(), 4800, 2);   // 100 ms, still playing
    MixMeters m{};
    p.meters(m);
    CHECK(m.inDb[0] == Approx(-12.04).margin(0.1));        // SAI
    CHECK(m.unitDb[trim] == Approx(-6.02).margin(0.1));    // after the trim
    CHECK(m.unitDb[eq] == Approx(-6.02).margin(0.1));      // bypassed
    CHECK(m.returnDb[0] == Approx(-6.02).margin(0.1));     // VOLTA
    std::vector<float> sc(2 * 1024);
    REQUIRE(p.scope(0, sc.data(), 1024) == 1024);
    for (int i = 0; i < 1024; i++) CHECK(sc[size_t(2 * i)] == out[size_t(2 * (4800 - 1024 + i))]);
    REQUIRE(p.scope(1, sc.data(), 16) == 16);   // the monitored track (0) at its tap: the same here
    CHECK(sc[30] == Approx(out[2 * 4799]).margin(1e-6));
    p.stop();
    std::remove("dz_stage.wav");
}

TEST_CASE("mix: a track's pre-chain source (VST3 render) replaces its audio until cleared") {
    Player p;
    REQUIRE(p.load(wav("dz_src_a.wav", sine(1000, 0.25, 0.5)).c_str()) == ANA_OK);
    REQUIRE(p.set_source(0, wav("dz_src_b.wav", sine(1000, 0.5, 0.3)).c_str()) == ANA_OK);   // shorter: padded to the take
    auto y = play(p);
    CHECK(y.size() == size_t(24000) * 2);
    CHECK(peak_db(y, 4800) == Approx(-6.02).margin(0.05));        // the source, not the take (-12)
    CHECK(std::fabs(y[2 * 20000]) < 1e-6f);                     // past the source's end: silence
    REQUIRE(p.set_source(0, nullptr) == ANA_OK);
    CHECK(peak_db(play(p), 4800) == Approx(-12.04).margin(0.05));
    CHECK(p.set_source(3, "dz_src_b.wav") == ANA_ERR_ARG);       // no such track
    REQUIRE(p.set_source(0, "dz_src_b.wav") == ANA_OK);
    REQUIRE(p.load("dz_src_a.wav") == ANA_OK);                   // new audio drops the source
    CHECK(peak_db(play(p), 4800) == Approx(-12.04).margin(0.05));
    std::remove("dz_src_a.wav");
    std::remove("dz_src_b.wav");
}

TEST_CASE("mix graph: the cables make the chain — order, a parallel path at a cable's gain, two of a kind; a file through units") {
    Player p;
    REQUIRE(p.load(wav("dz_mix_order.wav", sine(1000, 0.25)).c_str()) == ANA_OK);
    auto g = graph();
    const int trim = unit(g, UnitKind::Trim, 1), comp = unit(g, UnitKind::Comp, 2);
    g.unit[trim].trimDb = 12;
    g.unit[comp].compThresholdDb = -20; g.unit[comp].compRatio = 20;
    // Trim before the compressor: +12 dB into it, squashed. Compressor first: -12 dBFS barely touched, then +12.
    inserts(g, 0, {trim, comp});
    p.set_mix(g);
    const double trimFirst = peak_db(play(p), 9600);
    g.cables = 0;
    inserts(g, 0, {comp, trim});
    p.set_mix(g);
    const double compFirst = peak_db(play(p), 9600);
    CHECK(compFirst > trimFirst + 3);
    // Unpatched: the compressor is in the box but no cable goes through it.
    g.cables = 0;
    inserts(g, 0, {trim});
    p.set_mix(g);
    CHECK(peak_db(play(p), 9600) == Approx(20 * std::log10(0.25) + 12).margin(0.1));
    // Parallel: SAI on two cables, one through the trim (+12 dB, x3.98) at 0.5, both into VOLTA: dry + 0.5 * wet.
    g.cables = 0;
    cable(g, JackKind::TrackOut, 0, JackKind::TrackReturn, 0);
    cable(g, JackKind::TrackOut, 0, JackKind::UnitIn, trim);
    cable(g, JackKind::UnitOut, trim, JackKind::TrackReturn, 0, 0.5f);
    p.set_mix(g);
    CHECK(peak_db(play(p), 9600) == Approx(20 * std::log10(0.25 * (1 + 0.5 * 3.981))).margin(0.1));
    // Two trims in series: +12 and -6.
    const int trim2 = unit(g, UnitKind::Trim, 3);
    g.unit[trim2].trimDb = -6;
    g.cables = 0;
    inserts(g, 0, {trim, trim2});
    p.set_mix(g);
    CHECK(peak_db(play(p), 9600) == Approx(20 * std::log10(0.25) + 6).margin(0.1));
    // A file through units in series (the units before a VST3 insert): same gain, stereo out.
    const UnitParams series[2] = {g.unit[trim], g.unit[trim2]};
    REQUIRE(render_chain("dz_mix_order.wav", series, 2, "dz_mix_chain.wav") == ANA_OK);
    Player q;
    REQUIRE(q.load("dz_mix_chain.wav") == ANA_OK);
    CHECK(peak_db(play(q), 9600) == Approx(20 * std::log10(0.25) + 6).margin(0.1));
    std::remove("dz_mix_order.wav");
    std::remove("dz_mix_chain.wav");
}

TEST_CASE("mix graph: buses, sends, returns into another track, loops, solo") {
    Player p;
    REQUIRE(p.load(wav("dz_mix_bus_a.wav", sine(220, 0.25)).c_str()) == ANA_OK);
    REQUIRE(p.add_track(wav("dz_mix_bus_b.wav", sine(880, 0.25)).c_str()) == 1);
    const double a = 20 * std::log10(0.25);

    // A bus: both tracks' SAI into one trim (-6 dB), out to the master. The tracks' own VOLTAs get
    // nothing (their SAI is patched): each heard once, through the bus.
    auto g = graph();
    const int bus = unit(g, UnitKind::Trim, 1);
    g.unit[bus].trimDb = -6;
    cable(g, JackKind::TrackOut, 0, JackKind::UnitIn, bus);
    cable(g, JackKind::TrackOut, 1, JackKind::UnitIn, bus);
    cable(g, JackKind::UnitOut, bus, JackKind::Master, 0);
    p.set_mix(g);
    auto y = play(p);
    CHECK(peak_db(y, 4800) == Approx(20 * std::log10(0.5 * 0.964) - 6).margin(0.1));   // 220 + 880 Hz at 0.25 each: their sum peaks at 0.482
    g.track[0].faderDb = -60; g.track[1].faderDb = -60;                         // the faders carry nothing here
    p.set_mix(g);
    CHECK(peak_db(play(p), 4800) == Approx(20 * std::log10(0.5 * 0.964) - 6).margin(0.1));

    // A send: track 1 normalled and heard; its AUX (after the fader) into a -20 dB trim to the master.
    g = graph();
    const int fx = unit(g, UnitKind::Trim, 2);
    g.unit[fx].trimDb = -20;
    g.track[0].mute = 1;
    cable(g, JackKind::TrackAux, 1, JackKind::UnitIn, fx);
    cable(g, JackKind::UnitOut, fx, JackKind::Master, 0);
    p.set_mix(g);
    CHECK(peak_db(play(p), 4800) == Approx(20 * std::log10(0.25 * 1.1)).margin(0.1));   // dry + 0.1 x dry
    g.track[1].mute = 1;                                                                 // muted: its send too
    p.set_mix(g);
    CHECK(peak_db(play(p), 4800) < -80);

    // Into another track's VOLTA: track 1's chain ends at track 0's return, before track 0's fader.
    g = graph();
    cable(g, JackKind::TrackOut, 1, JackKind::TrackReturn, 0);
    g.track[0].faderDb = -60;
    p.set_mix(g);
    CHECK(peak_db(play(p), 4800) < a - 50);   // both under track 0's fader
    g.track[0].faderDb = 0;
    g.track[0].solo = 1;                      // track 0 soloed: what comes into it is heard with it
    p.set_mix(g);
    CHECK(peak_db(play(p), 4800) == Approx(20 * std::log10(0.5 * 0.964)).margin(0.1));
    g.track[0].solo = 0;
    g.track[1].solo = 1;                      // track 1 soloed: track 0, which it feeds, is heard too
    p.set_mix(g);
    CHECK(peak_db(play(p), 4800) == Approx(20 * std::log10(0.5 * 0.964)).margin(0.1));

    // A loop: track 1's AUX back into its own VOLTA through a unit. Track 1 is silent, track 0 plays.
    g = graph();
    const int u = unit(g, UnitKind::Trim, 3);
    cable(g, JackKind::TrackAux, 1, JackKind::UnitIn, u);
    cable(g, JackKind::UnitOut, u, JackKind::TrackReturn, 1);
    p.set_mix(g);
    y = play(p);
    CHECK(peak_db(y, 4800) == Approx(a).margin(0.1));
    CHECK(zero_crossings(y, 4800) == Approx(440 * 0.9).margin(20));

    // Out of range: refused, the mix as it was.
    g.cable[0].to = 40;
    CHECK(p.set_mix(g) == ANA_ERR_ARG);
    for (const char* f : {"dz_mix_bus_a.wav", "dz_mix_bus_b.wav"}) std::remove(f);
}

TEST_CASE("mix ring: rendered ahead, a change heard a few blocks after the one playing; bounce = playback, sample for sample") {
    Player p;
    REQUIRE(p.load(wav("dz_mix_ring.wav", sine(440, 0.5, 2.0)).c_str()) == ANA_OK);
    auto g = graph();
    const int eq = unit(g, UnitKind::Eq, 1), comp = unit(g, UnitKind::Comp, 2), trim = unit(g, UnitKind::Trim, 3);
    g.unit[eq].eq[0].gainDb = 4;
    g.unit[comp].compThresholdDb = -20;
    inserts(g, 0, {eq, comp, trim});
    p.set_mix(g);
    auto y = play(p);
    REQUIRE(p.bounce("dz_mix_ring_b.wav") == ANA_OK);
    Player q;
    REQUIRE(q.load("dz_mix_ring_b.wav") == ANA_OK);
    auto z = play(q);   // the bounce played plain
    REQUIRE(z.size() == y.size());
    size_t same = 0;
    for (size_t i = 0; i < y.size(); i++) same += y[i] == z[i];
    CHECK(same == y.size());

    // Playing, 4800 frames in (a second rendered ahead): the trim to -20 dB.
    PlayerInfo info{};
    p.info(info);
    std::vector<float> out(size_t(info.frames) * 2);
    p.seek(0);
    p.play();
    size_t pos = 0;
    for (; pos < 4800; pos += 512) p.render(out.data() + pos * 2, 512, 2);
    const double before = peak_db(std::vector<float>(out.begin() + 2400 * 2, out.begin() + pos * 2), 0);
    g.unit[trim].trimDb = -20;
    p.set_mix(g);
    for (; pos + 512 <= info.frames; pos += 512) p.render(out.data() + pos * 2, 512, 2);
    // The blocks already handed on (the guard) play as rendered; from the cut, -20 dB.
    CHECK(peak_db(std::vector<float>(out.begin() + 4800 * 2, out.begin() + 6400 * 2), 0) == Approx(before).margin(0.1));
    CHECK(peak_db(std::vector<float>(out.begin() + 12000 * 2, out.begin() + 30000 * 2), 0) == Approx(before - 20).margin(1.0));
    // No click at the cut: the gain glides (no sample jumps more than the sine itself can).
    double jump = 0;
    for (size_t i = 4800; i < 12000; i++) jump = std::max(jump, double(std::fabs(out[2 * i] - out[2 * i - 2])));
    CHECK(jump < 2 * kPi * 440 / kRate * 0.6);
    p.stop();
    std::remove("dz_mix_ring.wav");
    std::remove("dz_mix_ring_b.wav");
}

TEST_CASE("mix ring: with a device (miniaudio's null backend) the worker renders ahead and the callback plays it") {
    ma_backend backends[1] = {ma_backend_null};
    ma_context ctx;
    REQUIRE(ma_context_init(backends, 1, nullptr, &ctx) == MA_SUCCESS);
    {
        Player p;
        REQUIRE(p.load(wav("dz_mix_dev.wav", sine(440, 0.5, 3.0)).c_str()) == ANA_OK);
        REQUIRE(p.open_device(&ctx) == ANA_OK);
        p.play();
        std::this_thread::sleep_for(std::chrono::milliseconds(400));
        PlayerInfo info{};
        p.info(info);
        CHECK(info.playing);
        CHECK(info.positionFrame > 4800);
        MixMeters m{};
        p.meters(m);
        CHECK(m.peakDb[0] == Approx(-6.02).margin(0.2));
        CHECK(m.underruns <= 2);   // the first period may come before the first block
        // A change while playing: heard within ~50 ms (plus the null device's period).
        auto g = graph();
        g.track[0].faderDb = -20;
        p.set_mix(g);
        std::this_thread::sleep_for(std::chrono::milliseconds(250));
        p.meters(m);
        CHECK(m.peakDb[0] == Approx(-26.02).margin(0.3));
        p.seek(0);
        std::this_thread::sleep_for(std::chrono::milliseconds(100));
        p.info(info);
        CHECK(info.positionFrame < 48000 / 2);
        p.close_device();
    }
    ma_context_uninit(&ctx);
    std::remove("dz_mix_dev.wav");
}
