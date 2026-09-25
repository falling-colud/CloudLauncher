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
[Route("packs/{packId:guid}/sync")]
public class SyncController(
    AppDbContext db,
    PackPermissionResolver resolver,
    BlobStore blobs,
    UploadGuard guard,
    PendingUploadLedger pending,
    ILogger<SyncController> logger) : ControllerBase
{
    /// <summary>The largest single blob a PUT may carry, stated here rather than left to the
    /// server-wide limit.</summary>
    private const long MaxBlobBytes = 200L * 1024 * 1024;

    [HttpGet("manifest")]
    public async Task<ActionResult<PackManifest>> GetManifest(Guid packId, CancellationToken ct)
    {
        var (pack, perms, err) = await LoadAsync(packId, PackPermissions.View, ct);
        if (err is not null) return err;

        var entries = await db.PackManifestEntries
            .Where(e => e.PackId == packId)
            .Select(e => new ManifestEntry(e.RelativePath, e.Hash, e.Size))
            .ToListAsync(ct);
        return Ok(new PackManifest(pack!.Id, pack.ManifestVersion, pack.UpdatedAt, entries));
    }

    [HttpPost("upload/begin")]
    public async Task<ActionResult<BeginUploadResponse>> BeginUpload(
        Guid packId, [FromBody] BeginUploadRequest req, CancellationToken ct)
    {
        var (pack, perms, err) = await LoadAsync(packId, PackPermissions.UploadShared, ct);
        if (err is not null) return err;
        if (!pack!.IsShared) return BadRequest(new { error = "Pack is not shared" });
        if (ValidateEntries(req.Entries) is { } entryErr) return entryErr;
        if (req.BaseVersion != pack.ManifestVersion)
            return Conflict(await BuildConflictAsync(pack, req, ct));

        var hashes = req.Entries.Select(e => e.Hash.ToLowerInvariant()).Distinct().ToList();
        var missing = new List<string>();
        var present = new List<string>();
        foreach (var h in hashes) (blobs.Exists(h) ? present : missing).Add(h);

        // Blobs already stored but not yet named by this pack may be about to be collected as
        // unreferenced. Refreshing them restarts the grace period until the commit claims them.
        if (present.Count > 0)
        {
            var named = (await db.PackManifestEntries
                    .Where(e => e.PackId == packId)
                    .Select(e => e.Hash)
                    .ToListAsync(ct))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var h in present)
                if (!named.Contains(h)) blobs.Touch(h);
        }

        return Ok(new BeginUploadResponse(Guid.NewGuid(), missing));
    }

    [HttpPut("blob/{hash}")]
    [RequestSizeLimit(MaxBlobBytes)]
    public async Task<IActionResult> UploadBlob(Guid packId, string hash, CancellationToken ct)
    {
        var (pack, perms, err) = await LoadAsync(packId, PackPermissions.UploadShared, ct);
        if (err is not null) return err;

        var expected = hash.ToLowerInvariant();
        if (!BlobStore.IsValidHash(expected))
            return BadRequest(new { error = "Hash must be 64 lowercase hex chars" });

        // Refused on the declared size before anything is streamed to disk: the disk floor, and the pack
        // owner's quota counting everything they store plus uploads still waiting for a commit. Without a
        // Content-Length only an owner who is already full is caught here; the measured check below
        // catches the rest.
        var me = this.UserId();
        var declared = Request.ContentLength ?? 0;
        if (await guard.RefuseAsync(pack!.OwnerId, me, declared, expected, ct) is { } refusal)
            return refusal;

        var stored = await blobs.PutAsync(Request.Body, ct);
        if (stored.Hash != expected)
        {
            await guard.DiscardAsync(stored);
            return BadRequest(new { error = "Uploaded content hash mismatch", expected, actual = stored.Hash });
        }

        // Charged to the owner as pending until the commit naming it lands. If it no longer fits, the new
        // file is removed again unless something else needs it.
        if (await guard.ChargeAsync(pack.OwnerId, me, stored, ct) is { } overQuota)
            return overQuota;

        return Ok(new { hash = stored.Hash, size = stored.Size });
    }

    [HttpPost("upload/commit")]
    public async Task<ActionResult<CommitUploadResponse>> Commit(
        Guid packId, [FromBody] BeginUploadRequest req, CancellationToken ct)
    {
        var (pack, perms, err) = await LoadAsync(packId, PackPermissions.UploadShared, ct);
        if (err is not null) return err;
        if (!pack!.IsShared) return BadRequest(new { error = "Pack is not shared" });
        if (ValidateEntries(req.Entries) is { } entryErr) return entryErr;
        if (req.BaseVersion != pack.ManifestVersion)
            return Conflict(await BuildConflictAsync(pack, req, ct));

        var existing = await db.PackManifestEntries.Where(x => x.PackId == packId).ToListAsync(ct);
        // Captured before the rows are replaced, for the set difference below.
        var previousHashes = existing.Select(x => x.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Verify every referenced blob exists and measure its real size on disk. The
        // client-supplied ManifestEntry.Size is not trusted, since it feeds the quota. Blobs this
        // pack does not name yet get their write time refreshed so maintenance cannot collect them
        // before the save below.
        var sizes = new Dictionary<string, long>();
        foreach (var e in req.Entries)
        {
            var h = e.Hash.ToLowerInvariant();
            if (sizes.ContainsKey(h)) continue;
            var present = previousHashes.Contains(h) ? blobs.Exists(h) : blobs.Touch(h);
            if (!present)
                return BadRequest(new { error = $"Blob missing: {e.Hash}" });
            sizes[h] = blobs.SizeOf(h);
        }

        // Only what this manifest adds to the owner's storage counts, each blob once. A commit
        // that adds nothing always goes through, so an owner over quota can still sync their
        // usage back down.
        var me = this.UserId();
        if (await guard.RefuseCommitAsync(pack.OwnerId, me, packId, sizes, ct) is { } refusal)
            return refusal;

        db.PackManifestEntries.RemoveRange(existing);
        var newHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in req.Entries)
        {
            var h = e.Hash.ToLowerInvariant();
            newHashes.Add(h);
            db.PackManifestEntries.Add(new PackManifestEntry
            {
                PackId = packId,
                RelativePath = e.RelativePath,
                Hash = h,
                Size = sizes[h]
            });
        }
        pack.ManifestVersion += 1;
        pack.UpdatedAt = DateTimeOffset.UtcNow;

        // Who last uploaded to this pack, as opposed to who last renamed it. Only set here,
        // never by a PATCH.
        pack.LastUploadedById = me;
        pack.LastUploadedAt = pack.UpdatedAt;

        // Recorded rather than written: it commits in the same transaction as the manifest it
        // describes, so the feed cannot claim an upload that then failed.
        ActivityLog.Record(db, me, ActivityKind.Uploaded, ActivitySubjectType.Pack,
            pack.Id, pack.Name, detail: $"{req.Entries.Count} files");

        await db.SaveChangesAsync(ct);

        // The manifest now charges these blobs, so they stop counting as pending uploads.
        guard.Settle(pack.OwnerId, newHashes);

        // Only after the save: DeleteBlobIfUnreferencedAsync asks the database, which still sees
        // the old rows until then.
        previousHashes.ExceptWith(newHashes);
        await CollectOrphanedBlobsAsync(packId, previousHashes);

        return Ok(new CommitUploadResponse(pack.ManifestVersion));
    }

    /// <summary>How many blobs one commit tries to delete; the rest wait for the next.</summary>
    /// <remarks>
    /// Bounds the request time when a commit orphans a huge manifest. Leftovers stay orphaned and
    /// are picked up by the next commit to this pack, or by deleting any row naming them.
    /// </remarks>
    private const int MaxBlobsCollectedPerCommit = 1024;

    /// <summary>How many hashes go into one "is anything still pointing at these?" query.</summary>
    private const int ReferenceProbeBatch = 5000;

    /// <summary>
    /// Deletes the blobs a commit just orphaned. Best effort, and never fails the commit.
    /// </summary>
    /// <remarks>
    /// <para>Without this, every commit leaves its old blobs on disk forever, and a pack synced
    /// daily turns over its whole manifest within a month.</para>
    /// <para>Blobs are content-addressed and shared across packs, mods, worlds and resource packs,
    /// so the decision is always <see cref="ControllerHelpers.DeleteBlobIfUnreferencedAsync"/>,
    /// which checks every table that can hold a hash. Stage one here is only a fast pre-filter over
    /// <c>PackManifestEntries</c>; it can only drop candidates, never make one deletable.</para>
    /// <para>Runs on <see cref="CancellationToken.None"/>: the commit is already saved, and a
    /// client hanging up must not leak blobs.</para>
    /// </remarks>
    private async Task CollectOrphanedBlobsAsync(Guid packId, IReadOnlyCollection<string> candidates)
    {
        if (candidates.Count == 0) return;
        var ct = CancellationToken.None;

        var freed = 0;
        var considered = 0;
        try
        {
            // Stage 1: one query per batch instead of per hash. Anything another manifest row still
            // names is dropped here.
            var unclaimed = new List<string>();
            foreach (var chunk in candidates.Chunk(ReferenceProbeBatch))
            {
                var stillNamed = await db.PackManifestEntries
                    .Where(e => chunk.Contains(e.Hash))
                    .Select(e => e.Hash)
                    .Distinct()
                    .ToListAsync(ct);
                var named = stillNamed.ToHashSet(StringComparer.OrdinalIgnoreCase);
                unclaimed.AddRange(chunk.Where(h => !named.Contains(h)));
            }

            // Stage 2: the real decision, per hash, across every table that can hold one.
            foreach (var hash in unclaimed)
            {
                if (considered >= MaxBlobsCollectedPerCommit) break;
                considered++;
                // Somebody's upload of the same bytes is waiting for its own commit, which needs the file.
                if (pending.IsPendingAnywhere(hash)) continue;
                try
                {
                    await ControllerHelpers.DeleteBlobIfUnreferencedAsync(db, blobs, hash, ct);
                    if (!blobs.Exists(hash)) freed++;
                }
                catch (Exception ex)
                {
                    // One locked blob (an in-flight download, say) should not stop the rest.
                    logger.LogDebug(ex, "Blob {Hash} could not be collected", hash);
                }
            }

            var deferred = unclaimed.Count - considered;
            if (freed > 0 || deferred > 0)
                logger.LogInformation(
                    "Sync GC for pack {PackId}: {Candidates} dropped from the manifest, {Freed} blobs deleted, {Deferred} left for a later commit",
                    packId, candidates.Count, freed, deferred);
        }
        catch (Exception ex)
        {
            // The commit succeeded. A failure to tidy up after it is a log line, not a 500.
            logger.LogWarning(ex, "Sync GC failed for pack {PackId} after {Freed} blobs", packId, freed);
        }
    }

    /// <summary>How many differing paths a 409 body will name before it gives up listing them.</summary>
    private const int MaxConflictPaths = 200;

    /// <summary>
    /// Builds the body of a 409: what version the server is on, who put it there, and which paths
    /// differ from the manifest that was just refused.
    /// </summary>
    /// <remarks>
    /// Keeps the <c>error</c> and <c>currentVersion</c> fields older clients read. The diff is
    /// against the server's current manifest, since the client's base version is not stored: it
    /// shows what the upload would overwrite.
    /// </remarks>
    private async Task<ManifestConflict> BuildConflictAsync(Pack pack, BeginUploadRequest req, CancellationToken ct)
    {
        var serverEntries = await db.PackManifestEntries
            .Where(e => e.PackId == pack.Id)
            .Select(e => new { e.RelativePath, e.Hash })
            .ToListAsync(ct);

        var lastUploadedBy = pack.LastUploadedById is null
            ? null
            : await db.Users.Where(u => u.Id == pack.LastUploadedById).Select(u => u.UserName).FirstOrDefaultAsync(ct);

        var server = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in serverEntries) server[e.RelativePath] = e.Hash;
        var uploaded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in req.Entries) uploaded[e.RelativePath] = e.Hash.ToLowerInvariant();

        var paths = new List<ManifestConflictPath>();
        var total = 0;
        foreach (var (path, hash) in server)
        {
            ManifestConflictChange? change =
                !uploaded.TryGetValue(path, out var mine) ? ManifestConflictChange.OnlyOnServer
                : !string.Equals(mine, hash, StringComparison.OrdinalIgnoreCase) ? ManifestConflictChange.HashDiffers
                : null;
            if (change is null) continue;
            total++;
            if (paths.Count < MaxConflictPaths) paths.Add(new ManifestConflictPath(path, change.Value));
        }
        foreach (var path in uploaded.Keys)
        {
            if (server.ContainsKey(path)) continue;
            total++;
            if (paths.Count < MaxConflictPaths) paths.Add(new ManifestConflictPath(path, ManifestConflictChange.OnlyInUpload));
        }

        return new ManifestConflict(
            "Manifest version mismatch",
            pack.Id,
            pack.ManifestVersion,
            req.BaseVersion,
            pack.UpdatedAt,
            pack.LastUploadedById,
            lastUploadedBy,
            pack.LastUploadedAt,
            paths,
            total,
            total > paths.Count);
    }

    [HttpGet("blob/{hash}")]
    public async Task<IActionResult> DownloadBlob(Guid packId, string hash, CancellationToken ct)
    {
        var (_, perms, err) = await LoadAsync(packId, PackPermissions.Download, ct);
        if (err is not null) return err;

        var safe = hash.ToLowerInvariant();
        if (safe.Length != 64 || !safe.All(Uri.IsHexDigit))
            return BadRequest(new { error = "Hash must be 64 lowercase hex chars" });

        // Only serve blobs in this pack's committed manifest. The blob store is global and
        // content-addressed, so otherwise Download on any pack would let a user read any blob by hash.
        var belongs = await db.PackManifestEntries.AnyAsync(e => e.PackId == packId && e.Hash == safe, ct);
        if (!belongs || !blobs.Exists(safe)) return NotFound();

        return File(blobs.OpenRead(safe), "application/octet-stream", enableRangeProcessing: true);
    }

    /// <summary>Maximum manifest entries accepted in a single upload (DoS guard).</summary>
    private const int MaxManifestEntries = 100_000;

    /// <summary>Rejects a manifest with too many entries or an unsafe relative path. Returns the
    /// error result, or null when all entries are safe.</summary>
    /// <remarks>Every subscriber writes these paths to disk, so a <c>..</c> or rooted path would allow
    /// arbitrary file writes (zip-slip). Hashes are checked too, before they become blob-store paths or
    /// query parameters.</remarks>
    private ActionResult? ValidateEntries(IReadOnlyList<ManifestEntry>? entries)
    {
        if (entries is null)
            return BadRequest(new { error = "The manifest has no entries list." });
        if (entries.Count > MaxManifestEntries)
            return BadRequest(new { error = $"Too many manifest entries (max {MaxManifestEntries})." });
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (e is null)
                return BadRequest(new { error = "The manifest contains an empty entry." });
            if (!IsSafeRelativePath(e.RelativePath))
                return BadRequest(new { error = $"Manifest contains an unsafe file path: '{e.RelativePath}'" });
            if (!BlobStore.IsValidHash(e.Hash))
                return BadRequest(new { error = $"Manifest entry '{e.RelativePath}' does not have a valid SHA-256 hash." });
            // Reject paths that collide case-insensitively: they are legal on the Linux server but
            // a duplicate key on a Windows subscriber, where they crash sync for everyone.
            if (!seen.Add(e.RelativePath))
                return BadRequest(new { error = $"Manifest contains a duplicate (case-insensitive) path: '{e.RelativePath}'" });
        }
        return null;
    }

    private static bool IsSafeRelativePath(string? rel)
    {
        if (string.IsNullOrWhiteSpace(rel)) return false;
        if (rel.Length > 1024) return false;
        // Manifests always use '/' as the separator (the client normalises with Replace('\\','/')).
        // A backslash reaching here is therefore hostile: on the Windows clients that consume these
        // paths a leading '\' or "\\server\share" is rooted/UNC, but Path.IsPathRooted misses that
        // on the Linux server, so screen backslashes and colons explicitly and independently of OS.
        if (rel.Contains('\\')) return false;
        if (rel.Contains(':')) return false; // drive letter / NTFS alternate data stream
        if (rel[0] == '/') return false;     // absolute
        if (Path.IsPathRooted(rel)) return false;
        foreach (var segment in rel.Split('/'))
            if (segment is "" or "." or "..") return false; // empty (e.g. "a//b"), current, or parent
        return true;
    }

    private async Task<(Pack? pack, PackPermissions perms, ActionResult? error)> LoadAsync(
        Guid packId, PackPermissions required, CancellationToken ct)
    {
        var pack = await db.Packs.FirstOrDefaultAsync(p => p.Id == packId, ct);
        if (pack is null) return (null, PackPermissions.None, NotFound());
        var perms = await resolver.GetAsync(pack, this.UserId(), ct);
        if (!perms.HasFlag(required)) return (pack, perms, Forbid());
        return (pack, perms, null);
    }
}
