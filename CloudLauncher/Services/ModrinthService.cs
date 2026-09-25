using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudLauncher.Services;

// ── shared mod model ─────────────────────────────────────────────────────────

public enum ModSource { Modrinth, CurseForge, External }

public sealed record ModSummary(
    string Id,
    string Slug,
    string Name,
    string? Author,
    string? Description,
    long DownloadCount,
    string? IconUrl,
    ModSource Source,
    string[] Categories);

public sealed record ModVersion(
    string Id,
    string Name,
    string VersionNumber,
    string[] GameVersions,
    string[] Loaders,
    string ReleaseChannel,    // release / beta / alpha
    DateTimeOffset DatePublished,
    long DownloadCount,
    string? Changelog,
    ModSource Source,
    IReadOnlyList<ModVersionFile> Files,
    IReadOnlyList<ModDependency> Dependencies);

public sealed record ModDependency(
    string? ProjectId,
    string? VersionId,
    ModSource Source,
    string DependencyType,
    string? FileName);

public sealed record ModVersionFile(
    string Filename,
    string DownloadUrl,
    long Size,
    string? Sha512,
    bool IsPrimary);

public sealed record ModProjectLinks(
    string? WebsiteUrl,
    string? IssuesUrl,
    string? SourceUrl,
    string? WikiUrl,
    string? DiscordUrl);

public sealed record ModMediaItem(
    string Url,
    string? ThumbnailUrl,
    string? Title,
    string? Description);

/// <summary>One selectable category in the mod-browse filter. <see cref="Label"/> is shown to the
/// user; <see cref="Value"/> is what the store's search takes: a Modrinth category slug or a
/// CurseForge numeric category id.</summary>
public sealed record ModBrowseCategory(string Label, string Value);

public sealed record ModProjectDetail(
    string? Description,
    ModProjectLinks Links,
    IReadOnlyList<ModMediaItem> Screenshots,
    // Modrinth ships Markdown and CurseForge ships HTML. The viewer renders by this flag instead of
    // guessing from the content, which misreads Markdown that embeds an HTML tag.
    bool IsMarkdown = false);

public sealed record InstalledMod(
    string FilePath,
    string Filename,
    long Size,
    string Sha512,
    ModSummary? LinkedMod,
    ModVersion? LinkedVersion,
    ModSource Source);

// ── Modrinth service ─────────────────────────────────────────────────────────

public sealed class ModrinthService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ApiClient _api;

    /// <summary>Plain client used only for downloading mod files from upstream CDNs.
    /// File-download URLs are public, so they don't need to be proxied.</summary>
    private readonly HttpClient _downloads = CreateDownloadClient();

    public ModrinthService(ApiClient api) { _api = api; }

    private static HttpClient CreateDownloadClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All
        })
        { Timeout = TimeSpan.FromMinutes(10) };
        ApiClient.ApplyUserAgent(client);
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return client;
    }

    // ── search ────────────────────────────────────────────────────────────────

    public async Task<List<ModSummary>> SearchAsync(
        string query,
        string? mcVersion = null,
        string? loader = null,
        IReadOnlyList<string>? categories = null,
        int limit = 30,
        int offset = 0,
        string projectType = "mod",
        string index = "relevance",
        CancellationToken ct = default)
    {
        var facets = new List<string[]>();
        facets.Add([$"project_type:{projectType}"]);
        if (!string.IsNullOrEmpty(mcVersion))
            facets.Add([$"versions:{mcVersion}"]);
        if (!string.IsNullOrEmpty(loader))
            facets.Add([$"categories:{loader.ToLowerInvariant()}"]);
        // Each category is its own facet group, so results must match all of them (AND), the same
        // way selecting several categories narrows results on the Modrinth site.
        if (categories is not null)
            foreach (var c in categories)
                if (!string.IsNullOrWhiteSpace(c))
                    facets.Add([$"categories:{c}"]);

        var facetsJson = JsonSerializer.Serialize(facets);
        var path = $"search?query={Uri.EscapeDataString(query)}&facets={Uri.EscapeDataString(facetsJson)}&limit={limit}&offset={offset}&index={index}";
        // Throws on failure so a refused search (rate limited, Modrinth down) doesn't read as "No
        // results".
        var resp = await ProxyGetJsonStrictAsync<SearchResponse>(path, "search", ct);
        return resp?.Hits?.Select(ToSummary).ToList() ?? new();
    }

    private List<ModBrowseCategory>? _categoryCache;

    /// <summary>Modrinth's category tags for a project type (mod, resourcepack, ...), cached for
    /// the session. Header categories only, with loaders and resolutions filtered out, so the
    /// filter matches the site's "Categories" list.</summary>
    public async Task<List<ModBrowseCategory>> GetCategoriesAsync(string projectType = "mod", CancellationToken ct = default)
    {
        if (_categoryCache is { } cached) return cached;
        try
        {
            var tags = await ProxyGetJsonAsync<List<CategoryTag>>("tag/category", ct);
            var list = (tags ?? new())
                .Where(t => !string.IsNullOrWhiteSpace(t.Name)
                            && string.Equals(t.ProjectType, projectType, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(t.Header, "categories", StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Select(n => new ModBrowseCategory(Prettify(n), n))
                .ToList();
            _categoryCache = list;
            return list;
        }
        catch { return new(); }
    }

    /// <summary>"worldgen" -> "Worldgen", "game-mechanics" -> "Game mechanics".</summary>
    private static string Prettify(string slug)
    {
        var spaced = slug.Replace('-', ' ').Replace('_', ' ').Trim();
        return spaced.Length == 0 ? slug : char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }

    private sealed class CategoryTag
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("project_type")] public string? ProjectType { get; set; }
        [JsonPropertyName("header")] public string? Header { get; set; }
    }

    // ── project detail ────────────────────────────────────────────────────────

    public async Task<ModSummary?> GetProjectAsync(string idOrSlug, CancellationToken ct = default)
    {
        var resp = await ProxyGetJsonAsync<ProjectResponse>($"project/{idOrSlug}", ct);
        return resp is null ? null : ToSummaryFromProject(resp);
    }

    public async Task<string?> GetDescriptionAsync(string idOrSlug, CancellationToken ct = default)
    {
        var resp = await ProxyGetJsonAsync<JsonElement>($"project/{idOrSlug}", ct);
        return resp.ValueKind != JsonValueKind.Undefined && resp.TryGetProperty("body", out var body)
            ? body.GetString() : null;
    }

    public async Task<ModProjectDetail> GetProjectDetailAsync(string idOrSlug, CancellationToken ct = default)
    {
        var project = await ProxyGetJsonAsync<ProjectResponse>($"project/{idOrSlug}", ct);
        if (project is null) return EmptyDetail();

        return new ModProjectDetail(
            project.Body ?? project.Description,
            new ModProjectLinks(
                $"https://modrinth.com/mod/{project.Slug}",
                project.IssuesUrl,
                project.SourceUrl,
                project.WikiUrl,
                project.DiscordUrl),
            project.Gallery?
                .Where(g => !string.IsNullOrWhiteSpace(g.Url))
                .Select(g => new ModMediaItem(g.Url, g.Url, g.Title, g.Description))
                .ToList()
                ?? new List<ModMediaItem>(),
            IsMarkdown: true); // Modrinth project bodies are Markdown
    }

    // ── versions ─────────────────────────────────────────────────────────────

    public async Task<List<ModVersion>> GetVersionsAsync(
        string projectId,
        string? mcVersion = null,
        string? loader = null,
        CancellationToken ct = default)
    {
        var path = $"project/{projectId}/version";
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(mcVersion)) parts.Add($"game_versions=[\"{mcVersion}\"]");
        if (!string.IsNullOrEmpty(loader))    parts.Add($"loaders=[\"{loader.ToLowerInvariant()}\"]");
        if (parts.Count > 0) path += "?" + string.Join("&", parts);

        var resp = await ProxyGetJsonAsync<List<VersionResponse>>(path, ct);
        return resp?.Select(ToVersion).ToList() ?? new();
    }

    /// <summary>A project's versions, either all of them or only those for a Minecraft version and
    /// loader (filtered by Modrinth, so it's one small page). Changelogs are left out;
    /// <see cref="GetVersionChangelogAsync"/> fetches one on demand. Unlike
    /// <see cref="GetVersionsAsync"/> this throws when the store refuses (a 429, an outage), so a
    /// caller that caches the answer never caches "no versions" for a mod that just couldn't be
    /// checked.</summary>
    /// <param name="pacing"><see cref="ProxyPacing.UpdateCheck"/> for update checks, which are
    /// paced by their own setting.</param>
    public async Task<List<ModVersion>> GetVersionsStrictAsync(string projectId, string? mcVersion = null,
        string? loader = null, CancellationToken ct = default, ProxyPacing pacing = ProxyPacing.Default)
    {
        var query = new List<string> { "include_changelog=false" };
        if (!string.IsNullOrEmpty(mcVersion))
            query.Add("game_versions=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { mcVersion })));
        if (!string.IsNullOrEmpty(loader))
            query.Add("loaders=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader.ToLowerInvariant() })));
        var list = await ProxyGetJsonStrictAsync<List<VersionResponse>>(
            $"project/{projectId}/version?{string.Join("&", query)}", "version list", ct, pacing);
        return list?.Select(ToVersion).ToList() ?? new();
    }

    /// <summary>The newest version of the project each installed file belongs to, for many files in
    /// one request (<c>POST /version_files/update</c>), keyed by the SHA-512 it was asked with. The
    /// project id comes back with each answer so the caller can check it is the mod it meant.</summary>
    /// <remarks>Lets an update check cover hundreds of mods in one round trip. Modrinth filters by
    /// <paramref name="mcVersion"/>, <paramref name="loader"/> and <paramref name="versionTypes"/>
    /// (null = every channel). A hash is absent when no version passes the filters or when Modrinth
    /// doesn't know the file; asking once without a channel filter tells the two apart. Throws when
    /// Modrinth or the launcher server refuses, so "could not ask" never reads as "nothing newer".
    /// The answers include changelogs for the update review.</remarks>
    public async Task<Dictionary<string, (string ProjectId, ModVersion Version)>> GetLatestVersionsByHashAsync(
        IReadOnlyCollection<string> sha512Hashes, string? mcVersion, string? loader,
        IReadOnlyCollection<string>? versionTypes, CancellationToken ct = default)
    {
        var result = new Dictionary<string, (string, ModVersion)>(StringComparer.OrdinalIgnoreCase);
        if (sha512Hashes.Count == 0) return result;

        var body = new UpdateLookupBody
        {
            Hashes = sha512Hashes,
            Loaders = string.IsNullOrWhiteSpace(loader) ? null : [loader.Trim().ToLowerInvariant()],
            GameVersions = string.IsNullOrWhiteSpace(mcVersion) ? null : [mcVersion.Trim()],
            VersionTypes = versionTypes is { Count: > 0 } ? versionTypes : null
        };
        using var resp = await _api.ProxyAsync("modrinth", HttpMethod.Post, "version_files/update",
            JsonContent.Create(body, options: Json), ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Modrinth update lookup failed: {await DescribeFailureAsync(resp, ct)}", null, resp.StatusCode);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var ver = prop.Value.Deserialize<VersionResponse>(Json);
            if (ver is null || string.IsNullOrEmpty(ver.Id) || string.IsNullOrEmpty(ver.ProjectId)) continue;
            result[prop.Name] = (ver.ProjectId, ToVersion(ver));
        }
        return result;
    }

    private sealed class UpdateLookupBody
    {
        [JsonPropertyName("hashes")] public required IReadOnlyCollection<string> Hashes { get; init; }
        [JsonPropertyName("algorithm")] public string Algorithm { get; init; } = "sha512";

        [JsonPropertyName("loaders"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string[]? Loaders { get; init; }

        [JsonPropertyName("game_versions"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string[]? GameVersions { get; init; }

        [JsonPropertyName("version_types"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyCollection<string>? VersionTypes { get; init; }
    }

    /// <summary>One version's changelog (Markdown), for the update review.</summary>
    public async Task<string?> GetVersionChangelogAsync(string versionId, CancellationToken ct = default)
    {
        var version = await ProxyGetJsonStrictAsync<VersionResponse>($"version/{versionId}", "changelog", ct);
        return version?.Changelog;
    }

    private async Task<T?> ProxyGetJsonStrictAsync<T>(string pathAndQuery, string operation, CancellationToken ct,
        ProxyPacing pacing = ProxyPacing.Default)
    {
        using var resp = await _api.ProxyAsync("modrinth", HttpMethod.Get, pathAndQuery, null, ct, pacing);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Modrinth {operation} failed: {await DescribeFailureAsync(resp, ct)}", null, resp.StatusCode);
        return await resp.Content.ReadFromJsonAsync<T>(Json, ct);
    }

    /// <summary>A readable reason for a failed Modrinth call. The launcher server explains its own
    /// refusals (rate limiting) as <c>{"error": sentence, "code": ...}</c>; Modrinth itself answers
    /// <c>{"error": code, "description": sentence}</c>.</summary>
    private static async Task<string> DescribeFailureAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var body = (await resp.Content.ReadAsStringAsync(ct)).TrimStart();
            if (body.StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("code", out _) && root.TryGetProperty("error", out var serverError)
                    && serverError.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(serverError.GetString()))
                    return serverError.GetString()!;
                if (root.TryGetProperty("description", out var description)
                    && description.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(description.GetString()))
                    return description.GetString()!;
            }
        }
        catch { /* fall through to the status */ }

        return resp.StatusCode switch
        {
            HttpStatusCode.TooManyRequests =>
                "Modrinth is rate-limiting the launcher server (too many requests). Wait a minute and try again.",
            HttpStatusCode.NotFound => "the project no longer exists on Modrinth.",
            _ => $"{(int)resp.StatusCode} {resp.ReasonPhrase}"
        };
    }

    /// <summary>Several projects in one round trip (<c>GET /projects?ids=[...]</c>), keyed by id. Unknown
    /// ids are absent. Used wherever a whole pack is identified at once.</summary>
    public async Task<Dictionary<string, ModSummary>> GetProjectsAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var result = new Dictionary<string, ModSummary>(StringComparer.OrdinalIgnoreCase);
        var distinct = ids.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var chunk in distinct.Chunk(50))
        {
            var idsJson = JsonSerializer.Serialize(chunk);
            var list = await ProxyGetJsonAsync<List<ProjectResponse>>($"projects?ids={Uri.EscapeDataString(idsJson)}", ct);
            foreach (var p in list ?? new())
            {
                var summary = ToSummaryFromProject(p);
                result[p.Id] = summary;
                if (!string.IsNullOrEmpty(p.Slug)) result[p.Slug] = summary;
            }
        }
        return result;
    }

    public async Task<(ModSummary Mod, ModVersion Version)?> GetVersionWithProjectAsync(string versionId, CancellationToken ct = default)
    {
        var version = await ProxyGetJsonAsync<VersionResponse>($"version/{versionId}", ct);
        if (version is null) return null;

        var project = await GetProjectAsync(version.ProjectId, ct);
        return project is null ? null : (project, ToVersion(version));
    }

    // ── fingerprint matching ──────────────────────────────────────────────────

    public async Task<Dictionary<string, (ModSummary mod, ModVersion version)>> MatchHashesAsync(
        IEnumerable<string> sha512Hashes, CancellationToken ct = default)
    {
        var hashes = sha512Hashes.Select(h => h.ToLowerInvariant()).Distinct().ToList();
        if (hashes.Count == 0) return new();

        var body = new { hashes, algorithm = "sha512" };
        var content = JsonContent.Create(body, options: Json);
        using var resp = await _api.ProxyAsync("modrinth", HttpMethod.Post, "version_files", content, ct);
        // Throw rather than return "no matches" (see the note on CurseForge's fingerprint match): a
        // failed lookup must not look like an unmatched hash, or installed mods show up as not
        // installed.
        resp.EnsureSuccessStatusCode();

        var result = new Dictionary<string, (ModSummary, ModVersion)>();
        var matched = new List<(string Hash, VersionResponse Version)>();
        using (var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct))
        {
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var ver = JsonSerializer.Deserialize<VersionResponse>(prop.Value.GetRawText(), Json);
                if (ver is not null) matched.Add((prop.Name, ver));
            }
        }

        // One batched project lookup for every match; anything the batch missed is fetched singly so
        // a partial answer never loses a match.
        Dictionary<string, ModSummary> projects;
        try { projects = await GetProjectsAsync(matched.Select(m => m.Version.ProjectId), ct); }
        catch { projects = new(StringComparer.OrdinalIgnoreCase); }

        foreach (var (hash, ver) in matched)
        {
            if (!projects.TryGetValue(ver.ProjectId, out var project))
            {
                project = await GetProjectAsync(ver.ProjectId, ct);
                if (project is null) continue;
            }
            result[hash] = (project, ToVersion(ver));
        }
        return result;
    }

    // ── cross-store counterpart ─────────────────────────────────────────────────

    /// <summary>Finds the Modrinth project for a mod known on CurseForge, to recognise it as
    /// installed when the two stores' jars differ and hash matching misses it. Tries the slug first
    /// (Modrinth resolves slugs on the project endpoint), then a name search, accepting only a
    /// candidate whose slug or name lines up (see <see cref="ModMatching"/>).</summary>
    public async Task<ModSummary?> FindCounterpartAsync(string? slug, string? name, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(slug))
        {
            var bySlug = await GetProjectAsync(slug, ct);
            if (bySlug is not null && ModMatching.IsLikelySameMod(bySlug, slug, name))
                return bySlug;
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            var hits = await SearchAsync(name, limit: 8, ct: ct);
            var match = hits.FirstOrDefault(h => ModMatching.IsLikelySameMod(h, slug, name));
            if (match is not null) return match;
        }

        return null;
    }

    // ── download ──────────────────────────────────────────────────────────────

    /// <summary>How many times a file may be restarted because a pause dropped its connection. A
    /// long pause can cost more than one attempt, and giving up early would leave a file missing
    /// from a pack the import reports as complete.</summary>
    private const int MaxPauseRetries = 4;

    /// <param name="pause">Held mid-file while the user pauses the transfer this download belongs
    /// to. A modpack is one big archive, so pausing only between files would do little.</param>
    public async Task DownloadFileAsync(
        string url, string destPath, IProgress<(long done, long total)>? progress = null,
        CancellationToken ct = default, PauseGate? pause = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        // Download to a temp file, then atomically rename over destPath, so an interrupted download
        // never leaves a truncated jar that the game fails to load and "skip if exists" treats as
        // complete. The byte count is also checked against Content-Length.
        var part = destPath + ".part";
        for (var attempt = 0; ; attempt++)
        {
            // A pause holds the response stream open without reading, which a server or proxy may
            // drop. If that's what broke the transfer, restart the file instead of reporting a
            // failed download.
            var pausedThisAttempt = false;
            try
            {
                using var resp = await _downloads.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? -1;
                await using (var src = await resp.Content.ReadAsStreamAsync(ct))
                await using (var dst = File.Create(part))
                {
                    var buf = new byte[81920];
                    long done = 0;
                    int read;
                    while ((read = await src.ReadAsync(buf, ct)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, read), ct);
                        done += read;
                        if (total > 0) progress?.Report((done, total));
                        if (pause is { IsPaused: true })
                        {
                            pausedThisAttempt = true;
                            await pause.WaitAsync(ct);
                        }
                    }
                    if (total > 0 && done != total)
                        throw new IOException($"Incomplete download: received {done} of {total} bytes from {url}");
                }
                File.Move(part, destPath, overwrite: true);
                return;
            }
            // Gated on whether this attempt was paused, not on the attempt number: a long pause can
            // drop the connection more than once.
            catch (Exception ex) when (pausedThisAttempt && attempt < MaxPauseRetries && ex is not OperationCanceledException)
            {
                try { if (File.Exists(part)) File.Delete(part); } catch { /* best effort */ }
                // Retry from the start.
            }
            catch
            {
                try { if (File.Exists(part)) File.Delete(part); } catch { /* best effort */ }
                throw;
            }
        }
    }

    // ── fingerprint helper (static) ───────────────────────────────────────────

    public static string Sha512OfFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA512.HashData(fs)).ToLowerInvariant();
    }

    // ── proxy helpers ─────────────────────────────────────────────────────────

    private async Task<T?> ProxyGetJsonAsync<T>(string pathAndQuery, CancellationToken ct)
    {
        using var resp = await _api.ProxyAsync("modrinth", HttpMethod.Get, pathAndQuery, null, ct);
        if (!resp.IsSuccessStatusCode) return default;
        return await resp.Content.ReadFromJsonAsync<T>(Json, ct);
    }

    // ── private mapping ───────────────────────────────────────────────────────

    private static ModSummary ToSummary(SearchHit h) => new(
        h.ProjectId, h.Slug, h.Title, h.Author, h.Description, h.Downloads, h.IconUrl,
        ModSource.Modrinth, h.Categories ?? Array.Empty<string>());

    private static ModSummary ToSummaryFromProject(ProjectResponse p) => new(
        p.Id, p.Slug, p.Title, null, p.Description, p.Downloads, p.IconUrl,
        ModSource.Modrinth, p.Categories ?? Array.Empty<string>());

    private static ModVersion ToVersion(VersionResponse v) => new(
        v.Id, v.Name, v.VersionNumber,
        v.GameVersions ?? Array.Empty<string>(),
        v.Loaders ?? Array.Empty<string>(),
        v.VersionType ?? "release",
        v.DatePublished,
        v.Downloads,
        v.Changelog,
        ModSource.Modrinth,
        v.Files?.Select(f => new ModVersionFile(f.Filename, f.Url, f.Size, f.Hashes?.Sha512, f.Primary)).ToList()
            ?? new List<ModVersionFile>(),
        v.Dependencies?
            .Where(d => !string.IsNullOrWhiteSpace(d.ProjectId) || !string.IsNullOrWhiteSpace(d.VersionId))
            .Select(d => new ModDependency(d.ProjectId, d.VersionId, ModSource.Modrinth, d.DependencyType ?? "", d.FileName))
            .ToList()
            ?? new List<ModDependency>());

    private static ModProjectDetail EmptyDetail() => new(
        null,
        new ModProjectLinks(null, null, null, null, null),
        Array.Empty<ModMediaItem>());

    // ── JSON DTOs ─────────────────────────────────────────────────────────────

    private sealed class SearchResponse
    {
        [JsonPropertyName("hits")] public List<SearchHit>? Hits { get; set; }
    }
    private sealed class SearchHit
    {
        [JsonPropertyName("project_id")] public string ProjectId { get; set; } = "";
        [JsonPropertyName("slug")] public string Slug { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("author")] public string? Author { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("downloads")] public long Downloads { get; set; }
        [JsonPropertyName("icon_url")] public string? IconUrl { get; set; }
        [JsonPropertyName("categories")] public string[]? Categories { get; set; }
    }
    private sealed class ProjectResponse
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("slug")] public string Slug { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("downloads")] public long Downloads { get; set; }
        [JsonPropertyName("icon_url")] public string? IconUrl { get; set; }
        [JsonPropertyName("categories")] public string[]? Categories { get; set; }
        [JsonPropertyName("issues_url")] public string? IssuesUrl { get; set; }
        [JsonPropertyName("source_url")] public string? SourceUrl { get; set; }
        [JsonPropertyName("wiki_url")] public string? WikiUrl { get; set; }
        [JsonPropertyName("discord_url")] public string? DiscordUrl { get; set; }
        [JsonPropertyName("gallery")] public List<ProjectGalleryItem>? Gallery { get; set; }
    }
    private sealed class VersionResponse
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("version_number")] public string VersionNumber { get; set; } = "";
        [JsonPropertyName("game_versions")] public string[]? GameVersions { get; set; }
        [JsonPropertyName("loaders")] public string[]? Loaders { get; set; }
        [JsonPropertyName("version_type")] public string? VersionType { get; set; }
        [JsonPropertyName("date_published")] public DateTimeOffset DatePublished { get; set; }
        [JsonPropertyName("downloads")] public long Downloads { get; set; }
        [JsonPropertyName("changelog")] public string? Changelog { get; set; }
        [JsonPropertyName("project_id")] public string ProjectId { get; set; } = "";
        [JsonPropertyName("files")] public List<VersionFile>? Files { get; set; }
        [JsonPropertyName("dependencies")] public List<VersionDependency>? Dependencies { get; set; }
    }
    private sealed class VersionFile
    {
        [JsonPropertyName("filename")] public string Filename { get; set; } = "";
        [JsonPropertyName("url")] public string Url { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("hashes")] public FileHashes? Hashes { get; set; }
        [JsonPropertyName("primary")] public bool Primary { get; set; }
    }
    private sealed class FileHashes
    {
        [JsonPropertyName("sha512")] public string? Sha512 { get; set; }
    }
    private sealed class VersionDependency
    {
        [JsonPropertyName("project_id")] public string? ProjectId { get; set; }
        [JsonPropertyName("version_id")] public string? VersionId { get; set; }
        [JsonPropertyName("dependency_type")] public string? DependencyType { get; set; }
        [JsonPropertyName("file_name")] public string? FileName { get; set; }
    }
    private sealed class ProjectGalleryItem
    {
        [JsonPropertyName("url")] public string Url { get; set; } = "";
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
    }
}
