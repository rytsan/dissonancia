#include <catch2/catch_approx.hpp>
#include <catch2/catch_test_macros.hpp>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <random>
#include <string>
#include <vector>

#include "rt.hpp"
#include "theory.hpp"
#include "voice.hpp"

using namespace dz;
using Catch::Approx;

namespace {

constexpr double kPi = 3.14159265358979323846;
constexpr uint32_t kRate = 48000;

double midi_hz(double m) { return 440.0 * std::pow(2.0, (m - 69) / 12); }

// Voice-like tone: 3 harmonics, 5 ms attack, optional vibrato (cents peak).
struct Segment { double midi; double seconds; double vibratoCents = 0; };

std::vector<float> render(const std::vector<Segment>& segs, float amp = 0.2f) {
    std::vector<float> x;
    double phase = 0;
    for (const auto& s : segs) {
        size_t n = size_t(s.seconds * kRate);
        for (size_t i = 0; i < n; i++) {
            if (s.midi < 0) { x.push_back(0); phase = 0; continue; }
            double t = double(i) / kRate;
            double hz = midi_hz(s.midi) * std::pow(2.0, s.vibratoCents * std::sin(2 * kPi * 5.5 * t) / 1200);
            phase += 2 * kPi * hz / kRate;
            double env = std::min(1.0, t / 0.005);
            x.push_back(float(amp * env * (std::sin(phase) + 0.5 * std::sin(2 * phase) + 0.25 * std::sin(3 * phase))));
        }
    }
    return x;
}

SessionConfig voice_session(AnalysisMode mode = AnalysisMode::VoiceMono, int8_t fifths = 1, Clef clef = Clef::Treble) {
    SessionConfig s{};
    s.mode = mode;
    s.referenceA4 = 440;
    s.keyFifths = fifths;
    s.clef = clef;
    s.meter = {4, 4};
    s.bpm = 120;
    return s;
}

struct Run {
    std::vector<AnalyzerEvent> events;
    std::vector<std::pair<uint64_t, PitchEstimate>> pitch;   // end frame of each hop
    std::vector<std::pair<uint64_t, NoteEstimate>> notes;
};

Run run(const std::vector<float>& x, const SessionConfig& s, AudioQuality q) {
    LiveConfig c = live_config(s.mode, q, kRate);
    uint32_t hop = uint32_t(std::lround(c.hopSeconds * kRate));
    VoicePipeline p(s, c, kRate, hop, 0.0);
    Run r;
    VoiceOutput out{};
    for (size_t pos = 0; pos + hop <= x.size(); pos += hop) {
        {
            RtScope rt;   // allocation trap armed (tests/rt_trap.cpp)
            p.process(x.data() + pos, hop, pos + hop, out);
        }
        for (uint32_t i = 0; i < out.eventCount; i++) r.events.push_back(out.events[i]);
        r.pitch.emplace_back(pos + hop, out.pitch);
        r.notes.emplace_back(pos + hop, out.note);
    }
    p.flush(out);
    for (uint32_t i = 0; i < out.eventCount; i++) r.events.push_back(out.events[i]);
    return r;
}

std::vector<MusicalNoteEvent> ended(const Run& r) {
    std::vector<MusicalNoteEvent> v;
    for (auto& e : r.events)
        if (e.type == AnalyzerEventType::NoteEnd) v.push_back(e.data.note);
    return v;
}

std::string names(const Run& r) {
    std::string s;
    for (auto& n : ended(r)) s += std::string(n.writtenName) + " ";
    return s;
}

}  // namespace

TEST_CASE("YIN: harmonic tones within 3 cents (6 above 800 Hz) across the voice range") {
    Yin y;
    y.init(16000, 800, 70, 1200);
    for (double hz : {82.41, 110.0, 196.0, 220.0, 440.0, 880.0, 1046.5}) {
        std::vector<float> x(800);
        for (size_t i = 0; i < x.size(); i++) {
            double ph = 2 * kPi * hz * double(i) / 16000;
            x[i] = float(0.3 * (std::sin(ph) + 0.6 * std::sin(2 * ph) + 0.3 * std::sin(3 * ph)));
        }
        float clarity = 0, f = y.estimate(x.data(), clarity);
        INFO("hz " << hz << " -> " << f);
        REQUIRE(f > 0);
        CHECK(std::fabs(1200 * std::log2(f / hz)) < (hz < 800 ? 3 : 6));   // lag ~15 samples at C6: interpolation limit
        CHECK(clarity > 0.8f);
    }
    std::mt19937 rng(1);
    std::normal_distribution<float> noise(0, 0.1f);
    std::vector<float> x(800);
    for (auto& v : x) v = noise(rng);
    float clarity = 0;
    CHECK(y.estimate(x.data(), clarity) == 0);   // noise is unvoiced
}

TEST_CASE("spelling: key signature, direction, letter octave, clef") {
    Speller g;
    g.configure(1, KeyMode::Major);   // G major
    char n[8];
    auto name = [&](Speller& sp, int midi, int prev, int shift = 0) { Speller::name(sp.spell(midi, prev), shift, n); return std::string(n); };
    CHECK(name(g, 66, -1) == "F#4");   // diatonic
    CHECK(name(g, 70, 69) == "A#4");   // ascending chromatic -> raised
    g.reset_phrase();
    CHECK(name(g, 70, 71) == "Bb4");   // descending chromatic -> lowered
    CHECK(name(g, 70, 69) == "Bb4");   // repeated within the phrase keeps its spelling
    g.reset_phrase();
    CHECK(name(g, 64, -1, 1) == "E5");   // Treble 8vb shifts the written octave

    Speller gb;
    gb.configure(-6, KeyMode::Major);   // Gb major: Cb is diatonic
    CHECK(name(gb, 59, -1) == "Cb4");   // MIDI 59 is Cb4, not B3 (octave follows the letter)
    Speller cs;
    cs.configure(7, KeyMode::Major);   // C# major: B# is diatonic
    CHECK(name(cs, 60, -1) == "B#3");

    Speller am;
    am.configure(0, KeyMode::NaturalMinor);
    CHECK(name(am, 68, 69) == "G#4");   // raised leading tone even when descending
    Speller gsm;
    gsm.configure(5, KeyMode::NaturalMinor);   // G# minor
    CHECK(name(gsm, 67, -1) == "Fx4");         // F double-sharp leading tone

    Speller c;
    c.configure(0, KeyMode::Major);
    CHECK(name(c, 61, -1) == "C#4");   // no context: C major prefers sharps
    Speller f;
    f.configure(-1, KeyMode::Major);
    CHECK(name(f, 61, -1) == "Db4");   // flat key prefers flats
    CHECK_FALSE(c.diatonic(61));
}

TEST_CASE("voice pipeline: onsets, backdated notes, latency budgets") {
    // 0.3 s silence, A3 0.6 s, B3 0.6 s, 0.4 s silence
    auto x = render({{-1, 0.3}, {57, 0.6}, {59, 0.6}, {-1, 0.4}});
    for (AudioQuality q : {AudioQuality::LowLatency, AudioQuality::Balanced, AudioQuality::HighPrecision}) {
        Run r = run(x, voice_session(), q);
        auto notes = ended(r);
        INFO("quality " << int(q) << " notes: " << names(r));
        REQUIRE(notes.size() == 2);
        CHECK(std::string(notes[0].writtenName) == "A3");
        CHECK(std::string(notes[1].writtenName) == "B3");
        CHECK(notes[0].startTimeSeconds == Approx(0.3).margin(0.012));
        CHECK(notes[0].endTimeSeconds == Approx(0.9).margin(0.04));
        CHECK(notes[1].startTimeSeconds == Approx(notes[0].endTimeSeconds));   // no overlap, no gap
        CHECK(notes[1].endTimeSeconds == Approx(1.5).margin(0.06));
        CHECK(std::fabs(notes[0].avgCents) < 5);
        CHECK(notes[0].medianHz == Approx(220).epsilon(0.005));
        CHECK_FALSE(notes[0].vibrato);

        // Latencies on the sample clock from the known onset (0.3 s).
        double firstPitch = -1, stable = -1;
        for (auto& [end, p] : r.pitch)
            if (end > 0.3 * kRate && p.voiced && std::fabs(p.midiFloat - 57) < 0.5) { firstPitch = double(end) / kRate - 0.3; break; }
        for (auto& [end, ne] : r.notes)
            if (end > 0.3 * kRate && ne.valid && ne.midi == 57) { stable = double(end) / kRate - 0.3; break; }
        std::printf("[measure] quality %d: time-to-first-pitch %.1f ms, time-to-stable %.1f ms (algorithmic, sample clock)\n", int(q),
                    firstPitch * 1000, stable * 1000);
        REQUIRE(firstPitch > 0);
        REQUIRE(stable > 0);
        if (q != AudioQuality::HighPrecision) {
            CHECK(firstPitch <= 0.080);   // §4 budget
        }
        if (q == AudioQuality::LowLatency) {
            CHECK(stable <= 0.150);   // §4 budget
        }
    }
}

TEST_CASE("voice pipeline: vibrato never splits a note, noise makes none") {
    Run r = run(render({{-1, 0.2}, {64, 1.2, 35}, {-1, 0.3}}), voice_session(), AudioQuality::Balanced);
    auto notes = ended(r);
    REQUIRE(notes.size() == 1);
    CHECK(std::string(notes[0].writtenName) == "E4");
    CHECK(notes[0].vibrato);

    std::mt19937 rng(7);
    std::normal_distribution<float> noise(0, 0.05f);
    std::vector<float> x(kRate);
    for (auto& v : x) v = noise(rng);
    CHECK(ended(run(x, voice_session(), AudioQuality::Balanced)).empty());
}

TEST_CASE("voice pipeline: chromatic notes spelled by melodic direction") {
    // G major: A3 A#3 B3 | B3 Bb3 A3  (each 0.25 s, legato)
    auto up = run(render({{-1, 0.2}, {57, 0.25}, {58, 0.25}, {59, 0.25}, {-1, 0.3}}), voice_session(), AudioQuality::Balanced);
    CHECK(names(up) == "A3 A#3 B3 ");
    auto down = run(render({{-1, 0.2}, {59, 0.25}, {58, 0.25}, {57, 0.25}, {-1, 0.3}}), voice_session(), AudioQuality::Balanced);
    CHECK(names(down) == "B3 Bb3 A3 ");
    // Repeated pitch after silence = two notes.
    auto rep = run(render({{-1, 0.2}, {62, 0.3}, {-1, 0.2}, {62, 0.3}, {-1, 0.3}}), voice_session(), AudioQuality::Balanced);
    CHECK(names(rep) == "D4 D4 ");
}

TEST_CASE("instrument in Treble 8vb: sounding E2 is written E3") {
    auto r = run(render({{-1, 0.2}, {40, 0.6}, {-1, 0.3}}), voice_session(AnalysisMode::InstrumentMono, 1, Clef::Treble8vb), AudioQuality::Balanced);
    CHECK(names(r) == "E3 ");
}

TEST_CASE("YIN: alternating-period amplitude (decay, breathiness) is not read an octave down") {
    // Every other period 50% quieter: the dip at the double period beats the threshold first.
    const double fs = 16000, f = 220;
    std::vector<float> x(800);
    for (size_t i = 0; i < x.size(); i++) {
        double ph = 2 * kPi * f * double(i) / fs;
        double g = std::fmod(ph / (2 * kPi), 2.0) < 1 ? 0.5 : 1.0;
        x[i] = float(g * (std::sin(ph) + 0.5 * std::sin(2 * ph) + 0.25 * std::sin(3 * ph)));
    }
    Yin plain, guarded;
    plain.init(fs, 800, 70, 1200);
    guarded.init(fs, 800, 70, 1200, 0.15f, 0.25f);
    float c = 0;
    CHECK(plain.estimate(x.data(), c) == Approx(110).epsilon(0.01));   // the failure the guard exists for
    CHECK(guarded.estimate(x.data(), c) == Approx(220).epsilon(0.01));
}

TEST_CASE("voice pipeline: a note after an unvoiced gap above the gate starts at its own onset") {
    // A4, 0.3 s of breath noise (above the -50 dBFS gate, unvoiced), C5.
    auto x = render({{-1, 0.2}, {69, 0.4}, {-1, 0.3}, {72, 0.4}, {-1, 0.3}});
    std::mt19937 rng(3);
    std::normal_distribution<float> noise(0, 0.03f);
    for (size_t i = size_t(0.6 * kRate); i < size_t(0.9 * kRate); i++) x[i] += noise(rng);
    auto notes = ended(run(x, voice_session(), AudioQuality::Balanced));
    REQUIRE(notes.size() == 2);
    CHECK(std::string(notes[1].writtenName) == "C5");
    CHECK(notes[1].startTimeSeconds == Approx(0.9).margin(0.03));
}
