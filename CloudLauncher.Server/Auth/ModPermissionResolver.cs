using CloudLauncher.Server.Data;
using CloudLauncher.Shared;
using Microsoft.EntityFrameworkCore;

namespace CloudLauncher.Server.Auth;

public class ModPermissionResolver(AppDbContext db)
{
    public async Task<PackPermissions> GetAsync(Mod mod, Guid? userId, CancellationToken ct = default)
    {
        if (userId is null)
            return mod.Visibility == PackVisibility.Public ? PackPermissions.ReadOnly : PackPermissions.None;

        if (mod.OwnerId == userId)
            return PackPermissions.Full;

        var perms = PackPermissions.None;

        if (mod.Visibility == PackVisibility.Public)
            perms |= PackPermissions.ReadOnly;

        var direct = await db.ModCollaborators
            .Where(c => c.ModId == mod.Id && c.UserId == userId)
            .Select(c => c.Permissions)
            .FirstOrDefaultAsync(ct);
        perms |= direct;

        var teamPerms = await db.ModTeams
            .Where(mt => mt.ModId == mod.Id)
            .Where(mt => db.TeamMembers.Any(tm => tm.TeamId == mt.TeamId && tm.UserId == userId))
            .Select(mt => mt.Permissions)
            .ToListAsync(ct);
        foreach (var p in teamPerms)
            perms |= p;

        return perms;
    }
}
