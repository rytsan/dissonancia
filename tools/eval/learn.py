# DEV: learns the harmony HMM's emission for a corpus (docs/IMPLEMENTATION.md, phase 1).
# The perceived notes (dz_wav DZ_NOTEDUMP) become a memory per register (bass < E3, middle, treble
# >= G4) at three time scales (0.1, 0.4, 1.5 s) plus the lowest note; a softmax over the 24 major /
# minor triads, its weights tied by interval from the root, is fitted to the reference chords.
#   python3 tools/eval/learn.py rwc-pop --split   train on even items, report frame accuracy on odd
#   python3 tools/eval/learn.py rwc-pop           train on all, print the C++ table (core/src/notes.cpp)
import os, subprocess, sys
import numpy as np
from concurrent.futures import ThreadPoolExecutor
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import eval as e
corpus, split = sys.argv[1], "--split" in sys.argv
TAUS, DELAY, HOP, SCALE = (0.1, 0.4, 1.5), 0.1, 0.02, 2.0   # SCALE: per-hop logits against the HMM's change cost (swept: 2 best)
cache = os.path.join(e.DATA, "learn", corpus + ("-" + os.path.basename(os.environ["DZ_NOTES_IN"].rstrip("/")) if "DZ_NOTES_IN" in os.environ else ""))   # DZ_NOTES_IN: another front end
os.makedirs(cache, exist_ok=True)
items = e.CORPORA[corpus]()

def feats(it):
    name, wav, mode, ref = it
    p = os.path.join(cache, name + ".notes")
    if not os.path.exists(p):
        txt = subprocess.run([e.DZ, wav, mode], capture_output=True, text=True, env={**os.environ, "DZ_NOTES": "1", "DZ_NOTEDUMP": "1"}).stdout
        open(p, "w").write("\n".join(l for l in txt.splitlines() if l.startswith("notes ")))
    rows = [l.split()[1:] for l in open(p).read().splitlines()]
    n = len(rows); F = np.zeros((n, 9, 12), np.float32); low = np.full(n, -1); t = np.zeros(n)
    mem = np.zeros((3, 3, 12), np.float32); dec = np.exp(-HOP / np.array(TAUS))[None, :, None]
    for i, r in enumerate(rows):
        t[i] = float(r[0]); notes = [int(x) for x in r[1:] if x.isdigit()]
        mem *= dec
        for m in notes: mem[0 if m < 52 else 1 if m < 67 else 2, :, m % 12] = 1
        if notes: low[i] = min(notes) % 12
        F[i] = mem.reshape(9, 12)
    y = np.full(n, -1)
    for s, f, ch in ref:
        mm = e.majmin(ch) if ch else None
        if mm: y[(t - DELAY >= s) & (t - DELAY < f)] = mm[0] + 12 * (mm[1] == "min")
    k = (y >= 0) & (F.max((1, 2)) > 0.05)
    return F[k][::3], low[k][::3], y[k][::3]

with ThreadPoolExecutor(os.cpu_count()) as ex: data = list(ex.map(feats, items))
idx = (np.arange(12)[:, None] + np.arange(12)[None, :]) % 12   # [root, interval] -> pitch class
def build(ix):
    M = np.concatenate([data[i][0] for i in ix]); B = np.concatenate([data[i][1] for i in ix]); Y = np.concatenate([data[i][2] for i in ix])
    X = M[:, :, idx].transpose(0, 2, 1, 3).reshape(len(M), 12, -1)   # n, root, 9 x 12
    BB = np.zeros((len(B), 12, 12), np.float32)
    for r in range(12): ok = B >= 0; BB[ok, r, (B[ok] - r) % 12] = 1
    return np.concatenate([X, BB, np.ones((len(B), 12, 1), np.float32)], 2), Y
train = [i for i in range(len(items)) if not split or i % 2 == 0]
X, Y = build(train)
W = np.zeros((2, X.shape[2]), np.float32); mt = np.zeros_like(W); vt = np.zeros_like(W)
sc = lambda W, X: np.einsum("nrd,qd->nqr", X, W).reshape(len(X), 24)
for it in range(500):   # Adam on the softmax cross-entropy, a little L2
    S_ = sc(W, X); S_ -= S_.max(1, keepdims=True); P = np.exp(S_); P /= P.sum(1, keepdims=True)
    P[np.arange(len(Y)), Y] -= 1
    g = np.einsum("nqr,nrd->qd", P.reshape(-1, 2, 12) / len(Y), X) + 1e-4 * W
    mt = 0.9 * mt + 0.1 * g; vt = 0.999 * vt + 0.001 * g * g
    W -= 0.05 * (mt / (1 - 0.9 ** (it + 1))) / (np.sqrt(vt / (1 - 0.999 ** (it + 1))) + 1e-8)
if split:
    Xt, Yt = build([i for i in range(len(items)) if i % 2 == 1])
    print(f"{corpus}: frame accuracy on the odd items {(sc(W, Xt).argmax(1) == Yt).mean():.3f} (per hop, no HMM)")
else:
    w = (W * SCALE).reshape(-1)
    print(f"// tools/eval/learn.py {corpus}: major then minor; 9 x 12 memory weights (register x time scale, by interval), 12 bass, 1 bias")
    print("constexpr float kBandEmission[2 * 121] = {" + ", ".join(f"{x:.3f}f" for x in w) + "};")
