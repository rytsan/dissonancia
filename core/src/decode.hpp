// STUDIO whole-take chord decoding (studio-plan S5, first part): what LIVE cannot do, because it
// needs the future. Pass 1 computes every frame's chroma, settled bass and onsets; pass 2 finds
// the most likely chord SEQUENCE over the whole take (Viterbi over the matcher's acoustic scores,
// with a cost per change, so a chord lasts until the harmony really changes); each segment is then
// labelled on its persistence-weighted mean chroma, its bass from the frames' settled bass, its
// start snapped to the attack that began it. POST layer: allocates.
#pragma once
#include <cstdint>
#include <functional>
#include <vector>

#include "dissonancia.h"

namespace dz {

// metronomeGrid: the file starts on a downbeat of s.bpm / s.meter (a REC take): changes cost less
// on the beats and least on the downbeats. progress(fraction) returns false to cancel (empty result).
std::vector<AnalyzerEvent> decode_chords(const SessionConfig& s, const float* x, size_t n, uint32_t rate, double compensationSeconds,
                                         bool metronomeGrid, const std::function<bool(double)>& progress = {});

// Whole-take note decoding (voice / melody): pass 1 = the LIVE pitch estimate of every hop (YIN,
// clarity); pass 2 = Viterbi over MIDI notes + unvoiced with a cost per change and a robust
// emission (a vibrato swing, a stray octave frame or a transition does not split a note); each note
// is then spelled in the key by melodic direction, with its mean pitch, cents, vibrato, confidence.
std::vector<AnalyzerEvent> decode_notes(const SessionConfig& s, const float* x, size_t n, uint32_t rate, double compensationSeconds,
                                        const std::function<bool(double)>& progress = {});

}  // namespace dz
