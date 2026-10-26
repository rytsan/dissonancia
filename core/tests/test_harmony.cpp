#include <catch2/catch_test_macros.hpp>
#include <cmath>
#include <string>
#include <vector>

#include "harmony.hpp"
#include "timing.hpp"

using namespace dz;

namespace {
// Strummed chords at 120 BPM, one strum per beat (strings 4 ms apart, partials decaying), 4/4.
struct Bar { std::vector<int> notes; double lateMs; bool liftBefore; };   // per chord, two bars each
std::vector<float> strum_take(uint32_t rate, const std::vector<Bar>& chords, std::vector<double>& beats) {
    const double beat = 0.5;
    std::vector<float> x(size_t((chords.size() * 8 + 2) * beat * rate), 0.f);
    for (size_t c = 0; c < chords.size(); c++)
        for (int b = 0; b < 8; b++) {
            if (b == 7 && c + 1 < chords.size() && chords[c + 1].liftBefore) continue;   // the hand off before the change
            const double t0 = (c * 8 + b) * beat + (b == 0 ? chords[c].lateMs / 1000 : 0);
            for (size_t s = 0; s < chords[c].notes.size(); s++) {
                const double f0 = 440 * std::pow(2.0, (chords[c].notes[s] - 69) / 12.0), start = t0 + 0.004 * double(s);
                // A strum rings until the next one; before a lifted beat it is damped early.
                const bool damp = b == 6 && c + 1 < chords.size() && chords[c + 1].liftBefore;
                const double dur = damp ? 0.3 : beat;
                for (size_t i = size_t(start * rate); i < size_t((start + dur) * rate) && i < x.size(); i++) {
                    const double u = double(i) / rate - start, fade = std::min(1.0, (start + dur - double(i) / rate) / 0.01);
                    double v = 0;
                    for (int h = 1; h <= 5; h++) v += std::sin(2 * M_PI * f0 * h * u) * std::exp(-u * h / 0.5) / h;
                    x[i] += float(0.05 * fade * v);
                }
            }
        }
    for (size_t i = 0; i <= chords.size() * 8; i++) beats.push_back(i * beat);
    return x;
}
}  // namespace

TEST_CASE("harmony: a late change, a gap before a change, clean chords") {
    const uint32_t rate = 44100;
    std::vector<double> beats;
    auto x = strum_take(rate, {{{48, 52, 55, 60, 64}, 0, false},          // C
                               {{43, 47, 50, 55, 59, 67}, 30, false},     // G, 30 ms late
                               {{45, 52, 57, 60, 64}, 0, true},           // Am, the hand lifted the beat before
                               {{41, 48, 53, 57, 60, 65}, 0, false}},     // F
                        beats);
    SessionConfig s{};
    s.mode = AnalysisMode::GuitarChords;
    s.quality = AudioQuality::Balanced;
    s.referenceA4 = 440;
    s.meter = {4, 4};
    s.bpm = 120;
    std::vector<HeardFrame> heard;
    const auto chords = decode_chords(s, x.data(), x.size(), rate, 0.0, false, {}, &heard);
    std::vector<double> attacks;
    for (const Onset& o : detect_onsets(x.data(), x.size(), rate)) attacks.push_back(o.time);
    const HarmonyReport r = harmony_report(chords, heard, x.data(), x.size(), rate, 0.0, attacks, beats, 4);
    std::string log;
    for (const ChordPlayed& c : r.chords) log += "chord " + std::to_string(c.start) + " " + c.symbol + " clean " + std::to_string(c.cleanPct) + "\n";
    for (const ChordChange& c : r.changes)
        log += "change " + std::to_string(c.time) + " bar " + std::to_string(c.bar) + " offset " + std::to_string(c.offsetMs) + " gap " + std::to_string(c.gapDb) + "\n";
    INFO(log);
    REQUIRE(r.changes.size() == 3);
    // Attacks read 10-20 ms into this 20 ms six-string strum (GuitarSet: -8 ms against its annotated onsets).
    CHECK(r.changes[0].bar == 3);
    CHECK(r.changes[1].bar == 5);
    CHECK(r.changes[2].bar == 7);
    CHECK(r.changes[0].beatInBar == 1);
    CHECK(std::abs(r.changes[0].offsetMs - r.changes[2].offsetMs - 30) < 10);
    CHECK(std::abs(r.changes[1].offsetMs - r.changes[2].offsetMs) < 20);   // out of silence, read earlier in the strum
    CHECK(std::abs(r.changes[2].offsetMs) < 25);
    CHECK(r.changes[1].gapDb < -10);
    CHECK(r.changes[0].gapDb > -6);
    CHECK(r.changes[2].gapDb > -6);
    CHECK(r.gaps == 1);
    CHECK(r.cleanPct > 85);
}
