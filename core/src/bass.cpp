#include "bass.hpp"

#include <algorithm>
#include <cmath>

namespace dz {

// ---------------------------------------------------------------- onsets

void OnsetDetector::init(const Cqt& cqt, double hopSeconds) {
    bins_ = cqt.bins();
    firstBin_ = std::max(0, bins_ - 3 * cqt.bins_per_octave());   // top 3 octaves: windows <= ~30 ms
    prev_.assign(size_t(bins_), 0.f);
    refractoryHops_ = std::max<uint32_t>(2, uint32_t(std::ceil(0.08 / hopSeconds)));
    mean_ = var_ = 0;
    sinceOnset_ = 1000;
    wasSilent_ = true;
}

bool OnsetDetector::process(const float* mag, bool silent, float& flux) {
    flux = 0;
    float peak = 1e-9f;
    for (int k = firstBin_; k < bins_; k++) peak = std::max(peak, mag[k]);
    for (int k = firstBin_; k < bins_; k++) {
        // log compression relative to an absolute floor, so quiet noise does not look like attacks
        float cur = std::log1p(1000.f * mag[k]), before = std::log1p(1000.f * prev_[size_t(k)]);
        flux += std::max(0.f, cur - before);
        prev_[size_t(k)] = mag[k];
    }
    flux /= float(bins_ - firstBin_);
    sinceOnset_++;

    bool onset = false;
    if (!silent && wasSilent_) onset = true;   // sound after silence
    else if (!silent) {
        float sd = std::sqrt(std::max(0.f, var_));
        onset = flux > mean_ + 3 * sd + 0.05f && sinceOnset_ > refractoryHops_;
    }
    // Adaptive statistics of the flux (slow, ~0.5 s), not updated by the onset frame itself.
    if (!onset) {
        float d = flux - mean_;
        mean_ += 0.05f * d;
        var_ = 0.95f * (var_ + 0.05f * d * d);
    }
    if (onset) sinceOnset_ = 0;
    wasSilent_ = silent;
    return onset;
}

// ---------------------------------------------------------------- bass

BassTracker::BassTracker(const SessionConfig& s, const Cqt& cqt, float bassMin, float bassMax, double settleSeconds)
    : session_(s), bassMin_(bassMin), bassMax_(bassMax), settle_(settleSeconds) {
    speller_.configure(s.keyFifths, s.keyMode);
    octaveShift_ = clef_octave_shift(s.clef);
    // Lowest-rate octave that still carries the whole bass range with margin (rate >= 4 x bassMax).
    octave_ = 0;
    while (octave_ + 1 < cqt.octaves() && cqt.octave_rate(octave_ + 1) >= 4 * bassMax) octave_++;
    const double rate = cqt.octave_rate(octave_);
    window_ = uint32_t(std::ceil(3.0 / bassMin * rate));   // 3 periods of the lowest bass (§11)
    yin_.init(rate, window_, bassMin, bassMax, 0.25f);   // polyphonic low band: looser threshold than voice
    const int bpo = cqt.bins_per_octave();
    auto bin_of = [&](double hz) { return int(std::lround(bpo * std::log2(hz / cqt.min_hz()))); };
    firstBin_ = std::clamp(bin_of(bassMin), 0, cqt.bins() - 1);
    lastBin_ = std::clamp(bin_of(bassMax), 0, cqt.bins() - 1);
}

void BassTracker::process(const Cqt& cqt, const float* mag, const uint8_t* gated, double t, double lastOnset, BassEstimate& out) {
    out = {};

    // (1) Preview: periodicity of the low octave, every hop.
    uint32_t len = 0;
    const float* x = cqt.octave_samples(octave_, len);
    float clarity = 0;
    float previewHz = yin_.estimate(x + (len - window_), clarity);
    if (previewHz < bassMin_ || previewHz > bassMax_) previewHz = 0;   // sub-range candidates rejected (§11)
    out.previewHz = previewHz;

    // (2) Confirmation: lowest strong, ungated CQT peak in the bass range.
    float mx = 0, all = 0;
    for (int k = 0; k < cqt.bins(); k++) all = std::max(all, mag[k]);
    for (int k = firstBin_; k <= lastBin_; k++)
        if (!gated[k]) mx = std::max(mx, mag[k]);
    int bin = -1;
    for (int k = firstBin_; k <= lastBin_ && mx > 0; k++) {
        if (gated[k]) continue;
        bool peak = (k == 0 || mag[k] >= mag[k - 1]) && (k + 1 >= cqt.bins() || mag[k] >= mag[k + 1]);
        if (peak && mag[k] >= 0.3f * mx && mag[k] >= 0.1f * all) { bin = k; break; }
    }
    const int bpo = cqt.bins_per_octave();
    const float tuning = cqt.tuning();
    const double sinceOnset = lastOnset < 0 ? 1e9 : t - lastOnset;
    out.settleRemainingMs = float(std::max(0.0, settle_ - sinceOnset) * 1000);

    int midi = -1;
    float hz = 0;
    if (bin >= 0) {
        midi = cqt.min_midi() + int(std::lround(bin * 12.0 / bpo));
        hz = float(session_.referenceA4 * std::pow(2.0, (midi - 69) / 12.0 + tuning / 1200));
        // Agreement: same note (+-50 cents) or the preview an octave below (chord sub-harmonic).
        float rel = previewHz > 0 ? 12.f * std::log2(previewHz / hz) : 0;
        bool agree = previewHz <= 0 || std::fabs(rel) < 0.5f || std::fabs(rel + 12) < 0.5f;
        out.settled = sinceOnset >= settle_ && agree;
        out.fromPreview = 0;
        out.confidence = std::min(1.f, mag[bin] / mx) * (out.settled ? 1.f : 0.5f);
    } else if (previewHz > 0) {
        midi = int(std::lround(69 + 12 * std::log2(previewHz / session_.referenceA4) - tuning / 100));
        hz = previewHz;
        out.fromPreview = 1;
        out.confidence = 0.5f * clarity;   // reduced: estimation, not resolution
    }
    if (midi < 0) { lastMidi_ = -1; return; }

    out.valid = 1;
    out.midi = int8_t(midi);
    out.pitchClass = int8_t(midi % 12);
    out.frequencyHz = hz;
    speller_.reset_phrase();
    Spelled sp = speller_.spell(midi, lastMidi_);
    out.letter = sp.letter;
    out.alter = sp.alter;
    out.writtenOctave = int8_t(sp.octave + octaveShift_);
    Speller::name(sp, octaveShift_, out.writtenName);
    lastMidi_ = midi;
}

}  // namespace dz
