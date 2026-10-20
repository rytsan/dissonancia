# DEV: the delay a player sees — from each chord change in the ground truth to the moment the
# display shows the new chord (dz_wav "shown" lines) and the new harmony ("harmony" lines).
# The chord lines carry the start the lag backdates them to, so chordscore.py never sees this delay.
#   python3 tools/loopback/latency.py truth.json dz_wav_output.txt   (dz_wav run with DZ_NOTES=1)
import json, statistics, sys

NOTE = {"C": 0, "D": 2, "E": 4, "F": 5, "G": 7, "A": 9, "B": 11}
def root(sym):
    p = NOTE[sym[0]] + (1 if sym[1:2] == "#" else -1 if sym[1:2] == "b" else 0)
    return p % 12
def triad(sym):   # root and major/minor third
    q = sym.split("/")[0][1 + (sym[1:2] in ("#", "b")):]
    return root(sym), q.startswith("m") and not q.startswith("maj")

truth = json.load(open(sys.argv[1])); bar = truth["bar"]; chords = truth["chords"]
ev = {"shown": [], "harmony": []}
for line in open(sys.argv[2]):
    f = line.split()
    if f and f[0] in ev and len(f) > 2: ev[f[0]].append((float(f[1]), f[2]))
for kind, shown in ev.items():
    delays, missed = [], 0
    for k in range(1, len(chords)):
        if root(chords[k]) == root(chords[k - 1]): continue
        b = k * bar
        hit = next((t for t, s in shown if b - 0.1 <= t < b + bar and root(s) == root(chords[k])), None)
        if hit is None: missed += 1
        else: delays.append(hit - b)
    # what is on screen, when it is on screen: right root and third, the share of the time
    ok = n = 0
    t = 0.0
    while t < len(chords) * bar:
        on = [s for ts, s in shown if ts <= t]
        n += 1
        ok += bool(on) and triad(on[-1]) == triad(chords[int(t / bar)])
        t += 0.01
    if delays:
        print(f"{kind:8s} median {statistics.median(delays):.2f} s  max {max(delays):.2f} s  missed {missed}/{len(delays) + missed}  on screen {100 * ok / n:.1f} %  changes {len(shown)}")
    else:
        print(f"{kind:8s} none")
