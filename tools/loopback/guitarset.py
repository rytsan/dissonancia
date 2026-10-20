# DEV: chord accuracy on GuitarSet (Xi et al., ISMIR 2018, CC BY 4.0; zenodo.org/records/3371780):
# solo acoustic guitar, 6 players x 5 styles (bossa nova, funk, jazz, rock, singer-songwriter) x
# comping and soloing, every chord annotated. Scores dz_wav (both chord paths) on the comping takes
# with MIREX "majmin": the root and a major or minor third must match; sus, dim, aug and no-chord
# spans are left out.
#   python3 tools/loopback/guitarset.py <annotation dir> <audio_mono-mic dir> [limit]
import json, os, subprocess, sys
from collections import defaultdict

ann_dir, mic_dir = sys.argv[1], sys.argv[2]
limit = int(sys.argv[3]) if len(sys.argv) > 3 else 10**9
DZ = os.path.join(os.path.dirname(__file__), "../../core/build/dz_wav")
NOTE = {"C": 0, "D": 2, "E": 4, "F": 5, "G": 7, "A": 9, "B": 11}

def pc(name):
    p = NOTE[name[0]]
    for a in name[1:]: p += 1 if a == "#" else -1 if a == "b" else 0
    return p % 12

def harte(label):   # "A:min7/b3" -> (root, "maj"/"min") or None
    if label in ("N", "X"): return None
    root, _, q = label.partition(":")
    q = (q or "maj").split("/")[0].split("(")[0]
    if q.startswith(("min", "m7", "m6")) and not q.startswith("maj"): third = "min"
    elif q.startswith(("maj", "7", "9", "11", "13", "6")) or q == "": third = "maj"
    else: return None   # sus, dim, hdim, aug: not majmin
    return pc(root), third

def ours(sym):     # "F#m7/C#" -> (root, third) or None
    sym = sym.split("/")[0]
    r = pc(sym[:2] if len(sym) > 1 and sym[1] in "#b" else sym[0])
    q = sym[2:] if len(sym) > 1 and sym[1] in "#b" else sym[1:]
    if q.startswith(("sus", "5", "dim", "aug", "m7b5")): return None
    return r, "min" if q.startswith("m") and not q.startswith("maj") else "maj"

def chords_of(jams):
    d = json.load(open(jams))
    anns = [a for a in d["annotations"] if a["namespace"] == "chord"]
    # GuitarSet has the instructed (lead sheet) chords and the performed ones; the performed are the truth
    a = anns[-1]
    return [(o["time"], o["time"] + o["duration"], o["value"]) for o in a["data"]]

def run(wav, notes):
    env = dict(os.environ)
    env.pop("DZ_NOTES", None)
    if notes: env["DZ_NOTES"] = "1"
    out = subprocess.run([DZ, wav, "guitar"], capture_output=True, text=True, env=env).stdout
    ev = []
    for line in out.splitlines():
        f = line.split()
        if f and f[0] == "chord": ev.append((float(f[1]), f[2]))
        if f and f[0] == "ended": pass
    return sorted(ev)

totals = defaultdict(lambda: [0, 0])
files = sorted(f for f in os.listdir(mic_dir) if f.endswith("_comp_mic.wav"))[:limit]
for f in files:
    base = f[:-len("_mic.wav")]
    ref = chords_of(os.path.join(ann_dir, base + ".jams"))
    style = base.split("_")[1].split("-")[0]
    for path in ("now", "notes"):
        ev = run(os.path.join(mic_dir, f), path == "notes")
        ok = n = 0
        t = 0.0
        while t < ref[-1][1]:
            want = next((harte(v) for a, b, v in ref if a <= t < b), None)
            if want is not None:
                shown = [s for ts, s in ev if ts <= t]
                n += 1
                ok += bool(shown) and ours(shown[-1]) == want
            t += 0.05
        for key in (path, f"{path} {style}"):
            totals[key][0] += ok
            totals[key][1] += n
    print(f"{base:28s} now {100 * totals['now'][0] / max(1, totals['now'][1]):5.1f}  notes {100 * totals['notes'][0] / max(1, totals['notes'][1]):5.1f}", flush=True)
print()
for k in sorted(totals):
    print(f"{k:16s} {100 * totals[k][0] / max(1, totals[k][1]):5.1f} %")
