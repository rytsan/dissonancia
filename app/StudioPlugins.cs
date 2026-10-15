using System.Diagnostics;
using System.Text.Json;

namespace Dissonancia;

// STUDIO S8 — VST3 inserts (spec §20.2) through the dz_plugin helper, a process of its own: a
// plugin that crashes or hangs never takes the app down. Optional: the helper ships with the app
// when it was built (DZ_WITH_VST3). Step 1: the installed effects, scanned one bundle per process
// (a bundle that crashes the scan loses only itself), cached by the bundle's modification time.
public static class PluginHost
{
    public sealed record Plugin(string Bundle, string Cid, string Name, string Vendor, string Version, string Sub);
    public sealed record Entry(string Bundle, long Stamp, List<Plugin> Plugins, string? Error);

    /// Tests point this at the core's build folder.
    public static string? HelperOverride;
    public static string Helper => HelperOverride ?? Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "dz_plugin.exe" : "dz_plugin");
    public static bool Present => File.Exists(Helper);
    public static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(15);

    public static string? CacheOverride;
    static string CachePath => CacheOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dissonancia", "plugins.json");

    static long Stamp(string bundle)
    {
        try   // a bundle is a folder: its newest file (a rebuilt binary leaves the folder's own time alone)
        {
            return Directory.Exists(bundle)
                ? Directory.EnumerateFiles(bundle, "*", SearchOption.AllDirectories).Select(f => File.GetLastWriteTimeUtc(f).Ticks).DefaultIfEmpty(Directory.GetLastWriteTimeUtc(bundle).Ticks).Max()
                : File.GetLastWriteTimeUtc(bundle).Ticks;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    static async Task<(int Code, string Out, string Err)> Run(CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo(Helper) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ScanTimeout);
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            return (-1, "", "não respondeu em 15 s");
        }
        return (p.ExitCode, await stdout, await stderr);
    }

    /// Renders a file through one effect, offline, in the helper (no timeout: a long take takes its
    /// time; cancel kills the process). The state blob is read from / written to the given files.
    public static async Task<string?> RenderAsync(Plugin plugin, string input, string output, string? stateIn, string? stateOut,
        IReadOnlyDictionary<uint, double>? parameters = null, Action<float>? progress = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(Helper) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "render", plugin.Bundle, plugin.Cid, input, output }) psi.ArgumentList.Add(a);
        if (stateIn is not null && File.Exists(stateIn)) { psi.ArgumentList.Add("--state-in"); psi.ArgumentList.Add(stateIn); }
        if (stateOut is not null) { psi.ArgumentList.Add("--state-out"); psi.ArgumentList.Add(stateOut); }
        foreach (var (id, v) in parameters ?? new Dictionary<uint, double>())
        {
            psi.ArgumentList.Add("--param");
            psi.ArgumentList.Add(FormattableString.Invariant($"{id}={v:R}"));
        }
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync(ct);
        try
        {
            while (await p.StandardOutput.ReadLineAsync(ct) is { } line)
                if (line.StartsWith("progress ") && float.TryParse(line[9..], System.Globalization.CultureInfo.InvariantCulture, out var f)) progress?.Invoke(f);
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException) { try { p.Kill(true); } catch (InvalidOperationException) { } return "cancelado"; }
        if (p.ExitCode == 0) return null;
        string err = (await stderr).Trim();
        return err.Length > 0 ? err : $"o plugin terminou com código {p.ExitCode}";   // a crash ends here, not in the app
    }

    public static List<Entry> Cached()
    {
        try { return File.Exists(CachePath) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(CachePath)) ?? [] : []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    /// Every installed bundle, rescanning only those changed since the cache. progress(done, total).
    public static async Task<List<Entry>> ScanAsync(Action<int, int>? progress = null, CancellationToken ct = default)
    {
        var (code, list, err) = await Run(ct, "list");
        if (code != 0) throw new IOException("dz_plugin list: " + err.Trim());
        var bundles = list.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
        var cache = Cached().ToDictionary(e => e.Bundle);
        var result = new List<Entry>();
        for (int i = 0; i < bundles.Count; i++)
        {
            progress?.Invoke(i, bundles.Count);
            string b = bundles[i];
            long stamp = Stamp(b);
            if (cache.TryGetValue(b, out var old) && old.Stamp == stamp) { result.Add(old); continue; }
            var (c, o, e) = await Run(ct, "scan", b);
            var plugins = new List<Plugin>();
            foreach (var line in o.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                try
                {
                    using var d = JsonDocument.Parse(line);
                    string S(string k) => d.RootElement.GetProperty(k).GetString() ?? "";
                    plugins.Add(new Plugin(b, S("cid"), S("name"), S("vendor"), S("version"), S("sub")));
                }
                catch (JsonException) { }
            result.Add(new Entry(b, stamp, plugins, c == 0 ? null : e.Trim().Length > 0 ? e.Trim() : $"o scan terminou com código {c}"));
        }
        progress?.Invoke(bundles.Count, bundles.Count);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(result));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return result;
    }
}
