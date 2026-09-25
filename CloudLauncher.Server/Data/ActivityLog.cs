using CloudLauncher.Shared;
using Microsoft.EntityFrameworkCore;

namespace CloudLauncher.Server.Data;

/// <summary>Writes <see cref="ActivityEntry"/> rows, one line each, like "(user) shared (pack)
/// with (team)".</summary>
/// <remarks>
/// <para>Static so recording is one line in any controller that already holds an
/// <see cref="AppDbContext"/>. <see cref="ActivityWriter"/> wraps it for injection.</para>
/// <para>Never fails a request: the feed is secondary to the work it describes. Both entry points
/// swallow errors, and strings are truncated to their column widths so a long pack name can't turn
/// a successful upload into a 500.</para>
/// </remarks>
public static class ActivityLog
{
    private const int MaxSubjectName = 128;   // ActivityEntries."SubjectName" varchar(128)
    private const int MaxDetail = 256;        // ActivityEntries."Detail"      varchar(256)

    /// <summary>Queues one entry on the caller's change tracker without saving.</summary>
    /// <remarks>Preferred when the caller is about to save anyway: the entry then commits in the same
    /// transaction as what it describes. Use <see cref="WriteAsync"/> only after the work is
    /// saved.</remarks>
    public static ActivityEntry Record(
        AppDbContext db,
        Guid actorUserId,
        ActivityKind kind,
        ActivitySubjectType subjectType,
        Guid subjectId,
        string? subjectName,
        Guid? targetUserId = null,
        Guid? targetTeamId = null,
        string? detail = null)
    {
        var entry = new ActivityEntry
        {
            ActorUserId = actorUserId,
            Kind = kind,
            SubjectType = subjectType,
            SubjectId = subjectId,
            SubjectName = Truncate(subjectName, MaxSubjectName) ?? "(unnamed)",
            TargetUserId = targetUserId,
            TargetTeamId = targetTeamId,
            Detail = Truncate(detail, MaxDetail),
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.ActivityEntries.Add(entry);
        return entry;
    }

    /// <summary>Records one entry and saves it. Never throws, and never reports failure.</summary>
    /// <remarks>This saves everything pending on the context, so call it after the caller's own save.
    /// On failure the entry is detached so the caller's next save doesn't retry it.</remarks>
    public static async Task WriteAsync(
        AppDbContext db,
        Guid actorUserId,
        ActivityKind kind,
        ActivitySubjectType subjectType,
        Guid subjectId,
        string? subjectName,
        Guid? targetUserId = null,
        Guid? targetTeamId = null,
        string? detail = null,
        CancellationToken ct = default,
        ILogger? logger = null)
    {
        ActivityEntry? entry = null;
        try
        {
            entry = Record(db, actorUserId, kind, subjectType, subjectId, subjectName,
                           targetUserId, targetTeamId, detail);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            if (entry is not null)
            {
                try { db.Entry(entry).State = Microsoft.EntityFrameworkCore.EntityState.Detached; }
                catch { /* the context itself is unusable; nothing left to clean up */ }
            }
            logger?.LogWarning(ex, "Activity entry not written ({Kind} on {SubjectType} {SubjectId})",
                kind, subjectType, subjectId);
        }
    }

    /// <summary>How many rows are deleted per statement when old entries are pruned.</summary>
    private const int PruneBatch = 5000;

    /// <summary>How many entries were written before <paramref name="cutoff"/>.</summary>
    public static Task<int> CountOlderThanAsync(AppDbContext db, DateTimeOffset cutoff, CancellationToken ct = default) =>
        db.ActivityEntries.CountAsync(e => e.CreatedAt < cutoff, ct);

    /// <summary>Deletes every entry written before <paramref name="cutoff"/>. Returns how many went.</summary>
    /// <remarks>In batches, so no single statement locks or writes WAL for years of history at
    /// once.</remarks>
    public static async Task<int> DeleteOlderThanAsync(AppDbContext db, DateTimeOffset cutoff, CancellationToken ct = default)
    {
        var total = 0;
        while (true)
        {
            var ids = await db.ActivityEntries
                .Where(e => e.CreatedAt < cutoff)
                .OrderBy(e => e.CreatedAt)
                .Select(e => e.Id)
                .Take(PruneBatch)
                .ToListAsync(ct);
            if (ids.Count == 0) return total;

            total += await db.ActivityEntries.Where(e => ids.Contains(e.Id)).ExecuteDeleteAsync(ct);
            if (ids.Count < PruneBatch) return total;
        }
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}

/// <summary>An injectable face on <see cref="ActivityLog"/>, for controllers that prefer one.</summary>
/// <remarks>Registered scoped alongside the permission resolvers. Stateless; both write the same
/// rows.</remarks>
public class ActivityWriter(AppDbContext db, ILogger<ActivityWriter> logger)
{
    /// <inheritdoc cref="ActivityLog.Record"/>
    public ActivityEntry Record(
        Guid actorUserId, ActivityKind kind, ActivitySubjectType subjectType, Guid subjectId,
        string? subjectName, Guid? targetUserId = null, Guid? targetTeamId = null, string? detail = null)
        => ActivityLog.Record(db, actorUserId, kind, subjectType, subjectId, subjectName,
                              targetUserId, targetTeamId, detail);

    /// <inheritdoc cref="ActivityLog.WriteAsync"/>
    public Task WriteAsync(
        Guid actorUserId, ActivityKind kind, ActivitySubjectType subjectType, Guid subjectId,
        string? subjectName, Guid? targetUserId = null, Guid? targetTeamId = null,
        string? detail = null, CancellationToken ct = default)
        => ActivityLog.WriteAsync(db, actorUserId, kind, subjectType, subjectId, subjectName,
                                  targetUserId, targetTeamId, detail, ct, logger);
}
