// Coach metric R2.8 for a whole take: its form. The take is cut into phrases of 4 or 8 bars from
// the first downbeat (the length under which most bars repeat an earlier phrase); each phrase takes
// the label of the earlier one it matches (the pitch classes sounding on each beat), else a new one.
// A repeat is set against its first time: harmony and tempo. POST layer: allocates.
#pragma once
#include <cmath>
#include <cstdint>
#include <string>
#include <vector>

namespace dz {

struct FormSection {
    int firstBar, bars;     // 1-based
    char label;             // 'A', 'B', ...
    int first;              // index of the label's first section (itself when new)
    float matchPct;         // harmony against the first time (100 for itself)
    float bpm;              // heard; 0 when unknown
};

struct FormReport {
    int phraseBars = 0;
    std::string form;                 // "AABA"
    std::vector<FormSection> sections;
    int repeats = 0;                  // sections that repeat an earlier one
    float repeatMatchPct = 0;         // their mean harmony match
    float repeatTempoPct = NAN;       // their mean |tempo change| against the first time, %; NaN: no tempo heard
};

// beatMasks: per beat of the grid, the pitch classes sounding (bit k: pitch class k; 0: none).
// grid: the beats (seconds), the first a downbeat; heardBeats: the player's, for the tempo.
FormReport form_report(const std::vector<uint16_t>& beatMasks, int beatsPerBar, const std::vector<double>& grid,
                       const std::vector<double>& heardBeats);

}  // namespace dz
