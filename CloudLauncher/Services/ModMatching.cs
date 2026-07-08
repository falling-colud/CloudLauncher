namespace CloudLauncher.Services;

/// <summary>
/// Heuristics for deciding whether a project listing on one store is the same mod as
/// a listing on the other store.
///
/// Hash matching (SHA-512 / Murmur2) is exact but only links the two stores when they
/// host a byte-identical jar. Many mods ship a re-built/re-signed jar to each store, so
/// the hashes differ and a mod installed from CurseForge can still look downloadable on
/// Modrinth (and vice versa). This closes that gap by matching on the project's slug or
/// display name, which stay stable across stores — while staying strict enough (exact
/// match after normalisation) that an unrelated mod sharing a name isn't wrongly hidden.
/// </summary>
public static class ModMatching
{
    /// <summary>True when <paramref name="candidate"/> shares a normalised slug or name
    /// with the source mod from the other store — a high-confidence identity signal.</summary>
    public static bool IsLikelySameMod(ModSummary candidate, string? sourceSlug, string? sourceName) =>
        KeysMatch(candidate.Slug, sourceSlug)
        || KeysMatch(candidate.Slug, sourceName)
        || KeysMatch(candidate.Name, sourceSlug)
        || KeysMatch(candidate.Name, sourceName);

    private static bool KeysMatch(string? a, string? b)
    {
        var normalized = Normalize(a);
        return normalized.Length > 0 && normalized == Normalize(b);
    }

    // Strip everything but letters and digits so "Iris Shaders", "iris-shaders" and
    // "irisshaders" all collapse to the same key across the two stores' conventions.
    private static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }
}
