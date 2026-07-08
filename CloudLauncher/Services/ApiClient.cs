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
public sealed class SessionExpiredException() : Exception("Session expired — please log in again.");

public sealed class ApiClient
{
    private readonly HttpClient _http;
    private readonly AppSettings _settings;
    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new(System.Text.Json.JsonSerializerDefaults.Web);

    // Raised on the calling thread when the refresh token itself has expired.
    // The handler should navigate to the login screen.
    public event Action? SessionExpired;

    public ApiClient(AppSettings settings)
    {
        _settings = settings;
        _http = new HttpClient { BaseAddress = new Uri(settings.ServerUrl.TrimEnd('/') + "/") };
        _http.Timeout = TimeSpan.FromMinutes(5);
        if (!string.IsNullOrEmpty(settings.AccessToken))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.AccessToken);
    }

    public string ServerUrl => _settings.ServerUrl;

    public void SetTokens(TokenResponse tokens)
    {
        _settings.AccessToken = tokens.AccessToken;
        _settings.RefreshToken = tokens.RefreshToken;
        _settings.AccessTokenExpiresAt = tokens.AccessTokenExpiresAt;
        _settings.Username = tokens.Username;
        _settings.UserId = tokens.UserId;
        _settings.Save();
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
    }

    public void ClearTokens()
    {
        _settings.AccessToken = null;
        _settings.RefreshToken = null;
        _settings.AccessTokenExpiresAt = null;
        _settings.Username = null;
        _settings.UserId = null;
        _settings.Save();
        _http.DefaultRequestHeaders.Authorization = null;
    }

    // ── token management ────────────────────────────────────────────────────

    /// <summary>
    /// Proactively refresh the access token if it is expired or within 2 minutes of expiry.
    /// Throws SessionExpiredException (and fires SessionExpired event) if refresh fails.
    /// </summary>
    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_settings.AccessToken)) return; // not logged in — let the request fail naturally

        var expiresAt = _settings.AccessTokenExpiresAt;
        var nearExpiry = expiresAt is null || expiresAt.Value <= DateTimeOffset.UtcNow.AddMinutes(2);
        if (!nearExpiry) return; // token is fresh — nothing to do

        if (string.IsNullOrEmpty(_settings.RefreshToken))
        {
            ClearTokens();
            SessionExpired?.Invoke();
            throw new SessionExpiredException();
        }

        try
        {
            // Call refresh endpoint directly — bypass EnsureTokenAsync to avoid recursion
            var resp = await _http.PostAsJsonAsync("auth/refresh",
                new RefreshRequest(_settings.RefreshToken), JsonOpts, ct);

            if (!resp.IsSuccessStatusCode)
            {
                ClearTokens();
                SessionExpired?.Invoke();
                throw new SessionExpiredException();
            }

            var tokens = await resp.Content.ReadFromJsonAsync<TokenResponse>(JsonOpts, ct)
                ?? throw new SessionExpiredException();
            SetTokens(tokens);
        }
        catch (SessionExpiredException) { throw; }
        catch
        {
            ClearTokens();
            SessionExpired?.Invoke();
            throw new SessionExpiredException();
        }
    }

    // ── auth ────────────────────────────────────────────────────────────────

    public async Task<RegisterPendingResponse> RegisterAsync(RegisterRequest req, CancellationToken ct = default) =>
        await ReadAsync<RegisterPendingResponse>(await _http.PostAsJsonAsync("auth/register", req, JsonOpts, ct), ct);

    public async Task<TokenResponse> LoginAsync(LoginRequest req, CancellationToken ct = default) =>
        await ReadAsync<TokenResponse>(await _http.PostAsJsonAsync("auth/login", req, JsonOpts, ct), ct);

    public async Task ResendVerificationAsync(ResendVerificationRequest req, CancellationToken ct = default) =>
        await ReadAsync<object>(await _http.PostAsJsonAsync("auth/resend-verification", req, JsonOpts, ct), ct);

    public async Task<GoogleAuthStartResponse> GoogleAuthStartAsync(CancellationToken ct = default) =>
        await ReadAsync<GoogleAuthStartResponse>(await _http.GetAsync("auth/google/start", ct), ct);

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

    // ── packs ────────────────────────────────────────────────────────────────

    public async Task<List<PackSummary>> ListPacksAsync(CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        return await ReadAsync<List<PackSummary>>(await _http.GetAsync("packs", ct), ct);
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

    public async Task<ModBrowsePage> BrowseModsAsync(
        ModBrowseSource source, Guid? teamId = null,
        string? query = null, string? mcVersion = null, string? loader = null,
        int offset = 0, int limit = 25,
        CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        var qs = $"mods/browse?source={source}&offset={offset}&limit={limit}";
        if (teamId.HasValue) qs += $"&teamId={teamId.Value}";
        if (!string.IsNullOrWhiteSpace(query)) qs += $"&q={Uri.EscapeDataString(query)}";
        if (!string.IsNullOrWhiteSpace(mcVersion)) qs += $"&mcVersion={Uri.EscapeDataString(mcVersion)}";
        if (!string.IsNullOrWhiteSpace(loader)) qs += $"&loader={Uri.EscapeDataString(loader)}";
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

    public async Task<HostedModVersionInfo> UploadModVersionAsync(
        Guid modId, string filePath, CreateModVersionRequest meta, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        using var form = new MultipartFormDataContent();
        await using var fs = File.OpenRead(filePath);
        var fileContent = new StreamContent(fs);
        fileContent.Headers.ContentType = new("application/java-archive");
        form.Add(fileContent, "file", Path.GetFileName(filePath));
        var metaJson = System.Text.Json.JsonSerializer.Serialize(meta, JsonOpts);
        form.Add(new StringContent(metaJson), "metadata");
        return await ReadHostedModAsync<HostedModVersionInfo>(
            await _http.PostAsync($"mods/{modId}/versions", form, ct), ct);
    }

    public async Task<Stream> DownloadModVersionAsync(Guid modId, Guid versionId, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        var resp = await _http.GetAsync($"mods/{modId}/files/{versionId}",
            HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            resp.Dispose();
            throw new ApiException(
                $"Hosted mod routes are not available on {ServerUrl}. Deploy/restart the current CloudLauncher.Server build so /mods is registered.",
                HttpStatusCode.NotFound);
        }
        await EnsureSuccessKeepBody(resp, ct); // keep the response — we stream its body to the caller
        return await resp.Content.ReadAsStreamAsync(ct);
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

    public async Task<SharedWorldVersionInfo> UploadSharedWorldVersionAsync(
        Guid worldId, string filePath, CreateWorldVersionRequest meta, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        using var form = new MultipartFormDataContent();
        await using var fs = File.OpenRead(filePath);
        var fileContent = new StreamContent(fs);
        fileContent.Headers.ContentType = new("application/zip");
        form.Add(fileContent, "file", Path.GetFileName(filePath));
        var metaJson = System.Text.Json.JsonSerializer.Serialize(meta, JsonOpts);
        form.Add(new StringContent(metaJson), "metadata");
        return await ReadAsync<SharedWorldVersionInfo>(
            await _http.PostAsync($"worlds/{worldId}/versions", form, ct), ct);
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

    public async Task<HostedResourcePackVersionInfo> UploadResourcePackVersionAsync(
        Guid packId, string filePath, CreateResourcePackVersionRequest meta, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        using var form = new MultipartFormDataContent();
        await using var fs = File.OpenRead(filePath);
        var fileContent = new StreamContent(fs);
        fileContent.Headers.ContentType = new("application/zip");
        form.Add(fileContent, "file", Path.GetFileName(filePath));
        var metaJson = System.Text.Json.JsonSerializer.Serialize(meta, JsonOpts);
        form.Add(new StringContent(metaJson), "metadata");
        return await ReadAsync<HostedResourcePackVersionInfo>(
            await _http.PostAsync($"resourcepacks/{packId}/versions", form, ct), ct);
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

    public async Task<PackDetail> GetPackAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        return await ReadAsync<PackDetail>(await _http.GetAsync($"packs/{id}", ct), ct);
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

    /// <summary>Send a request through the server-side mod-platform proxy. The server
    /// attaches the admin's API key; clients never see it. <paramref name="pathAndQuery"/>
    /// is the upstream-relative path (no leading slash), e.g. "mods/search?gameId=432".</summary>
    public async Task<HttpResponseMessage> ProxyAsync(
        string platform, HttpMethod method, string pathAndQuery,
        HttpContent? body = null, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        using var req = new HttpRequestMessage(method, $"proxy/{platform}/{pathAndQuery.TrimStart('/')}")
            { Content = body };
        return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }

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

    /// <summary>Latest published launcher build, or null if none has been uploaded yet.
    /// Anonymous — works even before the user logs in.</summary>
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

    /// <summary>Publish a new launcher build (admin only).</summary>
    public async Task<LauncherReleaseInfo> UploadLauncherAsync(
        string filePath, string version, string? notes, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        using var form = new MultipartFormDataContent();
        await using var fs = File.OpenRead(filePath);
        var fileContent = new StreamContent(fs);
        fileContent.Headers.ContentType = new("application/octet-stream");
        form.Add(fileContent, "file", Path.GetFileName(filePath));
        form.Add(new StringContent(version), "version");
        if (!string.IsNullOrWhiteSpace(notes))
            form.Add(new StringContent(notes), "notes");
        return await ReadAsync<LauncherReleaseInfo>(await _http.PostAsync("launcher/upload", form, ct), ct);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    // Note: these helpers take ownership of the response and dispose it (via `using`) so the
    // ~40 call sites don't each have to. Relying on the finalizer to clean up an
    // HttpResponseMessage is a documented antipattern. The streaming download methods
    // (DownloadLauncherAsync, Download*VersionAsync) intentionally do NOT route through here —
    // they hand the live response/stream to the caller.

    private static async Task EnsureSuccess(HttpResponseMessage resp, CancellationToken ct = default)
    {
        using (resp)
        {
            if (resp.IsSuccessStatusCode) return;
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new ApiException($"{(int)resp.StatusCode} {resp.ReasonPhrase}: {body}", resp.StatusCode);
        }
    }

    /// <summary>Like <see cref="EnsureSuccess"/> but does NOT dispose the response on success —
    /// for streaming endpoints whose live response/stream is returned to the caller. Only
    /// disposes when throwing on a failure status.</summary>
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
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            resp.Dispose();
            throw new ApiException(
                $"Hosted mod routes are not available on {ServerUrl}. Deploy/restart the current CloudLauncher.Server build so /mods is registered.",
                resp.StatusCode);
        }
        await EnsureSuccess(resp, ct);
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
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            resp.Dispose();
            throw new ApiException(
                $"Hosted mod routes are not available on {ServerUrl}. Deploy/restart the current CloudLauncher.Server build so /mods is registered.",
                resp.StatusCode);
        }
        return await ReadAsync<T>(resp, ct);
    }
}
