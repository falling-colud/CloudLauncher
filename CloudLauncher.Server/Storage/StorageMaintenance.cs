using System.Globalization;
using CloudLauncher.Server.Data;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CloudLauncher.Server.Storage;

/// <summary>What <see cref="StorageMaintenance"/> is allowed to do, from <c>Storage:CleanupMode</c>.</summary>
public enum StorageCleanupMode
{
    /// <summary>Nothing is looked at or deleted.</summary>
    Off,

    /// <summary>Everything is looked at and what would be deleted is logged, but nothing is deleted.</summary>
    DryRun,

    /// <summary>Unreferenced blobs, stale upload temp files and old activity entries are deleted.</summary>
    Delete
}

/// <summary>The counts from one maintenance run.</summary>
public sealed class StorageMaintenanceReport
{
    public StorageCleanupMode Mode { get; init; }
    public int BlobsScanned { get; set; }
    public int BlobsInGrace { get; set; }
    public int BlobsInUse { get; set; }
    public int Orphans { get; set; }
    public long OrphanBytes { get; set; }
    public int OrphansDeleted { get; set; }
    public long OrphanBytesDeleted { get; set; }
    public int DeleteFailures { get; set; }
    public int TempFiles { get; set; }
    public long TempBytes { get; set; }
    public int TempFilesDeleted { get; set; }
    public int ActivityEntries { get; set; }
    public int PendingExpired { get; set; }
    public List<string> Samples { get; } = [];
}

/// <summary>Storage housekeeping, ten minutes after start and every six hours after that: deletes
/// blob files nothing references, deletes old activity entries, and expires the pending-upload
/// ledger.</summary>
/// <remarks>
/// <para>Catches blobs that per-row deletes miss (uncommitted uploads, whole mods or worlds deleted, a
/// request that failed between storing the file and saving its row). A blob is deleted only when no
/// row names it (<see cref="BlobReferences"/>), no upload has it pending, and it hasn't been written
/// for <see cref="Grace"/>; the write time is re-read just before deleting.</para>
/// <para><c>Storage:CleanupMode</c> is Off, DryRun (the default: logs counts and sample hashes only)
/// or Delete.</para>
/// </remarks>
public sealed class StorageMaintenance(
    IServiceScopeFactory scopes,
    BlobStore blobs,
    PendingUploadLedger pending,
    IConfiguration config,
    ILogger<StorageMaintenance> logger) : BackgroundService
{
    /// <summary>How long an unreferenced blob or upload temp file is left alone before it may go.</summary>
    public static readonly TimeSpan Grace = PendingUploadLedger.Lifetime;

    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private const int DefaultActivityRetentionDays = 365;

    /// <summary>How many old blob files are checked against the database in one go.</summary>
    private const int ProbeBatch = 5000;

    private const int SampleCount = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstRunDelay, stoppingToken);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // A failed run (database down, say) is retried on the next tick. Letting it
                    // escape would stop the whole host.
                    logger.LogWarning(ex, "Storage maintenance run failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One full pass. Public so it can be run on demand as well as on the timer.</summary>
    public async Task<StorageMaintenanceReport> RunOnceAsync(CancellationToken ct)
    {
        var mode = ReadMode();
        var report = new StorageMaintenanceReport { Mode = mode, PendingExpired = pending.Expire() };
        if (mode == StorageCleanupMode.Off)
        {
            logger.LogDebug("Storage maintenance is off (Storage:CleanupMode); expired {Expired} pending uploads",
                report.PendingExpired);
            return report;
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var deleteBlobs = mode == StorageCleanupMode.Delete;
        if (deleteBlobs && !await BlobReferences.AnyAsync(db, ct))
        {
            // No references at all more likely means the wrong or a freshly restored database than an empty
            // server, and deleting on its word would empty the store.
            logger.LogWarning("Storage maintenance found no rows referencing any blob; not deleting blobs this run");
            deleteBlobs = false;
        }

        await SweepBlobsAsync(db, deleteBlobs, report, ct);
        SweepTempFiles(deleteBlobs, report);

        var retentionDays = ReadInt("Storage:ActivityRetentionDays", DefaultActivityRetentionDays);
        if (retentionDays > 0)
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
            report.ActivityEntries = mode == StorageCleanupMode.Delete
                ? await ActivityLog.DeleteOlderThanAsync(db, cutoff, ct)
                : await ActivityLog.CountOlderThanAsync(db, cutoff, ct);
        }

        Log(report, deleteBlobs, retentionDays);
        return report;
    }

    /// <summary>Walks the blob store and settles every file older than <see cref="Grace"/>, a batch
    /// at a time.</summary>
    private async Task SweepBlobsAsync(AppDbContext db, bool delete, StorageMaintenanceReport report, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - Grace;
        var batch = new List<FileInfo>(ProbeBatch);
        foreach (var file in blobs.EnumerateBlobFiles())
        {
            ct.ThrowIfCancellationRequested();
            report.BlobsScanned++;
            if (file.LastWriteTimeUtc > cutoff)
            {
                report.BlobsInGrace++;
                continue;
            }

            batch.Add(file);
            if (batch.Count < ProbeBatch) continue;
            await SettleBatchAsync(db, batch, cutoff, delete, report, ct);
            batch.Clear();
        }
        if (batch.Count > 0) await SettleBatchAsync(db, batch, cutoff, delete, report, ct);
    }

    private async Task SettleBatchAsync(
        AppDbContext db, List<FileInfo> batch, DateTime cutoff, bool delete, StorageMaintenanceReport report, CancellationToken ct)
    {
        var named = await BlobReferences.FindReferencedAsync(db, batch.Select(f => f.Name), ct);
        foreach (var file in batch)
        {
            if (named.Contains(file.Name) || pending.IsPendingAnywhere(file.Name))
            {
                report.BlobsInUse++;
                continue;
            }

            // Re-read right before acting: an upload or sync begin reusing these bytes since the walk
            // started has refreshed the write time.
            file.Refresh();
            if (!file.Exists || file.LastWriteTimeUtc > cutoff)
            {
                report.BlobsInUse++;
                continue;
            }

            var size = file.Length;
            report.Orphans++;
            report.OrphanBytes += size;
            if (report.Samples.Count < SampleCount) report.Samples.Add(file.Name);
            if (!delete) continue;

            try
            {
                file.Delete();
                report.OrphansDeleted++;
                report.OrphanBytesDeleted += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report.DeleteFailures++;
                logger.LogDebug(ex, "Unreferenced blob {Hash} could not be deleted", file.Name);
            }
        }
    }

    /// <summary>Temp files an upload left behind when it crashed or was killed mid-write.</summary>
    private void SweepTempFiles(bool delete, StorageMaintenanceReport report)
    {
        var cutoff = DateTime.UtcNow - Grace;
        foreach (var file in blobs.EnumerateTempFiles())
        {
            if (file.LastWriteTimeUtc > cutoff) continue;
            report.TempFiles++;
            report.TempBytes += file.Length;
            if (!delete) continue;
            try
            {
                file.Delete();
                report.TempFilesDeleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report.DeleteFailures++;
            }
        }
    }

    private void Log(StorageMaintenanceReport report, bool deletedBlobs, int retentionDays)
    {
        var samples = report.Samples.Count == 0 ? "none" : string.Join(", ", report.Samples);
        var activity = retentionDays > 0
            ? $"{report.ActivityEntries} activity entries older than {retentionDays} days"
            : "no activity entries (Storage:ActivityRetentionDays is off)";
        if (report.Mode == StorageCleanupMode.Delete)
            logger.LogInformation(
                "Storage maintenance: deleted {Deleted} of {Orphans} unreferenced blobs ({DeletedBytes} of {OrphanBytes} bytes, e.g. {Samples}), {TempDeleted} of {TempFiles} stale upload temp files and {Activity}; {Failures} files could not be deleted. Scanned {Scanned} blobs, {InGrace} written within the grace period, {InUse} still referenced or pending. Expired {Expired} pending uploads.",
                report.OrphansDeleted, report.Orphans, report.OrphanBytesDeleted, report.OrphanBytes, samples,
                report.TempFilesDeleted, report.TempFiles, activity, report.DeleteFailures,
                report.BlobsScanned, report.BlobsInGrace, report.BlobsInUse, report.PendingExpired);
        else
            logger.LogInformation(
                "Storage maintenance (dry run): would delete {Orphans} unreferenced blobs ({OrphanBytes} bytes, e.g. {Samples}), {TempFiles} stale upload temp files ({TempBytes} bytes) and {Activity}. Scanned {Scanned} blobs, {InGrace} written within the grace period, {InUse} still referenced or pending. Expired {Expired} pending uploads.",
                report.Orphans, report.OrphanBytes, samples, report.TempFiles, report.TempBytes, activity,
                report.BlobsScanned, report.BlobsInGrace, report.BlobsInUse, report.PendingExpired);

        if (report.Mode == StorageCleanupMode.Delete && !deletedBlobs)
            logger.LogInformation("Blob deletion was held back this run; the counts above are what it would have deleted");
    }

    private StorageCleanupMode ReadMode()
    {
        var raw = config["Storage:CleanupMode"]?.Trim();
        switch (raw?.ToLowerInvariant())
        {
            case null or "":
            case "dryrun":
                return StorageCleanupMode.DryRun;
            case "off":
                return StorageCleanupMode.Off;
            case "delete":
                return StorageCleanupMode.Delete;
            default:
                logger.LogWarning("Storage:CleanupMode '{Value}' is not Off, DryRun or Delete; running as DryRun", raw);
                return StorageCleanupMode.DryRun;
        }
    }

    private int ReadInt(string key, int fallback) =>
        int.TryParse(config[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}

public static class StorageMaintenanceExtensions
{
    /// <summary>
    /// Registers storage accounting and upkeep: the pending-upload ledger, the <see cref="UploadGuard"/>
    /// the upload routes ask, and the <see cref="StorageMaintenance"/> job.
    /// </summary>
    public static IServiceCollection AddStorageMaintenance(this IServiceCollection services)
    {
        services.TryAddSingleton<PendingUploadLedger>();
        services.TryAddScoped<UploadGuard>();
        services.AddHostedService<StorageMaintenance>();
        return services;
    }
}
