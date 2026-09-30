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
}
