using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CloudLauncher.Shared;
using Microsoft.Win32.SafeHandles;

namespace CloudLauncher.Services;

public sealed class PackFolderService(AppSettings settings, ApiClient api)
{
    private PackAssetService? _assets;
    public void SetPackAssets(PackAssetService assets) => _assets = assets;

    // Set via setter (not ctor) because ModMetadataService already depends on this service —
    // a ctor dependency would be a cycle. Used to drop the cached mods.json after a sync pulls a
    // collaborator's newer copy, so their flags aren't shadowed by our stale in-memory doc.
    private ModMetadataService? _modMetadata;
    public void SetModMetadata(ModMetadataService metadata) => _modMetadata = metadata;

    /// <summary>Same cycle-breaking setter for the planning boards (plans.json lives beside mods.json).</summary>
    private ModPlanService? _modPlans;
    public void SetModPlans(ModPlanService plans) => _modPlans = plans;

    /// <summary>Resolves <paramref name="relative"/> under <paramref name="gameDir"/> and
    /// guarantees the result stays inside the pack's game directory. Manifest relative paths
    /// originate from whoever uploaded the shared pack and are untrusted; without this guard a
    /// crafted <c>..</c>/rooted path would let a malicious manifest write or delete files
    /// outside the pack folder (zip-slip / arbitrary file write) on every subscriber that
    /// syncs. Throws when the path escapes containment. Defense-in-depth alongside the
    /// server-side validation in SyncController.</summary>
    private static string SafeResolve(string gameDir, string relative)
    {
        var rootFull = Path.GetFullPath(gameDir);
        var full = Path.GetFullPath(Path.Combine(rootFull, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(full, rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Sync entry escapes the pack folder and was rejected: '{relative}'");
        return full;
    }

    // ── folder resolution ────────────────────────────────────────────────────

    // In-memory cache: packId → absolute pack root path.
    // ConcurrentDictionary because PackRoot() is called from the UI thread and from Task.Run
    // workers (mod inventory scans, manifest hashing, sync) — a plain Dictionary can corrupt
    // or throw under concurrent writes.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> _rootCache = new();

    /// <summary>
    /// Returns the pack root directory.
    /// Lookup order:
    ///   1. In-memory cache
    ///   2. Scan packs/ for a folder whose .packid matches the ID   (named folders)
    ///   3. Legacy fallback — folder named after the raw GUID        (old packs)
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

        // 2. Legacy: folder named after the GUID (e.g. existing packs before this change)
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

    /// <summary>Renames the on-disk pack folder to match a new instance name. Folders are
    /// located by their <c>.packid</c> marker, so this is purely cosmetic — keeping the folder
    /// name in step with the instance name. It is best-effort: if the folder can't be moved
    /// (e.g. it's locked because the instance is running, or a same-named folder already
    /// exists for another instance), the old folder is left in place and the new name still
    /// applies server-side. Returns the resulting absolute root path. Renaming within the same
    /// PacksRoot is a same-volume metadata rename, so it is fast and synchronous.</summary>
    public string TryRenameFolder(Guid packId, string newName)
    {
        string current;
        try { current = PackRoot(packId); }
        catch { return ""; } // no folder exists yet — nothing to rename

        var parent = settings.PacksRoot;
        var slug = Slugify(newName);
        var target = Path.Combine(parent, slug);

        // Already named correctly — just make sure the marker file is present.
        if (string.Equals(Path.GetFullPath(current), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            EnsurePackIdFile(current, packId);
            return current;
        }

        // Collision with a different instance's folder — disambiguate with a short id suffix
        // (mirrors CreateNamedFolder).
        if (Directory.Exists(target))
            target = Path.Combine(parent, $"{slug}-{packId:N}"[..(slug.Length + 9)]);

        try
        {
            Directory.CreateDirectory(parent);
            Directory.Move(current, target);
        }
        catch (IOException) { return current; }                 // locked (running instance) — keep old name
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
        s = s.Trim('-').ToLowerInvariant();
        return string.IsNullOrEmpty(s) ? "pack" : s;
    }

    // ── sub-directory helpers ────────────────────────────────────────────────

    public string GameDir(Guid packId)   => Path.Combine(PackRoot(packId), "game");
    public string LocalDir(Guid packId)  => Path.Combine(PackRoot(packId), "local");

    // Kept for migration/legacy — no longer used as a sync source.
    public string SharedDir(Guid packId) => Path.Combine(PackRoot(packId), "shared");

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

    // No-op — shared/ folder is no longer used; sync goes directly from/to game/.
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
        var src = Path.Combine(sourceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var dst = Path.Combine(destRoot,   relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(src)) throw new FileNotFoundException(src);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        if (File.Exists(dst)) File.Delete(dst);
        File.Move(src, dst);
    }

    public void CopyFileBetweenFolders(string sourceRoot, string destRoot, string relativePath)
    {
        var src = Path.Combine(sourceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var dst = Path.Combine(destRoot,   relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(src)) throw new FileNotFoundException(src);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Copy(src, dst, overwrite: true);
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

    private static void OverlayFilesIntoGame(string sourceRoot, string gameRoot, bool overwriteExisting)
    {
        foreach (var srcFile in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceRoot, srcFile).Replace('\\', '/');
            var destFile = Path.Combine(gameRoot, rel.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(destFile))
            {
                if (PathsReferToSameFile(srcFile, destFile))
                    continue;
                if (!overwriteExisting)
                    continue;
                File.Delete(destFile);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            if (!TryCreateHardLink(destFile, srcFile))
                File.Copy(srcFile, destFile, overwrite: true);
        }
    }

    private static bool TryCreateHardLink(string linkPath, string existingPath)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        return CreateHardLinkW(linkPath, existingPath, IntPtr.Zero);
    }

    [DllImport("Kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    private static bool PathsReferToSameFile(string pathA, string pathB)
    {
        if (!File.Exists(pathA) || !File.Exists(pathB))
            return false;

        if (string.Equals(Path.GetFullPath(pathA), Path.GetFullPath(pathB), StringComparison.OrdinalIgnoreCase))
            return true;

        // On Windows compare the underlying file IDs (volume serial + file index).
        // Two paths with the same ID are hard links to the same inode — no content
        // check needed and no false positives from coincidentally equal metadata.
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
    /// Uploads files from game/ that are selected by <paramref name="sharedPaths"/>.
    /// The caller should pass the relative paths of files that should be synced
    /// (i.e. those matching "shared" rules). Files are read directly from game/.
    /// </summary>
    /// <summary>Returns the new manifest version the server assigned at commit, so callers
    /// can record it authoritatively instead of issuing a second manifest fetch.</summary>
    public async Task<long> UploadSharedAsync(Guid packId, long baseVersion, IReadOnlyCollection<string> sharedPaths, IProgress<string>? log, CancellationToken ct = default)
    {
        void Report(string m) { log?.Report(m); AppLog.Log("upload", m); }

        var gameDir = GameDir(packId);
        ProgressHub.Indeterminate(packId, "Building manifest…");

        // Private assets never leave this machine, whatever the rules say (see PrivateAssetPolicy).
        // Done here rather than in the caller so no upload path - button, script, future automation -
        // can forget it. Reported loudly: an omission the owner cannot see is how the bundle leaked.
        var (publicPaths, privatePaths) = PrivateAssetPolicy.Partition(sharedPaths, settings);
        if (privatePaths.Count > 0)
            Report($"Kept private on this machine (not uploaded): {PrivateAssetPolicy.Describe(privatePaths, settings)}");
        sharedPaths = publicPaths;

        // Manifest names can differ from local paths in exactly one case: a jar that LOW MODE renamed to
        // .jar.disabled is shared under its enabled name, so this machine's low mode never becomes everyone's
        // baseline and mod updates keep flowing to other players. Hand-disabled jars keep their name - that
        // rename is a deliberate whole-pack action.
        // The same masking applies to config CONTENT: a file low mode turned down is hashed and uploaded as
        // the player's own pre-low-mode values, so low mode can safely edit shared configs too. Null means
        // "nothing masked here", and the file on disk is uploaded verbatim.
        var entryPaths = await Task.Run(() =>
        {
            var result = new List<(ManifestEntry Entry, string Abs, byte[]? Masked)>();
            foreach (var rel in sharedPaths)
            {
                var abs = Path.Combine(gameDir, rel.Replace('/', Path.DirectorySeparatorChar));
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
            // Skip (don't crash) if the server asks for a blob we have no local path for — a
            // malformed/mismatched response otherwise threw KeyNotFoundException and aborted sync.
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

        ProgressHub.Indeterminate(packId, "Committing…");
        var commit = await api.CommitUploadAsync(packId, new BeginUploadRequest(baseVersion, entries), ct);
        Report($"Committed {entries.Count} file(s). New manifest version: {commit.NewVersion}");
        SaveSyncManifestLock(packId, entries.Select(e => e.RelativePath));
        ProgressHub.Clear(packId);
        return commit.NewVersion;
    }

    public async Task DownloadSharedAsync(Guid packId, IProgress<string>? log, CancellationToken ct = default)
    {
        void Report(string m) { log?.Report(m); AppLog.Log("download", m); }

        var gameDir = GameDir(packId);
        Directory.CreateDirectory(gameDir);
        ProgressHub.Indeterminate(packId, "Fetching manifest…");
        var manifest = await api.GetManifestAsync(packId, ct);
        Report($"Server manifest has {manifest.Entries.Count} files at version {manifest.Version}");

        // A manifest can still carry private paths - an older upload from before the policy existed, or
        // a collaborator's copy - and must not be allowed to overwrite the real files here. They are
        // dropped from the working set entirely, so neither the download loop nor the prune below sees them.
        var privateOnServer = manifest.Entries.Where(e => PrivateAssetPolicy.IsPrivate(e.RelativePath, settings)).ToList();
        if (privateOnServer.Count > 0)
        {
            Report($"Ignoring private path(s) from the server manifest: {PrivateAssetPolicy.Describe(privateOnServer.Select(e => e.RelativePath).ToList(), settings)}");
            manifest = manifest with { Entries = manifest.Entries.Where(e => !PrivateAssetPolicy.IsPrivate(e.RelativePath, settings)).ToList() };
        }

        // Whose machine is this? The owner's private files are protected from the prune below; on any
        // other machine a private path that arrived through an earlier sync is exactly what should be
        // pruned once the server stops listing it. If the pack cannot be fetched, err on the side of
        // keeping files - a lost network is not a reason to delete anything.
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
            // Build manually (not ToDictionary) so two manifest entries differing only in case —
            // legal on the Linux server, a duplicate key on Windows — don't throw ArgumentException
            // and permanently break sync for every Windows subscriber.
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
        foreach (var e in manifest.Entries)
        {
            ct.ThrowIfCancellationRequested();
            i++;
            if (localByPath.TryGetValue(e.RelativePath, out var localHash) && localHash == e.Hash) continue;
            var abs = SafeResolve(gameDir, e.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
            Report($"Downloading {i}/{total}: {e.RelativePath}");
            ProgressHub.Report(packId,
                total == 0 ? 1 : (double)(i - 1) / total,
                $"Downloading {i}/{total}: {e.RelativePath}",
                -1,
                e.RelativePath);

            // Download to a temp file, verify its hash, then atomically replace the real file.
            // Writing straight to `abs` (as before) truncated the existing good copy immediately,
            // so a network drop / cancel left a corrupt half-file at the final path with the
            // previous good version already destroyed. Never verified the bytes, either.
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
                    }, ct);
                }

                string actualHash;
                await using (var vfs = File.OpenRead(tmp))
                    actualHash = Convert.ToHexString(await SHA256.HashDataAsync(vfs, ct)).ToLowerInvariant();
                if (!string.Equals(actualHash, e.Hash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException(
                        $"Downloaded '{e.RelativePath}' failed integrity check (expected {e.Hash}, got {actualHash}).");

                File.Move(tmp, abs, overwrite: true);
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
            // The owner's private path is never pruned: the lock may list it from an upload made before
            // the policy existed, and "no longer on the server" is exactly the state the policy creates
            // on purpose. Elsewhere it is pruned like any other file the server stopped listing.
            if (protectPrivate && PrivateAssetPolicy.IsPrivate(rel, settings)) continue;
            if (!serverPaths.Contains(rel))
            {
                // Guard the delete sink too: the sync-lock list was itself populated from
                // server-supplied paths, so an unchecked traversal here is an arbitrary-delete
                // primitive. Skip anything that would escape the pack folder.
                string abs;
                try { abs = SafeResolve(gameDir, rel); }
                catch (InvalidOperationException) { continue; }
                if (File.Exists(abs)) { File.Delete(abs); Report($"Removed (no longer on server): {rel}"); }
            }
        }

        SaveSyncManifestLock(packId, manifest.Entries.Select(e => e.RelativePath));
        // Drop the cached mods.json / plans.json so a collaborator's freshly-synced flags, categories
        // and planning boards are read from disk next time, instead of being overwritten by our
        // stale in-memory copies.
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
        CancellationToken ct)
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
        }
    }
}
