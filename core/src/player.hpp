// STUDIO playback (studio-plan S1): a whole file decoded to float in memory, played on its own
// output device, with a min/max peak mipmap for the waveform at any zoom. Separate from the LIVE
// engine: the two never share a device or a thread.
//
// The mix is rendered ahead of the device (S11): a worker thread renders blocks into a ring about a
// second ahead, each with the engine's state before it; the device callback only copies them out.
// A change (the mix, a seek, the loop) cuts the ring a few blocks after the one playing, restores
// the state saved with the block at the cut and renders again from there: heard in ~50 ms, with the
// filters' memory intact. Without a device (tests, screenshots) the callback renders what it needs.
#pragma once
#include <atomic>
#include <cstdint>
#include <functional>
#include <memory>
#include <string>
#include <thread>
#include <vector>

#include "dissonancia.h"
#include "miniaudio.h"
#include "loudness.hpp"
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
    void seek(uint64_t frame);
    // Loop [a, b) while playing; b <= a turns it off.
    void set_loop(uint64_t a, uint64_t b) { loopA_.store(a); loopB_.store(b > a ? std::min(b, frames()) : 0); dirty_++; }
    void info(PlayerInfo& out) const;

    // S2: the edited take from the original (see ana_player_apply_edits); count 0 = the original.
    int apply_edits(const EditSegment* segs, int count, uint64_t fadeIn, uint64_t fadeOut, float normalizePeakDbfs);
    int save_wav(const char* path) const;

    // S4 mixing (see ana_player_add_track ...): track 0 = the edited take, 1.. = stems.
    int add_track(const char* path);
    void clear_tracks();
    // S8: a track's pre-chain source — its audio rendered through the channel's VST3 inserts
    // (offline, by the host helper), the take's length; nullptr / "" restores the original.
    int set_source(int track, const char* path);
    int set_mix(const MixGraph& g);
    void meters(MixMeters& out) const;
    int render_tap(uint32_t mask, const char* path) const;
    int bounce(const char* path) const;
    int tracks() const { return 1 + int(stems_.size()); }
    // Scopes and loudness (see ana_player_scope ...).
    int scope(int source, float* out, int frames) const;
    void set_monitor(int track) { monitor_.store(track, std::memory_order_relaxed); dirty_++; }
    void reset_loudness() { resetLoudness_.store(true, std::memory_order_release); }

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
    // S8 pre-chain sources per track (stereo at rate_, the take's length); empty = the original.
    std::vector<float> sources_[ANA_MAX_TRACKS];
    // The mix crosses to the renderer through a seqlock (writer: odd while copying).
    struct MixSlot { std::atomic<uint32_t> seq{0}; MixGraph g{}; };
    MixSlot mixSlot_;
    MixGraph mix_() const;   // a stable copy (API thread, offline renders)

    // The ring of rendered blocks. Producer: the worker thread (or the callback without a device);
    // consumer: the device callback. Blocks [rd_, written_) are rendered; the consumer owns rd_.
    static constexpr uint32_t kBlock = 512, kRing = 128;
    struct Block {
        uint64_t song = 0;       // the take's frame of the block's first frame
        uint32_t len = 0, epoch = 0;
        bool end = false;        // the take ends here (no audio)
        float out[kBlock * 2]{}, tap[kBlock * 2]{};   // the master; the monitored track's VOLTA
        mix::BlockMeters m{};
        mix::Engine start;       // before this block: rendered again from here after a change
    };
    std::unique_ptr<Block[]> ring_{new Block[kRing]};
    std::atomic<uint64_t> written_{0}, rd_{0};
    uint32_t rdOff_ = 0;                          // consumer: frames of block rd_ already out
    std::atomic<uint32_t> epoch_{0}, dirty_{0}, maxPeriod_{512}, underruns_{0};
    std::atomic<uint64_t> seekTo_{0};
    // Producer state.
    mix::Engine eng_;
    MixGraph graph_{};
    uint64_t wpos_ = 0;
    uint32_t epochSeen_ = 0, dirtySeen_ = 0, mixSeen_ = 0;
    bool fresh_ = true;
    bool produce();                               // renders one block if the ring wants one
    void render_block(mix::Engine& e, uint64_t song, uint32_t len, float* out, float* tap, int tapTrack, mix::BlockMeters* m) const;
    void reset_ring();
    std::thread worker_;
    std::atomic<bool> quit_{false};
    bool threaded_ = false;

    // Meters of the block playing (the callback publishes them).
    std::atomic<float> mPeak_[ANA_MAX_TRACKS], mRms_[ANA_MAX_TRACKS], mIn_[ANA_MAX_TRACKS], mRet_[ANA_MAX_TRACKS], mUnit_[ANA_MAX_UNITS], mGr_[ANA_MAX_UNITS];
    std::atomic<float> mMaster_[4], mMasterGr_{0}, mLimGr_{0}, mLufs_[4], mTp_[2];
    void publish(const mix::BlockMeters& m);
    meter::Loudness loud_;
    std::atomic<bool> resetLoudness_{false};
    // Scope rings: the master and the monitored track's tap, stereo; ringW_ counts frames written.
    std::atomic<float> ringMaster_[ANA_SCOPE_FRAMES * 2], ringTrack_[ANA_SCOPE_FRAMES * 2];
    std::atomic<uint64_t> ringW_{0};
    std::atomic<int> monitor_{0};
    void sample(int track, uint64_t frame, float& l, float& r) const;
    void silence_meters();
    void restart_meters();
    void reopen_after(const std::function<void()>& change);
    std::atomic<uint64_t> pos_{0}, loopA_{0}, loopB_{0};
    std::atomic<bool> playing_{false};
    ma_device device_{};
    bool deviceOpen_ = false;
    std::string error_;
};

// A file through units in series, stereo, at the file's rate (ana_render_chain).
int render_chain(const char* in, const UnitParams* units, int count, const char* out);

}  // namespace dz
