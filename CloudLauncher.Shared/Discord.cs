namespace CloudLauncher.Shared;

/// <summary>
/// A Discord channel that shows a pack's planning boards: one message per board, a picture of the
/// board plus how far along each section is, edited in place whenever the board changes.
/// </summary>
/// <param name="Mode"><c>"webhook"</c> (posted through a channel webhook someone pasted in) or
/// <c>"bot"</c> (posted by the CloudLauncher app, linked with <c>/plan link</c> in the channel).</param>
/// <param name="State"><c>"pending"</c> (a bot link waiting for its code to be used in a channel),
/// <c>"active"</c>, or <c>"broken"</c> (the channel or webhook is gone; see <paramref name="LastError"/>).</param>
/// <param name="BoardId">The one board this channel shows, or null for every board.</param>
/// <param name="Label">What to call the channel: the webhook's name, or the channel's.</param>
/// <param name="Code">For a pending bot link: what to type after <c>/plan link code:</c>.</param>
/// <param name="ViewUrl">A web page with the live boards, zoomable. The posted messages link to it.</param>
public sealed record PackDiscordLinkDto(
    Guid Id,
    string Mode,
    string State,
    string? BoardId,
    string? BoardName,
    string? GuildId,
    string? ChannelId,
    string? Label,
    string? Code,
    DateTimeOffset? CodeExpiresAt,
    string? ViewUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PublishedAt,
    string? LastError);

/// <summary>A pack's Discord links, and whether this server can link through its own app.</summary>
/// <param name="BotAvailable">The server has a Discord app set up, so <c>/plan link</c> works.</param>
/// <param name="BotInviteUrl">Adds that app to a server, with the permissions it needs.</param>
public sealed record PackDiscordInfo(bool BotAvailable, string? BotInviteUrl, IReadOnlyList<PackDiscordLinkDto> Links);

/// <summary>Links a pack's planning boards to a Discord channel.</summary>
/// <param name="WebhookUrl">A channel webhook's URL (channel settings, Integrations, Webhooks). The
/// boards go up at once. Null to link through the CloudLauncher app instead: the answer then carries a
/// code to use with <c>/plan link</c> in the channel.</param>
/// <param name="BoardId">Only this board; null for all of them.</param>
public sealed record CreatePackDiscordLinkRequest(string? WebhookUrl, string? BoardId);
