using CloudLauncher.Server.Data;
using CloudLauncher.Shared;
using Microsoft.EntityFrameworkCore;

namespace CloudLauncher.Server.Auth;

/// <summary>What one user may do with one content bundle.</summary>
/// <remarks>Same rules as <see cref="ResourcePackPermissionResolver"/> (owner is Full, Public grants
/// ReadOnly, direct and team grants are OR'd on top), and <see cref="PackVisibility.Team"/> is
/// honoured here as well.</remarks>
public class ContentBundlePermissionResolver(AppDbContext db)
{
    public async Task<PackPermissions> GetAsync(ContentBundle bundle, Guid? userId, CancellationToken ct = default)
    {
        if (userId is null)
            return bundle.Visibility == PackVisibility.Public ? PackPermissions.ReadOnly : PackPermissions.None;

        if (bundle.OwnerId == userId)
            return PackPermissions.Full;

        var perms = PackPermissions.None;

        if (bundle.Visibility == PackVisibility.Public)
            perms |= PackPermissions.ReadOnly;

        var direct = await db.ContentBundleCollaborators
            .Where(c => c.BundleId == bundle.Id && c.UserId == userId)
            .Select(c => c.Permissions)
            .FirstOrDefaultAsync(ct);
        perms |= direct;

        var teamPerms = await db.ContentBundleTeams
            .Where(bt => bt.BundleId == bundle.Id)
            .Where(bt => db.TeamMembers.Any(tm => tm.TeamId == bt.TeamId && tm.UserId == userId))
            .Select(bt => bt.Permissions)
            .ToListAsync(ct);
        foreach (var p in teamPerms)
            perms |= p;

        // Team visibility means "the teams this is shared with can see it", so a member of any team on
        // the bundle gets at least ReadOnly. The team row may grant more, and an explicit grant of less
        // doesn't take viewing away from someone the owner shared the bundle with.
        if (bundle.Visibility == PackVisibility.Team
            && await db.ContentBundleTeams.AnyAsync(
                   bt => bt.BundleId == bundle.Id
                         && db.TeamMembers.Any(tm => tm.TeamId == bt.TeamId && tm.UserId == userId), ct))
            perms |= PackPermissions.ReadOnly;

        return perms;
    }
}
