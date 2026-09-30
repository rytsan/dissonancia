using SkiaSharp;
using Svg.Skia;
using Svg.Skia.TypefaceProviders;

namespace Dissonancia;

/// Verovio SVG -> PNG on white (Svg.Skia). Verovio writes tempo notes and chord accidentals as
/// Leipzig text glyphs embedded as WOFF2, which Skia cannot read: the TTF installed next to the
/// Verovio data serves them.
public static class SvgRaster
{
    static readonly Lazy<FamilyTypeface?> Leipzig = new(() =>
    {
        string ttf = Path.Combine(AppContext.BaseDirectory, "verovio-data", "Leipzig.ttf");
        return File.Exists(ttf) && SKTypeface.FromFile(ttf) is { } tf ? new FamilyTypeface(tf) : null;
    });

    /// scale 2: rendered at 2x for sharp staff lines, shown at 1x.
    public static byte[]? Png(string svg, float scale)
    {
        using var sk = new SKSvg();
        if (Leipzig.Value is { } leipzig)
        {
            sk.Settings.TypefaceProviders ??= [new FontManagerTypefaceProvider(), new DefaultTypefaceProvider()];
            sk.Settings.TypefaceProviders.Insert(0, leipzig);
        }
        if (sk.FromSvg(svg) is not { } picture) return null;
        var r = picture.CullRect;
        using var bitmap = new SKBitmap((int)Math.Ceiling(r.Width * scale), (int)Math.Ceiling(r.Height * scale));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            canvas.Scale(scale);
            canvas.DrawPicture(picture);
        }
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    /// One typeface for its family name whatever the requested style (Leipzig is weight 500;
    /// Svg.Skia's own provider wants an exact style match, and tempo text asks for bold).
    sealed class FamilyTypeface(SKTypeface typeface) : ITypefaceProvider
    {
        public SKTypeface? FromFamilyName(string family, SKFontStyleWeight weight, SKFontStyleWidth width, SKFontStyleSlant slant) =>
            family.Trim('\'', '"', ' ').Equals(typeface.FamilyName, StringComparison.OrdinalIgnoreCase) ? typeface : null;
    }
}
