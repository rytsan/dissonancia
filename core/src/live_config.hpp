// Mode x quality -> fixed live configuration (spec §5). The ONLY source of live settings.
#pragma once
#include <cstdint>

#include "dissonancia.h"

namespace dz {

struct LiveConfig {
    uint32_t decimation;     // integer divisor of the device rate
    float hopSeconds;
    float windowSeconds;     // mono: YIN window; chords: T_low = Q / f_min
    float fMin, fMax;        // analysis range (chords: CQT range)
    float bassMin, bassMax;  // bass preview range (chord modes only)
    uint8_t binsPerOctave;   // 0 = no CQT (mono pipelines)
};

inline bool is_chord_mode(AnalysisMode m) { return m >= AnalysisMode::GuitarChords; }

// Largest integer divisor that keeps the rate >= target (48k/16k = 3, 44.1k/16k = 2 -> 22.05k).
inline uint32_t divisor_for(uint32_t nativeRate, uint32_t targetRate) {
    uint32_t d = nativeRate / targetRate;
    return d < 1 ? 1 : d;
}

inline LiveConfig live_config(AnalysisMode mode, AudioQuality q, uint32_t nativeRate) {
    using Q = AudioQuality;
    LiveConfig c{};
    if (!is_chord_mode(mode)) {
        bool voice = mode == AnalysisMode::VoiceMono;
        switch (q) {
            case Q::LowLatency: c = {divisor_for(nativeRate, 16000), 0.005f, 0.040f, voice ? 70.f : 60.f, voice ? 1200.f : 2000.f, 0, 0, 0}; break;
            case Q::Balanced: c = {divisor_for(nativeRate, 16000), 0.010f, 0.050f, voice ? 70.f : 60.f, voice ? 1200.f : 2000.f, 0, 0, 0}; break;
            case Q::HighPrecision: c = {divisor_for(nativeRate, 22050), 0.010f, voice ? 0.070f : 0.080f, voice ? 50.f : 40.f, voice ? 2000.f : 4000.f, 0, 0, 0}; break;
        }
        return c;
    }
    bool piano = mode == AnalysisMode::PianoChords;
    switch (q) {
        case Q::LowLatency: c = {2, 0.020f, 0, 100.f, 4200.f, piano ? 55.f : 70.f, piano ? 600.f : 500.f, 12}; break;
        case Q::Balanced: c = {2, 0.020f, 0, 82.4f, 4200.f, piano ? 55.f : 70.f, piano ? 600.f : 500.f, 12}; break;
        case Q::HighPrecision: c = {1, 0.020f, 0, piano ? 55.f : 82.4f, piano ? 4186.f : 4200.f, piano ? 55.f : 70.f, piano ? 600.f : 500.f, 24}; break;
    }
    c.windowSeconds = 1.443f * c.binsPerOctave / c.fMin;   // settle = Q / f_min, computed (§4)
    return c;
}

}  // namespace dz
