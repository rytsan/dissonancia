#include <catch2/catch_approx.hpp>
#include <catch2/catch_test_macros.hpp>
#include <cmath>
#include <cstring>
#include <string>
#include <vector>

#include "chords.hpp"
#include "cqt.hpp"
#include "rt.hpp"

using namespace dz;
using Catch::Approx;

namespace {

constexpr double kPi = 3.14159265358979323846;
constexpr uint32_t kRate = 48000, kHop = 960;   // 20 ms at 48 kHz

SessionConfig session(int8_t fifths = 0) {
    SessionConfig s{};
    s.mode = AnalysisMode::GuitarChords;
    s.quality = AudioQuality::Balanced;
    s.referenceA4 = 440;
    s.keySet = 1;
    s.keyFifths = fifths;
    s.meter = {4, 4};
    s.bpm = 120;
    return s;
}

struct Segment { std::vector<int> midis; double seconds; };

struct Result {
    std::vector<ChordEvent> ended, confirmed;
    std::vector<double> confirmedAt;   // sample-clock time the confirmation was emitted
    ChordTracker::Output last{};
};

// Harmonic tones (4 partials, 1/h), per-note phase continuous; a new segment re-attacks.
Result run(const std::vector<Segment>& segs, const SessionConfig& s = session()) {
    ChromaFrontEnd fe(s, 2, kRate, 82.4f, 4200, 12, kHop);
    ChordTracker tr(s, 0.02, 0.0);
    ChromaFrontEnd::Output co{};
    Result r;
    std::vector<float> block(kHop);
    uint64_t pos = 0;
    for (const auto& seg : segs) {
        uint64_t end = pos + uint64_t(seg.seconds * kRate), segStart = pos;
        for (; pos < end; pos += kHop) {
            std::fill(block.begin(), block.end(), 0.f);
            for (int m : seg.midis) {
                double hz = 440 * std::pow(2.0, (m - 69) / 12.0);
                for (int h = 1; h <= 4; h++)
                    for (uint32_t i = 0; i < kHop; i++) {
                        double t = double(pos + i - segStart) / kRate;
                        block[i] += float(0.08 / h * std::min(1.0, t / 0.005) * std::sin(2 * kPi * h * hz * t));
                    }
            }
            {
                RtScope rt;   // allocation trap armed
                fe.process(block.data(), kHop, pos + kHop, co);
                tr.process(co.chroma, co.chroma.timestampSeconds, double(pos + kHop) / kRate, r.last);
            }
            for (uint32_t i = 0; i < r.last.eventCount; i++) {
                const AnalyzerEvent& e = r.last.events[i];
                if (e.type == AnalyzerEventType::ChordEnded) r.ended.push_back(e.data.chord);
                if (e.type == AnalyzerEventType::ChordConfirmed) { r.confirmed.push_back(e.data.chord); r.confirmedAt.push_back(double(pos + kHop) / kRate); }
            }
        }
    }
    ChordTracker::Output out{};
    tr.flush(out);
    for (uint32_t i = 0; i < out.eventCount; i++)
        if (out.events[i].type == AnalyzerEventType::ChordEnded) r.ended.push_back(out.events[i].data.chord);
    return r;
}

struct Q { ChordQuality q; std::vector<int> iv; };
const std::vector<Q> kAll = {
    {ChordQuality::Major, {0, 4, 7}},       {ChordQuality::Minor, {0, 3, 7}},        {ChordQuality::Diminished, {0, 3, 6}},
    {ChordQuality::Augmented, {0, 4, 8}},   {ChordQuality::Sus2, {0, 2, 7}},         {ChordQuality::Sus4, {0, 5, 7}},
    {ChordQuality::Power, {0, 7, 12}},      {ChordQuality::Dom7, {0, 4, 7, 10}},     {ChordQuality::Maj7, {0, 4, 7, 11}},
    {ChordQuality::Min7, {0, 3, 7, 10}},    {ChordQuality::HalfDim7, {0, 3, 6, 10}}, {ChordQuality::Dim7, {0, 3, 6, 9}},
    {ChordQuality::Maj6, {0, 4, 7, 9}},     {ChordQuality::Min6, {0, 3, 7, 9}},      {ChordQuality::Add9, {0, 4, 7, 14}},
};

uint16_t mask_of(const ChordCandidate& c) {
    uint16_t m = 0;
    for (int i = 0; i < c.expectedCount; i++) m |= uint16_t(1u << c.expected[i]);
    return m;
}

}  // namespace

TEST_CASE("chord matcher: 15 qualities x 4 roots, identical sets flagged, never forced") {
    ChordMatcher names(session());
    int exact = 0, total = 0;
    for (int root : {0, 2, 7, 10})
        for (const auto& [q, iv] : kAll) {
            std::vector<int> midis;
            for (int i : iv) midis.push_back(48 + root + i);
            Result r = run({{midis, 1.0}});
            const auto& p = r.last.preview;
            char want[16];
            names.symbol(root, q, want);
            uint16_t wantMask = 0;
            for (int i : iv) wantMask |= uint16_t(1u << ((root + i) % 12));
            INFO("expected " << want << " got " << p.best.symbol << " (" << p.explanation << ")");
            total++;
            if (std::string(p.best.symbol) == want) { exact++; continue; }
            // Only an identical pitch-class set may win instead, and it must be flagged.
            CHECK(mask_of(p.best) == wantMask);
            CHECK(p.ambiguous);
        }
    std::printf("[measure] chord matcher: %d/%d exact, the rest identical pitch-class sets flagged ambiguous\n", exact, total);
}

TEST_CASE("chord ambiguities: C6 = Am7, symmetric, missing third") {
    Result c6 = run({{{48, 52, 55, 57}, 1.0}});   // C E G A
    std::string why = c6.last.preview.explanation;
    INFO(c6.last.preview.best.symbol << " / " << why);
    CHECK(c6.last.preview.ambiguous);
    CHECK(why.find("bass decides") != std::string::npos);
    CHECK(c6.last.preview.best.confidence <= 0.5f);

    Result aug = run({{{48, 52, 56}, 1.0}});
    CHECK(std::string(aug.last.preview.explanation).find("symmetric") != std::string::npos);

    Result power = run({{{48, 55, 60}, 1.0}});
    CHECK(std::string(power.last.preview.best.symbol) == "C5");
    CHECK(std::string(power.last.preview.explanation).find("no third") != std::string::npos);
}

TEST_CASE("chord symbols follow the key: Bb not A#, F#m in D, F#dim stays sharp") {
    CHECK(std::string(run({{{46, 50, 53}, 0.8}}, session(0)).last.preview.best.symbol) == "Bb");   // bVII in C
    CHECK(std::string(run({{{51, 55, 58}, 0.8}}, session(0)).last.preview.best.symbol) == "Eb");   // bIII in C
    CHECK(std::string(run({{{54, 57, 60}, 0.8}}, session(0)).last.preview.best.symbol) == "F#dim");   // vii of V
    CHECK(std::string(run({{{54, 57, 61}, 0.8}}, session(2)).last.preview.best.symbol) == "F#m");  // iii in D
    CHECK(std::string(run({{{46, 50, 53}, 0.8}}, session(-1)).last.preview.best.symbol) == "Bb");  // IV in F
}

TEST_CASE("chord tracker: passing tone ignored, backdated onsets, no overlap, silence closes") {
    // C 1.0 s | C + passing D 0.15 s | G 1.0 s | silence 0.5 s
    Result r = run({{{48, 52, 55}, 1.0}, {{48, 50, 52, 55}, 0.15}, {{43, 47, 50, 55}, 1.0}, {{}, 0.5}});
    REQUIRE(r.ended.size() == 2);
    CHECK(std::string(r.ended[0].symbol) == "C");
    CHECK(std::string(r.ended[1].symbol) == "G");
    CHECK(r.ended[0].startTimeSeconds == Approx(0).margin(0.06));
    CHECK(r.ended[1].startTimeSeconds == Approx(1.15).margin(0.12));
    CHECK(r.ended[0].endTimeSeconds == Approx(r.ended[1].startTimeSeconds));   // no overlap, no gap
    CHECK(r.ended[1].endTimeSeconds == Approx(2.15).margin(0.15));
    REQUIRE(r.confirmed.size() == 2);
    double confirmDelay = r.confirmedAt[1] - r.confirmed[1].startTimeSeconds;
    std::printf("[measure] chord confirmation %.0f ms after the backdated onset (documented 0.4-1 s)\n", confirmDelay * 1000);
    CHECK((confirmDelay >= 0.4 && confirmDelay <= 1.0));
}
