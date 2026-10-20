using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Dissonancia;

/// Acceleration (studio-plan "Reliable in any environment"): what this machine is, which GPU
/// backend fits it, and the onnxruntime build that uses it. The app ships the CPU onnxruntime;
/// the GPU build for the machine is installed on demand (pinned downloads, checked by SHA-256),
/// then checked in a child process against the CPU result before anything uses it.
///   Linux x64 + NVIDIA driver: CUDA (onnxruntime-gpu + the CUDA 12 / cuDNN 9 libraries the
///                              system lacks, from NVIDIA's redistributable wheels)
///   Windows 10+ x64:           DirectML (any DirectX 12 GPU: NVIDIA, AMD, Intel)
///   macOS:                     CoreML (in the shipped build; only checked)
///   anything else:             CPU
public static class Accel
{
    public const string OrtVersion = "1.20.1";   // = the Microsoft.ML.OnnxRuntime package the app is built with

    /// One pinned download: an archive whose files under Take (one folder, no subfolders) are the libraries.
    public sealed record Pack(string Name, string Url, long Size, string Sha256, string Take, string? Soname = null);

    static readonly Pack[] CudaPacks =
    [
        new("onnxruntime-gpu " + OrtVersion, $"https://github.com/microsoft/onnxruntime/releases/download/v{OrtVersion}/onnxruntime-linux-x64-gpu-{OrtVersion}.tgz",
            258487100, "6bfb87c6ebe55367a94509b8ef062239e188dccf8d5caac8d6909b2344893bf0", $"onnxruntime-linux-x64-gpu-{OrtVersion}/lib/"),
        // Soname: skipped when the system already has it (a CUDA toolkit or cuDNN install).
        new("cuDNN 9.5", "https://files.pythonhosted.org/packages/2a/78/4535c9c7f859a64781e43c969a3a7e84c54634e319a996d43ef32ce46f83/nvidia_cudnn_cu12-9.5.1.17-py3-none-manylinux_2_28_x86_64.whl",
            570988386, "30ac3869f6db17d170e0e556dd6cc5eee02647abc31ca856634d5a40f82c15b2", "nvidia/cudnn/lib/", "libcudnn.so.9"),
        new("cuBLAS 12.6", "https://files.pythonhosted.org/packages/af/eb/ff4b8c503fa1f1796679dce648854d58751982426e4e4b37d6fce49d259c/nvidia_cublas_cu12-12.6.4.1-py3-none-manylinux2014_x86_64.manylinux_2_17_x86_64.whl",
            393138322, "08ed2686e9875d01b58e3cb379c6896df8e76c75e0d4a7f7dace3d7b6d9ef8eb", "nvidia/cublas/lib/", "libcublas.so.12"),
        new("cuFFT 11.3", "https://files.pythonhosted.org/packages/8f/16/73727675941ab8e6ffd86ca3a4b7b47065edcca7a997920b831f8147c99d/nvidia_cufft_cu12-11.3.0.4-py3-none-manylinux2014_x86_64.manylinux_2_17_x86_64.whl",
            200221632, "ccba62eb9cef5559abd5e0d54ceed2d9934030f51163df018532142a8ec533e5", "nvidia/cufft/lib/", "libcufft.so.11"),
        new("cuRAND 10.3", "https://files.pythonhosted.org/packages/73/1b/44a01c4e70933637c93e6e1a8063d1e998b50213a6b65ac5a9169c47e98e/nvidia_curand_cu12-10.3.7.77-py3-none-manylinux2014_x86_64.manylinux_2_17_x86_64.whl",
            56279010, "a42cd1344297f70b9e39a1e4f467a4e1c10f1da54ff7a85c12197f6c652c8bdf", "nvidia/curand/lib/", "libcurand.so.10"),
        new("NVRTC 12.6", "https://files.pythonhosted.org/packages/75/2e/46030320b5a80661e88039f59060d1790298b4718944a65a7f2aeda3d9e9/nvidia_cuda_nvrtc_cu12-12.6.77-py3-none-manylinux2014_x86_64.whl",
            23650380, "35b0cc6ee3a9636d5409133e79273ce1f3fd087abb0532d2d2e8fff1fe9efc53", "nvidia/cuda_nvrtc/lib/", "libnvrtc.so.12"),
        new("CUDA runtime 12.6", "https://files.pythonhosted.org/packages/e1/23/e717c5ac26d26cf39a27fbc076240fad2e3b817e5889d671b67f4f9f49c5/nvidia_cuda_runtime_cu12-12.6.77-py3-none-manylinux2014_x86_64.manylinux_2_17_x86_64.whl",
            897690, "ba3b56a4f896141e25e19ab287cd71e52a6a0f4b29d0d31609f60e3b4d5219b7", "nvidia/cuda_runtime/lib/", "libcudart.so.12"),
    ];

    static readonly Pack[] DirectMlPacks =
    [
        new("onnxruntime DirectML " + OrtVersion, $"https://www.nuget.org/api/v2/package/Microsoft.ML.OnnxRuntime.DirectML/{OrtVersion}",
            15844459, "6763468507b7cfc777b1334b3e174c11a540ddacb7bd4354bc2e0ec89e56eec2", "runtimes/win-x64/native/"),
        new("DirectML 1.15.2", "https://www.nuget.org/api/v2/package/Microsoft.AI.DirectML/1.15.2",
            202291463, "9f07482559087088a4dba4ae76eeeee1fad3f7077a92ccfbdb439c6bc2964c09", "bin/x64-win/"),
    ];

    // ---------------------------------------------------------------- the machine

    public sealed record Profile(string Os, string Arch, int Cores, long RamMb, string? Gpu, long VramMb, string? Driver,
                                 string Backend, string[] Issues, Pack[] Missing)
    {
        public long DownloadBytes => Missing.Sum(p => p.Size);
    }

    /// What this machine has and what the GPU backend for it still needs (no download, no load).
    public static Profile Detect()
    {
        string os = OperatingSystem.IsWindows() ? $"Windows {Environment.OSVersion.Version}" : OperatingSystem.IsMacOS() ? $"macOS {Environment.OSVersion.Version}"
                  : File.Exists("/proc/sys/fs/binfmt_misc/WSLInterop") || Environment.GetEnvironmentVariable("WSL_DISTRO_NAME") is not null ? "Linux (WSL)" : "Linux";
        var arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        long ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes >> 20;
        var (gpu, vram, driver) = NvidiaSmi();
        var issues = new List<string>();
        string backend = "cpu";
        Pack[] missing = [];
        if (OperatingSystem.IsMacOS()) backend = "coreml";
        else if (OperatingSystem.IsWindows())
        {
            if (RuntimeInformation.OSArchitecture != Architecture.X64) issues.Add("DirectML pack is x64 only");
            else if (Environment.OSVersion.Version.Build < 18362) issues.Add("DirectML needs Windows 10 1903 or later");
            else { backend = "directml"; missing = Installed("directml") ? [] : DirectMlPacks; }
        }
        else if (RuntimeInformation.OSArchitecture != Architecture.X64) issues.Add("GPU build is x64 only");
        else if (gpu is null) issues.Add("no NVIDIA GPU (nvidia-smi not found)");
        else if (driver is null || !int.TryParse(driver.Split('.')[0], out int major) || major < 525)
            issues.Add($"NVIDIA driver {driver ?? "?"}: CUDA 12 needs 525 or later");
        else
        {
            backend = "cuda";
            var sys = SystemSonames();
            missing = Installed("cuda") ? [] : [.. CudaPacks.Where(p => p.Soname is null || !sys.Contains(p.Soname))];
        }
        return new(os, arch, Environment.ProcessorCount, ram, gpu, vram, driver, backend, [.. issues], missing);
    }

    static (string? Gpu, long VramMb, string? Driver) NvidiaSmi()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=name,memory.total,driver_version --format=csv,noheader,nounits")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })!;
            var line = p.StandardOutput.ReadLine();
            p.WaitForExit(5000);
            var f = line?.Split(',', StringSplitOptions.TrimEntries);
            return f is { Length: 3 } ? (f[0], long.TryParse(f[1], out long v) ? v : 0, f[2]) : (null, 0, null);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return (null, 0, null); }
    }

    // The shared libraries the dynamic loader would find (ldconfig cache + LD_LIBRARY_PATH).
    // ponytail: glibc only (ldconfig); musl systems download the full set, which also works.
    static HashSet<string> SystemSonames()
    {
        var names = new HashSet<string>();
        try
        {
            using var p = Process.Start(new ProcessStartInfo("/sbin/ldconfig", "-p") { RedirectStandardOutput = true, UseShellExecute = false })!;
            for (string? l; (l = p.StandardOutput.ReadLine()) is not null;)
                if (l.TrimStart().Split(' ')[0] is { Length: > 0 } n) names.Add(n);
            p.WaitForExit(5000);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        foreach (var d in (Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
            if (Directory.Exists(d)) foreach (var f in Directory.GetFiles(d)) names.Add(Path.GetFileName(f));
        return names;
    }

    // ---------------------------------------------------------------- install

    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dissonancia", "runtime");
    static string DirOf(string backend) => Path.Combine(Root, $"{backend}-{OrtVersion}");
    static string CheckFile(string backend) => Path.Combine(DirOf(backend), "check.json");
    static bool Installed(string backend) => File.Exists(Path.Combine(DirOf(backend), "installed"));

    /// Downloads, verifies and unpacks every missing pack, then checks the GPU against the CPU.
    /// progress: (stage, 0..1). Returns null on success, else why.
    public static async Task<string?> Install(Profile p, Action<string, float> progress, CancellationToken ct)
    {
        if (p.Backend is "cpu") return "no GPU backend for this machine: " + string.Join("; ", p.Issues);
        var dir = DirOf(p.Backend);
        Directory.CreateDirectory(dir);
        long total = Math.Max(1, p.DownloadBytes), before = 0;
        foreach (var pack in p.Missing)
        {
            var tmp = Path.Combine(dir, "download.part");
            long at = before;
            var err = await Download.FileAsync(pack.Url, tmp, pack.Size, pack.Sha256, n => progress(pack.Name, (float)(at + n) / total), ct);
            if (err is not null) return $"{pack.Name}: {err}";
            progress("unpacking " + pack.Name, (float)(before + pack.Size) / total);
            try { await Task.Run(() => Unpack(tmp, pack.Take, dir), ct); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { return $"{pack.Name}: {e.Message}"; }
            finally { File.Delete(tmp); }
            before += pack.Size;
        }
        if (p.Backend is not "coreml") File.WriteAllText(Path.Combine(dir, "installed"), OrtVersion);
        progress("checking against the CPU", 1);
        var err2 = await RunCheck(p.Backend);
        Machine = Detect();
        // Takes effect now unless onnxruntime was already loaded (then on the next start; until
        // then Options() falls back to the CPU).
        if (err2 is null) Use(p.Backend, force: false);
        return err2;
    }

    // The files directly under `take` (a tgz or a zip: wheels and nupkgs are zips), flattened into dir.
    static void Unpack(string archive, string take, string dir)
    {
        static bool Lib(string name) => name.EndsWith(".dll") || name.EndsWith(".so") || name.Contains(".so.");
        bool Wanted(string path) => path.StartsWith(take) && path.IndexOf('/', take.Length) < 0 && Lib(path);
        using var f = File.OpenRead(archive);
        if (f.ReadByte() == 0x1f)   // gzip
        {
            f.Position = 0;
            using var tar = new TarReader(new GZipStream(f, CompressionMode.Decompress));
            // Regular files only: the loader asks for the real names (symlinks are aliases of them).
            for (TarEntry? e; (e = tar.GetNextEntry()) is not null;)
                if (e.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile && Wanted(e.Name))
                    e.ExtractToFile(Path.Combine(dir, Path.GetFileName(e.Name)), true);
            return;
        }
        f.Position = 0;
        using var zip = new ZipArchive(f);
        foreach (var e in zip.Entries)
            if (Wanted(e.FullName)) e.ExtractToFile(Path.Combine(dir, e.Name), true);
    }

    // ---------------------------------------------------------------- check (child process)

    public sealed record Check(bool Ok, string Backend, string Device, double MaxDiff, double CpuMs, double GpuMs, string? Error);

    /// The result of the last check, if the GPU build passed it (null: CPU only).
    public static Check? Passed(string backend)
    {
        try { return JsonSerializer.Deserialize<Check>(File.ReadAllText(CheckFile(backend))) is { Ok: true } c ? c : null; }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }

    // A crash in a GPU driver or library must not take the app down: the check runs in a child.
    static async Task<string?> RunCheck(string backend)
    {
        File.Delete(CheckFile(backend));
        var exe = Environment.ProcessPath!;
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(exe) == "dotnet") psi.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        psi.ArgumentList.Add("--accel-check");
        psi.ArgumentList.Add(backend);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        var c = Passed(backend);
        if (c is not null) return null;
        try { c = JsonSerializer.Deserialize<Check>(File.ReadAllText(CheckFile(backend))); }
        catch (Exception e) when (e is IOException or JsonException) { }
        return c?.Error ?? $"check failed (exit {p.ExitCode}): {(await err).Trim().Split('\n').LastOrDefault()}";
    }

    /// `--accel-check <backend>`: Basic Pitch (always shipped) on a fixed chord, on the CPU and on the
    /// GPU; the GPU passes when its note posteriors agree within 1e-3. Writes check.json.
    public static int CheckMain(string backend)
    {
        Check result;
        try
        {
            Use(backend, force: true);
            var x = new float[BasicPitch.Window];
            for (int i = 0; i < x.Length; i++)
                foreach (var f in (double[])[220, 277.18, 329.63]) x[i] += (float)(0.2 * Math.Sin(2 * Math.PI * f * i / BasicPitch.Rate));
            var (cpu, cpuMs) = RunModel(BasicPitch.ModelPath, x, new SessionOptions());
            var (gpu, gpuMs) = RunModel(BasicPitch.ModelPath, x, GpuOptions(backend));
            double diff = cpu.Zip(gpu).Max(z => Math.Abs(z.First - z.Second));
            result = new(diff < 1e-3, backend, Device(backend), diff, cpuMs, gpuMs, diff < 1e-3 ? null : $"GPU result differs from the CPU by {diff:g3}");
        }
        catch (Exception e) { result = new(false, backend, "", 0, 0, 0, e.GetType().Name + ": " + e.Message); }
        Directory.CreateDirectory(DirOf(backend));
        File.WriteAllText(CheckFile(backend), JsonSerializer.Serialize(result));
        return result.Ok ? 0 : 1;
    }

    static (float[] Out, double Ms) RunModel(string model, float[] x, SessionOptions opt)
    {
        using var s = new InferenceSession(model, opt);
        var t = new DenseTensor<float>(x, [1, x.Length, 1]);
        var input = s.InputMetadata.Keys.First();
        s.Run([NamedOnnxValue.CreateFromTensor(input, t)]).Dispose();   // warm-up (kernel selection)
        var sw = Stopwatch.StartNew();
        using var o = s.Run([NamedOnnxValue.CreateFromTensor(input, t)]);
        double ms = sw.Elapsed.TotalMilliseconds;
        return ([.. o.SelectMany(v => v.AsTensor<float>().ToArray())], ms);
    }

    static string Device(string backend) => backend switch { "cuda" => "CUDA", "directml" => "DirectML", "coreml" => "CoreML", _ => "CPU" };

    // ---------------------------------------------------------------- use

    static string? _active, _ortPath;
    static bool _resolver;

    /// At startup, before anything touches onnxruntime: the installed GPU build, if it passed its check.
    public static void Init()
    {
        Machine = Detect();   // ponytail: spawns nvidia-smi/ldconfig at startup (~50 ms); cache the profile if it shows
        if (Machine.Backend is not "cpu" && Passed(Machine.Backend) is not null) Use(Machine.Backend, force: false);
    }

    /// The machine as Init (or the last install) found it.
    public static Profile Machine { get => _machine ??= Detect(); private set => _machine = value; }
    static Profile? _machine;

    static void Use(string backend, bool force)
    {
        if (backend is "coreml") { _active = backend; return; }   // in the shipped build
        var dir = DirOf(backend);
        if (!force && !Installed(backend)) return;
        // The libraries the GPU build needs are found by name, so load the downloaded ones first
        // (by path; the loader then matches the names). Repeat until each one's dependencies are in.
        var pending = Directory.GetFiles(dir).Where(f => Path.GetFileName(f) is var n && !n.StartsWith("libonnxruntime") && !n.StartsWith("onnxruntime")
                                                         && (n.EndsWith(".dll") || n.Contains(".so"))).ToList();
        for (bool progress = true; progress && pending.Count > 0;)
        {
            progress = false;
            foreach (var f in pending.ToArray())
                if (NativeLibrary.TryLoad(f, out _)) { pending.Remove(f); progress = true; }
        }
        var ort = Path.Combine(dir, OperatingSystem.IsWindows() ? "onnxruntime.dll" : $"libonnxruntime.so.{OrtVersion}");
        if (!File.Exists(ort)) return;
        _ortPath = ort;
        _active = backend;
        if (!_resolver)   // one per assembly; consulted on onnxruntime's first call, so before any session
            NativeLibrary.SetDllImportResolver(typeof(InferenceSession).Assembly, (name, _, _) => name == "onnxruntime" && _ortPath is not null ? NativeLibrary.Load(_ortPath) : 0);
        _resolver = true;
    }

    public static string ActiveDevice => _active is null ? "CPU" : Device(_active);

    /// Options for a heavy model: the GPU when its build is active and passed, else the CPU.
    public static (SessionOptions Options, string Device) Options()
    {
        if (_active is null) return (new SessionOptions(), "cpu");
        try { return (GpuOptions(_active), _active); }
        catch (OnnxRuntimeException) { return (new SessionOptions(), "cpu"); }
    }

    static SessionOptions GpuOptions(string backend)
    {
        var o = new SessionOptions();
        switch (backend)
        {
            case "cuda": o.AppendExecutionProvider_CUDA(0); break;
            case "directml":   // DirectML requires these two
                o.EnableMemoryPattern = false;
                o.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                o.AppendExecutionProvider_DML(0);
                break;
            case "coreml": o.AppendExecutionProvider_CoreML(CoreMLFlags.COREML_FLAG_USE_NONE); break;
        }
        return o;
    }
}

/// Automatic setup: at every start, in the background, whatever this machine should have and lacks —
/// the GPU build for its backend, the Demucs weights (when the separation helper is in this build)
/// and the YourMT3+ weights (built here from the authors' checkpoint). Nothing comes from us:
/// every download is the upstream file at a pinned version and checksum. A failure or a cancel is remembered, so it is not retried at every start: the SYSTEM
/// section shows why and installs on request; the automatic run can also be turned off there.
public static class Setup
{
    public static volatile string Stage = "";
    public static volatile float Progress;
    public static volatile string? Error;
    public static volatile bool Running;
    static CancellationTokenSource? _cts;

    static string SkipFile => Path.Combine(Accel.Root, "auto-skip");   // holds why (failure, cancel, off)
    public static string? Skipped => File.Exists(SkipFile) ? File.ReadAllText(SkipFile) : null;

    /// Turns the automatic run on or off (off: nothing downloads until asked).
    public static void SetAuto(bool on)
    {
        if (on) File.Delete(SkipFile);
        else { Directory.CreateDirectory(Accel.Root); File.WriteAllText(SkipFile, "turned off"); }
    }

    static IEnumerable<DemucsModel.Variant> MissingDemucs() =>
        DemucsModel.HelperPresent ? new[] { DemucsModel.FourStems, DemucsModel.SixStems }.Where(v => !DemucsModel.Installed(v)) : [];

    static bool GpuPending(Accel.Profile p) => p.Backend is not "cpu" && Accel.Passed(p.Backend) is null;
    static bool Mt3Pending => YourMt3.Shipped && !YourMt3.Installed;

    /// What still needs installing on this machine, and its download size.
    public static (bool Needed, long Bytes) Pending()
    {
        var p = Accel.Machine;
        long bytes = (GpuPending(p) ? p.DownloadBytes : 0) + MissingDemucs().Sum(v => v.Size) + (Mt3Pending ? YourMt3.Recipe().CheckpointSize : 0);
        return (GpuPending(p) || MissingDemucs().Any() || Mt3Pending, bytes);
    }

    /// At startup: runs unless turned off or a previous run failed or was cancelled.
    public static void StartAuto() { if (Skipped is null && Pending().Needed) Start(); }

    /// On request (clears a remembered failure).
    public static void Start()
    {
        if (Running) return;
        File.Delete(SkipFile);
        (Running, Error, Progress, Stage) = (true, null, 0, "");
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Task.Run(async () =>
        {
            var errors = new List<string>();
            var p = Accel.Machine;
            if (GpuPending(p) && await Accel.Install(p, (s, f) => (Stage, Progress) = (s, f), ct) is { } e) errors.Add(e);
            foreach (var v in MissingDemucs().ToList())
            {
                if (ct.IsCancellationRequested) break;
                Stage = $"Demucs {v.Stems}-stem weights";
                if (await DemucsModel.InstallAsync(v, f => Progress = f, ct) is { } d) errors.Add($"Demucs {v.Stems} stems: {d}");
            }
            if (Mt3Pending && !ct.IsCancellationRequested && await YourMt3.Build((s, f) => (Stage, Progress) = (s, f), ct) is { } m) errors.Add("YourMT3+: " + m);
            Error = ct.IsCancellationRequested ? "cancelled" : errors.Count > 0 ? string.Join("; ", errors) : null;
            if (Error is not null) { Directory.CreateDirectory(Accel.Root); File.WriteAllText(SkipFile, Error); }
            Running = false;
        });
    }

    public static void Cancel() => _cts?.Cancel();
}
