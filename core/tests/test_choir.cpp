#include <catch2/catch_test_macros.hpp>
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <random>
#include <string>
#include <vector>

#include "choir.hpp"
#include "miniaudio.h"
#include "post.hpp"

using namespace dz;

namespace {
constexpr double kPi = 3.14159265358979323846;
constexpr uint32_t kRate = 44100;

// Three chorales, bass to soprano, one chord per second: I IV V I vi ii V7 I in C; a G major
// one in open position with a passing tenor; an F major one with the alto and tenor a third apart
// and a bass leap of an octave.
const std::vector<std::vector<std::array<int, 4>>> kChorales = {
    {{48, 55, 64, 72}, {41, 57, 65, 72}, {43, 59, 62, 74}, {48, 55, 64, 72}, {45, 57, 64, 72}, {50, 57, 65, 74}, {43, 59, 65, 74}, {48, 55, 64, 72}},
    {{43, 59, 62, 71}, {48, 60, 64, 72}, {50, 57, 66, 74}, {43, 59, 67, 74}, {52, 59, 67, 76}, {45, 57, 64, 72}, {50, 57, 66, 74}, {43, 55, 62, 71}},
    {{41, 57, 60, 69}, {46, 58, 62, 70}, {48, 55, 64, 72}, {41, 57, 65, 69}, {53, 57, 62, 74}, {46, 58, 65, 74}, {48, 58, 64, 72}, {41, 57, 65, 69}},
};

// A sung-like voice: harmonics under a two-formant envelope, vibrato, a small breath between chords.
void voice(std::vector<float>& y, const std::vector<std::array<int, 4>>& chorale, int section, double formant1, double formant2, double vibratoHz, std::mt19937& rng) {
    std::normal_distribution<double> detune(0, 4);   // cents
    double phase = 0;
    for (size_t c = 0; c < chorale.size(); c++) {
        const double cents = detune(rng), f0 = 440 * std::pow(2, (chorale[c][size_t(3 - section)] - 69 + cents / 100) / 12.0);
        for (size_t i = size_t(double(c) * kRate); i < size_t(double(c + 1) * kRate) && i < y.size(); i++) {
            const double t = double(i) / kRate, local = t - double(c);
            const double env = std::min({1.0, local / 0.05, (1.0 - local) / 0.06}) * 0.9;
            const double f = f0 * std::pow(2, 0.25 / 12 * std::sin(2 * kPi * vibratoHz * t));
            phase += 2 * kPi * f / kRate;
            double v = 0;
            for (int h = 1; h * f0 < 5000; h++) {
                const double fh = h * f0;
                const double a = 1.0 / h * (1 + 2.5 * std::exp(-std::pow((fh - formant1) / 150, 2)) + 1.5 * std::exp(-std::pow((fh - formant2) / 250, 2)));
                v += a * std::sin(h * phase);
            }
            y[i] += float(0.08 * env * v);
        }
    }
}

std::vector<float> sing(const std::vector<std::array<int, 4>>& chorale, unsigned seed) {
    std::vector<float> y(size_t((double(chorale.size()) + 0.2) * kRate), 0.f);
    std::mt19937 rng(seed);
    voice(y, chorale, kSoprano, 700, 1200, 5.6, rng);
    voice(y, chorale, kAlto, 600, 1000, 5.1, rng);
    voice(y, chorale, kTenor, 500, 900, 5.9, rng);
    voice(y, chorale, kBass, 400, 800, 4.8, rng);
    return y;
}

struct Score { int right[kSections]{}, total = 0, elsewhere = 0; };

// A chord's note is right when the section's note at the chord's middle has its pitch; a note
// "elsewhere" carries another section's pitch of that chord.
Score score(const std::vector<std::array<int, 4>>& chorale, const std::vector<ChoirNote>& notes) {
    Score s;
    for (size_t c = 0; c < chorale.size(); c++)
        for (int k = 0; k < kSections; k++) {
            s.total++;
            for (auto& n : notes)
                if (n.section == k && n.start <= double(c) + 0.5 && n.end >= double(c) + 0.5) {
                    s.right[k] += n.midi == chorale[c][size_t(3 - k)];
                    break;
                }
        }
    for (auto& n : notes) {
        const size_t c = size_t(std::clamp(int((n.start + n.end) / 2), 0, int(chorale.size()) - 1));
        bool other = false;
        for (int k = 0; k < kSections; k++) other |= k != n.section && chorale[c][size_t(3 - k)] == n.midi && chorale[c][size_t(3 - n.section)] != n.midi;
        s.elsewhere += other;
    }
    return s;
}
}  // namespace

TEST_CASE("choir: three synthetic chorales, each note on its section") {
    std::vector<std::vector<ChoirFrame>> frames;
    for (size_t i = 0; i < kChorales.size(); i++) {
        auto y = sing(kChorales[i], unsigned(7 + i));
        frames.push_back(choir_frames(y.data(), y.size(), kRate));
    }
    auto run = [&](const ChoirTuning& t, bool print) {
        int right[kSections]{}, total = 0, elsewhere = 0;
        for (size_t i = 0; i < kChorales.size(); i++) {
            auto s = score(kChorales[i], choir_notes(frames[i], t));
            for (int k = 0; k < kSections; k++) right[k] += s.right[k];
            total += s.total;
            elsewhere += s.elsewhere;
        }
        const int n = total / kSections;
        if (print)
            std::printf("[measure] choir, 3 synthetic chorales (%d chords): S %d/%d, A %d/%d, T %d/%d, B %d/%d notes right; %d notes on another section\n",
                        n, right[0], n, right[1], n, right[2], n, right[3], n, elsewhere);
        return std::array<int, 5>{right[0], right[1], right[2], right[3], elsewhere};
    };
    if (std::getenv("DZ_CHOIR_SWEEP"))   // the defaults' search: ./dz_tests "choir: three*" with DZ_CHOIR_SWEEP=1
        for (double u : {0.75, 1.5, 3.0, 5.0})
            for (double j : {0.15, 0.3, 0.6}) {
                auto r = run({u, j}, false);
                std::printf("u %.2f j %.2f: %d %d %d %d (%d) sum %d\n", u, j, r[0], r[1], r[2], r[3], r[4], r[0] + r[1] + r[2] + r[3]);
            }
    auto r = run({}, true);
    for (int k = 0; k < kSections; k++) CHECK(r[size_t(k)] >= 17);   // of 24; measured S 18, A 18, T 21, B 19
    CHECK(r[4] <= 16);   // measured 11
}

TEST_CASE("choir: silence gives no notes, one voice gives one section") {
    std::vector<float> z(kRate * 2, 0.f);
    CHECK(choir_notes(choir_frames(z.data(), z.size(), kRate)).empty());
    std::vector<float> y(size_t(2 * kRate), 0.f);
    for (size_t i = 0; i < y.size(); i++) {
        const double t = double(i) / kRate;
        for (int h = 1; h < 12; h++) y[i] += float(0.1 / h * std::sin(2 * kPi * h * 110 * t));   // A2: the bass
    }
    auto notes = choir_notes(choir_frames(y.data(), y.size(), kRate));
    int bass = 0, other = 0;
    for (auto& n : notes) (n.section == kBass && n.midi == 45 ? bass : other)++;
    CHECK(bass >= 1);
    CHECK(other == 0);
}

TEST_CASE("choir: the offline job writes the notes with their parts") {
    auto y = sing(kChorales[0], 7);
    ma_encoder enc;
    ma_encoder_config c = ma_encoder_config_init(ma_encoding_format_wav, ma_format_f32, 1, kRate);
    REQUIRE(ma_encoder_init_file("dz_choir.wav", &c, &enc) == MA_SUCCESS);
    ma_encoder_write_pcm_frames(&enc, y.data(), y.size(), nullptr);
    ma_encoder_uninit(&enc);
    SessionConfig s{};
    s.mode = AnalysisMode::VoiceMono;
    s.referenceA4 = 440;
    s.meter = {4, 4};
    s.bpm = 60;
    PostJob job;
    REQUIRE(job.run(s, 0.0, "dz_choir.wav", "dz_choir.json", ANA_POST_CHOIR) == ANA_OK);
    FILE* f = std::fopen("dz_choir.json", "r");
    REQUIRE(f);
    std::string text;
    for (int ch; (ch = std::fgetc(f)) != EOF;) text += char(ch);
    std::fclose(f);
    for (const char* part : {"\"part\": \"S\"", "\"part\": \"A\"", "\"part\": \"T\"", "\"part\": \"B\""}) CHECK(text.find(part) != std::string::npos);
    CHECK(text.find("\"name\": \"C5\"") != std::string::npos);   // the soprano's first note, as sounding
    std::remove("dz_choir.wav");
    std::remove("dz_choir.json");
}
