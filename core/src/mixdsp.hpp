// STUDIO S4 channel strip and master bus DSP (stereo, per sample, no allocation): RBJ biquads,
// gate, feed-forward compressor, peak limiter. Coefficients are recomputed only when a
// parameter block changes.
#pragma once
#include <algorithm>
#include <cmath>

#include "dissonancia.h"

namespace dz::mix {

constexpr double kPi = 3.14159265358979323846;
inline float db_to_lin(float db) { return std::pow(10.f, db / 20.f); }
inline float lin_to_db(float x) { return 20.f * std::log10(std::max(x, 1e-9f)); }
inline float coef(float ms, double rate) { return ms <= 0 ? 1.f : float(1 - std::exp(-1.0 / (ms * 1e-3 * rate))); }

struct Biquad {
    float b0 = 1, b1 = 0, b2 = 0, a1 = 0, a2 = 0, z1[2]{}, z2[2]{};
    float run(float x, int c) {
        float y = b0 * x + z1[c];
        z1[c] = b1 * x - a1 * y + z2[c];
        z2[c] = b2 * x - a2 * y;
        return y;
    }
    void set(double b0n, double b1n, double b2n, double a0, double a1n, double a2n) {
        b0 = float(b0n / a0); b1 = float(b1n / a0); b2 = float(b2n / a0); a1 = float(a1n / a0); a2 = float(a2n / a0);
    }
    void bypass() { b0 = 1; b1 = b2 = a1 = a2 = 0; }
    void reset() { z1[0] = z1[1] = z2[0] = z2[1] = 0; }
    // RBJ audio EQ cookbook.
    void lowpass(double f, double q, double fs) { double w = 2 * kPi * f / fs, c = std::cos(w), a = std::sin(w) / (2 * q); set((1 - c) / 2, 1 - c, (1 - c) / 2, 1 + a, -2 * c, 1 - a); }
    void highpass(double f, double q, double fs) { double w = 2 * kPi * f / fs, c = std::cos(w), a = std::sin(w) / (2 * q); set((1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + a, -2 * c, 1 - a); }
    void band(const EqBand& b, double fs) {
        const double A = std::pow(10.0, b.gainDb / 40), w = 2 * kPi * std::clamp<double>(b.freqHz, 10, fs * 0.45) / fs, c = std::cos(w),
                     al = std::sin(w) / (2 * std::max(0.1f, b.q));
        if (b.type == EqType::Peak) set(1 + al * A, -2 * c, 1 - al * A, 1 + al / A, -2 * c, 1 - al / A);
        else {
            const double sq = 2 * std::sqrt(A) * al, s = b.type == EqType::LowShelf ? 1 : -1;
            set(A * ((A + 1) - s * (A - 1) * c + sq), s * 2 * A * ((A - 1) - s * (A + 1) * c), A * ((A + 1) - s * (A - 1) * c - sq),
                (A + 1) + s * (A - 1) * c + sq, -s * 2 * ((A - 1) + s * (A + 1) * c), (A + 1) + s * (A - 1) * c - sq);
        }
    }
};

// Butterworth: one biquad (Q 0.707) for 12 dB/oct, two (Q 0.541, 1.307) for 24.
struct Filter {
    Biquad s[2];
    bool on = false, steep = false;
    void set(bool enabled, bool hp, double f, bool st, double fs) {
        on = enabled && f > 0;
        steep = st;
        if (!on) return;
        const double q0 = st ? 0.5412 : 0.7071, q1 = 1.3066;
        hp ? s[0].highpass(f, q0, fs) : s[0].lowpass(f, q0, fs);
        hp ? s[1].highpass(f, q1, fs) : s[1].lowpass(f, q1, fs);
    }
    float run(float x, int c) { if (!on) return x; x = s[0].run(x, c); return steep ? s[1].run(x, c) : x; }
};

struct Eq {
    Biquad b[ANA_EQ_BANDS];
    bool on[ANA_EQ_BANDS]{};
    void set(bool enabled, const EqBand* bands, double fs) {
        for (int i = 0; i < ANA_EQ_BANDS; i++) {
            on[i] = enabled && bands[i].on && std::fabs(bands[i].gainDb) > 0.01f;
            if (on[i]) b[i].band(bands[i], fs);
        }
    }
    float run(float x, int c) { for (int i = 0; i < ANA_EQ_BANDS; i++) if (on[i]) x = b[i].run(x, c); return x; }
};

// Stereo-linked peak envelope; the gate opens above the threshold and closes 3 dB below it.
struct Gate {
    bool on = false, open = true;
    float thr = 0, range = 0, aA = 1, aR = 1, env = 0, gain = 1;
    void set(bool enabled, float thrDb, float attackMs, float releaseMs, float rangeDb, double fs) {
        on = enabled; thr = db_to_lin(thrDb); range = db_to_lin(-std::fabs(rangeDb)); aA = coef(attackMs, fs); aR = coef(releaseMs, fs);
    }
    void run(float& l, float& r) {
        if (!on) { open = true; return; }
        const float lv = std::max(std::fabs(l), std::fabs(r));
        env += (lv > env ? 0.5f : 0.0015f) * (lv - env);   // fast peak follower
        if (env > thr) open = true; else if (env < thr * 0.708f) open = false;
        const float target = open ? 1.f : range;
        gain += (target > gain ? aA : aR) * (target - gain);
        l *= gain; r *= gain;
    }
};

// Feed-forward, stereo-linked, 6 dB soft knee, peak detector with attack / release.
struct Compressor {
    bool on = false;
    float thr = 0, ratio = 1, aA = 1, aR = 1, makeup = 1, env = 0, gr = 0;   // gr: gain reduction in dB, >= 0
    void set(bool enabled, float thrDb, float r, float attackMs, float releaseMs, float makeupDb, double fs) {
        on = enabled; thr = thrDb; ratio = std::max(1.f, r); aA = coef(attackMs, fs); aR = coef(releaseMs, fs); makeup = db_to_lin(makeupDb);
    }
    void run(float& l, float& r) {
        if (!on) { gr = 0; return; }
        const float lv = lin_to_db(std::max(std::fabs(l), std::fabs(r))), over = lv - thr, knee = 6.f;
        float target = 0;
        if (over > knee / 2) target = over * (1 - 1 / ratio);
        else if (over > -knee / 2) { float k = over + knee / 2; target = (1 - 1 / ratio) * k * k / (2 * knee); }
        gr += (target > gr ? aA : aR) * (target - gr);
        const float g = db_to_lin(-gr) * makeup;
        l *= g; r *= g;
    }
};

// Peak limiter: instant attack (no sample above the ceiling), smooth release.
struct Limiter {
    bool on = false;
    float ceil = 1, aR = 1, gain = 1;
    void set(bool enabled, float ceilingDb, float releaseMs, double fs) { on = enabled; ceil = db_to_lin(ceilingDb); aR = coef(releaseMs, fs); }
    void run(float& l, float& r) {
        if (!on) { gain = 1; return; }
        const float pk = std::max(std::fabs(l), std::fabs(r)), need = pk > ceil ? ceil / pk : 1.f;
        gain = need < gain ? need : gain + aR * (1 - gain);
        l *= gain; r *= gain;
    }
    float gr_db() const { return -lin_to_db(gain); }
};

struct Strip {
    ChannelParams p{};
    float trim = 1, fader = 1, panL = 0.7071f, panR = 0.7071f;
    Filter hp, lp;
    Gate gate;
    Eq eq;
    Compressor comp;
    void set(const ChannelParams& np, double fs) {
        p = np;
        trim = db_to_lin(p.trimDb);
        hp.set(p.hpOn, true, p.hpHz, p.steep, fs);
        lp.set(p.lpOn, false, p.lpHz, p.steep, fs);
        gate.set(p.gateOn, p.gateThresholdDb, p.gateAttackMs, p.gateReleaseMs, p.gateRangeDb, fs);
        eq.set(p.eqOn, p.eq, fs);
        comp.set(p.compOn, p.compThresholdDb, p.compRatio, p.compAttackMs, p.compReleaseMs, p.compMakeupDb, fs);
        fader = p.faderDb <= -90 ? 0.f : db_to_lin(p.faderDb);
        const double a = (std::clamp(p.pan, -1.f, 1.f) + 1) * kPi / 4;   // constant power
        panL = float(std::cos(a)); panR = float(std::sin(a));
    }
    // Up to the analysis tap: trim, filters, gate, EQ, compressor.
    void tap(float& l, float& r) {
        l *= trim; r *= trim;
        l = lp.run(hp.run(l, 0), 0); r = lp.run(hp.run(r, 1), 1);
        gate.run(l, r);
        l = eq.run(l, 0); r = eq.run(r, 1);
        comp.run(l, r);
    }
    void out(float& l, float& r) const { l *= fader * panL * 1.4142f; r *= fader * panR * 1.4142f; }   // centre = unity
};

struct Bus {
    MasterParams p{};
    Eq eq;
    Compressor comp;
    Limiter lim;
    float fader = 1;
    void set(const MasterParams& np, double fs) {
        p = np;
        eq.set(p.eqOn, p.eq, fs);
        comp.set(p.compOn, p.compThresholdDb, p.compRatio, p.compAttackMs, p.compReleaseMs, p.compMakeupDb, fs);
        lim.set(p.limiterOn, p.limiterCeilingDb, p.limiterReleaseMs, fs);
        fader = db_to_lin(p.faderDb);
    }
    void run(float& l, float& r) { l = eq.run(l, 0); r = eq.run(r, 1); comp.run(l, r); l *= fader; r *= fader; lim.run(l, r); }
};

inline void channel_defaults(ChannelParams& c) {
    c = {};
    c.hpHz = 80; c.lpHz = 12000; c.gateThresholdDb = -50; c.gateAttackMs = 1; c.gateReleaseMs = 120; c.gateRangeDb = 40;
    const float f[ANA_EQ_BANDS] = {120, 500, 2000, 8000};
    for (int i = 0; i < ANA_EQ_BANDS; i++) c.eq[i] = {f[i], 0, 1.0f, i == 0 ? EqType::LowShelf : i == 3 ? EqType::HighShelf : EqType::Peak, 1, {}};
    c.compThresholdDb = -18; c.compRatio = 3; c.compAttackMs = 10; c.compReleaseMs = 120; c.compMakeupDb = 0;
}

inline void master_defaults(MasterParams& m) {
    m = {};
    const float f[ANA_EQ_BANDS] = {80, 400, 3000, 10000};
    for (int i = 0; i < ANA_EQ_BANDS; i++) m.eq[i] = {f[i], 0, 0.8f, i == 0 ? EqType::LowShelf : i == 3 ? EqType::HighShelf : EqType::Peak, 1, {}};
    m.compThresholdDb = -12; m.compRatio = 2; m.compAttackMs = 20; m.compReleaseMs = 200; m.limiterCeilingDb = -1; m.limiterReleaseMs = 80; m.limiterOn = 1;
}

}  // namespace dz::mix
