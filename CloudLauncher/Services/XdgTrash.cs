using System.IO;

namespace CloudLauncher.Services;

/// <summary>
/// The desktop trash on Linux (the freedesktop.org trash spec): what the Windows launcher sends to the
/// Recycle Bin goes here, so a deleted instance can still be brought back from the file manager.
/// </summary>
/// <remarks>Only the home trash (<c>$XDG_DATA_HOME/Trash</c>, usually <c>~/.local/share/Trash</c>) is
/// used. A folder on another drive cannot be moved there, and <see cref="Move"/> says so by throwing,
/// so the caller decides whether to delete it outright.</remarks>
public static class XdgTrash
{
    /// <summary>The home trash folder.</summary>
    public static string HomeTrash
    {
        get
        {
            var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrWhiteSpace(data) || !Path.IsPathRooted(data))
                data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return Path.Combine(data, "Trash");
        }
    }

    /// <summary>Moves a file or folder into the trash, with the record the file manager restores it
    /// from. Throws <see cref="IOException"/> when it cannot (another drive, no trash folder).</summary>
    public static void Move(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!File.Exists(full) && !Directory.Exists(full))
            throw new FileNotFoundException("Nothing to move to the trash.", full);

        var files = Path.Combine(HomeTrash, "files");
        var info = Path.Combine(HomeTrash, "info");
        Directory.CreateDirectory(files);
        Directory.CreateDirectory(info);

        // A name not yet in the trash: "name", "name.2", "name.3", ...
        var baseName = Path.GetFileName(full);
        var name = baseName;
        for (var n = 2; File.Exists(Path.Combine(info, name + ".trashinfo"))
                        || File.Exists(Path.Combine(files, name)) || Directory.Exists(Path.Combine(files, name)); n++)
            name = $"{baseName}.{n}";

        // The record first, as the spec asks: a trash entry without its file is cleaned up by file
        // managers, a file without its record could never be restored.
        var record = Path.Combine(info, name + ".trashinfo");
        File.WriteAllText(record,
            "[Trash Info]\n" +
            $"Path={EscapePath(full)}\n" +
            $"DeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n");
        try
        {
            var target = Path.Combine(files, name);
            if (Directory.Exists(full)) Directory.Move(full, target);
            else File.Move(full, target);
        }
        catch
        {
            try { File.Delete(record); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>The path as the spec stores it: percent-encoded, slashes kept.</summary>
    private static string EscapePath(string path) =>
        string.Join("/", path.Split('/').Select(Uri.EscapeDataString));
}
