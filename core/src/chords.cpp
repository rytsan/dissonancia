#include "chords.hpp"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <iterator>

namespace dz {

namespace {

struct QualityDef {
    ChordQuality q;
    const char* suffix;
    int n;
    int8_t iv[4];   // intervals from the root, in semitones (mod 12)
};

// Order matters for exact ties: simpler / more common spellings first (C6 vs Am7 still flagged).
constexpr QualityDef kDefs[ChordMatcher::kQualities] = {
    {ChordQuality::Major, "", 3, {0, 4, 7}},       {ChordQuality::Minor, "m", 3, {0, 3, 7}},
    {ChordQuality::Dom7, "7", 4, {0, 4, 7, 10}},   {ChordQuality::Min7, "m7", 4, {0, 3, 7, 10}},
    {ChordQuality::Maj7, "maj7", 4, {0, 4, 7, 11}}, {ChordQuality::Power, "5", 2, {0, 7}},
    {ChordQuality::Sus4, "sus4", 3, {0, 5, 7}},    {ChordQuality::Sus2, "sus2", 3, {0, 2, 7}},
    {ChordQuality::Maj6, "6", 4, {0, 4, 7, 9}},    {ChordQuality::Min6, "m6", 4, {0, 3, 7, 9}},
    {ChordQuality::Diminished, "dim", 3, {0, 3, 6}}, {ChordQuality::HalfDim7, "m7b5", 4, {0, 3, 6, 10}},
    {ChordQuality::Dim7, "dim7", 4, {0, 3, 6, 9}}, {ChordQuality::Augmented, "aug", 3, {0, 4, 8}},
    {ChordQuality::Add9, "add9", 4, {0, 4, 7, 2}},
};

const QualityDef& def(ChordQuality q) {
    for (const auto& d : kDefs)
        if (d.q == q) return d;
    return kDefs[0];
}

// Amplitude a single note puts on pitch classes relative to itself (partials 1..6, 1/h).
constexpr float kContextWeight = 0.06f;

constexpr float kPartialPc[12] = {1.0f + 0.5f + 0.25f, 0, 0, 0, 0.2f, 0, 0, 0.33f + 0.17f, 0, 0, 0, 0};

bool contains(const int8_t* a, int n, int v) {
    for (int i = 0; i < n; i++)
        if (a[i] == v) return true;
    return false;
}

}  // namespace

// ---------------------------------------------------------------- matcher

ChordMatcher::ChordMatcher(const SessionConfig& s)
    : keySet_(s.keySet != 0), fifths_(s.keyFifths), minor_(s.keyMode != KeyMode::Major),
      tonic_((7 * (s.keyFifths + 12) + (s.keyMode != KeyMode::Major ? 9 : 0)) % 12) {
    speller_.configure(s.keyFifths, s.keyMode);
    for (int qi = 0; qi < kQualities; qi++)
        for (int root = 0; root < 12; root++) {
            int t = qi * 12 + root;
            float* v = templates_[t];
            std::fill(v, v + 12, 0.f);
            masks_[t] = 0;
            for (int i = 0; i < kDefs[qi].n; i++) {
                int note = (root + kDefs[qi].iv[i]) % 12;
                masks_[t] |= uint16_t(1u << note);
                for (int k = 0; k < 12; k++) v[(note + k) % 12] += kPartialPc[k];
            }
            double norm = 0;
            for (int k = 0; k < 12; k++) norm += double(v[k]) * v[k];
            for (int k = 0; k < 12; k++) v[k] = float(v[k] / std::sqrt(norm));
        }
}

void ChordMatcher::symbol(int root, ChordQuality q, char (&out)[16]) const {
    // Diatonic roots follow the key. Chromatic roots take the lowered form (bIII, bVI, bVII, bII),
    // except the raised 4th (#iv / vii of V): Bb in C major, not A#; F# stays F#.
    int tonic = ((7 * (fifths_ + 12)) % 12);
    bool raised4 = (root - tonic + 12) % 12 == 6;
    speller_.reset_phrase();
    Spelled sp = speller_.spell(60 + root, raised4 ? 59 + root : 61 + root);
    static const char* acc[5] = {"bb", "b", "", "#", "x"};
    std::snprintf(out, sizeof out, "%c%s%s", "CDEFGAB"[sp.letter], acc[sp.alter + 2], def(q).suffix);
}

namespace {

enum class Family { Major, Minor, Dominant, Diminished, Other };

Family family(ChordQuality q) {
    switch (q) {
        case ChordQuality::Major: case ChordQuality::Maj7: case ChordQuality::Maj6: case ChordQuality::Add9:
        case ChordQuality::Sus2: case ChordQuality::Sus4: return Family::Major;
        case ChordQuality::Minor: case ChordQuality::Min7: case ChordQuality::Min6: return Family::Minor;
        case ChordQuality::Dom7: return Family::Dominant;
        case ChordQuality::Diminished: case ChordQuality::HalfDim7: case ChordQuality::Dim7: return Family::Diminished;
        default: return Family::Other;   // power (no third), augmented
    }
}

bool fits(Family rule, ChordQuality q) {
    Family f = family(q);
    if (q == ChordQuality::Power) return rule == Family::Major || rule == Family::Minor || rule == Family::Dominant;
    return f == rule || (rule == Family::Dominant && f == Family::Major);   // a V triad is still dominant
}

struct DegreeRule { int semis; Family family; float weight; const char* roman; };

// Function of a chord by its root's distance from the tonic. Includes the chromatic chords a
// live player uses constantly: harmonic-minor V, secondary dominants, borrowed chords.
constexpr DegreeRule kMajorRules[] = {
    {0, Family::Major, 1.0f, "I"},    {7, Family::Dominant, 0.95f, "V"}, {5, Family::Major, 0.85f, "IV"},
    {9, Family::Minor, 0.7f, "vi"},   {2, Family::Minor, 0.7f, "ii"},    {4, Family::Minor, 0.5f, "iii"},
    {11, Family::Diminished, 0.4f, "vii"}, {2, Family::Dominant, 0.35f, "V/V"}, {4, Family::Dominant, 0.35f, "V/vi"},
    {9, Family::Dominant, 0.35f, "V/ii"}, {0, Family::Dominant, 0.35f, "V/IV"}, {5, Family::Minor, 0.3f, "iv"},
    {10, Family::Major, 0.3f, "bVII"}, {8, Family::Major, 0.3f, "bVI"},
};
constexpr DegreeRule kMinorRules[] = {
    {0, Family::Minor, 1.0f, "i"},    {7, Family::Dominant, 0.95f, "V"}, {5, Family::Minor, 0.85f, "iv"},
    {8, Family::Major, 0.7f, "VI"},   {3, Family::Major, 0.6f, "III"},   {10, Family::Major, 0.6f, "VII"},
    {2, Family::Diminished, 0.5f, "ii"}, {7, Family::Minor, 0.45f, "v"}, {11, Family::Diminished, 0.45f, "vii"},
    {5, Family::Major, 0.35f, "IV"},  {0, Family::Major, 0.3f, "I"},
};

}  // namespace

float ChordMatcher::context(int root, ChordQuality q, int prevRoot, ChordQuality prevQ, const char** why) const {
    if (!keySet_) return 0;
    auto degree = [&](int r, ChordQuality cq, const char** roman) {
        int semis = (r - tonic_ + 12) % 12;
        float best = 0.15f;   // anything else: possible, just not expected
        const DegreeRule* rules = minor_ ? kMinorRules : kMajorRules;
        size_t n = minor_ ? std::size(kMinorRules) : std::size(kMajorRules);
        for (size_t i = 0; i < n; i++)
            if (rules[i].semis == semis && fits(rules[i].family, cq) && rules[i].weight > best) {
                best = rules[i].weight;
                if (roman) *roman = rules[i].roman;
            }
        return best;
    };
    const char* roman = nullptr;
    float prior = 0.7f * degree(root, q, &roman);
    if (why) *why = roman;

    if (prevRoot >= 0) {
        int from = (prevRoot - tonic_ + 12) % 12, to = (root - tonic_ + 12) % 12;
        Family pf = family(prevQ);
        bool prevDominant = from == 7 && (pf == Family::Dominant || pf == Family::Major);
        bool tonicChord = to == 0 && family(q) == (minor_ ? Family::Minor : Family::Major);
        if (prevDominant && tonicChord) { prior += 0.5f; if (why) *why = minor_ ? "V-i cadence" : "V-I cadence"; }
        else if (from == 5 && tonicChord) { prior += 0.3f; if (why) *why = "plagal cadence"; }
        else if (prevDominant && to == (minor_ ? 8 : 9)) { prior += 0.25f; if (why) *why = "deceptive cadence"; }
        else if ((prevRoot - root + 12) % 12 == 7) prior += 0.15f;   // root falls a fifth
    }
    return std::min(prior, 1.f);
}

void ChordMatcher::match(const float* energy, const ChordHistory& h, ChordRecognitionResult& out) const {
    auto previous = [&](int root, ChordQuality q, ChordQuality& pq) {
        bool isCurrent = root == h.currentRoot && q == h.currentQuality;
        pq = isCurrent ? h.beforeQuality : h.currentQuality;
        return isCurrent ? h.beforeRoot : h.currentRoot;
    };
    auto ctx = [&](int root, ChordQuality q, const char** why) {
        ChordQuality pq;
        int pr = previous(root, q, pq);
        return context(root, q, pr, pq, why);
    };
    out = {};
    float amp[12], mx = 0;
    double norm = 0;
    for (int i = 0; i < 12; i++) {
        amp[i] = std::sqrt(std::max(0.f, energy[i]));
        mx = std::max(mx, amp[i]);
        norm += double(amp[i]) * amp[i];
    }
    if (mx <= 0) return;   // silence: no candidate
    const float inv = float(1 / std::sqrt(norm));

    // Score all 180 templates; keep the best 4 (bounded, no allocation).
    struct Scored { int t; float score; };
    Scored top[4] = {{-1, -1}, {-1, -1}, {-1, -1}, {-1, -1}};
    for (int t = 0; t < kQualities * 12; t++) {
        float dot = 0;
        for (int k = 0; k < 12; k++) dot += templates_[t][k] * amp[k];
        float score = dot * inv;
        // Occam: a strong pitch class the template does not contain (and that is too strong to be a
        // partial) costs, and so does a template note that is absent. Keeps Cm6 from reading as
        // Adim + harmonic, and a triad from growing a phantom seventh.
        for (int k = 0; k < 12; k++) {
            bool in = masks_[t] >> k & 1;
            if (!in && amp[k] >= 0.6f * mx) score -= 0.04f;
            if (in && amp[k] < 0.3f * mx) score -= 0.04f;
        }
        // Context (key function + cadence) weighs less than one Occam penalty: it decides only
        // what the audio leaves open (identical sets, C+E dyad), never overrides a clear chord.
        score += kContextWeight * ctx(t % 12, kDefs[t / 12].q, nullptr);
        for (int i = 0; i < 4; i++)
            if (score > top[i].score) {
                for (int j = 3; j > i; j--) top[j] = top[j - 1];
                top[i] = {t, score};
                break;
            }
    }

    auto fill = [&](const Scored& s, ChordCandidate& c) {
        c = {};
        const QualityDef& d = kDefs[s.t / 12];
        int root = s.t % 12;
        c.rootPitchClass = int8_t(root);
        c.bassPitchClass = -1;
        c.quality = d.q;
        symbol(root, d.q, c.symbol);
        c.expectedCount = uint8_t(d.n);
        for (int i = 0; i < d.n; i++) {
            int pc = (root + d.iv[i]) % 12;
            c.expected[i] = int8_t(pc);
            if (amp[pc] >= 0.3f * mx) c.detected[c.detectedCount++] = int8_t(pc);
            else c.missing[c.missingCount++] = int8_t(pc);
        }
        for (int pc = 0; pc < 12 && c.extraCount < 8; pc++) {
            if (contains(c.expected, d.n, pc) || amp[pc] < 0.3f * mx) continue;
            bool harmonic = false;   // explainable as a partial of a chord note
            for (int i = 0; i < d.n; i++) harmonic |= (pc == (c.expected[i] + 7) % 12 || pc == (c.expected[i] + 4) % 12) && amp[pc] < 0.6f * mx;
            if (!harmonic) c.extra[c.extraCount++] = int8_t(pc);
        }
        c.rootScore = amp[root] / mx;
        c.thirdScore = d.n > 2 ? amp[(root + d.iv[1]) % 12] / mx : 0;
        c.fifthScore = amp[(root + 7) % 12] / mx;
        c.chromaScore = s.score;
        c.totalScore = s.score;
        c.tonalScore = ctx(root, d.q, nullptr);
        c.chromaScore = s.score - kContextWeight * c.tonalScore;   // acoustic part only
        c.incomplete = c.missingCount > 0;
    };

    fill(top[0], out.best);
    const uint16_t bestMask = masks_[top[0].t];
    const float margin = top[0].score - top[1].score;
    bool sameSet = false, symmetric = false;
    for (int i = 1; i < 4 && out.alternativeCount < ANA_MAX_CHORD_ALTERNATIVES; i++) {
        if (top[i].t < 0) break;
        fill(top[i], out.alternatives[out.alternativeCount++]);
        if (masks_[top[i].t] == bestMask) (kDefs[top[i].t / 12].q == kDefs[top[0].t / 12].q ? symmetric : sameSet) = true;
    }

    // Confidence: match quality x margin over the runner-up (ambiguity lowers it).
    float quality = std::clamp((top[0].score - 0.75f) / 0.25f, 0.f, 1.f);
    float separation = std::clamp(margin / 0.05f, 0.f, 1.f);
    out.best.confidence = quality * (0.5f + 0.5f * separation);
    if (sameSet || symmetric) out.best.confidence = std::min(out.best.confidence, 0.5f);
    out.ambiguous = sameSet || symmetric || margin < 0.01f;

    ChordQuality q = out.best.quality;
    const ChordCandidate& alt = out.alternatives[0];
    // Did the context decide? (the runner-up was acoustically as good or better)
    const char* function = nullptr;
    ctx(out.best.rootPitchClass, q, &function);
    bool contextDecided = out.alternativeCount && alt.chromaScore >= out.best.chromaScore - 0.005f && function;
    if (contextDecided && (sameSet || margin < 0.02f)) {
        std::snprintf(out.explanation, sizeof out.explanation, sameSet ? "%s = %s: %s in key, bass decides" : "%s over %s: %s in key",
                      out.best.symbol, alt.symbol, function);
        return;
    }
    if (symmetric) std::snprintf(out.explanation, sizeof out.explanation, "symmetric chord - root needs bass");
    else if (sameSet) std::snprintf(out.explanation, sizeof out.explanation, "%s = %s - bass decides", out.best.symbol, alt.symbol);
    else if (q == ChordQuality::Power || q == ChordQuality::Sus2 || q == ChordQuality::Sus4) std::snprintf(out.explanation, sizeof out.explanation, "no third - major/minor unknown");
    else if (out.best.missingCount && function) std::snprintf(out.explanation, sizeof out.explanation, "incomplete chord, %s in key", function);
    else if (out.best.missingCount) std::snprintf(out.explanation, sizeof out.explanation, "incomplete chord");
    else if (margin < 0.01f) std::snprintf(out.explanation, sizeof out.explanation, "close to %s", alt.symbol);
}

// ---------------------------------------------------------------- tracker

ChordTracker::ChordTracker(const SessionConfig& s, double hopSeconds, double latencyCompensation)
    : matcher_(s), hop_(hopSeconds), latencyComp_(latencyCompensation),
      confirmSeconds_(s.quality == AudioQuality::HighPrecision ? 0.6 : 0.4), releaseSeconds_(0.15) {}

void ChordTracker::emit(Output& out, AnalyzerEventType type, const Chord& ch, double end) {
    if (out.eventCount == 4) return;
    AnalyzerEvent& e = out.events[out.eventCount++];
    e = {};
    e.type = type;
    ChordEvent& c = e.data.chord;
    c.startTimeSeconds = ch.start - latencyComp_;
    c.endTimeSeconds = (type == AnalyzerEventType::ChordEnded ? end : ch.start) - latencyComp_;
    c.durationSeconds = c.endTimeSeconds - c.startTimeSeconds;
    std::memcpy(c.symbol, ch.c.symbol, sizeof c.symbol);
    c.rootPitchClass = ch.c.rootPitchClass;
    c.bassPitchClass = -1;
    c.quality = ch.c.quality;
    c.inversion = 0;
    c.detectedCount = ch.c.detectedCount;
    c.missingCount = ch.c.missingCount;
    std::memcpy(c.detectedNotes, ch.c.detected, sizeof c.detectedNotes);
    std::memcpy(c.missingNotes, ch.c.missing, sizeof c.missingNotes);
    c.confidence = float(ch.sumConfidence / std::max<uint32_t>(1, ch.frames));
    c.incomplete = ch.c.incomplete;
    c.provisional = 0;
    c.bassSettled = 0;
}

void ChordTracker::process(const ChromaVector& chroma, double t, double frameEnd, Output& out) {
    out.eventCount = 0;
    matcher_.match(chroma.raw, history_, out.preview);
    const ChordCandidate& best = out.preview.best;
    const bool sound = best.symbol[0] != 0;

    if (!sound) {
        if (silentSince_ < 0) silentSince_ = t;
        candidateOn_ = false;
        if (active_ && t - silentSince_ >= releaseSeconds_) {   // silence closes the chord at the release
            emit(out, AnalyzerEventType::ChordEnded, cur_, lastSound_);
            active_ = false;
        }
    } else {
        silentSince_ = -1;
        lastSound_ = t;
        const bool same = active_ && std::strcmp(best.symbol, cur_.c.symbol) == 0;
        if (same) {
            // The confirmed chord is still best: a passing tone never accumulated enough.
            candidateOn_ = false;
            cur_.sumConfidence += best.confidence;
            cur_.frames++;
        } else {
            if (!candidateOn_ || std::strcmp(best.symbol, cand_.symbol) != 0) {
                candidateOn_ = true;
                cand_ = best;
                candStart_ = t;
                candTime_ = 0;
                candLatencyMs_ = float((frameEnd - t) * 1000);   // onset estimate -> first preview
            }
            candTime_ = frameEnd - candStart_;   // measured from the onset estimate
            if (candTime_ + 1e-9 >= confirmSeconds_ && best.confidence >= 0.2f) {
                // Confirmed: the previous chord ends exactly at the new chord's backdated onset.
                if (active_) emit(out, AnalyzerEventType::ChordEnded, cur_, candStart_);
                cur_ = {best, candStart_, best.confidence, 1};
                history_ = {best.rootPitchClass, best.quality, history_.currentRoot, history_.currentQuality};   // cadence context
                curLatencyMs_ = candLatencyMs_;
                active_ = true;
                candidateOn_ = false;
                emit(out, AnalyzerEventType::ChordConfirmed, cur_, candStart_);
            }
        }
    }

    if (active_) std::memcpy(out.confirmedSymbol, cur_.c.symbol, sizeof out.confirmedSymbol);
    else std::memset(out.confirmedSymbol, 0, sizeof out.confirmedSymbol);
    out.confirmed = active_ && std::strcmp(best.symbol, cur_.c.symbol) == 0;
    out.latencyMs = candidateOn_ ? candLatencyMs_ : curLatencyMs_;
    out.confirmElapsedMs = candidateOn_ ? float(candTime_ * 1000) : 0.f;
}

void ChordTracker::flush(Output& out) {
    out.eventCount = 0;
    if (active_) emit(out, AnalyzerEventType::ChordEnded, cur_, lastSound_);
    active_ = candidateOn_ = false;
}

}  // namespace dz
