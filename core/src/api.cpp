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
void ana_channel_defaults(ChannelParams* out) { if (out) dz::mix::channel_defaults(*out); }
void ana_master_defaults(MasterParams* out) { if (out) dz::mix::master_defaults(*out); }
int32_t ana_player_set_channel(PlayerHandle* p, int32_t track, const ChannelParams* params) {
    if (!p || !params) return ANA_ERR_ARG;
    return p->player.set_channel(track, *params);
}
void ana_player_set_master(PlayerHandle* p, const MasterParams* params) { if (p && params) p->player.set_master(*params); }
void ana_player_meters(PlayerHandle* p, MixMeters* out) { if (p && out) p->player.meters(*out); }
int32_t ana_player_render_tap(PlayerHandle* p, uint32_t mask, const char* path) {
    if (!p || !path) return ANA_ERR_ARG;
    int32_t r = p->player.render_tap(mask, path);
    if (r < 0) p->error = std::string("cannot write ") + path;
    return r;
}
int32_t ana_player_bounce(PlayerHandle* p, const char* path) {
    if (!p || !path) return ANA_ERR_ARG;
    int32_t r = p->player.bounce(path);
    if (r < 0) p->error = std::string("cannot write ") + path;
    return r;
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

int32_t ana_post_start(PostHandle* h, const SessionConfig* s, double comp, const char* in, const char* out, uint8_t grid) {
    if (!h || !s || !in || !out) return ANA_ERR_ARG;
    try { return h->job.start(*s, comp, in, out, grid != 0); } catch (const std::exception&) { return ANA_ERR_STATE; }
}

void ana_post_status(PostHandle* h, PostStatus* out) { if (h && out) h->job.status(*out); }
void ana_post_cancel(PostHandle* h) { if (h) h->job.cancel(); }

}  // extern "C"
