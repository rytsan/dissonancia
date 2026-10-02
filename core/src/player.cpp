#include "player.hpp"

#include <algorithm>
#include <cstring>

namespace dz {

Player::~Player() { close_device(); }

int Player::load(const char* path) {
    stop();
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
    samples_.swap(all);
    channels_ = ch;
    pos_ = 0;
    loopA_ = loopB_ = 0;

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
    if (reopen && open_device(ctx) != ANA_OK) return ANA_ERR_DEVICE;
    return ANA_OK;
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
    if (!playing_.load(std::memory_order_acquire)) return;
    uint64_t pos = pos_.load(std::memory_order_acquire);
    const uint64_t total = frames(), a = loopA_.load(), b = loopB_.load();
    for (uint32_t i = 0; i < n; i++) {
        if (b > 0 && pos >= b) pos = a;
        if (pos >= total) { playing_.store(false, std::memory_order_release); break; }
        for (uint32_t c = 0; c < outChannels; c++) out[i * outChannels + c] = samples_[pos * channels_ + std::min(c, channels_ - 1)];
        pos++;
    }
    pos_.store(pos, std::memory_order_release);
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
