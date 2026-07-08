namespace CloudLauncher.Server;

/// <summary>Named rate-limiting policies shared between Program startup and controllers.</summary>
public static class RateLimitPolicies
{
    /// <summary>Per-IP throttle for password/email auth endpoints (login, register, resend).</summary>
    public const string Auth = "auth";
}
