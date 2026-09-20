using System.Collections.Concurrent;

namespace CloudLauncher.Services;

/// <summary>
/// One place every view asks for a mod's published versions, so a pack of four hundred mods costs
/// four hundred store calls once per quarter hour rather than once per view, per tab switch and per
/// re-scan. The Mods tab, Modpack Management, the graph, the update dialog, the version picker and
/// the browse page all read through here.
///
/// <para>Why it exists: the launcher server proxies every store call from one address for every user.
/// On 2026-09-12 the journal showed the same version lists being fetched thousands of times a minute
/// during update checks — CurseForge lists paged fifty files at a time, so one mod could cost thirty
/// calls — until CurseForge answered 403 and Modrinth 429 for everyone at once, which the client then
/// reported as "API key rejected". Fewer, cached, filtered calls are the fix at the source.</para>
///
/// <para>Lists are cached per (mod, Minecraft version, loader). A filtered ask is sent to the store
/// filtered — one page instead of the mod's entire history — and is served from an unfiltered list
/// instead when one is already cached. In-flight fetches are shared: a second caller for the same key
/// awaits the first request instead of adding one. A failed fetch is not cached, so the next caller
/// retries rather than being told the mod has no versions.</para>
/// </summary>
public sealed class ModVersionCatalog
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    private readonly ModrinthService _modrinth;
    private readonly CurseForgeService _curseForge;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _changelogs = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Entry
    {
        public required Task<IReadOnlyList<ModVersion>> Task { get; init; }
        public required DateTimeOffset FetchedAt { get; init; }
        public bool IsFresh => !Task.IsFaulted && !Task.IsCanceled && DateTimeOffset.UtcNow - FetchedAt < Ttl;
    }

    public ModVersionCatalog(ModrinthService modrinth, CurseForgeService curseForge)
    {
        _modrinth = modrinth;
        _curseForge = curseForge;
    }

    private static string KeyFor(ModSummary mod, string? mc, string? loader) =>
        $"{mod.Source}:{mod.Id}|{mc?.Trim().ToLowerInvariant()}|{loader?.Trim().ToLowerInvariant()}";

    /// <summary>The published versions of <paramref name="mod"/>, newest first — every version when
    /// <paramref name="mcVersion"/> and <paramref name="loader"/> are null, otherwise only those for that
    /// Minecraft version and loader. <paramref name="forceRefresh"/> bypasses the cache.</summary>
    public async Task<IReadOnlyList<ModVersion>> GetVersionsAsync(ModSummary mod, string? mcVersion = null,
        string? loader = null, bool forceRefresh = false, CancellationToken ct = default)
    {
        mcVersion = string.IsNullOrWhiteSpace(mcVersion) ? null : mcVersion.Trim();
        loader = string.IsNullOrWhiteSpace(loader) ? null : loader.Trim();
        var key = KeyFor(mod, mcVersion, loader);

        if (!forceRefresh)
        {
            if (_entries.TryGetValue(key, out var cached) && cached.IsFresh)
                return await cached.Task.WaitAsync(ct);

            // A fresh, already-complete unfiltered list answers a filtered ask without the network.
            if ((mcVersion is not null || loader is not null)
                && _entries.TryGetValue(KeyFor(mod, null, null), out var whole)
                && whole.IsFresh && whole.Task.IsCompletedSuccessfully)
                return whole.Task.Result.Where(v => IsCompatible(v, mcVersion, loader)).ToList();
        }

        // The shared fetch runs on its own token: one caller cancelling (a closed tab) must not fault
        // the list for everyone else waiting on the same request.
        var entry = new Entry { Task = FetchAsync(mod, mcVersion, loader), FetchedAt = DateTimeOffset.UtcNow };
        _entries[key] = entry;
        _ = entry.Task.ContinueWith(
            _ => _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry)),
            CancellationToken.None,
            TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return await entry.Task.WaitAsync(ct);
    }

    /// <summary>The newest versions for a Minecraft version and loader — enough to answer "is there an
    /// update?". For Modrinth that is the whole filtered list (one request either way); for CurseForge
    /// it is the first page of fifty, newest first, instead of paging through a mod's entire history.
    /// A cached full list serves it when there is one.</summary>
    public async Task<IReadOnlyList<ModVersion>> GetLatestVersionsAsync(ModSummary mod, string? mcVersion,
        string? loader, bool forceRefresh = false, CancellationToken ct = default)
    {
        if (mod.Source != ModSource.CurseForge)
            return await GetVersionsAsync(mod, mcVersion, loader, forceRefresh, ct);

        mcVersion = string.IsNullOrWhiteSpace(mcVersion) ? null : mcVersion.Trim();
        loader = string.IsNullOrWhiteSpace(loader) ? null : loader.Trim();
        var fullKey = KeyFor(mod, mcVersion, loader);
        var headKey = fullKey + "|head";
        if (!forceRefresh)
        {
            if (_entries.TryGetValue(fullKey, out var full) && full.IsFresh && full.Task.IsCompletedSuccessfully)
                return full.Task.Result;
            if (_entries.TryGetValue(headKey, out var head) && head.IsFresh)
                return await head.Task.WaitAsync(ct);
        }

        var entry = new Entry { Task = FetchAsync(mod, mcVersion, loader, maxPages: 1), FetchedAt = DateTimeOffset.UtcNow };
        _entries[headKey] = entry;
        _ = entry.Task.ContinueWith(
            _ => _entries.TryRemove(new KeyValuePair<string, Entry>(headKey, entry)),
            CancellationToken.None,
            TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return await entry.Task.WaitAsync(ct);
    }

    /// <summary>True when a version supports the Minecraft version and loader (either may be null = any).</summary>
    public static bool IsCompatible(ModVersion v, string? mc, string? loader)
    {
        var okMc = string.IsNullOrEmpty(mc) || v.GameVersions.Any(g => string.Equals(g, mc, StringComparison.OrdinalIgnoreCase));
        var okLoader = string.IsNullOrEmpty(loader) || v.Loaders.Any(l => string.Equals(l, loader, StringComparison.OrdinalIgnoreCase));
        return okMc && okLoader;
    }

    /// <summary>Forgets every cached list for a mod so the next ask hits the store.</summary>
    public void Invalidate(ModSummary mod)
    {
        var prefix = $"{mod.Source}:{mod.Id}|";
        foreach (var key in _entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            _entries.TryRemove(key, out _);
    }

    public void InvalidateAll() => _entries.Clear();

    /// <summary>The changelog for one version, and whether it is Markdown (Modrinth) or HTML
    /// (CurseForge). Lists are fetched without changelogs to keep them small, so this asks for the one
    /// version on demand and remembers the answer for the session.</summary>
    public async Task<(string? Text, bool IsMarkdown)> GetChangelogAsync(ModSummary mod, ModVersion version, CancellationToken ct = default)
    {
        var isMarkdown = version.Source != ModSource.CurseForge;
        if (!string.IsNullOrWhiteSpace(version.Changelog))
            return (version.Changelog, isMarkdown);

        var key = $"{version.Source}:{version.Id}";
        if (_changelogs.TryGetValue(key, out var known)) return (known, isMarkdown);

        string? text;
        if (version.Source == ModSource.CurseForge)
        {
            if (!CurseForgeService.TryParseFileIds(mod, version, out var modId, out var fileId))
                return (null, false);
            text = await _curseForge.GetChangelogAsync(modId, fileId, ct);
        }
        else
        {
            text = await _modrinth.GetVersionChangelogAsync(version.Id, ct);
        }
        if (text is not null) _changelogs[key] = text;
        return (text, isMarkdown);
    }

    private async Task<IReadOnlyList<ModVersion>> FetchAsync(ModSummary mod, string? mcVersion, string? loader, int maxPages = 0)
    {
        List<ModVersion> list;
        if (mod.Source == ModSource.CurseForge)
        {
            if (!int.TryParse(mod.Id, out var cfId)) return Array.Empty<ModVersion>();
            list = await _curseForge.GetVersionsAsync(cfId, mcVersion, loader, maxPages);
        }
        else
        {
            list = await _modrinth.GetVersionsStrictAsync(mod.Id, mcVersion, loader);
        }
        return list.OrderByDescending(v => v.DatePublished).ToList();
    }
}
