using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CloudLauncher.Services;

/// <summary>Storage categories, grouped by how the files behave.</summary>
public enum StorageCategory
{
    Mods = 0,
    Worlds,
    ResourcePacks,
    ShaderPacks,
    ConfigScripts,
    Logs,
    Screenshots,
    ServerFiles,
    /// <summary>Minecraft libraries, assets and version jars. Shared by every instance.</summary>
    Runtime,
    /// <summary>Java runtimes the launcher downloaded. Global.</summary>
    Java,
    /// <summary>The launcher's own profile folder: settings and caches.</summary>
    Profile,
    Other
}

/// <summary>Who a byte belongs to: an instance, or one of the four non-instance roots.</summary>
public enum StorageOwnerKind
{
    Instance = 0,
    /// <summary><c>.library/</c>: the single copy of everything shared into instances.</summary>
    Library,
    Runtime,
    Java,
    Profile
}

/// <summary>One instance (or one of the global roots), with its bytes split three ways.</summary>
/// <param name="UniqueBytes">Bytes reachable only from here, i.e. what deleting it would free.</param>
/// <param name="SharedBytes">Bytes something else reaches too. Attributed to every owner that can see
/// them, so these add up to more than the disk holds.</param>
/// <param name="GrossBytes">What a plain folder sum reports for this
/// owner, counting every link.</param>
public sealed record StorageOwnerRow(
    StorageOwnerKind Kind,
    Guid PackId,
    string Name,
    string Path,
    long UniqueBytes,
    long SharedBytes,
    long GrossBytes,
    int Files);

/// <summary>One category's share of the bytes that are actually on the disk.</summary>
public sealed record StorageCategoryRow(StorageCategory Category, string Name, string Note,
                                        long Bytes, int Files);

/// <summary>One named thing worth seeing in a "biggest first" list: a world, a pack, an instance's
/// whole mods folder, a runtime sub-tree.</summary>
public sealed record StorageItemRow(string Name, string Where, string Path, long Bytes, int Files,
                                    bool IsShared);

/// <summary>Something the launcher writes and would write again, with why it's safe to lose.</summary>
/// <remarks>Only folders the launcher regenerates (see <see cref="StorageUsageService"/>).</remarks>
public sealed record StorageReclaimRow(string Name, string Where, string Path, string Evidence,
                                       long Bytes, int Files);

/// <summary>A volume one of the roots lives on, so "412 GB" can be read against what is left.</summary>
public sealed record StorageVolumeRow(string Name, long FreeBytes, long TotalBytes);

/// <summary>What one complete pass found, as of <see cref="TakenUtc"/>.</summary>
/// <param name="OnDiskBytes">Distinct bytes: each physical file counted once, however many paths
/// lead to it.</param>
/// <param name="GrossBytes">The same tree counted per path, like a folder-by-folder sum. Always at
/// least <paramref name="OnDiskBytes"/>; the difference is what hard links saved.</param>
/// <param name="Approximate">True when the pass stopped checking for shared files part-way, so the
/// total isn't exact.</param>
public sealed record StorageReport(
    DateTimeOffset TakenUtc,
    long OnDiskBytes,
    long GrossBytes,
    long SharedBytes,
    int SharedFiles,
    long TotalFiles,
    bool Approximate,
    IReadOnlyList<StorageOwnerRow> Owners,
    IReadOnlyList<StorageCategoryRow> Categories,
    IReadOnlyList<StorageItemRow> Items,
    IReadOnlyList<StorageReclaimRow> Reclaimable,
    IReadOnlyList<StorageVolumeRow> Volumes,
    IReadOnlyList<string> Problems)
{
    /// <summary>Bytes hard-linking saved: the gap between what the folders add up to and what the
    /// disk is actually holding.</summary>
    public long SavedByLinks => Math.Max(0, GrossBytes - OnDiskBytes);

    public long ReclaimableBytes => Reclaimable.Sum(r => r.Bytes);

    /// <summary>Instances only, biggest unique size first. The global roots aren't deletable like an
    /// instance, so they're left out.</summary>
    public IEnumerable<StorageOwnerRow> Instances =>
        Owners.Where(o => o.Kind == StorageOwnerKind.Instance);

    public IEnumerable<StorageOwnerRow> Globals =>
        Owners.Where(o => o.Kind != StorageOwnerKind.Instance);

    /// <summary>"just now", "3 hours ago", worded like the other cache age notes.</summary>
    public string? AgeInWords => PackListCache.Describe(TakenUtc);
}

/// <summary>How far a pass has got. Reported often enough to move, rarely enough not to flood the
/// dispatcher.</summary>
public sealed record StorageProgress(int Done, int Total, string What, long BytesSoFar, long FilesSoFar);

/// <summary>Where the disk space went, counted once per physical file
/// rather than once per path.</summary>
/// <remarks>
/// <para>Library content is hard-linked into each instance's <c>local/</c>, then into <c>game/</c> and
/// <c>server-run/</c>, so a plain folder sum would count one 300 MB pack on ten instances as 9.3 GB.
/// Files are identified by volume serial and file index (<c>GetFileInformationByHandle</c>, as in
/// <see cref="PackFolderService.PathsReferToSameFile"/>); <c>NumberOfLinks</c> from the same call means
/// single-name files need no bookkeeping.</para>
/// <para>A handle open per file is slow, so only likely links are probed: everything in
/// <c>.library/</c> and <c>local/</c>, plus files in <c>game/</c> and <c>server-run/</c> that were
/// links in <c>local/</c> or are at least <see cref="ProbeFloorBytes"/>. Worlds, logs and the
/// global roots are never probed. <see cref="StorageReport.OnDiskBytes"/> can therefore be
/// slightly high, never low.</para>
/// <para>Reclaimable rows are only folders the launcher regenerates (logs, crash reports, debug,
/// .mixin.out, .trash, server installers, scan caches, mod fingerprints). Nothing is deleted here. A
/// pass runs off the dispatcher, writes nothing when cancelled, and is cached through
/// <see cref="ScanCache{T}"/>.</para>
/// </remarks>
public sealed class StorageUsageService(AppSettings settings)
{
    /// <summary>The cache's name, which is also its file: <c>scan/storage-usage.json</c>.</summary>
    public const string CacheKind = ScanKinds.StorageUsage;

    /// <summary>Files at least this big are always probed where a link is possible. Jars,
    /// pack zips and server libraries are practically always bigger, and a miss costs at
    /// most this much per file.</summary>
    public const long ProbeFloorBytes = 64 * 1024;

    /// <summary>Handle opens per pass before it stops probing and marks the report approximate. About
    /// four times what 40 instances of 900 jars need.</summary>
    public const int ProbeBudget = 150_000;

    /// <summary>A single cache scope, so the whole report goes stale together.</summary>
    private const string ScopeKey = "all";

    /// <summary>Progress is reported every this-many files, so the status line moves during a big
    /// world folder without posting to the dispatcher once per file.</summary>
    private const int ProgressEvery = 4_000;

    /// <summary>Largest-items rows to keep; also keeps the cache under its size limit.</summary>
    private const int MaxItems = 60;

    private static readonly ScanCache<StorageCacheRow> Cache =
        ScanCaches.For<StorageCacheRow>(CacheKind, maxRowsPerScope: 2_000, maxScopes: 2,
                                        maxAge: TimeSpan.FromDays(7), crossInstance: true);

    /// <summary>Reads the cache file off the dispatcher. Call it before the first
    /// <see cref="Cached"/> if that one is on the UI thread.</summary>
    public static Task WarmAsync() => Cache.EnsureLoadedAsync();

    // ── the size words ───────────────────────────────────────────────────────

    /// <summary>Bytes in readable units, up to TB.</summary>
    /// <remarks><see cref="LibraryItem.SizeLabel"/> stops at MB, which is fine for one file but
    /// not for a disk total.</remarks>
    public static string Size(long bytes)
    {
        if (bytes <= 0) return "-";
        const double K = 1024;
        double b = bytes;
        if (b < K) return $"{bytes} B";
        if (b < K * K) return $"{b / K:0.#} KB";
        if (b < K * K * K) return $"{b / (K * K):0.#} MB";
        if (b < K * K * K * K) return $"{b / (K * K * K):0.##} GB";
        return $"{b / (K * K * K * K):0.##} TB";
    }

    // ── the remembered answer ────────────────────────────────────────────────

    /// <summary>The last completed pass, or null if there has never been one. No disk work, never
    /// throws.</summary>
    /// <remarks>Returned however old it is, with <see cref="StorageReport.TakenUtc"/> on it; the caller
    /// decides whether to re-walk.</remarks>
    public StorageReport? Cached()
    {
        try
        {
            var hit = Cache.Get(ScopeKey);
            if (hit.State != ScanState.Cached || hit.Rows.Count == 0 || hit.ScannedUtc is not { } taken)
                return null;
            return Rehydrate(hit.Rows, taken);
        }
        catch { return null; }
    }

    // ── the pass ─────────────────────────────────────────────────────────────

    /// <summary>Walks every root and returns what is actually on the disk.</summary>
    /// <remarks>Runs on the thread pool. Cancelling throws before anything reaches the cache, so the
    /// remembered report is left as it was. An unreadable root becomes a line in
    /// <see cref="StorageReport.Problems"/> instead of failing the pass.</remarks>
    public Task<StorageReport> ScanAsync(IProgress<StorageProgress>? progress, CancellationToken ct) =>
        Task.Run(() => Scan(progress, ct), ct);

    private StorageReport Scan(IProgress<StorageProgress>? progress, CancellationToken ct)
    {
        var problems = new List<string>();
        var owners = BuildOwners(problems);
        var ledger = new Ledger(owners.Count, problems);

        for (var i = 0; i < owners.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var owner = owners[i];
            progress?.Report(new StorageProgress(i, owners.Count, owner.Name,
                                                 ledger.BytesSeen, ledger.FilesSeen));
            try
            {
                WalkOwner(ledger, owner, i, progress, owners.Count, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.LogError("storage", ex);
                problems.Add($"{owner.Name} could not be read in full.");
            }
        }

        ct.ThrowIfCancellationRequested();
        progress?.Report(new StorageProgress(owners.Count, owners.Count, "Adding it up",
                                             ledger.BytesSeen, ledger.FilesSeen));

        var report = ledger.Close(owners, Volumes(owners));
        Remember(report);
        return report;
    }

    /// <summary>Writes the finished pass into the scan cache.</summary>
    /// <remarks>The fingerprint is a shallow stamp of each root (a full one would cost as much as the
    /// walk), so it only notices a root appearing or disappearing. That's why the page shows the age
    /// and offers Refresh.</remarks>
    private void Remember(StorageReport report)
    {
        try
        {
            var fp = new Fingerprint();
            foreach (var (name, path) in RootPaths())
            {
                var (exists, mtime) = RootFingerprint.Shallow(path);
                fp.Add(name, exists ? mtime.ToString() : "-");
            }
            Cache.Put(ScopeKey, Dehydrate(report), fp.ToString());
        }
        catch { /* a cache that cannot be written is not worth failing the pass over */ }
    }

    private IEnumerable<(string Name, string Path)> RootPaths()
    {
        yield return ("packs", settings.PacksRoot);
        yield return ("runtime", AppSettings.RuntimeRoot);
        yield return ("java", AppSettings.JavaRoot);
        yield return ("profile", AppSettings.DataRootPath);
    }

    // ── what gets walked ─────────────────────────────────────────────────────

    /// <summary>One thing to walk, and the subtree to walk before the rest of it.</summary>
    /// <param name="First">Walked first, then skipped inside <c>Root</c>. This is <c>local/</c>:
    /// knowing which of its paths are links tells the <c>game/</c> walk what to probe.</param>
    private sealed record Owner(StorageOwnerKind Kind, Guid PackId, string Name, string Root,
                                string? First);

    private List<Owner> BuildOwners(List<string> problems)
    {
        var owners = new List<Owner>();

        // The library first: it holds the original of everything linked, so shared files get the
        // library's category and name.
        var library = Path.Combine(settings.PacksRoot, ContentLibraryService.FolderName);
        if (SafeDirExists(library))
            owners.Add(new Owner(StorageOwnerKind.Library, Guid.Empty, "Shared copies", library, null));

        try
        {
            var named = LocalPackScanner.Scan(settings)
                                        .GroupBy(p => p.Id)
                                        .ToDictionary(g => g.Key, g => g.First().Name);

            // Not PackFolderService.PackRoot: it creates missing folders, and this scan writes nothing.
            if (SafeDirExists(settings.PacksRoot))
                foreach (var dir in Directory.EnumerateDirectories(settings.PacksRoot))
                {
                    if (!LocalPackScanner.TryReadPackId(dir, out var id)) continue;
                    var name = named.TryGetValue(id, out var n) && n.Length > 0 ? n : Path.GetFileName(dir);
                    owners.Add(new Owner(StorageOwnerKind.Instance, id, name, dir, "local"));
                }
        }
        catch (Exception ex)
        {
            AppLog.LogError("storage", ex);
            problems.Add("The instances folder could not be listed.");
        }

        // The three global roots last. Nothing links into them, so the order doesn't matter; keeping
        // them together lets the page label them once.
        if (SafeDirExists(AppSettings.RuntimeRoot))
            owners.Add(new Owner(StorageOwnerKind.Runtime, Guid.Empty, "Minecraft runtime",
                                 AppSettings.RuntimeRoot, null));
        if (SafeDirExists(AppSettings.JavaRoot))
            owners.Add(new Owner(StorageOwnerKind.Java, Guid.Empty, "Java runtimes",
                                 AppSettings.JavaRoot, null));
        if (SafeDirExists(AppSettings.DataRootPath))
            owners.Add(new Owner(StorageOwnerKind.Profile, Guid.Empty, "Launcher profile",
                                 AppSettings.DataRootPath, null));

        return owners;
    }

    private static List<StorageVolumeRow> Volumes(List<Owner> owners)
    {
        var rows = new List<StorageVolumeRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var owner in owners)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(owner.Root));
                if (string.IsNullOrEmpty(root) || !seen.Add(root)) continue;
                var drive = new DriveInfo(root);
                if (!drive.IsReady) continue;
                rows.Add(new StorageVolumeRow(drive.Name, drive.AvailableFreeSpace, drive.TotalSize));
            }
            catch { /* an unreadable drive simply has no row */ }
        }
        return rows;
    }

    // ── the walk ─────────────────────────────────────────────────────────────

    private void WalkOwner(Ledger ledger, Owner owner, int index, IProgress<StorageProgress>? progress,
                           int total, CancellationToken ct)
    {
        ledger.StartOwner();

        if (owner.First is { } first)
        {
            var firstPath = Path.Combine(owner.Root, first);
            if (SafeDirExists(firstPath))
                WalkTree(ledger, owner, index, firstPath, null, progress, total, ct);
        }

        WalkTree(ledger, owner, index, owner.Root, owner.First, progress, total, ct);
    }

    /// <summary>Iterative walk that skips reparse points and any folder it can't read.</summary>
    /// <remarks>Reparse points aren't followed (as in <c>FileCleanupPanel.Walk</c>): the
    /// managed Java folder and some mod caches are junctions, and following them would
    /// measure the whole disk.</remarks>
    private void WalkTree(Ledger ledger, Owner owner, int ownerIndex, string start,
                          string? skipTopLevel, IProgress<StorageProgress>? progress, int total,
                          CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(start);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();

            FileSystemInfo[] entries;
            // GetFileSystemInfos throws up front rather than part-way through an enumeration, so an
            // unreadable folder skips itself instead of aborting the root.
            try { entries = new DirectoryInfo(dir).GetFileSystemInfos(); }
            catch { continue; }

            foreach (var info in entries)
            {
                try
                {
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;

                    if (info is DirectoryInfo sub)
                    {
                        if (skipTopLevel is not null
                            && string.Equals(sub.Name, skipTopLevel, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(Path.GetDirectoryName(sub.FullName), owner.Root,
                                             StringComparison.OrdinalIgnoreCase))
                            continue;      // already walked first
                        stack.Push(sub.FullName);
                    }
                    else if (info is FileInfo file)
                    {
                        Count(ledger, owner, ownerIndex, file);

                        if (ledger.FilesSeen % ProgressEvery == 0)
                            progress?.Report(new StorageProgress(ownerIndex, total, owner.Name,
                                                                 ledger.BytesSeen, ledger.FilesSeen));
                    }
                }
                catch { /* a file that vanished mid-walk simply does not count */ }
            }
        }
    }

    /// <summary>Records one file: gross bytes always, net bytes once its identity is known.</summary>
    /// <remarks>Gross is per name and added here. A single-name file's net bytes are added here too; a
    /// multi-name file is parked by id for <see cref="Ledger.Close"/>, which knows how many owners
    /// reach it. That way no bytes are ever added and then subtracted again.</remarks>
    private void Count(Ledger ledger, Owner owner, int ownerIndex, FileInfo file)
    {
        var length = file.Length;
        var rel = Relative(owner.Root, file.FullName);

        ledger.FilesSeen++;
        ledger.BytesSeen += length;
        ledger.OwnerGross[ownerIndex] += length;
        ledger.OwnerFiles[ownerIndex]++;

        var slot = Classify(owner, rel);
        var sighting = new Sighting(ownerIndex, slot.Category, slot.ItemKey, slot.ItemName,
                                    slot.ItemPath, RankOf(owner, rel));

        Reclaim(ledger, owner, rel, length);

        if (!ShouldProbe(owner, rel, length, ledger)) { ledger.Sole(sighting, length); return; }

        ledger.Probes++;
        if (!TryIdentify(file.FullName, out var id, out var links))
        {
            // Can't identify it, so count it once per name; that can only over-count.
            ledger.Unreadable++;
            ledger.Sole(sighting, length);
            return;
        }

        if (links <= 1) { ledger.Sole(sighting, length); return; }

        // Under local/, remember the relative path so the game/ copy is probed whatever its size.
        // Otherwise an unpacked config bundle's small files would count twice.
        if (owner.Kind == StorageOwnerKind.Instance
            && rel.StartsWith("local/", StringComparison.OrdinalIgnoreCase))
            ledger.LinkedLocalRels.Add(rel["local/".Length..]);

        ledger.Link(id, length, sighting);
    }

    /// <summary>Whether this file could be one of the launcher's hard links and is worth a handle open.
    /// See the class remarks.</summary>
    private static bool ShouldProbe(Owner owner, string rel, long length, Ledger ledger)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (ledger.Probes >= ProbeBudget) { ledger.Budget = true; return false; }

        // Walked once each and linked into by nothing, so no two names in this pass reach their bytes.
        if (owner.Kind is StorageOwnerKind.Runtime or StorageOwnerKind.Java or StorageOwnerKind.Profile)
            return false;

        if (owner.Kind == StorageOwnerKind.Library) return true;

        var first = Lower(Segment(rel, 0));
        if (first == "local") return true;
        if (first is not ("game" or "server-run")) return false;

        // Worlds are copied, never linked (ContentLibraryService.Materialise refuses to link one), and
        // logs and screenshots are written in place, so don't probe them.
        var second = Lower(Segment(rel, 1));
        if (second is "saves" or "logs" or "crash-reports" or "screenshots" or "debug"
                   or ".mixin.out" or "backups")
            return false;

        if (length >= ProbeFloorBytes) return true;

        // Below the floor, but the overlay linked this relative path from local/ into game/.
        return first == "game" && rel.Length > 5 && ledger.LinkedLocalRels.Contains(rel[5..]);
    }

    // ── classification ───────────────────────────────────────────────────────

    /// <summary>A file's category, and the "largest item" it rolls up into.</summary>
    private readonly record struct Slot(StorageCategory Category, string ItemKey, string ItemName,
                                        string ItemPath);

    /// <summary>Classifies one relative path.</summary>
    /// <remarks><c>local/</c> and <c>game/</c> map to the same item key, otherwise the same pack would
    /// show up twice in the largest-items list.</remarks>
    private static Slot Classify(Owner owner, string rel)
    {
        var first = Segment(rel, 0);

        switch (owner.Kind)
        {
            case StorageOwnerKind.Runtime:
                return new Slot(StorageCategory.Runtime, "runtime/" + Lower(first), Title(first),
                                Path.Combine(owner.Root, first));

            case StorageOwnerKind.Java:
                return new Slot(StorageCategory.Java, "java/" + Lower(first), first,
                                Path.Combine(owner.Root, first));

            case StorageOwnerKind.Profile:
                return new Slot(StorageCategory.Profile, "profile/" + Lower(first), first,
                                Path.Combine(owner.Root, first));

            case StorageOwnerKind.Library:
            {
                var entry = Segment(rel, 1);
                return new Slot(LibraryCategory(Lower(first)),
                                "library/" + Lower(first) + "/" + Lower(entry),
                                entry.Length == 0 ? "Shared-copy bookkeeping" : "'" + Pretty(entry) + "'",
                                Path.Combine(owner.Root, first, entry));
            }
        }

        // ── an instance ──
        switch (Lower(first))
        {
            case "server-run":
            case "server":
                return new Slot(StorageCategory.ServerFiles, "server", "Server files",
                                Path.Combine(owner.Root, first));
            case ".trash":
                return new Slot(StorageCategory.Other, "trash", "Deleted files (.trash)",
                                Path.Combine(owner.Root, ".trash"));
            case "game":
            case "local":
                break;
            default:
                return new Slot(StorageCategory.Other, "other", "Other files", owner.Root);
        }

        var game = Path.Combine(owner.Root, "game");
        var folder = Segment(rel, 1);
        var name = Segment(rel, 2);

        switch (Lower(folder))
        {
            case "mods":
                return new Slot(StorageCategory.Mods, "mods", "Mods", Path.Combine(game, "mods"));

            case "saves":
                return new Slot(StorageCategory.Worlds, "saves/" + Lower(name),
                                name.Length == 0 ? "Saves" : "World '" + name + "'",
                                Path.Combine(game, "saves", name));

            case "resourcepacks":
            case "texturepacks":
                return new Slot(StorageCategory.ResourcePacks, "rp/" + Lower(name),
                                name.Length == 0 ? "Resource packs" : "Resource pack '" + Pretty(name) + "'",
                                Path.Combine(game, folder, name));

            case "shaderpacks":
                return new Slot(StorageCategory.ShaderPacks, "sp/" + Lower(name),
                                name.Length == 0 ? "Shader packs" : "Shader pack '" + Pretty(name) + "'",
                                Path.Combine(game, "shaderpacks", name));

            case "config":
            case "defaultconfigs":
            case "kubejs":
            case "scripts":
            case "openloader":
                return new Slot(StorageCategory.ConfigScripts, "config", "Config & scripts",
                                Path.Combine(game, "config"));

            case "logs":
            case "crash-reports":
            case "debug":
            case ".mixin.out":
                return new Slot(StorageCategory.Logs, "logs", "Logs & crash reports",
                                Path.Combine(game, "logs"));

            case "screenshots":
                return new Slot(StorageCategory.Screenshots, "screenshots", "Screenshots",
                                Path.Combine(game, "screenshots"));

            default:
                return new Slot(StorageCategory.Other, "other", "Other files", game);
        }
    }

    private static StorageCategory LibraryCategory(string folder) => folder switch
    {
        "mods" => StorageCategory.Mods,
        "saves" => StorageCategory.Worlds,
        "resourcepacks" => StorageCategory.ResourcePacks,
        "shaderpacks" => StorageCategory.ShaderPacks,
        "configbundles" or "kubejsbundles" => StorageCategory.ConfigScripts,
        _ => StorageCategory.Other
    };

    // ── reclaimable ──────────────────────────────────────────────────────────

    /// <summary>Counts a file towards a reclaimable folder when it is in one.</summary>
    /// <remarks>Only the known folders from the class remarks. Don't add one because its name looks
    /// temporary; a wrong guess could cost someone a world.</remarks>
    private static void Reclaim(Ledger ledger, Owner owner, string rel, long length)
    {
        string? key = null, name = null, why = null, path = null;

        if (owner.Kind == StorageOwnerKind.Instance)
        {
            var first = Lower(Segment(rel, 0));
            var second = Lower(Segment(rel, 1));

            if (first == ".trash")
                (key, name, why, path) = (".trash", "Deleted files (.trash)",
                    "Files the launcher has already deleted for you. It keeps them so that a wrong "
                    + "delete costs a drag back in Explorer; emptying this is the last step of that "
                    + "delete.",
                    Path.Combine(owner.Root, ".trash"));
            else if (first == "server-run" && second == ".installers")
                (key, name, why, path) = ("installers", "Loader installers (server-run/.installers)",
                    "The NeoForge or Forge installer jar. The launcher downloads it again the next "
                    + "time it sets a server up and does not find it.",
                    Path.Combine(owner.Root, "server-run", ".installers"));
            else if (first == "game")
                switch (second)
                {
                    case "logs":
                        (key, name, why, path) = ("logs", "Game logs (game/logs)",
                            "Written fresh on every launch. Nothing reads them back - but they are "
                            + "also the crash you were about to read.",
                            Path.Combine(owner.Root, "game", "logs"));
                        break;
                    case "crash-reports":
                        (key, name, why, path) = ("crash", "Crash reports (game/crash-reports)",
                            "One file per crash, kept for ever. Worth reading before they go.",
                            Path.Combine(owner.Root, "game", "crash-reports"));
                        break;
                    case "debug":
                        (key, name, why, path) = ("debug", "Debug output (game/debug)",
                            "Written when a mod is asked for diagnostics. Nothing reads it back.",
                            Path.Combine(owner.Root, "game", "debug"));
                        break;
                    case ".mixin.out":
                        (key, name, why, path) = ("mixin", "Mixin dumps (game/.mixin.out)",
                            "Transformed classes a loader wrote out for debugging. Regenerated on "
                            + "demand.",
                            Path.Combine(owner.Root, "game", ".mixin.out"));
                        break;
                }
        }
        else if (owner.Kind == StorageOwnerKind.Profile)
        {
            var first = Lower(Segment(rel, 0));
            if (first == "scan")
                (key, name, why, path) = ("scan", "Remembered folder scans (scan/)",
                    "What each page found last time it walked a folder, so it can paint before it "
                    + "re-walks. A re-walk rebuilds all of it - the cache already deletes its own "
                    + "file whenever a row's shape changes.",
                    Path.Combine(owner.Root, "scan"));
            else if (first == "icon-cache")
                (key, name, why, path) = ("icons", "Downloaded icons (icon-cache/)",
                    "Mod and pack artwork, fetched again when a page needs it.",
                    Path.Combine(owner.Root, "icon-cache"));
            else if (first == "mod-fingerprints.json")
                (key, name, why, path) = ("fingerprints", "Mod fingerprints (mod-fingerprints.json)",
                    "Hashes of every jar you have and what the stores said they were. Re-derived by "
                    + "hashing the jars again, which is slow - so this one is worth keeping unless "
                    + "the space matters more than the wait.",
                    Path.Combine(owner.Root, "mod-fingerprints.json"));
        }

        if (key is null) return;

        var where = owner.Kind == StorageOwnerKind.Instance ? owner.Name : "Launcher profile";
        ledger.Reclaim(where + "\u0000" + key, name!, where, path!, why!, length);
    }

    // ── file identity ────────────────────────────────────────────────────────

    /// <summary>Volume serial and file index for one path, and how many names point at it.</summary>
    /// <remarks>Opened with no access rights and full sharing, so it never blocks a running game or
    /// fails on a jar the game has open. <c>FILE_FLAG_BACKUP_SEMANTICS</c> as in
    /// <see cref="PackFolderService"/>.</remarks>
    private static bool TryIdentify(string path, out FileId id, out uint links)
    {
        id = default;
        links = 0;
        try
        {
            using var handle = CreateFileW(path, 0, FileShare.ReadWrite | FileShare.Delete,
                                           IntPtr.Zero, FileMode.Open,
                                           FileAttributes.Normal | (FileAttributes)0x02000000,
                                           IntPtr.Zero);
            if (handle.IsInvalid) return false;
            if (!GetFileInformationByHandle(handle, out var info)) return false;

            id = new FileId(info.VolumeSerialNumber,
                            ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
            links = info.NumberOfLinks;
            return true;
        }
        catch { return false; }
    }

    private readonly record struct FileId(uint Volume, ulong Index);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, FileShare dwShareMode, IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition, FileAttributes dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);

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

    // ── the running total ────────────────────────────────────────────────────

    /// <param name="Rank">How canonical this name is (see <see cref="RankOf"/>). A shared file is filed
    /// under its lowest-ranked sighting.</param>
    private readonly record struct Sighting(int Owner, StorageCategory Category, string ItemKey,
                                            string ItemName, string ItemPath, byte Rank);

    /// <summary>Which of a file's names its bytes are filed under.</summary>
    /// <remarks>Walk order depends on directory enumeration, so "first seen" would vary between passes.
    /// Ranked instead: the library copy, then <c>local/</c>, then <c>game/</c>, then
    /// <c>server-run/</c>.</remarks>
    private static byte RankOf(Owner owner, string rel) => owner.Kind switch
    {
        StorageOwnerKind.Library => 0,
        StorageOwnerKind.Instance => Lower(Segment(rel, 0)) switch
        {
            "local" => 1,
            "game" => 2,
            "server-run" or "server" => 3,
            _ => 4
        },
        _ => 4
    };

    /// <summary>One physical file with more than one name, and every owner that reaches it.</summary>
    private sealed class Physical
    {
        public long Length;
        public Sighting First;
        public int Sightings;
        /// <summary>Distinct owner indices. A list because it's almost always one or two long, and a
        /// HashSet per shared file would mean tens of thousands of allocations.</summary>
        public readonly List<int> Owners = [];
    }

    private sealed class Item
    {
        public string Name = "";
        public string Path = "";
        public int Owner;
        public long Bytes;
        public int Files;
        public bool Shared;
    }

    private sealed class ReclaimBucket
    {
        public string Name = "";
        public string Where = "";
        public string Path = "";
        public string Evidence = "";
        public long Bytes;
        public int Files;
    }

    /// <summary>Everything one pass accumulates. Only one walk uses it, so nothing locks.</summary>
    private sealed class Ledger(int owners, List<string> problems)
    {
        private const int Categories = (int)StorageCategory.Other + 1;

        public readonly long[] OwnerUnique = new long[owners];
        public readonly long[] OwnerShared = new long[owners];
        public readonly long[] OwnerGross = new long[owners];
        public readonly int[] OwnerFiles = new int[owners];

        private readonly long[] _catBytes = new long[Categories];
        private readonly int[] _catFiles = new int[Categories];
        private readonly Dictionary<FileId, Physical> _physicals = [];
        private readonly Dictionary<string, Item> _items = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ReclaimBucket> _reclaim = new(StringComparer.Ordinal);

        /// <summary>Paths in the current instance whose <c>local/</c> copy is a link, so their
        /// <c>game/</c> counterparts are probed however small. Cleared per instance by
        /// <see cref="StartOwner"/>.</summary>
        public readonly HashSet<string> LinkedLocalRels = new(StringComparer.OrdinalIgnoreCase);

        public long FilesSeen;
        public long BytesSeen;
        public int Probes;
        public int Unreadable;
        public bool Budget;

        public void StartOwner() => LinkedLocalRels.Clear();

        /// <summary>Adds the bytes of a physical file that only one owner reaches.</summary>
        public void Sole(in Sighting s, long length)
        {
            OwnerUnique[s.Owner] += length;
            _catBytes[(int)s.Category] += length;
            _catFiles[(int)s.Category]++;
            Bank(s, length, shared: false);
        }

        /// <summary>Records one name of a multi-name file. Its bytes are banked by
        /// <see cref="Close"/>, once, when the owner set is finally known.</summary>
        public void Link(FileId id, long length, in Sighting s)
        {
            if (!_physicals.TryGetValue(id, out var physical))
                _physicals[id] = physical = new Physical { Length = length, First = s };
            else if (s.Rank < physical.First.Rank)
                physical.First = s;   // a more canonical name for the same bytes, see RankOf
            physical.Sightings++;
            if (!physical.Owners.Contains(s.Owner)) physical.Owners.Add(s.Owner);
        }

        public void Reclaim(string key, string name, string where, string path, string evidence,
                            long length)
        {
            if (!_reclaim.TryGetValue(key, out var bucket))
                _reclaim[key] = bucket = new ReclaimBucket
                {
                    Name = name, Where = where, Path = path, Evidence = evidence
                };
            bucket.Bytes += length;
            bucket.Files++;
        }

        private void Bank(in Sighting s, long length, bool shared)
        {
            var key = s.Owner + "\u0000" + s.ItemKey;
            if (!_items.TryGetValue(key, out var item))
                _items[key] = item = new Item { Name = s.ItemName, Path = s.ItemPath, Owner = s.Owner };
            item.Bytes += length;
            item.Files++;
            if (shared) item.Shared = true;
        }

        /// <summary>Turns the pass into a report: resolves every multi-name file, then sorts.</summary>
        /// <remarks>A multi-name file whose names are all inside one owner (an instance's
        /// <c>local/</c> and <c>game/</c>) counts as that owner's unique bytes, since deleting the
        /// instance would free it.</remarks>
        public StorageReport Close(List<Owner> owners, List<StorageVolumeRow> volumes)
        {
            long sharedBytes = 0;
            var sharedFiles = 0;

            foreach (var physical in _physicals.Values)
            {
                if (physical.Owners.Count <= 1)
                {
                    Sole(physical.First, physical.Length);
                    continue;
                }

                sharedBytes += physical.Length;
                sharedFiles++;
                _catBytes[(int)physical.First.Category] += physical.Length;
                _catFiles[(int)physical.First.Category]++;
                Bank(physical.First, physical.Length, shared: true);
                foreach (var owner in physical.Owners) OwnerShared[owner] += physical.Length;
            }

            var ownerRows = new List<StorageOwnerRow>();
            for (var i = 0; i < owners.Count; i++)
                ownerRows.Add(new StorageOwnerRow(owners[i].Kind, owners[i].PackId, owners[i].Name,
                                                  owners[i].Root, OwnerUnique[i], OwnerShared[i],
                                                  OwnerGross[i], OwnerFiles[i]));

            var categoryRows = new List<StorageCategoryRow>();
            for (var c = 0; c < Categories; c++)
                if (_catBytes[c] > 0)
                    categoryRows.Add(new StorageCategoryRow((StorageCategory)c,
                                                            CategoryName((StorageCategory)c),
                                                            CategoryNote((StorageCategory)c),
                                                            _catBytes[c], _catFiles[c]));

            var itemRows = _items.Values
                .OrderByDescending(i => i.Bytes)
                .Take(MaxItems)
                .Select(i => new StorageItemRow(
                    i.Name,
                    i.Owner < owners.Count ? owners[i.Owner].Name : "",
                    i.Path, i.Bytes, i.Files, i.Shared))
                .ToList();

            var reclaimRows = _reclaim.Values
                .Where(b => b.Bytes > 0)
                .OrderByDescending(b => b.Bytes)
                .Select(b => new StorageReclaimRow(b.Name, b.Where, b.Path, b.Evidence, b.Bytes, b.Files))
                .ToList();

            if (Budget)
                problems.Add($"Stopped checking for shared files after {ProbeBudget:N0} of them, so "
                             + "the total may read a little high.");
            if (Unreadable > 0)
                problems.Add($"{Unreadable:N0} file(s) were locked or unreadable and are counted once "
                             + "per name.");

            return new StorageReport(
                DateTimeOffset.UtcNow,
                OnDiskBytes: OwnerUnique.Sum() + sharedBytes,
                // Gross was summed per path during the walk: already the folder-by-folder number.
                GrossBytes: BytesSeen,
                sharedBytes, sharedFiles, FilesSeen,
                Approximate: Budget,
                ownerRows.OrderBy(r => r.Kind == StorageOwnerKind.Instance ? 0 : 1)
                         .ThenByDescending(r => r.Kind == StorageOwnerKind.Instance ? r.UniqueBytes : 0)
                         .ThenBy(r => (int)r.Kind)
                         .ToList(),
                categoryRows.OrderByDescending(r => r.Bytes).ToList(),
                itemRows, reclaimRows, volumes, problems);
        }
    }

    // ── words ────────────────────────────────────────────────────────────────

    public static string CategoryName(StorageCategory category) => category switch
    {
        StorageCategory.Mods => "Mods",
        StorageCategory.Worlds => "Worlds & saves",
        StorageCategory.ResourcePacks => "Resource packs",
        StorageCategory.ShaderPacks => "Shader packs",
        StorageCategory.ConfigScripts => "Config & scripts",
        StorageCategory.Logs => "Logs & crash reports",
        StorageCategory.Screenshots => "Screenshots",
        StorageCategory.ServerFiles => "Server files",
        StorageCategory.Runtime => "Minecraft runtime",
        StorageCategory.Java => "Java runtimes",
        StorageCategory.Profile => "Launcher profile",
        _ => "Everything else"
    };

    /// <summary>The line under a category name, saying what makes that category different.</summary>
    public static string CategoryNote(StorageCategory category) => category switch
    {
        StorageCategory.Mods => "Jars. One copy of the bytes wherever two instances use the same "
                              + "shared copy.",
        StorageCategory.Worlds => "Your saves. Never shared between instances - a world the launcher keeps "
                                + "is a template that gets copied in, so every instance owns its own.",
        StorageCategory.ResourcePacks => "One copy of the bytes however many instances use the pack.",
        StorageCategory.ShaderPacks => "One copy of the bytes however many instances use the pack.",
        StorageCategory.ConfigScripts => "config, defaultconfigs, kubejs. Small files, a great many of them.",
        StorageCategory.Logs => "Rewritten on every launch.",
        StorageCategory.Screenshots => "Yours. Nothing here or anywhere else in the launcher deletes them.",
        StorageCategory.ServerFiles => "server-run. The jars in it are the same bytes the client runs.",
        StorageCategory.Runtime => "Libraries, assets and version jars - global, and reused by every "
                                 + "instance. Not divided among them, because none of them owns it.",
        StorageCategory.Java => "Downloaded when a pack needs a Java the system does not have. Global.",
        StorageCategory.Profile => "Settings, and the launcher's own caches.",
        _ => "Backups, deleted files, loose files in an instance."
    };

    /// <summary>Segoe MDL2 glyph for a category, reusing glyphs the launcher already uses for the same
    /// things.</summary>
    public static string Glyph(StorageCategory category) => category switch
    {
        StorageCategory.Mods => "",
        StorageCategory.Worlds => "",
        StorageCategory.ResourcePacks => "",
        StorageCategory.ShaderPacks => "",
        StorageCategory.ConfigScripts => "",
        StorageCategory.Logs => "",
        StorageCategory.Screenshots => "",
        StorageCategory.ServerFiles => "",
        StorageCategory.Runtime => "",
        StorageCategory.Java => "",
        StorageCategory.Profile => "",
        _ => ""
    };

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>Forward-slashed path relative to a root, original case. Every comparison against it
    /// is case-insensitive, so the display name keeps the capitals the user gave a world.</summary>
    private static string Relative(string root, string full)
    {
        try { return Path.GetRelativePath(root, full).Replace('\\', '/'); }
        catch { return full.Replace('\\', '/'); }
    }

    /// <summary>The nth '/'-separated segment, or "" when the path is shorter than that.</summary>
    private static string Segment(string rel, int index)
    {
        var start = 0;
        for (var i = 0; i < index; i++)
        {
            var next = rel.IndexOf('/', start);
            if (next < 0) return "";
            start = next + 1;
        }
        var end = rel.IndexOf('/', start);
        return end < 0 ? rel[start..] : rel[start..end];
    }

    private static string Lower(string text) => text.ToLowerInvariant();

    private static string Title(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>A file name as a heading: archive extension off, separators to spaces. Like
    /// <see cref="ShaderPackService.PrettyName"/>, but also handles folders, worlds and library
    /// entries.</summary>
    private static string Pretty(string name)
    {
        var text = name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
            ? name[..^4] : name;
        return text.Replace('_', ' ').Trim();
    }

    private static bool SafeDirExists(string path)
    {
        try { return Directory.Exists(path); }
        catch { return false; }
    }

    // ── the cached shape ─────────────────────────────────────────────────────

    /// <summary>One persisted line of a report, tagged with the list it came from.</summary>
    /// <remarks>Flat rows because <see cref="ScanCache{T}"/> limits size by row count. <c>RowShape</c>
    /// hashes this type's members, so adding a field discards the old cache file.</remarks>
    public sealed record StorageCacheRow
    {
        public byte Tag { get; init; }
        public int Kind { get; init; }
        public int Files { get; init; }
        public long Bytes { get; init; }
        public long Bytes2 { get; init; }
        public long Bytes3 { get; init; }
        public long Count { get; init; }
        public bool Flag { get; init; }
        public string Name { get; init; } = "";
        public string Where { get; init; } = "";
        public string Path { get; init; } = "";
        public string Note { get; init; } = "";
    }

    private const byte TagTotals = 0, TagOwner = 1, TagCategory = 2, TagItem = 3,
                       TagReclaim = 4, TagVolume = 5, TagProblem = 6;

    private static List<StorageCacheRow> Dehydrate(StorageReport report)
    {
        var rows = new List<StorageCacheRow>
        {
            new()
            {
                Tag = TagTotals, Flag = report.Approximate, Bytes = report.OnDiskBytes,
                Bytes2 = report.GrossBytes, Bytes3 = report.SharedBytes,
                Files = report.SharedFiles, Count = report.TotalFiles
            }
        };
        foreach (var o in report.Owners)
            rows.Add(new StorageCacheRow
            {
                Tag = TagOwner, Kind = (int)o.Kind, Bytes = o.UniqueBytes, Bytes2 = o.SharedBytes,
                Bytes3 = o.GrossBytes, Files = o.Files, Name = o.Name, Path = o.Path,
                Note = o.PackId.ToString("N")
            });
        foreach (var c in report.Categories)
            rows.Add(new StorageCacheRow
            {
                Tag = TagCategory, Kind = (int)c.Category, Bytes = c.Bytes, Files = c.Files
            });
        foreach (var i in report.Items)
            rows.Add(new StorageCacheRow
            {
                Tag = TagItem, Flag = i.IsShared, Bytes = i.Bytes, Files = i.Files,
                Name = i.Name, Where = i.Where, Path = i.Path
            });
        foreach (var r in report.Reclaimable)
            rows.Add(new StorageCacheRow
            {
                Tag = TagReclaim, Bytes = r.Bytes, Files = r.Files, Name = r.Name,
                Where = r.Where, Path = r.Path, Note = r.Evidence
            });
        foreach (var v in report.Volumes)
            rows.Add(new StorageCacheRow
            {
                Tag = TagVolume, Bytes = v.FreeBytes, Bytes2 = v.TotalBytes, Name = v.Name
            });
        foreach (var p in report.Problems)
            rows.Add(new StorageCacheRow { Tag = TagProblem, Name = p });
        return rows;
    }

    private static StorageReport Rehydrate(IReadOnlyList<StorageCacheRow> rows, DateTimeOffset taken)
    {
        long onDisk = 0, gross = 0, sharedBytes = 0, totalFiles = 0;
        var sharedFiles = 0;
        var approximate = false;
        var owners = new List<StorageOwnerRow>();
        var categories = new List<StorageCategoryRow>();
        var items = new List<StorageItemRow>();
        var reclaim = new List<StorageReclaimRow>();
        var volumes = new List<StorageVolumeRow>();
        var problems = new List<string>();

        foreach (var row in rows)
            switch (row.Tag)
            {
                case TagTotals:
                    approximate = row.Flag;
                    onDisk = row.Bytes;
                    gross = row.Bytes2;
                    sharedBytes = row.Bytes3;
                    sharedFiles = row.Files;
                    totalFiles = row.Count;
                    break;
                case TagOwner:
                    owners.Add(new StorageOwnerRow((StorageOwnerKind)row.Kind,
                                                   Guid.TryParse(row.Note, out var id) ? id : Guid.Empty,
                                                   row.Name, row.Path, row.Bytes, row.Bytes2,
                                                   row.Bytes3, row.Files));
                    break;
                case TagCategory:
                {
                    var category = (StorageCategory)row.Kind;
                    categories.Add(new StorageCategoryRow(category, CategoryName(category),
                                                          CategoryNote(category), row.Bytes, row.Files));
                    break;
                }
                case TagItem:
                    items.Add(new StorageItemRow(row.Name, row.Where, row.Path, row.Bytes, row.Files,
                                                 row.Flag));
                    break;
                case TagReclaim:
                    reclaim.Add(new StorageReclaimRow(row.Name, row.Where, row.Path, row.Note,
                                                      row.Bytes, row.Files));
                    break;
                case TagVolume:
                    volumes.Add(new StorageVolumeRow(row.Name, row.Bytes, row.Bytes2));
                    break;
                case TagProblem:
                    problems.Add(row.Name);
                    break;
            }

        return new StorageReport(taken, onDisk, gross, sharedBytes, sharedFiles, totalFiles,
                                 approximate, owners, categories, items, reclaim, volumes, problems);
    }
}
