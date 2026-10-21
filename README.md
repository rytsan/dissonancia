# Dissonância

Offline music analyzer for the desktop. Play or sing: LIVE shows the note, the
chord and the harmony as you play, spelled for the key (D♭ is not C♯). STUDIO
takes the recording through a studio session (editing, separation, mixing,
transcription, review) and delivers a score, MIDI and a chord chart.

- Core in C++20, app in C# (.NET 10, Avalonia) through a plain C ABI.
- Windows, Linux, macOS. No web, no cloud, no account.
- The UI is Dissonância's own 19" rack.

## LIVE

One dedicated pipeline per mode, chosen before the session:

| Mode | Shows |
|---|---|
| Música completa | the harmony of the whole band first, the chord played under it |
| Melodia | the sung or played melody (YIN + Basic Pitch) |
| Acústico / Acústico + voz | guitar chords (strummed or picked), bass, inversions; voice line |
| Piano | piano chords and bass |

**Chords from perceived notes.** The audio is turned into the notes a listener
hears: an A-weighted spectrum, peaks masked by louder neighbours (Bark scale),
virtual pitch, notes judged per register (bass, middle, treble) and above an
audibility floor. A hidden Markov model then picks the chord that explains
those notes, weighted by recency, sustain and register, with the key as a
prior. Two readings run side by side:

- **Harmony**: major and minor triads only, slow to change. Passing chords,
  colours and inversions fold into what a listener sums up (G … D … G). This
  is what LIVE leads with and what its timeline records.
- **Chord played**: every quality (7, maj7, m7, sus, add9, dim, 5), the line
  under it. Inversions (D/F♯) are an option in START.

Latency, from a change to the screen (`tools/loopback/latency.py`): first
pitch 30 ms, stable note 50 ms, bass settled 220 ms, chord played ~0.5 s,
harmony ~1 s. CPU about 1 %.

**Playground** (PLAYGROUND above the rack): 16 pads of your WAVs (click, or a MIDI pad
controller on notes 36–51), a sampler that plays one WAV on every other MIDI note (repitched from
its root), a 16-step sequencer on the metronome's grid (sample-accurate with the click and the
input), and a MIDI controller slot with MIDI learn (sequencer, REC, metronome, volumes). It plays
through the LIVE output; MIDI input is RtMidi (ALSA / WinMM / CoreMIDI). **Métricas**: the
pipeline's latency, input, CPU, losses, tuning, tempo and confidence as lit keys, one plotted.

## STUDIO

```
session ─▶ editing ─▶ separation ─▶ mixing ─▶ transcription ─▶ review ─▶ delivery
takes,     trim, cut,  Demucs v4     channel    notes, chords,   key,       score (Verovio),
import     fades, gain stems         strips     bass, beats      meter,     MusicXML, MIDI,
                                     + VST3                      fixes      WAV, JSON, chart
```

- **From LIVE**: a take recorded in LIVE is on the tracks when you open STUDIO, with its session
  (mode, tempo, meter, key).
- **Chords in detail**: the same notes model decoded over the whole take at
  once (past and future), every quality, and a bass that holds becomes a slash
  chord (G/B, D/F♯).
- **Mixing as a console**: every track is a strip (polarity, inserts, fader, pan, mute / solo,
  a post-fader AUX send); its inserts are modules in one lunchbox, patched with cables that are
  the signal graph: several cables into one jack are a bus, one output on several cables a
  parallel path or a send, each cable with its own level. Rendered about a second ahead in
  64-bit, a change heard ~50 ms later; the bounce is what you heard, sample for sample.
- **Notes of every instrument**: YourMT3+ (multi-instrument transcription),
  native through onnxruntime, on the GPU when the machine has one.

## Measured

Chord accuracy on GuitarSet (180 solo-guitar comping takes, 5 styles, MIREX
majmin: root and third must match):

| | before (chroma templates) | now (notes + HMM) |
|---|---|---|
| LIVE | 34 % | 69 % |
| STUDIO | 39 % | 71 % |

Per style (STUDIO): singer-songwriter 86 %, rock 77 %, bossa nova 71 %, jazz
62 %, funk 54 %. Synthetic fingerpicking 93–97 %, band mix 93 %. Weak spots:
funk and mixes with loud drums.

Dev tools for these numbers are in `tools/loopback/` (`guitarset.py`,
`picking.py`, `mix.py`, `chordscore.py`) and `core/build/dz_wav` (any WAV
through the LIVE or STUDIO path).

## We host nothing

Every machine downloads what it needs from the original source, pinned by
size and SHA-256, and builds its own artifacts:

- **GPU**: on START the app detects the machine and installs the matching
  onnxruntime build (CUDA on Linux + NVIDIA, DirectML on Windows, CoreML on
  macOS), checks it against the CPU result in a child process, and falls back
  to the CPU.
- **YourMT3+**: the app ships the ONNX graphs without weights; the weights are
  rebuilt locally from the authors' checkpoint and checked against a recipe.
- **Demucs**: ggml weights downloaded on demand.

Python is dev-side only (exports, prototypes, evaluation). Users never need it.

## Build

Requirements: CMake ≥ 3.24, a C++20 compiler (GCC 13 / Clang 17 / MSVC 2022),
Ninja (optional), .NET SDK 10.

```bash
# core + tests (Catch2, Verovio, demucs.cpp and the VST3 SDK are fetched by CMake;
# -DDZ_WITH_VEROVIO=OFF / DZ_WITH_DEMUCS=OFF / DZ_WITH_VST3=OFF skip the optional parts)
cmake -S core -B core/build -G Ninja
cmake --build core/build
core/build/dz_tests

# SCORE checks (quantization, ties, MusicXML/MIDI writers, rack presets)
dotnet run --project tests/score

# app (copies the native library from core/build)
cd app
dotnet run
```

On WSL2 with WSLg, use `DISPLAY=:0 dotnet run`; WSLg audio works for
development but not for latency acceptance. Without the native library the
app runs on simulated data and says so.

Takes are saved to `~/Music/Dissonancia/` (or `~/Dissonancia/`) as WAV plus a
JSON sidecar.

## Keys (LIVE)

`Space` REC · `M` metronome · `T` tap tempo · `S` stage mode · `Esc` leave stage mode

EDIT RACK reorders, resizes, adds and removes modules; the layout is saved per
mode.

## Layout

```
core/   C++ engine: include/dissonancia.h (C ABI), src/ (notes.cpp: perceived notes + chord HMM;
        chords.cpp: tracker; decode.cpp: STUDIO whole-take decoder), tests/, tools/ (dz_wav, dz_separate)
app/    C# Avalonia app: LIVE rack, STUDIO stages, Score, Accel (GPU setup), YourMt3, BasicPitch
tests/  score/: runnable checks for the SCORE layer, rack presets, fretboard shapes
tools/  loopback/: evaluation scripts · yourmt3/: ONNX export (dev) · shot/: headless screenshots
docs/   IMPLEMENTATION.md (what is built and measured), spec.md, studio-plan.md
```

## License

MIT. Dependencies: miniaudio (public domain), Catch2, Svg.Skia, Avalonia,
onnxruntime (MIT); Basic Pitch and YourMT3+ (Apache-2.0); demucs.cpp (MIT) with
Eigen (MPL-2.0) in the separate `dz_separate` helper, Demucs v4 weights (MIT,
Meta). Verovio (LGPL-3.0) is a separate shared library loaded at run time,
built from unmodified source; its Leipzig font is SIL OFL 1.1. GuitarSet
(CC BY 4.0) is used for evaluation only and is not distributed.
