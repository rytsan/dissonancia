#include <catch2/catch_approx.hpp>
#include <catch2/catch_test_macros.hpp>
#include <cmath>
#include <cstdio>
#include <vector>

#include "miniaudio.h"
#include "player.hpp"

using namespace dz;
using Catch::Approx;

namespace {
// Stereo WAV: left = sine 0.5 at 441 Hz, right = silence; 1 s at 44.1 kHz.
std::string write_wav() {
    const char* path = "dz_test_player.wav";
    ma_encoder enc;
    ma_encoder_config c = ma_encoder_config_init(ma_encoding_format_wav, ma_format_f32, 2, 44100);
    REQUIRE(ma_encoder_init_file(path, &c, &enc) == MA_SUCCESS);
    std::vector<float> x(44100 * 2);
    for (size_t i = 0; i < 44100; i++) x[2 * i] = 0.5f * float(std::sin(2 * 3.14159265358979 * 441 * double(i) / 44100));
    ma_encoder_write_pcm_frames(&enc, x.data(), 44100, nullptr);
    ma_encoder_uninit(&enc);
    return path;
}
}  // namespace

TEST_CASE("player: decode, peaks at any zoom, render, loop, end") {
    const std::string path = write_wav();
    Player p;
    REQUIRE(p.load(path.c_str()) == ANA_OK);
    PlayerInfo info{};
    p.info(info);
    CHECK(info.frames == 44100);
    CHECK(info.sampleRate == 44100);
    CHECK(info.channels == 2);
    CHECK_FALSE(info.playing);

    // Whole file in 10 columns (mipmap) and 1 ms in 44 columns (samples): mono mix = L / 2.
    std::vector<float> mn(10), mx(10);
    p.peaks(0, 44100, 10, mn.data(), mx.data());
    for (int i = 0; i < 10; i++) { CHECK(mx[size_t(i)] == Approx(0.25).margin(0.01)); CHECK(mn[size_t(i)] == Approx(-0.25).margin(0.01)); }
    std::vector<float> fmn(44), fmx(44);
    p.peaks(0, 44, 44, fmn.data(), fmx.data());   // one sample per column
    CHECK(fmx[10] == Approx(0.25f * std::sin(2 * 3.14159265358979 * 441 * 10 / 44100.0)).margin(1e-5));

    // Render: the file's frames, both output channels from the file's own.
    std::vector<float> out(2 * 64);
    p.render(out.data(), 64, 2);
    CHECK(out[0] == 0.f);   // stopped: silence
    p.seek(100);
    p.play();
    p.render(out.data(), 64, 2);
    CHECK(out[0] == Approx(0.5f * std::sin(2 * 3.14159265358979 * 441 * 100 / 44100.0)).margin(1e-6));
    CHECK(out[1] == 0.f);
    p.info(info);
    CHECK(info.positionFrame == 164);

    // Loop [1000, 1010): after 25 frames the cursor wrapped twice.
    p.set_loop(1000, 1010);
    p.seek(1000);
    p.render(out.data(), 25, 2);
    p.info(info);
    CHECK(info.positionFrame == 1005);
    CHECK(info.looping);

    // End: playback stops by itself at the last frame.
    p.set_loop(0, 0);
    p.seek(44100 - 10);
    p.render(out.data(), 64, 2);
    p.info(info);
    CHECK_FALSE(info.playing);
    CHECK(info.positionFrame == 44100);
    std::remove(path.c_str());
}

TEST_CASE("player: an unreadable file is an error, not a crash") {
    Player p;
    CHECK(p.load("does-not-exist.wav") == ANA_ERR_IO);
    PlayerInfo info{};
    p.info(info);
    CHECK(info.frames == 0);
    p.play();   // nothing to play: stays stopped
    p.info(info);
    CHECK_FALSE(info.playing);
}
