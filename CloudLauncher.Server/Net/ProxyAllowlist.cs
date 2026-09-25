using System.Text.RegularExpressions;

namespace CloudLauncher.Server.Net;

/// <summary>
/// The store calls the launcher makes through <c>/proxy/{platform}/...</c>, and nothing else.
/// CurseForge calls carry the server's shared key, so an account here must not become general
/// access to the CurseForge API under that key.
/// </summary>
/// <remarks>
/// Paths are matched whole, after <see cref="IsCleanPath"/>. CurseForge ids are digits; Modrinth
/// ids, slugs and hashes are letters, digits, '-' and '_'. A new store endpoint in the launcher
/// needs a line here too.
/// </remarks>
public static class ProxyAllowlist
{
    /// <summary>Longest query string forwarded. The longest the launcher sends, a Modrinth lookup of
    /// fifty project ids, is under a kilobyte.</summary>
    public const int MaxQueryLength = 4 * 1024;

    /// <summary>Longest path looked at. The longest allowed shape is well under this.</summary>
    private const int MaxPathLength = 256;

    private const string CfId = "[0-9]{1,10}";
    private const string MrId = "[A-Za-z0-9_-]{1,64}";

    private sealed record Entry(string Platform, string Method, Regex Path);

    private static readonly Entry[] Entries =
    [
        // Browse search, cross-store slug lookups, Settings key test (SearchAsync, SearchBySlugAsync).
        Get("curseforge", "mods/search"),
        // Category filters on the mod and resource pack browsers (GetCategoriesAsync).
        Get("curseforge", "categories"),
        // One project: detail pages, dependencies, pack import, the opt-out check behind a refused
        // download URL (GetModAsync, GetProjectDetailAsync).
        Get("curseforge", $"mods/{CfId}"),
        // Project descriptions on detail pages (GetDescriptionAsync).
        Get("curseforge", $"mods/{CfId}/description"),
        // File list: version pickers, update checks, dependencies, pack export (GetVersionsAsync).
        Get("curseforge", $"mods/{CfId}/files"),
        // One file, when importing a CurseForge pack (GetVersionAsync).
        Get("curseforge", $"mods/{CfId}/files/{CfId}"),
        // A file's changelog in the update review and changelog cards (GetChangelogAsync).
        Get("curseforge", $"mods/{CfId}/files/{CfId}/changelog"),
        // Download URL for a file listed without one (GetDownloadUrlAsync).
        Get("curseforge", $"mods/{CfId}/files/{CfId}/download-url"),
        // Batch project lookups: pack import/export, update checks, fingerprint matches
        // (GetModsAsync, GetProjectFactsAsync, GetLatestFileIndexesAsync).
        Post("curseforge", "mods"),
        // Batch file lookups: pack import and update checks (GetFilesAsync).
        Post("curseforge", "mods/files"),
        // Recognising installed jars by fingerprint (MatchFingerprintsAsync).
        Post("curseforge", "fingerprints"),

        // Browse search and cross-store name lookups (SearchAsync).
        Get("modrinth", "search"),
        // Category filters on the mod and resource pack browsers (GetCategoriesAsync).
        Get("modrinth", "tag/category"),
        // One project by id or slug: detail pages, dependencies, cross-store matching
        // (GetProjectAsync, GetProjectDetailAsync, GetDescriptionAsync).
        Get("modrinth", $"project/{MrId}"),
        // A project's versions: version pickers, update checks, dependencies
        // (GetVersionsAsync, GetVersionsStrictAsync).
        Get("modrinth", $"project/{MrId}/version"),
        // One version: pinned dependencies and update review changelogs
        // (GetVersionWithProjectAsync, GetVersionChangelogAsync).
        Get("modrinth", $"version/{MrId}"),
        // Batch project lookups after a hash match (GetProjectsAsync).
        Get("modrinth", "projects"),
        // Recognising installed jars by SHA-512 (MatchHashesAsync).
        Post("modrinth", "version_files"),
        // Update checks for many installed files at once (GetLatestVersionsByHashAsync).
        Post("modrinth", "version_files/update"),
    ];

    /// <summary>The platform's canonical name ("curseforge" or "modrinth"), or null for anything else.</summary>
    public static string? Platform(string? platform) =>
        string.Equals(platform, "curseforge", StringComparison.OrdinalIgnoreCase) ? "curseforge"
        : string.Equals(platform, "modrinth", StringComparison.OrdinalIgnoreCase) ? "modrinth"
        : null;

    /// <summary>False for a path that is empty or too long, has an empty segment, or contains '%',
    /// '\' or "..". Checked before any pattern, so no encoding or traversal trick reaches one.</summary>
    public static bool IsCleanPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxPathLength) return false;
        if (path.Contains('%') || path.Contains('\\') || path.Contains("..", StringComparison.Ordinal)) return false;
        foreach (var segment in path.Split('/'))
            if (segment.Length == 0) return false;
        return true;
    }

    /// <summary>True when <paramref name="method"/> on <paramref name="path"/> is a call the launcher
    /// makes to <paramref name="platform"/> (a canonical name from <see cref="Platform"/>).</summary>
    public static bool Allows(string platform, string method, string? path)
    {
        if (!IsCleanPath(path)) return false;
        foreach (var entry in Entries)
            if (entry.Platform == platform
                && string.Equals(entry.Method, method, StringComparison.OrdinalIgnoreCase)
                && entry.Path.IsMatch(path!))
                return true;
        return false;
    }

    private static Entry Get(string platform, string pattern) => new(platform, "GET", Whole(pattern));
    private static Entry Post(string platform, string pattern) => new(platform, "POST", Whole(pattern));

    private static Regex Whole(string pattern) =>
        new($@"\A(?:{pattern})\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
