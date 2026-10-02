namespace Dissonancia;

/// S2 edit list (non-destructive): source ranges in order with clip gain, fades over the edited take,
/// peak normalisation. Operations take a selection in EDITED frames. Kept per take, with undo.
public sealed class EditList
{
    public sealed record Seg(ulong A, ulong B, float GainDb);
    public List<Seg> Segments { get; set; } = [];
    public ulong FadeIn { get; set; }
    public ulong FadeOut { get; set; }
    public bool Normalize { get; set; }

    [System.Text.Json.Serialization.JsonIgnore] public bool IsEmpty => Segments.Count == 0 && FadeIn == 0 && FadeOut == 0 && !Normalize;
    public ulong Length(ulong original) => Segments.Count == 0 ? original : (ulong)Segments.Sum(s => (long)(s.B - s.A));
    public EditList Clone() => new() { Segments = [.. Segments], FadeIn = FadeIn, FadeOut = FadeOut, Normalize = Normalize };
    /// The take still starts where the original did, without cuts: the metronome grid holds.
    [System.Text.Json.Serialization.JsonIgnore] public bool KeepsGrid => Segments.Count <= 1 && (Segments.Count == 0 || Segments[0].A == 0);
    [System.Text.Json.Serialization.JsonIgnore] public string Key => System.Text.Json.JsonSerializer.Serialize(this);

    List<Seg> Base(ulong original) => Segments.Count == 0 ? [new Seg(0, original, 0)] : Segments;

    /// The pieces of the edited range [a, b), optionally with a gain change.
    List<Seg> Slice(ulong original, ulong a, ulong b, float gain = 0)
    {
        var outp = new List<Seg>();
        ulong pos = 0;
        foreach (var s in Base(original))
        {
            ulong len = s.B - s.A, lo = Math.Max(a, pos), hi = Math.Min(b, pos + len);
            if (hi > lo) outp.Add(new Seg(s.A + (lo - pos), s.A + (hi - pos), s.GainDb + gain));
            pos += len;
        }
        return outp;
    }

    public void Trim(ulong original, ulong a, ulong b) { Segments = Slice(original, a, b); Clamp(original); }
    public void Cut(ulong original, ulong a, ulong b) { Segments = [.. Slice(original, 0, a), .. Slice(original, b, ulong.MaxValue)]; Clamp(original); }
    public void Gain(ulong original, ulong a, ulong b, float db) =>
        Segments = [.. Slice(original, 0, a), .. Slice(original, a, b, db), .. Slice(original, b, ulong.MaxValue)];
    void Clamp(ulong original) { ulong n = Length(original); FadeIn = Math.Min(FadeIn, n); FadeOut = Math.Min(FadeOut, n); }
}
