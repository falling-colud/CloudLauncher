using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

public sealed record ImportProgress(
    double TotalFraction,
    string TotalLabel,
    double CurrentFraction = -1,
    string CurrentLabel = "");

public sealed class ModpackImportService(
    ModrinthService modrinth,
    CurseForgeService curseforge,
    ApiClient api,
    PackFolderService packs,
    ModFingerprintCache fingerprints)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static readonly JsonSerializerOptions CfManifestJson = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private PackAssetService? _assets;

    public void SetPackAssets(PackAssetService assets) => _assets = assets;

    /// <summary>Resolves <paramref name="relative"/> under <paramref name="root"/>, returning
    /// the absolute path only when it stays inside the root. Imported modpacks are untrusted
    /// archives (downloaded from CurseForge/Modrinth or local files), so a crafted entry such
    /// as <c>..\..\Startup\evil.bat</c> or an absolute path would otherwise escape the pack
    /// folder — a zip-slip arbitrary-file-write. Returns null when the entry escapes so the
    /// caller can skip that one entry rather than aborting the whole import.</summary>
    private static string? SafeResolveUnderRoot(string root, string relative)
    {
        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(rootFull, relative));
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(full, rootFull, StringComparison.OrdinalIgnoreCase)
            ? full : null;
    }

    // ── Modrinth .mrpack ──────────────────────────────────────────────────────

    public Task<PackSummary> ImportMrpackAsync(
        string mrpackPath,
        string packName,
        IProgress<string>? log,
        IProgress<ImportProgress>? progress = null,
        CancellationToken ct = default) =>
        ImportMrpackCoreAsync(mrpackPath, packName, null, log, progress, null, ct);

    public Task<PackSummary> ImportMrpackIntoPackAsync(
        string mrpackPath,
        Guid packId,
        IProgress<string>? log,
        IProgress<ImportProgress>? progress = null,
        PackImportMetadata? metadata = null,
        CancellationToken ct = default) =>
        ImportMrpackCoreAsync(mrpackPath, null, packId, log, progress, metadata, ct);

    private async Task<PackSummary> ImportMrpackCoreAsync(
        string mrpackPath,
        string? packName,
        Guid? existingPackId,
        IProgress<string>? log,
        IProgress<ImportProgress>? progress,
        PackImportMetadata? metadata,
        CancellationToken ct)
    {
        log?.Report($"Opening {Path.GetFileName(mrpackPath)}...");
        progress?.Report(new ImportProgress(-1, $"Opening {Path.GetFileName(mrpackPath)}..."));
        using var zip = ZipFile.OpenRead(mrpackPath);

        var indexEntry = zip.GetEntry("modrinth.index.json")
            ?? throw new InvalidOperationException("Not a valid .mrpack — missing modrinth.index.json");

        MrIndex index;
        await using (var stream = indexEntry.Open())
            index = await JsonSerializer.DeserializeAsync<MrIndex>(stream, cancellationToken: ct)
                    ?? throw new InvalidOperationException("Failed to parse modrinth.index.json");

        var mcVersion = index.Dependencies?.TryGetValue("minecraft", out var mc) == true ? mc : null;
        var loaderKind = LoaderKind.None;
        string? loaderVersion = null;
        if (index.Dependencies?.TryGetValue("fabric-loader", out var fl) == true)
        { loaderKind = LoaderKind.Fabric; loaderVersion = fl; }
        else if (index.Dependencies?.TryGetValue("neoforge", out var nf) == true)
        { loaderKind = LoaderKind.NeoForge; loaderVersion = nf; }
        else if (index.Dependencies?.TryGetValue("forge", out var fg) == true)
        { loaderKind = LoaderKind.Forge; loaderVersion = fg; }

        log?.Report($"MC {mcVersion} · {loaderKind} {loaderVersion} · {index.Files?.Count ?? 0} mods");

        PackSummary pack;
        var importFields = MetadataToFields(metadata);
        if (existingPackId is Guid targetId)
        {
            pack = await api.UpdatePackAsync(targetId, new UpdatePackRequest(
                null,
                importFields.DescriptionExcerpt,
                null,
                null,
                false,
                mcVersion,
                loaderKind,
                loaderVersion,
                null,
                importFields.Summary));
            progress?.Report(new ImportProgress(-1, $"Installing into {pack.Name}"));
        }
        else
        {
            pack = await api.CreatePackAsync(new CreatePackRequest(
                packName!, importFields.DescriptionExcerpt ?? $"Imported from {Path.GetFileName(mrpackPath)}",
                false, mcVersion, loaderKind, loaderVersion, importFields.Summary), ct);
            progress?.Report(new ImportProgress(-1, $"Created instance {pack.Name}"));
        }

        packs.EnsurePackFolder(pack.Id, pack.Name, pack.IsShared);

        var gameDir = packs.GameDir(pack.Id);
        var modsDir = Path.Combine(gameDir, "mods");
        Directory.CreateDirectory(modsDir);

        // Download required files
        var files = index.Files ?? new();
        for (int i = 0; i < files.Count; i++)
        {
            var f = files[i];
            var fileIndex = i;
            var fileCount = files.Count;
            ct.ThrowIfCancellationRequested();
            var dest = SafeResolveUnderRoot(gameDir, f.Path.Replace('/', Path.DirectorySeparatorChar));
            if (dest is null) { log?.Report($"  WARNING: skipping file outside pack folder: {f.Path}"); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            var fileName = Path.GetFileName(f.Path);
            var label = $"Downloading {fileIndex + 1}/{fileCount}: {fileName}";
            if (File.Exists(dest))
            {
                log?.Report($"  Skip (exists): {f.Path}");
                progress?.Report(new ImportProgress(
                    fileCount == 0 ? 1 : (double)(fileIndex + 1) / fileCount,
                    $"Skipped {fileIndex + 1}/{fileCount}: {fileName}",
                    1,
                    fileName));
                continue;
            }

            log?.Report($"  [{fileIndex + 1}/{fileCount}] {Path.GetFileName(f.Path)}");
            progress?.Report(new ImportProgress(
                fileCount == 0 ? 1 : (double)fileIndex / fileCount,
                label,
                -1,
                fileName));
            var downloaded = false;
            foreach (var url in f.Downloads ?? Enumerable.Empty<string>())
            {
                try
                {
                    var fileProgress = new Progress<(long done, long total)>(p =>
                    {
                        if (p.total <= 0) return;
                        var current = Math.Min(1.0, p.done / (double)p.total);
                        progress?.Report(new ImportProgress(
                            fileCount == 0 ? 1 : (fileIndex + current) / fileCount,
                            label,
                            current,
                            fileName));
                    });
                    await modrinth.DownloadFileAsync(url, dest, fileProgress, ct);
                    progress?.Report(new ImportProgress(
                        fileCount == 0 ? 1 : (double)(fileIndex + 1) / fileCount,
                        label,
                        1,
                        fileName));
                    downloaded = true;
                    break;
                }
                catch { /* try next URL */ }
            }
            if (!downloaded) log?.Report($"  WARNING: could not download {f.Path}");
        }

        // Copy overrides/ folder
        var overridesEntry = zip.Entries
            .Where(e => e.FullName.StartsWith("overrides/") && !e.FullName.EndsWith("/"))
            .ToList();
        if (overridesEntry.Count > 0)
        {
            log?.Report($"Copying {overridesEntry.Count} override files...");
            progress?.Report(new ImportProgress(1, $"Copying {overridesEntry.Count} override files...", -1, "Overrides"));
        }
        foreach (var entry in overridesEntry)
        {
            var rel = entry.FullName["overrides/".Length..].Replace('/', Path.DirectorySeparatorChar);
            var dest = SafeResolveUnderRoot(gameDir, rel);
            if (dest is null) { log?.Report($"  WARNING: skipping override outside pack folder: {entry.FullName}"); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
        }

        log?.Report($"Import complete: {pack.Name}");
        progress?.Report(new ImportProgress(1, $"Import complete: {pack.Name}"));
        await SaveImportAssetsAsync(pack.Id, metadata, ct);
        return pack;
    }

    // ── CurseForge .zip ───────────────────────────────────────────────────────

    public Task<PackSummary> ImportCurseForgeZipAsync(
        string zipPath,
        string packName,
        IProgress<string>? log,
        IProgress<ImportProgress>? progress = null,
        CancellationToken ct = default) =>
        ImportCurseForgeZipCoreAsync(zipPath, packName, null, log, progress, null, ct);

    public Task<PackSummary> ImportCurseForgeZipIntoPackAsync(
        string zipPath,
        Guid packId,
        IProgress<string>? log,
        IProgress<ImportProgress>? progress = null,
        PackImportMetadata? metadata = null,
        CancellationToken ct = default) =>
        ImportCurseForgeZipCoreAsync(zipPath, null, packId, log, progress, metadata, ct);

    private async Task<PackSummary> ImportCurseForgeZipCoreAsync(
        string zipPath,
        string? packName,
        Guid? existingPackId,
        IProgress<string>? log,
        IProgress<ImportProgress>? progress,
        PackImportMetadata? metadata,
        CancellationToken ct)
    {
        log?.Report($"Opening {Path.GetFileName(zipPath)}...");
        progress?.Report(new ImportProgress(-1, $"Opening {Path.GetFileName(zipPath)}..."));
        using var zip = ZipFile.OpenRead(zipPath);

        var manifestEntry = zip.GetEntry("manifest.json")
            ?? throw new InvalidOperationException("Not a valid CurseForge pack — missing manifest.json");

        CfManifest manifest;
        await using (var stream = manifestEntry.Open())
            manifest = await JsonSerializer.DeserializeAsync<CfManifest>(stream, CfManifestJson, ct)
                       ?? throw new InvalidOperationException("Failed to parse manifest.json");

        var mcVersion = manifest.Minecraft?.Version;
        var loaderKind = LoaderKind.None;
        string? loaderVersion = null;
        var loaderInfo = manifest.Minecraft?.ModLoaders?.FirstOrDefault(l => l.Primary);
        if (loaderInfo?.Id is not null)
        {
            var lid = loaderInfo.Id;
            if (lid.StartsWith("neoforge-")) { loaderKind = LoaderKind.NeoForge; loaderVersion = lid["neoforge-".Length..]; }
            else if (lid.StartsWith("fabric-")) { loaderKind = LoaderKind.Fabric; loaderVersion = lid["fabric-".Length..]; }
            else if (lid.StartsWith("forge-")) { loaderKind = LoaderKind.Forge; loaderVersion = lid["forge-".Length..]; }
        }

        log?.Report($"MC {mcVersion} · {loaderKind} · {manifest.Files?.Count ?? 0} mods");

        PackSummary pack;
        var importFields = MetadataToFields(metadata);
        if (existingPackId is Guid targetId)
        {
            pack = await api.UpdatePackAsync(targetId, new UpdatePackRequest(
                null,
                importFields.DescriptionExcerpt,
                null,
                null,
                false,
                mcVersion,
                loaderKind,
                loaderVersion,
                null,
                importFields.Summary));
            progress?.Report(new ImportProgress(-1, $"Installing into {pack.Name}"));
        }
        else
        {
            pack = await api.CreatePackAsync(new CreatePackRequest(
                packName!, importFields.DescriptionExcerpt ?? $"Imported from {Path.GetFileName(zipPath)}",
                false, mcVersion, loaderKind, loaderVersion, importFields.Summary), ct);
            progress?.Report(new ImportProgress(-1, $"Created instance {pack.Name}"));
        }

        packs.EnsurePackFolder(pack.Id, pack.Name, pack.IsShared);

        var gameDir = packs.GameDir(pack.Id);
        var modsDir = Path.Combine(gameDir, "mods");
        Directory.CreateDirectory(modsDir);

        // Resolve and download mods via CurseForge API
        var files = manifest.Files ?? new();
        for (int i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var f = files[i];
            var fileIndex = i;
            var fileCount = files.Count;
            log?.Report($"  [{fileIndex + 1}/{fileCount}] Resolving mod {f.ProjectId}:{f.FileId}...");
            progress?.Report(new ImportProgress(
                fileCount == 0 ? 1 : (double)fileIndex / fileCount,
                $"Resolving {fileIndex + 1}/{fileCount}: {f.ProjectId}:{f.FileId}",
                -1,
                "Resolving download"));
            try
            {
                var url = await curseforge.GetDownloadUrlAsync(f.ProjectId, f.FileId, ct);
                if (url is null) { log?.Report($"  WARNING: no download URL for {f.ProjectId}:{f.FileId}"); continue; }
                var filename = Path.GetFileName(new Uri(url).LocalPath);
                var dest = Path.Combine(modsDir, filename);
                log?.Report($"  Downloading {filename}...");
                var label = $"Downloading {fileIndex + 1}/{fileCount}: {filename}";
                progress?.Report(new ImportProgress(
                    fileCount == 0 ? 1 : (double)fileIndex / fileCount,
                    label,
                    -1,
                    filename));
                var fileProgress = new Progress<(long done, long total)>(p =>
                {
                    if (p.total <= 0) return;
                    var current = Math.Min(1.0, p.done / (double)p.total);
                    progress?.Report(new ImportProgress(
                        fileCount == 0 ? 1 : (fileIndex + current) / fileCount,
                        label,
                        current,
                        filename));
                });
                await modrinth.DownloadFileAsync(url, dest, fileProgress, ct);
                await RememberCurseForgeMatchAsync(dest, f.ProjectId, f.FileId, ct);
                progress?.Report(new ImportProgress(
                    fileCount == 0 ? 1 : (double)(fileIndex + 1) / fileCount,
                    label,
                    1,
                    filename));
            }
            catch (Exception ex) { log?.Report($"  WARNING: {ex.Message}"); }
        }

        // Copy overrides
        var prefix = (manifest.Overrides ?? "overrides") + "/";
        var overrides = zip.Entries.Where(e => e.FullName.StartsWith(prefix) && !e.FullName.EndsWith("/")).ToList();
        if (overrides.Count > 0)
        {
            log?.Report($"Copying {overrides.Count} override files...");
            progress?.Report(new ImportProgress(1, $"Copying {overrides.Count} override files...", -1, "Overrides"));
        }
        foreach (var entry in overrides)
        {
            var rel = entry.FullName[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
            var dest = SafeResolveUnderRoot(gameDir, rel);
            if (dest is null) { log?.Report($"  WARNING: skipping override outside pack folder: {entry.FullName}"); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
        }

        log?.Report($"Import complete: {pack.Name}");
        progress?.Report(new ImportProgress(1, $"Import complete: {pack.Name}"));
        await SaveImportAssetsAsync(pack.Id, metadata, ct);
        return pack;
    }

    private async Task RememberCurseForgeMatchAsync(string path, int projectId, int fileId, CancellationToken ct)
    {
        try
        {
            var mod = await curseforge.GetModAsync(projectId, ct);
            var version = await curseforge.GetVersionAsync(projectId, fileId, ct);
            if (mod is null || version is null) return;

            var (sha512, curseForgeFingerprint) = ModFingerprintCache.ComputeHashes(path);
            fingerprints.Store(path, sha512, curseForgeFingerprint, new CachedModMatch
            {
                Mod = mod,
                Version = version
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Imported jars can still be identified by the normal scanner later.
        }
    }

    private static PackImportFields MetadataToFields(PackImportMetadata? metadata)
    {
        if (metadata is null) return new PackImportFields(null, null, null, false);

        if (!string.IsNullOrWhiteSpace(metadata.DescriptionHtml))
            return PackText.FieldsFromExternalProject(
                metadata.Summary,
                metadata.DescriptionHtml,
                metadata.DescriptionIsMarkdown);

        if (!string.IsNullOrWhiteSpace(metadata.DescriptionExcerpt))
        {
            return new PackImportFields(
                PackText.Truncate(metadata.Summary, PackText.SummaryMaxLength),
                PackText.Truncate(metadata.DescriptionExcerpt, PackText.DescriptionMaxLength),
                null,
                metadata.DescriptionIsMarkdown);
        }

        return PackText.FieldsFromExternalProject(metadata.Summary, null);
    }

    private async Task SaveImportAssetsAsync(Guid packId, PackImportMetadata? metadata, CancellationToken ct)
    {
        if (_assets is null || metadata is null) return;

        var fields = MetadataToFields(metadata);
        if (fields.DescriptionIsMarkdown && !string.IsNullOrWhiteSpace(fields.DescriptionSource))
            _assets.SaveDescriptionMarkdown(packId, fields.DescriptionSource);
        else if (!string.IsNullOrWhiteSpace(fields.DescriptionHtml))
            _assets.SaveDescriptionHtml(packId, fields.DescriptionHtml);

        try
        {
            await _assets.SaveIconFromUrlAsync(packId, metadata.IconUrl, ct);
        }
        catch
        {
            /* icon is optional */
        }
    }

    // ── JSON DTOs ─────────────────────────────────────────────────────────────

    private sealed class MrIndex
    {
        [JsonPropertyName("files")] public List<MrFile>? Files { get; set; }
        [JsonPropertyName("dependencies")] public Dictionary<string, string>? Dependencies { get; set; }
    }
    private sealed class MrFile
    {
        [JsonPropertyName("path")] public string Path { get; set; } = "";
        [JsonPropertyName("downloads")] public List<string>? Downloads { get; set; }
        [JsonPropertyName("fileSize")] public long FileSize { get; set; }
    }
    private sealed class CfManifest
    {
        [JsonPropertyName("minecraft")] public CfMinecraft? Minecraft { get; set; }
        [JsonPropertyName("files")] public List<CfFileRef>? Files { get; set; }
        [JsonPropertyName("overrides")] public string? Overrides { get; set; }
    }
    private sealed class CfMinecraft
    {
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("modLoaders")] public List<CfLoader>? ModLoaders { get; set; }
    }
    private sealed class CfLoader
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("primary")] public bool Primary { get; set; }
    }
    private sealed class CfFileRef
    {
        [JsonPropertyName("projectID")] public int ProjectId { get; set; }
        [JsonPropertyName("fileID")] public int FileId { get; set; }
    }
}
