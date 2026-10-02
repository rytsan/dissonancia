#include <catch2/catch_approx.hpp>
#include <catch2/catch_test_macros.hpp>
#include <cmath>
#include <random>
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
