using System.Windows.Media;

namespace CloudLauncher.Views;

/// <summary>
/// Shared accent choices for user-coloured things: planning cards, links and category swatches.
/// Hues match <see cref="Services.PriorityPalette"/>.
/// </summary>
public static class AccentPalette
{
    public static readonly (string Name, string Hex)[] Colors =
    [
        ("Blue",   "#5B9DF9"),
        ("Green",  "#3FB950"),
        ("Amber",  "#E3B341"),
        ("Orange", "#E87D3E"),
        ("Red",    "#D36A6A"),
        ("Purple", "#CFAEFF"),
        ("Teal",   "#3EC8C1"),
        ("Slate",  "#6B7688")
    ];

    /// <summary>Parses a <c>#RRGGBB</c> into a frozen brush, falling back when it's null or malformed.</summary>
    public static Brush Brush(string? hex, Brush fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
        catch { return fallback; }
    }
}
