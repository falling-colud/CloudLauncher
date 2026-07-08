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

public sealed record UserSummary(Guid Id, string Username, bool EmailConfirmed);
