// DEV: dotnet run -c Release --project tools/eval/bpnotes -- OUTDIR a.wav b.wav ...
// Writes OUTDIR/<name>.notes, one note per line: start end midi confidence (Basic Pitch, whole file).
using Dissonancia;

var outDir = args[0];
Directory.CreateDirectory(outDir);
Parallel.ForEach(args.Skip(1), new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 4) }, wav =>
{
    var path = Path.Combine(outDir, Path.GetFileNameWithoutExtension(wav) + ".notes");
    if (File.Exists(path)) return;
    var (x, rate) = ReadWav(wav);
    using var bp = new BasicPitch();
    var notes = bp.Notes(BasicPitch.Resample(x, rate));
    File.WriteAllLines(path, notes.Select(n => FormattableString.Invariant($"{n.Start:F3} {n.End:F3} {n.Midi} {n.Confidence:F2}")));
    Console.WriteLine($"{Path.GetFileName(wav)}: {notes.Count} notes");
});

// PCM 16/24-bit or float 32, any channel count, mixed to mono
static (float[], int) ReadWav(string path)
{
    using var r = new BinaryReader(File.OpenRead(path));
    r.ReadBytes(12);
    int channels = 1, rate = 0, bits = 16, format = 1;
    while (r.BaseStream.Position + 8 <= r.BaseStream.Length)
    {
        var id = new string(r.ReadChars(4));
        int size = r.ReadInt32();
        if (id == "fmt ")
        {
            format = r.ReadInt16(); channels = r.ReadInt16(); rate = r.ReadInt32(); r.ReadBytes(6); bits = r.ReadInt16();
            r.ReadBytes(size - 16);
        }
        else if (id == "data")
        {
            var raw = r.ReadBytes(size);
            int bps = bits / 8, n = raw.Length / (bps * channels);
            var x = new float[n];
            for (int i = 0; i < n; i++)
            {
                float s = 0;
                for (int c = 0; c < channels; c++)
                {
                    int o = (i * channels + c) * bps;
                    s += format == 3 ? BitConverter.ToSingle(raw, o)
                       : bits == 24 ? ((raw[o] | raw[o + 1] << 8 | (sbyte)raw[o + 2] << 16) / 8388608f)
                       : BitConverter.ToInt16(raw, o) / 32768f;
                }
                x[i] = s / channels;
            }
            return (x, rate);
        }
        else r.ReadBytes(size + (size & 1));
    }
    throw new InvalidDataException(path);
}
