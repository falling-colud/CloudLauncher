using System.Diagnostics;
using System.Text;
using Avalonia.Platform;
using CloudLauncher.Services;

namespace CloudLauncher;

/// <summary>
/// The launcher's entry in the desktop's app menu (the freedesktop way): <c>cloudlauncher.desktop</c> in
/// <c>$XDG_DATA_HOME/applications</c>, starting this copy of the launcher, and its icon in the hicolor
/// theme. Both files are in the user's home folder; nothing else is touched. tools/release/linux/install.sh
/// writes the same two files.
/// </summary>
/// <remarks>Install and Uninstall write files and run the desktop's cache tools, so call them off the UI
/// thread.</remarks>
public static class DesktopIntegration
{
    public const string EntryFileName = "cloudlauncher.desktop";
    private const string IconName = "cloudlauncher";
    private static readonly Uri IconAsset = new("avares://cloudlauncher/Assets/cloudlauncher.png");

    /// <summary>An app menu entry is a Linux desktop's.</summary>
    public static bool IsSupported => OperatingSystem.IsLinux();

    /// <summary><c>$XDG_DATA_HOME</c>, else <c>~/.local/share</c>. A relative XDG_DATA_HOME is ignored, as
    /// the base directory spec says.</summary>
    public static string DataHome
    {
        get
        {
            var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            return !string.IsNullOrWhiteSpace(data) && Path.IsPathRooted(data)
                ? data
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }
    }

    public static string EntryPath => Path.Combine(DataHome, "applications", EntryFileName);

    public static string IconPath => Path.Combine(DataHome, "icons", "hicolor", "256x256", "apps", IconName + ".png");

    /// <summary>True when the app menu has a CloudLauncher entry (this copy's or another's).</summary>
    public static bool IsInstalled => File.Exists(EntryPath);

    /// <summary>The launcher program the menu entry starts, as its TryExec line names it; null when there
    /// is no entry or it names none.</summary>
    public static string? InstalledTarget
    {
        get
        {
            try
            {
                if (!File.Exists(EntryPath)) return null;
                var line = File.ReadLines(EntryPath).FirstOrDefault(l => l.StartsWith("TryExec=", StringComparison.Ordinal));
                return line is null ? null : UnescapeString(line["TryExec=".Length..]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>The program this launcher runs as, when it is one that an app menu can start.</summary>
    public static string? LauncherPath
    {
        get
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return null;
            // Started through "dotnet cloudlauncher.dll": the menu would start the dotnet host alone.
            if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return null;
            return exe;
        }
    }

    /// <summary>True when the menu entry starts this copy of the launcher.</summary>
    public static bool PointsHere => InstalledTarget is { } target && LauncherPath is { } exe
                                     && string.Equals(target, exe, StringComparison.Ordinal);

    /// <summary>Writes the menu entry for this copy of the launcher, and its icon, replacing any entry
    /// there was.</summary>
    /// <exception cref="InvalidOperationException">Not on Linux, or the launcher's own path cannot go in a
    /// menu entry; the message says so in a sentence.</exception>
    public static void Install()
    {
        if (!IsSupported) throw new InvalidOperationException("The app menu entry is for Linux desktops.");
        var exe = LauncherPath
                  ?? throw new InvalidOperationException("Start the launcher from its own program file to add it to the app menu.");
        var entry = BuildEntry(exe);

        // The icon first, so the menu never shows the entry without it.
        using (var icon = AssetLoader.Open(IconAsset))
        using (var bytes = new MemoryStream())
        {
            icon.CopyTo(bytes);
            WriteFile(IconPath, bytes.ToArray());
        }
        WriteFile(EntryPath, Encoding.UTF8.GetBytes(entry));
        RefreshCaches();
        AppLog.Log("desktop", $"Added to the app menu: {EntryPath} starts {exe}.");
    }

    /// <summary>Removes the menu entry and its icon. The launcher itself and its data stay.</summary>
    public static void Uninstall()
    {
        var removed = false;
        foreach (var path in new[] { EntryPath, IconPath })
        {
            if (!File.Exists(path)) continue;
            File.Delete(path);
            removed = true;
        }
        if (!removed) return;
        RefreshCaches();
        AppLog.Log("desktop", "Removed from the app menu.");
    }

    /// <summary>The desktop entry that starts <paramref name="exePath"/>, in the Desktop Entry
    /// Specification's format (UTF-8, LF line ends).</summary>
    public static string BuildEntry(string exePath)
    {
        if (exePath.Any(char.IsControl))
            throw new InvalidOperationException("The launcher's folder has a name an app menu entry cannot hold. Move it to a plain folder name first.");
        var sb = new StringBuilder();
        sb.Append("[Desktop Entry]\n");
        sb.Append("Type=Application\n");
        sb.Append("Name=CloudLauncher\n");
        sb.Append("Comment=A cloud-synced Minecraft launcher\n");
        sb.Append("Exec=").Append(ExecArgument(exePath)).Append(" %U\n");
        sb.Append("TryExec=").Append(EscapeString(exePath)).Append('\n');
        sb.Append("Icon=").Append(IconName).Append('\n');
        sb.Append("Terminal=false\n");
        sb.Append("Categories=Game;\n");
        sb.Append("StartupWMClass=cloudlauncher\n");
        return sb.ToString();
    }

    /// <summary>One quoted Exec argument. The spec's quoting rule escapes <c>" ` $ \</c> with a backslash
    /// inside quotes; then the value, as a string, escapes every backslash again (so a literal backslash
    /// ends up as four, and <c>$</c> as <c>\\$</c>); a literal <c>%</c> is <c>%%</c>.</summary>
    private static string ExecArgument(string path)
    {
        var quoted = new StringBuilder("\"");
        foreach (var ch in path)
        {
            if (ch is '"' or '`' or '$' or '\\') quoted.Append('\\');
            quoted.Append(ch);
        }
        quoted.Append('"');
        return EscapeString(quoted.ToString().Replace("%", "%%", StringComparison.Ordinal));
    }

    /// <summary>A string value: backslashes doubled (control characters are refused before this).</summary>
    private static string EscapeString(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal);

    private static string UnescapeString(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                i++;
                sb.Append(value[i] switch { 's' => ' ', 'n' => '\n', 't' => '\t', 'r' => '\r', var other => other });
            }
            else sb.Append(value[i]);
        }
        return sb.ToString();
    }

    /// <summary>Writes beside the target and renames it into place, so a menu never reads half a file.</summary>
    private static void WriteFile(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".new";
        File.WriteAllBytes(temp, content);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Tells the desktop about the change where it keeps caches: the menu's MIME cache, and the
    /// icon cache, but only when the user's hicolor folder already has one (making one there would leave
    /// a cache other programs' icons are then missing from). Failures only go to the log.</summary>
    private static void RefreshCaches()
    {
        RunQuietly("update-desktop-database", Path.GetDirectoryName(EntryPath)!);
        var hicolor = Path.Combine(DataHome, "icons", "hicolor");
        if (File.Exists(Path.Combine(hicolor, "icon-theme.cache")))
            RunQuietly("gtk-update-icon-cache", "-q", "-t", "-f", hicolor);
    }

    private static void RunQuietly(string tool, params string[] args)
    {
        var exe = FindOnPath(tool);
        if (exe is null) return;
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi);
            if (process is null) return;
            // Drain both pipes so a chatty tool cannot stall on a full one.
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(); } catch { /* it ended meanwhile */ }
                AppLog.Log("desktop", $"{tool} did not finish in 10 s.");
            }
        }
        catch (Exception ex)
        {
            AppLog.Log("desktop", $"{tool} did not run: {ex.Message}");
        }
    }

    private static string? FindOnPath(string tool)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), tool);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* a malformed PATH entry */ }
        }
        return null;
    }
}
