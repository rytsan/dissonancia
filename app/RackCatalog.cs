using System.Text.Json;

namespace Dissonancia;

// Modular rack (spec §22.4): a fixed catalog of view-only modules, full or half width, heights in
// rack units, one preset per mode saved as JSON. Changing the rack never touches the analysis.

public enum ModuleKind { Input, Scope, Analyzer, Tuner, Fretboard, Keyboard, Waterfall, Timeline, Transport, Status }

public sealed record RackEntry(ModuleKind Module, bool Half, int HeightU);

public static class RackCatalog
{
    public const double UnitPx = 80;

    public static string Name(ModuleKind k) => k switch
    {
        ModuleKind.Input => "Analog VU + peak LEDs", ModuleKind.Analyzer => "Analyzer display (chord + note)",
        ModuleKind.Waterfall => "CQT waterfall", ModuleKind.Timeline => "Chord timeline", _ => k.ToString(),
    };

    /// REC access and the ms readout must always be on screen.
    public static bool Pinned(ModuleKind k) => k is ModuleKind.Transport or ModuleKind.Status;

    public static bool Applies(ModuleKind k, AppMode mode) => k switch
    {
        ModuleKind.Fretboard => mode == AppMode.GuitarChords,
        ModuleKind.Timeline or ModuleKind.Waterfall => mode is AppMode.GuitarChords or AppMode.PianoChords or AppMode.GeneralChords,
        _ => true,
    };

    public static (int Min, int Max) Units(ModuleKind k) => k switch
    {
        ModuleKind.Transport or ModuleKind.Status => (1, 1),
        ModuleKind.Analyzer => (3, 5),
        ModuleKind.Timeline => (1, 2),
        ModuleKind.Input or ModuleKind.Fretboard or ModuleKind.Keyboard => (2, 3),
        _ => (1, 4),
    };

    public static double Height(RackEntry e) => e.Module switch
    {
        ModuleKind.Transport => 96, ModuleKind.Status => 38, ModuleKind.Timeline => e.HeightU * 84, _ => e.HeightU * UnitPx,
    };

    public static List<RackEntry> Default(AppMode mode)
    {
        List<RackEntry> top = [new(ModuleKind.Input, true, 2), new(ModuleKind.Analyzer, true, 4), new(ModuleKind.Scope, true, 2)];
        List<RackEntry> middle = mode switch
        {
            AppMode.VoiceMono or AppMode.InstrumentMono => [new(ModuleKind.Tuner, false, 2)],
            AppMode.GuitarChords => [new(ModuleKind.Fretboard, false, 2), new(ModuleKind.Timeline, false, 1)],
            AppMode.PianoChords => [new(ModuleKind.Keyboard, false, 2), new(ModuleKind.Waterfall, false, 2), new(ModuleKind.Timeline, false, 1)],
            _ => [new(ModuleKind.Waterfall, false, 2), new(ModuleKind.Timeline, false, 1)],
        };
        return [.. top, .. middle, new(ModuleKind.Transport, false, 1), new(ModuleKind.Status, false, 1)];
    }

    static string PresetPath(AppMode mode) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dissonancia", $"rack-{mode}.json");

    sealed record Dto(string module, string width, int heightU);

    /// User preset for the mode, else the default. Unknown, duplicate or inapplicable entries are
    /// dropped; pinned modules are restored; heights are clamped to the module's range.
    public static List<RackEntry> Load(AppMode mode)
    {
        try
        {
            var path = PresetPath(mode);
            if (File.Exists(path) && JsonSerializer.Deserialize<Dto[]>(File.ReadAllText(path)) is { } items)
            {
                var list = new List<RackEntry>();
                foreach (var d in items)
                    if (Enum.TryParse<ModuleKind>(d.module, out var k) && Applies(k, mode) && list.All(e => e.Module != k))
                        list.Add(new RackEntry(k, d.width == "half" && !Pinned(k), Math.Clamp(d.heightU, Units(k).Min, Units(k).Max)));
                foreach (var pinned in new[] { ModuleKind.Transport, ModuleKind.Status })
                    if (list.All(e => e.Module != pinned)) list.Add(new RackEntry(pinned, false, 1));
                return list;
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return Default(mode);
    }

    public static void Save(AppMode mode, IEnumerable<RackEntry> entries)
    {
        try
        {
            var path = PresetPath(mode);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(entries.Select(e => new Dto(e.Module.ToString(), e.Half ? "half" : "full", e.HeightU)),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static void Reset(AppMode mode)
    {
        try { File.Delete(PresetPath(mode)); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
