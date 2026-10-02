using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform.Storage;

namespace Dissonancia;

// STUDIO S1 — Session: library, playback, waveform (docs/studio-plan.md).

[StructLayout(LayoutKind.Sequential)]
struct PlayerInfoNative
{
    public ulong Frames, PositionFrame;
    public uint SampleRate;
    public ushort Channels;
    public byte Playing, Looping;
}

[StructLayout(LayoutKind.Sequential)]
struct EditSegmentNative
{
    public ulong SourceStart, SourceEnd;
    public float GainDb;
    uint _pad0;
}

static partial class PlayerApi
{
    const string Lib = "dissonancia";
    [LibraryImport(Lib, EntryPoint = "ana_player_create")] public static partial nint Create();
    [LibraryImport(Lib, EntryPoint = "ana_player_destroy")] public static partial void Destroy(nint p);
    [LibraryImport(Lib, EntryPoint = "ana_player_last_error")] public static partial nint LastError(nint p);
    [LibraryImport(Lib, EntryPoint = "ana_player_load", StringMarshalling = StringMarshalling.Utf8)] public static partial int Load(nint p, string path);
    [LibraryImport(Lib, EntryPoint = "ana_player_info")] public static partial void Info(nint p, out PlayerInfoNative info);
    [LibraryImport(Lib, EntryPoint = "ana_player_play")] public static partial void Play(nint p);
    [LibraryImport(Lib, EntryPoint = "ana_player_stop")] public static partial void Stop(nint p);
    [LibraryImport(Lib, EntryPoint = "ana_player_seek")] public static partial void Seek(nint p, ulong frame);
    [LibraryImport(Lib, EntryPoint = "ana_player_set_loop")] public static partial void SetLoop(nint p, ulong a, ulong b);
    [LibraryImport(Lib, EntryPoint = "ana_player_apply_edits")] public static partial int ApplyEdits(nint p, EditSegmentNative[] segs, int count, ulong fadeIn, ulong fadeOut, float normalizeDbfs);
    [LibraryImport(Lib, EntryPoint = "ana_player_save_wav", StringMarshalling = StringMarshalling.Utf8)] public static partial int SaveWav(nint p, string path);
    [LibraryImport(Lib, EntryPoint = "ana_player_peaks")] public static unsafe partial int Peaks(nint p, ulong a, ulong b, int columns, float* mn, float* mx);
}

/// The core's STUDIO player (own output device). Null when the native library is missing.
public sealed class StudioPlayer : IDisposable
{
    readonly nint _p;
    StudioPlayer(nint p) => _p = p;

    public static StudioPlayer? TryCreate()
    {
        try { nint p = PlayerApi.Create(); return p == 0 ? null : new StudioPlayer(p); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    public ulong Frames, Position;
    public uint Rate;
    public int Channels;
    public bool Playing, Looping;

    public string? Load(string path) =>
        PlayerApi.Load(_p, path) == 0 ? null : Marshal.PtrToStringUTF8(PlayerApi.LastError(_p)) ?? "load failed";

    public void Refresh()
    {
        PlayerApi.Info(_p, out var i);
        (Frames, Position, Rate, Channels, Playing, Looping) = (i.Frames, i.PositionFrame, i.SampleRate, i.Channels, i.Playing != 0, i.Looping != 0);
    }

    public void Play() => PlayerApi.Play(_p);
    public void Stop() => PlayerApi.Stop(_p);
    public void Seek(ulong frame) => PlayerApi.Seek(_p, frame);
    public void Loop(ulong a, ulong b) => PlayerApi.SetLoop(_p, a, b);

    /// Renders the edited take in the core (the original file is never changed).
    public void Apply(EditList e) =>
        PlayerApi.ApplyEdits(_p, e.Segments.Select(s => new EditSegmentNative { SourceStart = s.A, SourceEnd = s.B, GainDb = s.GainDb }).ToArray(),
            e.Segments.Count, e.FadeIn, e.FadeOut, e.Normalize ? -1f : 1f);

    public bool SaveWav(string path) => PlayerApi.SaveWav(_p, path) == 0;

    public unsafe void Peaks(ulong a, ulong b, float[] mn, float[] mx, int columns)
    {
        fixed (float* pmn = mn, pmx = mx) PlayerApi.Peaks(_p, a, b, columns, pmn, pmx);
    }

    public void Dispose() => PlayerApi.Destroy(_p);
}

[StructLayout(LayoutKind.Sequential)]
struct PostStatusNative
{
    public float Progress;
    public byte State;   // 0 idle, 1 running, 2 done, 3 failed, 4 cancelled
    byte _pad0, _pad1, _pad2;
}

static partial class PostApi
{
    const string Lib = "dissonancia";
    [LibraryImport(Lib, EntryPoint = "ana_post_create")] public static partial nint Create();
    [LibraryImport(Lib, EntryPoint = "ana_post_destroy")] public static partial void Destroy(nint h);
    [LibraryImport(Lib, EntryPoint = "ana_post_last_error")] public static partial nint LastError(nint h);
    [LibraryImport(Lib, EntryPoint = "ana_post_start", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Start(nint h, in SessionConfigNative s, double compensationSeconds, string inPath, string outJson, byte metronomeGrid);
    [LibraryImport(Lib, EntryPoint = "ana_post_status")] public static partial void Status(nint h, out PostStatusNative s);
    [LibraryImport(Lib, EntryPoint = "ana_post_cancel")] public static partial void Cancel(nint h);
}

/// The core's offline analysis job (one at a time).
public sealed class StudioJob : IDisposable
{
    readonly nint _h;
    StudioJob(nint h) => _h = h;

    public static StudioJob? TryCreate()
    {
        try { nint h = PostApi.Create(); return h == 0 ? null : new StudioJob(h); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    public enum State { Idle, Running, Done, Failed, Cancelled }

    /// metronomeGrid: the file starts on a downbeat of the session's tempo and meter (a REC take).
    public string? Start(Session s, double compensationSeconds, string input, string outJson, bool metronomeGrid) =>
        PostApi.Start(_h, NativeCore.Config(s), compensationSeconds, input, outJson, (byte)(metronomeGrid ? 1 : 0)) == 0 ? null : Error;

    public (State State, float Progress) Poll()
    {
        PostApi.Status(_h, out var st);
        return ((State)st.State, st.Progress);
    }

    public void Cancel() => PostApi.Cancel(_h);
    public string Error => Marshal.PtrToStringUTF8(PostApi.LastError(_h)) ?? "";
    public void Dispose() => PostApi.Destroy(_h);
}

/// Take project (S1): the options an offline result was made with, next to the result, under
/// <takes>/studio/. A result is reused while the source file and the options are unchanged.
public static class StudioProject
{
    public sealed record Project(string Source, long Size, long Modified, string Options, string Result, DateTime AnalyzedAt);

    static string Dir => System.IO.Path.Combine(Take.Folder, "studio");

    static string Stem(string source)
    {
        var full = System.IO.Path.GetFullPath(source);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(full)))[..8].ToLowerInvariant();
        return System.IO.Path.Combine(Dir, $"{System.IO.Path.GetFileNameWithoutExtension(source)}-{hash}");
    }

    public static string ResultPath(string source) => Stem(source) + ".events.json";
    static string ProjectPath(string source) => Stem(source) + ".studio.json";

    /// The options that change the result (the cache key).
    public static string Options(Session s, double comp) =>
        $"mode={s.Mode};quality={s.Quality};key={(s.KeySet ? s.Key.Fifths + (s.Key.Minor ? "m" : "M") : "none")};clef={s.CoreClef};meter={s.BeatsPerBar}/{s.BeatUnit};bpm={s.Bpm:0.###};comp={comp:0.####}";

    /// A cached result for this source and options, or null.
    public static string? Cached(string source, string options)
    {
        try
        {
            var p = JsonSerializer.Deserialize<Project>(File.ReadAllText(ProjectPath(source)));
            var fi = new FileInfo(source);
            return p is not null && p.Options == options && p.Size == fi.Length && p.Modified == fi.LastWriteTimeUtc.Ticks && File.Exists(p.Result) ? p.Result : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public static void Save(string source, string options)
    {
        var fi = new FileInfo(source);
        File.WriteAllText(ProjectPath(source), JsonSerializer.Serialize(new Project(source, fi.Length, fi.LastWriteTimeUtc.Ticks, options, ResultPath(source), DateTime.UtcNow),
            new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void EnsureDir() => Directory.CreateDirectory(Dir);

    // S2: the take's edit list, and the edited audio the analysis reads.
    static string EditsPath(string source) => Stem(source) + ".edits.json";
    public static string EditedWavPath(string source) => Stem(source) + ".edited.wav";

    public static EditList LoadEdits(string source)
    {
        try { return File.Exists(EditsPath(source)) ? JsonSerializer.Deserialize<EditList>(File.ReadAllText(EditsPath(source))) ?? new() : new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public static void SaveEdits(string source, EditList e)
    {
        EnsureDir();
        File.WriteAllText(EditsPath(source), JsonSerializer.Serialize(e));
    }

    /// A REC take carries its own session (mode, key, clef, meter, BPM) and round-trip latency in
    /// its sidecar; an imported file takes the START session, with no compensation.
    public static (Session Session, double Compensation, bool FromTake) SessionFor(string source, Session current)
    {
        var sidecar = System.IO.Path.ChangeExtension(source, ".json");
        if (File.Exists(sidecar))
        {
            try
            {
                var t = Take.Load(sidecar);
                using var doc = JsonDocument.Parse(File.ReadAllText(sidecar));
                double comp = doc.RootElement.TryGetProperty("compensationLatencyMs", out var c) ? c.GetDouble() / 1000 : 0;
                var s = new Session
                {
                    Mode = (AppMode)t.Mode, Quality = current.Quality, KeySet = t.KeySet, Key = new KeyOption(t.KeyFifths, t.Minor),
                    Clef = t.Clef, AutoClef = current.AutoClef && t.Clef == Clef.Treble, BeatsPerBar = t.BeatsPerBar, BeatUnit = t.BeatUnit, Bpm = (float)t.Bpm,
                };
                return (s, comp, true);
            }
            catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { }
        }
        var copy = new Session
        {
            Mode = current.Mode, Quality = current.Quality, KeySet = current.KeySet, Key = current.Key, Clef = current.Clef, AutoClef = current.AutoClef,
            BeatsPerBar = current.BeatsPerBar, BeatUnit = current.BeatUnit, Bpm = current.Bpm,
        };
        return (copy, 0, false);
    }
}

/// Library (S1): the REC takes in the takes folder plus imported files, remembered in library.json.
public static class StudioLibrary
{
    public sealed record Item(string Path, string Kind)
    {
        public string Name => System.IO.Path.GetFileName(Path);
    }

    static string FilePath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dissonancia", "library.json");
    public static readonly string[] Extensions = [".wav", ".flac", ".mp3"];

    public static List<Item> Load()
    {
        var items = new List<Item>();
        if (Directory.Exists(Take.Folder))
            items.AddRange(Directory.GetFiles(Take.Folder, "take-*.wav").OrderByDescending(File.GetLastWriteTimeUtc).Select(p => new Item(p, "REC")));
        foreach (var p in Imported())
            if (File.Exists(p)) items.Add(new Item(p, System.IO.Path.GetExtension(p).TrimStart('.').ToUpperInvariant()));
        return items;
    }

    static List<string> Imported()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? [] : []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    /// Imported files stay where they are; the library only remembers the path.
    public static void Import(string path)
    {
        var list = Imported();
        if (list.Contains(path)) return;
        list.Add(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(list));
    }
}


/// S2 edit list (non-destructive): source ranges in order with clip gain, fades over the edited take,
/// peak normalisation. Operations take a selection in EDITED frames. Kept per take, with undo.
public sealed class EditList
{
    public sealed record Seg(ulong A, ulong B, float GainDb);
    public List<Seg> Segments { get; set; } = [];
    public ulong FadeIn { get; set; }
    public ulong FadeOut { get; set; }
    public bool Normalize { get; set; }

    [System.Text.Json.Serialization.JsonIgnore] public bool IsEmpty => Segments.Count == 0 && FadeIn == 0 && FadeOut == 0 && !Normalize;
    public ulong Length(ulong original) => Segments.Count == 0 ? original : (ulong)Segments.Sum(s => (long)(s.B - s.A));
    public EditList Clone() => new() { Segments = [.. Segments], FadeIn = FadeIn, FadeOut = FadeOut, Normalize = Normalize };
    /// The take still starts where the original did, without cuts: the metronome grid holds.
    [System.Text.Json.Serialization.JsonIgnore] public bool KeepsGrid => Segments.Count <= 1 && (Segments.Count == 0 || Segments[0].A == 0);
    [System.Text.Json.Serialization.JsonIgnore] public string Key => System.Text.Json.JsonSerializer.Serialize(this);

    List<Seg> Base(ulong original) => Segments.Count == 0 ? [new Seg(0, original, 0)] : Segments;

    /// The pieces of the edited range [a, b), optionally with a gain change.
    List<Seg> Slice(ulong original, ulong a, ulong b, float gain = 0)
    {
        var outp = new List<Seg>();
        ulong pos = 0;
        foreach (var s in Base(original))
        {
            ulong len = s.B - s.A, lo = Math.Max(a, pos), hi = Math.Min(b, pos + len);
            if (hi > lo) outp.Add(new Seg(s.A + (lo - pos), s.A + (hi - pos), s.GainDb + gain));
            pos += len;
        }
        return outp;
    }

    public void Trim(ulong original, ulong a, ulong b) { Segments = Slice(original, a, b); Clamp(original); }
    public void Cut(ulong original, ulong a, ulong b) { Segments = [.. Slice(original, 0, a), .. Slice(original, b, ulong.MaxValue)]; Clamp(original); }
    public void Gain(ulong original, ulong a, ulong b, float db) =>
        Segments = [.. Slice(original, 0, a), .. Slice(original, a, b, db), .. Slice(original, b, ulong.MaxValue)];
    void Clamp(ulong original) { ulong n = Length(original); FadeIn = Math.Min(FadeIn, n); FadeOut = Math.Min(FadeOut, n); }
}
