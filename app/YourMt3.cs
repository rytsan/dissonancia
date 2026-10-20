using System.Numerics;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Dissonancia;

/// STUDIO, optional: YourMT3+ (Chang et al. 2024, "YourMT3+: Multi-instrument Music Transcription
/// with Enhanced Transformer Architectures and Cross-dataset Stem Augmentation", Apache-2.0), the
/// YPTF.MoE+Multi model — every note of the take with its instrument. Native: the authors' model
/// exported to ONNX (tools/yourmt3/export_onnx.py, dev-side) and run here with onnxruntime — on the GPU
/// when the machine has it, else the CPU. The spectrogram, the greedy decoding loop and the
/// token -> note decoding are ports of the authors' Python, checked against it (tools/yourmt3).
public sealed class YourMt3 : IDisposable
{
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dissonancia", "models", "yourmt3");
    static readonly string[] Files = ["encoder.onnx", "decoder_first.onnx", "decoder_next.onnx", "meta.json"];
    public static bool Installed => Files.All(f => File.Exists(Path.Combine(Dir, f)));

    public sealed record Note(double Start, double End, int Midi, int Velocity, int Program, string Instrument, bool Drum);
    public sealed record Result(string Model, List<Note> Notes);

    sealed class Meta
    {
        public int sample_rate { get; set; }
        public int input_frames { get; set; }
        public int n_fft { get; set; }
        public int hop_length { get; set; }
        public float eps { get; set; }
        public int channels { get; set; }
        public int max_tokens { get; set; }
        public int tps { get; set; }
        public List<JsonElement> events { get; set; } = [];
        public Dictionary<string, JsonElement> programs { get; set; } = [];
    }

    readonly Meta _m;
    readonly (string Type, int Value)[] _events;
    readonly InferenceSession _enc, _first, _next;
    readonly RunOptions _run = new();
    readonly OrtMemoryInfo _mem;   // where the caches stay between steps
    public string Device { get; }

    public YourMt3()
    {
        _m = JsonSerializer.Deserialize<Meta>(File.ReadAllText(Path.Combine(Dir, "meta.json")))!;
        _events = [.. _m.events.Select(e => (e[0].GetString()!, e[1].GetInt32()))];
        var (opt, device) = Accel.Options();   // the GPU when its build is installed and checked
        Device = device;
        Batch = device == "cpu" ? 1 : 8;
        _mem = device switch
        {
            "cuda" => new OrtMemoryInfo(OrtMemoryInfo.allocatorCUDA, OrtAllocatorType.DeviceAllocator, 0, OrtMemType.Default),
            "directml" => new OrtMemoryInfo("DML", OrtAllocatorType.DeviceAllocator, 0, OrtMemType.Default),
            _ => OrtMemoryInfo.DefaultInstance,
        };
        _enc = new InferenceSession(Path.Combine(Dir, "encoder.onnx"), opt);
        _first = new InferenceSession(Path.Combine(Dir, "decoder_first.onnx"), opt);
        _next = new InferenceSession(Path.Combine(Dir, "decoder_next.onnx"), opt);
    }

    // ---------------------------------------------------------------- spectrogram

    /// torchaudio Spectrogram(n_fft, hop, hann, power 1, centred with reflect padding) of one
    /// segment, DC bin dropped, log(x + eps), min-max normalized over the segment: (T, F) row-major.
    public float[] Spectrogram(float[] seg, out int frames, out int bins)
    {
        int n = _m.n_fft, hop = _m.hop_length, half = n / 2, len = seg.Length;
        frames = 1 + len / hop;
        bins = half;
        var window = new float[n];
        for (int i = 0; i < n; i++) window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n));   // periodic
        float At(int i) => seg[i < 0 ? -i : i >= len ? 2 * (len - 1) - i : i];                        // reflect
        var spec = new float[frames * bins];
        var buf = new Complex[n];
        for (int t = 0; t < frames; t++)
        {
            for (int i = 0; i < n; i++) buf[i] = new Complex(At(t * hop - half + i) * window[i], 0);
            Fft(buf);
            for (int k = 1; k <= half; k++) spec[t * bins + k - 1] = (float)Math.Log(buf[k].Magnitude + _m.eps);
        }
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (var v in spec) { lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
        for (int i = 0; i < spec.Length; i++)
        {
            float v = (spec[i] - lo) / (hi - lo + 0.008f);
            spec[i] = float.IsFinite(v) ? v : 0;
        }
        return spec;
    }

    static void Fft(Complex[] a)   // in place, radix 2
    {
        int n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (a[i], a[j]) = (a[j], a[i]);
        }
        for (int size = 2; size <= n; size <<= 1)
        {
            var w = Complex.FromPolarCoordinates(1, -2 * Math.PI / size);
            for (int s = 0; s < n; s += size)
            {
                var wk = Complex.One;
                for (int k = 0; k < size / 2; k++, wk *= w)
                {
                    var u = a[s + k];
                    var v = a[s + k + size / 2] * wk;
                    a[s + k] = u + v;
                    a[s + k + size / 2] = u - v;
                }
            }
        }
    }

    // ---------------------------------------------------------------- decoding

    /// Greedy tokens of one segment, per decoding channel (as the authors' generate: stop at EOS,
    /// PAD after it).
    public int[][] Tokens(float[] spec, int frames, int bins) => Tokens(spec, 1, frames, bins)[0];

    /// The same for `batch` segments at once (specs back to back): one encoder run and one decoder
    /// step for all of them, until every channel of every segment has ended.
    public int[][][] Tokens(float[] specs, int batch, int frames, int bins)
    {
        const int eos = 1, pad = 0;
        int k = _m.channels, rows = batch * k;
        // The caches go from one run to the next as OrtValues left where the model runs (GPU memory
        // on a GPU): the cross-attention cache alone is ~35 MB per segment, and copying it out and
        // back every step made each step several times slower. Only the logits come to the CPU.
        using var specV = OrtValue.CreateTensorValueFromMemory(specs, [batch, frames, bins]);
        using var encOut = Run(_enc, [("spec", specV)], [], ["enc"]);
        var idsBuf = new long[rows];
        using var ids = OrtValue.CreateTensorValueFromMemory(idsBuf, [batch, k, 1]);
        using var first = Run(_first, [("ids", ids), ("enc", encOut[0])], ["logits"], ["self_kv", "cross_kv"]);
        var tokens = Enumerable.Range(0, rows).Select(_ => new List<int>()).ToArray();
        var alive = Enumerable.Repeat(true, rows).ToArray();
        var next = Argmax(first[0], rows);
        IDisposableReadOnlyCollection<OrtValue>? step = null;
        try
        {
            for (int n = 0; ; n++)
            {
                for (int r = 0; r < rows; r++)
                {
                    tokens[r].Add(alive[r] ? next[r] : pad);
                    if (next[r] == eos) alive[r] = false;
                }
                if (!alive.Any(a => a) || n + 1 >= _m.max_tokens) break;
                for (int r = 0; r < rows; r++) idsBuf[r] = next[r];
                var o = Run(_next, [("ids", ids), ("enc", encOut[0]), ("self_kv", step?[1] ?? first[1]), ("cross_kv", first[2])], ["logits"], ["self_kv_out"]);
                step?.Dispose();
                step = o;
                next = Argmax(o[0], rows);
            }
        }
        finally { step?.Dispose(); }
        // A segment's channels stop at its own longest one, as when it is decoded alone.
        return [.. Enumerable.Range(0, batch).Select(i =>
        {
            var seg = tokens.AsSpan(i * k, k).ToArray();
            int len = seg.Max(t => t.IndexOf(eos) is var e and >= 0 ? e + 1 : t.Count);
            return seg.Select(t => t.Take(len).ToArray()).ToArray();
        })];
    }

    // Outputs in the order named: cpuOut in CPU memory (to read), deviceOut where the model ran (to feed back).
    IDisposableReadOnlyCollection<OrtValue> Run(InferenceSession s, (string Name, OrtValue Value)[] inputs, string[] cpuOut, string[] deviceOut)
    {
        using var b = s.CreateIoBinding();
        foreach (var (n, v) in inputs) b.BindInput(n, v);
        foreach (var n in cpuOut) b.BindOutputToDevice(n, OrtMemoryInfo.DefaultInstance);
        foreach (var n in deviceOut) b.BindOutputToDevice(n, _mem);
        b.SynchronizeBoundInputs();
        s.RunWithBinding(_run, b);
        b.SynchronizeBoundOutputs();
        return b.GetOutputValues();
    }

    static int[] Argmax(OrtValue logits, int k)   // (B, K, 1, V): k = B*K rows
    {
        var l = logits.GetTensorDataAsSpan<float>();
        int v = l.Length / k;
        var r = new int[k];
        for (int c = 0; c < k; c++)
        {
            var row = l.Slice(c * v, v);
            float best = float.MinValue;
            for (int i = 0; i < v; i++)
                if (row[i] > best) { best = row[i]; r[c] = i; }
        }
        return r;
    }

    /// The whole recording (mono, any rate): 2.048 s segments back to back, as the authors slice it.
    /// Segments decoded together: on a GPU one step for several costs about the same as for one;
    /// on the CPU the time grows with the batch, so one at a time.
    public int Batch { get; set; }

    public List<Note> Transcribe(float[] audio, int rate, Func<double, bool>? progress = null)
    {
        var x = Resample(audio, rate, _m.sample_rate);
        int seg = _m.input_frames, count = Math.Max(1, (x.Length + seg - 1) / seg);
        var segments = new List<int[][]>();
        var buf = new float[seg];
        for (int i = 0; i < count;)
        {
            if (progress is not null && !progress((double)i / count)) return [];
            int batch = Math.Min(Batch, count - i), frames = 0, bins = 0;
            var specs = new List<float>();
            for (int j = 0; j < batch; j++, i++)
            {
                Array.Clear(buf);
                Array.Copy(x, i * seg, buf, 0, Math.Min(seg, x.Length - i * seg));
                specs.AddRange(Spectrogram(buf, out frames, out bins));
            }
            segments.AddRange(Tokens([.. specs], batch, frames, bins));
        }
        return Notes(segments);
    }

    /// torchaudio.functional.resample (sinc_interp_hann, lowpass_filter_width 6, rolloff 0.99), as the
    /// authors feed the model: the notes depend on it (a linear resampler loses some).
    public static float[] Resample(float[] x, int from, int to)
    {
        if (from == to) return x;
        int g = (int)BigInteger.GreatestCommonDivisor(from, to), orig = from / g, nw = to / g;
        const double lpw = 6, rolloff = 0.99;
        double baseFreq = Math.Min(orig, nw) * rolloff, scale = baseFreq / orig;
        int width = (int)Math.Ceiling(lpw * orig / baseFreq), taps = 2 * width + orig;
        var kernels = new float[nw, taps];
        for (int k = 0; k < nw; k++)
            for (int i = 0; i < taps; i++)
            {
                double t = (-(double)k / nw + (double)(i - width) / orig) * baseFreq;
                t = Math.Clamp(t, -lpw, lpw);
                double window = Math.Pow(Math.Cos(t * Math.PI / lpw / 2), 2);
                t *= Math.PI;
                kernels[k, i] = (float)((t == 0 ? 1 : Math.Sin(t) / t) * window * scale);
            }
        int outLen = (int)Math.Ceiling((double)nw * x.Length / orig);
        var y = new float[outLen];
        for (int j = 0, o = 0; o < outLen; j++)
            for (int k = 0; k < nw && o < outLen; k++, o++)
            {
                double acc = 0;
                int start = j * orig - width;   // x index of tap 0 (zero padding outside)
                for (int i = Math.Max(0, -start); i < taps && start + i < x.Length; i++) acc += kernels[k, i] * x[start + i];
                y[o] = (float)acc;
            }
        return y;
    }

    // ---------------------------------------------------------------- tokens -> notes
    // Port of event2note_event, merge_zipped_note_events_and_ties_to_notes, note_event2note and
    // mix_notes (amt/src/utils), with the same error tolerance: a malformed event is skipped.

    record struct NoteEvent(bool Drum, int Program, double? Time, int Velocity, int Pitch);
    sealed class RawNote(bool drum, int program, double onset, double offset, int pitch, int velocity)
    {
        public bool Drum = drum;
        public int Program = program, Pitch = pitch, Velocity = velocity;
        public double Onset = onset, Offset = offset;
    }
    const double MinOffset = 0.01;

    /// segments[i][channel] = tokens; segment i starts at input_frames * i / sample_rate.
    public List<Note> Notes(List<int[][]> segments)
    {
        var perChannel = new List<RawNote>();
        for (int ch = 0; ch < _m.channels; ch++)
        {
            var merged = new List<NoteEvent>();
            List<(int Program, int Pitch)>? prevLast = null;
            for (int i = 0; i < segments.Count; i++)
            {
                double start = (double)_m.input_frames * i / _m.sample_rate;
                var (nes, ties, last) = Events(segments[i][ch], start);
                if (prevLast is not null)
                {
                    var tied = ties.Select(t => (t.Program, t.Pitch)).ToHashSet();
                    foreach (var pp in prevLast)
                        if (!tied.Contains(pp)) merged.Add(new NoteEvent(false, pp.Program, start, 0, pp.Pitch));
                }
                merged.AddRange(nes);
                prevLast = last;
            }
            perChannel.AddRange(ToNotes(merged));
        }
        var mixed = Validate(Trim(perChannel.OrderBy(n => n.Onset).ThenBy(n => n.Drum).ThenBy(n => n.Program).ThenBy(n => n.Velocity)
            .ThenBy(n => n.Pitch).ThenBy(n => n.Offset).ToList()));
        return [.. mixed.Select(n =>
        {
            if (n.Drum) return new Note(n.Onset, n.Offset, n.Pitch, n.Velocity, 128, "Drums", true);
            var p = _m.programs.TryGetValue(n.Program.ToString(), out var e) ? (e[0].GetInt32(), e[1].GetString()!) : (n.Program, $"program {n.Program}");
            return new Note(n.Onset, n.Offset, n.Pitch, n.Velocity, p.Item1, p.Item2, false);
        })];
    }

    (List<NoteEvent> Notes, List<NoteEvent> Ties, List<(int, int)> Last) Events(int[] tokens, double startTime)
    {
        var events = tokens.Where(t => t >= 0 && t < _events.Length).Select(t => _events[t]).ToList();
        int? program = null, velocity = null;
        var ties = new List<NoteEvent>();
        var last = new List<(int, int)>();
        int tieIndex = -1;
        for (int i = 0; i < events.Count; i++)
        {
            var (type, value) = events[i];
            if (type == "tie") { tieIndex = i; break; }
            if (type == "shift") break;
            if (type == "program") program = value;
            else if (type == "pitch" && program is int p) { ties.Add(new NoteEvent(false, p, null, 1, value)); last.Add((p, value)); }
        }
        if (tieIndex < 0) return ([], [], []);
        var notes = new List<NoteEvent>();
        int startTick = (int)Math.Round(startTime * _m.tps), tick = startTick;
        foreach (var (type, value) in events.Skip(tieIndex + 1))
        {
            if (type == "shift") { if (value > 0 && value <= 1000) tick = startTick + value; }
            else if (type == "drum") notes.Add(new NoteEvent(true, 128, (double)tick / _m.tps, 1, value));
            else if (type == "program") program = value;
            else if (type == "velocity") velocity = value;
            else if (type == "pitch")
            {
                if (program is not int p || velocity is not int v) continue;
                if (v > 0) last.Add((p, value));
                else if (!last.Remove((p, value))) continue;   // note off without note on
                notes.Add(new NoteEvent(false, p, (double)tick / _m.tps, v, value));
            }
            else if (type == "EOS") break;
        }
        notes = [.. notes.OrderBy(n => n.Time).ThenBy(n => n.Drum).ThenBy(n => n.Program).ThenBy(n => n.Velocity).ThenBy(n => n.Pitch)];
        ties = [.. ties.OrderBy(n => n.Drum).ThenBy(n => n.Program).ThenBy(n => n.Pitch)];
        return (notes, ties, last);
    }

    static List<RawNote> ToNotes(List<NoteEvent> events)
    {
        var notes = new List<RawNote>();
        var active = new Dictionary<(int Pitch, int Program), NoteEvent>();
        foreach (var ne in events.OrderBy(n => n.Time).ThenBy(n => n.Drum).ThenBy(n => n.Pitch).ThenBy(n => n.Velocity).ThenBy(n => n.Program))
        {
            if (ne.Time is not double t) continue;
            if (ne.Drum) { if (ne.Velocity == 1) notes.Add(new RawNote(true, 128, t, t + MinOffset, ne.Pitch, 1)); continue; }
            var key = (ne.Pitch, ne.Program);
            if (ne.Velocity == 1)
            {
                if (active.Remove(key, out var a)) notes.Add(new RawNote(false, a.Program, a.Time!.Value, t, a.Pitch, a.Velocity));
                active[key] = ne;
            }
            else if (ne.Velocity == 0 && active.Remove(key, out var a)) notes.Add(new RawNote(false, a.Program, a.Time!.Value, t, a.Pitch, a.Velocity));
        }
        foreach (var ne in active.Values)
            if (ne.Velocity == 1 && ne.Time is double t) notes.Add(new RawNote(false, ne.Program, t, t + MinOffset, ne.Pitch, 1));
        foreach (var n in notes) if (n.Offset - n.Onset > 10) n.Offset = n.Onset + MinOffset;   // long note > 10 s
        notes = [.. notes.OrderBy(n => n.Onset).ThenBy(n => n.Drum).ThenBy(n => n.Program).ThenBy(n => n.Velocity).ThenBy(n => n.Pitch)];
        return Trim(Validate(notes));
    }

    static List<RawNote> Validate(List<RawNote> notes)
    {
        foreach (var n in notes)
        {
            if (n.Onset > n.Offset) n.Offset = Math.Max(n.Offset, n.Onset + MinOffset);
            else if (!n.Drum && n.Offset - n.Onset < 0.01) n.Offset = n.Onset + MinOffset;
        }
        return notes;
    }

    static List<RawNote> Trim(List<RawNote> notes)   // overlapping notes of one pitch and program, zero-length dropped
    {
        if (notes.Count <= 1) return notes;
        var kept = new List<RawNote>();
        foreach (var g in notes.GroupBy(n => (n.Pitch, n.Program, n.Drum)))
        {
            var s = g.OrderBy(n => n.Onset).ToList();
            for (int i = 1; i < s.Count; i++) if (s[i - 1].Offset > s[i].Onset) s[i - 1].Offset = s[i].Onset;
            kept.AddRange(s.Where(n => n.Onset < n.Offset));
        }
        return [.. kept.OrderBy(n => n.Onset).ThenBy(n => n.Drum).ThenBy(n => n.Program).ThenBy(n => n.Velocity).ThenBy(n => n.Pitch)];
    }

    public void Dispose() { _enc.Dispose(); _first.Dispose(); _next.Dispose(); _run.Dispose(); }
}

/// The STUDIO job around YourMt3: the whole take on a worker thread, the result as JSON.
public sealed class YourMt3Job
{
    public static bool Installed => YourMt3.Installed;
    readonly Task _task;
    readonly CancellationTokenSource _cancel = new();
    volatile string _stage = "loading", _error = "";
    volatile float _progress;

    YourMt3Job(string input, string output)
    {
        _task = Task.Run(() =>
        {
            try
            {
                using var model = new YourMt3();
                var audio = StemTrack.ReadMono(input, out int rate);
                _stage = "inference " + model.Device;
                var notes = model.Transcribe(audio, rate, p => { _progress = (float)p; return !_cancel.IsCancellationRequested; });
                if (_cancel.IsCancellationRequested) return;
                File.WriteAllText(output, JsonSerializer.Serialize(new YourMt3.Result("YPTF.MoE+Multi (noPS)", notes)));
                _stage = "done";
            }
            catch (Exception e) when (e is OnnxRuntimeException or IOException or JsonException or InvalidOperationException) { _error = e.Message; }
        });
    }

    public static YourMt3Job? Start(string input, string output, out string? error)
    {
        error = Installed ? null : "modelo não instalado";
        return Installed ? new YourMt3Job(input, output) : null;
    }

    public bool Running => !_task.IsCompleted;
    public string Stage => _stage;
    public float Progress => _progress;
    public bool Succeeded => _task.IsCompleted && _stage == "done";
    public string Error => _error.Length > 0 ? _error : _task.IsFaulted ? _task.Exception?.InnerException?.Message ?? "falhou" : _cancel.IsCancellationRequested ? "cancelado" : "";
    public void Cancel() => _cancel.Cancel();

    public static YourMt3.Result? Load(string path)
    {
        try { return JsonSerializer.Deserialize<YourMt3.Result>(File.ReadAllText(path)); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
}
