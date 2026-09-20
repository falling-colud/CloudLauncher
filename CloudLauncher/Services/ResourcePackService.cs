using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows.Media.Imaging;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>Which of an instance's two resourcepacks/ folders a pack was found in.</summary>
/// <remarks>A shared instance keeps its own unsynced files under <c>local/</c>; both folders end up
/// merged into the game directory at launch, so a pack in either one is a pack the game will load.
/// The distinction still matters to the user, because only the <c>game/</c> copy is shared with the
/// other people on that instance.</remarks>
public enum ResourcePackOrigin
{
    Game = 0,
    Local
}

/// <summary>
/// Scans each instance's <c>resourcepacks/</c> folders, and owns which packs are turned on and in
/// what order.
/// </summary>
/// <remarks>
/// Two things make resource packs different from mods. First, a pack is not "installed" by being in
/// the folder — it also has to be listed in <c>options.txt</c>, in priority order, which is what
/// <see cref="ActiveFor"/> and <see cref="SetActive"/> read and write. Second, a pack may be either a
/// zip or an unpacked folder; Minecraft loads both, so both are scanned, and every path that deletes,
/// copies or renames one has to cope with a directory as well as a file.
/// </remarks>
public sealed class ResourcePackService(AppSettings settings, PackFolderService packs)
{
    private const string FolderName = "resourcepacks";

    /// <summary>Settings key for a pack in the instance's shared <c>game/</c> folder.</summary>
    public static string Key(Guid sourcePackId, string fileName) => $"{sourcePackId:N}:{fileName}";

    /// <summary>
    /// Settings key for a pack, qualified by which folder it lives in.
    /// </summary>
    /// <remarks>The <c>game/</c> form is left exactly as it always was so that display names, folders
    /// and hosting links recorded by earlier versions keep resolving; only the <c>local/</c> form is
    /// new, and it is namespaced so that the same file name in both folders is two packs rather than
    /// one entry the two of them fight over.</remarks>
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

    /// <summary>
    /// Every resource pack installed in an instance, with its enabled state and stack position.
    /// </summary>
    /// <param name="includeLocal">Scan the unsynced <c>local/resourcepacks/</c> folder too. Cheap
    /// enough to leave on — the folder simply does not exist for a non-shared instance.</param>
    public List<ResourcePackInfo> ScanPack(Guid packId, string packName, bool includeLocal)
    {
        var gameDir = packs.GameDir(packId, packName);

        // Read the stack once per instance rather than once per pack: it is one file, and a card grid
        // over a dozen instances would otherwise re-read and re-parse it for every zip on screen.
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

                // A zip, or an unpacked pack folder — Minecraft loads both. Anything else in there
                // (a stray readme, the OS's own hidden files) is not a resource pack.
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

                // The stack lists file names, so a pack enabled in game/ and a same-named one in
                // local/ both read as on — which is exactly what happens in the merged folder.
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

    /// <summary>
    /// The file names of the packs this instance has turned on, <b>highest priority first</b>.
    /// </summary>
    /// <remarks>
    /// This is deliberately the reverse of what is in options.txt, where the last element wins. Every
    /// caller in the UI thinks top-of-list = wins, which is also how the in-game screen reads, so the
    /// flip happens once, here, instead of in each view. "vanilla" never appears: it is the base the
    /// stack sits on, not a pack the user can order.
    /// </remarks>
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

    /// <summary>Replaces the enabled stack. <paramref name="fileNamesHighestFirst"/> is in the order
    /// the UI shows it; the file gets the reversed list plus "vanilla" at the bottom.</summary>
    public void SetActive(Guid packId, string? packName, IReadOnlyList<string> fileNamesHighestFirst)
    {
        var dir = packName is null ? packs.GameDir(packId) : packs.GameDir(packId, packName);
        var prefix = OptionsTxtService.UsesFilePrefix(dir) ? "file/" : "";

        var ordered = fileNamesHighestFirst
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Reverse()
            .Select(n => prefix + n)
            .ToList();

        OptionsTxtService.WriteResourcePacks(dir, ordered);
    }

    /// <summary>Turns a pack on or off, without disturbing the order of the others.</summary>
    /// <remarks>A newly enabled pack goes to the top of the stack — highest priority — which is where
    /// the game itself puts one you select, and is what somebody who just installed a texture pack
    /// expects to see the moment they load a world.</remarks>
    public void SetEnabled(Guid packId, string? packName, string fileName, bool enabled)
    {
        var stack = ActiveFor(packId, packName);
        stack.RemoveAll(n => string.Equals(n, fileName, StringComparison.OrdinalIgnoreCase));
        if (enabled) stack.Insert(0, fileName);
        SetActive(packId, packName, stack);
    }

    /// <summary>
    /// Moves an enabled pack <paramref name="delta"/> places up (negative) or down (positive) the
    /// stack, clamped at both ends. A pack that is not enabled is not in the stack and is ignored.
    /// </summary>
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

    /// <summary>Renames a pack inside the stack so that renaming the file on disk does not silently
    /// turn it off. A no-op when the pack was not enabled.</summary>
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

    /// <summary>
    /// The pack's own icon, description and pack_format, read out of <c>pack.png</c> and
    /// <c>pack.mcmeta</c>.
    /// </summary>
    /// <remarks>
    /// Cached by path plus last-write-time plus size, because the card grid asks for this on every
    /// filter keystroke and opening a 300 MB zip per keystroke is not free. Returns
    /// <see cref="ResourcePackMeta.Empty"/> rather than null for a pack with no metadata, so a
    /// malformed zip is cached as "nothing here" instead of being reopened forever. Never throws: a
    /// pack whose zip is corrupt simply falls back to the generated letter tile.
    /// </remarks>
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

    private static ResourcePackMeta ReadZipMeta(string zipPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);

            var icon = archive.GetEntry("pack.png") is { } png ? LoadIcon(() => png.Open()) : null;
            var mcmeta = archive.GetEntry("pack.mcmeta");
            string? json = null;
            if (mcmeta is not null)
            {
                using var reader = new StreamReader(mcmeta.Open());
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
            var json = File.Exists(metaPath) ? File.ReadAllText(metaPath) : null;
            return BuildMeta(icon, json);
        }
        catch { return ResourcePackMeta.Empty; }
    }

    /// <summary>
    /// Decodes pack.png into a frozen bitmap.
    /// </summary>
    /// <remarks>The stream is copied into memory first and <c>CacheOption.OnLoad</c> forces the decode
    /// to happen here: a zip entry stream does not survive the lazy decode WPF would otherwise do, and
    /// freezing is what lets the scan run off the UI thread and still hand the image to a binding.
    /// Icons are 128px at most — they are drawn into a card cover, and a 1024px pack.png would cost
    /// fifty times the memory for no visible gain.</remarks>
    private static BitmapImage? LoadIcon(Func<Stream> open)
    {
        try
        {
            using var source = open();
            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            if (buffer.Length == 0) return null;
            buffer.Position = 0;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.DecodePixelWidth = 128;
            image.StreamSource = buffer;
            image.EndInit();
            image.Freeze();
            return image;
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

    /// <summary>
    /// Flattens a pack description into plain text.
    /// </summary>
    /// <remarks>The field is a Minecraft text component, so it is a bare string in most packs but can
    /// equally be an object with <c>text</c> and <c>extra</c>, or an array of those — which is how
    /// authors colour their description. Only the words are wanted here; the formatting is Minecraft's
    /// and would not survive into a WPF tooltip anyway.</remarks>
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

    /// <summary>
    /// Moves everything recorded about a pack from one settings key to another, following it into
    /// every user folder it was filed in.
    /// </summary>
    /// <remarks>The key contains the file name, so renaming the file on disk changes it. Without this
    /// the rename would silently drop the pack's display name, hosting link and folder membership —
    /// the pack would come back looking like one that had just been dropped in by hand.</remarks>
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

    /// <summary>
    /// Records which store listing an installed pack came from.
    /// </summary>
    /// <remarks>Written at install time, because nothing inside a resource pack zip identifies it
    /// afterwards. Matching a pack back to a store by name is how a user gets offered a different
    /// author's same-named pack as an "update", so a pack with no provenance is left alone rather than
    /// guessed at. Passing a null <paramref name="source"/> clears it, which is right for a file the
    /// user replaced by hand.</remarks>
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

    /// <summary>
    /// Deletes a pack from disk — recursively for an unpacked folder — and forgets everything the
    /// launcher knew about it, including its place in the instance's stack.
    /// </summary>
    /// <remarks>Blocking, and deliberately so: callers run it inside <c>Task.Run</c> because deleting
    /// a large unpacked pack is thousands of file operations. Settings are not saved here; the caller
    /// saves once after a batch.</remarks>
    public void DeleteFromDisk(ResourcePackInfo pack)
    {
        if (pack.IsFolder)
        {
            if (Directory.Exists(pack.FilePath)) Directory.Delete(pack.FilePath, recursive: true);
        }
        else if (File.Exists(pack.FilePath))
        {
            File.Delete(pack.FilePath);
        }

        RemoveFromStack(pack.SourcePackId, pack.SourcePackName, pack.FileName);
        Forget(pack.Key);
    }

    /// <summary>
    /// Renames the file or folder on disk, carrying the settings entry and the options.txt stack
    /// entry across with it.
    /// </summary>
    /// <returns>The new settings key.</returns>
    /// <exception cref="IOException">A pack of that name is already there — the caller reports it
    /// rather than overwriting somebody's pack.</exception>
    public string RenameFileOnDisk(ResourcePackInfo pack, string newFileName)
    {
        var clean = SanitizeFileName(newFileName, pack.IsFolder);
        if (string.Equals(clean, pack.FileName, StringComparison.Ordinal)) return pack.Key;

        var dir = Path.GetDirectoryName(pack.FilePath)
                  ?? throw new IOException("This pack is not inside a resourcepacks folder.");
        var target = Path.Combine(dir, clean);

        if (File.Exists(target) || Directory.Exists(target))
            throw new IOException($"'{clean}' already exists in that folder.");

        if (pack.IsFolder) Directory.Move(pack.FilePath, target);
        else File.Move(pack.FilePath, target);

        RenameInStack(pack.SourcePackId, pack.SourcePackName, pack.FileName, clean);

        var newKey = Key(pack.SourcePackId, clean, pack.Origin);
        ReKey(pack.Key, newKey);
        return newKey;
    }

    /// <summary>Strips characters Windows will not accept and keeps the .zip suffix on a zip.</summary>
    public static string SanitizeFileName(string name, bool isFolder)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string((name ?? "").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(clean)) clean = isFolder ? "resourcepack" : "resourcepack.zip";
        if (!isFolder && !clean.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) clean += ".zip";
        return clean;
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
