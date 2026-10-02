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

/// Waveform of the loaded file: peaks for the visible range from the core's mipmap, playback
/// cursor, selection (drag) and zoom (wheel, around the pointer; shift + wheel pans).
public sealed class WaveformView : Control
{
    public StudioPlayer? Player { get; set; }
    public double ViewStart, ViewEnd;           // frames
    public double SelStart = -1, SelEnd = -1;   // frames, -1 = none
    public event Action<ulong>? Seek;
    public event Action? SelectionChanged;

    float[] _mn = new float[4096], _mx = new float[4096];
    double _dragFrom = -1;

    static readonly IBrush Bg = new ImmutableSolidColorBrush(Color.FromUInt32(0xFF0D0E10));
    static readonly IBrush Fill = new ImmutableSolidColorBrush(Color.FromUInt32(0x663DDC84));
    static readonly IPen Trace = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF3DDC84)), 1);
    static readonly IBrush Sel = new ImmutableSolidColorBrush(Color.FromUInt32(0x22FFB000));
    static readonly IPen SelEdge = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFFFFB000)), 1);
    static readonly IPen Cursor = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFFFF3B30)), 2);
    static readonly IPen Grid = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF2A2D32)), 1);

    public void Fit() { ViewStart = 0; ViewEnd = Math.Max(1, Player?.Frames ?? 1); SelStart = SelEnd = -1; InvalidateVisual(); }

    double FrameAt(double x) => ViewStart + Math.Clamp(x / Math.Max(1, Bounds.Width), 0, 1) * (ViewEnd - ViewStart);
    double XAt(double frame) => (frame - ViewStart) / Math.Max(1, ViewEnd - ViewStart) * Bounds.Width;

    public override void Render(DrawingContext ctx)
    {
        var r = new Rect(Bounds.Size);
        ctx.DrawRectangle(Bg, null, r, 4, 4);
        var p = Player;
        if (p is null || p.Frames == 0) { Ui.Text(ctx, "abra um take ou arquivo da biblioteca", r.Center.X, r.Center.Y - 8, 13, Ui.Label, Ui.Sans, Ui.Align.Center); return; }

        // Second ticks
        double secs = (ViewEnd - ViewStart) / p.Rate, step = secs > 120 ? 30 : secs > 40 ? 10 : secs > 8 ? 2 : secs > 2 ? 0.5 : 0.1;
        for (double t = Math.Ceiling(ViewStart / p.Rate / step) * step; t * p.Rate < ViewEnd; t += step)
        {
            double x = XAt(t * p.Rate);
            ctx.DrawLine(Grid, new Point(x, 0), new Point(x, r.Height));
            Ui.Text(ctx, $"{t:0.#} s", x + 3, 2, 9, Ui.Label, Ui.Mono);
        }

        int cols = Math.Clamp((int)r.Width, 1, _mn.Length);
        p.Peaks((ulong)Math.Max(0, ViewStart), (ulong)Math.Max(0, ViewEnd), _mn, _mx, cols);
        double mid = r.Height / 2, amp = r.Height * 0.45;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(0, mid - _mx[0] * amp), true);
            for (int i = 1; i < cols; i++) c.LineTo(new Point(i, mid - _mx[i] * amp));
            for (int i = cols - 1; i >= 0; i--) c.LineTo(new Point(i, mid - _mn[i] * amp));
            c.EndFigure(true);
        }
        ctx.DrawGeometry(Fill, Trace, g);

        if (SelStart >= 0 && SelEnd > SelStart)
        {
            var s = new Rect(XAt(SelStart), 0, Math.Max(1, XAt(SelEnd) - XAt(SelStart)), r.Height);
            ctx.DrawRectangle(Sel, null, s);
            ctx.DrawLine(SelEdge, s.TopLeft, s.BottomLeft);
            ctx.DrawLine(SelEdge, s.TopRight, s.BottomRight);
        }
        double cx = XAt(p.Position);
        if (cx >= 0 && cx <= r.Width) ctx.DrawLine(Cursor, new Point(cx, 0), new Point(cx, r.Height));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _dragFrom = FrameAt(e.GetPosition(this).X);
        SelStart = SelEnd = -1;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_dragFrom < 0) return;
        double f = FrameAt(e.GetPosition(this).X);
        if (Math.Abs(XAt(f) - XAt(_dragFrom)) < 3) return;
        (SelStart, SelEnd) = (Math.Min(_dragFrom, f), Math.Max(_dragFrom, f));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_dragFrom < 0) return;
        if (SelStart < 0) Seek?.Invoke((ulong)_dragFrom);   // a click places the cursor
        else SelectionChanged?.Invoke();
        _dragFrom = -1;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (Player is not { Frames: > 0 } p) return;
        double span = ViewEnd - ViewStart, at = FrameAt(e.GetPosition(this).X);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            double shift = -e.Delta.Y * span * 0.1;
            ViewStart += shift; ViewEnd += shift;
        }
        else
        {
            double k = e.Delta.Y > 0 ? 0.8 : 1.25, ns = Math.Clamp(span * k, 64, p.Frames);
            ViewStart = at - (at - ViewStart) * ns / span;
            ViewEnd = ViewStart + ns;
        }
        double len = ViewEnd - ViewStart;
        if (ViewStart < 0) { ViewStart = 0; ViewEnd = len; }
        if (ViewEnd > p.Frames) { ViewEnd = p.Frames; ViewStart = Math.Max(0, p.Frames - len); }
        InvalidateVisual();
        e.Handled = true;
    }
}

/// The STUDIO tab (S1): stage bar, library, waveform, transport.
public sealed class StudioView : DockPanel
{
    readonly StudioPlayer? _player = StudioPlayer.TryCreate();
    readonly StudioJob? _job = StudioJob.TryCreate();
    readonly Session _session;
    readonly ComboBox _mode = new() { Width = 200, ItemsSource = new[] { "Voz", "Melodia (instrumento)", "Acordes · violão", "Acordes · piano", "Acordes · geral" } };
    readonly Button _analyze = new() { Content = "ANALISAR (offline)", Height = 36 };
    readonly Button _cancel = new() { Content = "CANCELAR", Height = 36, IsEnabled = false };
    readonly ProgressBar _progress = new() { Width = 220, Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _analysis = new() { Foreground = Ui.Label, VerticalAlignment = VerticalAlignment.Center };
    string? _result, _jobOptions, _jobSource;
    bool _running;
    readonly WaveformView _wave = new();
    readonly ListBox _library = new() { Width = 280 };
    readonly TextBlock _title = new() { FontSize = 18, FontWeight = FontWeight.Bold, Foreground = Ui.LabelBright };
    readonly TextBlock _info = new() { Foreground = Ui.Label, FontFamily = new FontFamily("Cascadia Mono, Consolas, DejaVu Sans Mono, monospace") };
    readonly TextBlock _time = new() { FontSize = 22, Foreground = Ui.LabelBright, FontFamily = new FontFamily("Cascadia Mono, Consolas, DejaVu Sans Mono, monospace") };
    readonly TextBlock _status = new() { Foreground = Ui.Label };
    readonly Button _play = new() { Content = "▶  PLAY", Width = 120, Height = 40 };
    readonly Button _loop = new() { Content = "LOOP SELEÇÃO", Height = 40 };
    string? _current;

    /// Opens SCORE from a take JSON: the offline result, or the raw take's sidecar as a preview.
    public event Action<string>? ScoreRequested;

    public StudioView(Session session)
    {
        _session = session;
        var stages = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(16, 12) };
        string[] names = ["1 SESSÃO", "2 EDIÇÃO", "3 SEPARAÇÃO", "4 MIXAGEM", "5 TRANSCRIÇÃO", "6 REVISÃO", "7 ENTREGA"];
        for (int i = 0; i < names.Length; i++)
            stages.Children.Add(new Border
            {
                Padding = new Thickness(12, 6), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1),
                BorderBrush = i == 0 ? Ui.Amber : new SolidColorBrush(Color.FromUInt32(0xFF3C4046)),
                Background = i == 0 ? new SolidColorBrush(Color.FromUInt32(0xFF3A2A06)) : Brushes.Transparent,
                Child = new TextBlock { Text = names[i], FontWeight = FontWeight.Bold, FontSize = 12, Foreground = i == 0 ? Ui.LabelBright : Ui.Label },
            });
        stages.Children.Add(new TextBlock { Text = "próximos marcos: edição, separação, mixagem…", Foreground = Ui.Label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0) });
        SetDock(stages, Dock.Top);
        Children.Add(stages);

        var import = new Button { Content = "IMPORTAR ARQUIVO", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        import.Click += async (_, _) => await ImportAsync();
        var refresh = new Button { Content = "ATUALIZAR", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        refresh.Click += (_, _) => ReloadLibrary();
        _library.SelectionChanged += (_, _) => { if (_library.SelectedItem is ListBoxItem { Tag: StudioLibrary.Item it }) Open(it.Path); };
        var left = new DockPanel { Margin = new Thickness(16, 0, 8, 16) };
        var buttons = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0), Children = { import, refresh } };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var libTitle = new TextBlock { Text = "BIBLIOTECA", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Ui.Label, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(libTitle, Dock.Top);
        left.Children.Add(libTitle);
        left.Children.Add(buttons);
        left.Children.Add(_library);
        SetDock(left, Dock.Left);
        Children.Add(left);

        _wave.Player = _player;
        _wave.Seek += f => { _player?.Seek(f); };
        _wave.SelectionChanged += () => { if (_player is not null && _wave.SelEnd > _wave.SelStart) _player.Seek((ulong)_wave.SelStart); };
        _play.Click += (_, _) => TogglePlay();
        _loop.Click += (_, _) => ToggleLoop();
        var fit = new Button { Content = "ZOOM TOTAL", Height = 40 };
        fit.Click += (_, _) => _wave.Fit();
        var score = new Button { Content = "SCORE", Height = 40 };
        score.Click += (_, _) => OpenScore();
        _analyze.Click += (_, _) => Analyze();
        _cancel.Click += (_, _) => _job?.Cancel();
        var analysisRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 10),
            Children = { new TextBlock { Text = "ANÁLISE", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Ui.Label, VerticalAlignment = VerticalAlignment.Center }, _mode, _analyze, _cancel, _progress, _analysis },
        };
        DockPanel.SetDock(analysisRow, Dock.Top);
        var transport = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _play, _loop, fit, _time, score } };
        _time.VerticalAlignment = VerticalAlignment.Center;
        _time.Margin = new Thickness(12, 0);

        var center = new DockPanel { Margin = new Thickness(8, 0, 16, 16) };
        var head = new StackPanel { Spacing = 4, Children = { _title, _info } };
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(transport, Dock.Top);
        transport.Margin = new Thickness(0, 10);
        var help = new TextBlock { Text = "clique: posiciona · arraste: seleciona · roda: zoom · shift + roda: rola · espaço: play / stop", Foreground = Ui.Label, Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(help, Dock.Bottom);
        DockPanel.SetDock(_status, Dock.Bottom);
        center.Children.Add(head);
        center.Children.Add(transport);
        center.Children.Add(analysisRow);
        center.Children.Add(_status);
        center.Children.Add(help);
        center.Children.Add(new Border { MinHeight = 200, Child = _wave });
        Children.Add(center);

        if (_player is null) _status.Text = "núcleo nativo ausente: biblioteca sem reprodução nem análise";
        _analyze.IsEnabled = _job is not null;
        ReloadLibrary();
    }

    void ReloadLibrary()
    {
        _library.ItemsSource = StudioLibrary.Load().Select(it => new ListBoxItem
        {
            Tag = it,
            Content = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = it.Name, FontWeight = FontWeight.Bold, FontSize = 12 },
                    new TextBlock { Text = $"{it.Kind} · {File.GetLastWriteTime(it.Path):dd/MM HH:mm}", FontSize = 11, Foreground = Ui.Label },
                },
            },
        }).ToList();
    }

    async Task ImportAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } sp) return;
        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Importar áudio", AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Áudio") { Patterns = StudioLibrary.Extensions.Select(e => "*" + e).ToArray() }],
        });
        foreach (var f in files)
            if (f.TryGetLocalPath() is { } path) StudioLibrary.Import(path);
        ReloadLibrary();
    }

    public void Open(string path)
    {
        if (_player is null) return;
        _player.Stop();
        var err = _player.Load(path);
        _current = path;
        _player.Refresh();
        _title.Text = Path.GetFileName(path);
        var (s, comp, fromTake) = StudioProject.SessionFor(path, _session);
        _mode.SelectedIndex = (int)s.Mode;
        _result = StudioProject.Cached(path, StudioProject.Options(s, comp) + (fromTake ? ";grid" : "") + ";decoder=2");
        _progress.Value = _result is null ? 0 : 1;
        _analysis.Text = (fromTake ? "sessão do take" : "sessão da START") + (_result is null ? " · não analisado" : " · resultado em cache");
        _info.Text = err ?? $"{_player.Frames / (double)Math.Max(1, _player.Rate):0.00} s · {_player.Rate} Hz · {(_player.Channels == 1 ? "mono" : "estéreo")}";
        _status.Text = err is null ? "" : "não foi possível abrir: " + err;
        _wave.Fit();
    }

    Session AnalysisSession(out double comp, out bool fromTake)
    {
        var (s, c, take) = StudioProject.SessionFor(_current!, _session);
        s.Mode = (AppMode)Math.Max(0, _mode.SelectedIndex);
        comp = c;
        fromTake = take;
        return s;
    }

    public void AnalyzeCurrent() => Analyze();

    void Analyze()
    {
        if (_job is null || _current is null || _running) return;
        var s = AnalysisSession(out double comp, out bool fromTake);
        var options = StudioProject.Options(s, comp) + (fromTake ? ";grid" : "") + ";decoder=2";   // decoder 2: whole-take Viterbi
        if (StudioProject.Cached(_current, options) is { } cached) { _result = cached; _analysis.Text = "resultado em cache (mesmas opções)"; _progress.Value = 1; return; }
        StudioProject.EnsureDir();
        var err = _job.Start(s, comp, _current, StudioProject.ResultPath(_current), fromTake);   // a REC take is on its metronome grid
        if (err is not null) { _analysis.Text = "falhou: " + err; return; }
        (_running, _jobOptions, _jobSource, _result) = (true, options, _current, null);
        _analyze.IsEnabled = false;
        _cancel.IsEnabled = true;
        _analysis.Text = "analisando…";
    }

    void PollJob()
    {
        if (!_running || _job is null) return;
        var (state, progress) = _job.Poll();
        _progress.Value = progress;
        if (state == StudioJob.State.Running) return;
        _running = false;
        _analyze.IsEnabled = true;
        _cancel.IsEnabled = false;
        if (state == StudioJob.State.Done && _jobSource is not null && _jobOptions is not null)
        {
            StudioProject.Save(_jobSource, _jobOptions);
            if (_jobSource == _current) _result = StudioProject.ResultPath(_jobSource);
            try
            {
                var t = Take.Load(StudioProject.ResultPath(_jobSource));
                _analysis.Text = $"pronto · {t.Notes.Count} notas · {t.Chords.Count} acordes";
            }
            catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException) { _analysis.Text = "pronto"; }
        }
        else _analysis.Text = state == StudioJob.State.Cancelled ? "cancelado" : "falhou: " + _job.Error;
    }

    public void ScoreCurrent() => OpenScore();

    void OpenScore()
    {
        if (_current is null) return;
        if (_result is not null) { ScoreRequested?.Invoke(_result); return; }
        var sidecar = Path.ChangeExtension(_current, ".json");
        if (File.Exists(sidecar)) ScoreRequested?.Invoke(sidecar);   // raw take preview
        else _status.Text = "analise o arquivo primeiro (ANALISAR)";
    }

    /// Opens the first library item (screenshot tool, first visit).
    public void OpenFirst() { if (_library.ItemCount > 0) _library.SelectedIndex = 0; }

    public void TogglePlay()
    {
        if (_player is null) return;
        _player.Refresh();
        if (_player.Playing) _player.Stop();
        else
        {
            if (_player.Position >= _player.Frames) _player.Seek(0);
            _player.Play();
        }
    }

    void ToggleLoop()
    {
        if (_player is null) return;
        _player.Refresh();
        if (_player.Looping || _wave.SelEnd <= _wave.SelStart) _player.Loop(0, 0);
        else { _player.Loop((ulong)_wave.SelStart, (ulong)_wave.SelEnd); _player.Seek((ulong)_wave.SelStart); }
    }

    /// Animation tick from the window: cursor, time, button labels.
    public void Tick()
    {
        PollJob();
        if (_player is null) return;
        _player.Refresh();
        _play.Content = _player.Playing ? "■  STOP" : "▶  PLAY";
        _loop.Content = _player.Looping ? "LOOP: ON" : "LOOP SELEÇÃO";
        var t = TimeSpan.FromSeconds(_player.Position / (double)Math.Max(1, _player.Rate));
        _time.Text = $"{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
        _wave.InvalidateVisual();
    }

    public void Close() { _player?.Stop(); _player?.Dispose(); _job?.Dispose(); }
}
