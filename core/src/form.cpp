#include "form.hpp"

#include <algorithm>
#include <bit>
#include <cmath>

namespace dz {

// A phrase repeats an earlier one when their beats agree this much (Jaccard of the pitch classes,
// over the beats where either sounds). Swept 0.6-0.8 against the annotated chords' forms: 0.65 best
// on GuitarSet (pairwise F 0.87, 0.75: 0.82) and RWC-Pop (0.65, 0.61).
constexpr float kSame = 0.65f;

namespace {
float similarity(const std::vector<uint16_t>& m, size_t a, size_t b, size_t len) {
    double s = 0;
    int n = 0;
    for (size_t k = 0; k < len && b + k < m.size(); k++) {
        const uint16_t x = m[a + k], y = m[b + k];
        if (!x && !y) continue;
        s += double(std::popcount(unsigned(x & y))) / std::popcount(unsigned(x | y)), n++;
    }
    return n ? float(s / n) : 0.f;
}
}  // namespace

FormReport form_report(const std::vector<uint16_t>& m, int beatsPerBar, const std::vector<double>& grid, const std::vector<double>& heard) {
    FormReport r;
    if (beatsPerBar <= 0) return r;
    const size_t bpb = size_t(beatsPerBar), bars = m.size() / bpb;
    // The phrase length under which the most bars repeat an earlier phrase; ties to the longer. Two
    // bars are left out (a two-bar riff repeats under any song: pairwise F 0.76 -> 0.82 on GuitarSet,
    // 0.51 -> 0.61 on RWC-Pop); 12 and 16 never won.
    size_t best = 0;
    for (size_t p : {8, 4}) {
        if (bars < 2 * p) continue;
        size_t covered = 0;
        for (size_t i = 1; i < bars / p; i++)
            for (size_t j = 0; j < i; j++)
                if (similarity(m, j * p * bpb, i * p * bpb, p * bpb) >= kSame) { covered += p; break; }
        if (covered > best || !r.phraseBars) best = covered, r.phraseBars = int(p);
    }
    if (!r.phraseBars) return r;
    const size_t p = size_t(r.phraseBars), len = p * bpb;
    auto bpm = [&](size_t beat0) {   // the heard tempo over the phrase's span
        if (beat0 + len >= grid.size()) return 0.f;
        std::vector<double> iv;
        for (size_t k = 1; k < heard.size(); k++)
            if (heard[k - 1] >= grid[beat0] && heard[k] <= grid[beat0 + len]) iv.push_back(heard[k] - heard[k - 1]);
        if (iv.size() < 2) return 0.f;
        std::nth_element(iv.begin(), iv.begin() + long(iv.size() / 2), iv.end());
        return float(60 / iv[iv.size() / 2]);
    };
    char next = 'A';
    double match = 0, tempo = 0;
    int tempoN = 0;
    for (size_t i = 0; i < bars / p; i++) {
        FormSection s{int(i * p) + 1, int(p), 0, int(r.sections.size()), 100, bpm(i * len)};
        float top = 0;
        for (size_t j = 0; j < r.sections.size(); j++) {
            if (size_t(r.sections[j].first) != j) continue;   // against each label's first time
            const float sim = similarity(m, j * len, i * len, len);
            if (sim >= kSame && sim > top) top = sim, s.first = int(j), s.label = r.sections[j].label;
        }
        if (!s.label) s.label = next < 'Z' ? next++ : 'Z';
        else {
            s.matchPct = 100 * top;
            r.repeats++, match += s.matchPct;
            const float b0 = r.sections[size_t(s.first)].bpm;
            if (b0 > 0 && s.bpm > 0) tempo += 100 * std::fabs(s.bpm / b0 - 1), tempoN++;
        }
        r.form += s.label;
        r.sections.push_back(s);
    }
    if (r.repeats) r.repeatMatchPct = float(match / r.repeats);
    if (tempoN) r.repeatTempoPct = float(tempo / tempoN);
    return r;
}

}  // namespace dz
