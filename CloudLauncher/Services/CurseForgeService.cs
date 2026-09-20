using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudLauncher.Shared;

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

    // CurseForge Minecraft classIds: 6=Mods, 12=ResourcePacks, 17=Worlds, 4471=Modpacks, 6552=Shaders
    public const int ClassIdMods = 6;
    public const int ClassIdResourcePacks = 12;
    public const int ClassIdModpacks = 4471;
    public const int ClassIdWorlds = 17;
    public const int ClassIdShaders = 6552;

    public async Task<List<ModSummary>> SearchAsync(
        string query,
        string? mcVersion = null,
        string? loader = null,
        int limit = 20,
        int offset = 0,
        int classId = ClassIdMods,
        int sortField = 2,
        IReadOnlyList<int>? categoryIds = null,
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
        // CF takes a single categoryId or a JSON array of categoryIds — use whichever fits, since a
        // single-element categoryId is the most broadly-supported form.
        var cats = categoryIds?.Where(c => c > 0).Distinct().ToList();
        if (cats is { Count: 1 })
            parameters.Add(("categoryId", cats[0].ToString()));
        else if (cats is { Count: > 1 })
            parameters.Add(("categoryIds", "[" + string.Join(",", cats) + "]"));
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
            throw await CurseForgeRequestExceptionAsync("search", response, ct);

        var payload = await response.Content.ReadFromJsonAsync<CfSearchResponse>(Json, ct);
        return payload?.Data?.Select(ToSummary).ToList() ?? new();
    }

    private readonly Dictionary<int, List<ModBrowseCategory>> _categoryCache = new();

    /// <summary>CurseForge's categories for a class (Mods / Resource Packs / …), cached per class for
    /// the session. Returns each as (display name, numeric id) — the id is what search's
    /// <c>categoryId</c> wants.</summary>
    public async Task<List<ModBrowseCategory>> GetCategoriesAsync(int classId = ClassIdMods, CancellationToken ct = default)
    {
        if (_categoryCache.TryGetValue(classId, out var cached)) return cached;
        try
        {
            var resp = await ProxyGetJsonAsync<CfCategoriesResponse>($"categories?gameId={MinecraftGameId}&classId={classId}", ct);
            var list = (resp?.Data ?? new())
                .Where(c => c.Id > 0 && !string.IsNullOrWhiteSpace(c.Name))
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c => new ModBrowseCategory(c.Name!, c.Id.ToString()))
                .ToList();
            _categoryCache[classId] = list;
            return list;
        }
        catch { return new(); }
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

    public Task<List<ModVersion>> GetVersionsAsync(int modId, CancellationToken ct = default) =>
        GetVersionsAsync(modId, null, null, 0, ct);

    /// <summary>A mod's files, newest first — all of them, or only those for a Minecraft version and
    /// loader. The filter goes to CurseForge, which matters: JEI's whole history is 3,600+ files (73
    /// pages of fifty), its NeoForge 1.21.1 files about 220. <paramref name="maxPages"/> stops early
    /// (0 = every page); an update check only needs the first page, which holds the newest files.</summary>
    public async Task<List<ModVersion>> GetVersionsAsync(int modId, string? mcVersion, string? loader,
        int maxPages = 0, CancellationToken ct = default)
    {
        var pages = 0;
        const int pageSize = 50;
        var all = new List<ModVersion>();
        var index = 0;
        var filter = "";
        if (!string.IsNullOrWhiteSpace(mcVersion))
            filter += "&gameVersion=" + Uri.EscapeDataString(mcVersion.Trim());
        if (!string.IsNullOrWhiteSpace(loader) && LoaderToInt(loader) is > 0 and var loaderType)
            filter += "&modLoaderType=" + loaderType;

        while (true)
        {
            using var response = await ProxyGetAsync($"mods/{modId}/files?index={index}&pageSize={pageSize}{filter}", ct);
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
            if (maxPages > 0 && ++pages >= maxPages) break;
            index += pageSize;
        }

        return all;
    }

    public async Task<ModVersion?> GetVersionAsync(int modId, int fileId, CancellationToken ct = default)
    {
        var resp = await ProxyGetJsonAsync<CfFileResponse>($"mods/{modId}/files/{fileId}", ct);
        return resp?.Data is null ? null : ToCfVersion(modId, resp.Data);
    }

    /// <summary>The changelog CurseForge stores for one file — HTML, fetched on demand because it is
    /// not part of the file listing. Null when the file has none or the call failed.</summary>
    public async Task<string?> GetChangelogAsync(int modId, int fileId, CancellationToken ct = default)
    {
        try
        {
            var resp = await ProxyGetJsonAsync<JsonElement>($"mods/{modId}/files/{fileId}/changelog", ct);
            if (resp.ValueKind == JsonValueKind.Undefined) return null;
            return resp.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String
                ? data.GetString() : null;
        }
        catch { return null; }
    }

    private const int BatchSize = 50;

    /// <summary>Several mods in one round trip (<c>POST /mods</c>), keyed by id. Ids CurseForge does
    /// not know are simply absent. Replaces one <c>GET /mods/{id}</c> per mod wherever a whole pack
    /// is being identified — the difference between 8 calls and 400.</summary>
    public async Task<Dictionary<int, ModSummary>> GetModsAsync(IEnumerable<int> modIds, CancellationToken ct = default)
    {
        var result = new Dictionary<int, ModSummary>();
        var ids = modIds.Where(i => i > 0).Distinct().ToList();
        foreach (var chunk in ids.Chunk(BatchSize))
        {
            using var resp = await ProxyPostJsonAsync("mods", new { modIds = chunk, filterPcOnly = false }, ct);
            if (!resp.IsSuccessStatusCode)
                throw await CurseForgeRequestExceptionAsync("mod lookup", resp, ct);
            var payload = await resp.Content.ReadFromJsonAsync<CfSearchResponse>(Json, ct);
            foreach (var m in payload?.Data ?? new())
                result[m.Id] = ToSummary(m);
        }
        return result;
    }

    /// <summary>Several files in one round trip (<c>POST /mods/files</c>), keyed by file id. Each
    /// version's id is <c>modId:fileId</c> and its single file carries CurseForge's download URL
    /// (empty when the author opted out of third-party distribution).</summary>
    public async Task<Dictionary<int, ModVersion>> GetFilesAsync(IEnumerable<int> fileIds, CancellationToken ct = default)
    {
        var result = new Dictionary<int, ModVersion>();
        var ids = fileIds.Where(i => i > 0).Distinct().ToList();
        foreach (var chunk in ids.Chunk(BatchSize))
        {
            using var resp = await ProxyPostJsonAsync("mods/files", new { fileIds = chunk }, ct);
            if (!resp.IsSuccessStatusCode)
                throw await CurseForgeRequestExceptionAsync("file lookup", resp, ct);
            var payload = await resp.Content.ReadFromJsonAsync<CfFilesResponse>(Json, ct);
            foreach (var f in payload?.Data ?? new())
                if (f.ModId > 0) result[f.Id] = ToCfVersion(f.ModId, f);
        }
        return result;
    }

    public async Task<Dictionary<long, (ModSummary mod, ModVersion version)>> MatchFingerprintsAsync(
        IEnumerable<long> fingerprints, CancellationToken ct = default)
    {
        var distinct = fingerprints.Distinct().ToList();
        if (distinct.Count == 0) return new();

        var body = new { fingerprints = distinct };
        using var resp = await ProxyPostJsonAsync("fingerprints", body, ct);
        // Throw rather than return "no matches". This call is how the launcher recognises which
        // jars on disk are already installed, and a failure is not the same answer as an empty
        // result: reporting a blocked or erroring call as "none of these mods exist on CurseForge"
        // is what made downloaded mods show up as not downloaded while browsing.
        if (!resp.IsSuccessStatusCode)
            throw await CurseForgeRequestExceptionAsync("fingerprint match", resp, ct);

        var payload = await resp.Content.ReadFromJsonAsync<CfFingerprintResponse>(Json, ct);
        var result = new Dictionary<long, (ModSummary, ModVersion)>();
        var exactFingerprints = payload?.Data?.ExactFingerprints ?? new();
        var requestedFingerprints = distinct.ToHashSet();
        var exactMatches = payload?.Data?.ExactMatches ?? new List<CfFingerprintMatch>();

        // One batched lookup for every matched mod's summary; a mod the batch missed falls back to
        // the single GET below, so a partial batch answer never drops a match.
        Dictionary<int, ModSummary> mods;
        try { mods = await GetModsAsync(exactMatches.Where(m => m.File is not null).Select(m => m.File!.ModId), ct); }
        catch { mods = new(); }

        var matchIndex = 0;
        foreach (var match in exactMatches)
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
            if (!mods.TryGetValue(match.File.ModId, out var mod))
            {
                mod = await GetModAsync(match.File.ModId, ct);
                if (mod is null) continue;
            }
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
        {
            if (response.StatusCode is System.Net.HttpStatusCode.Forbidden
                                    or System.Net.HttpStatusCode.Unauthorized
                && await DistributionOptOutAsync(modId, ct) is { } project)
                throw new HttpRequestException(
                    $"{project.Name} can't be downloaded through the launcher: its author has turned off " +
                    "third-party distribution on CurseForge, so the API refuses a download URL for it — " +
                    "with any key. Download the file from its project page and add it to the pack by hand" +
                    (project.WebsiteUrl is null ? "." : $": {project.WebsiteUrl}"),
                    null, response.StatusCode);
            throw await CurseForgeRequestExceptionAsync("download URL", response, ct);
        }

        var resp = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        return resp.ValueKind != JsonValueKind.Undefined && resp.TryGetProperty("data", out var data)
            ? data.GetString() : null;
    }

    /// <summary>Names the project when CurseForge refuses its downloads because the author opted out
    /// of third-party distribution (<c>allowModDistribution: false</c>) — otherwise null.
    ///
    /// The launcher only asks for a download URL when the file listing came back without one, which
    /// is exactly what that opt-out looks like, and the endpoint then answers a bare 403 for every
    /// key. A rejected key answers the same way, and the two want opposite things from whoever reads
    /// the error: rotate a key, or go fetch the file by hand. Asking the mod endpoint settles it
    /// either way — if the key really is the problem, this call fails too and the caller falls back
    /// to reporting the key.</summary>
    private async Task<(string Name, string? WebsiteUrl)?> DistributionOptOutAsync(int modId, CancellationToken ct)
    {
        try
        {
            var mod = (await ProxyGetJsonAsync<CfModResponse>($"mods/{modId}", ct))?.Data;
            return mod is null || mod.AllowModDistribution != false
                ? null
                : (mod.Name, mod.Links?.WebsiteUrl);
        }
        catch { return null; }
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

    /// <summary>The launcher server's own explanation of a failure, when the body is one of its JSON
    /// error payloads. The server knows things this side cannot — whether a key is configured, which
    /// upstream routes it tried and how each was refused — so its wording wins whenever it has one.</summary>
    private static string? ServerMessage(string body)
    {
        var trimmed = body?.TrimStart();
        if (string.IsNullOrEmpty(trimmed) || trimmed[0] != '{') return null;
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.String)
                return null;
            var text = error.GetString();
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (doc.RootElement.TryGetProperty("detail", out var detail) &&
                detail.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(detail.GetString()))
                text += $" ({detail.GetString()})";
            return text;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>A one-line gist of an upstream error body. The CloudFront block page is ~900 bytes of
    /// HTML, and pasting it whole into a message box buries the sentence that matters.</summary>
    private static string? Gist(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var text = System.Text.RegularExpressions.Regex.Replace(body, "<[^>]*>", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length == 0) return null;
        return text.Length > 180 ? text[..180].TrimEnd() + "…" : text;
    }

    private static async Task<HttpRequestException> CurseForgeRequestExceptionAsync(
        string operation,
        HttpResponseMessage response,
        CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        string message;

        if (ServerMessage(body) is { Length: > 0 } explained)
        {
            message = $"CurseForge {operation} failed: {explained}";
        }
        else if (UpstreamEdge.IsBlockPage(body))
        {
            // Only reached when a block page arrives unproxied — an older server, or one whose
            // routing was bypassed. Quote the CloudFront request id rather than the page: it's the
            // one part CurseForge support can act on when asking them to unblock the address.
            var id = UpstreamEdge.RequestId(body);
            message = $"CurseForge {operation} failed: CurseForge's CDN is blocking the launcher server's " +
                      "address, so the request never reached the API. This is not the API key — an admin " +
                      "needs that address unblocked, or CurseForge traffic routed via another host " +
                      "(set Upstream:curseforge on the server; see deploy/UPSTREAM-ROUTING.md)." +
                      (id is null ? "" : $" (CloudFront request {id})");
        }
        else
        {
            // CurseForge answers a burst of requests with a bare, fast 403 from its edge — the same
            // status a rejected key gets, but the key rejection says so in its body. Telling the two
            // apart matters: one wants a pause, the other wants an admin.
            var keyRejected = body?.Contains("API Key", StringComparison.OrdinalIgnoreCase) == true
                              || body?.Contains("api key", StringComparison.OrdinalIgnoreCase) == true;
            message = response.StatusCode switch
            {
                System.Net.HttpStatusCode.ServiceUnavailable =>
                    $"CurseForge {operation} failed: the server has no CurseForge API key configured.",
                System.Net.HttpStatusCode.TooManyRequests =>
                    $"CurseForge {operation} failed: CurseForge is rate-limiting the launcher server " +
                    "(too many requests in a short time). Wait a minute and try again.",
                System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden when keyRejected =>
                    $"CurseForge {operation} failed: the server API key was rejected. An admin must update it in the dev menu.",
                System.Net.HttpStatusCode.Forbidden =>
                    $"CurseForge {operation} failed: CurseForge refused the request (HTTP 403). This is usually a " +
                    "temporary rate limit after a burst of requests from the launcher server — wait a minute and try again.",
                System.Net.HttpStatusCode.Unauthorized =>
                    $"CurseForge {operation} failed: the server API key was rejected. An admin must update it in the dev menu.",
                _ => $"CurseForge {operation} failed: {(int)response.StatusCode} {response.ReasonPhrase}"
            };
            if (Gist(body ?? "") is { } gist && response.StatusCode is not (System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests))
                message += $" ({gist})";
        }

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
        // null when CurseForge did not say; false means the author blocked third-party downloads.
        [JsonPropertyName("allowModDistribution")] public bool? AllowModDistribution { get; set; }
        [JsonPropertyName("screenshots")]   public List<CfScreenshot>? Screenshots { get; set; }
    }
    private sealed class CfLogo   { [JsonPropertyName("url")] public string? Url { get; set; } [JsonPropertyName("thumbnailUrl")] public string? ThumbnailUrl { get; set; } }
    private sealed class CfAuthor { [JsonPropertyName("name")] public string? Name { get; set; } }
    private sealed class CfCategory { [JsonPropertyName("name")] public string Name { get; set; } = ""; }

    private sealed class CfCategoriesResponse { [JsonPropertyName("data")] public List<CfCategoryOption>? Data { get; set; } }
    private sealed class CfCategoryOption
    {
        [JsonPropertyName("id")]   public int Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
    }
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
