// Coach metrics R2.1-R2.3 for a whole take: the attacks played, set against a beat grid (the
// metronome's, or the beat heard when there is none). POST layer: allocates.
#pragma once
#include <cstddef>
#include <cstdint>
#include <vector>

namespace dz {

struct Onset {
    double time;   // s, the start of the attack
    float level;   // dB, the attack's peak energy (relative)
};

// The attacks: a rise of log energy over 10 ms that stands out of its neighbourhood, at least
// 50 ms apart; a strum's strings fold into one attack, timed where it starts.
std::vector<Onset> detect_onsets(const float* x, size_t n, uint32_t rate);

struct TimingReport {
    int onsets = 0, matched = 0;         // attacks, and those within an eighth of a sixteenth grid point
    double meanOffsetMs = 0;             // > 0: behind the grid (dragging), < 0: ahead (rushing)
    double spreadMs = 0;                 // standard deviation of the offsets
    double offsetMs[4]{}, accentDb[4]{}; // per sixteenth of the beat (on, e, and, a): mean offset, level against the mean
    int count[4]{};
    double swing = 0;                    // long / short eighth (1 straight, 2 triplet); 0: too few offbeats
    double bpm = 0, driftBpm = 0;        // tempo of the beats, and its change over the take (linear fit)
    double tempoSpreadBpm = 0;           // standard deviation of the beat-to-beat tempo
    double confidence = 0;               // matched / onsets, scaled down under 16 attacks
};

// beats: the grid, seconds (a metronome's beats, or track_beats() on the take).
TimingReport timing_report(const std::vector<Onset>& onsets, const std::vector<double>& beats);

}  // namespace dz
