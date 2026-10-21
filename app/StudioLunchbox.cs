using System.Diagnostics;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Dissonancia;

// STUDIO S10 — DS-C, the session's lunchbox: a chassis of slots where modules sit (built-in units
// from the drawer, VST3 plugins dragged in from the effect browser), each with an IN and an OUT
// jack on its lip, and under it a patchbay with every track's SAI (its source), VOLTA (its insert
// return, into its fader) and AUX (after its fader, a send), and the MASTER's input. Patch cables
// between jacks are the mixer graph (S11): VOZ SAI -> EQ -> COMP -> VOZ VOLTA; a track whose SAI has
// no cable goes straight through (normalled); cables into one jack are summed (a bus), one output on
// several cables is copied (a parallel path, a send), and such a cable has its own level knob.
// Cables are ropes: a plug is picked out of its jack with the mouse, the cable swings and hangs
// under its weight, and drops into a jack (or hangs loose anywhere else). Where a module sits does
// not change the sound; the cables do.
public sealed partial class ChainUnit
{
    const double SlotW = 62, LipH = 26, PatchH = 86, ColW = 74;
    public const int SlotCount = 16;

    static readonly Dictionary<string, (string Model, int Span)> Face = new()
    {
        ["trim"] = ("DS-1", 2), ["filter"] = ("DF-2", 2), ["gate"] = ("DG-3", 2), ["eq"] = ("DQ-4", 3), ["comp"] = ("DC-5", 3), ["vst"] = ("DV", 2),
    };
    public static int SpanOf(BoxModule m) => Face.TryGetValue(m.Type, out var f) ? f.Span : 2;

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
        // Which tracks reach each module (one: its name on the module; more: a bus).
        var onTrack = new Dictionary<string, List<int>>();
        for (int t = 0; t < names.Count; t++)
            foreach (var n in Patchbay.Reach(mix.Patch, names[t] + ".sai", names[t] + ".aux"))
                if (!onTrack.TryGetValue(n, out var l)) onTrack[n] = [t]; else if (!l.Contains(t)) l.Add(t);
        var loops = Patchbay.Loops(mix.Patch);

        // Header: the selected track's way, how to use the box.
        string tn = S.ModuleName(S.NameOf(sel) + ".sai"), self = S.NameOf(sel);
        var way = Patchbay.Reach(mix.Patch, self + ".sai");
        var ends = way.Where(n => n.EndsWith(".volta") || n == "master").ToList();
        string? err = loops.Any(n => n.StartsWith(self + ".") || way.Contains(n)) ? "um cabo faz o sinal voltar (laço): o que está no laço fica mudo"
            : mix.Patch.Any(k => k.From == Patchbay.Sai(self)) && ends.Count == 0 ? "a saída não chega a uma VOLTA nem ao MASTER" : null;
        string Name(string n) => n.EndsWith(".volta") ? (n == self + ".volta" ? "VOLTA" : "VOLTA " + S.ModuleName(n)) : S.ModuleName(n);
        string route = err is not null ? $"{tn}: SEM SOM — {err}"
            : way.Count == 0 ? $"{tn}: SAI ▶ VOLTA (direto, sem cabo)"
            : $"{tn}: SAI ▶ " + string.Join(" ▶ ", way.Where(n => !n.EndsWith(".aux")).Select(Name));
        var aux = Patchbay.Reach(mix.Patch, self + ".aux");
        if (err is null && aux.Count > 0) route += "   · AUX ▶ " + string.Join(" ▶ ", aux.Select(Name));
        var bypassed = way.Concat(aux).Select(n => mix.Box.FirstOrDefault(m => m.Id == n)).Where(m => m?.Vst is { On: true } && !S.Rendered(sel).Contains(m)).ToList();
        if (err is null && bypassed.Count > 0) route += $"   ({bypassed[0]!.Vst!.Name}: VST3 só toca logo depois do SAI, em série; aqui passa direto)";
        Ui.Led(ctx, new Point(r.X + 96, r.Y + 8), 3.5, true, err is null ? Ui.Green : Ui.Red, Ui.GreenOff);
        Ui.Text(ctx, route.Length > 130 ? route[..130] : route, r.X + 106, r.Y + 2, 9, err is null ? Ui.LabelBright : Ui.Red, Ui.Mono);
        Ui.Text(ctx, "pegue um plugue e leve a outro jack · cabo sob o mouse: Delete ou botão direito apaga · vários cabos num jack somam (bus) · AUX = envio pós-fader · arraste um módulo pelo topo · fora da caixa = tira · plugins: arraste da lista de efeitos", r.X + 106, r.Y + 15, 8, Ui.Label, Ui.Sans);

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

        // The patchbay: per track its tape, SAI on top, VOLTA under it, AUX at the bottom; the MASTER.
        var bay = new Rect(bx, chassis.Bottom + 6, Math.Max(ColW * names.Count + 70 + ColW, 200), PatchH);
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF1E2024)), Ui.FaceEdge, bay, 4, 4);
        Ui.Text(ctx, "PATCHBAY", bay.X + 8, bay.Y + 4, 8, Ui.Label, Ui.SansBold);
        Ui.Text(ctx, "SAI", bay.X + 8, bay.Y + 22, 7, Ui.Label, Ui.SansBold);
        Ui.Text(ctx, "VOLTA", bay.X + 8, bay.Y + 44, 7, Ui.Label, Ui.SansBold);
        Ui.Text(ctx, "AUX", bay.X + 8, bay.Y + 66, 7, Ui.Label, Ui.SansBold);
        double mxc = bay.X + 70 + names.Count * ColW + ColW / 2;
        Ui.Text(ctx, "MASTER", mxc, bay.Y + 4, 8, Ui.Amber, Ui.SansBold, Ui.Align.Center);
        _jacks[Patchbay.Master] = new Point(mxc, bay.Y + 48);
        for (int t = 0; t < names.Count; t++)
        {
            double cx = bay.X + 70 + t * ColW + ColW / 2;
            string label = S.ModuleName(names[t] + ".sai");
            Ui.Text(ctx, label.Length > 8 ? label[..8] : label, cx, bay.Y + 4, 8, t == sel ? Ui.Amber : Ui.LabelBright, Ui.SansBold, Ui.Align.Center);
            _jacks[Patchbay.Sai(names[t])] = new Point(cx, bay.Y + 26);
            _jacks[Patchbay.Volta(names[t])] = new Point(cx, bay.Y + 48);
            _jacks[Patchbay.Aux(names[t])] = new Point(cx, bay.Y + 70);
            bool normalled = !mix.Patch.Any(k => k.From == Patchbay.Sai(names[t]));
            if (normalled) ctx.DrawLine(new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(0xFF3E6B53)), 1.2, new ImmutableDashStyle([2, 2], 0)), new Point(cx, bay.Y + 32), new Point(cx, bay.Y + 42));
        }

        // Levels on the cables: a track's SAI (its source), its AUX (after the fader), a module's output.
        float Lv(string node)
        {
            int dot = node.LastIndexOf('.'), t = dot < 0 ? -1 : names.IndexOf(node[..dot]);
            if (t >= 0) return node.EndsWith(".sai") ? S.Meters.In[Math.Min(t, 7)] : S.Meters.Peak[Math.Min(t, 7)];
            return S.MeterOf(node) is int mi and >= 0 ? S.Meters.Unit[mi] : -90;
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
            if (m.Vst is { } v) DrawInsert(ctx, box, m, v, fy);
            else DrawUnit(ctx, box, m.Type, m.P, S.MeterOf(m.Id), ky, fy, has, () => S.MixChanged(), Veil, Lv(m.Id), S.ModuleName(m.Id));
            string who = !onTrack.TryGetValue(m.Id, out var ts) ? "sem cabo" : loops.Contains(m.Id) ? "LAÇO" : ts.Count > 1 ? "BUS " + string.Join("+", ts.Select(t => S.ModuleName(names[t] + ".sai")[..Math.Min(3, S.ModuleName(names[t] + ".sai").Length)]))
                : S.ModuleName(names[ts[0]] + ".sai");
            Ui.Text(ctx, who.Length > 16 ? who[..16] : who, box.Center.X, box.Y + 20, 7, who is "sem cabo" or "LAÇO" ? Ui.Amber : Ui.Green, Ui.SansBold, Ui.Align.Center);
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
            if (ReferenceEquals(_hover, rope)) Halo(ctx, rope);
            DrawCable(ctx, rope, CableColours[k.Colour % CableColours.Length], Lv(Patchbay.Node(k.From)), true, true);
        }
        foreach (var k in _ropes.Keys.Where(k => !mix.Patch.Contains(k)).ToList()) _ropes.Remove(k);
        foreach (var (rope, fix, _, col) in _loose)
            if (_jacks.TryGetValue(fix, out var a)) { rope.Step(dt, a, null, _floor); if (ReferenceEquals(_hover, rope)) Halo(ctx, rope); DrawCable(ctx, rope, CableColours[col % CableColours.Length], -90, true, true); }
        _loose.RemoveAll(l => !_jacks.ContainsKey(l.Fixed));
        if (_cable is { } held && _jacks.TryGetValue(held.Fixed, out var fa))
        {
            held.Rope.Step(dt, fa, _hand, _floor);
            DrawCable(ctx, held.Rope, CableColours[held.Colour % CableColours.Length], -90, true, true);
            if (NearJack(_hand, !held.FixedIsOut) is { } target) ctx.DrawEllipse(null, new ImmutablePen((IImmutableBrush)Ui.Green, 2), _jacks[target], 10, 10);
        }

        // A level knob in line: on a send (from an AUX) and on each of several cables into one jack.
        foreach (var k in mix.Patch)
            if ((k.From.EndsWith(".aux:out") || mix.Patch.Count(x => x.To == k.To) > 1) && _ropes.TryGetValue(k, out var rope))
            {
                var kp = rope.P[rope.P.Length / 2];
                var link = k;
                Knob(ctx, kp, 8, "", "", Math.Sqrt(k.Gain), n => { link.Gain = (float)Math.Round(n * n, 3); S.MixChanged(); }, true);
                Ui.Text(ctx, k.Gain <= 0.001 ? "-∞" : $"{20 * Math.Log10(k.Gain):0}", kp.X + 11, kp.Y - 5, 8, Ui.Amber, Ui.SansBold);
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

    void DrawCable(DrawingContext ctx, Rope rope, uint colour, float db, bool plugA, bool plugB) => Cables.Draw(ctx, rope, colour, db, _clock.Elapsed.TotalSeconds, plugA, plugB);

    // The cable under the mouse (a rope, plugged or loose): Delete / Backspace or a right click removes it.
    Rope? _hover;
    Rope? RopeAt(Point p)
    {
        Rope? best = null;
        double bd = 8 * 8;
        foreach (var rope in _ropes.Values.Concat(_loose.Select(l => l.Rope)))
            foreach (var q in rope.P)
                if ((p - q).X * (p - q).X + (p - q).Y * (p - q).Y is double d && d < bd) (bd, best) = (d, rope);
        return best;
    }
    static void Halo(DrawingContext ctx, Rope rope) => Cables.Halo(ctx, rope);

    /// Delete / Backspace: the cable in the hand, or the one under the mouse, out of the box.
    public bool DeleteCable()
    {
        if (_cable is not null) { _cable = null; InvalidateVisual(); return true; }   // already out of the patch: dropped
        if (_hover is not { } rope) return false;
        _hover = null;
        _loose.RemoveAll(l => ReferenceEquals(l.Rope, rope));
        if (_ropes.FirstOrDefault(kv => ReferenceEquals(kv.Value, rope)).Key is { } k) { S.Mix.Patch.Remove(k); _ropes.Remove(k); S.MixChanged(); }
        InvalidateVisual();
        return true;
    }

    /// The jack under a point, of the kind wanted (an output, or an input), with room for a cable.
    string? NearJack(Point p, bool wantOut)
    {
        foreach (var (id, j) in _jacks)
        {
            if (IsOut(id) != wantOut || (p - j).X * (p - j).X + (p - j).Y * (p - j).Y > 14 * 14) continue;
            if (S.Mix.Patch.Count(k => k.From == id || k.To == id) < 4) return id;
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
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed) { _hover = RopeAt(p); DeleteCable(); return; }
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
        if (_cable is null && _heldModule is null)
        {
            var h = RopeAt(e.GetPosition(this));
            if (!ReferenceEquals(h, _hover)) { _hover = h; InvalidateVisual(); }
            base.OnPointerMoved(e);
            return;
        }
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
