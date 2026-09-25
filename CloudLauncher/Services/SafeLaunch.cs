using System.Diagnostics;
using System.IO;

namespace CloudLauncher.Services;

/// <summary>Opens links, folders and files through the shell without ever starting a program.</summary>
/// <remarks>Links come from descriptions, changelogs, store pages and other people's packs, so they
/// are not trusted: only http and https go to the browser. Local files open with their default app
/// only when the extension is on a short list of viewer types; anything else is shown in Explorer
/// instead.</remarks>
public static class SafeLaunch
{
    private static readonly HashSet<string> ViewerExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".md", ".json", ".json5", ".jsonc", ".toml", ".cfg", ".conf", ".ini",
        ".properties", ".yml", ".yaml", ".xml", ".csv", ".snbt", ".mcmeta", ".lang", ".zs",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tga",
    };

    /// <summary>True for an absolute http(s) URL with a host.</summary>
    public static bool IsWebUrl(string? url, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp) return false;
        if (parsed.IsUnc || parsed.IsFile || string.IsNullOrEmpty(parsed.Host)) return false;
        uri = parsed;
        return true;
    }

    /// <summary>Opens an http or https URL in the default browser. Anything else is refused.</summary>
    public static bool OpenUrl(string? url)
    {
        if (!IsWebUrl(url, out var uri))
        {
            AppLog.Log(nameof(SafeLaunch), $"Refused to open a link that is not http or https: {Describe(url)}");
            return false;
        }
        return Start(new ProcessStartInfo(uri!.AbsoluteUri) { UseShellExecute = true });
    }

    /// <summary>Opens a new e-mail to <paramref name="address"/> in the default mail app.</summary>
    public static bool OpenMail(string address, string? subject = null, string? body = null)
    {
        var query = new List<string>();
        if (!string.IsNullOrEmpty(subject)) query.Add("subject=" + Uri.EscapeDataString(subject));
        if (!string.IsNullOrEmpty(body)) query.Add("body=" + Uri.EscapeDataString(body));
        var link = "mailto:" + Uri.EscapeDataString(address) + (query.Count > 0 ? "?" + string.Join("&", query) : "");
        return Start(new ProcessStartInfo(link) { UseShellExecute = true });
    }

    /// <summary>Opens a folder in Explorer. The folder has to exist.</summary>
    public static bool OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string full;
        try { full = Path.GetFullPath(path); }
        catch { return false; }
        if (!Directory.Exists(full)) return false;
        var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        psi.ArgumentList.Add(full);
        return Start(psi);
    }

    /// <summary>Shows a file or folder selected in its parent folder. Selecting never opens it.</summary>
    public static bool RevealFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string full;
        try { full = Path.GetFullPath(path); }
        catch { return false; }
        if (Directory.Exists(full)) full = Path.TrimEndingDirectorySeparator(full);
        else if (!File.Exists(full)) return OpenFolder(Path.GetDirectoryName(full));
        // explorer.exe parses its own command line, so the path is quoted by hand here.
        var psi = new ProcessStartInfo("explorer.exe", $"/select,\"{full}\"") { UseShellExecute = false };
        return Start(psi);
    }

    /// <summary>Opens a local file with its default app when it is a text or image file; any other
    /// file is shown in Explorer rather than opened.</summary>
    public static bool OpenFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string full;
        try { full = Path.GetFullPath(path); }
        catch { return false; }
        if (Directory.Exists(full)) return OpenFolder(full);
        if (!File.Exists(full)) return false;
        if (!ViewerExtensions.Contains(Path.GetExtension(full))) return RevealFile(full);
        return Start(new ProcessStartInfo(full) { UseShellExecute = true });
    }

    private static bool Start(ProcessStartInfo psi)
    {
        try
        {
            using var _ = Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Log(nameof(SafeLaunch), $"Could not open {Describe(psi.FileName)}: {ex.Message}");
            return false;
        }
    }

    private static string Describe(string? s) => s is null ? "(nothing)" : s.Length > 120 ? s[..120] + "..." : s;
}
