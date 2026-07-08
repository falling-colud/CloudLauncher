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
    [DisableRequestSizeLimit]
    public async Task<IActionResult> UploadBlob(Guid packId, string hash, CancellationToken ct)
    {
        var (_, perms, err) = await LoadAsync(packId, PackPermissions.UploadShared, ct);
        if (err is not null) return err;

        var expected = hash.ToLowerInvariant();
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit))
            return BadRequest(new { error = "Hash must be 64 lowercase hex chars" });

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

        // Quota check: new total for this pack = sum of incoming entries.
        var owner = await users.FindByIdAsync(pack.OwnerId.ToString());
        if (owner is not null && owner.StorageQuotaBytes is not null)
        {
            // Usage = all OTHER shared packs' manifest sizes + new pack size
            var otherUsed = await db.PackManifestEntries
                .Where(e => e.Pack.OwnerId == pack.OwnerId && e.PackId != packId && e.Pack.IsShared)
                .SumAsync(e => e.Size, ct);
            var newPackSize = req.Entries.Sum(e => e.Size);
            if (otherUsed + newPackSize > owner.StorageQuotaBytes.Value)
            {
                var quotaMb = owner.StorageQuotaBytes.Value / (1024 * 1024);
                var usedMb = (otherUsed + newPackSize) / (1024 * 1024);
                return BadRequest(new { error = $"Storage quota exceeded: would use {usedMb} MB of {quotaMb} MB limit." });
            }
        }

        foreach (var e in req.Entries)
        {
            if (!blobs.Exists(e.Hash))
                return BadRequest(new { error = $"Blob missing: {e.Hash}" });
        }

        var existing = await db.PackManifestEntries.Where(x => x.PackId == packId).ToListAsync(ct);
        db.PackManifestEntries.RemoveRange(existing);
        foreach (var e in req.Entries)
        {
            db.PackManifestEntries.Add(new PackManifestEntry
            {
                PackId = packId,
                RelativePath = e.RelativePath,
                Hash = e.Hash.ToLowerInvariant(),
                Size = e.Size
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
        if (!blobs.Exists(safe)) return NotFound();

        await Task.Yield();
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
        if (Path.IsPathRooted(rel)) return false;
        if (rel.Contains(':')) return false; // drive letter / NTFS alternate data stream
        foreach (var segment in rel.Split('/', '\\'))
            if (segment == "..") return false;
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
