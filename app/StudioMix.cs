using System.Runtime.InteropServices;
using System.Text.Json;

namespace Dissonancia;

// STUDIO S4 / S11 — mixing (docs/IMPLEMENTATION.md): the C# side of the core's mixer graph. A track's
// strip (fader, pan, mute, solo, polarity), the lunchbox's modules and cables, the master; per take
// in <takes>/studio/<name>.mix.json; and the native mirrors (MixGraph).

[StructLayout(LayoutKind.Sequential)]
struct EqBandNative { public float FreqHz, GainDb, Q; public byte Type, On; ushort _pad0; }

[StructLayout(LayoutKind.Sequential)]
struct UnitParamsNative
{
    public ushort Id;
    public byte Kind, On;   // kind: 0 wire, 1 trim, 2 filter, 3 gate, 4 EQ, 5 compressor
    public float TrimDb, HpHz, LpHz;
    public byte HpOn, LpOn, Steep, _pad0;
    public float GateThresholdDb, GateAttackMs, GateReleaseMs, GateRangeDb;
    public EqBandNative Eq0, Eq1, Eq2, Eq3;
    public float CompThresholdDb, CompRatio, CompAttackMs, CompReleaseMs, CompMakeupDb;
}

/// Jack kinds: 0 a track's SAI, 1 its AUX, 2 its VOLTA, 3 a unit's IN, 4 its OUT, 5 the master.
[StructLayout(LayoutKind.Sequential)]
struct CableNative { public byte FromKind, From, ToKind, To; public float Gain; }

[StructLayout(LayoutKind.Sequential)]
struct TrackParamsNative { public float FaderDb, Pan; public byte Mute, Solo, Polarity, _pad0; }

[System.Runtime.CompilerServices.InlineArray(8)] struct TrackArray8 { TrackParamsNative _e; }
[System.Runtime.CompilerServices.InlineArray(32)] struct UnitArray32 { UnitParamsNative _e; }
[System.Runtime.CompilerServices.InlineArray(96)] struct CableArray96 { CableNative _e; }

[StructLayout(LayoutKind.Sequential)]
struct MixGraphNative
{
    public TrackArray8 Track;
    public UnitArray32 Unit;
    public CableArray96 Cable;
    public int Units, Cables;
    public MasterParamsNative Master;
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
    public fixed float PeakDb[8], RmsDb[8], InDb[8], ReturnDb[8], UnitDb[32], UnitGrDb[32];
    public fixed float MasterPeakDb[2], MasterRmsDb[2];
    public float MasterCompGrDb, LimiterGrDb;
    public int Tracks;
    public uint Underruns;
    public float LufsMomentary, LufsShortTerm, LufsIntegrated, LoudnessRange;
    public fixed float TruePeakDb[2];
}

static partial class MixApi
{
    const string Lib = "dissonancia";
    [LibraryImport(Lib, EntryPoint = "ana_player_add_track", StringMarshalling = StringMarshalling.Utf8)] public static partial int AddTrack(nint p, string path);
    [LibraryImport(Lib, EntryPoint = "ana_player_clear_tracks")] public static partial void ClearTracks(nint p);
    [LibraryImport(Lib, EntryPoint = "ana_player_set_source", StringMarshalling = StringMarshalling.Utf8)] public static partial int SetSource(nint p, int track, string? path);
    [LibraryImport(Lib, EntryPoint = "ana_unit_defaults")] public static partial void UnitDefaults(out UnitParamsNative u);
    [LibraryImport(Lib, EntryPoint = "ana_master_defaults")] public static partial void MasterDefaults(out MasterParamsNative m);
    [LibraryImport(Lib, EntryPoint = "ana_player_set_mix")] public static partial int SetMix(nint p, in MixGraphNative g);
    [LibraryImport(Lib, EntryPoint = "ana_player_meters")] public static partial void Meters(nint p, out MixMetersNative m);
    [LibraryImport(Lib, EntryPoint = "ana_player_scope")] public static unsafe partial int Scope(nint p, int source, float* stereo, int frames);
    [LibraryImport(Lib, EntryPoint = "ana_player_set_monitor")] public static partial void SetMonitor(nint p, int track);
    [LibraryImport(Lib, EntryPoint = "ana_player_reset_loudness")] public static partial void ResetLoudness(nint p);
    [LibraryImport(Lib, EntryPoint = "ana_player_pull")] public static partial void Pull(nint p, int frames);
    [LibraryImport(Lib, EntryPoint = "ana_player_render_tap", StringMarshalling = StringMarshalling.Utf8)] public static partial int RenderTap(nint p, uint mask, string path);
    [LibraryImport(Lib, EntryPoint = "ana_player_bounce", StringMarshalling = StringMarshalling.Utf8)] public static partial int Bounce(nint p, string path);
    [LibraryImport(Lib, EntryPoint = "ana_render_chain", StringMarshalling = StringMarshalling.Utf8)] public static unsafe partial int RenderChain(string input, UnitParamsNative* units, int count, string output);
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

/// A track's strip (fader, pan, mute, solo, polarity); also a module's settings (BoxModule.P: only its
/// own unit's fields are read).
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
    /// The VST3 inserts the track's signal goes through (set from the lunchbox); in a take from
    /// before it, its inserts.
    public List<VstInsert> Vst { get; set; } = [];
    /// What the rendered VST3 source is (set when it is in the player): part of the tap's key.
    [System.Text.Json.Serialization.JsonIgnore] public string VstSignature { get; set; } = "";

    /// The source inverted (Ø), before SAI.
    public bool Polarity { get; set; }

    public static ChannelMix Default()
    {
        UnitParamsNative c = default;
        try { MixApi.UnitDefaults(out c); } catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
        var m = new ChannelMix { Hp = c.HpHz, Lp = c.LpHz, GateThreshold = c.GateThresholdDb, GateAttack = c.GateAttackMs, GateRelease = c.GateReleaseMs, GateRange = c.GateRangeDb,
            CompThreshold = c.CompThresholdDb, CompRatio = c.CompRatio, CompAttack = c.CompAttackMs, CompRelease = c.CompReleaseMs, CompMakeup = c.CompMakeupDb };
        m.Bands = [Band.From(c.Eq0), Band.From(c.Eq1), Band.From(c.Eq2), Band.From(c.Eq3)];
        return m;
    }

    public static readonly string[] Units = ["trim", "filter", "gate", "eq", "comp"];
    /// The chain before the lunchbox (S10), kept to bring an old take into the box: slot ids in
    /// signal order ("trim", "filter", "gate", "eq", "comp", "vst:<id>"); empty: console order, inserts first.
    public List<string> Chain { get; set; } = [];

    public static string SlotOf(VstInsert v) => "vst:" + v.Id;
    public VstInsert? VstOf(string slot) => Vst.FirstOrDefault(v => SlotOf(v) == slot);

    /// The old chain in signal order: the stored one, minus inserts that are gone, plus new ones first.
    public List<string> Slots()
    {
        var ids = Vst.Select(SlotOf).ToList();
        if (Chain.Count == 0) return [.. ids, .. Units];
        var l = Chain.Where(s => Units.Contains(s) || ids.Contains(s)).Distinct().ToList();
        for (int i = ids.Count - 1; i >= 0; i--) if (!l.Contains(ids[i])) l.Insert(0, ids[i]);
        return l;
    }

    /// Whether a unit of this type does anything with these settings.
    public bool IsOn(string type) => type switch { "trim" => true, "filter" => HpOn || LpOn, "gate" => GateOn, "eq" => EqOn, "comp" => CompOn, _ => false };

    /// These settings as one unit of the mixer graph (a wire: a VST3 insert, or units already rendered into the track's source).
    internal UnitParamsNative UnitNative(string type, ushort id, bool wire) => new()
    {
        Id = id, Kind = (byte)(wire ? 0 : Array.IndexOf(Units, type) + 1), On = B(!wire && IsOn(type)),
        TrimDb = Trim, HpHz = Hp, LpHz = Lp, HpOn = B(HpOn), LpOn = B(LpOn), Steep = B(Steep),
        GateThresholdDb = GateThreshold, GateAttackMs = GateAttack, GateReleaseMs = GateRelease, GateRangeDb = GateRange,
        Eq0 = Bands.ElementAtOrDefault(0)?.Native ?? default, Eq1 = Bands.ElementAtOrDefault(1)?.Native ?? default,
        Eq2 = Bands.ElementAtOrDefault(2)?.Native ?? default, Eq3 = Bands.ElementAtOrDefault(3)?.Native ?? default,
        CompThresholdDb = CompThreshold, CompRatio = CompRatio, CompAttackMs = CompAttack, CompReleaseMs = CompRelease, CompMakeupDb = CompMakeup,
    };

    internal TrackParamsNative TrackNative => new() { FaderDb = Fader, Pan = Pan, Mute = B(Mute), Solo = B(Solo), Polarity = B(Polarity) };
    static byte B(bool v) => (byte)(v ? 1 : 0);

    /// A unit's own settings, as the tap key and the render cache see them.
    public string UnitKey(string type) => type switch
    {
        "trim" => JsonSerializer.Serialize(new { Trim }),
        "filter" => JsonSerializer.Serialize(new { HpOn, Hp, LpOn, Lp, Steep }),
        "gate" => JsonSerializer.Serialize(new { GateOn, GateThreshold, GateAttack, GateRelease, GateRange }),
        "eq" => JsonSerializer.Serialize(new { EqOn, Bands }),
        "comp" => JsonSerializer.Serialize(new { CompOn, CompThreshold, CompRatio, CompAttack, CompRelease, CompMakeup }),
        _ => "",
    };
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

/// The lunchbox's patch. Jacks: a track's SAI ("<track>.sai:out", its source), AUX ("<track>.aux:out",
/// after its fader: a send) and VOLTA ("<track>.volta:in", its insert return, into its fader); a
/// module's "<id>:in" / "<id>:out"; the master's input ("master:in"). Cables into one input are summed,
/// one output on several cables is copied. Normalled: a track whose SAI has no cable goes straight
/// to its own VOLTA. The core plays the graph as patched (MixGraph); this reads it for the UI.
public static class Patchbay
{
    public const string Master = "master:in";
    public static string Sai(string track) => track + ".sai:out";
    public static string Aux(string track) => track + ".aux:out";
    public static string Volta(string track) => track + ".volta:in";
    public static string Node(string jack) => jack[..jack.LastIndexOf(':')];

    /// Every node reached from `starts` along the cables, in signal order (by the longest way in:
    /// a node comes after everything that feeds it from there).
    public static List<string> Reach(IReadOnlyList<CableLink> cables, params string[] starts)
    {
        var outs = cables.ToLookup(c => Node(c.From), c => Node(c.To));
        var seen = new List<string>();
        var queue = new Queue<string>(starts);
        while (queue.Count > 0)
            foreach (var n in outs[queue.Dequeue()])
                if (!seen.Contains(n)) { seen.Add(n); queue.Enqueue(n); }
        var depth = starts.ToDictionary(n => n, _ => 0);
        for (int pass = 0; pass <= seen.Count; pass++)   // bounded: a loop does not run away
            foreach (var c in cables)
                if (depth.TryGetValue(Node(c.From), out int d) && depth.GetValueOrDefault(Node(c.To), -1) < d + 1) depth[Node(c.To)] = d + 1;
        return seen.OrderBy(n => depth.GetValueOrDefault(n)).ToList();
    }

    /// Every node that feeds `node`, however far back.
    public static List<string> Feeders(IReadOnlyList<CableLink> cables, string node)
    {
        var ins = cables.ToLookup(c => Node(c.To), c => Node(c.From));
        var seen = new List<string>();
        var queue = new Queue<string>([node]);
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            foreach (var f in ins[n].Concat(n.EndsWith(".aux") ? [n[..^4] + ".volta"] : []))   // a track's AUX comes after its VOLTA
                if (!seen.Contains(f)) { seen.Add(f); queue.Enqueue(f); }
        }
        return seen;
    }

    /// The run of modules straight from a track's SAI: each the only cable on, fed by nothing else.
    public static List<string> Head(IReadOnlyList<CableLink> cables, string track)
    {
        var run = new List<string>();
        string node = track + ".sai";
        while (true)
        {
            var o = cables.Where(c => Node(c.From) == node).ToList();
            if (o.Count != 1) return run;
            string next = Node(o[0].To);
            if (next.Contains('.') || next == "master" || run.Contains(next) || cables.Count(c => Node(c.To) == next) != 1) return run;
            run.Add(next);
            node = next;
        }
    }

    /// The nodes on a loop (the core leaves them silent, and what they feed).
    public static HashSet<string> Loops(IReadOnlyList<CableLink> cables)
    {
        var edges = cables.Select(c => (Node(c.From), Node(c.To))).ToList();
        foreach (var a in edges.Select(e => e.Item1).Concat(edges.Select(e => e.Item2)).Where(n => n.EndsWith(".aux") || n.EndsWith(".volta")).Distinct().ToList())
            edges.Add((a[..a.LastIndexOf('.')] + ".volta", a[..a.LastIndexOf('.')] + ".aux"));   // through the strip
        var nodes = edges.SelectMany(e => new[] { e.Item1, e.Item2 }).ToHashSet();
        var indeg = nodes.ToDictionary(n => n, n => edges.Count(e => e.Item2 == n));
        var queue = new Queue<string>(nodes.Where(n => indeg[n] == 0));
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            nodes.Remove(n);
            foreach (var e in edges.Where(e => e.Item1 == n)) if (--indeg[e.Item2] == 0) queue.Enqueue(e.Item2);
        }
        return nodes;
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
    /// Its level (linear): an attenuator in line, for a send or one of several paths into a jack.
    public float Gain { get; set; } = 1;
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
    public float[] Peak = new float[8], Rms = new float[8], In = new float[8], Return = new float[8];
    public float[] Unit = new float[32], UnitGr = new float[32];   // by the unit's place in the graph (StudioState.MeterOf)
    public float[] MasterPeak = new float[2], MasterRms = new float[2];
    public float MasterGr, LimiterGr;
    public int Tracks;
    public uint Underruns;
    public float LufsM = -120, LufsS = -120, LufsI = -120, Lra, TruePeak = -120;
}
