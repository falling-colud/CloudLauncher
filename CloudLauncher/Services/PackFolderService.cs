using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CloudLauncher.Shared;
using Microsoft.Win32.SafeHandles;

namespace CloudLauncher.Services;

/// <summary>What <see cref="PackFolderService.LinkOrCopyFile"/> ended up doing.</summary>
public enum FileLinkOutcome
{
    /// <summary>The destination was already this same file. Nothing was written.</summary>
    AlreadySameFile = 0,
    /// <summary>A hard link: one copy of the bytes, two directory entries.</summary>
    Linked,
    /// <summary>The filesystem refused a link (another volume, non-NTFS), so the bytes
    /// were copied.</summary>
    Copied,
    /// <summary>Something else was already in the way and overwriting was not allowed.</summary>
    Blocked
}

public sealed class PackFolderService(AppSettings settings, ApiClient api)
{
    private PackAssetService? _assets;
    public void SetPackAssets(PackAssetService assets) => _assets = assets;

    // Set via a setter like the ones below: the library is built on top of this service, so it cannot
    // be a constructor argument. Optional; when null, nothing is held back at the sync boundary.
    private ContentLibraryService? _library;
    public void SetLibrary(ContentLibraryService library) => _library = library;

    // Set via a setter because ModMetadataService depends on this service. Lets a sync drop the cached
    // mods.json after pulling a collaborator's newer copy, so our stale in-memory copy does not win.
    private ModMetadataService? _modMetadata;
    public void SetModMetadata(ModMetadataService metadata) => _modMetadata = metadata;

    /// <summary>Same cycle-breaking setter for the planning boards (plans.json lives beside
    /// mods.json).</summary>
    private ModPlanService? _modPlans;
    public void SetModPlans(ModPlanService plans) => _modPlans = plans;

    /// <summary>Resolves <paramref name="relative"/> under <paramref name="gameDir"/>, or returns
    /// null when it is not a plain relative path that stays inside the pack's game directory.
    /// Manifest paths come from whoever uploaded the shared pack, so they are checked here as well
    /// as in SyncController.</summary>
    private static string? SafeResolve(string gameDir, string? relative) =>
        PathSafety.ResolveInside(gameDir, relative);

    /// <summary>True for a lower- or upper-case hex SHA-256, the only hash the
    /// manifest carries.</summary>
    private static bool IsSha256Hex(string? hash) =>
        hash is { Length: 64 } && hash.All(Uri.IsHexDigit);

    /// <summary>A manifest path as it can safely appear in a log line.</summary>
    private static string Printable(string? path)
    {
        if (path is null) return "(none)";
        var s = new string(path.Select(c => char.IsControl(c) ? '?' : c).ToArray());
        return s.Length > 120 ? s[..120] + "..." : s;
    }

    // ── folder resolution ────────────────────────────────────────────────────

    // In-memory cache: packId -> absolute pack root path. Concurrent because PackRoot() is called
    // from the UI thread and from background workers (mod scans, manifest hashing, sync).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> _rootCache = new();

    /// <summary>
    /// Returns the pack root directory.
    /// Lookup order:
    ///   1. In-memory cache
    ///   2. Scan packs/ for a folder whose .packid matches the ID   (named folders)
    ///   3. Legacy fallback: folder named after the raw GUID       (old packs)
    /// If none found and <paramref name="name"/> is provided, creates a new named folder.
    /// </summary>
    public string PackRoot(Guid packId, string? name = null)
    {
        if (_rootCache.TryGetValue(packId, out var cached))
            return cached;

        // 1. Scan for a .packid file
        var found = FindFolderByPackId(packId);
        if (found != null)
        {
            _rootCache[packId] = found;
            return found;
        }

        // 2. Legacy: folder named after the GUID (packs created by older versions)
        var legacy = Path.Combine(settings.PacksRoot, packId.ToString("N"));
        if (Directory.Exists(legacy))
        {
            _rootCache[packId] = legacy;
            return legacy;
        }

        // 3. Create a new named folder
        if (name is null)
            throw new InvalidOperationException(
                $"Pack folder for {packId} not found and no name provided to create one.");

        var root = CreateNamedFolder(packId, name);
        return root;
    }

    /// <summary>Forgets every resolved pack root so the next lookup scans the current
    /// PacksRoot.</summary>
    /// <remarks>Call it when the instances folder changes, since cached paths point into the old
    /// one. Otherwise it only costs a rescan.</remarks>
    public void InvalidateRootCache() => _rootCache.Clear();

    /// <summary>Creates and registers a new named pack folder.</summary>
    public string CreateNamedFolder(Guid packId, string name)
    {
        Directory.CreateDirectory(settings.PacksRoot);
        var slug = Slugify(name);
        var path = Path.Combine(settings.PacksRoot, slug);

        // Handle name collisions by appending the first 8 hex chars of the ID
        if (Directory.Exists(path))
            path = Path.Combine(settings.PacksRoot, $"{slug}-{packId:N}"[..(slug.Length + 9)]);

        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, ".packid"), packId.ToString());
        _rootCache[packId] = path;
        return path;
    }

    /// <summary>Renames the on-disk pack folder to match a new instance name. Cosmetic only, since
    /// folders are found by their <c>.packid</c> marker. Best effort: if the folder is locked
    /// (instance running) or the name is taken, the old folder stays. Returns the resulting
    /// absolute root path.</summary>
    public string TryRenameFolder(Guid packId, string newName)
    {
        string current;
        try { current = PackRoot(packId); }
        catch { return ""; } // no folder exists yet, nothing to rename

        var parent = settings.PacksRoot;
        var slug = Slugify(newName);
        var target = Path.Combine(parent, slug);

        // Already named correctly; just make sure the marker file is present.
        if (string.Equals(Path.GetFullPath(current), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            EnsurePackIdFile(current, packId);
            return current;
        }

        // Collision with another instance's folder: add a short id suffix (as CreateNamedFolder does).
        if (Directory.Exists(target))
            target = Path.Combine(parent, $"{slug}-{packId:N}"[..(slug.Length + 9)]);

        try
        {
            Directory.CreateDirectory(parent);
            Directory.Move(current, target);
        }
        catch (IOException) { return current; }                 // locked (running instance), keep old name
        catch (UnauthorizedAccessException) { return current; }

        EnsurePackIdFile(target, packId);
        _rootCache[packId] = target;
        return target;
    }

    private static void EnsurePackIdFile(string root, Guid packId)
    {
        var idFile = Path.Combine(root, ".packid");
        if (!File.Exists(idFile))
            File.WriteAllText(idFile, packId.ToString());
    }

    private string? FindFolderByPackId(Guid packId)
    {
        if (!Directory.Exists(settings.PacksRoot)) return null;
        foreach (var dir in Directory.EnumerateDirectories(settings.PacksRoot))
        {
            var idFile = Path.Combine(dir, ".packid");
            if (!File.Exists(idFile)) continue;
            if (Guid.TryParse(File.ReadAllText(idFile).Trim(), out var id) && id == packId)
                return dir;
        }
        return null;
    }

    /// <summary>Converts a pack name to a safe, lowercase folder name slug.</summary>
    public static string Slugify(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => invalid.Contains(c) || c == '\\' || c == '/' ? '-' : c).ToArray());
        s = Regex.Replace(s, @"\s+", "-");
        s = Regex.Replace(s, @"-{2,}", "-");
        // Pack names come from whoever owns the pack. Win32 drops trailing dots, so "." and ".." end up
        // empty here, and a device name such as "con" is kept readable but made into a plain name.
        s = s.Trim('-').ToLowerInvariant().TrimEnd('.');
        if (string.IsNullOrEmpty(s)) return "pack";
        return PathSafety.IsSafeFileName(s) ? s : "pack-" + s.Replace('.', '-');
    }

    // ── sub-directory helpers ────────────────────────────────────────────────

    public string GameDir(Guid packId)   => Path.Combine(PackRoot(packId), "game");
    public string LocalDir(Guid packId)  => Path.Combine(PackRoot(packId), "local");

    // Legacy, kept for migration. Not a sync source.
    public string SharedDir(Guid packId) => Path.Combine(PackRoot(packId), "shared");

    /// <summary>Where a dedicated server for this pack actually runs: the mirror of game/, the
    /// loader's installed libraries, server.properties, the world, ops/whitelist/bans.</summary>
    public string ServerRunDir(Guid packId) => Path.Combine(PackRoot(packId), "server-run");

    /// <summary>The user's server-only files, laid over the mirror after every sync so a
    /// server-specific config (or a server-only mod) wins over the client's copy and survives the
    /// next mirror.</summary>
    public string ServerOverrideDir(Guid packId) => Path.Combine(PackRoot(packId), "server");

    // name-aware overloads (used when creating a new pack)
    public string GameDir(Guid packId, string name)   => Path.Combine(PackRoot(packId, name), "game");
    public string LocalDir(Guid packId, string name)  => Path.Combine(PackRoot(packId, name), "local");
    public string SharedDir(Guid packId, string name) => Path.Combine(PackRoot(packId, name), "shared");

    // ── folder creation ──────────────────────────────────────────────────────

    private static readonly string[] DefaultGameSubfolders =
    [
        "mods",
        "config",
        "resourcepacks",
        "saves",
        "shaderpacks",
        "screenshots",
    ];

    public void EnsurePackFolder(Guid packId, string name, bool shared)
    {
        var gameDir = GameDir(packId, name);
        Directory.CreateDirectory(gameDir);
        foreach (var sub in DefaultGameSubfolders)
            Directory.CreateDirectory(Path.Combine(gameDir, sub));
    }

    // Overload for callers that already know the folder exists (no name needed)
    public void EnsurePackFolder(Guid packId, bool shared)
    {
        Directory.CreateDirectory(GameDir(packId));
    }

    // No-op: the shared/ folder is unused; sync goes directly to and from game/.
    public void EnsureSharedFolders(Guid packId) { }

    // ── file listing ─────────────────────────────────────────────────────────

    public IReadOnlyList<string> ListRelativeFiles(string root)
    {
        if (!Directory.Exists(root)) return Array.Empty<string>();
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            // Hide the .packid metadata file from the UI
            .Where(p => !string.Equals(Path.GetFileName(p), ".packid", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
    }

    public List<ManifestEntry> ComputeManifest(string root)
    {
        var entries = new List<ManifestEntry>();
        if (!Directory.Exists(root)) return entries;
        foreach (var rel in ListRelativeFiles(root))
        {
            var abs = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            using var fs = File.OpenRead(abs);
            var hash = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
            entries.Add(new ManifestEntry(rel, hash, new FileInfo(abs).Length));
        }
        return entries;
    }

    // ── file moves ───────────────────────────────────────────────────────────

    public void MoveFileBetweenFolders(string sourceRoot, string destRoot, string relativePath)
    {
        var (src, dst) = ResolveTransfer(sourceRoot, destRoot, relativePath);
        if (!File.Exists(src)) throw new FileNotFoundException(src);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        if (File.Exists(dst)) File.Delete(dst);
        File.Move(src, dst);
    }

    public void CopyFileBetweenFolders(string sourceRoot, string destRoot, string relativePath)
    {
        var (src, dst) = ResolveTransfer(sourceRoot, destRoot, relativePath);
        if (!File.Exists(src)) throw new FileNotFoundException(src);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Copy(src, dst, overwrite: true);
    }

    /// <summary>Both ends of a move or copy, each held inside its own root.</summary>
    private static (string Source, string Destination) ResolveTransfer(string sourceRoot, string destRoot, string relativePath)
    {
        var src = PathSafety.ResolveInside(sourceRoot, relativePath);
        var dst = PathSafety.ResolveInside(destRoot, relativePath);
        if (src is null || dst is null)
            throw new InvalidOperationException($"'{Printable(relativePath)}' is not a path inside the instance folder.");
        return (src, dst);
    }

    /// <summary>
    /// Overlays local/ files into game/ so Minecraft can see per-user additions at launch.
    /// Shared files are already in game/ (synced there directly), so no shared/ overlay needed.
    /// </summary>
    public void PrepareLaunchOverlay(Guid packId)
    {
        var gameDir = GameDir(packId);
        Directory.CreateDirectory(gameDir);

        var localDir = LocalDir(packId);
        if (Directory.Exists(localDir))
            OverlayFilesIntoGame(localDir, gameDir, overwriteExisting: false);
    }

    private static void OverlayFilesIntoGame(string sourceRoot, string gameRoot, bool overwriteExisting) =>
        LinkOrCopyTree(sourceRoot, gameRoot, overwriteExisting);

    /// <summary>
    /// Puts <paramref name="existingPath"/> at <paramref name="linkPath"/>, sharing the bytes when
    /// the filesystem allows it and copying them when it does not.
    /// </summary>
    /// <param name="overwriteExisting">False leaves anything already at the destination alone and
    /// reports <see cref="FileLinkOutcome.Blocked"/>. The launch overlay uses this so an instance's
    /// own file wins.</param>
    /// <param name="preferHardLink">False forces a real copy, for callers that know a link is wrong
    /// here (a file the user will edit in one place only, for example).</param>
    public static FileLinkOutcome LinkOrCopyFile(
        string existingPath, string linkPath, bool overwriteExisting = false, bool preferHardLink = true)
    {
        if (File.Exists(linkPath))
        {
            if (PathsReferToSameFile(existingPath, linkPath)) return FileLinkOutcome.AlreadySameFile;
            if (!overwriteExisting) return FileLinkOutcome.Blocked;
            File.Delete(linkPath);
        }
        else if (Directory.Exists(linkPath))
        {
            // A folder with the file's name is in the way. Copying onto it would throw, so treat it
            // like any other file already there.
            if (!overwriteExisting) return FileLinkOutcome.Blocked;
            Directory.Delete(linkPath, recursive: true);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        if (preferHardLink && TryCreateHardLink(linkPath, existingPath)) return FileLinkOutcome.Linked;
        File.Copy(existingPath, linkPath, overwrite: true);
        return FileLinkOutcome.Copied;
    }

    /// <summary>Recursively <see cref="LinkOrCopyFile"/>s a whole tree, for the content that
    /// is a folder rather than a zip (an unpacked resource or shader pack) and for the
    /// launch overlay.</summary>
    public static (int Linked, int Copied, int Skipped) LinkOrCopyTree(
        string sourceRoot, string destRoot, bool overwriteExisting = false, bool preferHardLink = true)
    {
        int linked = 0, copied = 0, skipped = 0;
        foreach (var srcFile in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var destFile = Path.Combine(destRoot, Path.GetRelativePath(sourceRoot, srcFile));
            switch (LinkOrCopyFile(srcFile, destFile, overwriteExisting, preferHardLink))
            {
                case FileLinkOutcome.Linked: linked++; break;
                case FileLinkOutcome.Copied: copied++; break;
                default: skipped++; break;
            }
        }
        return (linked, copied, skipped);
    }

    /// <summary>Creates a hard link, or returns false when the filesystem will not have one
    /// (another volume, a non-NTFS drive, not Windows). Unlike symlinks, hard links need no
    /// elevation.</summary>
    public static bool TryCreateHardLink(string linkPath, string existingPath)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        return CreateHardLinkW(linkPath, existingPath, IntPtr.Zero);
    }

    [DllImport("Kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    /// <summary>
    /// Whether two paths are the same on-disk content: the same file, or for an unpacked pack
    /// folder, a sampled match.
    /// </summary>
    /// <remarks>For folders it compares the file count and the first file (ordinal by relative
    /// path), which is enough to recognise a folder this launcher linked. It is not proof of
    /// equality, so never rely on it before deleting something the user may have edited.</remarks>
    public static bool EntriesReferToSameContent(string pathA, string pathB)
    {
        if (File.Exists(pathA) || File.Exists(pathB))
            return PathsReferToSameFile(pathA, pathB);

        if (!Directory.Exists(pathA) || !Directory.Exists(pathB)) return false;

        try
        {
            var a = Directory.EnumerateFiles(pathA, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(pathA, f)).OrderBy(s => s, StringComparer.Ordinal).ToList();
            var b = Directory.EnumerateFiles(pathB, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(pathB, f)).OrderBy(s => s, StringComparer.Ordinal).ToList();
            if (a.Count != b.Count) return false;
            if (a.Count == 0) return true;
            if (!string.Equals(a[0], b[0], StringComparison.OrdinalIgnoreCase)) return false;
            return PathsReferToSameFile(Path.Combine(pathA, a[0]), Path.Combine(pathB, b[0]));
        }
        catch { return false; }
    }

    /// <summary>True when two paths are two names for one file (same volume serial and file index,
    /// i.e. a hard link). No hashing and no false positives from matching metadata.</summary>
    public static bool PathsReferToSameFile(string pathA, string pathB)
    {
        if (!File.Exists(pathA) || !File.Exists(pathB))
            return false;

        if (string.Equals(Path.GetFullPath(pathA), Path.GetFullPath(pathB), StringComparison.OrdinalIgnoreCase))
            return true;

        // On Windows compare the underlying file IDs (volume serial + file index).
        if (OperatingSystem.IsWindows())
            return TryGetFileId(pathA, out var idA) && TryGetFileId(pathB, out var idB) && idA == idB;

        // Non-Windows fallback: compare size + last-write time.
        try
        {
            var infoA = new FileInfo(pathA);
            var infoB = new FileInfo(pathB);
            return infoA.Length == infoB.Length
                   && infoA.LastWriteTimeUtc == infoB.LastWriteTimeUtc;
        }
        catch { return false; }
    }

    private static bool TryGetFileId(string path, out (uint volume, ulong index) id)
    {
        id = default;
        using var handle = CreateFileW(
            path,
            0,                          // GENERIC_READ not required; 0 = query only
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            FileAttributes.Normal | (FileAttributes)0x02000000, // FILE_FLAG_BACKUP_SEMANTICS
            IntPtr.Zero);

        if (handle.IsInvalid) return false;

        if (!GetFileInformationByHandle(handle, out var info)) return false;

        id = (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition,
        FileAttributes dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out ByHandleFileInformation lpFileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    // ── sync manifest lock file ───────────────────────────────────────────────

    /// <summary>
    /// Returns the path to the local file that records which relative paths were last
    /// downloaded from the server. Used to safely prune stale server-managed files from
    /// game/ without touching the user's own local files.
    /// </summary>
    private string SyncManifestLockPath(Guid packId) =>
        Path.Combine(PackRoot(packId), ".sync-manifest.json");

    private HashSet<string> LoadSyncManifestLock(Guid packId)
    {
        try
        {
            var path = SyncManifestLockPath(packId);
            if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
            var lines = System.Text.Json.JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path));
            return new HashSet<string>(lines ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private void SaveSyncManifestLock(Guid packId, IEnumerable<string> paths)
    {
        var path = SyncManifestLockPath(packId);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(paths.ToList()));
    }

    // ── sync ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Uploads the files in game/ listed in <paramref name="sharedPaths"/> (the relative paths
    /// matching "shared" rules).
    /// </summary>
    /// <returns>The new manifest version the server assigned at commit, so callers can record it
    /// without fetching the manifest again.</returns>
    public async Task<long> UploadSharedAsync(Guid packId, long baseVersion, IReadOnlyCollection<string> sharedPaths, IProgress<string>? log, CancellationToken ct = default)
    {
        void Report(string m) { log?.Report(m); AppLog.Log("upload", m); }

        var gameDir = GameDir(packId);
        ProgressHub.Indeterminate(packId, "Building manifest...");

        // Private assets never leave this machine, whatever the rules say (see PrivateAssetPolicy).
        // Done here so no upload path can skip it, and reported so the owner can see what was left out.
        var (publicPaths, privatePaths) = PrivateAssetPolicy.Partition(sharedPaths, settings);
        if (privatePaths.Count > 0)
            Report($"Kept private on this machine (not uploaded): {PrivateAssetPolicy.Describe(privatePaths, settings)}");
        sharedPaths = publicPaths;

        // Also hold back library items marked "keep on this machine". The launch overlay puts
        // library files into game/, where resourcepacks/ and shaderpacks/ are shared by default. A
        // rule pattern cannot tell them from the instance's own packs, so the test is file identity.
        if (_library is { } library)
        {
            var keptLocal = new List<string>();
            var stillShared = new List<string>();
            foreach (var rel in publicPaths)
                (library.IsKeepLocalPath(gameDir, rel) ? keptLocal : stillShared).Add(rel);
            if (keptLocal.Count > 0)
            {
                Report($"Kept on this machine (shared copy, not uploaded): {ContentLibraryService.Describe(keptLocal)}");
                sharedPaths = stillShared;
            }
        }

        // Manifest names differ from local paths in one case: a jar low mode renamed to
        // .jar.disabled is shared under its enabled name, so this machine's low mode does not
        // spread to other players. Hand-disabled jars keep their name. Config content is masked the
        // same way: a file low mode changed is uploaded with the player's pre-low-mode values. Null
        // means nothing is masked and the file is uploaded as is.
        var entryPaths = await Task.Run(() =>
        {
            var result = new List<(ManifestEntry Entry, string Abs, byte[]? Masked)>();
            foreach (var rel in sharedPaths)
            {
                var abs = SafeResolve(gameDir, rel);
                if (abs is null)
                {
                    Report($"Skipped (not a path inside game/): {Printable(rel)}");
                    continue;
                }
                if (!File.Exists(abs)) continue;

                var masked = LowModeService.SharedContentFor(gameDir, rel);
                string hash;
                long length;
                if (masked is not null)
                {
                    hash = Convert.ToHexString(SHA256.HashData(masked)).ToLowerInvariant();
                    length = masked.LongLength;
                }
                else
                {
                    using var fs = File.OpenRead(abs);
                    hash = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
                    length = new FileInfo(abs).Length;
                }

                var manifestRel = LowModeService.UploadNameFor(rel);
                result.Add((new ManifestEntry(manifestRel, hash, length), abs, masked));
            }
            return result;
        }, ct);
        var entries = entryPaths.Select(p => p.Entry).ToList();

        Report($"game/ has {entries.Count} shared file(s) to sync");

        var begin = await api.BeginUploadAsync(packId, new BeginUploadRequest(baseVersion, entries), ct);
        Report($"Server says {begin.MissingHashes.Count} blob(s) need upload");

        var missingSet = new HashSet<string>(begin.MissingHashes, StringComparer.OrdinalIgnoreCase);
        var byHash = entryPaths
            .GroupBy(p => p.Entry.Hash, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var i = 0;
        foreach (var hash in missingSet)
        {
            ct.ThrowIfCancellationRequested();
            // Skip a blob the server asks for that we have no local path for, rather
            // than aborting the sync.
            if (!byHash.TryGetValue(hash, out var pair)) continue;
            i++;
            Report($"Uploading {i}/{missingSet.Count}: {pair.Entry.RelativePath}");
            ProgressHub.Report(packId, missingSet.Count == 0 ? 1 : (double)i / missingSet.Count,
                $"Uploading {i}/{missingSet.Count}: {pair.Entry.RelativePath}");
            // Upload the masked bytes when there are any, so the blob matches the hash in the manifest.
            await using Stream body = pair.Masked is not null
                ? new MemoryStream(pair.Masked, writable: false)
                : File.OpenRead(pair.Abs);
            await api.UploadBlobAsync(packId, hash, body, ct);
        }

        ProgressHub.Indeterminate(packId, "Committing...");
        var commit = await api.CommitUploadAsync(packId, new BeginUploadRequest(baseVersion, entries), ct);
        Report($"Committed {entries.Count} file(s). New manifest version: {commit.NewVersion}");
        SaveSyncManifestLock(packId, entries.Select(e => e.RelativePath));
        ProgressHub.Clear(packId);
        return commit.NewVersion;
    }

    /// <param name="job">
    /// The pausable job this download belongs to, when one is driving it. Stopping it rolls back
    /// the files that were not here before; files it replaced keep their new, hash-verified copy.
    /// </param>
    public async Task DownloadSharedAsync(
        Guid packId, IProgress<string>? log, CancellationToken ct = default, PackJob? job = null)
    {
        void Report(string m) { log?.Report(m); AppLog.Log("download", m); }

        var gameDir = GameDir(packId);
        Directory.CreateDirectory(gameDir);
        ProgressHub.Indeterminate(packId, "Fetching manifest...");
        var manifest = await api.GetManifestAsync(packId, ct);
        Report($"Server manifest has {manifest.Entries.Count} files at version {manifest.Version}");

        // Every path and hash in the manifest comes from whoever last uploaded the pack. An entry that
        // is not a plain relative path inside game/, or whose hash is not a SHA-256, is dropped here,
        // before any other check reads it, so neither the download loop nor the prune below sees it.
        bool Usable(ManifestEntry? e) => e is not null && SafeResolve(gameDir, e.RelativePath) is not null && IsSha256Hex(e.Hash);
        var unsafeEntries = manifest.Entries.Where(e => !Usable(e)).ToList();
        if (unsafeEntries.Count > 0)
        {
            var sample = string.Join(", ", unsafeEntries.Take(5).Select(e => Printable(e?.RelativePath)));
            Report($"Skipped {unsafeEntries.Count} manifest entr(ies) with an unsafe path or hash: {sample}");
            manifest = manifest with { Entries = manifest.Entries.Where(Usable).ToList() };
        }

        // A manifest can still carry private paths (an upload from before the policy existed, or a
        // collaborator's copy). They must not overwrite the real files here, so they are dropped
        // from the working set before the download loop and the prune.
        var privateOnServer = manifest.Entries.Where(e => PrivateAssetPolicy.IsPrivate(e.RelativePath, settings)).ToList();
        if (privateOnServer.Count > 0)
        {
            Report($"Ignoring private path(s) from the server manifest: {PrivateAssetPolicy.Describe(privateOnServer.Select(e => e.RelativePath).ToList(), settings)}");
            manifest = manifest with { Entries = manifest.Entries.Where(e => !PrivateAssetPolicy.IsPrivate(e.RelativePath, settings)).ToList() };
        }

        // Same identity test as the upload side. A collaborator's same-named pack would replace
        // the directory entry via File.Move below, and the instance would stop using the
        // library file unnoticed.
        if (_library is { } keepLibrary)
        {
            var kept = manifest.Entries.Where(e => keepLibrary.IsKeepLocalPath(gameDir, e.RelativePath)).ToList();
            if (kept.Count > 0)
            {
                Report($"Ignoring server copies of files kept on this machine: {ContentLibraryService.Describe(kept.Select(e => e.RelativePath).ToList())}");
                var keptPaths = new HashSet<string>(kept.Select(e => e.RelativePath), StringComparer.OrdinalIgnoreCase);
                manifest = manifest with { Entries = manifest.Entries.Where(e => !keptPaths.Contains(e.RelativePath)).ToList() };
            }
        }

        // The owner's private files are protected from the prune below; on other machines a private
        // path from an earlier sync is pruned once the server stops listing it. If the pack cannot
        // be fetched, keep files.
        bool protectPrivate;
        try
        {
            var packInfo = await api.GetPackAsync(packId, ct);
            protectPrivate = settings.UserId is Guid me && packInfo.OwnerId == me;
        }
        catch (OperationCanceledException) { throw; }
        catch { protectPrivate = true; }

        // Compare against currently present game/ files
        var serverPaths = new HashSet<string>(manifest.Entries.Select(e => e.RelativePath), StringComparer.OrdinalIgnoreCase);
        var localByPath = await Task.Run(() =>
        {
            // Built by hand rather than ToDictionary: two entries differing only in case are legal
            // on the Linux server but would throw here and break sync on every Windows client.
            var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in manifest.Entries)
            {
                if (map.ContainsKey(e.RelativePath)) continue;
                var abs = SafeResolve(gameDir, e.RelativePath);
                if (!File.Exists(abs)) { map[e.RelativePath] = null; continue; }
                using var fs = File.OpenRead(abs);
                map[e.RelativePath] = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
            }
            return map;
        }, ct);

        var total = manifest.Entries.Count;
        var i = 0;
        // Files this download replaced or brought in, so the Config pages take them as the new
        // original rather than as local edits (see ConfigEditTracker.ForgetSynced).
        var replaced = new List<string>();
        foreach (var e in manifest.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (job is not null) await job.Gate.WaitAsync(ct);
            i++;
            if (localByPath.TryGetValue(e.RelativePath, out var localHash) && localHash == e.Hash) continue;
            var abs = SafeResolve(gameDir, e.RelativePath);
            if (abs is null) continue;   // already filtered out above
            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
            Report($"Downloading {i}/{total}: {e.RelativePath}");
            ProgressHub.Report(packId,
                total == 0 ? 1 : (double)(i - 1) / total,
                $"Downloading {i}/{total}: {e.RelativePath}",
                -1,
                e.RelativePath);

            // Download to a temp file, verify its hash, then atomically replace the real file, so a
            // dropped connection or cancel never leaves a half-written file in place of the good copy.
            var tmp = abs + ".cldownload";
            try
            {
                await using (var src = await api.DownloadBlobAsync(packId, e.Hash, ct))
                await using (var dst = File.Create(tmp))
                {
                    await CopyWithProgressAsync(src, dst, e.Size, currentFraction =>
                    {
                        var overall = total == 0 ? 1 : ((i - 1) + currentFraction) / total;
                        ProgressHub.Report(packId,
                            overall,
                            $"Downloading {i}/{total}: {e.RelativePath}",
                            currentFraction,
                            e.RelativePath);
                    }, ct, job?.Gate);
                }

                string actualHash;
                await using (var vfs = File.OpenRead(tmp))
                    actualHash = Convert.ToHexString(await SHA256.HashDataAsync(vfs, ct)).ToLowerInvariant();
                if (!string.Equals(actualHash, e.Hash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException(
                        $"Downloaded '{e.RelativePath}' failed integrity check (expected {e.Hash}, got {actualHash}).");

                File.Move(tmp, abs, overwrite: true);
                replaced.Add(e.RelativePath);
                // A path that had no local copy is one this download brought in, so a stop can
                // take it back out. A replaced file stays: its new copy is complete and verified.
                if (localHash is null) job?.TrackCreatedFile(abs);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
                throw;
            }

            ProgressHub.Report(packId,
                total == 0 ? 1 : (double)i / total,
                $"Downloaded {i}/{total}: {e.RelativePath}",
                1,
                e.RelativePath);
        }

        // Remove game/ files that were previously server-managed but are no longer on the server.
        // We only remove files we know came from the server (tracked in the sync lock file)
        // to avoid deleting the user's own local files.
        var previousServerPaths = LoadSyncManifestLock(packId);
        foreach (var rel in previousServerPaths)
        {
            if (serverPaths.Contains(rel)) continue;
            // Guard the delete sink too: the sync-lock list was itself populated from server-supplied
            // paths, so each one is resolved again here and anything that is not a plain path inside
            // game/ is left alone.
            var abs = SafeResolve(gameDir, rel);
            if (abs is null) continue;
            // Never prune the owner's private paths: the lock may list them from an upload made
            // before the policy existed, and the policy is what keeps them off the server.
            // Elsewhere they are pruned like any other file.
            if (protectPrivate && PrivateAssetPolicy.IsPrivate(rel, settings)) continue;
            // Library items kept on this machine are never pruned: that setting keeps them off the
            // server, and pruning would remove the pack from an instance it was applied to.
            if (_library?.IsKeepLocalPath(gameDir, rel) == true) continue;
            if (!File.Exists(abs)) continue;
            // A junction or symbolic link partway down can point anywhere, so nothing reached through
            // one is deleted.
            if (PathSafety.CrossesLink(gameDir, abs))
            {
                Report($"Kept (inside a linked folder): {Printable(rel)}");
                continue;
            }
            File.Delete(abs);
            Report($"Removed (no longer on server): {rel}");
        }

        SaveSyncManifestLock(packId, manifest.Entries.Select(e => e.RelativePath));
        ConfigEditTracker.ForgetSynced(PackRoot(packId), replaced);
        // Drop the cached mods.json / plans.json so the synced flags, categories and planning boards
        // are read from disk next time instead of being overwritten by stale in-memory copies.
        _modMetadata?.Invalidate(packId);
        _modPlans?.Invalidate(packId);
        Report("Done.");
        ProgressHub.Clear(packId);
        settings.PackSyncedVersion[packId] = manifest.Version;
        settings.Save();
        _assets?.ApplyFromSharedFolder(packId);
    }

    private static async Task CopyWithProgressAsync(
        Stream source,
        Stream destination,
        long totalBytes,
        Action<double> progress,
        CancellationToken ct,
        PauseGate? pause = null)
    {
        if (totalBytes <= 0)
        {
            await source.CopyToAsync(destination, ct);
            return;
        }

        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            progress(Math.Min(1.0, done / (double)totalBytes));
            // Checked mid-file, so pausing during one large file stops the transfer.
            if (pause is { IsPaused: true }) await pause.WaitAsync(ct);
        }
    }
}
