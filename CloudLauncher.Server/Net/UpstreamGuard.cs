using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;

namespace CloudLauncher.Server.Net;

/// <summary>
/// Keeps the launcher server inside the mod platforms' rate limits, whatever the clients do.
/// </summary>
/// <remarks>
/// <para>Every client's store calls leave from this server's address, so a few big update checks
/// can get it throttled: CurseForge answers with minutes of fast 403s, Modrinth with 429s.</para>
/// <para>So identical GETs are cached briefly; upstream calls are paced per bucket, and a queue that
/// would wait too long gets a 429; a 429, or a CurseForge 403 shortly after a 200, pauses the bucket
/// and is reported as rate limiting; and each account has its own limits (<see cref="Users"/>).</para>
/// <para>Buckets: one per platform for the server's key, one shared by user CurseForge keys not yet
/// answered 200, and one each for a bounded number of recently used keys that have.</para>
/// </remarks>
public sealed class UpstreamGuard
{
    private readonly IMemoryCache _cache;

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

    // Modrinth documents 300 requests a minute per address. CurseForge publishes no figure; about
    // 8 a second gets blocked, 6 a second sustained is fine.
    private static readonly TimeSpan ModrinthSpacing = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan CurseForgeSpacing = TimeSpan.FromMilliseconds(170);

    /// <summary>How many user-supplied keys keep a bucket of their own. The least recently used one
    /// goes back to the untested bucket when a new key needs a place.</summary>
    private const int MaxOwnKeyBuckets = 256;

    public UpstreamGuard(IMemoryCache cache) { _cache = cache; }

    /// <summary>Per-account request rate and queue caps for the proxy.</summary>
    public ProxyUserLimits Users { get; } = new();

    /// <summary>One pacing unit with its own in-flight limit, spacing, pause and success window. A
    /// request holds its bucket for its whole life, so a bucket evicted meanwhile still gets its slot
    /// back.</summary>
    public sealed class Bucket
    {
        internal Bucket(string name, TimeSpan spacing, bool manyKeys = false)
        {
            Name = name;
            Spacing = spacing;
            ManyKeys = manyKeys;
        }

        /// <summary>"curseforge", "modrinth", "curseforge#untested" or "curseforge#{key hash}". Never
        /// contains a key: it ends up in log lines.</summary>
        public string Name { get; }

        /// <summary>True for the untested-key bucket, which carries many callers' keys: a "too many"
        /// answered to one of them says nothing about the others, so it is never paused.</summary>
        public bool ManyKeys { get; }

        internal TimeSpan Spacing { get; }
        internal readonly SemaphoreSlim Gate = new(4, 4);
        internal readonly object Lock = new();
        internal DateTimeOffset NextSlot = DateTimeOffset.MinValue;
        internal DateTimeOffset BackoffUntil = DateTimeOffset.MinValue;
        internal DateTimeOffset LastSuccess = DateTimeOffset.MinValue;

        public override string ToString() => Name;
    }

    private readonly Bucket _curseForge = new("curseforge", CurseForgeSpacing);
    private readonly Bucket _modrinth = new("modrinth", ModrinthSpacing);
    private readonly Bucket _untestedKeys = new("curseforge#untested", CurseForgeSpacing, manyKeys: true);

    private readonly object _ownKeysLock = new();
    private readonly Dictionary<string, LinkedListNode<(string Id, Bucket Bucket)>> _ownKeys = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Id, Bucket Bucket)> _ownKeyOrder = new(); // most recently used first

    /// <summary>The bucket for calls made with the server's own key (or, for Modrinth, no key).</summary>
    public Bucket SharedBucket(string platform) => platform switch
    {
        "curseforge" => _curseForge,
        "modrinth" => _modrinth,
        _ => throw new ArgumentException($"Unknown platform '{platform}'.", nameof(platform))
    };

    /// <summary>
    /// The bucket for a CurseForge call made with a caller's own key: its own once the key has been
    /// answered 200 here (see <see cref="NoteOwnKeySuccess"/>), until then the one shared by every
    /// untested key.
    /// </summary>
    /// <remarks>Keys are caller-supplied, so an unproven key gets nothing of its own: a stream of made-up
    /// keys shares one bucket and cannot grow this list or touch the shared key's pacing.</remarks>
    public Bucket BucketForOwnKey(string key)
    {
        var id = KeyId(key);
        lock (_ownKeysLock)
        {
            if (!_ownKeys.TryGetValue(id, out var node)) return _untestedKeys;
            _ownKeyOrder.Remove(node);
            _ownKeyOrder.AddFirst(node);
            return node.Value.Bucket;
        }
    }

    /// <summary>Gives a caller's key a bucket of its own after CurseForge answered it with a success.</summary>
    public void NoteOwnKeySuccess(string key)
    {
        var id = KeyId(key);
        lock (_ownKeysLock)
        {
            if (_ownKeys.ContainsKey(id)) return;
            _ownKeys[id] = _ownKeyOrder.AddFirst((id, new Bucket($"curseforge#{id[..12]}", CurseForgeSpacing)));
            while (_ownKeys.Count > MaxOwnKeyBuckets && _ownKeyOrder.Last is { } oldest)
            {
                _ownKeyOrder.RemoveLast();
                _ownKeys.Remove(oldest.Value.Id);
            }
        }
    }

    private static string KeyId(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    /// <summary>Waits for an upstream send slot: honours the bucket's pause (set after a throttle),
    /// spaces sends out, and bounds how many are in flight. Returns the wait that would be needed
    /// instead of waiting when that exceeds <see cref="MaxQueueWait"/>; null means the slot is held
    /// and must be given back with <see cref="Release"/>.</summary>
    public async Task<TimeSpan?> TryAcquireAsync(Bucket bucket, CancellationToken ct)
    {
        TimeSpan wait;
        lock (bucket.Lock)
        {
            var now = DateTimeOffset.UtcNow;
            var earliest = bucket.NextSlot > bucket.BackoffUntil ? bucket.NextSlot : bucket.BackoffUntil;
            var slot = earliest > now ? earliest : now;
            wait = slot - now;
            if (wait > MaxQueueWait) return wait;
            bucket.NextSlot = slot + bucket.Spacing;
        }
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
        await bucket.Gate.WaitAsync(ct);
        return null;
    }

    public void Release(Bucket bucket) => bucket.Gate.Release();

    /// <summary>Hold every send in the bucket for <paramref name="delay"/>.</summary>
    public void BackOff(Bucket bucket, TimeSpan delay)
    {
        lock (bucket.Lock)
        {
            var until = DateTimeOffset.UtcNow + delay;
            if (until > bucket.BackoffUntil) bucket.BackoffUntil = until;
        }
    }

    public void NoteSuccess(Bucket bucket)
    {
        lock (bucket.Lock) bucket.LastSuccess = DateTimeOffset.UtcNow;
    }

    /// <summary>True when the bucket's key was answered successfully within <see cref="RecentSuccessWindow"/>.</summary>
    public bool SucceededRecently(Bucket bucket)
    {
        lock (bucket.Lock) return DateTimeOffset.UtcNow - bucket.LastSuccess < RecentSuccessWindow;
    }

    // ── API key lookup cache ───────────────────────────────────────────────

    private const string KeyCacheKey = "upstream:curseforge-key";

    /// <summary>The key the database gave last time it answered, so a database that is restarting
    /// does not take the CurseForge proxy down with it.</summary>
    private volatile string? _lastKnownKey;
    private volatile bool _keyEverLoaded;

    /// <summary>The admin-configured CurseForge key, read from the database at most once a minute
    /// instead of on every proxied call. <see cref="InvalidateKeys"/> is called when an admin changes it.
    /// While the database is not answering, the key it gave last time is used and asked for again on
    /// the next call; only a server that has never read it fails the call.</summary>
    public async Task<string?> GetCurseForgeKeyAsync(Func<Task<string?>> load)
    {
        if (_cache.TryGetValue(KeyCacheKey, out string? cached)) return cached;
        string? fresh;
        try
        {
            fresh = await load();
        }
        catch (Exception) when (_keyEverLoaded)
        {
            return _lastKnownKey;
        }
        _lastKnownKey = fresh;
        _keyEverLoaded = true;
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

    /// <summary>How long a successful GET may be served from cache, or null for never. Searches get
    /// a short time; a project's files and summary only change when its author publishes.</summary>
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
