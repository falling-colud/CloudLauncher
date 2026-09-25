using CloudLauncher.Server.Auth;
using CloudLauncher.Server.Data;
using CloudLauncher.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CloudLauncher.Server.Controllers;

/// <summary>Reads the activity feed. Entries are written by <see cref="ActivityLog"/>.</summary>
/// <remarks>
/// Two queries, matching the two indexes on <c>ActivityEntries</c>: one subject's history and one
/// person's. Anything else would scan a table that only grows.
/// </remarks>
[ApiController]
[Authorize]
[Route("activity")]
public class ActivityController(
    AppDbContext db,
    PackPermissionResolver packs,
    ModPermissionResolver mods,
    SharedWorldPermissionResolver worlds,
    ResourcePackPermissionResolver resourcePacks,
    ContentBundlePermissionResolver bundles) : ControllerBase
{
    /// <summary>
    /// How many of the caller's own things <see cref="Mine"/> will look for activity on.
    /// </summary>
    /// <remarks>
    /// The caller's ids are fetched up front and matched through the index, which is much cheaper than
    /// a join per family. Anything past the cap drops out of the personal feed.
    /// </remarks>
    private const int MaxOwnedSubjects = 2000;

    /// <summary>One subject's history, newest first.</summary>
    /// <remarks>Visible to anyone who may view the subject, checked through that family's permission
    /// resolver, so the feed can't reveal who a private pack is shared with.</remarks>
    [HttpGet]
    public async Task<ActionResult<ActivityFeedPage>> ForSubject(
        [FromQuery] ActivitySubjectType? subjectType = null,
        [FromQuery] Guid? subjectId = null,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 25,
        [FromQuery] int? skip = null,
        [FromQuery] int? take = null,
        CancellationToken ct = default)
    {
        if (subjectType is null || subjectId is null || subjectId == Guid.Empty)
            return BadRequest(new { error = "subjectType and subjectId are required." });

        Normalize(ref offset, ref limit, skip, take);
        var me = this.UserId();

        if (!await CanViewSubjectAsync(subjectType.Value, subjectId.Value, me, ct))
            return Forbid();

        var type = subjectType.Value;
        var id = subjectId.Value;
        var query = db.ActivityEntries.Where(e => e.SubjectType == type && e.SubjectId == id);

        var total = await query.CountAsync(ct);
        var items = await Project(query).Skip(offset).Take(limit).ToListAsync(ct);
        return Ok(new ActivityFeedPage(items, offset, limit, total));
    }

    /// <summary>What happened to me, or to anything I own, newest first.</summary>
    [HttpGet("me")]
    public async Task<ActionResult<ActivityFeedPage>> Mine(
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 25,
        [FromQuery] int? skip = null,
        [FromQuery] int? take = null,
        CancellationToken ct = default)
    {
        Normalize(ref offset, ref limit, skip, take);
        var me = this.UserId();

        var myPacks = await db.Packs.Where(p => p.OwnerId == me).Select(p => p.Id).Take(MaxOwnedSubjects).ToListAsync(ct);
        var myMods = await db.Mods.Where(m => m.OwnerId == me).Select(m => m.Id).Take(MaxOwnedSubjects).ToListAsync(ct);
        var myWorlds = await db.SharedWorlds.Where(w => w.OwnerId == me).Select(w => w.Id).Take(MaxOwnedSubjects).ToListAsync(ct);
        var myResourcePacks = await db.HostedResourcePacks.Where(p => p.OwnerId == me).Select(p => p.Id).Take(MaxOwnedSubjects).ToListAsync(ct);
        var myBundles = await db.ContentBundles.Where(b => b.OwnerId == me).Select(b => b.Id).Take(MaxOwnedSubjects).ToListAsync(ct);
        // Teams I'm a member of, not just ones I own: team news concerns every member, and team rows are
        // what make shared packs reachable.
        var myTeams = await db.TeamMembers.Where(tm => tm.UserId == me).Select(tm => tm.TeamId).Take(MaxOwnedSubjects).ToListAsync(ct);

        // Each subject clause names its type as well as its id so Postgres can seek the
        // (SubjectType, SubjectId, CreatedAt) index instead of scanning on SubjectId alone.
        var query = db.ActivityEntries.Where(e =>
            e.ActorUserId == me
            || e.TargetUserId == me
            || (e.TargetTeamId != null && myTeams.Contains(e.TargetTeamId.Value))
            || (e.SubjectType == ActivitySubjectType.Pack && myPacks.Contains(e.SubjectId))
            || (e.SubjectType == ActivitySubjectType.Mod && myMods.Contains(e.SubjectId))
            || (e.SubjectType == ActivitySubjectType.World && myWorlds.Contains(e.SubjectId))
            || (e.SubjectType == ActivitySubjectType.ResourcePack && myResourcePacks.Contains(e.SubjectId))
            || (e.SubjectType == ActivitySubjectType.Bundle && myBundles.Contains(e.SubjectId))
            || (e.SubjectType == ActivitySubjectType.Team && myTeams.Contains(e.SubjectId)));

        var total = await query.CountAsync(ct);
        var items = await Project(query).Skip(offset).Take(limit).ToListAsync(ct);
        return Ok(new ActivityFeedPage(items, offset, limit, total));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>Accepts either paging spelling and clamps both.</summary>
    private static void Normalize(ref int offset, ref int limit, int? skip, int? take)
    {
        if (skip.HasValue) offset = skip.Value;
        if (take.HasValue) limit = take.Value;
        if (limit <= 0 || limit > 100) limit = 25;
        if (offset < 0) offset = 0;
    }

    /// <summary>
    /// Orders newest-first and projects straight to the DTO.
    /// </summary>
    /// <remarks>
    /// A projection instead of <c>Include</c>: the feed needs three names from those rows, and loading
    /// whole user entities makes every page far bigger.
    /// </remarks>
    private static IQueryable<ActivityFeedEntry> Project(IQueryable<ActivityEntry> query) =>
        query.OrderByDescending(e => e.CreatedAt)
             .Select(e => new ActivityFeedEntry(
                 e.Id, e.Kind, e.SubjectType, e.SubjectId, e.SubjectName,
                 e.ActorUserId, e.Actor.UserName,
                 e.TargetUserId, e.TargetUser != null ? e.TargetUser.UserName : null,
                 e.TargetTeamId, e.TargetTeam != null ? e.TargetTeam.Name : null,
                 e.Detail, e.CreatedAt));

    /// <summary>Whether the caller may read this subject's history at all.</summary>
    /// <remarks>
    /// False when the subject no longer exists. Entries outlive their subject (<c>SubjectId</c> has no
    /// foreign key), and otherwise anyone could read a deleted private pack's share history by guessing
    /// its id.
    /// </remarks>
    private async Task<bool> CanViewSubjectAsync(
        ActivitySubjectType type, Guid id, Guid me, CancellationToken ct)
    {
        switch (type)
        {
            case ActivitySubjectType.Pack:
            {
                var row = await db.Packs.FirstOrDefaultAsync(x => x.Id == id, ct);
                return row is not null && (await packs.GetAsync(row, me, ct)).HasFlag(PackPermissions.View);
            }
            case ActivitySubjectType.Mod:
            {
                var row = await db.Mods.FirstOrDefaultAsync(x => x.Id == id, ct);
                return row is not null && (await mods.GetAsync(row, me, ct)).HasFlag(PackPermissions.View);
            }
            case ActivitySubjectType.World:
            {
                var row = await db.SharedWorlds.FirstOrDefaultAsync(x => x.Id == id, ct);
                return row is not null && (await worlds.GetAsync(row, me, ct)).HasFlag(PackPermissions.View);
            }
            case ActivitySubjectType.ResourcePack:
            {
                var row = await db.HostedResourcePacks.FirstOrDefaultAsync(x => x.Id == id, ct);
                return row is not null && (await resourcePacks.GetAsync(row, me, ct)).HasFlag(PackPermissions.View);
            }
            case ActivitySubjectType.Bundle:
            {
                var row = await db.ContentBundles.FirstOrDefaultAsync(x => x.Id == id, ct);
                return row is not null && (await bundles.GetAsync(row, me, ct)).HasFlag(PackPermissions.View);
            }
            case ActivitySubjectType.Team:
                // Teams have no visibility flag or resolver; membership is the only rule.
                return await db.Teams.AnyAsync(
                    t => t.Id == id
                         && (t.OwnerId == me || db.TeamMembers.Any(tm => tm.TeamId == t.Id && tm.UserId == me)), ct);
            default:
                return false;
        }
    }
}
