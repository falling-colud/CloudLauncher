using System.Globalization;
using System.Net;
using System.Text;
using SkiaSharp;

namespace CloudLauncher.Server.Discord;

/// <summary>What the picture of a board says above the board itself.</summary>
/// <param name="Title">The board's name.</param>
/// <param name="Subtitle">Pack name, card count and when it changed.</param>
internal sealed record PlanHeader(string Title, string Subtitle);

/// <summary>
/// Paints a laid-out board (<see cref="PlanBoardLayout"/>): as a PNG for Discord, or as SVG for the
/// live viewer page, from the same drawing steps so both look the same.
/// </summary>
internal static class PlanPainters
{
    private const float Margin = 40;
    private const float HeaderH = 64;
    private const float GridCell = 24;
    private const int GridMajorEvery = 4;

    /// <summary>The longest side of a picture, in pixels. Discord shows it scaled down in the channel
    /// and full size when clicked.</summary>
    private const float MaxSide = 4096;
    private const float MaxPixels = 14_000_000;

    /// <summary>A PNG of the board with a title strip on top, at twice the launcher's size when it fits.</summary>
    public static byte[] Png(IReadOnlyList<PlanOp> ops, SKRect bounds, PlanHeader header,
        IReadOnlyDictionary<string, SKImage> icons)
    {
        var board = BoardArea(bounds);
        var widthPt = Math.Max(board.Width, 560);
        var heightPt = board.Height + HeaderH;
        var scale = 2f;
        scale = Math.Min(scale, MaxSide / Math.Max(widthPt, heightPt));
        scale = Math.Min(scale, MathF.Sqrt(MaxPixels / (widthPt * heightPt)));
        scale = Math.Max(scale, 0.35f);

        var w = (int)Math.Ceiling(widthPt * scale);
        var h = (int)Math.Ceiling(heightPt * scale);
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(PlanColours.Canvas));
        canvas.Scale(scale);

        using var text = new PlanText();
        DrawHeader(canvas, text, header, widthPt);

        canvas.Save();
        canvas.Translate(-board.Left + (widthPt - board.Width) / 2, HeaderH - board.Top);
        DrawGrid(canvas, board);
        Paint(canvas, text, ops, icons);
        canvas.Restore();

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>The board as an SVG document, sized in board units with its own view box. Text uses
    /// the fonts the viewer page loads (Inter, Pixeloid Sans).</summary>
    public static string Svg(IReadOnlyList<PlanOp> ops, SKRect bounds, IReadOnlyDictionary<string, string> iconData)
    {
        var board = BoardArea(bounds);
        var sb = new StringBuilder(64 * 1024);
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"")
          .Append(F(board.Left)).Append(' ').Append(F(board.Top)).Append(' ')
          .Append(F(board.Width)).Append(' ').Append(F(board.Height))
          .Append("\" width=\"").Append(F(board.Width)).Append("\" height=\"").Append(F(board.Height))
          .Append("\" font-family=\"Inter, 'Segoe UI', sans-serif\" text-rendering=\"geometricPrecision\">");

        var clipId = 0;
        var openGroups = 0;
        foreach (var op in ops)
        {
            switch (op)
            {
                case RectOp r:
                    sb.Append("<rect x=\"").Append(F(r.Rect.Left)).Append("\" y=\"").Append(F(r.Rect.Top))
                      .Append("\" width=\"").Append(F(r.Rect.Width)).Append("\" height=\"").Append(F(r.Rect.Height))
                      .Append("\" rx=\"").Append(F(r.Radius)).Append('"');
                    sb.Append(" fill=\"").Append(r.Fill is { } fill ? PlanColours.Css(fill) : "none").Append('"');
                    if (r.Stroke is { } stroke)
                    {
                        sb.Append(" stroke=\"").Append(PlanColours.Css(stroke)).Append("\" stroke-width=\"")
                          .Append(F(r.StrokeWidth)).Append('"');
                        if (r.Dash is { } dash) sb.Append(" stroke-dasharray=\"").Append(string.Join(' ', dash.Select(F))).Append('"');
                    }
                    if (r.Opacity < 1) sb.Append(" opacity=\"").Append(F(r.Opacity)).Append('"');
                    sb.Append("/>");
                    break;
                case TextOp t:
                {
                    var (family, weight, italic) = PlanFonts.Css(t.Style.Face);
                    sb.Append("<text x=\"").Append(F(t.X)).Append("\" y=\"").Append(F(t.Baseline))
                      .Append("\" font-size=\"").Append(F(t.Style.Size)).Append('"');
                    if (family != "Inter") sb.Append(" font-family=\"").Append(family).Append('"');
                    if (weight != 400) sb.Append(" font-weight=\"").Append(weight).Append('"');
                    if (italic) sb.Append(" font-style=\"italic\"");
                    if (t.Strike) sb.Append(" text-decoration=\"line-through\"");
                    sb.Append(" fill=\"").Append(PlanColours.Css(t.Colour)).Append("\" xml:space=\"preserve\">")
                      .Append(WebUtility.HtmlEncode(t.Text)).Append("</text>");
                    break;
                }
                case StrokeOp s:
                    sb.Append("<path d=\"").Append(s.Path.ToSvg()).Append("\" fill=\"none\" stroke=\"")
                      .Append(PlanColours.Css(s.Colour)).Append("\" stroke-width=\"").Append(F(s.Width))
                      .Append("\" stroke-linecap=\"round\" stroke-linejoin=\"round\"");
                    if (s.Dash is { } d) sb.Append(" stroke-dasharray=\"").Append(string.Join(' ', d.Select(F))).Append('"');
                    sb.Append("/>");
                    break;
                case FillOp f:
                    sb.Append("<path d=\"").Append(f.Path.ToSvg()).Append("\" fill=\"").Append(PlanColours.Css(f.Colour))
                      .Append("\"/>");
                    break;
                case CircleOp c:
                    sb.Append("<circle cx=\"").Append(F(c.Cx)).Append("\" cy=\"").Append(F(c.Cy)).Append("\" r=\"")
                      .Append(F(c.R)).Append("\" fill=\"").Append(PlanColours.Css(c.Fill)).Append("\"/>");
                    break;
                case ImageOp img when iconData.TryGetValue(img.Url, out var dataUri):
                {
                    var id = "i" + clipId++;
                    sb.Append("<clipPath id=\"").Append(id).Append("\"><rect x=\"").Append(F(img.Rect.Left))
                      .Append("\" y=\"").Append(F(img.Rect.Top)).Append("\" width=\"").Append(F(img.Rect.Width))
                      .Append("\" height=\"").Append(F(img.Rect.Height)).Append("\" rx=\"").Append(F(img.Radius))
                      .Append("\"/></clipPath><image href=\"").Append(dataUri).Append("\" x=\"").Append(F(img.Rect.Left))
                      .Append("\" y=\"").Append(F(img.Rect.Top)).Append("\" width=\"").Append(F(img.Rect.Width))
                      .Append("\" height=\"").Append(F(img.Rect.Height)).Append("\" clip-path=\"url(#").Append(id)
                      .Append(")\" preserveAspectRatio=\"xMidYMid slice\"/>");
                    break;
                }
                case ClipOp clip:
                {
                    var id = "c" + clipId++;
                    sb.Append("<clipPath id=\"").Append(id).Append("\"><rect x=\"").Append(F(clip.Rect.Left))
                      .Append("\" y=\"").Append(F(clip.Rect.Top)).Append("\" width=\"").Append(F(clip.Rect.Width))
                      .Append("\" height=\"").Append(F(clip.Rect.Height)).Append("\" rx=\"").Append(F(clip.Radius))
                      .Append("\"/></clipPath><g clip-path=\"url(#").Append(id).Append(")\">");
                    openGroups++;
                    break;
                }
                case UnclipOp when openGroups > 0:
                    sb.Append("</g>");
                    openGroups--;
                    break;
            }
        }
        while (openGroups-- > 0) sb.Append("</g>");
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>The board's drawn area plus a margin, never empty.</summary>
    private static SKRect BoardArea(SKRect bounds)
    {
        if (bounds.IsEmpty) bounds = SKRect.Create(0, 0, 480, 240);
        var r = SKRect.Create(bounds.Left - Margin, bounds.Top - Margin, bounds.Width + Margin * 2, bounds.Height + Margin * 2);
        return SKRect.Create(MathF.Floor(r.Left), MathF.Floor(r.Top), MathF.Ceiling(r.Width), MathF.Ceiling(r.Height));
    }

    private static void DrawHeader(SKCanvas canvas, PlanText text, PlanHeader header, float width)
    {
        using var fill = new SKPaint { Color = new SKColor(PlanColours.Header), IsAntialias = true };
        canvas.DrawRect(0, 0, width, HeaderH, fill);
        using var rule = new SKPaint { Color = new SKColor(PlanColours.Border), StrokeWidth = 1, IsAntialias = false };
        canvas.DrawLine(0, HeaderH - 0.5f, width, HeaderH - 0.5f, rule);

        // Pixeloid Sans is drawn on its 9-pixel grid: 18 is two of its pixels per font pixel.
        var titleStyle = PlanStyle.Pixel(18);
        var subStyle = PlanStyle.Regular(12);
        var maxW = width - Margin * 2;
        var title = text.Ellipsize(header.Title, titleStyle, maxW);
        var sub = text.Ellipsize(header.Subtitle, subStyle, maxW);
        DrawText(canvas, text, new TextOp(Margin - 8, 30, title, titleStyle, PlanColours.Text, 0));
        DrawText(canvas, text, new TextOp(Margin - 8, 50, sub, subStyle, PlanColours.Text3, 0));
    }

    private static void DrawGrid(SKCanvas canvas, SKRect area)
    {
        using var minor = new SKPaint { Color = new SKColor(PlanColours.WithAlpha(PlanColours.GridInk, 0.07)), StrokeWidth = 1 };
        using var major = new SKPaint { Color = new SKColor(PlanColours.WithAlpha(PlanColours.GridInk, 0.13)), StrokeWidth = 1 };
        var firstX = (int)Math.Floor(area.Left / GridCell);
        var lastX = (int)Math.Ceiling(area.Right / GridCell);
        for (var k = firstX; k <= lastX; k++)
        {
            var x = k * GridCell + 0.5f;
            canvas.DrawLine(x, area.Top, x, area.Bottom, k % GridMajorEvery == 0 ? major : minor);
        }
        var firstY = (int)Math.Floor(area.Top / GridCell);
        var lastY = (int)Math.Ceiling(area.Bottom / GridCell);
        for (var k = firstY; k <= lastY; k++)
        {
            var y = k * GridCell + 0.5f;
            canvas.DrawLine(area.Left, y, area.Right, y, k % GridMajorEvery == 0 ? major : minor);
        }
    }

    private static void Paint(SKCanvas canvas, PlanText text, IReadOnlyList<PlanOp> ops,
        IReadOnlyDictionary<string, SKImage> icons)
    {
        foreach (var op in ops)
        {
            switch (op)
            {
                case RectOp r:
                {
                    var layer = r.Opacity < 1;
                    using var layerPaint = layer ? new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * r.Opacity)) } : null;
                    if (layer) canvas.SaveLayer(layerPaint);
                    using var rr = new SKRoundRect(r.Rect, r.Radius);
                    if (r.Fill is { } fill)
                    {
                        using var p = new SKPaint { Color = new SKColor(fill), IsAntialias = true, Style = SKPaintStyle.Fill };
                        canvas.DrawRoundRect(rr, p);
                    }
                    if (r.Stroke is { } stroke)
                    {
                        using var p = new SKPaint
                        {
                            Color = new SKColor(stroke), IsAntialias = true, Style = SKPaintStyle.Stroke,
                            StrokeWidth = r.StrokeWidth
                        };
                        if (r.Dash is { } dash) p.PathEffect = SKPathEffect.CreateDash(dash, 0);
                        canvas.DrawRoundRect(rr, p);
                    }
                    if (layer) canvas.Restore();
                    break;
                }
                case TextOp t:
                    DrawText(canvas, text, t);
                    break;
                case StrokeOp s:
                {
                    using var path = s.Path.ToSkia();
                    using var p = new SKPaint
                    {
                        Color = new SKColor(s.Colour), IsAntialias = true, Style = SKPaintStyle.Stroke,
                        StrokeWidth = s.Width, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round
                    };
                    if (s.Dash is { } dash)
                    {
                        p.StrokeCap = SKStrokeCap.Butt;
                        p.PathEffect = SKPathEffect.CreateDash(dash, 0);
                    }
                    canvas.DrawPath(path, p);
                    break;
                }
                case FillOp f:
                {
                    using var path = f.Path.ToSkia();
                    using var p = new SKPaint { Color = new SKColor(f.Colour), IsAntialias = true, Style = SKPaintStyle.Fill };
                    canvas.DrawPath(path, p);
                    break;
                }
                case CircleOp c:
                {
                    using var p = new SKPaint { Color = new SKColor(c.Fill), IsAntialias = true };
                    canvas.DrawCircle(c.Cx, c.Cy, c.R, p);
                    break;
                }
                case ImageOp img when icons.TryGetValue(img.Url, out var image):
                {
                    canvas.Save();
                    using var clip = new SKRoundRect(img.Rect, img.Radius);
                    canvas.ClipRoundRect(clip, antialias: true);
                    using var p = new SKPaint { IsAntialias = true };
                    canvas.DrawImage(image, Cover(image, img.Rect), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), p);
                    canvas.Restore();
                    break;
                }
                case ClipOp clip:
                {
                    canvas.Save();
                    using var rr = new SKRoundRect(clip.Rect, clip.Radius);
                    canvas.ClipRoundRect(rr, antialias: true);
                    break;
                }
                case UnclipOp:
                    canvas.Restore();
                    break;
            }
        }
    }

    private static void DrawText(SKCanvas canvas, PlanText text, TextOp t)
    {
        var font = text.Font(t.Style);
        using var paint = new SKPaint { Color = new SKColor(t.Colour), IsAntialias = true };
        canvas.DrawText(t.Text, t.X, t.Baseline, SKTextAlign.Left, font, paint);
        if (t.Strike)
        {
            var y = t.Baseline - t.Style.Size * 0.3f;
            using var line = new SKPaint { Color = new SKColor(t.Colour), IsAntialias = true, StrokeWidth = Math.Max(1, t.Style.Size / 12) };
            canvas.DrawLine(t.X, y, t.X + t.Width, y, line);
        }
    }

    /// <summary>The destination for an image filling <paramref name="box"/> without stretching
    /// (cropped like CSS <c>object-fit: cover</c>).</summary>
    private static SKRect Cover(SKImage image, SKRect box)
    {
        var s = Math.Max(box.Width / image.Width, box.Height / image.Height);
        var w = image.Width * s;
        var h = image.Height * s;
        return SKRect.Create(box.MidX - w / 2, box.MidY - h / 2, w, h);
    }

    private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
