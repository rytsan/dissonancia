# STUDIO — plan

Status: plan (2026-10-02). Supersedes the order of spec §24 M7–M11; spec §20
stays the reference for stage options. LIVE is frozen until real-instrument
testing is possible again.

## What STUDIO is

The heavy, offline layer, in the order of a real studio. A REC take (or an
imported file) goes through the same stages a recording goes through in a
studio, and only then is it transcribed and delivered:

```
session ─▶ editing ─▶ separation ─▶ mixing (per channel) ─▶ transcription ─▶ review ─▶ delivery
takes      trim, cut   Demucs:       trim → HP/LP → gate      voice: notes      key,       score
import     fades,      the stereo    → EQ → compressor        chords            meter,     MusicXML
playback   clip gain,  take becomes  → [analysis tap]         bass line         tempo,     MIDI
           normalize   a multitrack  → fader / pan → master   beats             fixes      WAV, JSON
```

Order is fixed and mirrors real life (decision 2026-10-02):
- **Stages** follow a studio session: tracking (the take), editing,
  multitrack (here made by separation), mixing, then the transcriber's and
  the producer's work, then delivery.
- **The channel strip** follows a real console: input trim → filters
  (high-pass / low-pass) → gate → EQ → compressor → fader → pan → master
  bus. The order is not rearranged by the user.
- **Analysis taps each channel post-inserts, pre-fader** (like a direct out
  after EQ): the cleaned signal is what gets transcribed, and the listening
  fader never changes the transcription.
- **Master bus** (EQ, bus compressor, limiter) affects listening and the WAV
  bounce only, never the analysis.

Not 100 %, but far more precise than the live events, because it has:
look-ahead (decide a note knowing what follows), the whole take as context,
high-resolution analysis (24–36 bins per octave, long windows, multi-pass),
and clean stems (the voice read alone, the guitar read without the voice).

Rules kept from the spec: never touches a running LIVE session; the original
take is never modified (edits are a list, outputs are cached per stage);
every result carries a confidence and the UI never presents it as certain;
key and meter are the user's (STUDIO may suggest, never applies by itself).

## Layout: a mixing console

STUDIO looks and works like LIVE's rack, as a mixing desk. Mockup:
https://claude.ai/artifact/1cES4MCBM9otq1qSVa3ed5 (three screens).

1. **Mixing (overview):** stage bar Session · Editing · Separation · Mixing ·
   Transcription · Review · Delivery; library on the left; track timeline on
   top (non-destructive trim / cut / fades / normalize across all tracks);
   the console below. One channel strip per track — the original mix, one
   per separated stem (voice, guitar, bass, drums, other) — plus master.
   Per strip: source and separation confidence, the inserts in console
   order, the analysis tap, analysis type (notes / chords / bass / beats /
   off), staff assignment, pan, mute / solo, meter, fader.
2. **Channel:** the strip as rack modules in console order (1 trim,
   2 filters, 3 gate, 4 EQ with its curve over the stem's spectrum,
   5 compressor), then the analysis module at the tap (type, resolution,
   vocabulary, whole-take decoding) with a preview of its result, then
   fader / pan. Re-running one stage re-runs only it and what follows.
3. **Transcription and review:** one lane per channel on the bar grid
   (voice piano roll, chord lane with confidences, bass notes, beats), click
   to correct; a side panel where the user sets key (none by default),
   meter, tempo and quantization — STUDIO only suggests — and a list of
   low-confidence spots, then "Generate score".

## Milestones (in studio order)

Each one compiles, tests and measures, and is usable on its own. Built in
this order because each stage needs the one before it, as in a studio.

### S1 — Session: library, playback, jobs
- Library: REC takes + imported WAV / FLAC / MP3 (miniaudio decoder) / OGG
  (stb_vorbis); duration, mode, date; open, rename, delete.
- Take project `take-….studio.json` next to the take: stage options, edit
  list, user corrections. Stage cache keyed by hash(options + upstream).
- Playback engine (offline graph rendered to the output device): play,
  stop, locate, loop a selection, cursor. Everything after this is judged
  by listening.
- Job system: C++ `OfflineAnalysisThread` pool behind the C ABI
  (`ana_post_*`: start, progress, cancel, results as POD arrays); C# tasks;
  progress and cancel in the tab.

### S2 — Editing (spec M7b, §20.1)
- Waveform from a peak mipmap (O(visible pixels) zoom), selection, bar/beat
  grid from the take's metronome, snap.
- Non-destructive edit list: trim, cut, delete, split, mute region, fades
  (linear / equal-power), clip gain, normalize (peak / RMS).
- Edits apply to all tracks at once (time-aligned), also after separation.

### S3 — Separation: the multitrack (spec M8, §20.3)
- demucs.cpp (MIT, C++17 + Eigen, CPU) with Demucs v4 weights (MIT):
  4 stems (vocals, drums, bass, other) by default, 6 (+ guitar, piano) as an
  option. Weights downloaded once, checksum-pinned.
- Cancellable job with progress; cached per take + edit list + options.
  Minutes per song on CPU expected — MEASURE before promising anything.
- Each stem becomes a track and a channel; every stem shows a confidence.
  Without separation the mix is one channel and everything still works.

### S4 — Mixing: channel strip and master (spec stage 2)
- Strip in console order: trim → HP/LP filters → gate → parametric EQ
  (4–6 bands, curve over the spectrum) → compressor → fader → pan.
- Master bus: EQ, bus compressor, limiter (listening and bounce only).
- Metering per channel and master; mute / solo; the analysis tap
  (post-inserts, pre-fader) exposed per channel.
- Built-in processors only; VST3 is S8.

### S5 — Transcription per channel (spec stage 4, §21)
- Reads each channel at its tap, by its analysis type:
  - Voice / melody: YIN + MPM cross-check, Viterbi smoothing over the whole
    take (pYIN-style), note segmentation with look-ahead, vibrato and
    glides kept as one note.
  - Chords (guitar / piano / other): CQT 24–36 bpo down to 27.5 Hz, NNLS
    chroma, inharmonicity correction for piano (spec M7c), whole-take chord
    decoding (HMM / Viterbi with a change penalty), vocabulary option
    (triads / sevenths / extended).
  - Bass: bass line as notes; chord inversions come from it.
  - Beats (drums): beat and downbeat tracking → tempo map; drift against
    the metronome flagged.
- Conflicts between channels (voice note vs chord, bass vs chord root)
  lower confidence, never forced.

### S6 — Review (spec stages 5–6)
- Key and meter set by the user; STUDIO shows its suggestion (key profile
  over the whole take, meter from the downbeats) with a confidence.
- Tempo: session metronome by default; the tempo map from S5 as an option.
- Quantization: grid (beat unit down to 1/32), tuplets, swing, strength,
  pickup bar.
- Corrections by clicking a note or chord; kept in the take project and
  preserved when upstream stages re-run. Low-confidence spots listed.

### S7 — Delivery: score and export (spec stages 7–8, M10)
- Staves per channel: voice (clef auto by range), chord symbols above,
  guitar (treble 8vb), piano grand staff, bass (bass clef).
- Roman numerals and cadences only with a key set.
- MusicXML (XSD-validated, opened in MuseScore), MIDI type 1 (one track per
  channel + tempo map), JSON (full result with confidences), text chart,
  WAV bounce of the mix and of each stem.

### Later
- S8: VST3 inserts, out-of-process host (spec M11, §20.2).
- S9: choir by section, SATB (spec M9, §21) — experimental.

## How precision is measured

- Corpus with ground truth: synthetic mixes with stems and exact notes /
  chords (extend `mix.py`: more progressions, tempos, voicings, singers'
  ranges, drums), plus a few real recordings annotated by hand.
- Metrics per stage: chord time correct (root + triad family, and exact
  symbol), note onset / offset / pitch F-measure (mir_eval style, 50 ms
  onset tolerance), bass inversion accuracy, beat F-measure, separation SDR
  where stems are known.
- Every change reports before → after on the corpus; numbers in
  `docs/IMPLEMENTATION.md`, as for LIVE.

## Reliable in any environment (decision 2026-10-02)

Everything beyond the base is optional and used when it is available and the
take needs it; STUDIO always works with what the machine has:
- Base, always present: library, playback, editing, channel strip, the
  offline analysis of the mix as one channel, review, score and export.
- Separation: used when its weights are installed (downloaded on demand,
  checksum-pinned); 4 or 6 stems chosen per take. Without it the mix is one
  channel.
- Acceleration: CPU always; a GPU backend only when present and measured to
  agree with the CPU result.
- Optional outputs (guitar tablature, Verovio engraving, VST3 inserts) appear
  only when their component is there; missing ones say why, never fail.
- Each optional piece reports itself (present / missing / failed) in the
  STUDIO status line, and a take records which ones produced its result.
