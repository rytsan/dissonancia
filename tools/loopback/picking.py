# Fingerpicking (dedilhado) test signals: the chords are never struck together, only one string
# at a time. Four patterns over C Am F G Em Am Dm G7 (twice), each with its ground truth:
#   arpeggio  p-i-m-a-m-i-m-a eighths, strings let ring until struck again or the chord changes
#   travis    alternating bass on the beats (thumb), treble notes on the offbeats
#   damped    the same arpeggio, every note damped after 150 ms (nothing rings)
#   slow68    6/8 broken chord, one note per eighth, dotted quarter = 50
#   python3 tools/loopback/picking.py <dir>   -> picking-<pattern>.wav / .json
import json, sys
import numpy as np
exec(open(__file__.replace("picking.py", "gen.py")).read().split("bpm = 92")[0])   # SR, rng, ks(), write()

OPEN = [40, 45, 50, 55, 59, 64]
# frets per string (low E .. high e), -1 = not played; bass = the string the thumb plays on the downbeat
SHAPES = {"C": [-1, 3, 2, 0, 1, 0], "Am": [-1, 0, 2, 2, 1, 0], "F": [1, 3, 3, 2, 1, 1], "G": [3, 2, 0, 0, 0, 3],
          "Em": [0, 2, 2, 0, 0, 0], "Dm": [-1, -1, 0, 2, 3, 1], "G7": [3, 2, 0, 0, 0, 1]}
BASS = {"C": 1, "Am": 1, "F": 0, "G": 0, "Em": 0, "Dm": 2, "G7": 0}
ALT = {"C": 2, "Am": 2, "F": 2, "G": 2, "Em": 2, "Dm": 3, "G7": 2}   # the thumb's other string (travis)
prog = ["C", "Am", "F", "G", "Em", "Am", "Dm", "G7"] * 2


def render(name, bar, notes, ring=True, damp=0.15):
    """notes: (time, string, chord index). A string rings until it is plucked again or its chord ends."""
    total = len(prog) * bar + 2
    g = np.zeros(int(SR * total))
    for k, (t0, s, ci) in enumerate(notes):
        later = [t for t, s2, _ in notes[k + 1:] if s2 == s]
        end = min([(ci + 1) * bar] + later) if ring else t0 + damp
        midi = OPEN[s] + SHAPES[prog[ci]][s]
        t0 = max(0.0, t0 + rng.normal(0, 0.006))
        note = ks(midi, end - t0 + 0.03) * (1.0 if s == BASS[prog[ci]] else 0.75)
        rel = int(0.03 * SR); note[-rel:] *= np.linspace(1, 0, rel)
        i0 = int(SR * t0); g[i0:i0 + len(note)] += note[:len(g) - i0]
    write(f"{sys.argv[1]}/picking-{name}.wav", g)
    json.dump({"bar": bar, "chords": prog}, open(f"{sys.argv[1]}/picking-{name}.json", "w"))


def treble(chord):   # the strings above the bass that the chord plays, low to high
    return [s for s in range(BASS[chord] + 1, 6) if SHAPES[chord][s] >= 0]


beat = 60 / 80; bar = 4 * beat
arp = []
for ci, c in enumerate(prog):
    i, m, a = treble(c)[-3:]
    order = [BASS[c], i, m, a, m, i, m, a]   # thumb, then i m a up and down
    arp += [(ci * bar + k * beat / 2, s, ci) for k, s in enumerate(order)]
render("arpeggio", bar, arp)
render("damped", bar, arp, ring=False)

travis = []
for ci, c in enumerate(prog):
    tr = treble(c)
    for b in range(4):
        travis.append((ci * bar + b * beat, BASS[c] if b % 2 == 0 else ALT[c], ci))
        travis.append((ci * bar + (b + 0.5) * beat, tr[[-1, -2, -3, -2][b]], ci))
render("travis", bar, travis)

eighth = 60 / 50 / 3; bar68 = 6 * eighth
slow = []
for ci, c in enumerate(prog):
    i, m, a = treble(c)[-3:]
    order = [BASS[c], i, m, a, m, i]
    slow += [(ci * bar68 + k * eighth, s, ci) for k, s in enumerate(order)]
render("slow68", bar68, slow)
