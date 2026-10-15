# Scores dz_wav's choir output against choir.py's truth: per part, the note at each chord's middle.
#   ./dz_wav choir.wav choir > out.txt; python3 tools/loopback/choirscore.py choir.json out.txt
import json, sys
truth = json.load(open(sys.argv[1]))
notes = [l.split() for l in open(sys.argv[2]) if l.strip()]
total = 0
for p in "SATB":
    right = 0; mine = [(float(a), float(b), int(m)) for q, a, b, m, *_ in notes if q == p]
    for m, a, b in truth["parts"][p]:
        mid = (a + b) / 2; got = [x for x in mine if x[0] <= mid <= x[1]]
        right += bool(got) and got[0][2] == m
    total += right
    print(f"{p}: {right}/{len(truth['parts'][p])}")
print(f"all: {total}/{sum(len(v) for v in truth['parts'].values())}; {len(notes)} notes written for {sum(len(v) for v in truth['parts'].values())} sung")
