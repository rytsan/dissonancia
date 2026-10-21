# DEV: the ruler (docs/ROADMAP.md phase 0). Runs core/build/dz_wav over the evaluation corpora and
# prints the R1 figures of docs/REQUIREMENTS.md, measured the way a player sees them:
#   harmony / chord on screen   majmin right, scored from 0.5 s after each reference change (R1.1)
#   exact                       the chord played's full symbol, where the reference is in our vocabulary (R1.4)
#   delay                       from a reference change to the screen showing it, median and p90 (R1.2)
#   changes                     shown changes per reference change (R1.3)
#   take                        the backdated labels a take keeps, majmin, all the time
#   melody                      raw pitch accuracy (+-50 cents) of the live notes, voiced frames (R1.5)
#
#   python3 tools/eval/eval.py [corpus ...] [--limit N] [--json out.json] [--check baseline.json]
#   corpora: synthetic (generated, no download), guitarset, rwc-pop, vocadito (tools/eval/corpora.py)
#   --check: exit 1 when a figure falls more than 1 point (or 50 ms) below the baseline
import json, os, statistics, subprocess, sys, tempfile
from concurrent.futures import ThreadPoolExecutor

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "../.."))
DZ = os.environ.get("DZ_WAV", os.path.join(ROOT, "core/build/dz_wav"))
DATA = os.environ.get("DZ_EVAL_DATA", os.path.expanduser("~/.cache/dissonancia/eval"))
SETTLE = 0.5   # the latency budget: scored from this long after each change
STEP = 0.05

# ---------------------------------------------------------------- chord labels
NOTE = {"C": 0, "D": 2, "E": 4, "F": 5, "G": 7, "A": 9, "B": 11}
def pc(s): return (NOTE[s[0]] + s[1:].count("#") - s[1:].count("b")) % 12

# our symbol qualities (core matcher) and Harte's, onto one vocabulary
OURS = {"": "maj", "m": "min", "7": "7", "maj7": "maj7", "m7": "min7", "sus2": "sus2", "sus4": "sus4",
        "add9": "add9", "dim": "dim", "5": "5", "6": "maj6", "m6": "min6", "dim7": "dim7", "m7b5": "hdim7", "aug": "aug"}
HARTE = {"maj": "maj", "min": "min", "7": "7", "maj7": "maj7", "min7": "min7", "sus2": "sus2", "sus4": "sus4",
         "dim": "dim", "5": "5", "maj6": "maj6", "min6": "min6", "dim7": "dim7", "hdim7": "hdim7", "aug": "aug",
         "add9": "add9", "maj(9)": "add9", "": "maj", "1": None}
MAJ = {"maj", "7", "maj7", "maj6", "add9", "9", "maj9", "11", "13"}
MIN = {"min", "min7", "min6", "min9", "minmaj7", "min11"}

def from_ours(sym):   # "F#m7/C#" -> (root, quality) or None
    if not sym or sym in ("-", "N"): return None
    sym = sym.split("/")[0]
    n = 2 if len(sym) > 1 and sym[1] in "#b" else 1
    return pc(sym[:n]), OURS.get(sym[n:], sym[n:])

def from_harte(v):    # "Ab:min7/b3" -> (root, quality) or None ("N", "X")
    if v in ("N", "X", ""): return None
    root, _, q = v.partition(":")
    q = q.split("/")[0]
    return pc(root), HARTE.get(q, q)

def majmin(c):
    if c is None: return None
    r, q = c
    return (r, "maj") if q in MAJ else (r, "min") if q in MIN else None

# ---------------------------------------------------------------- corpora
def synthetic():
    d = os.path.join(DATA, "synthetic")
    if not os.path.exists(os.path.join(d, "mix.json")):
        os.makedirs(d, exist_ok=True)
        for g in ("mix.py", "picking.py"):
            subprocess.run([sys.executable, os.path.join(ROOT, "tools/loopback", g), d], check=True, capture_output=True)
    items = []
    for w in ("mix", "mix_guitar", "mix_guitar_drums", "picking-arpeggio", "picking-travis", "picking-damped", "picking-slow68"):
        j = os.path.join(d, w + ".json")
        if not os.path.exists(j): j = os.path.join(d, "mix.json")
        t = json.load(open(j))
        ref = [(k * t["bar"], (k + 1) * t["bar"], from_ours(c)) for k, c in enumerate(t["chords"])]
        items.append((w, os.path.join(d, w + ".wav"), "general" if w.startswith("mix") else "guitar", ref))
    return items

def guitarset():
    d = os.path.join(DATA, "guitarset")
    mic = next((os.path.join(r) for r, _, fs in os.walk(d) if any(f.endswith("_comp_mic.wav") for f in fs)), None)
    ann = next((r for r, _, fs in os.walk(d) if any(f.endswith(".jams") for f in fs)), None)
    if not mic or not ann: sys.exit("guitarset: run tools/eval/corpora.py guitarset")
    items = []
    for f in sorted(f for f in os.listdir(mic) if f.endswith("_comp_mic.wav")):
        base = f[:-len("_mic.wav")]
        j = json.load(open(os.path.join(ann, base + ".jams")))
        a = [a for a in j["annotations"] if a["namespace"] == "chord"][-1]   # the performed chords
        ref = [(o["time"], o["time"] + o["duration"], from_harte(o["value"])) for o in a["data"]]
        items.append((base, os.path.join(mic, f), "guitar", ref))
    return items

def rwc_pop():
    d = os.path.join(DATA, "rwc-pop")
    chords = next((r for r, _, fs in os.walk(d) if r.endswith(os.path.join("chords", "RWC-P"))), None)
    wavs = {os.path.splitext(f)[0]: os.path.join(r, f) for r, _, fs in os.walk(d) for f in fs if f.lower().endswith(".wav")}
    if not chords or not wavs: sys.exit("rwc-pop: run tools/eval/corpora.py rwc-pop")
    items = []
    for f in sorted(os.listdir(chords)):
        rid = f[:-4]   # RWC_P001
        wav = wavs.get(rid) or next((p for k, p in wavs.items() if k.endswith(rid[-3:]) and "P" in k.upper()), None)
        if not wav: continue
        ref = []
        for line in open(os.path.join(chords, f)).read().splitlines()[1:]:
            a, b, v = line.split(";")
            ref.append((float(a), float(b), from_harte(v)))
        items.append((rid, wav, "general", ref))
    return items

def vocadito():
    d = os.path.join(DATA, "vocadito")
    audio = next((r for r, _, fs in os.walk(d) if any(f.endswith(".wav") for f in fs)), None)
    f0 = next((r for r, _, fs in os.walk(d) if r.rstrip("/").endswith("F0")), None)
    if not audio or not f0: sys.exit("vocadito: run tools/eval/corpora.py vocadito")
    items = []
    for f in sorted(os.listdir(audio)):
        if not f.endswith(".wav"): continue
        b = f[:-4]
        fp = next((os.path.join(f0, g) for g in os.listdir(f0) if g.startswith(b)), None)
        if fp: items.append((b, os.path.join(audio, f), "voice", fp))
    return items

CORPORA = {"synthetic": synthetic, "guitarset": guitarset, "rwc-pop": rwc_pop, "vocadito": vocadito}

# ---------------------------------------------------------------- scoring
def run(wav, mode):
    env = dict(os.environ, DZ_NOTES="1")
    out = subprocess.run([DZ, wav, mode], capture_output=True, text=True, env=env).stdout
    ev = {"shown": [], "harmony": [], "chord": [], "note": []}
    for line in out.splitlines():
        f = line.split()
        if not f or f[0] not in ev: continue
        if f[0] == "note": ev["note"].append((float(f[1]), float(f[2]), int(f[3])))
        elif len(f) > 2: ev[f[0]].append((float(f[1]), f[2]))
    return ev

def at(seq, t, i0=0):
    """The label on screen at t (seq sorted by time); returns (label, index to resume from)."""
    i, cur = i0, None
    while i < len(seq) and seq[i][0] <= t: i += 1
    return (seq[i - 1][1] if i > 0 else None), max(0, i - 1)

def score_chords(ref, ev):
    end = ref[-1][1]
    changes = [b for (a0, b0, c0), (b, _, c1) in zip(ref, ref[1:]) if majmin(c0) != majmin(c1) and majmin(c1)]
    res = {}
    for k in ("harmony", "shown", "chord"):
        seq = sorted(ev[k])
        ok = n = ex_ok = ex_n = 0
        i = 0
        t = 0.0
        for a, b, c in ref:
            if c is None: continue
            settled_from = a + (SETTLE if k != "chord" else 0)
            t = max(t, settled_from)
            while t < b and t < end:
                lab, i = at(seq, t, i)
                want = majmin(c)
                got = from_ours(lab) if lab else None
                if want:
                    n += 1; ok += majmin(got) == want
                if k == "shown" and c[1] in OURS.values():
                    ex_n += 1; ex_ok += got == c
                t += STEP
        r = {"majmin": 100 * ok / n if n else None}
        if k == "shown": r["exact"] = 100 * ex_ok / ex_n if ex_n else None
        if k != "chord":
            delays = []
            for ch in changes:
                seg = next((s for s in ref if s[0] <= ch < s[1]), None)
                want = majmin(seg[2]); limit = seg[1]
                hit = next((ts for ts, s in seq if ch - 0.1 <= ts < limit and majmin(from_ours(s)) == want), None)
                if hit is None and at(seq, ch)[0] and majmin(from_ours(at(seq, ch)[0])) == want: hit = ch   # already shown (anticipated)
                delays.append((hit - ch) if hit is not None else limit - ch)
            r["delays"] = delays
            shown_changes = sum(1 for (_, x), (_, y) in zip(seq, seq[1:]) if majmin(from_ours(x)) != majmin(from_ours(y)))
            r["changes"] = (shown_changes, len(changes))
        res[k] = r
        res[k]["_n"] = n
    return res

def score_melody(f0path, ev):
    ok = n = 0
    notes = ev["note"]
    for line in open(f0path).read().splitlines():
        f = line.replace(",", " ").split()
        try: t, hz = float(f[0]), float(f[1])
        except (ValueError, IndexError): continue
        if hz <= 0: continue
        n += 1
        import math
        want = 69 + 12 * math.log2(hz / 440)
        on = [m for a, b, m in notes if a <= t < b]
        ok += bool(on) and abs(on[0] - want) <= 0.5
    return {"rpa": 100 * ok / n if n else None, "_n": n}

def evaluate(name, limit):
    items = CORPORA[name]()[:limit]
    def one(it):
        _, wav, mode, ref = it
        ev = run(wav, mode)
        return score_melody(ref, ev) if mode == "voice" else score_chords(ref, ev)
    with ThreadPoolExecutor(os.cpu_count() or 4) as ex: rs = list(ex.map(one, items))
    out = {"items": len(items)}
    if name == "vocadito":
        tot = sum(r["_n"] for r in rs)
        out["melody_rpa"] = sum((r["rpa"] or 0) * r["_n"] for r in rs) / tot if tot else None
        return out
    for k, label in (("harmony", "harmony"), ("shown", "chord"), ("chord", "take")):
        tot = sum(r[k]["_n"] for r in rs)
        out[f"{label}_majmin"] = sum((r[k]["majmin"] or 0) * r[k]["_n"] for r in rs) / tot if tot else None
        if k != "chord":
            ds = sorted(d for r in rs for d in r[k]["delays"])
            out[f"{label}_delay_med"] = statistics.median(ds) if ds else None
            out[f"{label}_delay_p90"] = ds[int(0.9 * len(ds))] if ds else None
            sc = sum(r[k]["changes"][0] for r in rs); rc = sum(r[k]["changes"][1] for r in rs)
            out[f"{label}_changes"] = sc / rc if rc else None
    ex = [r["shown"] for r in rs if r["shown"].get("exact") is not None]
    out["chord_exact"] = statistics.mean(r["exact"] for r in ex) if ex else None
    return out

def show(name, r):
    f = lambda v, u="%": "—" if v is None else (f"{v:.1f} %" if u == "%" else f"{v:.2f} s" if u == "s" else f"×{v:.2f}")
    if "melody_rpa" in r:
        print(f"{name:10s} {r['items']:4d}  melody RPA {f(r['melody_rpa'])}")
        return
    print(f"{name:10s} {r['items']:4d}  harmony {f(r['harmony_majmin'])} delay {f(r['harmony_delay_med'], 's')} p90 {f(r['harmony_delay_p90'], 's')} changes {f(r['harmony_changes'], 'x')}"
          f"  | chord {f(r['chord_majmin'])} exact {f(r['chord_exact'])} delay {f(r['chord_delay_med'], 's')} changes {f(r['chord_changes'], 'x')}  | take {f(r['take_majmin'])}")

if __name__ == "__main__":
    args, names, limit, jout, check = sys.argv[1:], [], None, None, None
    while args:
        a = args.pop(0)
        if a == "--limit": limit = int(args.pop(0))
        elif a == "--json": jout = args.pop(0)
        elif a == "--check": check = args.pop(0)
        else: names.append(a)
    results = {}
    for n in names or ["synthetic", "guitarset", "rwc-pop", "vocadito"]:
        results[n] = evaluate(n, limit)
        show(n, results[n])
    if jout: json.dump(results, open(jout, "w"), indent=1)
    if check:
        base, bad = json.load(open(check)), []
        for n, r in results.items():
            for k, v in base.get(n, {}).items():
                if v is None or r.get(k) is None or k == "items": continue
                worse = (r[k] - v > 0.05) if "delay" in k else (r[k] - v > 0.05) if "changes" in k else (v - r[k] > 1.0)
                if worse: bad.append(f"{n} {k}: {r[k]:.2f} (baseline {v:.2f})")
        if bad: print("REGRESSION\n  " + "\n  ".join(bad)); sys.exit(1)
        print("no regression against", check)
