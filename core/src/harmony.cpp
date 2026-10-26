#include "harmony.hpp"

#include <algorithm>
#include <cmath>
#include <cstring>

#include "chords.hpp"

namespace dz {

// A change's first 0.25 s belongs to the change (the old chord rings, fingers land). The gap is the
// level over 150..30 ms before the change's attack against the median of the same before the chord
// before's own attacks (a strummed pattern's rests are not a gap), else its median 50 ms level.
constexpr double kChange = 0.25, kGapFrom = 0.15, kGapTo = 0.03, kFrame = 0.05;
constexpr float kGapDip = -10;

HarmonyReport harmony_report(const std::vector<AnalyzerEvent>& events, const std::vector<HeardFrame>& heard, const float* x, size_t n,
                             uint32_t rate, double comp, const std::vector<double>& attacks, const std::vector<double>& beats, int beatsPerBar) {
    HarmonyReport r;
    auto db = [&](double a, double b) {   // RMS level of x over player time [a, b)
        const size_t i = size_t(std::clamp((a + comp) * rate, 0.0, double(n))), j = size_t(std::clamp((b + comp) * rate, 0.0, double(n)));
        double e = 0;
        for (size_t k = i; k < j; k++) e += double(x[k]) * x[k];
        return j > i ? 10 * std::log10(e / double(j - i) + 1e-12) : -120.0;
    };
    // Notes heard over [a, b) that are in the mask, and all of them.
    auto tones = [&](double a, double b, uint16_t mask, int& in, int& all) {
        in = all = 0;
        auto it = std::lower_bound(heard.begin(), heard.end(), a, [](const HeardFrame& h, double t) { return h.t < t; });
        for (; it != heard.end() && it->t < b; ++it)
            for (int k = 0; k < it->notes.count; k++) in += mask >> (it->notes.midi[k] % 12) & 1, all++;
    };

    std::vector<const ChordEvent*> chords;
    for (const AnalyzerEvent& e : events)
        if (e.type == AnalyzerEventType::ChordEnded && e.data.chord.quality != ChordQuality::Unknown) chords.push_back(&e.data.chord);
    long inAll = 0, all = 0, inChange = 0, allChange = 0;
    double sum = 0, sum2 = 0;
    for (size_t i = 0; i < chords.size(); i++) {
        const ChordEvent& c = *chords[i];
        const uint16_t mask = chord_tones(c.rootPitchClass, c.quality);
        ChordPlayed p{c.startTimeSeconds, c.endTimeSeconds, {}, NAN};
        std::memcpy(p.symbol, c.symbol, sizeof p.symbol);
        p.symbol[sizeof p.symbol - 1] = 0;
        int a, b;
        tones(c.startTimeSeconds + kChange, c.endTimeSeconds, mask, a, b);
        if (b) p.cleanPct = 100.f * float(a) / float(b), inAll += a, all += b;
        r.chords.push_back(p);
        // A change: to other tones (not a new bass under the same chord), from a chord right before.
        if (i == 0 || c.startTimeSeconds - chords[i - 1]->endTimeSeconds > 0.1 || mask == chord_tones(chords[i - 1]->rootPitchClass, chords[i - 1]->quality))
            continue;
        // Its time: the nearest attack within 0.1 s (the decoder's start comes from the chroma's
        // onsets, ~20 ms early against GuitarSet's strums).
        double t = c.startTimeSeconds, best = 0.1;
        for (double on : attacks)
            if (std::fabs(on - c.startTimeSeconds) < best) best = std::fabs(on - c.startTimeSeconds), t = on;
        ChordChange ch{t, NAN, 0, 0, false, 0, NAN};
        tones(c.startTimeSeconds, c.startTimeSeconds + kChange, mask, a, b);
        if (b) ch.cleanAfterPct = 100.f * float(a) / float(b), inChange += a, allChange += b;
        std::vector<double> lv;
        for (double a : attacks)
            if (a > chords[i - 1]->startTimeSeconds + 0.1 && a < t - 0.1) lv.push_back(db(a - kGapFrom, a - kGapTo));
        if (lv.empty())
            for (double a = chords[i - 1]->startTimeSeconds; a + kFrame <= t - 0.2; a += kFrame) lv.push_back(db(a, a + kFrame));
        if (!lv.empty()) {
            std::nth_element(lv.begin(), lv.begin() + long(lv.size() / 2), lv.end());
            ch.gapDb = float(db(t - kGapFrom, t - kGapTo) - lv[lv.size() / 2]);
            r.gaps += ch.gapDb <= kGapDip;
        }
        // The nearest eighth of the grid, within 0.1 beat.
        if (beats.size() >= 2) {
            size_t k = size_t(std::upper_bound(beats.begin(), beats.end(), t) - beats.begin());
            k = std::clamp<size_t>(k, 1, beats.size() - 1) - 1;
            const double d = beats[k + 1] - beats[k], frac = (t - beats[k]) / d, e8 = std::round(frac * 2) / 2;
            if (frac > -0.1 && frac < 1.1 && std::fabs(frac - e8) <= 0.1) {
                ch.offsetMs = float((frac - e8) * d * 1000);
                const size_t beat = k + size_t(std::floor(e8));
                ch.offbeat = std::fmod(e8, 1.0) != 0;
                ch.beatInBar = beatsPerBar > 0 ? int(beat % size_t(beatsPerBar)) + 1 : 0;
                ch.bar = beatsPerBar > 0 ? int(beat / size_t(beatsPerBar)) + 1 : 0;
                r.onGrid++, sum += ch.offsetMs, sum2 += double(ch.offsetMs) * ch.offsetMs;
            }
        }
        r.changes.push_back(ch);
    }
    if (all) r.cleanPct = 100.f * float(inAll) / float(all);
    if (allChange) r.cleanAtChangePct = 100.f * float(inChange) / float(allChange);
    if (r.onGrid) {
        r.changeMeanMs = float(sum / r.onGrid);
        r.changeSpreadMs = float(std::sqrt(std::max(0.0, sum2 / r.onGrid - (sum / r.onGrid) * (sum / r.onGrid))));
    }
    if (!r.changes.empty()) r.confidence = double(r.onGrid) / double(r.changes.size()) * std::min(1.0, double(r.changes.size()) / 8);
    return r;
}

}  // namespace dz
