// LIVE engine, M0 skeleton (spec §3): callback -> rings -> analysis thread -> snapshot/events,
// recorder thread -> WAV + sidecar, metronome click on the input clock.
#pragma once
#include <atomic>
#include <cstdint>
#include <string>
#include <thread>
#include <vector>

#include "dissonancia.h"
#include "live_config.hpp"
#include "lockfree.hpp"
#include "miniaudio.h"

namespace dz {

class Engine {
public:
    Engine();
    ~Engine();

    // ctx == nullptr: headless (tests drive on_audio directly with d.sampleRate, 1 channel).
    int start(const SessionConfig& s, const AudioDeviceConfig& d, ma_context* ctx, const ma_device_id* captureId);
    int stop();
    bool running() const { return running_; }

    void read_snapshot(LiveSnapshot* out);
    size_t drain_events(AnalyzerEvent* out, size_t cap) { return events_.pop_many(out, cap); }
    bool push_event(AnalyzerEvent e);   // analysis thread only

    int rec_start(const char* wavPath);
    int rec_stop();
    int set_metronome(bool on, float bpm, TimeSignature meter);
    void clear_clip() { clearClip_.store(true, std::memory_order_relaxed); }

    // Audio callback body; public for headless tests. in: interleaved captureChannels_, out: playbackChannels_ (may be null).
    void on_audio(const float* in, float* out, uint32_t frames);

    uint64_t notify_count() const { return notifies_.load(std::memory_order_relaxed); }
    uint64_t callback_frames() const { return cbFrames_.load(std::memory_order_acquire); }
    const std::string& error() const { return error_; }

private:
    void analysis_loop();
    void recorder_loop();
    void process_hop(const float* x, uint32_t n);
    void mix_click(float* out, uint32_t frames, uint64_t pos);
    void write_sidecar(uint64_t frames, uint64_t dropped);
    uint64_t next_downbeat_after(uint64_t frame, uint32_t extraBars) const;
    double beat_frames() const;

    // session
    SessionConfig session_{};
    LiveConfig live_{};
    uint32_t rate_ = 48000, captureChannels_ = 1, playbackChannels_ = 0, hopFrames_ = 480, colFrames_ = 256;
    float captureLatencyMs_ = 0;
    bool running_ = false, deviceOpen_ = false;
    ma_device device_{};
    std::string error_;

    // rings
    ma_pcm_rb analysisRing_{}, recorderRing_{};
    bool ringsInit_ = false;

    // callback -> analysis wake-up (§3 notify rule)
    std::atomic<uint32_t> wake_{0};
    std::atomic<bool> waiting_{false}, stopFlag_{false};
    std::atomic<uint64_t> notifies_{0};

    // callback state (callback thread only, except the atomics)
    uint64_t cbPos_ = 0;
    std::atomic<uint64_t> cbFrames_{0};
    std::atomic<double> cbTime_{0};
    std::atomic<uint32_t> xruns_{0};

    // metronome parameters (API thread writes, then bumps metroGen_)
    std::atomic<bool> metroOn_{true};
    std::atomic<float> metroBpm_{120};
    std::atomic<uint8_t> beatsPerBar_{4};
    std::atomic<uint64_t> metroOrigin_{0};
    std::atomic<uint32_t> metroGen_{0};
    uint32_t cbMetroGen_ = ~0u;
    double cbBeatFrames_ = 0;
    uint64_t cbOrigin_ = 0, cbNextBeat_ = 0;
    uint32_t cbBeatsPerBar_ = 4, cbClickPos_ = ~0u;
    bool cbMetroOn_ = false, cbAccent_ = false;
    std::vector<float> clickAccent_, clickBeat_;

    // REC window on the input sample clock
    static constexpr uint64_t kNever = ~0ull;
    std::atomic<uint64_t> recStart_{kNever}, recStop_{kNever};
    std::atomic<uint64_t> recDropped_{0}, recWritten_{0};
    std::atomic<uint32_t> recorderGaps_{0};
    std::atomic<ma_encoder*> encoder_{nullptr};
    std::string recPath_;

    // analysis state (analysis thread only)
    std::vector<float> hopBuf_;
    uint64_t anFrames_ = 0;
    float meanSquare_ = 0, peakHoldDb_ = -120, vu_ = -40;
    double peakHoldT_ = 0;
    bool clip_ = false;
    std::atomic<bool> clearClip_{false};
    float colMin_ = 1, colMax_ = -1;
    uint32_t colFill_ = 0, waveWrite_ = 0, scopeWrite_ = 0;
    std::vector<float> waveMin_, waveMax_, scope_;
    float cpu_ = 0;
    uint64_t seq_ = 0;
    uint32_t eventSeq_ = 0;
    std::atomic<uint32_t> droppedEvents_{0};

    TripleBuffer<LiveSnapshot> snapshots_;
    SpscQueue<AnalyzerEvent, ANA_EVENT_QUEUE_CAPACITY> events_;

    std::thread analysisThread_, recorderThread_;
};

}  // namespace dz
