using System.Xml;
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
string Durs(Measure m) => string.Join(" ", m.Items.Select(i => (i.Pitch is null ? "r" : "n") + i.Duration + (i.TieStart ? "~" : "")));

// Crossing the bar line: beat 4 to beat 2 of the next bar -> tied quarters.
var s1 = Score.Build(T((1.5, 2.5, "C4")));
Check(Durs(s1.Measures[0]) == "r36 n12~" && Durs(s1.Measures[1]) == "n12 r12 r24", $"bar line tie: {Durs(s1.Measures[0])} | {Durs(s1.Measures[1])}");

// Beat 2 to beat 4 is quarter + tied quarter (beat 3 stays visible); beat 3 to the end is a half.
var s2 = Score.Build(T((0.5, 1.5, "D4")));
Check(Durs(s2.Measures[0]) == "r12 n12~ n12 r12", $"beat 2-4: {Durs(s2.Measures[0])}");
var s3 = Score.Build(T((1.0, 2.0, "E4")));
Check(Durs(s3.Measures[0]) == "r24 n24", $"half on beat 3: {Durs(s3.Measures[0])}");

// A note longer than a bar: whole + tied chain; jittered times snap to the grid.
var s4 = Score.Build(T((0.01, 3.02, "F4")));
Check(Durs(s4.Measures[0]) == "n48~" && Durs(s4.Measures[1]) == "n24 r24", $"long note: {Durs(s4.Measures[0])} | {Durs(s4.Measures[1])}");

// Eighth triplets on beat 1.
double q = 0.5 / 3;
var s5 = Score.Build(T((0, q, "G4"), (q, 2 * q, "A4"), (2 * q, 0.5, "B4")));
var m5 = s5.Measures[0].Items;
Check(Durs(s5.Measures[0]) == "n4 n4 n4 r12 r24" && m5[0].TupletStart && m5[2].TupletStop, $"triplets: {Durs(s5.Measures[0])}");

// Sixteenths and a dotted eighth stay inside the beat.
var s6 = Score.Build(T((0, 0.375, "C5"), (0.375, 0.5, "D5")));
Check(Durs(s6.Measures[0]) == "n9 n3 r12 r24", $"dotted eighth + 16th: {Durs(s6.Measures[0])}");

// Chords: harmony on the right item, chart with repeat bars, sustained melody tied at a change.
var t7 = T((0, 4.0, "E4"));
t7.Chords.Add(new TakeChord(0, 2.0, "C", "I", 0, 0, 0, 0.8f));
t7.Chords.Add(new TakeChord(2.0, 3.0, "G7/B", "V65", 7, 11, 7, 0.7f));
t7.Chords.Add(new TakeChord(3.0, 4.0, "Bbm7", "bvii7", 10, -1, 9, 0.6f));
var s7 = Score.Build(t7);
Check(s7.ChordChart() == "| C | G7/B Bbm7 |", $"chart: {s7.ChordChart()}");
Check(s7.Measures[1].Items[0].Harmony?.Symbol == "G7/B" && s7.Measures[1].Items[0].TieStop, "harmony + tie at a chord change");
Check(Score.ParseSymbol("Bbm7/F") == ('B', -1, "m7", 'F', 0), "symbol parse");

// MusicXML is well-formed and carries ties, triplets, harmony and the 8vb clef.
var t8 = T((1.5, 2.5, "C4"));
t8.Clef = Clef.Treble8vb;
t8.Chords.Add(new TakeChord(0, 2, "F#m", "vi", 6, -1, 1, 0.8f));
string xml = Score.Build(t8).MusicXml();
var doc = new XmlDocument { XmlResolver = null };
doc.Load(new XmlTextReader(new StringReader(xml)) { DtdProcessing = DtdProcessing.Ignore });
Check(doc.SelectNodes("//tie[@type='start']")!.Count == 1 && doc.SelectNodes("//tied")!.Count == 2, "musicxml ties");
Check(doc.SelectSingleNode("//harmony/kind")!.InnerText == "minor" && doc.SelectSingleNode("//root-alter")!.InnerText == "1", "musicxml harmony");
Check(doc.SelectSingleNode("//clef-octave-change")!.InnerText == "-1", "musicxml 8vb clef");
Check(Score.Build(T((0, q, "G4"), (q, 2 * q, "A4"), (2 * q, 0.5, "B4"))).MusicXml().Contains("<actual-notes>3</actual-notes>"), "musicxml triplet");
foreach (XmlNode m in doc.SelectNodes("//measure")!)
{
    int sum = 0;
    foreach (XmlNode d in m.SelectNodes("note/duration")!) sum += int.Parse(d.InnerText);
    Check(sum == 48, $"measure {m.Attributes!["number"]!.Value} sums to 48 ({sum})");
}

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
Check(Score.Build(take).Measures[0].Items.Sum(i => i.Duration) == 36, "3/4 measure");

Console.WriteLine(failures == 0 ? "all checks passed" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
