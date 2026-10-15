# A four-part choir for the STUDIO choir-by-section measure (S9): soprano, alto, tenor and bass
# singers (source-filter: glottal pulse train with jitter and vibrato through the formants of a
# vowel per chord, breath noise, a consonant burst on some onsets), two per section slightly
# detuned (a section, not a soloist), a short room (early reflections), 16 chords of a chorale.
#   python3 tools/loopback/choir.py <dir>   -> choir.wav, choir.json (ground truth: notes per part)
import json, sys
import numpy as np
SR = 44100
rng = np.random.default_rng(11)

def lfilter(b, a, x):   # direct-form IIR, up to 2 poles
    y = np.zeros(len(x)); a1 = a[1] if len(a) > 1 else 0.0; a2 = a[2] if len(a) > 2 else 0.0
    b0 = b[0]; b1 = b[1] if len(b) > 1 else 0.0; x1 = y1 = y2 = 0.0
    for i, xi in enumerate(x):
        yi = b0 * xi + b1 * x1 - a1 * y1 - a2 * y2
        y[i] = yi; x1 = xi; y2 = y1; y1 = yi
    return y
def formant(x, fc, bw):
    r = np.exp(-np.pi * bw / SR); th = 2 * np.pi * fc / SR
    return lfilter([1 - r], [1, -2 * r * np.cos(th), r * r], x)

VOWELS = {"a": [(730, 90), (1090, 110), (2440, 170)], "e": [(530, 70), (1840, 120), (2480, 170)], "o": [(570, 80), (840, 100), (2410, 170)], "u": [(440, 70), (1020, 100), (2240, 160)]}
SCALE = {"S": 1.12, "A": 1.05, "T": 0.97, "B": 0.9}   # formants shift with the singer's size
# Bass, tenor, alto, soprano per chord (two chorales of test_choir.cpp, one second each).
CHORDS = [(48, 55, 64, 72), (41, 57, 65, 72), (43, 59, 62, 74), (48, 55, 64, 72), (45, 57, 64, 72), (50, 57, 65, 74), (43, 59, 65, 74), (48, 55, 64, 72),
          (43, 59, 62, 71), (48, 60, 64, 72), (50, 57, 66, 74), (43, 59, 67, 74), (52, 59, 67, 76), (45, 57, 64, 72), (50, 57, 66, 74), (43, 55, 62, 71)]
DUR = 1.0
n = int(SR * (len(CHORDS) * DUR + 1)); mix = np.zeros(n); truth = {p: [] for p in "SATB"}
for part, idx in zip("SATB", (3, 2, 1, 0)):
    for singer in range(2):
        ph = 0.0; detune = rng.normal(0, 6); vib = 5.0 + rng.uniform(0, 1.2); level = 0.5 if part in "SB" else 0.42
        for k, ch in enumerate(CHORDS):
            m = ch[idx]; t = np.arange(int(SR * DUR)) / SR
            f = 440 * 2 ** ((m - 69 + detune / 100) / 12) * 2 ** ((25 * np.sin(2 * np.pi * vib * t + singer) * np.clip(t / 0.25, 0, 1)
                                                                 + 5 * rng.standard_normal(len(t)).cumsum() / np.sqrt(len(t) + 1)) / 1200)
            phase = ph + np.cumsum(f) / SR; ph = phase[-1] % 1
            saw = 2 * (phase % 1) - 1
            src = lfilter([1], [1, -0.9], np.diff(saw, prepend=saw[0])) * (1 + 0.05 * rng.standard_normal(len(t))) + 0.03 * rng.standard_normal(len(t))
            vow = VOWELS["aoeu"[k % 4]]
            y = sum(formant(src, fc * SCALE[part], bw) * g for (fc, bw), g in zip(vow, (1.0, 0.6, 0.3)))
            env = np.clip((t - 0.02) / 0.06, 0, 1) * np.clip((DUR - t) / 0.07, 0, 1)
            x = env * y / (np.abs(y).max() + 1e-9)
            if k % 3 == 0:
                nc = int(0.025 * SR); x[:nc] += rng.standard_normal(nc) * np.linspace(0.25, 0, nc)
            i0 = max(0, int(SR * (k * DUR + rng.normal(0, 0.008) + 0.01))); mix[i0:i0 + len(x)] += x[:n - i0] * level
            if singer == 0: truth[part].append((m, k * DUR + 0.03, (k + 1) * DUR))
room = mix.copy()
for delay, g in ((0.011, 0.35), (0.019, 0.25), (0.031, 0.18), (0.047, 0.12)):
    d = int(SR * delay); room[d:] += g * mix[:-d]
room *= 0.9 / np.abs(room).max()
import wave
with wave.open(sys.argv[1] + "/choir.wav", "wb") as w:
    w.setnchannels(1); w.setsampwidth(2); w.setframerate(SR); w.writeframes((room * 32767).astype("<i2").tobytes())
json.dump({"chordSeconds": DUR, "parts": truth}, open(sys.argv[1] + "/choir.json", "w"))
print("choir.wav", round(n / SR, 1), "s,", len(CHORDS), "chords, 2 singers per section")
