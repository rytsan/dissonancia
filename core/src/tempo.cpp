#include "tempo.hpp"

#include <algorithm>
#include <cmath>

namespace dz {

namespace {
constexpr double kEnvSeconds = 8, kMinEnvSeconds = 4;
constexpr double kMinBpm = 40, kMaxBpm = 200, kPriorBpm = 110, kPriorOctaves = 1.0;
}  // namespace

TempoTracker::TempoTracker(double hopSeconds)
    : hop_(hopSeconds), env_(size_t(std::lround(kEnvSeconds / hopSeconds)), 0.f),
      updateHops_(std::max<uint32_t>(1, uint32_t(std::lround(0.5 / hopSeconds)))), lin_(env_.size(), 0.f) {}

void TempoTracker::process(float onsetStrength, double now, ContextEstimate& out) {
    env_[envWrite_] = std::max(0.f, onsetStrength);
    envWrite_ = (envWrite_ + 1) % uint32_t(env_.size());
    envFill_ = std::min<uint32_t>(envFill_ + 1, uint32_t(env_.size()));
    now_ = now;
    if (++sinceUpdate_ >= updateHops_) {   // ~2 estimates per second; the rest of the time: the last one
        sinceUpdate_ = 0;
        estimate(last_);
    }
    out = last_;
}

void TempoTracker::estimate(ContextEstimate& out) const {
    out = {};
    if (envFill_ * hop_ < kMinEnvSeconds) return;
    const uint32_t n = envFill_, size = uint32_t(env_.size());
    // Unrolled oldest first and smoothed over ~50 ms (triangle): attacks played a few ms off the
    // grid still overlap at the true period.
    const int half = std::max(1, int(std::lround(0.025 / hop_)));
    double mean = 0;
    for (uint32_t i = 0; i < n; i++) {
        float acc = 0, wsum = 0;
        for (int k = -half; k <= half; k++) {
            int j = int(i) + k;
            if (j < 0 || j >= int(n)) continue;
            float w = float(half + 1 - std::abs(k));
            acc += w * env_[(envWrite_ + size - n + uint32_t(j)) % size];
            wsum += w;
        }
        lin_[i] = acc / wsum;
        mean += lin_[i];
    }
    mean /= n;
    double r0 = 0;
    for (uint32_t i = 0; i < n; i++) { lin_[i] -= float(mean); r0 += double(lin_[i]) * lin_[i]; }
    if (r0 <= 1e-9) return;
    // Nothing played for the last 2 s (the envelope is all decay): keep no estimate rather than one
    // made of the tail.
    const uint32_t recent = std::min(n, uint32_t(std::lround(2 / hop_)));
    double recentMean = 0;
    for (uint32_t i = 0; i < recent; i++) recentMean += env_[(envWrite_ + size - 1 - i) % size];
    if (recentMean / recent < 0.25 * mean) return;

    auto ac = [&](uint32_t lag) {
        double s = 0;
        for (uint32_t i = lag; i < n; i++) s += double(lin_[i]) * lin_[i - lag];
        return s / r0 * n / double(n - lag);   // unbiased: long lags are not penalised by overlap
    };
    const uint32_t lo = std::max<uint32_t>(2, uint32_t(std::floor(60 / kMaxBpm / hop_)));
    const uint32_t hi = std::min<uint32_t>(n / 2, uint32_t(std::ceil(60 / kMinBpm / hop_)));
    double bestScore = -1, bestAc = 0;
    uint32_t bestLag = 0;
    for (uint32_t lag = lo; lag <= hi; lag++) {
        double r = ac(lag), bpm = 60 / (lag * hop_);
        double z = std::log2(bpm / kPriorBpm) / kPriorOctaves;
        double score = r * std::exp(-0.5 * z * z);
        if (score > bestScore) { bestScore = score; bestAc = r; bestLag = lag; }
    }
    if (bestLag == 0 || bestAc <= 0) return;
    // Accents every bar make the double period as periodic as the beat itself; the prior alone
    // cannot split them. Half the period wins when it keeps 60 % of the periodicity and still a
    // moderate tempo (a strummed eighth-note pattern at 92 must not become 184).
    if (const uint32_t h = (bestLag + 1) / 2; h >= lo && 60 / (h * hop_) <= 160) {
        uint32_t hb = h;
        for (uint32_t l = h - (h > lo ? 1 : 0); l <= h + 1 && l <= hi; l++)
            if (ac(l) > ac(hb)) hb = l;
        if (double r = ac(hb); r >= 0.6 * bestAc) { bestLag = hb; bestAc = r; }
    }
    // Sub-hop period: the peaks at 1..4 periods, each refined by a parabola and divided by its
    // multiple (a hop is 4 % of the period at 120 BPM / 20 ms; four periods cut that by 4).
    double sumPeriod = 0, sumW = 0;
    for (uint32_t k = 1; k <= 4 && k * bestLag + k + 1 < n / 2 + n / 4; k++) {
        uint32_t peak = k * bestLag;
        for (uint32_t l = k * bestLag - std::min(k * bestLag, k); l <= k * bestLag + k; l++)
            if (l >= 1 && ac(l) > ac(peak)) peak = l;
        double a = ac(peak - 1), b = ac(peak), c = ac(peak + 1), den = a - 2 * b + c;
        double pos = peak + (den < 0 ? std::clamp(0.5 * (a - c) / den, -0.5, 0.5) : 0);
        if (b <= 0) break;
        sumPeriod += b * pos / k;
        sumW += b;
    }
    double bpm = 60 / (sumPeriod / sumW * hop_);
    // Tempo octave: a pulse found below 60 (strums on beats 1 and 3 only) is reported as the beat
    // a player would count, twice as fast; above 180, half.
    while (bpm < 60) bpm *= 2;
    while (bpm > 180) bpm /= 2;
    out.bpm = float(bpm);

    // Phase: the beat grid offset that collects the most envelope, newest sample = now. The marks
    // extrapolate from the last beat until the next estimate (0.5 s).
    const double period = 60 / bpm / hop_;
    double bestSum = -1e30;
    uint32_t bestPhase = 0;
    for (uint32_t ph = 0; ph < uint32_t(std::ceil(period)); ph++) {
        double sum = 0;
        for (double k = 0;; k++) {
            const double idx = double(n - 1) - ph - k * period;
            if (idx < 0) break;
            sum += lin_[size_t(std::lround(idx))];
        }
        if (sum > bestSum) { bestSum = sum; bestPhase = ph; }
    }
    out.lastBeatSeconds = now_ - bestPhase * hop_;
    out.tempoConfidence = float(std::clamp(bestAc, 0.0, 1.0));
}

}  // namespace dz
