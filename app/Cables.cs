using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Dissonancia;

// Patch cables as ropes (Verlet point masses) and their drawing: STUDIO's lunchbox and LIVE's
// pedalboard share them.

/// A patch cable as a rope of point masses (Verlet), its ends pinned to jacks or to the hand.
public sealed class Rope
{
    const int N = 20;
    public readonly Point[] P = new Point[N], Q = new Point[N];
    double _seg = -1;
    public Rope(Point a, Point b)
    {
        for (int i = 0; i < N; i++)
        {
            double u = i / (N - 1.0);
            P[i] = Q[i] = new Point(a.X + (b.X - a.X) * u, a.Y + (b.Y - a.Y) * u + Math.Sin(u * Math.PI) * 30);
        }
    }
    public Point B => P[N - 1];
    public void Reverse() { Array.Reverse(P); Array.Reverse(Q); }

    /// b null: that end hangs free (a cable let go), its length kept.
    public void Step(double dt, Point a, Point? b, double floor)
    {
        if (b is { } bb)
        {
            double d = Math.Sqrt((bb.X - a.X) * (bb.X - a.X) + (bb.Y - a.Y) * (bb.Y - a.Y));
            _seg = (d + 36 + 0.22 * d) / (N - 1);   // slack: a patch cable is longer than the gap
        }
        else if (_seg < 0) _seg = 8;
        const double g = 1500;
        for (int sub = 0; sub < 2; sub++)
        {
            double h = dt / 2;
            for (int i = 0; i < N; i++)
            {
                var v = (P[i] - Q[i]) * 0.965;
                Q[i] = P[i];
                P[i] = P[i] + v + new Point(0, g * h * h);
            }
            for (int it = 0; it < 14; it++)
            {
                P[0] = a;
                if (b is { } pin) P[N - 1] = pin;
                for (int i = 0; i < N - 1; i++)
                {
                    var dvec = P[i + 1] - P[i];
                    double len = Math.Max(1e-6, Math.Sqrt(dvec.X * dvec.X + dvec.Y * dvec.Y));
                    var corr = dvec * ((len - _seg) / len * 0.5);
                    bool fixedA = i == 0, fixedB = i + 1 == N - 1 && b is not null;
                    if (!fixedA) P[i] += corr * (fixedB ? 2 : 1);
                    if (!fixedB) P[i + 1] -= corr * (fixedA ? 2 : 1);
                }
                for (int i = 0; i < N; i++) if (P[i].Y > floor) P[i] = new Point(P[i].X, floor);
            }
            P[0] = a;
            if (b is { } pin2) P[N - 1] = pin2;
        }
    }
}


public static class Cables
{
    public static readonly uint[] Colours = [0xFFE2462F, 0xFFF2C12E, 0xFF2F8FE2, 0xFF3DBE6B, 0xFFF08A24, 0xFFD9D9D9, 0xFFB45CE0];

    /// A rope drawn as a patch cable: jacket, coloured core, pulses running with the signal, plugs.
    public static void Draw(DrawingContext ctx, Rope rope, uint colour, float db, double seconds, bool plugA = true, bool plugB = true)
    {
        var pts = rope.P;
        var g = new StreamGeometry();
        using (var gc = g.Open())
        {
            gc.BeginFigure(pts[0], false);
            for (int i = 1; i < pts.Length - 1; i++)
                gc.QuadraticBezierTo(pts[i], new Point((pts[i].X + pts[i + 1].X) / 2, (pts[i].Y + pts[i + 1].Y) / 2));
            gc.LineTo(pts[^1]);
            gc.EndFigure(false);
        }
        var col = Color.FromUInt32(colour);
        var dark = Color.FromArgb(255, (byte)(col.R * 0.45), (byte)(col.G * 0.45), (byte)(col.B * 0.45));
        ctx.DrawGeometry(null, new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0x66000000)), 7, lineCap: PenLineCap.Round), g);
        ctx.DrawGeometry(null, new ImmutablePen(new ImmutableSolidColorBrush(dark), 5.5, lineCap: PenLineCap.Round), g);
        ctx.DrawGeometry(null, new ImmutablePen(new ImmutableSolidColorBrush(col), 3.2, lineCap: PenLineCap.Round), g);
        if (db > -60)
        {   // the signal travelling: pulses from the output end to the input end
            double total = 0;
            var cum = new double[pts.Length];
            for (int i = 1; i < pts.Length; i++) cum[i] = total += Math.Sqrt(Math.Pow(pts[i].X - pts[i - 1].X, 2) + Math.Pow(pts[i].Y - pts[i - 1].Y, 2));
            if (!double.IsFinite(total) || total > 1e5) return;
            float k = Math.Clamp((db + 60) / 60, 0, 1);
            var pulse = new ImmutableSolidColorBrush(Color.FromArgb((byte)(110 + 145 * k), 255, 245, 215));
            double phase = seconds * 80 % 24;
            int j = 1;
            for (double d = phase; d < total; d += 24)
            {
                while (j < pts.Length - 1 && cum[j] < d) j++;
                double u = (d - cum[j - 1]) / Math.Max(1e-6, cum[j] - cum[j - 1]);
                var p = new Point(pts[j - 1].X + (pts[j].X - pts[j - 1].X) * u, pts[j - 1].Y + (pts[j].Y - pts[j - 1].Y) * u);
                ctx.DrawEllipse(pulse, null, p, 1.3 + 1.2 * k, 1.3 + 1.2 * k);
            }
        }
        if (plugA) Plug(ctx, pts[0], pts[1], col);
        if (plugB) Plug(ctx, pts[^1], pts[^2], col);
    }

    /// A jack plug: a metal sleeve at the end, its boot in the cable's colour, along the cable.
    public static void Plug(DrawingContext ctx, Point tip, Point toward, Color col)
    {
        var d = toward - tip;
        double len = Math.Max(1e-6, Math.Sqrt(d.X * d.X + d.Y * d.Y));
        var u = d / len;
        ctx.DrawLine(new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFFB9BEC6)), 7, lineCap: PenLineCap.Round), tip, tip + u * 4);
        ctx.DrawLine(new ImmutablePen(new ImmutableSolidColorBrush(col), 9, lineCap: PenLineCap.Round), tip + u * 5, tip + u * 13);
        ctx.DrawEllipse(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF2A2D32)), null, tip, 3.2, 3.2);
    }

    /// The cable under the mouse, lit.
    public static void Halo(DrawingContext ctx, Rope rope)
    {
        var pen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xAAFFFFFF)), 10, lineCap: PenLineCap.Round);
        for (int i = 1; i < rope.P.Length; i++) ctx.DrawLine(pen, rope.P[i - 1], rope.P[i]);
    }
}
