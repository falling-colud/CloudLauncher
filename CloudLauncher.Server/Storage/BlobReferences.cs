using CloudLauncher.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace CloudLauncher.Server.Storage;

/// <summary>Which blobs the database still points at, and deleting the ones it does not.</summary>
/// <remarks>
/// <para>Blobs are content-addressed, so a mod version, world version, resource pack version, bundle
/// version, synced pack file and icon with identical bytes share one file. A blob can only be deleted
/// once no row of any of those kinds names it.</para>
/// <para>Every deletion goes through <see cref="All"/>. A content type missing from it would have its
/// blobs deleted when something unrelated with the same bytes is removed. A new hosted content type
/// needs two lines there: its versions' hash and its icon.</para>
/// </remarks>
public static class BlobReferences
{
    /// <summary>How many hashes go into one "which of these are still named?" query.</summary>
    private const int ProbeBatch = 5000;

    /// <summary>Every hash any row names, duplicates included.</summary>
    private static IQueryable<string> All(AppDbContext db) =>
        db.ModVersions.Select(v => v.BlobHash)
            .Concat(db.SharedWorldVersions.Select(v => v.BlobHash))
            .Concat(db.HostedResourcePackVersions.Select(v => v.BlobHash))
            .Concat(db.PackManifestEntries.Select(e => e.Hash))
            .Concat(db.ContentBundleVersions.Select(v => v.BlobHash))
            .Concat(db.Mods.Where(m => m.IconBlobHash != null).Select(m => m.IconBlobHash!))
            .Concat(db.SharedWorlds.Where(w => w.IconBlobHash != null).Select(w => w.IconBlobHash!))
            .Concat(db.HostedResourcePacks.Where(p => p.IconBlobHash != null).Select(p => p.IconBlobHash!))
            .Concat(db.ContentBundles.Where(b => b.IconBlobHash != null).Select(b => b.IconBlobHash!));

    /// <summary>Whether any row names this blob.</summary>
    /// <remarks>Where-then-Any translates to EXISTS with the comparison in every branch of the union;
    /// Any with a predicate becomes an IN over the whole union.</remarks>
    public static Task<bool> IsReferencedAsync(AppDbContext db, string hash, CancellationToken ct = default) =>
        All(db).Where(h => h == hash).AnyAsync(ct);

    /// <summary>Whether any row names any blob at all.</summary>
    public static Task<bool> AnyAsync(AppDbContext db, CancellationToken ct = default) =>
        All(db).AnyAsync(ct);

    /// <summary>The subset of <paramref name="hashes"/> that some row names, asked in batches.</summary>
    /// <remarks>Compared ignoring case: blob files are named in lowercase, and a row that recorded its
    /// hash any other way must still keep its file alive.</remarks>
    public static async Task<HashSet<string>> FindReferencedAsync(
        AppDbContext db, IEnumerable<string> hashes, CancellationToken ct = default)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in hashes.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(ProbeBatch))
        {
            var probe = chunk.Select(h => h.ToLowerInvariant()).ToList();
            var named = await All(db)
                .Select(h => h.ToLower())
                .Where(h => probe.Contains(h))
                .Distinct()
                .ToListAsync(ct);
            found.UnionWith(named);
        }
        return found;
    }

    /// <summary>Deletes the stored file for <paramref name="hash"/> once no row names it.</summary>
    /// <remarks>
    /// Call only after the owning row's delete is saved; an unsaved delete still counts as a reference.
    /// A file that can't be deleted is left for the maintenance job instead of failing the caller.
    /// </remarks>
    public static async Task DeleteIfUnreferencedAsync(
        AppDbContext db, BlobStore blobs, string hash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(hash)) return;
        if (await IsReferencedAsync(db, hash, ct)) return;

        try
        {
            var path = blobs.PathForHash(hash);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (ArgumentException) { }        // not a hash this store could ever have written
        catch (IOException) { }              // locked by an in-flight download; left for the maintenance job
        catch (UnauthorizedAccessException) { }
    }
}
