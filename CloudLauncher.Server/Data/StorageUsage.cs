using System.Globalization;
using CloudLauncher.Server.Storage;
using Microsoft.EntityFrameworkCore;

namespace CloudLauncher.Server.Data;

/// <summary>
/// How much of the server a user's content takes up, for quota decisions.
/// </summary>
/// <remarks>
/// <para>Counts each distinct blob kept alive by the user's pack manifests, hosted mod, world,
/// resource-pack and bundle versions, and their icons, once. Blobs are shared on disk, but every
/// owner of the same bytes is charged for them.</para>
/// <para>Unshared packs count too: their synced files stay on the server, and leaving them out would
/// allow upload, unshare, repeat.</para>
/// <para>Uploads nothing references yet are tracked by <see cref="PendingUploadLedger"/>, and
/// <see cref="UploadGuard"/> adds them on top of these figures.</para>
/// </remarks>
public static class StorageUsage
{
    /// <summary>How many hashes go into one "which of these does the owner already store?" query.</summary>
    private const int ProbeBatch = 5000;

    /// <summary>Bytes the database charges this user, pending uploads not included.</summary>
    /// <remarks>Icon sizes exist only on disk, so without a <see cref="BlobStore"/> icons count as
    /// nothing here; <see cref="GetStoredBytesAsync"/> with a store measures them.</remarks>
    public static Task<long> GetUsedBytesAsync(AppDbContext db, Guid userId, CancellationToken ct = default) =>
        GetStoredBytesAsync(db, blobs: null, userId, ct: ct);

    /// <summary>Everything charged to the owner: what the database charges plus uploads still pending.</summary>
    public static async Task<long> GetUsedBytesAsync(
        AppDbContext db, BlobStore blobs, PendingUploadLedger pending, Guid ownerId, CancellationToken ct = default) =>
        await GetStoredBytesAsync(db, blobs, ownerId, ct: ct) + pending.BytesFor(ownerId);

    /// <summary>
    /// Returns true if <paramref name="incomingBytes"/> more would keep the user within their quota.
    /// Always returns true when quota is null (unlimited).
    /// </summary>
    public static async Task<bool> WouldFitAsync(
        AppDbContext db, AppUser user, long incomingBytes, CancellationToken ct = default)
    {
        if (user.StorageQuotaBytes is null) return true;
        var used = await GetUsedBytesAsync(db, user.Id, ct);
        return used + incomingBytes <= user.StorageQuotaBytes.Value;
    }

    /// <summary>Bytes the database charges the owner: each distinct blob their things name, once.</summary>
    /// <param name="blobs">Where icon sizes are read from; null counts icons as nothing.</param>
    /// <param name="exceptPackId">A pack whose manifest is left out, for working out what a commit to it
    /// would change.</param>
    public static async Task<long> GetStoredBytesAsync(
        AppDbContext db, BlobStore? blobs, Guid ownerId, Guid? exceptPackId = null, CancellationToken ct = default)
    {
        var charges = Charges(db, ownerId, exceptPackId);

        // Largest recorded size per hash, summed in the database. Rows for one hash should agree; Max
        // just stops a bad row from counting a blob twice.
        var bytes = await charges.GroupBy(c => c.Hash).Select(g => g.Max(c => c.Size)).SumAsync(ct);
        if (blobs is null) return bytes;

        var icons = await IconHashes(db, ownerId).Distinct().ToListAsync(ct);
        if (icons.Count == 0) return bytes;

        var counted = await charges.Where(c => icons.Contains(c.Hash)).Select(c => c.Hash).Distinct().ToListAsync(ct);
        foreach (var icon in icons.Except(counted, StringComparer.OrdinalIgnoreCase))
            bytes += SizeOnDisk(blobs, icon);
        return bytes;
    }

    /// <summary>The subset of <paramref name="hashes"/> the owner is already charged for.</summary>
    /// <param name="exceptPackId">A pack whose manifest does not count as already having them.</param>
    public static async Task<HashSet<string>> FindChargedAsync(
        AppDbContext db, Guid ownerId, IEnumerable<string> hashes, Guid? exceptPackId = null, CancellationToken ct = default)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in hashes.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(ProbeBatch))
        {
            var probe = chunk.ToList();
            var hits = await Charges(db, ownerId, exceptPackId).Select(c => c.Hash)
                .Concat(IconHashes(db, ownerId))
                .Where(h => probe.Contains(h))
                .Distinct()
                .ToListAsync(ct);
            found.UnionWith(hits);
        }
        return found;
    }

    /// <summary>Whether the owner is already charged for these bytes.</summary>
    public static Task<bool> IsChargedToAsync(AppDbContext db, Guid ownerId, string hash, CancellationToken ct = default) =>
        Charges(db, ownerId, exceptPackId: null).Select(c => c.Hash)
            .Concat(IconHashes(db, ownerId))
            .Where(h => h == hash)
            .AnyAsync(ct);

    /// <summary>A byte count as a person reads it: "812 KB", "12.4 MB", "1 GB".</summary>
    public static string Describe(long bytes)
    {
        const double Kb = 1024, Mb = Kb * 1024, Gb = Mb * 1024;
        var culture = CultureInfo.InvariantCulture;
        if (bytes < 0) bytes = 0;
        return bytes switch
        {
            1 => "1 byte",
            < 1024 => $"{bytes} bytes",
            < 1024 * 1024 => (bytes / Kb).ToString("0.#", culture) + " KB",
            < 1024L * 1024 * 1024 => (bytes / Mb).ToString("0.#", culture) + " MB",
            _ => (bytes / Gb).ToString("0.##", culture) + " GB"
        };
    }

    /// <summary>One blob a row of the owner's keeps alive, with the size that row recorded for it.</summary>
    private sealed class Charge
    {
        public string Hash { get; set; } = "";
        public long Size { get; set; }
    }

    /// <summary>Every sized blob reference the owner has: pack manifests and hosted versions.</summary>
    /// <remarks>Icons have no size column and are measured from disk. A new content type with blobs
    /// needs adding here as well as in <see cref="BlobReferences"/>.</remarks>
    private static IQueryable<Charge> Charges(AppDbContext db, Guid ownerId, Guid? exceptPackId)
    {
        var manifests = db.PackManifestEntries.Where(e => e.Pack.OwnerId == ownerId);
        if (exceptPackId is { } skip) manifests = manifests.Where(e => e.PackId != skip);

        return manifests.Select(e => new Charge { Hash = e.Hash, Size = e.Size })
            .Concat(db.ModVersions.Where(v => v.Mod.OwnerId == ownerId)
                .Select(v => new Charge { Hash = v.BlobHash, Size = v.FileSize }))
            .Concat(db.SharedWorldVersions.Where(v => v.World.OwnerId == ownerId)
                .Select(v => new Charge { Hash = v.BlobHash, Size = v.FileSize }))
            .Concat(db.HostedResourcePackVersions.Where(v => v.ResourcePack.OwnerId == ownerId)
                .Select(v => new Charge { Hash = v.BlobHash, Size = v.FileSize }))
            .Concat(db.ContentBundleVersions.Where(v => v.Bundle.OwnerId == ownerId)
                .Select(v => new Charge { Hash = v.BlobHash, Size = v.FileSize }));
    }

    /// <summary>The icons on the owner's mods, worlds, resource packs and bundles.</summary>
    private static IQueryable<string> IconHashes(AppDbContext db, Guid ownerId) =>
        db.Mods.Where(m => m.OwnerId == ownerId && m.IconBlobHash != null).Select(m => m.IconBlobHash!)
            .Concat(db.SharedWorlds.Where(w => w.OwnerId == ownerId && w.IconBlobHash != null).Select(w => w.IconBlobHash!))
            .Concat(db.HostedResourcePacks.Where(p => p.OwnerId == ownerId && p.IconBlobHash != null).Select(p => p.IconBlobHash!))
            .Concat(db.ContentBundles.Where(b => b.OwnerId == ownerId && b.IconBlobHash != null).Select(b => b.IconBlobHash!));

    private static long SizeOnDisk(BlobStore blobs, string hash)
    {
        try { return blobs.Exists(hash) ? blobs.SizeOf(hash) : 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return 0; }
    }
}
