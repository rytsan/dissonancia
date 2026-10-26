#include <catch2/catch_test_macros.hpp>
#include <cmath>
#include <random>
#include <vector>

#include "dynamics.hpp"
#include "timing.hpp"

using namespace dz;

namespace {
constexpr uint32_t kRate = 48000;
// A 1 kHz tone, its level (dBFS peak) given per second by db(t).
template <class F> void tone(std::vector<float>& x, double sec, F db) {
    for (size_t i = 0; i < size_t(sec * kRate); i++) {
        const double t = double(i) / kRate;
        x.push_back(float(std::pow(10.0, db(t) / 20) * std::sin(2 * M_PI * 1000 * t)));
    }
}
}  // namespace

TEST_CASE("dynamics: loudness of a steady tone, a crescendo per bar") {
    std::vector<float> x;
    tone(x, 16, [](double t) { return -20.0 + std::floor(t / 2); });   // 8 bars of 2 s, 1 dB louder each
    const DynamicsReport r = dynamics_report(x.data(), x.size(), kRate, 2.0);
    INFO("integrated " << r.integratedLufs << " trend " << r.trendLu);
    REQUIRE(r.barLufs.size() == 8);
    // A 0 dBFS 1 kHz sine reads -3.01 LUFS in mono (K-weighting ~ +0.69 dB there, -0.691 offset).
    CHECK(std::abs(r.barLufs[0] - (-20 - 3.01f)) < 0.3);
    CHECK(std::abs(r.barLufs[7] - r.barLufs[0] - 7) < 0.2);
    CHECK(std::abs(r.trendLu - 7) < 0.2);
    REQUIRE(r.phrases.size() == 1);
    CHECK(r.phrases[0].changeLu > 4);
    CHECK(r.phrases[0].peakAt > 0.85f);
}

TEST_CASE("dynamics: phrases, their shape, and silence between") {
    std::vector<float> x;
    tone(x, 3, [](double t) { return -30 + 4 * t; });   // swell
    x.insert(x.end(), kRate, 0.f);
    tone(x, 3, [](double t) { return -18 - 4 * t; });   // fade
    const DynamicsReport r = dynamics_report(x.data(), x.size(), kRate, 0);
    REQUIRE(r.phrases.size() == 2);
    CHECK(std::abs(r.phrases[0].start - 0) < 0.15);
    CHECK(std::abs(r.phrases[1].start - 4) < 0.15);
    CHECK(r.phrases[0].changeLu > 6);
    CHECK(r.phrases[1].changeLu < -6);
    CHECK(r.phrases[0].peakAt > 0.85f);
    CHECK(r.phrases[1].peakAt < 0.15f);
    CHECK(r.barLufs.empty());
}

TEST_CASE("dynamics: attacks even or uneven within a sixteenth") {
    auto take = [](double jitterDb) {
        std::vector<float> x(size_t(17 * kRate), 0.f);
        std::mt19937 rng(5);
        std::normal_distribution<double> g(0, 1);
        std::vector<double> beats;
        for (int b = 0; b <= 32; b++) beats.push_back(0.5 * b);
        for (int b = 0; b < 32; b++) {   // a decaying 220 Hz pluck per beat, downbeats 6 dB up
            const double amp = std::pow(10.0, ((b % 4 ? -6 : 0) + jitterDb * g(rng)) / 20) * 0.3;
            const size_t at = size_t(beats[size_t(b)] * kRate);
            for (size_t i = 0; i < size_t(0.4 * kRate); i++)
                x[at + i] += float(amp * std::exp(-double(i) / kRate / 0.1) * std::min(1.0, double(size_t(0.4 * kRate) - i) / (0.05 * kRate)) * std::sin(2 * M_PI * 220 * double(i) / kRate));
        }
        const auto on = detect_onsets(x.data(), x.size(), kRate);
        return timing_report(on, beats, 4);
    };
    const TimingReport even = take(0), uneven = take(3);
    INFO("even " << even.levelSpreadAllDb << " uneven " << uneven.levelSpreadAllDb);
    CHECK(even.matched == 31);   // the first attack, at 0, has no frame before it
    CHECK(even.levelSpreadAllDb < 0.5);
    CHECK(std::abs(uneven.levelSpreadAllDb - 3) < 1.2);
    REQUIRE(even.beatAccentDb.size() == 4);   // the downbeat's accent stays out of the spread, measured on its own
    CHECK(std::abs(even.beatAccentDb[0] - even.beatAccentDb[1] - 6) < 0.3);
}
