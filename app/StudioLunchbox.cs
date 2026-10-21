using System.Diagnostics;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Dissonancia;

// STUDIO S10 — DS-C, the session's lunchbox: a chassis of slots where modules sit (built-in units
// from the drawer, VST3 plugins dragged in from the effect browser), each with an IN and an OUT
// jack on its lip, and under it a patchbay with every track's SAI (its signal out) and VOLTA (its
// return). Patch cables between jacks route the tracks: VOZ SAI -> EQ -> COMP -> VOZ VOLTA; a track
// whose SAI has no cable goes straight through (normalled); one that ends at another track's VOLTA
// goes into that track. Cables are ropes: a plug is picked out of its jack with the mouse, the cable
// swings and hangs under its weight, and drops into a jack (or hangs loose anywhere else). Where a
// module sits does not change the sound; the cables do (Patchbay.Route).
public sealed partial class ChainUnit
{
    const double SlotW = 62, LipH = 26, PatchH = 64, ColW = 74;
    public const int SlotCount = 16;

    static readonly Dictionary<string, (string Model, int Span)> Face = new()
    {
        ["trim"] = ("DS-1", 2), ["filter"] = ("DF-2", 2), ["gate"] = ("DG-3", 2), ["eq"] = ("DQ-4", 3), ["comp"] = ("DC-5", 3), ["vst"] = ("DV", 2),
    };
    public static int SpanOf(BoxModule m) => Face.TryGetValue(m.Type, out var f) ? f.Span : 2;

    /// A patch cable as a rope of point masses (Verlet), its ends pinned to jacks or to the hand.
    sealed class Rope
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

    readonly Dictionary<CableLink, Rope> _ropes = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<string, Point> _jacks = [];
    readonly Dictionary<string, double> _modX = [];
    readonly List<(BoxModule M, Rect Box)> _modules = [];
    readonly Stopwatch _clock = Stopwatch.StartNew();
    double _lastT, _floor, _s0, _chassisTop, _chassisBottom;
    string? _ropesFor;

    // In the hand: a cable (its fixed end's jack, which end that is, its colour) or a module.
    (Rope Rope, string Fixed, bool FixedIsOut, int Colour)? _cable;
    readonly List<(Rope Rope, string Fixed, bool FixedIsOut, int Colour)> _loose = [];
    BoxModule? _heldModule;
    Point _grab, _hand;
    // A plugin being dragged in from the browser: where it would land.
    PluginHost.Plugin? _incoming;
    Point _incomingAt;

    static bool IsOut(string jack) => jack.EndsWith(":out");
    static readonly uint[] CableColours = [0xFFE2462F, 0xFFF2C12E, 0xFF2F8FE2, 0xFF3DBE6B, 0xFFF08A24, 0xFFD9D9D9, 0xFFB45CE0];

    static int NextColour(TakeMix mix)
    {
        for (int i = 0; i < CableColours.Length; i++) if (!mix.Patch.Any(k => k.Colour == i)) return i;
        return mix.Patch.Count;
    }

    /// The nearest slot to `want` where a module `span` wide fits without overlapping another.
    static int? FreeAt(TakeMix mix, BoxModule? self, int span, int want)
    {
        bool Fits(int at) => at >= 0 && at + span <= SlotCount && mix.Box.Where(m => m != self).All(m => at + span <= m.Slot || m.Slot + SpanOf(m) <= at);
        for (int d = 0; d < SlotCount; d++)
        {
            if (Fits(want - d)) return want - d;
            if (Fits(want + d)) return want + d;
        }
        return null;
    }

    int SlotAt(double x, int span) => (int)Math.Round((x - _s0) / SlotW - span / 2.0);

    /// Draws the lunchbox and its patchbay; returns where the analyser goes (x) and the modules' row.
    (double X, double Y, double H) DrawBay(DrawingContext ctx, Rect r, int sel)
    {
        var mix = S.Mix;
        bool has = S.Current is not null;
        if (_ropesFor != S.Current) { _ropes.Clear(); _loose.Clear(); _modX.Clear(); _cable = null; _heldModule = null; _ropesFor = S.Current; }
        double now = _clock.Elapsed.TotalSeconds, dt = Math.Clamp(now - _lastT, 0.001, 0.04);
        _lastT = now;
        var names = Enumerable.Range(0, S.Tracks).Select(S.NameOf).ToList();
        var routes = Patchbay.Route(mix.Patch, mix.Box, names, S.ModuleName);
        var onTrack = routes.SelectMany(kv => kv.Value.Modules.Select(m => (m, t: names.IndexOf(kv.Key)))).GroupBy(x => x.m).ToDictionary(g => g.Key, g => g.First().t);

        // Header: the selected track's route, how to use the box.
        var sc = S.Channel(sel);
        var sr = routes.GetValueOrDefault(S.NameOf(sel));
        Ui.Led(ctx, new Point(r.X + 96, r.Y + 8), 3.5, true, sc.RouteError is null ? Ui.Green : Ui.Red, Ui.GreenOff);
        string tn = S.ModuleName(S.NameOf(sel) + ".sai");
        string route = sc.RouteError is { } err ? $"{tn}: SEM SOM — {err}"
            : sr is null || sr.Modules.Count == 0 && sr.SendTo is null ? $"{tn}: SAI ▶ VOLTA (direto, sem cabo)"
            : $"{tn}: SAI ▶ " + string.Join(" ▶ ", sr.Modules.Select(S.ModuleName)) + $" ▶ {(sr.SendTo is { } to ? S.ModuleName(to + ".sai") + " " : "")}VOLTA"
              + (sr.First is not null ? $"   (∥ {S.ModuleName(sr.First)}…{S.ModuleName(sr.Last!)})" : "");
        Ui.Text(ctx, route.Length > 110 ? route[..110] : route, r.X + 106, r.Y + 2, 9, sc.RouteError is null ? Ui.LabelBright : Ui.Red, Ui.Mono);
        Ui.Text(ctx, "pegue um plugue e leve a outro jack · arraste um módulo pelo topo · fora da caixa = tira · plugins: arraste da lista de efeitos", r.X + 106, r.Y + 15, 8, Ui.Label, Ui.Sans);

        double y = r.Y + 36, h = r.Height - 36 - LipH - PatchH - 40;
        double bx = r.X;
        var chassis = new Rect(bx, y - 8, SlotCount * SlotW + 12, h + LipH + 14);
        (_chassisTop, _chassisBottom) = (chassis.Y, chassis.Bottom);
        _floor = r.Bottom + 6;
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF15171A)), new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF050606)), 2), chassis, 6, 6);
        double s0 = _s0 = bx + 6;
        for (int i = 0; i < SlotCount; i++)
        {
            var slotR = new Rect(s0 + i * SlotW + 1, y, SlotW - 2, h + LipH);
            ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF0E0F11)), new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF1F2226)), 1), slotR, 2, 2);
            ctx.DrawEllipse(Ui.Screw, null, new Point(slotR.Center.X, y - 4), 1.8, 1.8);
            ctx.DrawEllipse(Ui.Screw, null, new Point(slotR.Center.X, y + h + LipH + 3), 1.8, 1.8);
        }
        _jacks.Clear();

        // The patchbay: per track its tape, SAI on top, VOLTA under it.
        var bay = new Rect(bx, chassis.Bottom + 6, Math.Max(ColW * names.Count + 70, 200), PatchH);
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF1E2024)), Ui.FaceEdge, bay, 4, 4);
        Ui.Text(ctx, "PATCHBAY", bay.X + 8, bay.Y + 4, 8, Ui.Label, Ui.SansBold);
        Ui.Text(ctx, "SAI", bay.X + 8, bay.Y + 22, 7, Ui.Label, Ui.SansBold);
        Ui.Text(ctx, "VOLTA", bay.X + 8, bay.Y + 44, 7, Ui.Label, Ui.SansBold);
        for (int t = 0; t < names.Count; t++)
        {
            double cx = bay.X + 70 + t * ColW + ColW / 2;
            string label = S.ModuleName(names[t] + ".sai");
            Ui.Text(ctx, label.Length > 8 ? label[..8] : label, cx, bay.Y + 4, 8, t == sel ? Ui.Amber : Ui.LabelBright, Ui.SansBold, Ui.Align.Center);
            _jacks[Patchbay.Sai(names[t])] = new Point(cx, bay.Y + 26);
            _jacks[Patchbay.Volta(names[t])] = new Point(cx, bay.Y + 48);
            bool normalled = !mix.Patch.Any(k => k.From == Patchbay.Sai(names[t]));
            if (normalled) ctx.DrawLine(new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF3E6B53)), 1.2, new ImmutableDashStyle([2, 2], 0)), new Point(cx, bay.Y + 32), new Point(cx, bay.Y + 42));
        }

        // Levels: a track's SAI at its input; a module live after the last insert that is on, by its
        // position; before it everything is rendered, its level the track's input.
        float Lv(string node)
        {
            if (node.EndsWith(".sai")) { int t = names.IndexOf(node[..^4]); return t < 0 ? -90 : S.Meters.Stage[Math.Min(t, 7), 0]; }
            if (!onTrack.TryGetValue(node, out int tt)) return -90;
            var c = S.Channel(tt);
            var live = c.Segments()[^1];
            string key = mix.Box.FirstOrDefault(m => m.Id == node)?.Type ?? "";
            int mt = Math.Min(tt, 7);
            return live.Contains(key) ? S.Meters.Stage[mt, live.IndexOf(key) + 1] : S.Meters.Stage[mt, 0];
        }

        // Modules: they glide into their slot; the one in the hand follows it, lifted, with a shadow.
        _modules.Clear();
        foreach (var m in has ? mix.Box.OrderBy(m => m == _heldModule).ToList() : [])
        {
            double target = s0 + m.Slot * SlotW + 1;
            double mx = _modX.TryGetValue(m.Id, out var cur) ? cur + (target - cur) * Math.Min(1, dt * 18) : target;
            _modX[m.Id] = mx;
            var box = new Rect(mx, y, SpanOf(m) * SlotW - 2, h);
            if (m == _heldModule)
            {
                box = new Rect(_hand.X - _grab.X, _hand.Y - _grab.Y - 6, box.Width, h);
                ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0x88000000)), null, box.Translate(new Vector(7, 10)).Inflate(new Thickness(0, 0, 0, LipH)), 5, 5);
            }
            var lip = new Rect(box.X, box.Bottom, box.Width, LipH);
            ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF1E2024)), Ui.FaceEdge, lip, 0, 0);
            double ky = box.Y + h / 2 - 6, fy = box.Y + h - 22;
            var b = box;
            void Veil(bool on) { if (!on && has) { ctx.DrawRectangle(BypassVeil, null, b, 4, 4); Ui.Text(ctx, "BYPASS", b.Center.X + 22, fy - 6, 8, Ui.Amber, Ui.SansBold); } }
            int mt = Math.Min(onTrack.GetValueOrDefault(m.Id, sel), 7);
            if (m.Vst is { } v) DrawInsert(ctx, box, m, v, fy);
            else DrawUnit(ctx, box, m.Type, m.P, mt, ky, fy, has, () => S.MixChanged(), Veil, Lv(m.Id), S.ModuleName(m.Id));
            if (!onTrack.ContainsKey(m.Id)) Ui.Text(ctx, "sem cabo", box.Center.X, box.Y + 20, 7, Ui.Amber, Ui.SansBold, Ui.Align.Center);
            else Ui.Text(ctx, S.ModuleName(names[onTrack[m.Id]] + ".sai"), box.Center.X, box.Y + 20, 7, Ui.Green, Ui.SansBold, Ui.Align.Center);
            var jin = new Point(lip.X + 14, lip.Center.Y);
            var jout = new Point(lip.Right - 14, lip.Center.Y);
            Ui.Text(ctx, "IN", jin.X + 10, lip.Y + 7, 7, Ui.Label, Ui.SansBold);
            Ui.Text(ctx, "OUT", jout.X - 10, lip.Y + 7, 7, Ui.Label, Ui.SansBold, Ui.Align.Right);
            _jacks[m.Id + ":in"] = jin;
            _jacks[m.Id + ":out"] = jout;
            _modules.Add((m, box));
        }
        foreach (var j in _jacks.Values) Jack(ctx, j);
        if (_pullDemo && _modules.Count > 0) PullNow();

        // A plugin on its way in from the browser: the slots it would take.
        if (_incoming is not null && chassis.Inflate(30).Contains(_incomingAt) && FreeAt(mix, null, 2, SlotAt(_incomingAt.X, 2)) is int inAt)
            ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0x33FFB000)), new ImmutablePen((IImmutableBrush)Ui.Amber, 2, new ImmutableDashStyle([4, 3], 0)),
                new Rect(s0 + inAt * SlotW + 1, y, 2 * SlotW - 2, h + LipH), 4, 4);

        // Cables: plugged ones (each a rope between its jacks), loose ones, the one in the hand.
        foreach (var k in mix.Patch.ToList())
        {
            if (k.Colour < 0) k.Colour = NextColour(mix);
            if (!_jacks.TryGetValue(k.From, out var a) || !_jacks.TryGetValue(k.To, out var e)) continue;
            if (!_ropes.TryGetValue(k, out var rope)) _ropes[k] = rope = new Rope(a, e);
            rope.Step(dt, a, e, _floor);
            DrawCable(ctx, rope, CableColours[k.Colour % CableColours.Length], Lv(Patchbay.Node(k.From)), true, true);
        }
        foreach (var k in _ropes.Keys.Where(k => !mix.Patch.Contains(k)).ToList()) _ropes.Remove(k);
        foreach (var (rope, fix, _, col) in _loose)
            if (_jacks.TryGetValue(fix, out var a)) { rope.Step(dt, a, null, _floor); DrawCable(ctx, rope, CableColours[col % CableColours.Length], -90, true, true); }
        _loose.RemoveAll(l => !_jacks.ContainsKey(l.Fixed));
        if (_cable is { } held && _jacks.TryGetValue(held.Fixed, out var fa))
        {
            held.Rope.Step(dt, fa, _hand, _floor);
            DrawCable(ctx, held.Rope, CableColours[held.Colour % CableColours.Length], -90, true, true);
            if (NearJack(_hand, !held.FixedIsOut) is { } target) ctx.DrawEllipse(null, new ImmutablePen((IImmutableBrush)Ui.Green, 2), _jacks[target], 10, 10);
        }

        // The parallel path's mix: a knob by the jack where it comes back (the selected track's).
        if (sc.RouteError is null && sr?.Last is { } last)
        {
            int li = sr.Modules.IndexOf(last);
            string merge = li + 1 < sr.Modules.Count ? sr.Modules[li + 1] + ":in" : Patchbay.Volta(sr.SendTo ?? S.NameOf(sel));
            if (_jacks.TryGetValue(merge, out var mj))
            {
                var kp = new Point(mj.X + 28, mj.Y + (merge.EndsWith(".volta:in") ? 0 : 30));
                Knob(ctx, kp, 9, "", "", sc.ParallelMix, n => { sc.ParallelMix = (float)Math.Round(n, 2); S.MixChanged(); }, true);
                Ui.Text(ctx, $"∥ {sc.ParallelMix * 100:0} %", kp.X + 13, kp.Y - 6, 8, Ui.Amber, Ui.SansBold);
            }
        }

        // The drawer: built-in modules, as many as fit; a click puts one in the first free slot.
        double dx = bay.Right + 16, dy = bay.Y + 8;
        Ui.Text(ctx, "GAVETA", dx, dy - 2, 8, Ui.Label, Ui.SansBold);
        dx += 52;
        foreach (var u in ChannelMix.Units)
        {
            string label = "+ " + StudioState.UnitNames[u];
            double w = label.Length * 7 + 18;
            Key(ctx, new Rect(dx, dy - 6, w, 20), label, () =>
            {
                if (FreeAt(mix, null, Face[u].Span, SlotCount) is int f) S.AddModule(u, f);
                else S.Status = "lunchbox cheio: tire um módulo antes";
            }, false, Ui.Amber, has, withLed: false);
            dx += w + 4;
        }
        return (chassis.Right + 14, y, h);
    }

    /// A rope drawn as a patch cable: jacket, coloured core, pulses running with the signal, plugs.
    void DrawCable(DrawingContext ctx, Rope rope, uint colour, float db, bool plugA, bool plugB)
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
            double phase = _clock.Elapsed.TotalSeconds * 80 % 24;
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
    static void Plug(DrawingContext ctx, Point tip, Point toward, Color col)
    {
        var d = toward - tip;
        double len = Math.Max(1e-6, Math.Sqrt(d.X * d.X + d.Y * d.Y));
        var u = d / len;
        ctx.DrawLine(new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFFB9BEC6)), 7, lineCap: PenLineCap.Round), tip, tip + u * 4);
        ctx.DrawLine(new ImmutablePen(new ImmutableSolidColorBrush(col), 9, lineCap: PenLineCap.Round), tip + u * 5, tip + u * 13);
        ctx.DrawEllipse(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF2A2D32)), null, tip, 3.2, 3.2);
    }

    /// The jack under a point, of the kind wanted (an output, or an input), with room for a cable.
    string? NearJack(Point p, bool wantOut)
    {
        foreach (var (id, j) in _jacks)
        {
            if (IsOut(id) != wantOut || (p - j).X * (p - j).X + (p - j).Y * (p - j).Y > 14 * 14) continue;
            if (S.Mix.Patch.Count(k => k.From == id || k.To == id) < 2) return id;
        }
        return null;
    }

    /// Screenshot tool: the cable out of the first module that feeds another pulled and let go (it
    /// hangs; that path is silent). On screen only: nothing saved to the take.
    public void PullDemo() => _pullDemo = true;
    bool _pullDemo;
    void PullNow()
    {
        _pullDemo = false;
        var mix = S.Mix;
        if (mix.Patch.FirstOrDefault(k => !k.From.Contains(".sai") && !k.To.Contains(".volta")) is not { } k || !_jacks.ContainsKey(k.From)) return;
        var rope = _ropes.GetValueOrDefault(k) ?? new Rope(_jacks[k.From], _jacks[k.To]);
        mix.Patch.Remove(k);
        _loose.Add((rope, k.From, true, k.Colour));
    }

    /// The effect browser drags a plugin over the rack: where it would land (null: it left).
    public void HoverPlugin(PluginHost.Plugin? p, Point at) { (_incoming, _incomingAt) = (p, at); InvalidateVisual(); }

    /// Let go over the box: the plugin is a module in the nearest free slots (unpatched).
    public bool DropPlugin(PluginHost.Plugin p, Point at)
    {
        _incoming = null;
        if (S.Current is null || at.Y < _chassisTop - 30 || at.Y > _chassisBottom + 30) { InvalidateVisual(); return false; }
        if (FreeAt(S.Mix, null, 2, SlotAt(at.X, 2)) is not int slot) { S.Status = "lunchbox cheio: tire um módulo antes"; return false; }
        S.AddModule("vst", slot, p);
        return true;
    }

    void BayPressed(PointerPressedEventArgs e, Point p)
    {
        if (S.Current is null) return;
        var mix = S.Mix;
        // A plug: picked out of its jack (the newest cable there), or a new cable from an empty jack.
        foreach (var (id, j) in _jacks)
        {
            if ((p - j).X * (p - j).X + (p - j).Y * (p - j).Y > 11 * 11) continue;
            var k = mix.Patch.LastOrDefault(x => x.From == id || x.To == id);
            if (k is not null)
            {
                bool heldIsTo = k.To == id;
                var rope = _ropes.GetValueOrDefault(k) ?? new Rope(_jacks[k.From], _jacks[k.To]);
                if (!heldIsTo) rope.Reverse();   // the rope runs from the fixed end to the hand
                mix.Patch.Remove(k);
                _cable = (rope, heldIsTo ? k.From : k.To, heldIsTo, k.Colour);
                S.MixChanged();   // pulled: that path is silent until it is plugged again
            }
            else _cable = (new Rope(j, p), id, IsOut(id), NextColour(mix));
            _hand = p;
            e.Pointer.Capture(this);
            return;
        }
        // A loose cable's plug lying around.
        for (int i = _loose.Count - 1; i >= 0; i--)
            if ((p - _loose[i].Rope.B).X * (p - _loose[i].Rope.B).X + (p - _loose[i].Rope.B).Y * (p - _loose[i].Rope.B).Y < 12 * 12)
            {
                _cable = _loose[i];
                _loose.RemoveAt(i);
                _hand = p;
                e.Pointer.Capture(this);
                return;
            }
        // A module by its top: lifted out of its slot.
        foreach (var (m, box) in _modules)
            if (new Rect(box.X, box.Y, box.Width, 18).Contains(p))
            {
                _heldModule = m;
                (_grab, _hand) = (p - box.TopLeft, p);
                e.Pointer.Capture(this);
                return;
            }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_cable is null && _heldModule is null) { base.OnPointerMoved(e); return; }
        _hand = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_cable is null && _heldModule is null) { base.OnPointerReleased(e); return; }
        var mix = S.Mix;
        _hand = e.GetPosition(this);
        e.Pointer.Capture(null);
        if (_cable is { } held)
        {
            _cable = null;
            if (NearJack(_hand, !held.FixedIsOut) is { } to && Patchbay.Node(to) != Patchbay.Node(held.Fixed))
            {
                var k = held.FixedIsOut ? new CableLink { From = held.Fixed, To = to, Colour = held.Colour } : new CableLink { From = to, To = held.Fixed, Colour = held.Colour };
                if (!held.FixedIsOut) held.Rope.Reverse();   // ropes run out -> in
                mix.Patch.Add(k);
                _ropes[k] = held.Rope;
                S.MixChanged();
            }
            else _loose.Add(held);   // let go anywhere else: it hangs from its jack
        }
        else if (_heldModule is { } m)
        {
            _heldModule = null;
            if (_hand.Y < _chassisTop - 40 || _hand.Y > _chassisBottom + 20) S.RemoveModule(m.Id);   // out of the box
            else if (FreeAt(mix, m, SpanOf(m), (int)Math.Round((_hand.X - _grab.X - _s0) / SlotW)) is int at)
            {
                m.Slot = at;
                _modX[m.Id] = _hand.X - _grab.X;   // glides in from where it was let go
                S.MixChanged();
            }
        }
        InvalidateVisual();
    }
}
