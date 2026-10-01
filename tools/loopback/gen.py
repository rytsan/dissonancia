# Test signals for the loopback run: strummed plucked-string guitar chords and a sung-like melody.
import numpy as np, wave, json, sys
SR = 48000
rng = np.random.default_rng(7)
def ks(midi, dur):
    # Plucked string (textbook): harmonic h ~ |sin(pi h beta)| / h with beta = 0.2 (pluck 1/5 from the
    # bridge), higher harmonics decay faster, inharmonicity B = 1e-4, 10 ms noise burst at the attack.
    f = 440 * 2 ** ((midi - 69) / 12); n = int(SR * dur); t = np.arange(n) / SR
    out = np.zeros(n)
    for h in range(1, 13):
        fh = h * f * np.sqrt(1 + 1e-4 * h * h)
        if fh > 8000: break
        a = abs(np.sin(np.pi * h * 0.2)) / h
        out += a * np.exp(-t / (1.8 / (1 + 0.4 * (h - 1)))) * np.sin(2 * np.pi * fh * t + rng.uniform(0, 6.3))
    burst = int(0.01 * SR)
    out[:burst] += 0.3 * rng.standard_normal(burst) * np.linspace(1, 0, burst)
    return out
def write(name, x):
    x = x / np.max(np.abs(x)) * 0.7
    with wave.open(name, 'wb') as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(SR); w.writeframes((x * 32767).astype(np.int16).tobytes())

bpm = 92; bar = 4 * 60 / bpm
OPEN = [40, 45, 50, 55, 59, 64]
prog = [("G", [3, 2, 0, 0, 0, 3]), ("Em", [0, 2, 2, 0, 0, 0]), ("C", [-1, 3, 2, 0, 1, 0]), ("D7", [-1, -1, 0, 2, 1, 2]),
        ("G/B", [-1, 2, 0, 0, 0, 3]), ("Am7", [-1, 0, 2, 0, 1, 0]), ("D", [-1, -1, 0, 2, 3, 2]), ("G", [3, 2, 0, 0, 0, 3])]
total = len(prog) * bar + 2
g = np.zeros(int(SR * total))
# A string rings until it is struck again or the chord changes (lifting the finger damps it): 30 ms release.
events = []
for b, (name, frets) in enumerate(prog):
    for beat in (0, 2):
        t0 = b * bar + beat * 60 / bpm
        k = 0
        for s_, fr in enumerate(frets):
            if fr < 0: continue
            events.append((s_, t0 + 0.012 * k, OPEN[s_] + fr, 1.0 if beat == 0 else 0.7, (b + 1) * bar)); k += 1
for s_, t0, midi, amp, chord_end in events:
    later = [e[1] for e in events if e[0] == s_ and e[1] > t0]
    end = min([chord_end] + later)
    note = ks(midi, end - t0 + 0.03) * amp
    rel = int(0.03 * SR); note[-rel:] *= np.linspace(1, 0, rel)
    i0 = int(SR * t0)
    g[i0:i0 + len(note)] += note[:len(g) - i0]
write(sys.argv[1] + "/guitar.wav", g)
json.dump({"bar": bar, "chords": [p[0] for p in prog]}, open(sys.argv[1] + "/guitar.json", "w"))

# Voice: G major line with a chromatic passing tone, vibrato 5.5 Hz +-15 cents, 3 harmonics + breath noise.
q = 60 / bpm
mel = [(67, 1), (69, 1), (71, 1), (72, 1), (74, 2), (73, 0.5), (72, 0.5), (71, 1), (69, 1), (67, 2), (0, 1), (71, 1), (74, 1), (79, 2)]
v = []
ph = 0
for m, beats in mel:
    n = int(SR * beats * q)
    if m == 0: v.append(np.zeros(n)); continue
    t = np.arange(n) / SR
    f = 440 * 2 ** ((m - 69) / 12) * 2 ** (15 * np.sin(2 * np.pi * 5.5 * t) / 1200 * np.clip(t / 0.25, 0, 1))
    phase = ph + 2 * np.pi * np.cumsum(f) / SR; ph = phase[-1]
    env = np.clip(t / 0.03, 0, 1) * np.clip((beats * q - t) / 0.05, 0, 1)
    v.append(env * (np.sin(phase) + 0.45 * np.sin(2 * phase) + 0.2 * np.sin(3 * phase)) + 0.01 * rng.standard_normal(n))
write(sys.argv[1] + "/voice.wav", np.concatenate([np.concatenate(v), np.zeros(SR)]))
json.dump({"notes": mel, "beat": q}, open(sys.argv[1] + "/voice.json", "w"))
print("ok", total)
