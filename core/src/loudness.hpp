// STUDIO metering on the master (ITU-R BS.1770-4 / EBU R128): K-weighted loudness — momentary
// (400 ms), short-term (3 s), integrated (gated: -70 LUFS absolute, -10 LU relative) and loudness
// range (short-term values, -20 LU relative, 10th..95th percentile) — and the true peak by 4x
// oversampling. Per sample, no allocation; the gated measures come from 0.1 LU histograms, so a
// whole song costs the same memory as a second.
#pragma once
#include <algorithm>
#include <cmath>
#include <cstdint>

namespace dz::meter {

constexpr double kPi = 3.14159265358979323846;
constexpr float kSilent = -120.f;

// Double precision: the K-weighting high-pass sits at 38 Hz, too low for float at 48 kHz.
struct Biquad2 {
    double b0 = 1, b1 = 0, b2 = 0, a1 = 0, a2 = 0, z1[2]{}, z2[2]{};
    double run(double x, int c) {
        double y = b0 * x + z1[c];
        z1[c] = b1 * x - a1 * y + z2[c];
        z2[c] = b2 * x - a2 * y;
        return y;
    }
};

// 4x oversampling by a 48-tap windowed sinc, 4 phases of 12 taps.
struct TruePeak {
    static constexpr int kTaps = 12;
    float h[4][kTaps]{}, x[2][kTaps]{};
    float peak[2]{};
    TruePeak() {
        for (int p = 0; p < 4; p++) {
            double sum = 0;
            for (int j = 0; j < kTaps; j++) {
                const int m = p + 4 * j;                  // 0..47
                const double t = (m - 23.5) / 4.0, w = 0.5 - 0.5 * std::cos(2 * kPi * (m + 0.5) / 48.0);
                h[p][j] = float((t == 0 ? 1.0 : std::sin(kPi * t) / (kPi * t)) * w);
                sum += h[p][j];
            }
            for (int j = 0; j < kTaps; j++) h[p][j] = float(h[p][j] / sum);   // unity gain per phase
        }
    }
    void run(float v, int c) {
        std::copy_backward(x[c], x[c] + kTaps - 1, x[c] + kTaps);
        x[c][0] = v;
        for (int p = 0; p < 4; p++) {
            float y = 0;
            for (int j = 0; j < kTaps; j++) y += h[p][j] * x[c][j];
            peak[c] = std::max(peak[c], std::fabs(y));
        }
        peak[c] = std::max(peak[c], std::fabs(v));
    }
};

struct Loudness {
    static constexpr int kBins = 750;          // 0.1 LU from -70 to +5
    static constexpr int kSubs = 30;           // 100 ms sub-blocks: 3 s
    Biquad2 pre, rlb;
    uint32_t sub = 4800, cnt = 0;
    double acc = 0, blocks[kSubs]{};
    int head = 0, filled = 0;
    float momentary = kSilent, shortTerm = kSilent;
    uint32_t mCount[kBins]{}, sCount[kBins]{};
    double mEnergy[kBins]{}, sEnergy[kBins]{};
    TruePeak tp;

    static float lufs(double e) { return e > 0 ? float(-0.691 + 10 * std::log10(e)) : kSilent; }
    static int bin(float l) { return std::clamp(int((l + 70.f) * 10.f), 0, kBins - 1); }
    static double energy(double l) { return std::pow(10.0, (l + 0.691) / 10); }

    void init(double fs) {
        // K-weighting at any rate (the BS.1770 stage-1 shelf and stage-2 high-pass, by their analog prototypes).
        double f0 = 1681.974450955533, G = 3.999843853973347, Q = 0.7071752369554196, K = std::tan(kPi * f0 / fs);
        const double Vh = std::pow(10.0, G / 20), Vb = std::pow(Vh, 0.4996667741545416), a0 = 1 + K / Q + K * K;
        pre.b0 = (Vh + Vb * K / Q + K * K) / a0; pre.b1 = 2 * (K * K - Vh) / a0; pre.b2 = (Vh - Vb * K / Q + K * K) / a0;
        pre.a1 = 2 * (K * K - 1) / a0; pre.a2 = (1 - K / Q + K * K) / a0;
        f0 = 38.13547087602444; Q = 0.5003270373238773; K = std::tan(kPi * f0 / fs);
        const double a = 1 + K / Q + K * K;
        rlb.b0 = 1; rlb.b1 = -2; rlb.b2 = 1; rlb.a1 = 2 * (K * K - 1) / a; rlb.a2 = (1 - K / Q + K * K) / a;
        sub = std::max<uint32_t>(1, uint32_t(fs / 10));
        reset();
    }

    void reset() {
        pre.z1[0] = pre.z1[1] = pre.z2[0] = pre.z2[1] = rlb.z1[0] = rlb.z1[1] = rlb.z2[0] = rlb.z2[1] = 0;
        acc = 0; cnt = 0; head = 0; filled = 0;
        momentary = shortTerm = kSilent;
        std::fill(std::begin(mCount), std::end(mCount), 0u); std::fill(std::begin(sCount), std::end(sCount), 0u);
        std::fill(std::begin(mEnergy), std::end(mEnergy), 0.0); std::fill(std::begin(sEnergy), std::end(sEnergy), 0.0);
        tp.peak[0] = tp.peak[1] = 0;
    }

    void run(float l, float r) {
        const double yl = rlb.run(pre.run(l, 0), 0), yr = rlb.run(pre.run(r, 1), 1);
        acc += yl * yl + yr * yr;
        tp.run(l, 0);
        tp.run(r, 1);
        if (++cnt == sub) close_block();
    }

    void close_block() {
        blocks[head] = acc / sub;
        head = (head + 1) % kSubs;
        filled = std::min(filled + 1, kSubs);
        acc = 0; cnt = 0;
        auto mean = [&](int n) { double s = 0; for (int k = 1; k <= n; k++) s += blocks[(head - k + kSubs) % kSubs]; return s / n; };
        if (filled >= 4) {   // 400 ms blocks, 75 % overlap
            const double e = mean(4);
            momentary = lufs(e);
            if (momentary >= -70) { mCount[bin(momentary)]++; mEnergy[bin(momentary)] += e; }
        }
        if (filled >= kSubs) {
            const double e = mean(kSubs);
            shortTerm = lufs(e);
            if (shortTerm >= -70) { sCount[bin(shortTerm)]++; sEnergy[bin(shortTerm)] += e; }
        }
    }

    // Mean energy of the blocks at or above `gate` LUFS.
    static double gated(const uint32_t* count, const double* en, float gate, uint64_t* n = nullptr) {
        double s = 0;
        uint64_t k = 0;
        for (int b = bin(gate); b < kBins; b++) { s += en[b]; k += count[b]; }
        if (n) *n = k;
        return k ? s / double(k) : 0;
    }

    float integrated() const {
        const double e = gated(mCount, mEnergy, -70);
        return e > 0 ? lufs(gated(mCount, mEnergy, lufs(e) - 10)) : kSilent;
    }

    float range() const {
        const double e = gated(sCount, sEnergy, -70);
        if (e <= 0) return 0;
        uint64_t n = 0;
        const int from = bin(lufs(e) - 20);
        gated(sCount, sEnergy, lufs(e) - 20, &n);
        if (n < 2) return 0;
        auto percentile = [&](double q) {
            const uint64_t want = uint64_t(q * double(n - 1));
            uint64_t seen = 0;
            for (int b = from; b < kBins; b++) { seen += sCount[b]; if (seen > want) return -70.f + 0.1f * float(b) + 0.05f; }
            return 5.f;
        };
        return percentile(0.95) - percentile(0.10);
    }

    float true_peak_db(int c) const { return tp.peak[c] > 0 ? 20.f * std::log10(tp.peak[c]) : kSilent; }
};

}  // namespace dz::meter
