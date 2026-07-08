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
    private bool _dirty;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ModFingerprintCache()
    {
        _cachePath = Path.Combine(GetDataRoot(), "mod-fingerprints.json");
        Load();
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

    public void Flush()
    {
        if (!_dirty) return;

        ModFingerprintCacheFile snapshot;
        lock (_lock)
        {
            snapshot = new ModFingerprintCacheFile
            {
                Entries = new Dictionary<string, ModFingerprintCacheEntry>(_entries, StringComparer.OrdinalIgnoreCase)
            };
            _dirty = false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(snapshot, JsonOpts));
        }
        catch { /* best-effort */ }
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
                }
            }
        }
        catch { /* corrupt cache — start fresh */ }
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
