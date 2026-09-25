using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
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

    /// <summary>The terms version the launcher showed before starting, when the user accepted
    /// them. Recorded on the account only if this sign-in creates it.</summary>
    public string? AcceptedTermsVersion { get; init; }
}

public sealed class GoogleAuthService(
    GoogleAuthOptions google,
    AppOptions app,
    IHttpClientFactory httpFactory,
    UserManager<AppUser> users,
    JwtTokenService tokens,
    AppDbContext db,
    ILogger<GoogleAuthService> log)
{
    private static readonly ConcurrentDictionary<string, GoogleAuthPendingResult> Pending = new();

    /// <summary>Started flows, oldest first, so expired ones are found at the front instead of by
    /// walking the whole dictionary on every start. Guarded by <see cref="StartOrderLock"/>.</summary>
    private static readonly Queue<(string State, DateTimeOffset ExpiresAt)> StartOrder = new();
    private static readonly object StartOrderLock = new();

    /// <summary>How many sign-ins may be waiting at once. Every start holds a little memory until it
    /// expires, so the total needs a ceiling.</summary>
    private const int MaxPending = 2000;

    public const string Provider = "Google";
    private static readonly TimeSpan PendingTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ResultTtl = TimeSpan.FromMinutes(2);

    private const string GenericFailure = "Could not complete Google sign-in. Try again from CloudLauncher.";
    private const string UsernameAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(google.ClientId) && !string.IsNullOrWhiteSpace(google.ClientSecret);

    public string RedirectUri =>
        $"{app.PublicBaseUrl.TrimEnd('/')}/auth/google/callback";

    /// <summary>Begins a sign-in, or returns null when too many are already waiting.</summary>
    /// <param name="acceptedTermsVersion">The terms version the user accepted in the launcher, if it
    /// asked. Null from launchers that do not.</param>
    public (string AuthUrl, string State)? TryStart(string? acceptedTermsVersion = null)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Google sign-in is not configured on the server.");

        var now = DateTimeOffset.UtcNow;
        string state;
        lock (StartOrderLock)
        {
            EvictExpired(now);
            if (Pending.Count >= MaxPending) return null;

            state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var expires = now.Add(PendingTtl);
            Pending[state] = new GoogleAuthPendingResult { ExpiresAt = expires, AcceptedTermsVersion = acceptedTermsVersion };
            StartOrder.Enqueue((state, expires));
        }

        var query = new Dictionary<string, string?>
        {
            ["client_id"] = google.ClientId,
            ["redirect_uri"] = RedirectUri,
            ["response_type"] = "code",
            // Only the account id and address. The display name is not used, so it is not asked for.
            ["scope"] = "openid email",
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
            Pending.TryRemove(new KeyValuePair<string, GoogleAuthPendingResult>(state, result));
            return new GoogleAuthPendingResult { Complete = true, Error = "Sign-in session expired." };
        }

        // A finished sign-in is handed out once. The state also sits in the browser's history, so
        // leaving the tokens collectable would let anyone who reads it sign in as well.
        if (result.Complete)
            Pending.TryRemove(new KeyValuePair<string, GoogleAuthPendingResult>(state, result));

        return result;
    }

    /// <summary>Removes every expired flow. Run now and then by <see cref="AuthMaintenance"/>, so
    /// flows nobody comes back for do not wait for the next sign-in to be cleared.</summary>
    public static int RemoveExpired()
    {
        var now = DateTimeOffset.UtcNow;
        var removed = 0;
        foreach (var entry in Pending)
            if (entry.Value.ExpiresAt < now && Pending.TryRemove(entry))
                removed++;
        lock (StartOrderLock) EvictExpired(now);
        return removed;
    }

    /// <summary>Drops expired flows from the front of <see cref="StartOrder"/>. Call under the lock.</summary>
    private static void EvictExpired(DateTimeOffset now)
    {
        while (StartOrder.TryPeek(out var head) && head.ExpiresAt < now)
        {
            StartOrder.Dequeue();
            if (!Pending.TryGetValue(head.State, out var entry)) continue;
            if (entry.ExpiresAt < now)
                Pending.TryRemove(new KeyValuePair<string, GoogleAuthPendingResult>(head.State, entry));
            else
                // It finished late and its result is still waiting to be collected.
                StartOrder.Enqueue((head.State, entry.ExpiresAt));
        }
    }

    /// <summary>Finishes a sign-in from Google's redirect.</summary>
    /// <returns>Null when it worked, otherwise the sentence for the browser page. The launcher gets
    /// the same sentence from its next poll.</returns>
    public async Task<string?> HandleCallbackAsync(string? code, string? state, string? error, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(state)
            || !Pending.TryGetValue(state, out var pending)
            || pending.ExpiresAt < DateTimeOffset.UtcNow)
            return "This sign-in has expired. Start again from CloudLauncher.";

        // The page was reloaded after the sign-in already finished.
        if (pending.Complete)
            return pending.Error;

        if (!string.IsNullOrEmpty(error))
            return Fail(state, "Google sign-in was cancelled.");

        if (string.IsNullOrEmpty(code))
            return Fail(state, "Missing authorization code from Google.");

        try
        {
            var profile = await ExchangeCodeAsync(code, ct);
            if (profile is null)
                return Fail(state, GenericFailure);

            var (user, refusal) = await FindOrCreateUserAsync(profile, pending.AcceptedTermsVersion, ct);
            if (user is null)
                return Fail(state, refusal ?? GenericFailure);

            var tokenResponse = await tokens.IssueAsync(user, ct);
            Pending[state] = new GoogleAuthPendingResult
            {
                Complete = true,
                Tokens = tokenResponse,
                ExpiresAt = DateTimeOffset.UtcNow.Add(ResultTtl)
            };
            return null;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Google sign-in failed.");
            return Fail(state, GenericFailure);
        }
    }

    private static string Fail(string state, string message)
    {
        Pending[state] = new GoogleAuthPendingResult
        {
            Complete = true,
            Error = message,
            ExpiresAt = DateTimeOffset.UtcNow.Add(ResultTtl)
        };
        return message;
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
        {
            log.LogWarning("Google token exchange answered {Status}.", (int)tokenResp.StatusCode);
            return null;
        }

        var tokenPayload = await tokenResp.Content.ReadFromJsonAsync<GoogleTokenResponse>(ct);
        if (string.IsNullOrEmpty(tokenPayload?.AccessToken))
            return null;

        using var userReq = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/oauth2/v3/userinfo");
        userReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenPayload.AccessToken);
        using var userResp = await http.SendAsync(userReq, ct);
        if (!userResp.IsSuccessStatusCode)
        {
            log.LogWarning("Google userinfo answered {Status}.", (int)userResp.StatusCode);
            return null;
        }

        var info = await userResp.Content.ReadFromJsonAsync<GoogleUserInfoResponse>(ct);
        if (info is null || string.IsNullOrEmpty(info.Sub))
            return null;

        return new GoogleProfile(info.Sub, info.Email, info.EmailVerified);
    }

    /// <summary>The account this Google identity signs in to, creating one on first use.</summary>
    /// <returns>The user, or null with the sentence to show (null there means the generic one).</returns>
    private async Task<(AppUser? User, string? Refusal)> FindOrCreateUserAsync(
        GoogleProfile profile, string? acceptedTermsVersion, CancellationToken ct)
    {
        var linked = await users.FindByLoginAsync(Provider, profile.Sub);
        if (linked is not null)
            return AccountStatus.IsDisabled(linked) ? (null, "This account has been disabled.") : (linked, null);

        if (string.IsNullOrWhiteSpace(profile.Email))
            return (null, "Google did not share an email address for this account.");

        // A matching address is never enough to attach a Google identity to an existing account:
        // whoever controls the Google side would get into it. The owner signs in the usual way.
        var existing = await users.FindByEmailAsync(profile.Email);
        if (existing is not null)
        {
            // An address Google has not verified proves nothing, so it is told nothing either.
            if (!profile.EmailVerified) return (null, null);
            return (null, await users.HasPasswordAsync(existing)
                ? "An account with this email already exists. Sign in with your username and password instead."
                : "This email is already used by a different CloudLauncher account.");
        }

        if (await AccountSignups.AreClosedAsync(db, ct))
            return (null, "Sign-ups are closed right now.");

        var username = await NewUsernameAsync();
        if (username is null)
        {
            log.LogWarning("Could not find a free generated username for a new Google account.");
            return (null, null);
        }

        var user = new AppUser
        {
            UserName = username,
            Email = profile.Email,
            // Only an address Google verified counts as confirmed.
            EmailConfirmed = profile.EmailVerified,
            TermsAcceptedAt = acceptedTermsVersion is null ? null : DateTimeOffset.UtcNow,
            TermsVersion = acceptedTermsVersion
        };

        var create = await users.CreateAsync(user);
        if (!create.Succeeded)
        {
            log.LogWarning("Creating an account for a Google sign-in failed: {Codes}",
                string.Join(", ", create.Errors.Select(e => e.Code)));
            return (null, null);
        }

        var link = await users.AddLoginAsync(user, new UserLoginInfo(Provider, profile.Sub, Provider));
        if (!link.Succeeded)
        {
            // An account nobody can sign in to is worse than none.
            await users.DeleteAsync(user);
            log.LogWarning("Linking a new account to its Google sign-in failed: {Codes}",
                string.Join(", ", link.Errors.Select(e => e.Code)));
            return (null, null);
        }

        return (user, null);
    }

    /// <summary>"player" plus six random letters and digits.</summary>
    /// <remarks>New Google accounts are not named after the Google profile: usernames are public
    /// through search and every share list, and a real name should not end up there unasked.
    /// People can rename themselves afterwards.</remarks>
    private async Task<string?> NewUsernameAsync()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var candidate = "player" + RandomNumberGenerator.GetString(UsernameAlphabet, 6);
            if (await users.FindByNameAsync(candidate) is null)
                return candidate;
        }
        return null;
    }

    private sealed record GoogleProfile(string Sub, string? Email, bool EmailVerified);

    private sealed class GoogleTokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    }

    private sealed class GoogleUserInfoResponse
    {
        [JsonPropertyName("sub")] public string? Sub { get; set; }
        [JsonPropertyName("email")] public string? Email { get; set; }
        [JsonPropertyName("email_verified")] public bool EmailVerified { get; set; }
    }
}
