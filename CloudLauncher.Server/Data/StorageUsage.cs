using Microsoft.EntityFrameworkCore;

namespace CloudLauncher.Server.Data;

public static class StorageUsage
{
    /// <summary>
    /// Total bytes used by all shared-pack manifest entries owned by this user.
    /// Uses manifest entry sizes (content-addressed, so this counts logical bytes, not
    /// unique-blob bytes — fair accounting per user regardless of shared blobs).
    /// </summary>
    public static async Task<long> GetUsedBytesAsync(AppDbContext db, Guid userId, CancellationToken ct = default)
    {
        var used = await db.PackManifestEntries
            .Where(e => e.Pack.OwnerId == userId && e.Pack.IsShared)
            .SumAsync(e => e.Size, ct);
        return used;
    }

    /// <summary>
    /// Returns true if uploading <paramref name="incomingBytes"/> would keep the user
    /// within their quota. Always returns true when quota is null (unlimited).
    /// </summary>
    public static async Task<bool> WouldFitAsync(
        AppDbContext db, AppUser user, long incomingBytes, CancellationToken ct = default)
    {
        if (user.StorageQuotaBytes is null) return true;
        var used = await GetUsedBytesAsync(db, user.Id, ct);
        return used + incomingBytes <= user.StorageQuotaBytes.Value;
    }
}
