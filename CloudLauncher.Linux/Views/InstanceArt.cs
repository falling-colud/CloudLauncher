using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// How the instance pages show an instance the same way everywhere: its cover (the instance's icon, or
/// a colour picked from its name with its first letter on it), its version line and its tags. The
/// Windows launcher's PackRow, for Avalonia, with the same colours so an instance looks the same on
/// both.
/// </summary>
public static class InstanceArt
{
    /// <summary>The Windows launcher's eight cover colours (PackRow.CoverBackground), top-left to
    /// bottom-right.</summary>
    private static readonly (Color From, Color To)[] Palette =
    [
        (Color.FromRgb(0x7A, 0x16, 0x16), Color.FromRgb(0x3E, 0x0A, 0x0A)), // red
        (Color.FromRgb(0x4F, 0x9C, 0xF9), Color.FromRgb(0x1E, 0x4F, 0x8C)), // blue
        (Color.FromRgb(0x10, 0xB9, 0x81), Color.FromRgb(0x0A, 0x6B, 0x4A)), // green
        (Color.FromRgb(0xA8, 0x55, 0xF7), Color.FromRgb(0x5F, 0x2C, 0x9A)), // purple
        (Color.FromRgb(0xE3, 0xB3, 0x41), Color.FromRgb(0x8E, 0x6A, 0x18)), // gold
        (Color.FromRgb(0x14, 0xB8, 0xA6), Color.FromRgb(0x0B, 0x6E, 0x65)), // teal
        (Color.FromRgb(0xEC, 0x4F, 0x88), Color.FromRgb(0x8E, 0x25, 0x4E)), // pink
        (Color.FromRgb(0xF9, 0x73, 0x16), Color.FromRgb(0x96, 0x42, 0x0B)), // orange
    ];

    /// <summary>The cover colour for an instance name: the same hash as the Windows launcher, so
    /// "Hearthstone Valley" is gold on both.</summary>
    public static IBrush CoverBrush(string? name)
    {
        var h = 0u;
        foreach (var c in name ?? "") h = h * 31 + char.ToLowerInvariant(c);
        var (from, to) = Palette[h % (uint)Palette.Length];
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(from, 0), new GradientStop(to, 1) }
        };
    }

    /// <summary>The instance's first letter, drawn on a cover without an icon.</summary>
    public static string Initial(string? name)
    {
        var s = name?.TrimStart() ?? "";
        return s.Length > 0 ? s[..1].ToUpperInvariant() : "?";
    }

    /// <summary>The instance's own icon (from its <c>.cloudlauncher</c> folder), or null.</summary>
    public static IImage? Icon(Guid packId)
    {
        try { return App.State.PackAssets.TryLoadIconImage(packId); }
        catch (Exception ex)
        {
            AppLog.Log("instances", $"The icon of {packId} could not be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>"MC 1.21.1 · NeoForge 21.1.215" on a card, "Minecraft 1.21.1 · ..." on the page.</summary>
    public static string VersionLine(bool isEmpty, string? mc, LoaderKind loader, string? loaderVersion, bool full = false)
    {
        if (isEmpty) return full ? "Empty instance (no Minecraft to launch)" : "Empty instance";
        var game = (full ? "Minecraft " : "MC ") + (string.IsNullOrEmpty(mc) ? "?" : mc);
        return loader == LoaderKind.None ? game : $"{game} · {loader} {loaderVersion}".TrimEnd();
    }

    public static string VersionLine(PackSummary p, bool full = false) =>
        VersionLine(p.IsEmpty, p.MinecraftVersion, p.Loader, p.LoaderVersion, full);

    public static string VersionLine(PackDetail p, bool full = false) =>
        VersionLine(p.IsEmpty, p.MinecraftVersion, p.Loader, p.LoaderVersion, full);

    /// <summary>The loader in capitals for the cover's corner badge.</summary>
    public static string LoaderBadge(bool isEmpty, LoaderKind loader) => isEmpty ? "EMPTY" : loader switch
    {
        LoaderKind.Fabric => "FABRIC",
        LoaderKind.Forge => "FORGE",
        LoaderKind.NeoForge => "NEOFORGE",
        _ => "VANILLA"
    };

    /// <summary>Who the instance belongs to: "on this PC" for one made without an account.</summary>
    public static string Owner(Guid packId, string ownerUsername) =>
        App.State.Api.IsLocalPack(packId) ? "On this computer" : "by " + ownerUsername;

    /// <summary>The kind of instance, as the Windows cards tag it.</summary>
    public enum TagKind { Empty, Local, Shared, Team, Public }

    public static TagKind Tag(bool isEmpty, bool isShared, PackVisibility visibility) =>
        isEmpty ? TagKind.Empty
        : !isShared ? TagKind.Local
        : visibility switch
        {
            PackVisibility.Public => TagKind.Public,
            PackVisibility.Team => TagKind.Team,
            _ => TagKind.Shared
        };

    public static string TagText(TagKind tag) => tag switch
    {
        TagKind.Empty => "EMPTY",
        TagKind.Local => "LOCAL",
        TagKind.Public => "PUBLIC",
        TagKind.Team => "TEAM",
        _ => "SHARED"
    };

    /// <summary>The tag's text colour on a dark chip.</summary>
    public static IBrush TagForeground(TagKind tag) => tag switch
    {
        TagKind.Public => Resource("SuccessBrush", "#5CB176"),
        TagKind.Team => Resource("InfoBrush", "#5BA8E0"),
        TagKind.Shared => Resource("AccentTextBrush", "#D9805E"),
        _ => Resource("TextSecondaryBrush", "#A19F97")
    };

    /// <summary>A small capitals chip (the tag on a cover, the tags beside an instance's name).</summary>
    public static Border Chip(string text, IBrush foreground, IBrush? background = null)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            LetterSpacing = 0.6,
            Foreground = foreground,
            VerticalAlignment = VerticalAlignment.Center
        };
        return new Border
        {
            Background = background ?? new SolidColorBrush(Color.FromArgb(0xA6, 0x10, 0x10, 0x0F)),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(7, 2),
            Child = label
        };
    }

    /// <summary>A cover: the icon when the instance has one, else its colour and first letter.</summary>
    /// <param name="letterSize">Font size of the first letter.</param>
    public static Panel Cover(Guid packId, string name, double letterSize, out Image image, out TextBlock letter)
    {
        letter = new TextBlock
        {
            Text = Initial(name),
            FontSize = letterSize,
            Foreground = Brushes.White,
            Opacity = 0.92,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (Application.Current?.TryFindResource("PixelFont", out var font) == true && font is FontFamily pixel)
            letter.FontFamily = pixel;
        image = new Image { Stretch = Stretch.UniformToFill };
        var panel = new Panel { Background = CoverBrush(name), Children = { letter, image } };
        SetIcon(image, letter, Icon(packId));
        return panel;
    }

    /// <summary>Shows <paramref name="icon"/> on a cover made by <see cref="Cover"/>, or the letter
    /// when there is none.</summary>
    public static void SetIcon(Image image, TextBlock letter, IImage? icon)
    {
        image.Source = icon;
        image.IsVisible = icon is not null;
        letter.IsVisible = icon is null;
    }

    private static IBrush Resource(string key, string fallback) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush
            ? brush
            : SolidColorBrush.Parse(fallback);
}
