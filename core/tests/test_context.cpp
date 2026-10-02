#include <catch2/catch_approx.hpp>
#include <catch2/catch_test_macros.hpp>
#include <cmath>
#include <algorithm>
#include <cstring>
#include <random>
#include <string>
#include <vector>

#include "context.hpp"
#include "rt.hpp"

using namespace dz;
using Catch::Approx;

namespace {

// Onset envelope of a played pulse: one decaying spike per beat, accent on beat 1, some jitter
// and a few missing or extra attacks, like strumming.
ContextEstimate pulse(double bpm, double hop, double seconds, uint32_t seed, float offbeat = 0) {
    ContextTracker c(hop);
    ContextEstimate e{};
    std::mt19937 rng(seed);
    std::normal_distribution<double> jitter(0, 0.012);
    std::uniform_real_distribution<double> u(0, 1);
    const double beat = offbeat > 0 ? 30 / bpm : 60 / bpm;   // offbeat: eighth-note strumming
    double next = 0.3;   // steady tempo, each attack +-12 ms off the grid
    int k = 0;
    for (double t = 0; t < seconds; t += hop) {
        float s = 0.02f * float(u(rng));
        if (t >= next) {
            if (u(rng) > 0.1) s += offbeat > 0 && k % 2 ? offbeat : k % (offbeat > 0 ? 8 : 4) == 0 ? 1.f : 0.6f;   // 10 % missing
            k++;
            next = 0.3 + k * beat + jitter(rng);
        }
        if (u(rng) < 0.01) s += 0.5f;   // stray attack
        RtScope rt;
        c.process(nullptr, s, e);
    }
    return e;
}

ContextEstimate melody(const std::vector<int>& pcs, double hop, double noteSeconds, int repeats) {
    ContextTracker c(hop);
    ContextEstimate e{};
    for (int r = 0; r < repeats; r++)
        for (int pc : pcs)
            for (double t = 0; t < noteSeconds; t += hop) {
                float w[12]{};
                w[pc] = 1;
                c.process(w, 0, e);
            }
    return e;
}

}  // namespace

TEST_CASE("tempo from the onset envelope: pulse within 2 BPM, after ~4 s") {
    for (double bpm : {72.0, 92.0, 120.0, 150.0})
        for (double hop : {0.01, 0.02}) {
            auto e = pulse(bpm, hop, 10, uint32_t(bpm));
            INFO("bpm " << bpm << " hop " << hop << " -> " << e.bpm);
            CHECK(e.bpm == Approx(bpm).margin(2));
            CHECK(e.tempoConfidence > 0.3f);
        }
    CHECK(pulse(100, 0.02, 3, 1).bpm == 0);   // not enough input yet
    // Eighth-note strumming at 92 (weaker off-beats): the beat, not the eighths.
    CHECK(pulse(92, 0.02, 10, 5, 0.5f).bpm == Approx(92).margin(2));
}

TEST_CASE("key from a pitch-class histogram (Krumhansl-Kessler)") {
    // "Ode to Joy" in G major: B B C D D C B A G G A B B A A.
    auto g = melody({11, 11, 0, 2, 2, 0, 11, 9, 7, 7, 9, 11, 11, 9, 9}, 0.01, 0.3, 3);
    CHECK(g.keyValid);
    CHECK(g.keyFifths == 1);
    CHECK(g.keyMode == KeyMode::Major);
    // A minor arpeggio and scale with the tonic held: A C E A G F E D C B A.
    auto a = melody({9, 0, 4, 9, 9, 7, 5, 4, 2, 0, 11, 9, 9}, 0.02, 0.3, 3);
    CHECK(a.keyValid);
    CHECK(a.keyFifths == 0);
    CHECK(a.keyMode == KeyMode::NaturalMinor);
    // Bb major scale: 2 flats.
    auto bb = melody({10, 0, 2, 3, 5, 7, 9, 10, 5, 10}, 0.02, 0.3, 3);
    CHECK(bb.keyFifths == -2);
    CHECK(bb.keyMode == KeyMode::Major);
    CHECK_FALSE(melody({7, 7}, 0.02, 0.3, 1).keyValid);   // 1.2 s: not enough yet
}

namespace {

int pc_of(const std::string& s, size_t& len) {
    static const std::pair<const char*, int> table[] = {{"C#", 1}, {"Db", 1}, {"D#", 3}, {"Eb", 3}, {"F#", 6}, {"Gb", 6}, {"G#", 8}, {"Ab", 8}, {"A#", 10},
                                                        {"Bb", 10}, {"Cb", 11}, {"C", 0}, {"D", 2}, {"E", 4}, {"F", 5}, {"G", 7}, {"A", 9}, {"B", 11}};
    for (auto& [n, pc] : table)
        if (s.rfind(n, 0) == 0) { len = std::strlen(n); return pc; }
    len = 0;
    return 0;
}

// "n:F#" = one sung note; otherwise a chord symbol as chroma: chord tones, a fifth-up leak
// (3rd harmonic), the bass.
std::vector<float> weights(const std::string& item) {
    std::vector<float> w(12, 0.f);
    size_t len = 0;
    if (item.rfind("n:", 0) == 0) { w[size_t(pc_of(item.substr(2), len))] = 1; return w; }
    std::string s = item, bass;
    if (auto slash = s.find('/'); slash != std::string::npos) { bass = s.substr(slash + 1); s = s.substr(0, slash); }
    int root = pc_of(s, len);
    std::string q = s.substr(len);
    static const std::pair<const char*, std::vector<int>> qualities[] = {
        {"", {0, 4, 7}}, {"m", {0, 3, 7}}, {"7", {0, 4, 7, 10}}, {"m7", {0, 3, 7, 10}}, {"maj7", {0, 4, 7, 11}}, {"dim", {0, 3, 6}},
        {"dim7", {0, 3, 6, 9}}, {"m7b5", {0, 3, 6, 10}}, {"aug", {0, 4, 8}}};
    for (auto& [name, iv] : qualities)
        if (q == name)
            for (int i : iv) { w[size_t((root + i) % 12)] += 1; w[size_t((root + i + 7) % 12)] += 0.25f; }
    w[size_t(bass.empty() ? root : pc_of(bass, len))] += 0.6f;
    return w;
}

// What the app applies (Program.FollowContext): a key held 3 s at confidence >= 0.7.
struct Applied { std::vector<std::pair<int, KeyMode>> keys; };

Applied play(const std::vector<std::string>& items, int repeats, double chordSeconds = 2.0, double hop = 0.02) {
    ContextTracker c(hop);
    ContextEstimate e{};
    Applied a;
    int candF = 99;
    KeyMode candM = KeyMode::Major;
    double since = 0, t = 0;
    for (int r = 0; r < repeats; r++)
        for (const auto& item : items) {
            const auto w = weights(item);
            const bool isNote = item.rfind("n:", 0) == 0;
            for (double d = 0; d < (isNote ? 0.5 : chordSeconds); d += hop, t += hop) {
                c.process(w.data(), 0, e);
                if (!e.keyValid || e.keyConfidence < 0.7f) { candF = 99; continue; }
                if (e.keyFifths != candF || e.keyMode != candM) { candF = e.keyFifths; candM = e.keyMode; since = t; }
                else if (t - since >= 3 && (a.keys.empty() || a.keys.back() != std::pair{candF, candM})) a.keys.emplace_back(candF, candM);
            }
        }
    return a;
}

}  // namespace

TEST_CASE("key: stress set - chromatic harmony never applies a wrong signature") {
    struct Case { const char* what; std::vector<std::string> items; int fifths; };   // 99 = ambiguous: must not apply
    const std::vector<Case> cases = {
        {"C major, V/V and a passing dim7", {"C", "C#dim7", "Dm", "G7", "C", "A7", "Dm", "D7", "G7", "C"}, 0},
        {"A harmonic minor, E7 and G#dim7", {"Am", "Dm", "E7", "Am", "G#dim7", "Am", "F", "E7"}, 0},
        {"G major, borrowed iv and bVII", {"G", "C", "Cm", "G", "G", "F", "C", "G", "D7", "G"}, 1},
        {"D minor, Neapolitan and A7", {"Dm", "Gm", "A7", "Dm", "Bb", "Eb/G", "A7", "Dm"}, -1},
        {"Bb ii-V-I, tritone sub, dim7", {"Cm7", "F7", "Bbmaj7", "Bbmaj7", "Cm7", "B7", "Bbmaj7", "Bdim7", "Cm7", "F7", "Bbmaj7", "Gm7"}, -2},
        {"E major, V/vi", {"E", "B", "C#m", "A", "E", "B", "G#7", "C#m", "A", "B7", "E"}, 4},
        {"A minor line cliche", {"Am", "Am/G#", "Am/G", "Am/F#", "F", "E7", "Am"}, 0},
        {"F# minor, V7 and bII", {"F#m", "Bm", "C#7", "F#m", "G", "C#7", "F#m"}, 3},
        {"Eb major, viio7/V and bVI", {"Eb", "Ab", "Adim7", "Eb/Bb", "Bb7", "Eb", "Cb", "Bb7", "Eb"}, -3},
        {"F major melody, chromatic neighbours",
         {"n:F", "n:G", "n:A", "n:Bb", "n:B", "n:C", "n:D", "n:C", "n:Bb", "n:A", "n:Ab", "n:A", "n:F#", "n:G", "n:C", "n:F", "n:E", "n:F", "n:C#", "n:D", "n:Bb", "n:G", "n:E", "n:F"}, -1},
        {"E minor melody, melodic minor", {"n:E", "n:F#", "n:G", "n:A", "n:B", "n:C#", "n:Eb", "n:E", "n:D", "n:C", "n:B", "n:A", "n:G", "n:F#", "n:E", "n:B", "n:G", "n:E"}, 1},
        {"blues in A (A7 D7 E7)", {"A7", "D7", "A7", "A7", "D7", "D7", "A7", "A7", "E7", "D7", "A7", "E7"}, 99},
        {"D dorian vamp", {"Dm7", "G7", "Dm7", "G7"}, 99},
        {"chromatic scale", {"n:C", "n:C#", "n:D", "n:Eb", "n:E", "n:F", "n:F#", "n:G", "n:Ab", "n:A", "n:Bb", "n:B"}, 99},
        {"augmented chords", {"Caug", "Daug"}, 99},
        {"dim7 cycle", {"Cdim7", "C#dim7", "Ddim7"}, 99},
    };
    for (const auto& c : cases) {
        auto a = play(c.items, c.items[0].rfind("n:", 0) == 0 ? 8 : 4);
        INFO(c.what << ": " << a.keys.size() << " applied, first " << (a.keys.empty() ? 99 : a.keys.front().first));
        for (auto& [f, m] : a.keys) CHECK((c.fifths == 99 ? false : f == c.fifths));   // never a wrong signature
        if (c.fifths != 99) CHECK_FALSE(a.keys.empty());
    }
}

TEST_CASE("key: a 3 s pause starts a new song (C major, then G major)") {
    ContextTracker c(0.02);
    ContextEstimate e{};
    auto feed = [&](const std::vector<std::string>& items, int reps) {
        for (int r = 0; r < reps; r++)
            for (auto& s : items) { auto w = weights(s); for (int i = 0; i < 100; i++) c.process(w.data(), 0, e); }
    };
    feed({"C", "Am", "F", "G7"}, 4);
    CHECK(e.keyFifths == 0);
    for (int i = 0; i < 200; i++) c.process(nullptr, 0, e);   // 4 s of silence
    feed({"G", "Em", "C", "D7"}, 1);                          // 8 s of the new song
    CHECK(e.keyFifths == 1);
    CHECK(e.keyMode == KeyMode::Major);
}
