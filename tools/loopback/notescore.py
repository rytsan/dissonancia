# Note transcription score (mir_eval style): a reference note is found when an estimated note has
# the same MIDI pitch and an onset within 50 ms. Precision, recall, F; plus extra / split notes.
#   python3 tools/loopback/notescore.py truth.json dz_wav_output.txt   (truth: {"notes": [[midi, start, end], ...]})
import json, sys
ref = [tuple(n) for n in json.load(open(sys.argv[1]))["notes"]]
est = []
for line in open(sys.argv[2]):
    if line.startswith("note "):
        _, a, b, m = line.split()[:4]; est.append((int(m), float(a), float(b)))
used = set(); hit = 0; err = []
for m, a, b in ref:
    best = None
    for i, (em, ea, eb) in enumerate(est):
        if i in used or em != m or abs(ea - a) > 0.05: continue
        if best is None or abs(ea - a) < abs(est[best][1] - a): best = i
    if best is not None: used.add(best); hit += 1; err.append(est[best][1] - a)
p = hit / max(1, len(est)); r = hit / max(1, len(ref)); f = 2 * p * r / max(1e-9, p + r)
off = sorted(abs(e) for e in err)
print(f"notes F {100*f:.1f} %  (P {100*p:.1f}, R {100*r:.1f})  {len(est)} estimated for {len(ref)}  onset |err| median {1000*off[len(off)//2] if off else 0:.0f} ms")
