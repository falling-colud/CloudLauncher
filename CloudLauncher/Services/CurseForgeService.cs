using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudLauncher.Services;

/// <summary>
/// CurseForge API wrapper. All requests are tunneled through the launcher's own
/// server proxy (<c>/proxy/curseforge/*</c>), which attaches the admin-configured
/// API key. Clients never see the key.
/// </summary>
public sealed class CurseForgeService
{
    private const int MinecraftGameId = 432;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    // Long-lived client for CDN downloads. A per-call `new HttpClient()` exhausts sockets
    // (each instance opens its own pool and lingers in TIME_WAIT after disposal).
    private static readonly HttpClient DownloadHttp = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly ApiClient _api;

    public CurseForgeService(ApiClient api) { _api = api; }

    /// <summary>Always true — configuration lives on the server. If the admin
    /// hasn't set the key, the proxy returns 503 with an actionable message.</summary>
    public bool IsConfigured => true;

    // ── search ────────────────────────────────────────────────────────────────

    // CurseForge Minecraft classIds: 6=Mods, 12=ResourcePacks, 17=Worlds, 4471=Modpacks, 4546=Shaders
    public const int ClassIdMods = 6;
    public const int ClassIdResourcePacks = 12;
    public const int ClassIdModpacks = 4471;
    public const int ClassIdWorlds = 17;

    public async Task<List<ModSummary>> SearchAsync(
        string query,
        string? mcVersion = null,
        string? loader = null,
        int limit = 20,
        int offset = 0,
        int classId = ClassIdMods,
        int sortField = 2,
        CancellationToken ct = default)
    {
        var parameters = new List<(string Name, string Value)>
        {
            ("gameId", MinecraftGameId.ToString()),
            ("classId", classId.ToString()),
            ("pageSize", limit.ToString()),
            ("index", offset.ToString()),
            ("sortField", sortField.ToString()),
            ("sortOrder", "desc")
        };
        if (!string.IsNullOrWhiteSpace(query))
            parameters.Add(("searchFilter", query));
        if (!string.IsNullOrEmpty(mcVersion))
            parameters.Add(("gameVersion", mcVersion));
        if (!string.IsNullOrEmpty(loader))
        {
            var loaderType = LoaderToInt(loader);
            if (loaderType > 0)
                parameters.Add(("modLoaderType", loaderType.ToString()));
        }

        using var response = await ProxyGetAsync($"mods/search?{ToQueryString(parameters)}", ct);
        if (!response.IsSuccessStatusCode)
        {
            var details = await response.Content.ReadAsStringAsync(ct);
            var message = response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                ? "CurseForge API key was rejected by the server. An admin must update it in the dev menu."
                : $"CurseForge search failed: {(int)response.StatusCode} {response.ReasonPhrase}";
            if (!string.IsNullOrWhiteSpace(details))
                message += $" ({details})";
            throw new HttpRequestException(message, null, response.StatusCode);
        }

        var payload = await response.Content.ReadFromJsonAsync<CfSearchResponse>(Json, ct);
        return payload?.Data?.Select(ToSummary).ToList() ?? new();
    }

    // ── mod detail + versions ─────────────────────────────────────────────────

    public async Task<ModSummary?> GetModAsync(int modId, CancellationToken ct = default)
    {
        var resp = await ProxyGetJsonAsync<CfModResponse>($"mods/{modId}", ct);
        return resp?.Data is null ? null : ToSummary(resp.Data);
    }

    public async Task<string?> GetDescriptionAsync(int modId, CancellationToken ct = default)
    {
        var resp = await ProxyGetJsonAsync<JsonElement>($"mods/{modId}/description", ct);
        return resp.ValueKind == JsonValueKind.Undefined ? null
             : resp.TryGetProperty("data", out var data) ? data.GetString() : null;
    }

    public async Task<ModProjectDetail> GetProjectDetailAsync(int modId, CancellationToken ct = default)
    {
        var modResp = await ProxyGetJsonAsync<CfModResponse>($"mods/{modId}", ct);
        var description = await GetDescriptionAsync(modId, ct);
        var mod = modResp?.Data;
        if (mod is null) return EmptyDetail(description);

        return new ModProjectDetail(
            description ?? mod.Summary,
            new ModProjectLinks(
                mod.Links?.WebsiteUrl,
                mod.Links?.IssuesUrl,
                mod.Links?.SourceUrl,
                mod.Links?.WikiUrl,
                null),
            mod.Screenshots?
                .Where(s => !string.IsNullOrWhiteSpace(s.Url) || !string.IsNullOrWhiteSpace(s.ThumbnailUrl))
                .Select(s => new ModMediaItem(s.Url ?? s.ThumbnailUrl!, s.ThumbnailUrl, s.Title, s.Description))
                .ToList()
                ?? new List<ModMediaItem>());
    }

    public async Task<List<ModVersion>> GetVersionsAsync(int modId, CancellationToken ct = default)
    {
        const int pageSize = 50;
        var all = new List<ModVersion>();
        var index = 0;

        while (true)
        {
            using var response = await ProxyGetAsync($"mods/{modId}/files?index={index}&pageSize={pageSize}", ct);
            if (!response.IsSuccessStatusCode)
            {
                if (all.Count > 0) break;
                throw await CurseForgeRequestExceptionAsync("version list", response, ct);
            }

            var resp = await response.Content.ReadFromJsonAsync<CfFilesResponse>(Json, ct);
            var page = resp?.Data ?? new();
            if (page.Count == 0) break;

            all.AddRange(page.Select(f => ToCfVersion(modId, f)));
            if (page.Count < pageSize) break;
            index += pageSize;
        }

        return all;
    }

    public async Task<ModVersion?> GetVersionAsync(int modId, int fileId, CancellationToken ct = default)
    {
        var resp = await ProxyGetJsonAsync<CfFileResponse>($"mods/{modId}/files/{fileId}", ct);
        return resp?.Data is null ? null : ToCfVersion(modId, resp.Data);
    }

    public async Task<Dictionary<long, (ModSummary mod, ModVersion version)>> MatchFingerprintsAsync(
        IEnumerable<long> fingerprints, CancellationToken ct = default)
    {
        var distinct = fingerprints.Distinct().ToList();
        if (distinct.Count == 0) return new();

        var body = new { fingerprints = distinct };
        using var resp = await ProxyPostJsonAsync("fingerprints", body, ct);
        if (!resp.IsSuccessStatusCode) return new();

        var payload = await resp.Content.ReadFromJsonAsync<CfFingerprintResponse>(Json, ct);
        var result = new Dictionary<long, (ModSummary, ModVersion)>();
        var exactFingerprints = payload?.Data?.ExactFingerprints ?? new();
        var requestedFingerprints = distinct.ToHashSet();
        var matchIndex = 0;
        foreach (var match in payload?.Data?.ExactMatches ?? Enumerable.Empty<CfFingerprintMatch>())
        {
            var matchedFingerprints = match.Fingerprints?
                .Where(requestedFingerprints.Contains)
                .Distinct()
                .ToList()
                ?? new List<long>();

            if (matchedFingerprints.Count == 0)
            {
                var fallbackFingerprint = requestedFingerprints.Contains(match.Id)
                    ? match.Id
                    : matchIndex < exactFingerprints.Count
                        ? exactFingerprints[matchIndex]
                        : match.Id;
                if (fallbackFingerprint != 0)
                    matchedFingerprints.Add(fallbackFingerprint);
            }
            matchIndex++;

            if (match.File is null) continue;
            var mod = await GetModAsync(match.File.ModId, ct);
            if (mod is null) continue;
            var version = ToCfVersion(match.File.ModId, match.File);
            foreach (var fingerprint in matchedFingerprints)
                result[fingerprint] = (mod, version);
        }
        return result;
    }

    // ── cross-store counterpart ─────────────────────────────────────────────────

    /// <summary>Looks up CurseForge mods with an exact slug. Used for cross-store
    /// identity when a Modrinth-installed jar isn't byte-identical to CurseForge's copy.</summary>
    public async Task<List<ModSummary>> SearchBySlugAsync(string slug, int classId = ClassIdMods, CancellationToken ct = default)
    {
        var parameters = new List<(string Name, string Value)>
        {
            ("gameId", MinecraftGameId.ToString()),
            ("classId", classId.ToString()),
            ("slug", slug),
            ("pageSize", "10")
        };

        using var response = await ProxyGetAsync($"mods/search?{ToQueryString(parameters)}", ct);
        if (!response.IsSuccessStatusCode) return new();

        var payload = await response.Content.ReadFromJsonAsync<CfSearchResponse>(Json, ct);
        return payload?.Data?.Select(ToSummary).ToList() ?? new();
    }

    /// <summary>Finds the CurseForge mod that corresponds to a mod known on Modrinth,
    /// for recognising it as installed when the two stores' jars aren't byte-identical.
    /// Matches by exact slug first, then by a name search, accepting only a candidate
    /// whose slug or name lines up (see <see cref="ModMatching"/>).</summary>
    public async Task<ModSummary?> FindCounterpartAsync(string? slug, string? name, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(slug))
        {
            var bySlug = await SearchBySlugAsync(slug, ct: ct);
            var match = bySlug.FirstOrDefault(m => ModMatching.IsLikelySameMod(m, slug, name));
            if (match is not null) return match;
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            var byName = await SearchAsync(name, limit: 8, ct: ct);
            var match = byName.FirstOrDefault(m => ModMatching.IsLikelySameMod(m, slug, name));
            if (match is not null) return match;
        }

        return null;
    }

    public static bool TryParseFileIds(ModSummary mod, ModVersion version, out int modId, out int fileId)
    {
        modId = 0;
        fileId = 0;

        if (int.TryParse(mod.Id, out var parsedModId))
            modId = parsedModId;

        var ids = version.Id.Split(':', 2);
        if (ids.Length == 2)
        {
            if (int.TryParse(ids[0], out var versionModId))
                modId = versionModId;
            if (int.TryParse(ids[1], out var versionFileId))
                fileId = versionFileId;
        }

        return modId > 0 && fileId > 0;
    }

    // ── download URL ──────────────────────────────────────────────────────────

    public async Task<string?> GetDownloadUrlAsync(int modId, int fileId, CancellationToken ct = default)
    {
        using var response = await ProxyGetAsync($"mods/{modId}/files/{fileId}/download-url", ct);
        if (!response.IsSuccessStatusCode)
            throw await CurseForgeRequestExceptionAsync("download URL", response, ct);

        var resp = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        return resp.ValueKind != JsonValueKind.Undefined && resp.TryGetProperty("data", out var data)
            ? data.GetString() : null;
    }

    // ── modpack manifest resolution ───────────────────────────────────────────

    public async Task<List<(int ModId, int FileId)>> GetModpackFilesAsync(int modId, int fileId, CancellationToken ct = default)
    {
        // CurseForge modpack files reference their mods in the manifest.json inside the zip
        // This returns the file IDs listed in the manifest
        var url = await GetDownloadUrlAsync(modId, fileId, ct);
        if (url is null) return new();

        using var zip = new System.IO.Compression.ZipArchive(await DownloadHttp.GetStreamAsync(url, ct), System.IO.Compression.ZipArchiveMode.Read);
        var manifest = zip.GetEntry("manifest.json");
        if (manifest is null) return new();

        await using var stream = manifest.Open();
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var files = new List<(int, int)>();
        if (doc.RootElement.TryGetProperty("files", out var filesEl))
        {
            foreach (var f in filesEl.EnumerateArray())
            {
                var pid = f.GetProperty("projectID").GetInt32();
                var fid = f.GetProperty("fileID").GetInt32();
                files.Add((pid, fid));
            }
        }
        return files;
    }

    public static long FingerprintOfFile(string path)
    {
        using var input = File.OpenRead(path);
        using var normalized = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var b = buffer[i];
                if (b is 9 or 10 or 13 or 32) continue;
                normalized.WriteByte(b);
            }
        }

        return MurmurHash2Normalized(normalized);
    }

    internal static long MurmurHash2Normalized(MemoryStream normalized) =>
        MurmurHash2(normalized.GetBuffer().AsSpan(0, (int)normalized.Length), seed: 1);

    private static uint MurmurHash2(ReadOnlySpan<byte> data, uint seed)
    {
        const uint m = 0x5bd1e995;
        const int r = 24;

        var h = seed ^ (uint)data.Length;
        var len = data.Length;
        var index = 0;

        while (len >= 4)
        {
            var k = (uint)(data[index]
                           | (data[index + 1] << 8)
                           | (data[index + 2] << 16)
                           | (data[index + 3] << 24));
            k *= m;
            k ^= k >> r;
            k *= m;

            h *= m;
            h ^= k;

            index += 4;
            len -= 4;
        }

        switch (len)
        {
            case 3:
                h ^= (uint)data[index + 2] << 16;
                goto case 2;
            case 2:
                h ^= (uint)data[index + 1] << 8;
                goto case 1;
            case 1:
                h ^= data[index];
                h *= m;
                break;
        }

        h ^= h >> 13;
        h *= m;
        h ^= h >> 15;

        return h;
    }

    // ── proxy helpers ─────────────────────────────────────────────────────────

    private Task<HttpResponseMessage> ProxyGetAsync(string pathAndQuery, CancellationToken ct) =>
        _api.ProxyAsync("curseforge", HttpMethod.Get, pathAndQuery, null, ct);

    private async Task<T?> ProxyGetJsonAsync<T>(string pathAndQuery, CancellationToken ct)
    {
        using var resp = await ProxyGetAsync(pathAndQuery, ct);
        if (!resp.IsSuccessStatusCode) return default;
        return await resp.Content.ReadFromJsonAsync<T>(Json, ct);
    }

    private Task<HttpResponseMessage> ProxyPostJsonAsync(string path, object body, CancellationToken ct)
    {
        var content = JsonContent.Create(body, options: Json);
        return _api.ProxyAsync("curseforge", HttpMethod.Post, path, content, ct);
    }

    // ── private mapping ───────────────────────────────────────────────────────

    private static ModSummary ToSummary(CfMod m) => new(
        m.Id.ToString(), m.Slug ?? m.Name, m.Name,
        m.Authors?.FirstOrDefault()?.Name,
        m.Summary, m.DownloadCount,
        m.Logo?.ThumbnailUrl ?? m.Logo?.Url,
        ModSource.CurseForge,
        m.Categories?.Select(c => c.Name).ToArray() ?? Array.Empty<string>());

    private static ModVersion ToCfVersion(int modId, CfFile f) => new(
        $"{modId}:{f.Id}", f.DisplayName, f.DisplayName,
        f.GameVersions?.ToArray() ?? Array.Empty<string>(),
        f.GameVersions?.Where(v => !v.StartsWith("1.")).ToArray() ?? Array.Empty<string>(),
        f.ReleaseType == 1 ? "release" : f.ReleaseType == 2 ? "beta" : "alpha",
        f.FileDate,
        f.DownloadCount,
        null,
        ModSource.CurseForge,
        new List<ModVersionFile>
        {
            new(f.FileName, f.DownloadUrl ?? "", f.FileLength, null, true)
        },
        f.Dependencies?
            .Where(d => d.ModId > 0)
            .Select(d => new ModDependency(d.ModId.ToString(), null, ModSource.CurseForge, CfDependencyType(d.RelationType), null))
            .ToList()
            ?? new List<ModDependency>());

    private static int LoaderToInt(string loader) => loader.ToLowerInvariant() switch
    {
        "forge"    => 1,
        "cauldron" => 2,
        "liteloader" => 3,
        "fabric"   => 4,
        "quilt"    => 5,
        "neoforge" => 6,
        _          => 0
    };

    private static string CfDependencyType(int relationType) => relationType switch
    {
        3 => "required",
        2 => "optional",
        5 => "incompatible",
        _ => relationType.ToString()
    };

    private static string ToQueryString(IEnumerable<(string Name, string Value)> parameters) =>
        string.Join("&", parameters.Select(p => $"{Uri.EscapeDataString(p.Name)}={Uri.EscapeDataString(p.Value)}"));

    private static async Task<HttpRequestException> CurseForgeRequestExceptionAsync(
        string operation,
        HttpResponseMessage response,
        CancellationToken ct)
    {
        var details = await response.Content.ReadAsStringAsync(ct);
        var message = response.StatusCode switch
        {
            System.Net.HttpStatusCode.ServiceUnavailable =>
                $"CurseForge {operation} failed: the server has no CurseForge API key configured.",
            System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
                $"CurseForge {operation} failed: the server API key was rejected.",
            _ => $"CurseForge {operation} failed: {(int)response.StatusCode} {response.ReasonPhrase}"
        };
        if (!string.IsNullOrWhiteSpace(details))
            message += $" ({details})";
        return new HttpRequestException(message, null, response.StatusCode);
    }

    private static ModProjectDetail EmptyDetail(string? description = null) => new(
        description,
        new ModProjectLinks(null, null, null, null, null),
        Array.Empty<ModMediaItem>());

    // ── JSON DTOs ─────────────────────────────────────────────────────────────

    private sealed class CfSearchResponse { [JsonPropertyName("data")] public List<CfMod>? Data { get; set; } }
    private sealed class CfModResponse    { [JsonPropertyName("data")] public CfMod? Data { get; set; } }
    private sealed class CfFilesResponse  { [JsonPropertyName("data")] public List<CfFile>? Data { get; set; } }
    private sealed class CfFileResponse   { [JsonPropertyName("data")] public CfFile? Data { get; set; } }
    private sealed class CfFingerprintResponse { [JsonPropertyName("data")] public CfFingerprintData? Data { get; set; } }
    private sealed class CfFingerprintData
    {
        [JsonPropertyName("exactMatches")] public List<CfFingerprintMatch>? ExactMatches { get; set; }
        [JsonPropertyName("exactFingerprints")] public List<long>? ExactFingerprints { get; set; }
    }
    private sealed class CfFingerprintMatch
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("file")] public CfFile? File { get; set; }
        [JsonPropertyName("fingerprints")] public List<long>? Fingerprints { get; set; }
    }
    private sealed class CfMod
    {
        [JsonPropertyName("id")]            public int Id { get; set; }
        [JsonPropertyName("name")]          public string Name { get; set; } = "";
        [JsonPropertyName("slug")]          public string? Slug { get; set; }
        [JsonPropertyName("summary")]       public string? Summary { get; set; }
        [JsonPropertyName("downloadCount")] public long DownloadCount { get; set; }
        [JsonPropertyName("logo")]          public CfLogo? Logo { get; set; }
        [JsonPropertyName("authors")]       public List<CfAuthor>? Authors { get; set; }
        [JsonPropertyName("categories")]    public List<CfCategory>? Categories { get; set; }
        [JsonPropertyName("links")]         public CfLinks? Links { get; set; }
        [JsonPropertyName("screenshots")]   public List<CfScreenshot>? Screenshots { get; set; }
    }
    private sealed class CfLogo   { [JsonPropertyName("url")] public string? Url { get; set; } [JsonPropertyName("thumbnailUrl")] public string? ThumbnailUrl { get; set; } }
    private sealed class CfAuthor { [JsonPropertyName("name")] public string? Name { get; set; } }
    private sealed class CfCategory { [JsonPropertyName("name")] public string Name { get; set; } = ""; }
    private sealed class CfLinks
    {
        [JsonPropertyName("websiteUrl")] public string? WebsiteUrl { get; set; }
        [JsonPropertyName("wikiUrl")] public string? WikiUrl { get; set; }
        [JsonPropertyName("issuesUrl")] public string? IssuesUrl { get; set; }
        [JsonPropertyName("sourceUrl")] public string? SourceUrl { get; set; }
    }
    private sealed class CfScreenshot
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("thumbnailUrl")] public string? ThumbnailUrl { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
    }
    private sealed class CfFile
    {
        [JsonPropertyName("id")]            public int Id { get; set; }
        [JsonPropertyName("modId")]         public int ModId { get; set; }
        [JsonPropertyName("displayName")]   public string DisplayName { get; set; } = "";
        [JsonPropertyName("fileName")]      public string FileName { get; set; } = "";
        [JsonPropertyName("fileDate")]      public DateTimeOffset FileDate { get; set; }
        [JsonPropertyName("fileLength")]    public long FileLength { get; set; }
        [JsonPropertyName("downloadCount")] public long DownloadCount { get; set; }
        [JsonPropertyName("downloadUrl")]   public string? DownloadUrl { get; set; }
        [JsonPropertyName("gameVersions")]  public List<string>? GameVersions { get; set; }
        [JsonPropertyName("releaseType")]   public int ReleaseType { get; set; }
        [JsonPropertyName("dependencies")]  public List<CfFileDependency>? Dependencies { get; set; }
    }
    private sealed class CfFileDependency
    {
        [JsonPropertyName("modId")]        public int ModId { get; set; }
        [JsonPropertyName("relationType")] public int RelationType { get; set; }
    }
}
