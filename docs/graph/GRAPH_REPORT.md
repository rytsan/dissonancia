# Graph Report - dissonancia  (2026-09-30)

## Corpus Check
- 36 files · ~51,525 words
- Verdict: corpus is large enough that graph structure adds value.
- Unclassified: 2 file(s) not represented in the graph (top: (none) 2)

## Summary
- 1180 nodes · 1837 edges · 67 communities (55 shown, 12 thin omitted)
- Extraction: 94% EXTRACTED · 6% INFERRED · 0% AMBIGUOUS · INFERRED: 105 edges (avg confidence: 0.86)
- Token cost: 97,244 input · 0 output

## Community Hubs (Navigation)
- Engine core state
- C# P/Invoke layer
- Build targets and spec
- LiveSnapshot ABI
- Chord matcher and context
- Bass tracker
- Voice pipeline
- Chord tests
- Session config ABI
- CQT filter bank
- C ABI entry points
- Chord candidate
- GUI rack modules
- Plasma drawing primitives
- Chroma front end state
- Engine implementation
- Fake live source
- Instrument module
- Chord event
- Chord tracker
- Lock-free primitives
- CQT tests
- ABI layout export
- Voice implementation
- Onset detector
- Main window
- Voice tests
- Note event
- Note estimate
- Chroma front end impl
- UI palette
- Bass estimate
- Engine tests
- Live source seam
- Session and keys (GUI)
- Half-band decimator
- Clef theory (GUI)
- Real-time helpers
- Core headers
- Front end output
- YIN estimator
- Voice test harness
- Plasma font
- App entry point
- Analyzer event
- Pitch estimate
- Biquad high-pass
- Tracked note
- Allocation trap
- Audio device config
- Chroma vector
- Chord tracker output
- FIR decimator
- App modes (GUI)
- Chord recognition result
- Reference CQT
- App project
- Onset event
- Tuning estimate
- Mode x quality table
- Tracked chord
- Time signature
- CQT kernel
- Audio callback

## God Nodes (most connected - your core abstractions)
1. `Engine` - 131 edges
2. `LiveSnapshot` - 55 edges
3. `VoicePipeline` - 51 edges
4. `Cqt` - 46 edges
5. `ChromaFrontEnd` - 38 edges
6. `SessionConfig` - 34 edges
7. `MainWindow` - 33 edges
8. `ChordCandidate` - 33 edges
9. `ChordTracker` - 31 edges
10. `AnalyzerHandle` - 25 edges

## Surprising Connections (you probably didn't know these)
- `ChordMatcher` --implements--> `Tonal context / cadence prior`  [INFERRED]
  core/src/chords.hpp → docs/spec.md
- `ChordTracker` --implements--> `Arpeggio accumulator (decaying max-hold)`  [INFERRED]
  core/src/chords.hpp → docs/spec.md
- `ChromaFrontEnd` --implements--> `Global tuning estimation (11 kernel sets)`  [INFERRED]
  core/src/cqt.hpp → docs/spec.md
- `TripleBuffer` --implements--> `Triple-buffer LiveSnapshot (lossy state)`  [INFERRED]
  core/src/lockfree.hpp → docs/spec.md
- `VoicePipeline` --implements--> `Melodic note tracker (median-of-3, hysteresis)`  [INFERRED]
  core/src/voice.hpp → docs/spec.md

## Import Cycles
- None detected.

## Hyperedges (group relationships)
- **LIVE chord pipeline (CQT -> chroma/onsets/bass -> matcher -> tracker)** — core_src_halfband_halfbanddecimator, core_src_cqt_cqt, core_src_cqt_chromafrontend, core_src_bass_onsetdetector, core_src_bass_basstracker, core_src_chords_chordmatcher, core_src_chords_chordtracker [EXTRACTED 1.00]
- **Real-time safety guarantees** — docs_spec_zero_allocation_rule, docs_spec_rt_verification, docs_spec_rt_no_managed_code, docs_spec_notify_rule, core_src_rt_rtscope [INFERRED 0.85]
- **LIVE event contract feeding the score** — docs_spec_spsc_event_queue, docs_spec_completeness_rule, docs_spec_event_backdating, docs_spec_metronome_clock_rule, docs_spec_rec_take_sidecar, docs_spec_live_to_score_fast_path [INFERRED 0.85]

## Communities (67 total, 12 thin omitted)

### Community 0 - "Engine core state"
Cohesion: 0.02
Nodes (83): Engine, analysisRing_, analysisThread_, anFrames_, beatsPerBar_, captureChannels_, captureLatencyMs_, cbAccent_ (+75 more)

### Community 1 - "C# P/Invoke layer"
Cohesion: 0.07
Nodes (26): AbiLayout, Ana, AnalyzerEventNative, AnalyzerEventType, ChordConfirmed, ChordEnded, NoteEnd, NoteStart (+18 more)

### Community 2 - "Build targets and spec"
Cohesion: 0.07
Nodes (14): core CMakeLists, dissonancia shared library (C ABI), dz_abi_check (-Wpadded -Werror), dz_core static library, dz_tests (Catch2), miniaudio static library, Implementation Status, Spec V2.3 (Real-Time Offline Music Analyzer) (+6 more)

### Community 3 - "LiveSnapshot ABI"
Cohesion: 0.04
Nodes (46): LiveSnapshot, analyzedFrames, bass, beatInBar, chord, chordConfirmed, chordConfirmElapsedMs, chordLatencyMs (+38 more)

### Community 4 - "Chord matcher and context"
Cohesion: 0.07
Nodes (37): ChordHistory, beforeQuality, beforeRoot, currentQuality, currentRoot, ChordMatcher, ChordMatcher::ChordMatcher(), context (+29 more)

### Community 5 - "Bass tracker"
Cohesion: 0.07
Nodes (26): BassTracker, bassMax_, bassMin_, firstBin_, lastBin_, lastMidi_, octave_, octaveShift_ (+18 more)

### Community 6 - "Voice pipeline"
Cohesion: 0.06
Nodes (33): VoicePipeline, candHops_, candMidi_, candStart_, cfg_, cur_, dec_, decOut_ (+25 more)

### Community 7 - "Chord tests"
Cohesion: 0.07
Nodes (26): "arpeggio: sequential notes accumulate into the chord", "bass: never settled before T_low after the onset; preview first", "bass: settled bass resolves identical sets and gives inversions spelled as chord tones", "chord ambiguities: C6 = Am7, symmetric, missing third", "chord matcher: 15 qualities x 4 roots, identical sets flagged, never forced", "chord symbols follow the key: Bb not A#, F#m in D, F#dim stays sharp", "chord tracker: passing tone ignored, backdated onsets, no overlap, silence closes", "context: key mode + cadence decide what the audio leaves open, never override it" (+18 more)

### Community 8 - "Session config ABI"
Cohesion: 0.06
Nodes (29): GuitarTuning, capoFret, openMidi, stringCount, SessionConfig, bpm, clef, countInBars (+21 more)

### Community 9 - "CQT filter bank"
Cohesion: 0.07
Nodes (18): Cqt, bpo_, buf_, bufLen_, dec_, delay_, fMin_, im_ (+10 more)

### Community 10 - "C ABI entry points"
Cohesion: 0.13
Nodes (20): ana_capture_device_count(), ana_capture_device_name(), ana_clear_clip(), ana_create(), ana_destroy(), ana_last_error(), ana_read_snapshot(), ana_rec_start() (+12 more)

### Community 11 - "Chord candidate"
Cohesion: 0.07
Nodes (27): ChordCandidate, arpeggiated, bassPitchClass, bassScore, chromaScore, confidence, detected, detectedCount (+19 more)

### Community 12 - "GUI rack modules"
Cohesion: 0.11
Nodes (9): InputModule, RackModule, Frame, Session, Title, ScopeModule, StatusModule, TimelineModule (+1 more)

### Community 14 - "Chroma front end state"
Cohesion: 0.08
Nodes (22): BassTracker, ChromaFrontEnd, autoTune_, bass_, cqt_, decimate_, devCount_, deviations_ (+14 more)

### Community 15 - "Engine implementation"
Cohesion: 0.17
Nodes (18): analysis_loop, beat_frames, Engine::Engine(), flush_pipeline, mix_click, next_downbeat_after, on_audio, process_hop (+10 more)

### Community 16 - "Fake live source"
Cohesion: 0.13
Nodes (11): Chord, FakeLiveSource, BarSeconds, BeatSeconds, LiveFrame, Pitch, Midi, Name (+3 more)

### Community 18 - "Chord event"
Cohesion: 0.10
Nodes (19): ChordEvent, arpeggiated, bassPitchClass, bassSettled, confidence, detectedCount, detectedNotes, durationSeconds (+11 more)

### Community 19 - "Chord tracker"
Cohesion: 0.11
Nodes (19): ChordTracker, acc_, active_, bassSettledNow_, cand_, candidateOn_, candLatencyMs_, candStart_ (+11 more)

### Community 20 - "Lock-free primitives"
Cohesion: 0.13
Nodes (11): SpscQueue, buf_, head_, tail_, TripleBuffer, back_, front_, kDirty (+3 more)

### Community 21 - "CQT tests"
Cohesion: 0.13
Nodes (12): Case, midis, pcs, chord_session(), "chroma: triads and a seventh chord, zero allocation", "CQT: layout, T_low, tones on their bin, agrees with the full-rate reference", "global tuning: estimated from many peaks, kernels follow, chroma stays right", "half-band IIR: passband flat, stopband rejected, computed group delay" (+4 more)

### Community 22 - "ABI layout export"
Cohesion: 0.11
Nodes (18): AbiLayout, analyzerEventSize, audioDeviceConfigSize, chordEventSize, chordResultSize, eventDataOffset, liveSnapshotSize, noteEventSize (+10 more)

### Community 23 - "Voice implementation"
Cohesion: 0.14
Nodes (16): highpass, init, process, VoiceOutput, eventCount, events, note, noteLatencySeconds (+8 more)

### Community 24 - "Onset detector"
Cohesion: 0.13
Nodes (11): OnsetDetector, bins_, firstBin_, init, mean_, prev_, process, refractoryHops_ (+3 more)

### Community 26 - "Voice tests"
Cohesion: 0.14
Nodes (13): "instrument in Treble 8vb: sounding E2 is written E3", midi_hz(), render(), Segment, midi, seconds, vibratoCents, "spelling: key signature, direction, letter octave, clef" (+5 more)

### Community 27 - "Note event"
Cohesion: 0.13
Nodes (15): MusicalNoteEvent, avgCents, avgHz, chromatic, confidence, durationSeconds, endTimeSeconds, medianHz (+7 more)

### Community 28 - "Note estimate"
Cohesion: 0.13
Nodes (15): NoteEstimate, alter, cents, chromatic, confidence, detectedHz, diatonic, expectedHz (+7 more)

### Community 29 - "Chroma front end impl"
Cohesion: 0.17
Nodes (11): append(), ChromaFrontEnd::ChromaFrontEnd(), process, update_tuning, compute, ensure_history, init, lowest_window_seconds (+3 more)

### Community 30 - "UI palette"
Cohesion: 0.16
Nodes (5): Align, Center, Left, Right, Ui

### Community 31 - "Bass estimate"
Cohesion: 0.14
Nodes (14): BassEstimate, alter, confidence, frequencyHz, fromPreview, letter, midi, pitchClass (+6 more)

### Community 32 - "Engine tests"
Cohesion: 0.14
Nodes (9): "chord mode: CQT and chroma reach the snapshot", "event queue: overflow drops, counts, and leaves a sequence gap", "meters, scope, notify rule and metronome click on the input clock", "mode x quality table", "REC: count-in, bar-aligned start, WAV + sidecar, metronome locked", session(), sine(), "triple buffer returns the newest published slot" (+1 more)

### Community 34 - "Session and keys (GUI)"
Cohesion: 0.17
Nodes (9): Session, IsChordMode, KeyOption, Label, Tonic, Quality, Balanced, HighPrecision (+1 more)

### Community 35 - "Half-band decimator"
Cohesion: 0.19
Nodes (7): HalfbandDecimator, a_, havePending_, pending_, x_, y_, measured_gain_db()

### Community 36 - "Clef theory (GUI)"
Cohesion: 0.21
Nodes (7): Clef, Alto, Bass, Tenor, Treble, Treble8vb, Theory

### Community 39 - "Front end output"
Cohesion: 0.17
Nodes (12): Output, bass, bins, binsPerOctave, chroma, flux, gatedBins, lastOnset (+4 more)

### Community 40 - "YIN estimator"
Cohesion: 0.18
Nodes (7): Yin, d_, rate_, tauMax_, tauMin_, threshold_, YIN pitch estimator

### Community 41 - "Voice test harness"
Cohesion: 0.24
Nodes (6): ended(), names(), Run, events, notes, pitch

### Community 44 - "Analyzer event"
Cohesion: 0.25
Nodes (8): AnalyzerEvent, data, _pad0, sequence, type, ana_drain_events(), publish_event, push_event

### Community 45 - "Pitch estimate"
Cohesion: 0.22
Nodes (9): PitchEstimate, clarity, confidence, frequencyHz, midiFloat, _pad0, rms, timestampSeconds (+1 more)

### Community 46 - "Biquad high-pass"
Cohesion: 0.22
Nodes (8): Biquad, a1, a2, b0, b1, b2, z1, z2

### Community 47 - "Tracked note"
Cohesion: 0.22
Nodes (9): Note, count, midi, spelled, startFrame, sumCents, sumCentsSq, sumConf (+1 more)

### Community 48 - "Allocation trap"
Cohesion: 0.31
Nodes (3): checked_alloc(), operator delete(), operator new()

### Community 49 - "Audio device config"
Cohesion: 0.25
Nodes (8): AudioDeviceConfig, captureDevice, clickOutput, exclusive, _pad0, periodFrames, sampleRate, headless()

### Community 50 - "Chroma vector"
Cohesion: 0.25
Nodes (8): ChromaVector, bass, confidence, normalized, raw, smoothed, timestampSeconds, tuningOffsetCents

### Community 51 - "Chord tracker output"
Cohesion: 0.25
Nodes (8): Output, confirmed, confirmedSymbol, confirmElapsedMs, eventCount, events, latencyMs, preview

### Community 53 - "FIR decimator"
Cohesion: 0.25
Nodes (5): Decimator, buf_, d_, h_, phase_

### Community 55 - "App modes (GUI)"
Cohesion: 0.29
Nodes (6): AppMode, GeneralChords, GuitarChords, InstrumentMono, PianoChords, VoiceMono

### Community 56 - "Chord recognition result"
Cohesion: 0.29
Nodes (7): ChordRecognitionResult, alternativeCount, alternatives, ambiguous, best, explanation, _pad0

### Community 57 - "Reference CQT"
Cohesion: 0.29
Nodes (4): ReferenceCqt, buf, im, re

### Community 58 - "App project"
Cohesion: 0.33
Nodes (5): net10.0, Avalonia (12.1.3), Avalonia.Desktop (12.1.3), Avalonia.Themes.Fluent (12.1.3), Microsoft.NET.Sdk

### Community 59 - "Onset event"
Cohesion: 0.33
Nodes (5): OnsetEvent, _pad0, strength, timestampSeconds, type

### Community 60 - "Tuning estimate"
Cohesion: 0.33
Nodes (6): TuningEstimate, confidence, offsetCents, _pad0, referenceA4, valid

### Community 61 - "Mode x quality table"
Cohesion: 0.53
Nodes (3): divisor_for(), is_chord_mode(), live_config()

### Community 62 - "Tracked chord"
Cohesion: 0.40
Nodes (5): Chord, c, frames, start, sumConfidence

### Community 63 - "Time signature"
Cohesion: 0.50
Nodes (4): TimeSignature, denominator, numerator, set_metronome

### Community 65 - "CQT kernel"
Cohesion: 0.67
Nodes (3): Kernel, length, offset

## Knowledge Gaps
- **588 isolated node(s):** `net10.0`, `Avalonia (12.1.3)`, `Avalonia.Desktop (12.1.3)`, `Avalonia.Themes.Fluent (12.1.3)`, `Microsoft.NET.Sdk` (+583 more)
  These have ≤1 connection - possible missing edges or undocumented components. (Counts symbols only; 711 node(s) total have ≤1 connection when file, concept and rationale nodes are included.)
- **12 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `Engine` connect `Engine core state` to `Engine tests`, `Build targets and spec`, `LiveSnapshot ABI`, `Voice pipeline`, `Session config ABI`, `C ABI entry points`, `Analyzer event`, `Chroma front end state`, `Engine implementation`, `Chord tracker`, `Engine includes`, `Lock-free primitives`, `LIVE event contract`, `Voice implementation`, `Time signature`?**
  _High betweenness centrality (0.400) - this node is a cross-community bridge._
- **Why does `emit` connect `Fake live source` to `Chord tracker`, `Chord matcher and context`?**
  _High betweenness centrality (0.218) - this node is a cross-community bridge._
- **Why does `ChordTracker` connect `Chord tracker` to `Engine core state`, `Chord matcher and context`, `Core headers`, `Chord candidate`, `Chroma front end state`, `Fake live source`, `Chord tracker output`, `LIVE event contract`, `Tracked chord`?**
  _High betweenness centrality (0.217) - this node is a cross-community bridge._
- **Are the 7 inferred relationships involving `Engine` (e.g. with `ChromaFrontEnd` and `SpscQueue`) actually correct?**
  _`Engine` has 7 INFERRED edges - model-reasoned connections that need verification._
- **Are the 3 inferred relationships involving `VoicePipeline` (e.g. with `Engine` and `Melodic note tracker (median-of-3, hysteresis)`) actually correct?**
  _`VoicePipeline` has 3 INFERRED edges - model-reasoned connections that need verification._
- **Are the 2 inferred relationships involving `Cqt` (e.g. with `ChromaFrontEnd` and `Multi-resolution octave-decimated CQT`) actually correct?**
  _`Cqt` has 2 INFERRED edges - model-reasoned connections that need verification._
- **Are the 9 inferred relationships involving `ChromaFrontEnd` (e.g. with `BassTracker` and `OnsetDetector`) actually correct?**
  _`ChromaFrontEnd` has 9 INFERRED edges - model-reasoned connections that need verification._