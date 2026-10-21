// LIVE playground (creation): 16 pads of one-shot WAVs and a sampler (one WAV played on MIDI
// notes, repitched), MIDI input, and a song — blocks of bars (a pad pattern, sampler notes, an
// audio loop) on a timeline. Rendered in the LIVE engine's duplex callback, after the click: on
// the same sample clock as the input and the metronome, so a step lands on the beat the analysis
// hears. No allocation on the audio thread: samples, loops and songs are built by the API thread
// and published by pointer; a replaced one is freed once no callback can still hold it.
#pragma once
#include <algorithm>
#include <atomic>
#include <cstdint>
#include <map>
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
    int song(const PgBlock* blocks, int nb, const uint8_t* steps, int ns, const PgNote* notes, int nn, const PgEntry* order, int no, std::string& error);
    void play(int entry, bool loop) { cmd_.try_push({1, entry, loop}); }
    void stop() { cmd_.try_push({0, -1, false}); }
    // barFrames: the loops' length per bar at the tempo now (the engine's).
    int record(bool on, double barFrames);
    size_t recorded(PgRecorded* out, size_t cap) { return rec_.pop_many(out, cap); }
    int loop_file(uint32_t id, const char* path, bool save, uint32_t rate, std::string& error);
    int bounce(const char* path, uint32_t rate, double beatFrames, int beatsPerBar, std::string& error);
    // Any thread that is the only one calling it (the API thread, the MIDI thread: one queue each).
    void midi_from_api(uint8_t s, uint8_t d1, uint8_t d2) { push(s, d1, d2); }
    void midi_from_device(uint8_t s, uint8_t d1, uint8_t d2);
    size_t read_midi(uint32_t* out, size_t cap) { return raw_.pop_many(out, cap); }
    void start(uint32_t rate, uint32_t outLatencyFrames, uint32_t roundTripFrames);   // engine start: no callback runs yet
    void stop_engine();                                                                // no callback runs any more

    // Audio thread: adds to out (ch channels); in: the input (inCh channels, may be null). The grid:
    // the metronome's origin, beat length, meter.
    void render(float* out, uint32_t frames, uint32_t ch, const float* in, uint32_t inCh, uint64_t pos, uint64_t origin, double beatFrames,
                int beatsPerBar);

    struct Sample { std::vector<float> lr; uint64_t frames = 0; };
    struct Loop { std::vector<float> mono; };   // its length: the block's bars at the tempo it was made for
    struct Block {
        uint32_t id = 0;
        int bars = 1;
        std::vector<uint8_t> steps;              // [pad][bar * 16 + sixteenth]
        std::vector<PgNote> notes;
        Loop* loop = nullptr;
        uint8_t at(int pad, int step) const { return steps[size_t(pad) * size_t(bars) * ANA_PG_STEPS_PER_BAR + size_t(step)]; }
    };
    struct Song { std::vector<Block> blocks; std::vector<PgEntry> order; uint64_t gen = 0; };

private:
    struct Cmd { int kind, entry; bool loop; };
    // The song's player: transport, voices, the bar / sixteenth clock. The live one runs in the
    // callback; a bounce runs its own.
    struct Deck {
        struct Voice {
            const Sample* s = nullptr;
            int slot = -1, note = -1, stepsLeft = -1;   // stepsLeft: a song note's length, -1 = held by the player
            double pos = 0, rate = 1;
            float gain = 0, env = 1;
            bool release = false;
        };
        Voice voice[48];
        const Song* song = nullptr;
        bool playing = false, starting = false, looping = false, recording = false, once = false, ended = false;
        int entry = -1, repeat = 0, bar = 0, queued = -1;
        bool queuedLoop = false;
        uint64_t nextStep = 0;     // the next sixteenth's index from the origin
        double stepOrigin = -1, stepFrames = 0;
        uint64_t barStart = 0;
        int stepInBar = -1;
        int noteStep[128];         // REC: a held note's start (sixteenths in the block), -1 = none
        int64_t noteAbs[128]{};    // ... its sixteenth from the origin (its length when it is let go)
        uint8_t noteVel[128]{};
        uint32_t noteBlock[128]{};
    };
    void push(uint8_t s, uint8_t d1, uint8_t d2);
    void trigger(Deck& d, int slot, int note, float vel, int stepsLeft = -1);
    void render_deck(Deck& d, float* buf, uint32_t n, const float* in, uint32_t inCh, uint64_t p0, uint64_t origin, double beatFrames, int bpb, bool live);
    void on_step(Deck& d, uint64_t at, int stepsPerBar, double barFrames, bool live);
    void retire_free();

    std::atomic<Sample*> slot_[ANA_PADS + 1]{};
    std::mutex apiMutex_;                                   // API thread only (load, song, loops)
    std::vector<std::pair<Sample*, uint64_t>> retired_;     // freed once callbacks passed
    std::vector<std::pair<Song*, uint64_t>> retiredSongs_;  // freed once the callback holds a later one
    std::vector<std::pair<Loop*, uint64_t>> retiredLoops_;
    std::map<uint32_t, Loop*> loops_;                       // by block id (API thread)
    std::vector<PgBlock> lastBlocks_;                       // the song as given, to publish it again with new loops
    std::vector<uint8_t> lastSteps_;
    std::vector<PgNote> lastNotes_;
    std::vector<PgEntry> lastOrder_;
    std::atomic<Song*> song_{nullptr};                      // the newest song
    std::atomic<uint64_t> adopted_{0};                      // the generation the callback plays
    uint64_t gen_ = 0;
    std::atomic<uint64_t> callbacks_{0};
    std::atomic<bool> running_{false}, recordWant_{false};
    uint32_t rate_ = 48000, outLatency_ = 0, roundTrip_ = 0;
    int publish(std::string& error);                        // a song from the last one given, with the loops now

    struct Slot { std::atomic<uint32_t> seq{0}; PlaygroundConfig c{}; };
    Slot cfg_;
    uint32_t cfgSeen_ = ~0u;
    PlaygroundConfig cur_{};                                // audio thread's copy
    float gain_[ANA_PADS + 1][2]{};
    float loopGain_ = 1, master_ = 1;

    SpscQueue<uint32_t, 256> fromApi_, fromDevice_, raw_;   // packed status | d1 << 8 | d2 << 16
    SpscQueue<Cmd, 64> cmd_;
    SpscQueue<PgRecorded, 1024> rec_;
    Deck live_;
    std::atomic<int32_t> entry_{-1}, repeat_{0}, bar_{0}, step_{-1}, queued_{-1}, voices_{0};
    std::atomic<uint8_t> flags_{0};
    std::atomic<uint32_t> hits_[ANA_PADS + 1]{}, midiCount_{0};
    std::atomic<float> peak_{0};
    float seconds_[ANA_PADS + 1]{};                         // API thread (status)
};

}  // namespace dz
