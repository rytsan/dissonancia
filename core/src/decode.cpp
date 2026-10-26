#include "decode.hpp"

#include <algorithm>
#include <cmath>
#include <cstring>

#include "chords.hpp"
#include "cqt.hpp"
#include "live_config.hpp"
#include "theory.hpp"
#include "voice.hpp"

namespace dz {

namespace {

struct Frame {
    double t;          // chroma timestamp, sample clock seconds
    float raw[12];
    int8_t bass;       // settled bass pitch class, -1 none
    bool silent;
    NoteFrame notes;   // perceived notes (NoteFrontEnd)
};

}  // namespace

std::vector<AnalyzerEvent> decode_chords(const SessionConfig& s, const float* x, size_t n, uint32_t rate, double comp,
                                         bool /*grid*/, const std::function<bool(double)>& progress, std::vector<HeardFrame>* heard) {
    // ponytail: the metronome grid of a REC take (changes on the beats) is not used by the notes HMM
    // yet; add it as a lower change cost on the beats if takes show changes off the beat.
    const LiveConfig c = live_config(s.mode, s.quality, rate);
    const uint32_t hop = uint32_t(std::lround(c.hopSeconds * rate));
    ChromaFrontEnd fe(s, c.decimation, rate, c.fMin, c.fMax, c.binsPerOctave, hop, c.bassMin, c.bassMax, c.windowSeconds);
    ChromaFrontEnd::Output o{};

    // Pass 1: frames and onsets.
    std::vector<Frame> frames;
    std::vector<double> onsets;
    KeyTracker key(c.hopSeconds);
    for (size_t pos = 0; pos + hop <= n; pos += hop) {
        fe.process(x + pos, hop, pos + hop, o);
        Frame f{};
        f.t = o.chroma.timestampSeconds;
        std::memcpy(f.raw, o.chroma.raw, sizeof f.raw);
        f.bass = o.bass.valid && o.bass.settled ? o.bass.pitchClass : int8_t(-1);
        f.silent = true;
        for (float v : f.raw) f.silent &= v <= 0;
        f.notes = o.notes;
        key.process(o.chroma.raw);
        frames.push_back(f);
        if (o.onset) onsets.push_back(o.lastOnset);
        if (progress && (pos / hop) % 64 == 0 && !progress(0.8 * double(pos) / double(n))) return {};
    }
    if (frames.empty()) return {};
    if (heard)
        for (const Frame& f : frames) heard->push_back({f.t - comp, f.notes});

    // Pass 2: the chord HMM over the perceived notes (as LIVE), decoded with the whole take: every
    // frame decided from the past and the future. Silence (no note heard for 0.6 s) separates.
    ChordMatcher matcher(s);
    const KeyEstimate k = key.estimate();
    int tonic = 0;
    bool minor = false;
    if (s.keySet) {
        minor = s.keyMode != KeyMode::Major;
        tonic = (7 * (s.keyFifths + 12) + (minor ? 9 : 0)) % 12;
    } else if (k.valid) tonic = k.tonic, minor = k.minor;
    std::vector<NoteFrame> notes(frames.size());
    for (size_t i = 0; i < frames.size(); i++) notes[i] = frames[i].notes;
    const std::vector<int> path = ChordHmm::decode(notes, c.hopSeconds, ChordHmm::played(), s.keySet || k.valid, tonic, minor);
    std::vector<bool> quiet(frames.size());
    // quiet: nothing heard yet, or nothing for 0.6 s
    double lastHeard = -1e9;
    for (size_t i = 0; i < frames.size(); i++) {
        const double t = double(i) * c.hopSeconds;
        if (frames[i].notes.audible) lastHeard = t;
        quiet[i] = !frames[i].notes.audible && t - lastHeard > 0.6;
    }

    // Segments of the path; within a chord, a bass that holds 0.3 s or more is its own segment
    // (the detail STUDIO shows: G then G/F# under the same G, Em7/B).
    struct Seg { size_t a, b; ChordCandidate c; };   // frames [a, b)
    std::vector<Seg> segs;
    const size_t minBass = size_t(std::lround(0.3 / c.hopSeconds));
    for (size_t i = 0; i < frames.size();) {
        size_t j = i;
        while (j < frames.size() && path[j] == path[i] && quiet[j] == quiet[i]) j++;
        if (!quiet[i]) {
            // runs of the settled bass that hold 0.3 s (shorter ones belong to the runs around them);
            // none: the bass heard most often
            std::vector<std::pair<size_t, int>> runs;   // (first frame, bass pitch class)
            int votes[12]{};
            for (size_t f = i; f < j;) {
                const int b0 = frames[f].bass;   // the bass tracker's settled bass (the lowest perceived note
                size_t g = f;                    // wanders in picking, when the treble outweighs the ringing bass)
                while (g < j && frames[g].bass == b0) g++;
                if (b0 >= 0) votes[b0] += int(g - f);
                if (b0 >= 0 && g - f >= minBass && (runs.empty() || runs.back().second != b0)) runs.emplace_back(f, b0);
                f = g;
            }
            if (runs.empty()) {
                const int most = int(std::max_element(votes, votes + 12) - votes);
                runs.emplace_back(i, votes[most] > 0 ? most : -1);
            }
            // the previous chord's bass still ringing at the start of this one is not its inversion
            if (runs.size() > 1 && !segs.empty() && segs.back().b == i && runs.front().second == (segs.back().c.hasBass ? segs.back().c.bassPitchClass : segs.back().c.rootPitchClass)) runs.erase(runs.begin());
            runs.front().first = i;
            for (size_t r = 0; r < runs.size(); r++) {
                Seg g{runs[r].first, r + 1 < runs.size() ? runs[r + 1].first : j, {}};
                matcher.describe(ChordHmm::root(path[i]), ChordHmm::quality(path[i]), runs[r].second, g.c, true);
                segs.push_back(g);
            }
        }
        i = j;
    }

    std::vector<AnalyzerEvent> out;
    ChordHistory history{};
    ChordCandidate previous{};
    double previousEnd = -1, lastChordStart = 0;
    int lastChord = -1;   // index in out of the latest chord event
    uint32_t seq = 0;
    for (size_t si = 0; si < segs.size(); si++) {
        const Seg& g = segs[si];
        ChordRecognitionResult r{};
        r.best = g.c;
        // Start: the attack that began the segment (the chroma lags the change by the CQT windows),
        // within 0.25 s before; the previous chord then ends there (no overlap, no gap).
        double start = frames[g.a].t, end = g.b < frames.size() ? frames[g.b].t : frames.back().t + c.hopSeconds;
        const double floor = out.empty() ? 0.0 : lastChordStart + 0.1;
        double snap = -1;   // the latest attack before the boundary (an earlier one is the previous chord's strum)
        for (double on : onsets)
            if (on <= start + 0.05 && on >= start - 0.25 && on > floor) snap = on;
        if (snap >= 0) start = snap;
        if (lastChord >= 0 && std::fabs(out[size_t(lastChord)].data.chord.endTimeSeconds + comp - start) <= 0.3) {   // later or a small gap
            ChordEvent& pc = out[size_t(lastChord)].data.chord;
            pc.endTimeSeconds = start - comp;
            pc.durationSeconds = pc.endTimeSeconds - pc.startTimeSeconds;
        }

        // Same chord as the previous segment (after relabelling, without a gap): extend it.
        if (lastChord >= 0 && std::strcmp(out[size_t(lastChord)].data.chord.symbol, r.best.symbol) == 0 &&
            std::fabs(out[size_t(lastChord)].data.chord.endTimeSeconds + comp - start) < 1e-6) {
            ChordEvent& pc = out[size_t(lastChord)].data.chord;
            pc.endTimeSeconds = end - comp;
            pc.durationSeconds = pc.endTimeSeconds - pc.startTimeSeconds;
            previousEnd = end;
            continue;
        }
        AnalyzerEvent ev{};
        ev.type = AnalyzerEventType::ChordEnded;
        ev.sequence = seq++;
        ChordEvent& ce = ev.data.chord;
        const ChordCandidate& b = r.best;
        ce.startTimeSeconds = start - comp;
        ce.endTimeSeconds = end - comp;
        ce.durationSeconds = end - start;
        std::memcpy(ce.symbol, b.symbol, sizeof ce.symbol);
        std::memcpy(ce.roman, b.roman, sizeof ce.roman);
        ce.diatonicStatus = b.diatonicStatus;
        ce.rootPitchClass = b.rootPitchClass;
        ce.bassPitchClass = b.hasBass ? b.bassPitchClass : int8_t(-1);
        ce.quality = b.quality;
        ce.inversion = 0;
        if (b.hasBass && b.bassPitchClass != b.rootPitchClass) {
            ce.inversion = -1;
            for (int i = 0; i < b.expectedCount; i++) if (b.expected[i] == b.bassPitchClass) ce.inversion = int8_t(i);
        }
        ce.detectedCount = b.detectedCount;
        ce.missingCount = b.missingCount;
        std::memcpy(ce.detectedNotes, b.detected, sizeof ce.detectedNotes);
        std::memcpy(ce.missingNotes, b.missing, sizeof ce.missingNotes);
        ce.confidence = b.confidence;
        ce.incomplete = b.incomplete;
        ce.bassSettled = b.hasBass;

        // Cadence into this chord (arrival), and a half / Phrygian one at a phrase end (silence after).
        const bool gapBefore = previousEnd >= 0 && start - previousEnd > 0.3;
        CadenceEvent cad{};
        lastChord = int(out.size());
        lastChordStart = start;
        out.push_back(ev);
        if (previous.symbol[0] && !gapBefore && matcher.cadence(previous, b, false, cad)) {
            cad.timestampSeconds = start - comp;
            AnalyzerEvent ce2{};
            ce2.type = AnalyzerEventType::Cadence;
            ce2.sequence = seq++;
            ce2.data.cadence = cad;
            out.push_back(ce2);
        }
        const bool phraseEnd = si + 1 == segs.size() || frames[segs[si + 1].a].t - end > 0.3;
        if (phraseEnd && previous.symbol[0] && matcher.cadence(previous, b, true, cad)) {
            cad.timestampSeconds = start - comp;
            AnalyzerEvent ce3{};
            ce3.type = AnalyzerEventType::Cadence;
            ce3.sequence = seq++;
            ce3.data.cadence = cad;
            out.push_back(ce3);
        }
        history = {b.rootPitchClass, b.quality, history.currentRoot, history.currentQuality};
        previous = phraseEnd ? ChordCandidate{} : b;
        previousEnd = end;
    }
    if (progress) progress(1.0);
    return out;
}

namespace {
constexpr int kLowMidi = 28, kHighMidi = 100, kNotes = kHighMidi - kLowMidi + 1, kUnvoiced = kNotes;
// Cost of a note change in summed log-likelihood units; sigma of a held note's pitch (vibrato and
// intonation); the floor that keeps one stray frame (octave error, glide) from forcing a change.
constexpr float kNoteChange = 6.0f, kSigma = 0.45f, kFloor = -3.0f;
// Voiced <-> unvoiced change cost and the shortest note kept: swept 1 / 2.5 / 4 and 50 / 80 / 100 ms
// on the band-mix melody alone and over the drums (F 50 / 56 / 60 / 54 %; clean voice 100 % in all).
constexpr float kVoicing = 2.5f;
constexpr double kMinNote = 0.08;
}  // namespace

std::vector<AnalyzerEvent> decode_notes(const SessionConfig& s, const float* x, size_t n, uint32_t rate, double comp,
                                        const std::function<bool(double)>& progress, std::vector<PitchPoint>* track) {
    const LiveConfig c = live_config(s.mode, s.quality, rate);
    const uint32_t hop = uint32_t(std::lround(c.hopSeconds * rate));
    VoicePipeline vp(s, c, rate, hop, comp);
    VoiceOutput o{};
    struct P { double t; float midi, clarity, hz, rms; bool voiced; };
    std::vector<P> frames;
    std::vector<double> starts;   // the live pipeline's note starts: sample-accurate onsets / legato midpoints
    for (size_t pos = 0; pos + hop <= n; pos += hop) {
        vp.process(x + pos, hop, pos + hop, o);
        frames.push_back({o.pitch.timestampSeconds, o.pitch.midiFloat, o.pitch.clarity, o.pitch.frequencyHz, o.pitch.rms, o.pitch.voiced != 0});
        for (uint32_t k = 0; k < o.eventCount; k++)
            if (o.events[k].type == AnalyzerEventType::NoteStart) starts.push_back(o.events[k].data.note.startTimeSeconds);
        if (progress && (pos / hop) % 256 == 0 && !progress(0.8 * double(pos) / double(n))) return {};
    }
    if (frames.empty()) return {};
    if (track)
        for (const P& f : frames) track->push_back({f.t, f.voiced ? f.midi : 0.f});

    // Viterbi: emission = -(d / sigma)^2 / 2, floored, times the frame's clarity; unvoiced frames
    // belong to the unvoiced state, voiced ones cost it a constant.
    std::vector<uint8_t> back(frames.size() * (kNotes + 1));
    std::vector<float> prev(kNotes + 1, 0.f), cur(kNotes + 1), e(kNotes + 1);
    for (size_t i = 0; i < frames.size(); i++) {
        const P& f = frames[i];
        for (int k = 0; k < kNotes; k++) {
            if (!f.voiced) { e[size_t(k)] = -2.f; continue; }
            float d = (f.midi - float(kLowMidi + k)) / kSigma;
            e[size_t(k)] = std::max(kFloor, -0.5f * d * d) * std::max(0.2f, f.clarity);
        }
        e[kUnvoiced] = f.voiced ? -2.f : 0.f;
        int best = 0;
        for (int k = 1; k <= kNotes; k++) if (prev[size_t(k)] > prev[size_t(best)]) best = k;
        for (int k = 0; k <= kNotes; k++) {
            // Voice <-> silence changes are cheap (a phrase ends when the sound does); pitch changes cost.
            const float cost = (k == kUnvoiced || best == kUnvoiced) ? kVoicing : kNoteChange;
            const float stay = prev[size_t(k)], move = prev[size_t(best)] - cost;
            const bool keep = i == 0 || stay >= move;
            cur[size_t(k)] = e[size_t(k)] + (i == 0 ? 0 : keep ? stay : move);
            back[i * (kNotes + 1) + size_t(k)] = uint8_t(keep ? k : best);
        }
        std::swap(prev, cur);
    }
    std::vector<int> path(frames.size());
    int st = int(std::max_element(prev.begin(), prev.end()) - prev.begin());
    for (size_t i = frames.size(); i-- > 0;) { path[i] = st; st = back[i * (kNotes + 1) + size_t(st)]; }

    Speller speller;
    speller.configure(s.keyFifths, s.keyMode);
    const int shift = clef_octave_shift(s.clef);
    std::vector<AnalyzerEvent> out;
    int previousMidi = -1;
    double lastEnd = -1;
    uint32_t seq = 0;
    for (size_t i = 0; i < frames.size();) {
        size_t j = i;
        while (j < frames.size() && path[j] == path[i]) j++;
        if (path[i] != kUnvoiced) {
            const int midi = kLowMidi + path[i];
            double sumHz = 0, sumC = 0, sumC2 = 0, sumConf = 0;
            int cnt = 0;
            for (size_t k = i; k < j; k++) {
                const P& f = frames[k];
                if (!f.voiced || std::fabs(f.midi - float(midi)) > 0.5f) continue;   // glide / stray frames left out of the averages
                const double cents = 100.0 * (f.midi - midi);
                sumHz += f.hz; sumC += cents; sumC2 += cents * cents; sumConf += f.clarity; cnt++;
            }
            double start = frames[i].t, end = j < frames.size() ? frames[j].t : frames.back().t + c.hopSeconds;
            // The same pitch after a short gap with no dip in level (a stray octave frame or a glitch
            // read unvoiced, not a re-articulation) is the same note: a sung repeat has a consonant.
            bool sustained = false;
            if (cnt >= 2 && !out.empty() && out.back().data.note.midi == midi && start - out.back().data.note.endTimeSeconds < 0.15) {
                float gapMin = 1e9f, noteRms = 0;
                int nr = 0;
                for (size_t k = 0; k < i; k++) {
                    if (frames[k].t >= out.back().data.note.endTimeSeconds - 1e-9) gapMin = std::min(gapMin, frames[k].rms);
                    else if (frames[k].t >= out.back().data.note.startTimeSeconds) { noteRms += frames[k].rms; nr++; }
                }
                sustained = nr > 0 && gapMin >= 0.6f * noteRms / float(nr);
            }
            if (sustained) {
                MusicalNoteEvent& pm = out.back().data.note;
                pm.endTimeSeconds = end;
                pm.durationSeconds = end - pm.startTimeSeconds;
                lastEnd = end;
            } else if (cnt >= 2 && end - start >= kMinNote) {
                double bestD = 0.06;   // snap to the nearest live onset (the decoder places a boundary on a window centre)
                for (double on : starts)
                    if (std::fabs(on - start) < bestD) { bestD = std::fabs(on - start); start = std::max(on, lastEnd); }
                if (!out.empty() && start - lastEnd < 0.03 && start > lastEnd) {   // legato: the previous note ends here
                    MusicalNoteEvent& pm = out.back().data.note;
                    pm.endTimeSeconds = start;
                    pm.durationSeconds = pm.endTimeSeconds - pm.startTimeSeconds;
                }
                if (lastEnd > 0 && start - lastEnd > 0.15) speller.reset_phrase();
                const Spelled sp = speller.spell(midi, previousMidi);
                AnalyzerEvent ev{};
                ev.type = AnalyzerEventType::NoteEnd;
                ev.sequence = seq++;
                MusicalNoteEvent& m = ev.data.note;
                m.startTimeSeconds = start;   // pitch timestamps are window centres, already compensated
                m.endTimeSeconds = end;
                m.durationSeconds = end - start;
                m.midi = int8_t(midi);
                Speller::name(sp, shift, m.writtenName);
                m.avgHz = float(sumHz / cnt);
                m.medianHz = m.avgHz;
                m.avgCents = float(sumC / cnt);
                m.confidence = float(sumConf / cnt);
                m.chromatic = !sp.diatonic;
                m.vibrato = sumC2 / cnt - (sumC / cnt) * (sumC / cnt) > 144.0;
                out.push_back(ev);
                previousMidi = midi;
                lastEnd = end;
                if (!out.empty() && out.size() >= 2) {
                    MusicalNoteEvent& pm = out[out.size() - 2].data.note;   // no overlap with the note before
                    if (pm.endTimeSeconds > m.startTimeSeconds) { pm.endTimeSeconds = m.startTimeSeconds; pm.durationSeconds = pm.endTimeSeconds - pm.startTimeSeconds; }
                }
            }
        }
        i = j;
    }
    if (progress) progress(1.0);
    return out;
}

}  // namespace dz
