using System.Security.Claims;
using CloudLauncher.Server.Data;
using CloudLauncher.Server.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CloudLauncher.Server.Controllers;

internal static class ControllerHelpers
{
    public static Guid UserId(this ControllerBase c) =>
        Guid.Parse(c.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)!);

    /// <summary>
    /// Deletes the stored file for <paramref name="hash"/>, but only once nothing in the database
    /// points at it any more.
    /// </summary>
    /// <remarks>
    /// <para>Blobs are content-addressed: a mod version, a world version, a resource-pack version and
    /// a synced pack file that happen to hold identical bytes all resolve to one file on disk. Deleting
    /// that file because one of those rows was removed would silently break every other download naming
    /// the same hash, so every table that can reference a blob is checked first.</para>
    /// <para>Call this only after the owning row has been removed and saved — an unsaved delete still
    /// counts as a reference and the blob would be kept forever.</para>
    /// <para>A file that refuses to be deleted is left where it is rather than failing the request: the
    /// row is already gone, the blob is unreachable, and the next upload of the same bytes reuses it.
    /// Reporting a 500 for it would tell the caller their delete failed when it in fact succeeded.</para>
    /// </remarks>
    public static async Task DeleteBlobIfUnreferencedAsync(
        AppDbContext db, BlobStore blobs, string hash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(hash)) return;

        var stillReferenced =
            await db.ModVersions.AnyAsync(v => v.BlobHash == hash, ct)
            || await db.SharedWorldVersions.AnyAsync(v => v.BlobHash == hash, ct)
            || await db.HostedResourcePackVersions.AnyAsync(v => v.BlobHash == hash, ct)
            || await db.PackManifestEntries.AnyAsync(e => e.Hash == hash, ct);
        if (stillReferenced) return;

        try
        {
            var path = blobs.PathForHash(hash);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (ArgumentException) { }        // not a hash this store could ever have written
        catch (IOException) { }              // locked by an in-flight download; it will be orphaned, not lost
        catch (UnauthorizedAccessException) { }
    }
}
