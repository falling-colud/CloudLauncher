using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CloudLauncher.Server.Discord;

/// <summary>The gateway refused the bot, or answered something other than the identify handshake.</summary>
public sealed class DiscordGatewayException(string message) : Exception(message);

/// <summary>
/// One short visit to Discord's gateway: connect, identify as the app's bot, hang up.
/// </summary>
/// <remarks>
/// Discord lets a bot create messages only once it has identified with the gateway at least once.
/// The boards go out over REST alone and the app wants no events, so this stands in for the
/// connection an ordinary bot keeps open: no intents, and the bot shows online for a moment.
/// </remarks>
public sealed class DiscordGateway(DiscordOptions options)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Identifies and closes again; answers the bot's user name.</summary>
    public async Task<string?> IdentifyAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.BotToken))
            throw new InvalidOperationException("Discord:BotToken is not set");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("User-Agent", DiscordRest.UserAgent);
        await ws.ConnectAsync(new Uri(options.GatewayUrl!), timeout.Token);

        var hello = await ReceiveAsync(ws, timeout.Token);
        if (Op(hello) != 10) throw new DiscordGatewayException($"Discord's gateway opened with op {Op(hello)}, not HELLO");

        await SendAsync(ws, new JsonObject
        {
            ["op"] = 2,
            ["d"] = new JsonObject
            {
                ["token"] = options.BotToken,
                ["intents"] = 0,
                ["properties"] = new JsonObject
                {
                    ["os"] = "linux", ["browser"] = "CloudLauncher", ["device"] = "CloudLauncher"
                }
            }
        }, timeout.Token);

        while (true)
        {
            var message = await ReceiveAsync(ws, timeout.Token);
            switch (Op(message))
            {
                case 0 when message["t"]?.GetValue<string>() == "READY":
                    var user = message["d"]?["user"];
                    try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token); }
                    catch (Exception e) when (e is WebSocketException or OperationCanceledException) { /* identified already */ }
                    return user?["username"]?.GetValue<string>();
                case 1: // Discord asks for a heartbeat now
                    await SendAsync(ws, new JsonObject { ["op"] = 1, ["d"] = null }, timeout.Token);
                    break;
                case 7:
                    throw new DiscordGatewayException("Discord's gateway asked to reconnect before the bot was identified");
                case 9:
                    throw new DiscordGatewayException("Discord's gateway refused the identify (invalid session)");
            }
        }
    }

    private static int Op(JsonNode message) => message["op"]?.GetValue<int>() ?? -1;

    private static Task SendAsync(ClientWebSocket ws, JsonObject payload, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(payload.ToJsonString()), WebSocketMessageType.Text, true, ct);

    /// <summary>The next whole message; a close from Discord (4004: the token is wrong) is an error.</summary>
    private static async Task<JsonNode> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new DiscordGatewayException(
                    $"Discord's gateway closed the connection: {(int?)result.CloseStatus} {result.CloseStatusDescription}");
            message.Write(buffer, 0, result.Count);
            if (message.Length > 4 * 1024 * 1024) throw new DiscordGatewayException("Discord's gateway sent an oversized message");
            if (result.EndOfMessage) break;
        }
        return JsonNode.Parse(message.ToArray())
               ?? throw new DiscordGatewayException("Discord's gateway sent an empty message");
    }
}
