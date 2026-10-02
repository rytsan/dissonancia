using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform.Storage;

namespace Dissonancia;

// The STUDIO tab as Dissonância's own 19" rack (docs/studio-plan.md, layout): DS-T transport,
// DS-L library, DS-A track recorder (with the S2 edit keys), DS-C channel chain ending in the
// analyser. Same visual language as the LIVE rack: screwed faces, plasma dot-matrix readouts,
// phosphor screens, LEDs.

/// Everything the STUDIO units share: the file open, its session, edits, playback, analysis.
public sealed class StudioState
{
    public readonly StudioPlayer? Player = StudioPlayer.TryCreate();
    public readonly StudioJob? Job = StudioJob.TryCreate();
    public readonly Session Defaults;
    public List<StudioLibrary.Item> Library = [];
    public string? Current;
    public ulong OriginalFrames;
    public Session Session = new();
    public double Compensation;
    public bool FromTake;
    public EditList Edits = new();
    readonly Stack<EditList> _undo = new();
    public double ViewStart, ViewEnd, SelStart = -1, SelEnd = -1;   // edited frames
    public AppMode Mode;
    public string? Result;           // take JSON of the offline analysis (or the stems' lead sheet) of the current edits, reviewed
    public Take? ResultTake;

    // ---------------------------------------------------------------- S6 review
    public Review Review = new();
    public (KeyOption Key, double Correlation, double Margin)? Suggestion;
    public (string Kind, double Start)? Picked;   // the event being corrected

    void SaveReview() { StudioProject.EnsureDir(); Review.Save(StudioProject.ReviewPath(Current!)); }

    /// Key and meter are the user's: the suggestion becomes the key only here, and the analysis
    /// runs again with it (the key weighs chord choices and spells the notes).
    public void ApplyKey(KeyOption? key)
    {
        if (Current is null) return;
        Review.KeySet = key is not null;
        if (key is not null) (Review.KeyFifths, Review.KeyMinor) = (key.Fifths, key.Minor);
        SaveReview();
        OverlayReview();
        RefreshResult();
        Status = (key is null ? "sem armadura" : "tom " + key.Label) + (Result is null ? " · ANALISAR para transcrever com ele" : "");
        Changed?.Invoke();
    }

    public void ToggleTempoSource()
    {
        if (Current is null || ResultTake is null && !Review.TempoFromDrums) return;
        Review.TempoFromDrums = !Review.TempoFromDrums;
        SaveReview();
        RefreshResult();
        Changed?.Invoke();
    }

    void OverlayReview()
    {
        if (Review.KeySet is bool k) { Session.KeySet = k; if (k) Session.Key = new KeyOption(Review.KeyFifths, Review.KeyMinor); }
    }

    public void Pick(string? kind, double start = 0) { Picked = kind is null ? null : (kind, start); Changed?.Invoke(); }

    TakeChord? PickedChord => Picked is ("chord", var s) ? ResultTake?.Chords.MinBy(c => Math.Abs(c.Start - s)) : null;
    TakeNote? PickedNote => Picked is (var k, var s) && k != "chord" ? (k == "bass" ? ResultTake?.BassNotes : ResultTake?.Notes)?.MinBy(n => Math.Abs(n.Start - s)) : null;
    public string PickedLabel => PickedChord is { } c ? c.Symbol : PickedNote is { } n ? n.Sounding.Name : "";

    public void FixChord(int rootStep = 0, bool nextQuality = false, bool nextBass = false)
    {
        if (PickedChord is not { } c || Picked is not { } p) return;
        int root = (c.Root + rootStep + 12) % 12;
        int[] cycle = [0, 1, 7, 9, 8, 2, 10, 11, 5, 4, 6, 12, 13, 3, 14];   // common first: maj, m, 7, m7, maj7, dim, m7b5, dim7, sus4, sus2, 5, 6, m6, aug, add9
        int q = nextQuality ? cycle[(Array.IndexOf(cycle, c.Quality) + 1) % cycle.Length] : c.Quality;
        int? bass = null;
        if (nextBass)
        {   // root position -> each chord tone in turn
            var tones = LeadSheet.Intervals[Math.Clamp(q, 0, LeadSheet.Intervals.Length - 1)].Select(i => (root + i) % 12).ToArray();
            int at = Array.IndexOf(tones, c.Bass < 0 ? root : c.Bass);
            bass = tones[(at + 1) % tones.Length];
        }
        Review.Add(new Review.Fix("chord", p.Start, Root: rootStep != 0 ? root : null, Quality: nextQuality ? q : null, Bass: bass));
        AfterFix();
    }

    public void FixNote(int semitones)
    {
        if (PickedNote is not { } n || Picked is not { } p) return;
        Review.Add(new Review.Fix(p.Kind, p.Start, Midi: n.Midi + semitones));
        AfterFix();
    }

    public void DeletePicked()
    {
        if (Picked is not { } p) return;
        Review.Add(new Review.Fix(p.Kind, p.Start, Delete: true));
        Picked = null;
        AfterFix();
    }

    /// Back to the analysis for every event (key and tempo stay).
    public void ClearFixes() { Review.Fixes.Clear(); AfterFix(); }

    void AfterFix() { SaveReview(); RefreshResult(); Status = $"{Review.Fixes.Count} correções guardadas neste take"; Changed?.Invoke(); }

    /// The next spot to check (confidence under 0.5, not yet corrected), after the playhead.
    public void NextDoubt()
    {
        if (ResultTake is not { } t || Player is null) return;
        double now = Player.Position / Rate - Compensation + 0.05;
        var spots = t.Chords.Where(c => c.Confidence < 0.5f).Select(c => ("chord", c.Start))
            .Concat(t.Notes.Where(n => n.Confidence < 0.5f).Select(n => ("note", n.Start))).OrderBy(x => x.Start).ToList();
        if (spots.Count == 0) { Status = "nenhum ponto duvidoso"; Changed?.Invoke(); return; }
        var next = spots.FirstOrDefault(x => x.Start > now, spots[0]);
        Picked = next;
        Player.Seek((ulong)Math.Max(0, (next.Start + Compensation) * Rate));
        Status = $"{spots.Count} pontos duvidosos · este: {PickedLabel}";
        Changed?.Invoke();
    }
    public int Doubts => ResultTake is { } t ? t.Chords.Count(c => c.Confidence < 0.5f) + t.Notes.Count(n => n.Confidence < 0.5f) : 0;
    public bool Running;
    public float Progress;
    public string Status = "";

    public event Action<string>? ScoreRequested;
    public event Action? Changed;

    public StudioState(Session defaults) { Defaults = defaults; ReloadLibrary(); }

    public void ReloadLibrary() => Library = StudioLibrary.Load();

    public double Rate => Math.Max(1, Player?.Rate ?? 1);
    public double Beat => 60.0 / Math.Max(20, Session.Bpm);
    public double Bar => Beat * Math.Max(1, Session.BeatsPerBar);
    /// Musical time 0 (the first downbeat) in seconds of the take: a REC take's events are
    /// compensated by the round-trip latency, so its bar lines sit that much into the file.
    public double GridOrigin => FromTake ? Compensation : 0;

    public void Open(string path)
    {
        if (Player is null) { Status = "núcleo nativo ausente: sem reprodução nem análise"; return; }
        Player.Stop();
        var err = Player.Load(path);
        Current = path;
        Player.Refresh();
        OriginalFrames = Player.Frames;
        (Session, Compensation, FromTake) = StudioProject.SessionFor(path, Defaults);
        Mode = Session.Mode;
        Edits = StudioProject.LoadEdits(path);
        Mix = StudioProject.LoadMix(path);
        Review = Review.Load(StudioProject.ReviewPath(path));
        OverlayReview();
        Picked = null;
        Selected = 0;
        _undo.Clear();
        if (!Edits.IsEmpty) Player.Apply(Edits);
        Player.Refresh();
        Fit();
        Status = err is null ? "" : "não foi possível abrir: " + err;
        LoadStems();
        RefreshResult();
    }

    public void Fit() { ViewStart = 0; ViewEnd = Math.Max(1, Player?.Frames ?? 1); SelStart = SelEnd = -1; }

    // ---------------------------------------------------------------- S2 edits

    public bool HasSelection => SelStart >= 0 && SelEnd > SelStart;

    void Edit(Action<EditList> change)
    {
        if (Player is null || Current is null) return;
        _undo.Push(Edits.Clone());
        change(Edits);
        Commit();
    }

    void Commit()
    {
        Player!.Apply(Edits);
        Player.Refresh();
        StudioProject.SaveEdits(Current!, Edits);
        Fit();
        LoadStems();   // stems of other edits no longer apply
        RefreshResult();
        Changed?.Invoke();
    }

    public void Trim() { if (HasSelection) Edit(e => e.Trim(OriginalFrames, (ulong)SelStart, (ulong)SelEnd)); }
    public void Cut() { if (HasSelection) Edit(e => e.Cut(OriginalFrames, (ulong)SelStart, (ulong)SelEnd)); }
    public void Gain(float db) { if (HasSelection) Edit(e => e.Gain(OriginalFrames, (ulong)SelStart, (ulong)SelEnd, db)); }
    public void FadeIn() { if (HasSelection) Edit(e => e.FadeIn = (ulong)SelEnd); }
    public void FadeOut() { if (HasSelection) Edit(e => e.FadeOut = (ulong)Math.Max(0, (Player?.Frames ?? 0) - SelStart)); }
    public void Normalize() => Edit(e => e.Normalize = !e.Normalize);
    public void Undo() { if (_undo.Count > 0 && Player is not null) { Edits = _undo.Pop(); Commit(); } }
    public void Original() => Edit(e => { e.Segments = []; e.FadeIn = e.FadeOut = 0; e.Normalize = false; });

    // ---------------------------------------------------------------- playback

    public void TogglePlay()
    {
        if (Player is null) return;
        Player.Refresh();
        if (Player.Playing) { Player.Stop(); return; }
        if (Player.Position >= Player.Frames) Player.Seek(0);
        Player.Play();
    }

    public void ToggleLoop()
    {
        if (Player is null) return;
        Player.Refresh();
        if (Player.Looping || !HasSelection) Player.Loop(0, 0);
        else { Player.Loop((ulong)SelStart, (ulong)SelEnd); Player.Seek((ulong)SelStart); }
    }

    // ---------------------------------------------------------------- S3 separation

    public List<StemTrack> Stems = [];
    public DemucsModel.Variant Variant = DemucsModel.FourStems;
    SeparationJob? _sep;
    CancellationTokenSource? _install;
    public bool Separating => _sep is { Running: true } || _install is not null;
    public float SepProgress;
    public bool DemucsReady => DemucsModel.HelperPresent && DemucsModel.Installed(Variant);
    string StemsKey => Variant.File + ";" + Edits.Key;

    void LoadStems()
    {
        Stems = [];
        Player?.ClearTracks();
        if (Current is not null)
        {
            var dir = StudioProject.StemsDir(Current);
            var key = Path.Combine(dir, "key.txt");
            if (File.Exists(key) && File.ReadAllText(key) == StemsKey)   // separated from these edits and this model
                foreach (var name in SeparationJob.StemOrder)
                    if (StemTrack.Load(name, Path.Combine(dir, name + ".wav")) is { } t && Player?.AddTrack(t.Path) > 0) Stems.Add(t);
        }
        if (Selected > Stems.Count) Selected = 0;
        ApplyMix();
    }

    // ---------------------------------------------------------------- S4 mixing

    public TakeMix Mix = new();
    public readonly MixMeters Meters = new();
    public int Selected;   // channel on the chain unit: 0 = the take, 1.. = stems
    public string NameOf(int track) => track == 0 ? "mix" : Stems[track - 1].Name;
    public int Tracks => 1 + Stems.Count;
    public ChannelMix Channel(int track) => Mix.For(NameOf(track), Stems.Count > 0);

    void ApplyMix()
    {
        if (Player is null) return;
        for (int t = 0; t < Tracks; t++) Player.SetChannel(t, Channel(t));
        Player.SetMaster(Mix.Master);
    }

    /// A strip or the master changed in the UI: audible from the next block, kept with the take.
    public void MixChanged()
    {
        if (Current is null) return;
        ApplyMix();
        StudioProject.SaveMix(Current, Mix);
        RefreshResult();   // the analysis tap of an edited chain is another input
        Changed?.Invoke();
    }

    public void Bounce()
    {
        if (Player is null || Current is null) return;
        StudioProject.EnsureDir();
        var path = StudioProject.BouncePath(Current);
        Status = Player.Bounce(path) ? "bounce: " + path : "bounce falhou";
        Changed?.Invoke();
    }

    public void PollMeters() { if (Player is not null) Player.ReadMeters(Meters); }
    public void Select() => Changed?.Invoke();
    public bool ConsoleView;

    /// Installs the weights if needed (one download, checksum-pinned), then separates the edited take.
    public async void Separate()
    {
        if (Current is null || Player is null || Separating || Running) return;
        if (!DemucsModel.HelperPresent) { Status = "separação ausente nesta instalação (core sem DZ_WITH_DEMUCS)"; Changed?.Invoke(); return; }
        if (!DemucsModel.Installed(Variant))
        {
            _install = new CancellationTokenSource();
            Status = $"baixando o modelo Demucs ({Variant.Size / 1_000_000} MB, uma vez)…";
            var err = await DemucsModel.InstallAsync(Variant, f => SepProgress = f * 0.1f, _install.Token);
            _install = null;
            if (err is not null) { Status = "modelo não instalado: " + err; Changed?.Invoke(); return; }
        }
        StudioProject.EnsureDir();
        string input = Current;
        if (!Edits.IsEmpty) { input = StudioProject.EditedWavPath(Current); if (!Player.SaveWav(input)) { Status = "não foi possível gravar o áudio editado"; return; } }
        var dir = StudioProject.StemsDir(Current);
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { }
        _sep = SeparationJob.Start(Variant, input, dir, out var e);
        Status = _sep is null ? "falhou: " + e : "separando em faixas (Demucs)…";
        Changed?.Invoke();
    }

    void PollSeparation()
    {
        if (_sep is null) return;
        SepProgress = 0.1f + 0.9f * _sep.Progress;
        if (_sep.Running) return;
        var job = _sep;
        _sep = null;
        if (job.Succeeded && Current is not null)
        {
            File.WriteAllText(Path.Combine(StudioProject.StemsDir(Current), "key.txt"), StemsKey);
            LoadStems();
            RefreshResult();
            Status = $"separado: {string.Join(", ", Stems.Select(t => t.Name))}";
        }
        else Status = job.Error.Length > 0 ? "separação falhou: " + job.Error : "separação cancelada";
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- analysis

    /// One analysis of the queue: a session (mode), the tracks whose analysis tap it reads, its result suffix.
    sealed record Pass(Session Session, uint Mask, string Input, string Suffix, string Options, bool Beats = false);
    readonly Queue<Pass> _queue = new();
    Pass? _pass;
    string? _passSource;

    string Options(Session s, string extra = "") =>
        StudioProject.Options(s, Compensation) + (FromTake && Edits.KeepsGrid ? ";grid" : "") + ";decoder=2;edits=" + Edits.Key + extra;

    Session AnalysisSession(AppMode? mode = null)
    {
        var s = Session;
        return new Session
        {
            Mode = mode ?? Mode, Quality = s.Quality, KeySet = s.KeySet, Key = s.Key, Clef = s.Clef, AutoClef = s.AutoClef,
            BeatsPerBar = s.BeatsPerBar, BeatUnit = s.BeatUnit, Bpm = s.Bpm,
        };
    }

    AppMode HarmonyMode => Mode is AppMode.VoiceMono or AppMode.InstrumentMono ? AppMode.GeneralChords : Mode;

    /// The analyses this take gets: with stems, the voice on the vocals stem and the harmony on
    /// other + bass (+ guitar + piano); without, the mix in the selected mode.
    List<Pass> Plan()
    {
        if (Current is null) return [];
        if (Stems.Count == 0)
        {
            var s = AnalysisSession();
            return [new Pass(s, 1, StudioProject.TapPath(Current, "mix"), "", Options(s, ";tap=" + Channel(0).TapKey))];
        }
        uint Mask(params string[] names) => (uint)Enumerable.Range(1, Stems.Count).Where(t => names.Contains(Stems[t - 1].Name)).Sum(t => 1 << t);
        string Keys(uint mask) => string.Join("|", Enumerable.Range(0, Tracks).Where(t => (mask >> t & 1) != 0).Select(t => NameOf(t) + ":" + Channel(t).TapKey));
        var voice = AnalysisSession(AppMode.VoiceMono);
        var harmony = AnalysisSession(HarmonyMode);
        uint vm = Mask("vocals"), hm = Mask("other", "bass", "guitar", "piano"), bm = Mask("bass");
        var passes = new List<Pass>
        {
            new(voice, vm, StudioProject.TapPath(Current, "voice"), ".voice", Options(voice, ";stems=" + StemsKey + ";tap=" + Keys(vm))),
            new(harmony, hm, StudioProject.TapPath(Current, "harmony"), ".harmony", Options(harmony, ";stems=" + StemsKey + ";tap=" + Keys(hm))),
        };
        if (bm != 0)
        {   // the bass line: a melody pipeline over the low range (HighPrecision: down to 40 Hz)
            var bassS = AnalysisSession(AppMode.InstrumentMono);
            bassS.Quality = Quality.HighPrecision;
            bassS.AutoClef = false;
            bassS.Clef = Clef.Bass;
            passes.Add(new(bassS, bm, StudioProject.TapPath(Current, "bass"), ".bass", Options(bassS, ";stems=" + StemsKey + ";tap=" + Keys(bm))));
        }
        if (Mask("drums") is var dm and not 0)   // the beats of the drums: the tempo map, drift against the metronome
            passes.Add(new(voice, dm, StudioProject.TapPath(Current, "drums"), ".beats", Options(voice, ";beats;stems=" + StemsKey + ";tap=" + Keys(dm)), true));
        return passes;
    }

    void RefreshResult()
    {
        Result = null;
        ResultTake = null;
        if (Current is null) return;
        var plan = Plan();
        var done = plan.Select(p => StudioProject.Cached(Current, p.Options, p.Suffix)).ToList();
        if (done.Any(d => d is null)) return;
        Result = done.Count == 1 ? done[0] : MergeLeadSheet(plan.Zip(done, (p, d) => (p.Suffix, d!)).ToDictionary());
        Suggestion = null;
        if (Result is null) return;
        try
        {
            Suggestion = Review.SuggestKey(Take.Load(Result));   // from the analysis, before any correction
            if (!Review.IsEmpty)
            {   // S6: key, tempo and corrections over the analysis, never in it
                var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Result))!.AsObject();
                Review.Apply(root);
                File.WriteAllText(StudioProject.ReviewedPath(Current), root.ToJsonString());
                Result = StudioProject.ReviewedPath(Current);
            }
            ResultTake = Take.Load(Result);
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { }
    }

    /// Melody (voice notes) + harmony (chords) of the separated stems in one take JSON for SCORE.
    string? MergeLeadSheet(Dictionary<string, string> results)
    {
        try
        {
            static System.Text.Json.Nodes.JsonNode Read(string p) => System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(p))!;
            var bass = results.TryGetValue(".bass", out var b) ? Read(b)["events"]!.AsArray() : null;
            var root = LeadSheet.Merge(Read(results[".harmony"]).AsObject(), Read(results[".voice"])["events"]!.AsArray(), bass);
            if (results.TryGetValue(".beats", out var d)) LeadSheet.AddBeats(root, Read(d).AsObject(), FromTake && Edits.KeepsGrid ? Session.Bpm : 0);
            var path = StudioProject.LeadSheetPath(Current!);
            File.WriteAllText(path, root.ToJsonString());
            return path;
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or NullReferenceException) { return null; }
    }

    public void CycleMode() { Mode = (AppMode)(((int)Mode + 1) % 5); RefreshResult(); }

    public void Analyze()
    {
        if (Job is null || Player is null || Current is null || Running || Separating) return;
        RefreshResult();
        if (Result is not null) { Status = "resultado em cache (mesmas opções e edições)"; Changed?.Invoke(); return; }
        StudioProject.EnsureDir();
        _queue.Clear();
        foreach (var p in Plan())
        {
            if (StudioProject.Cached(Current, p.Options, p.Suffix) is not null) continue;
            // The transcription reads the channel at its tap: post-inserts, pre-fader (the edited
            // take, through trim, filters, gate, EQ and compressor; stems summed when several).
            if (!Player.RenderTap(p.Mask, p.Input)) { Status = "não foi possível renderizar o canal"; return; }
            _queue.Enqueue(p);
        }
        _passSource = Current;
        Next();
    }

    void Next()
    {
        if (Job is null || _passSource is null) return;
        if (!_queue.TryDequeue(out var p)) { Running = false; _pass = null; RefreshResult(); FinishStatus(); return; }
        var err = Job.Start(p.Session, Compensation, p.Input, StudioProject.ResultPath(_passSource, p.Suffix), FromTake && Edits.KeepsGrid, p.Beats);
        if (err is not null) { Running = false; Status = "falhou: " + err; return; }
        (_pass, Running) = (p, true);
        Status = p.Suffix switch { ".voice" => "transcrevendo a voz…", ".harmony" => "transcrevendo a harmonia…", ".bass" => "transcrevendo o baixo…", ".beats" => "rastreando a bateria…", _ => "analisando…" };
    }

    void FinishStatus() =>
        Status = ResultTake is { } t ? $"pronto · {t.Notes.Count} notas · {t.Chords.Count} acordes" + (t.BassNotes.Count > 0 ? $" · {t.BassNotes.Count} do baixo" : "")
            + (t.Beats.Count > 0 ? $" · bateria {t.TempoBpm:0.0} BPM" + (t.MaxDriftMs is double dr ? $", deriva máx {dr:0} ms" + (dr > LeadSheet.DriftFlagMs ? " (fora do metrônomo)" : "") : "") : "")
            + (Stems.Count > 0 ? " · das faixas separadas" : "") : "pronto";

    public void Cancel() { _queue.Clear(); Job?.Cancel(); _sep?.Cancel(); _install?.Cancel(); }

    public void Poll()
    {
        PollSeparation();
        if (!Running || Job is null || _pass is null) return;
        var (state, progress) = Job.Poll();
        Progress = progress;
        if (state == StudioJob.State.Running) return;
        if (state == StudioJob.State.Done && _passSource is not null) { StudioProject.Save(_passSource, _pass.Options, _pass.Suffix); Next(); }
        else { Running = false; _queue.Clear(); Status = state == StudioJob.State.Cancelled ? "cancelado" : "falhou: " + Job.Error; }
        Changed?.Invoke();
    }

    public void OpenScore()
    {
        if (Current is null) return;
        if (Result is not null) { ScoreRequested?.Invoke(Result); return; }
        var sidecar = Path.ChangeExtension(Current, ".json");
        if (File.Exists(sidecar) && Edits.IsEmpty && Stems.Count == 0) ScoreRequested?.Invoke(sidecar);   // raw take preview
        else Status = "analise primeiro (DA · ANALISAR)";
    }

    /// S7 delivery: everything for the take in one folder — score (MusicXML, MIDI with a track per
    /// part, JSON, chord chart), the mix bounce and the separated stems.
    public string? Deliver()
    {
        if (Current is null || ResultTake is null || Player is null) return null;
        var dir = Path.Combine(Take.Folder, "entregas", Path.GetFileNameWithoutExtension(Current));
        try
        {
            Directory.CreateDirectory(dir);
            var name = Path.Combine(dir, Path.GetFileNameWithoutExtension(Current));
            var files = Score.Build(ResultTake, Defaults.SmallestNote, Defaults.Triplets).Export(name).ToList();
            if (Player.Bounce(name + ".mix.wav")) files.Add(name + ".mix.wav");
            // ponytail: stems as separated (before their strips); a per-channel bounce needs a track mask in the core's bounce.
            foreach (var st in Stems) { var to = $"{name}.{st.Name}.wav"; File.Copy(st.Path, to, true); files.Add(to); }
            Status = $"entregue: {files.Count} arquivos em {dir}";
            Changed?.Invoke();
            return dir;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Status = "entrega falhou: " + e.Message; Changed?.Invoke(); return null; }
    }

    public void Close() { Cancel(); Player?.Stop(); Player?.Dispose(); Job?.Dispose(); }
}

/// Base of every STUDIO rack unit: screwed face, engraved maker + model + name, hardware keys.
public abstract class StudioUnit : Control
{
    protected readonly StudioState S;
    readonly string _model, _name;
    readonly List<(Rect R, Action A)> _keys = [];

    protected StudioUnit(StudioState s, string model, string name) { S = s; _model = model; _name = name; }

    public static readonly IBrush Phosphor = new ImmutableSolidColorBrush(Color.FromUInt32(0xFF9DFFC8));
    public static readonly IBrush PhosphorDim = new ImmutableSolidColorBrush(Color.FromUInt32(0xFF3E6B53));
    public static readonly IBrush PhosphorBg = new ImmutableSolidColorBrush(Color.FromUInt32(0xFF07130D));
    public static readonly IPen PhosphorLine = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF1B3326)), 1);
    public static readonly IPen PhosphorTrace = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF9DFFC8)), 1);
    static readonly IBrush KeyFace = new ImmutableLinearGradientBrush(
        [new ImmutableGradientStop(0, Color.FromUInt32(0xFF4A4F56)), new ImmutableGradientStop(1, Color.FromUInt32(0xFF2C2F34))],
        startPoint: new RelativePoint(0, 0, RelativeUnit.Relative), endPoint: new RelativePoint(0, 1, RelativeUnit.Relative));
    static readonly IPen KeyEdge = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF0B0C0D)), 1);
    static readonly IBrush Engrave = new ImmutableSolidColorBrush(Color.FromUInt32(0xFF6B7078));

    public sealed override void Render(DrawingContext ctx)
    {
        _keys.Clear();
        _controls.Clear();
        var b = new Rect(Bounds.Size).Deflate(2);
        ctx.DrawRectangle(Ui.Face, Ui.FaceEdge, b, 4, 4);
        foreach (var p in new[] { b.TopLeft + new Point(9, 9), b.TopRight + new Point(-9, 9), b.BottomLeft + new Point(9, -9), b.BottomRight + new Point(-9, -9) })
        {
            ctx.DrawEllipse(Ui.Screw, null, p, 3.6, 3.6);
            ctx.DrawLine(Ui.ScrewSlot, p + new Point(-2.2, -2.2), p + new Point(2.2, 2.2));
        }
        double x = b.X + 24;
        x += Ui.Text(ctx, "DISSONÂNCIA", x, b.Y + 4, 8, Engrave, Ui.SansBold).Width + 8;
        x += Ui.Text(ctx, _model, x, b.Y + 4, 8, Ui.Amber, Ui.SansBold).Width + 8;
        Ui.Text(ctx, _name, x, b.Y + 4, 8, Ui.LabelBright, Ui.SansBold);
        DrawContent(ctx, new Rect(b.X + 22, b.Y + 20, b.Width - 44, b.Height - 30));
    }

    protected abstract void DrawContent(DrawingContext ctx, Rect r);

    /// A hardware key with its LED; registered for clicks.
    protected void Key(DrawingContext ctx, Rect r, string label, Action action, bool lit = false, IBrush? led = null, bool enabled = true)
    {
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF0B0C0D)), null, r.Translate(new Vector(0, 2)), 3, 3);
        ctx.DrawRectangle(KeyFace, KeyEdge, r, 3, 3);
        Ui.Text(ctx, label, r.Center.X, r.Center.Y - 7, 11, enabled ? Ui.LabelBright : Ui.Label, Ui.SansBold, Ui.Align.Center);
        Ui.Led(ctx, new Point(r.Right - 6, r.Y + 6), 2.6, lit, led ?? Ui.Green, Ui.GreenOff);
        if (enabled) _keys.Add((r, action));
    }

    /// A plasma readout in its dark window (as the LIVE analyser).
    protected static double Readout(DrawingContext ctx, double x, double y, string text, int cells, double dot, string label)
    {
        double w = Plasma.CellWidth(dot) * cells + 12, h = dot * 1.3 * 7 + 10;
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF120804)), Ui.FaceEdge, new Rect(x, y, w, h), 2, 2);
        Plasma.DotText(ctx, text, x + 6, y + 5, dot, cells);
        Ui.Text(ctx, label, x + w / 2, y + h + 2, 7, Ui.Label, Ui.SansBold, Ui.Align.Center);
        return w;
    }

    /// Masking-tape scribble strip, as on a real desk.
    protected static void Tape(DrawingContext ctx, double x, double y, string text, double angle = -1.5)
    {
        using (ctx.PushTransform(Matrix.CreateTranslation(-x, -y) * Matrix.CreateRotation(angle * Math.PI / 180) * Matrix.CreateTranslation(x, y)))
        {
            var tf = new Typeface("Permanent Marker, Comic Sans MS, Segoe Print, DejaVu Sans", FontStyle.Italic, FontWeight.Bold);
            var size = Ui.Text(ctx, text, -1000, -1000, 13, Brushes.Transparent, tf);
            ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFFE9DFB8)), null, new Rect(x, y, size.Width + 14, size.Height + 4), 1, 1);
            Ui.Text(ctx, text, x + 7, y + 1, 13, new ImmutableSolidColorBrush(Color.FromUInt32(0xFF1F2A44)), tf);
        }
    }

    // Knobs and faders: a value in 0..1 with its setter. Knobs follow a vertical drag (200 px =
    // the whole range) and the wheel; a fader follows the pointer along its slot.
    sealed record Control1(Rect R, double Norm, Action<double> Set, bool Fader);
    readonly List<Control1> _controls = [];
    Control1? _drag;
    double _dragY, _dragNorm;

    static readonly IBrush KnobBody = new ImmutableRadialGradientBrush(
        [new ImmutableGradientStop(0, Color.FromUInt32(0xFF6B7078)), new ImmutableGradientStop(1, Color.FromUInt32(0xFF24272B))],
        center: new RelativePoint(0.38, 0.32, RelativeUnit.Relative), radiusX: new RelativeScalar(0.75, RelativeUnit.Relative), radiusY: new RelativeScalar(0.75, RelativeUnit.Relative));
    static readonly IPen KnobRim = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF0B0C0D)), 1.2);
    static readonly IPen KnobMark = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFFFFB000)), 2.4, lineCap: PenLineCap.Round);
    static readonly IPen KnobMarkOff = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF6B7078)), 2.4, lineCap: PenLineCap.Round);
    static readonly IPen ArcPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF3A2A06)), 2);

    /// A rack knob: -135° .. +135°, amber index when its section is on.
    protected void Knob(DrawingContext ctx, Point c, double r, string label, string value, double norm, Action<double> set, bool on = true)
    {
        norm = Math.Clamp(norm, 0, 1);
        for (int i = 0; i <= 10; i++)   // scale ticks
        {
            double a = (-135 + 27 * i) * Math.PI / 180;
            ctx.DrawLine(ArcPen, new Point(c.X + Math.Sin(a) * (r + 3), c.Y - Math.Cos(a) * (r + 3)), new Point(c.X + Math.Sin(a) * (r + 6), c.Y - Math.Cos(a) * (r + 6)));
        }
        ctx.DrawEllipse(KnobBody, KnobRim, c, r, r);
        double ang = (-135 + 270 * norm) * Math.PI / 180;
        ctx.DrawLine(on ? KnobMark : KnobMarkOff, new Point(c.X + Math.Sin(ang) * r * 0.25, c.Y - Math.Cos(ang) * r * 0.25), new Point(c.X + Math.Sin(ang) * r * 0.9, c.Y - Math.Cos(ang) * r * 0.9));
        Ui.Text(ctx, value, c.X, c.Y + r + 6, 9, on ? Ui.LabelBright : Ui.Label, Ui.Mono, Ui.Align.Center);
        Ui.Text(ctx, label, c.X, c.Y + r + 18, 7, Ui.Label, Ui.SansBold, Ui.Align.Center);
        _controls.Add(new Control1(new Rect(c.X - r - 6, c.Y - r - 6, 2 * r + 12, 2 * r + 12), norm, set, false));
    }

    static readonly IBrush Slot = new ImmutableSolidColorBrush(Color.FromUInt32(0xFF0A0B0C));
    static readonly IBrush Cap = new ImmutableLinearGradientBrush(
        [new ImmutableGradientStop(0, Color.FromUInt32(0xFFB9BEC5)), new ImmutableGradientStop(0.5, Color.FromUInt32(0xFF6B7078)), new ImmutableGradientStop(1, Color.FromUInt32(0xFF4A4F56))],
        startPoint: new RelativePoint(0, 0, RelativeUnit.Relative), endPoint: new RelativePoint(0, 1, RelativeUnit.Relative));

    /// A vertical fader (norm 1 = top).
    protected void Fader(DrawingContext ctx, Rect slot, double norm, Action<double> set)
    {
        norm = Math.Clamp(norm, 0, 1);
        ctx.DrawRectangle(Slot, Ui.FaceEdge, new Rect(slot.Center.X - 4, slot.Y, 8, slot.Height), 4, 4);
        double y = slot.Bottom - norm * slot.Height;
        var cap = new Rect(slot.Center.X - 16, y - 9, 32, 18);
        ctx.DrawRectangle(Cap, KnobRim, cap, 2, 2);
        ctx.DrawLine(new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF111111)), 2), new Point(cap.X + 3, cap.Center.Y), new Point(cap.Right - 3, cap.Center.Y));
        _controls.Add(new Control1(new Rect(slot.X - 12, slot.Y - 10, slot.Width + 24, slot.Height + 20), norm, set, true));
    }

    // Value mappings for the controls.
    protected static double LogNorm(double v, double lo, double hi) => Math.Log(Math.Clamp(v, lo, hi) / lo) / Math.Log(hi / lo);
    protected static double FromLog(double n, double lo, double hi) => lo * Math.Pow(hi / lo, Math.Clamp(n, 0, 1));
    protected static double LinNorm(double v, double lo, double hi) => (Math.Clamp(v, lo, hi) - lo) / (hi - lo);
    protected static double FromLin(double n, double lo, double hi) => lo + Math.Clamp(n, 0, 1) * (hi - lo);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var p = e.GetPosition(this);
        foreach (var (r, a) in _keys)
            if (r.Contains(p)) { a(); InvalidateVisual(); e.Handled = true; return; }
        foreach (var c in _controls)
            if (c.R.Contains(p))
            {
                (_drag, _dragY, _dragNorm) = (c, p.Y, c.Norm);
                if (c.Fader) c.Set(Math.Clamp((c.R.Bottom - 10 - p.Y) / (c.R.Height - 20), 0, 1));
                e.Pointer.Capture(this);
                e.Handled = true;
                InvalidateVisual();
                return;
            }
        Pressed(e, p);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_drag is null) { base.OnPointerMoved(e); return; }
        var p = e.GetPosition(this);
        if (_drag.Fader) _drag.Set(Math.Clamp((_drag.R.Bottom - 10 - p.Y) / (_drag.R.Height - 20), 0, 1));
        else _drag.Set(Math.Clamp(_dragNorm + (_dragY - p.Y) / 200, 0, 1));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_drag is null) { base.OnPointerReleased(e); return; }
        _drag = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var p = e.GetPosition(this);
        foreach (var c in _controls)
            if (c.R.Contains(p)) { c.Set(Math.Clamp(c.Norm + Math.Sign(e.Delta.Y) * 0.02, 0, 1)); InvalidateVisual(); e.Handled = true; return; }
        base.OnPointerWheelChanged(e);
    }

    protected virtual void Pressed(PointerPressedEventArgs e, Point p) { }
}

/// 19" rack rail with mounting holes.
public sealed class RackRail : Control
{
    static readonly IBrush Steel = new ImmutableLinearGradientBrush(
        [new ImmutableGradientStop(0, Color.FromUInt32(0xFF4A4F56)), new ImmutableGradientStop(0.45, Color.FromUInt32(0xFF6B7078)), new ImmutableGradientStop(1, Color.FromUInt32(0xFF3A3E44))],
        startPoint: new RelativePoint(0, 0, RelativeUnit.Relative), endPoint: new RelativePoint(1, 0, RelativeUnit.Relative));
    static readonly IBrush Hole = new ImmutableSolidColorBrush(Color.FromUInt32(0xFF0B0C0D));
    public RackRail() { Width = 20; }
    public override void Render(DrawingContext ctx)
    {
        ctx.DrawRectangle(Steel, null, new Rect(Bounds.Size));
        for (double y = 10; y < Bounds.Height; y += 17) ctx.DrawRectangle(Hole, null, new Rect(6, y, 8, 5), 2, 2);
    }
}

/// DS-T: transport (1U).
public sealed class TransportUnit : StudioUnit
{
    public TransportUnit(StudioState s) : base(s, "DS-T", "TRANSPORT") { Height = 86; }

    protected override void DrawContent(DrawingContext ctx, Rect r)
    {
        var p = S.Player;
        p?.Refresh();
        double y = r.Y + 6, x = r.X;
        Key(ctx, new Rect(x, y + 4, 48, 34), p?.Playing == true ? "■" : "▶", S.TogglePlay, p?.Playing == true);
        Key(ctx, new Rect(x + 56, y + 4, 48, 34), "LOOP", S.ToggleLoop, p?.Looping == true, Ui.Amber);
        Key(ctx, new Rect(x + 112, y + 4, 82, 34), S.ConsoleView ? "GRAVADOR" : "CONSOLE", () => { S.ConsoleView = !S.ConsoleView; S.Select(); }, S.ConsoleView, Ui.Amber);
        x += 214;
        double sec = (p?.Position ?? 0) / S.Rate, mus = sec - S.GridOrigin;
        int bar = (int)Math.Floor(mus / S.Bar), beat = (int)Math.Floor((mus - bar * S.Bar) / S.Beat), six = (int)Math.Floor((mus - bar * S.Bar - beat * S.Beat) / (S.Beat / 4));
        x += Readout(ctx, x, y, mus < 0 ? "0.0.0" : $"{bar + 1}.{beat + 1}.{six + 1}", 7, 3.4, "POSIÇÃO") + 12;
        var t = TimeSpan.FromSeconds(Math.Max(0, sec));
        x += Readout(ctx, x, y, $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 10:00}", 7, 2.6, "TEMPO") + 12;
        x += Readout(ctx, x, y, $"{(S.Review.TempoFromDrums && S.ResultTake is { } rt ? rt.Bpm : S.Session.Bpm):0.0}", 5, 2.6, S.Review.TempoFromDrums ? "BPM BATERIA" : "BPM") + 12;
        x += Readout(ctx, x, y, $"{S.Session.BeatsPerBar}/{S.Session.BeatUnit}", 3, 2.6, "COMPASSO") + 12;
        // S6: the key is the user's; STUDIO only suggests. Tempo: the metronome, or the drums' map.
        string KeyShort(KeyOption k) => k.Tonic.Replace("♭", "b").Replace("♯", "#") + (k.Minor ? "m" : "");
        x += Readout(ctx, x, y, S.Session.KeySet ? KeyShort(S.Session.Key) : "--", 3, 2.6, "TOM") + 8;
        if (S.Suggestion is { } sg && !(S.Session.KeySet && S.Session.Key == sg.Key))
        {
            bool unsure = sg.Margin < 0.05 || sg.Correlation < 0.6;
            Key(ctx, new Rect(x, y, 112, 20), $"APLICAR {KeyShort(sg.Key)}", () => S.ApplyKey(sg.Key), false, Ui.Amber, !S.Running);
            Ui.Text(ctx, $"sugestão {sg.Correlation * 100:0}%{(unsure ? " · incerta" : "")}", x + 56, y + 22, 7, unsure ? Ui.Label : Ui.Amber, Ui.SansBold, Ui.Align.Center);
        }
        else if (S.Session.KeySet)
            Key(ctx, new Rect(x, y, 112, 20), "SEM TOM", () => S.ApplyKey(null), false, Ui.Amber, !S.Running);
        if (S.ResultTake is { Beats.Count: > 0 } || S.Review.TempoFromDrums)
            Key(ctx, new Rect(x, y + 34, 112, 20), S.Review.TempoFromDrums ? "TEMPO BATERIA" : "TEMPO CLICK", S.ToggleTempoSource, S.Review.TempoFromDrums, Ui.Amber);
        if (p is { Looping: true } && S.HasSelection)
        {
            x += Readout(ctx, x, y, $"{S.SelStart / S.Rate:0.00}", 5, 2.2, "LOOP INÍCIO") + 8;
            x += Readout(ctx, x, y, $"{(S.SelEnd - S.SelStart) / S.Rate:0.00}", 5, 2.2, "TAMANHO") + 12;
        }
        // Optional components present (any environment: everything optional, used when there).
        double lx = r.Right - 216;
        (string, bool)[] parts = [("PLAYER", p is not null), ("ANÁLISE", S.Job is not null), ("VEROVIO", Directory.Exists(Path.Combine(AppContext.BaseDirectory, "verovio-data"))), ("DEMUCS", S.DemucsReady), ("GPU", false)];
        for (int i = 0; i < parts.Length; i++)
        {
            Ui.Led(ctx, new Point(lx + 16 + i * 44, y + 12), 4, parts[i].Item2, Ui.Green, Ui.GreenOff);
            Ui.Text(ctx, parts[i].Item1, lx + 16 + i * 44, y + 22, 7, parts[i].Item2 ? Ui.LabelBright : Ui.Label, Ui.SansBold, Ui.Align.Center);
        }
        Ui.Text(ctx, S.Session.KeySet ? S.Session.Key.Label : "sem armadura", lx + 16, y + 40, 10, Ui.Label, Ui.Mono);
    }
}

/// DS-L: library on a phosphor screen.
public sealed class LibraryUnit : StudioUnit
{
    int _scroll;
    readonly List<(Rect R, StudioLibrary.Item It)> _rows = [];
    public LibraryUnit(StudioState s) : base(s, "DS-L", "BIBLIOTECA") { Width = 270; }

    protected override void DrawContent(DrawingContext ctx, Rect r)
    {
        var screen = new Rect(r.X, r.Y + 4, r.Width, r.Height - 52);
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF0B0C0D)), null, screen.Inflate(3), 4, 4);
        ctx.DrawRectangle(PhosphorBg, null, screen, 2, 2);
        _rows.Clear();
        double y = screen.Y + 6;
        string? group = null;
        foreach (var it in S.Library.Skip(_scroll))
        {
            string g = it.Kind == "REC" ? "TAKES" : "IMPORTADOS";
            if (g != group) { Ui.Text(ctx, "▸ " + g, screen.X + 8, y, 9, PhosphorDim, Ui.Mono); y += 16; group = g; }
            if (y > screen.Bottom - 18) break;
            var row = new Rect(screen.X + 2, y - 1, screen.Width - 4, 17);
            bool sel = it.Path == S.Current;
            if (sel) ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF12321F)), null, row);
            Ui.Text(ctx, Path.GetFileNameWithoutExtension(it.Name), screen.X + 14, y, 10, sel ? Phosphor : PhosphorDim, Ui.Mono);
            Ui.Text(ctx, it.Kind, screen.Right - 8, y, 8, PhosphorDim, Ui.Mono, Ui.Align.Right);
            _rows.Add((row, it));
            y += 18;
        }
        Key(ctx, new Rect(r.X, r.Bottom - 38, r.Width / 2 - 4, 32), "IMPORTAR", () => _ = ImportAsync());
        Key(ctx, new Rect(r.X + r.Width / 2 + 4, r.Bottom - 38, r.Width / 2 - 4, 32), "ATUALIZAR", () => { S.ReloadLibrary(); InvalidateVisual(); });
    }

    protected override void Pressed(PointerPressedEventArgs e, Point p)
    {
        foreach (var (row, it) in _rows)
            if (row.Contains(p)) { S.Open(it.Path); InvalidateVisual(); return; }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        _scroll = Math.Clamp(_scroll - Math.Sign(e.Delta.Y), 0, Math.Max(0, S.Library.Count - 1));
        InvalidateVisual();
    }

    async Task ImportAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } sp) return;
        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Importar áudio", AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Áudio") { Patterns = StudioLibrary.Extensions.Select(x => "*" + x).ToArray() }],
        });
        foreach (var f in files)
            if (f.TryGetLocalPath() is { } path) StudioLibrary.Import(path);
        S.ReloadLibrary();
        InvalidateVisual();
    }
}

/// DS-A: track recorder — channel card with its tape label, the phosphor screen (bar ruler,
/// waveform, selection, loop, playhead), the transcription lane, and the S2 edit keys.
public sealed class RecorderUnit : StudioUnit
{
    float[] _mn = new float[4096], _mx = new float[4096];
    Rect _screen, _lane;
    readonly List<(Rect R, int Track)> _cards = [];
    readonly List<(Rect R, string Kind, double Start)> _events = [];   // S6: what a click picks for review
    double _dragFrom = -1;

    public RecorderUnit(StudioState s) : base(s, "DS-A", "GRAVADOR DE FAIXAS") { }

    double FrameAt(double x) => S.ViewStart + Math.Clamp((x - _screen.X) / Math.Max(1, _screen.Width), 0, 1) * (S.ViewEnd - S.ViewStart);
    double XAt(double frame) => _screen.X + (frame - S.ViewStart) / Math.Max(1, S.ViewEnd - S.ViewStart) * _screen.Width;

    protected override void DrawContent(DrawingContext ctx, Rect r)
    {
        // Edit keys (S2): the selection is in the edited take. With an event picked, the review keys (S6).
        double kx = r.X, ky = r.Y;
        bool sel = S.HasSelection;
        _events.Clear();
        (string, Action, bool, bool)[] keys = S.Picked is ("chord", _) ?
        [
            ("◀ RAIZ", () => S.FixChord(rootStep: -1), false, true), ("RAIZ ▶", () => S.FixChord(rootStep: 1), false, true),
            ("QUALIDADE", () => S.FixChord(nextQuality: true), false, true), ("BAIXO", () => S.FixChord(nextBass: true), false, true),
            ("APAGAR", S.DeletePicked, false, true), ("DÚVIDA ▶", S.NextDoubt, false, S.Doubts > 0), ("OK", () => S.Pick(null), true, true),
        ] : S.Picked is not null ?
        [
            ("−8ª", () => S.FixNote(-12), false, true), ("−½", () => S.FixNote(-1), false, true), ("+½", () => S.FixNote(1), false, true),
            ("+8ª", () => S.FixNote(12), false, true), ("APAGAR", S.DeletePicked, false, true), ("DÚVIDA ▶", S.NextDoubt, false, S.Doubts > 0),
            ("OK", () => S.Pick(null), true, true),
        ] :
        [
            ("APARAR", S.Trim, false, sel), ("CORTAR", S.Cut, false, sel), ("−3 dB", () => S.Gain(-3), false, sel), ("+3 dB", () => S.Gain(3), false, sel),
            ("FADE IN", S.FadeIn, S.Edits.FadeIn > 0, sel), ("FADE OUT", S.FadeOut, S.Edits.FadeOut > 0, sel),
            ("NORMALIZAR", S.Normalize, S.Edits.Normalize, S.Current is not null), ("DESFAZER", S.Undo, false, S.Current is not null),
            ("ORIGINAL", S.Original, S.Edits.IsEmpty, S.Current is not null), ("ZOOM TOTAL", S.Fit, false, S.Current is not null),
        ];
        foreach (var (label, act, lit, en) in keys)
        {
            double w = Math.Max(64, label.Length * 7.5 + 18);
            Key(ctx, new Rect(kx, ky, w, 28), label, () => { act(); InvalidateVisual(); }, lit, Ui.Amber, en);
            kx += w + 6;
        }
        if (S.Picked is not null)
        {
            Plasma.DotText(ctx, S.PickedLabel, kx + 8, ky + 6, 2.4, 6);
            Ui.Text(ctx, $"REVISÃO · {S.Review.Fixes.Count} correções", kx + 8 + Plasma.CellWidth(2.4) * 6 + 8, ky + 9, 8, Ui.Amber, Ui.SansBold);
        }
        else if (S.ResultTake is not null && kx + 150 < r.Right)
            Key(ctx, new Rect(kx, ky, 110, 28), S.Doubts > 0 ? $"DÚVIDAS {S.Doubts}" : "DÚVIDAS", S.NextDoubt, false, Ui.Amber, S.Doubts > 0);

        // Channel card + screen.
        var area = new Rect(r.X, r.Y + 38, r.Width, r.Height - 38);
        var card = new Rect(area.X, area.Y, 150, area.Height);
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF24272B)), new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF0B0C0D)), 1), card, 3, 3);
        Ui.Text(ctx, "CANAIS", card.X + 8, card.Y + 4, 8, Ui.Label, Ui.SansBold);
        double laneH = 44;
        double stemH = S.Stems.Count == 0 ? 0 : Math.Clamp((area.Height - 30 - laneH) * 0.55 / S.Stems.Count, 26, 60);
        double trackH = Math.Max(60, area.Height - 30 - laneH - stemH * S.Stems.Count);
        _cards.Clear();
        var selPen = new ImmutablePen((IImmutableBrush)Ui.Amber, 1.5);
        var mixRow = new Rect(card.X + 3, card.Y + 18, card.Width - 6, trackH + 4);
        _cards.Add((mixRow, 0));
        if (S.Selected == 0 && S.Current is not null) ctx.DrawRectangle(null, selPen, mixRow, 3, 3);
        Tape(ctx, card.X + 10, card.Y + 26, S.Current is null ? "—" : "MIX");
        if (S.Channel(0).Mute) Ui.Text(ctx, "MUDO", card.X + 16, card.Y + 82, 8, Ui.Amber, Ui.SansBold);
        Plasma.DotText(ctx, "1", card.Right - 22, card.Y + 26, 2.4, 1);
        Ui.Led(ctx, new Point(card.X + 16, card.Y + 64), 3.5, S.Player?.Playing == true, Ui.Green, Ui.GreenOff);
        Ui.Text(ctx, "PLAY", card.X + 24, card.Y + 58, 8, Ui.Label, Ui.SansBold);
        Ui.Led(ctx, new Point(card.X + 70, card.Y + 64), 3.5, !S.Edits.IsEmpty, Ui.Amber, Ui.AmberOff);
        Ui.Text(ctx, "EDITADO", card.X + 78, card.Y + 58, 8, Ui.Label, Ui.SansBold);
        string laneName = S.Stems.Count > 0 ? "↳ VOZ + CIFRAS" : S.Mode is AppMode.VoiceMono or AppMode.InstrumentMono ? "↳ NOTAS" : "↳ ACORDES";
        var stemNames = new Dictionary<string, string> { ["vocals"] = "VOZ", ["bass"] = "BAIXO", ["other"] = "OUTROS", ["drums"] = "BATERIA", ["guitar"] = "VIOLÃO", ["piano"] = "PIANO" };
        for (int i = 0; i < S.Stems.Count; i++)
        {
            double sy = area.Y + 22 + trackH + i * stemH;
            var row = new Rect(card.X + 3, sy, card.Width - 6, stemH);
            _cards.Add((row, i + 1));
            if (S.Selected == i + 1) ctx.DrawRectangle(null, selPen, row, 3, 3);
            Tape(ctx, card.X + 10, sy + stemH / 2 - 10, stemNames.GetValueOrDefault(S.Stems[i].Name, S.Stems[i].Name.ToUpperInvariant()), i % 2 == 0 ? 1.2 : -1.2);
            Plasma.DotText(ctx, $"{i + 2}", card.Right - 22, sy + stemH / 2 - 8, 2.2, 1);
        }
        Ui.Text(ctx, laneName, card.X + 16, area.Y + 22 + trackH + stemH * S.Stems.Count + 14, 10, S.ResultTake is null ? Ui.Label : Ui.Amber, Ui.SansBold);

        var bezel = new Rect(card.Right + 8, area.Y, area.Right - card.Right - 8, area.Height);
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF0B0C0D)), null, bezel, 5, 5);
        _screen = new Rect(bezel.X + 5, bezel.Y + 22, bezel.Width - 10, trackH);
        _lane = new Rect(_screen.X, _screen.Bottom + stemH * S.Stems.Count + 4, _screen.Width, laneH);
        ctx.DrawRectangle(PhosphorBg, null, new Rect(bezel.X + 5, bezel.Y + 5, bezel.Width - 10, bezel.Height - 10), 2, 2);

        var p = S.Player;
        if (p is null || p.Frames == 0 || S.Current is null)
        {
            Ui.Text(ctx, "escolha um take ou arquivo na biblioteca", _screen.Center.X, _screen.Center.Y - 8, 12, PhosphorDim, Ui.Mono, Ui.Align.Center);
            return;
        }
        // Bar ruler on the session's metronome grid.
        double span = (S.ViewEnd - S.ViewStart) / S.Rate, step = S.Bar;
        while (span / step > 24) step *= 2;
        double t0 = S.GridOrigin + Math.Ceiling((S.ViewStart / S.Rate - S.GridOrigin) / step) * step;
        for (double t = t0; t * S.Rate < S.ViewEnd; t += step)
        {
            double x = XAt(t * S.Rate);
            ctx.DrawLine(PhosphorLine, new Point(x, bezel.Y + 6), new Point(x, _lane.Bottom));
            Ui.Text(ctx, $"{Math.Round((t - S.GridOrigin) / S.Bar) + 1}", x + 3, bezel.Y + 6, 9, Phosphor, Ui.Mono);
        }

        // Waveform.
        int cols = Math.Clamp((int)_screen.Width, 1, _mn.Length);
        p.Peaks((ulong)Math.Max(0, S.ViewStart), (ulong)Math.Max(0, S.ViewEnd), _mn, _mx, cols);
        double mid = _screen.Center.Y, amp = _screen.Height * 0.45;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(_screen.X, mid - _mx[0] * amp), true);
            for (int i = 1; i < cols; i++) c.LineTo(new Point(_screen.X + i, mid - _mx[i] * amp));
            for (int i = cols - 1; i >= 0; i--) c.LineTo(new Point(_screen.X + i, mid - _mn[i] * amp));
            c.EndFigure(true);
        }
        ctx.DrawGeometry(new ImmutableSolidColorBrush(Color.FromUInt32(0x553DDC84)), PhosphorTrace, g);

        // Separated stems, one row each, on the same time axis (stems are the edited take).
        uint[] stemColours = [0xFFFF7A1A, 0xFF5AA9FF, 0xFF9DFFC8, 0xFFC9CED6, 0xFF3DDC84, 0xFFFFB000];
        for (int i = 0; i < S.Stems.Count; i++)
        {
            var row = new Rect(_screen.X, _screen.Bottom + i * stemH, _screen.Width, stemH);
            ctx.DrawLine(PhosphorLine, row.TopLeft, row.TopRight);
            var st = S.Stems[i];
            var brush = new ImmutableSolidColorBrush(Color.FromUInt32(stemColours[i % stemColours.Length]));
            double secPerCol = (S.ViewEnd - S.ViewStart) / S.Rate / Math.Max(1, row.Width);
            for (int col = 0; col < (int)row.Width; col += 2)
            {
                double a = S.ViewStart / S.Rate + col * secPerCol;
                var (lo, hi) = st.Peak(a, a + 2 * secPerCol);
                double cyy = row.Center.Y, ah = row.Height * 0.45;
                ctx.DrawRectangle(brush, null, new Rect(row.X + col, cyy - hi * ah * 1.6, 1.4, Math.Max(1, (hi - lo) * ah * 1.6)));
            }
            // The stem's own transcription on its row: the bass line (pitch over E1..G3), the beats
            // of the drums (ticks; amber where they leave the metronome).
            if (S.ResultTake is not { } rt) continue;
            double o = S.Compensation;
            if (st.Name == "bass")
                foreach (var n in rt.BassNotes)
                {
                    double a = XAt((n.Start + o) * S.Rate), b = XAt((n.End + o) * S.Rate);
                    if (b < row.X || a > row.Right) continue;
                    double ny = row.Bottom - 3 - (row.Height - 6) * Math.Clamp((n.Midi - 28) / 27.0, 0, 1);
                    var nr = new Rect(Math.Max(a, row.X), ny - 1.5, Math.Max(2, Math.Min(b, row.Right) - Math.Max(a, row.X) - 1), 3);
                    bool picked = S.Picked is ("bass", var ps) && Math.Abs(ps - n.Start) < Review.MatchSeconds;
                    ctx.DrawRectangle(picked ? Ui.LabelBright : Ui.Amber, picked ? new ImmutablePen((IImmutableBrush)Ui.Amber, 1) : null, picked ? nr.Inflate(1.5) : nr);
                    _events.Add((nr.Inflate(new Thickness(0, 4)), "bass", n.Start));
                }
            if (st.Name == "drums")
            {
                double period = S.Session.Bpm > 0 ? 60 / S.Session.Bpm : 0;
                foreach (var bt in rt.Beats)
                {
                    double x = XAt((bt + o) * S.Rate);
                    if (x < row.X || x > row.Right) continue;
                    bool off = rt.MaxDriftMs is not null && period > 0 && Math.Abs(bt - Math.Round(bt / period) * period) * 1000 > LeadSheet.DriftFlagMs;
                    ctx.DrawLine(new ImmutablePen((IImmutableBrush)(off ? Ui.Amber : Ui.LabelBright), 1.2), new Point(x, row.Y + 2), new Point(x, row.Y + 8));
                }
            }
        }

        // Transcription lane: the offline result of these edits.
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0x55000000)), null, _lane);
        if (S.ResultTake is { } take)
        {
            double off = S.Compensation;   // result times are compensated; the screen is file time
            foreach (var ch in take.Chords)
            {
                double a = XAt((ch.Start + off) * S.Rate), b = XAt((ch.End + off) * S.Rate);
                if (b < _lane.X || a > _lane.Right) continue;
                var box = new Rect(Math.Max(a, _lane.X) + 1, _lane.Y + 4, Math.Max(2, Math.Min(b, _lane.Right) - Math.Max(a, _lane.X) - 2), _lane.Height - 8);
                bool low = ch.Confidence < 0.5f;
                bool picked = S.Picked is ("chord", var ps) && Math.Abs(ps - ch.Start) < Review.MatchSeconds;
                if (picked) ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0x33FFB000)), null, box, 2, 2);
                ctx.DrawRectangle(null, new ImmutablePen((IImmutableBrush)(picked ? Ui.LabelBright : low ? Ui.Amber : Ui.Green), picked ? 2 : 1), box, 2, 2);
                _events.Add((box, "chord", ch.Start));
                if (box.Width > 26) Plasma.DotText(ctx, ch.Symbol.Length > 6 ? ch.Symbol[..6] : ch.Symbol, box.X + 4, box.Y + 6, 2.6, dim: low);
            }
            if (take.Notes.Count > 0)
            {
                int lo = take.Notes.Min(n => n.Midi), hi = Math.Max(lo + 12, take.Notes.Max(n => n.Midi));
                foreach (var n in take.Notes)
                {
                    double a = XAt((n.Start + off) * S.Rate), b = XAt((n.End + off) * S.Rate);
                    if (b < _lane.X || a > _lane.Right) continue;
                    double yy = _lane.Bottom - 6 - (n.Midi - lo) / (double)(hi - lo) * (_lane.Height - 12);
                    var nr = new Rect(Math.Max(a, _lane.X), yy - 2, Math.Max(2, b - a - 1), 4);
                    bool picked = S.Picked is ("note", var ps) && Math.Abs(ps - n.Start) < Review.MatchSeconds;
                    ctx.DrawRectangle(picked ? Ui.LabelBright : Plasma.Lit, picked ? new ImmutablePen((IImmutableBrush)Ui.Amber, 1) : null, picked ? nr.Inflate(1.5) : nr, 1, 1);
                    _events.Insert(0, (nr.Inflate(new Thickness(1, 4)), "note", n.Start));   // notes before the chord boxes they sit in
                }
            }
        }
        else Ui.Text(ctx, S.Running ? "analisando…" : "sem transcrição: DA · ANALISAR", _lane.X + 8, _lane.Y + 14, 10, PhosphorDim, Ui.Mono);

        if (S.HasSelection)
        {
            var s = new Rect(XAt(S.SelStart), _screen.Y, Math.Max(1, XAt(S.SelEnd) - XAt(S.SelStart)), _lane.Bottom - _screen.Y);
            ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0x22FFB000)), null, s);
            ctx.DrawLine(new ImmutablePen((IImmutableBrush)Ui.Amber, 1), s.TopLeft, s.BottomLeft);
            ctx.DrawLine(new ImmutablePen((IImmutableBrush)Ui.Amber, 1), s.TopRight, s.BottomRight);
            if (p.Looping) ctx.DrawRectangle(Ui.Amber, null, new Rect(s.X, bezel.Y + 5, s.Width, 4));
        }
        double cx = XAt(p.Position);
        if (cx >= _screen.X && cx <= _screen.Right)
        {
            ctx.DrawLine(new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0x66FF3B30)), 6), new Point(cx, bezel.Y + 6), new Point(cx, _lane.Bottom));
            ctx.DrawLine(new ImmutablePen((IImmutableBrush)Ui.Red, 2), new Point(cx, bezel.Y + 6), new Point(cx, _lane.Bottom));
        }
        // Scanlines.
        for (double y = bezel.Y + 6; y < bezel.Bottom - 6; y += 3) ctx.DrawLine(new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0x22000000)), 1), new Point(bezel.X + 5, y), new Point(bezel.Right - 5, y));
    }

    protected override void Pressed(PointerPressedEventArgs e, Point p)
    {
        foreach (var (row, track) in _cards)
            if (row.Contains(p) && S.Current is not null) { S.Selected = track; S.Select(); InvalidateVisual(); return; }   // the chain shows this channel
        foreach (var (rr, kind, start) in _events)
            if (rr.Contains(p)) { S.Pick(kind, start); InvalidateVisual(); return; }
        if (S.Picked is not null) S.Pick(null);
        if (!_screen.Union(_lane).Contains(p) || S.Player is null) return;
        _dragFrom = FrameAt(p.X);
        S.SelStart = S.SelEnd = -1;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_dragFrom < 0) return;
        double f = FrameAt(e.GetPosition(this).X);
        if (Math.Abs(XAt(f) - XAt(_dragFrom)) < 3) return;
        (S.SelStart, S.SelEnd) = (Math.Min(_dragFrom, f), Math.Max(_dragFrom, f));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_dragFrom < 0) return;
        if (!S.HasSelection) S.Player?.Seek((ulong)_dragFrom);
        else S.Player?.Seek((ulong)S.SelStart);
        _dragFrom = -1;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (S.Player is not { Frames: > 0 } p) return;
        double span = S.ViewEnd - S.ViewStart, at = FrameAt(e.GetPosition(this).X);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { double sh = -e.Delta.Y * span * 0.1; S.ViewStart += sh; S.ViewEnd += sh; }
        else
        {
            double ns = Math.Clamp(span * (e.Delta.Y > 0 ? 0.8 : 1.25), 64, p.Frames);
            S.ViewStart = at - (at - S.ViewStart) * ns / span;
            S.ViewEnd = S.ViewStart + ns;
        }
        double len = S.ViewEnd - S.ViewStart;
        if (S.ViewStart < 0) { S.ViewStart = 0; S.ViewEnd = len; }
        if (S.ViewEnd > p.Frames) { S.ViewEnd = p.Frames; S.ViewStart = Math.Max(0, p.Frames - len); }
        InvalidateVisual();
        e.Handled = true;
    }
}

/// DS-C: the channel chain in fixed studio order, joined by patch cables, ending in the analyser
/// (the only unit at work before S4: the others are mounted and wait for the mixing milestone).
public sealed class ChainUnit : StudioUnit
{
    public ChainUnit(StudioState s) : base(s, "DS-C", "CADEIA DO CANAL") { Height = 240; }

    static readonly IPen CableBlack = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF0B0C0D)), 7, lineCap: PenLineCap.Round);
    static readonly IPen CableAmber = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFFFFB000)), 4, lineCap: PenLineCap.Round);

    static string Hz(double f) => f >= 1000 ? $"{f / 1000:0.#}k" : $"{f:0}";

    protected override void DrawContent(DrawingContext ctx, Rect r)
    {
        var names = new Dictionary<string, string> { ["mix"] = "MIX", ["vocals"] = "VOZ", ["bass"] = "BAIXO", ["other"] = "OUTROS", ["drums"] = "BATERIA", ["guitar"] = "VIOLÃO", ["piano"] = "PIANO" };
        Tape(ctx, r.X, r.Y, S.Current is null ? "—" : names.GetValueOrDefault(S.NameOf(S.Selected), S.NameOf(S.Selected)), -1);
        Ui.Text(ctx, "ordem fixa de estúdio · cabos de patch na sequência do sinal · análise lê pós-insertos, pré-fader · clique na fita do canal para escolhê-lo", r.X + 90, r.Y + 2, 9, Ui.Label, Ui.Mono);
        double x = r.X, y = r.Y + 26, h = r.Height - 26;
        var c = S.Channel(S.Selected);
        void Changed() => S.MixChanged();
        bool has = S.Current is not null;
        double ky = y + h / 2 + 4;

        // DS-1 TRIM
        var box = new Rect(x, y, 96, h);
        DrawDevice(ctx, box, "DS-1", "TRIM", has);
        Knob(ctx, new Point(box.Center.X, ky - 6), 18, "GANHO", $"{c.Trim:+0.0;-0.0} dB", LinNorm(c.Trim, -24, 24), n => { c.Trim = (float)Math.Round(FromLin(n, -24, 24), 1); Changed(); });
        float pk = S.Meters.Peak[Math.Min(S.Selected, 7)];
        for (int i = 0; i < 10; i++) Ui.Led(ctx, new Point(box.X + 14 + i * 8, box.Bottom - 14), 2.6, pk > -50 + i * 5.5, i > 7 ? Ui.Red : i > 5 ? Ui.Amber : Ui.Green, Ui.GreenOff);
        Cable(ctx, box.Right, box.Center.Y);
        x = box.Right + 26;

        // DF-2 FILTRO
        box = new Rect(x, y, 132, h);
        DrawDevice(ctx, box, "DF-2", "FILTRO", c.HpOn || c.LpOn);
        Key(ctx, new Rect(box.X + 12, box.Y + 24, 36, 20), "HP", () => { c.HpOn = !c.HpOn; Changed(); }, c.HpOn, Ui.Green, has);
        Key(ctx, new Rect(box.X + 52, box.Y + 24, 36, 20), "LP", () => { c.LpOn = !c.LpOn; Changed(); }, c.LpOn, Ui.Green, has);
        Key(ctx, new Rect(box.X + 88, box.Y + 24, 36, 20), c.Steep ? "24" : "12", () => { c.Steep = !c.Steep; Changed(); }, c.Steep, Ui.Amber, has);
        Knob(ctx, new Point(box.X + 38, ky + 10), 16, "HP", Hz(c.Hp), LogNorm(c.Hp, 20, 1000), n => { c.Hp = (float)Math.Round(FromLog(n, 20, 1000)); Changed(); }, c.HpOn);
        Knob(ctx, new Point(box.X + 96, ky + 10), 16, "LP", Hz(c.Lp), LogNorm(c.Lp, 1000, 20000), n => { c.Lp = (float)Math.Round(FromLog(n, 1000, 20000), -1); Changed(); }, c.LpOn);
        Cable(ctx, box.Right, box.Center.Y);
        x = box.Right + 26;

        // DG-3 GATE
        box = new Rect(x, y, 140, h);
        DrawDevice(ctx, box, "DG-3", "GATE", c.GateOn);
        Key(ctx, new Rect(box.X + 12, box.Y + 24, 40, 20), "ON", () => { c.GateOn = !c.GateOn; Changed(); }, c.GateOn, Ui.Green, has);
        Ui.Led(ctx, new Point(box.X + 66, box.Y + 34), 3.5, c.GateOn && S.Meters.GateOpen[Math.Min(S.Selected, 7)], Ui.Green, Ui.GreenOff);
        Ui.Text(ctx, "ABERTO", box.X + 74, box.Y + 28, 7, Ui.Label, Ui.SansBold);
        Knob(ctx, new Point(box.X + 28, ky + 10), 14, "LIMIAR", $"{c.GateThreshold:0}", LinNorm(c.GateThreshold, -80, 0), n => { c.GateThreshold = (float)Math.Round(FromLin(n, -80, 0)); Changed(); }, c.GateOn);
        Knob(ctx, new Point(box.X + 70, ky + 10), 14, "SOLTURA", $"{c.GateRelease:0} ms", LogNorm(c.GateRelease, 5, 1000), n => { c.GateRelease = (float)Math.Round(FromLog(n, 5, 1000)); Changed(); }, c.GateOn);
        Knob(ctx, new Point(box.X + 112, ky + 10), 14, "FAIXA", $"{c.GateRange:0} dB", LinNorm(c.GateRange, 0, 80), n => { c.GateRange = (float)Math.Round(FromLin(n, 0, 80)); Changed(); }, c.GateOn);
        Cable(ctx, box.Right, box.Center.Y);
        x = box.Right + 26;

        // DQ-4 EQ (4 bands: low shelf, two bells, high shelf; frequency and gain per band)
        box = new Rect(x, y, 216, h);
        DrawDevice(ctx, box, "DQ-4", "EQ", c.EqOn);
        Key(ctx, new Rect(box.X + 12, box.Y + 24, 40, 20), "ON", () => { c.EqOn = !c.EqOn; Changed(); }, c.EqOn, Ui.Green, has);
        string[] bandNames = ["GRAVE", "MÉDIO-G", "MÉDIO-A", "AGUDO"];
        for (int b = 0; b < Math.Min(4, c.Bands.Length); b++)
        {
            var band = c.Bands[b];
            double bx = box.X + 28 + b * 52;
            Knob(ctx, new Point(bx, ky - 14), 12, bandNames[b], Hz(band.Freq), LogNorm(band.Freq, 20, 20000), n => { band.Freq = (float)Math.Round(FromLog(n, 20, 20000)); Changed(); }, c.EqOn);
            Knob(ctx, new Point(bx, ky + 40), 12, "GANHO", $"{band.Gain:+0;-0}", LinNorm(band.Gain, -15, 15), n => { band.Gain = (float)Math.Round(FromLin(n, -15, 15), 1); Changed(); }, c.EqOn);
        }
        Cable(ctx, box.Right, box.Center.Y);
        x = box.Right + 26;

        // DC-5 COMP
        box = new Rect(x, y, 184, h);
        DrawDevice(ctx, box, "DC-5", "COMP", c.CompOn);
        Key(ctx, new Rect(box.X + 12, box.Y + 24, 40, 20), "ON", () => { c.CompOn = !c.CompOn; Changed(); }, c.CompOn, Ui.Green, has);
        float gr = S.Meters.Gr[Math.Min(S.Selected, 7)];
        for (int i = 0; i < 8; i++) Ui.Led(ctx, new Point(box.X + 62 + i * 13, box.Y + 34), 3, c.CompOn && gr > 1 + i * 1.5, Ui.Amber, Ui.AmberOff);
        Knob(ctx, new Point(box.X + 30, ky + 10), 13, "LIMIAR", $"{c.CompThreshold:0}", LinNorm(c.CompThreshold, -50, 0), n => { c.CompThreshold = (float)Math.Round(FromLin(n, -50, 0)); Changed(); }, c.CompOn);
        Knob(ctx, new Point(box.X + 68, ky + 10), 13, "RAZÃO", $"{c.CompRatio:0.#}:1", LogNorm(c.CompRatio, 1, 20), n => { c.CompRatio = (float)Math.Round(FromLog(n, 1, 20), 1); Changed(); }, c.CompOn);
        Knob(ctx, new Point(box.X + 106, ky + 10), 13, "ATAQUE", $"{c.CompAttack:0.#}", LogNorm(c.CompAttack, 0.1, 100), n => { c.CompAttack = (float)Math.Round(FromLog(n, 0.1, 100), 1); Changed(); }, c.CompOn);
        Knob(ctx, new Point(box.X + 144, ky + 10), 13, "GANHO", $"{c.CompMakeup:+0}", LinNorm(c.CompMakeup, 0, 24), n => { c.CompMakeup = (float)Math.Round(FromLin(n, 0, 24)); Changed(); }, c.CompOn);
        Cable(ctx, box.Right, box.Center.Y);
        x = box.Right + 26;

        var an = new Rect(x, y, r.Right - x, h);
        DrawDevice(ctx, an, S.Mode is AppMode.VoiceMono or AppMode.InstrumentMono ? "DA-N" : "DA-C", "ANALISADOR", true);
        string mode = S.Mode switch
        {
            AppMode.VoiceMono => "VOZ", AppMode.InstrumentMono => "MELODIA", AppMode.GuitarChords => "VIOLÃO", AppMode.PianoChords => "PIANO", _ => "GERAL",
        };
        double ix = an.X + 14, iy = an.Y + 24;
        Key(ctx, new Rect(ix, iy, 88, 28), mode, () => { S.CycleMode(); InvalidateVisual(); }, true, Ui.Amber, !S.Running);
        string sepLabel = !DemucsModel.HelperPresent ? "SEM DEMUCS" : S.Stems.Count > 0 ? "SEPARADO" : DemucsModel.Installed(S.Variant) ? "SEPARAR" : "SEPARAR*";
        Key(ctx, new Rect(ix + 94, iy, 104, 28), sepLabel, S.Separate, S.Stems.Count > 0 || S.Separating, Ui.Amber, DemucsModel.HelperPresent && S.Current is not null && !S.Separating && !S.Running);
        Key(ctx, new Rect(ix, iy + 34, 100, 28), "ANALISAR", S.Analyze, S.Running, Ui.Green, S.Current is not null && !S.Running && !S.Separating);
        Key(ctx, new Rect(ix + 106, iy + 34, 92, 28), "CANCELAR", S.Cancel, false, Ui.Red, S.Running || S.Separating);
        Key(ctx, new Rect(ix + 204, iy, 84, 28), "SCORE", S.OpenScore, S.Result is not null, Ui.Green, S.Current is not null);
        Key(ctx, new Rect(ix + 204, iy + 34, 84, 28), "ENTREGAR", () => S.Deliver(), false, Ui.Amber, S.Result is not null && !S.Running);
        if (sepLabel == "SEPARAR*") Ui.Text(ctx, $"* baixa o Demucs ({S.Variant.Size / 1_000_000} MB) uma vez", ix, iy + 66, 8, Ui.Label, Ui.Mono);
        // Progress as an LED bar (separation amber, analysis green).
        bool sep = S.Separating;
        int lit = (int)Math.Round((sep ? S.SepProgress : S.Running ? S.Progress : S.Result is not null ? 1 : 0) * 20);
        for (int i = 0; i < 20; i++) Ui.Led(ctx, new Point(ix + 6 + i * 13.5, iy + 82), 4, i < lit, sep ? Ui.Amber : Ui.Green, sep ? Ui.AmberOff : Ui.GreenOff);
        // Plasma readout of the result.
        var win = new Rect(ix, iy + 94, Math.Min(an.Width - 28, 520), an.Bottom - iy - 102);
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF120804)), Ui.FaceEdge, win, 2, 2);
        if (S.ResultTake is { } t)
        {
            bool notes = t.Notes.Count > 0;
            string main = notes ? $"{t.Notes.Count}" : $"{t.Chords.Count}";
            Plasma.DotText(ctx, main, win.X + 8, win.Y + 6, 3, 3);
            Plasma.Text(ctx, notes ? "NOTAS" : "ACORDES", win.X + 84, win.Y + 6, 13);
            string how = S.Stems.Count > 0 ? $"{t.Notes.Count} notas da voz · {t.Chords.Count} acordes · faixas separadas"
                : notes ? "voz: take inteiro" : "take inteiro · Viterbi" + (S.FromTake && S.Edits.KeepsGrid ? " · grade do metrônomo" : "");
            Plasma.Text(ctx, how.Length > 44 ? how[..44] : how, win.X + 84, win.Y + 24, 9, false);
        }
        else Plasma.Text(ctx, S.Separating ? $"SEPARANDO {S.SepProgress * 100:0} %" : S.Running ? $"{S.Progress * 100:0} %" : "—", win.X + 12, win.Y + 12, 16, S.Running || S.Separating);
    }

    void DrawDevice(DrawingContext ctx, Rect box, string model, string name, bool on)
    {
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF2A2D32)), Ui.FaceEdge, box, 4, 4);
        foreach (var p in new[] { box.TopLeft + new Point(7, 7), box.TopRight + new Point(-7, 7), box.BottomLeft + new Point(7, -7), box.BottomRight + new Point(-7, -7) })
            ctx.DrawEllipse(Ui.Screw, null, p, 2.8, 2.8);
        double tx = box.X + 14;
        tx += Ui.Text(ctx, model, tx, box.Y + 6, 9, on ? Ui.Amber : Ui.Label, Ui.SansBold).Width + 6;
        Ui.Text(ctx, name, tx, box.Y + 6, 9, on ? Ui.LabelBright : Ui.Label, Ui.SansBold);
        Ui.Led(ctx, new Point(box.Right - 14, box.Y + 12), 3, on, Ui.Green, Ui.GreenOff);
        foreach (double jx in new[] { box.X, box.Right })   // jacks
        {
            ctx.DrawEllipse(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF5B6068)), null, new Point(jx, box.Center.Y), 6, 6);
            ctx.DrawEllipse(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF0B0C0D)), null, new Point(jx, box.Center.Y), 2.5, 2.5);
        }
    }

    static void Cable(DrawingContext ctx, double x, double y)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(x, y), false);
            c.CubicBezierTo(new Point(x + 8, y + 26), new Point(x + 18, y + 26), new Point(x + 26, y));
            c.EndFigure(false);
        }
        ctx.DrawGeometry(null, CableBlack, g);
        ctx.DrawGeometry(null, CableAmber, g);
    }
}

/// DS-M: the console — one strip per channel (number, tape label, trim, insert LEDs in console order,
/// analyser, pan, mute / solo, meter, fader, dB) and the master (EQ, bus compressor, limiter,
/// fader, a pair of VU meters, BOUNCE). Faders and pans are listening only: never the transcription.
public sealed class ConsoleUnit : StudioUnit
{
    public ConsoleUnit(StudioState s) : base(s, "DS-M", "CONSOLE") { }

    static double FaderNorm(float db) => db <= -60 ? 0 : Math.Pow((db + 60) / 72, 1.6);   // -60..+12 dB, more travel near 0
    static float FaderDb(double n) => n <= 0.001 ? -90f : (float)Math.Round(Math.Pow(n, 1 / 1.6) * 72 - 60, 1);

    protected override void DrawContent(DrawingContext ctx, Rect r)
    {
        if (S.Current is null) { Ui.Text(ctx, "escolha um take na biblioteca", r.Center.X, r.Center.Y, 12, Ui.Label, Ui.Mono, Ui.Align.Center); return; }
        var names = new Dictionary<string, string> { ["mix"] = "MIX", ["vocals"] = "VOZ", ["bass"] = "BAIXO", ["other"] = "OUTROS", ["drums"] = "BATERIA", ["guitar"] = "VIOLÃO", ["piano"] = "PIANO" };
        double w = 118, x = r.X;
        for (int t = 0; t < S.Tracks; t++, x += w + 8)
        {
            var c = S.Channel(t);
            int track = t;
            var st = new Rect(x, r.Y, w, r.Height);
            ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF2A2D32)), t == S.Selected ? new ImmutablePen((IImmutableBrush)Ui.Amber, 1.5) : Ui.FaceEdge, st, 4, 4);
            Plasma.DotText(ctx, $"{t + 1}", st.X + 8, st.Y + 8, 2.2, 1);
            Tape(ctx, st.X + 26, st.Y + 6, names.GetValueOrDefault(S.NameOf(t), S.NameOf(t)), t % 2 == 0 ? -1.2 : 1.2);
            Knob(ctx, new Point(st.Center.X, st.Y + 52), 13, "TRIM", $"{c.Trim:+0;-0} dB", LinNorm(c.Trim, -24, 24), n => { c.Trim = (float)Math.Round(FromLin(n, -24, 24)); S.MixChanged(); });
            (string, bool)[] ins = [("FILT", c.HpOn || c.LpOn), ("GATE", c.GateOn), ("EQ", c.EqOn), ("COMP", c.CompOn)];
            for (int i = 0; i < ins.Length; i++)
            {
                var rr = new Rect(st.X + 8, st.Y + 88 + i * 17, w - 16, 15);
                ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(ins[i].Item2 ? 0xFF24272Bu : 0xFF16181Bu)), Ui.FaceEdge, rr, 2, 2);
                Ui.Text(ctx, ins[i].Item1, rr.X + 5, rr.Y + 1, 8, ins[i].Item2 ? Ui.LabelBright : Ui.Label, Ui.Mono);
                Ui.Led(ctx, new Point(rr.Right - 7, rr.Center.Y), 2.6, ins[i].Item2, Ui.Green, Ui.GreenOff);
            }
            Key(ctx, new Rect(st.X + 8, st.Y + 160, w - 16, 18), "CADEIA ↓", () => { S.Selected = track; S.Select(); }, t == S.Selected, Ui.Amber);
            Knob(ctx, new Point(st.Center.X, st.Y + 204), 12, "PAN", c.Pan == 0 ? "C" : c.Pan < 0 ? $"L{-c.Pan * 100:0}" : $"R{c.Pan * 100:0}", (c.Pan + 1) / 2, n => { c.Pan = (float)Math.Round(n * 2 - 1, 2); if (Math.Abs(c.Pan) < 0.04f) c.Pan = 0; S.MixChanged(); });
            Key(ctx, new Rect(st.X + 14, st.Y + 244, 40, 22), "M", () => { c.Mute = !c.Mute; S.MixChanged(); }, c.Mute, Ui.Amber);
            Key(ctx, new Rect(st.Right - 54, st.Y + 244, 40, 22), "S", () => { c.Solo = !c.Solo; S.MixChanged(); }, c.Solo, Ui.Green);
            // Meter (post-fader peak) and fader.
            var slot = new Rect(st.Center.X + 6, st.Y + 282, 20, st.Bottom - st.Y - 282 - 34);
            float pk = S.Meters.Peak[Math.Min(t, 7)];
            int segs = (int)(slot.Height / 7);
            for (int i = 0; i < segs; i++)
            {
                double db = -60 + 63.0 * i / segs;
                var col = db > 0 ? Ui.Red : db > -6 ? Ui.Amber : Ui.Green;
                ctx.DrawRectangle(pk >= db ? col : new ImmutableSolidColorBrush(Color.FromUInt32(0xFF15301F)), null, new Rect(st.X + 14, slot.Bottom - (i + 1) * 7, 10, 5));
            }
            Fader(ctx, slot, FaderNorm(c.Fader), n => { c.Fader = FaderDb(n); S.MixChanged(); });
            Plasma.Text(ctx, c.Fader <= -90 ? "-inf" : $"{c.Fader:+0.0;-0.0}", st.Center.X, st.Bottom - 24, 11, true, Ui.Mono, Ui.Align.Center);
        }

        // Master.
        var m = S.Mix.Master;
        double mw = 300, mx = r.Right - mw;
        var ms = new Rect(mx, r.Y, mw, r.Height);
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF2A2D32)), new ImmutablePen((IImmutableBrush)Ui.Amber, 1), ms, 4, 4);
        Tape(ctx, ms.X + 10, ms.Y + 6, "MASTER", -1);
        float vuL = S.Meters.MasterRms[0] + 18, vuR = S.Meters.MasterRms[1] + 18;   // 0 VU = -18 dBFS
        InputModule.DrawVu(ctx, new Rect(ms.X + 12, ms.Y + 34, 134, 80), vuL);
        InputModule.DrawVu(ctx, new Rect(ms.X + 154, ms.Y + 34, 134, 80), vuR);
        Key(ctx, new Rect(ms.X + 12, ms.Y + 126, 60, 22), "EQ", () => { m.EqOn = !m.EqOn; S.MixChanged(); }, m.EqOn, Ui.Green);
        Key(ctx, new Rect(ms.X + 78, ms.Y + 126, 60, 22), "COMP", () => { m.CompOn = !m.CompOn; S.MixChanged(); }, m.CompOn, Ui.Green);
        Key(ctx, new Rect(ms.X + 144, ms.Y + 126, 72, 22), "LIMITER", () => { m.LimiterOn = !m.LimiterOn; S.MixChanged(); }, m.LimiterOn, Ui.Green);
        Knob(ctx, new Point(ms.X + 40, ms.Y + 186), 14, "LIMIAR", $"{m.CompThreshold:0}", LinNorm(m.CompThreshold, -40, 0), n => { m.CompThreshold = (float)Math.Round(FromLin(n, -40, 0)); S.MixChanged(); }, m.CompOn);
        Knob(ctx, new Point(ms.X + 100, ms.Y + 186), 14, "RAZÃO", $"{m.CompRatio:0.#}:1", LogNorm(m.CompRatio, 1, 10), n => { m.CompRatio = (float)Math.Round(FromLog(n, 1, 10), 1); S.MixChanged(); }, m.CompOn);
        Knob(ctx, new Point(ms.X + 160, ms.Y + 186), 14, "TETO", $"{m.Ceiling:0.0}", LinNorm(m.Ceiling, -12, 0), n => { m.Ceiling = (float)Math.Round(FromLin(n, -12, 0), 1); S.MixChanged(); }, m.LimiterOn);
        for (int i = 0; i < 8; i++) Ui.Led(ctx, new Point(ms.X + 196 + i * 11, ms.Y + 186), 3, S.Meters.LimiterGr > 0.5 + i, Ui.Red, Ui.RedOff);
        Ui.Text(ctx, "LIMITANDO", ms.X + 196, ms.Y + 196, 7, Ui.Label, Ui.SansBold);
        var mslot = new Rect(ms.X + 40, ms.Y + 236, 20, ms.Bottom - ms.Y - 236 - 34);
        Fader(ctx, mslot, FaderNorm(m.Fader), n => { m.Fader = FaderDb(n); S.MixChanged(); });
        Plasma.Text(ctx, $"{m.Fader:+0.0;-0.0}", mslot.Center.X, ms.Bottom - 24, 11, true, Ui.Mono, Ui.Align.Center);
        Key(ctx, new Rect(ms.X + 120, ms.Bottom - 70, 150, 34), "BOUNCE WAV", S.Bounce, false, Ui.Amber);
        Ui.Text(ctx, "escuta e bounce; não a transcrição", ms.X + 120, ms.Bottom - 30, 7, Ui.Label, Ui.Mono);
    }
}

/// The STUDIO tab: the rack between its rails.
public sealed class StudioView : DockPanel
{
    readonly StudioState _s;
    readonly StudioUnit[] _units;
    readonly TextBlock _status = new() { Foreground = Ui.Label, FontFamily = new FontFamily("Cascadia Mono, Consolas, DejaVu Sans Mono, monospace"), Margin = new Thickness(6, 2) };

    public event Action<string>? ScoreRequested;

    public StudioView(Session session)
    {
        _s = new StudioState(session);
        _s.ScoreRequested += p => ScoreRequested?.Invoke(p);
        Background = new SolidColorBrush(Color.FromUInt32(0xFF0D0E10));
        var transport = new TransportUnit(_s);
        var library = new LibraryUnit(_s);
        var recorder = new RecorderUnit(_s);
        var chain = new ChainUnit(_s);
        var console = new ConsoleUnit(_s) { IsVisible = false };
        _recorder = recorder;
        _console = console;
        _units = [transport, library, recorder, console, chain];
        var left = new RackRail();
        var right = new RackRail();
        SetDock(left, Dock.Left);
        SetDock(right, Dock.Right);
        Children.Add(left);
        Children.Add(right);
        var body = new DockPanel { Margin = new Thickness(6, 0) };
        SetDock(transport, Dock.Top);
        transport.Margin = new Thickness(0, 6);
        SetDock(_status, Dock.Bottom);
        SetDock(chain, Dock.Bottom);
        chain.Margin = new Thickness(0, 6, 0, 0);
        SetDock(library, Dock.Left);
        library.Margin = new Thickness(0, 0, 6, 0);
        body.Children.Add(transport);
        body.Children.Add(_status);
        body.Children.Add(chain);
        body.Children.Add(library);
        body.Children.Add(new Panel { Children = { recorder, console } });
        Children.Add(body);
        _s.Changed += Refresh;
        if (_s.Player is null) _s.Status = "núcleo nativo ausente: biblioteca sem reprodução nem análise";
    }

    readonly StudioUnit _recorder, _console;

    void Refresh()
    {
        _recorder.IsVisible = !_s.ConsoleView;
        _console.IsVisible = _s.ConsoleView;
        foreach (var u in _units) u.InvalidateVisual();
        _status.Text = _s.Status;
    }

    /// Animation tick from the window: job progress, meters, playhead, readouts.
    public void Tick() { _s.Poll(); _s.PollMeters(); Refresh(); }
    public void ShowConsole(bool on) { _s.ConsoleView = on; Refresh(); }

    public void TogglePlay() => _s.TogglePlay();
    public void OpenFirst() { if (_s.Library.Count > 0) _s.Open(_s.Library[0].Path); Refresh(); }
    public void AnalyzeCurrent() => _s.Analyze();
    public void SeparateCurrent() => _s.Separate();
    public void ScoreCurrent() => _s.OpenScore();
    /// Review actions for tests and screenshots: pick the n-th chord, fix it, apply the suggested key.
    public void ReviewAction(string name)
    {
        switch (name)
        {
            case "pick": if (_s.ResultTake?.Chords.ElementAtOrDefault(1) is { } c) _s.Pick("chord", c.Start); break;
            case "root": _s.FixChord(rootStep: 1); break;
            case "quality": _s.FixChord(nextQuality: true); break;
            case "key": if (_s.Suggestion is { } sg) _s.ApplyKey(sg.Key); break;
            case "clear": _s.ClearFixes(); _s.ApplyKey(null); break;
            case "deliver": _s.Deliver(); break;
        }
        Refresh();
    }
    public void SelectRange(double fromSeconds, double toSeconds) { _s.SelStart = fromSeconds * _s.Rate; _s.SelEnd = toSeconds * _s.Rate; Refresh(); }
    public void EditAction(string name)
    {
        switch (name) { case "trim": _s.Trim(); break; case "cut": _s.Cut(); break; case "normalize": _s.Normalize(); break; case "undo": _s.Undo(); break; }
        Refresh();
    }
    public void Close() => _s.Close();
}
