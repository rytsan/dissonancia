#include <catch2/catch_test_macros.hpp>
#include <cmath>
#include <random>
#include <vector>

#include "timing.hpp"

using namespace dz;

namespace {

// Strums: six strings 3 ms apart, each a pick click then a slightly stiff string (upper partials
// sharp and dying sooner), over a noise floor.
// truth: where each strum starts.
std::vector<float> strums(uint32_t rate, const std::vector<double>& at, const std::vector<float>& amp, uint32_t seed) {
    double end = at.back() + 1.5;
    std::vector<float> x(size_t(rate * end), 0.f);
    std::mt19937 rng(seed);
    std::normal_distribution<float> noise(0, 1);
    const double f0[6] = {82.4, 110, 146.8, 196, 246.9, 329.6};
    for (size_t k = 0; k < at.size(); k++)
        for (int s = 0; s < 6; s++) {
            const double t0 = at[k] + 0.003 * s;
            for (size_t i = size_t(t0 * rate); i < x.size() && i < size_t((t0 + 1.2) * rate); i++) {
                const double u = double(i) / rate - t0;
                double v = u < 0.003 ? 0.5 * noise(rng) : 0;   // the pick
                for (int h = 1; h <= 6; h++) v += std::sin(2 * M_PI * f0[s] * h * (1 + 0.0004 * h * h) * u) * std::exp(-u * h / 0.4) / h;
                x[i] += amp[k] * 0.08f * float(std::exp(-u / 0.6) * v);
            }
        }
    for (float& v : x) v += 0.001f * noise(rng);
    return x;
}

std::vector<double> grid(double bpm, int n, double t0 = 0.5) {
    std::vector<double> b;
    for (int i = 0; i < n; i++) b.push_back(t0 + i * 60 / bpm);
    return b;
}

}  // namespace

TEST_CASE("timing: strums dragging 15 ms with a 6 ms spread are measured as such") {
    const auto beats = grid(100, 40);
    std::mt19937 rng(3);
    std::normal_distribution<double> jitter(0, 6);
    std::vector<double> at;
    std::vector<float> amp;
    for (size_t i = 0; i + 1 < beats.size(); i++)
        for (int e = 0; e < 2; e++) {   // eighths, the downbeat louder
            at.push_back(beats[i] + e * 0.3 + (15 + jitter(rng)) / 1000);
            amp.push_back(e ? 0.5f : 1.f);
        }
    auto x = strums(44100, at, amp, 1);
    auto on = detect_onsets(x.data(), x.size(), 44100);
    CHECK(on.size() == at.size());
    auto r = timing_report(on, beats);
    INFO("mean " << r.meanOffsetMs << " spread " << r.spreadMs << " matched " << r.matched << " accent " << r.accentDb[0] << "/" << r.accentDb[2]);
    CHECK(std::abs(r.meanOffsetMs - 15) < 3);
    CHECK(std::abs(r.spreadMs - 6) < 2);
    CHECK(r.matched == int(at.size()));
    CHECK(r.accentDb[0] > r.accentDb[2] + 3);   // the downbeats stand out
    CHECK(r.swing < 1.1);
    CHECK(std::abs(r.bpm - 100) < 0.1);
    CHECK(r.confidence > 0.9);
}

TEST_CASE("timing: swung eighths and a drifting tempo") {
    std::vector<double> beats;
    for (double t = 0.5, bpm = 90; beats.size() < 40; t += 60 / bpm, bpm += 0.25) beats.push_back(t);   // 90 -> ~100 BPM
    std::vector<double> at;
    for (size_t i = 0; i + 1 < beats.size(); i++) {
        at.push_back(beats[i]);
        at.push_back(beats[i] + (beats[i + 1] - beats[i]) * 2 / 3);   // triplet swing
    }
    auto x = strums(44100, at, std::vector<float>(at.size(), 1.f), 2);
    auto r = timing_report(detect_onsets(x.data(), x.size(), 44100), beats);
    INFO("swing " << r.swing << " drift " << r.driftBpm << " mean " << r.meanOffsetMs);
    CHECK(std::abs(r.swing - 2) < 0.15);
    CHECK(std::abs(r.driftBpm - 9.75) < 1);
    CHECK(std::abs(r.meanOffsetMs) < 4);
    CHECK(r.matched > int(at.size()) * 9 / 10);
}

TEST_CASE("timing: silence and no grid give nothing") {
    std::vector<float> z(44100 * 3, 0.f);
    CHECK(detect_onsets(z.data(), z.size(), 44100).empty());
    auto r = timing_report({{1.0, 0.f}}, {});
    CHECK(r.matched == 0);
    CHECK(r.confidence == 0);
}
