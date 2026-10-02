// Dissonancia core — C ABI (spec §22).
// Every struct here crosses the ABI: POD, fixed-size inline arrays, no alignas,
// every padding hole written as an explicit _padN field (checked with -Wpadded).
#pragma once
#include <cstddef>
#include <cstdint>

#if defined(_WIN32)
#  define ANA_API __declspec(dllexport)
#else
#  define ANA_API __attribute__((visibility("default")))
#endif

constexpr size_t ANA_MAX_CQT_BINS = 160;   // 24 bpo x 6 octaves + margin
constexpr size_t ANA_WAVE_COLUMNS = 1024;
constexpr size_t ANA_SCOPE_SAMPLES = 2048;
constexpr size_t ANA_EVENT_QUEUE_CAPACITY = 4096;

// ---------------------------------------------------------------- session (§5)

enum class AnalysisMode : uint8_t { VoiceMono, InstrumentMono, GuitarChords, PianoChords, GeneralChords };
enum class AudioQuality : uint8_t { LowLatency, Balanced, HighPrecision };
enum class KeyMode : uint8_t { Major, NaturalMinor, HarmonicMinor, MelodicMinor, Dorian, Phrygian, Mixolydian };
enum class Clef : uint8_t { Treble, Treble8vb, Bass, Alto, Tenor };

struct TimeSignature { uint8_t numerator, denominator; };

struct GuitarTuning {
    uint8_t stringCount;
    int8_t openMidi[8];                // standard: 40 45 50 55 59 64
    int8_t capoFret;
};

struct SessionConfig {                 // passed to ana_start(), immutable after
    AnalysisMode mode;
    AudioQuality quality;
    uint8_t keySet;                    // bool
    int8_t keyFifths;                  // -7..+7, MusicXML <fifths>
    float referenceA4;                 // default 440
    KeyMode keyMode;
    Clef clef;
    TimeSignature meter;
    float bpm;
    uint8_t metronome;                 // bool; forced on while REC
    uint8_t countInBars;
    GuitarTuning guitarTuning;         // GuitarChords only
};

struct AudioDeviceConfig {
    int32_t captureDevice;             // index from ana_capture_device_name, -1 = system default
    uint32_t sampleRate;               // 0 = device native; the real rate is read back
    uint32_t periodFrames;             // 0 = 5 ms request; the real period is read back
    uint8_t exclusive;                 // bool: WASAPI exclusive capture
    uint8_t clickOutput;               // bool: duplex device, click on the same device's output
    uint8_t clickDuringTake;           // bool: 0 = click only in the count-in (a microphone does not record it)
    uint8_t _pad0[1];
};

// ---------------------------------------------------------------- events (§3, §13, §14)

enum class OnsetType : uint8_t { VocalAttack, InstrumentAttack, ChordAttack, NoteChange, Unknown };
enum class ChordQuality : uint8_t {
    Major, Minor, Diminished, Augmented, Sus2, Sus4,
    Power, Dom7, Maj7, Min7, HalfDim7, Dim7, Maj6, Min6, Add9, Unknown
};

// Harmonic function of a chord in the session key (§15). Out of key is never an error.
enum class DiatonicStatus : uint8_t {
    Diatonic, ChromaticPassing, ChromaticNeighbor,
    AppliedDominant, BorrowedChord, PossibleModulation, Unknown
};
enum class CadenceType : uint8_t { None, PerfectAuthentic, ImperfectAuthentic, Half, Plagal, Deceptive, Phrygian };

struct OnsetEvent {
    double timestampSeconds;
    float strength;
    OnsetType type;
    uint8_t _pad0[3];
};

struct MusicalNoteEvent {
    double startTimeSeconds;           // backdated to the onset (§13)
    double endTimeSeconds;
    double durationSeconds;
    float avgHz, medianHz, avgCents;
    int8_t midi;
    char writtenName[8];
    uint8_t _pad0[3];
    float confidence;
    uint8_t rest, tie, chromatic, vibrato;   // bool
};

struct ChordEvent {
    double startTimeSeconds;           // backdated to the onset (§13)
    double endTimeSeconds;
    double durationSeconds;
    char symbol[16];
    int8_t rootPitchClass;
    int8_t bassPitchClass;
    ChordQuality quality;
    int8_t inversion;
    uint8_t detectedCount, missingCount;
    int8_t detectedNotes[8];
    int8_t missingNotes[8];
    char roman[12];                    // ASCII Roman numeral in the key, e.g. "V7", "ii65", "V/V", "bVII", "viio7"; "" = no key
    DiatonicStatus diatonicStatus;
    uint8_t _pad0;
    float confidence;
    uint8_t incomplete, arpeggiated, provisional, bassSettled;   // bool
    uint8_t _pad1[4];
};

// Harmonic cadence candidate (§17), from root motion + bass position. LIVE has no melody and
// no phrase analysis, so the confidence stays below 0.8 and the evidence says what was used.
struct CadenceEvent {
    double timestampSeconds;           // arrival (backdated onset) of the final chord
    CadenceType type;
    uint8_t _pad0[3];
    float confidence;
    char fromRoman[16];
    char toRoman[16];
    char evidence[40];                 // ASCII, e.g. "5-1, root position, V7"
};

enum class AnalyzerEventType : uint8_t {
    Onset,          // display only
    NoteStart,      // display only (provisional)
    NoteEnd,        // COMPLETE MusicalNoteEvent (start + end)
    ChordConfirmed, // display only (end not yet known)
    ChordEnded,     // COMPLETE ChordEvent (start + end)
    Cadence         // CadenceEvent, after the chord that completes it
};

struct AnalyzerEvent {
    AnalyzerEventType type;
    uint8_t _pad0[3];
    uint32_t sequence;                 // +1 per event; a gap = dropped events
    union {
        OnsetEvent onset;
        MusicalNoteEvent note;
        ChordEvent chord;
        CadenceEvent cadence;
    } data;
};

// ---------------------------------------------------------------- pitch (§7)

struct PitchEstimate {                 // instantaneous, every hop (tuner needle)
    uint8_t voiced;                    // bool
    uint8_t _pad0[3];
    float frequencyHz;
    float midiFloat;                   // 69 + 12 log2(f / A4)
    float confidence;
    float clarity;
    float rms;
    double timestampSeconds;           // window centre, sample clock, delay-compensated
};

struct NoteEstimate {                  // stable note (median + tracker), spelled (§22.2)
    uint8_t valid;                     // bool
    int8_t midi;                       // sounding pitch
    int8_t letter;                     // 0=C .. 6=B
    int8_t alter;                      // -2..+2
    int8_t writtenOctave;              // octave of the LETTER, after clef shift (Cb4 = MIDI 59)
    char writtenName[8];               // ASCII, e.g. "C#4", "Db4", "Cb4", "Fx5"
    uint8_t _pad0[3];
    float detectedHz;
    float expectedHz;
    float cents;                       // instantaneous pitch vs the stable note
    float confidence;
    uint8_t chromatic, diatonic;       // bool
    uint8_t _pad1[2];
};

// ---------------------------------------------------------------- CQT / chroma / tuning (§8, §9)

struct TuningEstimate {
    uint8_t valid;                     // bool: enough stable peaks, >= 60 % inliers
    uint8_t _pad0[3];
    float referenceA4;                 // estimated A4 (session A4 x offset)
    float offsetCents;                 // median deviation of stable peaks from the tempered grid
    float confidence;                  // inlier fraction (+-15 cents of the median)
};

struct ChromaVector {
    float raw[12];                     // energy per pitch class, C = 0
    float normalized[12];              // log-compressed, max = 1, noise bins zeroed
    float smoothed[12];
    float bass[12];                    // bass chroma (section 11, M4)
    double timestampSeconds;
    float confidence;
    float tuningOffsetCents;           // kernel set in use
};

// ---------------------------------------------------------------- bass (§11)

struct BassEstimate {
    uint8_t valid;                     // bool
    int8_t midi;
    int8_t pitchClass;
    uint8_t settled;                   // bool: settleSeconds elapsed since the onset AND CQT confirmed
    float frequencyHz;
    float confidence;
    int8_t letter, alter, writtenOctave;   // spelled like NoteEstimate (§22.2)
    char writtenName[8];
    uint8_t fromPreview;               // bool: value comes from the periodicity preview, not the CQT
    float previewHz;                   // periodicity preview, 0 = unvoiced
    float settleRemainingMs;           // until the lowest window has refilled after the last onset
};

// ---------------------------------------------------------------- chords (§12)

struct ChordCandidate {
    int8_t rootPitchClass;
    int8_t bassPitchClass;             // -1 = unknown (no inversion reported, §11 hard rule)
    ChordQuality quality;
    char symbol[16];                   // ASCII, e.g. "Cmaj7", "F#m", "Bbsus4"
    uint8_t expectedCount, detectedCount, missingCount, extraCount;
    int8_t expected[8];
    int8_t detected[8];
    int8_t missing[8];
    int8_t extra[8];
    char roman[12];                    // as ChordEvent.roman
    DiatonicStatus diatonicStatus;
    float rootScore, thirdScore, fifthScore, chromaScore, bassScore, temporalScore, tonalScore, totalScore;
    float confidence;
    uint8_t hasBass, incomplete, arpeggiated;   // bool
    uint8_t _pad1;
};

constexpr size_t ANA_MAX_CHORD_ALTERNATIVES = 3;
struct ChordRecognitionResult {        // per-hop preview
    ChordCandidate best;
    uint8_t alternativeCount;
    uint8_t ambiguous;                 // bool
    uint8_t _pad0[2];
    ChordCandidate alternatives[ANA_MAX_CHORD_ALTERNATIVES];
    char explanation[64];              // e.g. "C6 = Am7 - bass decides", "missing third"
};

// Tempo heard in the live input: beat marks for the waveform display only. The session BPM
// (metronome, REC, score) is set by hand and never follows it.
struct ContextEstimate {
    float bpm;                         // beats per minute of the pulse, 0 = no estimate (needs ~4 s of onsets)
    float tempoConfidence;             // 0..1: normalised autocorrelation at the chosen period
    double lastBeatSeconds;            // sample clock (analyzedFrames / sampleRate) of the latest beat mark
};

// ---------------------------------------------------------------- snapshot (§22)
// Meters, scope, transport, latency, pitch (M1). Chord blocks are added by M3+
// (the layout test keeps C# in sync).

struct LiveSnapshot {
    uint64_t sequence;
    double publishTimeSeconds;         // ana_now() clock, for GUI latency
    double recordedSeconds;            // negative while counting in: seconds until the take starts
    uint64_t analyzedFrames;           // input sample clock position of this snapshot
    uint32_t sampleRate;               // real device rate (read back)
    uint32_t liveRate;                 // analysis rate after integer decimation (§5)
    float hopSeconds;
    float settleSeconds;               // computed Q / f_min (chord modes) or window (mono modes)
    float latencyCaptureMs;            // backend-REPORTED device latency (not a loopback measurement)
    float latencyProcessingMs;         // measured: last sample delivered -> publish
    uint32_t xruns;                    // capture frames lost (analysis ring full)
    uint32_t droppedEvents;
    uint32_t recorderGaps;
    float metronomeBpm;                // 0 = off
    float vuLevel;                     // VU units (0 VU = -18 dBFS), 300 ms ballistics
    float peakDbfs, peakHoldDbfs;
    uint32_t waveWriteIndex;
    float waveColumnSeconds;
    float cpuPercent;                  // analysis thread busy time / wall time
    uint8_t beatInBar;                 // 1-based, 0 = metronome off
    uint8_t recording, countingIn, clipLatched;   // bool
    float noteLatencyMs;               // estimated onset -> publish of the stable note
    PitchEstimate pitch;
    NoteEstimate note;
    uint16_t cqtBinCount, cqtBinsPerOctave;   // 0 in mono modes
    float cqtMinHz;                    // bin k = cqtMinHz * 2^(k / bpo), tuning applied
    TuningEstimate tuning;
    uint8_t _pad1[4];
    ChromaVector chroma;
    float cqtMagnitude[ANA_MAX_CQT_BINS];   // inline copy, no pointers
    ChordRecognitionResult chord;      // preview, every hop (chord modes)
    char confirmedSymbol[16];          // tracker's confirmed chord, "" = none
    uint8_t chordConfirmed;            // bool: preview == confirmed chord
    uint8_t _pad2[3];
    float chordLatencyMs;              // estimated onset -> first preview of the current candidate
    float chordConfirmElapsedMs;       // time since onset while provisional, 0 once confirmed
    BassEstimate bass;
    double lastOnsetSeconds;           // sample clock, delay-compensated; < 0 = none yet
    ContextEstimate context;           // tempo heard so far (display only)
    float waveMin[ANA_WAVE_COLUMNS];
    float waveMax[ANA_WAVE_COLUMNS];
    float scope[ANA_SCOPE_SAMPLES];    // last samples, oldest first
};

// Layout exported for the C# layout test (§22).
struct AbiLayout {
    uint32_t sessionConfigSize, audioDeviceConfigSize, liveSnapshotSize, analyzerEventSize;
    uint32_t snapshotWaveMinOffset, snapshotScopeOffset, snapshotBeatInBarOffset, eventDataOffset;
    uint32_t chordEventSize, noteEventSize;
    uint32_t snapshotPitchOffset, snapshotNoteOffset, snapshotChromaOffset, snapshotCqtOffset, snapshotChordOffset, chordResultSize, snapshotBassOffset;
};

// ---------------------------------------------------------------- STUDIO player (studio-plan S1)

struct PlayerInfo {
    uint64_t frames;                   // file length
    uint64_t positionFrame;            // playback cursor
    uint32_t sampleRate;               // the file's rate
    uint16_t channels;                 // 1 or 2 (more are mixed down to the first two)
    uint8_t playing, looping;          // bool
};

// Non-destructive edit (studio-plan S2): the edited take is these source ranges in order, each with
// its clip gain; the original file is never modified.
struct EditSegment {
    uint64_t sourceStart, sourceEnd;   // frames of the original file, [start, end)
    float gainDb;                      // clip gain
    uint8_t _pad0[4];
};

// ---------------------------------------------------------------- STUDIO mixing (studio-plan S4)
// One channel strip in the order of a real console: trim -> high-pass / low-pass -> gate -> EQ ->
// compressor -> [analysis tap] -> fader -> pan. The master: EQ -> bus compressor -> limiter -> fader.

constexpr int ANA_MAX_TRACKS = 8;       // track 0 = the edited take, 1.. = separated stems
constexpr int ANA_EQ_BANDS = 4;

enum class EqType : uint8_t { Peak, LowShelf, HighShelf };

struct EqBand {
    float freqHz, gainDb, q;
    EqType type;
    uint8_t on;                        // bool
    uint8_t _pad0[2];
};

struct ChannelParams {
    float trimDb;
    float hpHz, lpHz;
    uint8_t hpOn, lpOn, steep;         // bool; steep: 24 dB/oct filters (else 12)
    uint8_t gateOn;
    float gateThresholdDb, gateAttackMs, gateReleaseMs, gateRangeDb;
    uint8_t eqOn, compOn, mute, solo;  // bool
    EqBand eq[ANA_EQ_BANDS];
    float compThresholdDb, compRatio, compAttackMs, compReleaseMs, compMakeupDb;
    float faderDb, pan;                // pan -1 (left) .. +1 (right), constant power
};

struct MasterParams {
    uint8_t eqOn, compOn, limiterOn;   // bool
    uint8_t _pad0;
    EqBand eq[ANA_EQ_BANDS];
    float compThresholdDb, compRatio, compAttackMs, compReleaseMs, compMakeupDb;
    float limiterCeilingDb, limiterReleaseMs;
    float faderDb;
};

struct MixMeters {
    float peakDb[ANA_MAX_TRACKS];      // post-fader, held peak of the last block
    float rmsDb[ANA_MAX_TRACKS];
    float compGrDb[ANA_MAX_TRACKS];    // compressor gain reduction (>= 0)
    uint8_t gateOpen[ANA_MAX_TRACKS];  // bool
    float masterPeakDb[2], masterRmsDb[2];
    float masterCompGrDb, limiterGrDb;
    int32_t tracks;
    uint8_t _pad0[4];
};

// ---------------------------------------------------------------- STUDIO offline analysis (studio-plan S1)

enum class PostState : uint8_t { Idle, Running, Done, Failed, Cancelled };

struct PostStatus {
    float progress;                    // 0..1
    PostState state;
    uint8_t _pad0[3];
};

// ---------------------------------------------------------------- functions

enum AnaResult : int32_t { ANA_OK = 0, ANA_ERR_STATE = -1, ANA_ERR_DEVICE = -2, ANA_ERR_ARG = -3, ANA_ERR_IO = -4 };

struct AnalyzerHandle;
struct PlayerHandle;
struct PostHandle;

extern "C" {
ANA_API AnalyzerHandle* ana_create(void);
ANA_API void ana_destroy(AnalyzerHandle* h);
ANA_API const char* ana_last_error(AnalyzerHandle* h);
ANA_API double ana_now(void);                        // monotonic seconds, same clock as publishTimeSeconds
ANA_API void ana_struct_layout(AbiLayout* out);

ANA_API int32_t ana_capture_device_count(AnalyzerHandle* h);
ANA_API int32_t ana_capture_device_name(AnalyzerHandle* h, int32_t index, char* utf8, int32_t cap);

ANA_API int32_t ana_start(AnalyzerHandle* h, const SessionConfig* session, const AudioDeviceConfig* device);
ANA_API int32_t ana_stop(AnalyzerHandle* h);         // flushes REC and open events

ANA_API void ana_read_snapshot(AnalyzerHandle* h, LiveSnapshot* out);   // single reader (UI thread)
ANA_API int32_t ana_drain_events(AnalyzerHandle* h, AnalyzerEvent* out, int32_t cap);

ANA_API int32_t ana_rec_start(AnalyzerHandle* h, const char* wavPathUtf8);  // arms at next bar + count-in
ANA_API int32_t ana_rec_stop(AnalyzerHandle* h);     // finalizes WAV + JSON sidecar
ANA_API int32_t ana_set_metronome(AnalyzerHandle* h, uint8_t on, float bpm, TimeSignature meter);  // refused while REC
ANA_API void ana_clear_clip(AnalyzerHandle* h);

// STUDIO player: its own audio context and output device, independent of the LIVE engine.
ANA_API PlayerHandle* ana_player_create(void);
ANA_API void ana_player_destroy(PlayerHandle* p);
ANA_API const char* ana_player_last_error(PlayerHandle* p);
ANA_API int32_t ana_player_load(PlayerHandle* p, const char* pathUtf8);   // WAV / FLAC / MP3, decoded to memory; opens the output
ANA_API void ana_player_info(PlayerHandle* p, PlayerInfo* out);
ANA_API void ana_player_play(PlayerHandle* p);
ANA_API void ana_player_stop(PlayerHandle* p);
ANA_API void ana_player_seek(PlayerHandle* p, uint64_t frame);
ANA_API void ana_player_set_loop(PlayerHandle* p, uint64_t startFrame, uint64_t endFrame);   // end <= start: off
// Edits: renders the segments (joined with 2 ms crossfades), a fade-in / fade-out over the edited
// take, and a peak normalisation (normalizePeakDbfs <= 0, e.g. -1; > 0 = off). count 0 = the
// original. Playback, peaks, info and save_wav then use the edited take.
ANA_API int32_t ana_player_apply_edits(PlayerHandle* p, const EditSegment* segments, int32_t count, uint64_t fadeInFrames, uint64_t fadeOutFrames,
                                       float normalizePeakDbfs);
ANA_API int32_t ana_player_save_wav(PlayerHandle* p, const char* pathUtf8);   // the edited take as float32 WAV
// S4 mixing. Tracks: 0 = the edited take; add_track appends a stem (decoded to the take's rate and
// length) and returns its index; clear_tracks keeps only track 0. Parameters take effect from the
// next audio block, without blocking playback.
ANA_API int32_t ana_player_add_track(PlayerHandle* p, const char* pathUtf8);
ANA_API void ana_player_clear_tracks(PlayerHandle* p);
ANA_API void ana_channel_defaults(ChannelParams* out);
ANA_API void ana_master_defaults(MasterParams* out);
ANA_API int32_t ana_player_set_channel(PlayerHandle* p, int32_t track, const ChannelParams* params);
ANA_API void ana_player_set_master(PlayerHandle* p, const MasterParams* params);
ANA_API void ana_player_meters(PlayerHandle* p, MixMeters* out);
// Offline renders with the current settings: a track at its analysis tap (post-inserts, pre-fader,
// mono float32 WAV) — what the transcription reads; tracks: a bitmask summed at their taps (e.g.
// the harmony = other + bass); and the bounce of the whole mix through faders and master (stereo).
ANA_API int32_t ana_player_render_tap(PlayerHandle* p, uint32_t trackMask, const char* pathUtf8);
ANA_API int32_t ana_player_bounce(PlayerHandle* p, const char* pathUtf8);
// min/max of the mono mix per column over [startFrame, endFrame), from a peak mipmap (any zoom).
ANA_API int32_t ana_player_peaks(PlayerHandle* p, uint64_t startFrame, uint64_t endFrame, int32_t columns, float* minOut, float* maxOut);

// STUDIO offline analysis: the whole file through session->mode's pipeline at session->quality on a
// worker thread; the result is a take JSON at outJsonUtf8 (same schema as the REC sidecar).
ANA_API PostHandle* ana_post_create(void);
ANA_API void ana_post_destroy(PostHandle* h);   // cancels and joins a running job
ANA_API const char* ana_post_last_error(PostHandle* h);
// flags: ANA_POST_GRID — the file starts on a downbeat of session->bpm / meter (a REC take);
// ANA_POST_BEATS — track the beats instead of transcribing (the drums channel: a "beats" array
// and the global tempo in the JSON, session->bpm as the prior when ANA_POST_GRID is set).
enum { ANA_POST_GRID = 1, ANA_POST_BEATS = 2 };
ANA_API int32_t ana_post_start(PostHandle* h, const SessionConfig* session, double compensationSeconds, const char* inPathUtf8, const char* outJsonUtf8,
                               uint8_t flags);
ANA_API void ana_post_status(PostHandle* h, PostStatus* out);
ANA_API void ana_post_cancel(PostHandle* h);
}
