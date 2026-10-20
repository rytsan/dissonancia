#include "notes.hpp"

#include <algorithm>
#include <cmath>

namespace dz {

namespace {
constexpr double kPi = 3.14159265358979323846;
constexpr int kLowMidi = 40, kHighMidi = 88;           // E2 .. E6, candidate fundamentals
constexpr int kBassTop = 52, kTrebleBottom = 67;       // registers: [40,52) bass, [52,67) middle, [67,89) treble
constexpr float kRegister = 0.5f, kGlobal = 0.15f;     // strong within its register / against the frame's best
constexpr float kFloorDb = 30, kFadeDbPerSecond = 21.5f;
constexpr int kMaxPeaks = 256;
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
    aweight_.resize(size_t(bins));
    bark_.resize(size_t(bins));
    db_.resize(size_t(bins));
    for (int k = 0; k < bins; k++) {
        double f = std::max(1.0, k * rate / n_), f2 = f * f;
        double ra = 12194.0 * 12194 * f2 * f2 / ((f2 + 20.6 * 20.6) * std::sqrt((f2 + 107.7 * 107.7) * (f2 + 737.9 * 737.9)) * (f2 + 12194.0 * 12194));
        aweight_[size_t(k)] = float(20 * std::log10(std::max(ra, 1e-9)) + 2.0);
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
        db_[size_t(k)] = 10 * std::log10(std::norm(buf_[size_t(k)]) + 1e-12f) + aweight_[size_t(k)];
        top = std::max(top, db_[size_t(k)]);
    }
    runMax_ = std::max(top, runMax_ - kFadeDbPerSecond * float(hopSeconds));
    bool now[128]{};
    if (top >= runMax_ - kFloorDb) {
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
        // virtual pitch: subharmonic summation, three times, each note's partials removed
        bool alive[kMaxPeaks];
        std::fill(alive, alive + na, true);
        int found[NoteFrame::kMax];
        float sal[NoteFrame::kMax];
        int nf = 0;
        for (int it = 0; it < NoteFrame::kMax; it++) {
            float best = 0;
            int bm = -1;
            for (int m = kLowMidi; m <= kHighMidi; m++) {
                const float f0 = 440.f * std::pow(2.f, (m - 69) / 12.f);
                float s = 0;
                bool low = false;
                for (int i = 0; i < na; i++) {
                    if (!alive[i]) continue;
                    const float h = freq[i] / f0, hn = std::round(h);
                    if (hn < 1 || hn > 10 || std::fabs(1200 * std::log2(h / hn)) >= 35) continue;
                    s += loud[i] / std::sqrt(hn);
                    low |= hn <= 2;   // its fundamental or octave heard: no missing-fundamental guess
                }
                if (low && s > best) best = s, bm = m;
            }
            if (bm < 0) break;
            found[nf] = bm;
            sal[nf++] = best;
            const float f0 = 440.f * std::pow(2.f, (bm - 69) / 12.f);
            for (int i = 0; i < na; i++) {
                const float h = freq[i] / f0, hn = std::round(h);
                if (hn >= 1 && hn <= 10 && std::fabs(1200 * std::log2(h / hn)) < 35) alive[i] = false;
            }
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
                                                     ChordQuality::Power};

namespace {
constexpr int8_t kIv[ChordHmm::kQualities][4] = {{0, 4, 7, -1}, {0, 3, 7, -1}, {0, 4, 7, 10}, {0, 4, 7, 11}, {0, 3, 7, 10},
                                                 {0, 3, 6, -1}, {0, 5, 7, -1}, {0, 2, 7, -1}, {0, 7, -1, -1}};
constexpr float kPrior[ChordHmm::kQualities] = {0, 0, -0.7f, -1.2f, -1.0f, -1.5f, -1.5f, -1.5f, -2.5f};   // simpler is likelier
// Per-hop weight of the notes' evidence against the cost of a change: at 1, two changes in a row
// (C, then the old notes and the new together, then G) are cheap; 0.2 measured best (tools/loopback).
constexpr float kEvidence = 0.2f;
constexpr float kNewBass = 0.2f;              // what remains of the old notes when a new bass arrives
constexpr float kStay = 0.99f;                 // per hop (20 ms): a chord lasts ~2 s on average
// memory of a note (s); a melody-register note counts fully once it has lasted kSustain (a passing
// note is short) and weighs kTrebleWeight; lower notes count at once (a strummed change is sharp)
constexpr float kTau = 0.3f, kSustain = 0.4f, kTrebleWeight = 0.5f;
const float kLogIn = std::log(0.9f), kLogOut = std::log(0.08f);

uint16_t mask_of(int state) {
    uint16_t m = 0;
    const int r = ChordHmm::root(state), q = state % ChordHmm::kQualities;
    for (int8_t iv : kIv[q]) if (iv >= 0) m |= uint16_t(1u << ((r + iv) % 12));
    return m;
}
}  // namespace

ChordHmm::ChordHmm(double hopSeconds, double lagSeconds)
    : hop_(hopSeconds), lag_(std::max(1, int(std::lround(lagSeconds / hopSeconds)))), back_(size_t(lag_) * kStates) {
    reset();
}

void ChordHmm::reset() {
    std::fill(mem_, mem_ + 12, 0.f);
    std::fill(age_, age_ + 128, 0.f);
    lastBass_ = -1;
    std::fill(alpha_, alpha_ + kStates, 0.f);
    filled_ = row_ = 0;
    best_ = decided_ = -1;
    heard_ = false;
}

void ChordHmm::step(const NoteFrame& notes, bool keyOn, int keyTonic, bool keyMinor) {
    // memory of the notes heard: recency, sustain, register
    const float decay = std::exp(-float(hop_) / kTau);
    for (float& v : mem_) v *= decay;
    bool on[128]{};
    for (int i = 0; i < notes.count; i++) on[notes.midi[i]] = true;
    for (int m = 0; m < 128; m++) age_[m] = on[m] ? age_[m] + float(hop_) : 0.f;
    // A new bass note that was not sounding (the change's downbeat): what came before mostly goes,
    // or the old chord's notes and the new one's add up to a chord in between (C then G: Em7).
    if (notes.count && notes.midi[0] % 12 != lastBass_ && mem_[notes.midi[0] % 12] < 0.3f && age_[notes.midi[0]] <= float(hop_) * 1.5f)
        for (float& v : mem_) v *= kNewBass;
    if (notes.count) lastBass_ = notes.midi[0] % 12;
    for (int i = 0; i < notes.count; i++) {
        const int m = notes.midi[i];
        const float w = m < 67 ? 1.f : std::min(1.f, age_[m] / kSustain) * kTrebleWeight;
        mem_[m % 12] = std::max(mem_[m % 12], w);
    }
    heard_ |= notes.count > 0;
    const int bass = notes.count > 0 ? notes.midi[0] % 12 : -1;
    float mx = 0;
    for (float v : mem_) mx = std::max(mx, v);
    uint16_t scale = 0;
    if (keyOn) {
        static const int major[7] = {0, 2, 4, 5, 7, 9, 11}, minor[8] = {0, 2, 3, 5, 7, 8, 10, 11};   // minor with its leading tone
        for (int i = 0; i < (keyMinor ? 8 : 7); i++) scale |= uint16_t(1u << ((keyTonic + (keyMinor ? minor[i] : major[i])) % 12));
    }
    // Viterbi step: stay, or move from the best state
    int prevBest = 0;
    for (int s = 1; s < kStates; s++) if (alpha_[s] > alpha_[prevBest]) prevBest = s;
    const float logStay = std::log(kStay), logMove = std::log((1 - kStay) / (kStates - 1));
    uint8_t* back = &back_[size_t(row_) * kStates];
    float top = -1e30f;
    for (int s = 0; s < kStates; s++) {
        const float stay = alpha_[s] + logStay, move = alpha_[prevBest] + logMove;
        back[s] = uint8_t(stay >= move ? s : prevBest);
        float e = 0;
        if (mx > 0.05f) {
            const uint16_t m = mask_of(s);
            int size = 0;
            float inSum = 0;
            for (int p = 0; p < 12; p++) {
                const bool in = m >> p & 1;
                size += in;
                if (in) inSum += mem_[p];
                e += mem_[p] * (in ? kLogIn : kLogOut);
            }
            e += 0.4f * (inSum / float(size) - 1);   // completeness: tones left unheard cost a little
            if (bass >= 0) e += (m >> bass & 1) ? (root(s) == bass ? 0.3f : 0.f) : -1.f;
            if (keyOn && (m & ~scale) == 0) e += 0.3f;   // diatonic
        }
        alpha_[s] = std::max(stay, move) + kEvidence * e + kPrior[s % kQualities] * float(hop_) * 5;
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

}  // namespace dz
