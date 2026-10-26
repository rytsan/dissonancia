// Coach metric R2.4 for a whole take (a voice or a single line): each note's pitch read from the
// YIN track (decode_notes), against equal temperament at the session's A4 and at the tuning heard.
// POST layer: allocates.
#pragma once
#include <vector>

#include "decode.hpp"

namespace dz {

struct NoteIntonation {
    double start, end;
    int midi;
    float cents;                    // median over the note's steady part, against A4's equal temperament
    float scoopCents;               // the glide into the note, its first 60 ms against the steady part (< 0: from below)
    float driftCents;               // change across the steady part (last third against the first)
    float vibratoHz, vibratoCents;  // rate and depth (half peak to peak); 0: none
};

struct PhraseIntonation {
    double start, end;
    int notes;
    float driftCents;               // change across the phrase of its notes' cents (linear fit)
};

struct IntonationReport {
    std::vector<NoteIntonation> notes;   // those with a steady part long enough to read
    std::vector<PhraseIntonation> phrases;   // 4 notes or more, gaps under 0.4 s
    float tuningCents = 0;               // median of the notes' cents: the tuning heard against A4
    float inTunePct = 0, inTuneOwnPct = 0;   // notes within +-20 cents of A4's grid, of the tuning heard's
    float meanAbsCents = 0;              // against the tuning heard
    int scoops = 0, vibratoNotes = 0;    // scoops: attacks more than 30 cents off the steady pitch
    double confidence = 0;               // notes read / notes, scaled down under 8 notes
};

// notes: decode_notes' events; track: its pitch track.
IntonationReport intonation_report(const std::vector<AnalyzerEvent>& notes, const std::vector<PitchPoint>& track);

}  // namespace dz
