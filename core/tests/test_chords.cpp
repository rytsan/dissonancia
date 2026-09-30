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

SessionConfig session(int8_t fifths = 0, bool minor = false, bool keySet = true) {
    SessionConfig s{};
    s.keyMode = minor ? KeyMode::NaturalMinor : KeyMode::Major;
    s.mode = AnalysisMode::GuitarChords;
    s.quality = AudioQuality::Balanced;
    s.referenceA4 = 440;
    s.keySet = keySet;
    s.keyFifths = fifths;
    s.meter = {4, 4};
    s.bpm = 120;
    return s;
}

struct Segment { std::vector<int> midis; double seconds; };

struct Result {
    std::vector<ChordEvent> ended, confirmed;
    std::vector<double> confirmedAt;   // sample-clock time the confirmation was emitted
    std::vector<double> onsets;        // detected onset times
    std::vector<std::pair<double, std::string>> previews;   // (time, preview symbol) per hop
    std::vector<std::pair<double, BassEstimate>> bass;
    ChordTracker::Output last{};
};

// Harmonic tones (4 partials, 1/h), per-note phase continuous; a new segment re-attacks.
// withBass = false: no bass tracker (the pre-M4 behaviour, nothing can resolve identical sets).
// decaySeconds > 0: each segment's notes decay (plucked); notes of a segment may be staggered.
Result run(const std::vector<Segment>& segs, const SessionConfig& s = session(), bool withBass = true, double decaySeconds = 0) {
    ChromaFrontEnd fe(s, 2, kRate, 82.4f, 4200, 12, kHop, withBass ? 70.f : 0.f, withBass ? 500.f : 0.f, 0.210);
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
                        double env = std::min(1.0, t / 0.005) * (decaySeconds > 0 ? std::exp(-t / decaySeconds) : 1.0);
                        block[i] += float(0.08 / h * env * std::sin(2 * kPi * h * hz * t));
                    }
            }
            {
                RtScope rt;   // allocation trap armed
                fe.process(block.data(), kHop, pos + kHop, co);
                tr.process(co.chroma, co.chroma.timestampSeconds, double(pos + kHop) / kRate, co.bass, co.lastOnset, r.last);
            }
            double now = double(pos + kHop) / kRate;
            if (co.onset) r.onsets.push_back(co.lastOnset);
            r.previews.emplace_back(now, r.last.preview.best.symbol);
            r.bass.emplace_back(now, co.bass);
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
            Result r = run({{midis, 1.0}}, session(), false);   // audio alone
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
    std::printf("[measure] chord matcher, audio only (no bass): %d/%d exact, the rest identical pitch-class sets flagged ambiguous\n", exact, total);
}

TEST_CASE("chord ambiguities: C6 = Am7, symmetric, missing third") {
    Result c6 = run({{{48, 52, 55, 57}, 1.0}}, session(0, false, false), false);   // C E G A, no key, no bass: nothing can decide
    std::string why = c6.last.preview.explanation;
    INFO(c6.last.preview.best.symbol << " / " << why);
    CHECK(c6.last.preview.ambiguous);
    CHECK(why.find("bass decides") != std::string::npos);
    CHECK(c6.last.preview.best.confidence <= 0.5f);

    Result aug = run({{{48, 52, 56}, 1.0}}, session(), false);
    CHECK(std::string(aug.last.preview.explanation).find("symmetric") != std::string::npos);

    Result power = run({{{48, 55, 60}, 1.0}}, session(), false);
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

TEST_CASE("context: key mode + cadence decide what the audio leaves open, never override it") {
    auto last = [](const Result& r) { return std::string(r.last.preview.best.symbol); };
    auto why = [](const Result& r) { return std::string(r.last.preview.explanation); };
    const std::vector<int> G = {43, 47, 50, 55}, E = {40, 44, 47, 52}, E7 = {40, 44, 47, 50}, dyadCE = {48, 52, 60}, C6 = {48, 52, 55, 57};

    // C + E only (no G, no A): C major after G = V-I -> C; A minor after E = V-i -> Am.
    // No bass tracker here: the context alone must decide (M4's bass is tested separately).
    Result maj = run({{G, 1.0}, {dyadCE, 1.0}}, session(0, false), false);
    INFO("C major: " << last(maj) << " / " << why(maj));
    CHECK(last(maj) == "C");
    CHECK(why(maj).find("V-I") != std::string::npos);
    Result min = run({{E, 1.0}, {dyadCE, 1.0}}, session(0, true), false);
    INFO("A minor: " << last(min) << " / " << why(min));
    CHECK(last(min) == "Am");
    CHECK(why(min).find("V-i") != std::string::npos);

    // Identical sets: C6 in C major after G7-ish; Am7 in A minor after E7. Still flagged.
    Result c6 = run({{G, 1.0}, {C6, 1.0}}, session(0, false), false);
    CHECK(last(c6) == "C6");
    CHECK(c6.last.preview.ambiguous);
    Result am7 = run({{E7, 1.0}, {C6, 1.0}}, session(0, true), false);
    CHECK(last(am7) == "Am7");
    CHECK(why(am7).find("bass decides") != std::string::npos);

    // A full C triad stays C even in A minor right after E: the audio is clear.
    CHECK(last(run({{E, 1.0}, {{48, 52, 55}, 1.0}}, session(0, true), false)) == "C");
    // And a full A minor triad stays Am in C major after G.
    CHECK(last(run({{G, 1.0}, {{45, 48, 52}, 1.0}}, session(0, false), false)) == "Am");
}

TEST_CASE("bass: settled bass resolves identical sets and gives inversions spelled as chord tones") {
    auto last = [](const Result& r) { return std::string(r.last.preview.best.symbol); };
    // Same notes C E G A: A in the bass -> Am7; C in the bass -> C6. Resolved, not capped.
    Result am7 = run({{{45, 48, 52, 55}, 1.0}});
    CHECK(last(am7) == "Am7");
    CHECK_FALSE(am7.last.preview.ambiguous);
    CHECK(am7.last.preview.best.confidence > 0.5f);
    CHECK(std::string(am7.last.preview.explanation).find("bass decides") != std::string::npos);
    CHECK(last(run({{{48, 52, 55, 57}, 1.0}})) == "C6");

    // Inversions (C major key): chord tones spelled from the root letter.
    Result ce = run({{{52, 55, 60}, 1.0}});   // E3 G3 C4
    CHECK(last(ce) == "C/E");
    REQUIRE(ce.ended.size() == 1);
    CHECK(ce.ended[0].inversion == 1);
    CHECK(ce.ended[0].bassPitchClass == 4);
    CHECK(ce.ended[0].bassSettled);
    CHECK(last(run({{{44, 47, 52}, 1.0}})) == "E/G#");   // not E/Ab
    CHECK(last(run({{{42, 45, 50}, 1.0}})) == "D/F#");
    CHECK(last(run({{{43, 48, 52}, 1.0}})) == "C/G");     // second inversion
}

TEST_CASE("bass: never settled before T_low after the onset; preview first") {
    Result r = run({{{}, 0.3}, {{43, 47, 50, 55}, 1.0}});   // G major from 0.3 s
    double firstValid = -1, firstSettled = -1;
    for (auto& [t, b] : r.bass) {
        if (t < 0.3) continue;
        if (b.valid && firstValid < 0) firstValid = t;
        if (b.settled && firstSettled < 0) firstSettled = t;
    }
    std::printf("[measure] bass: first estimate %.0f ms, settled %.0f ms after the onset (T_low 210 ms)\n", (firstValid - 0.3) * 1000,
                (firstSettled - 0.3) * 1000);
    REQUIRE(firstSettled > 0);
    CHECK(firstSettled - 0.3 >= 0.210 - 0.021);
    CHECK(firstSettled - 0.3 <= 0.35);
    CHECK(firstValid <= firstSettled);
    CHECK(r.bass.back().second.midi == 43);
    CHECK(std::string(r.bass.back().second.writtenName) == "G2");
}

TEST_CASE("onsets: one per chord change, none while sustained; low bins gated after an attack") {
    // G (G2 bass) -> C (C3 bass) -> Am (A2 bass), 0.6 s each, abrupt changes.
    Result r = run({{{43, 47, 50, 55}, 0.6}, {{48, 52, 55, 60}, 0.6}, {{45, 48, 52, 57}, 0.6}});
    REQUIRE(r.onsets.size() == 3);
    for (size_t i = 0; i < 3; i++) CHECK(r.onsets[i] == Approx(0.6 * double(i)).margin(0.025));
    // Onset gating: after the change to C, the old G2 in the long low windows must never show as bass.
    for (auto& [t, sym] : r.previews)
        if (t > 0.6 && t < 1.2) CHECK(sym.find("/G") == std::string::npos);
    REQUIRE(r.ended.size() == 3);
    CHECK(std::string(r.ended[1].symbol) == "C");
    CHECK(r.ended[1].startTimeSeconds == Approx(0.6).margin(0.03));   // backdated to the attack
}

TEST_CASE("arpeggio: sequential notes accumulate into the chord") {
    // C3 E3 G3 C4 plucked one at a time, 0.2 s apart (16ths at 75 bpm), twice. Faster than the
    // lowest window (133 ms at C3) a low note cannot be identified by the CQT at all (§4 physics).
    std::vector<Segment> segs;
    for (int rep = 0; rep < 2; rep++)
        for (int m : {48, 52, 55, 60}) segs.push_back({{m}, 0.2});
    Result r = run(segs, session(), true, 0.4);
    REQUIRE_FALSE(r.confirmed.empty());
    CHECK(std::string(r.confirmed[0].symbol) == "C");
    CHECK(r.confirmed[0].arpeggiated);
}
