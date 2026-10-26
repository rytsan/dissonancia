#include "dynamics.hpp"

#include <algorithm>
#include <cmath>

#include "loudness.hpp"

namespace dz {

namespace {
float lufs(double e) { return e > 1e-12 ? float(-0.691 + 10 * std::log10(e)) : meter::kSilent; }
// Least-squares change of y over its span (y's index as time).
float fitted_change(const std::vector<float>& y) {
    const double m = double(y.size());
    if (m < 2) return 0;
    double sx = 0, sy = 0, sxx = 0, sxy = 0;
    for (size_t i = 0; i < y.size(); i++) sx += double(i), sy += y[i], sxx += double(i) * double(i), sxy += double(i) * y[i];
    const double den = m * sxx - sx * sx;
    return den > 0 ? float((m * sxy - sx * sy) / den * (m - 1)) : 0;
}
}  // namespace

DynamicsReport dynamics_report(const float* x, size_t n, uint32_t rate, double barSeconds) {
    DynamicsReport r;
    meter::Loudness L;
    L.init(rate);
    meter::Biquad2 pre = L.pre, rlb = L.rlb;   // the same K-weighting, for per-block energies
    const size_t block = std::max<size_t>(1, rate / 10);
    std::vector<double> blocks;   // K-weighted mean square per 100 ms
    double acc = 0;
    for (size_t i = 0; i < n; i++) {
        L.run(x[i], 0.f);
        const double y = rlb.run(pre.run(x[i], 0), 0);
        acc += y * y;
        if ((i + 1) % block == 0) blocks.push_back(acc / double(block)), acc = 0;
    }
    r.integratedLufs = L.integrated();
    r.rangeLu = L.range();
    if (blocks.empty() || r.integratedLufs <= -70) return r;

    // Bars: the mean energy of their blocks; the trend over the bars that sound (within 20 LU).
    if (barSeconds > 0) {
        const size_t per = std::max<size_t>(1, size_t(std::lround(barSeconds * 10)));
        std::vector<float> sounding;
        for (size_t b = 0; b * per < blocks.size(); b++) {
            double e = 0;
            size_t k = b * per;
            for (; k < std::min(blocks.size(), (b + 1) * per); k++) e += blocks[k];
            r.barLufs.push_back(lufs(e / double(k - b * per)));
            if (r.barLufs.back() > r.integratedLufs - 20) sounding.push_back(r.barLufs.back());
        }
        r.trendLu = fitted_change(sounding);
    }

    // Phrases: blocks within 20 LU of the take, gaps of 4 blocks or less bridged, 15 blocks or more.
    const float gate = r.integratedLufs - 20;
    for (size_t i = 0; i < blocks.size();) {
        if (lufs(blocks[i]) < gate) { i++; continue; }
        size_t j = i, last = i;
        while (j < blocks.size() && j - last <= 4) {
            if (lufs(blocks[j]) >= gate) last = j;
            j++;
        }
        const size_t end = last + 1, len = end - i;
        if (len >= 15) {
            double e = 0, a = 0, b = 0;
            const size_t third = len / 3;
            for (size_t k = i; k < end; k++) e += blocks[k];
            for (size_t k = i; k < i + third; k++) a += blocks[k];
            for (size_t k = end - third; k < end; k++) b += blocks[k];
            size_t peak = i;
            double best = 0;
            for (size_t k = i; k + 4 <= end; k++) {   // 400 ms, as the momentary loudness
                double m = 0;
                for (size_t q = k; q < k + 4; q++) m += blocks[q];
                if (m > best) best = m, peak = k;
            }
            r.phrases.push_back({double(i) / 10, double(end) / 10, lufs(e / double(len)), lufs(b / double(third)) - lufs(a / double(third)),
                                 float(double(peak - i) / double(std::max<size_t>(1, len - 4)))});
        }
        i = end;
    }
    return r;
}

}  // namespace dz
