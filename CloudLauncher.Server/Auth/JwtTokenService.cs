using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using CloudLauncher.Server.Data;
using CloudLauncher.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace CloudLauncher.Server.Auth;

public class JwtOptions
{
    public string SigningKey { get; set; } = "";
    public string Issuer { get; set; } = "CloudLauncher";
    public string Audience { get; set; } = "CloudLauncher";
    public int AccessTokenMinutes { get; set; } = 60;
    public int RefreshTokenDays { get; set; } = 30;
}

public class JwtTokenService(AppDbContext db, JwtOptions opts, ILogger<JwtTokenService> log)
{
    /// <summary>How long a rotated refresh token may come back and be treated as the same client
    /// retrying, for example after a lost response, rather than as a stolen copy.</summary>
    private static readonly TimeSpan RotationGrace = TimeSpan.FromSeconds(60);

    public Task<TokenResponse> IssueAsync(AppUser user, CancellationToken ct = default) =>
        IssueAsync(user, Guid.NewGuid(), ct);

    private async Task<TokenResponse> IssueAsync(AppUser user, Guid refreshTokenId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var accessExpires = now.AddMinutes(opts.AccessTokenMinutes);

        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(opts.SigningKey)),
            SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.UniqueName, user.UserName ?? ""),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };
        if (user.IsAdmin)
            claims.Add(new Claim(ClaimTypes.Role, "admin"));

        var jwt = new JwtSecurityToken(
            issuer: opts.Issuer,
            audience: opts.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: accessExpires.UtcDateTime,
            signingCredentials: creds);

        var accessToken = new JwtSecurityTokenHandler().WriteToken(jwt);

        var refreshRaw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        var refreshHash = HashRefreshToken(refreshRaw);
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = refreshTokenId,
            UserId = user.Id,
            TokenHash = refreshHash,
            ExpiresAt = now.AddDays(opts.RefreshTokenDays)
        });
        await db.SaveChangesAsync(ct);

        return new TokenResponse(accessToken, refreshRaw, accessExpires, user.UserName ?? "", user.Id);
    }

    /// <summary>Trades a refresh token for a new pair, revoking the old one.</summary>
    /// <remarks>A rotated token that turns up again more than <see cref="RotationGrace"/> later means
    /// two parties hold it, and there's no telling which is legitimate, so every live token the user
    /// has is revoked and both must sign in again.</remarks>
    public async Task<TokenResponse?> RefreshAsync(string refreshRaw, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(refreshRaw)) return null;

        var hash = HashRefreshToken(refreshRaw);
        var token = await db.RefreshTokens
            .AsNoTracking()
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null) return null;

        var now = DateTimeOffset.UtcNow;
        if (token.RevokedAt is { } revokedAt)
        {
            if (token.ReplacedById is not null && now - revokedAt > RotationGrace)
            {
                var revoked = await RevokeAllAsync(token.UserId, ct);
                log.LogWarning("A rotated refresh token was used again; revoked {Count} live tokens for user {UserId}.",
                    revoked, token.UserId);
            }
            return null;
        }
        if (token.ExpiresAt < now || AccountStatus.IsDisabled(token.User))
            return null;

        // Revoking the old token and storing the new one commit together: a rotation that stopped
        // halfway would leave a token marked as replaced, and its next use would look like theft.
        // The conditional update also means two refreshes racing on one token cannot both win.
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync<TokenResponse?>(async () =>
        {
            db.ChangeTracker.Clear();
            var nextId = Guid.NewGuid();
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var claimed = await db.RefreshTokens
                .Where(t => t.Id == token.Id && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.RevokedAt, (DateTimeOffset?)now)
                    .SetProperty(t => t.ReplacedById, (Guid?)nextId), ct);
            if (claimed == 0) return null;

            var issued = await IssueAsync(token.User, nextId, ct);
            await tx.CommitAsync(ct);
            return issued;
        });
    }

    /// <summary>Revokes every refresh token the user still holds. Returns how many there were.</summary>
    public Task<int> RevokeAllAsync(Guid userId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        return db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, (DateTimeOffset?)now), ct);
    }

    /// <summary>Refresh tokens are stored only as this hash.</summary>
    internal static string HashRefreshToken(string raw)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes);
    }
}
