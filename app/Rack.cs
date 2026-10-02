using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Dissonancia;

/// Palette and drawing helpers for the studio-rack look (spec §22.3).
static class Ui
{
    static IBrush B(uint argb) => new ImmutableSolidColorBrush(Color.FromUInt32(argb));
    static IPen P(uint argb, double w) => new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(argb)), w);

    public static readonly IBrush RackBg = B(0xFF111214);
    public static readonly IBrush Face = new ImmutableLinearGradientBrush(
        [new ImmutableGradientStop(0, Color.FromUInt32(0xFF2E3136)), new ImmutableGradientStop(1, Color.FromUInt32(0xFF222428))],
        startPoint: new RelativePoint(0, 0, RelativeUnit.Relative), endPoint: new RelativePoint(0, 1, RelativeUnit.Relative));
    public static readonly IPen FaceEdge = P(0xFF3C4046, 1);
    public static readonly IBrush Screw = B(0xFF6B7078);
    public static readonly IPen ScrewSlot = P(0xFF2A2C30, 1.2);
    public static readonly IBrush Label = B(0xFF8E959E);
    public static readonly IBrush LabelBright = B(0xFFC9CED6);

    public static readonly IBrush Lcd = B(0xFF07130D);
    public static readonly IPen LcdEdge = P(0xFF1B2A22, 2);
    public static readonly IBrush LcdText = B(0xFF9DFFC8);
    public static readonly IBrush LcdDim = B(0xFF3E6B53);
    public static readonly IBrush LcdGhost = B(0xFF12241A);
    public static readonly IPen LcdLine = P(0xFF9DFFC8, 1.2);
    public static readonly IPen LcdLineDim = P(0xFF2C4A3A, 1);

    public static readonly IBrush Green = B(0xFF3DDC84);
    public static readonly IBrush GreenOff = B(0xFF15301F);
    public static readonly IBrush Amber = B(0xFFFFB000);
    public static readonly IBrush AmberOff = B(0xFF3A2A06);
    public static readonly IBrush Red = B(0xFFFF3B30);
    public static readonly IBrush RedOff = B(0xFF3A1210);
    public static readonly IBrush RedGlow = B(0x66FF3B30);

    public static readonly IBrush VuFace = new ImmutableLinearGradientBrush(
        [new ImmutableGradientStop(0, Color.FromUInt32(0xFFF6E7BE)), new ImmutableGradientStop(1, Color.FromUInt32(0xFFE3C98A))],
        startPoint: new RelativePoint(0, 0, RelativeUnit.Relative), endPoint: new RelativePoint(0, 1, RelativeUnit.Relative));
    public static readonly IPen VuInk = P(0xFF2B2418, 1.2);
    public static readonly IPen VuRed = P(0xFFC62828, 3);
    public static readonly IPen VuNeedle = P(0xFF111111, 1.6);
    public static readonly IBrush VuInkBrush = B(0xFF2B2418);

    // VU glass: sheen (curved reflection), top inner shadow, edge vignette, warm backlight, needle shadow.
    static ImmutableGradientStop S(double o, uint argb) => new(o, Color.FromUInt32(argb));
    static RelativePoint Rel(double x, double y) => new(x, y, RelativeUnit.Relative);
    public static readonly IBrush GlassSheen = new ImmutableLinearGradientBrush(
        [S(0, 0x00FFFFFF), S(0.74, 0x00FFFFFF), S(0.76, 0x60FFFFFF), S(1, 0x0CFFFFFF)], startPoint: Rel(0, 0), endPoint: Rel(0, 1));
    public static readonly IBrush GlassShadow = new ImmutableLinearGradientBrush(
        [S(0, 0x70000000), S(0.2, 0x00000000)], startPoint: Rel(0, 0), endPoint: Rel(0, 1));
    public static readonly IBrush GlassVignette = new ImmutableRadialGradientBrush(
        [S(0, 0x00000000), S(0.72, 0x00000000), S(1, 0x55000000)], center: Rel(0.5, 0.55), gradientOrigin: Rel(0.5, 0.55),
        radiusX: new RelativeScalar(0.75, RelativeUnit.Relative), radiusY: new RelativeScalar(0.9, RelativeUnit.Relative));
    public static readonly IBrush Backlight = new ImmutableRadialGradientBrush(
        [S(0, 0x55FFD890), S(1, 0x00FFD890)], center: Rel(0.5, 1), gradientOrigin: Rel(0.5, 1),
        radiusX: new RelativeScalar(0.7, RelativeUnit.Relative), radiusY: new RelativeScalar(0.9, RelativeUnit.Relative));
    public static readonly IBrush GlassStreak = B(0x16FFFFFF);
    public static readonly IPen NeedleShadow = P(0x38000000, 2.4);
    public static readonly IPen BezelOuter = P(0xFF0B0C0E, 4);
    public static readonly IPen BezelHighlight = P(0x40FFFFFF, 1);

    public static readonly IBrush Scope = B(0xFF050E09);
    public static readonly IPen ScopeGrid = P(0xFF10261A, 1);
    public static readonly IPen ScopeTrace = P(0xFF7CFFB2, 1.2);
    public static readonly IPen ScopeGlow = P(0x337CFFB2, 4);
    public static readonly IPen BeatMark = P(0x66FFB000, 1);   // tempo heard: beat marks on the scope
    public static readonly IBrush ScopeFill = B(0x557CFFB2);

    public static readonly IBrush SevenSeg = B(0xFFFF5A3C);
    public static readonly IBrush SevenSegGhost = B(0xFF2A0E0A);
    public static readonly IBrush SevenSegBg = B(0xFF0C0605);

    public static readonly Typeface Sans = new("Inter, Segoe UI, DejaVu Sans, sans-serif");
    public static readonly Typeface SansBold = new("Inter, Segoe UI, DejaVu Sans, sans-serif", FontStyle.Normal, FontWeight.Bold);
    public static readonly Typeface Mono = new("Cascadia Mono, Consolas, DejaVu Sans Mono, monospace", FontStyle.Normal, FontWeight.Bold);
    public static readonly Typeface Music = new("Noto Music, Segoe UI Symbol, FreeSerif, serif");

    public enum Align { Left, Center, Right }

    // ponytail: FormattedText per draw allocates; the back-end phase caches text per value change (spec §22.7).
    public static Size Text(DrawingContext ctx, string s, double x, double y, double size, IBrush brush,
        Typeface? tf = null, Align align = Align.Left)
    {
        var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf ?? Sans, size, brush);
        double dx = align switch { Align.Center => -ft.Width / 2, Align.Right => -ft.Width, _ => 0 };
        ctx.DrawText(ft, new Point(x + dx, y));
        return new Size(ft.Width, ft.Height);
    }

    public static void Led(DrawingContext ctx, Point c, double r, bool on, IBrush onBrush, IBrush offBrush)
    {
        if (on) ctx.DrawEllipse(new ImmutableSolidColorBrush(((ISolidColorBrush)onBrush).Color, 0.25), null, c, r * 2.2, r * 2.2);
        ctx.DrawEllipse(on ? onBrush : offBrush, null, c, r, r);
    }

    public static string Ms(float ms) => ms > 0 ? $"{ms:0} ms" : "— ms";
}

/// A rack unit: faceplate, rack screws and a title; subclasses draw the content.
/// Every LIVE unit is one of these so the rack can be reordered later (spec §22.4).
public abstract class RackModule : Control
{
    public string Title { get; init; } = "";
    public LiveFrame? Frame { get; set; }
    public Session Session { get; set; } = new();

    public sealed override void Render(DrawingContext ctx)
    {
        var b = new Rect(Bounds.Size).Deflate(2);
        ctx.DrawRectangle(Ui.Face, Ui.FaceEdge, b, 4, 4);
        foreach (var p in new[] { b.TopLeft + new Point(9, 9), b.TopRight + new Point(-9, 9),
                                  b.BottomLeft + new Point(9, -9), b.BottomRight + new Point(-9, -9) })
        {
            ctx.DrawEllipse(Ui.Screw, null, p, 3.6, 3.6);
            ctx.DrawLine(Ui.ScrewSlot, p + new Point(-2.2, -2.2), p + new Point(2.2, 2.2));
        }
        if (Title.Length > 0) Ui.Text(ctx, Title, b.X + 22, b.Y + 4, 10, Ui.Label, Ui.SansBold);
        if (Frame is not null) DrawContent(ctx, new Rect(b.X + 20, b.Y + 20, b.Width - 40, b.Height - 28));
    }

    protected abstract void DrawContent(DrawingContext ctx, Rect r);
}

/// Analog VU needle + digital peak LED ladder.
public sealed class InputModule : RackModule
{
    public InputModule() { Title = "INPUT"; }

    static readonly float[] VuTicks = [-20, -10, -7, -5, -3, -2, -1, 0, 1, 2, 3];

    static double VuPos(float vu) // VU scale is linear in voltage
    {
        double lo = Math.Pow(10, -20 / 20.0), hi = Math.Pow(10, 3 / 20.0);
        return (Math.Pow(10, Math.Clamp(vu, -20, 3) / 20.0) - lo) / (hi - lo);
    }

    protected override void DrawContent(DrawingContext ctx, Rect r)
    {
        var f = Frame!;
        // VU face
        var face = new Rect(r.X, r.Y, Math.Min(230, r.Width * 0.55), r.Height);
        var faceClip = new RoundedRect(face, 6);
        ctx.DrawRectangle(Ui.VuFace, null, face, 6, 6);
        using (ctx.PushClip(faceClip)) ctx.DrawRectangle(Ui.Backlight, null, face);
        var pivot = new Point(face.Center.X, face.Bottom + face.Height * 0.35);
        double radius = face.Height * 1.05;
        const double a0 = -0.78, a1 = 0.78;       // radians from vertical
        Point OnArc(double pos, double rr) { double a = a0 + (a1 - a0) * pos; return pivot + new Point(Math.Sin(a) * rr, -Math.Cos(a) * rr); }

        var red = new StreamGeometry();
        using (var g = red.Open())
        {
            g.BeginFigure(OnArc(VuPos(0), radius * 0.82), false);
            for (int i = 1; i <= 12; i++) g.LineTo(OnArc(VuPos(0) + (1 - VuPos(0)) * i / 12.0, radius * 0.82));
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, Ui.VuRed, red);
        foreach (var t in VuTicks)
        {
            double p = VuPos(t);
            ctx.DrawLine(Ui.VuInk, OnArc(p, radius * 0.78), OnArc(p, radius * 0.86));
            if (t is -20 or -10 or -5 or -3 or 0 or 3 || t == -7)
            {
                var lp = OnArc(p, radius * 0.93);
                Ui.Text(ctx, t > 0 ? $"+{t}" : $"{t}", lp.X, lp.Y - 7, 9, t >= 0 ? Ui.Red : Ui.VuInkBrush, Ui.SansBold, Ui.Align.Center);
            }
        }
        Ui.Text(ctx, "VU", face.Center.X, face.Bottom - 26, 13, Ui.VuInkBrush, Ui.SansBold, Ui.Align.Center);
        using (ctx.PushClip(faceClip))
        {
            var tip = OnArc(VuPos(f.VuLevel), radius * 0.9);
            var shadow = new Point(2.5, 3.5);   // needle sits a few mm above the scale, under the glass
            ctx.DrawLine(Ui.NeedleShadow, pivot + shadow, tip + shadow);
            ctx.DrawLine(Ui.VuNeedle, pivot, tip);

            // Glass: inner shadow under the bezel, edge vignette, curved reflection, a thin streak.
            ctx.DrawRectangle(Ui.GlassShadow, null, face);
            ctx.DrawRectangle(Ui.GlassVignette, null, face);
            var sheen = new Rect(face.X - face.Width * 0.35, face.Y - face.Height * 1.6, face.Width * 1.7, face.Height * 2.1);
            ctx.DrawEllipse(Ui.GlassSheen, null, sheen.Center, sheen.Width / 2, sheen.Height / 2);
            var streak = new StreamGeometry();
            using (var g = streak.Open())
            {
                g.BeginFigure(new Point(face.X + face.Width * 0.62, face.Y), true);
                g.LineTo(new Point(face.X + face.Width * 0.70, face.Y));
                g.LineTo(new Point(face.X + face.Width * 0.46, face.Bottom));
                g.LineTo(new Point(face.X + face.Width * 0.40, face.Bottom));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(Ui.GlassStreak, null, streak);
        }
        ctx.DrawRectangle(null, Ui.BezelOuter, face.Inflate(1.5), 7, 7);
        ctx.DrawRectangle(null, Ui.BezelHighlight, face.Deflate(0.5), 6, 6);

        // Peak LED ladder (dBFS), target zone -18..-6
        double x0 = face.Right + 18, w = r.Right - x0, y = r.Y + 14;
        const int n = 24; const float lo = -48, hi = 0;
        double seg = w / n;
        for (int i = 0; i < n; i++)
        {
            float db = lo + (hi - lo) * (i + 0.5f) / n;
            bool on = f.PeakDbfs >= db;
            bool hold = Math.Abs(f.PeakHoldDbfs - db) < (hi - lo) / n / 2;
            var (onB, offB) = db < -18 ? (Ui.Green, Ui.GreenOff) : db < -6 ? (Ui.Amber, Ui.AmberOff) : (Ui.Red, Ui.RedOff);
            ctx.DrawRectangle(on || hold ? onB : offB, null, new Rect(x0 + i * seg + 1, y, seg - 2, 16), 1, 1);
        }
        double tz0 = x0 + w * (-18 - lo) / (hi - lo), tz1 = x0 + w * (-6 - lo) / (hi - lo);
        ctx.DrawLine(Ui.LcdLineDim, new Point(tz0, y + 22), new Point(tz1, y + 22));
        Ui.Text(ctx, "target", (tz0 + tz1) / 2, y + 24, 9, Ui.Label, null, Ui.Align.Center);
        foreach (var db in new[] { -48, -36, -24, -18, -12, -6, 0 })
            Ui.Text(ctx, $"{db}", x0 + w * (db - lo) / (hi - lo), y - 13, 9, Ui.Label, null, Ui.Align.Center);

        Ui.Text(ctx, "PEAK", x0, y + 44, 10, Ui.Label, Ui.SansBold);
        Ui.Text(ctx, $"{f.PeakDbfs:0.0} dBFS", x0 + 40, y + 40, 16, Ui.LabelBright, Ui.Mono);
        Ui.Text(ctx, $"hold {f.PeakHoldDbfs:0.0}", x0 + 40, y + 62, 11, Ui.Label, Ui.Mono);
        Ui.Led(ctx, new Point(r.Right - 30, y + 52), 6, f.ClipLatched, Ui.Red, Ui.RedOff);
        Ui.Text(ctx, "CLIP", r.Right - 30, y + 62, 9, Ui.Label, Ui.SansBold, Ui.Align.Center);
        Ui.Text(ctx, $"VU {f.VuLevel:+0.0;-0.0}", x0, r.Bottom - 16, 11, Ui.Label, Ui.Mono);
    }
}

/// Live waveform line: min/max envelope, scrolling, phosphor look.
/// Scrolling min/max envelope (~5 s). Mono modes: click toggles a triggered oscilloscope that uses
/// the detected period, so the waveform stands still while a note is held (spec §22.3).
public sealed class ScopeModule : RackModule
{
    bool _triggered;
    public ScopeModule() { Title = "SCOPE  ·  click: envelope / triggered"; }

    protected override void OnPointerPressed(PointerPressedEventArgs e) => ToggleTrigger();
    public void ToggleTrigger() { _triggered = !_triggered; InvalidateVisual(); }

    protected override void DrawContent(DrawingContext ctx, Rect r)
    {
        var f = Frame!;
        ctx.DrawRectangle(Ui.Scope, Ui.LcdEdge, r, 3, 3);
        for (int i = 1; i < 10; i++) ctx.DrawLine(Ui.ScopeGrid, new Point(r.X + r.Width * i / 10, r.Y), new Point(r.X + r.Width * i / 10, r.Bottom));
        for (int i = 1; i < 4; i++) ctx.DrawLine(Ui.ScopeGrid, new Point(r.X, r.Y + r.Height * i / 4), new Point(r.Right, r.Y + r.Height * i / 4));
        if (_triggered && !Session.IsChordMode) DrawTriggered(ctx, r, f); else DrawEnvelope(ctx, r, f);
    }

    static void DrawEnvelope(DrawingContext ctx, Rect r, LiveFrame f)
    {
        int cols = (int)r.Width;
        double mid = r.Center.Y, amp = r.Height * 0.45;
        var env = new StreamGeometry();
        using (var g = env.Open())
        {
            for (int pass = 0; pass < 2; pass++)
                for (int k = 0; k < cols; k++)
                {
                    int x = pass == 0 ? k : cols - 1 - k;
                    int idx = (f.WaveWriteIndex + x * LiveFrame.WaveColumns / cols) % LiveFrame.WaveColumns;
                    double v = pass == 0 ? f.WaveMax[idx] : f.WaveMin[idx];
                    var p = new Point(r.X + x, mid - Math.Clamp(v * 1.4, -1, 1) * amp);
                    if (k == 0 && pass == 0) g.BeginFigure(p, true); else g.LineTo(p);
                }
            g.EndFigure(true);
        }
        ctx.DrawGeometry(Ui.ScopeFill, Ui.ScopeGlow, env);
        ctx.DrawGeometry(null, Ui.ScopeTrace, env);
        double span = LiveFrame.WaveColumns * f.WaveColumnSeconds;
        // Beat marks of the tempo heard (display only), extrapolated from the latest beat to "now".
        if (f.DetectedBpm > 0 && span > 0)
        {
            double period = 60 / f.DetectedBpm, now = f.AnalyzedSeconds;
            double first = f.LastBeatSeconds + Math.Floor((now - f.LastBeatSeconds) / period) * period;   // newest beat <= now
            for (double tb = first; tb > now - span; tb -= period)
            {
                double x = r.Right - (now - tb) / span * r.Width;
                ctx.DrawLine(Ui.BeatMark, new Point(x, r.Y + 2), new Point(x, r.Bottom - 2));
            }
        }
        Ui.Text(ctx, $"{span:0.0} s", r.X + 6, r.Bottom - 16, 10, Ui.LcdDim, Ui.Mono);
        Ui.Text(ctx, "now", r.Right - 6, r.Bottom - 16, 10, Ui.LcdDim, Ui.Mono, Ui.Align.Right);
    }

    // Three periods of the detected pitch, starting at the newest rising zero crossing that leaves
    // room for them; free-running (newest samples) while unvoiced.
    static void DrawTriggered(DrawingContext ctx, Rect r, LiveFrame f)
    {
        var x = f.Scope;
        int n = x.Length;
        double period = f.NoteValid && f.Hz > 0 ? f.SampleRate / f.Hz : 0;
        int window = period > 0 ? (int)Math.Min(n - 1, 3 * period) : n / 4;
        int start = n - window;
        if (period > 0)
            for (int i = n - window - 1; i > Math.Max(1, n - window - 2 * (int)period); i--)
                if (x[i - 1] < 0 && x[i] >= 0) { start = i; break; }
        double mid = r.Center.Y, amp = r.Height * 0.45, peak = 1e-4;
        for (int i = start; i < start + window; i++) peak = Math.Max(peak, Math.Abs(x[i]));
        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            for (int i = 0; i < window; i++)
            {
                var p = new Point(r.X + r.Width * i / (window - 1), mid - x[start + i] / peak * amp * 0.9);
                if (i == 0) g.BeginFigure(p, false); else g.LineTo(p);
            }
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, Ui.ScopeGlow, line);
        ctx.DrawGeometry(null, Ui.ScopeTrace, line);
        Ui.Text(ctx, period > 0 ? $"triggered · 3 periods · {window / f.SampleRate * 1000:0.0} ms" : "triggered · no pitch (free run)",
            r.X + 6, r.Bottom - 16, 10, Ui.LcdDim, Ui.Mono);
    }
}

/// Plasma display: chord line on top, monophonic note (or bass) below with a mini staff.
public sealed class LcdModule : RackModule
{
    StreamGeometry? _grid;
    Size _gridSize;

    public LcdModule() { Title = "ANALYZER"; }

    protected override void DrawContent(DrawingContext ctx, Rect r)
    {
        var f = Frame!;
        Plasma.Panel(ctx, r, ref _grid, ref _gridSize);
        var inner = r.Deflate(16);
        bool chords = Session.IsChordMode;
        double split = chords ? inner.Y + inner.Height * 0.54 : inner.Y;

        if (chords)
        {
            // Chord symbol: 7 dot-matrix cells, unlit dots visible like a real gas-plasma panel.
            double rx = inner.Right, status = 150;
            double dot = Math.Min((inner.Width - status - 10) / (7 * 6 * 1.3), (split - inner.Y - 62) / (7 * 1.3));
            // The confirmed chord stays on the panel; a candidate that has held 120 ms shows beside it.
            bool steady = f.DisplayChord.Length > 0 && f.DisplayChord != f.CandidateChord;
            Plasma.DotText(ctx, f.DisplayChord, inner.X, inner.Y, dot, cells: 7, dim: !steady);

            bool pending = f.CandidateChord.Length > 0;
            Plasma.Lamp(ctx, new Point(rx - 140, inner.Y + 8), 4.5, pending);
            Plasma.Text(ctx, "PROV", rx - 130, inner.Y + 1, 11, pending, Ui.SansBold);
            Plasma.Lamp(ctx, new Point(rx - 70, inner.Y + 8), 4.5, steady && !pending);
            Plasma.Text(ctx, "CONF", rx - 60, inner.Y + 1, 11, steady && !pending, Ui.SansBold);
            if (steady && pending) Plasma.Text(ctx, $"→ {f.CandidateChord}?", rx, inner.Y + 76, 15, true, Ui.Mono, Ui.Align.Right);

            for (int i = 0; i < 10; i++)
                Plasma.Lamp(ctx, new Point(rx - 136 + i * 13.5, inner.Y + 30), 3.6, i < f.ChordConfidence * 10);
            Plasma.Text(ctx, $"{f.ChordConfidence:0.00}", rx, inner.Y + 40, 12, true, Ui.Mono, Ui.Align.Right);

            string ms = f.ChordConfirmed ? Ui.Ms(f.ChordLatencyMs) : $"{Ui.Ms(f.ChordLatencyMs)} · +{f.ChordConfirmElapsedMs:0}";
            Plasma.Text(ctx, ms, rx, inner.Y + 58, 13, true, Ui.Mono, Ui.Align.Right);

            double textY = inner.Y + dot * 1.3 * 7 + 10;
            Plasma.Text(ctx, $"alt  {f.ChordAlternatives}", inner.X, textY, 13, false);
            if (f.ChordReason.Length > 0) Plasma.Text(ctx, f.ChordReason, inner.X, textY + 18, 12, false);
            if (f.DisplayRoman.Length > 0) Plasma.Text(ctx, f.DisplayRoman, rx, textY - 4, 18, steady, Ui.Mono, Ui.Align.Right);
            if (f.Cadence.Length > 0) Plasma.Text(ctx, f.Cadence, rx, textY + 18, 12, false, Ui.Mono, Ui.Align.Right);

            ctx.DrawLine(Plasma.LineDim, new Point(inner.X, split - 6), new Point(inner.Right, split - 6));
        }

        if (!chords) { DrawNoteScreen(ctx, inner, f); return; }

        // Lower line (chord modes): bass, spelled and clef-shifted.
        bool valid = f.BassValid;
        var written = f.Bass.WithOctaveShift(Session.Clef.OctaveShift());
        double y = split + 2, noteDot = 4.2;
        Plasma.Text(ctx, "BASS", inner.X, y, 10, false, Ui.SansBold);
        Plasma.DotText(ctx, valid ? written.Name : "", inner.X, y + 16, noteDot, cells: 4);
        double infoX = inner.X + Plasma.CellWidth(noteDot) * 4 + 14;
        Plasma.Lamp(ctx, new Point(infoX + 5, y + 26), 4.5, f.BassSettled);
        Plasma.Text(ctx, f.BassSettled ? "settled" : f.BassSettleRemainingMs > 0 ? $"settling {f.BassSettleRemainingMs:0} ms" : "settling",
            infoX + 18, y + 18, 13, f.BassSettled, Ui.Mono);
        Plasma.Text(ctx, valid ? $"{f.Bass.Hz():0.0} Hz" : "— Hz", infoX, y + 40, 13, false);
        if (Session.Clef == Clef.Treble8vb) Plasma.Text(ctx, "written 8vb", inner.X, y + 22 + noteDot * 1.3 * 7, 10, false);
        if (Session.GrandStaff) DrawGrandStaff(ctx, new Rect(inner.Right - 170, y + 2, 170, Math.Max(80, inner.Bottom - y - 4)), f.Bass, valid);
        else DrawStaff(ctx, new Rect(inner.Right - 160, y + 10, 160, 56), Session.Clef, written, valid);
    }

    /// Mono modes: the whole display is the note — big spelled name, staff, full-width cents meter.
    void DrawNoteScreen(DrawingContext ctx, Rect inner, LiveFrame f)
    {
        var written = f.Note.WithOctaveShift(Session.Clef.OctaveShift());
        double staffW = Math.Min(230, inner.Width * 0.38);
        double dot = Math.Min((inner.Width - staffW - 24) / (4 * 6 * 1.3), (inner.Height * 0.5) / (7 * 1.3));
        double noteH = dot * 1.3 * 7;
        Plasma.Text(ctx, "NOTE", inner.X, inner.Y, 10, false, Ui.SansBold);
        Plasma.DotText(ctx, f.NoteValid ? written.Name : "", inner.X, inner.Y + 16, dot, cells: 4);
        DrawStaff(ctx, new Rect(inner.Right - staffW, inner.Y + 10, staffW, noteH + 6), Session.Clef, written, f.NoteValid);

        double y = inner.Y + 16 + noteH + 22;
        var meter = new Rect(inner.X, y, inner.Width, 34);
        DrawCents(ctx, meter, f.Cents, f.NoteValid);
        foreach (var (c, t) in new[] { (-50, "-50"), (0, "0"), (50, "+50") })
            Plasma.Text(ctx, t, meter.X + meter.Width * (c + 50) / 100.0, meter.Bottom + 2, 10, false, Ui.Mono, Ui.Align.Center);

        double ty = meter.Bottom + 22;
        Plasma.Text(ctx, f.NoteValid ? $"{f.Cents:+0;-0} ¢" : "— ¢", inner.X, ty, 20);
        Plasma.Text(ctx, f.NoteValid ? $"{f.Hz:0.0} Hz" : "— Hz", inner.X + inner.Width * 0.36, ty, 20);
        Plasma.Text(ctx, Ui.Ms(f.NoteLatencyMs), inner.Right, ty, 20, true, Ui.Mono, Ui.Align.Right);
        if (Session.Clef == Clef.Treble8vb) Plasma.Text(ctx, "written 8vb", inner.X + inner.Width * 0.36, inner.Y, 10, false);
    }

    static void DrawCents(DrawingContext ctx, Rect r, float cents, bool valid)
    {
        ctx.DrawLine(Plasma.LineDim, new Point(r.X, r.Center.Y), new Point(r.Right, r.Center.Y));
        for (int c = -50; c <= 50; c += 10)
        {
            double x = r.X + r.Width * (c + 50) / 100.0;
            var a = new Point(x, r.Center.Y - (c == 0 ? 10 : 5));
            var b = new Point(x, r.Center.Y + (c == 0 ? 10 : 5));
            if (c == 0) Plasma.GlowLine(ctx, a, b); else ctx.DrawLine(Plasma.LineDim, a, b);
        }
        if (!valid) return;
        double nx = r.X + r.Width * (Math.Clamp(cents, -50, 50) + 50) / 100.0;
        var bar = new Rect(nx - 2.5, r.Y, 5, r.Height);
        ctx.DrawRectangle(Plasma.Halo, null, bar.Inflate(4), 3, 3);
        ctx.DrawRectangle(Math.Abs(cents) < 5 ? Plasma.Hot : Plasma.Lit, null, bar, 1.5, 1.5);   // in tune = white-hot
    }

    /// Piano: treble and bass staves joined by a brace line; the note goes on the bass staff below C4.
    void DrawGrandStaff(DrawingContext ctx, Rect r, Pitch sounding, bool valid)
    {
        // Each staff takes 6 gaps (lines + one gap of margin each side); 2 more gaps between them for C4 ledgers.
        double gap = r.Height / 14, h = 6 * gap;
        bool low = sounding.Midi < 60;
        var top = new Rect(r.X, r.Y, r.Width, h);
        var bottom = new Rect(r.X, r.Y + 8 * gap, r.Width, h);
        DrawStaff(ctx, top, Clef.Treble, sounding, valid && !low);
        DrawStaff(ctx, bottom, Clef.Bass, sounding, valid && low);
        Plasma.GlowLine(ctx, new Point(r.X, top.Bottom - 5 * gap), new Point(r.X, bottom.Bottom - gap));   // system line
    }

    void DrawStaff(DrawingContext ctx, Rect r, Clef clef, Pitch written, bool valid)
    {
        double gap = r.Height / 6, bottom = r.Bottom - gap;   // 5 lines, 4 spaces
        double Y(int pos) => bottom - pos * gap / 2;
        for (int i = 0; i < 5; i++) Plasma.GlowLine(ctx, new Point(r.X, Y(i * 2)), new Point(r.Right, Y(i * 2)));

        string glyph = clef switch { Clef.Bass => "𝄢", Clef.Alto or Clef.Tenor => "𝄡", _ => "𝄞" };
        double clefSize = gap * 4.2;
        Plasma.Glyph(ctx, glyph, r.X + 2, Y(clef is Clef.Bass ? 8 : clef is Clef.Alto ? 6 : clef is Clef.Tenor ? 8 : 6) - clefSize * 0.55, clefSize, Ui.Music);
        if (clef == Clef.Treble8vb) Plasma.Glyph(ctx, "8", r.X + 12, Y(-4), gap * 1.3, Ui.SansBold);

        // Key signature
        double kx = r.X + gap * 3.2;
        string acc = Session.Key.Fifths >= 0 ? "♯" : "♭";
        foreach (var pos in clef.KeySignaturePositions(Session.Key.Fifths))
        {
            Plasma.Glyph(ctx, acc, kx, Y(pos) - gap * 1.25, gap * 2, Ui.Music);
            kx += gap * 0.95;
        }
        if (!valid) return;

        // Note head, accidental, ledger lines
        int notePos = written.Step - clef.BottomLineStep();
        double nx = Math.Max(kx + gap * 2.4, r.X + r.Width * 0.72);
        for (int p = -2; p >= notePos; p -= 2) Plasma.GlowLine(ctx, new Point(nx - gap * 1.1, Y(p)), new Point(nx + gap * 1.1, Y(p)));
        for (int p = 10; p <= notePos; p += 2) Plasma.GlowLine(ctx, new Point(nx - gap * 1.1, Y(p)), new Point(nx + gap * 1.1, Y(p)));
        var head = new Point(nx, Y(notePos));
        ctx.DrawEllipse(Plasma.Halo, null, head, gap * 1.0, gap * 0.85);
        ctx.DrawEllipse(Plasma.Lit, null, head, gap * 0.62, gap * 0.46);
        if (ShowAccidental(written))
            Plasma.Glyph(ctx, Theory.Accidental(written.Alter) is { Length: > 0 } a ? a : "♮", nx - gap * 2.1, Y(notePos) - gap * 1.25, gap * 2, Ui.Music);
    }

    /// Accidental is printed only when it differs from the key signature.
    bool ShowAccidental(Pitch p)
    {
        int f = Session.Key.Fifths;
        // Letters altered by the key signature, in order: sharps F C G D A E B, flats B E A D G C F.
        int[] sharpOrder = [3, 0, 4, 1, 5, 2, 6], flatOrder = [6, 2, 5, 1, 4, 0, 3];
        int keyAlter = f > 0 && sharpOrder.Take(f).Contains(p.Letter) ? 1 : f < 0 && flatOrder.Take(-f).Contains(p.Letter) ? -1 : 0;
        return p.Alter != keyAlter;
    }
}

/// Instrument view: guitar fretboard or piano keyboard (click to switch).
/// Chord timeline: confirmed bars solid, current provisional outlined, playhead inside the bar.
public sealed class TimelineModule : RackModule
{
    public TimelineModule() { Title = "CHORD TIMELINE"; }

    protected override void DrawContent(DrawingContext ctx, Rect r)
    {
        var f = Frame!;
        int slots = 9;
        double w = r.Width / slots;
        int count = f.TimelineBars.Count + (f.ProvisionalChord.Length > 0 ? 1 : 0);
        for (int i = 0; i < count; i++)
        {
            bool prov = i == f.TimelineBars.Count;
            var cell = new Rect(r.X + i * w + 2, r.Y + 2, w - 4, r.Height - 4);
            if (prov)
                ctx.DrawRectangle(null, new ImmutablePen(Ui.Amber as IImmutableBrush, 1.5, new ImmutableDashStyle([4, 3], 0)), cell, 3, 3);
            else
                ctx.DrawRectangle(i == count - 1 ? Ui.Lcd : new ImmutableSolidColorBrush(Color.FromUInt32(0xFF1A1C20)), Ui.LcdLineDim, cell, 3, 3);
            string sym = prov ? f.ProvisionalChord : f.TimelineBars[i];
            Ui.Text(ctx, sym, cell.Center.X, cell.Center.Y - 11, 18, prov ? Ui.Amber : Ui.LcdText, Ui.Mono, Ui.Align.Center);
            string roman = prov ? f.ChordRoman : i < f.TimelineRomans.Count ? f.TimelineRomans[i] : "";
            if (roman.Length > 0) Ui.Text(ctx, roman, cell.Center.X, cell.Center.Y + 10, 12, prov ? Ui.Amber : Ui.LcdText, Ui.Mono, Ui.Align.Center);
        }
        // playhead in the current bar
        double px = r.X + (count - 1) * w + 2 + (w - 4) * f.BarPhase;
        ctx.DrawLine(new ImmutablePen(Ui.Red as IImmutableBrush, 2), new Point(px, r.Y), new Point(px, r.Bottom));
    }
}

/// Transport: REC, 7-segment time counter, BPM + beat LEDs, metronome / count-in switches, → SCORE.
public sealed class TransportModule : RackModule
{
    public TransportModule() { Title = "TRANSPORT"; }

    public event Action? RecPressed, MetronomePressed, ScorePressed, TapPressed;
    Rect _rec, _metro, _score, _tap;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_rec.Contains(p)) RecPressed?.Invoke();
        else if (_metro.Contains(p)) MetronomePressed?.Invoke();
        else if (_score.Contains(p)) ScorePressed?.Invoke();
        else if (_tap.Contains(p)) TapPressed?.Invoke();
    }

    protected override void DrawContent(DrawingContext ctx, Rect r)
    {
        var f = Frame!;
        double cy = r.Center.Y;

        // REC button (blinks during count-in, lit while recording)
        _rec = new Rect(r.X, cy - 24, 48, 48);
        bool blink = f.CountingIn && (f.BeatInBar % 2 == 1);
        if (f.Recording || blink) ctx.DrawEllipse(Ui.RedGlow, null, _rec.Center, 34, 34);
        ctx.DrawEllipse(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF1A1A1A)), Ui.FaceEdge, _rec.Center, 24, 24);
        ctx.DrawEllipse(f.Recording || blink ? Ui.Red : Ui.RedOff, null, _rec.Center, 15, 15);
        Ui.Text(ctx, f.CountingIn ? "COUNT-IN" : "REC", _rec.Center.X, _rec.Bottom + 2, 9, Ui.Label, Ui.SansBold, Ui.Align.Center);

        // Time counter mm:ss.mmm
        var tc = new Rect(r.X + 70, cy - 22, 190, 44);
        ctx.DrawRectangle(Ui.SevenSegBg, Ui.FaceEdge, tc, 3, 3);
        var ts = TimeSpan.FromSeconds(Math.Abs(f.RecordedSeconds));   // negative during the count-in: time to REC
        Ui.Text(ctx, "88:88.888", tc.X + 10, tc.Y + 5, 28, Ui.SevenSegGhost, Ui.Mono);
        Ui.Text(ctx, (f.RecordedSeconds < 0 ? $"-{Math.Min(9, (int)ts.TotalMinutes)}" : $"{(int)ts.TotalMinutes:00}") + $":{ts.Seconds:00}.{ts.Milliseconds:000}", tc.X + 10, tc.Y + 5, 28, Ui.SevenSeg, Ui.Mono);

        // BPM + beat LEDs + meter
        var bp = new Rect(tc.Right + 18, cy - 22, 104, 44);
        ctx.DrawRectangle(Ui.SevenSegBg, Ui.FaceEdge, bp, 3, 3);
        Ui.Text(ctx, "888", bp.X + 34, bp.Y + 5, 28, Ui.SevenSegGhost, Ui.Mono);
        Ui.Text(ctx, "♩=", bp.X + 6, bp.Y + 12, 16, Ui.SevenSeg, Ui.Music);
        Ui.Text(ctx, $"{f.Bpm:000}", bp.X + 34, bp.Y + 5, 28, Ui.SevenSeg, Ui.Mono);
        double lx = bp.Right + 22;
        for (int i = 1; i <= Session.BeatsPerBar; i++)
            Ui.Led(ctx, new Point(lx + (i - 1) * 22, cy - 6), 6, f.BeatInBar == i, i == 1 ? Ui.Red : Ui.Green, i == 1 ? Ui.RedOff : Ui.GreenOff);
        Ui.Text(ctx, $"{Session.BeatsPerBar}/{Session.BeatUnit}", lx + (Session.BeatsPerBar - 1) * 11, cy + 8, 13, Ui.LabelBright, Ui.Mono, Ui.Align.Center);

        // Toggle switches
        double sx = lx + Session.BeatsPerBar * 22 + 24;
        _metro = new Rect(sx, cy - 22, 60, 44);
        DrawSwitch(ctx, _metro, f.Metronome, "METRO");
        DrawSwitch(ctx, new Rect(sx + 70, cy - 22, 60, 44), Session.CountInBars > 0, $"COUNT-IN {Session.CountInBars}");

        // Tap tempo (T), refused during REC
        _tap = new Rect(sx + 150, cy - 18, 64, 36);
        bool canTap = !f.Recording && !f.CountingIn;
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF24272C)), Ui.FaceEdge, _tap, 4, 4);
        Ui.Text(ctx, "TAP", _tap.Center.X, _tap.Center.Y - 9, 14, canTap ? Ui.LabelBright : Ui.Label, Ui.SansBold, Ui.Align.Center);

        // Tempo heard: beat marks on the scope; the BPM above is the session's, set by hand or TAP.
        string heard = f.DetectedBpm > 0 ? $"♩≈{f.DetectedBpm:0}  ({f.TempoConfidence:0.00})  ·  beat marks on the scope" : "listening…";
        Ui.Text(ctx, $"HEARD TEMPO  {heard}", r.X + 90, r.Y - 16, 10, Ui.Label, Ui.SansBold);

        // → SCORE
        _score = new Rect(r.Right - 110, cy - 18, 110, 36);
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF1D3A2A)), new ImmutablePen(Ui.Green as IImmutableBrush, 1.2), _score, 4, 4);
        Ui.Text(ctx, "→ SCORE", _score.Center.X, _score.Center.Y - 9, 14, Ui.Green, Ui.SansBold, Ui.Align.Center);
    }

    static void DrawSwitch(DrawingContext ctx, Rect r, bool on, string label)
    {
        var slot = new Rect(r.Center.X - 9, r.Y + 2, 18, 26);
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF0E0F11)), Ui.FaceEdge, slot, 9, 9);
        ctx.DrawEllipse(new ImmutableSolidColorBrush(Color.FromUInt32(0xFFB8BEC6)), null, new Point(slot.Center.X, on ? slot.Y + 8 : slot.Bottom - 8), 7, 7);
        Ui.Led(ctx, new Point(r.Center.X + 18, r.Y + 8), 3, on, Ui.Amber, Ui.AmberOff);
        Ui.Text(ctx, label, r.Center.X, r.Bottom - 12, 9, Ui.Label, Ui.SansBold, Ui.Align.Center);
    }
}

/// Status strip: pipeline milliseconds, CPU, xruns, recorder gaps. Always visible (pinned).
public sealed class StatusModule : RackModule
{
    public StatusModule() { Title = ""; }

    protected override void DrawContent(DrawingContext ctx, Rect r)
    {
        var f = Frame!;
        string sim = f.Simulated ? "   ·   SIMULATED DATA (no core)" : "";
        Ui.Text(ctx,
            $"capture {f.CaptureMs:0.0} ms   ·   proc {f.ProcessingMs:0.0} ms   ·   display {f.DisplayMs:0.0} ms   ·   CPU {f.CpuPercent:0} %   ·   xrun {f.Xruns}   ·   gaps {f.RecorderGaps}{sim}",
            r.X, r.Y - 12, 12, f.Simulated ? Ui.Amber : Ui.LabelBright, Ui.Mono);
    }
}

/// Stage mode (spec §22.5): huge chord and note, beat LEDs, one ms line.
public sealed class StageView : RackModule
{
    StreamGeometry? _grid;
    Size _gridSize;

    public StageView() { Title = "STAGE  ·  Esc to leave"; }

    protected override void DrawContent(DrawingContext ctx, Rect r)
    {
        var f = Frame!;
        Plasma.Panel(ctx, r, ref _grid, ref _gridSize);
        double h = r.Height;
        bool chords = Session.IsChordMode;
        var p = (chords ? f.Bass : f.Note).WithOctaveShift(Session.Clef.OctaveShift());

        // Big dot-matrix readout, centred: chord (7 cells) or note (4 cells).
        int cells = chords ? 7 : 4;
        double dot = Math.Min(r.Width * 0.85 / (cells * 6 * 1.3), h * (chords ? 0.34 : 0.42) / (7 * 1.3));
        string main = Centre(chords ? f.DisplayChord : f.NoteValid ? p.Name : "", cells);
        Plasma.DotText(ctx, main, r.Center.X - Plasma.CellWidth(dot) * cells / 2, r.Y + h * 0.08, dot, cells, dim: chords && f.DisplayChord == f.CandidateChord);
        if (chords)
        {
            double bassDot = dot * 0.42;
            Plasma.DotText(ctx, Centre(f.ChordSymbol.Length > 0 ? p.Name : "", 4), r.Center.X - Plasma.CellWidth(bassDot) * 2, r.Y + h * 0.52, bassDot, 4);
        }
        else
        {
            double bw = r.Width * 0.6, bx = r.Center.X - bw / 2, by = r.Y + h * 0.66;
            ctx.DrawLine(Plasma.LineDim, new Point(bx, by + 7), new Point(bx + bw, by + 7));
            Plasma.GlowLine(ctx, new Point(r.Center.X, by - 6), new Point(r.Center.X, by + 20));
            if (f.NoteValid)
            {
                double nx = bx + bw * (Math.Clamp(f.Cents, -50, 50) + 50) / 100;
                var bar = new Rect(nx - 5, by - 8, 10, 30);
                ctx.DrawRectangle(Plasma.Halo, null, bar.Inflate(6), 4, 4);
                ctx.DrawRectangle(Math.Abs(f.Cents) < 5 ? Plasma.Hot : Plasma.Lit, null, bar, 2, 2);
            }
        }
        for (int i = 1; i <= Session.BeatsPerBar; i++)
            Plasma.Lamp(ctx, new Point(r.Center.X + (i - (Session.BeatsPerBar + 1) / 2.0) * 60, r.Y + h * 0.82), i == 1 ? 16 : 12, f.BeatInBar == i);
        string rec = f.Recording ? "● REC   " : f.CountingIn ? "COUNT-IN   " : "";
        static string Centre(string s, int cells) => new string(' ', Math.Max(0, (cells - Plasma.CellCount(s)) / 2)) + s;
        Plasma.Text(ctx, $"{rec}capture {f.CaptureMs:0} ms · proc {f.ProcessingMs:0} ms · display {f.DisplayMs:0} ms", r.Center.X, r.Bottom - 30, 16, f.Recording, Ui.Mono, Ui.Align.Center);
    }
}

/// Count-in over the LIVE rack: beats left until REC, the beat of the bar, meter and tempo, so the
/// first downbeat of the take is never a surprise. Hidden outside the count-in; never takes input.
public sealed class CountInOverlay : Control
{
    public LiveFrame? Frame { get; set; }
    public Session Session { get; set; } = new();

    public CountInOverlay() { IsHitTestVisible = false; }

    public override void Render(DrawingContext ctx)
    {
        var f = Frame;
        if (f is null || !f.CountingIn || f.Bpm <= 0) return;
        double beat = 60 / f.Bpm;
        int left = Math.Max(1, (int)Math.Ceiling(-f.RecordedSeconds / beat - 0.02));
        int bars = (left + Session.BeatsPerBar - 1) / Session.BeatsPerBar;

        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xB0000000)), null, new Rect(Bounds.Size));
        var card = new Rect(Bounds.Width / 2 - 260, Bounds.Height / 2 - 170, 520, 340);
        ctx.DrawRectangle(Ui.Face, new ImmutablePen(Ui.Red as IImmutableBrush, 2), card, 10, 10);
        Ui.Text(ctx, "COUNT-IN  ·  RECORDING STARTS RIGHT AFTER 1", card.Center.X, card.Y + 18, 14, Ui.LabelBright, Ui.SansBold, Ui.Align.Center);
        Ui.Text(ctx, left.ToString(), card.Center.X, card.Y + 44, 150, left <= Session.BeatsPerBar ? Ui.Red : Ui.Amber, Ui.Mono, Ui.Align.Center);
        double lx = card.Center.X - (Session.BeatsPerBar - 1) * 22;
        for (int i = 1; i <= Session.BeatsPerBar; i++)
            Ui.Led(ctx, new Point(lx + (i - 1) * 44, card.Bottom - 74), 12, f.BeatInBar == i, i == 1 ? Ui.Red : Ui.Green, i == 1 ? Ui.RedOff : Ui.GreenOff);
        string beats = left == 1 ? "1 beat" : $"{left} beats";
        Ui.Text(ctx, $"{beats} left  ·  {bars} bar{(bars == 1 ? "" : "s")}  ·  {Session.BeatsPerBar}/{Session.BeatUnit}  ·  ♩ = {f.Bpm:0}",
            card.Center.X, card.Bottom - 50, 15, Ui.LabelBright, Ui.Sans, Ui.Align.Center);
        Ui.Text(ctx, Session.ClickDuringTake ? "click continues during the take" : "click stops at REC  ·  the beat LEDs keep the beat",
            card.Center.X, card.Bottom - 26, 12, Ui.Label, Ui.Sans, Ui.Align.Center);
    }
}
