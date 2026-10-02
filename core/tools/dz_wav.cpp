// Offline chord run over a WAV file (16-bit PCM or float32, any channel count, mixed to mono):
// the same front end and tracker as the live engine, hop by hop, without a device. Prints the
// confirmed chords and how often the live preview would change on screen.
//   dz_wav file.wav [guitar|piano|general] [low|balanced|high]
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

#include "chords.hpp"
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
    SessionConfig s{};
    s.mode = mode == "piano" ? AnalysisMode::PianoChords : mode == "general" ? AnalysisMode::GeneralChords : AnalysisMode::GuitarChords;
    s.quality = q == "low" ? AudioQuality::LowLatency : q == "high" ? AudioQuality::HighPrecision : AudioQuality::Balanced;
    s.referenceA4 = 440;
    s.keySet = 1;
    s.meter = {4, 4};
    s.bpm = 120;
    const LiveConfig c = live_config(s.mode, s.quality, rate);
    const uint32_t hop = uint32_t(std::lround(c.hopSeconds * rate));
    ChromaFrontEnd fe(s, c.decimation, rate, c.fMin, c.fMax, c.binsPerOctave, hop, c.bassMin, c.bassMax, c.windowSeconds);
    ChordTracker tracker(s, c.hopSeconds, 0.0);
    ChromaFrontEnd::Output o{};
    ChordTracker::Output t{};
    std::string shownPreview, shownConfirmed;
    int previewChanges = 0, confirmedChanges = 0;
    auto events = [&] {
        for (uint32_t i = 0; i < t.eventCount; i++)
            if (t.events[i].type == AnalyzerEventType::ChordConfirmed)
                std::printf("chord %8.3f %-8s %.2f\n", t.events[i].data.chord.startTimeSeconds, t.events[i].data.chord.symbol, t.events[i].data.chord.confidence);
            else if (t.events[i].type == AnalyzerEventType::ChordEnded)   // the take / score label
                std::printf("ended %8.3f %8.3f %s\n", t.events[i].data.chord.startTimeSeconds, t.events[i].data.chord.endTimeSeconds, t.events[i].data.chord.symbol);
    };
    for (size_t p = 0; p + hop <= x.size(); p += hop) {
        fe.process(x.data() + p, hop, p + hop, o);
        tracker.process(o.chroma, o.chroma.timestampSeconds, double(p + hop) / rate, o.bass, o.lastOnset, t);
        events();
        if (std::getenv("DZ_PREVIEW")) std::printf("preview %8.3f %s\n", double(p + hop) / rate, t.preview.best.symbol[0] ? t.preview.best.symbol : "-");
        if (t.preview.best.symbol != shownPreview) { shownPreview = t.preview.best.symbol; previewChanges++; }
        if (t.confirmedSymbol != shownConfirmed) { shownConfirmed = t.confirmedSymbol; confirmedChanges++; }
    }
    tracker.flush(t);
    events();
    const double seconds = double(x.size()) / rate;
    std::printf("preview changes %d (%.1f/s), confirmed changes %d, %.1f s\n", previewChanges, previewChanges / seconds, confirmedChanges, seconds);
}
