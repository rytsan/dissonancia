using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

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

/// The coach helper kept open (dz_coach serve): the model is loaded once and stays while reports are
/// asked for, then closes after a while unused (it holds the model's memory). One report at a time:
/// a session JSON path in, its checked report out.
public sealed class CoachServer
{
    public static string Helper => Environment.GetEnvironmentVariable("DZ_COACH_HELPER") is { Length: > 0 } h ? h   // tools and tests
        : Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "dz_coach.exe" : "dz_coach");
    public static string Examples => Path.Combine(Path.GetDirectoryName(Helper)!, "coach", "examples");
    public static bool Available => File.Exists(Helper);
    public static readonly TimeSpan Idle = TimeSpan.FromMinutes(10);

    Process? _p;
    readonly object _lock = new();
    readonly System.Text.StringBuilder _out = new();
    string _model = "", _plan = "", _error = "";
    bool _ready, _busy;
    string? _queued;
    long _lastUse;

    public string Take { get; private set; } = "";        // the report being written, or the last one
    public string Report { get; private set; } = "";
    public int Code { get; private set; } = -1;           // 0: passed the check; 3: some items dropped; 1: failed; -1: none yet
    public int Items { get; private set; }
    public int Dropped { get; private set; }
    public double Seconds { get; private set; }
    public bool Loading { get { lock (_lock) return _p is { HasExited: false } && !_ready; } }
    public bool Busy
    {
        get
        {
            lock (_lock)
            {
                if ((_busy || _queued is not null) && _p is not { HasExited: false })   // the helper died mid-report
                {
                    _busy = false;
                    _queued = null;
                    Code = 1;
                    if (_error.Length == 0) _error = "o coach parou";
                }
                return _busy || _queued is not null;
            }
        }
    }
    public string Plan { get { lock (_lock) return _plan; } }
    public string Error { get { lock (_lock) return _error; } }

    /// The report on a take's JSON: the helper starts (or restarts for another model) when needed.
    public void Request(CoachSettings s, string takeJson)
    {
        lock (_lock)
        {
            _lastUse = Environment.TickCount64;
            if (_p is not { HasExited: false } || _model != s.Model) Start(s);
            Take = takeJson;
            Report = "";
            Code = -1;
            _error = "";
            if (_ready && !_busy) Send(takeJson); else _queued = takeJson;
        }
    }

    void Start(CoachSettings s)
    {
        Stop();
        var psi = new ProcessStartInfo(Helper) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in new[] { "serve", s.Model, "--gpu", s.GpuLayers < 0 ? "auto" : s.GpuLayers.ToString(CultureInfo.InvariantCulture) })
            psi.ArgumentList.Add(a);
        if (Directory.Exists(Examples)) { psi.ArgumentList.Add("--examples"); psi.ArgumentList.Add(Examples); }   // else the helper's own
        try
        {
            var p = Process.Start(psi) ?? throw new InvalidOperationException("o coach não iniciou");
            _p = p;
            _model = s.Model;
            _ready = _busy = false;
            p.OutputDataReceived += (_, e) => Line(e.Data);
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not { } l) return;
                lock (_lock)
                {
                    if (l.StartsWith("plan: ")) _plan = l[6..];
                    else if (l.StartsWith("dz_coach:") || l.StartsWith("cannot")) _error = l;
                }
            };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { _error = e.Message; _p = null; }
    }

    void Send(string json)
    {
        _busy = true;
        _out.Clear();
        try { _p!.StandardInput.WriteLine(json); _p.StandardInput.Flush(); }
        catch (Exception e) when (e is IOException or InvalidOperationException) { _busy = false; _error = e.Message; }
    }

    void Line(string? l)
    {
        if (l is null) return;
        lock (_lock)
        {
            if (l == "@@ready")
            {
                _ready = true;
                if (_queued is { } q) { _queued = null; Send(q); }
                return;
            }
            if (l.StartsWith("@@end "))
            {
                var f = l.Split(' ');
                Code = int.Parse(f[1], CultureInfo.InvariantCulture);
                Items = int.Parse(f[2], CultureInfo.InvariantCulture);
                Dropped = int.Parse(f[3], CultureInfo.InvariantCulture);
                Seconds = double.Parse(f[4], CultureInfo.InvariantCulture);
                Report = _out.ToString();
                _busy = false;
                _lastUse = Environment.TickCount64;
                if (_queued is { } q) { _queued = null; Send(q); }
                return;
            }
            _out.AppendLine(l);
            Report = _out.ToString();
        }
    }

    /// Closes the helper when it has been idle long enough (its model's memory goes back).
    public void Tick()
    {
        lock (_lock)
            if (_p is { HasExited: false } && !_busy && _queued is null && Environment.TickCount64 - _lastUse > Idle.TotalMilliseconds) Stop();
    }

    public void Stop()
    {
        lock (_lock)
        {
            try { if (_p is { HasExited: false }) _p.Kill(true); } catch (InvalidOperationException) { }
            _p?.Dispose();
            _p = null;
            _ready = _busy = false;
            _queued = null;
        }
    }

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

}
