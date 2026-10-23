#include "notes.hpp"

#include <algorithm>
#include <cmath>

namespace dz {

namespace {
constexpr double kPi = 3.14159265358979323846;
constexpr int kLowMidi = 40, kHighMidi = 88;           // E2 .. E6, candidate fundamentals
constexpr int kBassTop = 52, kTrebleBottom = 67;       // registers: [40,52) bass, [52,67) middle, [67,89) treble
constexpr float kRegister = 0.4f, kGlobal = 0.15f;     // strong within its register / against the frame's best (0.4: a G2 heard at 75 phon would hide the B2 beside it at 0.5)
constexpr float kFloorDb = 30, kFadeDbPerSecond = 21.5f;
constexpr float kAbsoluteFloorDb = -10;   // ~-70 dBFS for a full-scale sine (|X| = n/4 at 4096): silence
constexpr int kMaxPeaks = 256;
constexpr double kPhon = 75;   // music at playing level (ISO 226); A-weighting (the 40-phon contour) cost RWC-Pop 3.9 points

// ISO 226:2003 equal-loudness contours: the level (dB SPL) at f that sounds as loud as `phon` at 1 kHz
double iso226(double f, double phon) {
    static const double fr[] = {20, 25, 31.5, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800,
                                1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500};
    static const double af[] = {0.532, 0.506, 0.480, 0.455, 0.432, 0.409, 0.387, 0.367, 0.349, 0.330, 0.315, 0.301, 0.288, 0.276, 0.267,
                                0.259, 0.253, 0.250, 0.246, 0.244, 0.243, 0.243, 0.243, 0.242, 0.242, 0.245, 0.254, 0.271, 0.301};
    static const double lu[] = {-31.6, -27.2, -23.0, -19.1, -15.9, -13.0, -10.3, -8.1, -6.2, -4.5, -3.1, -2.0, -1.1, -0.4, 0.0,
                                0.3, 0.5, 0.0, -2.7, -4.1, -1.0, 1.7, 2.5, 1.2, -2.1, -7.1, -11.2, -10.7, -3.1};
    static const double tf[] = {78.5, 68.7, 59.5, 51.1, 44.0, 37.5, 31.5, 26.5, 22.1, 17.9, 14.4, 11.4, 8.6, 6.2, 4.4,
                                3.0, 2.2, 2.4, 3.5, 1.7, -1.3, -4.2, -6.0, -5.4, -1.5, 6.0, 12.6, 13.9, 12.3};
    auto lp = [&](int i) {
        const double a = 4.47e-3 * (std::pow(10.0, 0.025 * phon) - 1.15) + std::pow(0.4 * std::pow(10.0, (tf[i] + lu[i]) / 10 - 9), af[i]);
        return 10 / af[i] * std::log10(a) - lu[i] + 94;
    };
    f = std::clamp(f, fr[0], fr[28]);
    int i = 0;
    while (i < 27 && fr[i + 1] < f) i++;
    const double x = std::log(f / fr[i]) / std::log(fr[i + 1] / fr[i]);
    return lp(i) + x * (lp(i + 1) - lp(i));
}
}  // namespace

// ---------------------------------------------------------------- notes

void NoteFrontEnd::init(double rate) {
    rate_ = rate;
    n_ = 1;
    while (n_ * 2 <= rate * 0.19) n_ *= 2;   // ~180 ms (4096 at 22-24 kHz)
    ring_.assign(size_t(n_), 0.f);
    pos_ = 0;
    window_.resize(size_t(n_));
    for (int i = 0; i < n_; i++) window_[size_t(i)] = float(0.5 - 0.5 * std::cos(2 * kPi * i / n_));
    const int bins = n_ / 2 + 1;
    loudw_.resize(size_t(bins));

    bark_.resize(size_t(bins));
    db_.resize(size_t(bins));
    for (int k = 0; k < bins; k++) {
        const double f = std::max(1.0, k * rate / n_);
        loudw_[size_t(k)] = float(iso226(1000, kPhon) - iso226(f, kPhon) + 2.0);
        bark_[size_t(k)] = float(13 * std::atan(0.00076 * f) + 3.5 * std::atan((f / 7500) * (f / 7500)));
    }
    buf_.resize(size_t(n_));
    twiddle_.resize(size_t(n_ / 2));
    for (int i = 0; i < n_ / 2; i++) twiddle_[size_t(i)] = std::polar(1.f, float(-2 * kPi * i / n_));
    bitrev_.resize(size_t(n_));
    for (int i = 0, j = 0; i < n_; i++) {
        bitrev_[size_t(i)] = j;
        int bit = n_ >> 1;
        for (; j & bit; bit >>= 1) j ^= bit;
        j |= bit;
    }
    runMax_ = -200;
    std::fill(last_, last_ + 128, false);
}

void NoteFrontEnd::push(const float* x, uint32_t n) {
    for (uint32_t i = 0; i < n; i++) {
        ring_[size_t(pos_)] = x[i];
        pos_ = (pos_ + 1) % n_;
    }
}

void NoteFrontEnd::analyse(double hopSeconds, NoteFrame& out) {
    out = {};
    // windowed FFT of the newest n_ samples (oldest first), bit-reversed in place, radix 2
    for (int i = 0; i < n_; i++) buf_[size_t(bitrev_[size_t(i)])] = {ring_[size_t((pos_ + i) % n_)] * window_[size_t(i)], 0.f};
    for (int size = 2; size <= n_; size <<= 1) {
        const int half = size / 2, step = n_ / size;
        for (int s = 0; s < n_; s += size)
            for (int k = 0; k < half; k++) {
                auto u = buf_[size_t(s + k)], v = buf_[size_t(s + k + half)] * twiddle_[size_t(k * step)];
                buf_[size_t(s + k)] = u + v;
                buf_[size_t(s + k + half)] = u - v;
            }
    }
    const int bins = n_ / 2 + 1;
    float top = -200;
    for (int k = 0; k < bins; k++) {
        db_[size_t(k)] = 10 * std::log10(std::norm(buf_[size_t(k)]) + 1e-12f) + loudw_[size_t(k)];
        top = std::max(top, db_[size_t(k)]);
    }
    runMax_ = std::max(top, runMax_ - kFadeDbPerSecond * float(hopSeconds));
    bool now[128]{};
    if (top >= runMax_ - kFloorDb && top > kAbsoluteFloorDb) {
        out.audible = true;
        // peaks 60 Hz .. 5 kHz within 60 dB of the loudest
        int peak[kMaxPeaks], np = 0;
        const int k0 = std::max(1, int(60.0 * n_ / rate_)), k1 = std::min(bins - 2, int(5000.0 * n_ / rate_));
        for (int k = k0; k <= k1 && np < kMaxPeaks; k++)
            if (db_[size_t(k)] > db_[size_t(k - 1)] && db_[size_t(k)] >= db_[size_t(k + 1)] && db_[size_t(k)] > top - 60) peak[np++] = k;
        // masking: a peak counts only above what every other peak spreads over it (Schroeder, -10 dB)
        float freq[kMaxPeaks], loud[kMaxPeaks];
        int na = 0;
        for (int i = 0; i < np; i++) {
            const int k = peak[i];
            float mask = -1e9f;
            for (int j = 0; j < np; j++) {
                if (j == i) continue;
                const float dz = bark_[size_t(k)] - bark_[size_t(peak[j])] + 0.474f;
                mask = std::max(mask, db_[size_t(peak[j])] + 15.81f + 7.5f * dz - 17.5f * std::sqrt(1 + dz * dz) - 10);
            }
            if (db_[size_t(k)] <= mask) continue;
            const float a = db_[size_t(k - 1)], b = db_[size_t(k)], c = db_[size_t(k + 1)];
            const float p = 0.5f * (a - c) / (a - 2 * b + c + 1e-12f);
            freq[na] = float((k + p) * rate_ / n_);
            loud[na] = std::pow(10.f, 0.03f * (b - top));   // loudness ~ intensity^0.3, relative
            na++;
        }
        // virtual pitch: subharmonic summation, the best note taken and its partials removed, kMax times
        bool alive[kMaxPeaks];
        std::fill(alive, alive + na, true);
        int found[NoteFrame::kMax];
        float sal[NoteFrame::kMax];
        int nf = 0;
        // salience of m from the peaks still unexplained; lowest: the partial number it must be heard at
        auto salience = [&](int m, float lowest) {
            const float f0 = 440.f * std::pow(2.f, (m - 69) / 12.f);
            float s = 0;
            bool low = false;
            for (int i = 0; i < na; i++) {
                if (!alive[i]) continue;
                const float h = freq[i] / f0, hn = std::round(h);
                if (hn < 1 || hn > 10 || std::fabs(1200 * std::log2(h / hn)) >= 35) continue;
                s += loud[i] / std::sqrt(hn);
                low |= hn <= lowest;
            }
            return low ? s : 0.f;
        };
        auto best = [&](int lo, int hi, float lowest, float& bs) {
            int bm = -1;
            bs = 0;
            for (int m = lo; m < hi; m++)
                if (const float s = salience(m, lowest); s > bs) bs = s, bm = m;
            return bm;
        };
        // The bass apart, on every peak: a listener follows the lowest voice on its own, and in the
        // greedy search below an octave above it (G3 over G2, their shared partials beating with D3's)
        // can win and take its partials away. Its fundamental must be heard (no octave-below guess).
        float bassSal;
        const int bass = best(kLowMidi, kBassTop, 1, bassSal);
        while (nf < NoteFrame::kMax) {   // the rest: its fundamental or octave heard, no missing-fundamental guess
            float bs;
            const int bm = best(kLowMidi, kHighMidi + 1, 2, bs);
            if (bm < 0) break;
            found[nf] = bm;
            sal[nf++] = bs;
            const float f0 = 440.f * std::pow(2.f, (bm - 69) / 12.f);
            for (int i = 0; i < na; i++) {
                const float h = freq[i] / f0, hn = std::round(h);
                if (hn >= 1 && hn <= 10 && std::fabs(1200 * std::log2(h / hn)) < 35) alive[i] = false;
            }
        }
        if (bass >= 0 && std::find(found, found + nf, bass) == found + nf) {   // missed: it takes the last slot
            if (nf == NoteFrame::kMax) nf--;
            found[nf] = bass;
            sal[nf++] = bassSal;
        }
        float gmax = 0;
        for (int i = 0; i < nf; i++) gmax = std::max(gmax, sal[i]);
        for (int r = 0; r < 3; r++) {
            const int lo = r == 0 ? kLowMidi : r == 1 ? kBassTop : kTrebleBottom, hi = r == 0 ? kBassTop : r == 1 ? kTrebleBottom : kHighMidi + 1;
            float rmax = 0;
            for (int i = 0; i < nf; i++) if (found[i] >= lo && found[i] < hi) rmax = std::max(rmax, sal[i]);
            for (int i = 0; i < nf; i++)
                if (found[i] >= lo && found[i] < hi && sal[i] >= kRegister * rmax && sal[i] >= kGlobal * gmax) now[found[i]] = true;
        }
    }
    // heard for two hops
    for (int m = kLowMidi; m <= kHighMidi && out.count < NoteFrame::kMax; m++)
        if (now[m] && last_[m]) out.midi[out.count++] = int8_t(m);
    std::copy(now, now + 128, last_);
}

// ---------------------------------------------------------------- chords

const ChordQuality ChordHmm::kQuality[kQualities] = {ChordQuality::Major, ChordQuality::Minor, ChordQuality::Dom7, ChordQuality::Maj7,
                                                     ChordQuality::Min7, ChordQuality::Diminished, ChordQuality::Sus4, ChordQuality::Sus2,
                                                     ChordQuality::Power, ChordQuality::Add9};

namespace {
constexpr int8_t kIv[ChordHmm::kQualities][4] = {{0, 4, 7, -1}, {0, 3, 7, -1}, {0, 4, 7, 10}, {0, 4, 7, 11}, {0, 3, 7, 10},
                                                 {0, 3, 6, -1}, {0, 5, 7, -1}, {0, 2, 7, -1}, {0, 7, -1, -1}, {0, 4, 7, 2}};
constexpr float kPrior[ChordHmm::kQualities] = {0, 0, -0.7f, -1.2f, -1.0f, -1.5f, -1.5f, -1.5f, -2.5f, -1.0f};   // simpler is likelier
// Params: evidence 0.2 per hop measured best (tools/loopback): at 1, two changes in a row (C, then
// the old notes and the new together, then G) are cheap.
// a melody-register note counts fully once it has lasted kSustain (a passing note is short) and
// weighs kTrebleWeight; lower notes count at once (a strummed change is sharp)
constexpr float kSustain = 0.4f, kTrebleWeight = 0.5f;
const float kLogIn = std::log(0.9f), kLogOut = std::log(0.08f);

uint16_t mask_of(int state) {
    uint16_t m = 0;
    const int r = ChordHmm::root(state), q = state % ChordHmm::kQualities;
    for (int8_t iv : kIv[q]) if (iv >= 0) m |= uint16_t(1u << ((r + iv) % 12));
    return m;
}
}  // namespace

ChordHmm::ChordHmm(double hopSeconds, Params p)
    : hop_(hopSeconds), p_(p), lag_(std::max(1, int(std::lround(p.lag / hopSeconds)))), back_(size_t(lag_) * kStates) {
    std::fill(keyLp_, keyLp_ + 24, -std::log(24.f));
    reset();
}

namespace {
// P(chord | key): diatonic chords by how often each degree is played, the rest rare. Keys 0-11
// major, 12-23 minor (harmonic: its V major).
float key_lik(int key, int state) {
    static float t[24][ChordHmm::kStates];
    static bool init = false;
    if (!init) {
        static const int major[7] = {0, 2, 4, 5, 7, 9, 11}, minor[7] = {0, 2, 3, 5, 7, 8, 10};
        for (int k = 0; k < 24; k++) {
            const int tonic = k % 12;
            const bool mi = k >= 12;
            uint16_t sc = 0;
            for (int i = 0; i < 7; i++) sc |= uint16_t(1u << ((tonic + (mi ? minor[i] : major[i])) % 12));
            if (mi) sc |= uint16_t(1u << ((tonic + 11) % 12));
            for (int s = 0; s < ChordHmm::kStates; s++) {
                const int d = (ChordHmm::root(s) - tonic + 12) % 12;
                const bool inside = (mask_of(s) & ~sc) == 0;
                float w;
                if (!mi) w = d == 0 ? 1.f : d == 7 ? .9f : d == 5 ? .8f : d == 9 ? .6f : d == 2 ? .5f : d == 4 ? .35f : .1f;
                else w = d == 0 ? 1.f : d == 7 ? .8f : d == 5 ? .7f : d == 8 ? .6f : d == 3 ? .5f : d == 10 ? .5f : .1f;
                t[k][s] = inside ? w : 0.08f;
            }
        }
        init = true;
    }
    return t[key][state];
}
}  // namespace

// The key heard, as a listener sums it up from a few chords: each second of a decided chord is one
// observation of P(chord | key); some belief drifts back to every key (a modulation). Its weight on
// the chords is mild: at 1 or more it locks onto a wrong key and feeds itself (RWC-Pop -8 to -23
// points). At 0.3 against the fixed diatonic bonus on the estimated key, which came only after
// ~10 s: GuitarSet +1.3 points, RWC-Pop even, fewer changes (x1.63 -> x1.51) (tools/eval, 2026-10-23).
constexpr float kKeyWeight = 0.3f, kKeyModulationPerSecond = 0.1f;

void ChordHmm::key_update() {
    if (decided_ >= 0) {
        const float w = float(hop_);
        for (int k = 0; k < 24; k++) keyLp_[k] += w * std::log(key_lik(k, decided_));
    }
    float mx = -1e30f;
    for (float l : keyLp_) mx = std::max(mx, l);
    float p[24], sum = 0;
    for (int k = 0; k < 24; k++) sum += p[k] = std::exp(keyLp_[k] - mx);
    const float m = kKeyModulationPerSecond * float(hop_);
    for (int k = 0; k < 24; k++) p[k] = (1 - m) * p[k] / sum + m / 24, keyLp_[k] = std::log(p[k]);
    for (int s = 0; s < kStates; s++) {
        float l = 0;
        for (int k = 0; k < 24; k++) l += p[k] * key_lik(k, s);
        keyBonus_[s] = std::log(l);
    }
}

void ChordHmm::reset() {
    std::fill(mem_, mem_ + 12, 0.f);
    std::fill(age_, age_ + 128, 0.f);
    for (auto& r : reg_) std::fill(r, r + 12, 0.f);
    lastBass_ = -1;
    std::fill(alpha_, alpha_ + kStates, 0.f);
    filled_ = row_ = 0;
    best_ = decided_ = -1;
    heard_ = false;
}

// The harmony's emission for a full band (GeneralChords), learned on RWC-Pop by tools/eval/learn.py
// (RWC Music Database, CC BY-NC 4.0; rwc-annotations). Against the hand-set weights it gained
// 6.5 points on the songs it was not trained on (2026-10-23); on a solo guitar it did not.
// tools/eval/learn.py rwc-pop: major then minor; 9 x 12 memory weights (register x time scale, by interval), 12 bass, 1 bias
const float ChordHmm::kBandEmission[2 * 121] = {0.575f, -0.728f, 1.135f, -0.985f, 1.915f, -0.974f, -0.596f, 0.952f, -0.670f, 0.335f, -0.854f, -0.152f, 2.317f, -0.101f, -2.387f, -0.499f, -1.302f, -1.402f, -0.461f, -0.348f, -0.574f, -1.436f, 0.637f, -1.142f, 2.268f, -2.249f, 0.587f, -1.584f, -0.228f, -0.308f, -1.504f, 1.375f, -2.159f, -1.146f, -1.211f, -0.710f, 0.764f, -0.892f, 0.531f, -1.459f, 0.635f, -0.370f, -0.456f, 0.720f, -1.213f, 0.616f, -0.080f, -0.115f, 1.256f, 0.068f, -1.475f, 1.090f, 0.992f, -0.773f, -0.139f, 0.736f, 0.058f, -2.040f, 0.743f, -0.142f, 0.824f, -2.084f, 1.142f, -3.241f, 1.384f, -0.698f, -1.296f, 2.031f, -1.939f, 0.933f, -1.208f, -0.215f, 0.957f, -0.953f, 0.420f, -1.399f, 1.303f, -0.722f, -0.350f, 1.113f, -1.215f, 0.032f, -0.563f, -0.171f, 0.651f, -0.684f, -0.105f, -0.363f, 0.355f, -0.309f, -1.002f, 0.823f, -0.117f, -0.832f, 0.126f, 0.134f, 0.243f, -0.821f, -0.032f, -0.364f, 0.945f, 0.307f, -0.077f, 1.044f, -0.778f, -0.343f, -0.008f, -0.360f, 0.776f, -0.659f, -0.128f, -0.152f, 0.181f, -0.129f, -0.363f, 0.223f, -0.568f, -0.152f, 0.044f, -0.277f, 1.572f, 0.184f, -1.111f, -0.304f, 0.835f, -0.414f, 0.067f, -0.521f, 0.575f, -1.104f, -0.666f, 0.255f, -0.884f, 2.268f, 0.081f, -1.701f, -1.709f, -0.159f, -3.219f, -0.645f, -0.145f, -0.605f, 0.016f, -1.456f, -0.747f, 3.405f, -1.793f, -0.136f, 1.086f, -2.561f, 0.678f, -1.429f, 0.716f, -1.574f, -2.460f, 0.610f, -1.103f, 0.573f, -1.097f, -0.565f, 1.026f, -1.857f, 0.544f, -0.581f, 0.249f, -0.060f, 0.118f, 0.975f, -0.400f, 0.320f, -0.105f, -0.123f, 0.973f, 0.260f, -2.142f, 0.058f, 0.672f, -2.032f, -1.079f, -0.359f, -0.888f, 1.783f, -1.559f, 0.045f, 0.884f, -1.950f, 1.412f, -2.286f, 2.146f, -0.190f, -0.912f, 1.748f, -0.880f, 1.137f, -1.113f, -0.451f, 0.907f, -1.093f, -0.175f, -1.026f, 1.353f, -0.823f, -0.596f, 0.540f, -0.806f, 0.114f, -0.397f, 0.421f, 0.714f, -0.807f, 0.109f, -0.271f, 0.714f, -0.797f, -0.276f, 0.686f, -0.572f, 0.221f, -0.961f, -0.386f, 2.253f, -0.158f, -0.136f, -0.527f, 0.058f, -0.036f, -1.259f, 0.651f, -0.658f, 0.705f, -0.849f, -0.275f, 0.102f, -0.622f, 0.018f, -0.859f, 0.056f, -0.407f, -0.552f, -0.291f, -0.664f, -1.572f};

void ChordHmm::evidence(const NoteFrame& notes, bool keyOn, int keyTonic, bool keyMinor, float* e) {
    // memory of the notes heard: recency, sustain, register
    const float decay = std::exp(-float(hop_) / p_.tau);
    for (float& v : mem_) v *= decay;
    bool on[128]{};
    for (int i = 0; i < notes.count; i++) on[notes.midi[i]] = true;
    for (int m = 0; m < 128; m++) age_[m] = on[m] ? age_[m] + float(hop_) : 0.f;
    // A new bass note that was not sounding (the change's downbeat): what came before mostly goes,
    // or the old chord's notes and the new one's add up to a chord in between (C then G: Em7).
    if (notes.count && notes.midi[0] % 12 != lastBass_ && mem_[notes.midi[0] % 12] < 0.3f && age_[notes.midi[0]] <= float(hop_) * 1.5f)
        for (float& v : mem_) v *= p_.newBass;
    if (notes.count) lastBass_ = notes.midi[0] % 12;
    for (int i = 0; i < notes.count; i++) {
        const int m = notes.midi[i];
        const float w = m < 67 ? 1.f : std::min(1.f, age_[m] / kSustain) * kTrebleWeight;
        mem_[m % 12] = std::max(mem_[m % 12], w);
    }
    heard_ |= notes.count > 0;
    if (p_.emission) {   // the learned emission's input: memory per register (bass, middle, treble) at 0.1, 0.4, 1.5 s
        static const float taus[3] = {0.1f, 0.4f, 1.5f};
        for (int f = 0; f < 9; f++) {
            const float d = std::exp(-float(hop_) / taus[f % 3]);
            for (float& x : reg_[f]) x *= d;
        }
        for (int i = 0; i < notes.count; i++) {
            const int m = notes.midi[i], g = m < 52 ? 0 : m < 67 ? 1 : 2;
            for (int k = 0; k < 3; k++) reg_[g * 3 + k][m % 12] = 1;
        }
    }
    const int bass = notes.count > 0 ? notes.midi[0] % 12 : -1;
    float mx = 0;
    for (float v : mem_) mx = std::max(mx, v);
    uint16_t scale = 0;
    if (keyOn) {
        static const int major[7] = {0, 2, 4, 5, 7, 9, 11}, minor[8] = {0, 2, 3, 5, 7, 8, 10, 11};   // minor with its leading tone
        for (int i = 0; i < (keyMinor ? 8 : 7); i++) scale |= uint16_t(1u << ((keyTonic + (keyMinor ? minor[i] : major[i])) % 12));
    }
    for (int s = 0; s < kStates; s++) {
        float v = 0;
        if (mx > 0.05f) {
            const uint16_t m = mask_of(s);
            int size = 0;
            float inSum = 0;
            for (int p = 0; p < 12; p++) {
                const bool in = m >> p & 1;
                size += in;
                if (in) inSum += mem_[p];
                v += mem_[p] * (in ? kLogIn : kLogOut);
            }
            v += 0.4f * (inSum / float(size) - 1);   // completeness: tones left unheard cost a little
            if (bass >= 0) v += (m >> bass & 1) ? (root(s) == bass ? 0.3f : 0.f) : -1.f;
            if (p_.emission && s % kQualities <= 1) {   // learned: replaces the hand-set terms above
                const float* q = p_.emission + (s % kQualities) * 121;
                const int r = root(s);
                v = q[120];
                for (int f = 0; f < 9; f++)
                    for (int i = 0; i < 12; i++) v += q[f * 12 + i] * reg_[f][(r + i) % 12];
                if (bass >= 0) v += q[108 + (bass - r + 12) % 12];
            }
            if (keyOn) v += (m & ~scale) == 0 ? 0.3f : 0.f;   // diatonic in the session's key
            else v += kKeyWeight * keyBonus_[s];
        }
        e[s] = p_.evidence * v + kPrior[s % kQualities] * float(hop_) * 5;
        if (p_.triads && s % kQualities > 1) e[s] = -1e9f;   // only major and minor triads
    }
}

void ChordHmm::step(const NoteFrame& notes, bool keyOn, int keyTonic, bool keyMinor) {
    float e[kStates];
    if (!keyOn) key_update();
    evidence(notes, keyOn, keyTonic, keyMinor, e);
    // Viterbi step: stay, or move from the best state
    int prevBest = 0;
    for (int s = 1; s < kStates; s++) if (alpha_[s] > alpha_[prevBest]) prevBest = s;
    const float logStay = std::log(p_.stay), logMove = std::log((1 - p_.stay) / (kStates - 1));
    uint8_t* back = &back_[size_t(row_) * kStates];
    float top = -1e30f;
    for (int s = 0; s < kStates; s++) {
        const float stay = alpha_[s] + logStay, move = alpha_[prevBest] + logMove;
        back[s] = uint8_t(stay >= move ? s : prevBest);
        alpha_[s] = std::max(stay, move) + e[s];
        top = std::max(top, alpha_[s]);
    }
    for (float& a : alpha_) a -= top;
    row_ = (row_ + 1) % lag_;
    filled_ = std::min(filled_ + 1, lag_);
    if (!heard_) return;
    best_ = 0;
    for (int s = 1; s < kStates; s++) if (alpha_[s] > alpha_[best_]) best_ = s;
    // fixed lag: backtrack from today's best through the last lag_ rows
    if (filled_ == lag_) {
        int s = best_;
        for (int k = 0; k < lag_; k++) s = back_[size_t((row_ - 1 - k + 2 * lag_) % lag_) * kStates + size_t(s)];
        decided_ = s;
    }
}

std::vector<int> ChordHmm::decode(const std::vector<NoteFrame>& frames, double hopSeconds, Params p, bool keyOn, int keyTonic, bool keyMinor) {
    ChordHmm h(hopSeconds, p);
    std::vector<uint8_t> back(frames.size() * kStates);
    float alpha[kStates]{}, e[kStates];
    const float logStay = std::log(p.stay), logMove = std::log((1 - p.stay) / (kStates - 1));
    for (size_t i = 0; i < frames.size(); i++) {
        h.evidence(frames[i], keyOn, keyTonic, keyMinor, e);
        int prevBest = 0;
        for (int s = 1; s < kStates; s++) if (alpha[s] > alpha[prevBest]) prevBest = s;
        float top = -1e30f;
        for (int s = 0; s < kStates; s++) {
            const float stay = alpha[s] + logStay, move = alpha[prevBest] + logMove;
            back[i * kStates + size_t(s)] = uint8_t(stay >= move ? s : prevBest);
            alpha[s] = std::max(stay, move) + e[s];
            top = std::max(top, alpha[s]);
        }
        for (float& a : alpha) a -= top;
    }
    std::vector<int> path(frames.size());
    int s = int(std::max_element(alpha, alpha + kStates) - alpha);
    for (size_t i = frames.size(); i-- > 0;) {
        path[i] = s;
        s = back[i * kStates + size_t(s)];
    }
    return path;
}

}  // namespace dz
