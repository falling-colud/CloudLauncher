using System.IO;
using System.IO.Compression;

namespace CloudLauncher.Services;

/// <summary>Where a world's data pack stands with the game.</summary>
public enum DataPackState
{
    /// <summary>Listed in level.dat's <c>DataPacks.Enabled</c>: it loads with the world.</summary>
    On,

    /// <summary>Listed in <c>DataPacks.Disabled</c>: the file is there and the game leaves it
    /// out.</summary>
    Off,

    /// <summary>In <c>datapacks/</c> but in neither list yet. The game turns a pack like this on by
    /// itself the next time the world loads.</summary>
    New
}

/// <summary>One data pack inside a world's <c>datapacks/</c> folder.</summary>
/// <param name="Priority">1 for the pack that wins, 2 for the one under it, and so on; 0 when the
/// pack is not on. Counts every enabled pack, including the game's own and the ones mods add, so
/// the numbers match <c>/datapack list</c>.</param>
/// <param name="HasPackMeta">False when there is no <c>pack.mcmeta</c> at the top of the pack, which
/// the game takes to mean "not a pack" and skips without a word.</param>
public sealed record DataPackInfo(
    string FileName,
    string FilePath,
    bool IsFolder,
    long SizeBytes,
    DateTimeOffset Modified,
    DataPackState State,
    int Priority,
    string? Description,
    int? PackFormat,
    bool HasPackMeta)
{
    /// <summary>The id the game uses for it in level.dat and in <c>/datapack</c> commands.</summary>
    public string PackId => DataPackService.FilePackId(FileName);

    /// <summary>The name without <c>.zip</c>.</summary>
    public string DisplayName => IsFolder ? FileName : Path.GetFileNameWithoutExtension(FileName);
}

/// <summary>What a world's level.dat says about its data packs, next to what is on disk.</summary>
/// <param name="Packs">The packs in <c>datapacks/</c>, highest priority first, then the ones that are
/// off or new.</param>
/// <param name="BuiltInOn">How many enabled packs are not files in <c>datapacks/</c>: the game's own
/// ("vanilla", feature packs) and those that mods and global-pack mods add.</param>
/// <param name="Readable">False when level.dat is missing or could not be read; states are then all
/// shown as <see cref="DataPackState.New"/> and nothing can be switched.</param>
public sealed record WorldDataPacks(
    IReadOnlyList<DataPackInfo> Packs,
    int BuiltInOn,
    bool Readable);

/// <summary>A world's data packs: listing them, switching them on and off, adding and removing
/// them.</summary>
/// <remarks>
/// <para>Uses the game's own switch: the <c>Enabled</c> and <c>Disabled</c> lists under
/// <c>Data.DataPacks</c> in level.dat, which is exactly what <c>/datapack enable</c> and
/// <c>/datapack disable</c> edit. Moving files out of <c>datapacks/</c> would also stop a pack
/// loading, but the game would then log it as missing and forget its place in the order, and a
/// pack moved back would come back as "new" at the top.</para>
/// <para>A world must never be damaged by this, so every level.dat write goes through
/// <see cref="EditLevelDat"/>: refused while Minecraft runs in the instance or the save's
/// <c>session.lock</c> is held, refused when level.dat cannot be read (writing back a file we
/// failed to read would replace the whole world's metadata), a copy kept in the instance's
/// <c>backups/level-dat/</c> first, and the written file read back before it counts.</para>
/// <para>Only the two lists are touched. Everything else in level.dat is written back exactly as
/// it was read, with the file's own compression.</para>
/// </remarks>
public sealed class DataPackService(PackFolderService packs, MinecraftInstanceService instances)
{
    /// <summary>The folder inside a save that the game reads data packs from.</summary>
    public const string FolderName = "datapacks";

    /// <summary>How many level.dat copies are kept per world. Each is a few KB to a few hundred.</summary>
    private const int BackupsKept = 10;

    /// <summary>The id the game gives a pack file in a world's <c>datapacks/</c> folder.</summary>
    public static string FilePackId(string fileName) => "file/" + fileName;

    /// <summary>True when <paramref name="minecraftVersion"/> has data packs at all.</summary>
    /// <remarks>They arrived in 1.13, the same release that spells resource pack entries
    /// <c>file/Name.zip</c>, so the version test is shared. An unknown version is treated as modern,
    /// since that is what the launcher installs by default.</remarks>
    public static bool VersionHasDataPacks(string? minecraftVersion) =>
        OptionsTxtService.VersionUsesFilePrefix(minecraftVersion);

    /// <summary>The save's <c>datapacks/</c> folder. Not created.</summary>
    public static string FolderFor(string worldDir) => Path.Combine(worldDir, FolderName);

    // ── reading ──────────────────────────────────────────────────────────────

    /// <summary>The world's data packs and what level.dat says about each. Never throws.</summary>
    /// <remarks>Opens each zip for its pack.mcmeta (memo-cached by
    /// <see cref="ResourcePackService.ReadMeta"/>), so call it off the UI thread.</remarks>
    public WorldDataPacks List(string worldDir)
    {
        var (enabled, disabled, readable) = ReadLists(worldDir);
        var packsFound = new List<DataPackInfo>();
        var dir = FolderFor(worldDir);

        // The game ranks everything in Enabled, its own and the mods' packs included; last wins.
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < enabled.Count; i++) rank[enabled[i]] = enabled.Count - i;

        try
        {
            if (Directory.Exists(dir))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
                {
                    var name = Path.GetFileName(entry);
                    var isFolder = Directory.Exists(entry);
                    if (name.StartsWith('.')) continue;
                    // The game loads zips and folders; anything else in there is not a pack.
                    if (!isFolder && !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;

                    var id = FilePackId(name);
                    var state = !readable ? DataPackState.New
                        : rank.ContainsKey(id) ? DataPackState.On
                        : disabled.Contains(id) ? DataPackState.Off
                        : DataPackState.New;

                    long size = 0;
                    var modified = DateTimeOffset.MinValue;
                    try
                    {
                        if (isFolder)
                        {
                            modified = Directory.GetLastWriteTime(entry);
                            size = new DirectoryInfo(entry).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                        }
                        else
                        {
                            var info = new FileInfo(entry);
                            modified = info.LastWriteTime;
                            size = info.Length;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

                    var meta = ResourcePackService.ReadMeta(entry, isFolder);
                    packsFound.Add(new DataPackInfo(
                        name, entry, isFolder, size, modified, state,
                        state == DataPackState.On ? rank[id] : 0,
                        meta.Description, meta.PackFormat,
                        HasPackMeta: meta.PackFormat is not null || meta.Description is not null || HasPackMetaFile(entry, isFolder)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.LogError(nameof(DataPackService), ex);
        }

        var fileIds = packsFound.Select(p => p.PackId).ToHashSet(StringComparer.Ordinal);
        var builtIn = enabled.Count(e => !fileIds.Contains(e));

        var ordered = packsFound
            .OrderBy(p => p.State switch { DataPackState.On => 0, DataPackState.New => 1, _ => 2 })
            .ThenBy(p => p.State == DataPackState.On ? p.Priority : int.MaxValue)
            .ThenBy(p => p.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new WorldDataPacks(ordered, builtIn, readable);
    }

    /// <summary>The Enabled and Disabled lists, in file order (lowest priority first).</summary>
    /// <returns><c>Readable</c> false when level.dat is missing or unreadable.</returns>
    private static (List<string> Enabled, HashSet<string> Disabled, bool Readable) ReadLists(string worldDir)
    {
        var root = Nbt.ReadFile(Path.Combine(worldDir, "level.dat"));
        if (root?["Data"] is not { } data) return ([], [], false);
        var dp = data["DataPacks"];
        return (Strings(dp?["Enabled"]).ToList(), Strings(dp?["Disabled"]).ToHashSet(StringComparer.Ordinal), true);
    }

    private static IEnumerable<string> Strings(NbtTag? list) =>
        list is { Type: NbtTagType.List }
            ? list.Children.Where(c => c.Type == NbtTagType.String).Select(c => c.StringValue)
            : [];

    /// <summary>True when the pack has a <c>pack.mcmeta</c> at its top, where the game looks.</summary>
    public static bool HasPackMetaFile(string path, bool isFolder)
    {
        try
        {
            if (isFolder) return File.Exists(Path.Combine(path, "pack.mcmeta"));
            using var zip = ZipFile.OpenRead(path);
            return zip.GetEntry("pack.mcmeta") is not null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ── switching ────────────────────────────────────────────────────────────

    /// <summary>Turns a pack on (to the top of the order, as <c>/datapack enable</c> does) or off.</summary>
    /// <exception cref="DataPackWriteRefusedException">The world is in use or its level.dat can't be
    /// read; nothing was written.</exception>
    public void SetEnabled(Guid packId, string worldDir, string fileName, bool enabled)
    {
        var id = FilePackId(fileName);
        EditLevelDat(packId, worldDir, lists =>
        {
            RemoveAll(lists.Enabled, id);
            RemoveAll(lists.Disabled, id);
            // Enabled runs lowest priority first, so the end is the top.
            (enabled ? lists.Enabled : lists.Disabled).Children.Add(NbtTag.NewString("", id));
        });
    }

    // ── adding and removing ──────────────────────────────────────────────────

    /// <summary>Copies a pack (zip or folder) into the world's <c>datapacks/</c> folder.</summary>
    /// <remarks>
    /// <para>Never overwrites: a second copy of the same name becomes <c>name-2.zip</c>, as the other
    /// content pages do.</para>
    /// <para>The game turns a new pack on by itself when the world next loads, unless its id is on
    /// the Disabled list from an earlier copy of the same name. That entry is cleared here, so adding a
    /// pack always means "use it".</para>
    /// </remarks>
    /// <returns>The path it was copied to.</returns>
    /// <exception cref="DataPackWriteRefusedException">The world is in use; nothing was copied.</exception>
    public string Add(Guid packId, string worldDir, string sourcePath)
    {
        EnsureWritable(packId, worldDir);
        var isFolder = Directory.Exists(sourcePath);
        var name = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!isFolder && !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new IOException($"{name} is not a .zip or a folder, so the game would not load it as a data pack.");

        var dir = FolderFor(worldDir);
        Directory.CreateDirectory(dir);
        var dest = ShaderPackService.NextFreePath(dir, name);
        if (isFolder) CopyDirectory(sourcePath, dest);
        else File.Copy(sourcePath, dest);

        var id = FilePackId(Path.GetFileName(dest));
        var (_, disabled, readable) = ReadLists(worldDir);
        if (readable && disabled.Contains(id))
            EditLevelDat(packId, worldDir, lists => RemoveAll(lists.Disabled, id));
        WorldService.Invalidate(worldDir);
        return dest;
    }

    /// <summary>Deletes a pack from the world's <c>datapacks/</c> folder and drops it from both
    /// lists.</summary>
    /// <remarks>The file goes first: a pack still listed but missing only earns a "Missing data
    /// pack" line in the game's log, while a pack deleted from the lists but not the disk would come
    /// back on by itself as new.</remarks>
    /// <exception cref="DataPackWriteRefusedException">The world is in use; nothing was deleted.</exception>
    public void Remove(Guid packId, string worldDir, DataPackInfo pack)
    {
        EnsureWritable(packId, worldDir);
        var dir = FolderFor(worldDir);
        var path = PathSafety.ResolveFileName(dir, pack.FileName)
                   ?? throw new IOException($"'{pack.FileName}' is not a file inside this world's datapacks folder.");
        if (Directory.Exists(path))
        {
            if (PathSafety.CrossesLink(dir, path))
                throw new IOException($"Refused to delete {path}: it is a link to somewhere else.");
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path)) File.Delete(path);

        var (enabled, disabled, readable) = ReadLists(worldDir);
        if (readable && (enabled.Contains(pack.PackId) || disabled.Contains(pack.PackId)))
            EditLevelDat(packId, worldDir, lists =>
            {
                RemoveAll(lists.Enabled, pack.PackId);
                RemoveAll(lists.Disabled, pack.PackId);
            });
        WorldService.Invalidate(worldDir);
    }

    // ── the one level.dat writer ─────────────────────────────────────────────

    /// <summary>The two lists, as live NBT tags inside the root being edited.</summary>
    private sealed record Lists(NbtTag Enabled, NbtTag Disabled);

    /// <summary>Refuses, with a sentence for the user, when this world must not be written now.</summary>
    /// <remarks>Public so a move that also writes somewhere else can ask before it starts, rather than
    /// finding out half way.</remarks>
    /// <exception cref="DataPackWriteRefusedException">Minecraft runs in the instance, or the save is
    /// open in a game the launcher did not start.</exception>
    public void EnsureWritable(Guid packId, string worldDir)
    {
        if (!ContentWriteGate.Allows(instances, packId, duringLaunch: false, out var why))
            throw new DataPackWriteRefusedException(why);
        if (SessionLockHeld(worldDir))
            throw new DataPackWriteRefusedException(
                "This world is open in Minecraft - close it before changing its data packs.");
    }

    /// <summary>True when something holds the save's <c>session.lock</c> open.</summary>
    /// <remarks>The game keeps that file open (and locked) for as long as the world is loaded, even
    /// when it was started by another launcher or a server pointed at this folder, which the instance
    /// status can't see. A missing lock file means nobody has the world open.</remarks>
    private static bool SessionLockHeld(string worldDir)
    {
        var path = Path.Combine(worldDir, "session.lock");
        if (!File.Exists(path)) return false;
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return false; }   // read-only file, not a lock
    }

    /// <summary>Reads level.dat, lets <paramref name="edit"/> change the two lists, and writes it
    /// back safely.</summary>
    /// <exception cref="DataPackWriteRefusedException">See <see cref="EnsureWritable"/>, or level.dat
    /// is missing, unreadable or not a world's.</exception>
    private void EditLevelDat(Guid packId, string worldDir, Action<Lists> edit)
    {
        EnsureWritable(packId, worldDir);

        var path = Path.Combine(worldDir, "level.dat");
        if (!File.Exists(path))
            throw new DataPackWriteRefusedException("This world has no level.dat, so its data packs can't be switched.");

        // Null covers locked and corrupt alike. Either way the file is not ours to replace.
        var root = Nbt.ReadFile(path, out var compression)
                   ?? throw new DataPackWriteRefusedException(
                       "This world's level.dat could not be read (it may be open or damaged), so nothing was changed.");
        if (root["Data"] is not { Type: NbtTagType.Compound } data)
            throw new DataPackWriteRefusedException("This world's level.dat has no world data in it, so nothing was changed.");

        var dataPacks = data["DataPacks"];
        if (dataPacks is null)
        {
            // Older saves and some generators leave it out. The game's own default is vanilla alone.
            dataPacks = NbtTag.NewCompound("DataPacks");
            var fresh = NbtTag.NewList("Enabled", NbtTagType.String);
            fresh.Children.Add(NbtTag.NewString("", "vanilla"));
            dataPacks.Children.Add(fresh);
            data.Children.Add(dataPacks);
        }
        else if (dataPacks.Type != NbtTagType.Compound)
        {
            throw new DataPackWriteRefusedException("This world's level.dat stores its data packs in a shape the launcher doesn't know, so nothing was changed.");
        }

        var lists = new Lists(ListIn(dataPacks, "Enabled"), ListIn(dataPacks, "Disabled"));
        edit(lists);

        var backup = BackUp(packId, worldDir, path);
        try
        {
            Nbt.WriteFile(path, root, compression);
            // Proof, not hope: what was written has to read back with the lists in it.
            if (Nbt.ReadFile(path)?["Data"]?["DataPacks"] is null)
                throw new IOException("level.dat did not read back after writing.");
        }
        catch (Exception ex) when (ex is not DataPackWriteRefusedException)
        {
            AppLog.LogError(nameof(DataPackService), ex);
            try { File.Copy(backup, path, overwrite: true); }
            catch (Exception restoreEx) { AppLog.LogError(nameof(DataPackService), restoreEx); }
            throw new DataPackWriteRefusedException(
                $"level.dat could not be written ({ex.Message}). The copy from just before is back in place.");
        }
        finally { WorldService.Invalidate(worldDir); }
    }

    /// <summary>The named string list inside <paramref name="dataPacks"/>, created empty when absent.</summary>
    /// <exception cref="DataPackWriteRefusedException">A list of something other than strings.</exception>
    private static NbtTag ListIn(NbtTag dataPacks, string name)
    {
        if (dataPacks[name] is { } existing)
        {
            if (existing.Type != NbtTagType.List
                || existing.Children.Any(c => c.Type != NbtTagType.String))
                throw new DataPackWriteRefusedException(
                    $"This world's level.dat has a {name} list the launcher doesn't know, so nothing was changed.");
            existing.ListElementType = NbtTagType.String;
            return existing;
        }
        var list = NbtTag.NewList(name, NbtTagType.String);
        dataPacks.Children.Add(list);
        return list;
    }

    private static void RemoveAll(NbtTag list, string id) =>
        list.Children.RemoveAll(c => c.Type == NbtTagType.String && string.Equals(c.StringValue, id, StringComparison.Ordinal));

    /// <summary>Copies level.dat to <c>&lt;instance&gt;/backups/level-dat/</c> and keeps the newest
    /// <see cref="BackupsKept"/> per world.</summary>
    /// <remarks>Beside <c>game/</c>, like the world backups, so the copies never sync or export.
    /// <c>level.dat_old</c> is not enough on its own: the game overwrites it on every save, so after
    /// one play session it no longer holds the file from before the launcher's change.</remarks>
    /// <returns>The copy's path.</returns>
    /// <exception cref="DataPackWriteRefusedException">The copy could not be made; nothing is written
    /// without one.</exception>
    private string BackUp(Guid packId, string worldDir, string levelDat)
    {
        try
        {
            var dir = Path.Combine(packs.PackRoot(packId), "backups", "level-dat");
            Directory.CreateDirectory(dir);
            var stem = WorldService.SafeFolderName(Path.GetFileName(worldDir));
            var copy = Path.Combine(dir, $"{stem}-{TimeFormat.StampNow()}.dat");
            // Two edits in one second share a stamp; the later copy is of the same file anyway.
            File.Copy(levelDat, copy, overwrite: true);

            foreach (var old in Directory.EnumerateFiles(dir, stem + "-*.dat")
                         .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                         .Skip(BackupsKept))
            {
                try { File.Delete(old); } catch (IOException) { }
            }
            return copy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.LogError(nameof(DataPackService), ex);
            throw new DataPackWriteRefusedException(
                $"level.dat could not be backed up first ({ex.Message}), so nothing was changed.");
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
        foreach (var sub in Directory.EnumerateDirectories(source))
            CopyDirectory(sub, Path.Combine(dest, Path.GetFileName(sub)));
    }
}

/// <summary>A data pack change the launcher refused to make, with the reason written for the
/// user.</summary>
/// <remarks>Its own type so pages can show the message as it is, instead of wrapping it in "Error:".</remarks>
public sealed class DataPackWriteRefusedException(string message) : InvalidOperationException(message);
