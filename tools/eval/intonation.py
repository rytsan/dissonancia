# DEV: the coach's intonation metrics (R2.4; core/src/intonation.cpp) on vocadito. The reference is
# the same metrics read from the annotated F0 inside annotator 1's notes, with the same rules (steady
# part, scoop window, vibrato test), so the error measured is the hearing: YIN and the note decoder.
#   python3 tools/eval/intonation.py
import math, os, statistics, subprocess, sys
from concurrent.futures import ThreadPoolExecutor
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import eval as e

ATTACK, RELEASE, SCOOP, IN_TUNE = 0.08, 0.05, 0.06, 20

def med(v): return statistics.median(v)

def slope(t, c):
    n, sx, sy = len(t), sum(t), sum(c)
    den = n * sum(x * x for x in t) - sx * sx
    return (n * sum(x * y for x, y in zip(t, c)) - sx * sy) / den if den > 0 else 0

def vibrato(t, c, k, hop):
    if t[-1] - t[0] < 0.3: return 0, 0
    m, tm = sum(c) / len(c), sum(t) / len(t)
    r = [y - m - k * (x - tm) for x, y in zip(t, c)]
    e0 = sum(v * v for v in r)
    best, lag = 0, 0
    for L in range(max(1, math.floor(1 / (9 * hop))), math.ceil(1 / (3.5 * hop)) + 1):
        if L >= len(r) // 2: break
        ac = sum(r[i] * r[i + L] for i in range(len(r) - L)) / e0 * len(r) / (len(r) - L) if e0 > 0 else 0
        if ac > best: best, lag = ac, L
    depth = math.sqrt(2 * e0 / len(r))
    return (1 / (lag * hop), depth) if best > 0.4 and depth > 8 and lag else (0, 0)

def note(track, a, b, midi, hop, prev_end=-1):
    g = a   # the glide into the note, followed back up to 60 ms
    for x, m in reversed([p for p in track if p[0] < a]):
        if x < max(prev_end, a - 0.06) or m <= 0 or abs(m - midi) >= 2: break
        g = x
    dur = b - a
    sa, sb = a + min(ATTACK, dur / 4), b - min(RELEASE, dur / 6)
    t, c, sc = [], [], []
    for x, m in track:
        if m <= 0 or x < g or x >= b: continue
        cents = 100 * (m - midi)
        if x < g + SCOOP and abs(cents) < 200: sc.append(cents)
        if x < a: continue
        if sa <= x < sb and abs(cents) < 100: t.append(x); c.append(cents)
    if len(c) < 5: return None
    k = slope(t, c)
    cm = med(c)
    vh, vd = vibrato(t, c, k, hop)
    n3 = len(c) // 3
    drift = med(c[-n3:]) - med(c[:n3]) if n3 >= 2 else k * (t[-1] - t[0])
    return {"cents": cm, "scoop": sum(sc) / len(sc) - cm if sc else 0, "drift": drift, "vib": vh, "vibd": vd}

def summary(notes):
    c = [n["cents"] for n in notes]
    tun = med(c)
    return {"tuning": tun, "inTune": 100 * sum(abs(x) <= IN_TUNE for x in c) / len(c),
            "inTuneOwn": 100 * sum(abs(x - tun) <= IN_TUNE for x in c) / len(c), "meanAbs": sum(abs(x - tun) for x in c) / len(c)}

def one(it):
    name, wav, _, f0path = it
    track = []
    for line in open(f0path):
        x, hz = map(float, line.split(","))
        track.append((x, 69 + 12 * math.log2(hz / 440) if hz > 0 else 0))
    hop = (track[-1][0] - track[0][0]) / (len(track) - 1)
    ann = os.path.join(os.path.dirname(os.path.dirname(f0path)), "Notes", name + "_notesA1.csv")
    ref, prev = [], -1
    for line in open(ann):
        a, hz, d = map(float, line.split(","))
        midi = round(69 + 12 * math.log2(hz / 440))
        r = note(track, a, a + d, midi, hop, prev)
        prev = a + d
        if r: ref.append((a, a + d, midi, r))
    out = subprocess.run([e.DZ, wav, "intonation"], capture_output=True, text=True).stdout.splitlines()
    ours = []
    for l in out:
        f = l.split()
        if f[0] == "note":
            ours.append((float(f[1]), float(f[2]), int(f[3]), {"cents": float(f[5]), "scoop": float(f[7]), "drift": float(f[9]), "vib": float(f[11]), "vibd": float(f[12])}))
    pairs = []
    for a, b, m, r in ref:   # our note of the same pitch overlapping most of it
        best = max(((min(b, ob) - max(a, oa), o) for oa, ob, om, o in ours if om == m), default=(0, None), key=lambda p: p[0])
        if best[1] and best[0] > 0.5 * (b - a): pairs.append((r, best[1]))
    return name, ref, ours, pairs

R = list(ThreadPoolExecutor(os.cpu_count()).map(one, e.CORPORA["vocadito"]()))
P = [p for _, _, _, ps in R for p in ps]
nref = sum(len(r) for _, r, _, _ in R)
print(f"vocadito {len(R)} tracks: {nref} reference notes read, {len(P)} matched by ours ({100 * len(P) / nref:.0f} %)")
def err(k): v = sorted(abs(a[k] - b[k]) for a, b in P); return f"median {med(v):.1f}, p90 {v[int(.9 * len(v))]:.1f}"
print(f"  per note |error| (cents): pitch {err('cents')}; scoop {err('scoop')}; drift {err('drift')}")
vr = [a["vib"] > 0 for a, _ in P]; vo = [b["vib"] > 0 for _, b in P]
tp = sum(a and b for a, b in zip(vr, vo))
print(f"  vibrato notes: reference {sum(vr)}, ours {sum(vo)}, both {tp}"
      + (f"; rate |error| median {med([abs(a['vib'] - b['vib']) for a, b in P if a['vib'] and b['vib']]):.2f} Hz, depth {med([abs(a['vibd'] - b['vibd']) for a, b in P if a['vib'] and b['vib']]):.1f} cents" if tp else ""))
sc = [(abs(a["scoop"]) > 30, abs(b["scoop"]) > 30) for a, b in P]
print(f"  scoops (> 30 cents): reference {sum(x for x, _ in sc)}, ours {sum(y for _, y in sc)}, both {sum(x and y for x, y in sc)}")
for k in ("tuning", "inTune", "inTuneOwn", "meanAbs"):
    d = sorted(abs(summary([x[3] for x in ref])[k] - summary([x[3] for x in ours])[k]) for _, ref, ours, _ in R if ref and ours)
    print(f"  per track {k}: |error| median {med(d):.1f}, p90 {d[int(.9 * len(d))]:.1f}")
