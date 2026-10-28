using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Dissonancia;

/// DS-C COACH: the newest take's report, written once its metrics are in (after REC) or on request.
public sealed class CoachModule : KeyedModule
{
    public CoachModule()
    {
        Title = "DS-C  COACH";
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _server.Stop();   // the model's memory goes with the app
    }

    readonly CoachSettings _s = CoachSettings.Load();
    readonly CoachServer _server = new();
    string _machine = "";   // the probe of the chosen model on this machine
    bool _probed;
    TakeReport? _take;
    string _done = "", _status = "";
    long _lastLoad;
    bool _primed;   // the take there at start is not reported on by itself (only a new one, or on request)
    double _scroll;

    void Generate(string json)
    {
        _server.Request(_s, json);
        _status = "";
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
        _server.Tick();
        if (model && CoachServer.Available && !_probed)
        {
            _probed = true;
            var path = _s.Model;
            Task.Run(() => _machine = CoachServer.Describe(CoachServer.Probe(path), _s.LastSeconds));
        }
        if (!_server.Busy && _server.Seconds > 0 && _server.Seconds != _s.LastSeconds)
        {
            _s.LastSeconds = _server.Seconds;
            _s.Save();
            _probed = false;   // the line shows the new time
        }
        // A new take with its metrics in: its report, once.
        if (model && CoachServer.Available && _take is { Pending: false } t && t.Path != _done && !_server.Busy) Generate(t.Path);

        double kx = r.Right - 280;
        Key(ctx, new Rect(kx, r.Y, 130, 22), "MODELO GGUF…", PickModel, model, Ui.Green);
        Key(ctx, new Rect(kx + 136, r.Y, 70, 22), "GERAR", () => { if (_take is { Pending: false } tk && model) Generate(tk.Path); }, withLed: false);
        Key(ctx, new Rect(kx + 212, r.Y, 66, 22), "PARAR", () => { _server.Stop(); _status = "parado: o modelo saiu da memória"; }, _server.Busy, Ui.Amber);
        string head = !CoachServer.Available ? "dz_coach não está instalado (build sem o coach)"
            : !model ? "escolha um modelo base em GGUF (você baixa e aceita a licença dele): MODELO GGUF…"
            : _take is null ? "nenhum take ainda: grave com REC"
            : _take.Pending ? "take gravado · esperando as métricas…"
            : _status.Length > 0 ? _status
            : _server.Loading ? $"carregando o modelo ({_server.Plan})…"
            : _server.Busy ? $"escrevendo o relatório de {Path.GetFileNameWithoutExtension(_server.Take)}… (o modelo roda localmente)"
            : _server.Code == 0 ? "relatório conferido: cada número vem das métricas do take"
            : _server.Code == 3 ? $"relatório com {_server.Dropped} de {_server.Items} itens cortados na conferência"
            : _server.Code == 1 || _server.Error.Length > 0 ? $"falhou: {_server.Error}"
            : "";
        Ui.Text(ctx, _machine.Length > 0 ? $"{Path.GetFileName(_s.Model)} · {_machine}" : Path.GetFileName(_s.Model), r.X + 4, r.Y + 2, 9, Ui.LcdDim, Ui.Mono);
        Ui.Text(ctx, head, r.X + 4, r.Y + 14, 9, Ui.Label, Ui.Sans);

        var box = new Rect(r.X, r.Y + 30, r.Width, r.Height - 32);
        if (box.Height < 20) return;
        ctx.DrawRectangle(Ui.Lcd, Ui.LcdEdge, box, 3, 3);
        string text = _server.Report;
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
