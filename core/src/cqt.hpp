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
#include <memory>
#include <vector>

#include "dissonancia.h"
#include "halfband.hpp"

namespace dz { class OnsetDetector; class BassTracker; }

namespace dz {

class Cqt {
public:
    static constexpr int kTuningSets = 11;   // -50..+50 cents in 10-cent steps, precomputed
    static constexpr float kTuningStep = 10.f;

    // fMin is snapped to the tempered grid of a4. Bins: fMin * 2^(k / bpo), whole octaves.
    void init(double liveRate, float fMin, float fMax, int bpo, float a4, uint32_t maxBlock);
    void ensure_history(uint32_t samples);          // per octave; init time only (allocates)
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
    // Octave o (0 = top) signal, newest sample last, and its rate.
    const float* octave_samples(int o, uint32_t& length) const { length = bufLen_; return buf_[size_t(o)].data(); }
    double octave_rate(int o) const { return liveRate_ / double(1u << o); }
    int octave_of(int bin) const { return octaves_ - 1 - bin / bpo_; }
    double bin_window_seconds(int bin) const {
        return kernels_[size_t(tuningSet_ * bpo_ + bin % bpo_)].length / octave_rate(octave_of(bin));
    }
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
        BassEstimate bass;
        bool onset;
        double lastOnset;              // sample clock (hop start of the onset hop), < 0 = none
        float flux;
        uint16_t gatedBins;            // low bins still holding pre-onset signal (excluded)
    };

    // bassMin/Max = 0: no bass tracker. settleSeconds: computed T_low (§4).
    ChromaFrontEnd(const SessionConfig& s, uint32_t decimation, double nativeRate, float fMin, float fMax, int bpo, uint32_t maxHop,
                   float bassMin = 0, float bassMax = 0, double settleSeconds = 0, bool autoTune = true);
    ~ChromaFrontEnd();
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
    std::unique_ptr<OnsetDetector> onsets_;   // forward-declared: keeps cqt.hpp free of bass.hpp
    std::unique_ptr<BassTracker> bass_;
    std::vector<uint8_t> gated_;
    // Harmonic magnitudes for the chroma: per-bin median over the last kMedianHops hops (drums and
    // attacks are short in time, chords are long), whitened across frequency, weighted by register.
    static constexpr int kMedianHops = 3;
    std::vector<float> history_, harmonic_, registerWeight_;
    int historyPos_ = 0;
    double lastOnset_ = -1, hopSeconds_ = 0.02;
};

}  // namespace dz
