using CloudLauncher.Server.Data;
using CloudLauncher.Shared;
using Microsoft.EntityFrameworkCore;

namespace CloudLauncher.Server.Auth;

public class SharedWorldPermissionResolver(AppDbContext db)
{
    public async Task<PackPermissions> GetAsync(SharedWorld world, Guid? userId, CancellationToken ct = default)
    {
        if (userId is null)
            return world.Visibility == PackVisibility.Public ? PackPermissions.ReadOnly : PackPermissions.None;

        if (world.OwnerId == userId)
            return PackPermissions.Full;

        var perms = PackPermissions.None;
        if (world.Visibility == PackVisibility.Public)
            perms |= PackPermissions.ReadOnly;

        var direct = await db.SharedWorldCollaborators
            .Where(c => c.WorldId == world.Id && c.UserId == userId)
            .Select(c => c.Permissions)
            .FirstOrDefaultAsync(ct);
        perms |= direct;

        var teamPerms = await db.SharedWorldTeams
            .Where(wt => wt.WorldId == world.Id)
            .Where(wt => db.TeamMembers.Any(tm => tm.TeamId == wt.TeamId && tm.UserId == userId))
            .Select(wt => wt.Permissions)
            .ToListAsync(ct);
        foreach (var p in teamPerms) perms |= p;

        // Team visibility, same rule as instances and bundles: anyone on a team the world is shared
        // with may see and download it, whatever that team's own grant says. Only ReadOnly, so a
        // View-only team grant gets nothing beyond downloading.
        if (world.Visibility == PackVisibility.Team && teamPerms.Count > 0)
            perms |= PackPermissions.ReadOnly;

        return perms;
    }
}
