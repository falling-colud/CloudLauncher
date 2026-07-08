using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CloudLauncher.Services;

/// <summary>
/// Some modpacks ship the "ReLauncher" mod, which keeps its own copy of the Java
/// path in <c>config/relauncher.json</c> and uses it to relaunch the game in a
/// separate JVM. When that path is empty or points at a Java that no longer
/// exists (e.g. it was baked in by another launcher / a different machine), the
/// modpack fails to start. We force it to match the Java the launcher actually
/// resolved for this pack so the two never disagree.
/// </summary>
public static class RelauncherConfigService
{
    /// <summary>
    /// If <paramref name="gameDir"/> contains <c>config/relauncher.json</c>, rewrite its
    /// <c>javaPath</c> to <paramref name="launcherJavaPath"/> when the stored value is
    /// empty or doesn't match the resolved launcher Java. Other fields are preserved.
    /// </summary>
    public static void FixJavaPath(string gameDir, string launcherJavaPath, Action<string>? report = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gameDir) || string.IsNullOrWhiteSpace(launcherJavaPath))
                return;

            var configPath = Path.Combine(gameDir, "config", "relauncher.json");
            if (!File.Exists(configPath))
                return;

            // ReLauncher launches the game windowless, so prefer javaw.exe over java.exe
            // when both live in the same bin/ directory (matches what the mod expects).
            var desired = PreferWindowlessJava(launcherJavaPath);

            JsonNode? root;
            try
            {
                var text = File.ReadAllText(configPath);
                root = string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text);
            }
            catch
            {
                // Corrupt/unparseable JSON — start fresh so the mod still launches.
                root = new JsonObject();
            }

            if (root is not JsonObject obj)
                obj = new JsonObject();

            var current = obj.TryGetPropertyValue("javaPath", out var node) ? node?.GetValue<string>() : null;
            if (string.Equals(current, desired, StringComparison.OrdinalIgnoreCase))
                return; // already correct

            obj["javaPath"] = desired;

            // Use the relaxed encoder so characters like '+' in the args field aren't
            // over-escaped to "\u002B" (the default HTML-safe encoder does that and
            // mangles the file). Backslashes in paths are still escaped to "\\" — that
            // is mandatory JSON and decodes back to a single backslash.
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            File.WriteAllText(configPath, obj.ToJsonString(options));
            report?.Invoke($"Corrected relauncher.json javaPath → {desired}");
            AppLog.Log("relauncher", $"Set {configPath} javaPath to {desired} (was '{current}')");
        }
        catch (Exception ex)
        {
            // Never let this block a launch.
            AppLog.LogError("relauncher", ex);
        }
    }

    /// <summary>
    /// Snapshot of the bits of <c>config/relauncher.json</c> the launcher cares about.
    /// <paramref name="Exists"/> is false when the pack doesn't ship the mod's config.
    /// <paramref name="TargetJavaMajor"/> is the JVM the mod will relaunch into
    /// (parsed from <c>"targetJavaVersion": "J25"</c> → 25), or null if unset.
    /// </summary>
    public sealed record RelauncherInfo(bool Exists, bool Enabled, int? TargetJavaMajor);

    /// <summary>Reads the ReLauncher config for <paramref name="gameDir"/> without modifying it.</summary>
    public static RelauncherInfo ReadInfo(string gameDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gameDir))
                return new RelauncherInfo(false, false, null);

            var configPath = Path.Combine(gameDir, "config", "relauncher.json");
            if (!File.Exists(configPath))
                return new RelauncherInfo(false, false, null);

            var text = File.ReadAllText(configPath);
            if (string.IsNullOrWhiteSpace(text) || JsonNode.Parse(text) is not JsonObject obj)
                return new RelauncherInfo(true, false, null);

            // enableRelauncher defaults to true when absent — the config's presence implies intent.
            var enabled = true;
            if (obj.TryGetPropertyValue("enableRelauncher", out var en) && en is not null)
            {
                try { enabled = en.GetValue<bool>(); } catch { /* keep default */ }
            }

            int? target = null;
            if (obj.TryGetPropertyValue("targetJavaVersion", out var node) && node is not null)
            {
                var raw = node.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    // "J25" / "Java25" / "25" → 25
                    var match = Regex.Match(raw, @"\d+");
                    if (match.Success && int.TryParse(match.Value, out var major))
                        target = major;
                }
            }

            return new RelauncherInfo(true, enabled, target);
        }
        catch (Exception ex)
        {
            AppLog.LogError("relauncher", ex);
            return new RelauncherInfo(false, false, null);
        }
    }

    private static string PreferWindowlessJava(string javaPath)
    {
        if (!OperatingSystem.IsWindows())
            return javaPath;

        var fileName = Path.GetFileName(javaPath);
        if (!fileName.Equals("java.exe", StringComparison.OrdinalIgnoreCase))
            return javaPath;

        var dir = Path.GetDirectoryName(javaPath);
        if (string.IsNullOrEmpty(dir))
            return javaPath;

        var javaw = Path.Combine(dir, "javaw.exe");
        return File.Exists(javaw) ? javaw : javaPath;
    }
}
