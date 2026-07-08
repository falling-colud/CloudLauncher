using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.FileSystemGlobbing;

namespace CloudLauncher.Services;

// Keep these local types — they match the Shared DTOs by value so JSON serialization
// works in both directions. The shared PackRule is the wire DTO; this is the service model.

public enum RuleAction
{
    Local   = 0,
    Shared  = 1,
    Ignored = 2
}

public sealed class PackRule
{
    public string Pattern { get; set; } = "";
    public RuleAction Action { get; set; }

    [JsonIgnore]
    public string ActionLabel => Action switch
    {
        RuleAction.Local   => "→ local",
        RuleAction.Shared  => "→ shared",
        RuleAction.Ignored => "ignored",
        _                  => ""
    };

    // Convert to/from the shared wire DTO
    public CloudLauncher.Shared.PackFileRule ToShared() =>
        new(Pattern, (CloudLauncher.Shared.PackRuleAction)(int)Action);

    public static PackRule FromShared(CloudLauncher.Shared.PackFileRule r) =>
        new() { Pattern = r.Pattern, Action = (RuleAction)(int)r.Action };
}

public sealed class RuleMatchResult
{
    public PackRule? Rule { get; init; }
    public RuleAction? Action => Rule?.Action;
    public bool IsIgnored => Action == RuleAction.Ignored;
    public bool IsAutoLocal => Action == RuleAction.Local;
    public bool IsAutoShared => Action == RuleAction.Shared;
    public string Badge => Rule?.ActionLabel ?? "";
}

public sealed class PackRuleService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly ApiClient _api;

    public PackRuleService(ApiClient api) { _api = api; }

    private static string RulesPath(string packRoot) =>
        Path.Combine(packRoot, ".rules.json");

    /// <summary>Pull the admin-defined global default rules from the server and
    /// cache them locally. Safe to call at startup — silently no-ops on network
    /// failure so first-time/offline launches still work from the local cache.</summary>
    public async Task SyncGlobalDefaultsFromServerAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _api.GetDefaultRulesAsync(ct);
            if (resp.Rules.Count == 0) return; // no admin override yet — keep local
            var local = resp.Rules.Select(PackRule.FromShared).ToList();
            SaveGlobalDefaults(local);
        }
        catch { /* offline / not logged in — fall back to last cached copy */ }
    }

    /// <summary>Admin-only: push the global default rules to the server. The local
    /// cache is updated on success so the change is visible immediately.</summary>
    public async Task PushGlobalDefaultsToServerAsync(List<PackRule> rules, CancellationToken ct = default)
    {
        var req = new CloudLauncher.Shared.UpdateGlobalSettingsRequest(
            DefaultRules: rules.Select(r => r.ToShared()).ToList());
        await _api.UpdateGlobalSettingsAsync(req, ct);
        SaveGlobalDefaults(rules);
    }

    public List<PackRule> Load(string packRoot)
    {
        var path = RulesPath(packRoot);
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<List<PackRule>>(File.ReadAllText(path), JsonOpts) ?? new();
        }
        catch { /* corrupt */ }

        // No pack-specific rules yet — fall back to global defaults if they exist.
        return LoadGlobalDefaults() ?? new();
    }

    /// <summary>
    /// Sync rules received from the server into the local .rules.json cache.
    /// Call this when opening a shared pack.
    /// </summary>
    public void SyncFromServer(string packRoot, IReadOnlyList<CloudLauncher.Shared.PackFileRule> serverRules)
    {
        if (serverRules.Count == 0) return; // nothing on server yet — keep local copy
        var local = serverRules.Select(PackRule.FromShared).ToList();
        Save(packRoot, local);
    }

    /// <summary>Convert local rules to the shared wire format for sending to the server.</summary>
    public static List<CloudLauncher.Shared.PackFileRule> ToShared(List<PackRule> rules) =>
        rules.Select(r => r.ToShared()).ToList();

    // ── global default rules (set via dev menu, stored per profile) ──────────

    private static string GlobalDefaultsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CloudLauncher",
            Environment.GetEnvironmentVariable("CL_PROFILE") is { Length: > 0 } p ? p : "default",
            "global-default-rules.json");

    public List<PackRule>? LoadGlobalDefaults()
    {
        try
        {
            if (File.Exists(GlobalDefaultsPath))
                return JsonSerializer.Deserialize<List<PackRule>>(File.ReadAllText(GlobalDefaultsPath), JsonOpts);
        }
        catch { /* corrupt */ }
        return null;
    }

    public void SaveGlobalDefaults(List<PackRule> rules)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(GlobalDefaultsPath)!);
        File.WriteAllText(GlobalDefaultsPath, JsonSerializer.Serialize(rules, JsonOpts));
    }

    public void Save(string packRoot, List<PackRule> rules)
    {
        Directory.CreateDirectory(packRoot);
        File.WriteAllText(RulesPath(packRoot), JsonSerializer.Serialize(rules, JsonOpts));
    }

    /// <summary>
    /// Match a relative path against the rule list. The most specific matching rule wins —
    /// a deeper/longer pattern (e.g. <c>mods/1.12.2/</c>) overrides a broader one (<c>mods/</c>)
    /// regardless of list order, so nested overrides and carve-out exceptions behave intuitively.
    /// Among equally specific matches, the earlier rule in the list wins.
    /// </summary>
    public RuleMatchResult Match(string relativePath, List<PackRule> rules)
    {
        // normalise to forward-slash for consistent matching
        var norm = relativePath.Replace('\\', '/');

        PackRule? best = null;
        var bestScore = int.MinValue;
        foreach (var rule in rules)
        {
            if (!Matches(norm, rule.Pattern)) continue;
            var score = Specificity(rule.Pattern);
            if (score > bestScore) // strictly greater → ties keep the earlier rule
            {
                best = rule;
                bestScore = score;
            }
        }
        return best is null ? new RuleMatchResult() : new RuleMatchResult { Rule = best };
    }

    private static readonly char[] Wildcards = ['*', '?'];

    /// <summary>
    /// How specific a pattern is, for "most specific wins" resolution. Exact (wildcard-free)
    /// patterns beat everything; otherwise a longer anchored prefix (deeper folder) wins, with
    /// total literal-character count as a tiebreaker so e.g. <c>mods/*.jar</c> beats <c>mods/**</c>.
    /// Normalisation mirrors <see cref="Matches"/> so the score reflects what actually matched.
    /// </summary>
    private static int Specificity(string pattern)
    {
        var p = pattern.Replace('\\', '/');
        if (p.EndsWith('/')) p += "**";

        var firstWildcard = p.IndexOfAny(Wildcards);
        var exact = firstWildcard < 0;
        var prefixLen = exact ? p.Length : firstWildcard;

        var literalChars = 0;
        foreach (var c in p)
            if (c != '*' && c != '?') literalChars++;

        return (exact ? 1_000_000 : 0) + prefixLen * 1_000 + literalChars;
    }

    private static bool Matches(string path, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return false;

        // Normalise pattern
        var p = pattern.Replace('\\', '/');

        // Folder prefix shorthand: pattern ends with '/' → match everything inside
        if (p.EndsWith('/'))
            p += "**";

        // If the pattern contains no wildcard, treat it as exact match
        if (!p.Contains('*') && !p.Contains('?'))
            return string.Equals(path, p, StringComparison.OrdinalIgnoreCase);

        // Use FileSystemGlobbing for wildcard patterns
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(p);
        return matcher.Match(path).HasMatches;
    }

    /// <summary>
    /// Apply rules to a list of relative paths, returning those that are NOT ignored,
    /// each annotated with its match result.
    /// </summary>
    public List<(string RelativePath, RuleMatchResult Match)> Apply(
        IEnumerable<string> relativePaths, List<PackRule> rules)
    {
        var result = new List<(string, RuleMatchResult)>();
        foreach (var rp in relativePaths)
        {
            var m = Match(rp, rules);
            if (!m.IsIgnored)
                result.Add((rp, m));
        }
        return result;
    }

    /// <summary>Build a sensible default rule list for a new Minecraft pack.</summary>
    public static List<PackRule> DefaultRules() =>
    [
        new() { Pattern = "logs/",          Action = RuleAction.Ignored },
        new() { Pattern = "crash-reports/", Action = RuleAction.Ignored },
        new() { Pattern = "debug/",         Action = RuleAction.Ignored },
        new() { Pattern = ".mixin.out/",    Action = RuleAction.Ignored },
        new() { Pattern = "saves/",         Action = RuleAction.Ignored },
        new() { Pattern = "screenshots/",   Action = RuleAction.Ignored },
        new() { Pattern = "options.txt",    Action = RuleAction.Ignored },
        new() { Pattern = "servers.dat",    Action = RuleAction.Ignored },
        new() { Pattern = ".cloudlauncher/", Action = RuleAction.Shared  }, // mod flags travel with the pack
        new() { Pattern = "mods/",          Action = RuleAction.Shared  },
        new() { Pattern = "config/",        Action = RuleAction.Shared  },
        new() { Pattern = "scripts/",       Action = RuleAction.Shared  },
        new() { Pattern = "resources/",     Action = RuleAction.Shared  },
        new() { Pattern = "resourcepacks/", Action = RuleAction.Shared  },
        new() { Pattern = "shaderpacks/",   Action = RuleAction.Shared  },
    ];
}
