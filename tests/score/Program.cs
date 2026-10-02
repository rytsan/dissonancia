using System.Xml;
using System.Xml.Schema;
using Dissonancia;

// 4/4 at 120 BPM: one quarter = 0.5 s, 12 divisions.
int failures = 0;
void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); if (!ok) failures++; }

Take T(params (double s, double e, string name)[] notes)
{
    var t = new Take { Path = "/tmp/take-test.json", KeySet = true, BeatsPerBar = 4, BeatUnit = 4, Bpm = 120, Clef = Clef.Treble };
    foreach (var (s, e, n) in notes) { var p = Take.ParseName(n); t.Notes.Add(new TakeNote(s, e, p.Midi, p, 0.9f)); }
    return t;
}
Score B(Take t) => Score.Build(t, 16, true);   // the checks below exercise the finest grid
string Durs(Measure m) => string.Join(" ", m.Items.Select(i => (i.Pitch is null ? "r" : "n") + i.Duration + (i.TieStart ? "~" : "")));

// Default grid = the meter's beat unit: in 4/4 a sung eighth-ish note becomes a quarter, no
// sixteenths or triplets unless asked for.
var q0 = Score.Build(T((0.0, 0.3, "C4"), (0.3, 0.55, "D4"), (1.0, 1.8, "E4")));
Check(Durs(q0.Measures[0]) == "n12 n12 n24", $"default 4/4 grid is the quarter: {Durs(q0.Measures[0])}");
Check(Score.Build(T((0.0, 0.3, "C4")), 8).Step == 6 && Score.Build(T((0.0, 0.3, "C4")), 16).Step == 3, "smallest value sets the grid");
var t68 = T((0.0, 0.25, "C4")); t68.BeatsPerBar = 6; t68.BeatUnit = 8;
Check(Score.Build(t68).Step == 6, "default 6/8 grid is the eighth");

// Clef "Auto" by range: the male voice of the 2026-10-02 take (B2-D4, median G3) is read an octave
// up in treble 8vb, a bass line (E2-C3) gets the bass clef, a soprano line treble; hysteresis keeps
// the clef near a boundary.
List<int> M(params string[] names) => names.Select(n => Take.ParseName(n).Midi).ToList();
Check(Theory.ClefForRange(M("B2", "Bb2", "C3", "B2", "A2", "C#3", "D3", "D#3", "E3", "F3", "G3", "Gb3", "G3", "G#3", "A3", "C4", "B3", "D4", "Db4", "B3", "Bb3", "B3", "Bb3")) == Clef.Treble8vb,
      "auto clef: the B2-D4 voice take -> treble 8vb");
Check(Theory.ClefForRange(M("E2", "G2", "A2", "C3", "B2", "A2", "G2", "E2")) == Clef.Bass, "auto clef: E2-C3 -> bass");
Check(Theory.ClefForRange(M("C3", "E3", "G3", "A3", "C4", "D4", "E4", "G3", "F3")) == Clef.Treble8vb, "auto clef: tenor -> treble 8vb");
Check(Theory.ClefForRange(M("D4", "G4", "A4", "B4", "C5", "D5", "A4")) == Clef.Treble, "auto clef: soprano -> treble");
Check(Theory.ClefForRange(M("D3", "E3", "D3"), Clef.Treble8vb) == Clef.Treble8vb && Theory.ClefForRange(M("C3", "D3", "C3"), Clef.Treble8vb) == Clef.Bass,
      "auto clef: hysteresis at E3 (D3 stays treble 8vb, C3 goes to bass)");

// STUDIO S5: the bass line decides the inversion (the note held longest, at least half the chord).
System.Text.Json.Nodes.JsonObject Chord(string sym, int root, int q) => new() { ["type"] = "chord", ["start"] = 0.0, ["end"] = 2.0, ["symbol"] = sym, ["root"] = root, ["quality"] = q, ["bass"] = -1, ["inversion"] = 0 };
System.Text.Json.Nodes.JsonNode BassNote(int midi, string name, double a, double b) => new System.Text.Json.Nodes.JsonObject { ["type"] = "note", ["midi"] = midi, ["name"] = name, ["start"] = a, ["end"] = b };
var dm = Chord("Dm/A", 2, 1);
LeadSheet.ApplyBass(dm, [BassNote(38, "D2", 0.02, 0.9), BassNote(45, "A2", 1.0, 1.7), BassNote(42, "F#2", 1.75, 1.95)]);   // root on 1, fifth on 3, a passing note
Check((string?)dm["symbol"] == "Dm", $"bass: the root on the downbeat -> root position, the fifth on 3 is not an inversion ({dm["symbol"]})");
var ce = Chord("C", 0, 0);
LeadSheet.ApplyBass(ce, [BassNote(40, "E2", 0, 1.6)]);
Check((string?)ce["symbol"] == "C/E" && (int)ce["inversion"]! == 1, $"bass: E under C -> C/E ({ce["symbol"]})");
var g7 = Chord("G7", 7, 7);
LeadSheet.ApplyBass(g7, [BassNote(41, "F2", 0, 1.5)]);
Check((string?)g7["symbol"] == "G7/F" && (int)g7["inversion"]! == 3, $"bass: F under G7 -> third inversion ({g7["symbol"]})");
var am = Chord("Am", 9, 1);
LeadSheet.ApplyBass(am, [BassNote(43, "G2", 0, 0.6)]);   // held less than half
LeadSheet.ApplyBass(am, [BassNote(42, "F#2", 0, 1.8)]);  // not a chord tone: left to the review
Check((string?)am["symbol"] == "Am", "bass: short or non-chord bass leaves the chord");

// STUDIO S2 edit list: trim, cut and clip gain on the edited timeline (frames).
var el = new EditList();
el.Trim(1000, 100, 900);                       // keep 100-900 of a 1000-frame take
Check(el.Length(1000) == 800 && el.Segments[0].A == 100, "edit: trim");
el.Cut(1000, 200, 300);                        // edited 200-300 = source 300-400 removed
Check(el.Length(1000) == 700 && el.Segments.Count == 2 && el.Segments[0].B == 300 && el.Segments[1].A == 400, $"edit: cut {string.Join(",", el.Segments)}");
el.Gain(1000, 150, 250, -6);                   // across the join: three pieces get -6 dB where selected
Check(el.Segments.Count == 4 && el.Segments.Sum(s => (long)(s.B - s.A)) == 700 && el.Segments.Count(s => s.GainDb == -6) == 2, $"edit: gain across a join {string.Join(",", el.Segments)}");
Check(!el.KeepsGrid && new EditList().KeepsGrid, "edit: a cut loses the metronome grid, no edit keeps it");
var key = el.Key;
Check(System.Text.Json.JsonSerializer.Deserialize<EditList>(key)!.Key == key, "edit: the list round-trips as JSON (the cache key)");

// Crossing the bar line: beat 4 to beat 2 of the next bar -> tied quarters.
var s1 = B(T((1.5, 2.5, "C4")));
Check(Durs(s1.Measures[0]) == "r36 n12~" && Durs(s1.Measures[1]) == "n12 r12 r24", $"bar line tie: {Durs(s1.Measures[0])} | {Durs(s1.Measures[1])}");

// Beat 2 to beat 4 is quarter + tied quarter (beat 3 stays visible); beat 3 to the end is a half.
var s2 = B(T((0.5, 1.5, "D4")));
Check(Durs(s2.Measures[0]) == "r12 n12~ n12 r12", $"beat 2-4: {Durs(s2.Measures[0])}");
var s3 = B(T((1.0, 2.0, "E4")));
Check(Durs(s3.Measures[0]) == "r24 n24", $"half on beat 3: {Durs(s3.Measures[0])}");

// A note longer than a bar: whole + tied chain; jittered times snap to the grid.
var s4 = B(T((0.01, 3.02, "F4")));
Check(Durs(s4.Measures[0]) == "n48~" && Durs(s4.Measures[1]) == "n24 r24", $"long note: {Durs(s4.Measures[0])} | {Durs(s4.Measures[1])}");

// Eighth triplets on beat 1.
double q = 0.5 / 3;
var s5 = B(T((0, q, "G4"), (q, 2 * q, "A4"), (2 * q, 0.5, "B4")));
var m5 = s5.Measures[0].Items;
Check(Durs(s5.Measures[0]) == "n4 n4 n4 r12 r24" && m5[0].TupletStart && m5[2].TupletStop, $"triplets: {Durs(s5.Measures[0])}");

// Sixteenths and a dotted eighth stay inside the beat.
var s6 = B(T((0, 0.375, "C5"), (0.375, 0.5, "D5")));
Check(Durs(s6.Measures[0]) == "n9 n3 r12 r24", $"dotted eighth + 16th: {Durs(s6.Measures[0])}");

// Chords: harmony on the right item, chart with repeat bars, sustained melody tied at a change.
var t7 = T((0, 4.0, "E4"));
t7.Chords.Add(new TakeChord(0, 2.0, "C", "I", 0, 0, 0, 0.8f));
t7.Chords.Add(new TakeChord(2.0, 3.0, "G7/B", "V65", 7, 11, 7, 0.7f));
t7.Chords.Add(new TakeChord(3.0, 4.0, "Bbm7", "bvii7", 10, -1, 9, 0.6f));
var s7 = B(t7);
Check(s7.ChordChart() == "| C | G7/B Bbm7 |", $"chart: {s7.ChordChart()}");
Check(s7.Measures[1].Items[0].Harmony?.Symbol == "G7/B" && s7.Measures[1].Items[0].TieStop, "harmony + tie at a chord change");
Check(Score.ParseSymbol("Bbm7/F") == ('B', -1, "m7", 'F', 0), "symbol parse");

// MusicXML is well-formed and carries ties, triplets, harmony and the 8vb clef.
var t8 = T((1.5, 2.5, "C4"));
t8.Clef = Clef.Treble8vb;
t8.Chords.Add(new TakeChord(0, 2, "F#m", "vi", 6, -1, 1, 0.8f));
string xml = B(t8).MusicXml();
var doc = new XmlDocument { XmlResolver = null };
doc.Load(new XmlTextReader(new StringReader(xml)) { DtdProcessing = DtdProcessing.Ignore });
Check(doc.SelectNodes("//tie[@type='start']")!.Count == 1 && doc.SelectNodes("//tied")!.Count == 2, "musicxml ties");
Check(doc.SelectSingleNode("//harmony/kind")!.InnerText == "minor" && doc.SelectSingleNode("//root-alter")!.InnerText == "1", "musicxml harmony");
Check(doc.SelectSingleNode("//clef-octave-change")!.InnerText == "-1", "musicxml 8vb clef");
Check(B(T((0, q, "G4"), (q, 2 * q, "A4"), (2 * q, 0.5, "B4"))).MusicXml().Contains("<actual-notes>3</actual-notes>"), "musicxml triplet");
foreach (XmlNode m in doc.SelectNodes("//measure")!)
{
    int sum = 0;
    foreach (XmlNode d in m.SelectNodes("note/duration")!) sum += int.Parse(d.InnerText);
    Check(sum == 48, $"measure {m.Attributes!["number"]!.Value} sums to 48 ({sum})");
}

// Accidentals: printed against the key and earlier accidentals in the bar, not repeated on ties.
var t9 = T((0, 0.5, "Db5"), (0.5, 1.0, "C5"), (1.0, 1.5, "Db5"), (1.5, 2.5, "F#4"));   // C major
var acc = new XmlDocument { XmlResolver = null };
acc.Load(new XmlTextReader(new StringReader(B(t9).MusicXml())) { DtdProcessing = DtdProcessing.Ignore });
string Accs(XmlDocument d) => string.Join(" ", d.SelectNodes("//note[pitch]")!.Cast<XmlNode>().Select(n => n.SelectSingleNode("accidental")?.InnerText ?? "-"));
Check(Accs(acc) == "flat - - sharp -", $"accidentals C major: {Accs(acc)}");
var t10 = T((0, 0.5, "F4"), (0.5, 1.0, "F#4"));
t10.KeyFifths = 1;   // G major: F needs a natural, F# none
var acc2 = new XmlDocument { XmlResolver = null };
acc2.Load(new XmlTextReader(new StringReader(B(t10).MusicXml())) { DtdProcessing = DtdProcessing.Ignore });
Check(Accs(acc2) == "natural sharp", $"accidentals G major: {Accs(acc2)}");

// MIDI: header, three tracks, tempo 500000 us/quarter.
byte[] mid = s7.Midi();
int tracks = 0;
for (int i = 0; i + 4 <= mid.Length; i++) if (mid[i] == 'M' && mid[i + 1] == 'T' && mid[i + 2] == 'r' && mid[i + 3] == 'k') tracks++;
Check(mid[..4].SequenceEqual("MThd"u8.ToArray()) && tracks == 3, $"midi: {tracks} tracks");
Check(mid.AsSpan().IndexOf(new byte[] { 0xFF, 0x51, 3, 0x07, 0xA1, 0x20 }) > 0, "midi tempo");

// Sidecar in the core's format loads (8vb: written name -> sounding pitch).
string path = Path.Combine(Path.GetTempPath(), "take-check.json");
File.WriteAllText(path, """
{"format": "dissonancia-take/1",
 "session": {"mode": 0, "quality": 1, "referenceA4": 440.000, "keySet": true, "keyFifths": -3, "keyMode": 0, "clef": 1, "meter": [3, 4], "bpm": 90.000, "countInBars": 1},
 "eventLogComplete": true,
 "events": [
    {"type": "note", "seq": 3, "start": 0.0100, "end": 0.6500, "midi": 55, "name": "G4", "avgHz": 196.0, "medianHz": 196.0, "avgCents": 1.0, "confidence": 0.900, "chromatic": false, "vibrato": false},
    {"type": "chord", "seq": 4, "start": 0.0, "end": 2.0, "symbol": "Eb", "roman": "I", "diatonicStatus": 0, "root": 3, "bass": 3, "inversion": 0, "quality": 0, "confidence": 0.800, "incomplete": false, "bassSettled": true},
    {"type": "cadence", "seq": 5, "time": 2.0, "cadence": 1, "from": "V7", "to": "I", "confidence": 0.700, "evidence": "5-1, root position, V7; no melody"}
 ]}
""");
var take = Take.Load(path);
Check(take.Notes[0].Sounding == new Pitch(4, 0, 3) && take.BeatsPerBar == 3 && take.Chords[0].Roman == "I" && take.Cadences.Count == 1, "sidecar load");
Check(B(take).Measures[0].Items.Sum(i => i.Duration) == 36, "3/4 measure");

// Likely guitar shapes: the common open and barre chords.
string Shape(int[] pcs, int bass) => string.Join(" ", Theory.LikelyShape(pcs, bass).Select(f => f < 0 ? "x" : f.ToString()));
Check(Shape([0, 4, 7], 0) == "x 3 2 0 1 0", $"shape C: {Shape([0, 4, 7], 0)}");
Check(Shape([7, 11, 2], 7) == "3 2 0 0 0 3", $"shape G: {Shape([7, 11, 2], 7)}");
Check(Shape([4, 7, 11], 4) == "0 2 2 0 0 0", $"shape Em: {Shape([4, 7, 11], 4)}");
Check(Shape([2, 6, 9], 2) == "x x 0 2 3 2", $"shape D: {Shape([2, 6, 9], 2)}");
Check(Shape([5, 9, 0], 5) == "1 3 3 2 1 1", $"shape F barre: {Shape([5, 9, 0], 5)}");
Check(Shape([9, 0, 4, 7], 7) == "3 0 2 0 1 0", $"shape Am7/G: {Shape([9, 0, 4, 7], 7)}");
Check(Shape([2, 6, 9], 6).StartsWith("2 "), $"shape D/F#: {Shape([2, 6, 9], 6)}");

var watch = System.Diagnostics.Stopwatch.StartNew();
for (int i = 0; i < 20; i++) Theory.LikelyShape([i % 12, (i + 4) % 12, (i + 7) % 12, (i + 10) % 12], i % 12);
Console.WriteLine($"[measure] LikelyShape (4-note chord): {watch.Elapsed.TotalMilliseconds / 20:0.000} ms per chord change");

// Rack presets: a hand-edited file is cleaned on load (duplicates, inapplicable modules, missing
// pinned modules, heights out of range).
string config = Path.Combine(Path.GetTempPath(), "dz-rack-check");
Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
Directory.CreateDirectory(Path.Combine(config, "Dissonancia"));
File.WriteAllText(Path.Combine(config, "Dissonancia", "rack-VoiceMono.json"), """
[{"module": "Scope", "width": "half", "heightU": 9}, {"module": "Scope", "width": "full", "heightU": 2},
 {"module": "Fretboard", "width": "full", "heightU": 2}, {"module": "Bogus", "width": "full", "heightU": 1},
 {"module": "Status", "width": "half", "heightU": 1}]
""");
var rack = RackCatalog.Load(AppMode.VoiceMono);
Check(string.Join(",", rack.Select(e => $"{e.Module}{(e.Half ? "/2" : "")}:{e.HeightU}")) == "Scope/2:4,Status:1,Transport:1", "rack preset cleaned: " + string.Join(",", rack.Select(e => $"{e.Module}{(e.Half ? "/2" : "")}:{e.HeightU}")));
RackCatalog.Save(AppMode.VoiceMono, RackCatalog.Default(AppMode.VoiceMono));
Check(RackCatalog.Load(AppMode.VoiceMono).SequenceEqual(RackCatalog.Default(AppMode.VoiceMono)), "rack preset round trip");
Check(RackCatalog.Default(AppMode.GuitarChords).Any(e => e.Module == ModuleKind.Fretboard) && !RackCatalog.Default(AppMode.VoiceMono).Any(e => e.Module == ModuleKind.Timeline), "per-mode defaults");
Directory.Delete(config, true);

// Every MusicXML we write validates against the MusicXML 4.0 XSD (downloaded once, cached next to the binary).
var samples = new[] { s1, s2, s4, s5, s6, s7, B(t8), B(take) }.Select(x => x.MusicXml()).ToArray();
if (Schema() is { } schema)
    for (int i = 0; i < samples.Length; i++)
    {
        var errors = new List<string>();
        var settings = new XmlReaderSettings { ValidationType = ValidationType.Schema, Schemas = schema, DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        settings.ValidationEventHandler += (_, e) => errors.Add($"{e.Severity}: {e.Message} (line {e.Exception?.LineNumber})");
        using (var r = XmlReader.Create(new StringReader(samples[i]), settings)) while (r.Read()) { }
        Check(errors.Count == 0, $"XSD sample {i + 1}{(errors.Count > 0 ? ": " + errors[0] : "")}");
    }
else Console.WriteLine("skip XSD (schema not reachable)");

// Verovio imports it (the engraving the SCORE tab shows).
if (Verovio.TryCreate(out var vrvError) is { } vrv)
    using (vrv)
        for (int i = 0; i < samples.Length; i++)
        {
            var pages = vrv.Render(samples[i], 1000);
            string log = vrv.Log;
            Check(pages.Length >= 1 && pages[0].Contains("<svg") && !log.Contains("Error"), $"Verovio {vrv.Version} sample {i + 1}: {pages.Length} page(s){(log.Length > 0 ? " log: " + log.Trim() : "")}");
        }
else Console.WriteLine("skip Verovio: " + vrvError);

Console.WriteLine(failures == 0 ? "all checks passed" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;

static XmlSchemaSet? Schema()
{
    string dir = Path.Combine(AppContext.BaseDirectory, "xsd");
    try
    {
        Directory.CreateDirectory(dir);
        using var http = new HttpClient();
        foreach (var f in new[] { "musicxml.xsd", "xlink.xsd", "xml.xsd" })
        {
            string path = Path.Combine(dir, f);
            if (File.Exists(path)) continue;
            string text = http.GetStringAsync("https://raw.githubusercontent.com/w3c/musicxml/v4.0/schema/" + f).Result;
            File.WriteAllText(path, text.Replace("http://www.musicxml.org/xsd/xml.xsd", "xml.xsd").Replace("http://www.musicxml.org/xsd/xlink.xsd", "xlink.xsd"));
        }
        var set = new XmlSchemaSet { XmlResolver = new XmlUrlResolver() };
        set.Add(null, Path.Combine(dir, "musicxml.xsd"));
        set.Compile();
        return set;
    }
    catch (Exception e) { Console.WriteLine("schema: " + e.Message); return null; }
}
