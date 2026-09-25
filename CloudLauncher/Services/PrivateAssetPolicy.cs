using System.IO;

namespace CloudLauncher.Services;

/// <summary>Files that never leave this machine when a pack is uploaded, and that a sync from the
/// server is never allowed to overwrite or delete.</summary>
/// <remarks>
/// <para>Enforced in code at the sync boundary, since pack rules are data that the server copy, a
/// re-import or a mod update can change. On download, a manifest without these paths would make
/// sync-lock pruning delete them, and one with stubs would overwrite them. The built-in entry keeps
/// the Arleana character mod's model bundle (~2 GB of the author's own assets) private; without it
/// the mod just reports "no bundle".</para>
/// <para>Patterns use the pack-rule syntax (trailing <c>/</c> = whole folder, otherwise an exact path
/// or a glob). Users can add more via <see cref="AppSettings.PrivatePathPatterns"/>.</para>
/// </remarks>
public static class PrivateAssetPolicy
{
    /// <summary>Always-on entries. Keep these folder-shaped (trailing slash) so a renamed or
    /// re-versioned file inside them is covered without anyone editing this list.</summary>
    public static readonly IReadOnlyList<string> BuiltIn =
    [
        "config/arleana/bundle/",   // Arleana: character models, textures, animation clips
    ];

    /// <summary>Effective pattern list: built-ins plus whatever the user added, de-duplicated.</summary>
    public static IReadOnlyList<string> Patterns(AppSettings settings)
    {
        var list = new List<string>(BuiltIn);
        foreach (var p in settings.PrivatePathPatterns ?? [])
        {
            var trimmed = p?.Trim().Replace('\\', '/');
            if (string.IsNullOrEmpty(trimmed)) continue;
            if (!list.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) list.Add(trimmed);
        }
        return list;
    }

    /// <summary>True when <paramref name="relativePath"/> (game/-relative, either slash style) must
    /// stay on this machine.</summary>
    public static bool IsPrivate(string relativePath, AppSettings settings)
    {
        var norm = relativePath.Replace('\\', '/');
        foreach (var pattern in Patterns(settings))
            if (PackRuleService.PatternMatches(norm, pattern)) return true;
        return false;
    }

    /// <summary>Splits paths into (public, private). The private list is what the caller should
    /// mention in its log so the omission is visible rather than silent.</summary>
    public static (List<string> Public, List<string> Private) Partition(IEnumerable<string> relativePaths, AppSettings settings)
    {
        var pub = new List<string>();
        var priv = new List<string>();
        foreach (var rel in relativePaths)
            (IsPrivate(rel, settings) ? priv : pub).Add(rel);
        return (pub, priv);
    }

    /// <summary>One human-readable line for sync logs: which folders were held back and how many files.</summary>
    public static string Describe(IReadOnlyCollection<string> privatePaths, AppSettings settings)
    {
        if (privatePaths.Count == 0) return "";
        var patterns = Patterns(settings);
        var hit = patterns.Where(p => privatePaths.Any(f => PackRuleService.PatternMatches(f, p))).ToList();
        return $"{privatePaths.Count} file(s) under {string.Join(", ", hit)}";
    }

    /// <summary>Convenience for callers that only have an absolute path under a game dir.</summary>
    public static bool IsPrivate(string gameDir, string absolutePath, AppSettings settings) =>
        IsPrivate(Path.GetRelativePath(gameDir, absolutePath), settings);
}
