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

// The song's key heard so far (chord modes): every hop's pitch-class energy summed with a slow
// decay (the whole spectrum, not only the recognised chords), matched to key profiles. When the
// session has no key, a valid estimate is the chord recognizer's tonal context: it decides only
// what the audio leaves open, never overrides a clear chord.
struct KeyEstimate {
    uint8_t valid;                     // bool: enough sound heard and a clear margin
    uint8_t minor;                     // bool
    int8_t fifths;                     // like SessionConfig.keyFifths (a minor key: its relative major's)
    int8_t tonic;                      // pitch class, C = 0
    float correlation;                 // with the key's profile, -1..1
    float margin;                      // over the best other key that is not its relative
    float seconds;                     // sound in the sum (effective seconds, after the decay)
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
    BassEstimate melody;               // predominant melody line (chord modes), same fields as the bass
    int8_t voices[8];                  // every line heard (multiple f0), MIDI highest first, -1 = none
    double lastOnsetSeconds;           // sample clock, delay-compensated; < 0 = none yet
    ContextEstimate context;           // tempo heard so far (display only)
    KeyEstimate songKey;               // key heard so far (chord modes)
    char harmonySymbol[16];            // the harmony underneath (major/minor triads, slow), "" = none
    float harmonyLatencyMs;            // a change of harmony -> shown (chordLatencyMs: the same for the chord played)
    uint8_t _pad5[4];
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

// ---------------------------------------------------------------- STUDIO mixing (studio-plan S4, S11)
// A console as a graph. Every track is a strip: its source (polarity) at SAI -> [inserts, patched]
// -> VOLTA -> fader -> pan -> mute / solo -> the master, with AUX (a copy after the fader, a send).
// The inserts are units (trim, filter, gate, EQ, compressor) joined by cables between jacks: a track
// whose SAI has no cable is normalled to its VOLTA; cables into one input sum (a bus), one output on
// several cables is copied (a parallel path, a send), each cable with its own gain. The master: EQ
// -> bus compressor -> fader -> limiter. Rendered ahead of the device in blocks (quality first: 64-bit
// sums and filters, smoothed gains); a change re-renders what has not been played yet.

constexpr int ANA_MAX_TRACKS = 8;       // track 0 = the edited take, 1.. = separated stems
constexpr int ANA_MAX_UNITS = 32;       // units in the mixer graph (the lunchbox's modules)
constexpr int ANA_MAX_CABLES = 96;
constexpr int ANA_SCOPE_FRAMES = 8192;  // stereo frames kept for the STUDIO scopes
constexpr int ANA_EQ_BANDS = 4;

enum class EqType : uint8_t { Peak, LowShelf, HighShelf };

struct EqBand {
    float freqHz, gainDb, q;
    EqType type;
    uint8_t on;                        // bool
    uint8_t _pad0[2];
};

enum class UnitKind : uint8_t { Wire, Trim, Filter, Gate, Eq, Comp };

// One unit of the graph; only its kind's fields are read.
struct UnitParams {
    uint16_t id;                       // the same while the module exists: it keeps its state (filter memory, envelopes) across changes
    UnitKind kind;
    uint8_t on;                        // bool; off = bypassed, a wire
    float trimDb;
    float hpHz, lpHz;
    uint8_t hpOn, lpOn, steep;         // bool; steep: 24 dB/oct filters (else 12)
    uint8_t _pad0;
    float gateThresholdDb, gateAttackMs, gateReleaseMs, gateRangeDb;
    EqBand eq[ANA_EQ_BANDS];
    float compThresholdDb, compRatio, compAttackMs, compReleaseMs, compMakeupDb;
};

// A jack: a track's SAI (out: its source), AUX (out: after its fader), VOLTA (in: its insert
// return, into its fader); a unit's IN / OUT; the master's input. `from` / `to` index the tracks or
// the units.
enum class JackKind : uint8_t { TrackOut, TrackAux, TrackReturn, UnitIn, UnitOut, Master };
struct Cable {
    JackKind fromKind;                 // TrackOut, TrackAux or UnitOut
    uint8_t from;
    JackKind toKind;                   // TrackReturn, UnitIn or Master
    uint8_t to;
    float gain;                        // linear
};

struct TrackParams {
    float faderDb, pan;                // pan -1 (left) .. +1 (right), constant power
    uint8_t mute, solo, polarity;      // bool; polarity: inverted at the source
    uint8_t _pad0;
};

struct MasterParams {
    uint8_t eqOn, compOn, limiterOn;   // bool
    uint8_t _pad0;
    EqBand eq[ANA_EQ_BANDS];
    float compThresholdDb, compRatio, compAttackMs, compReleaseMs, compMakeupDb;
    float limiterCeilingDb, limiterReleaseMs;
    float faderDb;
};

// The whole mix. Cables that close a loop are left out (what they feed is silent).
struct MixGraph {
    TrackParams track[ANA_MAX_TRACKS];
    UnitParams unit[ANA_MAX_UNITS];
    Cable cable[ANA_MAX_CABLES];
    int32_t units, cables;
    MasterParams master;
};

struct MixMeters {
    float peakDb[ANA_MAX_TRACKS];      // post-fader, peak of the block playing
    float rmsDb[ANA_MAX_TRACKS];
    float inDb[ANA_MAX_TRACKS];        // at SAI: the track's source
    float returnDb[ANA_MAX_TRACKS];    // at VOLTA: after its inserts (the analysis tap)
    float unitDb[ANA_MAX_UNITS];       // each unit's output
    float unitGrDb[ANA_MAX_UNITS];     // >= 0: a compressor's gain reduction, a gate's attenuation (0 = open)
    float masterPeakDb[2], masterRmsDb[2];
    float masterCompGrDb, limiterGrDb;
    int32_t tracks;
    uint32_t underruns;                // device periods that came before their audio was rendered
    // Master loudness (BS.1770-4 / EBU R128) since the take was loaded or the meter reset; the
    // momentary and short-term read silent while stopped, integrated and range hold.
    float lufsMomentary, lufsShortTerm, lufsIntegrated, loudnessRange;
    float truePeakDb[2];               // highest since the reset, 4x oversampled
};

// ---------------------------------------------------------------- STUDIO offline analysis (studio-plan S1)

enum class PostState : uint8_t { Idle, Running, Done, Failed, Cancelled };

struct PostStatus {
    float progress;                    // 0..1
    PostState state;
    uint8_t _pad0[3];
};

// ---------------------------------------------------------------- LIVE playground (creation)
// 16 pads (one-shot WAVs), a sampler (one WAV on MIDI notes, repitched from its root), MIDI input,
// and a song: blocks of N bars (a pad pattern in sixteenths, sampler notes, an audio loop recorded
// from the input) on a timeline (block x repeats). Played through the LIVE output (the click output
// must be on) on the metronome's grid: play, a launched block, a block's new length take effect on
// the next bar. REC overdubs the block playing: the audio into its loop (round trip compensated),
// pad hits and notes quantized to the sixteenth, handed to the app (ana_pg_recorded) which owns the
// song and publishes it again. MIDI notes 36..51 hit the pads, every other note plays the sampler;
// every message also reaches the app (ana_midi_read) for MIDI learn.

constexpr int ANA_PADS = 16;
constexpr int ANA_PG_STEPS_PER_BAR = 16;   // a block's pattern: 16 pads x bars x 16 sixteenths (bars of up to 4 beats)

struct PlaygroundConfig {
    float padGainDb[ANA_PADS];
    float padPan[ANA_PADS];            // -1 .. +1
    float samplerGainDb;
    float loopGainDb;                  // every block's audio loop
    float masterDb;
    uint8_t samplerRoot;               // the MIDI note the sampler's WAV sounds at
    uint8_t _pad0[3];
};

struct PgBlock {
    uint32_t id;                       // the app's: names the block's audio loop
    uint32_t bars;                     // 1 .. 64
    uint32_t stepOffset;               // its pattern in `steps`: [pad][bar * 16 + sixteenth], velocity 0 = off
    uint32_t noteOffset, noteCount;    // its sampler notes in `notes`
};
struct PgNote {
    uint16_t step;                     // sixteenths from the block's start
    uint16_t length;                   // sixteenths
    uint8_t note, velocity;
    uint8_t _pad0[2];
};
struct PgEntry {
    int32_t block;                     // index into the blocks
    int32_t repeats;                   // >= 1
};
// What REC caught (pads and notes; the audio goes straight into the loop).
struct PgRecorded {
    uint32_t blockId;
    uint16_t step, length;             // length: notes only
    uint8_t kind;                      // 0 pad, 1 sampler note
    uint8_t key;                       // the pad, or the note
    uint8_t velocity;
    uint8_t _pad0;
};

// The pedalboard: pedals (stompboxes) patched with cables between jacks, in the playground's
// callback. Jacks: 0 ENTRADA (out: the input), 1 PADS (out: pads + sampler), 2 SAÍDA (in: to the
// LIVE output), 3 GRAVAR (in: what REC puts in a block's loop); pedal k: 8 + 2k its IN, 9 + 2k its
// OUT. Normalled: GRAVAR unpatched records the input dry; PADS unpatched goes to the output as is;
// nothing of the input reaches the output unless a cable takes it there (no feedback by surprise).
// Cables into one jack sum, one output on several cables is copied; a loop is left silent.
constexpr int ANA_PEDALS = 16;
constexpr int ANA_PEDAL_CABLES = 48;
enum class PedalKind : uint8_t { Drive, Octave, Chorus, Delay, Reverb, Filter };
struct PedalParams {
    uint16_t id;                       // the same while the pedal exists: it keeps its state
    PedalKind kind;
    uint8_t on;                        // bool; off = true bypass
    float knob[4];                     // 0..1 each; what they are depends on the kind (see the app)
};
struct PedalCable {
    uint8_t from, to;                  // jack ids (above)
    uint8_t _pad0[2];
    float gain;                        // linear
};
struct Pedalboard {
    PedalParams pedal[ANA_PEDALS];
    PedalCable cable[ANA_PEDAL_CABLES];
    int32_t pedals, cables;
};

struct PlaygroundStatus {
    uint32_t hits[ANA_PADS + 1];       // triggers so far ([16]: sampler notes), for the pads' flash
    float seconds[ANA_PADS + 1];       // loaded length, 0 = empty
    float peakDb;                      // the playground's output since the last read
    int32_t voices;
    uint32_t midiMessages;             // received from the MIDI input since it was opened
    int32_t entry;                     // the timeline entry playing, -1 = stopped
    int32_t repeat, bar;               // its pass (0 based) and bar in the block
    int32_t step;                      // the sixteenth in the bar, -1 = stopped
    int32_t queued;                    // the entry that starts on the next bar, -1 = none
    uint8_t playing, recording, looping, starting;   // bool; starting: waits for the next bar
    float inputDb, outputDb;           // the pedalboard's ENTRADA and SAÍDA since the last read
    float pedalDb[ANA_PEDALS];         // each pedal's output since the last read
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
// The analysed mono input (device rate, snapshot.sampleRate) since the last call, oldest first;
// samples are dropped while nobody reads. Single reader (the GUI's note transcriber).
ANA_API int32_t ana_read_audio(AnalyzerHandle* h, float* out, int32_t cap);

ANA_API int32_t ana_rec_start(AnalyzerHandle* h, const char* wavPathUtf8);  // arms at next bar + count-in
ANA_API int32_t ana_rec_stop(AnalyzerHandle* h);     // finalizes WAV + JSON sidecar
ANA_API int32_t ana_set_metronome(AnalyzerHandle* h, uint8_t on, float bpm, TimeSignature meter);  // refused while REC
ANA_API void ana_clear_clip(AnalyzerHandle* h);

// LIVE playground. Load needs a running engine (its rate); slot ANA_PADS = the sampler; "" empties.
ANA_API int32_t ana_pad_load(AnalyzerHandle* h, int32_t slot, const char* wavPathUtf8);
ANA_API void ana_pad_hit(AnalyzerHandle* h, int32_t pad, float velocity);
// A MIDI message as if from the controller (the app's keyboard, tests).
ANA_API void ana_midi_send(AnalyzerHandle* h, uint8_t status, uint8_t data1, uint8_t data2);
ANA_API void ana_playground_set(AnalyzerHandle* h, const PlaygroundConfig* c);
ANA_API void ana_playground_status(AnalyzerHandle* h, PlaygroundStatus* out);
ANA_API int32_t ana_pedalboard_set(AnalyzerHandle* h, const Pedalboard* board);   // heard from the next callback
// The song (copied): heard from the next bar. steps: per block 16 x bars x 16 velocities.
ANA_API int32_t ana_pg_song(AnalyzerHandle* h, const PgBlock* blocks, int32_t blockCount, const uint8_t* steps, int32_t stepCount,
                            const PgNote* notes, int32_t noteCount, const PgEntry* order, int32_t orderCount);
// From the next bar: the timeline at `entry`, looping that entry's block (loop) or going on through
// the timeline (and round again). Stop: at once.
ANA_API void ana_pg_play(AnalyzerHandle* h, int32_t entry, uint8_t loop);
ANA_API void ana_pg_stop(AnalyzerHandle* h);
ANA_API int32_t ana_pg_record(AnalyzerHandle* h, uint8_t on);   // overdub; makes room for the loops of every block
ANA_API int32_t ana_pg_recorded(AnalyzerHandle* h, PgRecorded* out, int32_t cap);
// A block's audio loop: to a WAV (save 1) or from one (save 0); "" with save 0 clears it.
ANA_API int32_t ana_pg_loop_file(AnalyzerHandle* h, uint32_t blockId, const char* pathUtf8, uint8_t save);
// The timeline once (every entry, its repeats) offline at the metronome's tempo, to a stereo WAV.
ANA_API int32_t ana_pg_bounce(AnalyzerHandle* h, const char* pathUtf8);
// MIDI input ports (RtMidi; 0 when built without it or none present). open: -1 closes.
ANA_API int32_t ana_midi_port_count(AnalyzerHandle* h);
ANA_API int32_t ana_midi_port_name(AnalyzerHandle* h, int32_t index, char* utf8, int32_t cap);
ANA_API int32_t ana_midi_open(AnalyzerHandle* h, int32_t index);
// Every message received (status | data1 << 8 | data2 << 16), oldest first, since the last read.
ANA_API int32_t ana_midi_read(AnalyzerHandle* h, uint32_t* out, int32_t cap);


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
// STUDIO S8: a track's pre-chain source — the channel's audio rendered through its VST3 inserts by
// the host helper (offline), decoded to the take's length; NULL or "" restores the original. Load,
// edits and clear_tracks drop every source (they were rendered from other audio).
ANA_API int32_t ana_player_set_source(PlayerHandle* p, int32_t track, const char* pathUtf8);
ANA_API void ana_unit_defaults(UnitParams* out);     // every field's default, a wire
ANA_API void ana_master_defaults(MasterParams* out);
// The whole mix (copied): heard from the first block not yet played, ~50 ms (the rendered audio
// after it is rendered again). ANA_ERR_ARG: a count or index out of range.
ANA_API int32_t ana_player_set_mix(PlayerHandle* p, const MixGraph* mix);
ANA_API void ana_player_meters(PlayerHandle* p, MixMeters* out);
// Offline renders with the current mix: tracks at their analysis tap (VOLTA: after the inserts,
// before the fader; mono float32 WAV), a bitmask summed — what the transcription reads (e.g. the
// harmony = other + bass); and the bounce of the whole mix through faders and master (stereo).
ANA_API int32_t ana_player_render_tap(PlayerHandle* p, uint32_t trackMask, const char* pathUtf8);
// A file through units in series (stereo float32 WAV at the file's rate): the units patched before
// a VST3 insert, rendered so the insert can read them.
ANA_API int32_t ana_render_chain(const char* inUtf8, const UnitParams* units, int32_t count, const char* outUtf8);
ANA_API int32_t ana_player_bounce(PlayerHandle* p, const char* pathUtf8);
// min/max of the mono mix per column over [startFrame, endFrame), from a peak mipmap (any zoom).
// The latest output for the scopes (stereo, interleaved, oldest first): source 0 = the master, 1 =
// the monitored track at its analysis tap. Returns the frames written (<= frames, ANA_SCOPE_FRAMES).
ANA_API int32_t ana_player_scope(PlayerHandle* p, int32_t source, float* stereoOut, int32_t frames);
ANA_API void ana_player_set_monitor(PlayerHandle* p, int32_t track);
ANA_API void ana_player_reset_loudness(PlayerHandle* p);
// Pulls `frames` through the mixer as the output device would and discards them: meters and scopes
// without a device (screenshots, tests). Not for use while a device is open.
ANA_API void ana_player_pull(PlayerHandle* p, int32_t frames);
ANA_API int32_t ana_player_peaks(PlayerHandle* p, uint64_t startFrame, uint64_t endFrame, int32_t columns, float* minOut, float* maxOut);

// STUDIO offline analysis: the whole file through session->mode's pipeline at session->quality on a
// worker thread; the result is a take JSON at outJsonUtf8 (same schema as the REC sidecar).
ANA_API PostHandle* ana_post_create(void);
ANA_API void ana_post_destroy(PostHandle* h);   // cancels and joins a running job
ANA_API const char* ana_post_last_error(PostHandle* h);
// flags: ANA_POST_GRID — the file starts on a downbeat of session->bpm / meter (a REC take);
// ANA_POST_BEATS — track the beats instead of transcribing (the drums channel: a "beats" array
// and the global tempo in the JSON, session->bpm as the prior when ANA_POST_GRID is set).
// ANA_POST_CHOIR — the choir by section (EXPERIMENTAL): multiple-f0 and SATB assignment, notes
// with "part": "S" / "A" / "T" / "B", spelled as sounding.
enum { ANA_POST_GRID = 1, ANA_POST_BEATS = 2, ANA_POST_CHOIR = 4 };
ANA_API int32_t ana_post_start(PostHandle* h, const SessionConfig* session, double compensationSeconds, const char* inPathUtf8, const char* outJsonUtf8,
                               uint8_t flags);
ANA_API void ana_post_status(PostHandle* h, PostStatus* out);
ANA_API void ana_post_cancel(PostHandle* h);
}
