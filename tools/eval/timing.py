# DEV: the coach's timing metrics (R2.1, R2.2; core/src/timing.cpp) on GuitarSet comping takes.
# The players followed a click, and the beat annotations are that grid. Per take, the metrics from
# the attacks we detect are set against the same metrics from the annotated note onsets (a strum's
# strings, 50 ms apart at most, are one attack, at their median). Without the grid, the tempo heard
# (track_beats) is set against the click's.
#   python3 tools/eval/timing.py [step]     every step-th take (default 1)
import json, os, statistics, subprocess, sys, tempfile
from concurrent.futures import ThreadPoolExecutor
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import eval as e

step = int(sys.argv[1]) if len(sys.argv) > 1 else 1
items = e.CORPORA["guitarset"]()[::step]
ann = next(r for r, _, fs in os.walk(os.path.join(e.DATA, "guitarset")) if any(f.endswith(".jams") for f in fs))
tmp = tempfile.mkdtemp(prefix="dz_timing_")

def run(wav, beats=None, onsets=None):
    env = {**os.environ, **({"DZ_ONSETS_IN": onsets} if onsets else {})}
    out = subprocess.run([e.DZ, wav, "timing"] + ([beats] if beats else []), capture_output=True, text=True, env=env).stdout
    f = next(l.split() for l in out.splitlines() if l.startswith("timing "))
    return {f[i]: float(f[i + 1]) for i in range(1, len(f) - 1, 2)}

def one(it):
    name, wav = it[0], it[1]
    j = json.load(open(os.path.join(ann, name + ".jams")))
    beats = [o["time"] for a in j["annotations"] if a["namespace"] == "beat_position" for o in a["data"]]
    bpm = next(o["value"] for a in j["annotations"] if a["namespace"] == "tempo" for o in a["data"])
    notes = sorted(o["time"] for a in j["annotations"] if a["namespace"] == "note_midi" for o in a["data"])
    groups = []
    for t in notes:
        if groups and t - groups[-1][-1] <= 0.05: groups[-1].append(t)
        else: groups.append([t])
    bp, op = os.path.join(tmp, name + ".beats"), os.path.join(tmp, name + ".onsets")
    open(bp, "w").write("\n".join(f"{t:.4f}" for t in beats))
    open(op, "w").write("\n".join(f"{statistics.median(g):.4f}" for g in groups))
    return run(wav, bp), run(wav, bp, op), run(wav), bpm

with ThreadPoolExecutor(os.cpu_count()) as ex: R = list(ex.map(one, items))
ok = [r for r in R if r[0]["matched"] >= 16 and r[1]["matched"] >= 16]
def gap(k): return [abs(a[k] - b[k]) for a, b, _, _ in ok]
mean_bias = statistics.median(a["mean"] - b["mean"] for a, b, _, _ in ok)
print(f"guitarset {len(R)} takes ({len(ok)} with 16+ attacks on the grid)")
print(f"  mean offset: ours - annotated, median {mean_bias:+.1f} ms; |error| median {statistics.median(gap('mean')):.1f} ms, p90 {sorted(gap('mean'))[int(.9 * len(ok))]:.1f} ms")
d = [a["mean"] - b["mean"] - mean_bias for a, b, _, _ in ok]
print(f"  mean offset, that constant removed: |error| median {statistics.median(abs(x) for x in d):.1f} ms, p90 {sorted(abs(x) for x in d)[int(.9 * len(ok))]:.1f} ms")
print(f"  spread:      |error| median {statistics.median(gap('spread')):.1f} ms, p90 {sorted(gap('spread'))[int(.9 * len(ok))]:.1f} ms"
      f"  (annotated spread median {statistics.median(b['spread'] for _, b, _, _ in ok):.1f} ms)")
heard = [abs(h["bpm"] / bpm - 1) for _, _, h, bpm in R if h["bpm"] > 0]
octave = [min(abs(h["bpm"] / bpm * m - 1) for m in (0.5, 1, 2, 2 / 3, 1.5)) for _, _, h, bpm in R if h["bpm"] > 0]
print(f"  tempo heard (no grid): within 2 % {100 * sum(x <= 0.02 for x in heard) / len(R):.1f} %,"
      f" counting octave / 3:2 errors {100 * sum(x <= 0.02 for x in octave) / len(R):.1f} %")
