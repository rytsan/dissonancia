// Tempo heard in the live input, for beat marks on the waveform display (never for the session:
// the BPM of the metronome, REC and the score is set by hand). Autocorrelation of an onset-strength
// envelope over the last 8 s with a log-normal prior around 110 BPM, half-period rule, octave
// folding into 60-180; the beat phase from the grid offset that collects the most envelope.
// Allocates only in the constructor.
#pragma once
#include <cstdint>
#include <vector>

#include "dissonancia.h"

namespace dz {

class TempoTracker {
public:
    explicit TempoTracker(double hopSeconds);

    // onsetStrength: >= 0, this hop's onset envelope sample; now: hop end on the sample clock.
    void process(float onsetStrength, double now, ContextEstimate& out);

private:
    void estimate(ContextEstimate& out) const;

    double hop_, now_ = 0;
    std::vector<float> env_;        // onset envelope ring, last 8 s
    uint32_t envWrite_ = 0, envFill_ = 0, sinceUpdate_ = 0, updateHops_;
    mutable std::vector<float> lin_;   // envelope unrolled oldest first
    ContextEstimate last_{};
};

}  // namespace dz
