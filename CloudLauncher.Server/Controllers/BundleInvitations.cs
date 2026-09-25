using CloudLauncher.Server.Data;
using CloudLauncher.Shared;
using Microsoft.EntityFrameworkCore;

namespace CloudLauncher.Server.Controllers;

/// <summary>What happened when a bundle token was redeemed: an HTTP status and either a sentence or
/// the row that was redeemed.</summary>
/// <remarks>
/// Bundle version of <see cref="TeamInvitationOutcome"/>. Shared by the bundle routes and
/// <c>/invitations/{token}</c> so both refuse for the same reasons in the same words.
/// </remarks>
/// <param name="Granted">The caller's permissions on the bundle after redeeming: what they had OR
/// what the invitation offered. Only meaningful when <paramref name="Status"/> is 200.</param>
public sealed record BundleInvitationOutcome(
    int Status,
    string? Error,
    ContentBundleInvitation? Invitation = null,
    PackPermissions Granted = PackPermissions.None);

/// <summary>Content-bundle invitations, shared by the controllers that need them.</summary>
/// <remarks>
/// <para><see cref="ContentBundlesController"/> mints and revokes them, while
/// <see cref="PackInvitationsController"/> owns <c>/invitations/{token}</c>, where the redeemer has
/// only a token. Expiry, revocation, link rules and acceptance live here so both agree.</para>
/// <para>Bundles reuse the pack permission model, so invitations carry <see cref="PackPermissions"/>
/// and use <see cref="CreatePackInvitationRequest"/> as the request shape.</para>
/// </remarks>
public static class BundleInvitations
{
    /// <summary>Named invitations waiting for this user's answer, newest first. Share links are
    /// addressed to nobody, so they are left out.</summary>
    public static async Task<IReadOnlyList<BundleInvitationEntry>> PendingForUserAsync(
        AppDbContext db, Guid userId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = await db.ContentBundleInvitations
            .AsNoTracking()
            .Include(i => i.Bundle)
            .Include(i => i.InvitedBy)
            .Where(i => i.InvitedUserId == userId
                     && i.AcceptedAt == null
                     && i.RevokedAt == null
                     && (i.ExpiresAt == null || i.ExpiresAt > now))
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(ct);

        // Include the token: the invitation is addressed to this caller and it is how they accept.
        return rows.Select(i => ToEntry(i, i.Bundle.Name, i.Bundle.Kind, includeToken: true)).ToList();
    }

    /// <summary>Resolves a redeem token to its bundle invitation, with bundle and inviter, or null.</summary>
    /// <remarks>Tokens are unique and 128 bits wide, so the token alone authorises a link.</remarks>
    public static async Task<ContentBundleInvitation?> FindAsync(
        AppDbContext db, string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 32) return null;
        return await db.ContentBundleInvitations
            .Include(i => i.Bundle).ThenInclude(b => b.Owner)
            .Include(i => i.InvitedBy)
            .FirstOrDefaultAsync(i => i.Token == token, ct);
    }

    /// <summary>Redeems a token: grants access to the bundle.</summary>
    /// <remarks>
    /// Idempotent, so double clicks, retries and reopened share links end in the same grant. The grant
    /// is OR-ed into what the caller already had, so a read-only link never demotes anyone. Unlike
    /// packs there is no listing row; the collaborator row is all acceptance creates.
    /// </remarks>
    public static async Task<BundleInvitationOutcome> AcceptAsync(
        AppDbContext db, string token, Guid me, CancellationToken ct = default)
    {
        var invitation = await FindAsync(db, token, ct);
        if (invitation is null)
            return new BundleInvitationOutcome(404, "That invitation link is not valid.");

        // Fail closed, and say whether it was revoked or expired so they know whether to ask again.
        if (invitation.RevokedAt is not null)
            return new BundleInvitationOutcome(400, "That invitation has been revoked.");
        if (invitation.ExpiresAt is not null && invitation.ExpiresAt <= DateTimeOffset.UtcNow)
            return new BundleInvitationOutcome(400, "That invitation has expired.");

        var isLink = invitation.InvitedUserId is null;
        if (!isLink && invitation.InvitedUserId != me)
            return new BundleInvitationOutcome(403, "That invitation is addressed to somebody else.");

        var bundle = invitation.Bundle;
        if (bundle.OwnerId == me)
            // An owner redeeming their own link: nothing to do, but not an error.
            return new BundleInvitationOutcome(200, null, invitation, PackPermissions.Full);

        var existing = await db.ContentBundleCollaborators
            .FirstOrDefaultAsync(c => c.BundleId == bundle.Id && c.UserId == me, ct);

        // An accepted named invitation is spent, so a removed or demoted collaborator can't reuse it.
        // Someone still on the bundle (a double click) just gets the answer they already have.
        if (!isLink && invitation.AcceptedAt is not null)
            return existing is null
                ? new BundleInvitationOutcome(400, "That invitation has already been used.")
                : new BundleInvitationOutcome(200, null, invitation, existing.Permissions);

        if (existing is null)
            db.ContentBundleCollaborators.Add(new ContentBundleCollaborator
            {
                BundleId = bundle.Id, UserId = me, Permissions = invitation.Permissions
            });
        else
            existing.Permissions |= invitation.Permissions;

        // Only named invitations are spent on use; stamping AcceptedAt on a share link would make it
        // single-use.
        if (!isLink && invitation.AcceptedAt is null)
            invitation.AcceptedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        await PackSharing.LogAsync(db, me, ActivityKind.InviteAccepted, ActivitySubjectType.Bundle,
            bundle.Id, bundle.Name, targetUserId: invitation.InvitedByUserId,
            detail: invitation.Permissions.ToString(), ct: ct);

        var granted = existing is null ? invitation.Permissions : existing.Permissions;
        return new BundleInvitationOutcome(200, null, invitation, granted);
    }

    /// <summary>Turns down an invitation addressed to you.</summary>
    /// <remarks>
    /// Recorded as a revocation, since both mean the token no longer redeems. Share links can't be
    /// declined, or any reader could kill a link meant for everyone.
    /// </remarks>
    public static async Task<BundleInvitationOutcome> DeclineAsync(
        AppDbContext db, string token, Guid me, CancellationToken ct = default)
    {
        var invitation = await FindAsync(db, token, ct);
        if (invitation is null)
            return new BundleInvitationOutcome(404, "That invitation link is not valid.");

        if (invitation.InvitedUserId is null)
            return new BundleInvitationOutcome(400, "A share link cannot be declined.");
        if (invitation.InvitedUserId != me)
            return new BundleInvitationOutcome(403, "That invitation is addressed to somebody else.");
        if (invitation.AcceptedAt is not null)
            return new BundleInvitationOutcome(400, "You have already accepted that invitation.");
        if (invitation.RevokedAt is not null)
            return new BundleInvitationOutcome(204, null, invitation);

        // No share-token pointer to clear: links were refused above, and a named invitation's token is
        // never the one the bundle advertises.
        invitation.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return new BundleInvitationOutcome(204, null, invitation);
    }

    /// <summary>Clears the bundle's share-link pointer when it names the row being revoked.</summary>
    /// <remarks>Otherwise the share dialog keeps showing a link that no longer redeems.</remarks>
    public static void ClearShareTokenIfPointedAt(ContentBundle bundle, string token)
    {
        if (bundle.ShareToken != token) return;
        bundle.ShareToken = null;
        bundle.ShareTokenCreatedAt = null;
    }

    /// <summary>Shapes one invitation row for the wire.</summary>
    /// <param name="includeToken">Only for people who may manage the bundle's sharing and for the
    /// invitee. For anyone else the token is the access itself.</param>
    public static BundleInvitationEntry ToEntry(
        ContentBundleInvitation i, string bundleName, BundleKind kind, bool includeToken) => new(
        i.Id, i.BundleId, bundleName, kind,
        i.InvitedUserId, i.InvitedUsername,
        i.InvitedByUserId, i.InvitedBy?.UserName ?? "",
        i.Permissions, i.Message,
        i.CreatedAt, i.ExpiresAt, i.AcceptedAt, i.RevokedAt,
        IsLink: i.InvitedUserId is null && i.InvitedUsername is null,
        Token: includeToken ? i.Token : null);
}
