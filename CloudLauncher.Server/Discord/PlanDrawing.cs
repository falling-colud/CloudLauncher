using System.Globalization;
using System.Text;
using SkiaSharp;

namespace CloudLauncher.Server.Discord;

/// <summary>
/// The launcher's Slate dark palette (<c>ThemeService.ApplySlatePalette</c>) with the Rust accent the
/// website uses, as ARGB.
/// </summary>
internal static class PlanColours
{
    public const uint Canvas = 0xFF191918;        // Surface1: the board behind the cards
    public const uint Surface0 = 0xFF161615;      // sections, a recessed well
    public const uint Surface2 = 0xFF1D1D1C;      // cards
    public const uint Surface3 = 0xFF222221;      // chips, an unticked box
    public const uint Header = 0xFF141413;        // the picture's title strip
    public const uint Border = 0xFF33332F;
    public const uint BorderStrong = 0xFF45443F;
    public const uint Text = 0xFFECEAE4;
    public const uint Text2 = 0xFFA19F97;
    public const uint Text3 = 0xFF86847D;
    public const uint Accent = 0xFF601B00;
    public const uint AccentText = 0xFFA07868;
    public const uint OnAccent = 0xFFF5F4EF;
    public const uint Edge = 0xAA6B7688;          // ModPlanView.EdgeBrush
    public const uint Arrow = 0xE6939EB2;         // ModPlanView.ArrowBrush
    public const uint GridInk = 0xFF8A94A6;

    /// <summary>A card's <c>#RRGGBB</c> (or <c>#AARRGGBB</c>) colour, or null when it has none or it
    /// doesn't parse.</summary>
    public static uint? Parse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var s = hex.Trim().TrimStart('#');
        if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return null;
        return s.Length switch
        {
            6 => 0xFF000000 | v,
            8 => v,
            _ => null
        };
    }

    public static uint WithAlpha(uint c, double alpha) =>
        ((uint)Math.Clamp(Math.Round(((c >> 24) & 0xFF) * alpha), 0, 255) << 24) | (c & 0x00FFFFFF);

    public static string Css(uint c) =>
        (c >> 24) == 0xFF
            ? $"#{c & 0xFFFFFF:x6}"
            : $"rgba({(c >> 16) & 0xFF},{(c >> 8) & 0xFF},{c & 0xFF},{((c >> 24) & 0xFF) / 255.0:0.###})";
}

internal enum PlanFace { Regular, Italic, SemiBold, Pixel }

/// <summary>A font at a size. Line height follows WPF's default for the launcher's UI font.</summary>
internal readonly record struct PlanStyle(PlanFace Face, float Size)
{
    public float LineHeight => MathF.Round(Size * 1.33f);

    public static PlanStyle Regular(float size) => new(PlanFace.Regular, size);
    public static PlanStyle Italic(float size) => new(PlanFace.Italic, size);
    public static PlanStyle Semi(float size) => new(PlanFace.SemiBold, size);
    public static PlanStyle Pixel(float size) => new(PlanFace.Pixel, size);
}

/// <summary>The typefaces, loaded once from the assembly (Inter, and Pixeloid Sans for titles).</summary>
internal static class PlanFonts
{
    private static readonly Lazy<IReadOnlyDictionary<PlanFace, SKTypeface>> Faces = new(Load);

    public static SKTypeface Face(PlanFace face) => Faces.Value[face];

    /// <summary>The SVG side's names for the same faces (the viewer page loads them).</summary>
    public static (string Family, int Weight, bool Italic) Css(PlanFace face) => face switch
    {
        PlanFace.SemiBold => ("Inter", 600, false),
        PlanFace.Italic => ("Inter", 400, true),
        PlanFace.Pixel => ("Pixeloid Sans", 700, false),
        _ => ("Inter", 400, false)
    };

    private static IReadOnlyDictionary<PlanFace, SKTypeface> Load()
    {
        var asm = typeof(PlanFonts).Assembly;
        SKTypeface Read(string name)
        {
            using var stream = asm.GetManifestResourceStream("fonts/" + name)
                ?? throw new InvalidOperationException($"Font resource fonts/{name} is missing");
            using var data = SKData.Create(stream);
            return SKTypeface.FromData(data) ?? throw new InvalidOperationException($"Font {name} did not load");
        }
        return new Dictionary<PlanFace, SKTypeface>
        {
            [PlanFace.Regular] = Read("Inter-Regular.ttf"),
            [PlanFace.Italic] = Read("Inter-Italic.ttf"),
            [PlanFace.SemiBold] = Read("Inter-SemiBold.ttf"),
            [PlanFace.Pixel] = Read("PixeloidSans-Bold.ttf")
        };
    }
}

/// <summary>Measuring and wrapping text the way the board's TextBlocks do. One per drawing: SKFont
/// isn't meant to be shared across threads.</summary>
internal sealed class PlanText : IDisposable
{
    private readonly Dictionary<PlanStyle, SKFont> _fonts = new();

    public SKFont Font(PlanStyle style)
    {
        if (_fonts.TryGetValue(style, out var font)) return font;
        font = new SKFont(PlanFonts.Face(style.Face), style.Size)
        {
            Subpixel = true,
            Edging = SKFontEdging.Antialias,
            Hinting = SKFontHinting.Slight
        };
        return _fonts[style] = font;
    }

    public float Measure(string text, PlanStyle style) => text.Length == 0 ? 0 : Font(style).MeasureText(text);

    /// <summary>Where the baseline sits in a line box of the style's line height.</summary>
    public float BaselineOffset(PlanStyle style)
    {
        var m = Font(style).Metrics;
        var ascent = -m.Ascent;
        var height = ascent + m.Descent;
        return (style.LineHeight - height) / 2 + ascent;
    }

    /// <summary>One line, cut with an ellipsis if it doesn't fit (WPF's CharacterEllipsis).</summary>
    public string Ellipsize(string text, PlanStyle style, float maxWidth)
    {
        if (Measure(text, style) <= maxWidth) return text;
        const string dots = "…";
        var lo = 0;
        var hi = text.Length;
        // The longest prefix that still fits with the ellipsis after it.
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (Measure(text[..mid].TrimEnd() + dots, style) <= maxWidth) lo = mid;
            else hi = mid - 1;
        }
        return lo == 0 ? dots : text[..lo].TrimEnd() + dots;
    }

    /// <summary>Wraps text to a width, at most <paramref name="maxLines"/> lines, the last one cut
    /// with an ellipsis when there's more. Explicit line breaks are kept.</summary>
    public List<string> Wrap(string text, PlanStyle style, float maxWidth, int maxLines = int.MaxValue)
    {
        var lines = new List<string>();
        maxWidth = Math.Max(8, maxWidth);
        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            var current = "";
            foreach (var word in paragraph.Split(' '))
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (Measure(candidate, style) <= maxWidth) { current = candidate; continue; }
                if (current.Length > 0) lines.Add(current);
                current = word;
                // A word longer than the line breaks where it must.
                while (current.Length > 1 && Measure(current, style) > maxWidth)
                {
                    var fit = FitCount(current, style, maxWidth);
                    lines.Add(current[..fit]);
                    current = current[fit..];
                }
            }
            lines.Add(current);
            if (lines.Count > maxLines) break;
        }

        if (lines.Count <= maxLines) return lines;
        var kept = lines.Take(maxLines).ToList();
        kept[^1] = Ellipsize(kept[^1].TrimEnd() + "…", style, maxWidth);
        return kept;
    }

    private int FitCount(string s, PlanStyle style, float maxWidth)
    {
        var lo = 1;
        var hi = s.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (Measure(s[..mid], style) <= maxWidth) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    public void Dispose()
    {
        foreach (var f in _fonts.Values) f.Dispose();
        _fonts.Clear();
    }
}

/// <summary>A path the two painters can both draw: Skia for the picture, SVG for the viewer.</summary>
internal sealed class PlanPath
{
    private readonly List<(char Cmd, float[] P)> _parts = new();

    public PlanPath MoveTo(float x, float y) { _parts.Add(('M', [x, y])); return this; }
    public PlanPath LineTo(float x, float y) { _parts.Add(('L', [x, y])); return this; }
    public PlanPath QuadTo(float x1, float y1, float x, float y) { _parts.Add(('Q', [x1, y1, x, y])); return this; }
    public PlanPath CubicTo(float x1, float y1, float x2, float y2, float x, float y) { _parts.Add(('C', [x1, y1, x2, y2, x, y])); return this; }
    public PlanPath Close() { _parts.Add(('Z', [])); return this; }

    public SKPath ToSkia()
    {
        var p = new SKPath();
        foreach (var (cmd, a) in _parts)
        {
            switch (cmd)
            {
                case 'M': p.MoveTo(a[0], a[1]); break;
                case 'L': p.LineTo(a[0], a[1]); break;
                case 'Q': p.QuadTo(a[0], a[1], a[2], a[3]); break;
                case 'C': p.CubicTo(a[0], a[1], a[2], a[3], a[4], a[5]); break;
                case 'Z': p.Close(); break;
            }
        }
        return p;
    }

    public string ToSvg()
    {
        var sb = new StringBuilder();
        foreach (var (cmd, a) in _parts)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(cmd);
            foreach (var v in a) sb.Append(' ').Append(v.ToString("0.##", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>A loose box around the points, control points included (enough for the picture's
    /// margins).</summary>
    public SKRect Bounds()
    {
        float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
        foreach (var (_, a) in _parts)
            for (var i = 0; i + 1 < a.Length; i += 2)
            {
                l = Math.Min(l, a[i]); r = Math.Max(r, a[i]);
                t = Math.Min(t, a[i + 1]); b = Math.Max(b, a[i + 1]);
            }
        return l > r ? SKRect.Empty : new SKRect(l, t, r, b);
    }
}

/// <summary>One step of drawing a board, in board coordinates.</summary>
internal abstract record PlanOp;

internal sealed record RectOp(SKRect Rect, float Radius, uint? Fill, uint? Stroke, float StrokeWidth = 1,
    float Opacity = 1, float[]? Dash = null) : PlanOp;

/// <summary>A line of text at its baseline. <paramref name="Width"/> is what it measured, for the
/// strike-through and so the SVG can't run wider than the picture.</summary>
internal sealed record TextOp(float X, float Baseline, string Text, PlanStyle Style, uint Colour, float Width,
    bool Strike = false) : PlanOp;

internal sealed record StrokeOp(PlanPath Path, uint Colour, float Width, float[]? Dash = null) : PlanOp;

internal sealed record FillOp(PlanPath Path, uint Colour) : PlanOp;

internal sealed record CircleOp(float Cx, float Cy, float R, uint Fill) : PlanOp;

/// <summary>A store icon (mod or placeholder card), drawn only when it could be fetched.</summary>
internal sealed record ImageOp(SKRect Rect, string Url, float Radius) : PlanOp;

/// <summary>Everything until the matching <see cref="UnclipOp"/> stays inside the rounded rect.</summary>
internal sealed record ClipOp(SKRect Rect, float Radius) : PlanOp;

internal sealed record UnclipOp : PlanOp;
