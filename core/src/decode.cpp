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

constexpr int kStates = ChordMatcher::kTemplates + 1, kSilence = ChordMatcher::kTemplates;
// Cost of a chord change in summed score units (a new chord must out-score the current one by
// this much over its frames, ~0.1 per frame for 2 s). Swept 2 / 4 / 6 / 10 / 16 on the band mix
// (take labels 84 / 92 / 98 / 99 / 99 %); 16 starts to merge real changes on the validation
// progressions (jazz 93 %), 10 keeps them all at 100 %.
constexpr float kChangePenalty = 10.0f;

struct Frame {
    double t;          // chroma timestamp, sample clock seconds
    float raw[12];
    int8_t bass;       // settled bass pitch class, -1 none
    bool silent;
};

}  // namespace

std::vector<AnalyzerEvent> decode_chords(const SessionConfig& s, const float* x, size_t n, uint32_t rate, double comp,
                                         bool grid, const std::function<bool(double)>& progress) {
    const LiveConfig c = live_config(s.mode, s.quality, rate);
    const uint32_t hop = uint32_t(std::lround(c.hopSeconds * rate));
    ChromaFrontEnd fe(s, c.decimation, rate, c.fMin, c.fMax, c.binsPerOctave, hop, c.bassMin, c.bassMax, c.windowSeconds);
    ChromaFrontEnd::Output o{};

    // Pass 1: frames and onsets.
    std::vector<Frame> frames;
    std::vector<double> onsets;
    for (size_t pos = 0; pos + hop <= n; pos += hop) {
        fe.process(x + pos, hop, pos + hop, o);
        Frame f{};
        f.t = o.chroma.timestampSeconds;
        std::memcpy(f.raw, o.chroma.raw, sizeof f.raw);
        f.bass = o.bass.valid && o.bass.settled ? o.bass.pitchClass : int8_t(-1);
        f.silent = true;
        for (float v : f.raw) f.silent &= v <= 0;
        frames.push_back(f);
        if (o.onset) onsets.push_back(o.lastOnset);
        if (progress && (pos / hop) % 64 == 0 && !progress(0.8 * double(pos) / double(n))) return {};
    }
    if (frames.empty()) return {};

    // Pass 2: Viterbi. score(s, t) + (stay ? 0 : -penalty); silence is its own state.
    ChordMatcher matcher(s);
    const float penalty = kChangePenalty;
    // Metronome grid: a change shows in the chroma from the attack to ~120 ms after it (the low CQT
    // bins refill), so a frame counts as "on" a beat from 60 ms before to 120 ms after it. Half the
    // cost on a downbeat, the full cost on the other beats (0.75 split chords on beat 2), 1.2 off
    // the grid; 0.4-0.6 / 0.9-1.0 / 1.0-1.4 measured the same on the band mix and validation.
    const double beat = 60.0 / std::max(20.f, s.bpm);
    const int perBar = std::max<int>(1, s.meter.numerator);
    auto grid_factor = [&](double t) {
        const double b = t / beat;
        const long nearest = std::lround(b - 0.03 / beat);
        const double d = t - nearest * beat;
        if (d < -0.06 || d > 0.12) return 1.2f;
        return nearest % perBar == 0 ? 0.5f : 1.0f;
    };
    std::vector<uint8_t> back(frames.size() * kStates);
    std::vector<float> prev(kStates, 0.f), cur(kStates), e(kStates);
    for (size_t i = 0; i < frames.size(); i++) {
        const Frame& f = frames[i];
        matcher.score_all(f.raw, f.bass, e.data());
        for (int k = 0; k < ChordMatcher::kTemplates; k++) e[size_t(k)] = f.silent ? 0.f : e[size_t(k)] <= ChordMatcher::kRuledOut ? -2.f : e[size_t(k)];
        e[kSilence] = f.silent ? 1.f : 0.3f;
        int best = 0;
        for (int k = 1; k < kStates; k++) if (prev[size_t(k)] > prev[size_t(best)]) best = k;
        const float cost = penalty * (grid ? grid_factor(f.t - comp) : 1.f);
        for (int k = 0; k < kStates; k++) {
            const float stay = prev[size_t(k)], move = prev[size_t(best)] - cost;
            const bool keep = i == 0 || stay >= move;
            cur[size_t(k)] = e[size_t(k)] + (i == 0 ? 0 : keep ? stay : move);
            back[i * kStates + size_t(k)] = uint8_t(keep ? k : best);
        }
        std::swap(prev, cur);
    }
    std::vector<int> path(frames.size());
    int st = int(std::max_element(prev.begin(), prev.end()) - prev.begin());
    for (size_t i = frames.size(); i-- > 0;) {
        path[i] = st;
        st = back[i * kStates + size_t(st)];
    }

    // Segments of the path, labelled on their own frames.
    struct Seg { size_t a, b; };   // frames [a, b)
    std::vector<Seg> segs;
    for (size_t i = 0; i < frames.size();) {
        size_t j = i;
        while (j < frames.size() && path[j] == path[i]) j++;
        if (path[i] != kSilence) segs.push_back({i, j});
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
        // Persistence-weighted mean (normalised frames): chord tones ring through the segment.
        float mean[12]{}, present[12]{};
        int bassVotes[12]{}, voiced = 0;
        for (size_t i = g.a; i < g.b; i++) {
            const Frame& f = frames[i];
            float mx = 0;
            for (float v : f.raw) mx = std::max(mx, v);
            if (mx <= 0) continue;
            for (int k = 0; k < 12; k++) { mean[k] += f.raw[k] / mx; present[k] += f.raw[k] >= 0.09f * mx; }
            if (f.bass >= 0) bassVotes[f.bass]++;
            voiced++;
        }
        if (voiced < 3) continue;
        for (int k = 0; k < 12; k++) { float share = present[k] / float(voiced); mean[k] = mean[k] / float(voiced) * share * share; }
        int bass = int(std::max_element(bassVotes, bassVotes + 12) - bassVotes);
        if (bassVotes[bass] < voiced * 3 / 10) bass = -1;   // no bass that held for 30 % of the chord
        ChordRecognitionResult r;
        matcher.match(mean, history, bass, r);
        if (!r.best.symbol[0]) continue;

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
                                        const std::function<bool(double)>& progress) {
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
