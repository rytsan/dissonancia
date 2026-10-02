using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Dissonancia;

// STUDIO S6: the review of a take — what the user decides after the transcription. Key and meter
// are the user's (STUDIO suggests a key, never applies it by itself); the tempo is the metronome's,
// or the drums' tempo map as an option; corrections are kept per event and re-applied whenever the
// analysis runs again (matched by type and start time, so an upstream re-run keeps them).
public sealed class Review
{
    public bool? KeySet { get; set; }          // null: the take's own (its REC session or START)
    public int KeyFifths { get; set; }
    public bool KeyMinor { get; set; }
    public bool TempoFromDrums { get; set; }   // the drums' beats become the bar grid
    public List<Fix> Fixes { get; set; } = [];

    public sealed record Fix(string Kind, double Start, int? Root = null, int? Quality = null, int? Bass = null, int? Midi = null, bool Delete = false);

    /// How close an event must start to a fix to be the one it corrects.
    public const double MatchSeconds = 0.12;

    [JsonIgnore] public bool IsEmpty => KeySet is null && !TempoFromDrums && Fixes.Count == 0;

    // ----------------------------------------------------------------- suggestions

    // Temperley's (2001) key profiles, tonic first.
    static readonly double[] MajorProfile = [5, 2, 3.5, 2, 4.5, 4, 2, 4.5, 2, 3.5, 1.5, 4];
    static readonly double[] MinorProfile = [5, 2, 3.5, 4.5, 2, 4, 2, 4.5, 3.5, 2, 1.5, 4];

    /// The key that best fits the whole take — voice and bass notes by duration, chord tones by the
    /// chord's duration — and how sure: the correlation of the best key and its margin over the
    /// next one that is not its relative.
    public static (KeyOption Key, double Correlation, double Margin)? SuggestKey(Take t)
    {
        var h = new double[12];
        foreach (var n in t.Notes.Concat(t.BassNotes)) h[((n.Midi % 12) + 12) % 12] += n.End - n.Start;
        foreach (var c in t.Chords)
            if (c.Quality >= 0 && c.Quality < LeadSheet.Intervals.Length)
                foreach (var i in LeadSheet.Intervals[c.Quality]) h[(c.Root + i) % 12] += 0.5 * (c.End - c.Start);
        if (h.Sum() < 2) return null;   // under ~2 s of material: no suggestion
        var scores = new List<(int Tonic, bool Minor, double R)>();
        for (int tonic = 0; tonic < 12; tonic++)
            foreach (bool minor in new[] { false, true })
                scores.Add((tonic, minor, Correlation(h, minor ? MinorProfile : MajorProfile, tonic)));
        scores.Sort((a, b) => b.R.CompareTo(a.R));
        var best = scores[0];
        int relative = best.Minor ? (best.Tonic + 3) % 12 : (best.Tonic + 9) % 12;
        var next = scores.Skip(1).First(s => !(s.Tonic == relative && s.Minor != best.Minor));
        // Fifths of the key: major tonic pc -> fifths; minor via its relative major.
        int majorPc = best.Minor ? (best.Tonic + 3) % 12 : best.Tonic;
        int fifths = (majorPc * 7) % 12;
        if (fifths > 6) fifths -= 12;   // prefer the flat side past F#
        return (new KeyOption(fifths, best.Minor), best.R, best.R - next.R);
    }

    static double Correlation(double[] h, double[] profile, int tonic)
    {
        double mh = h.Average(), mp = profile.Average(), num = 0, dh = 0, dp = 0;
        for (int k = 0; k < 12; k++)
        {
            double a = h[(tonic + k) % 12] - mh, b = profile[k] - mp;
            num += a * b; dh += a * a; dp += b * b;
        }
        return dh > 0 ? num / Math.Sqrt(dh * dp) : 0;
    }

    // ----------------------------------------------------------------- applying

    static readonly string[] Suffix = ["", "m", "dim", "aug", "sus2", "sus4", "5", "7", "maj7", "m7", "m7b5", "dim7", "6", "m6", "add9"];

    // ponytail: corrected events are spelled like the core does without melodic context — the
    // key's own notes by its side, others lowered except the raised 4th (Bb, Eb, Ab, Db; F# in C);
    // re-running the analysis respells everything with the core's speller.
    static readonly string[] Sharps = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
    static readonly string[] Flats = ["C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B"];

    static string PcName(int pc, int fifths)
    {
        pc = ((pc % 12) + 12) % 12;
        int tonic = ((fifths * 7) % 12 + 12) % 12, deg = (pc - tonic + 12) % 12;
        bool diatonic = deg is 0 or 2 or 4 or 5 or 7 or 9 or 11;
        if (diatonic) return (fifths < 0 ? Flats : Sharps)[pc];
        return (deg == 6 ? Sharps : Flats)[pc];
    }

    public static string ChordSymbol(int root, int quality, int bass, int fifths) =>
        PcName(root, fifths) + (quality >= 0 && quality < Suffix.Length ? Suffix[quality] : "") + (bass >= 0 && bass != root ? "/" + PcName(bass, fifths) : "");

    public static string NoteName(int midi, int fifths)
    {
        var pc = PcName(midi, fifths);
        // The octave belongs to the letter (B#3 = C4); these names never cross C.
        return pc + (midi / 12 - 1);
    }

    /// Applies key, tempo and corrections to a take JSON (in place).
    public void Apply(JsonObject root)
    {
        var session = root["session"]!.AsObject();
        if (KeySet is bool k)
        {
            session["keySet"] = k;
            if (k) { session["keyFifths"] = KeyFifths; session["keyMode"] = KeyMinor ? 1 : 0; }
        }
        if (TempoFromDrums && root["beats"] is JsonArray beats && beats.Count >= 4) WarpToBeats(root, beats.Select(b => (double)b!).ToList());
        int fifths = (bool)session["keySet"]! ? (int)session["keyFifths"]! : 0;
        var events = root["events"]!.AsArray();
        foreach (var f in Fixes)
        {
            var hit = events.Select(e => e!.AsObject())
                .Where(e => Kind(e) == f.Kind && Math.Abs((double)e["start"]! - f.Start) <= MatchSeconds)
                .OrderBy(e => Math.Abs((double)e["start"]! - f.Start)).FirstOrDefault();
            if (hit is null) continue;   // the event is gone (an edit removed it): the fix waits
            if (f.Delete) { events.Remove(hit); continue; }
            hit["corrected"] = true;
            hit["confidence"] = 1.0;
            if (f.Kind == "chord")
            {
                int r = f.Root ?? (int)hit["root"]!, q = f.Quality ?? (int)hit["quality"]!, b = f.Bass ?? (int)hit["bass"]!;
                if (f.Root is not null && f.Bass is null && b == (int)hit["root"]!) b = r;   // root position follows the root
                hit["root"] = r; hit["quality"] = q; hit["bass"] = b;
                hit["symbol"] = ChordSymbol(r, q, b, fifths);
                hit["roman"] = "";   // the core's numeral belonged to the old chord
            }
            else if (f.Midi is int m) { hit["midi"] = m; hit["name"] = NoteName(m, fifths); }
        }
    }

    public static string Kind(JsonObject e) => (string?)e["type"] == "chord" ? "chord" : (string?)e["part"] == "bass" ? "bass" : (string?)e["type"] == "note" ? "note" : "";

    /// The drums' beats as the bar grid: every time moves so that beat k lands on k beats of the
    /// global tempo from the first beat (piecewise linear between beats, constant tempo outside).
    public static void WarpToBeats(JsonObject root, IReadOnlyList<double> beats)
    {
        double bpm = (double)root["tempoBpm"]!, p = 60 / bpm;
        double Warp(double t)
        {
            int i = 0;
            while (i < beats.Count - 2 && t >= beats[i + 1]) i++;
            double seg = beats[i + 1] - beats[i];
            return (i + (t - beats[i]) / seg) * p;   // extrapolates with the edge interval before / after
        }
        foreach (var e in root["events"]!.AsArray().Select(e => e!.AsObject()))
            foreach (var key in new[] { "start", "end", "time" })
                if (e[key] is JsonValue v && v.TryGetValue<double>(out var t)) e[key] = Math.Round(Warp(t), 4);
        root["session"]!["bpm"] = Math.Round(bpm, 3);
        root["beats"] = new JsonArray(beats.Select((_, k) => (JsonNode)Math.Round(k * p, 4)).ToArray());
        root.Remove("beatDriftMs");
        root["tempoSource"] = "drums";
    }

    // ----------------------------------------------------------------- storage

    public static Review Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Review>(File.ReadAllText(path)) ?? new() : new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this));

    /// A correction of an event already corrected merges with the earlier one (root, then quality).
    public void Add(Fix f)
    {
        if (Fixes.FirstOrDefault(x => x.Kind == f.Kind && Math.Abs(x.Start - f.Start) <= MatchSeconds) is { } old)
        {
            Fixes.Remove(old);
            if (!f.Delete) f = f with { Root = f.Root ?? old.Root, Quality = f.Quality ?? old.Quality, Bass = f.Bass ?? old.Bass, Midi = f.Midi ?? old.Midi };
        }
        Fixes.Add(f);
    }
}
