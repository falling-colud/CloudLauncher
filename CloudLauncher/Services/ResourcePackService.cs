using System.IO;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>
/// Scans pack <c>game/resourcepacks/</c> folders for installed resource pack zip files.
/// </summary>
public sealed class ResourcePackService(AppSettings settings, PackFolderService packs)
{
    public static string Key(Guid sourcePackId, string fileName) => $"{sourcePackId:N}:{fileName}";

    public List<ResourcePackInfo> ScanAll(IReadOnlyList<PackSummary> knownPacks)
    {
        var result = new List<ResourcePackInfo>();
        foreach (var p in knownPacks)
        {
            try { result.AddRange(ScanPack(p)); }
            catch { /* skip missing folders */ }
        }
        return result.OrderByDescending(r => r.LastModified).ToList();
    }

    public List<ResourcePackInfo> ScanPack(PackSummary pack) => ScanPack(pack.Id, pack.Name);

    public List<ResourcePackInfo> ScanPack(Guid packId, string packName)
    {
        var dir = Path.Combine(packs.GameDir(packId, packName), "resourcepacks");
        if (!Directory.Exists(dir)) return new();

        var result = new List<ResourcePackInfo>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.zip"))
        {
            var fileName = Path.GetFileName(file);
            var key = Key(packId, fileName);
            var entry = settings.ResourcePacks.TryGetValue(key, out var e) ? e : null;
            var displayName = !string.IsNullOrEmpty(entry?.DisplayName)
                ? entry.DisplayName
                : Path.GetFileNameWithoutExtension(fileName);
            var info = new FileInfo(file);
            var compatibleWithAll = entry?.CompatibleWithAll ?? false;
            var compat = entry?.CompatiblePackIds.ToList() ?? new();
            if (!compat.Contains(packId)) compat.Insert(0, packId);

            result.Add(new ResourcePackInfo(
                Key: key,
                SourcePackId: packId,
                SourcePackName: packName,
                FileName: fileName,
                FilePath: file,
                DisplayName: displayName,
                LastModified: info.LastWriteTime,
                SizeBytes: info.Length,
                CompatibleWithAll: compatibleWithAll,
                CompatiblePackIds: compat));
        }
        return result;
    }

    public ResourcePackEntry GetOrCreate(string key)
    {
        if (!settings.ResourcePacks.TryGetValue(key, out var e))
        {
            e = new ResourcePackEntry();
            settings.ResourcePacks[key] = e;
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

    public void LinkHostedResourcePack(string key, Guid hostedId)
    {
        var e = GetOrCreate(key);
        e.HostedResourcePackId = hostedId;
        settings.Save();
    }

    public void SetSharingEnabled(string key, bool enabled)
    {
        var e = GetOrCreate(key);
        e.SharingEnabled = enabled;
        settings.Save();
    }

    public bool IsCompatible(ResourcePackInfo pack, Guid packId) =>
        pack.SourcePackId == packId || pack.CompatibleWithAll || pack.CompatiblePackIds.Contains(packId);

    public List<PackSummary> CompatiblePacks(ResourcePackInfo pack, IReadOnlyList<PackSummary> allPacks) =>
        allPacks.Where(p => IsCompatible(pack, p.Id)).ToList();
}

public sealed record ResourcePackInfo(
    string Key,
    Guid SourcePackId,
    string SourcePackName,
    string FileName,
    string FilePath,
    string DisplayName,
    DateTimeOffset LastModified,
    long SizeBytes,
    bool CompatibleWithAll,
    IReadOnlyList<Guid> CompatiblePackIds);
