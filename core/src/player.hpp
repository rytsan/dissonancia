// STUDIO playback (studio-plan S1): a whole file decoded to float in memory, played on its own
// output device, with a min/max peak mipmap for the waveform at any zoom. Separate from the LIVE
// engine: the two never share a device or a thread.
#pragma once
#include <atomic>
#include <cstdint>
#include <functional>
#include <string>
#include <vector>

#include "dissonancia.h"
#include "miniaudio.h"
#include "mixdsp.hpp"

namespace dz {

class Player {
public:
    ~Player();
    // Decodes WAV / FLAC / MP3 (miniaudio) to float at the file's own rate and channel count (max 2).
    int load(const char* path);
    // ctx == nullptr: no device (tests call render directly).
    int open_device(ma_context* ctx);
    void close_device();

    void play() { if (frames() > 0) playing_.store(true, std::memory_order_release); }
    void stop() { playing_.store(false, std::memory_order_release); }
    void seek(uint64_t frame) { pos_.store(std::min(frame, frames()), std::memory_order_release); }
    // Loop [a, b) while playing; b <= a turns it off.
    void set_loop(uint64_t a, uint64_t b) { loopA_.store(a); loopB_.store(b > a ? std::min(b, frames()) : 0); }
    void info(PlayerInfo& out) const;

    // S2: the edited take from the original (see ana_player_apply_edits); count 0 = the original.
    int apply_edits(const EditSegment* segs, int count, uint64_t fadeIn, uint64_t fadeOut, float normalizePeakDbfs);
    int save_wav(const char* path) const;

    // S4 mixing (see ana_player_add_track ...): track 0 = the edited take, 1.. = stems.
    int add_track(const char* path);
    void clear_tracks();
    int set_channel(int track, const ChannelParams& p);
    void set_master(const MasterParams& p);
    void meters(MixMeters& out) const;
    int render_tap(uint32_t mask, const char* path) const;
    int bounce(const char* path) const;
    int tracks() const { return 1 + int(stems_.size()); }

    // min/max of the mono mix in each of `columns` equal slices of [start, end).
    void peaks(uint64_t start, uint64_t end, uint32_t columns, float* mn, float* mx) const;

    // Device callback body (public for tests): writes `n` frames of `outChannels` channels.
    void render(float* out, uint32_t n, uint32_t outChannels);

    uint64_t frames() const { return channels_ ? samples_.size() / channels_ : 0; }
    const std::string& error() const { return error_; }

private:
    void build_mipmap();
    std::vector<float> original_;         // the decoded file, never changed
    std::vector<float> samples_;          // interleaved, channels_ per frame: the edited take
    uint32_t rate_ = 0, channels_ = 0;
    // Peak mipmap of the mono mix: level k holds min/max per 2^(k + kBase) frames.
    static constexpr int kBase = 8;
    std::vector<std::vector<float>> mipMin_, mipMax_;
    // Stems: stereo at rate_, the take's length. Changed only with the device closed.
    std::vector<std::vector<float>> stems_;
    // Parameters cross to the audio thread through a seqlock per slot (writer: odd while copying).
    struct ChannelSlot { std::atomic<uint32_t> seq{0}; ChannelParams p{}; };
    struct MasterSlot { std::atomic<uint32_t> seq{0}; MasterParams p{}; };
    ChannelSlot slots_[ANA_MAX_TRACKS];
    MasterSlot masterSlot_;
    // Audio-thread state.
    mix::Strip strips_[ANA_MAX_TRACKS];
    mix::Bus bus_;
    uint32_t seen_[ANA_MAX_TRACKS]{}, masterSeen_ = 0;
    bool primed_ = false;
    // Meters (audio thread writes per block).
    std::atomic<float> mPeak_[ANA_MAX_TRACKS], mRms_[ANA_MAX_TRACKS], mGr_[ANA_MAX_TRACKS], mMaster_[4], mMasterGr_{0}, mLimGr_{0};
    std::atomic<uint8_t> mGate_[ANA_MAX_TRACKS];
    void sample(int track, uint64_t frame, float& l, float& r) const;
    void sync_params(double fs);
    void reopen_after(const std::function<void()>& change);
    std::atomic<uint64_t> pos_{0}, loopA_{0}, loopB_{0};
    std::atomic<bool> playing_{false};
    ma_device device_{};
    bool deviceOpen_ = false;
    std::string error_;
};

}  // namespace dz
