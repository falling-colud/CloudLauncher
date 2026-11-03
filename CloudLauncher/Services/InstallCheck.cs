using System.IO;
using System.Text.Json;

namespace CloudLauncher.Services;

/// <summary>Checks that the folder the launcher runs from still holds every file it was installed
/// with.</summary>
/// <remarks>
/// <para>The launcher is self-contained, and the .NET host does not look for its files on disk when it
/// starts: a missing one only shows when something first needs it, as a FileNotFoundException that can
/// close the launcher at any moment. A copy unzipped into Downloads lost 197 of its 277 files when that
/// folder was cleaned up, and closed every few minutes (0.9.10).</para>
/// <para>The list is the app's deps.json, the one the host reads. Only a self-contained install is
/// checked; a development build runs on the shared framework and keeps its own files in bin.</para>
/// </remarks>
public static class InstallCheck
{
    /// <summary>What the last <see cref="Run"/> found missing: file names, sorted.</summary>
    public static IReadOnlyList<string> Missing { get; private set; } = [];

    /// <summary>The folder the launcher runs from.</summary>
    public static string InstallDir => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>Looks for missing files in <see cref="InstallDir"/>, keeps the answer in
    /// <see cref="Missing"/> and logs it. Never throws: a check that cannot run finds nothing.</summary>
    public static IReadOnlyList<string> Run()
    {
        try
        {
            var deps = Path.Combine(InstallDir, typeof(InstallCheck).Assembly.GetName().Name + ".deps.json");
            var listed = 0;
            Missing = File.Exists(deps)
                ? FindMissing(InstallDir, File.ReadAllText(deps), out listed)
                : [Path.GetFileName(deps)];
            if (Missing.Count > 0)
            {
                const int Shown = 12;
                var names = string.Join(", ", Missing.Take(Shown))
                            + (Missing.Count > Shown ? $", and {Missing.Count - Shown} more" : "");
                AppLog.Log("install", listed > 0
                    ? $"{Missing.Count} of the {listed} files the launcher was installed with are missing from {InstallDir}: {names}."
                    : $"The launcher's file list ({names}) is missing from {InstallDir}.");
            }
        }
        catch (Exception ex)
        {
            AppLog.LogError("install-check", ex);
            Missing = [];
        }
        return Missing;
    }

    /// <summary>The files <paramref name="depsJson"/> lists for this runtime that are in neither place
    /// a publish can put them: their own path under <paramref name="installDir"/>, or the folder itself
    /// (a package's assembly is listed as <c>lib/net8.0/X.dll</c> but published as <c>X.dll</c>, while
    /// WebView2's loader keeps its <c>runtimes/win-x64/native</c> path). Empty for a deps.json that is
    /// not self-contained.</summary>
    /// <param name="listed">How many files the list names.</param>
    internal static List<string> FindMissing(string installDir, string depsJson, out int listed)
    {
        listed = 0;
        var missing = new List<string>();
        using var doc = JsonDocument.Parse(depsJson);
        var root = doc.RootElement;
        if (!root.TryGetProperty("libraries", out var libraries)
            || !libraries.EnumerateObject().Any(l => l.Value.TryGetProperty("type", out var type) && type.ValueEquals("runtimepack"))
            || !root.TryGetProperty("runtimeTarget", out var runtimeTarget)
            || !runtimeTarget.TryGetProperty("name", out var targetName)
            || !root.TryGetProperty("targets", out var targets)
            || !targets.TryGetProperty(targetName.GetString() ?? "", out var target))
            return missing;

        foreach (var library in target.EnumerateObject())
        {
            foreach (var kind in new[] { "runtime", "native" })
            {
                if (!library.Value.TryGetProperty(kind, out var assets)) continue;
                foreach (var asset in assets.EnumerateObject())
                {
                    listed++;
                    var relative = asset.Name.Replace('/', Path.DirectorySeparatorChar);
                    var name = Path.GetFileName(relative);
                    if (!File.Exists(Path.Combine(installDir, relative)) && !File.Exists(Path.Combine(installDir, name)))
                        missing.Add(name);
                }
            }
        }
        missing.Sort(StringComparer.OrdinalIgnoreCase);
        return missing;
    }
}
