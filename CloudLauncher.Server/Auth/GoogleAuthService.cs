using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using CloudLauncher.Server.Data;
using CloudLauncher.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;

namespace CloudLauncher.Server.Auth;

public sealed class GoogleAuthPendingResult
{
    public bool Complete { get; init; }
    public TokenResponse? Tokens { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
}

public sealed class GoogleAuthService(
    GoogleAuthOptions google,
    AppOptions app,
    IHttpClientFactory httpFactory,
    UserManager<AppUser> users,
    JwtTokenService tokens)
{
    private static readonly ConcurrentDictionary<string, GoogleAuthPendingResult> Pending = new();

    private const string Provider = "Google";
    private static readonly TimeSpan PendingTtl = TimeSpan.FromMinutes(10);

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(google.ClientId) && !string.IsNullOrWhiteSpace(google.ClientSecret);

    public string RedirectUri =>
        $"{app.PublicBaseUrl.TrimEnd('/')}/auth/google/callback";

    public (string AuthUrl, string State) Start()
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Google sign-in is not configured on the server.");

        // Opportunistically evict expired pending flows so abandoned sign-ins (user never
        // polls, callback never fires) don't accumulate in the static dictionary forever.
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in Pending)
            if (kv.Value.ExpiresAt < now)
                Pending.TryRemove(kv.Key, out _);

        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        Pending[state] = new GoogleAuthPendingResult { ExpiresAt = DateTimeOffset.UtcNow.Add(PendingTtl) };

        var query = new Dictionary<string, string?>
        {
            ["client_id"] = google.ClientId,
            ["redirect_uri"] = RedirectUri,
            ["response_type"] = "code",
            ["scope"] = "openid email profile",
            ["state"] = state,
            ["access_type"] = "online",
            ["prompt"] = "select_account"
        };

        var authUrl = QueryHelpers.AddQueryString(
            "https://accounts.google.com/o/oauth2/v2/auth", query!);

        return (authUrl, state);
    }

    public GoogleAuthPendingResult Poll(string state)
    {
        if (!Pending.TryGetValue(state, out var result))
            return new GoogleAuthPendingResult { Complete = true, Error = "Sign-in session expired or invalid." };

        if (result.ExpiresAt < DateTimeOffset.UtcNow)
        {
            Pending.TryRemove(state, out _);
            return new GoogleAuthPendingResult { Complete = true, Error = "Sign-in session expired." };
        }

        return result;
    }

    public async Task HandleCallbackAsync(string? code, string? state, string? error, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(state) || !Pending.ContainsKey(state))
            return;

        void Fail(string message)
        {
            Pending[state] = new GoogleAuthPendingResult
            {
                Complete = true,
                Error = message,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2)
            };
        }

        if (!string.IsNullOrEmpty(error))
        {
            Fail("Google sign-in was cancelled.");
            return;
        }

        if (string.IsNullOrEmpty(code))
        {
            Fail("Missing authorization code from Google.");
            return;
        }

        try
        {
            var profile = await ExchangeCodeAsync(code, ct);
            if (profile is null)
            {
                Fail("Could not complete Google sign-in.");
                return;
            }

            var user = await FindOrCreateUserAsync(profile, ct);
            var tokenResponse = await tokens.IssueAsync(user, ct);
            Pending[state] = new GoogleAuthPendingResult
            {
                Complete = true,
                Tokens = tokenResponse,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2)
            };
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
    }

    private async Task<GoogleProfile?> ExchangeCodeAsync(string code, CancellationToken ct)
    {
        var http = httpFactory.CreateClient();
        using var tokenReq = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"] = code,
                ["client_id"] = google.ClientId,
                ["client_secret"] = google.ClientSecret,
                ["redirect_uri"] = RedirectUri,
                ["grant_type"] = "authorization_code"
            })
        };

        using var tokenResp = await http.SendAsync(tokenReq, ct);
        if (!tokenResp.IsSuccessStatusCode)
            return null;

        var tokenPayload = await tokenResp.Content.ReadFromJsonAsync<GoogleTokenResponse>(ct);
        if (string.IsNullOrEmpty(tokenPayload?.AccessToken))
            return null;

        using var userReq = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/oauth2/v3/userinfo");
        userReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenPayload.AccessToken);
        using var userResp = await http.SendAsync(userReq, ct);
        if (!userResp.IsSuccessStatusCode)
            return null;

        var info = await userResp.Content.ReadFromJsonAsync<GoogleUserInfoResponse>(ct);
        if (info is null || string.IsNullOrEmpty(info.Sub))
            return null;

        return new GoogleProfile(info.Sub, info.Email, info.Name, info.EmailVerified);
    }

    private async Task<AppUser> FindOrCreateUserAsync(GoogleProfile profile, CancellationToken ct)
    {
        var login = await users.FindByLoginAsync(Provider, profile.Sub);
        if (login is not null)
            return login;

        // Only auto-link a Google identity to an existing local account when Google has
        // actually verified the email. Without this check, an attacker holding a Google
        // token whose email is unverified could bind their login to a victim account that
        // happens to share that address and then sign in as the victim (account takeover).
        if (profile.EmailVerified && !string.IsNullOrWhiteSpace(profile.Email))
        {
            var byEmail = await users.FindByEmailAsync(profile.Email);
            if (byEmail is not null)
            {
                await users.AddLoginAsync(byEmail, new UserLoginInfo(Provider, profile.Sub, Provider));
                if (!byEmail.EmailConfirmed)
                {
                    byEmail.EmailConfirmed = true;
                    await users.UpdateAsync(byEmail);
                }
                return byEmail;
            }
        }

        var username = await AllocateUsernameAsync(profile);
        var user = new AppUser
        {
            UserName = username,
            Email = profile.Email,
            // Trust the address only if Google verified it; an unverified email must not
            // silently confirm a new account.
            EmailConfirmed = profile.EmailVerified
        };

        var create = await users.CreateAsync(user);
        if (!create.Succeeded)
            throw new InvalidOperationException(string.Join("; ", create.Errors.Select(e => e.Description)));

        await users.AddLoginAsync(user, new UserLoginInfo(Provider, profile.Sub, Provider));
        return user;
    }

    private async Task<string> AllocateUsernameAsync(GoogleProfile profile)
    {
        var seed = profile.Name?.Trim();
        if (string.IsNullOrWhiteSpace(seed) && !string.IsNullOrWhiteSpace(profile.Email))
            seed = profile.Email.Split('@')[0];

        seed ??= "user";
        seed = SanitizeUsername(seed);
        if (seed.Length < 3)
            seed = "user";

        var candidate = seed;
        for (var i = 0; i < 100; i++)
        {
            if (await users.FindByNameAsync(candidate) is null)
                return candidate;
            candidate = $"{seed}{RandomNumberGenerator.GetInt32(1000, 9999)}";
            if (candidate.Length > 32)
                candidate = candidate[..32];
        }

        return $"user{Guid.NewGuid():N}"[..12];
    }

    private static string SanitizeUsername(string value)
    {
        var chars = value
            .Where(c => char.IsLetterOrDigit(c) || c is '_' or '-')
            .ToArray();
        return new string(chars);
    }

    private sealed record GoogleProfile(string Sub, string? Email, string? Name, bool EmailVerified);

    private sealed class GoogleTokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    }

    private sealed class GoogleUserInfoResponse
    {
        [JsonPropertyName("sub")] public string? Sub { get; set; }
        [JsonPropertyName("email")] public string? Email { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("email_verified")] public bool EmailVerified { get; set; }
    }
}
