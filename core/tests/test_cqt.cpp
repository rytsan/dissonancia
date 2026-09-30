#include <catch2/catch_approx.hpp>
#include <catch2/catch_test_macros.hpp>
#include <cmath>
#include <cstdio>
#include <vector>

#include "halfband.hpp"

using namespace dz;
using Catch::Approx;

namespace {
constexpr double kPi = 3.14159265358979323846;

// Measured steady-state gain (dB, from RMS) of the decimator for a sine at `f` (fraction of input rate).
double measured_gain_db(const std::vector<double>& coefs, double f) {
    HalfbandDecimator d;
    d.init(coefs);
    const uint32_t n = 1 << 15;
    std::vector<float> in(n), out(n / 2);
    for (uint32_t i = 0; i < n; i++) in[i] = float(std::sin(2 * kPi * f * i));
    uint32_t m = d.process(in.data(), n, out.data());
    double sq = 0;   // RMS, not peak: samples rarely hit the crest
    for (uint32_t i = m / 2; i < m; i++) sq += double(out[i]) * out[i];
    return 20 * std::log10(std::sqrt(2 * sq / (m - m / 2)) + 1e-12);
}
}  // namespace

TEST_CASE("half-band IIR: passband flat, stopband rejected, computed group delay") {
    auto coefs = design_halfband(6, 0.08);
    for (double a : coefs) CHECK((a > 0 && a < 1));

    HalfbandDecimator d;
    d.init(coefs);
    for (double f : {0.01, 0.05, 0.10, 0.12}) CHECK(std::abs(20 * std::log10(std::abs(d.response(2 * kPi * f)))) < 0.1);
    for (double f : {0.33, 0.40, 0.45, 0.49}) CHECK(20 * std::log10(std::abs(d.response(2 * kPi * f))) < -60);

    // Simulation agrees with the analytic response.
    CHECK(measured_gain_db(coefs, 0.05) == Approx(0).margin(0.1));
    CHECK(measured_gain_db(coefs, 0.42) < -60);

    double gd = d.group_delay(2 * kPi * 0.05);
    std::printf("[measure] half-band 6 coefs, tbw 0.08: group delay %.2f input samples at 0.05 fs\n", gd);
    CHECK((gd > 0.5 && gd < 10));
}

// ---------------------------------------------------------------- CQT, chroma, tuning

#include <algorithm>
#include <chrono>
#include <random>

#include "cqt.hpp"
#include "rt.hpp"

namespace {

constexpr double kLive = 24000;

double hz_of(double midi, double cents = 0) { return 440.0 * std::pow(2.0, (midi - 69 + cents / 100) / 12); }

// Harmonic tone mix (4 partials, 1/h), continuing phase from sample `pos`.
void tones(std::vector<float>& block, uint64_t pos, double rate, const std::vector<double>& midis, float amp, double cents = 0) {
    std::fill(block.begin(), block.end(), 0.f);
    for (double m : midis)
        for (int h = 1; h <= 4; h++)
            for (size_t i = 0; i < block.size(); i++)
                block[i] += float(amp / h * std::sin(2 * kPi * h * hz_of(m, cents) * double(pos + i) / rate));
}

// Reference CQT: every bin directly at the live rate (no decimation), same Q and window.
struct ReferenceCqt {
    std::vector<std::vector<float>> re, im;
    std::vector<float> buf;
    ReferenceCqt(double rate, float fMin, int bins, int bpo) {
        const double q = 1.443 * bpo;
        size_t maxLen = 0;
        for (int k = 0; k < bins; k++) {
            double f = fMin * std::pow(2.0, k / double(bpo));
            size_t len = size_t(std::ceil(q * rate / f));
            std::vector<float> r(len), i(len);
            double wsum = 0;
            for (size_t n = 0; n < len; n++) wsum += 0.5 - 0.5 * std::cos(2 * kPi * (n + 0.5) / len);
            for (size_t n = 0; n < len; n++) {
                double w = (0.5 - 0.5 * std::cos(2 * kPi * (n + 0.5) / len)) * 2 / wsum;
                r[n] = float(w * std::cos(2 * kPi * f * n / rate));
                i[n] = float(-w * std::sin(2 * kPi * f * n / rate));
            }
            maxLen = std::max(maxLen, len);
            re.push_back(std::move(r));
            im.push_back(std::move(i));
        }
        buf.assign(maxLen, 0.f);
    }
    void push(const float* x, size_t n) {
        std::memmove(buf.data(), buf.data() + n, (buf.size() - n) * sizeof(float));
        std::memcpy(buf.data() + buf.size() - n, x, n * sizeof(float));
    }
    void compute(float* mag) const {
        for (size_t k = 0; k < re.size(); k++) {
            const float* x = buf.data() + buf.size() - re[k].size();
            float sr = 0, si = 0;
            for (size_t n = 0; n < re[k].size(); n++) { sr += re[k][n] * x[n]; si += im[k][n] * x[n]; }
            mag[k] = std::sqrt(sr * sr + si * si);
        }
    }
};

double db(double v) { return 20 * std::log10(v + 1e-12); }

SessionConfig chord_session() {
    SessionConfig s{};
    s.mode = AnalysisMode::GuitarChords;
    s.quality = AudioQuality::Balanced;
    s.referenceA4 = 440;
    s.meter = {4, 4};
    s.bpm = 120;
    return s;
}

std::vector<int> top_pcs(const float* chroma, int n) {
    std::vector<int> idx(12);
    for (int i = 0; i < 12; i++) idx[size_t(i)] = i;
    std::sort(idx.begin(), idx.end(), [&](int a, int b) { return chroma[a] > chroma[b]; });
    idx.resize(size_t(n));
    std::sort(idx.begin(), idx.end());
    return idx;
}

}  // namespace

TEST_CASE("CQT: layout, T_low, tones on their bin, agrees with the full-rate reference") {
    Cqt c;
    c.init(kLive, 82.4f, 4200, 12, 440, 480);
    CHECK(c.bins() == 72);
    CHECK(c.octaves() == 6);
    CHECK(c.min_midi() == 40);   // E2
    CHECK(c.lowest_window_seconds() == Approx(0.210).margin(0.005));
    std::printf("[measure] CQT 12 bpo neighbour-bin leakage %.2f (%.1f dB)\n", c.neighbour_leakage(), db(c.neighbour_leakage()));

    Cqt hp;
    hp.init(48000, 55, 4186, 24, 440, 960);
    CHECK(hp.bins() <= ANA_MAX_CQT_BINS);

    for (int midi : {40, 45, 52, 57, 64, 69, 81, 93, 111}) {
        Cqt q;
        q.init(kLive, 82.4f, 4200, 12, 440, 480);
        ReferenceCqt ref(kLive, q.min_hz(), q.bins(), 12);
        std::vector<float> block(480), mag(q.bins()), refMag(q.bins());
        for (uint64_t pos = 0; pos < uint64_t(kLive); pos += 480) {
            for (size_t i = 0; i < block.size(); i++) block[i] = float(0.5 * std::sin(2 * kPi * hz_of(midi) * double(pos + i) / kLive));
            q.push(block.data(), 480);
            ref.push(block.data(), 480);
        }
        q.compute(mag.data());
        ref.compute(refMag.data());
        int bin = midi - 40;
        INFO("midi " << midi << " peak bin " << (std::max_element(mag.begin(), mag.end()) - mag.begin()));
        CHECK(std::max_element(mag.begin(), mag.end()) - mag.begin() == bin);
        CHECK(db(mag[size_t(bin)]) == Approx(db(0.5)).margin(0.5));
        CHECK(db(mag[size_t(bin)]) == Approx(db(refMag[size_t(bin)])).margin(0.5));
        if (bin >= 2) CHECK(db(mag[size_t(bin - 2)]) < db(mag[size_t(bin)]) - 25);
        if (bin + 2 < q.bins()) CHECK(db(mag[size_t(bin + 2)]) < db(mag[size_t(bin)]) - 25);
    }
}

TEST_CASE("chroma: triads and a seventh chord, zero allocation") {
    struct Case { std::vector<double> midis; std::vector<int> pcs; };
    float chordConfidence = 1;
    // Cmaj7 has adjacent pitch classes (B, C): both must survive leakage removal.
    for (auto& [midis, pcs] : std::vector<Case>{{{48, 52, 55}, {0, 4, 7}}, {{57, 60, 64, 67}, {0, 4, 7, 9}}, {{50, 54, 57}, {2, 6, 9}},
                                                {{48, 52, 55, 59}, {0, 4, 7, 11}}}) {
        ChromaFrontEnd fe(chord_session(), 2, 48000, 82.4f, 4200, 12, 960);
        ChromaFrontEnd::Output out{};
        std::vector<float> block(960);
        for (uint64_t pos = 0; pos < 48000; pos += 960) {
            tones(block, pos, 48000, midis, 0.08f);
            RtScope rt;
            fe.process(block.data(), 960, pos + 960, out);
        }
        CHECK(top_pcs(out.chroma.normalized, int(pcs.size())) == pcs);
        chordConfidence = std::min(chordConfidence, out.chroma.confidence);
    }
    // Noise: flat chroma, lower confidence than any chord.
    ChromaFrontEnd fe(chord_session(), 2, 48000, 82.4f, 4200, 12, 960);
    ChromaFrontEnd::Output out{};
    std::mt19937 rng(3);
    std::normal_distribution<float> noise(0, 0.05f);
    std::vector<float> block(960);
    for (uint64_t pos = 0; pos < 48000; pos += 960) {
        for (auto& v : block) v = noise(rng);
        fe.process(block.data(), 960, pos + 960, out);
    }
    INFO("chord confidence " << chordConfidence << " noise " << out.chroma.confidence);
    CHECK(out.chroma.confidence < chordConfidence);
}

TEST_CASE("global tuning: estimated from many peaks, kernels follow, chroma stays right") {
    for (double cents : {0.0, 30.0, -20.0}) {
        ChromaFrontEnd fe(chord_session(), 2, 48000, 82.4f, 4200, 12, 960);
        ChromaFrontEnd::Output out{};
        std::vector<float> block(960);
        const std::vector<std::vector<double>> progression = {{48, 52, 55}, {53, 57, 60}, {55, 59, 62}, {45, 48, 52}};
        for (uint64_t pos = 0; pos < 4 * 48000; pos += 960) tones(block, pos, 48000, progression[pos / 48000], 0.08f, cents), fe.process(block.data(), 960, pos + 960, out);
        INFO("true offset " << cents << " estimated " << out.tuning.offsetCents << " confidence " << out.tuning.confidence);
        CHECK(out.tuning.valid);
        CHECK(out.tuning.offsetCents == Approx(cents).margin(6));
        CHECK(fe.cqt().tuning() == Approx(cents).margin(5));
        CHECK(top_pcs(out.chroma.normalized, 3) == std::vector<int>{0, 4, 9});   // A minor, last second
    }
}

TEST_CASE("microbench: octave-decimated CQT vs full-rate direct CQT") {
    ChromaFrontEnd fe(chord_session(), 2, 48000, 82.4f, 4200, 12, 960);
    ChromaFrontEnd::Output out{};
    ReferenceCqt ref(kLive, fe.cqt().min_hz(), fe.cqt().bins(), 12);
    std::vector<float> block(960), live(480), mag(fe.cqt().bins());
    tones(block, 0, 48000, {48, 52, 55}, 0.08f);
    for (size_t i = 0; i < live.size(); i++) live[i] = block[2 * i];
    const int hops = 500;
    auto t0 = std::chrono::steady_clock::now();
    for (int i = 0; i < hops; i++) fe.process(block.data(), 960, uint64_t(i + 1) * 960, out);
    auto t1 = std::chrono::steady_clock::now();
    for (int i = 0; i < hops; i++) { ref.push(live.data(), live.size()); ref.compute(mag.data()); }
    auto t2 = std::chrono::steady_clock::now();
    double us = std::chrono::duration<double, std::micro>(t1 - t0).count() / hops;
    double refUs = std::chrono::duration<double, std::micro>(t2 - t1).count() / hops;
    std::printf("[measure] CQT 72 bins, 12 bpo, hop 20 ms: decimated %.1f us/hop (%.2f %% of one core), full-rate direct %.1f us/hop (%.1fx)\n",
                us, us / 20000 * 100, refUs, refUs / us);
    CHECK(us < refUs);
}
