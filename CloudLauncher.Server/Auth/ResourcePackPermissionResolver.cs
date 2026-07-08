using CloudLauncher.Server.Data;
using CloudLauncher.Shared;
using Microsoft.EntityFrameworkCore;

namespace CloudLauncher.Server.Auth;

public class ResourcePackPermissionResolver(AppDbContext db)
{
    public async Task<PackPermissions> GetAsync(HostedResourcePack pack, Guid? userId, CancellationToken ct = default)
    {
        if (userId is null)
            return pack.Visibility == PackVisibility.Public ? PackPermissions.ReadOnly : PackPermissions.None;

        if (pack.OwnerId == userId)
            return PackPermissions.Full;

        var perms = PackPermissions.None;

        if (pack.Visibility == PackVisibility.Public)
            perms |= PackPermissions.ReadOnly;

        var direct = await db.HostedResourcePackCollaborators
            .Where(c => c.ResourcePackId == pack.Id && c.UserId == userId)
            .Select(c => c.Permissions)
            .FirstOrDefaultAsync(ct);
        perms |= direct;

        var teamPerms = await db.HostedResourcePackTeams
            .Where(mt => mt.ResourcePackId == pack.Id)
            .Where(mt => db.TeamMembers.Any(tm => tm.TeamId == mt.TeamId && tm.UserId == userId))
            .Select(mt => mt.Permissions)
            .ToListAsync(ct);
        foreach (var p in teamPerms)
            perms |= p;

        return perms;
    }
}
