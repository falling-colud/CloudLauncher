using System.IO;
using System.Security.Cryptography;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>
/// The scanning, diffing and cross-instance copying behind the Config and scripts page.
/// </summary>
/// <remarks>
/// <para><b>Why this is its own class.</b> The page's whole reason to exist is comparing the same
/// relative path across several instances, so nothing here is written per-pack: a scan produces a flat
/// list of <see cref="Entry"/> records keyed by a game-relative path, and the page groups them. Keeping
/// the file work out of the view also keeps it off the UI thread by construction — every public method
/// here is either pure or safe to call from <c>Task.Run</c>.</para>
///
/// <para><b>Why the cache is static.</b> The page is constructed fresh on every navigation, and a
/// config tree across a dozen large instances is thousands of files; re-walking it each time the user
/// clicks "Config &amp; scripts" would make the page feel broken. The cache is keyed by instance and
/// validated against a cheap folder stamp (see <see cref="Stamp"/>), so an edit made in Explorer is
/// picked up without a manual refresh in the common case, and the Refresh button forces the rest.</para>
/// </remarks>
public static class ConfigHubService
{
    /// <summary>Which of the page's four sources a file came from. Drives the kind filter and the
    /// badge on each row.</summary>
    public enum FileKind
    {
        /// <summary><c>config/</c> — the per-instance mod settings people actually edit.</summary>
        Config,
        /// <summary><c>kubejs/</c> — startup/server/client scripts plus the data and assets they generate.</summary>
        KubeJs,
        /// <summary><c>defaultconfigs/</c> — what a pack copies into a new world's config. Confuses
        /// people constantly, which is exactly why it is listed next to the real configs.</summary>
        DefaultConfigs,
        /// <summary><c>logs/kubejs/</c> — where a KubeJS script error lands.</summary>
        KubeJsLog
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

    /// <summary>The result of one instance's walk, plus the stamp it was valid for.</summary>
    private sealed record PackScan(List<Entry> Files, string Stamp, DateTime ScannedUtc);

    private static readonly Dictionary<Guid, PackScan> Cache = new();
    private static readonly object CacheLock = new();

    /// <summary>The folders the page covers, and the kind each produces. Order matters only in that
    /// it is the order files appear in before sorting.</summary>
    private static readonly (string Relative, FileKind Kind)[] Roots =
    [
        ("config",         FileKind.Config),
        ("kubejs",         FileKind.KubeJs),
        ("defaultconfigs", FileKind.DefaultConfigs),
        ("logs/kubejs",    FileKind.KubeJsLog),
    ];

    /// <summary>
    /// Folder names never walked into. These hold generated dumps rather than anything hand-edited —
    /// KubeJS's <c>probe_dumps</c> alone can be tens of thousands of files, which would dominate both
    /// the scan time and the list.
    /// </summary>
    private static readonly HashSet<string> SkipFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".svn", "probe_dumps", "exported", ".cache", "cache"
    };

    /// <summary>Files larger than this are listed but never hashed or grepped — at that size a config
    /// is a generated dump, and reading a dozen of them would stall the scan for no benefit.</summary>
    public const long MaxInspectBytes = 4L * 1024 * 1024;

    // ── scanning ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Walks every given instance's config, KubeJS and defaultconfigs folders. Call from a background
    /// thread. Instances with none of those folders contribute nothing and are not an error — a fresh
    /// instance that has never been launched is exactly that case.
    /// </summary>
    /// <param name="force">Ignore the cache and re-walk every instance.</param>
    /// <param name="progress">Reports the instance name as each one starts, for the status line.</param>
    public static List<Entry> Scan(
        IReadOnlyList<PackSummary> packs,
        PackFolderService folders,
        AppSettings settings,
        bool force,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var all = new List<Entry>();
        foreach (var pack in packs)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(pack.Name);

            string gameDir;
            // The name-less overload never creates the folder, which matters: scanning must not
            // conjure an empty game/ directory for an instance that was never downloaded.
            try { gameDir = folders.GameDir(pack.Id); }
            catch { continue; }

            var stamp = Stamp(gameDir);
            lock (CacheLock)
            {
                if (!force && Cache.TryGetValue(pack.Id, out var cached) && cached.Stamp == stamp)
                {
                    all.AddRange(cached.Files);
                    continue;
                }
            }

            var files = WalkPack(pack, gameDir, settings, ct);
            lock (CacheLock) Cache[pack.Id] = new PackScan(files, stamp, DateTime.UtcNow);
            all.AddRange(files);
        }
        return all;
    }

    /// <summary>Drops one instance's cached walk, so the next scan re-reads it. Called after this page
    /// writes into an instance, which is the one case the folder stamp is guaranteed to miss (writing
    /// a file does not change its folder's timestamp when the file already existed).</summary>
    public static void Invalidate(Guid packId)
    {
        lock (CacheLock) Cache.Remove(packId);
    }

    /// <summary>Drops every cached walk.</summary>
    public static void InvalidateAll()
    {
        lock (CacheLock) Cache.Clear();
    }

    /// <summary>
    /// A cheap fingerprint of an instance's config folders: the write time of each root and of its
    /// immediate children. Creating, deleting or renaming a file bumps its folder's timestamp, so this
    /// catches the changes made outside the launcher that matter most. It deliberately does not walk
    /// deeper — that would cost as much as the scan it is trying to avoid — which is why the page has
    /// a Refresh button and why <see cref="Invalidate"/> exists for our own writes.
    /// </summary>
    private static string Stamp(string gameDir)
    {
        var parts = new List<string>();
        foreach (var (relative, _) in Roots)
        {
            var root = Path.Combine(gameDir, relative.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (!Directory.Exists(root)) { parts.Add(relative + ":-"); continue; }
                parts.Add($"{relative}:{Directory.GetLastWriteTimeUtc(root).Ticks}");
                foreach (var child in Directory.EnumerateDirectories(root))
                    parts.Add($"{Path.GetFileName(child)}:{Directory.GetLastWriteTimeUtc(child).Ticks}");
            }
            catch (IOException) { parts.Add(relative + ":?"); }
            catch (UnauthorizedAccessException) { parts.Add(relative + ":?"); }
        }
        return string.Join("|", parts);
    }

    private static List<Entry> WalkPack(PackSummary pack, string gameDir, AppSettings settings, CancellationToken ct)
    {
        var files = new List<Entry>();
        foreach (var (relative, kind) in Roots)
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
        // Config trees are shallow in practice; a limit stops a symlink loop or a stray world folder
        // from turning the scan into a hang.
        if (depth > 12) return;

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
            // The privacy policy is the sync boundary, but it is also a "do not look at this" list:
            // the Arleana bundle lives under config/ and has no business being browsable here either.
            if (PrivateAssetPolicy.IsPrivate(rel, settings)) continue;

            FileInfo info;
            try { info = new FileInfo(path); }
            catch { continue; }

            into.Add(new Entry(pack.Id, pack.Name, rel, path, info.Length, info.LastWriteTimeUtc, kind));
        }
    }

    /// <summary>True for the backups this page writes before overwriting (<c>foo.toml.bak-20260921-013000</c>).
    /// They are hidden from the list — after a few cross-instance copies they would outnumber the real
    /// files — and reached instead through the row's "Restore a backup" action.</summary>
    public static bool IsBackup(string fileName)
    {
        var dot = fileName.LastIndexOf(".bak-", StringComparison.OrdinalIgnoreCase);
        return dot > 0;
    }

    /// <summary>The backups sitting next to <paramref name="fullPath"/>, newest first.</summary>
    public static List<FileInfo> FindBackups(string fullPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(fullPath);
            if (dir is null || !Directory.Exists(dir)) return [];
            return new DirectoryInfo(dir)
                .GetFiles(Path.GetFileName(fullPath) + ".bak-*")
                .OrderByDescending(f => f.LastWriteTimeUtc)
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
    /// Files whose lengths differ cannot be identical, so the common case — a config the user has
    /// actually edited in one instance — is answered without reading a byte. Only same-length copies
    /// are hashed, which in a real pack lineage is most of them but they are small.
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

    /// <summary>SHA-256 of a file's bytes, or null when it cannot be read (locked by the running
    /// game, most likely). Never throws — an unreadable file becomes "unknown", not a crash.</summary>
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
    /// <param name="OnlyLineEndings">True when the two files have the same lines but different bytes —
    /// i.e. they differ only in line endings or in a byte-order mark. Saying "identical" there would be
    /// a lie and showing every line as changed would be useless, so it gets its own answer.</param>
    public sealed record DiffResult(
        IReadOnlyList<DiffLine> Lines, int Added, int Removed, bool Truncated, bool OnlyLineEndings);

    /// <summary>
    /// Above this many lines (after the common prefix and suffix are trimmed) the diff falls back to a
    /// straight positional comparison. The LCS table is O(n·m), so an unbounded diff of two 50,000-line
    /// recipe dumps would allocate gigabytes; a config anyone edits by hand is nowhere near the cap.
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

    /// <param name="Skipped">Files that were too large or could not be read. Reported rather than
    /// swallowed, because a search that silently missed a file is worse than no search.</param>
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
    /// that mention it, so a broken script can carry its error on the row instead of the user having to
    /// know that KubeJS logs somewhere else entirely.
    /// </summary>
    /// <remarks>
    /// Deliberately crude: KubeJS error lines name the script as <c>server_scripts/foo.js:12</c> or
    /// just <c>foo.js</c> depending on the version and the kind of failure, so this matches on the file
    /// name and accepts the odd false positive. The alternative — parsing each KubeJS release's log
    /// format — would break on the next release, and a wrongly attributed warning costs the user a
    /// glance while a missed one costs them an evening.
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
            // "server_scripts/foo.js:12" → "foo.js"
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
    /// Works out exactly what a copy would do before anything is written: how many files, which of them
    /// already exist, and which destination instances would push the change to other people. Call from a
    /// background thread — it reads each destination's <c>.rules.json</c>.
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
    /// The backup is not optional and is not a setting. A mod config is the one file in a pack that,
    /// when wrong, stops the game starting with no clue as to why; a copy that silently replaced the
    /// working one would be unrecoverable for anyone who did not think to make their own copy first.
    /// </remarks>
    public static CopyResult Copy(
        string sourceGameDir,
        IReadOnlyList<string> sourceRelativePaths,
        IReadOnlyList<PackSummary> targets,
        PackFolderService folders,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
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

    // ── small helpers the page shares with its dialogs ───────────────────────

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.##} MB"
    };

    /// <summary>"3 minutes ago" style, matching how the rest of the launcher reports file times.</summary>
    public static string FormatAge(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span < TimeSpan.Zero) return "just now";
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} h ago";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays} d ago";
        return utc.ToLocalTime().ToString("d MMM yyyy");
    }

    public static string KindLabel(FileKind kind) => kind switch
    {
        FileKind.Config => "Config",
        FileKind.KubeJs => "KubeJS",
        FileKind.DefaultConfigs => "Default",
        FileKind.KubeJsLog => "Log",
        _ => ""
    };

    /// <summary>The pin key stored in <see cref="AppSettings.ConfigHubPins"/>.</summary>
    public static string PinKey(Guid packId, string relativePath) => $"{packId:N}/{relativePath}";
}
