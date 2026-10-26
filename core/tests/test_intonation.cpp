#include <catch2/catch_test_macros.hpp>
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <string>
#include <vector>

#include "intonation.hpp"
#include "session.hpp"

using namespace dz;

namespace {
// A sung line: each note a harmonic tone whose pitch follows cents(t) around its MIDI note, apart.
struct Sung { int midi; double sec; double (*cents)(double t); };
std::vector<float> sing(uint32_t rate, const std::vector<Sung>& notes) {
    std::vector<float> x(size_t(0.3 * rate), 0.f);
    for (const Sung& n : notes) {
        double ph = 0;
        for (size_t i = 0; i < size_t(n.sec * rate); i++) {
            const double t = double(i) / rate, hz = 440 * std::pow(2.0, (n.midi - 69 + n.cents(t) / 100) / 12);
            ph += 2 * M_PI * hz / rate;
            const double env = std::min(1.0, std::min(t, n.sec - t) / 0.01);
            x.push_back(float(env * 0.2 * (std::sin(ph) + 0.5 * std::sin(2 * ph) + 0.25 * std::sin(3 * ph))));
        }
        x.insert(x.end(), size_t(0.25 * rate), 0.f);
    }
    return x;
}
SessionConfig voice() {
    SessionConfig s{};
    s.mode = AnalysisMode::VoiceMono;
    s.quality = AudioQuality::Balanced;
    s.referenceA4 = 440;
    s.meter = {4, 4};
    s.bpm = 120;
    return s;
}
}  // namespace

TEST_CASE("intonation: cents, a scoop, a vibrato and a drift read per note") {
    const uint32_t rate = 44100;
    auto x = sing(rate, {
        {69, 0.8, [](double) { return 12.0; }},                                          // A4 12 cents sharp
        {67, 0.8, [](double t) { return t < 0.1 ? -150 + 1500 * t : 0.0; }},             // G4 scooped from 150 cents under
        {64, 1.2, [](double t) { return 40 * std::sin(2 * M_PI * 5.5 * t); }},           // E4, 5.5 Hz vibrato +-40 cents
        {65, 1.2, [](double t) { return -40 * t; }},                                     // F4 sagging 48 cents
        {62, 0.8, [](double) { return -30.0; }},                                         // D4 30 cents flat
    });
    std::vector<PitchPoint> track;
    const IntonationReport r = intonation_report(decode_notes(voice(), x.data(), x.size(), rate, 0.0, {}, &track), track);
    REQUIRE(r.notes.size() == 5);
    for (const NoteIntonation& n : r.notes)
        INFO(n.midi << " cents " << n.cents << " scoop " << n.scoopCents << " drift " << n.driftCents << " vibrato " << n.vibratoHz << " " << n.vibratoCents);
    CHECK(std::abs(r.notes[0].cents - 12) < 3);
    CHECK(std::abs(r.notes[0].scoopCents) < 15);
    CHECK(r.notes[0].vibratoHz == 0);
    CHECK(r.notes[1].scoopCents < -30);
    CHECK(std::abs(r.notes[1].cents) < 3);
    CHECK(std::abs(r.notes[2].vibratoHz - 5.5) < 0.5);
    CHECK(std::abs(r.notes[2].vibratoCents - 40) < 8);
    CHECK(r.notes[3].driftCents < -20);
    CHECK(r.notes[3].vibratoHz == 0);
    CHECK(std::abs(r.notes[4].cents + 30) < 3);
    CHECK(r.scoops == 1);
    CHECK(r.vibratoNotes == 1);
    CHECK(r.inTunePct < 81);   // the flat D4 and the sagging F4 are not all in tune
    REQUIRE(r.phrases.size() == 1);
}

TEST_CASE("intonation: a voice take's session JSON carries it") {
    auto x = sing(44100, {{69, 0.6, [](double) { return 0.0; }}, {71, 0.6, [](double) { return 0.0; }}, {72, 0.6, [](double) { return -25.0; }}});
    FILE* f = std::tmpfile();
    REQUIRE(f);
    write_take_metrics_json(f, x.data(), x.size(), 44100, voice(), false, 0, 0);
    std::string s(size_t(std::ftell(f)), '\0');
    std::rewind(f);
    REQUIRE(std::fread(s.data(), 1, s.size(), f) == s.size());
    std::fclose(f);
    INFO(s);
    CHECK(std::count(s.begin(), s.end(), '{') == std::count(s.begin(), s.end(), '}'));
    CHECK(s.find("\"intonation\": {\"referenceA4\": 440.00") != std::string::npos);
    CHECK(s.find("\"midi\": 72, \"cents\": -2") != std::string::npos);   // -25 cents, read within 5
    CHECK(s.find("\"notesRead\"") != std::string::npos);
    CHECK(s.find("intonation: few notes read") != std::string::npos);   // three notes: low confidence
}
