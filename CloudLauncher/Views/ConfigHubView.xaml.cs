using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CloudLauncher.Services;
using CloudLauncher.Shared;
using Kind = CloudLauncher.Services.ConfigHubService.FileKind;
using Entry = CloudLauncher.Services.ConfigHubService.Entry;
using GroupState = CloudLauncher.Services.ConfigHubService.GroupState;

namespace CloudLauncher.Views;

/// <summary>
/// The Config and scripts page: one place to read, edit, compare and copy the config files and KubeJS
/// scripts that live inside each instance, instead of opening each instance's folder in turn.
/// </summary>
/// <remarks>
/// <para><b>The page is organised around a relative path, not a file.</b> The user runs several
/// instances of the same modpack lineage, so the question he actually has is "is this config the same
/// in all of them, and if not, which one is right?". Every row therefore stands for a game-relative
/// path — <c>config/jei/jei-client.ini</c> — and carries the list of instances that have a file there,
/// whether their copies match, and the actions to compare and to push one copy over the others. With a
/// single instance selected in the filter the rows happen to be that instance's files, but they are
/// still built the same way, so the cross-instance badge is present either way.</para>
///
/// <para><b>Nothing touches the disk on the UI thread.</b> The scan, the hashing that decides
/// "identical" or "differs", the content grep, the preview read and every copy run in
/// <see cref="Task.Run"/> behind <see cref="_workCts"/>, which the Cancel button trips. Scans are
/// cached per instance by <see cref="ConfigHubService"/>, so switching filters is instant and only
/// Refresh (or a write of our own) pays for a re-walk.</para>
/// </remarks>
public partial class ConfigHubView : Page
{
    private readonly MainWindow _shell;

    private readonly ObservableCollection<FileRow> _rows = new();
    private readonly ObservableCollection<KindChip> _kindChips = new();
    private readonly ObservableCollection<CopyRow> _copies = new();
    private readonly ObservableCollection<HitRow> _hits = new();

    private List<PackSummary> _packs = new();
    private List<Entry> _scan = new();
    private Dictionary<string, GroupState> _groupStates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>KubeJS error lines per instance, keyed by script file name. Filled by the scan.</summary>
    private Dictionary<Guid, Dictionary<string, List<string>>> _kubeErrors = new();

    /// <summary>Content-search hits keyed by full path, or null when no content search is active.
    /// Non-null with zero entries for a search that matched nothing, which is a different state.</summary>
    private Dictionary<string, List<ConfigHubService.SearchHit>>? _contentHits;

    private Kind? _kindFilter;
    private SortMode _sort = SortMode.Path;
    private bool _differsOnly;
    private bool _loading;

    /// <summary>Cancels whatever background work the page is doing — one at a time by design, because
    /// a scan and a copy racing over the same tree is how you lose a config.</summary>
    private CancellationTokenSource? _workCts;

    /// <summary>Guards the async detail load against a stale result arriving after the user has moved
    /// on to another row.</summary>
    private int _detailToken;

    private enum SortMode { Name, Path, Modified, Size, Diff }

    public ConfigHubView(MainWindow shell)
    {
        InitializeComponent();
        _shell = shell;
        FileList.ItemsSource = _rows;
        KindStrip.ItemsSource = _kindChips;
        CopiesList.ItemsSource = _copies;
        HitsList.ItemsSource = _hits;
        RestoreFilter();
        Loaded += OnLoaded;
        Unloaded += (_, _) => _workCts?.Cancel();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try { await LoadAsync(force: false); }
        catch (Exception ex) { StatusLabel.Text = "Could not open the config list: " + ex.Message; }
    }

    // ── loading ──────────────────────────────────────────────────────────────

    private async Task LoadAsync(bool force)
    {
        if (_loading) return;
        _loading = true;
        _workCts?.Cancel();
        _workCts = new CancellationTokenSource();
        var ct = _workCts.Token;
        SetBusy(true, "Reading instances…");

        try
        {
            _packs = (await App.State.Api.ListPacksAsync())
                .Where(p => !App.State.Settings.IsPackHidden(p.Id))
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            RebuildPackFilter();

            var packs = _packs;
            var settings = App.State.Settings;
            var folders = App.State.Packs;
            var progress = new Progress<string>(name => StatusLabel.Text = $"Scanning {name}…");

            var (scan, states, errors) = await Task.Run(() =>
            {
                var files = ConfigHubService.Scan(packs, folders, settings, force, progress, ct);
                // Only paths that exist in more than one instance need comparing, and only those whose
                // copies are the same length need reading — see CompareGroups.
                var groups = files.GroupBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                                  .Where(g => g.Count() > 1);
                var compared = ConfigHubService.CompareGroups(groups, ct);

                var kube = new Dictionary<Guid, Dictionary<string, List<string>>>();
                foreach (var pack in packs)
                {
                    ct.ThrowIfCancellationRequested();
                    try { kube[pack.Id] = ConfigHubService.ReadKubeJsErrors(folders.GameDir(pack.Id), ct); }
                    catch { /* an instance with no logs folder is the normal case */ }
                }
                return (files, compared, kube);
            }, ct);

            _scan = scan;
            _groupStates = states;
            _kubeErrors = errors;
            _contentHits = null;
            Refresh();

            StatusLabel.Text = App.State.Api.PackListStale is { Length: > 0 } why
                ? $"Showing your last known instances — the server is not answering ({why})."
                : "";
        }
        catch (OperationCanceledException) { StatusLabel.Text = "Scan cancelled."; }
        catch (Exception ex) { StatusLabel.Text = "Scan failed: " + ex.Message; }
        finally
        {
            _loading = false;
            SetBusy(false);
        }
    }

    private void RebuildPackFilter()
    {
        var items = new List<PackFilterItem> { new(null, "All instances") };
        items.AddRange(_packs.Select(p => new PackFilterItem(p.Id, p.Name)));
        var previous = SelectedPackId ?? _restoredPackId;
        _restoredPackId = null;
        PackFilterBox.ItemsSource = items;
        PackFilterBox.SelectedItem = items.FirstOrDefault(i => i.Id == previous) ?? items[0];
    }

    private Guid? SelectedPackId => (PackFilterBox.SelectedItem as PackFilterItem)?.Id;

    /// <summary>The instance a new file is created in, or whose folder the toolbar opens: the filtered
    /// one, or the first instance when the filter is on "All".</summary>
    private Guid? TargetPackId => SelectedPackId ?? _packs.FirstOrDefault()?.Id;

    private string PackName(Guid id) => _packs.FirstOrDefault(p => p.Id == id)?.Name ?? "an instance";

    // ── building the list ────────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds the rows from the cached scan. Pure in-memory work — no disk access — so it is safe to
    /// call from every filter, sort and search change.
    /// </summary>
    private void Refresh()
    {
        var previous = SelectedRow is { } sel ? (sel.PackId, sel.RelativePath) : ((Guid, string)?)null;

        var scope = SelectedPackId is { } packId
            ? _scan.Where(e => e.PackId == packId).ToList()
            : _scan;

        // Group across the whole scan even when one instance is selected: "this file is in 5 instances,
        // 2 of them differ" is the answer the page exists to give, and it must not vanish when the user
        // narrows the list down to the instance they are editing.
        var byPath = _scan.GroupBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase)
                          .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var query = SearchBox.Text?.Trim() ?? "";
        var pins = App.State.Settings.ConfigHubPins;

        // With "All instances" chosen, one row per relative path: the first instance alphabetically
        // becomes the row's own copy and the rest show up in the detail pane. A content search is the
        // exception — there the user is looking for occurrences, so each instance's hit earns a row.
        var candidates = scope;
        if (SelectedPackId is null && _contentHits is null)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            candidates = scope.Where(e => seen.Add(e.RelativePath)).ToList();
        }

        // The chip counts describe the rows this filter can produce, so they are taken after the
        // de-duplication and before the kind and search filters — otherwise "Config 4,812" would sit
        // above a list of 1,204 rows.
        var counts = candidates.GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Count());

        var rows = new List<FileRow>();
        foreach (var entry in candidates)
        {
            if (_kindFilter is { } kind && entry.Kind != kind) continue;
            if (query.Length > 0 && !MatchesQuery(entry, query)) continue;

            var copies = byPath.GetValueOrDefault(entry.RelativePath) ?? [entry];
            var state = copies.Count < 2
                ? GroupState.Unique
                : _groupStates.GetValueOrDefault(entry.RelativePath, GroupState.Unknown);
            if (_differsOnly && state != GroupState.Differs) continue;

            var hits = _contentHits?.GetValueOrDefault(entry.FullPath);
            if (_contentHits is not null && (hits is null || hits.Count == 0)) continue;

            rows.Add(new FileRow(entry, copies, state,
                pinned: pins.Contains(ConfigHubService.PinKey(entry.PackId, entry.RelativePath)),
                errorCount: ErrorsFor(entry).Count,
                hitCount: hits?.Count ?? 0));
        }

        // A pin whose file has gone is shown greyed rather than dropped — the instance may just be
        // mid-sync, and silently losing a pin is worse than showing a dead one.
        if (!_differsOnly && _contentHits is null) rows.AddRange(MissingPinRows(rows, query));

        Sort(rows);
        _rows.Clear();
        foreach (var row in rows) _rows.Add(row);

        RebuildKindChips(counts, candidates.Count);
        UpdateEmptyState(query);
        UpdateSubLabel(candidates.Count);

        if (previous is { } key)
        {
            var restored = _rows.FirstOrDefault(r => r.PackId == key.Item1 && r.RelativePath == key.Item2);
            if (restored is not null) FileList.SelectedItem = restored;
            else ShowDetail(null);
        }
    }

    private static bool MatchesQuery(Entry entry, string query) =>
        entry.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase)
        || entry.PackName.Contains(query, StringComparison.OrdinalIgnoreCase);

    private IEnumerable<FileRow> MissingPinRows(List<FileRow> present, string query)
    {
        var have = present.Select(r => ConfigHubService.PinKey(r.PackId, r.RelativePath))
                          .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pin in App.State.Settings.ConfigHubPins)
        {
            if (have.Contains(pin)) continue;
            var slash = pin.IndexOf('/');
            if (slash <= 0 || !Guid.TryParseExact(pin[..slash], "N", out var packId)) continue;
            var rel = pin[(slash + 1)..];
            if (SelectedPackId is { } only && only != packId) continue;
            if (_kindFilter is not null) continue;
            if (query.Length > 0 && !rel.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            // The file is gone from the scan, so there is nothing to size or date.
            var ghost = new Entry(packId, PackName(packId), rel, "", 0, DateTime.MinValue, Kind.Config);
            yield return new FileRow(ghost, [], GroupState.Unique, pinned: true, errorCount: 0,
                hitCount: 0, missing: true);
        }
    }

    private void Sort(List<FileRow> rows)
    {
        // Pins always float to the top whatever the sort: the point of pinning is not having to look.
        Comparison<FileRow> comparison = _sort switch
        {
            SortMode.Name => (a, b) => string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase),
            SortMode.Modified => (a, b) => b.Entry.ModifiedUtc.CompareTo(a.Entry.ModifiedUtc),
            SortMode.Size => (a, b) => b.Entry.Size.CompareTo(a.Entry.Size),
            SortMode.Diff => (a, b) => DiffRank(a).CompareTo(DiffRank(b)),
            _ => (a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase)
        };
        rows.Sort((a, b) =>
        {
            if (a.Pinned != b.Pinned) return a.Pinned ? -1 : 1;
            var primary = comparison(a, b);
            return primary != 0
                ? primary
                : string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static int DiffRank(FileRow row) => row.State switch
    {
        GroupState.Differs => 0,
        GroupState.Unknown => 1,
        GroupState.Identical => 2,
        _ => 3
    };

    private void RebuildKindChips(Dictionary<Kind, int> counts, int total)
    {
        var chips = new List<KindChip>
        {
            new(null, "All", total, _kindFilter is null, "Every config file, script and default config"),
            new(Kind.Config, "Config", counts.GetValueOrDefault(Kind.Config), _kindFilter == Kind.Config,
                "game/config — the mod settings for this instance"),
            new(Kind.KubeJs, "KubeJS", counts.GetValueOrDefault(Kind.KubeJs), _kindFilter == Kind.KubeJs,
                "game/kubejs — startup, server and client scripts, plus the data and assets they generate"),
            new(Kind.DefaultConfigs, "Defaults", counts.GetValueOrDefault(Kind.DefaultConfigs),
                _kindFilter == Kind.DefaultConfigs,
                "game/defaultconfigs — what the pack copies into each NEW world, not what the game is using now"),
            new(Kind.KubeJsLog, "KubeJS logs", counts.GetValueOrDefault(Kind.KubeJsLog),
                _kindFilter == Kind.KubeJsLog,
                "game/logs/kubejs — where a script error is reported")
        };
        _kindChips.Clear();
        foreach (var chip in chips) _kindChips.Add(chip);
    }

    private void UpdateSubLabel(int scopeCount)
    {
        if (_packs.Count == 0)
        {
            SubLabel.Text = "Every instance's config files and KubeJS scripts, side by side.";
            return;
        }
        var differing = _groupStates.Count(kv => kv.Value == GroupState.Differs);
        SubLabel.Text = (SelectedPackId is { } id
                            ? $"{scopeCount:N0} file(s) in {PackName(id)}"
                            : $"{scopeCount:N0} distinct path(s) across {_packs.Count} instances") +
                        (differing > 0
                            ? $" · {differing:N0} path(s) differ between instances"
                            : _groupStates.Count > 0 ? " · every shared path matches" : "");
    }

    private void UpdateEmptyState(string query)
    {
        EmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_rows.Count > 0) return;

        if (_packs.Count == 0)
        {
            EmptyTitle.Text = "No instances yet";
            EmptyBody.Text = "Create or download an instance and its config files and KubeJS scripts appear here.";
        }
        else if (_contentHits is not null)
        {
            EmptyTitle.Text = "Nothing matched";
            EmptyBody.Text = $"No config or script contains “{ContentQueryBox.Text.Trim()}”. " +
                             "Very large files are skipped — the status line says how many.";
        }
        else if (_differsOnly)
        {
            EmptyTitle.Text = "Everything matches";
            EmptyBody.Text = "No file at the same path differs between your instances. " +
                             "Turn the filter off in the sort menu to see them all.";
        }
        else if (query.Length > 0 || _kindFilter is not null)
        {
            EmptyTitle.Text = "Nothing matched";
            EmptyBody.Text = "No file matched that filter. Clear the search or pick a different kind.";
        }
        else
        {
            EmptyTitle.Text = "No config files here yet";
            EmptyBody.Text = "This instance has no config, kubejs or defaultconfigs folder. " +
                             "That is normal until it has been launched or synced once.";
        }
    }

    private List<string> ErrorsFor(Entry entry)
    {
        if (entry.Kind != Kind.KubeJs) return [];
        if (!_kubeErrors.TryGetValue(entry.PackId, out var byScript)) return [];
        return byScript.GetValueOrDefault(entry.FileName) ?? [];
    }

    // ── the detail pane ──────────────────────────────────────────────────────

    private FileRow? SelectedRow => FileList.SelectedItems.Count == 1
        ? FileList.SelectedItem as FileRow
        : null;

    private void ShowDetail(FileRow? row)
    {
        var token = ++_detailToken;
        _copies.Clear();
        _hits.Clear();

        if (row is null || row.Missing)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            DetailPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        DetailPanel.Visibility = Visibility.Visible;
        DetailPlaceholder.Visibility = Visibility.Collapsed;

        DetailName.Text = row.FileName;
        DetailPath.Text = row.Entry.FullPath;
        DetailMeta.Text = $"{ConfigHubService.KindLabel(row.Entry.Kind)} · " +
                          $"{ConfigHubService.FormatSize(row.Entry.Size)} · " +
                          $"modified {ConfigHubService.FormatAge(row.Entry.ModifiedUtc)} · in {row.Entry.PackName}";
        DetailCompareButton.IsEnabled = row.Copies.Count > 1;

        var errors = ErrorsFor(row.Entry);
        ErrorPanel.Visibility = errors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Text = string.Join("\n", errors);

        CopiesHeader.Text = row.Copies.Count > 1
            ? $"ACROSS INSTANCES — {row.Copies.Count}"
            : "ACROSS INSTANCES — ONLY THIS ONE";

        if (_contentHits?.GetValueOrDefault(row.Entry.FullPath) is { Count: > 0 } hits)
        {
            HitsPanel.Visibility = Visibility.Visible;
            HitsHeader.Text = $"MATCHES — {hits.Count}";
            foreach (var hit in hits) _hits.Add(new HitRow(hit));
        }
        else
        {
            HitsPanel.Visibility = Visibility.Collapsed;
        }

        _ = LoadDetailAsync(row, token);
    }

    /// <summary>
    /// Fills in the per-instance comparison and the preview. Both read the disk, so both happen off the
    /// UI thread; <paramref name="token"/> drops the result if the user has since picked another row.
    /// </summary>
    private async Task LoadDetailAsync(FileRow row, int token)
    {
        var copies = row.Copies;
        var selfPath = row.Entry.FullPath;
        try
        {
            var (rows, preview, note) = await Task.Run(() =>
            {
                var selfHash = ConfigHubService.HashOrNull(selfPath);
                var list = copies
                    .OrderBy(c => c.PackName, StringComparer.OrdinalIgnoreCase)
                    .Select(c => new CopyRow(c,
                        isSelf: c.FullPath == selfPath,
                        same: c.FullPath == selfPath
                            ? true
                            : selfHash is not null && c.Size <= ConfigHubService.MaxInspectBytes
                                ? ConfigHubService.HashOrNull(c.FullPath) == selfHash
                                : null))
                    .ToList();

                return (list, ReadPreview(selfPath), PreviewNoteFor(selfPath));
            });

            if (token != _detailToken) return;
            foreach (var copy in rows) _copies.Add(copy);
            PreviewBox.Text = preview;
            PreviewNote.Text = note ?? "";
            PreviewNote.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
            PreviewBox.Visibility = note is null ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            if (token != _detailToken) return;
            PreviewBox.Visibility = Visibility.Collapsed;
            PreviewNote.Visibility = Visibility.Visible;
            PreviewNote.Text = "Could not read this file: " + ex.Message;
        }
    }

    /// <summary>How much of a file the preview shows. Enough to recognise any hand-written config;
    /// past that the built-in editor is the right tool and a WPF TextBox is not.</summary>
    private const int PreviewCharCap = 200_000;

    private static string ReadPreview(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > ConfigHubService.MaxInspectBytes) return "";
            var text = File.ReadAllText(path);
            return text.Length > PreviewCharCap
                ? text[..PreviewCharCap] + "\n\n… preview truncated — open it in the editor to see the rest."
                : text;
        }
        catch { return ""; }
    }

    private static string? PreviewNoteFor(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return "This file is no longer on disk.";
            if (info.Length > ConfigHubService.MaxInspectBytes)
                return $"{ConfigHubService.FormatSize(info.Length)} — too large to preview. " +
                       "This is almost always generated data rather than something to hand-edit.";
            if (info.Length == 0) return "This file is empty.";
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    // ── filters, sort, search ────────────────────────────────────────────────

    private void OnPackFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        Refresh();
        SaveFilter();
    }

    private void OnKindChipClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: KindChip chip }) return;
        _kindFilter = chip.Kind;
        Refresh();
        SaveFilter();
    }

    private void OnSortButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null) return;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    private void OnSortMenuOpened(object sender, RoutedEventArgs e)
    {
        SortNameMenuItem.IsChecked = _sort == SortMode.Name;
        SortPathMenuItem.IsChecked = _sort == SortMode.Path;
        SortModifiedMenuItem.IsChecked = _sort == SortMode.Modified;
        SortSizeMenuItem.IsChecked = _sort == SortMode.Size;
        SortDiffMenuItem.IsChecked = _sort == SortMode.Diff;
        DiffersOnlyMenuItem.IsChecked = _differsOnly;
    }

    private void OnSortMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag }) return;
        if (Enum.TryParse<SortMode>(tag, out var mode)) _sort = mode;
        Refresh();
        SaveFilter();
    }

    private void OnDiffersOnlyClick(object sender, RoutedEventArgs e)
    {
        _differsOnly = DiffersOnlyMenuItem.IsChecked;
        Refresh();
        SaveFilter();
    }

    private void OnSearchToggle(object sender, RoutedEventArgs e) => OpenSearch(!IsSearchOpen);

    private bool IsSearchOpen => SearchBox.Visibility == Visibility.Visible;

    private void OpenSearch(bool open)
    {
        SearchBox.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        CompactSearchHost.Width = open ? 240 : 36;
        if (open) SearchBox.Focus();
        else if (SearchBox.Text.Length > 0) { SearchBox.Text = ""; Refresh(); }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded) Refresh();
    }

    private void OnSearchPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        OpenSearch(false);
        e.Handled = true;
    }

    private void OnSearchLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (SearchBox.Text.Length == 0) OpenSearch(false);
    }

    // ── content search ───────────────────────────────────────────────────────

    private void OnToggleContentSearch(object sender, RoutedEventArgs e)
    {
        var opening = ContentSearchBar.Visibility != Visibility.Visible;
        ContentSearchBar.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
        SearchInFilesButton.Content = opening ? "Close file search" : "Search in files";
        if (opening) { ContentQueryBox.Focus(); return; }

        if (_contentHits is null) return;
        _contentHits = null;
        Refresh();
        StatusLabel.Text = "";
    }

    private void OnContentQueryKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnRunContentSearch(sender, e);
    }

    private async void OnRunContentSearch(object sender, RoutedEventArgs e)
    {
        var query = ContentQueryBox.Text?.Trim() ?? "";
        if (query.Length < 2)
        {
            StatusLabel.Text = "Type at least two characters to search inside files.";
            return;
        }

        _workCts?.Cancel();
        _workCts = new CancellationTokenSource();
        var ct = _workCts.Token;
        SetBusy(true, $"Searching {_scan.Count:N0} files for “{query}”…");
        ContentSearchGoButton.Visibility = Visibility.Collapsed;
        ContentSearchCancelButton.Visibility = Visibility.Visible;

        try
        {
            var files = SelectedPackId is { } packId
                ? _scan.Where(f => f.PackId == packId).ToList()
                : _scan;
            var total = files.Count;
            var progress = new Progress<int>(done =>
                StatusLabel.Text = $"Searching… {done:N0} of {total:N0} files");

            var result = await Task.Run(
                () => ConfigHubService.SearchContents(files, query, maxHits: 2000, progress, ct), ct);

            _contentHits = result.Hits
                .GroupBy(h => h.File.FullPath)
                .ToDictionary(g => g.Key, g => g.ToList());
            Refresh();

            StatusLabel.Text =
                $"{result.Hits.Count:N0} match(es) in {_contentHits.Count:N0} file(s) " +
                $"of {result.FilesRead:N0} read" +
                (result.Skipped > 0
                    ? $" · {result.Skipped:N0} skipped (too large, unreadable, or past the 2,000-match limit)"
                    : "");
        }
        catch (OperationCanceledException) { StatusLabel.Text = "Search cancelled."; }
        catch (Exception ex) { StatusLabel.Text = "Search failed: " + ex.Message; }
        finally
        {
            SetBusy(false);
            ContentSearchGoButton.Visibility = Visibility.Visible;
            ContentSearchCancelButton.Visibility = Visibility.Collapsed;
        }
    }

    private void OnCancelContentSearch(object sender, RoutedEventArgs e) => _workCts?.Cancel();
    private void OnCancelWork(object sender, RoutedEventArgs e) => _workCts?.Cancel();

    // ── list events ──────────────────────────────────────────────────────────

    private void OnFileSelected(object sender, SelectionChangedEventArgs e) => ShowDetail(SelectedRow);

    private void OnFileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedRow is { Missing: false } row) OpenInEditor(row.Entry);
    }

    private async void OnFileListKeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            switch (e.Key)
            {
                case Key.Enter when SelectedRow is { Missing: false } row:
                    OpenInEditor(row.Entry);
                    e.Handled = true;
                    break;
                case Key.Delete when SelectedRows().Count > 0:
                    e.Handled = true;
                    await DeleteAsync(SelectedRows());
                    break;
            }
        }
        catch (Exception ex) { StatusLabel.Text = "That did not work: " + ex.Message; }
    }

    /// <summary>
    /// Page-level shortcuts. Ctrl+F opens the name filter, Ctrl+Shift+F the content search and F5
    /// re-scans — the three things this page is used for repeatedly.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (ctrl && shift && e.Key == Key.F)
        {
            if (ContentSearchBar.Visibility != Visibility.Visible) OnToggleContentSearch(this, e);
            ContentQueryBox.Focus();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.F)
        {
            OpenSearch(true);
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            OnRefresh(this, e);
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    private List<FileRow> SelectedRows() => FileList.SelectedItems.OfType<FileRow>().ToList();

    /// <summary>The rows an action applies to: the whole selection when the click came from the list's
    /// own selection, otherwise just the row whose button was pressed.</summary>
    private List<FileRow> RowsFor(object sender)
    {
        var row = (sender as FrameworkElement)?.Tag as FileRow
                  ?? ((sender as FrameworkElement)?.DataContext as FileRow)
                  ?? SelectedRow;
        var selection = SelectedRows();
        if (row is not null && selection.Contains(row)) return selection;
        return row is null ? selection : [row];
    }

    private FileRow? RowFor(object sender) => RowsFor(sender).FirstOrDefault();

    // ── actions ──────────────────────────────────────────────────────────────

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        ConfigHubService.InvalidateAll();
        _ = LoadAsync(force: true);
    }

    private void OnRowEdit(object sender, RoutedEventArgs e)
    {
        if (RowFor(sender) is { Missing: false } row) OpenInEditor(row.Entry);
    }

    private void OnDetailEdit(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is { Missing: false } row) OpenInEditor(row.Entry);
    }

    private void OnCopyEdit(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: CopyRow copy }) OpenInEditor(copy.Entry);
    }

    /// <summary>Hands a file to the launcher's own text editor, which owns saving, encodings and
    /// unsaved-change prompts. Nothing here writes text files itself.</summary>
    private void OpenInEditor(Entry entry)
    {
        try
        {
            FileEditorWindow.OpenFileFor(_shell, entry.PackId, entry.PackName, entry.FullPath);
            HookEditorClose(entry.PackId, entry.PackName);
            StatusLabel.Text = $"Opened {entry.FileName} from {entry.PackName} in the editor.";
        }
        catch (Exception ex) { StatusLabel.Text = "Could not open the editor: " + ex.Message; }
    }

    /// <summary>Editor windows this page has already subscribed to, so opening a second file in the
    /// same window does not stack another refresh onto its close.</summary>
    private readonly HashSet<FileEditorWindow> _hookedEditors = new();

    /// <summary>
    /// Re-reads the edited instance once its editor window closes, so its rows stop showing the size
    /// and age the files had before they were edited.
    /// </summary>
    /// <remarks>
    /// <para>The editor saves without announcing it, and nothing else notices: the cheap folder stamp
    /// the scan cache is validated against only covers each root and its immediate children, so saving
    /// <c>config/jei/jei-client.ini</c> — an existing file, in a nested folder — changes neither. The
    /// row would keep its pre-edit numbers until somebody pressed Refresh.</para>
    /// <para>The window is found by its title instead of being handed to us because
    /// <see cref="FileEditorWindow"/> owns its one-window-per-instance table and this page must not
    /// reach into it. Two instances sharing a display name would at worst cost one extra refresh.</para>
    /// </remarks>
    private void HookEditorClose(Guid packId, string packName)
    {
        var window = Application.Current?.Windows.OfType<FileEditorWindow>()
            .FirstOrDefault(w => w.Title == $"Edit files · {packName}");
        if (window is null || !_hookedEditors.Add(window)) return;

        window.Closed += OnEditorClosed;

        void OnEditorClosed(object? sender, EventArgs e)
        {
            window.Closed -= OnEditorClosed;
            _hookedEditors.Remove(window);
            // Drop the cached walk either way — a later visit to this page must not read it back.
            ConfigHubService.Invalidate(packId);
            // force: false so only the invalidated instance is re-walked; the others stay cached.
            if (IsLoaded) _ = LoadAsync(force: false);
        }
    }

    private void OnRowReveal(object sender, RoutedEventArgs e) => Reveal(RowFor(sender)?.Entry);
    private void OnDetailReveal(object sender, RoutedEventArgs e) => Reveal(SelectedRow?.Entry);

    private void Reveal(Entry? entry)
    {
        if (entry is null || entry.FullPath.Length == 0) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{entry.FullPath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private void OnRowCopyPath(object sender, RoutedEventArgs e)
    {
        var rows = RowsFor(sender);
        if (rows.Count == 0) return;
        var text = string.Join(Environment.NewLine, rows.Select(r => r.Entry.FullPath));
        StatusLabel.Text = ClipboardHelper.TrySetText(text)
            ? $"Copied {rows.Count} path(s) to the clipboard."
            : "The clipboard is locked by another program — try again in a moment.";
    }

    private void OnDetailMore(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null) return;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    private void OnRowPin(object sender, RoutedEventArgs e)
    {
        var rows = RowsFor(sender);
        if (rows.Count == 0) return;
        try
        {
            var pins = App.State.Settings.ConfigHubPins;
            // A mixed selection pins everything rather than toggling each one, which would leave the
            // user unsure what happened.
            var pinAll = rows.Any(r => !r.Pinned);
            foreach (var row in rows)
            {
                var key = ConfigHubService.PinKey(row.PackId, row.RelativePath);
                if (pinAll) { if (!pins.Contains(key)) pins.Add(key); }
                else pins.Remove(key);
            }
            App.State.Settings.Save();
            StatusLabel.Text = pinAll
                ? $"Pinned {rows.Count} file(s) to the top of the list."
                : $"Unpinned {rows.Count} file(s).";
            Refresh();
        }
        catch (Exception ex) { StatusLabel.Text = "Could not save the pin: " + ex.Message; }
    }

    // ── compare ──────────────────────────────────────────────────────────────

    private async void OnRowCompare(object sender, RoutedEventArgs e)
    {
        try { await CompareAsync(RowFor(sender), null); }
        catch (Exception ex) { StatusLabel.Text = "Compare failed: " + ex.Message; }
    }

    private async void OnDetailCompare(object sender, RoutedEventArgs e)
    {
        try { await CompareAsync(SelectedRow, null); }
        catch (Exception ex) { StatusLabel.Text = "Compare failed: " + ex.Message; }
    }

    private async void OnCopyCompare(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement { Tag: CopyRow copy })
                await CompareAsync(SelectedRow, copy.Entry);
        }
        catch (Exception ex) { StatusLabel.Text = "Compare failed: " + ex.Message; }
    }

    private async Task CompareAsync(FileRow? row, Entry? against)
    {
        if (row is null || row.Missing) return;
        if (row.Copies.Count < 2)
        {
            await AppDialog.MessageAsync(_shell, "Nothing to compare",
                $"Only {row.Entry.PackName} has a file at {row.RelativePath}. " +
                "Use “Copy to other instances” to put it somewhere else first.");
            return;
        }

        var card = new ConfigCompareCard(row.Copies, row.Entry, against, _shell);
        await _shell.ShowCardAsync(card, card.Completion, card.Close);

        if (card.CopiedSomething)
        {
            ConfigHubService.InvalidateAll();
            await LoadAsync(force: true);
        }
    }

    // ── copy across instances ────────────────────────────────────────────────

    private async void OnRowCopyTo(object sender, RoutedEventArgs e)
    {
        try { await CopyAsync(RowsFor(sender), wholeFolder: false); }
        catch (Exception ex) { StatusLabel.Text = "Copy failed: " + ex.Message; }
    }

    private async void OnDetailCopyTo(object sender, RoutedEventArgs e)
    {
        try { await CopyAsync(SelectedRows(), wholeFolder: false); }
        catch (Exception ex) { StatusLabel.Text = "Copy failed: " + ex.Message; }
    }

    private async void OnRowCopyFolder(object sender, RoutedEventArgs e)
    {
        try { await CopyAsync(RowsFor(sender), wholeFolder: true); }
        catch (Exception ex) { StatusLabel.Text = "Copy failed: " + ex.Message; }
    }

    private async Task CopyAsync(List<FileRow> rows, bool wholeFolder)
    {
        rows = rows.Where(r => !r.Missing).ToList();
        if (rows.Count == 0) return;

        var sourcePackId = rows[0].PackId;
        if (rows.Any(r => r.PackId != sourcePackId))
        {
            await AppDialog.MessageAsync(_shell, "One instance at a time",
                "The selected files come from different instances, so there is no single source to copy " +
                "from. Pick an instance in the filter first, then select the files.");
            return;
        }

        var paths = new List<string>();
        if (wholeFolder)
        {
            foreach (var folder in rows.Select(r => r.Entry.FolderPath).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (folder.Length == 0) continue;
                paths.AddRange(ConfigHubService.ExpandFolder(_scan, sourcePackId, folder));
            }
            paths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (paths.Count == 0)
            {
                StatusLabel.Text = "That file is not inside a folder that can be copied whole.";
                return;
            }
        }
        else
        {
            paths = rows.Select(r => r.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        var targets = _packs.Where(p => p.Id != sourcePackId).ToList();
        if (targets.Count == 0)
        {
            await AppDialog.MessageAsync(_shell, "Nowhere to copy to",
                "There is only one instance, so there is no other instance to copy these files into.");
            return;
        }

        var card = new ConfigCopyCard(PackName(sourcePackId), paths, targets, _shell);
        await _shell.ShowCardAsync(card, card.Completion, card.Close);
        if (card.ChosenTargets is not { Count: > 0 } chosen) return;

        _workCts?.Cancel();
        _workCts = new CancellationTokenSource();
        var ct = _workCts.Token;

        var sourceGameDir = App.State.Packs.GameDir(sourcePackId);
        var preview = card.Preview;
        var shared = preview?.SharedPacks.Where(name => chosen.Any(t => t.Name == name)).ToList() ?? [];
        var overwriting = preview?.Items.Count(i => i.Exists && chosen.Any(t => t.Id == i.PackId)) ?? 0;

        var message =
            $"Copy {paths.Count} file(s) from {PackName(sourcePackId)} into " +
            $"{chosen.Count} other instance(s).\n\n" +
            (overwriting > 0
                ? $"{overwriting} existing file(s) will be replaced. Each one is backed up next to itself " +
                  "as .bak-<timestamp> first.\n\n"
                : "Nothing existing will be replaced.\n\n") +
            (shared.Count > 0
                ? $"Careful: in {string.Join(", ", shared)} these paths are marked Shared, so the change " +
                  "will travel to everyone else on those packs the next time they sync.\n\n"
                : "") +
            "Continue?";

        if (!await AppDialog.ConfirmAsync(_shell, "Copy across instances", message, "Copy", "Cancel",
                danger: shared.Count > 0))
            return;

        SetBusy(true, "Copying…");
        try
        {
            var progress = new Progress<string>(what => StatusLabel.Text = "Copying " + what);
            var result = await Task.Run(() => ConfigHubService.Copy(
                sourceGameDir, paths, chosen, App.State.Packs, progress, ct), ct);

            StatusLabel.Text =
                $"Copied {result.Copied} file(s) into {chosen.Count} instance(s)" +
                (result.BackedUp > 0 ? $" · {result.BackedUp} replaced file(s) backed up" : "") +
                (result.Failures.Count > 0 ? $" · {result.Failures.Count} failed" : "") + ".";

            if (result.Failures.Count > 0)
                await AppDialog.MessageAsync(_shell, "Some files did not copy",
                    string.Join("\n", result.Failures.Take(12)) +
                    (result.Failures.Count > 12 ? $"\n… and {result.Failures.Count - 12} more." : ""));

            await LoadAsync(force: true);
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = "Copy cancelled — files already written were left in place and backed up.";
        }
        finally { SetBusy(false); }
    }

    // ── housekeeping: new, rename, delete, restore ───────────────────────────

    private async void OnNewFile(object sender, RoutedEventArgs e)
    {
        try
        {
            if (TargetPackId is not { } packId)
            {
                StatusLabel.Text = "There is no instance to create a file in.";
                return;
            }

            var seed = SelectedRow is { Missing: false } row ? row.Entry.FolderPath + "/" : "config/";
            var entered = await _shell.PromptAsync("New config file",
                "Path inside the instance's game folder — e.g. kubejs/server_scripts/recipes.js. " +
                "End it with a / to create an empty folder instead.", seed);
            if (string.IsNullOrWhiteSpace(entered)) return;

            var rel = entered.Trim().Replace('\\', '/').TrimStart('/');
            if (rel.Contains("..", StringComparison.Ordinal))
            {
                StatusLabel.Text = "That path steps outside the instance folder.";
                return;
            }

            var gameDir = App.State.Packs.GameDir(packId);
            var full = Path.Combine(gameDir, rel.Replace('/', Path.DirectorySeparatorChar));

            if (rel.EndsWith('/'))
            {
                Directory.CreateDirectory(full);
                StatusLabel.Text = $"Created folder {rel} in {PackName(packId)}.";
            }
            else if (File.Exists(full))
            {
                StatusLabel.Text = $"{rel} already exists in {PackName(packId)}.";
                return;
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                await File.WriteAllTextAsync(full, "");
                StatusLabel.Text = $"Created {rel} in {PackName(packId)}.";
                FileEditorWindow.OpenFileFor(_shell, packId, PackName(packId), full);
            }

            ConfigHubService.Invalidate(packId);
            await LoadAsync(force: true);
        }
        catch (Exception ex) { StatusLabel.Text = "Could not create that: " + ex.Message; }
    }

    private async void OnRowRename(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RowFor(sender) is not { Missing: false } row) return;
            var entered = await _shell.PromptAsync("Rename file",
                $"New name for {row.FileName} (it stays in {row.Entry.FolderPath}).", row.FileName);
            if (string.IsNullOrWhiteSpace(entered) || entered == row.FileName) return;

            var name = entered.Trim();
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                StatusLabel.Text = "That name contains characters Windows does not allow in a file name.";
                return;
            }

            var dir = Path.GetDirectoryName(row.Entry.FullPath)!;
            var dest = Path.Combine(dir, name);
            if (File.Exists(dest))
            {
                StatusLabel.Text = $"{name} already exists in that folder.";
                return;
            }

            File.Move(row.Entry.FullPath, dest);
            ConfigHubService.Invalidate(row.PackId);
            StatusLabel.Text = $"Renamed {row.FileName} to {name}. " +
                               "Remember that most mods look for an exact config file name.";
            await LoadAsync(force: true);
        }
        catch (Exception ex) { StatusLabel.Text = "Rename failed: " + ex.Message; }
    }

    private async void OnRowDelete(object sender, RoutedEventArgs e)
    {
        try { await DeleteAsync(RowsFor(sender)); }
        catch (Exception ex) { StatusLabel.Text = "Delete failed: " + ex.Message; }
    }

    private async Task DeleteAsync(List<FileRow> rows)
    {
        // A pinned row whose file is gone is removed from the pin list rather than from disk.
        var ghosts = rows.Where(r => r.Missing).ToList();
        rows = rows.Where(r => !r.Missing).ToList();
        if (ghosts.Count > 0)
        {
            foreach (var ghost in ghosts)
                App.State.Settings.ConfigHubPins.Remove(
                    ConfigHubService.PinKey(ghost.PackId, ghost.RelativePath));
            App.State.Settings.Save();
            StatusLabel.Text = $"Removed {ghosts.Count} pin(s) whose file no longer exists.";
            Refresh();
        }
        if (rows.Count == 0) return;

        var what = rows.Count == 1
            ? $"Delete {rows[0].RelativePath} from {rows[0].Entry.PackName}?"
            : $"Delete {rows.Count} files?";
        var body = what + "\n\n" +
                   (rows.Count > 1
                       ? string.Join("\n", rows.Take(8).Select(r => $"· {r.RelativePath} — {r.Entry.PackName}"))
                         + (rows.Count > 8 ? $"\n… and {rows.Count - 8} more." : "") + "\n\n"
                       : "") +
                   "A mod usually rewrites a missing config with its defaults on the next launch, but any " +
                   "settings in it are lost. This cannot be undone from here.";

        if (!await AppDialog.ConfirmAsync(_shell, "Delete file", body, "Delete", "Cancel", danger: true))
            return;

        var deleted = 0;
        var failures = new List<string>();
        foreach (var row in rows)
        {
            try { File.Delete(row.Entry.FullPath); deleted++; ConfigHubService.Invalidate(row.PackId); }
            catch (Exception ex) { failures.Add($"{row.RelativePath}: {ex.Message}"); }
        }

        StatusLabel.Text = $"Deleted {deleted} file(s)." +
                           (failures.Count > 0 ? $" {failures.Count} could not be deleted." : "");
        if (failures.Count > 0)
            await AppDialog.MessageAsync(_shell, "Some files were not deleted",
                string.Join("\n", failures.Take(10)));
        await LoadAsync(force: true);
    }

    private async void OnRowRestoreBackup(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RowFor(sender) is not { Missing: false } row) return;
            var backups = await Task.Run(() => ConfigHubService.FindBackups(row.Entry.FullPath));
            if (backups.Count == 0)
            {
                await AppDialog.MessageAsync(_shell, "No backups",
                    $"There is no backup of {row.FileName} in {row.Entry.PackName}. " +
                    "Backups are written here whenever this page overwrites a file.");
                return;
            }

            // Most recently taken, not newest by mtime — a backup inherits its source file's
            // last-write time, so mtime is the age of the content, not of the backup.
            var newest = backups[0];
            if (!await AppDialog.ConfirmAsync(_shell, "Restore backup",
                    $"Restore {row.FileName} in {row.Entry.PackName} from the backup taken " +
                    $"{ConfigHubService.FormatAge(ConfigHubService.BackupTakenUtc(newest))} " +
                    $"({ConfigHubService.FormatSize(newest.Length)})?\n\n" +
                    (backups.Count > 1 ? $"There are {backups.Count} backups; this restores the newest.\n\n" : "") +
                    "The file as it stands now is backed up first, so this is reversible.",
                    "Restore", "Cancel"))
                return;

            var path = row.Entry.FullPath;
            var source = newest.FullName;
            await Task.Run(() =>
            {
                File.Copy(path, $"{path}.bak-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
                File.Copy(source, path, overwrite: true);
            });
            ConfigHubService.Invalidate(row.PackId);
            StatusLabel.Text = $"Restored {row.FileName} in {row.Entry.PackName}.";
            await LoadAsync(force: true);
        }
        catch (Exception ex) { StatusLabel.Text = "Restore failed: " + ex.Message; }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (TargetPackId is not { } packId) { StatusLabel.Text = "There is no instance to open."; return; }
        try
        {
            var dir = Path.Combine(App.State.Packs.GameDir(packId), "config");
            if (!Directory.Exists(dir)) dir = App.State.Packs.GameDir(packId);
            if (!Directory.Exists(dir))
            {
                StatusLabel.Text = $"{PackName(packId)} has no game folder yet — launch or sync it once.";
                return;
            }
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    // ── odds and ends ────────────────────────────────────────────────────────

    private void SetBusy(bool busy, string? status = null)
    {
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyCancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (status is not null) StatusLabel.Text = status;
    }

    private Guid? _restoredPackId;

    /// <summary>
    /// Restores the filter the page was left on. The stored form is
    /// <c>kind|packId|sort|differsOnly</c> — this page owns the string, so it is kept deliberately
    /// forgiving: anything it cannot parse falls back to the default rather than throwing.
    /// </summary>
    private void RestoreFilter()
    {
        var saved = App.State.Settings.ConfigHubLastFilter;
        if (string.IsNullOrWhiteSpace(saved)) return;
        var parts = saved.Split('|');
        if (parts.Length > 0 && Enum.TryParse<Kind>(parts[0], out var kind)) _kindFilter = kind;
        if (parts.Length > 1 && Guid.TryParseExact(parts[1], "N", out var packId)) _restoredPackId = packId;
        if (parts.Length > 2 && Enum.TryParse<SortMode>(parts[2], out var sort)) _sort = sort;
        if (parts.Length > 3) _differsOnly = parts[3] == "1";
    }

    private void SaveFilter()
    {
        try
        {
            App.State.Settings.ConfigHubLastFilter =
                $"{(_kindFilter is { } k ? k.ToString() : "")}|" +
                $"{(SelectedPackId is { } id ? id.ToString("N") : "")}|" +
                $"{_sort}|{(_differsOnly ? "1" : "0")}";
            App.State.Settings.Save();
        }
        catch { /* a settings write that fails must not break the filter the user just clicked */ }
    }

    // ── row models ───────────────────────────────────────────────────────────

    private sealed record PackFilterItem(Guid? Id, string Label);

    /// <summary>One chip in the kind strip.</summary>
    private sealed class KindChip(Kind? kind, string label, int count, bool selected, string hint)
    {
        public Kind? Kind { get; } = kind;
        public string Label { get; } = label;
        public string CountLabel { get; } = count.ToString("N0");
        public bool IsSelected { get; } = selected;
        public string Hint { get; } = hint;
    }

    /// <summary>
    /// One row: a game-relative path, the instance whose copy this row shows, and every instance that
    /// has a file at the same path.
    /// </summary>
    /// <remarks>
    /// The labels are computed once here rather than bound through converters because the list is
    /// rebuilt wholesale on every filter change. Nothing on this class holds a <c>Brush</c> — the row
    /// exposes <see cref="StateName"/> and the XAML picks the colour with a DynamicResource, so a theme
    /// change repaints the list instead of leaving stale colours behind.
    /// </remarks>
    private sealed class FileRow
    {
        public FileRow(Entry entry, IReadOnlyList<Entry> copies, GroupState state,
                       bool pinned, int errorCount, int hitCount, bool missing = false)
        {
            Entry = entry;
            Copies = copies;
            State = state;
            Pinned = pinned;
            Missing = missing;

            var differing = state == GroupState.Differs ? " · differs somewhere" : "";
            var where = missing
                ? "file no longer on disk"
                : copies.Count > 1
                    ? $"in {copies.Count} instances{differing}"
                    : $"only in {entry.PackName}";

            MetaLabel = missing
                ? $"{entry.RelativePath} · {where}"
                : hitCount > 0
                    ? $"{entry.RelativePath} · {hitCount} match(es) · {where}"
                    : $"{entry.RelativePath} · {ConfigHubService.FormatSize(entry.Size)} · " +
                      $"{ConfigHubService.FormatAge(entry.ModifiedUtc)} · {where}";

            FullPathHint = missing ? entry.RelativePath : entry.FullPath;
            ErrorVisibility = errorCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            ErrorHint = errorCount > 0
                ? $"KubeJS logged {errorCount} error line(s) naming this script — open it to see them."
                : "";
        }

        public Entry Entry { get; }
        public IReadOnlyList<Entry> Copies { get; }
        public GroupState State { get; }
        public bool Pinned { get; }
        public bool Missing { get; }

        public Guid PackId => Entry.PackId;
        public string RelativePath => Entry.RelativePath;
        public string FileName => Entry.FileName;
        public string MetaLabel { get; }
        public string FullPathHint { get; }

        public string Glyph => Entry.Kind switch
        {
            Kind.KubeJs => "",      // code
            Kind.KubeJsLog => "",   // warning-ish: a log is only read when something went wrong
            _ => ""                 // document
        };

        public double MissingOpacity => Missing ? 0.45 : 1.0;

        /// <summary>The value the XAML's DataTriggers switch on. Kept as a string rather than the enum
        /// so the trigger comparison works without a converter.</summary>
        public string StateName => State.ToString();

        public string StateLabel => State switch
        {
            GroupState.Differs => "DIFFERS",
            GroupState.Identical => "SAME",
            GroupState.Unknown => "?",
            _ => "ONLY HERE"
        };

        public Visibility StateVisibility =>
            Missing || State == GroupState.Unique ? Visibility.Collapsed : Visibility.Visible;

        public string StateHint => State switch
        {
            GroupState.Differs =>
                $"{Copies.Count} instances have a file here and at least one differs. Compare them to see how.",
            GroupState.Identical => $"All {Copies.Count} instances have exactly the same file here.",
            GroupState.Unknown => "Too large to compare, or locked by a running game.",
            _ => $"Only {Entry.PackName} has a file at this path."
        };

        public Visibility CompareVisibility =>
            Copies.Count > 1 && !Missing ? Visibility.Visible : Visibility.Collapsed;

        public string CompareHint => $"Compare this file across {Copies.Count} instances";

        public Visibility ErrorVisibility { get; }
        public string ErrorHint { get; }

        public string PinGlyph => Pinned ? "" : "";
        public string PinHint => Pinned ? "Unpin" : "Pin to the top of the list";
    }

    /// <summary>One instance's copy of the selected file, in the detail pane.</summary>
    private sealed class CopyRow(Entry entry, bool isSelf, bool? same)
    {
        public Entry Entry { get; } = entry;
        public string PackName => Entry.PackName;

        public string MetaLabel =>
            $"{ConfigHubService.FormatSize(Entry.Size)} · modified {ConfigHubService.FormatAge(Entry.ModifiedUtc)}";

        public string StateLabel => isSelf ? "THIS ONE" : same switch
        {
            true => "SAME",
            false => "DIFFERS",
            _ => "?"
        };

        public string StateHint => isSelf
            ? "The copy shown above."
            : same switch
            {
                true => "Byte-for-byte identical to the copy shown above.",
                false => "Differs from the copy shown above — compare to see how.",
                _ => "Could not be read to compare: too large, or locked by a running game."
            };

        public Visibility CompareVisibility => isSelf ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>One content-search hit inside the selected file.</summary>
    private sealed class HitRow(ConfigHubService.SearchHit hit)
    {
        public string LineLabel { get; } = $"L{hit.LineNumber}";
        public string Line { get; } = hit.Line;
    }
}
