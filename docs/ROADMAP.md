# Roadmap

Written 2026-10-21, after the requirements audit in [`IMPLEMENTATION.md`](IMPLEMENTATION.md).
Nothing here is decided yet: each track lists its options, and the order is to be chosen
together.

## Where the project stands

Dissonância does much more than its spec asked. LIVE analysis, STUDIO (an analysis DAW), SCORE
and a playground for making songs are all built, and every part has tests and measurements on
synthetic material. Three things are thin:

- **Proof outside the dev machine.** It has run on one Linux machine under WSL2 only. There is
  no CI, no Windows build, no installer and no reference machine.
- **Proof on real music.** The real-music numbers are GuitarSet (69 % LIVE, 71 % STUDIO) and the
  user's ear (about 80 %). There is no corpus of real recordings with their chords written down.
- **Focus.** Every week added a surface (LIVE modes, native ML, the lunchbox, the playground).
  Each new surface widens what a release has to carry.

## Track A — Foundation (recommended first)

1. **CI on Linux:** build the core, run `dz_tests` and `tests/score` on every push.
   - This alone would have caught the 2026-10-21 break.
   - Cost: a small GitHub Actions workflow. Pinned downloads already make builds reproducible.
2. **Windows build and run.** The spec names Windows as the primary platform.
   - Build the core with MSVC and run the app.
   - Check WASAPI shared and exclusive latency, DirectML and the Win32 VST3 editor.
   - Then add a Windows CI job.
3. **Packaging:** a self-contained `dotnet publish` with the core library and its helpers
   (`dz_separate`, `dz_plugin`, Verovio). Setup already fetches the rest on first start, and
   nothing is hosted: users build from source or a release is built by CI from tags. The hosting
   rule needs a decision for release binaries.
4. **Reference machine:** pick one and take the CPU and latency figures again on it.

## Track B — Accuracy on real music

1. **A small real corpus.** Twenty songs the user knows, each with its chords written down as a
   `.lab` file (start, end, chord). Score LIVE harmony, chord played and STUDIO against it with
   `chordscore.py`. This turns "about 80 %" into a number that a change can move.
2. **Drums in a mix** (the weak spot):
   - In LIVE, a harmonic / percussive split (a median filter over the STFT the notes front end
     already computes) before the notes are picked.
   - In STUDIO, the Demucs stems already remove the drums.
3. **Funk** (54 %): short, muted chords. They need checking against the corpus before any tuning.
4. **Voice through a microphone:** legato repeated notes need an attack detector.

## Track C — Making music

- Playground:
  - a note editor for sampler notes;
  - time-stretched loops when the tempo changes;
  - pedals checked on a real input with monitoring latency.
- STUDIO S11:
  - phase 4, VST3 played live with plugin delay compensation;
  - phase 5, oversampled dynamics and a true-peak limiter.
- Editor: split, copy / paste, gain envelope.

## Track D — New ideas worth weighing

- **Play along:** STUDIO analyses a song, then LIVE follows it. LIVE shows the next chord ahead
  of time and marks where the user's chord differs. This joins the two halves of the app and uses
  only parts that already exist (lead sheet, LIVE harmony, metronome grid).
- **Guitar tablature** from the likely shapes and the STUDIO notes.
- **Solfège display** (scale degrees, movable do) over the existing theory layer.
- **One UI language.** Labels mix Portuguese and English today. Choose one, or add PT-BR / EN as
  the spec asked.

## Questions to decide together

1. Release first (A), or accuracy first (B)? A and B can run side by side. C should wait for
   one of them.
2. Should the release be binaries built by CI, or source only? "We host nothing" covers models
   and data; it does not yet say what happens to the app itself.
3. Which twenty songs make the real corpus, and who writes their chord files?
4. Freeze new surfaces (playground, lunchbox) until A is done?
