# A band-like mix for chord-tracking robustness: strummed guitar (down/up eighths, up-strums hit
# only the top strings, strings ring into the next chord), a bass line with passing notes, a sung
# melody with passing and neighbour tones across the changes, and drums (kick, snare, hi-hat).
#   python3 tools/loopback/mix.py <dir>   -> mix.wav, mix.json (ground truth: one chord per bar)
import json, sys
import numpy as np
exec(open(__file__.replace("mix.py", "gen.py")).read().split("bpm = 92")[0])   # SR, rng, ks(), write()

bpm = 96; beat = 60 / bpm; bar = 4 * beat
OPEN = [40, 45, 50, 55, 59, 64]
prog = [("C", [-1, 3, 2, 0, 1, 0]), ("Am", [-1, 0, 2, 2, 1, 0]), ("F", [1, 3, 3, 2, 1, 1]), ("G", [3, 2, 0, 0, 0, 3]),
        ("Em", [0, 2, 2, 0, 0, 0]), ("Am", [-1, 0, 2, 2, 1, 0]), ("Dm", [-1, -1, 0, 2, 3, 1]), ("G7", [3, 2, 0, 0, 0, 1])] * 2
n = int(SR * (len(prog) * bar + 2)); g = np.zeros(n)
# Guitar: D . D U . U D U per bar (eighths); up-strums only strings 3-6 (top four), softer.
pattern = [(0, 'D'), (1, 'D'), (1.5, 'U'), (2.5, 'U'), (3, 'D'), (3.5, 'U')]
for b, (_, frets) in enumerate(prog):
    for pos, d in pattern:
        t0 = b * bar + pos * beat + rng.normal(0, 0.008)
        strings = [s for s, f in enumerate(frets) if f >= 0]
        if d == 'U': strings = [s for s in strings if s >= 2][::-1]
        amp = (1.0 if d == 'D' else 0.6) * (1.15 if pos == 0 else 1)
        for k, s_ in enumerate(strings):
            note = ks(OPEN[s_] + frets[s_], 1.2) * amp * 0.35
            i0 = int(SR * (t0 + 0.009 * k)); g[i0:i0 + len(note)] += note[:n - i0]
# Bass: root on 1, fifth on 3, a chromatic or scale passing note on the "and" of 4 into the next root.
roots = {"C": 36, "Am": 33, "F": 29, "G": 31, "Em": 28, "Dm": 26, "G7": 31}
bs = np.zeros(n)
for b, (name, _) in enumerate(prog):
    r = roots[name]; nxt = roots[prog[(b + 1) % len(prog)][0]]
    for pos, m, dur in [(0, r, 1.9), (2, r + 7, 1.4), (3.5, nxt - 1 if nxt - 1 != r else nxt + 2, 0.45)]:
        t = np.arange(int(SR * dur * beat)) / SR
        f = 440 * 2 ** ((m + 12 - 69) / 12)
        x = (np.sin(2 * np.pi * f * t) + 0.5 * np.sin(4 * np.pi * f * t) + 0.25 * np.sin(6 * np.pi * f * t)) * np.exp(-t / 0.8)
        i0 = int(SR * (b * bar + pos * beat)); bs[i0:i0 + len(x)] += x[:n - i0] * 0.45
# Melody (C major): chord tones on the beats, passing / neighbour tones between, notes held over bar lines.
mel = [(72, 1), (74, .5), (76, 1.5), (74, 1), (72, 2), (71, .5), (69, 1.5), (72, 1), (77, 1.5), (76, .5), (74, 1), (72, 1),
       (74, 2), (71, 1), (67, 1), (71, 1.5), (72, .5), (74, 1), (71, 1), (69, 2.5), (71, .5), (72, 1), (69, 1), (74, 1.5),
       (73, .5), (74, 1), (77, 1), (79, 2), (77, 1), (74, 1)] * 2
# Sung voice (source-filter): a glottal pulse train with jitter / shimmer and vibrato through the
# formants of a vowel per syllable, breath noise, and a short consonant (noise burst) starting each
# syllable, so a separator hears a voice, not a synth.
def lfilter(b, a, x):   # direct-form IIR, up to 2 poles (no scipy needed)
    y = np.zeros(len(x)); a1 = a[1] if len(a) > 1 else 0.0; a2 = a[2] if len(a) > 2 else 0.0
    b0 = b[0]; b1 = b[1] if len(b) > 1 else 0.0; x1 = y1 = y2 = 0.0
    for i, xi in enumerate(x):
        yi = b0 * xi + b1 * x1 - a1 * y1 - a2 * y2
        y[i] = yi; x1 = xi; y2 = y1; y1 = yi
    return y
VOWELS = {"a": [(730, 90), (1090, 110), (2440, 170)], "e": [(530, 70), (1840, 120), (2480, 170)], "o": [(570, 80), (840, 100), (2410, 170)], "i": [(300, 60), (2200, 140), (3000, 200)]}
def formant(x, fc, bw):
    r = np.exp(-np.pi * bw / SR); th = 2 * np.pi * fc / SR
    return lfilter([1 - r], [1, -2 * r * np.cos(th), r * r], x)
v = np.zeros(n); t0 = 0.0; ph = 0.0; notes = []
for k, (m, bt) in enumerate(mel):
    dur = bt * beat; t = np.arange(int(SR * dur)) / SR
    f = 440 * 2 ** ((m - 69) / 12) * 2 ** ((20 * np.sin(2 * np.pi * 5.5 * t) * np.clip(t / 0.3, 0, 1) + 6 * rng.standard_normal(len(t)).cumsum() / np.sqrt(len(t) + 1)) / 1200)
    phase = ph + np.cumsum(f) / SR; ph = phase[-1] % 1
    saw = 2 * (phase % 1) - 1                                   # glottal-like pulse train (rich, -6 dB/oct)
    src = lfilter([1], [1, -0.9], np.diff(saw, prepend=saw[0])) * (1 + 0.05 * rng.standard_normal(len(t)))
    src += 0.04 * rng.standard_normal(len(t))                   # breath
    vow = VOWELS["aeoi"[k % 4]]
    y = sum(formant(src, fc, bw) * g for (fc, bw), g in zip(vow, (1.0, 0.6, 0.35)))
    cons = np.zeros(len(t)); nc = int(0.03 * SR)
    if k % 2 == 0: cons[:nc] = rng.standard_normal(nc) * np.linspace(1, 0, nc) * 0.3   # "t" / "s" onset
    env = np.clip((t - 0.02) / 0.05, 0, 1) * np.clip((dur - t) / 0.06, 0, 1)
    x = env * y / (np.abs(y).max() + 1e-9) + lfilter([1, -0.95], [1], cons)
    i0 = int(SR * t0); v[i0:i0 + len(x)] += x[:n - i0] * 0.45; notes.append((m, t0 + 0.02, t0 + dur)); t0 += dur
    if t0 >= len(prog) * bar: break
# Drums: kick on 1 and 3, snare on 2 and 4, closed hi-hat eighths.
d = np.zeros(n)
for b in range(len(prog)):
    for e8 in range(8):
        tt = b * bar + e8 * beat / 2; i0 = int(SR * tt)
        hh = rng.standard_normal(int(0.04 * SR)) * np.exp(-np.arange(int(0.04 * SR)) / SR / 0.01)
        hh = np.diff(hh, prepend=0) * 0.25; d[i0:i0 + len(hh)] += hh[:n - i0]
        if e8 in (0, 4):
            t = np.arange(int(0.3 * SR)) / SR
            k = np.sin(2 * np.pi * (50 + 80 * np.exp(-t / 0.03)) * t) * np.exp(-t / 0.12) * 0.9; d[i0:i0 + len(k)] += k[:n - i0]
        if e8 in (2, 6):
            t = np.arange(int(0.2 * SR)) / SR
            s = (rng.standard_normal(len(t)) * 0.5 + np.sin(2 * np.pi * 190 * t)) * np.exp(-t / 0.06) * 0.6; d[i0:i0 + len(s)] += s[:n - i0]
mix = g + bs + v + d
write(sys.argv[1] + "/mix.wav", mix)
json.dump({"notes": [n for n in notes if n[1] < len(prog) * bar]}, open(sys.argv[1] + "/mix_voice.json", "w"))   # melody ground truth
for name, stem in [("voice", v), ("voice_drums", v + d), ("guitar", g), ("guitar_bass", g + bs), ("guitar_voice", g + v), ("guitar_drums", g + d)]:   # stems: which part hurts
    write(sys.argv[1] + f"/mix_{name}.wav", stem)
json.dump({"bar": bar, "chords": [p[0] for p in prog]}, open(sys.argv[1] + "/mix.json", "w"))
print("mix.wav", round(len(mix) / SR, 1), "s,", len(prog), "bars at", bpm, "bpm")
