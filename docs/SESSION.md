# Session JSON — `dissonancia-session/1`

Every REC take writes `<take>.json` next to `<take>.wav` (R3). It is complete without the audio: the
coach (phase 3) reads only this file. Writer: `Engine::write_sidecar` (`core/src/engine.cpp`), with
`write_events_json` (`core/src/sidecar.hpp`) and `write_take_metrics_json` (`core/src/timing.cpp`).
Checked by `core/tests/test_core.cpp` (a REC take), `core/tests/test_timing.cpp` (the metrics on
synthetic strums) and `tests/score` (the app loads it, and the older format).

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
- `bars`: `bar` (1-based), `matched`, `meanOffsetMs`, `spreadMs`.
- `evidence`: how it was measured.

### `metrics.tempo`

`bpm` heard, `metronomeBpm`, `driftBpm` (change over the take, linear fit), `spreadBpm` (beat to
beat), `beats` tracked, `evidence`. The beat tracker is seeded at the metronome's tempo, so a player
far off the click may be read at the click's tempo.

### `limits`

`peakDbfs`, `rmsDbfs`, `clippedSamples`, `droppedFrames`, and `warnings`: sentences for the coach
(clipping, a very quiet input, dropped frames, timing with low confidence).

## Versions

- `dissonancia-take/1`: the same without `metrics` and `limits`. It is still read. STUDIO's offline
  result (`"analysis": "studio-offline"`, `core/src/post.cpp`) still writes it.
- `dissonancia-session/1`: from 2026-10-26.
