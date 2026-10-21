#include "playground.hpp"

#include <algorithm>
#include <cmath>
#include <cstring>

#include "miniaudio.h"

namespace dz {

namespace {
float db(float d) { return std::pow(10.f, d / 20.f); }
}  // namespace

Playground::Playground() = default;

Playground::~Playground() {
    for (auto& s : slot_) delete s.exchange(nullptr);
    for (auto& r : retired_) delete r.first;
}

void Playground::start(uint32_t rate) {
    std::lock_guard lock(loadMutex_);
    if (rate != rate_)   // decoded at another rate: empty (the app loads them again)
        for (int k = 0; k <= kSampler; k++) { delete slot_[k].exchange(nullptr); seconds_[k] = 0; }
    for (auto& r : retired_) delete r.first;
    retired_.clear();
    rate_ = rate;
    for (auto& v : voice_) v = {};
    cfgSeen_ = ~0u;
    stepOrigin_ = -1;
    step_ = -1;
    midiCount_ = 0;
    running_ = true;
}

void Playground::stop() { running_ = false; }

int Playground::load(int slot, const char* path, uint32_t rate, std::string& error) {
    if (slot < 0 || slot > kSampler) { error = "no such pad"; return ANA_ERR_ARG; }
    std::unique_ptr<Sample> s;
    if (path && *path) {
        ma_decoder dec;
        ma_decoder_config cfg = ma_decoder_config_init(ma_format_f32, 2, rate);
        if (ma_decoder_init_file(path, &cfg, &dec) != MA_SUCCESS) { error = std::string("cannot decode ") + path; return ANA_ERR_IO; }
        s = std::make_unique<Sample>();
        float buf[8192];
        for (ma_uint64 got = 0; ma_decoder_read_pcm_frames(&dec, buf, 4096, &got) == MA_SUCCESS && got > 0;) s->lr.insert(s->lr.end(), buf, buf + got * 2);
        ma_decoder_uninit(&dec);
        const size_t cap = size_t(rate) * 60 * 2;   // a minute at most: a pad, not a song
        if (s->lr.size() > cap) s->lr.resize(cap);
        s->frames = s->lr.size() / 2;
        if (s->frames == 0) { error = std::string("empty: ") + path; return ANA_ERR_IO; }
    }
    std::lock_guard lock(loadMutex_);
    seconds_[slot] = s ? float(double(s->frames) / rate) : 0.f;
    Sample* old = slot_[slot].exchange(s.release(), std::memory_order_acq_rel);
    const uint64_t now = callbacks_.load(std::memory_order_acquire);
    if (old) retired_.push_back({old, now});
    // A callback holds a sample for at most the callback it reads it in, and the next one drops the
    // voices on it: two callbacks later it is free (at once while no callback runs).
    std::erase_if(retired_, [&](auto& r) {
        if (running_ && callbacks_.load(std::memory_order_acquire) < r.second + 2) return false;
        delete r.first;
        return true;
    });
    return ANA_OK;
}

void Playground::set(const PlaygroundConfig& c) {
    cfg_.seq.fetch_add(1, std::memory_order_acq_rel);
    std::atomic_thread_fence(std::memory_order_release);
    cfg_.c = c;
    std::atomic_thread_fence(std::memory_order_release);
    cfg_.seq.fetch_add(1, std::memory_order_acq_rel);
}

void Playground::status(PlaygroundStatus& out) const {
    out = {};
    for (int k = 0; k <= kSampler; k++) { out.hits[k] = hits_[k].load(std::memory_order_relaxed); out.seconds[k] = seconds_[k]; }
    const float pk = const_cast<std::atomic<float>&>(peak_).exchange(0, std::memory_order_relaxed);
    out.peakDb = pk > 0 ? 20 * std::log10(pk) : -120.f;
    out.step = step_.load(std::memory_order_relaxed);
    out.voices = voices_.load(std::memory_order_relaxed);
    out.midiMessages = midiCount_.load(std::memory_order_relaxed);
}

void Playground::push(uint8_t s, uint8_t d1, uint8_t d2) { fromApi_.try_push(uint32_t(s) | uint32_t(d1) << 8 | uint32_t(d2) << 16); }

void Playground::midi_from_device(uint8_t s, uint8_t d1, uint8_t d2) {
    const uint32_t m = uint32_t(s) | uint32_t(d1) << 8 | uint32_t(d2) << 16;
    fromDevice_.try_push(m);
    raw_.try_push(m);
    midiCount_.fetch_add(1, std::memory_order_relaxed);
}

void Playground::trigger(int slot, int note, float vel) {
    const Sample* s = slot_[slot].load(std::memory_order_acquire);
    if (!s) return;
    Voice* v = nullptr;
    for (auto& x : voice_) if (!x.s) { v = &x; break; }
    if (!v) {   // all busy: the one nearest its end
        v = &voice_[0];
        for (auto& x : voice_) if (x.pos / double(x.s->frames) > v->pos / double(v->s->frames)) v = &x;
    }
    *v = {};
    v->s = s;
    v->slot = slot;
    v->note = note;
    v->rate = slot == kSampler ? std::pow(2.0, (note - int(cur_.samplerRoot)) / 12.0) : 1.0;
    v->gain = vel;
    hits_[slot].fetch_add(1, std::memory_order_relaxed);
}

void Playground::note_on(int note, float vel, int) {
    if (note >= 36 && note < 36 + ANA_PADS) trigger(note - 36, -1, vel);
    else trigger(kSampler, note, vel);
}

void Playground::render(float* out, uint32_t frames, uint32_t ch, uint64_t pos, uint64_t origin, double beatFrames, int beatsPerBar) {
    // Settings, then the voices on a sample that was replaced (it is about to be freed).
    if (const uint32_t a = cfg_.seq.load(std::memory_order_acquire); a != cfgSeen_ && !(a & 1)) {
        PlaygroundConfig c = cfg_.c;
        std::atomic_thread_fence(std::memory_order_acquire);
        if (cfg_.seq.load(std::memory_order_acquire) == a) {
            cur_ = c;
            cfgSeen_ = a;
            for (int k = 0; k < ANA_PADS; k++) {
                const double p = (std::clamp(c.padPan[k], -1.f, 1.f) + 1) * 3.14159265358979 / 4, g = db(c.padGainDb[k]) * 1.41421356;
                gain_[k][0] = float(g * std::cos(p)); gain_[k][1] = float(g * std::sin(p));
            }
            gain_[kSampler][0] = gain_[kSampler][1] = db(c.samplerGainDb);
            master_ = db(c.masterDb);
        }
    }
    for (auto& v : voice_) if (v.s && v.s != slot_[v.slot].load(std::memory_order_acquire)) v.s = nullptr;

    uint32_t msg[64];
    for (auto* q : {&fromApi_, &fromDevice_})
        for (size_t n = q->pop_many(msg, 64), i = 0; i < n; i++) {
            const uint8_t s = uint8_t(msg[i]), d1 = uint8_t(msg[i] >> 8) & 0x7F, d2 = uint8_t(msg[i] >> 16) & 0x7F;
            if ((s & 0xF0) == 0x90 && d2 > 0) note_on(d1, d2 / 127.f, 0);
            else if ((s & 0xF0) == 0x80 || (s & 0xF0) == 0x90)
                for (auto& v : voice_) if (v.s && v.slot == kSampler && v.note == d1) v.release = true;
        }

    // The sequencer: sixteenths from the metronome's origin; a bar has up to 16 of them.
    const int steps = std::clamp(beatsPerBar * 4, 1, 16);
    if (!cur_.sequencerOn || beatFrames <= 0) step_.store(-1, std::memory_order_relaxed);
    float buf[2 * 512];
    for (uint32_t done = 0; done < frames;) {
        const uint32_t n = std::min<uint32_t>(512, frames - done);
        const uint64_t p0 = pos + done;
        std::memset(buf, 0, sizeof(float) * 2 * n);
        // Steps in this chunk start voices at their frame: render up to each, then trigger.
        uint32_t i = 0;
        auto run = [&](uint32_t upto) {
            for (auto& v : voice_) {
                if (!v.s) continue;
                const float gl = gain_[v.slot][0] * v.gain, gr = gain_[v.slot][1] * v.gain, rel = 1.f / (0.03f * float(rate_));
                for (uint32_t j = i; j < upto; j++) {
                    const uint64_t k = uint64_t(v.pos);
                    if (k + 1 >= v.s->frames) { v.s = nullptr; break; }
                    const float f = float(v.pos - double(k)), *x = v.s->lr.data() + 2 * k;
                    if (v.release && (v.env -= rel) <= 0) { v.s = nullptr; break; }
                    buf[2 * j] += v.env * gl * (x[0] + f * (x[2] - x[0]));
                    buf[2 * j + 1] += v.env * gr * (x[1] + f * (x[3] - x[1]));
                    v.pos += v.rate;
                }
            }
            i = upto;
        };
        if (cur_.sequencerOn && beatFrames > 0) {
            const double sf = beatFrames / 4;
            if (sf != stepFrames_ || double(origin) != stepOrigin_) {
                stepFrames_ = sf;
                stepOrigin_ = double(origin);
                nextStep_ = p0 > origin ? uint64_t(std::ceil(double(p0 - origin) / sf)) : 0;
            }
            for (;;) {
                const uint64_t at = origin + uint64_t(std::llround(double(nextStep_) * sf));
                if (at >= p0 + n) break;
                if (at >= p0) {
                    run(uint32_t(at - p0));
                    const int k = int(nextStep_ % uint64_t(steps));
                    step_.store(k, std::memory_order_relaxed);
                    for (int pad = 0; pad < ANA_PADS; pad++) if (cur_.steps[pad] >> k & 1) trigger(pad, -1, 1.f);
                }
                nextStep_++;
            }
        }
        run(n);
        float pk = 0;
        for (uint32_t j = 0; j < n; j++) {
            const float l = buf[2 * j] * master_, r = buf[2 * j + 1] * master_;
            pk = std::max(pk, std::max(std::fabs(l), std::fabs(r)));
            if (ch >= 2) { out[(done + j) * ch] += l; out[(done + j) * ch + 1] += r; }
            else if (ch == 1) out[done + j] += 0.5f * (l + r);
        }
        if (pk > peak_.load(std::memory_order_relaxed)) peak_.store(pk, std::memory_order_relaxed);
        done += n;
    }
    int active = 0;
    for (auto& v : voice_) active += v.s != nullptr;
    voices_.store(active, std::memory_order_relaxed);
    callbacks_.fetch_add(1, std::memory_order_acq_rel);
}

}  // namespace dz
