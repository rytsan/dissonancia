# Session JSON — `dissonancia-session/1`

Every REC take writes `<take>.json` next to `<take>.wav` (R3). It is complete without the audio: the
coach (phase 3) reads only this file. Writer: `Engine::write_sidecar` (`core/src/engine.cpp`), with
`write_events_json` (`core/src/sidecar.hpp`) and `write_take_metrics_json` (`core/src/session.cpp`).
Checked by `core/tests/test_core.cpp` (a REC take), `core/tests/test_timing.cpp` (timing on
synthetic strums), `core/tests/test_dynamics.cpp` (loudness and attacks on synthetic tones),
`core/tests/test_harmony.cpp` (changes on synthetic strums),
`core/tests/test_intonation.cpp` (intonation on a synthetic sung line) and
`tests/score` (the app loads it, and the older format).

Times are seconds from the take's first downbeat. A number is `null` where there is nothing to
measure (no attacks in a bar, too few offbeats for a swing).

## Members

| Member | Holds |
|---|---|
| `format` | `"dissonancia-session/1"` |
| `session` | mode, quality, `referenceA4`, key (`keySet`, `keyFifths`, `keyMode`), clef, `meter`, `bpm`, `countInBars` |
| `deviceRate`, `channels`, `inputLatencyMs`, `outputLatencyMs`, `compensationLatencyMs`, `latencySource` | the device |
| `startSample`, `firstDownbeatSample`, `frames`, `recorderGaps`, `droppedFrames`, `normalizationGainDb` | the recording |
| `eventLogComplete`, `eventTimeBase`, `events` | the timeline: `note`, `chord` and `cadence` events as LIVE confirmed them |
| `metrics.timing` | R2.1, R2.3 (below) |
| `metrics.tempo` | R2.2 (below) |
| `metrics.intonation` | R2.4 (below); voice and single-line modes only |
| `metrics.dynamics` | R2.7 (below) |
| `metrics.harmony` | R2.6 (below); chord modes only |
| `limits` | the take's known limits (below) |

### `metrics.timing`

- `grid`: `"metronome"` when the click played during the take (the device's `clickDuringTake`);
  otherwise `"beats heard"`, the player's own beats. Against those, the mean offset says little;
  the spread still holds.
- `confidence` (0–1), `onsets`, `matched`: the attacks, and those within 0.1 beat of a sixteenth.
- `meanOffsetMs`: > 0 dragging, < 0 rushing, after the round-trip latency is taken off. `spreadMs`
  is the standard deviation of the offsets.
- `swing`: the long eighth over the short (1 straight, 2 triplet).
- `perSixteenth`: `position` (`1`, `e`, `&`, `a`), `count`, `offsetMs`, `accentDb` (level against
  the mean). Positions with no attacks are left out.
- `accentPerBeatDb`: per beat of the bar, the attacks' level against the mean (`null`: none).
- `bars`: `bar` (1-based), `matched`, `meanOffsetMs`, `spreadMs`.
- `evidence`: how it was measured.

### `metrics.tempo`

`bpm` heard, `metronomeBpm`, `driftBpm` (change over the take, linear fit), `spreadBpm` (beat to
beat), `beats` tracked, `evidence`. The beat tracker is seeded at the metronome's tempo, so a player
far off the click may be read at the click's tempo.

### `metrics.intonation`

Each note the whole-take decoder finds, read from the YIN track; a note needs a steady part (its
attack and release left out) of 5 hops or more.
- `referenceA4`, `confidence` (notes read / notes, scaled down under 8), `notes`.
- `tuningCents`: the median of the notes' cents against equal temperament at `referenceA4`, the
  tuning heard. `inTunePct`: notes within ±20 cents of the reference's grid;
  `inTuneOwnTuningPct` and `meanAbsCents`: against the tuning heard (a singer a quarter tone flat
  throughout is in tune with themselves).
- `scoops`: notes whose glide in (60 ms from where it starts) is more than 30 cents off;
  `vibratoNotes`.
- `phrases`: runs of 4 notes or more with gaps under 0.4 s; `driftCents`, the fitted change of the
  notes' cents across the phrase (going flat < 0).
- `bars`: per bar of the metronome, by note start: `notes`, `inTuneOwnTuningPct`, `meanAbsCents`.
- `notesRead`: `start`, `end`, `midi`, `cents`, `scoopCents` (< 0 from below), `driftCents` (the
  steady part's last third against its first), `vibratoHz` and `vibratoCents` (0: none).
- `evidence`.

### `metrics.dynamics`

- `integratedLufs`, `rangeLu`: EBU R128 (ITU-R BS.1770) over the take.
- `trendLu`: the fitted change of the bars' loudness over the take (> 0 growing), bars within
  20 LU of the take only.
- `attackSpreadDb`: how even the attacks are: their level's standard deviation within each beat of
  the bar and sixteenth, so an accent pattern played on purpose is not counted. `perSixteenth`:
  `position`, `count`, `spreadDb`.
- `bars`: `bar`, `lufs` (`null`: silent).
- `phrases`: sound with gaps under 0.4 s, 1.5 s or longer: `start`, `end`, `lufs`, `changeLu` (last
  third against the first), `peakAt` (where the loudest 400 ms falls, 0–1).
- `evidence`.

### `metrics.harmony`

The chords as the whole-take decoder reads them, against what was played.
- `confidence` (changes on the grid / changes, scaled down under 8), `chords`, `changes` (to other
  tones; a new bass under the same chord is not one), `onGrid`.
- `changeMeanMs`, `changeSpreadMs`: each change's attack against the nearest eighth of the grid
  (the same grid as `timing`), within 0.1 beat; > 0 late.
- `gaps`: changes with the level 150–30 ms before their attack 10 dB or more under the same before
  the chord before's own strums (the hand lifted early).
- `cleanPct`, `cleanAtChangePct`: the notes heard that are the chord's tones, after each chord's
  first 0.25 s and within it. The hearing itself puts ~15 % of notes outside the chord, so these
  compare takes; they are not absolute.
- `changeList`: `time`, `bar`, `beat`, `offbeat`, `offsetMs`, `gapDb`, `cleanAfterPct`.
- `chordList`: `start`, `end`, `symbol`, `cleanPct`.
- `evidence`.

### `limits`

`peakDbfs`, `rmsDbfs`, `clippedSamples`, `droppedFrames`, and `warnings`: sentences for the coach
(clipping, a very quiet input, dropped frames, timing, intonation or harmony with low confidence).

## Versions

- `dissonancia-take/1`: the same without `metrics` and `limits`. It is still read. STUDIO's offline
  result (`"analysis": "studio-offline"`, `core/src/post.cpp`) still writes it.
- `dissonancia-session/1`: from 2026-10-26.
