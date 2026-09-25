using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudLauncher.Services;

/// <summary>One entry of <c>ops.json</c>.</summary>
/// <remarks>Property names match what the server writes. The server reads this file, and a renamed
/// field would silently drop the op.</remarks>
public sealed class ServerOpEntry
{
    [JsonPropertyName("uuid")] public string Uuid { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>1 bypasses spawn protection, 2 adds the single-player cheat commands, 3 adds player
    /// management, 4 adds stop/op/deop. 4 is what <c>/op</c> from the console gives.</summary>
    [JsonPropertyName("level")] public int Level { get; set; } = 4;

    [JsonPropertyName("bypassesPlayerLimit")] public bool BypassesPlayerLimit { get; set; }
}

/// <summary>One entry of <c>whitelist.json</c>.</summary>
public sealed class ServerWhitelistEntry
{
    [JsonPropertyName("uuid")] public string Uuid { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

/// <summary>One entry of <c>banned-players.json</c>.</summary>
public sealed class ServerBannedPlayerEntry
{
    [JsonPropertyName("uuid")] public string Uuid { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>When the ban was issued, in Minecraft's own format
    /// (<c>2026-09-21 14:03:11 +0200</c>). Kept as text rather than a DateTimeOffset so a file written
    /// by a server in another locale round-trips unchanged.</summary>
    [JsonPropertyName("created")] public string Created { get; set; } = "";

    /// <summary>Who issued it. The console writes "Server".</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = "Server";

    /// <summary>An expiry in the same format as <see cref="Created"/>, or the literal "forever".</summary>
    [JsonPropertyName("expires")] public string Expires { get; set; } = "forever";

    [JsonPropertyName("reason")] public string Reason { get; set; } = "Banned by an operator.";
}

/// <summary>One entry of <c>banned-ips.json</c>. The same shape as a player ban with an address
/// instead of a uuid/name pair.</summary>
public sealed class ServerBannedIpEntry
{
    [JsonPropertyName("ip")] public string Ip { get; set; } = "";
    [JsonPropertyName("created")] public string Created { get; set; } = "";
    [JsonPropertyName("source")] public string Source { get; set; } = "Server";
    [JsonPropertyName("expires")] public string Expires { get; set; } = "forever";
    [JsonPropertyName("reason")] public string Reason { get; set; } = "Banned by an operator.";
}

/// <summary>The console commands equivalent to editing one of these files.</summary>
/// <remarks>A running server keeps ops, the whitelist and both ban lists in memory and writes them
/// on stop, so file edits made while it runs are lost (and wouldn't apply until a restart anyway).
/// Edit the files while it is stopped and send commands while it runs; the caller decides from
/// <c>ServerHostService.GetStatus(packId)</c>.</remarks>
public static class ServerAccessCommands
{
    public static string Op(string name) => $"op {Sanitize(name)}";
    public static string Deop(string name) => $"deop {Sanitize(name)}";
    public static string WhitelistAdd(string name) => $"whitelist add {Sanitize(name)}";
    public static string WhitelistRemove(string name) => $"whitelist remove {Sanitize(name)}";
    public static string WhitelistOn() => "whitelist on";
    public static string WhitelistOff() => "whitelist off";
    public static string WhitelistReload() => "whitelist reload";

    public static string Ban(string name, string? reason = null) =>
        string.IsNullOrWhiteSpace(reason) ? $"ban {Sanitize(name)}" : $"ban {Sanitize(name)} {Sanitize(reason)}";

    public static string Pardon(string name) => $"pardon {Sanitize(name)}";

    public static string BanIp(string ip, string? reason = null) =>
        string.IsNullOrWhiteSpace(reason) ? $"ban-ip {Sanitize(ip)}" : $"ban-ip {Sanitize(ip)} {Sanitize(reason)}";

    public static string PardonIp(string ip) => $"pardon-ip {Sanitize(ip)}";

    /// <summary>A command is one line; a newline in an argument would be a second command the user did
    /// not type. Collapse anything that could split the line.</summary>
    private static string Sanitize(string value) =>
        (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
}

/// <summary>Reads and writes a server's access lists: <c>ops.json</c>, <c>whitelist.json</c>,
/// <c>banned-players.json</c> and <c>banned-ips.json</c>.</summary>
/// <remarks>
/// <para>Each is a JSON array in the server's shape, always written whole. A file that doesn't parse
/// reads as empty instead of throwing, so a stray trailing comma can't stop the hosting page
/// opening.</para>
/// <para>No network here, since adding someone must work offline. <see cref="LookupUuidAsync"/> is
/// a separate best-effort call that returns null on any failure.</para>
/// </remarks>
public static class ServerAccessListService
{
    public const string OpsFileName = "ops.json";
    public const string WhitelistFileName = "whitelist.json";
    public const string BannedPlayersFileName = "banned-players.json";
    public const string BannedIpsFileName = "banned-ips.json";

    /// <summary>Indented, because a server admin opens these files by hand and Minecraft writes them
    /// indented too. Unicode is left unescaped for the same reason.</summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly HttpClient Http = ApiClient.WithUserAgent(new() { Timeout = TimeSpan.FromSeconds(8) });

    // ── ops ──────────────────────────────────────────────────────────────────

    public static IReadOnlyList<ServerOpEntry> ReadOps(string serverRunDir) =>
        ReadList<ServerOpEntry>(Path.Combine(serverRunDir, OpsFileName));

    public static void WriteOps(string serverRunDir, IEnumerable<ServerOpEntry> entries) =>
        WriteList(Path.Combine(serverRunDir, OpsFileName), entries);

    /// <summary>Adds or updates an op. Returns the list as it now stands on disk.</summary>
    /// <remarks>Matching on name, case-insensitively, because that is what the user typed and what the
    /// file shows; a uuid is filled in when one is known and left alone when it is not.</remarks>
    public static IReadOnlyList<ServerOpEntry> AddOp(
        string serverRunDir, string name, Guid? uuid = null, int level = 4, bool bypassesPlayerLimit = false)
    {
        var list = ReadOps(serverRunDir).ToList();
        var entry = list.FirstOrDefault(e => NameMatches(e.Name, name));
        if (entry is null)
        {
            entry = new ServerOpEntry { Name = name.Trim() };
            list.Add(entry);
        }
        entry.Name = name.Trim();
        if (uuid is { } id) entry.Uuid = FormatUuid(id);
        entry.Level = Math.Clamp(level, 1, 4);
        entry.BypassesPlayerLimit = bypassesPlayerLimit;
        WriteOps(serverRunDir, list);
        return list;
    }

    public static IReadOnlyList<ServerOpEntry> RemoveOp(string serverRunDir, string name)
    {
        var list = ReadOps(serverRunDir).Where(e => !NameMatches(e.Name, name)).ToList();
        WriteOps(serverRunDir, list);
        return list;
    }

    // ── whitelist ────────────────────────────────────────────────────────────

    public static IReadOnlyList<ServerWhitelistEntry> ReadWhitelist(string serverRunDir) =>
        ReadList<ServerWhitelistEntry>(Path.Combine(serverRunDir, WhitelistFileName));

    public static void WriteWhitelist(string serverRunDir, IEnumerable<ServerWhitelistEntry> entries) =>
        WriteList(Path.Combine(serverRunDir, WhitelistFileName), entries);

    public static IReadOnlyList<ServerWhitelistEntry> AddToWhitelist(string serverRunDir, string name, Guid? uuid = null)
    {
        var list = ReadWhitelist(serverRunDir).ToList();
        var entry = list.FirstOrDefault(e => NameMatches(e.Name, name));
        if (entry is null)
        {
            entry = new ServerWhitelistEntry { Name = name.Trim() };
            list.Add(entry);
        }
        entry.Name = name.Trim();
        if (uuid is { } id) entry.Uuid = FormatUuid(id);
        WriteWhitelist(serverRunDir, list);
        return list;
    }

    public static IReadOnlyList<ServerWhitelistEntry> RemoveFromWhitelist(string serverRunDir, string name)
    {
        var list = ReadWhitelist(serverRunDir).Where(e => !NameMatches(e.Name, name)).ToList();
        WriteWhitelist(serverRunDir, list);
        return list;
    }

    // ── banned players ───────────────────────────────────────────────────────

    public static IReadOnlyList<ServerBannedPlayerEntry> ReadBannedPlayers(string serverRunDir) =>
        ReadList<ServerBannedPlayerEntry>(Path.Combine(serverRunDir, BannedPlayersFileName));

    public static void WriteBannedPlayers(string serverRunDir, IEnumerable<ServerBannedPlayerEntry> entries) =>
        WriteList(Path.Combine(serverRunDir, BannedPlayersFileName), entries);

    public static IReadOnlyList<ServerBannedPlayerEntry> BanPlayer(
        string serverRunDir, string name, Guid? uuid = null, string? reason = null,
        DateTimeOffset? expires = null, string source = "CloudLauncher")
    {
        var list = ReadBannedPlayers(serverRunDir).ToList();
        var entry = list.FirstOrDefault(e => NameMatches(e.Name, name));
        if (entry is null)
        {
            entry = new ServerBannedPlayerEntry { Name = name.Trim(), Created = FormatTimestamp(DateTimeOffset.Now) };
            list.Add(entry);
        }
        entry.Name = name.Trim();
        if (uuid is { } id) entry.Uuid = FormatUuid(id);
        entry.Source = source;
        entry.Expires = expires is { } e ? FormatTimestamp(e) : "forever";
        if (!string.IsNullOrWhiteSpace(reason)) entry.Reason = reason.Trim();
        WriteBannedPlayers(serverRunDir, list);
        return list;
    }

    public static IReadOnlyList<ServerBannedPlayerEntry> PardonPlayer(string serverRunDir, string name)
    {
        var list = ReadBannedPlayers(serverRunDir).Where(e => !NameMatches(e.Name, name)).ToList();
        WriteBannedPlayers(serverRunDir, list);
        return list;
    }

    // ── banned IPs ───────────────────────────────────────────────────────────

    public static IReadOnlyList<ServerBannedIpEntry> ReadBannedIps(string serverRunDir) =>
        ReadList<ServerBannedIpEntry>(Path.Combine(serverRunDir, BannedIpsFileName));

    public static void WriteBannedIps(string serverRunDir, IEnumerable<ServerBannedIpEntry> entries) =>
        WriteList(Path.Combine(serverRunDir, BannedIpsFileName), entries);

    public static IReadOnlyList<ServerBannedIpEntry> BanIp(
        string serverRunDir, string ip, string? reason = null,
        DateTimeOffset? expires = null, string source = "CloudLauncher")
    {
        var list = ReadBannedIps(serverRunDir).ToList();
        var entry = list.FirstOrDefault(e => string.Equals(e.Ip, ip?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            entry = new ServerBannedIpEntry { Ip = (ip ?? "").Trim(), Created = FormatTimestamp(DateTimeOffset.Now) };
            list.Add(entry);
        }
        entry.Source = source;
        entry.Expires = expires is { } e ? FormatTimestamp(e) : "forever";
        if (!string.IsNullOrWhiteSpace(reason)) entry.Reason = reason.Trim();
        WriteBannedIps(serverRunDir, list);
        return list;
    }

    public static IReadOnlyList<ServerBannedIpEntry> PardonIp(string serverRunDir, string ip)
    {
        var list = ReadBannedIps(serverRunDir)
            .Where(e => !string.Equals(e.Ip, ip?.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        WriteBannedIps(serverRunDir, list);
        return list;
    }

    // ── uuid ─────────────────────────────────────────────────────────────────

    /// <summary>Mojang's uuid for a player name, or null on any failure (no network, no such player,
    /// rate limited).</summary>
    /// <remarks>The lists work fine with an empty uuid on an offline-mode server, so nothing depends on
    /// Mojang being reachable.</remarks>
    public static async Task<Guid?> LookupUuidAsync(string name, CancellationToken ct = default)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) return null;
        try
        {
            using var resp = await Http.GetAsync(
                $"https://api.mojang.com/users/profiles/minecraft/{Uri.EscapeDataString(trimmed)}", ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("id", out var idEl)) return null;
            var raw = idEl.GetString();
            return Guid.TryParseExact(raw, "N", out var id) ? id : null;
        }
        catch (Exception ex)
        {
            AppLog.Log("server-access", $"Could not look up a uuid for '{trimmed}': {ex.Message}");
            return null;
        }
    }

    /// <summary>The dashed, lower-case spelling Minecraft writes into these files.</summary>
    public static string FormatUuid(Guid id) => id.ToString("D").ToLowerInvariant();

    /// <summary>Minecraft's timestamp format, e.g. <c>2026-09-21 14:03:11 +0200</c>. The offset is
    /// written without its colon and with an explicit sign, which a custom TimeSpan format will not
    /// produce on its own.</summary>
    public static string FormatTimestamp(DateTimeOffset at) =>
        at.ToString("yyyy-MM-dd HH:mm:ss ", CultureInfo.InvariantCulture)
        + (at.Offset < TimeSpan.Zero ? "-" : "+")
        + at.Offset.ToString("hhmm", CultureInfo.InvariantCulture);

    // ── file layer ───────────────────────────────────────────────────────────

    private static bool NameMatches(string? a, string? b) =>
        string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<T> ReadList<T>(string path)
    {
        try
        {
            if (!File.Exists(path)) return Array.Empty<T>();
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<T>();
            return JsonSerializer.Deserialize<List<T>>(text, JsonOpts) ?? new List<T>();
        }
        catch (Exception ex)
        {
            AppLog.Log("server-access", $"Could not read {Path.GetFileName(path)}: {ex.Message}");
            return Array.Empty<T>();
        }
    }

    /// <summary>Writes the list through a temp file in the same folder, so an interrupted save cannot
    /// leave a half-written whitelist, which a server reads as empty.</summary>
    private static void WriteList<T>(string path, IEnumerable<T> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(entries.ToList(), JsonOpts);
        var tmp = path + ".cl-tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }
}
