namespace Dissonancia;

// The playground's sounds from an open bank we do not host: the Versilian Community Sample Library
// (VCSL, CC0 — public domain), single WAVs fetched from its GitHub repository at a pinned commit, each
// checked by size and SHA-256 (Setup installs them with the other components). A drum and
// percussion kit for the 16 pads, four instruments for the sampler (one note each, its root given).
public static class SampleBank
{
    const string Commit = "c1ea7bcc3c7309650ab0da9d15c9cd1fbc4a4c7e";
    public sealed record Sound(string Name, string Path, long Size, string Sha256, int Root = -1)
    {
        public string Url => $"https://raw.githubusercontent.com/sgossner/VCSL/{Commit}/" + string.Join("/", Path.Split('/').Select(Uri.EscapeDataString));
        public string File => System.IO.Path.Combine(Dir, Name + ".wav");
    }

    /// Pads 1..16 in this order (pad n = MIDI note 35 + n).
    public static readonly Sound[] Kit =
    [
        new("kick", "Membranophones/Struck Membranophones/Bass Drum 1/BDrumNew_hit_v5_rr1_Sum.wav", 588092, "642aaf3375eaff1307973f7de3ccbf74d18e71fb3cf9412e021b08fc3bf1981c"),
        new("snare", "Membranophones/Struck Membranophones/Snare Drum, Modern 1/Snare2_HitSN_v5_rr1_Mid.wav", 240496, "6ec8445cb207df8f1178d8c3894f47969fd53fb2b81c404317e466de4598dc3e"),
        new("hat", "Idiophones/Struck Idiophones/Hi-Hat Cymbal/HiHat_HitC_v3_rr1_Mid.wav", 218792, "b65ca06ea4f8b5209fc8d98912094be1212911f21e01bd949cc9960db049d37c"),
        new("hat-open", "Idiophones/Struck Idiophones/Hi-Hat Cymbal/HiHat_HitO_rr1_Mid.wav", 1026612, "fa492ec14cc6924b43ad5fcf9e9a0f406d926998835bde2eb5847cd15ad25be7"),
        new("clap", "Idiophones/Struck Idiophones/Claps/Clap_rr1.wav", 141974, "4c9f3fe4a34733b9be6ed9ce5e5aad48b19c1029ae9c6dd1b96294a847539e3f"),
        new("tom-hi", "Membranophones/Struck Membranophones/Tom 1/Mallet/TomH_HitM_v3_rr1_Mid.wav", 497412, "f39c17ddb76504dcab90d0d14bbf2b722eda87eb533401a662a3ef454b76bea0"),
        new("tom-lo", "Membranophones/Struck Membranophones/Tom 2/Mallet/TomL_HitM_v3_rr1_Mid.wav", 421476, "d882ba9eb8b094519f7d4defd832c375b49fa12f2c094979a7ed3b559ba25b21"),
        new("crash", "Idiophones/Struck Idiophones/Suspended Cymbal 1/susCymb1_hit_f1.wav", 2046894, "42a8ece91eccec9b7063793e6644d037a9005c01d5b76f6bec358e8d53fb92a5"),
        new("snare-dry", "Membranophones/Struck Membranophones/Snare Drum, Modern 2/Snare3M_HitNS_v4_rr1_Mid.wav", 180284, "f32c6d7257b6bec1540a529e9cb4f2f13662d0b0619e6e2b611e9212611edf6d"),
        new("cowbell", "Idiophones/Struck Idiophones/Cowbells/Cowbell1_Hit_v3_rr1_Mid.wav", 204528, "36e9243e18b299bde0ebbb108c65846ddb5d46b35a61655fa18a0327e3390e87"),
        new("tambourine", "Idiophones/Struck Idiophones/Tambourine 1/Tamb1_Hit_v2_rr1_Mid.wav", 293256, "885ebf1d70f8e888f576682e4e4112b92b69649162f392c83b3379c91a09cf41"),
        new("shaker", "Idiophones/Struck Idiophones/Shaker, Small/Mid_ShakerDouble_Down_rr1.wav", 46844, "0df7c271e1dba4de7304f7df81886390dd9ae698bfdd6df570026af4f2223574"),
        new("conga", "Membranophones/Struck Membranophones/Conga/Conga_HitN_v2_rr1_Sum.wav", 140012, "13d322c0e7a830cdbcbdc5a6e05566dfe3f5a073b7b82a3262d3f6fc10f96764"),
        new("bongo", "Membranophones/Struck Membranophones/Bongos/BongoH_Hit1_v2_rr1_Mid.wav", 209356, "dd9b6239a151702966a995937f74ac022c9bb950c967277fbd16beaced0c9e1a"),
        new("cajon", "Idiophones/Struck Idiophones/Cajon/Cajon_hit1_f_rr1.wav", 222546, "18241c7c9f73dc9edfe6402371fefba5584ed5f9bc6fe827d3bba945841301d6"),
        new("claves", "Idiophones/Struck Idiophones/Claves/Legacy/claves_mf.wav", 140164, "7906ca75132d3374a00d12c23b030807107e4576716b6a437a9a6a8c7824b291"),
    ];

    /// For the sampler: a note each, at its root.
    public static readonly Sound[] Instruments =
    [
        new("piano", "Chordophones/Zithers/Upright Piano, Yamaha/Sustains/Upright1_Sus_C4_vl2_rr1.wav", 2964236, "39fdc55747ce7c0bd09215a357b577274c4c02928bf48972441ba6f47ec6e0a4", 60),
        new("fm-piano", "Electrophones/TX81Z/FM Piano/FMPiano_C4_vl2.wav", 941536, "7578fbb7b8203d1cfdcff1f3cbb74f158e183d810faa52dfb3656114e33fa595", 60),
        new("vibraphone", "Idiophones/Struck Idiophones/Vibraphone/Hard Mallets/Vibes_hard_C3_v2_rr1_Main.wav", 1903984, "26082761ea9b12fbe06d38bd4ea2826143958c29786948e50649e0478041b0fc", 48),
        new("kalimba", "Idiophones/Plucked Idiophones/Kalimba, Kenya/Mbira6_Normal_MainSpirit_A4_k1_vl3_rr2.wav", 949124, "153c55100dc25cdbec013b1153018e78fa094e5c0ee0b9f050ee57c2b198a500", 69),
    ];

    public static IEnumerable<Sound> All => Kit.Concat(Instruments);
    public static string Dir => System.IO.Path.Combine(Accel.Root, "sounds", "vcsl");
    static bool Present(Sound s) => System.IO.File.Exists(s.File) && new FileInfo(s.File).Length == s.Size;
    public static bool Installed => All.All(Present);
    public static long Missing => All.Where(s => !Present(s)).Sum(s => s.Size);

    /// Fetches what is missing (each checked); null or why not.
    public static async Task<string?> InstallAsync(Action<string, float> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);
        var todo = All.Where(s => !Present(s)).ToList();
        long total = todo.Sum(s => s.Size), done = 0;
        foreach (var s in todo)
        {
            if (ct.IsCancellationRequested) return "cancelled";
            string tmp = s.File + ".part";
            long at = done;
            var err = await Download.FileAsync(s.Url, tmp, s.Size, s.Sha256, n => progress($"sons VCSL: {s.Name}", (float)(at + n) / total), ct);
            if (err is not null) return $"{s.Name}: {err}";
            File.Move(tmp, s.File, true);
            done += s.Size;
        }
        return null;
    }
}
