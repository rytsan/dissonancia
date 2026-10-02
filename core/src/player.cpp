#include "player.hpp"

#include <algorithm>
#include <cmath>
#include <cstring>

namespace dz {

Player::~Player() { close_device(); }

namespace {
template <class Slot, class P>
void write_slot(Slot& s, const P& p) {   // seqlock writer (one writer: the API thread)
    s.seq.fetch_add(1, std::memory_order_acq_rel);
    std::atomic_thread_fence(std::memory_order_release);
    s.p = p;
    std::atomic_thread_fence(std::memory_order_release);
    s.seq.fetch_add(1, std::memory_order_acq_rel);
}
template <class Slot, class P>
bool read_slot(const Slot& s, uint32_t& seen, P& out) {   // reader: a stable, changed copy or nothing
    const uint32_t a = s.seq.load(std::memory_order_acquire);
    if (a == seen || (a & 1)) return false;
    P copy = s.p;
    std::atomic_thread_fence(std::memory_order_acquire);
    if (s.seq.load(std::memory_order_acquire) != a) return false;
    out = copy;
    seen = a;
    return true;
}
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
}  // namespace

void Player::reopen_after(const std::function<void()>& change) {
    const bool playing = playing_.load();
    stop();
    const bool reopen = deviceOpen_;
    ma_context* ctx = reopen ? device_.pContext : nullptr;
    close_device();
    change();
    primed_ = false;
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

void Player::clear_tracks() { reopen_after([&] { stems_.clear(); }); }

int Player::set_channel(int track, const ChannelParams& p) {
    if (track < 0 || track >= ANA_MAX_TRACKS) return ANA_ERR_ARG;
    write_slot(slots_[track], p);
    return ANA_OK;
}

void Player::set_master(const MasterParams& p) { write_slot(masterSlot_, p); }

void Player::sample(int track, uint64_t i, float& l, float& r) const {
    if (track == 0) {
        l = samples_[i * channels_];
        r = samples_[i * channels_ + (channels_ > 1 ? 1 : 0)];
    } else {
        const auto& s = stems_[size_t(track - 1)];
        l = s[2 * i]; r = s[2 * i + 1];
    }
}

void Player::sync_params(double fs) {
    if (!primed_) {   // first block after (re)opening: every strip from its slot, fresh filter state
        for (int t = 0; t < ANA_MAX_TRACKS; t++) { strips_[t] = {}; seen_[t] = ~0u; }
        bus_ = {};
        masterSeen_ = ~0u;
        primed_ = true;
    }
    for (int t = 0; t < tracks(); t++) {
        ChannelParams cp;
        if (read_slot(slots_[t], seen_[t], cp)) strips_[t].set(cp, fs);
    }
    MasterParams mp;
    if (read_slot(masterSlot_, masterSeen_, mp)) bus_.set(mp, fs);
}

int Player::load(const char* path) {
    stop();
    silence_meters();
    for (auto& s : slots_) { ChannelParams d; mix::channel_defaults(d); write_slot(s, d); }
    { MasterParams m; mix::master_defaults(m); m.limiterOn = 0; write_slot(masterSlot_, m); }   // a plain file plays untouched
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
    // The device callback reads samples_: swap only while no device is open.
    const bool reopen = deviceOpen_;
    ma_context* ctx = reopen ? device_.pContext : nullptr;
    close_device();
    original_ = all;
    stems_.clear();
    primed_ = false;
    samples_.swap(all);
    channels_ = ch;
    pos_ = 0;
    loopA_ = loopB_ = 0;
    build_mipmap();
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
    close_device();   // the callback reads samples_
    samples_.swap(out);
    stems_.clear();   // stems were separated from the previous edit: other length, no longer aligned
    primed_ = false;
    pos_ = std::min<uint64_t>(pos_.load(), frames());
    loopA_ = loopB_ = 0;
    build_mipmap();
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
    if (ma_device_start(&device_) != MA_SUCCESS) { close_device(); error_ = "cannot start the playback device"; return ANA_ERR_DEVICE; }
    return ANA_OK;
}

void Player::close_device() {
    if (deviceOpen_) ma_device_uninit(&device_);
    deviceOpen_ = false;
}

void Player::render(float* out, uint32_t n, uint32_t outChannels) {
    std::memset(out, 0, size_t(n) * outChannels * sizeof(float));
    sync_params(rate_);
    if (!playing_.load(std::memory_order_acquire)) { silence_meters(); return; }
    uint64_t pos = pos_.load(std::memory_order_acquire);
    const uint64_t total = frames(), a = loopA_.load(), b = loopB_.load();
    const int nt = tracks();
    bool anySolo = false;
    for (int t = 0; t < nt; t++) anySolo |= strips_[t].p.solo != 0;
    float pk[ANA_MAX_TRACKS]{}, sq[ANA_MAX_TRACKS]{}, mpk[2]{}, msq[2]{};
    uint32_t done = 0;
    for (uint32_t i = 0; i < n; i++) {
        if (b > 0 && pos >= b) pos = a;
        if (pos >= total) { playing_.store(false, std::memory_order_release); break; }
        float L = 0, R = 0;
        for (int t = 0; t < nt; t++) {
            float l, r;
            sample(t, pos, l, r);
            mix::Strip& s = strips_[t];
            s.tap(l, r);
            s.out(l, r);
            if (s.p.mute || (anySolo && !s.p.solo)) l = r = 0;
            const float m = std::max(std::fabs(l), std::fabs(r));
            pk[t] = std::max(pk[t], m);
            sq[t] += 0.5f * (l * l + r * r);
            L += l; R += r;
        }
        bus_.run(L, R);
        mpk[0] = std::max(mpk[0], std::fabs(L)); mpk[1] = std::max(mpk[1], std::fabs(R));
        msq[0] += L * L; msq[1] += R * R;
        if (outChannels == 1) out[i] = 0.5f * (L + R);
        else { out[i * outChannels] = L; out[i * outChannels + 1] = R; }
        pos++;
        done++;
    }
    pos_.store(pos, std::memory_order_release);
    if (done == 0) return;
    for (int t = 0; t < nt; t++) {
        mPeak_[t].store(mix::lin_to_db(pk[t]), std::memory_order_relaxed);
        mRms_[t].store(mix::lin_to_db(std::sqrt(sq[t] / float(done))), std::memory_order_relaxed);
        mGr_[t].store(strips_[t].comp.gr, std::memory_order_relaxed);
        mGate_[t].store(strips_[t].gate.open, std::memory_order_relaxed);
    }
    for (int c = 0; c < 2; c++) {
        mMaster_[c].store(mix::lin_to_db(mpk[c]), std::memory_order_relaxed);
        mMaster_[2 + c].store(mix::lin_to_db(std::sqrt(msq[c] / float(done))), std::memory_order_relaxed);
    }
    mMasterGr_.store(bus_.comp.gr, std::memory_order_relaxed);
    mLimGr_.store(bus_.lim.gr_db(), std::memory_order_relaxed);
}

void Player::silence_meters() {
    for (int t = 0; t < ANA_MAX_TRACKS; t++) { mPeak_[t].store(-120.f, std::memory_order_relaxed); mRms_[t].store(-120.f, std::memory_order_relaxed); mGr_[t].store(0, std::memory_order_relaxed); }
    for (auto& m : mMaster_) m.store(-120.f, std::memory_order_relaxed);
    mMasterGr_.store(0, std::memory_order_relaxed);
    mLimGr_.store(0, std::memory_order_relaxed);
}

void Player::meters(MixMeters& out) const {
    out = {};
    out.tracks = tracks();
    for (int t = 0; t < ANA_MAX_TRACKS; t++) {
        out.peakDb[t] = t < out.tracks ? mPeak_[t].load(std::memory_order_relaxed) : -120.f;
        out.rmsDb[t] = t < out.tracks ? mRms_[t].load(std::memory_order_relaxed) : -120.f;
        out.compGrDb[t] = mGr_[t].load(std::memory_order_relaxed);
        out.gateOpen[t] = mGate_[t].load(std::memory_order_relaxed);
    }
    for (int c = 0; c < 2; c++) { out.masterPeakDb[c] = mMaster_[c].load(std::memory_order_relaxed); out.masterRmsDb[c] = mMaster_[2 + c].load(std::memory_order_relaxed); }
    out.masterCompGrDb = mMasterGr_.load(std::memory_order_relaxed);
    out.limiterGrDb = mLimGr_.load(std::memory_order_relaxed);
}

namespace {
int write_wav(const char* path, const std::vector<float>& x, uint32_t channels, uint32_t rate) {
    ma_encoder enc;
    ma_encoder_config c = ma_encoder_config_init(ma_encoding_format_wav, ma_format_f32, channels, rate);
    if (ma_encoder_init_file(path, &c, &enc) != MA_SUCCESS) return ANA_ERR_IO;
    ma_encoder_write_pcm_frames(&enc, x.data(), x.size() / channels, nullptr);
    ma_encoder_uninit(&enc);
    return ANA_OK;
}
}  // namespace

int Player::render_tap(uint32_t mask, const char* path) const {
    // Fresh strips with the latest parameters: offline, the same DSP as playback up to the tap.
    const uint64_t n = frames();
    std::vector<float> y(size_t(n), 0.f);
    for (int t = 0; t < tracks(); t++) {
        if (!(mask >> t & 1)) continue;
        mix::Strip s;
        uint32_t seen = ~0u;
        ChannelParams cp;
        read_slot(slots_[t], seen, cp);
        s.set(cp, rate_);
        for (uint64_t i = 0; i < n; i++) {
            float l, r;
            sample(t, i, l, r);
            s.tap(l, r);
            y[i] += 0.5f * (l + r);
        }
    }
    return write_wav(path, y, 1, rate_);
}

int Player::bounce(const char* path) const {
    const uint64_t n = frames();
    const int nt = tracks();
    std::vector<mix::Strip> s(static_cast<size_t>(nt));
    bool anySolo = false;
    for (int t = 0; t < nt; t++) {
        uint32_t seen = ~0u;
        ChannelParams cp;
        read_slot(slots_[t], seen, cp);
        s[size_t(t)].set(cp, rate_);
        anySolo |= cp.solo != 0;
    }
    mix::Bus bus;
    uint32_t seen = ~0u;
    MasterParams mp;
    read_slot(masterSlot_, seen, mp);
    bus.set(mp, rate_);
    std::vector<float> y(size_t(n) * 2, 0.f);
    for (uint64_t i = 0; i < n; i++) {
        float L = 0, R = 0;
        for (int t = 0; t < nt; t++) {
            float l, r;
            sample(t, i, l, r);
            s[size_t(t)].tap(l, r);
            s[size_t(t)].out(l, r);
            if (s[size_t(t)].p.mute || (anySolo && !s[size_t(t)].p.solo)) continue;
            L += l; R += r;
        }
        bus.run(L, R);
        y[2 * i] = L; y[2 * i + 1] = R;
    }
    return write_wav(path, y, 2, rate_);
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
