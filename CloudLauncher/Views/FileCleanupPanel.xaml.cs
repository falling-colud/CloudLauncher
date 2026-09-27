using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// The Cleanup tab of the file-management page: where this instance's disk space went, and which
/// files are safe to delete.
/// </summary>
/// <remarks>
/// <para>Nothing is deleted automatically: every sweep lists real paths, nothing starts ticked, and
/// the confirm gives the file count and total size.</para>
/// <para>Deleted files are moved to <c>&lt;packRoot&gt;/.trash/&lt;timestamp&gt;/</c> with their paths
/// kept. <c>.trash</c> sits beside <c>game/</c>, which keeps it out of
/// <see cref="PackFolderService.ListRelativeFiles"/> and so out of every sync, scan and mirror.</para>
/// <para>Sizes are cheap and load on open. Finding duplicates reads every byte, so it is a button.</para>
/// </remarks>
public partial class FileCleanupPanel : UserControl
{
    /// <summary>How many rows of one sweep are drawn; the rest are still deleted when the group is
    /// ticked. Tens of thousands of checkboxes would stall the UI.</summary>
    private const int RowDisplayCap = 250;

    private readonly MainWindow _shell;
    private readonly PackDetail _pack;
    private readonly PageState _state;

    private readonly ObservableCollection<UsageRow> _usage = new();
    private readonly ObservableCollection<SweepSection> _sweeps = new();
    private readonly ObservableCollection<BackupGroup> _backups = new();

    /// <summary>Every row in every list, including the ones past <see cref="RowDisplayCap"/>. This is
    /// what Delete acts on; the ObservableCollections are only what is drawn.</summary>
    private readonly List<CleanupRow> _allRows = new();

    private string _packRoot = "";
    private string _gameDir = "";

    /// <summary>Total bytes under the instance root, for the duplicate finder's warning.</summary>
    private long _instanceBytes;

    /// <summary>One background job at a time, so a scan and a delete never race over the same
    /// tree.</summary>
    private CancellationTokenSource? _workCts;
    private bool _busy;

    /// <summary>False for a collaborator without upload rights. They can read everything; the
    /// launcher does not delete or restore inside an instance that is not theirs to change.</summary>
    private readonly bool _canWrite;

    private readonly string _readOnlyReason;

    public FileCleanupPanel(MainWindow shell, PackDetail pack)
    {
        InitializeComponent();
        _shell = shell;
        _pack = pack;

        _canWrite = App.State.OwnsPack(pack.Id, pack.OwnerId)
                    || pack.EffectivePermissions.HasFlag(PackPermissions.UploadShared);
        _readOnlyReason = "You have read-only access to " + pack.Name +
                          ", so the launcher will not delete or restore files in it.";

        UsageList.ItemsSource = _usage;
        SweepList.ItemsSource = _sweeps;
        BackupList.ItemsSource = _backups;

        _state = new PageState(ContentScroller, PageStateHost, nameof(FileCleanupPanel))
            .Copy(CleanupCopy)
            .Slots(SubLabel, StatusLabel, BusyBar, BusyCancelButton)
            .DisableWhileBusy(RefreshButton, DuplicatesButton);
        _state.RetryRequested += () => _ = LoadAsync();
        _state.CancelRequested += () => _workCts?.Cancel();

        if (!_canWrite)
        {
            DeleteButton.ToolTip = _readOnlyReason;
            ToolTipService.SetShowOnDisabled(DeleteButton, true);
        }

        Loaded += OnLoaded;
        Unloaded += (_, _) => _workCts?.Cancel();
    }

    /// <summary>Page strings for this tab. Empty means the instance folder holds no files at all;
    /// "nothing to clean up" goes in the count slot and the cards, next to the breakdown.</summary>
    private static readonly PageCopy CleanupCopy = new()
    {
        Glyph = "",
        Verb = "Measuring",
        Noun = "folder(s)",
        LoadingLine = "Measuring every folder in this instance.",
        EmptyTitle = "This instance has no files yet",
        EmptyBody = "Nothing has been downloaded into it and it has never been launched, so there is "
                  + "nothing to measure and nothing to clean up.",
        FilteredTitle = "Nothing matched",
        FilteredBody = "No file matched that filter.",
        ErrorTitle = "Could not measure this instance",
        OfflineTitle = "Working from this PC",
        OfflineBody = "The server is not answering ({0}). Everything on this tab is read from disk, "
                    + "so all of it still works."
    };

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_usage.Count == 0 && _state.Kind == PageStateKind.Idle) await LoadAsync();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F5) { _ = LoadAsync(); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }

    // ── scanning ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A progress sink that always lands on the UI thread.
    /// </summary>
    /// <remarks>
    /// <see cref="Progress{T}"/> falls back to the thread pool when created without a
    /// <see cref="SynchronizationContext"/>, as in hosts that never call <c>Dispatcher.Run</c> (the
    /// off-screen test harness). Checking access per report avoids cross-thread exceptions there.
    /// </remarks>
    private IProgress<string> UiProgress()
    {
        var dispatcher = Dispatcher;
        return new Progress<string>(line =>
        {
            if (dispatcher.CheckAccess()) _state.Progress(line);
            else dispatcher.BeginInvoke(() => _state.Progress(line));
        });
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = LoadAsync();

    private async Task LoadAsync()
    {
        if (_busy) return;
        _busy = true;
        _workCts?.Cancel();
        _workCts = new CancellationTokenSource();
        var ct = _workCts.Token;
        _state.Begin();

        try
        {
            _packRoot = App.State.Packs.PackRoot(_pack.Id);
            _gameDir = App.State.Packs.GameDir(_pack.Id);

            var packRoot = _packRoot;
            var gameDir = _gameDir;
            var rules = App.State.Rules;
            var progress = UiProgress();

            var scan = await Task.Run(() => Measure(packRoot, gameDir, rules, progress, ct), ct);
            if (ct.IsCancellationRequested) return;

            Apply(scan);
        }
        catch (OperationCanceledException)
        {
            _state.Cancelled("Measuring stopped.");
        }
        catch (Exception ex)
        {
            _state.Error("The instance folder could not be read.", ex);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>What one pass over the instance found. Built entirely off the UI thread.</summary>
    private sealed record ScanResult(
        List<UsageBucket> Usage,
        long TotalBytes,
        List<SweepSection> Sweeps,
        List<BackupGroup> Backups,
        string? ManifestNote);

    private sealed record UsageBucket(string Name, long Bytes, int Files);

    /// <summary>One file on disk, statted once so later questions don't touch the disk again.</summary>
    private sealed record Stat(string RelativeToRoot, string FullPath, long Length);

    private ScanResult Measure(string packRoot, string gameDir, PackRuleService rules,
                               IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Listing files...");

        // Walk from the instance root, not game/: server-run/ and backups/ are often the biggest
        // folders and aren't shown anywhere else in the launcher.
        var stats = new List<Stat>();
        Walk(packRoot, packRoot, stats, ct);

        var total = stats.Sum(s => s.Length);

        // game/ is usually the bulk, so it is broken down one level deeper than its siblings.
        progress.Report("Adding it up...");
        var buckets = stats
            .GroupBy(BucketOf, StringComparer.OrdinalIgnoreCase)
            .Select(g => new UsageBucket(g.Key, g.Sum(s => s.Length), g.Count()))
            .OrderByDescending(b => b.Bytes)
            .ToList();

        // Everything below is relative to game/, the tree the user knows.
        var gamePrefix = Path.GetRelativePath(packRoot, gameDir).Replace('\\', '/').TrimEnd('/') + "/";
        var gameStats = stats
            .Where(s => s.RelativeToRoot.StartsWith(gamePrefix, StringComparison.OrdinalIgnoreCase))
            .Select(s => (Rel: s.RelativeToRoot[gamePrefix.Length..], s.FullPath, s.Length))
            .ToList();

        ct.ThrowIfCancellationRequested();
        progress.Report("Looking for leftovers...");

        var sweeps = new List<SweepSection>();
        var backups = BuildBackupGroups(gameStats, ct);

        // Each file belongs to one group only, or the byte total would count it twice.
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Same row objects as the Backups card, so ticking a backup in either place is one tick.
        var backupRows = backups.SelectMany(g => g.All).ToList();
        foreach (var row in backupRows) claimed.Add(row.DisplayPath);
        if (backupRows.Count > 0)
            sweeps.Add(new SweepSection("Backup copies (.bak-...)",
                "Written automatically before the launcher overwrites a file for you. The Backups "
                + "card below lists them by file and can restore one.", backupRows));

        AddSweep(sweeps, claimed, "Interrupted saves (.cl-tmp)",
            "The launcher writes a .cl-tmp beside a file and renames it into place. One left behind "
            + "means a save was interrupted; the real file is still there.",
            gameStats.Where(s => s.Rel.EndsWith(".cl-tmp", StringComparison.OrdinalIgnoreCase)));

        AddFolderSweep(sweeps, claimed, gameStats, "logs", "Game logs (logs/)",
            "Written fresh on every launch. Deleting them loses nothing the game needs - but it also "
            + "loses the crash you were about to read.");
        AddFolderSweep(sweeps, claimed, gameStats, "crash-reports", "Crash reports (crash-reports/)",
            "One file per crash, kept forever. Worth reading before they go.");
        AddFolderSweep(sweeps, claimed, gameStats, "debug", "Debug output (debug/)",
            "Written when a mod is asked for diagnostics. Nothing reads it back.");
        AddFolderSweep(sweeps, claimed, gameStats, ".mixin.out", "Mixin dumps (.mixin.out/)",
            "Transformed classes a loader wrote out for debugging. Regenerated on demand.");

        ct.ThrowIfCancellationRequested();
        var (orphans, manifestNote) = BuildOrphans(packRoot, gameStats, rules, claimed);
        if (orphans is not null) sweeps.Add(orphans);

        return new ScanResult(buckets, total, sweeps, backups, manifestNote);

        string BucketOf(Stat s)
        {
            var parts = s.RelativeToRoot.Split('/');
            if (parts.Length == 1) return "(files in the instance root)";
            if (parts[0].Equals("game", StringComparison.OrdinalIgnoreCase))
                return parts.Length == 2 ? "game/ (loose files)" : "game/" + parts[1];
            return parts[0] + "/";
        }
    }

    /// <summary>Recursive walk that skips folders it can't open. Reparse points aren't followed:
    /// the managed-Java folder and some mod caches are junctions to elsewhere on the disk.</summary>
    private static void Walk(string root, string dir, List<Stat> into, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IEnumerable<FileSystemInfo> entries;
        try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos(); }
        catch { return; }

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;

            if (entry is DirectoryInfo sub)
            {
                Walk(root, sub.FullName, into, ct);
                continue;
            }
            if (entry is not FileInfo file) continue;

            long length;
            try { length = file.Length; }
            catch { continue; }

            into.Add(new Stat(Path.GetRelativePath(root, file.FullName).Replace('\\', '/'),
                              file.FullName, length));
        }
    }

    private void AddSweep(List<SweepSection> into, HashSet<string> claimed, string title, string summary,
                          IEnumerable<(string Rel, string FullPath, long Length)> files)
    {
        var rows = files.Where(f => !claimed.Contains(f.Rel))
                        .Select(f => new CleanupRow(f.Rel, f.FullPath, f.Length, "", _canWrite, _readOnlyReason))
                        .OrderBy(r => r.DisplayPath, StringComparer.OrdinalIgnoreCase)
                        .ToList();
        if (rows.Count == 0) return;
        foreach (var row in rows) claimed.Add(row.DisplayPath);
        into.Add(new SweepSection(title, summary, rows));
    }

    private void AddFolderSweep(List<SweepSection> into, HashSet<string> claimed,
                                List<(string Rel, string FullPath, long Length)> all,
                                string folder, string title, string summary)
    {
        var prefix = folder + "/";
        AddSweep(into, claimed, title, summary,
            all.Where(f => f.Rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }

    // ── backups ──────────────────────────────────────────────────────────────

    private List<BackupGroup> BuildBackupGroups(List<(string Rel, string FullPath, long Length)> all,
                                                CancellationToken ct)
    {
        var groups = new List<BackupGroup>();
        var byOriginal = new Dictionary<string, List<(string Rel, string FullPath, long Length)>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var file in all)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file.Rel);
            if (!ConfigHubService.IsBackup(name)) continue;
            var cut = file.Rel.LastIndexOf(".bak-", StringComparison.OrdinalIgnoreCase);
            if (cut <= 0) continue;
            var original = file.Rel[..cut];
            if (!byOriginal.TryGetValue(original, out var list))
                byOriginal[original] = list = [];
            list.Add(file);
        }

        foreach (var (original, files) in byOriginal.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var originalFull = Path.Combine(_gameDir, original.Replace('/', Path.DirectorySeparatorChar));

            // Order by the stamp in the name, not mtime: File.Copy keeps the source's last-write time, so
            // mtime can sort backups backwards. ConfigHubService.BackupTakenUtc parses the stamp.
            var rows = files
                .Select(f => (File: f, Taken: ConfigHubService.BackupTakenUtc(new FileInfo(f.FullPath))))
                .OrderByDescending(p => p.Taken)
                .Select(p => new CleanupRow(p.File.Rel, p.File.FullPath, p.File.Length,
                                            "Taken " + ConfigHubService.FormatAge(p.Taken)
                                            + " · " + TimeFormat.DateTime(TimeFormat.FromUtc(p.Taken)),
                                            _canWrite, _readOnlyReason)
                {
                    RestoreTarget = originalFull,
                    RestoreLabel = Path.GetFileName(original),
                    RestoreHint = _canWrite
                        ? "Copy this backup back over " + Path.GetFileName(original)
                        : _readOnlyReason
                })
                .ToList();

            groups.Add(new BackupGroup(original, originalFull, rows));
        }

        return groups;
    }

    // ── orphans ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Leftovers from sync: paths in <c>.sync-manifest.json</c> (what the server last owned here) that
    /// are still on disk but no longer shared by the current rules. The download pruner only removes
    /// paths the server still claims, so nothing else cleans these up.
    /// </summary>
    private (SweepSection? Section, string? Note) BuildOrphans(
        string packRoot, List<(string Rel, string FullPath, long Length)> all, PackRuleService rules,
        HashSet<string> claimed)
    {
        var lockPath = Path.Combine(packRoot, ".sync-manifest.json");
        if (!File.Exists(lockPath))
            return (null, "This instance has never synced with the server, so there is nothing to "
                        + "compare its files against.");

        HashSet<string> owned;
        try
        {
            var listed = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(lockPath)) ?? [];
            owned = new HashSet<string>(listed, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            AppLog.LogError("files", ex);
            return (null, "The record of what the server last sent could not be read.");
        }

        var ruleList = rules.Load(packRoot);
        // No rules at all (no .rules.json and no global defaults): every path would look unshared and
        // the whole manifest would be offered for deletion.
        if (ruleList.Count == 0)
            return (null, "This instance has no sync rules, so the launcher cannot tell which of the "
                        + "server's files it still wants. Set its rules and measure again.");

        var rows = all
            .Where(f => owned.Contains(f.Rel)
                        && !claimed.Contains(f.Rel)
                        && !rules.Match(f.Rel, ruleList).IsAutoShared)
            .Select(f => new CleanupRow(f.Rel, f.FullPath, f.Length,
                                        "Came from the server; this instance's rules no longer share it.",
                                        _canWrite, _readOnlyReason))
            .OrderBy(r => r.DisplayPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var missing = owned.Count - all.Count(f => owned.Contains(f.Rel));
        var note = missing > 0
            ? $"{missing:N0} file(s) the server last sent are no longer on this PC."
            : null;

        if (rows.Count == 0) return (null, note);
        foreach (var row in rows) claimed.Add(row.DisplayPath);

        return (new SweepSection("Left over from a server sync",
            "The last sync listed these files, but this instance's rules no longer share them. "
            + "Nothing will download or remove them again.", rows), note);
    }

    // ── applying a scan to the UI ────────────────────────────────────────────

    private void Apply(ScanResult scan)
    {
        _usage.Clear();
        var biggest = scan.Usage.Count == 0 ? 1L : Math.Max(1L, scan.Usage[0].Bytes);
        foreach (var bucket in scan.Usage)
            _usage.Add(new UsageRow(bucket.Name, bucket.Bytes, bucket.Files, biggest));

        UsageTotalLabel.Text = ConfigHubService.FormatSize(scan.TotalBytes) + " in total";
        _instanceBytes = scan.TotalBytes;

        _sweeps.Clear();
        foreach (var sweep in scan.Sweeps) _sweeps.Add(sweep);

        _backups.Clear();
        foreach (var group in scan.Backups) _backups.Add(group);

        // A backup row appears in both the Backups card and the ".bak-" sweep; the set drops the
        // second one without an O(n^2) List.Contains.
        _allRows.Clear();
        var seen = new HashSet<CleanupRow>();
        foreach (var row in scan.Sweeps.SelectMany(s => s.All).Concat(scan.Backups.SelectMany(g => g.All)))
        {
            if (!seen.Add(row)) continue;
            _allRows.Add(row);
            row.PropertyChanged += OnRowChanged;
        }

        RefreshSweepTotals();

        var backupBytes = scan.Backups.SelectMany(g => g.All).Sum(r => r.Size);
        BackupTotalLabel.Text = scan.Backups.Count == 0
            ? ""
            : $"{scan.Backups.Sum(g => g.All.Count):N0} backup(s) of {scan.Backups.Count:N0} file(s) · "
              + ConfigHubService.FormatSize(backupBytes);
        NoBackupsLabel.Visibility = scan.Backups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        DuplicatesHint.Text = "Reads every byte of this instance ("
                              + ConfigHubService.FormatSize(scan.TotalBytes)
                              + ") and groups files with identical contents. Nothing is deleted or "
                              + "ticked by the scan.";

        // Pass the number of folders measured, not files worth deleting: Empty should only show when
        // the instance holds no files, since the breakdown is still useful.
        _state.Content(scan.Usage.Count, countText: DescribeCounts(scan));
        if (scan.ManifestNote is { Length: > 0 }) _state.Note(scan.ManifestNote);
        UpdateSelection();
    }

    /// <summary>The "what can go" card's own header and its no-groups line. Shared with the
    /// duplicate finder, which adds a group after the scan has already written these once.</summary>
    private void RefreshSweepTotals()
    {
        var bytes = _sweeps.SelectMany(s => s.All).Distinct().Sum(r => r.Size);
        SweepTotalLabel.Text = _sweeps.Count == 0
            ? ""
            : $"{ConfigHubService.FormatSize(bytes)} across {_sweeps.Count} group(s)";
        NoSweepsLabel.Visibility = _sweeps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string DescribeCounts(ScanResult scan)
    {
        var sweepable = scan.Sweeps.SelectMany(s => s.All).Distinct().ToList();
        return sweepable.Count == 0
            ? $"Nothing to clean up · {ConfigHubService.FormatSize(scan.TotalBytes)} in this instance"
            : $"{sweepable.Count:N0} file(s) worth a look · "
              + ConfigHubService.FormatSize(sweepable.Sum(r => r.Size));
    }

    /// <summary>Set while a whole group is ticked at once, so totals are recomputed once rather than
    /// per row.</summary>
    private bool _bulkTicking;

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_bulkTicking) return;
        if (e.PropertyName == nameof(CleanupRow.IsChecked)) UpdateSelection();
    }

    private void UpdateSelection()
    {
        var chosen = _allRows.Where(r => r.IsChecked).ToList();
        var bytes = chosen.Sum(r => r.Size);
        SelectionLabel.Text = chosen.Count == 0
            ? ""
            : $"{chosen.Count:N0} file(s) ticked · {ConfigHubService.FormatSize(bytes)}";
        DeleteButton.IsEnabled = _canWrite && chosen.Count > 0 && !_busy;
        foreach (var sweep in _sweeps) sweep.RefreshSelection();
    }

    // ── per-group ticking ────────────────────────────────────────────────────

    private void OnSweepSelectAll(object sender, RoutedEventArgs e) => SetSweep(sender, true);
    private void OnSweepSelectNone(object sender, RoutedEventArgs e) => SetSweep(sender, false);

    private void SetSweep(object sender, bool value)
    {
        if ((sender as FrameworkElement)?.DataContext is not SweepSection section) return;
        if (value && !_canWrite) return;
        _bulkTicking = true;
        // Every row, not just the drawn ones, so a capped list ticks everything it claims to.
        try { foreach (var row in section.All) row.IsChecked = value; }
        finally { _bulkTicking = false; }
        UpdateSelection();
    }

    // ── duplicates ───────────────────────────────────────────────────────────

    /// <summary>
    /// Groups files with identical contents.
    /// </summary>
    /// <remarks>
    /// Files are grouped by length first so only real candidates get hashed, via
    /// <see cref="ConfigHubService.HashOrNull"/>, which returns null for a locked file.
    /// <see cref="PackFolderService.ComputeManifest"/> would hash everything and abort on the first
    /// jar a running game holds open.
    /// </remarks>
    private async void OnFindDuplicates(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            if (!await AppDialog.ConfirmAsync(_shell, "Find duplicate files",
                    $"This reads every file in {_pack.Name} ({ConfigHubService.FormatSize(_instanceBytes)}) "
                    + "to compare their contents. It changes nothing and can be stopped at any point.\n\n"
                    + "On a large modpack it takes a minute or two.",
                    "Scan", "Cancel"))
                return;

            _busy = true;
            _workCts?.Cancel();
            _workCts = new CancellationTokenSource();
            var ct = _workCts.Token;
            _state.Begin("Reading every file to compare their contents.", refreshing: true);

            // Replace any previous duplicates group rather than stacking a second one. Done before
            // the scan so the exclusion set below is the other groups only.
            var existing = _sweeps.FirstOrDefault(s => s.IsDuplicates);
            if (existing is not null)
            {
                foreach (var row in existing.All) { row.PropertyChanged -= OnRowChanged; _allRows.Remove(row); }
                _sweeps.Remove(existing);
            }

            var gameDir = _gameDir;
            var canWrite = _canWrite;
            var reason = _readOnlyReason;
            var progress = UiProgress();
            // A file already offered as junk must not be offered again as a duplicate, or the
            // selection total counts it twice.
            var exclude = new HashSet<string>(_allRows.Select(r => r.FullPath), StringComparer.OrdinalIgnoreCase);

            var section = await Task.Run(
                () => FindDuplicates(gameDir, exclude, canWrite, reason, progress, ct), ct);
            if (ct.IsCancellationRequested) return;

            if (section is not null)
            {
                section.IsExpanded = true;
                _sweeps.Add(section);
                foreach (var row in section.All)
                {
                    _allRows.Add(row);
                    row.PropertyChanged += OnRowChanged;
                }
            }

            RefreshSweepTotals();

            // Still the folder count, as in Apply.
            var sweepable = _allRows.Count;
            _state.Content(_usage.Count, countText: sweepable == 0
                ? "Nothing to clean up"
                : $"{sweepable:N0} file(s) worth a look · "
                  + ConfigHubService.FormatSize(_allRows.Sum(r => r.Size)));
            _state.Note(section?.Summary
                        ?? "No two files in this instance have identical contents.");
            UpdateSelection();
        }
        catch (OperationCanceledException) { _state.Cancelled("Duplicate scan stopped."); }
        catch (Exception ex) { _state.Error("The duplicate scan could not finish.", ex); }
        finally { _busy = false; UpdateSelection(); }
    }

    private static SweepSection? FindDuplicates(string gameDir, HashSet<string> exclude,
                                                bool canWrite, string reason,
                                                IProgress<string> progress, CancellationToken ct)
    {
        var files = new List<Stat>();
        Walk(gameDir, gameDir, files, ct);

        var candidates = files.Where(f => f.Length > 0)
                              .GroupBy(f => f.Length)
                              .Where(g => g.Count() > 1)
                              .ToList();

        var rows = new List<CleanupRow>();
        var groups = 0;
        long wasted = 0;
        var done = 0;

        foreach (var sameLength in candidates)
        {
            ct.ThrowIfCancellationRequested();
            done++;
            progress.Report($"Comparing {done:N0} of {candidates.Count:N0} size group(s)...");

            foreach (var byHash in sameLength.GroupBy(f => ConfigHubService.HashOrNull(f.FullPath)))
            {
                if (byHash.Key is null) continue;          // unreadable: never claimed as a duplicate
                var copies = byHash.OrderBy(f => f.RelativeToRoot.Length)
                                   .ThenBy(f => f.RelativeToRoot, StringComparer.Ordinal)
                                   .ToList();
                if (copies.Count < 2) continue;

                var keeper = copies[0];
                // Never offer the first copy, so "tick all" can't delete every copy of a file.
                var extras = copies.Skip(1).Where(c => !exclude.Contains(c.FullPath)).ToList();
                if (extras.Count == 0) continue;

                groups++;
                foreach (var copy in extras)
                {
                    wasted += copy.Length;
                    rows.Add(new CleanupRow(copy.RelativeToRoot, copy.FullPath, copy.Length,
                                            "Identical to " + keeper.RelativeToRoot, canWrite, reason));
                }
            }
        }

        if (rows.Count == 0) return null;

        return new SweepSection($"Duplicate files ({groups:N0} group(s))",
            $"{rows.Count:N0} extra copy(s) of {groups:N0} file(s), {ConfigHubService.FormatSize(wasted)} "
            + "in all. One copy of each is kept and is never listed here.", rows)
        {
            IsDuplicates = true
        };
    }

    // ── restore ──────────────────────────────────────────────────────────────

    private async void OnRestoreBackup(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CleanupRow row) return;
        if (row.RestoreTarget is not { Length: > 0 } target) return;
        if (!_canWrite) return;

        try
        {
            var exists = File.Exists(target);
            if (!await AppDialog.ConfirmAsync(_shell, "Restore this backup",
                    $"Put {row.RestoreLabel} back from this backup?\n\n{row.Note}\n\n"
                    + (exists
                        ? $"The file as it stands now is copied to {row.RestoreLabel}.bak-<timestamp> "
                          + "first, so this is reversible."
                        : "There is no file at that path right now, so nothing is replaced."),
                    "Restore", "Cancel"))
                return;

            var source = row.FullPath;
            await Task.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                    // Invariant: BackupTakenUtc parses this suffix back to date the backup.
                    File.Copy(target, $"{target}.bak-{TimeFormat.StampNow()}", overwrite: true);
                File.Copy(source, target, overwrite: true);
            });

            ConfigHubService.Invalidate(_pack.Id);
            AppLog.Log("files", $"Restored {row.RestoreLabel} in {_pack.Name} from {row.DisplayPath}.");
            var restored = row.RestoreLabel;
            await LoadAsync();
            _state.Note($"Restored {restored}. The version it replaced was backed up first.");
        }
        catch (Exception ex)
        {
            _state.Error("That backup could not be put back.", ex);
        }
    }

    // ── delete ───────────────────────────────────────────────────────────────

    private async void OnDeleteChecked(object sender, RoutedEventArgs e)
    {
        if (_busy || !_canWrite) return;

        var chosen = _allRows.Where(r => r.IsChecked).ToList();
        if (chosen.Count == 0) return;

        try
        {
            var bytes = chosen.Sum(r => r.Size);
            // Invariant: this names a batch folder under the instance's .trash, which the Files
            // page (FileBrowserPanel.ReadTrash) parses back with InvariantCulture.
            var stamp = TimeFormat.StampNow();

            // List the files in the confirm, not just a count.
            var named = string.Join("\n", chosen.Take(12).Select(r => "  " + r.DisplayPath));
            if (chosen.Count > 12) named += $"\n  ...and {chosen.Count - 12:N0} more";

            if (!await AppDialog.ConfirmAsync(_shell, "Move these files out of the instance",
                    $"{chosen.Count:N0} file(s), {ConfigHubService.FormatSize(bytes)}:\n\n{named}\n\n"
                    + $"They are moved to .trash\\{stamp}\\ inside the instance folder, keeping their "
                    + "paths, so you can drag them back. Nothing in .trash is ever launched, mirrored "
                    + "to a server or uploaded.",
                    "Move to trash", "Cancel", danger: true))
                return;

            _busy = true;
            _workCts?.Cancel();
            _workCts = new CancellationTokenSource();
            var ct = _workCts.Token;
            _state.Begin("Moving files to the instance's trash folder.", refreshing: true);

            var trashDir = Path.Combine(_packRoot, ".trash", stamp);
            var packRoot = _packRoot;
            var paths = chosen.Select(r => r.FullPath).ToList();
            var progress = UiProgress();

            var (moved, failed) = await Task.Run(
                () => MoveToTrash(packRoot, trashDir, paths, progress, ct), ct);

            AppLog.Log("files", $"Moved {moved} file(s) from {_pack.Name} into .trash/{stamp}"
                                + (failed.Count > 0 ? $"; {failed.Count} could not be moved" : "") + ".");
            foreach (var line in failed.Take(20)) AppLog.Log("files", "  " + line);

            ConfigHubService.Invalidate(_pack.Id);
            _busy = false;
            await LoadAsync();

            _state.Note(failed.Count == 0
                ? $"Moved {moved:N0} file(s) to .trash\\{stamp}."
                : $"Moved {moved:N0} file(s) to .trash\\{stamp}. {failed.Count:N0} could not be moved "
                  + "- they are most likely open in the running game. The details are in the launcher log.");
        }
        catch (OperationCanceledException) { _state.Cancelled("Delete stopped."); }
        catch (Exception ex) { _state.Error("Those files could not be moved.", ex); }
        finally { _busy = false; UpdateSelection(); }
    }

    /// <summary>
    /// Moves each path into <paramref name="trashDir"/>, keeping its path relative to the instance
    /// root. Files that can't be moved are reported.
    /// </summary>
    private static (int Moved, List<string> Failed) MoveToTrash(
        string packRoot, string trashDir, List<string> paths,
        IProgress<string> progress, CancellationToken ct)
    {
        var moved = 0;
        var failed = new List<string>();
        var index = 0;

        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            index++;
            var rel = Path.GetRelativePath(packRoot, path);
            progress.Report($"Moving {index:N0} of {paths.Count:N0}: {rel}");
            try
            {
                if (!File.Exists(path)) continue;
                var dest = Path.Combine(trashDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(path, dest, overwrite: true);
                moved++;
            }
            catch (Exception ex)
            {
                failed.Add($"{rel}: {ex.Message}");
            }
        }

        return (moved, failed);
    }

    // ── explorer ─────────────────────────────────────────────────────────────

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var root = _packRoot.Length > 0 ? _packRoot : App.State.Packs.PackRoot(_pack.Id);
            Directory.CreateDirectory(root);
            if (!SafeLaunch.OpenFolder(root))
                _state.Note("The instance folder could not be opened. The details are in the launcher log.");
        }
        catch (Exception ex)
        {
            AppLog.LogError("files", ex);
            _state.Note("The instance folder could not be opened. The details are in the launcher log.");
        }
    }

    // ── rows ─────────────────────────────────────────────────────────────────

    /// <summary>One folder in the breakdown, drawn relative to the biggest folder rather than the
    /// total so small folders stay visible.</summary>
    private sealed class UsageRow(string name, long bytes, int files, long biggest)
    {
        public string Name { get; } = name;
        public string SizeLabel { get; } = ConfigHubService.FormatSize(bytes);
        public string FileCountLabel { get; } = $"{files:N0} file(s)";

        // Floored at half a percent so a tiny folder still draws a bar.
        public double Percent { get; } =
            Math.Max(0.5, 100.0 * bytes / Math.Max(1, biggest));
    }

    /// <summary>One file offered for deletion. Notifies because a backup row appears in two lists
    /// and both must show the same tick.</summary>
    private sealed class CleanupRow(string relativePath, string fullPath, long size, string note,
                                    bool canDelete, string readOnlyReason) : INotifyPropertyChanged
    {
        private bool _checked;

        public string DisplayPath { get; } = relativePath;
        public string FullPath { get; } = fullPath;
        public long Size { get; } = size;
        public string SizeLabel { get; } = ConfigHubService.FormatSize(size);
        public string Note { get; } = note;
        public Visibility NoteVisibility => Note.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        public bool CanDelete { get; } = canDelete;
        public string CheckHint { get; } = canDelete
            ? "Include this file when you press Delete"
            : readOnlyReason;

        /// <summary>The file this row would be copied back over, for a backup row. Null otherwise.</summary>
        public string? RestoreTarget { get; init; }

        /// <summary>The restored file's name, for the confirm text.</summary>
        public string RestoreLabel { get; init; } = "";

        /// <summary>Tooltip for the Restore button, which does the opposite of the tick.</summary>
        public string RestoreHint { get; init; } = "";

        public bool IsChecked
        {
            get => _checked;
            set
            {
                if (_checked == value) return;
                if (value && !CanDelete) return;
                _checked = value;
                Raise();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>One group of files in the "what can go" card.</summary>
    private sealed class SweepSection : INotifyPropertyChanged
    {
        public SweepSection(string title, string summary, List<CleanupRow> all)
        {
            Title = title;
            Summary = summary;
            All = all;
            Rows = all.Take(RowDisplayCap).ToList();
            SizeLabel = ConfigHubService.FormatSize(all.Sum(r => r.Size));
            MoreLabel = all.Count > RowDisplayCap
                ? $"...and {all.Count - RowDisplayCap:N0} more, not listed. Tick all includes them."
                : "";
            MoreVisibility = MoreLabel.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        public string Title { get; }
        public string Summary { get; }
        public string SizeLabel { get; }

        /// <summary>Every row in the group; what Delete and "Tick all" act on.</summary>
        public List<CleanupRow> All { get; }

        /// <summary>The first <see cref="RowDisplayCap"/> of them; what is drawn.</summary>
        public List<CleanupRow> Rows { get; }

        public string MoreLabel { get; }
        public Visibility MoreVisibility { get; }

        public bool IsDuplicates { get; init; }

        private bool _expanded;
        public bool IsExpanded
        {
            get => _expanded;
            set { if (_expanded == value) return; _expanded = value; Raise(nameof(IsExpanded)); }
        }

        public string SelectionLabel
        {
            get
            {
                var ticked = All.Count(r => r.IsChecked);
                return ticked == 0
                    ? $"{All.Count:N0} file(s), none ticked"
                    : $"{ticked:N0} of {All.Count:N0} ticked · "
                      + ConfigHubService.FormatSize(All.Where(r => r.IsChecked).Sum(r => r.Size));
            }
        }

        public void RefreshSelection() => Raise(nameof(SelectionLabel));

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Every backup of one file, newest taken first.</summary>
    private sealed class BackupGroup(string relativePath, string originalFullPath, List<CleanupRow> rows)
    {
        public string RelativePath { get; } = relativePath;
        public string OriginalFullPath { get; } = originalFullPath;
        public string FileName { get; } = Path.GetFileName(relativePath);

        public List<CleanupRow> All { get; } = rows;
        public List<CleanupRow> Rows { get; } = rows;

        public string SizeLabel { get; } = ConfigHubService.FormatSize(rows.Sum(r => r.Size));

        public string Summary { get; } = rows.Count == 1
            ? relativePath + " · 1 backup"
            : $"{relativePath} · {rows.Count:N0} backups";
    }
}
