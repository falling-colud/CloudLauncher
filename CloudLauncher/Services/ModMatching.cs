namespace CloudLauncher.Services;

/// <summary>
/// Heuristics for deciding whether a project on one store is the same mod as a project on the
/// other store.
///
/// Hash matching (SHA-512 / Murmur2) only links the stores when both host a byte-identical jar,
/// and many mods ship a rebuilt or re-signed jar to each. Matching the slug or display name
/// (exact match after normalisation) closes that gap without hiding unrelated mods that happen
/// to share a name.
/// </summary>
public static class ModMatching
{
    /// <summary>True when <paramref name="candidate"/> shares a normalised slug or name
    /// with the source mod from the other store.</summary>
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
