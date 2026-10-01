# Dissonância

Real-time, offline music analyzer. Play or sing, and it shows the note or the
chord as you play: spelled for the key you chose (D♭ is not C♯), with bass and
inversion, on a studio-rack desktop app. A REC take keeps the bar grid and the
confirmed events, which will feed a score and chord chart.

- **LIVE**: lowest latency, one dedicated pipeline per mode, chosen before the
  session (voice, instrument melody, guitar / piano / general chords).
- **STUDIO / SCORE** (planned): heavy post-processing, separation, score.
- Core in C++20, app in C# (.NET 10, Avalonia) through a plain C ABI.
  Cross-platform (Windows, Linux, macOS). No web, no cloud.

| Voice: A3 → A♯3 → B3 | Chords |
|---|---|
| time-to-first-pitch 30 ms, stable note 50 ms | bass settled 220 ms, chord confirmed 0.4 s |

See [`docs/IMPLEMENTATION.md`](docs/IMPLEMENTATION.md) for what is built and
measured, and [`docs/spec.md`](docs/spec.md) for the full design.

## Status

| Milestone | Status |
|---|---|
| M0 skeleton (audio, threads, snapshot, events, REC, metronome, C ABI) | done, some open items |
| M1 voice / melody (YIN, tracker, spelling) | done |
| M2 CQT, chroma, tuning | done |
| M3 chords (templates, tracker, ambiguity, key/cadence context) | done |
| M4 onsets, bass, inversions, arpeggios | done |
| M5 music theory (Roman numerals, cadences) | done |
| M5b rack modules (tuner, waterfall, fretboard shapes, edit mode, presets) | done |
| M6 LIVE → score (Verovio), MusicXML / MIDI / JSON | done |
| M7+ STUDIO | planned |

## Build

Requirements: CMake ≥ 3.24, a C++20 compiler (GCC 13 / Clang 17 / MSVC 2022),
Ninja (optional), .NET SDK 10.

```bash
# core + tests (Catch2 and Verovio are fetched by CMake; Verovio builds once, ~2 min;
# -DDZ_WITH_VEROVIO=OFF skips it and the SCORE tab shows text only)
cmake -S core -B core/build -G Ninja
cmake --build core/build
core/build/dz_tests

# SCORE checks (quantization, ties, MusicXML/MIDI writers)
dotnet run --project tests/score

# app (copies the native library from core/build)
cd app
dotnet run
```

On WSL2 with WSLg, use `DISPLAY=:0 dotnet run`. The WSLg audio path works for
development, but it is not valid for latency acceptance (see the spec).
If the native library is missing, the app runs on simulated data and says so.

Takes are saved to `~/Music/Dissonancia/` (or `~/Dissonancia/`) as WAV plus a
JSON sidecar. The SCORE tab reads the newest take and exports MusicXML, MIDI,
JSON and a text chord chart next to it.

## Keys (LIVE)

`Space` REC · `M` metronome · `T` tap tempo · `S` stage mode · `Esc` leave stage mode

The LIVE rack is modular: EDIT RACK reorders, resizes, adds and removes
modules; the layout is saved per mode.

## Layout

```
core/   C++ engine: include/dissonancia.h (C ABI), src/, tests/, third_party/miniaudio
app/    C# Avalonia desktop app (Score.cs: take -> score, MusicXML/MIDI/JSON/text)
tests/  score/: runnable checks for the SCORE layer, rack presets, fretboard shapes
tools/  shot/: headless screenshots of the app
docs/   IMPLEMENTATION.md, spec.md, spec.v2.1.md, graph/ (knowledge graph)
```

## License

MIT. Dependencies are MIT / public domain (miniaudio, Catch2, Svg.Skia).
Verovio (LGPL-3.0, SCORE engraving) is a separate shared library loaded at run
time, built from its unmodified source; its Leipzig font is SIL OFL 1.1.
