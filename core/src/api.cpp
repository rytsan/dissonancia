// extern "C" surface (spec §22): no C++ types, no exceptions across the boundary.
#include <cstdio>
#include <cstring>
#include <new>
#include <string>
#include <vector>

#include "dissonancia.h"
#include "engine.hpp"
#include "mixdsp.hpp"
#include "player.hpp"
#include "post.hpp"
#include "rt.hpp"
#if DZ_MIDI
#include "RtMidi.h"
#endif

struct PlayerHandle {
    ma_context ctx{};
    bool ctxOk = false;
    dz::Player player;
    std::string error;
};

struct PostHandle {
    dz::PostJob job;
    std::string error;
};

struct AnalyzerHandle {
    ma_context ctx{};
    bool ctxOk = false;
    std::vector<ma_device_info> captures;
    dz::Engine engine;
    std::string error;
#if DZ_MIDI
    std::unique_ptr<RtMidiIn> midi;   // after the engine: closed first (its callback feeds the engine)
#endif
};

namespace {

template <class F>
int32_t guarded(AnalyzerHandle* h, F&& f) {
    if (!h) return ANA_ERR_ARG;
    try {
        int32_t r = f();
        if (r < 0) h->error = h->engine.error();
        return r;
    } catch (const std::exception& e) {
        h->error = e.what();
        return ANA_ERR_STATE;
    } catch (...) {
        h->error = "unknown error";
        return ANA_ERR_STATE;
    }
}

}  // namespace

extern "C" {

AnalyzerHandle* ana_create(void) {
    auto* h = new (std::nothrow) AnalyzerHandle;
    if (h) h->ctxOk = ma_context_init(nullptr, 0, nullptr, &h->ctx) == MA_SUCCESS;
    return h;
}

void ana_destroy(AnalyzerHandle* h) {
    if (!h) return;
    try { h->engine.stop(); } catch (...) {}
    if (h->ctxOk) ma_context_uninit(&h->ctx);
    delete h;
}

const char* ana_last_error(AnalyzerHandle* h) { return h ? h->error.c_str() : "null handle"; }
double ana_now(void) { return dz::now_seconds(); }

void ana_struct_layout(AbiLayout* out) {
    if (!out) return;
    out->sessionConfigSize = sizeof(SessionConfig);
    out->audioDeviceConfigSize = sizeof(AudioDeviceConfig);
    out->liveSnapshotSize = sizeof(LiveSnapshot);
    out->analyzerEventSize = sizeof(AnalyzerEvent);
    out->snapshotWaveMinOffset = offsetof(LiveSnapshot, waveMin);
    out->snapshotScopeOffset = offsetof(LiveSnapshot, scope);
    out->snapshotBeatInBarOffset = offsetof(LiveSnapshot, beatInBar);
    out->eventDataOffset = offsetof(AnalyzerEvent, data);
    out->chordEventSize = sizeof(ChordEvent);
    out->noteEventSize = sizeof(MusicalNoteEvent);
    out->snapshotPitchOffset = offsetof(LiveSnapshot, pitch);
    out->snapshotNoteOffset = offsetof(LiveSnapshot, note);
    out->snapshotChromaOffset = offsetof(LiveSnapshot, chroma);
    out->snapshotCqtOffset = offsetof(LiveSnapshot, cqtMagnitude);
    out->snapshotChordOffset = offsetof(LiveSnapshot, chord);
    out->chordResultSize = sizeof(ChordRecognitionResult);
    out->snapshotBassOffset = offsetof(LiveSnapshot, bass);
}

int32_t ana_capture_device_count(AnalyzerHandle* h) {
    return guarded(h, [&]() -> int32_t {
        if (!h->ctxOk) { h->error = "no audio backend"; return ANA_ERR_DEVICE; }
        ma_device_info* infos = nullptr;
        ma_uint32 count = 0;
        if (ma_context_get_devices(&h->ctx, nullptr, nullptr, &infos, &count) != MA_SUCCESS) return ANA_ERR_DEVICE;
        h->captures.assign(infos, infos + count);
        return int32_t(count);
    });
}

int32_t ana_capture_device_name(AnalyzerHandle* h, int32_t index, char* utf8, int32_t cap) {
    return guarded(h, [&]() -> int32_t {
        if (!utf8 || cap <= 0 || index < 0 || size_t(index) >= h->captures.size()) return ANA_ERR_ARG;
        std::snprintf(utf8, size_t(cap), "%s", h->captures[size_t(index)].name);
        return ANA_OK;
    });
}

int32_t ana_start(AnalyzerHandle* h, const SessionConfig* session, const AudioDeviceConfig* device) {
    return guarded(h, [&]() -> int32_t {
        if (!session || !device) return ANA_ERR_ARG;
        if (!h->ctxOk) { h->error = "no audio backend"; return ANA_ERR_DEVICE; }
        const ma_device_id* id = nullptr;
        if (device->captureDevice >= 0) {
            if (size_t(device->captureDevice) >= h->captures.size()) return ANA_ERR_ARG;
            id = &h->captures[size_t(device->captureDevice)].id;
        }
        return h->engine.start(*session, *device, &h->ctx, id);
    });
}

int32_t ana_stop(AnalyzerHandle* h) { return guarded(h, [&] { return int32_t(h->engine.stop()); }); }

void ana_read_snapshot(AnalyzerHandle* h, LiveSnapshot* out) {
    if (h && out) h->engine.read_snapshot(out);
}

int32_t ana_drain_events(AnalyzerHandle* h, AnalyzerEvent* out, int32_t cap) {
    if (!h || !out || cap <= 0) return 0;
    return int32_t(h->engine.drain_events(out, size_t(cap)));
}

int32_t ana_read_audio(AnalyzerHandle* h, float* out, int32_t cap) {
    if (!h || !out || cap <= 0) return 0;
    return int32_t(h->engine.read_audio(out, size_t(cap)));
}

int32_t ana_rec_start(AnalyzerHandle* h, const char* wavPathUtf8) {
    return guarded(h, [&]() -> int32_t {
        if (!wavPathUtf8) return ANA_ERR_ARG;
        return h->engine.rec_start(wavPathUtf8);
    });
}

int32_t ana_rec_stop(AnalyzerHandle* h) { return guarded(h, [&] { return int32_t(h->engine.rec_stop()); }); }

int32_t ana_set_metronome(AnalyzerHandle* h, uint8_t on, float bpm, TimeSignature meter) {
    return guarded(h, [&] { return int32_t(h->engine.set_metronome(on != 0, bpm, meter)); });
}

void ana_clear_clip(AnalyzerHandle* h) {
    if (h) h->engine.clear_clip();
}

// ---------------------------------------------------------------- LIVE playground

int32_t ana_pad_load(AnalyzerHandle* h, int32_t slot, const char* path) {
    return guarded(h, [&] {
        if (!h->engine.running()) { h->error = "start LIVE first (the pads play at its rate)"; return int32_t(ANA_ERR_STATE); }
        std::string err;
        int32_t r = h->engine.playground().load(slot, path, h->engine.rate(), err);
        if (r < 0) h->error = err;
        return r;
    });
}
void ana_pad_hit(AnalyzerHandle* h, int32_t pad, float velocity) { if (h && pad >= 0 && pad < ANA_PADS) h->engine.playground().hit(pad, velocity); }
void ana_midi_send(AnalyzerHandle* h, uint8_t status, uint8_t d1, uint8_t d2) { if (h) h->engine.playground().midi_from_api(status, d1, d2); }
void ana_playground_set(AnalyzerHandle* h, const PlaygroundConfig* c) { if (h && c) h->engine.playground().set(*c); }
void ana_playground_status(AnalyzerHandle* h, PlaygroundStatus* out) { if (h && out) h->engine.playground().status(*out); }
int32_t ana_pg_song(AnalyzerHandle* h, const PgBlock* blocks, int32_t nb, const uint8_t* steps, int32_t ns, const PgNote* notes, int32_t nn,
                    const PgEntry* order, int32_t no) {
    if (!h || (nb > 0 && !blocks) || (ns > 0 && !steps) || (nn > 0 && !notes) || (no > 0 && !order)) return ANA_ERR_ARG;
    return guarded(h, [&] {
        std::string err;
        int32_t r = h->engine.playground().song(blocks, nb, steps, ns, notes, nn, order, no, err);
        if (r < 0) h->error = err;
        return r;
    });
}
void ana_pg_play(AnalyzerHandle* h, int32_t entry, uint8_t loop) { if (h) h->engine.playground().play(entry, loop != 0); }
void ana_pg_stop(AnalyzerHandle* h) { if (h) h->engine.playground().stop(); }
int32_t ana_pg_record(AnalyzerHandle* h, uint8_t on) {
    return guarded(h, [&] {
        if (on && !h->engine.running()) { h->error = "start LIVE first"; return int32_t(ANA_ERR_STATE); }
        return int32_t(h->engine.playground().record(on != 0, h->engine.beat_frames_now() * h->engine.beats_per_bar()));
    });
}
int32_t ana_pg_recorded(AnalyzerHandle* h, PgRecorded* out, int32_t cap) {
    if (!h || !out || cap <= 0) return 0;
    return int32_t(h->engine.playground().recorded(out, size_t(cap)));
}
int32_t ana_pg_loop_file(AnalyzerHandle* h, uint32_t id, const char* path, uint8_t save) {
    if (!h || !path) return ANA_ERR_ARG;
    return guarded(h, [&] {
        std::string err;
        int32_t r = h->engine.playground().loop_file(id, path, save != 0, h->engine.rate(), err);
        if (r < 0) h->error = err.empty() ? std::string("cannot write ") + path : err;
        return r;
    });
}
int32_t ana_pg_bounce(AnalyzerHandle* h, const char* path) {
    if (!h || !path) return ANA_ERR_ARG;
    return guarded(h, [&] {
        std::string err;
        int32_t r = h->engine.playground().bounce(path, h->engine.rate(), h->engine.beat_frames_now(), h->engine.beats_per_bar(), err);
        if (r < 0) h->error = err.empty() ? std::string("cannot write ") + path : err;
        return r;
    });
}

#if DZ_MIDI
namespace {
RtMidiIn* midi_probe(AnalyzerHandle* h) {
    if (!h->midi) {
        try { h->midi = std::make_unique<RtMidiIn>(); } catch (const RtMidiError& e) { h->error = e.getMessage(); return nullptr; }
    }
    return h->midi.get();
}
}  // namespace
int32_t ana_midi_port_count(AnalyzerHandle* h) {
    if (!h) return 0;
    try { auto* m = midi_probe(h); return m ? int32_t(m->getPortCount()) : 0; } catch (...) { return 0; }
}
int32_t ana_midi_port_name(AnalyzerHandle* h, int32_t index, char* utf8, int32_t cap) {
    if (!h || !utf8 || cap <= 0) return ANA_ERR_ARG;
    try {
        auto* m = midi_probe(h);
        if (!m || index < 0 || unsigned(index) >= m->getPortCount()) return ANA_ERR_ARG;
        std::snprintf(utf8, size_t(cap), "%s", m->getPortName(unsigned(index)).c_str());
        return ANA_OK;
    } catch (...) { return ANA_ERR_STATE; }
}
int32_t ana_midi_open(AnalyzerHandle* h, int32_t index) {
    if (!h) return ANA_ERR_ARG;
    try {
        auto* m = midi_probe(h);
        if (!m) return int32_t(ANA_ERR_DEVICE);
        if (m->isPortOpen()) { m->cancelCallback(); m->closePort(); }
        if (index < 0) return int32_t(ANA_OK);
        if (unsigned(index) >= m->getPortCount()) return int32_t(ANA_ERR_ARG);
        m->openPort(unsigned(index), "Dissonancia");
        m->ignoreTypes(true, true, true);   // no sysex, timing clock, active sensing
        m->setCallback([](double, std::vector<unsigned char>* msg, void* user) {
            if (msg && msg->size() >= 2) static_cast<dz::Engine*>(user)->playground().midi_from_device((*msg)[0], (*msg)[1], msg->size() > 2 ? (*msg)[2] : 0);
        }, &h->engine);
        return int32_t(ANA_OK);
    } catch (const RtMidiError& e) {
        h->error = e.getMessage();
        return int32_t(ANA_ERR_DEVICE);
    }
}
#else
int32_t ana_midi_port_count(AnalyzerHandle*) { return 0; }
int32_t ana_midi_port_name(AnalyzerHandle*, int32_t, char*, int32_t) { return ANA_ERR_ARG; }
int32_t ana_midi_open(AnalyzerHandle* h, int32_t index) { if (h && index >= 0) h->error = "built without MIDI input"; return index < 0 ? ANA_OK : ANA_ERR_DEVICE; }
#endif
int32_t ana_midi_read(AnalyzerHandle* h, uint32_t* out, int32_t cap) {
    if (!h || !out || cap <= 0) return 0;
    return int32_t(h->engine.playground().read_midi(out, size_t(cap)));
}

PlayerHandle* ana_player_create(void) {
    auto* p = new (std::nothrow) PlayerHandle;
    if (p) p->ctxOk = ma_context_init(nullptr, 0, nullptr, &p->ctx) == MA_SUCCESS;
    return p;
}

void ana_player_destroy(PlayerHandle* p) {
    if (!p) return;
    p->player.close_device();
    if (p->ctxOk) ma_context_uninit(&p->ctx);
    delete p;
}

const char* ana_player_last_error(PlayerHandle* p) { return p ? p->error.c_str() : "null handle"; }

int32_t ana_player_load(PlayerHandle* p, const char* pathUtf8) {
    if (!p || !pathUtf8) return ANA_ERR_ARG;
    try {
        int32_t r = p->player.load(pathUtf8);
        if (r == ANA_OK) r = p->player.open_device(p->ctxOk ? &p->ctx : nullptr);   // no backend: silent, still analysable
        if (r < 0) p->error = p->player.error();
        return r;
    } catch (const std::exception& e) {
        p->error = e.what();
        return ANA_ERR_STATE;
    }
}

void ana_player_info(PlayerHandle* p, PlayerInfo* out) { if (p && out) p->player.info(*out); }
void ana_player_play(PlayerHandle* p) { if (p) p->player.play(); }
void ana_player_stop(PlayerHandle* p) { if (p) p->player.stop(); }
void ana_player_seek(PlayerHandle* p, uint64_t frame) { if (p) p->player.seek(frame); }
void ana_player_set_loop(PlayerHandle* p, uint64_t a, uint64_t b) { if (p) p->player.set_loop(a, b); }

int32_t ana_player_apply_edits(PlayerHandle* p, const EditSegment* segs, int32_t count, uint64_t fadeIn, uint64_t fadeOut, float norm) {
    if (!p || (count > 0 && !segs)) return ANA_ERR_ARG;
    try {
        int32_t r = p->player.apply_edits(segs, count, fadeIn, fadeOut, norm);
        if (r < 0) p->error = p->player.error();
        return r;
    } catch (const std::exception& e) {
        p->error = e.what();
        return ANA_ERR_STATE;
    }
}

int32_t ana_player_save_wav(PlayerHandle* p, const char* path) {
    if (!p || !path) return ANA_ERR_ARG;
    int32_t r = p->player.save_wav(path);
    if (r < 0) p->error = std::string("cannot write ") + path;
    return r;
}

int32_t ana_player_add_track(PlayerHandle* p, const char* path) {
    if (!p || !path) return ANA_ERR_ARG;
    try {
        int32_t r = p->player.add_track(path);
        if (r < 0) p->error = p->player.error();
        return r;
    } catch (const std::exception& e) {
        p->error = e.what();
        return ANA_ERR_STATE;
    }
}
void ana_player_clear_tracks(PlayerHandle* p) { if (p) p->player.clear_tracks(); }
void ana_unit_defaults(UnitParams* out) { if (out) dz::mix::unit_defaults(*out); }
void ana_master_defaults(MasterParams* out) { if (out) dz::mix::master_defaults(*out); }
int32_t ana_player_set_mix(PlayerHandle* p, const MixGraph* mix) {
    if (!p || !mix) return ANA_ERR_ARG;
    int32_t r = p->player.set_mix(*mix);
    if (r < 0) p->error = p->player.error();
    return r;
}
void ana_player_meters(PlayerHandle* p, MixMeters* out) { if (p && out) p->player.meters(*out); }
int32_t ana_player_render_tap(PlayerHandle* p, uint32_t mask, const char* path) {
    if (!p || !path) return ANA_ERR_ARG;
    int32_t r = p->player.render_tap(mask, path);
    if (r < 0) p->error = std::string("cannot write ") + path;
    return r;
}
int32_t ana_render_chain(const char* in, const UnitParams* units, int32_t count, const char* out) {
    if (!in || (!units && count > 0) || count < 0 || count > ANA_MAX_UNITS || !out) return ANA_ERR_ARG;
    return dz::render_chain(in, units, count, out);
}
int32_t ana_player_bounce(PlayerHandle* p, const char* path) {
    if (!p || !path) return ANA_ERR_ARG;
    int32_t r = p->player.bounce(path);
    if (r < 0) p->error = std::string("cannot write ") + path;
    return r;
}

int32_t ana_player_scope(PlayerHandle* p, int32_t source, float* out, int32_t frames) {
    if (!p || !out || frames < 0) return ANA_ERR_ARG;
    return p->player.scope(source, out, frames);
}
int32_t ana_player_set_source(PlayerHandle* p, int32_t track, const char* path) {
    if (!p) return ANA_ERR_ARG;
    int32_t r = p->player.set_source(track, path);
    if (r < 0) p->error = p->player.error();
    return r;
}
void ana_player_set_monitor(PlayerHandle* p, int32_t track) { if (p) p->player.set_monitor(track); }
void ana_player_reset_loudness(PlayerHandle* p) { if (p) p->player.reset_loudness(); }
void ana_player_pull(PlayerHandle* p, int32_t frames) {
    if (!p || frames <= 0) return;
    std::vector<float> out(512 * 2);
    for (int32_t done = 0; done < frames; done += 512) p->player.render(out.data(), uint32_t(std::min(512, frames - done)), 2);
}

int32_t ana_player_peaks(PlayerHandle* p, uint64_t a, uint64_t b, int32_t columns, float* mn, float* mx) {
    if (!p || columns <= 0 || !mn || !mx) return ANA_ERR_ARG;
    p->player.peaks(a, b, uint32_t(columns), mn, mx);
    return ANA_OK;
}

PostHandle* ana_post_create(void) { return new (std::nothrow) PostHandle; }
void ana_post_destroy(PostHandle* h) { delete h; }

const char* ana_post_last_error(PostHandle* h) {
    if (!h) return "null handle";
    h->error = h->job.error();
    return h->error.c_str();
}

int32_t ana_post_start(PostHandle* h, const SessionConfig* s, double comp, const char* in, const char* out, uint8_t flags) {
    if (!h || !s || !in || !out) return ANA_ERR_ARG;
    try { return h->job.start(*s, comp, in, out, flags); } catch (const std::exception&) { return ANA_ERR_STATE; }
}

void ana_post_status(PostHandle* h, PostStatus* out) { if (h && out) h->job.status(*out); }
void ana_post_cancel(PostHandle* h) { if (h) h->job.cancel(); }

}  // extern "C"
