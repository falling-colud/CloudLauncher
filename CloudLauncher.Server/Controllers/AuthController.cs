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
            EmailConfirmed = false,
            IsAdmin = isAdmin
        };

        var result = await users.CreateAsync(user, req.Password);
        if (!result.Succeeded)
            return BadRequest(new { error = string.Join("; ", result.Errors.Select(e => e.Description)) });

        var code = await users.GenerateEmailConfirmationTokenAsync(user);
        var link = BuildConfirmLink(user.Id, code);
        await emailSender.SendEmailConfirmationAsync(user, link, ct);

        return Ok(new RegisterPendingResponse(
            RequiresEmailVerification: true,
            Message: "Account created. Check your email for a confirmation link before signing in."));
    }

    [HttpPost("login")]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<ActionResult<TokenResponse>> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        var user = await users.FindByNameAsync(req.Username);
        if (user is null || !await users.CheckPasswordAsync(user, req.Password))
            return Unauthorized(new { error = "Invalid username or password" });

        if (!user.EmailConfirmed)
            return Unauthorized(new { error = "Please verify your email before signing in." });

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
        return Ok(new UserSummary(id, user.UserName ?? "", user.EmailConfirmed));
    }

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
