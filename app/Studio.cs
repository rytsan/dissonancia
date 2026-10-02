using System.Runtime.InteropServices;
using System.Text.Json;

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

    // S4 mixing.
    public int AddTrack(string path) => MixApi.AddTrack(_p, path);
    public void ClearTracks() => MixApi.ClearTracks(_p);
    public void SetChannel(int track, ChannelMix c) => MixApi.SetChannel(_p, track, c.Native);
    public void SetMaster(MasterMix m) => MixApi.SetMaster(_p, m.Native);
    public bool RenderTap(uint mask, string path) => MixApi.RenderTap(_p, mask, path) == 0;
    public bool Bounce(string path) => MixApi.Bounce(_p, path) == 0;
    public unsafe void ReadMeters(MixMeters m)
    {
        MixApi.Meters(_p, out var n);
        m.Tracks = n.Tracks;
        for (int i = 0; i < 8; i++) { m.Peak[i] = n.PeakDb[i]; m.Rms[i] = n.RmsDb[i]; m.Gr[i] = n.CompGrDb[i]; m.GateOpen[i] = n.GateOpen[i] != 0; }
        for (int c = 0; c < 2; c++) { m.MasterPeak[c] = n.MasterPeakDb[c]; m.MasterRms[c] = n.MasterRmsDb[c]; }
        m.MasterGr = n.MasterCompGrDb;
        m.LimiterGr = n.LimiterGrDb;
    }

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
    public static partial int Start(nint h, in SessionConfigNative s, double compensationSeconds, string inPath, string outJson, byte flags);
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
    public string? Start(Session s, double compensationSeconds, string input, string outJson, bool metronomeGrid, bool beats = false) =>
        PostApi.Start(_h, NativeCore.Config(s), compensationSeconds, input, outJson, (byte)((metronomeGrid ? 1 : 0) | (beats ? 2 : 0))) == 0 ? null : Error;

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

    // suffix: one analysis among several of the same take (".voice", ".harmony" on separated stems).
    public static string ResultPath(string source, string suffix = "") => Stem(source) + suffix + ".events.json";
    static string ProjectPath(string source, string suffix = "") => Stem(source) + suffix + ".studio.json";
    public static string StemsDir(string source) => Stem(source) + "-stems";
    public static string LeadSheetPath(string source) => Stem(source) + ".leadsheet.json";
    public static string TapPath(string source, string channel) => Stem(source) + "." + channel + ".tap.wav";
    public static string BouncePath(string source) => Stem(source) + ".bounce.wav";
    static string MixPath(string source) => Stem(source) + ".mix.json";

    public static TakeMix LoadMix(string source)
    {
        try { return File.Exists(MixPath(source)) ? JsonSerializer.Deserialize<TakeMix>(File.ReadAllText(MixPath(source))) ?? new() : new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public static void SaveMix(string source, TakeMix m)
    {
        EnsureDir();
        File.WriteAllText(MixPath(source), JsonSerializer.Serialize(m));
    }

    /// The options that change the result (the cache key).
    public static string Options(Session s, double comp) =>
        $"mode={s.Mode};quality={s.Quality};key={(s.KeySet ? s.Key.Fifths + (s.Key.Minor ? "m" : "M") : "none")};clef={s.CoreClef};meter={s.BeatsPerBar}/{s.BeatUnit};bpm={s.Bpm:0.###};comp={comp:0.####}";

    /// A cached result for this source and options, or null.
    public static string? Cached(string source, string options, string suffix = "")
    {
        try
        {
            var p = JsonSerializer.Deserialize<Project>(File.ReadAllText(ProjectPath(source, suffix)));
            var fi = new FileInfo(source);
            return p is not null && p.Options == options && p.Size == fi.Length && p.Modified == fi.LastWriteTimeUtc.Ticks && File.Exists(p.Result) ? p.Result : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public static void Save(string source, string options, string suffix = "")
    {
        var fi = new FileInfo(source);
        File.WriteAllText(ProjectPath(source, suffix), JsonSerializer.Serialize(new Project(source, fi.Length, fi.LastWriteTimeUtc.Ticks, options, ResultPath(source, suffix), DateTime.UtcNow),
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
