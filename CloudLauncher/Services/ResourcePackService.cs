using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows.Media.Imaging;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>Which of an instance's two resourcepacks/ folders a pack was found in.</summary>
/// <remarks>Both are merged into the game directory at launch, but only the <c>game/</c> copy is
/// synced to the instance's other members; <c>local/</c> is unsynced.</remarks>
public enum ResourcePackOrigin
{
    Game = 0,
    Local
}

/// <summary>Scans each instance's <c>resourcepacks/</c> folders, and owns which packs are turned on
/// and in what order.</summary>
/// <remarks>A pack only loads if it is also listed in <c>options.txt</c>, in priority order (see
/// <see cref="ActiveFor"/> and <see cref="SetActive"/>). Packs can be zips or unpacked folders, so
/// every delete, copy and rename has to handle directories too.</remarks>
public sealed class ResourcePackService(AppSettings settings, PackFolderService packs)
{
    private const string FolderName = "resourcepacks";

    /// <summary>Settings key for a pack in the instance's shared <c>game/</c> folder.</summary>
    public static string Key(Guid sourcePackId, string fileName) => $"{sourcePackId:N}:{fileName}";

    /// <summary>Settings key for a pack, qualified by which folder it lives in.</summary>
    /// <remarks>The <c>game/</c> form is unchanged so keys saved by older versions still resolve. The
    /// <c>local/</c> form is namespaced so the same file name in both folders is two packs.</remarks>
    public static string Key(Guid sourcePackId, string fileName, ResourcePackOrigin origin) =>
        origin == ResourcePackOrigin.Local ? $"{sourcePackId:N}:local/{fileName}" : Key(sourcePackId, fileName);

    public List<ResourcePackInfo> ScanAll(IReadOnlyList<PackSummary> knownPacks)
    {
        var result = new List<ResourcePackInfo>();
        foreach (var p in knownPacks)
        {
            try { result.AddRange(ScanPack(p)); }
            catch { /* an instance that has never been launched has no folder yet */ }
        }
        return result.OrderByDescending(r => r.LastModified).ToList();
    }

    public List<ResourcePackInfo> ScanPack(PackSummary pack) => ScanPack(pack.Id, pack.Name, pack.IsShared);

    public List<ResourcePackInfo> ScanPack(Guid packId, string packName) => ScanPack(packId, packName, includeLocal: true);

    /// <summary>Every resource pack installed in an instance, with its enabled state and stack
    /// position.</summary>
    /// <param name="includeLocal">Also scan the unsynced <c>local/resourcepacks/</c> folder. Cheap; it
    /// doesn't exist for a non-shared instance.</param>
    public List<ResourcePackInfo> ScanPack(Guid packId, string packName, bool includeLocal)
    {
        var gameDir = packs.GameDir(packId, packName);

        // Read the stack once per instance, not once per pack.
        var active = ActiveFor(packId, packName);

        var result = new List<ResourcePackInfo>();
        Collect(Path.Combine(gameDir, FolderName), ResourcePackOrigin.Game);
        if (includeLocal)
        {
            try { Collect(Path.Combine(packs.LocalDir(packId), FolderName), ResourcePackOrigin.Local); }
            catch { /* no local folder for this instance */ }
        }
        return result;

        void Collect(string dir, ResourcePackOrigin origin)
        {
            if (!Directory.Exists(dir)) return;

            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                var fileName = Path.GetFileName(entry);
                var isFolder = Directory.Exists(entry);

                // Zips and unpacked pack folders only (Minecraft loads both); skip anything else.
                if (!isFolder && !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                if (fileName.StartsWith('.')) continue;

                var key = Key(packId, fileName, origin);
                var settingsEntry = settings.ResourcePacks.TryGetValue(key, out var e) ? e : null;
                var displayName = !string.IsNullOrWhiteSpace(settingsEntry?.DisplayName)
                    ? settingsEntry!.DisplayName
                    : Path.GetFileNameWithoutExtension(fileName);

                long size;
                DateTimeOffset modified;
                try
                {
                    modified = isFolder ? Directory.GetLastWriteTime(entry) : File.GetLastWriteTime(entry);
                    size = isFolder ? FolderSize(entry) : new FileInfo(entry).Length;
                }
                catch { modified = DateTimeOffset.MinValue; size = 0; }

                var compatibleWithAll = settingsEntry?.CompatibleWithAll ?? false;
                var compat = settingsEntry?.CompatiblePackIds.ToList() ?? new();
                if (!compat.Contains(packId)) compat.Insert(0, packId);

                // The stack lists file names, so same-named packs in game/ and local/ both read as on.
                var stackIndex = active.FindIndex(n => string.Equals(n, fileName, StringComparison.OrdinalIgnoreCase));

                result.Add(new ResourcePackInfo(
                    Key: key,
                    SourcePackId: packId,
                    SourcePackName: packName,
                    FileName: fileName,
                    FilePath: entry,
                    DisplayName: displayName,
                    LastModified: modified,
                    SizeBytes: size,
                    CompatibleWithAll: compatibleWithAll,
                    CompatiblePackIds: compat,
                    Origin: origin,
                    IsFolder: isFolder,
                    StackIndex: stackIndex));
            }
        }
    }

    private static long FolderSize(string dir)
    {
        try
        {
            return new DirectoryInfo(dir)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch { return 0; }
    }

    // ── the enabled stack ────────────────────────────────────────────────────

    /// <summary>File names of the packs this instance has turned on, highest priority first.</summary>
    /// <remarks>Reversed from options.txt (where the last entry wins) to match the UI and the in-game
    /// screen. "vanilla" is left out: it is the base, not a pack you can order.</remarks>
    public List<string> ActiveFor(Guid packId, string? packName = null)
    {
        var dir = packName is null ? packs.GameDir(packId) : packs.GameDir(packId, packName);
        var names = OptionsTxtService.ReadResourcePacks(dir)
            .Where(e => !string.Equals(e, OptionsTxtService.VanillaEntry, StringComparison.Ordinal))
            .Select(OptionsTxtService.EntryFileName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();
        names.Reverse();
        return names;
    }

    /// <summary>Replaces the enabled stack. <paramref name="fileNamesHighestFirst"/> is in UI order;
    /// the file gets the reversed list plus "vanilla" at the bottom.</summary>
    /// <param name="minecraftVersion">The instance's Minecraft version, if known. Only needed when
    /// options.txt has no named pack to copy the spelling from, so a pre-1.13 instance isn't given a
    /// <c>file/</c> prefix it doesn't understand. Can be omitted when reordering or removing.</param>
    /// <remarks>Not every entry is a file. Loaders and mods put their own packs in the stack
    /// (<c>fabric</c>, <c>mod_resources</c>, a global-pack mod's ids), and <see cref="ActiveFor"/>
    /// hands those back unchanged. Each name is therefore written the way the file already spelled
    /// it; only a name the file has never seen gets the prefix. Prefixing everything turned
    /// <c>fabric</c> into <c>file/fabric</c>, a pack that doesn't exist, which the game then
    /// silently dropped along with Fabric's own resources.</remarks>
    public void SetActive(Guid packId, string? packName, IReadOnlyList<string> fileNamesHighestFirst,
        string? minecraftVersion = null)
    {
        var dir = packName is null ? packs.GameDir(packId) : packs.GameDir(packId, packName);
        var prefix = OptionsTxtService.UsesFilePrefix(dir, minecraftVersion) ? "file/" : "";

        var spelled = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in OptionsTxtService.ReadResourcePacks(dir))
            spelled.TryAdd(OptionsTxtService.EntryFileName(raw), raw);

        var ordered = fileNamesHighestFirst
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Reverse()
            .Select(n => spelled.TryGetValue(n, out var raw) ? raw : prefix + n)
            .ToList();

        OptionsTxtService.WriteResourcePacks(dir, ordered);
    }

    /// <summary>Turns a pack on or off, without disturbing the order of the others.</summary>
    /// <remarks>A newly enabled pack goes on top of the stack, as the game does when you select
    /// one.</remarks>
    /// <param name="minecraftVersion">Passed on to <see cref="SetActive"/>. Matters most here, since
    /// this usually writes an instance's first entry.</param>
    public void SetEnabled(Guid packId, string? packName, string fileName, bool enabled,
        string? minecraftVersion = null)
    {
        var stack = ActiveFor(packId, packName);
        stack.RemoveAll(n => string.Equals(n, fileName, StringComparison.OrdinalIgnoreCase));
        if (enabled) stack.Insert(0, fileName);
        SetActive(packId, packName, stack, minecraftVersion);
    }

    /// <summary>Moves an enabled pack <paramref name="delta"/> places up (negative) or down (positive)
    /// the stack, clamped at both ends. Ignored for a pack that isn't enabled.</summary>
    public void MoveInStack(Guid packId, string? packName, string fileName, int delta)
    {
        var stack = ActiveFor(packId, packName);
        var index = stack.FindIndex(n => string.Equals(n, fileName, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;

        var target = Math.Clamp(index + delta, 0, stack.Count - 1);
        if (target == index) return;

        var moved = stack[index];
        stack.RemoveAt(index);
        stack.Insert(target, moved);
        SetActive(packId, packName, stack);
    }

    /// <summary>Renames a pack inside the stack, so renaming the file on disk doesn't turn it off.
    /// No-op when the pack wasn't enabled.</summary>
    public void RenameInStack(Guid packId, string? packName, string oldFileName, string newFileName)
    {
        var stack = ActiveFor(packId, packName);
        var index = stack.FindIndex(n => string.Equals(n, oldFileName, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        stack[index] = newFileName;
        SetActive(packId, packName, stack);
    }

    /// <summary>Drops a pack from the stack. Called after the file is deleted, so the game is never
    /// asked to load something that is no longer there.</summary>
    public void RemoveFromStack(Guid packId, string? packName, string fileName)
    {
        var stack = ActiveFor(packId, packName);
        if (stack.RemoveAll(n => string.Equals(n, fileName, StringComparison.OrdinalIgnoreCase)) == 0) return;
        SetActive(packId, packName, stack);
    }

    // ── pack.png / pack.mcmeta ───────────────────────────────────────────────

    private static readonly Dictionary<string, (DateTime Stamp, long Size, ResourcePackMeta Meta)> MetaCache = new();
    private static readonly object MetaGate = new();

    /// <summary>The pack's own icon, description and pack_format, read from <c>pack.png</c> and
    /// <c>pack.mcmeta</c>.</summary>
    /// <remarks>Cached by path, last-write time and size, since lists ask for this on every filter
    /// keystroke. Returns <see cref="ResourcePackMeta.Empty"/> rather than null so a malformed zip is
    /// cached instead of reopened every time. Never throws.</remarks>
    public static ResourcePackMeta ReadMeta(string path, bool isFolder)
    {
        DateTime stamp;
        long size;
        try
        {
            stamp = isFolder ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path);
            size = isFolder ? 0 : new FileInfo(path).Length;
        }
        catch { return ResourcePackMeta.Empty; }

        lock (MetaGate)
        {
            if (MetaCache.TryGetValue(path, out var hit) && hit.Stamp == stamp && hit.Size == size)
                return hit.Meta;
        }

        var meta = isFolder ? ReadFolderMeta(path) : ReadZipMeta(path);

        lock (MetaGate)
        {
            // Bounded so a long session browsing hundreds of packs cannot pin every icon in memory.
            if (MetaCache.Count > 400) MetaCache.Clear();
            MetaCache[path] = (stamp, size, meta);
        }
        return meta;
    }

    /// <summary>Largest pack.png or pack.mcmeta that is read. Packs come from other people, and both
    /// files are read whole into memory.</summary>
    private const long MaxMetaFileBytes = 8L * 1024 * 1024;

    private static ResourcePackMeta ReadZipMeta(string zipPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);

            var png = archive.GetEntry("pack.png");
            var icon = png is not null && png.Length <= MaxMetaFileBytes ? LoadIcon(() => png.Open()) : null;
            var mcmeta = archive.GetEntry("pack.mcmeta");
            string? json = null;
            if (mcmeta is not null && mcmeta.Length <= MaxMetaFileBytes)
            {
                using var buffer = new MemoryStream();
                using (var entry = mcmeta.Open())
                    SafeZip.CopyCapped(entry, buffer, MaxMetaFileBytes);
                buffer.Position = 0;
                using var reader = new StreamReader(buffer);
                json = reader.ReadToEnd();
            }
            return BuildMeta(icon, json);
        }
        catch { return ResourcePackMeta.Empty; }
    }

    private static ResourcePackMeta ReadFolderMeta(string dir)
    {
        try
        {
            var pngPath = Path.Combine(dir, "pack.png");
            var icon = File.Exists(pngPath) ? LoadIcon(() => File.OpenRead(pngPath)) : null;

            var metaPath = Path.Combine(dir, "pack.mcmeta");
            var json = File.Exists(metaPath) && new FileInfo(metaPath).Length <= MaxMetaFileBytes
                ? File.ReadAllText(metaPath)
                : null;
            return BuildMeta(icon, json);
        }
        catch { return ResourcePackMeta.Empty; }
    }

    /// <summary>Decodes pack.png into a frozen bitmap.</summary>
    /// <remarks>Copied into memory and decoded with <c>CacheOption.OnLoad</c> because a zip entry
    /// stream doesn't survive WPF's lazy decode. Frozen so the scan can run off the UI thread. Capped
    /// at 128px, which is all a cover needs.</remarks>
    private static BitmapImage? LoadIcon(Func<Stream> open)
    {
        try
        {
            using var source = open();
            using var buffer = new MemoryStream();
            SafeZip.CopyCapped(source, buffer, MaxMetaFileBytes);
            if (buffer.Length == 0) return null;

            // Packs come from other people: SafeImage refuses absurd dimensions and bounds the
            // decode on the longer side, not just the width.
            return SafeImage.Decode(buffer.ToArray(), maxSide: 128, BitmapCreateOptions.PreservePixelFormat, scaleUp: true);
        }
        catch { return null; }
    }

    private static ResourcePackMeta BuildMeta(BitmapImage? icon, string? mcmetaJson)
    {
        if (string.IsNullOrWhiteSpace(mcmetaJson))
            return new ResourcePackMeta(icon, null, null);

        try
        {
            using var doc = JsonDocument.Parse(mcmetaJson);
            if (!doc.RootElement.TryGetProperty("pack", out var pack))
                return new ResourcePackMeta(icon, null, null);

            int? format = pack.TryGetProperty("pack_format", out var pf) && pf.TryGetInt32(out var fv) ? fv : null;
            string? description = pack.TryGetProperty("description", out var desc) ? FlattenText(desc) : null;
            return new ResourcePackMeta(icon, string.IsNullOrWhiteSpace(description) ? null : description.Trim(), format);
        }
        catch { return new ResourcePackMeta(icon, null, null); }
    }

    /// <summary>Flattens a pack description into plain text.</summary>
    /// <remarks>The field is a Minecraft text component: a string, an object with <c>text</c> and
    /// <c>extra</c>, or an array of those. Only the words are kept.</remarks>
    private static string FlattenText(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString() ?? "";
            case JsonValueKind.Array:
                return string.Concat(element.EnumerateArray().Select(FlattenText));
            case JsonValueKind.Object:
            {
                var text = element.TryGetProperty("text", out var t) ? FlattenText(t) : "";
                if (element.TryGetProperty("extra", out var extra))
                    text += FlattenText(extra);
                return text;
            }
            default:
                return "";
        }
    }

    // ── settings entries ─────────────────────────────────────────────────────

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

    /// <summary>Moves everything recorded about a pack from one settings key to another, including
    /// its folder memberships.</summary>
    /// <remarks>The key contains the file name, so renaming the file changes it. Without this the pack
    /// would lose its display name, hosting link and folders.</remarks>
    public void ReKey(string oldKey, string newKey)
    {
        if (string.Equals(oldKey, newKey, StringComparison.Ordinal)) return;

        if (settings.ResourcePacks.Remove(oldKey, out var entry))
            settings.ResourcePacks[newKey] = entry;

        foreach (var list in settings.ResourcePackFolders.Values)
        {
            var at = list.IndexOf(oldKey);
            if (at < 0) continue;
            if (list.Contains(newKey)) list.RemoveAt(at);
            else list[at] = newKey;
        }
        settings.Save();
    }

    /// <summary>Forgets a pack entirely: its entry and its membership of every folder. The caller
    /// saves, because this usually runs alongside other settings changes.</summary>
    public void Forget(string key)
    {
        settings.ResourcePacks.Remove(key);
        foreach (var list in settings.ResourcePackFolders.Values) list.Remove(key);
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

    /// <summary>Records which store listing an installed pack came from.</summary>
    /// <remarks>Written at install time, since nothing in the zip identifies it later. A pack without
    /// provenance is never matched by name, which could offer another author's same-named pack as an
    /// update. A null <paramref name="source"/> clears it (for a file replaced by hand).</remarks>
    public void SetProvenance(string key, ModSource? source, string? projectId, string? versionId, string? versionNumber)
    {
        var e = GetOrCreate(key);
        e.Source = source;
        e.ProjectId = string.IsNullOrWhiteSpace(projectId) ? null : projectId.Trim();
        e.VersionId = string.IsNullOrWhiteSpace(versionId) ? null : versionId.Trim();
        e.VersionNumber = string.IsNullOrWhiteSpace(versionNumber) ? null : versionNumber.Trim();
        settings.Save();
    }

    public bool IsCompatible(ResourcePackInfo pack, Guid packId) =>
        pack.SourcePackId == packId || pack.CompatibleWithAll || pack.CompatiblePackIds.Contains(packId);

    public List<PackSummary> CompatiblePacks(ResourcePackInfo pack, IReadOnlyList<PackSummary> allPacks) =>
        allPacks.Where(p => IsCompatible(pack, p.Id)).ToList();

    // ── file operations ──────────────────────────────────────────────────────

    /// <summary>Deletes a pack from disk (recursively for a folder) and forgets everything the launcher
    /// knew about it, including its place in the stack.</summary>
    /// <remarks>Blocking; callers run it in <c>Task.Run</c> because a large unpacked pack is thousands
    /// of file operations. Doesn't save settings; the caller saves once after a batch.</remarks>
    /// <exception cref="IOException">The path is not an entry of a resourcepacks folder;
    /// nothing was deleted.</exception>
    public void DeleteFromDisk(ResourcePackInfo pack)
    {
        var path = EntryInPackFolder(pack)
                   ?? throw new IOException("This pack is not inside a resourcepacks folder.");
        if (pack.IsFolder)
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }

        RemoveFromStack(pack.SourcePackId, pack.SourcePackName, pack.FileName);
        Forget(pack.Key);
    }

    /// <summary>Renames the file or folder on disk, carrying the settings entry and the options.txt
    /// stack entry with it.</summary>
    /// <returns>The new settings key.</returns>
    /// <exception cref="IOException">A pack with that name already exists.</exception>
    public string RenameFileOnDisk(ResourcePackInfo pack, string newFileName)
    {
        var clean = SanitizeFileName(newFileName, pack.IsFolder);
        if (string.Equals(clean, pack.FileName, StringComparison.Ordinal)) return pack.Key;

        var source = EntryInPackFolder(pack)
                     ?? throw new IOException("This pack is not inside a resourcepacks folder.");
        var dir = Path.GetDirectoryName(source)!;
        var target = PathSafety.ResolveFileName(dir, clean)
                     ?? throw new IOException($"'{clean}' cannot be used as a file name.");

        if (File.Exists(target) || Directory.Exists(target))
            throw new IOException($"'{clean}' already exists in that folder.");

        if (pack.IsFolder) Directory.Move(source, target);
        else File.Move(source, target);

        RenameInStack(pack.SourcePackId, pack.SourcePackName, pack.FileName, clean);

        var newKey = Key(pack.SourcePackId, clean, pack.Origin);
        ReKey(pack.Key, newKey);
        return newKey;
    }

    /// <summary>Strips characters Windows won't accept and keeps the .zip suffix on a zip. Always
    /// returns a single plain name.</summary>
    public static string SanitizeFileName(string name, bool isFolder)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string((name ?? "").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        // Windows drops a folder name's trailing dots and spaces, which would turn ".." into the
        // parent folder. A zip always ends in ".zip", so only a folder needs this.
        if (isFolder) clean = clean.TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(clean)) clean = isFolder ? "resourcepack" : "resourcepack.zip";
        if (!isFolder && !clean.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) clean += ".zip";
        // A device name such as CON or NUL cannot be a file name at all.
        if (!PathSafety.IsSafeFileName(clean)) clean = "resourcepack-" + clean;
        return clean;
    }

    /// <summary>The pack's full path when it is a plain entry directly inside a resourcepacks
    /// folder, else null.</summary>
    private static string? EntryInPackFolder(ResourcePackInfo pack)
    {
        var dir = Path.GetDirectoryName(pack.FilePath);
        if (dir is null || !string.Equals(Path.GetFileName(dir), FolderName, StringComparison.OrdinalIgnoreCase))
            return null;
        return PathSafety.ResolveFileName(dir, Path.GetFileName(pack.FilePath));
    }
}

/// <summary>What a pack says about itself in <c>pack.png</c> and <c>pack.mcmeta</c>.</summary>
/// <param name="Icon">Frozen 128px icon, or null when the pack ships none.</param>
/// <param name="Description">Plain-text description with Minecraft's formatting flattened out.</param>
/// <param name="PackFormat">The pack format number, which is what decides whether the game considers
/// this pack compatible with the instance's version.</param>
public sealed record ResourcePackMeta(BitmapImage? Icon, string? Description, int? PackFormat)
{
    public static readonly ResourcePackMeta Empty = new(null, null, null);

    public bool HasAnything => Icon is not null || Description is not null || PackFormat is not null;
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
    IReadOnlyList<Guid> CompatiblePackIds,
    ResourcePackOrigin Origin = ResourcePackOrigin.Game,
    bool IsFolder = false,
    int StackIndex = -1)
{
    /// <summary>True when this pack is listed in the instance's options.txt.</summary>
    public bool Enabled => StackIndex >= 0;

    /// <summary>1 for the pack that wins, 2 for the one under it, and so on. 0 when not enabled.</summary>
    public int Priority => StackIndex >= 0 ? StackIndex + 1 : 0;

    public bool IsLocal => Origin == ResourcePackOrigin.Local;
}
