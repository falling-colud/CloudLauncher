using System.Security.Cryptography;
using CloudLauncher.Server.Data;
using CloudLauncher.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace CloudLauncher.Server.Controllers;

[ApiController]
[Authorize]
[Route("teams")]
public class TeamsController(AppDbContext db) : ControllerBase
{
    private const int MaxTeamNameLength = 64;
    private const int MaxInviteMessageLength = 512;   // matches TeamInvitations.Message
    private const int MaxUsernameLength = 256;        // matches TeamInvitations.InvitedUsername
    private const int MaxExpiryDays = 365;

    /// <summary>How many invitations one account may create per hour, across every team it
    /// manages.</summary>
    /// <remarks>Inviting by name reveals whether a username exists. The per-IP rate limit is the first
    /// defence; this per-account cap covers probers with several addresses.</remarks>
    private const int MaxInvitesPerHour = 30;

    // ── teams ────────────────────────────────────────────────────────────────

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
                db.HostedResourcePackTeams.Count(x => x.TeamId == t.Id),
                db.ContentBundleTeams.Count(x => x.TeamId == t.Id),
                // Ownership is Team.OwnerId; the role column only covers roles below it, so the owner's
                // role never depends on their membership row.
                t.OwnerId == me
                    ? TeamRole.Owner
                    : db.TeamMembers.Where(tm => tm.TeamId == t.Id && tm.UserId == me)
                        .Select(tm => (TeamRole?)tm.Role).FirstOrDefault() ?? TeamRole.Member))
            .ToListAsync(ct);
        return Ok(teams);
    }

    [HttpPost]
    public async Task<ActionResult<TeamSummary>> Create([FromBody] CreateTeamRequest req, CancellationToken ct)
    {
        // Trim before validating and checking for duplicates, or " Friends" would pass the duplicate
        // check and then fail on the unique index with a 500.
        var name = (req.Name ?? string.Empty).Trim();
        if (name.Length == 0 || name.Length > MaxTeamNameLength)
            return BadRequest(new { error = "Team name must be 1-64 characters" });

        var me = this.UserId();
        // Names are unique per owner, not globally. The message reveals nothing about other people's
        // teams.
        if (await db.Teams.AnyAsync(t => t.OwnerId == me && t.Name == name, ct))
            return BadRequest(new { error = "You already have a team with that name" });

        var team = new Team { Name = name, OwnerId = me };
        team.Members.Add(new TeamMember { TeamId = team.Id, UserId = me, Role = TeamRole.Owner });
        db.Teams.Add(team);
        await db.SaveChangesAsync(ct);
        await db.Entry(team).Reference(t => t.Owner).LoadAsync(ct);
        // A brand-new team holds nothing yet, so every shared-content count is zero by construction.
        return Ok(new TeamSummary(team.Id, team.Name, team.OwnerId, team.Owner.UserName!, 1, 0, 0, 0, 0, 0,
            TeamRole.Owner));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<TeamDetail>> Get(Guid id, CancellationToken ct)
    {
        var me = this.UserId();
        var team = await db.Teams
            .Include(t => t.Owner)
            .Include(t => t.Members).ThenInclude(m => m.User)
            .FirstOrDefaultAsync(t => t.Id == id, ct);
        if (team is null) return NotFound(new { error = "That team does not exist." });

        var role = EffectiveRole(team, team.Members.FirstOrDefault(m => m.UserId == me)?.Role);
        if (role is null) return Denied("You are not a member of this team.");

        var members = team.Members
            .OrderByDescending(m => m.UserId == team.OwnerId)
            .ThenBy(m => m.JoinedAt)
            .ToList();

        // TeamDetail.Members is the legacy shape, kept for older clients; MemberDetails is what the UI
        // renders. Both come from this one list so they always agree.
        var memberDetails = members.Select(m => new TeamMemberEntry(
            m.UserId, m.User.UserName ?? "", m.User.EmailConfirmed, m.JoinedAt,
            IsOwner: m.UserId == team.OwnerId,
            Role: m.UserId == team.OwnerId ? TeamRole.Owner : m.Role)).ToList();

        // Pending invitations (and their tokens) only go to people who can manage the team. A token
        // grants entry, so plain members don't get them.
        var canManage = role is TeamRole.Owner or TeamRole.Admin;
        var pending = canManage ? await PendingInvitationsForTeamAsync(id, ct) : null;

        return Ok(new TeamDetail(
            team.Id, team.Name, team.OwnerId, team.Owner.UserName!,
            members.Select(m => new UserSummary(m.UserId, m.User.UserName ?? "", m.User.EmailConfirmed)).ToList(),
            team.CreatedAt,
            memberDetails,
            await db.PackTeams.CountAsync(x => x.TeamId == id, ct),
            await db.ModTeams.CountAsync(x => x.TeamId == id, ct),
            await db.SharedWorldTeams.CountAsync(x => x.TeamId == id, ct),
            await db.HostedResourcePackTeams.CountAsync(x => x.TeamId == id, ct),
            await db.ContentBundleTeams.CountAsync(x => x.TeamId == id, ct),
            role.Value,
            pending));
    }

    /// <summary>Renames a team. Owner only.</summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Rename(Guid id, [FromBody] RenameTeamRequest req, CancellationToken ct)
    {
        var name = (req.Name ?? string.Empty).Trim();
        if (name.Length == 0 || name.Length > MaxTeamNameLength)
            return BadRequest(new { error = "Team name must be 1-64 characters" });

        var (team, role) = await LoadWithRoleAsync(id, ct);
        if (team is null) return NotFound(new { error = "That team does not exist." });
        if (role is null) return Denied("You are not a member of this team.");
        if (role != TeamRole.Owner) return Denied("Only the team's owner can rename it.");

        // Same uniqueness rule as Create, excluding this team so renaming to its own name (only
        // whitespace changed) doesn't fail. Scoped to the owner, like the unique index.
        if (await db.Teams.AnyAsync(t => t.Id != id && t.OwnerId == team.OwnerId && t.Name == name, ct))
            return BadRequest(new { error = "You already have a team with that name" });

        team.Name = name;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Hands the team to one of its existing members. Owner only.</summary>
    /// <remarks>The outgoing owner stays on the team as an Admin, so they keep access to its shared
    /// content. The new owner can demote them.</remarks>
    [HttpPost("{id:guid}/transfer")]
    public async Task<IActionResult> TransferOwnership(
        Guid id, [FromBody] TransferTeamOwnershipRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var (team, role) = await LoadWithRoleAsync(id, ct);
        if (team is null) return NotFound(new { error = "That team does not exist." });
        if (role is null) return Denied("You are not a member of this team.");
        if (role != TeamRole.Owner) return Denied("Only the team's owner can hand the team over.");
        if (req.UserId == team.OwnerId) return BadRequest(new { error = "That user already owns this team" });

        var incoming = await db.TeamMembers.FirstOrDefaultAsync(tm => tm.TeamId == id && tm.UserId == req.UserId, ct);
        if (incoming is null)
            return BadRequest(new { error = "The new owner must already be a member of the team" });

        team.OwnerId = req.UserId;
        // Team.OwnerId and TeamMember.Role must agree, or the new owner would show as a Member in
        // member lists.
        incoming.Role = TeamRole.Owner;

        // Teams created by older builds (or edited by hand) may lack the owner's membership row;
        // without one the outgoing owner would lose the team.
        var outgoing = await db.TeamMembers.FirstOrDefaultAsync(tm => tm.TeamId == id && tm.UserId == me, ct);
        if (outgoing is null) db.TeamMembers.Add(new TeamMember { TeamId = id, UserId = me, Role = TeamRole.Admin });
        else outgoing.Role = TeamRole.Admin;

        TeamActivityLog.Add(db, me, ActivityKind.OwnershipTransferred, team, req.UserId);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var (team, role) = await LoadWithRoleAsync(id, ct);
        if (team is null) return NotFound(new { error = "That team does not exist." });
        if (role is null) return Denied("You are not a member of this team.");
        if (role != TeamRole.Owner) return Denied("Only the team's owner can delete it.");

        db.Teams.Remove(team);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── what the team holds ──────────────────────────────────────────────────

    /// <summary>Everything shared with this team. Any member may read it.</summary>
    /// <remarks>Revoking is not here: each content type has its own
    /// <c>DELETE /{family}/{id}/teams/{teamId}</c> route, and duplicating those checks would let them
    /// drift apart.</remarks>
    [HttpGet("{id:guid}/shared")]
    public async Task<ActionResult<TeamSharedContent>> Shared(Guid id, CancellationToken ct)
    {
        var me = this.UserId();
        var (team, role) = await LoadWithRoleAsync(id, ct);
        if (team is null) return NotFound(new { error = "That team does not exist." });
        if (role is null) return Denied("You are not a member of this team.");

        var items = new List<TeamSharedItem>();

        // CanRevoke mirrors what each type's delete route accepts, which is only the resource's
        // owner. Showing it to team admins would just give them a button that 403s.
        items.AddRange(await db.PackTeams.Where(x => x.TeamId == id)
            .Select(x => new TeamSharedItem(
                TeamSharedKind.Pack, x.Pack.Id, x.Pack.Name, null, x.Pack.Summary,
                x.Pack.OwnerId, x.Pack.Owner.UserName!, null, x.Permissions, x.Pack.UpdatedAt,
                null, x.Pack.OwnerId == me))
            .ToListAsync(ct));

        items.AddRange(await db.ModTeams.Where(x => x.TeamId == id)
            .Select(x => new TeamSharedItem(
                TeamSharedKind.Mod, x.Mod.Id, x.Mod.Name, x.Mod.Slug, x.Mod.Summary,
                x.Mod.OwnerId, x.Mod.Owner.UserName!, x.Mod.IconBlobHash, x.Permissions, x.Mod.UpdatedAt,
                null, x.Mod.OwnerId == me))
            .ToListAsync(ct));

        items.AddRange(await db.SharedWorldTeams.Where(x => x.TeamId == id)
            .Select(x => new TeamSharedItem(
                TeamSharedKind.World, x.World.Id, x.World.Name, x.World.Slug, x.World.Summary,
                x.World.OwnerId, x.World.Owner.UserName!, x.World.IconBlobHash, x.Permissions, x.World.UpdatedAt,
                null, x.World.OwnerId == me))
            .ToListAsync(ct));

        items.AddRange(await db.HostedResourcePackTeams.Where(x => x.TeamId == id)
            .Select(x => new TeamSharedItem(
                TeamSharedKind.ResourcePack, x.ResourcePack.Id, x.ResourcePack.Name, x.ResourcePack.Slug,
                x.ResourcePack.Summary, x.ResourcePack.OwnerId, x.ResourcePack.Owner.UserName!,
                x.ResourcePack.IconBlobHash, x.Permissions, x.ResourcePack.UpdatedAt,
                null, x.ResourcePack.OwnerId == me))
            .ToListAsync(ct));

        items.AddRange(await db.ContentBundleTeams.Where(x => x.TeamId == id)
            .Select(x => new TeamSharedItem(
                TeamSharedKind.Bundle, x.Bundle.Id, x.Bundle.Name, x.Bundle.Slug, x.Bundle.Summary,
                x.Bundle.OwnerId, x.Bundle.Owner.UserName!, x.Bundle.IconBlobHash, x.Permissions,
                x.Bundle.UpdatedAt, x.Bundle.Kind, x.Bundle.OwnerId == me))
            .ToListAsync(ct));

        return Ok(new TeamSharedContent(
            team.Id, team.Name, role.Value,
            items.OrderByDescending(i => i.UpdatedAt).ThenBy(i => i.Name).ToList()));
    }

    // ── members ──────────────────────────────────────────────────────────────

    /// <summary>Deprecated. Kept for older launchers, but it sends an invitation instead of adding
    /// someone outright.</summary>
    /// <remarks>Nobody should be added to a team without agreeing to it.
    /// <c>POST /teams/{id}/invitations</c> is the full route. This still returns the invitee's
    /// <see cref="UserSummary"/> so old clients can parse the reply, but the person only shows up in
    /// the member list once they accept.</remarks>
    [HttpPost("{id:guid}/members")]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<ActionResult<UserSummary>> AddMember(
        Guid id, [FromBody] AddTeamMemberRequest req, CancellationToken ct)
    {
        // A blank username means "create a link" on the invitation route, but this route can't return
        // a token, so reject it.
        if (string.IsNullOrWhiteSpace(req.Username))
            return BadRequest(new { error = "Say who to invite" });

        var (failure, _, invitee) = await TryCreateInvitationAsync(
            id, new CreateTeamInvitationRequest(req.Username, TeamRole.Member), ct);
        if (failure is not null) return failure;
        // invitee is non-null here: a request naming a username either resolves it or fails above.
        return Ok(new UserSummary(invitee!.Id, invitee.UserName!, invitee.EmailConfirmed));
    }

    /// <summary>Changes what an existing member may do. Owner only.</summary>
    [HttpPut("{id:guid}/members/{userId:guid}/role")]
    public async Task<IActionResult> SetMemberRole(
        Guid id, Guid userId, [FromBody] UpdateTeamMemberRoleRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var (team, role) = await LoadWithRoleAsync(id, ct);
        if (team is null) return NotFound(new { error = "That team does not exist." });
        if (role is null) return Denied("You are not a member of this team.");
        if (role != TeamRole.Owner) return Denied("Only the team's owner can change what a member may do.");

        // Enums arrive as numbers, so reject undefined values before they reach the database.
        if (!Enum.IsDefined(req.Role))
            return BadRequest(new { error = "That is not a role" });
        // Owner can't be assigned: it is Team.OwnerId, and moving it is a transfer.
        if (req.Role == TeamRole.Owner)
            return BadRequest(new { error = "Hand the team over with POST /teams/{id}/transfer instead" });
        if (userId == team.OwnerId)
            return BadRequest(new { error = "The owner's role follows ownership; transfer the team to change it" });

        var row = await db.TeamMembers.FirstOrDefaultAsync(tm => tm.TeamId == id && tm.UserId == userId, ct);
        if (row is null) return NotFound(new { error = "That person is not on this team." });
        if (row.Role == req.Role) return NoContent();

        var was = row.Role;
        row.Role = req.Role;
        TeamActivityLog.Add(db, me, ActivityKind.RoleChanged, team, userId, $"{was} to {req.Role}");
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Leaves a team you are a member of. The owner transfers or deletes instead.</summary>
    /// <remarks>Shares <see cref="RemoveMember"/>'s implementation, but the client doesn't need to know
    /// its own user id, and leaving is distinguishable from being removed.</remarks>
    [HttpDelete("{id:guid}/members/me")]
    public Task<IActionResult> LeaveTeam(Guid id, CancellationToken ct) =>
        RemoveMemberCoreAsync(id, this.UserId(), ct);

    [HttpDelete("{id:guid}/members/{userId:guid}")]
    public Task<IActionResult> RemoveMember(Guid id, Guid userId, CancellationToken ct) =>
        RemoveMemberCoreAsync(id, userId, ct);

    // ── invitations to this team ─────────────────────────────────────────────

    /// <summary>Invites somebody to the team by name, or mints a join link. Owner or Admin.</summary>
    /// <remarks>Admins may only invite at <see cref="TeamRole.Member"/>, since promoting is the owner's
    /// call (<see cref="SetMemberRole"/>).</remarks>
    [HttpPost("{id:guid}/invitations")]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<ActionResult<TeamInvitationEntry>> CreateInvitation(
        Guid id, [FromBody] CreateTeamInvitationRequest req, CancellationToken ct)
    {
        var (failure, entry, _) = await TryCreateInvitationAsync(id, req, ct);
        return failure ?? Ok(entry);
    }

    /// <summary>Every invitation this team has issued: pending, accepted and revoked. Owner or
    /// Admin.</summary>
    [HttpGet("{id:guid}/invitations")]
    public async Task<ActionResult<IReadOnlyList<TeamInvitationEntry>>> ListInvitations(
        Guid id, CancellationToken ct)
    {
        var (team, role) = await LoadWithRoleAsync(id, ct);
        if (team is null) return NotFound(new { error = "That team does not exist." });
        if (role is null) return Denied("You are not a member of this team.");
        if (role is not (TeamRole.Owner or TeamRole.Admin))
            return Denied("Only the team's owner and admins can see its invitations.");

        var rows = await db.TeamInvitations
            .Include(i => i.Team)
            .Include(i => i.InvitedBy)
            .Where(i => i.TeamId == id)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(ct);
        return Ok(rows.Select(i => TeamInvitations.ToEntry(i, includeToken: true)).ToList());
    }

    /// <summary>Withdraws an invitation. Owner or Admin.</summary>
    [HttpDelete("{id:guid}/invitations/{invId:guid}")]
    public async Task<IActionResult> RevokeInvitation(Guid id, Guid invId, CancellationToken ct)
    {
        var (team, role) = await LoadWithRoleAsync(id, ct);
        if (team is null) return NotFound(new { error = "That team does not exist." });
        if (role is null) return Denied("You are not a member of this team.");
        if (role is not (TeamRole.Owner or TeamRole.Admin))
            return Denied("Only the team's owner and admins can withdraw invitations.");

        var inv = await db.TeamInvitations.FirstOrDefaultAsync(i => i.Id == invId && i.TeamId == id, ct);
        if (inv is null) return NotFound(new { error = "That invitation does not exist." });
        if (inv.AcceptedAt is not null)
            return BadRequest(new { error = "That invitation was already accepted; remove the member instead" });

        // Already revoked is the state the caller asked for, so it is a success, not a conflict.
        if (inv.RevokedAt is null)
        {
            inv.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return NoContent();
    }

    // ── invitations addressed to me ──────────────────────────────────────────

    /// <summary>The team invitations waiting for the signed-in user's answer.</summary>
    /// <remarks>The combined <c>GET /invitations</c> list (packs, teams, bundles) lives in another
    /// controller and fills its team part from <see cref="TeamInvitations.PendingForUserAsync"/>, as
    /// this route does. This one keeps working whether or not that route is deployed.</remarks>
    [HttpGet("invitations")]
    public async Task<ActionResult<IReadOnlyList<TeamInvitationEntry>>> MyTeamInvitations(CancellationToken ct) =>
        Ok(await TeamInvitations.PendingForUserAsync(db, this.UserId(), ct));

    /// <summary>What accepting this token would get you, without accepting it.</summary>
    [HttpGet("invitations/{token}")]
    public async Task<ActionResult<TeamInvitationEntry>> PreviewInvitation(string token, CancellationToken ct)
    {
        var entry = await TeamInvitations.PreviewAsync(db, token, ct);
        return entry is null
            ? NotFound(new { error = "That invitation link is not valid." })
            : Ok(entry);
    }

    /// <summary>Accepts a team invitation. Idempotent.</summary>
    [HttpPost("invitations/{token}/accept")]
    public async Task<ActionResult<TeamInvitationEntry>> AcceptInvitation(string token, CancellationToken ct) =>
        Result(await TeamInvitations.AcceptAsync(db, token, this.UserId(), ct));

    /// <summary>Declines a team invitation. Idempotent.</summary>
    [HttpPost("invitations/{token}/decline")]
    public async Task<ActionResult<TeamInvitationEntry>> DeclineInvitation(string token, CancellationToken ct) =>
        Result(await TeamInvitations.DeclineAsync(db, token, this.UserId(), ct));

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>A refusal that says why.</summary>
    /// <remarks><c>Forbid()</c> returns a 403 with an empty body, which the launcher shows as a bare
    /// "403 Forbidden". This uses the same <c>{ error }</c> shape as a 400.</remarks>
    private ObjectResult Denied(string why) =>
        StatusCode(StatusCodes.Status403Forbidden, new { error = why });

    private ObjectResult Throttled(string why) =>
        StatusCode(StatusCodes.Status429TooManyRequests, new { error = why });

    private ActionResult<TeamInvitationEntry> Result(TeamInvitationOutcome outcome) =>
        outcome.Entry is not null && outcome.Status == StatusCodes.Status200OK
            ? Ok(outcome.Entry)
            : StatusCode(outcome.Status, new { error = outcome.Error });

    /// <summary>The team plus what the caller may do on it, or a null role when they are not on it.</summary>
    private async Task<(Team? Team, TeamRole? Role)> LoadWithRoleAsync(Guid id, CancellationToken ct)
    {
        var team = await db.Teams.Include(t => t.Owner).FirstOrDefaultAsync(t => t.Id == id, ct);
        if (team is null) return (null, null);
        var me = this.UserId();
        var stored = await db.TeamMembers
            .Where(tm => tm.TeamId == id && tm.UserId == me)
            .Select(tm => (TeamRole?)tm.Role)
            .FirstOrDefaultAsync(ct);
        return (team, EffectiveRole(team, stored));
    }

    /// <summary>Resolves a stored role against the team's actual owner.</summary>
    /// <remarks><see cref="Team.OwnerId"/> wins both ways: the owner is Owner even without a membership
    /// row, and a stale row reading Owner is treated as Admin.</remarks>
    private TeamRole? EffectiveRole(Team team, TeamRole? stored)
    {
        var me = this.UserId();
        if (team.OwnerId == me) return TeamRole.Owner;
        if (stored is null) return null;
        return stored == TeamRole.Owner ? TeamRole.Admin : stored;
    }

    /// <summary>Drops one membership row.</summary>
    /// <remarks>Anyone may remove themselves, the owner may remove anyone, and an admin may remove only
    /// plain members (otherwise admins could empty the team one by one).</remarks>
    private async Task<IActionResult> RemoveMemberCoreAsync(Guid id, Guid userId, CancellationToken ct)
    {
        var me = this.UserId();
        var (team, role) = await LoadWithRoleAsync(id, ct);
        if (team is null) return NotFound(new { error = "That team does not exist." });
        if (role is null) return Denied("You are not a member of this team.");

        if (team.OwnerId == userId)
            return BadRequest(new
            {
                error = userId == me
                    ? "You own this team, so you cannot leave it. Hand it to another member with a transfer first, or delete the team."
                    : "The owner cannot be removed. Ask them to hand the team over, or to delete it."
            });

        var row = await db.TeamMembers.FirstOrDefaultAsync(tm => tm.TeamId == id && tm.UserId == userId, ct);
        if (row is null) return NotFound(new { error = "That person is not on this team." });

        if (userId != me)
        {
            if (role == TeamRole.Member)
                return Denied("Only the team's owner and admins can remove members.");
            if (role == TeamRole.Admin && row.Role != TeamRole.Member)
                return Denied("Admins can only remove plain members. The team's owner can remove an admin.");
        }

        db.TeamMembers.Remove(row);
        TeamActivityLog.Add(db, me, ActivityKind.MemberRemoved, team, userId,
            userId == me ? "left" : null);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Everything still waiting for an answer on this team.</summary>
    private async Task<IReadOnlyList<TeamInvitationEntry>> PendingInvitationsForTeamAsync(
        Guid id, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = await db.TeamInvitations
            .Include(i => i.Team)
            .Include(i => i.InvitedBy)
            .Where(i => i.TeamId == id && i.AcceptedAt == null && i.RevokedAt == null
                        && (i.ExpiresAt == null || i.ExpiresAt > now))
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(ct);
        return rows.Select(i => TeamInvitations.ToEntry(i, includeToken: true)).ToList();
    }

    /// <summary>The one path that mints a team invitation, shared by the invitation route and the
    /// deprecated add-member one.</summary>
    /// <returns>Either a failure to return as-is, or the new entry and the account it names.</returns>
    private async Task<(ActionResult? Failure, TeamInvitationEntry? Entry, AppUser? Invitee)>
        TryCreateInvitationAsync(Guid id, CreateTeamInvitationRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var (team, role) = await LoadWithRoleAsync(id, ct);
        if (team is null) return (NotFound(new { error = "That team does not exist." }), null, null);
        if (role is null) return (Denied("You are not a member of this team."), null, null);
        if (role is not (TeamRole.Owner or TeamRole.Admin))
            return (Denied("Only the team's owner and admins can invite people."), null, null);

        // Roles arrive as numbers; an undefined one would be stored and never match anything.
        if (!Enum.IsDefined(req.Role))
            return (BadRequest(new { error = "That is not a role" }), null, null);
        if (req.Role == TeamRole.Owner)
            return (BadRequest(new { error = "Ownership is handed over with a transfer, not an invitation" }),
                null, null);
        if (role == TeamRole.Admin && req.Role != TeamRole.Member)
            return (Denied("Admins can only invite plain members. Ask the owner to invite an admin."), null, null);

        if (req.Message is { Length: > MaxInviteMessageLength })
            return (BadRequest(new { error = $"The message has to be {MaxInviteMessageLength} characters or fewer" }),
                null, null);
        if (req.ExpiresInDays is { } days && (days < 1 || days > MaxExpiryDays))
            return (BadRequest(new { error = $"An invitation can expire between 1 and {MaxExpiryDays} days from now" }),
                null, null);

        var since = DateTimeOffset.UtcNow.AddHours(-1);
        if (await db.TeamInvitations.CountAsync(i => i.InvitedByUserId == me && i.CreatedAt > since, ct)
            >= MaxInvitesPerHour)
            return (Throttled($"You have sent {MaxInvitesPerHour} invitations in the last hour. Try again later."),
                null, null);

        AppUser? invitee = null;
        var username = req.Username?.Trim();
        if (!string.IsNullOrEmpty(username))
        {
            if (username.Length > MaxUsernameLength)
                return (BadRequest(new { error = "No account has a username that long" }), null, null);

            // Match on the normalized column, which is indexed and case-insensitive.
            var normalized = username.ToUpperInvariant();
            var found = await db.Users.FirstOrDefaultAsync(u => u.NormalizedUserName == normalized, ct);
            if (found is null)
                return (BadRequest(new { error = $"No account called \"{username}\"" }), null, null);
            AppUser user = found;
            invitee = user;

            if (user.Id == team.OwnerId
                || await db.TeamMembers.AnyAsync(tm => tm.TeamId == id && tm.UserId == user.Id, ct))
                return (BadRequest(new { error = $"{user.UserName} is already on this team" }), null, null);

            // Inviting the same person again reuses the pending invitation, so there are never two
            // live tokens, and the caller still gets a token back.
            var now = DateTimeOffset.UtcNow;
            var existing = await db.TeamInvitations
                .Include(i => i.Team)
                .Include(i => i.InvitedBy)
                .FirstOrDefaultAsync(i => i.TeamId == id && i.InvitedUserId == user.Id
                                          && i.AcceptedAt == null && i.RevokedAt == null
                                          && (i.ExpiresAt == null || i.ExpiresAt > now), ct);
            if (existing is not null)
                return (null, TeamInvitations.ToEntry(existing, includeToken: true), user);
        }

        var inv = new TeamInvitation
        {
            TeamId = id,
            InvitedUserId = invitee?.Id,
            InvitedUsername = invitee?.UserName,
            InvitedByUserId = me,
            Role = req.Role,
            Token = NewInviteToken(),
            Message = req.Message,
            ExpiresAt = req.ExpiresInDays is { } d ? DateTimeOffset.UtcNow.AddDays(d) : null
        };
        db.TeamInvitations.Add(inv);
        TeamActivityLog.Add(db, me, ActivityKind.InviteSent, team, invitee?.Id,
            invitee is null ? $"link, {req.Role}" : req.Role.ToString());
        await db.SaveChangesAsync(ct);

        var inviter = await db.Users.FirstOrDefaultAsync(u => u.Id == me, ct);
        var entry = TeamInvitations.ToEntry(inv, team.Name, inviter?.UserName ?? "", includeToken: true);
        return (null, entry, invitee);
    }

    /// <summary>A fresh redeem token: 16 random bytes, 22 url-safe characters.</summary>
    /// <remarks>The pack and bundle controllers have the same generator; it could move to
    /// <c>ControllerHelpers</c>.</remarks>
    private static string NewInviteToken() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(16));
}

/// <summary>What happened when a token was redeemed: an HTTP status and either a sentence or the
/// row.</summary>
/// <remarks>Shared by this controller's team routes and the cross-type <c>/invitations</c> routes,
/// so both refuse for the same reasons with the same words.</remarks>
public sealed record TeamInvitationOutcome(int Status, string? Error, TeamInvitationEntry? Entry);

/// <summary>Team invitations, as the rest of the server sees them.</summary>
/// <remarks>Public so the controller that owns <c>GET /invitations</c> and
/// <c>/invitations/{token}</c> can handle team invitations without reimplementing expiry,
/// revocation, links or membership.</remarks>
public static class TeamInvitations
{
    /// <summary>Everything waiting for this user's answer, newest first. Tokens are included, since
    /// they are the user's own invitations and the token is how they accept.</summary>
    public static async Task<IReadOnlyList<TeamInvitationEntry>> PendingForUserAsync(
        AppDbContext db, Guid userId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = await db.TeamInvitations
            .Include(i => i.Team)
            .Include(i => i.InvitedBy)
            .Where(i => i.InvitedUserId == userId && i.AcceptedAt == null && i.RevokedAt == null
                        && (i.ExpiresAt == null || i.ExpiresAt > now))
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(ct);
        return rows.Select(i => ToEntry(i, includeToken: true)).ToList();
    }

    /// <summary>The invitation a token names, or null when no team invitation carries it.</summary>
    /// <remarks>Returned in any state (expired, revoked, accepted) so the accept screen can say
    /// which.</remarks>
    public static async Task<TeamInvitationEntry?> PreviewAsync(
        AppDbContext db, string token, CancellationToken ct = default)
    {
        var inv = await FindAsync(db, token, ct);
        // No token back out of a preview: the caller already holds it, and echoing it would put a
        // live token into any log or screenshot of the accept screen.
        return inv is null ? null : ToEntry(inv, includeToken: false);
    }

    /// <summary>Redeems a token, creating the membership. Idempotent.</summary>
    public static async Task<TeamInvitationOutcome> AcceptAsync(
        AppDbContext db, string token, Guid userId, CancellationToken ct = default)
    {
        var inv = await FindAsync(db, token, ct);
        var refusal = Refuse(inv, userId);
        if (refusal is not null) return refusal;
        var invitation = inv!;

        // A named invitation is used once. A second call returns the same answer instead of
        // re-adding someone who has since left.
        if (invitation.AcceptedAt is not null)
            return new TeamInvitationOutcome(StatusCodes.Status200OK, null, ToEntry(invitation, false));

        var team = invitation.Team;
        var isOwner = team.OwnerId == userId;
        var row = await db.TeamMembers
            .FirstOrDefaultAsync(tm => tm.TeamId == invitation.TeamId && tm.UserId == userId, ct);

        // Never Owner: that role follows Team.OwnerId, and an invitation cannot hand a team over.
        var granted = invitation.Role == TeamRole.Owner ? TeamRole.Admin : invitation.Role;

        if (row is null && !isOwner)
        {
            db.TeamMembers.Add(new TeamMember
            {
                TeamId = invitation.TeamId,
                UserId = userId,
                Role = granted
            });
            TeamActivityLog.Add(db, userId, ActivityKind.MemberAdded, team, userId, granted.ToString());
        }
        else if (row is not null && !isOwner && (int)granted > (int)row.Role)
        {
            // Already on the team at a lower role: an invitation may lift somebody, never drop them.
            var was = row.Role;
            row.Role = granted;
            TeamActivityLog.Add(db, userId, ActivityKind.RoleChanged, team, userId, $"{was} to {granted}");
        }

        // Links are not consumed by use. Only a named invitation records its acceptance, since it
        // has a single intended redeemer.
        if (invitation.InvitedUserId is not null)
            invitation.AcceptedAt = DateTimeOffset.UtcNow;

        TeamActivityLog.Add(db, userId, ActivityKind.InviteAccepted, team, userId);
        await db.SaveChangesAsync(ct);
        return new TeamInvitationOutcome(StatusCodes.Status200OK, null, ToEntry(invitation, false));
    }

    /// <summary>Turns a token down. Idempotent.</summary>
    public static async Task<TeamInvitationOutcome> DeclineAsync(
        AppDbContext db, string token, Guid userId, CancellationToken ct = default)
    {
        var inv = await FindAsync(db, token, ct);
        // Fewer refusals than accept: declining an invitation that is already withdrawn or expired
        // is what the caller wants, so declining twice answers the same both times.
        var refusal = NotFoundOrWrongAccount(inv, userId);
        if (refusal is not null) return refusal;
        var invitation = inv!;

        if (invitation.AcceptedAt is not null)
            return new TeamInvitationOutcome(StatusCodes.Status400BadRequest,
                "You already accepted that invitation. Leave the team if you no longer want it.", null);

        // Declining a link only means "not me". Marking the row revoked would take the link away
        // from everybody else holding it, so there is nothing to write down.
        if (invitation.InvitedUserId is null)
            return new TeamInvitationOutcome(StatusCodes.Status200OK, null, ToEntry(invitation, false));

        if (invitation.RevokedAt is null)
        {
            // There is one terminal column, so a decline is recorded as a revocation, which is
            // what it amounts to from the team's side.
            invitation.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return new TeamInvitationOutcome(StatusCodes.Status200OK, null, ToEntry(invitation, false));
    }

    /// <summary>The two refusals accept and decline share: no such token, and somebody else's.</summary>
    private static TeamInvitationOutcome? NotFoundOrWrongAccount(TeamInvitation? inv, Guid userId)
    {
        if (inv is null)
            return new TeamInvitationOutcome(StatusCodes.Status404NotFound,
                "That invitation link is not valid.", null);
        // Whose invitation it is stays out of the sentence: the holder of a mis-sent token does not
        // need to be told which account it was for.
        if (inv.InvitedUserId is { } target && target != userId)
            return new TeamInvitationOutcome(StatusCodes.Status403Forbidden,
                "That invitation was sent to a different account. Sign in as that account to use it.", null);
        return null;
    }

    /// <summary>Everything that stops a token being redeemed, in the order it has to be checked.</summary>
    private static TeamInvitationOutcome? Refuse(TeamInvitation? inv, Guid userId)
    {
        var shared = NotFoundOrWrongAccount(inv, userId);
        if (shared is not null) return shared;
        if (inv!.RevokedAt is not null)
            return new TeamInvitationOutcome(StatusCodes.Status400BadRequest,
                "That invitation was withdrawn.", null);
        if (inv.ExpiresAt is { } exp && exp <= DateTimeOffset.UtcNow)
            return new TeamInvitationOutcome(StatusCodes.Status400BadRequest,
                $"That invitation expired on {exp:d}.", null);
        return null;
    }

    private static Task<TeamInvitation?> FindAsync(AppDbContext db, string token, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(token)
            ? Task.FromResult<TeamInvitation?>(null)
            : db.TeamInvitations
                .Include(i => i.Team)
                .Include(i => i.InvitedBy)
                .FirstOrDefaultAsync(i => i.Token == token, ct);

    internal static TeamInvitationEntry ToEntry(TeamInvitation inv, bool includeToken) =>
        ToEntry(inv, inv.Team?.Name ?? "", inv.InvitedBy?.UserName ?? "", includeToken);

    internal static TeamInvitationEntry ToEntry(
        TeamInvitation inv, string teamName, string invitedByUsername, bool includeToken) =>
        new(inv.Id, inv.TeamId, teamName, inv.InvitedUserId, inv.InvitedUsername,
            inv.InvitedByUserId, invitedByUsername, inv.Role, inv.Message, inv.CreatedAt,
            inv.ExpiresAt, inv.AcceptedAt, inv.RevokedAt,
            // A link is the invitation nobody was named on, and it stays one after it is redeemed.
            IsLink: inv.InvitedUsername is null,
            Token: includeToken ? inv.Token : null);
}

/// <summary>Who may do what on a team, for the controllers that share content with one.</summary>
/// <remarks>Kept in one place so every content controller applies the same rules.</remarks>
public static class TeamAccess
{
    /// <summary>What this user may do on this team, or null when they are not on it at all.</summary>
    public static async Task<TeamRole?> RoleAsync(
        AppDbContext db, Guid teamId, Guid userId, CancellationToken ct = default)
    {
        var ownerId = await db.Teams.Where(t => t.Id == teamId).Select(t => (Guid?)t.OwnerId).FirstOrDefaultAsync(ct);
        if (ownerId is null) return null;
        if (ownerId == userId) return TeamRole.Owner;

        var stored = await db.TeamMembers
            .Where(tm => tm.TeamId == teamId && tm.UserId == userId)
            .Select(tm => (TeamRole?)tm.Role)
            .FirstOrDefaultAsync(ct);
        if (stored is null) return null;
        // Only Team.OwnerId makes an owner; a row left reading Owner is treated as an admin.
        return stored == TeamRole.Owner ? TeamRole.Admin : stored;
    }

    /// <summary>Whether this user is on the team in any capacity.</summary>
    public static async Task<bool> IsMemberAsync(
        AppDbContext db, Guid teamId, Guid userId, CancellationToken ct = default) =>
        await RoleAsync(db, teamId, userId, ct) is not null;

    /// <summary>Whether this user may share something of theirs with the team. Every
    /// <c>POST /{family}/{id}/teams</c> route checks this before writing the grant.</summary>
    /// <remarks>Any member may share. Someone outside the team can't push content into it.</remarks>
    public static Task<bool> CanShareWithAsync(
        AppDbContext db, Guid teamId, Guid userId, CancellationToken ct = default) =>
        IsMemberAsync(db, teamId, userId, ct);

    /// <summary>Whether this user may administer the team: invite, remove members, manage its
    /// grants.</summary>
    public static async Task<bool> CanManageAsync(
        AppDbContext db, Guid teamId, Guid userId, CancellationToken ct = default) =>
        await RoleAsync(db, teamId, userId, ct) is TeamRole.Owner or TeamRole.Admin;
}

/// <summary>Writes the team half of the activity feed.</summary>
/// <remarks>The team is the subject of every entry here, so <c>TargetTeamId</c> stays null; that
/// column is for actions on another subject, such as a pack shared with a team.</remarks>
internal static class TeamActivityLog
{
    public static void Add(
        AppDbContext db, Guid actorId, ActivityKind kind, Team team,
        Guid? targetUserId = null, string? detail = null) =>
        db.ActivityEntries.Add(new ActivityEntry
        {
            ActorUserId = actorId,
            Kind = kind,
            SubjectType = ActivitySubjectType.Team,
            SubjectId = team.Id,
            SubjectName = team.Name,
            TargetUserId = targetUserId,
            Detail = detail
        });
}
