using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>
/// The scanning, diffing and cross-instance copying behind the Config and scripts page.
/// </summary>
/// <remarks>
/// <para>A scan produces a flat list of <see cref="Entry"/> records keyed by game-relative path, and
/// the page groups them. Every public method is either pure or safe to call from <c>Task.Run</c>.</para>
/// <para>Scans, the cross-instance compare (<see cref="CompareGroups"/>, which hashes same-length
/// copies) and <see cref="ReadKubeJsErrors"/> are cached on disk per profile in
/// <see cref="ScanCache{T}"/>, since re-walking thousands of files on every page open is too slow.
/// <see cref="Cached"/> returns the last result without touching a folder; <see cref="Rescan"/>
/// re-walks behind it and publishes only what changed.</para>
/// <para>Each root is validated by <see cref="RootFingerprint"/> (exists, mtime, recursive file and
/// dir count, total bytes), which catches a create, delete or resize at any depth. It can't see an
/// in-place edit that keeps a file's length: Refresh covers that, and <see cref="Invalidate"/>
/// covers our own writes.</para>
/// </remarks>
public static class ConfigHubService
{
    /// <summary>Which of the page's folders a file came from. Drives the kind filter and the
    /// badge on each row.</summary>
    public enum FileKind
    {
        /// <summary><c>config/</c>: the per-instance mod settings people actually edit.</summary>
        Config,
        /// <summary><c>kubejs/</c>: startup, server and client scripts plus the data and assets they
        /// generate.</summary>
        KubeJs,
        /// <summary><c>defaultconfigs/</c>: what a pack copies into a new world's config. Often confused
        /// with the real configs, so it's listed next to them.</summary>
        DefaultConfigs,
        /// <summary><c>logs/kubejs/</c>: where KubeJS script errors land.</summary>
        KubeJsLog,
        /// <summary><c>saves/&lt;world&gt;/serverconfig/</c>: one world's server settings. Last so the
        /// byte stored in the scan cache keeps its meaning for the older kinds.</summary>
        ServerConfig
    }

    /// <summary>One file in one instance.</summary>
    /// <param name="RelativePath">Game-relative, forward slashes, e.g. <c>config/jei/jei-client.ini</c>.
    /// This is the key the page groups on, so it must be normalised the same way everywhere.</param>
    public sealed record Entry(
        Guid PackId,
        string PackName,
        string RelativePath,
        string FullPath,
        long Size,
        DateTime ModifiedUtc,
        FileKind Kind)
    {
        public string FileName => Path.GetFileName(RelativePath);

        /// <summary>The folder part of <see cref="RelativePath"/>, or "" at the root.</summary>
        public string FolderPath
        {
            get
            {
                var slash = RelativePath.LastIndexOf('/');
                return slash < 0 ? "" : RelativePath[..slash];
            }
        }
    }

    /// <summary>One file as remembered between launches. <c>FullPath</c> and <c>PackName</c> are
    /// re-derived on load rather than stored, which keeps the file much smaller.</summary>
    public sealed record CachedEntry(string Rel, long Size, long MTicks, byte Kind);

    /// <summary>One shared path's compare verdict, remembered so the SHA-256 pass is not repaid on
    /// every page open.</summary>
    public sealed record CachedGroupState(string Rel, byte State);

    /// <summary>One KubeJS error line and the script it names.</summary>
    public sealed record CachedKubeError(string Script, string Line);

    private static readonly ScanCache<CachedEntry> FileCache =
        ScanCaches.For<CachedEntry>(ScanKinds.ConfigHub);

    /// <summary>The cross-instance compare. Entries span every instance, so any instance's mutation
    /// clears the lot. That's needed: after our own same-size write the fingerprint doesn't move, so
    /// the key would otherwise still hit.</summary>
    private static readonly ScanCache<CachedGroupState> GroupCache =
        ScanCaches.For<CachedGroupState>(ScanKinds.ConfigGroups, maxScopes: 8, crossInstance: true);

    private static readonly ScanCache<CachedKubeError> KubeCache =
        ScanCaches.For<CachedKubeError>(ScanKinds.KubeJsErrors);

    /// <summary>Reads the three cache files off the UI thread. Await it once before the page's
    /// first-frame <see cref="Cached"/> call so no cache read touches the dispatcher.</summary>
    public static Task WarmAsync() => Task.WhenAll(
        FileCache.EnsureLoadedAsync(), GroupCache.EnsureLoadedAsync(), KubeCache.EnsureLoadedAsync());

    /// <summary>The fixed folders the page covers, and the kind each produces. Order matters only in
    /// that it is the order files appear in before sorting. Each world's <c>serverconfig/</c> is added
    /// per instance by <see cref="RootsOf"/>.</summary>
    private static readonly (string Relative, FileKind Kind)[] Roots =
    [
        ("config",         FileKind.Config),
        ("kubejs",         FileKind.KubeJs),
        ("defaultconfigs", FileKind.DefaultConfigs),
        ("logs/kubejs",    FileKind.KubeJsLog),
    ];

    /// <summary>
    /// Every folder walked in one instance: the fixed <see cref="Roots"/>, then the
    /// <c>serverconfig/</c> of each world that has one.
    /// </summary>
    /// <remarks>
    /// <para>Server configs are per world on Forge, and FML fills a new world's from
    /// <c>defaultconfigs/</c>, so they belong next to the other two. Finding them costs one read of
    /// <c>saves/</c>; the worlds themselves are never walked.</para>
    /// <para>Sorted by name so the fingerprint built from this list is stable. A world linked in from
    /// elsewhere is skipped, as the walk skips any linked folder.</para>
    /// </remarks>
    private static List<(string Relative, FileKind Kind)> RootsOf(string gameDir)
    {
        var roots = new List<(string, FileKind)>(Roots);
        try
        {
            var saves = Path.Combine(gameDir, "saves");
            if (!Directory.Exists(saves)) return roots;
            foreach (var world in new DirectoryInfo(saves).GetDirectories().OrderBy(d => d.Name, StringComparer.Ordinal))
            {
                if (world.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (!Directory.Exists(Path.Combine(world.FullName, "serverconfig"))) continue;
                roots.Add(($"saves/{world.Name}/serverconfig", FileKind.ServerConfig));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return roots;
    }

    /// <summary>Which kind a game-relative path would be listed as, or null when it lies outside
    /// every folder the page covers.</summary>
    public static FileKind? KindForPath(string relativePath)
    {
        var rel = relativePath.Replace('\\', '/');
        if (rel.StartsWith("logs/kubejs/", StringComparison.OrdinalIgnoreCase)) return FileKind.KubeJsLog;
        foreach (var (root, kind) in Roots)
            if (rel.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)) return kind;
        var parts = rel.Split('/');
        return parts.Length >= 4 && parts[0].Equals("saves", StringComparison.OrdinalIgnoreCase)
                                 && parts[2].Equals("serverconfig", StringComparison.OrdinalIgnoreCase)
            ? FileKind.ServerConfig
            : null;
    }

    /// <summary>
    /// Folder names never walked into. They hold generated dumps, not hand-edited files; KubeJS's
    /// <c>probe_dumps</c> alone can be tens of thousands of files.
    /// </summary>
    private static readonly HashSet<string> SkipFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".svn", "probe_dumps", "exported", ".cache", "cache"
    };

    /// <summary>Files larger than this are listed but never hashed or grepped. At that size a config
    /// is a generated dump, and reading a dozen of them would stall the scan.</summary>
    public const long MaxInspectBytes = 4L * 1024 * 1024;

    /// <summary>How deep the walk goes. Config trees are shallow in practice; the limit stops a
    /// symlink loop or a stray world folder from turning the scan into a hang. The fingerprint uses
    /// the same number, so it describes the tree that was actually walked.</summary>
    private const int MaxWalkDepth = 12;

    // ── scanning ─────────────────────────────────────────────────────────────

    /// <summary>Everything the Config page needs for one paint: the files, the cross-instance compare
    /// and the KubeJS errors, plus when the oldest part of the answer was read.</summary>
    /// <param name="ScannedUtc">Null only when nothing in the answer came from a remembered scan.</param>
    public sealed record ScanResult(
        List<Entry> Files,
        Dictionary<string, GroupState> Groups,
        Dictionary<Guid, Dictionary<string, List<string>>> KubeErrors,
        DateTimeOffset? ScannedUtc);

    /// <summary>
    /// What was found last time, with no disk walk, so it's safe on the UI thread for the page's first
    /// frame. Null when anything is missing; the page then shows the scan's progress instead.
    /// </summary>
    /// <remarks>
    /// <para>All-or-nothing on files and compare: files without the compare would paint every shared
    /// path with the "?" badge, which claims the file was too large or locked. Both are invalidated by
    /// the same fingerprint changes, so they hit and miss together in practice.</para>
    /// <para>KubeJS errors may be absent: that just shows no warning triangle until the background
    /// rescan fills them in.</para>
    /// </remarks>
    public static ScanResult? Cached(IReadOnlyList<PackSummary> packs, PackFolderService folders)
    {
        if (packs.Count == 0) return null;

        var files = new List<Entry>();
        var fingerprints = new List<(Guid Id, string Fingerprint)>();
        DateTimeOffset? oldest = null;

        foreach (var pack in packs)
        {
            string gameDir;
            try { gameDir = folders.GameDir(pack.Id); }
            catch { return null; }

            var hit = FileCache.Get(ScanCache<CachedEntry>.ScopeKey(pack.Id, "files"));
            if (hit.State != ScanState.Cached || hit.Fingerprint is not { Length: > 0 } fingerprint)
                return null;

            // Cheap check, and only when there's something to lose: don't paint remembered files for an
            // instance whose folder has since gone. An instance that never had a game folder is a
            // remembered empty list and needs no check.
            if (hit.Rows.Count > 0 && !RootFingerprint.Shallow(gameDir).Exists) return null;

            fingerprints.Add((pack.Id, fingerprint));
            foreach (var row in hit.Rows) files.Add(Rehydrate(row, pack, gameDir));
            if (hit.ScannedUtc is { } when && (oldest is null || when < oldest)) oldest = when;
        }

        var groupHit = GroupCache.Get(GroupScopeKey(fingerprints));
        if (groupHit.State != ScanState.Cached) return null;
        if (groupHit.ScannedUtc is { } gWhen && (oldest is null || gWhen < oldest)) oldest = gWhen;

        var groups = new Dictionary<string, GroupState>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in groupHit.Rows) groups[row.Rel] = (GroupState)row.State;

        return new ScanResult(files, groups, CachedKubeErrors(packs), oldest);
    }

    /// <summary>
    /// Re-reads whatever has changed and returns the whole answer. Call from a background thread.
    /// </summary>
    /// <param name="force">Ignore every remembered scan and re-read every file. This is the only way
    /// to catch an in-place edit that did not change a file's size, which is why the Refresh button
    /// says so.</param>
    /// <param name="progress">Reports the instance name as each one starts, for the status line.</param>
    public static ScanResult Rescan(
        IReadOnlyList<PackSummary> packs,
        PackFolderService folders,
        AppSettings settings,
        bool force,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var (files, fingerprints) = ScanFiles(packs, folders, settings, force, progress, ct);

        // The compare stays valid while no participating instance has changed, which is also the only
        // time its verdicts can change, so the concatenated fingerprints are the key.
        var groupKey = GroupScopeKey(fingerprints);
        var groupHit = force ? CachedScan<CachedGroupState>.Cold : GroupCache.Get(groupKey);

        Dictionary<string, GroupState> groups;
        if (groupHit.State == ScanState.Cached)
        {
            groups = new Dictionary<string, GroupState>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in groupHit.Rows) groups[row.Rel] = (GroupState)row.State;
        }
        else
        {
            // Only paths in more than one instance need comparing, and only same-length copies need
            // reading (see CompareGroups).
            var shared = files.GroupBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                              .Where(g => g.Count() > 1);
            groups = CompareGroups(shared, ct);
            GroupCache.Put(groupKey,
                groups.Select(kv => new CachedGroupState(kv.Key, (byte)kv.Value)).ToList(),
                groupKey);
        }

        var kube = new Dictionary<Guid, Dictionary<string, List<string>>>();
        foreach (var pack in packs)
        {
            ct.ThrowIfCancellationRequested();
            string gameDir;
            try { gameDir = folders.GameDir(pack.Id); }
            catch { continue; }
            kube[pack.Id] = KubeErrorsFor(pack.Id, gameDir, force, ct);
        }

        return new ScanResult(files, groups, kube, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Walks every given instance's config, KubeJS and defaultconfigs folders, re-reading only those
    /// whose fingerprint has moved. Call from a background thread. Instances without those folders (a
    /// fresh, never-launched one, say) just contribute nothing.
    /// </summary>
    /// <param name="force">Ignore the remembered scans and re-walk every instance.</param>
    /// <param name="progress">Reports the instance name as each one starts, for the status line.</param>
    public static List<Entry> Scan(
        IReadOnlyList<PackSummary> packs,
        PackFolderService folders,
        AppSettings settings,
        bool force,
        IProgress<string>? progress,
        CancellationToken ct) =>
        ScanFiles(packs, folders, settings, force, progress, ct).Files;

    private static (List<Entry> Files, List<(Guid Id, string Fingerprint)> Fingerprints) ScanFiles(
        IReadOnlyList<PackSummary> packs,
        PackFolderService folders,
        AppSettings settings,
        bool force,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var all = new List<Entry>();
        var fingerprints = new List<(Guid, string)>();

        foreach (var pack in packs)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(pack.Name);

            string gameDir;
            // The name-less overload never creates the folder, so scanning doesn't create an empty game/
            // for an instance that was never downloaded.
            try { gameDir = folders.GameDir(pack.Id); }
            catch { continue; }

            var key = ScanCache<CachedEntry>.ScopeKey(pack.Id, "files");
            var remembered = force ? CachedScan<CachedEntry>.Cold : FileCache.Get(key);
            var now = FingerprintOf(gameDir, ct);
            fingerprints.Add((pack.Id, now));

            // A null remembered fingerprint means "walk it all"; that's how an entry past its maximum age
            // forces a full re-read without a second flag.
            if (remembered.State == ScanState.Cached && remembered.Fingerprint == now)
            {
                foreach (var row in remembered.Rows) all.Add(Rehydrate(row, pack, gameDir));
                continue;
            }

            var files = WalkPack(pack, gameDir, settings, ct);
            FileCache.Put(key, files.Select(ToCached).ToList(), now);
            all.AddRange(files);
        }
        return (all, fingerprints);
    }

    /// <summary>Drops one instance's remembered scans, so the next one re-reads it. Called after this
    /// page writes into an instance, which is the one case the fingerprint is guaranteed to miss
    /// (overwriting a file of the same length changes no folder's timestamp and no byte total).</summary>
    public static void Invalidate(Guid packId) =>
        ScanCaches.InvalidatePack(packId, ScanScope.Config);

    /// <summary>Drops every remembered config scan, compare and KubeJS log parse.</summary>
    public static void InvalidateAll()
    {
        FileCache.Clear();
        GroupCache.Clear();
        KubeCache.Clear();
    }

    /// <summary>
    /// What an instance's config roots look like, cheaply: exists, own write time, and the
    /// recursive file count, folder count and byte total of each. It walks the tree, so it is paid
    /// only by the background rescan and never before serving a remembered list.
    /// </summary>
    /// <remarks>The skip list and depth are the scan's own, so the fingerprint describes the same
    /// tree that was walked rather than a larger one.</remarks>
    private static string FingerprintOf(string gameDir, CancellationToken ct)
    {
        var fingerprint = new Fingerprint();
        foreach (var (relative, _) in RootsOf(gameDir))
        {
            var root = Path.Combine(gameDir, relative.Replace('/', Path.DirectorySeparatorChar));
            fingerprint.Add(relative, RootFingerprint.Compute(root, SkipFolders, MaxWalkDepth, ct));
        }
        return fingerprint.ToString();
    }

    /// <summary>
    /// The scope one cross-instance compare is remembered under: every participating instance's id
    /// and fingerprint, in a fixed order, hashed down to something short enough for a key.
    /// </summary>
    private static string GroupScopeKey(IReadOnlyList<(Guid Id, string Fingerprint)> packs)
    {
        var material = string.Join('\n', packs.OrderBy(p => p.Id)
                                              .Select(p => $"{p.Id:N}={p.Fingerprint}"));
        return "__groups:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..32];
    }

    private static CachedEntry ToCached(Entry entry) =>
        new(entry.RelativePath, entry.Size, entry.ModifiedUtc.Ticks, (byte)entry.Kind);

    private static Entry Rehydrate(CachedEntry row, PackSummary pack, string gameDir) =>
        new(pack.Id, pack.Name, row.Rel,
            Path.Combine(gameDir, row.Rel.Replace('/', Path.DirectorySeparatorChar)),
            row.Size, new DateTime(row.MTicks, DateTimeKind.Utc), (FileKind)row.Kind);

    /// <summary>One instance's KubeJS errors, re-parsed only when the log folder has changed.</summary>
    /// <remarks>Fingerprinted on <c>logs/kubejs</c> alone: nothing else it reads can change its
    /// answer, and that folder is small and flat, so the check costs one directory read.</remarks>
    private static Dictionary<string, List<string>> KubeErrorsFor(
        Guid packId, string gameDir, bool force, CancellationToken ct)
    {
        var key = ScanCache<CachedKubeError>.ScopeKey(packId, "kubejs");
        var dir = Path.Combine(gameDir, "logs", "kubejs");
        var now = new Fingerprint().Add("logs/kubejs", RootFingerprint.Compute(dir, null, 2, ct)).ToString();

        if (!force)
        {
            var hit = KubeCache.Get(key);
            if (hit.State == ScanState.Cached && hit.Fingerprint == now) return Regroup(hit.Rows);
        }

        Dictionary<string, List<string>> parsed;
        try { parsed = ReadKubeJsErrors(gameDir, ct); }
        catch (OperationCanceledException) { throw; }
        // An unreadable log folder is normal, not an error. It isn't cached either, so "unknown" doesn't
        // harden into "no errors".
        catch { return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase); }

        KubeCache.Put(key,
            parsed.SelectMany(kv => kv.Value.Select(line => new CachedKubeError(kv.Key, line))).ToList(),
            now);
        return parsed;
    }

    private static Dictionary<Guid, Dictionary<string, List<string>>> CachedKubeErrors(
        IReadOnlyList<PackSummary> packs)
    {
        var byPack = new Dictionary<Guid, Dictionary<string, List<string>>>();
        foreach (var pack in packs)
        {
            var hit = KubeCache.Get(ScanCache<CachedKubeError>.ScopeKey(pack.Id, "kubejs"));
            if (hit.State != ScanState.Cached) continue;
            byPack[pack.Id] = Regroup(hit.Rows);
        }
        return byPack;
    }

    private static Dictionary<string, List<string>> Regroup(IReadOnlyList<CachedKubeError> rows)
    {
        var byScript = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!byScript.TryGetValue(row.Script, out var list))
                byScript[row.Script] = list = new List<string>();
            list.Add(row.Line);
        }
        return byScript;
    }

    private static List<Entry> WalkPack(PackSummary pack, string gameDir, AppSettings settings, CancellationToken ct)
    {
        var files = new List<Entry>();
        foreach (var (relative, kind) in RootsOf(gameDir))
        {
            var root = Path.Combine(gameDir, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(root)) continue;
            Walk(root, gameDir, pack, kind, settings, files, depth: 0, ct);
        }
        return files;
    }

    /// <summary>Iterative-per-folder recursion so one unreadable subfolder cannot abort the whole
    /// instance, which <c>SearchOption.AllDirectories</c> would.</summary>
    private static void Walk(
        string dir, string gameDir, PackSummary pack, FileKind kind,
        AppSettings settings, List<Entry> into, int depth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (depth > MaxWalkDepth) return;

        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }

        foreach (var path in entries)
        {
            ct.ThrowIfCancellationRequested();
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch { continue; }

            var name = Path.GetFileName(path);
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                if (SkipFolders.Contains(name)) continue;
                // A reparse point is either a junction into another pack (double-listing) or a loop.
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                Walk(path, gameDir, pack, kind, settings, into, depth + 1, ct);
                continue;
            }

            if (!TextFileService.LooksEditable(path)) continue;
            if (IsBackup(name)) continue;

            var rel = Path.GetRelativePath(gameDir, path).Replace('\\', '/');
            // The privacy policy is the sync boundary, but also a "don't show this" list: private bundles
            // under config/ shouldn't be browsable here either.
            if (PrivateAssetPolicy.IsPrivate(rel, settings)) continue;

            FileInfo info;
            try { info = new FileInfo(path); }
            catch { continue; }

            into.Add(new Entry(pack.Id, pack.Name, rel, path, info.Length, info.LastWriteTimeUtc, kind));
        }
    }

    /// <summary>True for the backups this page writes before overwriting
    /// (<c>foo.toml.bak-20260921-013000</c>). They are hidden from the list, since after a few copies
    /// they'd outnumber the real files, and are reached through the row's "Restore a backup" action
    /// instead.</summary>
    public static bool IsBackup(string fileName)
    {
        var dot = fileName.LastIndexOf(".bak-", StringComparison.OrdinalIgnoreCase);
        return dot > 0;
    }

    /// <summary>
    /// When a backup was taken, read from the <c>.bak-yyyyMMdd-HHmmss</c> suffix this page writes,
    /// falling back to the last-write time for names that aren't ours.
    /// </summary>
    /// <remarks>
    /// Not mtime: <see cref="File.Copy(string,string,bool)"/> keeps the source's last-write time, so a
    /// backup's mtime is the age of its content, and "restore the newest backup" would pick the wrong
    /// one after two edits in a row.
    /// </remarks>
    /// <seealso cref="TimeFormat.Stamp"/>
    public static DateTime BackupTakenUtc(FileInfo backup)
    {
        var name = backup.Name;
        var dot = name.LastIndexOf(".bak-", StringComparison.OrdinalIgnoreCase);
        if (dot >= 0 && DateTime.TryParseExact(
                name[(dot + 5)..], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
                // TimeFormat.StampNow writes the local wall clock, so that is what this reads.
                DateTimeStyles.AssumeLocal, out var taken))
            return taken.ToUniversalTime();
        return backup.LastWriteTimeUtc;
    }

    /// <summary>The backups sitting next to <paramref name="fullPath"/>, most recently taken first
    /// (see <see cref="BackupTakenUtc"/> for why that is not the same as newest by mtime).</summary>
    public static List<FileInfo> FindBackups(string fullPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(fullPath);
            if (dir is null || !Directory.Exists(dir)) return [];
            return new DirectoryInfo(dir)
                .GetFiles(Path.GetFileName(fullPath) + ".bak-*")
                .OrderByDescending(BackupTakenUtc)
                .ToList();
        }
        catch { return []; }
    }

    // ── comparing ────────────────────────────────────────────────────────────

    /// <summary>How the copies of one relative path across instances relate to each other.</summary>
    public enum GroupState
    {
        /// <summary>Only one instance has this file.</summary>
        Unique,
        /// <summary>Every instance's copy is byte-identical.</summary>
        Identical,
        /// <summary>At least one instance's copy differs.</summary>
        Differs,
        /// <summary>Too large to compare without stalling the scan.</summary>
        Unknown
    }

    /// <summary>
    /// Works out, for each relative path present in more than one instance, whether the copies match.
    /// Call from a background thread.
    /// </summary>
    /// <remarks>
    /// Files of different lengths can't be identical, so the common case (a config edited in one
    /// instance) is answered without reading a byte. Only same-length copies are hashed; in a real pack
    /// that's most of them, but they're small.
    /// </remarks>
    public static Dictionary<string, GroupState> CompareGroups(
        IEnumerable<IGrouping<string, Entry>> groups, CancellationToken ct)
    {
        var result = new Dictionary<string, GroupState>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            var copies = group.ToList();
            if (copies.Count < 2) { result[group.Key] = GroupState.Unique; continue; }

            if (copies.Select(c => c.Size).Distinct().Count() > 1)
            {
                result[group.Key] = GroupState.Differs;
                continue;
            }
            if (copies[0].Size > MaxInspectBytes)
            {
                result[group.Key] = GroupState.Unknown;
                continue;
            }

            string? first = null;
            var state = GroupState.Identical;
            foreach (var copy in copies)
            {
                var hash = HashOrNull(copy.FullPath);
                if (hash is null) { state = GroupState.Unknown; break; }
                if (first is null) first = hash;
                else if (first != hash) { state = GroupState.Differs; break; }
            }
            result[group.Key] = state;
        }
        return result;
    }

    /// <summary>SHA-256 of a file's bytes, or null when it can't be read (most likely locked by the
    /// running game). Never throws: an unreadable file becomes "unknown".</summary>
    public static string? HashOrNull(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch { return null; }
    }

    /// <summary>True when two files are byte-identical. Unreadable counts as "not identical" so the
    /// caller never claims a match it could not verify.</summary>
    public static bool SameBytes(string a, string b)
    {
        var ha = HashOrNull(a);
        return ha is not null && ha == HashOrNull(b);
    }

    // ── line diff ────────────────────────────────────────────────────────────

    public enum DiffKind { Same, Added, Removed }

    /// <param name="LeftNumber">1-based line number on the left, or null for an added line.</param>
    /// <param name="RightNumber">1-based line number on the right, or null for a removed line.</param>
    public sealed record DiffLine(DiffKind Kind, int? LeftNumber, int? RightNumber, string Text);

    /// <summary>What a diff came out as, so the caller can say "identical" plainly.</summary>
    /// <param name="OnlyLineEndings">True when the two files have the same lines but different bytes
    /// (line endings or a byte-order mark). Neither "identical" nor "every line changed" would be
    /// right, so it gets its own answer.</param>
    public sealed record DiffResult(
        IReadOnlyList<DiffLine> Lines, int Added, int Removed, bool Truncated, bool OnlyLineEndings);

    /// <summary>
    /// Above this many lines (after trimming the common prefix and suffix) the diff falls back to a
    /// positional comparison. The LCS table is O(n*m), so two 50,000-line recipe dumps would allocate
    /// gigabytes; hand-edited configs are nowhere near the cap.
    /// </summary>
    private const int LcsLineCap = 2000;

    /// <summary>
    /// A unified line diff of two texts, longest-common-subsequence based. Call from a background
    /// thread for anything but a small file.
    /// </summary>
    public static DiffResult Diff(string leftText, string rightText)
    {
        var left = SplitLines(leftText);
        var right = SplitLines(rightText);

        // Trim the identical head and tail first. Two copies of the same config usually differ in a
        // handful of lines, and this turns that into a tiny LCS problem.
        var head = 0;
        while (head < left.Length && head < right.Length && left[head] == right[head]) head++;
        var tail = 0;
        while (tail < left.Length - head && tail < right.Length - head
               && left[^(tail + 1)] == right[^(tail + 1)]) tail++;

        var midLeft = left[head..(left.Length - tail)];
        var midRight = right[head..(right.Length - tail)];

        var lines = new List<DiffLine>(left.Length + midRight.Length);
        for (var i = 0; i < head; i++)
            lines.Add(new DiffLine(DiffKind.Same, i + 1, i + 1, left[i]));

        var truncated = false;
        if (midLeft.Length > LcsLineCap || midRight.Length > LcsLineCap)
        {
            truncated = true;
            PositionalDiff(midLeft, midRight, head, lines);
        }
        else
        {
            LcsDiff(midLeft, midRight, head, lines);
        }

        for (var i = 0; i < tail; i++)
        {
            var l = left.Length - tail + i;
            var r = right.Length - tail + i;
            lines.Add(new DiffLine(DiffKind.Same, l + 1, r + 1, left[l]));
        }

        var added = lines.Count(l => l.Kind == DiffKind.Added);
        var removed = lines.Count(l => l.Kind == DiffKind.Removed);
        return new DiffResult(lines, added, removed, truncated,
            OnlyLineEndings: added == 0 && removed == 0 && leftText != rightText);
    }

    private static void LcsDiff(string[] left, string[] right, int offset, List<DiffLine> into)
    {
        var n = left.Length;
        var m = right.Length;
        var table = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                table[i, j] = left[i] == right[j]
                    ? table[i + 1, j + 1] + 1
                    : Math.Max(table[i + 1, j], table[i, j + 1]);

        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (left[x] == right[y])
            {
                into.Add(new DiffLine(DiffKind.Same, offset + x + 1, offset + y + 1, left[x]));
                x++; y++;
            }
            // Ties are resolved towards "removed first" so a changed line reads as the old value
            // immediately above the new one, which is how every diff tool shows it.
            else if (table[x + 1, y] >= table[x, y + 1])
            {
                into.Add(new DiffLine(DiffKind.Removed, offset + x + 1, null, left[x]));
                x++;
            }
            else
            {
                into.Add(new DiffLine(DiffKind.Added, null, offset + y + 1, right[y]));
                y++;
            }
        }
        while (x < n) { into.Add(new DiffLine(DiffKind.Removed, offset + x + 1, null, left[x])); x++; }
        while (y < m) { into.Add(new DiffLine(DiffKind.Added, null, offset + y + 1, right[y])); y++; }
    }

    /// <summary>The fallback for files too large for the LCS table: compare line <c>n</c> with line
    /// <c>n</c>. Useless after an inserted line, which is why the caller is told it was truncated.</summary>
    private static void PositionalDiff(string[] left, string[] right, int offset, List<DiffLine> into)
    {
        var shared = Math.Min(left.Length, right.Length);
        for (var i = 0; i < shared; i++)
        {
            if (left[i] == right[i])
            {
                into.Add(new DiffLine(DiffKind.Same, offset + i + 1, offset + i + 1, left[i]));
            }
            else
            {
                into.Add(new DiffLine(DiffKind.Removed, offset + i + 1, null, left[i]));
                into.Add(new DiffLine(DiffKind.Added, null, offset + i + 1, right[i]));
            }
        }
        for (var i = shared; i < left.Length; i++)
            into.Add(new DiffLine(DiffKind.Removed, offset + i + 1, null, left[i]));
        for (var i = shared; i < right.Length; i++)
            into.Add(new DiffLine(DiffKind.Added, null, offset + i + 1, right[i]));
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    // ── searching inside files ───────────────────────────────────────────────

    /// <param name="LineNumber">1-based.</param>
    public sealed record SearchHit(Entry File, int LineNumber, string Line);

    /// <param name="Skipped">Files that were too large or unreadable. Reported so a search never
    /// silently misses a file.</param>
    public sealed record SearchResult(List<SearchHit> Hits, int FilesRead, int Skipped);

    /// <summary>
    /// Greps <paramref name="files"/> for <paramref name="query"/>. Call from a background thread.
    /// </summary>
    /// <param name="maxHits">Stops early once this many hits are found; a search for <c>true</c> across
    /// twelve instances would otherwise produce tens of thousands of rows that nobody reads.</param>
    public static SearchResult SearchContents(
        IReadOnlyList<Entry> files, string query, int maxHits,
        IProgress<int>? progress, CancellationToken ct)
    {
        var hits = new List<SearchHit>();
        var read = 0;
        var skipped = 0;
        var done = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            done++;
            if (done % 25 == 0) progress?.Report(done);
            if (hits.Count >= maxHits) { skipped++; continue; }

            if (file.Size > MaxInspectBytes) { skipped++; continue; }
            string[] lines;
            try { lines = File.ReadAllLines(file.FullPath); }
            catch { skipped++; continue; }

            read++;
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                hits.Add(new SearchHit(file, i + 1, lines[i].Trim()));
                if (hits.Count >= maxHits) break;
            }
        }
        progress?.Report(done);
        return new SearchResult(hits, read, skipped);
    }

    // ── KubeJS errors ────────────────────────────────────────────────────────

    /// <summary>
    /// Reads an instance's <c>logs/kubejs/*.txt</c> and maps each script file name to the error lines
    /// that mention it, so a broken script shows its error on its own row.
    /// </summary>
    /// <remarks>
    /// Matches on the file name only: depending on the KubeJS version and the failure, lines name the
    /// script as <c>server_scripts/foo.js:12</c> or just <c>foo.js</c>. The odd false positive is cheaper
    /// than parsing each release's log format.
    /// </remarks>
    public static Dictionary<string, List<string>> ReadKubeJsErrors(string gameDir, CancellationToken ct)
    {
        var byScript = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var dir = Path.Combine(gameDir, "logs", "kubejs");
        if (!Directory.Exists(dir)) return byScript;

        foreach (var log in Directory.EnumerateFiles(dir, "*.txt"))
        {
            ct.ThrowIfCancellationRequested();
            string[] lines;
            try
            {
                var info = new FileInfo(log);
                if (info.Length > MaxInspectBytes) continue;
                lines = File.ReadAllLines(log);
            }
            catch { continue; }

            foreach (var line in lines)
            {
                if (line.IndexOf("ERROR", StringComparison.Ordinal) < 0
                    && line.IndexOf("error:", StringComparison.OrdinalIgnoreCase) < 0) continue;

                foreach (var name in ScriptNamesIn(line))
                {
                    if (!byScript.TryGetValue(name, out var list))
                        byScript[name] = list = new List<string>();
                    // Same error repeated on every reload: keep the first few, drop the rest.
                    if (list.Count < 4 && !list.Contains(line)) list.Add(line.Trim());
                }
            }
        }
        return byScript;
    }

    /// <summary>Pulls anything that looks like a script file name out of a log line.</summary>
    private static IEnumerable<string> ScriptNamesIn(string line)
    {
        var separators = new[] { ' ', '\t', '(', ')', '[', ']', '\'', '"', ',', ';', '#' };
        foreach (var token in line.Split(separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = token;
            // "server_scripts/foo.js:12" -> "foo.js"
            var colon = candidate.IndexOf(".js:", StringComparison.OrdinalIgnoreCase);
            if (colon >= 0) candidate = candidate[..(colon + 3)];
            if (!candidate.EndsWith(".js", StringComparison.OrdinalIgnoreCase)) continue;
            var slash = candidate.LastIndexOfAny(['/', '\\']);
            if (slash >= 0) candidate = candidate[(slash + 1)..];
            if (candidate.Length > 3) yield return candidate;
        }
    }

    // ── copying across instances ─────────────────────────────────────────────

    /// <param name="RelativePath">Game-relative path of a file that would be overwritten.</param>
    /// <param name="Shared">True when the destination instance's sync rules make this path Shared, so
    /// the change will travel to everyone else on that pack.</param>
    public sealed record CopyPreviewItem(Guid PackId, string PackName, string RelativePath, bool Exists, bool Shared);

    /// <param name="WouldOverwrite">How many existing files this copy replaces (each is backed up).</param>
    public sealed record CopyPreview(List<CopyPreviewItem> Items, int FileCount, int WouldOverwrite, List<string> SharedPacks);

    /// <summary>
    /// Works out what a copy would do before anything is written: how many files, which already exist,
    /// and which destination instances would sync the change to other people. Call from a background
    /// thread; it reads each destination's <c>.rules.json</c>.
    /// </summary>
    public static CopyPreview PreviewCopy(
        IReadOnlyList<string> sourceRelativePaths,
        IReadOnlyList<PackSummary> targets,
        PackFolderService folders,
        PackRuleService rules)
    {
        var items = new List<CopyPreviewItem>();
        var sharedPacks = new List<string>();
        var overwrite = 0;

        foreach (var target in targets)
        {
            string gameDir, packRoot;
            try
            {
                gameDir = folders.GameDir(target.Id);
                packRoot = folders.PackRoot(target.Id);
            }
            catch { continue; }

            var targetRules = rules.Load(packRoot);
            var anyShared = false;

            foreach (var rel in sourceRelativePaths)
            {
                var dest = Path.Combine(gameDir, rel.Replace('/', Path.DirectorySeparatorChar));
                var exists = File.Exists(dest);
                if (exists) overwrite++;
                var shared = rules.Match(rel, targetRules).IsAutoShared;
                if (shared) anyShared = true;
                items.Add(new CopyPreviewItem(target.Id, target.Name, rel, exists, shared));
            }
            if (anyShared) sharedPacks.Add(target.Name);
        }

        return new CopyPreview(items, sourceRelativePaths.Count * Math.Max(targets.Count, 1), overwrite, sharedPacks);
    }

    /// <param name="Copied">Files written.</param>
    /// <param name="BackedUp">Files that already existed and were backed up first.</param>
    /// <param name="Failures">One line per file that could not be copied, with the reason.</param>
    public sealed record CopyResult(int Copied, int BackedUp, List<string> Failures);

    /// <summary>
    /// Copies each of <paramref name="sourceRelativePaths"/> from <paramref name="sourceGameDir"/> into
    /// every target instance, backing up anything it overwrites. Call from a background thread.
    /// </summary>
    /// <remarks>
    /// Always backs up: a broken mod config can stop the game starting with no clue why, and an
    /// overwritten working copy would be unrecoverable.
    /// </remarks>
    public static CopyResult Copy(
        string sourceGameDir,
        IReadOnlyList<string> sourceRelativePaths,
        IReadOnlyList<PackSummary> targets,
        PackFolderService folders,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        // Invariant culture, because BackupTakenUtc parses the stamp back with InvariantCulture. In the
        // current culture a Thai or Saudi locale writes year 2569 or 1448, parsing fails, and backups
        // fall back to mtime (the source file's time).
        var stamp = TimeFormat.StampNow();
        var copied = 0;
        var backedUp = 0;
        var failures = new List<string>();

        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            string gameDir;
            try { gameDir = folders.GameDir(target.Id); }
            catch (Exception ex) { failures.Add($"{target.Name}: {ex.Message}"); continue; }

            foreach (var rel in sourceRelativePaths)
            {
                ct.ThrowIfCancellationRequested();
                var source = Path.Combine(sourceGameDir, rel.Replace('/', Path.DirectorySeparatorChar));
                var dest = Path.Combine(gameDir, rel.Replace('/', Path.DirectorySeparatorChar));
                progress?.Report($"{target.Name} · {rel}");
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    if (File.Exists(dest))
                    {
                        File.Copy(dest, $"{dest}.bak-{stamp}", overwrite: true);
                        backedUp++;
                    }
                    File.Copy(source, dest, overwrite: true);
                    copied++;
                }
                catch (Exception ex) { failures.Add($"{target.Name} · {rel}: {ex.Message}"); }
            }
            Invalidate(target.Id);
        }

        return new CopyResult(copied, backedUp, failures);
    }

    /// <summary>Every game-relative file path under <paramref name="relativeFolder"/> in one instance,
    /// used when the user copies a whole folder such as <c>kubejs/server_scripts</c>.</summary>
    public static List<string> ExpandFolder(IReadOnlyList<Entry> scan, Guid packId, string relativeFolder)
    {
        var prefix = relativeFolder.TrimEnd('/') + "/";
        return scan.Where(e => e.PackId == packId
                               && e.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                   .Select(e => e.RelativePath)
                   .ToList();
    }

    // ── defaultconfigs ───────────────────────────────────────────────────────

    /// <summary>
    /// Where FML looks for the pack's default of this file, game-relative, or null when it never
    /// does.
    /// </summary>
    /// <remarks>
    /// <para>Read from FML itself (NeoForge's FML 4.0 for 1.21.1; Forge's fmlcore for 1.20.1 and
    /// 1.21.1): when a mod's config file is missing, FML copies
    /// <c>defaultconfigs/&lt;file name&gt;</c> into place before it would write the mod's built-in
    /// defaults. The file name is the config's own, relative to the folder it loads from, so a
    /// subfolder is kept: <c>config/create/client.toml</c> becomes
    /// <c>defaultconfigs/create/client.toml</c>. That holds for every type: startup, client, common
    /// and server.</para>
    /// <para>Server configs load from <c>config/</c> on NeoForge 1.21, with a world's
    /// <c>serverconfig/</c> as an override read in place; on Forge they load from the world's
    /// <c>serverconfig/</c>. Either way the default is looked up by the same name, so
    /// <c>saves/W/serverconfig/foo-server.toml</c> becomes <c>defaultconfigs/foo-server.toml</c>.</para>
    /// <para>Only <c>.toml</c>: FML's configs are always TOML, and a mod that writes JSON or
    /// <c>.cfg</c> reads its own file from <c>config/</c> and never looks here. <c>fml.toml</c> is
    /// FML's own settings, read before any of this.</para>
    /// </remarks>
    public static string? DefaultConfigsPath(Entry entry)
    {
        var name = entry.Kind switch
        {
            FileKind.Config => entry.RelativePath["config/".Length..],
            FileKind.ServerConfig => entry.RelativePath.Split('/', 4) is { Length: 4 } parts ? parts[3] : null,
            _ => null
        };
        if (string.IsNullOrEmpty(name) || !name.EndsWith(".toml", StringComparison.OrdinalIgnoreCase)) return null;
        if (name.Equals("fml.toml", StringComparison.OrdinalIgnoreCase)) return null;
        return "defaultconfigs/" + name;
    }

    /// <summary>Why an instance on <paramref name="loader"/> has no defaultconfigs, or null when it
    /// has.</summary>
    public static string? NoDefaultConfigsReason(LoaderKind loader) => loader switch
    {
        LoaderKind.Forge or LoaderKind.NeoForge => null,
        LoaderKind.Fabric =>
            "Fabric and Quilt have no defaultconfigs folder. Each mod reads its own file in config/, " +
            "so that is where a pack's settings go.",
        _ => "Without a mod loader nothing reads defaultconfigs."
    };

    /// <summary>Why one file can't go to defaultconfigs, for the list of skipped files.</summary>
    public static string WhyNotDefaultConfigs(Entry entry) => entry.Kind switch
    {
        FileKind.DefaultConfigs => "already in defaultconfigs",
        FileKind.KubeJs or FileKind.KubeJsLog => "KubeJS files are not configs FML loads",
        _ when entry.FileName.Equals("fml.toml", StringComparison.OrdinalIgnoreCase) =>
            "FML reads its own fml.toml before defaultconfigs",
        _ => "not a .toml file - the mod reads it from config/ only"
    };

    /// <param name="TargetRelativePath">Game-relative, under <c>defaultconfigs/</c>.</param>
    /// <param name="Exists">A copy is already there and would be replaced (after a backup).</param>
    /// <param name="Identical">That copy is byte-identical already, so there is nothing to do.</param>
    public sealed record DefaultsItem(Entry Source, string TargetRelativePath, bool Exists, bool Identical);

    /// <summary>What copying <paramref name="entries"/> into defaultconfigs would do. Files FML never
    /// reads from there are left out; the caller lists them with <see cref="WhyNotDefaultConfigs"/>.
    /// Call from a background thread: it hashes every copy that already exists.</summary>
    public static List<DefaultsItem> PreviewDefaults(string gameDir, IEnumerable<Entry> entries)
    {
        var items = new List<DefaultsItem>();
        foreach (var entry in entries)
        {
            if (DefaultConfigsPath(entry) is not { } target) continue;
            var dest = Path.Combine(gameDir, target.Replace('/', Path.DirectorySeparatorChar));
            var exists = File.Exists(dest);
            items.Add(new DefaultsItem(entry, target, exists, exists && SameBytes(entry.FullPath, dest)));
        }
        return items;
    }

    /// <summary>
    /// Copies each item's file into defaultconfigs, backing up a copy it replaces the same way
    /// <see cref="Copy"/> does. With <paramref name="removeOriginal"/> the source is deleted once its
    /// copy is written, so FML fills it back in from defaultconfigs on the next start. Call from a
    /// background thread.
    /// </summary>
    /// <remarks>Two sources can map to one target (the same server config in two worlds); the later
    /// one wins, and its earlier copy is still backed up.</remarks>
    public static CopyResult CopyToDefaults(
        Guid packId, string gameDir, IReadOnlyList<DefaultsItem> items, bool removeOriginal, CancellationToken ct)
    {
        // Invariant culture: BackupTakenUtc parses this stamp back to order the backups.
        var stamp = TimeFormat.StampNow();
        var copied = 0;
        var backedUp = 0;
        var failures = new List<string>();

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Identical && !removeOriginal) continue;
            var dest = Path.Combine(gameDir, item.TargetRelativePath.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (!item.Identical)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    if (File.Exists(dest))
                    {
                        File.Copy(dest, $"{dest}.bak-{stamp}", overwrite: true);
                        backedUp++;
                    }
                    File.Copy(item.Source.FullPath, dest, overwrite: true);
                    copied++;
                }
                // Only once the copy is known to be there: a failed copy must never cost the original.
                if (removeOriginal && SameBytes(item.Source.FullPath, dest)) File.Delete(item.Source.FullPath);
            }
            catch (Exception ex) { failures.Add($"{item.Source.RelativePath}: {ex.Message}"); }
        }
        Invalidate(packId);
        return new CopyResult(copied, backedUp, failures);
    }

    // ── small helpers the page shares with its dialogs ───────────────────────

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.##} MB"
    };

    /// <summary>"3 minutes ago" style, for a timestamp the caller knows is UTC.</summary>
    /// <remarks>Kept as a wrapper because callers hold a UTC <see cref="DateTime"/>, and
    /// <see cref="TimeFormat"/> won't guess a <see cref="DateTime.Kind"/>.</remarks>
    public static string FormatAge(DateTime utc) => TimeFormat.Ago(TimeFormat.FromUtc(utc)) ?? "";

    public static string KindLabel(FileKind kind) => kind switch
    {
        FileKind.Config => "Config",
        FileKind.KubeJs => "KubeJS",
        FileKind.DefaultConfigs => "Default",
        FileKind.KubeJsLog => "Log",
        FileKind.ServerConfig => "Server config",
        _ => ""
    };

    /// <summary>The pin key stored in <see cref="AppSettings.ConfigHubPins"/>.</summary>
    public static string PinKey(Guid packId, string relativePath) => $"{packId:N}/{relativePath}";
}
