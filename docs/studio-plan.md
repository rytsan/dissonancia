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

## Layout: Dissonância's own rack, Ableton-like workflow (decision 2026-10-02)

Workflow and layout follow Ableton Live (arrangement, device chain, mixer);
the look is Dissonância's own 19" rack — not a clone of any DAW. Same visual
language as the LIVE rack. Mockup: https://claude.ai/artifact/1cES4MCBM9otq1qSVa3ed5.

- Rack frame: 19" rails with mounting holes on both sides; every part is a
  screwed rack unit with a model name.
- Digital look as in LIVE: orange gas-plasma dot-matrix readouts (lit dots
  glowing over a grid of unlit dots) for the transport, channel numbers,
  analysers and the console's dB; phosphor-green screens for the track
  recorder, library and EQ curve.
- VU meters as on analog racks: backlit cream face, drawn scale with the red
  zone, needle with its shadow, chrome bezel and the diagonal reflection of
  the glass.
- **DS-T Transport (1U):** tab selector, hardware keys (TAP, ▶, ■, ●, loop)
  with LEDs, seven-segment readouts (BPM, meter, grid, position in
  bars.beats.sixteenths, loop start / length), LEDs of the optional
  components present (Demucs, Verovio, GPU), CPU, key.
- **DS-L Library:** phosphor-green list in a bezel (takes, imported files,
  effect units, analysers).
- **DS-A Track recorder:** channel cards with masking-tape scribble strips
  (name), channel number, solo / arm LEDs, level; a phosphor screen with the
  bar ruler, loop brace, playhead, clips with their waveforms, and under each
  separated track its transcription lane (notes, chords with confidence,
  bass notes, beats). Light scanlines.
- **DS-C Channel chain:** the selected channel's units in fixed studio order,
  joined by patch cables: DS-1 TRIM (VU), DF-2 FILTER, DG-3 NOISE GATE,
  DQ-4 PARAMETRIC EQ (curve on a phosphor screen), DC-5 COMPRESSOR (gain
  reduction LEDs), then the analyser at the tap (DA-N notes, DA-C chords,
  DA-B bass, DA-T beats; post-inserts, pre-fader). DL-6 LIMITER on master.
- **DS-M Console:** strips with channel number, tape label, source and
  separation confidence, trim, insert slots by model with LEDs, analyser,
  pan, mute / solo, LED meters, fader, seven-segment dB; master strip with
  EQ, bus compressor, limiter and a pair of VU meters.

### Signal flow and measuring tubes (decision 2026-10-15)

The rack reads like a desk or a pedalboard: where the signal comes from, where it goes and
where it splits is drawn, and every patch cable carries its level (core colour and a middle LED:
dark = nothing, amber = signal, bright = over -6 dBFS, red = over 0).

- **DS-P Signal flow (under the transport):** FONTE → MIX channel directly, and FONTE → DS-S
  separator → one channel per stem (split bus); every channel that sounds drops into the sum bus
  → DS-M → MASTER → SAÍDA (headphones / bounce); every analysed channel sends a copy from its tap
  (dashed, post-inserts, pre-fader) along the send bus to DA-N / DA-C / DA-B / DA-T → SCORE.
- **DS-C as a pedalboard:** IN plate → units in studio order, each gate / EQ / compressor with a
  footswitch and its red LED (off = BYPASS), the level on every cable between units (core:
  peak after each unit) → TAP, which splits: OUT to the channel's fader, a send to the analyser.
- **DS-M as a desk:** each strip shows its source (FONTE direto / DS-S saída n), inserts with
  the level they hand on, the analysis send, then pan, mute / solo, fader, and drops into the
  sum bus drawn to the master; the master chain BUS → EQ → COMP → FADER → DL-6 → SAÍDA.
- **DS-V Measuring tubes (phosphor CRTs, right column):** vectorscope with the phase
  correlation bar, oscilloscope L / R (triggered), spectrum of the master or the selected
  channel's tap (1/12 octave, peak hold), loudness history (30 s, momentary and short-term, -23
  EBU and -14 streaming marks) with plasma readouts: integrated LUFS, short-term, momentary,
  true peak, LRA (BS.1770-4 / EBU R128 in the core; ZERAR resets).
- The rack is taller than a 900 px window and scrolls; in console view the library steps aside.
- **REC in STUDIO (decision 2026-10-15):** ● on DS-T records through the core's recorder — the
  same as LIVE's REC: input, rate, count-in and metronome from START, a take WAV + JSON sidecar
  in the takes folder. If START has not opened the input, the ● opens it with the START options.
  While armed / recording the transport shows the count-in or the recorded time and the input
  level, and DS-P's FONTE becomes ENTRADA · ● REC with the device and its level; the player stops.
  When the take is written it opens in STUDIO. LIVE is unchanged.

## Milestones (in studio order)

Each one compiles, tests and measures, and is usable on its own. Built in
this order because each stage needs the one before it, as in a studio.

### S1 — Session: library, playback, jobs (done 2026-10-02)
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

### S2 — Editing (spec M7b, §20.1) — done 2026-10-02
- Waveform from a peak mipmap (O(visible pixels) zoom), selection, bar/beat
  grid from the take's metronome, snap.
- Non-destructive edit list: trim, cut, delete, split, mute region, fades
  (linear / equal-power), clip gain, normalize (peak / RMS).
- Edits apply to all tracks at once (time-aligned), also after separation.

### S3 — Separation: the multitrack (spec M8, §20.3) — done 2026-10-02
- demucs.cpp (MIT, C++17 + Eigen, CPU) with Demucs v4 weights (MIT):
  4 stems (vocals, drums, bass, other) by default, 6 (+ guitar, piano) as an
  option. Weights downloaded once, checksum-pinned.
- Cancellable job with progress; cached per take + edit list + options.
  Minutes per song on CPU expected — MEASURE before promising anything.
- Each stem becomes a track and a channel; every stem shows a confidence.
  Without separation the mix is one channel and everything still works.

### S4 — Mixing: channel strip and master (spec stage 2) — done 2026-10-02
- Strip in console order: trim → HP/LP filters → gate → parametric EQ
  (4–6 bands, curve over the spectrum) → compressor → fader → pan.
- Master bus: EQ, bus compressor, limiter (listening and bounce only).
- Metering per channel and master; mute / solo; the analysis tap
  (post-inserts, pre-fader) exposed per channel.
- Built-in processors only; VST3 is S8.

### S5 — Transcription per channel (spec stage 4, §21) — done 2026-10-03
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

### S6 — Review (spec stages 5–6) — done 2026-10-03 (meter suggestion, swing and pickup left for later)
- Key and meter set by the user; STUDIO shows its suggestion (key profile
  over the whole take, meter from the downbeats) with a confidence.
- Tempo: session metronome by default; the tempo map from S5 as an option.
- Quantization: grid (beat unit down to 1/32), tuplets, swing, strength,
  pickup bar.
- Corrections by clicking a note or chord; kept in the take project and
  preserved when upstream stages re-run. Low-confidence spots listed.

### S7 — Delivery: score and export (spec stages 7–8, M10) — done 2026-10-03 (piano / guitar note staves wait for a polyphonic note transcriber; stems delivered as separated)
- Staves per channel: voice (clef auto by range), chord symbols above,
  guitar (treble 8vb), piano grand staff, bass (bass clef).
- Roman numerals and cadences only with a key set.
- MusicXML (XSD-validated, opened in MuseScore), MIDI type 1 (one track per
  channel + tempo map), JSON (full result with confidences), text chart,
  WAV bounce of the mix and of each stem.

### S8 — VST3 inserts, out-of-process host (spec M11, §20.2) — in progress
- SDK: VST3 SDK 3.8.0 (MIT), its four repos (base, pluginterfaces, public.sdk, cmake) as
  pinned tarballs; only the SDK libraries are built (no VSTGUI, no examples). Optional
  component `DZ_WITH_VST3`: the `dz_plugin` helper ships next to the app when built.
- Step 1 (done 2026-10-15) — scanning: `dz_plugin list` (standard VST3 folders +
  `DISSONANCIA_VST3_PATH`), `dz_plugin scan <bundle>` one process per bundle (15 s timeout, a
  crash loses only that bundle), cache by modification time in the app data folder. DS-L shows
  EFEITOS VST3 (name, vendor; bundles that failed show their error). Test fixture: the core's
  own `DZ Test Gain` plugin (gain parameter, state), never installed into the user's folders.
- Step 2 (done 2026-10-15) — audio: `dz_plugin render <bundle> <cid> in.wav out.wav` through
  one effect, offline (kOffline, 32-bit float, 1024-frame blocks), component + controller
  connected (the SDK's PlugProvider), stereo buses or the plugin's own, parameters at the start
  (`--param id=value`), state blob in / out. Checked: +6 dB through DZ Test Gain sample-exact,
  the saved state renders the same file, a wrong class is an error from the helper.
- Step 3 (done 2026-10-15) — the unit in DS-C: DV-0 VST3 between IN and DS-1 TRIM, up to five
  effects per channel in order (click a slot: on / bypass; ✕ removes; + VST3 opens the library's
  effects, a click inserts on the selected channel). Offline means before the chain: the
  channel's source (the edited take or the stem) is rendered insert by insert in the helper and
  the player reads the render (core `ana_player_set_source`), so the strip, the analysis tap and
  the bounce all hear it. Render cached by a key of the source and each insert's saved state;
  the key is part of the tap's cache key; analysis waits for a render. A failing plugin leaves
  the channel playing without its VST3, with the helper's error on the status line.
- Step 4 — latency (done 2026-10-15): the render asks the plugin's latency after its state and
  parameters are in, runs that many frames past the end and drops them from the start, so the
  render lines up with the take sample for sample (checked with DZ Test Gain's 64-sample
  look-ahead switch). Tails past the end are cut: a channel keeps the take's length.
- Step 4 — parameters (done 2026-10-15): `dz_plugin params` lists the controller's parameters
  after the state (title, units, steps, value, text); a click on an insert's name in DV-0 opens
  them as knobs where the analyser sits (eight a page; read-only, hidden, program and bypass
  parameters left out). A change is kept in the take project and rendered 0.7 s after the last
  one (the values are applied over the state at every render and are part of the render key).
- Step 4 — editor: the plugin's own editor in a native window owned by the helper.

### Later
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
