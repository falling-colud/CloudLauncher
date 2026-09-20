using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>
/// Scans pack <c>game/saves/</c> folders for Minecraft worlds and tracks which packs
/// may run each world. Storage is local — worlds don't sync to the server.
/// </summary>
/// <remarks>
/// Everything here that touches the disk in bulk (scanning, copying, zipping, restoring) is written
/// to be called from a background thread with a <see cref="CancellationToken"/>: a modded save is
/// routinely several gigabytes across tens of thousands of region and chunk files, and the Worlds
/// page used to do all of it on the dispatcher.
/// </remarks>
public sealed class WorldService(AppSettings settings, PackFolderService packs)
{
    public static string Key(Guid sourcePackId, string folderName) => $"{sourcePackId:N}:{folderName}";

    /// <summary>
    /// Folder sizes already measured, keyed by save folder path.
    /// </summary>
    /// <remarks>
    /// Walking every file of a big save costs seconds, and the page re-scans on every visit, every
    /// F5 and after every action. The stamp is the save's own last-write time, so the entry is
    /// thrown away the moment the world is played, imported into or restored — the only times its
    /// size can have changed — and a second visit to an untouched world is free.
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
            catch { /* missing folder etc — skip */ }
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

    // ── copying, importing, exporting ────────────────────────────────────────

    /// <summary>
    /// Copies a save folder. Progress is a 0–1 fraction of files copied.
    /// </summary>
    /// <remarks>
    /// The file list is enumerated up front so the fraction is real rather than a spinner, which is
    /// the whole point on a save whose copy takes a minute. <c>session.lock</c> is opened share-all
    /// and skipped if the running game still owns it — it is a zero-byte marker the game rewrites on
    /// load, so losing it costs nothing and failing the whole copy for it costs everything.
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
    /// Never delete the destination first. The callers hand this the same token their Cancel button
    /// holds, so a delete-then-copy left a half-written folder where the user's world had been the
    /// moment anyone cancelled mid-copy — the copy throws between two files and there is nothing to
    /// put back. The new copy is built in a temporary sibling instead and only swapped in once it is
    /// complete, and the old save is moved aside rather than deleted so that even a failed swap
    /// leaves one intact copy on disk. On cancellation or failure the temporary folder goes and the
    /// existing save is untouched.
    /// </remarks>
    public static async Task ReplaceWorldAsync(string src, string dst, IProgress<double>? progress = null,
                                               CancellationToken ct = default)
    {
        var stamp = DateTime.Now.ToString("HHmmssfff");
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
                try { Directory.Move(parked, dst); } catch { /* left as .replaced-… on disk */ }
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
    /// <remarks>Used for the temporary folders of a swap, where the swap has already decided which
    /// copy is the real one: failing to tidy up is never a reason to fail the operation, and never a
    /// reason to touch the copy that is now live.</remarks>
    private static async Task TryDeleteDirectoryAsync(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                await Task.Run(() => Directory.Delete(dir, recursive: true), CancellationToken.None);
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
    /// Worlds on the internet are zips, and about half of them wrap the save in one top-level folder,
    /// so the extract is followed by <see cref="FlattenIfSingleSubdir"/>. If no <c>level.dat</c> turns
    /// up after that the extracted folder is removed again and the caller is told — leaving a folder
    /// of stray files in saves/ is worse than refusing.
    /// </remarks>
    public async Task<string> ImportZipAsync(string zipPath, Guid packId, string packName, string? preferredName = null,
                                             IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var savesDir = SavesDir(packId, packName);
        Directory.CreateDirectory(savesDir);
        var baseName = SafeFolderName(preferredName ?? Path.GetFileNameWithoutExtension(zipPath));
        var folderName = UniqueFolderName(savesDir, baseName);
        var targetDir = Path.Combine(savesDir, folderName);

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
            try { if (Directory.Exists(targetDir)) Directory.Delete(targetDir, recursive: true); } catch { /* best effort */ }
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

    // ── backups ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Where a world's backups live: <c>&lt;pack root&gt;/backups/</c>, beside <c>game/</c> rather than
    /// inside it.
    /// </summary>
    /// <remarks>
    /// Inside the game folder the zips would be swept up by anything that walks the instance —
    /// manifests, syncs, an export of the pack — and would grow the instance by the size of the save
    /// every time someone took a snapshot. Beside it, they are still per-instance and still removed
    /// with the instance.
    /// </remarks>
    public string BackupsDir(Guid packId) => Path.Combine(packs.PackRoot(packId), "backups");

    /// <summary>Zips the save into the instance's backups folder. Returns the zip path.</summary>
    public async Task<string> BackupAsync(WorldInfo world, IProgress<double>? progress = null,
                                          CancellationToken ct = default)
    {
        var dir = BackupsDir(world.SourcePackId);
        Directory.CreateDirectory(dir);
        var zipPath = Path.Combine(dir, $"{SafeFolderName(world.FolderName)}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        await ZipDirectoryAsync(world.FolderPath, zipPath, progress, ct);
        return zipPath;
    }

    /// <summary>
    /// Backups taken of this world, newest first.
    /// </summary>
    /// <remarks>
    /// The match is the whole name, not just the prefix: every backup is
    /// <c>&lt;folder&gt;-&lt;timestamp&gt;.zip</c>, and a bare prefix test also swept up
    /// <c>MyWorld-copy-…</c> and <c>MyWorld-2-…</c> under <c>MyWorld</c>. Those are different saves,
    /// and restoring one of them here overwrote a world with a duplicate's contents.
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
    /// <remarks>This is what makes the backup match exact: whatever follows the folder name must be
    /// the timestamp and nothing else, so a longer folder name that merely starts the same way
    /// cannot claim this zip.</remarks>
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
    /// A safety backup of the current state is taken first, and the old folder is moved aside rather
    /// than deleted until the extract has succeeded — restoring the wrong snapshot is a mistake
    /// people make, and it must not also be the moment their current world disappears.
    /// <para>
    /// The extract succeeding is the commit point, and nothing after it may roll back. Removing the
    /// parked copy used to sit inside the same <c>try</c>, so a game still holding one of its files
    /// sent a perfectly good restore down the rollback path, which deletes the world that was just
    /// restored. Tidying up is now allowed to fail: the parked folder's path is returned instead so
    /// the caller can mention it, and the restored world is never touched again.
    /// </para>
    /// </remarks>
    /// <returns>The path of the parked copy of the old save if it could not be removed, else null.</returns>
    public async Task<string?> RestoreAsync(WorldInfo world, string zipPath, IProgress<double>? progress = null,
                                            CancellationToken ct = default)
    {
        await BackupAsync(world, null, ct);

        var parked = world.FolderPath + ".restoring-" + DateTime.Now.ToString("HHmmss");
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
            // Put the world back exactly as it was before doing anything else.
            try
            {
                if (Directory.Exists(world.FolderPath)) Directory.Delete(world.FolderPath, recursive: true);
                Directory.Move(parked, world.FolderPath);
            }
            catch { /* the parked copy is still on disk under its .restoring name */ }
            Invalidate(world.FolderPath);
            throw;
        }

        Invalidate(world.FolderPath);
        try
        {
            await Task.Run(() => Directory.Delete(parked, recursive: true), CancellationToken.None);
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

    // ── renaming ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes a new <c>LevelName</c> into the save's level.dat — the name Minecraft itself shows in
    /// the world list.
    /// </summary>
    /// <remarks>
    /// Without this, "rename" in the launcher only ever set a launcher-side label, so the world kept
    /// its old name everywhere that mattered. The file's original compression is preserved and the
    /// previous copy is kept as <c>level.dat_old</c>, exactly as the game does. Throws
    /// <see cref="IOException"/> if Minecraft still has the file open, which the caller should report
    /// as "close the game first" rather than as a failure the user can do nothing about.
    /// </remarks>
    public static void SetLevelName(WorldInfo world, string newName)
    {
        var path = Path.Combine(world.FolderPath, "level.dat");
        var root = Nbt.ReadFile(path, out var compression)
            ?? throw new IOException("level.dat could not be read — the world may be open in Minecraft.");

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
        var target = Path.Combine(savesDir, clean);
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

    // ── deleting ─────────────────────────────────────────────────────────────

    /// <summary>Deletes the save folder from disk and forgets the world's launcher-side settings.</summary>
    public async Task DeleteAsync(WorldInfo world, CancellationToken ct = default)
    {
        await Task.Run(() => Directory.Delete(world.FolderPath, recursive: true), ct);
        Invalidate(world.FolderPath);
        Forget(world.Key);
    }

    // ── shared helpers ───────────────────────────────────────────────────────

    /// <summary>True if this folder is a Minecraft save — i.e. it has a level.dat directly inside.</summary>
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

    public static string SafeFolderName(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string((name ?? "world").Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim();
        if (string.IsNullOrEmpty(clean)) clean = "world";
        if (clean.Length > 80) clean = clean[..80];
        return clean;
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
        foreach (var f in Directory.GetFiles(sub))
            File.Move(f, Path.Combine(dir, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(sub))
            Directory.Move(d, Path.Combine(dir, Path.GetFileName(d)));
        Directory.Delete(sub, recursive: true);
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
    /// <see cref="ZipFile.CreateFromDirectory(string,string)"/> would be one line, but it reports
    /// nothing and opens every file exclusively — so a world that Minecraft still has open fails the
    /// whole archive on <c>session.lock</c>. Writing the entries by hand costs twenty lines and buys
    /// a real progress fraction, a cancel that takes effect within one file, and a backup that can be
    /// taken while the game is running.
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
                    catch (IOException) { /* a file the game is rewriting right now — skip it */ }
                    progress?.Report((i + 1) / (double)files.Length);
                }
            }
            catch
            {
                try { if (File.Exists(destZip)) File.Delete(destZip); } catch { /* best effort */ }
                throw;
            }
        }, ct);

    /// <summary>Extracts an archive with per-entry progress, refusing entries that escape the target.</summary>
    /// <remarks>The path check is not paranoia: these archives come from CurseForge, from a friend's
    /// Discord, or from whatever the user dragged onto the page, and <c>..\..\</c> in an entry name is
    /// the oldest trick there is.</remarks>
    private static Task ExtractZipAsync(string zipPath, string targetDir, IProgress<double>? progress,
                                        CancellationToken ct) =>
        Task.Run(() =>
        {
            Directory.CreateDirectory(targetDir);
            // The trailing separator matters: without it "saves/world" would also accept a path that
            // resolved to "saves/world-elsewhere".
            var root = Path.GetFullPath(targetDir);
            if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;
            using var archive = ZipFile.OpenRead(zipPath);
            var total = archive.Entries.Count;
            for (var i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var entry = archive.Entries[i];
                var destination = Path.GetFullPath(Path.Combine(root, entry.FullName));
                if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Archive entry '{entry.FullName}' points outside the world folder.");

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destination);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination, overwrite: true);
                }
                progress?.Report((i + 1) / (double)total);
            }
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
                // Since 1.16 the seed moved into WorldGenSettings and lost its capital. Older saves
                // still carry RandomSeed at the top of Data, and a search by name finds neither on a
                // modern file — so both paths are spelled out here.
                Seed: data["WorldGenSettings"]?["seed"]?.AsLong() ?? data.Find("RandomSeed")?.AsLong(),
                GameType: data.Find("GameType")?.AsInt(),
                Difficulty: data.Find("Difficulty")?.AsInt(),
                Hardcore: data.Find("hardcore")?.AsBool() ?? false,
                AllowCommands: data.Find("allowCommands")?.AsBool() ?? false,
                VersionName: data.Find("Version")?["Name"]?.AsString(),
                LastPlayed: data.Find("LastPlayed")?.AsLong() is { } ms and > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                    : null);
        }

        MetaCache[worldDir] = (stamp, meta);
        return meta;
    }
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
