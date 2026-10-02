#include "post.hpp"

#include <cmath>
#include <cstdio>

#include "decode.hpp"
#include "live_config.hpp"
#include "miniaudio.h"
#include "sidecar.hpp"
#include "voice.hpp"

namespace dz {

void PostJob::set_error(const std::string& e) {
    while (errorLock_.test_and_set(std::memory_order_acquire)) {}
    error_ = e;
    errorLock_.clear(std::memory_order_release);
}

std::string PostJob::error() const {
    while (errorLock_.test_and_set(std::memory_order_acquire)) {}
    std::string e = error_;
    errorLock_.clear(std::memory_order_release);
    return e;
}

void PostJob::status(PostStatus& out) const {
    out = {};
    out.progress = progress_.load();
    out.state = PostState(state_.load());
}

int PostJob::start(const SessionConfig& s, double comp, const char* in, const char* out, bool grid) {
    if (state_.load() == uint8_t(PostState::Running)) { set_error("a job is already running"); return ANA_ERR_STATE; }
    join();
    cancel_ = false;
    progress_ = 0;
    set_error("");
    state_ = uint8_t(PostState::Running);
    std::string i = in, o = out;
    thread_ = std::thread([this, s, comp, i, o, grid] {
        int r = ANA_ERR_STATE;
        try { r = run(s, comp, i, o, grid); } catch (const std::exception& e) { set_error(e.what()); }
        state_ = uint8_t(r == ANA_OK ? PostState::Done : cancel_.load() ? PostState::Cancelled : PostState::Failed);
    });
    return ANA_OK;
}

int PostJob::run(const SessionConfig& session, double comp, const std::string& in, const std::string& out, bool grid) {
    // Decode to mono float at the file's rate (miniaudio mixes the channels down).
    ma_decoder dec;
    ma_decoder_config dc = ma_decoder_config_init(ma_format_f32, 1, 0);
    if (ma_decoder_init_file(in.c_str(), &dc, &dec) != MA_SUCCESS) { set_error("cannot decode " + in); return ANA_ERR_IO; }
    const uint32_t rate = dec.outputSampleRate;
    std::vector<float> x;
    float buf[8192];
    for (ma_uint64 got = 0; ma_decoder_read_pcm_frames(&dec, buf, 8192, &got) == MA_SUCCESS && got > 0;) x.insert(x.end(), buf, buf + got);
    ma_decoder_uninit(&dec);
    if (x.empty()) { set_error("empty file: " + in); return ANA_ERR_IO; }

    // The session's quality: HighPrecision is not better on the band mix (take labels 89 % vs 91 %
    // Balanced), so offline does not force it; the S5 whole-take decoders are where precision grows.
    const SessionConfig& s = session;
    const LiveConfig c = live_config(s.mode, s.quality, rate);
    const uint32_t hop = uint32_t(std::lround(c.hopSeconds * rate));
    std::vector<AnalyzerEvent> events;
    auto keep = [&](const AnalyzerEvent& e) {
        if (e.type == AnalyzerEventType::NoteEnd || e.type == AnalyzerEventType::ChordEnded || e.type == AnalyzerEventType::Cadence) {
            events.push_back(e);
            events.back().sequence = uint32_t(events.size() - 1);
        }
    };
    auto step = [&](size_t pos) {
        progress_.store(float(double(pos) / double(x.size())), std::memory_order_relaxed);
        return !cancel_.load(std::memory_order_relaxed);
    };

    if (!is_chord_mode(s.mode)) {
        VoicePipeline vp(s, c, rate, hop, comp);
        VoiceOutput o{};
        for (size_t pos = 0; pos + hop <= x.size(); pos += hop) {
            vp.process(x.data() + pos, hop, pos + hop, o);
            for (uint32_t i = 0; i < o.eventCount; i++) keep(o.events[i]);
            if ((pos / hop) % 256 == 0 && !step(pos)) { set_error("cancelled"); return ANA_ERR_STATE; }
        }
        vp.flush(o);
        for (uint32_t i = 0; i < o.eventCount; i++) keep(o.events[i]);
    } else {
        // Whole-take decoding (decode.hpp): the chord sequence decided with the future in view.
        bool cancelled = false;
        auto ev = decode_chords(s, x.data(), x.size(), rate, comp, grid, [&](double f) {
            progress_.store(float(f), std::memory_order_relaxed);
            cancelled = cancel_.load(std::memory_order_relaxed);
            return !cancelled;
        });
        if (cancelled) { set_error("cancelled"); return ANA_ERR_STATE; }
        for (const AnalyzerEvent& e : ev) keep(e);
    }

    FILE* f = std::fopen(out.c_str(), "w");
    if (!f) { set_error("cannot write " + out); return ANA_ERR_IO; }
    std::fprintf(f,
                 "{\n"
                 "  \"format\": \"dissonancia-take/1\",\n"
                 "  \"analysis\": \"studio-offline\",\n"
                 "  \"session\": {\"mode\": %d, \"quality\": %d, \"referenceA4\": %.3f, \"keySet\": %s, \"keyFifths\": %d,"
                 " \"keyMode\": %d, \"clef\": %d, \"meter\": [%d, %d], \"bpm\": %.3f, \"countInBars\": %d},\n"
                 "  \"deviceRate\": %u,\n"
                 "  \"frames\": %llu,\n"
                 "  \"compensationLatencyMs\": %.3f,\n"
                 "  \"eventLogComplete\": true,\n"
                 "  \"eventTimeBase\": \"seconds from the start of the file, minus the compensation\",\n"
                 "  \"events\": [",
                 int(s.mode), int(s.quality), s.referenceA4, s.keySet ? "true" : "false", s.keyFifths, int(s.keyMode), int(s.clef),
                 s.meter.numerator, s.meter.denominator, s.bpm, s.countInBars, rate, (unsigned long long)x.size(), comp * 1000);
    write_events_json(f, events, 0.0);
    std::fprintf(f, "\n  ]\n}\n");
    std::fclose(f);
    progress_ = 1;
    return ANA_OK;
}

}  // namespace dz
