namespace CloudLauncher.Server.Discord;

/// <summary>
/// The "Discord" section: the app that posts planning boards to Discord channels.
/// </summary>
/// <remarks>
/// Channels linked through a webhook need none of the app settings; the publisher posts to them
/// either way. <c>/plan link</c>, the board buttons and the Activity need the app: its id, public key
/// (to check that an interaction really came from Discord) and bot token.
/// </remarks>
public sealed class DiscordOptions
{
    public string ApiBase { get; set; } = "https://discord.com/api/v10";

    /// <summary>Where the bot identifies once when the server starts (<see cref="DiscordGateway"/>);
    /// empty skips that, for test runs against a stand-in API.</summary>
    public string? GatewayUrl { get; set; } = "wss://gateway.discord.gg/?v=10&encoding=json";

    public string? ApplicationId { get; set; }

    /// <summary>The app's public key, hex, from the Developer Portal's General Information page.</summary>
    public string? PublicKey { get; set; }

    public string? BotToken { get; set; }

    /// <summary>The app's Activity is set up (its URL mapping points at this server's <c>/discord-activity/</c>),
    /// so messages posted by the app get an "Open board" button that opens it inside Discord.</summary>
    public bool ActivityEnabled { get; set; }

    /// <summary>How long a board must be left alone before a change is posted, so dragging a card
    /// around is one edit rather than twenty.</summary>
    public int QuietSeconds { get; set; } = 8;

    /// <summary>The least time between two updates of the same channel.</summary>
    public int MinIntervalSeconds { get; set; } = 15;

    /// <summary>Off stops all posting (links stay as they are).</summary>
    public bool PublisherEnabled { get; set; } = true;

    public bool BotConfigured =>
        !string.IsNullOrWhiteSpace(ApplicationId)
        && !string.IsNullOrWhiteSpace(PublicKey)
        && !string.IsNullOrWhiteSpace(BotToken);

    /// <summary>View Channel, Send Messages, Embed Links, Attach Files, Read Message History.</summary>
    public const long BotPermissions = 1024 + 2048 + 16384 + 32768 + 65536;

    /// <summary>Adds the app to a server with the permissions it posts boards with.</summary>
    public string? InviteUrl => string.IsNullOrWhiteSpace(ApplicationId)
        ? null
        : $"https://discord.com/oauth2/authorize?client_id={ApplicationId}&scope=bot%20applications.commands&permissions={BotPermissions}";
}
