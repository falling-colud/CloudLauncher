using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>
/// Folder-navigable file browser used for game/, local/, shared/ columns.
/// Supports drag-and-drop between sibling FolderViews.
/// </summary>
public partial class FolderView : UserControl
{
    // ── public surface ───────────────────────────────────────────────────────

    /// <summary>Absolute path to the root folder (game/ or local/).</summary>
    public string? Root { get; set; }

    /// <summary>Rules used to badge files. Required when <see cref="ShowOnlyShared"/> is true.</summary>
    public List<PackRule>? Rules { get; set; }

    /// <summary>When set on the game/ view, files mirrored to local/ are hidden but remain on disk.</summary>
    public string? MirrorLocalRoot { get; set; }
    public string? MirrorSharedRoot { get; set; }

    /// <summary>
    /// When true on the game/ view, files matching a "shared" rule are hidden (they only belong in
    /// the Shared column) but remain on disk in game/ — the live instance still sees them at launch.
    /// Requires <see cref="Rules"/> to be set so shared files can be identified.
    /// </summary>
    public bool HideShared { get; set; }

    /// <summary>
    /// When true this view acts as a "sync preview": it shares the same Root as the game view
    /// but only shows files whose rule action is Shared. Used to display what will be uploaded.
    /// </summary>
    public bool ShowOnlyShared { get; set; }

    /// <summary>Raised when the selection changes.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Fired when files have been dropped onto this FolderView from another one.
    /// Subscriber should perform the move (the source root tells you where files came from).</summary>
    public event Action<string /*sourceRoot*/, string /*destRoot*/, IReadOnlyList<string> /*relPaths*/>? FilesDropped;

    public bool IsNotAtRoot => _current.Length > 0;

    /// <summary>Returns selected files as paths relative to Root. Folders are skipped.</summary>
    public IReadOnlyList<string> GetSelectedFiles() =>
        FileList.SelectedItems
            .Cast<FolderEntry>()
            .Where(e => !e.IsFolder)
            .Select(e => e.RelativePath)
            .ToList();

    /// <summary>Returns selected entries (files AND folders).</summary>
    public IReadOnlyList<FolderEntry> GetSelectedEntries() =>
        FileList.SelectedItems.Cast<FolderEntry>().ToList();

    /// <summary>Returns the first selected item regardless of whether it is a file or folder.</summary>
    public FolderEntry? SelectedEntry => FileList.SelectedItem as FolderEntry;

    // ── internals ────────────────────────────────────────────────────────────

    private string _current = ""; // path relative to Root, always uses '/'

    // Sorting — applied to the current listing; folders are always grouped before files.
    private List<FolderEntry> _entries = new();
    private FolderSortField _sortField = FolderSortField.Name;
    private bool _sortDescending;

    // Cached whole-tree rule/mirror classification, reused across in-view navigation so clicking
    // into a folder doesn't re-scan the entire pack. Rebuilt by RefreshAsync when content changes.
    private TreeContext? _treeContext;

    // Drag-start tracking
    private Point _dragStart;
    private bool _isMouseDownInList;

    public FolderView() { InitializeComponent(); }

    public void Refresh() => _ = RefreshAsync();

    /// <summary>Full refresh: re-scans the whole tree for rule/badge classification, then lists the
    /// current folder. Use after files, rules, root, or flags change.</summary>
    public Task RefreshAsync() => RefreshInternalAsync(rebuildContext: true);

    /// <summary>Navigation refresh: reuses the cached whole-tree classification so clicking into a
    /// folder doesn't re-scan the entire pack. Safe for in-view navigation, where root/rules are
    /// unchanged. Builds the context on first use if there isn't one yet.</summary>
    private Task NavigateAsync() => RefreshInternalAsync(rebuildContext: false);

    private async Task RefreshInternalAsync(bool rebuildContext)
    {
        if (Root is null) { _entries = new(); _treeContext = null; FileList.ItemsSource = null; return; }

        var root = Root;
        var current = _current;
        var rules = Rules;
        var mirrorLocal = MirrorLocalRoot;
        var mirrorShared = MirrorSharedRoot;
        var showOnlyShared = ShowOnlyShared;
        var hideShared = HideShared;
        var selectedPaths = FileList.SelectedItems
            .Cast<FolderEntry>()
            .Select(e => e.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The tree classification only depends on (root, rules, mirrors, flags) — not on which
        // folder is shown — so navigation reuses it instead of re-scanning the whole pack.
        var reuse = rebuildContext ? null : _treeContext;

        var (context, snapshot) = await Task.Run(() =>
        {
            var ctx = reuse ?? BuildTreeContext(root, rules, mirrorLocal, mirrorShared, showOnlyShared, hideShared);
            var snap = BuildListing(root, current, rules, ctx, showOnlyShared, hideShared);
            return (ctx, snap);
        });

        if (!ReferenceEquals(Root, root) || _current != current) return;

        _treeContext = context;
        ApplySnapshot(snapshot, selectedPaths);
    }

    public void NavigateTo(string relPath) { _current = relPath.Replace('\\', '/').Trim('/'); Refresh(); }
    public void NavigateToRoot()           { _current = ""; Refresh(); }

    // ── events ────────────────────────────────────────────────────────────────

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileList.SelectedItem is FolderEntry { IsFolder: true } entry)
        {
            _current = entry.RelativePath;
            _ = NavigateAsync();
        }
    }

    private void OnUp(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_current)) return;
        var idx = _current.LastIndexOf('/');
        _current = idx < 0 ? "" : _current[..idx];
        _ = NavigateAsync();
    }

    private void OnBreadcrumbClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path })
        {
            _current = path;
            _ = NavigateAsync();
        }
    }

    private void ApplySnapshot(FolderListingSnapshot snapshot, HashSet<string>? reselectPaths)
    {
        if (!Directory.Exists(snapshot.AbsDir))
        {
            _entries = new();
            FileList.ItemsSource = null;
            BuildBreadcrumb();
            UpButton.Visibility = IsNotAtRoot ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        _entries = snapshot.Entries;
        BuildBreadcrumb();
        UpButton.Visibility = IsNotAtRoot ? Visibility.Visible : Visibility.Collapsed;
        BindEntries(reselectPaths);
    }

    /// <summary>Sorts the current listing per the active field/direction and rebinds, restoring selection.</summary>
    private void BindEntries(HashSet<string>? reselectPaths)
    {
        var ordered = SortEntries(_entries).ToList();
        FileList.ItemsSource = ordered;
        UpdateSortIndicators();

        if (reselectPaths is null || reselectPaths.Count == 0) return;

        FileList.SelectedItems.Clear();
        foreach (var entry in ordered)
        {
            if (reselectPaths.Contains(entry.RelativePath))
                FileList.SelectedItems.Add(entry);
        }
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
            _ => _sortDescending
                ? grouped.ThenByDescending(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                : grouped.ThenBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        };
    }

    private void OnSortByName(object sender, RoutedEventArgs e) => ToggleSort(FolderSortField.Name);
    private void OnSortByDate(object sender, RoutedEventArgs e) => ToggleSort(FolderSortField.Date);

    private void ToggleSort(FolderSortField field)
    {
        if (_sortField == field)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortField = field;
            // Newest-first feels natural when switching to Date; A→Z when switching to Name.
            _sortDescending = field == FolderSortField.Date;
        }

        var selected = FileList.SelectedItems
            .Cast<FolderEntry>()
            .Select(en => en.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        BindEntries(selected);
    }

    private void UpdateSortIndicators()
    {
        var arrow = _sortDescending ? "▼" : "▲"; // ▼ / ▲
        NameSortArrow.Text = _sortField == FolderSortField.Name ? arrow : "";
        DateSortArrow.Text = _sortField == FolderSortField.Date ? arrow : "";
    }

    /// <summary>
    /// Computes the whole-tree classification sets (mirrored paths, fully-mirrored / fully-shared /
    /// contains-shared folders). This is the expensive part — it recurses the entire tree and runs
    /// the rule matcher per file — but it depends only on (root, rules, mirrors, flags), so the
    /// result is cached and reused across navigation.
    /// </summary>
    private static TreeContext BuildTreeContext(
        string root,
        List<PackRule>? rules,
        string? mirrorLocalRoot,
        string? mirrorSharedRoot,
        bool showOnlyShared,
        bool hideShared)
    {
        var mirroredPaths = BuildMirroredPathSet(mirrorLocalRoot, mirrorSharedRoot);
        var fullyMirroredFolders = mirroredPaths is not null
            ? BuildFullyMirroredFolderSet(root, rules, mirroredPaths)
            : null;

        // Pre-compute which sub-folders contain at least one "shared" file so we can
        // show/hide folder entries when showOnlyShared is active.
        HashSet<string>? foldersWithShared = null;
        if (showOnlyShared && rules is not null)
            foldersWithShared = BuildFoldersWithSharedFiles(root, rules);

        // Pre-compute folders whose visible files are ALL shared — when hideShared is active
        // these are hidden (every file inside them lives only in the Shared column).
        HashSet<string>? fullySharedFolders = null;
        if (hideShared)
            fullySharedFolders = BuildFullySharedFolderSet(root, rules);

        return new TreeContext(mirroredPaths, fullyMirroredFolders, foldersWithShared, fullySharedFolders);
    }

    /// <summary>
    /// Lists a single directory level (<paramref name="current"/>) using the pre-computed
    /// <paramref name="ctx"/>. Cheap — touches only the one folder, not the whole tree.
    /// </summary>
    private static FolderListingSnapshot BuildListing(
        string root,
        string current,
        List<PackRule>? rules,
        TreeContext ctx,
        bool showOnlyShared,
        bool hideShared)
    {
        var absDir = string.IsNullOrEmpty(current)
            ? root
            : Path.Combine(root, current.Replace('/', Path.DirectorySeparatorChar));

        if (!Directory.Exists(absDir))
            return new FolderListingSnapshot(absDir, []);

        var mirroredPaths = ctx.MirroredPaths;
        var fullyMirroredFolders = ctx.FullyMirroredFolders;
        var foldersWithShared = ctx.FoldersWithShared;
        var fullySharedFolders = ctx.FullySharedFolders;

        var entries = new List<FolderEntry>();
        var dirInfo = new DirectoryInfo(absDir);

        foreach (var sub in dirInfo.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            var name = sub.Name;
            var rel  = string.IsNullOrEmpty(current) ? name : $"{current}/{name}";
            if (fullyMirroredFolders is not null && fullyMirroredFolders.Contains(rel)) continue;
            if (showOnlyShared && foldersWithShared is not null && !foldersWithShared.Contains(rel)) continue;
            if (fullySharedFolders is not null && fullySharedFolders.Contains(rel)) continue;
            entries.Add(new FolderEntry { DisplayName = name + "/", RelativePath = rel, IsFolder = true, Modified = sub.LastWriteTime });
        }

        foreach (var fi in dirInfo.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            var name = fi.Name;
            var rel  = string.IsNullOrEmpty(current) ? name : $"{current}/{name}";
            var match = rules is not null
                ? App.State.Rules.Match(rel, rules)
                : new RuleMatchResult();

            if (match.IsIgnored) continue;
            if (showOnlyShared && !match.IsAutoShared) continue;
            if (hideShared && match.IsAutoShared) continue;
            if (mirroredPaths is not null && mirroredPaths.Contains(rel)) continue;

            entries.Add(new FolderEntry
            {
                DisplayName  = name,
                RelativePath = rel,
                IsFolder     = false,
                Badge        = match.Badge,
                IsAutoLocal  = match.IsAutoLocal,
                IsAutoShared = match.IsAutoShared,
                Modified     = fi.LastWriteTime,
                TextColorKey = match.IsAutoLocal  ? "InfoBrush"
                             : match.IsAutoShared ? "SuccessBrush"
                             : "TextPrimaryBrush"
            });
        }

        return new FolderListingSnapshot(absDir, entries);
    }

    /// <summary>
    /// Returns the set of relative folder paths (at any depth under root) that contain
    /// at least one file matching a "shared" rule. Used to show folder entries in the
    /// sync-preview pane even when only shared files are displayed.
    /// </summary>
    private static HashSet<string> BuildFoldersWithSharedFiles(string root, List<PackRule> rules)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return result;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            var match = App.State.Rules.Match(rel, rules);
            if (!match.IsAutoShared || !rel.Contains('/')) continue;
            var parts = rel.Split('/');
            for (var i = 0; i < parts.Length - 1; i++)
                result.Add(string.Join("/", parts.Take(i + 1)));
        }
        return result;
    }

    /// <summary>
    /// One pass over game/ — folders whose visible (non-ignored) files are ALL matched by a
    /// "shared" rule. When HideShared is active these folders are hidden from the game view
    /// because everything inside them is surfaced in the Shared column instead.
    /// </summary>
    private static HashSet<string> BuildFullySharedFolderSet(string root, List<PackRule>? rules)
    {
        var folderVisible = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var folderNonShared = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(root)) return [];

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            var match = rules is not null
                ? App.State.Rules.Match(rel, rules)
                : new RuleMatchResult();
            if (match.IsIgnored || !rel.Contains('/')) continue;

            var parts = rel.Split('/');
            for (var i = 0; i < parts.Length - 1; i++)
            {
                var folderRel = string.Join("/", parts.Take(i + 1));
                folderVisible[folderRel] = folderVisible.GetValueOrDefault(folderRel) + 1;
                if (!match.IsAutoShared)
                    folderNonShared[folderRel] = folderNonShared.GetValueOrDefault(folderRel) + 1;
            }
        }

        var fullyShared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folderRel, visibleCount) in folderVisible)
        {
            if (visibleCount > 0 && !folderNonShared.ContainsKey(folderRel))
                fullyShared.Add(folderRel);
        }

        return fullyShared;
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
        foreach (var file in Directory.EnumerateFiles(mirrorRoot, "*", SearchOption.AllDirectories))
            set.Add(Path.GetRelativePath(mirrorRoot, file).Replace('\\', '/'));
    }

    /// <summary>
    /// One pass over game/ — folders whose visible files are all mirrored to local/ or shared/.
    /// </summary>
    private static HashSet<string> BuildFullyMirroredFolderSet(
        string root,
        List<PackRule>? rules,
        HashSet<string> mirroredPaths)
    {
        var folderVisible = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var folderUnmirrored = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(root)) return [];

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            var match = rules is not null
                ? App.State.Rules.Match(rel, rules)
                : new RuleMatchResult();
            if (match.IsIgnored || !rel.Contains('/')) continue;

            var parts = rel.Split('/');
            for (var i = 0; i < parts.Length - 1; i++)
            {
                var folderRel = string.Join("/", parts.Take(i + 1));
                folderVisible[folderRel] = folderVisible.GetValueOrDefault(folderRel) + 1;
                if (!mirroredPaths.Contains(rel))
                    folderUnmirrored[folderRel] = folderUnmirrored.GetValueOrDefault(folderRel) + 1;
            }
        }

        var fullyMirrored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folderRel, visibleCount) in folderVisible)
        {
            if (visibleCount > 0 && !folderUnmirrored.ContainsKey(folderRel))
                fullyMirrored.Add(folderRel);
        }

        return fullyMirrored;
    }

    // ── drag and drop ────────────────────────────────────────────────────────

    private void OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // WPF doesn't select an item on right-click, so context-menu actions (set rule,
        // move to shared, reveal) would otherwise run against a stale or empty selection.
        // Select the item under the cursor first — unless it's already part of a
        // multi-selection, in which case keep the whole selection so the action applies to all.
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
        // Ctrl/Shift clicks are for multi-select — don't track them for drag.
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
        DragDrop.DoDragDrop(FileList, data, DragDropEffects.Move);
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (Root is null) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        if (e.Data.GetDataPresent(typeof(FileMovePayload).FullName!))
        {
            var payload = e.Data.GetData(typeof(FileMovePayload).FullName!) as FileMovePayload;
            // ShowOnlyShared views share the same Root as the game view; allow drops into them
            // (the drop handler treats it as "mark as shared" rather than a file move).
            var isSelf = payload?.SourceRoot == Root && !ShowOnlyShared;
            e.Effects = isSelf ? DragDropEffects.None : DragDropEffects.Move;
        }
        else e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e) { /* no visual yet */ }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (Root is null) return;
        if (e.Data.GetData(typeof(FileMovePayload).FullName!) is not FileMovePayload payload) return;
        // Block drops onto the exact same view instance (not same-root ShowOnlyShared views).
        if (payload.SourceRoot == Root && !ShowOnlyShared) return;

        FilesDropped?.Invoke(payload.SourceRoot, Root!, payload.RelativePaths);
        e.Handled = true;
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => SelectionChanged?.Invoke(this, EventArgs.Empty);

    private sealed class FolderListingSnapshot(string absDir, List<FolderEntry> entries)
    {
        public string AbsDir { get; } = absDir;
        public List<FolderEntry> Entries { get; } = entries;
    }

    /// <summary>Whole-tree classification, computed once per content/rule change and reused across
    /// navigation. Null members mean that classification isn't active for the current view mode.</summary>
    private sealed class TreeContext(
        HashSet<string>? mirroredPaths,
        HashSet<string>? fullyMirroredFolders,
        HashSet<string>? foldersWithShared,
        HashSet<string>? fullySharedFolders)
    {
        public HashSet<string>? MirroredPaths { get; } = mirroredPaths;
        public HashSet<string>? FullyMirroredFolders { get; } = fullyMirroredFolders;
        public HashSet<string>? FoldersWithShared { get; } = foldersWithShared;
        public HashSet<string>? FullySharedFolders { get; } = fullySharedFolders;
    }
}

public sealed class FolderEntry
{
    public string DisplayName  { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public bool   IsFolder     { get; init; }
    public string Badge        { get; init; } = "";
    public bool   IsAutoLocal  { get; init; }
    public bool   IsAutoShared { get; init; }
    public string TextColorKey { get; init; } = "TextPrimaryBrush";
    /// <summary>Last-write time of the file/folder on disk — when it appeared or last changed.</summary>
    public DateTime Modified   { get; init; }
    public Brush TextColor =>
        (Brush)Application.Current.FindResource(TextColorKey);
    public string Icon         => IsFolder ? "📁" : "📄";
    public string DateLabel    => Modified == default ? "" : Modified.ToString("g");
    public FontWeight FontWeight => IsFolder ? FontWeights.SemiBold : FontWeights.Normal;
}

/// <summary>Column a <see cref="FolderView"/> is sorted by.</summary>
public enum FolderSortField { Name, Date }

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
