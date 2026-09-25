using System.Globalization;
using System.Security.Cryptography;
using CloudLauncher.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CloudLauncher.Server.Storage;

/// <summary>
/// Decides whether an upload may be stored: the blob store's disk must stay above a floor, and the
/// owner of whatever the upload is for must stay within their storage quota.
/// </summary>
/// <remarks>
/// <para>Every route that writes to the blob store goes through this. Usage is
/// <see cref="StorageUsage"/> (each blob counted once) plus the owner's pending uploads in
/// <see cref="PendingUploadLedger"/>. The owner is charged, not the caller, so a collaborator's
/// upload spends the owner's quota.</para>
/// <para>Order: <see cref="RefuseAsync(Guid, Guid, long, string, CancellationToken)"/> before any
/// bytes land, <see cref="ChargeAsync"/> once they have, and <see cref="Settle(Guid, string)"/> after
/// the row naming the blob is saved.</para>
/// <para>A null <see cref="AppUser.StorageQuotaBytes"/> means unlimited, and a missing owner row is
/// not limited either.</para>
/// </remarks>
public sealed class UploadGuard(
    AppDbContext db,
    BlobStore blobs,
    PendingUploadLedger pending,
    IConfiguration config,
    ILogger<UploadGuard> logger)
{
    /// <summary>Free space the blob store's volume keeps when <c>Blobs:MinFreeBytes</c> is not set.</summary>
    public const long DefaultMinFreeBytes = 3L * 1024 * 1024 * 1024;

    private const string LowDiskMessage = "The server is low on disk space. Try again later.";

    /// <summary>A full disk refuses every upload, so the warning is logged at most this often.</summary>
    private static readonly TimeSpan WarningInterval = TimeSpan.FromMinutes(10);
    private static long s_lastWarningTicks;

    private readonly Dictionary<Guid, long?> _quotas = new();

    /// <summary>The configured floor, in bytes. Zero or less turns the check off.</summary>
    public long MinFreeBytes =>
        long.TryParse(config["Blobs:MinFreeBytes"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var floor)
            ? floor
            : DefaultMinFreeBytes;

    /// <summary>A 507 when storing <paramref name="incomingBytes"/> would leave the blob store's volume
    /// under <see cref="MinFreeBytes"/>, otherwise null.</summary>
    /// <remarks>If free space can't be read the upload is allowed with a warning, rather than failing
    /// every upload.</remarks>
    public ObjectResult? RefuseIfLowOnDisk(long incomingBytes = 0)
    {
        var floor = MinFreeBytes;
        if (floor <= 0) return null;

        long free;
        try
        {
            free = new DriveInfo(Path.GetFullPath(blobs.Root)).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (ShouldWarn())
                logger.LogWarning(ex, "Free space on the blob store volume could not be read; uploads are not being checked against the floor");
            return null;
        }

        if (free - Math.Max(0, incomingBytes) >= floor) return null;

        if (ShouldWarn())
            logger.LogWarning(
                "Blob store volume has {FreeBytes} bytes free, under the {FloorBytes} byte floor (Blobs:MinFreeBytes); refusing uploads",
                free, floor);
        return new ObjectResult(new { error = LowDiskMessage }) { StatusCode = StatusCodes.Status507InsufficientStorage };
    }

    /// <summary>
    /// The disk floor, then the owner's quota, for <paramref name="incomingBytes"/> that are about to be
    /// stored. Null when the upload may go ahead.
    /// </summary>
    /// <param name="hash">The bytes' hash when it is known up front. Bytes the owner already stores, or
    /// already has pending, cost them nothing more.</param>
    public async Task<ObjectResult?> RefuseAsync(
        Guid ownerId, Guid callerId, long incomingBytes, string? hash, CancellationToken ct)
    {
        if (RefuseIfLowOnDisk(incomingBytes) is { } lowDisk) return lowDisk;
        return await RefuseOverQuotaAsync(ownerId, callerId, incomingBytes, hash, file: null, ct);
    }

    /// <summary>The same checks for an upload that arrived as a form file.</summary>
    /// <remarks>The file is only hashed when its size alone would be refused, to find out whether the
    /// owner already has these bytes.</remarks>
    public async Task<ObjectResult?> RefuseAsync(Guid ownerId, Guid callerId, IFormFile file, CancellationToken ct)
    {
        if (RefuseIfLowOnDisk(file.Length) is { } lowDisk) return lowDisk;
        return await RefuseOverQuotaAsync(ownerId, callerId, file.Length, hash: null, file, ct);
    }

    /// <summary>
    /// Charges a blob that has just been stored to its owner as a pending upload. When it no longer fits
    /// (another upload finished first, or no size was declared up front) it is refused with a 413 and,
    /// if this request created the file and nothing else claims it, the file is removed again.
    /// </summary>
    public async Task<ObjectResult?> ChargeAsync(Guid ownerId, Guid callerId, StoredBlob blob, CancellationToken ct)
    {
        var quota = await QuotaAsync(ownerId, ct);
        var stored = quota is null ? 0 : await StorageUsage.GetStoredBytesAsync(db, blobs, ownerId, ct: ct);
        if (pending.TryAdd(ownerId, blob.Hash, blob.Size, stored, quota)) return null;

        // The owner already stores these bytes elsewhere, so keeping them costs nothing new.
        if (await StorageUsage.IsChargedToAsync(db, ownerId, blob.Hash, ct)) return null;

        var used = stored + pending.BytesFor(ownerId, blob.Hash);
        await DiscardAsync(blob);
        return QuotaExceeded(quota!.Value, used, blob.Size, ownerId == callerId);
    }

    /// <summary>
    /// Checks that a sync commit fits the pack owner's quota. Only what the new manifest adds counts,
    /// and a commit that adds nothing is allowed even over quota, so an account over its quota can
    /// still sync its way back down.
    /// </summary>
    /// <param name="files">The new manifest's blobs by hash, with their measured sizes.</param>
    public async Task<ObjectResult?> RefuseCommitAsync(
        Guid ownerId, Guid callerId, Guid packId, IReadOnlyDictionary<string, long> files, CancellationToken ct)
    {
        var quota = await QuotaAsync(ownerId, ct);
        if (quota is null) return null;

        var committed = new HashSet<string>(files.Keys, StringComparer.OrdinalIgnoreCase);
        var elsewhere = await StorageUsage.FindChargedAsync(db, ownerId, committed, exceptPackId: packId, ct);
        var after = await StorageUsage.GetStoredBytesAsync(db, blobs, ownerId, exceptPackId: packId, ct)
                    + files.Where(f => !elsewhere.Contains(f.Key)).Sum(f => f.Value)
                    + pending.BytesFor(ownerId, committed);
        if (after <= quota) return null;

        var before = await StorageUsage.GetStoredBytesAsync(db, blobs, ownerId, ct: ct) + pending.BytesFor(ownerId);
        if (after <= before) return null;
        return QuotaExceeded(quota.Value, before, after - before, ownerId == callerId);
    }

    /// <summary>Whether the owner has room for these blobs on top of what they already store.</summary>
    /// <param name="files">Blobs by hash, with their sizes.</param>
    public async Task<bool> HasRoomForAsync(Guid ownerId, IReadOnlyDictionary<string, long> files, CancellationToken ct)
    {
        var quota = await QuotaAsync(ownerId, ct);
        if (quota is null || files.Count == 0) return true;

        var theirs = await StorageUsage.FindChargedAsync(db, ownerId, files.Keys, ct: ct);
        var adding = files.Where(f => !theirs.Contains(f.Key)).Sum(f => f.Value);
        if (adding == 0) return true;

        var used = await StorageUsage.GetStoredBytesAsync(db, blobs, ownerId, ct: ct) + pending.BytesFor(ownerId);
        return used + adding <= quota.Value;
    }

    /// <summary>Clears a pending upload once the row naming it is saved; from then on the database charges it.</summary>
    public void Settle(Guid ownerId, string hash) => pending.Remove(ownerId, [hash]);

    /// <inheritdoc cref="Settle(Guid, string)"/>
    public void Settle(Guid ownerId, IEnumerable<string> hashes) => pending.Remove(ownerId, hashes);

    /// <summary>
    /// Removes a blob this request stored and is not going to use, unless something else needs it.
    /// </summary>
    /// <remarks>Leaves bytes that were already stored, that a row names, or that another upload is
    /// waiting to commit. Runs to completion even if the client has gone.</remarks>
    public async Task DiscardAsync(StoredBlob blob)
    {
        if (!blob.Created) return;
        if (pending.IsPendingAnywhere(blob.Hash)) return;
        await BlobReferences.DeleteIfUnreferencedAsync(db, blobs, blob.Hash, CancellationToken.None);
    }

    private async Task<ObjectResult?> RefuseOverQuotaAsync(
        Guid ownerId, Guid callerId, long incomingBytes, string? hash, IFormFile? file, CancellationToken ct)
    {
        var quota = await QuotaAsync(ownerId, ct);
        if (quota is null) return null;

        var used = await StorageUsage.GetStoredBytesAsync(db, blobs, ownerId, ct: ct) + pending.BytesFor(ownerId, hash);
        if (used + incomingBytes <= quota) return null;

        hash ??= file is null ? null : await HashOfAsync(file, ct);
        if (hash is not null
            && (pending.Contains(ownerId, hash) || await StorageUsage.IsChargedToAsync(db, ownerId, hash, ct)))
            return null;

        return QuotaExceeded(quota.Value, used, incomingBytes, ownerId == callerId);
    }

    /// <summary>The owner's quota in bytes, or null for none. Looked up once per request.</summary>
    private async Task<long?> QuotaAsync(Guid ownerId, CancellationToken ct)
    {
        if (_quotas.TryGetValue(ownerId, out var known)) return known;
        var row = await db.Users.AsNoTracking()
            .Where(u => u.Id == ownerId)
            .Select(u => new { u.StorageQuotaBytes })
            .FirstOrDefaultAsync(ct);
        var quota = row?.StorageQuotaBytes;
        _quotas[ownerId] = quota;
        return quota;
    }

    private static ObjectResult QuotaExceeded(long quota, long used, long needed, bool callerIsOwner)
    {
        var left = StorageUsage.Describe(Math.Max(0, quota - used));
        var who = callerIsOwner ? "You have" : "The owner has";
        var text = $"Not enough storage space. {who} {left} of {StorageUsage.Describe(quota)} left";
        text += needed > 0 ? $" and this needs {StorageUsage.Describe(needed)}." : ".";
        return new ObjectResult(new { error = text }) { StatusCode = StatusCodes.Status413PayloadTooLarge };
    }

    private static async Task<string> HashOfAsync(IFormFile file, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }

    private static bool ShouldWarn()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref s_lastWarningTicks);
        if (last != 0 && now - last < (long)WarningInterval.TotalMilliseconds) return false;
        return Interlocked.CompareExchange(ref s_lastWarningTicks, now, last) == last;
    }
}
