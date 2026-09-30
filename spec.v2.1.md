# PROMPT V2.1 — REAL-TIME OFFLINE MUSIC ANALYZER (single specification)

You are a senior engineering team: audio DSP, MIR (Music Information Retrieval),
Western music theory, real-time C++ (C++20), GUI, and WebAssembly.

## 0. GOAL AND PHILOSOPHY

A 100% offline application (no cloud, no external APIs, no neural networks in
v1) that receives audio from microphone/interface/file and produces:

1. Sung voice pitch: Hz, MIDI note, cents deviation, confidence.
2. Guitar/piano notes and chords: notes, bass, inversion, chord symbol.
3. Live visualization of all the above + key + tuning.
4. Post-processing: melody score, chord chart score, functional Roman-numeral
   analysis, probable cadences, MusicXML/MIDI/JSON/text export.

Non-negotiable rules:
- Never fake certainty. Insufficient evidence → alternatives, reduced
  confidence, or "unknown".
- Do not solve everything with one technique. Voice and polyphony use
  independent pipelines, sharing only pre-processing, CQT, and music theory.
- Strictly separate live (provisional, cheap, deterministic, zero-allocation)
  from offline (accurate, expensive, post-processed).
- Measure, never promise. Every reported latency/CPU figure must come from
  measurement.
- If a requirement conflicts with physics (e.g., time-frequency resolution,
  filter group delay), physics wins. Report the conflict; do not pretend to
  implement it.
- Zero dynamic allocation (`malloc`, `new`, `std::vector` resize, `std::string`)
  in the audio callback AND in the steady-state loop of `RealtimeAnalysisThread`.

## 1. MANDATORY STACK

| Component       | Choice                                                      |
|-----------------|-------------------------------------------------------------|
| Language / Std  | C++20 (`-std=c++20`, concepts, `std::span`, atomic fences)  |
| Capture         | miniaudio (single-header, WASM-ready)                       |
| Files           | dr_wav, dr_flac, stb_vorbis                                 |
| FFT             | PocketFFT or pffft (avoid FFTW/GPL)                         |
| CQT             | Octave-decimated filter bank, sparse CSR kernels (~1–3 MB)  |
| Lock-free IPC   | `readerwriterqueue` (SPSC) + Lock-free Triple-Buffering     |
| SIMD Layer      | Portable vector abstraction (AVX2, ARM NEON, WASM simd128)  |
| GUI             | Dear ImGui + OpenGL/GLES                                    |
| Score           | Verovio (C++, compiles to WASM, debounced in worker/task)   |
| Testing         | Catch2 + synthetic WAVs + CI allocation trap & microbench   |
| WASM            | Emscripten, -O3 -msimd128, `-fno-exceptions -fno-rtti`      |

The DSP core does not depend on the GUI, performs no I/O, is deterministic,
float32 throughout, and aligns all computational buffers to 64 bytes (`alignas(64)`).

## 2. ARCHITECTURE

```
Audio Hardware / File
       │
       ▼ (audio callback: strictly lock-free, zero alloc, zero I/O)
[ SPSC Ring Buffer (readerwriterqueue) ]
       │
       ▼ (pops audio blocks)
[ RealtimeAnalysisThread ]
 ├─ Pre-processing (downsampling FIR, DC removal, VAD, RMS/Peak)
 ├─ Pipeline A: Monophonic Voice (YIN: SIMD time-domain or FFT autocorrelation)
 └─ Pipeline B: Polyphony
     └─ Multi-resolution Octave-Decimated CQT
         ├─ Spectral flux (onsets)
         ├─ Harmonic peaks & inharmonicity correction
         ├─ Bass & inversion tracker
         ├─ NNLS-chroma (bounded iterations)
         └─ Chord templates & Temporal tracker (preview vs confirmed)
       │
       ▼ (publishes via lock-free triple buffer, zero allocation)
[ Triple-Buffer Snapshot ]
       │
       ▼ (reads latest complete snapshot at ≤ 30 Hz)
[ UIThread (Dear ImGui) ]

End of recording / File Mode:
[ OfflineAnalysisThread ] (full reprocessing, multi-pass Viterbi, exact bass)
       │
       ▼
[ ExportThread ] (MusicXML / MIDI / JSON / Text / Verovio Score generation)
```

Operating modes: VOICE / CHORDS / FILE. The COMBINED mode (voice + instrument)
is experimental and deferred to v2 (section 21).

## 3. THREADS AND REAL TIME

Threads: `AudioCallback`, `RealtimeAnalysisThread`, `UIThread`, `OfflineAnalysisThread`, `ExportThread`.

The audio callback NEVER: allocates, blocks on a mutex, performs I/O, prints,
logs, updates widgets, runs CQT or analysis. It only moves samples into the
SPSC ring buffer and increments atomic counters.

The `RealtimeAnalysisThread`:
- Executes on a dedicated thread with elevated priority (below audio callback).
- Pre-warms/touches all static buffers and arenas during initialization (preventing page faults).
- Publishes immutable POD snapshots to the `UIThread` using a **Triple-Buffering Lock-Free**
  protocol with `std::atomic<uint32_t>` and acquire-release memory ordering. Zero mutexes,
  zero allocations per published frame.

Mandatory verification (not optional):
- In Debug/CI, global `operator new` and `malloc` become traps: any allocation on
  the audio callback OR inside the steady-state loop of `RealtimeAnalysisThread`
  fails the test.
- Memory page faults and xrun/underrun counters exposed live in the GUI.
- Capture/processing/GUI/confirmation latencies measured and displayed.

## 4. BUDGETS — ACCEPTANCE CRITERIA [MEASURE]

| Item | Target |
|---|---|
| Capture latency (interface buffer + ring) | ≤ 20 ms |
| Voice end-to-end latency (stable note on GUI, LowLatency) | ≤ 150 ms |
| Chord preview | instant (every hop) with `bassSettled` flag |
| Chord confirmation (hysteresis) | 0.4–1 s — DOCUMENTED behavior, not a bug |
| Total live CPU (Balanced, modest machine) | ≤ 15% of one core |
| LowLatency CPU | ≤ 10% of one core |
| Audio callback execution time | ≤ 1 ms per 10 ms block |
| GUI update rate | snapshot read at 30 Hz, never per audio block |
| Verovio score render | Debounced ~250 ms in worker/secondary thread |
| xruns in 10 min | 0 |

CQT physics (law, not preference): window $T \approx Q/f$, with $Q \approx 1.443 \cdot \text{binsPerOctave}$.
Filter group delay $\tau_g \approx T/2$.

| f (Hz) | 12 bpo (T / $\tau_g$) | 24 bpo (T / $\tau_g$) | 36 bpo (T / $\tau_g$) |
|--------|-----------------------|-----------------------|-----------------------|
| 27.5   | 630 ms / 315 ms       | 1.26 s / 630 ms       | 1.9 s / 950 ms        |
| 55     | 315 ms / 158 ms       | 630 ms / 315 ms       | 945 ms / 472 ms       |
| 82.4   | 210 ms / 105 ms       | 420 ms / 210 ms       | 630 ms / 315 ms       |
| 165    | 105 ms / 52 ms        | 210 ms / 105 ms       | 315 ms / 158 ms       |
| 440    | 39 ms / 20 ms         | 79 ms / 40 ms         | 118 ms / 59 ms        |

Mandatory consequences:
- LIVE: minimum chord frequency $\ge 80\text{–}100\text{ Hz}$. $55\text{ Hz}$ only if the user accepts
  the physical latency. $27.5\text{ Hz}$ NEVER live — offline only.
- Instant chord preview: When an onset occurs, report preview candidate immediately from
  mid/high bins, but set `bassSettled = false` and reduced confidence for the first $150\text{–}200\text{ ms}$
  until the low-frequency filters accumulate steady-state energy. This prevents UI chattering/flicker.
- Multi-resolution CQT: Octave decimation filter bank (long windows in lows, short in highs).
- "Chord latency" = window of lowest active bin + hop + hysteresis. Report MEASURED numbers.

## 5. PROFILES AND SAMPLE RATES

```cpp
enum class AnalysisProfile : uint8_t {
    VoiceMonophonic, InstrumentMonophonic,
    PolyphonicAcousticGuitar, PolyphonicElectricGuitar, PolyphonicPiano,
    GeneralPolyphonic, VoiceAndChord, Automatic
};

enum class AudioQuality : uint8_t { LowLatency, Balanced, HighPrecision };
```

Rates via INTEGER division of the device's native rate (never an arbitrary-ratio resampler on the live path):
- Voice: native/3 ($48\text{k} \rightarrow 16\text{k}$) or native/2 ($44.1\text{k} \rightarrow 22.05\text{k}$).
- Chords economy/live: native/2 ($48\text{k} \rightarrow 24\text{k}$, $44.1\text{k} \rightarrow 22.05\text{k}$).
- Piano/precision: native rate.

Anti-Aliasing Filter Requirement:
Integer downsampling must use linear-phase FIR (or minimum-phase FIR with known group delay compensation)
to prevent nonlinear phase distortion that warps waveform symmetry and corrupts YIN minima.

Detect the device's real rate; never assume the requested one was accepted.

| Profile            | Rate      | Window     | Hop        | Algorithm             | Range          |
|--------------------|-----------|------------|------------|-----------------------|----------------|
| VOICE_FAST         | 16k       | 40–50 ms   | 10 ms      | YIN (SIMD time-domain)| 70–1200 Hz     |
| VOICE_ACCURATE     | 22.05k    | 60–80 ms   | 10–15 ms   | YIN (FFT auto-corr)   | 50–2000 Hz     |
| CHORD_FAST         | native/2  | 100–140 ms | 20 ms      | CQT 12 bpo (decimated)| 82–4200 Hz     |
| CHORD_BALANCED     | native/2  | 150–200 ms | 20 ms      | CQT 24 bpo (decimated)| 80–4200 Hz     |
| CHORD_ACCURATE     | native    | 200–300 ms | 20–40 ms   | CQT 24–36 bpo         | 55–4200 Hz     |
| PIANO (offline ok) | native    | multi-res  | 20–40 ms   | CQT 24–36 bpo         | 27.5–4186 Hz   |

The Automatic profile only suggests; the user can always set it manually.

## 6. PRE-PROCESSING (shared by all pipelines)

Mono (sum / channel selection / optional difference), DC removal, configurable
high-pass and low-pass (voice HP 50–70 Hz; polyphony LP 8–12 kHz; piano with no
aggressive filtering), optional normalization, noise gate, RMS, peak,
clipping/saturation detection, noise-floor estimation, simple VAD (energy +
periodicity). Do not destroy transients.

```cpp
struct alignas(64) AudioFrame {
    const float* samples;
    std::size_t sampleCount;
    double sampleRate;
    double timestampSeconds;
    float rms;
    float peak;
    bool clipped;
    bool silent;
};
```

## 7. PIPELINE A — VOICE (YIN)

Steps: Hann window → squared difference $d(\tau)$ → accumulated normalized CMND →
first minimum below threshold (0.10–0.20) → parabolic interpolation → range
validation → confidence/clarity → reject unstable estimates and false octaves
(temporal validation). Alternatives: normalized ACF, MPM. When two estimators
disagree, reduce confidence instead of picking arbitrarily.

Algorithmic execution strategy:
- `VOICE_FAST` ($W \le 800$ samples @ 16 kHz): Time-domain difference $d(\tau)$
  vectorized with 128-bit / 256-bit SIMD. Avoids FFT transform overhead.
- `VOICE_ACCURATE` ($W \ge 1320$ samples @ 22.05 kHz): FFT-based autocorrelation
  $d(\tau) = r_t(0) + r_{t+\tau}(0) - 2r_t(\tau)$ via PocketFFT/pffft to bound
  complexity to $O(N \log N)$.

Real-time POD Data Structures (Zero Dynamic Allocation):

```cpp
struct PitchEstimate {
    bool voiced;
    float frequencyHz;
    float midiFloat;
    float confidence;
    float clarity;
    float rms;
    double timestampSeconds;
};

// Intonation conversion (configurable A4, default 440.0f):
// midiFloat = 69.0f + 12.0f * log2f(f / A4)
// cents = 1200.0f * log2f(f / expectedHz)

struct NoteEstimate {
    bool valid;
    int8_t midi;
    int8_t octave;
    char writtenName[8];       // POD fixed-size buffer, e.g. "C#4", "Bb3"
    float detectedHz;
    float expectedHz;
    float cents;
    float confidence;
    bool chromatic;
    bool diatonic;
};

// Melodic tracking: Silence/Candidate/Stable/Transition/Releasing
struct MusicalNoteEvent {
    double startTimeSeconds;
    double endTimeSeconds;
    double durationSeconds;
    float avgHz;
    float medianHz;
    float avgCents;
    int8_t midi;
    char writtenName[8];
    float confidence;
    bool rest;
    bool tie;
    bool chromatic;
    bool vibrato;
};
```

Melodic tracking: states Silence/Candidate/Stable/Transition/Releasing; temporal
median; hysteresis; attack/release/rest; repeated note requires an onset;
vibrato NEVER creates a new note (instantaneous pitch for the GUI, stabilized
pitch for the score); glissando and octave jumps validated temporally.

## 8. MULTI-RESOLUTION CQT (shared)

Architecture: Octave-Decimated Filter Bank (Schörkhuber & Klapuri / Brown-Puckette).
- Rather than running a giant FFT over all octaves, only the topmost octave is
  analyzed at full rate. Lower octaves are successively decimated by factor 2
  using a low-order linear-phase FIR anti-aliasing filter.
- Keeps FFT sizes constant and small ($N \le 2048$), reducing compute by over
  $4\times$, meeting the $\le 15\%$ CPU target.
- Sparse spectral kernels precomputed in CSR (Compressed Sparse Row) format,
  aligned to 64 bytes (`alignas(64)`), Structure of Arrays (SoA) layout.

```cpp
struct CQTConfig {
    double sampleRate;
    float minFrequency;
    float maxFrequency;
    uint16_t binsPerOctave;
    uint16_t numberOfBins;
    uint16_t hopSize;
    bool useLogMagnitude;
    bool useTuningCorrection;
};

struct alignas(64) CQTFrame {
    float* magnitude;          // Preallocated contiguous buffer (size: numberOfBins)
    float* phase;              // Preallocated contiguous buffer (size: numberOfBins)
    double timestampSeconds;
    float tuningOffsetCents;
    float confidence;
    bool bassSettled;          // False during the first ~150ms after onset
};
```

Implementation order: (1) reference → (2) precomputed sparse CSR kernels →
(3) octave decimation filter bank → (4) SIMD (AVX2/NEON/simd128). One pipeline
feeds chroma, peaks, bass AND onset flux (spectral flux over CQT magnitudes is
nearly free).

Global tuning estimation: median of deviations of stable peaks vs the tempered
grid, outlier rejection, A4 with optional lock. Never change the global tuning
based on a single note.

```cpp
struct TuningEstimate {
    bool valid;
    float referenceA4;
    float offsetCents;
    float confidence;
};
```

## 9. CHROMA + NNLS

```cpp
struct ChromaVector {
    alignas(16) std::array<float, 12> raw;
    alignas(16) std::array<float, 12> normalized;
    alignas(16) std::array<float, 12> smoothed;
    double timestampSeconds;
    float confidence;
    float tuningOffsetCents;
};
```

Inter-octave aggregation → logarithmic compression → normalization → temporal
smoothing → adaptive threshold.

NNLS-chroma Real-Time Determinism:
- Lawson-Hanson iterative NNLS has unbounded convergence time, creating frame jitter.
- For real-time execution, use a **Bounded Accelerated Projected Gradient Descent (PGD)**
  or active-set solver with hard-capped iterations ($\le 12$ iterations) in float32 SIMD.
- Chroma alone gives neither octave nor fundamental — combine with CQT peaks + bass.

## 10. POLYPHONIC NOTES

```cpp
struct PolyphonicNote {
    int8_t midi;
    int8_t pitchClass;
    float frequencyHz;
    float cents;
    float energy;
    float normalizedEnergy;
    float confidence;
    bool likelyFundamental;
    bool likelyHarmonic;
};

constexpr size_t MAX_POLYPHONIC_NOTES = 16;
struct PolyphonicNoteSnapshot {
    uint8_t count;
    std::array<PolyphonicNote, MAX_POLYPHONIC_NOTES> notes;
};
```

Local maxima + parabolic interpolation + grouping + neighboring-peak
suppression. Harmonics 2×–6×: a peak explainable as a partial of a lower
fundamental loses weight as an independent note — but is NOT automatically
discarded (real fundamentals coexist with harmonics). Piano: INHARMONIC
(stretched) partials ($f_n = n f_0 \sqrt{1 + B n^2}$) — suppression must tolerate
increasing cents deviation in upper partials. Per-note confidence, always.

## 11. BASS

```cpp
struct BassEstimate {
    bool valid;
    int8_t midi;
    int8_t pitchClass;
    float frequencyHz;
    float confidence;
    bool settled;              // False if group delay window has not fully filled
};
```

Ranges: guitar 70–500 Hz; piano 27.5–600 Hz (offline). Low CQT + temporal
persistence + priority to fundamentals.
Hard rule: uncertain bass → do NOT report an inversion; display the
root-position chord with reduced confidence.

## 12. CHORDS — TEMPLATES, MATCHING, AMBIGUITY

Templates (pre-transposed: 15 qualities × 12 roots = 180 vectors; matching ≈
2k MACs/frame — negligible):

```
major 0,4,7 | minor 0,3,7 | dim 0,3,6 | aug 0,4,8 | sus2 0,2,7 | sus4 0,5,7
power 0,7 | dom7 0,4,7,10 | maj7 0,4,7,11 | min7 0,3,7,10 | halfDim7 0,3,6,10
dim7 0,3,6,9 | maj6 0,4,7,9 | min6 0,3,7,9 | add9 0,4,7,14
```

Score per candidate: energy of expected notes, missing notes, foreign notes,
third, fifth, root, bass, tonal context, temporal stability → ranked candidates.

Real-time POD Data Structures (Zero Heap Allocation):

```cpp
enum class ChordQuality : uint8_t {
    Major, Minor, Diminished, Augmented, Sus2, Sus4,
    Power, Dom7, Maj7, Min7, HalfDim7, Dim7, Maj6, Min6, Add9, Unknown
};

struct ChordCandidate {
    int8_t rootPitchClass;
    int8_t bassPitchClass;
    ChordQuality quality;
    char symbol[16];           // e.g. "Cmaj7/E", "Am7"

    uint8_t expectedCount;
    uint8_t detectedCount;
    uint8_t missingCount;
    uint8_t extraCount;
    std::array<int8_t, 8> expected;
    std::array<int8_t, 8> detected;
    std::array<int8_t, 8> missing;
    std::array<int8_t, 8> extra;

    float rootScore;
    float thirdScore;
    float fifthScore;
    float chromaScore;
    float bassScore;
    float temporalScore;
    float tonalScore;
    float totalScore;
    float confidence;
    bool hasBass;
    bool incomplete;
    bool arpeggiated;
};

constexpr size_t MAX_CHORD_ALTERNATIVES = 3;
struct ChordRecognitionResult {
    ChordCandidate best;
    uint8_t alternativeCount;
    std::array<ChordCandidate, MAX_CHORD_ALTERNATIVES> alternatives;
    bool ambiguous;
    char explanation[64];      // e.g. "missing third", "C6 vs Am7"
};
```

Ambiguities that must NEVER be forced: C6 ≡ Am7; missing third → do not report
major/minor; power chord carries no third; an inversion can look like another
chord; extensions can look like basics; a passing tone creates false chords.
The GUI displays the reason: "Likely chord: C | conf. 0.64 | alternatives:
Am/C, F6 | reason: missing or unreliable third".

Guitar: standard tuning (E2 A2 D3 G3 B3 E4), Drop D, DADGAD, Eb, D, custom,
capo, variable string count. String/voicing constraints only TIE-BREAK
candidates — never replace audio analysis. Handle pick noise, muted strings,
distortion, arpeggios, incomplete chords.

Piano: A0–C8, high polyphony, decay (distinguish sustained from re-attacked
via onset), octave doublings, strong harmonics + inharmonicity (section 10),
no string constraints.

Arpeggios: accumulation window 300–600 ms live / 500–900 ms balanced /
adaptive offline. Notes of the same chord close in time accumulate evidence.
`arpeggiated` flag. Never require simultaneous notes.

## 13. CHORD TRACKER

States: Silence → Candidate → Confirmed → Transition → Released.

Tools: mean/median/EMA, hysteresis, accumulated score, change cost, minimum
duration, minimum confidence, attack detection, chord-end detection.
Do NOT change chord because of: a passing tone, attack noise, one harmonic,
one bad frame, a short extension.

```cpp
struct ChordEvent {
    double startTimeSeconds;
    double endTimeSeconds;
    double durationSeconds;
    char symbol[16];
    int8_t rootPitchClass;
    int8_t bassPitchClass;
    ChordQuality quality;
    int8_t inversion;

    uint8_t detectedCount;
    uint8_t missingCount;
    std::array<int8_t, 8> detectedNotes;
    std::array<int8_t, 8> missingNotes;

    float confidence;
    bool incomplete;
    bool arpeggiated;
    bool provisional;          // true during live preview; false upon confirmation
    bool bassSettled;          // false if low-frequency filters still in transient lag
};
```

Default GUI behavior: instant per-frame preview (`provisional = true`) + later
confirmation (`provisional = false`). This is expected system behavior.

## 14. ONSETS

Spectral flux over CQT magnitudes (reused, marginal cost), chroma flux, RMS
attack, pitch change.

```cpp
enum class OnsetType : uint8_t {
    VocalAttack, InstrumentAttack, ChordAttack, NoteChange, Unknown
};

struct OnsetEvent {
    double timestampSeconds;
    float strength;
    OnsetType type;
};
```

Uses: separate repeated notes, detect arpeggios, quantization, segmentation.

## 15. MUSIC THEORY — KEY, KEY SIGNATURE, SPELLING

Key: live → fixed by the user; offline → estimated (Krumhansl-Schmuckler over
aggregated chroma) with alternatives and uncertainty marking. v1: major and
natural minor. Minor analysis ACCEPTS raised leading tone (major V, vii°),
borrowed chords, and secondary dominants — never assume natural minor only.
Modulation: marked as a per-section candidate, never automatic.

Key signatures — sharps: F#, C#, G#, D#, A#, E#, B#. Flats: Bb, Eb, Ab, Db, Gb, Cb, Fb.

```cpp
enum class KeyMode : uint8_t {
    Major, NaturalMinor, HarmonicMinor, MelodicMinor, Dorian, Phrygian, Mixolydian
};

enum class DiatonicStatus : uint8_t {
    Diatonic, ChromaticPassing, ChromaticNeighbor,
    AppliedDominant, BorrowedChord, PossibleModulation, Unknown
};

struct KeySignature {
    int8_t tonicPitchClass;
    KeyMode mode;
    int8_t accidentalCount;
    bool prefersSharps;
    uint8_t scaleCount;
    std::array<int8_t, 7> scalePitchClasses;
    std::array<const char*, 7> keyAccidentals;
};
```

An out-of-scale note is NEVER an automatic error — classify via `DiatonicStatus`.

Spelling: internally MIDI/pitch class/Hz/cents (C# ≡ Db). External spelling by
priority: key > key signature > explicit chord symbol > melodic direction >
neighboring notes. Never switch sharps/flats arbitrarily.

## 16. CHORD SYMBOLS

Separate line from the melody: `| C | G | Am | F |` or `| Cmaj7 | Dm7 | G7 | Cmaj7 |`.
Support root, quality, extensions, inversion (C/E, G/B, D/F#), incomplete
chords, context-aware spelling from the key.

## 17. FUNCTIONAL ANALYSIS AND CADENCES

Roman numerals: uppercase = major, lowercase = minor, ° dim, + augmented,
7, 6, inversions (figured bass), secondary dominants (V/x), modal borrowing,
tonicization. Major: I ii iii IV V vi vii°. Minor: i ii° III iv V VI vii°
(major V via raised leading tone).

Cadences: perfect authentic, imperfect authentic, half, plagal,
deceptive/interrupted, Phrygian (minor, optional), sequence without cadence,
ambiguous.

Initial rules:
- Authentic: V(7)→I. Evidence to check: root motion 5→1, bass position, metric
  position of final chord, final melodic note (tonic or leading tone), phrase
  end (onset gap + energy drop), final chord duration.
- Imperfect authentic: V→I with melody ending on 3rd or 5th.
- Half: phrase ending on V. Plagal: IV→I with metric evidence.
- Deceptive: V→vi (major) or V→VI (minor).

Output ALWAYS with confidence + list of textual evidence. Never state a
definitive cadence without metric and melodic context.

## 18. SOLFÈGE

Two separate layers:
(a) Musical representation (movable/fixed do, scale degrees 1–7, accidentals) —
trivial over the theory layer, v1.5.
(b) Recognition of SUNG syllables ("do", "re"...) — v2, explicitly out of v1.
Reason: distinguishing syllables is vowel/formant classification, NOT pitch;
pitch alone never solves it.

```cpp
struct SolfegeSyllable {
    char text[8];
    int8_t scaleDegree;
    int8_t chromaticAlteration;
    double startTimeSeconds;
    double endTimeSeconds;
    float confidence;
};
```

## 19. RHYTHM AND QUANTIZATION

ALWAYS keep two timelines: observed real time and quantized time (user toggle).
Durations: whole note through sixteenth + triplets (other tuplets later). BPM
via onset autocorrelation, chord-interval spacing, tap tempo, or manual.
Do not quantize vibrato-heavy sung notes aggressively.

## 20. SCORE AND EXPORT (Offline / Post-Processing Layer)

Melody score and harmonic score are INDEPENDENT structures.
Unlike the real-time layer, heap-allocated dynamic containers (`std::vector`, `std::string`)
are strictly localized here (within `OfflineAnalysisThread` and `ExportThread`).

```cpp
struct TimeSignature { uint8_t numerator, denominator; };
enum class Clef : uint8_t { Treble, Bass, Alto, Tenor };

struct MelodyScore {
    KeySignature key;
    TimeSignature meter;
    double bpm;
    Clef clef;
    std::vector<MusicalNoteEvent> events;
    std::vector<SolfegeSyllable> solfege;
};

struct ChordScore {
    KeySignature key;
    TimeSignature meter;
    double bpm;
    std::vector<MusicalNoteEvent> melody;
    std::vector<ChordEvent> chords;
};
```

Export: MusicXML (own writer, validated by opening in MuseScore), MIDI type 1
(melody + chord track + tempo), JSON (complete intermediate representation,
including confidences and alternatives), text (chord chart line).

## 21. COMBINED MODE (v2, EXPERIMENTAL)

Voice + instrument together: reuse the SAME shared CQT; Melodia-style melody
saliency (register prior — voice usually above accompaniment; NEVER assume the
strongest peak is the voice); harmonic masking; reduce confidence on conflict;
suggest a dedicated mode when interference is high. Do not promise perfect
separation — display "voice: G4 (conf. 0.68) | chord: C (conf. 0.81)".

## 22. GUI

ImGui. Consumes Triple-Buffer Snapshot at $\le 30\text{ Hz}$.
Always display: note/freq/cents, tuner, likely chord + alternatives + reason,
`bassSettled` state, confidences, key, chord timeline, score preview,
MEASURED latencies, and xrun counter.

Score rendering (Verovio):
- Executed exclusively outside the real-time analysis thread (asynchronous worker or UI pass).
- Debounced ~250 ms and rendered only for confirmed events to prevent UI stutter.

## 23. TESTS AND METRICS [MEASURE]

Catch2. Synthetic corpus: WAVs generated from MIDI with models (sines +
harmonics, guitar/piano ADSR, optional noise and reverb, global tuning offset
in cents).

CI Metrics (mir_eval/MIREX style):
- Raw Pitch Accuracy (±50 cents) — voice.
- Weighted Chord Symbol Recall — chords.
- Key accuracy; latencies p50/p95; xruns.
- **Zero-Allocation Assertion:** Debug/CI test suite overrides global `new`/`malloc`.
  Any dynamic allocation inside the audio callback OR the steady-state loop of
  `RealtimeAnalysisThread` aborts execution and fails CI.
- Mandatory adversarial cases: silence, noise, speech, incomplete chords,
  arpeggios, global detuning, vibrato, glissando, voice+chord.

## 24. INCREMENTAL PLAN (every step compiles, tests, and measures)

- **M0 Skeleton:** Audio callback + SPSC ring buffer + preallocated triple-buffer + minimal ImGui + latency/xrun/allocation trap.
- **M1 Voice:** YIN (time-domain SIMD & FFT autocorrelation) + cents + tuner + melodic tracker (zero-alloc POD).
- **M2 CQT Engine:** Octave-decimated filter bank + sparse CSR kernels + chroma + global tuning estimator.
- **M3 Chords:** POD templates + bounded matching + chord tracker + ambiguity reasons in GUI.
- **M4 Bass & Temporal:** Settled-state tracking + inversions + onset spectral flux + arpeggio accumulator.
- **M5 Music Theory:** Key + spelling + chord symbols + Roman numerals + cadence rules.
- **M6 Offline & Export:** Reprocessing thread + MusicXML/MIDI/JSON writers + debounced Verovio score preview.
- **M7 Refined Piano & NNLS:** Bounded-iteration NNLS-chroma + inharmonicity partial correction ($B$ coefficient).
- **M8 WASM & Experimental:** Emscripten AudioWorklet build + thread synchronization headers + experimental combined mode.

Process: Interfaces first; reference implementation; then optimization with
before/after measurement. No invented performance numbers. Any uncertain result
must appear as uncertain in the UI.