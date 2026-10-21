using System.Text.Json;
using System.Text.Json.Nodes;
using CloudLauncher.Server.Auth;
using CloudLauncher.Server.Data;
using CloudLauncher.Server.Discord;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace CloudLauncher.Server.Controllers;

/// <summary>
/// Discord's interactions endpoint for the CloudLauncher app: the <c>/plan</c> command and the
/// buttons on posted boards.
/// </summary>
/// <remarks>
/// <para>Set as the app's Interactions Endpoint URL (<c>https://api.cloudlauncher.co/discord/interactions</c>).
/// Every request is signed by Discord; an unsigned one gets a 401 before anything else happens.</para>
/// <para>Discord wants an answer within three seconds, so anything slow (taking messages down) is
/// started and left to finish after the reply.</para>
/// </remarks>
[ApiController]
[AllowAnonymous]
[Route("discord/interactions")]
[EnableRateLimiting(RateLimitPolicies.PlanView)]
public sealed class DiscordInteractionsController(
    AppDbContext db,
    DiscordRest discord,
    DiscordPlanPublisher publisher,
    DiscordOptions options,
    AppOptions app,
    ILogger<DiscordInteractionsController> log) : ControllerBase
{
    private const int MaxBody = 256 * 1024;
    private const int Ephemeral = 64;

    [HttpPost]
    public async Task<IActionResult> Post(CancellationToken ct)
    {
        if (!options.BotConfigured) return NotFound();

        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, ct);
        if (buffer.Length > MaxBody) return StatusCode(413);
        var body = buffer.ToArray();
        if (!DiscordSignature.Verify(options.PublicKey, Request.Headers["X-Signature-Ed25519"],
                Request.Headers["X-Signature-Timestamp"], body))
            return Unauthorized();

        using var doc = JsonDocument.Parse(body);
        var interaction = doc.RootElement;
        var type = interaction.GetProperty("type").GetInt32();
        return type switch
        {
            1 => Json(new JsonObject { ["type"] = 1 }), // ping
            2 => await CommandAsync(interaction, ct),
            3 => await ComponentAsync(interaction, ct),
            _ => BadRequest()
        };
    }

    private async Task<IActionResult> CommandAsync(JsonElement interaction, CancellationToken ct)
    {
        var data = interaction.GetProperty("data");
        if (data.GetProperty("name").GetString() != "plan") return Reply("I don't know that command.");
        var sub = data.TryGetProperty("options", out var subs) && subs.GetArrayLength() > 0 ? subs[0] : default;
        var name = sub.ValueKind == JsonValueKind.Object ? sub.GetProperty("name").GetString() : null;
        var channelId = ChannelOf(interaction);
        var guildId = interaction.TryGetProperty("guild_id", out var g) ? g.GetString() : null;
        if (channelId is null || guildId is null) return Reply("Use this in a server's text channel.");

        switch (name)
        {
            case "link":
            {
                var code = Option(sub, "code");
                return await LinkAsync(interaction, NormalizeCode(code), guildId, channelId, ct);
            }
            case "unlink":
            {
                var links = await db.PackDiscordLinks.Where(l => l.ChannelId == channelId).ToListAsync(ct);
                if (links.Count == 0) return Reply("No CloudLauncher boards are shown in this channel.");
                db.PackDiscordLinks.RemoveRange(links);
                await db.SaveChangesAsync(ct);
                _ = Task.Run(async () =>
                {
                    foreach (var link in links) await publisher.RemoveMessagesAsync(link, CancellationToken.None);
                }, CancellationToken.None);
                var packs = links.Select(l => l.PackId).Distinct().Count();
                return Reply(packs == 1
                    ? "Done: this channel no longer shows that pack's boards. Their messages are being taken down."
                    : $"Done: this channel no longer shows {packs} packs' boards. Their messages are being taken down.");
            }
            case "refresh":
            {
                var links = await db.PackDiscordLinks.Where(l => l.ChannelId == channelId).ToListAsync(ct);
                if (links.Count == 0) return Reply("No CloudLauncher boards are shown in this channel.");
                foreach (var link in links)
                {
                    link.RefreshRequested = true;
                    link.Broken = false;
                }
                await db.SaveChangesAsync(ct);
                publisher.Wake();
                return Reply("Redrawing the boards here now.");
            }
            default:
                return Reply("Use `/plan link`, `/plan unlink` or `/plan refresh`.");
        }
    }

    private async Task<IActionResult> LinkAsync(JsonElement interaction, string? code, string guildId, string channelId,
        CancellationToken ct)
    {
        if (code is null) return Reply("That code doesn't look right. It's on the pack's planning page in CloudLauncher, under Show in Discord.");
        var link = await db.PackDiscordLinks.FirstOrDefaultAsync(l => l.Code == code && l.Mode == "bot", ct);
        if (link is null || link.CodeExpiresAt is not { } expires || expires < DateTimeOffset.UtcNow)
            return Reply("That code isn't valid (codes last 30 minutes and work once). Make a new one on the pack's planning page in CloudLauncher.");

        // The app has to be able to see and post in the channel, or every update would fail later.
        string? channelName;
        try
        {
            var channel = await discord.GetChannelAsync(channelId, ct);
            channelName = channel.TryGetProperty("name", out var n) ? n.GetString() : null;
        }
        catch (DiscordApiException e) when (e.TargetGone || e.Status == 404)
        {
            return Reply("I can't see this channel. Give CloudLauncher View Channel, Send Messages, Embed Links and Attach Files here (channel settings, Permissions), then run `/plan link` again.");
        }

        var packName = await db.Packs.AsNoTracking().Where(p => p.Id == link.PackId).Select(p => p.Name)
            .FirstOrDefaultAsync(ct) ?? "the pack";
        link.Code = null;
        link.CodeExpiresAt = null;
        link.GuildId = guildId;
        link.ChannelId = channelId;
        link.Label = channelName is null ? null : "#" + channelName;
        link.LinkedByDiscordUserId = UserOf(interaction);
        link.RefreshRequested = true;
        await db.SaveChangesAsync(ct);
        publisher.Wake();
        log.LogInformation("Discord link {Link} for pack {Pack} made in channel {Channel}", link.Id, link.PackId, channelId);
        return Reply($"Linked **{PlanMessageBuilder.Escape(packName)}**. Its planning boards show up here in a moment, as one message you can flip through, and follow every change.");
    }

    private async Task<IActionResult> ComponentAsync(JsonElement interaction, CancellationToken ct)
    {
        var data = interaction.GetProperty("data");
        var id = data.GetProperty("custom_id").GetString() ?? "";
        if (Switch(interaction, data, id) is { } switched) return switched;
        if (id != PlanMessageBuilder.OpenActivityId) return Reply("That button is from an older version of this message.");
        if (options.ActivityEnabled) return Json(new JsonObject { ["type"] = 12 }); // launch the Activity

        var channelId = ChannelOf(interaction);
        var link = channelId is null ? null
            : await db.PackDiscordLinks.AsNoTracking().FirstOrDefaultAsync(l => l.ChannelId == channelId && !l.Broken, ct);
        return link is null
            ? Reply("These boards aren't linked here any more.")
            : Reply($"Live board: {DiscordPlanPublisher.ViewUrl(app, link)}");
    }

    /// <summary>The board list and Previous/Next on the app's one message. Drawing the board can take
    /// longer than Discord waits for an answer, so the answer is "the message will change" (deferred
    /// update) and the publisher edits it a moment later. Anyone who can see the channel may switch:
    /// the message is the channel's, like a shared slideshow.</summary>
    private ContentResult? Switch(JsonElement interaction, JsonElement data, string id)
    {
        string? boardId = null;
        int step;
        string linkPart;
        if (id.StartsWith(PlanMessageBuilder.SwitchBoardPrefix, StringComparison.Ordinal))
        {
            linkPart = id[PlanMessageBuilder.SwitchBoardPrefix.Length..];
            step = 0;
            if (data.TryGetProperty("values", out var values) && values.GetArrayLength() > 0) boardId = values[0].GetString();
            if (boardId is null) return Json(new JsonObject { ["type"] = 6 });
        }
        else if (id.StartsWith(PlanMessageBuilder.PreviousPrefix, StringComparison.Ordinal))
        {
            linkPart = id[PlanMessageBuilder.PreviousPrefix.Length..];
            step = -1;
        }
        else if (id.StartsWith(PlanMessageBuilder.NextPrefix, StringComparison.Ordinal))
        {
            linkPart = id[PlanMessageBuilder.NextPrefix.Length..];
            step = 1;
        }
        else return null;

        if (!Guid.TryParseExact(linkPart, "N", out var linkId) || ChannelOf(interaction) is not { } channelId)
            return Reply("That button is from an older version of this message.");
        _ = Task.Run(async () =>
        {
            try { await publisher.SwitchAsync(linkId, channelId, boardId, step, CancellationToken.None); }
            catch (Exception e) { log.LogWarning(e, "Switching board on Discord link {Link} failed", linkId); }
        }, CancellationToken.None);
        return Json(new JsonObject { ["type"] = 6 }); // deferred update: the message changes in a moment
    }

    private static string? ChannelOf(JsonElement interaction) =>
        interaction.TryGetProperty("channel_id", out var c) ? c.GetString()
        : interaction.TryGetProperty("channel", out var ch) && ch.TryGetProperty("id", out var id) ? id.GetString()
        : null;

    private static string? UserOf(JsonElement interaction) =>
        interaction.TryGetProperty("member", out var m) && m.TryGetProperty("user", out var u) ? u.GetProperty("id").GetString()
        : interaction.TryGetProperty("user", out var user) ? user.GetProperty("id").GetString()
        : null;

    private static string? Option(JsonElement sub, string name)
    {
        if (sub.ValueKind != JsonValueKind.Object || !sub.TryGetProperty("options", out var opts)) return null;
        foreach (var o in opts.EnumerateArray())
            if (o.GetProperty("name").GetString() == name) return o.GetProperty("value").GetString();
        return null;
    }

    /// <summary>"abcd 2345", "ABCD2345" and "ABCD-2345" are the same code.</summary>
    private static string? NormalizeCode(string? code)
    {
        if (code is null) return null;
        var chars = code.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray();
        return chars.Length == 8 ? new string(chars, 0, 4) + "-" + new string(chars, 4, 4) : null;
    }

    private ContentResult Reply(string text) => Json(new JsonObject
    {
        ["type"] = 4,
        ["data"] = new JsonObject
        {
            ["content"] = text,
            ["flags"] = Ephemeral,
            ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() }
        }
    });

    private ContentResult Json(JsonObject body) => Content(body.ToJsonString(), "application/json");
}

/// <summary>Registers the <c>/plan</c> command when the server starts with a Discord app configured, and
/// identifies the bot with the gateway, without which Discord won't let it post (<see cref="DiscordGateway"/>).</summary>
public sealed class DiscordCommandRegistrar(
    DiscordRest discord,
    DiscordGateway gateway,
    DiscordOptions options,
    ILogger<DiscordCommandRegistrar> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!options.BotConfigured) return;
        await RetryAsync("Registering the Discord /plan command", async () =>
        {
            await discord.UpsertGlobalCommandAsync(PlanCommand(), stop);
            log.LogInformation("Discord /plan command registered");
        }, stop);
        if (string.IsNullOrWhiteSpace(options.GatewayUrl)) return;
        await RetryAsync("Identifying the Discord bot with the gateway", async () =>
        {
            var name = await gateway.IdentifyAsync(stop);
            log.LogInformation("Discord bot identified with the gateway as {Name}", name);
        }, stop);
    }

    private async Task RetryAsync(string what, Func<Task> work, CancellationToken stop)
    {
        for (var attempt = 1; attempt <= 5 && !stop.IsCancellationRequested; attempt++)
        {
            try
            {
                await work();
                return;
            }
            catch (Exception e) when (e is DiscordApiException or DiscordGatewayException or HttpRequestException
                                          or System.Net.WebSockets.WebSocketException or OperationCanceledException
                                      && !stop.IsCancellationRequested)
            {
                log.LogWarning("{What} failed (attempt {Attempt}): {Message}", what, attempt, e.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(30 * attempt), stop); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private static JsonObject PlanCommand() => new()
    {
        ["name"] = "plan",
        ["type"] = 1,
        ["description"] = "Show a CloudLauncher pack's planning boards in this channel",
        // Manage Channels: linking puts a pack's plans in front of everyone here.
        ["default_member_permissions"] = "16",
        ["contexts"] = new JsonArray { 0 },
        ["integration_types"] = new JsonArray { 0 },
        ["options"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = 1, ["name"] = "link",
                ["description"] = "Show a pack's boards here, with the code from its planning page",
                ["options"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = 3, ["name"] = "code", ["required"] = true, ["min_length"] = 8, ["max_length"] = 12,
                        ["description"] = "The code CloudLauncher shows under Show in Discord"
                    }
                }
            },
            new JsonObject { ["type"] = 1, ["name"] = "unlink", ["description"] = "Stop showing planning boards in this channel" },
            new JsonObject { ["type"] = 1, ["name"] = "refresh", ["description"] = "Redraw the boards shown in this channel" }
        }
    };
}
