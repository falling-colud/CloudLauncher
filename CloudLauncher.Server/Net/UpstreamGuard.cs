using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace CloudLauncher.Server.Net;

/// <summary>
/// Keeps the launcher server inside the mod platforms' rate limits, whatever the clients do.
///
/// <para>Every desktop client's store calls leave this server from one address, so CurseForge and
/// Modrinth see the sum of all users at once. On 2026-09-12 launchers checking a 460-mod pack for
/// updates sent thousands of upstream calls a minute. CurseForge answered with fast (2 ms) 403s for
/// minutes at a time, letting one request in twenty seconds through, and Modrinth with 429s — and
/// every user saw "the server API key was rejected".</para>
///
/// <para>Three things stop that here. Identical GETs are answered from a short cache, so a version
/// list three launchers ask for is fetched once. Calls that do go upstream are bounded per platform
/// — a few in flight, spaced out — so a burst becomes a queue rather than a ban, and a queue that
/// would wait too long is turned away with a 429 the client honours. And a throttle is recognised
/// as one: a 429, or a CurseForge 403 shortly after the same key was answering 200, pauses that
/// platform for everyone and reaches the client as "rate limited", not as a rejected key.</para>
/// </summary>
public sealed class UpstreamGuard
{
    private readonly IMemoryCache _cache;
    private readonly ConcurrentDictionary<string, Pace> _pace = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Largest response body worth keeping. Filtered version lists are a few KB to a few
    /// hundred; anything bigger is streamed through uncached.</summary>
    public const int MaxCacheableBytes = 4 * 1024 * 1024;

    /// <summary>A request that would have to queue longer than this is refused with a 429 instead, so
    /// clients back off rather than hold connections open for minutes.</summary>
    public static readonly TimeSpan MaxQueueWait = TimeSpan.FromSeconds(20);

    /// <summary>How recent a success must be for a CurseForge 403 to count as throttling rather than a
    /// rejected key. A key that CurseForge actually revoked never answers 200 again.</summary>
    public static readonly TimeSpan RecentSuccessWindow = TimeSpan.FromMinutes(3);

    /// <summary>Pause applied after a throttling 403 (CurseForge sends no Retry-After with those).</summary>
    public static readonly TimeSpan ThrottlePause = TimeSpan.FromSeconds(20);

    public UpstreamGuard(IMemoryCache cache) { _cache = cache; }

    private sealed class Pace
    {
        public required SemaphoreSlim Gate { get; init; }
        public required TimeSpan Spacing { get; init; }
        public readonly object Lock = new();
        public DateTimeOffset NextSlot = DateTimeOffset.MinValue;
        public DateTimeOffset BackoffUntil = DateTimeOffset.MinValue;
        public DateTimeOffset LastSuccess = DateTimeOffset.MinValue;
    }

    private Pace PaceFor(string bucket) => _pace.GetOrAdd(bucket, b => PlatformOf(b) switch
    {
        // Modrinth documents 300 requests a minute per address. CurseForge publishes no figure but
        // blocked the server at roughly 8 requests a second; 6 a second sustained stayed clean.
        "modrinth" => new Pace { Gate = new SemaphoreSlim(4, 4), Spacing = TimeSpan.FromMilliseconds(220) },
        _ => new Pace { Gate = new SemaphoreSlim(4, 4), Spacing = TimeSpan.FromMilliseconds(170) }
    });

    /// <summary>The platform a bucket belongs to: buckets are "platform" or "platform#keyhash".</summary>
    private static string PlatformOf(string bucket)
    {
        var hash = bucket.IndexOf('#');
        return (hash < 0 ? bucket : bucket[..hash]).ToLowerInvariant();
    }

    /// <summary>How many distinct per-key buckets to track before new keys share the platform bucket.
    /// Pacing state is small, but it is keyed by something a caller supplies, so it gets a ceiling.</summary>
    private const int MaxBuckets = 256;

    /// <summary>
    /// The pacing bucket for a request made under a caller's own API key. Upstream quotas are per key,
    /// so each key gets its own pace, backoff and success window rather than sharing the server key's.
    /// </summary>
    /// <remarks>The bucket name carries a truncated hash, never the key: it ends up in log lines.</remarks>
    public string BucketFor(string platform, string? key)
    {
        if (string.IsNullOrEmpty(key)) return platform;
        if (_pace.Count >= MaxBuckets) return platform;
        var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
        return $"{platform}#{Convert.ToHexString(digest)[..12].ToLowerInvariant()}";
    }

    /// <summary>Waits for an upstream send slot: honours a platform-wide pause (set after a throttle),
    /// spaces sends out, and bounds how many are in flight. Returns the wait that would be needed
    /// instead of waiting when that exceeds <see cref="MaxQueueWait"/>; null means the slot is held
    /// and must be given back with <see cref="Release"/>.</summary>
    public async Task<TimeSpan?> TryAcquireAsync(string bucket, CancellationToken ct)
    {
        var pace = PaceFor(bucket);
        TimeSpan wait;
        lock (pace.Lock)
        {
            var now = DateTimeOffset.UtcNow;
            var earliest = pace.NextSlot > pace.BackoffUntil ? pace.NextSlot : pace.BackoffUntil;
            var slot = earliest > now ? earliest : now;
            wait = slot - now;
            if (wait > MaxQueueWait) return wait;
            pace.NextSlot = slot + pace.Spacing;
        }
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
        await pace.Gate.WaitAsync(ct);
        return null;
    }

    public void Release(string bucket) => PaceFor(bucket).Gate.Release();

    /// <summary>Hold every send to the platform for <paramref name="delay"/>.</summary>
    public void BackOff(string bucket, TimeSpan delay)
    {
        var pace = PaceFor(bucket);
        lock (pace.Lock)
        {
            var until = DateTimeOffset.UtcNow + delay;
            if (until > pace.BackoffUntil) pace.BackoffUntil = until;
        }
    }

    public void NoteSuccess(string bucket)
    {
        var pace = PaceFor(bucket);
        lock (pace.Lock) pace.LastSuccess = DateTimeOffset.UtcNow;
    }

    /// <summary>True when the platform answered a request successfully within <see cref="RecentSuccessWindow"/>.</summary>
    public bool SucceededRecently(string bucket)
    {
        var pace = PaceFor(bucket);
        lock (pace.Lock) return DateTimeOffset.UtcNow - pace.LastSuccess < RecentSuccessWindow;
    }

    // ── API key lookup cache ───────────────────────────────────────────────

    private const string KeyCacheKey = "upstream:global-settings";

    /// <summary>The admin-configured keys, read from the database at most once a minute instead of on
    /// every proxied call. <see cref="InvalidateKeys"/> is called when an admin changes them.</summary>
    public async Task<(string? CurseForgeKey, string? ModrinthToken)> GetKeysAsync(
        Func<Task<(string? CurseForgeKey, string? ModrinthToken)>> load)
    {
        if (_cache.TryGetValue(KeyCacheKey, out (string?, string?) cached)) return cached;
        var fresh = await load();
        _cache.Set(KeyCacheKey, fresh, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1),
            Size = 256
        });
        return fresh;
    }

    public void InvalidateKeys() => _cache.Remove(KeyCacheKey);

    // ── response cache ─────────────────────────────────────────────────────

    public sealed record CachedResponse(int StatusCode, string ContentType, byte[] Body);

    /// <summary>How long a successful GET may be answered from cache, or null for "never cache".
    /// Searches change as new mods appear, so they live a short while; a project's file list or
    /// summary changes when its author publishes, which is rare against the rate it is asked for.</summary>
    public static TimeSpan? CacheTtl(string platform, string pathAndQuery)
    {
        var p = pathAndQuery.Trim('/').ToLowerInvariant();
        if (platform.Equals("curseforge", StringComparison.OrdinalIgnoreCase))
        {
            if (p.StartsWith("mods/search")) return TimeSpan.FromSeconds(90);
            if (p.StartsWith("categories")) return TimeSpan.FromHours(6);
            if (p.Contains("/download-url")) return TimeSpan.FromMinutes(10);
            if (p.Contains("/changelog")) return TimeSpan.FromHours(6);
            if (p.StartsWith("mods/")) return TimeSpan.FromMinutes(10); // mods/{id}, mods/{id}/files, mods/{id}/description
            return TimeSpan.FromMinutes(2);
        }
        if (platform.Equals("modrinth", StringComparison.OrdinalIgnoreCase))
        {
            if (p.StartsWith("search")) return TimeSpan.FromSeconds(90);
            if (p.StartsWith("tag/")) return TimeSpan.FromHours(6);
            if (p.StartsWith("project/") || p.StartsWith("projects") || p.StartsWith("version/")) return TimeSpan.FromMinutes(10);
            return TimeSpan.FromMinutes(2);
        }
        return null;
    }

    public static string CacheKey(string platform, string pathAndQuery) => $"upstream:{platform.ToLowerInvariant()}:{pathAndQuery}";

    public bool TryGetCached(string key, out CachedResponse response)
    {
        if (_cache.TryGetValue(key, out CachedResponse? hit) && hit is not null)
        {
            response = hit;
            return true;
        }
        response = null!;
        return false;
    }

    public void Store(string key, CachedResponse response, TimeSpan ttl)
    {
        _cache.Set(key, response, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ttl,
            Size = response.Body.Length + 256
        });
    }
}
