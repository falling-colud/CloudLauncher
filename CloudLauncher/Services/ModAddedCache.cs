using System.IO;
using System.Text.Json;

namespace CloudLauncher.Services;

/// <summary>
/// Remembers when each mod first appeared in a pack, so the List view can show "added 12 Sep, 21:04"
/// and the "Add date" sort is right.
///
/// <para>The jar's creation time alone can't answer this: updating a mod writes a new file and
/// deletes the old one, so every updated mod would look like it was added today. The first time a
/// mod is seen, its file time is recorded under its stable store key (<c>modrinth:...</c> /
/// <c>curseforge:...</c>, falling back to the file name), which survives updates.</para>
///
/// <para>Per machine: it lives next to the fingerprint cache instead of in the pack's synced
/// <c>mods.json</c>, because it's a local question and the file is rewritten on every mod scan.</para>
/// </summary>
public sealed class ModAddedCache
{
    private readonly string _path;
    private readonly object _lock = new();
    private readonly Dictionary<Guid, Dictionary<string, DateTime>> _packs = new();
    private bool _dirty;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public ModAddedCache()
    {
        _path = Path.Combine(GetDataRoot(), "mod-added.json");
        Load();
    }

    /// <summary>When this mod first showed up in the pack: the earliest of any date recorded under its
    /// keys and the jar's file time. Stored under the mod's primary key so later updates keep the
    /// original date.</summary>
    public DateTime Resolve(Guid packId, IReadOnlyList<string> candidateKeys, DateTime fileTime)
    {
        if (candidateKeys.Count == 0) return fileTime;

        lock (_lock)
        {
            if (!_packs.TryGetValue(packId, out var map))
                _packs[packId] = map = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

            DateTime? known = null;
            foreach (var key in candidateKeys)
                if (map.TryGetValue(key, out var stored) && stored > DateTime.MinValue && (known is null || stored < known))
                    known = stored;

            // A jar with no readable file time (locked, or lost by the filesystem) arrives as
            // DateTime.MinValue and must never replace or win over a real recorded date.
            var usable = fileTime > DateTime.MinValue;
            if (!usable && known is null) return fileTime;
            var first = !usable ? known!.Value
                : known is null ? fileTime
                : fileTime < known.Value ? fileTime : known.Value;

            var primary = candidateKeys[0];
            if (!map.TryGetValue(primary, out var current) || current != first)
            {
                map[primary] = first;
                _dirty = true;
            }
            return first;
        }
    }

    /// <summary>Drops entries for mods that are no longer in the pack. Only call this after a complete
    /// identity pass: <paramref name="aliveKeys"/> must hold every key of every installed mod, or the
    /// mods it missed lose their dates.</summary>
    public void Prune(Guid packId, IEnumerable<string> aliveKeys)
    {
        var alive = new HashSet<string>(aliveKeys, StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            if (!_packs.TryGetValue(packId, out var map)) return;
            foreach (var key in map.Keys.Where(k => !alive.Contains(k)).ToList())
            {
                map.Remove(key);
                _dirty = true;
            }
        }
    }

    public void Flush()
    {
        Dictionary<Guid, Dictionary<string, DateTime>> snapshot;
        lock (_lock)
        {
            if (!_dirty) return;
            snapshot = _packs.ToDictionary(p => p.Key, p => new Dictionary<string, DateTime>(p.Value, StringComparer.OrdinalIgnoreCase));
            _dirty = false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, JsonOpts));
            File.Move(tmp, _path, overwrite: true);
        }
        catch { /* best-effort: a lost cache only costs the added dates */ }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var file = JsonSerializer.Deserialize<Dictionary<Guid, Dictionary<string, DateTime>>>(File.ReadAllText(_path), JsonOpts);
            if (file is null) return;
            lock (_lock)
            {
                foreach (var (packId, map) in file)
                    _packs[packId] = new Dictionary<string, DateTime>(map, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { /* corrupt cache: start fresh, dates fall back to file times */ }
    }

    private static string GetDataRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CloudLauncher",
        Environment.GetEnvironmentVariable("CL_PROFILE") is { Length: > 0 } profile ? profile : "default");
}
