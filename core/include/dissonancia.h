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
    uint8_t _pad0[2];
};

// ---------------------------------------------------------------- events (§3, §13, §14)

enum class OnsetType : uint8_t { VocalAttack, InstrumentAttack, ChordAttack, NoteChange, Unknown };
enum class ChordQuality : uint8_t {
    Major, Minor, Diminished, Augmented, Sus2, Sus4,
    Power, Dom7, Maj7, Min7, HalfDim7, Dim7, Maj6, Min6, Add9, Unknown
};

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
    uint8_t _pad0[2];
    float confidence;
    uint8_t incomplete, arpeggiated, provisional, bassSettled;   // bool
};

enum class AnalyzerEventType : uint8_t {
    Onset,          // display only
    NoteStart,      // display only (provisional)
    NoteEnd,        // COMPLETE MusicalNoteEvent (start + end)
    ChordConfirmed, // display only (end not yet known)
    ChordEnded      // COMPLETE ChordEvent (start + end)
};

struct AnalyzerEvent {
    AnalyzerEventType type;
    uint8_t _pad0[3];
    uint32_t sequence;                 // +1 per event; a gap = dropped events
    union {
        OnsetEvent onset;
        MusicalNoteEvent note;
        ChordEvent chord;
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
    uint8_t _pad0;
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

// ---------------------------------------------------------------- snapshot (§22)
// Meters, scope, transport, latency, pitch (M1). Chord blocks are added by M3+
// (the layout test keeps C# in sync).

struct LiveSnapshot {
    uint64_t sequence;
    double publishTimeSeconds;         // ana_now() clock, for GUI latency
    double recordedSeconds;
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

// ---------------------------------------------------------------- functions

enum AnaResult : int32_t { ANA_OK = 0, ANA_ERR_STATE = -1, ANA_ERR_DEVICE = -2, ANA_ERR_ARG = -3, ANA_ERR_IO = -4 };

struct AnalyzerHandle;

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
}
