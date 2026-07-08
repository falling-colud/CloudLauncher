using CloudLauncher.Server.Data;
using CloudLauncher.Shared;
using Microsoft.EntityFrameworkCore;

namespace CloudLauncher.Server.Auth;

public class PackPermissionResolver(AppDbContext db)
{
    public async Task<PackPermissions> GetAsync(Pack pack, Guid? userId, CancellationToken ct = default)
    {
        if (userId is null)
            return pack.Visibility == PackVisibility.Public ? PackPermissions.ReadOnly : PackPermissions.None;

        if (pack.OwnerId == userId)
            return PackPermissions.Full;

        var perms = PackPermissions.None;

        if (pack.Visibility == PackVisibility.Public)
            perms |= PackPermissions.ReadOnly;

        var direct = await db.PackCollaborators
            .Where(c => c.PackId == pack.Id && c.UserId == userId)
            .Select(c => c.Permissions)
            .FirstOrDefaultAsync(ct);
        perms |= direct;

        var teamPerms = await db.PackTeams
            .Where(pt => pt.PackId == pack.Id)
            .Where(pt => db.TeamMembers.Any(tm => tm.TeamId == pt.TeamId && tm.UserId == userId))
            .Select(pt => pt.Permissions)
            .ToListAsync(ct);
        foreach (var p in teamPerms)
            perms |= p;

        return perms;
    }
}
