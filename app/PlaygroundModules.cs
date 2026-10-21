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

/// DS-Q SEQUENCER: the selected block's pattern, one bar at a time (16 sixteenths per pad with a
/// sound); the sixteenth playing lit when that block and bar play.
public sealed class SequencerModule : KeyedModule
{
    public SequencerModule() { Title = "DS-Q  SEQUENCER · BLOCO SELECIONADO"; }

    protected override void Draw(DrawingContext ctx, Rect r)
    {
        var s = P.S;
        var blk = P.SelBlock;
        P.Page = Math.Clamp(P.Page, 0, blk.Bars - 1);
        Ui.Text(ctx, $"BLOCO {blk.Name}", r.X + 2, r.Y, 12, Ui.Amber, Ui.SansBold);
        Key(ctx, new Rect(r.X, r.Y + 20, 22, 20), "◀", () => P.Page = Math.Max(0, P.Page - 1), withLed: false);
        Ui.Text(ctx, $"{P.Page + 1}/{blk.Bars}", r.X + 36, r.Y + 22, 11, Ui.LabelBright, Ui.Mono, Ui.Align.Center);
        Key(ctx, new Rect(r.X + 50, r.Y + 20, 22, 20), "▶", () => P.Page = Math.Min(blk.Bars - 1, P.Page + 1), withLed: false);
        Ui.Text(ctx, "compasso", r.X + 2, r.Y + 44, 7, Ui.Label, Ui.Sans);
        Key(ctx, new Rect(r.X, r.Y + 56, 72, 20), "LIMPAR", P.ClearBlock, withLed: false);
        if (blk.Notes.Count > 0) Ui.Text(ctx, $"{blk.Notes.Count} notas", r.X + 2, r.Y + 80, 8, Ui.LcdText, Ui.Mono);
        var rows = Enumerable.Range(0, 16).Where(k => s.Pads[k].Path.Length > 0 || Enumerable.Range(0, blk.Bars * 16).Any(i => blk.At(k, i) > 0)).ToList();
        if (rows.Count == 0) rows = [0, 1, 2, 3];
        double x0 = r.X + 150, cw = Math.Max(14, (r.Right - x0) / 16 - 3), rh = Math.Max(10, Math.Min(22, (r.Height - 16) / rows.Count - 3));
        int steps = Math.Clamp(Session.BeatsPerBar * 4, 1, 16);
        bool here = P.Playing && P.Entry >= 0 && P.Entry < s.Order.Count && s.Order[P.Entry].Block == P.SelEntry.Block && P.Bar == P.Page;
        for (int c = 0; c < 16; c++)
            Ui.Led(ctx, new Point(x0 + c * (cw + 3) + cw / 2, r.Y + 5), 3, here && P.Step == c, c % 4 == 0 ? Ui.Amber : Ui.Green, c < steps ? Ui.GreenOff : Ui.RackBg);
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
                int step = P.Page * 16 + c;
                bool on = blk.At(k, step) > 0, now = here && P.Step == c && on;
                uint col = now ? 0xFFFFE08Au : on ? (c % 4 == 0 ? 0xFFFFB000u : 0xFF3DDC84u) : c % 4 == 0 ? 0xFF2A2D32u : 0xFF1C1E21u;
                ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(col)), KeyEdge, b, 2, 2);
                int pad = k;
                if (c < steps) Area(b, () => P.ToggleStep(pad, step));
            }
        }
    }
}

/// DS-T TIMELINE: the song as blocks in a row (width = bars x repeats), the one playing with its
/// progress, the one queued; transport (play the song, loop a block, stop, REC overdub, bounce to
/// STUDIO) and the selected entry's edit keys (bars, repeats, move, new, duplicate, repeat, remove).
public sealed class SongModule : KeyedModule
{
    public SongModule() { Title = "DS-T  TIMELINE · MÚSICA"; }

    static readonly uint[] Colours = [0xFF2E6B8Fu, 0xFF8F5A2Eu, 0xFF4E8F2Eu, 0xFF7A2E8Fu, 0xFF8F2E45u, 0xFF2E8F7Fu, 0xFF8F842Eu, 0xFF4A4F8Fu];

    protected override void Draw(DrawingContext ctx, Rect r)
    {
        var s = P.S;
        P.Selected = Math.Clamp(P.Selected, 0, s.Order.Count - 1);
        bool blink = Environment.TickCount64 / 250 % 2 == 0;
        Key(ctx, new Rect(r.X, r.Y, 96, 24), "▶ MÚSICA", () => P.Play(loop: false), P.Playing && !P.Looping, Ui.Green);
        Key(ctx, new Rect(r.X + 100, r.Y, 84, 24), "⟲ LOOP", () => P.Play(loop: true), P.Playing && P.Looping, Ui.Green);
        Key(ctx, new Rect(r.X + 188, r.Y, 40, 24), "■", P.Stop, false, withLed: false);
        Key(ctx, new Rect(r.X + 232, r.Y, 74, 24), "● REC", P.Record, P.Armed && (P.Recording || blink), Ui.Red);
        Key(ctx, new Rect(r.X + 310, r.Y, 150, 24), "BOUNCE → STUDIO", () => Bounced?.Invoke(), false, withLed: false);
        string where = P.Starting ? "começa no próximo compasso…"
            : P.Playing && P.Entry >= 0 && P.Entry < s.Order.Count
                ? $"{s.Blocks[s.Order[P.Entry].Block].Name} · compasso {P.Bar + 1}/{s.Blocks[s.Order[P.Entry].Block].Bars} · vez {P.Repeat + 1}/{s.Order[P.Entry].Repeats}"
                  + (P.Queued >= 0 && P.Queued < s.Order.Count ? $" · próximo compasso: {s.Blocks[s.Order[P.Queued].Block].Name}" : "") + (P.Recording ? " · GRAVANDO" : "")
                : "parado";
        Ui.Text(ctx, where, r.X + 472, r.Y + 5, 11, P.Recording ? Ui.Red : Ui.LcdText, Ui.Mono);

        // The lane.
        var lane = new Rect(r.X, r.Y + 32, r.Width, Math.Max(36, r.Height - 32 - 32));
        ctx.DrawRectangle(Ui.Lcd, Ui.LcdEdge, lane, 3, 3);
        int totalBars = s.Order.Sum(e => s.Blocks[e.Block].Bars * e.Repeats);
        double px = Math.Min(48, (lane.Width - 8) / Math.Max(1, totalBars)), x = lane.X + 4;
        for (int i = 0; i < s.Order.Count; i++)
        {
            var e = s.Order[i];
            var b = s.Blocks[e.Block];
            var box = new Rect(x, lane.Y + 4, Math.Max(24, b.Bars * e.Repeats * px) - 3, lane.Height - 8);
            ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(Colours[e.Block % Colours.Length])), null, box, 4, 4);
            for (int k = 1; k < e.Repeats; k++)   // the repeats
                ctx.DrawLine(new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0x88000000)), 1.5, new ImmutableDashStyle([3, 3], 0)),
                    new Point(box.X + box.Width * k / e.Repeats, box.Y + 2), new Point(box.X + box.Width * k / e.Repeats, box.Bottom - 2));
            if (P.Playing && P.Entry == i)
            {
                double prog = ((P.Looping ? 0 : P.Repeat) * b.Bars + P.Bar + Math.Max(0, P.Step) / 16.0) / (b.Bars * (P.Looping ? 1 : e.Repeats));
                ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0x40FFFFFF)), null, new Rect(box.X, box.Y, box.Width * Math.Clamp(prog, 0, 1), box.Height), 4, 4);
                ctx.DrawLine(new ImmutablePen(P.Recording ? (IImmutableBrush)Ui.Red : (IImmutableBrush)Ui.Amber, 2), new Point(box.X + box.Width * prog, box.Y), new Point(box.X + box.Width * prog, box.Bottom));
            }
            if (i == P.Selected) ctx.DrawRectangle(null, new ImmutablePen((IImmutableBrush)Ui.Amber, 2), box.Inflate(1.5), 5, 5);
            if (P.Queued == i && blink) ctx.DrawRectangle(null, new ImmutablePen((IImmutableBrush)Ui.Green, 2, new ImmutableDashStyle([3, 2], 0)), box.Inflate(3.5), 6, 6);
            Ui.Text(ctx, b.Name, box.X + 6, box.Y + 3, 14, Ui.LabelBright, Ui.SansBold);
            if (box.Width > 50) Ui.Text(ctx, $"{b.Bars}c{(e.Repeats > 1 ? $" ×{e.Repeats}" : "")}", box.X + 6, box.Bottom - 15, 9, Ui.LabelBright, Ui.Mono);
            if (b.HasLoop) Ui.Text(ctx, "◉", box.Right - 14, box.Y + 3, 10, Ui.Red, Ui.SansBold);
            int idx = i;
            Area(box, () => { P.Selected = idx; P.Page = 0; });
            x += box.Width + 3;
        }

        // The selected entry's keys.
        double ky = lane.Bottom + 6, kx = r.X;
        var sel = P.SelBlock;
        void K(string label, Action a, double w) { Key(ctx, new Rect(kx, ky, w, 22), label, a, withLed: false); kx += w + 4; }
        Ui.Text(ctx, $"BLOCO {sel.Name}", kx, ky + 4, 10, Ui.Amber, Ui.SansBold);
        kx += 66;
        K("−", () => P.Bars(-1), 24);
        Ui.Text(ctx, $"{sel.Bars} comp.", kx + 30, ky + 4, 10, Ui.LabelBright, Ui.Mono, Ui.Align.Center);
        kx += 62;
        K("+", () => P.Bars(+1), 24);
        K("−", () => P.Repeats(-1), 24);
        Ui.Text(ctx, $"×{P.SelEntry.Repeats}", kx + 18, ky + 4, 10, Ui.LabelBright, Ui.Mono, Ui.Align.Center);
        kx += 38;
        K("+", () => P.Repeats(+1), 24);
        kx += 8;
        K("◀", () => P.Move(-1), 24);
        K("▶", () => P.Move(+1), 24);
        kx += 8;
        K("+ BLOCO", () => P.NewBlock(), 76);
        K("DUPLICAR", P.Duplicate, 80);
        K("REPETIR", P.RepeatBlock, 72);
        K("✕", P.RemoveEntry, 28);
        if (sel.HasLoop) K("APAGAR LOOP", P.ClearLoop, 100);
        if (P.Status.Length > 0 && kx + 20 < r.Right) Ui.Text(ctx, P.Status, kx + 8, ky + 5, 9, Ui.Amber, Ui.Sans);
    }

    /// BOUNCE → STUDIO (the window renders the timeline and opens it in STUDIO).
    public event Action? Bounced;
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

/// DS-B PEDALEIRA: stompboxes on the floor, patched with cables (ropes, as STUDIO's lunchbox):
/// ENTRADA (the input) and PADS on the left, SAÍDA (the LIVE output) and GRAVAR (what REC puts in
/// a loop) on the right, each pedal's IN / OUT on its top. Pick a plug out of its jack and carry it to
/// another; a new cable comes from an empty jack; Delete or a right click on a cable removes it.
/// A pedal's footswitch turns it on / off; a right click on its name takes it off the board.
public sealed class PedalboardModule : KeyedModule
{
    public PedalboardModule() { Title = "DS-B  PEDALEIRA"; }

    readonly Dictionary<string, Point> _jacks = [];
    readonly Dictionary<CableLink, Rope> _ropes = new(ReferenceEqualityComparer.Instance);
    readonly List<(Rope Rope, string Fixed, bool FixedIsOut, int Colour)> _loose = [];
    (Rope Rope, string Fixed, bool FixedIsOut, int Colour)? _cable;
    readonly List<(Rect Name, PedalDef P)> _names = [];
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    double _lastT, _floor;
    Point _hand;
    Rope? _hover;

    static bool IsOut(string jack) => jack.EndsWith(":out");

    float Lv(string node) => node switch
    {
        "in" => P.InputDb,
        "pads" => P.PeakDb,
        _ => P.S.Pedals.FindIndex(p => p.Id == node) is int k and >= 0 && k < 16 ? P.PedalDb[k] : -120,
    };

    protected override void Draw(DrawingContext ctx, Rect r)
    {
        var s = P.S;
        double now = _clock.Elapsed.TotalSeconds, dt = Math.Clamp(now - _lastT, 0.001, 0.04);
        _lastT = now;
        _floor = r.Bottom + 4;
        _jacks.Clear();
        _names.Clear();
        // The board: a dark floor with the io panels at its ends.
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF141517)), Ui.FaceEdge, new Rect(r.X, r.Y, r.Width, r.Height - 26), 4, 4);
        void Io(double x, string label, string jack, bool lit, float db)
        {
            Ui.Text(ctx, label, x, r.Y + 6, 8, Ui.LabelBright, Ui.SansBold, Ui.Align.Center);
            _jacks[jack] = new Point(x, r.Y + 30);
            Ui.Led(ctx, new Point(x, r.Y + 48), 3, db > -50, db > -3 ? Ui.Red : Ui.Green, Ui.GreenOff);
            if (lit) Ui.Text(ctx, "normal", x, r.Y + 56, 7, Ui.LcdDim, Ui.Mono, Ui.Align.Center);
        }
        Io(r.X + 30, "ENTRADA", "in:out", false, P.InputDb);
        _jacks["pads:out"] = new Point(r.X + 30, r.Y + 90);
        Ui.Text(ctx, "PADS", r.X + 30, r.Y + 66, 8, Ui.LabelBright, Ui.SansBold, Ui.Align.Center);
        bool padsNormal = !s.PedalCables.Any(k => k.From == "pads:out"), recNormal = !s.PedalCables.Any(k => k.To == "rec:in");
        if (padsNormal) Ui.Text(ctx, "→ saída", r.X + 30, r.Y + 104, 7, Ui.LcdDim, Ui.Mono, Ui.Align.Center);
        Io(r.Right - 30, "SAÍDA", "out:in", false, P.OutputDb);
        _jacks["rec:in"] = new Point(r.Right - 30, r.Y + 90);
        Ui.Text(ctx, "GRAVAR", r.Right - 30, r.Y + 66, 8, P.Armed ? Ui.Red : Ui.LabelBright, Ui.SansBold, Ui.Align.Center);
        if (recNormal) Ui.Text(ctx, "entrada seca", r.Right - 30, r.Y + 104, 7, Ui.LcdDim, Ui.Mono, Ui.Align.Center);

        // The pedals, left to right.
        double pw = 118, ph = Math.Min(r.Height - 40, 190), x = r.X + 70;
        for (int i = 0; i < s.Pedals.Count && x + pw < r.Right - 66; i++, x += pw + 10)
        {
            var p = s.Pedals[i];
            var box = new Rect(x, r.Y + 6, pw, ph);
            var col = Color.FromUInt32(PedalDef.Colours[p.Kind]);
            ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF060707)), null, box.Translate(new Vector(0, 3)), 8, 8);
            ctx.DrawRectangle(new ImmutableLinearGradientBrush([new ImmutableGradientStop(0, col), new ImmutableGradientStop(1, Color.FromArgb(255, (byte)(col.R * 0.6), (byte)(col.G * 0.6), (byte)(col.B * 0.6)))],
                startPoint: new RelativePoint(0, 0, RelativeUnit.Relative), endPoint: new RelativePoint(0, 1, RelativeUnit.Relative)), KeyEdge, box, 8, 8);
            var name = new Rect(box.X, box.Y + 22, box.Width, 18);
            Ui.Text(ctx, PedalDef.Names[p.Kind], box.Center.X, name.Y, 13, Ui.LabelBright, Ui.SansBold, Ui.Align.Center);
            _names.Add((name, p));
            int k = i;
            float lv = k < 16 ? P.PedalDb[k] : -120;
            for (int j = 0; j < 4; j++)
            {
                if (PedalDef.Labels[p.Kind][j].Length == 0) continue;
                int kj = j;
                Knob(ctx, new Point(box.X + 32 + (j % 2) * 54, box.Y + 62 + (j / 2) * 46), 12, PedalDef.Labels[p.Kind][j], p.Value(j), p.Knobs[j],
                    n => { p.Knobs[kj] = (float)Math.Round(n, 3); P.PublishBoard(); });
            }
            // Footswitch and its LED.
            var fs = new Point(box.Center.X, box.Bottom - 22);
            Ui.Led(ctx, new Point(box.Center.X, box.Bottom - 44), 4, p.On, Ui.Red, Ui.RedOff);
            ctx.DrawEllipse(new ImmutableSolidColorBrush(Color.FromUInt32(0xFFB9BEC5)), new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF3A3E44)), 2), fs, 11, 11);
            ctx.DrawEllipse(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF7A7F86)), null, fs, 6, 6);
            Area(new Rect(fs.X - 14, fs.Y - 14, 28, 28), () => { p.On = !p.On; P.BoardChanged(); });
            for (int m = 0; m < 5; m++) Ui.Led(ctx, new Point(box.X + 14 + m * 7, box.Bottom - 8), 2, lv > -48 + m * 10, m > 3 ? Ui.Red : Ui.Green, Ui.GreenOff);
            _jacks[p.Id + ":in"] = new Point(box.X + 12, box.Y + 10);
            _jacks[p.Id + ":out"] = new Point(box.Right - 12, box.Y + 10);
            Ui.Text(ctx, "IN", box.X + 22, box.Y + 4, 7, Ui.LabelBright, Ui.SansBold);
            Ui.Text(ctx, "OUT", box.Right - 22, box.Y + 4, 7, Ui.LabelBright, Ui.SansBold, Ui.Align.Right);
        }
        foreach (var j in _jacks.Values)
        {
            ctx.DrawEllipse(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF0B0C0D)), new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF8A9099)), 1.5), j, 6.5, 6.5);
            ctx.DrawEllipse(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF000000)), null, j, 3, 3);
        }

        // Cables.
        foreach (var c in s.PedalCables.ToList())
        {
            if (c.Colour < 0) c.Colour = NextColour();
            if (!_jacks.TryGetValue(c.From, out var a) || !_jacks.TryGetValue(c.To, out var e)) continue;
            if (!_ropes.TryGetValue(c, out var rope)) _ropes[c] = rope = new Rope(a, e);
            rope.Step(dt, a, e, _floor);
            if (ReferenceEquals(rope, _hover)) Cables.Halo(ctx, rope);
            Cables.Draw(ctx, rope, Cables.Colours[c.Colour % Cables.Colours.Length], Lv(c.From[..c.From.IndexOf(':')]), now);
        }
        foreach (var c in _ropes.Keys.Where(c => !s.PedalCables.Contains(c)).ToList()) _ropes.Remove(c);
        foreach (var (rope, fix, _, col) in _loose)
            if (_jacks.TryGetValue(fix, out var a)) { rope.Step(dt, a, null, _floor); if (ReferenceEquals(rope, _hover)) Cables.Halo(ctx, rope); Cables.Draw(ctx, rope, Cables.Colours[col % Cables.Colours.Length], -90, now); }
        _loose.RemoveAll(l => !_jacks.ContainsKey(l.Fixed));
        if (_cable is { } held && _jacks.TryGetValue(held.Fixed, out var fa))
        {
            held.Rope.Step(dt, fa, _hand, _floor);
            Cables.Draw(ctx, held.Rope, Cables.Colours[held.Colour % Cables.Colours.Length], -90, now);
            if (NearJack(_hand, !held.FixedIsOut) is { } target) ctx.DrawEllipse(null, new ImmutablePen((IImmutableBrush)Ui.Green, 2), _jacks[target], 10, 10);
        }

        // The drawer.
        double dx = r.X, dy = r.Bottom - 22;
        Ui.Text(ctx, "GAVETA", dx, dy + 4, 8, Ui.Label, Ui.SansBold);
        dx += 52;
        for (int kind = 0; kind < PedalDef.Names.Length; kind++)
        {
            int kk = kind;
            Key(ctx, new Rect(dx, dy, 84, 20), "+ " + PedalDef.Names[kind], () => { if (P.S.Pedals.Count < 16) P.AddPedal(kk); }, withLed: false);
            dx += 88;
        }
        Ui.Text(ctx, "plugue ↔ jack · Delete / botão direito no cabo apaga · direito no nome tira o pedal", dx + 8, dy + 5, 8, Ui.Label, Ui.Sans);
    }

    int NextColour()
    {
        for (int i = 0; i < Cables.Colours.Length; i++) if (!P.S.PedalCables.Any(k => k.Colour == i)) return i;
        return P.S.PedalCables.Count;
    }

    string? NearJack(Point p, bool wantOut)
    {
        foreach (var (id, j) in _jacks)
            if (IsOut(id) == wantOut && (p - j).X * (p - j).X + (p - j).Y * (p - j).Y <= 14 * 14 && P.S.PedalCables.Count(k => k.From == id || k.To == id) < 4) return id;
        return null;
    }

    Rope? RopeAt(Point p)
    {
        Rope? best = null;
        double bd = 8 * 8;
        foreach (var rope in _ropes.Values.Concat(_loose.Select(l => l.Rope)))
            foreach (var q in rope.P)
                if ((p - q).X * (p - q).X + (p - q).Y * (p - q).Y is double d && d < bd) (bd, best) = (d, rope);
        return best;
    }

    /// Delete / Backspace: the cable in the hand, or the one under the mouse, off the board.
    public bool DeleteCable()
    {
        if (_cable is not null) { _cable = null; return true; }
        if (_hover is not { } rope) return false;
        _hover = null;
        _loose.RemoveAll(l => ReferenceEquals(l.Rope, rope));
        if (_ropes.FirstOrDefault(kv => ReferenceEquals(kv.Value, rope)).Key is { } k) { P.S.PedalCables.Remove(k); _ropes.Remove(k); P.BoardChanged(); }
        return true;
    }

    protected override void Pressed(PointerPressedEventArgs e, Point p)
    {
        var s = P.S;
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            foreach (var (name, pd) in _names) if (name.Contains(p)) { P.RemovePedal(pd); return; }
            _hover = RopeAt(p);
            DeleteCable();
            return;
        }
        foreach (var (id, j) in _jacks)
        {
            if ((p - j).X * (p - j).X + (p - j).Y * (p - j).Y > 11 * 11) continue;
            var k = s.PedalCables.LastOrDefault(x => x.From == id || x.To == id);
            if (k is not null)
            {
                bool heldIsTo = k.To == id;
                var rope = _ropes.GetValueOrDefault(k) ?? new Rope(_jacks[k.From], _jacks[k.To]);
                if (!heldIsTo) rope.Reverse();
                s.PedalCables.Remove(k);
                _cable = (rope, heldIsTo ? k.From : k.To, heldIsTo, k.Colour);
                P.BoardChanged();
            }
            else _cable = (new Rope(j, p), id, IsOut(id), NextColour());
            _hand = p;
            e.Pointer.Capture(this);
            return;
        }
        for (int i = _loose.Count - 1; i >= 0; i--)
            if ((p - _loose[i].Rope.B).X * (p - _loose[i].Rope.B).X + (p - _loose[i].Rope.B).Y * (p - _loose[i].Rope.B).Y < 12 * 12)
            {
                _cable = _loose[i];
                _loose.RemoveAt(i);
                _hand = p;
                e.Pointer.Capture(this);
                return;
            }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_cable is null)
        {
            _hover = RopeAt(e.GetPosition(this));
            base.OnPointerMoved(e);
            return;
        }
        _hand = e.GetPosition(this);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_cable is not { } held) { base.OnPointerReleased(e); return; }
        _cable = null;
        _hand = e.GetPosition(this);
        e.Pointer.Capture(null);
        if (NearJack(_hand, !held.FixedIsOut) is { } to && to[..to.IndexOf(':')] != held.Fixed[..held.Fixed.IndexOf(':')])
        {
            var k = held.FixedIsOut ? new CableLink { From = held.Fixed, To = to, Colour = held.Colour } : new CableLink { From = to, To = held.Fixed, Colour = held.Colour };
            if (!held.FixedIsOut) held.Rope.Reverse();
            P.S.PedalCables.Add(k);
            _ropes[k] = held.Rope;
            P.BoardChanged();
        }
        else _loose.Add(held);
    }
}
