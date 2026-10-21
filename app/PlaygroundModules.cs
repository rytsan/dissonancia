using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform.Storage;

namespace Dissonancia;

/// A LIVE unit with hardware keys and knobs (as STUDIO's): hit areas are registered while drawing.
public abstract class KeyedModule : RackModule
{
    readonly List<(Rect R, Action A)> _keys = [];
    readonly List<(Rect R, double Norm, Action<double> Set)> _knobs = [];
    (Rect R, double Norm, Action<double> Set)? _drag;
    double _dragY;
    protected static readonly IBrush KeyFace = new ImmutableLinearGradientBrush(
        [new ImmutableGradientStop(0, Color.FromUInt32(0xFF3A3E44)), new ImmutableGradientStop(1, Color.FromUInt32(0xFF26292D))],
        startPoint: new RelativePoint(0, 0, RelativeUnit.Relative), endPoint: new RelativePoint(0, 1, RelativeUnit.Relative));
    protected static readonly IPen KeyEdge = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF55595F)), 1);
    static readonly IBrush KnobBody = new ImmutableSolidColorBrush(Color.FromUInt32(0xFF1C1E21));
    static readonly IPen KnobRim = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF5A5F66)), 1.5);
    static readonly IPen KnobMark = new ImmutablePen((IImmutableBrush)Ui.Amber, 2.4, lineCap: PenLineCap.Round);

    protected Playground P => Playground.Current;

    protected sealed override void DrawContent(DrawingContext ctx, Rect r)
    {
        _keys.Clear();
        _knobs.Clear();
        Draw(ctx, r);
    }
    protected abstract void Draw(DrawingContext ctx, Rect r);

    protected void Key(DrawingContext ctx, Rect r, string label, Action action, bool lit = false, IBrush? led = null, bool withLed = true)
    {
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF0B0C0D)), null, r.Translate(new Vector(0, 2)), 3, 3);
        ctx.DrawRectangle(KeyFace, KeyEdge, r, 3, 3);
        Ui.Text(ctx, label, r.Center.X, r.Center.Y - 7, 11, Ui.LabelBright, Ui.SansBold, Ui.Align.Center);
        if (withLed) Ui.Led(ctx, new Point(r.Right - 6, r.Y + 6), 2.6, lit, led ?? Ui.Green, Ui.GreenOff);
        _keys.Add((r, action));
    }
    protected void Area(Rect r, Action action) => _keys.Add((r, action));

    protected void Knob(DrawingContext ctx, Point c, double r, string label, string value, double norm, Action<double> set)
    {
        norm = Math.Clamp(norm, 0, 1);
        ctx.DrawEllipse(KnobBody, KnobRim, c, r, r);
        double a = (-135 + 270 * norm) * Math.PI / 180;
        ctx.DrawLine(KnobMark, new Point(c.X + Math.Sin(a) * r * 0.25, c.Y - Math.Cos(a) * r * 0.25), new Point(c.X + Math.Sin(a) * r * 0.9, c.Y - Math.Cos(a) * r * 0.9));
        Ui.Text(ctx, value, c.X, c.Y + r + 3, 9, Ui.LabelBright, Ui.Mono, Ui.Align.Center);
        Ui.Text(ctx, label, c.X, c.Y + r + 14, 7, Ui.Label, Ui.SansBold, Ui.Align.Center);
        _knobs.Add((new Rect(c.X - r - 4, c.Y - r - 4, 2 * r + 8, 2 * r + 8), norm, set));
    }

    /// A click not on a key or a knob (right button included).
    protected virtual void Pressed(PointerPressedEventArgs e, Point p) { }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var p = e.GetPosition(this);
        bool right = e.GetCurrentPoint(this).Properties.IsRightButtonPressed;
        if (!right)
        {
            foreach (var k in _knobs)
                if (k.R.Contains(p)) { (_drag, _dragY) = (k, p.Y); e.Pointer.Capture(this); e.Handled = true; return; }
            foreach (var (r, a) in _keys)
                if (r.Contains(p)) { a(); InvalidateVisual(); e.Handled = true; return; }
        }
        Pressed(e, p);
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_drag is not { } d) { base.OnPointerMoved(e); return; }
        d.Set(Math.Clamp(d.Norm + (_dragY - e.GetPosition(this).Y) / 200, 0, 1));
        InvalidateVisual();
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_drag is null) { base.OnPointerReleased(e); return; }
        _drag = null;
        e.Pointer.Capture(null);
        P.Save();
    }

    protected static double DbNorm(float db) => (db + 54) / 60.0;   // -54 .. +6 dB
    protected static float NormDb(double n) => (float)Math.Round(n * 60 - 54, 1);
}

/// DS-X MÉTRICAS: what the LIVE pipeline measures, as lit keys; the chosen one's last 30 s plotted.
public sealed class MetricsModule : KeyedModule
{
    public MetricsModule() { Title = "DS-X  MÉTRICAS DO LIVE"; }

    int _sel;
    readonly float[][] _hist = Enumerable.Range(0, 8).Select(_ => new float[300]).ToArray();
    int _head;
    long _lastSample;

    protected override void Draw(DrawingContext ctx, Rect r)
    {
        var f = Frame!;
        bool chords = Session.IsChordMode;
        float lat = Session.Live == LiveMode.FullSong ? f.HarmonyLatencyMs : chords ? f.ChordLatencyMs : f.NoteLatencyMs;
        float tempoErr = f.DetectedBpm > 0 && f.Bpm > 0 ? 100 * Math.Abs(f.DetectedBpm - f.Bpm) / f.Bpm : -1;
        (string Name, string Value, string Sub, bool Ok, float Plot)[] tiles =
        [
            ("LATÊNCIA", lat > 0 ? $"{lat:0} ms" : "—", Session.Live == LiveMode.FullSong ? $"acorde {f.ChordLatencyMs:0} ms" : chords ? "acorde tocado" : "nota estável", lat is > 0 and < 700, lat),
            ("ENTRADA", $"{f.PeakDbfs:0} dB", f.ClipLatched ? "CLIP" : $"VU {f.VuLevel:+0;-0}", !f.ClipLatched && f.PeakDbfs < -1, f.PeakDbfs),
            ("CPU", $"{f.CpuPercent:0} %", "análise", f.CpuPercent < 60, f.CpuPercent),
            ("PERDAS", $"{f.Xruns + f.RecorderGaps}", $"xrun {f.Xruns} · gaps {f.RecorderGaps}", f.Xruns + f.RecorderGaps == 0, f.Xruns + f.RecorderGaps),
            ("AFINAÇÃO", f.TuningValid ? $"{f.TuningCents:+0;-0} ¢" : "—", f.TuningValid ? $"A4 = {440 * Math.Pow(2, f.TuningCents / 1200):0.0} Hz" : "toque algo", !f.TuningValid || Math.Abs(f.TuningCents) < 15, f.TuningCents),
            ("TEMPO", f.DetectedBpm > 0 ? $"{f.DetectedBpm:0} bpm" : "—", f.Bpm > 0 ? $"metrônomo {f.Bpm:0}" : "metrônomo off", tempoErr is >= 0 and < 3 || tempoErr < 0, f.DetectedBpm),
            ("CONFIANÇA", chords ? $"{f.ChordConfidence * 100:0} %" : f.NoteValid ? "nota" : "—", chords ? (f.ChordConfirmed ? "confirmado" : "provisório") : $"{f.Cents:+0;-0} ¢", !chords || f.ChordConfidence > 0.5f, chords ? f.ChordConfidence * 100 : f.Cents),
            ("SAÍDA", $"{f.CaptureMs:0} ms", $"{f.SampleRate / 1000:0.#} kHz · PG {P.Voices} voz", P.PeakDb < -1, P.PeakDb),
        ];
        long now = Environment.TickCount64;
        if (now - _lastSample >= 100)
        {
            _lastSample = now;
            for (int i = 0; i < tiles.Length; i++) _hist[i][_head] = tiles[i].Plot;
            _head = (_head + 1) % 300;
        }
        bool plot = r.Height > 90;
        double tw = Math.Min(150, (plot ? r.Width * 0.62 : r.Width) / tiles.Length - 6), th = Math.Min(52, r.Height - 4);
        for (int i = 0; i < tiles.Length; i++)
        {
            var t = tiles[i];
            var b = new Rect(r.X + i * (tw + 6), r.Y + 2, tw, th);
            int k = i;
            ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF0B0C0D)), null, b.Translate(new Vector(0, 2)), 4, 4);
            ctx.DrawRectangle(KeyFace, i == _sel ? new ImmutablePen((IImmutableBrush)Ui.Amber, 1.5) : KeyEdge, b, 4, 4);
            Ui.Led(ctx, new Point(b.X + 9, b.Y + 9), 3, true, t.Ok ? Ui.Green : Ui.Red, Ui.GreenOff);
            Ui.Text(ctx, t.Name, b.X + 17, b.Y + 3, 8, Ui.Label, Ui.SansBold);
            Ui.Text(ctx, t.Value, b.X + 8, b.Y + 15, 15, t.Ok ? Ui.LcdText : Ui.Red, Ui.Mono);
            Ui.Text(ctx, t.Sub, b.X + 8, b.Bottom - 13, 8, Ui.Label, Ui.Mono);
            Area(b, () => _sel = k);
        }
        if (!plot) return;
        var g = new Rect(r.X + tiles.Length * (tw + 6) + 6, r.Y + 2, r.Right - (r.X + tiles.Length * (tw + 6) + 6), r.Height - 6);
        if (g.Width < 60) return;
        ctx.DrawRectangle(Ui.Scope, Ui.LcdEdge, g, 3, 3);
        var h = _hist[_sel];
        float lo = h.Min(), hi = h.Max();
        if (hi - lo < 1) { lo -= 0.5f; hi += 0.5f; }
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            for (int i = 0; i < 300; i++)
            {
                var p = new Point(g.X + 4 + (g.Width - 8) * i / 299, g.Bottom - 6 - (g.Height - 22) * (h[(_head + i) % 300] - lo) / (hi - lo));
                if (i == 0) c.BeginFigure(p, false); else c.LineTo(p);
            }
            c.EndFigure(false);
        }
        ctx.DrawGeometry(null, Ui.ScopeTrace, geo);
        Ui.Text(ctx, $"{tiles[_sel].Name} · últimos 30 s · {lo:0.#} … {hi:0.#}", g.X + 6, g.Y + 3, 9, Ui.LcdDim, Ui.Mono);
    }
}

/// DS-P PADS: 16 pads (click plays, right click loads a WAV, shift + right click empties) and the
/// sampler (one WAV on the MIDI notes, its root note and level).
public sealed class PadsModule : KeyedModule
{
    public PadsModule() { Title = "DS-P  PADS · SAMPLER"; }

    readonly List<(Rect R, int Slot)> _pads = [];

    protected override void Draw(DrawingContext ctx, Rect r)
    {
        _pads.Clear();
        var s = P.S;
        long now = Environment.TickCount64;
        double side = Math.Min((r.Height - 6) / 4, 74), gx = r.X;
        for (int k = 0; k < 16; k++)
        {
            int row = 3 - k / 4, col = k % 4;   // pad 1 bottom left, as on a pad controller
            var b = new Rect(gx + col * (side + 6), r.Y + row * (side + 2), side, side - 2);
            bool has = s.Pads[k].Path.Length > 0;
            double glow = Math.Clamp(1 - (now - P.Flash[k]) / 250.0, 0, 1);
            var face = Color.FromUInt32(has ? 0xFF2B3A33u : 0xFF1A1C1F);
            if (glow > 0) face = Color.FromArgb(255, (byte)(face.R + (255 - face.R) * glow * 0.8), (byte)(face.G + (176 - face.G) * glow * 0.8), (byte)(face.B * (1 - glow)));
            ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF060707)), null, b.Translate(new Vector(0, 2)), 6, 6);
            ctx.DrawRectangle(new ImmutableSolidColorBrush(face), KeyEdge, b, 6, 6);
            Ui.Text(ctx, $"{k + 1}", b.X + 5, b.Y + 3, 9, has ? Ui.LabelBright : Ui.Label, Ui.SansBold);
            string name = s.Pads[k].Name;
            Ui.Text(ctx, name.Length > 9 ? name[..9] : name.Length > 0 ? name : "vazio", b.X + 5, b.Bottom - 15, 8, has ? Ui.LcdText : Ui.Label, Ui.Mono);
            _pads.Add((b, k));
        }
        // The sampler.
        double sx = gx + 4 * (side + 6) + 12;
        var sb = new Rect(sx, r.Y, Math.Min(260, r.Right - sx), r.Height - 4);
        if (sb.Width < 120) return;
        ctx.DrawRectangle(Ui.Lcd, Ui.LcdEdge, new Rect(sb.X, sb.Y, sb.Width, 40), 3, 3);
        double glowS = Math.Clamp(1 - (now - P.Flash[16]) / 250.0, 0, 1);
        Ui.Led(ctx, new Point(sb.X + 10, sb.Y + 10), 3.5, glowS > 0, Ui.Amber, Ui.AmberOff);
        Ui.Text(ctx, "SAMPLER", sb.X + 20, sb.Y + 3, 9, Ui.LcdDim, Ui.SansBold);
        string sn = s.SamplerPath.Length > 0 ? Path.GetFileNameWithoutExtension(s.SamplerPath) : "sem WAV — CARREGAR";
        Ui.Text(ctx, sn.Length > 28 ? sn[..28] : sn, sb.X + 8, sb.Y + 19, 11, Ui.LcdText, Ui.Mono);
        Key(ctx, new Rect(sb.X, sb.Y + 48, 92, 22), "CARREGAR", () => Pick(16), withLed: false);
        Key(ctx, new Rect(sb.X + 98, sb.Y + 48, 60, 22), "TOCAR", () => { P.Note(s.SamplerRoot, true); }, withLed: false);
        string[] names = ["C", "C♯", "D", "E♭", "E", "F", "F♯", "G", "A♭", "A", "B♭", "B"];
        double ky = sb.Y + 98;
        if (ky + 30 < r.Bottom)
        {
            Knob(ctx, new Point(sb.X + 24, ky), 14, "RAIZ", $"{names[s.SamplerRoot % 12]}{s.SamplerRoot / 12 - 1}", s.SamplerRoot / 127.0, n => { s.SamplerRoot = (int)Math.Round(n * 127); P.Apply(); });
            Knob(ctx, new Point(sb.X + 74, ky), 14, "NÍVEL", $"{s.SamplerGainDb:+0;-0}", DbNorm(s.SamplerGainDb), n => { s.SamplerGainDb = NormDb(n); P.Apply(); });
            Knob(ctx, new Point(sb.X + 124, ky), 14, "MASTER", $"{s.MasterDb:+0;-0}", DbNorm(s.MasterDb), n => { s.MasterDb = NormDb(n); P.Apply(); });
            for (int i = 0; i < 8; i++) Ui.Led(ctx, new Point(sb.X + 160 + i * 9, ky), 3, P.PeakDb > -48 + i * 6, i > 6 ? Ui.Red : i > 4 ? Ui.Amber : Ui.Green, Ui.GreenOff);
        }
        string hint = P.Attached ? "clique toca · botão direito carrega WAV · shift + direito esvazia" : "dê START no LIVE (saída de clique ligada) para ouvir";
        Ui.Text(ctx, P.Status.Length > 0 ? P.Status : hint, sb.X, sb.Bottom - 12, 8, P.Status.Length > 0 ? Ui.Amber : Ui.Label, Ui.Sans);
    }

    protected override void Pressed(PointerPressedEventArgs e, Point p)
    {
        foreach (var (b, k) in _pads)
        {
            if (!b.Contains(p)) continue;
            var pt = e.GetCurrentPoint(this).Properties;
            if (pt.IsRightButtonPressed)
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) P.LoadSlot(k, "");
                else Pick(k);
            }
            else P.Hit(k, (float)Math.Clamp(0.4 + 0.6 * (b.Bottom - p.Y) / b.Height, 0.2, 1));   // higher on the pad = harder
            InvalidateVisual();
            return;
        }
    }

    async void Pick(int slot)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } sp) return;
        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = slot == 16 ? "WAV do sampler" : $"WAV do pad {slot + 1}",
            FileTypeFilter = [new FilePickerFileType("Áudio") { Patterns = ["*.wav", "*.flac", "*.mp3"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) P.LoadSlot(slot, path);
        InvalidateVisual();
    }
}

/// DS-Q SEQUENCER: 16 steps (sixteenths from the bar's downbeat, on the metronome's grid) per pad
/// with a sound; the step playing lit.
public sealed class SequencerModule : KeyedModule
{
    public SequencerModule() { Title = "DS-Q  SEQUENCER · 16 PASSOS"; }

    protected override void Draw(DrawingContext ctx, Rect r)
    {
        var s = P.S;
        Key(ctx, new Rect(r.X, r.Y, 70, 24), s.SequencerOn ? "■ SEQ" : "▶ SEQ", () => P.SetSequencer(!s.SequencerOn), s.SequencerOn);
        Key(ctx, new Rect(r.X, r.Y + 30, 70, 20), "LIMPAR", () => { Array.Clear(s.Steps); P.Apply(); P.Save(); }, withLed: false);
        Ui.Text(ctx, $"♩ = {Frame!.Bpm:0}", r.X + 4, r.Y + 56, 10, Ui.LabelBright, Ui.Mono);
        Ui.Text(ctx, "grade do metrônomo", r.X + 4, r.Y + 70, 7, Ui.Label, Ui.Sans);
        var rows = Enumerable.Range(0, 16).Where(k => s.Pads[k].Path.Length > 0 || s.Steps[k] != 0).ToList();
        if (rows.Count == 0) rows = [0, 1, 2, 3];
        double x0 = r.X + 150, cw = Math.Max(14, (r.Right - x0) / 16 - 3), rh = Math.Max(10, Math.Min(22, (r.Height - 16) / rows.Count - 3));
        int steps = Math.Clamp(Session.BeatsPerBar * 4, 1, 16);
        for (int c = 0; c < 16; c++)
            Ui.Led(ctx, new Point(x0 + c * (cw + 3) + cw / 2, r.Y + 5), 3, P.Step == c, c % 4 == 0 ? Ui.Amber : Ui.Green, c < steps ? Ui.GreenOff : Ui.RackBg);
        for (int i = 0; i < rows.Count; i++)
        {
            int k = rows[i];
            double y = r.Y + 14 + i * (rh + 3);
            if (y + rh > r.Bottom) break;
            string name = s.Pads[k].Name;
            Ui.Text(ctx, $"{k + 1} {(name.Length > 9 ? name[..9] : name)}", r.X + 80, y + rh / 2 - 6, 9, name.Length > 0 ? Ui.LabelBright : Ui.Label, Ui.Mono);
            for (int c = 0; c < 16; c++)
            {
                var b = new Rect(x0 + c * (cw + 3), y, cw, rh);
                bool on = (s.Steps[k] >> c & 1) != 0, now = P.Step == c && on;
                uint col = now ? 0xFFFFE08Au : on ? (c % 4 == 0 ? 0xFFFFB000u : 0xFF3DDC84u) : c % 4 == 0 ? 0xFF2A2D32u : 0xFF1C1E21u;
                ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(col)), KeyEdge, b, 2, 2);
                int pad = k, step = c;
                if (c < steps) Area(b, () => P.ToggleStep(pad, step));
            }
        }
    }
}

/// DS-I MIDI: the controller's port, activity, the last message, MIDI learn for the keys, and the
/// note map (36..51 the pads, any other note the sampler).
public sealed class MidiModule : KeyedModule
{
    public MidiModule() { Title = "DS-I  CONTROLADOR MIDI"; }

    uint _seen;
    long _flash;

    protected override void Draw(DrawingContext ctx, Rect r)
    {
        var s = P.S;
        if (P.MidiMessages != _seen) { _seen = P.MidiMessages; _flash = Environment.TickCount64; }
        bool act = Environment.TickCount64 - _flash < 120;
        var lcd = new Rect(r.X, r.Y, Math.Min(300, r.Width * 0.35), Math.Min(48, r.Height - 2));
        ctx.DrawRectangle(Ui.Lcd, Ui.LcdEdge, lcd, 3, 3);
        Ui.Led(ctx, new Point(lcd.X + 10, lcd.Y + 10), 3.5, act, Ui.Green, Ui.GreenOff);
        string port = s.MidiPort.Length > 0 ? s.MidiPort : P.Attached ? (P.Ports.Length == 0 ? "nenhuma porta" : "fechado") : "START no LIVE";
        Ui.Text(ctx, port.Length > 34 ? port[..34] : port, lcd.X + 20, lcd.Y + 3, 10, Ui.LcdText, Ui.Mono);
        Ui.Text(ctx, P.LastMidi.Length > 0 ? P.LastMidi : "notas 36–51 = pads · demais = sampler", lcd.X + 8, lcd.Y + 22, 9, Ui.LcdDim, Ui.Mono);
        Ui.Text(ctx, "MIDI learn: clique numa função e mexa no controle", lcd.Right + 8, r.Y + 40, 8, Ui.Label, Ui.Sans);
        Key(ctx, new Rect(lcd.Right + 8, r.Y, 84, 22), "PORTA ▸", () => P.NextPort(), s.MidiPort.Length > 0);
        double x = lcd.Right + 100;
        foreach (var (id, name) in Playground.Targets)
        {
            bool learning = P.Learning == id;
            var b = new Rect(x, r.Y, 104, 22);
            if (b.Right > r.Right) break;
            Key(ctx, b, learning ? "MEXA…" : name, () => P.Learning = learning ? null : id, s.Learned.ContainsKey(id) || learning, learning ? Ui.Amber : Ui.Green);
            string bound = s.Learned.GetValueOrDefault(id, "aprender");
            Ui.Text(ctx, bound, b.Center.X, b.Bottom + 4, 8, s.Learned.ContainsKey(id) ? Ui.LcdText : Ui.Label, Ui.Mono, Ui.Align.Center);
            x += 110;
        }
    }
}
