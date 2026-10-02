// Pipeline A — monophonic voice / instrument (spec §7): integer decimation -> HP -> YIN
// (unwindowed) -> instantaneous pitch every hop -> median of 3 -> note tracker -> events.
// Everything is allocated in the constructor; process() never allocates.
#pragma once
#include <cstdint>
#include <vector>

#include "dissonancia.h"
#include "live_config.hpp"
#include "theory.hpp"

namespace dz {

// Linear-phase windowed-sinc FIR decimator (group delay (taps-1)/2 native samples, compensated).
class Decimator {
public:
    void init(uint32_t factor, uint32_t maxInput);
    uint32_t process(const float* in, uint32_t n, float* out);   // returns output count
    double delay_native() const { return (h_.size() - 1) / 2.0; }
    uint32_t factor() const { return d_; }

private:
    uint32_t d_ = 1, phase_ = 0;
    std::vector<float> h_, buf_;
};

struct Biquad {
    float b0 = 1, b1 = 0, b2 = 0, a1 = 0, a2 = 0, z1 = 0, z2 = 0;
    void highpass(double fc, double fs);
    float operator()(float x) {
        float y = b0 * x + z1;
        z1 = b1 * x - a1 * y + z2;
        z2 = b2 * x - a2 * y;
        return y;
    }
};

// YIN over an unwindowed frame of `window` samples: lags up to rate/fMin, integration = window - maxLag.
class Yin {
public:
    // halfThreshold > 0: prefer half the chosen lag when its CMND dip is below it (octave-down guard).
    void init(double rate, uint32_t window, float fMin, float fMax, float threshold = 0.15f, float halfThreshold = 0);
    // Returns f0 in Hz, or 0 when unvoiced. clarity = 1 - CMND at the chosen lag.
    float estimate(const float* x, float& clarity);
    uint32_t window() const { return window_; }

private:
    double rate_ = 16000;
    uint32_t window_ = 0, tauMin_ = 2, tauMax_ = 0;
    float threshold_ = 0.15f, halfThreshold_ = 0;
    std::vector<float> d_;
};

struct VoiceOutput {
    PitchEstimate pitch;
    NoteEstimate note;
    float noteLatencySeconds;          // sample clock: estimated onset -> note confirmed
    uint32_t eventCount;
    AnalyzerEvent events[4];
};

class VoicePipeline {
public:
    // latencyCompensation: subtracted from event times (§13 backdating).
    VoicePipeline(const SessionConfig& s, const LiveConfig& c, uint32_t nativeRate, uint32_t maxHopFrames, double latencyCompensation);

    // x: one native-rate mono hop ending at input frame endFrame.
    void process(const float* x, uint32_t n, uint64_t endFrame, VoiceOutput& out);
    // Closes an open note (REC stop / ana_stop), emitting its complete NoteEnd.
    void flush(VoiceOutput& out);

private:
    enum class State { Silence, Candidate, Stable };
    struct Note {
        int midi;
        Spelled spelled;
        uint64_t startFrame;
        double sumHz, sumCents, sumCentsSq, sumConf;
        uint32_t count;
    };

    double seconds(uint64_t frame) const { return double(frame) / rate_ - delay_ - latencyComp_; }
    void emit(VoiceOutput& out, AnalyzerEventType type, const Note& n, uint64_t endFrame);
    void accumulate(float hz, float confidence);

    SessionConfig session_;
    LiveConfig cfg_;
    double rate_, liveRate_, delay_, latencyComp_;
    Decimator dec_;
    Biquad hp_;
    Yin yin_;
    Speller speller_;
    int octaveShift_;
    std::vector<float> decOut_, window_, hzHistory_;
    uint32_t filled_ = 0, windowNative_ = 0, minCandidateHops_ = 2, releaseHops_ = 5;
    float gate_ = 0.00316f;            // -50 dBFS hop RMS; calibration knob
    bool wasSilent_ = true;
    uint64_t energyOnset_ = 0, lastVoicedEnd_ = 0;

    float med_[3]{};
    uint32_t medCount_ = 0, medPos_ = 0;

    State state_ = State::Silence;
    Note cur_{};
    int previousMidi_ = -1;
    int candMidi_ = -1;
    uint32_t candHops_ = 0, unvoicedHops_ = 0;
    uint64_t candStart_ = 0;
    float lastLatency_ = 0;
};

}  // namespace dz
