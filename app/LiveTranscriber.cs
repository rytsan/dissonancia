using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Dissonancia;

/// LIVE Melodia, polyphonic notes by a neural transcriber: Basic Pitch (Spotify, Apache-2.0, ICASSP
/// 2022; models/basic-pitch-nmp.onnx, 230 KB) over the core's audio tap. Every 0.25 s the last 2 s
/// are resampled to 22.05 kHz and run on the CPU (~25 ms, a GPU would spend more on the copy than on
/// the model); the notes are read a few frames before the window's end, where the model has context
/// on both sides — about 0.35 s behind the input. Optional: without the model the CQT voices stay.
public sealed class LiveTranscriber : IDisposable
{
    const int ModelRate = 22050, Window = 43844, Frames = 172, Pitches = 88, LowestMidi = 21;
    const int HopSamples = ModelRate / 4;
    const int ReadFrom = Frames - 20, ReadTo = Frames - 8;   // frames averaged for "now" (~0.1-0.23 s before the end)
    const float Threshold = 0.4f;                           // note posterior; calibration knob (Basic Pitch's default frame threshold is 0.3)

    static string ModelPath => Path.Combine(AppContext.BaseDirectory, "models", "basic-pitch-nmp.onnx");
    public static bool Available => File.Exists(ModelPath);

    readonly nint _core;
    readonly InferenceSession _model;
    readonly string _input;
    readonly Thread _thread;
    readonly float[] _window = new float[Window];   // newest sample last
    volatile bool _enabled, _stop;
    volatile int[] _notes = [];                     // MIDI, highest first
    volatile bool _reset;
    volatile uint _rate;

    /// The device rate of the tap (snapshot.sampleRate), set by the UI thread each frame.
    public uint Rate { set => _rate = value; }

    public LiveTranscriber(nint core)
    {
        _core = core;
        _model = new InferenceSession(ModelPath);
        _input = _model.InputMetadata.Keys.First();
        _thread = new Thread(Run) { IsBackground = true, Name = "dz-transcriber", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// On for a session in Melodia; turning it on drops what the tap held from before.
    public bool Enabled { get => _enabled; set { if (value && !_enabled) _reset = true; _enabled = value; if (!value) _notes = []; } }

    /// The notes sounding now (MIDI, highest first), empty while off or silent.
    public int[] Notes => _notes;

    void Run()
    {
        var raw = new float[16384];
        var resampled = new List<float>(HopSamples * 2);
        var tensor = new DenseTensor<float>(new[] { 1, Window, 1 });
        double pos = 0;   // resampler phase, in input samples (-1 = the last sample of the previous read)
        float prev = 0, prevS = 0;
        int n = 0;
        // ponytail: 2-tap average + linear interpolation; aliasing above 11 kHz is far above the model's pitches (a polyphase FIR if it shows).
        float S(int i) => i < 0 ? prevS : 0.5f * (raw[i] + (i == 0 ? prev : raw[i - 1]));
        while (!_stop)
        {
            if (!_enabled) { Thread.Sleep(50); continue; }
            if (_reset) { while (Ana.ReadAudio(_core, raw) > 0) { } Array.Clear(_window); resampled.Clear(); pos = 0; _reset = false; }
            n = Ana.ReadAudio(_core, raw);
            uint rate = _rate;
            if (n <= 0 || rate == 0) { Thread.Sleep(20); continue; }
            double step = rate / (double)ModelRate;
            for (; pos < n - 1; pos += step)
            {
                int i = (int)Math.Floor(pos);
                float a = S(i), b = S(i + 1);
                resampled.Add(a + (float)(pos - i) * (b - a));
            }
            prevS = S(n - 1);
            prev = raw[n - 1];
            pos -= n;
            if (resampled.Count < HopSamples) continue;

            int add = Math.Min(resampled.Count, Window);
            Array.Copy(_window, add, _window, 0, Window - add);
            resampled.CopyTo(resampled.Count - add, _window, Window - add, add);
            resampled.Clear();
            var heard = Transcribe(_window, tensor);
            if (_enabled) _notes = heard;
        }
    }

    /// The notes sounding near the end of a 2 s window at 22.05 kHz (MIDI, highest first, at most MaxVoices).
    public int[] Transcribe(float[] window, DenseTensor<float>? tensor = null)
    {
        tensor ??= new DenseTensor<float>(new[] { 1, Window, 1 });
        window.AsSpan(0, Window).CopyTo(tensor.Buffer.Span);
        using var outputs = _model.Run([NamedOnnxValue.CreateFromTensor(_input, tensor)]);
        var note = outputs.First(o => o.Name.EndsWith(":1")).AsTensor<float>();   // :1 note, :2 onset, :0 contour
        var level = new float[Pitches];
        for (int p = 0; p < Pitches; p++)
        {
            for (int f = ReadFrom; f < ReadTo; f++) level[p] += note[0, f, p];
            level[p] /= ReadTo - ReadFrom;
        }
        // A weak note an octave over a stronger one is its 2nd harmonic, not a voice; the strongest
        // voices win the slots, shown highest first.
        return [.. Enumerable.Range(0, Pitches)
            .Where(p => level[p] >= Threshold && !(p >= 12 && level[p - 12] > level[p] && level[p] < 0.6f))
            .OrderByDescending(p => level[p]).Take(LiveFrame.MaxVoices)
            .OrderByDescending(p => p).Select(p => LowestMidi + p)];
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(500);
        _model.Dispose();
    }
}
