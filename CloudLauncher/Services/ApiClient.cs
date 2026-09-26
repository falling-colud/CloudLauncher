using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

public sealed class ApiException(string message, HttpStatusCode status) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>Thrown when the refresh token is expired/revoked. UI should navigate to login.</summary>
public sealed class SessionExpiredException() : Exception("Session expired - please log in again.");

/// <summary>
/// Thrown when a call couldn't reach the server, or reached something that wasn't it.
/// </summary>
/// <remarks>
/// Unlike <see cref="SessionExpiredException"/>, nothing has been cleared and the same call will
/// work again when the network does. Callers with cached data should show it; otherwise say
/// "offline", never "log in again".
/// </remarks>
public sealed class OfflineException(string? reason = null) : Exception(
    reason is { Length: > 0 } r
        ? "CloudLauncher could not reach the server: " + r + "."
        : "CloudLauncher could not reach the server.")
{
    /// <summary>The short phrase naming the cause, e.g. "the connection was refused".</summary>
    public string? Reason { get; } = reason;
}

/// <summary>Which pace a store call through <see cref="ApiClient.ProxyAsync"/> keeps.</summary>
public enum ProxyPacing
{
    /// <summary>The steady trickle every store call keeps: about 4.5 a second to Modrinth and 6 to
    /// CurseForge, a few in flight.</summary>
    Default,

    /// <summary>A one-mod-at-a-time request made by an update check, paced by
    /// <see cref="AppSettings.ModUpdateChecksPerSecond"/> as one budget across both stores and read
    /// live, so moving the setting during a check changes the pace of the rest of it. Searches, pages
    /// and downloads never use it.</summary>
    UpdateCheck
}

/// <summary>A store call is waiting to be retried: the store, or the launcher server in front of it,
/// asked for a pause. Raised by <see cref="ApiClient.StoreWaiting"/> so a page can say so.</summary>
/// <param name="Store">"CurseForge" or "Modrinth".</param>
/// <param name="Delay">How long the call waits before its next attempt.</param>
/// <param name="Attempt">The attempt that just failed, counted from one.</param>
/// <param name="Attempts">How many attempts the call gets in all.</param>
public readonly record struct StoreWait(string Store, TimeSpan Delay, int Attempt, int Attempts)
{
    /// <summary>"CurseForge is busy, trying again..." with the wait named when it is long enough to
    /// notice.</summary>
    public string Describe() => Delay < TimeSpan.FromSeconds(4)
        ? $"{Store} is busy, trying again..."
        : $"{Store} is busy, trying again in {(int)Math.Round(Delay.TotalSeconds)} s...";
}

public sealed partial class ApiClient
{
    private readonly HttpClient _http;
    private readonly AppSettings _settings;
    private readonly ConnectivityState _connectivity = new();
    // Serializes token refresh so concurrent calls near expiry don't each POST auth/refresh with the
    // same refresh token, which is rotated on use: all but one would fail and force a spurious logout.
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new(System.Text.Json.JsonSerializerDefaults.Web);

    // Raised on the calling thread when the refresh token itself has expired.
    // The handler should navigate to the login screen.
    public event Action? SessionExpired;

    public ApiClient(AppSettings settings)
    {
        _settings = settings;
        var sockets = new SocketsHttpHandler
        {
            // The overall timeout stays at five minutes for blob transfers, but a blackholed route
            // shouldn't take that long to detect: a handshake happens within seconds or not at all.
            ConnectTimeout = TimeSpan.FromSeconds(8)
        };
        _connectivity.Changed += online => ConnectivityChanged?.Invoke(online);
        _http = new HttpClient(new TransportGuard(this, new MutationWatcher(this, new ConnectivityHandler(_connectivity, sockets))))
            { BaseAddress = new Uri(settings.ServerUrl.TrimEnd('/') + "/") };
        _http.Timeout = TimeSpan.FromMinutes(5);
        _credentialsAllowed = AppSettings.IsSecureServerUri(_http.BaseAddress!);
        // Identifies the launcher. The server treats requests without a User-Agent as pre-0.8.4
        // launchers and offers them releases under their old-line number (see AppVersion.Rank).
        ApplyUserAgent(_http);
        if (_credentialsAllowed && !string.IsNullOrEmpty(settings.AccessToken))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.AccessToken);
    }

    /// <summary>False when the server address is plain http to another machine: no password or token
    /// is sent to it then (see <see cref="TransportGuard"/>).</summary>
    private readonly bool _credentialsAllowed;

    public string ServerUrl => _settings.ServerUrl;

    // ── connectivity ────────────────────────────────────────────────────────

    /// <summary>True when the launcher's own traffic is not getting answers.</summary>
    /// <remarks>Set by <see cref="ConnectivityHandler"/> from real requests, not from the network
    /// adapter: a captive portal that answers everything reads as online, and so does a VPN that reports
    /// "no network" while calls succeed through it.</remarks>
    public bool IsOffline => _connectivity.IsOffline;

    /// <summary>Short phrase naming why, e.g. "the connection was refused". Null while online.</summary>
    public string? OfflineReason => _connectivity.OfflineReason;

    /// <summary>When the launcher first noticed it was out. Null while online.</summary>
    public DateTimeOffset? OfflineSince => _connectivity.OfflineSince;

    /// <summary>Raised when the state flips; true means online. Raised on whichever thread made the
    /// call that noticed; subscribe through <see cref="AppState"/> for a UI-thread version.</summary>
    public event Action<bool>? ConnectivityChanged;

    /// <summary>Records the failure and hands back the exception to throw for it.</summary>
    private OfflineException GoOffline(string reason)
    {
        _connectivity.MarkOffline(reason);
        return new OfflineException(reason);
    }

    public void SetTokens(TokenResponse tokens)
    {
        // A different account on this profile: the remembered instance list, details and teams belong to
        // the previous account and pages paint from them before the server answers, so drop them first.
        if (_settings.UserId is { } previous && previous != tokens.UserId) ForgetAccountCaches();
        _settings.SetSessionTokens(tokens.AccessToken, tokens.RefreshToken);
        _settings.AccessTokenExpiresAt = tokens.AccessTokenExpiresAt;
        _settings.Username = tokens.Username;
        _settings.UserId = tokens.UserId;
        _settings.Save();
        _http.DefaultRequestHeaders.Authorization = _credentialsAllowed
            ? new AuthenticationHeaderValue("Bearer", tokens.AccessToken)
            : null;
    }

    public void ClearTokens()
    {
        _settings.SetSessionTokens(null, null);
        _settings.AccessTokenExpiresAt = null;
        _settings.Username = null;
        _settings.UserId = null;
        _settings.Save();
        _http.DefaultRequestHeaders.Authorization = null;
        // Signed out (or the server refused the session): whoever signs in next must not be shown this
        // account's instances while their own list is on its way.
        ForgetAccountCaches();
    }

    /// <summary>Drops everything remembered about the signed-in account's instances and teams.</summary>
    private void ForgetAccountCaches()
    {
        ForgetRecentPacks();
        PackListCache.Clear();
        PackDetailCache.Clear();
        TeamListCache.Clear();
    }

    // ── token management ────────────────────────────────────────────────────

    /// <summary>
    /// Proactively refresh the access token if it is expired or within 2 minutes of expiry.
    /// </summary>
    /// <remarks>
    /// Throws <see cref="SessionExpiredException"/> (clearing the stored tokens and firing
    /// <see cref="SessionExpired"/>) only when the server rejects the refresh token. When the refresh
    /// can't be asked at all it throws <see cref="OfflineException"/> and touches nothing, so time spent
    /// offline never signs the user out.
    /// </remarks>
    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        // Signed in, but the server address is plain http: the session stays stored and unused, and
        // the call says why instead of going out without it.
        if (!_credentialsAllowed && _settings.HasStoredSession) throw InsecureTransport();
        if (string.IsNullOrEmpty(_settings.AccessToken)) return; // not logged in; let the request fail naturally
        if (!IsNearExpiry()) return; // token is fresh, nothing to do (fast path, no lock)

        await _refreshLock.WaitAsync(ct);
        try
        {
            // Re-check under the lock: a concurrent caller may have already refreshed (or cleared)
            // the token while we waited, in which case there is nothing for us to do.
            if (string.IsNullOrEmpty(_settings.AccessToken)) return;
            if (!IsNearExpiry()) return;

            if (string.IsNullOrEmpty(_settings.RefreshToken))
            {
                ClearTokens();
                SessionExpired?.Invoke();
                throw new SessionExpiredException();
            }

            try
            {
                // Call the refresh endpoint directly, bypassing EnsureTokenAsync to avoid recursion
                using var resp = await _http.PostAsJsonAsync("auth/refresh",
                    new RefreshRequest(_settings.RefreshToken), JsonOpts, ct);

                if (!resp.IsSuccessStatusCode)
                {
                    // Only the server rejecting the identity ends the session. A 502 from a restarting
                    // API, a 429 or a captive portal's page is not our auth endpoint answering.
                    if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    {
                        ClearTokens();
                        SessionExpired?.Invoke();
                        throw new SessionExpiredException();
                    }

                    var reason = resp.ReasonPhrase is { Length: > 0 } rp ? rp : resp.StatusCode.ToString();
                    throw GoOffline($"the server answered {(int)resp.StatusCode} {reason}");
                }

                var tokens = await resp.Content.ReadFromJsonAsync<TokenResponse>(JsonOpts, ct);
                if (tokens is null)
                    throw GoOffline("the server's reply to the refresh was empty");
                SetTokens(tokens);
            }
            catch (SessionExpiredException) { throw; }
            catch (OfflineException) { throw; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Anything else is a failure to ask (no route, timeout, reset socket, or portal HTML
                // that JSON can't parse) and says nothing about the refresh token, so keep it.
                throw GoOffline(Connectivity.DescribeTransportFailure(ex, ct) ?? "the reply could not be understood");
            }
        }
        finally { _refreshLock.Release(); }
    }

    private bool IsNearExpiry()
    {
        var expiresAt = _settings.AccessTokenExpiresAt;
        return expiresAt is null || expiresAt.Value <= DateTimeOffset.UtcNow.AddMinutes(2);
    }

    // ── auth ────────────────────────────────────────────────────────────────

    public async Task<RegisterPendingResponse> RegisterAsync(RegisterRequest req, CancellationToken ct = default) =>
        await ReadAsync<RegisterPendingResponse>(await _http.PostAsJsonAsync("auth/register", req, JsonOpts, ct), ct);

    public async Task<TokenResponse> LoginAsync(LoginRequest req, CancellationToken ct = default) =>
        await ReadAsync<TokenResponse>(await _http.PostAsJsonAsync("auth/login", req, JsonOpts, ct), ct);

    public async Task ResendVerificationAsync(ResendVerificationRequest req, CancellationToken ct = default) =>
        await ReadAsync<object>(await _http.PostAsJsonAsync("auth/resend-verification", req, JsonOpts, ct), ct);

    /// <param name="acceptTerms">Recorded by the server if this sign-in creates the account.</param>
    public async Task<GoogleAuthStartResponse> GoogleAuthStartAsync(bool acceptTerms = false, CancellationToken ct = default)
    {
        var url = acceptTerms
            ? $"auth/google/start?acceptTerms=true&termsVersion={Uri.EscapeDataString(Legal.TermsVersion)}"
            : "auth/google/start";
        return await ReadAsync<GoogleAuthStartResponse>(await _http.GetAsync(url, ct), ct);
    }

    public async Task<GoogleAuthPollResponse> GoogleAuthPollAsync(string state, CancellationToken ct = default)
    {
        var url = $"auth/google/poll?state={Uri.EscapeDataString(state)}";
        return await ReadAsync<GoogleAuthPollResponse>(await _http.GetAsync(url, ct), ct);
    }

    public async Task<UserSummary> MeAsync(CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        return await ReadAsync<UserSummary>(await _http.GetAsync("auth/me", ct), ct);
    }

    /// <summary>
    /// Changes the password and adopts the fresh token pair the server issues.
    /// </summary>
    /// <remarks>
    /// The change revokes every refresh token on the account, including this session's, so the
    /// replacement pair in the response is stored here to keep the launcher signed in.
    /// </remarks>
    public async Task ChangePasswordAsync(ChangePasswordRequest req, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        var tokens = await ReadAsync<TokenResponse>(
            await _http.PostAsJsonAsync("auth/change-password", req, JsonOpts, ct), ct);
        SetTokens(tokens);
    }

    /// <summary>Signs this session out: revokes its refresh token server-side and forgets both tokens.</summary>
    /// <remarks>
    /// Doesn't call <see cref="EnsureTokenAsync"/>: refreshing a token we're about to discard would turn
    /// an expired session into a spurious "session expired" popup. Stored tokens are cleared even if the
    /// server never answers.
    /// </remarks>
    public async Task LogoutAsync(CancellationToken ct = default)
    {
        var refreshToken = _settings.RefreshToken;
        try
        {
            if (!string.IsNullOrEmpty(refreshToken))
                await EnsureSuccess(
                    await _http.PostAsJsonAsync("auth/logout", new RefreshRequest(refreshToken), JsonOpts, ct));
        }
        finally { ClearTokens(); }
    }

    /// <summary>Signs the account out everywhere, this launcher included.</summary>
    public async Task LogoutAllAsync(CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        try { await EnsureSuccess(await _http.PostAsync("auth/logout-all", content: null, ct)); }
        finally { ClearTokens(); }
    }

    /// <summary>How much server storage this account is using, and its quota if it has one.</summary>
    public async Task<UserStorageUsage> GetMyStorageUsageAsync(CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        return await ReadAsync<UserStorageUsage>(await _http.GetAsync("auth/me/usage", ct), ct);
    }

    /// <summary>Where that storage went: everything the account owns, with what deleting each one
    /// would give back.</summary>
    /// <remarks>
    /// Its <c>UsedBytes</c> matches <see cref="GetMyStorageUsageAsync"/> (same sources), so a page
    /// showing both can't disagree with itself. The per-item columns add up to more, since a blob two
    /// packs share is charged to both.
    /// <para>Older servers answer 404 here. Treat that like a missing <c>auth/me/usage</c>: hide the
    /// section rather than showing an error.</para>
    /// </remarks>
    public async Task<CloudStorageBreakdown> GetMyStorageBreakdownAsync(CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        return await ReadAsync<CloudStorageBreakdown>(
            await _http.GetAsync("auth/me/usage/breakdown", ct), ct);
    }

    // ── packs ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The user's instances. A server that answers is authoritative and replaces the cached copy; one
    /// that can't answer (restarting database, no network) falls back to the last list, with
    /// <see cref="PackListStale"/> set so the UI can say so. An expired session still throws, since it
    /// needs the login screen, not stale data.
    /// </summary>
    public async Task<List<PackSummary>> ListPacksAsync(CancellationToken ct = default)
    {
        try { await EnsureTokenAsync(ct); }
        catch (OfflineException ex)
        {
            // The token couldn't be refreshed because nothing answered, and asking for the list would
            // fail the same way, so go straight to what we can show without a server.
            var offline = CachedOrLocalPacks(ex.Reason ?? "the server is unreachable");
            if (offline is null) throw;
            return offline;
        }

        // Taken before the request goes out: a change made while this GET is in flight must win over
        // the answer it brings back (see RememberPacks).
        var generation = Volatile.Read(ref _packsGeneration);
        try
        {
            var packs = await ReadAsync<List<PackSummary>>(await _http.GetAsync("packs", ct), ct);
            PackListCache.Save(packs);
            PackListStale = null;
            RememberPacks(packs, generation);
            return packs;
        }
        catch (SessionExpiredException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var fallback = CachedOrLocalPacks(
                Connectivity.DescribeTransportFailure(ex, ct) ?? ex.Message);
            if (fallback is null) throw;
            return fallback;
        }
    }

    /// <summary>Why the last instance list came from the cache, or null when it came from the server.</summary>
    /// <remarks>A clause, not a sentence: the pages that show it wrap it in wording of their own.</remarks>
    public string? PackListStale { get; private set; }

    // ── the recent instance list ────────────────────────────────────────────

    /// <summary>How long a page may reuse the instance list the server gave another page.</summary>
    private const long RecentPacksMs = 120_000;

    /// <summary>Past this age a reuse also asks the server again in the background, for next time.</summary>
    private const long RecentPacksRevalidateMs = 15_000;

    private readonly object _recentPacksGate = new();
    private List<PackSummary>? _recentPacks;
    private long _recentPacksAt;
    private Task? _recentPacksRevalidation;

    /// <summary>Bumped by every request that could change the server's answer (anything but a GET), so a
    /// list fetched across a change is never remembered as current.</summary>
    private int _packsGeneration;

    /// <summary>
    /// The instance list for pages that only need to know which instances exist (Worlds, Mods, Resource
    /// packs, Shader packs, Servers and Config enumerate instance folders from it).
    /// </summary>
    /// <remarks>
    /// <para>Answers from the server's last answer if it is under two minutes old, with no network, so
    /// those pages can paint their remembered rows immediately instead of after a round trip. Past a few
    /// seconds old it also re-asks in the background for next time. With nothing recent (first page
    /// after start, or a change made through this client since) it is just
    /// <see cref="ListPacksAsync"/>.</para>
    /// <para>The Instances page doesn't use this; its content is the list, so it always asks.</para>
    /// </remarks>
    public Task<List<PackSummary>> ListPacksQuickAsync(CancellationToken ct = default)
    {
        lock (_recentPacksGate)
        {
            if (_recentPacks is { } recent)
            {
                var age = Environment.TickCount64 - _recentPacksAt;
                if (age < RecentPacksMs)
                {
                    if (age > RecentPacksRevalidateMs && _recentPacksRevalidation is not { IsCompleted: false })
                        _recentPacksRevalidation = Task.Run(RevalidateRecentPacksAsync);
                    return Task.FromResult(new List<PackSummary>(recent));
                }
            }
        }
        return ListPacksAsync(ct);
    }

    private async Task RevalidateRecentPacksAsync()
    {
        try { await ListPacksAsync(); }
        catch (Exception ex) { AppLog.Log("packs", $"Background instance-list refresh failed: {ex.Message}"); }
    }

    /// <summary>Keeps a server answer for <see cref="ListPacksQuickAsync"/>, unless something was changed
    /// while it was on its way.</summary>
    private void RememberPacks(List<PackSummary> packs, int generation)
    {
        lock (_recentPacksGate)
        {
            if (generation != Volatile.Read(ref _packsGeneration)) return;
            _recentPacks = new List<PackSummary>(packs);
            _recentPacksAt = Environment.TickCount64;
        }
    }

    /// <summary>Forgets the remembered list: this client just asked the server to change something, and
    /// the next page to need the list should see the result.</summary>
    public void ForgetRecentPacks()
    {
        Interlocked.Increment(ref _packsGeneration);
        lock (_recentPacksGate) _recentPacks = null;
    }

    /// <summary>Calls <see cref="ForgetRecentPacks"/> around every request that isn't a read.</summary>
    /// <remarks>Blunter than listing the routes that change the instance list, but a route added later
    /// can't forget to invalidate, and a needless invalidation only costs one network wait. Runs before
    /// and after the request, so a list fetched mid-change isn't kept either. Two POST families that
    /// change nothing are exempt: the store proxy (hash and fingerprint lookups) and auth (token
    /// refresh).</remarks>
    private sealed class MutationWatcher(ApiClient owner, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            var mutation = request.Method != HttpMethod.Get && request.Method != HttpMethod.Head
                           && request.Method != HttpMethod.Options
                           && !path.Contains("/proxy/", StringComparison.OrdinalIgnoreCase)
                           && !path.Contains("/auth/", StringComparison.OrdinalIgnoreCase);
            if (!mutation) return await base.SendAsync(request, ct);
            owner.ForgetRecentPacks();
            try { return await base.SendAsync(request, ct); }
            finally { owner.ForgetRecentPacks(); }
        }
    }

    /// <summary>
    /// The best instance list available without the server: the one it last gave, else the folders
    /// on this PC. Null when there is neither, which is the only case left worth throwing over.
    /// </summary>
    private List<PackSummary>? CachedOrLocalPacks(string why)
    {
        var cached = PackListCache.Load();
        if (cached is { Count: > 0 })
        {
            PackListStale = PackListCache.AgeInWords() is { } age ? $"{why}; last updated {age}" : why;
            AppLog.Log("packs", $"Instance list unavailable ({why}); showing the last known {cached.Count}.");
            return cached;
        }

        // Nothing cached (first run offline, or a profile restored without its cache file). The modpacks
        // are still in the packs folder, so list them rather than showing an empty library. Only when
        // someone is signed in: a signed-out launcher shouldn't list what's on disk.
        if (string.IsNullOrEmpty(_settings.RefreshToken) && string.IsNullOrEmpty(_settings.AccessToken))
            return cached;

        var scanned = LocalPackScanner.Scan(_settings);
        if (scanned.Count == 0) return cached; // may be an empty cached list, which is still an answer
        PackListStale = LocalPackScanner.StaleReason;
        AppLog.Log("packs", $"Instance list unavailable ({why}); found {scanned.Count} instance(s) on this PC.");
        return scanned;
    }

    public async Task<PackBrowsePage> BrowsePacksAsync(
        PackBrowseSource source, Guid? teamId = null,
        string? query = null, int offset = 0, int limit = 30,
        CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        var qs = $"packs/browse?source={source}&offset={offset}&limit={limit}";
        if (teamId.HasValue) qs += $"&teamId={teamId.Value}";
        if (!string.IsNullOrWhiteSpace(query)) qs += $"&q={Uri.EscapeDataString(query)}";
        return await ReadAsync<PackBrowsePage>(await _http.GetAsync(qs, ct), ct);
    }

    public async Task<PackSummary> SubscribePackAsync(Guid packId, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        return await ReadAsync<PackSummary>(
            await _http.PostAsync($"packs/{packId}/subscribe", content: null, ct), ct);
    }

    public async Task UnsubscribePackAsync(Guid packId, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        await EnsureSuccess(await _http.PostAsync($"packs/{packId}/unsubscribe", content: null, ct));
    }

    // ── hosted mods ──────────────────────────────────────────────────────────

    /// <param name="sort">One of <see cref="ModBrowseSort"/>, or null for the server's default (most
    /// recently updated). Older servers ignore it and use their own order.</param>
    public async Task<ModBrowsePage> BrowseModsAsync(
        ModBrowseSource source, Guid? teamId = null,
        string? query = null, string? mcVersion = null, string? loader = null,
        int offset = 0, int limit = 25,
        CancellationToken ct = default,
        string? sort = null)
    {
        await EnsureTokenAsync(ct);
        var qs = $"mods/browse?source={source}&offset={offset}&limit={limit}";
        if (teamId.HasValue) qs += $"&teamId={teamId.Value}";
        if (!string.IsNullOrWhiteSpace(query)) qs += $"&q={Uri.EscapeDataString(query)}";
        if (!string.IsNullOrWhiteSpace(mcVersion)) qs += $"&mcVersion={Uri.EscapeDataString(mcVersion)}";
        if (!string.IsNullOrWhiteSpace(loader)) qs += $"&loader={Uri.EscapeDataString(loader)}";
        if (!string.IsNullOrWhiteSpace(sort)) qs += $"&sort={Uri.EscapeDataString(sort)}";
        return await ReadHostedModAsync<ModBrowsePage>(await _http.GetAsync(qs, ct), ct);
    }

    public async Task<HostedModDetail> GetModAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        return await ReadHostedModAsync<HostedModDetail>(await _http.GetAsync($"mods/{id}", ct), ct);
    }

    public async Task<HostedModSummary> CreateModAsync(CreateModRequest req, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        return await ReadHostedModAsync<HostedModSummary>(await _http.PostAsJsonAsync("mods", req, JsonOpts, ct), ct);
    }

    public async Task UpdateModAsync(Guid id, UpdateModRequest req, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        await EnsureHostedModSuccess(await _http.PatchAsJsonAsync($"mods/{id}", req, JsonOpts, ct));
    }

    public async Task DeleteModAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        await EnsureHostedModSuccess(await _http.DeleteAsync($"mods/{id}", ct));
    }

    public async Task<PackCollaboratorEntry> AddModCollaboratorAsync(Guid modId, AddCollaboratorRequest req, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        return await ReadHostedModAsync<PackCollaboratorEntry>(
            await _http.PostAsJsonAsync($"mods/{modId}/collaborators", req, JsonOpts, ct), ct);
    }

    public async Task UpdateModCollaboratorAsync(Guid modId, Guid userId, UpdateCollaboratorRequest req, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        await EnsureHostedModSuccess(await _http.PatchAsJsonAsync($"mods/{modId}/collaborators/{userId}", req, JsonOpts, ct));
    }

    public async Task RemoveModCollaboratorAsync(Guid modId, Guid userId, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        await EnsureHostedModSuccess(await _http.DeleteAsync($"mods/{modId}/collaborators/{userId}", ct));
    }

    public async Task<PackTeamEntry> AddModTeamAsync(Guid modId, AddPackTeamRequest req, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        return await ReadHostedModAsync<PackTeamEntry>(
            await _http.PostAsJsonAsync($"mods/{modId}/teams", req, JsonOpts, ct), ct);
    }

    public async Task UpdateModTeamAsync(Guid modId, Guid teamId, UpdatePackTeamRequest req, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        await EnsureHostedModSuccess(await _http.PatchAsJsonAsync($"mods/{modId}/teams/{teamId}", req, JsonOpts, ct));
    }

    public async Task RemoveModTeamAsync(Guid modId, Guid teamId, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        await EnsureHostedModSuccess(await _http.DeleteAsync($"mods/{modId}/teams/{teamId}", ct));
    }

    /// <param name="progress">Reports total bytes handed to the socket so far, or null for no reporting.</param>
    public async Task<HostedModVersionInfo> UploadModVersionAsync(
        Guid modId, string filePath, CreateModVersionRequest meta, CancellationToken ct = default,
        IProgress<long>? progress = null)
    {
        await EnsureTokenAsync(ct);
        using var form = new MultipartFormDataContent();
        await using var fs = File.OpenRead(filePath);
        var fileContent = new StreamContent(new ProgressStream(fs, progress));
        fileContent.Headers.ContentType = new("application/java-archive");
        form.Add(fileContent, "file", Path.GetFileName(filePath));
        var metaJson = System.Text.Json.JsonSerializer.Serialize(meta, JsonOpts);
        form.Add(new StringContent(metaJson), "metadata");
        return await ReadHostedModAsync<HostedModVersionInfo>(
            await _http.PostAsync($"mods/{modId}/versions", form, ct), ct);
    }

    /// <summary>Corrects an uploaded version's details without touching its file.</summary>
    /// <remarks>Null fields keep whatever the server has, so a caller that only means to fix one
    /// field cannot blank the others by omission.</remarks>
    public async Task<HostedModVersionInfo> UpdateModVersionAsync(
        Guid modId, Guid versionId, UpdateModVersionRequest req, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        return await ReadHostedModAsync<HostedModVersionInfo>(
            await _http.PatchAsJsonAsync($"mods/{modId}/versions/{versionId}", req, JsonOpts, ct), ct);
    }

    /// <summary>Deletes one uploaded version of a hosted mod. Deleting the last one keeps the mod.</summary>
    public async Task DeleteModVersionAsync(Guid modId, Guid versionId, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        await EnsureHostedModSuccess(await _http.DeleteAsync($"mods/{modId}/versions/{versionId}", ct));
    }

    /// <param name="progress">Reports (bytes read, total bytes) as the caller drains the returned
    /// stream. Total is the response's Content-Length, or -1 when the server didn't send one.</param>
    /// <remarks>
    /// Measured as the caller reads rather than by buffering here, since the caller is already copying
    /// the stream to disk. A null reporter returns the stream unwrapped.
    /// </remarks>
    public async Task<Stream> DownloadModVersionAsync(
        Guid modId, Guid versionId, CancellationToken ct = default,
        IProgress<(long done, long total)>? progress = null)
    {
        await EnsureTokenAsync(ct);
        var resp = await _http.GetAsync($"mods/{modId}/files/{versionId}",
            HttpCompletionOption.ResponseHeadersRead, ct);
        // A version that has just been deleted is a 404 from a route that exists; only an empty one
        // means this server has no /mods at all. See ThrowHostedModNotFoundAsync.
        if (resp.StatusCode == HttpStatusCode.NotFound) await ThrowHostedModNotFoundAsync(resp, ct);
        await EnsureSuccessKeepBody(resp, ct); // keep the response; we stream its body to the caller
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        if (progress is null) return stream;
        return new ProgressReadStream(stream, resp.Content.Headers.ContentLength ?? -1, progress);
    }

    // ── hosted icons ─────────────────────────────────────────────────────────

    /// <summary>The address of a hosted mod's icon on this server.</summary>
    /// <remarks>
    /// Useful directly only for a public mod: the route is anonymous for those, and a WPF
    /// <c>&lt;Image&gt;</c> binding sends no Authorization header. For anything private or shared,
    /// use <see cref="GetModIconFileAsync"/>, which fetches the bytes with the session's token and
    /// hands back a local file the same binding can show.
    /// </remarks>
    public string ModIconUrl(Guid modId) => $"{ServerUrl.TrimEnd('/')}/mods/{modId}/icon";

    public string ResourcePackIconUrl(Guid packId) => $"{ServerUrl.TrimEnd('/')}/resourcepacks/{packId}/icon";

    public string SharedWorldIconUrl(Guid worldId) => $"{ServerUrl.TrimEnd('/')}/worlds/{worldId}/icon";

    /// <summary>Uploads a new icon for a hosted mod, replacing any current one.</summary>
    public Task UploadModIconAsync(Guid modId, string filePath, CancellationToken ct = default) =>
        UploadIconAsync($"mods/{modId}/icon", filePath, ct);

    public Task UploadResourcePackIconAsync(Guid packId, string filePath, CancellationToken ct = default) =>
        UploadIconAsync($"resourcepacks/{packId}/icon", filePath, ct);

    public Task UploadSharedWorldIconAsync(Guid worldId, string filePath, CancellationToken ct = default) =>
        UploadIconAsync($"worlds/{worldId}/icon", filePath, ct);

    /// <summary>Clears a hosted mod's icon, putting it back to its letter tile.</summary>
    public async Task DeleteModIconAsync(Guid modId, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        await EnsureHostedModSuccess(await _http.DeleteAsync($"mods/{modId}/icon", ct));
    }

    public async Task DeleteResourcePackIconAsync(Guid packId, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        await EnsureSuccess(await _http.DeleteAsync($"resourcepacks/{packId}/icon", ct));
    }

    public async Task DeleteSharedWorldIconAsync(Guid worldId, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        await EnsureSuccess(await _http.DeleteAsync($"worlds/{worldId}/icon", ct));
    }

    /// <summary>
    /// A local file holding a hosted mod's icon, or null when it has none or can't be fetched.
    /// </summary>
    /// <param name="iconBlobHash">The hash from the summary/detail. Null means "no icon" and answers
    /// null without a round trip.</param>
    /// <remarks>
    /// <para>The icon route needs auth for anything non-public, and a WPF image binding can't carry a
    /// token, so the bytes are fetched here and written to a file the binding can point at. A token in
    /// the URL would leak into logs and proxies.</para>
    /// <para>The cache is keyed by blob hash, so a changed icon is a different file and nothing goes
    /// stale. Failures return null and cache nothing, so they're retried next time.</para>
    /// </remarks>
    public Task<string?> GetModIconFileAsync(Guid modId, string? iconBlobHash, CancellationToken ct = default) =>
        GetIconFileAsync($"mods/{modId}/icon", iconBlobHash, ct);

    public Task<string?> GetResourcePackIconFileAsync(Guid packId, string? iconBlobHash, CancellationToken ct = default) =>
        GetIconFileAsync($"resourcepacks/{packId}/icon", iconBlobHash, ct);

    public Task<string?> GetSharedWorldIconFileAsync(Guid worldId, string? iconBlobHash, CancellationToken ct = default) =>
        GetIconFileAsync($"worlds/{worldId}/icon", iconBlobHash, ct);

    /// <summary>Where downloaded icons are kept. Content-addressed, so it never needs clearing.</summary>
    private static string IconCacheDir => Path.Combine(AppSettings.DataRootPath, "icon-cache");

    private async Task UploadIconAsync(string route, string filePath, CancellationToken ct)
    {
        await EnsureTokenAsync(ct);
        using var form = new MultipartFormDataContent();
        await using var fs = File.OpenRead(filePath);
        var content = new StreamContent(fs);
        content.Headers.ContentType = new(
            Path.GetExtension(filePath).Equals(".png", StringComparison.OrdinalIgnoreCase)
                ? "image/png"
                : "image/jpeg");
        form.Add(content, "file", Path.GetFileName(filePath));
        await EnsureSuccess(await _http.PostAsync(route, form, ct));
    }

    private async Task<string?> GetIconFileAsync(string route, string? iconBlobHash, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(iconBlobHash)) return null;

        // Only hex is ever a blob hash; anything else would be a path fragment, not a file name.
        if (!iconBlobHash.All(Uri.IsHexDigit)) return null;
        var cached = Path.Combine(IconCacheDir, iconBlobHash + ".img");
        if (File.Exists(cached)) return cached;

        try
        {
            await EnsureTokenAsync(ct);
            using var resp = await _http.GetAsync(route, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return null;

            Directory.CreateDirectory(IconCacheDir);
            // Written to a temp name and moved into place so a cancelled or failed download can
            // never leave a half-written file that later looks like a cache hit.
            var tmp = cached + "." + Guid.NewGuid().ToString("N")[..8] + ".part";
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tmp))
                await src.CopyToAsync(dst, ct);

            try { File.Move(tmp, cached, overwrite: true); }
            catch { try { File.Delete(tmp); } catch { } }
            return File.Exists(cached) ? cached : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; } // an icon is decoration: never fail the screen that wanted it
    }


    // ── shared worlds ────────────────────────────────────────────────────────

    public async Task<WorldBrowsePage> BrowseWorldsAsync(
        WorldBrowseSource source, Guid? teamId = null,
        string? query = null, string? mcVersion = null,
        int offset = 0, int limit = 25,
        CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        var qs = $"worlds/browse?source={source}&offset={offset}&limit={limit}";
        if (teamId.HasValue) qs += $"&teamId={teamId.Value}";
        if (!string.IsNullOrWhiteSpace(query)) qs += $"&q={Uri.EscapeDataString(query)}";
        if (!string.IsNullOrWhiteSpace(mcVersion)) qs += $"&mcVersion={Uri.EscapeDataString(mcVersion)}";
        return await ReadAsync<WorldBrowsePage>(await _http.GetAsync(qs, ct), ct);
    }

    public async Task<SharedWorldDetail> GetSharedWorldAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        return await ReadAsync<SharedWorldDetail>(await _http.GetAsync($"worlds/{id}", ct), ct);
    }

    public async Task<SharedWorldSummary> CreateSharedWorldAsync(CreateWorldRequest req, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        return await ReadAsync<SharedWorldSummary>(await _http.PostAsJsonAsync("worlds", req, JsonOpts, ct), ct);
    }

    public async Task UpdateSharedWorldAsync(Guid id, UpdateWorldRequest req, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        await EnsureSuccess(await _http.PatchAsJsonAsync($"worlds/{id}", req, JsonOpts, ct));
    }

    public async Task DeleteSharedWorldAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        await EnsureSuccess(await _http.DeleteAsync($"worlds/{id}", ct));
    }

    public async Task<PackCollaboratorEntry> AddSharedWorldCollaboratorAsync(Guid worldId, AddCollaboratorRequest req, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        return await ReadAsync<PackCollaboratorEntry>(
            await _http.PostAsJsonAsync($"worlds/{worldId}/collaborators", req, JsonOpts, ct), ct);
    }

    public async Task UpdateSharedWorldCollaboratorAsync(Guid worldId, Guid userId, UpdateCollaboratorRequest req, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        await EnsureSuccess(await _http.PatchAsJsonAsync($"worlds/{worldId}/collaborators/{userId}", req, JsonOpts, ct));
    }

    public async Task RemoveSharedWorldCollaboratorAsync(Guid worldId, Guid userId, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        await EnsureSuccess(await _http.DeleteAsync($"worlds/{worldId}/collaborators/{userId}", ct));
    }

    public async Task<PackTeamEntry> AddSharedWorldTeamAsync(Guid worldId, AddPackTeamRequest req, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        return await ReadAsync<PackTeamEntry>(
            await _http.PostAsJsonAsync($"worlds/{worldId}/teams", req, JsonOpts, ct), ct);
    }

    public async Task UpdateSharedWorldTeamAsync(Guid worldId, Guid teamId, UpdatePackTeamRequest req, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        await EnsureSuccess(await _http.PatchAsJsonAsync($"worlds/{worldId}/teams/{teamId}", req, JsonOpts, ct));
    }

    public async Task RemoveSharedWorldTeamAsync(Guid worldId, Guid teamId, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        await EnsureSuccess(await _http.DeleteAsync($"worlds/{worldId}/teams/{teamId}", ct));
    }

    /// <param name="progress">Reports total bytes handed to the socket so far, or null for no reporting.</param>
    public async Task<SharedWorldVersionInfo> UploadSharedWorldVersionAsync(
        Guid worldId, string filePath, CreateWorldVersionRequest meta, CancellationToken ct = default,
        IProgress<long>? progress = null)
    {
        await EnsureTokenAsync(ct);
        using var form = new MultipartFormDataContent();
        await using var fs = File.OpenRead(filePath);
        var fileContent = new StreamContent(new ProgressStream(fs, progress));
        fileContent.Headers.ContentType = new("application/zip");
        form.Add(fileContent, "file", Path.GetFileName(filePath));
        var metaJson = System.Text.Json.JsonSerializer.Serialize(meta, JsonOpts);
        form.Add(new StringContent(metaJson), "metadata");
        return await ReadAsync<SharedWorldVersionInfo>(
            await _http.PostAsync($"worlds/{worldId}/versions", form, ct), ct);
    }

    /// <summary>Deletes one uploaded version of a shared world. Deleting the last one keeps the world.</summary>
    public async Task DeleteSharedWorldVersionAsync(Guid worldId, Guid versionId, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        await EnsureSuccess(await _http.DeleteAsync($"worlds/{worldId}/versions/{versionId}", ct));
    }

    public async Task<Stream> DownloadSharedWorldVersionAsync(Guid worldId, Guid versionId, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        var resp = await _http.GetAsync($"worlds/{worldId}/files/{versionId}",
            HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessKeepBody(resp, ct);
        return await resp.Content.ReadAsStreamAsync(ct);
    }

    // ── hosted resource packs ──────────────────────────────────────────────────

    public async Task<ResourcePackBrowsePage> BrowseResourcePacksAsync(
        ResourcePackBrowseSource source, Guid? teamId = null,
        string? query = null, string? mcVersion = null,
        int offset = 0, int limit = 25,
        CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        var qs = $"resourcepacks/browse?source={source}&offset={offset}&limit={limit}";
        if (teamId.HasValue) qs += $"&teamId={teamId.Value}";
        if (!string.IsNullOrWhiteSpace(query)) qs += $"&q={Uri.EscapeDataString(query)}";
        if (!string.IsNullOrWhiteSpace(mcVersion)) qs += $"&mcVersion={Uri.EscapeDataString(mcVersion)}";
        return await ReadAsync<ResourcePackBrowsePage>(await _http.GetAsync(qs, ct), ct);
    }

    public async Task<HostedResourcePackDetail> GetResourcePackAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        return await ReadAsync<HostedResourcePackDetail>(await _http.GetAsync($"resourcepacks/{id}", ct), ct);
    }

    public async Task<HostedResourcePackSummary> CreateResourcePackAsync(CreateResourcePackRequest req, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        return await ReadAsync<HostedResourcePackSummary>(await _http.PostAsJsonAsync("resourcepacks", req, JsonOpts, ct), ct);
    }

    public async Task UpdateResourcePackAsync(Guid id, UpdateResourcePackRequest req, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        await EnsureSuccess(await _http.PatchAsJsonAsync($"resourcepacks/{id}", req, JsonOpts, ct));
    }

    public async Task DeleteResourcePackAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        await EnsureSuccess(await _http.DeleteAsync($"resourcepacks/{id}", ct));
    }

    public async Task<PackCollaboratorEntry> AddResourcePackCollaboratorAsync(Guid packId, AddCollaboratorRequest req, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        return await ReadAsync<PackCollaboratorEntry>(
            await _http.PostAsJsonAsync($"resourcepacks/{packId}/collaborators", req, JsonOpts, ct), ct);
    }

    public async Task UpdateResourcePackCollaboratorAsync(Guid packId, Guid userId, UpdateCollaboratorRequest req, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        await EnsureSuccess(await _http.PatchAsJsonAsync($"resourcepacks/{packId}/collaborators/{userId}", req, JsonOpts, ct));
    }

    public async Task RemoveResourcePackCollaboratorAsync(Guid packId, Guid userId, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        await EnsureSuccess(await _http.DeleteAsync($"resourcepacks/{packId}/collaborators/{userId}", ct));
    }

    public async Task<PackTeamEntry> AddResourcePackTeamAsync(Guid packId, AddPackTeamRequest req, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        return await ReadAsync<PackTeamEntry>(
            await _http.PostAsJsonAsync($"resourcepacks/{packId}/teams", req, JsonOpts, ct), ct);
    }

    public async Task UpdateResourcePackTeamAsync(Guid packId, Guid teamId, UpdatePackTeamRequest req, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        await EnsureSuccess(await _http.PatchAsJsonAsync($"resourcepacks/{packId}/teams/{teamId}", req, JsonOpts, ct));
    }

    public async Task RemoveResourcePackTeamAsync(Guid packId, Guid teamId, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        await EnsureSuccess(await _http.DeleteAsync($"resourcepacks/{packId}/teams/{teamId}", ct));
    }

    /// <param name="progress">Reports total bytes handed to the socket so far, or null for no reporting.</param>
    public async Task<HostedResourcePackVersionInfo> UploadResourcePackVersionAsync(
        Guid packId, string filePath, CreateResourcePackVersionRequest meta, CancellationToken ct = default,
        IProgress<long>? progress = null)
    {
        await EnsureTokenAsync(ct);
        using var form = new MultipartFormDataContent();
        await using var fs = File.OpenRead(filePath);
        var fileContent = new StreamContent(new ProgressStream(fs, progress));
        fileContent.Headers.ContentType = new("application/zip");
        form.Add(fileContent, "file", Path.GetFileName(filePath));
        var metaJson = System.Text.Json.JsonSerializer.Serialize(meta, JsonOpts);
        form.Add(new StringContent(metaJson), "metadata");
        return await ReadAsync<HostedResourcePackVersionInfo>(
            await _http.PostAsync($"resourcepacks/{packId}/versions", form, ct), ct);
    }

    /// <summary>Deletes one uploaded version of a hosted resource pack. Deleting the last one keeps
    /// the pack.</summary>
    public async Task DeleteResourcePackVersionAsync(Guid packId, Guid versionId, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        await EnsureSuccess(await _http.DeleteAsync($"resourcepacks/{packId}/versions/{versionId}", ct));
    }

    public async Task<Stream> DownloadResourcePackVersionAsync(Guid packId, Guid versionId, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        var resp = await _http.GetAsync($"resourcepacks/{packId}/files/{versionId}",
            HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessKeepBody(resp, ct);
        return await resp.Content.ReadAsStreamAsync(ct);
    }

    public async Task<PackSummary> CreatePackAsync(CreatePackRequest req, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        return await ReadAsync<PackSummary>(await _http.PostAsJsonAsync("packs", req, JsonOpts, ct), ct);
    }

    /// <summary>
    /// One instance in full. The server is authoritative and its answer is cached; when it can't
    /// answer, the last cached detail stands in, and failing that one built from the cached list entry
    /// plus the pack's own rules file.
    /// </summary>
    /// <remarks>
    /// Play, the detail page and joining a server all need a <see cref="PackDetail"/> first, so without
    /// this fallback every button fails offline. A <c>404</c>, <c>401</c> or <c>403</c> is still
    /// obeyed: resurrecting a deleted or unshared pack from cache would be worse than the error.
    /// </remarks>
    public async Task<PackDetail> GetPackAsync(Guid id, CancellationToken ct = default)
    {
        try { await EnsureTokenAsync(ct); }
        catch (OfflineException)
        {
            var offline = OfflinePackDetail(id);
            if (offline is null) throw;
            return offline;
        }

        try
        {
            var detail = await ReadAsync<PackDetail>(await _http.GetAsync($"packs/{id}", ct), ct);
            PackDetailCache.Save(detail);
            return detail;
        }
        catch (SessionExpiredException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (!ServerSaidNo(ex))
        {
            var offline = OfflinePackDetail(id);
            if (offline is null) throw;
            AppLog.Log("packs", $"Instance {id} unavailable ({ex.Message}); using the last known details.");
            return offline;
        }
    }

    /// <summary>The server answered "no", which shouldn't be papered over with a cache.</summary>
    private static bool ServerSaidNo(Exception ex) =>
        ex is ApiException { Status: HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden };

    /// <summary>The last known details for one instance, without asking the server: the answer cached
    /// the last time it was opened, else one built from the cached instance list. Null when neither has
    /// it.</summary>
    /// <remarks>For painting a page before the real answer arrives, so it doesn't open as an empty frame
    /// and then jump. Never a substitute for the real answer.</remarks>
    public PackDetail? PeekPackDetail(Guid id)
    {
        try { return OfflinePackDetail(id); }
        catch (Exception ex)
        {
            AppLog.Log("packs", $"No remembered details for {id}: {ex.Message}");
            return null;
        }
    }

    /// <summary>The best detail available without the server, or null when there is none.</summary>
    private PackDetail? OfflinePackDetail(Guid id)
    {
        if (PackDetailCache.Load(id) is { } cached) return cached;

        // Never opened, so never cached, but the list entry has every summary field. PackDetail only
        // adds rules (from the pack folder) and the collaborator and team lists, which are the server's
        // to know and are left empty.
        var summary = PackListCache.Load()?.FirstOrDefault(p => p.Id == id);
        return summary is null ? null : PackDetailCache.Synthesise(summary, LocalRulesFor(id));
    }

    /// <summary>The pack's own <c>.rules.json</c>, or the global defaults behind it, or nothing.</summary>
    private List<PackFileRule> LocalRulesFor(Guid id)
    {
        try
        {
            var root = LocalPackScanner.FindPackRoot(_settings, id);
            if (root is null) return [];
            return PackRuleService.ToShared(new PackRuleService(this).Load(root));
        }
        catch { return []; }
    }

    public async Task<PackSummary> UpdatePackAsync(Guid id, UpdatePackRequest req, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        return await ReadAsync<PackSummary>(await _http.PatchAsJsonAsync($"packs/{id}", req, JsonOpts, ct), ct);
    }

    public async Task PushPackRulesAsync(Guid packId, List<PackFileRule> rules, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        var req = new UpdatePackRequest(Name: null, Description: null, Visibility: null, IsShared: null,
            IsEmpty: null, MinecraftVersion: null, Loader: null, LoaderVersion: null, Rules: rules);
        await EnsureSuccess(await _http.PatchAsJsonAsync($"packs/{packId}", req, JsonOpts, ct));
    }


    public async Task DeletePackAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        await EnsureSuccess(await _http.DeleteAsync($"packs/{id}", ct));
        PackDetailCache.Remove(id); // a deleted pack must not come back from the offline cache
    }

    public async Task<PackCollaboratorEntry> AddCollaboratorAsync(Guid packId, AddCollaboratorRequest req, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        return await ReadAsync<PackCollaboratorEntry>(await _http.PostAsJsonAsync($"packs/{packId}/collaborators", req, JsonOpts, ct), ct);
    }

    public async Task UpdateCollaboratorAsync(Guid packId, Guid userId, UpdateCollaboratorRequest req, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        await EnsureSuccess(await _http.PatchAsJsonAsync($"packs/{packId}/collaborators/{userId}", req, JsonOpts, ct));
    }

    public async Task RemoveCollaboratorAsync(Guid packId, Guid userId, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        await EnsureSuccess(await _http.DeleteAsync($"packs/{packId}/collaborators/{userId}", ct));
    }

    public async Task<PackTeamEntry> AddPackTeamAsync(Guid packId, AddPackTeamRequest req, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        return await ReadAsync<PackTeamEntry>(await _http.PostAsJsonAsync($"packs/{packId}/teams", req, JsonOpts, ct), ct);
    }

    public async Task UpdatePackTeamAsync(Guid packId, Guid teamId, UpdatePackTeamRequest req, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        await EnsureSuccess(await _http.PatchAsJsonAsync($"packs/{packId}/teams/{teamId}", req, JsonOpts, ct));
    }

    public async Task RemovePackTeamAsync(Guid packId, Guid teamId, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        await EnsureSuccess(await _http.DeleteAsync($"packs/{packId}/teams/{teamId}", ct));
    }

    // ── teams ────────────────────────────────────────────────────────────────

    public async Task<List<TeamSummary>> ListTeamsAsync(CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        return await ReadAsync<List<TeamSummary>>(await _http.GetAsync("teams", ct), ct);
    }

    public async Task<TeamSummary> CreateTeamAsync(CreateTeamRequest req, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        return await ReadAsync<TeamSummary>(await _http.PostAsJsonAsync("teams", req, JsonOpts, ct), ct);
    }

    public async Task<TeamDetail> GetTeamAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        return await ReadAsync<TeamDetail>(await _http.GetAsync($"teams/{id}", ct), ct);
    }

    public async Task<UserSummary> AddTeamMemberAsync(Guid id, AddTeamMemberRequest req, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        return await ReadAsync<UserSummary>(await _http.PostAsJsonAsync($"teams/{id}/members", req, JsonOpts, ct), ct);
    }

    public async Task RemoveTeamMemberAsync(Guid id, Guid userId, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        await EnsureSuccess(await _http.DeleteAsync($"teams/{id}/members/{userId}", ct));
    }

    /// <summary>Renames a team. Owner only.</summary>
    public async Task RenameTeamAsync(Guid id, RenameTeamRequest req, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        await EnsureSuccess(await _http.PutAsJsonAsync($"teams/{id}", req, JsonOpts, ct));
    }

    /// <summary>Hands a team to one of its existing members. Owner only; the old owner stays a member.</summary>
    public async Task TransferTeamOwnershipAsync(Guid id, TransferTeamOwnershipRequest req, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        await EnsureSuccess(await _http.PostAsJsonAsync($"teams/{id}/transfer", req, JsonOpts, ct));
    }

    /// <summary>Leaves a team. The owner cannot: they transfer ownership or delete the team instead.</summary>
    public async Task LeaveTeamAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        await EnsureSuccess(await _http.DeleteAsync($"teams/{id}/members/me", ct));
    }

    public async Task DeleteTeamAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        await EnsureSuccess(await _http.DeleteAsync($"teams/{id}", ct));
    }

    // ── admin ────────────────────────────────────────────────────────────────

    public async Task<List<UserQuotaInfo>> ListUserQuotasAsync(CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        return await ReadAsync<List<UserQuotaInfo>>(await _http.GetAsync("admin/users", ct), ct);
    }

    public async Task<UserQuotaInfo> SetUserQuotaAsync(Guid userId, SetQuotaRequest req, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        return await ReadAsync<UserQuotaInfo>(await _http.PatchAsJsonAsync($"admin/users/{userId}/quota", req, JsonOpts, ct), ct);
    }

    // ── global settings (admin) ─────────────────────────────────────────────

    public async Task<GlobalSettingsView> GetGlobalSettingsAsync(CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        return await ReadAsync<GlobalSettingsView>(await _http.GetAsync("admin/global-settings", ct), ct);
    }

    public async Task<GlobalSettingsView> UpdateGlobalSettingsAsync(UpdateGlobalSettingsRequest req, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        var response = await _http.PutAsJsonAsync("admin/global-settings", req, JsonOpts, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            response.Dispose();
            response = await _http.PostAsJsonAsync("admin/global-settings", req, JsonOpts, ct);
        }

        using (response)
            return await ReadAsync<GlobalSettingsView>(response, ct);
    }

    public async Task<DefaultRulesResponse> GetDefaultRulesAsync(CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        return await ReadAsync<DefaultRulesResponse>(await _http.GetAsync("settings/default-rules", ct), ct);
    }

    // ── proxy (mod platforms) ───────────────────────────────────────────────

    // ── upstream pacing ──────────────────────────────────────────────────────
    // Every store call from every launcher leaves the server from one address, so the stores' rate
    // limits are shared by all users. Each client keeps its calls to a steady trickle (a few in
    // flight, spaced out), and on "too many" backs the whole platform off for the requested time.
    private const int MaxInFlightPerPlatform = 3;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PlatformPace> _pace =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The pace of the one-mod-at-a-time part of an update check: one budget across both
    /// stores, which is what <see cref="AppSettings.ModUpdateChecksPerSecond"/> promises and what
    /// keeps a check under the launcher server's per-account rate. Rebuilt whenever the setting no
    /// longer matches it.</summary>
    private PlatformPace? _updateCheckPace;

    /// <summary>One pause per route, shared by every pace on it: the launcher server counts update
    /// checks and browsing against the same upstream limit. The direct CurseForge route has its own
    /// entry (<see cref="DirectRouteKey"/>), since its pauses concern one key, not the server.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PlatformHold> _holds =
        new(StringComparer.OrdinalIgnoreCase);

    private PlatformHold HoldFor(string route) => _holds.GetOrAdd(route, _ => new PlatformHold());

    /// <summary>Retries for a store call that was told to wait (see <see cref="IsWorthRetrying"/>):
    /// three, so a browse page answers within half a minute or so and then says the store is busy.</summary>
    private const int DefaultRetries = 3;

    /// <summary>Retries for a paced update-check request after "too many". More than the usual three:
    /// it's background work, so waiting out a few more pauses (at most 30 s each) beats reporting the
    /// mod as unchecked.</summary>
    private const int UpdateCheckRetries = 8;

    /// <summary>Retries for a CurseForge 403 on the shared key, whatever the pacing. It is usually
    /// CurseForge throttling the server for a while, which a few short waits ride out; when it is a
    /// key that stopped working, no number of retries helps, so the count stays small.</summary>
    private const int RefusedRetries = 3;

    /// <summary>Longest pause a Retry-After header can ask for. Anything longer is the store or server
    /// being cautious, and a page cannot sit on "trying again" for minutes.</summary>
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private static int RetriesFor(ProxyPacing pacing) => pacing == ProxyPacing.UpdateCheck ? UpdateCheckRetries : DefaultRetries;

    /// <summary>
    /// Raised whenever a store call is about to wait before another attempt, from whichever thread made
    /// the call. Subscribe through <see cref="AppState.StoreWaiting"/> for a UI-thread version.
    /// </summary>
    public event Action<StoreWait>? StoreWaiting;

    private static string DisplayName(string platform) =>
        platform.Equals("modrinth", StringComparison.OrdinalIgnoreCase) ? "Modrinth"
        : platform.Equals("curseforge", StringComparison.OrdinalIgnoreCase) ? "CurseForge"
        : "The store";

    /// <summary>Minimum gap between two sends on a route from this client. Modrinth allows 300 a
    /// minute per address (shared by everyone behind the server), so ~4.5/s; CurseForge publishes no
    /// figure but blocked bursts around 8/s, so ~6/s. The direct route is one key's own quota with
    /// nobody else on it, so it runs at ~8/s; still a trickle, since the per-key limit is undocumented
    /// too.</summary>
    private static TimeSpan SpacingFor(string route) => route.ToLowerInvariant() switch
    {
        "modrinth" => TimeSpan.FromMilliseconds(220),
        DirectRouteKey => TimeSpan.FromMilliseconds(120),
        _ => TimeSpan.FromMilliseconds(170)
    };

    /// <summary>How many default-paced requests a route may have in flight at once.</summary>
    private static int InFlightFor(string route) =>
        route.Equals(DirectRouteKey, StringComparison.OrdinalIgnoreCase) ? DirectInFlight : MaxInFlightPerPlatform;

    /// <summary>How many update-check requests may be in flight at once at <paramref name="perSecond"/>
    /// a second: enough that a slow answer does not stall the pace, never fewer than a route's default
    /// pace allows, never more than eight. With the default pace's three, a route stays inside the
    /// twelve places the launcher server lets one account hold in a store's queue.</summary>
    private static int UpdateCheckInFlight(int perSecond) => Math.Clamp(perSecond / 5, MaxInFlightPerPlatform, 8);

    /// <summary>The pace a request of <paramref name="pacing"/> keeps on <paramref name="route"/>
    /// (a platform through the proxy, or <see cref="DirectRouteKey"/>). Pauses are kept apart, per
    /// route, in <see cref="HoldFor"/>.</summary>
    /// <remarks>
    /// The update-check rate can change while a check runs, and a <see cref="SemaphoreSlim"/> can't be
    /// resized, so a changed rate gets a new pace object; requests already waiting finish on the old
    /// one. The route's <see cref="PlatformHold"/> is separate, so a pause survives the swap.
    /// </remarks>
    private PlatformPace PaceFor(string route, ProxyPacing pacing)
    {
        if (pacing != ProxyPacing.UpdateCheck)
            return _pace.GetOrAdd(route, r => new PlatformPace(SpacingFor(r), InFlightFor(r)));

        var rate = _settings.EffectiveModUpdateChecksPerSecond;
        while (true)
        {
            var current = Volatile.Read(ref _updateCheckPace);
            if (current is not null && current.Rate == rate) return current;
            var fresh = new PlatformPace(TimeSpan.FromSeconds(1.0 / rate), UpdateCheckInFlight(rate), rate);
            if (Interlocked.CompareExchange(ref _updateCheckPace, fresh, current) == current) return fresh;
        }
    }

    /// <summary>A platform-wide pause, set when a store (or the server's queue) says "too many".</summary>
    private sealed class PlatformHold
    {
        private readonly object _lock = new();
        private DateTimeOffset _until = DateTimeOffset.MinValue;

        public DateTimeOffset Until { get { lock (_lock) return _until; } }

        public void Extend(TimeSpan delay)
        {
            lock (_lock)
            {
                var until = DateTimeOffset.UtcNow + delay;
                if (until > _until) _until = until;
            }
        }
    }

    private sealed class PlatformPace(TimeSpan spacing, int inFlight, int rate = 0)
    {
        private TimeSpan MinSpacing { get; } = spacing;

        /// <summary>The update checks per second this pace was built for; 0 for the default pace.</summary>
        public int Rate { get; } = rate;

        public readonly SemaphoreSlim Gate = new(inFlight, inFlight);
        private readonly object _lock = new();
        private DateTimeOffset _nextSlot = DateTimeOffset.MinValue;

        /// <summary>Reserves the next send slot, no earlier than <paramref name="hold"/> allows, and
        /// returns how long to wait for it.</summary>
        public TimeSpan Reserve(PlatformHold hold)
        {
            var held = hold.Until;
            lock (_lock)
            {
                var now = DateTimeOffset.UtcNow;
                var earliest = _nextSlot > held ? _nextSlot : held;
                var slot = earliest > now ? earliest : now;
                _nextSlot = slot + MinSpacing;
                return slot - now;
            }
        }
    }

    /// <summary>Waits for a send slot and an in-flight place, and returns holding the place (release
    /// <see cref="PlatformPace.Gate"/> after sending).</summary>
    /// <remarks>The route's pause is checked again after each wait. Otherwise every request queued
    /// behind a "too many" would still go out on schedule, straight into the pause, and each refusal
    /// would start another one.</remarks>
    private static async Task EnterAsync(PlatformPace pace, PlatformHold hold, CancellationToken ct)
    {
        while (true)
        {
            var wait = pace.Reserve(hold);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            var held = hold.Until - DateTimeOffset.UtcNow;
            if (held > TimeSpan.Zero)
            {
                await Task.Delay(held, ct);
                continue;
            }

            await pace.Gate.WaitAsync(ct);
            if (hold.Until <= DateTimeOffset.UtcNow) return;
            pace.Gate.Release(); // a pause began while this request waited for a place
        }
    }

    /// <summary>Header the proxy reads a caller-supplied CurseForge key from.</summary>
    public const string OwnCurseForgeKeyHeader = "X-CloudLauncher-CF-Key";

    /// <summary>
    /// Sends the user's own CurseForge key with the request when they have set one, so the server
    /// proxies the call under that key instead of the shared one.
    /// </summary>
    /// <remarks>
    /// The shared key serves every launcher and gets throttled with fast 403s under load; a personal key
    /// has its own quota. It's sent per request and never stored on the server, in a header rather than
    /// the query string to stay out of access logs, and withheld entirely when ServerUrl is plain http to
    /// another machine.
    /// </remarks>
    private void AttachOwnKey(HttpRequestMessage req, string platform)
    {
        if (!platform.Equals("curseforge", StringComparison.OrdinalIgnoreCase)) return;
        if (_settings.CurseForgeApiKey is not { Length: > 0 } key) return;
        if (!_credentialsAllowed) return;
        req.Headers.TryAddWithoutValidation(OwnCurseForgeKeyHeader, key.Trim());
    }

    /// <summary>Send a request through the server-side mod-platform proxy. The server
    /// attaches the admin's API key; clients never see it. <paramref name="pathAndQuery"/>
    /// is the upstream-relative path (no leading slash), e.g. "mods/search?gameId=432".
    /// A CurseForge call made under the user's own key with <see cref="AppSettings.CurseForgeDirect"/>
    /// on skips the server and goes to CurseForge itself (see <see cref="SendDirectCurseForgeAsync"/>);
    /// the answer has the same shape either way.</summary>
    /// <param name="pacing">Which pace the request keeps. <see cref="ProxyPacing.UpdateCheck"/> is for
    /// the one-mod-at-a-time requests of an update check only; everything else keeps the default.</param>
    public async Task<HttpResponseMessage> ProxyAsync(
        string platform, HttpMethod method, string pathAndQuery,
        HttpContent? body = null, CancellationToken ct = default, ProxyPacing pacing = ProxyPacing.Default)
    {
        // A request message disposes its content, so a body that may need re-sending is buffered once.
        byte[]? bodyBytes = null;
        System.Net.Http.Headers.MediaTypeHeaderValue? bodyType = null;
        if (body is not null)
        {
            bodyBytes = await body.ReadAsByteArrayAsync(ct);
            bodyType = body.Headers.ContentType;
        }

        if (DirectCurseForgeKey(platform) is { } ownKey)
            return await SendDirectCurseForgeAsync(ownKey, method, pathAndQuery, bodyBytes, bodyType, ct, pacing);

        await EnsureTokenAsync(ct);
        var url = $"proxy/{platform}/{pathAndQuery.TrimStart('/')}";
        var retries = RetriesFor(pacing);
        var hold = HoldFor(platform);

        for (var attempt = 0; ; attempt++)
        {
            // Looked up per attempt: the update-check rate is live, so a retry after a pause is sent
            // at whatever the setting says now.
            var pace = PaceFor(platform, pacing);
            await EnterAsync(pace, hold, ct);
            HttpResponseMessage resp;
            try
            {
                using var req = new HttpRequestMessage(method, url);
                AttachOwnKey(req, platform);
                if (bodyBytes is not null)
                {
                    var content = new ByteArrayContent(bodyBytes);
                    content.Headers.ContentType = bodyType ?? new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                    req.Content = content;
                }
                resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            finally { pace.Gate.Release(); }

            if (resp.IsSuccessStatusCode) return resp;

            // Answers worth waiting out: a "too many" from the server or the store, the store not
            // answering, and a CurseForge 403 on the shared key. Anything else, and the last attempt,
            // go back to the caller as they are.
            var refused = resp.StatusCode == HttpStatusCode.Forbidden;
            var allowed = refused ? Math.Min(retries, RefusedRetries) : retries;
            if (attempt >= allowed || !await IsWorthRetryingAsync(resp, platform, pathAndQuery, ct))
                return resp;

            var delay = BackoffFor(resp, attempt);
            hold.Extend(delay);
            var status = (int)resp.StatusCode;
            resp.Dispose();
            AppLog.Log("proxy", $"{platform} answered {status} for {pathAndQuery}; trying again in {delay.TotalSeconds:0}s (attempt {attempt + 1} of {allowed + 1}).");
            StoreWaiting?.Invoke(new StoreWait(DisplayName(platform), delay, attempt + 1, allowed + 1));
            await Task.Delay(delay, ct);
        }
    }

    /// <summary>
    /// True for an answer that a short wait may change: a 429 from anyone; a 502, 503 or 504 that is
    /// the launcher server's database restarting, the store not answering, or nginx between them
    /// restarting (never the server saying it has no CurseForge key, or that the store's CDN blocks
    /// it); and a bare CurseForge 403, which is its throttle far more often than a dead key. Not a
    /// download-URL 403, which is the author's opt-out and is read by the caller.
    /// </summary>
    private static async Task<bool> IsWorthRetryingAsync(HttpResponseMessage resp, string platform, string pathAndQuery, CancellationToken ct)
    {
        switch (resp.StatusCode)
        {
            case HttpStatusCode.TooManyRequests:
                return true;
            case HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout:
                return await ServerCodeAsync(resp, ct) is not ("key_not_configured" or "upstream_blocked");
            case HttpStatusCode.Forbidden:
                // Only a CurseForge answer passed through as it came; the server's own refusals carry
                // a code and mean what they say.
                return platform.Equals("curseforge", StringComparison.OrdinalIgnoreCase)
                       && !IsDownloadUrlRequest(pathAndQuery)
                       && await ServerCodeAsync(resp, ct) is null;
            default:
                return false;
        }
    }

    /// <summary>The <c>code</c> of the launcher server's JSON error shape, or null when the body is
    /// something else (a store's own error, an HTML page from nginx, nothing). Reading it buffers
    /// the body, so the caller can still read it afterwards.</summary>
    private static async Task<string?> ServerCodeAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            var trimmed = body.AsSpan().TrimStart();
            if (trimmed.Length == 0 || trimmed[0] != '{') return null;
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("code", out var code)
                   && code.ValueKind == System.Text.Json.JsonValueKind.String
                ? code.GetString()
                : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    /// <summary>How long to wait before the next attempt: the Retry-After when the answer carries one,
    /// otherwise 2, 4, 8 s by attempt; never under a second, never over <see cref="MaxBackoff"/>.</summary>
    private static TimeSpan BackoffFor(HttpResponseMessage? resp, int attempt)
    {
        var delay = resp?.Headers.RetryAfter?.Delta
                    ?? (resp?.Headers.RetryAfter?.Date is { } at ? at - DateTimeOffset.UtcNow : (TimeSpan?)null)
                    ?? TimeSpan.FromSeconds(2 * Math.Pow(2, attempt));
        if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
        if (delay > MaxBackoff) delay = MaxBackoff;
        return delay;
    }

    // ── CurseForge direct ────────────────────────────────────────────────────
    // With their own key and AppSettings.CurseForgeDirect on, the user's CurseForge calls skip the
    // launcher server, whose queue exists to keep everyone inside one key's limit. They use a separate
    // client: the server client carries the bearer token, which CurseForge must never see, and the
    // ConnectivityHandler, which would report a CurseForge outage as the launcher server being down.

    /// <summary>The pace, pause and success window of the direct route, kept apart from the proxy's
    /// "curseforge" entries: a pause the launcher server asked for says nothing about this key.</summary>
    private const string DirectRouteKey = "curseforge-direct";

    /// <summary>Requests the direct route may have in flight at once at its default pace.</summary>
    private const int DirectInFlight = 6;

    /// <summary>How recent a success must be for a 403 to count as throttling rather than a rejected
    /// key. Same window as the server's guard: a key CurseForge revoked never answers 200 again.</summary>
    private static readonly TimeSpan DirectRecentSuccessWindow = TimeSpan.FromMinutes(3);

    /// <summary>Pause after a throttling 403 (CurseForge sends no Retry-After with those).</summary>
    private static readonly TimeSpan DirectThrottlePause = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Where the direct route sends CurseForge calls, without a trailing slash. The environment
    /// variable <c>CL_CURSEFORGE_BASE</c> overrides it so a test harness can stand a fake CurseForge
    /// up on this machine (see <see cref="ResolveDirectCurseForgeBase"/>); it is read once, when this
    /// class is first used.
    /// </summary>
    public static readonly string DirectCurseForgeBaseUrl = ResolveDirectCurseForgeBase();

    /// <summary>One client for the process: no base address, no bearer token, no connectivity
    /// handler. A plain product-and-contact User-Agent, because CurseForge's edge is known to refuse
    /// odd ones.</summary>
    private static readonly Lazy<HttpClient> DirectClient = new(() =>
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(8),
            AutomaticDecompression = DecompressionMethods.All
        })
        { Timeout = TimeSpan.FromMinutes(2) };
        ApplyUserAgent(client);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    });

    private long _directLastSuccessTicks;
    private int _directAnnounced;

    /// <summary>True when CurseForge calls currently skip the launcher server: a key of the user's own
    /// is set and <see cref="AppSettings.CurseForgeDirect"/> is on. Read live, so Settings can say so
    /// the moment either changes.</summary>
    public bool IsCurseForgeDirect => DirectCurseForgeKey("curseforge") is not null;

    /// <summary>The user's own key when this request should go straight to CurseForge, else null.</summary>
    private string? DirectCurseForgeKey(string platform)
    {
        if (!platform.Equals("curseforge", StringComparison.OrdinalIgnoreCase)) return null;
        if (!_settings.CurseForgeDirect) return null;
        return _settings.CurseForgeApiKey is { Length: > 0 } key ? key.Trim() : null;
    }

    /// <summary>A download-URL request, whose 403 is most often the author's third-party-distribution
    /// opt-out rather than a throttle or a bad key. <see cref="CurseForgeService.GetDownloadUrlAsync"/>
    /// tells those apart itself, so that one status is handed to it untouched.</summary>
    private static bool IsDownloadUrlRequest(string pathAndQuery) =>
        pathAndQuery.Contains("/download-url", StringComparison.OrdinalIgnoreCase);

    private bool DirectSucceededRecently() =>
        DateTimeOffset.UtcNow.UtcTicks - Volatile.Read(ref _directLastSuccessTicks) < DirectRecentSuccessWindow.Ticks;

    /// <summary>
    /// Sends one CurseForge call under the user's own key, answering the way the launcher server's
    /// proxy would, so <see cref="CurseForgeService"/> needs no second set of cases.
    /// </summary>
    /// <remarks>
    /// <para>The proxy passes CurseForge's body through except for four answers, reproduced here in its
    /// JSON shape (<c>{"error", "code"}</c>): a 429 that outlasts the retries, a throttling 403, a
    /// rejected key, and CurseForge not answering (a transport failure or a 502, 503 or 504 that
    /// outlasts the retries). The last is a synthetic 502 rather than a throw, since browse pages
    /// treat a throw with no status code as "you are offline".</para>
    /// <para>CurseForge throttles bursts with bare, fast 403s, the same status a rejected key gets. A 403
    /// within three minutes of a success on this route is treated as throttling (pause and retry);
    /// otherwise it's the key, which only the user can fix.</para>
    /// </remarks>
    private async Task<HttpResponseMessage> SendDirectCurseForgeAsync(
        string key, HttpMethod method, string pathAndQuery, byte[]? bodyBytes,
        System.Net.Http.Headers.MediaTypeHeaderValue? bodyType, CancellationToken ct, ProxyPacing pacing)
    {
        var url = $"{DirectCurseForgeBaseUrl}/{pathAndQuery.TrimStart('/')}";
        var retries = RetriesFor(pacing);
        var hold = HoldFor(DirectRouteKey);
        if (Interlocked.Exchange(ref _directAnnounced, 1) == 0)
            AppLog.Log("curseforge", "Talking to CurseForge directly with the key set in Settings > Mod stores; " +
                                     "the launcher server's queue is not used for CurseForge.");

        for (var attempt = 0; ; attempt++)
        {
            var pace = PaceFor(DirectRouteKey, pacing);
            await EnterAsync(pace, hold, ct);
            HttpResponseMessage resp;
            var held = true;
            try
            {
                using var req = new HttpRequestMessage(method, url);
                req.Headers.TryAddWithoutValidation("x-api-key", key);
                if (bodyBytes is not null)
                {
                    var content = new ByteArrayContent(bodyBytes);
                    content.Headers.ContentType = bodyType ?? new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                    req.Content = content;
                }
                resp = await DirectClient.Value.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (Connectivity.DescribeTransportFailure(ex, ct) is { } why)
            {
                // A blip on the way to CurseForge gets the same short waits as a 429 before it is
                // reported, so a dropped connection mid-check does not fail the mod outright.
                if (attempt >= retries)
                {
                    AppLog.Log("curseforge", $"CurseForge did not answer {method} {pathAndQuery}: {why}.");
                    return Synthetic(HttpStatusCode.BadGateway,
                        $"{{\"error\":\"CurseForge did not answer ({JsonText(why)}).\",\"code\":\"upstream_unreachable\"}}");
                }
                // The place goes back before the wait, so a pause does not hold one of the route's few.
                pace.Gate.Release();
                held = false;
                await WaitBeforeRetryAsync(hold, BackoffFor(null, attempt), attempt, retries, $"did not answer ({why})", pathAndQuery, ct);
                continue;
            }
            finally
            {
                if (held) pace.Gate.Release();
            }

            if (resp.IsSuccessStatusCode)
            {
                Volatile.Write(ref _directLastSuccessTicks, DateTimeOffset.UtcNow.UtcTicks);
                return resp;
            }

            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var delay = BackoffFor(resp, attempt);
                resp.Dispose();
                if (attempt >= retries)
                    return RateLimited(delay,
                        "CurseForge is rate-limiting requests made with your API key (too many in a short time). " +
                        "Try again in a minute.");
                await WaitBeforeRetryAsync(hold, delay, attempt, retries, "answered 429", pathAndQuery, ct);
                continue;
            }

            if (resp.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
            {
                var delay = BackoffFor(resp, attempt);
                var status = (int)resp.StatusCode;
                resp.Dispose();
                if (attempt >= retries)
                    return Synthetic(HttpStatusCode.BadGateway,
                        $"{{\"error\":\"CurseForge is not answering right now (HTTP {status}). Try again in a minute.\",\"code\":\"upstream_unreachable\"}}");
                await WaitBeforeRetryAsync(hold, delay, attempt, retries, $"answered {status}", pathAndQuery, ct);
                continue;
            }

            if (resp.StatusCode == HttpStatusCode.Forbidden && !IsDownloadUrlRequest(pathAndQuery) && DirectSucceededRecently())
            {
                resp.Dispose();
                if (attempt >= retries)
                    return RateLimited(DirectThrottlePause,
                        "CurseForge is temporarily refusing requests made with your API key (too many in a short " +
                        "time). This is not the key itself. Try again in a minute.");
                await WaitBeforeRetryAsync(hold, DirectThrottlePause, attempt, retries,
                    "answered 403 while the key was working minutes ago; treating it as throttling and", pathAndQuery, ct);
                continue;
            }

            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized && !IsDownloadUrlRequest(pathAndQuery))
            {
                AppLog.Log("curseforge", $"CurseForge answered {(int)resp.StatusCode} for {pathAndQuery} with no recent success on this key; " +
                                         "reporting the key as rejected.");
                resp.Dispose();
                return Synthetic(HttpStatusCode.BadRequest,
                    "{\"error\":\"CurseForge rejected the API key you set in Settings > Mod stores. Check it, or clear " +
                    "the box to go back to the launcher's shared key.\",\"code\":\"own_key_rejected\"}");
            }

            return resp; // anything else CurseForge said (404, 400, 500...) is the caller's to read, as through the proxy
        }
    }

    /// <summary>Pauses the direct route for <paramref name="delay"/>, says so in the log and to any
    /// page listening, and waits it out.</summary>
    private async Task WaitBeforeRetryAsync(PlatformHold hold, TimeSpan delay, int attempt, int retries,
        string what, string pathAndQuery, CancellationToken ct)
    {
        hold.Extend(delay);
        AppLog.Log("curseforge", $"CurseForge {what} for {pathAndQuery}; trying again in {delay.TotalSeconds:0}s (attempt {attempt + 1} of {retries + 1}).");
        StoreWaiting?.Invoke(new StoreWait("CurseForge", delay, attempt + 1, retries + 1));
        await Task.Delay(delay, ct);
    }

    /// <summary>A 429 in the proxy's JSON error shape, with the Retry-After the client honours.</summary>
    private static HttpResponseMessage RateLimited(TimeSpan retryAfter, string message)
    {
        var seconds = (int)Math.Ceiling(retryAfter.TotalSeconds);
        var resp = Synthetic(HttpStatusCode.TooManyRequests,
            $"{{\"error\":\"{JsonText(message)}\",\"code\":\"upstream_rate_limited\",\"platform\":\"CurseForge\",\"retryAfterSeconds\":{seconds}}}");
        resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        return resp;
    }

    private static HttpResponseMessage Synthetic(HttpStatusCode status, string json) => new(status)
    {
        ReasonPhrase = status.ToString(),
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static string JsonText(string text) =>
        System.Text.Json.JsonEncodedText.Encode(text, System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString();

    // ── sync ─────────────────────────────────────────────────────────────────

    public async Task<PackManifest> GetManifestAsync(Guid packId, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        return await ReadAsync<PackManifest>(await _http.GetAsync($"packs/{packId}/sync/manifest", ct), ct);
    }

    public async Task<BeginUploadResponse> BeginUploadAsync(Guid packId, BeginUploadRequest req, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        return await ReadAsync<BeginUploadResponse>(await _http.PostAsJsonAsync($"packs/{packId}/sync/upload/begin", req, JsonOpts, ct), ct);
    }

    public async Task UploadBlobAsync(Guid packId, string hash, Stream content, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Put, $"packs/{packId}/sync/blob/{hash}")
            { Content = new StreamContent(content) };
        await EnsureSuccess(await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct));
    }

    public async Task<CommitUploadResponse> CommitUploadAsync(Guid packId, BeginUploadRequest req, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        return await ReadAsync<CommitUploadResponse>(await _http.PostAsJsonAsync($"packs/{packId}/sync/upload/commit", req, JsonOpts, ct), ct);
    }

    public async Task<Stream> DownloadBlobAsync(Guid packId, string hash, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        var resp = await _http.GetAsync($"packs/{packId}/sync/blob/{hash}", HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessKeepBody(resp, ct);
        return await resp.Content.ReadAsStreamAsync(ct);
    }

    // ── launcher self-update ─────────────────────────────────────────────────

    /// <summary>Every published launcher release, newest first (the changelog shown in Settings).
    /// Anonymous; an older server without the endpoint just has no changelog.</summary>
    public async Task<IReadOnlyList<LauncherReleaseInfo>> GetLauncherReleasesAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync("launcher/releases", ct);
        if (resp.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound)
        {
            resp.Dispose();
            // Fall back to the one release we can always ask for, so the dialog still says something.
            var latest = await GetLatestLauncherAsync(ct);
            return latest is null ? Array.Empty<LauncherReleaseInfo>() : new[] { latest };
        }
        return await ReadAsync<List<LauncherReleaseInfo>>(resp, ct);
    }

    /// <summary>Latest published launcher build, or null if none has been uploaded yet.
    /// Anonymous, so it works before the user logs in.</summary>
    public async Task<LauncherReleaseInfo?> GetLatestLauncherAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync("launcher/latest", ct);
        if (resp.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound)
        {
            resp.Dispose();
            return null;
        }
        return await ReadAsync<LauncherReleaseInfo>(resp, ct);
    }

    /// <summary>Streams the latest launcher package. The caller owns the response and must dispose it.</summary>
    public async Task<HttpResponseMessage> DownloadLauncherAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync("launcher/download", HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessKeepBody(resp, ct);
        return resp;
    }

    /// <summary>Streams the launcher package with this SHA-256, the one a signed manifest names. The
    /// caller owns the response and must dispose it.</summary>
    /// <remarks>Asking by hash means a release published mid-download cannot swap the file. A server
    /// that has no copy under that hash, or predates the parameter, answers 404 and the current package
    /// is fetched instead; the caller checks whatever arrives against the manifest either way.</remarks>
    public async Task<HttpResponseMessage> DownloadLauncherAsync(string sha256, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"launcher/download?sha256={Uri.EscapeDataString(sha256)}",
            HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            resp.Dispose();
            resp = await _http.GetAsync("launcher/download", HttpCompletionOption.ResponseHeadersRead, ct);
        }
        await EnsureSuccessKeepBody(resp, ct);
        return resp;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    // These helpers take ownership of the response and dispose it, so the call sites don't have to;
    // leaving HttpResponseMessage to the finalizer is an antipattern. The streaming download methods
    // (DownloadLauncherAsync, Download*VersionAsync) don't use them, since they hand the live
    // response/stream to the caller.

    private static async Task EnsureSuccess(HttpResponseMessage resp, CancellationToken ct = default)
    {
        using (resp)
        {
            if (resp.IsSuccessStatusCode) return;
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new ApiException($"{(int)resp.StatusCode} {resp.ReasonPhrase}: {body}", resp.StatusCode);
        }
    }

    /// <summary>Like <see cref="EnsureSuccess"/> but doesn't dispose the response on success, for
    /// streaming endpoints that return the live response/stream to the caller. Disposes only when
    /// throwing on a failure status.</summary>
    private static async Task EnsureSuccessKeepBody(HttpResponseMessage resp, CancellationToken ct = default)
    {
        if (resp.IsSuccessStatusCode) return;
        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new ApiException($"{(int)resp.StatusCode} {resp.ReasonPhrase}: {body}", resp.StatusCode);
        }
    }

    private async Task EnsureHostedModSuccess(HttpResponseMessage resp, CancellationToken ct = default)
    {
        if (resp.StatusCode == HttpStatusCode.NotFound) await ThrowHostedModNotFoundAsync(resp, ct);
        await EnsureSuccess(resp, ct);
    }

    /// <summary>Turns a 404 from a <c>/mods</c> route into the right sentence, and throws it.</summary>
    /// <remarks>
    /// A 404 can mean the server has no <c>/mods</c> routes at all, or just a missing item (a version
    /// deleted a moment ago, a collaborator already removed). A controller's NotFound always has a
    /// body, while an unmatched route gets an empty 404 from the framework, so only the empty one means
    /// the route family is missing.
    /// </remarks>
    private async Task ThrowHostedModNotFoundAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        using (resp)
        {
            string body;
            try { body = await resp.Content.ReadAsStringAsync(ct); }
            catch (OperationCanceledException) { throw; }
            catch { body = ""; }

            if (string.IsNullOrWhiteSpace(body))
                throw new ApiException(
                    $"Hosted mod routes are not available on {ServerUrl}. Deploy/restart the current CloudLauncher.Server build so /mods is registered.",
                    HttpStatusCode.NotFound);
            throw new ApiException($"{(int)resp.StatusCode} {resp.ReasonPhrase}: {body}", resp.StatusCode);
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage resp, CancellationToken ct)
    {
        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                throw new ApiException($"{(int)resp.StatusCode} {resp.ReasonPhrase}: {body}", resp.StatusCode);
            }
            var result = await resp.Content.ReadFromJsonAsync<T>(JsonOpts, ct);
            return result ?? throw new ApiException("Empty response body", resp.StatusCode);
        }
    }

    private async Task<T> ReadHostedModAsync<T>(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.StatusCode == HttpStatusCode.NotFound) await ThrowHostedModNotFoundAsync(resp, ct);
        return await ReadAsync<T>(resp, ct);
    }

    /// <summary>
    /// A pass-through read-only stream that reports (bytes read, expected total) as it is drained.
    /// </summary>
    /// <remarks>
    /// <para>The download counterpart of <see cref="ProgressStream"/>: counting as the caller reads
    /// avoids buffering the download just to measure it.</para>
    /// <para><paramref name="total"/> is the Content-Length, or -1 when the body was chunked. It's
    /// passed through as is, so the caller draws a determinate bar only when the size is known.</para>
    /// <para>Disposing disposes the wrapped response stream, since this object stands in for it.</para>
    /// </remarks>
    private sealed class ProgressReadStream(Stream inner, long total, IProgress<(long done, long total)> progress) : Stream
    {
        private long _read;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => total >= 0 ? total : throw new NotSupportedException();

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Advance(inner.Read(buffer, offset, count));

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            Advance(await inner.ReadAsync(buffer.AsMemory(offset, count), ct));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            Advance(await inner.ReadAsync(buffer, ct));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }

        private int Advance(int count)
        {
            if (count > 0)
            {
                _read += count;
                progress.Report((_read, total));
            }
            return count;
        }
    }

    /// <summary>A pass-through read-only stream that reports how many bytes have been read out of it.</summary>
    /// <remarks>
    /// <para>Upload progress can only be measured where bytes are pulled from the file, which is
    /// <see cref="StreamContent"/> inside HttpClient, so this sits between the two.</para>
    /// <para>Seeking passes through and resets the counter: HttpClient measures a seekable body for
    /// Content-Length and rewinds it before a retry. Blocking seeks would force chunked encoding, and a
    /// retried upload would report progress past the file size.</para>
    /// <para>Disposing doesn't dispose the file: the upload methods own it with their own
    /// <c>await using</c>, and this wrapper is disposed by the content it was handed to.</para>
    /// </remarks>
    private sealed class ProgressStream(Stream inner, IProgress<long>? progress) : Stream
    {
        private long _read;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set { inner.Position = value; Rebase(value); }
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Advance(inner.Read(buffer, offset, count));

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            Advance(await inner.ReadAsync(buffer.AsMemory(offset, count), ct));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            Advance(await inner.ReadAsync(buffer, ct));

        public override long Seek(long offset, SeekOrigin origin)
        {
            var position = inner.Seek(offset, origin);
            Rebase(position);
            return position;
        }

        public override void Flush() => inner.Flush();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Advance(int count)
        {
            if (count > 0)
            {
                _read += count;
                progress?.Report(_read);
            }
            return count;
        }

        private void Rebase(long position)
        {
            _read = position;
            progress?.Report(_read);
        }
    }
}
