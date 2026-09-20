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
public class SharedWorldsController(AppDbContext db, SharedWorldPermissionResolver resolver, BlobStore blobs) : ControllerBase
{
    // Worlds can be much larger than mods — bump to 256 MB. Configurable later.
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

        var canSeeMembers = w.OwnerId == me || perms.HasFlag(PackPermissions.ManageCollaborators);
        var collabs = canSeeMembers
            ? w.Collaborators.Select(c => new PackCollaboratorEntry(c.UserId, c.User.UserName ?? "", c.Permissions)).ToList()
            : new List<PackCollaboratorEntry>();
        var teams = canSeeMembers
            ? w.Teams.Select(t => new PackTeamEntry(t.TeamId, t.Team.Name, t.Permissions)).ToList()
            : new List<PackTeamEntry>();

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
        if (w.OwnerId != me) return Forbid();

        if (req.Name is not null) w.Name = req.Name.Trim();
        if (req.Summary is not null) w.Summary = req.Summary;
        if (req.Description is not null) w.Description = req.Description;
        if (req.Visibility is not null) w.Visibility = req.Visibility.Value;
        // Only a version upload could write this before, so a world created against the wrong
        // Minecraft version had no way back. Null still means "leave it alone".
        if (req.McVersion is not null) w.McVersion = req.McVersion;
        w.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
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

    [HttpPost("{id:guid}/collaborators")]
    public async Task<ActionResult<PackCollaboratorEntry>> AddCollaborator(
        Guid id, [FromBody] AddCollaboratorRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var world = await db.SharedWorlds.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (world is null) return NotFound();
        if (world.OwnerId != me) return Forbid();

        var user = await db.Users.FirstOrDefaultAsync(u => u.UserName == req.Username, ct);
        if (user is null) return BadRequest(new { error = "User not found" });
        if (user.Id == world.OwnerId) return BadRequest(new { error = "Owner is implicit" });

        var existing = await db.SharedWorldCollaborators
            .FirstOrDefaultAsync(c => c.WorldId == id && c.UserId == user.Id, ct);
        if (existing is not null)
            existing.Permissions = req.Permissions;
        else
            db.SharedWorldCollaborators.Add(new SharedWorldCollaborator { WorldId = id, UserId = user.Id, Permissions = req.Permissions });

        world.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok(new PackCollaboratorEntry(user.Id, user.UserName ?? "", req.Permissions));
    }

    [HttpPatch("{id:guid}/collaborators/{userId:guid}")]
    public async Task<IActionResult> UpdateCollaborator(
        Guid id, Guid userId, [FromBody] UpdateCollaboratorRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var world = await db.SharedWorlds.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (world is null) return NotFound();
        if (world.OwnerId != me) return Forbid();

        var row = await db.SharedWorldCollaborators.FirstOrDefaultAsync(c => c.WorldId == id && c.UserId == userId, ct);
        if (row is null) return NotFound();
        row.Permissions = req.Permissions;
        world.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}/collaborators/{userId:guid}")]
    public async Task<IActionResult> RemoveCollaborator(Guid id, Guid userId, CancellationToken ct)
    {
        var me = this.UserId();
        var world = await db.SharedWorlds.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (world is null) return NotFound();
        if (world.OwnerId != me) return Forbid();

        var row = await db.SharedWorldCollaborators.FirstOrDefaultAsync(c => c.WorldId == id && c.UserId == userId, ct);
        if (row is null) return NotFound();
        db.SharedWorldCollaborators.Remove(row);
        world.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ---- World teams ----

    [HttpPost("{id:guid}/teams")]
    public async Task<ActionResult<PackTeamEntry>> AddTeam(
        Guid id, [FromBody] AddPackTeamRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var world = await db.SharedWorlds.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (world is null) return NotFound();
        if (world.OwnerId != me) return Forbid();

        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == req.TeamId, ct);
        if (team is null) return BadRequest(new { error = "Team not found" });

        var existing = await db.SharedWorldTeams.FirstOrDefaultAsync(wt => wt.WorldId == id && wt.TeamId == req.TeamId, ct);
        if (existing is not null)
            existing.Permissions = req.Permissions;
        else
            db.SharedWorldTeams.Add(new SharedWorldTeam { WorldId = id, TeamId = req.TeamId, Permissions = req.Permissions });

        world.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok(new PackTeamEntry(team.Id, team.Name, req.Permissions));
    }

    [HttpPatch("{id:guid}/teams/{teamId:guid}")]
    public async Task<IActionResult> UpdateTeam(
        Guid id, Guid teamId, [FromBody] UpdatePackTeamRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var world = await db.SharedWorlds.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (world is null) return NotFound();
        if (world.OwnerId != me) return Forbid();
        var row = await db.SharedWorldTeams.FirstOrDefaultAsync(wt => wt.WorldId == id && wt.TeamId == teamId, ct);
        if (row is null) return NotFound();
        row.Permissions = req.Permissions;
        world.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}/teams/{teamId:guid}")]
    public async Task<IActionResult> RemoveTeam(Guid id, Guid teamId, CancellationToken ct)
    {
        var me = this.UserId();
        var world = await db.SharedWorlds.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (world is null) return NotFound();
        if (world.OwnerId != me) return Forbid();
        var row = await db.SharedWorldTeams.FirstOrDefaultAsync(wt => wt.WorldId == id && wt.TeamId == teamId, ct);
        if (row is null) return NotFound();
        db.SharedWorldTeams.Remove(row);
        world.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
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
        var w = await db.SharedWorlds.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (w is null) return NotFound();
        if (w.OwnerId != me) return Forbid();
        if (file is null || file.Length == 0) return BadRequest(new { error = "File required" });
        if (file.Length > MaxWorldBytes) return BadRequest(new { error = $"File too large (max {MaxWorldBytes / (1024 * 1024)} MB)" });

        CreateWorldVersionRequest? req;
        try { req = JsonSerializer.Deserialize<CreateWorldVersionRequest>(metadata, JsonOpts); }
        catch { return BadRequest(new { error = "Invalid metadata JSON" }); }
        if (req is null || string.IsNullOrWhiteSpace(req.VersionString))
            return BadRequest(new { error = "VersionString required" });

        string hash;
        await using (var s = file.OpenReadStream())
            hash = await blobs.StoreAsync(s, ct);

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

        return Ok(new SharedWorldVersionInfo(
            version.Id, version.VersionString, version.Changelog,
            version.BlobHash, version.FileSize, version.FileName,
            version.McVersion, version.PublishedAt));
    }

    /// <summary>Removes one uploaded version of a shared world.</summary>
    /// <remarks>
    /// Deleting the last version is allowed and leaves the world itself in place — a world page with
    /// no save behind it is a valid state (it is exactly what <see cref="Create"/> produces), and
    /// making the final delete a special case would mean the owner could never clear a bad upload.
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
