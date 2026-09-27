using System.Text.Json;
using CloudLauncher.Server.Auth;
using CloudLauncher.Server.Data;
using CloudLauncher.Server.Storage;
using CloudLauncher.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CloudLauncher.Server.Controllers;

[ApiController]
[Authorize]
[Route("packs")]
public class PacksController(
    AppDbContext db,
    PackPermissionResolver resolver,
    BlobStore blobs,
    UploadGuard guard,
    IConfiguration config) : ControllerBase
{
    /// <summary>How long a share link or invitation may be left open, in days.</summary>
    private const int MaxExpiryDays = 365;

    /// <summary>
    /// Packs every signed-in user sees in their list without subscribing, from the comma-separated
    /// <c>AutoListPackIds</c> setting (or environment variable). Empty by default.
    /// </summary>
    /// <remarks>
    /// An explicit list rather than every public pack, which would put every test and side
    /// pack in every account. The pack still needs Public visibility to grant View/Download;
    /// this only lists it.
    /// </remarks>
    private IReadOnlyList<Guid> AutoListPackIds() =>
        (config["AutoListPackIds"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToList();

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PackSummary>>> List(CancellationToken ct)
    {
        var me = this.UserId();

        // The library is: packs you own, packs you added (PackListings), and the AutoListPackIds
        // packs. Being a collaborator only grants access; a directly shared pack shows under
        // "Shared with me" and is listed once you add it or accept the invitation.
        // Team grants write a PackListing for each member (PackSharing), so the listing table still
        // decides everything.
        var autoListed = AutoListPackIds();
        var packs = await db.Packs
            .AsNoTracking()
            .Include(p => p.Owner)
            .Include(p => p.LastUploadedBy)
            .Where(p =>
                p.OwnerId == me
                || autoListed.Contains(p.Id)
                || db.PackListings.Any(l => l.PackId == p.Id && l.UserId == me))
            .ToListAsync(ct);

        var result = new List<PackSummary>(packs.Count);
        foreach (var p in packs)
        {
            var perms = await resolver.GetAsync(p, me, ct);
            result.Add(ToSummary(p, perms));
        }
        return Ok(result);
    }

    [HttpGet("browse")]
    public async Task<ActionResult<PackBrowsePage>> Browse(
        [FromQuery] PackBrowseSource source = PackBrowseSource.Public,
        [FromQuery] Guid? teamId = null,
        [FromQuery] string? q = null,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 30,
        CancellationToken ct = default)
    {
        if (limit <= 0 || limit > 100) limit = 30;
        if (offset < 0) offset = 0;

        var me = this.UserId();
        var query = db.Packs.AsNoTracking()
            .Include(p => p.Owner)
            .Include(p => p.LastUploadedBy)
            .AsQueryable();

        query = source switch
        {
            // Every public pack, including the caller's own and ones already shared with them,
            // as the other browsers do. Each source is its own list in the launcher, so nothing
            // is shown twice.
            PackBrowseSource.Public =>
                query.Where(p => p.Visibility == PackVisibility.Public),
            PackBrowseSource.Shared =>
                query.Where(p => p.OwnerId != me
                              && db.PackCollaborators.Any(c => c.PackId == p.Id && c.UserId == me)),
            // Everything my teams can reach: packs granted to a team plus Team-visibility packs, both
            // of which grant access through the PackTeams row (see PackPermissionResolver).
            PackBrowseSource.Team =>
                teamId.HasValue
                    ? query.Where(p => db.PackTeams.Any(pt => pt.PackId == p.Id && pt.TeamId == teamId.Value)
                                    && db.TeamMembers.Any(tm => tm.TeamId == teamId.Value && tm.UserId == me))
                    : query.Where(p => db.PackTeams.Any(pt => pt.PackId == p.Id
                                                       && db.TeamMembers.Any(tm => tm.TeamId == pt.TeamId && tm.UserId == me))),
            PackBrowseSource.Owned =>
                query.Where(p => p.OwnerId == me),
            _ => query.Where(_ => false)
        };

        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim().ToLower();
            query = query.Where(p =>
                p.Name.ToLower().Contains(needle)
                || (p.Summary != null && p.Summary.ToLower().Contains(needle))
                || (p.Description != null && p.Description.ToLower().Contains(needle)));
        }

        var total = await query.CountAsync(ct);
        var page = await query
            .OrderByDescending(p => p.UpdatedAt)
            .Skip(offset).Take(limit)
            .ToListAsync(ct);

        var items = new List<PackSummary>(page.Count);
        foreach (var p in page)
        {
            var perms = await resolver.GetAsync(p, me, ct);
            items.Add(ToSummary(p, perms));
        }
        return Ok(new PackBrowsePage(items, offset, limit, total));
    }

    [HttpPost("{id:guid}/subscribe")]
    public async Task<ActionResult<PackSummary>> Subscribe(Guid id, CancellationToken ct)
    {
        var me = this.UserId();
        var pack = await db.Packs.Include(p => p.Owner).Include(p => p.LastUploadedBy)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pack is null) return NotFound();
        if (pack.OwnerId == me)
            return BadRequest(new { error = "You own this pack" });

        // Subscribing to a non-public pack is only allowed if the user already has
        // some path to view it (existing collaborator or team grant). For public
        // packs anyone may subscribe.
        var existingPerms = await resolver.GetAsync(pack, me, ct);
        if (pack.Visibility != PackVisibility.Public && !existingPerms.HasFlag(PackPermissions.View))
            return Forbid();

        // A public pack you found yourself needs a grant to read it; a pack already shared with you
        // has one, and must not be downgraded to ReadOnly by adding it to your list.
        var existing = await db.PackCollaborators
            .FirstOrDefaultAsync(c => c.PackId == id && c.UserId == me, ct);
        var changed = false;
        if (existing is null && !existingPerms.HasFlag(PackPermissions.View))
        {
            db.PackCollaborators.Add(new PackCollaborator
            {
                PackId = id,
                UserId = me,
                Permissions = PackPermissions.ReadOnly
            });
            changed = true;
        }

        // The listing is the part that actually puts it in the user's instances.
        var listed = await db.PackListings.AnyAsync(l => l.PackId == id && l.UserId == me, ct);
        if (!listed)
        {
            db.PackListings.Add(new PackListing { PackId = id, UserId = me });
            changed = true;
        }
        if (changed) await db.SaveChangesAsync(ct);

        // Reuse the permissions resolved above; the only possible change is the ReadOnly
        // grant just added.
        var perms = existingPerms | PackPermissions.ReadOnly;
        return Ok(ToSummary(pack, perms));
    }

    [HttpPost("{id:guid}/unsubscribe")]
    public async Task<IActionResult> Unsubscribe(Guid id, CancellationToken ct)
    {
        var me = this.UserId();
        var pack = await db.Packs.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pack is null) return NotFound();
        if (pack.OwnerId == me)
            return BadRequest(new { error = "Owners cannot unsubscribe; delete the pack instead" });

        var listing = await db.PackListings.FirstOrDefaultAsync(l => l.PackId == id && l.UserId == me, ct);
        var self = await db.PackCollaborators
            .FirstOrDefaultAsync(c => c.PackId == id && c.UserId == me, ct);

        if (listing is null && self is null)
            return NotFound(new { error = "This pack is not in your list" });

        if (listing is not null) db.PackListings.Remove(listing);

        // Only drop access that was self-granted by adding a public pack. A ReadOnly row on a
        // non-public pack came from the owner sharing it, and removing it would make
        // re-adding impossible.
        if (self is { Permissions: PackPermissions.ReadOnly } && pack.Visibility == PackVisibility.Public)
            db.PackCollaborators.Remove(self);

        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost]
    public async Task<ActionResult<PackSummary>> Create([FromBody] CreatePackRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 128)
            return BadRequest(new { error = "Pack name must be 1-128 characters" });
        if (req.Summary?.Length > PackText.SummaryMaxLength)
            return BadRequest(new { error = $"Pack summary must be {PackText.SummaryMaxLength} characters or fewer" });
        if (req.Description?.Length > PackText.DescriptionMaxLength)
            return BadRequest(new { error = $"Pack description must be {PackText.DescriptionMaxLength} characters or fewer" });

        var me = this.UserId();
        var pack = new Pack
        {
            Name = req.Name.Trim(),
            Summary = req.Summary,
            Description = req.Description,
            OwnerId = me,
            IsEmpty = req.IsEmpty,
            MinecraftVersion = req.IsEmpty ? null : req.MinecraftVersion,
            Loader = req.IsEmpty ? LoaderKind.None : req.Loader,
            LoaderVersion = req.IsEmpty ? null : req.LoaderVersion
        };

        // A launcher adding an instance it made while signed out keeps that instance's id, which its
        // folder and per-instance settings are filed under. Random ids from the launcher, so a taken
        // one is a conflict rather than something to hand over.
        if (req.Id is { } wanted)
        {
            if (wanted == Guid.Empty) return BadRequest(new { error = "That instance id is not valid." });
            if (await db.Packs.AnyAsync(p => p.Id == wanted, ct))
                return Conflict(new { error = "An instance with this id already exists." });
            pack.Id = wanted;
        }

        db.Packs.Add(pack);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (req.Id is not null)
        {
            // The same id arrived twice at once and the other request won.
            return Conflict(new { error = "An instance with this id already exists." });
        }
        await db.Entry(pack).Reference(p => p.Owner).LoadAsync(ct);
        return Ok(ToSummary(pack, PackPermissions.Full));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PackDetail>> Get(Guid id, CancellationToken ct)
    {
        var me = this.UserId();
        var pack = await db.Packs
            .Include(p => p.Owner)
            .Include(p => p.LastUploadedBy)
            .Include(p => p.Collaborators).ThenInclude(c => c.User)
            .Include(p => p.Teams).ThenInclude(t => t.Team)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pack is null) return NotFound();

        var perms = await resolver.GetAsync(pack, me, ct);
        if (!perms.HasFlag(PackPermissions.View))
            return Forbid();

        // Anybody who can see the pack can see who else can. Usernames only: sharing a pack is no
        // reason to hand out e-mail addresses, and none of these rows carries one.
        var collaborators = pack.Collaborators
            .Select(c => new PackCollaboratorEntry(c.UserId, c.User.UserName ?? "", c.Permissions))
            .ToList();
        var teams = await TeamEntriesAsync(pack.Teams, ct);

        // The share token and pending invitations are capabilities (a token is the access), so they
        // stay with the people who may manage sharing.
        var canManage = CanManageSharing(pack, me, perms);
        string? shareToken = null;
        List<PackInvitationEntry>? pending = null;
        if (canManage)
        {
            shareToken = pack.ShareToken;
            var now = DateTimeOffset.UtcNow;
            var invites = await db.PackInvitations
                .AsNoTracking()
                .Include(i => i.InvitedBy)
                .Where(i => i.PackId == id
                         && i.RevokedAt == null
                         && i.AcceptedAt == null
                         && (i.ExpiresAt == null || i.ExpiresAt > now))
                .OrderByDescending(i => i.CreatedAt)
                .ToListAsync(ct);
            pending = invites.Select(i => ToInvitationEntry(i, pack.Name, includeToken: true)).ToList();
        }

        var rules = pack.RulesJson is null
            ? new List<PackFileRule>()
            : JsonSerializer.Deserialize<List<PackFileRule>>(pack.RulesJson) ?? new List<PackFileRule>();

        return Ok(new PackDetail(
            pack.Id, pack.Name, pack.Description, pack.OwnerId, pack.Owner.UserName ?? "",
            pack.Visibility, pack.IsShared, pack.IsEmpty, pack.MinecraftVersion, pack.Loader, pack.LoaderVersion,
            pack.CreatedAt, pack.UpdatedAt, perms, collaborators, teams, rules, pack.Summary,
            pack.LastUploadedById, pack.LastUploadedBy?.UserName, pack.LastUploadedAt,
            shareToken, pending));
    }

    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<PackSummary>> Update(Guid id, [FromBody] UpdatePackRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var pack = await db.Packs.Include(p => p.Owner).Include(p => p.LastUploadedBy)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pack is null) return NotFound();

        // ManageCollaborators is presented as "full access, can manage sharing", so it covers the
        // pack's settings too (visibility, file rules). Deleting and transferring the pack stay
        // with the owner.
        var perms = await resolver.GetAsync(pack, me, ct);
        if (!CanManageSharing(pack, me, perms)) return Forbid();

        if (req.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 128)
                return BadRequest(new { error = "Pack name must be 1-128 characters" });
            pack.Name = req.Name.Trim();
        }
        if (req.Description is not null)
        {
            if (req.Description.Length > PackText.DescriptionMaxLength)
                return BadRequest(new { error = $"Pack description must be {PackText.DescriptionMaxLength} characters or fewer" });
            pack.Description = req.Description;
        }
        if (req.Summary is not null)
        {
            if (req.Summary.Length > PackText.SummaryMaxLength)
                return BadRequest(new { error = $"Pack summary must be {PackText.SummaryMaxLength} characters or fewer" });
            pack.Summary = req.Summary;
        }
        var visibilityChangedTo = req.Visibility is not null && req.Visibility.Value != pack.Visibility
            ? req.Visibility
            : null;
        if (req.Visibility is not null) pack.Visibility = req.Visibility.Value;
        if (req.IsShared is not null) pack.IsShared = req.IsShared.Value;
        if (req.IsEmpty is not null)
        {
            pack.IsEmpty = req.IsEmpty.Value;
            if (pack.IsEmpty)
            {
                pack.MinecraftVersion = null;
                pack.Loader = LoaderKind.None;
                pack.LoaderVersion = null;
            }
        }
        if (!pack.IsEmpty)
        {
            if (req.MinecraftVersion is not null) pack.MinecraftVersion = req.MinecraftVersion;
            if (req.Loader is not null) pack.Loader = req.Loader.Value;
            if (req.LoaderVersion is not null) pack.LoaderVersion = req.LoaderVersion;
        }
        if (req.Rules is not null)
            pack.RulesJson = req.Rules.Count == 0
                ? null
                : JsonSerializer.Serialize(req.Rules);
        pack.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        if (visibilityChangedTo is not null)
            await PackSharing.LogAsync(db, me, ActivityKind.VisibilityChanged, ActivitySubjectType.Pack,
                pack.Id, pack.Name, detail: visibilityChangedTo.Value.ToString(), ct: ct);

        // Re-resolved rather than assumed Full: a non-owner may have just changed the visibility
        // their own access depends on.
        var after = await resolver.GetAsync(pack, me, ct);
        return Ok(ToSummary(pack, after));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var me = this.UserId();
        var pack = await db.Packs.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pack is null) return NotFound();
        // Owner only, whatever else the caller holds: ManageCollaborators is about who may use the
        // pack, not about whether it continues to exist.
        if (pack.OwnerId != me) return Forbid();

        // Read the manifest's blobs before the cascade removes the rows, so the pack's files
        // are deleted with it. DeleteBlobIfUnreferenced checks every other table first, so
        // shared bytes stay.
        var hashes = await db.PackManifestEntries
            .Where(e => e.PackId == id)
            .Select(e => e.Hash)
            .Distinct()
            .ToListAsync(ct);

        db.Packs.Remove(pack);
        await db.SaveChangesAsync(ct);

        foreach (var hash in hashes)
            await ControllerHelpers.DeleteBlobIfUnreferencedAsync(db, blobs, hash, ct);

        return NoContent();
    }

    // ---- Collaborators ----

    [HttpPost("{id:guid}/collaborators")]
    public async Task<ActionResult<PackCollaboratorEntry>> AddCollaborator(
        Guid id, [FromBody] AddCollaboratorRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var (pack, perms) = await LoadForSharingAsync(id, ct);
        if (pack is null) return NotFound();
        if (!CanManageSharing(pack, me, perms)) return Forbid();

        var grantError = ValidateGrant(pack, me, perms, req.Permissions);
        if (grantError is not null) return BadRequest(new { error = grantError });

        var user = await ControllerHelpers.FindUserByNameAsync(db, req.Username, ct);
        if (user is null) return BadRequest(new { error = "User not found" });
        if (user.Id == pack.OwnerId) return BadRequest(new { error = "Owner is implicit" });

        var existing = await db.PackCollaborators
            .FirstOrDefaultAsync(c => c.PackId == id && c.UserId == user.Id, ct);
        var isNew = existing is null;
        if (existing is not null)
        {
            existing.Permissions = req.Permissions;
        }
        else
        {
            db.PackCollaborators.Add(new PackCollaborator
            {
                PackId = id, UserId = user.Id, Permissions = req.Permissions
            });
        }
        pack.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await PackSharing.LogAsync(db, me,
            isNew ? ActivityKind.Shared : ActivityKind.PermissionsChanged,
            ActivitySubjectType.Pack, pack.Id, pack.Name,
            targetUserId: user.Id, detail: req.Permissions.ToString(), ct: ct);

        return Ok(new PackCollaboratorEntry(user.Id, user.UserName ?? "", req.Permissions));
    }

    [HttpPatch("{id:guid}/collaborators/{userId:guid}")]
    public async Task<IActionResult> UpdateCollaborator(
        Guid id, Guid userId, [FromBody] UpdateCollaboratorRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var (pack, perms) = await LoadForSharingAsync(id, ct);
        if (pack is null) return NotFound();
        if (!CanManageSharing(pack, me, perms)) return Forbid();

        var grantError = ValidateGrant(pack, me, perms, req.Permissions);
        if (grantError is not null) return BadRequest(new { error = grantError });

        var row = await db.PackCollaborators.FirstOrDefaultAsync(c => c.PackId == id && c.UserId == userId, ct);
        if (row is null) return NotFound();
        row.Permissions = req.Permissions;
        pack.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await PackSharing.LogAsync(db, me, ActivityKind.PermissionsChanged, ActivitySubjectType.Pack,
            pack.Id, pack.Name, targetUserId: userId, detail: req.Permissions.ToString(), ct: ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}/collaborators/{userId:guid}")]
    public async Task<IActionResult> RemoveCollaborator(Guid id, Guid userId, CancellationToken ct)
    {
        var me = this.UserId();
        var (pack, perms) = await LoadForSharingAsync(id, ct);
        if (pack is null) return NotFound();
        // Anybody managing sharing may withdraw a grant, and anybody may withdraw their own.
        if (!CanManageSharing(pack, me, perms) && me != userId) return Forbid();

        var row = await db.PackCollaborators.FirstOrDefaultAsync(c => c.PackId == id && c.UserId == userId, ct);
        if (row is null) return NotFound();
        db.PackCollaborators.Remove(row);

        // Revoke the invitation that granted this too, or its token would let the person back in.
        var now = DateTimeOffset.UtcNow;
        var theirInvites = await db.PackInvitations
            .Where(i => i.PackId == id && i.InvitedUserId == userId && i.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var invite in theirInvites) invite.RevokedAt = now;

        pack.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        await PackSharing.LogAsync(db, me, ActivityKind.Unshared, ActivitySubjectType.Pack,
            pack.Id, pack.Name, targetUserId: userId, ct: ct);
        return NoContent();
    }

    // ---- Pack teams ----

    [HttpPost("{id:guid}/teams")]
    public async Task<ActionResult<PackTeamEntry>> AddTeam(
        Guid id, [FromBody] AddPackTeamRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var (pack, perms) = await LoadForSharingAsync(id, ct);
        if (pack is null) return NotFound();
        if (!CanManageSharing(pack, me, perms)) return Forbid();

        var grantError = ValidateGrant(pack, me, perms, req.Permissions);
        if (grantError is not null) return BadRequest(new { error = grantError });

        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == req.TeamId, ct);
        if (team is null) return BadRequest(new { error = "Team not found" });

        // A manager who is not the owner may only share with a team they are in, or they could push a
        // private pack into any team whose id they can guess. Same rule as the other content types.
        if (pack.OwnerId != me
            && !await db.TeamMembers.AnyAsync(tm => tm.TeamId == req.TeamId && tm.UserId == me, ct))
            return BadRequest(new { error = "You can only share this with a team you belong to." });

        var existing = await db.PackTeams.FirstOrDefaultAsync(pt => pt.PackId == id && pt.TeamId == req.TeamId, ct);
        var isNew = existing is null;
        if (existing is not null) existing.Permissions = req.Permissions;
        else db.PackTeams.Add(new PackTeam { PackId = id, TeamId = req.TeamId, Permissions = req.Permissions });

        pack.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        // Sharing with a team puts the pack in its members' libraries.
        if (isNew) await PackSharing.AddListingsForTeamAsync(db, id, req.TeamId, ct);

        await PackSharing.LogAsync(db, me,
            isNew ? ActivityKind.Shared : ActivityKind.PermissionsChanged,
            ActivitySubjectType.Pack, pack.Id, pack.Name,
            targetTeamId: team.Id, detail: req.Permissions.ToString(), ct: ct);

        var entries = await TeamEntriesAsync(
            new[] { new PackTeam { PackId = id, TeamId = team.Id, Team = team, Permissions = req.Permissions } }, ct);
        return Ok(entries[0]);
    }

    [HttpPatch("{id:guid}/teams/{teamId:guid}")]
    public async Task<IActionResult> UpdateTeam(
        Guid id, Guid teamId, [FromBody] UpdatePackTeamRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var (pack, perms) = await LoadForSharingAsync(id, ct);
        if (pack is null) return NotFound();
        if (!CanManageSharing(pack, me, perms)) return Forbid();

        var grantError = ValidateGrant(pack, me, perms, req.Permissions);
        if (grantError is not null) return BadRequest(new { error = grantError });

        var row = await db.PackTeams.FirstOrDefaultAsync(pt => pt.PackId == id && pt.TeamId == teamId, ct);
        if (row is null) return NotFound();
        row.Permissions = req.Permissions;
        pack.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await PackSharing.LogAsync(db, me, ActivityKind.PermissionsChanged, ActivitySubjectType.Pack,
            pack.Id, pack.Name, targetTeamId: teamId, detail: req.Permissions.ToString(), ct: ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}/teams/{teamId:guid}")]
    public async Task<IActionResult> RemoveTeam(Guid id, Guid teamId, CancellationToken ct)
    {
        var me = this.UserId();
        var (pack, perms) = await LoadForSharingAsync(id, ct);
        if (pack is null) return NotFound();
        if (!CanManageSharing(pack, me, perms)) return Forbid();

        var row = await db.PackTeams.FirstOrDefaultAsync(pt => pt.PackId == id && pt.TeamId == teamId, ct);
        if (row is null) return NotFound();
        db.PackTeams.Remove(row);
        pack.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        // Remove only the listings this grant was holding up (see PackSharing).
        await PackSharing.RemoveTeamListingsAsync(db, id, teamId, ct);

        await PackSharing.LogAsync(db, me, ActivityKind.Unshared, ActivitySubjectType.Pack,
            pack.Id, pack.Name, targetTeamId: teamId, ct: ct);
        return NoContent();
    }

    // ---- Ownership ----

    /// <summary>Hands the pack to one of its existing collaborators. Owner only.</summary>
    /// <remarks>
    /// Modelled on <c>POST /teams/{id}/transfer</c>. Storage counts against the owner's quota, so
    /// the transfer is refused when the new owner lacks room.
    /// </remarks>
    [HttpPost("{id:guid}/transfer")]
    public async Task<IActionResult> TransferOwnership(
        Guid id, [FromBody] TransferPackOwnershipRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var pack = await db.Packs.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pack is null) return NotFound();
        if (pack.OwnerId != me) return Forbid();
        if (req.UserId == pack.OwnerId) return BadRequest(new { error = "That user already owns this pack" });

        var target = await db.Users.FirstOrDefaultAsync(u => u.Id == req.UserId, ct);
        if (target is null) return BadRequest(new { error = "User not found" });
        if (!await db.PackCollaborators.AnyAsync(c => c.PackId == id && c.UserId == req.UserId, ct))
            return BadRequest(new { error = "The new owner must already be a collaborator on the pack" });

        // Every pack's files count against its owner's quota, shared or not, so the new owner needs room
        // for whatever of them they do not already store.
        var files = await db.PackManifestEntries
            .Where(e => e.PackId == id)
            .GroupBy(e => e.Hash)
            .Select(g => new { Hash = g.Key, Size = g.Max(e => e.Size) })
            .ToDictionaryAsync(f => f.Hash, f => f.Size, ct);
        if (!await guard.HasRoomForAsync(target.Id, files, ct))
            return BadRequest(new
            {
                error = $"{target.UserName} does not have enough storage quota left for this pack."
            });

        var previousOwner = pack.OwnerId;
        pack.OwnerId = req.UserId;
        pack.UpdatedAt = DateTimeOffset.UtcNow;

        // The new owner's collaborator row is redundant: the owner's access is implicit everywhere.
        var targetRow = await db.PackCollaborators.FirstOrDefaultAsync(c => c.PackId == id && c.UserId == req.UserId, ct);
        if (targetRow is not null) db.PackCollaborators.Remove(targetRow);

        // The outgoing owner keeps full access and the library listing, as a team transfer
        // keeps them a member.
        if (!await db.PackCollaborators.AnyAsync(c => c.PackId == id && c.UserId == previousOwner, ct))
            db.PackCollaborators.Add(new PackCollaborator
            {
                PackId = id, UserId = previousOwner, Permissions = PackPermissions.Full
            });
        if (!await db.PackListings.AnyAsync(l => l.PackId == id && l.UserId == previousOwner, ct))
            db.PackListings.Add(new PackListing { PackId = id, UserId = previousOwner });

        await db.SaveChangesAsync(ct);

        await PackSharing.LogAsync(db, me, ActivityKind.OwnershipTransferred, ActivitySubjectType.Pack,
            pack.Id, pack.Name, targetUserId: req.UserId, ct: ct);
        return NoContent();
    }

    // ---- Invitations and share links ----

    /// <summary>Every invitation ever minted for this pack: pending, accepted and revoked.</summary>
    [HttpGet("{id:guid}/invitations")]
    public async Task<ActionResult<IReadOnlyList<PackInvitationEntry>>> ListInvitations(Guid id, CancellationToken ct)
    {
        var me = this.UserId();
        var (pack, perms) = await LoadForSharingAsync(id, ct);
        if (pack is null) return NotFound();
        // Contains tokens, which are access, so only sharing managers get it (unlike the access
        // list in GET /packs/{id}).
        if (!CanManageSharing(pack, me, perms)) return Forbid();

        var invites = await db.PackInvitations
            .AsNoTracking()
            .Include(i => i.InvitedBy)
            .Where(i => i.PackId == id)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(ct);

        return Ok(invites.Select(i => ToInvitationEntry(i, pack.Name, includeToken: true)).ToList());
    }

    /// <summary>Invites somebody to the pack by name, or mints a share link when no name is given.</summary>
    [HttpPost("{id:guid}/invitations")]
    public async Task<ActionResult<PackInvitationEntry>> CreateInvitation(
        Guid id, [FromBody] CreatePackInvitationRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var (pack, perms) = await LoadForSharingAsync(id, ct);
        if (pack is null) return NotFound();
        if (!CanManageSharing(pack, me, perms)) return Forbid();

        var grantError = ValidateGrant(pack, me, perms, req.Permissions);
        if (grantError is not null) return BadRequest(new { error = grantError });

        var (expiresAt, expiryError) = ResolveExpiry(req.ExpiresInDays);
        if (expiryError is not null) return BadRequest(new { error = expiryError });
        if (req.Message is { Length: > 512 })
            return BadRequest(new { error = "That message is too long (512 characters maximum)." });

        PackInvitation invitation;
        if (string.IsNullOrWhiteSpace(req.Username))
        {
            invitation = await MintLinkAsync(pack, me, req.Permissions, expiresAt, req.Message, ct);
        }
        else
        {
            var user = await ControllerHelpers.FindUserByNameAsync(db, req.Username, ct);
            if (user is null) return BadRequest(new { error = "User not found" });
            if (user.Id == pack.OwnerId) return BadRequest(new { error = "Owner is implicit" });

            // One live invitation per person per pack: re-inviting somebody with different
            // permissions should change the offer, not leave two tokens with different answers.
            var now = DateTimeOffset.UtcNow;
            var live = await db.PackInvitations
                .Where(i => i.PackId == id && i.InvitedUserId == user.Id
                         && i.RevokedAt == null && i.AcceptedAt == null)
                .ToListAsync(ct);
            foreach (var old in live) old.RevokedAt = now;

            invitation = new PackInvitation
            {
                PackId = id,
                InvitedUserId = user.Id,
                InvitedUsername = user.UserName,
                InvitedByUserId = me,
                Permissions = req.Permissions,
                Token = PackSharing.NewShareToken(),
                ExpiresAt = expiresAt,
                Message = req.Message
            };
            db.PackInvitations.Add(invitation);
            await db.SaveChangesAsync(ct);

            await PackSharing.LogAsync(db, me, ActivityKind.InviteSent, ActivitySubjectType.Pack,
                pack.Id, pack.Name, targetUserId: user.Id, detail: req.Permissions.ToString(), ct: ct);
        }

        await db.Entry(invitation).Reference(i => i.InvitedBy).LoadAsync(ct);
        return Ok(ToInvitationEntry(invitation, pack.Name, includeToken: true));
    }

    /// <summary>Withdraws an invitation. Idempotent: revoking a revoked invitation is a no-op.</summary>
    [HttpDelete("{id:guid}/invitations/{invId:guid}")]
    public async Task<IActionResult> RevokeInvitation(Guid id, Guid invId, CancellationToken ct)
    {
        var me = this.UserId();
        var (pack, perms) = await LoadForSharingAsync(id, ct);
        if (pack is null) return NotFound();
        if (!CanManageSharing(pack, me, perms)) return Forbid();

        var invitation = await db.PackInvitations.FirstOrDefaultAsync(i => i.Id == invId && i.PackId == id, ct);
        if (invitation is null) return NotFound();
        if (invitation.RevokedAt is not null) return NoContent();

        invitation.RevokedAt = DateTimeOffset.UtcNow;
        // Revoking the row the share link points at has to take the pointer with it, or the pack
        // keeps advertising a token that no longer redeems.
        if (pack.ShareToken == invitation.Token)
        {
            pack.ShareToken = null;
            pack.ShareTokenCreatedAt = null;
        }
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Mints (or re-mints) the pack's share link.</summary>
    [HttpPost("{id:guid}/share-link")]
    public async Task<ActionResult<ShareLinkInfo>> CreateShareLink(
        Guid id, [FromBody] CreateShareLinkRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var (pack, perms) = await LoadForSharingAsync(id, ct);
        if (pack is null) return NotFound();
        if (!CanManageSharing(pack, me, perms)) return Forbid();

        var grantError = ValidateGrant(pack, me, perms, req.Permissions);
        if (grantError is not null) return BadRequest(new { error = grantError });

        var (expiresAt, expiryError) = ResolveExpiry(req.ExpiresInDays);
        if (expiryError is not null) return BadRequest(new { error = expiryError });

        var link = await MintLinkAsync(pack, me, req.Permissions, expiresAt, message: null, ct);
        return Ok(new ShareLinkInfo(link.Token, link.Permissions, link.CreatedAt, link.ExpiresAt));
    }

    /// <summary>Kills the pack's share link. What people redeemed while it was live stays granted.</summary>
    [HttpDelete("{id:guid}/share-link")]
    public async Task<IActionResult> RevokeShareLink(Guid id, CancellationToken ct)
    {
        var me = this.UserId();
        var (pack, perms) = await LoadForSharingAsync(id, ct);
        if (pack is null) return NotFound();
        if (!CanManageSharing(pack, me, perms)) return Forbid();

        var now = DateTimeOffset.UtcNow;
        var links = await db.PackInvitations
            .Where(i => i.PackId == id && i.InvitedUserId == null && i.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var link in links) link.RevokedAt = now;

        pack.ShareToken = null;
        pack.ShareTokenCreatedAt = null;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>Loads a pack together with what the caller may do to it.</summary>
    private async Task<(Pack? Pack, PackPermissions Perms)> LoadForSharingAsync(Guid id, CancellationToken ct)
    {
        var pack = await db.Packs.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pack is null) return (null, PackPermissions.None);
        return (pack, await resolver.GetAsync(pack, this.UserId(), ct));
    }

    /// <summary>Who may change who else can see this pack: the owner, and anybody given
    /// <see cref="PackPermissions.ManageCollaborators"/>.</summary>
    private static bool CanManageSharing(Pack pack, Guid me, PackPermissions perms) =>
        pack.OwnerId == me || perms.HasFlag(PackPermissions.ManageCollaborators);

    /// <summary>Checks a permission set somebody is about to hand to somebody else.</summary>
    /// <returns>The sentence to show the caller, or null when the grant is fine.</returns>
    private static string? ValidateGrant(Pack pack, Guid me, PackPermissions held, PackPermissions requested)
    {
        if ((requested & ~PackPermissions.Full) != 0)
            return "That permission set has bits this server does not know.";
        if (requested == PackPermissions.None)
            return "Pick at least one permission.";
        // A delegate cannot grant what they do not hold. The owner holds Full, so this only limits
        // ManageCollaborators holders.
        if (pack.OwnerId != me && (requested & ~held) != 0)
            return "You cannot give away a permission you do not have yourself.";
        return null;
    }

    /// <summary>Turns a requested lifetime in days into an absolute expiry.</summary>
    private static (DateTimeOffset? ExpiresAt, string? Error) ResolveExpiry(int? days)
    {
        if (days is null) return (null, null);
        if (days < 1 || days > MaxExpiryDays)
            return (null, $"An invitation can last between 1 and {MaxExpiryDays} days.");
        return (DateTimeOffset.UtcNow.AddDays(days.Value), null);
    }

    /// <summary>Replaces the pack's share link with a fresh one and points the pack at it.</summary>
    /// <remarks>
    /// A link is an invitation row with no invited user, with the same expiry, revocation and
    /// accept endpoint. <c>Pack.ShareToken</c> only points at the live row, so re-minting revokes
    /// the previous one first; otherwise revoking the link would leave an older one working.
    /// </remarks>
    private async Task<PackInvitation> MintLinkAsync(
        Pack pack, Guid me, PackPermissions permissions, DateTimeOffset? expiresAt, string? message, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var live = await db.PackInvitations
            .Where(i => i.PackId == pack.Id && i.InvitedUserId == null && i.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var old in live) old.RevokedAt = now;

        var link = new PackInvitation
        {
            PackId = pack.Id,
            InvitedUserId = null,
            InvitedByUserId = me,
            Permissions = permissions,
            Token = PackSharing.NewShareToken(),
            CreatedAt = now,
            ExpiresAt = expiresAt,
            Message = message
        };
        db.PackInvitations.Add(link);
        pack.ShareToken = link.Token;
        pack.ShareTokenCreatedAt = now;
        await db.SaveChangesAsync(ct);

        await PackSharing.LogAsync(db, me, ActivityKind.InviteSent, ActivitySubjectType.Pack,
            pack.Id, pack.Name, detail: $"link, {permissions}", ct: ct);
        return link;
    }

    /// <summary>Builds the team rows of an access list, each with a few of its members.</summary>
    /// <remarks>Wraps <see cref="PackSharing.TeamEntriesAsync"/>, picking the fields from the
    /// pack's grant rows.</remarks>
    private Task<List<PackTeamEntry>> TeamEntriesAsync(
        IEnumerable<PackTeam> packTeams, CancellationToken ct) =>
        PackSharing.TeamEntriesAsync(db, packTeams.Select(t => (t.TeamId, t.Team.Name, t.Permissions)), ct);

    /// <summary>Shapes one invitation row for the wire.</summary>
    /// <param name="includeToken">Tokens go only to people who may manage the pack's sharing and to
    /// the invited person, since the token is the access.</param>
    internal static PackInvitationEntry ToInvitationEntry(PackInvitation i, string packName, bool includeToken) => new(
        i.Id, i.PackId, packName,
        i.InvitedUserId, i.InvitedUsername,
        i.InvitedByUserId, i.InvitedBy?.UserName ?? "",
        i.Permissions, i.Message,
        i.CreatedAt, i.ExpiresAt, i.AcceptedAt, i.RevokedAt,
        IsLink: i.InvitedUserId is null && i.InvitedUsername is null,
        Token: includeToken ? i.Token : null);

    private static PackSummary ToSummary(Pack p, PackPermissions perms) => new(
        p.Id, p.Name, p.Description, p.OwnerId, p.Owner.UserName ?? "",
        p.Visibility, p.IsShared, p.IsEmpty, p.MinecraftVersion, p.Loader, p.LoaderVersion,
        p.CreatedAt, p.UpdatedAt, perms, p.Summary,
        p.LastUploadedById, p.LastUploadedBy?.UserName, p.LastUploadedAt);
}
