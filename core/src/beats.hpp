// STUDIO S5: offline beat tracking of a whole take (the drums channel at its tap) for the tempo
// map. Onset envelope (log energy flux, 10 ms hop), global period from its autocorrelation with a
// log-normal prior (narrow around the session BPM when given), then Ellis's dynamic programming
// (2007): every beat chosen with the whole take in view, free to follow a drifting drummer.
// POST layer: allocates.
#pragma once
#include <cstddef>
#include <cstdint>
#include <vector>

namespace dz {

struct BeatTrack {
    double bpm = 0;              // global tempo
    std::vector<double> beats;   // seconds from the start of the input
};

// bpmHint > 0: the session tempo (a REC take's metronome), else a broad prior around 110 BPM.
BeatTrack track_beats(const float* x, size_t n, uint32_t rate, double bpmHint);

}  // namespace dz
