using System.Text.Json;
using CloudLauncher.Server.Auth;
using CloudLauncher.Server.Data;
using CloudLauncher.Server.Storage;
using CloudLauncher.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CloudLauncher.Server.Controllers;

/// <summary>
/// The generic hosted content family: shader packs, config bundles, KubeJS bundles and data packs,
/// told apart by <see cref="BundleKind"/> and by the folder each unpacks into.
/// </summary>
/// <remarks>
/// <para>Mirrors <see cref="ResourcePacksController"/> (browse sources, multipart upload,
/// collaborator and team routes) so the client's hosted-content code works unchanged. Slugs are
/// unique per <c>(Kind, Slug)</c> and every query is scoped by kind.</para>
/// <para>A bundle is a zip the client unpacks into a folder the bundle names, so that folder and
/// every entry path are untrusted input for a file write on someone else's machine. Both go
/// through <see cref="BundleContentPolicy"/> before anything is stored.</para>
/// </remarks>
[ApiController]
[Authorize]
[Route("bundles")]
public class ContentBundlesController(
    AppDbContext db,
    ContentBundlePermissionResolver resolver,
    BlobStore blobs,
    UploadGuard guard,
    ILogger<ContentBundlesController> logger) : ControllerBase
{
    /// <summary>How big one bundle zip may be.</summary>
    /// <remarks>Same as resource packs, and under the 128 MB default multipart body limit.</remarks>
    private const long MaxBundleBytes = 64L * 1024 * 1024;

    private static readonly HashSet<string> ReleaseChannels =
        new(StringComparer.OrdinalIgnoreCase) { "release", "beta", "alpha" };

    // ── browse ───────────────────────────────────────────────────────────────

    /// <summary>Lists bundles the caller may see, scoped by kind and source.</summary>
    /// <remarks>Served at both <c>/bundles</c> and <c>/bundles/browse</c> (the other families use
    /// <c>/browse</c>). Paging takes <c>offset</c>/<c>limit</c> like the other families, with
    /// <c>skip</c>/<c>take</c> accepted as aliases.</remarks>
    [HttpGet]
    [HttpGet("browse")]
    public async Task<ActionResult<BundleBrowsePage>> Browse(
        [FromQuery] BundleKind? kind = null,
        [FromQuery] BundleBrowseSource source = BundleBrowseSource.Public,
        [FromQuery] Guid? teamId = null,
        [FromQuery] string? q = null,
        [FromQuery] string? mc = null,
        [FromQuery] string? mcVersion = null,
        [FromQuery] string? loader = null,
        [FromQuery] string? sort = null,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 25,
        [FromQuery] int? skip = null,
        [FromQuery] int? take = null,
        CancellationToken ct = default)
    {
        if (skip.HasValue) offset = skip.Value;
        if (take.HasValue) limit = take.Value;
        if (limit <= 0 || limit > 100) limit = 25;
        if (offset < 0) offset = 0;

        var me = this.UserId();
        var query = db.ContentBundles.Include(b => b.Owner).AsQueryable();

        if (kind.HasValue) query = query.Where(b => b.Kind == kind.Value);

        query = source switch
        {
            BundleBrowseSource.Public =>
                query.Where(b => b.Visibility == PackVisibility.Public),
            BundleBrowseSource.Personal =>
                query.Where(b => b.OwnerId == me),
            BundleBrowseSource.Shared =>
                query.Where(b => b.OwnerId != me
                                 && db.ContentBundleCollaborators.Any(c => c.BundleId == b.Id && c.UserId == me)),
            BundleBrowseSource.Team =>
                teamId.HasValue
                    ? query.Where(b => db.ContentBundleTeams.Any(bt => bt.BundleId == b.Id && bt.TeamId == teamId.Value)
                                       && db.TeamMembers.Any(tm => tm.TeamId == teamId.Value && tm.UserId == me))
                    : query.Where(b => db.ContentBundleTeams.Any(bt => bt.BundleId == b.Id
                                       && db.TeamMembers.Any(tm => tm.TeamId == bt.TeamId && tm.UserId == me))),
            _ => query.Where(_ => false)
        };

        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim().ToLower();
            query = query.Where(b =>
                b.Name.ToLower().Contains(needle)
                || (b.Summary != null && b.Summary.ToLower().Contains(needle))
                || (b.Description != null && b.Description.ToLower().Contains(needle)));
        }

        var wantedMc = string.IsNullOrWhiteSpace(mc) ? mcVersion : mc;
        if (!string.IsNullOrWhiteSpace(wantedMc))
            query = query.Where(b => b.McVersionsCsv != null && b.McVersionsCsv.Contains(wantedMc));
        if (!string.IsNullOrWhiteSpace(loader))
        {
            var wantedLoader = loader.Trim().ToLower();
            query = query.Where(b => b.LoadersCsv != null && b.LoadersCsv.ToLower().Contains(wantedLoader));
        }

        var total = await query.CountAsync(ct);

        query = (sort ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            BundleBrowseSort.Created => query.OrderByDescending(b => b.CreatedAt),
            BundleBrowseSort.Name => query.OrderBy(b => b.Name),
            BundleBrowseSort.Downloads => query.OrderByDescending(b => b.DownloadCount),
            _ => query.OrderByDescending(b => b.UpdatedAt)
        };

        var page = await query.Skip(offset).Take(limit).ToListAsync(ct);

        // One grouped count for the whole page rather than a COUNT per row; a bundle with no
        // uploaded version is simply absent and falls through to 0.
        var pageIds = page.Select(b => b.Id).ToList();
        var versionCounts = await db.ContentBundleVersions
            .Where(v => pageIds.Contains(v.BundleId))
            .GroupBy(v => v.BundleId)
            .Select(g => new { BundleId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.BundleId, x => x.Count, ct);

        var items = new List<ContentBundleSummary>(page.Count);
        foreach (var b in page)
        {
            var perms = await resolver.GetAsync(b, me, ct);
            items.Add(ToSummary(b, perms, versionCounts.GetValueOrDefault(b.Id)));
        }

        return Ok(new BundleBrowsePage(items, offset, limit, total));
    }

    // ── detail ───────────────────────────────────────────────────────────────

    /// <summary>One bundle by id, or by slug.</summary>
    /// <remarks>Slugs are unique per kind only, so an ambiguous slug is a 400 naming the matching kinds;
    /// pass <c>?kind=</c> to pick one.</remarks>
    [HttpGet("{idOrSlug}")]
    public async Task<ActionResult<ContentBundleDetail>> Get(
        string idOrSlug, [FromQuery] BundleKind? kind = null, CancellationToken ct = default)
    {
        var me = this.UserId();

        var query = db.ContentBundles
            .Include(b => b.Owner)
            .Include(b => b.Versions)
            .Include(b => b.Collaborators).ThenInclude(c => c.User)
            .Include(b => b.Teams).ThenInclude(t => t.Team)
            .AsQueryable();

        ContentBundle? bundle;
        if (Guid.TryParse(idOrSlug, out var id))
        {
            bundle = await query.FirstOrDefaultAsync(b => b.Id == id, ct);
        }
        else
        {
            var slug = idOrSlug.Trim().ToLowerInvariant();
            var bySlug = query.Where(b => b.Slug == slug);
            if (kind.HasValue)
            {
                var wantedKind = kind.Value;
                bySlug = bySlug.Where(b => b.Kind == wantedKind);
            }
            var matches = await bySlug.ToListAsync(ct);
            if (matches.Count > 1)
                return BadRequest(new
                {
                    error = $"'{slug}' names a bundle in more than one kind. Pass ?kind= to choose.",
                    kinds = matches.Select(m => m.Kind).Distinct().ToArray()
                });
            bundle = matches.FirstOrDefault();
        }

        if (bundle is null) return NotFound();

        var perms = await resolver.GetAsync(bundle, me, ct);
        if (!perms.HasFlag(PackPermissions.View)) return Forbid();

        var teams = await PackSharing.TeamEntriesAsync(
            db, bundle.Teams.Select(t => (t.TeamId, t.Team.Name, t.Permissions)), ct);
        return Ok(ToDetail(bundle, perms, me, teams));
    }

    // ── create / update / delete ─────────────────────────────────────────────

    [HttpPost]
    public async Task<ActionResult<ContentBundleSummary>> Create(
        [FromBody] CreateBundleRequest req, CancellationToken ct)
    {
        if (!BundleContentPolicy.IsKnownKind(req.Kind))
            return BadRequest(new { error = $"Unknown bundle kind '{(int)req.Kind}'." });
        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 128)
            return BadRequest(new { error = "Bundle name must be 1-128 characters" });
        if (req.Summary?.Length > 512)
            return BadRequest(new { error = "Summary must be 512 characters or fewer" });
        if (req.Description?.Length > 4096)
            return BadRequest(new { error = "Description must be 4096 characters or fewer" });
        if (req.InitialMcVersionsCsv?.Length > 512)
            return BadRequest(new { error = "Minecraft versions must be 512 characters or fewer" });
        if (req.InitialLoadersCsv?.Length > 128)
            return BadRequest(new { error = "Loaders must be 128 characters or fewer" });
        if (!BundleContentPolicy.TryNormalizeTargetPathRoot(req.Kind, req.TargetPathRoot, out var root, out var rootError))
            return BadRequest(new { error = rootError });

        var me = this.UserId();
        var bundle = new ContentBundle
        {
            Kind = req.Kind,
            Name = req.Name.Trim(),
            Slug = await GenerateUniqueSlugAsync(req.Kind, req.Name, ct),
            Summary = req.Summary,
            Description = req.Description,
            OwnerId = me,
            Visibility = req.Visibility,
            TargetPathRoot = root,
            McVersionsCsv = req.InitialMcVersionsCsv,
            LoadersCsv = req.InitialLoadersCsv
        };
        db.ContentBundles.Add(bundle);
        await db.SaveChangesAsync(ct);
        await db.Entry(bundle).Reference(b => b.Owner).LoadAsync(ct);
        return Ok(ToSummary(bundle, PackPermissions.Full, versionCount: 0));
    }

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateBundleRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var bundle = await db.ContentBundles.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (bundle is null) return NotFound();
        if (bundle.OwnerId != me) return Forbid();

        if (req.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 128)
                return BadRequest(new { error = "Bundle name must be 1-128 characters" });
            bundle.Name = req.Name.Trim();
        }
        if (req.Summary is not null)
        {
            if (req.Summary.Length > 512) return BadRequest(new { error = "Summary must be 512 characters or fewer" });
            bundle.Summary = req.Summary;
        }
        if (req.Description is not null)
        {
            if (req.Description.Length > 4096) return BadRequest(new { error = "Description must be 4096 characters or fewer" });
            bundle.Description = req.Description;
        }
        if (req.McVersionsCsv is not null)
        {
            if (req.McVersionsCsv.Length > 512) return BadRequest(new { error = "Minecraft versions must be 512 characters or fewer" });
            bundle.McVersionsCsv = req.McVersionsCsv;
        }
        if (req.LoadersCsv is not null)
        {
            if (req.LoadersCsv.Length > 128) return BadRequest(new { error = "Loaders must be 128 characters or fewer" });
            bundle.LoadersCsv = req.LoadersCsv;
        }
        if (req.TargetPathRoot is not null)
        {
            // Validate against the stored kind, never one from the request, so a caller can't pass a more
            // permissive kind to widen the allow-list. UpdateBundleRequest can't change Kind.
            if (!BundleContentPolicy.TryNormalizeTargetPathRoot(bundle.Kind, req.TargetPathRoot, out var root, out var rootError))
                return BadRequest(new { error = rootError });
            bundle.TargetPathRoot = root;
        }

        var visibilityChanged = req.Visibility is not null && req.Visibility.Value != bundle.Visibility;
        if (req.Visibility is not null) bundle.Visibility = req.Visibility.Value;

        bundle.UpdatedAt = DateTimeOffset.UtcNow;
        if (visibilityChanged)
            ActivityLog.Record(db, me, ActivityKind.VisibilityChanged, ActivitySubjectType.Bundle,
                bundle.Id, bundle.Name, detail: bundle.Visibility.ToString());
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Deletes a bundle and frees every blob it was the last reference to.</summary>
    /// <remarks>Version rows cascade but blobs have no foreign keys. Hashes are read before the delete
    /// and freed after the save, because <see cref="ControllerHelpers.DeleteBlobIfUnreferencedAsync"/>
    /// asks the database, where an unsaved delete still counts as a reference.</remarks>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var me = this.UserId();
        var bundle = await db.ContentBundles.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (bundle is null) return NotFound();
        if (bundle.OwnerId != me) return Forbid();

        var hashes = await db.ContentBundleVersions
            .Where(v => v.BundleId == id)
            .Select(v => v.BlobHash)
            .ToListAsync(ct);
        if (!string.IsNullOrEmpty(bundle.IconBlobHash)) hashes.Add(bundle.IconBlobHash);

        db.ContentBundles.Remove(bundle);
        await db.SaveChangesAsync(ct);

        foreach (var hash in hashes.Distinct(StringComparer.OrdinalIgnoreCase))
            await ControllerHelpers.DeleteBlobIfUnreferencedAsync(db, blobs, hash, ct);
        return NoContent();
    }

    // ── collaborators ────────────────────────────────────────────────────────

    [HttpPost("{id:guid}/collaborators")]
    public async Task<ActionResult<PackCollaboratorEntry>> AddCollaborator(
        Guid id, [FromBody] AddCollaboratorRequest req, CancellationToken ct)
    {
        var (bundle, perms, err) = await LoadManageableAsync(id, ct);
        if (err is not null) return err;

        var me = this.UserId();
        var user = await ControllerHelpers.FindUserByNameAsync(db, req.Username, ct);
        if (user is null) return BadRequest(new { error = "User not found" });
        if (user.Id == bundle!.OwnerId) return BadRequest(new { error = "Owner is implicit" });
        if (GuardGrant(bundle, perms, req.Permissions, me) is { } grantErr) return grantErr;

        var existing = await db.ContentBundleCollaborators
            .FirstOrDefaultAsync(c => c.BundleId == id && c.UserId == user.Id, ct);
        var isNew = existing is null;
        if (existing is not null)
            existing.Permissions = req.Permissions;
        else
            db.ContentBundleCollaborators.Add(new ContentBundleCollaborator
            {
                BundleId = id,
                UserId = user.Id,
                Permissions = req.Permissions
            });

        bundle.UpdatedAt = DateTimeOffset.UtcNow;
        ActivityLog.Record(db, me, isNew ? ActivityKind.Shared : ActivityKind.PermissionsChanged,
            ActivitySubjectType.Bundle, bundle.Id, bundle.Name,
            targetUserId: user.Id, detail: req.Permissions.ToString());
        await db.SaveChangesAsync(ct);
        return Ok(new PackCollaboratorEntry(user.Id, user.UserName ?? "", req.Permissions));
    }

    [HttpPatch("{id:guid}/collaborators/{userId:guid}")]
    public async Task<IActionResult> UpdateCollaborator(
        Guid id, Guid userId, [FromBody] UpdateCollaboratorRequest req, CancellationToken ct)
    {
        var (bundle, perms, err) = await LoadManageableAsync(id, ct);
        if (err is not null) return err;

        var me = this.UserId();
        if (GuardGrant(bundle!, perms, req.Permissions, me) is { } grantErr) return grantErr;

        var row = await db.ContentBundleCollaborators.FirstOrDefaultAsync(c => c.BundleId == id && c.UserId == userId, ct);
        if (row is null) return NotFound();
        row.Permissions = req.Permissions;
        bundle!.UpdatedAt = DateTimeOffset.UtcNow;
        ActivityLog.Record(db, me, ActivityKind.PermissionsChanged, ActivitySubjectType.Bundle,
            bundle.Id, bundle.Name, targetUserId: userId, detail: req.Permissions.ToString());
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}/collaborators/{userId:guid}")]
    public async Task<IActionResult> RemoveCollaborator(Guid id, Guid userId, CancellationToken ct)
    {
        var me = this.UserId();
        var (bundle, _, err) = await LoadManageableAsync(id, ct);
        // Managers may remove anyone, and anyone may remove themselves (leaving needs no permission, as
        // on instances). Only the "may not manage" refusal is waived; a missing bundle is still a 404.
        if (err is not null && !(bundle is not null && userId == me)) return err;

        var row = await db.ContentBundleCollaborators.FirstOrDefaultAsync(c => c.BundleId == id && c.UserId == userId, ct);
        if (row is null)
            return NotFound(new
            {
                error = userId == me
                    ? "You are not a collaborator on this bundle, so there is nothing to leave."
                    : "They are not a collaborator on this bundle."
            });
        db.ContentBundleCollaborators.Remove(row);

        // Revoke the invitation that granted this too, as on instances, or the original token would let
        // a removed collaborator back in.
        var now = DateTimeOffset.UtcNow;
        var theirInvites = await db.ContentBundleInvitations
            .Where(i => i.BundleId == id && i.InvitedUserId == userId && i.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var invite in theirInvites) invite.RevokedAt = now;

        bundle!.UpdatedAt = now;
        ActivityLog.Record(db, me, ActivityKind.Unshared, ActivitySubjectType.Bundle,
            bundle.Id, bundle.Name, targetUserId: userId);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── teams ────────────────────────────────────────────────────────────────

    [HttpPost("{id:guid}/teams")]
    public async Task<ActionResult<PackTeamEntry>> AddTeam(
        Guid id, [FromBody] AddPackTeamRequest req, CancellationToken ct)
    {
        var (bundle, perms, err) = await LoadManageableAsync(id, ct);
        if (err is not null) return err;

        var me = this.UserId();
        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == req.TeamId, ct);
        if (team is null) return BadRequest(new { error = "Team not found" });
        if (GuardGrant(bundle!, perms, req.Permissions, me) is { } grantErr) return grantErr;

        // A non-owner manager may only add teams they are in; otherwise they could share someone else's
        // private bundle with any team whose id they can guess.
        if (bundle!.OwnerId != me
            && !await db.TeamMembers.AnyAsync(tm => tm.TeamId == req.TeamId && tm.UserId == me, ct))
            return BadRequest(new { error = "You can only share this with a team you belong to." });

        var existing = await db.ContentBundleTeams.FirstOrDefaultAsync(bt => bt.BundleId == id && bt.TeamId == req.TeamId, ct);
        var isNew = existing is null;
        if (existing is not null)
            existing.Permissions = req.Permissions;
        else
            db.ContentBundleTeams.Add(new ContentBundleTeam
            {
                BundleId = id,
                TeamId = req.TeamId,
                Permissions = req.Permissions
            });

        bundle.UpdatedAt = DateTimeOffset.UtcNow;
        ActivityLog.Record(db, me, isNew ? ActivityKind.Shared : ActivityKind.PermissionsChanged,
            ActivitySubjectType.Bundle, bundle.Id, bundle.Name,
            targetTeamId: team.Id, detail: req.Permissions.ToString());
        await db.SaveChangesAsync(ct);

        var entries = await PackSharing.TeamEntriesAsync(db, [(team.Id, team.Name, req.Permissions)], ct);
        return Ok(entries[0]);
    }

    [HttpPatch("{id:guid}/teams/{teamId:guid}")]
    public async Task<IActionResult> UpdateTeam(
        Guid id, Guid teamId, [FromBody] UpdatePackTeamRequest req, CancellationToken ct)
    {
        var (bundle, perms, err) = await LoadManageableAsync(id, ct);
        if (err is not null) return err;

        var me = this.UserId();
        if (GuardGrant(bundle!, perms, req.Permissions, me) is { } grantErr) return grantErr;

        var row = await db.ContentBundleTeams.FirstOrDefaultAsync(bt => bt.BundleId == id && bt.TeamId == teamId, ct);
        if (row is null) return NotFound();
        row.Permissions = req.Permissions;
        bundle!.UpdatedAt = DateTimeOffset.UtcNow;
        ActivityLog.Record(db, me, ActivityKind.PermissionsChanged, ActivitySubjectType.Bundle,
            bundle.Id, bundle.Name, targetTeamId: teamId, detail: req.Permissions.ToString());
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}/teams/{teamId:guid}")]
    public async Task<IActionResult> RemoveTeam(Guid id, Guid teamId, CancellationToken ct)
    {
        var (bundle, _, err) = await LoadManageableAsync(id, ct);
        if (err is not null) return err;

        var row = await db.ContentBundleTeams.FirstOrDefaultAsync(bt => bt.BundleId == id && bt.TeamId == teamId, ct);
        if (row is null) return NotFound();
        db.ContentBundleTeams.Remove(row);
        bundle!.UpdatedAt = DateTimeOffset.UtcNow;
        ActivityLog.Record(db, this.UserId(), ActivityKind.Unshared, ActivitySubjectType.Bundle,
            bundle.Id, bundle.Name, targetTeamId: teamId);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── versions ─────────────────────────────────────────────────────────────

    /// <summary>Publishes one version of a bundle: a multipart zip plus a JSON metadata part.</summary>
    /// <remarks>Same shape as the mod, world and resource-pack routes. The archive's entry paths are
    /// checked after upload, and a failing zip's blob is released.
    /// <see cref="PackPermissions.UploadShared"/> is enough to publish, as on the other families.</remarks>
    [HttpPost("{id:guid}/versions")]
    [RequestSizeLimit(MaxBundleBytes + (1 << 16))]
    public async Task<ActionResult<ContentBundleVersionInfo>> UploadVersion(
        Guid id,
        [FromForm] IFormFile file,
        [FromForm] string metadata,
        CancellationToken ct)
    {
        var (bundle, _, err) = await LoadAsync(id, PackPermissions.UploadShared, ct);
        if (err is not null) return err;
        if (file is null || file.Length == 0) return BadRequest(new { error = "File required" });
        if (file.Length > MaxBundleBytes)
            return BadRequest(new { error = $"File too large (max {MaxBundleBytes / (1024 * 1024)} MB)" });

        CreateBundleVersionRequest? req;
        try { req = JsonSerializer.Deserialize<CreateBundleVersionRequest>(metadata, JsonOpts); }
        catch { return BadRequest(new { error = "Invalid metadata JSON" }); }
        if (req is null || string.IsNullOrWhiteSpace(req.VersionString))
            return BadRequest(new { error = "VersionString required" });
        if (req.VersionString.Trim().Length > 64)
            return BadRequest(new { error = "Version must be 64 characters or fewer" });
        if (req.Changelog?.Length > 8192)
            return BadRequest(new { error = "Changelog must be 8192 characters or fewer" });
        if (req.McVersionsCsv?.Length > 512)
            return BadRequest(new { error = "Minecraft versions must be 512 characters or fewer" });
        if (req.LoadersCsv?.Length > 128)
            return BadRequest(new { error = "Loaders must be 128 characters or fewer" });

        var channel = string.IsNullOrWhiteSpace(req.ReleaseChannel) ? "release" : req.ReleaseChannel.Trim().ToLowerInvariant();
        if (!ReleaseChannels.Contains(channel))
            return BadRequest(new { error = "Release channel must be release, beta or alpha" });

        // Only a download label, but the client writes it to disk, so strip any path from it.
        var declaredName = string.IsNullOrWhiteSpace(req.FileName) ? file.FileName : req.FileName;
        var fileName = SafeDownloadName(declaredName, bundle!.Slug);

        // Check free disk space and the owner's quota before storing. A collaborator's upload counts
        // against the owner's quota, as a pack sync does.
        var me = this.UserId();
        if (await guard.RefuseAsync(bundle.OwnerId, me, file, ct) is { } refusal) return refusal;

        StoredBlob blob;
        await using (var s = file.OpenReadStream())
            blob = await blobs.PutAsync(s, ct);
        var hash = blob.Hash;

        // Validate the stored blob rather than the request stream: ZipArchive needs a seekable stream to
        // read the central directory, and this avoids buffering the upload twice.
        string? zipError;
        await using (var stored = blobs.OpenRead(hash))
            zipError = BundleContentPolicy.ValidateZip(stored);
        if (zipError is not null)
        {
            // DiscardAsync only deletes the blob if this upload created it and nothing else references it.
            await guard.DiscardAsync(blob);
            return BadRequest(new { error = zipError });
        }

        if (await guard.ChargeAsync(bundle.OwnerId, me, blob, ct) is { } overQuota) return overQuota;

        var version = new ContentBundleVersion
        {
            BundleId = bundle.Id,
            VersionString = req.VersionString.Trim(),
            Changelog = req.Changelog,
            ReleaseChannel = channel,
            BlobHash = hash,
            FileSize = file.Length,
            FileName = fileName,
            McVersionsCsv = req.McVersionsCsv,
            LoadersCsv = req.LoadersCsv
        };
        db.ContentBundleVersions.Add(version);

        if (!string.IsNullOrWhiteSpace(req.McVersionsCsv)) bundle.McVersionsCsv = req.McVersionsCsv;
        if (!string.IsNullOrWhiteSpace(req.LoadersCsv)) bundle.LoadersCsv = req.LoadersCsv;
        bundle.UpdatedAt = DateTimeOffset.UtcNow;
        ActivityLog.Record(db, me, ActivityKind.VersionPublished, ActivitySubjectType.Bundle,
            bundle.Id, bundle.Name, detail: version.VersionString);
        await db.SaveChangesAsync(ct);
        guard.Settle(bundle.OwnerId, hash);

        return Ok(ToVersionInfo(version));
    }

    /// <summary>Corrects a published version's metadata. The file behind it never moves.</summary>
    [HttpPatch("{id:guid}/versions/{versionId:guid}")]
    public async Task<IActionResult> UpdateVersion(
        Guid id, Guid versionId, [FromBody] UpdateBundleVersionRequest req, CancellationToken ct)
    {
        var (bundle, _, err) = await LoadAsync(id, PackPermissions.UploadShared, ct);
        if (err is not null) return err;

        var version = await db.ContentBundleVersions.FirstOrDefaultAsync(v => v.Id == versionId && v.BundleId == id, ct);
        if (version is null) return NotFound();

        if (req.VersionString is not null)
        {
            if (string.IsNullOrWhiteSpace(req.VersionString) || req.VersionString.Length > 64)
                return BadRequest(new { error = "Version must be 1-64 characters" });
            version.VersionString = req.VersionString.Trim();
        }
        if (req.Changelog is not null)
        {
            if (req.Changelog.Length > 8192) return BadRequest(new { error = "Changelog must be 8192 characters or fewer" });
            version.Changelog = req.Changelog;
        }
        if (req.ReleaseChannel is not null)
        {
            var channel = req.ReleaseChannel.Trim().ToLowerInvariant();
            if (!ReleaseChannels.Contains(channel))
                return BadRequest(new { error = "Release channel must be release, beta or alpha" });
            version.ReleaseChannel = channel;
        }
        if (req.McVersionsCsv is not null)
        {
            if (req.McVersionsCsv.Length > 512) return BadRequest(new { error = "Minecraft versions must be 512 characters or fewer" });
            version.McVersionsCsv = req.McVersionsCsv;
        }
        if (req.LoadersCsv is not null)
        {
            if (req.LoadersCsv.Length > 128) return BadRequest(new { error = "Loaders must be 128 characters or fewer" });
            version.LoadersCsv = req.LoadersCsv;
        }

        bundle!.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Removes one uploaded version and frees its blob if nothing else names it.</summary>
    /// <remarks>Owner only: contributors may add versions but not delete release history.</remarks>
    [HttpDelete("{id:guid}/versions/{versionId:guid}")]
    public async Task<IActionResult> DeleteVersion(Guid id, Guid versionId, CancellationToken ct)
    {
        var me = this.UserId();
        var bundle = await db.ContentBundles.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (bundle is null) return NotFound();
        if (bundle.OwnerId != me) return Forbid();

        var version = await db.ContentBundleVersions.FirstOrDefaultAsync(v => v.Id == versionId && v.BundleId == id, ct);
        if (version is null) return NotFound();

        var hash = version.BlobHash;
        db.ContentBundleVersions.Remove(version);
        bundle.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await ControllerHelpers.DeleteBlobIfUnreferencedAsync(db, blobs, hash, ct);
        return NoContent();
    }

    /// <summary>Serves one version's zip and counts the download.</summary>
    /// <remarks>The count is bumped with a single UPDATE so concurrent downloads don't lose counts. A
    /// failure there is logged and ignored so the download still succeeds.</remarks>
    [HttpGet("{id:guid}/versions/{versionId:guid}/download")]
    [HttpGet("{id:guid}/files/{versionId:guid}")]
    public async Task<IActionResult> Download(Guid id, Guid versionId, CancellationToken ct)
    {
        var (_, _, err) = await LoadAsync(id, PackPermissions.Download, ct);
        if (err is not null) return err;

        var version = await db.ContentBundleVersions.FirstOrDefaultAsync(v => v.Id == versionId && v.BundleId == id, ct);
        if (version is null) return NotFound();
        if (!blobs.Exists(version.BlobHash)) return NotFound();

        try
        {
            await db.ContentBundles.Where(b => b.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.DownloadCount, b => b.DownloadCount + 1), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Download counter not bumped for bundle {BundleId}", id);
        }

        return File(blobs.OpenRead(version.BlobHash), "application/zip", version.FileName);
    }

    // ── icon ─────────────────────────────────────────────────────────────────

    [HttpPost("{id:guid}/icon")]
    [RequestSizeLimit(ControllerHelpers.MaxIconBytes + (1 << 16))]
    public async Task<IActionResult> UploadIcon(Guid id, [FromForm] IFormFile file, CancellationToken ct)
    {
        var me = this.UserId();
        var bundle = await db.ContentBundles.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (bundle is null) return NotFound();
        if (bundle.OwnerId != me) return Forbid();

        var (hash, failure) = await ControllerHelpers.TryStoreIconAsync(blobs, guard, bundle.OwnerId, me, file, ct);
        if (hash is null) return failure!;

        var previous = bundle.IconBlobHash;
        bundle.IconBlobHash = hash;
        bundle.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        guard.Settle(bundle.OwnerId, hash);

        if (previous is not null && previous != hash)
            await ControllerHelpers.DeleteBlobIfUnreferencedAsync(db, blobs, previous, ct);
        return Ok(new { iconBlobHash = hash });
    }

    /// <summary>Serves the bundle's icon, or 404 when it has none.</summary>
    /// <remarks>Anonymous under the bundle's visibility rule, so an &lt;Image&gt; without an
    /// Authorization header can show a public bundle's icon, as in the other families.</remarks>
    [AllowAnonymous]
    [HttpGet("{id:guid}/icon")]
    public async Task<IActionResult> GetIcon(Guid id, CancellationToken ct)
    {
        var bundle = await db.ContentBundles.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (bundle is null) return NotFound();

        var perms = await resolver.GetAsync(bundle, this.UserIdOrNull(), ct);
        if (!perms.HasFlag(PackPermissions.View)) return NotFound();

        if (string.IsNullOrEmpty(bundle.IconBlobHash) || !blobs.Exists(bundle.IconBlobHash)) return NotFound();
        return File(blobs.OpenRead(bundle.IconBlobHash), ControllerHelpers.IconResponseContentType);
    }

    [HttpDelete("{id:guid}/icon")]
    public async Task<IActionResult> DeleteIcon(Guid id, CancellationToken ct)
    {
        var me = this.UserId();
        var bundle = await db.ContentBundles.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (bundle is null) return NotFound();
        if (bundle.OwnerId != me) return Forbid();

        var previous = bundle.IconBlobHash;
        if (previous is null) return NoContent();

        bundle.IconBlobHash = null;
        bundle.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await ControllerHelpers.DeleteBlobIfUnreferencedAsync(db, blobs, previous, ct);
        return NoContent();
    }

    // ── invitations and share links ──────────────────────────────────────────

    /// <summary>Every invitation ever minted for this bundle: pending, accepted and revoked.</summary>
    /// <remarks>Includes tokens, which are access, so this needs manage-sharing rather than View.</remarks>
    [HttpGet("{id:guid}/invitations")]
    public async Task<ActionResult<IReadOnlyList<BundleInvitationEntry>>> ListInvitations(
        Guid id, CancellationToken ct)
    {
        var (bundle, _, error) = await LoadManageableAsync(id, ct);
        if (error is not null) return error;

        var invites = await db.ContentBundleInvitations
            .AsNoTracking()
            .Include(i => i.InvitedBy)
            .Where(i => i.BundleId == id)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(ct);

        return Ok(invites
            .Select(i => BundleInvitations.ToEntry(i, bundle!.Name, bundle.Kind, includeToken: true))
            .ToList());
    }

    /// <summary>Invites somebody to the bundle by name, or mints a share link when no name is
    /// given.</summary>
    [HttpPost("{id:guid}/invitations")]
    public async Task<ActionResult<BundleInvitationEntry>> CreateInvitation(
        Guid id, [FromBody] CreatePackInvitationRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var (bundle, perms, error) = await LoadManageableAsync(id, ct);
        if (error is not null) return error;

        var grantError = GuardGrant(bundle!, perms, req.Permissions, me);
        if (grantError is not null) return grantError;

        var (expiresAt, expiryError) = ResolveExpiry(req.ExpiresInDays);
        if (expiryError is not null) return BadRequest(new { error = expiryError });
        if (req.Message is { Length: > 512 })
            return BadRequest(new { error = "That message is too long (512 characters maximum)." });

        ContentBundleInvitation invitation;
        if (string.IsNullOrWhiteSpace(req.Username))
        {
            invitation = await MintShareLinkAsync(bundle!, me, req.Permissions, expiresAt, req.Message, ct);
        }
        else
        {
            var user = await ControllerHelpers.FindUserByNameAsync(db, req.Username, ct);
            if (user is null) return BadRequest(new { error = "User not found" });
            if (user.Id == bundle!.OwnerId) return BadRequest(new { error = "Owner is implicit" });

            // One live invitation per person per bundle: re-inviting replaces the offer rather than leaving
            // two tokens with different permissions.
            var now = DateTimeOffset.UtcNow;
            var live = await db.ContentBundleInvitations
                .Where(i => i.BundleId == id && i.InvitedUserId == user.Id
                         && i.RevokedAt == null && i.AcceptedAt == null)
                .ToListAsync(ct);
            foreach (var old in live) old.RevokedAt = now;

            invitation = new ContentBundleInvitation
            {
                BundleId = id,
                InvitedUserId = user.Id,
                InvitedUsername = user.UserName,
                InvitedByUserId = me,
                Permissions = req.Permissions,
                Token = PackSharing.NewShareToken(),
                ExpiresAt = expiresAt,
                Message = req.Message
            };
            db.ContentBundleInvitations.Add(invitation);
            await db.SaveChangesAsync(ct);

            await PackSharing.LogAsync(db, me, ActivityKind.InviteSent, ActivitySubjectType.Bundle,
                bundle.Id, bundle.Name, targetUserId: user.Id, detail: req.Permissions.ToString(), ct: ct);
        }

        await db.Entry(invitation).Reference(i => i.InvitedBy).LoadAsync(ct);
        return Ok(BundleInvitations.ToEntry(invitation, bundle!.Name, bundle.Kind, includeToken: true));
    }

    /// <summary>Withdraws an invitation. Idempotent: revoking a revoked invitation is a no-op.</summary>
    [HttpDelete("{id:guid}/invitations/{invId:guid}")]
    public async Task<IActionResult> RevokeInvitation(Guid id, Guid invId, CancellationToken ct)
    {
        var (bundle, _, error) = await LoadManageableAsync(id, ct);
        if (error is not null) return error;

        var invitation = await db.ContentBundleInvitations
            .FirstOrDefaultAsync(i => i.Id == invId && i.BundleId == id, ct);
        if (invitation is null) return NotFound();
        if (invitation.RevokedAt is not null) return NoContent();

        invitation.RevokedAt = DateTimeOffset.UtcNow;
        BundleInvitations.ClearShareTokenIfPointedAt(bundle!, invitation.Token);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Mints (or re-mints) the bundle's share link.</summary>
    [HttpPost("{id:guid}/share-link")]
    public async Task<ActionResult<ShareLinkInfo>> CreateShareLink(
        Guid id, [FromBody] CreateShareLinkRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var (bundle, perms, error) = await LoadManageableAsync(id, ct);
        if (error is not null) return error;

        var grantError = GuardGrant(bundle!, perms, req.Permissions, me);
        if (grantError is not null) return grantError;

        var (expiresAt, expiryError) = ResolveExpiry(req.ExpiresInDays);
        if (expiryError is not null) return BadRequest(new { error = expiryError });

        var link = await MintShareLinkAsync(bundle!, me, req.Permissions, expiresAt, message: null, ct);
        return Ok(new ShareLinkInfo(link.Token, link.Permissions, link.CreatedAt, link.ExpiresAt));
    }

    /// <summary>Kills the bundle's share link. What people redeemed while it was live stays
    /// granted.</summary>
    [HttpDelete("{id:guid}/share-link")]
    public async Task<IActionResult> RevokeShareLink(Guid id, CancellationToken ct)
    {
        var (bundle, _, error) = await LoadManageableAsync(id, ct);
        if (error is not null) return error;

        var now = DateTimeOffset.UtcNow;
        var links = await db.ContentBundleInvitations
            .Where(i => i.BundleId == id && i.InvitedUserId == null && i.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var link in links) link.RevokedAt = now;

        bundle!.ShareToken = null;
        bundle.ShareTokenCreatedAt = null;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>The longest an invitation or share link may be asked to live for.</summary>
    private const int MaxExpiryDays = 365;

    /// <summary>Turns a requested lifetime in days into an absolute expiry.</summary>
    private static (DateTimeOffset? ExpiresAt, string? Error) ResolveExpiry(int? days)
    {
        if (days is null) return (null, null);
        if (days < 1 || days > MaxExpiryDays)
            return (null, $"An invitation can last between 1 and {MaxExpiryDays} days.");
        return (DateTimeOffset.UtcNow.AddDays(days.Value), null);
    }

    /// <summary>Replaces the bundle's share link with a fresh one and points the bundle at it.</summary>
    /// <remarks>A link is an invitation row with no invited user. <see cref="ContentBundle.ShareToken"/>
    /// only points at the live row, so re-minting revokes the old one first; otherwise revoking the
    /// posted link would leave another one working.</remarks>
    private async Task<ContentBundleInvitation> MintShareLinkAsync(
        ContentBundle bundle, Guid me, PackPermissions permissions,
        DateTimeOffset? expiresAt, string? message, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var live = await db.ContentBundleInvitations
            .Where(i => i.BundleId == bundle.Id && i.InvitedUserId == null && i.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var old in live) old.RevokedAt = now;

        var link = new ContentBundleInvitation
        {
            BundleId = bundle.Id,
            InvitedUserId = null,
            InvitedByUserId = me,
            Permissions = permissions,
            Token = PackSharing.NewShareToken(),
            CreatedAt = now,
            ExpiresAt = expiresAt,
            Message = message
        };
        db.ContentBundleInvitations.Add(link);
        bundle.ShareToken = link.Token;
        bundle.ShareTokenCreatedAt = now;
        await db.SaveChangesAsync(ct);

        await PackSharing.LogAsync(db, me, ActivityKind.InviteSent, ActivitySubjectType.Bundle,
            bundle.Id, bundle.Name, detail: $"link, {permissions}", ct: ct);
        return link;
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Slugs the router would swallow. A bundle called "Browse" still gets a usable URL.</summary>
    private static readonly HashSet<string> ReservedSlugs =
        new(StringComparer.OrdinalIgnoreCase) { "browse", "new", "mine" };

    private async Task<(ContentBundle? bundle, PackPermissions perms, ActionResult? error)> LoadAsync(
        Guid id, PackPermissions required, CancellationToken ct)
    {
        var bundle = await db.ContentBundles.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (bundle is null) return (null, PackPermissions.None, NotFound());
        var perms = await resolver.GetAsync(bundle, this.UserId(), ct);
        if (!perms.HasFlag(required)) return (bundle, perms, Forbid());
        return (bundle, perms, null);
    }

    /// <summary>Loads a bundle for a caller who may change who it is shared with.</summary>
    /// <remarks>The owner always qualifies, and so does anyone holding
    /// <see cref="PackPermissions.ManageCollaborators"/>, as on the other families.</remarks>
    private async Task<(ContentBundle? bundle, PackPermissions perms, ActionResult? error)> LoadManageableAsync(
        Guid id, CancellationToken ct)
    {
        var bundle = await db.ContentBundles.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (bundle is null) return (null, PackPermissions.None, NotFound());
        var me = this.UserId();
        var perms = await resolver.GetAsync(bundle, me, ct);
        if (bundle.OwnerId != me && !perms.HasFlag(PackPermissions.ManageCollaborators))
            return (bundle, perms, Forbid());
        return (bundle, perms, null);
    }

    /// <summary>Checks a permission set somebody is about to hand to somebody else, as a 400.</summary>
    /// <remarks>Uses the shared rules in <see cref="ControllerHelpers.ValidateGrant"/>: known bits, not
    /// empty, View included, and a non-owner manager can't grant more than they hold (otherwise they
    /// could grant UploadShared to a second account of their own).</remarks>
    private ActionResult? GuardGrant(ContentBundle bundle, PackPermissions mine, PackPermissions granting, Guid me) =>
        ControllerHelpers.ValidateGrant(bundle.OwnerId, me, mine, granting) is { } error
            ? BadRequest(new { error })
            : null;

    /// <summary>Reduces a client-supplied download name to a bare file name.</summary>
    private static string SafeDownloadName(string? declared, string fallbackStem)
    {
        var name = string.IsNullOrWhiteSpace(declared) ? "" : declared.Trim();
        // Both separators, because the name came off a Windows client and is read on Linux.
        var cut = name.LastIndexOfAny(['/', '\\']);
        if (cut >= 0) name = name[(cut + 1)..];
        name = new string(name.Where(c => !char.IsControl(c) && c is not (':' or '*' or '?' or '"' or '<' or '>' or '|')).ToArray()).Trim();
        if (name is "" or "." or "..") name = $"{fallbackStem}.zip";
        return name.Length > 255 ? name[..255] : name;
    }

    private async Task<string> GenerateUniqueSlugAsync(BundleKind kind, string name, CancellationToken ct)
    {
        var basic = new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        basic = string.Join('-', basic.Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (basic.Length == 0 || ReservedSlugs.Contains(basic)) basic = $"bundle-{basic}".TrimEnd('-');
        if (basic.Length > 80) basic = basic[..80];

        // Unique per (Kind, Slug), matching the index: "vanilla" may exist once per kind.
        var candidate = basic;
        var n = 1;
        while (await db.ContentBundles.AnyAsync(b => b.Kind == kind && b.Slug == candidate, ct))
            candidate = $"{basic}-{++n}";
        return candidate;
    }

    private static ContentBundleVersionInfo ToVersionInfo(ContentBundleVersion v) => new(
        v.Id, v.VersionString, v.Changelog, v.ReleaseChannel,
        v.BlobHash, v.FileSize, v.FileName,
        v.McVersionsCsv, v.LoadersCsv, v.PublishedAt);

    private static ContentBundleSummary ToSummary(ContentBundle b, PackPermissions perms, int versionCount) => new(
        b.Id, b.Kind, b.Slug, b.Name, b.Summary, b.OwnerId, b.Owner.UserName ?? "",
        b.Visibility, b.IconBlobHash, b.TargetPathRoot, b.McVersionsCsv, b.LoadersCsv,
        b.DownloadCount, b.CreatedAt, b.UpdatedAt, perms, versionCount);

    /// <param name="teams">The bundle's team rows with their rosters, from
    /// <see cref="PackSharing.TeamEntriesAsync"/>. The caller builds it because it needs a query.</param>
    private static ContentBundleDetail ToDetail(
        ContentBundle b, PackPermissions perms, Guid me, List<PackTeamEntry> teams)
    {
        // Anyone who can see the bundle can see who else can, as on instances, so collaborators can find
        // their own row to leave. The share token is access, so only managers get it.
        var canManage = b.OwnerId == me || perms.HasFlag(PackPermissions.ManageCollaborators);
        var collabs = b.Collaborators
            .Select(c => new PackCollaboratorEntry(c.UserId, c.User.UserName ?? "", c.Permissions))
            .ToList();

        var versions = b.Versions
            .OrderByDescending(v => v.PublishedAt)
            .Select(ToVersionInfo)
            .ToList();

        return new ContentBundleDetail(
            b.Id, b.Kind, b.Slug, b.Name, b.Summary, b.Description,
            b.OwnerId, b.Owner.UserName ?? "", b.Visibility, b.IconBlobHash,
            b.TargetPathRoot, b.McVersionsCsv, b.LoadersCsv, b.DownloadCount,
            b.CreatedAt, b.UpdatedAt, perms, versions, collabs, teams,
            canManage ? b.ShareToken : null);
    }
}
