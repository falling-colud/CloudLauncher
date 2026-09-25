using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudLauncher.Services;

/// <summary>
/// Caches per-file SHA512 + CurseForge fingerprints and last-known provider matches.
/// Entries are keyed by absolute path and invalidated when length or last-write time changes.
/// </summary>
public sealed class ModFingerprintCache
{
    private readonly object _lock = new();
    private readonly Dictionary<string, ModFingerprintCacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (ModSummary mod, ModVersion version)> _modrinthBySha =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<long, (ModSummary mod, ModVersion version)> _curseForgeByFingerprint = new();
    private readonly string _cachePath;
    private readonly object _writeGate = new();
    private bool _dirty;
    private Timer? _flushTimer;

    private const int FlushDelayMs = 2000;

    /// <summary>Not indented: on a big pack the file runs to about 21 MB indented, and every write
    /// re-serialises all of it.</summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ModFingerprintCache()
    {
        _cachePath = Path.Combine(GetDataRoot(), "mod-fingerprints.json");
        Load();
        // Backstop for the debounced write (ScanCaches does the same), so hashes computed just before
        // exit aren't lost and redone next launch.
        try { AppDomain.CurrentDomain.ProcessExit += (_, _) => WriteNow(); }
        catch { /* a host that will not let us hook exit still writes on the debounce */ }
    }

    public bool TryGet(string path, out ModFingerprintCacheEntry entry)
    {
        path = NormalizePath(path);
        lock (_lock)
        {
            if (!_entries.TryGetValue(path, out entry!))
                return false;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                Remove(path);
                return false;
            }

            if (info.Length != entry.Length || info.LastWriteTimeUtc.Ticks != entry.LastWriteUtcTicks)
                return false;

            if (entry.Match is not null)
            {
                if (!string.IsNullOrEmpty(entry.Sha512))
                    _modrinthBySha.TryAdd(entry.Sha512, (entry.Match.Mod, entry.Match.Version));
                if (entry.CurseForgeFingerprint != 0)
                    _curseForgeByFingerprint.TryAdd(entry.CurseForgeFingerprint, (entry.Match.Mod, entry.Match.Version));
            }
            IndexStoreMatches(entry);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Store(string path, string sha512, long curseForgeFingerprint, CachedModMatch? match = null)
    {
        path = NormalizePath(path);
        var info = new FileInfo(path);

        lock (_lock)
        {
            if (!_entries.TryGetValue(path, out var entry))
                entry = new ModFingerprintCacheEntry();

            entry.Length = info.Exists ? info.Length : 0;
            entry.LastWriteUtcTicks = info.Exists ? info.LastWriteTimeUtc.Ticks : 0;
            entry.Sha512 = sha512;
            entry.CurseForgeFingerprint = curseForgeFingerprint;
            if (match is not null)
                entry.Match = match;

            _entries[path] = entry;
            _dirty = true;
        }

        if (match is not null)
        {
            _modrinthBySha[sha512] = (match.Mod, match.Version);
            _curseForgeByFingerprint[curseForgeFingerprint] = (match.Mod, match.Version);
        }
    }

    public void StoreMatch(string path, ModSummary mod, ModVersion version)
    {
        path = NormalizePath(path);
        var match = new CachedModMatch { Mod = mod, Version = version };
        ModFingerprintCacheEntry? entry;

        lock (_lock)
        {
            if (!_entries.TryGetValue(path, out entry))
                return;
            entry.Match = match;
            _dirty = true;
        }

        if (!string.IsNullOrEmpty(entry.Sha512))
            _modrinthBySha[entry.Sha512] = (mod, version);
        if (entry.CurseForgeFingerprint != 0)
            _curseForgeByFingerprint[entry.CurseForgeFingerprint] = (mod, version);
    }

    /// <summary>Records the identity a jar resolved to on each store. A store that came back empty
    /// leaves what was known before: a failed lookup is not evidence the jar is missing there.</summary>
    public void StoreStoreMatches(string path,
        (ModSummary Mod, ModVersion Version)? modrinth,
        (ModSummary Mod, ModVersion Version)? curseForge)
    {
        if (modrinth is null && curseForge is null) return;
        path = NormalizePath(path);
        ModFingerprintCacheEntry? entry;
        lock (_lock)
        {
            if (!_entries.TryGetValue(path, out entry)) return;
            if (modrinth is { } m && m.Mod.Source == ModSource.Modrinth)
                entry.ModrinthMatch = new CachedModMatch { Mod = m.Mod, Version = m.Version };
            if (curseForge is { } c && c.Mod.Source == ModSource.CurseForge)
                entry.CurseForgeMatch = new CachedModMatch { Mod = c.Mod, Version = c.Version };
            _dirty = true;
        }
        IndexStoreMatches(entry);
    }

    /// <summary>Both stores' cached identities for a jar (either may be null). False when the jar has
    /// no valid cache entry or no identity on either store. Older entries that only carry the single
    /// <see cref="ModFingerprintCacheEntry.Match"/> contribute it to the store it came from.</summary>
    public bool TryGetCachedMatches(string path,
        out (ModSummary Mod, ModVersion Version)? modrinth,
        out (ModSummary Mod, ModVersion Version)? curseForge)
    {
        modrinth = null;
        curseForge = null;
        if (!TryGet(path, out var entry)) return false;

        var mr = entry.ModrinthMatch ?? (entry.Match?.Mod.Source == ModSource.Modrinth ? entry.Match : null);
        var cf = entry.CurseForgeMatch ?? (entry.Match?.Mod.Source == ModSource.CurseForge ? entry.Match : null);
        if (mr is not null) modrinth = (mr.Mod, mr.Version);
        if (cf is not null) curseForge = (cf.Mod, cf.Version);
        return mr is not null || cf is not null;
    }

    /// <summary>Makes the per-store identities answer hash lookups, so a jar known on both stores
    /// resolves from cache on both instead of re-asking the store it wasn't primarily cached under.</summary>
    private void IndexStoreMatches(ModFingerprintCacheEntry entry)
    {
        if (entry.ModrinthMatch is { } mr && !string.IsNullOrEmpty(entry.Sha512))
            _modrinthBySha[entry.Sha512] = (mr.Mod, mr.Version);
        if (entry.CurseForgeMatch is { } cf && entry.CurseForgeFingerprint != 0)
            _curseForgeByFingerprint[entry.CurseForgeFingerprint] = (cf.Mod, cf.Version);
    }

    public bool TryGetCachedMatch(string path, out ModSummary mod, out ModVersion version)
    {
        mod = null!;
        version = null!;
        if (!TryGet(path, out var entry) || entry.Match is null)
            return false;

        mod = entry.Match.Mod;
        version = entry.Match.Version;
        return true;
    }

    public Dictionary<string, (ModSummary mod, ModVersion version)> ResolveModrinthMatches(IEnumerable<string> sha512Hashes)
    {
        var result = new Dictionary<string, (ModSummary, ModVersion)>(StringComparer.OrdinalIgnoreCase);
        foreach (var hash in sha512Hashes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_modrinthBySha.TryGetValue(hash, out var match))
                result[hash] = match;
        }
        return result;
    }

    public Dictionary<long, (ModSummary mod, ModVersion version)> ResolveCurseForgeMatches(IEnumerable<long> fingerprints)
    {
        var result = new Dictionary<long, (ModSummary, ModVersion)>();
        foreach (var fingerprint in fingerprints.Distinct())
        {
            if (_curseForgeByFingerprint.TryGetValue(fingerprint, out var match))
                result[fingerprint] = match;
        }
        return result;
    }

    public void RememberModrinthMatch(string sha512, ModSummary mod, ModVersion version) =>
        _modrinthBySha[sha512.ToLowerInvariant()] = (mod, version);

    public void RememberCurseForgeMatch(long fingerprint, ModSummary mod, ModVersion version) =>
        _curseForgeByFingerprint[fingerprint] = (mod, version);

    /// <summary>Asks for the cache to be written. The write is debounced onto a thread-pool thread:
    /// serialising the whole cache can take over 100 ms, too long for the dispatcher. Process exit is the
    /// backstop.</summary>
    public void Flush()
    {
        if (!_dirty) return;
        try
        {
            lock (_lock)
            {
                _flushTimer ??= new Timer(_ => WriteNow(), null, Timeout.Infinite, Timeout.Infinite);
                _flushTimer.Change(FlushDelayMs, Timeout.Infinite);
            }
        }
        catch { /* the process-exit write is the backstop */ }
    }

    /// <summary>Serialises and writes now. Only the debounce timer and process exit call it.</summary>
    private void WriteNow()
    {
        ModFingerprintCacheFile snapshot;
        lock (_lock)
        {
            if (!_dirty) return;
            snapshot = new ModFingerprintCacheFile
            {
                Entries = new Dictionary<string, ModFingerprintCacheEntry>(_entries, StringComparer.OrdinalIgnoreCase)
            };
            _dirty = false;
        }

        try
        {
            // The timer and the exit hook can both arrive at once, and they share one .tmp path.
            lock (_writeGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
                var tmp = _cachePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, JsonOpts));
                File.Move(tmp, _cachePath, overwrite: true);
            }
        }
        catch
        {
            // Not worth failing an install over, but worth retrying: mark it dirty again for the next
            // debounce or for process exit.
            lock (_lock) _dirty = true;
        }
    }

    public void Remove(string path)
    {
        path = NormalizePath(path);
        lock (_lock)
        {
            if (!_entries.Remove(path)) return;
            _dirty = true;
        }
    }

    /// <summary>Reads the file once and computes both provider hashes.</summary>
    public static (string sha512, long curseForgeFingerprint) ComputeHashes(string path)
    {
        using var input = File.OpenRead(path);
        using var normalized = new MemoryStream();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            sha.AppendData(buffer.AsSpan(0, read));
            for (var i = 0; i < read; i++)
            {
                var b = buffer[i];
                if (b is 9 or 10 or 13 or 32) continue;
                normalized.WriteByte(b);
            }
        }

        var sha512 = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        var curseForgeFingerprint = CurseForgeService.MurmurHash2Normalized(normalized);
        return (sha512, curseForgeFingerprint);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;
            var file = JsonSerializer.Deserialize<ModFingerprintCacheFile>(File.ReadAllText(_cachePath), JsonOpts);
            if (file?.Entries is null) return;

            lock (_lock)
            {
                _entries.Clear();
                foreach (var (path, entry) in file.Entries)
                {
                    _entries[NormalizePath(path)] = entry;
                    if (!string.IsNullOrEmpty(entry.Sha512) && entry.Match is not null)
                        _modrinthBySha.TryAdd(entry.Sha512, (entry.Match.Mod, entry.Match.Version));
                    if (entry.CurseForgeFingerprint != 0 && entry.Match is not null)
                        _curseForgeByFingerprint.TryAdd(entry.CurseForgeFingerprint, (entry.Match.Mod, entry.Match.Version));
                    IndexStoreMatches(entry);
                }
            }
        }
        catch { /* corrupt cache: start fresh */ }
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path);

    private static string GetDataRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CloudLauncher",
        Environment.GetEnvironmentVariable("CL_PROFILE") is { Length: > 0 } profile ? profile : "default");
}

public sealed class ModFingerprintCacheEntry
{
    public long Length { get; set; }
    public long LastWriteUtcTicks { get; set; }
    public string Sha512 { get; set; } = "";
    public long CurseForgeFingerprint { get; set; }
    public CachedModMatch? Match { get; set; }

    /// <summary>The jar's Modrinth identity, when known (a jar published on both stores has both).</summary>
    public CachedModMatch? ModrinthMatch { get; set; }

    /// <summary>The jar's CurseForge identity, when known.</summary>
    public CachedModMatch? CurseForgeMatch { get; set; }
}

public sealed class CachedModMatch
{
    public ModSummary Mod { get; set; } = null!;
    public ModVersion Version { get; set; } = null!;
}

internal sealed class ModFingerprintCacheFile
{
    public Dictionary<string, ModFingerprintCacheEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
