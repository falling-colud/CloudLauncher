using CloudLauncher.Server.Data;
using CloudLauncher.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CloudLauncher.Server.Controllers;

[ApiController]
[Authorize(Roles = "admin")]
[Route("admin")]
public class AdminController(AppDbContext db, UserManager<AppUser> users) : ControllerBase
{
    /// <summary>List all users with their current storage usage and quota.</summary>
    [HttpGet("users")]
    public async Task<ActionResult<IReadOnlyList<UserQuotaInfo>>> ListUsers(CancellationToken ct)
    {
        var allUsers = await db.Users.AsNoTracking().ToListAsync(ct);

        // Compute every owner's usage in a single grouped query instead of one SUM per user
        // (previously 1 + N queries). Owners with no shared-pack entries simply won't appear
        // in the dictionary and default to 0 below.
        var usageByOwner = await db.PackManifestEntries
            .Where(e => e.Pack.IsShared)
            .GroupBy(e => e.Pack.OwnerId)
            .Select(g => new { OwnerId = g.Key, Used = g.Sum(e => e.Size) })
            .ToDictionaryAsync(x => x.OwnerId, x => x.Used, ct);

        var result = allUsers
            .OrderBy(u => u.UserName)
            .Select(u => new UserQuotaInfo(
                u.Id, u.UserName ?? "", usageByOwner.GetValueOrDefault(u.Id), u.StorageQuotaBytes))
            .ToList();

        return Ok(result);
    }

    /// <summary>Set or remove the storage quota for a user. QuotaBytes = null means unlimited.</summary>
    [HttpPatch("users/{userId:guid}/quota")]
    public async Task<ActionResult<UserQuotaInfo>> SetQuota(
        Guid userId, [FromBody] SetQuotaRequest req, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null) return NotFound();

        if (req.QuotaBytes is not null && req.QuotaBytes < 0)
            return BadRequest(new { error = "Quota cannot be negative." });

        user.StorageQuotaBytes = req.QuotaBytes;
        await users.UpdateAsync(user);

        var used = await StorageUsage.GetUsedBytesAsync(db, userId, ct);
        return Ok(new UserQuotaInfo(user.Id, user.UserName ?? "", used, user.StorageQuotaBytes));
    }

    /// <summary>Promote or demote a user's admin status.</summary>
    [HttpPatch("users/{userId:guid}/admin")]
    public async Task<IActionResult> SetAdmin(Guid userId, [FromBody] SetAdminRequest req)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null) return NotFound();
        user.IsAdmin = req.IsAdmin;
        await users.UpdateAsync(user);
        return NoContent();
    }
}
