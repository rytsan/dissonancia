namespace Dissonancia;

/// Options fixed on the START tab for one session (spec §5 SessionConfig).
public sealed class Session
{
    public AppMode Mode = AppMode.GuitarChords;
    public Quality Quality = Quality.Balanced;
    public KeyOption Key = new(1, false);
    public Clef Clef = Clef.Treble8vb;
    public int BeatsPerBar = 4, BeatUnit = 4;
    public float Bpm = 92;
    public int CountInBars = 1;

    // Audio device (START tab). 0 = let the device choose; the core reads back the real values.
    public int CaptureDevice = -1;
    public uint SampleRate, PeriodFrames;
    public bool Exclusive, ClickOutput = true;
    public bool ClickDuringTake;           // false: click only in the count-in, the microphone does not record it

    // Notation (SCORE): shortest notated value, as a note-value denominator; 0 = the meter's beat unit.
    public int SmallestNote;
    public bool Triplets;

    /// Piano: grand staff (treble + bass, split at C4); the clef setting does not apply (spec §22.2).
    public bool GrandStaff => Mode == AppMode.PianoChords;

    public bool IsChordMode => Mode is AppMode.GuitarChords or AppMode.PianoChords or AppMode.GeneralChords;
}

/// View-side mirror of the core's LiveSnapshot (spec §22). Preallocated; the source fills it in place.
public sealed class LiveFrame
{
    public const int WaveColumns = 1024, ScopeSamples = 2048, MaxCqtBins = 160;
    public readonly float[] WaveMin = new float[WaveColumns];
    public readonly float[] WaveMax = new float[WaveColumns];
    public int WaveWriteIndex;
    public float WaveColumnSeconds;

    public float VuLevel = -20;            // VU units (0 VU = -18 dBFS)
    public float PeakDbfs = -90, PeakHoldDbfs = -90;
    public bool ClipLatched;

    public string ChordSymbol = "";
    public float ChordConfidence;
    public bool ChordConfirmed;
    public string ChordAlternatives = "";
    public string ChordReason = "";
    public string ChordRoman = "";         // Roman numeral in the session key, "" = no key
    public string Cadence = "", CadenceEvidence = "";   // last cadence candidate (live: harmonic evidence only)
    public float ChordLatencyMs, ChordConfirmElapsedMs;
    public Pitch Bass;
    public bool BassSettled, BassValid;
    public float BassSettleRemainingMs;
    public int[] ChordPitchClasses = [];
    public int[] Frets = [];               // per string (low E first), -1 = muted

    public readonly float[] Chroma = new float[12];   // normalized, C = 0 (chord modes)
    public float TuningCents;                          // estimated global offset vs session A4
    public bool TuningValid;

    public bool NoteValid;
    public Pitch Note;
    public float Cents, Hz, NoteLatencyMs;

    public readonly float[] Scope = new float[ScopeSamples];   // newest samples, oldest first
    public float SampleRate = 48000;
    public readonly float[] Cqt = new float[MaxCqtBins];       // magnitude per CQT bin, lowest first
    public int CqtBins, CqtBinsPerOctave = 12;
    public float CqtMinHz;
    public ulong AnalysisSequence;                             // +1 per analysis hop (one waterfall column)

    public float Bpm;
    public int BeatInBar;                  // 1-based, 0 = metronome off
    public bool Metronome = true, Recording, CountingIn;
    public double RecordedSeconds;         // negative while counting in: seconds until REC
    public float CaptureMs, ProcessingMs, DisplayMs, CpuPercent;
    public int Xruns, RecorderGaps;
    public bool Simulated;

    public readonly List<string> TimelineBars = [];   // confirmed chord per bar, oldest first
    public readonly List<string> TimelineRomans = []; // Roman numeral per TimelineBars entry (may be shorter: fake source)
    public string ProvisionalChord = "";
    public double BarPhase;                           // 0..1 position inside the current bar
    public readonly List<string> RecordedBars = [];   // fast-path event log (confirmed chords of the take)
}

/// Seam for the back-end: the native source (P/Invoke ana_read_snapshot / ana_drain_events)
/// replaces FakeLiveSource without touching the views.
public interface ILiveSource
{
    void Read(LiveFrame frame, double nowSeconds);
    void ToggleRec(double nowSeconds);
    void ToggleMetronome();
    void SetTempo(float bpm);   // tap tempo; refused by the core during REC
}

/// Simulated data so the GUI can be designed before the DSP core exists.
/// Everything it reports is flagged as Simulated — the GUI never presents it as measured.
public sealed class FakeLiveSource(Session session) : ILiveSource
{
    sealed record Chord(string Symbol, int[] PitchClasses, int[] Frets, Pitch Bass, string Alternatives, string Reason);

    static readonly Chord[] Progression =
    [
        new("G", [7, 11, 2], [3, 2, 0, 0, 0, 3], new(4, 0, 2), "Em/G · G6", ""),
        new("Em", [4, 7, 11], [0, 2, 2, 0, 0, 0], new(2, 0, 2), "G6/E · Em7", ""),
        new("C", [0, 4, 7], [-1, 3, 2, 0, 1, 0], new(0, 0, 3), "Am/C · C6", ""),
        new("D", [2, 6, 9], [-1, -1, 0, 2, 3, 2], new(1, 0, 3), "Bm/D · D6", ""),
        new("Am7/G", [9, 0, 4, 7], [3, 0, 2, 0, 1, 0], new(4, 0, 2), "C6/G · Am/G", "C6 ≡ Am7 — bass decides"),
    ];

    // Voice melody in G major with chromatic passing tones spelled by direction (§22.2):
    // ascending A → A♯ → B, descending B → B♭ → A.
    static readonly Pitch[] Melody =
    [
        new(4, 0, 4), new(5, 0, 4), new(5, 1, 4), new(6, 0, 4),
        new(1, 0, 5), new(0, 0, 5), new(6, 0, 4), new(6, -1, 4),
        new(5, 0, 4), new(3, 1, 4), new(4, 0, 4), new(4, 0, 4),
    ];

    static readonly int[] OpenStrings = [40, 45, 50, 55, 59, 64];
    const int Rate = 48000, SamplesPerColumn = 256;

    double _lastT = -1, _peakHoldT, _recStart = double.NaN;
    readonly float[] _scope = new float[LiveFrame.ScopeSamples];
    double _phase;
    int _scopeWrite;
    readonly float[] _columnBuf = new float[SamplesPerColumn];
    int _columnFill;
    float _vu = -20;
    bool _metronome = true;

    public void ToggleMetronome() { if (double.IsNaN(_recStart)) _metronome = !_metronome; }
    public void SetTempo(float bpm) { }   // reads session.Bpm every frame

    public void ToggleRec(double now)
    {
        if (!double.IsNaN(_recStart)) { _recStart = double.NaN; return; }
        _metronome = true;                                   // REC forces metronome + count-in (§5)
        double bar = BarSeconds;
        _recStart = (Math.Floor(now / bar) + 1 + session.CountInBars) * bar;
    }

    double BeatSeconds => 60.0 / session.Bpm;
    double BarSeconds => BeatSeconds * session.BeatsPerBar;

    public void Read(LiveFrame f, double t)
    {
        if (_lastT < 0 || t < _lastT) _lastT = t;   // first frame or clock jump back
        double bar = BarSeconds;
        long barIndex = (long)Math.Floor(t / bar);
        double barStart = barIndex * bar, age = t - barStart;
        var chord = Progression[barIndex % Progression.Length];

        // Chord preview → confirmation after 0.6 s, bass settles after T_low (210 ms, Balanced).
        f.ChordSymbol = chord.Symbol;
        f.ChordPitchClasses = chord.PitchClasses;
        f.Frets = chord.Frets;
        f.Bass = chord.Bass;
        f.ChordConfirmed = age > 0.6;
        f.BassSettled = age > 0.21;
        f.BassValid = true;
        f.ChordConfidence = (float)Math.Min(0.9, 0.35 + age * 0.9);
        f.ChordAlternatives = chord.Alternatives;
        f.ChordReason = f.BassSettled ? chord.Reason : "bass not settled";
        f.ChordLatencyMs = 118 + (float)(6 * Math.Sin(t * 3.1));
        f.ChordConfirmElapsedMs = f.ChordConfirmed ? 0 : (float)(age * 1000);

        f.TimelineBars.Clear();
        for (long b = Math.Max(0, barIndex - 7); b < barIndex; b++) f.TimelineBars.Add(Progression[b % Progression.Length].Symbol);
        if (f.ChordConfirmed) f.TimelineBars.Add(chord.Symbol);
        f.ProvisionalChord = f.ChordConfirmed ? "" : chord.Symbol;
        f.BarPhase = age / bar;

        // Voice: one melody note per beat, with vibrato.
        long beatIndex = (long)Math.Floor(t / BeatSeconds);
        var note = Melody[beatIndex % Melody.Length];
        double noteAge = t - beatIndex * BeatSeconds;
        f.NoteValid = noteAge > 0.035;
        f.Note = note;
        f.Cents = (float)(14 * Math.Sin(2 * Math.PI * 5.5 * t) + 4);
        f.Hz = (float)(note.Hz() * Math.Pow(2, f.Cents / 1200.0));
        f.NoteLatencyMs = 38 + (float)(3 * Math.Sin(t * 2.3));

        // Transport.
        f.Bpm = session.Bpm;
        f.Metronome = _metronome;
        f.BeatInBar = _metronome ? (int)(beatIndex % session.BeatsPerBar) + 1 : 0;
        bool armed = !double.IsNaN(_recStart);
        f.CountingIn = armed && t < _recStart;
        f.Recording = armed && t >= _recStart;
        f.RecordedSeconds = armed ? t - _recStart : 0;   // negative while counting in
        if (f.Recording && f.ChordConfirmed)
        {
            int recordedBar = (int)Math.Floor((t - _recStart) / bar);
            while (f.RecordedBars.Count <= recordedBar) f.RecordedBars.Add(chord.Symbol);
        }
        if (f.CountingIn) f.RecordedBars.Clear();

        f.CaptureMs = 8.1f;
        f.ProcessingMs = 31.4f + (float)Math.Sin(t);
        f.CpuPercent = 6;
        f.Simulated = true;

        Synthesize(f, t, chord, barStart, note, beatIndex * BeatSeconds);
        _lastT = t;
    }

    void Synthesize(LiveFrame f, double t, Chord chord, double barStart, Pitch note, double noteStart)
    {
        int n = (int)Math.Min((t - _lastT) * Rate, Rate / 10);
        double sumSq = 0, peak = 0;
        bool voice = !session.IsChordMode;
        for (int i = 0; i < n; i++)
        {
            double ts = _lastT + (double)i / Rate;
            double s = 0;
            if (voice)
            {
                double hz = note.Hz() * Math.Pow(2, 14 * Math.Sin(2 * Math.PI * 5.5 * ts) / 1200.0);
                _phase = (_phase + 2 * Math.PI * hz / Rate) % (2 * Math.PI);   // integrated: vibrato bends the pitch, not the time base
                double ph = _phase, env = Math.Min(1, (ts - noteStart) * 20);
                s = 0.14 * env * (Math.Sin(ph) + 0.5 * Math.Sin(2 * ph) + 0.25 * Math.Sin(3 * ph));
            }
            else
            {
                double env = Math.Exp(-(ts - barStart) * 1.6);
                for (int k = 0; k < 6; k++)
                {
                    if (chord.Frets[k] < 0) continue;
                    double hz = 440 * Math.Pow(2, (OpenStrings[k] + chord.Frets[k] - 69) / 12.0);
                    double ph = 2 * Math.PI * hz * ts;
                    s += 0.09 * env * (Math.Sin(ph) + 0.35 * Math.Sin(2 * ph));
                }
            }
            sumSq += s * s;
            peak = Math.Max(peak, Math.Abs(s));
            _scope[_scopeWrite] = (float)s;
            _scopeWrite = (_scopeWrite + 1) % LiveFrame.ScopeSamples;
            _columnBuf[_columnFill++] = (float)s;
            if (_columnFill == SamplesPerColumn)
            {
                float mn = 1, mx = -1;
                foreach (var v in _columnBuf) { mn = Math.Min(mn, v); mx = Math.Max(mx, v); }
                f.WaveMin[f.WaveWriteIndex] = mn;
                f.WaveMax[f.WaveWriteIndex] = mx;
                f.WaveWriteIndex = (f.WaveWriteIndex + 1) % LiveFrame.WaveColumns;
                _columnFill = 0;
            }
        }
        f.WaveColumnSeconds = (float)SamplesPerColumn / Rate;
        f.SampleRate = Rate;
        int tail = LiveFrame.ScopeSamples - _scopeWrite;
        Array.Copy(_scope, _scopeWrite, f.Scope, 0, tail);
        Array.Copy(_scope, 0, f.Scope, tail, _scopeWrite);
        SynthesizeCqt(f, t, chord, barStart, note, voice);
        if (n == 0) return;

        double dt = t - _lastT;
        float rmsDb = (float)(10 * Math.Log10(sumSq / n + 1e-12));
        float target = Math.Clamp(rmsDb + 18, -20, 3);
        _vu += (target - _vu) * (float)(1 - Math.Exp(-dt / 0.3));   // VU ballistics, 300 ms
        f.VuLevel = _vu;
        f.PeakDbfs = (float)(20 * Math.Log10(peak + 1e-9));
        if (f.PeakDbfs >= f.PeakHoldDbfs || t - _peakHoldT > 1.5) { f.PeakHoldDbfs = f.PeakDbfs; _peakHoldT = t; }
        if (peak >= 1) f.ClipLatched = true;
    }

    // CQT magnitudes of the sounding notes: 4 harmonics (1/h), 0.5-bin wide peaks, on a noise floor.
    void SynthesizeCqt(LiveFrame f, double t, Chord chord, double barStart, Pitch note, bool voice)
    {
        const float minHz = 82.41f;
        const int bins = 72;
        f.CqtBins = bins;
        f.CqtBinsPerOctave = 12;
        f.CqtMinHz = minHz;
        f.AnalysisSequence = (ulong)(t / 0.02);
        Array.Clear(f.Cqt);
        void Note(double hz, double amp)
        {
            for (int h = 1; h <= 4; h++)
            {
                double bin = 12 * Math.Log2(h * hz / minHz);
                for (int k = Math.Max(0, (int)bin - 2); k < Math.Min(bins, (int)bin + 3); k++)
                    f.Cqt[k] += (float)(amp / h * Math.Exp(-Math.Pow((k - bin) / 0.5, 2)));
            }
        }
        if (voice) Note(note.Hz(), 0.3);
        else
            for (int k = 0; k < 6; k++)
                if (chord.Frets[k] >= 0) Note(440 * Math.Pow(2, (OpenStrings[k] + chord.Frets[k] - 69) / 12.0), 0.2 * Math.Exp(-(t - barStart) * 1.6));
        for (int k = 0; k < bins; k++) f.Cqt[k] += 0.002f * (1 + (float)Math.Sin(k * 12.9898 + t * 78.233));
    }
}
