// LIVE pedalboard: stompboxes on the input and the pads (dissonancia.h Pedalboard), patched with
// cables like STUDIO's lunchbox, per sample in the playground's callback, no added latency.
// A pedal's state (delay lines, filters, LFOs) lives in a Pedal object kept by id while the pedal
// exists; a new board is published by pointer and the callback takes it on its next block.
#pragma once
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <vector>

#include "dissonancia.h"

namespace dz {

struct Pedal {
    static constexpr double kPi = 3.14159265358979323846;
    PedalKind kind;
    double fs;
    // Delay lines (chorus, delay), reverb combs and allpasses: sized at creation.
    std::vector<float> line[2];
    size_t w = 0;
    struct Comb { std::vector<float> b; size_t i = 0; float store = 0; };
    struct All { std::vector<float> b; size_t i = 0; };
    Comb comb[2][4];
    All all[2][2];
    double lfo = 0, lp[2]{}, ic1[2]{}, ic2[2]{}, hp[2]{}, hpx[2]{}, env = 0, prev = 0;
    float flip = 1;

    Pedal(PedalKind k, double rate) : kind(k), fs(rate) {
        if (k == PedalKind::Chorus) for (auto& l : line) l.assign(size_t(rate * 0.05) + 4, 0.f);
        if (k == PedalKind::Delay) for (auto& l : line) l.assign(size_t(rate * 2.0) + 4, 0.f);
        if (k == PedalKind::Reverb) {
            const int cl[4] = {1116, 1188, 1277, 1356}, al[2] = {556, 441};
            for (int c = 0; c < 2; c++) {
                for (int j = 0; j < 4; j++) comb[c][j].b.assign(size_t((cl[j] + 23 * c) * rate / 44100), 0.f);
                for (int j = 0; j < 2; j++) all[c][j].b.assign(size_t((al[j] + 23 * c) * rate / 44100), 0.f);
            }
        }
    }

    static float lerp(const std::vector<float>& l, size_t w, double d) {   // d samples behind the write position
        const size_t n = l.size();
        const double p = double(w + n) - d;
        const size_t a = size_t(p) % n, b = (a + 1) % n;
        const double f = p - std::floor(p);
        return float(l[a] * (1 - f) + l[b] * f);
    }

    // One stereo frame. k: the knobs (0..1); beatFrames: the tempo (the delay's sixteenths).
    void run(float& L, float& R, const float* k, double beatFrames) {
        switch (kind) {
            case PedalKind::Drive: {   // gain 0..40 dB, tone 800 Hz .. 12 kHz, level -24..+6 dB, mix
                const double g = std::pow(10.0, k[0] * 2), a = 1 - std::exp(-2 * kPi * (800 * std::pow(15.0, k[1])) / fs), lvl = std::pow(10.0, (k[2] * 30 - 24) / 20);
                float* x[2] = {&L, &R};
                for (int c = 0; c < 2; c++) {
                    const double y = std::tanh(g * *x[c]) / std::tanh(std::min(g, 3.0));
                    lp[c] += a * (y - lp[c]);
                    *x[c] = float((1 - k[3]) * *x[c] + k[3] * lp[c] * lvl);
                }
                break;
            }
            case PedalKind::Octave: {   // an octave down (flip-flop on the zero crossings), an octave up (full-wave), dry
                const double m = 0.5 * (L + R);
                lp[0] += 0.02 * (m - lp[0]);                     // tracking: the fundamental, ~150 Hz low-passed
                if (prev <= 0 && lp[0] > 0) flip = -flip;
                prev = lp[0];
                env += (std::fabs(m) > env ? 0.05 : 0.0005) * (std::fabs(m) - env);
                const double down = flip * env * 1.4;
                const double up = std::fabs(m);
                hp[0] += 0.001 * (up - hp[0]);                   // the full wave's DC out
                const double o = k[0] * down + k[1] * (up - hp[0]) * 2 + k[2] * m;
                L = R = float(o);
                break;
            }
            case PedalKind::Chorus: {   // rate 0.1..5 Hz, depth 1..15 ms, mix, stereo width
                lfo += 2 * kPi * (0.1 * std::pow(50.0, k[0])) / fs;
                if (lfo > 2 * kPi) lfo -= 2 * kPi;
                const double base = 0.007 * fs, depth = (0.001 + 0.014 * k[1]) * fs * 0.5;
                line[0][w] = L; line[1][w] = R;
                const float l = lerp(line[0], w, base + depth * std::sin(lfo)), r = lerp(line[1], w, base + depth * std::sin(lfo + kPi / 2 * k[3] * 2));
                w = (w + 1) % line[0].size();
                L = float((1 - 0.5 * k[2]) * L + k[2] * l);
                R = float((1 - 0.5 * k[2]) * R + k[2] * r);
                break;
            }
            case PedalKind::Delay: {   // time 1..16 sixteenths of the tempo, feedback, mix, tone (darker repeats)
                const double d = std::clamp(std::round(1 + k[0] * 15) * beatFrames / 4, 1.0, double(line[0].size() - 2));
                const double a = 1 - std::exp(-2 * kPi * (1500 * std::pow(8.0, k[3])) / fs);
                float* x[2] = {&L, &R};
                for (int c = 0; c < 2; c++) {
                    const double y = lerp(line[c], w, d);
                    lp[c] += a * (y - lp[c]);
                    line[c][w] = float(*x[c] + 0.95 * k[1] * lp[c]);
                    *x[c] = float(*x[c] + k[2] * y);
                }
                w = (w + 1) % line[0].size();
                break;
            }
            case PedalKind::Reverb: {   // size, damping, mix, width (Freeverb's combs and allpasses)
                const float fb = float(0.7 + 0.28 * k[0]), damp = float(0.05 + 0.6 * k[1]), in = 0.015f * (L + R);
                float out[2];
                for (int c = 0; c < 2; c++) {
                    float s = 0;
                    for (auto& cb : comb[c]) {
                        float y = cb.b[cb.i];
                        cb.store = y * (1 - damp) + cb.store * damp;
                        cb.b[cb.i] = in + cb.store * fb;
                        cb.i = (cb.i + 1) % cb.b.size();
                        s += y;
                    }
                    for (auto& ap : all[c]) {
                        float y = ap.b[ap.i];
                        ap.b[ap.i] = s + y * 0.5f;
                        ap.i = (ap.i + 1) % ap.b.size();
                        s = y - s;
                    }
                    out[c] = s;
                }
                const float wid = k[3], l = out[0] * (0.5f + wid / 2) + out[1] * (0.5f - wid / 2), r = out[1] * (0.5f + wid / 2) + out[0] * (0.5f - wid / 2);
                L = L * (1 - 0.5f * k[2]) + k[2] * l;
                R = R * (1 - 0.5f * k[2]) + k[2] * r;
                break;
            }
            case PedalKind::Filter: {   // cutoff 80 Hz .. 8 kHz, resonance, mode (low / band / high), LFO sweep rate (0: off)
                lfo += 2 * kPi * (k[3] < 0.02f ? 0 : 0.1 * std::pow(60.0, k[3])) / fs;
                if (lfo > 2 * kPi) lfo -= 2 * kPi;
                const double f = 80 * std::pow(100.0, std::clamp(k[0] + (k[3] < 0.02f ? 0 : 0.25 * std::sin(lfo)), 0.0, 1.0));
                const double g = std::tan(kPi * std::min(f, fs * 0.45) / fs), q = 0.7 + 9.3 * k[1] * k[1], kk = 1 / q;
                const double a1 = 1 / (1 + g * (g + kk)), a2 = g * a1, a3 = g * a2;
                float* x[2] = {&L, &R};
                for (int c = 0; c < 2; c++) {   // TPT state variable filter
                    const double v3 = *x[c] - ic2[c], v1 = a1 * ic1[c] + a2 * v3, v2 = ic2[c] + a2 * ic1[c] + a3 * v3;
                    ic1[c] = 2 * v1 - ic1[c];
                    ic2[c] = 2 * v2 - ic2[c];
                    const double lo = v2, bp = v1, hi = *x[c] - kk * v1 - v2;
                    *x[c] = float(k[2] < 0.34f ? lo : k[2] < 0.67f ? bp : hi);
                }
                break;
            }
        }
    }
};

}  // namespace dz
