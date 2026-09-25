using System.IO;
using System.Text.Json;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>
/// The last instance list the server successfully answered with, kept on disk so a server that is
/// briefly unreachable does not read as "your instances are gone".
/// </summary>
/// <remarks>
/// The cached list is shown, labelled as such, until a real answer arrives. It also makes the library
/// readable offline.
/// </remarks>
public static class PackListCache
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private static readonly object Gate = new();

    private static string Path => System.IO.Path.Combine(AppSettings.DataRootPath, "packs-cache.json");

    /// <summary>When the cached copy was written, or null when there is none.</summary>
    public static DateTimeOffset? CachedAt
    {
        get
        {
            try { return File.Exists(Path) ? File.GetLastWriteTimeUtc(Path) : null; }
            catch { return null; }
        }
    }

    /// <summary>
    /// How old the cached copy is, in words: "just now", "3 hours ago", "on 14 Sep". Null when
    /// there is no cache.
    /// </summary>
    public static string? AgeInWords() => Describe(CachedAt);

    /// <summary>The same wording for any timestamp, so other caches can say it the same way.</summary>
    public static string? Describe(DateTimeOffset? at)
    {
        if (at is not { } when) return null;
        var age = DateTimeOffset.UtcNow - when;
        if (age < TimeSpan.Zero) return "just now"; // clock moved backwards
        if (age < TimeSpan.FromMinutes(2)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} minutes ago";
        if (age < TimeSpan.FromHours(2)) return "an hour ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours} hours ago";
        if (age < TimeSpan.FromDays(2)) return "yesterday";
        if (age < TimeSpan.FromDays(7)) return $"{(int)age.TotalDays} days ago";
        // TimeFormat localises and converts to local time itself, so no ToLocalTime() here.
        // MonthDayOrDate adds the year for older dates, e.g. a backup from last year.
        return "on " + TimeFormat.MonthDayOrDate(when);
    }

    public static void Save(IReadOnlyList<PackSummary> packs)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var tmp = Path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(packs, Json));
                File.Move(tmp, Path, overwrite: true);
            }
        }
        catch { /* a cache that cannot be written is not worth failing a request over */ }
    }

    /// <summary>The last good list, or null when there is none (or it is unreadable).</summary>
    public static List<PackSummary>? Load()
    {
        try
        {
            lock (Gate)
            {
                if (!File.Exists(Path)) return null;
                return JsonSerializer.Deserialize<List<PackSummary>>(File.ReadAllText(Path), Json);
            }
        }
        catch { return null; }
    }

    public static void Clear()
    {
        try { lock (Gate) File.Delete(Path); } catch { /* nothing to do */ }
    }
}
