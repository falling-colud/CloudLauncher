using System.IO;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>
/// Scans pack <c>game/saves/</c> folders for Minecraft worlds and tracks which packs
/// may run each world. Storage is local — worlds don't sync to the server.
/// </summary>
public sealed class WorldService(AppSettings settings, PackFolderService packs)
{
    public static string Key(Guid sourcePackId, string folderName) => $"{sourcePackId:N}:{folderName}";

    /// <summary>List all worlds across the supplied packs.</summary>
    public List<WorldInfo> ScanAll(IReadOnlyList<PackSummary> knownPacks)
    {
        var result = new List<WorldInfo>();
        foreach (var p in knownPacks)
        {
            try { result.AddRange(ScanPack(p)); }
            catch { /* missing folder etc — skip */ }
        }
        return result.OrderByDescending(w => w.LastModified).ToList();
    }

    /// <summary>List worlds for a single pack.</summary>
    public List<WorldInfo> ScanPack(PackSummary pack) => ScanPack(pack.Id, pack.Name);

    public List<WorldInfo> ScanPack(Guid packId, string packName)
    {
        var savesDir = Path.Combine(packs.GameDir(packId, packName), "saves");
        if (!Directory.Exists(savesDir)) return new();

        var result = new List<WorldInfo>();
        foreach (var dir in Directory.EnumerateDirectories(savesDir))
        {
            var folder = Path.GetFileName(dir);
            var key = Key(packId, folder);
            var entry = settings.Worlds.TryGetValue(key, out var e) ? e : null;
            var displayName = !string.IsNullOrEmpty(entry?.DisplayName)
                ? entry.DisplayName
                : TryReadLevelName(dir) ?? folder;
            var size = DirectorySize(dir);
            var lastMod = SafeLastWriteTime(dir);
            var compatibleWithAll = entry?.CompatibleWithAll ?? false;
            var compat = entry?.CompatiblePackIds.ToList() ?? new();
            if (!compat.Contains(packId)) compat.Insert(0, packId); // source pack always compatible

            result.Add(new WorldInfo(
                Key: key,
                SourcePackId: packId,
                SourcePackName: packName,
                FolderName: folder,
                FolderPath: dir,
                DisplayName: displayName,
                LastModified: lastMod,
                SizeBytes: size,
                CompatibleWithAll: compatibleWithAll,
                CompatiblePackIds: compat));
        }
        return result;
    }

    public WorldEntry GetOrCreate(string key)
    {
        if (!settings.Worlds.TryGetValue(key, out var e))
        {
            e = new WorldEntry();
            settings.Worlds[key] = e;
        }
        return e;
    }

    public void Save() => settings.Save();

    public void SetCompatibleWithAll(string key, bool all)
    {
        var e = GetOrCreate(key);
        e.CompatibleWithAll = all;
        settings.Save();
    }

    public void SetCompatible(string key, Guid packId, bool allowed)
    {
        var e = GetOrCreate(key);
        if (allowed)
        {
            if (!e.CompatiblePackIds.Contains(packId)) e.CompatiblePackIds.Add(packId);
        }
        else e.CompatiblePackIds.Remove(packId);
        settings.Save();
    }

    public void Rename(string key, string newName)
    {
        var e = GetOrCreate(key);
        e.DisplayName = newName;
        settings.Save();
    }

    public void UpdateOverview(string key, string? summary, string? description, PackVisibility visibility)
    {
        var e = GetOrCreate(key);
        e.Summary = string.IsNullOrWhiteSpace(summary) ? null : summary.Trim();
        e.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        e.Visibility = visibility;
        settings.Save();
    }

    public void LinkSharedWorld(string key, Guid sharedWorldId)
    {
        var e = GetOrCreate(key);
        e.SharedWorldId = sharedWorldId;
        settings.Save();
    }

    public void SetSharingEnabled(string key, bool enabled)
    {
        var e = GetOrCreate(key);
        e.SharingEnabled = enabled;
        settings.Save();
    }

    /// <summary>True if the world can be launched with the given pack.</summary>
    public bool IsCompatible(WorldInfo world, Guid packId) =>
        world.SourcePackId == packId || world.CompatibleWithAll || world.CompatiblePackIds.Contains(packId);

    public List<PackSummary> CompatiblePacks(WorldInfo world, IReadOnlyList<PackSummary> allPacks) =>
        allPacks.Where(p => IsCompatible(world, p.Id)).ToList();

    // ── helpers ─────────────────────────────────────────────────────────────

    private static DateTimeOffset SafeLastWriteTime(string dir)
    {
        try { return new FileInfo(Path.Combine(dir, "level.dat")).LastWriteTime; }
        catch { try { return new DirectoryInfo(dir).LastWriteTime; } catch { return DateTimeOffset.MinValue; } }
    }

    private static long DirectorySize(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
        }
        catch { return 0; }
    }

    /// <summary>Best-effort: read the world name from level.dat. Returns null if unparseable.</summary>
    private static string? TryReadLevelName(string worldDir)
    {
        // NBT parsing is non-trivial; we just search the raw bytes of level.dat for the
        // "LevelName" UTF-8 string and grab the following short-prefixed name.
        // Good enough for display; fall back to folder name on failure.
        try
        {
            var levelDat = Path.Combine(worldDir, "level.dat");
            if (!File.Exists(levelDat)) return null;
            // level.dat is gzip-compressed NBT
            using var fs = File.OpenRead(levelDat);
            using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress);
            using var ms = new MemoryStream();
            gz.CopyTo(ms);
            var bytes = ms.ToArray();
            var marker = System.Text.Encoding.UTF8.GetBytes("LevelName");
            var idx = IndexOfBytes(bytes, marker);
            if (idx < 0) return null;
            // After the name "LevelName" comes a 2-byte big-endian length, then the UTF-8 string.
            var lenOffset = idx + marker.Length;
            if (lenOffset + 2 >= bytes.Length) return null;
            var len = (bytes[lenOffset] << 8) | bytes[lenOffset + 1];
            if (len <= 0 || len > 200 || lenOffset + 2 + len > bytes.Length) return null;
            return System.Text.Encoding.UTF8.GetString(bytes, lenOffset + 2, len);
        }
        catch { return null; }
    }

    private static int IndexOfBytes(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }
}

public sealed record WorldInfo(
    string Key,
    Guid SourcePackId,
    string SourcePackName,
    string FolderName,
    string FolderPath,
    string DisplayName,
    DateTimeOffset LastModified,
    long SizeBytes,
    bool CompatibleWithAll,
    IReadOnlyList<Guid> CompatiblePackIds);
