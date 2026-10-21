using System.Runtime.InteropServices;
using System.Text.Json;

namespace Dissonancia;

// LIVE playground (creation): the C# side of the core's pads, sampler, sequencer and MIDI input —
// what is loaded and patterned, kept in <config>/Dissonancia/playground.json and sent to the core
// at every START (the core decodes the WAVs at the input's rate); MIDI learn maps a controller's
// knobs and buttons to the playground's and the transport's keys.

[StructLayout(LayoutKind.Sequential)]
unsafe struct PlaygroundConfigNative
{
    public fixed ushort Steps[16];
    public fixed float PadGainDb[16];
    public fixed float PadPan[16];
    public float SamplerGainDb, MasterDb;
    public byte SequencerOn, SamplerRoot;
    ushort _pad0;
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct PlaygroundStatusNative
{
    public fixed uint Hits[17];
    public fixed float Seconds[17];
    public float PeakDb;
    public int Step, Voices;
    public uint MidiMessages;
}

static partial class PgApi
{
    const string Lib = "dissonancia";
    [LibraryImport(Lib, EntryPoint = "ana_pad_load", StringMarshalling = StringMarshalling.Utf8)] public static partial int Load(nint h, int slot, string path);
    [LibraryImport(Lib, EntryPoint = "ana_pad_hit")] public static partial void Hit(nint h, int pad, float velocity);
    [LibraryImport(Lib, EntryPoint = "ana_midi_send")] public static partial void Send(nint h, byte status, byte d1, byte d2);
    [LibraryImport(Lib, EntryPoint = "ana_playground_set")] public static partial void Set(nint h, in PlaygroundConfigNative c);
    [LibraryImport(Lib, EntryPoint = "ana_playground_status")] public static partial void Status(nint h, out PlaygroundStatusNative s);
    [LibraryImport(Lib, EntryPoint = "ana_midi_port_count")] public static partial int PortCount(nint h);
    [LibraryImport(Lib, EntryPoint = "ana_midi_port_name")] public static unsafe partial int PortName(nint h, int index, byte* utf8, int cap);
    [LibraryImport(Lib, EntryPoint = "ana_midi_open")] public static partial int Open(nint h, int index);
    [LibraryImport(Lib, EntryPoint = "ana_midi_read")] public static unsafe partial int Read(nint h, uint* out_, int cap);
    [LibraryImport(Lib, EntryPoint = "ana_last_error")] private static partial nint LastError(nint h);
    public static string Error(nint h) => Marshal.PtrToStringUTF8(LastError(h)) ?? "";
}

public sealed class PlaygroundState
{
    public sealed class Pad
    {
        public string Path { get; set; } = "";
        public float GainDb { get; set; }
        public float Pan { get; set; }
        public string Name => Path.Length == 0 ? "" : System.IO.Path.GetFileNameWithoutExtension(Path);
    }
    public Pad[] Pads { get; set; } = Enumerable.Range(0, 16).Select(_ => new Pad()).ToArray();
    public ushort[] Steps { get; set; } = new ushort[16];
    public string SamplerPath { get; set; } = "";
    public int SamplerRoot { get; set; } = 60;
    public float SamplerGainDb { get; set; }
    public float MasterDb { get; set; } = -6;
    public bool SequencerOn { get; set; }
    public string MidiPort { get; set; } = "";
    /// MIDI learn: a target ("seq", "rec", "metro", "master", "sampler") -> "cc:<ch>:<n>" or "note:<ch>:<n>".
    public Dictionary<string, string> Learned { get; set; } = [];
}

/// The playground as the LIVE modules use it.
public sealed class Playground
{
    public static Playground Current { get; } = new();

    public PlaygroundState S = Load();
    nint _h;
    public bool Attached => _h != 0;
    public string Status = "";
    public int Step = -1, Voices;
    public float PeakDb = -120;
    public uint MidiMessages;
    public readonly uint[] Hits = new uint[17];
    public readonly float[] Seconds = new float[17];
    public readonly double[] Flash = new double[17];   // when each pad last sounded (Environment ticks, ms)
    public string[] Ports = [];
    public string? Learning;                            // a target waiting for a control
    public string LastMidi = "";
    /// Transport keys from the controller: "rec", "metro" (the window runs them).
    public event Action<string>? Command;

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dissonancia", "playground.json");
    static PlaygroundState Load()
    {
        try { if (File.Exists(FilePath) && JsonSerializer.Deserialize<PlaygroundState>(File.ReadAllText(FilePath)) is { } s && s.Pads.Length == 16 && s.Steps.Length == 16) return s; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return new PlaygroundState();
    }
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(S, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// A LIVE session started on this core handle: its WAVs, pattern and MIDI port again.
    public void Attach(nint h, bool output)
    {
        _h = h;
        Status = output ? "" : "a saída do LIVE está desligada (START: saída de clique): os pads não soam";
        try
        {
            for (int k = 0; k < 16; k++) if (S.Pads[k].Path.Length > 0) LoadSlot(k, S.Pads[k].Path, save: false);
            if (S.SamplerPath.Length > 0) LoadSlot(16, S.SamplerPath, save: false);
            Apply();
            ScanPorts();
            int port = Array.IndexOf(Ports, S.MidiPort);
            if (port >= 0) PgApi.Open(_h, port);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { _h = 0; Status = "núcleo sem playground"; }
    }

    public void Detach() => _h = 0;

    public unsafe void ScanPorts()
    {
        if (_h == 0) { Ports = []; return; }
        int n = PgApi.PortCount(_h);
        var names = new string[Math.Max(0, n)];
        byte* buf = stackalloc byte[256];
        for (int i = 0; i < names.Length; i++) names[i] = PgApi.PortName(_h, i, buf, 256) == 0 ? Marshal.PtrToStringUTF8((nint)buf) ?? "" : $"porta {i}";
        Ports = names;
    }

    /// The next MIDI port (none after the last).
    public void NextPort()
    {
        if (_h == 0) { Status = "dê START no LIVE para abrir o MIDI"; return; }
        ScanPorts();
        int i = Array.IndexOf(Ports, S.MidiPort) + 1;
        S.MidiPort = i < Ports.Length ? Ports[i] : "";
        int r = PgApi.Open(_h, S.MidiPort.Length > 0 ? i : -1);
        Status = Ports.Length == 0 ? "nenhum controlador MIDI encontrado" : r != 0 ? "MIDI não abriu: " + PgApi.Error(_h) : S.MidiPort.Length > 0 ? "MIDI: " + S.MidiPort : "MIDI fechado";
        Save();
    }

    /// slot 16 = the sampler. "" empties it.
    public void LoadSlot(int slot, string path, bool save = true)
    {
        if (slot < 16) S.Pads[slot].Path = path; else S.SamplerPath = path;
        if (save) Save();
        if (_h == 0) { Status = "carregado; toca depois do START"; return; }
        if (PgApi.Load(_h, slot, path) != 0) Status = PgApi.Error(_h);
    }

    public void Hit(int pad, float velocity = 1)
    {
        if (_h != 0) PgApi.Hit(_h, pad, velocity);
        else Status = "dê START no LIVE: os pads tocam pela saída do LIVE";
    }
    public void Note(int note, bool on) { if (_h != 0) PgApi.Send(_h, (byte)(on ? 0x90 : 0x80), (byte)note, (byte)(on ? 110 : 0)); }

    public void ToggleStep(int pad, int step) { S.Steps[pad] ^= (ushort)(1 << step); Apply(); Save(); }
    public void SetSequencer(bool on) { S.SequencerOn = on; Apply(); Save(); }

    public unsafe void Apply()
    {
        if (_h == 0) return;
        var c = new PlaygroundConfigNative { SamplerGainDb = S.SamplerGainDb, MasterDb = S.MasterDb, SequencerOn = (byte)(S.SequencerOn ? 1 : 0), SamplerRoot = (byte)Math.Clamp(S.SamplerRoot, 0, 127) };
        for (int k = 0; k < 16; k++) { c.Steps[k] = S.Steps[k]; c.PadGainDb[k] = S.Pads[k].GainDb; c.PadPan[k] = S.Pads[k].Pan; }
        PgApi.Set(_h, c);
    }

    /// Every frame: the core's status, the MIDI messages (learn, mapped controls).
    public unsafe void Poll()
    {
        if (_h == 0) return;
        PgApi.Status(_h, out var st);
        long now = Environment.TickCount64;
        for (int k = 0; k < 17; k++)
        {
            if (st.Hits[k] != Hits[k]) Flash[k] = now;
            Hits[k] = st.Hits[k];
            Seconds[k] = st.Seconds[k];
        }
        (Step, Voices, MidiMessages) = (st.Step, st.Voices, st.MidiMessages);
        PeakDb = Math.Max(st.PeakDb, PeakDb - 1.5f);
        uint* msg = stackalloc uint[64];
        int n = PgApi.Read(_h, msg, 64);
        for (int i = 0; i < n; i++) Midi((byte)msg[i], (byte)(msg[i] >> 8), (byte)(msg[i] >> 16));
    }

    void Midi(byte status, byte d1, byte d2)
    {
        int kind = status & 0xF0, ch = (status & 0x0F) + 1;
        string? key = kind == 0xB0 ? $"cc:{ch}:{d1}" : kind == 0x90 && d2 > 0 ? $"note:{ch}:{d1}" : null;
        LastMidi = kind switch { 0xB0 => $"CC {d1} = {d2} · canal {ch}", 0x90 when d2 > 0 => $"nota {d1} vel {d2} · canal {ch}", 0x80 or 0x90 => $"nota {d1} solta", _ => $"0x{status:X2} {d1} {d2}" };
        if (key is null) return;
        if (Learning is { } target)
        {
            foreach (var t in S.Learned.Where(kv => kv.Value == key).Select(kv => kv.Key).ToList()) S.Learned.Remove(t);   // one control, one target
            S.Learned[target] = key;
            Learning = null;
            Status = $"{Targets.First(t => t.Id == target).Name} ← {key}";
            Save();
            return;
        }
        foreach (var (tg, k) in S.Learned)
        {
            if (k != key) continue;
            switch (tg)
            {
                case "master": S.MasterDb = d2 / 127f * 60 - 54; Apply(); break;   // -54 .. +6 dB
                case "sampler": S.SamplerGainDb = d2 / 127f * 60 - 54; Apply(); break;
                case "seq" when kind == 0x90 || d2 >= 64: SetSequencer(!S.SequencerOn); break;
                case "rec" when kind == 0x90 || d2 >= 64: Command?.Invoke("rec"); break;
                case "metro" when kind == 0x90 || d2 >= 64: Command?.Invoke("metro"); break;
            }
        }
    }

    public static readonly (string Id, string Name)[] Targets = [("seq", "SEQ ▶"), ("rec", "REC"), ("metro", "METRÔNOMO"), ("master", "VOLUME"), ("sampler", "SAMPLER")];
}
