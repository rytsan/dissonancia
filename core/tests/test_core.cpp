#include <atomic>
#include <catch2/catch_approx.hpp>
#include <catch2/catch_test_macros.hpp>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <sstream>
#include <thread>
#include <vector>

#include "engine.hpp"
#include "rt.hpp"

using namespace dz;
using Catch::Approx;

namespace {

SessionConfig session(AnalysisMode mode = AnalysisMode::VoiceMono) {
    SessionConfig s{};
    s.mode = mode;
    s.quality = AudioQuality::Balanced;
    s.referenceA4 = 440;
    s.keyFifths = 1;
    s.clef = Clef::Treble8vb;
    s.meter = {4, 4};
    s.bpm = 120;
    s.metronome = 1;
    s.countInBars = 1;
    return s;
}

AudioDeviceConfig headless(bool click = true) {
    AudioDeviceConfig d{};
    d.captureDevice = -1;
    d.sampleRate = 48000;
    d.clickOutput = click;
    return d;
}

// Fills `block` with a sine continuing from frame `pos`.
void sine(std::vector<float>& block, uint64_t pos, float amp, double hz = 1000) {
    for (size_t i = 0; i < block.size(); i++) block[i] = amp * float(std::sin(2 * 3.14159265358979 * hz * double(pos + i) / 48000));
}

template <class F>
bool wait_until(F&& cond, double seconds = 5) {
    auto end = std::chrono::steady_clock::now() + std::chrono::duration<double>(seconds);
    while (!cond()) {
        if (std::chrono::steady_clock::now() > end) return false;
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    return true;
}

}  // namespace

TEST_CASE("mode x quality table") {
    auto v48 = live_config(AnalysisMode::VoiceMono, AudioQuality::Balanced, 48000);
    CHECK(v48.decimation == 3);   // 16 kHz
    CHECK(live_config(AnalysisMode::VoiceMono, AudioQuality::Balanced, 44100).decimation == 2);   // 22.05 kHz
    CHECK(v48.hopSeconds == Approx(0.010f));

    auto g = live_config(AnalysisMode::GuitarChords, AudioQuality::Balanced, 48000);
    CHECK(g.decimation == 2);
    CHECK(g.windowSeconds == Approx(0.210).margin(0.002));   // T_low at 82.4 Hz, 12 bpo
    CHECK(live_config(AnalysisMode::GuitarChords, AudioQuality::LowLatency, 48000).windowSeconds == Approx(0.173).margin(0.002));
    CHECK(live_config(AnalysisMode::PianoChords, AudioQuality::HighPrecision, 48000).windowSeconds == Approx(0.630).margin(0.003));
}

TEST_CASE("triple buffer returns the newest published slot") {
    TripleBuffer<int> tb;
    tb.write_slot() = 1;
    tb.publish();
    tb.write_slot() = 2;
    tb.publish();
    CHECK(tb.read() == 2);
    CHECK(tb.read() == 2);   // nothing new: same slot
    tb.write_slot() = 3;
    tb.publish();
    CHECK(tb.read() == 3);
}

TEST_CASE("event queue: overflow drops, counts, and leaves a sequence gap") {
    Engine e;
    REQUIRE(e.start(session(), headless(false), nullptr, nullptr) == ANA_OK);
    AnalyzerEvent ev{};
    ev.type = AnalyzerEventType::Onset;
    for (size_t i = 0; i < ANA_EVENT_QUEUE_CAPACITY; i++) REQUIRE(e.push_event(ev));
    CHECK_FALSE(e.push_event(ev));
    std::vector<AnalyzerEvent> out(ANA_EVENT_QUEUE_CAPACITY + 10);
    size_t n = e.drain_events(out.data(), out.size());
    REQUIRE(n == ANA_EVENT_QUEUE_CAPACITY);
    CHECK(out[0].sequence == 0);
    CHECK(out[n - 1].sequence == n - 1);
    REQUIRE(e.push_event(ev));
    REQUIRE(e.drain_events(out.data(), 1) == 1);
    CHECK(out[0].sequence == ANA_EVENT_QUEUE_CAPACITY + 1);   // gap = the dropped one
    e.stop();
}

TEST_CASE("meters, scope, notify rule and metronome click on the input clock") {
    Engine e;
    REQUIRE(e.start(session(), headless(), nullptr, nullptr) == ANA_OK);

    const float amp = 0.1f;   // -20 dBFS peak, -23.01 dBFS RMS
    std::vector<float> in(64), out(64), click;
    for (uint64_t pos = 0; pos < 48000; pos += 64) {
        sine(in, pos, amp);
        std::fill(out.begin(), out.end(), 0.f);
        e.on_audio(in.data(), out.data(), 64);   // allocation trap armed inside
        click.insert(click.end(), out.begin(), out.end());
        std::this_thread::sleep_for(std::chrono::microseconds(50));
    }
    LiveSnapshot s{};
    REQUIRE(wait_until([&] { e.read_snapshot(&s); return s.analyzedFrames == 48000; }));

    CHECK(s.peakDbfs == Approx(-20).margin(0.1));
    CHECK(s.vuLevel == Approx(-23.01 + 18).margin(0.5));   // 300 ms ballistics, ~1 s in
    CHECK_FALSE(s.clipLatched);
    CHECK(s.hopSeconds == Approx(0.01f));
    CHECK(s.liveRate == 16000);
    CHECK(s.xruns == 0);
    CHECK(s.waveWriteIndex == 48000 / 256 % ANA_WAVE_COLUMNS);
    CHECK(s.waveMax[s.waveWriteIndex - 1] == Approx(amp).margin(0.002));
    CHECK(s.scope[ANA_SCOPE_SAMPLES - 1] == Approx(in.back()));   // newest sample last
    CHECK(s.beatInBar == 3);   // frame 48000 at 120 bpm = beat index 2
    CHECK(s.metronomeBpm == 120);

    // Notify rule: about one wake-up per hop (100), not one per callback (750). A notify can
    // race with the analysis thread re-checking the ring, so allow a few spurious ones.
    CHECK(e.notify_count() > 0);
    CHECK(e.notify_count() <= 110);

    // Click onsets exactly on the beat frames (0 and 24000 at 120 bpm / 48 kHz).
    auto energy = [&](size_t a, size_t b) { double x = 0; for (size_t i = a; i < b; i++) x += click[i] * click[i]; return x; };
    CHECK(energy(23800, 24000) == 0);
    CHECK(energy(24000, 24200) > 0.01);
    CHECK(energy(1500, 23800) == 0);   // 30 ms click from beat 0 ended by 1440
    e.stop();
}

TEST_CASE("REC: count-in, bar-aligned start, WAV + sidecar, metronome locked") {
    Engine e;
    REQUIRE(e.start(session(), headless(), nullptr, nullptr) == ANA_OK);
    std::atomic<bool> feeding{true};
    std::thread feeder([&] {   // plays the callback at ~10x real time
        std::vector<float> in(480), out(480);
        for (uint64_t pos = 0; feeding; pos += 480) {
            sine(in, pos, 0.25f, 440);
            std::fill(out.begin(), out.end(), 0.f);
            e.on_audio(in.data(), out.data(), 480);
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
        }
    });

    REQUIRE(wait_until([&] { return e.callback_frames() > 30000; }));
    std::string wav = "dz_test_take.wav", json = "dz_test_take.json";
    REQUIRE(e.rec_start(wav.c_str()) == ANA_OK);
    CHECK(e.set_metronome(true, 90, {3, 4}) == ANA_ERR_STATE);

    const uint64_t bar = 96000;   // 4 beats at 120 bpm, 48 kHz
    LiveSnapshot s{};
    e.read_snapshot(&s);
    REQUIRE(wait_until([&] { e.read_snapshot(&s); return s.recording && s.recordedSeconds > 1.0; }, 10));
    REQUIRE(e.rec_stop() == ANA_OK);
    feeding = false;
    feeder.join();

    std::ifstream f(json);
    REQUIRE(f.good());
    std::stringstream ss;
    ss << f.rdbuf();
    std::string side = ss.str();
    auto field = [&](const char* key) {
        auto p = side.find(key);
        REQUIRE(p != std::string::npos);
        return std::stoull(side.substr(p + std::strlen(key)));
    };
    uint64_t start = field("\"startSample\": "), frames = field("\"frames\": ");
    CHECK(start % bar == 0);
    CHECK(start >= 2 * bar);   // next bar after 30000 frames, plus one count-in bar
    CHECK(field("\"recorderGaps\": ") == 0);
    CHECK(frames > 48000);
    // The sustained A4 was open at REC stop: flushed as one complete note into the take log.
    CHECK(side.find("\"eventLogComplete\": true") != std::string::npos);
    CHECK(side.find("\"midi\": 69, \"name\": \"A5\"") != std::string::npos);   // Treble 8vb session: A4 written A5

    ma_decoder dec;
    REQUIRE(ma_decoder_init_file(wav.c_str(), nullptr, &dec) == MA_SUCCESS);
    ma_uint64 len = 0;
    ma_decoder_get_length_in_pcm_frames(&dec, &len);
    CHECK(len == frames);
    float first[4];
    ma_decoder_read_pcm_frames(&dec, first, 4, nullptr);
    CHECK(first[1] == Approx(0.25f * std::sin(2 * 3.14159265358979 * 440 * double(start + 1) / 48000)).margin(1e-5));
    ma_decoder_uninit(&dec);

    CHECK(e.set_metronome(true, 90, {3, 4}) == ANA_OK);   // unlocked after REC
    e.stop();
    std::remove(wav.c_str());
    std::remove(json.c_str());
}
