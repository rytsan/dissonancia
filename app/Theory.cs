namespace Dissonancia;

public enum AppMode { VoiceMono, InstrumentMono, GuitarChords, PianoChords, GeneralChords }
public enum Quality { LowLatency, Balanced, HighPrecision }
public enum Clef { Treble, Treble8vb, Bass, Alto, Tenor }

/// Spelled pitch (spec §22.2): letter + alter, octave belongs to the LETTER (Cb4 = MIDI 59).
public readonly record struct Pitch(int Letter, int Alter, int Octave)
{
    static readonly int[] LetterSemitones = [0, 2, 4, 5, 7, 9, 11];

    public int Midi => 12 * (Octave + 1) + LetterSemitones[Letter] + Alter;
    public double Hz(double a4 = 440) => a4 * Math.Pow(2, (Midi - 69) / 12.0);
    public string PitchClassName => "CDEFGAB"[Letter] + Theory.Accidental(Alter);
    public string Name => PitchClassName + Octave;
    /// Diatonic step index (C0 = 0), used for staff positions.
    public int Step => Letter + 7 * Octave;
    public Pitch WithOctaveShift(int shift) => this with { Octave = Octave + shift };
}

/// One entry of the START key cascade, stored like MusicXML &lt;fifths&gt;.
public sealed record KeyOption(int Fifths, bool Minor)
{
    static readonly string[] MajorNames = ["C♭", "G♭", "D♭", "A♭", "E♭", "B♭", "F", "C", "G", "D", "A", "E", "B", "F♯", "C♯"];
    static readonly string[] MinorNames = ["A♭", "E♭", "B♭", "F", "C", "G", "D", "A", "E", "B", "F♯", "C♯", "G♯", "D♯", "A♯"];

    public string Tonic => (Minor ? MinorNames : MajorNames)[Fifths + 7];
    public string Label => Fifths switch
    {
        0 => $"{Tonic} {(Minor ? "minor" : "major")}",
        > 0 => $"{Tonic} {(Minor ? "minor" : "major")}  ·  {Fifths}♯",
        _ => $"{Tonic} {(Minor ? "minor" : "major")}  ·  {-Fifths}♭",
    };
    public override string ToString() => Label;

    /// Circle-of-fifths order from spec §22.2: C, sharps ascending, then flats ascending.
    public static IReadOnlyList<KeyOption> All(bool minor) =>
        [.. new[] { 0, 1, 2, 3, 4, 5, 6, 7, -1, -2, -3, -4, -5, -6, -7 }.Select(f => new KeyOption(f, minor))];
}

public static class Theory
{
    public static string Accidental(int alter) => alter switch
    {
        -2 => "𝄫", -1 => "♭", 1 => "♯", 2 => "𝄪", _ => "",
    };

    public static string Name(this Clef c) => c switch
    {
        Clef.Treble => "Treble", Clef.Treble8vb => "Treble 8vb", Clef.Bass => "Bass",
        Clef.Alto => "Alto", _ => "Tenor",
    };

    /// Clef "Auto": by the median of the notes — below E3 the bass clef, below C4 treble 8vb (read an
    /// octave up, tenor), else treble. current: the clef shown now; switching away from it needs the
    /// median 2 semitones past the boundary (no flapping around E3 or C4).
    public static Clef ClefForRange(IReadOnlyCollection<int> midis, Clef? current = null)
    {
        if (midis.Count == 0) return current ?? Clef.Treble;
        int median = midis.Order().ElementAt(midis.Count / 2);
        int Edge(int boundary, Clef above) => current is null ? boundary : current == above ? boundary - 2 : boundary + 2;
        return median < Edge(52, Clef.Treble8vb) ? Clef.Bass : median < Edge(60, Clef.Treble) ? Clef.Treble8vb : Clef.Treble;
    }

    /// Written octave shift applied to sounding pitch (Treble 8vb: written one octave above).
    public static int OctaveShift(this Clef c) => c == Clef.Treble8vb ? 1 : 0;

    /// Diatonic step of the bottom staff line: treble E4, bass G2, alto F3, tenor D3.
    public static int BottomLineStep(this Clef c) => c switch
    {
        Clef.Treble or Clef.Treble8vb => new Pitch(2, 0, 4).Step,
        Clef.Bass => new Pitch(4, 0, 2).Step,
        Clef.Alto => new Pitch(3, 0, 3).Step,
        _ => new Pitch(1, 0, 3).Step,
    };

    // Staff positions (0 = bottom line, 1 step = half a line space) of key-signature
    // accidentals in treble clef, in the order they are added.
    static readonly int[] TrebleSharps = [8, 5, 9, 6, 3, 7, 4];
    static readonly int[] TrebleFlats = [4, 7, 3, 6, 2, 5, 1];

    // ponytail: tenor sharps use the alto offset; exact tenor sharp pattern when SCORE engraving needs it.
    public static int[] KeySignaturePositions(this Clef c, int fifths)
    {
        int offset = c switch { Clef.Bass => -2, Clef.Alto or Clef.Tenor => -1, _ => 0 };
        var src = fifths >= 0 ? TrebleSharps : TrebleFlats;
        return [.. src.Take(Math.Abs(fifths)).Select(p => p + offset)];
    }

    /// Standard guitar tuning, low E first (MIDI).
    public static readonly int[] StandardTuning = [40, 45, 50, 55, 59, 64];

    /// Most likely guitar shape for a chord (spec §22.3, labelled "likely shape": positions are
    /// ambiguous). One hand position (a 4-fret window plus open strings), frets 0-12, muted strings
    /// only below the lowest sounding one, lowest sounding note = the bass when known, every chord
    /// tone present. Cost prefers more strings, a small span, low positions and open strings.
    /// Returns frets per string (low E first), -1 = muted; all -1 when nothing fits.
    public static int[] LikelyShape(ReadOnlySpan<int> pitchClasses, int bassPc, int[]? tuning = null)
    {
        tuning ??= StandardTuning;
        int strings = tuning.Length, mask = 0;
        foreach (int pc in pitchClasses) mask |= 1 << (pc % 12);
        if (mask == 0) return [.. Enumerable.Repeat(-1, strings)];
        int[] best = [.. Enumerable.Repeat(-1, strings)], cur = new int[strings];
        double bestCost = double.MaxValue;
        for (int pos = 0; pos <= 8; pos++) Search(0, pos, false);
        return best;

        void Search(int s, int pos, bool sounding)
        {
            if (s == strings) { Score(); return; }
            if (!sounding) { cur[s] = -1; Search(s + 1, pos, false); }   // mute only below the lowest sounding string
            for (int k = -1; k < 4; k++)                                  // -1: open string, else fret pos+1+k
            {
                int fret = k < 0 ? 0 : pos + 1 + k;
                int pc = (tuning[s] + fret) % 12;
                if ((mask >> pc & 1) == 0) continue;
                if (!sounding && bassPc >= 0 && pc != bassPc) continue;  // the lowest sounding note is the bass
                cur[s] = fret;
                Search(s + 1, pos, true);
            }
        }

        void Score()
        {
            int covered = 0, count = 0, opens = 0, lo = 99, hi = 0;
            for (int s = 0; s < strings; s++)
            {
                int fret = cur[s];
                if (fret < 0) continue;
                count++;
                covered |= 1 << ((tuning[s] + fret) % 12);
                if (fret == 0) opens++; else { lo = Math.Min(lo, fret); hi = Math.Max(hi, fret); }
            }
            if (count < Math.Min(3, BitCount(mask))) return;
            int first = Array.FindIndex(cur, f => f >= 0), last = Array.FindLastIndex(cur, f => f >= 0);
            if (opens > 0 && lo != 99 && cur[first] == lo && cur[last] == lo) return;   // barre implied: no open string under it
            double cost = (lo == 99 ? 0 : hi - lo + 0.35 * (lo - 1)) - 0.6 * count - 0.15 * opens + 1.5 * BitCount(mask & ~covered);
            if (cost < bestCost) { bestCost = cost; cur.CopyTo(best, 0); }
        }

        static int BitCount(int v) => System.Numerics.BitOperations.PopCount((uint)v);
    }
}
