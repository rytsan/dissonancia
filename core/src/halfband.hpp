// Polyphase IIR all-pass half-band 2:1 decimator (spec §5, CQT octave cascade).
// Two branches of first-order all-pass sections in z^-2; magnitude-only use (phase is
// irrelevant for a magnitude CQT). Coefficient design after Laurent de Soras' HIIR
// (elliptic half-band, WTFPL). Group delay is computed, never guessed.
#pragma once
#include <cmath>
#include <complex>
#include <cstdint>
#include <vector>

namespace dz {

// Designs `count` all-pass coefficients for a half-band with transition band `tbw`
// (fraction of the input rate, e.g. 0.08: passband to 0.25-tbw, stopband from 0.25+tbw).
inline std::vector<double> design_halfband(int count, double tbw) {
    constexpr double pi = 3.14159265358979323846;
    double k = std::tan((1 - tbw * 2) * pi / 4);
    k *= k;
    double kksqrt = std::pow(1 - k * k, 0.25);
    double e = 0.5 * (1 - kksqrt) / (1 + kksqrt);
    double e2 = e * e, e4 = e2 * e2;
    double q = e * (1 + e4 * (2 + e4 * (15 + 150 * e4)));
    const int order = count * 2 + 1;
    std::vector<double> coefs(static_cast<size_t>(count));
    for (int index = 0; index < count; index++) {
        const int c = index + 1;
        double num = 0, den = 0, term;
        int i = 0, sign = 1;
        do {
            term = std::pow(q, double(i * (i + 1))) * std::sin((i * 2 + 1) * c * pi / order) * sign;
            num += term;
            sign = -sign;
            ++i;
        } while (std::fabs(term) > 1e-100);
        i = 1;
        sign = -1;
        do {
            term = std::pow(q, double(i * i)) * std::cos(i * 2 * c * pi / order) * sign;
            den += term;
            sign = -sign;
            ++i;
        } while (std::fabs(term) > 1e-100);
        double ww = num * std::pow(q, 0.25) / (den + 0.5);
        double wwsq = ww * ww;
        double x = std::sqrt((1 - wwsq * k) * (1 - wwsq / k)) / (1 + wwsq);
        coefs[size_t(index)] = (1 - x) / (1 + x);
    }
    return coefs;
}

class HalfbandDecimator {
public:
    void init(const std::vector<double>& coefs) {
        a_.assign(coefs.begin(), coefs.end());
        x_.assign(a_.size(), 0.f);
        y_.assign(a_.size(), 0.f);
        havePending_ = false;
    }

    // Consumes n input samples, writes floor((n + pending) / 2) outputs, returns the count.
    uint32_t process(const float* in, uint32_t n, float* out) {
        uint32_t count = 0;
        for (uint32_t i = 0; i < n; i++) {
            if (!havePending_) { pending_ = in[i]; havePending_ = true; continue; }
            havePending_ = false;
            float s0 = in[i], s1 = pending_;   // branch 0: newer sample, branch 1: older (z^-1)
            for (size_t c = 0; c < a_.size(); c++) {
                float& s = (c & 1) ? s1 : s0;
                float t = (s - y_[c]) * a_[c] + x_[c];
                x_[c] = s;
                y_[c] = t;
                s = t;
            }
            out[count++] = 0.5f * (s0 + s1);
        }
        return count;
    }

    // H(e^jw) at input-rate angular frequency w.
    std::complex<double> response(double w) const {
        using C = std::complex<double>;
        C z2 = std::exp(C(0, -2 * w)), b0 = 1, b1 = 1;
        for (size_t c = 0; c < a_.size(); c++) {
            const double a = a_[c];
            C ap = (a + z2) / (1.0 + a * z2);
            (c & 1 ? b1 : b0) *= ap;
        }
        return 0.5 * (b0 + std::exp(C(0, -w)) * b1);
    }

    // Group delay in INPUT samples at input-rate angular frequency w.
    double group_delay(double w) const {
        const double dw = 1e-4;
        double p1 = std::arg(response(w - dw)), p2 = std::arg(response(w + dw));
        double d = p2 - p1;
        while (d > 3.14159265358979) d -= 2 * 3.14159265358979;
        while (d < -3.14159265358979) d += 2 * 3.14159265358979;
        return -d / (2 * dw);
    }

private:
    std::vector<float> a_, x_, y_;
    float pending_ = 0;
    bool havePending_ = false;
};

}  // namespace dz
