using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CloudLauncher.Services;

/// <summary>
/// Keeps the ReLauncher mod's Java path (<c>config/relauncher.json</c>) in line with the Java the
/// launcher resolved. The mod relaunches the game in its own JVM, and an empty or stale path (e.g.
/// written on another machine) stops the pack from starting.
/// </summary>
public static class RelauncherConfigService
{
    /// <summary>
    /// Sets <c>javaPath</c> in <c>config/relauncher.json</c> to <paramref name="launcherJavaPath"/>
    /// when it is empty or different. Other fields are preserved.
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

            // ReLauncher runs the game windowless, so prefer javaw.exe when it sits next to java.exe.
            var desired = PreferWindowlessJava(launcherJavaPath);

            JsonNode? root;
            try
            {
                var text = File.ReadAllText(configPath);
                root = string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text);
            }
            catch
            {
                // Unparseable JSON: start fresh so the mod still launches.
                root = new JsonObject();
            }

            if (root is not JsonObject obj)
                obj = new JsonObject();

            var current = obj.TryGetPropertyValue("javaPath", out var node) ? node?.GetValue<string>() : null;
            if (string.Equals(current, desired, StringComparison.OrdinalIgnoreCase))
                return; // already correct

            obj["javaPath"] = desired;

            // Relaxed encoder so '+' in the args field isn't written as "\u002B" (the default HTML-safe
            // encoder does that). Backslashes are still escaped, as JSON requires.
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            File.WriteAllText(configPath, obj.ToJsonString(options));
            report?.Invoke($"Corrected relauncher.json javaPath > {desired}");
            AppLog.Log("relauncher", $"Set {configPath} javaPath to {desired} (was '{current}')");
        }
        catch (Exception ex)
        {
            // Never let this block a launch.
            AppLog.LogError("relauncher", ex);
        }
    }

    /// <summary>
    /// The parts of <c>config/relauncher.json</c> the launcher uses. <paramref name="Exists"/> is false
    /// when the pack doesn't ship the config. <paramref name="TargetJavaMajor"/> is the Java version
    /// the mod relaunches into (<c>"targetJavaVersion": "J25"</c> gives 25), or null if unset.
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

            // enableRelauncher defaults to true: shipping the config implies the mod is meant to run.
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
                    // "J25", "Java25" or "25" -> 25
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
