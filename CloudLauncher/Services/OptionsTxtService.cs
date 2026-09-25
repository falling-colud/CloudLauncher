using System.Globalization;
using System.IO;

namespace CloudLauncher.Services;

/// <summary>Reads and writes Minecraft's <c>options.txt</c>: one <c>key:value</c> pair per line.
/// Booleans are <c>true</c>/<c>false</c>, volumes 0.0 to 1.0, keybinds <c>key.keyboard.*</c> or
/// <c>key.mouse.*</c> identifiers.</summary>
public static class OptionsTxtService
{
    /// <summary>Write a fresh options.txt for the given game directory from defaults.</summary>
    public static void WriteDefaults(string gameDir, McDefaults d)
    {
        Directory.CreateDirectory(gameDir);
        var path = Path.Combine(gameDir, "options.txt");
        var sb = new System.Text.StringBuilder();
        var inv = CultureInfo.InvariantCulture;

        sb.AppendLine($"fov:{d.Fov}");
        sb.AppendLine($"renderDistance:{d.RenderDistance}");
        sb.AppendLine($"simulationDistance:{d.SimulationDistance}");
        sb.AppendLine($"gamma:{d.Brightness.ToString("0.0##", inv)}");
        sb.AppendLine($"guiScale:{d.GuiScale}");
        sb.AppendLine($"fullscreen:{d.Fullscreen.ToString().ToLowerInvariant()}");
        sb.AppendLine($"enableVsync:{d.VSync.ToString().ToLowerInvariant()}");
        sb.AppendLine($"bobView:{d.ViewBobbing.ToString().ToLowerInvariant()}");
        sb.AppendLine($"autoJump:{d.AutoJump.ToString().ToLowerInvariant()}");

        sb.AppendLine($"soundCategory_master:{d.MasterVolume.ToString("0.0##", inv)}");
        sb.AppendLine($"soundCategory_music:{d.MusicVolume.ToString("0.0##", inv)}");
        sb.AppendLine($"soundCategory_block:{d.SoundFxVolume.ToString("0.0##", inv)}");

        sb.AppendLine($"key_key.forward:{d.KeyForward}");
        sb.AppendLine($"key_key.back:{d.KeyBack}");
        sb.AppendLine($"key_key.left:{d.KeyLeft}");
        sb.AppendLine($"key_key.right:{d.KeyRight}");
        sb.AppendLine($"key_key.jump:{d.KeyJump}");
        sb.AppendLine($"key_key.sneak:{d.KeySneak}");
        sb.AppendLine($"key_key.sprint:{d.KeySprint}");
        sb.AppendLine($"key_key.inventory:{d.KeyInventory}");
        sb.AppendLine($"key_key.chat:{d.KeyChat}");
        sb.AppendLine($"key_key.drop:{d.KeyDrop}");
        sb.AppendLine($"key_key.attack:{d.KeyAttack}");
        sb.AppendLine($"key_key.use:{d.KeyUse}");
        sb.AppendLine($"key_key.togglePerspective:{d.KeyTogglePerspective}");

        File.WriteAllText(path, sb.ToString());
    }

    /// <summary>Merges the defaults into an existing options.txt. Only managed keys are overwritten;
    /// everything else is kept.</summary>
    public static void MergeIntoExisting(string gameDir, McDefaults d)
    {
        var path = Path.Combine(gameDir, "options.txt");
        if (!File.Exists(path)) { WriteDefaults(gameDir, d); return; }

        var existing = File.ReadAllLines(path).ToList();
        var managed = BuildManagedMap(d);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < existing.Count; i++)
        {
            var line = existing[i];
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            var key = line[..idx];
            if (managed.TryGetValue(key, out var newVal))
            {
                existing[i] = $"{key}:{newVal}";
                seen.Add(key);
            }
        }
        // Append any managed keys that weren't already present
        foreach (var (key, val) in managed)
            if (!seen.Contains(key))
                existing.Add($"{key}:{val}");

        File.WriteAllLines(path, existing);
    }

    public static void EnsureWindowed(string gameDir)
        => SetOption(gameDir, "fullscreen", "false");

    /// <summary>Forces <c>pauseOnLostFocus:false</c> so the game keeps running while the player uses
    /// the launcher's side panel (clicking command links, for example).</summary>
    public static void EnsureNoPauseOnLostFocus(string gameDir)
        => SetOption(gameDir, "pauseOnLostFocus", "false");

    /// <summary>Set a single <c>key:value</c> option, preserving every other line.</summary>
    private static void SetOption(string gameDir, string key, string value)
    {
        var path = Path.Combine(gameDir, "options.txt");
        Directory.CreateDirectory(gameDir);
        var line = $"{key}:{value}";
        if (!File.Exists(path))
        {
            File.WriteAllText(path, line + Environment.NewLine);
            return;
        }

        var existing = File.ReadAllLines(path).ToList();
        var updated = false;
        for (var i = 0; i < existing.Count; i++)
        {
            if (!existing[i].StartsWith(key + ":", StringComparison.Ordinal))
                continue;

            existing[i] = line;
            updated = true;
            break;
        }

        if (!updated)
            existing.Add(line);

        File.WriteAllLines(path, existing);
    }

    // ── the resource pack stack ──────────────────────────────────────────────

    /// <summary>options.txt key holding the ordered list of enabled resource packs.</summary>
    private const string ResourcePacksKey = "resourcePacks";

    /// <summary>The built-in pack. Always present, always the bottom of the stack.</summary>
    public const string VanillaEntry = "vanilla";

    /// <summary>The enabled resource packs in the file's order, lowest priority first.</summary>
    /// <remarks>
    /// <para>Stored as a one-line JSON array, e.g. <c>resourcePacks:["vanilla","file/Foo.zip"]</c>. The
    /// last entry loads last and wins; the in-game screen shows the stack reversed. Entries are
    /// <c>file/&lt;name&gt;</c> on 1.13+ and a bare <c>&lt;name&gt;</c> before that, and are returned
    /// as is, since rewriting a 1.12 instance into the modern form would turn every pack off. A missing
    /// file or key gives an empty list.</para>
    /// <para>The game rewrites options.txt on exit, so changes made while it runs are lost. Callers
    /// check <c>App.State.Instances.IsBusy</c> first.</para>
    /// </remarks>
    public static List<string> ReadResourcePacks(string gameDir)
    {
        var path = Path.Combine(gameDir, "options.txt");
        if (!File.Exists(path)) return new();

        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                if (!line.StartsWith(ResourcePacksKey + ":", StringComparison.Ordinal)) continue;
                return ParseJsonArray(line[(ResourcePacksKey.Length + 1)..].Trim());
            }
        }
        catch { /* unreadable options.txt behaves as "not configured" */ }
        return new();
    }

    /// <summary>Rewrites the <c>resourcePacks</c> line and leaves every other line as it was,
    /// including <c>incompatibleResourcePacks</c>, which the game maintains.</summary>
    /// <param name="entries">The stack in file order, lowest priority first. "vanilla" is forced to
    /// the front if the caller left it out, because a stack without it loads no base textures.</param>
    public static void WriteResourcePacks(string gameDir, IReadOnlyList<string> entries)
    {
        var ordered = new List<string>();
        if (!entries.Any(e => string.Equals(e, VanillaEntry, StringComparison.Ordinal)))
            ordered.Add(VanillaEntry);
        ordered.AddRange(entries);

        SetOption(gameDir, ResourcePacksKey, FormatJsonArray(ordered));
    }

    /// <summary>True when this instance's options.txt spells its entries <c>file/Name.zip</c>, the
    /// 1.13+ form.</summary>
    /// <param name="minecraftVersion">The instance's Minecraft version, used only when the file itself
    /// says nothing. Pass it wherever it is known (see <see cref="VersionUsesFilePrefix"/>).</param>
    /// <remarks>Decided from the file's existing entries where possible. A stack holding only
    /// "vanilla" gives no evidence, and assuming the modern form there breaks pre-1.13 instances (the
    /// game matches bare file names), so the version decides. With no version either, the modern form
    /// is assumed.</remarks>
    public static bool UsesFilePrefix(string gameDir, string? minecraftVersion = null)
    {
        var existing = ReadResourcePacks(gameDir);
        var named = existing.Where(e => !string.Equals(e, VanillaEntry, StringComparison.Ordinal)).ToList();
        if (named.Count == 0) return VersionUsesFilePrefix(minecraftVersion);
        return named.Any(e => e.StartsWith("file/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when <paramref name="minecraftVersion"/> is 1.13 or later, the releases that
    /// spell resource pack entries <c>file/Name.zip</c>.</summary>
    /// <remarks>Only the <c>1.x</c> release form is parsed. Anything else (a snapshot like
    /// <c>23w31a</c>, a loader's naming, an empty value) is treated as modern, since the launcher
    /// installs current versions by default.</remarks>
    public static bool VersionUsesFilePrefix(string? minecraftVersion)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersion)) return true;

        // "1.12.2", "1.13", "1.20.1-pre1": the number after the first dot decides.
        var parts = minecraftVersion.Trim().Split('.');
        if (parts.Length < 2 || parts[0] != "1") return true;

        var minorDigits = new string(parts[1].TakeWhile(char.IsDigit).ToArray());
        if (minorDigits.Length == 0) return true;
        return !int.TryParse(minorDigits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minor)
               || minor >= 13;
    }

    /// <summary>The file name inside an options.txt entry, with any <c>file/</c> prefix removed.</summary>
    public static string EntryFileName(string entry) =>
        entry.StartsWith("file/", StringComparison.OrdinalIgnoreCase) ? entry["file/".Length..] : entry;

    /// <summary>Parses the JSON string array Minecraft writes, tolerating hand-edited whitespace.</summary>
    private static List<string> ParseJsonArray(string value)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(value)) return result;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(value);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return result;
            foreach (var item in doc.RootElement.EnumerateArray())
                if (item.ValueKind == System.Text.Json.JsonValueKind.String && item.GetString() is { } s)
                    result.Add(s);
            return result;
        }
        catch (System.Text.Json.JsonException)
        {
            // A truncated or hand-edited line still says which packs were meant to be on. Better than
            // treating the instance as having none enabled and then writing that back.
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(value, "\"((?:[^\"\\\\]|\\\\.)*)\""))
                result.Add(m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\"));
            return result;
        }
    }

    /// <summary>Writes the array the way the game does: <c>["a","b"]</c>, no spaces.</summary>
    /// <remarks>Hand-rolled so non-ASCII pack names stay readable instead of becoming \u escapes. The
    /// game reads both, but the user should recognise their pack names in the file.</remarks>
    private static string FormatJsonArray(IReadOnlyList<string> entries)
    {
        var sb = new System.Text.StringBuilder("[");
        for (var i = 0; i < entries.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('"');
            foreach (var ch in entries[i])
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (char.IsControl(ch)) sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(ch);
                        break;
                }
            }
            sb.Append('"');
        }
        return sb.Append(']').ToString();
    }

    private static Dictionary<string, string> BuildManagedMap(McDefaults d)
    {
        var inv = CultureInfo.InvariantCulture;
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fov"]                     = d.Fov.ToString(inv),
            ["renderDistance"]          = d.RenderDistance.ToString(inv),
            ["simulationDistance"]      = d.SimulationDistance.ToString(inv),
            ["gamma"]                   = d.Brightness.ToString("0.0##", inv),
            ["guiScale"]                = d.GuiScale.ToString(inv),
            ["fullscreen"]              = d.Fullscreen.ToString().ToLowerInvariant(),
            ["enableVsync"]             = d.VSync.ToString().ToLowerInvariant(),
            ["bobView"]                 = d.ViewBobbing.ToString().ToLowerInvariant(),
            ["autoJump"]                = d.AutoJump.ToString().ToLowerInvariant(),
            ["soundCategory_master"]    = d.MasterVolume.ToString("0.0##", inv),
            ["soundCategory_music"]     = d.MusicVolume.ToString("0.0##", inv),
            ["soundCategory_block"]     = d.SoundFxVolume.ToString("0.0##", inv),
            ["key_key.forward"]         = d.KeyForward,
            ["key_key.back"]            = d.KeyBack,
            ["key_key.left"]            = d.KeyLeft,
            ["key_key.right"]           = d.KeyRight,
            ["key_key.jump"]            = d.KeyJump,
            ["key_key.sneak"]           = d.KeySneak,
            ["key_key.sprint"]          = d.KeySprint,
            ["key_key.inventory"]       = d.KeyInventory,
            ["key_key.chat"]            = d.KeyChat,
            ["key_key.drop"]            = d.KeyDrop,
            ["key_key.attack"]          = d.KeyAttack,
            ["key_key.use"]             = d.KeyUse,
            ["key_key.togglePerspective"] = d.KeyTogglePerspective,
        };
    }
}
