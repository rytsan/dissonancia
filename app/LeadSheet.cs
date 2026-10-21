using System.Text.Json.Nodes;

namespace Dissonancia;

// STUDIO S5: one take JSON for SCORE from the separated channels' transcriptions — the voice's
// notes (melody), the harmony's chords, and the bass line ("part": "bass"), which decides each
// chord's inversion: the bass note sounding as the chord arrives (if it holds a quarter of the
// chord), else the one held longest (if it holds half) is its bass; the root -> root position,
// another chord tone -> slash chord. A bassist's fifth on beat 3 is not an inversion.
public static class LeadSheet
{
    // ChordQuality order of the core: Major, Minor, Diminished, Augmented, Sus2, Sus4, Power, Dom7,
    // Maj7, Min7, HalfDim7, Dim7, Maj6, Min6, Add9.
    public static readonly int[][] Intervals =
    [
        [0, 4, 7], [0, 3, 7], [0, 3, 6], [0, 4, 8], [0, 2, 7], [0, 5, 7], [0, 7], [0, 4, 7, 10],
        [0, 4, 7, 11], [0, 3, 7, 10], [0, 3, 6, 10], [0, 3, 6, 9], [0, 4, 7, 9], [0, 3, 7, 9], [0, 4, 7, 2],
    ];

    public static JsonObject Merge(JsonObject harmony, JsonArray voiceEvents, JsonArray? bassEvents)
    {
        var root = (JsonObject)harmony.DeepClone();
        var events = root["events"]!.AsArray();
        if (bassEvents is not null)
        {
            var bass = bassEvents.Where(e => (string?)e!["type"] == "note").Select(e => e!).ToList();
            foreach (var chord in events.Where(e => (string?)e!["type"] == "chord").Select(e => e!.AsObject()))
                ApplyBass(chord, bass);
            foreach (var b in bass) { var n = b.DeepClone().AsObject(); n["part"] = "bass"; events.Add(n); }
        }
        foreach (var e in voiceEvents) events.Add(e!.DeepClone());
        root["analysis"] = "studio-offline-stems";
        root["session"]!["mode"] = (int)AppMode.VoiceMono;   // a lead sheet: the voice's staff, the harmony as symbols
        return root;
    }

    public const double DriftFlagMs = 40;

    /// The drums' beats (event time) and tempo; with a metronome (gridBpm > 0, the take starts on
    /// its downbeat) each beat's drift from the nearest click, so a drummer off the click shows.
    public static void AddBeats(JsonObject root, JsonObject beatsResult, double gridBpm)
    {
        var beats = beatsResult["beats"]!.AsArray().Select(b => (double)b!).ToList();
        root["tempoBpm"] = (double)beatsResult["tempoBpm"]!;
        root["beats"] = new JsonArray(beats.Select(b => (JsonNode)Math.Round(b, 4)).ToArray());
        if (gridBpm <= 0) return;
        double period = 60 / gridBpm;
        root["beatDriftMs"] = new JsonArray(beats.Select(b => (JsonNode)Math.Round((b - Math.Round(b / period) * period) * 1000, 1)).ToArray());
    }

    /// The chord's bass from the bass line (see the file comment); unchanged when the bass rests.
    public static void ApplyBass(JsonObject chord, IReadOnlyList<JsonNode> bass)
    {
        double a = (double)chord["start"]!, b = (double)chord["end"]!;
        if (b <= a) return;
        var held = new double[12];
        var names = new string[12];
        int arriving = -1;
        double dur = b - a;
        foreach (var n in bass)
        {
            double s = (double)n["start"]!, e = (double)n["end"]!, o = Math.Min(b, e) - Math.Max(a, s);
            if (o <= 0) continue;
            int pc = (((int)n["midi"]! % 12) + 12) % 12;
            held[pc] += o;
            names[pc] ??= new string(((string)n["name"]!).TakeWhile(ch => !char.IsDigit(ch) && ch != '-').ToArray());
            if (arriving < 0 && s <= a + 0.25 * dur && o >= 0.25 * dur) arriving = pc;
        }
        int best = arriving >= 0 ? arriving : Array.IndexOf(held, held.Max());
        if (arriving < 0 && held[best] < 0.5 * dur) return;
        int rootPc = (int)chord["root"]!, quality = (int)chord["quality"]!;
        var tones = quality >= 0 && quality < Intervals.Length ? Intervals[quality].Select(i => (rootPc + i) % 12).ToArray() : [rootPc];
        string symbol = (string)chord["symbol"]!;
        string plain = symbol.Contains('/') ? symbol[..symbol.IndexOf('/')] : symbol;
        int inv;
        if (best == rootPc) { chord["symbol"] = plain; inv = 0; }
        else if (Array.IndexOf(tones, best) is int i and > 0) { chord["symbol"] = plain + "/" + names[best]; inv = i; }
        else return;   // a bass outside the chord (a passing tone that held) is left to the review
        chord["bass"] = best;
        chord["inversion"] = inv;
        if (chord["roman"] is JsonValue rv && rv.TryGetValue<string>(out var roman) && roman.Length > 0) chord["roman"] = Refigure(roman, quality, inv);
    }

    /// The roman numeral with the figure of the new inversion (the core's: triads "", 6, 64;
    /// sevenths 7, 65, 43, 42), e.g. ii64 -> ii, V7 -> V65, V7/V -> V43/V.
    public static string Refigure(string roman, int quality, int inversion)
    {
        bool triad = quality is 0 or 1 or 2 or 3, seventh = quality is 7 or 8 or 9 or 10 or 11;
        if (!triad && !seventh) return roman;
        string[] figs = triad ? ["", "6", "64"] : ["7", "65", "43", "42"];
        int slash = roman.IndexOf('/');
        string head = slash < 0 ? roman : roman[..slash], tail = slash < 0 ? "" : roman[slash..];
        foreach (var f in figs.Where(f => f.Length > 0).OrderByDescending(f => f.Length))
            if (head.EndsWith(f)) { head = head[..^f.Length]; break; }
        return head + figs[Math.Min(inversion, figs.Length - 1)] + tail;
    }

    /// The harmony underneath the chords: each chord as its triad (root, major or minor; Cadd9 -> C,
    /// D/F# -> D, Em7 -> Em), the same triads in a row joined, and a chord shorter than a second
    /// folded into the one before it (a passing chord is not the harmony).
    // ponytail: a fold of the detailed chords, not the LIVE harmony HMM; run ChordHmm::harmony() offline if it falls short.
    /// Degrees in `key` (the session's, else the suggested one); none without a key.
    public static List<(double Start, double End, string Symbol, string Roman)> Harmony(IReadOnlyList<TakeChord> chords, KeyOption? key)
    {
        string[] major = ["I", "♭II", "II", "♭III", "III", "IV", "♯IV", "V", "♭VI", "VI", "♭VII", "VII"];
        string[] minorKey = ["I", "♭II", "II", "III", "♯III", "IV", "♯IV", "V", "VI", "♯VI", "VII", "♯VII"];
        int tonic = key is null ? -1 : ((key.Fifths * 7 % 12 + 12) % 12 + (key.Minor ? 9 : 0)) % 12;
        string Degree(int root, bool minor)
        {
            if (tonic < 0) return "";
            string d = (key!.Minor ? minorKey : major)[(root - tonic + 12) % 12];
            return minor ? d.ToLowerInvariant() : d;
        }
        var o = new List<(double Start, double End, string Symbol, string Roman)>();
        foreach (var c in chords)
        {
            string s = c.Symbol.Split('/')[0];
            int n = s.Length > 1 && (s[1] is '#' or 'b' or '♯' or '♭') ? 2 : 1;
            string q = s[n..];
            bool minor = q.StartsWith('m') && !q.StartsWith("maj") || q.StartsWith("dim");
            string triad = s[..n] + (minor ? "m" : "");
            int root = ("C D EF G A B".IndexOf(s[0]) + (n == 2 ? s[1] is '#' or '♯' ? 1 : -1 : 0) + 12) % 12;
            string roman = Degree(root, minor);
            if (o.Count > 0 && (o[^1].Symbol == triad || c.End - c.Start < 1.0)) o[^1] = (o[^1].Start, c.End, o[^1].Symbol, o[^1].Roman);
            else o.Add((c.Start, c.End, triad, roman));
        }
        for (int i = o.Count - 1; i > 0; i--)   // a fold can leave two equal neighbours
            if (o[i].Symbol == o[i - 1].Symbol) { o[i - 1] = (o[i - 1].Start, o[i].End, o[i - 1].Symbol, o[i - 1].Roman); o.RemoveAt(i); }
        return o;
    }
}
