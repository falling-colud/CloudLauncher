using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>The Discord routes: the channels a pack's planning boards are posted to and kept current
/// in (server <c>PackDiscordController</c>).</summary>
public sealed partial class ApiClient
{
    /// <summary>The pack's Discord channels, or null when the server is older than the feature.</summary>
    public async Task<PackDiscordInfo?> GetPackDiscordAsync(Guid packId, CancellationToken ct = default)
    {
        RefuseIfLocal(packId);
        await EnsureTokenAsync(ct);
        var resp = await _http.GetAsync($"packs/{packId}/discord", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) { resp.Dispose(); return null; }
        return await ReadAsync<PackDiscordInfo>(resp, ct);
    }

    /// <summary>Links a channel: at once through a webhook URL, or (with none) a pending link whose
    /// code is used with <c>/plan link</c> in the channel.</summary>
    /// <exception cref="ApiException">400 with the reason the webhook was refused.</exception>
    public async Task<PackDiscordLinkDto> CreatePackDiscordLinkAsync(Guid packId, string? webhookUrl, string? boardId,
        CancellationToken ct = default)
    {
        RefuseIfLocal(packId);
        await EnsureTokenAsync(ct);
        return await ReadAsync<PackDiscordLinkDto>(await _http.PostAsJsonAsync($"packs/{packId}/discord/links",
            new CreatePackDiscordLinkRequest(webhookUrl, boardId), JsonOpts, ct), ct);
    }

    /// <summary>Stops posting to a channel and takes its board messages down.</summary>
    public async Task DeletePackDiscordLinkAsync(Guid packId, Guid linkId, CancellationToken ct = default)
    {
        RefuseIfLocal(packId);
        await EnsureTokenAsync(ct);
        using var resp = await _http.DeleteAsync($"packs/{packId}/discord/links/{linkId}", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return; // already gone
        if (!resp.IsSuccessStatusCode)
            throw new ApiException($"{(int)resp.StatusCode} {resp.ReasonPhrase}: {await resp.Content.ReadAsStringAsync(ct)}",
                resp.StatusCode);
    }

    /// <summary>Redraws every board message in the channel now.</summary>
    public async Task<PackDiscordLinkDto> RefreshPackDiscordLinkAsync(Guid packId, Guid linkId, CancellationToken ct = default)
    {
        RefuseIfLocal(packId);
        await EnsureTokenAsync(ct);
        return await ReadAsync<PackDiscordLinkDto>(
            await _http.PostAsync($"packs/{packId}/discord/links/{linkId}/refresh", content: null, ct), ct);
    }
}
