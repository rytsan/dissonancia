#include "beats.hpp"

#include <algorithm>
#include <cmath>
#include <numeric>

namespace dz {

BeatTrack track_beats(const float* x, size_t n, uint32_t rate, double bpmHint) {
    BeatTrack out;
    const double hopS = 0.01;
    const size_t hop = size_t(rate * hopS), frames = hop ? n / hop : 0;
    if (frames < 400) return out;   // under 4 s: no tempo worth reporting

    // Onset envelope: positive log-energy flux, local mean removed, unit variance.
    std::vector<double> env(frames, 0.0);
    double prev = std::log(1e-10);   // silence before the file: a hit on frame 0 is an onset
    for (size_t f = 0; f < frames; f++) {
        double e = 0;
        for (size_t i = f * hop; i < (f + 1) * hop; i++) e += double(x[i]) * x[i];
        double le = std::log(e / double(hop) + 1e-10);
        env[f] = std::max(0.0, le - prev);
        prev = le;
    }
    {
        std::vector<double> m(frames);
        const int w = 25;   // +-0.25 s
        std::vector<double> c(frames + 1, 0);
        for (size_t f = 0; f < frames; f++) c[f + 1] = c[f] + env[f];
        for (size_t f = 0; f < frames; f++) {
            size_t a = f > size_t(w) ? f - w : 0, b = std::min(frames, f + w + 1);
            m[f] = (c[b] - c[a]) / double(b - a);
        }
        double sd = 0;
        for (size_t f = 0; f < frames; f++) { env[f] = std::max(0.0, env[f] - m[f]); sd += env[f] * env[f]; }
        sd = std::sqrt(sd / double(frames)) + 1e-12;
        for (double& v : env) v /= sd;
    }

    // Global period: autocorrelation over 40..200 BPM weighted by the prior.
    const double center = bpmHint > 0 ? bpmHint : 110, sigma = bpmHint > 0 ? 0.08 : 0.5;   // octaves
    const int lo = int(60 / 200.0 / hopS), hi = int(60 / 40.0 / hopS);
    std::vector<double> ac(size_t(hi + 2), 0);
    for (int L = lo - 1; L <= hi + 1; L++)
        for (size_t f = size_t(L); f < frames; f++) ac[size_t(L)] += env[f] * env[f - L];
    int best = lo;
    double bestScore = -1;
    for (int L = lo; L <= hi; L++) {
        double oct = std::log2(60 / (L * hopS) / center);
        double s = ac[size_t(L)] * std::exp(-0.5 * oct * oct / (sigma * sigma));
        if (s > bestScore) { bestScore = s; best = L; }
    }
    double a = ac[size_t(best - 1)], b = ac[size_t(best)], c = ac[size_t(best + 1)], d = a - 2 * b + c;
    const double period = best + (d < 0 ? 0.5 * (a - c) / d : 0);   // frames, parabolic refinement
    out.bpm = 60 / (period * hopS);

    // Dynamic programming: score = onset strength + best predecessor penalised by how far its
    // interval is from the period (log ratio, squared).
    const double tightness = 100;
    std::vector<double> score(frames);
    std::vector<int> from(frames, -1);
    const int pMin = int(std::round(period / 2)), pMax = int(std::round(period * 2));
    for (size_t t = 0; t < frames; t++) {
        double bestPrev = 0;   // starting fresh costs nothing
        int arg = -1;
        for (int k = pMin; k <= pMax && k <= int(t); k++) {
            double r = std::log(k / period), v = score[t - size_t(k)] - tightness * r * r;
            if (arg < 0 || v > bestPrev) { bestPrev = v; arg = int(t) - k; }
        }
        score[t] = env[t] + std::max(0.0, bestPrev);
        from[t] = bestPrev > 0 ? arg : -1;
    }
    // The last beat: the best score in the final period; then back along the chain.
    size_t last = frames - 1;
    for (size_t t = frames - size_t(std::min<double>(period, double(frames))); t < frames; t++)
        if (score[t] > score[last]) last = t;
    std::vector<int> chain;
    for (int t = int(last); t >= 0; t = from[size_t(t)]) chain.push_back(t);
    std::reverse(chain.begin(), chain.end());
    // Leading and trailing beats over silence (the chain runs to the file's edges): dropped while
    // their onset is under a third of the median beat's.
    auto strength = [&](int t) {
        double m = 0;
        for (int k = std::max(0, t - 2); k <= std::min(int(frames) - 1, t + 2); k++) m = std::max(m, env[size_t(k)]);
        return m;
    };
    std::vector<double> s;
    for (int t : chain) s.push_back(strength(t));
    if (s.empty()) return out;
    std::vector<double> sorted = s;
    std::nth_element(sorted.begin(), sorted.begin() + sorted.size() / 2, sorted.end());
    const double floor = sorted[sorted.size() / 2] / 3;
    size_t i0 = 0, i1 = chain.size();
    while (i0 < i1 && s[i0] < floor) i0++;
    while (i1 > i0 && s[i1 - 1] < floor) i1--;
    for (size_t i = i0; i < i1; i++) out.beats.push_back(chain[i] * hopS + hopS / 2);
    return out;
}

}  // namespace dz
