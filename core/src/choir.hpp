// STUDIO S9 — choir by section (spec §20.3, §21, M9; EXPERIMENTAL): multiple-f0 estimation over a
// choir recording (or its separated vocals) and the assignment of the voices to the sections
// soprano / alto / tenor / bass by range priors and voice-leading continuity — a Viterbi over the
// assignments, as the spec asks. Unison and crossing voices are ambiguous: their notes carry a
// lower confidence, never a forced answer. POST layer: allocates.
#pragma once
#include <cstddef>
#include <cstdint>
#include <functional>
#include <vector>

namespace dz {

enum ChoirSection : int { kSoprano = 0, kAlto, kTenor, kBass, kSections };

struct ChoirNote {
    int section;
    double start, end;    // seconds from the start of the input
    int midi;
    float hz;             // mean frequency over the note
    float confidence;     // salience against the frame's strongest voice, lowered where ambiguous
    bool ambiguous;       // mostly shared with a neighbouring section (unison) or crossing
};

// One analysis frame: the f0 candidates found, highest first, with their salience.
struct ChoirFrame {
    double t;                                       // window centre, seconds
    std::vector<std::pair<float, float>> f0;        // (Hz, salience)
};

// Multiple-f0 front end (Klapuri 2006, "A perceptually motivated multiple-F0 estimation method"):
// whitened spectrum, harmonic salience, iterative estimation and cancellation with spectral
// smoothness; voices down to a fifth of the strongest one's salience, at most `maxVoices` per frame.
std::vector<ChoirFrame> choir_frames(const float* x, size_t n, uint32_t rate, int maxVoices = 5,
                                     const std::function<bool(double)>& report = {});

// The assignment's costs (defaults chosen on the synthetic chorales of test_choir.cpp).
struct ChoirTuning {
    double unexplained = 1.5;   // per frame, a candidate no section takes, times its relative salience
    double jump = 0.3;          // per semitone a section moves between frames
    // ponytail: tuned on synthetic voices only; real choirs (breath, consonants, reverb) need their own corpus.
};

// Section assignment and notes.
std::vector<ChoirNote> choir_notes(const std::vector<ChoirFrame>& frames, const ChoirTuning& tuning = {});

// Section ranges of the spec (MIDI, inclusive): S C4–A5, A F3–D5, T C3–A4, B E2–E4.
constexpr int kChoirRange[kSections][2] = {{60, 81}, {53, 74}, {48, 69}, {40, 64}};
constexpr const char* kChoirPart[kSections] = {"S", "A", "T", "B"};

}  // namespace dz
