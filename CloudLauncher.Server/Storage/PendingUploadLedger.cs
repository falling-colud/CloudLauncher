using System.Collections.Concurrent;

namespace CloudLauncher.Server.Storage;

/// <summary>
/// Uploads that are in the blob store but that no row names yet, per owner, kept in memory.
/// </summary>
/// <remarks>
/// <para>A synced file is uploaded before the commit that names it, and the commit may never come.
/// A quota computed from the database alone can't see those bytes, so each upload is recorded here
/// against the owner's quota until the commit (or the hosted version or icon naming it) is saved.</para>
/// <para>Entries lapse after <see cref="Lifetime"/>, the same grace the maintenance job gives an
/// unreferenced blob. A restart empties the ledger, and the maintenance job collects what it was
/// tracking.</para>
/// </remarks>
public sealed class PendingUploadLedger
{
    /// <summary>How long an upload counts as pending before it is treated as abandoned.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<Guid, OwnerEntries> _owners = new();

    private readonly record struct Entry(long Size, DateTimeOffset At);

    private sealed class OwnerEntries
    {
        public readonly Dictionary<string, Entry> ByHash = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Set once this instance has been dropped from the map; a writer then fetches a new
        /// one.</summary>
        public bool Retired;
    }

    /// <summary>
    /// Records an upload for <paramref name="ownerId"/>, but only if what the database already charges them,
    /// what they have pending and this upload together stay within <paramref name="quotaBytes"/>.
    /// </summary>
    /// <remarks>The check and the write happen under one lock per owner, so two uploads finishing at the
    /// same moment cannot both squeeze into the last bit of a quota. An earlier entry for the same hash is
    /// replaced rather than counted twice.</remarks>
    /// <returns>False, recording nothing, when it would not fit. A null quota always fits.</returns>
    public bool TryAdd(Guid ownerId, string hash, long size, long storedBytes, long? quotaBytes)
    {
        while (true)
        {
            var owner = _owners.GetOrAdd(ownerId, _ => new OwnerEntries());
            lock (owner)
            {
                if (owner.Retired) continue;
                var now = DateTimeOffset.UtcNow;
                if (quotaBytes is { } quota && storedBytes + Sum(owner, now, h => h.Equals(hash, StringComparison.OrdinalIgnoreCase)) + size > quota)
                    return false;
                owner.ByHash[hash] = new Entry(size, now);
                return true;
            }
        }
    }

    /// <summary>Pending bytes for the owner, leaving out <paramref name="exceptHash"/> when given.</summary>
    public long BytesFor(Guid ownerId, string? exceptHash = null) =>
        SumFor(ownerId, exceptHash is null ? null : h => h.Equals(exceptHash, StringComparison.OrdinalIgnoreCase));

    /// <summary>Pending bytes for the owner, leaving out every hash in <paramref name="except"/>.</summary>
    public long BytesFor(Guid ownerId, IReadOnlySet<string> except) => SumFor(ownerId, except.Contains);

    /// <summary>Whether the owner has a live pending upload of these bytes.</summary>
    public bool Contains(Guid ownerId, string hash)
    {
        if (!_owners.TryGetValue(ownerId, out var owner)) return false;
        lock (owner)
            return owner.ByHash.TryGetValue(hash, out var entry) && IsLive(entry, DateTimeOffset.UtcNow);
    }

    /// <summary>Whether anybody has a live pending upload of these bytes.</summary>
    /// <remarks>Checked before deleting a blob that no row names: an upload still waiting for its commit
    /// needs the file to be there when the commit arrives.</remarks>
    public bool IsPendingAnywhere(string hash)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var owner in _owners.Values)
            lock (owner)
                if (owner.ByHash.TryGetValue(hash, out var entry) && IsLive(entry, now))
                    return true;
        return false;
    }

    /// <summary>Clears uploads that a saved row now references, so they are not counted twice.</summary>
    public void Remove(Guid ownerId, IEnumerable<string> hashes)
    {
        if (!_owners.TryGetValue(ownerId, out var owner)) return;
        lock (owner)
            foreach (var hash in hashes)
                owner.ByHash.Remove(hash);
    }

    /// <summary>Drops every entry older than <see cref="Lifetime"/>.</summary>
    /// <returns>How many entries were dropped.</returns>
    public int Expire()
    {
        var now = DateTimeOffset.UtcNow;
        var dropped = 0;
        foreach (var (ownerId, owner) in _owners)
        {
            lock (owner)
            {
                var stale = owner.ByHash.Where(kv => !IsLive(kv.Value, now)).Select(kv => kv.Key).ToList();
                foreach (var hash in stale) owner.ByHash.Remove(hash);
                dropped += stale.Count;

                if (owner.ByHash.Count == 0 && _owners.TryRemove(KeyValuePair.Create(ownerId, owner)))
                    owner.Retired = true;
            }
        }
        return dropped;
    }

    private long SumFor(Guid ownerId, Func<string, bool>? skip)
    {
        if (!_owners.TryGetValue(ownerId, out var owner)) return 0;
        lock (owner)
            return Sum(owner, DateTimeOffset.UtcNow, skip);
    }

    private static long Sum(OwnerEntries owner, DateTimeOffset now, Func<string, bool>? skip)
    {
        long total = 0;
        foreach (var (hash, entry) in owner.ByHash)
            if (IsLive(entry, now) && (skip is null || !skip(hash)))
                total += entry.Size;
        return total;
    }

    private static bool IsLive(Entry entry, DateTimeOffset now) => now - entry.At < Lifetime;
}
