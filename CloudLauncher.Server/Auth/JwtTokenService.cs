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

public class JwtTokenService(AppDbContext db, JwtOptions opts)
{
    public async Task<TokenResponse> IssueAsync(AppUser user, CancellationToken ct = default)
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
            UserId = user.Id,
            TokenHash = refreshHash,
            ExpiresAt = now.AddDays(opts.RefreshTokenDays)
        });
        await db.SaveChangesAsync(ct);

        return new TokenResponse(accessToken, refreshRaw, accessExpires, user.UserName ?? "", user.Id);
    }

    public async Task<TokenResponse?> RefreshAsync(string refreshRaw, CancellationToken ct = default)
    {
        var hash = HashRefreshToken(refreshRaw);
        var token = await db.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (token is null || token.RevokedAt is not null || token.ExpiresAt < DateTimeOffset.UtcNow)
            return null;

        token.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return await IssueAsync(token.User, ct);
    }

    private static string HashRefreshToken(string raw)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes);
    }
}
