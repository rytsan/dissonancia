// Perceived notes and chords from them (prototypes: tools/loopback/psynotes.py, hmmchords.py).
//
// NoteFrontEnd: what a listener hears as notes, per hop, on the live (decimated) signal. A Hann
// STFT (~180 ms), weighted by the 75-phon equal-loudness contour (ISO 226); peaks that the louder
// ones around them mask (spreading on the Bark scale) do not count; notes by virtual pitch
// (subharmonic summation of the audible peaks, the best note taken and its partials removed, four
// times; the bass searched apart on every peak and given the last slot when the search missed it),
// kept when strong within their own register (bass / middle / treble, so a soft treble string is
// not judged against the thumb's bass), heard for two hops (an attack's or a damping's noise lasts
// one), and nothing below the audibility floor (30 dB under the recent loudest frame).
//
// ChordHmm: chords as a hidden Markov model over those notes. Each chord explains the notes heard
// lately — a chord tone is likely, any other note not — each note weighted by how recently it
// sounded and, in the melody register, by how long it has sustained (a passing note is short) and
// less overall; the lowest note is a chord tone, preferably the root. Chords change rarely;
// diatonic chords of the key (set or heard) are more expected; simpler qualities are preferred.
// Online Viterbi with a fixed lag: the chord at t is decided at t + lag, so a first impression (the
// root and fifth before the third has sounded) is corrected before it is shown as confirmed.
#pragma once
#include <complex>
#include <cstdint>
#include <vector>

#include "dissonancia.h"

namespace dz {

struct NoteFrame {
    static constexpr int kMax = 4;
    uint8_t count = 0;
    int8_t midi[kMax]{};      // lowest first
    bool audible = false;     // above the audibility floor
};

class NoteFrontEnd {
public:
    // rate: the live analysis rate (Hz). Allocates; call off the audio thread.
    void init(double rate);
    void push(const float* x, uint32_t n);     // live samples, any count
    void analyse(double hopSeconds, NoteFrame& out);   // once per hop, on the newest samples

private:
    int n_ = 0, pos_ = 0;
    double rate_ = 0;
    std::vector<float> ring_, window_, loudw_, bark_;
    std::vector<std::complex<float>> buf_, twiddle_;
    std::vector<int> bitrev_;
    std::vector<float> db_;
    float runMax_ = -200;
    bool last_[128]{};
};

class ChordHmm {
public:
    static constexpr int kQualities = 10;
    static constexpr int kStates = 12 * kQualities;
    static const ChordQuality kQuality[kQualities];

    struct Params {
        double lag = 0.2;          // decision delay (s): the screen waits on the memory more than on this; less costs the take's labels (GuitarSet)
        float stay = 0.97f;        // per hop (20 ms): quick to change; the tracker's dwell steadies what is shown
        float tau = 0.3f;          // memory of a note (s)
        float evidence = 0.2f;     // per-hop weight of the notes against the cost of a change
        float newBass = 0.2f;      // what remains of the old notes when a new bass arrives
        bool triads = false;       // major and minor triads only (the harmony underneath)
        const float* emission = nullptr;   // learned triad weights (kBandEmission) instead of the hand-set ones
    };
    static const float kBandEmission[2 * 121];
    // The chord played: every quality, quick.
    static Params played() { return {}; }
    // The harmony underneath (the "campo harmonico" a listener sums up: G ... D ...): triads only,
    // a short memory, a dwell in the tracker; passing chords, inversions and colours fold into it.
    // band: a full band (GeneralChords), with the emission learned on RWC-Pop.
    static Params harmony(bool band = false) { return {0.3, 0.998f, 0.2f, 0.2f, 0.2f, true, band ? kBandEmission : nullptr}; }

    explicit ChordHmm(double hopSeconds, Params p = played());
    void reset();
    // keyOn: the session's key (tonic pitch class, minor mode); off, the key is the one heard.
    void step(const NoteFrame& notes, bool keyOn, int keyTonic, bool keyMinor);
    // The whole recording at once (STUDIO): the best chord per frame, from the past and the future.
    static std::vector<int> decode(const std::vector<NoteFrame>& frames, double hopSeconds, Params p, bool keyOn, int keyTonic, bool keyMinor);
    int current() const { return best_; }        // best state now (preview), -1 = nothing heard yet
    int decided() const { return decided_; }     // the state decided for lag_ hops ago, -1 = none yet
    int lag_hops() const { return lag_; }
    static int root(int state) { return state / kQualities; }
    static ChordQuality quality(int state) { return kQuality[state % kQualities]; }

private:
    // Updates the memory of the notes heard and writes each state's log evidence for this hop.
    void evidence(const NoteFrame& notes, bool keyOn, int keyTonic, bool keyMinor, float* e);

    double hop_;
    Params p_;
    int lag_;
    float mem_[12]{};
    float age_[128]{};
    float alpha_[kStates]{};
    std::vector<uint8_t> back_;   // lag_ rows of kStates backpointers (ring)
    int filled_ = 0, row_ = 0;
    int best_ = -1, decided_ = -1;
    bool heard_ = false;
    int lastBass_ = -1;
    // The key heard, when the session sets none: a posterior over 24 keys, updated by the chords
    // decided and kept through silences.
    float keyLp_[24]{}, keyBonus_[kStates]{};
    float reg_[9][12]{};   // memory per register and time scale (the learned emission)
    void key_update();
};

}  // namespace dz
