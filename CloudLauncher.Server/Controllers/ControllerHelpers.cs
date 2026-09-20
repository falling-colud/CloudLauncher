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

    /// <summary>The caller's id, or null when the request carries no usable identity.</summary>
    /// <remarks>
    /// <see cref="UserId"/> asserts the claim is there, which is right behind <c>[Authorize]</c> and
    /// wrong on the handful of routes that are <c>[AllowAnonymous]</c> so a public thing stays
    /// publicly readable — the mod icons an &lt;Image&gt; fetches without a header, for one. There a
    /// missing claim is the expected case and has to resolve to "anonymous" rather than a 500, which
    /// is exactly what the permission resolvers already accept as a null user.
    /// </remarks>
    public static Guid? UserIdOrNull(this ControllerBase c)
    {
        var raw = c.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    // ── icons ────────────────────────────────────────────────────────────────

    /// <summary>How big an uploaded icon may be, in bytes.</summary>
    /// <remarks>
    /// An icon is drawn at 44 px in the lists and never larger, so a megabyte is already far more
    /// than the art needs. The cap exists because the blob store has no opinion about size and an
    /// icon is the one upload a user makes casually — dropping a 20 MB screenshot on the well should
    /// be refused with a sentence, not stored forever.
    /// </remarks>
    public const long MaxIconBytes = 1024 * 1024;

    /// <summary>What an icon blob is served back as.</summary>
    /// <remarks>Icons are content-addressed with no record of the original extension, and PNG is
    /// what every icon the launcher uploads actually is, so it is the honest default. Image decoders
    /// sniff the bytes regardless.</remarks>
    public const string IconResponseContentType = "image/png";

    private static readonly HashSet<string> IconExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg" };

    /// <summary>Validates an uploaded icon and stores it in the blob store.</summary>
    /// <remarks>
    /// Shared by mods, hosted resource packs and shared worlds: all three keep exactly one
    /// <c>IconBlobHash</c> and all three want the same answer to "is this a picture, and is it
    /// small?". The type check is on the file name's extension rather than the client-supplied
    /// content type — the launcher picks the file from a dialog filtered to those extensions, while
    /// a content type is whatever the caller felt like sending.
    /// </remarks>
    /// <returns>The stored hash with a null error, or a null hash and the sentence to show the user.</returns>
    public static async Task<(string? Hash, string? Error)> TryStoreIconAsync(
        BlobStore blobs, IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return (null, "Pick an image first.");
        if (file.Length > MaxIconBytes)
            return (null, $"That image is {file.Length / 1024} KB. Icons have to be {MaxIconBytes / 1024} KB or smaller.");

        var ext = Path.GetExtension(file.FileName);
        if (string.IsNullOrEmpty(ext) || !IconExtensions.Contains(ext))
            return (null, "Icons have to be a .png or .jpg image.");

        await using var s = file.OpenReadStream();
        return (await blobs.StoreAsync(s, ct), null);
    }

    // ── blobs ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Deletes the stored file for <paramref name="hash"/>, but only once nothing in the database
    /// points at it any more.
    /// </summary>
    /// <remarks>
    /// <para>Blobs are content-addressed: a mod version, a world version, a resource-pack version and
    /// a synced pack file that happen to hold identical bytes all resolve to one file on disk. Deleting
    /// that file because one of those rows was removed would silently break every other download naming
    /// the same hash, so every table that can reference a blob is checked first.</para>
    /// <para>Icons are in that store too, so the three <c>IconBlobHash</c> columns are checked
    /// alongside the version tables: without them, clearing one mod's icon would delete the file out
    /// from under every other row that happens to show the same picture.</para>
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
            || await db.PackManifestEntries.AnyAsync(e => e.Hash == hash, ct)
            || await db.Mods.AnyAsync(m => m.IconBlobHash == hash, ct)
            || await db.SharedWorlds.AnyAsync(w => w.IconBlobHash == hash, ct)
            || await db.HostedResourcePacks.AnyAsync(p => p.IconBlobHash == hash, ct);
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
