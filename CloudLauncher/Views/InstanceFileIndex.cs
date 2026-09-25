using System.IO;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>
/// An in-memory picture of an instance folder (every file, its size and date, and which rule covers
/// it), read once and shared by every <see cref="FolderView"/> looking at that folder.
/// </summary>
/// <remarks>
/// <para>Walking <c>game/</c> per pane on every refresh is slow on a modded instance. Here a folder's
/// rows are its node's children, its size is a cached subtree sum, a rule change is one pass over the
/// file nodes, and a file operation re-reads only the directory it touched.</para>
/// <para>A folder is shared, local or ignored by its files: every visible file shared means shared,
/// some means partly shared, every file ignored means ignored. Kept as per-node counts.</para>
/// <para>Threading: disk reads run on the thread calling <see cref="Scan"/> or
/// <see cref="RescanDirectories"/>; results are swapped in under <see cref="_gate"/>, which listings
/// also take, so a listing never sees a half-replaced directory. <see cref="_mutate"/> serialises
/// mutations so a re-classification can't interleave with a re-scan. Callers keep the disk work off
/// the UI thread.</para>
/// </remarks>
public sealed class InstanceFileIndex
{
    /// <summary>A file, as the scan found it, plus the rule it matched at the last classification.</summary>
    public sealed class FileNode
    {
        internal FileNode(string rel, string name, long size, DateTime modified, bool isPrivate)
        {
            Rel = rel;
            Name = name;
            Size = size;
            Modified = modified;
            IsPrivate = isPrivate;
            Kind = FolderEntry.KindOf(rel);
        }

        public string Rel { get; }
        public string Name { get; }
        public long Size { get; }
        public DateTime Modified { get; }
        public FolderEntryKind Kind { get; }
        /// <summary>Held back by <see cref="PrivateAssetPolicy"/>. Path-only, so fixed at scan time.</summary>
        public bool IsPrivate { get; }
        public RuleMatchResult Match { get; internal set; } = NoRule;
    }

    /// <summary>A directory and the counts over its whole subtree that the panes' filters need.</summary>
    public sealed class DirNode
    {
        internal DirNode(string rel, string name, DateTime modified)
        {
            Rel = rel;
            Name = name;
            Modified = modified;
        }

        /// <summary>'/'-separated, no trailing slash, empty for the root.</summary>
        public string Rel { get; }
        public string Name { get; }
        public DirNode? Parent { get; internal set; }
        public DateTime Modified { get; internal set; }
        public List<FileNode> Files { get; internal set; } = [];
        public List<DirNode> Dirs { get; internal set; } = [];

        /// <summary>Files anywhere under this folder.</summary>
        public int FileCount { get; internal set; }
        /// <summary>Of those, the ones no Ignored rule covers.</summary>
        public int VisibleFiles { get; internal set; }
        public int SharedFiles { get; internal set; }
        public int LocalFiles { get; internal set; }

        public bool IsFullyShared => VisibleFiles > 0 && SharedFiles == VisibleFiles;
        public bool IsFullyLocal => VisibleFiles > 0 && LocalFiles == VisibleFiles;
        public bool IsFullyIgnored => FileCount > 0 && VisibleFiles == 0;

        // Cached subtree sums; null until asked for, dropped whenever the subtree or the rules change.
        internal long? SizeAll;
        internal long? SizeShared;
    }

    /// <summary>What a <see cref="FolderView"/> wants left out of a listing.</summary>
    public sealed class ListingOptions
    {
        public bool ShowOnlyShared { get; init; }
        public bool HideShared { get; init; }
        public bool ShowIgnored { get; init; }
        public bool MeasureFolders { get; init; }
        /// <summary>Paths mirrored elsewhere, hidden from this listing. Null when the view has no
        /// mirror.</summary>
        public HashSet<string>? Mirrored { get; init; }
        /// <summary>A name filter for the open folder; empty for none.</summary>
        public string Filter { get; init; } = "";
    }

    private static readonly RuleMatchResult NoRule = new();
    private const int MaxDepth = 48;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _mutate = new(1, 1);
    private readonly PackRuleService _ruleService;
    private readonly AppSettings _settings;
    private DirNode _root;
    private List<PackRule>? _rules;

    private InstanceFileIndex(string root, PackRuleService ruleService, AppSettings settings)
    {
        Root = root;
        _ruleService = ruleService;
        _settings = settings;
        _root = new DirNode("", "", default);
    }

    /// <summary>The absolute folder this index describes.</summary>
    public string Root { get; }

    public DateTime ScannedAt { get; private set; }

    /// <summary>Files anywhere under the root, from the last scan plus every re-scan since.</summary>
    public int FileCount { get { lock (_gate) return _root.FileCount; } }

    /// <summary>Files a Shared rule covers, as classified.</summary>
    public int SharedFileCount { get { lock (_gate) return _root.SharedFiles; } }

    /// <summary>True when two roots name the same folder, so a view can tell whether the index it was
    /// handed is the one for the folder it shows.</summary>
    public static bool SameRoot(string a, string b)
    {
        static string Norm(string p)
        {
            try { return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch { return p; }
        }
        return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);
    }

    // ── scanning ─────────────────────────────────────────────────────────────

    /// <summary>Reads the whole tree once. Blocking; call from a worker thread.</summary>
    /// <remarks>One directory enumeration per folder, taking size and date from the enumeration itself
    /// instead of a <c>FileInfo</c> per file. Hidden and system files are included.</remarks>
    public static InstanceFileIndex Scan(string root, List<PackRule>? rules, PackRuleService ruleService,
                                         AppSettings settings, CancellationToken ct = default,
                                         IProgress<int>? progress = null)
    {
        var index = new InstanceFileIndex(root, ruleService, settings);
        var counter = new Counter(progress);
        var tree = Directory.Exists(root)
            ? index.ScanDir(root, "", "", 0, ct, counter)
            : new DirNode("", "", default);
        index.ClassifyTree(tree, rules);
        index._root = tree;
        index._rules = rules;
        index.ScannedAt = DateTime.Now;
        return index;
    }

    private sealed class Counter(IProgress<int>? progress)
    {
        public int Files;
        public void Tick()
        {
            Files++;
            if (progress is not null && Files % 500 == 0) progress.Report(Files);
        }
    }

    private DirNode ScanDir(string abs, string rel, string name, int depth, CancellationToken ct, Counter? counter)
    {
        var di = new DirectoryInfo(abs);
        var node = new DirNode(rel, name, SafeLastWrite(di));
        if (depth > MaxDepth) return node;   // a junction loop, not a real tree

        IEnumerable<FileSystemInfo> entries;
        try { entries = di.EnumerateFileSystemInfos(); }
        catch { return node; }   // unreadable: listed as empty rather than failing the whole read

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var childRel = rel.Length == 0 ? entry.Name : rel + "/" + entry.Name;
            if (entry is DirectoryInfo sub)
            {
                var child = ScanDir(sub.FullName, childRel, sub.Name, depth + 1, ct, counter);
                child.Parent = node;
                node.Dirs.Add(child);
            }
            else if (entry is FileInfo fi)
            {
                node.Files.Add(NewFile(childRel, fi));
                counter?.Tick();
            }
        }

        SortChildren(node);
        return node;
    }

    private FileNode NewFile(string rel, FileInfo fi) =>
        new(rel, fi.Name, SafeLength(fi), SafeLastWrite(fi), PrivateAssetPolicy.IsPrivate(rel, _settings));

    private static void SortChildren(DirNode node)
    {
        node.Dirs.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        node.Files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
    }

    /// <summary>A file can vanish between the enumeration and the stat; a missing size is not worth
    /// failing a folder over.</summary>
    private static long SafeLength(FileInfo fi)
    {
        try { return fi.Length; }
        catch { return 0; }
    }

    private static DateTime SafeLastWrite(FileSystemInfo info)
    {
        try { return info.LastWriteTime; }
        catch { return default; }
    }

    // ── classification ───────────────────────────────────────────────────────

    /// <summary>True when the index was last classified against rules that say what these do, so a
    /// caller can skip a re-classification that would change nothing.</summary>
    public bool RulesAre(List<PackRule>? rules)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_rules, rules)) return true;
            if (_rules is null || rules is null) return (_rules?.Count ?? 0) == (rules?.Count ?? 0);
            if (_rules.Count != rules.Count) return false;
            for (var i = 0; i < rules.Count; i++)
            {
                if (_rules[i].Action != rules[i].Action) return false;
                if (!string.Equals(_rules[i].Pattern, rules[i].Pattern, StringComparison.Ordinal)) return false;
            }
            return true;
        }
    }

    /// <summary>Re-runs the rule matcher over every file. No disk access. Blocking; call from a worker
    /// thread on a large instance.</summary>
    /// <remarks>Matches are computed outside the lock and assigned inside it, together with the folder
    /// counts, so a concurrent listing sees the old or the new classification, never a mix.</remarks>
    public void Reclassify(List<PackRule>? rules)
    {
        _mutate.Wait();
        try
        {
            FileNode[] files;
            lock (_gate) files = AllFiles(_root).ToArray();

            var matches = new RuleMatchResult[files.Length];
            for (var i = 0; i < files.Length; i++)
                matches[i] = rules is null ? NoRule : _ruleService.Match(files[i].Rel, rules);

            lock (_gate)
            {
                for (var i = 0; i < files.Length; i++) files[i].Match = matches[i];
                _rules = rules;
                Aggregate(_root, dropSizes: false);
            }
        }
        finally { _mutate.Release(); }
    }

    private void ClassifyTree(DirNode node, List<PackRule>? rules)
    {
        foreach (var file in AllFiles(node))
            file.Match = rules is null ? NoRule : _ruleService.Match(file.Rel, rules);
        Aggregate(node, dropSizes: true);
    }

    /// <summary>Subtree counts, bottom-up. Sizes are dropped only when the files changed; a
    /// classification change invalidates the shared sum alone.</summary>
    private static void Aggregate(DirNode node, bool dropSizes)
    {
        foreach (var dir in node.Dirs) Aggregate(dir, dropSizes);
        AggregateShallow(node, dropSizes);
    }

    private static void AggregateShallow(DirNode node, bool dropSizes)
    {
        int files = 0, visible = 0, shared = 0, local = 0;
        foreach (var f in node.Files)
        {
            files++;
            if (f.Match.IsIgnored) continue;
            visible++;
            if (f.Match.IsAutoShared) shared++;
            else if (f.Match.IsAutoLocal) local++;
        }
        foreach (var d in node.Dirs)
        {
            files += d.FileCount;
            visible += d.VisibleFiles;
            shared += d.SharedFiles;
            local += d.LocalFiles;
        }
        node.FileCount = files;
        node.VisibleFiles = visible;
        node.SharedFiles = shared;
        node.LocalFiles = local;
        node.SizeShared = null;
        if (dropSizes) node.SizeAll = null;
    }

    // ── re-scanning one directory ────────────────────────────────────────────

    /// <summary>
    /// Re-reads the named directories, one level each, after something changed inside them.
    /// Blocking; call from a worker thread.
    /// </summary>
    /// <remarks>
    /// <para>Shallow, so a rename in <c>config/</c> doesn't re-walk <c>mods/</c>. A sub-folder that
    /// still exists keeps its node, a new one is read in full (moved or extracted folders), and a
    /// missing one is dropped. A directory that no longer exists is detached from its parent.</para>
    /// <para>A path the index has never seen (its parent was created after the scan) falls back to
    /// re-reading the nearest known ancestor.</para>
    /// </remarks>
    public void RescanDirectories(IEnumerable<string> dirRels, CancellationToken ct = default)
    {
        _mutate.Wait();
        try
        {
            var wanted = dirRels.Select(Normalize)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(r => r.Length)   // parents first: a freshly read child is then only re-read shallowly
                .ToList();
            foreach (var rel in wanted) RescanOne(rel, ct);
        }
        finally { _mutate.Release(); }
    }

    private void RescanOne(string rel, CancellationToken ct)
    {
        DirNode? node;
        List<PackRule>? rules;
        lock (_gate)
        {
            node = Find(rel);
            rules = _rules;
        }

        if (node is null)
        {
            var parent = ParentOf(rel);
            if (parent.Length < rel.Length) RescanOne(parent, ct);
            return;
        }

        var abs = AbsolutePath(rel);
        if (!Directory.Exists(abs))
        {
            lock (_gate)
            {
                if (node.Parent is { } p)
                {
                    p.Dirs.Remove(node);
                    node.Parent = null;
                    ReaggregateUp(p);
                }
                else
                {
                    node.Files = [];
                    node.Dirs = [];
                    ReaggregateUp(node);
                }
            }
            return;
        }

        var di = new DirectoryInfo(abs);
        var files = new List<FileNode>();
        var dirs = new List<DirNode>();
        var existing = new Dictionary<string, DirNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in node.Dirs) existing[d.Name] = d;

        IEnumerable<FileSystemInfo> entries;
        try { entries = di.EnumerateFileSystemInfos(); }
        catch { entries = []; }

        var depth = rel.Length == 0 ? 0 : rel.Count(c => c == '/') + 1;
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var childRel = rel.Length == 0 ? entry.Name : rel + "/" + entry.Name;
            if (entry is DirectoryInfo sub)
            {
                if (existing.TryGetValue(sub.Name, out var kept))
                {
                    kept.Modified = SafeLastWrite(sub);
                    dirs.Add(kept);
                }
                else
                {
                    var fresh = ScanDir(sub.FullName, childRel, sub.Name, depth + 1, ct, null);
                    ClassifyTree(fresh, rules);
                    dirs.Add(fresh);
                }
            }
            else if (entry is FileInfo fi)
            {
                var file = NewFile(childRel, fi);
                file.Match = rules is null ? NoRule : _ruleService.Match(childRel, rules);
                files.Add(file);
            }
        }

        var swapped = new DirNode(rel, node.Name, SafeLastWrite(di)) { Files = files, Dirs = dirs };
        SortChildren(swapped);

        lock (_gate)
        {
            node.Files = swapped.Files;
            node.Dirs = swapped.Dirs;
            node.Modified = swapped.Modified;
            foreach (var d in node.Dirs) d.Parent = node;
            ReaggregateUp(node);
        }
    }

    /// <summary>Recomputes the counts on a node and every ancestor, dropping their cached sizes.</summary>
    private static void ReaggregateUp(DirNode node)
    {
        for (var n = node; n is not null; n = n.Parent)
        {
            AggregateShallow(n, dropSizes: true);
        }
    }

    // ── lookups ──────────────────────────────────────────────────────────────

    /// <summary>The node for a relative folder path, or null if the index has no such folder.</summary>
    public DirNode? Find(string dirRel)
    {
        lock (_gate)
        {
            var rel = Normalize(dirRel);
            var node = _root;
            if (rel.Length == 0) return node;
            foreach (var part in rel.Split('/'))
            {
                DirNode? next = null;
                foreach (var d in node.Dirs)
                {
                    if (string.Equals(d.Name, part, StringComparison.OrdinalIgnoreCase)) { next = d; break; }
                }
                if (next is null) return null;
                node = next;
            }
            return node;
        }
    }

    /// <summary>The nearest folder the index knows on the way up from <paramref name="dirRel"/>.</summary>
    public string NearestExistingFolder(string dirRel)
    {
        var rel = Normalize(dirRel);
        while (rel.Length > 0 && Find(rel) is null) rel = ParentOf(rel);
        return rel;
    }

    /// <summary>Every file's path relative to the root, in tree order.</summary>
    public List<string> AllRelativeFiles()
    {
        lock (_gate) return AllFiles(_root).Select(f => f.Rel).ToList();
    }

    private static IEnumerable<FileNode> AllFiles(DirNode node)
    {
        foreach (var f in node.Files) yield return f;
        foreach (var d in node.Dirs)
            foreach (var f in AllFiles(d)) yield return f;
    }

    /// <summary>Bytes under a folder, cached per node until something in it changes.</summary>
    /// <param name="sharedOnly">Count only files a Shared rule covers, which is what the Shared pane
    /// shows as a folder's size (what would upload).</param>
    public long SizeOf(DirNode node, bool sharedOnly)
    {
        lock (_gate) return sharedOnly ? SizeSharedOf(node) : SizeAllOf(node);
    }

    private static long SizeAllOf(DirNode n)
    {
        if (n.SizeAll is { } cached) return cached;
        long total = 0;
        foreach (var f in n.Files) total += f.Size;
        foreach (var d in n.Dirs) total += SizeAllOf(d);
        n.SizeAll = total;
        return total;
    }

    private static long SizeSharedOf(DirNode n)
    {
        if (n.SizeShared is { } cached) return cached;
        long total = 0;
        foreach (var f in n.Files) if (f.Match.IsAutoShared) total += f.Size;
        foreach (var d in n.Dirs) total += SizeSharedOf(d);
        n.SizeShared = total;
        return total;
    }

    // ── listings ─────────────────────────────────────────────────────────────

    /// <summary>The rows for one folder, or null when the index has no such folder any more.</summary>
    public List<FolderEntry>? List(string dirRel, ListingOptions o)
    {
        lock (_gate)
        {
            var node = Find(dirRel);
            if (node is null) return null;

            var entries = new List<FolderEntry>(node.Dirs.Count + node.Files.Count);
            foreach (var d in node.Dirs)
            {
                if (o.ShowOnlyShared && d.SharedFiles == 0) continue;
                if (o.HideShared && d.IsFullyShared) continue;
                if (o.Mirrored is not null && IsFullyMirrored(d, o.Mirrored)) continue;
                if (o.Filter.Length > 0 && !d.Name.Contains(o.Filter, StringComparison.OrdinalIgnoreCase)) continue;
                entries.Add(FolderEntryFor(d, o));
            }
            foreach (var f in node.Files)
            {
                if (!Passes(f, o)) continue;
                if (o.Filter.Length > 0 && !f.Name.Contains(o.Filter, StringComparison.OrdinalIgnoreCase)) continue;
                entries.Add(FileEntryFor(f, f.Name));
            }
            return entries;
        }
    }

    /// <summary>Every file whose relative path contains <paramref name="query"/>, wherever it lives,
    /// with the path as its display name. Stops at <paramref name="cap"/> hits.</summary>
    public List<FolderEntry> Search(string query, ListingOptions o, int cap)
    {
        lock (_gate)
        {
            var entries = new List<FolderEntry>();
            foreach (var f in AllFiles(_root))
            {
                if (entries.Count >= cap) break;
                if (!f.Rel.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Passes(f, o)) continue;
                entries.Add(FileEntryFor(f, f.Rel));
            }
            return entries;
        }
    }

    private static bool Passes(FileNode f, ListingOptions o)
    {
        var m = f.Match;
        if (m.IsIgnored && !o.ShowIgnored) return false;
        if (o.ShowOnlyShared && !m.IsAutoShared) return false;
        if (o.HideShared && m.IsAutoShared) return false;
        if (o.Mirrored is not null && o.Mirrored.Contains(f.Rel)) return false;
        return true;
    }

    private static bool IsFullyMirrored(DirNode d, HashSet<string> mirrored)
    {
        var any = false;
        foreach (var f in AllFiles(d))
        {
            if (f.Match.IsIgnored) continue;
            any = true;
            if (!mirrored.Contains(f.Rel)) return false;
        }
        return any;
    }

    private static FolderEntry FolderEntryFor(DirNode d, ListingOptions o)
    {
        var shared = d.IsFullyShared;
        var ignored = d.IsFullyIgnored;
        var local = d.IsFullyLocal;
        return new FolderEntry
        {
            DisplayName  = d.Name + "/",
            RelativePath = d.Rel,
            IsFolder     = true,
            Kind         = FolderEntryKind.Folder,
            Modified     = d.Modified,
            Size         = o.MeasureFolders ? (o.ShowOnlyShared ? SizeSharedOf(d) : SizeAllOf(d)) : 0,
            IsAutoShared = shared,
            IsAutoLocal  = local,
            IsIgnored    = ignored,
            Badge        = shared ? "> shared"
                         : d.SharedFiles > 0 ? "partly shared"
                         : ignored ? "ignored"
                         : local ? "> local"
                         : "",
            TextColorKey = local ? "InfoBrush" : shared ? "SuccessBrush" : "TextPrimaryBrush"
        };
    }

    private static FolderEntry FileEntryFor(FileNode f, string displayName)
    {
        var match = f.Match;
        return new FolderEntry
        {
            DisplayName  = displayName,
            RelativePath = f.Rel,
            IsFolder     = false,
            Badge        = match.Badge,
            IsAutoLocal  = match.IsAutoLocal,
            IsAutoShared = match.IsAutoShared,
            IsIgnored    = match.IsIgnored,
            IsPrivate    = f.IsPrivate,
            Kind         = f.Kind,
            Modified     = f.Modified,
            Size         = f.Size,
            TextColorKey = match.IsAutoLocal  ? "InfoBrush"
                         : match.IsAutoShared ? "SuccessBrush"
                         : "TextPrimaryBrush"
        };
    }

    // ── paths ────────────────────────────────────────────────────────────────

    private string AbsolutePath(string rel) =>
        rel.Length == 0 ? Root : Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>'/'-separated, no leading or trailing slash, "" for the root.</summary>
    public static string Normalize(string rel)
    {
        var norm = rel.Replace('\\', '/').Trim('/');
        return norm == "." ? "" : norm;
    }

    /// <summary>The folder a relative path sits in; "" for something at the root.</summary>
    public static string ParentOf(string rel)
    {
        var norm = Normalize(rel);
        var idx = norm.LastIndexOf('/');
        return idx < 0 ? "" : norm[..idx];
    }
}

/// <summary>
/// The indexes the launcher currently holds, one per folder, so every view of the same folder
/// shares one read.
/// </summary>
/// <remarks>
/// <para>Two views refreshing the same root at once (the instance page does, with <c>Task.WhenAll</c>)
/// join one scan. A fresh request only joins a scan that started moments ago, so a refresh after a
/// file operation never attaches to a walk that started before it.</para>
/// <para>A few roots are kept, least recently used dropped first. An index costs a few hundred bytes
/// per file, about 5 MB for a 20,000-file instance.</para>
/// </remarks>
public static class InstanceFileIndexes
{
    private const int Keep = 4;
    private static readonly TimeSpan JoinWindow = TimeSpan.FromMilliseconds(300);

    private sealed class Entry
    {
        public InstanceFileIndex? Index;
        public Task<InstanceFileIndex>? InFlight;
        public DateTime StartedUtc;
        public DateTime TouchedUtc;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> ByRoot = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The index for a root if one has finished, without starting a scan.</summary>
    public static InstanceFileIndex? Peek(string root)
    {
        lock (Gate) return ByRoot.TryGetValue(Key(root), out var e) ? e.Index : null;
    }

    /// <summary>
    /// The index for a root: the finished one when <paramref name="fresh"/> is false and there is one,
    /// otherwise a new scan, or the scan already running if it started a moment ago.
    /// </summary>
    public static Task<InstanceFileIndex> GetAsync(string root, List<PackRule>? rules, bool fresh,
                                                    CancellationToken ct = default)
    {
        var key = Key(root);
        var now = DateTime.UtcNow;
        lock (Gate)
        {
            if (!ByRoot.TryGetValue(key, out var entry))
            {
                entry = new Entry();
                ByRoot[key] = entry;
                Trim();
            }
            entry.TouchedUtc = now;

            if (entry.InFlight is { IsCompleted: false } running && (!fresh || now - entry.StartedUtc < JoinWindow))
                return running;
            if (!fresh && entry.Index is not null)
                return Task.FromResult(entry.Index);

            var ruleService = App.State.Rules;
            var settings = App.State.Settings;
            var task = Task.Run(() => InstanceFileIndex.Scan(key, rules, ruleService, settings, ct), ct);
            entry.InFlight = task;
            entry.StartedUtc = now;
            task.ContinueWith(t =>
            {
                lock (Gate)
                {
                    if (t.IsCompletedSuccessfully) entry.Index = t.Result;
                    if (ReferenceEquals(entry.InFlight, t)) entry.InFlight = null;
                }
            }, TaskContinuationOptions.ExecuteSynchronously);
            return task;
        }
    }

    private static string Key(string root)
    {
        try { return Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return root; }
    }

    private static void Trim()
    {
        while (ByRoot.Count > Keep)
        {
            var oldest = ByRoot.Where(kv => kv.Value.InFlight is null)
                               .OrderBy(kv => kv.Value.TouchedUtc)
                               .Select(kv => kv.Key)
                               .FirstOrDefault();
            if (oldest is null) return;
            ByRoot.Remove(oldest);
        }
    }
}
