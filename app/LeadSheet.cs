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
    static readonly int[][] Intervals =
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
        if (best == rootPc) { chord["symbol"] = plain; chord["bass"] = rootPc; chord["inversion"] = 0; }
        else if (Array.IndexOf(tones, best) is int i and > 0) { chord["symbol"] = plain + "/" + names[best]; chord["bass"] = best; chord["inversion"] = i; }
        // A bass outside the chord (a passing tone that held) is left to the review.
    }
}
