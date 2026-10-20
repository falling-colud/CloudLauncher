using System.Net.Http.Json;
using System.Text.Json;
using CloudLauncher.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace CloudLauncher.Server.Discord;

/// <summary>
/// What a board picture needs from the stores: the name and icon behind a mod card's key, and the
/// icons themselves. Both are cached for a day.
/// </summary>
/// <remarks>
/// Icon addresses come from plans.json, which any editor of the pack can write, so only the stores'
/// own image hosts are fetched from: the server never fetches an arbitrary address on someone's
/// behalf.
/// </remarks>
public sealed class PlanAssets(IHttpClientFactory httpFactory, IServiceScopeFactory scopes, ILogger<PlanAssets> log)
{
    public const string HttpClientName = "plan-assets";

    private const int MaxIconBytes = 512 * 1024;
    private static readonly TimeSpan Keep = TimeSpan.FromDays(1);
    private static readonly TimeSpan KeepMiss = TimeSpan.FromMinutes(30);

    private static readonly string[] IconHosts =
    [
        "cdn.modrinth.com", "media.forgecdn.net", "edge.forgecdn.net", "raw.githubusercontent.com",
        "avatars.githubusercontent.com"
    ];

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 48L * 1024 * 1024 });

    /// <summary>Names and icons for the mod cards on these boards. Keys the stores don't answer for
    /// are left out (the card shows the key instead).</summary>
    public async Task<IReadOnlyDictionary<string, ModLabel>> ModLabelsAsync(IEnumerable<PlanBoardData> boards,
        CancellationToken ct)
    {
        var keys = boards.SelectMany(b => b.Nodes)
            .Where(n => n.Kind == PlanNodeData.KindMod && !string.IsNullOrWhiteSpace(n.ModKey))
            .Select(n => n.ModKey!).Distinct().ToList();
        var result = new Dictionary<string, ModLabel>();
        var modrinth = new List<string>();
        var curseforge = new List<string>();
        foreach (var key in keys)
        {
            if (_cache.TryGetValue(("label", key), out ModLabel? cached))
            {
                if (cached is not null) result[key] = cached;
                continue;
            }
            var (store, id) = Split(key);
            switch (store)
            {
                case "modrinth" when id.Length > 0: modrinth.Add(id); break;
                case "curseforge" when long.TryParse(id, out _): curseforge.Add(id); break;
                case "file":
                    result[key] = new ModLabel(FileTitle(id), "Local file", null);
                    break;
            }
        }

        if (modrinth.Count > 0) await ModrinthAsync(modrinth, result, ct);
        if (curseforge.Count > 0) await CurseForgeAsync(curseforge, result, ct);

        foreach (var key in keys.Where(k => !result.ContainsKey(k) && !_cache.TryGetValue(("label", k), out _)))
            Remember(("label", key), (ModLabel?)null, KeepMiss, 64);
        return result;
    }

    /// <summary>The bytes of each icon that could be fetched from a store's image host.</summary>
    public async Task<IReadOnlyDictionary<string, byte[]>> IconsAsync(IEnumerable<string> urls, CancellationToken ct)
    {
        var result = new Dictionary<string, byte[]>();
        foreach (var url in urls.Distinct().Take(200))
        {
            if (_cache.TryGetValue(("icon", url), out byte[]? cached))
            {
                if (cached is { Length: > 0 }) result[url] = cached;
                continue;
            }
            byte[]? bytes = null;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
                && IconHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(6));
                    using var response = await Http().GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    if (response.IsSuccessStatusCode && (response.Content.Headers.ContentLength ?? 0) <= MaxIconBytes)
                    {
                        var data = await response.Content.ReadAsByteArrayAsync(timeout.Token);
                        if (data.Length is > 0 and <= MaxIconBytes) bytes = data;
                    }
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
                {
                    log.LogDebug("Icon {Url} not fetched: {Message}", url, e.Message);
                }
            }
            Remember(("icon", url), bytes ?? [], bytes is null ? KeepMiss : Keep, (bytes?.Length ?? 0) + 64);
            if (bytes is not null) result[url] = bytes;
        }
        return result;
    }

    private async Task ModrinthAsync(List<string> ids, Dictionary<string, ModLabel> result, CancellationToken ct)
    {
        foreach (var chunk in ids.Chunk(100))
        {
            try
            {
                var url = "https://api.modrinth.com/v2/projects?ids=" + Uri.EscapeDataString(JsonSerializer.Serialize(chunk));
                using var doc = JsonDocument.Parse(await Http().GetStringAsync(url, ct));
                foreach (var p in doc.RootElement.EnumerateArray())
                {
                    var id = p.GetProperty("id").GetString()!;
                    var slug = p.TryGetProperty("slug", out var s) ? s.GetString() : null;
                    var label = new ModLabel(p.GetProperty("title").GetString() ?? id, "Modrinth",
                        p.TryGetProperty("icon_url", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null);
                    // The launcher's key holds the project id, but older boards may hold the slug.
                    foreach (var key in new[] { "modrinth:" + id, slug is null ? null : "modrinth:" + slug })
                        if (key is not null && chunk.Contains(key["modrinth:".Length..]))
                        {
                            result[key] = label;
                            Remember(("label", key), (ModLabel?)label, Keep, 256);
                        }
                }
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException
                                          or KeyNotFoundException or InvalidOperationException)
            {
                log.LogInformation("Modrinth names for a board not fetched: {Message}", e.Message);
            }
        }
    }

    private async Task CurseForgeAsync(List<string> ids, Dictionary<string, ModLabel> result, CancellationToken ct)
    {
        string? key;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            key = await db.GlobalSettings.AsNoTracking().Select(s => s.CurseForgeApiKey).FirstOrDefaultAsync(ct);
        }
        if (string.IsNullOrWhiteSpace(key)) return;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.curseforge.com/v1/mods")
            {
                Content = JsonContent.Create(new { modIds = ids.Select(long.Parse).ToArray() })
            };
            request.Headers.Add("x-api-key", key);
            using var response = await Http().SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            foreach (var m in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                var id = m.GetProperty("id").GetInt64().ToString();
                string? icon = null;
                if (m.TryGetProperty("logo", out var logo) && logo.ValueKind == JsonValueKind.Object
                    && logo.TryGetProperty("thumbnailUrl", out var t)) icon = t.GetString();
                var label = new ModLabel(m.GetProperty("name").GetString() ?? id, "CurseForge", icon);
                result["curseforge:" + id] = label;
                Remember(("label", "curseforge:" + id), (ModLabel?)label, Keep, 256);
            }
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException
                                      or KeyNotFoundException or InvalidOperationException)
        {
            log.LogInformation("CurseForge names for a board not fetched: {Message}", e.Message);
        }
    }

    private HttpClient Http() => httpFactory.CreateClient(HttpClientName);

    private void Remember<T>(object key, T value, TimeSpan keep, long size) =>
        _cache.Set(key, value, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = keep, Size = size });

    private static (string Store, string Id) Split(string key)
    {
        var i = key.IndexOf(':');
        return i < 0 ? ("", key) : (key[..i].ToLowerInvariant(), key[(i + 1)..]);
    }

    /// <summary>"sodium-neoforge-0.6.13+mc1.21.1.jar" reads best as the part before the version.</summary>
    private static string FileTitle(string file)
    {
        var name = file.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
        var cut = name.IndexOfAny(['-', '_', '+']);
        while (cut > 0 && cut < name.Length - 1 && !char.IsDigit(name[cut + 1]))
            cut = name.IndexOfAny(['-', '_', '+'], cut + 1);
        return cut > 0 ? name[..cut] : name;
    }
}
