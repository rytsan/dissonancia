using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Dissonancia;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--accel-check", var backend]) return Accel.CheckMain(backend);   // child of Accel.Install
        Accel.Init();
        Setup.StartAuto();   // whatever this machine should have and lacks, in the background
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
}

public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}

public sealed class MainWindow : Window
{
    readonly Session _session = new();
    readonly LiveFrame _frame = new();
    ILiveSource _source;
    readonly NativeCore? _core;
    readonly string _coreError;
    readonly TextBlock _startStatus = new() { Foreground = Ui.Label, TextWrapping = TextWrapping.Wrap };
    readonly RackModule[] _modules;
    readonly MelodyLaneModule _lane = new();   // sampled every frame, drawn or not
    readonly Dictionary<string, Action> _actions;
    readonly Dictionary<Key, string> _keys = new()
    {
        [Key.Space] = "Rec", [Key.M] = "Metronome", [Key.T] = "TapTempo", [Key.S] = "StageMode", [Key.Escape] = "LeaveStage",
    };
    readonly List<double> _taps = [];
    readonly TabControl _tabs = new();
    readonly TabItem _liveTab, _studioTab, _scoreTab;
    readonly PedalboardModule _pedals;
    readonly StudioView _studio;
    readonly RackView _rack;
    readonly StageView _stage = new();
    readonly TextBlock _scoreTitle = new() { FontWeight = FontWeight.Bold, Foreground = Ui.Label };
    readonly TextBlock _scoreStatus = new() { Foreground = Ui.Label, VerticalAlignment = VerticalAlignment.Center };
    Score? _score;
    readonly StackPanel _scorePages = new() { Spacing = 12 };
    Verovio? _verovio;
    string? _verovioError;
    readonly TextBlock _scoreText = new() { FontFamily = new FontFamily("Cascadia Mono, Consolas, DejaVu Sans Mono, monospace"), FontSize = 22, TextWrapping = TextWrapping.Wrap };
    readonly Stopwatch _clock = Stopwatch.StartNew();
    double _lastFrame;
    bool _stageMode;

    /// Time source; the screenshot tool overrides it to render a chosen instant.
    public Func<double> Clock { get; set; }

    public MainWindow()
    {
        Title = "Dissonância";
        Width = 1320; Height = 900;
        Background = Ui.RackBg;
        Clock = () => _clock.Elapsed.TotalSeconds;
        _source = new FakeLiveSource(_session);   // design data until START opens the core
        _core = NativeCore.TryLoad(out _coreError);
        Closed += (_, _) => { _core?.Dispose(); _verovio?.Dispose(); _studio.Close(); };

        var transport = new TransportModule();
        var catalog = new Dictionary<ModuleKind, RackModule>
        {
            [ModuleKind.Input] = new InputModule(), [ModuleKind.Scope] = new ScopeModule(), [ModuleKind.Analyzer] = new LcdModule(),
            [ModuleKind.Tuner] = new TunerModule(), [ModuleKind.Fretboard] = new FretboardModule(), [ModuleKind.Keyboard] = new KeyboardModule(),
            [ModuleKind.Waterfall] = new WaterfallModule(), [ModuleKind.Timeline] = new TimelineModule(), [ModuleKind.MelodyLane] = _lane,
            [ModuleKind.Transport] = transport, [ModuleKind.Status] = new StatusModule(),
            [ModuleKind.Metrics] = new MetricsModule(), [ModuleKind.Pads] = new PadsModule(), [ModuleKind.Sequencer] = new SequencerModule(), [ModuleKind.Midi] = new MidiModule(), [ModuleKind.Song] = new SongModule(), [ModuleKind.Pedals] = new PedalboardModule(),
        };
        ((SequencerModule)catalog[ModuleKind.Sequencer]).Song = (SongModule)catalog[ModuleKind.Song];
        ((SongModule)catalog[ModuleKind.Song]).Bounced += () =>
        {   // the timeline rendered, into STUDIO's library and on its tracks
            if (Playground.Current.Bounce(out var path) is { } err) { Playground.Current.Status = "bounce: " + err; return; }
            StudioLibrary.Import(path);
            _tabs.SelectedItem = _studioTab;
            _studio.OpenPath(path);
        };
        Playground.Current.Command += c =>
        {
            if (c == "rec") _source.ToggleRec(Clock());
            else if (c == "metro") _source.ToggleMetronome();
            else if (c.StartsWith("tempo:") && float.TryParse(c[6..], System.Globalization.CultureInfo.InvariantCulture, out var bpm))
            {   // the sequencer runs on the metronome: a MIDI file's tempo becomes the session's (refused by the core during REC)
                _session.Bpm = MathF.Round(Math.Clamp(bpm, 30, 300));
                _source.SetTempo(_session.Bpm);
            }
        };
        _pedals = (PedalboardModule)catalog[ModuleKind.Pedals];
        _modules = [.. catalog.Values, _stage];
        foreach (var m in _modules) { m.Frame = _frame; m.Session = _session; }
        _rack = new RackView(catalog);
        _rack.CountIn.Frame = _frame; _rack.CountIn.Session = _session;
        _rack.Load(_session.Live);

        // One action table (spec §22.6): keys, transport buttons (and later MIDI) all bind to it.
        _actions = new()
        {
            ["Rec"] = () => _source.ToggleRec(Clock()),
            ["Metronome"] = () => _source.ToggleMetronome(),
            ["TapTempo"] = TapTempo,
            ["StageMode"] = () => SetStage(!_stageMode),
            ["LeaveStage"] = () => SetStage(false),
            ["EditRack"] = _rack.ToggleEdit,
            ["Score"] = () => ShowScore(),
            ["Studio"] = () => _tabs.SelectedItem = _studioTab,
            ["Playground"] = () => _rack.SetPlayground(true),
            ["StudioOpenFirst"] = () => _studio.OpenFirst(),
            ["StudioOpenSeparated"] = () => _studio.OpenSeparated(),
            ["StudioAnalyze"] = () => _studio.AnalyzeCurrent(),
            ["StudioSeparate"] = () => _studio.SeparateCurrent(),
            ["StudioTick"] = () => _studio.Tick(),
            ["StudioRec"] = () => _studio.ToggleRec(),
            ["StudioPlugins"] = () => _studio.ShowPlugins(),
            ["StudioChoir"] = () => _studio.ChoirMode(),
            ["StudioOpenVst"] = () => _studio.OpenFirstInsert(),
            ["StudioAddVst"] = () => _studio.AddFirstPlugin(),   // screenshot tool: the first effect on the selected channel
            ["StudioConsole"] = () => _studio.ShowConsole(true),
            ["StudioPick"] = () => _studio.ReviewAction("pick"),
            ["StudioFixRoot"] = () => _studio.ReviewAction("root"),
            ["StudioFixQuality"] = () => _studio.ReviewAction("quality"),
            ["StudioApplyKey"] = () => _studio.ReviewAction("key"),
            ["StudioClearReview"] = () => _studio.ReviewAction("clear"),
            ["StudioDeliver"] = () => _studio.ReviewAction("deliver"),
            ["StudioScore"] = () => _studio.ScoreCurrent(),
            ["StudioPlayDemo"] = () => _studio.PlayHeadless(8, 12),   // screenshot tool: meters and tubes as if playing
            ["StudioSelect2"] = () => _studio.SelectChannel(2),
            ["StudioPatchDemo"] = () => _studio.PatchDemo(),
            ["StudioPullDemo"] = () => _studio.PullDemo(),
            ["StudioSelectDemo"] = () => _studio.SelectRange(1.0, 6.0),   // screenshot tool: a selection to edit
            ["StudioTrim"] = () => _studio.EditAction("trim"),
            ["StudioCut"] = () => _studio.EditAction("cut"),
            ["StudioNormalize"] = () => _studio.EditAction("normalize"),
            ["StudioUndo"] = () => _studio.EditAction("undo"),
            ["ScopeTrigger"] = ((ScopeModule)catalog[ModuleKind.Scope]).ToggleTrigger,
        };
        transport.RecPressed += _actions["Rec"];
        transport.MetronomePressed += _actions["Metronome"];
        transport.TapPressed += _actions["TapTempo"];
        transport.StudioPressed += _actions["Studio"];

        _liveTab = new TabItem { Header = "LIVE", Content = _rack };
        _scoreTab = new TabItem { Header = "SCORE", Content = BuildScoreTab() };
        _studio = new StudioView(_session);
        _studio.ScoreRequested += json => ShowScore(json);
        _studio.Input = new StudioState.InputHooks(() => _frame, StudioRec, InputName);
        _studioTab = new TabItem { Header = "STUDIO", Content = _studio };
        _tabs.ItemsSource = new[]
        {
            new TabItem { Header = "START", Content = new ScrollViewer { Content = BuildStartTab() } },
            _liveTab,
            _studioTab,
            _scoreTab,
        };
        Content = _tabs;
        _tabs.SelectionChanged += (_, _) =>
        {
            if (_tabs.SelectedIndex == 0) _syncStart();   // tap tempo may have moved
            if (_tabs.SelectedItem == _studioTab) _studio.EnterFromLive();   // the take just recorded in LIVE on the tracks
        };
        Opened += (_, _) => RequestAnimationFrame(OnFrame);
    }

    void OnFrame(TimeSpan _)
    {
        double now = Clock();
        if (_lastFrame > 0) _frame.DisplayMs = (float)((now - _lastFrame) * 1000);   // measured frame interval
        _lastFrame = now;
        _source.Read(_frame, now);
        _lane.Sample();
        Playground.Current.Bpm = _frame.Bpm;
        Playground.Current.Poll();
        SyncRack();
        if (_tabs.SelectedItem == _studioTab) _studio.Tick();
        if (_tabs.SelectedItem == _liveTab)
        {
            if (_stageMode) _stage.InvalidateVisual();
            else foreach (var m in _rack.Visible) m.InvalidateVisual();   // hidden modules are not rendered
            if (!_stageMode) _rack.CountIn.InvalidateVisual();   // draws nothing outside the count-in
        }
        RequestAnimationFrame(OnFrame);
    }

    /// START (spec §5): options are fixed for the session; a new START replaces the running session.
    void StartSession()
    {
        _recentNotes.Clear();
        _lastNote = -1;
        _session.ShownClef = Clef.Treble;
        if (_core is not null)
        {
            try
            {
                _source = _core.Start(_session);
                Playground.Current.Attach(_core.Handle, _session.ClickOutput);   // the pads at this input's rate, the pattern, the MIDI port
                _startStatus.Text = "";
            }
            catch (InvalidOperationException e)
            {
                _source = new FakeLiveSource(_session);
                _startStatus.Text = $"Audio start failed: {e.Message} — LIVE shows simulated data.";
                return;
            }
        }
        _tabs.SelectedItem = _liveTab;
    }

    /// REC from STUDIO: the core's recorder with the START options; opens the input if START has not.
    string? StudioRec()
    {
        if (_source is not NativeLiveSource)
        {
            if (_core is null) return $"núcleo nativo ausente ({_coreError})";
            try { _source = _core.Start(_session); Playground.Current.Attach(_core.Handle, _session.ClickOutput); }
            catch (InvalidOperationException e) { _source = new FakeLiveSource(_session); return "a entrada não abriu: " + e.Message; }
        }
        _source.ToggleRec(Clock());
        return (_source as NativeLiveSource)?.LastError;
    }

    string InputName()
    {
        var devices = _core?.CaptureDevices() ?? [];
        return _session.CaptureDevice >= 0 && _session.CaptureDevice < devices.Length ? devices[_session.CaptureDevice] : "padrão do sistema";
    }

    /// Default rack preset per mode (spec §22.4): the chord timeline only makes sense in chord modes.
    /// Preset follows the session mode; editing is locked while REC is armed or running.
    void SyncRack()
    {
        if (_rack.Mode != _session.Live) _rack.Load(_session.Live);
        _rack.SetLocked(_frame.Recording || _frame.CountingIn);
        FollowClef();
    }

    // Clef "Auto": the last 16 notes (melody, else the bass in chord modes) decide, with hysteresis.
    readonly List<int> _recentNotes = [];
    int _lastNote = -1;

    void FollowClef()
    {
        if (!_session.AutoClef) return;
        bool melody = _session.ShowsMelody;
        bool valid = melody ? _frame.MelodyValid && _frame.MelodySettled : _session.IsChordMode ? _frame.BassValid && _frame.BassSettled : _frame.NoteValid;
        int midi = valid ? (melody ? _frame.Melody : _session.IsChordMode ? _frame.Bass : _frame.Note).Midi : -1;
        if (midi < 0 || midi == _lastNote) { if (midi < 0) _lastNote = -1; return; }
        _lastNote = midi;
        _recentNotes.Add(midi);
        if (_recentNotes.Count > 16) _recentNotes.RemoveAt(0);
        if (_recentNotes.Count >= 4) _session.ShownClef = Theory.ClefForRange(_recentNotes, _session.ShownClef);
    }

    /// Tap tempo (spec §22.6): mean interval of the last taps (up to 5, reset after a 2 s pause); not during REC.
    void TapTempo()
    {
        if (_frame.Recording || _frame.CountingIn) return;
        double now = Clock();
        if (_taps.Count > 0 && now - _taps[^1] > 2) _taps.Clear();
        _taps.Add(now);
        if (_taps.Count > 5) _taps.RemoveAt(0);
        if (_taps.Count < 2) return;
        _session.Bpm = MathF.Round((float)Math.Clamp(60 * (_taps.Count - 1) / (_taps[^1] - _taps[0]), 30, 300));
        _source.SetTempo(_session.Bpm);
    }

    /// Screenshot tool entry: fill the frame at a fixed time without the animation loop.
    public void Step(double now)
    {
        _source.Read(_frame, now);
        _lane.Sample();
        Playground.Current.Bpm = _frame.Bpm;
        Playground.Current.Poll();
        SyncRack();
        foreach (var m in _modules) m.InvalidateVisual();
        _rack.CountIn.InvalidateVisual();
        if (_tabs.SelectedItem == _studioTab) _studio.Tick();
    }
    public void Action(string name) => _actions[name]();
    public void SelectTab(int index) => _tabs.SelectedIndex = index;
    public void SetMode(LiveMode mode, Clef? clef) { (_session.Live, _session.Mode) = (mode, mode.Core()); _session.AutoClef = clef is null; _session.Clef = clef ?? Clef.Treble; }
    public void ToggleRec(double now) => _source.ToggleRec(now);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_tabs.SelectedItem == _studioTab && e.Key == Key.Space) { _studio.TogglePlay(); e.Handled = true; return; }
        if (_tabs.SelectedItem == _studioTab && e.Key is Key.Delete or Key.Back && _studio.DeleteCable()) { e.Handled = true; return; }
        if (_tabs.SelectedItem == _liveTab && e.Key is Key.Delete or Key.Back && _pedals.DeleteCable()) { e.Handled = true; return; }
        if (_tabs.SelectedItem != _liveTab) { base.OnKeyDown(e); return; }
        if (!_keys.TryGetValue(e.Key, out var action)) { base.OnKeyDown(e); return; }
        _actions[action]();
        e.Handled = true;
    }

    void SetStage(bool on)
    {
        _stageMode = on;
        _liveTab.Content = on ? _stage : _rack;
        WindowState = on ? WindowState.FullScreen : WindowState.Normal;
    }

    /// sidecarPath: a take's JSON (STUDIO preview); null = the newest take.
    void ShowScore(string? sidecarPath = null)
    {
        _tabs.SelectedItem = _scoreTab;
        _score = null;
        _scoreStatus.Text = "";
        var path = sidecarPath is not null && File.Exists(sidecarPath) ? sidecarPath : Take.Latest();
        if (path is not null)
        {
            try
            {
                var take = Take.Load(path);
                if (_session.AutoClef) take.Clef = Theory.ClefForRange(take.Notes.Select(n => n.Midi).ToList());   // the take's own range
                _score = Score.Build(take, _session.SmallestNote, _session.Triplets);
                var t = _score.Take;
                var key = new KeyOption(t.KeyFifths, t.Minor);
                string cadences = string.Join("\n", t.Cadences.Select(c => $"  {Display(c.From, true)} → {Display(c.To, true)}   {c.Evidence}   {c.Confidence:0.00}"));
                _scoreTitle.Text = $"{Path.GetFileNameWithoutExtension(path)}  ·  {_score.Measures.Count} bars · {_score.QuantizedNotes.Count} notes · " +
                                   $"{_score.QuantizedChords.Count} chords{(t.EventLogComplete ? "" : "  ·  EVENT LOG INCOMPLETE")}";
                _scoreText.Text = $"{(t.KeySet ? key.Label : "no key")}   ·   {t.BeatsPerBar}/{t.BeatUnit}   ·   ♩ = {t.Bpm:0}   ·   " +
                                  $"clef {t.Clef.Name()}{(_session.AutoClef ? " (auto)" : "")}   ·   smallest value {NoteValueName(Score.Divisions * 4 / _score.Step)}{(_session.Triplets ? " + triplets" : "")}\n\n" +
                                  Display(_score.ChordChart()) + (_score.RomanLine().Length > 0 ? "\n\n" + Display(_score.RomanLine(), roman: true) : "") +
                                  (cadences.Length > 0 ? "\n\ncadences\n" + cadences : "") +
                                  (_score.QuantizedNotes.Count > 0 ? "\n\n" + string.Join("  ", _score.QuantizedNotes.Select(n => n.Note.Sounding.Name)) : "");
                RenderScore(_score);
                return;
            }
            catch (Exception e) { _scoreStatus.Text = $"could not read {Path.GetFileName(path)}: {e.Message}"; }
        }
        _scorePages.Children.Clear();
        // No take yet: fast path from the LIVE timeline (spec §20 entry A).
        var bars = _frame.RecordedBars.Count > 0 ? _frame.RecordedBars : _frame.TimelineBars;
        _scoreTitle.Text = "no REC take yet  ·  LIVE timeline";
        _scoreText.Text = $"{_session.Key.Label}   ·   {_session.BeatsPerBar}/{_session.BeatUnit}   ·   ♩ = {_session.Bpm:0}\n\n" +
                          string.Join("\n", bars.Chunk(4).Select(c => "| " + string.Join(" | ", c) + " |"));
    }

    /// Engraved score from the MusicXML writer through Verovio (SVG -> bitmap, paper on the dark face).
    void RenderScore(Score score)
    {
        _scorePages.Children.Clear();
        if (_verovio is null && _verovioError is null) { _verovio = Verovio.TryCreate(out var err); if (_verovio is null) _verovioError = err; }
        if (_verovio is null) { _scoreStatus.Text = _verovioError ?? ""; return; }
        int width = Math.Max(600, (int)(_tabs.Bounds.Width - 100));
        foreach (var svg in _verovio.Render(score.MusicXml(), width))
            if (SvgBitmap(svg) is { } bmp)
                _scorePages.Children.Add(new Border
                {
                    Background = Brushes.White, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new Image { Source = bmp, Width = bmp.Size.Width / 2, Height = bmp.Size.Height / 2 },
                });
        if (_scorePages.Children.Count == 0) _scoreStatus.Text = "Verovio could not engrave this take: " + _verovio.Log;
    }

    static Avalonia.Media.Imaging.Bitmap? SvgBitmap(string svg) =>
        SvgRaster.Png(svg, 2) is { } png ? new Avalonia.Media.Imaging.Bitmap(new MemoryStream(png)) : null;

    Button ExportButton()
    {
        var b = new Button { Content = "EXPORT  MusicXML · MIDI · JSON · TXT", Height = 40 };
        b.Click += (_, _) => ExportScore();
        return b;
    }

    void ExportScore()
    {
        if (_score is null) { _scoreStatus.Text = "record a take first (Space in LIVE)"; return; }
        try { _scoreStatus.Text = "exported: " + string.Join("  ", _score.Export().Select(Path.GetFileName)); }
        catch (Exception e) { _scoreStatus.Text = "export failed: " + e.Message; }
    }

    /// ASCII from the take -> display: chord and numeral text has no letter b other than flats.
    static string Display(string s, bool roman = false)
    {
        s = s.Replace('#', '♯').Replace('b', '♭');
        return roman ? s.Replace('o', '°').Replace('h', 'ø') : s;
    }

    Control BuildScoreTab() => new ScrollViewer { Content = new Border
    {
        Margin = new Thickness(16), Padding = new Thickness(24), Background = Ui.Face, CornerRadius = new CornerRadius(6),
        Child = new StackPanel
        {
            Spacing = 16,
            Children =
            {
                _scoreTitle,
                _scoreText,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 12,
                    Children = { ExportButton(), _scoreStatus },
                },
                new TextBlock { Text = "Exports next to the take: MusicXML (open in MuseScore), MIDI type 1, JSON (both timelines), text chart.", Foreground = Ui.Label },
                _scorePages,
            },
        },
    } };

    // ---------------- START tab (spec §22.1) ----------------

    Action _syncStart = () => { };   // START controls <- session (tap tempo)

    Control BuildStartTab()
    {
        var root = new StackPanel { Margin = new Thickness(24), Spacing = 18, MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Left };
        root.Children.Add(SystemSection());

        // Mode buttons
        // Clef per mode: voice and melody follow their range (Auto); guitar sounds an octave below
        // what is written (treble 8vb); piano uses the grand staff.
        var modes = new (LiveMode Mode, string Label, Clef? Clef)[]
        {
            (LiveMode.FullSong, "🎼  Música completa", Clef.Treble), (LiveMode.Melody, "🎤  Melodia", null),
            (LiveMode.Acoustic, "🎸  Acústico", Clef.Treble8vb), (LiveMode.AcousticVoice, "🎸🎤  Acústico + voz", null),
            (LiveMode.Piano, "🎹  Piano", Clef.Treble),
        };
        // Item 0 = Auto (by range), then the clefs in enum order.
        var clefBox = new ComboBox
        {
            ItemsSource = new[] { "Auto (by range)" }.Concat(Enum.GetValues<Clef>().Select(c => c.Name())).ToArray(),
            SelectedIndex = _session.AutoClef ? 0 : (int)_session.Clef + 1, Width = 180,
        };
        var clefNote = new TextBlock { Text = "piano: grand staff (split at C4)", Foreground = Ui.Label, VerticalAlignment = VerticalAlignment.Center };
        void ClefForMode() { clefBox.IsEnabled = !_session.GrandStaff; clefNote.IsVisible = _session.GrandStaff; }
        ClefForMode();
        var modeRow = new WrapPanel();
        var modeButtons = new List<ToggleButton>();
        foreach (var (mode, label, clef) in modes)
        {
            var b = new ToggleButton { Content = label, IsChecked = mode == _session.Live, Width = 200, Height = 64, Margin = new Thickness(0, 0, 10, 10), FontSize = 15 };
            b.Click += (_, _) =>
            {
                (_session.Live, _session.Mode) = (mode, mode.Core());
                foreach (var o in modeButtons) o.IsChecked = o == b;
                clefBox.SelectedIndex = clef is { } c ? (int)c + 1 : 0;   // default clef per mode, user can change (not on piano)
                ClefForMode();
            };
            modeButtons.Add(b);
            modeRow.Children.Add(b);
        }
        root.Children.Add(Section("MODE", modeRow));

        // Quality with computed T_low in ms
        var qualityRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var qualities = new (Quality Q, string Label)[] { (Quality.LowLatency, "LowLatency\nT low 173 ms"), (Quality.Balanced, "Balanced\nT low 210 ms"), (Quality.HighPrecision, "HighPrecision\nT low 420 ms") };
        foreach (var (q, label) in qualities)
        {
            var rb = new RadioButton { Content = label, GroupName = "quality", IsChecked = q == _session.Quality, MinWidth = 150 };
            rb.IsCheckedChanged += (_, _) => { if (rb.IsChecked == true) _session.Quality = q; };
            qualityRow.Children.Add(rb);
        }
        root.Children.Add(Section("QUALITY  (chord modes: lowest-bin window, measured values replace these)", qualityRow));

        // Key signature cascade + clef
        var modeBox = new ComboBox { ItemsSource = new[] { "Major", "minor" }, SelectedIndex = 0, Width = 120 };
        var keyBox = new ComboBox { ItemsSource = KeyOption.All(false), SelectedIndex = 1, Width = 220 };
        modeBox.SelectionChanged += (_, _) =>
        {
            int f = _session.Key.Fifths;
            var keys = KeyOption.All(modeBox.SelectedIndex == 1);
            keyBox.ItemsSource = keys;
            keyBox.SelectedItem = keys.First(k => k.Fifths == f);
        };
        keyBox.SelectionChanged += (_, _) => { if (keyBox.SelectedItem is KeyOption k) _session.Key = k; };
        clefBox.SelectionChanged += (_, _) =>
        {
            _session.AutoClef = clefBox.SelectedIndex <= 0;
            if (!_session.AutoClef) _session.Clef = (Clef)(clefBox.SelectedIndex - 1);
        };
        var useKey = new CheckBox { Content = "Key signature", IsChecked = _session.KeySet };
        void KeyEnabled() { modeBox.IsEnabled = keyBox.IsEnabled = _session.KeySet; }
        useKey.IsCheckedChanged += (_, _) => { _session.KeySet = useKey.IsChecked == true; KeyEnabled(); };
        KeyEnabled();
        root.Children.Add(Section("KEY SIGNATURE  ·  CLEF  (no key by default: plain chord names; a key adds roman numerals and cadences)",
            Row(Labeled(" ", useKey), Labeled("Mode", modeBox), Labeled("Key (circle of fifths)", keyBox), Labeled("Clef", clefBox), clefNote)));

        // Meter, tempo, metronome
        var meterBox = new ComboBox { ItemsSource = new[] { "4/4", "3/4", "2/4", "6/8", "12/8" }, SelectedIndex = 0, Width = 100 };
        meterBox.SelectionChanged += (_, _) =>
        {
            var parts = ((string)meterBox.SelectedItem!).Split('/');
            _session.BeatsPerBar = int.Parse(parts[0]); _session.BeatUnit = int.Parse(parts[1]);
        };
        var bpm = new NumericUpDown { Value = (decimal)_session.Bpm, Minimum = 30, Maximum = 300, Increment = 1, Width = 140, FormatString = "0" };
        bpm.ValueChanged += (_, _) => _session.Bpm = (float)(bpm.Value ?? 92);
        var countIn = new NumericUpDown { Value = _session.CountInBars, Minimum = 0, Maximum = 4, Increment = 1, Width = 120, FormatString = "0" };
        countIn.ValueChanged += (_, _) => _session.CountInBars = (int)(countIn.Value ?? 1);
        _syncStart = () => bpm.Value = (decimal)_session.Bpm;   // tap tempo
        var duringTake = new ComboBox { ItemsSource = new[] { "Count-in only (microphone)", "Whole take (headphones)" }, SelectedIndex = _session.ClickDuringTake ? 1 : 0, Width = 230 };
        duringTake.SelectionChanged += (_, _) => _session.ClickDuringTake = duringTake.SelectedIndex == 1;
        root.Children.Add(Section("METER  ·  TEMPO  ·  METRONOME  (REC always counts in; the beat LEDs keep the beat during the take)",
            Row(Labeled("Meter", meterBox), Labeled("BPM", bpm), Labeled("Count-in bars", countIn), Labeled("Click during the take", duringTake))));

        // Notation: like Finale's quantization settings. The grid is the shortest value written.
        int[] values = [0, 4, 8, 16];
        var smallest = new ComboBox
        {
            ItemsSource = new[] { "Beat unit (♩ in 4/4, ♪ in 6/8)", "Quarter ♩", "Eighth ♪", "Sixteenth" },
            SelectedIndex = Array.IndexOf(values, _session.SmallestNote), Width = 250,
        };
        smallest.SelectionChanged += (_, _) => _session.SmallestNote = values[Math.Max(0, smallest.SelectedIndex)];
        var triplets = new CheckBox { Content = "Allow eighth triplets", IsChecked = _session.Triplets };
        var inversions = new CheckBox { Content = "Show inversions in LIVE (D/F♯; off: D)", IsChecked = _session.ShowInversions };
        inversions.IsCheckedChanged += (_, _) => _session.ShowInversions = inversions.IsChecked == true;
        triplets.IsCheckedChanged += (_, _) => _session.Triplets = triplets.IsChecked == true;
        root.Children.Add(Section("NOTATION  (SCORE quantization: shorter notes are snapped to this grid; applies to the next SCORE view)",
            Row(Labeled("Smallest note value", smallest), Labeled(" ", triplets), Labeled(" ", inversions))));

        // Audio settings
        // Audio settings: requests only — the core reads back the real rate/period and the status strip shows them.
        var shareBox = new ComboBox { ItemsSource = new[] { "Shared", "Exclusive (WASAPI)" }, SelectedIndex = 0, Width = 180 };
        shareBox.SelectionChanged += (_, _) => _session.Exclusive = shareBox.SelectedIndex == 1;
        var devices = _core?.CaptureDevices() ?? [];
        var deviceBox = new ComboBox { ItemsSource = new[] { "Default input" }.Concat(devices).ToArray(), SelectedIndex = 0, Width = 260 };
        deviceBox.SelectionChanged += (_, _) => _session.CaptureDevice = deviceBox.SelectedIndex - 1;
        uint[] rates = [0, 44100, 48000, 96000];
        var rateBox = new ComboBox { ItemsSource = new[] { "Device native", "44100 Hz", "48000 Hz", "96000 Hz" }, SelectedIndex = 0, Width = 140 };
        rateBox.SelectionChanged += (_, _) => _session.SampleRate = rates[Math.Max(0, rateBox.SelectedIndex)];
        uint[] periods = [0, 64, 128, 256, 480];
        var periodBox = new ComboBox { ItemsSource = new[] { "5 ms (default)", "64 frames", "128 frames", "256 frames", "480 frames" }, SelectedIndex = 0, Width = 150 };
        periodBox.SelectionChanged += (_, _) => _session.PeriodFrames = periods[Math.Max(0, periodBox.SelectedIndex)];
        var clickBox = new ComboBox { ItemsSource = new[] { "Same device (recommended)", "Off" }, SelectedIndex = 0, Width = 220 };
        clickBox.SelectionChanged += (_, _) => _session.ClickOutput = clickBox.SelectedIndex == 0;
        root.Children.Add(Section("AUDIO",
            Row(Labeled("Mode", shareBox), Labeled("Input device", deviceBox), Labeled("Sample rate", rateBox),
                Labeled("Period", periodBox), Labeled("Metronome out", clickBox))));

        var start = new Button { Content = "START  ▶", FontSize = 20, Width = 240, Height = 60, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        start.Click += (_, _) => StartSession();
        _startStatus.Text = _core is null ? $"Core not loaded ({_coreError}) — LIVE shows simulated data." : "";
        root.Children.Add(start);
        root.Children.Add(_startStatus);
        return root;
    }

    /// SYSTEM: this machine, its GPU backend (installed on demand, checked against the CPU) and
    /// the optional components, each present or missing with why.
    Control SystemSection()
    {
        var machine = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var accel = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var parts = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Ui.Label };
        var install = new Button { Height = 36 };
        var auto = new CheckBox { Content = "Install automatically at start", IsChecked = Setup.Skipped is not "turned off" };
        var bar = new ProgressBar { Width = 320, Height = 8, Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center };
        var status = new TextBlock { Foreground = Ui.Label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 600 };
        var action = Row(install, bar, status);
        static string Mb(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes >> 20} MB";
        bool wasRunning = false;
        void Show()
        {
            var p = Accel.Machine;
            machine.Text = $"{p.Os} · {p.Arch} · {p.Cores} cores · {Mb(p.RamMb << 20)} RAM"
                         + (p.Gpu is null ? " · no NVIDIA GPU found" : $" · {p.Gpu} {Mb(p.VramMb << 20)} · driver {p.Driver}");
            string device = p.Backend switch { "cuda" => "CUDA", "directml" => "DirectML", "coreml" => "CoreML", _ => "CPU" };
            var check = p.Backend is "cpu" ? null : Accel.Passed(p.Backend);
            accel.Text = p.Backend is "cpu" ? "Acceleration: CPU only — " + string.Join("; ", p.Issues)
                       : check is not null ? $"Acceleration: {device} {(Accel.ActiveDevice == device ? "active" : "ready on the next start")} — checked against the CPU (max difference {check.MaxDiff:g2}; Basic Pitch {check.CpuMs:0} ms on the CPU, {check.GpuMs:0.0} ms on the GPU)"
                       : p.Missing.Length > 0 ? $"Acceleration: {device} available — needs {string.Join(", ", p.Missing.Select(m => m.Name))}"
                       : $"Acceleration: {device} installed, not checked yet";
            parts.Text = string.Join("   ·   ",
                $"Basic Pitch {(BasicPitch.Available ? "present" : "missing")}",
                $"Demucs separation {(!DemucsModel.HelperPresent ? "not in this build" : DemucsModel.Installed(DemucsModel.FourStems) && DemucsModel.Installed(DemucsModel.SixStems) ? "present" : "weights missing")}",
                $"YourMT3+ {(YourMt3.Installed ? "present" : !YourMt3.Shipped ? "not in this build" : "weights not built yet")}",
                $"Playground sounds (VCSL, CC0) {(SampleBank.Installed ? "present" : "missing")}");
            var (needed, bytes) = Setup.Pending();
            install.IsVisible = Setup.Running || needed;
            install.Content = Setup.Running ? "CANCEL" : $"INSTALL  ({Mb(bytes)})";
            bar.IsVisible = Setup.Running;
            bar.Value = Setup.Progress;
            status.Text = Setup.Running ? Setup.Stage : Setup.Error ?? (needed && Setup.Skipped is { } why ? "not installed automatically: " + why : "");
            action.IsVisible = install.IsVisible || status.Text is { Length: > 0 };
        }
        install.Click += (_, _) => { if (Setup.Running) Setup.Cancel(); else Setup.Start(); Show(); };
        auto.IsCheckedChanged += (_, _) => { Setup.SetAuto(auto.IsChecked == true); if (auto.IsChecked == true) Setup.StartAuto(); Show(); };
        // The setup runs in the background (started at launch); follow it while it runs.
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => { if (Setup.Running || wasRunning) Show(); wasRunning = Setup.Running; };
        timer.Start();
        Show();
        return Section("SYSTEM  (what this machine has and what it should have: the GPU build for it and the separation weights, downloaded once, checksum-pinned; the GPU is used only after it agrees with the CPU)",
            new StackPanel { Spacing = 8, Children = { machine, accel, action, parts, auto } });
    }

    static string NoteValueName(int denominator) => denominator switch { 4 => "quarter ♩", 8 => "eighth ♪", _ => "sixteenth" };

    static Control Section(string title, Control content) => new Border
    {
        Background = Ui.Face, BorderBrush = new SolidColorBrush(Color.FromUInt32(0xFF3C4046)), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6), Padding = new Thickness(16),
        Child = new StackPanel { Spacing = 10, Children = { new TextBlock { Text = title, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Ui.Label }, content } },
    };

    static Control Row(params Control[] items)
    {
        var p = new WrapPanel();
        foreach (var i in items) { i.Margin = new Thickness(0, 0, 18, 6); p.Children.Add(i); }
        return p;
    }

    static Control Labeled(string label, Control c) => new StackPanel
    {
        Spacing = 4, Children = { new TextBlock { Text = label, FontSize = 11, Foreground = Ui.Label }, c },
    };
}
