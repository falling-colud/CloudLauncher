using System.Net;
using System.Security.Claims;
using System.Text;
using CloudLauncher.Server.Auth;
using CloudLauncher.Server.Data;
using CloudLauncher.Server.Storage;
using CloudLauncher.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace CloudLauncher.Server.Controllers;

[ApiController]
[Route("auth")]
public class AuthController(
    AppDbContext db,
    UserManager<AppUser> users,
    JwtTokenService tokens,
    IAccountEmailSender emailSender,
    GoogleAuthService google,
    AppOptions appOptions,
    BlobStore blobs,
    ILogger<AuthController> log) : ControllerBase
{
    /// <summary>The one answer to a failed sign-in, whatever the reason: no such account, wrong
    /// password, or an account locked after too many tries. Anything more specific would say which
    /// usernames exist.</summary>
    private const string SignInFailed =
        "Invalid username or password. After several failed tries, sign-in pauses for a few minutes.";

    private const string TooManyAttempts = "Too many attempts. Try again in a few minutes.";

    [HttpPost("register")]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<ActionResult> Register([FromBody] RegisterRequest req, CancellationToken ct)
    {
        if (await AccountSignups.AreClosedAsync(db, ct))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sign-ups are closed right now." });
        // Launchers from before the terms screen send null and are let through. Only a no is refused.
        if (req.AcceptTerms == false)
            return BadRequest(new { error = "Please accept the Terms and Privacy Policy." });

        var username = req.Username?.Trim() ?? "";
        if (UsernameRules.Problem(username) is { } nameProblem)
            return BadRequest(new { error = nameProblem });
        if (string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new { error = "Password required" });
        if (string.IsNullOrWhiteSpace(req.Email) || !req.Email.Contains('@'))
            return BadRequest(new { error = "A valid email address is required" });

        var isAdmin = await ShouldBootstrapAdminAsync(username);
        var accepted = req.AcceptTerms == true;
        var user = new AppUser
        {
            UserName = username,
            Email = req.Email.Trim(),
            // Email verification is disabled: accounts are usable immediately.
            EmailConfirmed = true,
            IsAdmin = isAdmin,
            TermsAcceptedAt = accepted ? DateTimeOffset.UtcNow : null,
            TermsVersion = accepted ? AcceptedTermsVersion(req.TermsVersion) : null
        };

        var result = await users.CreateAsync(user, req.Password);
        if (!result.Succeeded)
        {
            var codes = result.Errors.Select(e => e.Code).ToHashSet();
            if (codes.Contains(nameof(IdentityErrorDescriber.DuplicateUserName)))
                return BadRequest(new { error = "That username is taken." });
            // Taken and malformed addresses get the same words, so the answer never confirms that
            // an address has an account.
            if (codes.Contains(nameof(IdentityErrorDescriber.DuplicateEmail))
                || codes.Contains(nameof(IdentityErrorDescriber.InvalidEmail)))
                return BadRequest(new { error = "That email address can't be used. If you already have an account, sign in instead." });
            return BadRequest(new { error = string.Join(" ", result.Errors.Select(e => e.Description)) });
        }

        return Ok(new RegisterPendingResponse(
            RequiresEmailVerification: false,
            Message: "Account created. You can sign in now."));
    }

    [HttpPost("login")]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<ActionResult<TokenResponse>> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        var user = string.IsNullOrWhiteSpace(req.Username) ? null : await users.FindByNameAsync(req.Username.Trim());
        var check = await CheckPasswordAsync(user, req.Password);
        if (user is null || check != PasswordCheck.Ok)
            return Unauthorized(new { error = SignInFailed });

        // First-run bootstrap: promote the configured bootstrap user only while no admin exists, so the
        // bootstrap name can't be registered later to gain admin.
        if (!user.IsAdmin && await ShouldBootstrapAdminAsync(user.UserName ?? ""))
        {
            user.IsAdmin = true;
            await users.UpdateAsync(user);
        }

        return Ok(await tokens.IssueAsync(user, ct));
    }

    [HttpPost("resend-verification")]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<IActionResult> ResendVerification([FromBody] ResendVerificationRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Email))
            return BadRequest(new { error = "Email is required" });

        var user = await users.FindByEmailAsync(req.Email.Trim());
        if (user is null || user.EmailConfirmed)
            return Ok(new { message = "If that account exists and is unverified, a new email was sent." });

        var code = await users.GenerateEmailConfirmationTokenAsync(user);
        var link = BuildConfirmLink(user.Id, code);
        await emailSender.SendEmailConfirmationAsync(user, link, ct);

        return Ok(new { message = "If that account exists and is unverified, a new email was sent." });
    }

    [HttpGet("confirm-email")]
    public async Task<IActionResult> ConfirmEmail([FromQuery] Guid userId, [FromQuery] string code, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null)
            return Content(HtmlPage("Invalid link", "This confirmation link is not valid."), "text/html");

        string decoded;
        try { decoded = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code)); }
        catch (FormatException)
        {
            return Content(HtmlPage("Invalid link", "This confirmation link is not valid."), "text/html");
        }
        var result = await users.ConfirmEmailAsync(user, decoded);
        if (!result.Succeeded)
            return Content(HtmlPage("Invalid link", "This confirmation link is expired or invalid."), "text/html");

        return Content(
            HtmlPage("Email confirmed", "Your email is verified. You can close this page and sign in to CloudLauncher."),
            "text/html");
    }

    /// <param name="acceptTerms">True when the launcher showed the terms and the user accepted them.
    /// Recorded only if this sign-in creates the account; older launchers leave it out.</param>
    /// <param name="termsVersion">The <see cref="Legal.TermsVersion"/> the launcher showed.</param>
    [HttpGet("google/start")]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public ActionResult<GoogleAuthStartResponse> GoogleStart(
        [FromQuery] bool? acceptTerms = null, [FromQuery] string? termsVersion = null)
    {
        if (!google.IsConfigured)
            return BadRequest(new { error = "Google sign-in is not configured on the server." });

        var accepted = acceptTerms == true ? AcceptedTermsVersion(termsVersion) : null;
        if (google.TryStart(accepted) is not { } started)
            return StatusCode(StatusCodes.Status429TooManyRequests,
                new { error = "Too many sign-ins are in progress. Try again in a minute." });
        return Ok(new GoogleAuthStartResponse(started.AuthUrl, started.State));
    }

    [HttpGet("google/callback")]
    public async Task<IActionResult> GoogleCallback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        CancellationToken ct)
    {
        var failure = await google.HandleCallbackAsync(code, state, error, ct);

        if (failure is null)
            return Content(
                HtmlPage("Signed in", "Google sign-in is complete. Return to CloudLauncher. You can close this tab."),
                "text/html");

        if (!string.IsNullOrEmpty(error))
            return Content(
                HtmlPage("Sign-in cancelled", "Google sign-in was not completed. Close this tab and try again in the launcher."),
                "text/html");

        return Content(HtmlPage("Sign-in failed", failure), "text/html");
    }

    [HttpGet("google/poll")]
    public ActionResult<GoogleAuthPollResponse> GooglePoll([FromQuery] string state)
    {
        if (string.IsNullOrWhiteSpace(state))
            return BadRequest(new { error = "State is required" });

        var result = google.Poll(state);
        return Ok(new GoogleAuthPollResponse(result.Complete, result.Tokens, result.Error));
    }

    [HttpPost("refresh")]
    public async Task<ActionResult<TokenResponse>> Refresh([FromBody] RefreshRequest req, CancellationToken ct)
    {
        var result = await tokens.RefreshAsync(req.RefreshToken, ct);
        if (result is null) return Unauthorized(new { error = "Invalid or expired refresh token" });
        return Ok(result);
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<ActionResult<UserSummary>> Me()
    {
        var id = this.UserId();
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null) return Unauthorized();
        // Only this route returns the email address: it describes the caller to themselves, and the
        // Account page needs it to say which address is unverified.
        return Ok(Describe(user));
    }

    /// <summary>Renames the signed-in account.</summary>
    /// <remarks>
    /// Owner names on packs, mods and teams are read through the owner row, so they follow
    /// automatically. Invitations keep a copy of the addressed name for display, so pending and past
    /// invitations to this account are updated to match.
    /// </remarks>
    [Authorize]
    [HttpPatch("me/username")]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<ActionResult<UserSummary>> ChangeUsername(
        [FromBody] ChangeUsernameRequest req, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(this.UserId().ToString());
        if (user is null) return Unauthorized();
        if (AccountStatus.IsDisabled(user)) return AccountDisabled();

        var name = req.Username?.Trim() ?? "";
        if (UsernameRules.Problem(name) is { } problem)
            return BadRequest(new { error = problem });
        if (string.Equals(name, user.UserName, StringComparison.Ordinal))
            return Ok(Describe(user));

        // The bootstrap name is reserved: while no admin exists, whoever holds it is made one.
        var bootstrap = appOptions.BootstrapAdminUsername;
        var taken = await users.FindByNameAsync(name);
        if ((taken is not null && taken.Id != user.Id)
            || (!string.IsNullOrWhiteSpace(bootstrap) && !user.IsAdmin
                && string.Equals(name, bootstrap.Trim(), StringComparison.OrdinalIgnoreCase)))
            return Conflict(new { error = "That username is taken." });

        IdentityResult result;
        try
        {
            result = await users.SetUserNameAsync(user, name);
        }
        catch (DbUpdateException)
        {
            // Lost a race for the name against another rename or a registration.
            return Conflict(new { error = "That username is taken." });
        }
        if (!result.Succeeded)
        {
            if (result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.DuplicateUserName)))
                return Conflict(new { error = "That username is taken." });
            return BadRequest(new { error = string.Join(" ", result.Errors.Select(e => e.Description)) });
        }

        await db.PackInvitations.Where(i => i.InvitedUserId == user.Id && i.InvitedUsername != null)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.InvitedUsername, name), ct);
        await db.TeamInvitations.Where(i => i.InvitedUserId == user.Id && i.InvitedUsername != null)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.InvitedUsername, name), ct);
        await db.ContentBundleInvitations.Where(i => i.InvitedUserId == user.Id && i.InvitedUsername != null)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.InvitedUsername, name), ct);

        return Ok(Describe(user));
    }

    /// <summary>Deletes the signed-in account and everything it owns.</summary>
    /// <remarks>The work is <see cref="AccountDeletion"/>'s. Asking again once the account is gone
    /// answers 204 as well, since the outcome the caller wanted is already true.</remarks>
    [Authorize]
    [HttpDelete("me")]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<IActionResult> DeleteAccount([FromBody] DeleteAccountRequest req, CancellationToken ct)
    {
        if (!string.Equals(req.Confirm, "DELETE", StringComparison.Ordinal))
            return BadRequest(new { error = "Type DELETE to confirm." });

        var user = await users.FindByIdAsync(this.UserId().ToString());
        if (user is null) return NoContent();
        if (AccountStatus.IsDisabled(user)) return AccountDisabled();

        // Accounts made through Google have no password; the typed confirmation is all they give.
        if (await users.HasPasswordAsync(user))
        {
            if (string.IsNullOrEmpty(req.Password))
                return BadRequest(new { error = "Enter your password to delete your account." });
            switch (await CheckPasswordAsync(user, req.Password))
            {
                case PasswordCheck.LockedOut:
                    return BadRequest(new { error = TooManyAttempts });
                case PasswordCheck.Wrong:
                    return BadRequest(new { error = "Password is incorrect." });
            }
        }

        await AccountDeletion.DeleteAsync(db, blobs, user.Id, log, ct);
        return NoContent();
    }

    /// <summary>Everything the server holds about the signed-in account, as a JSON download.</summary>
    [Authorize]
    [HttpGet("me/export")]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<IActionResult> Export(CancellationToken ct)
    {
        var user = await users.FindByIdAsync(this.UserId().ToString());
        if (user is null) return Unauthorized();

        var json = await AccountExport.BuildAsync(db, user, ct);
        Response.Headers.CacheControl = "no-store";
        return File(json, "application/json", AccountExport.FileName);
    }

    /// <summary>Changes the signed-in user's password and signs their other devices out.</summary>
    /// <remarks>
    /// Every refresh token is revoked and the caller gets a fresh pair in the response, so other
    /// devices are signed out once their access token expires while this one stays signed in.
    /// </remarks>
    [Authorize]
    [HttpPost("change-password")]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<ActionResult<TokenResponse>> ChangePassword(
        [FromBody] ChangePasswordRequest req, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(req.NewPassword))
            return BadRequest(new { error = "New password required" });

        var user = await users.FindByIdAsync(this.UserId().ToString());
        if (user is null) return Unauthorized();
        // This route hands out a fresh token pair, so a disabled account must not reach it.
        if (AccountStatus.IsDisabled(user)) return AccountDisabled();

        switch (await CheckPasswordAsync(user, req.CurrentPassword))
        {
            case PasswordCheck.LockedOut:
                return BadRequest(new { error = TooManyAttempts });
            case PasswordCheck.Wrong:
                return BadRequest(new { error = "Current password is incorrect" });
        }

        var result = await users.ChangePasswordAsync(user, req.CurrentPassword, req.NewPassword);
        if (!result.Succeeded)
            return BadRequest(new { error = string.Join(" ", result.Errors.Select(e => e.Description)) });

        await tokens.RevokeAllAsync(user.Id, ct);
        return Ok(await tokens.IssueAsync(user, ct));
    }

    /// <summary>Signs this session out by revoking the refresh token it holds.</summary>
    /// <remarks>
    /// Not <c>[Authorize]</c>: a client whose access token has expired still has a refresh token worth
    /// revoking, and holding the token is enough authority to give it up. The answer is the same
    /// whether or not the token existed, so it reveals nothing about which tokens are valid.
    /// </remarks>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] RefreshRequest req, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(req.RefreshToken))
        {
            var hash = JwtTokenService.HashRefreshToken(req.RefreshToken);
            var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
            if (token is not null && token.RevokedAt is null)
            {
                token.RevokedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
            }
        }
        return NoContent();
    }

    /// <summary>Signs the user out of every device by revoking all of their refresh tokens.</summary>
    [Authorize]
    [HttpPost("logout-all")]
    public async Task<IActionResult> LogoutAll(CancellationToken ct)
    {
        await tokens.RevokeAllAsync(this.UserId(), ct);
        return NoContent();
    }

    /// <summary>What the signed-in user is storing on the server, and their quota if they have one.</summary>
    /// <remarks>
    /// Counts each distinct blob once across everything they own, since identical uploads share one
    /// file on disk.
    /// </remarks>
    [Authorize]
    [HttpGet("me/usage")]
    public async Task<ActionResult<UserStorageUsage>> MyUsage(CancellationToken ct)
    {
        var id = this.UserId();
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null) return Unauthorized();

        // The same figure the upload quota enforces, so the number shown is the number that counts.
        var services = HttpContext.RequestServices;
        var usedBytes = await StorageUsage.GetUsedBytesAsync(db,
            services.GetRequiredService<CloudLauncher.Server.Storage.BlobStore>(),
            services.GetRequiredService<CloudLauncher.Server.Storage.PendingUploadLedger>(), id, ct);
        return Ok(new UserStorageUsage(usedBytes, user.StorageQuotaBytes));
    }

    /// <summary>The figure <see cref="MyUsage"/> returns, itemised per thing the user owns.</summary>
    /// <remarks>The four sources and filters below must match MyUsage's, or the items stop adding up to
    /// the total (unshared packs and content bundles are left out of both). Like MyUsage it runs four
    /// separate server-side <c>DISTINCT</c> queries and combines them in memory: EF can't translate a
    /// set operation after a client projection. Rows keep their owning item, so the manifest
    /// <c>DISTINCT</c> is per (pack, hash).</remarks>
    [Authorize]
    [HttpGet("me/usage/breakdown")]
    public async Task<ActionResult<CloudStorageBreakdown>> MyUsageBreakdown(CancellationToken ct)
    {
        var id = this.UserId();
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null) return Unauthorized();

        var modRefs = await db.ModVersions
            .Where(v => v.Mod.OwnerId == id)
            .Select(v => new { ItemId = v.ModId, Hash = v.BlobHash, Size = v.FileSize })
            .Distinct().ToListAsync(ct);
        var worldRefs = await db.SharedWorldVersions
            .Where(v => v.World.OwnerId == id)
            .Select(v => new { ItemId = v.WorldId, Hash = v.BlobHash, Size = v.FileSize })
            .Distinct().ToListAsync(ct);
        var resourcePackRefs = await db.HostedResourcePackVersions
            .Where(v => v.ResourcePack.OwnerId == id)
            .Select(v => new { ItemId = v.ResourcePackId, Hash = v.BlobHash, Size = v.FileSize })
            .Distinct().ToListAsync(ct);
        // The expensive one (a heavy user has ~40 shared packs of 400-900 files each). Filtering and
        // de-duplication run on the server and only three narrow columns come back, one row per distinct
        // (pack, file).
        var manifestRefs = await db.PackManifestEntries
            .Where(e => e.Pack.OwnerId == id && e.Pack.IsShared)
            .Select(e => new { ItemId = e.PackId, Hash = e.Hash, Size = e.Size })
            .Distinct().ToListAsync(ct);

        // One row per item rather than per blob, so names aren't repeated on every file row. The counts
        // are subqueries, so this is four queries in total, not one per pack.
        var packs = await db.Packs
            .Where(p => p.OwnerId == id && p.IsShared)
            .Select(p => new { p.Id, p.Name, Rows = p.ManifestEntries.Count }).ToListAsync(ct);
        var mods = await db.Mods
            .Where(m => m.OwnerId == id)
            .Select(m => new { m.Id, m.Name, Rows = m.Versions.Count }).ToListAsync(ct);
        var worlds = await db.SharedWorlds
            .Where(w => w.OwnerId == id)
            .Select(w => new { w.Id, w.Name, Rows = w.Versions.Count }).ToListAsync(ct);
        var resourcePacks = await db.HostedResourcePacks
            .Where(r => r.OwnerId == id)
            .Select(r => new { r.Id, r.Name, Rows = r.Versions.Count }).ToListAsync(ct);

        // Concatenated in MyUsage's order so that the tie-break below picks the same row it does.
        var refs = new List<ItemBlobRef>(
            modRefs.Count + worldRefs.Count + resourcePackRefs.Count + manifestRefs.Count);
        refs.AddRange(modRefs.Select(r =>
            new ItemBlobRef(CloudStorageKinds.Mod, r.ItemId, r.Hash, r.Size)));
        refs.AddRange(worldRefs.Select(r =>
            new ItemBlobRef(CloudStorageKinds.World, r.ItemId, r.Hash, r.Size)));
        refs.AddRange(resourcePackRefs.Select(r =>
            new ItemBlobRef(CloudStorageKinds.ResourcePack, r.ItemId, r.Hash, r.Size)));
        refs.AddRange(manifestRefs.Select(r =>
            new ItemBlobRef(CloudStorageKinds.Pack, r.ItemId, r.Hash, r.Size)));

        // Each distinct blob lands in one of the two buckets: a blob with one owner is charged to that
        // owner's unique bytes, a blob with several is counted once in the total and charged in full to
        // each owner's shared bytes. So Sum(unique) + (shared blobs counted once) == usedBytes by
        // construction.
        var unique = new Dictionary<(string Kind, Guid Id), long>();
        var shared = new Dictionary<(string Kind, Guid Id), long>();
        long usedBytes = 0;
        foreach (var blob in refs.GroupBy(r => r.Hash, StringComparer.Ordinal))
        {
            // If one hash was ever recorded with two sizes, take the first, as MyUsage does, so the two
            // totals still match.
            var size = blob.First().Size;
            usedBytes += size;

            // Distinct owners, not rows: a blob "shared" with its own owner would count as nobody's unique
            // bytes.
            var owners = blob.Select(r => (r.Kind, r.Id)).Distinct().ToList();
            if (owners.Count == 1)
                unique[owners[0]] = unique.GetValueOrDefault(owners[0]) + size;
            else
                foreach (var owner in owners)
                    shared[owner] = shared.GetValueOrDefault(owner) + size;
        }

        var owned = new List<OwnedItem>(packs.Count + mods.Count + worlds.Count + resourcePacks.Count);
        owned.AddRange(packs.Select(p => new OwnedItem(CloudStorageKinds.Pack, p.Id, p.Name, p.Rows)));
        owned.AddRange(mods.Select(m => new OwnedItem(CloudStorageKinds.Mod, m.Id, m.Name, m.Rows)));
        owned.AddRange(worlds.Select(w => new OwnedItem(CloudStorageKinds.World, w.Id, w.Name, w.Rows)));
        owned.AddRange(resourcePacks.Select(r =>
            new OwnedItem(CloudStorageKinds.ResourcePack, r.Id, r.Name, r.Rows)));

        // Zero-cost items are still listed (an empty shared pack or a mod with no versions is a real
        // state). Biggest first, ties broken by name ordinally so the order doesn't depend on the
        // server's culture.
        var items = owned
            .Select(o => new CloudStorageItem(
                o.Kind, o.Id, o.Name,
                unique.GetValueOrDefault((o.Kind, o.Id)),
                shared.GetValueOrDefault((o.Kind, o.Id)),
                o.Rows))
            .OrderByDescending(i => i.UniqueBytes + i.SharedBytes)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Null quota means unlimited; the account meter reads 0 that way and draws no bar.
        return Ok(new CloudStorageBreakdown(usedBytes, user.StorageQuotaBytes ?? 0, items));
    }

    /// <summary>A blob a user's content keeps alive, projected out of the version tables.</summary>
    private sealed record BlobRef(string Hash, long Size);

    /// <summary>The same thing, tagged with the item that is keeping it alive.</summary>
    /// <remarks><c>Kind</c> is part of the identity: ids are only unique within one content
    /// family.</remarks>
    private sealed record ItemBlobRef(string Kind, Guid Id, string Hash, long Size);

    /// <summary>One thing the user owns, before any bytes have been attributed to it.</summary>
    /// <param name="Rows">Uploaded versions, or manifest entries for a pack: whichever
    /// <see cref="CloudStorageItem.Versions"/> reports for that kind.</param>
    private sealed record OwnedItem(string Kind, Guid Id, string Name, int Rows);

    private enum PasswordCheck { Ok, Wrong, LockedOut }

    /// <summary>Checks a password the way sign-in does: a wrong one counts towards the lockout, a
    /// locked account is refused whatever was typed, and a right one clears the count.</summary>
    /// <remarks>Every refusal costs the same hashing work as a real check, so how long the answer
    /// takes does not tell an unknown username from a locked or password-less account.</remarks>
    private async Task<PasswordCheck> CheckPasswordAsync(AppUser? user, string? password)
    {
        password ??= "";
        if (user is null || user.PasswordHash is null)
        {
            SpendHashingTime(password);
            return PasswordCheck.Wrong;
        }
        // Read directly rather than through IsLockedOutAsync, which ignores the lockout end on rows
        // whose LockoutEnabled flag is off.
        if (user.LockoutEnd is { } lockedUntil && lockedUntil > DateTimeOffset.UtcNow)
        {
            SpendHashingTime(password);
            return PasswordCheck.LockedOut;
        }
        if (!await users.CheckPasswordAsync(user, password))
        {
            await users.AccessFailedAsync(user);
            return PasswordCheck.Wrong;
        }
        if (user.AccessFailedCount > 0)
            await users.ResetAccessFailedCountAsync(user);
        return PasswordCheck.Ok;
    }

    private static readonly AppUser TimingUser = new();
    private static string? _timingHash;

    private void SpendHashingTime(string password)
    {
        var hasher = users.PasswordHasher;
        _timingHash ??= hasher.HashPassword(TimingUser, Guid.NewGuid().ToString("N"));
        hasher.VerifyHashedPassword(TimingUser, _timingHash, password);
    }

    private ObjectResult AccountDisabled() =>
        StatusCode(StatusCodes.Status403Forbidden, new { error = "This account has been disabled." });

    /// <summary>The caller as they see themselves: the only summary that carries the address and
    /// the admin flag.</summary>
    private static UserSummary Describe(AppUser user) =>
        new(user.Id, user.UserName ?? "", user.EmailConfirmed, user.Email, user.IsAdmin);

    /// <summary>The terms version a registration says it showed, or the current one when it did not
    /// say. Clamped to the column's width.</summary>
    private static string AcceptedTermsVersion(string? sent)
    {
        var version = string.IsNullOrWhiteSpace(sent) ? Legal.TermsVersion : sent.Trim();
        return version.Length <= 32 ? version : version[..32];
    }

    /// <summary>True only when <paramref name="username"/> matches the configured bootstrap admin
    /// and no admin account exists yet, so bootstrapping is a one-time, first-run event rather than a
    /// standing escalation path.</summary>
    private async Task<bool> ShouldBootstrapAdminAsync(string username)
    {
        var bootstrap = appOptions.BootstrapAdminUsername;
        if (string.IsNullOrWhiteSpace(bootstrap)) return false;
        if (!string.Equals(username?.Trim(), bootstrap, StringComparison.OrdinalIgnoreCase)) return false;
        return !await users.Users.AnyAsync(u => u.IsAdmin);
    }

    private string BuildConfirmLink(Guid userId, string token)
    {
        var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        var path = QueryHelpers.AddQueryString("/auth/confirm-email", new Dictionary<string, string?>
        {
            ["userId"] = userId.ToString(),
            ["code"] = code
        });
        return $"{appOptions.PublicBaseUrl.TrimEnd('/')}{path}";
    }

    /// <summary>
    /// The small pages a browser lands on after an e-mail confirmation link or Google sign-in, styled
    /// like the website (wwwroot/site.css) with Pixelify Sans from /fonts on this host. Everything else
    /// is inline so the page stands on its own.
    /// </summary>
    private static string HtmlPage(string title, string message)
    {
        var t = WebUtility.HtmlEncode(title);
        var m = WebUtility.HtmlEncode(message);
        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="robots" content="noindex">
            <title>{{t}} · CloudLauncher</title>
            <link rel="icon" href="/favicon.ico">
            <style>
            @font-face{font-family:"Pixelify Sans";src:url("/fonts/pixelify-sans-400.woff2") format("woff2");font-display:swap}
            :root{color-scheme:dark}
            *{box-sizing:border-box}
            body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;padding:24px 16px;background:#161615;color:#eceae4;font:400 17px/28px "Segoe UI Variable Text","Segoe UI",system-ui,sans-serif;font-variant-ligatures:no-common-ligatures}
            main{position:relative;isolation:isolate;width:100%;max-width:30rem;padding:30px 32px 32px}
            main::before,main::after{content:"";position:absolute;inset:0;clip-path:polygon(6px 0,calc(100% - 6px) 0,calc(100% - 6px) 2px,calc(100% - 4px) 2px,calc(100% - 4px) 4px,calc(100% - 2px) 4px,calc(100% - 2px) 6px,100% 6px,100% calc(100% - 6px),calc(100% - 2px) calc(100% - 6px),calc(100% - 2px) calc(100% - 4px),calc(100% - 4px) calc(100% - 4px),calc(100% - 4px) calc(100% - 2px),calc(100% - 6px) calc(100% - 2px),calc(100% - 6px) 100%,6px 100%,6px calc(100% - 2px),4px calc(100% - 2px),4px calc(100% - 4px),2px calc(100% - 4px),2px calc(100% - 6px),0 calc(100% - 6px),0 6px,2px 6px,2px 4px,4px 4px,4px 2px,6px 2px)}
            main::before{z-index:-1;background:linear-gradient(#191918 0 0) 6px 2px/calc(100% - 12px) calc(100% - 4px) no-repeat,linear-gradient(#191918 0 0) 4px 4px/calc(100% - 8px) calc(100% - 8px) no-repeat,linear-gradient(#191918 0 0) 2px 6px/calc(100% - 4px) calc(100% - 12px) no-repeat,#45443f}
            main::after{z-index:-2;background:rgb(0 0 0/.45);transform:translate(4px,4px)}
            .brand{display:inline-flex;align-items:center;gap:10px;color:#eceae4;text-decoration:none;font:400 19px/1 "Pixelify Sans","Segoe UI",sans-serif}
            .brand svg{width:24px;height:24px;display:block}
            h1{margin:22px 0 0;font:400 33px/38px "Pixelify Sans","Segoe UI",sans-serif}
            p{margin:12px 0 0;color:#c9c6be}
            a{color:#a07868}
            a:focus-visible{outline:2px solid #a07868;outline-offset:3px}
            </style>
            </head>
            <body>
            <main>
            <a class="brand" href="/"><svg viewBox="0 0 16 16" shape-rendering="crispEdges" aria-hidden="true"><path fill="#601B00" d="M3 0h10v1h1v1h1v1h1v10h-1v1h-1v1h-1v1H3v-1H2v-1H1v-1H0V3h1V2h1V1h1z"/><path fill="#F5F4EF" d="M7 4h4v1h1v2h1v1h1v3h-1v1H3v-1H2V8h1V7h3V5h1z"/></svg>CloudLauncher</a>
            <h1>{{t}}</h1>
            <p>{{m}}</p>
            </main>
            </body>
            </html>
            """;
    }
}
