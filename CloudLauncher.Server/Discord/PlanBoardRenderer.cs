using SkiaSharp;

namespace CloudLauncher.Server.Discord;

/// <summary>
/// A board laid out once and ready to paint as a PNG (Discord) or an SVG (the viewer page).
/// </summary>
/// <remarks>Layout measures text, so it is done once per board revision and both outputs share it.
/// <see cref="IconUrls"/> are the store icons the cards would like to show; fetch them before
/// painting, and the ones that couldn't be fetched fall back to the card's initial.</remarks>
public sealed class PlanBoardDrawing
{
    private readonly IReadOnlyList<PlanOp> _ops;
    private readonly SKRect _bounds;

    private PlanBoardDrawing(IReadOnlyList<PlanOp> ops, SKRect bounds)
    {
        _ops = ops;
        _bounds = bounds;
        IconUrls = ops.OfType<ImageOp>().Select(i => i.Url).Distinct().ToList();
    }

    public IReadOnlyList<string> IconUrls { get; }

    public static PlanBoardDrawing Layout(PlanBoardData board, IReadOnlyDictionary<string, ModLabel>? mods = null)
    {
        using var text = new PlanText();
        var (ops, bounds) = PlanBoardLayout.Build(board, text, mods ?? new Dictionary<string, ModLabel>());
        return new PlanBoardDrawing(ops, bounds);
    }

    /// <summary>The picture posted to Discord: a title strip, then the board on its grid.</summary>
    public byte[] Png(string title, string subtitle, IReadOnlyDictionary<string, byte[]>? icons = null)
    {
        var decoded = new Dictionary<string, SKImage>();
        try
        {
            if (icons is not null)
                foreach (var (url, bytes) in icons)
                    if (SKImage.FromEncodedData(bytes) is { } image) decoded[url] = image;
            return PlanPainters.Png(_ops, _bounds, new PlanHeader(title, subtitle), decoded);
        }
        finally
        {
            foreach (var image in decoded.Values) image.Dispose();
        }
    }

    /// <summary>The board alone as SVG, for the viewer page to pan and zoom.</summary>
    public string Svg(IReadOnlyDictionary<string, byte[]>? icons = null)
    {
        var data = new Dictionary<string, string>();
        if (icons is not null)
            foreach (var (url, bytes) in icons)
                data[url] = "data:" + SniffImageType(bytes) + ";base64," + Convert.ToBase64String(bytes);
        return PlanPainters.Svg(_ops, _bounds, data);
    }

    private static string SniffImageType(byte[] b) =>
        b.Length > 4 && b[0] == 0x89 && b[1] == 0x50 ? "image/png"
        : b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 ? "image/jpeg"
        : b.Length > 12 && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P' ? "image/webp"
        : b.Length > 3 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' ? "image/gif"
        : "application/octet-stream";
}
