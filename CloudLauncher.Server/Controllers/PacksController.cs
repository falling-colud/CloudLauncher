using System.Text.Json;
using CloudLauncher.Server.Auth;
using CloudLauncher.Server.Data;
using CloudLauncher.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CloudLauncher.Server.Controllers;

[ApiController]
[Authorize]
[Route("packs")]
public class PacksController(AppDbContext db, PackPermissionResolver resolver, IConfiguration config) : ControllerBase
{
    /// <summary>
    /// Packs every signed-in user sees in their list without having to find and subscribe to them,
    /// configured as a comma-separated list of pack ids in <c>AutoListPackIds</c> (settings or the
    /// <c>AutoListPackIds</c> environment variable). Empty by default, so this changes nothing until set.
    /// </summary>
    /// <remarks>
    /// Deliberately an explicit id list rather than "every public pack": public means "anyone MAY use
    /// this", which is not the same as "everyone should have it in their list" - auto-listing all public
    /// packs would push every test and side pack onto every account. The pack still needs its own
    /// Public visibility for the permission resolver to grant View/Download; this only decides whether it
    /// shows up unprompted.
    /// </remarks>
    private IReadOnlyList<Guid> AutoListPackIds() =>
        (config["AutoListPackIds"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToList();
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PackSummary>>> List(CancellationToken ct)
    {
        var me = this.UserId();

        // What is in your library: packs you own, packs you added (PackListings), and the handful in
        // AutoListPackIds that every account gets. Being a collaborator or a team member is NOT
        // enough — that grants access, and access is not the same as "put this in my list". Someone
        // sharing a pack with you should not fill your instances; you find it under "Shared with me"
        // in the browser and add it when you want it. Everything else stays reachable there.
        var autoListed = AutoListPackIds();
        var packs = await db.Packs
            .AsNoTracking()
            .Include(p => p.Owner)
            .Where(p =>
                p.OwnerId == me
                || autoListed.Contains(p.Id)
                || db.PackListings.Any(l => l.PackId == p.Id && l.UserId == me))
            .ToListAsync(ct);

        var result = new List<PackSummary>(packs.Count);
        foreach (var p in packs)
        {
            var perms = await resolver.GetAsync(p, me, ct);
            result.Add(ToSummary(p, perms));
        }
        return Ok(result);
    }

    [HttpGet("browse")]
    public async Task<ActionResult<PackBrowsePage>> Browse(
        [FromQuery] PackBrowseSource source = PackBrowseSource.Public,
        [FromQuery] Guid? teamId = null,
        [FromQuery] string? q = null,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 30,
        CancellationToken ct = default)
    {
        if (limit <= 0 || limit > 100) limit = 30;
        if (offset < 0) offset = 0;

        var me = this.UserId();
        var query = db.Packs.AsNoTracking().Include(p => p.Owner).AsQueryable();

        query = source switch
        {
            PackBrowseSource.Public =>
                query.Where(p => p.Visibility == PackVisibility.Public
                              && p.OwnerId != me
                              && !db.PackCollaborators.Any(c => c.PackId == p.Id && c.UserId == me)),
            PackBrowseSource.Shared =>
                query.Where(p => p.OwnerId != me
                              && db.PackCollaborators.Any(c => c.PackId == p.Id && c.UserId == me)),
            PackBrowseSource.Team =>
                teamId.HasValue
                    ? query.Where(p => db.PackTeams.Any(pt => pt.PackId == p.Id && pt.TeamId == teamId.Value)
                                    && db.TeamMembers.Any(tm => tm.TeamId == teamId.Value && tm.UserId == me))
                    : query.Where(p => db.PackTeams.Any(pt => pt.PackId == p.Id
                                                       && db.TeamMembers.Any(tm => tm.TeamId == pt.TeamId && tm.UserId == me))),
            PackBrowseSource.Owned =>
                query.Where(p => p.OwnerId == me),
            _ => query.Where(_ => false)
        };

        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim().ToLower();
            query = query.Where(p =>
                p.Name.ToLower().Contains(needle)
                || (p.Summary != null && p.Summary.ToLower().Contains(needle))
                || (p.Description != null && p.Description.ToLower().Contains(needle)));
        }

        var total = await query.CountAsync(ct);
        var page = await query
            .OrderByDescending(p => p.UpdatedAt)
            .Skip(offset).Take(limit)
            .ToListAsync(ct);

        var items = new List<PackSummary>(page.Count);
        foreach (var p in page)
        {
            var perms = await resolver.GetAsync(p, me, ct);
            items.Add(ToSummary(p, perms));
        }
        return Ok(new PackBrowsePage(items, offset, limit, total));
    }

    [HttpPost("{id:guid}/subscribe")]
    public async Task<ActionResult<PackSummary>> Subscribe(Guid id, CancellationToken ct)
    {
        var me = this.UserId();
        var pack = await db.Packs.Include(p => p.Owner).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pack is null) return NotFound();
        if (pack.OwnerId == me)
            return BadRequest(new { error = "You own this pack" });

        // Subscribing to a non-public pack is only allowed if the user already has
        // some path to view it (existing collaborator or team grant). For public
        // packs anyone may subscribe.
        var existingPerms = await resolver.GetAsync(pack, me, ct);
        if (pack.Visibility != PackVisibility.Public && !existingPerms.HasFlag(PackPermissions.View))
            return Forbid();

        // A public pack you found yourself needs a grant to read it; a pack already shared with you
        // has one, and must not be downgraded to ReadOnly by adding it to your list.
        var existing = await db.PackCollaborators
            .FirstOrDefaultAsync(c => c.PackId == id && c.UserId == me, ct);
        var changed = false;
        if (existing is null && !existingPerms.HasFlag(PackPermissions.View))
        {
            db.PackCollaborators.Add(new PackCollaborator
            {
                PackId = id,
                UserId = me,
                Permissions = PackPermissions.ReadOnly
            });
            changed = true;
        }

        // The listing is the part that actually puts it in the user's instances.
        var listed = await db.PackListings.AnyAsync(l => l.PackId == id && l.UserId == me, ct);
        if (!listed)
        {
            db.PackListings.Add(new PackListing { PackId = id, UserId = me });
            changed = true;
        }
        if (changed) await db.SaveChangesAsync(ct);

        // Reuse the permissions already resolved above (the only possible change is the
        // ReadOnly grant we may have just added) instead of re-running the collaborator/team
        // queries a second time.
        var perms = existingPerms | PackPermissions.ReadOnly;
        return Ok(ToSummary(pack, perms));
    }

    [HttpPost("{id:guid}/unsubscribe")]
    public async Task<IActionResult> Unsubscribe(Guid id, CancellationToken ct)
    {
        var me = this.UserId();
        var pack = await db.Packs.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pack is null) return NotFound();
        if (pack.OwnerId == me)
            return BadRequest(new { error = "Owners cannot unsubscribe; delete the pack instead" });

        var listing = await db.PackListings.FirstOrDefaultAsync(l => l.PackId == id && l.UserId == me, ct);
        var self = await db.PackCollaborators
            .FirstOrDefaultAsync(c => c.PackId == id && c.UserId == me, ct);

        if (listing is null && self is null)
            return NotFound(new { error = "This pack is not in your list" });

        if (listing is not null) db.PackListings.Remove(listing);

        // Only give up access that was self-granted by adding a public pack. A ReadOnly row on a
        // pack that is NOT public came from the owner sharing it — removing the pack from your list
        // must not throw that away, or re-adding it later would be impossible.
        if (self is { Permissions: PackPermissions.ReadOnly } && pack.Visibility == PackVisibility.Public)
            db.PackCollaborators.Remove(self);

        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost]
    public async Task<ActionResult<PackSummary>> Create([FromBody] CreatePackRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 128)
            return BadRequest(new { error = "Pack name must be 1-128 characters" });
        if (req.Summary?.Length > PackText.SummaryMaxLength)
            return BadRequest(new { error = $"Pack summary must be {PackText.SummaryMaxLength} characters or fewer" });
        if (req.Description?.Length > PackText.DescriptionMaxLength)
            return BadRequest(new { error = $"Pack description must be {PackText.DescriptionMaxLength} characters or fewer" });

        var me = this.UserId();
        var pack = new Pack
        {
            Name = req.Name.Trim(),
            Summary = req.Summary,
            Description = req.Description,
            OwnerId = me,
            IsEmpty = req.IsEmpty,
            MinecraftVersion = req.IsEmpty ? null : req.MinecraftVersion,
            Loader = req.IsEmpty ? LoaderKind.None : req.Loader,
            LoaderVersion = req.IsEmpty ? null : req.LoaderVersion
        };
        db.Packs.Add(pack);
        await db.SaveChangesAsync(ct);
        await db.Entry(pack).Reference(p => p.Owner).LoadAsync(ct);
        return Ok(ToSummary(pack, PackPermissions.Full));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PackDetail>> Get(Guid id, CancellationToken ct)
    {
        var me = this.UserId();
        var pack = await db.Packs
            .Include(p => p.Owner)
            .Include(p => p.Collaborators).ThenInclude(c => c.User)
            .Include(p => p.Teams).ThenInclude(t => t.Team)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pack is null) return NotFound();

        var perms = await resolver.GetAsync(pack, me, ct);
        if (!perms.HasFlag(PackPermissions.View))
            return Forbid();

        var canSeeMembers = pack.OwnerId == me || perms.HasFlag(PackPermissions.ManageCollaborators);
        var collaborators = canSeeMembers
            ? pack.Collaborators.Select(c => new PackCollaboratorEntry(c.UserId, c.User.UserName ?? "", c.Permissions)).ToList()
            : new List<PackCollaboratorEntry>();
        var teams = canSeeMembers
            ? pack.Teams.Select(t => new PackTeamEntry(t.TeamId, t.Team.Name, t.Permissions)).ToList()
            : new List<PackTeamEntry>();

        var rules = pack.RulesJson is null
            ? new List<PackFileRule>()
            : JsonSerializer.Deserialize<List<PackFileRule>>(pack.RulesJson) ?? new List<PackFileRule>();

        return Ok(new PackDetail(
            pack.Id, pack.Name, pack.Description, pack.OwnerId, pack.Owner.UserName ?? "",
            pack.Visibility, pack.IsShared, pack.IsEmpty, pack.MinecraftVersion, pack.Loader, pack.LoaderVersion,
            pack.CreatedAt, pack.UpdatedAt, perms, collaborators, teams, rules, pack.Summary));
    }

    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<PackSummary>> Update(Guid id, [FromBody] UpdatePackRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var pack = await db.Packs.Include(p => p.Owner).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pack is null) return NotFound();
        if (pack.OwnerId != me) return Forbid();

        if (req.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 128)
                return BadRequest(new { error = "Pack name must be 1-128 characters" });
            pack.Name = req.Name.Trim();
        }
        if (req.Description is not null)
        {
            if (req.Description.Length > PackText.DescriptionMaxLength)
                return BadRequest(new { error = $"Pack description must be {PackText.DescriptionMaxLength} characters or fewer" });
            pack.Description = req.Description;
        }
        if (req.Summary is not null)
        {
            if (req.Summary.Length > PackText.SummaryMaxLength)
                return BadRequest(new { error = $"Pack summary must be {PackText.SummaryMaxLength} characters or fewer" });
            pack.Summary = req.Summary;
        }
        if (req.Visibility is not null) pack.Visibility = req.Visibility.Value;
        if (req.IsShared is not null) pack.IsShared = req.IsShared.Value;
        if (req.IsEmpty is not null)
        {
            pack.IsEmpty = req.IsEmpty.Value;
            if (pack.IsEmpty)
            {
                pack.MinecraftVersion = null;
                pack.Loader = LoaderKind.None;
                pack.LoaderVersion = null;
            }
        }
        if (!pack.IsEmpty)
        {
            if (req.MinecraftVersion is not null) pack.MinecraftVersion = req.MinecraftVersion;
            if (req.Loader is not null) pack.Loader = req.Loader.Value;
            if (req.LoaderVersion is not null) pack.LoaderVersion = req.LoaderVersion;
        }
        if (req.Rules is not null)
            pack.RulesJson = req.Rules.Count == 0
                ? null
                : JsonSerializer.Serialize(req.Rules);
        pack.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return Ok(ToSummary(pack, PackPermissions.Full));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var me = this.UserId();
        var pack = await db.Packs.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pack is null) return NotFound();
        if (pack.OwnerId != me) return Forbid();
        db.Packs.Remove(pack);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ---- Collaborators ----

    [HttpPost("{id:guid}/collaborators")]
    public async Task<ActionResult<PackCollaboratorEntry>> AddCollaborator(
        Guid id, [FromBody] AddCollaboratorRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var pack = await db.Packs.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pack is null) return NotFound();
        if (pack.OwnerId != me) return Forbid();

        var user = await db.Users.FirstOrDefaultAsync(u => u.UserName == req.Username, ct);
        if (user is null) return BadRequest(new { error = "User not found" });
        if (user.Id == pack.OwnerId) return BadRequest(new { error = "Owner is implicit" });

        var existing = await db.PackCollaborators
            .FirstOrDefaultAsync(c => c.PackId == id && c.UserId == user.Id, ct);
        if (existing is not null)
        {
            existing.Permissions = req.Permissions;
        }
        else
        {
            db.PackCollaborators.Add(new PackCollaborator
            {
                PackId = id, UserId = user.Id, Permissions = req.Permissions
            });
        }
        pack.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok(new PackCollaboratorEntry(user.Id, user.UserName ?? "", req.Permissions));
    }

    [HttpPatch("{id:guid}/collaborators/{userId:guid}")]
    public async Task<IActionResult> UpdateCollaborator(
        Guid id, Guid userId, [FromBody] UpdateCollaboratorRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var pack = await db.Packs.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pack is null) return NotFound();
        if (pack.OwnerId != me) return Forbid();

        var row = await db.PackCollaborators.FirstOrDefaultAsync(c => c.PackId == id && c.UserId == userId, ct);
        if (row is null) return NotFound();
        row.Permissions = req.Permissions;
        pack.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}/collaborators/{userId:guid}")]
    public async Task<IActionResult> RemoveCollaborator(Guid id, Guid userId, CancellationToken ct)
    {
        var me = this.UserId();
        var pack = await db.Packs.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pack is null) return NotFound();
        if (pack.OwnerId != me) return Forbid();

        var row = await db.PackCollaborators.FirstOrDefaultAsync(c => c.PackId == id && c.UserId == userId, ct);
        if (row is null) return NotFound();
        db.PackCollaborators.Remove(row);
        pack.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ---- Pack teams ----

    [HttpPost("{id:guid}/teams")]
    public async Task<ActionResult<PackTeamEntry>> AddTeam(
        Guid id, [FromBody] AddPackTeamRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var pack = await db.Packs.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pack is null) return NotFound();
        if (pack.OwnerId != me) return Forbid();

        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == req.TeamId, ct);
        if (team is null) return BadRequest(new { error = "Team not found" });

        var existing = await db.PackTeams.FirstOrDefaultAsync(pt => pt.PackId == id && pt.TeamId == req.TeamId, ct);
        if (existing is not null) existing.Permissions = req.Permissions;
        else db.PackTeams.Add(new PackTeam { PackId = id, TeamId = req.TeamId, Permissions = req.Permissions });

        pack.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok(new PackTeamEntry(team.Id, team.Name, req.Permissions));
    }

    [HttpPatch("{id:guid}/teams/{teamId:guid}")]
    public async Task<IActionResult> UpdateTeam(
        Guid id, Guid teamId, [FromBody] UpdatePackTeamRequest req, CancellationToken ct)
    {
        var me = this.UserId();
        var pack = await db.Packs.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pack is null) return NotFound();
        if (pack.OwnerId != me) return Forbid();
        var row = await db.PackTeams.FirstOrDefaultAsync(pt => pt.PackId == id && pt.TeamId == teamId, ct);
        if (row is null) return NotFound();
        row.Permissions = req.Permissions;
        pack.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}/teams/{teamId:guid}")]
    public async Task<IActionResult> RemoveTeam(Guid id, Guid teamId, CancellationToken ct)
    {
        var me = this.UserId();
        var pack = await db.Packs.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pack is null) return NotFound();
        if (pack.OwnerId != me) return Forbid();
        var row = await db.PackTeams.FirstOrDefaultAsync(pt => pt.PackId == id && pt.TeamId == teamId, ct);
        if (row is null) return NotFound();
        db.PackTeams.Remove(row);
        pack.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static PackSummary ToSummary(Pack p, PackPermissions perms) => new(
        p.Id, p.Name, p.Description, p.OwnerId, p.Owner.UserName ?? "",
        p.Visibility, p.IsShared, p.IsEmpty, p.MinecraftVersion, p.Loader, p.LoaderVersion,
        p.CreatedAt, p.UpdatedAt, perms, p.Summary);
}
