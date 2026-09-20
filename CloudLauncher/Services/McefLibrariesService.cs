using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CloudLauncher.Services;

/// <summary>
/// Repairs a half-downloaded MCEF native bundle before it can crash the client.
///
/// <para>MCEF fetches ~270&#160;MB of Chromium natives into <c>mods/mcef-libraries/</c> on first
/// run. That folder is deliberately excluded from pack sync, so every player downloads it
/// themselves, and an interrupted download leaves the folder present but incomplete. MCEF treats
/// "folder is there" as "already installed", so it never retries — and the next launch dies during
/// client init with <c>UnsatisfiedLinkError: Can't load library: …\libGLESv2.dll</c>, which reads
/// like a hard crash rather than a download that needs finishing.</para>
///
/// <para>This checks the bundle actually contains the natives it should, and if it does not,
/// deletes it so MCEF downloads it again cleanly. The folder is a cache and is never synced, so
/// deleting it costs bandwidth and nothing else.</para>
///
/// <para>The check is deliberately shallow: presence and a plausible size for the files that CEF
/// loads first. It is not a checksum — MCEF keeps its own <c>.sha256</c> beside the folder for
/// that, and re-hashing 270&#160;MB on every launch would be worse than the bug.</para>
/// </summary>
public static class McefLibrariesService
{
    /// <summary>Files CEF loads at startup, with the smallest size a real copy could plausibly be.</summary>
    private static readonly (string Name, long MinBytes)[] RequiredNatives =
    [
        ("libcef.dll",     64L * 1024 * 1024),   // ~200 MB in practice
        ("libGLESv2.dll",   1L * 1024 * 1024),   // ~7 MB — the one in the crash report
        ("chrome_elf.dll",       256L * 1024),
        ("jcef.dll",             256L * 1024),
    ];

    /// <summary>
    /// Verifies the pack's MCEF bundle and clears it when it is unusable. Returns a line for the
    /// launch log, or an empty string when there is nothing to say.
    /// </summary>
    public static string Verify(string gameDir)
    {
        try
        {
            var root = Path.Combine(gameDir, "mods", "mcef-libraries");
            if (!Directory.Exists(root)) return "";        // nothing downloaded yet; MCEF will fetch it

            var problem = Diagnose(root);
            if (problem is null) return "";

            Directory.Delete(root, recursive: true);
            return $"MCEF: {problem} — cleared, it will download again on this launch";
        }
        catch (Exception ex)
        {
            // A launch is never worth blocking over a cache folder.
            AppLog.Log("launch", $"MCEF: check skipped ({ex.Message})");
            return "";
        }
    }

    /// <summary>
    /// Describes what is wrong with a bundle, or null when it looks complete. Split out so the
    /// reason can be logged — "libGLESv2.dll is 0 bytes" is a very different report from
    /// "no platform folder", and the difference matters when a player asks why.
    /// </summary>
    private static string? Diagnose(string root)
    {
        var platform = Directory.EnumerateDirectories(root)
            .FirstOrDefault(d => Path.GetFileName(d).Contains('_'));   // windows_amd64, linux_amd64, macos_arm64…
        if (platform is null)
            return "native folder is missing";

        var missing = new List<string>();
        foreach (var (name, minBytes) in RequiredNatives)
        {
            var file = Path.Combine(platform, name);
            if (!File.Exists(file)) { missing.Add($"{name} is missing"); continue; }
            var len = new FileInfo(file).Length;
            if (len < minBytes) missing.Add($"{name} is only {len / 1024} KB");
        }

        if (missing.Count == 0) return null;
        return "download is incomplete (" + string.Join(", ", missing.Take(2))
             + (missing.Count > 2 ? $", +{missing.Count - 2} more" : "") + ")";
    }
}
