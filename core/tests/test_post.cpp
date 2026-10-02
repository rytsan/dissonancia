#include <catch2/catch_test_macros.hpp>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <fstream>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

#include "miniaudio.h"
#include "post.hpp"

using namespace dz;

namespace {
// Harmonic tones (3 partials) per segment, mono 48 kHz.
std::string write(const char* path, const std::vector<std::pair<std::vector<int>, double>>& segs) {
    ma_encoder enc;
    ma_encoder_config c = ma_encoder_config_init(ma_encoding_format_wav, ma_format_f32, 1, 48000);
    REQUIRE(ma_encoder_init_file(path, &c, &enc) == MA_SUCCESS);
    std::vector<float> x;
    for (auto& [midis, sec] : segs)
        for (size_t i = 0; i < size_t(sec * 48000); i++) {
            double t = double(i) / 48000, v = 0;
            for (int m : midis)
                for (int h = 1; h <= 3; h++) v += 0.08 / h * std::sin(2 * 3.14159265358979 * h * 440 * std::pow(2, (m - 69) / 12.0) * t);
            x.push_back(float(v * std::min(1.0, t / 0.005)));
        }
    ma_encoder_write_pcm_frames(&enc, x.data(), x.size(), nullptr);
    ma_encoder_uninit(&enc);
    return path;
}
std::string slurp(const std::string& p) { std::ifstream f(p); std::stringstream s; s << f.rdbuf(); return s.str(); }
size_t count(const std::string& s, const std::string& what) { size_t n = 0; for (size_t p = s.find(what); p != std::string::npos; p = s.find(what, p + 1)) n++; return n; }
SessionConfig session(AnalysisMode m) {
    SessionConfig s{};
    s.mode = m; s.quality = AudioQuality::Balanced; s.referenceA4 = 440; s.meter = {4, 4}; s.bpm = 120; s.clef = Clef::Treble;
    return s;
}
}  // namespace

TEST_CASE("post: a melody file becomes a take JSON of its notes") {
    auto in = write("dz_post_voice.wav", {{{}, 0.3}, {{57}, 0.6}, {{59}, 0.6}, {{60}, 0.6}, {{}, 0.4}});
    PostJob job;
    REQUIRE(job.run(session(AnalysisMode::VoiceMono), 0.0, in, "dz_post_voice.json") == ANA_OK);
    std::string j = slurp("dz_post_voice.json");
    CHECK(j.find("\"analysis\": \"studio-offline\"") != std::string::npos);
    CHECK(j.find("\"quality\": 1") != std::string::npos);   // the session's (Balanced)
    CHECK(count(j, "\"type\": \"note\"") == 3);
    CHECK(j.find("\"name\": \"A3\"") != std::string::npos);
    CHECK(j.find("\"name\": \"C4\"") != std::string::npos);
    PostStatus st{};
    job.status(st);
    CHECK(st.progress == 1.f);
    std::remove(in.c_str());
    std::remove("dz_post_voice.json");
}

TEST_CASE("post: a chord file becomes a take JSON of its chords, and a job can be cancelled") {
    auto in = write("dz_post_chords.wav", {{{48, 52, 55}, 1.5}, {{43, 47, 50, 55}, 1.5}, {{}, 0.5}});
    PostJob job;
    REQUIRE(job.run(session(AnalysisMode::GuitarChords), 0.0, in, "dz_post_chords.json") == ANA_OK);
    std::string j = slurp("dz_post_chords.json");
    CHECK(count(j, "\"type\": \"chord\"") == 2);
    CHECK(j.find("\"symbol\": \"C\"") != std::string::npos);
    CHECK(j.find("\"symbol\": \"G\"") != std::string::npos);

    // Asynchronous: cancelled at once, it ends Cancelled and writes nothing.
    PostJob slow;
    REQUIRE(slow.start(session(AnalysisMode::GuitarChords), 0.0, in.c_str(), "dz_post_cancel.json") == ANA_OK);
    slow.cancel();
    PostStatus st{};
    for (int i = 0; i < 500; i++) {
        slow.status(st);
        if (st.state != PostState::Running) break;
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }
    CHECK(st.state == PostState::Cancelled);
    CHECK_FALSE(std::ifstream("dz_post_cancel.json").good());

    // A missing file fails cleanly.
    CHECK(job.run(session(AnalysisMode::VoiceMono), 0.0, "missing.wav", "x.json") == ANA_ERR_IO);
    std::remove(in.c_str());
    std::remove("dz_post_chords.json");
}
