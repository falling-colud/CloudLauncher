using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CloudLauncher.Services;

/// <summary>
/// "Low mode": turns a pack's heavy visual settings down before launch, and puts them back when switched off.
///
/// <para>Most of what it does is rewrite <b>settings</b> — render distances, cloud layers, effect quality. It also
/// switches off a short list of purely decorative client mods (see <see cref="DisabledModPrefixes"/>) by renaming
/// their jars, but never one that the pack's server also runs and never one that registers a network payload, so a
/// player with low mode on can always join a server alongside one who has it off.</para>
///
/// <para>The values a player had before are recorded in <c>.lowmode-backup.json</c> beside the pack, and restored
/// verbatim when low mode is turned off. Anything the player changes <i>while</i> low mode is on is left alone on the
/// way out: only keys this service actually wrote are rolled back, so hand-tuning is never silently reverted.</para>
///
/// <para>Every edit is a targeted key rewrite rather than a whole-file rewrite, because these files belong to other
/// mods: a config we reserialise is a config we can corrupt. Missing files and missing keys are skipped quietly — a
/// pack that does not have Voxy simply has nothing to turn down.</para>
/// </summary>
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
    /// The profile. Chosen to cut the things that actually cost frames in this kind of pack — draw distance, the
    /// number of cloud decks, and per-pixel effects — while leaving anything that changes gameplay or would desync a
    /// multiplayer session untouched.
    /// </summary>
    private static readonly Tweak[] Profile =
    [
        // Low mode is a VOXY profile. Voxy's LOD is the single most expensive thing on screen - it renders
        // terrain far past the vanilla horizon - so that is where the frames are, and turning it down costs
        // almost nothing visually: the world still extends to the horizon, just in slightly coarser steps.
        //
        // Deliberately NOT here (Leon, 2026-08-31): graphicsMode, renderClouds, mipmapLevels, entityShadows,
        // particles, entityDistanceScaling, cloud layer count and LOD fog. Those are the knobs that make the
        // game look cheap - flat clouds, shimmering distant textures, shadowless mobs, fogless LOD seams -
        // while returning far less than the Voxy ones. Low mode should look like the game, only faster.
        // RestoreOrphans() puts each of them back on machines that applied an older profile.
        new("config/voxy-config.json", "section_render_distance", "2"),
        new("config/voxy-config.json", "service_threads", "4"),

        // Client-side distant generation is pure CPU spent inventing terrain the server already knows about;
        // with it off, the two throttles below only matter for anything still queued when low mode flips on.
        new("config/voxy-config.json", "distant_gen_enabled", "false"),
        new("config/voxy-config.json", "distant_gen_max_in_flight", "2"),
        new("config/voxy-config.json", "distant_gen_max_mspt", "5"),

        // Cloud DRAW DISTANCE, not cloud quality: clouds still render, with the same layers and shading, they
        // just stop following the LOD out to 16k blocks. 4096 is past anywhere the eye reads them as missing.
        new("config/micvoxy-client.toml", "cloudRenderDistanceMaxBlocks", "4096", "voxy_betterclouds"),

        // The one vanilla knob worth keeping. This used to be nearly free because Voxy drew everything past it;
        // now that low mode switches Voxy off outright, 10 chunks really is where the world ends, so this is the
        // setting to raise first if a machine turns out to have the headroom after all.
        new("options.txt", "renderDistance", "10"),
        new("options.txt", "simulationDistance", "10"),
    ];

    /// <summary>
    /// Client-only visual mods that low mode turns off entirely, matched by filename prefix so version bumps
    /// keep matching. These have no master switch in any config (Better Foliage and Grassier Grass expose only
    /// granular options; Atmospherics ships no config at all), so the jar is renamed to <c>.jar.disabled</c> -
    /// NeoForge's and the launcher's shared convention for "installed but off".
    ///
    /// Every entry here MUST be a client-only mod with no network payloads: a low-mode player and a
    /// full-settings player must always be able to play together. The share stays clean because the upload
    /// path reports low-mode-disabled jars under their ENABLED names (see <see cref="UploadNameFor"/>), so
    /// other players always receive current, enabled jars regardless of this machine's low mode.
    /// </summary>
    /// <para>Every entry below was checked against the pack's dedicated server before being added: a mod that
    /// is <i>also installed server-side</i> is never listed, because disabling it here risks a registry or
    /// channel mismatch on join. That check is what rules out Xaero's maps, AmbientSounds, Waves, Particular,
    /// Falling Leaves, Not Enough Animations, 3D Skin Layers, Big Water and Where Winds Blow, all of which are
    /// otherwise decorative. Each entry is additionally verified to register no network payloads.</para>
    ///
    /// <para>Percentages are measured shares of render-thread and client-tick samples from this pack's own
    /// profile, so the list is ordered by what it actually buys rather than by reputation.</para>
    private static readonly string[] DisabledModPrefixes =
    [
        // ---- originally profiled as the heavy decorative mods -------------------------------------------
        "atmospherics-",            // 6.3% render + 18.8% client tick - the single biggest decorative cost
        "grassiergrass-",           // 7.8% render (64 blades/block out to 160 blocks at stock)
        "better-clouds-",
        "betterfog-",
        "BetterFoliageRenewed-",

        // ---- added after the overnight profiling run ----------------------------------------------------
        "softimprints-",            // 5.2% render + 16.2% tick; footprints, and its model-contact path
                                    // throws a NoSuchMethodException per entity per tick
        "entity_model_features-",   // FreshAnimations' model half
        "entity_texture_features_", // ...and its texture half; both no-op the pack's FreshAnimations
        "cirrus-",                  // smooth sky/time transitions
        "particlerain-",            // weather particles
        "auroras-",                 // aurora rendering
        "punchy-",                  // hit effects / screen shake
        "ItemPhysicLite_",          // dropped-item physics

        // wakes and its Sable compat layer MUST move together: WakesSableCompat hard-requires wakes, so
        // disabling wakes alone would fail mod loading. Both are client-only and payload-free.
        "wakes-",
        "WakesSableCompat",

        // ---- Voxy (2026-09-04, at Leon's request) ------------------------------------------------------
        // The LOD renderer itself. Unlike everything above this is not a decorative mod - it is what draws
        // terrain past the vanilla horizon - so low mode now genuinely ends the world at renderDistance
        // instead of fading into LOD. It is here because it is also the one mod in the pack that will not
        // start at all on some Intel iGPUs: its shaders use `uint64_t` and `readonly`, which the Iris Xe
        // driver (32.0.101.7088) rejects outright, and the game dies during pipeline creation. Turning the
        // settings down cannot help that - the shaders are compiled either way - so the jar has to go.
        //
        // Safe by this list's own rules, each point checked against the pack on 2026-09-04:
        //   - client-only: the server runs "Voxy World Gen V2" but NOT voxy itself, so nothing desyncs;
        //   - no network payloads: its only packet-listener classes are mixins into vanilla listeners;
        //   - nothing breaks without it: voxyworldgenv2, micvoxy and betterfog all declare voxy as an
        //     OPTIONAL dependency. voxyworldgenv2 mixes only into vanilla server classes ("client": []),
        //     and micvoxy gates every mixin config behind a RequiredModsMixinPlugin, so its Voxy modules
        //     switch themselves off while its BetterClouds, BetterFog and Sable modules keep working.
        //
        // The pattern is "voxy-" with the hyphen: it matches voxy-1.0.0.jar and deliberately does not match
        // "Voxy World Gen V2-...", which is the server-side one and must stay enabled.
        //
        // The voxy-config.json tweaks above are left in place on purpose. They are dead while the jar is
        // renamed, and they are what still turns Voxy down if the rename ever fails (a locked file is
        // swallowed by SetModsDisabled).
        "voxy-",
    ];

    /// <summary>Applies or reverts low mode for a pack. Safe to call on every launch; it is idempotent.</summary>
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
    /// Lists the exact settings and mods, and — when a game directory is given — which of them this
    /// pack actually has, so the answer is about the player's pack rather than the profile in general.</summary>
    public static string Describe(string? gameDir)
    {
        var haveDir = !string.IsNullOrWhiteSpace(gameDir) && Directory.Exists(gameDir);
        var sb = new StringBuilder();
        sb.AppendLine("Low mode makes the pack run on a modest machine. Before every launch it turns the settings that cost the most frames down, and switches a few purely decorative client-side mods off. Your own values are saved first and put back the moment you turn low mode off — anything you change by hand while it is on is left alone.");
        sb.AppendLine();

        sb.AppendLine("Settings it lowers:");
        foreach (var t in Profile)
        {
            var file = haveDir ? Path.Combine(gameDir!, t.RelativePath.Replace('/', Path.DirectorySeparatorChar)) : null;
            var present = file is not null && File.Exists(file);
            var note = !haveDir ? "" : present ? "" : "   (not in this pack)";
            sb.AppendLine($"  • {t.RelativePath}: {t.Key} → {t.LowValue}{note}");
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

        sb.AppendLine("Left alone on purpose: graphics mode, clouds, mipmaps, entity shadows, particles and fog — those make the game look cheap for little gain.");
        sb.AppendLine("Every mod on the list is client-only and sends nothing over the network, and none of them is installed on the pack's server, so you can always join a server with low mode on alongside players who have it off.");
        return sb.ToString().TrimEnd();
    }

    private static string Enable(string gameDir, string backupPath)
    {
        // Already on: the backup holds the player's real values, so re-applying must not overwrite it with the
        // low values we ourselves wrote last time.
        var alreadyOn = File.Exists(backupPath);
        var backup = alreadyOn ? ReadBackup(backupPath) : new Dictionary<string, string>();

        // A profile that has shrunk leaves "orphaned" backup entries - settings an older build lowered
        // that this one no longer touches (e.g. graphicsMode). Heal them on every apply, not just on
        // disable, so an already-on machine returns to the player's value on its first launch after
        // updating instead of staying stuck at the old low setting forever.
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

            // Only roll back keys still sitting at the value we wrote. If the player has since tuned one by hand,
            // theirs wins - silently reverting someone's deliberate change is worse than leaving it low.
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

    /// <summary>
    /// Restores backup entries whose tweak no longer exists in <see cref="Profile"/> - settings an older
    /// build of low mode lowered that the current one no longer touches (graphicsMode was removed
    /// 2026-08-31: fancy stays on in low mode). Without this, such a value would stay stuck at the old
    /// low setting forever, because rollback is Profile-driven.
    ///
    /// Orphans are restored unconditionally: their old low value is unknown here, so the hand-tune check
    /// the Profile loop uses is not possible, and the player's pre-low-mode original is the least
    /// surprising outcome. Each handled orphan is removed from the backup map (callers persist or delete
    /// the file afterwards).
    /// </summary>
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

    /// <summary>
    /// Renames the low-mode visual mods between <c>X.jar</c> and <c>X.jar.disabled</c>.
    ///
    /// Runs at every launch, which is also what makes it survive syncing: an update always delivers the jar
    /// under its enabled name, so after a sync both the fresh <c>X.jar</c> and a stale <c>X.jar.disabled</c>
    /// (possibly an older version) can coexist - NeoForge would crash on the duplicate mod id. The rule is
    /// therefore: within one prefix, the plain <c>.jar</c> is the truth, and every <c>.disabled</c> twin is a
    /// leftover to delete before renaming the truth to the desired state.
    /// </summary>
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
    /// The name a mod file should be shared under: a low-mode-managed <c>.jar.disabled</c> is reported as its
    /// plain <c>.jar</c>, so this machine's low mode never uploads its disabled state as everyone's baseline.
    /// Files disabled by hand (not in the managed prefix list) keep their name - disabling a mod for the whole
    /// pack by renaming it IS a deliberate share action, and only low mode's own renames are transparent.
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

    /// <summary>
    /// The bytes a file should be <b>shared</b> as: this machine's low-mode edits reverted to the values the
    /// player had before low mode was switched on. Returns <c>null</c> when there is nothing to mask, and the
    /// caller should then upload the file as it is on disk.
    ///
    /// <para>This is the config-file counterpart of <see cref="UploadNameFor"/>, and together they are what let
    /// low mode edit <b>any</b> config, shared or not. Before this existed the profile had to be confined to
    /// Local-ruled files, because a shared file low mode had turned down would upload one machine's low
    /// settings as everyone's baseline — and worse, switching low mode off on one machine would then push the
    /// restored "high" values over the other player's. Low mode is per-machine state; it must never travel.</para>
    ///
    /// <para>Only keys low mode itself wrote are reverted. If the player changed one of them by hand while low
    /// mode was on, that value is theirs and is shared as-is — pushing our value over it would be the same
    /// mistake in the other direction.</para>
    /// </summary>
    public static byte[]? SharedContentFor(string gameDir, string relativePath)
    {
        // Cheapest possible rejection first: an upload asks this about every shared file in the pack - well over
        // a thousand of them - and all but a handful can never match a profile entry.
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

        // Matches what WriteValue would have left on disk with low mode off: platform newlines, trailing
        // newline, no BOM. If the file originally used different line endings the bytes can still differ from
        // the pristine original, which costs one redundant re-upload of that file and nothing else.
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
    // options.txt is "key:value" per line; TOML and JSON are both "key = value" / "key": value. Rather than pull in
    // three parsers for what is a single scalar each, match the key in place and swap only what is to its right,
    // which also preserves comments, ordering and formatting exactly.

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

            // No BOM: NeoForge's TOML parser rejects one outright, and Minecraft's options reader is no happier.
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

    /// <summary>Reads the effective preference for a pack. Missing entry means ON.</summary>
    public static bool IsEnabled(AppSettings settings, Guid packId) =>
        !settings.PackLowMode.TryGetValue(packId, out var on) || on;
}
