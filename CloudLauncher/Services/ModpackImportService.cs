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
    private static readonly HttpClient Http = ApiClient.WithUserAgent(new() { Timeout = TimeSpan.FromMinutes(10) });
    private static readonly JsonSerializerOptions CfManifestJson = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private PackAssetService? _assets;
    private ModMetadataService? _metadata;

    /// <summary>
    /// How many files an import downloads at once: the user's download concurrency (Settings ->
    /// Downloads), which also governs "Update all" and the mod browser.
    /// </summary>
    /// <remarks>Most files are small CDN round trips, so one at a time is mostly waiting. With several in
    /// flight the progress bar reports the batch instead of one file.</remarks>
    private int DownloadConcurrency => _settings?.EffectiveModDownloadConcurrency ?? 3;

    private AppSettings? _settings;

    public void SetSettings(AppSettings settings) => _settings = settings;

    public void SetPackAssets(PackAssetService assets) => _assets = assets;
    public void SetModMetadata(ModMetadataService metadata) => _metadata = metadata;

    /// <summary>The file written next to a CurseForge import's mods when some could not be
    /// downloaded, listing each with its project page.</summary>
    public const string ManualDownloadsFileName = "manual-downloads.txt";

    /// <summary>A warning for the import log, or for the launcher log when nobody is reading the
    /// import log (a modpack installed from the browse page runs in the background).</summary>
    private static void Warn(IProgress<string>? log, string text)
    {
        if (log is not null) log.Report("  WARNING: " + text);
        else AppLog.Log("import", text);
    }

    /// <summary>Records which store a modpack came from as the pack-wide default, so a mod that is
    /// listed on both stores keeps that store's identity (label, page link, update checks) instead of
    /// flipping to Modrinth. Only fills a blank: a preference the user already set is kept.</summary>
    private void RecordPackSource(Guid packId, ModSource source, IProgress<string>? log)
    {
        if (_metadata is null) return;
        try
        {
            var advanced = _metadata.Advanced(packId);
            if (advanced.PreferredSource is not null) return;
            advanced.PreferredSource = source;
            _metadata.SaveAdvanced(packId);
            log?.Report($"Mods in this pack follow {source} by default (Modpack Management > Advanced).");
        }
        catch { /* metadata is a convenience; the import itself is what matters */ }
    }

    // ── Modrinth .mrpack ──────────────────────────────────────────────────────

    public Task<PackSummary> ImportMrpackAsync(
        string mrpackPath,
        string packName,
        IProgress<string>? log,
        IProgress<ImportProgress>? progress = null,
        CancellationToken ct = default,
        PackJob? job = null) =>
        ImportMrpackCoreAsync(mrpackPath, packName, null, log, progress, null, ct, job);

    public Task<PackSummary> ImportMrpackIntoPackAsync(
        string mrpackPath,
        Guid packId,
        IProgress<string>? log,
        IProgress<ImportProgress>? progress = null,
        PackImportMetadata? metadata = null,
        CancellationToken ct = default,
        PackJob? job = null) =>
        ImportMrpackCoreAsync(mrpackPath, null, packId, log, progress, metadata, ct, job);

    private async Task<PackSummary> ImportMrpackCoreAsync(
        string mrpackPath,
        string? packName,
        Guid? existingPackId,
        IProgress<string>? log,
        IProgress<ImportProgress>? progress,
        PackImportMetadata? metadata,
        CancellationToken ct,
        PackJob? job)
    {
        log?.Report($"Opening {Path.GetFileName(mrpackPath)}...");
        progress?.Report(new ImportProgress(-1, $"Opening {Path.GetFileName(mrpackPath)}..."));
        using var zip = ZipFile.OpenRead(mrpackPath);

        var indexEntry = zip.GetEntry("modrinth.index.json")
            ?? throw new InvalidOperationException("Not a valid .mrpack - missing modrinth.index.json");

        MrIndex index;
        await using (var stream = indexEntry.Open())
            index = await JsonSerializer.DeserializeAsync<MrIndex>(stream, cancellationToken: ct)
                    ?? throw new InvalidOperationException("Failed to parse modrinth.index.json");

        // Collected now so an archive past the unpacking limits is refused before an instance is
        // created or anything is downloaded.
        var overridesEntry = zip.Entries
            .Where(e => e.FullName.StartsWith("overrides/", StringComparison.Ordinal) && !e.FullName.EndsWith('/'))
            .ToList();
        if (SafeZip.CheckLimits(overridesEntry) is { } tooBig)
            throw new InvalidDataException($"Refused to import this modpack: {tooBig}.");

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

        // Download required files, several at a time.
        var files = index.Files ?? new();
        var mrCompleted = 0;
        using var mrGate = new SemaphoreSlim(DownloadConcurrency, DownloadConcurrency);
        await Task.WhenAll(files.Select(async (f, i) =>
        {
            var fileIndex = i;
            var fileCount = files.Count;
            var solo = DownloadConcurrency == 1 || fileCount == 1;
            ct.ThrowIfCancellationRequested();
            // Wait out a pause before taking a slot, so a paused import doesn't hold the download slots
            // it needs to resume.
            if (job is not null) await job.Gate.WaitAsync(ct);
            var dest = PathSafety.ResolveInside(gameDir, WithoutDotPrefix(f.Path));
            if (dest is null) { Warn(log, $"skipping file with an unsafe path: {f.Path}"); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            var fileName = Path.GetFileName(f.Path);
            if (File.Exists(dest))
            {
                log?.Report($"  Skip (exists): {f.Path}");
                ReportBatch(progress, Interlocked.Increment(ref mrCompleted), fileCount, $"Skipped {fileName}", solo);
                return;
            }

            await mrGate.WaitAsync(ct);
            try
            {
                var label = $"Downloading {fileName}";
                log?.Report($"  [{fileIndex + 1}/{fileCount}] {fileName}");
                var downloaded = false;
                foreach (var url in f.Downloads ?? Enumerable.Empty<string>())
                {
                    if (!SafeLaunch.IsWebUrl(url, out _))
                    {
                        Warn(log, $"skipping a download link for {f.Path} that is not http or https");
                        continue;
                    }
                    try
                    {
                        // A single-file import gets a real per-file bar; a parallel batch reports the
                        // batch, as there's no single current file.
                        var fileProgress = solo
                            ? new Progress<(long done, long total)>(p =>
                            {
                                if (p.total <= 0) return;
                                var current = Math.Min(1.0, p.done / (double)p.total);
                                progress?.Report(new ImportProgress(
                                    fileCount == 0 ? 1 : (mrCompleted + current) / fileCount, label, current, fileName));
                            })
                            : null;
                        await modrinth.DownloadFileAsync(url, dest, fileProgress, ct, job?.Gate);
                        job?.TrackCreatedFile(dest);   // nothing was at this path before (see the skip above)
                        downloaded = true;
                        break;
                    }
                    // Rethrow so Stop isn't mistaken for a dead mirror and the import doesn't carry on
                    // with the next URL.
                    catch (OperationCanceledException) { throw; }
                    catch { /* try next URL */ }
                }
                if (!downloaded) Warn(log, $"could not download {f.Path}");
            }
            finally
            {
                mrGate.Release();
                ReportBatch(progress, Interlocked.Increment(ref mrCompleted), fileCount, fileName, solo);
            }
        }));

        await CopyOverridesAsync(overridesEntry, "overrides/", gameDir, log, progress, ct, job);

        RecordPackSource(pack.Id, ModSource.Modrinth, log);
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
        CancellationToken ct = default,
        PackJob? job = null) =>
        ImportCurseForgeZipCoreAsync(zipPath, packName, null, log, progress, null, ct, job);

    public Task<PackSummary> ImportCurseForgeZipIntoPackAsync(
        string zipPath,
        Guid packId,
        IProgress<string>? log,
        IProgress<ImportProgress>? progress = null,
        PackImportMetadata? metadata = null,
        CancellationToken ct = default,
        PackJob? job = null) =>
        ImportCurseForgeZipCoreAsync(zipPath, null, packId, log, progress, metadata, ct, job);

    private async Task<PackSummary> ImportCurseForgeZipCoreAsync(
        string zipPath,
        string? packName,
        Guid? existingPackId,
        IProgress<string>? log,
        IProgress<ImportProgress>? progress,
        PackImportMetadata? metadata,
        CancellationToken ct,
        PackJob? job)
    {
        log?.Report($"Opening {Path.GetFileName(zipPath)}...");
        progress?.Report(new ImportProgress(-1, $"Opening {Path.GetFileName(zipPath)}..."));
        using var zip = ZipFile.OpenRead(zipPath);

        var manifestEntry = zip.GetEntry("manifest.json")
            ?? throw new InvalidOperationException("Not a valid CurseForge pack - missing manifest.json");

        CfManifest manifest;
        await using (var stream = manifestEntry.Open())
            manifest = await JsonSerializer.DeserializeAsync<CfManifest>(stream, CfManifestJson, ct)
                       ?? throw new InvalidOperationException("Failed to parse manifest.json");

        // The overrides folder name comes from the manifest, so it gets the same path checks as the
        // entries in it. Collected now so an archive over the unpacking limits is refused before an
        // instance is created or anything is downloaded.
        var overridesFolder = manifest.Overrides ?? "overrides";
        var prefix = overridesFolder + "/";
        List<ZipArchiveEntry> overrides;
        if (PathSafety.IsSafeRelativePath(overridesFolder))
        {
            overrides = zip.Entries
                .Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal) && !e.FullName.EndsWith('/'))
                .ToList();
        }
        else
        {
            Warn(log, $"skipping the overrides, their folder name is not a plain path: {overridesFolder}");
            overrides = [];
        }
        if (SafeZip.CheckLimits(overrides) is { } tooBig)
            throw new InvalidDataException($"Refused to import this modpack: {tooBig}.");

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

        // Resolve and download mods via the CurseForge API. Files and projects are looked up in batches
        // (two calls per fifty mods); per-mod calls get the launcher server rate-limited on big packs.
        var files = manifest.Files ?? new();
        progress?.Report(new ImportProgress(-1, $"Resolving {files.Count} mods on CurseForge..."));
        Dictionary<int, ModVersion> knownFiles;
        Dictionary<int, ModSummary> knownMods;
        try { knownFiles = await curseforge.GetFilesAsync(files.Select(f => f.FileId), ct); }
        catch (Exception ex) { Warn(log, $"batch file lookup failed ({ex.Message}); resolving one by one."); knownFiles = new(); }
        try { knownMods = await curseforge.GetModsAsync(files.Select(f => f.ProjectId), ct); }
        catch (Exception ex) { Warn(log, $"batch mod lookup failed ({ex.Message}); resolving one by one."); knownMods = new(); }
        // Each file goes to the folder its project class says, as the CurseForge app and Prism do.
        // CurseForge packs list resource packs and shaders alongside mods, and a shader in mods/ is never
        // loaded.
        Dictionary<int, CurseForgeProjectFacts> projects;
        try { projects = await curseforge.GetProjectFactsAsync(files.Select(f => f.ProjectId), ct); }
        catch (Exception ex) { Warn(log, $"could not ask what each file is ({ex.Message}); everything goes into mods."); projects = new(); }

        var cfCompleted = 0;
        // Files CurseForge would not hand out (an author's opt-out, mostly), with the sentence that
        // says where to get each one, for the note written next to the mods at the end.
        var missing = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var cfGate = new SemaphoreSlim(DownloadConcurrency, DownloadConcurrency);
        await Task.WhenAll(files.Select(async (f, i) =>
        {
            ct.ThrowIfCancellationRequested();
            var fileIndex = i;
            var fileCount = files.Count;
            var solo = DownloadConcurrency == 1 || fileCount == 1;
            // Wait out a pause before taking a slot, so a paused import doesn't hold the download slots
            // it needs to resume.
            if (job is not null) await job.Gate.WaitAsync(ct);
            await cfGate.WaitAsync(ct);
            try
            {
                log?.Report($"  [{fileIndex + 1}/{fileCount}] Resolving mod {f.ProjectId}:{f.FileId}...");
                knownFiles.TryGetValue(f.FileId, out var knownVersion);
                var url = knownVersion?.Files.FirstOrDefault()?.DownloadUrl;
                if (string.IsNullOrWhiteSpace(url))
                {
                    // The listing had no link: ask for one. A file CurseForge will not hand out throws
                    // with its own sentence (project, page, "only the CurseForge app"), noted below.
                    try { url = await curseforge.GetDownloadUrlAsync(f.ProjectId, f.FileId, ct); }
                    catch (StoreRequestException ex) when (ex.Failure == StoreFailure.NotDistributable)
                    {
                        missing.Add(ex.Plain);
                        Warn(log, ex.Plain);
                        return;
                    }
                }
                if (!SafeLaunch.IsWebUrl(url, out var uri))
                {
                    Warn(log, $"skipping {f.ProjectId}:{f.FileId}, its download link is not http or https");
                    return;
                }
                // Saved under the link's last segment, so that must be one plain name.
                var filename = Path.GetFileName(uri!.LocalPath);
                if (!PathSafety.IsSafeFileName(filename))
                {
                    Warn(log, $"skipping {f.ProjectId}:{f.FileId}, its file name is not usable: {filename}");
                    return;
                }
                var folder = projects.GetValueOrDefault(f.ProjectId).ClassId switch
                {
                    CurseForgeService.ClassIdResourcePacks => "resourcepacks",
                    CurseForgeService.ClassIdShaders => "shaderpacks",
                    _ => "mods"
                };
                // Optional means "installed disabled", which only applies to mods. Resource packs and
                // shaders do nothing until picked in game anyway.
                if (!f.Required && folder == "mods") filename += ".disabled";
                var targetDir = Path.Combine(gameDir, folder);
                var dest = PathSafety.ResolveFileName(targetDir, filename);
                if (dest is null)
                {
                    Warn(log, $"skipping {f.ProjectId}:{f.FileId}, its file name is not usable: {filename}");
                    return;
                }
                Directory.CreateDirectory(targetDir);
                log?.Report($"  Downloading {filename}...");
                var label = $"Downloading {filename}";
                var fileProgress = solo
                    ? new Progress<(long done, long total)>(p =>
                    {
                        if (p.total <= 0) return;
                        var current = Math.Min(1.0, p.done / (double)p.total);
                        progress?.Report(new ImportProgress(
                            fileCount == 0 ? 1 : (cfCompleted + current) / fileCount, label, current, filename));
                    })
                    : null;
                var isNewFile = !File.Exists(dest);
                await modrinth.DownloadFileAsync(url, dest, fileProgress, ct, job?.Gate);
                if (isNewFile) job?.TrackCreatedFile(dest);
                knownMods.TryGetValue(f.ProjectId, out var knownMod);
                await RememberCurseForgeMatchAsync(dest, f.ProjectId, f.FileId, knownMod, knownVersion, ct);
            }
            // Rethrow: swallowing the cancellation would leave a stopped import looking finished, with an
            // empty instance.
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                missing.Add($"{f.ProjectId}:{f.FileId}: {StoreRequestException.PlainFor(ex) ?? ex.Message}");
                Warn(log, $"could not download {f.ProjectId}:{f.FileId}: {ex.Message}");
            }
            finally
            {
                cfGate.Release();
                ReportBatch(progress, Interlocked.Increment(ref cfCompleted), files.Count, "mods", solo);
            }
        }));

        await CopyOverridesAsync(overrides, prefix, gameDir, log, progress, ct, job);

        RecordPackSource(pack.Id, ModSource.CurseForge, log);
        var note = missing.IsEmpty ? "" : WriteManualDownloadsNote(gameDir, missing, job);
        log?.Report($"Import complete: {pack.Name}{note}");
        if (note.Length > 0) AppLog.Log("import", $"{pack.Name}{note}");
        progress?.Report(new ImportProgress(1, $"Import complete: {pack.Name}{note}"));
        await SaveImportAssetsAsync(pack.Id, metadata, ct);
        return pack;
    }

    /// <summary>
    /// Writes <see cref="ManualDownloadsFileName"/> into the game folder, one file per line with where
    /// to get it, and returns the sentence to append to the completion line. The pack still opens
    /// without those files, so this is a note rather than a failure.
    /// </summary>
    private static string WriteManualDownloadsNote(string gameDir, IEnumerable<string> missing, PackJob? job)
    {
        var lines = missing.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
        try
        {
            var path = Path.Combine(gameDir, ManualDownloadsFileName);
            var isNew = !File.Exists(path);
            File.WriteAllLines(path,
            [
                $"{lines.Count} file(s) in this pack could not be downloaded by the launcher.",
                "Most are mods whose authors allow downloads through the CurseForge app only: get each from",
                "its project page (or the CurseForge app) and drop it into the mods folder.",
                "",
                .. lines
            ]);
            if (isNew) job?.TrackCreatedFile(path);
        }
        catch (Exception ex)
        {
            AppLog.Log("import", $"Could not write {ManualDownloadsFileName}: {ex.Message}");
        }
        return $". {lines.Count} file(s) could not be downloaded (see {ManualDownloadsFileName} in the instance folder)";
    }

    /// <summary>Unpacks a modpack's override files into the game folder. An entry whose path is not a
    /// plain path inside the game folder, or whose sizes are not believable, is skipped with a warning
    /// instead of written.</summary>
    /// <param name="entries">The files under the overrides folder, already held to
    /// <see cref="SafeZip.CheckLimits"/>.</param>
    /// <param name="prefix">The overrides folder inside the archive, with its trailing slash.</param>
    private static async Task CopyOverridesAsync(
        IReadOnlyList<ZipArchiveEntry> entries, string prefix, string gameDir,
        IProgress<string>? log, IProgress<ImportProgress>? progress, CancellationToken ct, PackJob? job)
    {
        if (entries.Count > 0)
        {
            log?.Report($"Copying {entries.Count} override files...");
            progress?.Report(new ImportProgress(1, $"Copying {entries.Count} override files...", -1, "Overrides"));
        }
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (job is not null) await job.Gate.WaitAsync(ct);
            var dest = PathSafety.ResolveInside(gameDir, WithoutDotPrefix(entry.FullName[prefix.Length..]));
            if (dest is null) { Warn(log, $"skipping override with an unsafe path: {entry.FullName}"); continue; }
            if (SafeZip.CheckEntry(entry) is { } implausible)
            {
                Warn(log, $"skipping override {entry.FullName}: {implausible}");
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            // Only files we create get removed again if the import is stopped.
            var isNew = !File.Exists(dest);
            try
            {
                SafeZip.ExtractToFile(entry, dest, overwrite: true);
            }
            catch (InvalidDataException ex)
            {
                Warn(log, $"skipping override {entry.FullName}: {ex.Message}");
                continue;
            }
            if (isNew) job?.TrackCreatedFile(dest);
        }
    }

    /// <summary>A path from a modpack without the leading "./" some tools write, which
    /// <see cref="PathSafety"/> would otherwise refuse as a "." segment.</summary>
    private static string? WithoutDotPrefix(string? path)
    {
        while (path is not null && (path.StartsWith("./", StringComparison.Ordinal) || path.StartsWith(".\\", StringComparison.Ordinal)))
            path = path[2..];
        return path;
    }

    /// <summary>Progress for a batch of parallel downloads: the count is exact, and the per-file bar
    /// is indeterminate because several files are moving at once.</summary>
    private static void ReportBatch(IProgress<ImportProgress>? progress, int completed, int total, string current, bool solo)
    {
        if (progress is null) return;
        var fraction = total == 0 ? 1 : Math.Min(1.0, (double)completed / total);
        progress.Report(new ImportProgress(
            fraction,
            $"Downloaded {completed}/{total}" + (solo ? $": {current}" : ""),
            solo ? 1 : -1,
            current));
    }

    private async Task RememberCurseForgeMatchAsync(
        string path, int projectId, int fileId, ModSummary? mod, ModVersion? version, CancellationToken ct)
    {
        try
        {
            mod ??= await curseforge.GetModAsync(projectId, ct);
            version ??= await curseforge.GetVersionAsync(projectId, fileId, ct);
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
        /// <summary>False = the pack ships this mod switched off (exporters write disabled mods so).</summary>
        [JsonPropertyName("required")] public bool Required { get; set; } = true;
    }
}
