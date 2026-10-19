using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Dissonancia;

/// Basic Pitch (Spotify, Apache-2.0, ICASSP 2022; models/basic-pitch-nmp.onnx, 230 KB): polyphonic,
/// instrument-agnostic note posteriors over 2 s windows at 22.05 kHz, on the CPU. Used live
/// (LiveTranscriber) and offline over a whole separated stem (Notes, STUDIO).
public sealed class BasicPitch : IDisposable
{
    public const int Rate = 22050, Window = 43844, Frames = 172, Pitches = 88, LowestMidi = 21;
    public const double FrameSeconds = (double)Window / Frames / Rate;   // ~11.6 ms

    static string ModelPath => Path.Combine(AppContext.BaseDirectory, "models", "basic-pitch-nmp.onnx");
    public static bool Available => File.Exists(ModelPath);

    readonly InferenceSession _model = new(ModelPath);
    readonly DenseTensor<float> _tensor = new([1, Window, 1]);
    readonly string _input;

    public BasicPitch() => _input = _model.InputMetadata.Keys.First();

    /// Note and onset posteriors [Frames, Pitches] of one window (shorter input is zero-padded).
    public (float[,] Note, float[,] Onset) Run(ReadOnlySpan<float> window)
    {
        var buf = _tensor.Buffer.Span;
        buf.Clear();
        window[..Math.Min(window.Length, Window)].CopyTo(buf);
        using var outputs = _model.Run([NamedOnnxValue.CreateFromTensor(_input, _tensor)]);
        float[,] Get(string suffix)
        {
            var t = outputs.First(o => o.Name.EndsWith(suffix)).AsTensor<float>();   // :1 note, :2 onset, :0 contour
            var a = new float[Frames, Pitches];
            for (int f = 0; f < Frames; f++)
                for (int p = 0; p < Pitches; p++) a[f, p] = t[0, f, p];
            return a;
        }
        return (Get(":1"), Get(":2"));
    }

    /// Mono at any rate -> 22.05 kHz or `target` (2-tap average + linear interpolation).
    // ponytail: no proper anti-alias filter; the model reads pitches far below the folded band.
    public static float[] Resample(float[] x, int rate, int target = Rate)
    {
        double step = rate / (double)target;
        var y = new float[(int)(x.Length / step)];
        float S(int i) => i <= 0 ? x[0] : 0.5f * (x[i] + x[i - 1]);
        for (int k = 0; k < y.Length; k++)
        {
            double pos = k * step;
            int i = (int)pos;
            float a = S(i), b = S(Math.Min(i + 1, x.Length - 1));
            y[k] = a + (float)(pos - i) * (b - a);
        }
        return y;
    }

    public sealed record Note(double Start, double End, int Midi, float Confidence);

    /// Whole-recording transcription as Basic Pitch does it: overlapping windows (30 frames, half
    /// trimmed on each side), then notes from onset peaks extended while the note posterior holds.
    public List<Note> Notes(float[] audio, Func<double, bool>? progress = null,
                            float onsetThreshold = 0.5f, float frameThreshold = 0.3f, int minFrames = 11)
    {
        const int overlap = 30, trim = overlap / 2, hop = Window - overlap * 256;
        int total = (int)(audio.Length / (FrameSeconds * Rate)) + 1;
        var note = new float[total, Pitches];
        var onset = new float[total, Pitches];
        var padded = new float[overlap * 256 / 2 + audio.Length];
        audio.CopyTo(padded, overlap * 256 / 2);
        int frame = 0;
        for (int start = 0; start < padded.Length && frame < total; start += hop)
        {
            if (progress is not null && !progress((double)start / padded.Length)) return [];
            var (n, o) = Run(padded.AsSpan(start, Math.Min(Window, padded.Length - start)));
            for (int f = trim; f < Frames - trim && frame < total; f++, frame++)
                for (int p = 0; p < Pitches; p++) (note[frame, p], onset[frame, p]) = (n[f, p], o[f, p]);
        }
        return Decode(note, onset, frame, onsetThreshold, frameThreshold, minFrames);
    }

    // Basic Pitch's output_to_notes_polyphonic without the "melodia trick" (notes with no onset).
    // ponytail: notes that start without an onset peak (legato, slurs) are lost; add the trick if they matter.
    static List<Note> Decode(float[,] note, float[,] onset, int n, float onsetThreshold, float frameThreshold, int minFrames)
    {
        const int energyTolerance = 11;
        var remaining = (float[,])note.Clone();
        var peaks = new List<(int F, int P)>();
        for (int f = 1; f + 1 < n; f++)
            for (int p = 0; p < Pitches; p++)
                if (onset[f, p] >= onsetThreshold && onset[f, p] >= onset[f - 1, p] && onset[f, p] > onset[f + 1, p]) peaks.Add((f, p));
        var notes = new List<Note>();
        foreach (var (f0, p) in peaks.OrderByDescending(x => x.F))   // latest first, as Basic Pitch
        {
            int i = f0 + 1, k = 0;
            while (i < n - 1 && k < energyTolerance) { k = remaining[i, p] < frameThreshold ? k + 1 : 0; i++; }
            i -= k;
            if (i - f0 <= minFrames) continue;
            float sum = 0;
            for (int f = f0; f < i; f++)
            {
                sum += note[f, p];
                remaining[f, p] = 0;
                if (p > 0) remaining[f, p - 1] = 0;
                if (p + 1 < Pitches) remaining[f, p + 1] = 0;
            }
            notes.Add(new Note(f0 * FrameSeconds, i * FrameSeconds, LowestMidi + p, sum / (i - f0)));
        }
        // A weak note on a stronger one's 2nd, 3rd or 4th harmonic (an octave, a twelfth, two octaves above),
        // sounding through most of it, is that harmonic, not a voice (the live rule, extended).
        static double Overlap(Note a, Note b) => Math.Max(0, Math.Min(a.End, b.End) - Math.Max(a.Start, b.Start));
        notes.RemoveAll(h => h.Confidence < 0.6f && notes.Any(f => (h.Midi - f.Midi) is 12 or 19 or 24 && f.Confidence > h.Confidence && Overlap(f, h) > 0.5 * (h.End - h.Start)));
        notes.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Midi.CompareTo(b.Midi));
        return notes;
    }

    public void Dispose() => _model.Dispose();
}
