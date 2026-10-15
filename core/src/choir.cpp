#include "choir.hpp"

#include <algorithm>
#include <cmath>
#include <complex>
#include <limits>

namespace dz {

namespace {
constexpr double kPi = 3.14159265358979323846;

void fft(std::vector<std::complex<double>>& a) {
    const size_t n = a.size();
    for (size_t i = 1, j = 0; i < n; i++) {
        size_t bit = n >> 1;
        for (; j & bit; bit >>= 1) j ^= bit;
        j ^= bit;
        if (i < j) std::swap(a[i], a[j]);
    }
    for (size_t len = 2; len <= n; len <<= 1) {
        const std::complex<double> w = std::polar(1.0, -2 * kPi / double(len));
        for (size_t i = 0; i < n; i += len) {
            std::complex<double> c = 1;
            for (size_t k = 0; k < len / 2; k++, c *= w) {
                auto u = a[i + k], v = a[i + k + len / 2] * c;
                a[i + k] = u + v;
                a[i + k + len / 2] = u - v;
            }
        }
    }
}

double midi_of(double hz) { return 69 + 12 * std::log2(hz / 440); }
}  // namespace

std::vector<ChoirFrame> choir_frames(const float* x, size_t n, uint32_t rate, int maxVoices, const std::function<bool(double)>& report) {
    // ~85 ms windows (two periods of E2 and a fine bin spacing once zero-padded), 20 ms hop: a
    // choir moves slowly, and the assignment below needs every voice's harmonics resolved.
    const size_t win = size_t(std::pow(2, std::round(std::log2(rate * 0.085)))), nfft = 2 * win, hop = size_t(rate * 0.02);
    const double binHz = double(rate) / double(nfft);
    const size_t kMax = std::min(nfft / 2, size_t(6000 / binHz));
    std::vector<double> hann(win);
    for (size_t i = 0; i < win; i++) hann[i] = 0.5 - 0.5 * std::cos(2 * kPi * double(i) / double(win));

    // Whitening weights over critical bands (Klapuri: γ_b = σ_b^(ν−1), ν = 0.33).
    std::vector<double> centres;
    for (int b = 0;; b++) {
        const double c = 229 * (std::pow(10, (b + 1) / 21.4) - 1);
        if (c > 6500) break;
        centres.push_back(c);
    }

    // f0 grid: 20-cent steps over E2 − 1 tone .. A5 + 2 tones; harmonic windows ±3 %.
    std::vector<double> cand;
    for (double f = 73.4; f <= 1100; f *= std::pow(2, 20.0 / 1200)) cand.push_back(f);
    struct Partial { size_t a, b; double g; };
    std::vector<std::vector<Partial>> partials(cand.size());
    for (size_t c = 0; c < cand.size(); c++)
        for (int m = 1; m <= 20 && m * cand[c] < 5500; m++) {
            const double f = m * cand[c];
            partials[c].push_back({size_t(std::max(1.0, f * 0.97 / binHz)), std::min(kMax - 1, size_t(f * 1.03 / binHz + 1)), (cand[c] + 27) / (f + 320)});
        }

    // Silence: frames 50 dB under the loudest are not analysed.
    double peakRms = 0;
    std::vector<double> rms;
    for (size_t pos = 0; pos + win <= n || (pos == 0 && n > 0); pos += hop) {
        double e = 0;
        for (size_t i = 0; i < win && pos + i < n; i++) e += double(x[pos + i]) * x[pos + i];
        rms.push_back(std::sqrt(e / double(win)));
        peakRms = std::max(peakRms, rms.back());
        if (pos + win > n) break;
    }

    std::vector<ChoirFrame> frames;
    std::vector<std::complex<double>> buf(nfft);
    std::vector<double> Y(nfft / 2), R(nfft / 2), sal(cand.size());
    for (size_t fi = 0; fi < rms.size(); fi++) {
        const size_t pos = fi * hop;
        ChoirFrame fr{(double(pos) + double(win) / 2) / rate, {}};
        if (report && fi % 64 == 0 && !report(0.9 * double(fi) / double(rms.size()))) return {};
        if (rms[fi] < peakRms * 0.00316) { frames.push_back(fr); continue; }
        for (size_t i = 0; i < nfft; i++) buf[i] = i < win && pos + i < n ? x[pos + i] * hann[i] : 0.0;
        fft(buf);
        for (size_t k = 0; k < kMax; k++) Y[k] = std::abs(buf[k]);
        // Whitening: interpolated band weights.
        std::vector<double> gamma(centres.size());
        for (size_t b = 0; b < centres.size(); b++) {
            const double lo = b ? centres[b - 1] : 0, hi = b + 1 < centres.size() ? centres[b + 1] : centres[b] * 1.3;
            double s = 0, w = 0;
            for (size_t k = size_t(lo / binHz); k < std::min(kMax, size_t(hi / binHz) + 1); k++) {
                const double f = double(k) * binHz, h = f < centres[b] ? (f - lo) / (centres[b] - lo) : (hi - f) / (hi - centres[b]);
                if (h > 0) { s += h * Y[k] * Y[k]; w += h; }
            }
            gamma[b] = std::pow(std::sqrt(s / std::max(w, 1e-9)) + 1e-9, 0.33 - 1);
        }
        for (size_t k = 0; k < kMax; k++) {
            const double f = double(k) * binHz;
            size_t b = size_t(std::upper_bound(centres.begin(), centres.end(), f) - centres.begin());
            double g = b == 0 ? gamma[0] : b >= centres.size() ? gamma.back()
                     : gamma[b - 1] + (gamma[b] - gamma[b - 1]) * (f - centres[b - 1]) / (centres[b] - centres[b - 1]);
            R[k] = Y[k] = Y[k] * g;
        }

        // Iterative estimation and cancellation, while a voice is still 30 % as salient as the
        // first (Klapuri's Σ s / j^γ polyphony test stopped at three voices of a four-part choir:
        // the assignment below drops what is spurious).
        double first = 0;
        for (int j = 1; j <= maxVoices; j++) {
            size_t best = 0;
            for (size_t c = 0; c < cand.size(); c++) {
                double s = 0;
                for (auto& p : partials[c]) s += p.g * *std::max_element(R.begin() + std::ptrdiff_t(p.a), R.begin() + std::ptrdiff_t(p.b) + 1);
                sal[c] = s;
                if (s > sal[best]) best = c;
            }
            const double s = sal[best];
            if (s <= 0 || (j > 1 && s < 0.3 * first)) break;
            if (j == 1) first = s;
            // Refine on the first harmonics' peaks (parabolic), weighted by their level.
            const auto& ps = partials[best];
            std::vector<double> amp(ps.size());
            std::vector<size_t> at(ps.size());
            double fw = 0, ww = 0;
            for (size_t m = 0; m < ps.size(); m++) {
                at[m] = size_t(std::max_element(R.begin() + std::ptrdiff_t(ps[m].a), R.begin() + std::ptrdiff_t(ps[m].b) + 1) - R.begin());
                amp[m] = R[at[m]];
                if (m < 4 && at[m] > 0 && at[m] + 1 < kMax) {
                    const double l = R[at[m] - 1], c0 = R[at[m]], r = R[at[m] + 1], d = l - 2 * c0 + r;
                    const double off = d < 0 ? 0.5 * (l - r) / d : 0;
                    fw += amp[m] * (double(at[m]) + off) * binHz / double(m + 1);
                    ww += amp[m];
                }
            }
            const double hz = ww > 0 ? fw / ww : cand[best];
            fr.f0.push_back({float(hz), float(s)});
            // Cancel: each partial loses at most the level its neighbours suggest (spectral
            // smoothness), so a harmonic shared with another voice keeps that voice's part.
            for (size_t m = 0; m < ps.size(); m++) {
                double nb = amp[m];
                if (ps.size() > 1) nb = (amp[m] + amp[m ? m - 1 : 1] + amp[m + 1 < ps.size() ? m + 1 : m - 1]) / 3;
                const double take = std::min(amp[m], nb) * 0.89;
                if (amp[m] <= 0) continue;
                for (size_t k = at[m] > 3 ? at[m] - 3 : 0; k <= std::min(kMax - 1, at[m] + 3); k++) R[k] = std::max(0.0, R[k] - take * R[k] / amp[m]);
            }
        }
        std::sort(fr.f0.begin(), fr.f0.end(), [](auto& a, auto& b) { return a.first > b.first; });
        frames.push_back(fr);
    }
    return frames;
}

std::vector<ChoirNote> choir_notes(const std::vector<ChoirFrame>& input, const ChoirTuning& tune) {
    // The front end's saliences swing from frame to frame (vibrato, partials beating where the
    // voices share them): candidates within half a semitone merge, a voice missed for a frame or
    // two is carried from its neighbours, and each pitch takes its mean salience over ±3 frames
    // (60 ms; absent = 0), so a steady voice outweighs a ghost that comes and goes.
    auto near = [](float a, float b) { return std::fabs(midi_of(a) - midi_of(b)) < 0.5; };
    std::vector<ChoirFrame> merged = input;
    for (auto& fr : merged) {
        std::vector<std::pair<float, float>> out;
        for (auto& p : fr.f0) {
            auto it = std::find_if(out.begin(), out.end(), [&](auto& q) { return near(q.first, p.first); });
            if (it == out.end()) out.push_back(p);
            else if (p.second > it->second) *it = {p.first, it->second + p.second};
            else it->second += p.second;
        }
        fr.f0 = out;
    }
    std::vector<ChoirFrame> frames = merged;
    const size_t N = merged.size();
    for (size_t f = 0; f < N; f++) {
        if (merged[f].f0.empty()) continue;
        for (size_t g = f >= 2 ? f - 2 : 0; g <= std::min(N - 1, f + 2); g++)
            for (auto& p : merged[g].f0)
                if (g != f && frames[f].f0.size() < 6 && std::none_of(frames[f].f0.begin(), frames[f].f0.end(), [&](auto& q) { return near(q.first, p.first); }))
                    frames[f].f0.push_back({p.first, p.second * 0.5f});
        for (auto& p : frames[f].f0) {
            float sum = 0;
            int cnt = 0;
            for (size_t g = f >= 3 ? f - 3 : 0; g <= std::min(N - 1, f + 3); g++, cnt++)
                for (auto& q : merged[g].f0)
                    if (near(q.first, p.first)) { sum += q.second; break; }
            p.second = sum / float(cnt);
        }
        std::sort(frames[f].f0.begin(), frames[f].f0.end(), [](auto& a, auto& b) { return a.first > b.first; });
    }

    // A state gives each section one of the frame's candidates (sorted high to low) or none, in
    // order — S at or above A at or above T at or above B — two neighbours may share one (unison).
    struct State { int8_t c[kSections]; };
    const size_t F = frames.size();
    std::vector<std::vector<State>> states(F);
    for (size_t f = 0; f < F; f++) {
        const int K = int(frames[f].f0.size());
        State s{};
        std::function<void(int, int, int)> rec = [&](int sec, int next, int last) {   // next: lowest index still free
            if (sec == kSections) { states[f].push_back(s); return; }
            s.c[sec] = -1;
            rec(sec + 1, next, -1);
            for (int i = next; i < K; i++) { s.c[sec] = int8_t(i); rec(sec + 1, i + 1, i); }
            if (last >= 0) { s.c[sec] = int8_t(last); rec(sec + 1, next, -1); }   // unison with the section above
        };
        rec(0, 0, -1);
    }
    // Beam: the 64 assignments that explain each frame best (the full set grows past 400 with six
    // candidates, and the Viterbi is quadratic in it).

    auto midi = [&](size_t f, int c) { return midi_of(frames[f].f0[size_t(c)].first); };
    auto emission = [&](size_t f, const State& s) {
        const auto& fr = frames[f];
        if (fr.f0.empty()) {
            double cost = 0;
            for (int k = 0; k < kSections; k++) cost += s.c[k] >= 0 ? 1e9 : 0;
            return cost;
        }
        double top = 0;
        for (auto& p : fr.f0) top = std::max(top, double(p.second));
        double cost = 0;
        bool used[8]{};
        for (int k = 0; k < kSections; k++) {
            if (s.c[k] < 0) { cost += 2.5; continue; }
            const double m = midi(f, s.c[k]), rel = fr.f0[size_t(s.c[k])].second / top;
            const double lo = kChoirRange[k][0], hi = kChoirRange[k][1], c = 0.5 * (lo + hi), sd = (hi - lo) / 4;
            const double out = m < lo ? lo - m : m > hi ? m - hi : 0;
            cost += 0.1 * ((m - c) / sd) * ((m - c) / sd) + 0.5 * out * out;
            const bool shared = k > 0 && s.c[k - 1] == s.c[k];
            cost += shared ? 1.5 : -std::log(rel + 0.02);
            used[s.c[k]] = true;
        }
        for (size_t c = 0; c < fr.f0.size(); c++)
            if (!used[c]) cost += tune.unexplained * fr.f0[c].second / top;   // a strong voice left unexplained
        return cost;
    };
    auto transition = [&](size_t f, const State& a, const State& b) {   // frame f-1 -> f
        double cost = 0;
        for (int k = 0; k < kSections; k++) {
            if ((a.c[k] < 0) != (b.c[k] < 0)) { cost += 1.0; continue; }
            if (a.c[k] < 0) continue;
            const double j = std::fabs(midi(f, b.c[k]) - midi(f - 1, a.c[k]));
            cost += tune.jump * j + (j > 7 ? 0.3 * (j - 7) : 0);
        }
        return cost;
    };

    std::vector<std::vector<double>> em(F);
    for (size_t f = 0; f < F; f++) {
        auto& st = states[f];
        std::vector<std::pair<double, State>> scored;
        for (auto& x : st) scored.push_back({emission(f, x), x});
        const size_t keep = std::min<size_t>(64, scored.size());
        std::partial_sort(scored.begin(), scored.begin() + std::ptrdiff_t(keep), scored.end(), [](auto& a, auto& b) { return a.first < b.first; });
        st.clear();
        for (size_t i = 0; i < keep; i++) { st.push_back(scored[i].second); em[f].push_back(scored[i].first); }
    }
    std::vector<std::vector<double>> acc(F);
    std::vector<std::vector<int>> back(F);
    for (size_t f = 0; f < F; f++) {
        acc[f].assign(states[f].size(), 0);
        back[f].assign(states[f].size(), 0);
        for (size_t i = 0; i < states[f].size(); i++) {
            const double e = em[f][i];
            if (f == 0) { acc[f][i] = e; continue; }
            double best = std::numeric_limits<double>::infinity();
            for (size_t p = 0; p < states[f - 1].size(); p++) {
                const double v = acc[f - 1][p] + transition(f, states[f - 1][p], states[f][i]);
                if (v < best) { best = v; back[f][i] = int(p); }
            }
            acc[f][i] = best + e;
        }
    }
    std::vector<int> path(F, 0);
    if (F == 0) return {};
    path[F - 1] = int(std::min_element(acc[F - 1].begin(), acc[F - 1].end()) - acc[F - 1].begin());
    for (size_t f = F - 1; f > 0; f--) path[f - 1] = back[f][size_t(path[f])];

    // Per section: frames -> notes. A note holds while the pitch stays within 0.6 semitone of it;
    // another semitone for 3 frames (60 ms) starts the next note there; a gap ends it.
    std::vector<ChoirNote> notes;
    const double hop = F > 1 ? frames[1].t - frames[0].t : 0.02;
    for (int k = 0; k < kSections; k++) {
        std::vector<double> m(F, -1), conf(F, 0), hz(F, 0);
        std::vector<char> shared(F, 0);
        for (size_t f = 0; f < F; f++) {
            const State& st = states[f][size_t(path[f])];
            if (st.c[k] < 0) continue;
            double top = 0;
            for (auto& q : frames[f].f0) top = std::max(top, double(q.second));
            const auto& p = frames[f].f0[size_t(st.c[k])];
            m[f] = midi_of(p.first);
            hz[f] = p.first;
            conf[f] = p.second / top;
            shared[f] = (k > 0 && st.c[k - 1] == st.c[k]) || (k + 1 < kSections && st.c[k + 1] == st.c[k]);
        }
        auto emit = [&](size_t a, size_t b, int r) {   // frames [a, b]
            const double start = frames[a].t - hop / 2, end = frames[b].t + hop / 2;
            if (end - start < 0.1) return;
            double h = 0, c = 0;
            int sh = 0, cnt = 0;
            for (size_t f = a; f <= b; f++) if (m[f] >= 0) { h += hz[f]; c += conf[f]; sh += shared[f]; cnt++; }
            const bool amb = sh > 0.3 * cnt;
            notes.push_back({k, start, end, r, float(h / cnt), float(std::clamp(c / cnt * (amb ? 0.5 : 1.0), 0.0, 1.0)), amb});
        };
        bool open = false;
        size_t a = 0, changeAt = 0;
        int r0 = 0, changeTo = 0, changeRun = 0;
        for (size_t f = 0; f < F; f++) {
            if (m[f] < 0) { if (open) emit(a, f - 1, r0); open = false; continue; }
            const int r = int(std::lround(m[f]));
            if (!open) { open = true; a = f; r0 = r; changeRun = 0; continue; }
            if (std::fabs(m[f] - r0) < 0.6) { changeRun = 0; continue; }
            if (changeRun == 0 || r != changeTo) { changeTo = r; changeAt = f; changeRun = 0; }
            if (++changeRun >= 3) { emit(a, changeAt - 1, r0); a = changeAt; r0 = changeTo; changeRun = 0; }
        }
        if (open) emit(a, F - 1, r0);
    }

    // Per section, a note interrupted by a short gap (≤ 80 ms) or by a short excursion (< 250 ms,
    // a ghost or a beating partial) between two notes of the same pitch is one note.
    std::vector<ChoirNote> out;
    for (int k = 0; k < kSections; k++) {
        std::vector<ChoirNote> line;
        for (auto& n : notes) if (n.section == k) line.push_back(n);
        for (bool changed = true; changed;) {
            changed = false;
            for (size_t i = 0; i + 1 < line.size(); i++) {
                auto& a = line[i];
                if (line[i + 1].midi == a.midi && line[i + 1].start - a.end <= 0.08) {
                    const double la = a.end - a.start, lb = line[i + 1].end - line[i + 1].start;
                    a.hz = float((a.hz * la + line[i + 1].hz * lb) / (la + lb));
                    a.confidence = float((a.confidence * la + line[i + 1].confidence * lb) / (la + lb));
                    a.end = line[i + 1].end;
                    a.ambiguous = a.ambiguous && line[i + 1].ambiguous;
                    line.erase(line.begin() + std::ptrdiff_t(i) + 1);
                    changed = true;
                } else if (i + 2 < line.size() && line[i + 2].midi == a.midi && line[i + 1].end - line[i + 1].start < 0.25
                           && line[i + 2].start - a.end <= 0.33) {
                    line.erase(line.begin() + std::ptrdiff_t(i) + 1);   // the excursion; the merge above joins the two
                    line[i + 1].start = a.end;
                    changed = true;
                }
            }
        }
        out.insert(out.end(), line.begin(), line.end());
    }
    notes = out;
    std::sort(notes.begin(), notes.end(), [](auto& a, auto& b) { return a.start != b.start ? a.start < b.start : a.section < b.section; });
    return notes;
}

}  // namespace dz
