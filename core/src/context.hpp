// Musical context from the live input, before REC (spec §5 dynamic session): key by
// Temperley profile correlation over a decaying pitch-class histogram (reset after 3 s silence), tempo by the
// autocorrelation of an onset-strength envelope with a log-normal prior around 110 BPM (the
// half/double ambiguity of every tempo estimator). The meter is not estimated: the user sets it.
// Allocates only in the constructor.
#pragma once
#include <cstdint>
#include <vector>

#include "dissonancia.h"

namespace dz {

class ContextTracker {
public:
    explicit ContextTracker(double hopSeconds);

    // pcWeights: 12 pitch-class weights of this hop (chroma or a one-hot sung note), may be null.
    // onsetStrength: >= 0, this hop's onset envelope sample.
    void process(const float* pcWeights, float onsetStrength, ContextEstimate& out);

private:
    void estimate_key(ContextEstimate& out) const;
    void estimate_tempo(ContextEstimate& out) const;

    double hop_;
    float decay_;                   // per-hop key histogram decay (30 s time constant)
    double pc_[12]{};
    double mass_ = 0;               // decayed hop count with pitch evidence
    uint32_t silentHops_ = 0;
    std::vector<float> env_;        // onset envelope ring, last 8 s
    uint32_t envWrite_ = 0, envFill_ = 0, sinceUpdate_ = 0, updateHops_;
    mutable std::vector<float> lin_;   // envelope unrolled oldest first
    ContextEstimate last_{};
};

}  // namespace dz
