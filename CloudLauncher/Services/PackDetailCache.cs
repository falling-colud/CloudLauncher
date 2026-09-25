using System.IO;
using System.Text.Json;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>The last full answer the server gave for each instance, kept on disk beside the pack
/// list so launching, the detail page and joining a server work offline.</summary>
/// <remarks>Same folder and temp-file-and-move writes as <see cref="PackListCache"/>; write
/// failures are ignored.</remarks>
public static class PackDetailCache
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private static readonly object Gate = new();

    // Loaded once, then kept up to date by Save, so a lookup doesn't re-read the whole file.
    private static Dictionary<Guid, PackDetail>? _map;

    private static string Path => System.IO.Path.Combine(AppSettings.DataRootPath, "pack-details-cache.json");

    /// <summary>When the cached copy was last written, or null when there is none.</summary>
    public static DateTimeOffset? CachedAt
    {
        get
        {
            try { return File.Exists(Path) ? File.GetLastWriteTimeUtc(Path) : null; }
            catch { return null; }
        }
    }

    /// <summary>Records the server's answer for one instance.</summary>
    public static void Save(PackDetail detail)
    {
        try
        {
            lock (Gate)
            {
                var map = LoadMapLocked();
                map[detail.Id] = detail;
                WriteLocked(map);
            }
        }
        catch { /* not worth failing a request over */ }
    }

    /// <summary>The last good detail for this instance, or null when it was never fetched.</summary>
    public static PackDetail? Load(Guid id)
    {
        try
        {
            lock (Gate) return LoadMapLocked().GetValueOrDefault(id);
        }
        catch { return null; }
    }

    /// <summary>Every cached detail. Used by the disk scan to put real names on folders.</summary>
    public static IReadOnlyDictionary<Guid, PackDetail> LoadAll()
    {
        try
        {
            lock (Gate) return new Dictionary<Guid, PackDetail>(LoadMapLocked());
        }
        catch { return new Dictionary<Guid, PackDetail>(); }
    }

    /// <summary>Forgets one instance. Call it when the server says the pack is gone.</summary>
    public static void Remove(Guid id)
    {
        try
        {
            lock (Gate)
            {
                var map = LoadMapLocked();
                if (map.Remove(id)) WriteLocked(map);
            }
        }
        catch { /* nothing to do */ }
    }

    public static void Clear()
    {
        try
        {
            lock (Gate)
            {
                _map = new Dictionary<Guid, PackDetail>();
                File.Delete(Path);
            }
        }
        catch { /* nothing to do */ }
    }

    /// <summary>A <see cref="PackDetail"/> built from the list entry and the pack folder, for an
    /// instance whose detail page was never opened. Rules come from <c>.rules.json</c>; collaborators and
    /// teams are left empty rather than guessed.</summary>
    public static PackDetail Synthesise(PackSummary s, IReadOnlyList<PackFileRule> rules) =>
        new(s.Id, s.Name, s.Description, s.OwnerId, s.OwnerUsername, s.Visibility, s.IsShared,
            s.IsEmpty, s.MinecraftVersion, s.Loader, s.LoaderVersion, s.CreatedAt, s.UpdatedAt,
            s.EffectivePermissions, Collaborators: [], Teams: [], Rules: rules, Summary: s.Summary);

    /// <summary>The list-shaped view of a cached detail.</summary>
    public static PackSummary ToSummary(PackDetail d) =>
        new(d.Id, d.Name, d.Description, d.OwnerId, d.OwnerUsername, d.Visibility, d.IsShared,
            d.IsEmpty, d.MinecraftVersion, d.Loader, d.LoaderVersion, d.CreatedAt, d.UpdatedAt,
            d.EffectivePermissions, d.Summary);

    private static Dictionary<Guid, PackDetail> LoadMapLocked()
    {
        if (_map is not null) return _map;
        try
        {
            if (File.Exists(Path))
                _map = JsonSerializer.Deserialize<Dictionary<Guid, PackDetail>>(File.ReadAllText(Path), Json);
        }
        catch { /* corrupt: start over rather than never caching again */ }
        return _map ??= new Dictionary<Guid, PackDetail>();
    }

    private static void WriteLocked(Dictionary<Guid, PackDetail> map)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(map, Json));
        File.Move(tmp, Path, overwrite: true);
    }
}
