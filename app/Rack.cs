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

    public static readonly IBrush Scope = B(0xFF050E09);
    public static readonly IPen ScopeGrid = P(0xFF10261A, 1);
    public static readonly IPen ScopeTrace = P(0xFF7CFFB2, 1.2);
    public static readonly IPen ScopeGlow = P(0x337CFFB2, 4);
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
        ctx.DrawRectangle(Ui.VuFace, Ui.VuInk, face, 5, 5);
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
        using (ctx.PushClip(face))
            ctx.DrawLine(Ui.VuNeedle, pivot, OnArc(VuPos(f.VuLevel), radius * 0.9));

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
public sealed class ScopeModule : RackModule
{
    public ScopeModule() { Title = "SCOPE"; }

    protected override void DrawContent(DrawingContext ctx, Rect r)
    {
        var f = Frame!;
        ctx.DrawRectangle(Ui.Scope, Ui.LcdEdge, r, 3, 3);
        for (int i = 1; i < 10; i++) ctx.DrawLine(Ui.ScopeGrid, new Point(r.X + r.Width * i / 10, r.Y), new Point(r.X + r.Width * i / 10, r.Bottom));
        for (int i = 1; i < 4; i++) ctx.DrawLine(Ui.ScopeGrid, new Point(r.X, r.Y + r.Height * i / 4), new Point(r.Right, r.Y + r.Height * i / 4));

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
        Ui.Text(ctx, $"{span:0.0} s", r.X + 6, r.Bottom - 16, 10, Ui.LcdDim, Ui.Mono);
        Ui.Text(ctx, "now", r.Right - 6, r.Bottom - 16, 10, Ui.LcdDim, Ui.Mono, Ui.Align.Right);
    }
}

/// LCD panel: chord line on top, monophonic note (or bass) below with a mini staff.
public sealed class LcdModule : RackModule
{
    public LcdModule() { Title = "ANALYZER"; }

    protected override void DrawContent(DrawingContext ctx, Rect r)
    {
        var f = Frame!;
        ctx.DrawRectangle(Ui.Lcd, Ui.LcdEdge, r, 6, 6);
        var inner = r.Deflate(14);
        bool chords = Session.IsChordMode;
        double split = chords ? inner.Y + inner.Height * 0.52 : inner.Y;

        if (chords)
        {
            // Ghost segments behind the symbol, like an unlit LCD.
            Ui.Text(ctx, "888888", inner.X, inner.Y - 4, 52, Ui.LcdGhost, Ui.Mono);
            Ui.Text(ctx, f.ChordSymbol, inner.X, inner.Y - 4, 52, Ui.LcdText, Ui.Mono);

            double rx = inner.Right;
            Ui.Led(ctx, new Point(rx - 118, inner.Y + 10), 5, !f.ChordConfirmed, Ui.Amber, Ui.AmberOff);
            Ui.Text(ctx, "PROV", rx - 108, inner.Y + 3, 11, f.ChordConfirmed ? Ui.LcdDim : Ui.Amber, Ui.SansBold);
            Ui.Led(ctx, new Point(rx - 58, inner.Y + 10), 5, f.ChordConfirmed, Ui.Green, Ui.GreenOff);
            Ui.Text(ctx, "CONF", rx - 48, inner.Y + 3, 11, f.ChordConfirmed ? Ui.Green : Ui.LcdDim, Ui.SansBold);

            // Confidence bar (10 segments)
            for (int i = 0; i < 10; i++)
                ctx.DrawRectangle(i < f.ChordConfidence * 10 ? Ui.LcdText : Ui.LcdGhost, null,
                    new Rect(rx - 118 + i * 11.5, inner.Y + 26, 9, 8), 1, 1);
            Ui.Text(ctx, $"{f.ChordConfidence:0.00}", rx - 124, inner.Y + 22, 12, Ui.LcdText, Ui.Mono, Ui.Align.Right);

            string ms = f.ChordConfirmed ? Ui.Ms(f.ChordLatencyMs) : $"{Ui.Ms(f.ChordLatencyMs)} · +{f.ChordConfirmElapsedMs:0} ms";
            Ui.Text(ctx, ms, rx, inner.Y + 44, 13, Ui.LcdText, Ui.Mono, Ui.Align.Right);

            Ui.Text(ctx, $"alt: {f.ChordAlternatives}", inner.X, inner.Y + 66, 13, Ui.LcdDim, Ui.Mono);
            if (f.ChordReason.Length > 0) Ui.Text(ctx, $"reason: {f.ChordReason}", inner.X, inner.Y + 86, 12, Ui.LcdDim, Ui.Mono);

            ctx.DrawLine(Ui.LcdLineDim, new Point(inner.X, split - 6), new Point(inner.Right, split - 6));
        }

        // Lower line: bass (chord modes) or monophonic note (mono modes), spelled and clef-shifted.
        var sounding = chords ? f.Bass : f.Note;
        bool valid = chords || f.NoteValid;
        var written = sounding.WithOctaveShift(Session.Clef.OctaveShift());
        double big = chords ? 40 : 64, y = split + (chords ? 4 : 10);
        string label = chords ? "BASS" : "NOTE";
        Ui.Text(ctx, label, inner.X, y, 10, Ui.LcdDim, Ui.SansBold);
        Ui.Text(ctx, valid ? written.Name : "—", inner.X, y + 12, big, Ui.LcdText, Ui.Mono);

        double infoX = inner.X + (chords ? 130 : 210);
        if (chords)
        {
            Ui.Led(ctx, new Point(infoX + 5, y + 30), 5, f.BassSettled, Ui.Green, Ui.GreenOff);
            Ui.Text(ctx, f.BassSettled ? "settled" : "settling", infoX + 22, y + 22, 13, f.BassSettled ? Ui.LcdText : Ui.Amber, Ui.Mono);
            Ui.Text(ctx, $"{sounding.Hz():0.0} Hz", infoX, y + 44, 13, Ui.LcdDim, Ui.Mono);
        }
        else
        {
            DrawCents(ctx, new Rect(infoX, y + 18, 190, 26), f.Cents);
            Ui.Text(ctx, $"{f.Cents:+0;-0}¢   {f.Hz:0.0} Hz", infoX, y + 50, 14, Ui.LcdText, Ui.Mono);
            Ui.Text(ctx, Ui.Ms(f.NoteLatencyMs), infoX, y + 72, 13, Ui.LcdDim, Ui.Mono);
        }
        if (Session.Clef == Clef.Treble8vb) Ui.Text(ctx, "written 8vb", inner.X, y + 12 + big * 1.25, 10, Ui.LcdDim, Ui.Mono);

        double staffW = 170, staffH = chords ? 56 : 80;
        DrawStaff(ctx, new Rect(inner.Right - staffW, y + (chords ? 10 : 20), staffW, staffH), written, valid);
    }

    static void DrawCents(DrawingContext ctx, Rect r, float cents)
    {
        ctx.DrawLine(Ui.LcdLineDim, new Point(r.X, r.Center.Y), new Point(r.Right, r.Center.Y));
        for (int c = -50; c <= 50; c += 10)
        {
            double x = r.X + r.Width * (c + 50) / 100.0;
            ctx.DrawLine(c == 0 ? Ui.LcdLine : Ui.LcdLineDim, new Point(x, r.Center.Y - (c == 0 ? 10 : 5)), new Point(x, r.Center.Y + (c == 0 ? 10 : 5)));
        }
        double nx = r.X + r.Width * (Math.Clamp(cents, -50, 50) + 50) / 100.0;
        ctx.DrawRectangle(Math.Abs(cents) < 5 ? Ui.Green : Math.Abs(cents) < 15 ? Ui.LcdText : Ui.Amber, null, new Rect(nx - 2, r.Y, 4, r.Height), 1, 1);
    }

    void DrawStaff(DrawingContext ctx, Rect r, Pitch written, bool valid)
    {
        double gap = r.Height / 6, bottom = r.Bottom - gap;   // 5 lines, 4 spaces
        double Y(int pos) => bottom - pos * gap / 2;
        for (int i = 0; i < 5; i++) ctx.DrawLine(Ui.LcdLine, new Point(r.X, Y(i * 2)), new Point(r.Right, Y(i * 2)));

        var clef = Session.Clef;
        string glyph = clef switch { Clef.Bass => "𝄢", Clef.Alto or Clef.Tenor => "𝄡", _ => "𝄞" };
        double clefSize = gap * 4.2;
        Ui.Text(ctx, glyph, r.X + 2, Y(clef is Clef.Bass ? 8 : clef is Clef.Alto ? 6 : clef is Clef.Tenor ? 8 : 6) - clefSize * 0.55, clefSize, Ui.LcdText, Ui.Music);
        if (clef == Clef.Treble8vb) Ui.Text(ctx, "8", r.X + 12, Y(-4), gap * 1.3, Ui.LcdText, Ui.SansBold);

        // Key signature
        double kx = r.X + gap * 3.2;
        string acc = Session.Key.Fifths >= 0 ? "♯" : "♭";
        foreach (var pos in clef.KeySignaturePositions(Session.Key.Fifths))
        {
            Ui.Text(ctx, acc, kx, Y(pos) - gap * 1.25, gap * 2, Ui.LcdText, Ui.Music);
            kx += gap * 0.95;
        }
        if (!valid) return;

        // Note head, accidental, ledger lines
        int notePos = written.Step - clef.BottomLineStep();
        double nx = Math.Max(kx + gap * 2.4, r.X + r.Width * 0.72);
        for (int p = -2; p >= notePos; p -= 2) ctx.DrawLine(Ui.LcdLine, new Point(nx - gap * 1.1, Y(p)), new Point(nx + gap * 1.1, Y(p)));
        for (int p = 10; p <= notePos; p += 2) ctx.DrawLine(Ui.LcdLine, new Point(nx - gap * 1.1, Y(p)), new Point(nx + gap * 1.1, Y(p)));
        ctx.DrawEllipse(Ui.LcdText, null, new Point(nx, Y(notePos)), gap * 0.62, gap * 0.46);
        if (ShowAccidental(written))
            Ui.Text(ctx, Theory.Accidental(written.Alter) is { Length: > 0 } a ? a : "♮", nx - gap * 2.1, Y(notePos) - gap * 1.25, gap * 2, Ui.LcdText, Ui.Music);
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
public sealed class InstrumentModule : RackModule
{
    bool _keyboard;
    public InstrumentModule() { Title = "INSTRUMENT  ·  click to switch"; }

    protected override void OnPointerPressed(PointerPressedEventArgs e) { _keyboard = !_keyboard; InvalidateVisual(); }

    protected override void DrawContent(DrawingContext ctx, Rect r)
    {
        if (_keyboard || Session.Mode != AppMode.GuitarChords) DrawKeyboard(ctx, r); else DrawFretboard(ctx, r);
    }

    void DrawFretboard(DrawingContext ctx, Rect r)
    {
        var f = Frame!;
        const int frets = 12;
        var board = new Rect(r.X + 44, r.Y + 8, r.Width - 54, r.Height - 30);
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF3A2A1C)), null, board, 3, 3);
        double fw = board.Width / frets;
        for (int i = 0; i <= frets; i++)
            ctx.DrawLine(new ImmutablePen(new ImmutableSolidColorBrush(i == 0 ? Color.FromUInt32(0xFFE8E0D0) : Color.FromUInt32(0xFF9A948A)), i == 0 ? 4 : 1.5),
                new Point(board.X + i * fw, board.Y), new Point(board.X + i * fw, board.Bottom));
        foreach (var m in new[] { 3, 5, 7, 9, 12 })
            ctx.DrawEllipse(Ui.Label, null, new Point(board.X + (m - 0.5) * fw, board.Bottom + 10), 3, 3);
        string[] names = ["E", "A", "D", "G", "B", "e"];
        for (int s = 0; s < 6; s++)
        {
            double y = board.Bottom - (s + 0.5) * board.Height / 6;
            ctx.DrawLine(new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFFCFC7B8)), 2.2 - s * 0.25), new Point(board.X, y), new Point(board.Right, y));
            Ui.Text(ctx, names[s], r.X + 6, y - 8, 12, Ui.LabelBright, Ui.SansBold, Ui.Align.Center);
            int fret = s < f.Frets.Length ? f.Frets[s] : -1;
            if (fret < 0) Ui.Text(ctx, "×", board.X - 14, y - 9, 14, Ui.Red, Ui.SansBold, Ui.Align.Center);
            else if (fret == 0) ctx.DrawEllipse(null, new ImmutablePen(Ui.Green as IImmutableBrush, 2), new Point(board.X - 13, y), 6, 6);
            else ctx.DrawEllipse(s == LowestString(f.Frets) ? Ui.Amber : Ui.Green, null, new Point(board.X + (fret - 0.5) * fw, y), 9, 9);
        }
        Ui.Text(ctx, "likely shape", board.Right, r.Bottom - 14, 10, Ui.Label, null, Ui.Align.Right);
    }

    static int LowestString(int[] frets) { for (int i = 0; i < frets.Length; i++) if (frets[i] >= 0) return i; return -1; }

    void DrawKeyboard(DrawingContext ctx, Rect r)
    {
        var f = Frame!;
        const int firstMidi = 36, octaves = 4;           // C2..B5
        int whiteCount = octaves * 7;
        double ww = r.Width / whiteCount;
        int[] whiteSemis = [0, 2, 4, 5, 7, 9, 11];
        bool chords = Session.IsChordMode;
        // Chord modes: chord pitch classes lit, bass in amber. Mono modes: the sung/played note only.
        bool Lit(int midi) => chords ? f.ChordPitchClasses.Contains(midi % 12) : f.NoteValid && midi == f.Note.Midi;
        for (int i = 0; i < whiteCount; i++)
        {
            int midi = firstMidi + 12 * (i / 7) + whiteSemis[i % 7];
            bool bass = chords && midi == f.Bass.Midi, lit = Lit(midi);
            ctx.DrawRectangle(bass ? Ui.Amber : lit ? Ui.Green : new ImmutableSolidColorBrush(Color.FromUInt32(0xFFE9E6DF)),
                new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF1A1A1A)), 1), new Rect(r.X + i * ww, r.Y, ww, r.Height - 6), 2, 2);
            if (i % 7 == 0) Ui.Text(ctx, $"C{midi / 12 - 1}", r.X + i * ww + ww / 2, r.Bottom - 24, 9, Ui.Label, null, Ui.Align.Center);
        }
        for (int i = 0; i < whiteCount; i++)
        {
            int semi = whiteSemis[i % 7];
            if (semi is 4 or 11) continue;
            int midi = firstMidi + 12 * (i / 7) + semi + 1;
            bool bass = chords && midi == f.Bass.Midi, lit = Lit(midi);
            ctx.DrawRectangle(bass ? Ui.Amber : lit ? Ui.Green : new ImmutableSolidColorBrush(Color.FromUInt32(0xFF141414)), null,
                new Rect(r.X + (i + 1) * ww - ww * 0.3, r.Y, ww * 0.6, (r.Height - 6) * 0.62), 2, 2);
        }
    }
}

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

    public event Action? RecPressed, MetronomePressed, ScorePressed;
    Rect _rec, _metro, _score;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_rec.Contains(p)) RecPressed?.Invoke();
        else if (_metro.Contains(p)) MetronomePressed?.Invoke();
        else if (_score.Contains(p)) ScorePressed?.Invoke();
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
        var ts = TimeSpan.FromSeconds(f.RecordedSeconds);
        Ui.Text(ctx, "88:88.888", tc.X + 10, tc.Y + 5, 28, Ui.SevenSegGhost, Ui.Mono);
        Ui.Text(ctx, $"{(int)ts.TotalMinutes:00}:{ts.Seconds:00}.{ts.Milliseconds:000}", tc.X + 10, tc.Y + 5, 28, Ui.SevenSeg, Ui.Mono);

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
    public StageView() { Title = "STAGE  ·  Esc to leave"; }

    protected override void DrawContent(DrawingContext ctx, Rect r)
    {
        var f = Frame!;
        ctx.DrawRectangle(Ui.Lcd, Ui.LcdEdge, r, 8, 8);
        double h = r.Height;
        if (Session.IsChordMode)
            Ui.Text(ctx, f.ChordSymbol, r.Center.X, r.Y + h * 0.08, h * 0.34, f.ChordConfirmed ? Ui.LcdText : Ui.Amber, Ui.Mono, Ui.Align.Center);
        var p = (Session.IsChordMode ? f.Bass : f.Note).WithOctaveShift(Session.Clef.OctaveShift());
        Ui.Text(ctx, p.Name, r.Center.X, r.Y + h * (Session.IsChordMode ? 0.5 : 0.15), h * (Session.IsChordMode ? 0.16 : 0.36), Ui.LcdText, Ui.Mono, Ui.Align.Center);
        if (!Session.IsChordMode)
        {
            double bw = r.Width * 0.6, bx = r.Center.X - bw / 2, by = r.Y + h * 0.68;
            ctx.DrawRectangle(Ui.LcdGhost, null, new Rect(bx, by, bw, 14), 3, 3);
            double nx = bx + bw * (Math.Clamp(f.Cents, -50, 50) + 50) / 100;
            ctx.DrawRectangle(Math.Abs(f.Cents) < 5 ? Ui.Green : Ui.Amber, null, new Rect(nx - 4, by - 6, 8, 26), 2, 2);
        }
        for (int i = 1; i <= Session.BeatsPerBar; i++)
            Ui.Led(ctx, new Point(r.Center.X + (i - (Session.BeatsPerBar + 1) / 2.0) * 60, r.Y + h * 0.82), 14,
                f.BeatInBar == i, i == 1 ? Ui.Red : Ui.Green, i == 1 ? Ui.RedOff : Ui.GreenOff);
        string rec = f.Recording ? "● REC" : f.CountingIn ? "COUNT-IN" : "";
        Ui.Text(ctx, $"{rec}   capture {f.CaptureMs:0} ms · proc {f.ProcessingMs:0} ms · display {f.DisplayMs:0} ms", r.Center.X, r.Bottom - 30, 16, Ui.LcdDim, Ui.Mono, Ui.Align.Center);
    }
}
