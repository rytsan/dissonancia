using System.Text.Json;

namespace Dissonancia;

/// The coach's metrics of the newest REC take (dissonancia-session/1, docs/SESSION.md), for DS-X:
/// tiles, one value per bar for the timeline, the form's sections. Reloaded when the file changes
/// (the metrics arrive a few seconds after REC stops).
public sealed class TakeReport
{
    public record Tile(string Name, string Value, string Sub, bool Ok, string Series);
    public string Path = "";
    public bool Pending;                       // the take is there, its metrics not yet
    public List<Tile> Tiles = [];
    public Dictionary<string, double?[]> Bars = [];   // per bar (index 0 = bar 1)
    public List<(int FirstBar, int Bars, char Label, double Match)> Sections = [];
    public List<string> Warnings = [];
    DateTime _stamp;

    /// The newest take's report, reloaded when needed; null when there is no take.
    public static TakeReport? Latest(TakeReport? old)
    {
        var path = Take.Latest();
        if (path is null) return null;
        var stamp = File.GetLastWriteTimeUtc(path);
        if (old is not null && old.Path == path && old._stamp == stamp) return old;
        try { return Load(path, stamp); }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { return old; }
    }

    static double? Num(JsonElement o, string k) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    static double?[] PerBar(JsonElement list, string key)
    {
        var rows = list.EnumerateArray().ToList();
        int n = rows.Count == 0 ? 0 : rows.Max(r => r.GetProperty("bar").GetInt32());
        var v = new double?[Math.Max(0, n)];
        foreach (var r in rows) if (r.GetProperty("bar").GetInt32() is int b and > 0) v[b - 1] = Num(r, key);
        return v;
    }

    static TakeReport Load(string path, DateTime stamp)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var r = new TakeReport { Path = path, _stamp = stamp };
        if (!root.TryGetProperty("metrics", out var m)) { r.Pending = true; return r; }
        static string Ms(double? v) => v is double d ? $"{d:+0;-0;0} ms" : "—";

        if (m.TryGetProperty("timing", out var t))
        {
            double? mean = Num(t, "meanOffsetMs"), spread = Num(t, "spreadMs");
            bool metro = t.GetProperty("grid").GetString() == "metronome";
            r.Tiles.Add(new("TIMING", Ms(mean), $"desvio {spread:0} ms · {(metro ? "metrônomo" : "seu pulso")}",
                            (!metro || Math.Abs(mean ?? 0) < 15) && (spread ?? 0) < 20, "timing"));
            r.Bars["timing"] = PerBar(t.GetProperty("bars"), "meanOffsetMs");
            double? swing = Num(t, "swing");
            r.Tiles.Add(new("SWING", swing is double s ? $"{s:0.00}" : "—", swing is double s2 ? (s2 < 1.2 ? "colcheias retas" : s2 > 1.8 ? "tercinado" : "suingado") : "poucas colcheias", true, "timing"));
        }
        if (m.TryGetProperty("tempo", out var tp))
        {
            double? drift = Num(tp, "driftBpm");
            r.Tiles.Add(new("TEMPO", Num(tp, "bpm") is double b ? $"{b:0} bpm" : "—", drift is double d ? $"deriva {d:+0.0;-0.0} bpm" : "sem pulso", Math.Abs(drift ?? 0) < 2, ""));
        }
        if (m.TryGetProperty("intonation", out var it) && Num(it, "inTuneOwnTuningPct") is double inTune)
        {
            r.Tiles.Add(new("AFINAÇÃO", $"{inTune:0} %", $"{Num(it, "tuningCents"):+0;-0} ¢ · scoops {Num(it, "scoops"):0}", inTune >= 70, "intonation"));
            r.Bars["intonation"] = PerBar(it.GetProperty("bars"), "meanAbsCents");
        }
        if (m.TryGetProperty("harmony", out var h))
        {
            int changes = h.GetProperty("changes").GetInt32();
            r.Tiles.Add(new("TROCAS", changes > 0 ? Ms(Num(h, "changeMeanMs")) : "—", $"{changes} trocas · {Num(h, "gaps"):0} buracos · limpo {Num(h, "cleanPct"):0} %",
                            Num(h, "gaps") is not > 2 && Math.Abs(Num(h, "changeMeanMs") ?? 0) < 25, "harmony"));
            var per = new SortedDictionary<int, List<double>>();
            foreach (var c in h.GetProperty("changeList").EnumerateArray())
                if (c.GetProperty("bar").GetInt32() is int b and > 0 && Num(c, "offsetMs") is double o)
                    (per.TryGetValue(b, out var l) ? l : per[b] = []).Add(o);
            var arr = new double?[per.Count == 0 ? 0 : per.Keys.Max()];
            foreach (var (b, l) in per) arr[b - 1] = l.Average();
            r.Bars["harmony"] = arr;
        }
        if (m.TryGetProperty("dynamics", out var dy))
        {
            double? spread = Num(dy, "attackSpreadDb");
            r.Tiles.Add(new("DINÂMICA", Num(dy, "rangeLu") is double lra ? $"{lra:0.0} LU" : "—",
                            spread is double sd ? $"batidas ±{sd:0.0} dB · {Num(dy, "trendLu"):+0.0;-0.0} LU" : $"{Num(dy, "integratedLufs"):0} LUFS", (spread ?? 0) < 3, "dynamics"));
            r.Bars["dynamics"] = PerBar(dy.GetProperty("bars"), "lufs");
        }
        if (m.TryGetProperty("form", out var fo))
        {
            string form = fo.GetProperty("form").GetString() ?? "";
            int repeats = fo.GetProperty("repeats").GetInt32();
            r.Tiles.Add(new("FORMA", form.Length > 0 ? (form.Length > 8 ? form[..8] + "…" : form) : "—",
                            repeats > 0 ? $"{repeats} repetições · {Num(fo, "repeatMatchPct"):0} %" : "sem repetição", true, "form"));
            foreach (var s in fo.GetProperty("sections").EnumerateArray())
                r.Sections.Add((s.GetProperty("firstBar").GetInt32(), s.GetProperty("bars").GetInt32(), s.GetProperty("label").GetString()![0], Num(s, "matchPct") ?? 0));
        }
        if (root.TryGetProperty("limits", out var li))
        {
            r.Warnings = li.GetProperty("warnings").EnumerateArray().Select(w => w.GetString() ?? "").ToList();
            r.Tiles.Add(new("LIMITES", r.Warnings.Count == 0 ? "ok" : $"{r.Warnings.Count}", r.Warnings.Count == 0 ? $"pico {Num(li, "peakDbfs"):0} dBFS" : r.Warnings[0], r.Warnings.Count == 0, ""));
        }
        return r;
    }
}
