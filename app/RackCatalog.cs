using System.Text.Json;

namespace Dissonancia;

// Modular rack (spec §22.4): a fixed catalog of view-only modules, full or half width, heights in
// rack units, one preset per mode saved as JSON. Changing the rack never touches the analysis.

public enum ModuleKind { Input, Scope, Analyzer, Tuner, Fretboard, Keyboard, Waterfall, Timeline, Transport, Status, Metrics, Pads, Sequencer, Midi, Song }

public sealed record RackEntry(ModuleKind Module, bool Half, int HeightU);

public static class RackCatalog
{
    public const double UnitPx = 80;

    public static string Name(ModuleKind k) => k switch
    {
        ModuleKind.Input => "Analog VU + peak LEDs", ModuleKind.Analyzer => "Analyzer display (chord + note)",
        ModuleKind.Waterfall => "CQT waterfall", ModuleKind.Timeline => "Chord timeline", ModuleKind.Metrics => "Métricas do LIVE",
        ModuleKind.Pads => "Playground: pads + sampler", ModuleKind.Sequencer => "Playground: sequencer", ModuleKind.Midi => "Playground: controlador MIDI", ModuleKind.Song => "Playground: timeline da música", _ => k.ToString(),
    };

    /// REC access and the ms readout must always be on screen.
    public static bool Pinned(ModuleKind k) => k is ModuleKind.Transport or ModuleKind.Status;

    public static bool Applies(ModuleKind k, LiveMode mode) => k switch
    {
        ModuleKind.Fretboard => mode is LiveMode.Acoustic or LiveMode.AcousticVoice,
        _ => true,
    };

    public static (int Min, int Max) Units(ModuleKind k) => k switch
    {
        ModuleKind.Transport or ModuleKind.Status => (1, 1),
        ModuleKind.Analyzer => (3, 5),
        ModuleKind.Timeline => (1, 2),
        ModuleKind.Input or ModuleKind.Fretboard or ModuleKind.Keyboard => (2, 3),
        ModuleKind.Metrics or ModuleKind.Midi => (1, 2),
        ModuleKind.Pads => (3, 4),
        ModuleKind.Sequencer => (2, 4),
        ModuleKind.Song => (2, 3),
        _ => (1, 4),
    };

    public static double Height(RackEntry e) => e.Module switch
    {
        ModuleKind.Transport => 96, ModuleKind.Status => 38, ModuleKind.Timeline => e.HeightU * 84, _ => e.HeightU * UnitPx,
    };

    public static List<RackEntry> Default(LiveMode mode)
    {
        // Piano: a taller analyzer for the grand staff.
        bool piano = mode == LiveMode.Piano;
        List<RackEntry> top = [new(ModuleKind.Input, true, 2), new(ModuleKind.Analyzer, true, piano ? 5 : 4), new(ModuleKind.Scope, true, piano ? 3 : 2)];
        List<RackEntry> middle = mode switch
        {
            LiveMode.Melody => [new(ModuleKind.Keyboard, false, 2), new(ModuleKind.Waterfall, false, 2)],
            LiveMode.Acoustic or LiveMode.AcousticVoice => [new(ModuleKind.Fretboard, false, 2), new(ModuleKind.Timeline, false, 1)],
            LiveMode.Piano => [new(ModuleKind.Keyboard, false, 2), new(ModuleKind.Waterfall, false, 2), new(ModuleKind.Timeline, false, 1)],
            _ => [new(ModuleKind.Waterfall, false, 2), new(ModuleKind.Timeline, false, 1)],
        };
        return [.. top, .. middle, new(ModuleKind.Metrics, false, 1), new(ModuleKind.Transport, false, 1), new(ModuleKind.Status, false, 1)];
    }

    /// The playground's units (creation: pads, sequencer, MIDI), added and taken out together.
    public static readonly ModuleKind[] Playground = [ModuleKind.Song, ModuleKind.Pads, ModuleKind.Sequencer, ModuleKind.Midi];
    public static List<RackEntry> WithPlayground(List<RackEntry> l, bool on)
    {
        var r = l.Where(e => !Playground.Contains(e.Module)).ToList();
        if (on) r.InsertRange(0, [new(ModuleKind.Song, false, 2), new(ModuleKind.Sequencer, false, 2), new(ModuleKind.Pads, false, 3), new(ModuleKind.Midi, false, 1)]);   // on top: the section in front
        return r;
    }

    static string PresetPath(LiveMode mode) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dissonancia", $"rack-live-{mode}.json");

    sealed record Dto(string module, string width, int heightU);

    /// User preset for the mode, else the default. Unknown, duplicate or inapplicable entries are
    /// dropped; pinned modules are restored; heights are clamped to the module's range.
    public static List<RackEntry> Load(LiveMode mode)
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

    public static void Save(LiveMode mode, IEnumerable<RackEntry> entries)
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

    public static void Reset(LiveMode mode)
    {
        try { File.Delete(PresetPath(mode)); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
