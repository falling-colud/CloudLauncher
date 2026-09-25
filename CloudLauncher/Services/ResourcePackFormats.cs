namespace CloudLauncher.Services;

/// <summary>Which Minecraft versions one <c>pack_format</c> number belongs to.</summary>
/// <param name="First">Earliest release that reads this format.</param>
/// <param name="Last">Latest release that reads it. Equal to <paramref name="First"/> for a format
/// that lasted one release.</param>
public sealed record PackFormatRange(int Format, string First, string Last)
{
    /// <summary>"1.21 - 1.21.1", or just "1.21.4" when the format lasted one release.</summary>
    public string Label => First == Last ? First : $"{First} - {Last}";

    /// <summary>A version rule the user can accept as-is for an item of this format.</summary>
    /// <remarks>Uses the family form (<c>1.21.*</c>) that
    /// <see cref="ContentCompatibility.VersionMatches"/> reads, since a pack made for 1.21 keeps
    /// working on the point releases that share its format.</remarks>
    public string VersionCsv
    {
        get
        {
            var parts = First.Split('.');
            return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}.*" : First;
        }
    }
}

/// <summary>The <c>pack_format</c> to Minecraft version table, used only to suggest a version rule
/// and to warn.</summary>
/// <remarks>It will go out of date, so it never blocks anything: an unknown format or version means
/// "no opinion". The warning is still worth it, because the game loads a wrong-format pack, lists it
/// as incompatible and renders it oddly, which looks like a broken pack.</remarks>
public static class ResourcePackFormats
{
    // Ordered by format. Release versions only, since snapshot formats keep changing.
    private static readonly PackFormatRange[] Table =
    [
        new(1,  "1.6.1",  "1.8.9"),
        new(2,  "1.9",    "1.10.2"),
        new(3,  "1.11",   "1.12.2"),
        new(4,  "1.13",   "1.14.4"),
        new(5,  "1.15",   "1.16.1"),
        new(6,  "1.16.2", "1.16.5"),
        new(7,  "1.17",   "1.17.1"),
        new(8,  "1.18",   "1.18.2"),
        new(9,  "1.19",   "1.19.2"),
        new(11, "1.19.3", "1.19.3"),
        new(12, "1.19.4", "1.19.4"),
        new(13, "1.20",   "1.20.1"),
        new(15, "1.20.2", "1.20.2"),
        new(18, "1.20.3", "1.20.4"),
        new(22, "1.20.5", "1.20.6"),
        new(32, "1.21",   "1.21.1"),
        new(34, "1.21.2", "1.21.3"),
        new(42, "1.21.4", "1.21.4"),
        new(46, "1.21.5", "1.21.5"),
        new(55, "1.21.6", "1.21.8"),
    ];

    /// <summary>The highest format in the table. Packs above it are newer than this build knows about,
    /// so they get no warning.</summary>
    public static int Newest => Table[^1].Format;

    /// <summary>What a format number means, or null when it is not in the table.</summary>
    public static PackFormatRange? For(int packFormat) =>
        Table.FirstOrDefault(r => r.Format == packFormat);

    /// <summary>
    /// True when a pack of this format suits this instance, and also whenever it can't be told.
    /// </summary>
    /// <remarks>Only decides whether to show a warning. Unknown formats, formats newer than the table and
    /// non-release versions all return true so a stale table stays quiet.</remarks>
    public static bool FormatSuitsVersion(int packFormat, string? minecraftVersion)
    {
        if (For(packFormat) is not { } range) return true;
        if (ContentCompatibility.ParseRelease(minecraftVersion) is null) return true;

        // Inside [First, Last] inclusive. A failed comparison means an unreadable version: no opinion.
        if (ContentCompatibility.TryCompare(minecraftVersion, range.First, out var vsFirst) && vsFirst < 0) return false;
        if (ContentCompatibility.TryCompare(minecraftVersion, range.Last, out var vsLast) && vsLast > 0) return false;
        return true;
    }

    /// <summary>The version rule to pre-fill when the user makes a resource pack a default, or null
    /// when the pack's format says nothing useful.</summary>
    public static string? SuggestVersionCsv(int? packFormat) =>
        packFormat is { } f && For(f) is { } range ? range.VersionCsv : null;

    /// <summary>A sentence for the row when a pack's format does not suit an instance, else null.</summary>
    /// <remarks>Worded as information, not an error: the apply still happens, and the pack may be fine
    /// since authors often ship one zip for several formats.</remarks>
    public static string? Advisory(int? packFormat, string? minecraftVersion)
    {
        if (packFormat is not { } format) return null;
        if (For(format) is not { } range) return null;
        if (FormatSuitsVersion(format, minecraftVersion)) return null;
        return $"This pack is built for Minecraft {range.Label} (pack format {format}); this instance runs "
             + $"{minecraftVersion}. It will still be installed - Minecraft will list it as incompatible.";
    }

    /// <summary>"pack format 32 (1.21 - 1.21.1)", or just the number when it is not in the table.</summary>
    public static string Describe(int? packFormat) =>
        packFormat is not { } format ? "no pack format"
        : For(format) is { } range ? $"pack format {format} ({range.Label})"
        : $"pack format {format}";
}
