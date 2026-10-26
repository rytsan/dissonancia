#include "timing.hpp"

#include <algorithm>
#include <cmath>
#include <complex>
#include <string>

#include "beats.hpp"

namespace dz {

namespace {
void fft(std::vector<std::complex<float>>& a) {   // in place, radix 2, size a power of two
    const size_t n = a.size();
    for (size_t i = 1, j = 0; i < n; i++) {
        size_t bit = n >> 1;
        for (; j & bit; bit >>= 1) j ^= bit;
        j ^= bit;
        if (i < j) std::swap(a[i], a[j]);
    }
    for (size_t len = 2; len <= n; len <<= 1) {
        const std::complex<float> w(std::cos(float(-2 * M_PI / double(len))), std::sin(float(-2 * M_PI / double(len))));
        for (size_t i = 0; i < n; i += len) {
            std::complex<float> u(1, 0);
            for (size_t k = 0; k < len / 2; k++, u *= w) {
                const std::complex<float> v = a[i + k + len / 2] * u;
                a[i + k + len / 2] = a[i + k] - v;
                a[i + k] += v;
            }
        }
    }
}
}  // namespace

// Where in the ~23 ms frame the flux peaks after an attack starts: synthetic strums (first string)
// come out within ~1 ms with it.
constexpr double kAttackInFrame = 0.6;

std::vector<Onset> detect_onsets(const float* x, size_t n, uint32_t rate) {
    std::vector<Onset> out;
    // Spectral flux (Boeck et al. 2012): a ~23 ms Hann frame every 5 ms, log-compressed magnitudes,
    // the sum of their rises; strings beating against each other barely move it, a new attack does.
    size_t N = 256;
    while (N * 50 < rate) N <<= 1;
    const size_t hop = std::max<size_t>(1, rate / 200);
    if (n < N + hop) return out;
    const size_t frames = (n - N) / hop + 1, bins = N / 2;
    std::vector<float> win(N), prev(bins, 0.f), flux(frames, 0.f), le(frames, 0.f);
    for (size_t i = 0; i < N; i++) win[i] = float(0.5 - 0.5 * std::cos(2 * M_PI * double(i) / double(N)));
    std::vector<std::complex<float>> buf(N);
    float top = -200;
    for (size_t f = 0; f < frames; f++) {
        double e = 0;
        for (size_t i = 0; i < N; i++) e += double(x[f * hop + i]) * x[f * hop + i];
        le[f] = float(10 * std::log10(e / double(N) + 1e-12));
        top = std::max(top, le[f]);
    }
    // Magnitudes floored 55 dB under the loudest frame (a sine's peak bin is ~A N / 4): the noise
    // floor's random bins would otherwise flux as much as an attack.
    const float floor = float(std::pow(10.0, (top - 55) / 20) * double(N) / 4);
    for (size_t f = 0; f < frames; f++) {
        for (size_t i = 0; i < N; i++) buf[i] = {x[f * hop + i] * win[i], 0.f};
        fft(buf);
        float fl = 0;
        for (size_t k = 1; k < bins; k++) {
            const float m = std::log(std::max(std::abs(buf[k]), floor));
            fl += std::max(0.f, m - prev[k]);
            prev[k] = m;
        }
        flux[f] = f ? fl : 0;
    }
    // Peak picking: the flux's local maximum over +-30 ms, above the mean around it (-100..+35 ms)
    // by a margin, and the frame within 45 dB of the loudest; attacks at least 50 ms apart.
    double mean = 0;
    for (float v : flux) mean += v;
    mean /= double(frames);
    const float delta = float(1.2 * mean);
    const int pre = 6, post = 6, avgPre = 20, avgPost = 7;
    for (size_t f = 1; f < frames; f++) {
        if (le[f] < top - 45) continue;
        bool peak = true;
        for (int k = -pre; k <= post && peak; k++) {
            const long g = long(f) + k;
            if (g < 0 || g >= long(frames) || k == 0) continue;
            peak = k < 0 ? flux[size_t(g)] < flux[f] : flux[size_t(g)] <= flux[f];
        }
        if (!peak) continue;
        double avg = 0;
        int cnt = 0;
        for (long g = long(f) - avgPre; g <= long(f) + avgPost; g++)
            if (g >= 0 && g < long(frames)) avg += flux[size_t(g)], cnt++;
        if (flux[f] < avg / cnt + delta) continue;
        // The flux peaks when the attack is ~K of the way into the frame (synthetic strums, first string).
        const double t = (double(f * hop) + kAttackInFrame * double(N)) / rate;
        float lvl = le[f];
        for (size_t k = f; k < std::min(frames, f + 6); k++) lvl = std::max(lvl, le[k]);
        if (!out.empty() && t - out.back().time < 0.05) continue;
        out.push_back({t, lvl});
    }
    return out;
}

TimingReport timing_report(const std::vector<Onset>& onsets, const std::vector<double>& beats, int beatsPerBar) {
    TimingReport r;
    r.onsets = int(onsets.size());
    if (beats.size() < 2) return r;
    // Tempo: the median beat for the BPM, a linear fit of the beat-to-beat tempo for the drift.
    std::vector<double> iv;
    for (size_t i = 1; i < beats.size(); i++) iv.push_back(beats[i] - beats[i - 1]);
    std::vector<double> sorted = iv;
    std::nth_element(sorted.begin(), sorted.begin() + long(sorted.size() / 2), sorted.end());
    r.bpm = 60 / sorted[sorted.size() / 2];
    double st = 0, sb = 0, stt = 0, stb = 0, sbb = 0;
    const double m = double(iv.size());
    for (size_t i = 0; i < iv.size(); i++) {
        const double t = (beats[i] + beats[i + 1]) / 2, b = 60 / iv[i];
        st += t, sb += b, stt += t * t, stb += t * b, sbb += b * b;
    }
    const double den = m * stt - st * st;
    if (den > 0) r.driftBpm = (m * stb - st * sb) / den * (beats.back() - beats.front());
    r.tempoSpreadBpm = std::sqrt(std::max(0.0, sbb / m - (sb / m) * (sb / m)));

    // Each attack's place in its beat (0..1).
    struct At { double frac, dur; float level; size_t beat; };
    std::vector<At> at;
    for (const Onset& o : onsets) {
        size_t i = size_t(std::upper_bound(beats.begin(), beats.end(), o.time) - beats.begin());
        i = std::clamp<size_t>(i, 1, beats.size() - 1) - 1;
        const double d = beats[i + 1] - beats[i], frac = (o.time - beats[i]) / d;
        if (frac < -0.125 || frac >= 1.875) continue;   // beyond the grid
        const double k = std::floor(frac + 0.125);
        at.push_back({frac - k, d, o.level, i + size_t(k)});
    }
    // Swing: the eighths off the beat, where they fall (straight 0.5, triplet 0.67).
    // Measured from the beats as played, so a player late on every note is not swinging.
    std::vector<double> off, on;
    for (const At& a : at) {
        if (a.frac >= 0.375 && a.frac < 0.8) off.push_back(a.frac);
        else if (a.frac < 0.125) on.push_back(a.frac);
    }
    double eighth = 0.5;
    if (off.size() >= 8) {
        auto median = [](std::vector<double>& v) { std::nth_element(v.begin(), v.begin() + long(v.size() / 2), v.end()); return v[v.size() / 2]; };
        const double p = median(off) - (on.empty() ? 0 : median(on));
        r.swing = p / (1 - p);
        if (r.swing > 1.2) eighth = p;   // swung: the offbeat's grid point is where it is played (from the beat)
    }
    double sum = 0, sum2 = 0, lvl = 0;
    std::vector<double> barSum2;
    for (const At& a : at) {
        const double grid[4] = {0, 0.25, eighth, 0.75};
        int q = 0;
        for (int k = 1; k < 4; k++) if (std::abs(a.frac - grid[k]) < std::abs(a.frac - grid[q])) q = k;
        if (eighth > 0.5 && q == 3) q = 2;   // swung eighths: no "a" of its own
        if (std::abs(a.frac - grid[q]) > 0.1) continue;   // between the grid points (a triplet, a flam)
        const double ms = (a.frac - grid[q]) * a.dur * 1000;
        r.matched++, sum += ms, sum2 += ms * ms, lvl += a.level;
        r.offsetMs[q] += ms, r.accentDb[q] += a.level, r.count[q]++;
        if (beatsPerBar > 0) {
            const size_t b = a.beat / size_t(beatsPerBar);
            if (b >= r.bars.size()) r.bars.resize(b + 1), barSum2.resize(b + 1);
            r.bars[b].matched++, r.bars[b].meanOffsetMs += ms, barSum2[b] += ms * ms;
        }
    }
    for (size_t b = 0; b < r.bars.size(); b++) {
        BarTiming& t = r.bars[b];
        if (!t.matched) continue;
        t.meanOffsetMs /= t.matched;
        t.spreadMs = std::sqrt(std::max(0.0, barSum2[b] / t.matched - t.meanOffsetMs * t.meanOffsetMs));
    }
    if (r.matched) {
        r.meanOffsetMs = sum / r.matched;
        r.spreadMs = std::sqrt(std::max(0.0, sum2 / r.matched - r.meanOffsetMs * r.meanOffsetMs));
        lvl /= r.matched;
        for (int q = 0; q < 4; q++)
            if (r.count[q]) r.offsetMs[q] /= r.count[q], r.accentDb[q] = r.accentDb[q] / r.count[q] - lvl;
    }
    if (r.onsets) r.confidence = double(r.matched) / r.onsets * std::min(1.0, r.onsets / 16.0);
    return r;
}

void write_take_metrics_json(FILE* f, const float* x, size_t n, uint32_t rate, double bpm, int beatsPerBar, bool clickHeard,
                             double comp, uint64_t droppedFrames) {
    auto num = [&](double v, const char* fmt = "%.1f") {   // null where there is nothing to say
        if (std::isfinite(v)) std::fprintf(f, fmt, v);
        else std::fputs("null", f);
    };
    std::vector<Onset> on = detect_onsets(x, n, rate);
    for (Onset& o : on) o.time -= comp;
    // The player's own beats give the tempo (R2.2) either way; the grid for the offsets is the click
    // when it was heard.
    BeatTrack heard = track_beats(x, n, rate, bpm);
    for (double& t : heard.beats) t -= comp;
    std::vector<double> grid;
    if (clickHeard && bpm > 0)
        for (double t = 0; t < double(n) / rate + 60 / bpm; t += 60 / bpm) grid.push_back(t);
    else grid = heard.beats;
    const TimingReport r = timing_report(on, grid, beatsPerBar), tempo = timing_report(on, heard.beats);
    const double nan = std::nan("");

    std::fprintf(f, "  \"metrics\": {\n    \"timing\": {\"grid\": \"%s\", \"confidence\": %.2f, \"onsets\": %d, \"matched\": %d, \"meanOffsetMs\": ",
                 clickHeard ? "metronome" : "beats heard", r.confidence, r.onsets, r.matched);
    num(r.matched ? r.meanOffsetMs : nan);
    std::fputs(", \"spreadMs\": ", f);
    num(r.matched ? r.spreadMs : nan);
    std::fputs(", \"swing\": ", f);
    num(r.swing > 0 ? r.swing : nan, "%.2f");
    std::fputs(",\n      \"perSixteenth\": [", f);
    const char* names[4] = {"1", "e", "&", "a"};
    for (int q = 0, first = 1; q < 4; q++) {
        if (!r.count[q]) continue;
        std::fprintf(f, "%s{\"position\": \"%s\", \"count\": %d, \"offsetMs\": %.1f, \"accentDb\": %.1f}", first ? "" : ", ", names[q],
                     r.count[q], r.offsetMs[q], r.accentDb[q]);
        first = 0;
    }
    std::fputs("],\n      \"bars\": [", f);
    for (size_t b = 0; b < r.bars.size(); b++) {
        std::fprintf(f, "%s{\"bar\": %zu, \"matched\": %d, \"meanOffsetMs\": ", b ? ", " : "", b + 1, r.bars[b].matched);
        num(r.bars[b].matched ? r.bars[b].meanOffsetMs : nan);
        std::fputs(", \"spreadMs\": ", f);
        num(r.bars[b].matched ? r.bars[b].spreadMs : nan);
        std::fputs("}", f);
    }
    std::fprintf(f, "],\n      \"evidence\": \"attacks by spectral flux, %.1f ms of round-trip latency taken off, set to the nearest sixteenth of %s\"},\n",
                 comp * 1000, clickHeard ? "the metronome's beats (heard during the take)" : "the beats heard in the take (the click was off)");
    std::fputs("    \"tempo\": {\"bpm\": ", f);
    num(heard.bpm > 0 ? tempo.bpm : nan, "%.2f");
    std::fprintf(f, ", \"metronomeBpm\": %.2f, \"driftBpm\": ", bpm);
    num(heard.beats.size() > 2 ? tempo.driftBpm : nan, "%.2f");
    std::fputs(", \"spreadBpm\": ", f);
    num(heard.beats.size() > 2 ? tempo.tempoSpreadBpm : nan, "%.2f");
    std::fprintf(f, ", \"beats\": %zu, \"evidence\": \"beats tracked in the take (onset envelope, dynamic programming), seeded at the metronome's tempo\"}\n  },\n",
                 heard.beats.size());

    // Known limits of the take (R3.1): level, clipping, lost frames, and what they make unreliable.
    double peak = 0, energy = 0;
    size_t clipped = 0;
    for (size_t i = 0; i < n; i++) {
        const double v = std::abs(double(x[i]));
        peak = std::max(peak, v), energy += v * v, clipped += v >= 0.999;
    }
    const double peakDb = 20 * std::log10(peak + 1e-12), rmsDb = 10 * std::log10(energy / double(std::max<size_t>(n, 1)) + 1e-12);
    std::vector<std::string> warn;
    if (clipped) warn.push_back("clipping: " + std::to_string(clipped) + " samples at full scale");
    if (rmsDb < -50) warn.push_back("very quiet input: attacks may be missed");
    if (droppedFrames) warn.push_back("frames dropped by the recorder: times after the gap may be off");
    if (r.confidence < 0.5) warn.push_back("timing: few attacks on the grid, low confidence");
    std::fputs("  \"limits\": {\"peakDbfs\": ", f);
    num(peakDb);
    std::fputs(", \"rmsDbfs\": ", f);
    num(rmsDb);
    std::fprintf(f, ", \"clippedSamples\": %zu, \"droppedFrames\": %llu, \"warnings\": [", clipped, (unsigned long long)droppedFrames);
    for (size_t i = 0; i < warn.size(); i++) std::fprintf(f, "%s\"%s\"", i ? ", " : "", warn[i].c_str());
    std::fputs("]}\n", f);
}

}  // namespace dz
