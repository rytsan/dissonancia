using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Dissonancia;

// Blittable mirrors of core/include/dissonancia.h (spec §22). Layout checked against ana_struct_layout.

[StructLayout(LayoutKind.Sequential)]
unsafe struct SessionConfigNative
{
    public byte Mode, Quality, KeySet;
    public sbyte KeyFifths;
    public float ReferenceA4;
    public byte KeyMode, Clef, MeterNumerator, MeterDenominator;
    public float Bpm;
    public byte Metronome, CountInBars;
    public byte GuitarStringCount;
    public fixed sbyte GuitarOpenMidi[8];
    public sbyte CapoFret;
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct AudioDeviceConfigNative
{
    public int CaptureDevice;
    public uint SampleRate, PeriodFrames;
    public byte Exclusive, ClickOutput;
    fixed byte _pad0[2];
}

[StructLayout(LayoutKind.Sequential)]
struct PitchEstimateNative
{
    public byte Voiced;
    byte _p0, _p1, _p2;
    public float FrequencyHz, MidiFloat, Confidence, Clarity, Rms;
    public double TimestampSeconds;
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct NoteEstimateNative
{
    public byte Valid;
    public sbyte Midi, Letter, Alter, WrittenOctave;
    public fixed byte WrittenName[8];
    fixed byte _pad0[3];
    public float DetectedHz, ExpectedHz, Cents, Confidence;
    public byte Chromatic, Diatonic;
    fixed byte _pad1[2];
}

[StructLayout(LayoutKind.Sequential)]
struct TuningEstimateNative
{
    public byte Valid;
    byte _p0, _p1, _p2;
    public float ReferenceA4, OffsetCents, Confidence;
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct ChromaVectorNative
{
    public fixed float Raw[12];
    public fixed float Normalized[12];
    public fixed float Smoothed[12];
    public fixed float Bass[12];
    public double TimestampSeconds;
    public float Confidence, TuningOffsetCents;
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct ChordCandidateNative
{
    public sbyte RootPitchClass, BassPitchClass;
    public byte Quality;
    public fixed byte Symbol[16];
    public byte ExpectedCount, DetectedCount, MissingCount, ExtraCount;
    public fixed sbyte Expected[8];
    public fixed sbyte Detected[8];
    public fixed sbyte Missing[8];
    public fixed sbyte Extra[8];
    byte _pad0;
    public float RootScore, ThirdScore, FifthScore, ChromaScore, BassScore, TemporalScore, TonalScore, TotalScore, Confidence;
    public byte HasBass, Incomplete, Arpeggiated;
    byte _pad1;
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct ChordRecognitionNative
{
    public ChordCandidateNative Best;
    public byte AlternativeCount, Ambiguous;
    byte _p0, _p1;
    public ChordCandidateNative Alt0, Alt1, Alt2;
    public fixed byte Explanation[64];
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct BassEstimateNative
{
    public byte Valid;
    public sbyte Midi, PitchClass;
    public byte Settled;
    public float FrequencyHz, Confidence;
    public sbyte Letter, Alter, WrittenOctave;
    public fixed byte WrittenName[8];
    public byte FromPreview;
    public float PreviewHz, SettleRemainingMs;
}

public enum AnalyzerEventType : byte { Onset, NoteStart, NoteEnd, ChordConfirmed, ChordEnded }

/// Event header + raw payload; payload fields are read by offset (ChordEvent.symbol at data + 24).
[StructLayout(LayoutKind.Sequential)]
unsafe struct AnalyzerEventNative
{
    public AnalyzerEventType Type;
    byte _p0, _p1, _p2;
    public uint Sequence;
    public fixed byte Data[72];
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct LiveSnapshotNative
{
    public const int WaveColumns = 1024, ScopeSamples = 2048, MaxCqtBins = 160;
    public ulong Sequence;
    public double PublishTimeSeconds, RecordedSeconds;
    public ulong AnalyzedFrames;
    public uint SampleRate, LiveRate;
    public float HopSeconds, SettleSeconds, LatencyCaptureMs, LatencyProcessingMs;
    public uint Xruns, DroppedEvents, RecorderGaps;
    public float MetronomeBpm, VuLevel, PeakDbfs, PeakHoldDbfs;
    public uint WaveWriteIndex;
    public float WaveColumnSeconds, CpuPercent;
    public byte BeatInBar, Recording, CountingIn, ClipLatched;
    public float NoteLatencyMs;
    public PitchEstimateNative Pitch;
    public NoteEstimateNative Note;
    public ushort CqtBinCount, CqtBinsPerOctave;
    public float CqtMinHz;
    public TuningEstimateNative Tuning;
    fixed byte _pad1[4];
    public ChromaVectorNative Chroma;
    public fixed float CqtMagnitude[MaxCqtBins];
    public ChordRecognitionNative Chord;
    public fixed byte ConfirmedSymbol[16];
    public byte ChordConfirmed;
    byte _pad2a, _pad2b, _pad2c;
    public float ChordLatencyMs, ChordConfirmElapsedMs;
    public BassEstimateNative Bass;
    public double LastOnsetSeconds;
    public fixed float WaveMin[WaveColumns];
    public fixed float WaveMax[WaveColumns];
    public fixed float Scope[ScopeSamples];
}

[StructLayout(LayoutKind.Sequential)]
struct TimeSignatureNative { public byte Numerator, Denominator; }

[StructLayout(LayoutKind.Sequential)]
struct AbiLayout
{
    public uint SessionConfigSize, AudioDeviceConfigSize, LiveSnapshotSize, AnalyzerEventSize;
    public uint SnapshotWaveMinOffset, SnapshotScopeOffset, SnapshotBeatInBarOffset, EventDataOffset;
    public uint ChordEventSize, NoteEventSize;
    public uint SnapshotPitchOffset, SnapshotNoteOffset, SnapshotChromaOffset, SnapshotCqtOffset, SnapshotChordOffset, ChordResultSize, SnapshotBassOffset;
}

static partial class Ana
{
    const string Lib = "dissonancia";

    [LibraryImport(Lib, EntryPoint = "ana_create")] public static partial nint Create();
    [LibraryImport(Lib, EntryPoint = "ana_destroy")] public static partial void Destroy(nint h);
    [LibraryImport(Lib, EntryPoint = "ana_last_error")] public static partial nint LastError(nint h);
    [LibraryImport(Lib, EntryPoint = "ana_now")] public static partial double Now();
    [LibraryImport(Lib, EntryPoint = "ana_struct_layout")] public static partial void StructLayout(out AbiLayout layout);
    [LibraryImport(Lib, EntryPoint = "ana_capture_device_count")] public static partial int CaptureDeviceCount(nint h);
    [LibraryImport(Lib, EntryPoint = "ana_capture_device_name")] public static unsafe partial int CaptureDeviceName(nint h, int index, byte* utf8, int cap);
    [LibraryImport(Lib, EntryPoint = "ana_start")] public static partial int Start(nint h, in SessionConfigNative s, in AudioDeviceConfigNative d);
    [LibraryImport(Lib, EntryPoint = "ana_stop")] public static partial int Stop(nint h);
    [LibraryImport(Lib, EntryPoint = "ana_read_snapshot")] public static partial void ReadSnapshot(nint h, ref LiveSnapshotNative s);
    [LibraryImport(Lib, EntryPoint = "ana_drain_events")] public static unsafe partial int DrainEvents(nint h, AnalyzerEventNative* events, int cap);
    [LibraryImport(Lib, EntryPoint = "ana_rec_start", StringMarshalling = StringMarshalling.Utf8)] public static partial int RecStart(nint h, string wavPath);
    [LibraryImport(Lib, EntryPoint = "ana_rec_stop")] public static partial int RecStop(nint h);
    [LibraryImport(Lib, EntryPoint = "ana_set_metronome")] public static partial int SetMetronome(nint h, byte on, float bpm, TimeSignatureNative meter);
    [LibraryImport(Lib, EntryPoint = "ana_clear_clip")] public static partial void ClearClip(nint h);

    public static string Error(nint h) => Marshal.PtrToStringUTF8(LastError(h)) ?? "";

    /// Fails loudly when the C# mirrors drift from the core (spec §22 layout test).
    public static void CheckLayout()
    {
        StructLayout(out var l);
        void Eq(string what, long native, long managed)
        {
            if (native != managed) throw new InvalidOperationException($"ABI mismatch: {what} core={native} C#={managed}");
        }
        Eq("SessionConfig", l.SessionConfigSize, Unsafe.SizeOf<SessionConfigNative>());
        Eq("AudioDeviceConfig", l.AudioDeviceConfigSize, Unsafe.SizeOf<AudioDeviceConfigNative>());
        Eq("LiveSnapshot", l.LiveSnapshotSize, Unsafe.SizeOf<LiveSnapshotNative>());
        Eq("LiveSnapshot.beatInBar", l.SnapshotBeatInBarOffset, Marshal.OffsetOf<LiveSnapshotNative>(nameof(LiveSnapshotNative.BeatInBar)));
        Eq("LiveSnapshot.waveMin", l.SnapshotWaveMinOffset, Marshal.OffsetOf<LiveSnapshotNative>(nameof(LiveSnapshotNative.WaveMin)));
        Eq("LiveSnapshot.scope", l.SnapshotScopeOffset, Marshal.OffsetOf<LiveSnapshotNative>(nameof(LiveSnapshotNative.Scope)));
        Eq("LiveSnapshot.pitch", l.SnapshotPitchOffset, Marshal.OffsetOf<LiveSnapshotNative>(nameof(LiveSnapshotNative.Pitch)));
        Eq("LiveSnapshot.note", l.SnapshotNoteOffset, Marshal.OffsetOf<LiveSnapshotNative>(nameof(LiveSnapshotNative.Note)));
        Eq("LiveSnapshot.chroma", l.SnapshotChromaOffset, Marshal.OffsetOf<LiveSnapshotNative>(nameof(LiveSnapshotNative.Chroma)));
        Eq("LiveSnapshot.cqtMagnitude", l.SnapshotCqtOffset, Marshal.OffsetOf<LiveSnapshotNative>(nameof(LiveSnapshotNative.CqtMagnitude)));
        Eq("LiveSnapshot.chord", l.SnapshotChordOffset, Marshal.OffsetOf<LiveSnapshotNative>(nameof(LiveSnapshotNative.Chord)));
        Eq("ChordRecognitionResult", l.ChordResultSize, Unsafe.SizeOf<ChordRecognitionNative>());
        Eq("LiveSnapshot.bass", l.SnapshotBassOffset, Marshal.OffsetOf<LiveSnapshotNative>(nameof(LiveSnapshotNative.Bass)));
        Eq("AnalyzerEvent", l.AnalyzerEventSize, Unsafe.SizeOf<AnalyzerEventNative>());
    }
}

/// App-lifetime core handle: device list for START, one LIVE session at a time.
public sealed class NativeCore : IDisposable
{
    readonly nint _h;

    NativeCore(nint h) => _h = h;

    /// Null when the native library is missing or its layout does not match (the GUI then runs on simulated data).
    public static NativeCore? TryLoad(out string error)
    {
        error = "";
        try
        {
            Ana.CheckLayout();
            nint h = Ana.Create();
            if (h == 0) { error = "ana_create failed"; return null; }
            return new NativeCore(h);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            error = e.Message;
            return null;
        }
    }

    public unsafe string[] CaptureDevices()
    {
        int n = Ana.CaptureDeviceCount(_h);
        var names = new string[Math.Max(0, n)];
        byte* buf = stackalloc byte[256];
        for (int i = 0; i < names.Length; i++)
            names[i] = Ana.CaptureDeviceName(_h, i, buf, 256) == 0 ? Marshal.PtrToStringUTF8((nint)buf) ?? "" : $"device {i}";
        return names;
    }

    /// Starts a LIVE session with the START-tab options (immutable until the next START).
    public NativeLiveSource Start(Session s)
    {
        Ana.Stop(_h);
        var cfg = new SessionConfigNative
        {
            Mode = (byte)s.Mode, Quality = (byte)s.Quality, KeySet = 1, KeyFifths = (sbyte)s.Key.Fifths,
            ReferenceA4 = 440, KeyMode = (byte)(s.Key.Minor ? 1 : 0), Clef = (byte)s.Clef,
            MeterNumerator = (byte)s.BeatsPerBar, MeterDenominator = (byte)s.BeatUnit, Bpm = s.Bpm,
            Metronome = 1, CountInBars = (byte)s.CountInBars, GuitarStringCount = 6,
        };
        sbyte[] standard = [40, 45, 50, 55, 59, 64];
        unsafe { for (int i = 0; i < standard.Length; i++) cfg.GuitarOpenMidi[i] = standard[i]; }
        var dev = new AudioDeviceConfigNative
        {
            CaptureDevice = s.CaptureDevice, SampleRate = s.SampleRate, PeriodFrames = s.PeriodFrames,
            Exclusive = (byte)(s.Exclusive ? 1 : 0), ClickOutput = (byte)(s.ClickOutput ? 1 : 0),
        };
        if (Ana.Start(_h, cfg, dev) != 0) throw new InvalidOperationException(Ana.Error(_h));
        return new NativeLiveSource(_h, s);
    }

    public void Dispose() => Ana.Destroy(_h);
}

/// ILiveSource backed by the C++ core. Pulls one snapshot per rendered frame (spec §22); no managed allocation per read.
public sealed class NativeLiveSource : ILiveSource
{
    readonly nint _h;
    readonly Session _session;
    LiveSnapshotNative _snap;   // held in a field: ana_read_snapshot copies into it in place
    readonly AnalyzerEventNative[] _events = new AnalyzerEventNative[256];
    uint _nextSequence;
    readonly TextCache _symbol = new(), _alt0 = new(), _alt1 = new(), _alt2 = new(), _reason = new(), _event = new();
    string _alternatives = "";
    readonly int[][] _pcSets = Enumerable.Range(0, 9).Select(n => new int[n]).ToArray();
    bool _metronome = true;

    /// Events lost between core and GUI (sequence gaps), shown as incomplete event log.
    public int EventGaps { get; private set; }

    internal NativeLiveSource(nint h, Session s) { _h = h; _session = s; }

    public string? LastError { get; private set; }

    public unsafe void Read(LiveFrame f, double now)
    {
        Ana.ReadSnapshot(_h, ref _snap);
        fixed (float* mn = _snap.WaveMin, mx = _snap.WaveMax)
        {
            new ReadOnlySpan<float>(mn, LiveFrame.WaveColumns).CopyTo(f.WaveMin);
            new ReadOnlySpan<float>(mx, LiveFrame.WaveColumns).CopyTo(f.WaveMax);
        }
        f.WaveWriteIndex = (int)_snap.WaveWriteIndex;
        f.WaveColumnSeconds = _snap.WaveColumnSeconds;
        f.VuLevel = _snap.VuLevel;
        f.PeakDbfs = _snap.PeakDbfs;
        f.PeakHoldDbfs = _snap.PeakHoldDbfs;
        f.ClipLatched = _snap.ClipLatched != 0;

        _metronome = _snap.MetronomeBpm > 0;
        f.Bpm = _snap.MetronomeBpm > 0 ? _snap.MetronomeBpm : _session.Bpm;
        f.Metronome = _metronome;
        f.BeatInBar = _snap.BeatInBar;
        f.Recording = _snap.Recording != 0;
        f.CountingIn = _snap.CountingIn != 0;
        f.RecordedSeconds = _snap.RecordedSeconds;
        f.BarPhase = 0;

        f.CaptureMs = _snap.LatencyCaptureMs;
        f.ProcessingMs = _snap.LatencyProcessingMs;
        f.CpuPercent = _snap.CpuPercent;
        f.Xruns = (int)_snap.Xruns;
        f.RecorderGaps = (int)_snap.RecorderGaps;
        f.Simulated = false;

        // Pitch (M1). Chords arrive with M3: until then the chord LCD stays empty.
        ref readonly var n = ref _snap.Note;
        f.NoteValid = n.Valid != 0;
        if (f.NoteValid) f.Note = new Pitch(n.Letter, n.Alter, n.WrittenOctave - _session.Clef.OctaveShift());   // GUI holds sounding pitch
        f.Hz = _snap.Pitch.Voiced != 0 ? _snap.Pitch.FrequencyHz : 0;
        f.Cents = f.NoteValid ? n.Cents : 0;
        f.NoteLatencyMs = _snap.NoteLatencyMs;
        // Chords (M3): preview every hop; confirmed state from the tracker. No bass yet (M4).
        ref readonly var ch = ref _snap.Chord;
        fixed (byte* sym = ch.Best.Symbol) f.ChordSymbol = _symbol.Get(sym, 16, Display);
        f.ChordConfirmed = _snap.ChordConfirmed != 0;
        f.ChordConfidence = ch.Best.Confidence;
        f.ChordLatencyMs = _snap.ChordLatencyMs;
        f.ChordConfirmElapsedMs = _snap.ChordConfirmElapsedMs;
        fixed (byte* why = ch.Explanation) f.ChordReason = _reason.Get(why, 64, s => s.Replace(" = ", " ≡ ").Replace(" - ", " — "));
        bool altChanged = false;
        fixed (byte* a0 = ch.Alt0.Symbol) altChanged |= _alt0.Changed(a0, 16, Display);
        fixed (byte* a1 = ch.Alt1.Symbol) altChanged |= _alt1.Changed(a1, 16, Display);
        fixed (byte* a2 = ch.Alt2.Symbol) altChanged |= _alt2.Changed(a2, 16, Display);
        if (altChanged) _alternatives = string.Join(" · ", new[] { _alt0.Value, _alt1.Value, _alt2.Value }.Take(ch.AlternativeCount).Where(s => s.Length > 0));
        f.ChordAlternatives = _alternatives;
        int detected = Math.Min((int)ch.Best.DetectedCount, 8);
        var pcs = _pcSets[detected];
        for (int i = 0; i < detected; i++) pcs[i] = ch.Best.Detected[i];
        f.ChordPitchClasses = pcs;
        // Bass (M4): spelled like the note; the GUI holds the sounding pitch.
        ref readonly var b = ref _snap.Bass;
        f.BassValid = b.Valid != 0;
        f.BassSettled = b.Settled != 0;
        f.BassSettleRemainingMs = b.SettleRemainingMs;
        if (f.BassValid) f.Bass = new Pitch(b.Letter, b.Alter, b.WrittenOctave - _session.Clef.OctaveShift());
        f.ProvisionalChord = !f.ChordConfirmed && f.ChordSymbol.Length > 0 ? f.ChordSymbol : "";
        fixed (float* chroma = _snap.Chroma.Normalized) new ReadOnlySpan<float>(chroma, 12).CopyTo(f.Chroma);
        f.TuningValid = _snap.Tuning.Valid != 0;
        f.TuningCents = _snap.Tuning.OffsetCents;

        // Drain every frame so the queue never overflows; SCORE (M6) will consume the payloads.
        fixed (AnalyzerEventNative* ev = _events)
        {
            int count;
            while ((count = Ana.DrainEvents(_h, ev, _events.Length)) > 0)
                for (int i = 0; i < count; i++)
                {
                    if (ev[i].Sequence != _nextSequence) EventGaps++;
                    _nextSequence = ev[i].Sequence + 1;
                    if (ev[i].Type != AnalyzerEventType.ChordConfirmed) continue;
                    // Chord timeline: one cell per confirmed chord change, newest last.
                    string sym = _event.Get(ev[i].Data + 24, 16, Display);
                    f.TimelineBars.Add(sym);
                    if (f.TimelineBars.Count > 8) f.TimelineBars.RemoveAt(0);
                }
        }
    }

    public void ToggleRec(double now)
    {
        if (_snap.Recording != 0 || _snap.CountingIn != 0) { Check(Ana.RecStop(_h)); return; }
        var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);   // "" when the folder does not exist
        var dir = Path.Combine(music.Length > 0 ? music : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Dissonancia");
        Directory.CreateDirectory(dir);
        Check(Ana.RecStart(_h, Path.Combine(dir, $"take-{DateTime.Now:yyyyMMdd-HHmmss}.wav")));
    }

    public void ToggleMetronome() =>
        Check(Ana.SetMetronome(_h, (byte)(_metronome ? 0 : 1), _session.Bpm, new TimeSignatureNative { Numerator = (byte)_session.BeatsPerBar, Denominator = (byte)_session.BeatUnit }));

    public void ClearClip() => Ana.ClearClip(_h);

    void Check(int r) => LastError = r == 0 ? null : Ana.Error(_h);

    /// ASCII chord symbol from the core -> display ("F#m7b5" -> "F♯m7♭5"; letters are upper case, so 'b' is a flat).
    static string Display(string s) => s.Replace('#', '♯').Replace('b', '♭');
}

/// Rebuilds a managed string only when the native bytes change (no per-frame allocation, spec §22.7).
sealed unsafe class TextCache
{
    readonly byte[] _last = new byte[64];
    int _len = -1;
    public string Value { get; private set; } = "";

    public bool Changed(byte* p, int max, Func<string, string>? map = null)
    {
        int n = 0;
        while (n < max && p[n] != 0) n++;
        var bytes = new ReadOnlySpan<byte>(p, n);
        if (n == _len && bytes.SequenceEqual(_last.AsSpan(0, n))) return false;
        bytes.CopyTo(_last);
        _len = n;
        string s = System.Text.Encoding.UTF8.GetString(bytes);
        Value = map is null ? s : map(s);
        return true;
    }

    public string Get(byte* p, int max, Func<string, string>? map = null) { Changed(p, max, map); return Value; }
}
