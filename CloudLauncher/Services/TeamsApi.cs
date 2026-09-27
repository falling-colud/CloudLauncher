using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>
/// Team routes (invitations, roles, "what is shared with this team", user search) that
/// <see cref="ApiClient"/> does not have yet.
/// </summary>
/// <remarks>
/// <para>These belong beside <c>ListTeamsAsync</c> in <see cref="ApiClient"/>. If they move, keep two
/// behaviours: a transport failure becomes an <see cref="OfflineException"/>, and token refresh is
/// left to <see cref="ApiClient"/> (refreshing rotates the token, and two rotators racing sign the
/// account out).</para>
/// <para>Requests here don't go through <see cref="ConnectivityHandler"/>, so they don't update
/// <see cref="AppState.IsOffline"/>; callers treat an <see cref="OfflineException"/> as offline
/// themselves.</para>
/// </remarks>
public sealed class TeamsApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // One handler for the process. No BaseAddress: the server URL is read from settings per request,
    // so changing it takes effect straight away.
    private static readonly HttpClient Http = ApiClient.WithUserAgent(new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(8)
    })
    { Timeout = TimeSpan.FromSeconds(60) });

    private readonly AppSettings _settings;
    private readonly ApiClient _api;

    public TeamsApi(AppSettings settings, ApiClient api)
    {
        _settings = settings;
        _api = api;
    }

    // ── team management ──

    /// <summary>Everything the team has been granted access to: packs, mods, worlds, resource
    /// packs and bundles in one list, newest first. Any member may read it.</summary>
    public Task<TeamSharedContent> GetSharedAsync(Guid teamId, CancellationToken ct = default) =>
        SendAsync<TeamSharedContent>(HttpMethod.Get, $"teams/{teamId}/shared", null, ct);

    /// <summary>Promotes or demotes a member. Owner only; the owner's own role only changes through an
    /// ownership transfer.</summary>
    public Task SetRoleAsync(Guid teamId, Guid userId, TeamRole role, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, $"teams/{teamId}/members/{userId}/role",
                  new UpdateTeamMemberRoleRequest(role), ct);

    // ── invitations ──

    /// <summary>Invites somebody by username, or mints a join code when
    /// <see cref="CreateTeamInvitationRequest.Username"/> is null. Owner or Admin.</summary>
    public Task<TeamInvitationEntry> InviteAsync(
        Guid teamId, CreateTeamInvitationRequest req, CancellationToken ct = default) =>
        SendAsync<TeamInvitationEntry>(HttpMethod.Post, $"teams/{teamId}/invitations", req, ct);

    /// <summary>Every invitation this team has issued: pending, accepted and revoked. Owner or
    /// Admin only, since the tokens are included.</summary>
    public Task<List<TeamInvitationEntry>> ListInvitationsAsync(Guid teamId, CancellationToken ct = default) =>
        SendAsync<List<TeamInvitationEntry>>(HttpMethod.Get, $"teams/{teamId}/invitations", null, ct);

    /// <summary>Withdraws a pending invitation. Idempotent; refuses one already accepted.</summary>
    public Task RevokeInvitationAsync(Guid teamId, Guid invitationId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"teams/{teamId}/invitations/{invitationId}", null, ct);

    /// <summary>The signed-in user's own pending team invitations.</summary>
    /// <remarks>Uses <c>/teams/invitations</c> because the shared <c>/invitations</c> endpoint only
    /// resolves pack tokens and returns 404 for a team token.</remarks>
    public Task<List<TeamInvitationEntry>> ListMyInvitationsAsync(CancellationToken ct = default) =>
        SendAsync<List<TeamInvitationEntry>>(HttpMethod.Get, "teams/invitations", null, ct);

    /// <summary>Joins the team the code belongs to. Idempotent: accepting twice is not an error.</summary>
    public Task<TeamInvitationEntry> AcceptAsync(string token, CancellationToken ct = default) =>
        SendAsync<TeamInvitationEntry>(HttpMethod.Post,
            $"teams/invitations/{Uri.EscapeDataString(token)}/accept", null, ct);

    /// <summary>Turns an invitation down. Idempotent.</summary>
    public Task<TeamInvitationEntry> DeclineAsync(string token, CancellationToken ct = default) =>
        SendAsync<TeamInvitationEntry>(HttpMethod.Post,
            $"teams/invitations/{Uri.EscapeDataString(token)}/decline", null, ct);

    // ── people ──

    /// <summary>Usernames starting with <paramref name="query"/>, for an invite box's typeahead.</summary>
    /// <remarks>Prefix match, at most 20 rows, rate-limited to 60/minute per address. Queries under two
    /// characters return an empty page. Callers should still debounce (450ms) to stay inside the
    /// limit.</remarks>
    public async Task<UserSearchPage> SearchUsersAsync(string query, CancellationToken ct = default)
    {
        var q = (query ?? "").Trim();
        if (q.Length < 2) return new UserSearchPage(Array.Empty<UserSummary>(), 0);
        return await SendAsync<UserSearchPage>(
            HttpMethod.Get, $"users?q={Uri.EscapeDataString(q)}", null, ct);
    }

    // ── bundles (only the one route the teams screen needs) ──

    /// <summary>Drops a bundle's grant to a team. The other content kinds have this on
    /// <see cref="ApiClient"/> already.</summary>
    public Task RemoveBundleTeamAsync(Guid bundleId, Guid teamId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"bundles/{bundleId}/teams/{teamId}", null, ct);

    // ── plumbing ──

    private async Task SendAsync(HttpMethod method, string route, object? body, CancellationToken ct)
    {
        using var resp = await RawAsync(method, route, body, ct);
        if (!resp.IsSuccessStatusCode) throw await FailureAsync(resp, ct);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string route, object? body, CancellationToken ct)
    {
        using var resp = await RawAsync(method, route, body, ct);
        if (!resp.IsSuccessStatusCode) throw await FailureAsync(resp, ct);
        var value = await resp.Content.ReadFromJsonAsync<T>(Json, ct);
        return value ?? throw new ApiException("The server's reply was empty.", resp.StatusCode);
    }

    /// <summary>One request, with one retry after a refresh when the answer is a 401.</summary>
    /// <remarks><see cref="ApiClient"/> only refreshes inside the expiry window, so a token that expired
    /// between two calls here only shows up as a 401.</remarks>
    private async Task<HttpResponseMessage> RawAsync(
        HttpMethod method, string route, object? body, CancellationToken ct)
    {
        // Teams belong to accounts; say so rather than asking the server for a 401.
        if (!_api.IsSignedIn) throw new ApiException(ApiClient.SignInRequiredMessage, HttpStatusCode.Unauthorized);
        await EnsureTokenAsync(ct);
        var resp = await SendOnceAsync(method, route, body, ct);
        if (resp.StatusCode != HttpStatusCode.Unauthorized) return resp;

        resp.Dispose();
        try { await _api.MeAsync(ct); }
        catch (SessionExpiredException) { throw; }
        catch (OfflineException) { throw; }
        catch { /* not a refresh problem; let the retry produce the real answer */ }
        return await SendOnceAsync(method, route, body, ct);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        HttpMethod method, string route, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, new Uri(BaseUri(), route));
        if (body is not null) req.Content = JsonContent.Create(body, body.GetType(), options: Json);
        if (!string.IsNullOrEmpty(_settings.AccessToken))
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", _settings.AccessToken);

        try
        {
            return await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        }
        catch (Exception ex) when (Connectivity.DescribeTransportFailure(ex, ct) is { } why)
        {
            throw new OfflineException(why);
        }
    }

    private Uri BaseUri() => new(_settings.ServerUrl.TrimEnd('/') + "/");

    /// <summary>Refreshes through <see cref="ApiClient"/> when the token is about to expire, so the
    /// rotation happens in one place.</summary>
    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_settings.AccessToken)) return;
        if (_settings.AccessTokenExpiresAt is not { } expires) return; // unknown; the 401 path covers it
        if (expires > DateTimeOffset.UtcNow.AddMinutes(2)) return;
        await _api.MeAsync(ct);
    }

    private static async Task<Exception> FailureAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        string body;
        try { body = await resp.Content.ReadAsStringAsync(ct); }
        catch { body = ""; }
        return new ApiException(ErrorSentence(body) ?? StatusSentence(resp.StatusCode), resp.StatusCode);
    }

    // ── error messages ──

    /// <summary>
    /// The sentence to put in front of the user for a failed call, with the HTTP noise removed.
    /// </summary>
    /// <remarks>
    /// The server returns <c>{ "error": "&lt;sentence&gt;" }</c> on refusals; this digs that sentence out
    /// of whichever wrapper carried it. <paramref name="fallback"/> is only used when there is nothing
    /// else to say.
    /// </remarks>
    public static string Explain(Exception ex, string fallback)
    {
        if (ex is OfflineException offline) return offline.Message;
        if (ex is SessionExpiredException) return "Your session has expired - sign in again.";
        if (ex is not ApiException api) return fallback;

        var tail = StripStatusPrefix(api.Message);
        return ErrorSentence(tail)
               ?? (tail.Length > 0 && !LooksLikeJson(tail) ? tail : StatusSentence(api.Status));
    }

    /// <summary>Drops <see cref="ApiClient"/>'s <c>"403 Forbidden: "</c> preamble and nothing else, so a
    /// server sentence containing a colon is kept whole.</summary>
    private static string StripStatusPrefix(string message)
    {
        var text = (message ?? "").Trim();
        if (text.Length < 5 || !char.IsDigit(text[0]) || !char.IsDigit(text[1]) || !char.IsDigit(text[2]))
            return text;
        var colon = text.IndexOf(": ", StringComparison.Ordinal);
        return colon < 0 ? text : text[(colon + 2)..].Trim();
    }

    /// <summary>The <c>error</c> field of a problem body, or null when the body is not one.</summary>
    private static string? ErrorSentence(string? body)
    {
        if (string.IsNullOrWhiteSpace(body) || !LooksLikeJson(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var name in new[] { "error", "detail", "title", "message" })
            {
                if (doc.RootElement.TryGetProperty(name, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { Length: > 0 } sentence)
                    return sentence;
            }
        }
        catch { /* not JSON after all */ }
        return null;
    }

    private static bool LooksLikeJson(string body) => body.TrimStart().StartsWith('{');

    private static string StatusSentence(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Forbidden => "You are not allowed to do that.",
        HttpStatusCode.NotFound => "The server could not find that any more - it may already be gone.",
        HttpStatusCode.Conflict => "Something else changed it first. Refresh and try again.",
        HttpStatusCode.TooManyRequests => "That is too many requests in a row. Wait a minute and try again.",
        HttpStatusCode.BadRequest => "The server would not accept that.",
        _ => "The server could not do that."
    };
}
