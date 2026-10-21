using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Dissonancia;

/// DS-P: the signal flow of the whole session, drawn as a desk's buses. The source feeds the MIX
/// channel directly and, through the DS-S separator, one channel per stem (the split bus); every
/// channel that sounds drops into the sum bus to the console and the master, then the output;
/// every channel the transcription reads sends a copy from its tap (post-inserts, pre-fader,
/// dashed) along the send bus to its analysers and the score. Cables light with the level.
public sealed class FlowUnit : StudioUnit
{
    readonly List<(Rect R, int Track)> _channels = [];
    Rect _score;

    public FlowUnit(StudioState s) : base(s, "DS-P", "FLUXO DE SINAL") { Height = 128; }

    static readonly Dictionary<string, string> Names = new() { ["mix"] = "MIX", ["vocals"] = "VOZ", ["bass"] = "BAIXO", ["other"] = "OUTROS", ["drums"] = "BATERIA", ["guitar"] = "VIOLÃO", ["piano"] = "PIANO" };
    static readonly IBrush BoxFace = new ImmutableSolidColorBrush(Color.FromUInt32(0xFF2A2D32));

    void Box(DrawingContext ctx, Rect b, string model, string name, bool on, bool selected = false)
    {
        ctx.DrawRectangle(BoxFace, selected ? new ImmutablePen((IImmutableBrush)Ui.Amber, 1.5) : Ui.FaceEdge, b, 3, 3);
        double tx = b.X + 7;
        if (model.Length > 0) tx += Ui.Text(ctx, model, tx, b.Y + 3, 8, on ? Ui.Amber : Ui.Label, Ui.SansBold).Width + 4;
        Ui.Text(ctx, name, tx, b.Y + 3, 8, on ? Ui.LabelBright : Ui.Label, Ui.SansBold);
        Ui.Led(ctx, new Point(b.Right - 7, b.Y + 8), 2.4, on, Ui.Green, Ui.GreenOff);
    }

    protected override void DrawContent(DrawingContext ctx, Rect r)
    {
        _channels.Clear();
        var m = S.Meters;
        bool has = S.Current is not null, stems = S.Stems.Count > 0;
        float master = Math.Max(m.MasterPeak[0], m.MasterPeak[1]);
        double yDirect = r.Y + 4, ySplit = r.Y + 13, yBox = r.Y + 22, hBox = 36, ySum = r.Y + 72, ySend = r.Y + 88;
        double right = r.Right - 340;

        // FONTE and DS-S.
        var src = new Rect(r.X, yBox, 128, hBox);
        if (S.Recording && S.InputFrame is { } inF)
        {   // recording: the source is the input, its level on the jack, the take being written
            Box(ctx, src, "", inF.CountingIn ? "ENTRADA · CONTAGEM" : "ENTRADA · ● REC", true);
            Ui.Led(ctx, new Point(src.Right - 18, src.Y + 8), 3, !inF.CountingIn, Ui.Red, Ui.RedOff);
            string dev = S.InputName.Length > 19 ? S.InputName[..19] : S.InputName;
            Ui.Text(ctx, dev, src.X + 7, src.Y + 17, 9, Ui.LabelBright, Ui.Mono);
            for (int i = 0; i < 10; i++) Ui.Led(ctx, new Point(src.X + 10 + i * 11, src.Bottom + 6), 2, inF.PeakDbfs > -54 + i * 5.4, i > 8 ? Ui.Red : i > 6 ? Ui.Amber : Ui.Green, Ui.GreenOff);
            Ui.Text(ctx, "▶ grava o take na biblioteca", src.X, src.Bottom + 12, 7, Ui.Red, Ui.SansBold);
        }
        else
        {
            Box(ctx, src, "", "FONTE", has);
            string take = S.Current is null ? "—" : Path.GetFileNameWithoutExtension(S.Current);
            Ui.Text(ctx, take.Length > 20 ? take[..20] : take, src.X + 7, src.Y + 17, 9, Ui.LabelBright, Ui.Mono);
        }
        Jack(ctx, new Point(src.Right, src.Center.Y));
        var sep = new Rect(src.Right + 34, yBox, 118, hBox);
        Box(ctx, sep, "DS-S", "SEPARADOR", stems);
        Ui.Text(ctx, stems ? $"DEMUCS · {S.Stems.Count} saídas" : S.Separating ? $"separando {S.SepProgress * 100:0} %" : DemucsModel.HelperPresent ? "fora do caminho" : "ausente", sep.X + 7, sep.Y + 17, 9, stems ? Ui.LabelBright : Ui.Label, Ui.Mono);
        Jack(ctx, new Point(sep.X, sep.Center.Y));
        Jack(ctx, new Point(sep.Right, sep.Center.Y));
        float stemsIn = Enumerable.Range(1, Math.Min(7, S.Stems.Count)).Select(t => m.In[t]).DefaultIfEmpty(-120f).Max();
        if (stems) Patch(ctx, new Point(src.Right, src.Center.Y), new Point(sep.X, sep.Center.Y), m.In[0], 6);

        // Channels.
        int n = S.Tracks;
        double x0 = sep.Right + 30, avail = right - 40 - x0, w = Math.Min(128, avail / Math.Max(1, n) - 10);
        for (int t = 0; t < n; t++)
        {
            var b = new Rect(x0 + t * (w + 10), yBox, w, hBox);
            _channels.Add((b, t));
            var c = S.Channel(t);
            bool sounding = !c.Mute && !(Enumerable.Range(0, n).Any(k => S.Channel(k).Solo) && !c.Solo);
            Box(ctx, b, "", "", has, t == S.Selected);
            Plasma.DotText(ctx, $"{t + 1}", b.X + 6, b.Y + 6, 1.8, 1);
            Ui.Text(ctx, Names.GetValueOrDefault(S.NameOf(t), S.NameOf(t).ToUpperInvariant()), b.X + 22, b.Y + 4, 10, Ui.LabelBright, Ui.SansBold);
            Ui.Text(ctx, c.Mute ? "MUDO" : c.Solo ? "SOLO" : $"{c.Fader:+0.0;-0.0} dB", b.X + 22, b.Y + 19, 8, c.Mute || c.Solo ? Ui.Amber : Ui.Label, Ui.Mono);
            float pk = m.Peak[Math.Min(t, 7)];
            for (int i = 0; i < 6; i++) Ui.Led(ctx, new Point(b.Right - 30 + i * 4.5, b.Bottom - 6), 1.6, pk > -48 + i * 8, i > 4 ? Ui.Red : i > 3 ? Ui.Amber : Ui.Green, Ui.GreenOff);
            // In: the MIX straight from the source; a stem from the separator's split bus.
            float tin = m.In[Math.Min(t, 7)];
            if (t == 0) { Line(ctx, new Point(src.Right - 20, src.Y), new Point(src.Right - 20, yDirect), tin); Line(ctx, new Point(src.Right - 20, yDirect), new Point(b.Center.X, yDirect), tin); Line(ctx, new Point(b.Center.X, yDirect), new Point(b.Center.X, b.Y), tin); }
            else { Line(ctx, new Point(b.Center.X, ySplit), new Point(b.Center.X, b.Y), tin); }
            // Out: into the sum bus, broken while it does not sound.
            float tout = m.Peak[Math.Min(t, 7)];
            if (sounding) Line(ctx, new Point(b.Center.X - 8, b.Bottom), new Point(b.Center.X - 8, ySum), tout);
            else Ui.Text(ctx, "✕", b.Center.X - 8, b.Bottom + 4, 9, Ui.Amber, Ui.SansBold, Ui.Align.Center);
            // Send: the tap, to the analysers it feeds.
            var an = S.AnalysersOf(t);
            if (an.Length > 0)
            {
                Patch(ctx, new Point(b.Center.X + 10, b.Bottom), new Point(b.Center.X + 10, ySend), m.Return[Math.Min(t, 7)], 0, send: true);
                Ui.Text(ctx, string.Join(" ", an), b.Center.X + 14, ySend - 12, 7, Ui.Amber, Ui.SansBold);
            }
        }
        if (stems)
        {   // the split bus from the separator's output across the stems
            var last = _channels[^1].R;
            Line(ctx, new Point(sep.Right - 16, sep.Y), new Point(sep.Right - 16, ySplit), stemsIn);
            Line(ctx, new Point(sep.Right - 16, ySplit), new Point(last.Center.X, ySplit), stemsIn);
            Ui.Text(ctx, "SPLIT", sep.Right - 12, ySplit + 1, 7, Ui.Label, Ui.SansBold);
        }
        if (n > 0)
        {
            Ui.Text(ctx, "DIRETO", src.Right - 16, yDirect + 1, 7, Ui.Label, Ui.SansBold);
            double first = _channels[0].R.Center.X - 8;
            Line(ctx, new Point(first, ySum), new Point(right - 16, ySum), master);
            Ui.Text(ctx, "BUS DE SOMA ▶", right - 22, ySum - 11, 7, Ui.Label, Ui.SansBold, Ui.Align.Right);
            Patch(ctx, new Point(_channels[0].R.Center.X + 10, ySend), new Point(right - 16, ySend), -120, 0, send: true);
            Ui.Text(ctx, "ENVIO DE ANÁLISE · pós-insertos, pré-fader", first + 4, ySend + 3, 7, Ui.Label, Ui.SansBold);
        }

        // Console, master, output; analysers, score.
        var con = new Rect(right, yBox, 96, hBox);
        Box(ctx, con, "DS-M", "CONSOLE", has);
        Ui.Text(ctx, $"{n} canais", con.X + 7, con.Y + 17, 9, Ui.LabelBright, Ui.Mono);
        Line(ctx, new Point(right - 16, ySum), new Point(right - 16, con.Center.Y), master);
        Patch(ctx, new Point(right - 16, con.Center.Y), new Point(con.X, con.Center.Y), master, 0, thin: true, led: false);
        var mas = new Rect(con.Right + 24, yBox, 104, hBox);
        var mm = S.Mix.Master;
        Box(ctx, mas, "", "MASTER", has);
        Ui.Text(ctx, (mm.EqOn ? "EQ " : "") + (mm.CompOn ? "COMP " : "") + (mm.LimiterOn ? "DL-6" : ""), mas.X + 7, mas.Y + 17, 8, Ui.LabelBright, Ui.Mono);
        Patch(ctx, new Point(con.Right, con.Center.Y), new Point(mas.X, mas.Center.Y), master, 6);
        var outB = new Rect(mas.Right + 24, yBox, r.Right - mas.Right - 24, hBox);
        Box(ctx, outB, "", "SAÍDA", S.Player?.Playing == true);
        Ui.Text(ctx, "fone · bounce", outB.X + 7, outB.Y + 17, 8, Ui.LabelBright, Ui.Mono);
        Patch(ctx, new Point(mas.Right, mas.Center.Y), new Point(outB.X, outB.Center.Y), master, 6);
        foreach (var b in new[] { con, mas, outB }) { Jack(ctx, new Point(b.X, b.Center.Y)); if (b != outB) Jack(ctx, new Point(b.Right, b.Center.Y)); }

        var da = new Rect(right, ySend - 14, 224, 28);
        var used = Enumerable.Range(0, n).SelectMany(S.AnalysersOf).ToHashSet();
        Box(ctx, da, "DA", "ANÁLISE", S.Result is not null || S.Running);
        string[] all = S.Choir ? ["DA-K", "DA-C", "DA-B", "DA-T"] : ["DA-N", "DA-C", "DA-B", "DA-T"];
        string[] what = [S.Choir ? "coro" : "notas", "acordes", "baixo", "batidas"];
        for (int i = 0; i < all.Length; i++)
        {
            double ax = da.X + 74 + i * 38;
            Ui.Led(ctx, new Point(ax, da.Y + 9), 2.4, used.Contains(all[i]) && S.Result is not null, Ui.Green, used.Contains(all[i]) ? Ui.AmberOff : Ui.GreenOff);
            Ui.Text(ctx, what[i], ax, da.Y + 14, 7, used.Contains(all[i]) ? Ui.LabelBright : Ui.Label, Ui.SansBold, Ui.Align.Center);
        }
        Patch(ctx, new Point(right - 16, ySend), new Point(da.X, ySend), -120, 0, send: true);
        _score = new Rect(da.Right + 24, da.Y, r.Right - da.Right - 24, 28);
        Box(ctx, _score, "", "SCORE", S.Result is not null);
        Ui.Text(ctx, S.Result is not null ? "partitura pronta" : "—", _score.X + 7, _score.Y + 14, 8, Ui.LabelBright, Ui.Mono);
        Patch(ctx, new Point(da.Right, da.Center.Y), new Point(_score.X, _score.Center.Y), S.Result is not null ? -20 : -120, 0, send: true);
    }

    /// A bus run: a straight cable, no LED.
    static void Line(DrawingContext ctx, Point a, Point b, float db) => Patch(ctx, a, b, db, 0, thin: true, led: false);

    protected override void Pressed(PointerPressedEventArgs e, Point p)
    {
        foreach (var (b, t) in _channels)
            if (b.Contains(p) && S.Current is not null) { S.Selected = t; S.Select(); return; }
        if (_score.Contains(p) && S.Result is not null) S.OpenScore();
    }
}

/// DS-V: measuring tubes on the master (and the selected channel for the spectrum) — phosphor
/// CRTs in their bezels: vectorscope with the phase correlation, oscilloscope, spectrum, loudness
/// history with the integrated loudness, range and true peak on plasma.
public sealed class MeterUnit : StudioUnit
{
    public MeterUnit(StudioState s) : base(s, "DS-V", "MEDIÇÃO") { Width = 300; }

    static ImmutableGradientStop G(double o, uint c) => new(o, Color.FromUInt32(c));
    static RelativePoint Rel(double x, double y) => new(x, y, RelativeUnit.Relative);
    static readonly IBrush BezelBrush = new ImmutableLinearGradientBrush([G(0, 0xFF3A3E44), G(0.5, 0xFF1A1C1F), G(1, 0xFF2A2D32)], startPoint: Rel(0, 0), endPoint: Rel(1, 1));
    static readonly IBrush Glass = new ImmutableRadialGradientBrush([G(0, 0xFF0F2A1C), G(0.7, 0xFF06140D), G(1, 0xFF010403)],
        center: Rel(0.5, 0.5), gradientOrigin: Rel(0.5, 0.45), radiusX: new RelativeScalar(0.7, RelativeUnit.Relative), radiusY: new RelativeScalar(0.7, RelativeUnit.Relative));
    static readonly IBrush Reflection = new ImmutableLinearGradientBrush([G(0, 0x22FFFFFF), G(0.35, 0x06FFFFFF), G(0.36, 0x00FFFFFF)], startPoint: Rel(0, 0), endPoint: Rel(0.6, 1));
    static readonly IPen Graticule = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF173A28)), 1);
    static readonly IPen GraticuleDash = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF1F4A33)), 1, new ImmutableDashStyle([2, 3], 0));
    static readonly IPen Beam = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFFB8FFD6)), 1.1);
    static readonly IPen BeamGlow = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0x3352FF9A)), 4.5);
    static readonly IPen BeamDim = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0x9952FF9A)), 1);
    static readonly IBrush BeamFill = new ImmutableLinearGradientBrush([G(0, 0x7052FF9A), G(1, 0x1052FF9A)], startPoint: Rel(0, 0), endPoint: Rel(0, 1));
    static readonly IBrush Ink = new ImmutableSolidColorBrush(Color.FromUInt32(0xFF3E8A60));
    static readonly IPen Scan = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0x1C000000)), 1);

    /// A CRT: bezel, curved glass with its vignette, the trace, scanlines and the reflection.
    static void Tube(DrawingContext ctx, Rect r, string label, Action<Rect> trace)
    {
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF0B0C0D)), null, r.Translate(new Vector(0, 2)), 10, 10);
        ctx.DrawRectangle(BezelBrush, Ui.FaceEdge, r, 10, 10);
        var g = r.Deflate(6);
        ctx.DrawRectangle(Glass, null, g, 14, 14);
        using (ctx.PushClip(new RoundedRect(g, 14)))
        {
            trace(g);
            for (double y = g.Y + 1; y < g.Bottom; y += 2.5) ctx.DrawLine(Scan, new Point(g.X, y), new Point(g.Right, y));
            ctx.DrawRectangle(Reflection, null, g);
        }
        Ui.Text(ctx, label, r.X + 12, r.Bottom + 2, 7, Ui.Label, Ui.SansBold);
    }

    protected override void DrawContent(DrawingContext ctx, Rect r)
    {
        var sd = S.Scopes;
        var m = S.Meters;
        double x = r.X, y = r.Y + 2, half = (r.Width - 8) / 2;

        // Vectorscope + correlation; oscilloscope.
        var vec = new Rect(x, y, half, half);
        Tube(ctx, vec, "VETOR · L/R", g => Vector(ctx, g, sd));
        var osc = new Rect(x + half + 8, y, half, half);
        Tube(ctx, osc, "OSCILOSCÓPIO · L / R", g => Oscilloscope(ctx, g, sd));
        y = vec.Bottom + 14;
        var bar = new Rect(x + 14, y, r.Width - 28, 10);
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF0B0C0D)), Ui.FaceEdge, bar, 2, 2);
        for (int i = 0; i <= 8; i++) ctx.DrawLine(Graticule, new Point(bar.X + bar.Width * i / 8, bar.Y), new Point(bar.X + bar.Width * i / 8, bar.Bottom));
        double cx = bar.X + bar.Width * (sd.Correlation + 1) / 2;
        ctx.DrawRectangle(sd.Correlation < 0 ? Ui.Red : Ui.Green, null, new Rect(Math.Min(cx, bar.Center.X), bar.Y + 2, Math.Max(2, Math.Abs(cx - bar.Center.X)), bar.Height - 4));
        Ui.Text(ctx, "−1", bar.X - 12, bar.Y - 1, 8, Ui.Label, Ui.Mono);
        Ui.Text(ctx, "+1", bar.Right + 2, bar.Y - 1, 8, Ui.Label, Ui.Mono);
        Ui.Text(ctx, $"CORRELAÇÃO {sd.Correlation:+0.00;-0.00}", bar.Center.X, bar.Bottom + 2, 7, sd.Correlation < 0 ? Ui.Red : Ui.Label, Ui.SansBold, Ui.Align.Center);
        y = bar.Bottom + 16;

        // Spectrum of the master or the selected channel.
        string chName = S.Current is null ? "—" : S.NameOf(S.Selected) == "mix" ? "MIX" : S.NameOf(S.Selected).ToUpperInvariant();
        Key(ctx, new Rect(x, y, 90, 20), "MASTER", () => sd.SpectrumOfTrack = false, !sd.SpectrumOfTrack, Ui.Green);
        Key(ctx, new Rect(x + 96, y, 110, 20), $"CANAL {S.Selected + 1}", () => sd.SpectrumOfTrack = true, sd.SpectrumOfTrack, Ui.Green, S.Current is not null);
        y += 26;
        double specH = Math.Max(90, (r.Bottom - y - 150) * 0.5);
        Tube(ctx, new Rect(x, y, r.Width, specH), sd.SpectrumOfTrack ? $"ESPECTRO · CANAL {S.Selected + 1} {chName} · TAP" : "ESPECTRO · MASTER", g => Spectrum(ctx, g, sd));
        y += specH + 14;

        // Loudness.
        double loudH = Math.Max(80, r.Bottom - y - 76);
        Tube(ctx, new Rect(x, y, r.Width, loudH), "LOUDNESS · 30 s · M fino, S forte", g => Loudness(ctx, g, sd));
        y += loudH + 14;
        static string L(float v) => v <= -70 ? "--.-" : $"{v:0.0}";
        double rx = x;
        rx += Readout(ctx, rx, y, L(m.LufsI), 5, 1.9, "INTEGRADO") + 6;
        rx += Readout(ctx, rx, y, L(m.LufsS), 5, 1.4, "CURTO 3 s") + 6;
        rx += Readout(ctx, rx, y, L(m.LufsM), 5, 1.4, "MOMENT.") + 6;
        double ry = y + 34;
        rx = x;
        rx += Readout(ctx, rx, ry, m.TruePeak <= -70 ? "--.-" : $"{m.TruePeak:0.0}", 5, 1.4, "TRUE PEAK") + 6;
        rx += Readout(ctx, rx, ry, $"{m.Lra:0.0}", 4, 1.4, "LRA") + 6;
        Key(ctx, new Rect(r.Right - 60, ry, 60, 20), "ZERAR", S.ResetLoudness, false, Ui.Amber, S.Player is not null);
        if (m.TruePeak > -1) Ui.Led(ctx, new Point(rx + 6, ry + 8), 3, true, Ui.Red, Ui.RedOff);
    }

    static void Vector(DrawingContext ctx, Rect g, ScopeData sd)
    {
        var c = g.Center;
        double R = Math.Min(g.Width, g.Height) * 0.42;
        ctx.DrawEllipse(null, Graticule, c, R, R);
        ctx.DrawLine(GraticuleDash, new Point(c.X, c.Y - R), new Point(c.X, c.Y + R));
        ctx.DrawLine(GraticuleDash, new Point(c.X - R, c.Y), new Point(c.X + R, c.Y));
        double d = R * 0.7071;
        ctx.DrawLine(Graticule, new Point(c.X - d, c.Y - d), new Point(c.X + d, c.Y + d));
        ctx.DrawLine(Graticule, new Point(c.X + d, c.Y - d), new Point(c.X - d, c.Y + d));
        Ui.Text(ctx, "L", c.X - d - 8, c.Y - d - 10, 8, Ink, Ui.Mono);
        Ui.Text(ctx, "R", c.X + d + 2, c.Y - d - 10, 8, Ink, Ui.Mono);
        Ui.Text(ctx, "M", c.X + 3, g.Y + 3, 8, Ink, Ui.Mono);
        int n = sd.MasterCount;
        if (n < 2) return;
        double peak = 1e-4;
        for (int i = 0; i < n; i++) peak = Math.Max(peak, Math.Max(Math.Abs(sd.Master[2 * i]), Math.Abs(sd.Master[2 * i + 1])));
        double k = R / (peak * 1.4142);   // auto gain: the loudest sample reaches the ring
        var geo = new StreamGeometry();
        using (var gc = geo.Open())
        {
            int from = Math.Max(0, n - 2048);
            for (int i = from; i < n; i++)
            {
                double l = sd.Master[2 * i], r = sd.Master[2 * i + 1];
                var p = new Point(c.X + (r - l) * k, c.Y - (l + r) * k);   // mono stands up the M axis
                if (i == from) gc.BeginFigure(p, false); else gc.LineTo(p);
            }
            gc.EndFigure(false);
        }
        ctx.DrawGeometry(null, BeamGlow, geo);
        ctx.DrawGeometry(null, BeamDim, geo);
    }

    static void Oscilloscope(DrawingContext ctx, Rect g, ScopeData sd)
    {
        for (int i = 1; i < 8; i++) ctx.DrawLine(Graticule, new Point(g.X + g.Width * i / 8, g.Y), new Point(g.X + g.Width * i / 8, g.Bottom));
        for (int i = 1; i < 4; i++) ctx.DrawLine(i == 2 ? Graticule : GraticuleDash, new Point(g.X, g.Y + g.Height * i / 4), new Point(g.Right, g.Y + g.Height * i / 4));
        int n = sd.MasterCount, window = 1024;
        if (n < window + 2) return;
        // Triggered on the newest rising zero crossing of L that leaves a window after it.
        int start = n - window;
        for (int i = n - window - 1; i > Math.Max(1, n - 3 * window); i--)
            if (sd.Master[2 * (i - 1)] < 0 && sd.Master[2 * i] >= 0) { start = i; break; }
        double peak = 1e-4;
        for (int i = start; i < start + window; i++) peak = Math.Max(peak, Math.Max(Math.Abs(sd.Master[2 * i]), Math.Abs(sd.Master[2 * i + 1])));
        for (int ch = 0; ch < 2; ch++)
        {
            double mid = g.Y + g.Height * (ch == 0 ? 0.25 : 0.75), amp = g.Height * 0.22 / peak;
            var geo = new StreamGeometry();
            using (var gc = geo.Open())
            {
                for (int i = 0; i < window; i += 2)
                {
                    var p = new Point(g.X + g.Width * i / window, mid - sd.Master[2 * (start + i) + ch] * amp);
                    if (i == 0) gc.BeginFigure(p, false); else gc.LineTo(p);
                }
                gc.EndFigure(false);
            }
            ctx.DrawGeometry(null, BeamGlow, geo);
            ctx.DrawGeometry(null, Beam, geo);
        }
        Ui.Text(ctx, $"{window * 1000 / sd.Rate:0} ms", g.Right - 6, g.Bottom - 14, 8, Ink, Ui.Mono, Ui.Align.Right);
    }

    static void Spectrum(DrawingContext ctx, Rect g, ScopeData sd)
    {
        const double lo = -90, hi = 0;
        double X(int b) => g.X + 4 + (g.Width - 8) * b / (ScopeData.Bands - 1.0);
        double Y(double db) => g.Bottom - 6 - (g.Height - 14) * Math.Clamp((db - lo) / (hi - lo), 0, 1);
        foreach (var (hz, label) in new[] { (100.0, "100"), (1000.0, "1k"), (10000.0, "10k") })
        {
            double bx = g.X + 4 + (g.Width - 8) * Math.Log2(hz / 20) * 12 / (ScopeData.Bands - 1.0);
            ctx.DrawLine(Graticule, new Point(bx, g.Y), new Point(bx, g.Bottom));
            Ui.Text(ctx, label, bx + 2, g.Y + 3, 8, Ink, Ui.Mono);
        }
        for (double db = -20; db > lo; db -= 20) { ctx.DrawLine(GraticuleDash, new Point(g.X, Y(db)), new Point(g.Right, Y(db))); Ui.Text(ctx, $"{db:0}", g.Right - 6, Y(db) - 11, 7, Ink, Ui.Mono, Ui.Align.Right); }
        var fill = new StreamGeometry();
        var line = new StreamGeometry();
        using (var f = fill.Open())
        using (var l = line.Open())
        {
            f.BeginFigure(new Point(X(0), g.Bottom), true);
            for (int b = 0; b < ScopeData.Bands; b++)
            {
                var p = new Point(X(b), Y(sd.Spectrum[b]));
                f.LineTo(p);
                if (b == 0) l.BeginFigure(p, false); else l.LineTo(p);
            }
            f.LineTo(new Point(X(ScopeData.Bands - 1), g.Bottom));
            f.EndFigure(true);
            l.EndFigure(false);
        }
        ctx.DrawGeometry(BeamFill, null, fill);
        ctx.DrawGeometry(null, BeamGlow, line);
        ctx.DrawGeometry(null, Beam, line);
        for (int b = 0; b < ScopeData.Bands; b += 2)
            if (sd.Hold[b] > lo) ctx.DrawRectangle(Ui.Amber, null, new Rect(X(b) - 1, Y(sd.Hold[b]) - 1, 2.2, 1.6));
    }

    static void Loudness(DrawingContext ctx, Rect g, ScopeData sd)
    {
        const double lo = -50, hi = 0;
        double Y(double v) => g.Bottom - 6 - (g.Height - 12) * Math.Clamp((v - lo) / (hi - lo), 0, 1);
        foreach (var (v, label) in new[] { (-23.0, "−23 EBU"), (-14.0, "−14 streaming") })
        {
            ctx.DrawLine(GraticuleDash, new Point(g.X, Y(v)), new Point(g.Right, Y(v)));
            Ui.Text(ctx, label, g.X + 6, Y(v) - 11, 7, Ink, Ui.Mono);
        }
        for (int s = 5; s < 30; s += 5) ctx.DrawLine(Graticule, new Point(g.Right - g.Width * s / 30, g.Bottom - 4), new Point(g.Right - g.Width * s / 30, g.Bottom));
        foreach (var (data, pen) in new[] { (sd.Momentary, BeamDim), (sd.ShortTerm, Beam) })
        {
            var geo = new StreamGeometry();
            bool open = false;
            using (var gc = geo.Open())
            {
                for (int i = 0; i < ScopeData.History; i++)
                {
                    float v = data[(sd.HistoryHead + i) % ScopeData.History];
                    if (v <= lo) { if (open) gc.EndFigure(false); open = false; continue; }
                    var p = new Point(g.X + g.Width * i / (ScopeData.History - 1.0), Y(v));
                    if (!open) { gc.BeginFigure(p, false); open = true; } else gc.LineTo(p);
                }
                if (open) gc.EndFigure(false);
            }
            if (pen == Beam) ctx.DrawGeometry(null, BeamGlow, geo);
            ctx.DrawGeometry(null, pen, geo);
        }
    }
}
