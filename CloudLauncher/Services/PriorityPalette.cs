using System.Windows.Media;

namespace CloudLauncher.Services;

/// <summary>
/// Maps a mod priority band to a stable border color, shared by the List and Graph views so
/// colored borders mean the same thing everywhere. Priority 0 is neutral (no emphasis);
/// positive priorities cycle a distinct, theme-friendly palette; negatives are muted grey.
/// Brushes are frozen and cached for cheap reuse across many item containers.
/// </summary>
public static class PriorityPalette
{
    private static readonly Color[] Positives =
    [
        Color.FromRgb(0x5B, 0x9D, 0xF9), // 1 — blue   (info)
        Color.FromRgb(0x3F, 0xB9, 0x50), // 2 — green  (success)
        Color.FromRgb(0xE3, 0xB3, 0x41), // 3 — amber  (warning)
        Color.FromRgb(0xE8, 0x7D, 0x3E), // 4 — orange
        Color.FromRgb(0xD3, 0x6A, 0x6A), // 5 — red    (shared tag)
        Color.FromRgb(0xCF, 0xAE, 0xFF), // 6 — purple (team tag)
    ];

    private static readonly Color Negative = Color.FromRgb(0x7C, 0x85, 0x97); // tertiary grey
    private static readonly Color Neutral  = Color.FromRgb(0x2E, 0x34, 0x45); // default BorderBrush

    private static readonly Dictionary<int, Brush> _brushCache = new();

    public static Color ColorFor(int priority)
    {
        if (priority == 0) return Neutral;
        if (priority < 0)  return Negative;
        return Positives[(priority - 1) % Positives.Length];
    }

    public static Brush BorderFor(int priority)
    {
        lock (_brushCache)
        {
            if (_brushCache.TryGetValue(priority, out var cached)) return cached;
            var brush = new SolidColorBrush(ColorFor(priority));
            brush.Freeze();
            _brushCache[priority] = brush;
            return brush;
        }
    }

    /// <summary>True when a priority warrants a visible (non-neutral) border.</summary>
    public static bool IsEmphasized(int priority) => priority != 0;

    /// <summary>A short human label for a priority band, e.g. "P3" or "Normal".</summary>
    public static string Label(int priority) => priority == 0 ? "Normal" : $"P{priority}";
}
