// Offline chord run over a WAV file (16-bit PCM or float32, any channel count, mixed to mono):
// the same front end and tracker as the live engine, hop by hop, without a device. Prints the
// confirmed chords and how often the live preview would change on screen.
//   dz_wav file.wav [guitar|piano|general|voice|choir] [low|balanced|high] [offline [bpm beatsPerBar]]
// choir: the STUDIO choir by section (S9), one line per note: part start end midi confidence.
// offline: the STUDIO whole-take decoder instead of the live tracker; with a bpm, the file is taken
// to start on a downbeat of that metronome grid (a REC take).
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

#include "choir.hpp"
#include "chords.hpp"
#include "decode.hpp"
#include "voice.hpp"
#include "cqt.hpp"
#include "live_config.hpp"

using namespace dz;

static bool read_wav(const char* path, std::vector<float>& x, uint32_t& rate) {
    FILE* f = std::fopen(path, "rb");
    if (!f) return false;
    std::vector<unsigned char> b;
    unsigned char buf[1 << 16];
    for (size_t k; (k = std::fread(buf, 1, sizeof buf, f)) > 0;) b.insert(b.end(), buf, buf + k);
    std::fclose(f);
    uint16_t fmt = 1, ch = 1, bits = 16;
    for (size_t p = 12; p + 8 <= b.size();) {
        uint32_t sz;
        std::memcpy(&sz, &b[p + 4], 4);
        if (!std::memcmp(&b[p], "fmt ", 4)) {
            std::memcpy(&fmt, &b[p + 8], 2); std::memcpy(&ch, &b[p + 10], 2);
            std::memcpy(&rate, &b[p + 12], 4); std::memcpy(&bits, &b[p + 22], 2);
        }
        if (!std::memcmp(&b[p], "data", 4)) {
            sz = uint32_t(std::min<size_t>(sz, b.size() - p - 8));
            const size_t frames = sz / (bits / 8) / ch;
            x.assign(frames, 0.f);
            for (size_t i = 0; i < frames; i++)
                for (int c = 0; c < ch; c++) {
                    const unsigned char* s = &b[p + 8 + (i * ch + size_t(c)) * (bits / 8)];
                    float v;
                    if (fmt == 3 || bits == 32) std::memcpy(&v, s, 4);
                    else { int16_t i16; std::memcpy(&i16, s, 2); v = i16 / 32768.f; }
                    x[i] += v / ch;
                }
            return true;
        }
        p += 8 + sz + (sz & 1);
    }
    return false;
}

int main(int argc, char** argv) {
    if (argc < 2) { std::fprintf(stderr, "usage: dz_wav file.wav [guitar|piano|general] [low|balanced|high]\n"); return 2; }
    std::vector<float> x;
    uint32_t rate = 48000;
    if (!read_wav(argv[1], x, rate)) { std::fprintf(stderr, "cannot read %s\n", argv[1]); return 1; }
    const std::string mode = argc > 2 ? argv[2] : "guitar", q = argc > 3 ? argv[3] : "balanced";
    if (mode == "choir") {
        for (const ChoirNote& n : choir_notes(choir_frames(x.data(), x.size(), rate)))
            std::printf("%s %.3f %.3f %d %.2f%s\n", kChoirPart[n.section], n.start, n.end, n.midi, n.confidence, n.ambiguous ? " ambiguous" : "");
        return 0;
    }
    SessionConfig s{};
    s.mode = mode == "piano" ? AnalysisMode::PianoChords : mode == "general" ? AnalysisMode::GeneralChords : mode == "voice" ? AnalysisMode::VoiceMono : AnalysisMode::GuitarChords;
    s.quality = q == "low" ? AudioQuality::LowLatency : q == "high" ? AudioQuality::HighPrecision : AudioQuality::Balanced;
    s.referenceA4 = 440;
    s.keySet = 1;
    s.meter = {4, 4};
    s.bpm = 120;
    if (s.mode == AnalysisMode::VoiceMono) {   // notes: the live pipeline, or the whole-take decoder
        const bool offline = argc > 4 && std::string(argv[4]) == "offline";
        std::vector<AnalyzerEvent> notes;
        if (offline) notes = decode_notes(s, x.data(), x.size(), rate, 0.0);
        else {
            const LiveConfig lc = live_config(s.mode, s.quality, rate);
            const uint32_t h = uint32_t(std::lround(lc.hopSeconds * rate));
            VoicePipeline vp(s, lc, rate, h, 0.0);
            VoiceOutput vo{};
            for (size_t p = 0; p + h <= x.size(); p += h) {
                vp.process(x.data() + p, h, p + h, vo);
                for (uint32_t i = 0; i < vo.eventCount; i++) if (vo.events[i].type == AnalyzerEventType::NoteEnd) notes.push_back(vo.events[i]);
            }
            vp.flush(vo);
            for (uint32_t i = 0; i < vo.eventCount; i++) if (vo.events[i].type == AnalyzerEventType::NoteEnd) notes.push_back(vo.events[i]);
        }
        for (const AnalyzerEvent& e : notes)
            std::printf("note %8.3f %8.3f %d %s\n", e.data.note.startTimeSeconds, e.data.note.endTimeSeconds, e.data.note.midi, e.data.note.writtenName);
        std::printf("%s: %zu notes\n", offline ? "offline" : "live", notes.size());
        return 0;
    }
    if (argc > 4 && std::string(argv[4]) == "offline") {
        int chords = 0;
        const bool grid = argc > 5;
        if (grid) { s.bpm = float(std::atof(argv[5])); s.meter.numerator = uint8_t(argc > 6 ? std::atoi(argv[6]) : 4); }
        for (const AnalyzerEvent& e : decode_chords(s, x.data(), x.size(), rate, 0.0, grid)) {
            if (e.type != AnalyzerEventType::ChordEnded) continue;
            std::printf("ended %8.3f %8.3f %s\n", e.data.chord.startTimeSeconds, e.data.chord.endTimeSeconds, e.data.chord.symbol);
            chords++;
        }
        std::printf("offline: %d chords, %.1f s\n", chords, double(x.size()) / rate);
        return 0;
    }
    const LiveConfig c = live_config(s.mode, s.quality, rate);
    const uint32_t hop = uint32_t(std::lround(c.hopSeconds * rate));
    ChromaFrontEnd fe(s, c.decimation, rate, c.fMin, c.fMax, c.binsPerOctave, hop, c.bassMin, c.bassMax, c.windowSeconds);
    ChordTracker tracker(s, c.hopSeconds, 0.0);
    const bool noteChords = std::getenv("DZ_NOTES") != nullptr;   // DEV: chords from perceived notes (ChordHmm)
    ChromaFrontEnd::Output o{};
    ChordTracker::Output t{};
    std::string shownPreview, shownConfirmed;
    int shownKey = -1;
    std::string shownHarmony;   // tonic + 12 * minor, -1 = no valid estimate
    int previewChanges = 0, confirmedChanges = 0;
    double now = 0;   // when the event is shown (the chord line carries the start the lag backdated it to)
    auto events = [&] {
        for (uint32_t i = 0; i < t.eventCount; i++)
            if (t.events[i].type == AnalyzerEventType::ChordConfirmed) std::printf("shown %8.3f %s\n", now, t.events[i].data.chord.symbol);
        for (uint32_t i = 0; i < t.eventCount; i++)
            if (t.events[i].type == AnalyzerEventType::ChordConfirmed)
                std::printf("chord %8.3f %-8s %.2f\n", t.events[i].data.chord.startTimeSeconds, t.events[i].data.chord.symbol, t.events[i].data.chord.confidence);
            else if (t.events[i].type == AnalyzerEventType::ChordEnded)   // the take / score label
                std::printf("ended %8.3f %8.3f %s\n", t.events[i].data.chord.startTimeSeconds, t.events[i].data.chord.endTimeSeconds, t.events[i].data.chord.symbol);
    };
    // DEV: DZ_CHROMA_IN=file ("t c0..c11" per line) replaces the chord input, to try another front end.
    std::vector<std::pair<double, std::vector<float>>> chromaIn;
    if (const char* in = std::getenv("DZ_CHROMA_IN")) {
        FILE* f = std::fopen(in, "r");
        double tt;
        while (f && std::fscanf(f, "%lf", &tt) == 1) {
            std::vector<float> v(12);
            for (float& e : v) if (std::fscanf(f, "%f", &e) != 1) e = 0;
            chromaIn.emplace_back(tt, v);
        }
        if (f) std::fclose(f);
    }
    size_t inPos = 0;
    for (size_t p = 0; p + hop <= x.size(); p += hop) {
        fe.process(x.data() + p, hop, p + hop, o);
        if (!chromaIn.empty()) {
            while (inPos + 1 < chromaIn.size() && chromaIn[inPos + 1].first <= o.chroma.timestampSeconds) inPos++;
            for (int k = 0; k < 12; k++) o.chroma.raw[k] = chromaIn[inPos].second[k];
        }
        if (noteChords) tracker.process_notes(o.notes, o.chroma, o.chroma.timestampSeconds, double(p + hop) / rate, t);
        else tracker.process(o.chroma, o.chroma.timestampSeconds, double(p + hop) / rate, o.bass, o.lastOnset, t);
        now = double(p + hop) / rate;
        events();
        if (noteChords && t.harmony != shownHarmony) {   // the harmony underneath, as the display would show it
            shownHarmony = t.harmony;
            if (!shownHarmony.empty()) std::printf("harmony %8.3f %s\n", double(p + hop) / rate, t.harmony);
        }
        int key = t.key.valid ? t.key.tonic + 12 * t.key.minor : -1;
        if (key != shownKey) {   // the song's key as the live display would show it
            static const char* names[12] = {"C", "C#", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B"};
            if (key < 0) std::printf("key %8.3f -\n", double(p + hop) / rate);
            else std::printf("key %8.3f %s %s r %.2f margin %.2f\n", double(p + hop) / rate, names[t.key.tonic], t.key.minor ? "minor" : "major", t.key.correlation, t.key.margin);
            shownKey = key;
        }
        if (std::getenv("DZ_NOTEDUMP")) {   // the perceived notes per hop (NoteFrontEnd)
            std::printf("notes %8.3f%s", double(p + hop) / rate, o.notes.audible ? "" : " (quiet)");
            for (int i = 0; i < o.notes.count; i++) std::printf(" %d", o.notes.midi[i]);
            std::printf("\n");
        }
        if (std::getenv("DZ_CHROMA")) {   // the chord recognizer's input, for note-level evaluation
            std::printf("chroma %8.3f", o.chroma.timestampSeconds);
            for (float v : o.chroma.raw) std::printf(" %.4g", v);
            std::printf(" bass %d %d\n", o.bass.valid ? o.bass.midi : -1, int(o.bass.settled));
        }
        if (std::getenv("DZ_PREVIEW")) std::printf("preview %8.3f %s\n", double(p + hop) / rate, t.preview.best.symbol[0] ? t.preview.best.symbol : "-");
        if (t.preview.best.symbol != shownPreview) { shownPreview = t.preview.best.symbol; previewChanges++; }
        if (t.confirmedSymbol != shownConfirmed) {
            // A rename without a new chord (the notes path names a chord better once more of it is
            // heard): scored from when it was decided, like a confirmation.
            bool event = false;
            for (uint32_t i = 0; i < t.eventCount; i++) event |= t.events[i].type == AnalyzerEventType::ChordConfirmed;
            if (!event && t.confirmedSymbol[0] && !shownConfirmed.empty())
                std::printf("chord %8.3f %-8s %.2f\n", double(p + hop) / rate - t.latencyMs / 1000.0, t.confirmedSymbol, 1.0);
            shownConfirmed = t.confirmedSymbol;
            confirmedChanges++;
        }
    }
    tracker.flush(t);
    events();
    const double seconds = double(x.size()) / rate;
    std::printf("preview changes %d (%.1f/s), confirmed changes %d, %.1f s\n", previewChanges, previewChanges / seconds, confirmedChanges, seconds);
}
