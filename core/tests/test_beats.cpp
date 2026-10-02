#include <catch2/catch_test_macros.hpp>
#include <cmath>
#include <random>
#include <vector>

#include "beats.hpp"

using namespace dz;

namespace {

// A drum take: kick on every beat, hi-hat eighths, noise floor; tempo from a function of time.
std::vector<float> drums(uint32_t rate, double seconds, double (*bpmAt)(double), std::vector<double>& truth, uint32_t seed) {
    std::vector<float> x(size_t(rate * seconds), 0.f);
    std::mt19937 rng(seed);
    std::normal_distribution<float> noise(0, 1);
    auto hit = [&](double t, float amp, double decay, bool tonal) {
        for (size_t i = size_t(t * rate); i < x.size() && i < size_t((t + 0.25) * rate); i++) {
            double u = double(i) / rate - t;
            x[i] += amp * float(std::exp(-u / decay)) * (tonal ? float(std::sin(2 * M_PI * 60 * u)) : noise(rng));
        }
    };
    for (double t = 0.5; t < seconds - 0.3;) {
        truth.push_back(t);
        hit(t, 0.8f, 0.06, true);
        double beat = 60 / bpmAt(t);
        hit(t + beat / 2, 0.15f, 0.02, false);
        t += beat;
    }
    for (float& v : x) v += 0.002f * noise(rng);
    return x;
}

double fmeasure(const std::vector<double>& est, const std::vector<double>& ref, double tol = 0.05) {
    size_t hit = 0;
    std::vector<bool> used(ref.size());
    for (double e : est)
        for (size_t i = 0; i < ref.size(); i++)
            if (!used[i] && std::abs(e - ref[i]) <= tol) { used[i] = true; hit++; break; }
    double p = est.empty() ? 0 : double(hit) / est.size(), r = double(hit) / ref.size();
    return p + r > 0 ? 2 * p * r / (p + r) : 0;
}

}  // namespace

TEST_CASE("beats: steady drums, no hint") {
    std::vector<double> truth;
    auto x = drums(44100, 30, [](double) { return 96.0; }, truth, 1);
    auto b = track_beats(x.data(), x.size(), 44100, 0);
    CHECK(std::abs(b.bpm - 96) < 1.5);
    CHECK(fmeasure(b.beats, truth) > 0.95);
}

TEST_CASE("beats: a drummer drifting from the metronome (92 -> 100 BPM) is followed") {
    std::vector<double> truth;
    auto x = drums(44100, 40, [](double t) { return 92 + 8 * t / 40; }, truth, 2);
    auto b = track_beats(x.data(), x.size(), 44100, 92);
    CHECK(fmeasure(b.beats, truth) > 0.95);
    // The last beats are on the drummer, not on the 92 BPM grid.
    double lastTruth = truth.back(), lastGrid = 0.5 + std::floor((lastTruth - 0.5) / (60 / 92.0) + 0.5) * 60 / 92.0;
    INFO("bpm " << b.bpm << " n " << b.beats.size() << "/" << truth.size() << " first " << b.beats.front() << " last " << b.beats.back() << " truth " << lastTruth);
    CHECK(std::abs(b.beats.back() - lastTruth) < 0.03);
    CHECK(std::abs(lastTruth - lastGrid) > 0.1);
}

TEST_CASE("beats: silence and short input give nothing") {
    std::vector<float> z(44100 * 2, 0.f);
    CHECK(track_beats(z.data(), z.size(), 44100, 0).beats.empty());
}
