using System.Windows;
using System.Windows.Media;

namespace CloudLauncher.Services;

/// <summary>
/// The launcher's colours, at runtime.
/// </summary>
/// <remarks>
/// <para>Views reference the brushes in <c>Themes/Palette.xaml</c> with <c>DynamicResource</c>, so
/// replacing an entry in <see cref="Application.Resources"/> repaints everything using it. Brushes
/// can't be changed in place: WPF freezes what it loads from (or puts into) a resource dictionary,
/// and a <c>StaticResource</c> keeps the object it resolved at load time.</para>
/// <para>The user picks an accent and a base surface. Everything else (surface ladder, borders, text
/// tiers, hovers, title bar, scrollbars) is derived by stepping away from the base's luminance, so a
/// light base gives a light theme without a second code path.</para>
/// </remarks>
public static class ThemeService
{
    /// <summary>A named starting point. <c>Accent</c> drives every action colour; <c>Surface</c> is the
    /// deepest background the ladder is built from; the log colours are included so a theme also
    /// changes the log view.</summary>
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

    /// <summary>The Slate style's named accents: the launcher's own Rust (the default) first, then the
    /// mod's own <c>Palette.ACCENTS</c>, in its order.</summary>
    public static readonly (string Name, string Hex)[] SlateAccents =
    [
        ("Rust",       "#601B00"),
        ("Terracotta", "#D9805E"),
        ("Ember",      "#E06C4B"),
        ("Amber",      "#E0A458"),
        ("Moss",       "#7BB36A"),
        ("Mint",       "#5CC8A8"),
        ("Sky",        "#5BA8E0"),
        ("Cobalt",     "#5C7CFA"),
        ("Lavender",   "#9B86E0"),
        ("Orchid",     "#D076C9"),
        ("Rose",       "#E5698A"),
        ("Slate",      "#9AA5B4"),
        ("Bone",       "#D8D2C4"),
    ];

    /// <summary>
    /// Applies the user's whole look (Slate with its options, or Classic with the colours in
    /// <see cref="AppSettings.Theme"/>) to the running application. Safe to call repeatedly; the
    /// Appearance settings call it on every change.
    /// </summary>
    public static void ApplyLook(AppSettings settings)
    {
        try { ApplyLookCore(settings); }
        catch (Exception ex)
        {
            // Colours aren't worth failing startup over. This runs before the launcher's exception handlers
            // are set up, so an exception here would mean a window that never appears.
            AppLog.LogError("theme", ex);
        }
    }

    /// <summary>Classic-only entry kept for the Classic colour pickers: repaints with
    /// <paramref name="theme"/> when Classic is the style in use.</summary>
    public static void Apply(ThemeSettings? theme)
    {
        try
        {
            var app = Application.Current;
            if (app is null) return;
            ApplyClassicPalette(app, theme);
            Changed?.Invoke();
        }
        catch (Exception ex) { AppLog.LogError("theme", ex); }
    }

    private static void ApplyLookCore(AppSettings settings)
    {
        var app = Application.Current;
        if (app is null) return;

        var look = settings.Look ?? new LookSettings();
        Brush shadow;
        if (look.IsSlate)
        {
            var accent = Parse(look.Accent, LookSettings.DefaultAccent);
            if (look.IsVanilla) ApplyVanillaPalette(app, accent);
            else ApplySlatePalette(app, accent);
            shadow = Frozen(Color.FromArgb(look.IsVanilla ? (byte)0x80 : (byte)0x73, 0, 0, 0));
        }
        else
        {
            ApplyClassicPalette(app, settings.Theme);
            shadow = Brushes.Transparent;
        }

        ApplyTypography(app, look);
        ApplyMetrics(app, look);
        // Slate's popups get a hard pixel shadow from SlateBorder instead of a soft blur.
        app.Resources["PopupShadowEffect"] = look.IsSlate ? null : ClassicPopupShadow();
        LookState.Current.Update(look, shadow);

        // Self-drawn shapes do not repaint on a resource change the way brushes do; see SlateBorder.
        foreach (Window window in app.Windows)
            Controls.SlateBorder.RedrawTree(window);

        Changed?.Invoke();
    }

    private static void ApplyClassicPalette(Application app, ThemeSettings? theme)
    {
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

        // The info colour isn't a brand colour, so it follows the accent.
        var info = Readable(accent, surface);
        SetBrush(app, "InfoBrush", info);
        SetBrush(app, "InfoBlueBrush", info);
        SetBrush(app, "AccentTextBrush", info);

        // Tag pills are told apart by hue: shared follows the accent, the rest are fixed (team purple,
        // public green, Modrinth blue). Both tones are recomputed so they stay legible on light backgrounds.
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

        // Role brushes that the Slate skins set themselves; Classic keeps them on the surface ladder.
        SetBrush(app, "ButtonBrush",            step(0.105));
        SetBrush(app, "ButtonHoverBrush",       step(0.190));
        SetBrush(app, "ButtonPressedBrush",     step(0.245));
        SetBrush(app, "ButtonBorderBrush",      step(0.145));
        SetBrush(app, "ButtonBorderHoverBrush", step(0.210));
        SetBrush(app, "InputBrush",             step(0.035));
        SetBrush(app, "InputBorderBrush",       step(0.145));
        SetBrush(app, "PopupBrush",             step(0.105));
        SetBrush(app, "PopupBorderBrush",       step(0.210));
        SetBrush(app, SystemColors.HighlightBrushKey, accent);
        SetBrush(app, SystemColors.HighlightTextBrushKey, Readable(accent));
        SetBrush(app, "SliderThumbBrush", Colors.White);
        SetBrush(app, "SliderThumbBorderBrush", accent);
        SetBrush(app, "CaptionCloseHoverBrush", Hex("#8A1C1C"));

        ApplyLogColors(app, theme, surface, accent, dark);
        ApplyHtmlPalette(surface, accent, dark);
    }

    // ── Slate ──

    /// <summary>
    /// The Slate mod's Dark skin: a warm near-black, text that is never pure white, and one accent used
    /// sparingly (primary buttons, focus, the selected nav item, switches).
    /// </summary>
    /// <remarks>
    /// Values from the mod's <c>Palette.dark</c> (bg #161615, surface #222221, hover #2A2A28, active
    /// #323230, border #33332F, strong #45443F, text #ECEAE4, muted #A19F97, dim #6E6C66, danger #E5484D,
    /// success #5CB176, warning #E0A458), spread over a longer surface ladder because the launcher nests
    /// cards in pages and buttons in cards. The steps stay small so the page still reads as flat.
    /// </remarks>
    private static void ApplySlatePalette(Application app, Color accent)
    {
        var bg = Hex("#161615");
        SetBrush(app, "Surface0Brush",      bg);
        SetBrush(app, "Surface1Brush",      Hex("#191918"));
        SetBrush(app, "Surface2Brush",      Hex("#1D1D1C"));
        SetBrush(app, "Surface3Brush",      Hex("#222221"));
        SetBrush(app, "Surface4Brush",      Hex("#282826"));
        SetBrush(app, "SurfaceHoverBrush",  Hex("#2A2A28"));
        SetBrush(app, "SurfaceActiveBrush", Hex("#323230"));

        SetBrush(app, "BorderSubtleBrush", Hex("#272725"));
        SetBrush(app, "BorderBrush",       Hex("#33332F"));
        SetBrush(app, "BorderStrongBrush", Hex("#45443F"));
        SetBrush(app, "BorderFocusBrush",  accent);

        SetBrush(app, "TextPrimaryBrush",   Hex("#ECEAE4"));
        SetBrush(app, "TextSecondaryBrush", Hex("#A19F97"));
        SetBrush(app, "TextTertiaryBrush",  Hex("#86847D"));
        SetBrush(app, "TextDisabledBrush",  Hex("#6E6C66"));

        ApplySlateAccent(app, accent, bg);

        SetBrush(app, "DangerBrush",      Hex("#E5484D"));
        SetBrush(app, "DangerHoverBrush", Lighten(Hex("#E5484D"), 0.14));
        SetBrush(app, "SuccessBrush",     Hex("#5CB176"));
        SetBrush(app, "WarningBrush",     Hex("#E0A458"));

        Pill(app, "TagShared", accent, bg, true);
        Pill(app, "TagTeam", Hex("#9B86E0"), bg, true);
        Pill(app, "TagPublic", Hex("#5CB176"), bg, true);
        Pill(app, "TagEmpty", Hex("#A19F97"), bg, true);
        Pill(app, "TagModrinth", Hex("#5BA8E0"), bg, true);

        SetBrush(app, "ScrollThumbBrush",      Hex("#3A3935"));
        SetBrush(app, "ScrollThumbHoverBrush", Hex("#57564F"));
        SetBrush(app, "ScrollTrackBrush",      bg);

        // Flat, like the mod's screen header: the bar is the page, set apart by its bottom rule.
        SetBrush(app, "TitleBarBrush", Hex("#141413"));

        SetBrush(app, "ButtonBrush",            Hex("#222221"));
        SetBrush(app, "ButtonHoverBrush",       Hex("#2A2A28"));
        SetBrush(app, "ButtonPressedBrush",     Hex("#1E1E1D"));
        SetBrush(app, "ButtonBorderBrush",      Hex("#33332F"));
        SetBrush(app, "ButtonBorderHoverBrush", Hex("#45443F"));
        SetBrush(app, "InputBrush",             Hex("#121211"));
        SetBrush(app, "InputBorderBrush",       Hex("#33332F"));
        SetBrush(app, "PopupBrush",             Hex("#1D1D1C"));
        SetBrush(app, "PopupBorderBrush",       Hex("#45443F"));
        SetBrush(app, "SliderThumbBrush",       Hex("#ECEAE4"));
        SetBrush(app, "SliderThumbBorderBrush", Hex("#ECEAE4"));
        SetBrush(app, "CaptionCloseHoverBrush", Hex("#C4383D"));

        ApplySlateLogAndHtml(app, accent, bg, Hex("#121211"), Hex("#C9C6BE"), Hex("#7C7A73"));
    }

    /// <summary>
    /// The Slate mod's Vanilla skin: Minecraft's grey stone. Bevelled stone buttons (drawn by
    /// <see cref="Controls.SlateBorder"/>), white text, black outlines that turn white on hover, square
    /// corners, and a dark stone texture behind the page.
    /// </summary>
    private static void ApplyVanillaPalette(Application app, Color accent)
    {
        var bg = Hex("#1E1E1E");
        app.Resources["Surface0Brush"] = StoneTexture(bg, seed: 7);
        app.Resources["Surface1Brush"] = StoneTexture(Hex("#181818"), seed: 11);
        SetBrush(app, "Surface2Brush",      Hex("#262626"));
        SetBrush(app, "Surface3Brush",      Hex("#303030"));
        SetBrush(app, "Surface4Brush",      Hex("#3A3A3A"));
        SetBrush(app, "SurfaceHoverBrush",  Hex("#474747"));
        SetBrush(app, "SurfaceActiveBrush", Hex("#555555"));

        SetBrush(app, "BorderSubtleBrush", Hex("#0E0E0E"));
        SetBrush(app, "BorderBrush",       Hex("#000000"));
        SetBrush(app, "BorderStrongBrush", Hex("#8B8B8B"));
        SetBrush(app, "BorderFocusBrush",  Hex("#FFFFFF"));

        SetBrush(app, "TextPrimaryBrush",   Hex("#FFFFFF"));
        SetBrush(app, "TextSecondaryBrush", Hex("#A0A0A0"));
        SetBrush(app, "TextTertiaryBrush",  Hex("#8A8A8A"));
        SetBrush(app, "TextDisabledBrush",  Hex("#707070"));

        ApplySlateAccent(app, accent, bg);

        SetBrush(app, "DangerBrush",      Hex("#FF5555"));
        SetBrush(app, "DangerHoverBrush", Hex("#FF7777"));
        SetBrush(app, "SuccessBrush",     Hex("#55FF55"));
        SetBrush(app, "WarningBrush",     Hex("#FFAA00"));

        Pill(app, "TagShared", accent, bg, true);
        Pill(app, "TagTeam", Hex("#AA00AA"), bg, true);
        Pill(app, "TagPublic", Hex("#55FF55"), bg, true);
        Pill(app, "TagEmpty", Hex("#A0A0A0"), bg, true);
        Pill(app, "TagModrinth", Hex("#55FFFF"), bg, true);

        SetBrush(app, "ScrollThumbBrush",      Hex("#8B8B8B"));
        SetBrush(app, "ScrollThumbHoverBrush", Hex("#C6C6C6"));
        SetBrush(app, "ScrollTrackBrush",      Hex("#000000"));

        SetBrush(app, "TitleBarBrush", Hex("#101010"));

        // The stone button: vanilla's own greys, with the bevel drawn on top by SlateBorder.
        SetBrush(app, "ButtonBrush",            Hex("#6F6F6F"));
        SetBrush(app, "ButtonHoverBrush",       Hex("#7E7E86"));
        SetBrush(app, "ButtonPressedBrush",     Hex("#5E5E5E"));
        SetBrush(app, "ButtonBorderBrush",      Hex("#000000"));
        SetBrush(app, "ButtonBorderHoverBrush", Hex("#FFFFFF"));
        // Vanilla's text field: black, with a grey frame that goes white when it has focus.
        SetBrush(app, "InputBrush",             Hex("#000000"));
        SetBrush(app, "InputBorderBrush",       Hex("#A0A0A0"));
        // Vanilla's tooltip: near-black with the purple frame.
        SetBrush(app, "PopupBrush",             Color.FromArgb(0xF0, 0x10, 0x00, 0x10));
        SetBrush(app, "PopupBorderBrush",       Hex("#28007F"));
        SetBrush(app, "SliderThumbBrush",       Hex("#C6C6C6"));
        SetBrush(app, "SliderThumbBorderBrush", Hex("#000000"));
        SetBrush(app, "CaptionCloseHoverBrush", Hex("#AA0000"));

        ApplySlateLogAndHtml(app, accent, bg, Hex("#0A0A0A"), Hex("#E0E0E0"), Hex("#808080"));
    }

    /// <summary>The accent and everything derived from it, the way the mod derives them: hover is a
    /// little brighter, and text on an accent fill is dark or light, whichever has more contrast.</summary>
    private static void ApplySlateAccent(Application app, Color accent, Color bg)
    {
        // Contrast ratio rather than a brightness cut-off, which gave mid-tone accents (Ember, Rose,
        // Cobalt) light text at under 3:1.
        var dark = Hex("#141413");
        var light = Hex("#F5F4EF");
        var onAccent = ContrastRatio(accent, dark) >= ContrastRatio(accent, light) ? dark : light;
        SetBrush(app, "TextOnAccentBrush",  onAccent);
        SetBrush(app, "AccentBrush",        accent);
        SetBrush(app, "AccentHoverBrush",   Lighten(accent, 0.12));
        SetBrush(app, "AccentPressedBrush", Darken(accent, 0.12));
        SetBrushWithAlpha(app, "AccentSoftBrush", accent, 0x2E);
        SetBrushWithAlpha(app, "AccentGlowBrush", accent, 0x18);
        SetBrush(app, "PlayBrush",        accent);
        SetBrush(app, "PlayHoverBrush",   Lighten(accent, 0.12));
        SetBrush(app, "PlayPressedBrush", Darken(accent, 0.12));
        var info = Readable(accent, bg);
        SetBrush(app, "InfoBrush", info);
        SetBrush(app, "InfoBlueBrush", info);
        SetBrush(app, "AccentTextBrush", info);
        SetBrush(app, SystemColors.HighlightBrushKey, accent);
        SetBrush(app, SystemColors.HighlightTextBrushKey, onAccent);
    }

    private static void ApplySlateLogAndHtml(Application app, Color accent, Color bg, Color logBg, Color logText, Color logMuted)
    {
        SetBrush(app, "LogBackgroundBrush", logBg);
        SetBrush(app, "LogTextBrush",       logText);
        SetBrush(app, "LogMutedBrush",      logMuted);
        SetBrush(app, "LogAccentBrush",     Readable(accent, logBg));
        SetBrush(app, "LogWarningBrush",    Hex("#E0A458"));
        SetBrush(app, "LogErrorBrush",      Hex("#F07178"));
        SetBrush(app, "LogSuccessBrush",    Hex("#5CB176"));
        SetBrushWithAlpha(app, "LogSelectionBrush", accent, 0x55);

        var link = Readable(accent, bg);
        CloudLauncher.Shared.PackText.HtmlPalette.Current = new CloudLauncher.Shared.PackText.HtmlPalette(
            Background: ToHex(bg),
            Surface: "#1D1D1C",
            SurfaceAlt: "#222221",
            ScrollTrack: ToHex(bg),
            Border: "#33332F",
            BorderStrong: "#45443F",
            Text: "#A19F97",
            TextStrong: "#ECEAE4",
            TextSoft: "#C9C6BE",
            Link: ToHex(link),
            LinkHover: ToHex(Lighten(link, 0.22)));
    }

    /// <summary>The WCAG contrast ratio of two colours, from 1 (the same) to 21 (black on white).</summary>
    public static double ContrastRatio(Color a, Color b)
    {
        static double Linear(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        static double Relative(Color c) => 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);

        var la = Relative(a);
        var lb = Relative(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>
    /// A tiled, nearest-neighbour stone texture for the Vanilla skin's backgrounds. Generated because
    /// Minecraft's own menu texture is Mojang's: 16x16 blocks of slightly varied grey, drawn at two
    /// screen pixels per texture pixel.
    /// </summary>
    private static Brush StoneTexture(Color baseColor, int seed)
    {
        const int size = 16;
        var rng = new Random(seed);
        var pixels = new byte[size * size * 4];
        for (var i = 0; i < size * size; i++)
        {
            var d = rng.Next(-6, 7);
            pixels[i * 4 + 0] = (byte)Math.Clamp(baseColor.B + d, 0, 255);
            pixels[i * 4 + 1] = (byte)Math.Clamp(baseColor.G + d, 0, 255);
            pixels[i * 4 + 2] = (byte)Math.Clamp(baseColor.R + d, 0, 255);
            pixels[i * 4 + 3] = 255;
        }
        var bmp = System.Windows.Media.Imaging.BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        bmp.Freeze();
        var brush = new ImageBrush(bmp)
        {
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, size * 2, size * 2),
            Stretch = Stretch.Fill,
        };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
        brush.Freeze();
        return brush;
    }

    // ── typography ──

    private static readonly Uri FontBase = new("pack://application:,,,/CloudLauncher;component/Assets/Fonts/");

    /// <summary>A pixel font and the heading sizes that put its pixels on whole screen pixels.</summary>
    public sealed record PixelFontSpec(string Family, double H1, double H2, double Brand);

    /// <summary>The pixel fonts on offer, in the order Settings lists them. Pixeloid Sans and Monocraft
    /// are drawn on a 9-pixel em, so 18 and 27 are exact; Pixelify Sans is not on a strict grid.</summary>
    public static readonly IReadOnlyList<PixelFontSpec> PixelFonts =
    [
        new(LookSettings.PixeloidFont, 27, 18, 18),
        new(LookSettings.MonocraftFont, 27, 18, 18),
        new(LookSettings.PixelifyFont, 27.5, 22, 16.5),
    ];

    public static PixelFontSpec PixelFontFor(string? name) =>
        PixelFonts.FirstOrDefault(f => string.Equals(f.Family, name, StringComparison.OrdinalIgnoreCase)) ?? PixelFonts[0];

    /// <summary>The embedded pixel font <paramref name="spec"/>, with <paramref name="fallback"/> behind it.</summary>
    public static FontFamily PixelFamily(PixelFontSpec spec, string fallback) =>
        new(FontBase, "./#" + spec.Family + ", " + fallback);

    /// <summary>
    /// Fonts for the look. Slate headings use the chosen pixel font, and Slate icons come from a pixel
    /// icon font mapped onto the Segoe MDL2 codepoints the launcher already uses. Every family falls
    /// back to the Classic one for characters the pixel font lacks.
    /// </summary>
    /// <remarks>Heading sizes come from <see cref="PixelFonts"/> so a title's pixels land on whole
    /// screen pixels.</remarks>
    private static void ApplyTypography(Application app, LookSettings look)
    {
        const string ui = "Segoe UI Variable Text, Segoe UI, Arial";
        const string display = "Segoe UI Variable Display, Segoe UI, Arial";
        const string mdl2 = "Segoe MDL2 Assets";
        var slate = look.IsSlate;
        var pixel = PixelFontFor(look.PixelFont);

        app.Resources["UiFont"] = slate && look.PixelText
            ? PixelFamily(pixel, ui)
            : new FontFamily(ui);
        app.Resources["HeadingFont"] = slate && look.PixelHeadings
            ? PixelFamily(pixel, display)
            : new FontFamily(display);
        app.Resources["IconFont"] = slate && look.PixelIcons
            ? new FontFamily(FontBase, "./#Slate Icons, " + mdl2)
            : new FontFamily(mdl2);

        var pixelHeadings = slate && look.PixelHeadings;
        app.Resources["BrandFont"] = pixelHeadings
            ? PixelFamily(pixel, ui)
            : new FontFamily(ui);
        app.Resources["BrandFontSize"] = pixelHeadings ? pixel.Brand : 13.0;
        app.Resources["BrandFontWeight"] = pixelHeadings ? FontWeights.Normal : FontWeights.SemiBold;
        // The pixel icons are drawn on a 16-pixel grid: at 16 every icon pixel is a screen pixel.
        app.Resources["NavIconSize"] = slate && look.PixelIcons ? 16.0 : 15.0;
        app.Resources["H1FontSize"] = pixelHeadings ? pixel.H1 : 28.0;
        app.Resources["H2FontSize"] = pixelHeadings ? pixel.H2 : 20.0;
        app.Resources["H3FontSize"] = 14.0;
        app.Resources["HeadingWeight"] = pixelHeadings ? FontWeights.Normal : FontWeights.SemiBold;
    }

    /// <summary>
    /// The shape metrics that differ between looks: the nav rail's highlight (Classic fills the row;
    /// Slate insets a pixel-cornered pill with a 2px rail inside it, like the mod's sidebar) and the
    /// slider knob's ring.
    /// </summary>
    private static void ApplyMetrics(Application app, LookSettings look)
    {
        var slate = look.IsSlate;
        var vanilla = slate && look.IsVanilla;
        app.Resources["NavItemRadius"] = new CornerRadius(slate && !vanilla ? 4 : 0);
        app.Resources["NavItemInset"] = slate ? new Thickness(6, 0, 6, 0) : new Thickness(0);
        app.Resources["NavIndicatorWidth"] = slate ? 2.0 : 3.0;
        app.Resources["NavIndicatorMargin"] = slate ? new Thickness(6, 9, 0, 9) : new Thickness(0, 8, 0, 8);
        app.Resources["SliderThumbBorderThickness"] = new Thickness(vanilla ? 1 : slate ? 0 : 3);
    }

    private static Color Hex(string hex) => Parse(hex, "#FF00FF");

    private static System.Windows.Media.Effects.DropShadowEffect ClassicPopupShadow()
    {
        var e = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Colors.Black, Opacity = 0.5, BlurRadius = 14, ShadowDepth = 2,
        };
        e.Freeze();
        return e;
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static void SetBrush(Application app, object key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        app.Resources[key] = brush;
    }

    /// <summary>Raised after the palette changes, for the handful of places that read a brush once
    /// into a field instead of through a DynamicResource reference.</summary>
    public static event Action? Changed;

    /// <summary>The log view's own palette, kept apart from the chrome so the background stays flat and
    /// the level colours stay legible whatever the accent.</summary>
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
    /// The palette the description web view is painted with (see
    /// <see cref="CloudLauncher.Shared.PackText.HtmlPalette"/>). The web view only follows the theme
    /// through these values.
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

    /// <summary>The level a log line reads as, for colouring it. Kept cheap because it runs over every
    /// line of large log files.</summary>
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

    // ── colour helpers ──

    /// <summary>Puts a new frozen brush in the dictionary under <paramref name="key"/>; DynamicResource
    /// references pick up the change.</summary>
    private static void SetBrush(Application app, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        app.Resources[key] = brush;
    }

    /// <summary>Parses <c>#RGB</c>, <c>#RRGGBB</c> or <c>#AARRGGBB</c>, or null when the value is
    /// missing or malformed (settings.json may have been edited by hand).</summary>
    public static Color? TryParse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try { return (Color)ColorConverter.ConvertFromString(hex.Trim()); }
        catch { return null; }
    }

    /// <summary>As <see cref="TryParse"/>, falling back to a known-good literal.</summary>
    public static Color Parse(string? hex, string fallback) =>
        TryParse(hex) ?? TryParse(fallback) ?? Colors.Magenta;

    /// <summary>Perceived brightness, 0 to 1. Only decides which way to step, so the cheap
    /// coefficients are enough.</summary>
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
