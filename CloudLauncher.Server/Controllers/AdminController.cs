using CloudLauncher.Server.Auth;
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
public class AdminController(
    AppDbContext db,
    UserManager<AppUser> users,
    JwtTokenService tokens,
    ILogger<AdminController> log) : ControllerBase
{
    /// <summary>List all users with their current storage usage and quota.</summary>
    [HttpGet("users")]
    public async Task<ActionResult<IReadOnlyList<UserQuotaInfo>>> ListUsers(CancellationToken ct)
    {
        var allUsers = await db.Users.AsNoTracking().ToListAsync(ct);

        // One grouped query for every owner's usage instead of one SUM per user. Owners with no
        // shared-pack entries are absent from the dictionary and default to 0 below.
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
    /// <remarks>A demotion also signs the user out everywhere, so the admin role in their tokens
    /// lasts no longer than the access token they already hold.</remarks>
    [HttpPatch("users/{userId:guid}/admin")]
    public async Task<IActionResult> SetAdmin(Guid userId, [FromBody] SetAdminRequest req, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null) return NotFound();

        var demoted = user.IsAdmin && !req.IsAdmin;
        user.IsAdmin = req.IsAdmin;
        var result = await users.UpdateAsync(user);
        if (!result.Succeeded)
            return Conflict(new { error = "Could not change the admin role. " + Describe(result) });
        if (demoted)
            await tokens.RevokeAllAsync(user.Id, ct);

        log.LogInformation("Admin {AdminId} set admin to {IsAdmin} for user {UserId}.",
            this.UserId(), req.IsAdmin, userId);
        return NoContent();
    }

    /// <summary>Disables an account: it can no longer sign in or refresh, and every device it is
    /// signed in on is signed out once its current access token expires.</summary>
    [HttpPost("users/{userId:guid}/disable")]
    public async Task<IActionResult> DisableUser(Guid userId, CancellationToken ct)
    {
        if (userId == this.UserId())
            return BadRequest(new { error = "You can't disable your own account." });

        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null) return NotFound();

        // Set directly: Identity's own setter refuses rows whose LockoutEnabled flag is off.
        user.LockoutEnabled = true;
        user.LockoutEnd = AccountStatus.DisabledUntil;
        var result = await users.UpdateAsync(user);
        if (!result.Succeeded)
            return Conflict(new { error = "Could not disable the account. " + Describe(result) });
        await tokens.RevokeAllAsync(user.Id, ct);

        log.LogInformation("Admin {AdminId} disabled user {UserId}.", this.UserId(), userId);
        return NoContent();
    }

    /// <summary>Re-enables a disabled account, and clears any failed-sign-in lockout with it.</summary>
    [HttpPost("users/{userId:guid}/enable")]
    public async Task<IActionResult> EnableUser(Guid userId)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null) return NotFound();

        user.LockoutEnd = null;
        user.AccessFailedCount = 0;
        var result = await users.UpdateAsync(user);
        if (!result.Succeeded)
            return Conflict(new { error = "Could not enable the account. " + Describe(result) });

        log.LogInformation("Admin {AdminId} enabled user {UserId}.", this.UserId(), userId);
        return NoContent();
    }

    private static string Describe(IdentityResult result) =>
        string.Join(" ", result.Errors.Select(e => e.Description));

    /// <summary>Takes an item out of public view by making it private.</summary>
    /// <remarks>Only visibility changes: the owner keeps the item, and people it was shared with
    /// directly keep access, as does anyone who added it to their list while it was public. For packs,
    /// IsShared only controls file sync, so it is left alone.</remarks>
    /// <param name="kind">pack, mod, world, resourcepack or bundle.</param>
    [HttpPost("content/{kind}/{id:guid}/unpublish")]
    public async Task<IActionResult> Unpublish(string kind, Guid id, CancellationToken ct)
    {
        const PackVisibility hidden = PackVisibility.Private;
        var matched = kind.ToLowerInvariant() switch
        {
            "pack" => await db.Packs.Where(x => x.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Visibility, hidden), ct),
            "mod" => await db.Mods.Where(x => x.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Visibility, hidden), ct),
            "world" => await db.SharedWorlds.Where(x => x.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Visibility, hidden), ct),
            "resourcepack" => await db.HostedResourcePacks.Where(x => x.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Visibility, hidden), ct),
            "bundle" => await db.ContentBundles.Where(x => x.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Visibility, hidden), ct),
            _ => 0
        };
        if (matched == 0) return NotFound();

        log.LogInformation("Admin {AdminId} unpublished {Kind} {ItemId}.",
            this.UserId(), kind.ToLowerInvariant(), id);
        return NoContent();
    }
}
