// STUDIO mixing DSP (stereo, per sample, no allocation): RBJ biquads, gate, feed-forward
// compressor, peak limiter, in double precision; and the mixer graph (dissonancia.h MixGraph) that
// runs them as a console. Coefficients are recomputed only when the mix changes. Everything here is
// plain data: the player copies a whole Engine as the snapshot it re-renders from.
#pragma once
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>

#include "dissonancia.h"

namespace dz::mix {

constexpr double kPi = 3.14159265358979323846;
inline double db_to_lin(double db) { return std::pow(10.0, db / 20.0); }
inline float lin_to_db(double x) { return float(20.0 * std::log10(std::max(x, 1e-9))); }
inline double coef(double ms, double rate) { return ms <= 0 ? 1.0 : 1.0 - std::exp(-1.0 / (ms * 1e-3 * rate)); }

struct Biquad {
    double b0 = 1, b1 = 0, b2 = 0, a1 = 0, a2 = 0, z1[2]{}, z2[2]{};
    double run(double x, int c) {
        double y = b0 * x + z1[c];
        z1[c] = b1 * x - a1 * y + z2[c];
        z2[c] = b2 * x - a2 * y;
        return y;
    }
    void set(double b0n, double b1n, double b2n, double a0, double a1n, double a2n) {
        b0 = b0n / a0; b1 = b1n / a0; b2 = b2n / a0; a1 = a1n / a0; a2 = a2n / a0;
    }
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
    double run(double x, int c) { if (!on) return x; x = s[0].run(x, c); return steep ? s[1].run(x, c) : x; }
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
    double run(double x, int c) { for (int i = 0; i < ANA_EQ_BANDS; i++) if (on[i]) x = b[i].run(x, c); return x; }
};

// Stereo-linked peak envelope; the gate opens above the threshold and closes 3 dB below it.
struct Gate {
    bool on = false, open = true;
    double thr = 0, range = 0, aA = 1, aR = 1, env = 0, gain = 1;
    void set(bool enabled, float thrDb, float attackMs, float releaseMs, float rangeDb, double fs) {
        on = enabled; thr = db_to_lin(thrDb); range = db_to_lin(-std::fabs(rangeDb)); aA = coef(attackMs, fs); aR = coef(releaseMs, fs);
    }
    void run(double& l, double& r) {
        if (!on) { open = true; gain = 1; return; }
        const double lv = std::max(std::fabs(l), std::fabs(r));
        env += (lv > env ? 0.5 : 0.0015) * (lv - env);   // fast peak follower
        if (env > thr) open = true; else if (env < thr * 0.708) open = false;
        const double target = open ? 1.0 : range;
        gain += (target > gain ? aA : aR) * (target - gain);
        l *= gain; r *= gain;
    }
};

// Feed-forward, stereo-linked, 6 dB soft knee, peak detector with attack / release.
struct Compressor {
    bool on = false;
    double thr = 0, ratio = 1, aA = 1, aR = 1, makeup = 1, gr = 0;   // gr: gain reduction in dB, >= 0
    void set(bool enabled, float thrDb, float r, float attackMs, float releaseMs, float makeupDb, double fs) {
        on = enabled; thr = thrDb; ratio = std::max(1.f, r); aA = coef(attackMs, fs); aR = coef(releaseMs, fs); makeup = db_to_lin(makeupDb);
    }
    void run(double& l, double& r) {
        if (!on) { gr = 0; return; }
        const double lv = lin_to_db(std::max(std::fabs(l), std::fabs(r))), over = lv - thr, knee = 6;
        double target = 0;
        if (over > knee / 2) target = over * (1 - 1 / ratio);
        else if (over > -knee / 2) { double k = over + knee / 2; target = (1 - 1 / ratio) * k * k / (2 * knee); }
        gr += (target > gr ? aA : aR) * (target - gr);
        const double g = db_to_lin(-gr) * makeup;
        l *= g; r *= g;
    }
};

// Peak limiter: instant attack (no sample above the ceiling), smooth release.
struct Limiter {
    bool on = false;
    double ceil = 1, aR = 1, gain = 1;
    void set(bool enabled, float ceilingDb, float releaseMs, double fs) { on = enabled; ceil = db_to_lin(ceilingDb); aR = coef(releaseMs, fs); }
    void run(double& l, double& r) {
        if (!on) { gain = 1; return; }
        const double pk = std::max(std::fabs(l), std::fabs(r)), need = pk > ceil ? ceil / pk : 1.0;
        gain = need < gain ? need : gain + aR * (1 - gain);
        l *= gain; r *= gain;
    }
    float gr_db() const { return -lin_to_db(gain); }
};

// A gain that glides to its target (~10 ms): a fader, a mute or a cable moved never clicks.
struct Smooth {
    double v = 0, target = 0;
    double next(double k) { v += k * (target - v); return v; }
};

// One unit of the graph.
struct Unit {
    UnitParams p{};
    Filter hp, lp;
    Gate gate;
    Eq eq;
    Compressor comp;
    Smooth trim;
    void set(const UnitParams& np, double fs, bool fresh) {
        p = np;
        const bool on = p.on != 0;
        hp.set(on && p.kind == UnitKind::Filter && p.hpOn, true, p.hpHz, p.steep, fs);
        lp.set(on && p.kind == UnitKind::Filter && p.lpOn, false, p.lpHz, p.steep, fs);
        gate.set(on && p.kind == UnitKind::Gate, p.gateThresholdDb, p.gateAttackMs, p.gateReleaseMs, p.gateRangeDb, fs);
        eq.set(on && p.kind == UnitKind::Eq, p.eq, fs);
        comp.set(on && p.kind == UnitKind::Comp, p.compThresholdDb, p.compRatio, p.compAttackMs, p.compReleaseMs, p.compMakeupDb, fs);
        trim.target = on && p.kind == UnitKind::Trim ? db_to_lin(p.trimDb) : 1.0;
        if (fresh) trim.v = trim.target;
    }
    void run(double& l, double& r, double k) {
        switch (p.kind) {
            case UnitKind::Trim: { const double g = trim.next(k); l *= g; r *= g; break; }
            case UnitKind::Filter: l = lp.run(hp.run(l, 0), 0); r = lp.run(hp.run(r, 1), 1); break;
            case UnitKind::Gate: gate.run(l, r); break;
            case UnitKind::Eq: l = eq.run(l, 0); r = eq.run(r, 1); break;
            case UnitKind::Comp: comp.run(l, r); break;
            default: break;
        }
    }
    float gr_db() const { return p.kind == UnitKind::Comp ? float(comp.gr) : p.kind == UnitKind::Gate ? -lin_to_db(gate.gain) : 0.f; }
};

struct Bus {
    MasterParams p{};
    Eq eq;
    Compressor comp;
    Limiter lim;
    Smooth fader;
    void set(const MasterParams& np, double fs, bool fresh) {
        p = np;
        eq.set(p.eqOn, p.eq, fs);
        comp.set(p.compOn, p.compThresholdDb, p.compRatio, p.compAttackMs, p.compReleaseMs, p.compMakeupDb, fs);
        lim.set(p.limiterOn, p.limiterCeilingDb, p.limiterReleaseMs, fs);
        fader.target = db_to_lin(p.faderDb);
        if (fresh) fader.v = fader.target;
    }
    void run(double& l, double& r, double k) { l = eq.run(l, 0); r = eq.run(r, 1); comp.run(l, r); const double g = fader.next(k); l *= g; r *= g; lim.run(l, r); }
};

inline void unit_defaults(UnitParams& u) {
    u = {};
    u.hpHz = 80; u.lpHz = 12000; u.gateThresholdDb = -50; u.gateAttackMs = 1; u.gateReleaseMs = 120; u.gateRangeDb = 40;
    const float f[ANA_EQ_BANDS] = {120, 500, 2000, 8000};
    for (int i = 0; i < ANA_EQ_BANDS; i++) u.eq[i] = {f[i], 0, 1.0f, i == 0 ? EqType::LowShelf : i == 3 ? EqType::HighShelf : EqType::Peak, 1, {}};
    u.compThresholdDb = -18; u.compRatio = 3; u.compAttackMs = 10; u.compReleaseMs = 120; u.compMakeupDb = 0;
}

inline void master_defaults(MasterParams& m) {
    m = {};
    const float f[ANA_EQ_BANDS] = {80, 400, 3000, 10000};
    for (int i = 0; i < ANA_EQ_BANDS; i++) m.eq[i] = {f[i], 0, 0.8f, i == 0 ? EqType::LowShelf : i == 3 ? EqType::HighShelf : EqType::Peak, 1, {}};
    m.compThresholdDb = -12; m.compRatio = 2; m.compAttackMs = 20; m.compReleaseMs = 200; m.limiterCeilingDb = -1; m.limiterReleaseMs = 80; m.limiterOn = 1;
}

// Every track normalled, unity, the master plain: what a loaded file plays through.
inline void graph_defaults(MixGraph& g) {
    g = {};
    master_defaults(g.master);
    g.master.limiterOn = 0;
}

// Peaks and sums over a block, for the meters of the block when it plays.
struct BlockMeters {
    double peak[ANA_MAX_TRACKS], sq[ANA_MAX_TRACKS], in[ANA_MAX_TRACKS], ret[ANA_MAX_TRACKS], unit[ANA_MAX_UNITS], gr[ANA_MAX_UNITS];
    double mpk[2], msq[2], mgr, lgr;
    uint32_t frames;
};

// The graph as a console. Nodes: the tracks' sources (SAI), the units, the tracks' strips (VOLTA ->
// fader -> pan -> AUX, to the master), the master; run in an order where every node comes after
// what feeds it. A node on a loop is left out, and so is every cable to or from it.
struct Engine {
    static constexpr int kSrc = 0, kUnit = ANA_MAX_TRACKS, kStrip = kUnit + ANA_MAX_UNITS, kMaster = kStrip + ANA_MAX_TRACKS, kNodes = kMaster + 1;
    MixGraph g{};
    double fs = 48000, k = 1;   // k: the gains' glide per sample
    int tracks = 0;
    Unit unit[ANA_MAX_UNITS];
    Smooth gainL[ANA_MAX_TRACKS], gainR[ANA_MAX_TRACKS], cable[ANA_MAX_CABLES];
    Bus master;
    // The order: units and strips (sources come first, the master last).
    int16_t order[ANA_MAX_UNITS + ANA_MAX_TRACKS]{};
    int norder = 0;
    bool live[ANA_MAX_CABLES]{};          // not on a loop
    bool normalled[ANA_MAX_TRACKS]{};     // no cable from SAI: SAI is the VOLTA's own
    int16_t inStart[kNodes + 1]{}, inList[ANA_MAX_CABLES]{};
    // Per frame: what each node gives (sources, units, strips after the fader), what each VOLTA had.
    double sai[ANA_MAX_TRACKS][2]{}, uo[ANA_MAX_UNITS][2]{}, post[ANA_MAX_TRACKS][2]{}, ret[ANA_MAX_TRACKS][2]{};

    static int node_from(const Cable& c) { return c.fromKind == JackKind::TrackOut ? kSrc + c.from : c.fromKind == JackKind::TrackAux ? kStrip + c.from : kUnit + c.from; }
    static int node_to(const Cable& c) { return c.toKind == JackKind::TrackReturn ? kStrip + c.to : c.toKind == JackKind::UnitIn ? kUnit + c.to : kMaster; }

    // A new mix (copied). Units keep their state by id; fresh: no glide (a new take, an offline render).
    void set(const MixGraph& ng, int nTracks, double rate, bool fresh) {
        Unit old[ANA_MAX_UNITS];
        std::copy(unit, unit + ANA_MAX_UNITS, old);
        const MixGraph prev = g;
        g = ng;
        g.units = std::clamp(g.units, 0, ANA_MAX_UNITS);
        g.cables = std::clamp(g.cables, 0, ANA_MAX_CABLES);
        tracks = std::clamp(nTracks, 0, ANA_MAX_TRACKS);
        fs = rate;
        k = coef(10, rate);
        for (int u = 0; u < g.units; u++) {
            int was = -1;
            for (int o = 0; o < (fresh ? 0 : prev.units); o++) if (old[o].p.id == g.unit[u].id) was = o;
            unit[u] = was >= 0 ? old[was] : Unit{};
            unit[u].set(g.unit[u], rate, fresh || was < 0);
        }
        Smooth oldCable[ANA_MAX_CABLES];
        std::copy(cable, cable + ANA_MAX_CABLES, oldCable);
        for (int c = 0; c < g.cables; c++) {
            const Cable& n = g.cable[c];
            int was = -1;
            for (int o = 0; o < (fresh ? 0 : prev.cables); o++) {
                const Cable& p = prev.cable[o];
                if (p.fromKind == n.fromKind && p.from == n.from && p.toKind == n.toKind && p.to == n.to) was = o;
            }
            cable[c].target = n.gain;
            cable[c].v = was >= 0 ? oldCable[was].v : n.gain;
        }
        master.set(g.master, rate, fresh);
        sort_graph();
        // Solo in place: a soloed track and every strip it reaches are heard, the others not.
        bool heard[kNodes]{}, anySolo = false;
        for (int t = 0; t < tracks; t++) if (g.track[t].solo) { anySolo = true; heard[kSrc + t] = heard[kStrip + t] = true; }
        for (bool more = true; more;) {
            more = false;
            for (int c = 0; c < g.cables; c++)
                if (live[c] && heard[node_from(g.cable[c])] && !heard[node_to(g.cable[c])]) heard[node_to(g.cable[c])] = more = true;
        }
        for (int t = 0; t < tracks; t++) {
            const TrackParams& tp = g.track[t];
            const bool on = !tp.mute && (!anySolo || heard[kStrip + t]);
            const double a = (std::clamp(tp.pan, -1.f, 1.f) + 1) * kPi / 4, f = on && tp.faderDb > -90 ? db_to_lin(tp.faderDb) * 1.4142135623730951 : 0.0;   // centre = unity
            gainL[t].target = f * std::cos(a);
            gainR[t].target = f * std::sin(a);
            if (fresh) { gainL[t].v = gainL[t].target; gainR[t].v = gainR[t].target; }
        }
    }

    void sort_graph() {
        int indeg[kNodes]{};
        for (int t = 0; t < tracks; t++) normalled[t] = true;
        for (int c = 0; c < g.cables; c++) {
            const Cable& x = g.cable[c];
            const bool fromOk = x.fromKind == JackKind::UnitOut ? x.from < g.units : (x.fromKind == JackKind::TrackOut || x.fromKind == JackKind::TrackAux) && x.from < tracks;
            const bool toOk = (x.toKind == JackKind::UnitIn && x.to < g.units) || (x.toKind == JackKind::TrackReturn && x.to < tracks) || x.toKind == JackKind::Master;
            live[c] = fromOk && toOk;
            if (live[c] && x.fromKind == JackKind::TrackOut) normalled[x.from] = false;
        }
        // Kahn: units and strips; a strip also waits for its own source (always first).
        for (int c = 0; c < g.cables; c++) if (live[c]) indeg[node_to(g.cable[c])]++;
        int queue[kNodes], qh = 0, qt = 0;
        bool done[kNodes]{};
        for (int t = 0; t < tracks; t++) done[kSrc + t] = true;
        for (int c = 0; c < g.cables; c++) if (live[c] && done[node_from(g.cable[c])]) indeg[node_to(g.cable[c])]--;
        for (int u = 0; u < g.units; u++) if (indeg[kUnit + u] == 0) queue[qt++] = kUnit + u;
        for (int t = 0; t < tracks; t++) if (indeg[kStrip + t] == 0) queue[qt++] = kStrip + t;
        norder = 0;
        while (qh < qt) {
            const int n = queue[qh++];
            done[n] = true;
            order[norder++] = int16_t(n);
            for (int c = 0; c < g.cables; c++)
                if (live[c] && node_from(g.cable[c]) == n) {
                    const int to = node_to(g.cable[c]);
                    if (to != kMaster && --indeg[to] == 0) queue[qt++] = to;
                }
        }
        for (int c = 0; c < g.cables; c++) live[c] = live[c] && done[node_from(g.cable[c])] && (node_to(g.cable[c]) == kMaster || done[node_to(g.cable[c])]);
        // The cables into each node, by node (what `inputs` reads every frame).
        for (int n = 0; n <= kNodes; n++) inStart[n] = 0;
        for (int c = 0; c < g.cables; c++) if (live[c]) inStart[node_to(g.cable[c]) + 1]++;
        for (int n = 0; n < kNodes; n++) inStart[n + 1] += inStart[n];
        int fill[kNodes];
        std::copy(inStart, inStart + kNodes, fill);
        for (int c = 0; c < g.cables; c++) if (live[c]) inList[fill[node_to(g.cable[c])]++] = int16_t(c);
        for (int u = 0; u < g.units; u++) if (!done[kUnit + u]) uo[u][0] = uo[u][1] = 0;
        for (int t = 0; t < tracks; t++) if (!done[kStrip + t]) post[t][0] = post[t][1] = ret[t][0] = ret[t][1] = 0;
    }

    const double* port(const Cable& c) const {
        return c.fromKind == JackKind::TrackOut ? sai[c.from] : c.fromKind == JackKind::TrackAux ? post[c.from] : uo[c.from];
    }
    // What the cables into node n bring, each at its gain.
    void inputs(int n, double& l, double& r) {
        for (int i = inStart[n]; i < inStart[n + 1]; i++) {
            const int c = inList[i];
            const double gc = cable[c].next(k), *v = port(g.cable[c]);
            l += gc * v[0]; r += gc * v[1];
        }
    }

    // One frame: src = the tracks' audio (stereo), out = the master.
    void frame(const float (*src)[2], double& L, double& R, BlockMeters* m) {
        for (int t = 0; t < tracks; t++) {
            const double s = g.track[t].polarity ? -1.0 : 1.0;
            sai[t][0] = s * src[t][0]; sai[t][1] = s * src[t][1];
        }
        L = R = 0;
        for (int i = 0; i < norder; i++) {
            const int n = order[i];
            double l = 0, r = 0;
            if (n < kStrip) {
                const int u = n - kUnit;
                inputs(n, l, r);
                unit[u].run(l, r, k);
                uo[u][0] = l; uo[u][1] = r;
            } else {
                const int t = n - kStrip;
                if (normalled[t]) { l = sai[t][0]; r = sai[t][1]; }
                inputs(n, l, r);
                ret[t][0] = l; ret[t][1] = r;
                l *= gainL[t].next(k); r *= gainR[t].next(k);
                post[t][0] = l; post[t][1] = r;
                L += l; R += r;
            }
        }
        inputs(kMaster, L, R);
        if (m) {
            for (int t = 0; t < tracks; t++) {
                m->in[t] = std::max(m->in[t], std::max(std::fabs(sai[t][0]), std::fabs(sai[t][1])));
                m->ret[t] = std::max(m->ret[t], std::max(std::fabs(ret[t][0]), std::fabs(ret[t][1])));
                m->peak[t] = std::max(m->peak[t], std::max(std::fabs(post[t][0]), std::fabs(post[t][1])));
                m->sq[t] += 0.5 * (post[t][0] * post[t][0] + post[t][1] * post[t][1]);
            }
            for (int u = 0; u < g.units; u++) m->unit[u] = std::max(m->unit[u], std::max(std::fabs(uo[u][0]), std::fabs(uo[u][1])));
        }
        master.run(L, R, k);
        if (m) {
            for (int u = 0; u < g.units; u++) m->gr[u] = unit[u].gr_db();
            m->mpk[0] = std::max(m->mpk[0], std::fabs(L)); m->mpk[1] = std::max(m->mpk[1], std::fabs(R));
            m->msq[0] += L * L; m->msq[1] += R * R;
            m->mgr = master.comp.gr;
            m->lgr = master.lim.gr_db();
            m->frames++;
        }
    }
};

}  // namespace dz::mix
