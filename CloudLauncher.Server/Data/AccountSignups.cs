using Microsoft.EntityFrameworkCore;

namespace CloudLauncher.Server.Data;

/// <summary>The admin switch that stops new accounts from being created, by registering or by a
/// first Google sign-in. Existing accounts are unaffected.</summary>
public static class AccountSignups
{
    /// <summary>True when an admin has closed sign-ups. No settings row yet means open.</summary>
    public static Task<bool> AreClosedAsync(AppDbContext db, CancellationToken ct = default) =>
        db.GlobalSettings.AsNoTracking().AnyAsync(s => s.RegistrationClosed, ct);
}
