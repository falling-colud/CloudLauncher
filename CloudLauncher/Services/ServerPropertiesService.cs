using System.Globalization;
using System.IO;
using System.Text;

namespace CloudLauncher.Services;

/// <summary>One line of a <c>.properties</c> file: either a key/value pair or something we only
/// have to give back unchanged (a comment, a blank line, a line we could not read as a pair).</summary>
/// <remarks><see cref="Raw"/> is written back until the value changes, so an untouched file comes
/// back byte-for-byte and only edited lines are rewritten.</remarks>
public sealed class ServerPropertyLine
{
    /// <summary>The line as read, without its terminator. Rewritten only by <see cref="SetValue"/>.</summary>
    public string Raw { get; private set; } = "";

    /// <summary>The line terminator this line ended with ("\r\n", "\n", or "" for the last line of a
    /// file that does not end in a newline).</summary>
    public string Newline { get; internal set; } = "";

    /// <summary>The unescaped key, or null for a comment/blank/unparseable line.</summary>
    public string? Key { get; private set; }

    /// <summary>The unescaped value. Null when <see cref="Key"/> is null.</summary>
    public string? Value { get; private set; }

    /// <summary>The key as written in the file plus its separator (<c>"level-name="</c>,
    /// <c>"motd : "</c>), kept verbatim so setting a value doesn't restyle the key or drop an
    /// escape.</summary>
    private string _prefix = "";

    public bool IsPair => Key is not null;

    internal static ServerPropertyLine Comment(string raw, string newline) =>
        new() { Raw = raw, Newline = newline };

    internal static ServerPropertyLine Pair(string raw, string newline, string prefix, string key, string value) =>
        new() { Raw = raw, Newline = newline, _prefix = prefix, Key = key, Value = value };

    /// <summary>Rewrites this line with a new value, keeping its key spelling and separator.</summary>
    internal void SetValue(string value)
    {
        if (Key is null) throw new InvalidOperationException("This line has no key.");
        if (string.Equals(Value, value, StringComparison.Ordinal)) return;
        Value = value;
        Raw = _prefix + ServerPropertiesService.EscapeValue(value);
    }
}

/// <summary>A parsed <c>server.properties</c>: the file's lines in order, plus typed access to the
/// keys a hosting UI needs.</summary>
/// <remarks>Unknown keys, comments, blank lines and key order all survive a round trip, since mods
/// and people add their own settings to this file.</remarks>
public sealed class ServerProperties
{
    private readonly List<ServerPropertyLine> _lines = new();

    /// <summary>Key -> the line that currently defines it. Last definition wins, as in Java's own
    /// Properties loader.</summary>
    private readonly Dictionary<string, ServerPropertyLine> _index = new(StringComparer.Ordinal);

    /// <summary>True once anything has been changed since the file was read.</summary>
    public bool IsDirty { get; private set; }

    /// <summary>The file's lines in order, for a raw/advanced view.</summary>
    public IReadOnlyList<ServerPropertyLine> Lines => _lines;

    /// <summary>Every key/value pair in file order.</summary>
    public IEnumerable<KeyValuePair<string, string>> Pairs =>
        _lines.Where(l => l.IsPair).Select(l => new KeyValuePair<string, string>(l.Key!, l.Value!));

    public bool Contains(string key) => _index.ContainsKey(key);

    /// <summary>The raw string value for a key, or <paramref name="fallback"/> when it is absent.</summary>
    public string Get(string key, string fallback = "") =>
        _index.TryGetValue(key, out var line) ? line.Value! : fallback;

    /// <summary>Sets a key, rewriting its existing line in place or appending a new one at the end.</summary>
    /// <remarks>New keys go at the end, where guides tell people to add them. Setting a key to the value
    /// it already has does nothing, so writing a whole form back still gives a one-line diff.</remarks>
    public void Set(string key, string value)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("A property needs a key.", nameof(key));
        value ??= "";
        if (_index.TryGetValue(key, out var line))
        {
            if (string.Equals(line.Value, value, StringComparison.Ordinal)) return;
            line.SetValue(value);
            IsDirty = true;
            return;
        }

        // A file that does not end in a newline would otherwise get the new key glued onto its last
        // line. Give the previous last line the file's own ending first.
        if (_lines.Count > 0 && _lines[^1].Newline.Length == 0)
            _lines[^1].Newline = Newline;

        var prefix = ServerPropertiesService.EscapeKey(key) + "=";
        var added = ServerPropertyLine.Pair(prefix + ServerPropertiesService.EscapeValue(value), Newline, prefix, key, value);
        _lines.Add(added);
        _index[key] = added;
        IsDirty = true;
    }

    /// <summary>Removes a key and its line. Minecraft writes a missing key back with its default on the
    /// next start.</summary>
    public bool Remove(string key)
    {
        if (!_index.TryGetValue(key, out var line)) return false;
        _index.Remove(key);
        _lines.Remove(line);
        IsDirty = true;
        return true;
    }

    /// <summary>The line ending new lines get. Detected from the file; CRLF for one with none.</summary>
    public string Newline { get; internal set; } = "\r\n";

    internal void Add(ServerPropertyLine line)
    {
        _lines.Add(line);
        if (line.Key is not null) _index[line.Key] = line;
    }

    internal void MarkClean() => IsDirty = false;

    /// <summary>The whole file as text, ready for <see cref="TextFileService.Write"/>.</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        foreach (var line in _lines) sb.Append(line.Raw).Append(line.Newline);
        return sb.ToString();
    }

    // ── typed view ───────────────────────────────────────────────────────────
    // Fallbacks are the vanilla defaults, so a form filled from a file missing some keys still shows
    // what the server will do.

    public int    ServerPort            { get => GetInt("server-port", 25565);             set => SetInt("server-port", value); }
    public string Motd                  { get => Get("motd", "A Minecraft Server");        set => Set("motd", value); }
    public string Gamemode              { get => Get("gamemode", "survival");              set => Set("gamemode", value); }
    public string Difficulty            { get => Get("difficulty", "easy");                set => Set("difficulty", value); }
    public bool   Hardcore              { get => GetBool("hardcore", false);               set => SetBool("hardcore", value); }
    public bool   Pvp                   { get => GetBool("pvp", true);                     set => SetBool("pvp", value); }
    public int    MaxPlayers            { get => GetInt("max-players", 20);                set => SetInt("max-players", value); }
    public int    ViewDistance          { get => GetInt("view-distance", 10);              set => SetInt("view-distance", value); }
    public int    SimulationDistance    { get => GetInt("simulation-distance", 10);        set => SetInt("simulation-distance", value); }
    public bool   OnlineMode            { get => GetBool("online-mode", true);             set => SetBool("online-mode", value); }
    public bool   WhiteList             { get => GetBool("white-list", false);             set => SetBool("white-list", value); }
    public bool   EnforceWhitelist      { get => GetBool("enforce-whitelist", false);      set => SetBool("enforce-whitelist", value); }
    public bool   AllowFlight           { get => GetBool("allow-flight", false);           set => SetBool("allow-flight", value); }
    public bool   AllowNether           { get => GetBool("allow-nether", true);            set => SetBool("allow-nether", value); }
    public int    SpawnProtection       { get => GetInt("spawn-protection", 16);           set => SetInt("spawn-protection", value); }
    public string LevelName             { get => Get("level-name", "world");               set => Set("level-name", value); }
    public string LevelSeed             { get => Get("level-seed", "");                    set => Set("level-seed", value); }
    public bool   EnableCommandBlock    { get => GetBool("enable-command-block", false);   set => SetBool("enable-command-block", value); }
    public bool   EnableRcon            { get => GetBool("enable-rcon", false);            set => SetBool("enable-rcon", value); }
    public int    RconPort              { get => GetInt("rcon.port", 25575);               set => SetInt("rcon.port", value); }
    public string RconPassword          { get => Get("rcon.password", "");                 set => Set("rcon.password", value); }
    public bool   EnableQuery           { get => GetBool("enable-query", false);           set => SetBool("enable-query", value); }
    public int    QueryPort             { get => GetInt("query.port", 25565);              set => SetInt("query.port", value); }
    public int    PlayerIdleTimeout     { get => GetInt("player-idle-timeout", 0);         set => SetInt("player-idle-timeout", value); }
    public long   MaxWorldSize          { get => GetLong("max-world-size", 29999984);      set => SetLong("max-world-size", value); }
    public string ResourcePack          { get => Get("resource-pack", "");                 set => Set("resource-pack", value); }
    public string ResourcePackSha1      { get => Get("resource-pack-sha1", "");            set => Set("resource-pack-sha1", value); }
    public bool   RequireResourcePack   { get => GetBool("require-resource-pack", false);  set => SetBool("require-resource-pack", value); }

    /// <summary>A value Minecraft reads as true. Anything other than "true" is false to the server, so
    /// it is read the same way here.</summary>
    public bool GetBool(string key, bool fallback) =>
        _index.TryGetValue(key, out var line)
            ? string.Equals(line.Value, "true", StringComparison.OrdinalIgnoreCase)
            : fallback;

    public void SetBool(string key, bool value) => Set(key, value ? "true" : "false");

    public int GetInt(string key, int fallback) =>
        _index.TryGetValue(key, out var line) && int.TryParse(line.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : fallback;

    public void SetInt(string key, int value) => Set(key, value.ToString(CultureInfo.InvariantCulture));

    public long GetLong(string key, long fallback) =>
        _index.TryGetValue(key, out var line) && long.TryParse(line.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : fallback;

    public void SetLong(string key, long value) => Set(key, value.ToString(CultureInfo.InvariantCulture));
}

/// <summary>A parsed file plus what it takes to write it back unchanged.</summary>
public sealed record ServerPropertiesFile(string Path, ServerProperties Properties, TextFileService.TextFile Original, bool ExistsOnDisk);

/// <summary>Reads and writes a server's <c>server.properties</c>.</summary>
/// <remarks>
/// <para>Java properties format: <c>key=value</c> with <c>#</c>/<c>!</c> comments, <c>=</c>, <c>:</c>
/// or whitespace as the separator, backslash escapes, and <c>\uXXXX</c> for anything outside
/// printable ASCII (a motd's section sign is stored as <c>\u00A7</c>). Values are unescaped on read
/// and re-escaped on write, so a UI works with the text the player sees.</para>
/// <para>Static and directory-based like <see cref="OptionsTxtService"/>; call sites pass
/// <c>packs.ServerRunDir(id)</c>.</para>
/// </remarks>
public static class ServerPropertiesService
{
    public const string FileName = "server.properties";

    public static string PathFor(string serverRunDir) => System.IO.Path.Combine(serverRunDir, FileName);

    /// <summary>
    /// Reads the server's properties. A folder with no file yet reads as an empty document with the
    /// vanilla defaults showing through the typed view, so a UI can present the form before the server
    /// has ever run.
    /// </summary>
    public static ServerPropertiesFile Read(string serverRunDir)
    {
        var path = PathFor(serverRunDir);
        if (!File.Exists(path))
        {
            var empty = new ServerProperties();
            return new ServerPropertiesFile(
                path, empty,
                new TextFileService.TextFile("", new UTF8Encoding(false), false, "\r\n", DateTime.MinValue),
                ExistsOnDisk: false);
        }

        var original = TextFileService.Read(path);
        return new ServerPropertiesFile(path, Parse(original.Text, original.Newline), original, ExistsOnDisk: true);
    }

    /// <summary>Reads the properties, writing a starter file first if the server has never run.</summary>
    /// <remarks>The starter file only has port, motd, level name, difficulty, gamemode and the whitelist
    /// switches. Minecraft fills in every missing key with the defaults of the version being run, and
    /// those differ between versions.</remarks>
    public static ServerPropertiesFile ReadOrCreate(string serverRunDir, string? motd = null, int? port = null)
    {
        var file = Read(serverRunDir);
        if (file.ExistsOnDisk) return file;

        Directory.CreateDirectory(serverRunDir);
        var p = file.Properties;
        p.Add(ServerPropertyLine.Comment("#Minecraft server properties", p.Newline));
        p.Add(ServerPropertyLine.Comment("#Written by CloudLauncher - the server fills in everything else on its first start.", p.Newline));
        p.ServerPort = port ?? 25565;
        p.Motd = motd ?? "A Minecraft Server";
        p.LevelName = "world";
        p.Gamemode = "survival";
        p.Difficulty = "normal";
        p.MaxPlayers = 8;
        Save(file);
        return Read(serverRunDir);
    }

    /// <summary>Writes the document back, preserving the file's encoding, BOM and line endings.</summary>
    /// <remarks>Returns the re-read file so <see cref="ServerPropertiesFile.Original"/> has the new write
    /// time; otherwise <see cref="ChangedOnDisk"/> would flag the launcher's own save as an outside
    /// edit.</remarks>
    public static ServerPropertiesFile Save(ServerPropertiesFile file)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file.Path)!);
        var written = TextFileService.Write(file.Path, file.Properties.ToText(), file.Original);
        file.Properties.MarkClean();
        return file with { Original = file.Original with { WrittenAtUtc = written }, ExistsOnDisk = true };
    }

    /// <summary>True when the file changed on disk since it was read (a running server rewrites it on
    /// stop, and people edit it by hand).</summary>
    public static bool ChangedOnDisk(ServerPropertiesFile file) =>
        file.ExistsOnDisk && TextFileService.ChangedOnDisk(file.Path, file.Original);

    /// <summary>The port a server in this folder will listen on, without the caller having to hold a
    /// document. Used by the launch path to report where players connect.</summary>
    public static int PortFor(string serverRunDir)
    {
        try { return Read(serverRunDir).Properties.ServerPort; }
        catch { return 25565; }
    }

    // ── parsing ──────────────────────────────────────────────────────────────

    public static ServerProperties Parse(string text, string? newline = null)
    {
        var doc = new ServerProperties { Newline = newline ?? "\r\n" };
        var i = 0;
        while (i < text.Length)
        {
            var end = i;
            while (end < text.Length && text[end] != '\n' && text[end] != '\r') end++;
            var raw = text[i..end];
            var terminator = "";
            if (end < text.Length)
            {
                terminator = text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? "\r\n" : text[end].ToString();
                end += terminator.Length;
            }
            i = end;

            doc.Add(ParseLine(raw, terminator));
        }
        return doc;
    }

    private static ServerPropertyLine ParseLine(string raw, string terminator)
    {
        var start = 0;
        while (start < raw.Length && (raw[start] == ' ' || raw[start] == '\t' || raw[start] == '\f')) start++;
        if (start >= raw.Length || raw[start] == '#' || raw[start] == '!')
            return ServerPropertyLine.Comment(raw, terminator);

        // The key runs to the first unescaped separator: '=' , ':' or whitespace. A backslash escapes
        // whatever follows it, which is how a key can legally contain one of those characters.
        var k = start;
        var escaped = false;
        while (k < raw.Length)
        {
            var c = raw[k];
            if (escaped) { escaped = false; k++; continue; }
            if (c == '\\') { escaped = true; k++; continue; }
            if (c == '=' || c == ':' || c == ' ' || c == '\t' || c == '\f') break;
            k++;
        }
        var keyRaw = raw[start..k];
        if (keyRaw.Length == 0) return ServerPropertyLine.Comment(raw, terminator);

        // Separator: any run of whitespace, then at most one '=' or ':', then more whitespace.
        var s = k;
        while (s < raw.Length && (raw[s] == ' ' || raw[s] == '\t' || raw[s] == '\f')) s++;
        if (s < raw.Length && (raw[s] == '=' || raw[s] == ':')) s++;
        while (s < raw.Length && (raw[s] == ' ' || raw[s] == '\t' || raw[s] == '\f')) s++;

        var prefix = raw[..s];
        var value = Unescape(raw[s..]);
        return ServerPropertyLine.Pair(raw, terminator, prefix, Unescape(keyRaw), value);
    }

    /// <summary>Turns Java's escapes back into the text they stand for. An unknown escape drops its
    /// backslash, which is what <c>Properties.load</c> does.</summary>
    public static string Unescape(string s)
    {
        if (s.IndexOf('\\') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
            var next = s[++i];
            switch (next)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'f': sb.Append('\f'); break;
                case 'u':
                    if (i + 4 < s.Length
                        && ushort.TryParse(s.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                    {
                        sb.Append((char)code);
                        i += 4;
                    }
                    else sb.Append('u'); // a malformed \u is not worth throwing a config file away over
                    break;
                default: sb.Append(next); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Escapes a value the way <c>Properties.store</c> does: control characters and everything above
    /// printable ASCII become <c>\uXXXX</c>, so a motd with section signs or an em dash survives a file
    /// the server will read as ISO-8859-1.
    /// </summary>
    public static string EscapeValue(string value) => Escape(value, escapeSeparators: false);

    /// <summary>As <see cref="EscapeValue"/>, but a key must also escape the characters that would
    /// otherwise end it: <c>=</c>, <c>:</c> and spaces.</summary>
    public static string EscapeKey(string key) => Escape(key, escapeSeparators: true);

    private static string Escape(string s, bool escapeSeparators)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            switch (c)
            {
                case '\\': sb.Append("\\\\"); continue;
                case '\n': sb.Append("\\n"); continue;
                case '\r': sb.Append("\\r"); continue;
                case '\t': sb.Append("\\t"); continue;
                case '\f': sb.Append("\\f"); continue;
            }
            // A leading space in a value is dropped on read unless it is escaped; one in the middle is
            // not. Keys escape every space, because the first one would end the key.
            if (c == ' ' && (escapeSeparators || i == 0)) { sb.Append("\\ "); continue; }
            if (escapeSeparators && (c == '=' || c == ':' || c == '#' || c == '!')) { sb.Append('\\').Append(c); continue; }
            // A value starting with # or ! would read back as a comment.
            if (i == 0 && !escapeSeparators && (c == '#' || c == '!')) { sb.Append('\\').Append(c); continue; }
            if (c < 0x20 || c > 0x7E) { sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); continue; }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
