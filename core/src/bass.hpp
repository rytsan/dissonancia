// Onsets (spec §14) and bass (§11) for the chord pipelines.
//
// Onsets: positive log-magnitude spectral flux over the upper CQT octaves (short windows,
// so fast), adaptive threshold, refractory period; silence -> sound is always an onset.
// Bass: (1) preview = YIN periodicity on a decimated low octave of the CQT cascade, every
// hop, never settled; (2) confirmation = lowest strong CQT peak in the bass range, using
// only bins whose window has refilled since the last onset. Settled only after the
// computed settleSeconds and agreement with the preview (or CQT alone if the preview is
// unvoiced). An octave-below preview (sub-harmonic of a chord) counts as agreement.
#pragma once
#include <cstdint>
#include <vector>

#include "cqt.hpp"
#include "dissonancia.h"
#include "theory.hpp"
#include "voice.hpp"

namespace dz {

class OnsetDetector {
public:
    void init(const Cqt& cqt, double hopSeconds);
    // Returns true when an onset is detected in this hop. flux is reported for display/tests.
    bool process(const float* magnitude, bool silent, float& flux);

private:
    std::vector<float> prev_;
    int firstBin_ = 0, bins_ = 0;
    float mean_ = 0, var_ = 0;
    uint32_t refractoryHops_ = 4, sinceOnset_ = 1000;
    bool wasSilent_ = true;
};

class BassTracker {
public:
    // bassMin/Max: range of the bass line (guitar 70-500 Hz); settleSeconds = Q / f_min (§4).
    BassTracker(const SessionConfig& s, const Cqt& cqt, float bassMin, float bassMax, double settleSeconds);
    uint32_t history_samples() const { return window_; }

    // t: hop end on the sample clock; gated[k] != 0: bin k still holds pre-onset signal.
    void process(const Cqt& cqt, const float* magnitude, const uint8_t* gated, double t, double lastOnset, BassEstimate& out);

private:
    SessionConfig session_;
    Speller speller_;
    int octave_ = 0, firstBin_ = 0, lastBin_ = 0, octaveShift_ = 0;
    uint32_t window_ = 0;
    float bassMin_, bassMax_;
    double settle_;
    Yin yin_;
    int lastMidi_ = -1;
};

}  // namespace dz
