// Chord recognition (spec §12) and chord tracker (§13).
//
// Matching: cosine similarity between the chroma AMPLITUDE vector and pre-transposed,
// harmonic-aware templates (each chord note contributes its own partials: octaves to the
// same pitch class, 3rd/6th harmonic to the fifth, 5th harmonic to the major third).
// 15 qualities x 12 roots = 180 templates, ~2k MACs per hop.
// Ambiguities are reported, never forced: identical pitch-class sets (C6 = Am7, symmetric
// aug/dim7) and missing thirds. Bass/inversions come with M4; until then no inversion.
#pragma once
#include <cstdint>

#include "dissonancia.h"
#include "theory.hpp"

namespace dz {

// Confirmed chords so far: the current one and the one before it. A template equal to the
// current chord is judged against the chord before it (its own arrival), any other template
// against the current chord (the move it would make).
struct ChordHistory {
    int currentRoot = -1;
    ChordQuality currentQuality = ChordQuality::Unknown;
    int beforeRoot = -1;
    ChordQuality beforeQuality = ChordQuality::Unknown;
};

class ChordMatcher {
public:
    explicit ChordMatcher(const SessionConfig& s);
    // chromaEnergy: 12 energies (ChromaVector.raw), already leakage-cleaned; all zero = silence.
    // history: confirmed chords so far, for the backward-looking context prior.
    // bassPc: SETTLED bass pitch class, -1 = unknown (then no inversion is ever reported, §11).
    void match(const float* chromaEnergy, const ChordHistory& history, int bassPc, ChordRecognitionResult& out) const;

    // Tonal context prior in [0, 1] (spec §12 tonalScore): function of the chord in the key
    // (major or minor mode) + cadence from the previous chord. Causal: no look-ahead.
    float context(int root, ChordQuality q, int previousRoot, ChordQuality previousQuality, const char** why) const;
    void symbol(int root, ChordQuality q, char (&out)[16]) const;
    // Appends "/bass": a chord tone is spelled from the root's letter (E/G#, not E/Ab), others by key.
    void slash(int root, ChordQuality q, int bassPc, char (&out)[16]) const;
    // Roman numeral in the session key (§17): case by third, o/+/h for dim/aug/half-dim, figured
    // bass for inversions (bassPc < 0 = unknown: root position assumed), V/x and viio/x for applied
    // chords, b/# against the key's scale. Minor accepts the raised leading tone (V, viio). No key -> "".
    void roman(int root, ChordQuality q, int bassPc, char (&out)[12], DiatonicStatus& status) const;
    // Cadence candidate (§17) from harmonic motion: previous -> last on arrival (authentic, plagal,
    // deceptive), or last at a phrase end (half, Phrygian). previous may be empty (symbol "").
    // Returns false when no cadence applies.
    bool cadence(const ChordCandidate& previous, const ChordCandidate& last, bool phraseEnd, CadenceEvent& out) const;

    // Session key changed before REC (dynamic session): spelling, roman numerals, context prior.
    void set_key(int8_t fifths, KeyMode mode);

    static constexpr int kQualities = 15;

private:
    Spelled root_spelling(int root) const;

    float templates_[kQualities * 12][12];
    uint16_t masks_[kQualities * 12];
    bool keySet_;
    mutable Speller speller_;
    int8_t fifths_ = 0;
    bool minor_ = false;
    int tonic_ = 0;   // pitch class of the tonic of the session key (relative minor in minor mode)
};

class ChordTracker {
public:
    struct Output {
        ChordRecognitionResult preview;
        char confirmedSymbol[16];
        bool confirmed;              // preview == confirmed chord
        float latencyMs, confirmElapsedMs;
        uint32_t eventCount;
        AnalyzerEvent events[4];
    };

    // hopSeconds: analysis hop; latencyCompensation subtracted from event times (§13).
    ChordTracker(const SessionConfig& s, double hopSeconds, double latencyCompensation);
    // timestamp: chroma frame time on the sample clock (ChromaVector.timestampSeconds);
    // frameEnd: sample-clock time of the newest sample in this hop.
    // bass: bass estimate of this hop; lastOnset: sample-clock onset time (< 0 = none) used to
    // backdate a new candidate to its attack.
    void process(const ChromaVector& chroma, double timestamp, double frameEnd, const BassEstimate& bass, double lastOnset, Output& out);
    void flush(Output& out);
    void set_key(int8_t fifths, KeyMode mode) { matcher_.set_key(fifths, mode); }

private:
    struct Chord {
        ChordCandidate c;
        double start;
        double sumConfidence;
        uint32_t frames;
    };
    void emit(Output& out, AnalyzerEventType type, const Chord& ch, double end);
    void emit_cadence(Output& out, const ChordCandidate& previous, const Chord& last, bool phraseEnd);

    ChordMatcher matcher_;
    double hop_, latencyComp_, confirmSeconds_, releaseSeconds_;
    bool active_ = false;            // a confirmed chord is open
    Chord cur_{};
    bool candidateOn_ = false;
    ChordCandidate cand_{};
    double candStart_ = 0, candTime_ = 0, lastSound_ = 0, silentSince_ = -1;
    float candLatencyMs_ = 0, curLatencyMs_ = 0;
    ChordHistory history_;
    ChordCandidate previous_{};      // confirmed chord before cur_ (symbol "" = none): cadence context
    float acc_[12]{};                // arpeggio accumulator (decaying max-hold of chroma energy)
    bool bassSettledNow_ = false;
};

}  // namespace dz
