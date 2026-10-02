#include "engine.hpp"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstring>

#include "rt.hpp"

namespace dz {

namespace {

constexpr double kPi = 3.14159265358979323846;
constexpr float kVuSeconds = 0.3f, kPeakHoldSeconds = 1.5f;
constexpr double kRecorderRingSeconds = 8.0;   // >= 5 s required (§3)

void data_callback(ma_device* d, void* out, const void* in, ma_uint32 frames) {
    static_cast<Engine*>(d->pUserData)->on_audio(static_cast<const float*>(in), static_cast<float*>(out), frames);
}

std::vector<float> make_click(uint32_t rate, double hz, float gain) {
    std::vector<float> c(static_cast<size_t>(rate * 0.03));
    for (size_t i = 0; i < c.size(); i++) {
        double t = double(i) / rate;
        c[i] = gain * float(std::exp(-t * 150) * std::sin(2 * kPi * hz * t));
    }
    return c;
}

}  // namespace

Engine::Engine() = default;
Engine::~Engine() { stop(); }

double Engine::beat_frames() const { return rate_ * 60.0 / metroBpm_.load(std::memory_order_relaxed); }

uint64_t Engine::next_downbeat_after(uint64_t frame, uint32_t extraBars) const {
    double beat = beat_frames(), bpb = beatsPerBar_.load(std::memory_order_relaxed);
    uint64_t origin = metroOrigin_.load(std::memory_order_relaxed);
    double bars = frame > origin ? std::ceil((frame - origin) / (beat * bpb)) : 0;
    return origin + uint64_t(std::llround((bars + extraBars) * bpb * beat));
}

int Engine::start(const SessionConfig& s, const AudioDeviceConfig& d, ma_context* ctx, const ma_device_id* captureId) {
    if (running_) { error_ = "already running"; return ANA_ERR_STATE; }
    if (!(s.bpm >= 20 && s.bpm <= 400) || s.meter.numerator == 0 || s.meter.numerator > 16) { error_ = "invalid bpm or meter"; return ANA_ERR_ARG; }
    session_ = s;
    now_seconds();   // pin the clock epoch outside the callback

    if (ctx) {
        ma_device_config c = ma_device_config_init(d.clickOutput ? ma_device_type_duplex : ma_device_type_capture);
        c.capture.format = ma_format_f32;
        c.capture.pDeviceID = captureId;
        c.capture.shareMode = d.exclusive ? ma_share_mode_exclusive : ma_share_mode_shared;
        c.playback.format = ma_format_f32;
        c.sampleRate = d.sampleRate;
        if (d.periodFrames) c.periodSizeInFrames = d.periodFrames;
        else c.periodSizeInMilliseconds = 5;
        c.performanceProfile = ma_performance_profile_low_latency;
        c.dataCallback = data_callback;
        c.pUserData = this;
        if (ma_device_init(ctx, &c, &device_) != MA_SUCCESS) { error_ = "cannot open audio device"; return ANA_ERR_DEVICE; }
        deviceOpen_ = true;
        // Read back what the backend actually gave us (§4) — never assume the request.
        rate_ = device_.sampleRate;
        captureChannels_ = device_.capture.channels;
        playbackChannels_ = d.clickOutput ? device_.playback.channels : 0;
        clickDuringTake_ = d.clickDuringTake != 0;
        captureLatencyMs_ = 1000.f * device_.capture.internalPeriodSizeInFrames * device_.capture.internalPeriods /
                            float(device_.capture.internalSampleRate ? device_.capture.internalSampleRate : rate_);
        playbackLatencyMs_ = d.clickOutput ? 1000.f * device_.playback.internalPeriodSizeInFrames * device_.playback.internalPeriods /
                                                 float(device_.playback.internalSampleRate ? device_.playback.internalSampleRate : rate_)
                                           : 0.f;
    } else {
        rate_ = d.sampleRate ? d.sampleRate : 48000;
        captureChannels_ = 1;
        playbackChannels_ = d.clickOutput ? 1 : 0;
        captureLatencyMs_ = playbackLatencyMs_ = 0;
    }

    live_ = live_config(s.mode, s.quality, rate_);
    hopFrames_ = uint32_t(std::lround(live_.hopSeconds * rate_));
    colFrames_ = uint32_t(std::lround(rate_ * 256.0 / 48000));   // 5.3 ms columns

    if (ma_pcm_rb_init(ma_format_f32, 1, rate_, nullptr, nullptr, &analysisRing_) != MA_SUCCESS ||
        ma_pcm_rb_init(ma_format_f32, captureChannels_, uint32_t(rate_ * kRecorderRingSeconds), nullptr, nullptr, &recorderRing_) != MA_SUCCESS) {
        error_ = "ring allocation failed";
        if (deviceOpen_) ma_device_uninit(&device_);
        deviceOpen_ = false;
        return ANA_ERR_STATE;
    }
    ringsInit_ = true;

    // Preallocate and touch everything the steady state uses (§3 pre-warm).
    hopBuf_.assign(hopFrames_, 0.f);
    waveMin_.assign(ANA_WAVE_COLUMNS, 0.f);
    waveMax_.assign(ANA_WAVE_COLUMNS, 0.f);
    scope_.assign(ANA_SCOPE_SAMPLES, 0.f);
    voice_.reset();
    chroma_.reset();
    chords_.reset();
    if (!is_chord_mode(s.mode)) voice_ = std::make_unique<VoicePipeline>(s, live_, rate_, hopFrames_, compensationMs() / 1000.0);
    else {
        chroma_ = std::make_unique<ChromaFrontEnd>(s, live_.decimation, rate_, live_.fMin, live_.fMax, live_.binsPerOctave, hopFrames_,
                                                   live_.bassMin, live_.bassMax, live_.windowSeconds);
        chords_ = std::make_unique<ChordTracker>(s, live_.hopSeconds, compensationMs() / 1000.0);
    }
    tempo_ = std::make_unique<TempoTracker>(live_.hopSeconds);
    contextOut_ = {};
    lastHopDb_ = -120;
    chout_ = {};
    vout_ = {};
    cout_ = {};
    takeLog_.clear();
    takeLog_.reserve(kTakeLogCapacity);
    takeLogOpen_ = takeLogOverflow_ = flushRequest_ = flushDone_ = false;
    clickAccent_ = make_click(rate_, 1760, 0.5f);
    clickBeat_ = make_click(rate_, 1320, 0.35f);

    cbPos_ = anFrames_ = seq_ = 0;
    eventSeq_ = 0;
    cbFrames_ = 0;
    cbTime_ = now_seconds();
    xruns_ = droppedEvents_ = recorderGaps_ = 0;
    meanSquare_ = 0; vu_ = -40; peakHoldDb_ = -120; peakHoldT_ = 0; clip_ = false;
    colMin_ = 1; colMax_ = -1; colFill_ = waveWrite_ = scopeWrite_ = 0;
    cpu_ = 0;
    recStart_ = recStop_ = kNever;
    metroOn_ = s.metronome != 0;
    metroBpm_ = s.bpm;
    beatsPerBar_ = s.meter.numerator;
    metroOrigin_ = 0;
    metroGen_.fetch_add(1, std::memory_order_release);
    cbMetroGen_ = ~0u;
    cbClickPos_ = ~0u;
    stopFlag_ = false;

    analysisThread_ = std::thread([this] { analysis_loop(); });
    recorderThread_ = std::thread([this] { recorder_loop(); });
    running_ = true;

    if (deviceOpen_ && ma_device_start(&device_) != MA_SUCCESS) {
        error_ = "cannot start audio device";
        stop();
        return ANA_ERR_DEVICE;
    }
    return ANA_OK;
}

int Engine::stop() {
    if (!running_) return ANA_OK;
    if (encoder_.load()) rec_stop();   // flush the take while the device still runs
    if (deviceOpen_) { ma_device_uninit(&device_); deviceOpen_ = false; }
    stopFlag_.store(true);
    wake_.fetch_add(1, std::memory_order_release);
    wake_.notify_one();
    analysisThread_.join();
    recorderThread_.join();
    flush_pipeline();   // analysis thread is gone: this thread is now the only producer
    if (ringsInit_) { ma_pcm_rb_uninit(&analysisRing_); ma_pcm_rb_uninit(&recorderRing_); ringsInit_ = false; }
    running_ = false;
    return ANA_OK;
}

// ---------------------------------------------------------------- audio callback

void Engine::on_audio(const float* in, float* out, uint32_t frames) {
    static thread_local bool denormalsOff = (disable_denormals(), true);
    (void)denormalsOff;
    RtScope rt;
    const uint64_t pos = cbPos_;
    const uint32_t ch = captureChannels_;

    // Analysis ring: mono downmix.
    uint32_t done = 0;
    while (done < frames) {
        ma_uint32 n = frames - done;
        void* p = nullptr;
        ma_pcm_rb_acquire_write(&analysisRing_, &n, &p);
        if (n == 0) { xruns_.fetch_add(1, std::memory_order_relaxed); break; }
        float* dst = static_cast<float*>(p);
        for (uint32_t i = 0; i < n; i++) {
            float sum = 0;
            for (uint32_t c = 0; c < ch; c++) sum += in ? in[(done + i) * ch + c] : 0.f;
            dst[i] = sum / float(ch);
        }
        ma_pcm_rb_commit_write(&analysisRing_, n);
        done += n;
    }

    // Recorder ring: only the part of this block inside [recStart, recStop).
    uint64_t rs = recStart_.load(std::memory_order_acquire), re = recStop_.load(std::memory_order_acquire);
    uint64_t a = std::max(pos, rs), b = std::min(pos + frames, re);
    if (rs != kNever && a < b && in) {
        const float* src = in + (a - pos) * ch;
        uint32_t want = uint32_t(b - a), written = 0;
        while (written < want) {
            ma_uint32 n = want - written;
            void* p = nullptr;
            ma_pcm_rb_acquire_write(&recorderRing_, &n, &p);
            if (n == 0) break;
            std::memcpy(p, src + size_t(written) * ch, size_t(n) * ch * sizeof(float));
            ma_pcm_rb_commit_write(&recorderRing_, n);
            written += n;
        }
        if (written < want) {   // never silent loss: counted and marked in the sidecar
            recDropped_.fetch_add(want - written, std::memory_order_relaxed);
            recorderGaps_.fetch_add(1, std::memory_order_relaxed);
        }
    }

    if (out) mix_click(out, frames, pos);

    cbPos_ = pos + frames;
    cbTime_.store(now_seconds(), std::memory_order_relaxed);
    cbFrames_.store(cbPos_, std::memory_order_release);

    // Notify rule (§3): wake only when >= 1 hop is unread AND the analysis thread waits.
    std::atomic_thread_fence(std::memory_order_seq_cst);
    if (waiting_.load(std::memory_order_relaxed) && ma_pcm_rb_available_read(&analysisRing_) >= hopFrames_) {
        wake_.fetch_add(1, std::memory_order_release);
        wake_.notify_one();
        notifies_.fetch_add(1, std::memory_order_relaxed);
    }
}

void Engine::mix_click(float* out, uint32_t frames, uint64_t pos) {
    uint32_t g = metroGen_.load(std::memory_order_acquire);
    if (g != cbMetroGen_) {
        cbMetroGen_ = g;
        cbMetroOn_ = metroOn_.load(std::memory_order_relaxed);
        cbBeatsPerBar_ = beatsPerBar_.load(std::memory_order_relaxed);
        cbOrigin_ = metroOrigin_.load(std::memory_order_relaxed);
        cbBeatFrames_ = beat_frames();
        cbNextBeat_ = pos > cbOrigin_ ? uint64_t(std::ceil((pos - cbOrigin_) / cbBeatFrames_)) : 0;
    }
    const uint32_t ch = playbackChannels_;
    uint64_t beatFrame = cbOrigin_ + uint64_t(std::llround(cbNextBeat_ * cbBeatFrames_));
    for (uint32_t i = 0; i < frames; i++) {
        if (pos + i == beatFrame) {
            const uint64_t rs = recStart_.load(std::memory_order_relaxed), re = recStop_.load(std::memory_order_relaxed);
            const bool inTake = rs != kNever && pos + i >= rs && pos + i < re;
            if (cbMetroOn_ && (clickDuringTake_ || !inTake)) { cbClickPos_ = 0; cbAccent_ = cbNextBeat_ % cbBeatsPerBar_ == 0; }
            cbNextBeat_++;
            beatFrame = cbOrigin_ + uint64_t(std::llround(cbNextBeat_ * cbBeatFrames_));
        }
        const std::vector<float>& click = cbAccent_ ? clickAccent_ : clickBeat_;
        if (cbClickPos_ < click.size()) {
            float v = click[cbClickPos_++];
            for (uint32_t c = 0; c < ch; c++) out[i * ch + c] += v;
        }
    }
}

// ---------------------------------------------------------------- analysis thread

void Engine::analysis_loop() {
    disable_denormals();
    promote_thread();
    for (;;) {
        while (ma_pcm_rb_available_read(&analysisRing_) >= hopFrames_) {
            RtScope rt;
            double t0 = now_seconds();
            uint32_t got = 0;
            while (got < hopFrames_) {
                ma_uint32 n = hopFrames_ - got;
                void* p = nullptr;
                ma_pcm_rb_acquire_read(&analysisRing_, &n, &p);
                std::memcpy(hopBuf_.data() + got, p, n * sizeof(float));
                ma_pcm_rb_commit_read(&analysisRing_, n);
                got += n;
            }
            process_hop(hopBuf_.data(), hopFrames_);
            float busy = float((now_seconds() - t0) / live_.hopSeconds) * 100.f;
            cpu_ += 0.05f * (busy - cpu_);
            if (flushRequest_.load(std::memory_order_acquire)) flush_pipeline();
        }
        if (flushRequest_.load(std::memory_order_acquire)) flush_pipeline();
        if (stopFlag_.load()) break;
        uint32_t g = wake_.load(std::memory_order_acquire);
        waiting_.store(true, std::memory_order_relaxed);
        std::atomic_thread_fence(std::memory_order_seq_cst);
        if (ma_pcm_rb_available_read(&analysisRing_) < hopFrames_ && !stopFlag_.load() && !flushRequest_.load())
            wake_.wait(g, std::memory_order_acquire);
        waiting_.store(false, std::memory_order_relaxed);
    }
}

void Engine::process_hop(const float* x, uint32_t n) {
    double sumSq = 0;
    float peak = 0;
    for (uint32_t i = 0; i < n; i++) {
        float v = x[i];
        sumSq += double(v) * v;
        peak = std::max(peak, std::fabs(v));
        colMin_ = std::min(colMin_, v);
        colMax_ = std::max(colMax_, v);
        if (++colFill_ == colFrames_) {
            waveMin_[waveWrite_] = colMin_;
            waveMax_[waveWrite_] = colMax_;
            waveWrite_ = (waveWrite_ + 1) % ANA_WAVE_COLUMNS;
            colMin_ = 1; colMax_ = -1; colFill_ = 0;
        }
        scope_[scopeWrite_] = v;
        scopeWrite_ = (scopeWrite_ + 1) % ANA_SCOPE_SAMPLES;
    }
    anFrames_ += n;
    const double t = double(anFrames_) / rate_;   // input sample clock

    meanSquare_ += float(1 - std::exp(-live_.hopSeconds / kVuSeconds)) * (float(sumSq / n) - meanSquare_);
    vu_ = 10.f * std::log10(meanSquare_ + 1e-12f) + 18.f;
    float peakDb = 20.f * std::log10(peak + 1e-9f);
    if (peakDb >= peakHoldDb_ || t - peakHoldT_ > kPeakHoldSeconds) { peakHoldDb_ = peakDb; peakHoldT_ = t; }
    if (clearClip_.exchange(false, std::memory_order_relaxed)) clip_ = false;
    if (peak >= 0.999f) clip_ = true;

    // Onset envelope for the tempo estimate (beat marks): chord modes use the CQT spectral flux; mono modes
    // the rise of the hop level plus one pulse per note start (legato changes have no level rise).
    const float hopDb = 10.f * std::log10(float(sumSq / n) + 1e-12f);
    float onsetStrength = 0;

    if (voice_) {
        voice_->process(x, n, anFrames_, vout_);
        for (uint32_t i = 0; i < vout_.eventCount; i++) {
            publish_event(vout_.events[i]);
            if (vout_.events[i].type == AnalyzerEventType::NoteStart) onsetStrength += 1;
        }
        if (hopDb > -50) onsetStrength += std::max(0.f, hopDb - lastHopDb_) / 10.f;
    }
    if (chroma_) {
        chroma_->process(x, n, anFrames_, cout_);
        if (cout_.onset) {   // display only (§3): the score uses complete note/chord events
            AnalyzerEvent e{};
            e.type = AnalyzerEventType::Onset;
            e.data.onset.timestampSeconds = cout_.lastOnset - compensationMs() / 1000.0;
            e.data.onset.strength = cout_.flux;
            e.data.onset.type = OnsetType::ChordAttack;
            publish_event(e);
        }
        chords_->process(cout_.chroma, cout_.chroma.timestampSeconds, double(anFrames_) / rate_, cout_.bass, cout_.lastOnset, chout_);
        for (uint32_t i = 0; i < chout_.eventCount; i++) publish_event(chout_.events[i]);
        onsetStrength = cout_.flux;
    }
    lastHopDb_ = hopDb;
    tempo_->process(onsetStrength, t, contextOut_);

    LiveSnapshot& s = snapshots_.write_slot();
    double now = now_seconds();
    uint64_t cbFrames = cbFrames_.load(std::memory_order_acquire);
    double cbTime = cbTime_.load(std::memory_order_relaxed);
    s.sequence = ++seq_;
    s.publishTimeSeconds = now;
    s.analyzedFrames = anFrames_;
    s.sampleRate = rate_;
    s.liveRate = rate_ / live_.decimation;
    s.hopSeconds = live_.hopSeconds;
    s.settleSeconds = live_.windowSeconds;
    s.latencyCaptureMs = captureLatencyMs_;
    // ponytail: cbFrames/cbTime read as two atomics; a tear costs one callback period of error.
    s.latencyProcessingMs = float(std::max(0.0, now - cbTime + double(cbFrames - std::min(cbFrames, anFrames_)) / rate_) * 1000);
    s.xruns = xruns_.load(std::memory_order_relaxed);
    s.droppedEvents = droppedEvents_.load(std::memory_order_relaxed);
    s.recorderGaps = recorderGaps_.load(std::memory_order_relaxed);

    bool metro = metroOn_.load(std::memory_order_relaxed);
    uint64_t origin = metroOrigin_.load(std::memory_order_relaxed);
    uint32_t bpb = beatsPerBar_.load(std::memory_order_relaxed);
    s.metronomeBpm = metro ? metroBpm_.load(std::memory_order_relaxed) : 0;
    s.beatInBar = metro && anFrames_ >= origin ? uint8_t(uint64_t((anFrames_ - origin) / beat_frames()) % bpb + 1) : 0;
    uint64_t rs = recStart_.load(std::memory_order_acquire), re = recStop_.load(std::memory_order_acquire);
    s.countingIn = rs != kNever && anFrames_ < rs;
    s.recording = rs != kNever && anFrames_ >= rs && anFrames_ < re;
    s.recordedSeconds = rs == kNever ? 0 : anFrames_ >= rs ? double(std::min(anFrames_, re) - rs) / rate_ : -double(rs - anFrames_) / rate_;

    s.vuLevel = vu_;
    s.peakDbfs = peakDb;
    s.peakHoldDbfs = peakHoldDb_;
    s.clipLatched = clip_;
    s.waveWriteIndex = waveWrite_;
    s.waveColumnSeconds = float(colFrames_) / rate_;
    s.cpuPercent = cpu_;
    s.pitch = vout_.pitch;
    s.note = vout_.note;
    s.noteLatencyMs = vout_.note.valid ? vout_.noteLatencySeconds * 1000.f + s.latencyProcessingMs : 0.f;
    std::memcpy(s.waveMin, waveMin_.data(), sizeof s.waveMin);
    std::memcpy(s.waveMax, waveMax_.data(), sizeof s.waveMax);
    size_t tail = ANA_SCOPE_SAMPLES - scopeWrite_;
    std::memcpy(s.scope, scope_.data() + scopeWrite_, tail * sizeof(float));
    std::memcpy(s.scope + tail, scope_.data(), scopeWrite_ * sizeof(float));
    std::memset(s._pad1, 0, sizeof s._pad1);
    s.cqtBinCount = cout_.bins;
    s.cqtBinsPerOctave = cout_.binsPerOctave;
    s.cqtMinHz = cout_.minHz;
    s.tuning = cout_.tuning;
    s.chroma = cout_.chroma;
    std::memcpy(s.cqtMagnitude, cout_.magnitude, sizeof s.cqtMagnitude);
    s.chord = chout_.preview;
    std::memcpy(s.confirmedSymbol, chout_.confirmedSymbol, sizeof s.confirmedSymbol);
    s.chordConfirmed = chout_.confirmed;
    std::memset(s._pad2, 0, sizeof s._pad2);
    s.chordLatencyMs = chout_.preview.best.symbol[0] ? chout_.latencyMs + s.latencyProcessingMs : 0.f;
    s.chordConfirmElapsedMs = chout_.confirmElapsedMs;
    s.bass = cout_.bass;
    s.lastOnsetSeconds = chroma_ && cout_.lastOnset >= 0 ? cout_.lastOnset - compensationMs() / 1000.0 : -1;
    s.context = contextOut_;
    snapshots_.publish();
}

bool Engine::push_event(AnalyzerEvent e) {
    e.sequence = eventSeq_++;   // consumed even when dropped, so the reader sees the gap
    if (events_.try_push(e)) return true;
    droppedEvents_.fetch_add(1, std::memory_order_relaxed);
    return false;
}

void Engine::publish_event(const AnalyzerEvent& e) {
    AnalyzerEvent copy = e;
    push_event(copy);
    bool complete = e.type == AnalyzerEventType::NoteEnd || e.type == AnalyzerEventType::ChordEnded || e.type == AnalyzerEventType::Cadence;
    uint64_t rs = recStart_.load(std::memory_order_acquire);
    if (complete && takeLogOpen_.load(std::memory_order_acquire) && rs != kNever && anFrames_ >= rs) {
        if (takeLog_.size() < kTakeLogCapacity) takeLog_.push_back(e);   // within reserved capacity: no allocation
        else takeLogOverflow_.store(true, std::memory_order_relaxed);
    }
}

// Closes open notes/chords as complete events (§3 completeness rule). Runs on the analysis
// thread (REC stop) or on the API thread after the analysis thread has joined (ana_stop).
void Engine::flush_pipeline() {
    if (voice_) {
        voice_->flush(vout_);
        for (uint32_t i = 0; i < vout_.eventCount; i++) publish_event(vout_.events[i]);
        vout_.eventCount = 0;
    }
    if (chords_) {
        chords_->flush(chout_);
        for (uint32_t i = 0; i < chout_.eventCount; i++) publish_event(chout_.events[i]);
        chout_.eventCount = 0;
    }
    takeLogOpen_.store(false, std::memory_order_release);
    flushRequest_.store(false, std::memory_order_relaxed);
    flushDone_.store(true, std::memory_order_release);
}

void Engine::read_snapshot(LiveSnapshot* out) { *out = snapshots_.read(); }

// ---------------------------------------------------------------- metronome / REC (API thread)

int Engine::set_metronome(bool on, float bpm, TimeSignature meter) {
    if (!running_) return ANA_ERR_STATE;
    if (recStart_.load() != kNever) { error_ = "metronome is locked while REC is armed"; return ANA_ERR_STATE; }
    if (!(bpm >= 20 && bpm <= 400) || meter.numerator == 0 || meter.numerator > 16) { error_ = "invalid bpm or meter"; return ANA_ERR_ARG; }
    metroOn_ = on;
    metroBpm_ = bpm;
    beatsPerBar_ = meter.numerator;
    session_.bpm = bpm;
    session_.meter = meter;
    session_.metronome = on;
    metroOrigin_ = cbFrames_.load(std::memory_order_acquire);
    metroGen_.fetch_add(1, std::memory_order_release);
    return ANA_OK;
}

int Engine::rec_start(const char* wavPath) {
    if (!running_ || encoder_.load() || recStart_.load() != kNever) { error_ = "not running or already recording"; return ANA_ERR_STATE; }
    auto* enc = new ma_encoder;
    ma_encoder_config ec = ma_encoder_config_init(ma_encoding_format_wav, ma_format_f32, captureChannels_, rate_);
    if (ma_encoder_init_file(wavPath, &ec, enc) != MA_SUCCESS) {
        delete enc;
        error_ = std::string("cannot create ") + wavPath;
        return ANA_ERR_IO;
    }
    if (!metroOn_.load()) {   // REC forces the metronome (§5)
        metroOn_ = true;
        session_.metronome = true;
        metroOrigin_ = cbFrames_.load(std::memory_order_acquire);
        metroGen_.fetch_add(1, std::memory_order_release);
    }
    recPath_ = wavPath;
    takeLog_.clear();   // analysis thread does not touch the log while it is closed
    takeLogOverflow_ = false;
    takeLogOpen_.store(true, std::memory_order_release);
    recWritten_ = 0;
    recDropped_ = 0;
    recorderGaps_ = 0;
    encoder_.store(enc, std::memory_order_release);
    recStop_.store(kNever, std::memory_order_release);
    recStart_.store(next_downbeat_after(cbFrames_.load(std::memory_order_acquire), session_.countInBars), std::memory_order_release);
    return ANA_OK;
}

int Engine::rec_stop() {
    if (!encoder_.load()) return ANA_ERR_STATE;
    uint64_t start = recStart_.load();
    // Stop 100 ms ahead of the callback clock: every callback sees the new stop before
    // reaching it, so no block ever writes past it.
    uint64_t stopAt = std::max(cbFrames_.load(std::memory_order_acquire) + rate_ / 10, start);
    bool empty = cbFrames_.load() + rate_ / 10 <= start;   // stopped during count-in
    if (empty) stopAt = start;
    recStop_.store(stopAt, std::memory_order_release);

    // The recorder thread owns the encoder: wait until it wrote the take and closed the file.
    auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(3);
    while (encoder_.load(std::memory_order_acquire) && std::chrono::steady_clock::now() < deadline)
        std::this_thread::sleep_for(std::chrono::milliseconds(2));
    bool closed = encoder_.load() == nullptr;

    // Flush open events into the take log on the analysis thread, then read the (closed) log.
    flushDone_.store(false);
    flushRequest_.store(true, std::memory_order_release);
    wake_.fetch_add(1, std::memory_order_release);
    wake_.notify_one();
    auto flushDeadline = std::chrono::steady_clock::now() + std::chrono::seconds(1);
    while (!flushDone_.load(std::memory_order_acquire) && std::chrono::steady_clock::now() < flushDeadline)
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    takeLogOpen_.store(false, std::memory_order_release);

    if (closed) {
        if (empty) std::remove(recPath_.c_str());
        else write_sidecar(recWritten_.load(), recDropped_.load());
    }
    recStart_.store(kNever, std::memory_order_release);
    recStop_.store(kNever, std::memory_order_release);
    if (!closed) { error_ = "recorder did not finish (audio stalled?)"; return ANA_ERR_IO; }
    return ANA_OK;
}

void Engine::recorder_loop() {
    auto drain = [this] {
        ma_encoder* enc = encoder_.load(std::memory_order_acquire);
        uint64_t rs = recStart_.load(std::memory_order_acquire), re = recStop_.load(std::memory_order_acquire);
        uint64_t total = (enc && re != kNever) ? re - rs : kNever;
        for (;;) {
            ma_uint32 n = ma_pcm_rb_available_read(&recorderRing_);
            if (n == 0) break;
            void* p = nullptr;
            ma_pcm_rb_acquire_read(&recorderRing_, &n, &p);
            if (enc) {
                uint64_t done = recWritten_.load() + recDropped_.load();
                uint64_t w = total == kNever ? n : std::min<uint64_t>(n, total > done ? total - done : 0);
                if (w) ma_encoder_write_pcm_frames(enc, p, w, nullptr);
                recWritten_.fetch_add(w);
            }
            ma_pcm_rb_commit_read(&recorderRing_, n);   // outside a take: discarded
        }
        if (enc && total != kNever && recWritten_.load() + recDropped_.load() >= total) {
            ma_encoder_uninit(enc);
            delete enc;
            encoder_.store(nullptr, std::memory_order_release);
        }
    };
    while (!stopFlag_.load()) {
        drain();
        std::this_thread::sleep_for(std::chrono::milliseconds(5));   // not real-time: polling is fine here
    }
    drain();
}

void Engine::write_sidecar(uint64_t frames, uint64_t dropped) {
    std::string path = recPath_;
    size_t dot = path.find_last_of('.');
    path = (dot == std::string::npos ? path : path.substr(0, dot)) + ".json";
    FILE* f = std::fopen(path.c_str(), "w");
    if (!f) return;
    const SessionConfig& s = session_;
    std::fprintf(f,
                 "{\n"
                 "  \"format\": \"dissonancia-take/1\",\n"
                 "  \"session\": {\"mode\": %d, \"quality\": %d, \"referenceA4\": %.3f, \"keySet\": %s, \"keyFifths\": %d,"
                 " \"keyMode\": %d, \"clef\": %d, \"meter\": [%d, %d], \"bpm\": %.3f, \"countInBars\": %d},\n"
                 "  \"deviceRate\": %u,\n"
                 "  \"channels\": %u,\n"
                 "  \"inputLatencyMs\": %.3f,\n"
                 "  \"outputLatencyMs\": %.3f,\n"
                 "  \"compensationLatencyMs\": %.3f,\n"
                 "  \"latencySource\": \"reported\",\n"
                 "  \"startSample\": %llu,\n"
                 "  \"firstDownbeatSample\": 0,\n"
                 "  \"frames\": %llu,\n"
                 "  \"recorderGaps\": %u,\n"
                 "  \"droppedFrames\": %llu,\n"
                 "  \"normalizationGainDb\": null,\n"
                 "  \"eventLogComplete\": %s,\n"
                 "  \"eventTimeBase\": \"seconds from the first downbeat of the take\",\n"
                 "  \"events\": [",
                 int(s.mode), int(s.quality), s.referenceA4, s.keySet ? "true" : "false", s.keyFifths, int(s.keyMode), int(s.clef),
                 s.meter.numerator, s.meter.denominator, s.bpm, s.countInBars, rate_, captureChannels_, captureLatencyMs_,
                 playbackChannels_ ? playbackLatencyMs_ : 0.f, compensationMs(),
                 (unsigned long long)recStart_.load(), (unsigned long long)frames, recorderGaps_.load(), (unsigned long long)dropped,
                 !takeLogOverflow_.load() && flushDone_.load() ? "true" : "false");
    const double t0 = double(recStart_.load()) / rate_;
    for (size_t i = 0; i < takeLog_.size(); i++) {
        const AnalyzerEvent& e = takeLog_[i];
        if (e.type == AnalyzerEventType::ChordEnded) {
            const ChordEvent& c = e.data.chord;
            std::fprintf(f, "%s\n    {\"type\": \"chord\", \"seq\": %u, \"start\": %.4f, \"end\": %.4f, \"symbol\": \"%s\", \"roman\": \"%s\","
                            " \"diatonicStatus\": %d, \"root\": %d, \"bass\": %d, \"inversion\": %d, \"quality\": %d, \"confidence\": %.3f,"
                            " \"incomplete\": %s, \"bassSettled\": %s}",
                         i ? "," : "", e.sequence, c.startTimeSeconds - t0, c.endTimeSeconds - t0, c.symbol, c.roman, int(c.diatonicStatus),
                         c.rootPitchClass, c.bassPitchClass, c.inversion, int(c.quality), c.confidence, c.incomplete ? "true" : "false",
                         c.bassSettled ? "true" : "false");
            continue;
        }
        if (e.type == AnalyzerEventType::Cadence) {
            const CadenceEvent& c = e.data.cadence;
            std::fprintf(f, "%s\n    {\"type\": \"cadence\", \"seq\": %u, \"time\": %.4f, \"cadence\": %d, \"from\": \"%s\", \"to\": \"%s\","
                            " \"confidence\": %.3f, \"evidence\": \"%s\"}",
                         i ? "," : "", e.sequence, c.timestampSeconds - t0, int(c.type), c.fromRoman, c.toRoman, c.confidence, c.evidence);
            continue;
        }
        if (e.type != AnalyzerEventType::NoteEnd) continue;
        const MusicalNoteEvent& n = e.data.note;
        std::fprintf(f,
                     "%s\n    {\"type\": \"note\", \"seq\": %u, \"start\": %.4f, \"end\": %.4f, \"midi\": %d, \"name\": \"%s\","
                     " \"avgHz\": %.2f, \"medianHz\": %.2f, \"avgCents\": %.1f, \"confidence\": %.3f, \"chromatic\": %s, \"vibrato\": %s}",
                     i ? "," : "", e.sequence, n.startTimeSeconds - t0, n.endTimeSeconds - t0, n.midi, n.writtenName, n.avgHz, n.medianHz,
                     n.avgCents, n.confidence, n.chromatic ? "true" : "false", n.vibrato ? "true" : "false");
    }
    std::fprintf(f, "\n  ]\n}\n");
    std::fclose(f);
}

}  // namespace dz
