using System.Security.Cryptography;
using System.Text;
using CloudLauncher.Server.Data;
using CloudLauncher.Server.Discord;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace CloudLauncher.Server.Controllers;

/// <summary>
/// The live board viewer behind the "Open live board" button on posted boards: the page itself is
/// <c>wwwroot/plans/viewer.html</c> (served for <c>/plans/view/{token}</c>), and these are its data.
/// </summary>
/// <remarks>
/// <para>The token is a Discord link's id and its secret view key: anyone holding it sees what that
/// channel shows, and unlinking the channel ends it. It gives no other access to the pack.</para>
/// <para>Inside Discord the same page runs as the app's Activity, which trades the Activity's
/// instance id for the tokens of the boards linked to the channel it was opened in
/// (<see cref="Session"/>).</para>
/// </remarks>
[ApiController]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.PlanView)]
public sealed class PlanViewController(
    AppDbContext db,
    PlanChangeSignals signals,
    PlanAssets assets,
    DiscordRest discord,
    DiscordOptions options,
    IMemoryCache cache) : ControllerBase
{
    private const int MaxWaitSeconds = 25;

    /// <summary>Inside Discord every address the Activity asks for sits under its folder (Discord's URL
    /// mapping), so each route answers there too. Routing runs before Program.cs rewrites the alias
    /// paths, which is why these are routes and not left to the rewrite.</summary>
    private const string ActivityAlias = "discord-activity/";

    /// <summary>The boards the link shows, or after <paramref name="after"/>, the next change (204 when
    /// none came within <paramref name="wait"/> seconds).</summary>
    [HttpGet("plans/view/{token}/state")]
    [HttpGet(ActivityAlias + "plans/view/{token}/state")]
    public async Task<IActionResult> State(string token, [FromQuery] long? after, [FromQuery] int wait, CancellationToken ct)
    {
        var link = await FindAsync(token, ct);
        if (link is null) return NotFound(new { error = "This board link has ended." });

        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(wait, 0, MaxWaitSeconds));
        while (true)
        {
            var changed = signals.Next(link.PackId);
            var plan = await db.PackPlans.AsNoTracking().Where(p => p.PackId == link.PackId)
                .Select(p => new { p.Revision, p.UpdatedAt, p.Json }).FirstOrDefaultAsync(ct);
            var revision = plan?.Revision ?? 0;
            if (after is null || revision != after)
            {
                var packName = await db.Packs.AsNoTracking().Where(p => p.Id == link.PackId).Select(p => p.Name)
                    .FirstOrDefaultAsync(ct) ?? "Modpack";
                var boards = Boards(link, plan?.Json).Select(b =>
                {
                    var cards = b.Nodes.Count(n => n.Kind != PlanNodeData.KindSection);
                    return new { id = b.Id, name = b.Name, cards, hash = b.ContentHash("view") };
                }).ToList();
                Response.Headers.CacheControl = "no-store";
                return Ok(new { revision, updatedAt = plan?.UpdatedAt, pack = packName, boards });
            }

            var left = deadline - DateTimeOffset.UtcNow;
            if (left <= TimeSpan.Zero) return NoContent();
            try { await changed.WaitAsync(left, ct); }
            catch (TimeoutException) { /* read once more, then answer 204 */ }
        }
    }

    /// <summary>One board as SVG, at the revision stored now.</summary>
    [HttpGet("plans/view/{token}/board/{boardId}.svg")]
    [HttpGet(ActivityAlias + "plans/view/{token}/board/{boardId}.svg")]
    public async Task<IActionResult> Svg(string token, string boardId, CancellationToken ct)
    {
        var found = await ResolveAsync(token, boardId, ct);
        if (found is null) return NotFound(new { error = "That board isn't here any more." });
        var (key, board, _) = found.Value;
        if (Request.Headers.IfNoneMatch.ToString() == key) return StatusCode(304);
        if (!cache.TryGetValue(("plan-svg", key), out string? svg) || svg is null)
        {
            var (drawing, icons) = await DrawAsync(board, ct);
            svg = cache.Set(("plan-svg", key), drawing.Svg(icons), TimeSpan.FromMinutes(10));
        }
        Response.Headers.ETag = key;
        Response.Headers.CacheControl = "no-cache";
        return Content(svg, "image/svg+xml; charset=utf-8", Encoding.UTF8);
    }

    /// <summary>One board as the same picture Discord gets.</summary>
    [HttpGet("plans/view/{token}/board/{boardId}.png")]
    [HttpGet(ActivityAlias + "plans/view/{token}/board/{boardId}.png")]
    public async Task<IActionResult> Png(string token, string boardId, CancellationToken ct)
    {
        var found = await ResolveAsync(token, boardId, ct);
        if (found is null) return NotFound(new { error = "That board isn't here any more." });
        var (key, board, packName) = found.Value;
        if (Request.Headers.IfNoneMatch.ToString() == key) return StatusCode(304);
        if (!cache.TryGetValue(("plan-png", key), out byte[]? png) || png is null)
        {
            var (drawing, icons) = await DrawAsync(board, ct);
            var cards = board.Nodes.Count(n => n.Kind != PlanNodeData.KindSection);
            png = cache.Set(("plan-png", key),
                drawing.Png(board.Name, $"{packName}  ·  {cards} card{(cards == 1 ? "" : "s")}", icons),
                TimeSpan.FromMinutes(10));
        }
        Response.Headers.ETag = key;
        Response.Headers.CacheControl = "no-cache";
        return File(png, "image/png");
    }

    /// <summary>For the Activity page: the app id its Discord SDK starts with.</summary>
    [HttpGet("discord/activity/config")]
    [HttpGet(ActivityAlias + "discord/activity/config")]
    public IActionResult ActivityConfig() =>
        options.BotConfigured && options.ActivityEnabled ? Ok(new { clientId = options.ApplicationId }) : NotFound();

    /// <summary>For the Activity: the boards linked to the channel it was opened in. Discord confirms
    /// the instance and where it is, so a made-up channel id gets nothing.</summary>
    [HttpPost("discord/activity/session")]
    [HttpPost(ActivityAlias + "discord/activity/session")]
    public async Task<IActionResult> Session([FromBody] ActivitySessionRequest req, CancellationToken ct)
    {
        if (!options.BotConfigured || !options.ActivityEnabled) return NotFound();
        if (string.IsNullOrWhiteSpace(req.InstanceId) || req.InstanceId.Length > 200)
            return BadRequest(new { error = "Open this from the button on a board in Discord." });

        var instance = await discord.GetActivityInstanceAsync(req.InstanceId, ct);
        if (instance is not { } found || !found.TryGetProperty("location", out var location)
            || !location.TryGetProperty("channel_id", out var channel) || channel.GetString() is not { } channelId)
            return StatusCode(403, new { error = "Discord doesn't know this Activity. Open it again from a board's button." });

        var links = await db.PackDiscordLinks.AsNoTracking()
            .Where(l => l.ChannelId == channelId && !(l.Mode == "bot" && l.Code != null))
            .Join(db.Packs, l => l.PackId, p => p.Id, (l, p) => new { Link = l, Pack = p.Name })
            .ToListAsync(ct);
        return Ok(new
        {
            links = links.Select(x => new
            {
                token = DiscordPlanPublisher.ViewToken(x.Link),
                pack = x.Pack,
                // Open on the board the channel's message shows, since the button can't say which.
                boardId = x.Link.BoardId ?? DiscordPlanPublisher.ShownBoard(x.Link)
            })
        });
    }

    /// <summary>The board a viewer asks for and the ETag of what it shows now.</summary>
    private async Task<(string Key, PlanBoardData Board, string PackName)?> ResolveAsync(string token, string boardId,
        CancellationToken ct)
    {
        var link = await FindAsync(token, ct);
        if (link is null) return null;
        var plan = await db.PackPlans.AsNoTracking().Where(p => p.PackId == link.PackId)
            .Select(p => new { p.Revision, p.Json }).FirstOrDefaultAsync(ct);
        var board = Boards(link, plan?.Json).FirstOrDefault(b => b.Id == boardId);
        if (board is null || plan is null) return null;
        var packName = await db.Packs.AsNoTracking().Where(p => p.Id == link.PackId).Select(p => p.Name)
            .FirstOrDefaultAsync(ct) ?? "Modpack";
        // The content hash, not the revision: a change to another board leaves this one's picture cached.
        return ($"\"{link.PackId:N}-{board.ContentHash(packName)}\"", board, packName);
    }

    private async Task<(PlanBoardDrawing Drawing, IReadOnlyDictionary<string, byte[]> Icons)> DrawAsync(
        PlanBoardData board, CancellationToken ct)
    {
        var labels = await assets.ModLabelsAsync([board], ct);
        var drawing = PlanBoardDrawing.Layout(board, labels);
        var icons = await assets.IconsAsync(drawing.IconUrls, ct);
        return (drawing, icons);
    }

    private static List<PlanBoardData> Boards(PackDiscordLink link, string? json)
    {
        var doc = json is null ? null : PlanDocData.TryParse(json);
        return (doc?.Boards ?? new List<PlanBoardData>())
            .Where(b => link.BoardId is null || b.Id == link.BoardId)
            .Where(b => !b.IsEmpty)
            .ToList();
    }

    private async Task<PackDiscordLink?> FindAsync(string token, CancellationToken ct)
    {
        var dot = token.IndexOf('.');
        if (dot <= 0 || !Guid.TryParseExact(token[..dot], "N", out var id)) return null;
        var key = token[(dot + 1)..];
        var link = await db.PackDiscordLinks.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct);
        if (link is null || link.Mode == "bot" && link.ChannelId is null) return null;
        var a = Encoding.UTF8.GetBytes(link.ViewKey);
        var b = Encoding.UTF8.GetBytes(key);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b) ? link : null;
    }
}

public sealed record ActivitySessionRequest(string? InstanceId);
