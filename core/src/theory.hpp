// Live spelling (spec §15, §22.2): key signature -> letter + alter, chromatic notes by
// melodic direction, octave of the LETTER (Cb4 = MIDI 59). Deterministic, no look-ahead.
#pragma once
#include <cstdint>
#include <cstdio>
#include <initializer_list>

#include "dissonancia.h"

namespace dz {

struct Spelled {
    int8_t letter;   // 0=C .. 6=B
    int8_t alter;    // -2..+2
    int8_t octave;   // sounding octave of the letter (no clef shift)
    bool diatonic;
};

class Speller {
public:
    static constexpr int8_t kLetterSemis[7] = {0, 2, 4, 5, 7, 9, 11};

    void configure(int8_t fifths, KeyMode mode) {
        static constexpr int8_t sharps[7] = {3, 0, 4, 1, 5, 2, 6};   // F C G D A E B
        static constexpr int8_t flats[7] = {6, 2, 5, 1, 4, 0, 3};    // B E A D G C F
        for (auto& a : alter_) a = 0;
        for (int i = 0; i < 7 && i < (fifths < 0 ? -fifths : fifths); i++) alter_[fifths > 0 ? sharps[i] : flats[i]] = fifths > 0 ? 1 : -1;
        for (int pc = 0; pc < 12; pc++) diatonicLetter_[pc] = special_[pc].letter = -1;
        for (int8_t l = 0; l < 7; l++) diatonicLetter_[pc_of(l, alter_[l])] = l;
        prefersSharps_ = fifths >= 0;

        if (mode != KeyMode::Major) {
            // Minor: raised 7th (harmonic) and raised 6th (melodic) are spelled on their own letters,
            // e.g. G# in A minor, F-double-sharp in G# minor — never as lowered notes.
            int8_t tonic = int8_t((diatonicLetter_[(7 * (fifths + 12) + 9) % 12]));
            for (int8_t degree : {int8_t(6), int8_t(5)}) {
                int8_t l = int8_t((tonic + degree) % 7);
                int8_t a = int8_t(alter_[l] + 1);
                special_[pc_of(l, a)] = {l, a, 0, false};
            }
        }
        reset_phrase();
    }

    // Phrase boundary (silence): chromatic spellings may change again.
    void reset_phrase() {
        for (auto& m : memo_) m.letter = -1;
    }

    Spelled spell(int midi, int previousMidi) {
        int pc = ((midi % 12) + 12) % 12;
        Spelled s{};
        if (diatonicLetter_[pc] >= 0) {
            s = {diatonicLetter_[pc], alter_[diatonicLetter_[pc]], 0, true};
        } else if (memo_[pc].letter >= 0) {
            s = memo_[pc];
        } else if (special_[pc].letter >= 0) {
            s = special_[pc];
        } else {
            bool up = previousMidi < 0 || previousMidi == midi ? prefersSharps_ : midi > previousMidi;
            // In a heptatonic scale both chromatic neighbours are diatonic.
            int8_t l = up ? diatonicLetter_[(pc + 11) % 12] : diatonicLetter_[(pc + 1) % 12];
            s = {l, int8_t(alter_[l] + (up ? 1 : -1)), 0, false};
            memo_[pc] = s;
        }
        s.octave = int8_t((midi - s.alter - kLetterSemis[s.letter]) / 12 - 1);
        return s;
    }

    bool diatonic(int midi) const { return diatonicLetter_[((midi % 12) + 12) % 12] >= 0; }

    // ASCII name, e.g. "C#4", "Db4", "Fx5", "Cb4".
    static void name(const Spelled& s, int octaveShift, char (&out)[8]) {
        static const char* acc[5] = {"bb", "b", "", "#", "x"};
        std::snprintf(out, sizeof out, "%c%s%d", "CDEFGAB"[s.letter], acc[s.alter + 2], s.octave + octaveShift);
    }

private:
    static int pc_of(int letter, int alter) { return ((kLetterSemis[letter] + alter) % 12 + 12) % 12; }

    int8_t alter_[7]{};
    int8_t diatonicLetter_[12]{};
    Spelled special_[12]{};
    Spelled memo_[12]{};
    bool prefersSharps_ = true;
};

inline int clef_octave_shift(Clef c) { return c == Clef::Treble8vb ? 1 : 0; }

}  // namespace dz
