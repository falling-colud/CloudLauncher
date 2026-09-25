using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CloudLauncher.Services;

/// <summary>What a server answered a status ping with, or why it did not answer.</summary>
/// <remarks>A failure is a normal result, not an exception: servers in a player's list are often
/// off, and the Servers page shows that as an ordinary row state.</remarks>
public sealed record ServerPingResult
{
    /// <summary>True when the server answered a well-formed status response.</summary>
    public bool Online { get; init; }

    /// <summary>Why the ping failed, in words a player can act on. Null when <see cref="Online"/>.</summary>
    public string? Error { get; init; }

    /// <summary>The version string the server reports, e.g. "Paper 1.21.1". Free text, not a
    /// version number.</summary>
    public string? VersionName { get; init; }

    /// <summary>Protocol number, which is what actually decides whether a client can join.</summary>
    public int? Protocol { get; init; }

    public int PlayersOnline { get; init; }
    public int PlayersMax { get; init; }

    /// <summary>The handful of names the server chose to advertise. Usually capped at 12 by the server,
    /// often empty on servers that hide the sample, and never the full player list.</summary>
    public IReadOnlyList<string> PlayerSample { get; init; } = Array.Empty<string>();

    /// <summary>The MOTD, flattened to plain text with the legacy colour codes removed.</summary>
    public string? Motd { get; init; }

    /// <summary>The server icon as raw base64 PNG (the <c>data:</c> prefix already stripped), or
    /// null.</summary>
    public string? FaviconBase64 { get; init; }

    /// <summary>Round trip in milliseconds, from the ping packet when the server answers one and from
    /// the status exchange otherwise.</summary>
    public int? LatencyMs { get; init; }

    public static ServerPingResult Failed(string error) => new() { Online = false, Error = error };
}

/// <summary>The Minecraft Server List Ping, as used by the multiplayer screen for a server's MOTD,
/// player count, icon and latency.</summary>
/// <remarks>Only the modern (1.7+) JSON handshake; no instance this launcher builds could join a
/// pre-1.7 server. SRV records are not resolved (.NET has no built-in resolver), so hosts relying on
/// them fail to ping even though the game connects. The handshake sends protocol -1, since a real
/// number makes some servers answer with a version-mismatch MOTD.</remarks>
public static class MinecraftServerPing
{
    public const int DefaultPort = 25565;

    /// <summary>How long one server gets, in total, before it counts as unreachable. Long enough for a
    /// busy server on a slow link, short enough that a dead address does not hold up the sweep.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The longest favicon kept from a reply, in base64 characters. A 64x64 PNG is a few
    /// kilobytes, and a servers.dat entry cannot store more anyway (NBT strings stop at 64 KB), so
    /// anything longer is dropped.</summary>
    public const int MaxFaviconLength = 64 * 1024;

    /// <summary>The largest packet accepted, and so the largest status document. A status reply is a
    /// few kilobytes, a modded one listing its mods a few hundred.</summary>
    private const int MaxPacketBytes = 2 * 1024 * 1024;

    /// <summary>How much of a reply's text is kept. Far beyond what real servers send, but it keeps a
    /// reply the size of <see cref="MaxPacketBytes"/> out of a row's text and tooltip, which are laid
    /// out again on every change.</summary>
    private const int MaxMotdLength = 1024;
    private const int MaxNameLength = 256;
    private const int MaxSampleNames = 100;

    /// <summary>Splits a <c>servers.dat</c> address into host and port, defaulting the port to
    /// 25565.</summary>
    /// <remarks>Handles <c>[::1]:25565</c> and <c>host:port</c>, and treats a bare address with several
    /// colons as IPv6. A host containing whitespace, control characters, quotes or backslashes comes back
    /// empty ("not an address"): the address can come from someone else's servers.dat and ends up on the
    /// game's command line, where those characters could inject arguments.</remarks>
    public static (string Host, int Port) ParseAddress(string? address)
    {
        var (host, port) = SplitAddress((address ?? "").Trim());
        return IsPlausibleHost(host) ? (host, port) : ("", DefaultPort);
    }

    private static (string Host, int Port) SplitAddress(string text)
    {
        if (text.Length == 0) return ("", DefaultPort);

        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']');
            if (close > 0)
            {
                var h = text[1..close];
                var rest = text[(close + 1)..];
                if (rest.StartsWith(':') && int.TryParse(rest[1..], out var bracketPort) && InPortRange(bracketPort))
                    return (h, bracketPort);
                return (h, DefaultPort);
            }
        }

        var colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon
            && int.TryParse(text[(colon + 1)..], out var port) && InPortRange(port))
            return (text[..colon], port);

        return (text, DefaultPort);
    }

    /// <summary>False for a host containing anything no host name or IP literal can.</summary>
    private static bool IsPlausibleHost(string host)
    {
        foreach (var c in host)
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c is '"' or '\\') return false;
        return true;
    }

    private static bool InPortRange(int port) => port is > 0 and <= 65535;

    /// <summary>Formats a host and port back into the form <c>servers.dat</c> stores, leaving the
    /// default port off as the game does.</summary>
    public static string FormatAddress(string host, int port) =>
        port == DefaultPort ? host : host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";

    /// <summary>Pings the address as written in a server list entry.</summary>
    public static Task<ServerPingResult> PingAsync(string address, TimeSpan? timeout = null,
                                                   CancellationToken ct = default)
    {
        var (host, port) = ParseAddress(address);
        return PingAsync(host, port, timeout, ct);
    }

    /// <summary>Connects, asks for the status JSON, times a ping packet, and returns what came
    /// back.</summary>
    /// <remarks>Network failures (refused, DNS, wrong protocol, timeout) come back as an offline result
    /// with the reason; only cancellation throws, since a cancelled sweep is not a server being down. One
    /// deadline covers the whole exchange, so a stalling server costs at most the timeout.</remarks>
    public static async Task<ServerPingResult> PingAsync(string host, int port, TimeSpan? timeout = null,
                                                         CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host)) return ServerPingResult.Failed("No address");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? DefaultTimeout);
        var token = deadline.Token;

        using var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(host, port, token);
            var stream = client.GetStream();
            // Timed from here, after the connect: DNS and the TCP handshake are not what players mean by
            // latency, and on a cold cache they dwarf the round trip.
            var startedAt = Environment.TickCount64;

            await WritePacketAsync(stream, 0x00, body =>
            {
                WriteVarInt(body, -1);          // protocol version: "unknown", so any server replies
                WriteString(body, host);
                body.WriteByte((byte)(port >> 8));
                body.WriteByte((byte)port);
                WriteVarInt(body, 1);           // next state: status
            }, token);

            await WritePacketAsync(stream, 0x00, _ => { }, token);   // status request, empty body

            var (statusId, statusBody) = await ReadPacketAsync(stream, token);
            if (statusId != 0x00) return ServerPingResult.Failed("The server answered with something else");
            var json = ReadString(statusBody);
            var statusRoundTrip = (int)(Environment.TickCount64 - startedAt);

            var latency = await TimePingAsync(stream, token) ?? statusRoundTrip;
            return Parse(json, latency);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ServerPingResult.Failed("Timed out");
        }
        catch (SocketException ex)
        {
            return ServerPingResult.Failed(Describe(ex));
        }
        catch (IOException)
        {
            // Includes the server closing the socket mid-exchange, which is how some proxies refuse.
            return ServerPingResult.Failed("The connection was closed");
        }
        catch (InvalidDataException ex)
        {
            return ServerPingResult.Failed(ex.Message);
        }
        catch (JsonException)
        {
            return ServerPingResult.Failed("The server's status reply was not valid JSON");
        }
    }

    /// <summary>Times the optional 0x01 ping/pong. Returns null when the server does not answer one
    /// (several proxies and older plugins don't); the status round trip is used instead.</summary>
    private static async Task<int?> TimePingAsync(NetworkStream stream, CancellationToken ct)
    {
        try
        {
            var payload = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var sentAt = Environment.TickCount64;
            await WritePacketAsync(stream, 0x01, body =>
            {
                for (var shift = 56; shift >= 0; shift -= 8) body.WriteByte((byte)(payload >> shift));
            }, ct);
            var (id, _) = await ReadPacketAsync(stream, ct);
            return id == 0x01 ? (int)(Environment.TickCount64 - sentAt) : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException)
        {
            return null;
        }
    }

    private static string Describe(SocketException ex) => ex.SocketErrorCode switch
    {
        SocketError.ConnectionRefused => "Connection refused - nothing is listening on that port",
        SocketError.HostNotFound or SocketError.NoData => "That address does not resolve",
        SocketError.TimedOut => "Timed out",
        SocketError.NetworkUnreachable or SocketError.HostUnreachable => "Host unreachable",
        _ => ex.Message
    };

    // ── the status JSON ──────────────────────────────────────────────────────

    /// <summary>Pulls the fields the page shows out of the status document. Any field may be missing;
    /// modded and proxied servers often omit half of them.</summary>
    private static ServerPingResult Parse(string json, int latencyMs)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        // Valid JSON is not necessarily a status document, and TryGetProperty throws, rather than
        // returning false, on anything that is not an object.
        if (root.ValueKind != JsonValueKind.Object)
            return ServerPingResult.Failed("The server answered with something else");

        string? versionName = null;
        int? protocol = null;
        if (root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Object)
        {
            if (version.TryGetProperty("name", out var vn) && vn.ValueKind == JsonValueKind.String)
                versionName = Clip(StripFormatting(vn.GetString()), MaxNameLength);
            if (version.TryGetProperty("protocol", out var pv) && TryReadInt32(pv, out var p)) protocol = p;
        }

        var online = 0;
        var max = 0;
        var sample = new List<string>();
        if (root.TryGetProperty("players", out var players) && players.ValueKind == JsonValueKind.Object)
        {
            if (players.TryGetProperty("online", out var on) && TryReadInt32(on, out var o)) online = o;
            if (players.TryGetProperty("max", out var mx) && TryReadInt32(mx, out var m)) max = m;
            if (players.TryGetProperty("sample", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in list.EnumerateArray())
                {
                    if (sample.Count >= MaxSampleNames) break;
                    if (entry.ValueKind != JsonValueKind.Object) continue;
                    if (!entry.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) continue;
                    var name = Clip(StripFormatting(n.GetString()), MaxNameLength);
                    if (!string.IsNullOrWhiteSpace(name)) sample.Add(name);
                }
            }
        }

        string? motd = null;
        if (root.TryGetProperty("description", out var description))
            motd = StripFormatting(FlattenComponent(description));

        string? favicon = null;
        if (root.TryGetProperty("favicon", out var icon) && icon.ValueKind == JsonValueKind.String)
        {
            var value = icon.GetString() ?? "";
            var comma = value.IndexOf(',');
            // "data:image/png;base64,iVBOR...": everything before the comma is the data-URI preamble.
            favicon = comma >= 0 ? value[(comma + 1)..] : value;
            if (favicon.Length == 0 || favicon.Length > MaxFaviconLength) favicon = null;
        }

        return new ServerPingResult
        {
            Online = true,
            VersionName = versionName,
            Protocol = protocol,
            PlayersOnline = online,
            PlayersMax = max,
            PlayerSample = sample,
            Motd = string.IsNullOrWhiteSpace(motd) ? null : Clip(motd.Trim(), MaxMotdLength),
            FaviconBase64 = favicon,
            LatencyMs = latencyMs
        };
    }

    /// <summary>Cuts <paramref name="value"/> to at most <paramref name="max"/> characters without
    /// leaving half a surrogate pair at the end.</summary>
    private static string Clip(string value, int max)
    {
        if (value.Length <= max) return value;
        return value[..(char.IsHighSurrogate(value[max - 1]) ? max - 1 : max)];
    }

    /// <summary>Reads a status field that is supposed to be a number.</summary>
    /// <remarks>Keep the <see cref="JsonValueKind"/> check: <see cref="JsonElement.TryGetInt32"/> throws
    /// <see cref="InvalidOperationException"/> for a string, bool or null, and some plugins write
    /// <c>"online": "12"</c>. A numeric string is parsed as the number it means.</remarks>
    private static bool TryReadInt32(JsonElement element, out int value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return element.TryGetInt32(out value);
            case JsonValueKind.String:
                return int.TryParse(element.GetString(), NumberStyles.Integer,
                                    CultureInfo.InvariantCulture, out value);
            default:
                value = 0;
                return false;
        }
    }

    /// <summary>Flattens a chat component into plain text.</summary>
    /// <remarks>The MOTD may be a bare string, a component with <c>text</c> and nested <c>extra</c>, or an
    /// array of components, nested to any depth. Anything else (a <c>translate</c> key with no fallback,
    /// say) contributes nothing rather than leaking JSON into the UI.</remarks>
    private static string FlattenComponent(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString() ?? "";

            case JsonValueKind.Array:
            {
                var sb = new StringBuilder();
                foreach (var child in element.EnumerateArray()) sb.Append(FlattenComponent(child));
                return sb.ToString();
            }

            case JsonValueKind.Object:
            {
                var sb = new StringBuilder();
                if (element.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    sb.Append(text.GetString());
                if (element.TryGetProperty("extra", out var extra) && extra.ValueKind == JsonValueKind.Array)
                    foreach (var child in extra.EnumerateArray()) sb.Append(FlattenComponent(child));
                return sb.ToString();
            }

            default:
                return "";
        }
    }

    /// <summary>
    /// Removes the legacy section-sign formatting codes so a MOTD reads as text.
    /// </summary>
    /// <remarks>Servers still put <c>§a</c>-style codes inside the JSON. A code is always the section
    /// sign plus one character, so this is a two-character skip, not a general parser.</remarks>
    public static string StripFormatting(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.IndexOf('§') < 0) return value;

        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '§')
            {
                i++;    // and skip the code character with it
                continue;
            }
            sb.Append(value[i]);
        }
        return sb.ToString();
    }

    // ── packet framing ───────────────────────────────────────────────────────
    //
    // Every packet is: VarInt total length, VarInt packet id, payload. A VarInt is seven bits per
    // byte, low group first, with the high bit meaning "another byte follows".

    private static async Task WritePacketAsync(Stream stream, int packetId, Action<MemoryStream> writeBody,
                                               CancellationToken ct)
    {
        using var body = new MemoryStream();
        WriteVarInt(body, packetId);
        writeBody(body);

        using var packet = new MemoryStream();
        WriteVarInt(packet, (int)body.Length);
        body.Position = 0;
        await body.CopyToAsync(packet, ct);

        packet.Position = 0;
        await packet.CopyToAsync(stream, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>Reads one packet and returns its id together with a stream positioned at its payload.</summary>
    private static async Task<(int Id, MemoryStream Body)> ReadPacketAsync(Stream stream, CancellationToken ct)
    {
        var length = await ReadVarIntAsync(stream, ct);
        // Past MaxPacketBytes this is not a Minecraft server, and we won't allocate whatever size the peer
        // sends. Negative covers a VarInt whose fifth byte set the sign bit.
        if (length is <= 0 or > MaxPacketBytes)
            throw new InvalidDataException("The server sent a reply that is not from a Minecraft server");

        var buffer = new byte[length];
        await ReadExactlyAsync(stream, buffer, ct);

        var body = new MemoryStream(buffer, writable: false);
        var id = ReadVarInt(body);
        return (id, body);
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (read <= 0) throw new EndOfStreamException();
            offset += read;
        }
    }

    private static void WriteVarInt(Stream to, int value)
    {
        var unsigned = unchecked((uint)value);
        while (true)
        {
            if ((unsigned & ~0x7Fu) == 0) { to.WriteByte((byte)unsigned); return; }
            to.WriteByte((byte)((unsigned & 0x7F) | 0x80));
            unsigned >>= 7;
        }
    }

    private static int ReadVarInt(Stream from)
    {
        var result = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            var b = from.ReadByte();
            if (b < 0) throw new EndOfStreamException();
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
        }
        throw new InvalidDataException("Malformed VarInt");
    }

    /// <summary>The async twin of <see cref="ReadVarInt"/>, for the packet length prefix: the one
    /// VarInt that has to come off the socket before its packet's size is known.</summary>
    private static async Task<int> ReadVarIntAsync(Stream from, CancellationToken ct)
    {
        var one = new byte[1];
        var result = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            var read = await from.ReadAsync(one.AsMemory(0, 1), ct);
            if (read <= 0) throw new EndOfStreamException();
            result |= (one[0] & 0x7F) << shift;
            if ((one[0] & 0x80) == 0) return result;
        }
        throw new InvalidDataException("Malformed VarInt");
    }

    private static void WriteString(Stream to, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarInt(to, bytes.Length);
        to.Write(bytes, 0, bytes.Length);
    }

    private static string ReadString(Stream from)
    {
        var length = ReadVarInt(from);
        // Checked against what the packet actually holds before anything is allocated for it.
        if (length < 0 || length > MaxPacketBytes || (from.CanSeek && length > from.Length - from.Position))
            throw new InvalidDataException("Malformed string");
        var bytes = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = from.Read(bytes, offset, length - offset);
            if (read <= 0) throw new EndOfStreamException();
            offset += read;
        }
        return Encoding.UTF8.GetString(bytes);
    }
}
