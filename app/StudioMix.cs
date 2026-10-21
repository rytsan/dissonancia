using System.Runtime.InteropServices;
using System.Text.Json;

namespace Dissonancia;

// STUDIO S4 — mixing (docs/studio-plan.md): the C# side of the core's mixing engine. A channel's
// settings in console order (trim, filters, gate, EQ, compressor, fader, pan, mute, solo), the
// master's, per take in <takes>/studio/<name>.mix.json, and the native mirrors.

[StructLayout(LayoutKind.Sequential)]
struct EqBandNative { public float FreqHz, GainDb, Q; public byte Type, On; ushort _pad0; }

[StructLayout(LayoutKind.Sequential)]
unsafe struct ChannelParamsNative
{
    public float TrimDb, HpHz, LpHz;
    public byte HpOn, LpOn, Steep, GateOn;
    public float GateThresholdDb, GateAttackMs, GateReleaseMs, GateRangeDb;
    public byte EqOn, CompOn, Mute, Solo;
    public EqBandNative Eq0, Eq1, Eq2, Eq3;
    public float CompThresholdDb, CompRatio, CompAttackMs, CompReleaseMs, CompMakeupDb;
    public float FaderDb, Pan;
    public fixed byte Order[5];
    public byte ParallelFrom, ParallelTo, SendTo;
    public float ParallelMix;
}

[StructLayout(LayoutKind.Sequential)]
struct MasterParamsNative
{
    public byte EqOn, CompOn, LimiterOn, _pad0;
    public EqBandNative Eq0, Eq1, Eq2, Eq3;
    public float CompThresholdDb, CompRatio, CompAttackMs, CompReleaseMs, CompMakeupDb;
    public float LimiterCeilingDb, LimiterReleaseMs, FaderDb;
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct MixMetersNative
{
    public fixed float PeakDb[8], RmsDb[8], CompGrDb[8];
    public fixed byte GateOpen[8];
    public fixed float MasterPeakDb[2], MasterRmsDb[2];
    public float MasterCompGrDb, LimiterGrDb;
    public int Tracks;
    uint _pad0;
    public fixed float StagePeakDb[8 * 6];   // [track][in, trim, filter, gate, EQ, compressor]
    public float LufsMomentary, LufsShortTerm, LufsIntegrated, LoudnessRange;
    public fixed float TruePeakDb[2];
}

static partial class MixApi
{
    const string Lib = "dissonancia";
    [LibraryImport(Lib, EntryPoint = "ana_player_add_track", StringMarshalling = StringMarshalling.Utf8)] public static partial int AddTrack(nint p, string path);
    [LibraryImport(Lib, EntryPoint = "ana_player_clear_tracks")] public static partial void ClearTracks(nint p);
    [LibraryImport(Lib, EntryPoint = "ana_player_set_source", StringMarshalling = StringMarshalling.Utf8)] public static partial int SetSource(nint p, int track, string? path);
    [LibraryImport(Lib, EntryPoint = "ana_channel_defaults")] public static partial void ChannelDefaults(out ChannelParamsNative c);
    [LibraryImport(Lib, EntryPoint = "ana_master_defaults")] public static partial void MasterDefaults(out MasterParamsNative m);
    [LibraryImport(Lib, EntryPoint = "ana_player_set_channel")] public static partial int SetChannel(nint p, int track, in ChannelParamsNative c);
    [LibraryImport(Lib, EntryPoint = "ana_player_set_master")] public static partial void SetMaster(nint p, in MasterParamsNative m);
    [LibraryImport(Lib, EntryPoint = "ana_player_meters")] public static partial void Meters(nint p, out MixMetersNative m);
    [LibraryImport(Lib, EntryPoint = "ana_player_scope")] public static unsafe partial int Scope(nint p, int source, float* stereo, int frames);
    [LibraryImport(Lib, EntryPoint = "ana_player_set_monitor")] public static partial void SetMonitor(nint p, int track);
    [LibraryImport(Lib, EntryPoint = "ana_player_reset_loudness")] public static partial void ResetLoudness(nint p);
    [LibraryImport(Lib, EntryPoint = "ana_player_pull")] public static partial void Pull(nint p, int frames);
    [LibraryImport(Lib, EntryPoint = "ana_player_render_tap", StringMarshalling = StringMarshalling.Utf8)] public static partial int RenderTap(nint p, uint mask, string path);
    [LibraryImport(Lib, EntryPoint = "ana_player_bounce", StringMarshalling = StringMarshalling.Utf8)] public static partial int Bounce(nint p, string path);
    [LibraryImport(Lib, EntryPoint = "ana_render_chain", StringMarshalling = StringMarshalling.Utf8)] public static partial int RenderChain(string input, in ChannelParamsNative c, string output);
}

/// One EQ band (type 0 bell, 1 low shelf, 2 high shelf).
public sealed class Band
{
    public float Freq { get; set; }
    public float Gain { get; set; }
    public float Q { get; set; } = 1;
    public int Type { get; set; }
    public bool On { get; set; } = true;
    internal EqBandNative Native => new() { FreqHz = Freq, GainDb = Gain, Q = Q, Type = (byte)Type, On = (byte)(On ? 1 : 0) };
    internal static Band From(EqBandNative b) => new() { Freq = b.FreqHz, Gain = b.GainDb, Q = b.Q, Type = b.Type, On = b.On != 0 };
}

/// A channel strip's settings, in console order.
public sealed class ChannelMix
{
    public float Trim { get; set; }
    public bool HpOn { get; set; }
    public float Hp { get; set; } = 80;
    public bool LpOn { get; set; }
    public float Lp { get; set; } = 12000;
    public bool Steep { get; set; }
    public bool GateOn { get; set; }
    public float GateThreshold { get; set; } = -50;
    public float GateAttack { get; set; } = 1;
    public float GateRelease { get; set; } = 120;
    public float GateRange { get; set; } = 40;
    public bool EqOn { get; set; }
    public Band[] Bands { get; set; } = [];
    public bool CompOn { get; set; }
    public float CompThreshold { get; set; } = -18;
    public float CompRatio { get; set; } = 3;
    public float CompAttack { get; set; } = 10;
    public float CompRelease { get; set; } = 120;
    public float CompMakeup { get; set; }
    public float Fader { get; set; }
    public float Pan { get; set; }
    public bool Mute { get; set; }
    public bool Solo { get; set; }
    /// S8: VST3 effects before the chain (rendered offline by the host helper), in order.
    public List<VstInsert> Vst { get; set; } = [];
    /// What the rendered VST3 source is (set when it is in the player): part of the tap's key.
    [System.Text.Json.Serialization.JsonIgnore] public string VstSignature { get; set; } = "";

    public static ChannelMix Default()
    {
        ChannelParamsNative c = default;
        try { MixApi.ChannelDefaults(out c); } catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
        var m = new ChannelMix { Hp = c.HpHz, Lp = c.LpHz, GateThreshold = c.GateThresholdDb, GateAttack = c.GateAttackMs, GateRelease = c.GateReleaseMs, GateRange = c.GateRangeDb,
            CompThreshold = c.CompThresholdDb, CompRatio = c.CompRatio, CompAttack = c.CompAttackMs, CompRelease = c.CompReleaseMs, CompMakeup = c.CompMakeupDb };
        m.Bands = [Band.From(c.Eq0), Band.From(c.Eq1), Band.From(c.Eq2), Band.From(c.Eq3)];
        return m;
    }

    // ---- The chain as patched (the DS-C cables): built-in units and VST3 inserts in any order.

    public static readonly string[] Units = ["trim", "filter", "gate", "eq", "comp"];
    /// Slot ids in signal order ("trim", "filter", "gate", "eq", "comp", "vst:<id>"); a built-in
    /// unit missing is unpatched. Empty: the console order with the inserts first (as before S10).
    public List<string> Chain { get; set; } = [];
    /// A parallel path over built-in units: first .. last (slot ids), added to the dry signal at ParallelMix.
    public string? ParallelFirst { get; set; }
    public string? ParallelLast { get; set; }
    public float ParallelMix { get; set; }

    /// Why the patch makes no path for this track right now (the chain stays as it was, the track is
    /// silent), null when it does. Set from the lunchbox (StudioState.SyncBox).
    [System.Text.Json.Serialization.JsonIgnore] public string? RouteError { get; set; }
    /// Where the track goes: 0 the master, k into track k-1 (its patch ends at that track's return).
    [System.Text.Json.Serialization.JsonIgnore] public int SendTo { get; set; }

    public static string SlotOf(VstInsert v) => "vst:" + v.Id;
    public VstInsert? VstOf(string slot) => Vst.FirstOrDefault(v => SlotOf(v) == slot);

    /// The chain in signal order: the stored one, minus inserts that are gone, plus new ones first.
    public List<string> Slots()
    {
        var ids = Vst.Select(SlotOf).ToList();
        if (Chain.Count == 0) return [.. ids, .. Units];
        var l = Chain.Where(s => Units.Contains(s) || ids.Contains(s)).Distinct().ToList();
        for (int i = ids.Count - 1; i >= 0; i--) if (!l.Contains(ids[i])) l.Insert(0, ids[i]);
        return l;
    }

    /// The built-in units between the inserts that are on: one run before each insert (rendered
    /// offline into it), and the last run after them, which plays in real time.
    public List<List<string>> Segments()
    {
        var segs = new List<List<string>> { new() };
        foreach (var s in Slots())
            if (Units.Contains(s)) segs[^1].Add(s);
            else if (VstOf(s) is { On: true }) segs.Add([]);
        return segs;
    }

    /// The units patched after the last insert that is on: what the player runs.
    internal ChannelParamsNative Realtime
    {
        get
        {
            var n = NativeFor(Segments()[^1]);
            if (RouteError is not null) n.Mute = 1;   // a cable pulled out of the path: no sound until it is back
            return n;
        }
    }

    internal unsafe ChannelParamsNative NativeFor(List<string> units)
    {
        var n = Native;
        for (int i = 0; i < 5; i++) n.Order[i] = (byte)(i < units.Count ? Array.IndexOf(Units, units[i]) + 1 : 0);
        int a = ParallelFirst is null ? -1 : units.IndexOf(ParallelFirst), b = ParallelLast is null ? -1 : units.IndexOf(ParallelLast);
        if (a >= 0 && b >= a && ParallelMix > 0) (n.ParallelFrom, n.ParallelTo, n.ParallelMix) = ((byte)a, (byte)(b + 1), ParallelMix);
        if (units.Count == 0) n.Order[0] = 6;   // nothing patched: a plain wire (any other id; all 0 would be the console order)
        return n;
    }

    internal ChannelParamsNative Native => new()
    {
        TrimDb = Trim, HpHz = Hp, LpHz = Lp, HpOn = B(HpOn), LpOn = B(LpOn), Steep = B(Steep), GateOn = B(GateOn),
        GateThresholdDb = GateThreshold, GateAttackMs = GateAttack, GateReleaseMs = GateRelease, GateRangeDb = GateRange,
        EqOn = B(EqOn), CompOn = B(CompOn), Mute = B(Mute), Solo = B(Solo),
        Eq0 = Bands.ElementAtOrDefault(0)?.Native ?? default, Eq1 = Bands.ElementAtOrDefault(1)?.Native ?? default,
        Eq2 = Bands.ElementAtOrDefault(2)?.Native ?? default, Eq3 = Bands.ElementAtOrDefault(3)?.Native ?? default,
        CompThresholdDb = CompThreshold, CompRatio = CompRatio, CompAttackMs = CompAttack, CompReleaseMs = CompRelease, CompMakeupDb = CompMakeup,
        FaderDb = Fader, Pan = Pan, SendTo = (byte)SendTo,
    };
    static byte B(bool v) => (byte)(v ? 1 : 0);

    /// What changes the audio at the analysis tap (fader, pan, mute, solo do not): the cache key.
    public string TapKey => JsonSerializer.Serialize(new { Trim, HpOn, Hp, LpOn, Lp, Steep, GateOn, GateThreshold, GateAttack, GateRelease, GateRange, EqOn, Bands, CompOn, CompThreshold, CompRatio, CompAttack, CompRelease, CompMakeup, VstSignature, Chain = Slots(), ParallelFirst, ParallelLast, ParallelMix });
}

/// A module in the session's lunchbox (DS-C): a built-in unit (its settings in P, only its own
/// fields are read) or a VST3 insert, sitting from `Slot` over as many slots as it is wide.
public sealed class BoxModule
{
    public string Id { get; set; } = "";     // "eq1", "comp2", "vst:<insert id>"
    public string Type { get; set; } = "";   // "trim", "filter", "gate", "eq", "comp", "vst"
    public int Slot { get; set; }
    public ChannelMix P { get; set; } = new();
    public VstInsert? Vst { get; set; }
}

/// The lunchbox's patch: which track goes through which modules, read from the cables. A track's
/// patchbay has an output (SAI, "<track>.sai:out") and a return (VOLTA, "<track>.volta:in"); a
/// module's jacks are "<id>:in" and "<id>:out". Normalled: a track whose SAI has no cable goes
/// straight to its own return.
public static class Patchbay
{
    public static string Sai(string track) => track + ".sai:out";
    public static string Volta(string track) => track + ".volta:in";
    public static string Node(string jack) => jack[..jack.LastIndexOf(':')];

    public sealed record TrackRoute(List<string> Modules, string? First, string? Last, string? SendTo, string? Error);

    /// Every track's route. A module used by two tracks, two modules of one kind on a track, or an
    /// insert in a parallel path are not possible yet: that track has no route and says why.
    public static Dictionary<string, TrackRoute> Route(IReadOnlyList<CableLink> cables, IReadOnlyList<BoxModule> box, IReadOnlyList<string> tracks, Func<string, string> name)
    {
        var routes = new Dictionary<string, TrackRoute>();
        var type = box.ToDictionary(m => m.Id, m => m.Type);
        foreach (var t in tracks)
        {
            if (!cables.Any(k => k.From == Sai(t))) { routes[t] = new([], null, null, null, null); continue; }   // normalled
            var (order, a, b, end, err) = Walk(cables, t + ".sai", n => n.EndsWith(".volta"), name);
            if (err is null && (a is not null) && order!.Skip(order.IndexOf(a)).Take(order.IndexOf(b!) - order.IndexOf(a) + 1).Any(m => type.GetValueOrDefault(m) is "vst" or null))
                err = "o caminho paralelo leva só módulos da casa, sem VST3";
            if (err is null && order!.GroupBy(m => type.GetValueOrDefault(m)).FirstOrDefault(g => g.Count() > 1 && g.Key != "vst") is { } dup)
                err = $"dois {name(dup.First()).Split(' ')[0]} na mesma faixa: ainda não";
            if (err is null && order!.Any(m => !type.ContainsKey(m))) err = "um cabo vai para outra faixa no meio do caminho";
            string? to = end is null ? null : end[..^".volta".Length];
            routes[t] = err is null ? new(order!, a, b, to == t ? null : to, null) : new([], null, null, null, err);
        }
        foreach (var g in routes.Where(r => r.Value.Error is null).SelectMany(r => r.Value.Modules.Select(m => (m, r.Key))).GroupBy(x => x.m).Where(g => g.Count() > 1))
            foreach (var (_, t) in g) routes[t] = new([], null, null, null, $"{name(g.Key)} está em duas faixas: ainda não");
        foreach (var (t, r) in routes.Where(r => r.Value.SendTo is { } to && tracks.Contains(to) && routes[to].SendTo is not null).ToList())
            routes[t] = r with { SendTo = null, Error = "volta numa faixa que também vai para outra: ainda não" };
        return routes;
    }

    /// From `start`, cable by cable, to a node where `isEnd`. One output may feed two cables once: a
    /// run of modules on one, straight to where the run comes back on the other (the parallel path).
    public static (List<string>? Order, string? First, string? Last, string? End, string? Error) Walk(IReadOnlyList<CableLink> cables, string start, Func<string, bool> isEnd, Func<string, string> name)
    {
        var outs = new Dictionary<string, List<string>>();
        var ins = new Dictionary<string, int>();
        foreach (var c in cables)
        {
            string f = Node(c.From), t = Node(c.To);
            if (!outs.TryGetValue(f, out var l)) outs[f] = l = [];
            l.Add(t);
            ins[t] = ins.GetValueOrDefault(t) + 1;
        }
        var order = new List<string>();
        string? first = null, last = null;
        string node = start;
        var seen = new HashSet<string> { start };
        while (true)
        {
            var o = outs.GetValueOrDefault(node) ?? [];
            if (o.Count == 0) return (null, null, null, null, node == start ? "a saída não está ligada" : $"a saída do {name(node)} não está ligada");
            if (o.Count > 2) return (null, null, null, null, "uma saída alimenta mais de dois cabos");
            string next;
            if (o.Count == 1) next = o[0];
            else
            {
                if (first is not null) return (null, null, null, null, "só um caminho paralelo por faixa");
                List<string>? run = null;
                string merge = "";
                foreach (var (dry, wet) in new[] { (o[0], o[1]), (o[1], o[0]) })
                {
                    var r = new List<string>();
                    string n = wet;
                    while (n != dry && !isEnd(n) && r.Count < 12 && (outs.GetValueOrDefault(n)?.Count ?? 0) == 1) { r.Add(n); n = outs[n][0]; }
                    if (n == dry && r.Count > 0) { run = r; merge = dry; break; }
                }
                if (run is null || ins.GetValueOrDefault(merge) != 2) return (null, null, null, null, "o caminho dividido não volta a se juntar");
                foreach (var u in run) if (!seen.Add(u)) return (null, null, null, null, "um cabo faz o sinal voltar (laço)");
                order.AddRange(run);
                (first, last) = (run[0], run[^1]);
                next = merge;
            }
            if (isEnd(next)) return (order, first, last, next, null);
            if (!seen.Add(next)) return (null, null, null, null, "um cabo faz o sinal voltar (laço)");
            order.Add(next);
            node = next;
        }
    }

    /// A unit's own settings from one strip to another (a module's into the track it is on).
    public static void CopyUnit(ChannelMix from, ChannelMix to, string type)
    {
        switch (type)
        {
            case "trim": to.Trim = from.Trim; break;
            case "filter": (to.HpOn, to.Hp, to.LpOn, to.Lp, to.Steep) = (from.HpOn, from.Hp, from.LpOn, from.Lp, from.Steep); break;
            case "gate": (to.GateOn, to.GateThreshold, to.GateAttack, to.GateRelease, to.GateRange) = (from.GateOn, from.GateThreshold, from.GateAttack, from.GateRelease, from.GateRange); break;
            case "eq": (to.EqOn, to.Bands) = (from.EqOn, from.Bands.Select(b => new Band { Freq = b.Freq, Gain = b.Gain, Q = b.Q, Type = b.Type, On = b.On }).ToArray()); break;
            case "comp": (to.CompOn, to.CompThreshold, to.CompRatio, to.CompAttack, to.CompRelease, to.CompMakeup) = (from.CompOn, from.CompThreshold, from.CompRatio, from.CompAttack, from.CompRelease, from.CompMakeup); break;
        }
    }
}

/// A patch cable from an output jack to an input jack (DS-C).
public sealed class CableLink
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    /// Which of the box's cables it is (its colour), kept while it is replugged.
    public int Colour { get; set; } = -1;
}

/// One VST3 effect on a channel, before its chain; its state lives next to the take project.
public sealed class VstInsert
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Bundle { get; set; } = "";
    public string Cid { get; set; } = "";
    public string Name { get; set; } = "";
    public string Vendor { get; set; } = "";
    public bool On { get; set; } = true;
    /// Values set in the rack's panel, applied over the state at every render.
    public Dictionary<uint, double> Params { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore] public PluginHost.Plugin Plugin => new(Bundle, Cid, Name, Vendor, "", "");
}

public sealed class MasterMix
{
    public bool EqOn { get; set; }
    public Band[] Bands { get; set; } = [];
    public bool CompOn { get; set; }
    public float CompThreshold { get; set; } = -12;
    public float CompRatio { get; set; } = 2;
    public float CompAttack { get; set; } = 20;
    public float CompRelease { get; set; } = 200;
    public float CompMakeup { get; set; }
    public bool LimiterOn { get; set; }
    public float Ceiling { get; set; } = -1;
    public float LimiterRelease { get; set; } = 80;
    public float Fader { get; set; }

    public static MasterMix Default()
    {
        MasterParamsNative m = default;
        try { MixApi.MasterDefaults(out m); } catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
        return new MasterMix { Bands = [Band.From(m.Eq0), Band.From(m.Eq1), Band.From(m.Eq2), Band.From(m.Eq3)], CompThreshold = m.CompThresholdDb, CompRatio = m.CompRatio,
            CompAttack = m.CompAttackMs, CompRelease = m.CompReleaseMs, Ceiling = m.LimiterCeilingDb, LimiterRelease = m.LimiterReleaseMs, LimiterOn = false };
    }

    internal MasterParamsNative Native => new()
    {
        EqOn = (byte)(EqOn ? 1 : 0), CompOn = (byte)(CompOn ? 1 : 0), LimiterOn = (byte)(LimiterOn ? 1 : 0),
        Eq0 = Bands.ElementAtOrDefault(0)?.Native ?? default, Eq1 = Bands.ElementAtOrDefault(1)?.Native ?? default,
        Eq2 = Bands.ElementAtOrDefault(2)?.Native ?? default, Eq3 = Bands.ElementAtOrDefault(3)?.Native ?? default,
        CompThresholdDb = CompThreshold, CompRatio = CompRatio, CompAttackMs = CompAttack, CompReleaseMs = CompRelease, CompMakeupDb = CompMakeup,
        LimiterCeilingDb = Ceiling, LimiterReleaseMs = LimiterRelease, FaderDb = Fader,
    };
}

/// A take's mix: one strip per channel by name ("mix" = the take, then the stems), and the master.
public sealed class TakeMix
{
    public Dictionary<string, ChannelMix> Channels { get; set; } = [];
    /// The session's lunchbox: its modules and the cables (DS-C). Boxed: the tracks' chains from
    /// before it are already in the box as modules.
    public List<BoxModule> Box { get; set; } = [];
    public List<CableLink> Patch { get; set; } = [];
    public bool Boxed { get; set; }
    public MasterMix Master { get; set; } = MasterMix.Default();

    public ChannelMix For(string name, bool stemsPresent)
    {
        if (!Channels.TryGetValue(name, out var c))
        {
            c = ChannelMix.Default();
            if (name == "mix" && stemsPresent) c.Mute = true;   // with stems the original would double them
            Channels[name] = c;
        }
        return c;
    }
}

/// Meters as the UI reads them.
public sealed class MixMeters
{
    public float[] Peak = new float[8], Rms = new float[8], Gr = new float[8];
    public bool[] GateOpen = new bool[8];
    public float[] MasterPeak = new float[2], MasterRms = new float[2];
    public float MasterGr, LimiterGr;
    public int Tracks;
    public const int Stages = 6;                   // in, trim, filter, gate, EQ, compressor
    public float[,] Stage = new float[8, Stages];  // peak after each unit: the cable levels
    public float LufsM = -120, LufsS = -120, LufsI = -120, Lra, TruePeak = -120;
}
