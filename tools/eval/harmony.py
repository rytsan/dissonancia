# DEV: the coach's harmony execution (R2.6; core/src/harmony.cpp) on GuitarSet comping takes. The
# reference comes from the annotations: the performed chords (their bounds are on the beat grid), the
# notes per string, the beats (the click the players followed).
#   changes: each annotated chord change's attack, the strum (onsets 50 ms apart at most, at their
#     median) nearest the bound within 0.25 s, against the nearest eighth; ours matched within 0.3 s.
#   gaps: no annotated note sounding 150..30 ms before that attack (the hand lifted early).
#   clean: per take, the annotated notes' time in the performed chord's tones (its first 0.25 s left
#     out), against ours (our chords, the notes we heard).
#   python3 tools/eval/harmony.py [step]
import json, os, statistics, subprocess, sys, tempfile
from concurrent.futures import ThreadPoolExecutor
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import eval as e

step = int(sys.argv[1]) if len(sys.argv) > 1 else 1
items = e.CORPORA["guitarset"]()[::step]
ann = next(r for r, _, fs in os.walk(os.path.join(e.DATA, "guitarset")) if any(f.endswith(".jams") for f in fs))
tmp = tempfile.mkdtemp(prefix="dz_harmony_")

SHORT = {"maj": [0, 4, 7], "min": [0, 3, 7], "dim": [0, 3, 6], "aug": [0, 4, 8], "maj7": [0, 4, 7, 11], "min7": [0, 3, 7, 10],
         "7": [0, 4, 7, 10], "dim7": [0, 3, 6, 9], "hdim7": [0, 3, 6, 10], "minmaj7": [0, 3, 7, 11], "maj6": [0, 4, 7, 9],
         "min6": [0, 3, 7, 9], "9": [0, 4, 7, 10, 2], "maj9": [0, 4, 7, 11, 2], "min9": [0, 3, 7, 10, 2], "sus2": [0, 2, 7],
         "sus4": [0, 5, 7], "5": [0, 7], "1": [0]}
DEG = {"1": 0, "2": 2, "3": 4, "4": 5, "5": 7, "6": 9, "7": 11, "9": 2, "11": 5, "13": 9}

def degree(d):
    k = DEG[d.lstrip("b#")]
    return (k - d.count("b") + d.count("#")) % 12

def tones(label):   # Harte "D#:sus2(7)/1" -> pitch classes, None for N / X
    if ":" not in label: return None
    root, q = label.split(":")
    r = e.pc(root)
    q, bass = (q.split("/") + [None])[:2]
    ext = []
    if "(" in q: q, ext = q[:q.index("(")], q[q.index("(") + 1:-1].split(",")
    s = set(SHORT.get(q or "maj", [0, 4, 7]))
    for x in ext:
        if x.startswith("*"): s.discard(degree(x[1:]))
        elif x: s.add(degree(x))
    if bass: s.add(degree(bass))
    return {(r + i) % 12 for i in s}

def one(it):
    name, wav = it[0], it[1]
    j = json.load(open(os.path.join(ann, name + ".jams")))
    bp = [o for a in j["annotations"] if a["namespace"] == "beat_position" for o in a["data"]]
    first = next(i for i, o in enumerate(bp) if o["value"]["position"] == 1)
    beats = [o["time"] for o in bp[first:]]
    per_bar = bp[0]["value"]["num_beats"]
    chords = [a for a in j["annotations"] if a["namespace"] == "chord"][1]["data"]   # performed
    notes = sorted((o["time"], o["time"] + o["duration"], round(o["value"])) for a in j["annotations"] if a["namespace"] == "note_midi" for o in a["data"])
    groups = []
    for a, _, _ in notes:
        if groups and a - groups[-1][-1] <= 0.05: groups[-1].append(a)
        else: groups.append([a])
    attacks = [statistics.median(g) for g in groups]
    bf = os.path.join(tmp, name + ".beats")
    open(bf, "w").write("\n".join(f"{t:.4f}" for t in beats))
    out = subprocess.run([e.DZ, wav, "harmony", bf, str(per_bar)], capture_output=True, text=True).stdout.splitlines()
    ours = [l.split() for l in out if l.startswith("change ")]
    ours = [(float(f[1]), float(f[3]), float(f[7])) for f in ours]   # time, offset, gap
    summ = next(l.split() for l in out if l.startswith("harmony "))
    clean_ours = float(summ[summ.index("clean") + 1])

    ref = []   # (attack, offset ms or None, gap)
    for k in range(1, len(chords)):
        b = chords[k]["time"]
        if tones(chords[k]["value"]) == tones(chords[k - 1]["value"]): continue
        near = [t for t in attacks if abs(t - b) <= 0.25]
        if not near: continue
        t = min(near, key=lambda x: abs(x - b))
        i = max(0, min(len(beats) - 2, sum(x <= t for x in beats) - 1))
        d = beats[i + 1] - beats[i]
        frac = (t - beats[i]) / d
        e8 = round(frac * 2) / 2
        off = (frac - e8) * d * 1000 if abs(frac - e8) <= 0.1 else None
        gap = not any(a < t - 0.03 and z > t - 0.15 for a, z, _ in notes)
        ref.append((t, off, gap))
    pairs = []
    for t, off, gap in ref:
        m = [o for o in ours if abs(o[0] - t) <= 0.3]
        if m: pairs.append(((t, off, gap), min(m, key=lambda o: abs(o[0] - t))))
    # Clean, from the annotations: notes' time in the performed chord's tones.
    inn = tot = 0
    for c in chords:
        s = tones(c["value"])
        if s is None: continue
        a0, a1 = c["time"] + 0.25, c["time"] + c["duration"]
        for a, z, m in notes:
            ov = min(z, a1) - max(a, a0)
            if ov > 0: tot += ov; inn += ov * (m % 12 in s)
    return name, ref, ours, pairs, 100 * inn / tot if tot else None, clean_ours

R = list(ThreadPoolExecutor(os.cpu_count()).map(one, items))
nref = sum(len(r[1]) for r in R); nours = sum(len(r[2]) for r in R); P = [p for r in R for p in r[3]]
print(f"guitarset {len(R)} takes: {nref} annotated changes, {nours} of ours, {len(P)} matched (R {len(P) / nref:.2f}, P {len(P) / nours:.2f})")
d = sorted(abs(o[1] - r[1]) for r, o in P if r[1] is not None and o[1] == o[1])
both = len(d); refgrid = sum(r[1] is not None for r, _ in P)
print(f"  change offset |error| median {statistics.median(d):.1f} ms, p90 {d[int(.9 * len(d))]:.1f} ms ({both} of {refgrid} on the grid in both)")
bias = statistics.median(o[1] - r[1] for r, o in P if r[1] is not None and o[1] == o[1])
print(f"  change offset bias (ours - annotated) median {bias:+.1f} ms")
g = [(r[2], o[2] <= -10) for r, o in P]
tp = sum(a and b for a, b in g)
print(f"  gaps: annotated {sum(a for a, _ in g)}, ours {sum(b for _, b in g)}, both {tp}"
      + (f" (P {tp / max(1, sum(b for _, b in g)):.2f} R {tp / max(1, sum(a for a, _ in g)):.2f})"))
c = [(a, b) for *_, a, b in R if a is not None and b == b]
err = sorted(abs(a - b) for a, b in c)
print(f"  clean % per take: annotated median {statistics.median(a for a, _ in c):.1f}, ours {statistics.median(b for _, b in c):.1f};"
      f" |error| median {statistics.median(err):.1f}, p90 {err[int(.9 * len(err))]:.1f}")
xs, ys = zip(*c)
mx, my = statistics.mean(xs), statistics.mean(ys)
cov = sum((a - mx) * (b - my) for a, b in c)
print(f"  clean % per take: correlation ours / annotated {cov / (sum((a - mx) ** 2 for a in xs) * sum((b - my) ** 2 for b in ys)) ** 0.5:.2f}")
