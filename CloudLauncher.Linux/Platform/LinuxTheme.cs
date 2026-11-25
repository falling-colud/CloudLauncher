using Avalonia;
using Avalonia.Media;
using CloudLauncher.Services;

namespace CloudLauncher;

/// <summary>The user's accent (Settings > Appearance on Windows, the same setting here) over the Slate
/// palette in Themes/Slate.axaml.</summary>
public static class LinuxTheme
{
    public static void Apply(StyledElement root, AppSettings settings)
    {
        var app = Application.Current;
        if (app is null) return;
        var accent = Parse(settings.Look?.Accent) ?? Color.Parse(LookSettings.DefaultAccent);
        var bg = Color.Parse("#161615");

        Set(app, "AccentBrush", accent);
        Set(app, "AccentHoverBrush", Mix(accent, Colors.White, 0.12));
        Set(app, "AccentPressedBrush", Mix(accent, Colors.Black, 0.18));
        Set(app, "AccentSoftBrush", Color.FromArgb(0x40, accent.R, accent.G, accent.B));
        Set(app, "AccentTextBrush", Readable(accent, bg));
        app.Resources["SystemAccentColor"] = Readable(accent, bg);
        app.Resources["SystemAccentColorDark1"] = Mix(Readable(accent, bg), Colors.Black, 0.2);
        app.Resources["SystemAccentColorLight1"] = Mix(Readable(accent, bg), Colors.White, 0.15);
        app.Resources["SystemAccentColorLight2"] = Mix(Readable(accent, bg), Colors.White, 0.3);
        Set(app, "TextControlBorderBrushFocused", Readable(accent, bg));
    }

    private static void Set(Application app, string key, Color color) => app.Resources[key] = new SolidColorBrush(color);

    private static Color? Parse(string? hex) =>
        !string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex.Trim(), out var c) ? c : null;

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    /// <summary>The accent lightened until it reads as text on the background (a dark Rust accent is
    /// a fine button but unreadable as text), the way the Windows launcher's AccentTextBrush is made.</summary>
    public static Color Readable(Color c, Color bg)
    {
        var colour = c;
        for (var i = 0; i < 12 && Contrast(colour, bg) < 4.5; i++) colour = Mix(colour, Colors.White, 0.12);
        return colour;
    }

    private static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }
}
