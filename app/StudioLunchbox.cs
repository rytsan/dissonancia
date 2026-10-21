using System.Diagnostics;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Dissonancia;

// STUDIO S10 — DS-C as a lunchbox: a chassis of slots where the channel's modules sit (they snap
// into slots; one pulled out of the chassis goes back to the drawer), each with an IN and an OUT
// jack on its lip, and patch cables between the jacks that make the chain. Cables are ropes: a plug
// is picked up with the mouse, the cable swings and hangs under its weight, and drops into a jack
// (or hangs loose when let go anywhere else). Where a module sits does not change the sound; the
// cables do (ChannelMix.Route).
public sealed partial class ChainUnit
{
    const double SlotW = 62, PlateW = 44, LipH = 26;
    const int SlotCount = 13;

    /// Every module's face and its width in slots.
    static readonly Dictionary<string, (string Model, string Name, int Span)> UnitFace = new()
    {
        ["trim"] = ("DS-1", "TRIM", 2), ["filter"] = ("DF-2", "FILTRO", 2), ["gate"] = ("DG-3", "GATE", 2), ["eq"] = ("DQ-4", "EQ", 3), ["comp"] = ("DC-5", "COMP", 3),
    };
    static int Span(string slot) => UnitFace.TryGetValue(slot, out var f) ? f.Span : 2;

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
        public Point A => P[0];
        public Point B => P[N - 1];

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
    readonly List<(string Slot, Rect Box)> _modules = [];
    readonly Stopwatch _clock = Stopwatch.StartNew();
    double _lastT;
    int _ropesFor = -1;
    double _floor, _s0;

    // In the hand: a cable (its fixed end's jack, which end is held) or a module (where it was grabbed).
    (Rope Rope, string Fixed, bool FixedIsOut, int Colour)? _cable;
    readonly List<(Rope Rope, string Fixed, bool FixedIsOut, int Colour)> _loose = [];
    string? _heldModule;
    Point _grab, _hand;
    readonly Dictionary<string, double> _plugFlash = [];

    /// The first colour no cable of the box has (a new cable from the drawer of cables).
    static int NextColour(ChannelMix c)
    {
        for (int i = 0; i < CableColours.Length; i++) if (!c.Cables.Any(k => k.Colour == i)) return i;
        return c.Cables.Count;
    }

    static bool IsOut(string jack) => jack == "src" || jack.EndsWith(":out");
    static readonly uint[] CableColours = [0xFFE2462F, 0xFFF2C12E, 0xFF2F8FE2, 0xFF3DBE6B, 0xFFF08A24, 0xFFD9D9D9, 0xFFB45CE0];

    /// Places every module of the channel: as saved, new inserts in the first free slots; a take
    /// from before the lunchbox gets the cables of its chain.
    void EnsureRack(ChannelMix c)
    {
        if (c.Cables.Count == 0) c.Cables = c.DefaultCables();
        if (c.Rack.Count == 0)
        {
            int at = 0;
            foreach (var s in c.Slots().Concat(ChannelMix.Units).Distinct())
                if (at + Span(s) <= SlotCount) { c.Rack[s] = at; at += Span(s); }
        }
        foreach (var v in c.Vst)
            if (!c.Rack.ContainsKey(ChannelMix.SlotOf(v)) && FreeAt(c, ChannelMix.SlotOf(v), 0) is int f) c.Rack[ChannelMix.SlotOf(v)] = f;
        foreach (var k in c.Rack.Keys.Where(k => !ChannelMix.Units.Contains(k) && c.VstOf(k) is null).ToList()) c.Rack.Remove(k);
    }

    /// The nearest slot to `want` where `slot` fits without overlapping another module.
    static int? FreeAt(ChannelMix c, string slot, int want)
    {
        int span = Span(slot);
        bool Fits(int at) => at >= 0 && at + span <= SlotCount
            && c.Rack.Where(kv => kv.Key != slot).All(kv => at + span <= kv.Value || kv.Value + Span(kv.Key) <= at);
        for (int d = 0; d < SlotCount; d++)
        {
            if (Fits(want - d)) return want - d;
            if (Fits(want + d)) return want + d;
        }
        return null;
    }

    /// Draws the lunchbox; returns where the analyser goes (x) and the modules' row (y, height).
    (double X, double Y, double H) DrawBay(DrawingContext ctx, Rect r, int sel)
    {
        var c = S.Channel(sel);
        bool has = S.Current is not null;
        int mt = Math.Min(sel, 7);
        if (has) EnsureRack(c);
        if (_ropesFor != sel) { _ropes.Clear(); _loose.Clear(); _cable = null; _heldModule = null; _ropesFor = sel; }
        double now = _clock.Elapsed.TotalSeconds, dt = Math.Clamp(now - _lastT, 0.001, 0.04);
        _lastT = now;

        // Header: how to use it, the route the cables make, the drawer.
        Ui.Led(ctx, new Point(r.X + 96, r.Y + 8), 3.5, true, c.RouteError is null ? Ui.Green : Ui.Red, Ui.GreenOff);
        string route = c.RouteError is { } err ? "SEM SOM: " + err
            : "IN ▶ " + string.Join(" ▶ ", c.Slots().Select(s => UnitFace.TryGetValue(s, out var f) ? f.Name : c.VstOf(s)?.Name ?? s)) + " ▶ OUT"
              + (c.ParallelFirst is not null ? $"   (∥ {UnitFace[c.ParallelFirst].Name}…{UnitFace[c.ParallelLast!].Name})" : "");
        Ui.Text(ctx, route.Length > 96 ? route[..96] : route, r.X + 106, r.Y + 2, 9, c.RouteError is null ? Ui.LabelBright : Ui.Red, Ui.Mono);
        Ui.Text(ctx, "pegue um plugue e leve a outro jack · arraste um módulo pelo topo · fora da caixa = gaveta", r.X + 106, r.Y + 15, 8, Ui.Label, Ui.Sans);

        double y = r.Y + 34, h = r.Height - 34 - LipH - 44;
        double bx = r.X, bw = PlateW * 2 + SlotCount * SlotW + 12;
        var chassis = new Rect(bx, y - 8, bw, h + LipH + 14);
        _floor = r.Bottom + 6;
        // Chassis: a dark tub, rails with a screw hole per slot.
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF15171A)), new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF050606)), 2), chassis, 6, 6);
        double s0 = _s0 = bx + 6 + PlateW;
        for (int i = 0; i < SlotCount; i++)
        {
            var slotR = new Rect(s0 + i * SlotW + 1, y, SlotW - 2, h + LipH);
            ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF0E0F11)), new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF1F2226)), 1), slotR, 2, 2);
            ctx.DrawEllipse(Ui.Screw, null, new Point(slotR.Center.X, y - 4), 1.8, 1.8);
            ctx.DrawEllipse(Ui.Screw, null, new Point(slotR.Center.X, y + h + LipH + 3), 1.8, 1.8);
        }
        // IN and OUT plates.
        var inPlate = new Rect(bx + 4, y, PlateW - 4, h + LipH);
        var outPlate = new Rect(s0 + SlotCount * SlotW + 2, y, PlateW - 4, h + LipH);
        foreach (var (plate, label, sub) in new[] { (inPlate, "IN", sel == 0 ? "fonte" : $"stem {sel}"), (outPlate, "OUT", "tap") })
        {
            ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF24272B)), Ui.FaceEdge, plate, 3, 3);
            Ui.Text(ctx, label, plate.Center.X, plate.Y + 8, 10, Ui.LabelBright, Ui.SansBold, Ui.Align.Center);
            Ui.Text(ctx, sub, plate.Center.X, plate.Y + 22, 7, Ui.Label, Ui.Sans, Ui.Align.Center);
        }
        _jacks.Clear();
        _jacks["src"] = new Point(inPlate.Center.X, y + h + LipH / 2);
        _jacks["dst"] = new Point(outPlate.Center.X, y + h + LipH / 2);

        // Cable levels: units after the last insert that is on play live (stage peaks by position);
        // before it everything is rendered, its level the source's.
        var live = c.Segments()[^1];
        float Lv(string node) => !live.Contains(node) ? S.Meters.Stage[mt, 0] : S.Meters.Stage[mt, live.IndexOf(node) + 1];
        double ky = y + h / 2 - 6, fy = y + h - 22;

        // Modules: they glide into their slot; the one in the hand follows it, lifted, with a shadow.
        _modules.Clear();
        foreach (var (slot, idx) in has ? c.Rack.OrderBy(kv => kv.Key == _heldModule).ToList() : new List<KeyValuePair<string, int>>())
        {
            double target = s0 + idx * SlotW + 1;
            double mx = _modX.TryGetValue(slot, out var cur) ? cur + (target - cur) * Math.Min(1, dt * 18) : target;
            _modX[slot] = mx;
            var box = new Rect(mx, y, Span(slot) * SlotW - 2, h);
            if (slot == _heldModule)
            {
                box = new Rect(_hand.X - _grab.X, _hand.Y - _grab.Y - 6, box.Width, h);
                ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0x88000000)), null, box.Translate(new Vector(7, 10)).Inflate(new Thickness(0, 0, 0, LipH)), 5, 5);
            }
            var v = c.VstOf(slot);
            var lip = new Rect(box.X, box.Bottom, box.Width, LipH);
            ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF1E2024)), Ui.FaceEdge, lip, 0, 0);
            var b = box;   // for the veil
            void Veil(bool on) { if (!on && has) { ctx.DrawRectangle(BypassVeil, null, b, 4, 4); Ui.Text(ctx, "BYPASS", b.Center.X + 22, fy - 6, 8, Ui.Amber, Ui.SansBold); } }
            if (v is not null) DrawInsert(ctx, box, sel, v, box.Y + h - 22);
            else DrawUnit(ctx, box, slot, c, mt, box.Y + (ky - y), box.Y + (fy - y), has, () => S.MixChanged(), Veil, Lv(slot));
            bool patched = c.Slots().Contains(slot);
            if (!patched) Ui.Text(ctx, "fora do caminho", box.Center.X, box.Y + 20, 7, Ui.Amber, Ui.SansBold, Ui.Align.Center);
            var jin = new Point(lip.X + 14, lip.Center.Y);
            var jout = new Point(lip.Right - 14, lip.Center.Y);
            Jack(ctx, jin);
            Jack(ctx, jout);
            Ui.Text(ctx, "IN", jin.X + 10, lip.Y + 7, 7, Ui.Label, Ui.SansBold);
            Ui.Text(ctx, "OUT", jout.X - 10, lip.Y + 7, 7, Ui.Label, Ui.SansBold, Ui.Align.Right);
            _jacks[slot + ":in"] = jin;
            _jacks[slot + ":out"] = jout;
            _modules.Add((slot, box));
        }
        Jack(ctx, _jacks["src"]);
        Jack(ctx, _jacks["dst"]);
        if (_pullDemo && _modules.Count > 0) PullNow();

        // Cables: plugged ones (each a rope between its jacks), loose ones, the one in the hand.
        foreach (var k in c.Cables.ToList())
        {
            if (k.Colour < 0) k.Colour = NextColour(c);
            if (!_jacks.TryGetValue(k.From, out var a) || !_jacks.TryGetValue(k.To, out var e)) continue;
            if (!_ropes.TryGetValue(k, out var rope)) _ropes[k] = rope = new Rope(a, e);
            rope.Step(dt, a, e, _floor);
            string from = k.From == "src" ? "src" : k.From[..k.From.LastIndexOf(':')];
            DrawCable(ctx, rope, CableColours[k.Colour % CableColours.Length], c.RouteError is null ? Lv(from) : -90, true, true);
        }
        foreach (var k in _ropes.Keys.Where(k => !c.Cables.Contains(k)).ToList()) _ropes.Remove(k);
        foreach (var (rope, fix, _, col) in _loose)
            if (_jacks.TryGetValue(fix, out var a)) { rope.Step(dt, a, null, _floor); DrawCable(ctx, rope, CableColours[col % CableColours.Length], -90, true, true); }
        _loose.RemoveAll(l => !_jacks.ContainsKey(l.Fixed));
        if (_cable is { } held && _jacks.TryGetValue(held.Fixed, out var fa))
        {
            held.Rope.Step(dt, fa, _hand, _floor);
            DrawCable(ctx, held.Rope, CableColours[held.Colour % CableColours.Length], -90, true, true);
            // The jack it would go into lights up.
            if (NearJack(_hand, !held.FixedIsOut) is { } target) ctx.DrawEllipse(null, new ImmutablePen((IImmutableBrush)Ui.Green, 2), _jacks[target], 10, 10);
        }

        // The parallel path's mix: a knob by the jack where it comes back.
        if (c.RouteError is null && c.ParallelLast is not null)
        {
            var sl = c.Slots();
            int li = sl.IndexOf(c.ParallelLast);
            string merge = li + 1 < sl.Count ? sl[li + 1] + ":in" : "dst";
            if (_jacks.TryGetValue(merge, out var mj))
            {
                var kp = new Point(mj.X + (merge == "dst" ? -30 : 28), mj.Y + 30);
                Knob(ctx, kp, 9, "", "", c.ParallelMix, n => { c.ParallelMix = (float)Math.Round(n, 2); S.MixChanged(); }, true);
                Ui.Text(ctx, $"∥ {c.ParallelMix * 100:0} %", kp.X + 13, kp.Y - 6, 8, Ui.Amber, Ui.SansBold);
            }
        }

        // The drawer: modules out of the box; a click puts one in the first free slot (unpatched).
        double dx = r.X + 640;
        foreach (var u in has ? ChannelMix.Units.Where(u => !c.Rack.ContainsKey(u)) : [])
        {
            string label = "+ " + UnitFace[u].Name;
            double w = label.Length * 7 + 18;
            Key(ctx, new Rect(dx, r.Y + 6, w, 20), label, () =>
            {
                if (FreeAt(c, u, SlotCount) is int f) { c.Rack[u] = f; S.MixChanged(); }
                else S.Status = "lunchbox cheio: tire um módulo antes";
            }, false, Ui.Amber, true, withLed: false);
            dx += w + 4;
        }
        Key(ctx, new Rect(dx, r.Y + 6, 70, 20), "+ VST3", () => { S.PluginView = true; S.ShowBrowser = true; S.Select(); }, S.PluginView, Ui.Amber, has && PluginHost.Present && c.Vst.Count < StudioState.MaxVst, withLed: false);
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
        // The signal travelling: pulses from the output end to the input end.
        if (db > -60)
        {
            double total = 0;
            var cum = new double[pts.Length];
            for (int i = 1; i < pts.Length; i++) cum[i] = total += Math.Sqrt(Math.Pow(pts[i].X - pts[i - 1].X, 2) + Math.Pow(pts[i].Y - pts[i - 1].Y, 2));
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
        var boot = tip + u * 9;
        ctx.DrawLine(new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFFB9BEC6)), 7, lineCap: PenLineCap.Round), tip, tip + u * 4);
        ctx.DrawLine(new ImmutablePen(new ImmutableSolidColorBrush(col), 9, lineCap: PenLineCap.Round), tip + u * 5, boot + u * 4);
        ctx.DrawEllipse(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF2A2D32)), null, tip, 3.2, 3.2);
    }

    /// The jack under a point, of the kind wanted (an output, or an input with room for a cable).
    string? NearJack(Point p, bool wantOut)
    {
        var c = S.Channel(S.Selected);
        foreach (var (id, j) in _jacks)
        {
            if (IsOut(id) != wantOut || (p - j).X * (p - j).X + (p - j).Y * (p - j).Y > 14 * 14) continue;
            int load = c.Cables.Count(k => k.From == id || k.To == id);
            if (load < 2) return id;
        }
        return null;
    }

    /// Screenshot tool: the cable out of the trim pulled and let go (it hangs; the path is silent).
    public void PullDemo() => _pullDemo = true;
    bool _pullDemo;
    void PullNow()
    {
        _pullDemo = false;
        var c = S.Channel(S.Selected);
        if (c.Cables.FirstOrDefault(k => k.From == "trim:out" && k.To != "dst") is not { } k || !_jacks.ContainsKey(k.From)) return;
        var rope = _ropes.GetValueOrDefault(k) ?? new Rope(_jacks[k.From], _jacks[k.To]);
        c.Cables.Remove(k);
        _loose.Add((rope, k.From, true, k.Colour));
        c.RouteError = ChannelMix.Route(c.Cables).Error;   // on screen only: nothing saved to the take
    }

    void BayPressed(PointerPressedEventArgs e, Point p)
    {
        if (S.Current is null) return;
        var c = S.Channel(S.Selected);
        // A plug: picked out of its jack (the newest cable there), or a new cable from an empty jack.
        foreach (var (id, j) in _jacks)
        {
            if ((p - j).X * (p - j).X + (p - j).Y * (p - j).Y > 11 * 11) continue;
            var k = c.Cables.LastOrDefault(x => x.From == id || x.To == id);
            if (k is not null)
            {
                bool heldIsTo = k.To == id;
                var rope = _ropes.GetValueOrDefault(k) ?? new Rope(_jacks[k.From], _jacks[k.To]);
                if (!heldIsTo) Array.Reverse(rope.P);   // the rope runs from the fixed end to the hand
                if (!heldIsTo) Array.Reverse(rope.Q);
                c.Cables.Remove(k);
                _cable = (rope, heldIsTo ? k.From : k.To, heldIsTo, k.Colour);
                S.Repatch(S.Selected);   // pulled: that path is silent until it is plugged again
            }
            else _cable = (new Rope(j, p), id, IsOut(id), NextColour(c));
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
        foreach (var (slot, box) in _modules)
            if (new Rect(box.X, box.Y, box.Width, 20).Contains(p))
            {
                _heldModule = slot;
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
        var c = S.Channel(S.Selected);
        _hand = e.GetPosition(this);
        e.Pointer.Capture(null);
        if (_cable is { } held)
        {
            _cable = null;
            if (NearJack(_hand, !held.FixedIsOut) is { } to && to != held.Fixed)
            {
                var k = held.FixedIsOut ? new CableLink { From = held.Fixed, To = to, Colour = held.Colour } : new CableLink { From = to, To = held.Fixed, Colour = held.Colour };
                if (!held.FixedIsOut) { Array.Reverse(held.Rope.P); Array.Reverse(held.Rope.Q); }   // ropes run out -> in
                c.Cables.Add(k);
                _ropes[k] = held.Rope;
                S.Repatch(S.Selected);
            }
            else _loose.Add(held);   // let go anywhere else: it hangs from its jack
        }
        else if (_heldModule is { } slot)
        {
            _heldModule = null;
            var chassisTop = _modules.Count > 0 ? _modules.Min(m => m.Box.Y) : 0;
            if (_hand.Y < chassisTop - 40 || _hand.Y > _floor - 10)
            {   // out of the box: back to the drawer, its cables joined round it
                StudioState.Bridge(c, slot);
                if (c.VstOf(slot) is { } v) S.RemoveVst(S.Selected, v);
                else c.Rack.Remove(slot);
                _modX.Remove(slot);
                S.Repatch(S.Selected);
            }
            else if (FreeAt(c, slot, (int)Math.Round((_hand.X - _grab.X - _s0) / SlotW)) is int at)
            {
                c.Rack[slot] = at;
                _modX[slot] = _hand.X - _grab.X;   // glides in from where it was let go
                S.MixChanged();
            }
        }
        InvalidateVisual();
    }
}
