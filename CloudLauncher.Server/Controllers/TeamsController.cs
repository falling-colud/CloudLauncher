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
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TeamSummary>>> List(CancellationToken ct)
    {
        var me = this.UserId();
        var teams = await db.Teams
            .Include(t => t.Owner)
            .Where(t => t.OwnerId == me || db.TeamMembers.Any(tm => tm.TeamId == t.Id && tm.UserId == me))
            .Select(t => new TeamSummary(t.Id, t.Name, t.OwnerId, t.Owner.UserName!, t.Members.Count))
            .ToListAsync(ct);
        return Ok(teams);
    }

    [HttpPost]
    public async Task<ActionResult<TeamSummary>> Create([FromBody] CreateTeamRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 64)
            return BadRequest(new { error = "Team name must be 1-64 characters" });

        if (await db.Teams.AnyAsync(t => t.Name == req.Name, ct))
            return BadRequest(new { error = "Team name already taken" });

        var me = this.UserId();
        var team = new Team { Name = req.Name.Trim(), OwnerId = me };
        team.Members.Add(new TeamMember { TeamId = team.Id, UserId = me });
        db.Teams.Add(team);
        await db.SaveChangesAsync(ct);
        await db.Entry(team).Reference(t => t.Owner).LoadAsync(ct);
        return Ok(new TeamSummary(team.Id, team.Name, team.OwnerId, team.Owner.UserName!, 1));
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
        return Ok(new TeamDetail(
            team.Id, team.Name, team.OwnerId, team.Owner.UserName!,
            team.Members.Select(m => new UserSummary(m.UserId, m.User.UserName ?? "", m.User.EmailConfirmed)).ToList()));
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

    [HttpDelete("{id:guid}/members/{userId:guid}")]
    public async Task<IActionResult> RemoveMember(Guid id, Guid userId, CancellationToken ct)
    {
        var me = this.UserId();
        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (team is null) return NotFound();
        if (team.OwnerId != me && me != userId) return Forbid();
        if (team.OwnerId == userId) return BadRequest(new { error = "Owner cannot leave; delete the team instead" });

        var row = await db.TeamMembers.FirstOrDefaultAsync(tm => tm.TeamId == id && tm.UserId == userId, ct);
        if (row is null) return NotFound();
        db.TeamMembers.Remove(row);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

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
}
