#include "cqt.hpp"

#include <algorithm>
#include <cmath>
#include <cstring>

namespace dz {

namespace {
constexpr double kPi = 3.14159265358979323846;

void append(std::vector<float>& buf, const float* x, uint32_t m) {
    const size_t len = buf.size();
    if (m >= len) {
        std::memcpy(buf.data(), x + (m - len), len * sizeof(float));
    } else if (m) {
        std::memmove(buf.data(), buf.data() + m, (len - m) * sizeof(float));
        std::memcpy(buf.data() + (len - m), x, m * sizeof(float));
    }
}
}  // namespace

// ---------------------------------------------------------------- CQT

void Cqt::init(double liveRate, float fMin, float fMax, int bpo, float a4, uint32_t maxBlock) {
    liveRate_ = liveRate;
    bpo_ = bpo;
    minMidi_ = int(std::lround(69 + 12 * std::log2(fMin / a4)));
    fMin_ = float(a4 * std::pow(2.0, (minMidi_ - 69) / 12.0));
    octaves_ = std::max(1, int(std::ceil(std::log2(fMax / fMin_) - 1e-9)));
    while (bpo_ * octaves_ > int(ANA_MAX_CQT_BINS)) octaves_--;

    const double q = 1.443 * bpo_;   // Q, as in the §4 physics table
    const double topLow = fMin_ * std::pow(2.0, octaves_ - 1);
    kernels_.clear();
    re_.clear();
    im_.clear();
    bufLen_ = 0;
    for (int set = 0; set < kTuningSets; set++) {
        double cents = (set - kTuningSets / 2) * kTuningStep;
        for (int j = 0; j < bpo_; j++) {
            double f = topLow * std::pow(2.0, j / double(bpo_) + cents / 1200);
            uint32_t len = uint32_t(std::ceil(q * liveRate / f));
            kernels_.push_back({uint32_t(re_.size()), len});
            double wsum = 0;
            for (uint32_t n = 0; n < len; n++) wsum += 0.5 - 0.5 * std::cos(2 * kPi * (n + 0.5) / len);
            for (uint32_t n = 0; n < len; n++) {
                double w = (0.5 - 0.5 * std::cos(2 * kPi * (n + 0.5) / len)) * 2 / wsum;   // |X| = sine amplitude
                re_.push_back(float(w * std::cos(2 * kPi * f * n / liveRate)));
                im_.push_back(float(-w * std::sin(2 * kPi * f * n / liveRate)));
            }
            bufLen_ = std::max(bufLen_, len);
        }
    }

    {   // leakage: response of the mid kernel to a tone one bin higher, relative to its own tone
        const Kernel& kn = kernels_[size_t(kTuningSets / 2 * bpo_ + bpo_ / 2)];
        double f = topLow * std::pow(2.0, (bpo_ / 2) / double(bpo_));
        auto resp = [&](double hz) {
            double sr = 0, si = 0;
            for (uint32_t n = 0; n < kn.length; n++) {
                double ph = 2 * kPi * hz * n / liveRate;
                sr += re_[kn.offset + n] * std::cos(ph) - im_[kn.offset + n] * std::sin(ph);
                si += re_[kn.offset + n] * std::sin(ph) + im_[kn.offset + n] * std::cos(ph);
            }
            return std::sqrt(sr * sr + si * si);
        };
        leakage_ = float(resp(f * std::pow(2.0, 1.0 / bpo_)) / resp(f));
    }

    auto coefs = design_halfband(6, 0.08);
    dec_.assign(size_t(octaves_ - 1), HalfbandDecimator{});
    for (auto& d : dec_) d.init(coefs);
    buf_.assign(size_t(octaves_), std::vector<float>(bufLen_, 0.f));
    scratchA_.assign(maxBlock + 2, 0.f);
    scratchB_.assign(maxBlock + 2, 0.f);

    // Per-octave delay (§5: each octave compensated individually): cascaded half-band group
    // delay at the octave's centre + half the mid-octave kernel.
    delay_.assign(size_t(octaves_), 0.0);
    const double centreNorm = topLow * std::sqrt(2.0) / 2 / liveRate;   // next octave's centre, at this stage's input rate
    const uint32_t midLen = kernels_[size_t(kTuningSets / 2 * bpo_ + bpo_ / 2)].length;
    double cascade = 0;
    for (int o = 0; o < octaves_; o++) {
        double fs = liveRate / std::pow(2.0, o);
        delay_[size_t(o)] = cascade + midLen / 2.0 / fs;
        if (o + 1 < octaves_) cascade += dec_[size_t(o)].group_delay(2 * kPi * centreNorm) / fs;
    }
    tuningSet_ = kTuningSets / 2;
}

void Cqt::push(const float* x, uint32_t n) {
    append(buf_[0], x, n);
    const float* cur = x;
    uint32_t curN = n;
    for (int o = 1; o < octaves_; o++) {
        float* dst = (o & 1) ? scratchA_.data() : scratchB_.data();
        curN = dec_[size_t(o - 1)].process(cur, curN, dst);
        append(buf_[size_t(o)], dst, curN);
        cur = dst;
    }
}

void Cqt::compute(float* magnitude) const {
    const Kernel* set = kernels_.data() + size_t(tuningSet_ * bpo_);
    for (int k = 0; k < bpo_ * octaves_; k++) {
        const int o = octaves_ - 1 - k / bpo_;
        const Kernel& kn = set[k % bpo_];
        const float* x = buf_[size_t(o)].data() + (bufLen_ - kn.length);
        const float* kr = re_.data() + kn.offset;
        const float* ki = im_.data() + kn.offset;
        float sr = 0, si = 0;
        for (uint32_t n = 0; n < kn.length; n++) {
            sr += kr[n] * x[n];
            si += ki[n] * x[n];
        }
        magnitude[k] = std::sqrt(sr * sr + si * si);
    }
}

void Cqt::set_tuning(float cents) {
    tuningSet_ = std::clamp(int(std::lround(cents / kTuningStep)) + kTuningSets / 2, 0, kTuningSets - 1);
}

double Cqt::lowest_window_seconds() const {
    return kernels_[size_t(tuningSet_ * bpo_)].length / (liveRate_ / std::pow(2.0, octaves_ - 1));
}

// ---------------------------------------------------------------- chroma + tuning

ChromaFrontEnd::ChromaFrontEnd(const SessionConfig& s, uint32_t decimation, double nativeRate, float fMin, float fMax, int bpo,
                               uint32_t maxHop, bool autoTune)
    : session_(s), nativeRate_(nativeRate), decimate_(decimation == 2), autoTune_(autoTune) {
    toLive_.init(design_halfband(6, 0.08));
    cqt_.init(nativeRate / (decimate_ ? 2 : 1), fMin, fMax, bpo, s.referenceA4, maxHop);
    live_.assign(maxHop + 2, 0.f);
    deviations_.assign(512, 0.f);
    sorted_.assign(512, 0.f);
    tuning_.referenceA4 = s.referenceA4;
}

void ChromaFrontEnd::process(const float* x, uint32_t n, uint64_t endFrame, Output& out) {
    double sumSq = 0;
    for (uint32_t i = 0; i < n; i++) sumSq += double(x[i]) * x[i];
    const bool silent = std::sqrt(sumSq / n) < gate_;

    uint32_t m = n;
    if (decimate_) m = toLive_.process(x, n, live_.data());
    else std::memcpy(live_.data(), x, n * sizeof(float));
    cqt_.push(live_.data(), m);

    const uint16_t bins = cqt_.bins();
    out.bins = bins;
    out.binsPerOctave = uint16_t(cqt_.bins_per_octave());
    out.minHz = cqt_.min_hz() * std::pow(2.f, cqt_.tuning() / 1200.f);
    cqt_.compute(out.magnitude);
    std::fill(out.magnitude + bins, out.magnitude + ANA_MAX_CQT_BINS, 0.f);

    // Chroma: inter-octave aggregation of energy; a bin between two semitones (24 bpo) is shared.
    // Main-lobe leakage into neighbour bins is removed first: a lone D must not light C# and D#,
    // while two real adjacent tones (B + C) both survive.
    ChromaVector& c = out.chroma;
    c = {};
    const int bpo = cqt_.bins_per_octave(), pc0 = cqt_.min_midi() % 12;
    const float leak = 0.95f * cqt_.neighbour_leakage();
    if (!silent) {
        for (int k = 0; k < bins; k++) {
            float left = k > 0 ? out.magnitude[k - 1] : 0, right = k + 1 < bins ? out.magnitude[k + 1] : 0;
            float clean = std::max(0.f, out.magnitude[k] - leak * std::max(left, right));
            double s = k * 12.0 / bpo;
            int lo = int(std::floor(s));
            float frac = float(s - lo), e = clean * clean;
            c.raw[(pc0 + lo) % 12] += e * (1 - frac);
            if (frac > 0) c.raw[(pc0 + lo + 1) % 12] += e * frac;
        }
        float mx = 0;
        for (int i = 0; i < 12; i++) mx = std::max(mx, c.raw[i]);
        if (mx > 0) {
            const float gamma = 10.f, lmx = std::log1p(gamma);
            float sum = 0;
            for (int i = 0; i < 12; i++) {
                float amp = std::sqrt(c.raw[i] / mx);                     // relative amplitude
                c.normalized[i] = amp < 0.1f ? 0.f : std::log1p(gamma * amp) / lmx;   // < -20 dB: side lobes / noise
                sum += c.normalized[i];
            }
            c.confidence = 1 - sum / 12;   // peaky chroma = confident
        }
    }
    for (int i = 0; i < 12; i++) {
        smoothed_[i] += 0.5f * (c.normalized[i] - smoothed_[i]);   // ~30 ms at 20 ms hops
        c.smoothed[i] = smoothed_[i];
    }
    c.timestampSeconds = double(endFrame) / nativeRate_ - cqt_.octave_delay_seconds(0);
    if (!silent) update_tuning(out.magnitude, bins, out);
    c.tuningOffsetCents = cqt_.tuning();
    out.tuning = tuning_;
}

void ChromaFrontEnd::update_tuning(const float* mag, uint16_t bins, Output&) {
    float mx = *std::max_element(mag, mag + bins);
    const int bpo = cqt_.bins_per_octave();
    for (int k = 1; k + 1 < bins; k++) {
        if (!(mag[k] > mag[k - 1] && mag[k] >= mag[k + 1] && mag[k] > 0.2f * mx)) continue;
        float a = std::log(mag[k - 1] + 1e-9f), b = std::log(mag[k] + 1e-9f), cc = std::log(mag[k + 1] + 1e-9f);
        float den = a - 2 * b + cc;
        float delta = den < 0 ? 0.5f * (a - cc) / den : 0.f;
        double s = (k + delta) * 12.0 / bpo;                    // semitone position on the current grid
        float dev = float((s - std::round(s)) * 100) + cqt_.tuning();
        if (dev >= 50) dev -= 100;
        if (dev < -50) dev += 100;
        deviations_[devCount_++ % deviations_.size()] = dev;
    }
    if (++hopsSinceTuning_ < 25 || devCount_ < 64) return;   // ~0.5 s, and never from a single note
    hopsSinceTuning_ = 0;
    size_t count = std::min<size_t>(devCount_, deviations_.size());
    std::copy(deviations_.begin(), deviations_.begin() + long(count), sorted_.begin());
    std::nth_element(sorted_.begin(), sorted_.begin() + long(count / 2), sorted_.begin() + long(count));
    float median = sorted_[count / 2];
    size_t inliers = 0;
    for (size_t i = 0; i < count; i++) inliers += std::fabs(deviations_[i] - median) <= 15;
    tuning_.confidence = float(inliers) / count;
    tuning_.valid = tuning_.confidence >= 0.6f;
    tuning_.offsetCents = median;
    tuning_.referenceA4 = float(session_.referenceA4 * std::pow(2.0, median / 1200));
    // Hysteresis: switch kernel set only for a clear, confident change.
    if (autoTune_ && tuning_.valid && std::fabs(median - cqt_.tuning()) >= 7) cqt_.set_tuning(median);
}

}  // namespace dz
