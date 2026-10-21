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
Check(LeadSheet.Refigure("ii64", 1, 0) == "ii" && LeadSheet.Refigure("V7", 7, 1) == "V65" && LeadSheet.Refigure("V7/V", 7, 2) == "V43/V" && LeadSheet.Refigure("I", 0, 1) == "I6",
      $"bass: roman figures follow the bass ({LeadSheet.Refigure("ii64", 1, 0)} {LeadSheet.Refigure("V7", 7, 1)} {LeadSheet.Refigure("V7/V", 7, 2)})");
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

// STUDIO S5: the drums' beats against the metronome.
var withBeats = new System.Text.Json.Nodes.JsonObject();
LeadSheet.AddBeats(withBeats, new System.Text.Json.Nodes.JsonObject { ["tempoBpm"] = 118.0, ["beats"] = new System.Text.Json.Nodes.JsonArray(0.0, 0.5, 1.06, 1.45) }, 120);
var drift = withBeats["beatDriftMs"]!.AsArray().Select(d => (double)d!).ToArray();
Check(drift.SequenceEqual([0.0, 0.0, 60.0, -50.0]), $"beats: drift from the 120 BPM click ({string.Join(" ", drift)})");
var noGrid = new System.Text.Json.Nodes.JsonObject();
LeadSheet.AddBeats(noGrid, new System.Text.Json.Nodes.JsonObject { ["tempoBpm"] = 96.0, ["beats"] = new System.Text.Json.Nodes.JsonArray(0.0, 0.625) }, 0);
Check(noGrid["beatDriftMs"] is null && (double)noGrid["tempoBpm"]! == 96, "beats: an imported file has no click to drift from");

// STUDIO S6 review: key suggestion, corrections, tempo from the drums.
Take Progression(params (int Root, int Quality)[] chords)
{
    var t = new Take();
    for (int i = 0; i < chords.Length; i++) t.Chords.Add(new TakeChord(i * 2.5, (i + 1) * 2.5, "", "", chords[i].Root, -1, chords[i].Quality, 0.9f));
    return t;
}
var popC = Review.SuggestKey(Progression((0, 0), (9, 1), (5, 0), (7, 0), (4, 1), (9, 1), (2, 1), (7, 7)));
Check(popC is { } kc && kc.Key == new KeyOption(0, false), $"review: C Am F G Em Am Dm G7 -> C major ({popC?.Key}, r {popC?.Correlation:0.00}, margin {popC?.Margin:0.00})");
var amin = Review.SuggestKey(Progression((9, 1), (2, 1), (4, 7), (9, 1), (5, 0), (2, 1), (4, 7), (9, 1)));
Check(amin is { } ka && ka.Key == new KeyOption(0, true), $"review: Am Dm E7 Am F Dm E7 Am -> A minor ({amin?.Key})");
var ebM = Review.SuggestKey(Progression((3, 0), (8, 0), (10, 7), (3, 0), (0, 1), (8, 0), (10, 7), (3, 0)));
Check(ebM is { } ke && ke.Key == new KeyOption(-3, false), $"review: Eb Ab Bb7 Eb Cm Ab Bb7 Eb -> Eb major ({ebM?.Key})");
Check(Review.SuggestKey(new Take()) is null, "review: nothing to hear, no suggestion");

var rv = new Review();
rv.Add(new Review.Fix("chord", 2.53, Root: 2));        // Am -> Dm
rv.Add(new Review.Fix("chord", 2.5, Quality: 7));      // ... -> D7: merges with the root fix
rv.Add(new Review.Fix("note", 1.0, Midi: 67));
rv.Add(new Review.Fix("bass", 5.0, Delete: true));
var rdoc = System.Text.Json.Nodes.JsonNode.Parse("""
{"session": {"keySet": false, "keyFifths": 0, "keyMode": 0, "bpm": 92},
 "events": [
  {"type": "chord", "start": 2.48, "end": 5.0, "symbol": "Am", "root": 9, "quality": 1, "bass": 9, "roman": "vi", "confidence": 0.4},
  {"type": "note", "start": 1.05, "end": 1.4, "midi": 66, "name": "F#4", "confidence": 0.5},
  {"type": "note", "part": "bass", "start": 5.02, "end": 6.0, "midi": 40, "name": "E2", "confidence": 0.5},
  {"type": "note", "part": "bass", "start": 6.02, "end": 7.0, "midi": 43, "name": "G2", "confidence": 0.5}]}
""")!.AsObject();
rv.Apply(rdoc);
var evs = rdoc["events"]!.AsArray();
Check(rv.Fixes.Count == 3 && (string?)evs[0]!["symbol"] == "D7" && (int)evs[0]!["bass"]! == 2 && (string?)evs[0]!["roman"] == "" && (bool)evs[0]!["corrected"]!,
      $"review: chord fixes merge, root position follows the root ({evs[0]!["symbol"]})");
Check((string?)evs[1]!["name"] == "G4" && (double)evs[1]!["confidence"]! == 1, "review: a note fix renames it and marks it certain");
Check(evs.Count == 3 && (int)evs[2]!["midi"]! == 43, "review: the deleted bass note is gone, the other stays");
var wr = System.Text.Json.Nodes.JsonNode.Parse("""
{"session": {"bpm": 92}, "tempoBpm": 100.0, "beats": [0.1, 0.7, 1.3, 1.85, 2.4],
 "events": [{"type": "note", "start": 0.7, "end": 1.575, "midi": 60}]}
""")!.AsObject();
Review.WarpToBeats(wr, [0.1, 0.7, 1.3, 1.85, 2.4]);
var we = wr["events"]![0]!;
Check(Review.ChordSymbol(10, 7, 10, 0) == "Bb7" && Review.ChordSymbol(6, 1, 6, 0) == "F#m" && Review.ChordSymbol(6, 0, 1, 2) == "F#/C#" && Review.ChordSymbol(3, 0, 3, -3) == "Eb",
      $"review: spelling of corrected chords ({Review.ChordSymbol(10, 7, 10, 0)} {Review.ChordSymbol(6, 1, 6, 0)} {Review.ChordSymbol(6, 0, 1, 2)})");
var rt2 = new Review { KeySet = true, KeyFifths = -3, TempoFromDrums = true };
var back = System.Text.Json.JsonSerializer.Deserialize<Review>(System.Text.Json.JsonSerializer.Serialize(rt2))!;
Check(back.KeySet == true && back.KeyFifths == -3 && back.TempoFromDrums, "review: key and tempo choices are saved");
Check(Math.Abs((double)we["start"]! - 0.6) < 1e-6 && Math.Abs((double)we["end"]! - 1.5) < 1e-6 && (double)wr["session"]!["bpm"]! == 100,
      $"review: tempo from the drums puts beat k on k beats of 100 BPM ({we["start"]} {we["end"]})");

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
// STUDIO harmony map: chords folded into triads (colours, inversions and passing chords out), degrees in the key.
TakeChord Ch(double a, double b, string sym) => new(a, b, sym, "", 0, -1, 0, 1);
var hm = LeadSheet.Harmony([Ch(0, 4, "G"), Ch(4, 4.5, "Cadd9"), Ch(4.5, 8, "Gmaj7"), Ch(8, 12, "D/F#"), Ch(12, 15, "Em7"), Ch(15, 18, "F#m")], new KeyOption(1, false));
string Hm(List<(double Start, double End, string Symbol, string Roman)> l) => string.Join(" ", l.Select(h => $"{h.Symbol}:{h.Roman}@{h.Start:0}-{h.End:0}"));
Check(Hm(hm) == "G:I@0-8 D:V@8-12 Em:vi@12-15 F#m:vii@15-18", $"harmony map: {Hm(hm)}");
Check(LeadSheet.Harmony([Ch(0, 2, "Am"), Ch(2, 4, "E7")], new KeyOption(0, true)).Select(h => h.Roman).SequenceEqual(["i", "V"]), "harmony map: degrees in a minor key");

// STUDIO mixer graph (S11): the native mirrors are the core's sizes; a take from before the lunchbox keeps its order.
{
    Check(System.Runtime.CompilerServices.Unsafe.SizeOf<UnitParamsNative>() == 120 && System.Runtime.CompilerServices.Unsafe.SizeOf<CableNative>() == 8
          && System.Runtime.CompilerServices.Unsafe.SizeOf<MixGraphNative>() == 4812, "mix graph: UnitParams 120, Cable 8, MixGraph 4812 bytes, as the core");
    var cm = new ChannelMix();
    Check(string.Join(",", cm.Slots()) == "trim,filter,gate,eq,comp", "old chain: console order by default");
    cm.Vst.AddRange([new VstInsert { Id = "a" }, new VstInsert { Id = "b", On = false }]);
    Check(string.Join(",", cm.Slots()) == "vst:a,vst:b,trim,filter,gate,eq,comp", "old chain: inserts first when never patched");
    var u = new ChannelMix { EqOn = true, Bands = [new Band { Freq = 1000, Gain = 3 }] }.UnitNative("eq", 7, false);
    Check(u.Kind == 4 && u.On == 1 && u.Id == 7 && u.Eq0.GainDb == 3, "mix graph: an EQ module as a unit");
    Check(new ChannelMix { EqOn = true }.UnitNative("eq", 7, true).Kind == 0, "mix graph: a rendered module is a wire");
}

// LIVE playground: the native mirrors are the core's sizes; a pattern step toggles one bit.
Check(System.Runtime.CompilerServices.Unsafe.SizeOf<PlaygroundConfigNative>() == 144 && System.Runtime.CompilerServices.Unsafe.SizeOf<PlaygroundStatusNative>() == 172
      && System.Runtime.CompilerServices.Unsafe.SizeOf<PgBlockNative>() == 20 && System.Runtime.CompilerServices.Unsafe.SizeOf<PgNoteNative>() == 8
      && System.Runtime.CompilerServices.Unsafe.SizeOf<PgRecordedNative>() == 12, "playground: the song's native mirrors are the core's sizes");
{
    var blk = new SongBlock { Id = 1, Bars = 1 };
    blk.Set(2, 5, 100);
    blk.Notes.Add(new SongNote(20, 2, 60, 90));
    blk.Resize(2);
    Check(blk.At(2, 5) == 100 && blk.Steps.Length == 16 * 2 * 16 && blk.Notes.Count == 1, "song block: more bars keep the pattern and notes");
    blk.Resize(1);
    Check(blk.At(2, 5) == 100 && blk.Notes.Count == 0, "song block: fewer bars drop what is past the end");
}
Check(RackCatalog.WithPlayground(RackCatalog.Default(LiveMode.Acoustic), true).Take(3).Select(e => e.Module).SequenceEqual(RackCatalog.Playground)
      && !RackCatalog.WithPlayground(RackCatalog.WithPlayground(RackCatalog.Default(LiveMode.Acoustic), true), false).Any(e => RackCatalog.Playground.Contains(e.Module)),
    "playground: PLAYGROUND puts its units on top of the rack and takes them out again");

// DS-C lunchbox: what the cables reach, the serial run from SAI (rendered with its inserts), loops.
{
    CableLink L(string f, string t) => new() { From = f, To = t };
    string J(IEnumerable<string> x) => string.Join(",", x);
    var bus = new[] { L("vocals.sai:out", "eq1:in"), L("bass.sai:out", "eq1:in"), L("eq1:out", "comp1:in"), L("comp1:out", "master:in"), L("vocals.aux:out", "rev:in"), L("rev:out", "master:in") };
    Check(J(Patchbay.Reach(bus, "vocals.sai")) == "eq1,comp1,master", "lunchbox: a track's SAI reaches a bus and the master");
    Check(J(Patchbay.Reach(bus, "vocals.aux")) == "rev,master", "lunchbox: the AUX is a send of its own");
    Check(J(Patchbay.Feeders(bus, "comp1")) == "eq1,vocals.sai,bass.sai", "lunchbox: a bus is fed by both tracks");
    Check(Patchbay.Head(bus, "vocals").Count == 0, "lunchbox: a module fed by two cables is not a track's own run");
    var serial = new[] { L("vocals.sai:out", "vst:a:in"), L("vst:a:out", "eq1:in"), L("eq1:out", "vocals.volta:in") };
    Check(J(Patchbay.Head(serial, "vocals")) == "vst:a,eq1", "lunchbox: the serial run from SAI");
    Check(Patchbay.Loops(bus).Count == 0, "lunchbox: no loop in a bus");
    var loop = new[] { L("vocals.aux:out", "eq1:in"), L("eq1:out", "vocals.volta:in") };
    Check(J(Patchbay.Loops(loop).OrderBy(x => x)) == "eq1,vocals.aux,vocals.volta", "lunchbox: the AUX back into its own VOLTA is a loop");
}

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
File.WriteAllText(Path.Combine(config, "Dissonancia", "rack-live-Melody.json"), """
[{"module": "Scope", "width": "half", "heightU": 9}, {"module": "Scope", "width": "full", "heightU": 2},
 {"module": "Fretboard", "width": "full", "heightU": 2}, {"module": "Bogus", "width": "full", "heightU": 1},
 {"module": "Status", "width": "half", "heightU": 1}]
""");
var rack = RackCatalog.Load(LiveMode.Melody);
Check(string.Join(",", rack.Select(e => $"{e.Module}{(e.Half ? "/2" : "")}:{e.HeightU}")) == "Scope/2:4,Status:1,Transport:1", "rack preset cleaned: " + string.Join(",", rack.Select(e => $"{e.Module}{(e.Half ? "/2" : "")}:{e.HeightU}")));
RackCatalog.Save(LiveMode.Melody, RackCatalog.Default(LiveMode.Melody));
Check(RackCatalog.Load(LiveMode.Melody).SequenceEqual(RackCatalog.Default(LiveMode.Melody)), "rack preset round trip");
Check(RackCatalog.Default(LiveMode.Acoustic).Any(e => e.Module == ModuleKind.Fretboard) && !RackCatalog.Default(LiveMode.Melody).Any(e => e.Module == ModuleKind.Timeline), "per-mode defaults");
Directory.Delete(config, true);

// STUDIO S7: a lead sheet with a bass line -> voice staff with symbols + bass staff, same bars;
// MIDI with a bass track and, with the tempo from the drums, a tempo map.
var lead = T((0, 1.0, "E4"), (1.0, 2.0, "G4"));
lead.Bpm = 120;
lead.Chords.Add(new TakeChord(0, 4, "C", "", 0, -1, 0, 0.9f));
foreach (var (a, b, m, n) in new[] { (0.0, 1.0, 36, "C2"), (1.0, 2.0, 43, "G2"), (2.0, 4.0, 36, "C2"), (4.0, 6.0, 41, "F2") })
    lead.BassNotes.Add(new TakeNote(a, b, m, Take.ParseName(n), 0.8f));
var ls = Score.Build(lead, 8);
var lx = new XmlDocument { XmlResolver = null };
lx.Load(new XmlTextReader(new StringReader(ls.MusicXml())) { DtdProcessing = DtdProcessing.Ignore });
int m1 = lx.SelectNodes("//part[@id='P1']/measure")!.Count, m2 = lx.SelectNodes("//part[@id='P2']/measure")!.Count;
Check(ls.Bass is not null && m1 == m2 && m1 == 3 && lx.SelectSingleNode("//part[@id='P2']//clef/sign")?.InnerText == "F" && lx.SelectNodes("//part[@id='P2']//harmony")!.Count == 0,
      $"lead sheet: voice + bass staves, {m1}/{m2} bars, bass clef, symbols only on the voice");
lead.BeatTimes = [0, 0.5, 1.02, 1.5];
byte[] lmid = Score.Build(lead, 8).Midi();
int ltracks = 0, tempos = 0;
for (int i = 0; i + 4 <= lmid.Length; i++) if (lmid[i] == 'M' && lmid[i + 1] == 'T' && lmid[i + 2] == 'r' && lmid[i + 3] == 'k') ltracks++;
for (int i = 0; i + 2 <= lmid.Length; i++) if (lmid[i] == 0xFF && lmid[i + 1] == 0x51) tempos++;
Check(ltracks == 4 && tempos == 3 && lmid.AsSpan().IndexOf(new byte[] { 0xFF, 0x51, 3, 0x07, 0xEF, 0x40 }) > 0,   // 0.52 s per beat = 520000 us
      $"lead sheet midi: {ltracks} tracks (bass), tempo map {tempos} events");

// STUDIO S9: a choir take (the core's "part": S / A / T / B notes, sounding) -> four staves, SATB,
// same bars: soprano with the symbols, alto treble, tenor treble 8vb, bass F; a MIDI track each.
string choirJson = Path.Combine(Path.GetTempPath(), $"dz_choir_{Environment.ProcessId}.json");
File.WriteAllText(choirJson, """
{"session": {"mode": 0, "quality": 1, "keySet": false, "keyFifths": 0, "keyMode": 0, "clef": 0, "meter": [4, 4], "bpm": 60},
 "events": [
  {"type": "chord", "start": 0, "end": 4, "symbol": "C", "roman": "", "root": 0, "bass": 0, "quality": 0, "confidence": 0.9},
  {"type": "note", "part": "S", "start": 0, "end": 2, "midi": 72, "name": "C5", "confidence": 0.8},
  {"type": "note", "part": "A", "start": 0, "end": 2, "midi": 64, "name": "E4", "confidence": 0.8},
  {"type": "note", "part": "T", "start": 0, "end": 2, "midi": 55, "name": "G3", "confidence": 0.8},
  {"type": "note", "part": "B", "start": 0, "end": 6, "midi": 48, "name": "C3", "confidence": 0.8}]}
""");
var choirTake = Take.Load(choirJson);
File.Delete(choirJson);
var cs = Score.Build(choirTake, 4);
var cx = new XmlDocument { XmlResolver = null };
cx.Load(new XmlTextReader(new StringReader(cs.MusicXml())) { DtdProcessing = DtdProcessing.Ignore });
string Names(string xpath) => string.Join(",", cx.SelectNodes(xpath)!.Cast<XmlNode>().Select(n => n.InnerText));
int bars1 = cx.SelectNodes("//part[@id='P1']/measure")!.Count, bars4 = cx.SelectNodes("//part[@id='P4']/measure")!.Count;
Check(choirTake.IsChoir && Names("//score-part/part-name") == "Soprano,Alto,Tenor,Bass" && bars1 == bars4 && bars1 == 2
      && Names("//part/measure[@number='1']/attributes/clef/sign") == "G,G,G,F" && cx.SelectSingleNode("//part[@id='P3']//clef-octave-change")?.InnerText == "-1"
      && cx.SelectNodes("//part[@id='P1']//harmony")!.Count == 1 && cx.SelectNodes("//part[@id='P2']//harmony")!.Count == 0
      && cx.SelectSingleNode("//part[@id='P3']//note/pitch/octave")?.InnerText == "3",
      $"choir: SATB staves {Names("//score-part/part-name")}, {bars1}/{bars4} bars, clefs, tenor 8vb at sounding pitch, symbols on the soprano");
byte[] cmid = cs.Midi();
int ctracks = 0;
for (int i = 0; i + 4 <= cmid.Length; i++) if (cmid[i] == 'M' && cmid[i + 1] == 'T' && cmid[i + 2] == 'r' && cmid[i + 3] == 'k') ctracks++;
Check(ctracks == 6 && cs.Json().Contains("\"Tenor\""), $"choir midi: {ctracks} tracks (tempo, soprano, chords, A, T, B); JSON by part");

// Every MusicXML we write validates against the MusicXML 4.0 XSD (downloaded once, cached next to the binary).
var samples = new[] { s1, s2, s4, s5, s6, s7, B(t8), B(take), ls, cs }.Select(x => x.MusicXml()).ToArray();
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

// S8: VST3 scanning through the out-of-process helper, against the core's test plugin.
{
    string build = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../core/build"));
    PluginHost.HelperOverride = Path.Combine(build, OperatingSystem.IsWindows() ? "dz_plugin.exe" : "dz_plugin");
    if (!PluginHost.Present) Console.WriteLine("skip VST3 scan: core built without DZ_WITH_VST3");
    else
    {
        string cache = Path.Combine(Path.GetTempPath(), $"dz_plugins_{Environment.ProcessId}.json");
        PluginHost.CacheOverride = cache;
        Environment.SetEnvironmentVariable("DISSONANCIA_VST3_PATH", Path.Combine(build, "VST3"));
        var scan = PluginHost.ScanAsync().Result;
        var gain = scan.SelectMany(e => e.Plugins).FirstOrDefault(p => p.Name == "DZ Test Gain");
        Check(gain is { Vendor: "Dissonancia", Cid: "6F1A3C214D2B47E89A1C5B3E7D0E2F41" }, "VST3 scan finds the test plugin out of process");
        var again = PluginHost.ScanAsync().Result;   // unchanged bundles come from the cache
        Check(again.Count == scan.Count && File.Exists(cache), "VST3 second scan agrees (unchanged bundles from the cache)");
        File.Delete(cache);

        // S8 step 2: offline render through the plugin; its state restores the same sound.
        if (gain is not null)
        {
            string tmp = Path.Combine(Path.GetTempPath(), $"dz_vst_{Environment.ProcessId}");
            Directory.CreateDirectory(tmp);
            var sine = Enumerable.Range(0, 48000).Select(i => (float)(0.25 * Math.Sin(2 * Math.PI * 440 * i / 48000))).ToArray();
            WriteWav(Path.Combine(tmp, "in.wav"), sine, 48000);
            string? e1 = PluginHost.RenderAsync(gain, Path.Combine(tmp, "in.wav"), Path.Combine(tmp, "a.wav"), null, Path.Combine(tmp, "state.bin"),
                new Dictionary<uint, double> { [0] = 0.625 }).Result;   // +6 dB
            var a = ReadWav(Path.Combine(tmp, "a.wav"));
            Check(e1 is null && a.Length == 2 * sine.Length && Math.Abs(20 * Math.Log10(a.Max() / sine.Max()) - 6) < 0.01, $"VST3 render: +6 dB through the plugin, stereo, same length ({e1})");
            string? e2 = PluginHost.RenderAsync(gain, Path.Combine(tmp, "in.wav"), Path.Combine(tmp, "b.wav"), Path.Combine(tmp, "state.bin"), null).Result;
            var b = ReadWav(Path.Combine(tmp, "b.wav"));
            Check(e2 is null && b.SequenceEqual(a), "VST3 state: the saved state renders the same sound");
            // Latency: the plugin delays 64 samples and says so; the render lines up with the input.
            string? e4 = PluginHost.RenderAsync(gain, Path.Combine(tmp, "in.wav"), Path.Combine(tmp, "d.wav"), null, null, new Dictionary<uint, double> { [1] = 1 }).Result;
            var dl = ReadWav(Path.Combine(tmp, "d.wav"));
            bool aligned = dl.Length == 2 * sine.Length && Enumerable.Range(0, sine.Length).All(i => Math.Abs(dl[2 * i] - sine[i]) < 1e-6);
            Check(e4 is null && aligned, "VST3 latency: a 64-sample look-ahead plugin renders aligned with the take");
            // Parameters as the controller has them after the state (the +6 dB state from the first render).
            var ps = PluginHost.ParamsAsync(gain, Path.Combine(tmp, "state.bin")).Result;
            Check(ps.Count == 2 && ps[0] is { Title: "Gain", Units: "dB", Editable: true } && Math.Abs(ps[0].Value - 0.625) < 1e-9 && ps[1] is { Steps: 1, Value: 0 },
                "VST3 params: titles, units, steps and the values of the saved state");
            // The editor window (X11 / Win32): the test plugin's view edits the gain from a run-loop
            // timer; the helper closes the window by itself here and saves the state.
            Environment.SetEnvironmentVariable("DZ_EDITOR_AUTOCLOSE_MS", "600");
            var ed = PluginHost.EditorAsync(gain, Path.Combine(tmp, "edit.bin")).Result;
            if (ed.Error is { } ee && (ee.Contains("display") || ee.Contains("not available"))) Console.WriteLine("skip VST3 editor window: " + ee);
            else
            {
                var after = PluginHost.ParamsAsync(gain, Path.Combine(tmp, "edit.bin")).Result;
                Check(ed.Error is null && ed.Edits.TryGetValue(0, out var g) && Math.Abs(g - 0.75) < 1e-9 && Math.Abs(after[0].Value - 0.75) < 1e-9,
                    $"VST3 editor: the window, the plugin's edit, the saved state ({ed.Error})");
            }
            string? e3 = PluginHost.RenderAsync(gain with { Cid = "00000000000000000000000000000000" }, Path.Combine(tmp, "in.wav"), Path.Combine(tmp, "c.wav"), null, null).Result;
            Check(e3 is not null && e3.Contains("no class"), "VST3 render: a wrong plugin is an error, not a crash");
            Directory.Delete(tmp, true);
        }
    }
}

static void WriteWav(string path, float[] mono, int rate)
{
    using var w = new BinaryWriter(File.Create(path));
    w.Write("RIFF"u8); w.Write(36 + mono.Length * 4); w.Write("WAVE"u8);
    w.Write("fmt "u8); w.Write(16); w.Write((short)3); w.Write((short)1); w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)32);
    w.Write("data"u8); w.Write(mono.Length * 4);
    foreach (var v in mono) w.Write(v);
}

static float[] ReadWav(string path)   // float32 data chunk, any channel count, interleaved
{
    if (!File.Exists(path)) return [];
    var d = File.ReadAllBytes(path);
    for (int i = 12; i + 8 <= d.Length;)
    {
        int size = BitConverter.ToInt32(d, i + 4);
        if (d[i] == 'd' && d[i + 1] == 'a' && d[i + 2] == 't' && d[i + 3] == 'a')
            return Enumerable.Range(0, size / 4).Select(k => BitConverter.ToSingle(d, i + 8 + 4 * k)).ToArray();
        i += 8 + size + (size & 1);
    }
    return [];
}

// TorchCheckpoint: a tiny torch.save checkpoint (torch 2.5) with a plain tensor, a transposed view,
// an offset view, an OrderedDict and an object the reader does not know (argparse.Namespace).
{
    var ckptPath = Path.Combine(Path.GetTempPath(), "dz-tiny.ckpt");
    File.WriteAllBytes(ckptPath, Convert.FromBase64String("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"));
    using var ck = new TorchCheckpoint(ckptPath);
    var st = ck.State();
    float[] F(string k) { var b = ck.Read(st[k]); var f = new float[b.Length / 4]; Buffer.BlockCopy(b, 0, f, 0, b.Length); return f; }
    Check(st.Count == 3 && ck.Root["epoch"] is 3L, "checkpoint: state_dict with 3 tensors, epoch 3");
    Check(F("a").SequenceEqual([0f, 1, 2, 3, 4, 5]), "checkpoint: 2x3 tensor in row order");
    Check(F("t").SequenceEqual([0f, 3, 1, 4, 2, 5]), "checkpoint: transposed view read through its strides");
    Check(F("s").SequenceEqual([4f, 5, 6]), "checkpoint: view at a storage offset");
    ck.Dispose();
    File.Delete(ckptPath);
}

Console.WriteLine(failures == 0 ? "all checks passed" : $"{failures} FAILED");
// LIVE Música completa: key, meter and 4-bar form from confirmed chords on the beat count.
{
    LiveSong Play(int beatsPerBar, (int Root, int Q, string Sym)[][] phrases, int pickupBeats)
    {
        var song = new LiveSong();
        var chords = phrases.SelectMany(p => p).ToList();
        double period = 0.5, t = 0;
        int beat = 0, total = pickupBeats + chords.Count * beatsPerBar + beatsPerBar;
        for (; beat < total; beat++, t += period)
        {
            song.Beat(t, 120);
            int k = beat - pickupBeats;
            if (k >= 0 && k % beatsPerBar == 0 && k / beatsPerBar < chords.Count && k > 0)
            {
                var c = chords[k / beatsPerBar - 1];   // the previous bar's chord ends now
                song.AddChord(t - beatsPerBar * period, c.Sym);
            }
        }
        return song;
    }
    (int, int, string)[] a = [(7, 0, "G"), (2, 0, "D"), (4, 1, "Em"), (0, 0, "C")], b = [(9, 1, "Am"), (0, 0, "C"), (2, 0, "D"), (2, 0, "D")];
    var aaba = Play(4, [a, a, b, a], 1);
    Check(aaba.Meter() == (4, 1), $"live song: 4/4 with the downbeat after one pickup beat: {aaba.Meter()}");
    Check(string.Concat(aaba.Form()) == "AABA", $"live song: form AABA: {string.Concat(aaba.Form())}");
    var waltz = Play(3, [a, b, a], 0);
    Check(waltz.Meter().Beats == 3, $"live song: chords every 3 beats -> 3/4: {waltz.Meter()}");
}

// STUDIO: stereo position of a stem (energy pan, width from the channel correlation).
{
    string Stereo(string name, Func<double, (double L, double R)> at)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dz-pan-{name}.wav");
        using var w = new BinaryWriter(File.Create(path));
        int frames = 44100;
        w.Write("RIFF"u8); w.Write(36 + frames * 8); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)3); w.Write((short)2);
        w.Write(44100); w.Write(44100 * 8); w.Write((short)8); w.Write((short)32); w.Write("data"u8); w.Write(frames * 8);
        var rng = new Random(1);
        for (int i = 0; i < frames; i++) { var (l, r) = at(rng.NextDouble() * 2 - 1); w.Write((float)l); w.Write((float)r); }
        return path;
    }
    var centre = StemTrack.Load("c", Stereo("c", x => (x, x)))!;
    var left = StemTrack.Load("l", Stereo("l", x => (x, 0.5 * x)))!;   // energy 1 : 0.25 -> pan -0.6
    var hard = StemTrack.Load("h", Stereo("h", x => (0, x)))!;
    var wide = StemTrack.Load("w", Stereo("w", x => (x, Math.Sin(x * 1e4))))!;   // unrelated channels
    Check(centre.PanLabel == "C" && centre.Width < 0.01, $"pan: same signal both sides = centre, no width ({centre.PanLabel}, {centre.Width:0.00})");
    Check(left.PanLabel == "L60" && left.Width < 0.01, $"pan: left louder by 6 dB = L60 ({left.PanLabel}, {left.Width:0.00})");
    Check(hard.PanLabel == "R100" && hard.Width == 0, $"pan: right only = R100, a point ({hard.PanLabel}, {hard.Width:0.00})");
    Check(wide.Width > 0.8, $"pan: uncorrelated channels are wide ({wide.Width:0.00})");
}

// STUDIO instruments (Basic Pitch per stem): one more MIDI track each, notes on the event-time grid.
{
    var t = T((0.0, 1.0, "G4"));
    t.Instruments["other"] = [new TakeNote(0.0, 1.0, 43, Take.ParseName("G2"), 0.7f), new TakeNote(0.0, 1.0, 47, Take.ParseName("B2"), 0.7f), new TakeNote(1.0, 2.0, 50, Take.ParseName("D3"), 0.6f)];
    var midi = Score.Build(t).Midi();
    Check(midi[11] == 4, $"instruments: tempo, melody, chords + 1 instrument track = 4 tracks ({midi[11]})");
    int ons = 0;
    for (int i = 0; i + 2 < midi.Length; i++) if (midi[i] == 0x96 && midi[i + 2] > 0) ons++;   // note-ons on channel 6
    Check(ons == 3, $"instruments: 3 note-ons on channel 7 ({ons})");
    // YourMT3+ keys: the GM program from the key, drums on channel 10 without a program change.
    t.Instruments.Clear();
    t.Instruments["mt3:33:Bass"] = [new TakeNote(0.0, 1.0, 40, Take.ParseName("E2"), 0.8f)];
    t.Instruments["mt3:128:Drums"] = [new TakeNote(0.0, 0.1, 36, Take.ParseName("C2"), 0.8f)];
    var mt3Midi = Score.Build(t).Midi();
    bool bassProgram = false, drumOn = false, drumProgram = false;
    for (int i = 0; i + 2 < mt3Midi.Length; i++)
    {
        bassProgram |= mt3Midi[i] == 0xC6 && mt3Midi[i + 1] == 33;
        drumOn |= mt3Midi[i] == 0x99 && mt3Midi[i + 1] == 36;
        drumProgram |= mt3Midi[i] == 0xC9;
    }
    Check(bassProgram && drumOn && !drumProgram, "instruments: YourMT3+ program 33 on channel 7, drums on channel 10");
}

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
