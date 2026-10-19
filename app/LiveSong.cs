namespace Dissonancia;

/// LIVE "Música completa": what the song so far says about itself — key, meter and form — from the
/// confirmed chords (ChordEnded events), the settled melody and the core's beat marks. Display only:
/// nothing here changes the analysis or the session's own key and meter.
public sealed class LiveSong
{
    readonly double[] _pcs = new double[12];               // pitch-class weight in seconds (Review.KeyOf)
    readonly List<(long Beat, string Symbol)> _chords = [];   // chord starts on the beat count
    long _beats;                                           // beat marks seen so far
    double _lastBeat = double.NaN, _period;

    /// One call per frame with the core's tempo estimate: counts beats as their marks arrive, so a
    /// chord's bar position is measured against nearby beats and a slow tempo drift does not add up.
    public void Beat(double lastBeatSeconds, float bpm)
    {
        if (bpm <= 0) return;
        _period = 60.0 / bpm;
        if (double.IsNaN(_lastBeat)) { _lastBeat = lastBeatSeconds; return; }
        if (lastBeatSeconds - _lastBeat > _period / 2) { _beats += Math.Max(1, (long)Math.Round((lastBeatSeconds - _lastBeat) / _period)); _lastBeat = lastBeatSeconds; }
    }

    public long BeatOf(double seconds) => _beats - (long)Math.Round((_lastBeat - seconds) / _period);

    public void AddChord(double start, double end, int root, int quality, string symbol)
    {
        if (quality >= 0 && quality < LeadSheet.Intervals.Length)
            foreach (var i in LeadSheet.Intervals[quality]) _pcs[(root + i) % 12] += 0.5 * (end - start);
        if (_period > 0 && symbol.Length > 0) _chords.Add((BeatOf(start), symbol));
    }

    public void AddMelody(int midi, double seconds) { if (midi >= 0) _pcs[midi % 12] += seconds; }

    public (KeyOption Key, double Correlation, double Margin)? Key => Review.KeyOf(_pcs);

    /// Beats per bar and the downbeat's phase on the beat count: chords change on downbeats more than
    /// anywhere else; 3 or 4 by how far that preference stands above chance. 0 = not enough chords yet.
    public (int Beats, int Phase) Meter()
    {
        if (_chords.Count < 6) return (0, 0);
        (int Beats, int Phase, double Score) best = (0, 0, double.MinValue);
        foreach (int m in new[] { 4, 3 })
            for (int phase = 0; phase < m; phase++)
            {
                double hit = _chords.Count(c => Mod(c.Beat - phase, m) == 0) / (double)_chords.Count - 1.0 / m;
                if (hit > best.Score + 1e-9) best = (m, phase, hit);
            }
        return (best.Beats, best.Phase);
    }

    /// Form: 4-bar phrases, each lettered by the earlier phrase whose bar chords it repeats (3 of 4
    /// bars), else a new letter. The last, unfinished phrase is left out.
    // ponytail: fixed 4-bar phrases; 8-bar or irregular sections need self-similarity segmentation.
    public List<char> Form()
    {
        var (m, phase) = Meter();
        var form = new List<char>();
        if (m == 0) return form;
        long firstBar = FloorDiv(_chords[0].Beat - phase, m), lastBar = FloorDiv(_beats - phase, m);
        var bars = new List<string>();
        for (long b = firstBar; b < lastBar; b++)
        {
            long downbeat = phase + b * m;
            string sym = _chords[0].Symbol;
            foreach (var c in _chords) if (c.Beat <= downbeat) sym = c.Symbol;   // the chord sounding on the downbeat
            bars.Add(sym);
        }
        var phrases = new List<string[]>();
        for (int i = 0; i + 4 <= bars.Count; i += 4)
        {
            var p = bars.GetRange(i, 4).ToArray();
            int same = phrases.FindIndex(q => q.Zip(p).Count(z => z.First == z.Second) >= 3);
            form.Add(same >= 0 ? form[same] : (char)('A' + form.Distinct().Count()));
            phrases.Add(p);
        }
        return form;
    }

    static long FloorDiv(long a, int b) => (long)Math.Floor(a / (double)b);
    static long Mod(long a, int b) => ((a % b) + b) % b;
}
