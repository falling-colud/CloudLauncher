using System.Security.Claims;
using CloudLauncher.Server.Data;
using CloudLauncher.Server.Storage;
using CloudLauncher.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CloudLauncher.Server.Controllers;

internal static class ControllerHelpers
{
    public static Guid UserId(this ControllerBase c) =>
        Guid.Parse(c.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)!);

    /// <summary>The caller's id, or null when the request carries no usable identity.</summary>
    /// <remarks>For <c>[AllowAnonymous]</c> routes (mod icons fetched without a header, for one), where a
    /// missing claim means anonymous rather than a 500. <see cref="UserId"/> asserts the claim and belongs
    /// behind <c>[Authorize]</c>.</remarks>
    public static Guid? UserIdOrNull(this ControllerBase c)
    {
        var raw = c.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    // ── sharing ──────────────────────────────────────────────────────────────

    /// <summary>The longest name an account can have, as Identity stores it.</summary>
    private const int MaxUsernameLength = 256;

    /// <summary>Finds the account somebody typed the name of, ignoring case.</summary>
    /// <remarks>Matches on <c>NormalizedUserName</c> (upper-cased and uniquely indexed by Identity), like
    /// <c>UsersController</c> and team invitations, so it agrees with the case-insensitive
    /// autocomplete.</remarks>
    public static async Task<AppUser?> FindUserByNameAsync(AppDbContext db, string? typed, CancellationToken ct)
    {
        var name = typed?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MaxUsernameLength) return null;
        var normalized = name.ToUpperInvariant();
        return await db.Users.FirstOrDefaultAsync(u => u.NormalizedUserName == normalized, ct);
    }

    /// <summary>Who may change who else can use a shared thing: its owner, and anybody the owner
    /// gave <see cref="PackPermissions.ManageCollaborators"/> to.</summary>
    public static bool CanManageSharing(Guid ownerId, Guid me, PackPermissions held) =>
        ownerId == me || held.HasFlag(PackPermissions.ManageCollaborators);

    /// <summary>Checks a permission set somebody is about to hand to somebody else.</summary>
    /// <remarks>Rejects unknown bits and empty sets, and requires View, since a grant without it can't
    /// open the thing it grants (removing someone is how access is taken away). Also refuses permissions
    /// the granter doesn't hold, so someone with ManageCollaborators but not UploadShared can't give
    /// uploads to another account. The owner holds everything.</remarks>
    /// <returns>The sentence to show the caller, or null when the grant is fine.</returns>
    public static string? ValidateGrant(Guid ownerId, Guid me, PackPermissions held, PackPermissions requested)
    {
        if ((requested & ~PackPermissions.Full) != 0)
            return "That permission set has bits this server does not know.";
        if (requested == PackPermissions.None)
            return "Pick at least one permission.";
        if (!requested.HasFlag(PackPermissions.View))
            return "Everyone given access needs at least View. To take somebody's access away, remove them instead.";
        if (ownerId != me && (requested & ~held) != 0)
            return "You cannot give away a permission you do not have yourself.";
        return null;
    }

    // ── icons ────────────────────────────────────────────────────────────────

    /// <summary>How big an uploaded icon may be, in bytes.</summary>
    /// <remarks>Icons are drawn at 44 px at most, so 1 MB is plenty; the blob store itself has no size
    /// limit.</remarks>
    public const long MaxIconBytes = 1024 * 1024;

    /// <summary>What an icon blob is served back as.</summary>
    /// <remarks>Icons are content-addressed with no record of the extension. The launcher uploads PNGs,
    /// and image decoders sniff the bytes anyway.</remarks>
    public const string IconResponseContentType = "image/png";

    private static readonly HashSet<string> IconExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg" };

    /// <summary>Validates an uploaded icon and stores it in the blob store.</summary>
    /// <remarks>
    /// <para>Shared by mods, hosted resource packs, shared worlds and bundles. The type check uses the
    /// file name's extension, since the client-supplied content type can be anything.</para>
    /// <para>Icons count toward the owner's quota and are refused when the disk is low. On success the
    /// icon stays pending against the owner until the caller saves the row and calls
    /// <see cref="UploadGuard.Settle(Guid, string)"/>.</para>
    /// </remarks>
    /// <returns>The stored hash with a null failure, or a null hash and the response to send back.</returns>
    public static async Task<(string? Hash, ObjectResult? Failure)> TryStoreIconAsync(
        BlobStore blobs, UploadGuard guard, Guid ownerId, Guid callerId, IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return (null, IconError("Pick an image first."));
        if (file.Length > MaxIconBytes)
            return (null, IconError($"That image is {file.Length / 1024} KB. Icons have to be {MaxIconBytes / 1024} KB or smaller."));

        var ext = Path.GetExtension(file.FileName);
        if (string.IsNullOrEmpty(ext) || !IconExtensions.Contains(ext))
            return (null, IconError("Icons have to be a .png or .jpg image."));

        if (await guard.RefuseAsync(ownerId, callerId, file, ct) is { } refusal)
            return (null, refusal);

        StoredBlob stored;
        await using (var s = file.OpenReadStream())
            stored = await blobs.PutAsync(s, ct);
        if (await guard.ChargeAsync(ownerId, callerId, stored, ct) is { } overQuota)
            return (null, overQuota);
        return (stored.Hash, null);
    }

    private static ObjectResult IconError(string error) => new BadRequestObjectResult(new { error });

    // ── blobs ────────────────────────────────────────────────────────────────

    /// <summary>Deletes the stored file for <paramref name="hash"/>, but only once nothing in the
    /// database points at it any more.</summary>
    /// <remarks>
    /// <para>Blobs are content-addressed, so identical bytes in a mod version, world version, pack file,
    /// icon and so on share one file. Every referencing table is checked first; the list lives in
    /// <see cref="BlobReferences"/>, shared with the maintenance sweep.</para>
    /// <para>Call this only after the owning row's removal is saved; an unsaved delete still counts as a
    /// reference. A file that can't be deleted is left in place: the row is gone, and a later upload of
    /// the same bytes reuses it.</para>
    /// </remarks>
    public static Task DeleteBlobIfUnreferencedAsync(
        AppDbContext db, BlobStore blobs, string hash, CancellationToken ct = default) =>
        BlobReferences.DeleteIfUnreferencedAsync(db, blobs, hash, ct);
}
