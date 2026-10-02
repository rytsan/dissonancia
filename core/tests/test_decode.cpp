#include <catch2/catch_approx.hpp>
#include <catch2/catch_test_macros.hpp>
#include <cmath>
#include <random>
#include <string>
#include <vector>

#include "decode.hpp"

using namespace dz;
using Catch::Approx;

namespace {
constexpr double kPi = 3.14159265358979323846;
double hz(int m) { return 440 * std::pow(2.0, (m - 69) / 12.0); }

// A band-like take: chords strummed on every beat (decaying, the top strings on the off-beats),
// a louder melody with passing tones changing every half beat, and a snare (noise) on 2 and 4.
std::vector<float> band(const std::vector<std::vector<int>>& chords, double bar, uint32_t rate, std::vector<int> melody) {
    std::mt19937 rng(5);
    std::normal_distribution<float> noise(0, 1);
    const double beat = bar / 4;
    std::vector<float> x(size_t((chords.size() * bar + 0.5) * rate), 0.f);
    for (size_t c = 0; c < chords.size(); c++)
        for (int b = 0; b < 8; b++) {   // eighth strums
            const double t0 = c * bar + b * beat / 2;
            const auto& notes = chords[c];
            for (size_t k = (b % 2 ? notes.size() / 2 : 0); k < notes.size(); k++)
                for (size_t i = 0; i < size_t(beat / 2 * rate * 1.6) && size_t(t0 * rate) + i < x.size(); i++) {
                    const double t = double(i) / rate;
                    double v = 0;
                    for (int h = 1; h <= 4; h++) v += std::sin(2 * kPi * h * hz(notes[k]) * t) / h;
                    x[size_t(t0 * rate) + i] += float(0.06 * (b % 2 ? 0.6 : 1) * std::exp(-t / 0.35) * v);
                }
            if (b == 2 || b == 6)
                for (size_t i = 0; i < size_t(0.12 * rate); i++) x[size_t(t0 * rate) + i] += 0.25f * noise(rng) * float(std::exp(-double(i) / rate / 0.04));
        }
    double ph = 0;   // melody, half-beat notes, phase continuous, over the chords only
    for (size_t i = 0; i < size_t(chords.size() * bar * rate); i++) {
        const double t = double(i) / rate;
        const int m = melody[size_t(t / (beat / 2)) % melody.size()];
        ph += 2 * kPi * hz(m) / rate;
        x[i] += float(0.12 * (std::sin(ph) + 0.4 * std::sin(2 * ph)));
    }
    return x;
}
}  // namespace

TEST_CASE("whole-take decoding: a strummed progression under a melody and a snare, exact chords at the bar lines") {
    // C Am F G, 2 s bars; melody walks scale tones and neighbours across the changes.
    const uint32_t rate = 48000;
    auto x = band({{48, 52, 55, 60, 64}, {45, 52, 57, 60, 64}, {41, 48, 53, 57, 60}, {43, 50, 55, 59, 62}}, 2.0, rate,
                  {72, 74, 76, 77, 76, 74, 72, 71, 69, 71, 72, 74, 72, 71, 69, 67});
    SessionConfig s{};
    s.mode = AnalysisMode::GuitarChords;
    s.quality = AudioQuality::Balanced;
    s.referenceA4 = 440;
    s.keySet = 1;
    s.meter = {4, 4};
    s.bpm = 120;
    std::vector<std::string> got;
    std::vector<double> starts;
    for (const AnalyzerEvent& e : decode_chords(s, x.data(), x.size(), rate, 0.0, true))   // a REC take: on the metronome grid
        if (e.type == AnalyzerEventType::ChordEnded) { got.emplace_back(e.data.chord.symbol); starts.push_back(e.data.chord.startTimeSeconds); }
    std::string seq;
    for (auto& g : got) seq += g + " ";
    INFO("decoded: " << seq);
    REQUIRE(got.size() == 4);
    CHECK(got[0].substr(0, 1) == "C");
    CHECK(got[1].substr(0, 2) == "Am");
    CHECK(got[2].substr(0, 1) == "F");
    CHECK(got[3].substr(0, 1) == "G");
    for (size_t i = 1; i < 4; i++) CHECK(starts[i] == Approx(2.0 * double(i)).margin(0.06));
}

TEST_CASE("whole-take decoding: silence gives no chords; cancel stops it") {
    std::vector<float> x(48000 * 2, 0.f);
    SessionConfig s{};
    s.mode = AnalysisMode::GuitarChords;
    s.quality = AudioQuality::Balanced;
    s.referenceA4 = 440;
    s.meter = {4, 4};
    s.bpm = 120;
    CHECK(decode_chords(s, x.data(), x.size(), 48000, 0.0, false).empty());
    CHECK(decode_chords(s, x.data(), x.size(), 48000, 0.0, false, [](double) { return false; }).empty());
}

TEST_CASE("whole-take notes: deep vibrato and a stray octave frame stay one note; a line keeps its notes") {
    const uint32_t rate = 48000;
    std::vector<float> x;
    double ph = 0;
    auto tone = [&](int midi, double sec, double vibCents, double glitchAt = -1) {
        for (size_t i = 0; i < size_t(sec * rate); i++) {
            double t = double(i) / rate, m = midi + vibCents / 100 * std::sin(2 * kPi * 5.5 * t);
            if (glitchAt >= 0 && t >= glitchAt && t < glitchAt + 0.04) m -= 12;   // 40 ms read an octave down
            ph += 2 * kPi * hz(int(std::lround(m))) * std::pow(2.0, (m - std::lround(m)) / 12) / rate;
            x.push_back(float(0.2 * (std::sin(ph) + 0.5 * std::sin(2 * ph) + 0.25 * std::sin(3 * ph))));
        }
    };
    auto silence = [&](double sec) { x.insert(x.end(), size_t(sec * rate), 0.f); };
    silence(0.3);
    tone(64, 1.2, 45, 0.6);   // E4, +-45 cents vibrato, an octave glitch in the middle
    silence(0.3);
    tone(67, 0.4, 0); tone(69, 0.4, 0); tone(71, 0.4, 0);   // G4 A4 B4 legato
    silence(0.4);
    SessionConfig s{};
    s.mode = AnalysisMode::VoiceMono;
    s.quality = AudioQuality::Balanced;
    s.referenceA4 = 440;
    s.keyFifths = 1;
    s.meter = {4, 4};
    s.bpm = 120;
    std::string names;
    std::vector<double> starts;
    for (const AnalyzerEvent& e : decode_notes(s, x.data(), x.size(), rate, 0.0)) { names += std::string(e.data.note.writtenName) + " "; starts.push_back(e.data.note.startTimeSeconds); }
    INFO("decoded: " << names);
    CHECK(names == "E4 G4 A4 B4 ");
    REQUIRE(starts.size() == 4);
    CHECK(starts[0] == Approx(0.3).margin(0.02));
    CHECK(starts[1] == Approx(1.8).margin(0.02));
    CHECK(starts[2] == Approx(2.2).margin(0.02));
}
