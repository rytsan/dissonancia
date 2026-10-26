// The coach's metrics for a REC take, as the session JSON's "metrics" and "limits" members
// (dissonancia-session/1, docs/SESSION.md). POST layer: allocates.
#pragma once
#include <cstddef>
#include <cstdint>
#include <cstdio>

#include "dissonancia.h"

namespace dz {

// x: mono samples from the take's first downbeat; s: the session (metronome tempo and meter, mode,
// A4). clickHeard: the click played during the take, so it is the timing grid; otherwise the beats
// heard in the take are. comp: seconds the recording lags what the player heard (the round trip).
// A voice or single-line mode adds the intonation (R2.4).
void write_take_metrics_json(FILE* f, const float* x, size_t n, uint32_t rate, const SessionConfig& s, bool clickHeard, double comp,
                             uint64_t droppedFrames);

}  // namespace dz
