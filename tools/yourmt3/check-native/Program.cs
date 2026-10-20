// DEV-ONLY parity check of the native YourMT3+ (app/YourMt3.cs) against the authors' Python:
//   1. export:     <root>/venv/bin/python tools/yourmt3/export_onnx.py <root> <dir> <take.wav> 3.0
//   2. references: <root>/venv/bin/python tools/yourmt3/dz_yourmt3.py <root> <take.wav> <dir>/ref_notes.json
//                  CUDA_VISIBLE_DEVICES= ... dz_yourmt3.py ... <dir>/ref_cpu.json   (fp32, the exact reference)
//   3. install:    copy encoder/decoder_first/decoder_next.onnx + meta.json to YourMt3.Dir
//   4. check:      dotnet run --project tools/yourmt3/check-native -- <dir> <take.wav>
// Expect: spectrogram within ~0.003, every token equal, every note equal (2026-10-19: 32/32).
using System.Text.Json;
using Dissonancia;
Accel.Init();   // the GPU build when installed and checked (as the app does at startup)
var S = args[0];
var refj = JsonDocument.Parse(File.ReadAllText(S + "/reference.json")).RootElement;
var wav = refj.GetProperty("wav").EnumerateArray().Select(e => e.GetSingle()).ToArray();
var spec = refj.GetProperty("spec").EnumerateArray().SelectMany(r => r.EnumerateArray().Select(e => e.GetSingle())).ToArray();
using var m = new YourMt3();
if (Environment.GetEnvironmentVariable("DZ_MT3_BATCH") is { Length: > 0 } batch) m.Batch = int.Parse(batch);   // segments per decoder run
Console.WriteLine("device " + m.Device);
var mine = m.Spectrogram(wav, out int frames, out int bins);
Console.WriteLine($"spec {frames}x{bins} vs {spec.Length}: max |diff| {mine.Zip(spec).Max(z => Math.Abs(z.First - z.Second)):0.000000}");
var sw = System.Diagnostics.Stopwatch.StartNew();
var tok = m.Tokens(spec, frames, bins);
var want = refj.GetProperty("tokens").EnumerateArray().Select(c => c.EnumerateArray().Select(e => e.GetInt32()).ToArray()).ToArray();
int same = 0, total = 0;
for (int c = 0; c < want.Length; c++) for (int i = 0; i < Math.Min(want[c].Length, tok[c].Length); i++) { total++; same += want[c][i] == tok[c][i] ? 1 : 0; }
Console.WriteLine($"tokens {same}/{total} same, lengths {tok[0].Length} vs {want[0].Length}, {sw.ElapsedMilliseconds} ms");
// detokenizer on the Python run's tokens
var rn = JsonDocument.Parse(File.ReadAllText(S + "/ref_notes.json")).RootElement;
var segs = rn.GetProperty("segments").EnumerateArray().Select(s => s.EnumerateArray().Select(c => c.EnumerateArray().Select(e => e.GetInt32()).ToArray()).ToArray()).ToList();
var notes = m.Notes(segs);
var refNotes = rn.GetProperty("notes").EnumerateArray().Select(n => (n.GetProperty("start").GetDouble(), n.GetProperty("end").GetDouble(), n.GetProperty("midi").GetInt32(), n.GetProperty("instrument").GetString())).ToList();
int match = notes.Zip(refNotes).Count(z => Math.Abs(z.First.Start - z.Second.Item1) < 1e-3 && Math.Abs(z.First.End - z.Second.Item2) < 1e-3 && z.First.Midi == z.Second.Item3 && z.First.Instrument == z.Second.Item4);
Console.WriteLine($"detokenizer: {notes.Count} notes vs {refNotes.Count}, {match} identical");
// whole take, native
sw.Restart();
var audio = StemTrack.ReadMono(args[1], out int rate);
var all = m.Transcribe(audio, rate);
Console.WriteLine($"whole take native: {all.Count} notes in {sw.ElapsedMilliseconds} ms");
Console.WriteLine("notes hash " + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join(";", all.Select(n => FormattableString.Invariant($"{n.Start:0.000} {n.End:0.000} {n.Midi} {n.Instrument}"))))))[..12]);   // equal across batch sizes and devices
foreach (var n in all.Take(8)) Console.WriteLine($"  {n.Start:0.00}-{n.End:0.00} {n.Midi} {n.Instrument}");
var cpuRef = JsonDocument.Parse(File.ReadAllText(S + "/ref_cpu.json")).RootElement.GetProperty("notes").EnumerateArray()
    .Select(n => (n.GetProperty("start").GetDouble(), n.GetProperty("end").GetDouble(), n.GetProperty("midi").GetInt32(), n.GetProperty("instrument").GetString())).ToList();
int same2 = all.Zip(cpuRef).Count(z => Math.Abs(z.First.Start - z.Second.Item1) < 0.011 && Math.Abs(z.First.End - z.Second.Item2) < 0.011 && z.First.Midi == z.Second.Item3 && z.First.Instrument == z.Second.Item4);
Console.WriteLine($"native vs python cpu: {all.Count} vs {cpuRef.Count}, {same2} identical (±10 ms)");
