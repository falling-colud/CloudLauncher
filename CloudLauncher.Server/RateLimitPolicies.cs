namespace CloudLauncher.Server;

/// <summary>Named rate-limiting policies shared between Program startup and controllers.</summary>
public static class RateLimitPolicies
{
    /// <summary>Per-IP throttle for password/email auth endpoints (login, register, resend).</summary>
    public const string Auth = "auth";

    /// <summary>Per-IP throttle for the user-search autocomplete.</summary>
    /// <remarks>Kept apart from <see cref="Auth"/> so typing in the invite box can't lock anyone out of
    /// signing in. Search only answers prefix matches, but it still reveals account names, so it is
    /// capped.</remarks>
    public const string UserSearch = "user-search";
}
