#include "player.hpp"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstring>

namespace dz {

Player::~Player() { close_device(); }

namespace {
std::vector<float> decode_stereo(const char* path, uint32_t rate, uint64_t frames) {
    ma_decoder dec;
    ma_decoder_config cfg = ma_decoder_config_init(ma_format_f32, 2, rate);   // resampled to the take's rate
    std::vector<float> x;
    if (ma_decoder_init_file(path, &cfg, &dec) != MA_SUCCESS) return x;
    float buf[8192];
    for (ma_uint64 got = 0; ma_decoder_read_pcm_frames(&dec, buf, 4096, &got) == MA_SUCCESS && got > 0;) x.insert(x.end(), buf, buf + got * 2);
    ma_decoder_uninit(&dec);
    x.resize(size_t(frames) * 2, 0.f);   // the take's length exactly
    return x;
}
int write_wav(const char* path, const std::vector<float>& x, uint32_t channels, uint32_t rate) {
    ma_encoder enc;
    ma_encoder_config c = ma_encoder_config_init(ma_encoding_format_wav, ma_format_f32, channels, rate);
    if (ma_encoder_init_file(path, &c, &enc) != MA_SUCCESS) return ANA_ERR_IO;
    ma_encoder_write_pcm_frames(&enc, x.data(), x.size() / channels, nullptr);
    ma_encoder_uninit(&enc);
    return ANA_OK;
}
bool valid(const MixGraph& g) {
    if (g.units < 0 || g.units > ANA_MAX_UNITS || g.cables < 0 || g.cables > ANA_MAX_CABLES) return false;
    for (int c = 0; c < g.cables; c++) {
        const Cable& x = g.cable[c];
        if (x.fromKind != JackKind::TrackOut && x.fromKind != JackKind::TrackAux && x.fromKind != JackKind::UnitOut) return false;
        if (x.toKind != JackKind::TrackReturn && x.toKind != JackKind::UnitIn && x.toKind != JackKind::Master) return false;
        if (x.fromKind == JackKind::UnitOut ? x.from >= g.units : x.from >= ANA_MAX_TRACKS) return false;
        if (x.toKind == JackKind::UnitIn ? x.to >= g.units : x.toKind == JackKind::TrackReturn && x.to >= ANA_MAX_TRACKS) return false;
        if (!std::isfinite(x.gain)) return false;
    }
    return true;
}
}  // namespace

// ---------------------------------------------------------------- the mix

int Player::set_mix(const MixGraph& g) {
    if (!valid(g)) { error_ = "invalid mix graph"; return ANA_ERR_ARG; }
    mixSlot_.seq.fetch_add(1, std::memory_order_acq_rel);   // seqlock writer (one writer: the API thread)
    std::atomic_thread_fence(std::memory_order_release);
    mixSlot_.g = g;
    std::atomic_thread_fence(std::memory_order_release);
    mixSlot_.seq.fetch_add(1, std::memory_order_acq_rel);
    dirty_++;
    return ANA_OK;
}

MixGraph Player::mix_() const {
    for (;;) {
        const uint32_t a = mixSlot_.seq.load(std::memory_order_acquire);
        if (a & 1) { std::this_thread::yield(); continue; }
        MixGraph copy = mixSlot_.g;
        std::atomic_thread_fence(std::memory_order_acquire);
        if (mixSlot_.seq.load(std::memory_order_acquire) == a) return copy;
    }
}

void Player::sample(int track, uint64_t i, float& l, float& r) const {
    if (const auto& src = sources_[track]; !src.empty()) { l = src[2 * i]; r = src[2 * i + 1]; return; }
    if (track == 0) {
        l = samples_[i * channels_];
        r = samples_[i * channels_ + (channels_ > 1 ? 1 : 0)];
    } else {
        const auto& s = stems_[size_t(track - 1)];
        l = s[2 * i]; r = s[2 * i + 1];
    }
}

// `len` frames from the take's frame `song` through the engine: the master into out (stereo), the
// VOLTA of track tapTrack into tap (if any), peaks into m (if any).
void Player::render_block(mix::Engine& e, uint64_t song, uint32_t len, float* out, float* tap, int tapTrack, mix::BlockMeters* m) const {
    const int nt = tracks();
    float src[ANA_MAX_TRACKS][2];
    for (uint32_t i = 0; i < len; i++) {
        for (int t = 0; t < nt; t++) sample(t, song + i, src[t][0], src[t][1]);
        double L, R;
        e.frame(src, L, R, m);
        out[2 * i] = float(L); out[2 * i + 1] = float(R);
        if (tap) { tap[2 * i] = float(e.ret[tapTrack][0]); tap[2 * i + 1] = float(e.ret[tapTrack][1]); }
    }
}

// ---------------------------------------------------------------- the ring

void Player::reset_ring() {   // no producer running
    written_ = 0;
    rd_ = 0;
    rdOff_ = 0;
    wpos_ = pos_.load();
    fresh_ = true;
    epochSeen_ = epoch_.load();
    dirtySeen_ = dirty_.load() - 1;   // read the mix on the first block
}

void Player::seek(uint64_t frame) {
    frame = std::min(frame, frames());
    seekTo_.store(frame, std::memory_order_release);
    pos_.store(frame, std::memory_order_release);
    epoch_.fetch_add(1, std::memory_order_acq_rel);
}

bool Player::produce() {
    const uint64_t total = frames();
    if (total == 0) return false;
    const uint32_t ep = epoch_.load(std::memory_order_acquire), dirty = dirty_.load(std::memory_order_acquire);
    if (ep != epochSeen_ || dirty != dirtySeen_) {
        // Cut a few blocks after the one playing (the device may be reading up to there) and render
        // again from the state saved there.
        const uint64_t guard = 2 + (2 * uint64_t(maxPeriod_.load(std::memory_order_relaxed)) + kBlock - 1) / kBlock;
        const uint64_t w = written_.load(std::memory_order_relaxed), cut = std::min(w, rd_.load(std::memory_order_acquire) + guard);
        if (cut < w) {
            const Block& b = ring_[cut % kRing];
            eng_ = b.start;
            wpos_ = b.song;
            written_.store(cut, std::memory_order_release);
        }
        if (ep != epochSeen_) { wpos_ = seekTo_.load(std::memory_order_acquire); epochSeen_ = ep; }
        if (dirty != dirtySeen_) {
            graph_ = mix_();
            eng_.set(graph_, tracks(), rate_, fresh_);
            fresh_ = false;
            dirtySeen_ = dirty;
        }
    }
    const uint64_t w = written_.load(std::memory_order_relaxed), rd = rd_.load(std::memory_order_acquire);
    const uint64_t lead = std::min<uint64_t>(kRing - 8, (uint64_t(rate_) + kBlock - 1) / kBlock);   // ~1 s ahead
    if (w >= rd + lead) return false;
    if (w > 0 && ring_[(w - 1) % kRing].end && ring_[(w - 1) % kRing].epoch == ep) return false;   // the take is done
    Block& b = ring_[w % kRing];
    b.start = eng_;
    b.epoch = ep;
    b.song = wpos_;
    const uint64_t a = loopA_.load(), lb = loopB_.load(), stop = lb > 0 && wpos_ < lb ? lb : total;
    b.end = wpos_ >= stop;
    b.len = b.end ? 0 : uint32_t(std::min<uint64_t>(kBlock, stop - wpos_));
    b.m = {};
    const int nt = tracks(), mon = std::clamp(monitor_.load(std::memory_order_relaxed), 0, std::max(0, nt - 1));
    render_block(eng_, wpos_, b.len, b.out, b.tap, mon, &b.m);
    wpos_ += b.len;
    if (lb > 0 && wpos_ >= lb && a < lb) wpos_ = a;   // the loop: on from its start
    written_.store(w + 1, std::memory_order_release);
    return true;
}

// ---------------------------------------------------------------- tracks and sources

void Player::reopen_after(const std::function<void()>& change) {
    const bool playing = playing_.load();
    stop();
    const bool reopen = deviceOpen_;
    ma_context* ctx = reopen ? device_.pContext : nullptr;
    close_device();
    change();
    reset_ring();
    if (reopen) open_device(ctx);
    if (playing) play();
}

int Player::add_track(const char* path) {
    if (channels_ == 0) { error_ = "no file loaded"; return ANA_ERR_STATE; }
    if (tracks() >= ANA_MAX_TRACKS) { error_ = "too many tracks"; return ANA_ERR_ARG; }
    auto x = decode_stereo(path, rate_, frames());
    if (x.empty()) { error_ = std::string("cannot decode ") + path; return ANA_ERR_IO; }
    reopen_after([&] { stems_.push_back(std::move(x)); });
    return tracks() - 1;
}

void Player::clear_tracks() {
    reopen_after([&] {
        stems_.clear();
        for (int t = 1; t < ANA_MAX_TRACKS; t++) sources_[t].clear();
    });
}

int Player::set_source(int track, const char* path) {
    if (track < 0 || track >= tracks()) { error_ = "no such track"; return ANA_ERR_ARG; }
    std::vector<float> x;
    if (path && *path) {
        x = decode_stereo(path, rate_, frames());
        if (x.empty()) { error_ = std::string("cannot decode ") + path; return ANA_ERR_IO; }
    }
    reopen_after([&] { sources_[track].swap(x); });
    return ANA_OK;
}

int Player::load(const char* path) {
    stop();
    silence_meters();
    { MixGraph g; mix::graph_defaults(g); set_mix(g); }   // a plain file plays untouched
    ma_decoder dec;
    ma_decoder_config cfg = ma_decoder_config_init(ma_format_f32, 0, 0);   // native channels and rate
    if (ma_decoder_init_file(path, &cfg, &dec) != MA_SUCCESS) { error_ = std::string("cannot decode ") + path; return ANA_ERR_IO; }
    const uint32_t ch = std::min<uint32_t>(dec.outputChannels, 2), srcCh = dec.outputChannels;
    std::vector<float> all, buf(4096 * srcCh);
    for (;;) {
        ma_uint64 got = 0;
        ma_decoder_read_pcm_frames(&dec, buf.data(), 4096, &got);
        if (got == 0) break;
        for (ma_uint64 i = 0; i < got; i++)
            for (uint32_t c = 0; c < ch; c++) all.push_back(buf[i * srcCh + c]);   // channels beyond 2 dropped
    }
    rate_ = dec.outputSampleRate;
    ma_decoder_uninit(&dec);
    // The renderer reads samples_: swap only while it is stopped.
    const bool reopen = deviceOpen_;
    ma_context* ctx = reopen ? device_.pContext : nullptr;
    close_device();
    original_ = all;
    stems_.clear();
    samples_.swap(all);
    channels_ = ch;
    pos_ = 0;
    loopA_ = loopB_ = 0;
    restart_meters();
    for (auto& src : sources_) src.clear();   // rendered from the previous audio
    build_mipmap();
    reset_ring();
    if (reopen && open_device(ctx) != ANA_OK) return ANA_ERR_DEVICE;
    return ANA_OK;
}

int Player::apply_edits(const EditSegment* segs, int count, uint64_t fadeIn, uint64_t fadeOut, float normDb) {
    if (channels_ == 0) { error_ = "no file loaded"; return ANA_ERR_STATE; }
    const uint64_t srcFrames = original_.size() / channels_;
    std::vector<float> out;
    if (count <= 0) out = original_;
    const uint64_t xf = std::max<uint64_t>(1, rate_ / 500);   // 2 ms crossfade at every join
    for (int k = 0; k < count; k++) {
        const uint64_t a = std::min(segs[k].sourceStart, srcFrames), b = std::min(segs[k].sourceEnd, srcFrames);
        if (b <= a) continue;
        const float g = std::pow(10.f, segs[k].gainDb / 20.f);
        for (uint64_t i = a; i < b; i++) {
            float ramp = 1.f;   // fade both edges of an inner join so a cut never clicks
            if (k > 0 && i - a < xf) ramp = float(i - a) / float(xf);
            if (k + 1 < count && b - i <= xf) ramp = std::min(ramp, float(b - i) / float(xf));
            for (uint32_t c = 0; c < channels_; c++) out.push_back(original_[i * channels_ + c] * g * ramp);
        }
    }
    const uint64_t n = out.size() / channels_;
    for (uint64_t i = 0; i < std::min(fadeIn, n); i++)
        for (uint32_t c = 0; c < channels_; c++) out[i * channels_ + c] *= float(i) / float(fadeIn);
    for (uint64_t i = 0; i < std::min(fadeOut, n); i++)
        for (uint32_t c = 0; c < channels_; c++) out[(n - 1 - i) * channels_ + c] *= float(i) / float(fadeOut);
    if (normDb <= 0) {
        float peak = 0;
        for (float v : out) peak = std::max(peak, std::fabs(v));
        if (peak > 0) {
            const float g = std::pow(10.f, normDb / 20.f) / peak;
            for (float& v : out) v *= g;
        }
    }
    const bool playing = playing_.load();
    stop();
    const bool reopen = deviceOpen_;
    ma_context* ctx = reopen ? device_.pContext : nullptr;
    close_device();   // the renderer reads samples_
    samples_.swap(out);
    stems_.clear();   // stems were separated from the previous edit: other length, no longer aligned
    pos_ = std::min<uint64_t>(pos_.load(), frames());
    loopA_ = loopB_ = 0;
    restart_meters();
    for (auto& src : sources_) src.clear();   // rendered from the previous audio
    build_mipmap();
    reset_ring();
    if (reopen && open_device(ctx) != ANA_OK) return ANA_ERR_DEVICE;
    if (playing) play();
    return ANA_OK;
}

int Player::save_wav(const char* path) const {
    if (channels_ == 0) return ANA_ERR_STATE;
    ma_encoder enc;
    ma_encoder_config c = ma_encoder_config_init(ma_encoding_format_wav, ma_format_f32, channels_, rate_);
    if (ma_encoder_init_file(path, &c, &enc) != MA_SUCCESS) return ANA_ERR_IO;
    ma_encoder_write_pcm_frames(&enc, samples_.data(), frames(), nullptr);
    ma_encoder_uninit(&enc);
    return ANA_OK;
}

void Player::build_mipmap() {
    const uint32_t ch = channels_;
    // Mipmap: level 0 = 256-frame blocks of the mono mix, each next level halves.
    mipMin_.clear();
    mipMax_.clear();
    const uint64_t n = frames(), block = 1u << kBase;
    std::vector<float> mn((n + block - 1) / block, 0.f), mx(mn.size(), 0.f);
    for (uint64_t b = 0; b < mn.size(); b++) {
        float lo = 1e9f, hi = -1e9f;
        for (uint64_t i = b * block; i < std::min(n, (b + 1) * block); i++) {
            float v = 0;
            for (uint32_t c = 0; c < ch; c++) v += samples_[i * ch + c];
            v /= float(ch);
            lo = std::min(lo, v);
            hi = std::max(hi, v);
        }
        mn[b] = lo;
        mx[b] = hi;
    }
    while (!mn.empty()) {
        mipMin_.push_back(mn);
        mipMax_.push_back(mx);
        if (mn.size() == 1) break;
        std::vector<float> a((mn.size() + 1) / 2), z(a.size());
        for (size_t i = 0; i < a.size(); i++) {
            a[i] = std::min(mn[2 * i], 2 * i + 1 < mn.size() ? mn[2 * i + 1] : mn[2 * i]);
            z[i] = std::max(mx[2 * i], 2 * i + 1 < mx.size() ? mx[2 * i + 1] : mx[2 * i]);
        }
        mn.swap(a);
        mx.swap(z);
    }
}

int Player::open_device(ma_context* ctx) {
    close_device();
    if (!ctx || rate_ == 0) return ANA_OK;
    ma_device_config c = ma_device_config_init(ma_device_type_playback);
    c.playback.format = ma_format_f32;
    c.playback.channels = 2;
    c.sampleRate = rate_;   // miniaudio resamples to the device when they differ
    c.dataCallback = [](ma_device* d, void* out, const void*, ma_uint32 n) {
        static_cast<Player*>(d->pUserData)->render(static_cast<float*>(out), n, d->playback.channels);
    };
    c.pUserData = this;
    if (ma_device_init(ctx, &c, &device_) != MA_SUCCESS) { error_ = "cannot open the playback device"; return ANA_ERR_DEVICE; }
    deviceOpen_ = true;
    quit_ = false;
    threaded_ = true;
    worker_ = std::thread([this] {
        while (!quit_.load(std::memory_order_acquire))
            if (!produce()) std::this_thread::sleep_for(std::chrono::milliseconds(2));
    });
    if (ma_device_start(&device_) != MA_SUCCESS) { close_device(); error_ = "cannot start the playback device"; return ANA_ERR_DEVICE; }
    return ANA_OK;
}

void Player::close_device() {
    if (deviceOpen_) ma_device_uninit(&device_);
    deviceOpen_ = false;
    quit_ = true;
    if (worker_.joinable()) worker_.join();
    threaded_ = false;
}

void Player::render(float* out, uint32_t n, uint32_t outChannels) {
    std::memset(out, 0, size_t(n) * outChannels * sizeof(float));
    if (n > maxPeriod_.load(std::memory_order_relaxed)) maxPeriod_.store(n, std::memory_order_relaxed);
    if (!playing_.load(std::memory_order_acquire)) { silence_meters(); return; }
    if (resetLoudness_.exchange(false, std::memory_order_acq_rel)) loud_.reset();
    if (!threaded_) while (produce()) {}   // no device: render ahead here, as the worker would
    const uint32_t ep = epoch_.load(std::memory_order_acquire);
    uint64_t w = ringW_.load(std::memory_order_relaxed);
    uint32_t i = 0;
    while (i < n) {
        const uint64_t r = rd_.load(std::memory_order_relaxed);
        if (r >= written_.load(std::memory_order_acquire)) {
            if (!threaded_ && produce()) continue;   // no device: render what is needed now
            underruns_.fetch_add(1, std::memory_order_relaxed);
            break;
        }
        const Block& b = ring_[r % kRing];
        if (b.epoch != ep) { rdOff_ = 0; rd_.store(r + 1, std::memory_order_release); continue; }   // before a seek: dropped
        if (b.end) { playing_.store(false, std::memory_order_release); pos_.store(b.song, std::memory_order_release); break; }
        if (rdOff_ == 0) publish(b.m);
        const uint32_t k = std::min(b.len - rdOff_, n - i);
        for (uint32_t j = 0; j < k; j++) {
            const float L = b.out[2 * (rdOff_ + j)], R = b.out[2 * (rdOff_ + j) + 1];
            loud_.run(L, R);
            const size_t q = size_t(w % ANA_SCOPE_FRAMES) * 2;
            ringMaster_[q].store(L, std::memory_order_relaxed);
            ringMaster_[q + 1].store(R, std::memory_order_relaxed);
            ringTrack_[q].store(b.tap[2 * (rdOff_ + j)], std::memory_order_relaxed);
            ringTrack_[q + 1].store(b.tap[2 * (rdOff_ + j) + 1], std::memory_order_relaxed);
            w++;
            if (outChannels == 1) out[i + j] = 0.5f * (L + R);
            else { out[(i + j) * outChannels] = L; out[(i + j) * outChannels + 1] = R; }
        }
        rdOff_ += k;
        i += k;
        pos_.store(b.song + rdOff_, std::memory_order_release);
        if (rdOff_ == b.len) { rdOff_ = 0; rd_.store(r + 1, std::memory_order_release); }
    }
    ringW_.store(w, std::memory_order_release);
    mLufs_[0].store(loud_.momentary, std::memory_order_relaxed);
    mLufs_[1].store(loud_.shortTerm, std::memory_order_relaxed);
    mLufs_[2].store(loud_.integrated(), std::memory_order_relaxed);
    mLufs_[3].store(loud_.range(), std::memory_order_relaxed);
    for (int c = 0; c < 2; c++) mTp_[c].store(loud_.true_peak_db(c), std::memory_order_relaxed);
}

// ---------------------------------------------------------------- meters

void Player::publish(const mix::BlockMeters& m) {
    if (m.frames == 0) return;
    const double f = m.frames;
    for (int t = 0; t < ANA_MAX_TRACKS; t++) {
        mPeak_[t].store(mix::lin_to_db(m.peak[t]), std::memory_order_relaxed);
        mRms_[t].store(mix::lin_to_db(std::sqrt(m.sq[t] / f)), std::memory_order_relaxed);
        mIn_[t].store(mix::lin_to_db(m.in[t]), std::memory_order_relaxed);
        mRet_[t].store(mix::lin_to_db(m.ret[t]), std::memory_order_relaxed);
    }
    for (int u = 0; u < ANA_MAX_UNITS; u++) {
        mUnit_[u].store(mix::lin_to_db(m.unit[u]), std::memory_order_relaxed);
        mGr_[u].store(float(m.gr[u]), std::memory_order_relaxed);
    }
    for (int c = 0; c < 2; c++) {
        mMaster_[c].store(mix::lin_to_db(m.mpk[c]), std::memory_order_relaxed);
        mMaster_[2 + c].store(mix::lin_to_db(std::sqrt(m.msq[c] / f)), std::memory_order_relaxed);
    }
    mMasterGr_.store(float(m.mgr), std::memory_order_relaxed);
    mLimGr_.store(float(m.lgr), std::memory_order_relaxed);
}

// New audio (load, edits), renderer stopped: loudness from zero, scopes empty.
void Player::restart_meters() {
    loud_.init(rate_);
    resetLoudness_ = false;
    for (int k = 0; k < 4; k++) mLufs_[k].store(k == 3 ? 0.f : meter::kSilent);
    mTp_[0] = mTp_[1] = meter::kSilent;
    ringW_ = 0;
}

void Player::silence_meters() {
    for (int t = 0; t < ANA_MAX_TRACKS; t++)
        for (auto* a : {&mPeak_[t], &mRms_[t], &mIn_[t], &mRet_[t]}) a->store(-120.f, std::memory_order_relaxed);
    for (int u = 0; u < ANA_MAX_UNITS; u++) { mUnit_[u].store(-120.f, std::memory_order_relaxed); mGr_[u].store(0, std::memory_order_relaxed); }
    mLufs_[0].store(meter::kSilent, std::memory_order_relaxed);   // integrated and range hold
    mLufs_[1].store(meter::kSilent, std::memory_order_relaxed);
    for (auto& m : mMaster_) m.store(-120.f, std::memory_order_relaxed);
    mMasterGr_.store(0, std::memory_order_relaxed);
    mLimGr_.store(0, std::memory_order_relaxed);
}

void Player::meters(MixMeters& out) const {
    out = {};
    out.tracks = tracks();
    for (int t = 0; t < ANA_MAX_TRACKS; t++) {
        const bool on = t < out.tracks;
        out.peakDb[t] = on ? mPeak_[t].load(std::memory_order_relaxed) : -120.f;
        out.rmsDb[t] = on ? mRms_[t].load(std::memory_order_relaxed) : -120.f;
        out.inDb[t] = on ? mIn_[t].load(std::memory_order_relaxed) : -120.f;
        out.returnDb[t] = on ? mRet_[t].load(std::memory_order_relaxed) : -120.f;
    }
    for (int u = 0; u < ANA_MAX_UNITS; u++) {
        out.unitDb[u] = mUnit_[u].load(std::memory_order_relaxed);
        out.unitGrDb[u] = mGr_[u].load(std::memory_order_relaxed);
    }
    for (int c = 0; c < 2; c++) { out.masterPeakDb[c] = mMaster_[c].load(std::memory_order_relaxed); out.masterRmsDb[c] = mMaster_[2 + c].load(std::memory_order_relaxed); }
    out.underruns = underruns_.load(std::memory_order_relaxed);
    out.lufsMomentary = mLufs_[0].load(std::memory_order_relaxed);
    out.lufsShortTerm = mLufs_[1].load(std::memory_order_relaxed);
    out.lufsIntegrated = mLufs_[2].load(std::memory_order_relaxed);
    out.loudnessRange = mLufs_[3].load(std::memory_order_relaxed);
    out.truePeakDb[0] = mTp_[0].load(std::memory_order_relaxed);
    out.truePeakDb[1] = mTp_[1].load(std::memory_order_relaxed);
    out.masterCompGrDb = mMasterGr_.load(std::memory_order_relaxed);
    out.limiterGrDb = mLimGr_.load(std::memory_order_relaxed);
}

// ---------------------------------------------------------------- offline

int Player::render_tap(uint32_t mask, const char* path) const {
    // A fresh engine with the current mix: the same DSP as playback, read at the VOLTAs.
    const uint64_t n = frames();
    auto e = std::make_unique<mix::Engine>();
    e->set(mix_(), tracks(), rate_, true);
    std::vector<float> y(size_t(n), 0.f);
    const int nt = tracks();
    float src[ANA_MAX_TRACKS][2];
    for (uint64_t i = 0; i < n; i++) {
        for (int t = 0; t < nt; t++) sample(t, i, src[t][0], src[t][1]);
        double L, R;
        e->frame(src, L, R, nullptr);
        double v = 0;
        for (int t = 0; t < nt; t++) if (mask >> t & 1) v += 0.5 * (e->ret[t][0] + e->ret[t][1]);
        y[i] = float(v);
    }
    return write_wav(path, y, 1, rate_);
}

int render_chain(const char* in, const UnitParams* units, int count, const char* out) {
    ma_decoder dec;
    ma_decoder_config cfg = ma_decoder_config_init(ma_format_f32, 2, 0);   // stereo, the file's own rate
    if (ma_decoder_init_file(in, &cfg, &dec) != MA_SUCCESS) return ANA_ERR_IO;
    const uint32_t rate = dec.outputSampleRate;
    std::vector<float> x;
    float buf[8192];
    for (ma_uint64 got = 0; ma_decoder_read_pcm_frames(&dec, buf, 4096, &got) == MA_SUCCESS && got > 0;) x.insert(x.end(), buf, buf + got * 2);
    ma_decoder_uninit(&dec);
    std::vector<mix::Unit> u(size_t(std::clamp(count, 0, ANA_MAX_UNITS)));
    for (size_t k = 0; k < u.size(); k++) u[k].set(units[k], rate, true);
    const double k = mix::coef(10, rate);
    for (size_t i = 0; i + 1 < x.size(); i += 2) {
        double l = x[i], r = x[i + 1];
        for (auto& v : u) v.run(l, r, k);
        x[i] = float(l); x[i + 1] = float(r);
    }
    return write_wav(out, x, 2, rate);
}

int Player::bounce(const char* path) const {
    const uint64_t n = frames();
    auto e = std::make_unique<mix::Engine>();
    e->set(mix_(), tracks(), rate_, true);
    std::vector<float> y(size_t(n) * 2, 0.f);
    for (uint64_t i = 0; i < n; i += kBlock) render_block(*e, i, uint32_t(std::min<uint64_t>(kBlock, n - i)), y.data() + i * 2, nullptr, 0, nullptr);
    return write_wav(path, y, 2, rate_);
}

int Player::scope(int source, float* out, int frames) const {
    const uint64_t w = ringW_.load(std::memory_order_acquire);
    const auto& ring = source == 0 ? ringMaster_ : ringTrack_;
    const int n = int(std::min<uint64_t>({uint64_t(std::max(frames, 0)), w, uint64_t(ANA_SCOPE_FRAMES)}));
    for (int i = 0; i < n; i++) {
        const size_t k = size_t((w - uint64_t(n) + uint64_t(i)) % ANA_SCOPE_FRAMES) * 2;
        out[2 * i] = ring[k].load(std::memory_order_relaxed);
        out[2 * i + 1] = ring[k + 1].load(std::memory_order_relaxed);
    }
    return n;
}

void Player::info(PlayerInfo& out) const {
    out = {};
    out.frames = frames();
    out.positionFrame = pos_.load(std::memory_order_acquire);
    out.sampleRate = rate_;
    out.channels = uint16_t(channels_);
    out.playing = playing_.load(std::memory_order_acquire);
    out.looping = loopB_.load() > 0;
}

void Player::peaks(uint64_t start, uint64_t end, uint32_t columns, float* mn, float* mx) const {
    const uint64_t n = frames();
    end = std::min(end, n);
    for (uint32_t col = 0; col < columns; col++) { mn[col] = 0; mx[col] = 0; }
    if (columns == 0 || end <= start || mipMin_.empty()) return;
    const double per = double(end - start) / columns;   // frames per column
    for (uint32_t col = 0; col < columns; col++) {
        uint64_t a = start + uint64_t(col * per), b = std::max(a + 1, start + uint64_t((col + 1) * per));
        float lo = 1e9f, hi = -1e9f;
        if (per < double(1u << kBase)) {   // closer than the first level: the samples themselves
            for (uint64_t i = a; i < std::min(b, n); i++) {
                float v = 0;
                for (uint32_t c = 0; c < channels_; c++) v += samples_[i * channels_ + c];
                v /= float(channels_);
                lo = std::min(lo, v);
                hi = std::max(hi, v);
            }
        } else {
            int level = 0;   // coarsest level whose blocks still fit in a column
            while (level + 1 < int(mipMin_.size()) && double(1ull << (level + 1 + kBase)) <= per) level++;
            const uint64_t block = 1ull << (level + kBase);
            for (uint64_t k = a / block; k <= (b - 1) / block && k < mipMin_[size_t(level)].size(); k++) {
                lo = std::min(lo, mipMin_[size_t(level)][k]);
                hi = std::max(hi, mipMax_[size_t(level)][k]);
            }
        }
        if (lo <= hi) { mn[col] = lo; mx[col] = hi; }
    }
}

}  // namespace dz
