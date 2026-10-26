#include "session.hpp"

#include <algorithm>
#include <cmath>
#include <string>

#include "beats.hpp"
#include "dynamics.hpp"
#include "intonation.hpp"
#include "live_config.hpp"
#include "timing.hpp"

namespace dz {

namespace {
// Writes ",\n    \"intonation\": {...}" (R2.4) for a voice or a single line; returns its confidence.
double write_intonation_json(FILE* f, const float* x, size_t n, uint32_t rate, const SessionConfig& s, double comp) {
    std::vector<PitchPoint> track;
    const IntonationReport r = intonation_report(decode_notes(s, x, n, rate, comp, {}, &track), track);
    std::fprintf(f, ",\n    \"intonation\": {\"referenceA4\": %.2f, \"confidence\": %.2f, \"notes\": %zu", s.referenceA4, r.confidence, r.notes.size());
    if (r.notes.empty()) {
        std::fputs(", \"evidence\": \"no note held long enough to read\"}", f);
        return 0;
    }
    std::fprintf(f, ", \"tuningCents\": %.1f, \"inTunePct\": %.1f, \"inTuneOwnTuningPct\": %.1f, \"meanAbsCents\": %.1f, \"scoops\": %d,"
                    " \"vibratoNotes\": %d,\n      \"phrases\": [",
                 r.tuningCents, r.inTunePct, r.inTuneOwnPct, r.meanAbsCents, r.scoops, r.vibratoNotes);
    for (size_t i = 0; i < r.phrases.size(); i++)
        std::fprintf(f, "%s{\"start\": %.3f, \"end\": %.3f, \"notes\": %d, \"driftCents\": %.1f}", i ? ", " : "", r.phrases[i].start,
                     r.phrases[i].end, r.phrases[i].notes, r.phrases[i].driftCents);
    // Per bar of the metronome (from the first downbeat), by each note's start.
    std::fputs("],\n      \"bars\": [", f);
    const double bar = s.bpm > 0 && s.meter.numerator ? 60.0 / s.bpm * s.meter.numerator : 0;
    if (bar > 0) {
        struct Acc { int notes = 0, in = 0; double abs = 0; };
        std::vector<Acc> bars;
        for (const NoteIntonation& m : r.notes) {
            if (m.start < 0) continue;
            const size_t b = size_t(m.start / bar);
            if (b >= bars.size()) bars.resize(b + 1);
            const double d = std::fabs(m.cents - r.tuningCents);
            bars[b].notes++, bars[b].in += d <= 20, bars[b].abs += d;
        }
        bool first = true;
        for (size_t b = 0; b < bars.size(); b++) {
            if (!bars[b].notes) continue;
            std::fprintf(f, "%s{\"bar\": %zu, \"notes\": %d, \"inTuneOwnTuningPct\": %.1f, \"meanAbsCents\": %.1f}", first ? "" : ", ", b + 1,
                         bars[b].notes, 100.0 * bars[b].in / bars[b].notes, bars[b].abs / bars[b].notes);
            first = false;
        }
    }
    std::fputs("],\n      \"notesRead\": [", f);
    for (size_t i = 0; i < r.notes.size(); i++) {
        const NoteIntonation& m = r.notes[i];
        std::fprintf(f, "%s\n        {\"start\": %.3f, \"end\": %.3f, \"midi\": %d, \"cents\": %.1f, \"scoopCents\": %.1f, \"driftCents\": %.1f,"
                        " \"vibratoHz\": %.2f, \"vibratoCents\": %.1f}",
                     i ? "," : "", m.start, m.end, m.midi, m.cents, m.scoopCents, m.driftCents, m.vibratoHz, m.vibratoCents);
    }
    std::fputs("],\n      \"evidence\": \"YIN pitch track, notes by the whole-take decoder; cents are each note's steady median against equal"
               " temperament at referenceA4 (tuningCents: their median, the tuning heard); in tune within 20 cents\"}", f);
    return r.confidence;
}
}  // namespace

void write_take_metrics_json(FILE* f, const float* x, size_t n, uint32_t rate, const SessionConfig& s, bool clickHeard, double comp,
                             uint64_t droppedFrames) {
    const double bpm = s.bpm;
    const int beatsPerBar = s.meter.numerator;
    auto num = [&](double v, const char* fmt = "%.1f") {   // null where there is nothing to say
        if (std::isfinite(v)) std::fprintf(f, fmt, v);
        else std::fputs("null", f);
    };
    std::vector<Onset> on = detect_onsets(x, n, rate);
    for (Onset& o : on) o.time -= comp;
    // The player's own beats give the tempo (R2.2) either way; the grid for the offsets is the click
    // when it was heard.
    BeatTrack heard = track_beats(x, n, rate, bpm);
    for (double& t : heard.beats) t -= comp;
    std::vector<double> grid;
    if (clickHeard && bpm > 0)
        for (double t = 0; t < double(n) / rate + 60 / bpm; t += 60 / bpm) grid.push_back(t);
    else grid = heard.beats;
    const TimingReport r = timing_report(on, grid, beatsPerBar), tempo = timing_report(on, heard.beats);
    const double nan = std::nan("");

    std::fprintf(f, "  \"metrics\": {\n    \"timing\": {\"grid\": \"%s\", \"confidence\": %.2f, \"onsets\": %d, \"matched\": %d, \"meanOffsetMs\": ",
                 clickHeard ? "metronome" : "beats heard", r.confidence, r.onsets, r.matched);
    num(r.matched ? r.meanOffsetMs : nan);
    std::fputs(", \"spreadMs\": ", f);
    num(r.matched ? r.spreadMs : nan);
    std::fputs(", \"swing\": ", f);
    num(r.swing > 0 ? r.swing : nan, "%.2f");
    std::fputs(",\n      \"perSixteenth\": [", f);
    const char* names[4] = {"1", "e", "&", "a"};
    for (int q = 0, first = 1; q < 4; q++) {
        if (!r.count[q]) continue;
        std::fprintf(f, "%s{\"position\": \"%s\", \"count\": %d, \"offsetMs\": %.1f, \"accentDb\": %.1f}", first ? "" : ", ", names[q],
                     r.count[q], r.offsetMs[q], r.accentDb[q]);
        first = 0;
    }
    std::fputs("],\n      \"accentPerBeatDb\": [", f);
    for (size_t b = 0; b < r.beatAccentDb.size(); b++) {
        if (b) std::fputs(", ", f);
        num(r.beatAccentDb[b]);
    }
    std::fputs("],\n      \"bars\": [", f);
    for (size_t b = 0; b < r.bars.size(); b++) {
        std::fprintf(f, "%s{\"bar\": %zu, \"matched\": %d, \"meanOffsetMs\": ", b ? ", " : "", b + 1, r.bars[b].matched);
        num(r.bars[b].matched ? r.bars[b].meanOffsetMs : nan);
        std::fputs(", \"spreadMs\": ", f);
        num(r.bars[b].matched ? r.bars[b].spreadMs : nan);
        std::fputs("}", f);
    }
    std::fprintf(f, "],\n      \"evidence\": \"attacks by spectral flux, %.1f ms of round-trip latency taken off, set to the nearest sixteenth of %s\"},\n",
                 comp * 1000, clickHeard ? "the metronome's beats (heard during the take)" : "the beats heard in the take (the click was off)");
    std::fputs("    \"tempo\": {\"bpm\": ", f);
    num(heard.bpm > 0 ? tempo.bpm : nan, "%.2f");
    std::fprintf(f, ", \"metronomeBpm\": %.2f, \"driftBpm\": ", bpm);
    num(heard.beats.size() > 2 ? tempo.driftBpm : nan, "%.2f");
    std::fputs(", \"spreadBpm\": ", f);
    num(heard.beats.size() > 2 ? tempo.tempoSpreadBpm : nan, "%.2f");
    std::fprintf(f, ", \"beats\": %zu, \"evidence\": \"beats tracked in the take (onset envelope, dynamic programming), seeded at the metronome's tempo\"}",
                 heard.beats.size());
    double intonationConfidence = 1;
    if (!is_chord_mode(s.mode)) intonationConfidence = write_intonation_json(f, x, n, rate, s, comp);
    // R2.7: loudness over the take, per bar and phrase; the attacks' consistency per sixteenth.
    const DynamicsReport d = dynamics_report(x, n, rate, bpm > 0 ? 60 / bpm * beatsPerBar : 0);
    std::fputs(",\n    \"dynamics\": {\"integratedLufs\": ", f);
    num(d.integratedLufs > -70 ? d.integratedLufs : nan);
    std::fprintf(f, ", \"rangeLu\": %.1f, \"trendLu\": %.1f, \"attackSpreadDb\": ", d.rangeLu, d.trendLu);
    num(r.matched >= 8 ? r.levelSpreadAllDb : nan);
    std::fputs(",\n      \"perSixteenth\": [", f);
    for (int q = 0, first = 1; q < 4; q++) {
        if (r.count[q] < 2) continue;
        std::fprintf(f, "%s{\"position\": \"%s\", \"count\": %d, \"spreadDb\": %.1f}", first ? "" : ", ", names[q], r.count[q], r.levelSpreadDb[q]);
        first = 0;
    }
    std::fputs("],\n      \"bars\": [", f);
    for (size_t b = 0; b < d.barLufs.size(); b++) {
        std::fprintf(f, "%s{\"bar\": %zu, \"lufs\": ", b ? ", " : "", b + 1);
        num(d.barLufs[b] > -70 ? d.barLufs[b] : nan);
        std::fputs("}", f);
    }
    std::fputs("],\n      \"phrases\": [", f);
    for (size_t i = 0; i < d.phrases.size(); i++)
        std::fprintf(f, "%s{\"start\": %.1f, \"end\": %.1f, \"lufs\": %.1f, \"changeLu\": %.1f, \"peakAt\": %.2f}", i ? ", " : "", d.phrases[i].start,
                     d.phrases[i].end, d.phrases[i].lufs, d.phrases[i].changeLu, d.phrases[i].peakAt);
    std::fputs("],\n      \"evidence\": \"K-weighted loudness (ITU-R BS.1770, EBU R128) of the take, its bars and its phrases (sound with gaps under"
               " 0.4 s); attackSpreadDb: the attacks' level spread within each sixteenth, the accent pattern left out\"}", f);
    std::fputs("\n  },\n", f);

    // Known limits of the take (R3.1): level, clipping, lost frames, and what they make unreliable.
    double peak = 0, energy = 0;
    size_t clipped = 0;
    for (size_t i = 0; i < n; i++) {
        const double v = std::abs(double(x[i]));
        peak = std::max(peak, v), energy += v * v, clipped += v >= 0.999;
    }
    const double peakDb = 20 * std::log10(peak + 1e-12), rmsDb = 10 * std::log10(energy / double(std::max<size_t>(n, 1)) + 1e-12);
    std::vector<std::string> warn;
    if (clipped) warn.push_back("clipping: " + std::to_string(clipped) + " samples at full scale");
    if (rmsDb < -50) warn.push_back("very quiet input: attacks may be missed");
    if (droppedFrames) warn.push_back("frames dropped by the recorder: times after the gap may be off");
    if (r.confidence < 0.5) warn.push_back("timing: few attacks on the grid, low confidence");
    if (intonationConfidence < 0.5) warn.push_back("intonation: few notes read, low confidence");
    std::fputs("  \"limits\": {\"peakDbfs\": ", f);
    num(peakDb);
    std::fputs(", \"rmsDbfs\": ", f);
    num(rmsDb);
    std::fprintf(f, ", \"clippedSamples\": %zu, \"droppedFrames\": %llu, \"warnings\": [", clipped, (unsigned long long)droppedFrames);
    for (size_t i = 0; i < warn.size(); i++) std::fprintf(f, "%s\"%s\"", i ? ", " : "", warn[i].c_str());
    std::fputs("]}\n", f);
}

}  // namespace dz
