using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>"Low mode": turns a pack's heavy visual settings down before launch, and puts them back
/// when switched off.</summary>
/// <remarks>
/// <para>Mostly it rewrites settings. It also disables a few purely decorative client-only mods
/// (<see cref="DisabledModPrefixes"/>) that the server doesn't run and that register no network
/// payloads, so players with and without low mode can share a server.</para>
/// <para>Previous values are kept in <c>.lowmode-backup.json</c> and restored when it is switched off,
/// but only for keys still at the value low mode wrote. Edits touch single keys, never whole files,
/// because these files belong to other mods; missing files and keys are skipped.</para>
/// </remarks>
public static class LowModeService
{
    private const string BackupFileName = ".lowmode-backup.json";

    /// <summary>One setting to push down, and how to find it.</summary>
    /// <param name="RelativePath">Config file, relative to the pack's <c>game/</c> folder.</param>
    /// <param name="Key">Key name as it appears in the file.</param>
    /// <param name="LowValue">Value to write while low mode is on.</param>
    /// <param name="Section">TOML section the key must sit under, or null for JSON / any section.</param>
    private sealed record Tweak(string RelativePath, string Key, string LowValue, string? Section = null);

    /// <summary>
    /// The profile: cuts what costs frames in this kind of pack (draw distance, cloud decks, per-pixel
    /// effects) and leaves alone anything that changes gameplay or could desync a multiplayer session.
    /// </summary>
    private static readonly Tweak[] Profile =
    [
        // Mostly Voxy: its LOD is the most expensive thing on screen, and turning it down costs little
        // visually (the world still reaches the horizon, in coarser steps).
        //
        // Not changed here: graphicsMode, renderClouds, mipmapLevels, entityShadows, particles,
        // entityDistanceScaling, cloud layer count and LOD fog. They make the game look cheap for far less
        // gain. RestoreOrphans() puts them back on machines that applied an older profile.
        new("config/voxy-config.json", "section_render_distance", "2"),
        new("config/voxy-config.json", "service_threads", "4"),

        // Client-side distant generation is CPU spent generating terrain the server already has. With it
        // off, the two throttles below only matter for work still queued when low mode turns on.
        new("config/voxy-config.json", "distant_gen_enabled", "false"),
        new("config/voxy-config.json", "distant_gen_max_in_flight", "2"),
        new("config/voxy-config.json", "distant_gen_max_mspt", "5"),

        // Cloud draw distance only, not quality: clouds keep their layers and shading but stop following
        // the LOD out to 16k blocks. Beyond 4096 nobody notices them missing.
        new("config/micvoxy-client.toml", "cloudRenderDistanceMaxBlocks", "4096", "voxy_betterclouds"),

        // The only vanilla settings changed. Low mode also disables Voxy, so 10 chunks is where the world
        // ends; raise this first if a machine has the headroom.
        new("options.txt", "renderDistance", "10"),
        new("options.txt", "simulationDistance", "10"),
    ];

    /// <summary>Client-only visual mods that low mode switches off by renaming the jar to
    /// <c>.jar.disabled</c>, matched by filename prefix so new versions still match.</summary>
    /// <remarks>Every entry must be client-only, register no network payloads and not be installed on the
    /// pack's server, so players with and without low mode can play together. That rules out Xaero's
    /// maps, AmbientSounds, Waves, Particular, Falling Leaves, Not Enough Animations, 3D Skin Layers, Big
    /// Water and Where Winds Blow. Uploads report these jars under their enabled names
    /// (<see cref="UploadNameFor"/>). Percentages are measured render-thread and client-tick shares from
    /// this pack's profile.</remarks>
    private static readonly string[] DisabledModPrefixes =
    [
        // ---- heavy decorative mods ---------------------------------------------------------------------
        "atmospherics-",            // 6.3% render + 18.8% client tick - the single biggest decorative cost
        "grassiergrass-",           // 7.8% render (64 blades/block out to 160 blocks at stock)
        "better-clouds-",
        "betterfog-",
        "BetterFoliageRenewed-",

        // ---- more decorative mods ----------------------------------------------------------------------
        "softimprints-",            // 5.2% render + 16.2% tick; footprints, and its model-contact path
                                    // throws a NoSuchMethodException per entity per tick
        "entity_model_features-",   // FreshAnimations' model half
        "entity_texture_features_", // ...and its texture half; both no-op the pack's FreshAnimations
        "cirrus-",                  // smooth sky/time transitions
        "particlerain-",            // weather particles
        "auroras-",                 // aurora rendering
        "punchy-",                  // hit effects / screen shake
        "ItemPhysicLite_",          // dropped-item physics

        // wakes and WakesSableCompat go together: the compat mod hard-requires wakes, so disabling wakes
        // alone would fail mod loading. Both are client-only and payload-free.
        "wakes-",
        "WakesSableCompat",

        // ---- Voxy --------------------------------------------------------------------------------------
        // The LOD renderer rather than a decorative mod, so with it off the world ends at renderDistance.
        // It is listed because it won't start on some Intel iGPUs: the Iris Xe driver (32.0.101.7088)
        // rejects its shaders (`uint64_t`, `readonly`) and no setting avoids that. It is client-only (the
        // server runs "Voxy World Gen V2", not voxy), registers no payloads, and every mod that uses it
        // treats it as optional. "voxy-" with the hyphen doesn't match "Voxy World Gen V2-...", which
        // must stay enabled. The voxy-config.json tweaks above stay as a fallback if the rename fails.
        "voxy-",
    ];

    /// <summary>Applies or reverts low mode for a pack. Idempotent, so safe to call on every
    /// launch.</summary>
    /// <param name="gameDir">The pack's <c>game/</c> directory.</param>
    /// <param name="enabled">Desired state.</param>
    /// <returns>A short human-readable summary for the launch log.</returns>
    public static string Apply(string gameDir, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
            return "low mode: no game directory, skipped";

        var backupPath = Path.Combine(gameDir, BackupFileName);
        return enabled ? Enable(gameDir, backupPath) : Disable(gameDir, backupPath);
    }

    /// <summary>A plain-language account of what low mode changes, for the "?" next to the checkbox.
    /// Lists the exact settings and mods and, given a game directory, which of them this pack actually
    /// has.</summary>
    public static string Describe(string? gameDir)
    {
        var haveDir = !string.IsNullOrWhiteSpace(gameDir) && Directory.Exists(gameDir);
        var sb = new StringBuilder();
        sb.AppendLine("Low mode makes the pack run on a modest machine. Before every launch it turns the settings that cost the most frames down, and switches a few purely decorative client-side mods off. Your own values are saved first and put back the moment you turn low mode off - anything you change by hand while it is on is left alone.");
        sb.AppendLine();

        sb.AppendLine("Settings it lowers:");
        foreach (var t in Profile)
        {
            var file = haveDir ? Path.Combine(gameDir!, t.RelativePath.Replace('/', Path.DirectorySeparatorChar)) : null;
            var present = file is not null && File.Exists(file);
            var note = !haveDir ? "" : present ? "" : "   (not in this pack)";
            sb.AppendLine($"  • {t.RelativePath}: {t.Key} > {t.LowValue}{note}");
        }
        sb.AppendLine();

        sb.AppendLine("Mods it switches off (renamed to .jar.disabled, re-enabled when low mode is off):");
        var modsDir = haveDir ? Path.Combine(gameDir!, "mods") : null;
        string[] jars = Array.Empty<string>();
        if (modsDir is not null && Directory.Exists(modsDir))
        {
            try { jars = Directory.GetFiles(modsDir).Select(Path.GetFileName).Where(n => n is not null).ToArray()!; }
            catch { jars = Array.Empty<string>(); }
        }
        foreach (var prefix in DisabledModPrefixes)
        {
            var hit = jars.FirstOrDefault(j => j.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            var note = !haveDir ? "" : hit is null ? "   (not in this pack)" : $"   ({hit.Replace(".disabled", "")})";
            sb.AppendLine($"  • {prefix}*{note}");
        }
        sb.AppendLine();

        sb.AppendLine("Left alone on purpose: graphics mode, clouds, mipmaps, entity shadows, particles and fog - those make the game look cheap for little gain.");
        sb.AppendLine("Every mod on the list is client-only and sends nothing over the network, and none of them is installed on the pack's server, so you can always join a server with low mode on alongside players who have it off.");
        return sb.ToString().TrimEnd();
    }

    private static string Enable(string gameDir, string backupPath)
    {
        // Already on: the backup holds the player's real values, so re-applying must not overwrite it with the
        // low values we ourselves wrote last time.
        var alreadyOn = File.Exists(backupPath);
        var backup = alreadyOn ? ReadBackup(backupPath) : new Dictionary<string, string>();

        // Backup entries for settings an older build lowered but this one doesn't touch (e.g.
        // graphicsMode) are restored on every apply, not just on disable, so they don't stay low after an
        // update.
        var healed = RestoreOrphans(gameDir, backup);

        var changed = 0;
        foreach (var tweak in Profile)
        {
            var file = Path.Combine(gameDir, tweak.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file))
                continue;

            var id = BackupKey(tweak);
            var current = ReadValue(file, tweak);
            if (current is null)
                continue;

            if (!alreadyOn && !backup.ContainsKey(id))
                backup[id] = current;

            if (!string.Equals(current, tweak.LowValue, StringComparison.Ordinal) && WriteValue(file, tweak, tweak.LowValue))
                changed++;
        }

        WriteBackup(backupPath, backup);
        var disabled = SetModsDisabled(gameDir, true);
        var healNote = healed > 0 ? $", {healed} outdated setting(s) restored" : "";
        return $"low mode ON ({changed} setting(s) lowered, {disabled} visual mod(s) off{healNote})";
    }

    private static string Disable(string gameDir, string backupPath)
    {
        var reenabled = SetModsDisabled(gameDir, false);
        if (!File.Exists(backupPath))
            return $"low mode OFF ({reenabled} visual mod(s) back on, no settings to restore)";

        var backup = ReadBackup(backupPath);
        var restored = RestoreOrphans(gameDir, backup);
        foreach (var tweak in Profile)
        {
            var file = Path.Combine(gameDir, tweak.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file) || !backup.TryGetValue(BackupKey(tweak), out var original))
                continue;

            // Only roll back keys still at the value we wrote. If the player has since changed one by hand,
            // theirs wins.
            var current = ReadValue(file, tweak);
            if (current is not null
                && string.Equals(current, tweak.LowValue, StringComparison.Ordinal)
                && WriteValue(file, tweak, original))
            {
                restored++;
            }
        }

        try { File.Delete(backupPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return $"low mode OFF ({restored} setting(s) restored, {reenabled} visual mod(s) back on)";
    }

    /// <summary>Restores backup entries whose tweak is no longer in <see cref="Profile"/> (settings an
    /// older build lowered, such as graphicsMode). Rollback is Profile-driven, so they would otherwise
    /// stay low.</summary>
    /// <remarks>Restored unconditionally, since their low value is unknown. Handled entries are removed
    /// from the map; callers save or delete the file.</remarks>
    private static int RestoreOrphans(string gameDir, Dictionary<string, string> backup)
    {
        if (backup.Count == 0)
            return 0;

        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in Profile)
            known.Add(BackupKey(t));

        List<string>? orphans = null;
        foreach (var key in backup.Keys)
        {
            if (!known.Contains(key))
                (orphans ??= new List<string>()).Add(key);
        }
        if (orphans is null)
            return 0;

        var restored = 0;
        foreach (var key in orphans)
        {
            // BackupKey format: "RelativePath|Section|Key" (Section empty when null).
            var parts = key.Split('|');
            if (parts.Length == 3)
            {
                var tweak = new Tweak(parts[0], parts[2], backup[key],
                    string.IsNullOrEmpty(parts[1]) ? null : parts[1]);
                var file = Path.Combine(gameDir, tweak.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(file) && WriteValue(file, tweak, backup[key]))
                    restored++;
            }
            backup.Remove(key);
        }
        return restored;
    }

    /// <summary>Renames the low-mode visual mods between <c>X.jar</c> and <c>X.jar.disabled</c>.</summary>
    /// <remarks>Runs at every launch. A sync always delivers the enabled <c>X.jar</c>, so a stale
    /// <c>X.jar.disabled</c> can end up beside it and NeoForge would crash on the duplicate mod id. The
    /// plain jar wins and disabled twins are deleted before renaming.</remarks>
    /// <returns>How many mods ended up switched to the requested state.</returns>
    private static int SetModsDisabled(string gameDir, bool disabled)
    {
        var modsDir = Path.Combine(gameDir, "mods");
        if (!Directory.Exists(modsDir))
            return 0;

        var count = 0;
        foreach (var prefix in DisabledModPrefixes)
        {
            List<string> enabledJars;
            List<string> disabledJars;
            try
            {
                enabledJars = Directory.EnumerateFiles(modsDir, prefix + "*.jar").ToList();
                disabledJars = Directory.EnumerateFiles(modsDir, prefix + "*.jar.disabled").ToList();
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            try
            {
                if (enabledJars.Count > 0)
                {
                    // The enabled jar is current (fresh install or fresh sync); disabled twins are stale.
                    foreach (var stale in disabledJars)
                        File.Delete(stale);
                    if (disabled)
                    {
                        File.Move(enabledJars[0], enabledJars[0] + ".disabled");
                        count++;
                    }
                }
                else if (disabledJars.Count > 0 && !disabled)
                {
                    var target = disabledJars[0][..^".disabled".Length];
                    File.Move(disabledJars[0], target);
                    count++;
                }
                else if (disabledJars.Count > 0)
                {
                    count++;                                   // already off, nothing to do
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return count;
    }

    /// <summary>
    /// The name a mod file should be shared under: a <c>.jar.disabled</c> managed by low mode is reported
    /// as its plain <c>.jar</c>, so this machine's low mode is never uploaded as everyone's baseline.
    /// Files disabled by hand keep their name, since renaming a mod to disable it for the whole pack is a
    /// real change to share.
    /// </summary>
    public static string UploadNameFor(string relativePath)
    {
        const string suffix = ".jar.disabled";
        if (!relativePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return relativePath;
        var fileName = Path.GetFileName(relativePath);
        foreach (var prefix in DisabledModPrefixes)
        {
            if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return relativePath[..^".disabled".Length];
        }
        return relativePath;
    }

    /// <summary>The bytes a file should be shared as: this machine's low-mode edits reverted to the
    /// player's own values, or <c>null</c> when there is nothing to mask (upload the file as is).</summary>
    /// <remarks>The config counterpart of <see cref="UploadNameFor"/>: low mode is per-machine state and
    /// must never reach other players. Only keys still at low mode's value are reverted; a value the
    /// player changed by hand is shared as is.</remarks>
    public static byte[]? SharedContentFor(string gameDir, string relativePath)
    {
        // Cheapest rejection first: an upload asks this about every shared file in the pack (often over a
        // thousand), and almost none of them can match a profile entry.
        var rel = relativePath.Replace('\\', '/');
        var touched = false;
        foreach (var t in Profile)
        {
            if (string.Equals(t.RelativePath, rel, StringComparison.OrdinalIgnoreCase)) { touched = true; break; }
        }
        if (!touched)
            return null;                                  // low mode never touches this file

        var backup = CachedBackup(gameDir);
        if (backup is null || backup.Count == 0)
            return null;                                  // low mode is off: the file on disk is the truth

        var tweaks = Profile
            .Where(t => string.Equals(t.RelativePath, rel, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var file = Path.Combine(gameDir, rel.Replace('/', Path.DirectorySeparatorChar));
        string[] lines;
        try
        {
            lines = File.ReadAllLines(file);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        var reverted = false;
        foreach (var tweak in tweaks)
        {
            if (!backup.TryGetValue(BackupKey(tweak), out var original))
                continue;

            var idx = FindLine(lines, tweak);
            if (idx < 0) continue;

            var m = ValueRegex(tweak).Match(lines[idx]);
            if (!m.Success) continue;

            var g = m.Groups["v"];
            if (!string.Equals(g.Value.Trim(), tweak.LowValue, StringComparison.Ordinal))
                continue;                                 // the player's own value, not ours - leave it

            lines[idx] = lines[idx][..g.Index] + original + lines[idx][(g.Index + g.Length)..];
            reverted = true;
        }

        if (!reverted)
            return null;

        // Matches what WriteValue leaves on disk: platform newlines, trailing newline, no BOM. A file that
        // used other line endings may still differ, which only costs one redundant re-upload.
        var text = string.Join(Environment.NewLine, lines) + Environment.NewLine;
        return new UTF8Encoding(false).GetBytes(text);
    }

    // One upload asks SharedContentFor() about every shared file in the pack, so the backup is read once per
    // upload rather than once per file. Keyed on the backup's write time, so switching low mode on or off
    // mid-session invalidates it immediately.
    private static readonly object BackupCacheGate = new();
    private static string? _cachedBackupPath;
    private static DateTime _cachedBackupStamp;
    private static Dictionary<string, string>? _cachedBackup;

    private static Dictionary<string, string>? CachedBackup(string gameDir)
    {
        var path = Path.Combine(gameDir, BackupFileName);
        DateTime stamp;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            stamp = info.LastWriteTimeUtc;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        lock (BackupCacheGate)
        {
            if (_cachedBackup is not null &&
                string.Equals(_cachedBackupPath, path, StringComparison.OrdinalIgnoreCase) &&
                _cachedBackupStamp == stamp)
            {
                return _cachedBackup;
            }

            var loaded = ReadBackup(path);
            _cachedBackupPath = path;
            _cachedBackupStamp = stamp;
            _cachedBackup = loaded;
            return loaded;
        }
    }

    private static string BackupKey(Tweak t) => $"{t.RelativePath}|{t.Section}|{t.Key}";

    // ---- file editing -------------------------------------------------------------------------------------------
    //
    // options.txt is "key:value" per line; TOML and JSON are "key = value" / "key": value. Instead of three
    // parsers for one scalar each, match the key in place and replace only what is to its right, which also keeps
    // comments, ordering and formatting.

    private static bool IsOptionsTxt(Tweak t) =>
        t.RelativePath.EndsWith("options.txt", StringComparison.OrdinalIgnoreCase);

    private static bool IsJson(Tweak t) =>
        t.RelativePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    private static string? ReadValue(string file, Tweak tweak)
    {
        try
        {
            var lines = File.ReadAllLines(file);
            var idx = FindLine(lines, tweak);
            if (idx < 0) return null;
            var m = ValueRegex(tweak).Match(lines[idx]);
            return m.Success ? m.Groups["v"].Value.Trim() : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static bool WriteValue(string file, Tweak tweak, string value)
    {
        try
        {
            var lines = File.ReadAllLines(file);
            var idx = FindLine(lines, tweak);
            if (idx < 0) return false;

            var rx = ValueRegex(tweak);
            var m = rx.Match(lines[idx]);
            if (!m.Success) return false;

            var g = m.Groups["v"];
            lines[idx] = lines[idx][..g.Index] + value + lines[idx][(g.Index + g.Length)..];

            // No BOM: NeoForge's TOML parser rejects one, and Minecraft's options reader doesn't expect one.
            File.WriteAllLines(file, lines, new UTF8Encoding(false));
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Finds the line holding the key, honouring the TOML section when one is given.</summary>
    private static int FindLine(string[] lines, Tweak tweak)
    {
        var inSection = tweak.Section is null;
        var keyRx = ValueRegex(tweak);

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();

            if (tweak.Section is not null && trimmed.StartsWith('['))
            {
                inSection = string.Equals(trimmed, $"[{tweak.Section}]", StringComparison.Ordinal);
                continue;
            }

            if (!inSection || trimmed.StartsWith('#')) continue;
            if (keyRx.IsMatch(lines[i])) return i;
        }
        return -1;
    }

    private static Regex ValueRegex(Tweak tweak)
    {
        var key = Regex.Escape(tweak.Key);
        // options.txt:  renderDistance:16
        // toml:         renderDistance = 16
        // json:         "renderDistance": 16,
        var pattern = IsOptionsTxt(tweak)
            ? $@"^\s*{key}\s*:\s*(?<v>""[^""]*""|[^\s]+?)\s*$"
            : IsJson(tweak)
                ? $@"^\s*""{key}""\s*:\s*(?<v>-?[\d.]+|true|false|""[^""]*"")\s*,?\s*$"
                : $@"^\s*{key}\s*=\s*(?<v>-?[\d.]+|true|false|""[^""]*"")\s*$";
        return new Regex(pattern, RegexOptions.CultureInvariant);
    }

    // ---- backup -------------------------------------------------------------------------------------------------

    private static Dictionary<string, string> ReadBackup(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return dict ?? new Dictionary<string, string>();
        }
        catch (Exception) { return new Dictionary<string, string>(); }
    }

    private static void WriteBackup(string path, Dictionary<string, string> backup)
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(
                backup, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Reads the effective preference for a pack. A missing entry means on.</summary>
    /// <remarks>Only meaningful for a pack <see cref="AppliesTo(PackDetail, AppSettings)"/> accepts, so
    /// ask that first: this answers on for a pack the profile was never written for.</remarks>
    public static bool IsEnabled(AppSettings settings, Guid packId) =>
        !settings.PackLowMode.TryGetValue(packId, out var on) || on;

    /// <summary>
    /// Ids of the pack this profile was measured against (Create Ultimate Selection 2). Everyone
    /// subscribed to it shares the id, so they all get the option.
    /// </summary>
    private static readonly Guid[] ProfiledPackIds =
    [
        Guid.Parse("9dc74fcc-8baf-4a33-8eec-8e985ca309f3"),
        Guid.Parse("c2ca1e34-31e6-41be-aeb5-067913e3f602"),
    ];

    /// <summary>Whether low mode is offered for a pack at all.</summary>
    /// <remarks>Low mode is a profile tuned for one modpack (Create Ultimate Selection 2), so it is
    /// offered by pack id, or by name only for a pack the signed-in user owns (a re-import or renamed
    /// copy). Other packs get no checkbox, and <see cref="Apply"/> is called with <c>false</c> for them
    /// at launch so a pack an older build lowered is put back.</remarks>
    /// <param name="packId">The pack being launched or shown.</param>
    /// <param name="packName">Its display name, used only for the owner-gated fallback.</param>
    /// <param name="ownerId">Who owns the pack (<see cref="PackDetail.OwnerId"/>).</param>
    /// <param name="currentUserId">The signed-in user, or null when signed out (then only the profiled
    /// ids qualify).</param>
    public static bool AppliesTo(Guid packId, string? packName, Guid ownerId, Guid? currentUserId)
    {
        if (Array.IndexOf(ProfiledPackIds, packId) >= 0) return true;
        if (currentUserId is not { } me || ownerId != me) return false;
        return packName is { Length: > 0 }
            && packName.Contains("ultimate selection 2", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether low mode is offered for this pack, for callers that already hold the pack and
    /// the settings.</summary>
    public static bool AppliesTo(PackDetail pack, AppSettings settings) =>
        AppliesTo(pack.Id, pack.Name, pack.OwnerId, settings.UserId);
}
