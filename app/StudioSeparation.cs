using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Dissonancia;

// STUDIO S3 — separation (docs/IMPLEMENTATION.md): Demucs v4 through the dz_separate helper, run as
// its own process (a crash or a cancel never touches the app). Optional: the helper ships with the
// app when it was built (DZ_WITH_DEMUCS), the weights are downloaded on demand, checksum-pinned.

/// The Demucs weights (MIT, converted to ggml by demucs.cpp), downloaded once into the app data folder.
public static class DemucsModel
{
    public sealed record Variant(string Stems, string File, long Size, string Sha256);
    public static readonly Variant FourStems = new("4", "ggml-model-htdemucs-4s-f16.bin", 83994361, "72b17c42d308982ddb5069bc3bf48b81a5aac4cb6516e4366c0fa7cef6df0064");
    public static readonly Variant SixStems = new("6", "ggml-model-htdemucs-6s-f16.bin", 54855129, "09704f4ceae204e56e77d5eefd6ac71d7275be81fd507e6913371d59abcee856");
    const string BaseUrl = "https://huggingface.co/datasets/Retrobear/demucs.cpp/resolve/main/";

    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dissonancia", "models");
    public static string PathOf(Variant v) => Path.Combine(Dir, v.File);
    public static string Helper => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "dz_separate.exe" : "dz_separate");
    public static bool HelperPresent => File.Exists(Helper);
    public static bool Installed(Variant v) => File.Exists(PathOf(v)) && new FileInfo(PathOf(v)).Length == v.Size;

    /// Downloads and verifies the weights; a wrong checksum deletes the file. progress: 0..1.
    public static async Task<string?> InstallAsync(Variant v, Action<float> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);
        string tmp = PathOf(v) + ".part";
        var err = await Download.FileAsync(BaseUrl + v.File, tmp, v.Size, v.Sha256, n => progress((float)n / v.Size), ct);
        if (err is null) File.Move(tmp, PathOf(v), true);
        return err;
    }
}

/// One separation run: the helper on a file, progress parsed from its output, the stems it wrote.
public sealed class SeparationJob
{
    readonly Process _p;
    readonly float[] _threads;
    readonly List<(string Name, string Path)> _stems = [];
    readonly object _lock = new();
    string _error = "";

    public static readonly string[] StemOrder = ["vocals", "bass", "other", "drums", "guitar", "piano"];

    SeparationJob(Process p, int threads) { _p = p; _threads = new float[threads]; }

    /// threads: Demucs inference threads (4 measured fastest on a 10-core machine; more is slower).
    public static SeparationJob? Start(DemucsModel.Variant v, string input, string outDir, out string? error)
    {
        error = null;
        int threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        var psi = new ProcessStartInfo(DemucsModel.Helper)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        foreach (var a in new[] { DemucsModel.PathOf(v), input, outDir, threads.ToString(CultureInfo.InvariantCulture) }) psi.ArgumentList.Add(a);
        psi.Environment["OMP_NUM_THREADS"] = "1";   // parallel over segments, not inside them
        try
        {
            var p = Process.Start(psi);
            if (p is null) { error = "the separation helper did not start"; return null; }
            var job = new SeparationJob(p, threads);
            p.OutputDataReceived += (_, e) => job.Line(e.Data);
            p.ErrorDataReceived += (_, e) => { if (e.Data?.StartsWith("error") == true) lock (job._lock) job._error = e.Data; };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return job;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { error = e.Message; return null; }
    }

    static readonly Regex ThreadProgress = new(@"\[THREAD (\d+)\] \(\s*([0-9.]+)%\)", RegexOptions.Compiled);

    void Line(string? line)
    {
        if (line is null) return;
        lock (_lock)
        {
            var m = ThreadProgress.Match(line);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int t) && t < _threads.Length)
                _threads[t] = Math.Max(_threads[t], float.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) / 100f);
            else if (line.StartsWith("stem ")) { var parts = line.Split(' ', 3); if (parts.Length == 3) _stems.Add((parts[1], parts[2])); }
        }
    }

    public bool Running => !_p.HasExited;
    public float Progress { get { lock (_lock) return _threads.Average(); } }
    public string Error { get { lock (_lock) return _error.Length > 0 ? _error : _p.HasExited && _p.ExitCode != 0 ? $"helper exit code {_p.ExitCode}" : ""; } }
    public bool Succeeded => _p.HasExited && _p.ExitCode == 0;
    public List<(string Name, string Path)> Stems { get { lock (_lock) return [.. _stems]; } }
    public void Cancel() { try { if (!_p.HasExited) _p.Kill(true); } catch (InvalidOperationException) { } }
}

/// A separated track as the recorder draws it: its waveform peaks (min/max per 256 frames), read
/// from the stem's WAV once.
public sealed class StemTrack
{
    public required string Name, Path;
    public float[] Min = [], Max = [];
    public long Frames;
    public int Rate = 44100;
    public const int Block = 256;
    // Stereo position over the whole stem, energy-weighted (silence does not count): Pan -1 = all left,
    // +1 = all right (level difference only; a time-delay stereo image reads as centre); Width 0 = the
    // same signal in both channels, 1 = uncorrelated, beyond = out of phase (clamped).
    public float Pan, Width;
    public string PanLabel => Math.Abs(Pan) < 0.05f ? "C" : $"{(Pan < 0 ? "L" : "R")}{Math.Abs(Pan) * 100:0}";

    /// A stem's samples, channels averaged (32-bit float WAV, as the separator writes them).
    public static float[] ReadMono(string path, out int rate)
    {
        using var f = File.OpenRead(path);
        using var r = new BinaryReader(f);
        f.Seek(12, SeekOrigin.Begin);
        int ch = 2;
        rate = 44100;
        while (f.Position < f.Length)
        {
            string id = new(r.ReadChars(4));
            int size = r.ReadInt32();
            if (id == "fmt ") { r.ReadInt16(); ch = r.ReadInt16(); rate = r.ReadInt32(); f.Seek(size - 8, SeekOrigin.Current); }
            else if (id == "data")
            {
                var x = new float[size / 4 / ch];
                for (int i = 0; i < x.Length; i++)
                {
                    float v = 0;
                    for (int c = 0; c < ch; c++) v += r.ReadSingle();
                    x[i] = v / ch;
                }
                return x;
            }
            else f.Seek(size + (size & 1), SeekOrigin.Current);
        }
        return [];
    }

    public static StemTrack? Load(string name, string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            using var r = new BinaryReader(f);
            if (new string(r.ReadChars(4)) != "RIFF") return null;
            r.ReadInt32();
            r.ReadChars(4);
            int ch = 2, bits = 32, rate = 44100;
            while (f.Position < f.Length)
            {
                string id = new(r.ReadChars(4));
                int size = r.ReadInt32();
                if (id == "fmt ") { r.ReadInt16(); ch = r.ReadInt16(); rate = r.ReadInt32(); r.ReadInt32(); r.ReadInt16(); bits = r.ReadInt16(); f.Seek(size - 16, SeekOrigin.Current); }
                else if (id == "data" && bits == 32)
                {
                    long frames = size / 4 / ch;
                    var t = new StemTrack { Name = name, Path = path, Frames = frames, Rate = rate };
                    int blocks = (int)((frames + Block - 1) / Block);
                    t.Min = new float[blocks];
                    t.Max = new float[blocks];
                    double ll = 0, rr = 0, lr = 0;
                    for (int b = 0; b < blocks; b++)
                    {
                        float lo = 1, hi = -1;
                        for (int i = 0; i < Block && b * (long)Block + i < frames; i++)
                        {
                            float v = 0, left = 0;
                            for (int c = 0; c < ch; c++)
                            {
                                float s = r.ReadSingle();
                                v += s;
                                if (c == 0) left = s;
                                else if (c == 1) { ll += left * left; rr += s * s; lr += left * s; }
                            }
                            v /= ch;
                            lo = Math.Min(lo, v);
                            hi = Math.Max(hi, v);
                        }
                        (t.Min[b], t.Max[b]) = (lo, hi);
                    }
                    if (ll + rr > 0)
                    {
                        t.Pan = (float)((rr - ll) / (rr + ll));
                        // One side silent: a hard-panned point source, no width (correlation undefined).
                        t.Width = Math.Min(ll, rr) < 1e-6 * (ll + rr) ? 0 : (float)Math.Clamp(1 - lr / Math.Sqrt(ll * rr), 0, 1);
                    }
                    return t;
                }
                else f.Seek(size + (size & 1), SeekOrigin.Current);
            }
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or UnauthorizedAccessException) { }
        return null;
    }

    /// min/max over [a, b) seconds.
    public (float, float) Peak(double a, double b)
    {
        int i0 = (int)Math.Clamp(a * Rate / Block, 0, Min.Length - 1), i1 = (int)Math.Clamp(b * Rate / Block, i0, Min.Length - 1);
        float lo = 0, hi = 0;
        for (int i = i0; i <= i1; i++) { lo = Math.Min(lo, Min[i]); hi = Math.Max(hi, Max[i]); }
        return (lo, hi);
    }
}

/// One pinned download: written to `dest`, SHA-256 checked (a mismatch deletes it). Returns null or why.
public static class Download
{
    public static async Task<string?> FileAsync(string url, string dest, long size, string sha256, Action<long> progress, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(dest))
            {
                var buf = new byte[1 << 16];
                long done = 0;
                for (int n; (n = await src.ReadAsync(buf, ct)) > 0;)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    progress(Math.Min(done, size));
                }
            }
            string hash;
            await using (var f = File.OpenRead(dest)) hash = Convert.ToHexString(await SHA256.HashDataAsync(f, ct)).ToLowerInvariant();
            if (hash != sha256) { File.Delete(dest); return "checksum does not match: download refused"; }
            return null;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException)
        {
            try { File.Delete(dest); } catch (IOException) { }
            return e is TaskCanceledException ? "cancelled" : e.Message;
        }
    }
}
