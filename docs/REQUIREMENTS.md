# Requirements

Decided 2026-10-21. These requirements replace the old spec. [`IMPLEMENTATION.md`](IMPLEMENTATION.md)
says what is built; [`ROADMAP.md`](ROADMAP.md) says in what order the rest gets built.

## The idea

This is a study and a proof of concept, not a commercial product (2026-10-21). Non-commercial
data and models are fine; each keeps its own license, which the user accepts when fetching it.


**Dissonância is a music coach that needs no reference.** You play or sing, with or without a song
behind you. It hears what a trained musician would hear and measures it:
- the harmony and the chord played;
- the melody, rhythm, tuning and dynamics.

All of this comes from algorithms and needs no score. The coach returns metrics with their
confidence and writes a JSON of the session. An AI reads that JSON and gives feedback in words.

LIVE is the core. Everything else serves the coach:
- STUDIO is the slow, whole-take pass that makes the session report exact.
- SCORE writes down what was played.
- The playground is somewhere to practise against a groove.

A feature that does not feed the coach waits.

## Principles (kept from the old spec)

- **P1. Psychoacoustics first.** The analysis models what a listener perceives:
  - loudness at the level music is played (not A-weighting at 40 phon);
  - masking;
  - virtual pitch;
  - temporal integration;
  - judgement per register.

  Signal-processing shortcuts that a listener would not hear are bugs.
- **P2. Never fake certainty.** Every value has a confidence. "Not sure" is a valid answer, and the
  AI feedback must say it too.
- **P3. Measure, never promise.** Every figure comes from a named corpus and a named tool.
- **P4. Offline and portable.** No cloud is required. ML runs natively (ONNX, ggml). Weights are
  fetched from upstream at pinned versions. We host nothing.
- **P5. Real time stays real time.** No allocation, lock or I/O on the audio thread.

## R1 — LIVE hearing (the core)

| ID | Requirement | Acceptance |
|---|---|---|
| R1.1 | Harmony (the harmonic field: major / minor triads) | **≥ 90 %** majmin on the open real corpus (R5), measured **on screen**, scored from 0.5 s after each change |
| R1.2 | Harmony latency | the new harmony is on screen **≤ 0.5 s** after a change (median), ≤ 0.8 s p90 |
| R1.3 | Harmony steadiness | no more changes than the reference has (change ratio ≤ 1.2) |
| R1.4 | Chord played (7, maj7, m7, 9, sus2, sus4, add9, dim, 5; inversion as an option) | ≥ 70 % exact symbol on GuitarSet comping on screen; ≤ 0.4 s |
| R1.5 | Melody line (voice or lead) | raw pitch accuracy ≥ 90 % (±50 ¢) on vocadito; note on screen ≤ 100 ms |
| R1.6 | Bass note | ≥ 90 % on GuitarSet bass string notes; a low root under a full strum is heard |
| R1.7 | Key and tempo heard (display) | key ≥ 80 % on the corpus after 30 s; tempo within 2 % after 8 s |
| R1.8 | Works over a song playing in the room (YouTube) and on a solo instrument or voice | both reported separately in every measurement |

*Note on R1.1 and R1.2.* A display that is 0.5 s late is wrong for those 0.5 s after every change.
With chords lasting ~2 s, that alone costs ~25 % of "the time on screen". So R1.1 is scored from
0.5 s after each change, and R1.2 measures the delay on its own. The two together say "right, and
in time".

## R2 — Coach metrics (no reference needed)

Each metric is computed from the performance alone. Each carries its confidence and the evidence
it rests on.

| ID | Metric | Measured from |
|---|---|---|
| R2.1 | **Timing**: mean offset to the grid (rushing / dragging, ms), spread (ms), per beat position | onsets against the metronome, or against the heard beat when there is no metronome |
| R2.2 | **Tempo stability**: drift (BPM over the take), sections faster or slower | beat tracking |
| R2.3 | **Groove**: swing ratio, accents per beat position | onsets and loudness per sixteenth |
| R2.4 | **Intonation** (voice, fretless, bends): cents per note, % of notes within ±20 ¢, scoops at attacks, drift across a phrase, vibrato rate and depth | YIN pitch track against equal temperament at the session's A4 (or the tuning heard) |
| R2.5 | **Instrument tuning**: string-by-string offset | the tuning estimate and the open-string notes |
| R2.6 | **Harmony execution**: chord changes on time, the gap or noise at changes, chords played clean (tones heard against the chord's) | the notes front end and the chord tracker |
| R2.7 | **Dynamics**: loudness range, consistency of strums or notes, phrase shape | loudness (BS.1770) per note and per bar |
| R2.8 | **Form**: sections, repeats, and whether a repeat matches in tempo and harmony | chord sequence and beats |
| R2.9 | Shown live (DS-X), summarised after REC, and drawn on the timeline where they happen | — |

## R3 — Session JSON

- R3.1: Every REC take writes a versioned JSON, `dissonancia-session/1`. It holds:
  - session options;
  - the event timeline (notes, chords, beats);
  - every R2 metric, per bar and overall, each with its confidence and evidence;
  - the known limits of this take (noise, clipping, drums loud, low confidence).
- R3.2: The schema is documented in the repo and checked by a test. Old versions stay readable.
- R3.3: The JSON is complete without the audio. Feedback never needs the recording.
- R3.4: STUDIO may refine a session (whole-take decoding). The JSON then says which values were
  refined.

## R4 — AI feedback

- R4.1: The AI reads only the session JSON (R3), never the audio.
- R4.2: Every claim cites a metric from the JSON, with its value. It must not invent anything the
  JSON does not hold. Where confidence is low, it says so (P2).
- R4.3: The feedback tells the player:
  - what went well;
  - the one or two things to work on;
  - a concrete exercise for each (for example, "bar 5–8: changes to D are 120 ms late — loop them
    at 70 bpm").
- R4.4: Language: Portuguese by default.
- R4.5: The model is **local**, and it continues a document; it is not a chat (decision
  2026-10-21). A base model was the plan; on 2026-10-28 the user kept Qwen3.6-35B-A3B, an instruct
  model run as a plain continuation, because it passed the R4.6 test and no official base GGUF of a
  recent model was found. Any GGUF the user chooses still goes through that test.
  - The user downloads the model and accepts its license; a model is a separate contract. The app
    points to a GGUF file of the user's choice, and nothing is fetched or bundled by us.
  - Inference runs in llama.cpp, built locally from a pinned release (P4). Like `dz_separate`, it
    runs in its own helper process (`dz_coach`), so a crash never touches the app.
  - Without a model, the app works and the metrics stand alone.
- R4.7: How a base model is held to the facts:
  - **The prompt is a document**: a few worked examples (session JSON → feedback written by us),
    then this session's JSON, then the start of the feedback. The model continues it.
  - **The facts are given, not recalled**: the app turns the JSON into short Portuguese fact lines
    (metric, value, confidence) placed before the feedback, so the model rephrases and orders
    them rather than inventing.
  - **Decoding is constrained** (a llama.cpp GBNF grammar) to the feedback's sections.
  - **The output is checked**: every number and bar it mentions must exist in the JSON. A claim that
    fails the check is dropped; if too much is dropped, the output is generated again.
  - Each model the user tries is scored with the R4.6 test set before it is trusted.
- R4.6: The prompt and the JSON-to-text contract are versioned, and a test keeps them aligned
  (fixed JSON in, required citations out).

## R5 — Evaluation corpus (open data only)

| Corpus | What it gives | License | Use |
|---|---|---|---|
| GuitarSet | 360 solo guitar takes, chords, notes, beats | CC BY 4.0 | chords, bass, picking (in use) |
| RWC Music Database (re-released 2026) | real pop / jazz recordings; chord, beat, structure annotations kept apart | audio CC BY-NC 4.0 (fits: this is a non-commercial study); annotations: check | **harmony over a full band (R1.1, R1.8)** |
| vocadito | 40 solo singing excerpts, f0 and notes | CC BY 4.0 | melody and intonation (R1.5, R2.4) |
| Dagstuhl ChoirSet | close-mic choir singers, f0, beats | check | intonation, choir |
| AAM (Artificial Audio Multitracks) | 3000 generated tracks with chords, beats, keys, stems | check | volume testing only (synthetic) |
| The project's synthetic sets | band mix, picking, latency | ours | regressions |

- R5.1: Each corpus has a fetch script (pinned, checksummed, never redistributed) and a single
  evaluation command that prints every R1 / R2 figure.
- R5.2: Each corpus's licence is confirmed before use and recorded beside its fetch script.

## R6 — Platform and release

- R6.1: CI builds and runs every test on Linux on each push, then on Windows.
- R6.2: Windows 10+ (WASAPI) and Linux are supported; macOS follows.
- R6.3: One installable package per OS. On first start, Setup fetches the components.

## Frozen (maintenance only until R1–R4 meet their targets)

- STUDIO mixing, lunchbox and VST3;
- the choir;
- the playground beyond bug fixes.

STUDIO's analysis passes stay in scope: they refine the session (R3.4).
