using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Dissonancia;

/// Gas-plasma display look (orange neon dot matrix): 5x7 cells with the unlit dots visible,
/// each lit dot drawn as a glow halo + core + hot centre, over a fine pixel grid.
static class Plasma
{
    static IBrush B(uint argb) => new ImmutableSolidColorBrush(Color.FromUInt32(argb));
    static IPen P(uint argb, double w) => new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(argb)), w, lineCap: PenLineCap.Round);

    public static readonly IBrush Bg = new ImmutableLinearGradientBrush(
        [new ImmutableGradientStop(0, Color.FromUInt32(0xFF140604)), new ImmutableGradientStop(1, Color.FromUInt32(0xFF0A0302))],
        startPoint: new RelativePoint(0, 0, RelativeUnit.Relative), endPoint: new RelativePoint(0, 1, RelativeUnit.Relative));
    public static readonly IPen Bezel = P(0xFF050202, 3);
    public static readonly IPen GridLine = P(0x33000000, 1);
    public static readonly IBrush Lit = B(0xFFFF7A2E);
    public static readonly IBrush Hot = B(0xFFFFD2A6);
    public static readonly IBrush Halo = B(0x38FF5A1A);
    public static readonly IBrush HaloWide = B(0x14FF5A1A);
    public static readonly IBrush Ghost = B(0xFF2A0E07);
    public static readonly IBrush Dim = B(0xFFA8461C);
    public static readonly IBrush Faint = B(0xFF5A2410);
    public static readonly IPen Line = P(0xFFFF7A2E, 1.3);
    public static readonly IPen LineGlow = P(0x30FF5A1A, 4);
    public static readonly IPen LineDim = P(0xFF4A1C0C, 1);
    public static readonly IPen TextGlow = P(0x2EFF5A1A, 3.2);
    public static readonly IPen TextGlowWide = P(0x14FF5A1A, 6);

    // 5x7 font (rows top to bottom, bit 4 = leftmost column). Chord symbols and note names only.
    static readonly Dictionary<char, byte[]> Font = new()
    {
        ['0'] = [0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110],
        ['1'] = [0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110],
        ['2'] = [0b01110, 0b10001, 0b00001, 0b00010, 0b00100, 0b01000, 0b11111],
        ['3'] = [0b11111, 0b00010, 0b00100, 0b00010, 0b00001, 0b10001, 0b01110],
        ['4'] = [0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010],
        ['5'] = [0b11111, 0b10000, 0b11110, 0b00001, 0b00001, 0b10001, 0b01110],
        ['6'] = [0b00110, 0b01000, 0b10000, 0b11110, 0b10001, 0b10001, 0b01110],
        ['7'] = [0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000],
        ['8'] = [0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110],
        ['9'] = [0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00010, 0b01100],
        ['A'] = [0b01110, 0b10001, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001],
        ['B'] = [0b11110, 0b10001, 0b10001, 0b11110, 0b10001, 0b10001, 0b11110],
        ['C'] = [0b01110, 0b10001, 0b10000, 0b10000, 0b10000, 0b10001, 0b01110],
        ['D'] = [0b11100, 0b10010, 0b10001, 0b10001, 0b10001, 0b10010, 0b11100],
        ['E'] = [0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b11111],
        ['F'] = [0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b10000],
        ['G'] = [0b01110, 0b10001, 0b10000, 0b10111, 0b10001, 0b10001, 0b01111],
        ['a'] = [0b00000, 0b00000, 0b01110, 0b00001, 0b01111, 0b10001, 0b01111],
        ['b'] = [0b10000, 0b10000, 0b10110, 0b11001, 0b10001, 0b10001, 0b11110],
        ['d'] = [0b00001, 0b00001, 0b01101, 0b10011, 0b10001, 0b10001, 0b01111],
        ['g'] = [0b00000, 0b01111, 0b10001, 0b10001, 0b01111, 0b00001, 0b01110],
        ['i'] = [0b00100, 0b00000, 0b01100, 0b00100, 0b00100, 0b00100, 0b01110],
        ['j'] = [0b00010, 0b00000, 0b00110, 0b00010, 0b00010, 0b10010, 0b01100],
        ['m'] = [0b00000, 0b00000, 0b11010, 0b10101, 0b10101, 0b10001, 0b10001],
        ['o'] = [0b00000, 0b00000, 0b01110, 0b10001, 0b10001, 0b10001, 0b01110],
        ['s'] = [0b00000, 0b00000, 0b01110, 0b10000, 0b01110, 0b00001, 0b11110],
        ['u'] = [0b00000, 0b00000, 0b10001, 0b10001, 0b10001, 0b10011, 0b01101],
        ['♯'] = [0b01010, 0b01010, 0b11111, 0b01010, 0b11111, 0b01010, 0b01010],
        ['♭'] = [0b10000, 0b10000, 0b10110, 0b11001, 0b10010, 0b10100, 0b11000],
        ['x'] = [0b00000, 0b10001, 0b01010, 0b00100, 0b01010, 0b10001, 0b00000],   // double sharp
        ['/'] = [0b00000, 0b00001, 0b00010, 0b00100, 0b01000, 0b10000, 0b00000],
        ['+'] = [0b00000, 0b00100, 0b00100, 0b11111, 0b00100, 0b00100, 0b00000],
        ['-'] = [0b00000, 0b00000, 0b00000, 0b11111, 0b00000, 0b00000, 0b00000],
        ['°'] = [0b01100, 0b10010, 0b10010, 0b01100, 0b00000, 0b00000, 0b00000],
        ['ø'] = [0b00000, 0b00001, 0b01110, 0b10011, 0b10101, 0b11001, 0b01110],
        ['('] = [0b00010, 0b00100, 0b01000, 0b01000, 0b01000, 0b00100, 0b00010],
        [')'] = [0b01000, 0b00100, 0b00010, 0b00010, 0b00010, 0b00100, 0b01000],
        ['?'] = [0b01110, 0b10001, 0b00001, 0b00010, 0b00100, 0b00000, 0b00100],
        [' '] = [0, 0, 0, 0, 0, 0, 0],
    };

    /// Maps display text to font cells (𝄫 is two flats; — is a dash; unknown = ?).
    static IEnumerable<char> Cells(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsSurrogatePair(s, i))
            {
                string pair = s.Substring(i, 2);
                i++;
                if (pair == "𝄪") yield return 'x';
                else if (pair == "𝄫") { yield return '♭'; yield return '♭'; }
                else yield return '?';
                continue;
            }
            char c = s[i] switch { '#' => '♯', '—' => '-', '–' => '-', _ => s[i] };
            yield return Font.ContainsKey(c) ? c : '?';
        }
    }

    public static int CellCount(string text) => Cells(text).Count();

    public static double CellWidth(double dot) => dot * 1.3 * 6;   // 5 columns + 1 gap

    /// Draws `text` in `cells` dot-matrix cells starting at (x, y); unused cells show unlit dots.
    /// Returns the width of the text part. `dot` = dot pitch in px.
    public static double DotText(DrawingContext ctx, string text, double x, double y, double dot, int cells = 0, bool dim = false)
    {
        var chars = Cells(text).ToArray();
        int total = Math.Max(cells, chars.Length);
        double pitch = dot * 1.3, r = dot / 2;
        for (int cell = 0; cell < total; cell++)
        {
            byte[] glyph = cell < chars.Length ? Font[chars[cell]] : Font[' '];
            double cx = x + cell * pitch * 6;
            for (int row = 0; row < 7; row++)
                for (int col = 0; col < 5; col++)
                {
                    var c = new Point(cx + col * pitch + r, y + row * pitch + r);
                    if ((glyph[row] >> (4 - col) & 1) == 0) { ctx.DrawEllipse(Ghost, null, c, r * 0.8, r * 0.8); continue; }
                    if (dim) { ctx.DrawEllipse(Dim, null, c, r * 0.85, r * 0.85); continue; }
                    ctx.DrawEllipse(HaloWide, null, c, r * 2.6, r * 2.6);
                    ctx.DrawEllipse(Halo, null, c, r * 1.6, r * 1.6);
                    ctx.DrawEllipse(Lit, null, c, r * 0.95, r * 0.95);
                    ctx.DrawEllipse(Hot, null, c, r * 0.45, r * 0.45);
                }
        }
        return chars.Length * pitch * 6;
    }

    /// Small readouts: font text with a two-pass glow outline, plasma orange.
    public static void Text(DrawingContext ctx, string s, double x, double y, double size, bool bright = true,
        Typeface? tf = null, Ui.Align align = Ui.Align.Left)
    {
        var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf ?? Ui.Mono, size, bright ? Lit : Dim);
        double dx = align switch { Ui.Align.Center => -ft.Width / 2, Ui.Align.Right => -ft.Width, _ => 0 };
        if (bright && ft.BuildGeometry(new Point(x + dx, y)) is { } g)
        {
            ctx.DrawGeometry(null, TextGlowWide, g);
            ctx.DrawGeometry(null, TextGlow, g);
        }
        ctx.DrawText(ft, new Point(x + dx, y));
    }

    public static void Glyph(DrawingContext ctx, string s, double x, double y, double size, Typeface tf)
    {
        var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, size, Lit);
        if (ft.BuildGeometry(new Point(x, y)) is { } g) ctx.DrawGeometry(null, TextGlow, g);
        ctx.DrawText(ft, new Point(x, y));
    }

    public static void GlowLine(DrawingContext ctx, Point a, Point b)
    {
        ctx.DrawLine(LineGlow, a, b);
        ctx.DrawLine(Line, a, b);
    }

    /// Indicator lamp inside the display: lit = glowing orange, off = unlit dot.
    public static void Lamp(DrawingContext ctx, Point c, double r, bool on)
    {
        if (on)
        {
            ctx.DrawEllipse(HaloWide, null, c, r * 2.6, r * 2.6);
            ctx.DrawEllipse(Halo, null, c, r * 1.6, r * 1.6);
        }
        ctx.DrawEllipse(on ? Lit : Ghost, null, c, r, r);
        if (on) ctx.DrawEllipse(Hot, null, c, r * 0.45, r * 0.45);
    }

    /// Recessed dark panel with a fine pixel grid (drawn once per size, then reused).
    public static void Panel(DrawingContext ctx, Rect r, ref StreamGeometry? grid, ref Size gridSize)
    {
        ctx.DrawRectangle(Bg, Bezel, r, 6, 6);
        if (grid is null || gridSize != r.Size)
        {
            grid = new StreamGeometry();
            using (var g = grid.Open())
            {
                for (double y = 2; y < r.Height; y += 3) { g.BeginFigure(new Point(0, y), false); g.LineTo(new Point(r.Width, y)); g.EndFigure(false); }
                for (double x = 2; x < r.Width; x += 3) { g.BeginFigure(new Point(x, 0), false); g.LineTo(new Point(x, r.Height)); g.EndFigure(false); }
            }
            gridSize = r.Size;
        }
        using (ctx.PushTransform(Matrix.CreateTranslation(r.X, r.Y)))
        using (ctx.PushClip(new RoundedRect(new Rect(r.Size), 6)))
            ctx.DrawGeometry(null, GridLine, grid);
    }
}
