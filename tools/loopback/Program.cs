using System.Diagnostics;
using System.Text.Json;
using Dissonancia;

// Live loopback test (WSLg / PulseAudio): plays a WAV with paplay, captures the sink's monitor with the
// real core and a REC take, then builds the score and engraves it.
//   python3 tools/loopback/gen.py <dir>      (guitar.wav, voice.wav)
//   dotnet run --project tools/loopback -- <dir>/guitar.wav GuitarChords <dir>
string wav = args[0], outDir = args[2];
var mode = Enum.Parse<AppMode>(args[1]);
var core = NativeCore.TryLoad(out var err) ?? throw new Exception(err);
var devices = core.CaptureDevices();
int dev = Array.FindIndex(devices, d => d.Contains("Monitor", StringComparison.OrdinalIgnoreCase) || d.Contains("RDPSink"));
Console.WriteLine("capture devices: " + string.Join(" | ", devices) + $"  -> using #{dev}");
var s = new Session { Mode = mode, KeySet = true, Key = new KeyOption(1, false), Clef = mode == AppMode.GuitarChords ? Clef.Treble8vb : Clef.Treble,
                      Bpm = 92, BeatsPerBar = 4, BeatUnit = 4, CountInBars = 1, CaptureDevice = dev, ClickOutput = false };
var src = core.Start(s);
var f = new LiveFrame();
var clock = Stopwatch.StartNew();
double Now() => clock.Elapsed.TotalSeconds;
Thread.Sleep(500);
src.Read(f, Now());
src.ToggleRec(Now());
Process? play = null;
double recAt = 0, playAt = 0, endAt = double.MaxValue;
string lastChord = "", lastNote = "";
var log = new List<string>();
var noteLat = new List<float>(); var chordLat = new List<float>(); var proc = new List<float>();
float maxCpu = 0, maxPeak = -200;
double nextContext = 1;
while (Now() < endAt)
{
    src.Read(f, Now());
    if (f.Recording && play is null)
    {
        recAt = Now();
        play = Process.Start(new ProcessStartInfo("paplay", $"\"{wav}\"") { UseShellExecute = false });
        playAt = Now();
    }
    if (play is { HasExited: true } && endAt == double.MaxValue) endAt = Now() + 1.5;
    if (play is not null && Now() - playAt >= nextContext)   // dynamic-session estimates as they settle
    {
        nextContext += 1;
        log.Add($"{Now() - playAt,6:0.000}s  heard tempo {f.DetectedBpm:0.0} {f.TempoConfidence:0.00}  last beat {f.AnalyzedSeconds - f.LastBeatSeconds:0.000} s ago");
    }
    maxPeak = Math.Max(maxPeak, f.PeakDbfs);
    if (f.Recording) { proc.Add(f.ProcessingMs); maxCpu = Math.Max(maxCpu, f.CpuPercent); }
    if (s.IsChordMode && f.ChordConfirmed && f.ChordSymbol != lastChord)
    {
        lastChord = f.ChordSymbol;
        chordLat.Add(f.ChordLatencyMs);
        log.Add($"{Now() - playAt,6:0.000}s  chord {f.ChordSymbol,-7} {f.ChordRoman,-6} conf {f.ChordConfidence:0.00}  bass {(f.BassSettled ? f.Bass.Name : "-"),-4} lat {f.ChordLatencyMs:0} ms  {f.ChordReason}  {f.Cadence}");
    }
    if (!s.IsChordMode && f.NoteValid && f.Note.Name != lastNote)
    {
        lastNote = f.Note.Name;
        noteLat.Add(f.NoteLatencyMs);
        log.Add($"{Now() - playAt,6:0.000}s  note {f.Note.Name,-4} {f.Cents,4:+0;-0} c  {f.Hz,6:0.0} Hz  lat {f.NoteLatencyMs:0} ms");
    }
    Thread.Sleep(10);
}
src.ToggleRec(Now());
Thread.Sleep(1500);
src.Read(f, Now());
Console.WriteLine($"input peak max {maxPeak:0.0} dBFS, simulated {f.Simulated}, error {src.LastError}");
Console.WriteLine($"capture {f.CaptureMs:0.0} ms, processing median {Median(proc):0.0} ms, CPU max {maxCpu:0.0} %, xruns {f.Xruns}, gaps {f.RecorderGaps}, event gaps {(src as NativeLiveSource)?.EventGaps}, rec->play {playAt - recAt:0.000}s");
if (chordLat.Count > 0) Console.WriteLine($"chord latency at confirmation: median {Median(chordLat):0} ms");
if (noteLat.Count > 0) Console.WriteLine($"note latency: median {Median(noteLat):0} ms");
foreach (var l in log) Console.WriteLine(l);
core.Dispose();

var path = Take.Latest()!;
var take = Take.Load(path);
var score = Score.Build(take);
take.Path = Path.Combine(outDir, Path.GetFileName(path));
Console.WriteLine($"\ntake {Path.GetFileName(path)}: complete {take.EventLogComplete}, {take.Notes.Count} notes, {take.Chords.Count} chords, {take.Cadences.Count} cadences, {score.Measures.Count} bars");
foreach (var c in take.Chords) Console.WriteLine($"  chord {c.Start,7:0.000}-{c.End,7:0.000}  {c.Symbol,-7} {c.Roman,-6} conf {c.Confidence:0.00}");
foreach (var c in take.Cadences) Console.WriteLine($"  cadence {c.Time,7:0.000}  {c.From} -> {c.To}  type {c.Type}  {c.Confidence:0.00}  {c.Evidence}");
foreach (var n in take.Notes) Console.WriteLine($"  note {n.Start,7:0.000}-{n.End,7:0.000}  {n.Sounding.Name,-4} conf {n.Confidence:0.00}");
Console.WriteLine(score.ChordChart());
Console.WriteLine(score.RomanLine());
Console.WriteLine(string.Join(" ", score.Export().Select(Path.GetFileName)));
using var vrv = Verovio.TryCreate(out var verr) ?? throw new Exception(verr);
var pages = vrv.Render(score.MusicXml(), 1100);
for (int p = 0; p < pages.Length; p++) File.WriteAllBytes(Path.Combine(outDir, $"{mode}-page{p + 1}.png"), SvgRaster.Png(pages[p], 1.5f)!);
Console.WriteLine($"verovio: {pages.Length} page(s) {vrv.Log.Trim()}");

static float Median(List<float> x) { if (x.Count == 0) return 0; var s = x.OrderBy(v => v).ToList(); return s[s.Count / 2]; }
