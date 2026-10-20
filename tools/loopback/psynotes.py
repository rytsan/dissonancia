# DEV prototype: perceptual note detection by register (bass / mid / treble).
#   K=3 TH=0.5 G=0.15 OUT=notes.txt [CHROMA=pc.txt] python3 tools/loopback/psynotes.py take.wav bpm
#   OUT: "t midi midi ..." per frame (noteeval.py, notechords.py); CHROMA: "t c0..c11" for
#   dz_wav's DZ_CHROMA_IN. Measured 2026-10-20 against YourMT3+ on a flamenco guitar take: note
#   precision 0.71 vs 0.56 for the core's chroma (F1 0.75 vs 0.64); on synthetic picking 0.91-0.93
#   vs 0.75-0.79. As the chord matcher's input it does worse (the matcher expects partials).
# Per frame (STFT 8192 @ 44.1k, hop 1024):
#   1. loudness per bin: power -> dB, equal-loudness (A-weighting approx), -> sones-like (Stevens 0.3)
#   2. masking: spreading function on the Bark scale (Schroeder); a peak counts only above the mask
#   3. virtual pitch: subharmonic summation of the audible peaks over candidate notes E2..E6,
#      iterative: take the best note, remove its partials, repeat
#   4. registers: a note is kept when it is strong within its own register (not against the bass)
import sys, wave
import numpy as np

path, bpm = sys.argv[1], float(sys.argv[2])
def read_wav(path):   # PCM 16 or float32, any channels -> mono
    import struct
    d = open(path, 'rb').read(); i = 12; fmt = None
    while i < len(d):
        cid, sz = d[i:i + 4], struct.unpack('<I', d[i + 4:i + 8])[0]
        if cid == b'fmt ': fmt = struct.unpack('<HHIIHH', d[i + 8:i + 24])
        if cid == b'data':
            tag, ch, sr, _, _, bits = fmt
            y = np.frombuffer(d[i + 8:i + 8 + sz], '<f4' if tag == 3 else '<i2').astype(float)
            if tag != 3: y /= 32768
            return y.reshape(-1, ch).mean(1), sr
        i += 8 + sz + (sz & 1)
x, sr = read_wav(path)
import os
N, H = int(os.environ.get("NFFT", 8192)), 1024
K = int(os.environ.get("K", 6)); TH = float(os.environ.get("TH", 0.35)); G = float(os.environ.get("G", 0.0))
win = np.hanning(N)
freqs = np.fft.rfftfreq(N, 1 / sr)
bark = 13 * np.arctan(0.00076 * freqs) + 3.5 * np.arctan((freqs / 7500) ** 2)
f2 = np.maximum(freqs, 1) ** 2
ra = 12194**2 * f2**2 / ((f2 + 20.6**2) * np.sqrt((f2 + 107.7**2) * (f2 + 737.9**2)) * (f2 + 12194**2))
aw = 20 * np.log10(np.maximum(ra, 1e-9)) + 2.0          # A-weighting dB
NAMES = ["C", "C#", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B"]
REG = [(40, 52, "bass"), (52, 67, "mid"), (67, 89, "treb")]   # by fundamental MIDI

def frame_notes(seg):
    X = np.abs(np.fft.rfft(seg * win)) ** 2
    db = 10 * np.log10(X + 1e-12) + aw
    # peaks (local maxima above the floor)
    pk = np.where((db[1:-1] > db[:-2]) & (db[1:-1] >= db[2:]) & (freqs[1:-1] > 60) & (freqs[1:-1] < 5000))[0] + 1
    if len(pk) == 0: return []
    top = db[pk].max()
    pk = pk[db[pk] > top - 60]
    # masking: each peak spreads a threshold (Schroeder, -10 dB offset); keep peaks above the others' mask
    keep = []
    for i in pk:
        dz = bark[i] - bark[pk]
        spread = 15.81 + 7.5 * (dz + 0.474) - 17.5 * np.sqrt(1 + (dz + 0.474) ** 2)
        mask = db[pk] + spread - 10
        mask[pk == i] = -1e9
        if db[i] > mask.max(): keep.append(i)
    keep = np.array(keep)
    if len(keep) == 0: return []
    # refine peak frequency (parabolic), loudness (Stevens: intensity^0.3)
    a, b, c = db[keep - 1], db[keep], db[keep + 1]
    p = 0.5 * (a - c) / (a - 2 * b + c + 1e-12)
    fk = (keep + p) * sr / N
    loud = 10 ** (0.03 * (b - top))   # relative sones-like
    # virtual pitch: subharmonic summation, iterative removal
    cand = np.arange(40, 89)
    f0s = 440 * 2 ** ((cand - 69) / 12)
    alive = np.ones(len(fk), bool)
    notes = []
    for _ in range(K):
        best, bi, bused = 0, -1, None
        for ci, f0 in enumerate(f0s):
            h = fk / f0
            hn = np.round(h)
            ok = alive & (hn >= 1) & (hn <= 10) & (np.abs(1200 * np.log2(np.maximum(h, 1e-9) / np.maximum(hn, 1))) < 35)
            if not ok.any(): continue
            # a note needs its fundamental or 2nd harmonic heard (no missing-fundamental guesses below the guitar)
            if not (ok & (hn <= 2)).any(): continue
            s = (loud[ok] / hn[ok] ** 0.5).sum()
            if s > best: best, bi, bused = s, ci, ok
        if bi < 0: break
        notes.append((int(cand[bi]), best))
        alive &= ~bused
    if not notes: return []
    gmax = max(s for _, s in notes)
    notes = [(m, s) for m, s in notes if s >= G * gmax]
    # registers: strong within its own register
    out = []
    for lo, hi, name in REG:
        r = [(m, s) for m, s in notes if lo <= m < hi]
        if not r: continue
        mx = max(s for _, s in r)
        out += [(m, s / mx, name) for m, s in r if s >= TH * mx]
    return out

import os
frames = [(t * H / sr + N / 2 / sr, frame_notes(x[t * H:t * H + N])) for t in range(0, (len(x) - N) // H)]
with open(os.environ.get("OUT", "psy.notes.txt"), "w") as f:
    for t, ns in frames: f.write(f"{t:.3f} " + " ".join(str(m) for m, s, r in ns) + "\n")
if os.environ.get("CHROMA"):   # per-frame pitch-class salience (register-normalised), the chord input
    with open(os.environ["CHROMA"], "w") as f:
        for t, ns in frames:
            v = [0.0] * 12
            for m, s, r in ns: v[m % 12] = max(v[m % 12], s)
            f.write(f"{t:.3f} " + " ".join(f"{e * e:.4f}" for e in v) + "\n")   # amplitude -> energy
