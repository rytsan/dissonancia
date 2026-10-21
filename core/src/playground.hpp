// LIVE playground (creation): 16 pads of one-shot WAVs and a sampler (one WAV played on MIDI
// notes, repitched), a 16-step sequencer on the metronome's grid, MIDI input. Rendered in the LIVE
// engine's duplex callback, after the click: on the same sample clock as the input and the
// metronome, so a step lands on the beat the analysis hears. No allocation on the audio thread:
// samples are decoded by the API thread and published by pointer; a replaced one is freed once
// no callback can still hold it.
#pragma once
#include <algorithm>
#include <atomic>
#include <cstdint>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#include "dissonancia.h"
#include "lockfree.hpp"

namespace dz {

class Playground {
public:
    static constexpr int kSampler = ANA_PADS;   // the slot after the pads
    Playground();
    ~Playground();

    // API thread. rate: the engine's (the WAV is resampled to it); "" or nullptr empties the slot.
    int load(int slot, const char* path, uint32_t rate, std::string& error);
    void set(const PlaygroundConfig& c);
    void hit(int pad, float velocity) { push(uint8_t(0x99), uint8_t(36 + pad), uint8_t(std::clamp(velocity, 0.f, 1.f) * 127)); }
    void status(PlaygroundStatus& out) const;
    // Any thread that is the only one calling it (the API thread, the MIDI thread: one queue each).
    void midi_from_api(uint8_t s, uint8_t d1, uint8_t d2) { push(s, d1, d2); }
    void midi_from_device(uint8_t s, uint8_t d1, uint8_t d2);
    size_t read_midi(uint32_t* out, size_t cap) { return raw_.pop_many(out, cap); }
    void start(uint32_t rate);   // engine start: no callback runs yet
    void stop();                 // engine stopped: no callback runs any more

    // Audio thread: adds to out (ch channels). The grid: the metronome's origin, beat length, meter.
    void render(float* out, uint32_t frames, uint32_t ch, uint64_t pos, uint64_t origin, double beatFrames, int beatsPerBar);

private:
    struct Sample { std::vector<float> lr; uint64_t frames = 0; };
    struct Voice {
        const Sample* s = nullptr;
        int slot = -1, note = -1;
        double pos = 0, rate = 1;
        float gain = 0, env = 1;
        bool release = false;
    };
    void push(uint8_t s, uint8_t d1, uint8_t d2);
    void note_on(int note, float vel, int at);
    void trigger(int slot, int note, float vel);

    std::atomic<Sample*> slot_[ANA_PADS + 1]{};
    std::mutex loadMutex_;                                  // API thread only
    std::vector<std::pair<Sample*, uint64_t>> retired_;     // freed once callbacks passed
    std::atomic<uint64_t> callbacks_{0};
    std::atomic<bool> running_{false};
    uint32_t rate_ = 48000;

    struct Slot { std::atomic<uint32_t> seq{0}; PlaygroundConfig c{}; };
    Slot cfg_;
    uint32_t cfgSeen_ = ~0u;
    PlaygroundConfig cur_{};                                // audio thread's copy
    float gain_[ANA_PADS + 1][2]{};
    float master_ = 1;

    SpscQueue<uint32_t, 256> fromApi_, fromDevice_, raw_;   // packed status | d1 << 8 | d2 << 16
    Voice voice_[32];
    uint64_t nextStep_ = 0;                                 // the next step's index from the origin
    double stepOrigin_ = -1, stepFrames_ = 0;
    std::atomic<int32_t> step_{-1};
    std::atomic<uint32_t> hits_[ANA_PADS + 1]{}, midiCount_{0};
    std::atomic<float> peak_{0};
    std::atomic<int32_t> voices_{0};
    float seconds_[ANA_PADS + 1]{};                         // API thread (status)
};

}  // namespace dz
