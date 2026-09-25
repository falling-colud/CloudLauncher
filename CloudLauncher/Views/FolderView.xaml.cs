using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>Folder-navigable file browser for the game/, local/ and shared/ columns. Lists from an
/// <see cref="InstanceFileIndex"/> and supports drag-and-drop between sibling views and from
/// Explorer.</summary>
/// <remarks>
/// <para>A view reads its own index through <see cref="InstanceFileIndexes"/> (shared by views on the
/// same root) or gets one from its host via <see cref="Index"/>, in which case a refresh only
/// re-lists.</para>
/// <para>Rows are diffed into one bound collection (<see cref="ListDiff"/>) and the viewport is
/// anchored (<see cref="ListScrollAnchor"/>), so selection, scroll and sort survive a re-list.</para>
/// </remarks>
public partial class FolderView : UserControl
{
    // ── public surface ───────────────────────────────────────────────────────

    /// <summary>Absolute path to the root folder (game/ or local/).</summary>
    public string? Root { get; set; }

    /// <summary>Rules for badging files. Required when <see cref="ShowOnlyShared"/> is true.</summary>
    public List<PackRule>? Rules { get; set; }

    /// <summary>When set on the game/ view, files mirrored to local/ are hidden but remain on disk.</summary>
    public string? MirrorLocalRoot { get; set; }
    public string? MirrorSharedRoot { get; set; }

    /// <summary>When true on the game/ view, files matching a "shared" rule are hidden (they belong in
    /// the Shared column) but stay on disk, so the instance still sees them at launch. Requires
    /// <see cref="Rules"/>.</summary>
    public bool HideShared { get; set; }

    /// <summary>When true this view is a "sync preview": it shares the game view's Root but only shows
    /// files whose rule action is Shared, i.e. what will be uploaded.</summary>
    public bool ShowOnlyShared { get; set; }

    /// <summary>The host's own read of the folder, when the host owns the scan. With this set,
    /// <see cref="RefreshAsync"/> re-lists from it without touching the disk; the host re-scans and
    /// calls <see cref="Relist"/>. Null (the default) means the view reads the disk itself.</summary>
    public InstanceFileIndex? Index { get; set; }

    /// <summary>Raised when the selection changes.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Raised when a file (not a folder) is double-clicked. Carries the path relative to
    /// <see cref="Root"/>.</summary>
    public event Action<string>? FileActivated;

    /// <summary>Fired when files have been dropped onto this FolderView from another one.
    /// Subscriber should perform the move (the source root tells you where files came from).</summary>
    /// <remarks>For hosts that only handle a drop from the other pane. A same-root drop only arrives
    /// here when the target is a <see cref="ShowOnlyShared"/> view; hosts that subscribe
    /// <see cref="PaneDropped"/> get those drops there instead.</remarks>
    public event Action<string /*sourceRoot*/, string /*destRoot*/, IReadOnlyList<string> /*relPaths*/>? FilesDropped;

    /// <summary>A drop from a sibling view on the same root, with the view it came from. Game onto
    /// Shared means "share these", Shared onto Game "stop sharing these". Nothing moves on disk, so the
    /// host is told which pane rather than which folder.</summary>
    public event Action<FolderView /*source*/, IReadOnlyList<string> /*relPaths*/>? PaneDropped;

    /// <summary>A drop of this view's own rows onto one of its folder rows: move them into it.
    /// Without a handler, same-view drops do nothing.</summary>
    public event Action<IReadOnlyList<string> /*relPaths*/, string /*folderRel*/>? MoveIntoFolderRequested;

    /// <summary>Fired when files or folders are dropped in from outside the launcher (Explorer, the
    /// desktop, a browser download). Carries this view's root, the open folder and the dropped absolute
    /// paths. The subscriber does the copy, since it knows whether a rule, refresh or confirmation is
    /// needed.</summary>
    public event Action<string /*destRoot*/, string /*destRelativeDir*/, IReadOnlyList<string> /*absoluteSources*/>? ExternalFilesDropped;

    /// <summary>Del was pressed on the list with something selected.</summary>
    public event Action? DeleteRequested;

    /// <summary>F2 was pressed on the list with something selected.</summary>
    public event Action? RenameRequested;

    public bool IsNotAtRoot => _current.Length > 0;

    /// <summary>The folder currently open, relative to <see cref="Root"/> and always '/'-separated.
    /// Empty at the root. This is where a dropped or newly created file belongs.</summary>
    public string CurrentRelativeDir => _current;

    /// <summary>Returns selected files as paths relative to Root. Folders are skipped.</summary>
    public IReadOnlyList<string> GetSelectedFiles() =>
        FileList.SelectedItems
            .Cast<FolderEntry>()
            .Where(e => !e.IsFolder)
            .Select(e => e.RelativePath)
            .ToList();

    /// <summary>Returns selected entries, files and folders.</summary>
    public IReadOnlyList<FolderEntry> GetSelectedEntries() =>
        FileList.SelectedItems.Cast<FolderEntry>().ToList();

    /// <summary>Returns the first selected item regardless of whether it is a file or folder.</summary>
    public FolderEntry? SelectedEntry => FileList.SelectedItem as FolderEntry;

    /// <summary>
    /// Shows files an <see cref="RuleAction.Ignored"/> rule covers, badged and dimmed, instead of
    /// hiding them. Off by default, since the two-column view is about what syncs.
    /// </summary>
    public bool ShowIgnored { get; set; }

    /// <summary>Fills in <see cref="FolderEntry.Size"/> on folder rows.</summary>
    /// <remarks>Off by default. Cheap, since the index already holds every file's size; in a
    /// <see cref="ShowOnlyShared"/> view it is the total of the files that would upload.</remarks>
    public bool MeasureFolderSizes { get; set; }

    /// <summary>An extra predicate rows must satisfy (the file-management page's kind and
    /// sync-state chips). Null means no extra filtering.</summary>
    /// <remarks>Applied on the UI thread over the finished listing, so it may close over view state
    /// safely. Set it and call <see cref="Relist"/>.</remarks>
    public Func<FolderEntry, bool>? EntryFilter { get; set; }

    /// <summary>Hides the built-in search box for a host that supplies its own.</summary>
    public bool ShowSearchBox
    {
        get => _showSearchBox;
        set
        {
            _showSearchBox = value;
            SearchHost.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>The text in the filter box, whether the user or the host put it there.</summary>
    public string FilterText => _filter;

    /// <summary>Sets the filter box from outside, for one search field above two panes.</summary>
    public void SetFilterText(string text)
    {
        if (string.Equals(FilterBox.Text, text, StringComparison.Ordinal)) return;
        FilterBox.Text = text;   // OnFilterChanged does the rest, including the debounce
    }

    /// <summary>True while the box holds a whole-instance search rather than a folder filter.</summary>
    public bool IsSearching => _searchingTree;

    /// <summary>The folder currently open, as an absolute path.</summary>
    public string? CurrentAbsoluteDir => Root is null
        ? null
        : _current.Length == 0 ? Root : Path.Combine(Root, _current.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The rows actually on screen, in the order they are shown.</summary>
    public IReadOnlyList<FolderEntry> VisibleEntries => _rows;

    public FolderSortField SortField => _sortField;
    public bool SortDescending => _sortDescending;

    /// <summary>Sets the sort from outside (the page's sort menu drives both panes at once).</summary>
    public void SetSort(FolderSortField field, bool descending)
    {
        if (_sortField == field && _sortDescending == descending) return;
        _sortField = field;
        _sortDescending = descending;
        BindEntries();
    }

    public void SelectAll() => FileList.SelectAll();
    public void ClearSelection() => FileList.SelectedItems.Clear();

    /// <summary>Raised after the view moves to a different folder, with the new relative path.</summary>
    public event Action<string>? Navigated;

    /// <summary>Raised when a refresh finishes, so a host can relabel counts it shows elsewhere.</summary>
    public event Action? Refreshed;

    // ── internals ────────────────────────────────────────────────────────────

    private string _current = ""; // path relative to Root, always uses '/'
    private bool _showSearchBox = true;

    // A read is in flight and nothing is on screen yet. The empty label must not say "this folder is
    // empty" meanwhile, or people create the file that was about to appear.
    private bool _busy;

    // How many rows the host's chip filter removed from the last listing, so the empty label can
    // say "your filters" rather than "this folder is empty".
    private int _hiddenByFilter;

    // Each refresh or navigation is a pass; a pass that finishes after a newer one started must not
    // paint over it.
    private int _pass;

    // The index this view read for itself, when no host handed it one.
    private InstanceFileIndex? _ownIndex;

    // Paths mirrored to local/ or shared/, hidden from this view. Rebuilt on a full refresh only.
    private HashSet<string>? _mirrored;

    // Sorting, applied to the current listing. Folders always come before files.
    private List<FolderEntry> _entries = [];
    private FolderSortField _sortField = FolderSortField.Name;
    private bool _sortDescending;

    // Bound once for the life of the view and diffed in place; replacing ItemsSource would lose the
    // selection and scroll position.
    private readonly ObservableCollection<FolderEntry> _rows = [];
    private readonly ListScrollAnchor _anchor;

    // Drag-start tracking
    private Point _dragStart;
    private bool _isMouseDownInList;

    // Drop visuals: the row lit as a move target, and a stamp so a DragLeave that is really the
    // pointer moving between two rows does not flicker the highlight off and on.
    private FolderEntry? _dropRow;
    private int _dragStamp;

    // Filter box. Typing filters the open folder immediately; two or more characters also start a
    // whole-tree search, debounced since it is a pass over every file in the index.
    private string _filter = "";
    private readonly DispatcherTimer _filterDebounce = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private bool _searchingTree;

    /// <summary>A tree-wide search stops after this many hits.</summary>
    private const int SearchCap = 500;

    public FolderView()
    {
        InitializeComponent();
        FileList.ItemsSource = _rows;
        _anchor = new ListScrollAnchor(FileList);
        _filterDebounce.Tick += (_, _) =>
        {
            _filterDebounce.Stop();
            _ = NavigateAsync();
        };
    }

    public void Refresh() => _ = RefreshAsync();

    /// <summary>Puts the caret in the filter box (Ctrl+F on the Files tab).</summary>
    public void FocusFilter()
    {
        FilterBox.Focus();
        FilterBox.SelectAll();
    }

    /// <summary>Full refresh. A view reading for itself re-reads the disk; a view with a host-owned
    /// <see cref="Index"/> re-lists from it. Use after files, rules, root, or flags change.</summary>
    public Task RefreshAsync() => RefreshInternalAsync(fresh: true);

    /// <summary>Navigation refresh: lists from whatever index is already there, reading the disk
    /// only if there is none yet.</summary>
    private Task NavigateAsync() => RefreshInternalAsync(fresh: false);

    /// <summary>Marks a read as in flight, so an empty pane shows "Reading..." instead of "empty" until
    /// <see cref="Relist"/>. For a host that scans before handing over its index.</summary>
    public void BeginRead()
    {
        _busy = true;
        UpdateEmptyLabel();
    }

    private async Task RefreshInternalAsync(bool fresh)
    {
        var pass = ++_pass;
        if (Root is null)
        {
            _ownIndex = null;
            ClearRows();
            return;
        }

        var root = Root;
        var rules = Rules;
        _searchingTree = _filter.Length >= 2;

        var index = Index;
        var hostOwned = index is not null && InstanceFileIndex.SameRoot(index.Root, root);
        if (!hostOwned)
        {
            _busy = true;
            UpdateEmptyLabel();
            try
            {
                index = await InstanceFileIndexes.GetAsync(root, rules, fresh);
            }
            catch (Exception ex)
            {
                AppLog.LogError("files.index", ex);
                if (pass != _pass) return;
                _busy = false;
                ClearRows();
                return;
            }
            if (pass != _pass || !ReferenceEquals(Root, root)) return;
            _ownIndex = index;
        }

        // Reclassify only when the rules changed, on a worker thread: it is a pass over every file.
        if (!index!.RulesAre(rules))
        {
            await Task.Run(() => index.Reclassify(rules));
            if (pass != _pass) return;
        }

        if (fresh || _mirrored is null)
            _mirrored = BuildMirroredPathSet(MirrorLocalRoot, MirrorSharedRoot);

        Relist();
        Refreshed?.Invoke();
    }

    /// <summary>
    /// Re-lists the open folder (or the search) from the index, keeping selection, scroll position
    /// and sort. No disk access, so hosts call it after every rule change or re-scan.
    /// </summary>
    public void Relist()
    {
        var index = Index is { } handed && Root is not null && InstanceFileIndex.SameRoot(handed.Root, Root)
            ? handed
            : _ownIndex;
        if (Root is null || index is null)
        {
            ClearRows();
            return;
        }

        _searchingTree = _filter.Length >= 2;
        var options = Options();
        List<FolderEntry>? fresh;
        if (_searchingTree)
        {
            fresh = index.Search(_filter, options, SearchCap);
        }
        else
        {
            fresh = index.List(_current, options);
            if (fresh is null)
            {
                // The open folder is gone (deleted, renamed, moved): climb to the nearest one that still exists.
                _current = index.NearestExistingFolder(_current);
                Navigated?.Invoke(_current);
                fresh = index.List(_current, options) ?? [];
            }
        }

        _busy = false;
        _entries = ApplyEntryFilter(fresh);
        BuildBreadcrumb();
        UpButton.Visibility = IsNotAtRoot ? Visibility.Visible : Visibility.Collapsed;
        BindEntries();
        UpdateEmptyLabel();
    }

    private InstanceFileIndex.ListingOptions Options() => new()
    {
        ShowOnlyShared = ShowOnlyShared,
        HideShared = HideShared,
        ShowIgnored = ShowIgnored,
        MeasureFolders = MeasureFolderSizes,
        Mirrored = _mirrored,
        Filter = _searchingTree ? "" : _filter
    };

    private void ClearRows()
    {
        _entries = [];
        _hiddenByFilter = 0;
        _rows.Clear();
        BuildBreadcrumb();
        UpButton.Visibility = IsNotAtRoot ? Visibility.Visible : Visibility.Collapsed;
        UpdateEmptyLabel();
    }

    /// <summary>The chip filter, applied after the listing. Folders always pass, or you couldn't
    /// navigate to the files that match.</summary>
    private List<FolderEntry> ApplyEntryFilter(List<FolderEntry> entries)
    {
        _hiddenByFilter = 0;
        if (EntryFilter is not { } predicate) return entries;

        var kept = new List<FolderEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.IsFolder || predicate(entry)) kept.Add(entry);
            else _hiddenByFilter++;
        }
        return kept;
    }

    // ── filter box ───────────────────────────────────────────────────────────

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        var next = FilterBox.Text.Trim();
        if (_filter == next) return;
        _filter = next;

        // One character filters the open folder at once; two or more start a tree-wide search, which
        // waits for a pause in typing.
        _filterDebounce.Stop();
        if (_filter.Length >= 2) _filterDebounce.Start();
        else _ = NavigateAsync();
    }

    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            FilterBox.Clear();
            e.Handled = true;
        }
        else if (e.Key is Key.Down or Key.Enter && FileList.Items.Count > 0)
        {
            // Down/Enter jumps from the box to the first result, like the other search boxes in the app.
            FileList.SelectedIndex = 0;
            (FileList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
    }

    /// <summary>Del / F2 / F5 on the list. Delete and rename are only reported; the host does the
    /// work.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Handled) { base.OnKeyDown(e); return; }

        // Let the filter box keep its own keys.
        if (FilterBox.IsKeyboardFocusWithin && e.Key is not Key.F5) { base.OnKeyDown(e); return; }

        switch (e.Key)
        {
            case Key.Delete when FileList.SelectedItems.Count > 0 && DeleteRequested is not null:
                DeleteRequested.Invoke();
                e.Handled = true;
                return;
            case Key.F2 when FileList.SelectedItems.Count == 1 && RenameRequested is not null:
                RenameRequested.Invoke();
                e.Handled = true;
                return;
            case Key.F5:
                _ = RefreshAsync();
                e.Handled = true;
                return;
        }
        base.OnKeyDown(e);
    }

    public void NavigateTo(string relPath) { _current = relPath.Replace('\\', '/').Trim('/'); Navigated?.Invoke(_current); Refresh(); }
    public void NavigateToRoot()           { _current = ""; Navigated?.Invoke(_current); Refresh(); }

    /// <summary>In-view navigation: lists from the index already in hand and tells the host.</summary>
    private void GoTo(string relPath)
    {
        _current = relPath;
        Navigated?.Invoke(_current);
        _ = NavigateAsync();
    }

    // ── events ────────────────────────────────────────────────────────────────

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileList.SelectedItem is not FolderEntry entry) return;
        if (entry.IsFolder)
        {
            GoTo(entry.RelativePath);
            return;
        }
        // What "open" means is up to the host (the pack page opens the built-in editor).
        FileActivated?.Invoke(entry.RelativePath);
    }

    private void OnUp(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_current)) return;
        var idx = _current.LastIndexOf('/');
        GoTo(idx < 0 ? "" : _current[..idx]);
    }

    private void OnBreadcrumbClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path }) GoTo(path);
    }

    /// <summary>An empty folder, a filter that matched nothing and a column that is hiding everything
    /// all render as blank space; say which one it is.</summary>
    private void UpdateEmptyLabel()
    {
        if (_entries.Count > 0)
        {
            EmptyLabel.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyLabel.Visibility = Visibility.Visible;

        // While reading, say so rather than showing an empty state.
        if (_busy)
        {
            EmptyLabel.Text = _searchingTree ? "Searching this instance..." : "Reading this folder...";
            return;
        }

        if (_hiddenByFilter > 0)
        {
            EmptyLabel.Text = $"{_hiddenByFilter} item(s) here are hidden by the active filters.";
            return;
        }

        EmptyLabel.Text = _filter.Length switch
        {
            0 when ShowOnlyShared => "Nothing is marked for sharing yet. Select files on the left and press Share, or drag them here.",
            0 => "This folder is empty.",
            _ => $"Nothing matching '{_filter}'" + (_searchingTree ? " anywhere in this instance." : " in this folder.")
        };
    }

    /// <summary>Sorts the current listing per the active field/direction and folds it into the bound
    /// collection. Rows that survive keep their objects, so the selection and the scroll position
    /// survive with them.</summary>
    private void BindEntries()
    {
        var ordered = SortEntries(_entries).ToList();
        var before = Snapshot();
        var mark = _anchor.Take(before);

        ListDiff.Apply(_rows, ordered, RowKey, static (row, fresh) => row.CopyFrom(fresh), StringComparer.OrdinalIgnoreCase);
        UpdateSortIndicators();

        _anchor.Restore(mark, before, Snapshot());
    }

    /// <summary>A file and a folder can carry the same relative path across a delete-and-recreate,
    /// and they are not the same row.</summary>
    private static string RowKey(FolderEntry e) => (e.IsFolder ? "d:" : "f:") + e.RelativePath;

    private object[] Snapshot()
    {
        var copy = new object[_rows.Count];
        for (var i = 0; i < copy.Length; i++) copy[i] = _rows[i];
        return copy;
    }

    /// <summary>Folders always come first; within each group the active field/direction applies.</summary>
    private IEnumerable<FolderEntry> SortEntries(IEnumerable<FolderEntry> entries)
    {
        var grouped = entries.OrderByDescending(e => e.IsFolder);
        return _sortField switch
        {
            FolderSortField.Date => _sortDescending
                ? grouped.ThenByDescending(e => e.Modified).ThenBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                : grouped.ThenBy(e => e.Modified).ThenBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            FolderSortField.Size => _sortDescending
                ? grouped.ThenByDescending(e => e.Size).ThenBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                : grouped.ThenBy(e => e.Size).ThenBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            _ => _sortDescending
                ? grouped.ThenByDescending(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                : grouped.ThenBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        };
    }

    private void OnSortByName(object sender, RoutedEventArgs e) => ToggleSort(FolderSortField.Name);
    private void OnSortByDate(object sender, RoutedEventArgs e) => ToggleSort(FolderSortField.Date);
    private void OnSortBySize(object sender, RoutedEventArgs e) => ToggleSort(FolderSortField.Size);

    private void ToggleSort(FolderSortField field)
    {
        if (_sortField == field)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortField = field;
            // Date and Size start descending (newest, biggest first); Name starts A-Z.
            _sortDescending = field is FolderSortField.Date or FolderSortField.Size;
        }
        BindEntries();
    }

    private void UpdateSortIndicators()
    {
        var arrow = _sortDescending ? "▼" : "▲";         NameSortArrow.Text = _sortField == FolderSortField.Name ? arrow : "";
        DateSortArrow.Text = _sortField == FolderSortField.Date ? arrow : "";
        SizeSortArrow.Text = _sortField == FolderSortField.Size ? arrow : "";
    }

    private void BuildBreadcrumb()
    {
        var segments = new List<BreadcrumbSegment>
        {
            new() { Label = Root is not null ? Path.GetFileName(Root.TrimEnd(Path.DirectorySeparatorChar)) : "root", Path = "" }
        };

        if (!string.IsNullOrEmpty(_current))
        {
            var parts = _current.Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                var path = string.Join("/", parts.Take(i + 1));
                segments.Add(new BreadcrumbSegment { Label = parts[i], Path = path });
            }
        }

        Breadcrumb.ItemsSource = segments;
    }

    private static HashSet<string>? BuildMirroredPathSet(string? mirrorLocalRoot, string? mirrorSharedRoot)
    {
        if (mirrorLocalRoot is null && mirrorSharedRoot is null) return null;

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddMirroredPaths(mirrorLocalRoot, set);
        AddMirroredPaths(mirrorSharedRoot, set);
        return set;
    }

    private static void AddMirroredPaths(string? mirrorRoot, HashSet<string> set)
    {
        if (mirrorRoot is null || !Directory.Exists(mirrorRoot)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(mirrorRoot, "*", SearchOption.AllDirectories))
                set.Add(Path.GetRelativePath(mirrorRoot, file).Replace('\\', '/'));
        }
        catch { /* an unreadable mirror hides nothing */ }
    }

    // ── drag and drop ────────────────────────────────────────────────────────

    /// <summary>The view whose drag is in progress (OLE drags are modal, so there is only one). Static
    /// rather than on the payload, so nothing non-serialisable goes into a DataObject Explorer may
    /// marshal.</summary>
    internal static FolderView? ActiveDragSource { get; private set; }

    /// <summary>What a drop would do, decided the same way on every DragOver and on the Drop.</summary>
    private enum DropKind { None, Share, Unshare, MoveInto, Legacy, External }

    private readonly record struct DropVerdict(DropKind Kind, DragDropEffects Effects, string? Label, FolderEntry? Row);

    private void OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // WPF doesn't select an item on right-click, so context-menu actions would run against a stale
        // selection. Select the item under the cursor first, unless it is already part of a
        // multi-selection.
        if (e.OriginalSource is not DependencyObject src) return;
        if (ItemsControl.ContainerFromElement(FileList, src) is not ListBoxItem item) return;

        if (!item.IsSelected)
        {
            FileList.SelectedItems.Clear();
            item.IsSelected = true;
        }
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Ctrl/Shift clicks are for multi-select; don't track them for drag.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            _isMouseDownInList = false;
            return;
        }

        _isMouseDownInList = true;
        _dragStart = e.GetPosition(FileList);
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isMouseDownInList = false;
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isMouseDownInList || e.LeftButton != MouseButtonState.Pressed) return;
        if (Root is null) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;

        var pos = e.GetPosition(FileList);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var entries = GetSelectedEntries();
        if (entries.Count == 0) return;

        _isMouseDownInList = false;
        var paths = entries.Select(en => en.RelativePath).ToList();
        var payload = new FileMovePayload { SourceRoot = Root!, RelativePaths = paths };
        var data = new DataObject();
        data.SetData(typeof(FileMovePayload).FullName!, payload);

        // A pill under the cursor says what a drop would do ("Share 3 items"). GiveFeedback is raised
        // on the source, so this view draws it; the target picks the text through DragFeedback.
        var adorned = Window.GetWindow(this)?.Content as UIElement ?? this;
        var layer = AdornerLayer.GetAdornerLayer(adorned);
        DragLabelAdorner? pill = null;
        if (layer is not null)
        {
            pill = new DragLabelAdorner(adorned);
            layer.Add(pill);
        }

        void OnGiveFeedback(object s, GiveFeedbackEventArgs args)
        {
            var label = DragFeedback.Label;
            if (label is null)
            {
                pill?.Hide();
                args.UseDefaultCursors = true;
            }
            else
            {
                args.UseDefaultCursors = false;
                Mouse.SetCursor(Cursors.Arrow);
                if (pill is not null && DragFeedback.TryCursorPosition(adorned, out var at)) pill.Show(label, at);
                else pill?.Hide();
            }
            args.Handled = true;
        }

        ActiveDragSource = this;
        DragFeedback.Set(null);
        FileList.GiveFeedback += OnGiveFeedback;
        try
        {
            DragDrop.DoDragDrop(FileList, data, DragDropEffects.Move | DragDropEffects.Link);
        }
        finally
        {
            FileList.GiveFeedback -= OnGiveFeedback;
            if (pill is not null) layer!.Remove(pill);
            ActiveDragSource = null;
            DragFeedback.Set(null);
            ClearDropVisuals();
        }
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        _dragStamp++;
        var verdict = HandleDragOver(e.Data, e.GetPosition(FileList));
        e.Effects = verdict;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        // The pointer crossing from one row to the next raises Leave on the old row and Enter on the
        // new, both bubbling here; only a Leave with no Enter after it is a real exit.
        var stamp = ++_dragStamp;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_dragStamp != stamp) return;
            DragFeedback.Set(null);
            ClearDropVisuals();
        });
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        _dragStamp++;
        if (HandleDrop(e.Data, e.GetPosition(FileList))) e.Handled = true;
    }

    /// <summary>Decides and shows what a drop at this point would do. Returns the effect to report to
    /// the drag; the drop highlight and the cursor label are set as a side effect.</summary>
    internal DragDropEffects HandleDragOver(IDataObject data, Point? at)
    {
        var verdict = Classify(data, at);
        DragFeedback.Set(verdict.Label);
        ShowDropVisuals(verdict);
        return verdict.Effects;
    }

    /// <summary>Performs the drop <see cref="HandleDragOver"/> promised. True when something was
    /// done with it.</summary>
    internal bool HandleDrop(IDataObject data, Point? at)
    {
        var verdict = Classify(data, at);
        DragFeedback.Set(null);
        ClearDropVisuals();
        if (Root is null) return false;

        switch (verdict.Kind)
        {
            case DropKind.Share:
            case DropKind.Unshare:
                PaneDropped?.Invoke(ActiveDragSource!, Payload(data)!.RelativePaths);
                return true;
            case DropKind.MoveInto:
                MoveIntoFolderRequested?.Invoke(Payload(data)!.RelativePaths, verdict.Row!.RelativePath);
                return true;
            case DropKind.Legacy:
                var payload = Payload(data)!;
                FilesDropped?.Invoke(payload.SourceRoot, Root, payload.RelativePaths);
                return true;
            case DropKind.External:
                if (data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } dropped)
                {
                    ExternalFilesDropped?.Invoke(Root, _current, dropped);
                    return true;
                }
                return false;
            default:
                return false;
        }
    }

    private static FileMovePayload? Payload(IDataObject data)
    {
        try { return data.GetData(typeof(FileMovePayload).FullName!) as FileMovePayload; }
        catch { return null; }
    }

    /// <summary>The one place that decides what a drag over this view means.</summary>
    /// <remarks>A drag from a sibling pane on the same root changes rules, not files: onto a
    /// <see cref="ShowOnlyShared"/> view it shares, off one it unshares, wherever it lands. A drag within
    /// one pane onto one of its folder rows moves the files into it, if the host allows. Anything else
    /// follows the older <see cref="FilesDropped"/> contract.</remarks>
    private DropVerdict Classify(IDataObject data, Point? at)
    {
        var none = new DropVerdict(DropKind.None, DragDropEffects.None, null, null);
        if (Root is null) return none;

        if (data.GetDataPresent(typeof(FileMovePayload).FullName!))
        {
            var payload = Payload(data);
            if (payload is null) return none;
            var source = ActiveDragSource;
            var count = payload.RelativePaths.Count;
            var sameRoot = InstanceFileIndex.SameRoot(payload.SourceRoot, Root);

            if (ReferenceEquals(source, this))
            {
                if (MoveIntoFolderRequested is null || ShowOnlyShared) return none;
                var row = RowAt(at);
                if (row is not { IsFolder: true }) return none;
                // Ignore an item dropped onto itself or into one of its own subfolders.
                foreach (var rel in payload.RelativePaths)
                {
                    if (string.Equals(rel, row.RelativePath, StringComparison.OrdinalIgnoreCase)) return none;
                    if (row.RelativePath.StartsWith(rel + "/", StringComparison.OrdinalIgnoreCase)) return none;
                }
                return new DropVerdict(DropKind.MoveInto, DragDropEffects.Move,
                    $"Move into {row.DisplayName}", row);
            }

            if (source is not null && sameRoot && PaneDropped is not null)
            {
                if (ShowOnlyShared && !source.ShowOnlyShared)
                    return new DropVerdict(DropKind.Share, DragDropEffects.Link, Plural("Share", count), null);
                if (!ShowOnlyShared && source.ShowOnlyShared)
                    return new DropVerdict(DropKind.Unshare, DragDropEffects.Link, Plural("Unshare", count), null);
                return none;
            }

            // Without PaneDropped: a same-root drop is only accepted by a sync-preview view ("mark
            // shared"); any other cross-view drop is a move for the host.
            var isSelf = sameRoot && !ShowOnlyShared;
            return isSelf ? none : new DropVerdict(DropKind.Legacy, DragDropEffects.Move, null, null);
        }

        if (ExternalFilesDropped is not null && data.GetDataPresent(DataFormats.FileDrop))
        {
            var n = (data.GetData(DataFormats.FileDrop) as string[])?.Length ?? 0;
            var into = _current.Length == 0 ? Path.GetFileName(Root.TrimEnd(Path.DirectorySeparatorChar)) : _current;
            return new DropVerdict(DropKind.External, DragDropEffects.Copy,
                n > 0 ? $"{Plural("Copy", n)} into {into}/" : null, null);
        }

        return none;
    }

    private static string Plural(string verb, int count) =>
        count == 1 ? verb : $"{verb} {count} items";

    private FolderEntry? RowAt(Point? at)
    {
        if (at is not { } p) return null;
        if (FileList.InputHitTest(p) is not DependencyObject hit) return null;
        return (ItemsControl.ContainerFromElement(FileList, hit) as ListBoxItem)?.Content as FolderEntry;
    }

    private void ShowDropVisuals(DropVerdict verdict)
    {
        var row = verdict.Kind == DropKind.MoveInto ? verdict.Row : null;
        if (!ReferenceEquals(_dropRow, row))
        {
            if (_dropRow is not null) _dropRow.IsDropTarget = false;
            _dropRow = row;
            if (_dropRow is not null) _dropRow.IsDropTarget = true;
        }

        var paneWide = verdict.Kind is DropKind.Share or DropKind.Unshare or DropKind.Legacy or DropKind.External;
        DropHighlight.Visibility = paneWide ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearDropVisuals()
    {
        if (_dropRow is not null) _dropRow.IsDropTarget = false;
        _dropRow = null;
        DropHighlight.Visibility = Visibility.Collapsed;
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => SelectionChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>What a row is, for the kind chips.</summary>
public enum FolderEntryKind { Folder, Text, Jar, Image, WorldData, Log, Other }

/// <summary>One row in a <see cref="FolderView"/>.</summary>
/// <remarks>Path and file/folder flag are the row's identity; everything else notifies, so
/// <see cref="CopyFrom"/> can update the row on screen in place.</remarks>
public sealed class FolderEntry : System.ComponentModel.INotifyPropertyChanged
{
    public string RelativePath { get; init; } = "";
    public bool   IsFolder     { get; init; }

    public string DisplayName
    {
        get => _displayName;
        set => Set(ref _displayName, value, nameof(DisplayName));
    }
    private string _displayName = "";

    public string Badge
    {
        get => _badge;
        set => Set(ref _badge, value, nameof(Badge));
    }
    private string _badge = "";

    public bool IsAutoLocal
    {
        get => _isAutoLocal;
        set => Set(ref _isAutoLocal, value, nameof(IsAutoLocal), nameof(ToolTipText));
    }
    private bool _isAutoLocal;

    public bool IsAutoShared
    {
        get => _isAutoShared;
        set => Set(ref _isAutoShared, value, nameof(IsAutoShared), nameof(ToolTipText));
    }
    private bool _isAutoShared;

    /// <summary>An <see cref="RuleAction.Ignored"/> rule covers this file. Only ever true on a view
    /// that asked to see ignored files; everywhere else they are filtered out before this point.</summary>
    public bool IsIgnored
    {
        get => _isIgnored;
        set => Set(ref _isIgnored, value, nameof(IsIgnored), nameof(ToolTipText));
    }
    private bool _isIgnored;

    /// <summary>Held back from upload by <see cref="PrivateAssetPolicy"/>, whatever the rules say.</summary>
    public bool IsPrivate
    {
        get => _isPrivate;
        set => Set(ref _isPrivate, value, nameof(IsPrivate), nameof(PrivateBadgeVisibility), nameof(ToolTipText));
    }
    private bool _isPrivate;

    public FolderEntryKind Kind
    {
        get => _kind;
        set => Set(ref _kind, value, nameof(Kind), nameof(Icon));
    }
    private FolderEntryKind _kind = FolderEntryKind.Other;

    /// <summary>Theme resource key for this row's name colour. A key rather than a brush, so the
    /// template resolves it dynamically and follows theme changes.</summary>
    public string TextColorKey
    {
        get => _textColorKey;
        set => Set(ref _textColorKey, value, nameof(TextColorKey));
    }
    private string _textColorKey = "TextPrimaryBrush";

    /// <summary>Last-write time of the file or folder on disk.</summary>
    public DateTime Modified
    {
        get => _modified;
        set => Set(ref _modified, value, nameof(Modified), nameof(DateLabel));
    }
    private DateTime _modified;

    /// <summary>Bytes on disk. For a folder, the total inside it when the view measures folders,
    /// otherwise zero (shown as blank).</summary>
    public long Size
    {
        get => _size;
        set => Set(ref _size, value, nameof(Size), nameof(SizeLabel), nameof(ToolTipText));
    }
    private long _size;

    /// <summary>Lit while a same-pane drag would move its files into this folder.</summary>
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set => Set(ref _isDropTarget, value, nameof(IsDropTarget));
    }
    private bool _isDropTarget;

    /// <summary>Folds a fresh reading of the same path into this row. The row keeps its identity, so
    /// the list keeps its selection and its place.</summary>
    public void CopyFrom(FolderEntry fresh)
    {
        DisplayName = fresh.DisplayName;
        Badge = fresh.Badge;
        IsAutoLocal = fresh.IsAutoLocal;
        IsAutoShared = fresh.IsAutoShared;
        IsIgnored = fresh.IsIgnored;
        IsPrivate = fresh.IsPrivate;
        Kind = fresh.Kind;
        TextColorKey = fresh.TextColorKey;
        Modified = fresh.Modified;
        Size = fresh.Size;
    }

    public string Icon => IsFolder ? "📁" : Kind switch
    {
        FolderEntryKind.Jar       => "📦",
        FolderEntryKind.Image     => "🖼",
        FolderEntryKind.WorldData => "🗺",
        FolderEntryKind.Log       => "🧾",
        _                         => "📄"
    };

    public string DateLabel    => Modified == default ? "" : Modified.ToString("g");
    public string SizeLabel    => IsFolder && Size == 0 ? "" : FormatSize(Size);
    public FontWeight FontWeight => IsFolder ? FontWeights.SemiBold : FontWeights.Normal;

    public Visibility PrivateBadgeVisibility => IsPrivate ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The row's own tooltip: the path as it will be uploaded, plus what the rule badge means.</summary>
    public string ToolTipText
    {
        get
        {
            var head = IsFolder ? RelativePath + "/" : RelativePath;
            if (!IsFolder || Size > 0) head += $"\n{FormatSize(Size)}";
            if (IsIgnored) head += "\nIgnored - never uploaded, never downloaded.";
            else if (IsAutoShared) head += "\nShared - uploaded to the server with this instance.";
            else if (IsAutoLocal) head += "\nLocal - stays on this PC.";
            else if (IsFolder && Badge == "partly shared") head += "\nPartly shared - some of the files inside upload, some stay here.";
            if (IsPrivate) head += "\nPrivate - held back from every upload, whatever the rules say.";
            return head;
        }
    }

    /// <summary>Buckets a path for the kind chips.</summary>
    /// <remarks>Reads the folder as well as the extension: <c>saves/</c> is world data whatever the
    /// file inside it is called, and a <c>.json</c> under <c>logs/</c> is still a log.</remarks>
    public static FolderEntryKind KindOf(string relativePath)
    {
        var ext = System.IO.Path.GetExtension(relativePath);
        var top = relativePath.Split('/')[0];

        if (top.Equals("saves", StringComparison.OrdinalIgnoreCase)) return FolderEntryKind.WorldData;
        if (top.Equals("logs", StringComparison.OrdinalIgnoreCase) ||
            top.Equals("crash-reports", StringComparison.OrdinalIgnoreCase)) return FolderEntryKind.Log;

        return ext.ToLowerInvariant() switch
        {
            ".jar" or ".zip" or ".disabled" => FolderEntryKind.Jar,
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".ico" => FolderEntryKind.Image,
            ".dat" or ".dat_old" or ".mca" or ".mcr" or ".nbt" => FolderEntryKind.WorldData,
            ".log" => FolderEntryKind.Log,
            _ => TextFileService.LooksEditable(relativePath) ? FolderEntryKind.Text : FolderEntryKind.Other
        };
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, params string[] names)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        foreach (var name in names)
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):F1} GB"
        : bytes >= 1024L * 1024 ? $"{bytes / (1024.0 * 1024):F1} MB"
        : bytes >= 1024 ? $"{bytes / 1024.0:F0} KB"
        : $"{bytes} B";
}

/// <summary>Column a <see cref="FolderView"/> is sorted by.</summary>
public enum FolderSortField { Name, Date, Size }

public sealed class BreadcrumbSegment
{
    public string Label { get; init; } = "";
    public string Path  { get; init; } = "";
}

/// <summary>Drag payload describing which files came from which folder root.</summary>
[Serializable]
public sealed class FileMovePayload
{
    public string SourceRoot { get; set; } = "";
    public List<string> RelativePaths { get; set; } = new();
}
