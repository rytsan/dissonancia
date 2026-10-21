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
    TakeNote? PickedNote => Picked is (var k, var s) && k != "chord"
        ? (k == "bass" ? ResultTake?.BassNotes : k is ['n', 'o', 't', 'e', ':', var part] && part != 'S' ? ResultTake?.Choir.GetValueOrDefault(part.ToString()) : ResultTake?.Notes)?.MinBy(n => Math.Abs(n.Start - s))
        : null;
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
            .Concat(t.Notes.Where(n => n.Confidence < 0.5f).Select(n => (t.IsChoir ? "note:S" : "note", n.Start)))
            .Concat(t.Choir.SelectMany(kv => kv.Value.Where(n => n.Confidence < 0.5f).Select(n => ("note:" + kv.Key, n.Start)))).OrderBy(x => x.Start).ToList();
        if (spots.Count == 0) { Status = "nenhum ponto duvidoso"; Changed?.Invoke(); return; }
        var next = spots.FirstOrDefault(x => x.Start > now, spots[0]);
        Picked = next;
        Player.Seek((ulong)Math.Max(0, (next.Start + Compensation) * Rate));
        Status = $"{spots.Count} pontos duvidosos · este: {PickedLabel}";
        Changed?.Invoke();
    }
    public int Doubts => ResultTake is { } t ? t.Chords.Count(c => c.Confidence < 0.5f) + t.Notes.Count(n => n.Confidence < 0.5f) + t.Choir.Values.Sum(l => l.Count(n => n.Confidence < 0.5f)) : 0;
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
        foreach (var t in Enumerable.Range(0, Tracks)) Channel(t).VstSignature = "";   // the player dropped its sources
        ApplyMix();
        ApplyVst();
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
        // Units patched before an insert are rendered into it: render again (the cache keeps what did not change).
        if (Enumerable.Range(0, Tracks).Any(t => Channel(t).Vst.Any(v => v.On))) ApplyVst();
        RefreshResult();   // the analysis tap of an edited chain is another input
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- S10 the chain as patched

    /// Moves a module (unit or insert) to `index` in the chain of `track` (a cable dragged elsewhere).
    public void MoveSlot(int track, string slot, int index)
    {
        var c = Channel(track);
        var l = c.Slots();
        int from = l.IndexOf(slot);
        if (from < 0) l.Add(slot);   // an unpatched unit plugged back in
        else l.RemoveAt(from);
        l.Insert(Math.Clamp(from >= 0 && index > from ? index - 1 : index, 0, l.Count), slot);
        c.Chain = l;
        FixParallel(c);
        MixChanged();
    }

    /// Pulls a built-in unit's cables: out of the chain (it stays in the drawer, settings kept).
    public void Unpatch(int track, string unit)
    {
        var c = Channel(track);
        var l = c.Slots();
        l.Remove(unit);
        c.Chain = l;
        FixParallel(c);
        MixChanged();
    }

    /// Adds a unit to the parallel path, or takes an end unit out of it; the path is always one run
    /// of built-in units with no insert between them.
    public void ToggleParallel(int track, string unit)
    {
        var c = Channel(track);
        var l = c.Slots();
        if (c.ParallelFirst is null) (c.ParallelFirst, c.ParallelLast) = (unit, unit);
        else if (unit == c.ParallelFirst && unit == c.ParallelLast) (c.ParallelFirst, c.ParallelLast) = (null, null);
        else if (unit == c.ParallelFirst) c.ParallelFirst = l[l.IndexOf(unit) + 1];
        else if (unit == c.ParallelLast) c.ParallelLast = l[l.IndexOf(unit) - 1];
        else if (l.IndexOf(unit) < l.IndexOf(c.ParallelFirst)) c.ParallelFirst = unit;
        else c.ParallelLast = unit;
        if (c.ParallelFirst is not null && c.ParallelMix <= 0) c.ParallelMix = 0.5f;
        FixParallel(c);
        MixChanged();
    }

    static void FixParallel(ChannelMix c)
    {
        var l = c.Slots();
        int a = c.ParallelFirst is null ? -1 : l.IndexOf(c.ParallelFirst), b = c.ParallelLast is null ? -1 : l.IndexOf(c.ParallelLast);
        bool ok = a >= 0 && b >= a && l.Skip(a).Take(b - a + 1).All(ChannelMix.Units.Contains);
        if (!ok) (c.ParallelFirst, c.ParallelLast) = (null, null);
    }

    public void Bounce()
    {
        if (Player is null || Current is null) return;
        StudioProject.EnsureDir();
        var path = StudioProject.BouncePath(Current);
        Status = Player.Bounce(path) ? "bounce: " + path : "bounce falhou";
        Changed?.Invoke();
    }

    public readonly ScopeData Scopes = new();
    public void PollMeters(long? nowMs = null)
    {
        if (Player is null) return;
        Player.Monitor(Selected);
        Player.Refresh();
        Player.ReadMeters(Meters);
        Scopes.Update(Player, Meters, Rate, Player.Playing, nowMs ?? Environment.TickCount64);
    }
    public void ResetLoudness() { Player?.ResetLoudness(); Array.Fill(Scopes.Momentary, -120f); Array.Fill(Scopes.ShortTerm, -120f); }

    /// The analysers a channel's tap feeds (Plan): the voice notes, the harmony, the bass line, the beats.
    public string[] AnalysersOf(int track)
    {
        if (Stems.Count == 0) return track == 0 ? Choir ? ["DA-K", "DA-C"] : [Mode is AppMode.VoiceMono or AppMode.InstrumentMono ? "DA-N" : "DA-C"] : [];
        return track == 0 ? [] : NameOf(track) switch
        {
            "vocals" => [Choir ? "DA-K" : "DA-N"], "bass" => ["DA-C", "DA-B"], "drums" => ["DA-T"], _ => ["DA-C"],
        };
    }
    public void Select() => Changed?.Invoke();
    public bool ConsoleView;
    // Panels that open and close as in a DAW (the transport's view keys): the browser, the signal
    // routing, the measuring tubes. The arrangement takes the room they leave.
    public bool ShowBrowser = true, ShowRouting, ShowMeters;

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
    sealed record Pass(Session Session, uint Mask, string Input, string Suffix, string Options, bool Beats = false, bool Choir = false);
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
        if (Stems.Count == 0 && Choir)
        {   // S9: the choir by section and its harmony, both on the mix
            var cs = AnalysisSession(AppMode.VoiceMono);
            var hs = AnalysisSession(AppMode.GeneralChords);
            string tap = ";tap=" + Channel(0).TapKey;
            return [new Pass(cs, 1, StudioProject.TapPath(Current, "mix"), ".choir", Options(cs, ";choir" + tap), Choir: true),
                    new Pass(hs, 1, StudioProject.TapPath(Current, "mix"), ".harmony", Options(hs, tap))];
        }
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
            Choir   // S9: the vocals stem is the choir
                ? new(voice, vm, StudioProject.TapPath(Current, "voice"), ".choir", Options(voice, ";choir;stems=" + StemsKey + ";tap=" + Keys(vm)), Choir: true)
                : new(voice, vm, StudioProject.TapPath(Current, "voice"), ".voice", Options(voice, ";stems=" + StemsKey + ";tap=" + Keys(vm))),
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
        Result = done.Count == 1 ? WithInstruments(done[0]) : MergeLeadSheet(plan.Zip(done, (p, d) => (p.Suffix, d!)).ToDictionary());
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
            var root = LeadSheet.Merge(Read(results[".harmony"]).AsObject(), Read(results.TryGetValue(".choir", out var ch) ? ch : results[".voice"])["events"]!.AsArray(), bass);
            if (results.TryGetValue(".beats", out var d)) LeadSheet.AddBeats(root, Read(d).AsObject(), FromTake && Edits.KeepsGrid ? Session.Bpm : 0);
            AddInstruments(root);
            var path = StudioProject.LeadSheetPath(Current!);
            File.WriteAllText(path, root.ToJsonString());
            return path;
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or NullReferenceException) { return null; }
    }

    /// S9: the choir by section (EXPERIMENTAL), after the five modes in the analyser's cycle.
    public bool Choir;
    public void CycleMode()
    {
        if (Choir) { Choir = false; Mode = AppMode.VoiceMono; }
        else if (Mode == AppMode.GeneralChords) Choir = true;
        else Mode = (AppMode)((int)Mode + 1);
        RefreshResult();
    }

    public void Analyze()
    {
        if (Job is null || Player is null || Current is null || Running || Separating) return;
        if (RenderingVst) { Status = "espere os VST3 renderizarem: a análise lê o canal depois deles"; Changed?.Invoke(); return; }
        RefreshResult();
        if (Result is not null && PolyMissing.Count == 0 && !Mt3Missing) { Status = "resultado em cache (mesmas opções e edições)"; Changed?.Invoke(); return; }
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

    // ---------------------------------------------------------------- instruments (Basic Pitch per stem)
    // The pitched stems besides the voice and the bass get polyphonic notes from the neural
    // transcriber, read from the stem as separated (before the channel's inserts), cached per stems key.
    static readonly string[] PolyStems = ["other", "guitar", "piano"];
    Task? _poly;
    CancellationTokenSource? _polyCancel;
    volatile float _polyProgress;

    sealed record PolyResult(string StemsKey, List<BasicPitch.Note> Notes);
    string PolyPath(string source, string stem) => StudioProject.ResultPath(source, ".poly-" + stem);

    List<BasicPitch.Note>? PolyNotes(string stem)
    {
        try
        {
            var r = JsonSerializer.Deserialize<PolyResult>(File.ReadAllText(PolyPath(Current!, stem)));
            return r?.StemsKey == StemsKey ? r.Notes : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    List<StemTrack> PolyMissing => BasicPitch.Available && Current is not null ? Stems.Where(t => PolyStems.Contains(t.Name) && PolyNotes(t.Name) is null).ToList() : [];

    bool StartPoly()
    {
        var todo = PolyMissing;
        if (todo.Count == 0) return false;
        string source = Current!, key = StemsKey;
        double comp = Compensation;
        var cancel = _polyCancel = new CancellationTokenSource();
        _polyProgress = 0;
        _poly = Task.Run(() =>
        {
            using var bp = new BasicPitch();
            for (int i = 0; i < todo.Count; i++)
            {
                var mono = StemTrack.ReadMono(todo[i].Path, out int rate);
                int k = i;
                var notes = bp.Notes(BasicPitch.Resample(mono, rate), p => { _polyProgress = (float)((k + p) / todo.Count); return !cancel.IsCancellationRequested; });
                if (cancel.IsCancellationRequested) return;
                // Event time, as the core's results: the file time minus the input compensation.
                notes = notes.Select(n => n with { Start = n.Start - comp, End = n.End - comp }).ToList();
                File.WriteAllText(PolyPath(source, todo[i].Name), JsonSerializer.Serialize(new PolyResult(key, notes)));
            }
        });
        Running = true;
        Status = "transcrevendo os instrumentos (Basic Pitch)…";
        return true;
    }

    void PollPoly()
    {
        if (_poly is null) return;
        Progress = _polyProgress;
        if (!_poly.IsCompleted) return;
        var t = _poly;
        _poly = null;
        Running = false;
        if (t.IsFaulted) { Status = "instrumentos falharam: " + t.Exception?.InnerException?.Message; Changed?.Invoke(); return; }
        if (_polyCancel?.IsCancellationRequested == true) { Status = "cancelado"; Changed?.Invoke(); return; }
        if (StartMt3()) { Changed?.Invoke(); return; }
        RefreshResult();
        FinishStatus();
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- instruments (YourMT3+, optional)
    // The whole take (as edited) through YourMT3+: every note with its instrument, cached per edits.
    YourMt3Job? _mt3;
    string Mt3Input => StudioProject.TapPath(Current!, "mt3");   // the mix channel at its tap: any source format, as edited
    string Mt3Path(string source) => StudioProject.ResultPath(source, ".mt3");
    string Mt3Key => Edits.Key + ";tap=" + Channel(0).TapKey;   // edits and the mix channel's inserts

    YourMt3.Result? Mt3Notes()
    {
        if (Current is null) return null;
        string key = Mt3Path(Current) + ".key";
        return File.Exists(key) && File.ReadAllText(key) == Mt3Key ? YourMt3Job.Load(Mt3Path(Current)) : null;
    }

    bool Mt3Missing => YourMt3Job.Installed && Current is not null && Mt3Notes() is null;

    bool StartMt3()
    {
        if (!Mt3Missing) return false;
        if (Player?.RenderTap(1, Mt3Input) != true) return false;
        _mt3 = YourMt3Job.Start(Mt3Input, Mt3Path(Current!), out var err);
        if (_mt3 is null) { Status = "YourMT3+ não iniciou: " + err; return false; }
        _mt3Source = Current;
        Running = true;
        Status = "YourMT3+: carregando…";
        return true;
    }
    string? _mt3Source;

    void PollMt3()
    {
        if (_mt3 is null) return;
        if (_mt3.Running)
        {
            Progress = _mt3.Progress;
            Status = "YourMT3+: " + (_mt3.Stage.StartsWith("inference") ? $"transcrevendo ({_mt3.Stage[10..]})…" : "carregando o modelo…");
            Changed?.Invoke();
            return;
        }
        var job = _mt3;
        _mt3 = null;
        Running = false;
        if (job.Succeeded && _mt3Source is not null) File.WriteAllText(Mt3Path(_mt3Source) + ".key", Mt3Key);
        else { Status = "YourMT3+ falhou: " + job.Error; Changed?.Invoke(); return; }
        RefreshResult();
        FinishStatus();
        Changed?.Invoke();
    }

    /// The instruments into a result's events: Basic Pitch per stem ("inst:<stem>") and YourMT3+
    /// over the whole take ("mt3:<program>:<instrument>"; times made event time like the core's).
    void AddInstruments(System.Text.Json.Nodes.JsonObject root)
    {
        int fifths = (int?)root["session"]?["keyFifths"] ?? 0;
        var events = root["events"]!.AsArray();
        void Add(string part, double start, double end, int midi, double confidence) => events.Add(new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = "note", ["part"] = part, ["start"] = Math.Round(start, 4), ["end"] = Math.Round(end, 4),
            ["midi"] = midi, ["name"] = Review.NoteName(midi, fifths), ["confidence"] = Math.Round(confidence, 3),
        });
        foreach (var stem in PolyStems)
            foreach (var n in PolyNotes(stem) ?? []) Add("inst:" + stem, n.Start, n.End, n.Midi, n.Confidence);
        foreach (var n in Mt3Notes()?.Notes ?? [])
            Add($"mt3:{(n.Drum ? 128 : n.Program)}:{n.Instrument}", n.Start - Compensation, n.End - Compensation, n.Midi, n.Velocity / 127.0);
    }

    /// A single analysis result with the instruments added (a copy; the cached result stays as the core wrote it).
    string? WithInstruments(string? path)
    {
        if (path is null || Current is null || Mt3Notes() is null) return path;
        try
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            AddInstruments(root);
            File.WriteAllText(StudioProject.LeadSheetPath(Current), root.ToJsonString());
            return StudioProject.LeadSheetPath(Current);
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException) { return path; }
    }

    void Next()
    {
        if (Job is null || _passSource is null) return;
        if (!_queue.TryDequeue(out var p)) { _pass = null; if (StartPoly() || StartMt3()) return; Running = false; RefreshResult(); FinishStatus(); return; }
        var err = Job.Start(p.Session, Compensation, p.Input, StudioProject.ResultPath(_passSource, p.Suffix), FromTake && Edits.KeepsGrid, p.Beats, p.Choir);
        if (err is not null) { Running = false; Status = "falhou: " + err; return; }
        (_pass, Running) = (p, true);
        Status = p.Suffix switch { ".voice" => "transcrevendo a voz…", ".harmony" => "transcrevendo a harmonia…", ".bass" => "transcrevendo o baixo…", ".beats" => "rastreando a bateria…", _ => "analisando…" };
    }

    void FinishStatus() =>
        Status = ResultTake is { } t ? $"pronto · {t.Notes.Count} notas · {t.Chords.Count} acordes" + (t.BassNotes.Count > 0 ? $" · {t.BassNotes.Count} do baixo" : "")
            + (t.Beats.Count > 0 ? $" · bateria {t.TempoBpm:0.0} BPM" + (t.MaxDriftMs is double dr ? $", deriva máx {dr:0} ms" + (dr > LeadSheet.DriftFlagMs ? " (fora do metrônomo)" : "") : "") : "")
            + (t.Instruments.Count > 0 ? " · " + string.Join(", ", t.Instruments.Select(i => $"{i.Value.Count} {i.Key}")) : "")
            + (Stems.Count > 0 ? " · das faixas separadas" : "") : "pronto";

    public void Cancel() { _queue.Clear(); _polyCancel?.Cancel(); _mt3?.Cancel(); Job?.Cancel(); _sep?.Cancel(); _install?.Cancel(); }

    // ---------------------------------------------------------------- S8 VST3 plugins

    public List<PluginHost.Entry> PluginScan = PluginHost.Cached();
    public IEnumerable<PluginHost.Plugin> Plugins => PluginScan.SelectMany(e => e.Plugins).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase);
    public bool ScanningPlugins;
    public bool PluginView;   // the library shows the installed effects instead of the takes

    public async void ScanPlugins()
    {
        if (!PluginHost.Present || ScanningPlugins) return;
        ScanningPlugins = true;
        Status = "procurando plugins VST3…";
        Changed?.Invoke();
        try
        {
            PluginScan = await PluginHost.ScanAsync((d, n) => Avalonia.Threading.Dispatcher.UIThread.Post(() => { Status = $"procurando plugins VST3… {d}/{n}"; Changed?.Invoke(); }));
            int bad = PluginScan.Count(e => e.Error is not null);
            Status = $"VST3: {Plugins.Count()} efeitos em {PluginScan.Count} pacotes" + (bad > 0 ? $" · {bad} com erro" : "");
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { Status = "VST3: " + e.Message; }
        ScanningPlugins = false;
        Changed?.Invoke();
    }

    // VST3 inserts: before a channel's chain, rendered offline through the helper; the player
    // (and so the analysis tap and the bounce) reads the rendered audio. Cached by a key of the
    // source and every insert with its saved state.
    public const int MaxVst = 5;
    public bool RenderingVst;
    public float VstProgress;
    public string VstWhat = "";
    CancellationTokenSource? _vstCts;

    string VstKey(int t)
    {
        string src = t == 0 ? "take;" + Edits.Key : "stem;" + StemsKey + ";" + NameOf(t);
        var c = Channel(t);
        var segs = c.Segments();
        return src + "|" + string.Join("|", OnInserts(c).Select((v, i) =>
        {
            string st = StudioProject.VstStatePath(Current!, v.Id);
            return ChainHash(c, segs[i]) + ">" + v.Cid + ":" + (File.Exists(st) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(st)))[..16] : "-")
                + ":" + string.Join(",", v.Params.OrderBy(p => p.Key).Select(p => FormattableString.Invariant($"{p.Key}={p.Value:R}")));
        }));
    }

    /// The inserts that are on, in the order they are patched.
    static List<VstInsert> OnInserts(ChannelMix c) => c.Slots().Select(c.VstOf).OfType<VstInsert>().Where(v => v.On).ToList();

    /// The built-in units patched before an insert, as the render sees them ("" = none).
    static string ChainHash(ChannelMix c, List<string> units)
    {
        if (units.Count == 0) return "";
        var n = c.NativeFor(units);
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<ChannelParamsNative>(ref n)).ToArray();
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..12];
    }

    public void AddVst(PluginHost.Plugin plugin)
    {
        if (Current is null) return;
        var c = Channel(Selected);
        if (c.Vst.Count >= MaxVst) { Status = $"no máximo {MaxVst} VST3 por canal"; Changed?.Invoke(); return; }
        c.Vst.Add(new VstInsert { Bundle = plugin.Bundle, Cid = plugin.Cid, Name = plugin.Name, Vendor = plugin.Vendor });
        StudioProject.SaveMix(Current, Mix);
        ApplyVst();
    }

    public void RemoveVst(int track, VstInsert v)
    {
        if (Current is null) return;
        Channel(track).Vst.Remove(v);
        if (OpenVst == v) OpenVst = null;
        try { File.Delete(StudioProject.VstStatePath(Current, v.Id)); } catch (IOException) { }
        StudioProject.SaveMix(Current, Mix);
        ApplyVst();
    }

    public void ToggleVst(VstInsert v) { if (Current is null) return; v.On = !v.On; StudioProject.SaveMix(Current, Mix); ApplyVst(); }
    /// A track header's FX power: every insert of the track on or bypassed at once.
    public void SetVstOn(int track, bool on)
    {
        if (Current is null) return;
        foreach (var v in Channel(track).Vst) v.On = on;
        StudioProject.SaveMix(Current, Mix);
        ApplyVst();
    }

    /// Puts every channel's VST3 render in the player: from the cache, or rendered now (in order,
    /// insert by insert, each reading the previous one's output).
    public async void ApplyVst()
    {
        if (Player is null || Current is null) return;
        _vstCts?.Cancel();
        var cts = _vstCts = new CancellationTokenSource();
        string take = Current;
        bool changed = false;
        for (int t = 0; t < Tracks; t++)
        {
            var c = Channel(t);
            var on = OnInserts(c);
            var segs = c.Segments();
            if (on.Count == 0 || !PluginHost.Present)
            {
                if (c.VstSignature.Length > 0) { Player.SetSource(t, null); c.VstSignature = ""; changed = true; }
                continue;
            }
            string outPath = StudioProject.VstRenderPath(take, NameOf(t)), keyPath = outPath + ".key";
            string key = VstKey(t);
            if (File.Exists(outPath) && File.Exists(keyPath) && File.ReadAllText(keyPath) == key)
            {
                if (c.VstSignature != key) { Player.SetSource(t, outPath); c.VstSignature = key; changed = true; }
                continue;
            }
            StudioProject.EnsureDir();
            string src = t == 0 ? (Edits.IsEmpty ? take : StudioProject.EditedPath(take)) : Stems[t - 1].Path;
            if (t == 0 && !Edits.IsEmpty && !Player.SaveWav(src)) { Status = "VST3: não foi possível gravar o take editado"; break; }
            RenderingVst = true;
            string? err = null, cur = src;
            for (int i = 0; i < on.Count && err is null; i++)
            {
                if (segs[i].Count > 0)
                {   // the units patched before this insert, rendered into it
                    string pre = $"{outPath}.c{i}.wav";
                    VstWhat = $"cadeia antes de {on[i].Name} · canal {t + 1}";
                    var n = c.NativeFor(segs[i]);
                    string inFile = cur!;
                    int rc = await Task.Run(() => MixApi.RenderChain(inFile, n, pre));
                    if (rc != 0) { err = "não foi possível renderizar a cadeia"; break; }
                    if (cur != src) File.Delete(cur!);
                    cur = pre;
                }
                string next = i == on.Count - 1 ? outPath + ".part.wav" : $"{outPath}.{i}.wav";
                int k = i;
                VstWhat = $"{on[i].Name} · canal {t + 1}";
                err = await PluginHost.RenderAsync(on[i].Plugin, cur, next, StudioProject.VstStatePath(take, on[i].Id), StudioProject.VstStatePath(take, on[i].Id),
                    on[i].Params, f => Avalonia.Threading.Dispatcher.UIThread.Post(() => { VstProgress = (k + f) / on.Count; Changed?.Invoke(); }), cts.Token);
                if (cur != src) File.Delete(cur);
                cur = next;
            }
            if (cts.IsCancellationRequested || Current != take) { RenderingVst = false; return; }   // a newer change took over
            if (err is not null)
            {
                Status = $"VST3 ({VstWhat}): {err} — o canal toca sem os VST3";
                Player.SetSource(t, null);
                c.VstSignature = "";
                changed = true;
                continue;
            }
            File.Move(cur!, outPath, true);
            key = VstKey(t);   // after the render: the plugins saved their state
            File.WriteAllText(keyPath, key);
            if (Player.SetSource(t, outPath) is { } e2) Status = "VST3: " + e2;
            c.VstSignature = key;
            changed = true;
        }
        RenderingVst = false;
        VstProgress = 0;
        if (changed) { RefreshResult(); Changed?.Invoke(); }
        if (OpenVst is { } ov) await LoadParams(ov);   // the texts as the plugin now has them
    }

    // The generic parameter panel of one insert (any plugin, no editor needed).
    public VstInsert? OpenVst;
    public List<PluginHost.Param> OpenParams = [];
    public int ParamPage;
    Avalonia.Threading.DispatcherTimer? _paramTimer;

    public async void OpenPanel(VstInsert? v)
    {
        OpenVst = v;
        OpenParams = [];
        ParamPage = 0;
        Changed?.Invoke();
        if (v is not null) await LoadParams(v);
    }

    async Task LoadParams(VstInsert v)
    {
        if (Current is null) return;
        try
        {
            var ps = await PluginHost.ParamsAsync(v.Plugin, StudioProject.VstStatePath(Current, v.Id));
            if (OpenVst == v) { OpenParams = ps.Where(p => p.Editable).ToList(); Changed?.Invoke(); }
        }
        catch (IOException e) { Status = $"VST3 {v.Name}: {e.Message}"; Changed?.Invoke(); }
    }

    public bool EditorOpen;

    /// The plugin's own editor (a window of the helper); its edits join the panel's values.
    public async void OpenEditor(VstInsert v)
    {
        if (Current is null || EditorOpen) return;
        EditorOpen = true;
        Status = $"editor de {v.Name} aberto: feche a janela dele para aplicar";
        Changed?.Invoke();
        string take = Current;
        var r = await PluginHost.EditorAsync(v.Plugin, StudioProject.VstStatePath(take, v.Id), v.Params);
        EditorOpen = false;
        if (Current != take) return;
        foreach (var (id, val) in r.Edits) v.Params[id] = val;
        Status = r.Error is null ? $"{v.Name}: {r.Edits.Count} parâmetro(s) editado(s)" : $"VST3 {v.Name}: {r.Error}";
        StudioProject.SaveMix(take, Mix);
        ApplyVst();   // the state changed: a new render, then the panel reads it back
    }

    /// A knob in the panel: the value now, the render 0.7 s after the last change.
    public void SetParam(VstInsert v, PluginHost.Param p, double value)
    {
        if (Current is null) return;
        if (p.Steps > 0) value = Math.Round(value * p.Steps) / p.Steps;
        v.Params[p.Id] = value;
        int i = OpenParams.IndexOf(p);
        if (i >= 0) OpenParams[i] = p with { Value = value, Text = "" };
        StudioProject.SaveMix(Current, Mix);
        _paramTimer ??= new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(700), Avalonia.Threading.DispatcherPriority.Background, (_, _) => { _paramTimer!.Stop(); ApplyVst(); });
        _paramTimer.Stop();
        _paramTimer.Start();
    }

    // ---------------------------------------------------------------- recording in STUDIO
    // The core's recorder (the same as LIVE's REC: input, count-in and metronome from START, a
    // take WAV + JSON sidecar in the takes folder); the take opens here when it is written.

    /// The window's side: the live frame (input level, REC state), REC itself (null or an error),
    /// the input's name.
    public sealed record InputHooks(Func<LiveFrame> Frame, Func<string?> ToggleRec, Func<string> Device);
    public InputHooks? Input;
    public string InputName = "";
    bool _ownRec, _wasRec;
    DateTime _recStarted;
    long _openBy;
    public LiveFrame? InputFrame => Input?.Frame();
    public bool Recording => InputFrame is { Simulated: false } f && (f.Recording || f.CountingIn);

    public void ToggleRec()
    {
        if (Input is null) { Status = "gravação ausente nesta instalação"; Changed?.Invoke(); return; }
        bool starting = !Recording;
        if (starting)
        {
            if (Running || Separating) { Status = "espere a análise / separação terminar para gravar"; Changed?.Invoke(); return; }
            Player?.Stop();
            InputName = Input.Device();
            _recStarted = DateTime.Now;
        }
        var err = Input.ToggleRec();
        if (err is not null) { Status = "REC: " + err; Changed?.Invoke(); return; }
        _ownRec = true;
        Status = starting ? $"REC armado: contagem de {Session.CountInBars} compasso(s) a {Session.Bpm:0} BPM · entrada {InputName}" : "REC parado: gravando o take…";
        Changed?.Invoke();
    }

    void PollRecording()
    {
        bool rec = Recording;
        if (_wasRec && !rec && _ownRec) _openBy = Environment.TickCount64 + 5000;   // the core finishes the WAV and the sidecar
        _wasRec = rec;
        if (_openBy == 0) return;
        if (Environment.TickCount64 > _openBy) { _openBy = 0; _ownRec = false; Status = "take gravado, mas não apareceu na biblioteca: ATUALIZAR"; Changed?.Invoke(); return; }
        var take = Directory.Exists(Take.Folder) ? Directory.GetFiles(Take.Folder, "take-*.wav").Where(p => File.GetLastWriteTime(p) >= _recStarted.AddSeconds(-1)).OrderByDescending(File.GetLastWriteTime).FirstOrDefault() : null;
        if (take is null || !File.Exists(Path.ChangeExtension(take, ".json"))) return;
        _openBy = 0;
        _ownRec = false;
        ReloadLibrary();
        Open(take);
        Status = $"take gravado: {Path.GetFileNameWithoutExtension(take)} · entrada {InputName}";
        Changed?.Invoke();
    }

    public void Poll()
    {
        PollRecording();
        PollSeparation();
        PollPoly();
        PollMt3();
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
    protected void Key(DrawingContext ctx, Rect r, string label, Action action, bool lit = false, IBrush? led = null, bool enabled = true, bool withLed = true)
    {
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF0B0C0D)), null, r.Translate(new Vector(0, 2)), 3, 3);
        ctx.DrawRectangle(KeyFace, KeyEdge, r, 3, 3);
        Ui.Text(ctx, label, r.Center.X, r.Center.Y - 7, 11, enabled ? Ui.LabelBright : Ui.Label, Ui.SansBold, Ui.Align.Center);
        if (withLed) Ui.Led(ctx, new Point(r.Right - 6, r.Y + 6), 2.6, lit, led ?? Ui.Green, Ui.GreenOff);
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
    /// Where the stem sits in the stereo image: a dot from L to R, its width as a bar around it.
    protected static void PanMeter(DrawingContext ctx, double x, double y, double w, StemTrack t)
    {
        double cx = x + w / 2, px = cx + t.Pan * w / 2, half = Math.Max(2.5, t.Width * w / 2);
        ctx.DrawLine(PhosphorLine, new Point(x, y), new Point(x + w, y));
        ctx.DrawLine(PhosphorLine, new Point(cx, y - 4), new Point(cx, y + 4));
        ctx.DrawRectangle(Ui.AmberOff, null, new Rect(Math.Max(x, px - half), y - 3, Math.Min(x + w, px + half) - Math.Max(x, px - half), 6), 2, 2);
        ctx.DrawEllipse(Ui.Amber, null, new Point(px, y), 3, 3);
        Ui.Text(ctx, "L", x - 8, y - 6, 8, Ui.Label, Ui.SansBold);
        Ui.Text(ctx, "R", x + w + 3, y - 6, 8, Ui.Label, Ui.SansBold);
        Ui.Text(ctx, t.PanLabel, cx, y + 5, 8, Phosphor, Ui.Mono, Ui.Align.Center);
    }

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

    // ----------------------------------------------------------------- signal flow
    // Patch cables carry the signal's level in their core colour (dark: nothing; amber: signal;
    // bright: hot, over -6 dBFS; red: over 0) and an LED at the middle, so the route reads at a
    // glance, as on a desk or a pedalboard.
    static readonly IPen CableJacket = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF0B0C0D)), 7, lineCap: PenLineCap.Round);
    static readonly IPen CableJacketThin = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF0B0C0D)), 4, lineCap: PenLineCap.Round);
    static IBrush LevelBrush(float db) => new ImmutableSolidColorBrush(Color.FromUInt32(
        db > -0.5f ? 0xFFFF3B30u : db > -6 ? 0xFFFFD27Au : db > -60 ? 0xFFFFB000u : 0xFF4A3A12u));
    protected static IBrush LevelLed(float db) => db > -0.5f ? Ui.Red : db > -6 ? Ui.Amber : Ui.Green;

    /// A patch cable from a to b with its sag; dashed for a send (a copy of the signal, not its path).
    protected static void Patch(DrawingContext ctx, Point a, Point b, float db, double sag = 18, bool send = false, bool thin = false, bool led = true)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(a, false);
            if (sag == 0) c.LineTo(b);
            else c.CubicBezierTo(new Point(a.X + (b.X - a.X) * 0.3, Math.Max(a.Y, b.Y) + sag), new Point(a.X + (b.X - a.X) * 0.7, Math.Max(a.Y, b.Y) + sag), b);
            c.EndFigure(false);
        }
        var core = LevelBrush(db);
        if (send) ctx.DrawGeometry(null, new ImmutablePen((IImmutableBrush)core, 1.6, new ImmutableDashStyle([3, 2.5], 0)), g);
        else
        {
            ctx.DrawGeometry(null, thin ? CableJacketThin : CableJacket, g);
            ctx.DrawGeometry(null, new ImmutablePen((IImmutableBrush)core, thin ? 2 : 4, lineCap: PenLineCap.Round), g);
        }
        if (led && !send)
        {
            var m = sag == 0 ? new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2) : new Point((a.X + b.X) / 2, Math.Max(a.Y, b.Y) + sag * 0.75);
            Ui.Led(ctx, m, 2.4, db > -60, LevelLed(db), Ui.GreenOff);
        }
        // The signal travelling: pulses run along the cable from source to destination while there
        // is signal, brighter and bigger with the level.
        if (db <= -60) return;
        Point P(double u)
        {
            if (sag == 0) return new Point(a.X + (b.X - a.X) * u, a.Y + (b.Y - a.Y) * u);
            var c1 = new Point(a.X + (b.X - a.X) * 0.3, Math.Max(a.Y, b.Y) + sag);
            var c2 = new Point(a.X + (b.X - a.X) * 0.7, Math.Max(a.Y, b.Y) + sag);
            double v = 1 - u;
            return new Point(v * v * v * a.X + 3 * v * v * u * c1.X + 3 * v * u * u * c2.X + u * u * u * b.X,
                             v * v * v * a.Y + 3 * v * v * u * c1.Y + 3 * v * u * u * c2.Y + u * u * u * b.Y);
        }
        double len = Math.Max(1, Math.Abs(b.X - a.X) + Math.Abs(b.Y - a.Y) + Math.Abs(sag));
        const double spacing = 26, speed = 70;   // px between pulses, px/s
        double phase = Environment.TickCount64 / 1000.0 * speed % spacing;
        float k = Math.Clamp((db + 60) / 60, 0, 1);
        var pulse = new ImmutableSolidColorBrush(Color.FromArgb((byte)(90 + 165 * k), 255, 236, 190));
        for (double d = phase; d < len; d += spacing)
            ctx.DrawEllipse(pulse, null, P(d / len), thin || send ? 1.2 : 1.4 + 1.4 * k, thin || send ? 1.2 : 1.4 + 1.4 * k);
    }

    /// A jack socket on a panel.
    protected static void Jack(DrawingContext ctx, Point c)
    {
        ctx.DrawEllipse(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF5B6068)), null, c, 6, 6);
        ctx.DrawEllipse(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF0B0C0D)), null, c, 2.5, 2.5);
    }

    static readonly IBrush Chrome = new ImmutableRadialGradientBrush(
        [new ImmutableGradientStop(0, Color.FromUInt32(0xFFE4E8EE)), new ImmutableGradientStop(0.6, Color.FromUInt32(0xFF8A9099)), new ImmutableGradientStop(1, Color.FromUInt32(0xFF3A3E44))],
        center: new RelativePoint(0.35, 0.3, RelativeUnit.Relative), radiusX: new RelativeScalar(0.8, RelativeUnit.Relative), radiusY: new RelativeScalar(0.8, RelativeUnit.Relative));

    /// A pedal's footswitch: chrome stomp button in its nut, the red LED above it lit when engaged.
    protected void Footswitch(DrawingContext ctx, Point c, bool on, Action toggle, bool enabled = true)
    {
        ctx.DrawEllipse(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF1A1C1F)), KnobRim, c, 13, 13);
        ctx.DrawEllipse(Chrome, KnobRim, c, 9, 9);
        Ui.Led(ctx, new Point(c.X, c.Y - 21), 3, on, Ui.Red, Ui.RedOff);
        if (enabled) _keys.Add((new Rect(c.X - 14, c.Y - 26, 28, 40), toggle));
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
    public TransportUnit(StudioState s) : base(s, "DS-T", "TRANSPORT") { Height = 128; }

    protected override void DrawContent(DrawingContext ctx, Rect r)
    {
        var p = S.Player;
        p?.Refresh();
        double y = r.Y + 6, x = r.X;
        Key(ctx, new Rect(x, y + 4, 48, 34), p?.Playing == true ? "■" : "▶", S.TogglePlay, p?.Playing == true);
        Key(ctx, new Rect(x + 56, y + 4, 48, 34), "LOOP", S.ToggleLoop, p?.Looping == true, Ui.Amber);
        Key(ctx, new Rect(x + 112, y + 4, 82, 34), S.ConsoleView ? "GRAVADOR" : "CONSOLE", () => { S.ConsoleView = !S.ConsoleView; S.Select(); }, S.ConsoleView, Ui.Amber);
        // REC: the core's recorder through the START input; the LED blinks on the count-in.
        var inF = S.InputFrame;
        bool rec = S.Recording, countIn = rec && inF!.CountingIn;
        Key(ctx, new Rect(x + 202, y + 4, 48, 34), "●", S.ToggleRec, rec && (!countIn || inF!.BeatInBar % 2 == 1), Ui.Red, S.Input is not null);
        x += 268;
        if (rec)
        {   // while recording the readouts show the take, not the player
            double rs = inF!.RecordedSeconds;
            var rts = TimeSpan.FromSeconds(Math.Max(0, rs));
            x += Readout(ctx, x, y, countIn ? $"-{Math.Ceiling(-rs):0}" : $"{(int)rts.TotalMinutes}:{rts.Seconds:00}.{rts.Milliseconds / 10:00}", 7, 3.0, countIn ? "CONTAGEM" : "GRAVANDO") + 12;
            x += Readout(ctx, x, y, $"{inF.PeakDbfs:0.0}", 6, 2.4, "ENTRADA dBFS") + 12;
        }
        else
        {
            double sec = (p?.Position ?? 0) / S.Rate, mus = sec - S.GridOrigin;
            int bar = (int)Math.Floor(mus / S.Bar), beat = (int)Math.Floor((mus - bar * S.Bar) / S.Beat), six = (int)Math.Floor((mus - bar * S.Bar - beat * S.Beat) / (S.Beat / 4));
            x += Readout(ctx, x, y, mus < 0 ? "0.0.0" : $"{bar + 1}.{beat + 1}.{six + 1}", 7, 3.0, "POSIÇÃO") + 12;
            var t = TimeSpan.FromSeconds(Math.Max(0, sec));
            x += Readout(ctx, x, y, $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 10:00}", 7, 2.4, "TEMPO") + 12;
        }
        x += Readout(ctx, x, y, $"{(S.Review.TempoFromDrums && S.ResultTake is { } rt ? rt.Bpm : S.Session.Bpm):0.0}", 5, 2.2, S.Review.TempoFromDrums ? "BPM BATERIA" : "BPM") + 12;
        x += Readout(ctx, x, y, $"{S.Session.BeatsPerBar}/{S.Session.BeatUnit}", 3, 2.2, "COMPASSO") + 12;
        // S6: the key is the user's; STUDIO only suggests. Tempo: the metronome, or the drums' map.
        string KeyShort(KeyOption k) => k.Tonic.Replace("♭", "b").Replace("♯", "#") + (k.Minor ? "m" : "");
        x += Readout(ctx, x, y, S.Session.KeySet ? KeyShort(S.Session.Key) : "--", 3, 2.2, "TOM") + 8;
        if (S.Suggestion is { } sg && !(S.Session.KeySet && S.Session.Key == sg.Key))
        {
            bool unsure = sg.Margin < 0.05 || sg.Correlation < 0.6;
            Key(ctx, new Rect(x, y, 100, 20), $"APLICAR {KeyShort(sg.Key)}", () => S.ApplyKey(sg.Key), false, Ui.Amber, !S.Running);
            Ui.Text(ctx, $"sugestão {sg.Correlation * 100:0}%{(unsure ? " · incerta" : "")}", x + 50, y + 22, 7, unsure ? Ui.Label : Ui.Amber, Ui.SansBold, Ui.Align.Center);
        }
        else if (S.Session.KeySet)
            Key(ctx, new Rect(x, y, 100, 20), "SEM TOM", () => S.ApplyKey(null), false, Ui.Amber, !S.Running);
        if (S.ResultTake is { Beats.Count: > 0 } || S.Review.TempoFromDrums)
            Key(ctx, new Rect(x, y + 34, 100, 20), S.Review.TempoFromDrums ? "TEMPO BATERIA" : "TEMPO CLICK", S.ToggleTempoSource, S.Review.TempoFromDrums, Ui.Amber);
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
        Ui.Text(ctx, $"CPU {Cpu():0} %", r.Right, y + 40, 10, Ui.Label, Ui.Mono, Ui.Align.Right);

        // Second row, as a DAW's toolbar: the views on the left, the processes on the right, in the
        // order they are used (mode, separate, analyse; then the score and the delivery).
        double ty = r.Y + 66, tx = r.X;
        foreach (var (label, on, act) in new (string, bool, Action)[]
        {
            ("BROWSER", S.ShowBrowser, () => S.ShowBrowser = !S.ShowBrowser),
            ("ROTEAMENTO", S.ShowRouting, () => S.ShowRouting = !S.ShowRouting),
            ("MEDIÇÃO", S.ShowMeters, () => S.ShowMeters = !S.ShowMeters),
        })
        {
            double w = label.Length * 7.5 + 22;
            Key(ctx, new Rect(tx, ty, w, 26), label, () => { act(); S.Select(); }, on, Ui.Amber);
            tx += w + 6;
        }
        tx += 18;
        string mode = S.Choir ? "CORO" : S.Mode switch
        {
            AppMode.VoiceMono => "VOZ", AppMode.InstrumentMono => "MELODIA", AppMode.GuitarChords => "VIOLÃO", AppMode.PianoChords => "PIANO", _ => "GERAL",
        };
        string sepLabel = !DemucsModel.HelperPresent ? "SEM DEMUCS" : S.Stems.Count > 0 ? "SEPARADO" : DemucsModel.Installed(S.Variant) ? "SEPARAR" : "SEPARAR*";
        bool idle = S.Current is not null && !S.Running && !S.Separating;
        foreach (var (label, lit, led, en, act) in new (string, bool, IBrush, bool, Action)[]
        {
            (mode, true, Ui.Amber, !S.Running, S.CycleMode),
            (sepLabel, S.Stems.Count > 0 || S.Separating, Ui.Amber, DemucsModel.HelperPresent && idle, S.Separate),
            ("ANALISAR", S.Running, Ui.Green, idle, S.Analyze),
            ("CANCELAR", false, Ui.Red, S.Running || S.Separating, S.Cancel),
            ("SCORE", S.Result is not null, Ui.Green, S.Current is not null, S.OpenScore),
            ("ENTREGAR", false, Ui.Amber, S.Result is not null && !S.Running, () => S.Deliver()),
        })
        {
            double w = Math.Max(64, label.Length * 7.5 + 22);
            Key(ctx, new Rect(tx, ty, w, 26), label, act, lit, led, en);
            tx += w + 6;
        }
        // Progress as an LED bar (separation amber, analysis green).
        bool sep = S.Separating;
        int litN = (int)Math.Round((sep ? S.SepProgress : S.Running ? S.Progress : S.Result is not null ? 1 : 0) * 16);
        for (int i = 0; i < 16; i++) Ui.Led(ctx, new Point(tx + 10 + i * 11, ty + 13), 3.6, i < litN, sep ? Ui.Amber : Ui.Green, sep ? Ui.AmberOff : Ui.GreenOff);
        if (sepLabel == "SEPARAR*") Ui.Text(ctx, $"* baixa o Demucs ({S.Variant.Size / 1_000_000} MB) uma vez", tx + 4, ty + 26, 7, Ui.Label, Ui.Mono);
    }

    // Process CPU over the last second, across all cores (100 % = one core busy).
    static readonly System.Diagnostics.Process Proc = System.Diagnostics.Process.GetCurrentProcess();
    static TimeSpan _cpuBusy;
    static long _cpuAt;
    static double _cpu;
    static double Cpu()
    {
        long now = Environment.TickCount64;
        if (now - _cpuAt < 1000) return _cpu;
        Proc.Refresh();
        var busy = Proc.TotalProcessorTime;
        if (_cpuAt > 0) _cpu = (busy - _cpuBusy).TotalMilliseconds / (now - _cpuAt) * 100;
        (_cpuBusy, _cpuAt) = (busy, now);
        return _cpu;
    }
}

/// DS-L: library on a phosphor screen.
public sealed class LibraryUnit : StudioUnit
{
    int _scroll;
    readonly List<(Rect R, StudioLibrary.Item It)> _rows = [];
    public LibraryUnit(StudioState s) : base(s, "DS-L", "BIBLIOTECA") { Width = 230; }

    protected override void DrawContent(DrawingContext ctx, Rect r)
    {
        var screen = new Rect(r.X, r.Y + 4, r.Width, r.Height - 90);
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF0B0C0D)), null, screen.Inflate(3), 4, 4);
        ctx.DrawRectangle(PhosphorBg, null, screen, 2, 2);
        _rows.Clear();
        int nPlugins = S.Plugins.Count();
        Key(ctx, new Rect(r.X, r.Bottom - 76, r.Width, 30), S.PluginView ? "◀ TAKES" : $"EFEITOS VST3 · {nPlugins}", () => { S.PluginView = !S.PluginView; _scroll = 0; InvalidateVisual(); }, S.PluginView, Ui.Amber, PluginHost.Present || nPlugins > 0);
        Key(ctx, new Rect(r.X, r.Bottom - 38, r.Width / 2 - 4, 32), S.PluginView ? "+ PASTA" : "IMPORTAR", () => _ = S.PluginView ? AddPluginFolderAsync() : ImportAsync(), false, null, !S.PluginView || PluginHost.Present);
        Key(ctx, new Rect(r.X + r.Width / 2 + 4, r.Bottom - 38, r.Width / 2 - 4, 32), "ATUALIZAR", () => { if (S.PluginView) S.ScanPlugins(); else S.ReloadLibrary(); InvalidateVisual(); });
        if (S.PluginView) { DrawPlugins(ctx, screen); return; }
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
    }

    readonly List<(Rect R, PluginHost.Plugin P)> _pluginRows = [];

    /// The installed VST3 effects, by name; a bundle the scan could not read shows its error. A
    /// click inserts the effect on the selected channel (DS-C's DV-0).
    void DrawPlugins(DrawingContext ctx, Rect screen)
    {
        double y = screen.Y + 6;
        Ui.Text(ctx, "▸ EFEITOS VST3", screen.X + 8, y, 9, PhosphorDim, Ui.Mono);
        y += 16;
        if (!PluginHost.Present) { Ui.Text(ctx, "host VST3 ausente nesta instalação", screen.X + 14, y, 9, PhosphorDim, Ui.Mono); return; }
        var items = S.Plugins.Select(p => (Name: p.Name, Detail: p.Vendor, Error: false))
            .Concat(S.PluginScan.Where(e => e.Error is not null).Select(e => (Name: Path.GetFileNameWithoutExtension(e.Bundle), Detail: e.Error!, Error: true))).Skip(_scroll);
        if (S.ScanningPlugins) { Ui.Text(ctx, "procurando…", screen.X + 14, y, 10, Phosphor, Ui.Mono); y += 18; }
        else if (!S.PluginScan.Any()) Ui.Text(ctx, "nenhum plugin: ATUALIZAR procura", screen.X + 14, y, 9, PhosphorDim, Ui.Mono);
        _pluginRows.Clear();
        foreach (var (name, detail, error) in items)
        {
            if (y > screen.Bottom - 30) break;
            if (!error && S.Plugins.FirstOrDefault(pl => pl.Name == name && pl.Vendor == detail) is { } plugin) _pluginRows.Add((new Rect(screen.X + 2, y - 2, screen.Width - 4, 28), plugin));
            Ui.Text(ctx, name.Length > 26 ? name[..26] : name, screen.X + 14, y, 10, error ? Ui.Amber : Phosphor, Ui.Mono);
            Ui.Text(ctx, detail.Length > 34 ? detail[..34] : detail, screen.X + 14, y + 13, 8, PhosphorDim, Ui.Mono);
            y += 30;
        }
    }

    protected override void Pressed(PointerPressedEventArgs e, Point p)
    {
        if (S.PluginView)
        {
            foreach (var (row, pl) in _pluginRows)
                if (row.Contains(p) && S.Current is not null) { S.AddVst(pl); InvalidateVisual(); return; }
            return;
        }
        foreach (var (row, it) in _rows)
            if (row.Contains(p)) { S.Open(it.Path); InvalidateVisual(); return; }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        _scroll = Math.Clamp(_scroll - Math.Sign(e.Delta.Y), 0, Math.Max(0, S.Library.Count - 1));
        InvalidateVisual();
        e.Handled = true;
    }

    /// Your own plugins (a build folder, a .vst3 anywhere): the folder joins the search, then a scan.
    async Task AddPluginFolderAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } sp) return;
        var dirs = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Pasta com seus plugins VST3 (ou a própria .vst3)" });
        foreach (var d in dirs)
            if (d.TryGetLocalPath() is { } path) PluginHost.AddFolder(path);
        if (dirs.Count > 0) S.ScanPlugins();
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

    static readonly IBrush[] ChoirInk = [Plasma.Lit, new ImmutableSolidColorBrush(Color.FromUInt32(0xFFFFD27A)), new ImmutableSolidColorBrush(Color.FromUInt32(0xFF5AA9FF)), new ImmutableSolidColorBrush(Color.FromUInt32(0xFF9DFFC8))];

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

        // Arrangement, as Reaper or Live: a header per track (name, number, mute, solo, FX and its
        // power, level) beside its lane on the time axis; the audio tracks are short, the lanes of the
        // analysis under them as a DAW's chord and MIDI tracks: the harmony (triads with their
        // degrees), the chords in detail, the notes. What is left below stays empty.
        var area = new Rect(r.X, r.Y + 38, r.Width, r.Height - 38);
        var card = new Rect(area.X, area.Y, 210, area.Height);
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF1C1E22)), new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF0B0C0D)), 1), card, 3, 3);
        Ui.Text(ctx, "FAIXAS", card.X + 8, card.Y + 4, 8, Ui.Label, Ui.SansBold);
        bool noteLane = NoteLane;
        double laneH = harmonyH + chordH + (noteLane ? notesH : 0);
        _cards.Clear();
        var stemNames = new Dictionary<string, string> { ["vocals"] = "VOZ", ["bass"] = "BAIXO", ["other"] = "OUTROS", ["drums"] = "BATERIA", ["guitar"] = "VIOLÃO", ["piano"] = "PIANO" };
        double rowY = area.Y + 22;
        TrackHeader(ctx, new Rect(card.X + 3, rowY, card.Width - 6, trackH - 2), 0, S.Current is null ? "—" : "MIX");
        for (int i = 0; i < S.Stems.Count; i++)
            TrackHeader(ctx, new Rect(card.X + 3, rowY + trackH + i * stemH, card.Width - 6, stemH - 2), i + 1, stemNames.GetValueOrDefault(S.Stems[i].Name, S.Stems[i].Name.ToUpperInvariant()));
        double laneY = rowY + trackH + stemH * S.Stems.Count + 4;
        var laneInk = S.ResultTake is null ? Ui.Label : Ui.Amber;
        var degreesIn = S.Session.KeySet ? S.Session.Key : S.Suggestion?.Key;
        LaneHeader(ctx, new Rect(card.X + 3, laneY, card.Width - 6, harmonyH - 2), "HARMONIA", degreesIn is null ? "sem tom: sem graus" : $"graus em {degreesIn.Tonic}{(degreesIn.Minor ? "m" : "")}{(S.Session.KeySet ? "" : " (sugerido)")}", laneInk);
        LaneHeader(ctx, new Rect(card.X + 3, laneY + harmonyH, card.Width - 6, chordH - 2), "ACORDES", "detalhe", laneInk);
        if (noteLane) LaneHeader(ctx, new Rect(card.X + 3, laneY + harmonyH + chordH, card.Width - 6, notesH - 2), S.Choir ? "CORO SATB" : "NOTAS", "melodia", laneInk);

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
            // Stereo position, top right of the row (over the waveform, on its own backing).
            var pan = new Rect(row.Right - 104, row.Y + 3, 100, 22);
            ctx.DrawRectangle(PhosphorBg, null, pan, 2, 2);
            PanMeter(ctx, pan.X + 14, pan.Y + 7, 72, st);
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

        // Instruments (Basic Pitch): every note on its stem's row, pitch over the notes' own range.
        if (S.ResultTake is { } it)
            for (int i = 0; i < S.Stems.Count; i++)
            {
                if (!it.Instruments.TryGetValue(S.Stems[i].Name, out var notes) || notes.Count == 0) continue;
                var row = new Rect(_screen.X, _screen.Bottom + i * stemH, _screen.Width, stemH);
                int lo = notes.Min(n => n.Midi), hi = Math.Max(lo + 12, notes.Max(n => n.Midi));
                foreach (var n in notes)
                {
                    double a = XAt((n.Start + S.Compensation) * S.Rate), b = XAt((n.End + S.Compensation) * S.Rate);
                    if (b < row.X || a > row.Right) continue;
                    double ny = row.Bottom - 3 - (row.Height - 6) * (n.Midi - lo) / (double)(hi - lo);
                    ctx.DrawRectangle(Ui.Amber, null, new Rect(Math.Max(a, row.X), ny - 1, Math.Max(2, Math.Min(b, row.Right) - Math.Max(a, row.X) - 1), 2));
                }
            }

        // Transcription lanes: the offline result of these edits.
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0x55000000)), null, _lane);
        var harmonyLane = new Rect(_lane.X, _lane.Y, _lane.Width, harmonyH);
        var chordLane = new Rect(_lane.X, harmonyLane.Bottom, _lane.Width, chordH);
        var notesLane = new Rect(_lane.X, chordLane.Bottom, _lane.Width, noteLane ? notesH : 0);
        ctx.DrawLine(PhosphorLine, chordLane.TopLeft, chordLane.TopRight);
        if (noteLane) ctx.DrawLine(PhosphorLine, notesLane.TopLeft, notesLane.TopRight);
        if (S.ResultTake is { } take)
        {
            double off = S.Compensation;   // result times are compensated; the screen is file time
            // The harmony map: what a listener sums up (G ... D ... G), each with its degree in the key.
            foreach (var h in LeadSheet.Harmony(take.Chords, S.Session.KeySet ? S.Session.Key : S.Suggestion?.Key))
            {
                double a = XAt((h.Start + off) * S.Rate), b = XAt((h.End + off) * S.Rate);
                if (b < harmonyLane.X || a > harmonyLane.Right) continue;
                var box = new Rect(Math.Max(a, harmonyLane.X) + 1, harmonyLane.Y + 3, Math.Max(2, Math.Min(b, harmonyLane.Right) - Math.Max(a, harmonyLane.X) - 2), harmonyLane.Height - 6);
                bool now = p.Position >= (h.Start + off) * S.Rate && p.Position < (h.End + off) * S.Rate;
                ctx.DrawRectangle(now ? new ImmutableSolidColorBrush(Color.FromUInt32(0xFF1E2A22)) : new ImmutableSolidColorBrush(Color.FromUInt32(0xFF12181A)), Ui.LcdLineDim, box, 3, 3);
                if (box.Width > 18) Ui.Text(ctx, h.Symbol, box.X + 5, box.Y + 2, 14, now ? Plasma.Lit : Phosphor, Ui.Mono);
                if (box.Width > 18 && h.Roman.Length > 0) Ui.Text(ctx, h.Roman, box.X + 5, box.Y + 19, 10, PhosphorDim, Ui.Mono);
            }
            foreach (var ch in take.Chords)
            {
                double a = XAt((ch.Start + off) * S.Rate), b = XAt((ch.End + off) * S.Rate);
                if (b < chordLane.X || a > chordLane.Right) continue;
                var box = new Rect(Math.Max(a, chordLane.X) + 1, chordLane.Y + 3, Math.Max(2, Math.Min(b, chordLane.Right) - Math.Max(a, chordLane.X) - 2), chordLane.Height - 6);
                bool low = ch.Confidence < 0.5f;
                bool picked = S.Picked is ("chord", var ps) && Math.Abs(ps - ch.Start) < Review.MatchSeconds;
                if (picked) ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0x33FFB000)), null, box, 2, 2);
                ctx.DrawRectangle(null, new ImmutablePen((IImmutableBrush)(picked ? Ui.LabelBright : low ? Ui.Amber : Ui.Green), picked ? 2 : 1), box, 2, 2);
                _events.Add((box, "chord", ch.Start));
                int fit = (int)((box.Width - 6) / 7);   // mono 11 px
                if (fit >= 1) Ui.Text(ctx, ch.Symbol.Length > fit ? ch.Symbol[..fit] : ch.Symbol, box.X + 4, box.Y + 4, 11, low ? Ui.Amber : Plasma.Lit, Ui.Mono);
            }
            // The melody, or a choir's four sections (S9) in their colours, on one pitch axis.
            var lines = take.IsChoir
                ? new List<(string Kind, List<TakeNote> Notes, IBrush Brush)> { ("note:S", take.Notes, ChoirInk[0]) }
                    .Concat(new[] { "A", "T", "B" }.Select((p, i) => (Kind: $"note:{p}", Notes: take.Choir.GetValueOrDefault(p) ?? [], Brush: ChoirInk[i + 1]))).ToList()
                : new List<(string Kind, List<TakeNote> Notes, IBrush Brush)> { ("note", take.Notes, Plasma.Lit) };
            var all = lines.SelectMany(l => l.Notes).ToList();
            if (all.Count > 0)
            {
                int lo = all.Min(n => n.Midi), hi = Math.Max(lo + 12, all.Max(n => n.Midi));
                foreach (var (kind, notes, ink) in lines)
                    foreach (var n in notes)
                    {
                        double a = XAt((n.Start + off) * S.Rate), b = XAt((n.End + off) * S.Rate);
                        if (b < notesLane.X || a > notesLane.Right) continue;
                        double yy = notesLane.Bottom - 6 - (n.Midi - lo) / (double)(hi - lo) * (notesLane.Height - 12);
                        var nr = new Rect(Math.Max(a, notesLane.X), yy - 1.5, Math.Max(2, b - a - 1), take.IsChoir ? 3 : 4);
                        bool picked = S.Picked is (var pk, var ps) && pk == kind && Math.Abs(ps - n.Start) < Review.MatchSeconds;
                        ctx.DrawRectangle(picked ? Ui.LabelBright : ink, picked ? new ImmutablePen((IImmutableBrush)Ui.Amber, 1) : null, picked ? nr.Inflate(1.5) : nr, 1, 1);
                        _events.Insert(0, (nr.Inflate(new Thickness(1, 3)), kind, n.Start));   // notes before the chord boxes they sit in
                    }
                if (take.IsChoir)
                    for (int i = 0; i < 4; i++) Ui.Text(ctx, "SATB"[i].ToString(), notesLane.Right - 44 + i * 10, notesLane.Y + 2, 8, ChoirInk[i], Ui.Mono);
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

    const double harmonyH = 40, chordH = 32, notesH = 44, trackH = 76, stemH = 52;
    bool NoteLane => S.ResultTake is { } t && (t.Notes.Count > 0 || t.IsChoir);
    /// The height every track and lane needs (the unit is never shorter: the rack scrolls).
    public double Needed => 20 + 38 + 22 + trackH + stemH * S.Stems.Count + 4 + harmonyH + chordH + (NoteLane ? notesH : 0) + 24;

    static readonly IBrush HeaderFace = new ImmutableSolidColorBrush(Color.FromUInt32(0xFF2A2D32));

    /// A track's header: tape name and number, then mute, solo, FX (the inserts: opens the chain,
    /// or the effect browser when there is none) and the FX power (bypass every insert), and the
    /// level as a row of LEDs. A click elsewhere on it selects the track (the chain below shows it).
    void TrackHeader(DrawingContext ctx, Rect row, int track, string name)
    {
        ctx.DrawRectangle(HeaderFace, S.Selected == track && S.Current is not null ? new ImmutablePen((IImmutableBrush)Ui.Amber, 1.5) : Ui.FaceEdge, row, 3, 3);
        _cards.Add((row, track));
        Tape(ctx, row.X + 8, row.Y + 5, name, track % 2 == 0 ? -1.2 : 1.2);
        Plasma.DotText(ctx, $"{track + 1}", row.Right - 18, row.Y + 6, 2, 1);
        if (track == 0) Ui.Led(ctx, new Point(row.Right - 30, row.Y + 11), 3, !S.Edits.IsEmpty, Ui.Amber, Ui.AmberOff);
        if (S.Current is null) return;
        var c = S.Channel(track);
        double ky = row.Bottom - 24, kx = row.X + 8;
        Key(ctx, new Rect(kx, ky, 26, 19), "M", () => { c.Mute = !c.Mute; S.MixChanged(); }, c.Mute, Ui.Amber);
        Key(ctx, new Rect(kx + 30, ky, 26, 19), "S", () => { c.Solo = !c.Solo; S.MixChanged(); }, c.Solo, Ui.Green);
        bool anyOn = c.Vst.Any(v => v.On);
        Key(ctx, new Rect(kx + 60, ky, 46, 19), c.Vst.Count > 0 ? $"FX {c.Vst.Count}" : "FX", () =>
        {
            S.Selected = track;
            if (c.Vst.Count == 0) (S.PluginView, S.ShowBrowser) = (true, true);
            S.Select();
        }, anyOn, Ui.Green, PluginHost.Present || c.Vst.Count > 0);
        Key(ctx, new Rect(kx + 110, ky, 26, 19), "⏻", () => S.SetVstOn(track, !anyOn), anyOn, Ui.Red, c.Vst.Count > 0);
        float pk = S.Meters.Peak[Math.Min(track, 7)];
        for (int i = 0; i < 10; i++)
            Ui.Led(ctx, new Point(row.Right - 10, row.Bottom - 6 - i * 4.2), 1.6, pk > -54 + i * 5.4, i >= 9 ? Ui.Red : i >= 7 ? Ui.Amber : Ui.Green, Ui.GreenOff);
    }

    void LaneHeader(DrawingContext ctx, Rect row, string name, string detail, IBrush ink)
    {
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF232529)), Ui.FaceEdge, row, 3, 3);
        Ui.Text(ctx, "↳ " + name, row.X + 8, row.Y + 3, 10, ink, Ui.SansBold);
        if (row.Height > 30) Ui.Text(ctx, detail, row.X + 20, row.Y + 18, 8, Ui.Label, Ui.Sans);
        else Ui.Text(ctx, detail, row.Right - 8, row.Y + 5, 8, Ui.Label, Ui.Sans, Ui.Align.Right);
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
    public ChainUnit(StudioState s) : base(s, "DS-C", "CADEIA DO CANAL") { Height = 290; }

    readonly List<(Rect R, VstInsert V)> _vstRows = [], _vstOpen = [];

    protected override void Pressed(PointerPressedEventArgs e, Point p)
    {
        foreach (var (r, v) in _vstRows)
            if (r.Contains(p) && !S.RenderingVst) { S.ToggleVst(v); return; }
        foreach (var (r, v) in _vstOpen)
            if (r.Contains(p)) { S.OpenPanel(S.OpenVst == v ? null : v); return; }
        // A module's top (its engraved name): pick it up to patch it elsewhere.
        foreach (var (slot, box) in _modules)
            if (new Rect(box.X, box.Y, box.Width, 20).Contains(p) && S.Current is not null)
            {
                (_dragSlot, _dragX) = (slot, p.X);
                e.Pointer.Capture(this);
                InvalidateVisual();
                return;
            }
    }

    /// The open insert's parameters as knobs, eight a page, where the analyser sits.
    void DrawParams(DrawingContext ctx, Rect an, VstInsert v)
    {
        DrawDevice(ctx, an, "DV-0", v.Name.Length > 22 ? v.Name[..22] : v.Name, v.On);
        var ps = S.OpenParams;
        int pages = Math.Max(1, (ps.Count + 7) / 8);
        S.ParamPage = Math.Clamp(S.ParamPage, 0, pages - 1);
        Key(ctx, new Rect(an.X + 12, an.Y + 24, 28, 22), "◀", () => S.ParamPage--, false, null, S.ParamPage > 0, withLed: false);
        Key(ctx, new Rect(an.X + 44, an.Y + 24, 28, 22), "▶", () => S.ParamPage++, false, null, S.ParamPage < pages - 1, withLed: false);
        Ui.Text(ctx, ps.Count == 0 ? "lendo…" : pages > 1 ? $"{S.ParamPage + 1}/{pages}" : "", an.X + 80, an.Y + 29, 8, Ui.Label, Ui.Mono);
        Key(ctx, new Rect(an.Right - 146, an.Y + 24, 66, 22), "EDITOR", () => S.OpenEditor(v), S.EditorOpen, Ui.Amber, !S.EditorOpen && !S.RenderingVst);
        Key(ctx, new Rect(an.Right - 76, an.Y + 24, 64, 22), "FECHAR", () => S.OpenPanel(null), false, null, !S.EditorOpen, withLed: false);
        double cw = (an.Width - 24) / 4;
        foreach (var (p, i) in ps.Skip(S.ParamPage * 8).Take(8).Select((p, i) => (p, i)))
        {
            var c = new Point(an.X + 12 + cw * (i % 4 + 0.5), an.Y + 78 + (i / 4) * 66);
            double value = v.Params.TryGetValue(p.Id, out var set) ? set : p.Value;
            string text = p.Text.Length > 0 ? p.Text : p.Steps > 0 ? $"{Math.Round(value * p.Steps):0}" : $"{value * 100:0} %";
            string title = p.Title.Length > 10 ? p.Title[..10] : p.Title;
            var param = p;
            Knob(ctx, c, 13, title.ToUpperInvariant(), (text + (p.Units.Length > 0 && p.Text.Length > 0 ? " " + p.Units : "")) is var t && t.Length > 12 ? t[..12] : t, value,
                n => S.SetParam(v, param, n), v.On);
        }
        if (S.RenderingVst) Ui.Text(ctx, $"renderizando {S.VstProgress * 100:0} %", an.X + 14, an.Bottom - 16, 8, Ui.Amber, Ui.Mono);
    }

    static string Hz(double f) => f >= 1000 ? $"{f / 1000:0.#}k" : $"{f:0}";
    static readonly IBrush BypassVeil = new ImmutableSolidColorBrush(Color.FromUInt32(0x77111214));

    // Modules on screen this frame (slot id, box) and a module being dragged by its top.
    readonly List<(string Slot, Rect Box)> _modules = [];
    string? _dragSlot;
    double _dragX;
    static readonly Dictionary<string, (string Model, string Name, double W)> UnitFace = new()
    {
        ["trim"] = ("DS-1", "TRIM", 100), ["filter"] = ("DF-2", "FILTRO", 120), ["gate"] = ("DG-3", "GATE", 120), ["eq"] = ("DQ-4", "EQ", 184), ["comp"] = ("DC-5", "COMP", 156),
    };

    protected override void DrawContent(DrawingContext ctx, Rect r)
    {
        var names = new Dictionary<string, string> { ["mix"] = "MIX", ["vocals"] = "VOZ", ["bass"] = "BAIXO", ["other"] = "OUTROS", ["drums"] = "BATERIA", ["guitar"] = "VIOLÃO", ["piano"] = "PIANO" };
        int sel = S.Selected, mt = Math.Min(sel, 7);
        string chName = S.Current is null ? "—" : names.GetValueOrDefault(S.NameOf(sel), S.NameOf(sel));
        Tape(ctx, r.X, r.Y, chName, -1);
        var an0 = S.AnalysersOf(sel);
        var c = S.Channel(sel);
        bool has = S.Current is not null;
        var slots = c.Slots();
        // The drawer: units with their cables pulled; a click plugs one back in before the tap.
        double dx = r.X + 90;
        Ui.Text(ctx, "arraste um módulo pelo topo: muda a ordem  ·  ⏏ tira da cadeia  ·  ∥ caminho paralelo", dx, r.Y + 2, 9, Ui.Label, Ui.Mono);
        dx += 560;
        foreach (var u in ChannelMix.Units.Where(u => !slots.Contains(u)))
        {
            string label = "+ " + UnitFace[u].Name;
            double w = label.Length * 7 + 18;
            Key(ctx, new Rect(dx, r.Y - 2, w, 20), label, () => S.MoveSlot(sel, u, c.Slots().Count), false, Ui.Amber, has, withLed: false);
            dx += w + 4;
        }
        Key(ctx, new Rect(dx, r.Y - 2, 70, 20), "+ VST3", () => { S.PluginView = true; S.ShowBrowser = true; S.Select(); }, S.PluginView, Ui.Amber, has && PluginHost.Present && c.Vst.Count < StudioState.MaxVst, withLed: false);
        double y = r.Y + 30, h = r.Height - 30 - 30;   // room under the modules for the parallel path's dry cable
        void Changed() => S.MixChanged();
        double ky = y + h / 2 - 6, fy = y + h - 22;   // knob row, footswitch row
        // Cable levels: the units after the last insert that is on play live (the player's stage
        // peaks by position); before it everything is rendered, and its level is the source's.
        var live = c.Segments()[^1];
        float Lv(string? slot) => slot is null || !live.Contains(slot) ? S.Meters.Stage[mt, 0] : S.Meters.Stage[mt, live.IndexOf(slot) + 1];

        // IN plate.
        var inPlate = new Rect(r.X, y + h / 2 - 22, 30, 44);
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF1A1C1F)), Ui.FaceEdge, inPlate, 3, 3);
        Ui.Text(ctx, "IN", inPlate.Center.X, inPlate.Y + 3, 8, Ui.LabelBright, Ui.SansBold, Ui.Align.Center);
        Jack(ctx, new Point(inPlate.Center.X, inPlate.Center.Y + 6));
        double x = inPlate.Right + 20;
        Rect box = new(inPlate.Center.X, inPlate.Center.Y + 6, 0, 0);
        Point outPt = new(inPlate.Center.X, inPlate.Center.Y + 6);
        string? prev = null;
        _modules.Clear();
        _vstRows.Clear();
        _vstOpen.Clear();
        void Veil(bool on) { if (!on && has) { ctx.DrawRectangle(BypassVeil, null, box, 4, 4); Ui.Text(ctx, "BYPASS", box.Center.X + 22, fy - 6, 8, Ui.Amber, Ui.SansBold); } }

        foreach (var slot in slots)
        {
            var v = c.VstOf(slot);
            double w = v is not null ? 120 : UnitFace[slot].W;
            box = new Rect(x, y, w, h);
            Patch(ctx, outPt, new Point(box.X, box.Center.Y), Lv(prev), 14);
            if (v is not null) DrawInsert(ctx, box, sel, v, fy);
            else DrawUnit(ctx, box, slot, c, mt, ky, fy, has, Changed, Veil, Lv(slot));
            // The module's own keys on its top: the parallel path and pulling its cables.
            if (v is null && has)
            {
                bool inPar = InParallel(c, slots, slot);
                Key(ctx, new Rect(box.Right - 50, box.Y + 4, 18, 15), "∥", () => S.ToggleParallel(sel, slot), inPar, Ui.Amber, true, withLed: false);
                Key(ctx, new Rect(box.Right - 30, box.Y + 4, 18, 15), "⏏", () => S.Unpatch(sel, slot), false, null, true, withLed: false);
            }
            _modules.Add((slot, box));
            outPt = new Point(box.Right, box.Center.Y);
            prev = slot;
            x = box.Right + 20;
        }
        if (slots.Count == 0) Ui.Text(ctx, "cadeia vazia: o sinal passa direto", x, y + h / 2 - 6, 9, Ui.Label, Ui.Mono);

        // The parallel path: the dry signal on its own cable under the units it goes round, back in
        // at the end of the run with the processed one at the mix knob.
        if (c.ParallelFirst is not null && _modules.FirstOrDefault(m => m.Slot == c.ParallelFirst) is { Slot: not null } pa
            && _modules.FirstOrDefault(m => m.Slot == c.ParallelLast) is { Slot: not null } pb)
        {
            double py = y + h + 14;
            var a = new Point(pa.Box.X - 8, pa.Box.Center.Y);
            var b = new Point(pb.Box.Right + 6, pb.Box.Center.Y);
            ctx.DrawEllipse(Ui.Amber, null, a, 3.5, 3.5);
            ctx.DrawEllipse(Ui.Amber, null, b, 3.5, 3.5);
            Patch(ctx, a, new Point(a.X, py), Lv(slots.ElementAtOrDefault(slots.IndexOf(pa.Slot) - 1)), 0, led: false);
            Patch(ctx, new Point(a.X, py), new Point(b.X, py), Lv(slots.ElementAtOrDefault(slots.IndexOf(pa.Slot) - 1)), 0, thin: true);
            Patch(ctx, new Point(b.X, py), b, Lv(pb.Slot), 0, led: false);
            double kx = (a.X + b.X) / 2;
            Knob(ctx, new Point(kx, py - 2), 8, "", "", c.ParallelMix, n => { c.ParallelMix = (float)Math.Round(n, 2); Changed(); }, true);
            Ui.Text(ctx, $"∥ PARALELO · seco + {c.ParallelMix * 100:0} % processado", kx + 14, py - 16, 8, Ui.Amber, Ui.SansBold);
        }
        // A module being dragged: where it would go.
        if (_dragSlot is not null)
        {
            int at = DropIndex(_dragX);
            double gx = at < _modules.Count ? _modules[at].Box.X - 10 : (_modules.Count > 0 ? _modules[^1].Box.Right + 10 : inPlate.Right + 10);
            ctx.DrawLine(new ImmutablePen((IImmutableBrush)Ui.Amber, 3), new Point(gx, y - 4), new Point(gx, y + h + 4));
        }

        // The tap: the signal splits — OUT to the channel's fader, a send to the analyser.
        var tap = new Point(outPt.X + 14, outPt.Y);
        Patch(ctx, outPt, tap, Lv(prev), 0, led: false);
        ctx.DrawEllipse(Ui.Amber, null, tap, 4, 4);
        Ui.Text(ctx, "TAP", tap.X, tap.Y + 8, 7, Ui.Amber, Ui.SansBold, Ui.Align.Center);
        Patch(ctx, tap, new Point(tap.X, y - 8), Lv(prev), 0, led: false);
        Patch(ctx, new Point(tap.X, y - 8), new Point(r.Right - 96, y - 8), Lv(prev), 0, thin: true);
        Ui.Text(ctx, $"OUT ▶ FADER {sel + 1} · DS-M", r.Right, y - 14, 8, Ui.LabelBright, Ui.SansBold, Ui.Align.Right);
        x = tap.X + 14;
        if (an0.Length > 0) Patch(ctx, tap, new Point(x, tap.Y), Lv(prev), 0, send: true);
        x = Math.Min(x, r.Right - 200);
        var an = new Rect(x, y, r.Right - x, h);
        if (S.OpenVst is { } ov && c.Vst.Contains(ov)) { DrawParams(ctx, an, ov); return; }
        DrawDevice(ctx, an, an0.Length > 0 ? string.Join(" ", an0) : "DA", an0.Length > 0 ? "ANALISADOR · ENVIO PRÉ-FADER" : "ANALISADOR · canal sem análise", an0.Length > 0);
        // The processes are on the transport's toolbar; here, what the analysis made of this take.
        double ix = an.X + 14, iy = an.Y + 24;
        var win = new Rect(ix, iy, Math.Min(an.Width - 28, 520), Math.Min(64, an.Bottom - iy - 8));
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF120804)), Ui.FaceEdge, win, 2, 2);
        if (S.ResultTake is { } t)
        {
            bool notes = t.Notes.Count > 0;
            string main = t.IsChoir ? $"{t.Notes.Count + t.Choir.Values.Sum(l => l.Count)}" : notes ? $"{t.Notes.Count}" : $"{t.Chords.Count}";
            Plasma.DotText(ctx, main, win.X + 8, win.Y + 6, 3, 3);
            Plasma.Text(ctx, t.IsChoir ? "NOTAS SATB" : notes ? "NOTAS" : "ACORDES", win.X + 84, win.Y + 6, 13);
            string how = t.IsChoir ? $"S {t.Notes.Count} A {t.Choir.GetValueOrDefault("A")?.Count ?? 0} T {t.Choir.GetValueOrDefault("T")?.Count ?? 0} B {t.Choir.GetValueOrDefault("B")?.Count ?? 0} · experimental"
                : S.Stems.Count > 0 ? $"{t.Notes.Count} notas da voz · {t.Chords.Count} acordes · faixas separadas"
                : notes ? "voz: take inteiro" : "take inteiro · Viterbi" + (S.FromTake && S.Edits.KeepsGrid ? " · grade do metrônomo" : "");
            int fit = (int)((win.Width - 92) / 6.2);   // mono 9 px
            Plasma.Text(ctx, how.Length > fit ? how[..fit] : how, win.X + 84, win.Y + 24, 9, false);
        }
        else Plasma.Text(ctx, S.Separating ? $"SEPARANDO {S.SepProgress * 100:0} %" : S.Running ? $"{S.Progress * 100:0} %" : "—", win.X + 12, win.Y + 12, 16, S.Running || S.Separating);
    }

    static bool InParallel(ChannelMix c, List<string> slots, string slot)
    {
        int a = c.ParallelFirst is null ? -1 : slots.IndexOf(c.ParallelFirst), b = c.ParallelLast is null ? -1 : slots.IndexOf(c.ParallelLast), i = slots.IndexOf(slot);
        return a >= 0 && i >= a && i <= b;
    }

    /// Where a dragged module would land: before the first module whose centre is right of x.
    int DropIndex(double x)
    {
        for (int i = 0; i < _modules.Count; i++) if (x < _modules[i].Box.Center.X) return i;
        return _modules.Count;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_dragSlot is null) { base.OnPointerMoved(e); return; }
        _dragX = e.GetPosition(this).X;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_dragSlot is null) { base.OnPointerReleased(e); return; }
        int at = DropIndex(_dragX);
        string slot = _dragSlot;
        _dragSlot = null;
        e.Pointer.Capture(null);
        if (_modules.FindIndex(m => m.Slot == slot) is var from && at != from && at != from + 1) S.MoveSlot(S.Selected, slot, at);
        InvalidateVisual();
    }

    /// A VST3 insert as its own module: power, name (opens its parameters), remove.
    void DrawInsert(DrawingContext ctx, Rect box, int sel, VstInsert v, double fy)
    {
        DrawDevice(ctx, box, "DV", "VST3", v.On);
        var name = new Rect(box.X + 8, box.Y + 26, box.Width - 16, 40);
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF120804)), Ui.FaceEdge, name, 2, 2);
        Plasma.Text(ctx, v.Name.Length > 12 ? v.Name[..12] : v.Name, name.X + 6, name.Y + 4, 10, v.On);
        Ui.Text(ctx, v.Vendor.Length > 16 ? v.Vendor[..16] : v.Vendor, name.X + 6, name.Y + 22, 7, Ui.Label, Ui.Mono);
        if (S.OpenVst == v) ctx.DrawRectangle(null, new ImmutablePen((IImmutableBrush)Ui.Amber, 1.2), name, 2, 2);
        _vstOpen.Add((name, v));
        Key(ctx, new Rect(box.X + 8, box.Y + 74, box.Width - 16, 22), "PARÂMETROS", () => S.OpenPanel(S.OpenVst == v ? null : v), S.OpenVst == v, Ui.Amber, true);
        Key(ctx, new Rect(box.Right - 26, box.Y + 3, 18, 14), "✕", () => S.RemoveVst(sel, v), false, Ui.Red, !S.RenderingVst, withLed: false);
        if (S.RenderingVst)
        {
            int vlit = (int)Math.Round(S.VstProgress * 10);
            for (int i = 0; i < 10; i++) Ui.Led(ctx, new Point(box.X + 14 + i * 9.5, fy - 26), 2.4, i < vlit, Ui.Amber, Ui.AmberOff);
            Ui.Text(ctx, "RENDERIZANDO", box.Center.X, fy - 18, 7, Ui.Amber, Ui.SansBold, Ui.Align.Center);
        }
        if (!v.On) ctx.DrawRectangle(BypassVeil, null, box, 4, 4);
        Footswitch(ctx, new Point(box.Center.X, fy), v.On, () => { if (!S.RenderingVst) S.ToggleVst(v); }, true);
    }

    /// One built-in unit at `box`, its controls as on the console (S4).
    void DrawUnit(DrawingContext ctx, Rect box, string unit, ChannelMix c, int mt, double ky, double fy, bool has, Action Changed, Action<bool> Veil, float level)
    {
        double y = box.Y;
        switch (unit)
        {
            case "trim":
                DrawDevice(ctx, box, "DS-1", "TRIM", has);
                Knob(ctx, new Point(box.Center.X, ky), 18, "GANHO", $"{c.Trim:+0.0;-0.0} dB", LinNorm(c.Trim, -24, 24), n => { c.Trim = (float)Math.Round(FromLin(n, -24, 24), 1); Changed(); });
                for (int i = 0; i < 10; i++) Ui.Led(ctx, new Point(box.X + 10 + i * 6.4, fy + 4), 2.6, level > -50 + i * 5.5, i > 7 ? Ui.Red : i > 5 ? Ui.Amber : Ui.Green, Ui.GreenOff);
                Ui.Text(ctx, "NÍVEL", box.Center.X, fy + 10, 7, Ui.Label, Ui.SansBold, Ui.Align.Center);
                break;
            case "filter":
                DrawDevice(ctx, box, "DF-2", "FILTRO", c.HpOn || c.LpOn);
                Key(ctx, new Rect(box.X + 8, box.Y + 24, 34, 20), "HP", () => { c.HpOn = !c.HpOn; Changed(); }, c.HpOn, Ui.Green, has);
                Key(ctx, new Rect(box.X + 45, box.Y + 24, 34, 20), "LP", () => { c.LpOn = !c.LpOn; Changed(); }, c.LpOn, Ui.Green, has);
                Key(ctx, new Rect(box.X + 82, box.Y + 24, 32, 20), c.Steep ? "24" : "12", () => { c.Steep = !c.Steep; Changed(); }, c.Steep, Ui.Amber, has);
                Knob(ctx, new Point(box.X + 32, ky + 10), 16, "HP", Hz(c.Hp), LogNorm(c.Hp, 20, 1000), n => { c.Hp = (float)Math.Round(FromLog(n, 20, 1000)); Changed(); }, c.HpOn);
                Knob(ctx, new Point(box.X + 88, ky + 10), 16, "LP", Hz(c.Lp), LogNorm(c.Lp, 1000, 20000), n => { c.Lp = (float)Math.Round(FromLog(n, 1000, 20000), -1); Changed(); }, c.LpOn);
                if (!(c.HpOn || c.LpOn) && has) Ui.Text(ctx, "BYPASS", box.Center.X, fy, 8, Ui.Amber, Ui.SansBold, Ui.Align.Center);
                break;
            case "gate":
                DrawDevice(ctx, box, "DG-3", "GATE", c.GateOn);
                Ui.Led(ctx, new Point(box.X + 18, box.Y + 32), 3.5, c.GateOn && S.Meters.GateOpen[mt], Ui.Green, Ui.GreenOff);
                Ui.Text(ctx, "ABERTO", box.X + 26, box.Y + 26, 7, Ui.Label, Ui.SansBold);
                Knob(ctx, new Point(box.X + 24, ky + 4), 14, "LIMIAR", $"{c.GateThreshold:0}", LinNorm(c.GateThreshold, -80, 0), n => { c.GateThreshold = (float)Math.Round(FromLin(n, -80, 0)); Changed(); }, c.GateOn);
                Knob(ctx, new Point(box.X + 60, ky + 4), 14, "SOLTURA", $"{c.GateRelease:0} ms", LogNorm(c.GateRelease, 5, 1000), n => { c.GateRelease = (float)Math.Round(FromLog(n, 5, 1000)); Changed(); }, c.GateOn);
                Knob(ctx, new Point(box.X + 96, ky + 4), 14, "FAIXA", $"{c.GateRange:0} dB", LinNorm(c.GateRange, 0, 80), n => { c.GateRange = (float)Math.Round(FromLin(n, 0, 80)); Changed(); }, c.GateOn);
                Veil(c.GateOn);
                Footswitch(ctx, new Point(box.Center.X, fy), c.GateOn, () => { c.GateOn = !c.GateOn; Changed(); }, has);
                break;
            case "eq":
                DrawDevice(ctx, box, "DQ-4", "EQ", c.EqOn);
                string[] bandNames = ["GRAVE", "MÉDIO-G", "MÉDIO-A", "AGUDO"];
                for (int b = 0; b < Math.Min(4, c.Bands.Length); b++)
                {
                    var band = c.Bands[b];
                    double bx = box.X + 24 + b * 45;
                    Knob(ctx, new Point(bx, y + 44), 12, bandNames[b], Hz(band.Freq), LogNorm(band.Freq, 20, 20000), n => { band.Freq = (float)Math.Round(FromLog(n, 20, 20000)); Changed(); }, c.EqOn);
                    Knob(ctx, new Point(bx, y + 100), 12, "GANHO", $"{band.Gain:+0;-0}", LinNorm(band.Gain, -15, 15), n => { band.Gain = (float)Math.Round(FromLin(n, -15, 15), 1); Changed(); }, c.EqOn);
                }
                Veil(c.EqOn);
                Footswitch(ctx, new Point(box.Center.X, fy), c.EqOn, () => { c.EqOn = !c.EqOn; Changed(); }, has);
                break;
            case "comp":
                DrawDevice(ctx, box, "DC-5", "COMP", c.CompOn);
                float gr = S.Meters.Gr[mt];
                for (int i = 0; i < 8; i++) Ui.Led(ctx, new Point(box.X + 16 + i * 10, box.Y + 32), 3, c.CompOn && gr > 1 + i * 1.5, Ui.Amber, Ui.AmberOff);
                Ui.Text(ctx, "REDUÇÃO", box.X + 100, box.Y + 26, 7, Ui.Label, Ui.SansBold);
                Knob(ctx, new Point(box.X + 24, ky + 4), 13, "LIMIAR", $"{c.CompThreshold:0}", LinNorm(c.CompThreshold, -50, 0), n => { c.CompThreshold = (float)Math.Round(FromLin(n, -50, 0)); Changed(); }, c.CompOn);
                Knob(ctx, new Point(box.X + 59, ky + 4), 13, "RAZÃO", $"{c.CompRatio:0.#}:1", LogNorm(c.CompRatio, 1, 20), n => { c.CompRatio = (float)Math.Round(FromLog(n, 1, 20), 1); Changed(); }, c.CompOn);
                Knob(ctx, new Point(box.X + 94, ky + 4), 13, "ATAQUE", $"{c.CompAttack:0.#}", LogNorm(c.CompAttack, 0.1, 100), n => { c.CompAttack = (float)Math.Round(FromLog(n, 0.1, 100), 1); Changed(); }, c.CompOn);
                Knob(ctx, new Point(box.X + 129, ky + 4), 13, "GANHO", $"{c.CompMakeup:+0}", LinNorm(c.CompMakeup, 0, 24), n => { c.CompMakeup = (float)Math.Round(FromLin(n, 0, 24)); Changed(); }, c.CompOn);
                Veil(c.CompOn);
                Footswitch(ctx, new Point(box.Center.X, fy), c.CompOn, () => { c.CompOn = !c.CompOn; Changed(); }, has);
                break;
        }
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
        // ponytail: strips narrow down to 84 px; past six stems the master overlaps them — scroll the strips if that matters.
        double w = Math.Clamp((r.Width - 308) / S.Tracks - 8, 84, 118), x = r.X, busY = r.Bottom - 8, stH = r.Height - 22;
        float master = Math.Max(S.Meters.MasterPeak[0], S.Meters.MasterPeak[1]);
        bool anySolo = Enumerable.Range(0, S.Tracks).Any(k => S.Channel(k).Solo);
        for (int t = 0; t < S.Tracks; t++, x += w + 8)
        {
            var c = S.Channel(t);
            int track = t, mt = Math.Min(t, 7);
            var st = new Rect(x, r.Y, w, stH);
            ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF2A2D32)), t == S.Selected ? new ImmutablePen((IImmutableBrush)Ui.Amber, 1.5) : Ui.FaceEdge, st, 4, 4);
            Plasma.DotText(ctx, $"{t + 1}", st.X + 8, st.Y + 8, 2.2, 1);
            Tape(ctx, st.X + 26, st.Y + 6, names.GetValueOrDefault(S.NameOf(t), S.NameOf(t)), t % 2 == 0 ? -1.2 : 1.2);
            // Where the channel's signal comes from.
            Ui.Led(ctx, new Point(st.X + 12, st.Y + 36), 2.4, S.Meters.Stage[mt, 0] > -60, LevelLed(S.Meters.Stage[mt, 0]), Ui.GreenOff);
            Ui.Text(ctx, t == 0 ? "◀ FONTE direto" : $"◀ DS-S saída {t}", st.X + 20, st.Y + 30, 8, Ui.Label, Ui.Mono);
            Knob(ctx, new Point(st.Center.X, st.Y + 62), 13, "TRIM", $"{c.Trim:+0;-0} dB", LinNorm(c.Trim, -24, 24), n => { c.Trim = (float)Math.Round(FromLin(n, -24, 24)); S.MixChanged(); });
            // Inserts in signal order, joined by a thin cable, each with the level it hands on.
            (string, bool, int)[] ins = [("FILT", c.HpOn || c.LpOn, 2), ("GATE", c.GateOn, 3), ("EQ", c.EqOn, 4), ("COMP", c.CompOn, 5)];
            ctx.DrawLine(new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF0B0C0D)), 3), new Point(st.X + 5, st.Y + 100), new Point(st.X + 5, st.Y + 100 + ins.Length * 17));
            for (int i = 0; i < ins.Length; i++)
            {
                var rr = new Rect(st.X + 10, st.Y + 100 + i * 17, w - 18, 15);
                ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(ins[i].Item2 ? 0xFF24272Bu : 0xFF16181Bu)), Ui.FaceEdge, rr, 2, 2);
                Ui.Text(ctx, ins[i].Item1, rr.X + 5, rr.Y + 1, 8, ins[i].Item2 ? Ui.LabelBright : Ui.Label, Ui.Mono);
                if (!ins[i].Item2) Ui.Text(ctx, "bypass", rr.X + 40, rr.Y + 2, 7, Ui.Label, Ui.Mono);
                float lv = S.Meters.Stage[mt, ins[i].Item3];
                Ui.Led(ctx, new Point(rr.Right - 18, rr.Center.Y), 2.2, lv > -60, LevelLed(lv), Ui.GreenOff);
                Ui.Led(ctx, new Point(rr.Right - 7, rr.Center.Y), 2.6, ins[i].Item2, Ui.Green, Ui.GreenOff);
            }
            // The send to the analysis: a copy from the tap, before the fader.
            var an = S.AnalysersOf(t);
            var send = new Rect(st.X + 10, st.Y + 172, w - 18, 15);
            ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(an.Length > 0 ? 0xFF2E2614u : 0xFF16181Bu)), new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF5A4410)), 1, new ImmutableDashStyle([3, 2], 0)), send, 2, 2);
            Ui.Text(ctx, an.Length > 0 ? "ENVIO ▶ " + string.Join(" ", an) : "sem envio", send.X + 5, send.Y + 1, 8, an.Length > 0 ? Ui.Amber : Ui.Label, Ui.Mono);
            Key(ctx, new Rect(st.X + 8, st.Y + 194, w - 16, 18), "CADEIA ↓", () => { S.Selected = track; S.Select(); }, t == S.Selected, Ui.Amber);
            Knob(ctx, new Point(st.Center.X, st.Y + 236), 12, "PAN", c.Pan == 0 ? "C" : c.Pan < 0 ? $"L{-c.Pan * 100:0}" : $"R{c.Pan * 100:0}", (c.Pan + 1) / 2, n => { c.Pan = (float)Math.Round(n * 2 - 1, 2); if (Math.Abs(c.Pan) < 0.04f) c.Pan = 0; S.MixChanged(); });
            double kw = Math.Min(40, (w - 30) / 2);
            Key(ctx, new Rect(st.Center.X - kw - 5, st.Y + 274, kw, 22), "M", () => { c.Mute = !c.Mute; S.MixChanged(); }, c.Mute, Ui.Amber);
            Key(ctx, new Rect(st.Center.X + 5, st.Y + 274, kw, 22), "S", () => { c.Solo = !c.Solo; S.MixChanged(); }, c.Solo, Ui.Green);
            // Meter (post-fader peak) and fader.
            var slot = new Rect(st.Center.X + 6, st.Y + 312, 20, Math.Max(40, st.Bottom - st.Y - 312 - 34));
            float pk = S.Meters.Peak[mt];
            int segs = (int)(slot.Height / 7);
            for (int i = 0; i < segs; i++)
            {
                double db = -60 + 63.0 * i / segs;
                var col = db > 0 ? Ui.Red : db > -6 ? Ui.Amber : Ui.Green;
                ctx.DrawRectangle(pk >= db ? col : new ImmutableSolidColorBrush(Color.FromUInt32(0xFF15301F)), null, new Rect(st.X + 14, slot.Bottom - (i + 1) * 7, 10, 5));
            }
            Fader(ctx, slot, FaderNorm(c.Fader), n => { c.Fader = FaderDb(n); S.MixChanged(); });
            Plasma.Text(ctx, c.Fader <= -90 ? "-inf" : $"{c.Fader:+0.0;-0.0}", st.Center.X, st.Bottom - 24, 11, true, Ui.Mono, Ui.Align.Center);
            // Into the sum bus — open while the channel sounds.
            bool sounding = !c.Mute && !(anySolo && !c.Solo);
            Jack(ctx, new Point(st.Center.X, st.Bottom));
            if (sounding) Patch(ctx, new Point(st.Center.X, st.Bottom), new Point(st.Center.X, busY), pk, 0, thin: true, led: false);
            else Ui.Text(ctx, "✕", st.Center.X + 8, st.Bottom + 2, 9, Ui.Amber, Ui.SansBold);
        }

        // Master.
        var m = S.Mix.Master;
        double mw = 300, mx = r.Right - mw;
        var ms = new Rect(mx, r.Y, mw, stH);
        Patch(ctx, new Point(r.X + w / 2, busY), new Point(ms.X + 26, busY), master, 0, thin: true, led: false);
        Ui.Text(ctx, "BUS DE SOMA ▶ MASTER", (r.X + w / 2 + ms.X) / 2, busY - 12, 7, Ui.Label, Ui.SansBold, Ui.Align.Center);
        Patch(ctx, new Point(ms.X + 26, busY), new Point(ms.X + 26, ms.Bottom), master, 0, thin: true, led: false);
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF2A2D32)), new ImmutablePen((IImmutableBrush)Ui.Amber, 1), ms, 4, 4);
        Jack(ctx, new Point(ms.X + 26, ms.Bottom));
        Tape(ctx, ms.X + 10, ms.Y + 6, "MASTER", -1);
        Ui.Text(ctx, "BUS ▶ EQ ▶ COMP ▶ FADER ▶ DL-6 ▶ SAÍDA", ms.Right - 10, ms.Y + 10, 7, Ui.Label, Ui.SansBold, Ui.Align.Right);
        float vuL = S.Meters.MasterRms[0] + 18, vuR = S.Meters.MasterRms[1] + 18;   // 0 VU = -18 dBFS
        InputModule.DrawVu(ctx, new Rect(ms.X + 12, ms.Y + 34, 134, 80), vuL);
        InputModule.DrawVu(ctx, new Rect(ms.X + 154, ms.Y + 34, 134, 80), vuR);
        // The master chain in signal order (the fader sits before the limiter, as in the core).
        var keys = new[] { new Rect(ms.X + 12, ms.Y + 126, 60, 22), new Rect(ms.X + 84, ms.Y + 126, 60, 22), new Rect(ms.X + 156, ms.Y + 126, 86, 22) };
        Key(ctx, keys[0], "EQ", () => { m.EqOn = !m.EqOn; S.MixChanged(); }, m.EqOn, Ui.Green);
        Key(ctx, keys[1], "COMP", () => { m.CompOn = !m.CompOn; S.MixChanged(); }, m.CompOn, Ui.Green);
        Key(ctx, keys[2], "DL-6 LIMIT", () => { m.LimiterOn = !m.LimiterOn; S.MixChanged(); }, m.LimiterOn, Ui.Green);
        for (int i = 0; i < 2; i++) Patch(ctx, new Point(keys[i].Right, keys[i].Center.Y + 4), new Point(keys[i + 1].X, keys[i + 1].Center.Y + 4), master, 6, thin: true, led: false);
        Knob(ctx, new Point(ms.X + 40, ms.Y + 186), 14, "LIMIAR", $"{m.CompThreshold:0}", LinNorm(m.CompThreshold, -40, 0), n => { m.CompThreshold = (float)Math.Round(FromLin(n, -40, 0)); S.MixChanged(); }, m.CompOn);
        Knob(ctx, new Point(ms.X + 100, ms.Y + 186), 14, "RAZÃO", $"{m.CompRatio:0.#}:1", LogNorm(m.CompRatio, 1, 10), n => { m.CompRatio = (float)Math.Round(FromLog(n, 1, 10), 1); S.MixChanged(); }, m.CompOn);
        Knob(ctx, new Point(ms.X + 160, ms.Y + 186), 14, "TETO", $"{m.Ceiling:0.0}", LinNorm(m.Ceiling, -12, 0), n => { m.Ceiling = (float)Math.Round(FromLin(n, -12, 0), 1); S.MixChanged(); }, m.LimiterOn);
        for (int i = 0; i < 8; i++) Ui.Led(ctx, new Point(ms.X + 196 + i * 11, ms.Y + 186), 3, S.Meters.LimiterGr > 0.5 + i, Ui.Red, Ui.RedOff);
        Ui.Text(ctx, "LIMITANDO", ms.X + 196, ms.Y + 196, 7, Ui.Label, Ui.SansBold);
        var mslot = new Rect(ms.X + 40, ms.Y + 236, 20, Math.Max(40, ms.Bottom - ms.Y - 236 - 34));
        Fader(ctx, mslot, FaderNorm(m.Fader), n => { m.Fader = FaderDb(n); S.MixChanged(); });
        Plasma.Text(ctx, $"{m.Fader:+0.0;-0.0}", mslot.Center.X, ms.Bottom - 24, 11, true, Ui.Mono, Ui.Align.Center);
        // Output: headphones / the bounce.
        var outJ = new Point(ms.Right - 22, mslot.Y + 6);
        Jack(ctx, outJ);
        Ui.Text(ctx, "SAÍDA · fone / bounce", outJ.X + 8, outJ.Y + 10, 7, Ui.LabelBright, Ui.SansBold, Ui.Align.Right);
        Patch(ctx, new Point(mslot.Center.X + 20, outJ.Y), outJ, master, 0, thin: true);
        Key(ctx, new Rect(ms.X + 110, ms.Bottom - 70, 150, 34), "BOUNCE WAV", S.Bounce, false, Ui.Amber);
        Ui.Text(ctx, "escuta e bounce; não a transcrição", ms.X + 110, ms.Bottom - 30, 7, Ui.Label, Ui.Mono);
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
        var flow = new FlowUnit(_s) { Margin = new Thickness(0, 0, 0, 6) };
        var meters = new MeterUnit(_s) { Margin = new Thickness(6, 0, 0, 0) };
        _recorder = recorder;
        _console = console;
        _library = library;
        _flow = flow;
        _meters = meters;
        _units = [transport, flow, library, recorder, console, meters, chain];
        var left = new RackRail();
        var right = new RackRail();
        SetDock(left, Dock.Left);
        SetDock(right, Dock.Right);
        Children.Add(left);
        Children.Add(right);
        // As a DAW: the transport and its toolbar on top, the arrangement (recorder, or the console)
        // in the middle with the browser and the meters as side panels that open and close, the
        // selected track's device chain at the bottom; the signal routing opens under the transport.
        // Taller than a small window: the rack scrolls.
        var body = new DockPanel { Margin = new Thickness(6, 0) };
        SetDock(transport, Dock.Top);
        transport.Margin = new Thickness(0, 6);
        SetDock(flow, Dock.Top);
        SetDock(_status, Dock.Bottom);
        SetDock(chain, Dock.Bottom);
        chain.Margin = new Thickness(0, 6, 0, 0);
        SetDock(library, Dock.Left);
        library.Margin = new Thickness(0, 0, 6, 0);
        SetDock(meters, Dock.Right);
        body.Children.Add(transport);
        body.Children.Add(flow);
        body.Children.Add(_status);
        body.Children.Add(chain);
        body.Children.Add(library);
        body.Children.Add(meters);
        body.Children.Add(new Panel { Children = { recorder, console } });
        Children.Add(new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        _s.Changed += Refresh;
        if (_s.Player is null) _s.Status = "núcleo nativo ausente: biblioteca sem reprodução nem análise";
        _s.ScanPlugins();   // only bundles changed since the cache are scanned again
        Refresh();
    }

    readonly RecorderUnit _recorder;
    readonly StudioUnit _console, _library, _flow, _meters;

    void Refresh()
    {
        _recorder.IsVisible = !_s.ConsoleView;
        _recorder.MinHeight = _recorder.Needed;
        _console.IsVisible = _s.ConsoleView;
        _library.IsVisible = _s.ShowBrowser && !_s.ConsoleView;   // mixing: the console takes the library's width
        _flow.IsVisible = _s.ShowRouting;
        _meters.IsVisible = _s.ShowMeters;
        foreach (var u in _units) u.InvalidateVisual();
        _status.Text = _s.Status;
    }

    /// Animation tick from the window: job progress, meters, playhead, readouts.
    public void Tick() { _s.Poll(); _s.PollMeters(); Refresh(); }
    public void ShowConsole(bool on) { _s.ConsoleView = on; Refresh(); }
    public StudioState.InputHooks? Input { set => _s.Input = value; }
    public void ToggleRec() { _s.ToggleRec(); Refresh(); }
    public void ShowPlugins() { _s.PluginView = true; Refresh(); }
    public void ChoirMode() { while (!_s.Choir) _s.CycleMode(); Refresh(); }
    public void AddFirstPlugin() { if (_s.Plugins.FirstOrDefault() is { } p) _s.AddVst(p); Refresh(); }
    public void OpenFirstInsert() { if (_s.Channel(_s.Selected).Vst.FirstOrDefault() is { } v) _s.OpenPanel(v); Refresh(); }
    public void SelectChannel(int track) { _s.Selected = Math.Clamp(track, 0, _s.Tracks - 1); Refresh(); }
    /// Screenshot tool: a patched chain on screen only (nothing saved to the take): EQ first, the
    /// filter pulled out, gate and compressor in parallel.
    public void PatchDemo()
    {
        var c = _s.Channel(_s.Selected);
        (c.Chain, c.ParallelFirst, c.ParallelLast, c.ParallelMix) = (["eq", "trim", "gate", "comp"], "gate", "comp", 0.6f);
        Refresh();
    }

    public void TogglePlay() => _s.TogglePlay();
    public void OpenFirst() { if (_s.Library.Count > 0) _s.Open(_s.Library[0].Path); Refresh(); }
    /// Screenshot tool: the newest take already separated (its stems as tracks).
    public void OpenSeparated() { if (_s.Library.FirstOrDefault(i => Directory.Exists(StudioProject.StemsDir(i.Path))) is { } it) _s.Open(it.Path); Refresh(); }
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
    /// Screenshot tool: plays from `seconds` for `forSeconds` without a device.
    public void PlayHeadless(double seconds, double forSeconds)
    {
        if (_s.Player is not { } p) return;
        p.Seek((ulong)(seconds * _s.Rate));
        p.Play();
        long now = Environment.TickCount64;
        for (double t = 0; t < forSeconds; t += 0.05) { p.Pull((int)(0.05 * _s.Rate)); _s.PollMeters(now += 50); }
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
