// Multi-resolution CQT (spec §8) + chroma (§9) + global tuning (§8).
//
// Octave-decimated filter bank: octave 0 (top) runs at the live rate, each lower octave
// at half the rate of the one above (IIR half-band). All octaves share ONE kernel set in
// normalized frequency. Each bin is a direct time-domain dot product of the newest L_k
// samples with a Hann-windowed complex exponential (L_k = Q fs / f_k): only one frame per
// hop is needed, so this is cheaper than per-octave FFTs at these sizes (measured in the
// microbench, tests/test_cqt.cpp).
#pragma once
#include <cstdint>
#include <vector>

#include "dissonancia.h"
#include "halfband.hpp"

namespace dz {

class Cqt {
public:
    static constexpr int kTuningSets = 11;   // -50..+50 cents in 10-cent steps, precomputed
    static constexpr float kTuningStep = 10.f;

    // fMin is snapped to the tempered grid of a4. Bins: fMin * 2^(k / bpo), whole octaves.
    void init(double liveRate, float fMin, float fMax, int bpo, float a4, uint32_t maxBlock);
    void push(const float* x, uint32_t n);         // live-rate samples
    void compute(float* magnitude) const;          // bins() values, amplitude-calibrated
    void set_tuning(float cents);                  // nearest precomputed set
    float tuning() const { return (tuningSet_ - kTuningSets / 2) * kTuningStep; }

    uint16_t bins() const { return uint16_t(bpo_ * octaves_); }
    int bins_per_octave() const { return bpo_; }
    int octaves() const { return octaves_; }
    float min_hz() const { return fMin_; }                    // untuned grid
    int min_midi() const { return minMidi_; }
    double octave_delay_seconds(int octave) const { return delay_[size_t(octave)]; }   // 0 = top
    double lowest_window_seconds() const;                     // T_low of the current set
    // Magnitude a bin shows for a tone centred on its neighbour bin (window main lobe), measured at init.
    float neighbour_leakage() const { return leakage_; }

private:
    struct Kernel { uint32_t offset, length; };
    int bpo_ = 12, octaves_ = 1, minMidi_ = 40, tuningSet_ = kTuningSets / 2;
    float fMin_ = 82.4f;
    double liveRate_ = 24000;
    std::vector<Kernel> kernels_;     // [set][j]
    std::vector<float> re_, im_;      // SoA kernel coefficients
    std::vector<HalfbandDecimator> dec_;
    std::vector<std::vector<float>> buf_;   // per octave, newest sample last
    std::vector<float> scratchA_, scratchB_;
    std::vector<double> delay_;
    uint32_t bufLen_ = 0;
    float leakage_ = 0.5f;
};

// Chord-mode front end: native hop -> (half-band to the live rate) -> CQT -> chroma + tuning.
class ChromaFrontEnd {
public:
    struct Output {
        uint16_t bins, binsPerOctave;
        float minHz;
        float magnitude[ANA_MAX_CQT_BINS];
        ChromaVector chroma;
        TuningEstimate tuning;
    };

    ChromaFrontEnd(const SessionConfig& s, uint32_t decimation, double nativeRate, float fMin, float fMax, int bpo, uint32_t maxHop,
                   bool autoTune = true);
    void process(const float* x, uint32_t n, uint64_t endFrame, Output& out);
    const Cqt& cqt() const { return cqt_; }

private:
    void update_tuning(const float* mag, uint16_t bins, Output& out);

    SessionConfig session_;
    double nativeRate_;
    bool decimate_, autoTune_;
    HalfbandDecimator toLive_;
    Cqt cqt_;
    std::vector<float> live_;
    float smoothed_[12]{};
    float gate_ = 0.003f;   // hop RMS gate (~ -50 dBFS); calibration knob
    std::vector<float> deviations_, sorted_;
    uint32_t devCount_ = 0, hopsSinceTuning_ = 0;
    TuningEstimate tuning_{};
};

}  // namespace dz
