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

public sealed record ModProjectDetail(
    string? Description,
    ModProjectLinks Links,
    IReadOnlyList<ModMediaItem> Screenshots);

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
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; CloudLauncher/1.0)");
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return client;
    }

    // ── search ────────────────────────────────────────────────────────────────

    public async Task<List<ModSummary>> SearchAsync(
        string query,
        string? mcVersion = null,
        string? loader = null,
        string? category = null,
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
        if (!string.IsNullOrEmpty(category))
            facets.Add([$"categories:{category}"]);

        var facetsJson = JsonSerializer.Serialize(facets);
        var path = $"search?query={Uri.EscapeDataString(query)}&facets={Uri.EscapeDataString(facetsJson)}&limit={limit}&offset={offset}&index={index}";
        var resp = await ProxyGetJsonAsync<SearchResponse>(path, ct);
        return resp?.Hits?.Select(ToSummary).ToList() ?? new();
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
                ?? new List<ModMediaItem>());
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
        if (!resp.IsSuccessStatusCode) return new();

        var result = new Dictionary<string, (ModSummary, ModVersion)>();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var ver = JsonSerializer.Deserialize<VersionResponse>(prop.Value.GetRawText(), Json);
            if (ver is null) continue;
            var project = await GetProjectAsync(ver.ProjectId, ct);
            if (project is null) continue;
            result[prop.Name] = (project, ToVersion(ver));
        }
        return result;
    }

    // ── cross-store counterpart ─────────────────────────────────────────────────

    /// <summary>Finds the Modrinth project that corresponds to a mod known on
    /// CurseForge, for recognising it as installed when the two stores' jars aren't
    /// byte-identical (so hash matching misses it). Matches by slug first — Modrinth
    /// resolves slugs on the project endpoint — then by a name search, accepting only
    /// a candidate whose slug or name lines up (see <see cref="ModMatching"/>).</summary>
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

    public async Task DownloadFileAsync(
        string url, string destPath, IProgress<(long done, long total)>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        // Download to a temp file, then atomically rename over destPath. Writing straight to
        // destPath (as before) meant an interrupted download left a truncated jar at the final
        // path — which the game then fails to load, and which "skip if exists" logic treats as a
        // complete file forever. Also verify the byte count against Content-Length.
        var part = destPath + ".part";
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
                }
                if (total > 0 && done != total)
                    throw new IOException($"Incomplete download: received {done} of {total} bytes from {url}");
            }
            File.Move(part, destPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { /* best effort */ }
            throw;
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
