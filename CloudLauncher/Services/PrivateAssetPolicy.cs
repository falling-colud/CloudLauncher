using System.IO;

namespace CloudLauncher.Services;

/// <summary>
/// Files that never leave this machine when a pack is uploaded, and that a sync from the server is
/// never allowed to overwrite or delete.
///
/// <para><b>Why this exists.</b> The Arleana character mod keeps its models and textures - the whole
/// converted glTF bundle, ~2 GB of the author's own work - under <c>config/arleana/bundle/</c>, i.e.
/// inside a folder the pack rules share wholesale. Sharing the pack once uploaded the bundle to every
/// collaborator. A <c>.rules.json</c> "local" rule would exclude it, but rules are data: the server
/// copy of a shared pack's rules overwrites the local file whenever the pack page opens, a re-import
/// recreates the pack with defaults, and a mod update never touches rules at all. This policy is code,
/// applied at the sync boundary itself, so it holds no matter how the rules end up.</para>
///
/// <para><b>What the other side sees.</b> The mod jar still ships (it is in <c>mods/</c>), so a friend's
/// client has the same mod set as the server and joins normally; without a bundle folder the mod finds
/// no <c>character.glb</c> and simply reports "no bundle" on first use. Nothing else in the pack depends
/// on the bundle's contents.</para>
///
/// <para><b>Why the download side matters too.</b> A manifest that lacks these paths would make the
/// sync-lock pruning delete the owner's own bundle ("previously server-managed, no longer on the
/// server"), and a manifest that contains stubs for them would overwrite it. Both are refused here.</para>
///
/// <para>Patterns use the same syntax as pack rules: a trailing <c>/</c> means "everything under this
/// folder"; otherwise an exact path or a glob. Built-in entries always apply; users can add more in
/// settings via <see cref="AppSettings.PrivatePathPatterns"/>.</para>
/// </summary>
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
