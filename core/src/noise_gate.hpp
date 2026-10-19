// Adaptive silence gate (minimum statistics): the quietest hop level over the last few seconds is
// the room's noise (hiss, fan, interface hum); a hop is silent below max(absolute, floor x margin),
// the margin capped so a loud song that is never quiet cannot gate its own soft passages.
// No allocation; one per pipeline.
#pragma once
#include <algorithm>
#include <cmath>

namespace dz {

class NoiseGate {
public:
    static constexpr int kBlocks = 5;   // floor = minimum over kBlocks x blockSeconds (~5 s)

    void init(double hopSeconds, float absolute, double blockSeconds = 1.0) {
        absolute_ = absolute;
        hopsPerBlock_ = std::max(1, int(std::lround(blockSeconds / hopSeconds)));
        std::fill(blockMin_, blockMin_ + kBlocks, absolute);   // starts as the fixed gate, adapts as blocks rotate
        cur_ = 1e9f;
        hop_ = block_ = 0;
    }

    bool silent(float rms) {
        cur_ = std::min(cur_, rms);
        if (++hop_ == hopsPerBlock_) {
            blockMin_[block_] = cur_;
            block_ = (block_ + 1) % kBlocks;
            cur_ = 1e9f;
            hop_ = 0;
        }
        return rms < threshold();
    }

    float floor() const { return std::min(cur_, *std::min_element(blockMin_, blockMin_ + kBlocks)); }
    float threshold() const { return std::max(absolute_, std::min(floor() * kMargin, kCap)); }

private:
    static constexpr float kMargin = 2.f;      // +6 dB over the floor; calibration knob
    static constexpr float kCap = 0.0316f;     // -30 dBFS: the adaptive gate never goes above it
    float absolute_ = 0.003f, cur_ = 1e9f, blockMin_[kBlocks]{};
    int hopsPerBlock_ = 50, hop_ = 0, block_ = 0;
};

}  // namespace dz
