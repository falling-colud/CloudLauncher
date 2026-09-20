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
    BlobStore blobs) : ControllerBase
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

        // One grouped count for the whole page rather than a COUNT per row. Packs with no uploaded
        // version are simply absent from the dictionary and fall through to 0 below.
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

        var canSeeMembers = pack.OwnerId == me || perms.HasFlag(PackPermissions.ManageCollaborators);
        var collabs = canSeeMembers
            ? pack.Collaborators.Select(c => new PackCollaboratorEntry(c.UserId, c.User.UserName ?? "", c.Permissions)).ToList()
            : new List<PackCollaboratorEntry>();
        var teams = canSeeMembers
            ? pack.Teams.Select(t => new PackTeamEntry(t.TeamId, t.Team.Name, t.Permissions)).ToList()
            : new List<PackTeamEntry>();

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
        if (pack.OwnerId != me) return Forbid();

        if (req.Name is not null) { if (req.Name.Length > 128) return BadRequest(); pack.Name = req.Name.Trim(); }
        if (req.Summary is not null) { if (req.Summary.Length > 512) return BadRequest(); pack.Summary = req.Summary; }
        if (req.Description is not null) { if (req.Description.Length > 4096) return BadRequest(); pack.Description = req.Description; }
        if (req.Visibility is not null) pack.Visibility = req.Visibility.Value;
        // Only a version upload could write this before, so a pack created against the wrong
        // Minecraft version had no way back. Null still means "leave it alone".
        if (req.McVersionsCsv is not null) pack.McVersionsCsv = req.McVersionsCsv;
        pack.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
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

    [HttpPost("{id:guid}/collaborators")]
    public async Task<ActionResult<PackCollaboratorEntry>> AddCollaborator(
        Guid id, [FromBody] AddCollaboratorRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var pack = await db.HostedResourcePacks.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pack is null) return NotFound();
        if (pack.OwnerId != me) return Forbid();

        var user = await db.Users.FirstOrDefaultAsync(u => u.UserName == req.Username, ct);
        if (user is null) return BadRequest(new { error = "User not found" });
        if (user.Id == pack.OwnerId) return BadRequest(new { error = "Owner is implicit" });

        var existing = await db.HostedResourcePackCollaborators
            .FirstOrDefaultAsync(c => c.ResourcePackId == id && c.UserId == user.Id, ct);
        if (existing is not null)
            existing.Permissions = req.Permissions;
        else
            db.HostedResourcePackCollaborators.Add(new HostedResourcePackCollaborator { ResourcePackId = id, UserId = user.Id, Permissions = req.Permissions });

        pack.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok(new PackCollaboratorEntry(user.Id, user.UserName ?? "", req.Permissions));
    }

    [HttpPatch("{id:guid}/collaborators/{userId:guid}")]
    public async Task<IActionResult> UpdateCollaborator(
        Guid id, Guid userId, [FromBody] UpdateCollaboratorRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var pack = await db.HostedResourcePacks.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pack is null) return NotFound();
        if (pack.OwnerId != me) return Forbid();

        var row = await db.HostedResourcePackCollaborators.FirstOrDefaultAsync(c => c.ResourcePackId == id && c.UserId == userId, ct);
        if (row is null) return NotFound();
        row.Permissions = req.Permissions;
        pack.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}/collaborators/{userId:guid}")]
    public async Task<IActionResult> RemoveCollaborator(Guid id, Guid userId, CancellationToken ct)
    {
        var me = this.UserId();
        var pack = await db.HostedResourcePacks.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pack is null) return NotFound();
        if (pack.OwnerId != me) return Forbid();

        var row = await db.HostedResourcePackCollaborators.FirstOrDefaultAsync(c => c.ResourcePackId == id && c.UserId == userId, ct);
        if (row is null) return NotFound();
        db.HostedResourcePackCollaborators.Remove(row);
        pack.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ---- Resource pack teams ----

    [HttpPost("{id:guid}/teams")]
    public async Task<ActionResult<PackTeamEntry>> AddTeam(
        Guid id, [FromBody] AddPackTeamRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var pack = await db.HostedResourcePacks.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pack is null) return NotFound();
        if (pack.OwnerId != me) return Forbid();

        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == req.TeamId, ct);
        if (team is null) return BadRequest(new { error = "Team not found" });

        var existing = await db.HostedResourcePackTeams.FirstOrDefaultAsync(pt => pt.ResourcePackId == id && pt.TeamId == req.TeamId, ct);
        if (existing is not null)
            existing.Permissions = req.Permissions;
        else
            db.HostedResourcePackTeams.Add(new HostedResourcePackTeam { ResourcePackId = id, TeamId = req.TeamId, Permissions = req.Permissions });

        pack.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok(new PackTeamEntry(team.Id, team.Name, req.Permissions));
    }

    [HttpPatch("{id:guid}/teams/{teamId:guid}")]
    public async Task<IActionResult> UpdateTeam(
        Guid id, Guid teamId, [FromBody] UpdatePackTeamRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var pack = await db.HostedResourcePacks.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pack is null) return NotFound();
        if (pack.OwnerId != me) return Forbid();
        var row = await db.HostedResourcePackTeams.FirstOrDefaultAsync(pt => pt.ResourcePackId == id && pt.TeamId == teamId, ct);
        if (row is null) return NotFound();
        row.Permissions = req.Permissions;
        pack.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}/teams/{teamId:guid}")]
    public async Task<IActionResult> RemoveTeam(Guid id, Guid teamId, CancellationToken ct)
    {
        var me = this.UserId();
        var pack = await db.HostedResourcePacks.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pack is null) return NotFound();
        if (pack.OwnerId != me) return Forbid();
        var row = await db.HostedResourcePackTeams.FirstOrDefaultAsync(pt => pt.ResourcePackId == id && pt.TeamId == teamId, ct);
        if (row is null) return NotFound();
        db.HostedResourcePackTeams.Remove(row);
        pack.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
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
        var pack = await db.HostedResourcePacks.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pack is null) return NotFound();
        if (pack.OwnerId != me) return Forbid();
        if (file is null || file.Length == 0) return BadRequest(new { error = "File required" });
        if (file.Length > MaxResourcePackBytes)
            return BadRequest(new { error = $"File too large (max {MaxResourcePackBytes / (1024 * 1024)} MB)" });

        CreateResourcePackVersionRequest? req;
        try { req = JsonSerializer.Deserialize<CreateResourcePackVersionRequest>(metadata, JsonOpts); }
        catch { return BadRequest(new { error = "Invalid metadata JSON" }); }
        if (req is null || string.IsNullOrWhiteSpace(req.VersionString))
            return BadRequest(new { error = "VersionString required" });

        string hash;
        await using (var s = file.OpenReadStream())
            hash = await blobs.StoreAsync(s, ct);

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

        return Ok(new HostedResourcePackVersionInfo(
            version.Id, version.VersionString, version.Changelog, version.ReleaseChannel,
            version.BlobHash, version.FileSize, version.FileName,
            version.McVersionsCsv, version.PublishedAt));
    }

    /// <summary>Removes one uploaded version of a resource pack.</summary>
    /// <remarks>
    /// Deleting the last version is allowed and leaves the pack itself in place — a pack page with no
    /// file behind it is a valid state (it is exactly what <see cref="Create"/> produces), and making
    /// the final delete a special case would mean the owner could never clear a bad upload.
    /// </remarks>
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
        0, // DownloadCount placeholder — wire to a real counter later
        p.CreatedAt, p.UpdatedAt, perms, versionCount);
}
