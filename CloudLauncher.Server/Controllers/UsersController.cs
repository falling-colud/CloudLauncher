using CloudLauncher.Server.Data;
using CloudLauncher.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace CloudLauncher.Server.Controllers;

/// <summary>The autocomplete behind "who do you want to share this with?".</summary>
/// <remarks>Kept minimal so it doesn't turn into a directory of every account.</remarks>
[ApiController]
[Authorize]
[Route("users")]
public class UsersController(AppDbContext db) : ControllerBase
{
    /// <summary>Shortest query that will be answered at all.</summary>
    /// <remarks>One letter would match too much of the user base per request, and is no use for
    /// autocomplete anyway.</remarks>
    private const int MinQueryLength = 2;

    private const int MaxResults = 20;

    /// <summary>Finds users whose name starts with <paramref name="q"/>.</summary>
    /// <remarks>
    /// <para>Prefix match only: a substring search would make it easy to enumerate the user table.
    /// Matched against <c>NormalizedUserName</c>, which Identity keeps upper-cased and indexed, so it
    /// is case-insensitive and index-backed.</para>
    /// <para>Rows carry only an id and a name. <see cref="UserSummary.Email"/> stays null and
    /// <see cref="UserSummary.EmailConfirmed"/> is always false, so clients must not show a verified
    /// badge from it. <see cref="UserSearchPage.Total"/> is the number of rows returned, not the number
    /// that matched, so the size of the user base isn't exposed.</para>
    /// <para>Rate-limited per caller IP.</para>
    /// </remarks>
    [HttpGet]
    [EnableRateLimiting(RateLimitPolicies.UserSearch)]
    public async Task<ActionResult<UserSearchPage>> Search([FromQuery] string? q, CancellationToken ct)
    {
        var needle = (q ?? string.Empty).Trim();
        // Too short returns an empty result rather than an error, so the first keystroke doesn't show
        // one.
        if (needle.Length < MinQueryLength)
            return Ok(new UserSearchPage(Array.Empty<UserSummary>(), 0));
        if (needle.Length > 64) needle = needle[..64];

        var prefix = needle.ToUpperInvariant();
        var items = await db.Users
            .AsNoTracking()
            .Where(u => u.NormalizedUserName != null && u.NormalizedUserName.StartsWith(prefix))
            .OrderBy(u => u.NormalizedUserName)
            .Take(MaxResults)
            .Select(u => new UserSummary(u.Id, u.UserName ?? "", false, null))
            .ToListAsync(ct);

        return Ok(new UserSearchPage(items, items.Count));
    }
}
