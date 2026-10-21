using System.Runtime.InteropServices;
using System.Text.Json;

namespace Dissonancia;

// LIVE playground (creation): the C# side of the core's pads, sampler, MIDI input and song —
// what is loaded, the blocks (bars, pad pattern, sampler notes, an audio loop) and the timeline,
// kept in <config>/Dissonancia/playground.json (loops as WAVs beside it) and sent to the core at
// every START (the core decodes the WAVs at the input's rate). What REC catches comes back from the
// core and is written into the block. MIDI learn maps a controller's knobs and buttons to keys.

[StructLayout(LayoutKind.Sequential)]
unsafe struct PlaygroundConfigNative
{
    public fixed float PadGainDb[16];
    public fixed float PadPan[16];
    public float SamplerGainDb, LoopGainDb, MasterDb;
    public byte SamplerRoot;
    byte _pad0, _pad1, _pad2;
}

[StructLayout(LayoutKind.Sequential)] struct PgBlockNative { public uint Id, Bars, StepOffset, NoteOffset, NoteCount; }
[StructLayout(LayoutKind.Sequential)] struct PgNoteNative { public ushort Step, Length; public byte Note, Velocity; ushort _pad0; }
[StructLayout(LayoutKind.Sequential)] struct PgEntryNative { public int Block, Repeats; }
[StructLayout(LayoutKind.Sequential)] struct PgRecordedNative { public uint BlockId; public ushort Step, Length; public byte Kind, Key, Velocity, _pad0; }

[StructLayout(LayoutKind.Sequential)]
unsafe struct PlaygroundStatusNative
{
    public fixed uint Hits[17];
    public fixed float Seconds[17];
    public float PeakDb;
    public int Voices;
    public uint MidiMessages;
    public int Entry, Repeat, Bar, Step, Queued;
    public byte Playing, Recording, Looping, Starting;
    public float InputDb, OutputDb;
    public fixed float PedalDb[16];
}

[StructLayout(LayoutKind.Sequential)] unsafe struct PedalParamsNative { public ushort Id; public byte Kind, On; public fixed float Knob[4]; }
[StructLayout(LayoutKind.Sequential)] struct PedalCableNative { public byte From, To; ushort _pad0; public float Gain; }
[System.Runtime.CompilerServices.InlineArray(16)] struct PedalArray16 { PedalParamsNative _e; }
[System.Runtime.CompilerServices.InlineArray(48)] struct PedalCableArray48 { PedalCableNative _e; }
[StructLayout(LayoutKind.Sequential)] struct PedalboardNative { public PedalArray16 Pedal; public PedalCableArray48 Cable; public int Pedals, Cables; }

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
    [LibraryImport(Lib, EntryPoint = "ana_pg_song")] public static unsafe partial int Song(nint h, PgBlockNative* b, int nb, byte* steps, int ns, PgNoteNative* notes, int nn, PgEntryNative* order, int no);
    [LibraryImport(Lib, EntryPoint = "ana_pedalboard_set")] public static partial int Board(nint h, in PedalboardNative b);
    [LibraryImport(Lib, EntryPoint = "ana_pg_play")] public static partial void Play(nint h, int entry, byte loop);
    [LibraryImport(Lib, EntryPoint = "ana_pg_stop")] public static partial void Stop(nint h);
    [LibraryImport(Lib, EntryPoint = "ana_pg_record")] public static partial int Record(nint h, byte on);
    [LibraryImport(Lib, EntryPoint = "ana_pg_recorded")] public static unsafe partial int Recorded(nint h, PgRecordedNative* out_, int cap);
    [LibraryImport(Lib, EntryPoint = "ana_pg_loop_file", StringMarshalling = StringMarshalling.Utf8)] public static partial int LoopFile(nint h, uint id, string path, byte save);
    [LibraryImport(Lib, EntryPoint = "ana_pg_bounce", StringMarshalling = StringMarshalling.Utf8)] public static partial int Bounce(nint h, string path);
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
    /// The phase-1 sequencer's pattern (one bar); a file from then becomes the first block.
    public ushort[]? Steps { get; set; }
    public List<SongBlock> Blocks { get; set; } = [];
    public List<SongEntry> Order { get; set; } = [];
    public float LoopGainDb { get; set; }
    public string SamplerPath { get; set; } = "";
    public int SamplerRoot { get; set; } = 60;
    public float SamplerGainDb { get; set; }
    public float MasterDb { get; set; } = -6;
    public string MidiPort { get; set; } = "";
    /// MIDI learn: a target ("seq", "rec", "metro", "master", "sampler") -> "cc:<ch>:<n>" or "note:<ch>:<n>".
    public Dictionary<string, string> Learned { get; set; } = [];
    /// The pedalboard: its pedals (left to right as on the floor) and its cables. Jacks: "in:out"
    /// (ENTRADA), "pads:out", "out:in" (SAÍDA), "rec:in" (GRAVAR), "<pedal id>:in" / ":out".
    public List<PedalDef> Pedals { get; set; } = [];
    public List<CableLink> PedalCables { get; set; } = [];
}

public sealed class PedalDef
{
    public string Id { get; set; } = "";   // "p1", "p2" …
    public int Kind { get; set; }           // 0 drive, 1 octave, 2 chorus, 3 delay, 4 reverb, 5 filter
    public bool On { get; set; } = true;
    public float[] Knobs { get; set; } = [0.5f, 0.5f, 0.5f, 0.5f];

    public static readonly string[] Names = ["DRIVE", "OCTAVE", "CHORUS", "DELAY", "REVERB", "FILTRO"];
    public static readonly string[][] Labels =
    [
        ["GANHO", "TOM", "NÍVEL", "MIX"], ["OITAVA −", "OITAVA +", "SECO", ""], ["VELOC.", "PROF.", "MIX", "LARGURA"],
        ["TEMPO", "REPET.", "MIX", "TOM"], ["SALA", "ABAFAR", "MIX", "LARGURA"], ["CORTE", "RESSON.", "MODO", "LFO"],
    ];
    public static readonly float[][] Defaults =
    [
        [0.5f, 0.5f, 0.75f, 1], [0.8f, 0, 0.6f, 0], [0.3f, 0.4f, 0.5f, 0.6f], [3 / 15f, 0.35f, 0.4f, 0.5f], [0.6f, 0.4f, 0.35f, 0.8f], [0.45f, 0.4f, 0, 0],
    ];
    public static readonly uint[] Colours = [0xFFB0362Au, 0xFF2A6FB0u, 0xFF2AA08Au, 0xFF8A8A2Au, 0xFF6A3AA8u, 0xFFC27A1Eu];
    /// A knob's value as the pedal reads it.
    public string Value(int k)
    {
        float v = Knobs[k];
        return (Kind, k) switch
        {
            (0, 0) => $"{v * 40:0} dB", (0, 1) => Hz(800 * Math.Pow(15, v)), (0, 2) => $"{v * 30 - 24:+0;-0} dB",
            (2, 0) => $"{0.1 * Math.Pow(50, v):0.0} Hz", (2, 1) => $"{1 + 14 * v:0} ms",
            (3, 0) => $"{Math.Round(1 + v * 15):0}/16", (3, 3) => Hz(1500 * Math.Pow(8, v)),
            (5, 0) => Hz(80 * Math.Pow(100, v)), (5, 2) => v < 0.34f ? "LP" : v < 0.67f ? "BP" : "HP", (5, 3) => v < 0.02f ? "off" : $"{0.1 * Math.Pow(60, v):0.0} Hz",
            (1, 3) => "",
            _ => $"{v * 100:0}",
        };
    }
    static string Hz(double f) => f >= 1000 ? $"{f / 1000:0.#}k" : $"{f:0}";
}

/// A block of the song: its bars, the pads' pattern ([pad][bar * 16 + sixteenth] velocities), the
/// sampler's notes, whether it has an audio loop (a WAV beside playground.json).
public sealed class SongBlock
{
    public uint Id { get; set; }
    public string Name { get; set; } = "";
    public int Bars { get; set; } = 1;
    public byte[] Steps { get; set; } = new byte[16 * 16];
    public List<SongNote> Notes { get; set; } = [];
    public bool HasLoop { get; set; }
    /// The metronome's tempo when its loop was recorded (the loop is that long; it does not stretch yet).
    public float LoopBpm { get; set; }
    public byte At(int pad, int step) => Steps[pad * Bars * 16 + step];
    public void Set(int pad, int step, byte v) => Steps[pad * Bars * 16 + step] = v;

    /// To `bars`: the pattern kept bar for bar, notes past the end dropped.
    public void Resize(int bars)
    {
        bars = Math.Clamp(bars, 1, 64);
        var n = new byte[16 * bars * 16];
        for (int pad = 0; pad < 16; pad++)
            for (int k = 0; k < Math.Min(Bars, bars) * 16; k++) n[pad * bars * 16 + k] = At(pad, k);
        Steps = n;
        Bars = bars;
        Notes.RemoveAll(x => x.Step >= bars * 16);
    }
    public SongBlock Copy(uint id, string name) => new() { Id = id, Name = name, Bars = Bars, Steps = (byte[])Steps.Clone(), Notes = Notes.Select(x => x with { }).ToList() };
}
public sealed record SongNote(int Step, int Length, int Note, int Velocity);
public sealed class SongEntry { public int Block { get; set; } public int Repeats { get; set; } = 1; }

/// The playground as the LIVE modules use it.
public sealed class Playground
{
    public static Playground Current { get; } = new();

    public PlaygroundState S = Load();
    nint _h;
    public bool Attached => _h != 0;
    public string Status = "";
    public int Step = -1, Voices, Entry = -1, Bar, Repeat, Queued = -1;
    public bool Playing, Recording, Looping, Starting;
    public bool Armed;                                  // REC on (the core records while it plays)
    public float Bpm;                                   // the metronome's (the window sets it every frame)
    public int Selected;                                // the timeline entry being edited
    public int Page;                                    // the bar of its block the sequencer shows
    public float PeakDb = -120;
    public uint MidiMessages;
    public readonly uint[] Hits = new uint[17];
    public readonly float[] Seconds = new float[17];
    public readonly double[] Flash = new double[17];
    public float InputDb = -120, OutputDb = -120;
    public readonly float[] PedalDb = Enumerable.Repeat(-120f, 16).ToArray();   // when each pad last sounded (Environment ticks, ms)
    public string[] Ports = [];
    public string? Learning;                            // a target waiting for a control
    public string LastMidi = "";
    /// Transport keys from the controller: "rec", "metro" (the window runs them).
    public event Action<string>? Command;

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dissonancia", "playground.json");
    static PlaygroundState Load()
    {
        var s = new PlaygroundState();
        try { if (File.Exists(FilePath) && JsonSerializer.Deserialize<PlaygroundState>(File.ReadAllText(FilePath)) is { } f && f.Pads.Length == 16) s = f; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        if (s.Blocks.Count == 0)
        {   // a first block: the old one-bar pattern if there was one
            var b = new SongBlock { Id = 1, Name = "A" };
            if (s.Steps is { Length: 16 } old)
                for (int pad = 0; pad < 16; pad++) for (int k = 0; k < 16; k++) if ((old[pad] >> k & 1) != 0) b.Set(pad, k, 110);
            s.Blocks.Add(b);
        }
        s.Steps = null;
        s.Order.RemoveAll(e => e.Block < 0 || e.Block >= s.Blocks.Count);
        if (s.Order.Count == 0) s.Order.Add(new SongEntry { Block = 0 });
        return s;
    }
    static string LoopPath(uint id) => Path.Combine(Path.GetDirectoryName(FilePath)!, "playground", $"loop-{id}.wav");
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
            Publish();
            PublishBoard();
            foreach (var b in S.Blocks.Where(b => b.HasLoop)) if (File.Exists(LoopPath(b.Id))) PgApi.LoopFile(_h, b.Id, LoopPath(b.Id), 0);
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

    // ---------------------------------------------------------------- the song

    public SongEntry SelEntry => S.Order[Math.Clamp(Selected, 0, S.Order.Count - 1)];
    public SongBlock SelBlock => S.Blocks[SelEntry.Block];

    public void ToggleStep(int pad, int step) { var b = SelBlock; b.Set(pad, step, (byte)(b.At(pad, step) > 0 ? 0 : 110)); Changed(); }
    public void ClearBlock() { var b = SelBlock; Array.Clear(b.Steps); b.Notes.Clear(); Changed(); }
    public void Bars(int delta) { SelBlock.Resize(SelBlock.Bars + delta); Page = Math.Min(Page, SelBlock.Bars - 1); Changed(); }
    public void Repeats(int delta) { SelEntry.Repeats = Math.Clamp(SelEntry.Repeats + delta, 1, 64); Changed(); }
    uint NewId() => S.Blocks.Count == 0 ? 1 : S.Blocks.Max(b => b.Id) + 1;
    string NewName() => ((char)('A' + S.Blocks.Count % 26)).ToString() + (S.Blocks.Count >= 26 ? $"{S.Blocks.Count / 26}" : "");
    /// A new empty block of `bars`, after the selected entry.
    public void NewBlock(int bars = 4)
    {
        var b = new SongBlock { Id = NewId(), Name = NewName() };
        b.Resize(bars);
        S.Blocks.Add(b);
        S.Order.Insert(++Selected, new SongEntry { Block = S.Blocks.Count - 1 });
        Changed();
    }
    /// The selected block copied as a new one (edited apart), after it.
    public void Duplicate()
    {
        S.Blocks.Add(SelBlock.Copy(NewId(), NewName()));
        S.Order.Insert(Selected + 1, new SongEntry { Block = S.Blocks.Count - 1, Repeats = SelEntry.Repeats });
        Selected++;
        Changed();
    }
    /// The same block again (one block, played twice: an edit changes both), after it.
    public void RepeatBlock() { S.Order.Insert(Selected + 1, new SongEntry { Block = SelEntry.Block }); Selected++; Changed(); }
    public void Move(int delta)
    {
        int to = Math.Clamp(Selected + delta, 0, S.Order.Count - 1);
        if (to == Selected) return;
        (S.Order[Selected], S.Order[to]) = (S.Order[to], S.Order[Selected]);
        Selected = to;
        Changed();
    }
    public void RemoveEntry()
    {
        if (S.Order.Count <= 1) { Status = "a timeline precisa de um bloco"; return; }
        S.Order.RemoveAt(Selected);
        Selected = Math.Min(Selected, S.Order.Count - 1);
        // A block no entry plays any more goes (and its loop).
        for (int b = S.Blocks.Count - 1; b >= 0; b--)
        {
            if (S.Order.Any(e => e.Block == b)) continue;
            try { File.Delete(LoopPath(S.Blocks[b].Id)); } catch (IOException) { }
            S.Blocks.RemoveAt(b);
            foreach (var e in S.Order) if (e.Block > b) e.Block--;
        }
        Changed();
    }
    public void ClearLoop()
    {
        var b = SelBlock;
        b.HasLoop = false;
        try { File.Delete(LoopPath(b.Id)); } catch (IOException) { }
        if (_h != 0) PgApi.LoopFile(_h, b.Id, "", 0);
        Changed();
    }

    void Changed() { Publish(); Save(); }

    // ---------------------------------------------------------------- MIDI files

    /// A .mid's notes as blocks after the selected entry: pads (channel 10, notes 36..51) and the
    /// sampler (other channels); its markers make the blocks, the same block again in a row its repeats.
    public void ImportMidi(string path, int spb)
    {
        SongMidi.Imported im;
        try { im = SongMidi.Import(File.ReadAllBytes(path), spb); }
        catch (Exception e) when (e is IOException or InvalidDataException or IndexOutOfRangeException or UnauthorizedAccessException) { Status = "MIDI: " + e.Message; return; }
        static bool Same(SongBlock a, SongBlock b) => a.Name == b.Name && a.Bars == b.Bars && a.Steps.AsSpan().SequenceEqual(b.Steps) && a.Notes.SequenceEqual(b.Notes);
        var made = new List<int>();   // block index per imported block (one block for the same one again)
        foreach (var b in im.Blocks)
        {
            int same = b.Name.Length > 0 ? made.FirstOrDefault(i => Same(S.Blocks[i], b), -1) : -1;
            if (same >= 0) { made.Add(same); continue; }
            b.Id = NewId();
            if (b.Name.Length == 0) b.Name = NewName();
            S.Blocks.Add(b);
            made.Add(S.Blocks.Count - 1);
        }
        int at = Selected + 1;
        foreach (var bi in made)
        {
            if (at > 0 && at - 1 >= Selected + 1 && S.Order[at - 1].Block == bi) { S.Order[at - 1].Repeats++; continue; }
            S.Order.Insert(at++, new SongEntry { Block = bi });
        }
        Selected = Math.Min(Selected + 1, S.Order.Count - 1);
        Status = $"MIDI: {im.Notes} notas em {made.Distinct().Count()} blocos" + (im.Bpm > 0 ? $" · metrônomo a {Math.Round(im.Bpm):0} bpm, o do arquivo" : "");
        Changed();
        if (im.Bpm > 0) Command?.Invoke(FormattableString.Invariant($"tempo:{im.Bpm}"));   // the song at the file's tempo
    }

    /// The timeline, or the selected block alone, as a .mid.
    public void ExportMidi(string path, int spb, float bpm, int beatsPerBar, int beatUnit, bool blockOnly)
    {
        try
        {
            File.WriteAllBytes(path, SongMidi.Export(S, spb, bpm, beatsPerBar, beatUnit, blockOnly ? SelEntry.Block : null));
            Status = "MIDI: " + Path.GetFileName(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Status = "MIDI: " + e.Message; }
    }

    // ---------------------------------------------------------------- the pedalboard

    public PedalDef AddPedal(int kind)
    {
        int n = 1;
        while (S.Pedals.Any(p => p.Id == $"p{n}")) n++;
        var p = new PedalDef { Id = $"p{n}", Kind = kind, Knobs = (float[])PedalDef.Defaults[kind].Clone() };
        S.Pedals.Add(p);
        BoardChanged();
        return p;
    }
    public void RemovePedal(PedalDef p)
    {
        S.Pedals.Remove(p);
        S.PedalCables.RemoveAll(k => k.From.StartsWith(p.Id + ":") || k.To.StartsWith(p.Id + ":"));
        BoardChanged();
    }
    public void BoardChanged() { PublishBoard(); Save(); }

    /// The pedals and cables to the core (heard from the next callback).
    public unsafe void PublishBoard()
    {
        if (_h == 0) return;
        var b = new PedalboardNative();
        var pedals = S.Pedals.Take(16).ToList();
        for (int k = 0; k < pedals.Count; k++)
        {
            var n = new PedalParamsNative { Id = ushort.Parse(pedals[k].Id[1..]), Kind = (byte)pedals[k].Kind, On = (byte)(pedals[k].On ? 1 : 0) };
            for (int i = 0; i < 4; i++) n.Knob[i] = pedals[k].Knobs[i];
            b.Pedal[k] = n;
        }
        b.Pedals = pedals.Count;
        int? Jack(string j)
        {
            switch (j) { case "in:out": return 0; case "pads:out": return 1; case "out:in": return 2; case "rec:in": return 3; }
            int k = pedals.FindIndex(p => j.StartsWith(p.Id + ":"));
            return k < 0 ? null : j.EndsWith(":in") ? 8 + 2 * k : 9 + 2 * k;
        }
        foreach (var c in S.PedalCables)
            if (b.Cables < 48 && Jack(c.From) is int f && Jack(c.To) is int t)
                b.Cable[b.Cables++] = new PedalCableNative { From = (byte)f, To = (byte)t, Gain = c.Gain };
        if (PgApi.Board(_h, b) != 0) Status = PgApi.Error(_h);
    }

    /// The song to the core (heard from the next bar).
    public unsafe void Publish()
    {
        if (_h == 0) return;
        var blocks = new PgBlockNative[S.Blocks.Count];
        var steps = new List<byte>();
        var notes = new List<PgNoteNative>();
        for (int i = 0; i < S.Blocks.Count; i++)
        {
            var b = S.Blocks[i];
            blocks[i] = new PgBlockNative { Id = b.Id, Bars = (uint)b.Bars, StepOffset = (uint)steps.Count, NoteOffset = (uint)notes.Count, NoteCount = (uint)b.Notes.Count };
            steps.AddRange(b.Steps);
            notes.AddRange(b.Notes.Select(n => new PgNoteNative { Step = (ushort)n.Step, Length = (ushort)n.Length, Note = (byte)n.Note, Velocity = (byte)n.Velocity }));
        }
        var order = S.Order.Select(e => new PgEntryNative { Block = e.Block, Repeats = e.Repeats }).ToArray();
        var st = steps.ToArray();
        var nt = notes.ToArray();
        fixed (PgBlockNative* pb = blocks) fixed (byte* ps = st) fixed (PgNoteNative* pn = nt) fixed (PgEntryNative* po = order)
            if (PgApi.Song(_h, pb, blocks.Length, ps, st.Length, pn, nt.Length, po, order.Length) != 0) Status = PgApi.Error(_h);
    }

    /// From the next bar: the timeline from the selected entry (loop: that entry's block, again and again).
    public void Play(bool loop)
    {
        if (_h == 0) { Status = "dê START no LIVE para tocar"; return; }
        PgApi.Play(_h, Selected, (byte)(loop ? 1 : 0));
    }
    public void Stop()
    {
        if (_h == 0) return;
        if (Armed) Record();   // REC off with it: the loops kept
        PgApi.Stop(_h);
    }
    /// Overdub into the block playing: the input into its loop, pads and notes into its pattern.
    public void Record()
    {
        if (_h == 0) { Status = "dê START no LIVE para gravar"; return; }
        bool on = !Armed;
        if (PgApi.Record(_h, (byte)(on ? 1 : 0)) != 0) { Status = PgApi.Error(_h); return; }
        if (on && !Playing && !Starting) Play(loop: true);
        if (!on) SaveLoops();
        Armed = on;
    }
    void SaveLoops()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LoopPath(0))!);
        foreach (var b in S.Blocks)
        {
            bool had = b.HasLoop;
            b.HasLoop = PgApi.LoopFile(_h, b.Id, LoopPath(b.Id), 1) == 0;
            if (b.HasLoop && (!had || b.LoopBpm <= 0)) b.LoopBpm = Bpm;
        }
        Save();
    }

    /// The timeline once to a WAV for STUDIO (null: why not).
    public string? Bounce(out string path)
    {
        path = Path.Combine(Take.Folder, "playground", $"song-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
        if (_h == 0) return "dê START no LIVE (os pads tocam na taxa dele)";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return PgApi.Bounce(_h, path) == 0 ? null : PgApi.Error(_h);
    }

    public unsafe void Apply()
    {
        if (_h == 0) return;
        var c = new PlaygroundConfigNative { SamplerGainDb = S.SamplerGainDb, LoopGainDb = S.LoopGainDb, MasterDb = S.MasterDb, SamplerRoot = (byte)Math.Clamp(S.SamplerRoot, 0, 127) };
        for (int k = 0; k < 16; k++) { c.PadGainDb[k] = S.Pads[k].GainDb; c.PadPan[k] = S.Pads[k].Pan; }
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
        (Step, Voices, MidiMessages, Entry, Bar, Repeat, Queued) = (st.Step, st.Voices, st.MidiMessages, st.Entry, st.Bar, st.Repeat, st.Queued);
        (Playing, Recording, Looping, Starting) = (st.Playing != 0, st.Recording != 0, st.Looping != 0, st.Starting != 0);
        // What REC caught, into its block.
        var got = stackalloc PgRecordedNative[64];
        int nr = PgApi.Recorded(_h, got, 64);
        for (int i = 0; i < nr; i++)
        {
            var r = got[i];
            if (S.Blocks.FirstOrDefault(b => b.Id == r.BlockId) is not { } b || r.Step >= b.Bars * 16) continue;
            if (r.Kind == 0) b.Set(r.Key, r.Step, Math.Max(b.At(r.Key, r.Step), r.Velocity));
            else if (!b.Notes.Any(x => x.Step == r.Step && x.Note == r.Key)) b.Notes.Add(new SongNote(r.Step, r.Length, r.Key, r.Velocity));
        }
        if (nr > 0) Changed();
        PeakDb = Math.Max(st.PeakDb, PeakDb - 1.5f);
        InputDb = Math.Max(st.InputDb, InputDb - 1.5f);
        OutputDb = Math.Max(st.OutputDb, OutputDb - 1.5f);
        for (int k = 0; k < 16; k++) PedalDb[k] = Math.Max(st.PedalDb[k], PedalDb[k] - 1.5f);
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
                case "seq" when kind == 0x90 || d2 >= 64: if (Playing || Starting) Stop(); else Play(loop: true); break;
                case "rec" when kind == 0x90 || d2 >= 64: Command?.Invoke("rec"); break;
                case "metro" when kind == 0x90 || d2 >= 64: Command?.Invoke("metro"); break;
            }
        }
    }

    public static readonly (string Id, string Name)[] Targets = [("seq", "SEQ ▶"), ("rec", "REC"), ("metro", "METRÔNOMO"), ("master", "VOLUME"), ("sampler", "SAMPLER")];
}
