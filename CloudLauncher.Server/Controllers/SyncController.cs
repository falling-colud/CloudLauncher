using CloudLauncher.Server.Auth;
using CloudLauncher.Server.Data;
using CloudLauncher.Server.Storage;
using CloudLauncher.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
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
    UserManager<AppUser> users) : ControllerBase
{
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
        if (req.BaseVersion != pack.ManifestVersion)
            return Conflict(new { error = "Manifest version mismatch", currentVersion = pack.ManifestVersion });
        if (ValidateEntries(req.Entries) is { } entryErr) return entryErr;

        var hashes = req.Entries.Select(e => e.Hash.ToLowerInvariant()).Distinct().ToList();
        var missing = hashes.Where(h => !blobs.Exists(h)).ToList();
        return Ok(new BeginUploadResponse(Guid.NewGuid(), missing));
    }

    [HttpPut("blob/{hash}")]
    public async Task<IActionResult> UploadBlob(Guid packId, string hash, CancellationToken ct)
    {
        var (pack, perms, err) = await LoadAsync(packId, PackPermissions.UploadShared, ct);
        if (err is not null) return err;

        var expected = hash.ToLowerInvariant();
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit))
            return BadRequest(new { error = "Hash must be 64 lowercase hex chars" });

        // Reject an owner who is already at/over quota BEFORE streaming anything to disk.
        // Without this gate (and previously with [DisableRequestSizeLimit]) an authenticated
        // user could PUT unlimited/arbitrarily-large blobs — never calling commit — and exhaust
        // the server disk shared with Postgres. The 200 MB Kestrel cap now bounds each request.
        var owner = await users.FindByIdAsync(pack!.OwnerId.ToString());
        if (owner is not null && !await StorageUsage.WouldFitAsync(db, owner, 0, ct))
            return BadRequest(new { error = "Storage quota exceeded." });

        var actual = await blobs.StoreAsync(Request.Body, ct);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Uploaded content hash mismatch", expected, actual });

        return Ok(new { hash = actual, size = blobs.SizeOf(actual) });
    }

    [HttpPost("upload/commit")]
    public async Task<ActionResult<CommitUploadResponse>> Commit(
        Guid packId, [FromBody] BeginUploadRequest req, CancellationToken ct)
    {
        var (pack, perms, err) = await LoadAsync(packId, PackPermissions.UploadShared, ct);
        if (err is not null) return err;
        if (!pack!.IsShared) return BadRequest(new { error = "Pack is not shared" });
        if (req.BaseVersion != pack.ManifestVersion)
            return Conflict(new { error = "Manifest version mismatch", currentVersion = pack.ManifestVersion });
        if (ValidateEntries(req.Entries) is { } entryErr) return entryErr;

        // Verify every referenced blob exists and measure its REAL on-disk size. Never trust
        // the client-supplied ManifestEntry.Size: it feeds the quota check and stored usage, so
        // a client could otherwise under-report sizes and store far beyond its quota.
        var sizes = new Dictionary<string, long>();
        foreach (var e in req.Entries)
        {
            var h = e.Hash.ToLowerInvariant();
            if (!blobs.Exists(h))
                return BadRequest(new { error = $"Blob missing: {e.Hash}" });
            sizes[h] = blobs.SizeOf(h);
        }

        // Quota check: new total for this pack = sum of incoming entries (server-measured sizes).
        var owner = await users.FindByIdAsync(pack.OwnerId.ToString());
        if (owner is not null && owner.StorageQuotaBytes is not null)
        {
            // Usage = all OTHER shared packs' manifest sizes + new pack size
            var otherUsed = await db.PackManifestEntries
                .Where(e => e.Pack.OwnerId == pack.OwnerId && e.PackId != packId && e.Pack.IsShared)
                .SumAsync(e => e.Size, ct);
            var newPackSize = req.Entries.Sum(e => sizes[e.Hash.ToLowerInvariant()]);
            if (otherUsed + newPackSize > owner.StorageQuotaBytes.Value)
            {
                var quotaMb = owner.StorageQuotaBytes.Value / (1024 * 1024);
                var usedMb = (otherUsed + newPackSize) / (1024 * 1024);
                return BadRequest(new { error = $"Storage quota exceeded: would use {usedMb} MB of {quotaMb} MB limit." });
            }
        }

        var existing = await db.PackManifestEntries.Where(x => x.PackId == packId).ToListAsync(ct);
        db.PackManifestEntries.RemoveRange(existing);
        foreach (var e in req.Entries)
        {
            var h = e.Hash.ToLowerInvariant();
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
        await db.SaveChangesAsync(ct);
        return Ok(new CommitUploadResponse(pack.ManifestVersion));
    }

    [HttpGet("blob/{hash}")]
    public async Task<IActionResult> DownloadBlob(Guid packId, string hash, CancellationToken ct)
    {
        var (_, perms, err) = await LoadAsync(packId, PackPermissions.Download, ct);
        if (err is not null) return err;

        var safe = hash.ToLowerInvariant();
        if (safe.Length != 64 || !safe.All(Uri.IsHexDigit))
            return BadRequest(new { error = "Hash must be 64 lowercase hex chars" });

        // Only serve blobs that belong to THIS pack's committed manifest. Blobs live in one
        // global content-addressed store, so without this scoping a user with Download on any
        // single pack could read any blob in the system by hash (cross-pack, cross-user).
        var belongs = await db.PackManifestEntries.AnyAsync(e => e.PackId == packId && e.Hash == safe, ct);
        if (!belongs || !blobs.Exists(safe)) return NotFound();

        return File(blobs.OpenRead(safe), "application/octet-stream", enableRangeProcessing: true);
    }

    /// <summary>Maximum manifest entries accepted in a single upload (DoS guard).</summary>
    private const int MaxManifestEntries = 100_000;

    /// <summary>Rejects a manifest whose entries are too many or contain an unsafe relative
    /// path. This is the server-side defense against a poisoned manifest: every subscriber
    /// writes these paths to disk during sync, so a `..`/rooted path would be an arbitrary
    /// file-write (zip-slip) primitive across the user base. Returns an error result to
    /// return to the caller, or null when all entries are safe.</summary>
    private ActionResult? ValidateEntries(IReadOnlyList<ManifestEntry> entries)
    {
        if (entries.Count > MaxManifestEntries)
            return BadRequest(new { error = $"Too many manifest entries (max {MaxManifestEntries})." });
        foreach (var e in entries)
            if (!IsSafeRelativePath(e.RelativePath))
                return BadRequest(new { error = $"Manifest contains an unsafe file path: '{e.RelativePath}'" });
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
