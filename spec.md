# PROMPT V2.3 — REAL-TIME OFFLINE MUSIC ANALYZER (single specification)

You are a senior engineering team: audio DSP, MIR (Music Information Retrieval),
Western music theory, real-time C++ (C++20), desktop GUI in C# (.NET).

Changes from V2.1: native desktop only (C++ core + C# app, no web/WASM); LIVE
and POST layers strictly separated (score is POST only); modes chosen before
the session, one dedicated pipeline per mode, no automatic/dynamic switching;
four tabs (START / LIVE / STUDIO / SCORE) — everything heavy or optional lives
in STUDIO/SCORE; LIVE is a studio-rack view with REC, BPM and metronome;
no ASIO; MIT licence;
CQT configuration table corrected to physics; low-latency bass preview; hidden allocation, lossy-event
and missing-recorder gaps closed; latency measurement points defined.

## 0. GOAL AND PHILOSOPHY

A 100% offline native desktop application (no cloud, no external APIs, no
neural networks in LIVE — offline neural models allowed in STUDIO only, no
web/browser build) that receives audio from
microphone/interface/file.

The application has FOUR tabs. LIVE and STUDIO/SCORE share the DSP core and the
music-theory code, nothing else:

**START tab** (buttons; only when no session is running): mode buttons, quality,
key signature, clef, meter/BPM, metronome, guitar tuning, A4, and audio settings
(backend WASAPI / CoreAudio / ALSA / JACK / PipeWire, device, channels, sample
rate, buffer size, metronome output, latency calibration) — section 22.

**LIVE layer** (while playing/singing; goal = lowest possible latency):
1. Sung voice pitch: Hz, MIDI note, cents deviation, confidence, tuner.
2. Guitar/piano notes and chords: notes, bass, inversion, chord symbol,
   alternatives, reason.
3. Live visualization of the above + key (user-fixed) + tuning + chord timeline
   + measured latencies/xruns.
4. BPM and metronome: metronome on/off, BPM, meter, beat indicator. Without
   metronome, an estimated BPM (onset autocorrelation over the last ~6 s,
   display only, marked "estimated").
5. REC button: the core idea of LIVE is "play and see it recognized now"; REC
   additionally saves a take for post-processing. Recording never changes the
   live pipeline; the take appears in the STUDIO library when REC stops.
   REC requires the metronome with count-in (bars come from its grid).
6. LIVE → score / chord chart (fast path): the confirmed LIVE events (notes,
   chords) of a REC take — or of the last N bars without REC — are sent
   directly to STUDIO stages 5–8 (theory, rhythm, score, export) with NO audio
   reprocessing. Result in seconds: lead sheet, chord chart (`| C | G | Am |`),
   MusicXML/MIDI. "Refine from audio" re-runs the full STUDIO pipeline later.
The LIVE layer NEVER renders a score, never runs Verovio, never writes files,
never separates sources. It offers only the fixed modes of section 5.

**STUDIO tab = POST layer, audio side** (on a REC take or an imported file;
goal = accuracy and options):
- Library: REC takes + imported pre-made recordings (WAV, FLAC, MP3 via
  miniaudio `ma_decoder`; OGG via stb_vorbis). Imported files never go
  through LIVE.
- Configurable offline pipeline stages 0–4 (section 20): sample rate /
  resampling, editor / multitrack, effects, source separation (voice vs
  accompaniment, instruments), choir and choir-by-section (SATB), analysis
  resolution, multi-pitch.

**SCORE tab = POST layer, notation side** (stages 5–8): key/mode, meter
(default 4/4), ties across bar lines, accidental policy, clefs, transposition,
melody score, chord chart, Roman numerals, cadences, score view,
MusicXML/MIDI/JSON/text export. Input: LIVE fast path or STUDIO.

POST has no latency budget beyond "responsive UI"; it may be slow and allocate.

Placement rule: if a feature is expensive, optional, needs look-ahead, or
needs the whole recording (CQT at high resolution, separation, choir,
quantization, notation choices), it belongs in STUDIO/SCORE — never in LIVE.

Non-negotiable rules:
- Never fake certainty. Insufficient evidence → alternatives, reduced
  confidence, or "unknown".
- Do not solve everything with one technique. Voice and polyphony use
  independent pipelines, sharing only pre-processing, CQT, and music theory.
- Strictly separate LIVE (provisional, cheap, deterministic, zero-allocation)
  from POST (accurate, expensive, post-processed).
- Measure, never promise. Every reported latency/CPU figure must come from
  measurement (points defined in section 4).
- If a requirement conflicts with physics (e.g., time-frequency resolution,
  filter group delay), physics wins. Report the conflict; do not pretend to
  implement it.
- Zero dynamic allocation (`malloc`, `new`, `std::vector` resize, `std::string`)
  in the audio callback AND in the steady-state loop of `RealtimeAnalysisThread`.
- Real-time native threads NEVER call managed (C#) code.

## 1. MANDATORY STACK

| Component       | Choice                                                      |
|-----------------|-------------------------------------------------------------|
| Core language   | C++20 (`-std=c++20`, concepts, `std::span`, `std::atomic::wait/notify`) |
| App language    | C# (.NET 10 LTS; prototype in `app/`)                       |
| Core ↔ App      | Native shared library, plain C ABI (`extern "C"`), blittable POD structs, P/Invoke (`LibraryImport`) |
| Capture         | miniaudio: WASAPI (shared / exclusive), CoreAudio, ALSA, JACK, PipeWire. No ASIO in v1 — added only if a measured WASAPI capture on a real interface misses the ≤ 20 ms budget |
| Neural (STUDIO) | demucs.cpp (MIT, C++17 + Eigen, Demucs v4 weights, MIT) for source separation; optional ONNX Runtime (MIT, CPU) for other models |
| Plugins (STUDIO)| VST3 SDK (MIT since Oct 2025) — hosting study in section 20.2, milestone M11 |
| Resampler       | STUDIO only: r8brain-free-src (MIT), arbitrary ratio        |
| Files (STUDIO)  | miniaudio `ma_decoder` (WAV, FLAC, MP3) + stb_vorbis (OGG)   |
| FFT             | PocketFFT or pffft (avoid FFTW/GPL)                         |
| CQT             | Octave-decimated filter bank, sparse CSR kernels (~1–3 MB)  |
| Audio ring      | `ma_pcm_rb` (miniaudio's lock-free SPSC PCM ring)           |
| Event queue     | Own preallocated SPSC ring (`core/src/lockfree.hpp`, power-of-two, `try_push` only, never allocates) |
| Live snapshot   | Lock-free triple buffer                                     |
| SIMD            | Portable vector abstraction (AVX2 / SSE4.1 fallback, ARM NEON) |
| GUI             | Avalonia UI (C#, Skia) — cross-platform Windows / Linux / macOS |
| Score (SCORE)   | Verovio (C++, LGPL-3.0 → dynamically linked, own C wrapper) → SVG shown in C# |
| Export (STUDIO) | C#: `System.Xml` (MusicXML), own MIDI writer, `System.Text.Json` |
| Licence         | Open source, MIT. Dependencies are MIT/BSD/BSL/public domain; Verovio (LGPL-3.0) stays a separate dynamically linked library |
| Testing         | Catch2 (C++), xUnit (C#), synthetic WAVs, RealtimeSanitizer, microbench |

The DSP core does not depend on the GUI, performs no I/O on real-time threads,
is deterministic, float32 throughout, and aligns all computational buffers to
64 bytes (`alignas(64)`).

Target platforms: Windows (primary), Linux, macOS. WSL2 is a development
environment only — its audio path (WSLg/PulseAudio) is NOT valid for latency
acceptance.

## 2. ARCHITECTURE

```
Audio Hardware / File
       │
       ▼ (audio callback: lock-free, zero alloc, zero I/O)
[ ma_pcm_rb: analysis ring ]──────────────┐
       │                                   │ [ ma_pcm_rb: recorder ring ]
       ▼ (woken by atomic notify)          ▼
[ RealtimeAnalysisThread ]           [ RecorderThread ] → WAV on disk / chunk pool
 ├─ Pre-processing (decimation, DC removal, VAD, RMS/Peak)
 ├─ Pipeline A: Monophonic Voice (YIN)
 └─ Pipeline B: Polyphony
     ├─ Bass preview (time-domain periodicity, low band)
     └─ Multi-resolution Octave-Decimated CQT
         ├─ Spectral flux (onsets) → onset-gated low-octave windows
         ├─ Harmonic peaks & inharmonicity correction
         ├─ Bass confirmation & inversion tracker
         ├─ NNLS-chroma (bounded iterations)
         └─ Chord templates & Temporal tracker (preview vs confirmed)
       │                                   │
       ▼ state (lossy, latest wins)        ▼ events (lossless)
[ Triple-Buffer LiveSnapshot ]       [ SPSC EventQueue ] (onsets, note/chord events)
       │                                   │
       └──────────────┬────────────────────┘
                      ▼  C ABI (copy into preallocated managed structs)
               [ C# UIThread — LIVE view ]

──────────────── STUDIO tab = POST layer (separate) ────────────────
REC take stopped / file imported (STUDIO library)
       │
       ▼
[ OfflineAnalysisThread (C++) ] full reprocessing, multi-pass Viterbi, exact bass
       │  C ABI: returns MelodyScore / ChordScore as flat arrays
       ▼
[ C# STUDIO tab ] stages 0–4: library, editor, separation  →  [ C# SCORE tab ] stages 5–8: score (Verovio SVG), chord chart, Roman numerals, cadences
       │
       ▼
[ C# ExportTask ] MusicXML / MIDI / JSON / Text
```

Modes (section 5): VoiceMono, InstrumentMono, GuitarChords, PianoChords,
GeneralChords — chosen before the session, one dedicated pipeline each, never
switched at runtime. LIVE source is always live input; files enter through
the STUDIO library and go only to STUDIO/SCORE. The
voice + instrument and choir cases are STUDIO only (section 21), never live.

## 3. THREADS AND REAL TIME

Native threads: `AudioCallback`, `RealtimeAnalysisThread`, `RecorderThread`,
`OfflineAnalysisThread`. Managed: C# `UIThread`, C# export tasks.

The audio callback NEVER: allocates, blocks on a mutex, performs I/O, prints,
logs, runs CQT or analysis. It only writes samples into the analysis ring and
the recorder ring, increments atomic counters, and wakes the analysis thread
with `std::atomic<uint32_t>::notify_one()` ONLY when (a) the unread samples in
the analysis ring reach at least one hop and (b) the analysis thread has
flagged itself as waiting. With small device periods (64–128 frames) this
avoids hundreds of useless wake-ups/futex calls per second (non-blocking;
callback time is measured to confirm the cost).

Metronome clock rule: the bar grid lives on the INPUT sample clock (the clock
that records the take). Preferred: input and output on the same physical
device (one duplex `ma_device`), click mixed in the same callback from a
preloaded buffer (sample-accurate, zero allocation). If the user picks a
different output device, its crystal drifts (~50 ppm ≈ 30 ms per 10 min): the
click then runs on a separate playback `ma_device`, and each beat is
re-anchored — its output frame is computed from the input-clock beat time via
the measured input/output rate ratio (estimated continuously from both frame
counters vs the monotonic clock). The recorded grid is never taken from the
output clock. START shows a warning when input and output devices differ.

The `RealtimeAnalysisThread`:
- Sleeps in `std::atomic<uint32_t>::wait()` — never `sleep_for` polling
  (Windows default timer granularity ≈ 15.6 ms adds latency and jitter).
- Priority: Windows MMCSS ("Pro Audio"); Linux SCHED_FIFO via rtkit when
  available; macOS QoS `USER_INTERACTIVE` (join the device audio workgroup when
  available, so it is not scheduled on efficiency cores).
- Sets flush-to-zero / denormals-are-zero (x86 MXCSR FTZ|DAZ, ARM FPCR.FZ) at
  thread start. The audio callback does the same. Denormals in decaying
  signals otherwise cause CPU spikes and xruns.
- Pre-warms/touches all static buffers and arenas during initialization
  (preventing page faults).
- Publishes immutable POD `LiveSnapshot` via a triple buffer
  (`std::atomic<uint32_t>`, acquire-release). The snapshot contains values and
  inline arrays only — NO pointers into analysis-owned buffers.
- Pushes discrete events (`AnalyzerEvent`, section 22) into the preallocated
  SPSC `EventQueue` (capacity 4096 events) with `try_enqueue`; on full queue,
  increments a dropped-events counter (displayed). Each event carries a
  sequence number, so the C# side also detects gaps. The triple buffer is
  lossy by design and must never carry events.
- Completeness rule: the score/fast path consumes only COMPLETE events
  (`NoteEnd` with the full note, `ChordEnded` with the full chord, both with
  start and end). Start events are for display only. A dropped event can
  therefore lose one note/chord but never create a stuck (endless) one. On
  REC stop / `ana_stop` the core flushes: open notes and chords are closed
  and emitted as complete events. If drops or sequence gaps occurred, the
  event log is marked "incomplete" and SCORE shows a warning (suggesting
  "refine from audio").

REC: the callback writes into the recorder ring only while an atomic
`recording` flag is set (set/cleared by the REC button, sample-accurate via
the sample counter; with metronome count-in, the flag is set at the first
downbeat). The `RecorderThread` drains the recorder ring to a WAV file
(float32, device rate). It is not real-time; it may block on I/O but never on
the callback. Ring sized for ≥ 5 s of audio; overflow increments a counter
and marks a gap in the take (never silent data loss).

Each take gets a JSON sidecar: `SessionConfig`, device rate, measured input
latency, start sample, metronome BPM/meter and first-downbeat sample (if
used), recorder gaps, and the confirmed LIVE event log (source of the fast
path LIVE → score, section 20; the full STUDIO pipeline reprocesses from audio
instead).

Mandatory verification (not optional):
- RealtimeSanitizer (Clang ≥ 20, `-fsanitize=realtime`, `[[clang::nonblocking]]`
  on the callback and the analysis steady-state loop) in CI on Linux/macOS:
  catches malloc, locks and blocking syscalls.
- Fallback trap on all platforms: thread-scoped override of global
  `operator new`/`delete` that aborts when a real-time scope flag is set.
- Memory page faults, xrun/underrun counters, dropped events exposed live in
  the GUI.
- Latencies measured and displayed (section 4).

## 4. BUDGETS — ACCEPTANCE CRITERIA [MEASURE]

LIVE layer only. POST has no latency budget.

| Item | Target |
|---|---|
| Capture latency (device period + ring) | ≤ 20 ms (request 5 ms period, low-latency profile) |
| Voice time-to-first-pitch (tuner needle moves) | ≤ 80 ms |
| Voice time-to-stable (stable note label, LowLatency) | ≤ 150 ms |
| Bass preview (periodicity, `bassSettled = false`) | ≤ 3 periods of the bass + 1 hop |
| Chord preview | every hop, with `bassSettled` flag |
| Chord confirmation (hysteresis) | 0.4–1 s — DOCUMENTED behavior, not a bug |
| Total live CPU (Balanced, reference machine) | ≤ 15% of one core |
| LowLatency CPU | ≤ 10% of one core |
| Audio callback execution time | ≤ 1 ms per 10 ms block |
| GUI snapshot read | once per rendered frame (display refresh, ≥ 60 Hz), never per audio block |
| xruns in 10 min | 0 |

Reference machine: to be fixed before M0 closes (CPU model, OS, audio
interface/backend). CPU figures without it are invalid.

Measurement points (a figure without its point is invalid):
- Capture: loopback test (output click → input) minus known output latency;
  alternatively the backend-reported device latency, labelled as reported.
- Processing: sample-clock timestamps. For each estimate, latency = time the
  estimate is published − time the callback delivered the first sample of the
  triggering event (synthetic input with known onset).
- GUI: publish → C# read, stamped with the same monotonic clock.
- Display (optional): photodiode/high-speed camera. If not measured, the GUI
  shows "display latency: not measured".

miniaudio configuration: `ma_performance_profile_low_latency`, requested
period 5 ms, WASAPI exclusive mode when the user allows it, and ALWAYS read
back the real rate and period — never assume the requested ones were accepted.

Windows capture budget per mode (to be MEASURED on the reference machine):
- WASAPI exclusive (capture only): the ≤ 20 ms target is expected here.
  Exclusive capture locks only the input device — other apps (backing track,
  browser) keep playing through the output.
- WASAPI shared: period depends on the driver (10 ms default; smaller on
  Windows 10+ when the driver supports low-latency shared mode) plus the
  engine's buffering and possible rate conversion. The ≤ 20 ms target is NOT
  guaranteed; the GUI shows the measured value.
Capture sits behind a small backend interface (open / start / stop / real
rate / real period), so an ASIO backend can be added later without touching
the analysis code if measurements on real interfaces fail the budget.

CQT physics (law, not preference): window $T \approx Q/f$, with $Q \approx 1.443 \cdot \text{binsPerOctave}$.
Filter group delay $\tau_g \approx T/2$.

| f (Hz) | 12 bpo (T / $\tau_g$) | 24 bpo (T / $\tau_g$) | 36 bpo (T / $\tau_g$) |
|--------|-----------------------|-----------------------|-----------------------|
| 27.5   | 630 ms / 315 ms       | 1.26 s / 630 ms       | 1.9 s / 950 ms        |
| 55     | 315 ms / 158 ms       | 630 ms / 315 ms       | 945 ms / 472 ms       |
| 82.4   | 210 ms / 105 ms       | 420 ms / 210 ms       | 630 ms / 315 ms       |
| 100    | 173 ms / 87 ms        | 346 ms / 173 ms       | 519 ms / 260 ms       |
| 165    | 105 ms / 52 ms        | 210 ms / 105 ms       | 315 ms / 158 ms       |
| 440    | 39 ms / 20 ms         | 79 ms / 40 ms         | 118 ms / 59 ms        |

Resolution vs estimation (the key to low latency):
- RESOLVING two simultaneous tones one semitone apart needs ≈ 1/Δf
  (≈ 200 ms at 82 Hz). No algorithm beats this. This is what the CQT pays for.
- ESTIMATING the frequency of ONE tone needs only 2–3 periods
  (≈ 25–36 ms at 82 Hz). The bass line is almost always monophonic, so the
  bass PREVIEW uses estimation (section 11); the CQT CONFIRMS it.

Mandatory consequences:
- LIVE: minimum CQT frequency per mode/quality as in section 5. 55 Hz only if the
  user accepts the physical latency. 27.5 Hz NEVER live — POST only.
- Settle time is COMPUTED, never hard-coded: `settleSeconds = Q / f_minActive`.
  `bassSettled = false` until that time has elapsed since the last onset.
- Chord preview: on onset, report candidate immediately from mid/high bins +
  bass preview, with `bassSettled = false` and reduced confidence.
- Onset-gated progressive windows (low octaves): at an onset, pre-onset samples
  are excluded from the low-octave windows, so the previous chord's bass does
  not contaminate the new chord for up to T. Use 2–3 precomputed kernel sets
  per low octave (short → full length); confidence grows with window length.
  Short low-octave kernels have poor frequency resolution (Δf ≈ 1/T: a 50 ms
  window ≈ 20 Hz ≈ 3 semitones at 82 Hz), so they are used ONLY for energy
  and onset/flux evidence — never for note identity. Low-note identity comes
  from the bass preview until the full-length window has filled.
- Multi-resolution CQT: octave-decimation filter bank (long windows in lows,
  short in highs).
- "Chord latency" = window of lowest active bin + hop + hysteresis. Report
  MEASURED numbers.

## 5. MODES, SESSION CONFIG AND SAMPLE RATES

Every option is chosen by the user BEFORE the session starts and is immutable
for the whole session. There is NO automatic mode detection, NO runtime
switching between pipelines, NO "try several and pick" — each of those adds
analysis work and decision delay on the live path. Changing any option =
stop the session → reconfigure → start a new session (and a new recording).

Each mode is a separate, dedicated pipeline. At `ana_start` the core
instantiates and pre-warms ONLY the selected pipeline; the steady-state loop
is chosen once (function pointer / template instantiation), with no per-block
mode branching. Unselected pipelines are not allocated.

```cpp
enum class AnalysisMode : uint8_t {
    VoiceMono,        // one sung voice (monophonic) — Pipeline A
    InstrumentMono,   // one melodic line on an instrument — Pipeline A
    GuitarChords,     // polyphonic guitar, acoustic or electric — Pipeline B
    PianoChords,      // polyphonic piano — Pipeline B + inharmonicity
    GeneralChords     // other polyphonic sources — Pipeline B, no string constraints
};
// Never live: choir/duet, voice + instrument, separation — STUDIO (section 21).

enum class AudioQuality : uint8_t { LowLatency, Balanced, HighPrecision };

struct GuitarTuning {
    uint8_t stringCount;
    std::array<int8_t, 8> openMidi;   // e.g. standard: 40 45 50 55 59 64
    int8_t capoFret;
};

struct SessionConfig {                // passed to ana_start(), immutable after
    AnalysisMode mode;
    AudioQuality quality;
    float referenceA4;                // default 440
    bool keySet;                      // false = not set (POST may estimate)
    int8_t keyFifths;                 // -7..+7, as MusicXML <fifths>: Gb (-6) != F# (+6)
    KeyMode keyMode;
    Clef clef;                        // display/spelling octave (Treble8vb for guitar)
    TimeSignature meter;              // default 4/4
    float bpm;
    bool metronome;                   // click; forced ON while REC
    uint8_t countInBars;              // default 1; REC starts at first downbeat
    GuitarTuning guitarTuning;        // GuitarChords only
};
```

Metronome on/off, BPM and meter do not touch the analysis pipeline (output
click only), so they may be changed during a LIVE session — but NOT while REC
is active (a take has one tempo and meter; tempo changes = STUDIO tempo map).

REC always runs with the metronome and count-in: the bar grid of the take is
exact (BPM, meter, first-downbeat sample in the sidecar), which is what makes
the fast path LIVE → SCORE produce correct bar lines. Free-time material
without metronome enters through the STUDIO library instead.

Levels: analysis frames are internally RMS-normalized, so detection does not
depend on input gain (the VU guides the user to a target zone). On REC stop,
an optional non-destructive normalization (peak or RMS target) is stored as a
gain in the sidecar; the WAV itself is never rewritten.

Rates via INTEGER division of the device's native rate (never an
arbitrary-ratio resampler on the live path):
- Voice: native/3 ($48\text{k} \rightarrow 16\text{k}$) or native/2 ($44.1\text{k} \rightarrow 22.05\text{k}$).
- Chords live: native/2 ($48\text{k} \rightarrow 24\text{k}$, $44.1\text{k} \rightarrow 22.05\text{k}$).
- Piano/precision: native rate.

Decimation filters:
- Any LTI filter preserves periodicity, so YIN minima are not corrupted by
  non-linear phase. Voice decimation may use a linear-phase FIR or a
  minimum-phase FIR/IIR; choose the one with lower measured group delay that
  meets the alias rejection spec. Group delay is always compensated in
  timestamps.
- CQT octave decimation: polyphase IIR all-pass half-band filters (magnitude
  CQT; phase is irrelevant). A linear-phase FIR cascade accumulates delay per
  stage (e.g., 31 taps × 5 stages from 24 kHz ≈ 19 ms at the lowest octave).
  Each octave's timestamps are compensated individually; higher octaves are
  NOT delayed to align with lower ones.

Detect the device's real rate; never assume the requested one was accepted.

T_low = window of the lowest CQT bin (from the physics table).

Mode × quality → fixed live configuration (the ONLY source of live settings):

| Mode | Quality | Rate | Window / T_low | Hop | Algorithm | Range |
|---|---|---|---|---|---|---|
| VoiceMono | LowLatency | 16k | 40 ms | 5 ms | YIN time-domain SIMD | 70–1200 Hz |
| VoiceMono | Balanced | 16k | 50 ms | 10 ms | YIN time-domain SIMD | 70–1200 Hz |
| VoiceMono | HighPrecision | 22.05k | 60–80 ms | 10 ms | YIN FFT auto-corr | 50–2000 Hz |
| InstrumentMono | LowLatency | 16k | 40 ms | 5 ms | YIN time-domain SIMD | 60–2000 Hz |
| InstrumentMono | Balanced | 16k | 50 ms | 10 ms | YIN time-domain SIMD | 60–2000 Hz |
| InstrumentMono | HighPrecision | 22.05k | 80 ms | 10 ms | YIN FFT auto-corr | 40–4000 Hz |
| Guitar/GeneralChords | LowLatency | native/2 | T_low 173 ms (100 Hz) | 20 ms | CQT 12 bpo + bass preview | CQT 100–4200 Hz, bass 70–500 Hz |
| Guitar/GeneralChords | Balanced | native/2 | T_low 210 ms (82 Hz) | 20 ms | CQT 12 bpo + bass preview | 80–4200 Hz |
| Guitar/GeneralChords | HighPrecision | native | T_low 420 ms (82 Hz) | 20 ms | CQT 24 bpo + bass preview | 80–4200 Hz |
| PianoChords | LowLatency | native/2 | T_low 173 ms (100 Hz) | 20 ms | CQT 12 bpo + bass preview | CQT 100–4200 Hz, bass 55–600 Hz |
| PianoChords | Balanced | native/2 | T_low 210 ms (82 Hz) | 20 ms | CQT 12 bpo + bass preview | 80–4200 Hz |
| PianoChords | HighPrecision | native | T_low 630 ms (55 Hz) | 20 ms | CQT 24 bpo + bass preview | 55–4186 Hz |

POST (any mode, any quality): full reprocessing at native rate, 24–36 bpo,
lowest frequency 27.5 Hz (PianoChords) / 55 Hz (other chord modes), multi-pass.

24 bpo below ~165 Hz costs latency (see physics table); HighPrecision live
configs display their computed T_low to the user before the session starts.

## 6. PRE-PROCESSING (shared by all pipelines)

Mono (sum / channel selection / optional difference), DC removal, configurable
high-pass and low-pass (mono modes HP ≤ 0.8 × the configured lowest frequency,
e.g. ≈ 40 Hz for VoiceMono HighPrecision, ≈ 55 Hz for VoiceMono LowLatency;
polyphony LP 8–12 kHz; piano with no
aggressive filtering), optional normalization, noise gate, RMS, peak,
clipping/saturation detection, noise-floor estimation, simple VAD (energy +
periodicity). Do not destroy transients.

```cpp
// Internal to the core; never crosses the C ABI.
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

Steps: squared difference $d(\tau)$ over an UNWINDOWED frame (windowing
weights the two compared segments differently and biases $d(\tau)$ at the true
period) → accumulated normalized CMND → first minimum below threshold
(0.10–0.20) → parabolic interpolation → range validation → confidence/clarity →
reject unstable estimates and false octaves (temporal validation).
Live runs ONE estimator (YIN). A second estimator (MPM / normalized ACF)
runs only in POST: when they disagree, reduce confidence instead of picking
arbitrarily.

Algorithmic execution strategy:
- LowLatency / Balanced ($W \le 800$ samples @ 16 kHz): Time-domain difference $d(\tau)$
  vectorized with 128-bit / 256-bit SIMD. Avoids FFT transform overhead. Hop
  5 ms is affordable (≈ 30 M MAC/s) — measure.
- HighPrecision ($W \ge 1320$ samples @ 22.05 kHz): FFT-based autocorrelation
  $d(\tau) = r_t(0) + r_{t+\tau}(0) - 2r_t(\tau)$ via PocketFFT/pffft
  (energy terms via running sum of squares), $O(N \log N)$.

"Window" in the mode × quality table is the TOTAL analysed span: integration
length = window − max lag (max lag = rate / f_min), so the window is also the
worst-case look-back. Measured on synthetic input (M1, sample clock, 48 kHz):
time-to-first-pitch 30 / 30 / 40 ms and time-to-stable 55 / 50 / 60 ms for
LowLatency / Balanced / HighPrecision. Time-domain scalar YIN costs ≈ 2 % of
one core; SIMD and the FFT variant wait until a measurement asks for them.

Two latencies, two outputs:
- Instantaneous pitch (tuner needle, cents): published EVERY hop, no median.
- Stable note (label, score events): causal median of 3 frames + tracker
  hysteresis.

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
    int8_t midi;               // sounding pitch
    int8_t letter;             // 0=C 1=D 2=E 3=F 4=G 5=A 6=B (spelled, section 22.2)
    int8_t alter;              // -2..+2 (bb, b, natural, #, x)
    int8_t writtenOctave;      // octave of the LETTER, after clef shift (Cb4 = MIDI 59)
    char writtenName[8];       // POD fixed-size buffer, e.g. "C#4", "Db4", "Cb4"
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
- Only the topmost octave is analyzed at full rate. Lower octaves are
  successively decimated by factor 2 (section 5: polyphase IIR half-band,
  per-octave timestamp compensation).
- FFT sizes stay constant and small ($N \le 2048$). Compute savings vs a
  single full-rate FFT must be MEASURED in the M2 microbench.
- Sparse spectral kernels precomputed in CSR (Compressed Sparse Row) format,
  aligned to 64 bytes (`alignas(64)`), Structure of Arrays (SoA) layout.
- Low octaves: 2–3 precomputed kernel sets (progressive windows) for
  onset-gated analysis (section 4).

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

// Internal to the core. Pointers reference analysis-owned buffers, so this
// struct NEVER goes into LiveSnapshot (copy magnitudes into the snapshot's
// inline array instead).
struct alignas(64) CQTFrame {
    float* magnitude;          // Preallocated contiguous buffer (size: numberOfBins)
    float* phase;              // Preallocated contiguous buffer (size: numberOfBins)
    double timestampSeconds;
    float tuningOffsetCents;
    float confidence;
    bool bassSettled;          // False until settleSeconds (computed) after onset
};
```

Implementation order: (1) reference → (2) precomputed sparse CSR kernels →
(3) octave decimation filter bank → (4) SIMD (AVX2/NEON). One pipeline feeds
chroma, peaks, bass AND onset flux (spectral flux over CQT magnitudes is
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
// Crosses the C ABI (inside LiveSnapshot): no alignas (section 22).
// SIMD code works on internal aligned copies.
struct ChromaVector {
    std::array<float, 12> raw;
    std::array<float, 12> normalized;
    std::array<float, 12> smoothed;
    std::array<float, 12> bass;        // bass chroma, from the bass preview/confirmation
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

Treble vs bass chroma: the main chroma still receives the pitch class of low
notes through their octave harmonics (E2 → E3, E4), even when the CQT starts
at 100 Hz. What the CQT cut loses is WHICH note is lowest. That comes from a
separate bass chroma fed by the bass preview (section 11) and, once settled,
by the low CQT bins. Template matching uses the main chroma for chord
content and the bass chroma for `bassScore`/inversion; the bass is never
mixed into the main chroma.

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

Two stages:
1. **Preview (estimation, low latency):** YIN/MPM on the low band (LP ≈ 2× the
   upper bass range, decimated), window 2–3 periods of the lowest bass
   frequency (≈ 25–36 ms at 82 Hz, ≈ 43 ms at 70 Hz). Published every hop with
   `settled = false` and reduced confidence. Known failure: in some inversions
   the summed low band has a sub-harmonic period — reject candidates below the
   configured bass range and let the CQT decide.
2. **Confirmation (resolution):** low CQT bins + temporal persistence +
   priority to fundamentals. `settled = true` only after the computed
   `settleSeconds` and agreement (± 50 cents) with the preview, or CQT
   evidence alone after `settleSeconds` if the preview is unvoiced.

```cpp
struct BassEstimate {
    bool valid;
    int8_t midi;
    int8_t pitchClass;
    float frequencyHz;
    float confidence;
    bool settled;              // False until settleSeconds and CQT confirmation
};
```

Ranges: guitar 70–500 Hz; piano 27.5–600 Hz (POST). Hard rule: uncertain
bass → do NOT report an inversion; display the root-position chord with
reduced confidence.

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
major/minor; power chord carries no third; symmetric chords (aug: 3 equivalent
roots, dim7: 4 equivalent roots) have no root without bass evidence; an
inversion can look like another chord; extensions can look like basics; a
passing tone creates false chords. The GUI displays the reason: "Likely chord:
C | conf. 0.64 | alternatives: Am/C, F6 | reason: missing or unreliable third".

Guitar: standard tuning (E2 A2 D3 G3 B3 E4), Drop D, DADGAD, Eb, D, custom,
capo, variable string count. String/voicing constraints only TIE-BREAK
candidates — never replace audio analysis. Handle pick noise, muted strings,
distortion, arpeggios, incomplete chords.

Piano: A0–C8, high polyphony, decay (distinguish sustained from re-attacked
via onset), octave doublings, strong harmonics + inharmonicity (section 10),
no string constraints.

Arpeggios: accumulation window 300–600 ms live / 500–900 ms balanced /
adaptive POST. Notes of the same chord close in time accumulate evidence.
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

Default GUI behavior: instant per-frame preview (`provisional = true`, from
`LiveSnapshot`) + later confirmation (`provisional = false`, from `EventQueue`).
This is expected system behavior.

Event timing contract (required by the score): a confirmed event is emitted
late (confirmation takes 0.4–1 s), but its `startTimeSeconds` is BACKDATED to
the onset that started it, on the sample clock, minus the measured input
latency. Same rule for `MusicalNoteEvent`. The score therefore never inherits
the confirmation delay.

End of the previous event: the previous chord stays open while a new
candidate is provisional. When the new chord is CONFIRMED, the previous
chord is closed with `endTimeSeconds` = the new chord's backdated onset and
emitted as `ChordEnded` — so two chords never overlap on the timeline. If the
candidate is never confirmed (passing tone, noise), the previous chord simply
continues. Silence closes a chord at the release time detected by the
tracker. Same rule for monophonic notes (a confirmed new note closes the
previous one at its own onset).

## 14. ONSETS

Spectral flux over CQT magnitudes (reused, marginal cost), chroma flux, RMS
attack, pitch change. Onsets also gate the low-octave CQT windows (section 4).

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

Uses: separate repeated notes, detect arpeggios, gate low-octave windows,
quantization (POST), segmentation.

## 15. MUSIC THEORY — KEY, KEY SIGNATURE, SPELLING

Key: LIVE → fixed by the user; POST → estimated (Krumhansl-Schmuckler over
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
    char keyAccidentals[7][4];   // inline, e.g. "F#", "Bb" (C ABI safe, no pointers)
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

## 17. FUNCTIONAL ANALYSIS AND CADENCES (POST)

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

## 19. RHYTHM AND QUANTIZATION (POST)

ALWAYS keep two timelines: observed real time and quantized time (user toggle).
Durations: whole note through sixteenth + triplets (other tuplets later). BPM
via onset autocorrelation, chord-interval spacing, tap tempo, or manual.
Do not quantize vibrato-heavy sung notes aggressively.

Notes and chords longer than the space left in the bar:
- A note crossing a bar line is split at the bar line and TIED (never
  shortened, never extended). A note longer than a whole bar becomes the
  correct chain of tied values.
- Inside a bar, split by meter rules so the beat structure stays visible
  (4/4: do not hide beat 3 — a note from beat 2 to beat 4 is written
  quarter + tied quarter; a half note only when it starts on beat 1 or 3;
  6/8: group in dotted quarters).
- Ties are notation only: the note stays ONE `MusicalNoteEvent` (one onset);
  the MIDI export writes one note, not several.
- Chord chart: a chord lasting several bars repeats per bar (`| C | C |`, or
  `| C | % |` as an option); a chord change inside a bar is shown as a split
  bar (`| C G |`).
- Pickup (anacrusis): notes before the first downbeat of the count-in grid go
  into a pickup bar.
## 20. STUDIO TAB — POST PIPELINE, SCORE AND EXPORT

The STUDIO tab runs a configurable offline pipeline. Options are set in the
tab; none of them exists in LIVE. Two entry points:

- **A. From LIVE events (fast path, v1.0 priority):** input = confirmed LIVE
  event log (`MusicalNoteEvent`, `ChordEvent`, `OnsetEvent`) + sidecar
  (meter, BPM, first-downbeat sample, input latency, key). Runs stages 5–8
  only. No audio needed — works even without REC (event log kept in C# memory
  for the session). Quality = what LIVE saw.
- **B. From audio (full path):** REC take or imported file. Runs stages 0–8.
  Higher accuracy (look-ahead, high-resolution CQT, multi-pass), slower.

Both paths produce the same `MelodyScore` / `ChordScore` structures, so
stages 5–8 are shared code.

Stages (each optional stage can be disabled):

| # | Stage | Options |
|---|---|---|
| 0 | Input | REC take or imported file (from the STUDIO library); tempo/meter/downbeat from sidecar when present; channel / mix; analysis sample rate (native default; any rate via r8brain — arbitrary ratio allowed here); float32 internally |
| 1 | Edit (optional) | non-destructive audio editor (section 20.1): cut, trim, split, delete/mute region, fades, gain envelope, normalize |
| 2 | Effects (optional) | built-in: gain, high-pass/low-pass, EQ bands, noise gate. VST3 chain later (section 20.2) |
| 3 | Separation (optional) | none / 2 stems (vocals, accompaniment) / 4 stems (vocals, drums, bass, other) / 6 stems (+ guitar, piano) via Demucs v4 / choir by section (S, A, T, B; or SA/TB) — see 20.3 |
| 4 | Analysis per stem | mono stem: YIN + MPM cross-check, window, hop, threshold, range, Viterbi smoothing. Polyphonic stem: CQT 12–60 bpo, lowest frequency down to 27.5 Hz, hop 5–40 ms, window shape, NNLS on/off, inharmonicity on/off, chord vocabulary (triads / sevenths / extended). Choir stem: multi-f0 with section range priors (S C4–A5, A F3–D5, T C3–A4, B E2–E4) |
| 5 | Theory | key fixed or estimated, allowed modes, modulation candidates, spelling policy |
| 6 | Rhythm | meter (default 4/4; any n/d incl. compound 6/8, 12/8), pickup bar, tempo (fixed / from session metronome / tap / estimated tempo map), grid (1/4 … 1/32), tuplets, quantization strength, swing |
| 7 | Score | accidental policy (by key / prefer sharps / prefer flats; courtesy accidentals on/off), clef per staff (auto / treble / bass / treble-8vb), transposing instrument (C, Bb, Eb, F), staff layout (one staff per stem; SATB open or closed score), chord symbols above staff, Roman numerals below, solfège |
| 8 | Export | MusicXML / MIDI / JSON / text / edited or separated audio (WAV) |

Stage cache: each stage's output is cached under a hash of (its options +
upstream hash). Changing an option re-runs only that stage and downstream —
e.g., changing the accidental policy re-renders the score without re-running
separation or CQT.

Jobs run on worker threads (C++ `OfflineAnalysisThread` pool, C# tasks) with
progress and cancel, and never touch a running LIVE session.

### 20.1 Audio editor (STUDIO stage 1)

Basic, non-destructive: the original take is never modified. Edits are an
edit decision list (EDL) stored as JSON next to the take; undo/redo = EDL
history; the stage output is the rendered EDL (cached like any stage).

Operations: select region; cut / copy / paste / delete; trim (keep
selection); split; mute region; fade in / fade out / crossfade at joins
(linear, equal-power, exponential); gain envelope (breakpoints, linear or dB
interpolation); normalize (peak or RMS target); preview playback of the
selection. Waveform view from a precomputed peak mipmap (min/max per 2^k
samples) so zoom is O(visible pixels).

Multitrack (stems): after separation (stage 3) each stem is a track. Per
track: mute, solo, gain, pan (listening mix), waveform, analysis on/off and
analysis type (mono / chords / choir section), staff assignment in the score.
Edits can apply to one track or to all tracks (time-aligned cuts). Mix-down
export to WAV. Not in scope: recording new tracks over existing ones,
time-stretch, pitch-shift, spectral editing, automation beyond the gain
envelope.

### 20.2 VST3 hosting — study

Where: STUDIO stage 2 only (effects before analysis, e.g., denoise/EQ, and on
separated stems for export). NEVER in LIVE: third-party plugins may allocate,
lock, or add latency, breaking the real-time rules of section 3.

Licensing: VST3 SDK is MIT (since Oct 2025) — no agreement needed. CLAP (MIT)
is a simpler alternative format, optional later. AU (macOS) and LV2 (Linux)
out of scope.

Design:
- Out-of-process host (`PluginHost` executable, one per plugin chain): a
  crashing or hanging plugin never takes the app down. Audio exchanged via
  shared memory in blocks; control via a local pipe.
- Offline rendering with `processMode = kOffline` (may run faster or slower
  than real time); block size fixed per render.
- Latency compensation: read the plugin's reported latency
  (`getLatencySamples`) and shift the output; render the tail
  (`getTailSamples`) after the end.
- Plugin editor opens in its own native window owned by `PluginHost` (no
  embedding in Avalonia in v1).
- Plugin state (`getState`) saved in the stage options so the stage cache
  hash stays valid and the take reopens identically.
- Plugin scanning also out-of-process (scanning crashes are common), result
  cached.

Cost/benefit: high effort (host process, IPC, windows, state, scanning);
value mainly denoise/EQ before analysis. Recommendation: v1 ships the
built-in effects of stage 2; VST3 hosting is milestone M11.

### 20.3 Source separation (STUDIO stage 3)

- Instruments and voice vs accompaniment: Demucs v4 (hybrid transformer) via
  demucs.cpp (MIT, CPU). 2, 4 or 6 stems. Slow (expect minutes per song on
  CPU; MEASURE on the reference machine), runs as a cancellable job with
  progress; result cached per take + options.
- Choir by section (S/A/T/B): no production-quality model is assumed. Method:
  multi-f0 + section range priors + voice-leading continuity (section 21);
  score-informed separation when the user supplies a MusicXML reference.
  Marked EXPERIMENTAL.
- Every separated stem carries a confidence; the UI never presents
  separation as perfect.

### 20.4 Score and export

The score is a POST-layer product. It is built from the full recording after
reprocessing, never from the live stream. Melody score and harmonic score are
INDEPENDENT structures.

Heap-allocated containers are allowed ONLY in the POST layer
(`OfflineAnalysisThread` in C++; everything in C#).

```cpp
struct TimeSignature { uint8_t numerator, denominator; };
enum class Clef : uint8_t { Treble, Treble8vb, Bass, Alto, Tenor };   // Treble8vb: guitar, male voice

struct MelodyScore {
    KeySignature key;
    TimeSignature meter;
    double bpm;
    Clef clef;
    std::vector<MusicalNoteEvent> events;
    std::vector<SolfegeSyllable> solfege;
};

struct ChordScore {                // v1: chords only (one mode per session)
    KeySignature key;
    TimeSignature meter;
    double bpm;
    std::vector<ChordEvent> chords;
};
// A mono-mode LIVE session produces MelodyScore; a chord-mode session produces
// ChordScore. Melody + chords (or several staves) from ONE recording come from
// STUDIO stage 3 (separation) + one analysis per stem.
// Meter and bar lines come from SessionConfig (meter, bpm, metronome count-in),
// compensated by the measured input latency — never guessed from free audio
// when a score is requested.
```

Across the C ABI these are returned as header + flat POD arrays
(`ana_post_get_melody(handle, MusicalNoteEvent* out, size_t cap)`), never as
`std::vector`.

Export (C#): MusicXML (own writer, validated against the MusicXML XSD in CI and
by opening in MuseScore), MIDI type 1 (melody + chord track + tempo), JSON
(complete intermediate representation, including confidences and
alternatives), text (chord chart line).

Score view: Verovio (C++, LGPL-3.0, dynamically linked through a small C
wrapper) renders MusicXML → SVG on demand, off the UI thread; the C# SCORE tab
displays the SVG. Render only the visible pages/measures (render time grows
with score length).

## 21. VOICE + INSTRUMENT, CHOIR (STUDIO only)

Never live. Handled in STUDIO via stage 3 (separation) and stage 4 per stem.

Voice + instrument together: reuse the SAME shared CQT; Melodia-style melody
saliency (register prior — voice usually above accompaniment; NEVER assume the
strongest peak is the voice); harmonic masking; reduce confidence on conflict;
suggest a dedicated mode when interference is high. Do not promise perfect
separation — display "voice: G4 (conf. 0.68) | chord: C (conf. 0.81)".

Choir: multi-f0 over the (optionally separated) choir signal, notes assigned
to sections by range priors + voice-leading continuity (Viterbi over
assignments). Unison and voice crossing are ambiguous → reduced confidence,
never forced.

## 22. C ABI AND GUI (C#)

C ABI rules:
- Core exposes `extern "C"` functions only; opaque handle
  (`AnalyzerHandle*`); no C++ types, no exceptions across the boundary
  (error codes).
- Session lifecycle: `ana_start(handle, const SessionConfig*, const AudioDeviceConfig*)` allocates and
  pre-warms only the selected pipeline; `ana_stop(handle)`. No call changes
  the mode or quality of a running session.
- No callbacks from native real-time threads into C#. The C# side PULLS:
  - `ana_read_snapshot(handle, LiveSnapshot* out)` — copies the latest triple
    buffer slot (~19 KB, negligible at 60 Hz) into a caller-owned,
    preallocated struct. C#: `[LibraryImport]` with `ref LiveSnapshot`, the
    struct held in a field — no managed allocation per call.
  - `ana_drain_events(handle, AnalyzerEvent* out, size_t cap)` — returns count;
    C# drains into a preallocated array.
- Shared structs are POD with fixed-size inline arrays only. C# mirrors use
  `[StructLayout(LayoutKind.Sequential)]` (natural alignment, same padding
  rules as C), `byte` for C++ `bool`, `fixed byte` for `char[N]`,
  `enum : byte` for `uint8_t` enums — all blittable.
- NO `alignas` on any struct that crosses the ABI (C# cannot express it).
  Every padding hole is written as an explicit `_padN` field; ABI headers
  compile with `-Wpadded` (GCC/Clang) as an error, so implicit padding cannot
  sneak in.

```cpp
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
    uint32_t sequence;             // +1 per event; a gap = dropped events
    union {                        // 8-byte aligned (contains doubles)
        OnsetEvent onset;
        MusicalNoteEvent note;
        ChordEvent chord;
    } data;
};
// C#: [StructLayout(LayoutKind.Explicit)], union members at the same
// FieldOffset (8); size checked by the layout test.
```
- `static_assert(sizeof/offsetof)` in C++ and a C# test comparing
  `Marshal.SizeOf`/`Marshal.OffsetOf` against values exported by the core
  (`ana_struct_layout()`) — CI fails on mismatch.
- The managed GC can pause only managed threads; native real-time threads are
  unaffected as long as they never enter managed code.

```cpp
constexpr size_t MAX_CQT_BINS = 128;
constexpr size_t WAVE_COLUMNS = 1024;
constexpr size_t SCOPE_SAMPLES = 2048;

struct LiveSnapshot {
    uint64_t sequence;
    double publishTimeSeconds;          // monotonic clock, for GUI latency
    PitchEstimate pitch;
    NoteEstimate note;
    TuningEstimate tuning;
    BassEstimate bass;
    ChromaVector chroma;
    PolyphonicNoteSnapshot poly;
    ChordRecognitionResult chord;
    uint16_t cqtBinCount;
    std::array<float, MAX_CQT_BINS> cqtMagnitude;   // inline copy, no pointers
    float settleSeconds;                // computed, displayed
    float latencyCaptureMs, latencyProcessingMs;    // measured
    uint32_t xruns, pageFaults, droppedEvents;
    float metronomeBpm;                 // 0 = off
    float estimatedBpm, estimatedBpmConfidence;   // display only
    uint8_t beatInBar;                  // metronome position, 0 = off
    bool recording;
    double recordedSeconds;
    uint32_t recorderGaps;

    // Rack meters and scope (computed in the core, ballistics included)
    float vuLevel;                      // VU ballistics, 300 ms integration
    float peakDbfs, peakHoldDbfs;
    bool clipLatched;                   // cleared by the UI (ana_clear_clip)
    uint32_t waveWriteIndex;            // ring position (UI copies ring, no loss up to ~5 s)
    float waveColumnSeconds;            // e.g. 256 samples @ 48 kHz = 5.3 ms
    std::array<float, WAVE_COLUMNS> waveMin, waveMax;
    std::array<float, SCOPE_SAMPLES> scope;   // last samples at analysis rate (triggered scope)

    // Milliseconds shown next to every value
    float noteLatencyMs;                // onset → publish, measured
    float chordLatencyMs;               // onset → publish of current candidate
    float chordConfirmElapsedMs;        // time since onset while provisional
};
```

### 22.0 Reference GUI (already implemented)

A working design prototype exists in `app/` (C#, .NET 10, Avalonia 12.1):
START / LIVE / STUDIO / SCORE tabs, the LIVE rack modules of 22.3, stage mode
and shortcuts, fed by simulated data. It is the visual reference for this
section — the back-end work does not redesign it.

- `app/LiveModel.cs`: `LiveFrame` (view-side mirror of `LiveSnapshot`),
  `Session` (mirror of `SessionConfig`) and `ILiveSource`, the ONLY seam the
  back-end implements (native source over `ana_read_snapshot` /
  `ana_drain_events`). `FakeLiveSource` stays for GUI work and tests.
- `app/Rack.cs`: rack modules; `app/Theory.cs`: spelling, key cascade,
  clefs; `app/Program.cs`: window, tabs, START.
- The prototype flags everything as SIMULATED in the status strip; only
  "display ms" is measured.
- Not yet in the prototype: rack edit mode (22.4), CQT waterfall, triggered
  scope, SCORE engraving (Verovio), STUDIO, zero-allocation text rendering
  (22.7).

When the native core lands, `LiveFrame`/`Session` fields follow the C ABI
structs of this section (the ABI is the source of truth; the prototype adapts).


START · LIVE · STUDIO · SCORE.

- **START** (buttons): big mode buttons (Voice, Instrument melody, Guitar
  chords, Piano chords, General chords); quality selector (LowLatency /
  Balanced / HighPrecision, each showing its computed T_low in ms); key
  signature cascade + clef (22.2); meter (default 4/4) + BPM; metronome
  sound/volume + count-in bars; guitar tuning + capo (guitar mode); A4.
  Audio settings panel: backend (WASAPI shared / exclusive, CoreAudio, ALSA,
  JACK, PipeWire), device, input channels, sample rate (device-supported
  list), buffer size (with resulting ms), metronome output device, latency
  calibration (loopback). A big START button opens LIVE. Everything here is
  fixed for the session (section 5); editable only when no session runs.
- **LIVE**: studio rack (22.3). No score rendering.
- **STUDIO**: library (REC takes + imported WAV/FLAC/MP3/OGG, metadata,
  rename/delete), multitrack/editor, stages 0–4 options, job progress/cancel,
  per-stem confidence.
- **SCORE**: stages 5–8 — notation options (accidental policy, clefs,
  transposition, layout, ties, chord symbols, Roman numerals), Verovio view,
  chord chart view, cadences with evidence, timeline toggle (real /
  quantized), export. Input: LIVE fast path or STUDIO.

### 22.2 Key signature cascade, clef and spelling

Cascade:
1. Mode: Major / minor.
2. Key, ordered by the circle of fifths with its accidental count:
   C · G 1♯ · D 2♯ · A 3♯ · E 4♯ · B 5♯ · F♯ 6♯ · C♯ 7♯ ·
   F 1♭ · B♭ 2♭ · E♭ 3♭ · A♭ 4♭ · D♭ 5♭ · G♭ 6♭ · C♭ 7♭
   (and the relative minors). Enharmonic keys (F♯/G♭, C♯/D♭, B/C♭) are
   separate entries because spelling differs.
   Stored as `keyFifths` (−7…+7, same as MusicXML `<fifths>`) + `keyMode`.
3. Clef: Treble, Treble 8vb (guitar / male voice: written one octave above
   sounding), Bass, Alto, Tenor. Default by mode: Voice → Treble,
   Guitar → Treble 8vb, Piano → treble or bass by register (split at C4) in
   the LCD, grand staff in SCORE.

Live spelling rules (deterministic, no look-ahead):
- Diatonic pitch class → the key's letter and accidental.
- Chromatic pitch class → melodic direction from the previous stable note
  (ascending → raised form, descending → lowered form); no previous note →
  key preference (sharp keys → sharps, flat keys → flats; C major/A minor →
  sharps). A repeated chromatic note keeps its spelling within a phrase.
- D♭ ≠ C♯: `letter` + `alter` are stored and displayed, not only MIDI.
- The octave number follows the LETTER (scientific pitch notation):
  C♭4 sounds as B3 (MIDI 59); B♯3 sounds as C4 (MIDI 60).
  `writtenOctave` is derived from (midi − alter), then shifted by the clef
  (Treble 8vb: sounding E2 is written E3).
- Double sharps/flats only where the key requires them (e.g., F𝄪 as the
  leading tone of G♯ minor).

### 22.3 LIVE tab — studio rack

Visual language: a recording-studio rack. Dark rack units with ears and
screws, mixing analog elements (VU needle on a backlit face, knobs, toggle
switches) and digital elements (LCD/VFD panel, 7-segment counters, LED
ladders). Drawn as vectors (no photo textures), high contrast, HiDPI-scalable.

```
┌─ LIVE ─────────────────────────────────────────────────────────────────┐
│ ┌ INPUT ──────────────────────┐  ╔═ LCD ════════════════════════════╗ │
│ │ ANALOG VU     PEAK dBFS     │  ║  Am7/G    ▮▮▮▮▮▮▯ 0.82  ● CONF   ║ │
│ │   ╲ needle    ▮▮▮▮▮▮▯▯ −6   │  ║  alt: C6/G · Am/G     212 ms     ║ │
│ │               CLIP ○        │  ║  bass G2 ● settled               ║ │
│ ├ SCOPE ──────────────────────┤  ║ ──────────────────────────────── ║ │
│ │ ∿∿∿∿∿∿ live waveform ∿∿∿∿∿∿ │  ║  C♯5   +12¢   554.4 Hz   38 ms   ║ │
│ │                             │  ║  𝄞 ♯♯ ──♯●──  (mini staff)       ║ │
│ └─────────────────────────────┘  ╚══════════════════════════════════╝ │
├────────────────────────────────────────────────────────────────────────┤
│ INSTRUMENT: [fretboard] [keyboard] [waterfall]                         │
├────────────────────────────────────────────────────────────────────────┤
│ CHORD TIMELINE  | G | Em | C | D | Am7/G ▸                              │
├────────────────────────────────────────────────────────────────────────┤
│ TRANSPORT  (● REC)  00:42.318   ♩=92  ● ○ ○ ○  4/4   METRO ▣  COUNT-IN ▣ │
│ STATUS  capture 8.1 ms · proc 31.4 ms · display 16.7 ms · CPU 6 % · xrun 0 │
└────────────────────────────────────────────────────────────────────────┘
```

The layout above is the default preset; every unit is a rack module the user
can reorder, hide or add (section 22.4).

Units:
- **Analog VU**: needle with standard VU ballistics (300 ms integration),
  scale −20…+3 VU, 0 VU = −18 dBFS (calibratable).
- **Peak LED ladder**: dBFS, peak hold 1.5 s, CLIP LED latched until
  clicked, target zone marked (−18…−6 dBFS).
- **Scope**: live waveform line (min/max envelope, scrolling, ~5 s,
  phosphor style). Mono modes: toggle to a triggered oscilloscope that uses
  the detected period as trigger, so the waveform stands still while a note
  is held.
- **LCD** (top right):
  - Upper line = chord: symbol with key-aware spelling and slash bass,
    confidence bar, PROVISIONAL / CONFIRMED LED, bass-settled LED,
    alternatives, reason, latency in ms.
  - Lower line = monophonic note: spelled per 22.2, written octave, cents
    needle, Hz, latency in ms, mini staff with the chosen clef, key
    signature and accidental.
  - Chord modes: the lower line shows the bass note. Mono modes: the upper
    line is hidden and the note line grows.
- **Instrument view** (all three in v1.0, user picks one):
  - Guitar fretboard with tuning/capo, showing the most likely voicing
    (positions are ambiguous — labelled "likely shape").
  - Piano keyboard with lit keys, bass highlighted.
  - CQT waterfall: scrolling log-frequency spectrogram with a note grid.
- **Chord timeline**: confirmed chords solid, current provisional outlined,
  bar lines from the metronome grid.
- **Transport**: big REC button (forces metronome + count-in); time counter
  mm:ss.mmm (7-segment); BPM (7-segment) + beat LEDs; meter; metronome and
  count-in toggle switches; tap tempo; "→ SCORE" (fast path).
- **Status strip**: capture, processing and display latency in ms, CPU %,
  xruns, recorder gaps. Always visible.

Milliseconds are always shown: every detected value in the LCD carries its
measured latency (onset → publish), a provisional chord shows the elapsed ms
until confirmation, the quality selector shows T_low in ms, the status strip
shows the pipeline ms. A value that is not measured shows "— ms (not
measured)", never an estimate.

### 22.4 Modular rack

Each LIVE unit is a module, like hardware in a studio rack:
- Catalog (fixed list, no plugin system): Analog VU, Peak LEDs, Scope,
  LCD (chord + note), Tuner (big cents needle), Fretboard, Keyboard,
  Waterfall, Chord timeline, Transport, Status.
- Size: each module is full width or half width (two half modules share a
  row, e.g., Input | LCD), and has a height in rack units (1U–4U) where it
  makes sense (Scope, Waterfall).
- Edit mode (toggle "Edit rack"): drag to reorder, remove, add from the
  catalog, resize. Outside edit mode modules are fixed (no accidental drags
  while playing).
- Pinned: Transport and Status cannot be removed (REC access and the ms
  readout must always be on screen). Modules may appear only once.
- Presets: default per mode (Voice: Input + LCD, Tuner, Scope, Timeline;
  Guitar: Input + LCD, Fretboard, Timeline; Piano: Input + LCD, Keyboard,
  Waterfall, Timeline), plus user presets saved as JSON
  (`[{ "module": "Scope", "width": "half", "heightU": 2 }, …]`).
- Modules are view-only: changing the rack never touches the analysis
  pipeline, so it is allowed during a LIVE session (not while REC is
  armed/running, to avoid mis-clicks). Hidden modules are not rendered.
- Modules show data valid for the running mode only (e.g., Fretboard only in
  GuitarChords); a module not applicable to the mode is greyed out in the
  catalog.

### 22.5 Stage mode

Toggle (button or `S`). Full screen, only: chord (huge), note (huge) with
cents bar, beat LEDs, REC state, one status line in ms. Readable at 2–3 m.
Analysis is unchanged; only the view is reduced.

### 22.6 Shortcuts

`Space` REC start/stop · `M` metronome · `T` tap tempo (not during REC) ·
`S` stage mode · `Esc` leave stage mode.

All user actions (Rec, Metronome, TapTempo, StageMode, NextPreset, …) go
through ONE action table; keyboard shortcuts are just one binding source.

OPEN ITEM — MIDI controllers (footswitch, pads, knobs): not in v1.0, but the
action table is the attachment point. A future MIDI binding source maps
note/CC messages to the same actions ("MIDI learn"), with bindings saved in
JSON next to the rack presets. MIDI input library, device hot-plug and
continuous controls (e.g., a knob for metronome volume) are to be decided
when this item opens. MIDI input is handled on a non-real-time thread and
never enters the audio callback.

### 22.7 Rendering rules

- Units updated per frame (VU, LEDs, scope, LCD, instrument view,
  waterfall, timeline) are custom-drawn with Skia (Avalonia custom
  rendering / composition), not control trees with data bindings.
- Zero allocation per frame on the C# side: preallocated snapshot struct,
  buffers, paths, and text layouts. `GCSettings.LatencyMode =
  SustainedLowLatency` while LIVE runs — note it only avoids blocking Gen2
  collections; Gen0/Gen1 still run if anything allocates, so:
  - No string interpolation / `ToString()` in the render path. Numbers are
    formatted with `TryFormat` into preallocated `Span<char>` buffers.
  - Text objects (Skia text blobs / layouts) are rebuilt only when the
    displayed value changes; numeric readouts refresh at ~10 Hz (readable
    rate), needles/meters/scope at full frame rate.
  - CI/dev check: allocated bytes per frame (`GC.GetAllocatedBytesForCurrentThread`)
    must be 0 in the steady-state LIVE render loop.
- One snapshot read per rendered frame; frame time is measured and shown as
  "display ms".
- Themes: dark rack (default) and light. Languages: PT-BR and EN.

## 23. TESTS AND METRICS [MEASURE]

Catch2 (core), xUnit (C#). Synthetic corpus: WAVs generated from MIDI with
models (sines + harmonics, guitar/piano ADSR, optional noise and reverb, global
tuning offset in cents).

CI Metrics (mir_eval/MIREX style):
- Raw Pitch Accuracy (±50 cents) — voice.
- Weighted Chord Symbol Recall — chords.
- Key accuracy; latencies p50/p95 (time-to-first-pitch, time-to-stable, bass
  preview, chord preview, chord confirmation) measured on synthetic input with
  known onsets; xruns.
- **Zero-Allocation / Non-Blocking Assertion:** RealtimeSanitizer build
  (Linux/macOS) + thread-scoped `operator new` trap (all platforms). Any
  allocation, lock or blocking syscall inside the audio callback OR the
  steady-state loop of `RealtimeAnalysisThread` fails CI.
- ABI layout test (section 22), `-Wpadded` clean ABI headers.
- Event log: forced queue overflow → no stuck notes/chords, log marked
  incomplete; chord change → previous chord ends exactly at the new backdated
  onset (no overlap); REC stop flushes open events.
- Metronome with different input/output devices: simulated 50 ppm drift over
  10 min → beat error on the input grid ≤ 1 ms.
- C# LIVE render loop: 0 allocated bytes per frame in steady state.
- MusicXML XSD validation.
- STUDIO: separation quality (SDR/SIR, BSS-eval style) on synthetic mixes;
  choir multi-f0 and section-assignment accuracy; stage-cache invalidation
  test (changing a stage option re-runs only downstream stages).
- Mandatory adversarial cases: silence, noise, speech, incomplete chords,
  arpeggios, global detuning, vibrato, glissando, voice+chord, chord change
  with sustained previous bass (onset gating), long silence after reverb tail
  (denormals).

## 24. INCREMENTAL PLAN (every step compiles, tests, and measures)

Priority: solve LIVE first, concretely and coherently, and make it feed the
score/chord chart. Release scope:
- **v1.0 = M0–M6:** LIVE (voice + chords) with REC, metronome, and the fast
  path LIVE → score / chord chart / MusicXML / MIDI.
- **v1.x = M7+:** STUDIO library/import, full STUDIO pipeline from audio, editor/multitrack,
  separation, choir, VST3.
No v1.x work starts before v1.0 acceptance criteria (section 4) are met and
measured.

- **M0 Skeleton:** SessionConfig + mode×quality table + ana_start/ana_stop + audio callback + `ma_pcm_rb` rings + atomic wait/notify + FTZ/DAZ + thread priorities + triple buffer + event queue + REC (recorder ring, WAV + sidecar) + C ABI + START tab (buttons, audio settings) + metronome output/count-in + LIVE rack shell (VU, peak LEDs, scope, transport, status strip in ms) + latency/xrun/allocation trap + RTSan CI + reference machine fixed.
- **M1 Voice:** YIN (time-domain SIMD & FFT autocorrelation) + cents + tuner (every hop) + melodic tracker (zero-alloc POD).
- **M2 CQT Engine:** Octave-decimated filter bank (IIR half-band) + sparse CSR kernels + chroma + global tuning estimator + microbench.
- **M3 Chords:** POD templates + bounded matching + chord tracker + ambiguity reasons in GUI.
- **M4 Bass & Temporal:** Bass preview (periodicity) + CQT confirmation + computed settle + inversions + onset spectral flux + onset-gated windows + arpeggio accumulator.
- **M5 Music Theory:** Key cascade + spelling (letter/alter/written octave, section 22.2) + chord symbols + Roman numerals + cadence rules + LCD unit with mini staff.
- **M5b Rack modules:** fretboard, keyboard, CQT waterfall, tuner, chord timeline, modular rack (edit mode, presets JSON), stage mode, shortcuts.
- **M6 LIVE → SCORE:** SCORE tab + C# event log + backdated event timing + quantization on the REC metronome grid + ties across bar lines + stages 5–8 (spelling, chord symbols, Roman numerals, score, chord chart) + MusicXML/MIDI/JSON/text writers + Verovio score view. **v1.0 release.**
- **M7 STUDIO from audio:** library/import + stage pipeline + stage cache + stages 0, 2 (built-in effects), 4 (full reprocessing) + "refine from audio".
- **M7b Editor:** stage 1 single-track editor (section 20.1).
- **M7c Refined Piano & NNLS:** Bounded-iteration NNLS-chroma + inharmonicity partial correction ($B$ coefficient).
- **M8 STUDIO separation + multitrack:** demucs.cpp stage 3 (2/4/6 stems) + stems as tracks (mute/solo/gain/pan, per-track analysis, staff assignment) + voice+instrument score (section 21).
- **M9 STUDIO choir:** Choir multi-f0 + section assignment (SATB) + multi-staff score.
- **M10 Export audio:** edited / separated stems and mix-down to WAV (miniaudio encoder).
- **M11 VST3 host:** out-of-process PluginHost + scanning + offline render + latency/tail compensation + state (section 20.2).

Process: Interfaces first; reference implementation; then optimization with
before/after measurement. No invented performance numbers. Any uncertain result
must appear as uncertain in the UI.
