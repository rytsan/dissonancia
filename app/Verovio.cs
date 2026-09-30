using System.Runtime.InteropServices;

namespace Dissonancia;

/// Verovio (LGPL-3.0) through its C wrapper (spec §20.4). A separate shared library next to the
/// app, loaded at run time; without it the SCORE tab shows text only.
public sealed partial class Verovio : IDisposable
{
    const string Lib = "verovio";

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] private static partial nint vrvToolkit_constructorResourcePath(string resourcePath);
    [LibraryImport(Lib)] private static partial void vrvToolkit_destructor(nint tk);
    [LibraryImport(Lib)] private static partial void enableLogToBuffer([MarshalAs(UnmanagedType.U1)] bool value);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)] private static partial bool vrvToolkit_setOptions(nint tk, string options);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)] private static partial bool vrvToolkit_loadData(nint tk, string data);
    [LibraryImport(Lib)] private static partial int vrvToolkit_getPageCount(nint tk);
    [LibraryImport(Lib)] private static partial nint vrvToolkit_renderToSVG(nint tk, int page, [MarshalAs(UnmanagedType.U1)] bool xmlDeclaration);
    [LibraryImport(Lib)] private static partial nint vrvToolkit_getLog(nint tk);
    [LibraryImport(Lib)] private static partial nint vrvToolkit_getVersion(nint tk);

    nint _tk;

    Verovio(nint tk) { _tk = tk; }

    /// Returns null (and why) when the library or its font data is missing.
    public static Verovio? TryCreate(out string error)
    {
        error = "";
        string data = Path.Combine(AppContext.BaseDirectory, "verovio-data");
        if (!Directory.Exists(data)) { error = "Verovio data not found (build the core with DZ_WITH_VEROVIO=ON)"; return null; }
        try
        {
            enableLogToBuffer(true);
            nint tk = vrvToolkit_constructorResourcePath(data);
            if (tk == 0) { error = "Verovio could not start"; return null; }
            return new Verovio(tk);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            error = "Verovio library not found (build the core with DZ_WITH_VEROVIO=ON)";
            return null;
        }
    }

    public string Version => Marshal.PtrToStringUTF8(vrvToolkit_getVersion(_tk)) ?? "";
    /// Import warnings and errors of the last load (strings are owned by the toolkit).
    public string Log => Marshal.PtrToStringUTF8(vrvToolkit_getLog(_tk)) ?? "";

    /// MusicXML -> one SVG per page, laid out for widthPx at the given scale (percent).
    public string[] Render(string musicXml, int widthPx, int scale = 40)
    {
        vrvToolkit_setOptions(_tk, $$"""
            {"inputFrom": "musicxml", "scale": {{scale}}, "pageWidth": {{widthPx * 100 / scale}}, "adjustPageHeight": true,
             "pageMarginLeft": 50, "pageMarginRight": 50, "pageMarginTop": 50, "pageMarginBottom": 50,
             "header": "none", "footer": "none", "breaks": "auto"}
            """);
        if (!vrvToolkit_loadData(_tk, musicXml)) return [];
        int pages = vrvToolkit_getPageCount(_tk);
        var svgs = new string[pages];
        for (int p = 0; p < pages; p++) svgs[p] = Marshal.PtrToStringUTF8(vrvToolkit_renderToSVG(_tk, p + 1, false)) ?? "";
        return svgs;
    }

    public void Dispose()
    {
        if (_tk != 0) vrvToolkit_destructor(_tk);
        _tk = 0;
    }
}
