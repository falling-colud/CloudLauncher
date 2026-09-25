namespace CloudLauncher.Shared;

/// <param name="AcceptTerms">Null from launchers that predate the terms screen; false is refused.</param>
/// <param name="TermsVersion">The <see cref="Legal.TermsVersion"/> the user was shown.</param>
public sealed record RegisterRequest(string Username, string Password, string Email,
    bool? AcceptTerms = null, string? TermsVersion = null);

public sealed record LoginRequest(string Username, string Password);

public sealed record RefreshRequest(string RefreshToken);

public sealed record ResendVerificationRequest(string Email);

public sealed record TokenResponse(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessTokenExpiresAt,
    string Username,
    Guid UserId);

/// <summary>Returned from register when the account must be verified by email before sign-in.</summary>
public sealed record RegisterPendingResponse(bool RequiresEmailVerification, string Message);

public sealed record GoogleAuthStartResponse(string AuthUrl, string State);

public sealed record GoogleAuthPollResponse(
    bool Complete,
    TokenResponse? Tokens = null,
    string? Error = null);

/// <summary>Who a user is, as any screen that lists or identifies people sees them.</summary>
/// <param name="Email">The registered address, or null when this summary came from somewhere that
/// does not disclose it.</param>
/// <remarks>
/// <para><c>Email</c> is last and defaults to null so existing positional constructions keep
/// compiling.</para>
/// <para>Only <c>auth/me</c> fills it in, and only with the caller's own address, so the Account page
/// can say which mailbox to check. Team rosters leave it null so members don't see each other's
/// addresses.</para>
/// </remarks>
public sealed record UserSummary(Guid Id, string Username, bool EmailConfirmed, string? Email = null,
    bool IsAdmin = false);

/// <summary>People matching a search, for the "who do you want to invite?" box.</summary>
/// <remarks>Matches on username prefix only and returns a small page, so it works as an autocomplete
/// and can't list every account. <see cref="UserSummary.Email"/> is always null here for
/// the same reason.</remarks>
public sealed record UserSearchPage(IReadOnlyList<UserSummary> Items, int Total);

/// <summary>Change the signed-in user's own password.</summary>
/// <remarks>
/// The current password is required even though the caller is already authenticated: an access
/// token left behind on a shared machine should not be enough to lock its owner out of the account.
/// </remarks>
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>Rename the signed-in account.</summary>
public sealed record ChangeUsernameRequest(string Username);

/// <summary>Delete the signed-in account and everything it owns.</summary>
/// <param name="Password">Required when the account has a password; null for Google-only accounts.</param>
/// <param name="Confirm">Must be the literal text "DELETE".</param>
public sealed record DeleteAccountRequest(string? Password, string Confirm);

/// <summary>Versions and paths of the legal pages, shared so the launcher and the server agree on
/// which terms someone accepted.</summary>
public static class Legal
{
    public const string TermsVersion = "2026-09-25";
    public const string TermsPath = "/terms";
    public const string PrivacyPath = "/privacy";
    public const string ContactEmail = "leon.raineri@gmail.com";
}
