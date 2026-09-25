using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>
/// Scans pack <c>game/saves/</c> folders for Minecraft worlds and tracks which packs
/// may run each world. Storage is local; worlds don't sync to the server.
/// </summary>
/// <remarks>
/// Bulk disk work (scanning, copying, zipping, restoring) is meant to run on a background thread
/// with a <see cref="CancellationToken"/>: a modded save can be several gigabytes across tens of
/// thousands of files.
/// </remarks>
public sealed class WorldService(AppSettings settings, PackFolderService packs)
{
    public static string Key(Guid sourcePackId, string folderName) => $"{sourcePackId:N}:{folderName}";

    /// <summary>
    /// Folder sizes already measured, keyed by save folder path.
    /// </summary>
    /// <remarks>
    /// Walking a big save takes seconds. Entries are stamped with the save's last-write time, so
    /// playing, importing into or restoring a world invalidates its entry.
    /// </remarks>
    private static readonly ConcurrentDictionary<string, (DateTime Stamp, long Size)> SizeCache = new();

    /// <summary>Metadata parsed from <c>level.dat</c>, keyed by save folder path, stamped the same way.</summary>
    private static readonly ConcurrentDictionary<string, (DateTime Stamp, WorldMeta Meta)> MetaCache = new();

    /// <summary>List all worlds across the supplied packs.</summary>
    public List<WorldInfo> ScanAll(IReadOnlyList<PackSummary> knownPacks, CancellationToken ct = default)
    {
        var result = new List<WorldInfo>();
        foreach (var p in knownPacks)
        {
            ct.ThrowIfCancellationRequested();
            try { result.AddRange(ScanPack(p, ct)); }
            catch (OperationCanceledException) { throw; }
            catch { /* missing folder etc; skip */ }
        }
        return result.OrderByDescending(w => w.LastModified).ToList();
    }

    /// <summary>List worlds for a single pack.</summary>
    public List<WorldInfo> ScanPack(PackSummary pack, CancellationToken ct = default) =>
        ScanPack(pack.Id, pack.Name, ct);

    public List<WorldInfo> ScanPack(Guid packId, string packName, CancellationToken ct = default)
    {
        var savesDir = SavesDir(packId, packName);
        if (!Directory.Exists(savesDir)) return new();

        var result = new List<WorldInfo>();
        foreach (var dir in Directory.EnumerateDirectories(savesDir))
        {
            ct.ThrowIfCancellationRequested();
            var folder = Path.GetFileName(dir);
            var key = Key(packId, folder);
            var entry = settings.Worlds.TryGetValue(key, out var e) ? e : null;
            var meta = ReadMeta(dir);
            var displayName = !string.IsNullOrEmpty(entry?.DisplayName)
                ? entry.DisplayName
                : meta.LevelName ?? folder;
            var size = DirectorySize(dir, ct);
            var lastMod = meta.LastPlayed ?? SafeLastWriteTime(dir);
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
                CompatiblePackIds: compat,
                Meta: meta));
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

    /// <summary>Forgets the server-side world this save was published as, after it has been deleted
    /// on the server. Leaves every local setting (name, compatibility, folders) untouched.</summary>
    public void UnlinkSharedWorld(string key)
    {
        var e = GetOrCreate(key);
        e.SharedWorldId = null;
        settings.Save();
    }

    public void SetSharingEnabled(string key, bool enabled)
    {
        var e = GetOrCreate(key);
        e.SharingEnabled = enabled;
        settings.Save();
    }

    /// <summary>Drops every launcher-side trace of a world: its settings entry and any folder it was
    /// filed into. Call after the save folder itself is gone, or the page will keep showing it.</summary>
    public void Forget(string key)
    {
        settings.Worlds.Remove(key);
        foreach (var members in settings.WorldFolders.Values) members.Remove(key);
        settings.Save();
    }

    /// <summary>True if the world can be launched with the given pack.</summary>
    public bool IsCompatible(WorldInfo world, Guid packId) =>
        world.SourcePackId == packId || world.CompatibleWithAll || world.CompatiblePackIds.Contains(packId);

    public List<PackSummary> CompatiblePacks(WorldInfo world, IReadOnlyList<PackSummary> allPacks) =>
        allPacks.Where(p => IsCompatible(world, p.Id)).ToList();

    /// <summary>An instance's <c>saves/</c> folder. <paramref name="packName"/> is only needed when the
    /// pack folder may not exist yet, because that is the one case where it has to be created.</summary>
    public string SavesDir(Guid packId, string? packName = null) =>
        Path.Combine(string.IsNullOrEmpty(packName) ? packs.GameDir(packId) : packs.GameDir(packId, packName),
                     "saves");

    // ── copying, importing, exporting ──

    /// <summary>
    /// Copies a save folder. Progress is the fraction (0 to 1) of files copied.
    /// </summary>
    /// <remarks>
    /// Files are listed up front so the progress is real. <c>session.lock</c> is opened share-all and
    /// skipped if the running game holds it; it is a marker the game rewrites on load.
    /// </remarks>
    public static async Task CopyWorldAsync(string src, string dst, IProgress<double>? progress = null,
                                            CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            Directory.CreateDirectory(dst);
            foreach (var sub in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, sub)));

            var files = Directory.GetFiles(src, "*", SearchOption.AllDirectories);
            for (var i = 0; i < files.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(src, files[i]);
                var target = Path.Combine(dst, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                try { File.Copy(files[i], target, overwrite: true); }
                catch (IOException) when (IsLockFile(rel)) { /* game holds it; it is regenerated */ }
                progress?.Report((i + 1) / (double)files.Length);
            }
        }, ct);
    }

    /// <summary>
    /// Copies a save over an existing one at <paramref name="dst"/>, all-or-nothing.
    /// </summary>
    /// <remarks>
    /// The copy is built in a temporary sibling and only swapped in once complete, and the old save is
    /// moved aside rather than deleted, so a cancel (callers pass their Cancel button's token) or a
    /// failed swap always leaves an intact copy.
    /// </remarks>
    public static async Task ReplaceWorldAsync(string src, string dst, IProgress<double>? progress = null,
                                               CancellationToken ct = default)
    {
        // Invariant and dated, so a suffix left behind by a crash is readable and can't collide with a
        // later run. Seconds are enough: the UI disables the button while a replace runs.
        var stamp = TimeFormat.StampNow();
        var staging = dst + ".incoming-" + stamp;
        try
        {
            await CopyWorldAsync(src, staging, progress, ct);
        }
        catch
        {
            await TryDeleteDirectoryAsync(staging);
            throw;
        }

        // Committed from here: the copy is whole on disk, so the swap runs uncancellably.
        var parked = dst + ".replaced-" + stamp;
        var hadExisting = Directory.Exists(dst);
        try
        {
            await Task.Run(() =>
            {
                if (hadExisting) Directory.Move(dst, parked);
                Directory.Move(staging, dst);
            }, CancellationToken.None);
        }
        catch
        {
            if (hadExisting && !Directory.Exists(dst) && Directory.Exists(parked))
            {
                try { Directory.Move(parked, dst); } catch { /* left as .replaced-... on disk */ }
            }
            await TryDeleteDirectoryAsync(staging);
            throw;
        }

        Invalidate(dst);
        if (hadExisting) await TryDeleteDirectoryAsync(parked);
    }

    /// <summary>
    /// Removes a directory if it is there, swallowing failures.
    /// </summary>
    /// <remarks>For a swap's temporary folders, where failing to tidy up must never fail the operation
    /// or touch the live copy.</remarks>
    private static async Task TryDeleteDirectoryAsync(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                await Task.Run(() => DeleteFolderInside(Path.GetDirectoryName(dir)!, dir), CancellationToken.None);
        }
        catch { /* best effort */ }
    }

    /// <summary>Copies a world into another instance's <c>saves/</c> and returns the folder name it
    /// landed under, which is uniquified rather than overwriting an existing save.</summary>
    public async Task<string> CopyToPackAsync(WorldInfo world, Guid targetPackId, string targetPackName,
                                              IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var savesDir = SavesDir(targetPackId, targetPackName);
        Directory.CreateDirectory(savesDir);
        var folderName = UniqueFolderName(savesDir, world.FolderName);
        await CopyWorldAsync(world.FolderPath, Path.Combine(savesDir, folderName), progress, ct);
        return folderName;
    }

    /// <summary>Duplicates a world beside itself, as "name-copy".</summary>
    public async Task<string> DuplicateAsync(WorldInfo world, IProgress<double>? progress = null,
                                             CancellationToken ct = default)
    {
        var savesDir = Path.GetDirectoryName(world.FolderPath)!;
        var folderName = UniqueFolderName(savesDir, world.FolderName + "-copy");
        await CopyWorldAsync(world.FolderPath, Path.Combine(savesDir, folderName), progress, ct);
        return folderName;
    }

    /// <summary>Imports a world folder from anywhere on disk into an instance. Returns the new folder name.</summary>
    public async Task<string> ImportFolderAsync(string sourceDir, Guid packId, string packName,
                                                IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var savesDir = SavesDir(packId, packName);
        Directory.CreateDirectory(savesDir);
        var folderName = UniqueFolderName(savesDir, Path.GetFileName(sourceDir.TrimEnd(Path.DirectorySeparatorChar)));
        await CopyWorldAsync(sourceDir, Path.Combine(savesDir, folderName), progress, ct);
        return folderName;
    }

    /// <summary>
    /// Extracts a downloaded world .zip into an instance's saves folder. Returns the new folder name.
    /// </summary>
    /// <remarks>
    /// Many zips wrap the save in one top-level folder, hence <see cref="FlattenIfSingleSubdir"/>. If no
    /// <c>level.dat</c> turns up, the extracted folder is removed again and the caller is told.
    /// </remarks>
    public async Task<string> ImportZipAsync(string zipPath, Guid packId, string packName, string? preferredName = null,
                                             IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var savesDir = SavesDir(packId, packName);
        Directory.CreateDirectory(savesDir);
        var baseName = SafeFolderName(preferredName ?? Path.GetFileNameWithoutExtension(zipPath));
        var folderName = UniqueFolderName(savesDir, baseName);
        var targetDir = PathSafety.ResolveFileName(savesDir, folderName)
            ?? throw new IOException($"'{folderName}' cannot be used as a save folder name.");

        try
        {
            await ExtractZipAsync(zipPath, targetDir, progress, ct);
            FlattenIfSingleSubdir(targetDir);
            if (!IsWorldFolder(targetDir))
                throw new InvalidOperationException("That .zip does not contain a Minecraft world (no level.dat inside).");
            return folderName;
        }
        catch
        {
            try { if (Directory.Exists(targetDir)) DeleteFolderInside(savesDir, targetDir); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>Zips a save to <paramref name="destZip"/>, overwriting it.</summary>
    public static Task ExportZipAsync(WorldInfo world, string destZip, IProgress<double>? progress = null,
                                      CancellationToken ct = default) =>
        ZipDirectoryAsync(world.FolderPath, destZip, progress, ct);

    /// <summary>Zips a save into the temp folder and returns the path. The caller deletes it.</summary>
    public static async Task<string> ZipToTempAsync(WorldInfo world, IProgress<double>? progress = null,
                                                    CancellationToken ct = default)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cl-world-{Guid.NewGuid():N}.zip");
        await ZipDirectoryAsync(world.FolderPath, path, progress, ct);
        return path;
    }

    // ── backups ──

    /// <summary>
    /// Where a world's backups live: <c>&lt;pack root&gt;/backups/</c>, beside <c>game/</c> rather than
    /// inside it.
    /// </summary>
    /// <remarks>
    /// Inside game/ the zips would be picked up by manifests, syncs and pack exports. Beside it they are
    /// still per-instance and removed with the instance.
    /// </remarks>
    public string BackupsDir(Guid packId) => Path.Combine(packs.PackRoot(packId), "backups");

    /// <summary>Zips the save into the instance's backups folder. Returns the zip path.</summary>
    public async Task<string> BackupAsync(WorldInfo world, IProgress<double>? progress = null,
                                          CancellationToken ct = default)
    {
        var dir = BackupsDir(world.SourcePackId);
        Directory.CreateDirectory(dir);
        // Invariant: IsBackupOf below parses this tail, and a non-Gregorian year here would hide the
        // backup.
        var zipPath = Path.Combine(dir, $"{SafeFolderName(world.FolderName)}-{TimeFormat.StampNow()}.zip");
        await ZipDirectoryAsync(world.FolderPath, zipPath, progress, ct);
        return zipPath;
    }

    /// <summary>
    /// Backups taken of this world, newest first.
    /// </summary>
    /// <remarks>
    /// Matches the whole <c>&lt;folder&gt;-&lt;timestamp&gt;.zip</c> name, since a prefix test would also
    /// pick up backups of <c>MyWorld-copy</c> or <c>MyWorld-2</c> for <c>MyWorld</c>.
    /// </remarks>
    public List<WorldBackup> ListBackups(WorldInfo world)
    {
        var dir = BackupsDir(world.SourcePackId);
        if (!Directory.Exists(dir)) return new();
        var prefix = SafeFolderName(world.FolderName) + "-";
        var result = new List<WorldBackup>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.zip"))
        {
            var name = Path.GetFileName(file);
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsBackupStamp(Path.GetFileNameWithoutExtension(name).AsSpan(prefix.Length))) continue;
            try
            {
                var info = new FileInfo(file);
                result.Add(new WorldBackup(file, name, info.Length, info.LastWriteTime));
            }
            catch (IOException) { /* vanished mid-enumeration */ }
        }
        return result.OrderByDescending(b => b.TakenAt).ToList();
    }

    /// <summary>True for the <c>yyyyMMdd-HHmmss</c> tail <see cref="BackupAsync"/> appends.</summary>
    private static bool IsBackupStamp(ReadOnlySpan<char> tail)
    {
        if (tail.Length != 15 || tail[8] != '-') return false;
        for (var i = 0; i < tail.Length; i++)
            if (i != 8 && !char.IsAsciiDigit(tail[i])) return false;
        return true;
    }

    /// <summary>
    /// Replaces the live save with the contents of a backup zip.
    /// </summary>
    /// <remarks>
    /// A safety backup of the current state is taken first, and the old folder is moved aside until the
    /// extract succeeds. That is the commit point: nothing after it rolls back, and a failed cleanup
    /// only returns the parked folder's path.
    /// </remarks>
    public async Task<string?> RestoreAsync(WorldInfo world, string zipPath, IProgress<double>? progress = null,
                                            CancellationToken ct = default)
    {
        await BackupAsync(world, null, ct);

        var parked = world.FolderPath + ".restoring-" + TimeFormat.StampNow();
        Directory.Move(world.FolderPath, parked);
        try
        {
            await ExtractZipAsync(zipPath, world.FolderPath, progress, ct);
            FlattenIfSingleSubdir(world.FolderPath);
            if (!IsWorldFolder(world.FolderPath))
                throw new InvalidOperationException("That backup does not contain a level.dat.");
        }
        catch
        {
            // Put the world back as it was before doing anything else.
            try
            {
                if (Directory.Exists(world.FolderPath))
                    DeleteFolderInside(Path.GetDirectoryName(world.FolderPath)!, world.FolderPath);
                Directory.Move(parked, world.FolderPath);
            }
            catch { /* the parked copy is still on disk under its .restoring name */ }
            Invalidate(world.FolderPath);
            throw;
        }

        Invalidate(world.FolderPath);
        try
        {
            await Task.Run(() => DeleteFolderInside(Path.GetDirectoryName(parked)!, parked), CancellationToken.None);
            return null;
        }
        catch
        {
            return parked;
        }
    }

    public static void DeleteBackup(string zipPath)
    {
        try { File.Delete(zipPath); } catch (IOException) { throw; }
    }

    // ── renaming ──

    /// <summary>
    /// Writes a new <c>LevelName</c> into the save's level.dat, the name Minecraft shows in its world
    /// list.
    /// </summary>
    /// <remarks>
    /// Keeps the file's compression and the previous copy as <c>level.dat_old</c>, as the game does.
    /// Throws <see cref="IOException"/> if Minecraft still has the file open, which the caller should
    /// report as "close the game first".
    /// </remarks>
    public static void SetLevelName(WorldInfo world, string newName)
    {
        var path = Path.Combine(world.FolderPath, "level.dat");
        var root = Nbt.ReadFile(path, out var compression)
            ?? throw new IOException("level.dat could not be read - the world may be open in Minecraft.");

        var data = root["Data"] ?? root;
        if (data["LevelName"] is { } existing) existing.StringValue = newName;
        else data.Children.Add(NbtTag.NewString("LevelName", newName));

        Nbt.WriteFile(path, root, compression);
        Invalidate(world.FolderPath);
    }

    /// <summary>
    /// Renames the save folder on disk and moves every launcher-side reference to the new key.
    /// </summary>
    /// <remarks>
    /// The settings key is <c>{packId:N}:{folderName}</c>, so a folder rename orphans the world's
    /// entry and drops it out of any folder it was filed into unless both are re-keyed here. Returns
    /// the new key.
    /// </remarks>
    public async Task<string> RenameFolderAsync(WorldInfo world, string newFolderName, CancellationToken ct = default)
    {
        var savesDir = Path.GetDirectoryName(world.FolderPath)!;
        var clean = SafeFolderName(newFolderName);
        if (string.Equals(clean, world.FolderName, StringComparison.OrdinalIgnoreCase)) return world.Key;
        var target = PathSafety.ResolveFileName(savesDir, clean)
            ?? throw new IOException($"'{clean}' cannot be used as a save folder name.");
        if (Directory.Exists(target))
            throw new IOException($"A save folder named '{clean}' already exists in this instance.");

        await Task.Run(() => Directory.Move(world.FolderPath, target), ct);
        Invalidate(world.FolderPath);

        var newKey = Key(world.SourcePackId, clean);
        if (settings.Worlds.Remove(world.Key, out var entry)) settings.Worlds[newKey] = entry;
        foreach (var members in settings.WorldFolders.Values)
        {
            var at = members.IndexOf(world.Key);
            if (at >= 0) members[at] = newKey;
        }
        settings.Save();
        return newKey;
    }

    // ── deleting ──

    /// <summary>Deletes the save folder from disk and forgets the world's launcher-side settings.</summary>
    public async Task DeleteAsync(WorldInfo world, CancellationToken ct = default)
    {
        await Task.Run(() => DeleteFolderInside(Path.GetDirectoryName(world.FolderPath)!, world.FolderPath), ct);
        Invalidate(world.FolderPath);
        Forget(world.Key);
    }

    // ── shared helpers ──

    /// <summary>True if this folder is a Minecraft save, i.e. it has a level.dat directly inside.</summary>
    public static bool IsWorldFolder(string dir) => File.Exists(Path.Combine(dir, "level.dat"));

    /// <summary>True if the archive holds a level.dat, at the root or one folder down.</summary>
    public static bool ZipContainsWorld(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            return zip.Entries.Any(entry =>
                entry.FullName.EndsWith("level.dat", StringComparison.OrdinalIgnoreCase)
                && entry.FullName.Count(c => c is '/' or '\\') <= 1);
        }
        catch { return false; }
    }

    /// <summary>A folder name inside <paramref name="savesDir"/> that is legal and not already taken.</summary>
    public static string UniqueFolderName(string savesDir, string name)
    {
        var clean = SafeFolderName(name);
        var candidate = clean;
        var i = 2;
        while (Directory.Exists(Path.Combine(savesDir, candidate)))
            candidate = $"{clean}-{i++}";
        return candidate;
    }

    /// <summary>A plain folder name for a save. Names from other people's worlds, level.dat and zip
    /// file names all come through here, so the result is always one name inside saves/.</summary>
    public static string SafeFolderName(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string((name ?? "world").Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim();
        if (clean.Length > 80) clean = clean[..80];
        // Windows drops trailing dots and spaces, so "." and ".." end up empty here.
        clean = clean.TrimEnd('.', ' ');
        if (string.IsNullOrEmpty(clean)) return "world";
        // A device name such as CON or NUL is kept readable but made into a plain name.
        return PathSafety.IsSafeFileName(clean) ? clean : "world-" + clean.Replace('.', '-');
    }

    /// <summary>
    /// Hoists a save out of its wrapper folder: if <paramref name="dir"/> holds nothing but one
    /// subfolder, that subfolder's contents move up. Shared by the zip import and the world browser,
    /// because both receive archives packed either way.
    /// </summary>
    public static void FlattenIfSingleSubdir(string dir)
    {
        if (File.Exists(Path.Combine(dir, "level.dat"))) return;
        var subs = Directory.GetDirectories(dir);
        var files = Directory.GetFiles(dir);
        if (subs.Length != 1 || files.Length != 0) return;

        var sub = subs[0];
        // A junction here points somewhere else on the disk; its contents are not the save's to move.
        if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) return;
        foreach (var f in Directory.GetFiles(sub))
            File.Move(f, Path.Combine(dir, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(sub))
            Directory.Move(d, Path.Combine(dir, Path.GetFileName(d)));
        DeleteFolderInside(dir, sub);
    }

    /// <summary>
    /// Deletes <paramref name="dir"/> and everything in it, but only when it lies below
    /// <paramref name="root"/> and no folder on the way down is a junction or link.
    /// </summary>
    /// <exception cref="IOException">The folder is not inside <paramref name="root"/>; nothing was
    /// deleted.</exception>
    private static void DeleteFolderInside(string root, string dir)
    {
        if (!PathSafety.IsInside(root, dir) || PathSafety.IsInside(dir, root) || PathSafety.CrossesLink(root, dir))
        {
            AppLog.Log(nameof(WorldService), $"Left {dir} in place: it is not a folder inside {root}.");
            throw new IOException($"Refused to delete {dir}: it is not a folder inside {root}.");
        }
        Directory.Delete(dir, recursive: true);
    }

    /// <summary>Drops the cached size and metadata for a save whose contents just changed.</summary>
    public static void Invalidate(string worldDir)
    {
        SizeCache.TryRemove(worldDir, out _);
        MetaCache.TryRemove(worldDir, out _);
    }

    /// <summary>
    /// Zips a directory with per-file progress.
    /// </summary>
    /// <remarks>
    /// <see cref="ZipFile.CreateFromDirectory(string,string)"/> reports no progress and opens files
    /// exclusively, so a world the game has open fails on <c>session.lock</c>. Writing entries by hand
    /// gives real progress, a prompt cancel, and backups while the game is running.
    /// </remarks>
    private static Task ZipDirectoryAsync(string sourceDir, string destZip, IProgress<double>? progress,
                                          CancellationToken ct) =>
        Task.Run(() =>
        {
            var dir = Path.GetDirectoryName(destZip);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(destZip)) File.Delete(destZip);

            var files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
            try
            {
                using var archive = ZipFile.Open(destZip, ZipArchiveMode.Create);
                for (var i = 0; i < files.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var rel = Path.GetRelativePath(sourceDir, files[i]).Replace('\\', '/');
                    if (IsLockFile(rel)) continue;
                    try
                    {
                        using var src = new FileStream(files[i], FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete);
                        var entry = archive.CreateEntry(rel, CompressionLevel.Fastest);
                        using var dst = entry.Open();
                        src.CopyTo(dst);
                    }
                    catch (IOException) { /* a file the game is rewriting right now; skip it */ }
                    progress?.Report((i + 1) / (double)files.Length);
                }
            }
            catch
            {
                try { if (File.Exists(destZip)) File.Delete(destZip); } catch { /* best effort */ }
                throw;
            }
        }, ct);

    /// <summary>Extracts an archive with per-entry progress, skipping entries that escape the target.</summary>
    /// <remarks>Archives can come from anywhere (stores, chat, drag and drop), so entries with
    /// <c>..\..\</c> paths are expected and skipped. The archive's size is checked before anything is
    /// written and each entry is held to its declared size, so a crafted zip can't fill the disk.</remarks>
    private static Task ExtractZipAsync(string zipPath, string targetDir, IProgress<double>? progress,
                                        CancellationToken ct) =>
        Task.Run(() =>
        {
            using var archive = ZipFile.OpenRead(zipPath);
            if (SafeZip.CheckLimits(archive.Entries) is { } why)
                throw new InvalidDataException($"Refused to unpack {Path.GetFileName(zipPath)}: {why}.");

            Directory.CreateDirectory(targetDir);
            var skipped = new List<string>();
            var total = archive.Entries.Count;
            for (var i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var entry = archive.Entries[i];
                var rel = entry.FullName.Replace('\\', '/');
                // Some zip tools write a leading "./", which names the same place.
                while (rel.StartsWith("./", StringComparison.Ordinal)) rel = rel[2..];
                var isFolder = rel.EndsWith('/');
                var destination = PathSafety.ResolveInside(targetDir, isFolder ? rel.TrimEnd('/') : rel);

                if (destination is null)
                {
                    if (rel.Trim('/').Length > 0) skipped.Add($"{entry.FullName} (unsafe path)");
                }
                else if (isFolder)
                {
                    Directory.CreateDirectory(destination);
                }
                else if (SafeZip.CheckEntry(entry) is { } bad)
                {
                    skipped.Add($"{entry.FullName} ({bad})");
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    SafeZip.ExtractToFile(entry, destination, overwrite: true);
                }
                progress?.Report((i + 1) / (double)total);
            }

            if (skipped.Count > 0)
                AppLog.Log(nameof(WorldService),
                    $"Left {skipped.Count} entr(ies) out of {Path.GetFileName(zipPath)}: " +
                    string.Join(", ", skipped.Take(10)) + (skipped.Count > 10 ? ", ..." : ""));
        }, ct);

    private static bool IsLockFile(string relativePath) =>
        Path.GetFileName(relativePath).Equals("session.lock", StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset SafeLastWriteTime(string dir)
    {
        try { return new FileInfo(Path.Combine(dir, "level.dat")).LastWriteTime; }
        catch { try { return new DirectoryInfo(dir).LastWriteTime; } catch { return DateTimeOffset.MinValue; } }
    }

    private static DateTime StampOf(string dir)
    {
        try
        {
            var levelDat = Path.Combine(dir, "level.dat");
            return File.Exists(levelDat) ? File.GetLastWriteTimeUtc(levelDat) : Directory.GetLastWriteTimeUtc(dir);
        }
        catch { return DateTime.MinValue; }
    }

    private static long DirectorySize(string dir, CancellationToken ct = default)
    {
        var stamp = StampOf(dir);
        if (SizeCache.TryGetValue(dir, out var cached) && cached.Stamp == stamp) return cached.Size;

        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                try { total += new FileInfo(f).Length; } catch { /* vanished */ }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { return 0; }

        SizeCache[dir] = (stamp, total);
        return total;
    }

    /// <summary>
    /// Reads a save's <c>level.dat</c>. Never throws; an unreadable or absent file yields
    /// <see cref="WorldMeta.Unknown"/> and the caller falls back to the folder name.
    /// </summary>
    public static WorldMeta ReadMeta(string worldDir)
    {
        var stamp = StampOf(worldDir);
        if (MetaCache.TryGetValue(worldDir, out var cached) && cached.Stamp == stamp) return cached.Meta;

        var meta = WorldMeta.Unknown;
        if (Nbt.ReadFile(Path.Combine(worldDir, "level.dat")) is { } root)
        {
            // Everything interesting lives under Data{}; a handful of very old saves put it at the root.
            var data = root["Data"] ?? root;
            meta = new WorldMeta(
                LevelName: data.Find("LevelName")?.AsString(),
                // Since 1.16 the seed is WorldGenSettings.seed; older saves keep RandomSeed at the top of Data.
                Seed: data["WorldGenSettings"]?["seed"]?.AsLong() ?? data.Find("RandomSeed")?.AsLong(),
                GameType: data.Find("GameType")?.AsInt(),
                Difficulty: data.Find("Difficulty")?.AsInt(),
                Hardcore: data.Find("hardcore")?.AsBool() ?? false,
                AllowCommands: data.Find("allowCommands")?.AsBool() ?? false,
                VersionName: data.Find("Version")?["Name"]?.AsString(),
                // Past year 9999 the conversion throws, and one crafted level.dat would then hide every
                // other save, so such a value counts as not recorded.
                LastPlayed: data.Find("LastPlayed")?.AsLong() is { } ms and > 0 and <= MaxUnixMilliseconds
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                    : null);
        }

        MetaCache[worldDir] = (stamp, meta);
        return meta;
    }

    /// <summary>The last instant <see cref="DateTimeOffset.FromUnixTimeMilliseconds"/> accepts,
    /// 9999-12-31 23:59:59.999 UTC.</summary>
    private const long MaxUnixMilliseconds = 253_402_300_799_999;
}

/// <summary>What the launcher knows about a save without opening the game.</summary>
/// <param name="Seed">The world seed, or null when level.dat did not carry one.</param>
/// <param name="GameType">0 survival, 1 creative, 2 adventure, 3 spectator.</param>
/// <param name="Difficulty">0 peaceful, 1 easy, 2 normal, 3 hard.</param>
/// <param name="AllowCommands">Whether cheats are on.</param>
/// <param name="VersionName">The Minecraft version that last wrote the save, e.g. "1.21.1".</param>
public sealed record WorldMeta(
    string? LevelName,
    long? Seed,
    int? GameType,
    int? Difficulty,
    bool Hardcore,
    bool AllowCommands,
    string? VersionName,
    DateTimeOffset? LastPlayed)
{
    public static readonly WorldMeta Unknown = new(null, null, null, null, false, false, null, null);

    public string GameModeLabel => GameType switch
    {
        0 => "Survival",
        1 => "Creative",
        2 => "Adventure",
        3 => "Spectator",
        _ => "Unknown"
    };

    public string DifficultyLabel => Difficulty switch
    {
        0 => "Peaceful",
        1 => "Easy",
        2 => "Normal",
        3 => "Hard",
        _ => "Unknown"
    };

    public string SeedLabel => Seed?.ToString() ?? "Not recorded";
    public string VersionLabel => string.IsNullOrWhiteSpace(VersionName) ? "Unknown" : VersionName;
    public bool HasSeed => Seed is not null;
}

/// <summary>One snapshot zip taken of a world.</summary>
public sealed record WorldBackup(string Path, string FileName, long SizeBytes, DateTimeOffset TakenAt);

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
    IReadOnlyList<Guid> CompatiblePackIds,
    WorldMeta? Meta = null)
{
    /// <summary>Never null, so callers can read <c>world.Info.Seed</c> without a guard.</summary>
    public WorldMeta Info => Meta ?? WorldMeta.Unknown;
}
