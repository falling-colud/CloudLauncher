using System.Collections.Concurrent;
using System.Text.Json;
using CloudLauncher.Server.Auth;
using CloudLauncher.Server.Data;
using CloudLauncher.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CloudLauncher.Server.Controllers;

/// <summary>
/// A pack's planning boards, synced live: a launcher stores every edit here as it happens, and the
/// launchers with the board open hold a GET that answers the moment a newer revision is stored.
/// </summary>
/// <remarks>
/// Kept apart from the pack's files on purpose. Storing a board through the file sync would bump the
/// pack's manifest version, so every other launcher would be told the whole pack has an update and
/// the owner's next upload would refuse until they downloaded it.
/// </remarks>
[ApiController]
[Authorize]
[Route("packs/{packId:guid}/plans")]
public class PackPlansController(
    AppDbContext db,
    PackPermissionResolver resolver,
    PlanChangeSignals signals) : ControllerBase
{
    /// <summary>Larger than any real plans.json (a board of 2,000 cards is about 1 MB).</summary>
    private const int MaxPlanBytes = 8 * 1024 * 1024;

    /// <summary>The longest a GET waits for a change. Well inside nginx's read timeout.</summary>
    private const int MaxWaitSeconds = 25;

    /// <summary>The boards, or after <paramref name="after"/>, the next change.</summary>
    /// <param name="after">The revision the caller already has. When the server holds the same one,
    /// the request waits up to <paramref name="wait"/> seconds for a newer one and answers 204 if
    /// none came. A different revision (newer, or older after a restore) is answered at once.</param>
    [HttpGet]
    public async Task<IActionResult> Get(Guid packId, [FromQuery] long? after, [FromQuery] int wait,
        CancellationToken ct)
    {
        if (await CheckAsync(packId, PackPermissions.View, ct) is { } denied) return denied;

        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(wait, 0, MaxWaitSeconds));
        while (true)
        {
            // Taken before the read, so a store landing between the read and the wait still wakes us.
            var changed = signals.Next(packId);
            var revision = await db.PackPlans.AsNoTracking()
                .Where(p => p.PackId == packId).Select(p => (long?)p.Revision).FirstOrDefaultAsync(ct) ?? 0;
            if (after is null || revision != after) return Ok(await ReadAsync(packId, ct));

            var left = deadline - DateTimeOffset.UtcNow;
            if (left <= TimeSpan.Zero) return NoContent();
            try { await changed.WaitAsync(left, ct); }
            catch (TimeoutException) { /* read once more, then answer 204 */ }
        }
    }

    /// <summary>Stores a new version, if nobody stored one since <see cref="PutPackPlansRequest.BaseRevision"/>.
    /// Otherwise 409 with the current document, for the launcher to merge into and send again.</summary>
    [HttpPut]
    [RequestSizeLimit(MaxPlanBytes)]
    public async Task<IActionResult> Put(Guid packId, [FromBody] PutPackPlansRequest req, CancellationToken ct)
    {
        if (await CheckAsync(packId, PackPermissions.UploadShared, ct) is { } denied) return denied;
        if (req.Plans.ValueKind != JsonValueKind.Object
            || !req.Plans.TryGetProperty("Boards", out var boards) || boards.ValueKind != JsonValueKind.Array)
            return BadRequest(new { error = "Plans must be a plans.json document" });

        var doc = await db.PackPlans.FirstOrDefaultAsync(p => p.PackId == packId, ct);
        var current = doc?.Revision ?? 0;
        if (req.BaseRevision != current) return Conflict(await ReadAsync(packId, ct));

        if (doc is null)
        {
            doc = new PackPlanDocument { PackId = packId };
            db.PackPlans.Add(doc);
        }
        doc.Json = req.Plans.GetRawText();
        doc.Revision = current + 1;
        doc.UpdatedAt = DateTimeOffset.UtcNow;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another launcher stored on the same revision a moment earlier: a concurrency miss on
            // update, a duplicate key on the first insert. Either way theirs stands.
            db.ChangeTracker.Clear();
            return Conflict(await ReadAsync(packId, ct));
        }

        signals.Signal(packId);
        return Ok(new PackPlansStored(doc.Revision, doc.UpdatedAt));
    }

    private async Task<PackPlansDocument> ReadAsync(Guid packId, CancellationToken ct)
    {
        var doc = await db.PackPlans.AsNoTracking().FirstOrDefaultAsync(p => p.PackId == packId, ct);
        if (doc is null) return new PackPlansDocument(0, null, null);
        using var json = JsonDocument.Parse(doc.Json);
        return new PackPlansDocument(doc.Revision, doc.UpdatedAt, json.RootElement.Clone());
    }

    private async Task<IActionResult?> CheckAsync(Guid packId, PackPermissions required, CancellationToken ct)
    {
        var pack = await db.Packs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == packId, ct);
        if (pack is null) return NotFound(new { error = "Pack not found" });
        var perms = await resolver.GetAsync(pack, this.UserId(), ct);
        return perms.HasFlag(required) ? null : Forbid();
    }
}

/// <summary>
/// Wakes the GETs waiting on a pack's boards when a new revision is stored.
/// </summary>
/// <remarks>In memory, which is enough for the one server process. A waiter that misses a signal
/// (a restart) still answers at its timeout and the launcher simply asks again.</remarks>
public sealed class PlanChangeSignals
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _next = new();

    /// <summary>Completes on the next store to this pack.</summary>
    public Task Next(Guid packId) =>
        _next.GetOrAdd(packId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    public void Signal(Guid packId)
    {
        if (_next.TryRemove(packId, out var waiters)) waiters.TrySetResult();
    }
}
