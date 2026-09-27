using System.IO;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>
/// The instances on this PC, read straight from the disk.
/// </summary>
/// <remarks>
/// Fallback behind the server and the pack-list cache, for a profile that has never cached a
/// <c>GET /packs</c> (restored from a backup, or first run offline). Pack folders are found by the
/// <c>.packid</c> marker that <see cref="PackFolderService"/> writes.
/// </remarks>
public static class LocalPackScanner
{
    /// <summary>Shown when the list came from the disk rather than the server or its cache. Phrased
    /// as a clause because pages put it inside their own sentence.</summary>
    public const string StaleReason = "offline - showing the instances found on this PC";

    /// <summary>
    /// Every instance folder under <see cref="AppSettings.PacksRoot"/>, sorted by name.
    /// </summary>
    /// <remarks>
    /// Packs with cached details come back in full. Others are rebuilt from the folder, with the
    /// Minecraft version and loader left unknown.
    /// </remarks>
    public static List<PackSummary> Scan(AppSettings settings)
    {
        var rows = new List<PackSummary>();
        try
        {
            if (!Directory.Exists(settings.PacksRoot)) return rows;
            var details = PackDetailCache.LoadAll();

            foreach (var dir in Directory.EnumerateDirectories(settings.PacksRoot))
            {
                if (!TryReadPackId(dir, out var id)) continue;
                rows.Add(details.TryGetValue(id, out var detail)
                    ? PackDetailCache.ToSummary(detail)
                    : FromFolder(dir, id, settings));
            }
        }
        catch (Exception ex)
        {
            AppLog.LogError("packs", ex); // a failed scan returns what it found so far
        }

        return rows.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The folder holding this instance, or null when none of them claims the id.</summary>
    /// <remarks>
    /// Same lookup as <see cref="PackFolderService.PackRoot"/>, but that one creates the folder when it
    /// is missing, and the offline callers of this must not leave an empty directory behind.
    /// </remarks>
    public static string? FindPackRoot(AppSettings settings, Guid packId)
    {
        try
        {
            if (Directory.Exists(settings.PacksRoot))
                foreach (var dir in Directory.EnumerateDirectories(settings.PacksRoot))
                    if (TryReadPackId(dir, out var id) && id == packId)
                        return dir;

            // Packs created before named folders are still named after the raw GUID.
            var legacy = System.IO.Path.Combine(settings.PacksRoot, packId.ToString("N"));
            if (Directory.Exists(legacy)) return legacy;
        }
        catch { /* an unreadable packs root counts as having no folders */ }
        return null;
    }

    /// <summary>Reads a folder's <c>.packid</c> marker.</summary>
    public static bool TryReadPackId(string folder, out Guid packId)
    {
        packId = Guid.Empty;
        try
        {
            var marker = System.IO.Path.Combine(folder, ".packid");
            return File.Exists(marker) && Guid.TryParse(File.ReadAllText(marker).Trim(), out packId);
        }
        catch { return false; }
    }

    private static PackSummary FromFolder(string dir, Guid id, AppSettings settings)
    {
        var created = SafeTime(() => Directory.GetCreationTimeUtc(dir));
        var updated = SafeTime(() => Directory.GetLastWriteTimeUtc(dir));

        return new PackSummary(
            Id: id,
            Name: NameFromFolder(System.IO.Path.GetFileName(dir), id),
            Description: null,
            // Assume the signed-in user owns it; the row is only ever shown to them.
            OwnerId: settings.UserId ?? Guid.Empty,
            OwnerUsername: settings.Username ?? "",
            Visibility: PackVisibility.Private,
            IsShared: false,
            // "Empty" would make Play refuse to start it. The folder exists, so assume it has content.
            IsEmpty: false,
            MinecraftVersion: null,
            Loader: LoaderKind.None,
            LoaderVersion: null,
            CreatedAt: created,
            UpdatedAt: updated,
            // Enough to open and launch. Sharing controls need the server.
            EffectivePermissions: PackPermissions.ReadOnly);
    }

    /// <summary>Turns a folder slug back into a name: "expanding-biomes" -> "Expanding Biomes".</summary>
    /// <remarks>
    /// <see cref="PackFolderService.CreateNamedFolder"/> appends the first eight hex digits of the pack
    /// id on a name collision, so that suffix is stripped when it matches this pack.
    /// </remarks>
    internal static string NameFromFolder(string folder, Guid id)
    {
        var slug = folder;
        var suffix = "-" + id.ToString("N")[..8];
        if (slug.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            slug = slug[..^suffix.Length];

        var words = slug.Split(['-', '_', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length == 1 ? w.ToUpperInvariant() : char.ToUpperInvariant(w[0]) + w[1..]);
        var name = string.Join(' ', words);
        return string.IsNullOrWhiteSpace(name) ? folder : name;
    }

    private static DateTimeOffset SafeTime(Func<DateTime> read)
    {
        try { return new DateTimeOffset(read(), TimeSpan.Zero); }
        catch { return DateTimeOffset.UtcNow; }
    }
}
