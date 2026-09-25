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
public class ModsController(AppDbContext db, ModPermissionResolver resolver, BlobStore blobs, UploadGuard guard) : ControllerBase
{
    private const long MaxModBytes = 64L * 1024 * 1024; // 64 MB per jar; bump if needed

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

        // One grouped count for the whole page rather than a COUNT per row; mods with no uploaded
        // version are absent and fall through to 0.
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

        // Anyone who can see the mod can see who else can, as on instances, so collaborators can find
        // their own row. Usernames only; no e-mail addresses.
        var collabs = mod.Collaborators
            .Select(c => new PackCollaboratorEntry(c.UserId, c.User.UserName ?? "", c.Permissions))
            .ToList();
        var teams = await PackSharing.TeamEntriesAsync(
            db, mod.Teams.Select(t => (t.TeamId, t.Team.Name, t.Permissions)), ct);

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
        // Owner only, as on bundles: name, description and visibility are the owner's. This also guards
        // against 1.8.2 clients, which send name and visibility before every upload.
        if (mod.OwnerId != me) return Forbid();

        if (req.Name is not null) { if (req.Name.Length > 128) return BadRequest(); mod.Name = req.Name.Trim(); }
        if (req.Summary is not null) { if (req.Summary.Length > 512) return BadRequest(); mod.Summary = req.Summary; }
        if (req.Description is not null) { if (req.Description.Length > 4096) return BadRequest(); mod.Description = req.Description; }
        var visibilityChangedTo = req.Visibility is { } wanted && wanted != mod.Visibility ? req.Visibility : null;
        if (req.Visibility is not null) mod.Visibility = req.Visibility.Value;
        // Normalised like Create, so "NeoForge" still matches the lower-cased values browse filters on.
        if (req.McVersionsCsv is not null) mod.McVersionsCsv = req.McVersionsCsv;
        if (req.LoadersCsv is not null) mod.LoadersCsv = req.LoadersCsv.ToLowerInvariant();
        mod.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        if (visibilityChangedTo is not null)
            await PackSharing.LogAsync(db, me, ActivityKind.VisibilityChanged, ActivitySubjectType.Mod,
                mod.Id, mod.Name, detail: visibilityChangedTo.Value.ToString(), ct: ct);
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
    //
    // Sharing can be changed by the owner and anyone granted ManageCollaborators, as on instances
    // and bundles.

    [HttpPost("{id:guid}/collaborators")]
    public async Task<ActionResult<PackCollaboratorEntry>> AddCollaborator(
        Guid id, [FromBody] AddCollaboratorRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var (mod, perms) = await LoadAsync(id, ct);
        if (mod is null) return NotFound();
        if (!ControllerHelpers.CanManageSharing(mod.OwnerId, me, perms)) return Forbid();

        var grantError = ControllerHelpers.ValidateGrant(mod.OwnerId, me, perms, req.Permissions);
        if (grantError is not null) return BadRequest(new { error = grantError });

        var user = await ControllerHelpers.FindUserByNameAsync(db, req.Username, ct);
        if (user is null) return BadRequest(new { error = "User not found" });
        if (user.Id == mod.OwnerId) return BadRequest(new { error = "Owner is implicit" });

        var existing = await db.ModCollaborators
            .FirstOrDefaultAsync(c => c.ModId == id && c.UserId == user.Id, ct);
        var isNew = existing is null;
        if (existing is not null)
            existing.Permissions = req.Permissions;
        else
            db.ModCollaborators.Add(new ModCollaborator { ModId = id, UserId = user.Id, Permissions = req.Permissions });

        mod.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await PackSharing.LogAsync(db, me,
            isNew ? ActivityKind.Shared : ActivityKind.PermissionsChanged,
            ActivitySubjectType.Mod, mod.Id, mod.Name,
            targetUserId: user.Id, detail: req.Permissions.ToString(), ct: ct);
        return Ok(new PackCollaboratorEntry(user.Id, user.UserName ?? "", req.Permissions));
    }

    [HttpPatch("{id:guid}/collaborators/{userId:guid}")]
    public async Task<IActionResult> UpdateCollaborator(
        Guid id, Guid userId, [FromBody] UpdateCollaboratorRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var (mod, perms) = await LoadAsync(id, ct);
        if (mod is null) return NotFound();
        if (!ControllerHelpers.CanManageSharing(mod.OwnerId, me, perms)) return Forbid();

        var grantError = ControllerHelpers.ValidateGrant(mod.OwnerId, me, perms, req.Permissions);
        if (grantError is not null) return BadRequest(new { error = grantError });

        var row = await db.ModCollaborators.FirstOrDefaultAsync(c => c.ModId == id && c.UserId == userId, ct);
        if (row is null) return NotFound(new { error = "They are not a collaborator on this mod." });
        row.Permissions = req.Permissions;
        mod.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await PackSharing.LogAsync(db, me, ActivityKind.PermissionsChanged, ActivitySubjectType.Mod,
            mod.Id, mod.Name, targetUserId: userId, detail: req.Permissions.ToString(), ct: ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}/collaborators/{userId:guid}")]
    public async Task<IActionResult> RemoveCollaborator(Guid id, Guid userId, CancellationToken ct)
    {
        var me = this.UserId();
        var (mod, perms) = await LoadAsync(id, ct);
        if (mod is null) return NotFound();
        // Managers may remove anyone, and anyone may remove themselves (leaving needs no permission, as
        // on instances).
        if (!ControllerHelpers.CanManageSharing(mod.OwnerId, me, perms) && me != userId) return Forbid();

        var row = await db.ModCollaborators.FirstOrDefaultAsync(c => c.ModId == id && c.UserId == userId, ct);
        if (row is null)
            return NotFound(new
            {
                error = userId == me
                    ? "You are not a collaborator on this mod, so there is nothing to leave."
                    : "They are not a collaborator on this mod."
            });
        db.ModCollaborators.Remove(row);
        mod.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await PackSharing.LogAsync(db, me, ActivityKind.Unshared, ActivitySubjectType.Mod,
            mod.Id, mod.Name, targetUserId: userId, ct: ct);
        return NoContent();
    }

    // ---- Mod teams ----

    [HttpPost("{id:guid}/teams")]
    public async Task<ActionResult<PackTeamEntry>> AddTeam(
        Guid id, [FromBody] AddPackTeamRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var (mod, perms) = await LoadAsync(id, ct);
        if (mod is null) return NotFound();
        if (!ControllerHelpers.CanManageSharing(mod.OwnerId, me, perms)) return Forbid();

        var grantError = ControllerHelpers.ValidateGrant(mod.OwnerId, me, perms, req.Permissions);
        if (grantError is not null) return BadRequest(new { error = grantError });

        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == req.TeamId, ct);
        if (team is null) return BadRequest(new { error = "Team not found" });

        // A non-owner manager may only add teams they are in; otherwise they could share someone else's
        // private mod with any team whose id they can guess.
        if (mod.OwnerId != me
            && !await db.TeamMembers.AnyAsync(tm => tm.TeamId == req.TeamId && tm.UserId == me, ct))
            return BadRequest(new { error = "You can only share this with a team you belong to." });

        var existing = await db.ModTeams.FirstOrDefaultAsync(mt => mt.ModId == id && mt.TeamId == req.TeamId, ct);
        var isNew = existing is null;
        if (existing is not null)
            existing.Permissions = req.Permissions;
        else
            db.ModTeams.Add(new ModTeam { ModId = id, TeamId = req.TeamId, Permissions = req.Permissions });

        mod.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await PackSharing.LogAsync(db, me,
            isNew ? ActivityKind.Shared : ActivityKind.PermissionsChanged,
            ActivitySubjectType.Mod, mod.Id, mod.Name,
            targetTeamId: team.Id, detail: req.Permissions.ToString(), ct: ct);

        var entries = await PackSharing.TeamEntriesAsync(db, [(team.Id, team.Name, req.Permissions)], ct);
        return Ok(entries[0]);
    }

    [HttpPatch("{id:guid}/teams/{teamId:guid}")]
    public async Task<IActionResult> UpdateTeam(
        Guid id, Guid teamId, [FromBody] UpdatePackTeamRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var (mod, perms) = await LoadAsync(id, ct);
        if (mod is null) return NotFound();
        if (!ControllerHelpers.CanManageSharing(mod.OwnerId, me, perms)) return Forbid();

        var grantError = ControllerHelpers.ValidateGrant(mod.OwnerId, me, perms, req.Permissions);
        if (grantError is not null) return BadRequest(new { error = grantError });

        var row = await db.ModTeams.FirstOrDefaultAsync(mt => mt.ModId == id && mt.TeamId == teamId, ct);
        if (row is null) return NotFound(new { error = "That team is not on this mod." });
        row.Permissions = req.Permissions;
        mod.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await PackSharing.LogAsync(db, me, ActivityKind.PermissionsChanged, ActivitySubjectType.Mod,
            mod.Id, mod.Name, targetTeamId: teamId, detail: req.Permissions.ToString(), ct: ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}/teams/{teamId:guid}")]
    public async Task<IActionResult> RemoveTeam(Guid id, Guid teamId, CancellationToken ct)
    {
        var me = this.UserId();
        var (mod, perms) = await LoadAsync(id, ct);
        if (mod is null) return NotFound();
        if (!ControllerHelpers.CanManageSharing(mod.OwnerId, me, perms)) return Forbid();

        var row = await db.ModTeams.FirstOrDefaultAsync(mt => mt.ModId == id && mt.TeamId == teamId, ct);
        if (row is null) return NotFound(new { error = "That team is not on this mod." });
        db.ModTeams.Remove(row);
        mod.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await PackSharing.LogAsync(db, me, ActivityKind.Unshared, ActivitySubjectType.Mod,
            mod.Id, mod.Name, targetTeamId: teamId, ct: ct);
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
        var (mod, perms) = await LoadAsync(id, ct);
        if (mod is null) return NotFound();
        // UploadShared is enough, as on bundles (the dialog offers it as "can upload changes").
        if (!perms.HasFlag(PackPermissions.UploadShared)) return Forbid();
        if (file is null || file.Length == 0) return BadRequest(new { error = "File required" });
        if (file.Length > MaxModBytes) return BadRequest(new { error = $"File too large (max {MaxModBytes / (1024 * 1024)} MB)" });

        CreateModVersionRequest? req;
        try { req = JsonSerializer.Deserialize<CreateModVersionRequest>(metadata, JsonOpts); }
        catch { return BadRequest(new { error = "Invalid metadata JSON" }); }
        if (req is null || string.IsNullOrWhiteSpace(req.VersionString))
            return BadRequest(new { error = "VersionString required" });

        // Check free disk space and the owner's quota before storing. A collaborator's upload counts
        // against the owner's quota, as a pack sync does.
        if (await guard.RefuseAsync(mod.OwnerId, me, file, ct) is { } refusal) return refusal;

        StoredBlob stored;
        await using (var s = file.OpenReadStream())
            stored = await blobs.PutAsync(s, ct);
        if (await guard.ChargeAsync(mod.OwnerId, me, stored, ct) is { } overQuota) return overQuota;
        var hash = stored.Hash;

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
        guard.Settle(mod.OwnerId, hash);

        await PackSharing.LogAsync(db, me, ActivityKind.VersionPublished, ActivitySubjectType.Mod,
            mod.Id, mod.Name, detail: version.VersionString, ct: ct);

        return Ok(new HostedModVersionInfo(
            version.Id, version.VersionString, version.Changelog, version.ReleaseChannel,
            version.BlobHash, version.FileSize, version.FileName,
            version.McVersionsCsv, version.LoadersCsv, version.PublishedAt));
    }

    /// <summary>Corrects the details of an already-uploaded version, leaving its file alone.</summary>
    /// <remarks>
    /// <para>A null field keeps the stored value, so a caller can send only what it wants to fix.</para>
    /// <para>The mod's denormalised compatibility CSVs are only rewritten when this is the newest
    /// version, as in <see cref="UploadVersion"/>; copying them from an older version would republish
    /// the mod under an outdated compatibility list.</para>
    /// </remarks>
    [HttpPatch("{id:guid}/versions/{versionId:guid}")]
    public async Task<ActionResult<HostedModVersionInfo>> UpdateVersion(
        Guid id, Guid versionId, [FromBody] UpdateModVersionRequest req, CancellationToken ct)
    {
        var (mod, perms) = await LoadAsync(id, ct);
        if (mod is null) return NotFound();
        // Whoever may publish a version may correct one, as on bundles.
        if (!perms.HasFlag(PackPermissions.UploadShared)) return Forbid();

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
            // An empty string clears the changelog; null (above) leaves it as is.
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

        // Only the newest version updates the mod's CSVs. Editing an old version is common (the
        // per-version "Edit details..." action), and copying its CSVs up would republish the mod as,
        // say, 1.16-only with nothing to recompute it.
        var isNewest = !await db.ModVersions
            .AnyAsync(v => v.ModId == id && v.Id != versionId && v.PublishedAt > version.PublishedAt, ct);
        if (isNewest)
        {
            if (!string.IsNullOrWhiteSpace(version.McVersionsCsv)) mod.McVersionsCsv = version.McVersionsCsv;
            if (!string.IsNullOrWhiteSpace(version.LoadersCsv))    mod.LoadersCsv    = version.LoadersCsv;
        }
        mod.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return Ok(new HostedModVersionInfo(
            version.Id, version.VersionString, version.Changelog, version.ReleaseChannel,
            version.BlobHash, version.FileSize, version.FileName,
            version.McVersionsCsv, version.LoadersCsv, version.PublishedAt));
    }

    // ---- Icon ----

    /// <summary>Sets the mod's icon.</summary>
    /// <remarks>Replacing an icon drops the previous blob if nothing else references it, so repeated
    /// uploads don't leave dead files in the store.</remarks>
    [HttpPost("{id:guid}/icon")]
    [RequestSizeLimit(ControllerHelpers.MaxIconBytes + (1 << 16))]
    public async Task<IActionResult> UploadIcon(Guid id, [FromForm] IFormFile file, CancellationToken ct)
    {
        var me = this.UserId();
        var mod = await db.Mods.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (mod is null) return NotFound();
        // Owner only, like a bundle's icon: it belongs with the name and description.
        if (mod.OwnerId != me) return Forbid();

        var (hash, failure) = await ControllerHelpers.TryStoreIconAsync(blobs, guard, mod.OwnerId, me, file, ct);
        if (hash is null) return failure!;

        var previous = mod.IconBlobHash;
        mod.IconBlobHash = hash;
        mod.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        guard.Settle(mod.OwnerId, hash);

        if (previous is not null && previous != hash)
            await ControllerHelpers.DeleteBlobIfUnreferencedAsync(db, blobs, previous, ct);
        return Ok(new { iconBlobHash = hash });
    }

    /// <summary>Serves the mod's icon, or 404 when it has none.</summary>
    /// <remarks>Anonymous but gated by the mod's visibility: a public mod's icon is public, anything
    /// else needs a caller who can view the mod. WPF image bindings send no headers, so the launcher
    /// fetches private icons through its API client instead.</remarks>
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
    /// <remarks>Deleting the last version is allowed; a mod with no file is valid (it's what
    /// <see cref="Create"/> produces). Owner only, as on bundles: contributors may add versions but
    /// not delete release history.</remarks>
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

    /// <summary>Loads a mod together with what the caller may do to it.</summary>
    private async Task<(Mod? Mod, PackPermissions Perms)> LoadAsync(Guid id, CancellationToken ct)
    {
        var mod = await db.Mods.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (mod is null) return (null, PackPermissions.None);
        return (mod, await resolver.GetAsync(mod, this.UserId(), ct));
    }

    /// <summary>Applies the browse ordering named by <paramref name="sort"/>.</summary>
    /// <remarks>Unknown values, including null from older clients, order by most recently updated
    /// rather than returning a 400. <c>downloads</c> also maps to the default for now, since download
    /// counts aren't tracked yet (<c>HostedModSummary.DownloadCount</c> is always zero).</remarks>
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
        0, // DownloadCount placeholder; wire to a real counter later
        m.CreatedAt, m.UpdatedAt, perms, versionCount);
}
