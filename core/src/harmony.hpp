// Coach metric R2.6 for a whole take in a chord mode: the chords as the whole-take decoder reads
// them, each played against what was heard. Changes on time (the change's attack against the
// nearest eighth of the grid), a gap before a change (the hand lifted early: quieter than before the
// chord's other strums), and how clean each chord sounds (the notes heard that are its tones).
// POST layer: allocates.
#pragma once
#include <cmath>
#include <vector>

#include "decode.hpp"

namespace dz {

struct ChordChange {
    double time;            // the change's attack
    float offsetMs;         // to the nearest eighth of the grid (> 0 late); NaN: off the grid
    int bar, beatInBar;     // 1-based (bars from the grid's first beat); 0: off the grid
    bool offbeat;           // on the "and"
    float gapDb;            // the level just before the change against just before the chord before's own attacks (< 0: a dip)
    float cleanAfterPct;    // notes heard in the change's first 0.25 s that are the new chord's tones
};

struct ChordPlayed {
    double start, end;
    char symbol[16];
    float cleanPct;         // notes heard that are its tones, after its first 0.25 s; NaN: none heard
};

struct HarmonyReport {
    std::vector<ChordPlayed> chords;
    std::vector<ChordChange> changes;
    float cleanPct = NAN, cleanAtChangePct = NAN;   // over all chords (time-weighted), and over the changes
    float changeMeanMs = NAN, changeSpreadMs = NAN; // the changes on the grid
    int onGrid = 0, gaps = 0;                       // gaps: a dip of 10 dB or more before the change
    double confidence = 0;                          // changes on the grid / changes, scaled down under 8
};

// chords: decode_chords' events; heard: its notes; x: the take, mono (times in x are the events'
// plus comp); attacks: detect_onsets' times (player time); beats: the grid (player time);
// beatsPerBar: 0 without bars.
HarmonyReport harmony_report(const std::vector<AnalyzerEvent>& chords, const std::vector<HeardFrame>& heard, const float* x, size_t n,
                             uint32_t rate, double comp, const std::vector<double>& attacks, const std::vector<double>& beats, int beatsPerBar);

}  // namespace dz
