using CloudLauncher.Server.Data;

namespace CloudLauncher.Server.Auth;

/// <summary>Tells an account an admin disabled apart from one that is only locked for a few
/// minutes after failed sign-ins.</summary>
/// <remarks>Both use Identity's lockout end. A disable writes a date thousands of years out, so
/// anything past <see cref="DisabledThreshold"/> is a disable and anything before it is the short
/// lockout that ends on its own.</remarks>
public static class AccountStatus
{
    /// <summary>The lockout end an admin disable writes.</summary>
    public static readonly DateTimeOffset DisabledUntil = new(9999, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Lockouts that end after this are disables.</summary>
    public static readonly DateTimeOffset DisabledThreshold = new(9000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static bool IsDisabled(AppUser user) =>
        user.LockoutEnd is { } end && end >= DisabledThreshold;
}
