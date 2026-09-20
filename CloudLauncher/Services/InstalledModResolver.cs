namespace CloudLauncher.Services;

/// <summary>The platform identities a single installed jar resolves to. A jar that
/// is published to both stores carries one identity per store; recording both lets
/// callers recognise the mod as already installed no matter which store's listing
/// the user is browsing.</summary>
public sealed record InstalledModIdentity(
    string Path,
    (ModSummary Mod, ModVersion Version)? Modrinth,
    (ModSummary Mod, ModVersion Version)? CurseForge);

/// <summary>The outcome of one resolve pass.
///
/// <see cref="Complete"/> is false when a store lookup failed, which makes a missing identity mean
/// "not known yet" rather than "not installed". Callers must not treat a degraded pass as an
/// authoritative picture and overwrite what they already knew — doing so is what made installed
/// mods flip back to "downloadable" whenever an API hiccupped. It covers the two network lookups
/// only: a single unreadable jar leaves its own identity empty without invalidating the rest.</summary>
public sealed record InstalledModIndex(IReadOnlyList<InstalledModIdentity> Identities, bool Complete);

/// <summary>
/// Resolves installed mod jars to their Modrinth <em>and</em> CurseForge identities.
///
/// Matching is hash-based — SHA-512 for Modrinth, Murmur2 for CurseForge — so it
/// never mistakes one mod for another. It is cache-first with a network fallback,
/// and guards against the fingerprint cache's single-match entries (which can carry
/// the <em>other</em> store's identity) by re-querying when the cached match's source
/// doesn't line up with the store being resolved.
/// </summary>
public static class InstalledModResolver
{
    public static async Task<InstalledModIndex> ResolveAsync(
        IReadOnlyCollection<string> jarPaths,
        ModFingerprintCache cache,
        ModrinthService modrinth,
        CurseForgeService curseForge,
        CancellationToken ct = default)
    {
        var pathToSha = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pathToFingerprint = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var toHash = new List<string>();

        foreach (var path in jarPaths)
        {
            if (cache.TryGet(path, out var entry))
            {
                if (!string.IsNullOrEmpty(entry.Sha512)) pathToSha[path] = entry.Sha512;
                if (entry.CurseForgeFingerprint != 0) pathToFingerprint[path] = entry.CurseForgeFingerprint;
            }
            else
            {
                toHash.Add(path);
            }
        }

        if (toHash.Count > 0)
        {
            await Task.Run(() =>
            {
                Parallel.ForEach(
                    toHash,
                    new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                    path =>
                    {
                        try
                        {
                            var (sha512, fingerprint) = ModFingerprintCache.ComputeHashes(path);
                            cache.Store(path, sha512, fingerprint);
                            lock (pathToSha) pathToSha[path] = sha512;
                            lock (pathToFingerprint) pathToFingerprint[path] = fingerprint;
                        }
                        catch { /* locked or unreadable jar — leave it unmatched */ }
                    });
            }, ct);
        }

        var (modrinthMatches, modrinthComplete) =
            await ResolveModrinthAsync(cache, modrinth, pathToSha.Values, ct);
        var (curseForgeMatches, curseForgeComplete) =
            await ResolveCurseForgeAsync(cache, curseForge, pathToFingerprint.Values, ct);

        var results = new List<InstalledModIdentity>(jarPaths.Count);
        foreach (var path in jarPaths)
        {
            (ModSummary Mod, ModVersion Version)? modrinthId =
                pathToSha.TryGetValue(path, out var sha)
                && modrinthMatches.TryGetValue(sha, out var mr)
                && mr.mod.Source == ModSource.Modrinth
                    ? mr : null;

            (ModSummary Mod, ModVersion Version)? curseForgeId =
                pathToFingerprint.TryGetValue(path, out var fingerprint)
                && curseForgeMatches.TryGetValue(fingerprint, out var cf)
                && cf.mod.Source == ModSource.CurseForge
                    ? cf : null;

            // Warm the shared cache so other views and later sessions resolve this jar without a
            // network round-trip: a primary match (Modrinth preferred, kept for older readers) plus
            // the identity on each store, which is what lets a cross-listed jar load with both.
            if (modrinthId is { } m) cache.StoreMatch(path, m.Mod, m.Version);
            else if (curseForgeId is { } c) cache.StoreMatch(path, c.Mod, c.Version);
            cache.StoreStoreMatches(path, modrinthId, curseForgeId);

            results.Add(new InstalledModIdentity(path, modrinthId, curseForgeId));
        }

        cache.Flush();
        return new InstalledModIndex(results, modrinthComplete && curseForgeComplete);
    }

    private static async Task<(Dictionary<string, (ModSummary mod, ModVersion version)> Matches, bool Complete)>
        ResolveModrinthAsync(
            ModFingerprintCache cache, ModrinthService modrinth, IEnumerable<string> shaHashes, CancellationToken ct)
    {
        var matches = cache.ResolveModrinthMatches(shaHashes);
        var missing = shaHashes
            .Where(h => !(matches.TryGetValue(h, out var m) && m.mod.Source == ModSource.Modrinth))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missing.Count > 0)
        {
            try
            {
                var fresh = await modrinth.MatchHashesAsync(missing, ct);
                foreach (var (hash, match) in fresh)
                {
                    matches[hash] = match;
                    cache.RememberModrinthMatch(hash, match.mod, match.version);
                }
            }
            // Offline or API error — keep whatever resolved from cache, but say the pass is partial
            // so the caller treats the gaps as unknown rather than as "not installed".
            catch { return (matches, false); }
        }
        return (matches, true);
    }

    private static async Task<(Dictionary<long, (ModSummary mod, ModVersion version)> Matches, bool Complete)>
        ResolveCurseForgeAsync(
            ModFingerprintCache cache, CurseForgeService curseForge, IEnumerable<long> fingerprints, CancellationToken ct)
    {
        var matches = cache.ResolveCurseForgeMatches(fingerprints);
        var missing = fingerprints
            .Where(f => !(matches.TryGetValue(f, out var m) && m.mod.Source == ModSource.CurseForge))
            .Distinct()
            .ToList();
        if (missing.Count > 0)
        {
            try
            {
                var fresh = await curseForge.MatchFingerprintsAsync(missing, ct);
                foreach (var (fingerprint, match) in fresh)
                {
                    matches[fingerprint] = match;
                    cache.RememberCurseForgeMatch(fingerprint, match.mod, match.version);
                }
            }
            catch { return (matches, false); }
        }
        return (matches, true);
    }
}
