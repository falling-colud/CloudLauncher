using System.IO;
using System.Text.Json;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>
/// The last instance list the server successfully answered with, kept on disk so a server that is
/// briefly unreachable does not read as "your instances are gone".
/// </summary>
/// <remarks>
/// On 2026-09-19 the server's database spent minutes in crash recovery after the host's disk filled.
/// Every <c>GET /packs</c> failed, the launcher showed an empty library, and the first thing a user
/// said was "my modpack disappeared from it". Nothing had: the answer was simply missing. Serving the
/// last known list (clearly labelled, and replaced the moment a real answer arrives) is both truer and
/// far less alarming — and it makes the whole library readable offline, which it never was before.
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
