using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;

namespace Dissonancia;

/// Guitar fretboard with the most likely shape of the chord (spec §22.3, "likely shape").
public sealed class FretboardModule : RackModule
{
    public FretboardModule() { Title = "FRETBOARD"; }

    static readonly IBrush Wood = new ImmutableSolidColorBrush(Color.FromUInt32(0xFF3A2A1C));
    static readonly IPen Nut = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFFE8E0D0)), 4);
    static readonly IPen Fret = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF9A948A)), 1.5);
    static readonly IBrush StringBrush = new ImmutableSolidColorBrush(Color.FromUInt32(0xFFCFC7B8));
    static readonly IPen OpenRing = new ImmutablePen(Ui.Green as IImmutableBrush, 2);

    protected override void DrawContent(DrawingContext ctx, Rect r)
    {
        var f = Frame!;
        const int frets = 12;
        var board = new Rect(r.X + 44, r.Y + 8, r.Width - 54, r.Height - 30);
        ctx.DrawRectangle(Wood, null, board, 3, 3);
        double fw = board.Width / frets;
        for (int i = 0; i <= frets; i++)
            ctx.DrawLine(i == 0 ? Nut : Fret, new Point(board.X + i * fw, board.Y), new Point(board.X + i * fw, board.Bottom));
        foreach (var m in new[] { 3, 5, 7, 9, 12 })
            ctx.DrawEllipse(Ui.Label, null, new Point(board.X + (m - 0.5) * fw, board.Bottom + 10), 3, 3);
        string[] names = ["E", "A", "D", "G", "B", "e"];
        int lowest = Array.FindIndex(f.Frets, x => x >= 0);
        for (int s = 0; s < 6; s++)
        {
            double y = board.Bottom - (s + 0.5) * board.Height / 6;
            ctx.DrawLine(new ImmutablePen(StringBrush as IImmutableBrush, 2.2 - s * 0.25), new Point(board.X, y), new Point(board.Right, y));
            Ui.Text(ctx, names[s], r.X + 6, y - 8, 12, Ui.LabelBright, Ui.SansBold, Ui.Align.Center);
            if (f.Frets.Length == 0) continue;
            int fret = s < f.Frets.Length ? f.Frets[s] : -1;
            if (fret < 0) Ui.Text(ctx, "×", board.X - 14, y - 9, 14, Ui.Red, Ui.SansBold, Ui.Align.Center);
            else if (fret == 0) ctx.DrawEllipse(null, OpenRing, new Point(board.X - 13, y), 6, 6);
            else ctx.DrawEllipse(s == lowest ? Ui.Amber : Ui.Green, null, new Point(board.X + (fret - 0.5) * fw, y), 9, 9);
        }
        Ui.Text(ctx, "likely shape", board.Right, r.Bottom - 14, 10, Ui.Label, null, Ui.Align.Right);
    }
}

/// Piano keyboard C2-B5: chord pitch classes lit, bass in amber; mono modes light the note.
public sealed class KeyboardModule : RackModule
{
    public KeyboardModule() { Title = "KEYBOARD"; }

    static readonly IBrush White = new ImmutableSolidColorBrush(Color.FromUInt32(0xFFE9E6DF));
    static readonly IBrush Black = new ImmutableSolidColorBrush(Color.FromUInt32(0xFF141414));
    static readonly IPen KeyEdge = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF1A1A1A)), 1);

    protected override void DrawContent(DrawingContext ctx, Rect r)
    {
        var f = Frame!;
        const int firstMidi = 36, octaves = 4;
        int whiteCount = octaves * 7;
        double ww = r.Width / whiteCount;
        int[] whiteSemis = [0, 2, 4, 5, 7, 9, 11];
        bool chords = Session.IsChordMode;
        bool Lit(int midi) => chords ? f.ChordPitchClasses.Contains(midi % 12) : f.NoteValid && midi == f.Note.Midi;
        IBrush Fill(int midi, IBrush off) => chords && f.BassValid && midi == f.Bass.Midi ? Ui.Amber : Lit(midi) ? Ui.Green : off;
        for (int i = 0; i < whiteCount; i++)
        {
            int midi = firstMidi + 12 * (i / 7) + whiteSemis[i % 7];
            ctx.DrawRectangle(Fill(midi, White), KeyEdge, new Rect(r.X + i * ww, r.Y, ww, r.Height - 6), 2, 2);
            if (i % 7 == 0) Ui.Text(ctx, $"C{midi / 12 - 1}", r.X + i * ww + ww / 2, r.Bottom - 24, 9, Ui.Label, null, Ui.Align.Center);
        }
        for (int i = 0; i < whiteCount; i++)
        {
            int semi = whiteSemis[i % 7];
            if (semi is 4 or 11) continue;
            int midi = firstMidi + 12 * (i / 7) + semi + 1;
            ctx.DrawRectangle(Fill(midi, Black), null, new Rect(r.X + (i + 1) * ww - ww * 0.3, r.Y, ww * 0.6, (r.Height - 6) * 0.62), 2, 2);
        }
    }
}

/// Scrolling CQT spectrogram, low notes at the bottom, one column per analysis hop, C lines labelled.
public sealed class WaterfallModule : RackModule
{
    const int Columns = 500;   // 10 s at a 20 ms hop
    WriteableBitmap? _bitmap;
    int _rows, _write;
    ulong _lastSequence;
    float _peak = 1e-6f;
    static readonly IPen NoteLine = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0x40FFFFFF)), 1);

    public WaterfallModule() { Title = "CQT WATERFALL"; }

    protected override void DrawContent(DrawingContext ctx, Rect r)
    {
        var f = Frame!;
        ctx.DrawRectangle(Ui.Scope, Ui.LcdEdge, r, 3, 3);
        if (f.CqtBins <= 0) { Ui.Text(ctx, "chord modes: CQT not running", r.Center.X, r.Center.Y - 8, 12, Ui.LcdDim, Ui.Mono, Ui.Align.Center); return; }
        if (_bitmap is null || _rows != f.CqtBins)
        {
            _bitmap?.Dispose();
            _rows = f.CqtBins;
            _bitmap = new WriteableBitmap(new PixelSize(Columns, _rows), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            _write = 0;
            _lastSequence = f.AnalysisSequence;
            using var clear = _bitmap.Lock();
            unsafe { new Span<byte>((void*)clear.Address, clear.RowBytes * _rows).Fill(0); }
        }
        if (f.AnalysisSequence != _lastSequence)
        {
            // Hops since the last frame (display and analysis rates differ); the newest column repeats.
            int n = (int)Math.Min(f.AnalysisSequence - _lastSequence, Columns);
            _lastSequence = f.AnalysisSequence;
            float mx = 1e-9f;
            for (int k = 0; k < _rows; k++) mx = Math.Max(mx, f.Cqt[k]);
            _peak = Math.Max(_peak * 0.995f, mx);   // slow auto-gain, ~4 s to fall 20 dB
            using var fb = _bitmap.Lock();
            unsafe
            {
                for (int c = 0; c < n; c++, _write = (_write + 1) % Columns)
                    for (int k = 0; k < _rows; k++)
                    {
                        float db = 20 * MathF.Log10(f.Cqt[k] / _peak + 1e-9f);
                        float v = Math.Clamp((db + 50) / 50, 0, 1);
                        ((uint*)((byte*)fb.Address + (_rows - 1 - k) * fb.RowBytes))[_write] = Heat(v);
                    }
            }
        }
        // Oldest column at the left: [write, end) then [0, write).
        var inner = r.Deflate(2);
        double split = inner.Width * (Columns - _write) / Columns;
        ctx.DrawImage(_bitmap, new Rect(_write, 0, Columns - _write, _rows), new Rect(inner.X, inner.Y, split, inner.Height));
        if (_write > 0) ctx.DrawImage(_bitmap, new Rect(0, 0, _write, _rows), new Rect(inner.X + split, inner.Y, inner.Width - split, inner.Height));
        for (int midi = 0; midi < 128; midi += 12)
        {
            double bin = f.CqtBinsPerOctave * Math.Log2(440 * Math.Pow(2, (midi - 69) / 12.0) / f.CqtMinHz);
            if (bin < 0 || bin >= _rows) continue;
            double y = inner.Bottom - (bin + 0.5) / _rows * inner.Height;
            ctx.DrawLine(NoteLine, new Point(inner.X, y), new Point(inner.Right, y));
            Ui.Text(ctx, $"C{midi / 12 - 1}", inner.X + 4, y - 14, 10, Ui.LcdText, Ui.Mono);
        }
        Ui.Text(ctx, $"{Columns * 0.02:0} s", inner.X + 4, inner.Bottom - 16, 10, Ui.LcdDim, Ui.Mono);
        Ui.Text(ctx, "now", inner.Right - 4, inner.Bottom - 16, 10, Ui.LcdDim, Ui.Mono, Ui.Align.Right);
    }

    // Black -> green -> yellow -> white (BGRA); squared so the noise floor stays dark.
    static uint Heat(float v)
    {
        v *= v;
        float r = Math.Clamp(v * 3 - 1.4f, 0, 1), g = Math.Clamp(v * 1.6f, 0, 1), b = Math.Clamp(v * 3 - 2, 0, 1) * 0.9f + v * 0.1f;
        return 0xFF000000u | (uint)(r * 255) << 16 | (uint)(g * 255) << 8 | (uint)(b * 255);
    }
}

/// Big cents needle (plasma). Mono modes: the note; chord modes: the estimated global tuning vs A4.
public sealed class TunerModule : RackModule
{
    StreamGeometry? _grid;
    Size _gridSize;

    public TunerModule() { Title = "TUNER"; }

    protected override void DrawContent(DrawingContext ctx, Rect r)
    {
        var f = Frame!;
        Plasma.Panel(ctx, r, ref _grid, ref _gridSize);
        bool mono = !Session.IsChordMode;
        bool valid = mono ? f.NoteValid : f.TuningValid;
        float cents = mono ? f.Cents : f.TuningCents;
        string name = mono ? (valid ? f.Note.Name : "") : "A4";

        var c = new Point(r.Center.X, r.Bottom - 10);
        double radius = Math.Min(r.Width * 0.3, r.Height - 22);
        const double span = 60 * Math.PI / 180;   // ±50 cents = ±60°
        Point At(double cent, double rr)
        {
            double a = -Math.PI / 2 + Math.Clamp(cent, -50, 50) / 50 * span;
            return new Point(c.X + rr * Math.Cos(a), c.Y + rr * Math.Sin(a));
        }
        for (int t = -50; t <= 50; t += 5)
        {
            bool major = t % 10 == 0;
            bool inZone = Math.Abs(t) <= 5;
            var a = At(t, radius * (major ? 0.86 : 0.91));
            var b = At(t, radius);
            if (inZone) Plasma.GlowLine(ctx, a, b); else ctx.DrawLine(major ? Plasma.Line : Plasma.LineDim, a, b);
            if (t is -50 or -25 or 25 or 50) Plasma.Text(ctx, $"{t:+0;-0}", At(t, radius * 0.7).X, At(t, radius * 0.7).Y - 7, 10, false, Ui.Mono, Ui.Align.Center);
        }
        Plasma.Lamp(ctx, At(0, radius + 9), 4, valid && Math.Abs(cents) <= 5);
        if (valid) Plasma.GlowLine(ctx, c, At(cents, radius * 0.96));
        ctx.DrawEllipse(Plasma.Lit, null, c, 4, 4);

        double dot = Math.Min(5, r.Height / 30);
        Plasma.DotText(ctx, name, r.X + 14, r.Y + 12, dot, cells: 4, dim: !valid);
        Plasma.Text(ctx, valid ? $"{cents:+0;-0;0} ¢" : "— ¢", r.Right - 14, r.Y + 12, 22, valid, Ui.Mono, Ui.Align.Right);
        Plasma.Text(ctx, mono ? (valid ? $"{f.Hz:0.0} Hz" : "— Hz") : valid ? $"A4 = {440 * Math.Pow(2, cents / 1200):0.0} Hz" : "tuning: not settled",
            r.Right - 14, r.Y + 42, 13, false, Ui.Mono, Ui.Align.Right);
    }
}
