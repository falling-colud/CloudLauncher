using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// The Mods page: a launcher-wide mod manager first, a store browser second.
/// </summary>
/// <remarks>
/// <para>Mirrors the launcher-wide tabs of <see cref="ModManagementView"/> (List view, Mod browsing,
/// Graph view, Categories) using <see cref="ModListPane"/> over <see cref="GlobalModSet"/>, with
/// <see cref="ModMetadataService.GlobalScope"/> in place of a pack id. There is no global Update all:
/// one row can span instances with different versions and loaders, and <c>ModUpdater</c> writes the
/// new jar beside the old one, which would leave a linked library mod stale.</para>
/// <para>The row 3 strip is the only scope control and is shown on every tab
/// (<see cref="ApplyStripForTab"/>). Each loading surface has its own <see cref="PageState"/> on the
/// shared status slots, and <see cref="ShowTabAsync"/> cancels the one being left. For
/// <see cref="IReusablePage"/>, the cancellation sources are replaced at the start of every pass.</para>
/// </remarks>
public partial class ModsView : Page, IReusablePage, IRefreshablePage
{
    private readonly MainWindow _shell;

    // ── the management surface ───────────────────────────────────────────────
    private readonly GlobalModSet _modSet;
    private readonly ModFolderStore _folders = new();
    private readonly ObservableCollection<ModFolderChipRow> _folderChips = new();
    private readonly ObservableCollection<ScopeRow> _scopes = new();

    /// <summary>The management list's loading, empty, error and offline states.</summary>
    private readonly PageState _state;

    /// <summary>Marshals the scan's progress line onto the UI thread. Must be constructed on the UI
    /// thread, since a <see cref="Progress{T}"/> captures the synchronisation context it is created
    /// on.</summary>
    private readonly Progress<string> _scanProgress;

    private CancellationTokenSource? _scanCts;
    private GlobalModSnapshot? _snapshot;
    private IReadOnlyList<PackSummary> _instances = [];
    private string? _note;
    private string _manageSort = ManageSort.Name;
    private bool _reverseSort;
    private Guid? _scopeId;

    /// <summary>The row whose rules the popup is editing, and the policy it is editing.</summary>
    private GlobalModRow? _rulesRow;
    private ContentDefaultPolicy? _rulesDraft;

    private readonly Reentrancy _filling = new();

    // ── the store browser ────────────────────────────────────────────────────
    private readonly ObservableCollection<ModBrowseRow> _rows = new();
    private readonly ObservableCollection<ModSourceChipRow> _chips = new();
    private readonly List<ModVersion> _allExternalVersions = new();

    private ModSourceChipRow? _activeChip;
    private CancellationTokenSource _cts = new();

    /// <summary>Owns the paging and the load in flight, so a reset waits for the task it actually
    /// cancelled.</summary>
    private readonly InfiniteScroll.Pager _pager;

    /// <summary>The browser's own page state, over the results list.</summary>
    private readonly PageState _browseState;

    private ModSummary? _currentExternalMod;
    private HostedModDetail? _currentHostedMod;
    private string? _currentProjectUrl;

    private string _searchText = "";

    /// <summary>What the Filters popup's two "(browse)" fields were set to, or null for "not set".</summary>
    /// <remarks>Read through <see cref="EffectiveMcVersion"/> and <see cref="EffectiveLoader"/>: an
    /// empty field means the scoped instance supplies the value.</remarks>
    private string? _filterMcVersion;
    private string? _filterLoader;

    /// <summary>How the CloudLauncher sources are ordered. One of <see cref="ModBrowseSort"/>.</summary>
    /// <remarks>
    /// Not persisted. Only applies to the CloudLauncher chips: CurseForge and Modrinth return their own
    /// relevance order, and re-sorting one page of it would misrepresent the rest.
    /// </remarks>
    private string _sort = ModBrowseSort.Updated;
    private int _offset;
    private bool _isLoading;
    private bool _hasMore;

    /// <summary>How many the active hosted source says it has in total, so the count slot can read
    /// "40 of 512" instead of "40". Null for the stores, which do not report one.</summary>
    private int? _sourceTotal;

    private const int PageSize = 50;   // both stores cap a page at 50

    /// <summary>Safety stop so a runaway pager can't fill memory; not a browsing limit.</summary>
    private const int MaxResults = 2000;

    /// <summary>Which surface is on screen.</summary>
    /// <remarks>Coarser than the tab index: the Categories and Graph tabs use the list's mod set and
    /// load nothing of their own.</remarks>
    private enum Surface { Manage, Browse }

    private Surface _surface = Surface.Manage;

    /// <summary>Tab indices, named so adding a tab can't shift what the lazy-load and keyboard
    /// shortcuts refer to.</summary>
    private const int TabList = 0, TabBrowse = 1, TabGraph = 2, TabCategories = 3;

    // ── the tabs built from the row set, lazily ──────────────────────────────
    private ModCategoriesView? _categories;
    private ModGraphView? _graph;

    /// <summary>These two are a whole canvas each, so they are rebuilt when the data they were built
    /// from actually changed rather than on every tab switch.</summary>
    private bool _categoriesDirty = true, _graphDirty = true;

    /// <summary>What the canvas on screen was last drawn for: the page scope, and whether anything
    /// has been drawn at all.</summary>
    /// <remarks>A scope change refits the view; other rebuilds keep the user's zoom and pan. One
    /// instance's graph and every instance's differ in size by an order of magnitude.</remarks>
    private Guid? _graphedScopeId;
    private bool _graphedOnce;

    /// <summary>How many nodes this page will put on one canvas.</summary>
    /// <remarks>With "All instances" the graph draws the whole filtered union. Past this cap it is
    /// truncated and says how many mods are left out. At 3000 nodes the canvas opens in about 1.3s; at
    /// 5000 it takes about 4s.</remarks>
    private const int GraphNodeCap = 3000;

    /// <summary>False until the constructor is done. <c>TabControl</c> raises
    /// <c>SelectionChanged</c> while its items are being built, and the handler would load a surface
    /// against half-built fields.</summary>
    private bool _ready;

    /// <summary>True while <see cref="ShowSurfaceAsync"/> is moving the tab itself, so
    /// <see cref="OnTabChanged"/> does not start the same load a second time.</summary>
    private bool _switchingSurface;

    /// <summary>The management list's sort keys. Strings rather than an enum because they are the
    /// menu items' <c>Tag</c> values, which is the one place they have to be spelled.</summary>
    private static class ManageSort
    {
        public const string Name = "name";
        public const string Priority = "priority";
        public const string Instances = "instances";
        public const string Added = "added";
        public const string Size = "size";
    }

    public ModsView(MainWindow shell)
    {
        InitializeComponent();
        _shell = shell;
        _modSet = new GlobalModSet(App.State.ModInventory, App.State.ModMetadata, App.State.TestScope);

        ResultsList.ItemsSource = _rows;
        SourceStrip.ItemsSource = _chips;
        FolderStrip.ItemsSource = _folderChips;
        PackScopeBox.ItemsSource = _scopes;
        PackScopeBox.DisplayMemberPath = nameof(ScopeRow.Label);
        ModTabs.SelectionChanged += OnModTabsChanged;

        // Shared box: the management list could use a shorter debounce, but the store search needs
        // this one.
        SearchBox.DebounceMilliseconds = 250;
        SearchBox.TextChangedDebounced += OnSearchSettled;
        SearchBox.SearchSubmitted += OnSearchSubmitted;

        _pager = new InfiniteScroll.Pager(
            () => !_isLoading && _hasMore && _activeChip is not null && _rows.Count < MaxResults,
            () => _rows.Count,
            LoadMoreAsync);

        // No DisableWhileBusy: every control in this toolbar means "ask a different question",
        // which cancels and restarts the load anyway.
        _state = new PageState(ListPane, PageStateHost, nameof(ModsView) + ".mods")
            .Copy(PageCopy.Mods)
            .Slots(CountLabel, StatusLabel, BusyBar);
        _state.RetryRequested += () => _ = LoadManageAsync(force: true);
        _state.CancelRequested += () =>
        {
            _scanCts?.Cancel();
            _state.Cancelled("Scan stopped.");
        };

        _browseState = new PageState(ResultsList, BrowseStateHost, nameof(ModsView) + ".browse")
            .Copy(PageCopy.Results)
            .Slots(CountLabel, StatusLabel, BusyBar);
        _browseState.RetryRequested += () => _ = ResetAndLoadAsync();

        // Created here so it is on the UI thread and after _state is assigned.
        _scanProgress = new Progress<string>(line => _state.Progress(line));

        ListPane.Bind(BuildPaneHost());
        RestoreFolderSelection();

        InstancePickList.ItemsSource = _picks;

        Loaded += async (_, _) =>
        {
            // Replace a cancelled source first, or every background call on a reopened page fails at once.
            if (_cts.IsCancellationRequested) _cts = new CancellationTokenSource();

            if (Window.GetWindow(this) is Window w)
            {
                w.PreviewKeyDown -= OnShellKeyDown;
                w.PreviewKeyDown += OnShellKeyDown;
            }
            ThemeService.Changed -= OnThemeChanged;
            ThemeService.Changed += OnThemeChanged;

            // Reopening refreshes the scan (which also feeds the Categories and Graph tabs) over the cached
            // rows. The store browser is left as it was, keeping the user's search and scroll position.
            if (Tabs.SelectedIndex != TabBrowse) await LoadManageAsync();
        };
        Unloaded += (_, _) =>
        {
            if (Window.GetWindow(this) is Window w)
                w.PreviewKeyDown -= OnShellKeyDown;
            ThemeService.Changed -= OnThemeChanged;
            _cts.Cancel();
            _scanCts?.Cancel();
        };

        // Set the strip up for the current tab before the first paint; a reused page can open on any
        // tab without a tab change.
        ApplyStripForTab(Tabs.SelectedIndex);

        // Last, and after everything a tab change touches: see _ready.
        _ready = true;
    }

    /// <summary>The offline banner's Retry, F5, and anything else that wants this page re-read.</summary>
    /// <remarks>Re-reads the disk on every tab except the browser; the Categories board and the graph
    /// are drawn from the same scan (<see cref="LoadManageAsync"/>).</remarks>
    public Task RefreshAsync() =>
        _surface == Surface.Manage ? LoadManageAsync(force: true) : ResetAndLoadAsync();

    /// <summary>Repaints chip and badge colours, which are assigned brushes: ThemeService creates new
    /// brush objects on every Apply, so an open page would keep the old palette.</summary>
    private void OnThemeChanged()
    {
        ApplyChipStyles();
        RepaintHostedBadges();
    }

    private void RepaintHostedBadges()
    {
        // The rows notify, so only realized containers repaint. Items.Refresh() would rebuild every
        // container just to recolour a pill.
        var hostedBackground = (Brush)FindResource("AccentSoftBrush");
        var hostedForeground = (Brush)FindResource("AccentBrush");
        foreach (var row in _rows)
        {
            if (row.Hosted is not null)
            {
                row.SourceBadgeBackground = hostedBackground;
                row.SourceBadgeForeground = hostedForeground;
            }
            // Store rows keep the store's own brand colour, which no theme changes.
        }
    }

    // ── surfaces ─────────────────────────────────────────────────────────────

    /// <summary>Selects the tab that carries one surface, and loads it.</summary>
    /// <remarks>Takes a <see cref="Surface"/> so the browser's offline fallback needn't know the tab
    /// index. It awaits the load itself (and tells <see cref="OnTabChanged"/> to skip it) so the
    /// fallback's status line, written afterwards, isn't overwritten by the load.</remarks>
    private async Task ShowSurfaceAsync(Surface surface)
    {
        var tab = surface == Surface.Browse ? TabBrowse : TabList;
        if (Tabs.SelectedIndex != tab)
        {
            // Assigning SelectedIndex raises SelectionChanged synchronously, so the flag is only ever
            // set while that one handler runs.
            _switchingSurface = true;
            try { Tabs.SelectedIndex = tab; }
            finally { _switchingSurface = false; }
        }
        await ShowTabAsync(tab);
    }

    /// <summary>
    /// A tab was selected.
    /// </summary>
    /// <remarks>The detail pane's own TabControl bubbles its selection up here, hence the reference
    /// check; and this fires once before the constructor finishes, hence <see cref="_ready"/>.</remarks>
    private async void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, e.OriginalSource) || e.OriginalSource is not TabControl) return;
        if (!_ready || _switchingSurface) return;
        try { await ShowTabAsync(Tabs.SelectedIndex); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.LogError(nameof(ModsView), ex); }
    }

    /// <summary>
    /// Puts one tab on screen: stops the tab being left, re-points the shared controls, and loads.
    /// </summary>
    private async Task ShowTabAsync(int tab)
    {
        _surface = tab == TabBrowse ? Surface.Browse : Surface.Manage;
        var browsing = _surface == Surface.Browse;

        // Stop the work behind the tab being left: both surfaces write to the one status bar, and a
        // hidden tab's scan would overwrite the visible one's status.
        if (browsing) _scanCts?.Cancel();
        else _cts.Cancel();

        // The search box stays enabled on all four tabs: the query is part of the shared predicate, so
        // it also decides what is on the graph. The graph's own find box only dims non-matching nodes.
        SearchBox.Placeholder = browsing ? "Search the stores" : "Search mods";
        ApplyStripForTab(tab);

        switch (tab)
        {
            case TabBrowse:
                if (_chips.Count == 0) await BuildChipsAsync();
                if (_activeChip is null && _chips.Count > 0)
                    await SelectChipAsync(
                        _chips.FirstOrDefault(c => c.Kind == ModBrowseSourceKind.CurseForge)
                        ?? _chips.First(c => !c.IsDivider));
                else
                    await ResetAndLoadAsync();
                return;

            case TabCategories:
                // Both tabs are drawn from the scan, so the first thing either needs is a scan. The
                // load rebuilds whichever of them is on screen, so the call below is usually a no-op.
                if (_snapshot is null) await LoadManageAsync();
                LoadCategories();
                return;

            case TabGraph:
                if (_snapshot is null) await LoadManageAsync();
                LoadGraph();
                return;

            default:
                await LoadManageAsync();
                return;
        }
    }

    /// <summary>
    /// Points the filter strip at the tab now on screen: controls that apply stay live, the rest are
    /// disabled and faded in place with the reason in their tooltip.
    /// </summary>
    /// <remarks>
    /// Controls are never hidden, so buttons stay in the same spots across tabs. Scope and Filters
    /// apply everywhere; sort and group-by-category only to a list; defaults-only and the folder chips
    /// not to store results. Sort while browsing only works on the CloudLauncher sources, so this is
    /// called again from <see cref="SelectChipAsync"/>.
    /// </remarks>
    private void ApplyStripForTab(int tab)
    {
        var hostedChip = _activeChip?.Kind is ModBrowseSourceKind.CloudLauncherPublic
            or ModBrowseSourceKind.CloudLauncherPersonal
            or ModBrowseSourceKind.CloudLauncherShared
            or ModBrowseSourceKind.CloudLauncherTeam;

        var sortLive = tab switch
        {
            TabList => true,
            TabBrowse => hostedChip,
            _ => false
        };
        Live(SortButton, sortLive, tab switch
        {
            TabList => "Sort mods",
            TabBrowse => hostedChip
                ? $"Sort results: {BrowseSortLabel(_sort)}"
                : "CurseForge and Modrinth answer in their own relevance order - there is nothing to re-sort.",
            TabGraph => "A canvas has no row order. Lay it out with 'Cluster by' on the graph's own toolbar.",
            _ => "The board orders each category's mods by priority and size, which is what its headings are for."
        }, styleFades: false);
        if (sortLive) UpdateSortMenuState();

        // Live everywhere: the browse tab uses the popup's two "(browse)" fields, the other tabs the rest.
        Live(FiltersButton, true, "Filters");

        Live(PackScopeBox, true, tab == TabBrowse
            ? "Which instance to browse and install for - the store query follows its Minecraft version and loader"
            : "Narrow to one instance");

        Live(DefaultsOnlyButton, tab != TabBrowse,
            tab == TabBrowse
                ? "'Your defaults' is a fact about the mods on this PC. Store results are not yours yet."
                : "Show only your defaults");

        Live(GroupByCategoryButton, tab == TabList, tab switch
        {
            TabList => "Group the list by category",
            TabBrowse => "The stores group their own results.",
            TabGraph => "The graph groups by category itself - pick 'Categories' in its Cluster by box.",
            _ => "The board is already one column per category."
        });

        // The chips are Borders with click handlers, so the disabled state and fade go on their
        // ScrollViewer; a disabled element gets no mouse events.
        Live(FolderScroller, tab != TabBrowse,
            tab == TabBrowse
                ? "Folders file the mods already on this PC. The store chips below pick what you are browsing."
                : "Filter by folder", styleFades: false);

        UpdateBrowseScopeNote();
    }

    /// <summary>Sets one strip control's enabled state and tooltip, and fades it when its style
    /// doesn't.</summary>
    /// <param name="styleFades">True when the control's style already fades it while disabled (e.g.
    /// <c>ToolbarToggleButton</c>). Fading here as well would multiply the two.</param>
    private static void Live(FrameworkElement control, bool live, string tip, bool styleFades = true)
    {
        control.IsEnabled = live;
        control.ToolTip = tip;
        if (!styleFades) control.Opacity = live ? 1.0 : 0.4;
    }

    // ── the Categories and Graph tabs ────────────────────────────────────────

    /// <summary>
    /// The hub's Categories workbench, over the global set.
    /// </summary>
    /// <remarks>
    /// <para>Passing <see cref="ModMetadataService.GlobalScope"/> as the scope id is all it takes, since
    /// the control reads and writes through <see cref="ModMetadataService"/> by scope. Update and delete
    /// act on one file and a global row stands for many, so they are left out on this scope (partly
    /// here, partly inside <see cref="ModCategoriesView"/>).</para>
    /// <para>It gets <see cref="ModListPane.Filtered"/>, the list's own predicate, so the strip and the
    /// search narrow the board and its counts too (see <see cref="BoardScopeNote"/>). The rows' <c>Meta</c>
    /// is the global entry the list draws, so a category set here shows in the list at once.</para>
    /// </remarks>
    private void LoadCategories()
    {
        if (_categories is null)
        {
            _categories = new ModCategoriesView();
            CategoriesHost.Children.Clear();
            CategoriesHost.Children.Add(_categories);
            _categoriesDirty = true;
        }
        if (!_categoriesDirty) return;
        _categoriesDirty = false;
        // Set before Load, which reads them: the page's search box drives the board's search, the note
        // says what its counts cover, and the unfiltered set is what the document-wide operations (delete
        // a category, import from an instance) act on.
        _categories.HostOwnsSearch = true;
        _categories.ScopeNote = BoardScopeNote();
        _categories.UnfilteredMods = ListPane.All.Select(r => r.Mod).ToList();
        _categories.Load(ModMetadataService.GlobalScope,
                         ListPane.Filtered.Select(r => r.Mod).ToList(),
                         _shell,
                         OnSecondaryEdited,
                         onOpenMod: OpenStorePageFor,
                         onReload: () => _ = LoadManageAsync(force: true));
    }

    /// <summary>What the board is counting, when it is not counting everything.</summary>
    /// <remarks>With a filter active the board's counts are about the filtered set, so the strip's
    /// state is spelled out beside them. The graph's notice passes <paramref name="includeScope"/>
    /// false because it names the instance itself.</remarks>
    private string? BoardScopeNote(bool includeScope = true)
    {
        var parts = new List<string>();
        if (includeScope && _scopeId is { } id) parts.Add($"in {InstanceName(id)}");
        if (_folders.Active is { } folder) parts.Add($"filed under '{folder}'");
        if (DefaultsOnlyButton.IsChecked == true) parts.Add("your defaults only");
        if (_searchText.Trim() is { Length: > 0 } query) parts.Add($"matching '{query}'");
        if (FilterHideDisabled.IsChecked == true) parts.Add("enabled only");
        if (FilterShadowedOnly.IsChecked == true) parts.Add("where an instance has its own copy");
        if (FilterNotesOnly.IsChecked == true) parts.Add("with a note");
        if (FilterSourceBox.SelectedIndex > 0
            && FilterSourceBox.SelectedItem is ComboBoxItem { Content: string store })
            parts.Add($"from {store}");
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>
    /// The hub's dependency graph, over what the filter strip admits.
    /// </summary>
    /// <remarks>
    /// <para>Reads <see cref="ModListPane.Filtered"/>, like the list and the Categories board. With an
    /// instance scoped it draws that instance's own copies (<see cref="GlobalModRow.Copies"/>), so flags,
    /// edges and layout belong to that instance's document, shared with the hub.</para>
    /// <para>With "All instances" it draws one representative per mod, stores the layout under
    /// <see cref="ModMetadataService.GlobalScope"/>, and <see cref="GraphNotice"/> warns that an edge may
    /// join mods no single instance loads together. <see cref="ModGraphView"/> drops the per-file
    /// actions on that scope. Rows are put in list order before <see cref="GraphNodeCap"/> is
    /// applied.</para>
    /// </remarks>
    private void LoadGraph()
    {
        if (_snapshot is null)
        {
            GraphNotice.Text = "";
            ShowGraphHint("Nothing to graph",
                "There are no instances to graph yet. Make one, and its mods appear here.");
            return;
        }

        var scope = _scopeId;

        // Sort by the list's comparer before capping, so a capped graph holds the first N rows of the
        // list on screen.
        var rows = ListPane.Filtered.ToList();
        rows.Sort(Comparer());

        var mods = scope is { } id
            ? rows.SelectMany(r => r.Copies.Where(c => c.Instance.Id == id).Select(c => c.Mod)).ToList()
            : rows.Select(r => r.Mod).ToList();

        if (mods.Count == 0)
        {
            GraphNotice.Text = "";
            ShowGraphHint("Nothing to graph",
                ListPane.All.Count == 0
                    ? "Nothing in any instance's mods folder yet."
                    : "No mod matches the filters above. Clear one, or pick another instance.");
            return;
        }

        var total = mods.Count;
        var dropped = Math.Max(0, total - GraphNodeCap);
        if (dropped > 0) mods = mods.Take(GraphNodeCap).ToList();
        GraphNotice.Text = GraphNoticeText(scope, mods.Count, dropped);

        GraphHint.Visibility = Visibility.Collapsed;
        GraphHost.Visibility = Visibility.Visible;
        if (_graph is null)
        {
            _graph = new ModGraphView();
            GraphHost.Children.Clear();
            GraphHost.Children.Add(_graph);
            _graphDirty = true;
        }
        if (!_graphDirty) return;
        _graphDirty = false;

        // One instance and every instance differ in size by an order of magnitude, so a scope change
        // refits; every other rebuild keeps the zoom and pan the user set.
        var refit = !_graphedOnce || _graphedScopeId != scope;
        _graphedOnce = true;
        _graphedScopeId = scope;

        var graph = _graph;
        graph.ShowBuilding();   // paints before the build starts, so the tab is never blank
        // Deferred so the tab is on screen before the synchronous layout of several hundred nodes. The
        // try matters: nothing awaits this, so an exception would reach the dispatcher and crash the app.
        Dispatcher.InvokeAsync(() =>
        {
            try
            {
                graph.Load(scope ?? ModMetadataService.GlobalScope, mods, _shell, OpenStorePageFor,
                           () => _ = LoadManageAsync(force: true), OnSecondaryEdited, refit: refit);
            }
            catch (Exception ex)
            {
                AppLog.LogError(nameof(ModsView), ex);
                _graphDirty = true;   // so selecting the tab again tries once more
                ShowGraphHint("That graph could not be drawn", "The launcher log says why.");
            }
        }, DispatcherPriority.Background);
    }

    /// <summary>The line above the canvas: which mods are on it, and anything true about them that
    /// the canvas itself cannot show.</summary>
    /// <remarks>Names the strip's state, since the numbers on the canvas are about the filtered set,
    /// and says when <see cref="GraphNodeCap"/> left mods out.</remarks>
    private string GraphNoticeText(Guid? scope, int drawn, int dropped)
    {
        var filters = BoardScopeNote(includeScope: false);
        var where = scope is { } id ? $"in {InstanceName(id)}" : "from every instance";
        var text = $"Graphing {drawn:N0} mod{(drawn == 1 ? "" : "s")} {where}"
                   + (filters is null ? "." : $", {filters}.");

        if (scope is null)
            text += " One node per mod, so an edge here can join two mods that no single instance "
                    + "loads together - scope to an instance for the graph that instance really has.";

        if (dropped > 0)
            text += $" {dropped:N0} more {(dropped == 1 ? "is" : "are")} not on this canvas: it stops "
                    + $"at {GraphNodeCap:N0} nodes and keeps the first of them in the list's order. "
                    + "Narrow the scope or the filters to see the rest.";

        return text;
    }

    /// <summary>Says why there is no graph, instead of one.</summary>
    /// <remarks>The graph host is hidden, not just covered: the hint has no background, and an old
    /// graph behind it would look like the answer for the new scope.</remarks>
    private void ShowGraphHint(string title, string body)
    {
        GraphHost.Visibility = Visibility.Collapsed;
        GraphHint.Visibility = Visibility.Visible;
        GraphHintTitle.Text = title;
        GraphHintBody.Text = body;
    }

    private string InstanceName(Guid id) =>
        _instances.FirstOrDefault(p => p.Id == id)?.Name ?? "That instance";

    /// <summary>A mod's store page, for the tabs that hand back a representative rather than a row.</summary>
    private static void OpenStorePageFor(PackMod mod)
    {
        if (mod.PageUrl is { } url) ModOptionsMenu.OpenUrl(url);
    }

    /// <summary>
    /// An edit made inside the Categories or Graph tab.
    /// </summary>
    /// <remarks>As in the hub's <c>NoteSecondaryViewEdit</c>: the tab that raised this has already
    /// re-rendered, and rebuilding it would interrupt a drag. The other tab and the list are updated
    /// here.</remarks>
    private void OnSecondaryEdited()
    {
        App.State.ModMetadata.SyncManagedCategories(ModMetadataService.GlobalScope);
        ApplyManageFilter();
        RebuildFolderStrip();
        _categoriesDirty = Tabs.SelectedIndex != TabCategories;
        _graphDirty = Tabs.SelectedIndex != TabGraph;
    }

    /// <summary>Marks the two tabs drawn from the row set stale, and rebuilds the one on screen.</summary>
    /// <remarks>A scan returns new row objects. Hidden tabs rebuild lazily; the visible one has to
    /// rebuild now or it stays stale.</remarks>
    private void SecondaryTabsStale()
    {
        _categoriesDirty = _graphDirty = true;
        if (Tabs.SelectedIndex == TabCategories) LoadCategories();
        else if (Tabs.SelectedIndex == TabGraph) LoadGraph();
    }

    // ── the management surface: loading ──────────────────────────────────────

    /// <summary>
    /// Reads every instance's mods folder as a quiet background refresh.
    /// </summary>
    /// <param name="force">Ignore the remembered scan and re-read the disk (what Refresh does).</param>
    private async Task LoadManageAsync(bool force = false)
    {
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        if (force) _modSet.Invalidate();

        var ok = await PageRefresh.RunAsync(
            _state, ListPane.All, ListPane.List, r => r.Key,
            cached: () =>
            {
                // One read, not two: Cached is a locked property and a second call could answer
                // with a different snapshot than the one whose rows were just taken.
                var remembered = force ? null : _modSet.Cached;
                return remembered is null
                    ? CachedRows<GlobalModRow>.None
                    : new CachedRows<GlobalModRow>(remembered.Rows, remembered.ScannedUtc);
            },
            rescan: Rescan,
            ct: ct,
            filter: ApplyManageFilter,
            note: note => _note = note,
            update: (row, fresh) => row.CopyFrom(fresh),
            prepare: async token =>
            {
                _instances = await InstancesAsync(token);
                RebuildScopeBox();
            },
            busyLine: "Reading the mods folder in each instance.",
            failed: "The instances' mods folders could not be read.",
            quiet: !force);

        if (!ok) return;

        _snapshot = _modSet.Cached;
        RebuildFolderStrip();
        ApplyManageFilter();
        SecondaryTabsStale();

        // Identity resolution (hashing and store round trips) is the expensive half, so it runs after
        // the list is on screen, under the same token.
        await EnrichManageAsync(ct);
        if (ct.IsCancellationRequested) return;

        // Resolving identities changes mod keys and therefore the rows, so the other tabs need
        // updating again.
        SecondaryTabsStale();
    }

    /// <summary>The instance list. The API client falls back from server to cache to a disk scan, so
    /// this works offline; a throw means even the disk scan failed.</summary>
    private static async Task<IReadOnlyList<PackSummary>> InstancesAsync(CancellationToken ct)
    {
        // Quick: reuses the list the Instances page just fetched, when there is one, so the remembered
        // rows paint without waiting on the server.
        try { return await App.State.Api.ListPacksQuickAsync(ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(ModsView), ex);
            return [];
        }
    }

    /// <summary>The real scan, on a worker thread (<see cref="PageRefresh"/> puts it there).</summary>
    private IReadOnlyList<GlobalModRow> Rescan(CancellationToken ct)
    {
        var libraryMods = App.State.ModLibrary.Items();
        return _modSet.Build(_instances, libraryMods, _scanProgress, ct).Rows;
    }

    /// <summary>
    /// Resolves store identities and re-groups in the background, over the list already on screen.
    /// </summary>
    /// <remarks><see cref="Task.Run"/> around the whole thing because this overload of
    /// <see cref="PageRefresh.RunAsync"/> adds no thread hop of its own, and the hashing inside
    /// identity resolution happens before its first await.</remarks>
    private async Task EnrichManageAsync(CancellationToken ct)
    {
        if (_snapshot is not { Rows.Count: > 0 } snapshot) return;
        var libraryMods = App.State.ModLibrary.Items();

        var ok = await PageRefresh.RunAsync(
            _state, ListPane.All, ListPane.List, r => r.Key,
            cached: () => CachedRows<GlobalModRow>.None,
            rescan: token => Task.Run(async () =>
                (await _modSet.EnrichAsync(snapshot, libraryMods, _scanProgress, token)).Rows, token),
            ct: ct,
            filter: ApplyManageFilter,
            update: (row, fresh) => row.CopyFrom(fresh),
            busyLine: "Identifying mods...",
            failed: "Some mods could not be identified - the file names are still right.");

        if (ok) _snapshot = _modSet.Cached;
    }

    // ── the management surface: filtering, sorting, counting ─────────────────

    /// <summary>
    /// A control in the filter strip moved: re-narrow the list, and redraw the Categories board and
    /// the graph from the same predicate.
    /// </summary>
    /// <remarks>Kept apart from <see cref="ApplyManageFilter"/>, which every scan pass calls;
    /// rebuilding the board from there would throw the canvas away mid-scan and lose the selected
    /// category. Hidden tabs are only marked stale, so typing in the search box rebuilds one
    /// canvas.</remarks>
    private void ManageFilterChanged()
    {
        ApplyManageFilter();
        _categoriesDirty = _graphDirty = true;
        if (Tabs.SelectedIndex == TabCategories) LoadCategories();
        else if (Tabs.SelectedIndex == TabGraph) LoadGraph();
    }

    /// <summary>
    /// Narrows the list to what the toolbar asks for, and writes the count and the status line.
    /// </summary>
    /// <remarks>This owns <see cref="PageState.Content"/> (including the filtered-empty state), so the
    /// count it reports is the number of rows on screen.</remarks>
    private void ApplyManageFilter()
    {
        var query = _searchText.Trim();
        var scope = _scopeId;
        var folder = _folders.Active;
        var members = folder is null ? null : new HashSet<string>(_folders.Members(folder), StringComparer.OrdinalIgnoreCase);
        var defaultsOnly = DefaultsOnlyButton.IsChecked == true;
        var hideDisabled = FilterHideDisabled.IsChecked == true;
        var shadowedOnly = FilterShadowedOnly.IsChecked == true;
        var notesOnly = FilterNotesOnly.IsChecked == true;
        var store = FilterSourceBox.SelectedIndex;

        ListPane.Filter = row =>
        {
            if (defaultsOnly && !row.IsDefault) return false;
            if (shadowedOnly && !row.AnyShadowed) return false;
            if (notesOnly && !row.Mod.HasNote) return false;
            if (hideDisabled && !row.EnabledEverywhere && !row.MixedEnabled) return false;
            if (members is not null && !members.Contains(row.Key)) return false;
            if (scope is { } id && row.Copies.Count > 0 && row.Copies.All(c => c.Instance.Id != id)) return false;
            // A library-only default is in no instance, so an instance scope hides it.
            if (scope is not null && row.Copies.Count == 0) return false;
            if (store != 0 && !MatchesStore(row, store)) return false;
            return query.Length == 0 || MatchesQuery(row, query);
        };
        ListPane.Sort = Comparer();
        ListPane.GroupByCategory = GroupByCategoryButton.IsChecked == true;

        var shown = ListPane.ApplyFilterSort();
        var total = ListPane.All.Count;

        // The predicate is set whatever tab is up, so going back to the list shows the current scope.
        // The shared status slots belong to the surface on screen, so only write them when managing.
        if (_surface != Surface.Manage) return;

        if (total == 0)
        {
            _state.EmptyNext("No mods yet",
                "Nothing in any instance's mods folder, and nothing in your defaults. Import a jar "
                + "you already have, or use Browse to find some.",
                "", "Import", () => OnImport(this, new RoutedEventArgs()));
            _state.Content(0, note: NoteLine());
            return;
        }
        if (shown == 0)
        {
            _state.EmptyFiltered();
            _state.Content(0, note: NoteLine());
            return;
        }
        _state.Content(shown, note: NoteLine(), countText: ManageCountText(shown, total));
    }

    private static bool MatchesStore(GlobalModRow row, int index) => index switch
    {
        1 => row.Mod.PrimarySource == ModSource.CurseForge,
        2 => row.Mod.PrimarySource == ModSource.Modrinth,
        3 => row.Mod.IsExternal,
        _ => true
    };

    /// <summary>Everything about a mod a crash log or a memory might give you: its name, the jar, the
    /// version, the store, the categories, the note, and which instance it is in.</summary>
    private static bool MatchesQuery(GlobalModRow row, string query)
    {
        var mod = row.Mod;
        return mod.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || mod.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || mod.VersionLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
            || mod.SourceLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
            || mod.CategoriesLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (mod.Note is { } note && note.Contains(query, StringComparison.OrdinalIgnoreCase))
            || row.Copies.Any(c => c.Instance.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The list's order. Priority and content size are the tiebreakers everywhere in the
    /// launcher, and the name is the last word.</summary>
    private Comparison<GlobalModRow> Comparer()
    {
        var reverse = _reverseSort ? -1 : 1;
        var sort = _manageSort;
        return (a, b) =>
        {
            var primary = sort switch
            {
                ManageSort.Priority => b.Mod.Priority.CompareTo(a.Mod.Priority),
                ManageSort.Instances => b.InstanceCount.CompareTo(a.InstanceCount),
                ManageSort.Added => b.Mod.AddedAt.CompareTo(a.Mod.AddedAt),
                ManageSort.Size => b.Mod.ContentSize.CompareTo(a.Mod.ContentSize),
                _ => string.Compare(a.Mod.DisplayName, b.Mod.DisplayName, StringComparison.CurrentCultureIgnoreCase)
            };
            if (primary != 0) return primary * reverse;

            var priority = b.Mod.Priority.CompareTo(a.Mod.Priority);
            if (priority != 0) return priority;
            var size = b.Mod.ContentSize.CompareTo(a.Mod.ContentSize);
            if (size != 0) return size;
            return string.Compare(a.Mod.DisplayName, b.Mod.DisplayName, StringComparison.CurrentCultureIgnoreCase);
        };
    }

    /// <summary>The count slot: how many mods, how many of them are defaults, and how much of the
    /// list a filter is hiding.</summary>
    private string ManageCountText(int shown, int total)
    {
        var defaults = ListPane.All.Count(r => r.IsDefault);
        var text = shown == total
            ? $"{total:N0} mod{(total == 1 ? "" : "s")}"
            : $"{shown:N0} of {total:N0} mods";
        if (defaults > 0) text += $"  ·  {defaults:N0} default{(defaults == 1 ? "" : "s")}";
        return text;
    }

    /// <summary>The status line: the provenance sentence while a cached paint is up, else the scan's
    /// note about instances it couldn't read.</summary>
    private string? NoteLine() => _note ?? _snapshot?.Note;

    // ── the management surface: scope and folders ───────────────────────────

    /// <summary>One entry in the instance scope box.</summary>
    /// <remarks>Public, like the other view models here, because WPF binds through reflection.</remarks>
    /// <param name="Id">Null for "every instance".</param>
    public sealed record ScopeRow(Guid? Id, string Label);

    private void RebuildScopeBox()
    {
        using (_filling.Hold())
        {
            var wanted = _scopeId;
            _scopes.Clear();
            _scopes.Add(new ScopeRow(null, $"All instances ({_instances.Count})"));
            foreach (var instance in _instances.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
                _scopes.Add(new ScopeRow(instance.Id, instance.Name));

            // A scope pointing at a deleted instance falls back to "all" instead of an unexplained empty
            // list.
            var match = _scopes.FirstOrDefault(s => s.Id == wanted) ?? _scopes[0];
            _scopeId = match.Id;
            PackScopeBox.SelectedItem = match;
        }
    }

    /// <summary>
    /// The page scope changed. Every tab follows it, including the store browser.
    /// </summary>
    /// <remarks>For the browser the scope supplies the query's Minecraft version and loader
    /// (<see cref="EffectiveMcVersion"/>, <see cref="EffectiveLoader"/>) and the default install target
    /// (<see cref="PickTargetPackAsync"/>), so the results have to be fetched again.</remarks>
    private async void OnPackScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling.Busy) return;
        try
        {
            _scopeId = (PackScopeBox.SelectedItem as ScopeRow)?.Id;
            // Updates the list, board and graph even while browsing, so the tab you go back to is right.
            ManageFilterChanged();
            UpdateBrowseScopeNote();
            if (_surface == Surface.Browse) await ResetAndLoadAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.LogError(nameof(ModsView), ex); }
    }

    /// <summary>A folder chip, or the "New folder" chip that is always last.</summary>
    /// <remarks>Public for the same reason as <see cref="ScopeRow"/>: the strip's DataTemplate binds
    /// every one of these properties.</remarks>
    public sealed class ModFolderChipRow
    {
        public string Icon { get; init; } = "";
        public string Label { get; init; } = "";
        public string CountLabel { get; init; } = "";
        public string ToolTipText { get; init; } = "";
        public bool IsActive { get; init; }

        /// <summary>The folder this chip selects, or null for "all mods" and for "New folder".</summary>
        public string? Name { get; init; }

        /// <summary>True for the trailing chip that makes a folder rather than selecting one.</summary>
        public bool IsNew { get; init; }

        /// <summary>What the shared chip styles trigger on - the same fact as <see cref="IsNew"/>,
        /// under the name every other page's chip uses.</summary>
        public bool IsNewAction => IsNew;
    }

    private void RestoreFolderSelection()
    {
        // Reading the property is what heals a stale name: see ModFolderStore.Active.
        _ = _folders.Active;
    }

    private void RebuildFolderStrip()
    {
        var active = _folders.Active;
        _folderChips.Clear();
        _folderChips.Add(new ModFolderChipRow
        {
            Icon = "",
            Label = "All mods",
            CountLabel = ListPane.All.Count.ToString("N0"),
            ToolTipText = "Every mod, in every folder",
            IsActive = active is null
        });

        foreach (var name in _folders.Names())
        {
            var count = _folders.Count(name);
            _folderChips.Add(new ModFolderChipRow
            {
                Label = name,
                Name = name,
                CountLabel = count.ToString("N0"),
                ToolTipText = $"{count} mod{(count == 1 ? "" : "s")} in {name} - right-click to rename or delete",
                IsActive = string.Equals(active, name, StringComparison.Ordinal)
            });
        }

        // Always last. A plus rather than a folder glyph because it is an action in a row of filters;
        // the shared FolderChipBorder style outlines it for the same reason.
        _folderChips.Add(new ModFolderChipRow
        {
            Icon = "",
            Label = "New folder",
            ToolTipText = "Make a folder to file mods under",
            IsNew = true
        });
    }

    private void OnFolderChipClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ModFolderChipRow chip }) return;
        e.Handled = true;
        if (chip.IsNew) { _ = CreateFolderAsync(); return; }
        _folders.Active = chip.Name;
        RebuildFolderStrip();
        ManageFilterChanged();
    }

    private void OnFolderChipRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ModFolderChipRow chip } element) return;
        e.Handled = true;
        if (chip.Name is not { } name) return;

        var menu = new ContextMenu { PlacementTarget = element };
        var rename = new MenuItem { Header = "Rename folder" };
        rename.Click += (_, _) => _ = RenameFolderAsync(name);
        var delete = new MenuItem { Header = "Delete folder" };
        delete.Click += (_, _) => _ = DeleteFolderAsync(name);
        menu.Items.Add(rename);
        menu.Items.Add(delete);
        menu.IsOpen = true;
    }

    private async Task CreateFolderAsync()
    {
        try
        {
            var name = await _shell.PromptAsync("New folder", "Folder name", "");
            if (string.IsNullOrWhiteSpace(name)) return;
            if (ModFolderStore.ReservedReason(name) is { Length: > 0 } why)
            {
                _state.Note(why);
                return;
            }
            if (!_folders.Create(name))
            {
                _state.Note($"There is already a folder called '{name.Trim()}'.");
                return;
            }
            RebuildFolderStrip();
            _state.Note($"Created the folder '{name.Trim()}'.");
        }
        catch (Exception ex) { _state.Error("That folder could not be created.", ex); }
    }

    private async Task RenameFolderAsync(string oldName)
    {
        try
        {
            var name = await _shell.PromptAsync("Rename folder", "New folder name", oldName);
            if (string.IsNullOrWhiteSpace(name) || name.Trim() == oldName) return;
            // Same reserved-name rule as create, so a refused name can't be reached by renaming.
            if (ModFolderStore.ReservedReason(name) is { Length: > 0 } why)
            {
                _state.Note(why);
                return;
            }
            if (!_folders.Rename(oldName, name))
            {
                _state.Note($"There is already a folder called '{name.Trim()}'.");
                return;
            }
            RebuildFolderStrip();
            ManageFilterChanged();
        }
        catch (Exception ex) { _state.Error("That folder could not be renamed.", ex); }
    }

    private async Task DeleteFolderAsync(string name)
    {
        try
        {
            var count = _folders.Count(name);
            var ok = await AppDialog.ConfirmAsync(_shell, "Delete folder",
                count == 0
                    ? $"Delete the folder '{name}'?"
                    : $"Delete '{name}'? The {count} mod{(count == 1 ? "" : "s")} in it stay exactly where they are - "
                      + "only the folder goes.",
                "Delete", "Cancel", danger: true);
            if (!ok) return;
            _folders.Delete(name);
            RebuildFolderStrip();
            ManageFilterChanged();
        }
        catch (Exception ex) { _state.Error("That folder could not be deleted.", ex); }
    }

    // ── the management surface: the pane's actions ───────────────────────────

    private ModListPaneHost BuildPaneHost() => new()
    {
        // The one field that makes the shared options menu global: every flag edit in it ends at
        // SaveMeta(ctx.PackId, mod), so this writes the default instead of one instance's override.
        MetaScope = ModMetadataService.GlobalScope,
        Owner = _shell,
        Changed = OnPaneEdited,
        Reload = () => _ = LoadManageAsync(force: true),
        OpenPage = OpenModPage,
        Reveal = RevealMod,
        SetEnabled = (rows, enabled) => _ = SetEnabledAsync(rows, enabled),
        Delete = rows => _ = DeleteEverywhereAsync(rows),
        MakeDefault = rows => _ = MakeDefaultAsync(rows),
        StopBeingDefault = rows => _ = StopBeingDefaultAsync(rows),
        AddToInstances = OpenInstancePicker,
        EditRules = OpenRules,
        SetFolder = SetFolderMembership,
        Folders = () => _folders.Names(),
        InFolder = (row, folder) => _folders.Contains(folder, row.Key),
        DropFiles = paths => _ = ImportDroppedAsync(paths)
    };

    /// <summary>A flag, category or note was edited and persisted: re-render, and say so.</summary>
    /// <remarks>The edit went into the global document, so the rows' own <c>Meta</c> objects are
    /// already the edited ones: this is a re-sort and a repaint, not a re-read.</remarks>
    private void OnPaneEdited()
    {
        App.State.ModMetadata.SyncManagedCategories(ModMetadataService.GlobalScope);
        ApplyManageFilter();
        RebuildFolderStrip();
        // Flags and categories are what the other two tabs draw.
        _categoriesDirty = _graphDirty = true;
    }

    private void OpenModPage(GlobalModRow row)
    {
        // Opens the store page if there is one; otherwise the click does nothing.
        if (row.Mod.PageUrl is { } url) ModOptionsMenu.OpenUrl(url);
    }

    private void RevealMod(GlobalModRow row)
    {
        try
        {
            var path = row.Library?.Path ?? row.Copies.FirstOrDefault()?.Mod.FilePath ?? row.Mod.FilePath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            if (!SafeLaunch.RevealFile(path)) _state.Error("That file could not be shown.");
        }
        catch (Exception ex) { _state.Error("That file could not be shown.", ex); }
    }

    private void SetFolderMembership(IReadOnlyList<GlobalModRow> rows, string folder, bool member)
    {
        foreach (var row in rows) _folders.SetMembership(folder, row.Key, member);
        RebuildFolderStrip();
        // Filing a mod changes what an active folder chip admits, so the board hears about it too.
        ManageFilterChanged();
        _state.Note(member
            ? $"Filed {rows.Count} mod{(rows.Count == 1 ? "" : "s")} under '{folder}'."
            : $"Took {rows.Count} mod{(rows.Count == 1 ? "" : "s")} out of '{folder}'.");
    }

    /// <summary>
    /// Turns mods on or off in every instance that has them.
    /// </summary>
    /// <remarks>The confirmation names the instance count, since one click can move a dozen files.
    /// It is a rename on disk, so the library copy is untouched and a default stays a default.</remarks>
    private async Task SetEnabledAsync(IReadOnlyList<GlobalModRow> rows, bool enabled)
    {
        try
        {
            var copies = rows.SelectMany(r => r.Copies).Where(c => c.Mod.Enabled != enabled).ToList();
            if (copies.Count == 0)
            {
                _state.Note(rows.Count == 1 && rows[0].Copies.Count == 0
                    ? "That mod is in your defaults and in no instance yet, so there is nothing to switch."
                    : $"Already {(enabled ? "on" : "off")} everywhere.");
                return;
            }

            var instances = copies.Select(c => c.Instance.Id).Distinct().Count();
            if (instances > 1 || copies.Count > 1)
            {
                var verb = enabled ? "Enable" : "Disable";
                var what = rows.Count == 1 ? rows[0].Mod.DisplayName : $"{rows.Count} mods";
                var ok = await AppDialog.ConfirmAsync(_shell, $"{verb} in {instances} instances",
                    $"{verb} {what} in {instances} instance{(instances == 1 ? "" : "s")}? "
                    + "This renames the jar in each one; nothing is downloaded or deleted.",
                    verb, "Cancel");
                if (!ok) return;
            }

            var failed = 0;
            foreach (var copy in copies)
                if (!App.State.ModInventory.SetEnabled(copy.Mod, enabled)) failed++;

            // Both halves: the mod's own bindings, and the row's derived ones (the switch reads
            // EnabledEverywhere, which is computed from the copies that just changed).
            foreach (var row in rows) { row.Mod.Refresh(); row.Refresh(); }
            // ManageFilterChanged rather than just the filter: "Hide disabled" is part of the predicate the
            // board and graph use, and the graph draws disabled nodes differently.
            ManageFilterChanged();
            ListPane.UpdateSelectionBar();
            _state.Note(failed == 0
                ? $"{(enabled ? "Enabled" : "Disabled")} {copies.Count - failed} file{(copies.Count - failed == 1 ? "" : "s")}."
                : $"{copies.Count - failed} changed, {failed} could not be renamed - the game may be running.");
        }
        catch (Exception ex) { _state.Error("Those mods could not be switched.", ex); }
    }

    /// <summary>
    /// Deletes the jars from every instance that has them.
    /// </summary>
    /// <remarks>"Delete" on a global row could mean the library, one instance or everywhere, so this
    /// item says what it does. The library copy is never touched here; that is "Stop being a
    /// default".</remarks>
    private async Task DeleteEverywhereAsync(IReadOnlyList<GlobalModRow> rows)
    {
        try
        {
            var copies = rows.SelectMany(r => r.Copies).ToList();
            if (copies.Count == 0)
            {
                _state.Note("That mod is not in any instance - 'Stop being a default' is what removes the shared copy.");
                return;
            }

            var instances = copies.Select(c => c.Instance.Id).Distinct().Count();
            var what = rows.Count == 1 ? rows[0].Mod.DisplayName : $"{rows.Count} mods";
            var defaults = rows.Count(r => r.InLibrary);
            var keeps = defaults > 0
                ? $" The launcher keeps {(defaults == 1 ? "its copy" : "their copies")}, so {(defaults == 1 ? "it" : "they")} can be placed again."
                : "";
            var ok = await AppDialog.ConfirmAsync(_shell, "Delete from every instance",
                $"Delete {what} from {instances} instance{(instances == 1 ? "" : "s")}? "
                + $"{copies.Count} file{(copies.Count == 1 ? "" : "s")} will be removed.{keeps}",
                "Delete", "Cancel", danger: true);
            if (!ok) return;

            var deleted = 0;
            var failed = 0;
            foreach (var copy in copies)
            {
                try { File.Delete(copy.Mod.FilePath); deleted++; }
                catch (Exception ex) { failed++; AppLog.LogError(nameof(ModsView), ex); }
            }
            // Forget deleted mods so they don't count towards a folder.
            foreach (var row in rows.Where(r => !r.InLibrary)) _folders.Forget(row.Key);

            _state.Note(failed == 0
                ? $"Deleted {deleted} file{(deleted == 1 ? "" : "s")}."
                : $"Deleted {deleted}, {failed} could not be deleted - the game may be running.");
            await LoadManageAsync(force: true);
        }
        catch (Exception ex) { _state.Error("Those mods could not be deleted.", ex); }
    }

    /// <summary>
    /// Takes mods into the library and gives them a rule seeded from the instances they are in.
    /// </summary>
    /// <remarks>The rule is the union of the versions and loaders the mod is already in, so a mod from
    /// a 1.21.1 NeoForge instance isn't offered to a 1.16 Fabric one.</remarks>
    private async Task MakeDefaultAsync(IReadOnlyList<GlobalModRow> rows)
    {
        try
        {
            var candidates = rows.Where(r => !r.InLibrary && r.Copies.Count > 0).ToList();
            if (candidates.Count == 0)
            {
                _state.Note("Those are already in your defaults.");
                return;
            }

            var versions = ContentDefaults.Join(candidates
                .SelectMany(r => r.Copies)
                .Select(c => c.Instance.MinecraftVersion)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!.Trim()));
            var loaders = ContentDefaults.Join(candidates
                .SelectMany(r => r.Copies)
                .Where(c => c.Instance.Loader != LoaderKind.None)
                .Select(c => c.Instance.Loader.ToString()));

            var what = candidates.Count == 1 ? candidates[0].Mod.DisplayName : $"{candidates.Count} mods";
            var rule = $"Minecraft {versions ?? "any"} · {loaders ?? "any loader"}";
            var ok = await AppDialog.ConfirmAsync(_shell, "Make this a default",
                $"Have the launcher keep {what} and put {(candidates.Count == 1 ? "it" : "them")} in every "
                + $"instance that matches {rule}?\n\nThe instances that already have "
                + $"{(candidates.Count == 1 ? "it" : "them")} keep their own copy - nothing there is moved or "
                + "deleted. You can narrow the rule afterwards from the row's own menu.",
                "Make default", "Cancel");
            if (!ok) return;

            _state.Begin("Adding to your defaults...", refreshing: true);
            var added = 0;
            var problems = new List<string>();

            // One library copy per mod, taken from whichever instance has it; nothing is written into
            // instances here. The reconcile below places it, and instances that already have the mod keep
            // their own copy.
            foreach (var row in candidates)
            {
                var donor = row.Copies[0].Mod;
                var policy = new ContentDefaultPolicy
                {
                    Enabled = true,
                    McVersionsCsv = versions,
                    LoadersCsv = loaders,
                    // A shared instance's mods folder is uploaded to collaborators, so don't push a
                    // personal mod set to all of them.
                    KeepLocal = true
                };
                try
                {
                    _state.Progress($"Adding {donor.DisplayName}...");
                    await App.State.ModLibrary.AddFromFileAsync(
                        donor.FilePath, policy,
                        donor.PrimarySource, donor.PrimaryMod?.Id,
                        donor.PrimaryVersion?.Id, donor.PrimaryVersion?.VersionNumber,
                        row.Key);
                    added++;
                }
                catch (Exception ex)
                {
                    AppLog.LogError(nameof(ModsView), ex);
                    problems.Add(donor.DisplayName);
                }
            }

            _state.Note(problems.Count == 0
                ? $"{added} mod{(added == 1 ? " is" : "s are")} now a default - {rule}."
                : $"{added} added, {problems.Count} failed ({string.Join(", ", problems.Take(3))}).");
            await ReconcileAllAsync();
        }
        catch (Exception ex) { _state.Error("Those mods could not be made defaults.", ex); }
    }

    /// <summary>
    /// Takes mods out of the library. Every instance that has one keeps it.
    /// </summary>
    /// <remarks>Instances hold hard links, so the content survives; the launcher just stops handing
    /// the mod to further instances.</remarks>
    private async Task StopBeingDefaultAsync(IReadOnlyList<GlobalModRow> rows)
    {
        try
        {
            var defaults = rows.Where(r => r.Library is not null).ToList();
            if (defaults.Count == 0) return;

            var what = defaults.Count == 1 ? defaults[0].Mod.DisplayName : $"{defaults.Count} mods";
            var held = defaults.Sum(r => r.Copies.Count);
            var ok = await AppDialog.ConfirmAsync(_shell, "Stop being a default",
                $"Stop the launcher keeping {what}?\n\n"
                + (held > 0
                    ? $"The {held} instance cop{(held == 1 ? "y" : "ies")} keep working and are not deleted - "
                      + "the launcher just stops putting this in new instances."
                    : "Nothing is deleted from any instance."),
                "Stop", "Cancel");
            if (!ok) return;

            foreach (var row in defaults)
                await App.State.ModLibrary.RemoveAsync(row.Library!.Key);

            _state.Note($"{defaults.Count} mod{(defaults.Count == 1 ? " is" : "s are")} no longer a default.");
            await LoadManageAsync(force: true);
        }
        catch (Exception ex) { _state.Error("Those defaults could not be removed.", ex); }
    }

    // ── the management surface: the rules popup ──────────────────────────────

    private void OpenRules(GlobalModRow row)
    {
        if (row.Library is null) return;
        _rulesRow = row;
        _rulesDraft = App.State.ModLibrary.Rules(row.Library.Key);
        using (_filling.Hold())
        {
            RulesTitle.Text = "Default rules - " + row.Mod.DisplayName;
            RulesEnabled.IsChecked = _rulesDraft.Enabled;
            RulesMcBox.Text = _rulesDraft.McVersionsCsv ?? "";
            RulesLoaderBox.Text = _rulesDraft.LoadersCsv ?? "";
            RulesKeepLocal.IsChecked = _rulesDraft.KeepLocal;
        }
        UpdateRulesReach();
        RulesPopup.IsOpen = true;
    }

    private void OnRulesFieldChanged(object sender, RoutedEventArgs e)
    {
        if (_filling.Busy) return;
        UpdateRulesReach();
    }

    private void OnRulesTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_filling.Busy) return;
        UpdateRulesReach();
    }

    /// <summary>Shows how many instances the rule as typed would reach, before it is saved.</summary>
    private void UpdateRulesReach()
    {
        if (_rulesRow?.Library is not { } item)
        {
            RulesReach.Text = "";
            return;
        }
        var draft = DraftPolicy();
        if (!draft.Enabled)
        {
            RulesReach.Text = "Switched off: the launcher still keeps this, and it is only placed by hand.";
            return;
        }

        var matched = new List<string>();
        foreach (var instance in _instances)
            if (ContentCompatibility.Matches(draft, instance, item, ShaderLoader.Iris, out _))
                matched.Add(instance.Name);

        RulesReach.Text = matched.Count == 0
            ? "No instance matches this rule yet."
            : matched.Count <= 6
                ? $"Reaches {matched.Count} instance{(matched.Count == 1 ? "" : "s")}: {string.Join(", ", matched)}."
                : $"Reaches {matched.Count} instances, including {string.Join(", ", matched.Take(6))}.";
    }

    private ContentDefaultPolicy DraftPolicy()
    {
        var draft = (_rulesDraft ?? ContentDefaultPolicy.Off()).Clone();
        draft.Enabled = RulesEnabled.IsChecked == true;
        draft.McVersionsCsv = ContentDefaults.Join(ContentDefaults.Split(RulesMcBox.Text));
        draft.LoadersCsv = ContentDefaults.Join(ContentDefaults.Split(RulesLoaderBox.Text));
        draft.KeepLocal = RulesKeepLocal.IsChecked == true;
        return draft;
    }

    private void OnRulesCancel(object sender, RoutedEventArgs e)
    {
        RulesPopup.IsOpen = false;
        _rulesRow = null;
        _rulesDraft = null;
    }

    private async void OnRulesSave(object sender, RoutedEventArgs e)
    {
        var row = _rulesRow;
        if (row?.Library is not { } item) return;
        RulesPopup.IsOpen = false;
        try
        {
            App.State.ModLibrary.SetRules(item.Key, DraftPolicy());
            _rulesRow = null;
            _rulesDraft = null;

            // Apply the rule straight away rather than behind another button.
            await ReconcileAllAsync();
        }
        catch (Exception ex) { _state.Error("Those rules could not be saved.", ex); }
    }

    /// <summary>
    /// Brings every instance in line with the defaults, and reports what it did.
    /// </summary>
    /// <remarks>Checks <see cref="ModLibraryService.ConflictsWithInstalled"/> first: the same mod under
    /// two file names ends up twice in the instance and the game won't start, and the library only
    /// compares names. The user decides before anything is written.</remarks>
    private async Task ReconcileAllAsync()
    {
        if (await CollisionsBlockAsync()) return;

        _state.Begin("Applying your defaults...", refreshing: true);
        var changed = 0;
        var refused = 0;
        var problems = new List<string>();
        foreach (var instance in _instances)
        {
            try
            {
                var result = await App.State.ModLibrary.ReconcileAsync(instance);
                if (result.Refused) { refused++; continue; }
                if (result.DidAnything) changed++;
            }
            catch (Exception ex)
            {
                AppLog.LogError(nameof(ModsView), ex);
                problems.Add(instance.Name);
            }
        }

        var parts = new List<string>();
        parts.Add(changed == 0 ? "Everything is already as your defaults ask." : $"{changed} instance{(changed == 1 ? "" : "s")} updated.");
        if (refused > 0) parts.Add($"{refused} skipped - the game is running there.");
        if (problems.Count > 0) parts.Add($"{problems.Count} failed ({string.Join(", ", problems.Take(3))}).");
        _state.Note(string.Join(" ", parts));
        await LoadManageAsync(force: true);
    }

    /// <summary>
    /// True when the user has been told about a same-mod-different-file-name collision and chose to
    /// stop.
    /// </summary>
    /// <remarks>Answered from the snapshot's rows, which already hold every instance's mods, instead
    /// of a folder walk per item and instance.</remarks>
    private async Task<bool> CollisionsBlockAsync()
    {
        if (_snapshot is null) return false;

        var byInstance = _snapshot.Rows
            .SelectMany(r => r.Copies)
            .GroupBy(c => c.Instance.Id)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<PackMod>)g.Select(c => c.Mod).ToList());

        var clashes = new List<string>();
        foreach (var item in App.State.ModLibrary.Items())
        {
            foreach (var instance in _instances)
            {
                if (!App.State.ModLibrary.Admits(item, instance, out _)) continue;
                if (!byInstance.TryGetValue(instance.Id, out var installed)) continue;
                if (ModLibraryService.ConflictsWithInstalled(item, installed, out var why))
                    clashes.Add($"{instance.Name} - {item.DisplayName}: {why}");
            }
        }
        if (clashes.Count == 0) return false;

        var go = await AppDialog.ConfirmAsync(_shell, "Two copies of the same mod",
            $"{clashes.Count} instance{(clashes.Count == 1 ? "" : "s")} would end up with two file "
            + "names for one mod, which stops the game starting:\n\n"
            + string.Join("\n", clashes.Take(5))
            + (clashes.Count > 5 ? $"\n...and {clashes.Count - 5} more" : "")
            + "\n\nNarrow the rule, or delete the instance's own copy first.",
            "Apply anyway", "Stop", danger: true);
        if (go) return false;

        _state.Note("Nothing was placed. Narrow the rule, or remove the instance's own copy of that mod.");
        return true;
    }

    // ── the management surface: putting a mod in chosen instances ────────────

    /// <summary>One instance in the "add to instances" picker.</summary>
    /// <remarks>Public for WPF binding. Only the tick changes and nothing reads it while the popup is
    /// open, so there is no change notification. Colours come from the theme in the template.</remarks>
    public sealed class InstancePickRow
    {
        public required PackSummary Instance { get; init; }

        /// <summary>What the instance runs, how much of the selection it already has, and a warning
        /// when these mods aren't used on this version or loader.</summary>
        public required string Detail { get; init; }

        public required string ToolTipText { get; init; }

        /// <summary>The instance's name, which is what the row reads.</summary>
        public string Label => Instance.Name;

        /// <summary>False for an instance that already has every mod in the selection: its tick is
        /// shown on and locked, since un-ticking it would mean deleting a file.</summary>
        public bool CanChoose { get; init; } = true;

        public bool Chosen { get; set; }
    }

    private readonly ObservableCollection<InstancePickRow> _picks = new();
    private IReadOnlyList<GlobalModRow> _pickTargets = [];

    /// <summary>Opens the picker for one row or a whole selection.</summary>
    private void OpenInstancePicker(IReadOnlyList<GlobalModRow> rows)
    {
        if (rows.Count == 0) return;
        if (_instances.Count == 0)
        {
            _state.Note("There are no instances to put a mod in yet.");
            return;
        }

        _pickTargets = rows;
        InstancesTitle.Text = rows.Count == 1
            ? $"Put {rows[0].Mod.DisplayName} in..."
            : $"Put {rows.Count} mods in...";

        _picks.Clear();
        foreach (var instance in _instances.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var have = rows.Count(r => r.Copies.Any(c => c.Instance.Id == instance.Id));
            var all = have == rows.Count;
            _picks.Add(new InstancePickRow
            {
                Instance = instance,
                Detail = DescribePickTarget(instance, rows, have),
                ToolTipText = all
                    ? "Already here. Taking it back out is Delete, or 'Stop being a default' - not a tick box."
                    : $"Put it in {instance.Name}",
                CanChoose = !all,
                Chosen = all
            });
        }
        UpdatePickReach();
        InstancesPopup.IsOpen = true;
    }

    private static string DescribePickTarget(PackSummary instance, IReadOnlyList<GlobalModRow> rows, int have)
    {
        var parts = new List<string>
        {
            $"Minecraft {(string.IsNullOrWhiteSpace(instance.MinecraftVersion) ? "unknown" : instance.MinecraftVersion)}"
            + (instance.Loader == LoaderKind.None ? "" : $" · {instance.Loader}")
        };
        if (have == rows.Count) parts.Add("already here");
        else if (have > 0) parts.Add($"{have} of {rows.Count} already here");
        if (MismatchNote(instance, rows) is { Length: > 0 } warning) parts.Add(warning);
        return string.Join("  ·  ", parts);
    }

    /// <summary>
    /// Says when an instance is not like the ones these mods are already in.
    /// </summary>
    /// <remarks>A warning rather than a refusal: the launcher can't know a jar won't load. Based on
    /// the instances the mods are already in, since an unidentified jar has no store metadata.</remarks>
    private static string MismatchNote(PackSummary instance, IReadOnlyList<GlobalModRow> rows)
    {
        var already = rows.SelectMany(r => r.Copies).Select(c => c.Instance).ToList();
        var many = rows.Count > 1 ? "these" : "this";

        var loaders = already.Select(i => i.Loader).Where(l => l != LoaderKind.None).Distinct().ToList();
        if (instance.Loader != LoaderKind.None && loaders.Count > 0 && !loaders.Contains(instance.Loader))
            return $"you only run {many} on {string.Join("/", loaders)}";

        var versions = already.Select(i => i.MinecraftVersion)
            .Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (instance.MinecraftVersion is { Length: > 0 } mc && versions.Count > 0
            && !versions.Contains(mc.Trim(), StringComparer.OrdinalIgnoreCase))
            return $"you only run {many} on {string.Join(", ", versions.Take(3))}";

        return "";
    }

    private void OnInstancePickToggled(object sender, RoutedEventArgs e) => UpdatePickReach();

    /// <summary>Shows what the ticks add up to before anything is written, like the rules popup's
    /// reach line.</summary>
    private void UpdatePickReach()
    {
        var chosen = _picks.Where(p => p.Chosen && p.CanChoose).ToList();
        var mods = _pickTargets.Count;
        InstancesApplyButton.IsEnabled = chosen.Count > 0;
        InstancesReach.Text = chosen.Count == 0
            ? $"Tick the instances that should have {(mods == 1 ? "it" : "them")}."
            : $"{mods} mod{(mods == 1 ? "" : "s")} into {chosen.Count} instance{(chosen.Count == 1 ? "" : "s")}: "
              + string.Join(", ", chosen.Take(6).Select(p => p.Instance.Name))
              + (chosen.Count > 6 ? $" and {chosen.Count - 6} more" : "");
    }

    private void OnInstancesCancel(object sender, RoutedEventArgs e)
    {
        InstancesPopup.IsOpen = false;
        _pickTargets = [];
    }

    private async void OnInstancesApply(object sender, RoutedEventArgs e)
    {
        var rows = _pickTargets;
        var targets = _picks.Where(p => p.Chosen && p.CanChoose).Select(p => p.Instance).ToList();
        InstancesPopup.IsOpen = false;
        _pickTargets = [];
        if (rows.Count == 0 || targets.Count == 0) return;
        try { await AddToInstancesAsync(rows, targets); }
        catch (Exception ex) { _state.Error("Those mods could not be added.", ex); }
    }

    /// <summary>
    /// Puts mods into the instances the user ticked.
    /// </summary>
    /// <remarks>
    /// <para>Uses the defaults engine: an allowlist rule (<see cref="ContentDefaultPolicy.IncludePackIds"/>)
    /// or an instance's <see cref="ContentDefaultChoice.Forced"/> answer, after which
    /// <see cref="ReconcileAllAsync"/> links the files in.</para>
    /// <para>Always additive. A mod that isn't a default yet gets an allowlist of the ticked instances;
    /// an existing allowlist gets the ticks added. A version, loader or name rule is left alone and the
    /// ticked instances get Forced instead, since rewriting the rule would drop the mod from other
    /// instances. Forced also overrides an instance's opt-out and clears an earlier "never here".</para>
    /// </remarks>
    private async Task AddToInstancesAsync(IReadOnlyList<GlobalModRow> rows, IReadOnlyList<PackSummary> targets)
    {
        _state.Begin("Adding to your instances...", refreshing: true);
        var ids = targets.Select(t => t.Id).ToList();
        var ruled = 0;
        var forced = 0;
        var problems = new List<string>();

        foreach (var row in rows)
        {
            try
            {
                _state.Progress($"Adding {row.Mod.DisplayName}...");
                var item = row.Library;
                if (item is null)
                {
                    // Every row has a library item or copies, but the copies can be gone by the time this runs.
                    if (row.Copies.Count == 0)
                    {
                        problems.Add(row.Mod.DisplayName);
                        continue;
                    }
                    var donor = row.Copies[0].Mod;
                    item = await App.State.ModLibrary.AddFromFileAsync(
                        donor.FilePath,
                        new ContentDefaultPolicy
                        {
                            Enabled = true,
                            IncludePackIds = [.. ids],
                            // Same protection as "Make this a default": don't push a personal mod set
                            // to everyone on a shared instance.
                            KeepLocal = true
                        },
                        donor.PrimarySource, donor.PrimaryMod?.Id,
                        donor.PrimaryVersion?.Id, donor.PrimaryVersion?.VersionNumber,
                        row.Key);
                    ruled++;
                }
                else
                {
                    var policy = App.State.ModLibrary.Rules(item.Key);   // a clone; nothing written yet
                    if (!policy.Enabled)
                    {
                        // A library mod that was only ever placed by hand: the ticks become its rule.
                        policy.Enabled = true;
                        AddIds(policy, ids);
                        ruled++;
                    }
                    else if (policy.IncludePackIds.Count > 0)
                    {
                        // Already an allowlist: extend it, which keeps the row's rule line ("only N chosen
                        // instances") accurate.
                        AddIds(policy, ids);
                        ruled++;
                    }
                    else forced++;   // a live version/loader/name rule: left as the user wrote it

                    policy.ExcludePackIds.RemoveAll(ids.Contains);
                    App.State.ModLibrary.SetRules(item.Key, policy);
                }

                foreach (var id in ids)
                    App.State.ContentDefaults.SetChoice(id, item.Key, ContentDefaultChoice.Forced);
            }
            catch (Exception ex)
            {
                AppLog.LogError(nameof(ModsView), ex);
                problems.Add(row.Mod.DisplayName);
            }
        }

        var place = targets.Count == 1 ? targets[0].Name : $"{targets.Count} instances";
        var said = new List<string>();
        if (ruled > 0) said.Add($"{ruled} mod{(ruled == 1 ? " is" : "s are")} now set for {place}");
        if (forced > 0)
            said.Add($"{forced} already {(forced == 1 ? "has a rule, so it is" : "have rules, so they are")} "
                     + $"switched on for {place} without changing {(forced == 1 ? "it" : "them")}");
        if (problems.Count > 0) said.Add($"{problems.Count} failed ({string.Join(", ", problems.Take(3))})");
        _state.Note(said.Count == 0 ? "Nothing to add." : string.Join(". ", said) + ".");

        await ReconcileAllAsync();
    }

    /// <summary>Adds instance ids to a rule's allowlist, skipping the ones already in it.</summary>
    private static void AddIds(ContentDefaultPolicy policy, IReadOnlyList<Guid> ids)
    {
        foreach (var id in ids)
            if (!policy.IncludePackIds.Contains(id)) policy.IncludePackIds.Add(id);
    }

    // ── row 1 actions ────────────────────────────────────────────────────────

    /// <summary>
    /// The one Import flow: pick a jar, pick instances or none, and optionally publish it.
    /// </summary>
    /// <remarks>Hosting is the flow's last step. The card does the writes and the upload, so this
    /// only reloads afterwards.</remarks>
    private async void OnImport(object sender, RoutedEventArgs e)
    {
        try
        {
            var outcome = await ImportContentCard.ShowAsync(_shell,
                new ImportRequest(ImportContentKind.Mod, _instances, _scopeId));
            if (outcome is null) return;
            _state.Note(outcome.Summary);
            await LoadManageAsync(force: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _state.Error("That import could not be finished.", ex); }
    }

    private async Task ImportDroppedAsync(IReadOnlyList<string> paths)
    {
        try
        {
            var jar = paths.FirstOrDefault(p => p.EndsWith(".jar", StringComparison.OrdinalIgnoreCase));
            if (jar is null)
            {
                _state.Note("Only .jar files can be dropped here.");
                return;
            }
            // A drop already chose the file, so the card skips its first step.
            var outcome = await ImportContentCard.ShowAsync(_shell,
                new ImportRequest(ImportContentKind.Mod, _instances, _scopeId, jar));
            if (outcome is null) return;
            _state.Note(outcome.Summary);
            await LoadManageAsync(force: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _state.Error("That file could not be imported.", ex); }
    }


    /// <summary>Opens the folder the list is showing: the scoped instance's mods folder, or the
    /// library's own.</summary>
    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            string folder;
            if (_scopeId is { } id && _instances.FirstOrDefault(p => p.Id == id) is { } instance)
            {
                App.State.Packs.EnsurePackFolder(instance.Id, instance.Name, instance.IsShared);
                folder = Path.Combine(App.State.Packs.GameDir(instance.Id, instance.Name), "mods");
            }
            else
            {
                folder = App.State.Library.FolderFor(LibraryKind.Mod);
            }
            Directory.CreateDirectory(folder);
            if (!SafeLaunch.OpenFolder(folder)) _state.Error("That folder could not be opened.");
        }
        catch (Exception ex) { _state.Error("That folder could not be opened.", ex); }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        try { await RefreshAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.LogError(nameof(ModsView), ex); }
    }

    // ── row 3: sort and filters ─────────────────────────────────────────────

    private void OnSortButtonClick(object sender, RoutedEventArgs e)
    {
        if (SortButton.ContextMenu is null) return;
        UpdateSortMenuState();
        SortButton.ContextMenu.PlacementTarget = SortButton;
        SortButton.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        SortButton.ContextMenu.IsOpen = true;
    }

    private void OnSortMenuOpened(object sender, RoutedEventArgs e) => UpdateSortMenuState();

    private async void OnSortMenuItemClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if ((sender as MenuItem)?.Tag as string is not { } tag) return;
            if (tag is ModBrowseSort.Updated or ModBrowseSort.Created)
            {
                _sort = tag;
                UpdateSortMenuState();
                // The order is the server's, so a re-sort is a fresh first page rather than a
                // shuffle of what is already loaded.
                if (_surface == Surface.Browse) await ResetAndLoadAsync();
                return;
            }

            if (_surface == Surface.Browse && tag == ManageSort.Name)
            {
                _sort = ModBrowseSort.Name;
                UpdateSortMenuState();
                await ResetAndLoadAsync();
                return;
            }

            _manageSort = tag;
            UpdateSortMenuState();
            // A capped graph keeps the first N rows in this order, so the sort can change what it shows.
            ManageFilterChanged();
        }
        catch (Exception ex) { AppLog.LogError("ModsView.Sort", ex); }
    }

    private void OnSortReverseClick(object sender, RoutedEventArgs e)
    {
        _reverseSort = SortReverseMenuItem.IsChecked;
        ManageFilterChanged();   // see OnSortMenuItemClick: the order decides what a capped graph keeps
    }

    private void UpdateSortMenuState()
    {
        var browsing = _surface == Surface.Browse;
        var hosted = browsing && _activeChip?.Kind is ModBrowseSourceKind.CloudLauncherPublic
            or ModBrowseSourceKind.CloudLauncherPersonal
            or ModBrowseSourceKind.CloudLauncherShared
            or ModBrowseSourceKind.CloudLauncherTeam;

        // Store-only sorts are hidden while managing, since they don't apply to a local list.
        SortUpdatedMenuItem.Visibility = hosted ? Visibility.Visible : Visibility.Collapsed;
        SortCreatedMenuItem.Visibility = hosted ? Visibility.Visible : Visibility.Collapsed;
        SortPriorityMenuItem.Visibility = browsing ? Visibility.Collapsed : Visibility.Visible;
        SortInstancesMenuItem.Visibility = browsing ? Visibility.Collapsed : Visibility.Visible;
        SortAddedMenuItem.Visibility = browsing ? Visibility.Collapsed : Visibility.Visible;
        SortSizeMenuItem.Visibility = browsing ? Visibility.Collapsed : Visibility.Visible;
        SortReverseMenuItem.Visibility = browsing ? Visibility.Collapsed : Visibility.Visible;

        SortUpdatedMenuItem.IsChecked = browsing && _sort == ModBrowseSort.Updated;
        SortCreatedMenuItem.IsChecked = browsing && _sort == ModBrowseSort.Created;
        SortNameMenuItem.IsChecked = browsing ? _sort == ModBrowseSort.Name : _manageSort == ManageSort.Name;
        SortPriorityMenuItem.IsChecked = _manageSort == ManageSort.Priority;
        SortInstancesMenuItem.IsChecked = _manageSort == ManageSort.Instances;
        SortAddedMenuItem.IsChecked = _manageSort == ManageSort.Added;
        SortSizeMenuItem.IsChecked = _manageSort == ManageSort.Size;
        SortReverseMenuItem.IsChecked = _reverseSort;

        SortButton.ToolTip = browsing ? $"Sort results: {BrowseSortLabel(_sort)}" : "Sort mods";
    }

    private static string BrowseSortLabel(string sort) => sort switch
    {
        ModBrowseSort.Created => "Newest",
        ModBrowseSort.Name => "Name",
        _ => "Recently updated"
    };

    /// <summary>Opens the popup and resets the toggle to show whether a filter is on.</summary>
    /// <remarks>The click has already flipped IsChecked, which on this button means "a filter is on",
    /// not "the popup is open".</remarks>
    private void OnFiltersClick(object sender, RoutedEventArgs e)
    {
        FiltersPopup.IsOpen = true;
        SyncFiltersToggle();
    }

    private void OnFiltersPopupClosed(object sender, EventArgs e) => SyncFiltersToggle();

    private void SyncFiltersToggle() =>
        FiltersButton.IsChecked =
            FilterHideDisabled.IsChecked == true
            || FilterShadowedOnly.IsChecked == true
            || FilterNotesOnly.IsChecked == true
            || FilterSourceBox.SelectedIndex > 0
            || !string.IsNullOrWhiteSpace(FilterMcBox.Text)
            || FilterLoaderBox.SelectedIndex > 0;

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (_filling.Busy || !IsLoaded) return;
        ManageFilterChanged();
        SyncFiltersToggle();
    }

    private void OnFilterSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling.Busy || !IsLoaded) return;
        ManageFilterChanged();
        SyncFiltersToggle();
    }

    private async void OnApplyFilters(object sender, RoutedEventArgs e)
    {
        try
        {
            _filterMcVersion = string.IsNullOrWhiteSpace(FilterMcBox.Text) ? null : FilterMcBox.Text.Trim();
            var loader = (FilterLoaderBox.SelectedItem as ComboBoxItem)?.Content as string;
            _filterLoader = string.IsNullOrWhiteSpace(loader) || loader == "(any)" ? null : loader;
            FiltersPopup.IsOpen = false;
            SyncFiltersToggle();
            // Always both: one popup holds the local filters and the two the store query uses.
            ManageFilterChanged();
            if (_surface == Surface.Browse) await ResetAndLoadAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _state.Error("Those filters could not be applied.", ex); }
    }

    private async void OnClearFilters(object sender, RoutedEventArgs e)
    {
        try
        {
            using (_filling.Hold())
            {
                FilterHideDisabled.IsChecked = false;
                FilterShadowedOnly.IsChecked = false;
                FilterNotesOnly.IsChecked = false;
                FilterSourceBox.SelectedIndex = 0;
                FilterMcBox.Text = "";
                FilterLoaderBox.SelectedIndex = 0;
            }
            _filterMcVersion = null;
            _filterLoader = null;
            FiltersPopup.IsOpen = false;
            SyncFiltersToggle();
            ManageFilterChanged();
            if (_surface == Surface.Browse) await ResetAndLoadAsync();
        }
        catch (Exception ex) { _state.Error("Those filters could not be cleared.", ex); }
    }

    private void OnDefaultsOnlyClick(object sender, RoutedEventArgs e) => ManageFilterChanged();

    /// <summary>Grouping is how the list is drawn, not what it admits, so the board is left alone:
    /// it has its own headings and <see cref="ModListPane.Filtered"/> answers the same either way.</summary>
    private void OnGroupByCategoryClick(object sender, RoutedEventArgs e) => ApplyManageFilter();

    // ── search / keyboard ───────────────────────────────────────────────────

    private void OnShellKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsVisible) return;
        // A card (version picker, changelog) is open over the page and handles its own keys.
        if (Window.GetWindow(this) is MainWindow { DialogLayer.Visibility: Visibility.Visible }) return;
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            // The page search box on every tab, the graph included. The IsEnabled check keeps focus out
            // of a disabled box.
            if (!SearchBox.IsEnabled) return;
            SearchBox.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            _ = RefreshAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Tabs.SelectedIndex == TabList && !SearchBox.IsExpanded)
        {
            // One step back per press: the search first (it is what is hiding rows), then the
            // selection. Neither is destructive, so neither asks.
            if (ListPane.Selection.Count > 0)
            {
                foreach (var row in ListPane.All) row.IsSelected = false;
                ListPane.UpdateSelectionBar();
                e.Handled = true;
            }
        }
        // Delete / F2 act on the selected browse row, but only on your own hosted mods and never
        // while a search is being typed, when those keys belong to the text box.
        else if (e.Key is Key.Delete or Key.F2 && _surface == Surface.Browse && !SearchBox.IsExpanded)
        {
            if (ResultsList.SelectedItem is not ModBrowseRow { IsOwnedByMe: true } row) return;
            e.Handled = true;
            if (e.Key == Key.Delete) _ = DeleteHostedModAsync(row);
            else _ = RenameHostedModAsync(row);
        }
    }

    // Search as you type: CompactSearchBox settles the query after typing stops and only raises the
    // event when the settled text actually moved, so typing a letter and deleting it again costs
    // nothing. Enter still means "now".
    private string _lastSearched = "";

    private async void OnSearchSettled(object? sender, string query)
    {
        try
        {
            _searchText = query;
            // Applied on every tab, the store included, since the box is shared. ApplyManageFilter leaves
            // the status line to the browser while it owns it, and the canvases are only marked stale.
            ManageFilterChanged();
            if (_surface == Surface.Manage) return;
            if (_activeChip is null || string.Equals(query, _lastSearched, StringComparison.Ordinal)) return;
            await ResetAndLoadAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _browseState.Error("That search could not be run.", ex); }
    }

    private async void OnSearchSubmitted(object? sender, EventArgs e)
    {
        try
        {
            _searchText = SearchBox.Text.Trim();
            ManageFilterChanged();
            if (_surface == Surface.Manage) return;
            await ResetAndLoadAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _browseState.Error("That search could not be run.", ex); }
    }

    // ── the store browser: source chips ─────────────────────────────────────

    private async Task BuildChipsAsync()
    {
        _chips.Clear();
        _chips.Add(NewChip("", "CurseForge", ModBrowseSourceKind.CurseForge));
        _chips.Add(NewChip("", "Modrinth", ModBrowseSourceKind.Modrinth));
        _chips.Add(NewChip("", "CloudLauncher", ModBrowseSourceKind.CloudLauncherPublic));
        _chips.Add(NewDividerChip());
        _chips.Add(NewChip("", "Personal mods", ModBrowseSourceKind.CloudLauncherPersonal));
        _chips.Add(NewChip("", "Shared with me", ModBrowseSourceKind.CloudLauncherShared));
        _chips.Add(NewChip("", "All teams", ModBrowseSourceKind.CloudLauncherTeam));

        // Paint before asking the server for teams too: unstyled chips are invisible on the dark
        // theme, and the teams call can hang for the whole HTTP timeout when the server is unreachable.
        ApplyChipStyles();

        try
        {
            var teams = await App.State.Api.ListTeamsAsync();
            foreach (var team in teams.OrderBy(t => t.Name))
                _chips.Add(NewChip("", team.Name, ModBrowseSourceKind.CloudLauncherTeam, team.Id));
        }
        catch { /* teams are optional for browsing */ }

        ApplyChipStyles();
    }

    private static ModSourceChipRow NewChip(string icon, string label, ModBrowseSourceKind kind, Guid? teamId = null) =>
        new() { Icon = icon, Label = label, Kind = kind, TeamId = teamId };

    private static ModSourceChipRow NewDividerChip() => new()
    {
        IsDivider = true,
        Label = "|",
        Kind = ModBrowseSourceKind.Divider,
        IconVisibility = Visibility.Collapsed,
        CursorHint = Cursors.Arrow
    };

    private void ApplyChipStyles()
    {
        foreach (var chip in _chips)
        {
            if (chip.IsDivider)
            {
                chip.Background = Brushes.Transparent;
                chip.BorderColor = Brushes.Transparent;
                chip.Foreground = (Brush)FindResource("TextTertiaryBrush");
                chip.FontWeight = FontWeights.Normal;
                continue;
            }

            var active = chip == _activeChip;
            chip.Background = (Brush)FindResource(active ? "AccentBrush" : "Surface2Brush");
            chip.BorderColor = (Brush)FindResource(active ? "AccentBrush" : "BorderBrush");
            chip.Foreground = (Brush)FindResource(active ? "TextOnAccentBrush" : "TextSecondaryBrush");
            chip.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
        SourceStrip.Items.Refresh();
    }

    private async void OnSourceChipClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement fe && fe.DataContext is ModSourceChipRow row && !row.IsDivider)
                await SelectChipAsync(row);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetDownloadError("Couldn't switch source: " + ex.Message); }
    }

    private async Task SelectChipAsync(ModSourceChipRow chip)
    {
        _activeChip = chip;
        ApplyChipStyles();
        // Sort is only live on the CloudLauncher sources, so re-apply the strip for the new chip.
        ApplyStripForTab(Tabs.SelectedIndex);
        await ResetAndLoadAsync();
    }

    // ── the store browser: the page scope is its target ─────────────────────

    /// <summary>The instance the page is scoped to, or null for "All instances".</summary>
    private PackSummary? ScopedInstance =>
        _scopeId is { } id ? _instances.FirstOrDefault(p => p.Id == id) : null;

    /// <summary>The instance a browse or an install should assume, or null when the page is scoped
    /// to all of them or to something no mod can be installed into.</summary>
    /// <remarks>An instance without a Minecraft version or loader still scopes the mods list, but the
    /// browser treats it as no scope.</remarks>
    private PackSummary? BrowseTarget =>
        ScopedInstance is { } pack && IsModInstallProfile(pack) ? pack : null;

    /// <summary>
    /// The Minecraft version the store is asked for, and the loader.
    /// </summary>
    /// <remarks>The Filters popup wins where it is filled in and the page scope supplies the rest, as
    /// in <c>ModExplorerPage.EffectiveMcVersion</c>. Both are shown in
    /// <see cref="BrowseScopeNote"/>.</remarks>
    private string? EffectiveMcVersion() => _filterMcVersion ?? BrowseTarget?.MinecraftVersion;

    private string? EffectiveLoader() =>
        _filterLoader ?? (BrowseTarget is { } pack ? PackLoaderTag(pack) : null);

    /// <summary>An Install button's tooltip, naming the instance it will install into.</summary>
    private string InstallTargetTip(string what) =>
        BrowseTarget is { } pack
            ? $"{what} into {pack.Name} - right-click for another instance"
            : $"{what}; you pick the instance";

    /// <summary>Says what the page scope is doing to the browse tab, beside the store chips.</summary>
    private void UpdateBrowseScopeNote()
    {
        if (BrowseTarget is not { } pack)
        {
            // No scope, or one nothing can be installed into. Say so in the second case, since the list is
            // narrowed to that instance but the browser isn't.
            BrowseScopeNote.Text = ScopedInstance is { } scoped
                ? $"{scoped.Name} has no Minecraft version and loader to browse for, so results are unfiltered."
                : "";
            return;
        }
        BrowseScopeNote.Text =
            $"Browsing and installing as {pack.Name} - MC {pack.MinecraftVersion} · {PackLoaderTag(pack)}. "
            + "Right-click a result to install somewhere else.";
    }

    // ── the store browser: loading ──────────────────────────────────────────

    private async Task ResetAndLoadAsync()
    {
        if (_surface != Surface.Browse) return;
        _lastSearched = _searchText.Trim();
        _cts.Cancel();
        // Wait for the cancelled load before clearing: its finally resets the in-progress flag (until
        // then the new load would return at the guard), and a late page can't append to the cleared list.
        await _pager.DrainAsync();
        _cts = new CancellationTokenSource();
        _offset = 0;
        _rows.Clear();
        _pager.Reset();
        _hasMore = true;
        _sourceTotal = null;
        // A new query shows Loading, not Refreshing, since the old rows don't answer it.
        _browseState.Begin("Searching...", refreshing: false);
        ClearSelectedMod();
        await _pager.LoadPageAsync(_cts.Token);
    }

    private async void OnResultsScroll(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer sv) return;
        try { await _pager.FillAheadAsync(sv, _cts.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.LogError(nameof(ModsView), ex); }
    }

    private async Task LoadMoreAsync(CancellationToken ct)
    {
        if (_activeChip is null || _isLoading || !_hasMore) return;
        if (_rows.Count >= MaxResults) { _hasMore = false; return; }

        _isLoading = true;
        // Loading more over existing rows is a Refresh: a thin bar at the top, and the rows stay.
        if (_rows.Count > 0) _browseState.Begin(refreshing: true);
        try
        {
            int added;
            switch (_activeChip.Kind)
            {
                case ModBrowseSourceKind.CurseForge:
                    added = await LoadCurseForgeAsync(ct);
                    break;
                case ModBrowseSourceKind.Modrinth:
                    added = await LoadModrinthAsync(ct);
                    break;
                case ModBrowseSourceKind.CloudLauncherPersonal:
                    added = await LoadCloudLauncherAsync(ModBrowseSource.Personal, null, ct);
                    break;
                case ModBrowseSourceKind.CloudLauncherShared:
                    added = await LoadCloudLauncherAsync(ModBrowseSource.Shared, null, ct);
                    break;
                case ModBrowseSourceKind.CloudLauncherPublic:
                    added = await LoadCloudLauncherAsync(ModBrowseSource.Public, null, ct);
                    break;
                case ModBrowseSourceKind.CloudLauncherTeam:
                    added = await LoadCloudLauncherAsync(ModBrowseSource.Team, _activeChip.TeamId, ct);
                    break;
                default:
                    added = 0;
                    _hasMore = false;
                    break;
            }

            _offset += added;
            if (added == 0) _hasMore = false;
            ShowResultCount();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _hasMore = false;
            ReportLoadFailure(ex, ct);
        }
        finally
        {
            _isLoading = false;
        }
    }

    /// <summary>The count slot, and the empty panel when nothing came back. Zero rows with a load
    /// still running keeps the spinner.</summary>
    private void ShowResultCount()
    {
        if (_rows.Count > 0)
        {
            _browseState.Content(_rows.Count, countText: CountTextFor(_rows.Count));
            return;
        }
        if (!_hasMore)
        {
            EmptyCopyForActiveChip();
            _browseState.Content(0);
        }
    }

    private string CountTextFor(int count) =>
        // The hosted sources know the real total, so the slot says how far through it we are.
        _sourceTotal is { } total && total > 0
            ? $"{Math.Min(count, total):N0} of {total:N0}"
            : $"{count:N0} result{(count == 1 ? "" : "s")}";

    /// <summary>Picks the empty-panel wording for the active source.</summary>
    private void EmptyCopyForActiveChip()
    {
        // With an instance scoped, the query also carried its version and loader (EffectiveMcVersion),
        // so empty usually means nothing for that instance. Signed out on a CloudLauncher source wins.
        var kind = _activeChip?.Kind;
        var needsAccount = !App.State.Settings.IsLoggedIn
            && kind is ModBrowseSourceKind.CloudLauncherPersonal or ModBrowseSourceKind.CloudLauncherShared
                or ModBrowseSourceKind.CloudLauncherTeam or ModBrowseSourceKind.CloudLauncherPublic;
        if (!needsAccount && BrowseTarget is { } pack)
        {
            _browseState.EmptyNext($"Nothing here for {pack.Name}",
                $"This page is scoped to {pack.Name}, so the search asked for MC "
                + $"{pack.MinecraftVersion} · {PackLoaderTag(pack)} only. Pick 'All instances' in the "
                + "strip above to search every version and loader.");
            return;
        }

        switch (_activeChip?.Kind)
        {
            case ModBrowseSourceKind.CloudLauncherPersonal when !App.State.Settings.IsLoggedIn:
            case ModBrowseSourceKind.CloudLauncherShared when !App.State.Settings.IsLoggedIn:
            case ModBrowseSourceKind.CloudLauncherTeam when !App.State.Settings.IsLoggedIn:
            case ModBrowseSourceKind.CloudLauncherPublic when !App.State.Settings.IsLoggedIn:
                // Only the store chips need an account; managing local mods doesn't.
                _browseState.EmptyNext("Sign in to browse this",
                    "The stores and your hosted mods need an account. Everything on 'Your mods' is "
                    + "read from this PC and works signed out.", "");
                break;
            case ModBrowseSourceKind.CloudLauncherPersonal:
                _browseState.EmptyNext("No personal mods yet",
                    "Import a jar and tick 'publish' on the last step to host a mod here.",
                    "", "Import", () => OnImport(this, new RoutedEventArgs()));
                break;
            case ModBrowseSourceKind.CloudLauncherShared:
                _browseState.EmptyNext("No shared mods yet",
                    "Mods other people share with you directly appear here.", "");
                break;
            case ModBrowseSourceKind.CloudLauncherTeam:
                _browseState.EmptyNext(
                    _activeChip.TeamId is null ? "No team mods yet" : "This team has no mods yet",
                    "Mods published to a team you are on appear here.", "");
                break;
            case ModBrowseSourceKind.CloudLauncherPublic:
                _browseState.EmptyNext("No CloudLauncher mods found",
                    "Try a different search, filter, or source.", "");
                break;
            default:
                _browseState.EmptyFiltered();
                break;
        }
    }

    /// <summary>
    /// Tells a store error apart from being offline. Offline isn't treated as an error, and with
    /// nothing on screen the page switches back to the management list, which reads from disk.
    /// </summary>
    private void ReportLoadFailure(Exception ex, CancellationToken ct)
    {
        var why = ex is OfflineException offline
            ? offline.Reason ?? App.State.OfflineReason ?? "the connection failed"
            : Connectivity.DescribeTransportFailure(ex, ct);

        if (why is null)
        {
            _browseState.Error(StoreRequestException.PlainFor(ex) ?? "The store answered with an error.", ex);
            return;
        }

        _browseState.Offline(why);
        if (_rows.Count > 0) return;

        AppLog.Log(nameof(ModsView), $"Store unreachable ({why}); falling back to the mods on disk.");
        // Posted, not awaited: switching surfaces drains the pager, whose in-flight task is this
        // method's caller, so awaiting it here would wait on itself.
        _ = Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await ShowSurfaceAsync(Surface.Manage);
                _state.Note($"The store is not answering ({why}). These are the mods already on this PC.");
            }
            catch (OperationCanceledException) { }
            catch (Exception fallback) { AppLog.LogError(nameof(ModsView), fallback); }
        });
    }

    private async Task<int> LoadCurseForgeAsync(CancellationToken ct)
    {
        var hits = await App.State.CurseForge.SearchAsync(
            _searchText, EffectiveMcVersion(), EffectiveLoader(),
            limit: PageSize, offset: _offset,
            classId: CurseForgeService.ClassIdMods, ct: ct);
        foreach (var mod in hits) _rows.Add(ModBrowseRow.FromExternal(mod, "CurseForge", CurseForgeBrush(), Brushes.White));
        return hits.Count;
    }

    private async Task<int> LoadModrinthAsync(CancellationToken ct)
    {
        var hits = await App.State.Modrinth.SearchAsync(
            _searchText, EffectiveMcVersion(), EffectiveLoader(),
            limit: PageSize, offset: _offset, projectType: "mod", ct: ct);
        foreach (var mod in hits) _rows.Add(ModBrowseRow.FromExternal(mod, "Modrinth", ModrinthBrush(), Brushes.White));
        return hits.Count;
    }

    private async Task<int> LoadCloudLauncherAsync(ModBrowseSource source, Guid? teamId, CancellationToken ct)
    {
        var page = await App.State.Api.BrowseModsAsync(
            source, teamId, _searchText, EffectiveMcVersion(), EffectiveLoader(),
            offset: _offset, limit: PageSize, ct: ct, sort: _sort);

        // Resolve icons before building rows so each row appears complete. Fetched together (a page is
        // 25 mods); the cache means only new icons cost a request.
        var icons = await ResolveHostedIconsAsync(page.Items, ct);
        if (ct.IsCancellationRequested) return 0;

        foreach (var mod in page.Items)
            _rows.Add(ModBrowseRow.FromHosted(
                mod, (Brush)FindResource("AccentSoftBrush"), (Brush)FindResource("AccentBrush"),
                icons.GetValueOrDefault(mod.Id)));
        _hasMore = _offset + page.Items.Count < page.Total;
        // The server reports the total, so the count can read "40 of 512".
        _sourceTotal = page.Total;
        return page.Items.Count;
    }

    /// <summary>Local file paths for the icons of a page of hosted mods, keyed by mod id.</summary>
    /// <remarks>
    /// Icons of non-public mods need auth, which a WPF image binding can't send, so they go through the
    /// API client (cached by content hash). Missing or failed icons are left out and fall back to the
    /// letter tile.
    /// </remarks>
    private static async Task<Dictionary<Guid, string>> ResolveHostedIconsAsync(
        IReadOnlyList<HostedModSummary> mods, CancellationToken ct)
    {
        var wanted = mods.Where(m => !string.IsNullOrWhiteSpace(m.IconBlobHash)).ToList();
        if (wanted.Count == 0) return new Dictionary<Guid, string>();

        var results = new Dictionary<Guid, string>(wanted.Count);
        try
        {
            var fetches = wanted
                .Select(async m => (m.Id, Path: await App.State.Api.GetModIconFileAsync(m.Id, m.IconBlobHash, ct)))
                .ToList();
            foreach (var (id, path) in await Task.WhenAll(fetches))
                if (path is not null)
                    results[id] = path;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { AppLog.LogError("ModsView.ResolveIcons", ex); }
        return results;
    }

    // ── the store browser: detail panel ─────────────────────────────────────

    private async void OnResultSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsList.SelectedItem is not ModBrowseRow row) return;
        try
        {
            if (row.Hosted is not null) await LoadHostedModDetailAsync(row.Hosted.Id);
            else if (row.External is not null) await LoadExternalModDetailAsync(row.External);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetDownloadError("Couldn't load that mod: " + ex.Message); }
    }

    private async void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (ResultsList.SelectedItem is ModBrowseRow row)
                await InstallRowAsync(row);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetDownloadError("Install failed: " + ex.Message); }
    }

    // The Overview tab renders rich HTML/markdown in an embedded WebView2. Its HWND draws over WPF
    // (airspace), so hide it whenever another tab is selected. Scrolling is native.
    private void OnModTabsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != ModTabs) return;
        var onOverview = ModTabs.SelectedIndex == 0;
        OverviewHost.Visibility = onOverview ? Visibility.Visible : Visibility.Collapsed;
        OverviewBrowser.Visibility = onOverview ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowOverview(string? content, bool isMarkdown = false) => OverviewBrowser.Show(content, isMarkdown);

    /// <summary>Screenshots and Links only exist for CurseForge and Modrinth projects, so the tabs
    /// are collapsed for hosted mods.</summary>
    private void ShowStoreOnlyTabs(bool show)
    {
        var visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ScreenshotsTab.Visibility = visibility;
        LinksTab.Visibility = visibility;
    }

    private async Task LoadHostedModDetailAsync(Guid modId)
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        ClearSelectedMod(keepPanelVisible: true);
        DetailPlaceholder.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
        ShowOverview("Loading...");
        try
        {
            var detail = await App.State.Api.GetModAsync(modId, ct);
            _currentHostedMod = detail;
            _currentExternalMod = null;
            _currentProjectUrl = null;

            ModNameLabel.Text = detail.Name;
            ModMetaLabel.Text =
                $"by {detail.OwnerUsername} · {detail.Versions.Count} version{(detail.Versions.Count == 1 ? "" : "s")}"
                + $" · updated {TimeFormat.Date(detail.UpdatedAt)} · {detail.Visibility} · {detail.Slug}";
            SelectedIconFallback.Text = InitialFor(detail.Name);
            SetSelectedIcon(null);
            // Fetched after the panel is painted; the icon replaces the letter tile when it arrives.
            _ = ApplyHostedIconAsync(detail, ct);
            OpenProjectButton.Visibility = Visibility.Collapsed;
            ManageHostedButton.Visibility = detail.OwnerId == App.State.Settings.UserId
                ? Visibility.Visible : Visibility.Collapsed;
            InstallSelectedButton.Visibility = Visibility.Visible;
            InstallSelectedButton.IsEnabled = detail.Versions.Count > 0;
            InstallSelectedButton.ToolTip = detail.Versions.Count > 0
                ? InstallTargetTip("Install the newest compatible version")
                : "Nothing to install yet - upload a version from Manage first";
            ToolTipService.SetShowOnDisabled(InstallSelectedButton, true);

            ModTabs.SelectedIndex = 0;
            ShowOverview(detail.Description ?? detail.Summary, isMarkdown: true);
            // Hosted mods have no screenshots or project links, so hide those tabs.
            ShowStoreOnlyTabs(false);
            ScreenshotList.ItemsSource = null;
            ScreenshotList.Visibility = Visibility.Collapsed;
            ConfigureLinks(new ModProjectLinks(null, null, null, null, null));
            ShowHostedVersions(detail);
            DownloadStatus.Text = detail.Versions.Count == 0
                ? "Upload a version from Manage before this mod can be installed."
                : "";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ShowOverview("Error: " + ex.Message);
        }
    }

    private async Task LoadExternalModDetailAsync(ModSummary mod)
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        ClearSelectedMod(keepPanelVisible: true);
        DetailPlaceholder.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
        _currentExternalMod = mod;
        _currentHostedMod = null;
        _currentProjectUrl = BuildProjectUrl(mod);

        ModNameLabel.Text = mod.Name;
        ModMetaLabel.Text = $"by {mod.Author ?? "unknown"} · {FormatNumber(mod.DownloadCount)} downloads · {mod.Source}";
        SelectedIconFallback.Text = InitialFor(mod.Name);
        SetSelectedIcon(mod.IconUrl);
        OpenProjectButton.Visibility = Visibility.Visible;
        ManageHostedButton.Visibility = Visibility.Collapsed;
        InstallSelectedButton.Visibility = Visibility.Visible;
        InstallSelectedButton.IsEnabled = true;
        InstallSelectedButton.ToolTip =
            InstallTargetTip("Install the newest compatible version, with its required dependencies");
        ModTabs.SelectedIndex = 0;
        ShowOverview("Loading...");
        ShowStoreOnlyTabs(true);
        ScreenshotsEmptyText.Text = "Loading screenshots...";
        ScreenshotsEmptyText.Visibility = Visibility.Visible;
        ScreenshotList.ItemsSource = null;
        VersionsGrid.ItemsSource = null;
        VersionFilterNote.Text = "";
        ConfigureLinks(new ModProjectLinks(_currentProjectUrl, null, null, null, null));

        try
        {
            var detail = mod.Source == ModSource.CurseForge
                ? await App.State.CurseForge.GetProjectDetailAsync(int.TryParse(mod.Id, out var cid) ? cid : 0, ct)
                : await App.State.Modrinth.GetProjectDetailAsync(mod.Id, ct);
            ShowOverview(detail.Description ?? mod.Description, detail.IsMarkdown);
            ShowScreenshots(detail.Screenshots);
            ConfigureLinks(detail.Links with { WebsiteUrl = detail.Links.WebsiteUrl ?? _currentProjectUrl });

            // Fetched filtered to the browse filters, which is one small request rather than the
            // mod's whole file history; "Show all" pulls the rest on demand below.
            var showAll = ShowAllVersionsBox.IsChecked == true;
            _allExternalVersions.Clear();
            _allExternalVersions.AddRange(await LoadVersionsForModAsync(mod, applyFilters: !showAll, ct));
            _externalVersionsUnfiltered = showAll || (EffectiveMcVersion() is null && EffectiveLoader() is null);
            ShowExternalVersions(showAll);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ShowOverview("Error: " + ex.Message);
        }
    }

    private void ShowHostedVersions(HostedModDetail detail)
    {
        var rows = detail.Versions
            .OrderByDescending(v => v.PublishedAt)
            .Select(VersionDisplayRow.FromHosted)
            .ToList();
        VersionFilterNote.Text = $"{rows.Count} version{(rows.Count == 1 ? "" : "s")}";
        VersionsGrid.ItemsSource = rows;
    }

    private void ShowExternalVersions(bool all)
    {
        var filtered = all ? _allExternalVersions : _allExternalVersions.Where(v => MatchesFilters(v, EffectiveMcVersion(), EffectiveLoader())).ToList();
        VersionFilterNote.Text = all
            ? $"(all {_allExternalVersions.Count})"
            : $"(filtered {filtered.Count}/{_allExternalVersions.Count})";
        VersionsGrid.ItemsSource = filtered.Select(VersionDisplayRow.FromExternal).ToList();
    }

    /// <summary>True when <see cref="_allExternalVersions"/> holds every version rather than only
    /// those matching the browse filters.</summary>
    private bool _externalVersionsUnfiltered;

    private async void OnShowAllVersionsChanged(object sender, RoutedEventArgs e)
    {
        if (_currentExternalMod is null) return;

        var showAll = ShowAllVersionsBox.IsChecked == true;
        // The list arrives filtered to the pack; "Show all" needs the rest, fetched once on demand.
        if (showAll && !_externalVersionsUnfiltered && _currentExternalMod is { } mod)
        {
            VersionFilterNote.Text = "Loading every version...";
            try
            {
                var all = await LoadVersionsForModAsync(mod, applyFilters: false, CancellationToken.None);
                if (!ReferenceEquals(_currentExternalMod, mod)) return;
                _allExternalVersions.Clear();
                _allExternalVersions.AddRange(all);
                _externalVersionsUnfiltered = true;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { VersionFilterNote.Text = "Couldn't load every version: " + ex.Message; return; }
        }
        ShowExternalVersions(showAll);
    }

    private void ClearSelectedMod(bool keepPanelVisible = false)
    {
        _currentExternalMod = null;
        _currentHostedMod = null;
        _currentProjectUrl = null;
        _allExternalVersions.Clear();
        _externalVersionsUnfiltered = false;
        ModNameLabel.Text = "";
        ModMetaLabel.Text = "";
        SelectedIconFallback.Text = "";
        SetSelectedIcon(null);
        ShowOverview("");
        DownloadStatus.Text = "";
        VersionsGrid.ItemsSource = null;
        ScreenshotList.ItemsSource = null;
        ScreenshotList.Visibility = Visibility.Collapsed;
        VersionFilterNote.Text = "";
        if (!keepPanelVisible)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            DetailPlaceholder.Visibility = Visibility.Visible;
        }
    }

    private void OnManageHostedMod(object sender, RoutedEventArgs e)
    {
        if (_currentHostedMod is not null)
            _shell.OpenModDetail(_currentHostedMod.Id, _currentHostedMod.Name);
    }

    // ── the store browser: row context menu ─────────────────────────────────

    /// <summary>
    /// Right-click selects the row, then builds the menu for that one mod.
    /// </summary>
    /// <remarks>
    /// Built on demand rather than in the row template, so realizing rows stays cheap, and the items
    /// close over the row.
    /// </remarks>
    private void OnRowRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ModBrowseRow row) return;
        e.Handled = true;
        ResultsList.SelectedItem = row;   // the menu, the detail pane and Del/F2 all mean this row

        var menu = new ContextMenu { PlacementTarget = element };
        var hosted = row.Hosted is not null;
        var owner = row.IsOwnedByMe;

        MenuItem Item(string header, int glyph, Action onClick, string? gesture = null)
        {
            var item = new MenuItem { Header = header, Icon = Glyph(glyph) };
            if (gesture is not null) item.InputGestureText = gesture;
            item.Click += (_, _) => onClick();
            menu.Items.Add(item);
            return item;
        }

        if (hosted) Item("Manage", 0xE7C3, () => OpenHostedDetail(row));
        if (row.IsInstallable)
        {
            // With the page scoped, Install goes straight into that instance, so the item names it; the
            // menu also offers installing somewhere else.
            Item(ScopedInstance is { } scoped ? $"Install into {scoped.Name}" : "Install",
                 0xE896, () => _ = InstallFromMenuAsync(row));
            if (BrowseTarget is not null)
                Item("Install to another instance...", 0xE8DE,
                     () => _ = InstallFromMenuAsync(row, forcePicker: true));
        }

        if (owner)
        {
            Item("Upload version...", 0xE898, () => _ = UploadVersionAsync(row));
            menu.Items.Add(new Separator());
            Item("Rename...", 0xE8AC, () => _ = RenameHostedModAsync(row), "F2");

            var visibility = new MenuItem { Header = "Visibility", Icon = Glyph(0xE7B3) };
            foreach (var choice in new[] { PackVisibility.Private, PackVisibility.Team, PackVisibility.Public })
            {
                var pick = choice;
                var child = new MenuItem
                {
                    Header = pick.ToString(),
                    // The app's MenuItem template draws no check box, so the tick for the mod's current
                    // visibility goes in the gesture column, as in ModOptionsMenu.
                    InputGestureText = row.Hosted?.Visibility == pick ? "✓" : ""
                };
                child.Click += (_, _) => _ = SetVisibilityAsync(row, pick);
                visibility.Items.Add(child);
            }
            menu.Items.Add(visibility);

            Item("Manage collaborators...", 0xE716, () => _ = OpenCollaboratorsAsync(row));
        }

        menu.Items.Add(new Separator());
        // The store page, for anything that has one.
        if (row.External is not null) Item("Open page", 0xE774, () => OpenStorePage(row));
        if (hosted)
        {
            Item("Copy mod ID", 0xE8C8, () => CopyModId(row));
            Item("Copy slug", 0xE8C8, () => CopyModSlug(row));
        }

        if (owner)
        {
            menu.Items.Add(new Separator());
            Item("Delete...", 0xE74D, () => _ = DeleteHostedModAsync(row), "Del");
        }

        menu.IsOpen = true;
    }

    /// <summary>One menu glyph, in the same MDL2 vocabulary as ModOptionsMenu. The brush is a
    /// resource reference rather than a fetched brush because a menu outlives a theme change.</summary>
    private static TextBlock Glyph(int codepoint)
    {
        var icon = new TextBlock
        {
            Text = char.ConvertFromUtf32(codepoint),
            Width = 16,
            TextAlignment = TextAlignment.Center,
            FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        return icon;
    }

    private void OpenHostedDetail(ModBrowseRow row)
    {
        if (row.Hosted is { } mod) _shell.OpenModDetail(mod.Id, mod.Name);
    }

    private async Task InstallFromMenuAsync(ModBrowseRow row, bool forcePicker = false)
    {
        if (_installing) return;
        _installing = true;
        try { await InstallRowAsync(row, forcePicker); }
        finally { _installing = false; }
    }

    private async Task UploadVersionAsync(ModBrowseRow row)
    {
        if (row.Hosted is not { } summary) return;
        try
        {
            var detail = await App.State.Api.GetModAsync(summary.Id);
            var created = await UploadModVersionDialog.ShowAsync(_shell, detail);
            if (created is null) return;
            SetDownloadSuccess($"Uploaded {created.VersionString} to {detail.Name}.");
            await ResetAndLoadAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetDownloadError("Upload failed: " + ex.Message); }
    }

    private async Task RenameHostedModAsync(ModBrowseRow row)
    {
        if (row.Hosted is not { } mod) return;
        try
        {
            var dlg = new SimpleInputDialog("Rename mod", "New name", mod.Name) { Owner = _shell };
            if (dlg.ShowDialog() != true) return;
            var name = dlg.Result?.Trim();
            if (string.IsNullOrWhiteSpace(name) || name == mod.Name) return;

            await App.State.Api.UpdateModAsync(mod.Id, new UpdateModRequest(name, null, null, null));
            SetDownloadSuccess($"Renamed to {name}.");
            await ResetAndLoadAsync();
        }
        catch (Exception ex) { SetDownloadError("Rename failed: " + ex.Message); }
    }

    private async Task SetVisibilityAsync(ModBrowseRow row, PackVisibility visibility)
    {
        if (row.Hosted is not { } mod) return;
        if (visibility == mod.Visibility) return;
        try
        {
            await App.State.Api.UpdateModAsync(mod.Id, new UpdateModRequest(null, null, null, visibility));
            SetDownloadSuccess($"{mod.Name} is now {visibility}.");
            await ResetAndLoadAsync();
        }
        catch (Exception ex) { SetDownloadError("Couldn't change visibility: " + ex.Message); }
    }

    private async Task OpenCollaboratorsAsync(ModBrowseRow row)
    {
        if (row.Hosted is not { } mod) return;
        try
        {
            // PermissionsDialog needs the full detail (collaborators + teams), which the browse
            // summary does not carry.
            var detail = await App.State.Api.GetModAsync(mod.Id);
            new PermissionsDialog(detail) { Owner = _shell }.ShowDialog();
        }
        catch (Exception ex) { SetDownloadError("Couldn't open collaborators: " + ex.Message); }
    }

    private void OpenStorePage(ModBrowseRow row)
    {
        if (row.External is { } mod) OpenUrl(BuildProjectUrl(mod));
    }

    private void CopyModId(ModBrowseRow row)
    {
        if (row.Hosted is not { } mod) return;
        SetDownloadSuccess(ClipboardHelper.TrySetText(mod.Id.ToString())
            ? "Copied the mod ID."
            : "The clipboard is in use by another program.");
    }

    private void CopyModSlug(ModBrowseRow row)
    {
        if (row.Hosted is not { } mod) return;
        SetDownloadSuccess(ClipboardHelper.TrySetText(mod.Slug)
            ? $"Copied {mod.Slug}."
            : "The clipboard is in use by another program.");
    }

    private async Task DeleteHostedModAsync(ModBrowseRow row)
    {
        if (row.Hosted is not { } mod) return;
        try
        {
            var versions = mod.VersionCount;
            var consequence = versions == 0
                ? "It has no uploaded versions."
                : $"Its {versions} uploaded version{(versions == 1 ? "" : "s")} will stop being downloadable for everyone it is shared with.";
            var ok = await AppDialog.ConfirmAsync(_shell, "Delete mod",
                $"Delete {mod.Name}? {consequence} This cannot be undone.",
                "Delete", "Cancel", danger: true);
            if (!ok) return;

            await App.State.Api.DeleteModAsync(mod.Id);
            _rows.Remove(row);
            ClearSelectedMod();
            if (_sourceTotal is { } total) _sourceTotal = Math.Max(0, total - 1);
            ShowResultCount();
            SetDownloadSuccess($"Deleted {mod.Name}.");
        }
        catch (Exception ex) { SetDownloadError("Delete failed: " + ex.Message); }
    }

    /// <summary>The row's changelog button.</summary>
    private async void OnShowChangelog(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: VersionDisplayRow row }) await ShowChangelogAsync(row);
    }

    /// <summary>Opens the changelog card on one version, able to step through the rest of the grid
    /// in the order it is on screen.</summary>
    /// <remarks>Store versions carry no notes in their list entry, so the card fetches them on demand
    /// and renders both stores' formats.</remarks>
    private async Task ShowChangelogAsync(VersionDisplayRow row)
    {
        try
        {
            var (versions, index) = VersionChangelogCard.FromList(VersionsGrid.Items, row, ChangelogVersionOf);
            if (versions.Count == 0) return;
            var name = _currentExternalMod?.Name ?? _currentHostedMod?.Name ?? ModNameLabel.Text;
            await VersionChangelogCard.ShowAsync(this, name, versions, index, _currentExternalMod, _shell);
        }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(ModsView), ex);
            SetDownloadError("Could not open the changelog.");
        }
    }

    private static ModVersion? ChangelogVersionOf(VersionDisplayRow row) =>
        row.ExternalVersion
        ?? (row.HostedVersion is { } hosted ? VersionChangelogCard.FromHosted(hosted) : null);

    private void OnVersionsGridPreviewRightDown(object sender, MouseButtonEventArgs e) =>
        VersionRowMenu.SelectRowUnder(VersionsGrid, e);

    /// <summary>Right-click on a version: its changelog, an install of that specific version, and
    /// its number for the clipboard.</summary>
    private void OnVersionsGridRightClick(object sender, MouseButtonEventArgs e)
    {
        if (VersionRowMenu.RowAt<VersionDisplayRow>(e) is not { } row) return;
        e.Handled = true;
        VersionRowMenu.Open(BuildVersionMenu(row));
    }

    private ContextMenu BuildVersionMenu(VersionDisplayRow row)
    {
        var menu = VersionRowMenu.Create(VersionsGrid);
        VersionRowMenu.AddChangelog(menu, () => _ = ShowChangelogAsync(row));
        VersionRowMenu.Add(menu, "Install this version", VersionRowMenu.DownloadGlyph,
            () => _ = InstallVersionRowAsync(row));
        menu.Items.Add(new Separator());
        VersionRowMenu.AddCopyVersion(menu, row.VersionNumber, copied =>
        {
            if (copied) SetDownloadSuccess($"Copied {row.VersionNumber}.");
            else SetDownloadError("The clipboard is in use by another program.");
        });
        return menu;
    }

    // ── install/download ────────────────────────────────────────────────────

    // Guards the three install entry points below so a download in flight can't be started again
    // (quick-install, install-selected, and per-version download could otherwise race each other
    // and drop two copies of the same jar in the mods folder).
    private bool _installing;

    private async void OnQuickInstallMod(object sender, RoutedEventArgs e)
    {
        if (_installing) return;
        if (sender is FrameworkElement { DataContext: ModBrowseRow row })
        {
            _installing = true;
            try { await InstallRowAsync(row); }
            finally { _installing = false; }
        }
    }

    private async void OnInstallSelectedMod(object sender, RoutedEventArgs e)
    {
        if (_installing) return;
        if (ResultsList.SelectedItem is ModBrowseRow row)
        {
            _installing = true;
            try { await InstallRowAsync(row); }
            finally { _installing = false; }
        }
    }

    private async void OnDownloadVersion(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: VersionDisplayRow row }) await InstallVersionRowAsync(row);
    }

    /// <summary>Installs this specific version: the row's Install button and its menu item.</summary>
    private async Task InstallVersionRowAsync(VersionDisplayRow row)
    {
        if (_installing) return;
        _installing = true;
        try
        {
            if (row.ExternalVersion is not null && _currentExternalMod is not null)
                await InstallExternalVersionAsync(_currentExternalMod, row.ExternalVersion);
            else if (row.HostedVersion is not null && _currentHostedMod is not null)
                await InstallHostedVersionAsync(_currentHostedMod, row.HostedVersion);
        }
        catch (Exception ex)
        {
            SetDownloadError("Install failed: " + ex.Message);
        }
        finally
        {
            _installing = false;
        }
    }

    private async Task InstallRowAsync(ModBrowseRow row, bool forcePicker = false)
    {
        try
        {
            if (row.External is not null)
                await InstallLatestExternalAsync(row.External, forcePicker);
            else if (row.Hosted is not null)
                await InstallLatestHostedAsync(row.Hosted.Id, forcePicker);
        }
        catch (Exception ex)
        {
            SetDownloadError("Install failed: " + ex.Message);
        }
    }

    private async Task InstallLatestExternalAsync(ModSummary mod, bool forcePicker = false)
    {
        DownloadStatus.Text = $"Finding compatible files for {mod.Name}...";

        // The picker needs to know which instances this mod has a file for. Rather than the mod's whole
        // file history, ask for one filtered list per distinct Minecraft version + loader: MatchesPack
        // needs both to match, so the union holds the same versions, and the catalog caches each list.
        // With the page scoped and no picker coming, only the scoped instance's profile is asked.
        var packs = await App.State.Api.ListPacksAsync();
        var scopedOnly = !forcePicker && BrowseTarget is not null;
        var versions = await VersionsForProfilesAsync(
            mod, scopedOnly ? [BrowseTarget!] : packs.Where(IsModInstallProfile).ToList());

        // Nothing for the scoped instance: ask about every profile and let the picker offer the
        // instances that fit.
        if (scopedOnly && versions.Count == 0)
        {
            versions = await VersionsForProfilesAsync(mod, packs.Where(IsModInstallProfile).ToList());
            forcePicker = true;
        }

        var pack = await PickTargetPackAsync(
            "Install mod to...",
            "Only profiles matching this mod's Minecraft version and loader are shown.",
            "Install",
            p => versions.Any(v => MatchesPack(v, p)),
            "No matching profile found. Create or edit a non-empty Fabric, Forge, or NeoForge profile with a Minecraft version supported by this mod.",
            packs,
            forcePicker);
        if (pack is null) return;

        var version = PickBestVersion(versions, pack)
            ?? throw new InvalidOperationException($"No compatible version found for {PackCompatLabel(pack)}.");
        await InstallExternalVersionAsync(mod, version, pack);
    }

    /// <summary>The versions of one store mod that any of these instances could load, de-duplicated.
    /// One filtered request per distinct Minecraft version + loader, which is what the store can
    /// answer cheaply and the catalog caches.</summary>
    private static async Task<List<ModVersion>> VersionsForProfilesAsync(
        ModSummary mod, IReadOnlyList<PackSummary> profiles)
    {
        var versions = new List<ModVersion>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (mc, loader) in profiles
                     .Select(p => (Mc: p.MinecraftVersion!, Loader: PackLoaderTag(p)))
                     .Distinct())
        {
            foreach (var candidate in await App.State.ModVersions.GetVersionsAsync(mod, mc, loader))
                if (seen.Add(candidate.Id))
                    versions.Add(candidate);
        }
        return versions;
    }

    private async Task InstallLatestHostedAsync(Guid modId, bool forcePicker = false)
    {
        var detail = _currentHostedMod?.Id == modId
            ? _currentHostedMod
            : await App.State.Api.GetModAsync(modId);
        if (detail.Versions.Count == 0)
            throw new InvalidOperationException("This hosted mod has no uploaded versions yet.");
        EnsureCanDownload(detail);

        var pack = await PickTargetPackAsync(
            "Install mod to...",
            "Only profiles matching this hosted mod's Minecraft version and loader are shown.",
            "Install",
            p => detail.Versions.Any(v => MatchesPack(v, p, detail)),
            "No matching profile found. Create or edit a non-empty Fabric, Forge, or NeoForge profile supported by one of this hosted mod's versions.",
            forcePicker: forcePicker);
        if (pack is null) return;

        var version = PickBestVersion(detail.Versions, pack, detail)
            ?? throw new InvalidOperationException($"No compatible hosted version found for {PackCompatLabel(pack)}.");
        await InstallHostedVersionAsync(detail, version, pack);
    }

    private async Task InstallExternalVersionAsync(ModSummary mod, ModVersion version)
    {
        var pack = await PickTargetPackAsync(
            "Install mod to...",
            "Only profiles matching this version's Minecraft version and loader are shown.",
            "Install",
            p => MatchesPack(version, p),
            $"No matching profile found for {version.VersionNumber}. Pick another version or create a matching modded profile.");
        if (pack is null) return;

        await InstallExternalVersionAsync(mod, version, pack);
    }

    /// <summary>
    /// Installs one store version into an instance together with every dependency it declares as
    /// required.
    /// </summary>
    /// <remarks>
    /// Like the per-instance browser: resolve the required dependencies first, skip files already
    /// present, then fetch. Dependencies aren't flagged, and the root mod records its store so later
    /// update checks follow the right project.
    /// </remarks>
    private async Task InstallExternalVersionAsync(ModSummary mod, ModVersion version, PackSummary pack)
    {
        var folder = ModsFolderFor(pack);
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.IsIndeterminate = true;
        DownloadProgress.Value = 0;
        DownloadStatus.Text = $"Resolving dependencies for {mod.Name}...";

        try
        {
            var downloads = await ModDependencyResolver.ResolveRequiredDownloadsAsync(
                mod,
                version,
                pack.MinecraftVersion ?? "",
                PackLoaderTag(pack),
                App.State.Modrinth,
                App.State.CurseForge,
                App.State.ModMetadata.EffectiveUpdateChannel(pack.Id),
                App.State.ModVersions);

            if (downloads.Count == 0)
                throw new InvalidOperationException("No download URL available for this file.");

            // Count what is really missing first, so the progress below is not "3 of 12" where nine
            // were already in the folder.
            var pending = downloads
                .Where(item => !(item.IsDependency
                                 && PathSafety.ResolveFileName(folder, item.File.Filename) is { } present
                                 && File.Exists(present)))
                .ToList();
            var skipped = downloads.Count - pending.Count;

            // Names are settled before anything is fetched, because the downloads below overlap and
            // UniqueFilePath only knows about files already on disk. A store file name that cannot be
            // made into a plain name in the folder is not written at all.
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var targets = new List<(ModDownloadItem Item, string Dest)>();
            var refused = new List<string>();
            foreach (var item in pending)
            {
                if (UniqueFilePath(folder, item.File.Filename, claimed) is { } dest)
                {
                    targets.Add((item, dest));
                    continue;
                }
                AppLog.Log(nameof(ModsView), $"Skipped {item.Mod.Name}: the store's file name is not a usable file name: {item.File.Filename}");
                refused.Add($"{item.Mod.Name} (the store gave an unusable file name)");
            }
            pending = targets.Select(t => t.Item).ToList();

            // A mod and its dependencies are a few small files, mostly latency, so fetch them in parallel
            // up to the download concurrency setting, like the per-instance browser.
            var saved = 0;
            var rootFileName = "";
            var concurrency = App.State.Settings.EffectiveModDownloadConcurrency;
            using (var gate = new SemaphoreSlim(concurrency, concurrency))
            {
                var completed = 0;
                void ReportBatch(string? current = null)
                {
                    DownloadProgress.IsIndeterminate = pending.Count == 0;
                    DownloadProgress.Value = pending.Count == 0 ? 0 : completed * 100.0 / pending.Count;
                    DownloadStatus.Text = pending.Count == 1
                        ? $"Downloading {current ?? pending[0].File.Filename}..."
                        : $"Downloading {pending.Count} file(s) - {completed} done...";
                }
                ReportBatch();

                // One file at a time still gets a real percentage; several share the count.
                var progress = new Progress<(long done, long total)>(p =>
                {
                    if (p.total <= 0) return;
                    DownloadProgress.IsIndeterminate = false;
                    DownloadProgress.Value = (double)p.done / p.total * 100;
                });

                var failures = new List<string>(refused);
                await Task.WhenAll(targets.Select(async target =>
                {
                    await gate.WaitAsync();
                    try
                    {
                        await App.State.Modrinth.DownloadFileAsync(
                            target.Item.File.DownloadUrl, target.Dest, targets.Count == 1 ? progress : null);
                        Interlocked.Increment(ref saved);
                        if (!target.Item.IsDependency)
                        {
                            rootFileName = target.Item.File.Filename;
                            RememberInstallSource(pack.Id, target.Item);
                        }
                    }
                    catch (Exception ex) { lock (failures) failures.Add($"{target.Item.File.Filename} ({ex.Message})"); }
                    finally
                    {
                        gate.Release();
                        Interlocked.Increment(ref completed);
                        ReportBatch(target.Item.File.Filename);
                    }
                }));

                if (failures.Count > 0)
                    throw new InvalidOperationException(string.Join("; ", failures));
            }

            DownloadProgress.Value = 100;
            var dependencies = saved - (string.IsNullOrEmpty(rootFileName) ? 0 : 1);
            var extras = new List<string>(2);
            if (dependencies > 0) extras.Add($"{dependencies} dependenc{(dependencies == 1 ? "y" : "ies")}");
            if (skipped > 0) extras.Add($"{skipped} already present");
            SetDownloadSuccess(
                $"Installed {(string.IsNullOrEmpty(rootFileName) ? mod.Name : rootFileName)} to {pack.Name}"
                + (extras.Count == 0 ? "." : $" ({string.Join(", ", extras)})."));
            // The management list is now out of date by this one jar.
            _modSet.Invalidate();
        }
        finally
        {
            DownloadProgress.Visibility = Visibility.Collapsed;
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Value = 0;
        }
    }

    /// <summary>Records which store a just-installed jar came from, keyed by project id with a
    /// file-name fallback, so the instance's update check follows the same project later.</summary>
    private static void RememberInstallSource(Guid packId, ModDownloadItem item)
    {
        if (item.Mod.Source is not (ModSource.CurseForge or ModSource.Modrinth)) return;
        try
        {
            var keys = ModMetadataService.CandidateKeys(
                item.Mod.Source == ModSource.Modrinth ? item.Mod : null,
                item.Mod.Source == ModSource.CurseForge ? item.Mod : null,
                item.File.Filename);
            var meta = App.State.ModMetadata.GetMeta(packId, keys);
            meta.PreferredSource = item.Mod.Source;
            App.State.ModMetadata.SetMeta(packId, keys, meta);
        }
        catch
        {
            // Provenance is optional; a metadata write failure must not fail the install.
        }
    }

    /// <summary>Refuses, in words, before an instance is picked for a mod the caller may only
    /// look at.</summary>
    /// <remarks>Otherwise the server would answer with a bare 403 after the instance picker.</remarks>
    private static void EnsureCanDownload(HostedModDetail mod)
    {
        if (!mod.EffectivePermissions.HasFlag(PackPermissions.Download))
            throw new InvalidOperationException(
                $"You can see {mod.Name} but not download it. Ask {mod.OwnerUsername} for download access.");
    }

    private async Task InstallHostedVersionAsync(HostedModDetail mod, HostedModVersionInfo version)
    {
        EnsureCanDownload(mod);
        var pack = await PickTargetPackAsync(
            "Install mod to...",
            "Only profiles matching this hosted version's Minecraft version and loader are shown.",
            "Install",
            p => MatchesPack(version, p, mod),
            $"No matching profile found for {version.VersionString}. Pick another version or create a matching modded profile.");
        if (pack is null) return;

        await InstallHostedVersionAsync(mod, version, pack);
    }

    private async Task InstallHostedVersionAsync(HostedModDetail mod, HostedModVersionInfo version, PackSummary pack)
    {
        var folder = ModsFolderFor(pack);
        if (UniqueFilePath(folder, version.FileName) is not { } dest)
        {
            AppLog.Log(nameof(ModsView), $"Did not install {mod.Name}: the hosted file name is not a usable file name: {version.FileName}");
            throw new InvalidOperationException("The hosted mod's file name cannot be used.");
        }
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.IsIndeterminate = true;
        DownloadStatus.Text = $"Downloading {version.FileName}...";
        try
        {
            await using var stream = await App.State.Api.DownloadModVersionAsync(mod.Id, version.Id);
            await using var fs = File.Create(dest);
            await stream.CopyToAsync(fs);
            SetDownloadSuccess($"Installed {version.FileName} to {pack.Name}.");
            _modSet.Invalidate();
        }
        finally
        {
            DownloadProgress.Visibility = Visibility.Collapsed;
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Value = 0;
        }
    }

    /// <summary>
    /// Which instance an install goes into: the page scope when it has one, otherwise a picker.
    /// </summary>
    /// <param name="forcePicker">Ask even with a scope selected (the row menu's "Install to another
    /// instance...").</param>
    /// <remarks>The scoped instance must still pass the caller's compatibility test; if it doesn't
    /// (e.g. a NeoForge-only mod and a Fabric instance), the picker is shown.</remarks>
    private async Task<PackSummary?> PickTargetPackAsync(
        string title,
        string description,
        string actionText,
        Func<PackSummary, bool>? canPick = null,
        string? noMatchMessage = null,
        IReadOnlyList<PackSummary>? packs = null,
        bool forcePicker = false)
    {
        // A caller that already listed the instances to work out what this mod fits passes them in
        // rather than asking again.
        packs ??= await App.State.Api.ListPacksAsync();
        if (packs.Count == 0)
        {
            await AppDialog.MessageAsync(_shell, "No instance",
                "Create an instance before installing or importing mods.");
            return null;
        }

        var candidates = canPick is null ? packs : packs.Where(canPick).ToList();
        if (candidates.Count == 0)
        {
            await AppDialog.MessageAsync(_shell, "No compatible instance",
                noMatchMessage ?? "No compatible instance was found.");
            return null;
        }

        if (!forcePicker && _scopeId is { } scoped
            && candidates.FirstOrDefault(p => p.Id == scoped) is { } target)
            return target;

        var picker = new PackPickerDialog(candidates, title, description, actionText) { Owner = _shell };
        if (picker.ShowDialog() != true || picker.SelectedPackId is not { } packId)
            return null;
        return candidates.First(p => p.Id == packId);
    }

    private static string ModsFolderFor(PackSummary pack)
    {
        App.State.Packs.EnsurePackFolder(pack.Id, pack.Name, pack.IsShared);
        var folder = Path.Combine(App.State.Packs.GameDir(pack.Id, pack.Name), "mods");
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>A free path for a download. <paramref name="claimed"/> collects the paths handed out
    /// so far and is required when several downloads are in flight at once: on disk none of them
    /// exists yet, so two files with the same name would otherwise both be given it.</summary>
    /// <returns>Null when the name, once cleaned, is still not a plain file name (a device name such
    /// as CON, for one). The name comes from a store or another user, so nothing is written then.</returns>
    private static string? UniqueFilePath(string folder, string fileName, ISet<string>? claimed = null)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string((fileName ?? "mod.jar").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(clean)) clean = "mod.jar";
        if (!clean.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) clean += ".jar";
        if (PathSafety.ResolveFileName(folder, clean) is null) return null;

        var candidate = Path.Combine(folder, clean);
        var stem = Path.GetFileNameWithoutExtension(clean);
        var ext = Path.GetExtension(clean);
        var n = 2;
        while (File.Exists(candidate) || claimed?.Contains(candidate) == true)
            candidate = Path.Combine(folder, $"{stem}-{n++}{ext}");
        claimed?.Add(candidate);
        return candidate;
    }

    private void SetDownloadSuccess(string message) => SetStatusText(message, "AccentBrush");

    private void SetDownloadError(string message) => SetStatusText(message, "DangerBrush");

    /// <summary>Writes both status lines. The colour goes in as a resource reference rather than a
    /// resolved brush so the text follows a theme change instead of freezing in the old palette.</summary>
    private void SetStatusText(string message, string brushKey)
    {
        DownloadStatus.Text = message;
        DownloadStatus.SetResourceReference(ForegroundProperty, brushKey);
        StatusLabel.Text = message;
        StatusLabel.SetResourceReference(ForegroundProperty, brushKey);
    }

    // ── external mod details helpers ────────────────────────────────────────

    /// <summary>A store mod's versions, newest first, through the shared catalog, so the detail pane
    /// and the install share one cached list (15 minutes, shared while in flight).</summary>
    /// <remarks><paramref name="applyFilters"/> sends the browse filters to the store. Fetching a
    /// mod's full CurseForge history is slow: 50 files per page with ~170 ms between calls.</remarks>
    private async Task<List<ModVersion>> LoadVersionsForModAsync(ModSummary mod, bool applyFilters, CancellationToken ct)
    {
        var mc = applyFilters ? EffectiveMcVersion() : null;
        var loader = applyFilters ? EffectiveLoader() : null;
        return (await App.State.ModVersions.GetVersionsAsync(mod, mc, loader, ct: ct)).ToList();
    }

    private static ModVersion? PickBestVersion(IEnumerable<ModVersion> versions, PackSummary pack) =>
        ModUpdateChannel.PickNewest(
            versions.Where(v => MatchesPack(v, pack)),
            App.State.ModMetadata.EffectiveUpdateChannel(pack.Id),
            v => v.ReleaseChannel,
            v => v.DatePublished);

    private static HostedModVersionInfo? PickBestVersion(
        IEnumerable<HostedModVersionInfo> versions,
        PackSummary pack,
        HostedModDetail mod)
    {
        return ModUpdateChannel.PickNewest(
            versions.Where(v => MatchesPack(v, pack, mod)),
            App.State.ModMetadata.EffectiveUpdateChannel(pack.Id),
            v => v.ReleaseChannel,
            v => v.PublishedAt);
    }

    private static bool MatchesFilters(ModVersion version, string? mc, string? loader) =>
        (string.IsNullOrWhiteSpace(mc) || version.GameVersions.Contains(mc, StringComparer.OrdinalIgnoreCase)) &&
        (string.IsNullOrWhiteSpace(loader) || version.Loaders.Contains(loader, StringComparer.OrdinalIgnoreCase));

    private static bool MatchesPack(ModVersion version, PackSummary pack) =>
        IsModInstallProfile(pack)
        && ContainsToken(version.GameVersions, pack.MinecraftVersion!)
        && ContainsToken(version.Loaders, PackLoaderTag(pack));

    private static bool MatchesPack(HostedModVersionInfo version, PackSummary pack, HostedModDetail mod)
    {
        if (!IsModInstallProfile(pack)) return false;

        var mcCsv = string.IsNullOrWhiteSpace(version.McVersionsCsv) ? mod.McVersionsCsv : version.McVersionsCsv;
        var loadersCsv = string.IsNullOrWhiteSpace(version.LoadersCsv) ? mod.LoadersCsv : version.LoadersCsv;
        return CsvContains(mcCsv, pack.MinecraftVersion!)
            && CsvContains(loadersCsv, PackLoaderTag(pack));
    }

    private static bool IsModInstallProfile(PackSummary pack) =>
        !pack.IsEmpty
        && !string.IsNullOrWhiteSpace(pack.MinecraftVersion)
        && pack.Loader != LoaderKind.None;

    private static string PackLoaderTag(PackSummary pack) => pack.Loader.ToString().ToLowerInvariant();

    private static bool ContainsToken(IEnumerable<string> values, string expected) =>
        values.Any(v => string.Equals(v.Trim(), expected, StringComparison.OrdinalIgnoreCase));

    private static bool CsvContains(string? csv, string expected) =>
        !string.IsNullOrWhiteSpace(csv)
        && csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(v => string.Equals(v, expected, StringComparison.OrdinalIgnoreCase));

    private static string PackCompatLabel(PackSummary pack) =>
        $"MC {pack.MinecraftVersion} · {pack.Loader}";

    private void ShowScreenshots(IReadOnlyList<ModMediaItem> screenshots)
    {
        var rows = screenshots.Select(s => new MediaRow(s)).ToList();
        ScreenshotList.ItemsSource = rows;
        ScreenshotList.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ScreenshotsEmptyText.Text = "No screenshots are available for this mod.";
        ScreenshotsEmptyText.Visibility = rows.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ConfigureLinks(ModProjectLinks links)
    {
        ConfigureLinkButton(ProjectPageButton, links.WebsiteUrl);
        ConfigureLinkButton(IssuesButton, links.IssuesUrl);
        ConfigureLinkButton(SourceButton, links.SourceUrl);
        ConfigureLinkButton(WikiButton, links.WikiUrl);
        ConfigureLinkButton(DiscordButton, links.DiscordUrl);

        LinksEmptyText.Visibility =
            ProjectPageButton.Visibility == Visibility.Visible ||
            IssuesButton.Visibility == Visibility.Visible ||
            SourceButton.Visibility == Visibility.Visible ||
            WikiButton.Visibility == Visibility.Visible ||
            DiscordButton.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
    }

    private static void ConfigureLinkButton(Button button, string? url)
    {
        button.Tag = url;
        button.Visibility = string.IsNullOrWhiteSpace(url) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnOpenLink(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url }) OpenUrl(url);
    }

    private void OnOpenSelectedProject(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_currentProjectUrl))
            OpenUrl(_currentProjectUrl);
    }

    private void OnOpenScreenshot(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MediaRow row })
        {
            e.Handled = true;
            ScreenshotPreviewWindow.ShowFor(Window.GetWindow(this), row.FullImageUrl, row.Title);
        }
    }

    /// <summary>Links come from store metadata and hosted mods, so only http and https ones are
    /// opened.</summary>
    private void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!SafeLaunch.OpenUrl(url)) DownloadStatus.Text = "That link could not be opened.";
    }

    /// <summary>Swaps the detail pane's letter tile for the hosted mod's icon, once it arrives.</summary>
    /// <remarks>The selection is re-checked before the image is assigned: clicking down a list faster
    /// than the fetches complete would otherwise leave one mod's icon over another mod's name.</remarks>
    private async Task ApplyHostedIconAsync(HostedModDetail detail, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(detail.IconBlobHash)) return;
        try
        {
            var path = await App.State.Api.GetModIconFileAsync(detail.Id, detail.IconBlobHash, ct);
            if (path is null || ct.IsCancellationRequested || _currentHostedMod?.Id != detail.Id) return;
            SetSelectedIcon(path);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.LogError("ModsView.SelectedIcon", ex); }
    }

    /// <summary>
    /// The detail pane's icon, through the shared cache rather than a fresh <c>BitmapImage</c> per
    /// selection.
    /// </summary>
    /// <remarks>
    /// <see cref="IconLoader"/> caps the decode size, shares downloads with the list and drops late
    /// arrivals after the selection moves. It is the only thing that may set this element's Source.
    /// </remarks>
    private void SetSelectedIcon(string? iconUrl)
    {
        IconLoader.SetDecodeWidth(SelectedIconImage, 44);
        IconLoader.SetUrl(SelectedIconImage, string.IsNullOrWhiteSpace(iconUrl) ? null : iconUrl);
    }

    private static string BuildProjectUrl(ModSummary mod)
    {
        var slug = string.IsNullOrWhiteSpace(mod.Slug) ? mod.Id : mod.Slug;
        return mod.Source == ModSource.CurseForge
            ? $"https://www.curseforge.com/minecraft/mc-mods/{slug}"
            : $"https://modrinth.com/mod/{slug}";
    }

    private static string InitialFor(string name) =>
        string.IsNullOrWhiteSpace(name) ? "?" : name.Trim()[0].ToString().ToUpperInvariant();

    private static string FormatNumber(long n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:0.#}M",
        >= 1_000 => $"{n / 1_000.0:0.#}K",
        _ => n.ToString()
    };

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB"
    };

    private static Brush CurseForgeBrush() => new SolidColorBrush(Color.FromRgb(0xF1, 0x65, 0x36));
    private static Brush ModrinthBrush() => new SolidColorBrush(Color.FromRgb(0x1B, 0xD9, 0x6A));

    // ── view models ─────────────────────────────────────────────────────────

    public sealed class ModBrowseRow : INotifyPropertyChanged
    {
        public ModSummary? External { get; init; }
        public HostedModSummary? Hosted { get; init; }

        public string Name { get; init; } = "";
        public string Summary { get; init; } = "";
        public string MetaLabel { get; init; } = "";
        public string? IconUrl { get; init; }
        public string Initial { get; init; } = "?";
        public string SourceBadge { get; init; } = "";

        // Assigned brushes rather than DynamicResource, since the brush depends on the row's source.
        // They notify, so a theme change repaints only the badges.
        private Brush _badgeBackground = Brushes.Transparent;
        public Brush SourceBadgeBackground
        {
            get => _badgeBackground;
            set { if (!ReferenceEquals(_badgeBackground, value)) { _badgeBackground = value; Notify(); } }
        }

        private Brush _badgeForeground = Brushes.White;
        public Brush SourceBadgeForeground
        {
            get => _badgeForeground;
            set { if (!ReferenceEquals(_badgeForeground, value)) { _badgeForeground = value; Notify(); } }
        }

        public bool IsInstallable { get; init; } = true;

        /// <summary>The row's Install button.</summary>
        public Visibility InstallVisibility { get; init; } = Visibility.Visible;

        /// <summary>Why Install is greyed out, or what it will do. Shown on the disabled button too,
        /// so an empty hosted mod explains itself instead of looking broken.</summary>
        public string InstallToolTip { get; init; } = "Install the newest compatible version";

        /// <summary>Hover text for the whole row: the summary plus the compatibility line.</summary>
        public string RowToolTip { get; init; } = "";

        /// <summary>True for the signed-in user's own hosted mods, which get the owner-only menu
        /// items.</summary>
        public bool IsOwnedByMe => Hosted is not null && Hosted.OwnerId == App.State.Settings.UserId;

        public static ModBrowseRow FromExternal(ModSummary mod, string platform, Brush badgeBackground, Brush badgeForeground) => new()
        {
            External = mod,
            Name = mod.Name,
            Summary = string.IsNullOrWhiteSpace(mod.Description) ? "(no summary)" : mod.Description!,
            MetaLabel = $"by {mod.Author ?? "unknown"} · {FormatNumber(mod.DownloadCount)} downloads",
            IconUrl = mod.IconUrl,
            Initial = InitialFor(mod.Name),
            SourceBadge = platform,
            SourceBadgeBackground = badgeBackground,
            SourceBadgeForeground = badgeForeground,
            InstallToolTip = $"Install the newest {platform} version that fits an instance, with its required dependencies",
            RowToolTip = string.IsNullOrWhiteSpace(mod.Description) ? mod.Name : mod.Description!
        };

        /// <param name="iconPath">A local file holding the mod's icon, or null to keep the letter
        /// tile. A path rather than a URL because the icon route needs the session's token and an
        /// &lt;Image&gt; binding cannot send one.</param>
        public static ModBrowseRow FromHosted(HostedModSummary mod, Brush badgeBackground, Brush badgeForeground,
            string? iconPath = null)
        {
            // A mod with no uploaded jar, or one shared as "can view" (the server refuses the download),
            // says so on the row instead of failing after the instance picker.
            var hasVersions = mod.VersionCount > 0;
            var canDownload = mod.EffectivePermissions.HasFlag(PackPermissions.Download);
            var versions = hasVersions
                ? $" · {mod.VersionCount} version{(mod.VersionCount == 1 ? "" : "s")}"
                : " · no versions yet";
            return new ModBrowseRow
            {
                Hosted = mod,
                IconUrl = iconPath,
                Name = mod.Name,
                Summary = string.IsNullOrWhiteSpace(mod.Summary) ? "(no summary)" : mod.Summary!,
                MetaLabel = $"by {mod.OwnerUsername}{FormatCompat(mod.McVersionsCsv, mod.LoadersCsv)}{versions}",
                Initial = InitialFor(mod.Name),
                SourceBadge = mod.Visibility.ToString(),
                SourceBadgeBackground = badgeBackground,
                SourceBadgeForeground = badgeForeground,
                IsInstallable = hasVersions && canDownload,
                InstallToolTip = !hasVersions
                    ? "Nothing to install yet - upload a version from Manage first"
                    : !canDownload
                        ? $"You can see this mod but not download it. Ask {mod.OwnerUsername} for download access."
                        : "Install the newest compatible version into one of your instances",
                RowToolTip = (string.IsNullOrWhiteSpace(mod.Summary) ? mod.Name : mod.Summary!)
                    + $"\n{FormatCompatLine(mod.McVersionsCsv, mod.LoadersCsv)}"
            };
        }

        private static string FormatCompatLine(string? mcVersionsCsv, string? loadersCsv) =>
            $"MC {(string.IsNullOrWhiteSpace(mcVersionsCsv) ? "any" : mcVersionsCsv)} · "
            + $"{(string.IsNullOrWhiteSpace(loadersCsv) ? "any loader" : loadersCsv)}";

        private static string FormatCompat(string? mcVersionsCsv, string? loadersCsv)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(mcVersionsCsv)) parts.Add("MC " + mcVersionsCsv.Split(',')[0]);
            if (!string.IsNullOrWhiteSpace(loadersCsv)) parts.Add(loadersCsv.Split(',')[0]);
            return parts.Count == 0 ? "" : " · " + string.Join(" · ", parts);
        }

        private void Notify([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public sealed class VersionDisplayRow
    {
        public ModVersion? ExternalVersion { get; init; }
        public HostedModVersionInfo? HostedVersion { get; init; }
        public string VersionNumber { get; init; } = "";
        public string McVersions { get; init; } = "";
        public string LoaderList { get; init; } = "";
        public string ReleaseChannel { get; init; } = "";
        public string DateLabel { get; init; } = "";
        public string SizeLabel { get; init; } = "";

        /// <summary>The version's own notes, as a hosted mod stores one per upload. Store versions
        /// arrive from their list without notes, so this is empty for them; the changelog card
        /// fetches theirs when it is opened.</summary>
        public string? Changelog { get; init; }

        public bool HasChangelog => !string.IsNullOrWhiteSpace(Changelog);

        /// <summary>Every store row has a changelog button, because whether it has notes is only
        /// known by asking. A hosted row has one only when its notes are already here: an upload
        /// without notes has nothing the card could show.</summary>
        public Visibility ChangelogButtonVisibility =>
            ExternalVersion is not null || HasChangelog ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Row hover text: the first few lines of the changelog.</summary>
        public string? ChangelogToolTip => HasChangelog ? Truncate(Changelog!, 400) : null;

        private static string Truncate(string text, int max) =>
            text.Length <= max ? text : text[..max].TrimEnd() + "...";

        public static VersionDisplayRow FromExternal(ModVersion version) => new()
        {
            ExternalVersion = version,
            VersionNumber = version.VersionNumber,
            McVersions = string.Join(", ", version.GameVersions.Take(3)) + (version.GameVersions.Length > 3 ? "..." : ""),
            LoaderList = string.Join(", ", version.Loaders.Take(3)) + (version.Loaders.Length > 3 ? "..." : ""),
            ReleaseChannel = version.ReleaseChannel,
            DateLabel = TimeFormat.Date(version.DatePublished),
            SizeLabel = version.Files.FirstOrDefault()?.Size is long size ? FormatSize(size) : ""
        };

        public static VersionDisplayRow FromHosted(HostedModVersionInfo version) => new()
        {
            HostedVersion = version,
            VersionNumber = version.VersionString,
            McVersions = version.McVersionsCsv ?? "",
            LoaderList = version.LoadersCsv ?? "",
            ReleaseChannel = version.ReleaseChannel,
            DateLabel = TimeFormat.Date(version.PublishedAt),
            SizeLabel = FormatSize(version.FileSize),
            Changelog = version.Changelog
        };
    }
}
