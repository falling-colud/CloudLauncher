using System.Collections.Concurrent;
using System.Diagnostics;

namespace CloudLauncher.Services;

/// <summary>One installed mod an update check asks about: its store identity, the installed version
/// and, for Modrinth, the SHA-512 of a file of that version, which the bulk lookup is keyed by.</summary>
public readonly record struct UpdateCheckItem(ModSummary Mod, ModVersion Installed, string? Sha512);

/// <summary>What one bulk pass of an update check (<see cref="ModVersionCatalog.PrefetchLatestAsync"/>)
/// did: which mods it answered, what it cost, and why a store's part of it failed, if one did.</summary>
public sealed class BulkCheckReport
{
    private readonly ConcurrentDictionary<string, ModSource> _answered = new(StringComparer.OrdinalIgnoreCase);
    private int _requests;
    private string? _modrinthProblem, _curseForgeProblem;

    /// <summary>A report for a pass that asked nothing.</summary>
    public static BulkCheckReport None => new();

    /// <summary>Distinct mods handed in.</summary>
    public int Asked { get; internal set; }

    /// <summary>Mods left out because the session already had a fresh answer for them.</summary>
    public int AlreadyKnown { get; internal set; }

    /// <summary>Mods this pass answered.</summary>
    public int Answered => _answered.Count;

    public int AnsweredByModrinth => _answered.Values.Count(s => s == ModSource.Modrinth);
    public int AnsweredByCurseForge => _answered.Values.Count(s => s == ModSource.CurseForge);

    /// <summary>Bulk requests sent, including any that failed.</summary>
    public int Requests => Volatile.Read(ref _requests);

    public TimeSpan Elapsed { get; internal set; }

    /// <summary>Why the Modrinth part failed, or null. Its mods are then asked about one at a time.</summary>
    public string? ModrinthProblem => _modrinthProblem;

    /// <summary>Why the CurseForge part failed, or null. Its mods are then asked about one at a time.</summary>
    public string? CurseForgeProblem => _curseForgeProblem;

    /// <summary>True when this pass answered <paramref name="mod"/>. Its answer is then as fresh as a
    /// forced refresh would make it, so the check does not ask about it again.</summary>
    public bool IsAnswered(ModSummary mod) => _answered.ContainsKey(IdKey(mod));

    internal bool IsAnswered(string idKey) => _answered.ContainsKey(idKey);
    internal void MarkAnswered(ModSummary mod) => _answered[IdKey(mod)] = mod.Source;
    internal void CountRequest() => Interlocked.Increment(ref _requests);

    internal void NoteProblem(ModSource source, Exception ex)
    {
        if (source == ModSource.CurseForge) Interlocked.CompareExchange(ref _curseForgeProblem, ex.Message, null);
        else Interlocked.CompareExchange(ref _modrinthProblem, ex.Message, null);
    }

    internal static string IdKey(ModSummary mod) => $"{mod.Source}:{mod.Id}";
}

/// <summary>
/// The one place every view asks for a mod's published versions, so a big pack costs one round of
/// store calls per quarter hour instead of one per view, tab switch and re-scan.
///
/// <para>The launcher server proxies every store call from one address for all users, so excess
/// calls can get that address throttled for everyone.</para>
///
/// <para>Lists are cached per (mod, Minecraft version, loader). Filtered requests are sent filtered or
/// served from a cached unfiltered list. In-flight fetches are shared, and failures aren't cached.</para>
///
/// <para>Update checks only need the newest version per channel, which both stores return in bulk:
/// <see cref="PrefetchLatestAsync"/> fetches that first into a separate slot (never in place of a
/// full list), and <see cref="GetUpdateCandidatesAsync"/> reads it before falling back to a list.</para>
/// </summary>
public sealed class ModVersionCatalog
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    /// <summary>Installed files per Modrinth bulk request. 382 hashes make a 50 KB request and a 770 KB
    /// answer, well inside the launcher server's 1 MB limit on a POST body it can replay on another
    /// route.</summary>
    private const int HashesPerRequest = 400;

    /// <summary>Fewer mods than this on a store are asked about one at a time: a bulk pass costs up to
    /// three requests the server never caches, a single list request often comes from its cache.</summary>
    private const int MinBulk = 3;

    private readonly ModrinthService _modrinth;
    private readonly CurseForgeService _curseForge;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _changelogs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The bulk pass's answers: per (mod, Minecraft version, loader), the newest version of
    /// each release channel. Kept apart from the version lists, since a caller that wants every release
    /// between two versions must not be handed just these.</summary>
    private readonly ConcurrentDictionary<string, Candidates> _candidates = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Entry
    {
        public required Task<IReadOnlyList<ModVersion>> Task { get; init; }
        public required DateTimeOffset FetchedAt { get; init; }
        public bool IsFresh => !Task.IsFaulted && !Task.IsCanceled && DateTimeOffset.UtcNow - FetchedAt < Ttl;
    }

    private sealed record Candidates(IReadOnlyList<ModVersion> Versions, DateTimeOffset FetchedAt)
    {
        public bool IsFresh => DateTimeOffset.UtcNow - FetchedAt < Ttl;
    }

    public ModVersionCatalog(ModrinthService modrinth, CurseForgeService curseForge)
    {
        _modrinth = modrinth;
        _curseForge = curseForge;
    }

    private static string KeyFor(ModSummary mod, string? mc, string? loader) =>
        $"{mod.Source}:{mod.Id}|{mc?.Trim().ToLowerInvariant()}|{loader?.Trim().ToLowerInvariant()}";

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The published versions of <paramref name="mod"/>, newest first: every version when
    /// <paramref name="mcVersion"/> and <paramref name="loader"/> are null, otherwise only those for that
    /// Minecraft version and loader. <paramref name="forceRefresh"/> bypasses the cache.</summary>
    public Task<IReadOnlyList<ModVersion>> GetVersionsAsync(ModSummary mod, string? mcVersion = null,
        string? loader = null, bool forceRefresh = false, CancellationToken ct = default) =>
        GetVersionsCoreAsync(mod, mcVersion, loader, forceRefresh, ProxyPacing.Default, ct);

    private async Task<IReadOnlyList<ModVersion>> GetVersionsCoreAsync(ModSummary mod, string? mcVersion,
        string? loader, bool forceRefresh, ProxyPacing pacing, CancellationToken ct)
    {
        mcVersion = Clean(mcVersion);
        loader = Clean(loader);
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
        var entry = new Entry { Task = FetchAsync(mod, mcVersion, loader, 0, pacing), FetchedAt = DateTimeOffset.UtcNow };
        _entries[key] = entry;
        _ = entry.Task.ContinueWith(
            _ => _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry)),
            CancellationToken.None,
            TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return await entry.Task.WaitAsync(ct);
    }

    /// <summary>The newest versions for a Minecraft version and loader, enough to tell whether there is
    /// an update. For Modrinth that is the whole filtered list (one request either way); for CurseForge
    /// it is the first page of fifty instead of the mod's whole history. A cached full list is used when
    /// there is one. The bulk pass's answers aren't served from here because the update review reads
    /// the versions an update spans from this list.</summary>
    public Task<IReadOnlyList<ModVersion>> GetLatestVersionsAsync(ModSummary mod, string? mcVersion,
        string? loader, bool forceRefresh = false, CancellationToken ct = default) =>
        GetLatestCoreAsync(mod, mcVersion, loader, forceRefresh, ProxyPacing.Default, ct);

    private async Task<IReadOnlyList<ModVersion>> GetLatestCoreAsync(ModSummary mod, string? mcVersion,
        string? loader, bool forceRefresh, ProxyPacing pacing, CancellationToken ct)
    {
        if (mod.Source != ModSource.CurseForge)
            return await GetVersionsCoreAsync(mod, mcVersion, loader, forceRefresh, pacing, ct);

        mcVersion = Clean(mcVersion);
        loader = Clean(loader);
        var fullKey = KeyFor(mod, mcVersion, loader);
        var headKey = fullKey + "|head";
        if (!forceRefresh)
        {
            if (_entries.TryGetValue(fullKey, out var full) && full.IsFresh && full.Task.IsCompletedSuccessfully)
                return full.Task.Result;
            if (_entries.TryGetValue(headKey, out var head) && head.IsFresh)
                return await head.Task.WaitAsync(ct);
        }

        var entry = new Entry { Task = FetchAsync(mod, mcVersion, loader, 1, pacing), FetchedAt = DateTimeOffset.UtcNow };
        _entries[headKey] = entry;
        _ = entry.Task.ContinueWith(
            _ => _entries.TryRemove(new KeyValuePair<string, Entry>(headKey, entry)),
            CancellationToken.None,
            TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return await entry.Task.WaitAsync(ct);
    }

    // ── update checks ────────────────────────────────────────────────────────

    /// <summary>
    /// The versions an update check chooses from: the bulk pass's newest-per-channel answer when there
    /// is a fresh one, otherwise <see cref="GetLatestVersionsAsync"/>'s list, fetched if needed at the
    /// update-check pace (<see cref="AppSettings.ModUpdateChecksPerSecond"/>).
    /// </summary>
    /// <remarks>Either answer picks the same update: the newest version a channel admits is the newest
    /// of the channels it admits, and the bulk answer holds those.</remarks>
    public async Task<IReadOnlyList<ModVersion>> GetUpdateCandidatesAsync(ModSummary mod, string? mcVersion,
        string? loader, bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh && TryGetCachedCandidates(mod, mcVersion, loader, out var known)) return known;
        return await GetLatestCoreAsync(mod, mcVersion, loader, forceRefresh, ProxyPacing.UpdateCheck, ct);
    }

    /// <summary>What <see cref="GetUpdateCandidatesAsync"/> would answer without the network, if it
    /// can: a fresh bulk answer, or a fresh list that has already arrived.</summary>
    public bool TryGetCachedCandidates(ModSummary mod, string? mcVersion, string? loader,
        out IReadOnlyList<ModVersion> versions)
    {
        mcVersion = Clean(mcVersion);
        loader = Clean(loader);
        var key = KeyFor(mod, mcVersion, loader);

        if (_candidates.TryGetValue(key, out var bulk) && bulk.IsFresh)
        {
            versions = bulk.Versions;
            return true;
        }
        if (_entries.TryGetValue(key, out var full) && full.IsFresh && full.Task.IsCompletedSuccessfully)
        {
            versions = full.Task.Result;
            return true;
        }
        if (mod.Source == ModSource.CurseForge
            && _entries.TryGetValue(key + "|head", out var head) && head.IsFresh && head.Task.IsCompletedSuccessfully)
        {
            versions = head.Task.Result;
            return true;
        }
        if ((mcVersion is not null || loader is not null)
            && _entries.TryGetValue(KeyFor(mod, null, null), out var whole)
            && whole.IsFresh && whole.Task.IsCompletedSuccessfully)
        {
            versions = whole.Task.Result.Where(v => IsCompatible(v, mcVersion, loader)).ToList();
            return true;
        }
        versions = Array.Empty<ModVersion>();
        return false;
    }

    /// <summary>
    /// Asks the stores about many mods at once before an update check goes through them one by one,
    /// so the one-by-one pass mostly reads answers that are already here.
    /// </summary>
    /// <remarks>
    /// <para>Modrinth: the newest version per installed file hash (up to <see cref="HashesPerRequest"/> per
    /// request), unfiltered first, then with a channel filter for mods whose newest version isn't a
    /// release. Files missing from the unfiltered answer are left to the one-by-one pass.</para>
    /// <para>CurseForge: <c>latestFilesIndexes</c> from <c>POST /mods</c>, and details of files that
    /// aren't installed from <c>POST /mods/files</c>, fifty at a time. Mods whose index doesn't cover
    /// this pack, or has no release for it, are left to the one-by-one pass.</para>
    /// <para>Only cancellation throws; a failed store leaves its mods unanswered and the report says why.
    /// Mods with a fresh answer are skipped unless <paramref name="forceRefresh"/> is set.</para>
    /// </remarks>
    public async Task<BulkCheckReport> PrefetchLatestAsync(IReadOnlyList<UpdateCheckItem> items, string? mcVersion,
        string? loader, bool forceRefresh = false, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        var report = new BulkCheckReport();
        mcVersion = Clean(mcVersion);
        loader = Clean(loader);

        var byMod = new Dictionary<string, List<UpdateCheckItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var key = KeyFor(item.Mod, mcVersion, loader);
            if (!byMod.TryGetValue(key, out var group)) byMod[key] = group = new List<UpdateCheckItem>();
            group.Add(item);
        }
        report.Asked = byMod.Count;
        // Both stores' bulk answers are per Minecraft version; without one there is nothing to filter by.
        if (mcVersion is null) return Finish();

        var modrinth = new List<(string Key, UpdateCheckItem Item, string Hash)>();
        var curseForge = new List<(string Key, UpdateCheckItem Item, int ModId)>();
        foreach (var (key, group) in byMod)
        {
            var mod = group[0].Mod;
            if (!forceRefresh && TryGetCachedCandidates(mod, mcVersion, loader, out _))
            {
                report.AlreadyKnown++;
                continue;
            }
            if (mod.Source == ModSource.Modrinth)
            {
                foreach (var item in group)
                    if (item.Sha512 is { Length: > 0 } hash)
                        modrinth.Add((key, item, hash.Trim().ToLowerInvariant()));
            }
            else if (mod.Source == ModSource.CurseForge && int.TryParse(mod.Id, out var modId) && modId > 0)
            {
                curseForge.Add((key, group[0], modId));
            }
        }

        var work = new List<Task>();
        if (modrinth.Select(w => w.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= MinBulk)
            work.Add(PrefetchModrinthAsync(modrinth, mcVersion, loader, report, ct));
        if (curseForge.Count >= MinBulk)
            work.Add(PrefetchCurseForgeAsync(curseForge, mcVersion, loader, report, ct));
        await Task.WhenAll(work);
        return Finish();

        BulkCheckReport Finish()
        {
            report.Elapsed = clock.Elapsed;
            return report;
        }
    }

    private async Task PrefetchModrinthAsync(List<(string Key, UpdateCheckItem Item, string Hash)> work,
        string mcVersion, string? loader, BulkCheckReport report, CancellationToken ct)
    {
        var byHash = work
            .GroupBy(w => w.Hash, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        await Task.WhenAll(byHash.Keys.Chunk(HashesPerRequest)
            .Select(chunk => PrefetchModrinthChunkAsync(chunk, byHash, mcVersion, loader, report, ct)));
    }

    private async Task PrefetchModrinthChunkAsync(string[] hashes,
        Dictionary<string, List<(string Key, UpdateCheckItem Item, string Hash)>> byHash,
        string mcVersion, string? loader, BulkCheckReport report, CancellationToken ct)
    {
        var newest = await AskModrinthAsync(hashes, null);
        if (newest is null) return;

        // The unfiltered answer is the newest version on any channel. When that is a release it is also
        // the newest release and the newest release-or-beta, so only the rest need asking again.
        var needRelease = hashes.Where(h => newest.TryGetValue(h, out var n) && Rank(n.Version) < 2).ToArray();
        var needBeta = hashes.Where(h => newest.TryGetValue(h, out var n) && Rank(n.Version) < 1).ToArray();
        var releaseAsk = needRelease.Length == 0 ? null : AskModrinthAsync(needRelease, [ModUpdateChannel.Release]);
        var betaAsk = needBeta.Length == 0 ? null : AskModrinthAsync(needBeta, [ModUpdateChannel.Release, ModUpdateChannel.Beta]);
        var releases = releaseAsk is null ? null : await releaseAsk;
        var betas = betaAsk is null ? null : await betaAsk;

        foreach (var hash in hashes)
        {
            if (!newest.TryGetValue(hash, out var top)) continue; // not ours to answer: asked on its own
            foreach (var (key, item, _) in byHash[hash])
            {
                if (report.IsAnswered(BulkCheckReport.IdKey(item.Mod))) continue;
                // A file of some other project would answer for the wrong mod.
                if (!string.Equals(top.ProjectId, item.Mod.Id, StringComparison.Ordinal)) continue;

                var candidates = new List<ModVersion> { top.Version };
                if (Rank(top.Version) < 1)
                {
                    if (betas is null) continue; // the follow-up failed, so the answer is incomplete
                    if (betas.TryGetValue(hash, out var b) && b.ProjectId == top.ProjectId) candidates.Add(b.Version);
                }
                if (Rank(top.Version) < 2)
                {
                    if (releases is null) continue;
                    if (releases.TryGetValue(hash, out var r) && r.ProjectId == top.ProjectId) candidates.Add(r.Version);
                }
                StoreCandidates(key, candidates);
                report.MarkAnswered(item.Mod);
            }
        }

        async Task<Dictionary<string, (string ProjectId, ModVersion Version)>?> AskModrinthAsync(
            IReadOnlyCollection<string> ask, IReadOnlyCollection<string>? channels)
        {
            try
            {
                return await _modrinth.GetLatestVersionsByHashAsync(ask, mcVersion, loader, channels, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                report.NoteProblem(ModSource.Modrinth, ex);
                AppLog.Log("updates", $"Bulk lookup of {ask.Count} file(s): {ex.Message}");
                return null;
            }
            finally { report.CountRequest(); }
        }
    }

    private async Task PrefetchCurseForgeAsync(List<(string Key, UpdateCheckItem Item, int ModId)> work,
        string mcVersion, string? loader, BulkCheckReport report, CancellationToken ct)
    {
        // Every mod's index, fifty to a request.
        var indexes = new ConcurrentDictionary<int, IReadOnlyList<CurseForgeFileIndex>>();
        await Task.WhenAll(work.Select(w => w.ModId).Distinct().Chunk(CurseForgeService.BatchSize)
            .Select(chunk => AskCurseForgeAsync("index", chunk.Length, async () =>
            {
                foreach (var (id, index) in await _curseForge.GetLatestFileIndexesAsync(chunk, ct))
                    indexes[id] = index;
            })));

        // The newest file of each release type for this pack, per mod. A mod whose index doesn't cover
        // the pack's version and loader, or names no release for it, is left to the one-by-one pass.
        // CurseForge doesn't document whether the index keeps the newest file per release type or only
        // the newest file overall. Both readings agree when the index lists a release, so only then is it
        // taken as the whole answer.
        var plans = new List<(string Key, UpdateCheckItem Item, IReadOnlyList<int> FileIds, int Installed)>();
        foreach (var (key, item, modId) in work)
        {
            if (!indexes.TryGetValue(modId, out var index)) continue;
            var newest = CurseForgeService.NewestFilesFor(index, mcVersion, loader);
            if (!newest.Any(e => e.ReleaseType == 1)) continue;
            CurseForgeService.TryParseFileIds(item.Mod, item.Installed, out _, out var installedFileId);
            plans.Add((key, item, newest.Select(e => e.FileId).Distinct().ToList(), installedFileId));
        }

        // Details for the ones that are not already installed, fifty to a request.
        var files = new ConcurrentDictionary<int, ModVersion>();
        await Task.WhenAll(plans
            .SelectMany(p => p.FileIds.Where(id => id != p.Installed))
            .Distinct()
            .Chunk(CurseForgeService.BatchSize)
            .Select(chunk => AskCurseForgeAsync("file details", chunk.Length, async () =>
            {
                foreach (var (id, version) in await _curseForge.GetFilesAsync(chunk, ct))
                    files[id] = version;
            })));

        foreach (var (key, item, fileIds, installed) in plans)
        {
            var candidates = new List<ModVersion>(fileIds.Count);
            foreach (var id in fileIds)
            {
                if (id == installed) candidates.Add(item.Installed);
                else if (files.TryGetValue(id, out var v) && v.Id.StartsWith(item.Mod.Id + ":", StringComparison.Ordinal))
                    candidates.Add(v);
                else break; // a file whose details did not arrive: the answer would be incomplete
            }
            if (candidates.Count != fileIds.Count) continue;
            StoreCandidates(key, candidates);
            report.MarkAnswered(item.Mod);
        }

        async Task AskCurseForgeAsync(string what, int count, Func<Task> ask)
        {
            try { await ask(); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                report.NoteProblem(ModSource.CurseForge, ex);
                AppLog.Log("updates", $"Bulk {what} lookup of {count} item(s): {ex.Message}");
            }
            finally { report.CountRequest(); }
        }
    }

    private void StoreCandidates(string key, List<ModVersion> candidates) =>
        _candidates[key] = new Candidates(
            candidates.GroupBy(v => v.Id).Select(g => g.First()).OrderByDescending(v => v.DatePublished).ToList(),
            DateTimeOffset.UtcNow);

    private static int Rank(ModVersion version) => ModUpdateChannel.Rank(version.ReleaseChannel);

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>True when a version supports the Minecraft version and loader (either may be null,
    /// meaning any).</summary>
    public static bool IsCompatible(ModVersion v, string? mc, string? loader)
    {
        var okMc = string.IsNullOrEmpty(mc) || v.GameVersions.Any(g => string.Equals(g, mc, StringComparison.OrdinalIgnoreCase));
        var okLoader = string.IsNullOrEmpty(loader) || v.Loaders.Any(l => string.Equals(l, loader, StringComparison.OrdinalIgnoreCase));
        return okMc && okLoader;
    }

    /// <summary>Forgets every cached list and bulk answer for a mod so the next ask hits the store.</summary>
    public void Invalidate(ModSummary mod)
    {
        var prefix = $"{mod.Source}:{mod.Id}|";
        foreach (var key in _entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            _entries.TryRemove(key, out _);
        foreach (var key in _candidates.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            _candidates.TryRemove(key, out _);
    }

    public void InvalidateAll()
    {
        _entries.Clear();
        _candidates.Clear();
    }

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

    private async Task<IReadOnlyList<ModVersion>> FetchAsync(ModSummary mod, string? mcVersion, string? loader,
        int maxPages, ProxyPacing pacing)
    {
        List<ModVersion> list;
        if (mod.Source == ModSource.CurseForge)
        {
            if (!int.TryParse(mod.Id, out var cfId)) return Array.Empty<ModVersion>();
            list = await _curseForge.GetVersionsAsync(cfId, mcVersion, loader, maxPages, CancellationToken.None, pacing);
        }
        else
        {
            list = await _modrinth.GetVersionsStrictAsync(mod.Id, mcVersion, loader, CancellationToken.None, pacing);
        }
        return list.OrderByDescending(v => v.DatePublished).ToList();
    }
}
