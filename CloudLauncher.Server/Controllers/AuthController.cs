using System.Net;
using System.Security.Claims;
using System.Text;
using CloudLauncher.Server.Auth;
using CloudLauncher.Server.Data;
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
    AppOptions appOptions) : ControllerBase
{
    [HttpPost("register")]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<ActionResult> Register([FromBody] RegisterRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Username) || req.Username.Length < 3 || req.Username.Length > 32)
            return BadRequest(new { error = "Username must be 3-32 characters" });
        if (string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new { error = "Password required" });
        if (string.IsNullOrWhiteSpace(req.Email) || !req.Email.Contains('@'))
            return BadRequest(new { error = "A valid email address is required" });

        var isAdmin = await ShouldBootstrapAdminAsync(req.Username);
        var user = new AppUser
        {
            UserName = req.Username.Trim(),
            Email = req.Email.Trim(),
            // Email verification is disabled: accounts are usable immediately.
            EmailConfirmed = true,
            IsAdmin = isAdmin
        };

        var result = await users.CreateAsync(user, req.Password);
        if (!result.Succeeded)
            return BadRequest(new { error = string.Join("; ", result.Errors.Select(e => e.Description)) });

        return Ok(new RegisterPendingResponse(
            RequiresEmailVerification: false,
            Message: "Account created. You can sign in now."));
    }

    [HttpPost("login")]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<ActionResult<TokenResponse>> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        var user = await users.FindByNameAsync(req.Username);
        if (user is null || !await users.CheckPasswordAsync(user, req.Password))
            return Unauthorized(new { error = "Invalid username or password" });

        // First-run bootstrap only: promote the configured bootstrap user if (and only if) the
        // system still has no admin. Once an admin exists this is inert — it is not a standing
        // backdoor by which anyone registering the bootstrap name could later gain admin.
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

    [HttpGet("google/start")]
    public ActionResult<GoogleAuthStartResponse> GoogleStart()
    {
        if (!google.IsConfigured)
            return BadRequest(new { error = "Google sign-in is not configured on the server." });

        var (authUrl, state) = google.Start();
        return Ok(new GoogleAuthStartResponse(authUrl, state));
    }

    [HttpGet("google/callback")]
    public async Task<IActionResult> GoogleCallback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        CancellationToken ct)
    {
        await google.HandleCallbackAsync(code, state, error, ct);

        if (string.IsNullOrEmpty(error))
            return Content(
                HtmlPage("Signed in", "Google sign-in complete. Return to CloudLauncher — you can close this tab."),
                "text/html");

        return Content(
            HtmlPage("Sign-in cancelled", "Google sign-in was not completed. Close this tab and try again in the launcher."),
            "text/html");
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
        // Look up the real user so EmailConfirmed reflects actual state rather than a
        // hard-coded `true` (which made the flag meaningless to any consumer).
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null) return Unauthorized();
        // The address is returned here and nowhere else: this route only ever describes the caller
        // to themselves, so there is no one to leak it to, and without it the Account page can say
        // an address is unverified but not which address to go and check.
        return Ok(new UserSummary(id, user.UserName ?? "", user.EmailConfirmed, user.Email));
    }

    /// <summary>Changes the signed-in user's password and signs their other devices out.</summary>
    /// <remarks>
    /// Every refresh token is revoked and the caller is handed a fresh pair in the response. That is
    /// what "everywhere else is signed out, you are not" means for a stateless access token: the
    /// caller cannot keep a refresh token the change was supposed to invalidate, and a device that
    /// still holds an old one cannot trade it for a new access token once its current one expires.
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

        if (!await users.CheckPasswordAsync(user, req.CurrentPassword))
            return BadRequest(new { error = "Current password is incorrect" });

        var result = await users.ChangePasswordAsync(user, req.CurrentPassword, req.NewPassword);
        if (!result.Succeeded)
            return BadRequest(new { error = string.Join("; ", result.Errors.Select(e => e.Description)) });

        await RevokeRefreshTokensAsync(user.Id, ct);
        return Ok(await tokens.IssueAsync(user, ct));
    }

    /// <summary>Signs this session out by revoking the refresh token it holds.</summary>
    /// <remarks>
    /// Deliberately not <c>[Authorize]</c>: a client whose access token has already expired still has
    /// a refresh token worth revoking, and refusing it would leave that token live for its full 30
    /// days. Possession of the token is the only authority needed to give it up. The answer is the
    /// same whether or not the token existed, so this cannot be used to probe for valid tokens.
    /// </remarks>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] RefreshRequest req, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(req.RefreshToken))
        {
            var hash = HashRefreshToken(req.RefreshToken);
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
        await RevokeRefreshTokensAsync(this.UserId(), ct);
        return NoContent();
    }

    /// <summary>What the signed-in user is storing on the server, and their quota if they have one.</summary>
    /// <remarks>
    /// Counts each distinct blob once across everything they own. Two versions of the same mod that
    /// are byte-identical, or a world uploaded twice under different names, occupy one file on disk;
    /// charging for both would tell the user they are using storage that deleting would not return.
    /// </remarks>
    [Authorize]
    [HttpGet("me/usage")]
    public async Task<ActionResult<UserStorageUsage>> MyUsage(CancellationToken ct)
    {
        var id = this.UserId();
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null) return Unauthorized();

        // The four sources are queried separately and unioned here rather than Concat-ed into one
        // query. EF cannot translate a set operation that follows a client projection — building a
        // BlobRef in the Select and then Concat/Distinct throws "Unable to translate set operation
        // after client projection has been applied", which failed this endpoint for every user. Each
        // query still projects only the two columns and does its own server-side DISTINCT, so what
        // comes back is small; the GroupBy below finishes the de-duplication across the four sets.
        var modBlobs = await db.ModVersions
            .Where(v => v.Mod.OwnerId == id)
            .Select(v => new { Hash = v.BlobHash, Size = v.FileSize })
            .Distinct().ToListAsync(ct);
        var worldBlobs = await db.SharedWorldVersions
            .Where(v => v.World.OwnerId == id)
            .Select(v => new { Hash = v.BlobHash, Size = v.FileSize })
            .Distinct().ToListAsync(ct);
        var packBlobs = await db.HostedResourcePackVersions
            .Where(v => v.ResourcePack.OwnerId == id)
            .Select(v => new { Hash = v.BlobHash, Size = v.FileSize })
            .Distinct().ToListAsync(ct);
        var manifestBlobs = await db.PackManifestEntries
            .Where(e => e.Pack.OwnerId == id && e.Pack.IsShared)
            .Select(e => new { Hash = e.Hash, Size = e.Size })
            .Distinct().ToListAsync(ct);

        var distinct = modBlobs.Concat(worldBlobs).Concat(packBlobs).Concat(manifestBlobs)
            .Select(b => new BlobRef(b.Hash, b.Size))
            .Distinct()
            .ToList();
        // DISTINCT is over (hash, size); grouping again collapses the pathological case of one hash
        // recorded with two different sizes, which would otherwise be counted twice.
        var usedBytes = distinct.GroupBy(b => b.Hash).Sum(g => g.First().Size);

        return Ok(new UserStorageUsage(usedBytes, user.StorageQuotaBytes));
    }

    /// <summary>A blob a user's content keeps alive, projected out of the version tables.</summary>
    private sealed record BlobRef(string Hash, long Size);

    /// <summary>Revokes every refresh token this user still holds.</summary>
    private async Task RevokeRefreshTokensAsync(Guid userId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var live = await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var token in live)
            token.RevokedAt = now;
        if (live.Count > 0) await db.SaveChangesAsync(ct);
    }

    /// <summary>Hashes a raw refresh token the same way <see cref="JwtTokenService"/> stores it.</summary>
    /// <remarks>
    /// Refresh tokens are only ever stored hashed, and the service that writes them keeps its hashing
    /// private, so finding a row by the raw token the client presents means repeating it here. Any
    /// change to the algorithm there has to be mirrored here or logout silently stops matching rows.
    /// </remarks>
    private static string HashRefreshToken(string raw) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    /// <summary>True only when <paramref name="username"/> matches the configured bootstrap admin
    /// AND no admin account exists yet. This makes admin bootstrap a one-time, first-run event
    /// rather than a permanent "register this username to become admin" escalation path.</summary>
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

    private static string HtmlPage(string title, string message) =>
        "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"/><title>" +
        WebUtility.HtmlEncode(title) +
        "</title><style>body{font-family:system-ui,sans-serif;background:#0b0d11;color:#e8eaed;" +
        "display:flex;align-items:center;justify-content:center;min-height:100vh;margin:0}" +
        ".card{background:#161a22;border:1px solid #2a3140;border-radius:12px;padding:2rem;max-width:28rem}" +
        "</style></head><body><div class=\"card\"><h1>" + WebUtility.HtmlEncode(title) +
        "</h1><p>" + WebUtility.HtmlEncode(message) + "</p></div></body></html>";
}
