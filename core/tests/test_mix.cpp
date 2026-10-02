#include <catch2/catch_approx.hpp>
#include <catch2/catch_test_macros.hpp>
#include <cmath>
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
ChannelParams defaults() { ChannelParams c; mix::channel_defaults(c); return c; }
}  // namespace

TEST_CASE("mix: a strip with its defaults is transparent; filters, EQ, pan do what they say") {
    Player p;
    REQUIRE(p.load(wav("dz_mix_a.wav", sine(1000, 0.5)).c_str()) == ANA_OK);
    CHECK(peak_db(play(p), 4800) == Approx(20 * std::log10(0.5)).margin(0.05));   // defaults: nothing on, unity

    auto c = defaults();
    c.hpOn = 1; c.hpHz = 4000; c.steep = 1;       // 1 kHz two octaves under a 24 dB/oct high-pass
    p.set_channel(0, c);
    CHECK(peak_db(play(p), 4800) < -6 - 40);
    c.hpOn = 0;
    c.eqOn = 1; c.eq[1] = {1000, 6, 1.0f, EqType::Peak, 1, {}};   // +6 dB bell at the tone
    p.set_channel(0, c);
    CHECK(peak_db(play(p), 4800) == Approx(-6.02 + 6).margin(0.3));
    c.eqOn = 0;
    c.pan = -1;                                   // hard left, constant power: centre = unity, left = +3 dB
    p.set_channel(0, c);
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
    auto c = defaults();
    c.gateOn = 1; c.gateThresholdDb = -40; c.gateRangeDb = 40; c.gateReleaseMs = 20;
    p.set_channel(0, c);
    auto y = play(p);
    CHECK(peak_db(y, size_t(0.75 * kRate)) < -60 - 30);   // the quiet half is gated 40 dB down
    CHECK(peak_db(y, size_t(0.1 * kRate)) == Approx(-6.02).margin(0.2));

    c.gateOn = 0;
    c.compOn = 1; c.compThresholdDb = -18; c.compRatio = 4; c.compAttackMs = 1; c.compReleaseMs = 50;
    p.set_channel(0, c);
    y = play(p);
    // -6 dBFS in, 12 dB over the threshold at 4:1: 9 dB of reduction -> about -15 dBFS.
    const double settled = peak_db(std::vector<float>(y.begin() + size_t(0.3 * kRate) * 2, y.begin() + size_t(0.45 * kRate) * 2), 0);
    CHECK(settled == Approx(-15.0).margin(0.8));
    MixMeters m{};
    p.meters(m);
    CHECK(m.tracks == 1);

    c.compOn = 0;
    c.faderDb = 12;                               // +12 dB into a -1 dBFS limiter
    p.set_channel(0, c);
    MasterParams mp;
    mix::master_defaults(mp);
    mp.limiterOn = 1; mp.limiterCeilingDb = -1;
    p.set_master(mp);
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
    auto c0 = defaults(), c1 = defaults();
    c1.solo = 1;
    p.set_channel(0, c0);
    p.set_channel(1, c1);
    auto y = play(p);
    // Only the 880 Hz stem: zero crossings ~ 1760 per second.
    int zc = 0;
    for (size_t i = 4800 + 1; i < y.size() / 2; i++) zc += (y[2 * i] >= 0) != (y[2 * i - 2] >= 0);
    CHECK(zc == Approx(1760 * 0.9).margin(40));

    // The tap is before the fader: a -20 dB fader does not change it, the trim does.
    c1.solo = 0; c1.faderDb = -20; c1.trimDb = -6;
    p.set_channel(1, c1);
    REQUIRE(p.render_tap(0b10, "dz_mix_tap.wav") == ANA_OK);
    Player t;
    REQUIRE(t.load("dz_mix_tap.wav") == ANA_OK);
    std::vector<float> mn(64), mx(64);
    t.peaks(4800, 48000, 64, mn.data(), mx.data());
    CHECK(20 * std::log10(*std::max_element(mx.begin(), mx.end())) == Approx(20 * std::log10(0.25) - 6).margin(0.3));

    // Bounce: mute the take, the stem at -20 dB fader and -6 dB trim.
    c0.mute = 1;
    p.set_channel(0, c0);
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
