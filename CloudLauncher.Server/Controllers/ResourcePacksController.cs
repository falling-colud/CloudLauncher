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
[Route("resourcepacks")]
public class ResourcePacksController(
    AppDbContext db,
    ResourcePackPermissionResolver resolver,
    BlobStore blobs,
    UploadGuard guard) : ControllerBase
{
    private const long MaxResourcePackBytes = 64L * 1024 * 1024; // 64 MB per zip

    [HttpGet("browse")]
    public async Task<ActionResult<ResourcePackBrowsePage>> Browse(
        [FromQuery] ResourcePackBrowseSource source = ResourcePackBrowseSource.Public,
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
        var query = db.HostedResourcePacks.Include(p => p.Owner).AsQueryable();

        query = source switch
        {
            ResourcePackBrowseSource.Public =>
                query.Where(p => p.Visibility == PackVisibility.Public),
            ResourcePackBrowseSource.Personal =>
                query.Where(p => p.OwnerId == me),
            ResourcePackBrowseSource.Shared =>
                query.Where(p => p.OwnerId != me
                                  && db.HostedResourcePackCollaborators.Any(c => c.ResourcePackId == p.Id && c.UserId == me)),
            ResourcePackBrowseSource.Team =>
                teamId.HasValue
                    ? query.Where(p => db.HostedResourcePackTeams.Any(pt => pt.ResourcePackId == p.Id && pt.TeamId == teamId.Value)
                                         && db.TeamMembers.Any(tm => tm.TeamId == teamId.Value && tm.UserId == me))
                    : query.Where(p => db.HostedResourcePackTeams.Any(pt => pt.ResourcePackId == p.Id
                                                                              && db.TeamMembers.Any(tm => tm.TeamId == pt.TeamId && tm.UserId == me))),
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

        if (!string.IsNullOrWhiteSpace(mcVersion))
            query = query.Where(p => p.McVersionsCsv != null && p.McVersionsCsv.Contains(mcVersion));

        var total = await query.CountAsync(ct);
        var page = await query
            .OrderByDescending(p => p.UpdatedAt)
            .Skip(offset).Take(limit)
            .ToListAsync(ct);

        // One grouped count for the page instead of a COUNT per row. Packs with no uploaded version
        // are missing from the dictionary and count as 0 below.
        var pageIds = page.Select(p => p.Id).ToList();
        var versionCounts = await db.HostedResourcePackVersions
            .Where(v => pageIds.Contains(v.ResourcePackId))
            .GroupBy(v => v.ResourcePackId)
            .Select(g => new { PackId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.PackId, x => x.Count, ct);

        var items = new List<HostedResourcePackSummary>(page.Count);
        foreach (var p in page)
        {
            var perms = await resolver.GetAsync(p, me, ct);
            items.Add(ToSummary(p, perms, versionCounts.GetValueOrDefault(p.Id)));
        }

        return Ok(new ResourcePackBrowsePage(items, offset, limit, total));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<HostedResourcePackDetail>> Get(Guid id, CancellationToken ct)
    {
        var me = this.UserId();
        var pack = await db.HostedResourcePacks
            .Include(p => p.Owner)
            .Include(p => p.Versions)
            .Include(p => p.Collaborators).ThenInclude(c => c.User)
            .Include(p => p.Teams).ThenInclude(t => t.Team)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pack is null) return NotFound();

        var perms = await resolver.GetAsync(pack, me, ct);
        if (!perms.HasFlag(PackPermissions.View))
            return Forbid();

        // Anyone who can see the pack can see who else can, as with instances, so a collaborator
        // can find their own row to leave or see whom to ask for more access. Usernames only; these
        // rows carry no e-mail.
        var collabs = pack.Collaborators
            .Select(c => new PackCollaboratorEntry(c.UserId, c.User.UserName ?? "", c.Permissions))
            .ToList();
        var teams = await PackSharing.TeamEntriesAsync(
            db, pack.Teams.Select(t => (t.TeamId, t.Team.Name, t.Permissions)), ct);

        var versions = pack.Versions
            .OrderByDescending(v => v.PublishedAt)
            .Select(v => new HostedResourcePackVersionInfo(
                v.Id, v.VersionString, v.Changelog, v.ReleaseChannel,
                v.BlobHash, v.FileSize, v.FileName,
                v.McVersionsCsv, v.PublishedAt))
            .ToList();

        return Ok(new HostedResourcePackDetail(
            pack.Id, pack.Slug, pack.Name, pack.Summary, pack.Description,
            pack.OwnerId, pack.Owner.UserName ?? "",
            pack.Visibility, pack.IconBlobHash, pack.McVersionsCsv,
            pack.CreatedAt, pack.UpdatedAt, perms,
            versions, collabs, teams));
    }

    [HttpPost]
    public async Task<ActionResult<HostedResourcePackSummary>> Create([FromBody] CreateResourcePackRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 128)
            return BadRequest(new { error = "Resource pack name must be 1-128 characters" });
        if (req.Summary?.Length > 512)
            return BadRequest(new { error = "Summary must be 512 characters or fewer" });
        if (req.Description?.Length > 4096)
            return BadRequest(new { error = "Description must be 4096 characters or fewer" });

        var me = this.UserId();
        var slug = await GenerateUniqueSlugAsync(req.Name, ct);
        var pack = new HostedResourcePack
        {
            Name = req.Name.Trim(),
            Slug = slug,
            Summary = req.Summary,
            Description = req.Description,
            OwnerId = me,
            Visibility = req.Visibility,
            McVersionsCsv = req.InitialMcVersionsCsv
        };
        db.HostedResourcePacks.Add(pack);
        await db.SaveChangesAsync(ct);
        await db.Entry(pack).Reference(p => p.Owner).LoadAsync(ct);
        return Ok(ToSummary(pack, PackPermissions.Full, versionCount: 0));
    }

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateResourcePackRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var pack = await db.HostedResourcePacks.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pack is null) return NotFound();
        // Owner only, as with bundles: upload access covers versions, but the name and visibility
        // belong to the owner. It also guards against 1.8.2 clients, which send the local file's
        // name, overview and visibility before every upload and would otherwise let a collaborator
        // rename someone else's pack or change who can see it.
        if (pack.OwnerId != me) return Forbid();

        if (req.Name is not null) { if (req.Name.Length > 128) return BadRequest(); pack.Name = req.Name.Trim(); }
        if (req.Summary is not null) { if (req.Summary.Length > 512) return BadRequest(); pack.Summary = req.Summary; }
        if (req.Description is not null) { if (req.Description.Length > 4096) return BadRequest(); pack.Description = req.Description; }
        var visibilityChangedTo = req.Visibility is { } wanted && wanted != pack.Visibility ? req.Visibility : null;
        if (req.Visibility is not null) pack.Visibility = req.Visibility.Value;
        // Null leaves the Minecraft versions alone; a value corrects them without a new upload.
        if (req.McVersionsCsv is not null) pack.McVersionsCsv = req.McVersionsCsv;
        pack.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        if (visibilityChangedTo is not null)
            await PackSharing.LogAsync(db, me, ActivityKind.VisibilityChanged, ActivitySubjectType.ResourcePack,
                pack.Id, pack.Name, detail: visibilityChangedTo.Value.ToString(), ct: ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var me = this.UserId();
        var pack = await db.HostedResourcePacks.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pack is null) return NotFound();
        if (pack.OwnerId != me) return Forbid();
        db.HostedResourcePacks.Remove(pack);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ---- Collaborators ----
    //
    // Sharing can be changed by the owner and anyone the owner gave ManageCollaborators, the same
    // rule as instances and bundles.

    [HttpPost("{id:guid}/collaborators")]
    public async Task<ActionResult<PackCollaboratorEntry>> AddCollaborator(
        Guid id, [FromBody] AddCollaboratorRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var (pack, perms) = await LoadAsync(id, ct);
        if (pack is null) return NotFound();
        if (!ControllerHelpers.CanManageSharing(pack.OwnerId, me, perms)) return Forbid();

        var grantError = ControllerHelpers.ValidateGrant(pack.OwnerId, me, perms, req.Permissions);
        if (grantError is not null) return BadRequest(new { error = grantError });

        var user = await ControllerHelpers.FindUserByNameAsync(db, req.Username, ct);
        if (user is null) return BadRequest(new { error = "User not found" });
        if (user.Id == pack.OwnerId) return BadRequest(new { error = "Owner is implicit" });

        var existing = await db.HostedResourcePackCollaborators
            .FirstOrDefaultAsync(c => c.ResourcePackId == id && c.UserId == user.Id, ct);
        var isNew = existing is null;
        if (existing is not null)
            existing.Permissions = req.Permissions;
        else
            db.HostedResourcePackCollaborators.Add(new HostedResourcePackCollaborator { ResourcePackId = id, UserId = user.Id, Permissions = req.Permissions });

        pack.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await PackSharing.LogAsync(db, me,
            isNew ? ActivityKind.Shared : ActivityKind.PermissionsChanged,
            ActivitySubjectType.ResourcePack, pack.Id, pack.Name,
            targetUserId: user.Id, detail: req.Permissions.ToString(), ct: ct);
        return Ok(new PackCollaboratorEntry(user.Id, user.UserName ?? "", req.Permissions));
    }

    [HttpPatch("{id:guid}/collaborators/{userId:guid}")]
    public async Task<IActionResult> UpdateCollaborator(
        Guid id, Guid userId, [FromBody] UpdateCollaboratorRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var (pack, perms) = await LoadAsync(id, ct);
        if (pack is null) return NotFound();
        if (!ControllerHelpers.CanManageSharing(pack.OwnerId, me, perms)) return Forbid();

        var grantError = ControllerHelpers.ValidateGrant(pack.OwnerId, me, perms, req.Permissions);
        if (grantError is not null) return BadRequest(new { error = grantError });

        var row = await db.HostedResourcePackCollaborators.FirstOrDefaultAsync(c => c.ResourcePackId == id && c.UserId == userId, ct);
        if (row is null) return NotFound(new { error = "They are not a collaborator on this resource pack." });
        row.Permissions = req.Permissions;
        pack.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await PackSharing.LogAsync(db, me, ActivityKind.PermissionsChanged, ActivitySubjectType.ResourcePack,
            pack.Id, pack.Name, targetUserId: userId, detail: req.Permissions.ToString(), ct: ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}/collaborators/{userId:guid}")]
    public async Task<IActionResult> RemoveCollaborator(Guid id, Guid userId, CancellationToken ct)
    {
        var me = this.UserId();
        var (pack, perms) = await LoadAsync(id, ct);
        if (pack is null) return NotFound();
        // Managers may withdraw any grant, and anyone may withdraw their own: leaving needs no
        // permission, as on instances.
        if (!ControllerHelpers.CanManageSharing(pack.OwnerId, me, perms) && me != userId) return Forbid();

        var row = await db.HostedResourcePackCollaborators.FirstOrDefaultAsync(c => c.ResourcePackId == id && c.UserId == userId, ct);
        if (row is null)
            return NotFound(new
            {
                error = userId == me
                    ? "You are not a collaborator on this resource pack, so there is nothing to leave."
                    : "They are not a collaborator on this resource pack."
            });
        db.HostedResourcePackCollaborators.Remove(row);
        pack.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await PackSharing.LogAsync(db, me, ActivityKind.Unshared, ActivitySubjectType.ResourcePack,
            pack.Id, pack.Name, targetUserId: userId, ct: ct);
        return NoContent();
    }

    // ---- Resource pack teams ----

    [HttpPost("{id:guid}/teams")]
    public async Task<ActionResult<PackTeamEntry>> AddTeam(
        Guid id, [FromBody] AddPackTeamRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var (pack, perms) = await LoadAsync(id, ct);
        if (pack is null) return NotFound();
        if (!ControllerHelpers.CanManageSharing(pack.OwnerId, me, perms)) return Forbid();

        var grantError = ControllerHelpers.ValidateGrant(pack.OwnerId, me, perms, req.Permissions);
        if (grantError is not null) return BadRequest(new { error = grantError });

        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == req.TeamId, ct);
        if (team is null) return BadRequest(new { error = "Team not found" });

        // A manager who is not the owner may only share the pack with a team they are in. Otherwise
        // ManageCollaborators would let a collaborator publish someone else's private pack to any
        // team whose id they can guess. Same rule as bundles.
        if (pack.OwnerId != me
            && !await db.TeamMembers.AnyAsync(tm => tm.TeamId == req.TeamId && tm.UserId == me, ct))
            return BadRequest(new { error = "You can only share this with a team you belong to." });

        var existing = await db.HostedResourcePackTeams.FirstOrDefaultAsync(pt => pt.ResourcePackId == id && pt.TeamId == req.TeamId, ct);
        var isNew = existing is null;
        if (existing is not null)
            existing.Permissions = req.Permissions;
        else
            db.HostedResourcePackTeams.Add(new HostedResourcePackTeam { ResourcePackId = id, TeamId = req.TeamId, Permissions = req.Permissions });

        pack.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await PackSharing.LogAsync(db, me,
            isNew ? ActivityKind.Shared : ActivityKind.PermissionsChanged,
            ActivitySubjectType.ResourcePack, pack.Id, pack.Name,
            targetTeamId: team.Id, detail: req.Permissions.ToString(), ct: ct);

        var entries = await PackSharing.TeamEntriesAsync(db, [(team.Id, team.Name, req.Permissions)], ct);
        return Ok(entries[0]);
    }

    [HttpPatch("{id:guid}/teams/{teamId:guid}")]
    public async Task<IActionResult> UpdateTeam(
        Guid id, Guid teamId, [FromBody] UpdatePackTeamRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var (pack, perms) = await LoadAsync(id, ct);
        if (pack is null) return NotFound();
        if (!ControllerHelpers.CanManageSharing(pack.OwnerId, me, perms)) return Forbid();

        var grantError = ControllerHelpers.ValidateGrant(pack.OwnerId, me, perms, req.Permissions);
        if (grantError is not null) return BadRequest(new { error = grantError });

        var row = await db.HostedResourcePackTeams.FirstOrDefaultAsync(pt => pt.ResourcePackId == id && pt.TeamId == teamId, ct);
        if (row is null) return NotFound(new { error = "That team is not on this resource pack." });
        row.Permissions = req.Permissions;
        pack.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await PackSharing.LogAsync(db, me, ActivityKind.PermissionsChanged, ActivitySubjectType.ResourcePack,
            pack.Id, pack.Name, targetTeamId: teamId, detail: req.Permissions.ToString(), ct: ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}/teams/{teamId:guid}")]
    public async Task<IActionResult> RemoveTeam(Guid id, Guid teamId, CancellationToken ct)
    {
        var me = this.UserId();
        var (pack, perms) = await LoadAsync(id, ct);
        if (pack is null) return NotFound();
        if (!ControllerHelpers.CanManageSharing(pack.OwnerId, me, perms)) return Forbid();

        var row = await db.HostedResourcePackTeams.FirstOrDefaultAsync(pt => pt.ResourcePackId == id && pt.TeamId == teamId, ct);
        if (row is null) return NotFound(new { error = "That team is not on this resource pack." });
        db.HostedResourcePackTeams.Remove(row);
        pack.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await PackSharing.LogAsync(db, me, ActivityKind.Unshared, ActivitySubjectType.ResourcePack,
            pack.Id, pack.Name, targetTeamId: teamId, ct: ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/versions")]
    [RequestSizeLimit(MaxResourcePackBytes + (1 << 16))]
    public async Task<ActionResult<HostedResourcePackVersionInfo>> UploadVersion(
        Guid id,
        [FromForm] IFormFile file,
        [FromForm] string metadata,
        CancellationToken ct)
    {
        var me = this.UserId();
        var (pack, perms) = await LoadAsync(id, ct);
        if (pack is null) return NotFound();
        // UploadShared is enough (the dialog calls it "can upload changes"), as with bundles.
        if (!perms.HasFlag(PackPermissions.UploadShared)) return Forbid();
        if (file is null || file.Length == 0) return BadRequest(new { error = "File required" });
        if (file.Length > MaxResourcePackBytes)
            return BadRequest(new { error = $"File too large (max {MaxResourcePackBytes / (1024 * 1024)} MB)" });

        CreateResourcePackVersionRequest? req;
        try { req = JsonSerializer.Deserialize<CreateResourcePackVersionRequest>(metadata, JsonOpts); }
        catch { return BadRequest(new { error = "Invalid metadata JSON" }); }
        if (req is null || string.IsNullOrWhiteSpace(req.VersionString))
            return BadRequest(new { error = "VersionString required" });

        // The disk floor and the pack owner's quota, checked before the zip reaches the blob store.
        // A collaborator's upload counts against the owner's quota, as a pack sync does.
        if (await guard.RefuseAsync(pack.OwnerId, me, file, ct) is { } refusal) return refusal;

        StoredBlob stored;
        await using (var s = file.OpenReadStream())
            stored = await blobs.PutAsync(s, ct);
        if (await guard.ChargeAsync(pack.OwnerId, me, stored, ct) is { } overQuota) return overQuota;
        var hash = stored.Hash;

        var version = new HostedResourcePackVersion
        {
            ResourcePackId = pack.Id,
            VersionString = req.VersionString.Trim(),
            Changelog = req.Changelog,
            ReleaseChannel = string.IsNullOrWhiteSpace(req.ReleaseChannel) ? "release" : req.ReleaseChannel.ToLowerInvariant(),
            BlobHash = hash,
            FileSize = file.Length,
            FileName = string.IsNullOrWhiteSpace(req.FileName) ? file.FileName : req.FileName,
            McVersionsCsv = req.McVersionsCsv
        };
        db.HostedResourcePackVersions.Add(version);

        if (!string.IsNullOrWhiteSpace(req.McVersionsCsv)) pack.McVersionsCsv = req.McVersionsCsv;
        pack.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        guard.Settle(pack.OwnerId, hash);

        await PackSharing.LogAsync(db, me, ActivityKind.VersionPublished, ActivitySubjectType.ResourcePack,
            pack.Id, pack.Name, detail: version.VersionString, ct: ct);

        return Ok(new HostedResourcePackVersionInfo(
            version.Id, version.VersionString, version.Changelog, version.ReleaseChannel,
            version.BlobHash, version.FileSize, version.FileName,
            version.McVersionsCsv, version.PublishedAt));
    }

    /// <summary>Removes one uploaded version of a resource pack.</summary>
    /// <remarks>Deleting the last version is allowed and leaves the pack in place: a pack with no
    /// file is a valid state (it is what <see cref="Create"/> produces), and the owner has to be
    /// able to clear a bad upload. Owner only, as with bundles: adding a version is collaboration,
    /// removing one deletes someone else's release history.</remarks>
    [HttpDelete("{id:guid}/versions/{versionId:guid}")]
    public async Task<IActionResult> DeleteVersion(Guid id, Guid versionId, CancellationToken ct)
    {
        var me = this.UserId();
        var pack = await db.HostedResourcePacks.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pack is null) return NotFound();
        if (pack.OwnerId != me) return Forbid();

        var version = await db.HostedResourcePackVersions
            .FirstOrDefaultAsync(v => v.Id == versionId && v.ResourcePackId == id, ct);
        if (version is null) return NotFound();

        var hash = version.BlobHash;
        db.HostedResourcePackVersions.Remove(version);
        pack.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await ControllerHelpers.DeleteBlobIfUnreferencedAsync(db, blobs, hash, ct);
        return NoContent();
    }

    // ---- Icon ----

    /// <summary>Sets the resource pack's icon.</summary>
    /// <remarks>Same shape as the mod route, so the client can use one upload helper.</remarks>
    [HttpPost("{id:guid}/icon")]
    [RequestSizeLimit(ControllerHelpers.MaxIconBytes + (1 << 16))]
    public async Task<IActionResult> UploadIcon(Guid id, [FromForm] IFormFile file, CancellationToken ct)
    {
        var me = this.UserId();
        var pack = await db.HostedResourcePacks.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pack is null) return NotFound();
        if (pack.OwnerId != me) return Forbid();

        var (hash, failure) = await ControllerHelpers.TryStoreIconAsync(blobs, guard, pack.OwnerId, me, file, ct);
        if (hash is null) return failure!;

        var previous = pack.IconBlobHash;
        pack.IconBlobHash = hash;
        pack.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        guard.Settle(pack.OwnerId, hash);

        if (previous is not null && previous != hash)
            await ControllerHelpers.DeleteBlobIfUnreferencedAsync(db, blobs, previous, ct);
        return Ok(new { iconBlobHash = hash });
    }

    /// <summary>Serves the resource pack's icon, or 404 when it has none.</summary>
    /// <remarks>Anonymous under the same visibility rule as the pack itself, so an &lt;Image&gt; with
    /// no Authorization header can show a public pack's art.</remarks>
    [AllowAnonymous]
    [HttpGet("{id:guid}/icon")]
    public async Task<IActionResult> GetIcon(Guid id, CancellationToken ct)
    {
        var pack = await db.HostedResourcePacks.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pack is null) return NotFound();

        var perms = await resolver.GetAsync(pack, this.UserIdOrNull(), ct);
        if (!perms.HasFlag(PackPermissions.View)) return NotFound();

        if (string.IsNullOrEmpty(pack.IconBlobHash) || !blobs.Exists(pack.IconBlobHash)) return NotFound();
        return File(blobs.OpenRead(pack.IconBlobHash), ControllerHelpers.IconResponseContentType);
    }

    /// <summary>Clears the resource pack's icon.</summary>
    [HttpDelete("{id:guid}/icon")]
    public async Task<IActionResult> DeleteIcon(Guid id, CancellationToken ct)
    {
        var me = this.UserId();
        var pack = await db.HostedResourcePacks.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pack is null) return NotFound();
        if (pack.OwnerId != me) return Forbid();

        var previous = pack.IconBlobHash;
        if (previous is null) return NoContent();

        pack.IconBlobHash = null;
        pack.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await ControllerHelpers.DeleteBlobIfUnreferencedAsync(db, blobs, previous, ct);
        return NoContent();
    }

    [HttpGet("{id:guid}/files/{versionId:guid}")]
    public async Task<IActionResult> Download(Guid id, Guid versionId, CancellationToken ct)
    {
        var me = this.UserId();
        var pack = await db.HostedResourcePacks.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pack is null) return NotFound();
        var perms = await resolver.GetAsync(pack, me, ct);
        if (!perms.HasFlag(PackPermissions.Download)) return Forbid();

        var version = await db.HostedResourcePackVersions.FirstOrDefaultAsync(v => v.Id == versionId && v.ResourcePackId == id, ct);
        if (version is null) return NotFound();
        if (!blobs.Exists(version.BlobHash)) return NotFound();

        return File(blobs.OpenRead(version.BlobHash), "application/zip", version.FileName);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Loads a hosted resource pack together with what the caller may do to it.</summary>
    private async Task<(HostedResourcePack? Pack, PackPermissions Perms)> LoadAsync(Guid id, CancellationToken ct)
    {
        var pack = await db.HostedResourcePacks.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pack is null) return (null, PackPermissions.None);
        return (pack, await resolver.GetAsync(pack, this.UserId(), ct));
    }

    private async Task<string> GenerateUniqueSlugAsync(string name, CancellationToken ct)
    {
        var basic = new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        basic = string.Join('-', basic.Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (basic.Length == 0) basic = "resourcepack";
        if (basic.Length > 80) basic = basic[..80];

        var candidate = basic;
        var n = 1;
        while (await db.HostedResourcePacks.AnyAsync(p => p.Slug == candidate, ct))
            candidate = $"{basic}-{++n}";
        return candidate;
    }

    private static HostedResourcePackSummary ToSummary(HostedResourcePack p, PackPermissions perms, int versionCount) => new(
        p.Id, p.Slug, p.Name, p.Summary, p.OwnerId, p.Owner.UserName ?? "",
        p.Visibility, p.IconBlobHash, p.McVersionsCsv,
        0, // DownloadCount placeholder until there is a real counter
        p.CreatedAt, p.UpdatedAt, perms, versionCount);
}
