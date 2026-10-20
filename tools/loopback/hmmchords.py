# DEV prototype: chords from notes as a hidden Markov model, decoded online with a short lag.
# Measured 2026-10-20 (time correct; tracker today -> this on psynotes.py notes):
#   picking arpeggio 74 -> 92, travis 84 -> 89, damped 83 -> 95, slow 6/8 75 -> 94 (perfect notes: 91-95)
#   band mix 85 -> 91, guitar+bass 93 -> 96, guitar+voice 89 -> 89, guitar+drums 94.5 -> 92
#   [LAG=0.3] [STAY=0.99] [V=1] python3 tools/loopback/hmmchords.py notes.frames truth.json
#   notes.frames: "t midi midi ..." per hop (psynotes.py OUT, or the true notes through a frame dump)
# Each chord explains the notes heard lately: a chord tone is likely, any other note unlikely, every
# note weighted by how recently it sounded (memory, tau), the bass on its own term (a chord tone,
# preferably the root). Chords change rarely (STAY per hop); the key heard so far makes diatonic
# chords more expected. Online: forward filtering; each hop's label is decided LAG seconds later
# (fixed-lag smoothing), so a first impression (A5 before its third) can be corrected.
import sys, json, math, os
import numpy as np

NAMES = ["C", "C#", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B"]
Q = {"": [0, 4, 7], "m": [0, 3, 7], "7": [0, 4, 7, 10], "maj7": [0, 4, 7, 11], "m7": [0, 3, 7, 10],
     "dim": [0, 3, 6], "sus4": [0, 5, 7], "sus2": [0, 2, 7], "5": [0, 7]}
PRIOR = {"": 0, "m": 0, "7": -0.7, "maj7": -1.2, "m7": -1.0, "dim": -1.5, "sus4": -1.5, "sus2": -1.5, "5": -2.5}
FAM = {"": "M", "7": "M", "maj7": "M", "m": "m", "m7": "m", "dim": "o", "sus4": "s", "sus2": "s", "5": "5"}
TAU = float(os.environ.get("TAU", 0.3))      # note memory (s)
LAG = float(os.environ.get("LAG", 0.6))      # decision delay (s)
STAY = float(os.environ.get("STAY", 0.99))   # per hop
P_IN, P_OUT = 0.9, 0.08                      # a heard note is a chord tone / is not

states = [(r, q) for r in range(12) for q in Q]
member = np.array([[1.0 if p in [(r + i) % 12 for i in Q[q]] else 0.0 for p in range(12)] for r, q in states])
size = member.sum(1)
prior = np.array([PRIOR[q] for _, q in states])
MAJOR = [0, 2, 4, 5, 7, 9, 11]

frames = []
for l in open(sys.argv[1]):
    f = l.split()
    frames.append((float(f[0]), [int(m) for m in f[1:]]))
hop = frames[1][0] - frames[0][0]
lag = max(0, int(round(LAG / hop)))
S = len(states)
logA_stay, logA_move = math.log(STAY), math.log((1 - STAY) / (S - 1))
mem = np.zeros(12)
age = {}                                     # midi -> seconds it has been sounding
SUSTAIN = float(os.environ.get("SUSTAIN", 0.4))   # a note counts fully once it lasted this long (s)
TREBLE = float(os.environ.get("TREBLE", 0.5))     # weight of notes above G4 (melody register)
keysum = np.zeros(12)
alpha = np.full(S, -math.log(S))
history = []          # (t, backpointers) per hop
labels = []
for t, ns in frames:
    mem *= math.exp(-hop / TAU)
    age = {m: age.get(m, 0.0) + hop for m in ns}
    # harmony is what sustains (a melody's passing notes are short) and sits low; the melody register weighs less
    for m in ns:
        w = (min(1.0, age[m] / SUSTAIN) if SUSTAIN > 0 else 1.0) * (TREBLE if m >= 67 else 1.0)
        mem[m % 12] = max(mem[m % 12], w)
    for m in ns: keysum[m % 12] += hop
    bass = min(ns) % 12 if ns else -1
    # emission: every remembered note, weighted by its memory, is a chord tone or not; chords that
    # leave many of their tones unheard pay a little (completeness), the bass is a chord tone.
    if mem.max() > 0.05:
        heard = mem
        e = heard @ (member * math.log(P_IN) + (1 - member) * math.log(P_OUT)).T
        e += 0.4 * ((member * heard).sum(1) / size - 1)          # completeness
        if bass >= 0: e += np.where(member[:, bass] > 0, 0.0, -1.0) + np.array([0.3 if r == bass else 0 for r, _ in states])
    else:
        e = np.zeros(S)
    # key: diatonic chords (in the best-fitting major scale so far) a little more expected
    if keysum.sum() > 4:
        tonic = max(range(12), key=lambda k: sum(keysum[(k + i) % 12] for i in MAJOR))
        scale = {(tonic + i) % 12 for i in MAJOR}
        e += np.array([0.3 if all((r + i) % 12 in scale for i in Q[q]) else 0 for r, q in states])
    # Viterbi: best path into each state; stay or move from the best other state
    best = int(np.argmax(alpha))
    stay = alpha + logA_stay
    move = alpha[best] + logA_move
    back = np.where(stay >= move, np.arange(S), best)
    alpha = np.maximum(stay, move) + e + prior * hop * 5
    alpha -= alpha.max()
    history.append((t, back))
    # fixed lag: backtrack lag hops from today's best state; that is the decision for t - lag
    if len(history) > lag:
        s_ = int(np.argmax(alpha))
        for k in range(len(history) - 1, len(history) - 1 - lag, -1): s_ = int(history[k][1][s_])
        labels.append((history[-lag - 1][0], states[s_]))
# the last lag hops: backtrack from the end
s_ = int(np.argmax(alpha)); tail = []
for k in range(len(history) - 1, len(history) - 1 - lag, -1):
    tail.append((history[k][0], states[s_])); s_ = int(history[k][1][s_])
labels += tail[::-1]

truth = json.load(open(sys.argv[2])); bar = truth["bar"]; chords = truth["chords"]
def parse(sym):
    root = NAMES.index(sym[:2]) if sym[:2] in NAMES else NAMES.index(sym[0])
    rest = sym[2:] if sym[:2] in NAMES else sym[1:]
    return root, ("m" if rest.startswith("m") and not rest.startswith("maj") else "M")
ok = n = 0
for t, (r, q) in labels:
    k = int(t / bar)
    if k >= len(chords): continue
    n += 1
    tr, tf = parse(chords[k])
    ok += r == tr and FAM[q] == tf
print(f"time correct {100 * ok / max(1, n):.1f} %")
if os.environ.get("V"):
    prev = None
    for t, (r, q) in labels:
        s = NAMES[r] + q
        if s != prev: print(f"{t:6.2f} {s}"); prev = s
