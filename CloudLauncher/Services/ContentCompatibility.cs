using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>
/// Decides whether one library item's default rule covers one instance, and says why in a sentence
/// the user can act on.
/// </summary>
/// <remarks>
/// <para>Static and without IO: it is asked once per item per instance while a plan is built, so it
/// must not touch the disk. The two facts it can't work out itself (the instance's shader loader and
/// what is currently on disk) are passed in by <see cref="ContentDefaultsService"/>.</para>
/// <para>Every unknown answers yes: an empty rule, an instance with no recorded Minecraft version and a
/// version string that can't be parsed all match. Placing a file the user can remove is better than
/// blocking them over a snapshot name we couldn't parse.</para>
/// </remarks>
public static class ContentCompatibility
{
    /// <summary>
    /// True when <paramref name="policy"/> covers <paramref name="instance"/>.
    /// </summary>
    /// <param name="shaderLoader">What the instance can load shaders with, from
    /// <see cref="ContentLibraryService.LoaderFor"/>, which answers <see cref="ShaderLoader.Iris"/>
    /// instead of <see cref="ShaderLoader.None"/> when it can't tell, so a guess never blocks anyone.</param>
    /// <param name="why">A sentence for the UI, filled in either way: the reason it does not apply,
    /// or the rule it satisfied. Never empty.</param>
    /// <param name="folderRule">The rule on the folder(s) this item is filed under, from
    /// <see cref="ContentFolderRules.EffectiveFor"/>, or null when no folder has a rule. Optional so
    /// existing call sites keep compiling; null means the same as a folder with no rule.</param>
    public static bool Matches(
        ContentDefaultPolicy policy, PackSummary instance, LibraryItem item,
        ShaderLoader shaderLoader, out string why, ContentFolderRule? folderRule = null)
    {
        // Ask the folder first, so that when the folder is what excludes this instance the user reads
        // the folder's sentence. Both sides are vetoes, so the order only changes which one the
        // explanation names.
        if (!FolderAdmits(folderRule, instance, out var folderWhy))
        {
            why = folderWhy;
            return false;
        }

        // Excludes first, and they win: an opt-out the user typed must not be overridden by a broader
        // include.
        if (policy.ExcludePackIds.Contains(instance.Id))
        {
            why = $"you turned this off for {instance.Name}.";
            return false;
        }
        if (FirstMatch(policy.ExcludeInstances, instance.Name) is { } excluded)
        {
            why = $"the rule \"{excluded}\" leaves out instances named like {instance.Name}.";
            return false;
        }

        if (!policy.AppliesToAllInstances
            && !policy.IncludePackIds.Contains(instance.Id)
            && FirstMatch(policy.IncludeInstances, instance.Name) is null)
        {
            why = policy.IncludeInstances.Count > 0
                ? $"this one is limited to instances matching {string.Join(", ", policy.IncludeInstances)}, and {instance.Name} is not one."
                : $"this one is limited to {policy.IncludePackIds.Count} chosen instance(s), and {instance.Name} is not one.";
            return false;
        }

        if (!VersionMatches(policy.McVersionsCsv, instance.MinecraftVersion))
        {
            why = $"this one is set for Minecraft {policy.McVersionsCsv}, and {instance.Name} runs "
                + $"{(string.IsNullOrWhiteSpace(instance.MinecraftVersion) ? "an unknown version" : instance.MinecraftVersion)}.";
            return false;
        }

        if (!LoaderMatches(policy.LoadersCsv, instance.Loader))
        {
            why = $"this one is set for {policy.LoadersCsv}, and {instance.Name} runs "
                + $"{(instance.Loader == LoaderKind.None ? "no mod loader" : instance.Loader.ToString())}.";
            return false;
        }

        // A shader pack in an instance with no shader loader isn't reported by the game: it just renders
        // vanilla with no explanation.
        if (item.Kind == LibraryKind.ShaderPack && policy.RequireShaderLoader && shaderLoader == ShaderLoader.None)
        {
            why = $"nothing in {instance.Name}'s mods folder can load a shader pack, so this would do nothing in game.";
            return false;
        }

        why = policy.HasAnyRule
            ? $"{instance.Name} matches this item's rules ({Describe(policy)})."
            : $"this item has no rules, so every instance gets it - {instance.Name} included.";
        return true;
    }

    /// <summary>
    /// True when a folder's rule admits one instance, and the sentence naming the folder either way.
    /// </summary>
    /// <remarks>
    /// <para>Combined with the item's rule as an intersection: either side's exclude list vetoes, and a
    /// non-empty include list on either side must admit. Folders start with no rule and an empty list
    /// means "any", so libraries from before folder rules behave as they did.</para>
    /// <para>The per-instance choice (Follow / Excluded / Forced) is applied after this by
    /// <see cref="ContentDefaultsService"/>, so a folder never overrides a Forced answer.</para>
    /// </remarks>
    /// <param name="rule">The folder rule, or null when no folder has one.</param>
    /// <param name="why">A sentence for the UI, filled in either way. Never empty.</param>
    public static bool FolderAdmits(ContentFolderRule? rule, PackSummary instance, out string why)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (rule is null)
        {
            why = "no folder narrows this down.";
            return true;
        }

        // An inert head still has to check the rest of its chain. The folder-rule dialog puts the folder
        // being edited at the head (inert while its box is unticked) and the item's other folders in
        // AndAlso; returning early would drop them and show a wider answer than the reconciler gives.
        if (!rule.Enabled || !rule.HasAnyRule) return AndAlsoAdmits(rule, instance, "no folder narrows this down.", out why);

        var folder = string.IsNullOrWhiteSpace(rule.FolderName) ? "this folder" : $"the folder {rule.FolderName}";

        if (rule.ExcludePackIds.Contains(instance.Id))
        {
            why = $"{folder} is turned off for {instance.Name}.";
            return false;
        }
        if (FirstMatch(rule.ExcludeInstances, instance.Name) is { } excluded)
        {
            why = $"{folder} leaves out instances named like {instance.Name} (\"{excluded}\").";
            return false;
        }

        if (!rule.AppliesToAllInstances
            && !rule.IncludePackIds.Contains(instance.Id)
            && FirstMatch(rule.IncludeInstances, instance.Name) is null)
        {
            why = rule.IncludeInstances.Count > 0
                ? $"{folder} is only for instances matching {string.Join(", ", rule.IncludeInstances)}, and {instance.Name} is not one."
                : $"{folder} is only for {rule.IncludePackIds.Count} chosen instance(s), and {instance.Name} is not one.";
            return false;
        }

        if (!VersionMatches(rule.McVersionsCsv, instance.MinecraftVersion))
        {
            why = $"{folder} is only for Minecraft {rule.McVersionsCsv}, and {instance.Name} runs "
                + $"{(string.IsNullOrWhiteSpace(instance.MinecraftVersion) ? "an unknown version" : instance.MinecraftVersion)}.";
            return false;
        }

        if (!LoaderMatches(rule.LoadersCsv, instance.Loader))
        {
            why = $"{folder} is only for {rule.LoadersCsv}, and {instance.Name} runs "
                + $"{(instance.Loader == LoaderKind.None ? "no mod loader" : instance.Loader.ToString())}.";
            return false;
        }

        return AndAlsoAdmits(rule, instance, $"{folder} allows {instance.Name} ({Describe(rule)}).", out why);
    }

    /// <summary>The rest of the chain: every other folder this item is filed under must admit the
    /// instance too, and each reports under its own name.</summary>
    /// <param name="yes">The sentence to use when the whole chain admits.</param>
    private static bool AndAlsoAdmits(ContentFolderRule rule, PackSummary instance, string yes, out string why)
    {
        foreach (var also in rule.AndAlso)
        {
            if (!FolderAdmits(also, instance, out var alsoWhy))
            {
                why = alsoWhy;
                return false;
            }
        }
        why = yes;
        return true;
    }

    /// <summary>
    /// True when <paramref name="instanceVersion"/> is covered by a comma-separated version rule.
    /// </summary>
    /// <remarks>
    /// <para>Accepted forms:</para>
    /// <list type="bullet">
    /// <item>exact: <c>1.21.1</c> matches only 1.21.1;</item>
    /// <item>trailing wildcard: <c>1.21.*</c> and <c>1.21*</c> both match 1.21, 1.21.1, 1.21.4;</item>
    /// <item>bare family: <c>1.21</c> also admits 1.21.1 and 1.21.4, since packs labelled "1.21" are
    /// used across the whole family;</item>
    /// <item>range: <c>1.20-1.21</c>, also with an en or em dash and any spacing (the csv is only split
    /// on <c>,</c> and <c>;</c>, see <see cref="ContentDefaults.Split"/>).</item>
    /// </list>
    /// <para>Range bounds are inclusive. The upper bound is family-widened (only the components it names
    /// are compared), so <c>1.20-1.21</c> admits 1.21.4. Ranges never match snapshots, and a range whose
    /// bounds don't parse is skipped instead of allowed.</para>
    /// <para>Versions are split on dots, taking each part's leading digits (<c>1.20.1-pre1</c> reads as
    /// 1.20.1), as in <see cref="OptionsTxtService.VersionUsesFilePrefix"/>. Anything that isn't a
    /// <c>1.x</c> release only matches an exact string.</para>
    /// </remarks>
    public static bool VersionMatches(string? csv, string? instanceVersion)
    {
        var tokens = ContentDefaults.Split(csv);
        if (tokens.Count == 0) return true;
        // No recorded version (an instance that was never configured) isn't a mismatch; blocking would
        // hide the item with no visible reason.
        if (string.IsNullOrWhiteSpace(instanceVersion)) return true;

        var instance = instanceVersion.Trim();
        var instanceParts = ParseRelease(instance);

        foreach (var raw in tokens)
        {
            var token = raw.Trim();
            if (token is "*" || token.Equals("any", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(token, instance, StringComparison.OrdinalIgnoreCase)) return true;

            if (token.EndsWith('*'))
            {
                var prefix = token[..^1].TrimEnd('.');
                if (prefix.Length == 0) return true;
                if (instance.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
                continue;
            }

            // Range: "1.20-1.21". Checked before the family branch, because ParseRelease("1.20-1.21")
            // would read the token as 1.20.21.
            if (TrySplitRange(token, out var lowText, out var highText))
            {
                var low = ParseRelease(lowText);
                var high = ParseRelease(highText);
                // An unreadable bound, or an instance version that can't be ordered (a snapshot): this
                // token has no opinion, so skip it rather than grant it.
                if (low is null || high is null || instanceParts is null) continue;
                // Lower bound inclusive and compared in full, so 1.20.1 excludes 1.20.
                if (Compare(instanceParts, low, int.MaxValue) < 0) continue;
                // Family-widened upper bound: compare only the components the bound itself named.
                if (Compare(instanceParts, high, high.Length) > 0) continue;
                return true;
            }

            // Family: "1.21" covers "1.21.4". Only meaningful when both sides parse as releases.
            var tokenParts = ParseRelease(token);
            if (tokenParts is null || instanceParts is null) continue;
            if (tokenParts.Length >= instanceParts.Length) continue;
            var same = true;
            for (var i = 0; i < tokenParts.Length; i++)
                if (tokenParts[i] != instanceParts[i]) { same = false; break; }
            if (same) return true;
        }
        return false;
    }

    /// <summary>
    /// Splits <c>"1.20-1.21"</c> (or the same with an en or em dash, and any spacing) into its two
    /// bounds.
    /// </summary>
    /// <remarks>Splits on the first separator only, so a bound that contains a dash
    /// (<c>1.20.1-pre1</c>) ends up in a half that then fails to parse. Both halves must be non-empty,
    /// so a leading dash is not a range.</remarks>
    private static bool TrySplitRange(string token, out string low, out string high)
    {
        low = high = "";
        var at = token.IndexOfAny(RangeSeparators, 1);
        if (at <= 0 || at >= token.Length - 1) return false;
        low = token[..at].Trim();
        high = token[(at + 1)..].Trim();
        return low.Length > 0 && high.Length > 0;
    }

    /// <summary>ASCII hyphen, en dash and em dash: what people type, or paste from a store page, to
    /// mean "to".</summary>
    private static readonly char[] RangeSeparators = ['-', '\u2013', '\u2014'];

    /// <summary>
    /// Orders two parsed releases, comparing at most <paramref name="components"/> of them.
    /// </summary>
    /// <remarks>A missing component counts as 0, so 1.20 and 1.20.0 are the same version.
    /// <paramref name="components"/> is how a range's upper bound is family-widened: stopping after the
    /// bound's own components makes 1.21.4 equal to the bound 1.21.</remarks>
    private static int Compare(int[] a, int[] b, int components)
    {
        var length = Math.Min(components, Math.Max(a.Length, b.Length));
        for (var i = 0; i < length; i++)
        {
            var va = i < a.Length ? a[i] : 0;
            var vb = i < b.Length ? b[i] : 0;
            if (va != vb) return va < vb ? -1 : 1;
        }
        return 0;
    }

    /// <summary>True when a comma-separated loader rule covers <paramref name="loader"/>. Empty means
    /// any.</summary>
    /// <remarks>Matched by name, case-insensitively, with the same spelling the bundle DTOs and the mod
    /// stores use ("fabric,neoforge"). Unknown tokens ("quilt" pasted from a store page, say) are
    /// ignored instead of hiding the item from every instance.</remarks>
    public static bool LoaderMatches(string? csv, LoaderKind loader)
    {
        var tokens = ContentDefaults.Split(csv);
        if (tokens.Count == 0) return true;

        var known = false;
        foreach (var token in tokens)
        {
            if (token is "*" || token.Equals("any", StringComparison.OrdinalIgnoreCase)) return true;
            if (!Enum.TryParse<LoaderKind>(token, ignoreCase: true, out var parsed)) continue;
            known = true;
            if (parsed == loader) return true;
        }
        // Nothing in the csv named a loader this launcher knows about, so the rule says nothing about
        // this instance either way.
        return !known;
    }

    /// <summary>
    /// The numeric components of a <c>1.x</c> Minecraft release, or null for anything else.
    /// </summary>
    /// <remarks>Shared by the matcher and <see cref="ResourcePackFormats"/>, so a pack-format range and a
    /// version rule agree on what "1.21.4" is. Null for snapshots because they can't be ordered reliably
    /// by name.</remarks>
    public static int[]? ParseRelease(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var parts = version.Trim().Split('.');
        if (parts.Length < 2 || parts[0] != "1") return null;

        var result = new int[parts.Length];
        result[0] = 1;
        for (var i = 1; i < parts.Length; i++)
        {
            var digits = new string(parts[i].TakeWhile(char.IsAsciiDigit).ToArray());
            if (digits.Length == 0) return null;
            if (!int.TryParse(digits, out var value)) return null;
            result[i] = value;
        }
        return result;
    }

    /// <summary>Orders two Minecraft releases. False when either side is not a release this can read,
    /// which every caller must treat as "no opinion" rather than as equality.</summary>
    public static bool TryCompare(string? a, string? b, out int comparison)
    {
        comparison = 0;
        var pa = ParseRelease(a);
        var pb = ParseRelease(b);
        if (pa is null || pb is null) return false;

        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var va = i < pa.Length ? pa[i] : 0;
            var vb = i < pb.Length ? pb[i] : 0;
            if (va == vb) continue;
            comparison = va < vb ? -1 : 1;
            return true;
        }
        return true;
    }

    /// <summary>One line naming what a policy narrows down to, for a row subtitle.</summary>
    public static string Describe(ContentDefaultPolicy policy)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(policy.McVersionsCsv)) parts.Add(policy.McVersionsCsv!);
        if (!string.IsNullOrWhiteSpace(policy.LoadersCsv)) parts.Add(policy.LoadersCsv!);
        if (policy.IncludeInstances.Count > 0) parts.Add($"only {string.Join(", ", policy.IncludeInstances)}");
        if (policy.IncludePackIds.Count > 0) parts.Add($"only {policy.IncludePackIds.Count} chosen instance(s)");
        if (policy.ExcludeInstances.Count > 0) parts.Add($"not {string.Join(", ", policy.ExcludeInstances)}");
        if (policy.ExcludePackIds.Count > 0) parts.Add($"{policy.ExcludePackIds.Count} instance(s) opted out");
        return parts.Count == 0 ? "every instance" : string.Join("  ·  ", parts);
    }

    /// <summary>One line naming what a folder rule narrows down to, for a chip tooltip.</summary>
    /// <remarks>Same sentence shape as the item overload, since both appear side by side in the folder
    /// dialog.</remarks>
    public static string Describe(ContentFolderRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(rule.McVersionsCsv)) parts.Add(rule.McVersionsCsv!);
        if (!string.IsNullOrWhiteSpace(rule.LoadersCsv)) parts.Add(rule.LoadersCsv!);
        if (rule.IncludeInstances.Count > 0) parts.Add($"only {string.Join(", ", rule.IncludeInstances)}");
        if (rule.IncludePackIds.Count > 0) parts.Add($"only {rule.IncludePackIds.Count} chosen instance(s)");
        if (rule.ExcludeInstances.Count > 0) parts.Add($"not {string.Join(", ", rule.ExcludeInstances)}");
        if (rule.ExcludePackIds.Count > 0) parts.Add($"{rule.ExcludePackIds.Count} instance(s) opted out");
        return parts.Count == 0 ? "every instance" : string.Join("  \u00b7  ", parts);
    }

    /// <summary>The first pattern in <paramref name="patterns"/> that matches
    /// <paramref name="instanceName"/>, or null.</summary>
    /// <remarks>Uses <see cref="PackRuleService.PatternMatches"/>, the launcher's glob matcher (exact
    /// names, a trailing <c>*</c> and <c>**</c>), so there is only one set of pattern rules.</remarks>
    private static string? FirstMatch(IReadOnlyList<string> patterns, string instanceName)
    {
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            try
            {
                if (PackRuleService.PatternMatches(instanceName, pattern.Trim())) return pattern;
            }
            catch
            {
                // A pattern the glob matcher rejects matches nothing, instead of throwing in the middle
                // of a plan.
            }
        }
        return null;
    }
}
