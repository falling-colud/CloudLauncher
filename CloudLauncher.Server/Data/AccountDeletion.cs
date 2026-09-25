using CloudLauncher.Server.Controllers;
using CloudLauncher.Server.Storage;
using CloudLauncher.Shared;
using Microsoft.EntityFrameworkCore;

namespace CloudLauncher.Server.Data;

/// <summary>What one account deletion removed, for the log.</summary>
public sealed record AccountDeletionResult(
    int Packs, int Mods, int Worlds, int ResourcePacks, int Bundles,
    int TeamsDeleted, int TeamsHandedOver, int BlobsFreed);

/// <summary>Deletes an account and everything it owns.</summary>
/// <remarks>
/// <para>The database part is one transaction, mostly done by the schema's cascades. Activity lines
/// that name the user as the target stay in other people's feeds with the name cleared. Owned teams
/// pass to their longest-standing admin, or are deleted when they have none.</para>
/// <para>Files are freed after the commit. Blobs are shared by content, so each one goes through the
/// same "still referenced?" check the delete routes use.</para>
/// </remarks>
public static class AccountDeletion
{
    private const int MaxTeamNameLength = 64;   // Teams.Name varchar(64)

    /// <returns>What was removed, or null when the account no longer exists.</returns>
    public static async Task<AccountDeletionResult?> DeleteAsync(
        AppDbContext db, BlobStore blobs, Guid userId, ILogger log, CancellationToken ct = default)
    {
        var hashes = new List<string>();
        AccountDeletionResult? result = null;

        // The retry strategy reruns the whole unit after a transient failure, so each attempt clears the
        // change tracker and the collected state first.
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            hashes.Clear();
            result = null;

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (!await db.Users.AnyAsync(u => u.Id == userId, ct)) return;

            hashes.AddRange(await OwnedBlobHashesAsync(db, userId, ct));
            var (teamsDeleted, teamsHandedOver) = await ResolveOwnedTeamsAsync(db, userId, ct);
            await ClearShareLinkPointersAsync(db, userId, ct);

            var packs = await db.Packs.Where(p => p.OwnerId == userId).ExecuteDeleteAsync(ct);
            var mods = await db.Mods.Where(m => m.OwnerId == userId).ExecuteDeleteAsync(ct);
            var worlds = await db.SharedWorlds.Where(w => w.OwnerId == userId).ExecuteDeleteAsync(ct);
            var resourcePacks = await db.HostedResourcePacks.Where(r => r.OwnerId == userId).ExecuteDeleteAsync(ct);
            var bundles = await db.ContentBundles.Where(b => b.OwnerId == userId).ExecuteDeleteAsync(ct);
            await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync(ct);

            await tx.CommitAsync(ct);
            result = new AccountDeletionResult(
                packs, mods, worlds, resourcePacks, bundles, teamsDeleted, teamsHandedOver, 0);
        });

        if (result is null) return null;

        result = result with { BlobsFreed = await FreeBlobsAsync(db, blobs, hashes, log) };
        log.LogInformation(
            "Deleted an account: {Packs} packs, {Mods} mods, {Worlds} worlds, {ResourcePacks} resource packs, " +
            "{Bundles} bundles, {TeamsDeleted} teams deleted, {TeamsHandedOver} teams handed over, {BlobsFreed} files freed.",
            result.Packs, result.Mods, result.Worlds, result.ResourcePacks, result.Bundles,
            result.TeamsDeleted, result.TeamsHandedOver, result.BlobsFreed);
        return result;
    }

    /// <summary>Every blob the user's own content keeps alive: pack files, version files and icons.</summary>
    private static async Task<List<string>> OwnedBlobHashesAsync(AppDbContext db, Guid userId, CancellationToken ct)
    {
        var all = new HashSet<string>(StringComparer.Ordinal);
        all.UnionWith(await db.PackManifestEntries.Where(e => e.Pack.OwnerId == userId)
            .Select(e => e.Hash).Distinct().ToListAsync(ct));
        all.UnionWith(await db.ModVersions.Where(v => v.Mod.OwnerId == userId)
            .Select(v => v.BlobHash).Distinct().ToListAsync(ct));
        all.UnionWith(await db.SharedWorldVersions.Where(v => v.World.OwnerId == userId)
            .Select(v => v.BlobHash).Distinct().ToListAsync(ct));
        all.UnionWith(await db.HostedResourcePackVersions.Where(v => v.ResourcePack.OwnerId == userId)
            .Select(v => v.BlobHash).Distinct().ToListAsync(ct));
        all.UnionWith(await db.ContentBundleVersions.Where(v => v.Bundle.OwnerId == userId)
            .Select(v => v.BlobHash).Distinct().ToListAsync(ct));
        all.UnionWith(await db.Mods.Where(m => m.OwnerId == userId && m.IconBlobHash != null)
            .Select(m => m.IconBlobHash!).ToListAsync(ct));
        all.UnionWith(await db.SharedWorlds.Where(w => w.OwnerId == userId && w.IconBlobHash != null)
            .Select(w => w.IconBlobHash!).ToListAsync(ct));
        all.UnionWith(await db.HostedResourcePacks.Where(r => r.OwnerId == userId && r.IconBlobHash != null)
            .Select(r => r.IconBlobHash!).ToListAsync(ct));
        all.UnionWith(await db.ContentBundles.Where(b => b.OwnerId == userId && b.IconBlobHash != null)
            .Select(b => b.IconBlobHash!).ToListAsync(ct));
        return all.ToList();
    }

    /// <summary>Hands each team the user owns to its longest-standing admin, or deletes it when it
    /// has none.</summary>
    private static async Task<(int Deleted, int HandedOver)> ResolveOwnedTeamsAsync(
        AppDbContext db, Guid userId, CancellationToken ct)
    {
        var teams = await db.Teams.Where(t => t.OwnerId == userId)
            .Select(t => new { t.Id, t.Name })
            .ToListAsync(ct);

        int deleted = 0, handedOver = 0;
        foreach (var team in teams)
        {
            // A stored Owner role on another member counts as admin, as in the teams routes.
            var successor = await db.TeamMembers
                .Where(tm => tm.TeamId == team.Id && tm.UserId != userId && tm.Role != TeamRole.Member)
                .OrderBy(tm => tm.JoinedAt)
                .Select(tm => (Guid?)tm.UserId)
                .FirstOrDefaultAsync(ct);

            if (successor is { } heir)
            {
                var name = await FreeTeamNameAsync(db, heir, team.Name, ct);
                await db.Teams.Where(t => t.Id == team.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.OwnerId, heir).SetProperty(t => t.Name, name), ct);
                await db.TeamMembers.Where(tm => tm.TeamId == team.Id && tm.UserId == heir)
                    .ExecuteUpdateAsync(s => s.SetProperty(tm => tm.Role, TeamRole.Owner), ct);
                handedOver++;
                continue;
            }

            // Packs shared with this team show up in the members' libraries. Remove them where the team was
            // the only way in, as removing the grant would.
            var grantedPacks = await db.PackTeams
                .Where(pt => pt.TeamId == team.Id && pt.Pack.OwnerId != userId)
                .Select(pt => pt.PackId)
                .ToListAsync(ct);
            foreach (var packId in grantedPacks)
                await PackSharing.RemoveTeamListingsAsync(db, packId, team.Id, ct);

            deleted += await db.Teams.Where(t => t.Id == team.Id).ExecuteDeleteAsync(ct);
        }
        return (deleted, handedOver);
    }

    /// <summary>The team's name, or a numbered variant when the new owner already has a team called
    /// that. Names are unique per owner.</summary>
    private static async Task<string> FreeTeamNameAsync(AppDbContext db, Guid ownerId, string name, CancellationToken ct)
    {
        if (!await db.Teams.AnyAsync(t => t.OwnerId == ownerId && t.Name == name, ct))
            return name;

        for (var n = 2; n < 100; n++)
        {
            var suffix = $" ({n})";
            var stem = name.Length + suffix.Length > MaxTeamNameLength
                ? name[..(MaxTeamNameLength - suffix.Length)]
                : name;
            var candidate = stem + suffix;
            if (!await db.Teams.AnyAsync(t => t.OwnerId == ownerId && t.Name == candidate, ct))
                return candidate;
        }

        var fallback = $" ({Guid.NewGuid().ToString("N")[..8]})";
        return (name.Length + fallback.Length > MaxTeamNameLength
            ? name[..(MaxTeamNameLength - fallback.Length)]
            : name) + fallback;
    }

    /// <summary>Clears the share-link pointer on other people's packs and bundles whose live link the
    /// user created. The link's invitation row is deleted with the user, so the link would be dead.</summary>
    private static async Task ClearShareLinkPointersAsync(AppDbContext db, Guid userId, CancellationToken ct)
    {
        await db.Packs
            .Where(p => p.OwnerId != userId && p.ShareToken != null
                        && db.PackInvitations.Any(i => i.Token == p.ShareToken && i.InvitedByUserId == userId))
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.ShareToken, (string?)null)
                .SetProperty(p => p.ShareTokenCreatedAt, (DateTimeOffset?)null), ct);

        await db.ContentBundles
            .Where(b => b.OwnerId != userId && b.ShareToken != null
                        && db.ContentBundleInvitations.Any(i => i.Token == b.ShareToken && i.InvitedByUserId == userId))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.ShareToken, (string?)null)
                .SetProperty(b => b.ShareTokenCreatedAt, (DateTimeOffset?)null), ct);
    }

    /// <summary>Deletes the files nothing references any more. Returns how many went.</summary>
    /// <remarks>The account is already gone, so failures are only logged (worst case: files left on
    /// disk). Not cancellable, so a client disconnecting can't stop it halfway.</remarks>
    private static async Task<int> FreeBlobsAsync(AppDbContext db, BlobStore blobs, List<string> hashes, ILogger log)
    {
        var freed = 0;
        try
        {
            db.ChangeTracker.Clear();
            foreach (var hash in hashes)
            {
                var existed = BlobExists(blobs, hash);
                await ControllerHelpers.DeleteBlobIfUnreferencedAsync(db, blobs, hash, CancellationToken.None);
                if (existed && !BlobExists(blobs, hash)) freed++;
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Account deleted, but freeing its files stopped early after {Freed} of {Total}.",
                freed, hashes.Count);
        }
        return freed;
    }

    private static bool BlobExists(BlobStore blobs, string hash)
    {
        try { return blobs.Exists(hash); }
        catch (ArgumentException) { return false; }
    }
}
