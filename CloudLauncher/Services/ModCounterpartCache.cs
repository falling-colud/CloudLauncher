using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudLauncher.Services;

/// <summary>
/// Remembers, across sessions, which project on the <em>other</em> store corresponds to an installed
/// mod — and, just as importantly, which mods have no counterpart at all.
///
/// <para>The browse page used to look this up afresh every time it opened: one or two search calls
/// per installed mod that resolved on a single store, which for a CurseForge pack of four hundred mods
/// is hundreds of Modrinth searches in a burst — the pattern that got the launcher server rate-limited.
/// A counterpart is a stable fact, so it is worth keeping on disk: a hit for a month, a miss for a few
/// days (a mod may get published on the other store later).</para>
/// </summary>
public sealed class ModCounterpartCache
{
    private static readonly TimeSpan HitTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan MissTtl = TimeSpan.FromDays(3);

    private readonly object _lock = new();
    private readonly Dictionary<string, CounterpartEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;
    private bool _dirty;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ModCounterpartCache()
    {
        _path = Path.Combine(DataRoot(), "mod-counterparts.json");
        Load();
    }

    private static string Key(ModSource target, ModSummary source) => $"{target}<-{source.Source}:{source.Id}";

    /// <summary>True when a fresh answer is on file. <paramref name="counterpart"/> is null for a
    /// remembered "no counterpart".</summary>
    public bool TryGet(ModSource target, ModSummary source, out ModSummary? counterpart)
    {
        counterpart = null;
        lock (_lock)
        {
            if (!_entries.TryGetValue(Key(target, source), out var e)) return false;
            var ttl = e.Mod is null ? MissTtl : HitTtl;
            if (DateTimeOffset.UtcNow - e.CheckedAt > ttl) return false;
            counterpart = e.Mod;
            return true;
        }
    }

    public void Remember(ModSource target, ModSummary source, ModSummary? counterpart)
    {
        lock (_lock)
        {
            _entries[Key(target, source)] = new CounterpartEntry { Mod = counterpart, CheckedAt = DateTimeOffset.UtcNow };
            _dirty = true;
        }
    }

    public void Flush()
    {
        Dictionary<string, CounterpartEntry> snapshot;
        lock (_lock)
        {
            if (!_dirty) return;
            snapshot = new Dictionary<string, CounterpartEntry>(_entries, StringComparer.OrdinalIgnoreCase);
            _dirty = false;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, JsonOpts));
            File.Move(tmp, _path, overwrite: true);
        }
        catch { /* best-effort cache */ }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, CounterpartEntry>>(File.ReadAllText(_path), JsonOpts);
            if (loaded is null) return;
            lock (_lock)
            {
                foreach (var (k, v) in loaded) _entries[k] = v;
            }
        }
        catch { /* corrupt cache — start fresh */ }
    }

    private static string DataRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CloudLauncher",
        Environment.GetEnvironmentVariable("CL_PROFILE") is { Length: > 0 } profile ? profile : "default");

    public sealed class CounterpartEntry
    {
        public ModSummary? Mod { get; set; }
        public DateTimeOffset CheckedAt { get; set; }
    }
}
