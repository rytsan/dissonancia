namespace Dissonancia;

// The playground's song as a Standard MIDI File and back. A block is MIDI already: a pad step is a
// note on channel 10 (pad 1 = note 36 .. pad 16 = 51, as a pad controller sends), a sampler note a
// note on channel 1 with its length. Export: type 1, 96 ticks a quarter (a sixteenth = 24); track 1
// the tempo, the meter and a marker at each block's start (its name), track 2 the pads, track 3 the
// sampler. Import: notes quantized to the sixteenth; markers cut it into blocks (a file this app
// wrote comes back block for block), else blocks of up to 16 bars.
public static class SongMidi
{
    const int Ppq = 96, Sixteenth = Ppq / 4;

    /// The timeline (every entry, its repeats) or one block (`only`), `spb` sixteenths a bar.
    public static byte[] Export(PlaygroundState s, int spb, float bpm, int beatsPerBar, int beatUnit, int? only = null)
    {
        var meta = new List<(int Tick, byte[] Data)>();
        var pads = new List<(int Tick, byte[] Data)>();
        var smp = new List<(int Tick, byte[] Data)>();
        int usPerQuarter = (int)Math.Round(60_000_000 / Math.Max(20, bpm));
        meta.Add((0, [0xFF, 0x51, 3, (byte)(usPerQuarter >> 16), (byte)(usPerQuarter >> 8), (byte)usPerQuarter]));
        meta.Add((0, [0xFF, 0x58, 4, (byte)beatsPerBar, (byte)Math.Log2(beatUnit), 24, 8]));
        var blocks = only is int b ? [(b, 1)] : s.Order.Select(e => (e.Block, e.Repeats)).ToList();
        int at = 0;   // sixteenths
        foreach (var (bi, reps) in blocks)
        {
            var blk = s.Blocks[bi];
            for (int r = 0; r < reps; r++)
            {
                meta.Add((at * Sixteenth, Text(0x06, blk.Name)));
                for (int pad = 0; pad < 16; pad++)
                    for (int bar = 0; bar < blk.Bars; bar++)
                        for (int k = 0; k < spb; k++)
                            if (blk.At(pad, bar * 16 + k) is > 0 and var v)
                            {
                                int t = (at + bar * spb + k) * Sixteenth;
                                pads.Add((t, [0x99, (byte)(36 + pad), v]));
                                pads.Add((t + Sixteenth, [0x89, (byte)(36 + pad), 0]));
                            }
                foreach (var n in blk.Notes)
                {
                    int t = (at + n.Step / 16 * spb + n.Step % 16) * Sixteenth;
                    smp.Add((t, [0x90, (byte)n.Note, (byte)Math.Clamp(n.Velocity, 1, 127)]));
                    smp.Add((t + n.Length * Sixteenth, [0x80, (byte)n.Note, 0]));
                }
                at += blk.Bars * spb;
            }
        }
        pads.Insert(0, (0, Text(0x03, "Pads")));
        smp.Insert(0, (0, Text(0x03, "Sampler")));
        using var ms = new MemoryStream();
        ms.Write("MThd"u8);
        ms.Write([0, 0, 0, 6, 0, 1, 0, 3, Ppq >> 8, Ppq & 0xFF]);
        foreach (var tr in new[] { meta, pads, smp }) Track(ms, tr);
        return ms.ToArray();
    }

    static byte[] Text(byte type, string text)
    {
        var b = System.Text.Encoding.UTF8.GetBytes(text);
        return [0xFF, type, (byte)b.Length, .. b];
    }

    static void Track(Stream s, List<(int Tick, byte[] Data)> events)
    {
        using var body = new MemoryStream();
        int last = 0;
        foreach (var (t, d) in events.OrderBy(e => e.Tick).ThenBy(e => (e.Data[0] & 0xF0) == 0x90 ? 1 : 0))   // offs before ons
        {
            Vlq(body, t - last);
            body.Write(d);
            last = t;
        }
        Vlq(body, 0);
        body.Write([0xFF, 0x2F, 0]);
        s.Write("MTrk"u8);
        int n = (int)body.Length;
        s.Write([(byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n]);
        body.WriteTo(s);
    }

    static void Vlq(Stream s, int v)
    {
        Span<byte> buf = stackalloc byte[5];
        int i = 4;
        buf[i] = (byte)(v & 0x7F);
        while ((v >>= 7) > 0) buf[--i] = (byte)(0x80 | (v & 0x7F));
        s.Write(buf[i..]);
    }

    public sealed record Imported(List<SongBlock> Blocks, float Bpm, int Notes);

    /// A MIDI file as blocks (ids and names left to the caller when the file has no markers).
    public static Imported Import(byte[] data, int spb)
    {
        int p = 0;
        uint U32() { uint v = (uint)(data[p] << 24 | data[p + 1] << 16 | data[p + 2] << 8 | data[p + 3]); p += 4; return v; }
        int U16() { int v = data[p] << 8 | data[p + 1]; p += 2; return v; }
        int Var() { int v = 0; byte b; do { b = data[p++]; v = v << 7 | (b & 0x7F); } while ((b & 0x80) != 0); return v; }
        if (data.Length < 14 || data[0] != 'M' || data[1] != 'T' || data[2] != 'h' || data[3] != 'd') throw new InvalidDataException("não é um arquivo MIDI");
        p = 4;
        int hlen = (int)U32(), hstart = p;
        U16();
        int tracks = U16(), division = U16();
        if ((division & 0x8000) != 0) throw new InvalidDataException("MIDI em tempo SMPTE: não suportado");
        p = hstart + hlen;
        var notes = new List<(int On, int Off, int Ch, int Note, int Vel)>();
        var markers = new List<(int Tick, string Name)>();
        float bpm = 0;
        for (int tr = 0; tr < tracks && p + 8 <= data.Length; tr++)
        {
            if (data[p] != 'M' || data[p + 1] != 'T' || data[p + 2] != 'r' || data[p + 3] != 'k') { p += 4; p += (int)U32(); continue; }
            p += 4;
            int len = (int)U32(), end = Math.Min(data.Length, p + len), tick = 0, running = 0;
            var open = new Dictionary<(int, int), (int Tick, int Vel)>();
            while (p < end)
            {
                tick += Var();
                int st = data[p];
                if (st >= 0x80) p++; else st = running;
                if (st == 0xFF)
                {
                    int type = data[p++], l = Var();
                    if (type == 0x51 && l == 3 && bpm == 0) bpm = 60_000_000f / (data[p] << 16 | data[p + 1] << 8 | data[p + 2]);
                    if (type == 0x06) markers.Add((tick, System.Text.Encoding.UTF8.GetString(data, p, l)));
                    p += l;
                    continue;
                }
                if (st is 0xF0 or 0xF7) { p += Var(); continue; }
                running = st;
                int kind = st & 0xF0, ch = st & 0x0F, d1 = data[p++], d2 = kind is 0xC0 or 0xD0 ? 0 : data[p++];
                if (kind == 0x90 && d2 > 0) open[(ch, d1)] = (tick, d2);
                else if ((kind == 0x80 || kind == 0x90) && open.Remove((ch, d1), out var on)) notes.Add((on.Tick, tick, ch, d1, on.Vel));
            }
            foreach (var ((ch, n), on) in open) notes.Add((on.Tick, on.Tick + division / 4, ch, n, on.Vel));   // never let go: a sixteenth
            p = end;
        }
        int S16(int tick) => (int)Math.Round(tick * 4.0 / division);
        int total = notes.Count == 0 ? spb : notes.Max(n => S16(n.Off));
        // Block boundaries (sixteenths): the markers, else every 16 bars.
        var cuts = markers.Select(m => (At: S16(m.Tick), m.Name)).Where(m => m.At < Math.Max(total, 1)).DistinctBy(m => m.At).OrderBy(m => m.At).ToList();
        if (cuts.Count == 0 || cuts[0].At > 0) cuts.Insert(0, (0, ""));
        if (markers.Count == 0) cuts = Enumerable.Range(0, Math.Max(1, (total + 16 * spb - 1) / (16 * spb))).Select(i => (i * 16 * spb, "")).ToList();
        var blocks = new List<SongBlock>();
        for (int c = 0; c < cuts.Count; c++)
        {
            int a = cuts[c].At, z = c + 1 < cuts.Count ? cuts[c + 1].At : Math.Max(total, a + 1);
            var b = new SongBlock { Name = cuts[c].Name };
            b.Resize(Math.Clamp((z - a + spb - 1) / spb, 1, 64));
            foreach (var n in notes)
            {
                int s16 = S16(n.On) - a;
                if (s16 < 0 || s16 >= z - a || s16 >= b.Bars * spb) continue;
                int step = s16 / spb * 16 + s16 % spb;
                if (n.Ch == 9 && n.Note is >= 36 and < 52) b.Set(n.Note - 36, step, (byte)Math.Max(b.At(n.Note - 36, step), n.Vel));
                else if (n.Ch != 9) b.Notes.Add(new SongNote(step, Math.Clamp(S16(n.Off) - S16(n.On), 1, 256), n.Note, n.Vel));
            }
            blocks.Add(b);
        }
        return new Imported(blocks, bpm, notes.Count);
    }
}
