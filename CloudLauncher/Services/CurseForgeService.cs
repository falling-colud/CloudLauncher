using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>One entry of a CurseForge mod's <c>latestFilesIndexes</c>: the newest file of one
/// release type (1 release, 2 beta, 3 alpha) for one Minecraft version and loader (CurseForge's
/// loader id: 1 Forge, 4 Fabric, 5 Quilt, 6 NeoForge; 0 when the file names none).</summary>
public sealed record CurseForgeFileIndex(string GameVersion, int FileId, int ReleaseType, int ModLoader);

/// <summary>What <see cref="CurseForgeService.GetProjectFactsAsync"/> says about one project.</summary>
/// <param name="ClassId">Mods, resource packs, shaders... (<see cref="CurseForgeService.ClassIdMods"/> and
/// the other ClassId constants); 0 when CurseForge did not say.</param>
/// <param name="AllowsDistribution">False when the author turned off third-party distribution: its files
/// can be listed in a pack, but only the CurseForge app can download them.</param>
public readonly record struct CurseForgeProjectFacts(int ClassId, bool AllowsDistribution, string Name);

/// <summary>
/// CurseForge API wrapper. Requests go through the launcher server's proxy
/// (<c>/proxy/curseforge/*</c>), which attaches the admin-configured API key, so clients never see it.
/// A user with a key of their own (Settings -> Mod stores) talks to CurseForge directly;
/// <see cref="ApiClient.ProxyAsync"/> decides per call and answers in the proxy's shape either way.
/// </summary>
public sealed class CurseForgeService
{
    private const int MinecraftGameId = 432;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    // Long-lived client for CDN downloads. A per-call `new HttpClient()` exhausts sockets
    // (each instance opens its own pool and lingers in TIME_WAIT after disposal).
    private static readonly HttpClient DownloadHttp = CreateDownloadClient();

    private static HttpClient CreateDownloadClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        ApiClient.ApplyUserAgent(client);
        return client;
    }
    private readonly ApiClient _api;

    public CurseForgeService(ApiClient api) { _api = api; }

    /// <summary>Always true: configuration lives on the server. If the admin
    /// hasn't set the key, the proxy returns 503 with an actionable message.</summary>
    public bool IsConfigured => true;

    // ── search ────────────────────────────────────────────────────────────────

    // CurseForge Minecraft classIds: 6=Mods, 12=ResourcePacks, 17=Worlds, 4471=Modpacks, 6552=Shaders
    public const int ClassIdMods = 6;
    public const int ClassIdResourcePacks = 12;
    public const int ClassIdModpacks = 4471;
    public const int ClassIdWorlds = 17;
    public const int ClassIdShaders = 6552;
    /// <summary>"Data Packs", from <c>categories?gameId=432&amp;classesOnly=true</c> (checked
    /// 2026-09-28).</summary>
    public const int ClassIdDataPacks = 6945;

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
        // CF takes a single categoryId or a JSON array of categoryIds. The single form is used for one
        // category because it is the most widely supported.
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

    /// <summary>CurseForge's categories for a class (Mods / Resource Packs / ...), cached per class for
    /// the session, as (display name, numeric id). The id is what search's <c>categoryId</c> takes.</summary>
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

    /// <summary>A mod's files, newest first: all of them, or only those for a Minecraft version and
    /// loader. The filter is applied by CurseForge, which matters for big mods (JEI has 3,600+ files, about
    /// 220 of them for NeoForge 1.21.1). <paramref name="maxPages"/> stops early (0 = every page); an
    /// update check only needs the first page, which holds the newest files.</summary>
    /// <param name="pacing"><see cref="ProxyPacing.UpdateCheck"/> when the list is being fetched for
    /// an update check, which is paced by its own setting.</param>
    public async Task<List<ModVersion>> GetVersionsAsync(int modId, string? mcVersion, string? loader,
        int maxPages = 0, CancellationToken ct = default, ProxyPacing pacing = ProxyPacing.Default)
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
            using var response = await ProxyGetAsync($"mods/{modId}/files?index={index}&pageSize={pageSize}{filter}", ct, pacing);
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

    /// <summary>The changelog CurseForge stores for one file (HTML), fetched on demand because it is
    /// not part of the file listing. Null when the file has none.</summary>
    /// <remarks>Throws on a failed call, like the Modrinth path, so a changelog viewer can tell "the
    /// store did not answer" apart from "this version has no changelog".</remarks>
    public async Task<string?> GetChangelogAsync(int modId, int fileId, CancellationToken ct = default)
    {
        using var response = await ProxyGetAsync($"mods/{modId}/files/{fileId}/changelog", ct);
        if (!response.IsSuccessStatusCode)
            throw await CurseForgeRequestExceptionAsync("changelog", response, ct);
        var resp = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        if (resp.ValueKind == JsonValueKind.Undefined) return null;
        return resp.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String
            ? data.GetString() : null;
    }

    /// <summary>Ids per bulk request (<c>POST /mods</c>, <c>POST /mods/files</c>).</summary>
    public const int BatchSize = 50;

    /// <summary>Several mods in one round trip (<c>POST /mods</c>), keyed by id. Ids CurseForge doesn't
    /// know are absent. Use this instead of one <c>GET /mods/{id}</c> per mod when identifying a whole
    /// pack.</summary>
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

    /// <summary>Each project's class and distribution setting, fifty projects per request
    /// (<c>POST /mods</c>), keyed by project id. A project CurseForge does not know is absent.</summary>
    /// <remarks>
    /// <para>A launcher installing a CurseForge modpack puts each file in the folder its project's class
    /// names (<c>mods</c>, <c>resourcepacks</c>, <c>shaderpacks</c>), so export and import both need
    /// this.</para>
    /// <para>A project whose author turned off third-party distribution can be listed in a pack, but only
    /// the CurseForge app can download it, so the export reports which files those are.</para>
    /// <para>Throws when the call fails, like <see cref="GetModsAsync"/>.</para>
    /// </remarks>
    public async Task<Dictionary<int, CurseForgeProjectFacts>> GetProjectFactsAsync(IEnumerable<int> modIds, CancellationToken ct = default)
    {
        var result = new Dictionary<int, CurseForgeProjectFacts>();
        foreach (var chunk in modIds.Where(i => i > 0).Distinct().Chunk(BatchSize))
        {
            using var resp = await ProxyPostJsonAsync("mods", new { modIds = chunk, filterPcOnly = false }, ct);
            if (!resp.IsSuccessStatusCode)
                throw await CurseForgeRequestExceptionAsync("mod lookup", resp, ct);
            var payload = await resp.Content.ReadFromJsonAsync<CfSearchResponse>(Json, ct);
            foreach (var m in payload?.Data ?? new())
                result[m.Id] = new CurseForgeProjectFacts(m.ClassId ?? 0, m.AllowModDistribution != false, m.Name);
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

    /// <summary>
    /// Each mod's <c>latestFilesIndexes</c> (the newest file per Minecraft version, loader and release
    /// type) for up to <see cref="BatchSize"/> mods in one request (<c>POST /mods</c>), keyed by mod id.
    /// A mod CurseForge does not know is absent.
    /// </summary>
    /// <remarks>Update checks use this instead of one file list per mod: the index says which file is
    /// newest, and only files that aren't installed need their details fetched
    /// (<see cref="GetFilesAsync"/>). Throws when the call fails, like <see cref="GetModsAsync"/>.</remarks>
    public async Task<Dictionary<int, IReadOnlyList<CurseForgeFileIndex>>> GetLatestFileIndexesAsync(
        IReadOnlyCollection<int> modIds, CancellationToken ct = default)
    {
        if (modIds.Count == 0) return new();
        using var resp = await ProxyPostJsonAsync("mods", new { modIds, filterPcOnly = false }, ct);
        if (!resp.IsSuccessStatusCode)
            throw await CurseForgeRequestExceptionAsync("mod lookup", resp, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return ParseLatestFileIndexes(doc.RootElement);
    }

    /// <summary>Reads <c>data[].latestFilesIndexes</c> out of a <c>POST /mods</c> answer.</summary>
    /// <remarks>Parsed by hand so an entry with an unexpected shape (a missing field, a wrong type, a
    /// new loader id) only loses that entry, not the whole answer; that mod is then asked about on its
    /// own. Public so it can be tested against a sample without a CurseForge key.</remarks>
    public static Dictionary<int, IReadOnlyList<CurseForgeFileIndex>> ParseLatestFileIndexes(JsonElement root)
    {
        var result = new Dictionary<int, IReadOnlyList<CurseForgeFileIndex>>();
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var mod in data.EnumerateArray())
        {
            if (mod.ValueKind != JsonValueKind.Object || !TryInt(mod, "id", out var modId) || modId <= 0) continue;
            var entries = new List<CurseForgeFileIndex>();
            if (mod.TryGetProperty("latestFilesIndexes", out var index) && index.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in index.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    if (!TryInt(e, "fileId", out var fileId) || fileId <= 0) continue;
                    if (!e.TryGetProperty("gameVersion", out var gv) || gv.ValueKind != JsonValueKind.String) continue;
                    // No release type is read as alpha, the least stable, so it is only ever offered
                    // to a mod that follows every channel.
                    var releaseType = TryInt(e, "releaseType", out var rt) ? rt : 3;
                    // No loader (0 is CurseForge's "any") matches only a pack that has no loader.
                    var loader = TryInt(e, "modLoader", out var ml) ? ml : 0;
                    entries.Add(new CurseForgeFileIndex(gv.GetString() ?? "", fileId, releaseType, loader));
                }
            }
            result[modId] = entries;
        }
        return result;

        static bool TryInt(JsonElement obj, string name, out int value)
        {
            value = 0;
            return obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out value);
        }
    }

    /// <summary>CurseForge's release type (1 release, 2 beta, 3 alpha) as the launcher's channel word.
    /// Anything unknown counts as alpha, the least stable.</summary>
    public static string ReleaseChannelOf(int releaseType) =>
        releaseType == 1 ? ModUpdateChannel.Release : releaseType == 2 ? ModUpdateChannel.Beta : ModUpdateChannel.Alpha;

    /// <summary>The newest file per release type (anything but release and beta counts as alpha) for
    /// one Minecraft version and loader, out of a mod's <see cref="GetLatestFileIndexesAsync">index</see>.
    /// Empty when the index says nothing about that pairing; the caller then asks about the mod on its
    /// own instead of calling it current.</summary>
    /// <param name="loader">The pack's loader tag ("neoforge", ...), or null for a pack without one.</param>
    public static IReadOnlyList<CurseForgeFileIndex> NewestFilesFor(IEnumerable<CurseForgeFileIndex> index,
        string mcVersion, string? loader)
    {
        int? loaderType = null;
        if (!string.IsNullOrWhiteSpace(loader))
        {
            loaderType = LoaderToInt(loader);
            if (loaderType == 0) return Array.Empty<CurseForgeFileIndex>(); // a loader CurseForge has no id for
        }
        return index
            .Where(e => string.Equals(e.GameVersion, mcVersion, StringComparison.OrdinalIgnoreCase)
                        && (loaderType is null || e.ModLoader == loaderType))
            .GroupBy(e => e.ReleaseType is 1 or 2 ? e.ReleaseType : 3)
            .Select(g => g.MaxBy(e => e.FileId)! with { ReleaseType = g.Key })
            .ToList();
    }

    /// <summary>Fingerprints per request (<c>POST /fingerprints</c>). CurseForge publishes no limit; a
    /// thousand keeps a request to a few KB and its answer, which carries every matched file whole, to
    /// a few MB, so a 2,000-mod pack is two round trips.</summary>
    public const int FingerprintsPerRequest = 1000;

    public async Task<Dictionary<long, (ModSummary mod, ModVersion version)>> MatchFingerprintsAsync(
        IEnumerable<long> fingerprints, CancellationToken ct = default)
    {
        var distinct = fingerprints.Distinct().ToList();
        var result = new Dictionary<long, (ModSummary, ModVersion)>();
        foreach (var chunk in distinct.Chunk(FingerprintsPerRequest))
            await MatchFingerprintChunkAsync(chunk, result, ct);
        return result;
    }

    private async Task MatchFingerprintChunkAsync(long[] distinct, Dictionary<long, (ModSummary, ModVersion)> result,
        CancellationToken ct)
    {
        var body = new { fingerprints = distinct };
        using var resp = await ProxyPostJsonAsync("fingerprints", body, ct);
        // Throw instead of returning "no matches": this call is how the launcher recognises which jars
        // on disk are already installed, and treating a failed call as "none of these exist on
        // CurseForge" would show downloaded mods as not downloaded.
        if (!resp.IsSuccessStatusCode)
            throw await CurseForgeRequestExceptionAsync("fingerprint match", resp, ct);

        var payload = await resp.Content.ReadFromJsonAsync<CfFingerprintResponse>(Json, ct);
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

    /// <summary>
    /// The CDN link for a file that was listed without one (<c>GET /mods/{id}/files/{id}/download-url</c>).
    /// Never empty: when CurseForge will not hand one out, which is the author's third-party
    /// distribution opt-out far more often than anything else, this throws with the file's own
    /// sentence naming the project, its page and the CurseForge app, so the caller can show it as is.
    /// </summary>
    /// <remarks>Only for a file whose listing carried no URL. The listing's URL is the same CDN link
    /// and needs no extra request; see <see cref="ResolveDownloadUrlAsync"/>.</remarks>
    public async Task<string> GetDownloadUrlAsync(int modId, int fileId, CancellationToken ct = default)
    {
        using var response = await ProxyGetAsync($"mods/{modId}/files/{fileId}/download-url", ct);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized
                && await DistributionAsync(modId, ct) is { OptedOut: true } project)
                throw NotDistributable(project.Name, project.WebsiteUrl, fileId, optedOut: true);
            throw await CurseForgeRequestExceptionAsync("download URL", response, ct);
        }

        var resp = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        var url = resp.ValueKind != JsonValueKind.Undefined && resp.TryGetProperty("data", out var data)
                  && data.ValueKind == JsonValueKind.String ? data.GetString() : null;
        if (!string.IsNullOrWhiteSpace(url)) return url;

        // A 200 with no link is the same opt-out, answered politely.
        var facts = await DistributionAsync(modId, ct);
        throw NotDistributable(facts?.Name ?? $"CurseForge project {modId}", facts?.WebsiteUrl, fileId,
            optedOut: facts?.OptedOut == true);
    }

    /// <summary>
    /// The CDN link for <paramref name="file"/> of <paramref name="version"/>: the one the listing
    /// carried, else the one <see cref="GetDownloadUrlAsync"/> fetches. Modrinth files always carry
    /// theirs. Throws, with the file's own sentence, for a file CurseForge will not hand out.
    /// </summary>
    public async Task<string> ResolveDownloadUrlAsync(ModSummary mod, ModVersion version, ModVersionFile file,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(file.DownloadUrl)) return file.DownloadUrl;
        if (version.Source != ModSource.CurseForge)
            throw new StoreRequestException("Modrinth", "download", StoreFailure.NotDistributable,
                $"{mod.Name} has no download link for {file.Filename}. Get the file from its project page and add it by hand.",
                System.Net.HttpStatusCode.NotFound);
        if (!TryParseFileIds(mod, version, out var modId, out var fileId))
            throw new StoreRequestException("CurseForge", "download", StoreFailure.Other,
                $"The CurseForge file for {mod.Name} could not be identified, so it cannot be downloaded.",
                System.Net.HttpStatusCode.NotFound);
        return await GetDownloadUrlAsync(modId, fileId, ct);
    }

    /// <summary>The per-file sentence for a download CurseForge will not hand out.</summary>
    private static StoreRequestException NotDistributable(string name, string? websiteUrl, int fileId, bool optedOut)
    {
        var page = websiteUrl is null ? "." : $": {websiteUrl}";
        var plain = optedOut
            ? $"{name} can't be downloaded through the launcher: its author turned off third-party downloads on " +
              $"CurseForge, so only the CurseForge app can fetch it. Download the file from its project page and " +
              $"add it to the pack by hand{page}"
            : $"CurseForge has no download link for {name} (file {fileId}). Download the file from its project page " +
              $"and add it to the pack by hand{page}";
        return new StoreRequestException("CurseForge", "download", StoreFailure.NotDistributable, plain,
            System.Net.HttpStatusCode.Forbidden);
    }

    /// <summary>Whether the project's author opted out of third-party distribution
    /// (<c>allowModDistribution: false</c>), with the project's name and page; null when CurseForge
    /// could not say.
    ///
    /// The opt-out and a rejected key both get a bare 403 from the download-URL endpoint, but need
    /// different fixes: download the file by hand, or fix the key. Asking the mod endpoint tells them
    /// apart; if the key is the problem this call fails too and the caller reports the request.</summary>
    private async Task<(string Name, string? WebsiteUrl, bool OptedOut)?> DistributionAsync(int modId, CancellationToken ct)
    {
        try
        {
            var mod = (await ProxyGetJsonAsync<CfModResponse>($"mods/{modId}", ct))?.Data;
            return mod is null ? null : (mod.Name, mod.Links?.WebsiteUrl, mod.AllowModDistribution == false);
        }
        catch { return null; }
    }

    // ── modpack manifest resolution ───────────────────────────────────────────

    public async Task<List<(int ModId, int FileId)>> GetModpackFilesAsync(int modId, int fileId, CancellationToken ct = default)
    {
        // CurseForge modpack files reference their mods in the manifest.json inside the zip
        // This returns the file IDs listed in the manifest
        var url = await GetDownloadUrlAsync(modId, fileId, ct);

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

    private Task<HttpResponseMessage> ProxyGetAsync(string pathAndQuery, CancellationToken ct,
        ProxyPacing pacing = ProxyPacing.Default) =>
        _api.ProxyAsync("curseforge", HttpMethod.Get, pathAndQuery, null, ct, pacing);

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
        // Loaders are the entries that are not version numbers: "26.3" is a Minecraft version too.
        f.GameVersions?.Where(v => v.Length > 0 && !char.IsDigit(v[0])).ToArray() ?? Array.Empty<string>(),
        ReleaseChannelOf(f.ReleaseType),
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

    /// <summary>
    /// What to tell the user about a failed call, after the waits <see cref="ApiClient.ProxyAsync"/>
    /// already made. The launcher server's own answers carry a code and say what happened (which key,
    /// whether a route was blocked), so their wording wins; a CurseForge answer passed through is
    /// read by status.
    /// </summary>
    /// <remarks>The only answer that names an API key is the server's <c>own_key_rejected</c>, about the
    /// key the user set themselves. A 401 or 403 on the shared key is reported as CurseForge refusing
    /// the server: after the retries it is a throttle most of the time, and when it is the key, only
    /// the admin can act on it (the server log says which).</remarks>
    private static async Task<StoreRequestException> CurseForgeRequestExceptionAsync(
        string operation,
        HttpResponseMessage response,
        CancellationToken ct)
    {
        const string store = "CurseForge";
        var body = await response.Content.ReadAsStringAsync(ct);
        var status = response.StatusCode;
        var (serverMessage, code) = StoreRequestException.ReadServerError(body);

        StoreRequestException Fail(StoreFailure failure, string plain, string? detail = null) =>
            new(store, operation, failure, plain, status, detail);

        switch (code)
        {
            case "own_key_rejected":
                return Fail(StoreFailure.KeyRejected, serverMessage
                    ?? "CurseForge rejected the API key you set in Settings > Mod stores. Check it, or clear the box to go back to the launcher's shared key.");
            case "key_not_configured":
                return Fail(StoreFailure.KeyMissing, serverMessage
                    ?? "The launcher server has no CurseForge key configured yet. You can set your own in Settings > Mod stores.");
            case "upstream_blocked":
                return Fail(StoreFailure.Blocked, serverMessage
                    ?? "CurseForge is refusing requests from the launcher server right now. Try again later.");
            case "upstream_unreachable":
                return Fail(StoreFailure.Unreachable, serverMessage ?? StoreRequestException.UnreachableSentence(store));
            case "upstream_rate_limited" or "too_many_requests":
                return Fail(StoreFailure.Busy, StoreRequestException.BusySentence(store), serverMessage);
            case "db_unavailable":
                return Fail(StoreFailure.Unreachable, "The launcher server is restarting. Try again in a moment.");
            case "not_found":
                return Fail(StoreFailure.Other, "The launcher server does not forward that request. Update the launcher, or the server.");
            case not null:
                return Fail(StoreFailure.Other, serverMessage ?? $"The launcher server refused the request (HTTP {(int)status}).");
        }

        if (UpstreamEdge.IsBlockPage(body))
        {
            // Only reached when a block page arrives unproxied (an older server, or bypassed routing).
            // Quote the CloudFront request id instead of the page: CurseForge support needs it to
            // unblock the address.
            var id = UpstreamEdge.RequestId(body);
            return Fail(StoreFailure.Blocked,
                "CurseForge's CDN is blocking the launcher server's address, so the request never reached the API. " +
                "An admin needs that address unblocked, or CurseForge traffic routed via another host " +
                "(set Upstream:curseforge on the server; see deploy/UPSTREAM-ROUTING.md).",
                id is null ? null : $"CloudFront request {id}");
        }

        return status switch
        {
            System.Net.HttpStatusCode.TooManyRequests =>
                Fail(StoreFailure.Busy, StoreRequestException.BusySentence(store)),
            System.Net.HttpStatusCode.BadGateway or System.Net.HttpStatusCode.ServiceUnavailable
                or System.Net.HttpStatusCode.GatewayTimeout =>
                Fail(StoreFailure.Unreachable, StoreRequestException.UnreachableSentence(store)),
            System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
                Fail(StoreFailure.Refused,
                    $"CurseForge is refusing the launcher server's requests right now (HTTP {(int)status}). Try again " +
                    "in a minute; if it keeps happening, tell whoever runs the launcher server."),
            System.Net.HttpStatusCode.NotFound =>
                Fail(StoreFailure.NotFound, "CurseForge does not have that any more."),
            _ => Fail(StoreFailure.Other, $"CurseForge answered with an error (HTTP {(int)status}).",
                serverMessage ?? StoreRequestException.Gist(body))
        };
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
        // Mods, resource packs, shaders...: see ClassIdMods and the other ClassId constants.
        [JsonPropertyName("classId")] public int? ClassId { get; set; }
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
