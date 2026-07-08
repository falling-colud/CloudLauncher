using System.Globalization;
using System.IO;

namespace CloudLauncher.Services;

/// <summary>
/// Serialises and deserialises Minecraft's <c>options.txt</c> file.
/// The format is one <c>key:value</c> pair per line. Boolean values are <c>true</c>/<c>false</c>;
/// volumes are 0.0–1.0; keybinds are <c>key.keyboard.*</c> or <c>key.mouse.*</c> identifiers.
/// </summary>
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

        File.WriteAllText(path, sb.ToString());
    }

    /// <summary>
    /// Merge the supplied defaults INTO an existing options.txt — preserving any keys
    /// the user has customised that we don't manage. Only known keys get overwritten.
    /// </summary>
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

    /// <summary>
    /// Force <c>pauseOnLostFocus:false</c> so the embedded game keeps running while the
    /// player interacts with the launcher's side panel (e.g. clicking command links).
    /// Without this, submitting a chat command closes the chat screen and the unfocused
    /// game would auto-pause.
    /// </summary>
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
        };
    }
}
