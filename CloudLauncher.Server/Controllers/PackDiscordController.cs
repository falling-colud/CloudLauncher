using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CloudLauncher.Server.Auth;
using CloudLauncher.Server.Data;
using CloudLauncher.Server.Discord;
using CloudLauncher.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CloudLauncher.Server.Controllers;

/// <summary>
/// The Discord channels a pack's planning boards are shown in, managed from the launcher's planning
/// page.
/// </summary>
/// <remarks>
/// Linking puts the boards in front of everyone who can read the channel, so it takes the same right
/// as inviting people to the pack (<see cref="PackPermissions.ManageCollaborators"/>; owners have it).
/// A webhook's token is a password for posting in that channel: it is stored, used, and never sent
/// back out.
/// </remarks>
[ApiController]
[Authorize]
[Route("packs/{packId:guid}/discord")]
public partial class PackDiscordController(
    AppDbContext db,
    PackPermissionResolver resolver,
    DiscordRest discord,
    DiscordPlanPublisher publisher,
    DiscordOptions options,
    AppOptions app,
    ILogger<PackDiscordController> log) : ControllerBase
{
    private const int MaxLinksPerPack = 10;
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(30);

    [HttpGet]
    public async Task<IActionResult> Get(Guid packId, CancellationToken ct)
    {
        if (await CheckAsync(packId, PackPermissions.View, ct) is { } denied) return denied;
        var canLink = await HasAsync(packId, PackPermissions.ManageCollaborators, ct);
        var links = await db.PackDiscordLinks.AsNoTracking().Where(l => l.PackId == packId)
            .OrderBy(l => l.CreatedAt).ToListAsync(ct);
        // Expired codes are of no use to anyone; they go as soon as someone looks.
        var expired = links.Where(IsExpired).Select(l => l.Id).ToList();
        if (expired.Count > 0)
            await db.PackDiscordLinks.Where(l => expired.Contains(l.Id)).ExecuteDeleteAsync(ct);
        var names = await BoardNamesAsync(packId, ct);
        return Ok(new PackDiscordInfo(options.BotConfigured, options.InviteUrl,
            links.Where(l => !IsExpired(l)).Select(l => ToDto(l, names, canLink)).ToList()));
    }

    /// <summary>Links a channel: at once through a pasted webhook, or with a code for <c>/plan link</c>.</summary>
    [HttpPost("links")]
    public async Task<IActionResult> Create(Guid packId, [FromBody] CreatePackDiscordLinkRequest req, CancellationToken ct)
    {
        if (await CheckAsync(packId, PackPermissions.ManageCollaborators, ct) is { } denied) return denied;
        if (await db.PackDiscordLinks.CountAsync(l => l.PackId == packId, ct) >= MaxLinksPerPack)
            return BadRequest(new { error = $"A pack can show its boards in at most {MaxLinksPerPack} channels." });
        var boardId = string.IsNullOrWhiteSpace(req.BoardId) ? null : req.BoardId.Trim();
        if (boardId is { Length: > 64 }) return BadRequest(new { error = "That board id is not one of this pack's." });

        var link = new PackDiscordLink
        {
            PackId = packId,
            CreatedById = this.UserId(),
            BoardId = boardId,
            ViewKey = DiscordPlanPublisher.NewViewKey()
        };

        if (!string.IsNullOrWhiteSpace(req.WebhookUrl))
        {
            var match = WebhookUrl().Match(req.WebhookUrl.Trim());
            if (!match.Success)
                return BadRequest(new { error = "That isn't a Discord webhook URL. Copy it from the channel's settings: Integrations, Webhooks, Copy Webhook URL." });
            var (id, token) = (match.Groups[1].Value, match.Groups[2].Value);
            try
            {
                var hook = await discord.GetWebhookAsync(id, token, ct);
                if (hook.TryGetProperty("type", out var type) && type.GetInt32() != 1)
                    return BadRequest(new { error = "That webhook can't post messages. Make a new one in the channel's Integrations settings." });
                link.Mode = "webhook";
                link.WebhookId = id;
                link.WebhookToken = token;
                link.ChannelId = hook.TryGetProperty("channel_id", out var ch) ? ch.GetString() : null;
                link.GuildId = hook.TryGetProperty("guild_id", out var g) ? g.GetString() : null;
                link.Label = hook.TryGetProperty("name", out var n) ? Trim(n.GetString(), 128) : "Webhook";
            }
            catch (DiscordApiException e) when (e.Status is 401 or 404)
            {
                return BadRequest(new { error = "Discord doesn't know that webhook. It may have been deleted; make a new one." });
            }
            catch (Exception e) when (e is DiscordApiException or HttpRequestException or TaskCanceledException)
            {
                log.LogInformation("Webhook check failed: {Message}", e.Message);
                return StatusCode(502, new { error = "Couldn't reach Discord to check the webhook. Try again in a moment." });
            }
            if (await db.PackDiscordLinks.AnyAsync(l => l.PackId == packId && l.WebhookId == link.WebhookId, ct))
                return Conflict(new { error = "This pack already posts through that webhook." });
            link.RefreshRequested = true;
        }
        else
        {
            if (!options.BotConfigured)
                return BadRequest(new { error = "This server has no Discord app set up. Paste a channel webhook instead." });
            link.Mode = "bot";
            link.Code = NewCode();
            link.CodeExpiresAt = DateTimeOffset.UtcNow + CodeLifetime;
        }

        db.PackDiscordLinks.Add(link);
        await db.SaveChangesAsync(ct);
        if (link.RefreshRequested) publisher.Wake();
        return Ok(ToDto(link, await BoardNamesAsync(packId, ct), showCode: true));
    }

    [HttpDelete("links/{linkId:guid}")]
    public async Task<IActionResult> Delete(Guid packId, Guid linkId, CancellationToken ct)
    {
        if (await CheckAsync(packId, PackPermissions.ManageCollaborators, ct) is { } denied) return denied;
        var link = await db.PackDiscordLinks.FirstOrDefaultAsync(l => l.Id == linkId && l.PackId == packId, ct);
        if (link is null) return NotFound(new { error = "That link is already gone." });
        db.PackDiscordLinks.Remove(link);
        await db.SaveChangesAsync(ct);
        // After the row is gone, so the publisher can't post a board again while they are deleted.
        var removed = await publisher.RemoveMessagesAsync(link, ct);
        return Ok(new { removed });
    }

    /// <summary>Draws every board again now, whether or not it changed.</summary>
    [HttpPost("links/{linkId:guid}/refresh")]
    public async Task<IActionResult> Refresh(Guid packId, Guid linkId, CancellationToken ct)
    {
        if (await CheckAsync(packId, PackPermissions.UploadShared, ct) is { } denied) return denied;
        var link = await db.PackDiscordLinks.FirstOrDefaultAsync(l => l.Id == linkId && l.PackId == packId, ct);
        if (link is null) return NotFound(new { error = "That link is gone." });
        link.RefreshRequested = true;
        link.Broken = false;
        await db.SaveChangesAsync(ct);
        publisher.Wake();
        return Ok(ToDto(link, await BoardNamesAsync(packId, ct),
            await HasAsync(packId, PackPermissions.ManageCollaborators, ct)));
    }

    /// <param name="showCode">A pending code lets whoever holds it put the boards in their own channel,
    /// so only someone allowed to link may see it.</param>
    private PackDiscordLinkDto ToDto(PackDiscordLink l, IReadOnlyDictionary<string, string> boardNames, bool showCode) => new(
        l.Id,
        l.Mode,
        l.Broken ? "broken" : l.Mode == "bot" && l.ChannelId is null ? "pending" : "active",
        l.BoardId,
        l.BoardId is not null && boardNames.TryGetValue(l.BoardId, out var name) ? name : null,
        l.GuildId,
        l.ChannelId,
        l.Label,
        showCode ? l.Code : null,
        l.CodeExpiresAt,
        l.Mode == "bot" && l.ChannelId is null ? null : DiscordPlanPublisher.ViewUrl(app, l),
        l.CreatedAt,
        l.PublishedAt,
        l.LastError);

    private async Task<IReadOnlyDictionary<string, string>> BoardNamesAsync(Guid packId, CancellationToken ct)
    {
        var json = await db.PackPlans.AsNoTracking().Where(p => p.PackId == packId).Select(p => p.Json).FirstOrDefaultAsync(ct);
        var doc = json is null ? null : PlanDocData.TryParse(json);
        return doc?.Boards.GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First().Name)
               ?? new Dictionary<string, string>();
    }

    private static bool IsExpired(PackDiscordLink l) =>
        l.Mode == "bot" && l.ChannelId is null && l.CodeExpiresAt is { } at && at < DateTimeOffset.UtcNow;

    /// <summary>Eight letters and digits, in two groups, from an alphabet without look-alikes.</summary>
    private static string NewCode()
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        Span<char> c = stackalloc char[9];
        for (var i = 0; i < 9; i++) c[i] = i == 4 ? '-' : alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(c);
    }

    private static string? Trim(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];

    private async Task<bool> HasAsync(Guid packId, PackPermissions required, CancellationToken ct)
    {
        var pack = await db.Packs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == packId, ct);
        return pack is not null && (await resolver.GetAsync(pack, this.UserId(), ct)).HasFlag(required);
    }

    private async Task<IActionResult?> CheckAsync(Guid packId, PackPermissions required, CancellationToken ct)
    {
        var pack = await db.Packs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == packId, ct);
        if (pack is null) return NotFound(new { error = "Pack not found" });
        var perms = await resolver.GetAsync(pack, this.UserId(), ct);
        return perms.HasFlag(required) ? null : Forbid();
    }

    [GeneratedRegex(@"^https://(?:(?:canary|ptb)\.)?discord(?:app)?\.com/api(?:/v\d+)?/webhooks/(\d{5,25})/([A-Za-z0-9_\-]{20,128})/?(?:\?.*)?$")]
    private static partial Regex WebhookUrl();
}
