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
        [FromQuery] string? sort = null,
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
        var page = await ApplySort(query, sort)
            .Skip(offset).Take(limit)
            .ToListAsync(ct);

        // One grouped count for the whole page rather than a COUNT per row. Mods with no uploaded
        // version are simply absent from the dictionary and fall through to 0 below.
        var pageIds = page.Select(m => m.Id).ToList();
        var versionCounts = await db.ModVersions
            .Where(v => pageIds.Contains(v.ModId))
            .GroupBy(v => v.ModId)
            .Select(g => new { ModId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ModId, x => x.Count, ct);

        var items = new List<HostedModSummary>(page.Count);
        foreach (var m in page)
        {
            var perms = await resolver.GetAsync(m, me, ct);
            items.Add(ToSummary(m, perms, versionCounts.GetValueOrDefault(m.Id)));
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
        return Ok(ToSummary(mod, PackPermissions.Full, versionCount: 0));
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
        // Compatibility is normalised the same way Create does it, so a loader typed as "NeoForge"
        // here still matches the lower-cased values the browse filter compares against.
        if (req.McVersionsCsv is not null) mod.McVersionsCsv = req.McVersionsCsv;
        if (req.LoadersCsv is not null) mod.LoadersCsv = req.LoadersCsv.ToLowerInvariant();
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

    /// <summary>Corrects the details of an already-uploaded version, leaving its file alone.</summary>
    /// <remarks>
    /// <para>A null field keeps whatever is stored, so a caller that only wants to fix the changelog
    /// sends only the changelog and cannot accidentally blank the rest.</para>
    /// <para>The parent mod's denormalised CSVs are re-written exactly as <see cref="UploadVersion"/>
    /// does it, because they exist to make the browse filters work and are supposed to describe the
    /// most recent word on compatibility. Editing the newest version's Minecraft list and leaving the
    /// mod advertising the old one would make the mod un-findable under the version it now supports.
    /// </para>
    /// </remarks>
    [HttpPatch("{id:guid}/versions/{versionId:guid}")]
    public async Task<ActionResult<HostedModVersionInfo>> UpdateVersion(
        Guid id, Guid versionId, [FromBody] UpdateModVersionRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var mod = await db.Mods.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (mod is null) return NotFound();
        if (mod.OwnerId != me) return Forbid();

        var version = await db.ModVersions.FirstOrDefaultAsync(v => v.Id == versionId && v.ModId == id, ct);
        if (version is null) return NotFound();

        if (req.VersionString is not null)
        {
            var trimmed = req.VersionString.Trim();
            if (trimmed.Length == 0 || trimmed.Length > 64)
                return BadRequest(new { error = "Version must be 1-64 characters" });
            version.VersionString = trimmed;
        }
        if (req.Changelog is not null)
        {
            if (req.Changelog.Length > 4096)
                return BadRequest(new { error = "Changelog must be 4096 characters or fewer" });
            // An empty string is a deliberate "clear it", which is the only way to take back a
            // changelog that was wrong; null above is "leave it".
            version.Changelog = req.Changelog.Length == 0 ? null : req.Changelog;
        }
        if (req.ReleaseChannel is not null)
        {
            var channel = req.ReleaseChannel.Trim().ToLowerInvariant();
            if (channel is not ("release" or "beta" or "alpha"))
                return BadRequest(new { error = "Channel must be release, beta or alpha" });
            version.ReleaseChannel = channel;
        }
        if (req.McVersionsCsv is not null)
            version.McVersionsCsv = req.McVersionsCsv.Length == 0 ? null : req.McVersionsCsv;
        if (req.LoadersCsv is not null)
            version.LoadersCsv = req.LoadersCsv.Length == 0 ? null : req.LoadersCsv.ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(version.McVersionsCsv)) mod.McVersionsCsv = version.McVersionsCsv;
        if (!string.IsNullOrWhiteSpace(version.LoadersCsv))    mod.LoadersCsv    = version.LoadersCsv;
        mod.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return Ok(new HostedModVersionInfo(
            version.Id, version.VersionString, version.Changelog, version.ReleaseChannel,
            version.BlobHash, version.FileSize, version.FileName,
            version.McVersionsCsv, version.LoadersCsv, version.PublishedAt));
    }

    // ---- Icon ----

    /// <summary>Sets the mod's icon.</summary>
    /// <remarks>
    /// <c>Mod.IconBlobHash</c> has existed since the table did and <see cref="HostedModSummary"/> has
    /// always carried it, but nothing could ever write one — so every hosted mod rendered as a grey
    /// letter tile. Replacing an icon drops the previous blob if nothing else points at it, so
    /// re-uploading an icon a few times does not leave a trail of dead files in the store.
    /// </remarks>
    [HttpPost("{id:guid}/icon")]
    [RequestSizeLimit(ControllerHelpers.MaxIconBytes + (1 << 16))]
    public async Task<IActionResult> UploadIcon(Guid id, [FromForm] IFormFile file, CancellationToken ct)
    {
        var me = this.UserId();
        var mod = await db.Mods.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (mod is null) return NotFound();
        // The same gate uploading a version uses: owner only. The icon is part of how the mod
        // presents itself, so it belongs with the other things only the owner can rewrite.
        if (mod.OwnerId != me) return Forbid();

        var (hash, error) = await ControllerHelpers.TryStoreIconAsync(blobs, file, ct);
        if (hash is null) return BadRequest(new { error });

        var previous = mod.IconBlobHash;
        mod.IconBlobHash = hash;
        mod.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        if (previous is not null && previous != hash)
            await ControllerHelpers.DeleteBlobIfUnreferencedAsync(db, blobs, previous, ct);
        return Ok(new { iconBlobHash = hash });
    }

    /// <summary>Serves the mod's icon, or 404 when it has none.</summary>
    /// <remarks>
    /// Anonymous, and gated by the same visibility rule as the rest of the mod: a public mod's icon
    /// is public, anything else needs a caller who can view the mod. It has to work without an
    /// Authorization header because an icon's whole job is to be the source of an &lt;Image&gt;, and
    /// a WPF image binding sends no headers — so a signed-in launcher gets private icons by fetching
    /// the bytes through its API client instead.
    /// </remarks>
    [AllowAnonymous]
    [HttpGet("{id:guid}/icon")]
    public async Task<IActionResult> GetIcon(Guid id, CancellationToken ct)
    {
        var mod = await db.Mods.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (mod is null) return NotFound();

        var perms = await resolver.GetAsync(mod, this.UserIdOrNull(), ct);
        if (!perms.HasFlag(PackPermissions.View)) return NotFound();

        if (string.IsNullOrEmpty(mod.IconBlobHash) || !blobs.Exists(mod.IconBlobHash)) return NotFound();
        return File(blobs.OpenRead(mod.IconBlobHash), ControllerHelpers.IconResponseContentType);
    }

    /// <summary>Clears the mod's icon, putting it back to the letter tile.</summary>
    [HttpDelete("{id:guid}/icon")]
    public async Task<IActionResult> DeleteIcon(Guid id, CancellationToken ct)
    {
        var me = this.UserId();
        var mod = await db.Mods.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (mod is null) return NotFound();
        if (mod.OwnerId != me) return Forbid();

        var previous = mod.IconBlobHash;
        if (previous is null) return NoContent();

        mod.IconBlobHash = null;
        mod.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await ControllerHelpers.DeleteBlobIfUnreferencedAsync(db, blobs, previous, ct);
        return NoContent();
    }

    /// <summary>Removes one uploaded version of a mod.</summary>
    /// <remarks>
    /// Deleting the last version is allowed and leaves the mod itself in place — a mod page with no
    /// file behind it is a valid state (it is exactly what <see cref="Create"/> produces), and making
    /// the final delete a special case would mean the owner could never clear a bad upload.
    /// </remarks>
    [HttpDelete("{id:guid}/versions/{versionId:guid}")]
    public async Task<IActionResult> DeleteVersion(Guid id, Guid versionId, CancellationToken ct)
    {
        var me = this.UserId();
        var mod = await db.Mods.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (mod is null) return NotFound();
        if (mod.OwnerId != me) return Forbid();

        var version = await db.ModVersions.FirstOrDefaultAsync(v => v.Id == versionId && v.ModId == id, ct);
        if (version is null) return NotFound();

        var hash = version.BlobHash;
        db.ModVersions.Remove(version);
        mod.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await ControllerHelpers.DeleteBlobIfUnreferencedAsync(db, blobs, hash, ct);
        return NoContent();
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

    /// <summary>Applies the browse ordering named by <paramref name="sort"/>.</summary>
    /// <remarks>
    /// <para>Anything unrecognised — including null, which is every older client — orders by most
    /// recently updated, the ordering this route has always had. A browse that erupts in a 400
    /// because a launcher sent a mode this build has not heard of would be a worse answer than a
    /// list in the wrong order.</para>
    /// <para><c>downloads</c> is accepted and deliberately resolves to the default: nothing counts
    /// downloads yet (<c>HostedModSummary.DownloadCount</c> is a hard-coded zero), so sorting by it
    /// would shuffle the list arbitrarily and call it popularity. The name is honoured now so the
    /// route does not need changing on the day a counter exists.</para>
    /// </remarks>
    private static IQueryable<Mod> ApplySort(IQueryable<Mod> query, string? sort) => sort?.Trim().ToLowerInvariant() switch
    {
        ModBrowseSort.Created => query.OrderByDescending(m => m.CreatedAt),
        ModBrowseSort.Name => query.OrderBy(m => m.Name),
        _ => query.OrderByDescending(m => m.UpdatedAt)
    };

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

    private static HostedModSummary ToSummary(Mod m, PackPermissions perms, int versionCount) => new(
        m.Id, m.Slug, m.Name, m.Summary, m.OwnerId, m.Owner.UserName ?? "",
        m.Visibility, m.IconBlobHash, m.McVersionsCsv, m.LoadersCsv,
        0, // DownloadCount placeholder — wire to a real counter later
        m.CreatedAt, m.UpdatedAt, perms, versionCount);
}
