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
constexpr float kBassWeight = 0.05f;   // settled bass: decides identical sets, never a clear chord
// Colour cost (Occam on the chord vocabulary): a melody note over a triad reads as an add9, sus
// or 6 frame after frame, a missing third as a "5". Triads cost nothing, sevenths a little, the
// colours more; a clear colour chord still wins by its own notes.
constexpr float kColourCost[16] = {
    0, 0, 0.005f, 0.01f, 0.012f, 0.012f,           // Major Minor Diminished Augmented Sus2 Sus4
    0.015f, 0.003f, 0.007f, 0.005f, 0.007f, 0.007f, // Power Dom7 Maj7 Min7 HalfDim7 Dim7
    0.01f, 0.01f, 0.012f, 0};                      // Maj6 Min6 Add9 Unknown

constexpr float kPartialPc[12] = {1.0f + 0.5f + 0.25f, 0, 0, 0, 0.2f, 0, 0, 0.33f + 0.17f, 0, 0, 0, 0};

bool contains(const int8_t* a, int n, int v) {
    for (int i = 0; i < n; i++)
        if (a[i] == v) return true;
    return false;
}

}  // namespace

// ---------------------------------------------------------------- song key

namespace {
constexpr float kMajorProfile[12] = {5, 2, 3.5f, 2, 4.5f, 4, 2, 4.5f, 2, 3.5f, 1.5f, 4};   // Temperley
constexpr float kMinorProfile[12] = {5, 2, 3.5f, 4.5f, 2, 4, 2, 4.5f, 3.5f, 2, 1.5f, 4};
}  // namespace

KeyTracker::KeyTracker(double hopSeconds) : hop_(hopSeconds), decay_(std::exp(-hopSeconds / kTimeConstant)) {}

void KeyTracker::process(const float* energy) {
    double amp[12], total = 0;
    for (int i = 0; i < 12; i++) total += amp[i] = std::sqrt(std::max(0.f, energy[i]));
    if (total <= 0) return;   // silence neither adds nor fades the key
    for (int i = 0; i < 12; i++) sum_[i] = sum_[i] * decay_ + hop_ * amp[i] / total;
    seconds_ = seconds_ * decay_ + hop_;
    if (seconds_ < kMinSeconds) return;

    double r[24], mean = 0;
    for (double v : sum_) mean += v / 12;
    for (int k = 0; k < 24; k++) {
        const float* p = k < 12 ? kMajorProfile : kMinorProfile;
        int tonic = k % 12;
        double pm = 0, num = 0, dh = 0, dp = 0;
        for (int i = 0; i < 12; i++) pm += p[i] / 12.0;
        for (int i = 0; i < 12; i++) {
            double a = sum_[(tonic + i) % 12] - mean, b = p[i] - pm;
            num += a * b; dh += a * a; dp += b * b;
        }
        r[k] = dh > 0 ? num / std::sqrt(dh * dp) : 0;
    }
    auto relative = [](int k) { return k < 12 ? 12 + (k + 9) % 12 : (k - 12 + 3) % 12; };
    auto margin = [&](int k) {
        double next = -2;
        for (int j = 0; j < 24; j++)
            if (j != k && j != relative(k)) next = std::max(next, r[j]);
        return float(r[k] - next);
    };
    int best = 0;
    for (int k = 1; k < 24; k++)
        if (r[k] > r[best]) best = k;

    if (current_ < 0) current_ = best;
    else if (best != current_) {
        challengerSeconds_ = best == challenger_ ? challengerSeconds_ + hop_ : hop_;
        challenger_ = best;
        if (challengerSeconds_ >= kSwitchSeconds) current_ = best, challenger_ = -1, challengerSeconds_ = 0;
    } else challenger_ = -1, challengerSeconds_ = 0;

    bool minor = current_ >= 12;
    int tonic = current_ % 12, majorPc = minor ? (tonic + 3) % 12 : tonic, fifths = majorPc * 7 % 12;
    est_.minor = minor;
    est_.tonic = int8_t(tonic);
    est_.fifths = int8_t(fifths > 6 ? fifths - 12 : fifths);
    est_.correlation = float(r[current_]);
    est_.margin = margin(current_);
    est_.seconds = float(seconds_);
    est_.valid = est_.margin >= kMinMargin;
}

// ---------------------------------------------------------------- matcher

ChordMatcher::ChordMatcher(const SessionConfig& s)
    : keySet_(s.keySet != 0), fifths_(s.keyFifths), minor_(s.keyMode != KeyMode::Major),
      tonic_((7 * (s.keyFifths + 12) + (s.keyMode != KeyMode::Major ? 9 : 0)) % 12),
      ctxOn_(keySet_), ctxMinor_(minor_), ctxTonic_(tonic_) {
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

Spelled ChordMatcher::root_spelling(int root) const {
    // Diatonic roots follow the key. Chromatic roots take the lowered form (bIII, bVI, bVII, bII),
    // except the raised 4th (#iv / vii of V): Bb in C major, not A#; F# stays F#.
    int tonic = ((7 * (fifths_ + 12)) % 12);
    bool raised4 = (root - tonic + 12) % 12 == 6;
    speller_.reset_phrase();
    return speller_.spell(60 + root, raised4 ? 59 + root : 61 + root);
}

void ChordMatcher::symbol(int root, ChordQuality q, char (&out)[16]) const {
    Spelled sp = root_spelling(root);
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

void ChordMatcher::describe(int root, ChordQuality q, int bassPc, ChordCandidate& c) const {
    c = {};
    const QualityDef& d = def(q);
    c.rootPitchClass = int8_t(root);
    c.quality = q;
    c.bassPitchClass = -1;
    symbol(root, q, c.symbol);
    c.expectedCount = uint8_t(d.n);
    for (int i = 0; i < d.n; i++) c.expected[i] = int8_t((root + d.iv[i]) % 12);
    if (bassPc >= 0 && contains(c.expected, d.n, bassPc)) {
        c.hasBass = 1;
        c.bassPitchClass = int8_t(bassPc);
        if (bassPc != root) slash(root, q, bassPc, c.symbol);
    }
    roman(root, q, c.hasBass ? bassPc : -1, c.roman, c.diatonicStatus);
    c.confidence = 1;
}

void ChordMatcher::hint(const KeyEstimate& k) {
    if (keySet_) return;
    ctxOn_ = k.valid != 0;
    ctxMinor_ = k.minor != 0;
    ctxTonic_ = k.tonic;
}

float ChordMatcher::context(int root, ChordQuality q, int prevRoot, ChordQuality prevQ, const char** why) const {
    if (!ctxOn_) return 0;
    const int tonic = ctxTonic_;
    const bool minor = ctxMinor_;
    auto degree = [&](int r, ChordQuality cq, const char** roman) {
        int semis = (r - tonic + 12) % 12;
        float best = 0.15f;   // anything else: possible, just not expected
        const DegreeRule* rules = minor ? kMinorRules : kMajorRules;
        size_t n = minor ? std::size(kMinorRules) : std::size(kMajorRules);
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
        int from = (prevRoot - tonic + 12) % 12, to = (root - tonic + 12) % 12;
        Family pf = family(prevQ);
        bool prevDominant = from == 7 && (pf == Family::Dominant || pf == Family::Major);
        bool tonicChord = to == 0 && family(q) == (minor ? Family::Minor : Family::Major);
        if (prevDominant && tonicChord) { prior += 0.5f; if (why) *why = minor ? "V-i cadence" : "V-I cadence"; }
        else if (from == 5 && tonicChord) { prior += 0.3f; if (why) *why = "plagal cadence"; }
        else if (prevDominant && to == (minor ? 8 : 9)) { prior += 0.25f; if (why) *why = "deceptive cadence"; }
        else if ((prevRoot - root + 12) % 12 == 7) prior += 0.15f;   // root falls a fifth
    }
    return std::min(prior, 1.f);
}

namespace {

constexpr int kMajorScale[7] = {0, 2, 4, 5, 7, 9, 11}, kMinorScale[7] = {0, 2, 3, 5, 7, 8, 10};

bool in_scale(int pc, int tonic, const int* scale, bool leadingTone) {
    int i = (pc - tonic + 12) % 12;
    for (int k = 0; k < 7; k++)
        if (scale[k] == i) return true;
    return leadingTone && i == 11;
}

bool seventh_chord(ChordQuality q) {
    return q == ChordQuality::Dom7 || q == ChordQuality::Maj7 || q == ChordQuality::Min7 || q == ChordQuality::HalfDim7 || q == ChordQuality::Dim7;
}

}  // namespace

void ChordMatcher::roman(int root, ChordQuality q, int bassPc, char (&out)[12], DiatonicStatus& status) const {
    out[0] = 0;
    status = DiatonicStatus::Unknown;
    if (!keySet_) return;
    const QualityDef& d = def(q);
    const int* own = minor_ ? kMinorScale : kMajorScale;
    const int* parallel = minor_ ? kMajorScale : kMinorScale;
    auto all_in = [&](const int* scale, bool leadingTone) {
        for (int i = 0; i < d.n; i++)
            if (!in_scale((root + d.iv[i]) % 12, tonic_, scale, leadingTone)) return false;
        return true;
    };
    // Degree from the letter distance to the tonic, accidental against the key's own scale.
    auto numeral = [&](int r, bool lower, char (&buf)[8]) {
        static const char* up[7] = {"I", "II", "III", "IV", "V", "VI", "VII"};
        static const char* lo[7] = {"i", "ii", "iii", "iv", "v", "vi", "vii"};
        int deg = (root_spelling(r).letter - root_spelling(tonic_).letter + 7) % 7;
        int alt = ((r - tonic_ - own[deg]) % 12 + 12) % 12;
        if (alt > 6) alt -= 12;
        if (minor_ && deg == 6 && alt == 1) alt = 0;   // raised leading tone: vii, not #vii
        const char* acc = alt == -2 ? "bb" : alt == -1 ? "b" : alt == 1 ? "#" : alt == 2 ? "##" : "";
        std::snprintf(buf, sizeof buf, "%s%s", acc, (lower ? lo : up)[deg]);
    };

    int inv = 0;   // index of the bass among the chord tones; -1 = bass outside the chord
    if (bassPc >= 0 && bassPc != root) {
        inv = -1;
        for (int i = 0; i < d.n; i++)
            if ((root + d.iv[i]) % 12 == bassPc) inv = i;
    }
    static const char* triadFig[3] = {"", "6", "64"};
    static const char* seventhFig[4] = {"7", "65", "43", "42"};
    const bool triad = q == ChordQuality::Major || q == ChordQuality::Minor || q == ChordQuality::Diminished || q == ChordQuality::Augmented;
    const char* fig = seventh_chord(q) ? seventhFig[std::max(inv, 0)] : triad && inv > 0 ? triadFig[inv] : "";
    const char* qs = "";
    switch (q) {
        case ChordQuality::Diminished: case ChordQuality::Dim7: qs = "o"; break;
        case ChordQuality::HalfDim7: qs = "h"; break;
        case ChordQuality::Augmented: qs = "+"; break;
        case ChordQuality::Maj7: qs = "M"; break;
        case ChordQuality::Sus2: qs = "sus2"; break;
        case ChordQuality::Sus4: qs = "sus4"; break;
        case ChordQuality::Power: qs = "5"; break;
        case ChordQuality::Maj6: case ChordQuality::Min6: qs = "add6"; break;   // "6" alone is an inversion
        case ChordQuality::Add9: qs = "add9"; break;
        default: break;
    }
    const Family f = family(q);
    const bool lower = f == Family::Minor || f == Family::Diminished;
    char num[8];

    const bool neapolitan = (root - tonic_ + 12) % 12 == 1 && q == ChordQuality::Major;
    if (all_in(own, minor_)) status = DiatonicStatus::Diatonic;
    else if (all_in(parallel, false) || neapolitan) status = DiatonicStatus::BorrowedChord;
    else {
        // Applied chord: dominant a fifth above, or leading-tone chord a semitone below, a diatonic
        // major or minor triad other than the tonic.
        const bool dominant = q == ChordQuality::Major || q == ChordQuality::Dom7;
        const bool leading = q == ChordQuality::Diminished || q == ChordQuality::Dim7 || q == ChordQuality::HalfDim7;
        const int target = dominant ? (root + 5) % 12 : leading ? (root + 1) % 12 : -1;
        if (target >= 0 && target != tonic_ && in_scale(target, tonic_, own, false) && in_scale(target + 7, tonic_, own, minor_)) {
            char tn[8];
            numeral(target, !in_scale(target + 4, tonic_, own, minor_), tn);
            status = DiatonicStatus::AppliedDominant;
            std::snprintf(out, sizeof out, "%s%.1s%.2s/%.4s", dominant ? "V" : "vii", qs, fig, tn);   // widths: fits 11 chars
            return;
        }
    }
    numeral(root, lower, num);
    std::snprintf(out, sizeof out, "%.5s%.4s%.2s", num, qs, fig);
}

bool ChordMatcher::cadence(const ChordCandidate& prev, const ChordCandidate& last, bool phraseEnd, CadenceEvent& e) const {
    e = {};
    if (!keySet_) return false;
    auto semis = [&](const ChordCandidate& c) { return (c.rootPitchClass - tonic_ + 12) % 12; };
    auto dominant = [&](const ChordCandidate& c) { return semis(c) == 7 && (c.quality == ChordQuality::Major || c.quality == ChordQuality::Dom7); };
    auto rootPosition = [](const ChordCandidate& c) { return c.hasBass && c.bassPitchClass == c.rootPitchClass; };
    const bool hasPrev = prev.symbol[0] != 0;
    const bool tonicArrival = semis(last) == 0 && (family(last.quality) == Family::Major || family(last.quality) == Family::Minor);
    const bool bothRoot = hasPrev && rootPosition(prev) && rootPosition(last);
    const bool bassKnown = hasPrev && prev.hasBass && last.hasBass;
    const bool v7 = hasPrev && prev.quality == ChordQuality::Dom7;
    const char* motion = "";
    float conf = 0;

    if (!phraseEnd && hasPrev && dominant(prev) && tonicArrival) {
        e.type = bothRoot ? CadenceType::PerfectAuthentic : CadenceType::ImperfectAuthentic;
        motion = "5-1";
        conf = 0.45f + (bothRoot ? 0.15f : 0) + (v7 ? 0.1f : 0);
    } else if (!phraseEnd && hasPrev && semis(prev) == 5 && tonicArrival) {
        e.type = CadenceType::Plagal;
        motion = "4-1";
        conf = 0.4f + (bothRoot ? 0.1f : 0);
    } else if (!phraseEnd && hasPrev && dominant(prev) && semis(last) == (minor_ ? 8 : 9) &&
               family(last.quality) == (minor_ ? Family::Major : Family::Minor)) {
        e.type = CadenceType::Deceptive;
        motion = minor_ ? "5-b6" : "5-6";
        conf = 0.45f + (v7 ? 0.1f : 0);
    } else if (phraseEnd && dominant(last)) {
        const bool phrygian = minor_ && hasPrev && semis(prev) == 5 && family(prev.quality) == Family::Minor && prev.hasBass &&
                              prev.bassPitchClass == (tonic_ + 8) % 12;
        e.type = phrygian ? CadenceType::Phrygian : CadenceType::Half;
        motion = phrygian ? "iv6-V, phrase end" : "ends on V";
        conf = phrygian ? 0.5f : 0.35f + (rootPosition(last) ? 0.1f : 0);
    } else {
        return false;
    }
    e.confidence = std::min(conf, 0.75f);   // LIVE: no melody, no meter, never definitive (§17)
    if (hasPrev) std::memcpy(e.fromRoman, prev.roman, sizeof prev.roman);
    std::memcpy(e.toRoman, last.roman, sizeof last.roman);
    const char* bass = phraseEnd ? "" : bothRoot ? ", root position" : bassKnown ? ", inverted" : ", bass unknown";
    std::snprintf(e.evidence, sizeof e.evidence, "%s%s%s; no melody", motion, bass, v7 && !phraseEnd ? ", V7" : "");
    return true;
}

void ChordMatcher::slash(int root, ChordQuality q, int bassPc, char (&out)[16]) const {
    static const char* acc[5] = {"bb", "b", "", "#", "x"};
    const QualityDef& d = def(q);
    int interval = (bassPc - root + 12) % 12;
    bool chordTone = false;
    for (int i = 0; i < d.n; i++) chordTone |= d.iv[i] % 12 == interval;
    int letter, alter;
    if (chordTone) {
        // Letter steps of each interval above the root (third -> 2 letters, fifth -> 4, ...).
        static constexpr int steps[12] = {0, 1, 1, 2, 2, 3, 4, 4, 4, 5, 6, 6};
        speller_.reset_phrase();
        Spelled r = speller_.spell(60 + root, 61 + root);
        letter = (r.letter + steps[interval]) % 7;
        alter = ((bassPc - Speller::kLetterSemis[letter]) % 12 + 12) % 12;
        if (alter > 6) alter -= 12;
    } else {
        speller_.reset_phrase();
        Spelled b = speller_.spell(60 + bassPc, 61 + bassPc);
        letter = b.letter;
        alter = b.alter;
    }
    size_t n = std::strlen(out);
    if (alter >= -2 && alter <= 2) std::snprintf(out + n, sizeof out - n, "/%c%s", "CDEFGAB"[letter], acc[alter + 2]);
}

float ChordMatcher::acoustic(int t, const float* amp, float mx, float inv, int bassPc) const {
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
    // A power chord is the absence of a third, not a weak one: a barre F has one A among five
    // strings, an Am one C. Any audible third (minor or major) rules the "5" out.
    if (kDefs[t / 12].q == ChordQuality::Power) {
        const int r = t % 12;
        if (std::max(amp[(r + 3) % 12], amp[(r + 4) % 12]) >= 0.15f * mx) return kRuledOut;
    }
    score -= kColourCost[size_t(kDefs[t / 12].q)];
    // Settled bass (§11): the bass as root scores most, as another chord tone half.
    if (bassPc >= 0) score += kBassWeight * (t % 12 == bassPc ? 1.f : (masks_[t] >> bassPc & 1) ? 0.5f : 0.f);
    return score;
}

void ChordMatcher::score_all(const float* energy, int bassPc, float* out) const {
    float amp[12], mx = 0;
    double norm = 0;
    for (int i = 0; i < 12; i++) {
        amp[i] = std::sqrt(std::max(0.f, energy[i]));
        mx = std::max(mx, amp[i]);
        norm += double(amp[i]) * amp[i];
    }
    for (int t = 0; t < kTemplates; t++) out[t] = mx > 0 ? acoustic(t, amp, mx, float(1 / std::sqrt(norm)), bassPc) : kRuledOut;
}

void ChordMatcher::match(const float* energy, const ChordHistory& h, int bassPc, ChordRecognitionResult& out) const {
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
        float score = acoustic(t, amp, mx, inv, bassPc);
        if (score <= kRuledOut) continue;
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
        // In a key (set or estimated), a settled bass outside both the chord and the key is a passing
        // note under the chord, not its bass (C over a chromatic Ab walking down is still C).
        const bool passing = bassPc >= 0 && ctxOn_ && !(masks_[s.t] >> bassPc & 1)
                             && !in_scale(bassPc, ctxTonic_, ctxMinor_ ? kMinorScale : kMajorScale, ctxMinor_);
        const int chordBass = passing ? -1 : bassPc;
        if (chordBass >= 0) {
            c.hasBass = 1;
            c.bassPitchClass = int8_t(chordBass);
            c.bassScore = root == chordBass ? 1.f : (masks_[s.t] >> chordBass & 1) ? 0.5f : 0.f;
            if (chordBass != root) slash(root, d.q, chordBass, c.symbol);   // inversion only with a settled bass
        }
        roman(root, d.q, chordBass, c.roman, c.diatonicStatus);
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
    // A settled bass on the root resolves identical sets (C6 vs Am7, aug/dim7 roots).
    const bool resolvedByBass = bassPc >= 0 && out.best.rootPitchClass == bassPc;
    if ((sameSet || symmetric) && !resolvedByBass) out.best.confidence = std::min(out.best.confidence, 0.5f);
    out.ambiguous = ((sameSet || symmetric) && !resolvedByBass) || margin < 0.01f;

    ChordQuality q = out.best.quality;
    const ChordCandidate& alt = out.alternatives[0];
    // Did the context decide? (the runner-up was acoustically as good or better)
    const char* function = nullptr;
    ctx(out.best.rootPitchClass, q, &function);
    bool contextDecided = out.alternativeCount && alt.chromaScore >= out.best.chromaScore - 0.005f && function;
    if (resolvedByBass && (sameSet || symmetric)) {
        std::snprintf(out.explanation, sizeof out.explanation, "bass decides: %s, not %s", out.best.symbol, alt.symbol);
        return;
    }
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
      confirmSeconds_(s.quality == AudioQuality::HighPrecision ? 0.6 : 0.4), releaseSeconds_(0.15), key_(hopSeconds), hmm_(hopSeconds), harmony_(hopSeconds, ChordHmm::harmony()) {}

// Persistence-weighted mean chroma over [from, to]: each pitch class weighs by the share of frames
// in which it is strong (>= 0.3 of the frame maximum in amplitude). Chord tones ring through the
// segment; a melody note, a passing bass note or a fret squeak is there for a fraction of it.
bool ChordTracker::segment_chord(double from, double to, int bassPc, bool persistence, ChordRecognitionResult& out) const {
    float mean[12]{}, present[12]{};
    int n = 0;
    for (int i = 0; i < kSegFrames; i++) {
        if (segTime_[i] <= 0 || segTime_[i] < from - 1e-9 || segTime_[i] > to + 1e-9) continue;
        float mx = 0;
        for (int k = 0; k < 12; k++) mx = std::max(mx, seg_[i][k]);
        if (mx <= 0) continue;
        for (int k = 0; k < 12; k++) {
            // Persistence mode: frames normalised to their loudest pitch class, so a melody note
            // far above the accompaniment saturates at 1 instead of outweighing the chord.
            mean[k] += persistence ? seg_[i][k] / mx : seg_[i][k];
            present[k] += seg_[i][k] >= 0.09f * mx;   // 0.3 in amplitude
        }
        n++;
    }
    if (n < 3) return false;
    for (int k = 0; k < 12; k++) {
        const float share = persistence ? present[k] / float(n) : 1.f;
        mean[k] = mean[k] / float(n) * share * share;
    }
    matcher_.match(mean, history_, bassPc, out);
    return out.best.symbol[0] != 0;
}

void ChordTracker::emit_ended(Output& out, const Chord& ch, double end) {
    Chord whole = ch;
    ChordRecognitionResult r;
    if (segment_chord(ch.start, end, ch.c.hasBass ? ch.c.bassPitchClass : -1, true, r) && r.best.confidence >= 0.2f) whole.c = r.best;
    emit(out, AnalyzerEventType::ChordEnded, whole, end);
}

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
    c.bassPitchClass = ch.c.hasBass ? ch.c.bassPitchClass : int8_t(-1);
    c.quality = ch.c.quality;
    c.inversion = 0;   // 0 root position, 1 third, 2 fifth, 3 seventh/added, -1 bass outside the chord
    if (ch.c.hasBass && ch.c.bassPitchClass != ch.c.rootPitchClass) {
        c.inversion = -1;
        for (int i = 0; i < ch.c.expectedCount; i++)
            if (ch.c.expected[i] == ch.c.bassPitchClass) c.inversion = int8_t(i);
    }
    c.detectedCount = ch.c.detectedCount;
    c.missingCount = ch.c.missingCount;
    std::memcpy(c.detectedNotes, ch.c.detected, sizeof c.detectedNotes);
    std::memcpy(c.missingNotes, ch.c.missing, sizeof c.missingNotes);
    std::memcpy(c.roman, ch.c.roman, sizeof c.roman);
    c.diatonicStatus = ch.c.diatonicStatus;
    c.confidence = float(ch.sumConfidence / std::max<uint32_t>(1, ch.frames));
    c.incomplete = ch.c.incomplete;
    c.provisional = 0;
    c.bassSettled = ch.c.hasBass;
    c.arpeggiated = ch.c.arpeggiated;
}

void ChordTracker::emit_cadence(Output& out, const ChordCandidate& previous, const Chord& last, bool phraseEnd) {
    CadenceEvent c;
    if (out.eventCount == 4 || !matcher_.cadence(previous, last.c, phraseEnd, c)) return;
    c.timestampSeconds = last.start - latencyComp_;   // arrival of the final chord
    AnalyzerEvent& e = out.events[out.eventCount++];
    e = {};
    e.type = AnalyzerEventType::Cadence;
    e.data.cadence = c;
}

namespace {
constexpr float kHoldMargin = 0.04f;   // one Occam penalty: the confirmed chord holds within it
// Same chord: root and quality equal, and the bass equal when both know it. A preview without a
// settled bass (right after an onset) never splits a chord; two different settled basses do.
bool same_chord(const ChordCandidate& a, const ChordCandidate& b) {
    return a.rootPitchClass == b.rootPitchClass && a.quality == b.quality && (!a.hasBass || !b.hasBass || a.bassPitchClass == b.bassPitchClass);
}
int strong(const float* e, float mx) {
    int n = 0;
    for (int i = 0; i < 12; i++) n += std::sqrt(e[i]) >= 0.3f * std::sqrt(mx) && e[i] > 0;
    return n;
}
}  // namespace

void ChordTracker::process(const ChromaVector& chroma, double t, double frameEnd, const BassEstimate& bass, double lastOnset, Output& out) {
    out.eventCount = 0;

    // Arpeggio accumulator (§12): a decaying max-hold of chroma energy. tau 0.25 s keeps a note
    // above the 0.3 amplitude threshold for ~0.6 s (the live 300-600 ms window). Used only when the
    // frame alone shows <= 2 notes but the recent past shows a chord: never smears strummed changes.
    // Damped picking leaves gaps between its notes: through a silence under 0.6 s after a picked
    // note the accumulator holds (no decay) and the chord stays open; a longer one clears it.
    const float decay = float(std::exp(-hop_ / 0.25));
    float mxNow = 0, mxAcc = 0;
    for (int i = 0; i < 12; i++) mxNow = std::max(mxNow, chroma.raw[i]);
    const bool silentFrame = mxNow <= 0;
    if (!silentFrame) quietSince_ = -1;
    else if (quietSince_ < 0) quietSince_ = t;
    const bool gap = silentFrame && lastSparse_ && t - quietSince_ <= 0.6;
    for (int i = 0; i < 12; i++) {
        if (!silentFrame) acc_[i] = std::max(acc_[i] * decay, chroma.raw[i]);
        else if (!gap) acc_[i] = 0;
        mxAcc = std::max(mxAcc, acc_[i]);
    }
    if (!silentFrame) lastSparse_ = strong(chroma.raw, mxNow) <= 2;
    key_.process(chroma.raw);
    matcher_.hint(key_.estimate());
    out.key = key_.estimate();
    std::memcpy(seg_[segPos_], chroma.raw, sizeof seg_[0]);
    segTime_[segPos_] = t;
    segPos_ = (segPos_ + 1) % kSegFrames;
    const bool arpeggio = !silentFrame && strong(chroma.raw, mxNow) <= 2 && strong(acc_, mxAcc) >= 3;
    const bool held = gap && strong(acc_, mxAcc) >= 3;   // between two picked notes: still the chord
    const int bassPc = bass.valid && bass.settled ? bass.pitchClass : -1;
    matcher_.match(arpeggio || held ? acc_ : chroma.raw, history_, bassPc, out.preview);
    out.preview.best.arpeggiated = arpeggio || held;
    const ChordCandidate& best = out.preview.best;
    const bool sound = best.symbol[0] != 0;

    if (!sound) {
        if (silentSince_ < 0) silentSince_ = t;
        candidateOn_ = false;
        if (active_ && t - silentSince_ >= releaseSeconds_) {   // silence closes the chord at the release
            emit_ended(out, cur_, lastSound_);
            emit_cadence(out, previous_, cur_, true);   // silence = phrase end
            active_ = false;
            previous_ = {};
        }
    } else {
        // A gap between picked notes builds the chord like a note, but the chord does not last
        // into the gap: it ends at the last note heard.
        if (!held) silentSince_ = -1, lastSound_ = t;
        // Hysteresis: the confirmed chord holds while it is still a close runner-up. An up-strum on
        // the top strings, a passing melody note or a drum hit tilts one frame, not the harmony.
        bool holds = false;
        if (active_)
            for (uint8_t i = 0; i < out.preview.alternativeCount; i++) {
                const ChordCandidate& a = out.preview.alternatives[i];
                holds |= a.rootPitchClass == cur_.c.rootPitchClass && a.quality == cur_.c.quality && best.totalScore - a.totalScore < kHoldMargin;
            }
        const bool same = active_ && (same_chord(best, cur_.c) || holds);
        if (same) {
            // The confirmed chord is still best: a passing tone never accumulated enough.
            candidateOn_ = false;
            cur_.sumConfidence += best.confidence;
            cur_.frames++;
            if (best.hasBass && !cur_.c.hasBass) {   // bass settled after confirmation: now it has one
                cur_.c.hasBass = 1;
                cur_.c.bassPitchClass = best.bassPitchClass;
                std::memcpy(cur_.c.symbol, best.symbol, sizeof cur_.c.symbol);
                std::memcpy(cur_.c.roman, best.roman, sizeof cur_.c.roman);
            }
        } else {
            if (!candidateOn_ || !same_chord(best, cand_)) {
                candidateOn_ = true;
                cand_ = best;
                // Backdate to the attack that started this change: the newest onset after the current
                // chord began (a candidate can restart while the bass settles), within 1 s; else this frame.
                const bool changeOnset = lastOnset >= 0 && t - lastOnset < 1.0 && (!active_ || lastOnset > cur_.start + 0.05);
                candStart_ = changeOnset ? std::min(t, lastOnset) : t;
                candTime_ = 0;
                candLatencyMs_ = float((frameEnd - t) * 1000);   // onset estimate -> first preview
            }
            candTime_ = frameEnd - candStart_;   // measured from the onset estimate
            if (best.hasBass && !cand_.hasBass) cand_ = best;   // keep the settled bass once known
            // The same root in another colour (a "5", sus, add9, 6, a seventh added or dropped, another
            // bass) is usually the strum, not the harmony: it must last three times as long. A changed
            // third (Dm -> D7, C -> Cm) is harmony and confirms as fast as any change.
            const auto third = [](ChordQuality q) { const Family f = family(q); return f == Family::Minor || f == Family::Diminished ? 1 : f == Family::Major || f == Family::Dominant ? 2 : 0; };
            const int ta = third(cand_.quality), tb = third(cur_.c.quality);
            const bool variant = active_ && cand_.rootPitchClass == cur_.c.rootPitchClass && !(ta && tb && ta != tb);
            bool confirm = candTime_ + 1e-9 >= confirmSeconds_ * (variant ? 3 : 1) && best.confidence >= 0.2f;
            ChordRecognitionResult segment;
            // Decide on the segment, not this frame (an arpeggio already decides on its accumulator).
            if (confirm && !cand_.arpeggiated && segment_chord(candStart_, t, bassPc, false, segment)) {
                if (active_ && same_chord(segment.best, cur_.c)) { candidateOn_ = false; confirm = false; }   // it was the strum
                else cand_ = segment.best;
            }
            if (confirm) {
                const ChordCandidate& best = cand_;
                // Confirmed: the previous chord ends exactly at the new chord's backdated onset.
                if (active_) emit_ended(out, cur_, candStart_);
                previous_ = active_ ? cur_.c : ChordCandidate{};
                cur_ = {best, candStart_, best.confidence, 1};
                history_ = {best.rootPitchClass, best.quality, history_.currentRoot, history_.currentQuality};   // cadence context
                curLatencyMs_ = candLatencyMs_;
                active_ = true;
                candidateOn_ = false;
                emit(out, AnalyzerEventType::ChordConfirmed, cur_, candStart_);
                emit_cadence(out, previous_, cur_, false);
            }
        }
    }

    // Re-strum of the confirmed chord: its bass is re-settling (§11), but the chord keeps the inversion it
    // was confirmed with, so the preview shows it instead of flickering to root position.
    if (active_ && sound && same_chord(best, cur_.c) && !best.hasBass && cur_.c.hasBass) {
        ChordCandidate& b = out.preview.best;
        b.hasBass = 1;
        b.bassPitchClass = cur_.c.bassPitchClass;
        std::memcpy(b.symbol, cur_.c.symbol, sizeof b.symbol);
        std::memcpy(b.roman, cur_.c.roman, sizeof b.roman);
    }
    if (active_) std::memcpy(out.confirmedSymbol, cur_.c.symbol, sizeof out.confirmedSymbol);
    else std::memset(out.confirmedSymbol, 0, sizeof out.confirmedSymbol);
    out.confirmed = active_ && same_chord(best, cur_.c);
    out.latencyMs = candidateOn_ ? candLatencyMs_ : curLatencyMs_;
    out.confirmElapsedMs = candidateOn_ ? float(candTime_ * 1000) : 0.f;
}

void ChordTracker::process_notes(const NoteFrame& notes, const ChromaVector& chroma, double t, double frameEnd, Output& out) {
    out.eventCount = 0;
    key_.process(chroma.raw);
    matcher_.hint(key_.estimate());
    out.key = key_.estimate();
    int tonic = 0;
    bool minor = false;
    const bool keyOn = matcher_.context_key(tonic, minor);
    hmm_.step(notes, keyOn, tonic, minor);
    harmony_.step(notes, keyOn, tonic, minor);
    if (notes.audible) lastAudible_ = t;
    const bool sounding = lastAudible_ >= 0 && t - lastAudible_ < 0.6;
    const double lagSeconds = hmm_.lag_hops() * hop_;

    out.preview = {};
    if (sounding && hmm_.current() >= 0) {
        const int s = hmm_.current();
        matcher_.describe(ChordHmm::root(s), ChordHmm::quality(s), notes.count ? notes.midi[0] % 12 : -1, out.preview.best);
        static const char* names[12] = {"C", "C#", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B"};
        char* w = out.preview.explanation;
        int len = std::snprintf(w, sizeof out.preview.explanation, "heard");
        for (int i = 0; i < notes.count && len < int(sizeof out.preview.explanation) - 4; i++)
            len += std::snprintf(w + len, sizeof out.preview.explanation - size_t(len), " %s", names[notes.midi[i] % 12]);
    }
    if (active_ && !sounding) {   // silence ends the chord at the last sound
        int best = cur_.c.rootPitchClass, total = 0;
        for (int p = 0; p < 12; p++) { total += bassCount_[p]; if (bassCount_[p] > bassCount_[best]) best = p; }
        if (!(total > 0 && bassCount_[best] > 0.6 * total)) best = cur_.c.rootPitchClass;
        matcher_.describe(cur_.c.rootPitchClass, cur_.c.quality, best, cur_.c);
        emit(out, AnalyzerEventType::ChordEnded, cur_, lastAudible_);
        emit_cadence(out, previous_, cur_, true);
        active_ = false;
        previous_ = {};
        curState_ = -1;
        hmm_.reset();
        harmony_.reset();
    }
    // The chord's bass: the lowest note heard most often while it sounds; an inversion only when that
    // is not the root most of the time (an alternating thumb is still root position).
    auto chordBass = [&](int root) {
        int best = -1, total = 0;
        for (int p = 0; p < 12; p++) {
            total += bassCount_[p];
            if (best < 0 || bassCount_[p] > bassCount_[best]) best = p;
        }
        return total > 0 && best != root && bassCount_[best] > 0.6 * total ? best : root;
    };
    if (active_ && notes.count) bassCount_[notes.midi[0] % 12]++;
    const int d = hmm_.decided();
    // A change counts once the decision has held it for 0.2 s: the lagged decision may revise itself
    // hop to hop at a boundary, and a 20 ms chord is never what was played.
    if (d != pendingState_) pendingState_ = d, pendingSince_ = t;
    const bool held = d >= 0 && t - pendingSince_ >= kHoldSeconds;
    if (sounding && held && active_ && d != curState_ && ChordHmm::root(d) == cur_.c.rootPitchClass) {
        // Same root, another quality: the same chord heard better (a seventh or sus at its start was
        // the previous chord still ringing). Renamed in place; no change, no new chord.
        matcher_.describe(ChordHmm::root(d), ChordHmm::quality(d), chordBass(ChordHmm::root(d)), cur_.c);
        curState_ = d;
    } else if (sounding && held && (!active_ || d != curState_)) {
        const double start = std::max(pendingSince_ - lagSeconds, active_ ? cur_.start + hop_ : 0.0);
        if (active_) {
            matcher_.describe(cur_.c.rootPitchClass, cur_.c.quality, chordBass(cur_.c.rootPitchClass), cur_.c);
            emit(out, AnalyzerEventType::ChordEnded, cur_, start);
        }
        ChordCandidate c;
        matcher_.describe(ChordHmm::root(d), ChordHmm::quality(d), -1, c);
        std::fill(bassCount_, bassCount_ + 12, 0);
        previous_ = active_ ? cur_.c : ChordCandidate{};
        cur_ = {c, start, 1, 1};
        curState_ = d;
        history_ = {c.rootPitchClass, c.quality, history_.currentRoot, history_.currentQuality};
        active_ = true;
        curLatencyMs_ = float((frameEnd - start) * 1000);
        emit(out, AnalyzerEventType::ChordConfirmed, cur_, start);
        emit_cadence(out, previous_, cur_, false);
    }
    if (active_) std::memcpy(out.confirmedSymbol, cur_.c.symbol, sizeof out.confirmedSymbol);
    else std::memset(out.confirmedSymbol, 0, sizeof out.confirmedSymbol);
    out.confirmed = active_ && out.preview.best.symbol[0] && std::strcmp(out.preview.best.symbol, cur_.c.symbol) == 0;
    out.latencyMs = float(lagSeconds * 1000);
    out.confirmElapsedMs = 0;
    // The harmony underneath changes on screen once it has held 0.8 s (a passing Em between two Ds is
    // the chord played, not the harmony).
    const int h = harmony_.decided();
    if (h != harmonyPending_) harmonyPending_ = h, harmonySince_ = t;
    if (!sounding) harmonyShown_ = -1;
    else if (h >= 0 && (harmonyShown_ < 0 || t - harmonySince_ >= 0.8)) harmonyShown_ = h;
    std::memset(out.harmony, 0, sizeof out.harmony);
    if (harmonyShown_ >= 0) matcher_.symbol(ChordHmm::root(harmonyShown_), ChordHmm::quality(harmonyShown_), out.harmony);
}

void ChordTracker::flush(Output& out) {
    out.eventCount = 0;
    if (active_) emit_ended(out, cur_, lastSound_);
    active_ = candidateOn_ = false;
    previous_ = {};
}

}  // namespace dz
