using CloudLauncher.Server.Data;
using CloudLauncher.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CloudLauncher.Server.Controllers;

[ApiController]
[Authorize]
[Route("teams")]
public class TeamsController(AppDbContext db) : ControllerBase
{
    private const int MaxTeamNameLength = 64;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TeamSummary>>> List(CancellationToken ct)
    {
        var me = this.UserId();
        var teams = await db.Teams
            .Include(t => t.Owner)
            .Where(t => t.OwnerId == me || db.TeamMembers.Any(tm => tm.TeamId == t.Id && tm.UserId == me))
            .Select(t => new TeamSummary(
                t.Id, t.Name, t.OwnerId, t.Owner.UserName!, t.Members.Count,
                db.PackTeams.Count(x => x.TeamId == t.Id),
                db.ModTeams.Count(x => x.TeamId == t.Id),
                db.SharedWorldTeams.Count(x => x.TeamId == t.Id),
                db.HostedResourcePackTeams.Count(x => x.TeamId == t.Id)))
            .ToListAsync(ct);
        return Ok(teams);
    }

    [HttpPost]
    public async Task<ActionResult<TeamSummary>> Create([FromBody] CreateTeamRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > MaxTeamNameLength)
            return BadRequest(new { error = "Team name must be 1-64 characters" });

        if (await db.Teams.AnyAsync(t => t.Name == req.Name, ct))
            return BadRequest(new { error = "Team name already taken" });

        var me = this.UserId();
        var team = new Team { Name = req.Name.Trim(), OwnerId = me };
        team.Members.Add(new TeamMember { TeamId = team.Id, UserId = me });
        db.Teams.Add(team);
        await db.SaveChangesAsync(ct);
        await db.Entry(team).Reference(t => t.Owner).LoadAsync(ct);
        // A brand-new team holds nothing yet, so every shared-content count is zero by construction.
        return Ok(new TeamSummary(team.Id, team.Name, team.OwnerId, team.Owner.UserName!, 1, 0, 0, 0, 0));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<TeamDetail>> Get(Guid id, CancellationToken ct)
    {
        var me = this.UserId();
        var team = await db.Teams
            .Include(t => t.Owner)
            .Include(t => t.Members).ThenInclude(m => m.User)
            .FirstOrDefaultAsync(t => t.Id == id, ct);
        if (team is null) return NotFound();
        if (team.OwnerId != me && !team.Members.Any(m => m.UserId == me)) return Forbid();

        var members = team.Members
            .OrderByDescending(m => m.UserId == team.OwnerId)
            .ThenBy(m => m.JoinedAt)
            .ToList();

        return Ok(new TeamDetail(
            team.Id, team.Name, team.OwnerId, team.Owner.UserName!,
            members.Select(m => new UserSummary(m.UserId, m.User.UserName ?? "", m.User.EmailConfirmed)).ToList(),
            team.CreatedAt,
            members.Select(m => new TeamMemberEntry(
                m.UserId, m.User.UserName ?? "", m.User.EmailConfirmed, m.JoinedAt,
                IsOwner: m.UserId == team.OwnerId)).ToList(),
            await db.PackTeams.CountAsync(x => x.TeamId == id, ct),
            await db.ModTeams.CountAsync(x => x.TeamId == id, ct),
            await db.SharedWorldTeams.CountAsync(x => x.TeamId == id, ct),
            await db.HostedResourcePackTeams.CountAsync(x => x.TeamId == id, ct)));
    }

    /// <summary>Renames a team. Owner only.</summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Rename(Guid id, [FromBody] RenameTeamRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > MaxTeamNameLength)
            return BadRequest(new { error = "Team name must be 1-64 characters" });

        var me = this.UserId();
        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (team is null) return NotFound();
        if (team.OwnerId != me) return Forbid();

        var name = req.Name.Trim();
        // Same uniqueness rule as Create, minus this team: renaming a team to the name it already
        // has (different casing, stray whitespace) must not fail as "already taken".
        if (await db.Teams.AnyAsync(t => t.Id != id && t.Name == name, ct))
            return BadRequest(new { error = "Team name already taken" });

        team.Name = name;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Hands the team to one of its existing members. Owner only.</summary>
    /// <remarks>
    /// The outgoing owner keeps their membership row, so a transfer never costs anybody their access
    /// to the team's shared content — it only moves who is allowed to administer it.
    /// </remarks>
    [HttpPost("{id:guid}/transfer")]
    public async Task<IActionResult> TransferOwnership(
        Guid id, [FromBody] TransferTeamOwnershipRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (team is null) return NotFound();
        if (team.OwnerId != me) return Forbid();
        if (req.UserId == team.OwnerId) return BadRequest(new { error = "That user already owns this team" });

        if (!await db.TeamMembers.AnyAsync(tm => tm.TeamId == id && tm.UserId == req.UserId, ct))
            return BadRequest(new { error = "The new owner must already be a member of the team" });

        team.OwnerId = req.UserId;
        // Create adds the owner as a member, but a team from an older build (or one whose owner was
        // removed by hand) may not have that row. Without it the outgoing owner would lose the team.
        if (!await db.TeamMembers.AnyAsync(tm => tm.TeamId == id && tm.UserId == me, ct))
            db.TeamMembers.Add(new TeamMember { TeamId = id, UserId = me });

        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/members")]
    public async Task<ActionResult<UserSummary>> AddMember(
        Guid id, [FromBody] AddTeamMemberRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (team is null) return NotFound();
        if (team.OwnerId != me) return Forbid();

        var user = await db.Users.FirstOrDefaultAsync(u => u.UserName == req.Username, ct);
        if (user is null) return BadRequest(new { error = "User not found" });

        if (!await db.TeamMembers.AnyAsync(tm => tm.TeamId == id && tm.UserId == user.Id, ct))
        {
            db.TeamMembers.Add(new TeamMember { TeamId = id, UserId = user.Id });
            await db.SaveChangesAsync(ct);
        }
        return Ok(new UserSummary(user.Id, user.UserName!, user.EmailConfirmed));
    }

    /// <summary>Leaves a team you are a member of. Not for the owner — they transfer or delete instead.</summary>
    /// <remarks>
    /// <see cref="RemoveMember"/> already allows self-removal, and this shares its implementation. It
    /// exists as its own route so a client does not have to prove it knows its own user id to leave,
    /// and so "I left" and "the owner removed me" are distinguishable calls rather than the same one.
    /// </remarks>
    [HttpDelete("{id:guid}/members/me")]
    public Task<IActionResult> LeaveTeam(Guid id, CancellationToken ct) =>
        RemoveMemberCoreAsync(id, this.UserId(), ct);

    [HttpDelete("{id:guid}/members/{userId:guid}")]
    public Task<IActionResult> RemoveMember(Guid id, Guid userId, CancellationToken ct) =>
        RemoveMemberCoreAsync(id, userId, ct);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var me = this.UserId();
        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (team is null) return NotFound();
        if (team.OwnerId != me) return Forbid();
        db.Teams.Remove(team);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>Drops one membership row. The owner may remove anybody; anybody may remove themselves.</summary>
    private async Task<IActionResult> RemoveMemberCoreAsync(Guid id, Guid userId, CancellationToken ct)
    {
        var me = this.UserId();
        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (team is null) return NotFound();
        if (team.OwnerId != me && me != userId) return Forbid();
        if (team.OwnerId == userId)
            return BadRequest(new { error = "Owner cannot leave; transfer ownership or delete the team instead" });

        var row = await db.TeamMembers.FirstOrDefaultAsync(tm => tm.TeamId == id && tm.UserId == userId, ct);
        if (row is null) return NotFound();
        db.TeamMembers.Remove(row);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}
