# DEV prototype: chords from perceived notes (psynotes.py OUT), bass-anchored accumulation and
# pitch-class-set templates (no partials).
#   [TAU=0.8] [V=1] python3 tools/loopback/notechords.py notes.txt truth.json
#   -> time-correct % (same root and triad family, as chordscore.py)
import sys, json, math
import numpy as np
NAMES = ["C", "C#", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B"]
Q = {"": [0, 4, 7], "m": [0, 3, 7], "7": [0, 4, 7, 10], "maj7": [0, 4, 7, 11], "m7": [0, 3, 7, 10], "dim": [0, 3, 6],
     "sus4": [0, 5, 7], "sus2": [0, 2, 7], "5": [0, 7]}
FAM = {"": "M", "7": "M", "maj7": "M", "m": "m", "m7": "m", "dim": "o", "sus4": "s", "sus2": "s", "5": "5"}
OCCAM = {"": 0, "m": 0, "5": 0.15, "7": 0.08, "maj7": 0.1, "m7": 0.08, "dim": 0.1, "sus4": 0.1, "sus2": 0.1}
TAU = float(__import__("os").environ.get("TAU", 0.8))

frames = []
for l in open(sys.argv[1]):
    f = l.split()
    frames.append((float(f[0]), [int(m) for m in f[1:]]))
hop = frames[1][0] - frames[0][0]
acc = np.zeros(12); bass_pc = -1; labels = []
for t, ns in frames:
    acc *= math.exp(-hop / TAU)
    low = [m for m in ns if m < 52]
    if low:
        b = min(low) % 12
        if b != bass_pc and acc[b] < 0.3 * max(acc.max(), 1e-9):   # a new bass outside what sounds: restart
            acc[:] = 0
        bass_pc = b
    for m in ns: acc[m % 12] = max(acc[m % 12], 1.0)
    if acc.max() < 0.3: labels.append((t, None)); continue
    w = acc / acc.max()
    best = (-9, None)
    for r in range(12):
        for q, iv in Q.items():
            tones = [(r + i) % 12 for i in iv]
            s = sum(w[p] for p in tones) / len(tones) - 0.5 * sum(w[p] for p in range(12) if p not in tones) / 3 - OCCAM[q]
            s += 0.1 * (r == bass_pc)
            if s > best[0]: best = (s, (r, q))
    labels.append((t, best[1]))
truth = json.load(open(sys.argv[2])); bar = truth["bar"]; chords = truth["chords"]
def parse(sym):
    root = NAMES.index(sym[:2]) if sym[:2] in NAMES else NAMES.index(sym[0])
    rest = sym[2:] if sym[:2] in NAMES else sym[1:]
    return root, ("m" if rest.startswith("m") and not rest.startswith("maj") else "M")
ok = n = 0
for t, lab in labels:
    k = int(t / bar)
    if k >= len(chords): continue
    n += 1
    r, f = parse(chords[k])
    ok += lab is not None and lab[0] == r and FAM[lab[1]] == f
print(f"time correct {100 * ok / max(1, n):.1f} %")
if __import__("os").environ.get("V"):
    prev = None
    for t, lab in labels:
        s = NAMES[lab[0]] + lab[1] if lab else "-"
        if s != prev: print(f"{t:6.2f} {s}"); prev = s
