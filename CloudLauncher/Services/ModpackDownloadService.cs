using System.IO;
using System.IO.Compression;
using System.Net;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

public sealed record PackImportMetadata(
    string? Summary,
    string? DescriptionExcerpt,
    string? DescriptionHtml,
    string? IconUrl,
    string? SourceLabel,
    bool DescriptionIsMarkdown = false);

/// <summary>
/// Starts modpack downloads in the background, creates a placeholder instance immediately,
/// and reports progress through <see cref="ProgressHub"/>.
/// </summary>
public sealed class ModpackDownloadService(
    ModrinthService modrinth,
    CurseForgeService curseforge,
    ModpackImportService import,
    ApiClient api,
    PackFolderService packs,
    PackAssetService assets,
    AppSettings settings)
{
    public event Action<PackSummary>? PackAdded;

    public async Task<PackSummary> StartExternalDownloadAsync(
        ModSummary mod,
        ModVersion? selectedVersion,
        PackImportMetadata? metadata = null,
        CancellationToken ct = default)
    {
        metadata = await ResolveMetadataAsync(mod, metadata, ct);
        var fields = MetadataToFields(metadata, mod);

        var pack = await api.CreatePackAsync(new CreatePackRequest(
            mod.Name, fields.DescriptionExcerpt, IsEmpty: true, null, LoaderKind.None, null, fields.Summary), ct);

        packs.EnsurePackFolder(pack.Id, pack.Name, pack.IsShared);
        NotifyPackAdded(pack);
        ProgressHub.Indeterminate(pack.Id, $"Preparing {mod.Name}...");

        // The caller's token only covers creating the instance. The transfer outlives this call and is
        // stopped from wherever it shows on screen.
        var job = PackJobs.Start(pack.Id, PackJobKind.Download, mod.Name);
        _ = Task.Run(async () =>
        {
            try
            {
                await RunExternalImportAsync(pack.Id, mod, selectedVersion, metadata, job);
            }
            catch (OperationCanceledException)
            {
                await MarkImportStoppedAsync(pack.Id, job);
            }
            catch (Exception ex)
            {
                await MarkImportFailedAsync(pack.Id, ex.Message, CancellationToken.None);
            }
            finally { PackJobs.Finish(job); }
        });

        return pack;
    }

    public async Task<PackSummary> StartLocalFileImportAsync(string filePath, string packName, CancellationToken ct = default)
    {
        var pack = await api.CreatePackAsync(new CreatePackRequest(
            packName, $"Imported from {Path.GetFileName(filePath)}", IsEmpty: true,
            null, LoaderKind.None, null), ct);

        packs.EnsurePackFolder(pack.Id, pack.Name, pack.IsShared);
        NotifyPackAdded(pack);
        ProgressHub.Indeterminate(pack.Id, $"Preparing {packName}...");

        var job = PackJobs.Start(pack.Id, PackJobKind.Download, packName);
        _ = Task.Run(async () =>
        {
            try { await RunLocalImportAsync(pack.Id, filePath, job); }
            finally { PackJobs.Finish(job); }
        });

        return pack;
    }

    public async Task<PackSummary> SubscribeInternalPackAsync(PackSummary browsed, CancellationToken ct = default)
    {
        var pack = await api.SubscribePackAsync(browsed.Id, ct);
        // Clear any local "hidden" flag (a non-owner removal hides and unsubscribes), or the pack stays
        // out of the instance list with no way to add it back.
        settings.UnhidePack(pack.Id);
        packs.EnsurePackFolder(pack.Id, pack.Name, pack.IsShared);
        NotifyPackAdded(pack);
        if (pack.IsShared && pack.EffectivePermissions.HasFlag(PackPermissions.Download))
            await TryDownloadSharedContentAsync(pack.Id, ct);
        return pack;
    }

    private async Task TryDownloadSharedContentAsync(Guid packId, CancellationToken ct)
    {
        try
        {
            var manifest = await api.GetManifestAsync(packId, ct);
            if (manifest.Entries.Count == 0) return;
            await packs.DownloadSharedAsync(packId, null, ct);
        }
        catch
        {
            /* subscribe should succeed even if sync fails */
        }
    }

    private async Task RunExternalImportAsync(
        Guid packId,
        ModSummary mod,
        ModVersion? selectedVersion,
        PackImportMetadata? metadata,
        PackJob job)
    {
        var ct = job.Token;
        string? tempPath = null;
        try
        {
            metadata = await ResolveMetadataAsync(mod, metadata, ct);
            await SavePackAssetsAsync(packId, MetadataToFields(metadata, mod), metadata, ct);

            ProgressHub.Report(packId, 0, $"Resolving {mod.Name}...");

            var version = selectedVersion ?? await ResolveLatestVersionAsync(mod, ct);
            var file = version.Files.FirstOrDefault(f => f.IsPrimary) ?? version.Files.FirstOrDefault()
                       ?? throw new InvalidOperationException("No downloadable file found.");

            file = await ResolveDownloadFileAsync(mod, version, file, ct);
            if (string.IsNullOrWhiteSpace(file.DownloadUrl))
                throw new InvalidOperationException("No download URL.");
            if (!SafeLaunch.IsWebUrl(file.DownloadUrl, out _))
            {
                AppLog.Log("download", $"Refused to download {mod.Name}: its link is not http or https.");
                throw new InvalidOperationException("The download link is not an http or https address.");
            }

            tempPath = Path.Combine(Path.GetTempPath(), $"cloudlauncher-{Guid.NewGuid():N}.zip");

            ProgressHub.Report(packId, 0, $"Downloading {mod.Name}...", 0, file.Filename);
            var downloadProgress = new Progress<(long done, long total)>(p =>
            {
                if (p.total <= 0)
                {
                    ProgressHub.Indeterminate(packId, $"Downloading {mod.Name}...", file.Filename);
                    return;
                }

                var frac = Math.Min(1.0, p.done / (double)p.total);
                ProgressHub.Report(packId, frac * 0.15, $"Downloading {mod.Name}...", frac, file.Filename);
            });

            await modrinth.DownloadFileAsync(file.DownloadUrl, tempPath, downloadProgress, ct, job.Gate);
            ValidateDownloadedArchive(tempPath);

            var importProgress = new Progress<ImportProgress>(p =>
            {
                var scaled = 0.15 + p.TotalFraction * 0.85;
                ProgressHub.Report(packId, scaled, p.TotalLabel, p.CurrentFraction, p.CurrentLabel);
            });

            PackSummary result;
            if (IsMrpackArchive(tempPath))
                result = await import.ImportMrpackIntoPackAsync(tempPath, packId, null, importProgress, metadata, ct, job);
            else
                result = await import.ImportCurseForgeZipIntoPackAsync(tempPath, packId, null, importProgress, metadata, ct, job);

            NotifyPackAdded(result);
            ProgressHub.Report(packId, 1, $"Ready: {result.Name}");
            ProgressHub.Clear(packId);
        }
        catch (OperationCanceledException)
        {
            // Stopped by the user, not a failure. The job's token is already cancelled, so the rollback
            // doesn't use it.
            await MarkImportStoppedAsync(packId, job);
        }
        catch (Exception ex)
        {
            await MarkImportFailedAsync(packId, ex.Message, CancellationToken.None);
        }
        finally
        {
            if (tempPath is not null)
            {
                try { File.Delete(tempPath); } catch { /* best-effort */ }
            }
        }
    }

    private async Task RunLocalImportAsync(Guid packId, string filePath, PackJob job)
    {
        var ct = job.Token;
        try
        {
            var importProgress = new Progress<ImportProgress>(p =>
                ProgressHub.Report(packId, p.TotalFraction, p.TotalLabel, p.CurrentFraction, p.CurrentLabel));

            PackSummary result;
            if (filePath.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))
                result = await import.ImportMrpackIntoPackAsync(filePath, packId, null, importProgress, null, ct, job);
            else
                result = await import.ImportCurseForgeZipIntoPackAsync(filePath, packId, null, importProgress, null, ct, job);

            NotifyPackAdded(result);
            ProgressHub.Report(packId, 1, $"Ready: {result.Name}");
            ProgressHub.Clear(packId);
        }
        catch (OperationCanceledException)
        {
            await MarkImportStoppedAsync(packId, job);
        }
        catch (Exception ex)
        {
            await MarkImportFailedAsync(packId, ex.Message, CancellationToken.None);
        }
    }

    /// <summary>
    /// Rolls back a stopped download: deletes the files it brought in and resets the instance to an
    /// empty placeholder. Unlike a failure, a stop isn't noted in the pack description.
    /// </summary>
    private async Task MarkImportStoppedAsync(Guid packId, PackJob job)
    {
        var removed = job.RollbackCreatedFiles();
        AppLog.Log("download", $"Stopped; removed {removed} downloaded file(s) from {packId}.");

        try
        {
            // Mark it empty so it shows as not downloaded yet. The icon, summary and description stay.
            var updated = await api.UpdatePackAsync(packId, new UpdatePackRequest(
                null, null, null, null, IsEmpty: true, null, null, null), CancellationToken.None);
            NotifyPackAdded(updated);
        }
        catch { /* the local rollback is what matters; the flag catches up on the next refresh */ }

        ProgressHub.Clear(packId);
    }

    private async Task MarkImportFailedAsync(Guid packId, string message, CancellationToken ct)
    {
        ProgressHub.Report(packId, 0, $"Failed: {message}");
        try
        {
            var failedDescription = PackText.Truncate($"Import failed: {message}", PackText.DescriptionMaxLength);
            var failedSummary = PackText.Truncate($"Import failed: {message}", PackText.SummaryMaxLength);
            var updated = await api.UpdatePackAsync(packId, new UpdatePackRequest(
                null, failedDescription, null, null, true, null, LoaderKind.None, null, null, failedSummary), ct);
            NotifyPackAdded(updated);
        }
        catch { /* keep the instance even if we can't annotate the failure */ }
        ProgressHub.Clear(packId);
    }

    private async Task<PackImportMetadata> ResolveMetadataAsync(
        ModSummary mod,
        PackImportMetadata? metadata,
        CancellationToken ct)
    {
        var hasRichBody = !string.IsNullOrWhiteSpace(metadata?.DescriptionHtml);
        if (hasRichBody && !string.IsNullOrWhiteSpace(metadata?.Summary))
            return metadata!;

        try
        {
            if (mod.Source == ModSource.CurseForge && int.TryParse(mod.Id, out var cfId))
            {
                var detail = await curseforge.GetProjectDetailAsync(cfId, ct);
                var fields = PackText.FieldsFromExternalProject(mod.Description, detail.Description, false);
                return new PackImportMetadata(
                    metadata?.Summary ?? fields.Summary,
                    metadata?.DescriptionExcerpt ?? fields.DescriptionExcerpt,
                    metadata?.DescriptionHtml ?? fields.DescriptionHtml,
                    metadata?.IconUrl ?? mod.IconUrl,
                    metadata?.SourceLabel ?? "CurseForge");
            }

            var mrDetail = await modrinth.GetProjectDetailAsync(mod.Id, ct);
            var body = mrDetail.Description ?? mod.Description;
            var isMarkdown = !PackText.LooksLikeHtml(body);
            var mrFields = PackText.FieldsFromExternalProject(mod.Description, body, isMarkdown);
            return new PackImportMetadata(
                metadata?.Summary ?? mrFields.Summary,
                metadata?.DescriptionExcerpt ?? mrFields.DescriptionExcerpt,
                metadata?.DescriptionHtml ?? mrFields.DescriptionHtml,
                metadata?.IconUrl ?? mod.IconUrl,
                metadata?.SourceLabel ?? "Modrinth",
                isMarkdown);
        }
        catch
        {
            var fallback = PackText.FieldsFromExternalProject(mod.Description, mod.Description);
            return metadata ?? new PackImportMetadata(
                fallback.Summary,
                fallback.DescriptionExcerpt,
                fallback.DescriptionHtml,
                mod.IconUrl,
                mod.Source.ToString());
        }
    }

    private static PackImportFields MetadataToFields(PackImportMetadata metadata, ModSummary mod)
    {
        if (!string.IsNullOrWhiteSpace(metadata.DescriptionHtml))
            return PackText.FieldsFromExternalProject(
                metadata.Summary ?? mod.Description,
                metadata.DescriptionHtml,
                metadata.DescriptionIsMarkdown);

        if (!string.IsNullOrWhiteSpace(metadata.DescriptionExcerpt))
        {
            return new PackImportFields(
                metadata.Summary ?? PackText.Truncate(mod.Description, PackText.SummaryMaxLength),
                PackText.Truncate(metadata.DescriptionExcerpt, PackText.DescriptionMaxLength),
                null,
                metadata.DescriptionIsMarkdown);
        }

        return PackText.FieldsFromExternalProject(mod.Description, mod.Description);
    }

    private async Task SavePackAssetsAsync(Guid packId, PackImportFields fields, PackImportMetadata metadata, CancellationToken ct)
    {
        if (fields.DescriptionIsMarkdown && !string.IsNullOrWhiteSpace(fields.DescriptionSource))
            assets.SaveDescriptionMarkdown(packId, fields.DescriptionSource);
        else if (!string.IsNullOrWhiteSpace(fields.DescriptionHtml))
            assets.SaveDescriptionHtml(packId, fields.DescriptionHtml);

        try
        {
            await assets.SaveIconFromUrlAsync(packId, metadata.IconUrl, ct);
        }
        catch
        {
            /* icon is optional */
        }
    }

    private async Task<ModVersion> ResolveLatestVersionAsync(ModSummary mod, CancellationToken ct)
    {
        var versions = mod.Source == ModSource.CurseForge && int.TryParse(mod.Id, out var cfId)
            ? await curseforge.GetVersionsAsync(cfId, ct)
            : await modrinth.GetVersionsAsync(mod.Id, ct: ct);

        var latest = versions.OrderByDescending(v => v.DatePublished).FirstOrDefault()
                     ?? throw new InvalidOperationException("No versions found.");
        return latest;
    }

    private async Task<ModVersionFile> ResolveDownloadFileAsync(
        ModSummary mod, ModVersion version, ModVersionFile file, CancellationToken ct)
    {
        if (version.Source != ModSource.CurseForge)
            return file;

        var named = string.IsNullOrWhiteSpace(file.Filename) ? file with { Filename = "modpack.zip" } : file;

        // Only call /download-url when the version listing had no URL. That extra request can fail on
        // its own (a CDN block, or an author who opted out of third-party downloads) even though the
        // listed URL works. A pack CurseForge will not hand out throws with its own sentence, which
        // becomes the instance's "Import failed" note.
        if (!string.IsNullOrWhiteSpace(named.DownloadUrl))
            return named;

        return named with { DownloadUrl = await curseforge.ResolveDownloadUrlAsync(mod, version, named, ct) };
    }

    private static void ValidateDownloadedArchive(string path)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException("Downloaded modpack file is missing.");

        var size = new FileInfo(path).Length;
        if (size < 4)
            throw new InvalidOperationException("Downloaded modpack file is empty.");

        using var fs = File.OpenRead(path);
        Span<byte> header = stackalloc byte[4];
        if (fs.Read(header) < 4 || header[0] != 0x50 || header[1] != 0x4B)
            throw new InvalidOperationException("Downloaded file is not a valid modpack archive.");
    }

    private static bool IsMrpackArchive(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.GetEntry("modrinth.index.json") is not null;
    }

    private void NotifyPackAdded(PackSummary pack)
    {
        var handler = PackAdded;
        if (handler is null) return;

        var app = System.Windows.Application.Current;
        if (app is null) { handler(pack); return; }
        if (app.Dispatcher.CheckAccess()) handler(pack);
        else app.Dispatcher.BeginInvoke(() => handler(pack));
    }
}
