# STUDIO — plan

Status: plan (2026-10-02). Supersedes the order of spec §24 M7–M11; spec §20
stays the reference for stage options. LIVE is frozen until real-instrument
testing is possible again.

## What STUDIO is

The heavy, offline layer. A REC take (or an imported file) is treated, then
reanalysed with everything LIVE cannot afford, and only then turned into
notation and exported:

```
take ─▶ edit ─▶ effects ─▶ separation ─▶ analysis per stem ─▶ review ─▶ score ─▶ export
        trim     gain/EQ    Demucs        voice: notes          key, meter,   staves   MusicXML
        cut      HP/LP      vocals        harmony: chords       tempo map,    chords   MIDI
        fades    gate       bass, drums   bass: bass line       corrections   numerals JSON / text
        normalize           guitar, piano drums: beats          (user)                 WAV stems
```

Not 100 %, but far more precise than the live events, because it has:
look-ahead (decide a note knowing what follows), the whole take as context,
high-resolution analysis (24–36 bins per octave, long windows, multi-pass),
and clean stems (the voice read alone, the guitar read without the voice).

Rules kept from the spec: never touches a running LIVE session; the original
take is never modified (edits are a list, outputs are cached); every result
carries a confidence and the UI never presents it as certain; key and meter
are the user's (STUDIO may suggest, never applies by itself).

## Layout: a mixing console (decision 2026-10-02)

STUDIO looks and works like LIVE's rack, as a mixing desk. Mockup:
https://claude.ai/artifact/1cES4MCBM9otq1qSVa3ed5 (three screens).

1. **Desk (overview):** stage bar (Library · Edit · Desk · Analyse ·
   Review → Score), library on the left, track timeline on top
   (non-destructive trim / cut / fades / normalize across all tracks), the
   console below. One channel strip per track: the original mix, then
   one per separated stem (voice, guitar, bass, drums, other), plus master.
   Per strip: source and separation confidence, insert slots, analysis type
   (notes / chords / bass / beats / off), staff assignment, pan, mute / solo,
   meter, fader.
2. **Channel:** the strip's insert chain as rack modules, in order and
   reorderable (high-pass, parametric EQ with its curve over the stem's
   spectrum, noise gate, + insert; VST3 later), ending in the channel's
   analysis module (type, resolution, vocabulary, whole-take decoding) with
   a preview of its result. Listening, export and analysis use the same
   chain; re-running one stage re-runs only it and what follows.
3. **Analysis and review:** one lane per channel on the bar grid (voice
   piano roll, chord lane with confidences, bass notes, beats), click to
   correct; a side panel where the user sets key (none by default), meter,
   tempo and quantization — STUDIO only suggests — and a list of low-
   confidence spots to review, then "Generate score".

The milestones below map onto it: S1 library + analysis lanes, S2 the
timeline editing, S3 the insert modules, S4 the stem channels, S5 the
per-channel analysis, S6 the review panel, S7 the score.

## Milestones

Each one compiles, tests and measures, and is usable on its own.

### S1 — Library, jobs, offline reanalysis (spec M7)
- Library: REC takes + imported WAV/FLAC/MP3 (miniaudio decoder) / OGG
  (stb_vorbis); list with duration, mode, date; open, rename, delete.
- Take project: `take-….studio.json` next to the take — stage options, edit
  list, user corrections. Stage cache keyed by hash(options + upstream).
- Job system: C++ `OfflineAnalysisThread` pool behind the C ABI
  (`ana_post_*`: start, progress, cancel, fetch results as POD arrays); C#
  tasks; progress bar and cancel in the tab.
- Offline reanalysis with the existing pipelines run as HighPrecision over
  the whole file (no device, like `dz_wav`), plus look-ahead where it is
  cheap: chord labels decided per segment (already in the tracker), notes
  with a forward pass.
- SCORE input switches to the STUDIO result; the raw-take preview stays.
- Measure: the band mix (`tools/loopback/mix.py`) and the plucked-guitar /
  voice takes, offline vs LIVE.

### S2 — Editor (spec M7b, §20.1)
- Waveform from a peak mipmap (O(visible pixels) zoom), playback with
  cursor, selection.
- Non-destructive edit list: trim, cut, delete, split, mute region, fades
  (linear / equal-power), gain envelope, normalize (peak / RMS).
- Bar/beat grid overlay from the take's metronome; snap.

### S3 — Built-in effects (spec stage 2)
- Gain, high-pass / low-pass, parametric EQ (4–6 bands), noise gate. Applied
  before separation and analysis; preview by listening.
- VST3 stays last (S8): high effort, mainly the same denoise/EQ.

### S4 — Separation (spec M8, §20.3)
- demucs.cpp (MIT, C++17 + Eigen, CPU) with Demucs v4 weights (MIT):
  4 stems (vocals, drums, bass, other) and 6 stems (+ guitar, piano).
  Weights downloaded once (hundreds of MB), checksum-pinned.
- Cancellable job with progress; result cached per take + options. Expect
  minutes per song on CPU — MEASURE on the reference machine before
  promising anything; multi-threading measured too.
- Stems become tracks: mute / solo / gain / pan for listening, waveform,
  analysis type per track (voice, chords, bass, drums, off).
- Every stem shows a confidence; separation is never shown as perfect.

### S5 — Analysis per stem (spec stage 4, §21)
- Voice stem: YIN + MPM cross-check, Viterbi smoothing over the whole take
  (pYIN-style), note segmentation with look-ahead, vibrato and glides kept
  as one note.
- Harmony stems (guitar / piano / other): CQT 24–36 bpo down to 27.5 Hz,
  NNLS chroma, inharmonicity correction for piano (spec M7c), chord decoding
  over the whole take (HMM / Viterbi with a change penalty, so a chord lasts
  until the harmony changes), chord vocabulary option (triads / sevenths /
  extended).
- Bass stem: bass line as notes; inversions from it, not from the mix.
- Drums stem: beat and downbeat tracking → tempo map; flags tempo drift
  against the metronome.
- Conflicts reduce confidence (e.g. voice note vs chord), never forced.

### S6 — Review: theory and rhythm (spec stages 5–6)
- Key and meter: chosen by the user; STUDIO shows a suggestion (key profile
  over the whole take, meter from the downbeats) with its confidence.
- Tempo: session metronome by default; tempo map from S5 as an option.
- Quantization options: grid (from the beat unit down to 1/32), tuplets,
  swing, strength, pickup bar.
- Corrections: click a note or chord to change it (pitch, duration, symbol);
  stored in the take project and kept when upstream stages re-run.

### S7 — Score and export (spec stages 7–8)
- Staves per stem: voice (clef auto by range), chord symbols above, guitar
  (treble 8vb; tablature later), piano grand staff, bass (bass clef).
- Roman numerals and cadences only with a key set.
- MusicXML (XSD-validated, opened in MuseScore), MIDI type 1 (one track per
  stem + tempo map), JSON (full result with confidences and alternatives),
  text chart, WAV of edited / separated audio (spec M10).

### Later
- S8: VST3 host, out of process (spec M11, §20.2).
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

## Decisions needed before S1

1. Order after S1: separation first (S4, biggest precision gain, heaviest)
   or editor + effects first (S2–S3, simpler, needed for real takes)?
   Proposal: S1 → S4 → S5 → S7, editor and effects in between as needed.
2. Separation model: 4 stems (better quality) vs 6 stems (guitar / piano
   tracks, weaker on guitar). Proposal: 4 stems by default, 6 as an option.
3. Guitar output: chord symbols only, or also tablature later?
4. Hardware: CPU only (portable, slow) or optional GPU later?
