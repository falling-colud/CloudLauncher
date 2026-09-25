using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CloudLauncher.Services;

/// <summary>
/// Repairs a half-downloaded MCEF native bundle before it can crash the client.
///
/// <para>MCEF downloads about 270&#160;MB of Chromium natives into <c>mods/mcef-libraries/</c> on
/// first run. The folder is excluded from pack sync, and after an interrupted download MCEF sees the
/// folder and never retries, so the next launch dies with
/// <c>UnsatisfiedLinkError: Can't load library: ...\libGLESv2.dll</c>.</para>
///
/// <para>If natives are missing, the bundle is deleted so MCEF downloads it again. It's an unsynced
/// cache, so that only costs bandwidth. The check is presence plus a plausible size, not a checksum;
/// hashing 270&#160;MB on every launch would cost too much.</para>
/// </summary>
public static class McefLibrariesService
{
    /// <summary>Files CEF loads at startup, with the smallest size a real copy could plausibly be.</summary>
    private static readonly (string Name, long MinBytes)[] RequiredNatives =
    [
        ("libcef.dll",     64L * 1024 * 1024),   // ~200 MB in practice
        ("libGLESv2.dll",   1L * 1024 * 1024),   // ~7 MB, the one named in the crash
        ("chrome_elf.dll",       256L * 1024),
        ("jcef.dll",             256L * 1024),
    ];

    /// <summary>Verifies the pack's MCEF bundle and clears it when it is unusable. Returns a line for
    /// the launch log, or an empty string when there is nothing to say.</summary>
    public static string Verify(string gameDir)
    {
        try
        {
            var root = Path.Combine(gameDir, "mods", "mcef-libraries");
            if (!Directory.Exists(root)) return "";        // nothing downloaded yet; MCEF will fetch it

            var problem = Diagnose(root);
            if (problem is null) return "";

            Directory.Delete(root, recursive: true);
            return $"MCEF: {problem} - cleared, it will download again on this launch";
        }
        catch (Exception ex)
        {
            // A launch is never worth blocking over a cache folder.
            AppLog.Log("launch", $"MCEF: check skipped ({ex.Message})");
            return "";
        }
    }

    /// <summary>Describes what is wrong with a bundle, or null when it looks complete. Separate so the
    /// reason can be logged.</summary>
    private static string? Diagnose(string root)
    {
        var platform = Directory.EnumerateDirectories(root)
            .FirstOrDefault(d => Path.GetFileName(d).Contains('_'));   // windows_amd64, linux_amd64, macos_arm64, ...
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
