using System.Runtime.InteropServices;
using System.Text.Json;

namespace Dissonancia;

// STUDIO S4 — mixing (docs/studio-plan.md): the C# side of the core's mixing engine. A channel's
// settings in console order (trim, filters, gate, EQ, compressor, fader, pan, mute, solo), the
// master's, per take in <takes>/studio/<name>.mix.json, and the native mirrors.

[StructLayout(LayoutKind.Sequential)]
struct EqBandNative { public float FreqHz, GainDb, Q; public byte Type, On; ushort _pad0; }

[StructLayout(LayoutKind.Sequential)]
struct ChannelParamsNative
{
    public float TrimDb, HpHz, LpHz;
    public byte HpOn, LpOn, Steep, GateOn;
    public float GateThresholdDb, GateAttackMs, GateReleaseMs, GateRangeDb;
    public byte EqOn, CompOn, Mute, Solo;
    public EqBandNative Eq0, Eq1, Eq2, Eq3;
    public float CompThresholdDb, CompRatio, CompAttackMs, CompReleaseMs, CompMakeupDb;
    public float FaderDb, Pan;
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
}

static partial class MixApi
{
    const string Lib = "dissonancia";
    [LibraryImport(Lib, EntryPoint = "ana_player_add_track", StringMarshalling = StringMarshalling.Utf8)] public static partial int AddTrack(nint p, string path);
    [LibraryImport(Lib, EntryPoint = "ana_player_clear_tracks")] public static partial void ClearTracks(nint p);
    [LibraryImport(Lib, EntryPoint = "ana_channel_defaults")] public static partial void ChannelDefaults(out ChannelParamsNative c);
    [LibraryImport(Lib, EntryPoint = "ana_master_defaults")] public static partial void MasterDefaults(out MasterParamsNative m);
    [LibraryImport(Lib, EntryPoint = "ana_player_set_channel")] public static partial int SetChannel(nint p, int track, in ChannelParamsNative c);
    [LibraryImport(Lib, EntryPoint = "ana_player_set_master")] public static partial void SetMaster(nint p, in MasterParamsNative m);
    [LibraryImport(Lib, EntryPoint = "ana_player_meters")] public static partial void Meters(nint p, out MixMetersNative m);
    [LibraryImport(Lib, EntryPoint = "ana_player_render_tap", StringMarshalling = StringMarshalling.Utf8)] public static partial int RenderTap(nint p, uint mask, string path);
    [LibraryImport(Lib, EntryPoint = "ana_player_bounce", StringMarshalling = StringMarshalling.Utf8)] public static partial int Bounce(nint p, string path);
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

    public static ChannelMix Default()
    {
        ChannelParamsNative c = default;
        try { MixApi.ChannelDefaults(out c); } catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
        var m = new ChannelMix { Hp = c.HpHz, Lp = c.LpHz, GateThreshold = c.GateThresholdDb, GateAttack = c.GateAttackMs, GateRelease = c.GateReleaseMs, GateRange = c.GateRangeDb,
            CompThreshold = c.CompThresholdDb, CompRatio = c.CompRatio, CompAttack = c.CompAttackMs, CompRelease = c.CompReleaseMs, CompMakeup = c.CompMakeupDb };
        m.Bands = [Band.From(c.Eq0), Band.From(c.Eq1), Band.From(c.Eq2), Band.From(c.Eq3)];
        return m;
    }

    internal ChannelParamsNative Native => new()
    {
        TrimDb = Trim, HpHz = Hp, LpHz = Lp, HpOn = B(HpOn), LpOn = B(LpOn), Steep = B(Steep), GateOn = B(GateOn),
        GateThresholdDb = GateThreshold, GateAttackMs = GateAttack, GateReleaseMs = GateRelease, GateRangeDb = GateRange,
        EqOn = B(EqOn), CompOn = B(CompOn), Mute = B(Mute), Solo = B(Solo),
        Eq0 = Bands.ElementAtOrDefault(0)?.Native ?? default, Eq1 = Bands.ElementAtOrDefault(1)?.Native ?? default,
        Eq2 = Bands.ElementAtOrDefault(2)?.Native ?? default, Eq3 = Bands.ElementAtOrDefault(3)?.Native ?? default,
        CompThresholdDb = CompThreshold, CompRatio = CompRatio, CompAttackMs = CompAttack, CompReleaseMs = CompRelease, CompMakeupDb = CompMakeup,
        FaderDb = Fader, Pan = Pan,
    };
    static byte B(bool v) => (byte)(v ? 1 : 0);

    /// What changes the audio at the analysis tap (fader, pan, mute, solo do not): the cache key.
    public string TapKey => JsonSerializer.Serialize(new { Trim, HpOn, Hp, LpOn, Lp, Steep, GateOn, GateThreshold, GateAttack, GateRelease, GateRange, EqOn, Bands, CompOn, CompThreshold, CompRatio, CompAttack, CompRelease, CompMakeup });
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
}
