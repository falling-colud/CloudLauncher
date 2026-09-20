using System.Windows;
using System.Windows.Media;

namespace CloudLauncher.Services;

/// <summary>
/// The launcher's colours, at runtime.
/// </summary>
/// <remarks>
/// <para>Every themeable brush in <c>Themes/Palette.xaml</c> is referenced from the views with
/// <c>DynamicResource</c>, so replacing the entry in <see cref="Application.Resources"/> repaints
/// everything that uses it — including controls already on screen and the styles in
/// <c>Controls.xaml</c>. Mutating the brushes in place looks simpler and does not work: WPF freezes
/// the Freezables it loads from a resource dictionary (and freezes what you put into one), so a
/// recolour throws "in a read-only state", and a plain <c>StaticResource</c> reference keeps the
/// object it resolved at load time whatever the dictionary says afterwards.</para>
/// </remarks>
/// <remarks>
/// The user picks two colours, not thirty: an accent and a base surface. Everything else — the surface
/// ladder, borders, the four text tiers, hovers, the title-bar gradient, scrollbars — is derived from
/// those, which is what keeps a custom theme looking like a theme instead of a paint spill. The
/// derivation reads the base surface's luminance and steps *away* from it, so a light base produces a
/// light theme (lighter surfaces, dark text) without a second code path.
/// </remarks>
public static class ThemeService
{
    /// <summary>A named starting point. <c>Accent</c> drives every action colour; <c>Surface</c> is the
    /// deepest background the ladder is built from; the log colours ride along so a theme changes the
    /// log view with the rest of the app.</summary>
    public sealed record Preset(string Name, string Accent, string Surface, string LogText, string LogAccent);

    public static readonly Preset[] Presets =
    [
        new("Crimson (default)", "#6B1212", "#0B0D11", "#A8B0BF", "#5B9DF9"),
        new("Ocean",             "#1C5F8A", "#090E14", "#9FB4C7", "#4FD1C5"),
        new("Forest",            "#2F6B3A", "#0A0F0B", "#A6BCA8", "#8FD694"),
        new("Violet",            "#5B3A94", "#0D0A14", "#B4A8C7", "#CFAEFF"),
        new("Ember",             "#9A4A12", "#120C08", "#C4B0A0", "#E3B341"),
        new("Slate",             "#455066", "#0D0F13", "#A8B0BF", "#7FC7FF"),
        new("Paper (light)",     "#8A2020", "#F4F5F7", "#4A5060", "#1C5F8A"),
    ];

    public static Preset Default => Presets[0];

    /// <summary>Applies <paramref name="theme"/> to the running application. Safe to call repeatedly —
    /// a picker calls it on every colour change.</summary>
    public static void Apply(ThemeSettings? theme)
    {
        try { ApplyCore(theme); }
        catch (Exception ex)
        {
            // Colours are never worth failing to start over. This runs before the launcher's own
            // exception handlers are in place, so an escape here is a window that never appears.
            AppLog.LogError("theme", ex);
        }
    }

    private static void ApplyCore(ThemeSettings? theme)
    {
        var app = Application.Current;
        if (app is null) return;

        theme ??= new ThemeSettings();
        var accent  = Parse(theme.Accent,  Default.Accent);
        var surface = Parse(theme.Surface, Default.Surface);

        // Which way "away from the background" is: a dark base lightens, a light base darkens.
        var dark = Luminance(surface) < 0.5;
        var contrast = dark ? Colors.White : Colors.Black;

        Color step(double amount) => Mix(surface, contrast, amount);

        SetBrush(app, "Surface0Brush",      surface);
        SetBrush(app, "Surface1Brush",      step(0.035));
        SetBrush(app, "Surface2Brush",      step(0.070));
        SetBrush(app, "Surface3Brush",      step(0.105));
        SetBrush(app, "Surface4Brush",      step(0.150));
        SetBrush(app, "SurfaceHoverBrush",  step(0.190));
        SetBrush(app, "SurfaceActiveBrush", step(0.245));

        SetBrush(app, "BorderSubtleBrush", step(0.100));
        SetBrush(app, "BorderBrush",       step(0.145));
        SetBrush(app, "BorderStrongBrush", step(0.210));
        SetBrush(app, "BorderFocusBrush",  accent);

        // Text steps back toward the background rather than to grey, so secondary text stays in the
        // same colour family as the surface it sits on.
        var textPrimary = Mix(surface, contrast, dark ? 0.92 : 0.86);
        SetBrush(app, "TextPrimaryBrush",   textPrimary);
        SetBrush(app, "TextSecondaryBrush", Mix(surface, contrast, dark ? 0.66 : 0.62));
        SetBrush(app, "TextTertiaryBrush",  Mix(surface, contrast, dark ? 0.50 : 0.48));
        SetBrush(app, "TextDisabledBrush",  Mix(surface, contrast, dark ? 0.33 : 0.34));
        SetBrush(app, "TextOnAccentBrush",  Readable(accent));

        SetBrush(app, "AccentBrush",        accent);
        SetBrush(app, "AccentHoverBrush",   Lighten(accent, 0.16));
        SetBrush(app, "AccentPressedBrush", Darken(accent, 0.18));
        SetBrush(app, "AccentSoftBrush",    WithAlpha(accent, 0x33));
        SetBrush(app, "AccentGlowBrush",    WithAlpha(accent, 0x1A));

        SetBrush(app, "PlayBrush",        accent);
        SetBrush(app, "PlayHoverBrush",   Lighten(accent, 0.16));
        SetBrush(app, "PlayPressedBrush", Darken(accent, 0.18));

        // Danger keeps its own meaning but is pulled toward the accent's temperature so a red accent
        // and a red "delete" don't read as two unrelated reds.
        var danger = Parse(theme.Danger, dark ? "#8A1C1C" : "#B02A2A");
        SetBrush(app, "DangerBrush",      danger);
        SetBrush(app, "DangerHoverBrush", Lighten(danger, 0.14));
        SetBrush(app, "SuccessBrush",     Parse(theme.Success, "#3FB950"));
        SetBrush(app, "WarningBrush",     Parse(theme.Warning, "#E3B341"));

        // "Informational" is the app's own voice, not a brand colour, so it follows the accent.
        var info = Readable(accent, surface);
        SetBrush(app, "InfoBrush", info);
        SetBrush(app, "InfoBlueBrush", info);

        // Tag pills keep their meanings apart by hue — shared follows the accent, the rest are
        // fixed (team purple, public green, Modrinth blue) — but both tones are recomputed so they
        // stay legible instead of washing out on a light background.
        Pill(app, "TagShared", accent, surface, dark);
        Pill(app, "TagTeam", Parse(null, "#A855F7"), surface, dark);
        Pill(app, "TagPublic", Parse(null, "#10B981"), surface, dark);
        Pill(app, "TagEmpty", Mix(surface, contrast, 0.45), surface, dark);
        Pill(app, "TagModrinth", Parse(null, "#18A0FF"), surface, dark);

        SetBrush(app, "ScrollThumbBrush",      step(0.210));
        SetBrush(app, "ScrollThumbHoverBrush", step(0.300));
        SetBrush(app, "ScrollTrackBrush",      step(0.020));

        // Title bar: the surface one step up, warmed toward the accent on the right-hand side.
        var titleBar = new LinearGradientBrush(
            step(0.045), Mix(step(0.045), accent, 0.12), new Point(0, 0), new Point(1, 0));
        titleBar.Freeze();
        app.Resources["TitleBarBrush"] = titleBar;

        ApplyLogColors(app, theme, surface, accent, dark);
        ApplyHtmlPalette(surface, accent, dark);
        Changed?.Invoke();
    }

    /// <summary>Raised after the palette changes, for the handful of places that read a brush once
    /// into a field instead of through a DynamicResource reference.</summary>
    public static event Action? Changed;

    /// <summary>The log view's own palette. Kept separate from the chrome because a log is read, not
    /// navigated: the background wants to be flatter and the level colours want to stay legible even
    /// when someone picks a lurid accent.</summary>
    private static void ApplyLogColors(Application app, ThemeSettings theme, Color surface, Color accent, bool dark)
    {
        var logBg   = TryParse(theme.LogBackground) ?? Mix(surface, dark ? Colors.Black : Colors.White, 0.35);
        var logDark = Luminance(logBg) < 0.5;
        var logContrast = logDark ? Colors.White : Colors.Black;

        SetBrush(app, "LogBackgroundBrush", logBg);
        SetBrush(app, "LogTextBrush",       TryParse(theme.LogText) ?? Mix(logBg, logContrast, logDark ? 0.72 : 0.78));
        SetBrush(app, "LogMutedBrush",      TryParse(theme.LogMuted) ?? Mix(logBg, logContrast, 0.42));
        SetBrush(app, "LogAccentBrush",     TryParse(theme.LogAccent) ?? accent);
        SetBrush(app, "LogWarningBrush",    TryParse(theme.LogWarning) ?? Parse(theme.Warning, "#E3B341"));
        SetBrush(app, "LogErrorBrush",      TryParse(theme.LogError) ?? Parse(null, logDark ? "#FF6B6B" : "#B02A2A"));
        SetBrush(app, "LogSuccessBrush",    Parse(theme.Success, "#3FB950"));
        SetBrush(app, "LogSelectionBrush",  WithAlpha(accent, 0x55));
    }

    /// <summary>
    /// The palette the description web view is painted with — see
    /// <see cref="CloudLauncher.Shared.PackText.HtmlPalette"/>. A rendered description is a web
    /// document, so it follows the theme only because we hand it these.
    /// </summary>
    private static void ApplyHtmlPalette(Color surface, Color accent, bool dark)
    {
        var contrast = dark ? Colors.White : Colors.Black;
        Color step(double amount) => Mix(surface, contrast, amount);
        var link = Readable(accent, surface);

        CloudLauncher.Shared.PackText.HtmlPalette.Current = new CloudLauncher.Shared.PackText.HtmlPalette(
            Background: ToHex(surface),
            Surface: ToHex(step(0.035)),
            SurfaceAlt: ToHex(step(0.070)),
            ScrollTrack: ToHex(step(0.020)),
            Border: ToHex(step(0.145)),
            BorderStrong: ToHex(step(0.210)),
            Text: ToHex(Mix(surface, contrast, dark ? 0.66 : 0.62)),
            TextStrong: ToHex(Mix(surface, contrast, dark ? 0.92 : 0.86)),
            TextSoft: ToHex(Mix(surface, contrast, dark ? 0.78 : 0.72)),
            Link: ToHex(link),
            LinkHover: ToHex(dark ? Lighten(link, 0.22) : Darken(link, 0.18)));
    }

    /// <summary>A pill's background and foreground for one hue: a translucent wash of it behind text
    /// pushed far enough from the surface to read.</summary>
    private static void Pill(Application app, string prefix, Color hue, Color surface, bool dark)
    {
        SetBrushWithAlpha(app, prefix + "BgBrush", hue, dark ? (byte)0x33 : (byte)0x26);
        SetBrush(app, prefix + "FgBrush", Readable(hue, surface));
    }

    /// <summary>The nearest version of <paramref name="colour"/> that can be read on
    /// <paramref name="background"/>: an accent dark enough to sit behind white text is not
    /// necessarily light enough to be text itself.</summary>
    public static Color Readable(Color colour, Color background)
    {
        var target = Luminance(background) < 0.5 ? Colors.White : Colors.Black;
        var colour2 = colour;
        // Walk toward white (on dark) or black (on light) until there is a real gap in brightness.
        for (var i = 0; i < 8 && Math.Abs(Luminance(colour2) - Luminance(background)) < 0.34; i++)
            colour2 = Mix(colour2, target, 0.16);
        return colour2;
    }

    private static void SetBrushWithAlpha(Application app, string key, Color colour, byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, colour.R, colour.G, colour.B));
        brush.Freeze();
        app.Resources[key] = brush;
    }

    /// <summary>The level a log line reads as, used to colour it. Deliberately cheap: this runs over
    /// every line of a multi-megabyte log file.</summary>
    public static LogLevel LevelOf(string line)
    {
        if (line.Length == 0) return LogLevel.Normal;
        if (Contains(line, "ERROR") || Contains(line, "SEVERE") || Contains(line, "FATAL")
            || Contains(line, "Exception") || line.StartsWith("\tat ", StringComparison.Ordinal)
            || Contains(line, "Caused by:"))
            return LogLevel.Error;
        if (Contains(line, "WARN")) return LogLevel.Warning;
        if (Contains(line, "DEBUG") || Contains(line, "TRACE")) return LogLevel.Muted;
        return LogLevel.Normal;
    }

    private static bool Contains(string line, string token) =>
        line.Contains(token, StringComparison.OrdinalIgnoreCase);

    public enum LogLevel { Normal, Muted, Warning, Error }

    // ── colour helpers ───────────────────────────────────────────────────────

    /// <summary>Puts a new brush in the dictionary under <paramref name="key"/>. Frozen, because it
    /// is never mutated again and a frozen brush is cheaper to render and safe across threads;
    /// DynamicResource is what carries the change to the UI.</summary>
    private static void SetBrush(Application app, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        app.Resources[key] = brush;
    }

    /// <summary>Parses <c>#RGB</c>, <c>#RRGGBB</c> or <c>#AARRGGBB</c>, or null when the value is
    /// missing or malformed — a stored colour from a hand-edited settings.json must not take the
    /// launcher down with it.</summary>
    public static Color? TryParse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try { return (Color)ColorConverter.ConvertFromString(hex.Trim()); }
        catch { return null; }
    }

    /// <summary>As <see cref="TryParse"/>, falling back to a known-good literal.</summary>
    public static Color Parse(string? hex, string fallback) =>
        TryParse(hex) ?? TryParse(fallback) ?? Colors.Magenta;

    /// <summary>Perceived brightness, 0–1. Used only to decide which way to step, so the cheap
    /// coefficients are plenty.</summary>
    public static double Luminance(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

    /// <summary>Black or white, whichever stays readable on <paramref name="background"/>.</summary>
    public static Color Readable(Color background) =>
        Luminance(background) > 0.6 ? Color.FromRgb(0x10, 0x12, 0x18) : Colors.White;

    public static Color Mix(Color a, Color b, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            255,
            (byte)Math.Round(a.R + (b.R - a.R) * amount),
            (byte)Math.Round(a.G + (b.G - a.G) * amount),
            (byte)Math.Round(a.B + (b.B - a.B) * amount));
    }

    public static Color Lighten(Color c, double amount) => Mix(c, Colors.White, amount);
    public static Color Darken(Color c, double amount) => Mix(c, Colors.Black, amount);

    private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
