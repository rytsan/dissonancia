namespace Dissonancia;

// What the DS-V tubes show, from the core's scope rings and meters: the latest stereo output for
// the oscilloscope and the vectorscope, the phase correlation, a 1/12-octave spectrum with falling
// bars and peak hold, and 30 s of loudness history. Updated once per animation frame.
public sealed class ScopeData
{
    public const int Frames = 4096;
    public readonly float[] Master = new float[2 * Frames], Track = new float[2 * Frames];
    public int MasterCount, TrackCount;
    public double Rate = 48000;
    public bool SpectrumOfTrack;         // the spectrum reads the selected channel instead of the master
    public double Correlation;           // -1 .. +1 over the last 4096 frames (smoothed)
    public const int Bands = 120;        // 1/12 octave from 20 Hz: ten octaves
    public readonly float[] Spectrum = new float[Bands], Hold = new float[Bands];
    public const int History = 300;      // 30 s at 10 per second
    public readonly float[] Momentary = new float[History], ShortTerm = new float[History];
    public int HistoryHead;              // next slot
    long _lastHistory, _lastTick;

    readonly double[] _re = new double[Frames], _im = new double[Frames];
    static readonly double[] Window = Enumerable.Range(0, Frames).Select(i => 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / Frames)).ToArray();

    public ScopeData()
    {
        Array.Fill(Spectrum, -120f);
        Array.Fill(Hold, -120f);
        Array.Fill(Momentary, -120f);
        Array.Fill(ShortTerm, -120f);
    }

    public static double BandHz(int b) => 20 * Math.Pow(2, b / 12.0);

    public void Update(StudioPlayer p, MixMeters m, double rate, bool playing, long nowMs)
    {
        double dt = _lastTick == 0 ? 0.016 : Math.Clamp((nowMs - _lastTick) / 1000.0, 0, 0.25);
        _lastTick = nowMs;
        Rate = rate;
        MasterCount = p.Scope(0, Master);
        TrackCount = p.Scope(1, Track);
        if (!playing) { Fall(dt); return; }

        // Phase correlation of the master.
        double lr = 0, ll = 0, rr = 0;
        for (int i = 0; i < MasterCount; i++) { double l = Master[2 * i], r = Master[2 * i + 1]; lr += l * r; ll += l * l; rr += r * r; }
        double c = ll * rr > 1e-12 ? lr / Math.Sqrt(ll * rr) : 0;
        Correlation += (c - Correlation) * Math.Min(1, dt / 0.3);

        // Spectrum (Hann, 4096 points), the bins of each 1/12 octave as their peak.
        var src = SpectrumOfTrack ? Track : Master;
        int n = SpectrumOfTrack ? TrackCount : MasterCount;
        if (n == Frames)
        {
            for (int i = 0; i < Frames; i++) { _re[i] = 0.5 * (src[2 * i] + src[2 * i + 1]) * Window[i]; _im[i] = 0; }
            Fft(_re, _im);
            double binHz = rate / Frames;
            for (int b = 0; b < Bands; b++)
            {
                int k0 = Math.Max(1, (int)(BandHz(b) / Math.Pow(2, 1 / 24.0) / binHz)), k1 = Math.Clamp((int)(BandHz(b) * Math.Pow(2, 1 / 24.0) / binHz), k0, Frames / 2 - 1);
                double e = 0;
                for (int k = k0; k <= k1; k++) e = Math.Max(e, _re[k] * _re[k] + _im[k] * _im[k]);
                float db = (float)(10 * Math.Log10(Math.Max(e, 1e-20)) - 20 * Math.Log10(Frames / 4.0));   // a full-scale sine reads 0 dB
                Spectrum[b] = Math.Max(db, Spectrum[b] - (float)(30 * dt));   // ballistics: instant rise, 30 dB/s fall
                Hold[b] = Math.Max(db, Hold[b] - (float)(6 * dt));
            }
        }
        if (nowMs - _lastHistory >= 100)
        {
            _lastHistory = nowMs;
            Momentary[HistoryHead] = m.LufsM;
            ShortTerm[HistoryHead] = m.LufsS;
            HistoryHead = (HistoryHead + 1) % History;
        }
    }

    void Fall(double dt)
    {
        for (int b = 0; b < Bands; b++) { Spectrum[b] = Math.Max(-120, Spectrum[b] - (float)(30 * dt)); Hold[b] = Math.Max(-120, Hold[b] - (float)(6 * dt)); }
        Correlation *= Math.Max(0, 1 - dt / 0.3);
    }

    /// In-place radix-2 FFT (n a power of two).
    public static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double a = -2 * Math.PI / len, wr = Math.Cos(a), wi = Math.Sin(a);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int u = i + k, v = u + len / 2;
                    double tr = re[v] * cr - im[v] * ci, ti = re[v] * ci + im[v] * cr;
                    re[v] = re[u] - tr; im[v] = im[u] - ti;
                    re[u] += tr; im[u] += ti;
                    (cr, ci) = (cr * wr - ci * wi, cr * wi + ci * wr);
                }
            }
        }
    }
}
