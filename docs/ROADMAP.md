# Roadmap

Plan for [`REQUIREMENTS.md`](REQUIREMENTS.md), decided 2026-10-21. LIVE comes first.
- Each phase ends with numbers, measured on the R5 corpus.
- A phase may start before the one above it closes, but nothing ships without its measurement.

## Phase 0 — Ruler (first, short)

The targets are on real music, so the measurement has to exist before the tuning does.

1. `tools/eval/`: one command that prints, per corpus and per mode:
   - harmony and chord on screen (scored from 0.5 s after each change);
   - delay to the screen (median, p90);
   - change ratio;
   - backdated labels.

   The scripts in `tools/loopback` (guitarset.py, latency.py, the sweep) fold into it.
2. Fetch scripts for GuitarSet, RWC (audio and annotations, licence checked), vocadito and
   Dagstuhl ChoirSet: pinned, checksummed, cached outside the repo.
3. CI on Linux: build, `dz_tests`, `tests/score`, and a short eval on a fixed subset that fails on a
   regression.
4. **Baseline**: every R1 figure today, written into IMPLEMENTATION.md.

Exit: one command gives the R1 table, and CI runs on every push.

**Done 2026-10-21.** Baseline in IMPLEMENTATION.md: harmony 54.9 % on RWC-Pop and 73.4 % on GuitarSet,
against the 90 % target; melody 76.4 % on vocadito.

## Phase 1 — Psychoacoustic hearing (R1)

Each step is a separate change, measured against the baseline and kept only if it helps.

1. **Equal loudness at playing level** (ISO 226, ~70–80 phon) instead of A-weighting. A-weighting
   costs a G2 −19 dB; a listener at that level hears the bass well. **Done 2026-10-22**: 75 phon,
   RWC-Pop harmony +3.9 pts.
2. **Bass by virtual pitch.** A low root is heard through its harmonics, so the bass register is
   searched apart and does not compete with the treble for the note slots. **Done 2026-10-22**: the
   bass takes the last slot when the greedy search missed it; with step 1, RWC-Pop harmony
   54.9 → 62.1 % and 0.79 → 0.68 s. Chord exact on GuitarSet fell 46 → 42 % (more extensions).
3. **More notes per hop** (6 for a full strum), with the bass slot reserved. Tried 2026-10-22 without
   the bass slot: worse on GuitarSet chord and take; to try again after step 5.
4. **Temporal masking**: forward masking after an attack, for picking and damped strings.
5. **Harmonic / percussive split** (median filter over the STFT already computed) before the
   notes, for songs with drums (RWC). Tried 2026-10-22 as a causal time median: +0.8 at best, and a
   clean change passes through a third chord. Dropped; drums are not RWC's main error.
6. **Temporal integration of loudness** (~200 ms) feeding the HMM's memory, instead of a fixed
   decay. This is closer to what a listener "sums up" as the harmony.
Each step is judged with `eval.py --compare` (paired per song, 95 % interval), not by the totals.

7. **Retune** lag, memory and dwell on the corpus (the sweep from 2026-10-21) after each step above.
   First pass 2026-10-22: harmony dwell 0.4 s, memory 0.2 s, +4 pts on RWC-Pop and GuitarSet (66.1 %,
   78.5 %), delay 0.56 s. Open: the change ratio (×1.63 on RWC-Pop) and the exact symbol.

8. **A learned decision layer** (2026-10-23): the harmony's emission fitted on the corpus
   (`tools/eval/learn.py`), richer inputs (memory per register and time scale). Full band +6.5 pts
   held out. Transitions learned from the chord sequences: tried 2026-10-24, no gain. A non-linear emission
   and a longer memory: no gain. Perfect notes (RWC MIDI) reach 80-83 % live and 88 % offline with each
   chord's bounds given, so 90 % on a full band is above what the notes say (see open decision 3).
   Next: the same learned emission for the chord played (all qualities).

Exit: R1.1–R1.3 on RWC and GuitarSet. If a classical model stalls well below 90 %, weigh a small
neural model in LIVE, as Melodia already runs Basic Pitch.

**Closed 2026-10-26 at the ceiling.** Harmony on screen: RWC-Pop 75.4 % (with the band split, ATRAC
style), GuitarSet 79.8 %, synthetic 93.9 %; delay 0.49-0.58 s. Perfect notes reach 80-83 % live and 88 % offline with each chord's bounds
given, so the decision layer is near what the notes say; the rest is hearing (~8 points on a full
band). Open for later: the change ratio (R1.3), the exact symbol (R1.4), melody (R1.5).

## Phase 2 — Coach metrics and session JSON (R2, R3)

1. Timing and tempo (R2.1–R2.3) from onsets and beats, live and per take. **Per take done
   2026-10-26** (`core/src/timing.cpp`, `tools/eval/timing.py`): see IMPLEMENTATION.md. Per bar in the
   session JSON (step 4); live waits for DS-X (step 5).
2. Intonation and tuning (R2.4, R2.5) from the YIN track. **R2.4 per take done 2026-10-26**
   (`core/src/intonation.cpp`, `tools/eval/intonation.py`, in the session JSON). R2.5 (string by
   string) is not built.
3. Harmony execution, dynamics and form (R2.6–R2.8). **R2.6 and R2.7 per take done 2026-10-26**
   (`core/src/harmony.cpp`, `core/src/dynamics.cpp`; accents per beat of the bar join R2.3). R2.8
   (form) is not built.
4. `dissonancia-session/1`, written after every REC, with a schema test. It grows from today's
   sidecar. **Done 2026-10-26** ([`SESSION.md`](SESSION.md)): the events, the timing and
   tempo per take and per bar, and the take's limits. The other metrics join it as they are built.
5. DS-X shows the metrics live. After REC, a session report shows them and marks them on the
   timeline.

Exit: each metric validated on a corpus where the truth is known. Timing uses synthetic offsets
and GuitarSet beats; intonation uses vocadito and Dagstuhl.

## Phase 3 — AI feedback (R4)

1. `dz_coach`: a helper process on llama.cpp, built from a pinned release. It loads the GGUF base
   model the user downloaded and chose.
2. The document prompt, as in R4.7:
   - worked examples;
   - the fact lines from the JSON;
   - the session JSON;
   - the opening of the feedback, continued by the model under a GBNF grammar.
3. The checker: every number and bar in the output must exist in the JSON; otherwise the claim is
   dropped, or the output is generated again.
4. A test with fixed JSON files, run on each model the user tries. It scores citations,
   inventions, and what it says about low confidence.
5. A COACH unit in LIVE: after REC (or on request), the feedback with its exercises.

Exit: on ten real sessions of the user's, the user judges the feedback right and useful.

## Phase 4 — Release (R6)

1. A Windows build in CI and a run on a Windows machine: WASAPI latency, DirectML.
2. Packages per OS; Setup fetches the components on first start.
3. Reference machine figures.

## Frozen

STUDIO mixing, lunchbox and VST3; the choir; the playground beyond bug fixes. These come back
when they serve the coach. Likely first:
- the playground as the practice groove;
- STUDIO as the session's exact pass.

## Open decisions

Decided 2026-10-21:
- **AI model:** local, a base model (not instruct or thinking), downloaded by the user under its own
  license.
- **Use:** a non-commercial study and proof of concept, so RWC's CC BY-NC audio fits.

Still open:
1. **RWC annotations' licence** (they are kept apart from the audio).
2. **Portuguese from a base model.** Small base models write weaker Portuguese than English. Measure
   it on the test set; the fact lines carry the content either way.
3. **What "≥ 90 %" covers.** Harmony on a full band (RWC Pop) and on a solo guitar (GuitarSet)
   are different problems. Should the target hold on both, or on the full band first?
   Measured 2026-10-24: on RWC-Pop even perfect notes with each chord's bounds given reach 88 %, so
   a full band's target needs restating against that ceiling.
