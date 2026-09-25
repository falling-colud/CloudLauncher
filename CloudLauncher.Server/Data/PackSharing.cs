using System.Security.Cryptography;
using CloudLauncher.Shared;
using Microsoft.EntityFrameworkCore;

namespace CloudLauncher.Server.Data;

/// <summary>
/// The pack side of sharing: share tokens, putting a grant into someone's library, the access
/// list every content type shows, and the activity line a share leaves behind.
/// </summary>
/// <remarks>
/// Lives here instead of in a controller because <c>TeamsController</c> calls into it too: a team
/// grant is stored on the pack (<see cref="PackTeam"/>) and reaches a library through the pack
/// (<see cref="PackListing"/>).
/// </remarks>
public static class PackSharing
{
    // ── share tokens ─────────────────────────────────────────────────────────

    /// <summary>Mints a url-safe token for a share link or an invitation.</summary>
    /// <remarks>
    /// 16 random bytes as unpadded base64url: 22 characters, which fits the <c>varchar(32)</c> token
    /// columns, and 128 bits of entropy. Url-safe so it survives being pasted into a chat and clicked.
    /// </remarks>
    public static string NewShareToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    // ── listings ─────────────────────────────────────────────────────────────

    /// <summary>Puts <paramref name="packId"/> in one person's library if it is not there already.</summary>
    /// <returns>True when a listing row was actually added.</returns>
    public static async Task<bool> EnsureListedAsync(
        AppDbContext db, Guid packId, Guid userId, CancellationToken ct = default)
    {
        // The owner sees the pack through ownership (GET /packs unions owned packs with listed ones),
        // so a row for them would be redundant, and deleting it later would look like evicting them.
        var ownerId = await db.Packs.Where(p => p.Id == packId).Select(p => p.OwnerId).FirstOrDefaultAsync(ct);
        if (ownerId == userId) return false;

        if (await db.PackListings.AnyAsync(l => l.PackId == packId && l.UserId == userId, ct))
            return false;

        db.PackListings.Add(new PackListing { PackId = packId, UserId = userId });
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Lists a pack for every current member of a team it has just been shared with.
    /// </summary>
    /// <remarks>
    /// Call after the <see cref="PackTeam"/> row is saved, so members find the pack in their library
    /// without having to look for it.
    /// </remarks>
    /// <returns>How many people the pack was newly listed for.</returns>
    public static async Task<int> AddListingsForTeamAsync(
        AppDbContext db, Guid packId, Guid teamId, CancellationToken ct = default)
    {
        var ownerId = await db.Packs.Where(p => p.Id == packId).Select(p => p.OwnerId).FirstOrDefaultAsync(ct);
        var memberIds = await db.TeamMembers
            .Where(tm => tm.TeamId == teamId && tm.UserId != ownerId)
            .Select(tm => tm.UserId)
            .ToListAsync(ct);
        if (memberIds.Count == 0) return 0;

        var already = await db.PackListings
            .Where(l => l.PackId == packId && memberIds.Contains(l.UserId))
            .Select(l => l.UserId)
            .ToListAsync(ct);

        var added = 0;
        foreach (var userId in memberIds.Distinct())
        {
            if (already.Contains(userId)) continue;
            db.PackListings.Add(new PackListing { PackId = packId, UserId = userId });
            added++;
        }
        if (added > 0) await db.SaveChangesAsync(ct);
        return added;
    }

    /// <summary>
    /// Lists every pack shared with <paramref name="teamId"/> for somebody who has just joined it.
    /// </summary>
    /// <remarks>
    /// Counterpart of <see cref="AddListingsForTeamAsync"/> for a person joining a team that already
    /// has packs. Call from the teams controller after the <see cref="TeamMember"/> row is saved.
    /// </remarks>
    /// <returns>How many packs were newly listed for this user.</returns>
    public static async Task<int> AddListingsForNewMemberAsync(
        AppDbContext db, Guid teamId, Guid userId, CancellationToken ct = default)
    {
        var packIds = await db.PackTeams
            .Where(pt => pt.TeamId == teamId && pt.Pack.OwnerId != userId)
            .Select(pt => pt.PackId)
            .ToListAsync(ct);
        if (packIds.Count == 0) return 0;

        var already = await db.PackListings
            .Where(l => l.UserId == userId && packIds.Contains(l.PackId))
            .Select(l => l.PackId)
            .ToListAsync(ct);

        var added = 0;
        foreach (var packId in packIds.Distinct())
        {
            if (already.Contains(packId)) continue;
            db.PackListings.Add(new PackListing { PackId = packId, UserId = userId });
            added++;
        }
        if (added > 0) await db.SaveChangesAsync(ct);
        return added;
    }

    /// <summary>Cleans up the listings a team grant on one pack was holding up.</summary>
    /// <remarks>Call after the <see cref="PackTeam"/> row is removed and saved.</remarks>
    public static Task<int> RemoveTeamListingsAsync(
        AppDbContext db, Guid packId, Guid teamId, CancellationToken ct = default) =>
        RemoveOrphanedListingsAsync(db, new[] { packId }, userIds: null, teamId, ct);

    /// <summary>Cleans up the listings one person's membership of a team was holding up.</summary>
    /// <remarks>
    /// Call from the teams controller after the <see cref="TeamMember"/> row is removed and saved.
    /// Only packs that were in the library because of the team are removed.
    /// </remarks>
    public static Task<int> RemoveListingsForDepartedMemberAsync(
        AppDbContext db, Guid teamId, Guid userId, CancellationToken ct = default) =>
        RemoveOrphanedListingsAsync(db, packIds: null, new[] { userId }, teamId, ct);

    /// <summary>
    /// Drops listing rows that no longer stand on their own once a team grant is gone.
    /// </summary>
    /// <remarks>
    /// A listing is removed only when the person has no other way into the pack: not the owner, no
    /// collaborator row, no other team, and the pack isn't public. Otherwise the entry stays, since
    /// what is in a library is its owner's choice. The table doesn't record where a row came from, so
    /// this errs towards keeping rows.
    /// </remarks>
    /// <param name="exceptTeamId">The team whose grant is being withdrawn. Ignored when looking for
    /// other routes in, so this works whether it is called before or after that row is deleted.</param>
    private static async Task<int> RemoveOrphanedListingsAsync(
        AppDbContext db,
        IReadOnlyCollection<Guid>? packIds,
        IReadOnlyCollection<Guid>? userIds,
        Guid exceptTeamId,
        CancellationToken ct)
    {
        var query = db.PackListings.AsQueryable();
        if (packIds is not null)
        {
            var ids = packIds.ToList();
            query = query.Where(l => ids.Contains(l.PackId));
        }
        else
        {
            // Not named by pack: every pack the withdrawn team holds is a candidate.
            query = query.Where(l => db.PackTeams.Any(pt => pt.TeamId == exceptTeamId && pt.PackId == l.PackId));
        }
        if (userIds is not null)
        {
            var ids = userIds.ToList();
            query = query.Where(l => ids.Contains(l.UserId));
        }
        else
        {
            // Not named by user: everybody still on the team the grant is being taken from.
            query = query.Where(l => db.TeamMembers.Any(tm => tm.TeamId == exceptTeamId && tm.UserId == l.UserId));
        }

        var doomed = await query
            .Where(l => l.Pack.OwnerId != l.UserId)
            .Where(l => l.Pack.Visibility != PackVisibility.Public)
            .Where(l => !db.PackCollaborators.Any(c => c.PackId == l.PackId && c.UserId == l.UserId))
            .Where(l => !db.PackTeams.Any(pt => pt.PackId == l.PackId
                                             && pt.TeamId != exceptTeamId
                                             && db.TeamMembers.Any(tm => tm.TeamId == pt.TeamId && tm.UserId == l.UserId)))
            .ToListAsync(ct);

        if (doomed.Count == 0) return 0;
        db.PackListings.RemoveRange(doomed);
        await db.SaveChangesAsync(ct);
        return doomed.Count;
    }

    // ── access lists ─────────────────────────────────────────────────────────

    /// <summary>How many of a team's members an access list carries inline.</summary>
    /// <remarks>
    /// Enough to show who is in it without dumping every team's roster. The count stays exact; only
    /// the names are cut off.
    /// </remarks>
    public const int TeamMemberPreviewCount = 8;

    /// <summary>Builds the team rows of an access list, each with a few of its members.</summary>
    /// <remarks>
    /// One query for every team on the item, and it works whether or not the caller is in those teams.
    /// Shared by instances, mods, worlds, resource packs and bundles. Callers pass the grant rows with
    /// their <c>Team</c> navigation loaded.
    /// </remarks>
    public static async Task<List<PackTeamEntry>> TeamEntriesAsync(
        AppDbContext db,
        IEnumerable<(Guid TeamId, string TeamName, PackPermissions Permissions)> grants,
        CancellationToken ct = default)
    {
        var rows = grants.ToList();
        var teamIds = rows.Select(t => t.TeamId).Distinct().ToList();
        if (teamIds.Count == 0) return new List<PackTeamEntry>();

        var members = await db.TeamMembers
            .AsNoTracking()
            .Where(tm => teamIds.Contains(tm.TeamId))
            .OrderBy(tm => tm.JoinedAt)
            .Select(tm => new { tm.TeamId, Username = tm.User.UserName })
            .ToListAsync(ct);

        var byTeam = members
            .GroupBy(m => m.TeamId)
            .ToDictionary(g => g.Key, g => g.ToList());

        return rows.Select(t =>
        {
            var roster = byTeam.GetValueOrDefault(t.TeamId) ?? new();
            return new PackTeamEntry(
                t.TeamId,
                t.TeamName,
                t.Permissions,
                roster.Count,
                roster.Take(TeamMemberPreviewCount).Select(m => m.Username ?? "").ToList());
        }).ToList();
    }

    // ── activity ─────────────────────────────────────────────────────────────

    /// <summary>Appends one line to the activity feed. Never throws.</summary>
    /// <remarks>
    /// The share has already been saved when this runs, so a failed log write must not turn it into a
    /// 500. The insert gets its own save and catch, and the entry is detached on failure so it can't
    /// break a later save on the same context.
    /// </remarks>
    public static async Task LogAsync(
        AppDbContext db,
        Guid actorUserId,
        ActivityKind kind,
        ActivitySubjectType subjectType,
        Guid subjectId,
        string subjectName,
        Guid? targetUserId = null,
        Guid? targetTeamId = null,
        string? detail = null,
        CancellationToken ct = default)
    {
        var entry = new ActivityEntry
        {
            ActorUserId = actorUserId,
            Kind = kind,
            SubjectType = subjectType,
            SubjectId = subjectId,
            SubjectName = Clamp(subjectName, 128) ?? "",
            TargetUserId = targetUserId,
            TargetTeamId = targetTeamId,
            Detail = Clamp(detail, 256)
        };
        try
        {
            db.ActivityEntries.Add(entry);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception)
        {
            db.Entry(entry).State = EntityState.Detached;
        }
    }

    /// <summary>Truncates a denormalised string to fit its column instead of failing the insert.</summary>
    private static string? Clamp(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
