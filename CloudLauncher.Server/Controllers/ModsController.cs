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
[Route("mods")]
public class ModsController(AppDbContext db, ModPermissionResolver resolver, BlobStore blobs) : ControllerBase
{
    private const long MaxModBytes = 64L * 1024 * 1024; // 64 MB per jar — generous; bump if needed

    [HttpGet("browse")]
    public async Task<ActionResult<ModBrowsePage>> Browse(
        [FromQuery] ModBrowseSource source = ModBrowseSource.Public,
        [FromQuery] Guid? teamId = null,
        [FromQuery] string? q = null,
        [FromQuery] string? mcVersion = null,
        [FromQuery] string? loader = null,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 25,
        CancellationToken ct = default)
    {
        if (limit <= 0 || limit > 100) limit = 25;
        if (offset < 0) offset = 0;

        var me = this.UserId();
        var query = db.Mods.Include(m => m.Owner).AsQueryable();

        query = source switch
        {
            ModBrowseSource.Public =>
                query.Where(m => m.Visibility == PackVisibility.Public),
            ModBrowseSource.Personal =>
                query.Where(m => m.OwnerId == me),
            ModBrowseSource.Shared =>
                query.Where(m => m.OwnerId != me
                              && db.ModCollaborators.Any(c => c.ModId == m.Id && c.UserId == me)),
            ModBrowseSource.Team =>
                teamId.HasValue
                    ? query.Where(m => db.ModTeams.Any(mt => mt.ModId == m.Id && mt.TeamId == teamId.Value)
                                    && db.TeamMembers.Any(tm => tm.TeamId == teamId.Value && tm.UserId == me))
                    : query.Where(m => db.ModTeams.Any(mt => mt.ModId == m.Id
                                                       && db.TeamMembers.Any(tm => tm.TeamId == mt.TeamId && tm.UserId == me))),
            _ => query.Where(_ => false)
        };

        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim().ToLower();
            query = query.Where(m =>
                m.Name.ToLower().Contains(needle)
                || (m.Summary != null && m.Summary.ToLower().Contains(needle))
                || (m.Description != null && m.Description.ToLower().Contains(needle)));
        }
        if (!string.IsNullOrWhiteSpace(mcVersion))
            query = query.Where(m => m.McVersionsCsv != null && m.McVersionsCsv.Contains(mcVersion));
        if (!string.IsNullOrWhiteSpace(loader))
            query = query.Where(m => m.LoadersCsv != null && m.LoadersCsv.Contains(loader.ToLower()));

        var total = await query.CountAsync(ct);
        var page = await query
            .OrderByDescending(m => m.UpdatedAt)
            .Skip(offset).Take(limit)
            .ToListAsync(ct);

        var items = new List<HostedModSummary>(page.Count);
        foreach (var m in page)
        {
            var perms = await resolver.GetAsync(m, me, ct);
            items.Add(ToSummary(m, perms));
        }
        return Ok(new ModBrowsePage(items, offset, limit, total));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<HostedModDetail>> Get(Guid id, CancellationToken ct)
    {
        var me = this.UserId();
        var mod = await db.Mods
            .Include(m => m.Owner)
            .Include(m => m.Versions)
            .Include(m => m.Collaborators).ThenInclude(c => c.User)
            .Include(m => m.Teams).ThenInclude(t => t.Team)
            .FirstOrDefaultAsync(m => m.Id == id, ct);
        if (mod is null) return NotFound();

        var perms = await resolver.GetAsync(mod, me, ct);
        if (!perms.HasFlag(PackPermissions.View))
            return Forbid();

        var canSeeMembers = mod.OwnerId == me || perms.HasFlag(PackPermissions.ManageCollaborators);
        var collabs = canSeeMembers
            ? mod.Collaborators.Select(c => new PackCollaboratorEntry(c.UserId, c.User.UserName ?? "", c.Permissions)).ToList()
            : new List<PackCollaboratorEntry>();
        var teams = canSeeMembers
            ? mod.Teams.Select(t => new PackTeamEntry(t.TeamId, t.Team.Name, t.Permissions)).ToList()
            : new List<PackTeamEntry>();

        var versions = mod.Versions
            .OrderByDescending(v => v.PublishedAt)
            .Select(v => new HostedModVersionInfo(
                v.Id, v.VersionString, v.Changelog, v.ReleaseChannel,
                v.BlobHash, v.FileSize, v.FileName,
                v.McVersionsCsv, v.LoadersCsv, v.PublishedAt))
            .ToList();

        return Ok(new HostedModDetail(
            mod.Id, mod.Slug, mod.Name, mod.Summary, mod.Description,
            mod.OwnerId, mod.Owner.UserName ?? "",
            mod.Visibility, mod.IconBlobHash, mod.McVersionsCsv, mod.LoadersCsv,
            mod.CreatedAt, mod.UpdatedAt, perms,
            versions, collabs, teams));
    }

    [HttpPost]
    public async Task<ActionResult<HostedModSummary>> Create([FromBody] CreateModRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 128)
            return BadRequest(new { error = "Mod name must be 1-128 characters" });
        if (req.Summary?.Length > 512)
            return BadRequest(new { error = "Summary must be 512 characters or fewer" });
        if (req.Description?.Length > 4096)
            return BadRequest(new { error = "Description must be 4096 characters or fewer" });

        var me = this.UserId();
        var slug = await GenerateUniqueSlugAsync(req.Name, ct);
        var mod = new Mod
        {
            Name = req.Name.Trim(),
            Slug = slug,
            Summary = req.Summary,
            Description = req.Description,
            OwnerId = me,
            Visibility = req.Visibility,
            McVersionsCsv = req.InitialMcVersionsCsv,
            LoadersCsv = req.InitialLoadersCsv?.ToLowerInvariant()
        };
        db.Mods.Add(mod);
        await db.SaveChangesAsync(ct);
        await db.Entry(mod).Reference(m => m.Owner).LoadAsync(ct);
        return Ok(ToSummary(mod, PackPermissions.Full));
    }

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateModRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var mod = await db.Mods.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (mod is null) return NotFound();
        if (mod.OwnerId != me) return Forbid();

        if (req.Name is not null) { if (req.Name.Length > 128) return BadRequest(); mod.Name = req.Name.Trim(); }
        if (req.Summary is not null) { if (req.Summary.Length > 512) return BadRequest(); mod.Summary = req.Summary; }
        if (req.Description is not null) { if (req.Description.Length > 4096) return BadRequest(); mod.Description = req.Description; }
        if (req.Visibility is not null) mod.Visibility = req.Visibility.Value;
        mod.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var me = this.UserId();
        var mod = await db.Mods.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (mod is null) return NotFound();
        if (mod.OwnerId != me) return Forbid();
        db.Mods.Remove(mod);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ---- Collaborators ----

    [HttpPost("{id:guid}/collaborators")]
    public async Task<ActionResult<PackCollaboratorEntry>> AddCollaborator(
        Guid id, [FromBody] AddCollaboratorRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var mod = await db.Mods.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (mod is null) return NotFound();
        if (mod.OwnerId != me) return Forbid();

        var user = await db.Users.FirstOrDefaultAsync(u => u.UserName == req.Username, ct);
        if (user is null) return BadRequest(new { error = "User not found" });
        if (user.Id == mod.OwnerId) return BadRequest(new { error = "Owner is implicit" });

        var existing = await db.ModCollaborators
            .FirstOrDefaultAsync(c => c.ModId == id && c.UserId == user.Id, ct);
        if (existing is not null)
            existing.Permissions = req.Permissions;
        else
            db.ModCollaborators.Add(new ModCollaborator { ModId = id, UserId = user.Id, Permissions = req.Permissions });

        mod.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok(new PackCollaboratorEntry(user.Id, user.UserName ?? "", req.Permissions));
    }

    [HttpPatch("{id:guid}/collaborators/{userId:guid}")]
    public async Task<IActionResult> UpdateCollaborator(
        Guid id, Guid userId, [FromBody] UpdateCollaboratorRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var mod = await db.Mods.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (mod is null) return NotFound();
        if (mod.OwnerId != me) return Forbid();

        var row = await db.ModCollaborators.FirstOrDefaultAsync(c => c.ModId == id && c.UserId == userId, ct);
        if (row is null) return NotFound();
        row.Permissions = req.Permissions;
        mod.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}/collaborators/{userId:guid}")]
    public async Task<IActionResult> RemoveCollaborator(Guid id, Guid userId, CancellationToken ct)
    {
        var me = this.UserId();
        var mod = await db.Mods.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (mod is null) return NotFound();
        if (mod.OwnerId != me) return Forbid();

        var row = await db.ModCollaborators.FirstOrDefaultAsync(c => c.ModId == id && c.UserId == userId, ct);
        if (row is null) return NotFound();
        db.ModCollaborators.Remove(row);
        mod.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ---- Mod teams ----

    [HttpPost("{id:guid}/teams")]
    public async Task<ActionResult<PackTeamEntry>> AddTeam(
        Guid id, [FromBody] AddPackTeamRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var mod = await db.Mods.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (mod is null) return NotFound();
        if (mod.OwnerId != me) return Forbid();

        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == req.TeamId, ct);
        if (team is null) return BadRequest(new { error = "Team not found" });

        var existing = await db.ModTeams.FirstOrDefaultAsync(mt => mt.ModId == id && mt.TeamId == req.TeamId, ct);
        if (existing is not null)
            existing.Permissions = req.Permissions;
        else
            db.ModTeams.Add(new ModTeam { ModId = id, TeamId = req.TeamId, Permissions = req.Permissions });

        mod.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok(new PackTeamEntry(team.Id, team.Name, req.Permissions));
    }

    [HttpPatch("{id:guid}/teams/{teamId:guid}")]
    public async Task<IActionResult> UpdateTeam(
        Guid id, Guid teamId, [FromBody] UpdatePackTeamRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var mod = await db.Mods.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (mod is null) return NotFound();
        if (mod.OwnerId != me) return Forbid();
        var row = await db.ModTeams.FirstOrDefaultAsync(mt => mt.ModId == id && mt.TeamId == teamId, ct);
        if (row is null) return NotFound();
        row.Permissions = req.Permissions;
        mod.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}/teams/{teamId:guid}")]
    public async Task<IActionResult> RemoveTeam(Guid id, Guid teamId, CancellationToken ct)
    {
        var me = this.UserId();
        var mod = await db.Mods.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (mod is null) return NotFound();
        if (mod.OwnerId != me) return Forbid();
        var row = await db.ModTeams.FirstOrDefaultAsync(mt => mt.ModId == id && mt.TeamId == teamId, ct);
        if (row is null) return NotFound();
        db.ModTeams.Remove(row);
        mod.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/versions")]
    [RequestSizeLimit(MaxModBytes + (1 << 16))]
    public async Task<ActionResult<HostedModVersionInfo>> UploadVersion(
        Guid id,
        [FromForm] IFormFile file,
        [FromForm] string metadata,
        CancellationToken ct)
    {
        var me = this.UserId();
        var mod = await db.Mods.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (mod is null) return NotFound();
        if (mod.OwnerId != me) return Forbid();
        if (file is null || file.Length == 0) return BadRequest(new { error = "File required" });
        if (file.Length > MaxModBytes) return BadRequest(new { error = $"File too large (max {MaxModBytes / (1024 * 1024)} MB)" });

        CreateModVersionRequest? req;
        try { req = JsonSerializer.Deserialize<CreateModVersionRequest>(metadata, JsonOpts); }
        catch { return BadRequest(new { error = "Invalid metadata JSON" }); }
        if (req is null || string.IsNullOrWhiteSpace(req.VersionString))
            return BadRequest(new { error = "VersionString required" });

        string hash;
        await using (var s = file.OpenReadStream())
            hash = await blobs.StoreAsync(s, ct);

        var version = new ModVersion
        {
            ModId = mod.Id,
            VersionString = req.VersionString.Trim(),
            Changelog = req.Changelog,
            ReleaseChannel = string.IsNullOrWhiteSpace(req.ReleaseChannel) ? "release" : req.ReleaseChannel.ToLowerInvariant(),
            BlobHash = hash,
            FileSize = file.Length,
            FileName = string.IsNullOrWhiteSpace(req.FileName) ? file.FileName : req.FileName,
            McVersionsCsv = req.McVersionsCsv,
            LoadersCsv = req.LoadersCsv?.ToLowerInvariant()
        };
        db.ModVersions.Add(version);

        // Denormalize the latest MC/loader list onto the parent mod so browsing filters work.
        if (!string.IsNullOrWhiteSpace(req.McVersionsCsv)) mod.McVersionsCsv = req.McVersionsCsv;
        if (!string.IsNullOrWhiteSpace(req.LoadersCsv))    mod.LoadersCsv    = req.LoadersCsv.ToLowerInvariant();
        mod.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return Ok(new HostedModVersionInfo(
            version.Id, version.VersionString, version.Changelog, version.ReleaseChannel,
            version.BlobHash, version.FileSize, version.FileName,
            version.McVersionsCsv, version.LoadersCsv, version.PublishedAt));
    }

    [HttpGet("{id:guid}/files/{versionId:guid}")]
    public async Task<IActionResult> Download(Guid id, Guid versionId, CancellationToken ct)
    {
        var me = this.UserId();
        var mod = await db.Mods.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (mod is null) return NotFound();
        var perms = await resolver.GetAsync(mod, me, ct);
        if (!perms.HasFlag(PackPermissions.Download)) return Forbid();

        var version = await db.ModVersions.FirstOrDefaultAsync(v => v.Id == versionId && v.ModId == id, ct);
        if (version is null) return NotFound();
        if (!blobs.Exists(version.BlobHash)) return NotFound();

        return File(blobs.OpenRead(version.BlobHash), "application/java-archive", version.FileName);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private async Task<string> GenerateUniqueSlugAsync(string name, CancellationToken ct)
    {
        var basic = new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        basic = string.Join('-', basic.Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (basic.Length == 0) basic = "mod";
        if (basic.Length > 80) basic = basic[..80];

        var candidate = basic;
        var n = 1;
        while (await db.Mods.AnyAsync(m => m.Slug == candidate, ct))
            candidate = $"{basic}-{++n}";
        return candidate;
    }

    private static HostedModSummary ToSummary(Mod m, PackPermissions perms) => new(
        m.Id, m.Slug, m.Name, m.Summary, m.OwnerId, m.Owner.UserName ?? "",
        m.Visibility, m.IconBlobHash, m.McVersionsCsv, m.LoadersCsv,
        0, // DownloadCount placeholder — wire to a real counter later
        m.CreatedAt, m.UpdatedAt, perms);
}
