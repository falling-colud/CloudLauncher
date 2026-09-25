using CloudLauncher.Server.Data;
using CloudLauncher.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CloudLauncher.Server.Controllers;

/// <summary>
/// The invitee's side of sharing: what is waiting for me, and accepting or declining it.
/// </summary>
/// <remarks>
/// <para>Routed at <c>/invitations</c> rather than under <c>/packs</c> because a redeemer only has
/// the token, not the pack's id.</para>
/// <para>Pack and bundle tokens both resolve here: a token is looked up as a pack invitation first,
/// then as a bundle one (128-bit tokens, unique per table). Team tokens have their own routes under
/// <c>/teams/invitations</c> since they grant a role rather than permissions.</para>
/// </remarks>
[ApiController]
[Authorize]
[Route("invitations")]
public class PackInvitationsController(AppDbContext db) : ControllerBase
{
    /// <summary>Everything waiting for the signed-in user's answer.</summary>
    /// <remarks>
    /// <para>All three families in one round trip, so the notification badge doesn't wait on three
    /// calls. Team and bundle rows come from <see cref="TeamInvitations.PendingForUserAsync"/> and
    /// <see cref="BundleInvitations.PendingForUserAsync"/>, which apply the same rules to their own
    /// tables.</para>
    /// <para>Each row includes its token: it's addressed to this caller, who needs it to accept.</para>
    /// </remarks>
    [HttpGet]
    public async Task<ActionResult<MyInvitations>> Mine(CancellationToken ct)
    {
        var me = this.UserId();
        var now = DateTimeOffset.UtcNow;

        var packs = await db.PackInvitations
            .AsNoTracking()
            .Include(i => i.InvitedBy)
            .Include(i => i.Pack)
            .Where(i => i.InvitedUserId == me
                     && i.AcceptedAt == null
                     && i.RevokedAt == null
                     && (i.ExpiresAt == null || i.ExpiresAt > now))
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(ct);

        return Ok(new MyInvitations(
            packs.Select(i => PacksController.ToInvitationEntry(i, i.Pack.Name, includeToken: true)).ToList(),
            await TeamInvitations.PendingForUserAsync(db, me, ct),
            await BundleInvitations.PendingForUserAsync(db, me, ct)));
    }

    /// <summary>What a token holder is shown before they decide.</summary>
    /// <remarks>Not the pack itself, since nothing is accepted yet: the name, who is asking, what it
    /// grants, and whether it's already dead. An expired or revoked token gets a preview saying so
    /// rather than a 404.</remarks>
    [HttpGet("{token}")]
    public async Task<ActionResult<InvitationPreview>> Preview(string token, CancellationToken ct)
    {
        var invitation = await FindPackInvitationAsync(token, ct);
        if (invitation is null) return await PreviewBundleAsync(token, ct);

        var me = this.UserId();
        if (invitation.InvitedUserId is not null && invitation.InvitedUserId != me)
            return Forbid();

        return Ok(new InvitationPreview(
            invitation.Id,
            invitation.Pack.Name,
            invitation.InvitedBy?.UserName ?? "",
            invitation.Permissions,
            invitation.Message,
            invitation.ExpiresAt,
            AlreadyAccepted: invitation.AcceptedAt is not null,
            Revoked: invitation.RevokedAt is not null,
            Expired: invitation.ExpiresAt is not null && invitation.ExpiresAt <= DateTimeOffset.UtcNow));
    }

    /// <summary>Redeems a token: grants access and puts the pack in the caller's library.</summary>
    /// <remarks>Idempotent, so double clicks, retries and reopened share links converge on the same
    /// grant. The grant is OR-ed into what the caller already had; an invitation can only add, never
    /// demote.</remarks>
    [HttpPost("{token}/accept")]
    public async Task<ActionResult<PackSummary>> Accept(string token, CancellationToken ct)
    {
        var me = this.UserId();
        var invitation = await FindPackInvitationAsync(token, ct);
        if (invitation is null) return await AcceptBundleAsync(token, me, ct);

        // Fail closed, and say whether it was revoked or expired so the user knows whether to ask again.
        if (invitation.RevokedAt is not null)
            return BadRequest(new { error = "That invitation has been revoked." });
        if (invitation.ExpiresAt is not null && invitation.ExpiresAt <= DateTimeOffset.UtcNow)
            return BadRequest(new { error = "That invitation has expired." });

        var isLink = invitation.InvitedUserId is null;
        if (!isLink && invitation.InvitedUserId != me)
            return Forbid();

        var pack = invitation.Pack;
        if (pack.OwnerId == me)
            // Owners redeeming their own link is not an error, it just has nothing to do.
            return Ok(ToSummary(pack, PackPermissions.Full));

        var existing = await db.PackCollaborators
            .FirstOrDefaultAsync(c => c.PackId == pack.Id && c.UserId == me, ct);

        // An accepted named invitation is spent: re-adding its permissions would let a since-demoted
        // collaborator regain the old grant. Someone still on the pack just gets what they already have.
        if (!isLink && invitation.AcceptedAt is not null)
        {
            if (existing is null)
                return BadRequest(new { error = "That invitation has already been used." });
            if (!await db.PackListings.AnyAsync(l => l.PackId == pack.Id && l.UserId == me, ct))
            {
                db.PackListings.Add(new PackListing { PackId = pack.Id, UserId = me });
                await db.SaveChangesAsync(ct);
            }
            return Ok(ToSummary(pack, existing.Permissions));
        }

        if (existing is null)
            db.PackCollaborators.Add(new PackCollaborator
            {
                PackId = pack.Id, UserId = me, Permissions = invitation.Permissions
            });
        else
            existing.Permissions |= invitation.Permissions;

        if (!await db.PackListings.AnyAsync(l => l.PackId == pack.Id && l.UserId == me, ct))
            db.PackListings.Add(new PackListing { PackId = pack.Id, UserId = me });

        // Only named invitations are spent on accept; stamping AcceptedAt on a link would make it
        // single-use.
        if (!isLink && invitation.AcceptedAt is null)
            invitation.AcceptedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        await PackSharing.LogAsync(db, me, ActivityKind.InviteAccepted, ActivitySubjectType.Pack,
            pack.Id, pack.Name, targetUserId: invitation.InvitedByUserId,
            detail: invitation.Permissions.ToString(), ct: ct);

        var perms = existing is null ? invitation.Permissions : existing.Permissions;
        return Ok(ToSummary(pack, perms));
    }

    /// <summary>Turns down an invitation addressed to you.</summary>
    /// <remarks>Stored as a revocation, since both just mean the token no longer redeems. Share links
    /// can't be declined: they're addressed to nobody, and any reader could otherwise kill them.</remarks>
    [HttpPost("{token}/decline")]
    public async Task<IActionResult> Decline(string token, CancellationToken ct)
    {
        var me = this.UserId();
        var invitation = await FindPackInvitationAsync(token, ct);
        if (invitation is null) return await DeclineBundleAsync(token, me, ct);

        if (invitation.InvitedUserId is null)
            return BadRequest(new { error = "A share link cannot be declined." });
        if (invitation.InvitedUserId != me) return Forbid();
        if (invitation.AcceptedAt is not null)
            return BadRequest(new { error = "You have already accepted that invitation." });
        if (invitation.RevokedAt is not null) return NoContent();

        invitation.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>Resolves a redeem token to its pack invitation, or null.</summary>
    /// <remarks>Tokens are 128-bit and unique, so the token alone authorises a link. The pack and
    /// inviter are included because every caller needs them.</remarks>
    private async Task<PackInvitation?> FindPackInvitationAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 32) return null;
        return await db.PackInvitations
            .Include(i => i.Pack).ThenInclude(p => p.Owner)
            .Include(i => i.Pack).ThenInclude(p => p.LastUploadedBy)
            .Include(i => i.InvitedBy)
            .FirstOrDefaultAsync(i => i.Token == token, ct);
    }

    // ── the bundle half ────────────────────────────────────────────────

    /// <summary>The bundle branch of <see cref="Preview"/>, taken when the token is not a pack's.</summary>
    private async Task<ActionResult<InvitationPreview>> PreviewBundleAsync(string token, CancellationToken ct)
    {
        var invitation = await BundleInvitations.FindAsync(db, token, ct);
        if (invitation is null) return NotFound(new { error = "That invitation link is not valid." });

        var me = this.UserId();
        if (invitation.InvitedUserId is not null && invitation.InvitedUserId != me)
            return Forbid();

        // Same shape as the pack preview so one client-side reader covers both.
        return Ok(new InvitationPreview(
            invitation.Id,
            invitation.Bundle.Name,
            invitation.InvitedBy?.UserName ?? "",
            invitation.Permissions,
            invitation.Message,
            invitation.ExpiresAt,
            AlreadyAccepted: invitation.AcceptedAt is not null,
            Revoked: invitation.RevokedAt is not null,
            Expired: invitation.ExpiresAt is not null && invitation.ExpiresAt <= DateTimeOffset.UtcNow));
    }

    /// <summary>The bundle branch of <see cref="Accept"/>.</summary>
    /// <remarks>Returns a <see cref="ContentBundleSummary"/>, since that's what access was granted to.
    /// The launcher ignores the body on this route.</remarks>
    private async Task<ActionResult<PackSummary>> AcceptBundleAsync(string token, Guid me, CancellationToken ct)
    {
        var outcome = await BundleInvitations.AcceptAsync(db, token, me, ct);
        if (outcome.Invitation is null || outcome.Status != StatusCodes.Status200OK)
            return StatusCode(outcome.Status, new { error = outcome.Error });

        var bundle = outcome.Invitation.Bundle;
        var versions = await db.ContentBundleVersions.CountAsync(v => v.BundleId == bundle.Id, ct);
        return Ok(new ContentBundleSummary(
            bundle.Id, bundle.Kind, bundle.Slug, bundle.Name, bundle.Summary,
            bundle.OwnerId, bundle.Owner.UserName ?? "", bundle.Visibility, bundle.IconBlobHash,
            bundle.TargetPathRoot, bundle.McVersionsCsv, bundle.LoadersCsv, bundle.DownloadCount,
            bundle.CreatedAt, bundle.UpdatedAt, outcome.Granted, versions));
    }

    /// <summary>The bundle branch of <see cref="Decline"/>.</summary>
    private async Task<IActionResult> DeclineBundleAsync(string token, Guid me, CancellationToken ct)
    {
        var outcome = await BundleInvitations.DeclineAsync(db, token, me, ct);
        return outcome.Status == StatusCodes.Status204NoContent
            ? NoContent()
            : StatusCode(outcome.Status, new { error = outcome.Error });
    }

    private static PackSummary ToSummary(Pack p, PackPermissions perms) => new(
        p.Id, p.Name, p.Description, p.OwnerId, p.Owner.UserName ?? "",
        p.Visibility, p.IsShared, p.IsEmpty, p.MinecraftVersion, p.Loader, p.LoaderVersion,
        p.CreatedAt, p.UpdatedAt, perms, p.Summary,
        p.LastUploadedById, p.LastUploadedBy?.UserName, p.LastUploadedAt);
}
