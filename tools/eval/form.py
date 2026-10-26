# DEV: the coach's form (R2.8; core/src/form.cpp) from our chords against the annotated chords', on
# the same annotated beat grid (from its first downbeat): GuitarSet comping (a progression played
# through, often twice) and RWC-Pop (songs). Each bar's label is compared pairwise (do two bars share
# a label in both?), as structure is scored (Levy & Sandler 2008); and the phrase length chosen.
#   python3 tools/eval/form.py [guitarset|rwc-pop] [step]
import json, os, statistics, subprocess, sys, tempfile
from concurrent.futures import ThreadPoolExecutor
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import eval as e

corpus = sys.argv[1] if len(sys.argv) > 1 else "guitarset"
step = int(sys.argv[2]) if len(sys.argv) > 2 else 1
items = e.CORPORA[corpus]()[::step]
tmp = tempfile.mkdtemp(prefix="dz_form_")
TRIAD = {"maj": (0, 4, 7), "min": (0, 3, 7)}

def grid_of(name):
    if corpus == "guitarset":
        ann = next(r for r, _, fs in os.walk(os.path.join(e.DATA, "guitarset")) if any(f.endswith(".jams") for f in fs))
        bp = [o for a in json.load(open(os.path.join(ann, name + ".jams")))["annotations"] if a["namespace"] == "beat_position" for o in a["data"]]
        first = next(i for i, o in enumerate(bp) if o["value"]["position"] == 1)
        return [o["time"] for o in bp[first:]], bp[0]["value"]["num_beats"]
    d = next(r for r, _, fs in os.walk(os.path.join(e.DATA, "rwc-pop")) if r.endswith(os.path.join("beats", "RWC-P")))
    rows = [l.split(";") for l in open(os.path.join(d, name + ".csv")).read().splitlines()[1:]]
    first = next(i for i, r in enumerate(rows) if int(r[1]) == 1)
    return [float(r[0]) for r in rows[first:]], max(int(r[1]) for r in rows)

def masks(chords, grid):   # chords: (start, end, (root, quality) or None); the triad on each beat
    out = []
    for k in range(len(grid)):
        t = grid[k] + 0.25 * ((grid[k + 1] if k + 1 < len(grid) else grid[k] + 0.5) - grid[k])
        c = next((e.majmin(c) for a, b, c in chords if a <= t < b), None)
        out.append(sum(1 << ((c[0] + i) % 12) for i in TRIAD[c[1]]) if c else 0)
    return out

def form(wav, grid_path, bpb, m, name):
    mp = os.path.join(tmp, name + ".masks")
    open(mp, "w").write("\n".join(map(str, m)))
    out = subprocess.run([e.DZ, wav, "form", grid_path, str(bpb), mp], capture_output=True, text=True).stdout.splitlines()
    secs = [l.split() for l in out if l.startswith("section ")]
    labels = [s[3] for s in secs for _ in range(int(s[2]))]
    f = next(l.split() for l in out if l.startswith("form "))
    return labels, int(f[3]), f[1]

def one(it):
    name, wav, mode, ref = it
    grid, bpb = grid_of(name)
    gp = os.path.join(tmp, name + ".beats")
    open(gp, "w").write("\n".join(f"{t:.4f}" for t in grid))
    out = subprocess.run([e.DZ, wav, "harmony", gp, str(bpb), "general" if mode == "general" else "guitar"], capture_output=True, text=True).stdout
    ours = [(float(f[1]), float(f[2]), e.from_ours(f[3])) for f in (l.split() for l in out.splitlines() if l.startswith("chord "))]
    t_lab, t_p, t_form = form(wav, gp, bpb, masks(ref, grid), name + ".ref")
    o_lab, o_p, o_form = form(wav, gp, bpb, masks(ours, grid), name + ".ours")
    n = min(len(t_lab), len(o_lab))
    pt = {(i, j) for i in range(n) for j in range(i + 1, n) if t_lab[i] == t_lab[j]}
    po = {(i, j) for i in range(n) for j in range(i + 1, n) if o_lab[i] == o_lab[j]}
    return name, t_form, o_form, t_p, o_p, pt, po

R = list(ThreadPoolExecutor(os.cpu_count()).map(one, items))
tp = sum(len(pt & po) for *_, pt, po in R); nt = sum(len(pt) for *_, pt, _ in R); no = sum(len(po) for *_, po in R)
P, Rc = tp / max(1, no), tp / max(1, nt)
print(f"{corpus} {len(R)} takes: pairwise P {P:.2f} R {Rc:.2f} F {2 * P * Rc / max(1e-9, P + Rc):.2f};"
      f" phrase length as the annotations' {100 * sum(a == b for *_, a, b, _, _ in R) / len(R):.0f} %;"
      f" same form string {100 * sum(a == b for _, a, b, *_ in R) / len(R):.0f} %")
for name, a, b, *_ in R[:6]: print(f"  {name}: annotated {a}, ours {b}")
