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
    void match(const float* chromaEnergy, const ChordHistory& history, ChordRecognitionResult& out) const;

    // Tonal context prior in [0, 1] (spec §12 tonalScore): function of the chord in the key
    // (major or minor mode) + cadence from the previous chord. Causal: no look-ahead.
    float context(int root, ChordQuality q, int previousRoot, ChordQuality previousQuality, const char** why) const;
    void symbol(int root, ChordQuality q, char (&out)[16]) const;

    static constexpr int kQualities = 15;

private:
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
    void process(const ChromaVector& chroma, double timestamp, double frameEnd, Output& out);
    void flush(Output& out);

private:
    struct Chord {
        ChordCandidate c;
        double start;
        double sumConfidence;
        uint32_t frames;
    };
    void emit(Output& out, AnalyzerEventType type, const Chord& ch, double end);

    ChordMatcher matcher_;
    double hop_, latencyComp_, confirmSeconds_, releaseSeconds_;
    bool active_ = false;            // a confirmed chord is open
    Chord cur_{};
    bool candidateOn_ = false;
    ChordCandidate cand_{};
    double candStart_ = 0, candTime_ = 0, lastSound_ = 0, silentSince_ = -1;
    float candLatencyMs_ = 0, curLatencyMs_ = 0;
    ChordHistory history_;
};

}  // namespace dz
