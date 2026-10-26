// Coach metric R2.7 for a whole take: loudness (BS.1770, loudness.hpp) over the take, per bar and per
// phrase. The attacks' consistency comes from timing_report (levelSpreadDb). POST layer: allocates.
#pragma once
#include <cstddef>
#include <cstdint>
#include <vector>

namespace dz {

struct PhraseDynamics {
    double start, end;
    float lufs;           // the phrase's loudness
    float changeLu;       // its last third against its first (> 0 growing)
    float peakAt;         // where its loudest 400 ms falls, 0..1
};

struct DynamicsReport {
    float integratedLufs = -120, rangeLu = 0;   // EBU R128 integrated loudness and loudness range
    std::vector<float> barLufs;                 // per bar from the first downbeat; -120 silent
    float trendLu = 0;                          // fitted change of the bars' loudness over the take
    std::vector<PhraseDynamics> phrases;        // sound runs of 1.5 s or more, gaps under 0.4 s
};

// x: mono; barSeconds: the metronome's bar (0: no bars).
DynamicsReport dynamics_report(const float* x, size_t n, uint32_t rate, double barSeconds);

}  // namespace dz
