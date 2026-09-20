namespace CloudLauncher.Shared;

public sealed record RegisterRequest(string Username, string Password, string Email);

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
/// <para><c>Email</c> is appended last and defaults to null so every existing positional
/// construction site — the team roster in TeamsController among them — keeps compiling and keeps
/// meaning exactly what it did.</para>
/// <para>Only <c>auth/me</c> fills it in, and only ever with the caller's own address. Telling the
/// Account page "unverified" without telling it which mailbox to go and look in made the one action
/// that fixes it — resending the mail — start by asking the user for something the server already
/// knew. A team roster deliberately leaves it null: a shared team is not a reason to hand every
/// member's address to every other member.</para>
/// </remarks>
public sealed record UserSummary(Guid Id, string Username, bool EmailConfirmed, string? Email = null);

/// <summary>Change the signed-in user's own password.</summary>
/// <remarks>
/// The current password is required even though the caller is already authenticated: an access
/// token left behind on a shared machine should not be enough to lock its owner out of the account.
/// </remarks>
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
