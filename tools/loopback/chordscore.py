# Scores dz_wav output against a ground-truth JSON {"bar": seconds, "chords": [...]} (one chord per bar).
#   python3 tools/loopback/chordscore.py mix.json dz_wav_output.txt
# Exact = same root and triad family (major/minor/other, sevenths count as their triad); bass ignored.
import json, re, sys
truth = json.load(open(sys.argv[1])); bar = truth["bar"]; chords = truth["chords"]
NAMES = {"C": 0, "D": 2, "E": 4, "F": 5, "G": 7, "A": 9, "B": 11}
def parse(sym):
    sym = sym.split("/")[0]
    m = re.match(r"([A-G])([#b]?)(.*)", sym); root = (NAMES[m[1]] + {"#": 1, "b": -1, "": 0}[m[2]]) % 12; q = m[3]
    fam = "m" if q.startswith("m") and not q.startswith("maj") else "dim" if "dim" in q or "h" in q else "5" if q == "5" else "sus" if "sus" in q else "M"
    return root, fam
events, stats, frames, ended = [], "", [], []
for line in open(sys.argv[2]):
    if line.startswith("chord"): _, t, s, c = line.split(); events.append((float(t), s))
    elif line.startswith("ended"): _, a, b_, s = line.split(); ended.append((float(a), float(b_), s))
    elif line.startswith("preview changes"): stats = line.strip()
    elif line.startswith("preview"): _, t, s = line.split(); frames.append((float(t), s))
end = len(chords) * bar; step = 0.01; ok = total = 0
for i in range(int(end / step)):
    t = i * step; shown = [s for ts, s in events if ts <= t]
    if not shown: continue
    total += 1
    r, f = parse(shown[-1]); tr, tf = parse(chords[int(t / bar)])
    ok += r == tr and (f == tf or (f == "5" and tf in "Mm"))
wrong = sum(1 for ts, s in events if ts < end and parse(s) != parse(chords[min(len(chords) - 1, int((ts + 0.05) / bar))]))
print(f"time correct {100 * ok / max(1, total):.1f} %   confirmations {len(events)} for {len(chords)} chords ({wrong} wrong)   {stats}")
if frames:   # DZ_PREVIEW=1: frame-level accuracy of the matcher alone (no tracker)
    good = [s != "-" and parse(s) == parse(chords[min(len(chords) - 1, int(t / bar))]) for t, s in frames if t < end and s != "-"]
    print(f"frames correct {100 * sum(good) / max(1, len(good)):.1f} %")
if ended:   # the take as the score sees it: ChordEnded labels over their spans
    good = tot = 0
    for i in range(int(end / step)):
        t = i * step; span = [s for a, b_, s in ended if a <= t < b_]
        if not span: continue
        tot += 1; r, f = parse(span[0]); tr, tf = parse(chords[int(t / bar)]); good += r == tr and (f == tf or (f == "5" and tf in "Mm"))
    print(f"take (ended labels) correct {100 * good / max(1, tot):.1f} %, {len(ended)} chords")
