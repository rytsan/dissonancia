#include "intonation.hpp"

#include <algorithm>
#include <cmath>

namespace dz {

namespace {
float median(std::vector<float> v) {
    std::nth_element(v.begin(), v.begin() + long(v.size() / 2), v.end());
    return v[v.size() / 2];
}
// Least-squares slope of y over x.
double slope(const std::vector<double>& x, const std::vector<float>& y) {
    double sx = 0, sy = 0, sxx = 0, sxy = 0;
    const double n = double(x.size());
    for (size_t i = 0; i < x.size(); i++) sx += x[i], sy += y[i], sxx += x[i] * x[i], sxy += x[i] * y[i];
    const double den = n * sxx - sx * sx;
    return den > 0 ? (n * sxy - sx * sy) / den : 0;
}
}  // namespace

// The steady part leaves out the attack (80 ms, or a quarter of a short note) and the release. The
// scoop is read over 60 ms from where the glide into the note starts: the decoder starts a note once
// its pitch is near, so the glide is followed back up to 60 ms (vocadito: scoops over 30 cents found
// with P 0.69 R 0.68 against the annotated notes' F0, R 0.55 from the note's start).
constexpr double kAttack = 0.08, kRelease = 0.05, kScoopWindow = 0.06, kGlideBack = 0.06;
constexpr float kInTune = 20, kScoop = 30;
// Vibrato: 3.5-9 Hz in the detrended cents, autocorrelation above 0.4 at its period, 8 cents deep.
constexpr double kVibMinHz = 3.5, kVibMaxHz = 9;
constexpr float kVibCorr = 0.4f, kVibDepth = 8;

IntonationReport intonation_report(const std::vector<AnalyzerEvent>& notes, const std::vector<PitchPoint>& track) {
    IntonationReport r;
    if (track.size() < 2) return r;
    const double hop = (track.back().t - track.front().t) / double(track.size() - 1);
    int total = 0;
    double prevEnd = -1;
    for (const AnalyzerEvent& e : notes) {
        if (e.type != AnalyzerEventType::NoteEnd) continue;
        const MusicalNoteEvent& m = e.data.note;
        total++;
        double glide = m.startTimeSeconds;
        size_t k0 = size_t(std::lower_bound(track.begin(), track.end(), m.startTimeSeconds, [](const PitchPoint& p, double t) { return p.t < t; }) - track.begin());
        for (size_t k = k0; k-- > 0;) {
            const PitchPoint& p = track[k];
            if (p.t < std::max(prevEnd, m.startTimeSeconds - kGlideBack) || p.midi <= 0 || std::fabs(p.midi - float(m.midi)) >= 2) break;
            glide = p.t;
        }
        prevEnd = m.endTimeSeconds;
        const double dur = m.endTimeSeconds - m.startTimeSeconds;
        const double a = m.startTimeSeconds + std::min(kAttack, dur / 4), b = m.endTimeSeconds - std::min(kRelease, dur / 6);
        std::vector<double> t;
        std::vector<float> c, scoop;
        for (const PitchPoint& p : track) {
            if (p.midi <= 0 || p.t < glide || p.t >= m.endTimeSeconds) continue;
            const float cents = 100 * (p.midi - float(m.midi));
            if (p.t < glide + kScoopWindow && std::fabs(cents) < 200) scoop.push_back(cents);
            if (p.t < m.startTimeSeconds) continue;
            if (p.t >= a && p.t < b && std::fabs(cents) < 100) t.push_back(p.t), c.push_back(cents);
        }
        if (c.size() < 5) continue;
        NoteIntonation n{m.startTimeSeconds, m.endTimeSeconds, m.midi, median(c), 0, 0, 0, 0};
        if (!scoop.empty()) {
            float s = 0;
            for (float v : scoop) s += v;
            n.scoopCents = s / float(scoop.size()) - n.cents;
        }
        // Drift: the last third's median against the first's (a linear fit follows the edges: vocadito
        // |error| median 18.7 cents against 12.7).
        const double k = slope(t, c);
        const size_t third = c.size() / 3;
        if (third >= 2)
            n.driftCents = median(std::vector<float>(c.end() - long(third), c.end())) - median(std::vector<float>(c.begin(), c.begin() + long(third)));
        else n.driftCents = float(k * (t.back() - t.front()));
        // Vibrato on the residual of the linear fit.
        if (t.back() - t.front() >= 0.3) {
            std::vector<float> res(c.size());
            float mean = 0;
            for (float v : c) mean += v;
            mean /= float(c.size());
            double tm = 0;
            for (double v : t) tm += v;
            tm /= double(t.size());
            double e0 = 0;
            for (size_t i = 0; i < c.size(); i++) res[i] = c[i] - mean - float(k * (t[i] - tm)), e0 += double(res[i]) * res[i];
            const size_t lo = size_t(std::max(1.0, std::floor(1 / (kVibMaxHz * hop)))), hi = size_t(std::ceil(1 / (kVibMinHz * hop)));
            float best = 0;
            size_t lag = 0;
            for (size_t L = lo; L <= hi && L < res.size() / 2; L++) {
                double s = 0;
                for (size_t i = 0; i + L < res.size(); i++) s += double(res[i]) * res[i + L];
                const float ac = e0 > 0 ? float(s / e0 * double(res.size()) / double(res.size() - L)) : 0;
                if (ac > best) best = ac, lag = L;
            }
            const float depth = float(std::sqrt(2 * e0 / double(res.size())));   // a sine's amplitude from its RMS
            if (best > kVibCorr && depth > kVibDepth && lag) n.vibratoHz = float(1 / (double(lag) * hop)), n.vibratoCents = depth;
        }
        r.notes.push_back(n);
    }
    if (r.notes.empty()) return r;

    std::vector<float> cents;
    for (const NoteIntonation& n : r.notes) cents.push_back(n.cents);
    r.tuningCents = median(cents);
    int in = 0, inOwn = 0;
    float abs = 0;
    for (const NoteIntonation& n : r.notes) {
        in += std::fabs(n.cents) <= kInTune;
        inOwn += std::fabs(n.cents - r.tuningCents) <= kInTune;
        abs += std::fabs(n.cents - r.tuningCents);
        r.scoops += std::fabs(n.scoopCents) > kScoop;
        r.vibratoNotes += n.vibratoHz > 0;
    }
    const float count = float(r.notes.size());
    r.inTunePct = 100 * float(in) / count, r.inTuneOwnPct = 100 * float(inOwn) / count, r.meanAbsCents = abs / count;
    r.confidence = double(r.notes.size()) / total * std::min(1.0, double(r.notes.size()) / 8);

    // Phrases: notes with gaps under 0.4 s; the drift is the fitted change of their cents across it.
    for (size_t i = 0; i < r.notes.size();) {
        size_t j = i + 1;
        while (j < r.notes.size() && r.notes[j].start - r.notes[j - 1].end < 0.4) j++;
        if (j - i >= 4) {
            std::vector<double> t;
            std::vector<float> c;
            for (size_t k = i; k < j; k++) t.push_back((r.notes[k].start + r.notes[k].end) / 2), c.push_back(r.notes[k].cents);
            r.phrases.push_back({r.notes[i].start, r.notes[j - 1].end, int(j - i), float(slope(t, c) * (t.back() - t.front()))});
        }
        i = j;
    }
    return r;
}

}  // namespace dz
