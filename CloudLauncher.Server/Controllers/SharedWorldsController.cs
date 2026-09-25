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
[Route("worlds")]
public class SharedWorldsController(
    AppDbContext db, SharedWorldPermissionResolver resolver, BlobStore blobs, UploadGuard guard) : ControllerBase
{
    // Worlds can be much larger than mods, so allow 256 MB.
    private const long MaxWorldBytes = 256L * 1024 * 1024;

    [HttpGet("browse")]
    public async Task<ActionResult<WorldBrowsePage>> Browse(
        [FromQuery] WorldBrowseSource source = WorldBrowseSource.Public,
        [FromQuery] Guid? teamId = null,
        [FromQuery] string? q = null,
        [FromQuery] string? mcVersion = null,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 25,
        CancellationToken ct = default)
    {
        if (limit <= 0 || limit > 100) limit = 25;
        if (offset < 0) offset = 0;

        var me = this.UserId();
        var query = db.SharedWorlds.Include(w => w.Owner).AsQueryable();

        query = source switch
        {
            WorldBrowseSource.Public =>
                query.Where(w => w.Visibility == PackVisibility.Public),
            WorldBrowseSource.Personal =>
                query.Where(w => w.OwnerId == me),
            WorldBrowseSource.Shared =>
                query.Where(w => w.OwnerId != me
                              && db.SharedWorldCollaborators.Any(c => c.WorldId == w.Id && c.UserId == me)),
            WorldBrowseSource.Team =>
                teamId.HasValue
                    ? query.Where(w => db.SharedWorldTeams.Any(wt => wt.WorldId == w.Id && wt.TeamId == teamId.Value)
                                    && db.TeamMembers.Any(tm => tm.TeamId == teamId.Value && tm.UserId == me))
                    : query.Where(w => db.SharedWorldTeams.Any(wt => wt.WorldId == w.Id
                                                       && db.TeamMembers.Any(tm => tm.TeamId == wt.TeamId && tm.UserId == me))),
            _ => query.Where(_ => false)
        };

        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim().ToLower();
            query = query.Where(w =>
                w.Name.ToLower().Contains(needle)
                || (w.Summary != null && w.Summary.ToLower().Contains(needle))
                || (w.Description != null && w.Description.ToLower().Contains(needle)));
        }
        if (!string.IsNullOrWhiteSpace(mcVersion))
            query = query.Where(w => w.McVersion == mcVersion);

        var total = await query.CountAsync(ct);
        var page = await query.OrderByDescending(w => w.UpdatedAt).Skip(offset).Take(limit).ToListAsync(ct);

        // One grouped count for the whole page rather than a COUNT per row. Worlds with no uploaded
        // version are simply absent from the dictionary and fall through to 0 below.
        var pageIds = page.Select(w => w.Id).ToList();
        var versionCounts = await db.SharedWorldVersions
            .Where(v => pageIds.Contains(v.WorldId))
            .GroupBy(v => v.WorldId)
            .Select(g => new { WorldId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.WorldId, x => x.Count, ct);

        var items = new List<SharedWorldSummary>(page.Count);
        foreach (var w in page)
        {
            var perms = await resolver.GetAsync(w, me, ct);
            items.Add(ToSummary(w, perms, versionCounts.GetValueOrDefault(w.Id)));
        }
        return Ok(new WorldBrowsePage(items, offset, limit, total));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<SharedWorldDetail>> Get(Guid id, CancellationToken ct)
    {
        var me = this.UserId();
        var w = await db.SharedWorlds
            .Include(x => x.Owner)
            .Include(x => x.Versions)
            .Include(x => x.Collaborators).ThenInclude(c => c.User)
            .Include(x => x.Teams).ThenInclude(t => t.Team)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (w is null) return NotFound();

        var perms = await resolver.GetAsync(w, me, ct);
        if (!perms.HasFlag(PackPermissions.View)) return Forbid();

        // Anybody who can see the world can see who else can, as with instances. Collaborators
        // need their own row to leave, and the launcher uses the list to tell direct collaborators
        // from team members.
        var collabs = w.Collaborators
            .Select(c => new PackCollaboratorEntry(c.UserId, c.User.UserName ?? "", c.Permissions))
            .ToList();
        var teams = await PackSharing.TeamEntriesAsync(
            db, w.Teams.Select(t => (t.TeamId, t.Team.Name, t.Permissions)), ct);

        var versions = w.Versions.OrderByDescending(v => v.PublishedAt)
            .Select(v => new SharedWorldVersionInfo(
                v.Id, v.VersionString, v.Changelog, v.BlobHash, v.FileSize, v.FileName,
                v.McVersion, v.PublishedAt))
            .ToList();

        return Ok(new SharedWorldDetail(
            w.Id, w.Slug, w.Name, w.Summary, w.Description,
            w.OwnerId, w.Owner.UserName ?? "",
            w.Visibility, w.IconBlobHash, w.McVersion,
            w.CreatedAt, w.UpdatedAt, perms,
            versions, collabs, teams));
    }

    [HttpPost]
    public async Task<ActionResult<SharedWorldSummary>> Create([FromBody] CreateWorldRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 128)
            return BadRequest(new { error = "World name must be 1-128 characters" });

        var me = this.UserId();
        var slug = await GenerateUniqueSlugAsync(req.Name, ct);
        var w = new SharedWorld
        {
            Name = req.Name.Trim(),
            Slug = slug,
            Summary = req.Summary,
            Description = req.Description,
            OwnerId = me,
            Visibility = req.Visibility,
            McVersion = req.McVersion
        };
        db.SharedWorlds.Add(w);
        await db.SaveChangesAsync(ct);
        await db.Entry(w).Reference(x => x.Owner).LoadAsync(ct);
        return Ok(ToSummary(w, PackPermissions.Full, versionCount: 0));
    }

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateWorldRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var w = await db.SharedWorlds.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (w is null) return NotFound();
        // Owner only, as with bundles: upload access covers versions, while the name and visibility are
        // the owner's. This also protects against 1.8.2 clients, which send the local save's name,
        // overview and visibility before every upload and would otherwise rename or re-share the world.
        if (w.OwnerId != me) return Forbid();

        if (req.Name is not null) w.Name = req.Name.Trim();
        if (req.Summary is not null) w.Summary = req.Summary;
        if (req.Description is not null) w.Description = req.Description;
        var visibilityChangedTo = req.Visibility is { } wanted && wanted != w.Visibility ? req.Visibility : null;
        if (req.Visibility is not null) w.Visibility = req.Visibility.Value;
        // Lets a world created with the wrong Minecraft version be corrected. Null means
        // "leave it alone".
        if (req.McVersion is not null) w.McVersion = req.McVersion;
        w.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        if (visibilityChangedTo is not null)
            await PackSharing.LogAsync(db, me, ActivityKind.VisibilityChanged, ActivitySubjectType.World,
                w.Id, w.Name, detail: visibilityChangedTo.Value.ToString(), ct: ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var me = this.UserId();
        var w = await db.SharedWorlds.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (w is null) return NotFound();
        if (w.OwnerId != me) return Forbid();
        db.SharedWorlds.Remove(w);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ---- Collaborators ----
    //
    // Sharing can be changed by the owner and anybody given ManageCollaborators, as with
    // instances and bundles.

    [HttpPost("{id:guid}/collaborators")]
    public async Task<ActionResult<PackCollaboratorEntry>> AddCollaborator(
        Guid id, [FromBody] AddCollaboratorRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var (world, perms) = await LoadAsync(id, ct);
        if (world is null) return NotFound();
        if (!ControllerHelpers.CanManageSharing(world.OwnerId, me, perms)) return Forbid();

        var grantError = ControllerHelpers.ValidateGrant(world.OwnerId, me, perms, req.Permissions);
        if (grantError is not null) return BadRequest(new { error = grantError });

        var user = await ControllerHelpers.FindUserByNameAsync(db, req.Username, ct);
        if (user is null) return BadRequest(new { error = "User not found" });
        if (user.Id == world.OwnerId) return BadRequest(new { error = "Owner is implicit" });

        var existing = await db.SharedWorldCollaborators
            .FirstOrDefaultAsync(c => c.WorldId == id && c.UserId == user.Id, ct);
        var isNew = existing is null;
        if (existing is not null)
            existing.Permissions = req.Permissions;
        else
            db.SharedWorldCollaborators.Add(new SharedWorldCollaborator { WorldId = id, UserId = user.Id, Permissions = req.Permissions });

        world.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await PackSharing.LogAsync(db, me,
            isNew ? ActivityKind.Shared : ActivityKind.PermissionsChanged,
            ActivitySubjectType.World, world.Id, world.Name,
            targetUserId: user.Id, detail: req.Permissions.ToString(), ct: ct);
        return Ok(new PackCollaboratorEntry(user.Id, user.UserName ?? "", req.Permissions));
    }

    [HttpPatch("{id:guid}/collaborators/{userId:guid}")]
    public async Task<IActionResult> UpdateCollaborator(
        Guid id, Guid userId, [FromBody] UpdateCollaboratorRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var (world, perms) = await LoadAsync(id, ct);
        if (world is null) return NotFound();
        if (!ControllerHelpers.CanManageSharing(world.OwnerId, me, perms)) return Forbid();

        var grantError = ControllerHelpers.ValidateGrant(world.OwnerId, me, perms, req.Permissions);
        if (grantError is not null) return BadRequest(new { error = grantError });

        var row = await db.SharedWorldCollaborators.FirstOrDefaultAsync(c => c.WorldId == id && c.UserId == userId, ct);
        if (row is null) return NotFound(new { error = "They are not a collaborator on this world." });
        row.Permissions = req.Permissions;
        world.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await PackSharing.LogAsync(db, me, ActivityKind.PermissionsChanged, ActivitySubjectType.World,
            world.Id, world.Name, targetUserId: userId, detail: req.Permissions.ToString(), ct: ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}/collaborators/{userId:guid}")]
    public async Task<IActionResult> RemoveCollaborator(Guid id, Guid userId, CancellationToken ct)
    {
        var me = this.UserId();
        var (world, perms) = await LoadAsync(id, ct);
        if (world is null) return NotFound();
        // Anybody managing sharing may withdraw a grant, and anybody may withdraw their own
        // (leave), as on instances.
        if (!ControllerHelpers.CanManageSharing(world.OwnerId, me, perms) && me != userId) return Forbid();

        var row = await db.SharedWorldCollaborators.FirstOrDefaultAsync(c => c.WorldId == id && c.UserId == userId, ct);
        if (row is null)
            return NotFound(new
            {
                error = userId == me
                    ? "You are not a collaborator on this world, so there is nothing to leave."
                    : "They are not a collaborator on this world."
            });
        db.SharedWorldCollaborators.Remove(row);
        world.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await PackSharing.LogAsync(db, me, ActivityKind.Unshared, ActivitySubjectType.World,
            world.Id, world.Name, targetUserId: userId, ct: ct);
        return NoContent();
    }

    // ---- World teams ----

    [HttpPost("{id:guid}/teams")]
    public async Task<ActionResult<PackTeamEntry>> AddTeam(
        Guid id, [FromBody] AddPackTeamRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var (world, perms) = await LoadAsync(id, ct);
        if (world is null) return NotFound();
        if (!ControllerHelpers.CanManageSharing(world.OwnerId, me, perms)) return Forbid();

        var grantError = ControllerHelpers.ValidateGrant(world.OwnerId, me, perms, req.Permissions);
        if (grantError is not null) return BadRequest(new { error = grantError });

        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == req.TeamId, ct);
        if (team is null) return BadRequest(new { error = "Team not found" });

        // A manager who is not the owner may only share with a team they are in, or they could
        // publish a private world to any team whose id they can guess. Same rule as bundles.
        if (world.OwnerId != me
            && !await db.TeamMembers.AnyAsync(tm => tm.TeamId == req.TeamId && tm.UserId == me, ct))
            return BadRequest(new { error = "You can only share this with a team you belong to." });

        var existing = await db.SharedWorldTeams.FirstOrDefaultAsync(wt => wt.WorldId == id && wt.TeamId == req.TeamId, ct);
        var isNew = existing is null;
        if (existing is not null)
            existing.Permissions = req.Permissions;
        else
            db.SharedWorldTeams.Add(new SharedWorldTeam { WorldId = id, TeamId = req.TeamId, Permissions = req.Permissions });

        world.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await PackSharing.LogAsync(db, me,
            isNew ? ActivityKind.Shared : ActivityKind.PermissionsChanged,
            ActivitySubjectType.World, world.Id, world.Name,
            targetTeamId: team.Id, detail: req.Permissions.ToString(), ct: ct);

        var entries = await PackSharing.TeamEntriesAsync(db, [(team.Id, team.Name, req.Permissions)], ct);
        return Ok(entries[0]);
    }

    [HttpPatch("{id:guid}/teams/{teamId:guid}")]
    public async Task<IActionResult> UpdateTeam(
        Guid id, Guid teamId, [FromBody] UpdatePackTeamRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var (world, perms) = await LoadAsync(id, ct);
        if (world is null) return NotFound();
        if (!ControllerHelpers.CanManageSharing(world.OwnerId, me, perms)) return Forbid();

        var grantError = ControllerHelpers.ValidateGrant(world.OwnerId, me, perms, req.Permissions);
        if (grantError is not null) return BadRequest(new { error = grantError });

        var row = await db.SharedWorldTeams.FirstOrDefaultAsync(wt => wt.WorldId == id && wt.TeamId == teamId, ct);
        if (row is null) return NotFound(new { error = "That team is not on this world." });
        row.Permissions = req.Permissions;
        world.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await PackSharing.LogAsync(db, me, ActivityKind.PermissionsChanged, ActivitySubjectType.World,
            world.Id, world.Name, targetTeamId: teamId, detail: req.Permissions.ToString(), ct: ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}/teams/{teamId:guid}")]
    public async Task<IActionResult> RemoveTeam(Guid id, Guid teamId, CancellationToken ct)
    {
        var me = this.UserId();
        var (world, perms) = await LoadAsync(id, ct);
        if (world is null) return NotFound();
        if (!ControllerHelpers.CanManageSharing(world.OwnerId, me, perms)) return Forbid();

        var row = await db.SharedWorldTeams.FirstOrDefaultAsync(wt => wt.WorldId == id && wt.TeamId == teamId, ct);
        if (row is null) return NotFound(new { error = "That team is not on this world." });
        db.SharedWorldTeams.Remove(row);
        world.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await PackSharing.LogAsync(db, me, ActivityKind.Unshared, ActivitySubjectType.World,
            world.Id, world.Name, targetTeamId: teamId, ct: ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/versions")]
    [RequestSizeLimit(MaxWorldBytes + (1 << 16))]
    public async Task<ActionResult<SharedWorldVersionInfo>> UploadVersion(
        Guid id,
        [FromForm] IFormFile file,
        [FromForm] string metadata,
        CancellationToken ct)
    {
        var me = this.UserId();
        var (w, perms) = await LoadAsync(id, ct);
        if (w is null) return NotFound();
        // UploadShared is enough, as with bundles: it is offered as "can upload changes", and
        // several players of a shared world may need to publish new saves.
        if (!perms.HasFlag(PackPermissions.UploadShared)) return Forbid();
        if (file is null || file.Length == 0) return BadRequest(new { error = "File required" });
        if (file.Length > MaxWorldBytes) return BadRequest(new { error = $"File too large (max {MaxWorldBytes / (1024 * 1024)} MB)" });

        CreateWorldVersionRequest? req;
        try { req = JsonSerializer.Deserialize<CreateWorldVersionRequest>(metadata, JsonOpts); }
        catch { return BadRequest(new { error = "Invalid metadata JSON" }); }
        if (req is null || string.IsNullOrWhiteSpace(req.VersionString))
            return BadRequest(new { error = "VersionString required" });

        // The disk floor and the world owner's quota, before the save reaches the blob store. A
        // collaborator's upload spends the owner's quota, as a pack sync does.
        if (await guard.RefuseAsync(w.OwnerId, me, file, ct) is { } refusal) return refusal;

        StoredBlob stored;
        await using (var s = file.OpenReadStream())
            stored = await blobs.PutAsync(s, ct);
        if (await guard.ChargeAsync(w.OwnerId, me, stored, ct) is { } overQuota) return overQuota;
        var hash = stored.Hash;

        var version = new SharedWorldVersion
        {
            WorldId = w.Id,
            VersionString = req.VersionString.Trim(),
            Changelog = req.Changelog,
            BlobHash = hash,
            FileSize = file.Length,
            FileName = string.IsNullOrWhiteSpace(req.FileName) ? file.FileName : req.FileName,
            McVersion = req.McVersion ?? w.McVersion
        };
        db.SharedWorldVersions.Add(version);
        if (!string.IsNullOrWhiteSpace(req.McVersion)) w.McVersion = req.McVersion;
        w.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        guard.Settle(w.OwnerId, hash);

        await PackSharing.LogAsync(db, me, ActivityKind.VersionPublished, ActivitySubjectType.World,
            w.Id, w.Name, detail: version.VersionString, ct: ct);

        return Ok(new SharedWorldVersionInfo(
            version.Id, version.VersionString, version.Changelog,
            version.BlobHash, version.FileSize, version.FileName,
            version.McVersion, version.PublishedAt));
    }

    /// <summary>Removes one uploaded version of a shared world.</summary>
    /// <remarks>
    /// Deleting the last version is allowed and leaves the world in place, the same state
    /// <see cref="Create"/> produces. Owner only, unlike publishing (as with bundles), since
    /// removing a version destroys someone else's history.
    /// </remarks>
    [HttpDelete("{id:guid}/versions/{versionId:guid}")]
    public async Task<IActionResult> DeleteVersion(Guid id, Guid versionId, CancellationToken ct)
    {
        var me = this.UserId();
        var w = await db.SharedWorlds.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (w is null) return NotFound();
        if (w.OwnerId != me) return Forbid();

        var version = await db.SharedWorldVersions
            .FirstOrDefaultAsync(v => v.Id == versionId && v.WorldId == id, ct);
        if (version is null) return NotFound();

        var hash = version.BlobHash;
        db.SharedWorldVersions.Remove(version);
        w.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await ControllerHelpers.DeleteBlobIfUnreferencedAsync(db, blobs, hash, ct);
        return NoContent();
    }

    // ---- Icon ----

    /// <summary>Sets the world's icon.</summary>
    /// <remarks>
    /// Same shape as the mod and resource-pack routes, so one client-side helper covers all three.
    /// </remarks>
    [HttpPost("{id:guid}/icon")]
    [RequestSizeLimit(ControllerHelpers.MaxIconBytes + (1 << 16))]
    public async Task<IActionResult> UploadIcon(Guid id, [FromForm] IFormFile file, CancellationToken ct)
    {
        var me = this.UserId();
        var world = await db.SharedWorlds.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (world is null) return NotFound();
        if (world.OwnerId != me) return Forbid();

        var (hash, failure) = await ControllerHelpers.TryStoreIconAsync(blobs, guard, world.OwnerId, me, file, ct);
        if (hash is null) return failure!;

        var previous = world.IconBlobHash;
        world.IconBlobHash = hash;
        world.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        guard.Settle(world.OwnerId, hash);

        if (previous is not null && previous != hash)
            await ControllerHelpers.DeleteBlobIfUnreferencedAsync(db, blobs, previous, ct);
        return Ok(new { iconBlobHash = hash });
    }

    /// <summary>Serves the world's icon, or 404 when it has none.</summary>
    /// <remarks>Anonymous under the same visibility rule as the world itself, so an &lt;Image&gt; with
    /// no Authorization header can show a public world's art.</remarks>
    [AllowAnonymous]
    [HttpGet("{id:guid}/icon")]
    public async Task<IActionResult> GetIcon(Guid id, CancellationToken ct)
    {
        var world = await db.SharedWorlds.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (world is null) return NotFound();

        var perms = await resolver.GetAsync(world, this.UserIdOrNull(), ct);
        if (!perms.HasFlag(PackPermissions.View)) return NotFound();

        if (string.IsNullOrEmpty(world.IconBlobHash) || !blobs.Exists(world.IconBlobHash)) return NotFound();
        return File(blobs.OpenRead(world.IconBlobHash), ControllerHelpers.IconResponseContentType);
    }

    /// <summary>Clears the world's icon.</summary>
    [HttpDelete("{id:guid}/icon")]
    public async Task<IActionResult> DeleteIcon(Guid id, CancellationToken ct)
    {
        var me = this.UserId();
        var world = await db.SharedWorlds.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (world is null) return NotFound();
        if (world.OwnerId != me) return Forbid();

        var previous = world.IconBlobHash;
        if (previous is null) return NoContent();

        world.IconBlobHash = null;
        world.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await ControllerHelpers.DeleteBlobIfUnreferencedAsync(db, blobs, previous, ct);
        return NoContent();
    }

    [HttpGet("{id:guid}/files/{versionId:guid}")]
    public async Task<IActionResult> Download(Guid id, Guid versionId, CancellationToken ct)
    {
        var me = this.UserId();
        var w = await db.SharedWorlds.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (w is null) return NotFound();
        var perms = await resolver.GetAsync(w, me, ct);
        if (!perms.HasFlag(PackPermissions.Download)) return Forbid();

        var version = await db.SharedWorldVersions.FirstOrDefaultAsync(v => v.Id == versionId && v.WorldId == id, ct);
        if (version is null) return NotFound();
        if (!blobs.Exists(version.BlobHash)) return NotFound();

        return File(blobs.OpenRead(version.BlobHash), "application/zip", version.FileName);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Loads a world together with what the caller may do to it.</summary>
    private async Task<(SharedWorld? World, PackPermissions Perms)> LoadAsync(Guid id, CancellationToken ct)
    {
        var world = await db.SharedWorlds.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (world is null) return (null, PackPermissions.None);
        return (world, await resolver.GetAsync(world, this.UserId(), ct));
    }

    private async Task<string> GenerateUniqueSlugAsync(string name, CancellationToken ct)
    {
        var basic = new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        basic = string.Join('-', basic.Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (basic.Length == 0) basic = "world";
        if (basic.Length > 80) basic = basic[..80];

        var candidate = basic;
        var n = 1;
        while (await db.SharedWorlds.AnyAsync(w => w.Slug == candidate, ct))
            candidate = $"{basic}-{++n}";
        return candidate;
    }

    private static SharedWorldSummary ToSummary(SharedWorld w, PackPermissions perms, int versionCount) => new(
        w.Id, w.Slug, w.Name, w.Summary, w.OwnerId, w.Owner.UserName ?? "",
        w.Visibility, w.IconBlobHash, w.McVersion,
        w.CreatedAt, w.UpdatedAt, perms, versionCount);
}
