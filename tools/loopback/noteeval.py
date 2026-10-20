# DEV: note-level agreement per 50 ms frame (which pitch classes sound) against a reference.
#   python3 tools/loopback/noteeval.py ref.notes other.notes core.chroma [psy.notes]
#   notes files: "start end midi ..." (YourMT3+ / Basic Pitch dumps, picking.py truth);
#   core.chroma: dz_wav with DZ_CHROMA=1 (lines "chroma t c0..c11 ..."); psy: psynotes.py OUT.
import sys, numpy as np
FR = 0.05
def load(p):
    rows = [l.split() for l in open(p)]
    return [(float(a), float(b), int(m)) for a, b, m, *_ in rows]
def frames(notes, T, minlen=0.0):
    F = np.zeros((int(T / FR) + 1, 12), bool)
    for a, b, m in notes:
        if b - a < minlen: continue
        F[int(a / FR):int(b / FR) + 1, m % 12] = True
    return F
def f1(P, R):
    tp = (P & R).sum(); fp = (P & ~R).sum(); fn = (~P & R).sum()
    p = tp / max(1, tp + fp); r = tp / max(1, tp + fn)
    return p, r, 2 * p * r / max(1e-9, p + r)

if __name__ == "__main__":
    mt3 = load(sys.argv[1]); bp = load(sys.argv[2])
    T = max(b for _, b, _ in mt3 + bp)
    R = frames(mt3, T); B = frames(bp, T)
    print("BasicPitch vs reference  P %.2f R %.2f F1 %.2f" % f1(B, R))
    C = np.zeros_like(R)
    for l in open(sys.argv[3]):
        f = l.split(); t = float(f[1]); e = np.array([float(v) for v in f[2:14]])
        if t < 0 or e.max() <= 0: continue
        k = int(t / FR)
        if k < len(C): C[k] |= np.sqrt(e) >= 0.3 * np.sqrt(e.max())
    print("core chroma (strong) vs reference  P %.2f R %.2f F1 %.2f" % f1(C, R))
    if len(sys.argv) > 4:
        P = np.zeros_like(R)
        for l in open(sys.argv[4]):
            f = l.split(); k = int(float(f[0]) / FR)
            for m in f[1:]:
                if k < len(P): P[k, int(m) % 12] = True
        print("psy notes vs reference  P %.2f R %.2f F1 %.2f" % f1(P, R))
