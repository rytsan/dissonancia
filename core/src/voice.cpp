#include "voice.hpp"

#include <algorithm>
#include <cmath>
#include <cstring>

namespace dz {

namespace {
constexpr double kPi = 3.14159265358979323846;
constexpr float kHysteresisSemitones = 0.7f;   // vibrato never creates a new note
constexpr double kMinNoteSeconds = 0.03, kReleaseSeconds = 0.05;
}  // namespace

// ---------------------------------------------------------------- decimator

void Decimator::init(uint32_t factor, uint32_t maxInput) {
    d_ = factor < 1 ? 1 : factor;
    phase_ = 0;
    if (d_ == 1) { h_.assign(1, 1.f); buf_.assign(maxInput, 0.f); return; }
    const uint32_t taps = 32 * d_ + 1;
    const double fc = 0.4 / d_;   // cutoff = 0.8 x output Nyquist, as a fraction of the native rate
    h_.resize(taps);
    double sum = 0;
    for (uint32_t k = 0; k < taps; k++) {
        double m = k - (taps - 1) / 2.0;
        double sinc = m == 0 ? 2 * fc : std::sin(2 * kPi * fc * m) / (kPi * m);
        double w = 0.54 - 0.46 * std::cos(2 * kPi * k / (taps - 1));   // Hamming
        h_[k] = float(sinc * w);
        sum += h_[k];
    }
    for (auto& v : h_) v = float(v / sum);
    buf_.assign(taps - 1 + maxInput, 0.f);
}

uint32_t Decimator::process(const float* in, uint32_t n, float* out) {
    if (d_ == 1) { std::memcpy(out, in, n * sizeof(float)); return n; }
    const uint32_t hist = uint32_t(h_.size() - 1);
    std::memcpy(buf_.data() + hist, in, n * sizeof(float));
    uint32_t count = 0, i = phase_;
    for (; i < n; i += d_) {
        const float* x = buf_.data() + hist + i;   // newest sample of the dot product
        float acc = 0;
        for (uint32_t k = 0; k <= hist; k++) acc += h_[k] * x[-int(k)];
        out[count++] = acc;
    }
    phase_ = i - n;
    std::memmove(buf_.data(), buf_.data() + n, hist * sizeof(float));
    return count;
}

// ---------------------------------------------------------------- high-pass (RBJ Butterworth)

void Biquad::highpass(double fc, double fs) {
    double w = 2 * kPi * fc / fs, cw = std::cos(w), alpha = std::sin(w) / (2 * 0.70710678);
    double a0 = 1 + alpha;
    b0 = float((1 + cw) / 2 / a0);
    b1 = float(-(1 + cw) / a0);
    b2 = b0;
    a1 = float(-2 * cw / a0);
    a2 = float((1 - alpha) / a0);
    z1 = z2 = 0;
}

// ---------------------------------------------------------------- YIN

void Yin::init(double rate, uint32_t window, float fMin, float fMax, float threshold) {
    rate_ = rate;
    window_ = window;
    tauMax_ = std::min<uint32_t>(uint32_t(std::ceil(rate / fMin)) + 1, window / 2);
    tauMin_ = std::max<uint32_t>(2, uint32_t(rate / fMax));
    threshold_ = threshold;
    d_.assign(tauMax_ + 1, 0.f);
}

float Yin::estimate(const float* x, float& clarity) {
    // ponytail: scalar time-domain d(tau) (~20-50 M MAC/s); SIMD / FFT autocorrelation when the CPU budget says so.
    const uint32_t integ = window_ - tauMax_;
    d_[0] = 1;
    double running = 0;
    for (uint32_t tau = 1; tau <= tauMax_; tau++) {
        float acc = 0;
        for (uint32_t j = 0; j < integ; j++) {
            float diff = x[j] - x[j + tau];
            acc += diff * diff;
        }
        running += acc;
        d_[tau] = running > 0 ? float(acc * tau / running) : 1.f;   // CMND
    }
    uint32_t best = 0;
    for (uint32_t tau = tauMin_; tau < tauMax_; tau++) {
        if (d_[tau] < threshold_) {
            while (tau + 1 < tauMax_ && d_[tau + 1] < d_[tau]) tau++;
            best = tau;
            break;
        }
    }
    if (best == 0) {
        clarity = 1 - *std::min_element(d_.begin() + tauMin_, d_.begin() + tauMax_);
        return 0;
    }
    float a = d_[best - 1], b = d_[best], c = d_[best + 1];
    float den = a - 2 * b + c;
    float shift = den != 0 ? 0.5f * (a - c) / den : 0.f;
    clarity = std::clamp(1 - b, 0.f, 1.f);
    return float(rate_ / (best + std::clamp(shift, -0.5f, 0.5f)));
}

// ---------------------------------------------------------------- pipeline

VoicePipeline::VoicePipeline(const SessionConfig& s, const LiveConfig& c, uint32_t nativeRate, uint32_t maxHopFrames,
                             double latencyCompensation)
    : session_(s), cfg_(c), rate_(nativeRate), liveRate_(double(nativeRate) / c.decimation), latencyComp_(latencyCompensation) {
    dec_.init(c.decimation, maxHopFrames);
    delay_ = dec_.delay_native() / rate_;
    hp_.highpass(0.8 * c.fMin, liveRate_);
    const uint32_t window = uint32_t(std::lround(c.windowSeconds * liveRate_));
    yin_.init(liveRate_, window, c.fMin, c.fMax);
    windowNative_ = window * c.decimation;
    speller_.configure(s.keyFifths, s.keyMode);
    octaveShift_ = clef_octave_shift(s.clef);
    decOut_.assign(maxHopFrames / c.decimation + 2, 0.f);
    window_.assign(window, 0.f);
    hzHistory_.assign(256, 0.f);
    minCandidateHops_ = std::max<uint32_t>(2, uint32_t(std::ceil(kMinNoteSeconds / c.hopSeconds - 1e-6)));
    releaseHops_ = std::max<uint32_t>(2, uint32_t(std::ceil(kReleaseSeconds / c.hopSeconds - 1e-6)));
}

void VoicePipeline::emit(VoiceOutput& out, AnalyzerEventType type, const Note& n, uint64_t endFrame) {
    if (out.eventCount == 4) return;   // cannot happen: at most NoteEnd + NoteStart per hop
    AnalyzerEvent& e = out.events[out.eventCount++];
    e = {};
    e.type = type;
    MusicalNoteEvent& m = e.data.note;
    m.startTimeSeconds = seconds(n.startFrame);
    m.endTimeSeconds = type == AnalyzerEventType::NoteEnd ? seconds(endFrame) : m.startTimeSeconds;
    m.durationSeconds = m.endTimeSeconds - m.startTimeSeconds;
    m.midi = int8_t(n.midi);
    Speller::name(n.spelled, octaveShift_, m.writtenName);
    uint32_t cnt = std::max<uint32_t>(1, n.count);
    m.avgHz = float(n.sumHz / cnt);
    m.avgCents = float(n.sumCents / cnt);
    m.confidence = float(n.sumConf / cnt);
    uint32_t hist = std::min<uint32_t>(n.count, uint32_t(hzHistory_.size()));
    if (hist) {
        std::nth_element(hzHistory_.begin(), hzHistory_.begin() + hist / 2, hzHistory_.begin() + hist);
        m.medianHz = hzHistory_[hist / 2];
    }
    m.chromatic = !n.spelled.diatonic;
    double var = n.sumCentsSq / cnt - double(m.avgCents) * m.avgCents;
    m.vibrato = var > 12.0 * 12.0;   // cents standard deviation > 12 (±17 cents sine)
}

void VoicePipeline::accumulate(float hz, float confidence) {
    double expected = session_.referenceA4 * std::pow(2.0, (cur_.midi - 69) / 12.0);
    float cents = float(1200 * std::log2(hz / expected));
    cur_.sumHz += hz;
    cur_.sumCents += cents;
    cur_.sumConf += confidence;
    cur_.sumCentsSq += double(cents) * cents;
    hzHistory_[cur_.count % hzHistory_.size()] = hz;
    cur_.count++;
}

void VoicePipeline::process(const float* x, uint32_t n, uint64_t endFrame, VoiceOutput& out) {
    out.eventCount = 0;

    double sumSq = 0;
    for (uint32_t i = 0; i < n; i++) sumSq += double(x[i]) * x[i];
    const float rms = float(std::sqrt(sumSq / n));
    const bool silent = rms < gate_;
    if (!silent && wasSilent_) energyOnset_ = endFrame - n;
    wasSilent_ = silent;

    // Decimate, high-pass, slide the analysis window.
    uint32_t m = dec_.process(x, n, decOut_.data());
    for (uint32_t i = 0; i < m; i++) decOut_[i] = hp_(decOut_[i]);
    const uint32_t w = uint32_t(window_.size());
    if (m >= w) {
        std::memcpy(window_.data(), decOut_.data() + (m - w), w * sizeof(float));
    } else {
        std::memmove(window_.data(), window_.data() + m, (w - m) * sizeof(float));
        std::memcpy(window_.data() + (w - m), decOut_.data(), m * sizeof(float));
    }
    filled_ = std::min(w, filled_ + m);

    float clarity = 0, hz = 0;
    if (!silent && filled_ == w) hz = yin_.estimate(window_.data(), clarity);
    const bool voiced = hz >= cfg_.fMin && hz <= cfg_.fMax;

    // Instantaneous pitch: every hop, no median (tuner needle).
    PitchEstimate& p = out.pitch;
    p = {};
    p.voiced = voiced;
    p.frequencyHz = voiced ? hz : 0;
    p.midiFloat = voiced ? 69.f + 12.f * std::log2(hz / session_.referenceA4) : 0;
    p.confidence = voiced ? clarity : 0;
    p.clarity = clarity;
    p.rms = rms;
    p.timestampSeconds = seconds(endFrame - windowNative_ / 2);

    // Stable note: causal median of 3 + hysteresis + minimum duration.
    if (voiced) {
        med_[medPos_] = p.midiFloat;
        medPos_ = (medPos_ + 1) % 3;
        medCount_ = std::min<uint32_t>(3, medCount_ + 1);
    } else {
        medCount_ = medPos_ = 0;
    }
    float median = p.midiFloat;
    if (medCount_ == 3) median = std::max(std::min(med_[0], med_[1]), std::min(std::max(med_[0], med_[1]), med_[2]));
    else if (medCount_ == 2) median = 0.5f * (med_[0] + med_[1]);

    if (!voiced) {
        if (state_ == State::Candidate) state_ = previousMidi_ >= 0 && cur_.count ? State::Stable : State::Silence;
        if (++unvoicedHops_ >= releaseHops_ && state_ == State::Stable) {
            emit(out, AnalyzerEventType::NoteEnd, cur_, lastVoicedEnd_);
            cur_ = {};   // a repeated pitch after silence is a NEW note
            state_ = State::Silence;
            speller_.reset_phrase();
        }
        if (unvoicedHops_ >= releaseHops_) { state_ = State::Silence; candMidi_ = -1; }
    } else {
        unvoicedHops_ = 0;
        lastVoicedEnd_ = endFrame;
        const bool holding = state_ != State::Silence && cur_.count > 0 && std::fabs(median - cur_.midi) < kHysteresisSemitones;
        if (holding) {
            if (std::fabs(p.midiFloat - cur_.midi) < 0.5f) accumulate(hz, clarity);   // skip glide frames
            candMidi_ = -1;
            candHops_ = 0;
            if (state_ == State::Candidate) state_ = State::Stable;
        } else {
            int mi = int(std::lround(median));
            if (mi != candMidi_) {
                candMidi_ = mi;
                candHops_ = 0;
                // From silence the energy onset is sample-accurate to one hop; a pitch change
                // is placed at the centre of the first window that showed it.
                candStart_ = state_ == State::Silence ? energyOnset_ : endFrame - windowNative_ / 2;
                if (state_ == State::Silence) state_ = State::Candidate;
            }
            if (++candHops_ >= minCandidateHops_) {
                // Confirmed: the previous note ends exactly at the new note's backdated onset (§13).
                if (cur_.count > 0 && (state_ == State::Stable || state_ == State::Candidate) && previousMidi_ >= 0 && cur_.startFrame < candStart_)
                    emit(out, AnalyzerEventType::NoteEnd, cur_, candStart_);
                cur_ = {};
                cur_.midi = mi;
                cur_.spelled = speller_.spell(mi, previousMidi_);
                cur_.startFrame = candStart_;
                accumulate(hz, clarity);
                previousMidi_ = mi;
                state_ = State::Stable;
                candMidi_ = -1;
                candHops_ = 0;
                lastLatency_ = float(double(endFrame - std::min(endFrame, candStart_)) / rate_);
                emit(out, AnalyzerEventType::NoteStart, cur_, endFrame);
            }
        }
    }

    NoteEstimate& ne = out.note;
    ne = {};
    if (state_ == State::Stable && cur_.count > 0) {
        double expected = session_.referenceA4 * std::pow(2.0, (cur_.midi - 69) / 12.0);
        ne.valid = 1;
        ne.midi = int8_t(cur_.midi);
        ne.letter = cur_.spelled.letter;
        ne.alter = cur_.spelled.alter;
        ne.writtenOctave = int8_t(cur_.spelled.octave + octaveShift_);
        Speller::name(cur_.spelled, octaveShift_, ne.writtenName);
        ne.detectedHz = voiced ? hz : float(cur_.sumHz / cur_.count);
        ne.expectedHz = float(expected);
        ne.cents = float(1200 * std::log2(ne.detectedHz / expected));
        ne.confidence = float(cur_.sumConf / cur_.count);
        ne.diatonic = cur_.spelled.diatonic;
        ne.chromatic = !cur_.spelled.diatonic;
    }
    out.noteLatencySeconds = lastLatency_;
}

void VoicePipeline::flush(VoiceOutput& out) {
    out.eventCount = 0;
    if (state_ != State::Silence && cur_.count > 0) emit(out, AnalyzerEventType::NoteEnd, cur_, lastVoicedEnd_);
    state_ = State::Silence;
    cur_ = {};
    candMidi_ = -1;
    speller_.reset_phrase();
}

}  // namespace dz
