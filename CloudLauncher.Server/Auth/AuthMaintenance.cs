using CloudLauncher.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace CloudLauncher.Server.Auth;

/// <summary>Housekeeping for sign-in state: old refresh tokens and abandoned Google sign-ins.</summary>
/// <remarks>
/// Refresh tokens are kept for 30 days after they expire or are revoked, so a stolen token that was
/// already rotated is still recognised if it comes back. Without the sweep the table only grows.
/// </remarks>
public sealed class AuthMaintenance(IServiceScopeFactory scopes, ILogger<AuthMaintenance> log) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TokenSweepInterval = TimeSpan.FromHours(12);
    private static readonly TimeSpan TokenSweepRetry = TimeSpan.FromHours(1);
    private static readonly TimeSpan KeepTokensFor = TimeSpan.FromDays(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The first sweep waits a little so it does not compete with startup.
        var nextTokenSweep = DateTimeOffset.UtcNow.AddMinutes(5);
        using var timer = new PeriodicTimer(Tick);
        try
        {
            do
            {
                GoogleAuthService.RemoveExpired();

                if (DateTimeOffset.UtcNow >= nextTokenSweep)
                {
                    var swept = await SweepRefreshTokensAsync(stoppingToken);
                    nextTokenSweep = DateTimeOffset.UtcNow + (swept ? TokenSweepInterval : TokenSweepRetry);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> SweepRefreshTokensAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var cutoff = DateTimeOffset.UtcNow - KeepTokensFor;
            var removed = await db.RefreshTokens
                .Where(t => t.ExpiresAt < cutoff || (t.RevokedAt != null && t.RevokedAt < cutoff))
                .ExecuteDeleteAsync(ct);
            if (removed > 0)
                log.LogInformation("Deleted {Count} old refresh tokens.", removed);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The database may be restarting. Nothing here is urgent, so try again later.
            log.LogWarning(ex, "Refresh token cleanup failed; it will run again later.");
            return false;
        }
    }
}
