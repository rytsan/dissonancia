using System.Text;
using System.Text.Json;
using System.Xml;

namespace Dissonancia;

// LIVE -> SCORE (spec §19, §20.4, M6): the REC take's JSON sidecar is the event log. Bar lines
// come from the session meter and BPM (event times are already seconds from the first downbeat,
// round-trip compensated), never guessed from the audio. Two timelines are kept: every score
// item carries its observed start/end next to the quantized position.

public sealed record TakeNote(double Start, double End, int Midi, Pitch Sounding, float Confidence);
public sealed record TakeChord(double Start, double End, string Symbol, string Roman, int Root, int Bass, int Quality, float Confidence);
public sealed record TakeCadence(double Time, int Type, string From, string To, float Confidence, string Evidence);

/// One REC take, read from the core's sidecar (format dissonancia-take/1).
public sealed class Take
{
    public string Path = "";
    public bool KeySet, Minor, EventLogComplete;
    public int KeyFifths, BeatsPerBar = 4, BeatUnit = 4, Mode;
    public Clef Clef;
    public double Bpm = 120;
    public List<TakeNote> Notes = [];
    public List<TakeChord> Chords = [];
    public List<TakeCadence> Cadences = [];

    public static string Folder
    {
        get
        {
            if (Environment.GetEnvironmentVariable("DISSONANCIA_TAKES") is { Length: > 0 } dir) return dir;   // tools and tests
            var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);   // "" when the folder does not exist
            return System.IO.Path.Combine(music.Length > 0 ? music : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Dissonancia");
        }
    }

    /// Newest take sidecar, or null.
    public static string? Latest() => Directory.Exists(Folder)
        ? Directory.GetFiles(Folder, "take-*.json").Where(p => !p.EndsWith(".score.json")).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
        : null;

    public static Take Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var s = root.GetProperty("session");
        var meter = s.GetProperty("meter");
        var t = new Take
        {
            Path = path,
            Mode = s.GetProperty("mode").GetInt32(),
            KeySet = s.GetProperty("keySet").GetBoolean(),
            KeyFifths = s.GetProperty("keyFifths").GetInt32(),
            Minor = s.GetProperty("keyMode").GetInt32() != 0,
            Clef = (Clef)s.GetProperty("clef").GetInt32(),
            BeatsPerBar = meter[0].GetInt32(),
            BeatUnit = meter[1].GetInt32(),
            Bpm = s.GetProperty("bpm").GetDouble(),
            EventLogComplete = root.TryGetProperty("eventLogComplete", out var c) && c.GetBoolean(),
        };
        foreach (var e in root.GetProperty("events").EnumerateArray())
        {
            switch (e.GetProperty("type").GetString())
            {
                case "note":
                    var written = ParseName(e.GetProperty("name").GetString() ?? "");
                    t.Notes.Add(new TakeNote(e.GetProperty("start").GetDouble(), e.GetProperty("end").GetDouble(), e.GetProperty("midi").GetInt32(),
                        written.WithOctaveShift(-t.Clef.OctaveShift()), e.GetProperty("confidence").GetSingle()));
                    break;
                case "chord":
                    t.Chords.Add(new TakeChord(e.GetProperty("start").GetDouble(), e.GetProperty("end").GetDouble(), e.GetProperty("symbol").GetString() ?? "",
                        e.TryGetProperty("roman", out var r) ? r.GetString() ?? "" : "", e.GetProperty("root").GetInt32(), e.GetProperty("bass").GetInt32(),
                        e.GetProperty("quality").GetInt32(), e.GetProperty("confidence").GetSingle()));
                    break;
                case "cadence":
                    t.Cadences.Add(new TakeCadence(e.GetProperty("time").GetDouble(), e.GetProperty("cadence").GetInt32(), e.GetProperty("from").GetString() ?? "",
                        e.GetProperty("to").GetString() ?? "", e.GetProperty("confidence").GetSingle(), e.GetProperty("evidence").GetString() ?? ""));
                    break;
            }
        }
        return t;
    }

    /// Core ASCII name ("C#4", "Db4", "Fx5", "Cbb3") -> pitch.
    public static Pitch ParseName(string n)
    {
        int letter = "CDEFGAB".IndexOf(n[0]), i = 1, alter = 0;
        for (; i < n.Length && n[i] is 'b' or '#' or 'x'; i++) alter += n[i] switch { 'b' => -1, '#' => 1, _ => 2 };
        return new Pitch(letter, alter, int.Parse(n[i..]));
    }
}

public sealed record ScoreItem(int Offset, int Duration, Pitch? Pitch, double ObservedStart, double ObservedEnd, float Confidence)
{
    public bool TieStart, TieStop, Tuplet, TupletStart, TupletStop, WholeRest;
    public TakeChord? Harmony;   // chord symbol starting on this item
}

public sealed class Measure
{
    public int Number;
    public List<ScoreItem> Items = [];
}

public sealed class Score
{
    public const int Divisions = 12;   // per quarter: sixteenths (3) and eighth triplets (4)
    static readonly int[] Values = [48, 36, 24, 18, 12, 9, 8, 6, 4, 3];

    public required Take Take;
    public int MeasureDivs, BeatDivs;
    public bool Compound;
    public List<Measure> Measures = [];
    public List<(int Start, int End, TakeNote Note)> QuantizedNotes = [];
    public List<(int Start, int End, TakeChord Chord)> QuantizedChords = [];
    readonly HashSet<int> _triplets = [];   // quarters snapped to the triplet grid
    public int Step;                        // grid of the smallest notated value, in divisions
    bool _allowTriplets;

    public double SecondsPerQuarter => 60 / Take.Bpm * Take.BeatUnit / 4.0;

    /// smallest: shortest notated value as a note-value denominator (4 = quarter, 8, 16); 0 = the
    /// meter's beat unit (quarter in 4/4, eighth in 6/8). triplets: eighth triplets where they fit.
    public static Score Build(Take t, int smallest = 0, bool triplets = false)
    {
        var s = new Score { Take = t };
        s.Step = Divisions * 4 / Math.Clamp(smallest > 0 ? smallest : t.BeatUnit, 4, 16);
        s.Compound = t.BeatUnit == 8 && t.BeatsPerBar % 3 == 0 && t.BeatsPerBar > 3;
        s._allowTriplets = triplets && !s.Compound;
        s.MeasureDivs = t.BeatsPerBar * Divisions * 4 / t.BeatUnit;
        s.BeatDivs = s.Compound ? Divisions * 3 / 2 : Divisions * 4 / t.BeatUnit;
        s.Quantize();
        s.Lay();
        return s;
    }

    double Pos(double seconds) => Math.Max(0, seconds / SecondsPerQuarter * Divisions);
    static double Err(double p, int step) => Math.Abs(p - Math.Round(p / step) * step);

    int Snap(double p)
    {
        int step = _triplets.Contains((int)Math.Floor(p / Divisions)) ? 4 : Step;
        return (int)Math.Round(p / step) * step;
    }

    void Quantize()
    {
        // Grid per quarter: eighth triplets only where the note boundaries fit them clearly better.
        var bounds = Take.Notes.SelectMany(n => new[] { Pos(n.Start), Pos(n.End) }).ToList();
        if (_allowTriplets)
            foreach (var g in bounds.GroupBy(p => (int)Math.Floor(p / Divisions)))
                if (g.Count() >= 2 && g.Sum(p => Err(p, 4)) < 0.5 * g.Sum(p => Err(p, Step))) _triplets.Add(g.Key);

        foreach (var n in Take.Notes.OrderBy(n => n.Start))
        {
            int a = Snap(Pos(n.Start)), b = Snap(Pos(n.End));
            if (b <= a) b = a + (_triplets.Contains(a / Divisions) ? 4 : Step);   // never shorter than one grid step
            // Monophonic line: a new note cuts the previous one; one that snaps onto it replaces it.
            while (QuantizedNotes.Count > 0 && QuantizedNotes[^1].End > a)
            {
                var last = QuantizedNotes[^1];
                QuantizedNotes.RemoveAt(QuantizedNotes.Count - 1);
                if (last.Start < a) { QuantizedNotes.Add((last.Start, a, last.Note)); break; }
            }
            QuantizedNotes.Add((a, b, n));
        }

        // Chords snap to the note grid, never finer than eighths; one that snaps onto the previous
        // replaces it (it was too short to notate).
        int cs = Math.Max(6, Step);
        foreach (var c in Take.Chords.OrderBy(c => c.Start))
        {
            int a = (int)Math.Round(Pos(c.Start) / cs) * cs, b = Math.Max(a + cs, (int)Math.Round(Pos(c.End) / cs) * cs);
            while (QuantizedChords.Count > 0 && QuantizedChords[^1].Start >= a) QuantizedChords.RemoveAt(QuantizedChords.Count - 1);
            if (QuantizedChords.Count > 0 && QuantizedChords[^1].End > a) QuantizedChords[^1] = QuantizedChords[^1] with { End = a };
            QuantizedChords.Add((a, b, c));
        }
    }

    void Lay()
    {
        int end = Math.Max(QuantizedNotes.Count > 0 ? QuantizedNotes[^1].End : 0, QuantizedChords.Count > 0 ? QuantizedChords[^1].End : 0);
        int bars = Math.Max(1, (end + MeasureDivs - 1) / MeasureDivs);
        for (int i = 0; i < bars; i++) Measures.Add(new Measure { Number = i + 1 });

        // Contiguous timeline: notes and the rests between them.
        var spans = new List<(int A, int B, TakeNote? N)>();
        int pos = 0;
        foreach (var (a, b, n) in QuantizedNotes)
        {
            if (a > pos) spans.Add((pos, a, null));
            spans.Add((a, b, n));
            pos = b;
        }
        if (pos < bars * MeasureDivs) spans.Add((pos, bars * MeasureDivs, null));

        // Split points: bar lines and chord changes (a note held through a change is tied there).
        var cuts = new SortedSet<int>(Enumerable.Range(0, bars + 1).Select(i => i * MeasureDivs));
        foreach (var c in QuantizedChords) cuts.Add(c.Start);

        foreach (var (a, b, n) in spans)
        {
            var pieces = new List<(int Bar, ScoreItem Item)>();
            int p = a;
            while (p < b)
            {
                int stop = Math.Min(b, cuts.GetViewBetween(p + 1, b).DefaultIfEmpty(b).First());
                int bar = p / MeasureDivs;
                if (n is null && p % MeasureDivs == 0 && stop - p == MeasureDivs)
                {
                    pieces.Add((bar, new ScoreItem(0, MeasureDivs, null, 0, 0, 0) { WholeRest = true }));
                    p = stop;
                    continue;
                }
                while (p < stop)
                {
                    int o = p - bar * MeasureDivs, v = Pick(p, o, stop - p);
                    bool tuplet = v is 4 or 8;
                    pieces.Add((bar, new ScoreItem(o, v, n?.Sounding, n?.Start ?? 0, n?.End ?? 0, n?.Confidence ?? 0)
                    {
                        Tuplet = tuplet, TupletStart = tuplet && o % Divisions == 0, TupletStop = tuplet && (o + v) % Divisions == 0,
                    }));
                    p += v;
                }
            }
            if (n is not null)
                for (int i = 0; i < pieces.Count; i++) { pieces[i].Item.TieStart = i < pieces.Count - 1; pieces[i].Item.TieStop = i > 0; }
            foreach (var (bar, item) in pieces) Measures[bar].Items.Add(item);
        }

        foreach (var m in Measures) m.Items.Sort((x, y) => x.Offset.CompareTo(y.Offset));
        foreach (var (a, _, c) in QuantizedChords)
        {
            var m = Measures[a / MeasureDivs];
            var item = m.Items.FirstOrDefault(i => i.Offset == a % MeasureDivs);
            if (item is not null) item.Harmony = c;
        }
    }

    /// Largest notated value that keeps the beat structure visible (§19): sub-beat values stay
    /// inside their beat, longer values start on a beat, a half in 4/4 only on beat 1 or 3,
    /// dotted half / whole only from the downbeat; compound meters group in dotted beats.
    int Pick(int abs, int o, int remaining)
    {
        bool tripletQuarter = !Compound && _triplets.Contains(abs / Divisions);
        foreach (int v in Values)
        {
            if (v > remaining || v > MeasureDivs) continue;
            bool tupletValue = v is 4 or 8;
            if (tupletValue != tripletQuarter && !(tripletQuarter && v >= Divisions)) continue;   // triplet values only in triplet quarters
            if (tupletValue && o % Divisions + v > Divisions) continue;
            int end = abs + v;
            if (!tupletValue && end % Divisions != 0 && _triplets.Contains(end / Divisions)) continue;   // would end off the triplet grid
            if (v < BeatDivs) { if (o % BeatDivs + v > BeatDivs) continue; }
            else
            {
                if (o % BeatDivs != 0) continue;
                if (Compound && v % BeatDivs != 0) continue;
                if ((v == MeasureDivs || v == 36) && o != 0) continue;
                int half = MeasureDivs / 2;   // 4/4, 12/8: never hide the middle of the bar
                if (Take.BeatsPerBar % 4 == 0 && o != 0 && o < half && o + v > half) continue;
            }
            return v;
        }
        return Math.Min(remaining, Step);   // unreachable with snapped input: every piece is a grid multiple
    }

    // ---------------------------------------------------------------- chord chart (text)

    /// "| C | G | Am F | % |", four bars per line.
    public string ChordChart()
    {
        if (QuantizedChords.Count == 0) return "";
        var bars = Measures.Select(m => string.Join(" ", QuantizedChords.Where(c => c.Start / MeasureDivs == m.Number - 1).Select(c => c.Chord.Symbol))).ToList();
        for (int i = 0; i < bars.Count; i++) if (bars[i].Length == 0) bars[i] = "%";
        return string.Join("\n", bars.Chunk(4).Select(c => "| " + string.Join(" | ", c) + " |"));
    }

    public string RomanLine() =>
        string.Join("  ", QuantizedChords.Select(c => c.Chord.Roman).Where(r => r.Length > 0));

    // ---------------------------------------------------------------- MusicXML 4.0

    static readonly string[] Kinds =
    [
        "major", "minor", "diminished", "augmented", "suspended-second", "suspended-fourth", "power",
        "dominant", "major-seventh", "minor-seventh", "half-diminished", "diminished-seventh", "major-sixth", "minor-sixth", "major",
    ];

    public string MusicXml()
    {
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(new StringWriter(sb), new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 }))
        {
            w.WriteStartDocument(false);
            w.WriteDocType("score-partwise", "-//Recordare//DTD MusicXML 4.0 Partwise//EN", "http://www.musicxml.org/dtds/partwise.dtd", null);
            w.WriteStartElement("score-partwise");
            w.WriteAttributeString("version", "4.0");
            w.WriteStartElement("work");
            w.WriteElementString("work-title", System.IO.Path.GetFileNameWithoutExtension(Take.Path));
            w.WriteEndElement();
            w.WriteStartElement("identification");
            w.WriteStartElement("encoding");
            w.WriteElementString("software", "Dissonancia");
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteStartElement("part-list");
            w.WriteStartElement("score-part");
            w.WriteAttributeString("id", "P1");
            w.WriteElementString("part-name", Take.Mode switch { 0 => "Voice", 1 => "Melody", 2 => "Guitar", 3 => "Piano", _ => "Chords" });
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteStartElement("part");
            w.WriteAttributeString("id", "P1");
            foreach (var m in Measures)
            {
                var shown = KeyAlters();   // accidentals in force in this bar, per letter and octave
                w.WriteStartElement("measure");
                w.WriteAttributeString("number", m.Number.ToString());
                if (m.Number == 1) WriteAttributes(w);
                foreach (var item in m.Items)
                {
                    if (item.Harmony is not null) WriteHarmony(w, item.Harmony);
                    WriteNote(w, item, shown);
                }
                w.WriteEndElement();
            }
            w.WriteEndElement();
            w.WriteEndElement();
        }
        return sb.ToString().Replace("encoding=\"utf-16\"", "encoding=\"UTF-8\"");
    }

    void WriteAttributes(XmlWriter w)
    {
        w.WriteStartElement("attributes");
        w.WriteElementString("divisions", Divisions.ToString());
        w.WriteStartElement("key");
        w.WriteElementString("fifths", (Take.KeySet ? Take.KeyFifths : 0).ToString());   // no key: no signature, no mode
        if (Take.KeySet) w.WriteElementString("mode", Take.Minor ? "minor" : "major");
        w.WriteEndElement();
        w.WriteStartElement("time");
        w.WriteElementString("beats", Take.BeatsPerBar.ToString());
        w.WriteElementString("beat-type", Take.BeatUnit.ToString());
        w.WriteEndElement();
        w.WriteStartElement("clef");
        var (sign, line) = Take.Clef switch { Clef.Bass => ("F", 4), Clef.Alto => ("C", 3), Clef.Tenor => ("C", 4), _ => ("G", 2) };
        w.WriteElementString("sign", sign);
        w.WriteElementString("line", line.ToString());
        if (Take.Clef == Clef.Treble8vb) w.WriteElementString("clef-octave-change", "-1");
        w.WriteEndElement();
        w.WriteEndElement();

        w.WriteStartElement("direction");
        w.WriteAttributeString("placement", "above");
        w.WriteStartElement("direction-type");
        w.WriteStartElement("metronome");
        w.WriteElementString("beat-unit", Take.BeatUnit switch { 2 => "half", 8 => "eighth", 16 => "16th", _ => "quarter" });
        w.WriteElementString("per-minute", Take.Bpm.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteStartElement("sound");
        w.WriteAttributeString("tempo", (Take.Bpm * 4 / Take.BeatUnit).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
        w.WriteEndElement();
        w.WriteEndElement();
    }

    /// Chord symbol text -> root step/alter, suffix, bass ("Bbm7/F" -> B -1, "m7", F 0).
    public static (char Step, int Alter, string Suffix, char BassStep, int BassAlter) ParseSymbol(string symbol)
    {
        static (char, int, int) Note(string s, int i)
        {
            char step = s[i++];
            int alter = 0;
            for (; i < s.Length && s[i] is 'b' or '#' or 'x'; i++) alter += s[i] switch { 'b' => -1, '#' => 1, _ => 2 };
            return (step, alter, i);
        }
        int slash = symbol.IndexOf('/');
        string head = slash < 0 ? symbol : symbol[..slash];
        var (step, alter, i) = Note(head, 0);
        char bassStep = '\0';
        int bassAlter = 0;
        if (slash >= 0) (bassStep, bassAlter, _) = Note(symbol, slash + 1);
        return (step, alter, head[i..], bassStep, bassAlter);
    }

    static void WriteHarmony(XmlWriter w, TakeChord c)
    {
        var (step, alter, suffix, bassStep, bassAlter) = ParseSymbol(c.Symbol);
        w.WriteStartElement("harmony");
        w.WriteStartElement("root");
        w.WriteElementString("root-step", step.ToString());
        if (alter != 0) w.WriteElementString("root-alter", alter.ToString());
        w.WriteEndElement();
        w.WriteStartElement("kind");
        w.WriteAttributeString("text", suffix);
        w.WriteString(c.Quality >= 0 && c.Quality < Kinds.Length ? Kinds[c.Quality] : "other");
        w.WriteEndElement();
        if (bassStep != '\0')
        {
            w.WriteStartElement("bass");
            w.WriteElementString("bass-step", bassStep.ToString());
            if (bassAlter != 0) w.WriteElementString("bass-alter", bassAlter.ToString());
            w.WriteEndElement();
        }
        if (c.Quality == 14)   // add9
        {
            w.WriteStartElement("degree");
            w.WriteElementString("degree-value", "9");
            w.WriteElementString("degree-alter", "0");
            w.WriteElementString("degree-type", "add");
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    static (string Type, int Dots) TypeOf(int d) => d switch
    {
        48 => ("whole", 0), 36 => ("half", 1), 24 => ("half", 0), 18 => ("quarter", 1), 12 => ("quarter", 0), 9 => ("eighth", 1),
        8 => ("quarter", 0), 6 => ("eighth", 0), 4 => ("eighth", 0), _ => ("16th", 0),
    };

    /// Alteration the key signature gives each letter: sharps F C G D A E B, flats B E A D G C F.
    Dictionary<(int Letter, int Octave), int> KeyAlters() => new();

    int KeyAlter(int letter)
    {
        int[] sharps = [3, 0, 4, 1, 5, 2, 6], flats = [6, 2, 5, 1, 4, 0, 3];
        int f = Take.KeySet ? Take.KeyFifths : 0;
        return f > 0 && sharps.Take(f).Contains(letter) ? 1 : f < 0 && flats.Take(-f).Contains(letter) ? -1 : 0;
    }

    /// Printed accidental (§22.2): when the note's alteration differs from what is in force in the
    /// bar (key signature, or an earlier accidental on the same letter and octave). A tied
    /// continuation never repeats it.
    string? Accidental(ScoreItem item, Dictionary<(int, int), int> shown)
    {
        if (item.Pitch is not { } p) return null;
        var key = (p.Letter, p.Octave);
        int inForce = shown.TryGetValue(key, out var a) ? a : KeyAlter(p.Letter);
        shown[key] = p.Alter;
        if (item.TieStop || p.Alter == inForce) return null;
        return p.Alter switch { -2 => "flat-flat", -1 => "flat", 1 => "sharp", 2 => "double-sharp", _ => "natural" };
    }

    void WriteNote(XmlWriter w, ScoreItem item, Dictionary<(int, int), int> shown)
    {
        w.WriteStartElement("note");
        if (item.Pitch is { } p)
        {
            w.WriteStartElement("pitch");
            w.WriteElementString("step", "CDEFGAB"[p.Letter].ToString());
            if (p.Alter != 0) w.WriteElementString("alter", p.Alter.ToString());
            w.WriteElementString("octave", p.Octave.ToString());
            w.WriteEndElement();
        }
        else
        {
            w.WriteStartElement("rest");
            if (item.WholeRest) w.WriteAttributeString("measure", "yes");
            w.WriteEndElement();
        }
        w.WriteElementString("duration", item.Duration.ToString());
        if (item.TieStop) { w.WriteStartElement("tie"); w.WriteAttributeString("type", "stop"); w.WriteEndElement(); }
        if (item.TieStart) { w.WriteStartElement("tie"); w.WriteAttributeString("type", "start"); w.WriteEndElement(); }
        if (!item.WholeRest)
        {
            var (type, dots) = TypeOf(item.Duration);
            w.WriteElementString("type", type);
            for (int i = 0; i < dots; i++) w.WriteElementString("dot", "");
        }
        if (Accidental(item, shown) is { } accidental) w.WriteElementString("accidental", accidental);
        if (item.Tuplet)
        {
            w.WriteStartElement("time-modification");
            w.WriteElementString("actual-notes", "3");
            w.WriteElementString("normal-notes", "2");
            w.WriteEndElement();
        }
        if (item.TieStart || item.TieStop || item.TupletStart || item.TupletStop)
        {
            w.WriteStartElement("notations");
            if (item.TieStop) { w.WriteStartElement("tied"); w.WriteAttributeString("type", "stop"); w.WriteEndElement(); }
            if (item.TieStart) { w.WriteStartElement("tied"); w.WriteAttributeString("type", "start"); w.WriteEndElement(); }
            if (item.TupletStart) { w.WriteStartElement("tuplet"); w.WriteAttributeString("type", "start"); w.WriteEndElement(); }
            if (item.TupletStop) { w.WriteStartElement("tuplet"); w.WriteAttributeString("type", "stop"); w.WriteEndElement(); }
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    // ---------------------------------------------------------------- MIDI type 1

    static readonly int[][] ChordIntervals =
    [
        [0, 4, 7], [0, 3, 7], [0, 3, 6], [0, 4, 8], [0, 2, 7], [0, 5, 7], [0, 7], [0, 4, 7, 10], [0, 4, 7, 11], [0, 3, 7, 10],
        [0, 3, 6, 10], [0, 3, 6, 9], [0, 4, 7, 9], [0, 3, 7, 9], [0, 4, 7, 14],
    ];

    /// Tempo/meter/key track, melody track (channel 1), chord track (channel 2, bass + close voicing).
    public byte[] Midi()
    {
        const int ppq = 480, tick = ppq / Divisions;
        var tempo = new List<(int, byte[])>
        {
            (0, Meta(0x51, [.. BitConverter.GetBytes((int)Math.Round(SecondsPerQuarter * 1e6)).Take(3).Reverse()])),
            (0, Meta(0x58, [(byte)Take.BeatsPerBar, (byte)Math.Log2(Take.BeatUnit), 24, 8])),
            (0, Meta(0x59, [(byte)(sbyte)Take.KeyFifths, (byte)(Take.Minor ? 1 : 0)])),
        };
        var melody = new List<(int, byte[])>();
        foreach (var (a, b, n) in QuantizedNotes)
        {
            melody.Add((a * tick, [0x90, (byte)n.Midi, 90]));
            melody.Add((b * tick, [0x80, (byte)n.Midi, 0]));
        }
        var chords = new List<(int, byte[])>();
        foreach (var (a, b, c) in QuantizedChords)
        {
            if (c.Quality < 0 || c.Quality >= ChordIntervals.Length) continue;
            var keys = ChordIntervals[c.Quality].Select(i => 60 + c.Root + i).Prepend(48 + (c.Bass >= 0 ? c.Bass : c.Root)).ToArray();
            foreach (int k in keys) chords.Add((a * tick, [0x91, (byte)k, 70]));
            foreach (int k in keys) chords.Add((b * tick, [0x81, (byte)k, 0]));
        }
        using var ms = new MemoryStream();
        ms.Write("MThd"u8);
        ms.Write([0, 0, 0, 6, 0, 1, 0, 3, (byte)(ppq >> 8), (byte)(ppq & 0xFF)]);
        foreach (var track in new[] { tempo, melody, chords }) WriteTrack(ms, track);
        return ms.ToArray();
    }

    static byte[] Meta(byte type, byte[] data) => [0xFF, type, (byte)data.Length, .. data];

    static void WriteTrack(Stream s, List<(int Tick, byte[] Data)> events)
    {
        using var body = new MemoryStream();
        int last = 0;
        // Note-offs sort before note-ons at the same tick (repeated notes stay separate).
        foreach (var (t, d) in events.OrderBy(e => e.Tick).ThenBy(e => (e.Data[0] & 0xF0) == 0x90 ? 1 : 0))
        {
            WriteVlq(body, t - last);
            body.Write(d);
            last = t;
        }
        WriteVlq(body, 0);
        body.Write([0xFF, 0x2F, 0]);
        s.Write("MTrk"u8);
        int n = (int)body.Length;
        s.Write([(byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n]);
        body.WriteTo(s);
    }

    static void WriteVlq(Stream s, int v)
    {
        Span<byte> buf = stackalloc byte[5];
        int i = 4;
        buf[i] = (byte)(v & 0x7F);
        while ((v >>= 7) > 0) buf[--i] = (byte)(0x80 | (v & 0x7F));
        s.Write(buf[i..]);
    }

    // ---------------------------------------------------------------- JSON (complete intermediate representation)

    public string Json() => JsonSerializer.Serialize(new
    {
        format = "dissonancia-score/1",
        take = System.IO.Path.GetFileName(Take.Path),
        eventLogComplete = Take.EventLogComplete,
        key = new { fifths = Take.KeyFifths, mode = Take.Minor ? "minor" : "major", set = Take.KeySet },
        meter = new[] { Take.BeatsPerBar, Take.BeatUnit },
        bpm = Take.Bpm,
        clef = Take.Clef.ToString(),
        divisionsPerQuarter = Divisions,
        notes = QuantizedNotes.Select(q => new
        {
            start = q.Start, end = q.End, observedStart = q.Note.Start, observedEnd = q.Note.End,
            midi = q.Note.Midi, name = q.Note.Sounding.Name, confidence = q.Note.Confidence,
        }),
        chords = QuantizedChords.Select(q => new
        {
            start = q.Start, end = q.End, observedStart = q.Chord.Start, observedEnd = q.Chord.End,
            symbol = q.Chord.Symbol, roman = q.Chord.Roman, root = q.Chord.Root, bass = q.Chord.Bass, confidence = q.Chord.Confidence,
        }),
        cadences = Take.Cadences.Select(c => new { time = c.Time, type = c.Type, from = c.From, to = c.To, confidence = c.Confidence, evidence = c.Evidence }),
        measures = Measures.Select(m => new
        {
            number = m.Number,
            items = m.Items.Select(i => new
            {
                offset = i.Offset, duration = i.Duration, pitch = i.Pitch?.Name, tieStart = i.TieStart, tieStop = i.TieStop, triplet = i.Tuplet,
                harmony = i.Harmony?.Symbol, observedStart = i.ObservedStart, observedEnd = i.ObservedEnd, confidence = i.Confidence,
            }),
        }),
    }, new JsonSerializerOptions { WriteIndented = true });

    /// Writes .musicxml, .mid, .score.json and .txt next to the take; returns the paths.
    public string[] Export()
    {
        string stem = System.IO.Path.ChangeExtension(Take.Path, null);
        var files = new[] { stem + ".musicxml", stem + ".mid", stem + ".score.json", stem + ".txt" };
        File.WriteAllText(files[0], MusicXml());
        File.WriteAllBytes(files[1], Midi());
        File.WriteAllText(files[2], Json());
        File.WriteAllText(files[3], ChordChart() + (RomanLine().Length > 0 ? "\n\n" + RomanLine() : "") + "\n");
        return files;
    }
}
