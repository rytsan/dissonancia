using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Dissonancia;

/// Phase 3 (R4): the coach's feedback on a REC take, from a local base model the user chose (a GGUF
/// file; nothing is fetched or bundled). The dz_coach helper reads only the take's session JSON and
/// writes a report it has checked against the facts (docs/IMPLEMENTATION.md, AI feedback).
public sealed class CoachSettings
{
    public string Model { get; set; } = "";
    public int GpuLayers { get; set; } = -1;   // -1: as many as this GPU's free memory holds; 0: CPU only
    public double LastSeconds { get; set; }    // how long the last report took on this machine

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dissonancia", "coach.json");

    public static CoachSettings Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<CoachSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// One report: the helper on a take's JSON, its checked report (stdout) and its verdict (stderr).
public sealed class CoachJob
{
    public static string Helper => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "dz_coach.exe" : "dz_coach");
    public static string Examples => Path.Combine(AppContext.BaseDirectory, "coach", "examples");
    public static bool Available => File.Exists(Helper);

    readonly Process _p;
    readonly System.Text.StringBuilder _out = new();
    readonly object _lock = new();
    string _verdict = "";
    public readonly string Take;

    CoachJob(Process p, string take) { _p = p; Take = take; }

    public static CoachJob? Start(CoachSettings s, string takeJson, out string? error)
    {
        error = null;
        var psi = new ProcessStartInfo(Helper) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in new[] { "run", s.Model, takeJson, "--examples", Examples, "--gpu", s.GpuLayers < 0 ? "auto" : s.GpuLayers.ToString(CultureInfo.InvariantCulture) })
            psi.ArgumentList.Add(a);
        try
        {
            var p = Process.Start(psi);
            if (p is null) { error = "o coach não iniciou"; return null; }
            var job = new CoachJob(p, takeJson);
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (job._lock) job._out.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not { } l) return;
                if (l.StartsWith("checked:") || l.StartsWith("dz_coach:") || l.StartsWith("cannot")) lock (job._lock) job._verdict = l;
                var m = Regex.Match(l, @"^checked:.*, ([0-9]+) s$");
                if (m.Success) job.Seconds = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return job;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { error = e.Message; return null; }
    }

    public double Seconds;   // as the helper measured it, once done

    /// What this machine will do with the model (dz_coach probe): its lines as key -> value.
    public static Dictionary<string, string> Probe(string model)
    {
        var d = new Dictionary<string, string>();
        try
        {
            var psi = new ProcessStartInfo(Helper) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            psi.ArgumentList.Add("probe");
            psi.ArgumentList.Add(model);
            using var p = Process.Start(psi);
            if (p is null) return d;
            string all = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            foreach (var line in all.Split('\n'))
                if (line.IndexOf(' ') is int i and > 0) d[line[..i]] = line[(i + 1)..].Trim();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { }
        return d;
    }

    /// The probe in a line: what runs where, and what to expect.
    public static string Describe(Dictionary<string, string> p, double lastSeconds)
    {
        if (!p.TryGetValue("verdict", out var v)) return "";
        string gpu = p.GetValueOrDefault("gpu", "-");
        string where = v switch
        {
            "gpu" => $"todo na GPU ({gpu})",
            "partial" => $"{p.GetValueOrDefault("gpu_layers")} de {p.GetValueOrDefault("layers")} camadas na GPU ({gpu}), o resto na CPU",
            "cpu" => "só CPU: cada relatório leva minutos",
            _ => $"não cabe: modelo {p.GetValueOrDefault("model_mb")} MB, RAM livre {p.GetValueOrDefault("ram_free_mb")} MB — escolha um modelo menor",
        };
        return lastSeconds > 0 ? $"{where} · último relatório: {lastSeconds:0} s" : where;
    }

    public bool Running => !_p.HasExited;
    /// 0: checked and passed; 3: the best of its tries, some items dropped; else an error.
    public int ExitCode => _p.HasExited ? _p.ExitCode : -1;
    public string Report { get { lock (_lock) return _out.ToString(); } }
    public string Verdict { get { lock (_lock) return _verdict; } }
    public void Cancel() { try { if (!_p.HasExited) _p.Kill(true); } catch (InvalidOperationException) { } }
}

/// DS-C COACH: the newest take's report, written once its metrics are in (after REC) or on request.
public sealed class CoachModule : KeyedModule
{
    public CoachModule() { Title = "DS-C  COACH"; }

    readonly CoachSettings _s = CoachSettings.Load();
    CoachJob? _job;
    string _machine = "";   // the probe of the chosen model on this machine
    bool _probed;
    TakeReport? _take;
    string _done = "", _status = "";
    long _lastLoad;
    bool _primed;   // the take there at start is not reported on by itself (only a new one, or on request)
    double _scroll;

    void Generate(string json)
    {
        _job?.Cancel();
        _job = CoachJob.Start(_s, json, out var error);
        _status = error ?? "";
        _done = json;
    }

    protected override void Draw(DrawingContext ctx, Rect r)
    {
        long now = Environment.TickCount64;
        if (now - _lastLoad > 1000)
        {
            _lastLoad = now;
            _take = TakeReport.Latest(_take);
            if (!_primed) { _primed = true; _done = _take?.Path ?? ""; }
        }
        bool model = _s.Model.Length > 0 && File.Exists(_s.Model);
        if (model && CoachJob.Available && !_probed)
        {
            _probed = true;
            var path = _s.Model;
            Task.Run(() => _machine = CoachJob.Describe(CoachJob.Probe(path), _s.LastSeconds));
        }
        if (_job is { Running: false, Seconds: > 0 } done && done.Seconds != _s.LastSeconds)
        {
            _s.LastSeconds = done.Seconds;
            _s.Save();
            _probed = false;   // the line shows the new time
        }
        // A new take with its metrics in: its report, once.
        if (model && CoachJob.Available && _take is { Pending: false } t && t.Path != _done && _job?.Running != true) Generate(t.Path);

        double kx = r.Right - 280;
        Key(ctx, new Rect(kx, r.Y, 130, 22), "MODELO GGUF…", PickModel, model, Ui.Green);
        Key(ctx, new Rect(kx + 136, r.Y, 70, 22), "GERAR", () => { if (_take is { Pending: false } tk && model) Generate(tk.Path); }, withLed: false);
        Key(ctx, new Rect(kx + 212, r.Y, 66, 22), "PARAR", () => { _job?.Cancel(); _status = "parado"; }, _job?.Running == true, Ui.Amber);
        string head = !CoachJob.Available ? "dz_coach não está instalado (build sem o coach)"
            : !model ? "escolha um modelo base em GGUF (você baixa e aceita a licença dele): MODELO GGUF…"
            : _take is null ? "nenhum take ainda: grave com REC"
            : _take.Pending ? "take gravado · esperando as métricas…"
            : _job?.Running == true ? $"escrevendo o relatório de {Path.GetFileNameWithoutExtension(_take.Path)}… (o modelo roda localmente)"
            : _status.Length > 0 ? _status
            : _job is { } j ? (j.ExitCode == 0 ? "relatório conferido: cada número vem das métricas do take" : j.ExitCode == 3 ? $"relatório com itens cortados na conferência ({j.Verdict})" : $"falhou: {j.Verdict}")
            : "";
        Ui.Text(ctx, _machine.Length > 0 ? $"{Path.GetFileName(_s.Model)} · {_machine}" : Path.GetFileName(_s.Model), r.X + 4, r.Y + 2, 9, Ui.LcdDim, Ui.Mono);
        Ui.Text(ctx, head, r.X + 4, r.Y + 14, 9, Ui.Label, Ui.Sans);

        var box = new Rect(r.X, r.Y + 30, r.Width, r.Height - 32);
        if (box.Height < 20) return;
        ctx.DrawRectangle(Ui.Lcd, Ui.LcdEdge, box, 3, 3);
        string text = _job?.Report ?? "";
        if (text.Length == 0) return;
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Ui.Sans, 11, Ui.LcdText) { MaxTextWidth = box.Width - 16 };
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, ft.Height - box.Height + 12));
        using (ctx.PushClip(box.Deflate(4))) ctx.DrawText(ft, new Point(box.X + 8, box.Y + 6 - _scroll));
        double end = ft.Height - box.Height + 12;
        if (end > 0) Area(box, () => _scroll = _scroll >= end ? 0 : _scroll + 40);   // a click scrolls on, then back to the top
    }

    async void PickModel()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } sp) return;
        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Modelo base (GGUF) para o coach",
            FileTypeFilter = [new FilePickerFileType("GGUF") { Patterns = ["*.gguf"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) { _s.Model = path; _s.LastSeconds = 0; _s.Save(); _done = ""; _probed = false; _machine = ""; }
        InvalidateVisual();
    }
}
