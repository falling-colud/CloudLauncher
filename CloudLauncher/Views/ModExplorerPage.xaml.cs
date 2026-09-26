using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

public partial class ModExplorerPage : Page
{
    private readonly MainWindow _shell;
    private readonly PackDetail _pack;
    private readonly ObservableCollection<ModResultRow> _rows = new();
    private readonly ObservableCollection<ModSourceChipRow> _chips = new();
    private readonly HashSet<string> _installedModKeys = new(StringComparer.OrdinalIgnoreCase);
    private ModSourceChipRow? _activeChip;
    private List<ModVersion> _allVersions = new();
    private ModSummary? _currentMod;
    // When the open mod is cross-listed, these hold the same mod on each store so the source
    // toggle can switch which one is shown/downloaded; null = not available on that store.
    private ModSummary? _detailModrinth;
    private ModSummary? _detailCurseForge;
    private int _detailGeneration; // guards a slow counterpart lookup landing after the detail changed
    private ModSummary? _pendingShowMod; // a mod "Open page" asked to show; honored once the initial browse load lands
    private CancellationTokenSource _cts = new();

    /// <summary>Owns the paging and the load in flight, so a reset waits for the task it actually
    /// cancelled.</summary>
    private readonly InfiniteScroll.Pager _pager;

    /// <summary>The list area's state: loading, empty, error or offline.</summary>
    private readonly PageState _state;

    private string _searchText = "";
    private string? _filterMcOverride;
    private string? _filterLoaderOverride;
    // Store-specific category filter: Modrinth category slugs, or CurseForge numeric ids (as strings).
    private List<string> _filterCategories = new();
    private int _categoryGeneration; // guards against a slow category fetch landing after the source changed

    /// <summary>A category row in the filter list, carrying its own checkbox state.</summary>
    private sealed class CategoryFilterItem
    {
        public required string Label { get; init; }
        public required string Value { get; init; }
        public bool IsChecked { get; set; }
    }
    private int _sortMode; // 0 = relevance, 1 = latest, 2 = downloads
    private readonly HashSet<string> _allSeen = new(StringComparer.OrdinalIgnoreCase);

    private bool _isLoading;
    private bool _hasMore;
    private int _offset;
    private int _indexGeneration;
    private const int PageSize = 50;   // both stores cap a page at 50
    /// <summary>Safety stop so a runaway pager can't fill memory; not a browsing limit.</summary>
    private const int MaxResults = 2000;
    /// <summary>Below this width the detail sits under the results instead of beside them.</summary>
    private const double NarrowWidth = 700;

    /// <summary>Results and detail side by side when both columns fit, stacked otherwise.</summary>
    private void LayoutBody(double width)
    {
        var narrow = width < NarrowWidth;
        ResultsColumn.MinWidth = narrow ? 0 : 340;
        GapColumn.Width = new GridLength(narrow ? 0 : 16);
        DetailColumn.Width = narrow ? new GridLength(0) : new GridLength(4, GridUnitType.Star);
        DetailColumn.MinWidth = narrow ? 0 : 340;
        DetailRow.Height = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(DetailPane, narrow ? 0 : 2);
        Grid.SetRow(DetailPane, narrow ? 1 : 0);
        DetailPane.Margin = narrow ? new Thickness(0, 14, 0, 0) : new Thickness(0);
    }

    public ModExplorerPage(MainWindow shell, PackDetail pack)
    {
        InitializeComponent();
        _shell = shell;
        _pack = pack;
        HeroBadge.Text = InitialFor(pack.Name);
        HeroTitle.Text = $"Adding mods to {pack.Name}";
        HeroPackLabel.Text = $"Downloads land in this instance's mods/ folders · {PackVersionLabel()}";
        SourceStrip.ItemsSource = _chips;
        ResultsList.ItemsSource = _rows;
        ModTabs.SelectionChanged += OnModTabsChanged;
        BodyGrid.SizeChanged += (_, e) => LayoutBody(e.NewSize.Width);

        // Searches go to the stores, so the debounce is longer than a local filter's; 450ms suits the
        // APIs' pacing.
        SearchBox.DebounceMilliseconds = 450;
        SearchBox.TextChangedDebounced += OnSearchSettled;
        SearchBox.SearchSubmitted += OnSearchSubmitted;

        _pager = new InfiniteScroll.Pager(
            () => !_isLoading && _hasMore && _activeChip is not null && _rows.Count < MaxResults,
            () => _rows.Count,
            LoadMoreAsync);

        // No DisableWhileBusy: every toolbar control cancels and restarts the load anyway, and greying
        // them out during infinite-scroll top-ups would make them flicker.
        _state = new PageState(ResultsList, PageStateHost, nameof(ModExplorerPage))
            .Copy(PageCopy.Results)
            .Slots(SearchStatus, ListStatus);
        _state.RetryRequested += () => _ = ResetAndLoadAsync();

        Loaded += async (_, _) =>
        {
            UiScale.Changed -= ApplyModScale;   // re-apply the mod-list zoom live when the slider moves
            UiScale.Changed += ApplyModScale;
            ApplyModScale();
            ThemeService.Changed -= RepaintThemedChips;
            ThemeService.Changed += RepaintThemedChips;
            UpdateSortMenuState();

            // Fast cache-only pass first so already-known mods are badged immediately,
            // then resolve cross-store identities in the background and refine.
            await SeedInstalledFromCacheAsync();
            await BuildChipsAsync();
            if (_activeChip is null && _chips.Count > 0)
                await SelectChipAsync(_chips[0], userInitiated: false);
            _ = RefreshInstalledModIndexAsync();
        };
        Unloaded += (_, _) =>
        {
            UiScale.Changed -= ApplyModScale;
            ThemeService.Changed -= RepaintThemedChips;
            _cts.Cancel();
        };
    }

    // ── toolbar ──────────────────────────────────────────────────────────────

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        try { await ResetAndLoadAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _state.Error("The search could not be repeated.", ex); }
    }

    /// <summary>Opens the instance's mods folder; works even with both stores down.</summary>
    private void OnOpenModsFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.Combine(App.State.Packs.GameDir(_pack.Id), "mods");
            Directory.CreateDirectory(dir);
            if (!SafeLaunch.OpenFolder(dir)) DownloadStatus.Text = "Could not open the mods folder.";
        }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(ModExplorerPage), ex);
            DownloadStatus.Text = "Could not open the mods folder.";
        }
    }

    private void OnPageKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            SearchBox.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            _ = ResetAndLoadAsync();
            e.Handled = true;
        }
    }

    /// <summary>Re-paints the source chips after the theme changed.</summary>
    /// <remarks><see cref="ThemeService"/> swaps in new frozen brushes on every apply, and
    /// the chips are styled in code (their colours depend on which one is active), so they
    /// have to be painted again.</remarks>
    private void RepaintThemedChips()
    {
        if (!IsLoaded) return;
        ApplyChipStyles();
        UpdateSourceToggle();
    }

    private void ApplyModScale() => UiScale.ApplyModListScale(ResultsList);

    // The Overview tab hosts a WebView2 whose HWND draws over WPF (airspace), so hide it off-tab.
    // Scrolling is native, no manual wheel routing.
    private void OnModTabsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != ModTabs) return;
        var onOverview = ModTabs.SelectedIndex == 0;
        OverviewHost.Visibility = onOverview ? Visibility.Visible : Visibility.Collapsed;
        OverviewBrowser.Visibility = onOverview ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowOverview(string? content, bool isMarkdown = false) => OverviewBrowser.Show(content, isMarkdown);

    // ── source chips ─────────────────────────────────────────────────────────

    private async Task BuildChipsAsync()
    {
        _chips.Clear();
        _chips.Add(NewChip("", "All", ModBrowseSourceKind.All));
        _chips.Add(NewDividerChip());
        _chips.Add(NewChip("", "CurseForge", ModBrowseSourceKind.CurseForge));
        _chips.Add(NewChip("", "Modrinth",   ModBrowseSourceKind.Modrinth));
        _chips.Add(NewDividerChip());
        _chips.Add(NewChip("", "Personal", ModBrowseSourceKind.CloudLauncherPersonal));
        _chips.Add(NewChip("", "Shared",   ModBrowseSourceKind.CloudLauncherShared));
        _chips.Add(NewChip("", "Public",   ModBrowseSourceKind.CloudLauncherPublic));

        // Paint now as well as after the teams call. Unstyled chips are invisible on the dark theme,
        // and the teams call can hang for the full HTTP timeout when the server is down.
        ApplyChipStyles();

        try
        {
            var teams = await App.State.Api.ListTeamsAsync();
            foreach (var t in teams)
                _chips.Add(NewChip("", t.Name, ModBrowseSourceKind.CloudLauncherTeam, t.Id));
        }
        catch { /* teams unavailable, proceed without */ }

        ApplyChipStyles();
    }

    private static ModSourceChipRow NewChip(string icon, string label, ModBrowseSourceKind kind, Guid? teamId = null) =>
        new() { Icon = icon, Label = label, Kind = kind, TeamId = teamId };

    private static ModSourceChipRow NewDividerChip() => new()
    {
        IsDivider = true,
        Icon = "",
        Label = "|",
        Kind = ModBrowseSourceKind.Divider,
        IconVisibility = Visibility.Collapsed,
        CursorHint = Cursors.Arrow,
        ChipVisibility = Visibility.Visible
    };

    private void ApplyChipStyles()
    {
        foreach (var c in _chips)
        {
            if (c.IsDivider)
            {
                c.Background = Brushes.Transparent;
                c.BorderColor = Brushes.Transparent;
                c.Foreground = (Brush)FindResource("TextTertiaryBrush");
                c.FontWeight = FontWeights.Normal;
                continue;
            }
            var isActive = c == _activeChip;
            c.Background = (Brush)FindResource(isActive ? "AccentBrush" : "Surface2Brush");
            c.BorderColor = (Brush)FindResource(isActive ? "AccentBrush" : "BorderBrush");
            c.Foreground  = (Brush)FindResource(isActive ? "TextOnAccentBrush" : "TextSecondaryBrush");
            c.FontWeight  = isActive ? FontWeights.SemiBold : FontWeights.Normal;
        }
        SourceStrip.Items.Refresh();
    }

    private async void OnSourceChipClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ModSourceChipRow row && !row.IsDivider)
            await SelectChipAsync(row);
    }

    private async Task SelectChipAsync(ModSourceChipRow chip, bool userInitiated = true)
    {
        _activeChip = chip;
        ApplyChipStyles();
        // Categories are per-store, so a source switch clears the selection and reloads the list.
        _filterCategories = new();
        _ = RefreshCategoryFilterAsync();
        await ResetAndLoadAsync(userInitiated);
    }

    /// <summary>Fills the category checklist with the active store's own categories (Modrinth or
    /// CurseForge). Hidden for the aggregate "All" and the CloudLauncher sources, whose taxonomies
    /// don't line up. Fetched lazily and cached in the service.</summary>
    private async Task RefreshCategoryFilterAsync()
    {
        var gen = ++_categoryGeneration;
        var kind = _activeChip?.Kind;

        List<ModBrowseCategory> categories;
        string sourceLabel;
        switch (kind)
        {
            case ModBrowseSourceKind.CurseForge:
                sourceLabel = "  ·  CurseForge";
                categories = await App.State.CurseForge.GetCategoriesAsync(CurseForgeService.ClassIdMods);
                break;
            case ModBrowseSourceKind.Modrinth:
                sourceLabel = "  ·  Modrinth";
                categories = await App.State.Modrinth.GetCategoriesAsync("mod");
                break;
            default:
                CategoryFilterPanel.Visibility = Visibility.Collapsed;
                return;
        }

        if (gen != _categoryGeneration) return; // a newer source selection superseded this fetch

        CategorySourceLabel.Text = sourceLabel;
        FilterCategoryList.ItemsSource = categories
            .Select(c => new CategoryFilterItem { Label = c.Label, Value = c.Value })
            .ToList();
        // Keep the row out of the popup entirely if the store returned nothing (offline / API down).
        CategoryFilterPanel.Visibility = categories.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnClearCategories(object sender, RoutedEventArgs e)
    {
        if (FilterCategoryList.ItemsSource is IEnumerable<CategoryFilterItem> items)
            foreach (var it in items) it.IsChecked = false;
        // Rebind so the checkboxes visually clear (the POCO items don't raise change notifications).
        var src = FilterCategoryList.ItemsSource;
        FilterCategoryList.ItemsSource = null;
        FilterCategoryList.ItemsSource = src;
    }

    // ── search + filters ─────────────────────────────────────────────────────

    // Search as you type: CompactSearchBox raises the event 450ms after typing stops, and only if the
    // text changed. Enter searches immediately.
    private string _lastSearched = "";

    private async void OnSearchSettled(object? sender, string query)
    {
        try
        {
            _searchText = query;
            if (_activeChip is null || string.Equals(query, _lastSearched, StringComparison.Ordinal)) return;
            await ResetAndLoadAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _state.Error("That search could not be run.", ex); }
    }

    private async void OnSearchSubmitted(object? sender, EventArgs e)
    {
        try
        {
            _searchText = SearchBox.Text.Trim();
            await ResetAndLoadAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _state.Error("That search could not be run.", ex); }
    }

    private void OnFiltersClick(object sender, RoutedEventArgs e) => FiltersPopup.IsOpen = !FiltersPopup.IsOpen;

    private async void OnApplyFilters(object sender, RoutedEventArgs e)
    {
        _filterMcOverride = string.IsNullOrWhiteSpace(FilterMcBox.Text) ? null : FilterMcBox.Text.Trim();
        var loaderItem = FilterLoaderBox.SelectedItem as ComboBoxItem;
        var loader = loaderItem?.Content as string;
        _filterLoaderOverride = (string.IsNullOrEmpty(loader) || loader == "(inherit)") ? null : loader;
        _filterCategories = CategoryFilterPanel.Visibility == Visibility.Visible
                            && FilterCategoryList.ItemsSource is IEnumerable<CategoryFilterItem> items
            ? items.Where(i => i.IsChecked).Select(i => i.Value).ToList()
            : new();
        FiltersPopup.IsOpen = false;
        await ResetAndLoadAsync();
    }

    private string EffectiveMcVersion() => _filterMcOverride ?? PackMinecraftVersion();
    private string EffectiveLoader()    => _filterLoaderOverride ?? PackLoaderTag();

    // ── pagination / loading ─────────────────────────────────────────────────

    private async Task ResetAndLoadAsync(bool userInitiated = true)
    {
        // A user browse action (search / sort / chip / filter) discards any pending "Open page"
        // request. The initial load triggered right after ShowModAsync passes
        // userInitiated:false to keep it.
        if (userInitiated) _pendingShowMod = null;

        _lastSearched = _searchText.Trim();
        _cts.Cancel();
        // Wait for the cancelled load: its finally resets the in-progress flag, and until then the new
        // load below would return at the guard and leave the list empty.
        await _pager.DrainAsync();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _offset = 0;
        _rows.Clear();
        _pager.Reset();
        _allSeen.Clear();
        _hasMore = true;
        // A new query drops the old results, so this is Loading rather than Refreshing.
        _state.Begin(refreshing: false);
        if (_pendingShowMod is null) ClearSelectedMod(); // keep the requested mod on screen while results load
        await _pager.LoadPageAsync(ct);
        if (ct.IsCancellationRequested) return;

        // An explicit "Open page" wins; otherwise show the first result so the detail pane isn't empty.
        if (_pendingShowMod is { } pending)
        {
            _pendingShowMod = null;
            await LoadModDetailAsync(pending);
        }
        else if (_rows.Count > 0 && ResultsList.SelectedItem is null)
        {
            ResultsList.SelectedIndex = 0;
        }
    }

    private async void OnResultsScroll(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer sv) return;
        try { await _pager.FillAheadAsync(sv, _cts.Token); }
        catch (OperationCanceledException) { }
        catch (Exception) { /* LoadMoreAsync reports its own failures */ }
    }

    private async Task LoadMoreAsync(CancellationToken ct)
    {
        if (_activeChip is null || _isLoading || !_hasMore) return;
        if (_rows.Count >= MaxResults) { _hasMore = false; return; }

        _isLoading = true;
        // Loading more under existing rows is a Refresh: a thin bar at the top, and the rows stay.
        if (_rows.Count > 0) _state.Begin(refreshing: true);
        try
        {
            int added;
            switch (_activeChip.Kind)
            {
                case ModBrowseSourceKind.All:
                    added = await LoadAllAsync(ct);
                    break;
                case ModBrowseSourceKind.CurseForge:
                    added = await LoadCurseForgeAsync(ct);
                    break;
                case ModBrowseSourceKind.Modrinth:
                    added = await LoadModrinthAsync(ct);
                    break;
                case ModBrowseSourceKind.CloudLauncherPersonal:
                    added = await LoadCloudLauncherAsync(CloudLauncher.Shared.ModBrowseSource.Personal, null, ct);
                    break;
                case ModBrowseSourceKind.CloudLauncherShared:
                    added = await LoadCloudLauncherAsync(CloudLauncher.Shared.ModBrowseSource.Shared, null, ct);
                    break;
                case ModBrowseSourceKind.CloudLauncherPublic:
                    added = await LoadCloudLauncherAsync(CloudLauncher.Shared.ModBrowseSource.Public, null, ct);
                    break;
                case ModBrowseSourceKind.CloudLauncherTeam:
                    added = await LoadCloudLauncherAsync(CloudLauncher.Shared.ModBrowseSource.Team, _activeChip.TeamId, ct);
                    break;
                default:
                    added = 0; _hasMore = false; break;
            }
            _offset += added;
            if (added == 0) _hasMore = false;
            if (_rows.Count > 0)
                _state.Content(_rows.Count, countText:
                    $"{_rows.Count:N0} result{(_rows.Count == 1 ? "" : "s")}"
                    + (_hasMore ? " - scroll for more" : " - that is everything"));
            // Show the empty panel only when there are no rows and no more pages coming.
            else if (!_hasMore)
                _state.Content(0);
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

    /// <summary>Separates a store error from being offline. Offline gets its own state and message:
    /// nothing is broken, and the same search works once the network is back.</summary>
    private void ReportLoadFailure(Exception ex, CancellationToken ct)
    {
        var why = ex is OfflineException offline
            ? offline.Reason ?? App.State.OfflineReason ?? "the connection failed"
            : Connectivity.DescribeTransportFailure(ex, ct);

        if (why is not null) _state.Offline(why);
        else _state.Error(StoreRequestException.PlainFor(ex) ?? "The store answered with an error.", ex);
    }

    private async Task<int> LoadCurseForgeAsync(CancellationToken ct)
    {
        var mc = EffectiveMcVersion();
        var loader = EffectiveLoader();
        var hits = await App.State.CurseForge.SearchAsync(
            _searchText,
            mc.Length > 0 ? mc : null,
            loader.Length > 0 ? loader : null,
            limit: PageSize, offset: _offset,
            classId: CurseForgeService.ClassIdMods, sortField: CurseSortField(),
            categoryIds: CurseForgeCategoryIds(), ct: ct);
        foreach (var m in hits) _rows.Add(CreateResultRow(m));
        return hits.Count;
    }

    /// <summary>The selected CurseForge categories parsed to their numeric ids.</summary>
    private List<int> CurseForgeCategoryIds() =>
        _filterCategories.Select(v => int.TryParse(v, out var id) ? id : 0).Where(id => id > 0).ToList();

    private async Task<int> LoadModrinthAsync(CancellationToken ct)
    {
        var mc = EffectiveMcVersion();
        var loader = EffectiveLoader();
        var hits = await App.State.Modrinth.SearchAsync(
            _searchText,
            mc.Length > 0 ? mc : null,
            loader.Length > 0 ? loader : null,
            categories: _filterCategories.Count > 0 ? _filterCategories : null,
            limit: PageSize, offset: _offset, projectType: "mod", index: ModrinthIndex(), ct: ct);
        foreach (var m in hits) _rows.Add(CreateResultRow(m));
        return hits.Count;
    }

    private readonly Dictionary<string, HostedModSummary> _hostedByRowId = new();

    private async Task<int> LoadCloudLauncherAsync(ModBrowseSource source, Guid? teamId, CancellationToken ct)
    {
        var mc = EffectiveMcVersion();
        var loader = EffectiveLoader();
        var page = await App.State.Api.BrowseModsAsync(
            source, teamId, _searchText,
            mc.Length > 0 ? mc : null,
            loader.Length > 0 ? loader : null,
            offset: _offset, limit: PageSize, ct: ct);

        foreach (var m in page.Items)
        {
            var rowId = "cl:" + m.Id;
            _hostedByRowId[rowId] = m;
            var summary = new ModSummary(
                rowId, m.Slug, m.Name, m.OwnerUsername, m.Summary,
                m.DownloadCount, null, ModSource.External, Array.Empty<string>());
            _rows.Add(CreateResultRow(summary));
        }
        _hasMore = _offset + page.Items.Count < page.Total;
        return page.Items.Count;
    }

    // ── "All" aggregate source + sorting ─────────────────────────────────────────

    /// <summary>Opens the sort menu (a glyph button with a menu, as on the other pages).</summary>
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
            if ((sender as MenuItem)?.Tag is not string tag || !int.TryParse(tag, out var mode)) return;
            if (mode == _sortMode) { UpdateSortMenuState(); return; }
            _sortMode = mode;
            UpdateSortMenuState();
            // The stores do the sorting, so a new sort order reloads from the first page.
            if (IsLoaded && _activeChip is not null) await ResetAndLoadAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.LogError(nameof(ModExplorerPage), ex); }
    }

    private void UpdateSortMenuState()
    {
        SortRelevanceMenuItem.IsChecked = _sortMode == 0;
        SortLatestMenuItem.IsChecked = _sortMode == 1;
        SortDownloadsMenuItem.IsChecked = _sortMode == 2;
        SortButton.ToolTip = "Sort results: " + _sortMode switch
        {
            1 => "Latest",
            2 => "Downloads",
            _ => "Relevance"
        };
    }

    private string ModrinthIndex() => _sortMode switch { 1 => "updated", 2 => "downloads", _ => "relevance" };
    private int CurseSortField() => _sortMode switch { 1 => 3, 2 => 6, _ => 2 }; // 3=LastUpdated, 6=TotalDownloads, 2=Popularity

    /// <summary>Aggregates Modrinth + CurseForge + hosted public mods into one deduplicated list,
    /// sorted by the chosen mode. One store failing doesn't sink the others.</summary>
    private async Task<int> LoadAllAsync(CancellationToken ct)
    {
        var mc = EffectiveMcVersion();
        var loader = EffectiveLoader();
        string? mcArg = mc.Length > 0 ? mc : null, ldArg = loader.Length > 0 ? loader : null;

        var modrinthTask = SafeSearch(() => App.State.Modrinth.SearchAsync(
            _searchText, mcArg, ldArg, limit: PageSize, offset: _offset, index: ModrinthIndex(), ct: ct));
        var curseTask = SafeSearch(() => App.State.CurseForge.SearchAsync(
            _searchText, mcArg, ldArg, limit: PageSize, offset: _offset,
            classId: CurseForgeService.ClassIdMods, sortField: CurseSortField(), ct: ct));

        // Hosted (custom) mods are few, so fetch them on the first page only.
        var hosted = _offset == 0 ? await LoadHostedForAllAsync(mcArg, ldArg, ct) : new List<ModSummary>();
        var results = await Task.WhenAll(modrinthTask, curseTask);

        // Blend the two stores. Download counts are comparable across stores, so sort by them globally;
        // for relevance and latest, interleave each store's ranked page (Modrinth first, so it wins
        // dedupe ties).
        var merged = new List<ModSummary>(hosted);
        if (_sortMode == 2)
        {
            merged.AddRange(results[0]);
            merged.AddRange(results[1]);
            merged = merged.OrderByDescending(x => x.DownloadCount).ToList();
        }
        else
        {
            merged.AddRange(Interleave(results[0], results[1]));
        }

        foreach (var m in merged)
            if (_allSeen.Add(DedupeKey(m)))
                _rows.Add(CreateResultRow(m));

        if (results[0].Count + results[1].Count == 0) _hasMore = false;
        return PageSize; // advance both stores' offset together
    }

    private async Task<List<ModSummary>> LoadHostedForAllAsync(string? mc, string? loader, CancellationToken ct)
    {
        try
        {
            var page = await App.State.Api.BrowseModsAsync(
                ModBrowseSource.Public, null, _searchText, mc, loader, offset: 0, limit: PageSize, ct: ct);
            var list = new List<ModSummary>();
            foreach (var m in page.Items)
            {
                var rowId = "cl:" + m.Id;
                _hostedByRowId[rowId] = m;
                list.Add(new ModSummary(rowId, m.Slug, m.Name, m.OwnerUsername, m.Summary,
                    m.DownloadCount, null, ModSource.External, Array.Empty<string>()));
            }
            return list;
        }
        catch { return new List<ModSummary>(); }
    }

    private static async Task<List<ModSummary>> SafeSearch(Func<Task<List<ModSummary>>> search)
    {
        try { return await search(); } catch { return new List<ModSummary>(); }
    }

    /// <summary>Zipper-merges two ranked lists: a[0], b[0], a[1], b[1], ... then the rest of the longer
    /// list.</summary>
    private static IEnumerable<ModSummary> Interleave(IReadOnlyList<ModSummary> a, IReadOnlyList<ModSummary> b)
    {
        for (var i = 0; i < a.Count || i < b.Count; i++)
        {
            if (i < a.Count) yield return a[i];
            if (i < b.Count) yield return b[i];
        }
    }

    /// <summary>Cross-store dedupe key: same-named mods on CurseForge and Modrinth merge.</summary>
    private static string DedupeKey(ModSummary m)
    {
        var s = new string((m.Name ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return s.Length > 0 ? s : (m.Slug.Length > 0 ? m.Slug : m.Id);
    }

    private async void OnResultSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsList.SelectedItem is not ModResultRow row) return;
        if (TryGetHosted(row, out var hosted))
        {
            await LoadHostedModDetailAsync(hosted);
            return;
        }
        await LoadModDetailAsync(row.Source);
    }

    private async void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsList.SelectedItem is not ModResultRow row) return;
        if (TryGetHosted(row, out var hosted))
        {
            await LoadHostedModDetailAsync(hosted);
            return;
        }
        await LoadModDetailAsync(row.Source);
    }

    private bool TryGetHosted(ModResultRow row, out HostedModSummary hosted)
    {
        hosted = null!;
        return _hostedByRowId.TryGetValue(row.Source.Id, out hosted!);
    }

    private Guid? _currentHostedModId;

    private async Task LoadHostedModDetailAsync(HostedModSummary mod)
    {
        _cts.Cancel(); _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _currentMod = null;
        _detailModrinth = _detailCurseForge = null;
        SourceToggle.Visibility = Visibility.Collapsed;
        _currentHostedModId = mod.Id;
        DetailHeader.Visibility = Visibility.Visible;
        ModNameLabel.Text = mod.Name;
        ModMetaLabel.Text = $"by {mod.OwnerUsername} · CloudLauncher · {mod.Visibility}";
        SelectedIconFallback.Text = InitialFor(mod.Name);
        SetSelectedIcon(null);
        DetailPlaceholder.Visibility = Visibility.Collapsed;
        ModTabs.Visibility = Visibility.Visible;
        ModTabs.SelectedIndex = 0;
        ShowOverview(mod.Summary ?? "*No summary.*", isMarkdown: true);
        ScreenshotsEmptyText.Text = "Screenshots aren't supported for hosted mods yet.";
        ScreenshotsEmptyText.Visibility = Visibility.Visible;
        ScreenshotList.ItemsSource = null;
        ScreenshotList.Visibility = Visibility.Collapsed;
        ConfigureLinks(new ModProjectLinks(null, null, null, null, null));
        try
        {
            var detail = await App.State.Api.GetModAsync(mod.Id, ct);
            ShowOverview(detail.Description ?? detail.Summary, isMarkdown: true);
            var hostedVersions = detail.Versions.Select(v => new VersionRow(new ModVersion(
                v.Id.ToString(),
                v.VersionString, v.VersionString,
                v.McVersionsCsv?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>(),
                v.LoadersCsv?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>(),
                v.ReleaseChannel, v.PublishedAt, 0, v.Changelog, ModSource.External,
                Array.Empty<ModVersionFile>(),
                Array.Empty<ModDependency>()
            ))).ToList();
            VersionsGrid.ItemsSource = hostedVersions;
            VersionFilterNote.Text = $"{hostedVersions.Count} version(s)";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowOverview("Error: " + ex.Message); }
    }

    /// <summary>Public entry so the management List/Graph views can open a mod's page in this
    /// browser's right-hand detail panel (instead of the external website).</summary>
    public Task ShowModAsync(ModSummary mod)
    {
        // Remember it so the page's initial "All" load shows this mod instead of its first result.
        _pendingShowMod = mod;
        return LoadModDetailAsync(mod);
    }

    private async Task LoadModDetailAsync(ModSummary mod, bool resolveCounterpart = true)
    {
        _cts.Cancel(); _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _currentMod = mod;
        if (resolveCounterpart)
        {
            // Fresh detail: this store's mod is known; the other store gets filled in below.
            _detailModrinth = mod.Source == ModSource.Modrinth ? mod : null;
            _detailCurseForge = mod.Source == ModSource.CurseForge ? mod : null;
            UpdateSourceToggle();
        }
        var gen = ++_detailGeneration;
        DetailHeader.Visibility = Visibility.Visible;
        ModNameLabel.Text = mod.Name;
        ModMetaLabel.Text = $"by {mod.Author ?? "unknown"} · {FormatNumber(mod.DownloadCount)} downloads · {mod.Source}";
        SelectedIconFallback.Text = InitialFor(mod.Name);
        SetSelectedIcon(mod.IconUrl);
        DetailPlaceholder.Visibility = Visibility.Collapsed;
        ModTabs.Visibility = Visibility.Visible;
        ModTabs.SelectedIndex = 0;
        ShowOverview("Loading...");
        ScreenshotsEmptyText.Text = "Loading screenshots...";
        ScreenshotsEmptyText.Visibility = Visibility.Visible;
        ScreenshotList.ItemsSource = null;
        ScreenshotList.Visibility = Visibility.Collapsed;
        ConfigureLinks(new ModProjectLinks(BuildProjectUrl(mod), null, null, null, null));
        VersionsGrid.ItemsSource = null;
        VersionFilterNote.Text = "";
        try
        {
            var detail = mod.Source == ModSource.CurseForge
                ? await App.State.CurseForge.GetProjectDetailAsync(int.TryParse(mod.Id, out var cid) ? cid : 0, ct)
                : await App.State.Modrinth.GetProjectDetailAsync(mod.Id, ct);
            ShowOverview(detail.Description ?? mod.Description, detail.IsMarkdown);
            ShowScreenshots(detail.Screenshots);
            ConfigureLinks(detail.Links with { WebsiteUrl = detail.Links.WebsiteUrl ?? BuildProjectUrl(mod) });

            var showAll = ShowAllVersionsBox.IsChecked == true;
            _allVersions = await LoadVersionsForModAsync(mod, applyFilters: !showAll, ct);
            _versionsUnfiltered = showAll;

            ShowVersions(showAll);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowOverview("Error: " + ex.Message); }

        if (resolveCounterpart)
            _ = ResolveDetailCounterpartAsync(mod, gen, ct);
    }

    /// <summary>Looks up the same mod on the other store in the background and, if found, shows the
    /// source toggle.</summary>
    private async Task ResolveDetailCounterpartAsync(ModSummary mod, int gen, CancellationToken ct)
    {
        try
        {
            var other = mod.Source == ModSource.CurseForge ? ModSource.Modrinth : ModSource.CurseForge;
            var counterpart = other == ModSource.Modrinth
                ? await App.State.Modrinth.FindCounterpartAsync(mod.Slug, mod.Name, ct)
                : await App.State.CurseForge.FindCounterpartAsync(mod.Slug, mod.Name, ct);
            if (ct.IsCancellationRequested || gen != _detailGeneration || counterpart is null) return;

            if (other == ModSource.Modrinth) _detailModrinth = counterpart;
            else _detailCurseForge = counterpart;
            UpdateSourceToggle();
        }
        catch { /* offline or no counterpart: leave the toggle hidden */ }
    }

    /// <summary>Shows/hides the store toggle and highlights the active store.</summary>
    private void UpdateSourceToggle()
    {
        var both = _detailModrinth is not null && _detailCurseForge is not null;
        SourceToggle.Visibility = both ? Visibility.Visible : Visibility.Collapsed;
        if (!both) return;

        var active = _currentMod?.Source ?? ModSource.Modrinth;
        StyleSourceChip(SourceModrinthChip, active == ModSource.Modrinth);
        StyleSourceChip(SourceCurseForgeChip, active == ModSource.CurseForge);
    }

    private void StyleSourceChip(Border chip, bool active)
    {
        chip.Background = (Brush)FindResource(active ? "AccentBrush" : "Surface2Brush");
        chip.BorderBrush = (Brush)FindResource(active ? "AccentBrush" : "BorderBrush");
        if (chip.Child is TextBlock t)
            t.Foreground = (Brush)FindResource(active ? "TextOnAccentBrush" : "TextSecondaryBrush");
    }

    private async void OnPickModrinthSource(object sender, MouseButtonEventArgs e) => await SwitchDetailSourceAsync(ModSource.Modrinth);
    private async void OnPickCurseForgeSource(object sender, MouseButtonEventArgs e) => await SwitchDetailSourceAsync(ModSource.CurseForge);

    private async Task SwitchDetailSourceAsync(ModSource store)
    {
        var target = store == ModSource.Modrinth ? _detailModrinth : _detailCurseForge;
        if (target is null || _currentMod?.Source == store) return;
        // Keep the known counterpart pair; just reload the detail/versions for the chosen store.
        await LoadModDetailAsync(target, resolveCounterpart: false);
        UpdateSourceToggle();
    }

    private void ShowVersions(bool all)
    {
        var mc = PackMinecraftVersion();
        var loader = PackLoaderTag();
        var filtered = all ? _allVersions : _allVersions.Where(v => MatchesFilters(v, mc, loader)).ToList();
        VersionFilterNote.Text = all ? $"(all {_allVersions.Count})" : $"(filtered {filtered.Count}/{_allVersions.Count})";
        var isDownloaded = _currentMod is not null && IsModInstalled(_currentMod);
        VersionsGrid.ItemsSource = filtered
            .Select(v => new VersionRow(v, isDownloaded, MatchesFilters(v, mc, loader), PackVersionLabel()))
            .ToList();
    }

    private async void OnShowAllVersionsChanged(object s, RoutedEventArgs e)
    {
        var showAll = ShowAllVersionsBox.IsChecked == true;
        // The list is fetched filtered to the pack; "Show all" needs the rest, fetched once on demand.
        if (showAll && !_versionsUnfiltered && _currentMod is { } mod)
        {
            VersionFilterNote.Text = "Loading every version...";
            try
            {
                var all = await LoadVersionsForModAsync(mod, applyFilters: false, _cts.Token);
                if (!ReferenceEquals(_currentMod, mod)) return;
                _allVersions = all;
                _versionsUnfiltered = true;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { VersionFilterNote.Text = "Couldn't load every version: " + ex.Message; return; }
        }
        ShowVersions(showAll);
    }

    // Stops a double-click from starting a second dependency resolution behind the first.
    private bool _installing;

    /// <summary>Runs one install at a time, and marks the control that started it as busy while it
    /// runs.</summary>
    /// <remarks>The progress bar is in the detail column, so a row's button needs its own busy state.
    /// The row's <c>IsEnabled</c> and tooltip are bound to <c>ModResultRow</c>, so other properties are
    /// used and cleared afterwards: a local Opacity would override the style's <c>IsEnabled</c>
    /// trigger.</remarks>
    private async Task RunInstallAsync(Func<Task> install, ContentControl? pressed = null)
    {
        if (_installing) return;
        _installing = true;
        var content = pressed?.Content;
        if (pressed is not null)
        {
            // An icon button shows "..." while busy; a labelled button is only dimmed.
            if (content is string { Length: 1 }) pressed.Content = "...";
            pressed.IsHitTestVisible = false;
            pressed.Opacity = 0.6;
        }
        try { await install(); }
        finally
        {
            _installing = false;
            if (pressed is not null)
            {
                pressed.Content = content;
                pressed.ClearValue(IsHitTestVisibleProperty);
                pressed.ClearValue(OpacityProperty);
            }
        }
    }

    private async void OnQuickDownloadMod(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.DataContext is not ModResultRow row) return;
        if (row.IsDownloaded)
        {
            DownloadStatus.Text = $"{row.Name} is already downloaded in this pack.";
            return;
        }
        var mod = row.Source;
        await RunInstallAsync(() => QuickDownloadAsync(mod), el as ContentControl);
    }

    /// <summary>Right-click on a search result: the three download modes and the links.</summary>
    /// <remarks>Downloads are disabled when the mod is already in the pack;
    /// the tooltip says why.</remarks>
    private void OnResultRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement el || el.DataContext is not ModResultRow row) return;
        e.Handled = true;
        ResultsList.SelectedItem = row;   // right-clicking outside the selection selects that row

        var mod = row.Source;
        var canDownload = !row.IsDownloaded;
        var menu = new ContextMenu { PlacementTarget = el };

        MenuItem Item(string header, Action onClick, bool enabled = true, string? tip = null)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            if (tip is not null) { mi.ToolTip = tip; ToolTipService.SetShowOnDisabled(mi, true); }
            mi.Click += (_, _) => onClick();
            menu.Items.Add(mi);
            return mi;
        }

        var reason = canDownload ? null : row.DownloadTooltip;
        Item("Download", () => _ = RunInstallAsync(() => QuickDownloadAsync(mod)), canDownload, reason);
        Item("Download as test", () => _ = RunInstallAsync(() => QuickDownloadWithFlagsAsync(mod, m => m.IsTesting = true)), canDownload, reason);
        Item("Download with options...", () => _ = RunInstallAsync(() => CustomDownloadAsync(mod)), canDownload, reason);

        menu.Items.Add(new Separator());
        Item("Open website", () => OpenModWebsite(mod), ModWebsiteUrl(mod) is not null);
        Item("Copy link", () => ClipboardHelper.TrySetText(ModWebsiteUrl(mod)), ModWebsiteUrl(mod) is not null);
        Item("Copy name", () => ClipboardHelper.TrySetText(mod.Name));
        menu.IsOpen = true;
    }

    /// <summary>The mod's page on the store it came from, or null for a store with no known URL
    /// pattern.</summary>
    private static string? ModWebsiteUrl(ModSummary mod) => mod.Source switch
    {
        ModSource.Modrinth => $"https://modrinth.com/mod/{mod.Slug}",
        ModSource.CurseForge => $"https://www.curseforge.com/minecraft/mc-mods/{mod.Slug}",
        _ => null
    };

    private void OpenModWebsite(ModSummary mod)
    {
        if (ModWebsiteUrl(mod) is not { } url) return;
        if (!SafeLaunch.OpenUrl(url)) DownloadStatus.Text = "Could not open that page.";
    }

    /// <summary>The "Custom" download for a mod, shared by the detail pane's button and the result
    /// row's context menu.</summary>
    private async Task CustomDownloadAsync(ModSummary mod)
    {
        if (Window.GetWindow(this) is not MainWindow host) return;
        var options = await ModDownloadOptionsDialog.ShowAsync(host, mod.Name, _pack.Id);
        if (options is not null) await QuickDownloadWithFlagsAsync(mod, options.ApplyTo);
    }

    private async void OnDownloadVersion(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.DataContext is not VersionRow row) return;
        await DownloadVersionRowAsync(row, el as ContentControl);
    }

    /// <summary>Downloads this specific version into the pack (the row's Download button and its menu
    /// item).</summary>
    private async Task DownloadVersionRowAsync(VersionRow row, ContentControl? pressed)
    {
        if (_currentMod is not { } mod) return;
        if (row.IsDownloaded)
        {
            DownloadStatus.Text = $"{mod.Name} is already downloaded in this pack.";
            return;
        }
        if (!row.IsCompatible)
        {
            DownloadStatus.Text = $"{row.VersionNumber} does not support {PackVersionLabel()}.";
            return;
        }

        await RunInstallAsync(() => DownloadVersionAsync(mod, row.Source), pressed);
    }

    // ── versions tab: row menu and changelog ─────────────────────────────────

    private void OnVersionsGridPreviewRightDown(object sender, MouseButtonEventArgs e) =>
        VersionRowMenu.SelectRowUnder(VersionsGrid, e);

    /// <summary>Right-click on a version: its changelog, a download of that specific version, and
    /// its number for the clipboard.</summary>
    private void OnVersionsGridRightClick(object sender, MouseButtonEventArgs e)
    {
        if (VersionRowMenu.RowAt<VersionRow>(e) is not { } row) return;
        e.Handled = true;
        VersionRowMenu.Open(BuildVersionMenu(row));
    }

    /// <remarks>Hosted mods get no download item, since their row's Download button can't fetch from
    /// this page either.</remarks>
    private ContextMenu BuildVersionMenu(VersionRow row)
    {
        var menu = VersionRowMenu.Create(VersionsGrid);
        VersionRowMenu.AddChangelog(menu, () => _ = ShowChangelogAsync(row));
        if (_currentMod is not null)
            VersionRowMenu.Add(menu, "Download this version", VersionRowMenu.DownloadGlyph,
                () => _ = DownloadVersionRowAsync(row, null),
                row.CanDownload, row.CanDownload ? null : row.DownloadTooltip);
        menu.Items.Add(new Separator());
        VersionRowMenu.AddCopyVersion(menu, row.VersionNumber, copied => DownloadStatus.Text = copied
            ? $"Copied {row.VersionNumber}."
            : "The clipboard is in use by another program.");
        return menu;
    }

    /// <summary>Opens the changelog card on one version, able to step through the rest of the list
    /// in the order it is on screen.</summary>
    private async Task ShowChangelogAsync(VersionRow row)
    {
        try
        {
            var (versions, index) = VersionChangelogCard.FromList(VersionsGrid.Items, row, r => r.Source);
            var name = _currentMod?.Name ?? ModNameLabel.Text;
            await VersionChangelogCard.ShowAsync(this, name, versions, index, _currentMod, _shell);
        }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(ModExplorerPage), ex);
            DownloadStatus.Text = "Could not open the changelog.";
        }
    }

    private async Task QuickDownloadAsync(ModSummary mod)
    {
        if (IsModInstalled(mod))
        {
            DownloadStatus.Text = $"{mod.Name} is already downloaded in this pack.";
            RefreshDownloadedState();
            return;
        }

        DownloadStatus.Text = $"Finding latest compatible file for {mod.Name}...";
        try
        {
            if (!CanInstallModsToPack())
            {
                DownloadStatus.Text = "Pick a non-empty Fabric, Forge, or NeoForge profile before installing mods.";
                return;
            }

            var versions = await LoadVersionsForModAsync(mod, applyFilters: true, CancellationToken.None);
            var version = PickBestVersion(versions);
            if (version is null)
            {
                DownloadStatus.Text = $"No compatible versions found for {PackVersionLabel()}.";
                return;
            }

            await DownloadVersionAsync(mod, version);
        }
        catch (Exception ex)
        {
            DownloadStatus.Text = "Download failed: " + ex.Message;
        }
    }

    private async Task DownloadVersionAsync(ModSummary mod, ModVersion version, Action<ModMeta>? applyToRoot = null)
    {
        if (IsModInstalled(mod))
        {
            DownloadStatus.Text = $"{mod.Name} is already downloaded in this pack.";
            RefreshDownloadedState();
            return;
        }

        if (!CanInstallModsToPack())
        {
            DownloadStatus.Text = "Pick a non-empty Fabric, Forge, or NeoForge profile before installing mods.";
            return;
        }

        if (!MatchesFilters(version, PackMinecraftVersion(), PackLoaderTag()))
        {
            DownloadStatus.Text = $"{version.VersionNumber} does not support {PackVersionLabel()}.";
            return;
        }

        var target = (DownloadTargetBox.SelectedItem as ComboBoxItem)?.Content as string ?? "game/mods/";
        var folder = target switch
        {
            "local/mods/" => Path.Combine(App.State.Packs.LocalDir(_pack.Id), "mods"),
            _             => Path.Combine(App.State.Packs.GameDir(_pack.Id), "mods")
        };
        Directory.CreateDirectory(folder);

        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.IsIndeterminate = true;
        DownloadProgress.Value = 0;
        DownloadStatus.Text = $"Resolving dependencies for {mod.Name}...";
        try
        {
            var progress = new Progress<(long done, long total)>(p =>
            {
                if (p.total > 0)
                {
                    DownloadProgress.IsIndeterminate = false;
                    DownloadProgress.Value = (double)p.done / p.total * 100;
                }
            });

            var downloads = await ModDependencyResolver.ResolveRequiredDownloadsAsync(
                mod,
                version,
                PackMinecraftVersion(),
                PackLoaderTag(),
                App.State.Modrinth,
                App.State.CurseForge,
                PackChannel(),
                App.State.ModVersions);
            if (downloads.Count == 0)
            {
                DownloadStatus.Text = "No downloadable file found.";
                return;
            }

            // "Auto-download and enable required dependencies" (Mods Management -> Advanced). When off,
            // fetch only the requested mod. It's a per-pack setting shared with collaborators; enabling
            // a mod in the hub reads it too.
            var autoDeps = App.State.ModMetadata.Advanced(_pack.Id).AutoDownloadDependencies;

            // Work out what needs fetching first, so the progress count covers real downloads only.
            var pending = new List<ModDownloadItem>();
            var refused = new List<string>();
            var skipped = 0;
            foreach (var item in downloads)
            {
                if (item.IsDependency && !autoDeps) { skipped++; continue; }
                // The store supplies the file name; anything but a plain name could escape the folder.
                if (PathSafety.ResolveFileName(folder, item.File.Filename) is not { } dest)
                {
                    AppLog.Log(nameof(ModExplorerPage), $"Skipped {item.Mod.Name}: the store's file name is not a plain file name: {item.File.Filename}");
                    refused.Add($"{item.Mod.Name} (the store gave an unusable file name)");
                    continue;
                }
                if (item.IsDependency && (File.Exists(dest) || await IsDependencyAlreadyInstalledAsync(item.Mod)))
                {
                    skipped++;
                    continue;
                }
                pending.Add(item);
            }

            // A mod plus its dependencies is a handful of small files, each mostly latency. Fetch
            // them together, up to the user's download concurrency (Settings -> Downloads).
            var saved = 0;
            var installedPaths = new List<string>();
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

                var failures = new List<string>(refused);
                await Task.WhenAll(pending.Select(async item =>
                {
                    // Checked when pending was built, so this always resolves.
                    var dest = PathSafety.ResolveFileName(folder, item.File.Filename)!;
                    await gate.WaitAsync();
                    try
                    {
                        // One file at a time still gets a real percentage; several share the count.
                        await App.State.Modrinth.DownloadFileAsync(
                            item.File.DownloadUrl, dest, pending.Count == 1 ? progress : null);
                        await RememberInstalledModAsync(item, dest);
                        lock (installedPaths) installedPaths.Add(dest);

                        // The root mod records the store it came from, so a jar listed on both
                        // stores keeps this store's identity (label, page, updates), plus any
                        // requested download-mode flags.
                        if (!item.IsDependency)
                        {
                            var store = item.Mod.Source is ModSource.CurseForge or ModSource.Modrinth ? item.Mod.Source : (ModSource?)null;
                            if (store is not null || applyToRoot is not null)
                                SetDownloadedMetaFlag(item.Mod, item.File.Filename, m =>
                                {
                                    if (store is not null) m.PreferredSource = store;
                                    applyToRoot?.Invoke(m);
                                }, save: false);
                        }
                        else
                        {
                            // A jar pulled in as a dependency is a library mod; flag it so the Advanced
                            // tab's disable cascade can clean it up. Only new, untouched metadata is
                            // flagged, never a mod the user classified.
                            SetDownloadedMetaFlag(item.Mod, item.File.Filename, m =>
                            {
                                if (m.IsDefault) m.IsLibrary = true;
                            }, save: false);
                        }
                        Interlocked.Increment(ref saved);
                    }
                    catch (Exception ex) { lock (failures) failures.Add($"{item.File.Filename} ({ex.Message})"); }
                    finally
                    {
                        gate.Release();
                        Interlocked.Increment(ref completed);
                        ReportBatch(item.File.Filename);
                    }
                }));

                // One save for the whole batch, before the throw, so jars that did download keep
                // their flags when a sibling fails.
                if (saved > 0) App.State.ModMetadata.Save(_pack.Id);

                if (failures.Count > 0)
                    throw new InvalidOperationException(string.Join("; ", failures));
            }

            DownloadProgress.Value = 100;
            var skippedText = skipped > 0 ? $" ({skipped} already present)" : "";
            DownloadStatus.Text = saved == 1 && downloads.Count == 1
                ? $"Saved to {target}{downloads[0].File.Filename}"
                : $"Saved {saved} file(s) to {target}{skippedText}";
            App.State.ModFingerprints.Flush();
            RefreshDownloadedState();
            // Resolve the new jars on the other store too, so they stop showing as downloadable there.
            // Only the new jars, not the whole instance.
            _ = RefreshInstalledModIndexAsync(installedPaths);
        }
        catch (Exception ex) { DownloadStatus.Text = "Download failed: " + ex.Message; }
        finally
        {
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Value = 0;
            DownloadProgress.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>True if a required dependency is already in the pack, by its own identity or via its
    /// cross-store counterpart. Prevents re-downloading e.g. Create from Modrinth when the CurseForge
    /// copy (or a different version's jar) is already installed.</summary>
    private async Task<bool> IsDependencyAlreadyInstalledAsync(ModSummary depMod)
    {
        if (IsModInstalled(depMod)) return true;
        try
        {
            var otherStore = depMod.Source == ModSource.Modrinth ? ModSource.CurseForge : ModSource.Modrinth;
            var counterpartKey = await ResolveCounterpartKeyAsync(otherStore, depMod);
            return counterpartKey is not null && _installedModKeys.Contains(counterpartKey);
        }
        catch { return false; }
    }

    // ── colored download modes (grey = testing, orange = custom) ────────────────

    private async void OnTestDownload(object sender, RoutedEventArgs e)
    {
        if (_currentMod is not { } mod) { DownloadStatus.Text = "Select a mod first."; return; }
        await RunInstallAsync(() => QuickDownloadWithFlagsAsync(mod, m => m.IsTesting = true),
                              sender as ContentControl);
    }

    private async void OnCustomDownload(object sender, RoutedEventArgs e)
    {
        if (_currentMod is not { } mod) { DownloadStatus.Text = "Select a mod first."; return; }
        try { await RunInstallAsync(() => CustomDownloadAsync(mod), sender as ContentControl); }
        catch (Exception ex) { DownloadStatus.Text = "Download failed: " + ex.Message; }
    }

    private async Task QuickDownloadWithFlagsAsync(ModSummary mod, Action<ModMeta> apply)
    {
        if (IsModInstalled(mod))
        {
            DownloadStatus.Text = $"{mod.Name} is already downloaded in this pack.";
            RefreshDownloadedState();
            return;
        }
        if (!CanInstallModsToPack())
        {
            DownloadStatus.Text = "Pick a non-empty Fabric, Forge, or NeoForge profile before installing mods.";
            return;
        }
        DownloadStatus.Text = $"Finding latest compatible file for {mod.Name}...";
        try
        {
            var versions = await LoadVersionsForModAsync(mod, applyFilters: true, CancellationToken.None);
            var version = PickBestVersion(versions);
            if (version is null)
            {
                DownloadStatus.Text = $"No compatible versions found for {PackVersionLabel()}.";
                return;
            }
            await DownloadVersionAsync(mod, version, apply);
        }
        catch (Exception ex) { DownloadStatus.Text = "Download failed: " + ex.Message; }
    }

    /// <summary>Applies and persists a flag onto a just-downloaded jar's saved metadata, keyed by its
    /// source project id (so it survives version updates) with a file-name fallback.</summary>
    /// <param name="save">False inside a batch, which saves once at the end; <c>SetMeta</c> rewrites
    /// the whole mods.json each time.</param>
    private void SetDownloadedMetaFlag(ModSummary mod, string filename, Action<ModMeta> apply,
                                       bool save = true)
    {
        var keys = ModMetadataService.CandidateKeys(
            mod.Source == ModSource.Modrinth ? mod : null,
            mod.Source == ModSource.CurseForge ? mod : null,
            filename);
        var meta = App.State.ModMetadata.GetMeta(_pack.Id, keys);
        apply(meta);
        if (save) App.State.ModMetadata.SetMeta(_pack.Id, keys, meta);
        else App.State.ModMetadata.StoreMeta(_pack.Id, keys, meta);
    }

    private ModResultRow CreateResultRow(ModSummary mod) =>
        new(mod, IsModInstalled(mod));

    private bool IsModInstalled(ModSummary mod) =>
        _installedModKeys.Contains(ModKey(mod));

    private void RefreshDownloadedState()
    {
        // ModResultRow notifies on IsDownloaded, so only the affected row repaints. Items.Refresh()
        // would re-create every container and lose the scroll position.
        foreach (var row in _rows)
            row.IsDownloaded = IsModInstalled(row.Source);

        if (_currentMod is not null && _allVersions.Count > 0)
            ShowVersions(ShowAllVersionsBox.IsChecked == true);
    }

    // Cheap, no-IO pass: badge mods we've already identified (single store each).
    private async Task SeedInstalledFromCacheAsync()
    {
        try
        {
            var keys = await Task.Run(() =>
            {
                var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in EnumeratePackModFiles())
                {
                    if (App.State.ModFingerprints.TryGetCachedMatch(path, out var mod, out _))
                        installed.Add(ModKey(mod));
                }
                return installed;
            });

            _installedModKeys.Clear();
            foreach (var key in keys)
                _installedModKeys.Add(key);
        }
        catch
        {
            _installedModKeys.Clear();
        }
    }

    // Resolves every installed jar to both its Modrinth and CurseForge identity, so a mod from one
    // store is recognised while browsing the other. Hash-based, runs in the background.
    // onlyPaths narrows the pass to newly installed jars: InstalledModResolver stats each path on the
    // dispatcher, and an instance can hold hundreds of jars. A narrowed pass never clears the key set.
    private async Task RefreshInstalledModIndexAsync(IReadOnlyCollection<string>? onlyPaths = null)
    {
        var generation = ++_indexGeneration;
        try
        {
            if (onlyPaths is { Count: 0 }) return;   // every download failed; nothing to resolve
            var jarPaths = onlyPaths ?? await Task.Run(() => EnumeratePackModFiles().ToList());
            var index = await InstalledModResolver.ResolveAsync(
                jarPaths,
                App.State.ModFingerprints,
                App.State.Modrinth,
                App.State.CurseForge);

            if (generation != _indexGeneration) return;

            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var identity in index.Identities)
            {
                if (identity.Modrinth is { } modrinth)
                    keys.Add(ModKey(modrinth.Mod));
                if (identity.CurseForge is { } curseForge)
                    keys.Add(ModKey(curseForge.Mod));
                // Hosted/external jars that fingerprinting can't place still keep their
                // previously cached identity so they don't flip back to downloadable.
                if (identity.Modrinth is null && identity.CurseForge is null
                    && App.State.ModFingerprints.TryGetCachedMatch(identity.Path, out var cached, out _))
                    keys.Add(ModKey(cached));
            }

            // Only replace the set when both stores answered; otherwise merge, so a failed
            // lookup can't make installed mods look downloadable again. A deleted mod
            // stays badged until a clean pass.
            if (index.Complete && onlyPaths is null)
                _installedModKeys.Clear();
            foreach (var key in keys)
                _installedModKeys.Add(key);

            RefreshDownloadedState();

            // Hashes only link the stores when both host the identical jar. For jars found
            // on one store only, look up the other store's project by slug/name. Runs after
            // the exact-match badges are shown.
            await AddCrossStoreCounterpartKeysAsync(index.Identities, generation);
        }
        catch
        {
            // Keep whatever we already recognised; a transient failure shouldn't make
            // installed mods look downloadable again.
        }
    }

    // Memoises cross-store counterpart lookups (target store + source mod key -> counterpart mod key,
    // or null if none) so re-running the index after each download doesn't re-query the same mods.
    private readonly Dictionary<string, string?> _counterpartKeyCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many counterparts one pass asks the stores about. Each miss costs up to two
    /// searches, so a big pack opened for the first time would otherwise fire hundreds of requests
    /// in the background; the rest are looked up on later opens, and the on-disk cache keeps every
    /// answer for days, so the set fills in over a few visits.</summary>
    private const int CounterpartLookupsPerPass = 40;

    private async Task AddCrossStoreCounterpartKeysAsync(
        IReadOnlyList<InstalledModIdentity> identities, int generation)
    {
        var added = false;
        var lookups = 0;
        var deferred = 0;
        foreach (var identity in identities)
        {
            if (generation != _indexGeneration) return;

            var source = identity switch
            {
                { Modrinth: null, CurseForge: { } cf } => (Target: ModSource.Modrinth, Mod: cf.Mod),
                { CurseForge: null, Modrinth: { } mr } => (Target: ModSource.CurseForge, Mod: mr.Mod),
                _ => (Target: ModSource.External, Mod: (ModSummary?)null)
            };
            if (source.Mod is null) continue;

            if (!HasCounterpartAnswer(source.Target, source.Mod))
            {
                if (lookups >= CounterpartLookupsPerPass) { deferred++; continue; }
                lookups++;
            }
            var counterpartKey = await ResolveCounterpartKeyAsync(source.Target, source.Mod);

            if (generation != _indexGeneration) return;
            if (counterpartKey is not null && _installedModKeys.Add(counterpartKey))
                added = true;
        }

        App.State.ModCounterparts.Flush();
        if (deferred > 0)
            AppLog.Log(nameof(ModExplorerPage), $"Looked up {lookups} cross-store counterpart(s); {deferred} left for a later visit.");
        if (added && generation == _indexGeneration)
            RefreshDownloadedState();
    }

    /// <summary>True when the counterpart is already known here or on disk, so asking costs nothing.</summary>
    private bool HasCounterpartAnswer(ModSource targetStore, ModSummary sourceMod) =>
        _counterpartKeyCache.ContainsKey($"{targetStore}:{ModKey(sourceMod)}")
        || App.State.ModCounterparts.TryGet(targetStore, sourceMod, out _);

    private async Task<string?> ResolveCounterpartKeyAsync(ModSource targetStore, ModSummary sourceMod)
    {
        var memoKey = $"{targetStore}:{ModKey(sourceMod)}";
        if (_counterpartKeyCache.TryGetValue(memoKey, out var cached))
            return cached;

        // Check the on-disk cache first: counterparts rarely change, and re-asking the stores for every
        // installed mod on each page open gets the launcher server rate-limited.
        if (App.State.ModCounterparts.TryGet(targetStore, sourceMod, out var remembered))
        {
            var rememberedKey = remembered is not null ? ModKey(remembered) : null;
            _counterpartKeyCache[memoKey] = rememberedKey;
            return rememberedKey;
        }

        try
        {
            var counterpart = targetStore == ModSource.Modrinth
                ? await App.State.Modrinth.FindCounterpartAsync(sourceMod.Slug, sourceMod.Name)
                : await App.State.CurseForge.FindCounterpartAsync(sourceMod.Slug, sourceMod.Name);
            var key = counterpart is not null ? ModKey(counterpart) : null;
            _counterpartKeyCache[memoKey] = key; // definitive answer (incl. "none"), don't re-query
            App.State.ModCounterparts.Remember(targetStore, sourceMod, counterpart);
            return key;
        }
        catch
        {
            // Offline or API error: don't memoise, so a later refresh can retry.
            return null;
        }
    }

    private IEnumerable<string> EnumeratePackModFiles()
    {
        var dirs = new[]
        {
            Path.Combine(App.State.Packs.GameDir(_pack.Id), "mods"),
            Path.Combine(App.State.Packs.LocalDir(_pack.Id), "mods")
        }.Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly).ToList();
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                if (IsModFile(file))
                    yield return file;
            }
        }
    }

    /// <summary>Records the jar that just landed: its key for the badges, and its two provider
    /// hashes for the fingerprint cache.</summary>
    /// <remarks>Hashed off the dispatcher: SHA-512 plus a whitespace-stripped Murmur2 over a big jar is
    /// a visible freeze.</remarks>
    private async Task RememberInstalledModAsync(ModDownloadItem item, string path)
    {
        _installedModKeys.Add(ModKey(item.Mod));
        try
        {
            var (sha512, curseForgeFingerprint) = await Task.Run(() => ModFingerprintCache.ComputeHashes(path));
            App.State.ModFingerprints.Store(path, sha512, curseForgeFingerprint, new CachedModMatch
            {
                Mod = item.Mod,
                Version = item.Version
            });
        }
        catch
        {
            // The UI state can still update even if the cache cannot read a locked file.
        }
    }

    private static bool IsModFile(string path) =>
        path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase);

    private static string ModKey(ModSummary mod) => $"{mod.Source}:{mod.Id}";

    /// <summary>The mod's versions from the shared catalog (cached per mod for 15 minutes). With
    /// <paramref name="applyFilters"/>, only those for this pack's Minecraft version and loader,
    /// filtered by the store; otherwise every version.</summary>
    private async Task<List<ModVersion>> LoadVersionsForModAsync(ModSummary mod, bool applyFilters, CancellationToken ct)
    {
        if (!applyFilters)
            return (await App.State.ModVersions.GetVersionsAsync(mod, ct: ct)).ToList();
        var mc = PackMinecraftVersion();
        var loader = PackLoaderTag();
        return (await App.State.ModVersions.GetVersionsAsync(mod,
            mc.Length > 0 ? mc : null, loader.Length > 0 ? loader : null, ct: ct)).ToList();
    }

    /// <summary>True when <see cref="_allVersions"/> also holds incompatible versions.</summary>
    private bool _versionsUnfiltered;

    private async Task<ModVersionFile?> ResolveDownloadFileAsync(ModSummary mod, ModVersion version)
    {
        var file = version.Files.FirstOrDefault(f => f.IsPrimary) ?? version.Files.FirstOrDefault();
        if (file is null || !string.IsNullOrWhiteSpace(file.DownloadUrl)) return file;

        if (version.Source != ModSource.CurseForge) return file;

        var ids = version.Id.Split(':', 2);
        var modId = ids.Length == 2 && int.TryParse(ids[0], out var parsedModId)
            ? parsedModId
            : int.TryParse(mod.Id, out var fallbackModId) ? fallbackModId : 0;
        var fileId = ids.Length == 2 && int.TryParse(ids[1], out var parsedFileId) ? parsedFileId : 0;
        if (modId == 0 || fileId == 0) return file;

        var url = await App.State.CurseForge.GetDownloadUrlAsync(modId, fileId);
        return string.IsNullOrWhiteSpace(url) ? file : file with { DownloadUrl = url };
    }

    private ModVersion? PickBestVersion(IReadOnlyList<ModVersion> versions)
    {
        if (!CanInstallModsToPack()) return null;

        var mc = PackMinecraftVersion();
        var loader = PackLoaderTag();
        var compatible = versions.Where(v => MatchesFilters(v, mc, loader));
        return ModUpdateChannel.PickNewest(compatible, PackChannel(), v => v.ReleaseChannel, v => v.DatePublished);
    }

    /// <summary>The release channel this pack's downloads follow (pack setting, else the launcher
    /// default in Settings -> Mods).</summary>
    private string PackChannel() => App.State.ModMetadata.EffectiveUpdateChannel(_pack.Id);

    private static bool MatchesFilters(ModVersion version, string mc, string loader) =>
        (mc.Length == 0 || version.GameVersions.Contains(mc, StringComparer.OrdinalIgnoreCase)) &&
        (loader.Length == 0 || version.Loaders.Contains(loader, StringComparer.OrdinalIgnoreCase));

    private string PackMinecraftVersion() => _pack.MinecraftVersion?.Trim() ?? "";

    private string PackLoaderTag() =>
        _pack.Loader == LoaderKind.None ? "" : _pack.Loader.ToString().ToLowerInvariant();

    private bool CanInstallModsToPack() =>
        !_pack.IsEmpty
        && !string.IsNullOrWhiteSpace(_pack.MinecraftVersion)
        && _pack.Loader != LoaderKind.None;

    private string PackVersionLabel()
    {
        var mc = string.IsNullOrWhiteSpace(_pack.MinecraftVersion) ? "(no MC version)" : $"MC {_pack.MinecraftVersion}";
        return _pack.Loader == LoaderKind.None ? mc : $"{mc} · {_pack.Loader}";
    }

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

        IssuesEmptyText.Visibility =
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
        if (sender is not Button { Tag: string url } || string.IsNullOrWhiteSpace(url)) return;
        OpenUrl(url);
    }

    private void OnOpenScreenshot(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MediaRow row }) return;

        e.Handled = true;
        ScreenshotPreviewWindow.ShowFor(Window.GetWindow(this), row.FullImageUrl, row.Title);
    }

    /// <summary>Links come from store metadata, so only http and https ones are opened.</summary>
    private void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        if (!SafeLaunch.OpenUrl(url)) DownloadStatus.Text = "That link could not be opened.";
    }

    /// <summary>The detail pane's icon, via the shared <see cref="IconLoader"/> cache.</summary>
    /// <remarks>IconLoader caps the decode size, shares downloads with the list and drops late arrivals
    /// when the selection moves. It owns this element's Source; nothing else should set it.</remarks>
    private void SetSelectedIcon(string? iconUrl)
    {
        IconLoader.SetDecodeWidth(SelectedIconImage, 52);
        IconLoader.SetUrl(SelectedIconImage, string.IsNullOrWhiteSpace(iconUrl) ? null : iconUrl);
    }

    private void ClearSelectedMod()
    {
        _currentMod = null;
        _allVersions.Clear();
        _versionsUnfiltered = false;
        DetailHeader.Visibility = Visibility.Collapsed;
        DetailPlaceholder.Visibility = Visibility.Visible;
        ModTabs.Visibility = Visibility.Collapsed;
        ModNameLabel.Text = "";
        ModMetaLabel.Text = "";
        SelectedIconFallback.Text = "";
        SetSelectedIcon(null);
        VersionsGrid.ItemsSource = null;
        VersionFilterNote.Text = "";
    }

    private static string FormatNumber(long n) =>
        n >= 1_000_000 ? $"{n / 1_000_000.0:F1}M"
      : n >= 1_000     ? $"{n / 1000.0:F1}K"
      : n.ToString();

    private static string InitialFor(string name) =>
        string.IsNullOrWhiteSpace(name) ? "?" : name.Trim()[0].ToString().ToUpperInvariant();

    private static string BuildProjectUrl(ModSummary mod)
    {
        var slug = string.IsNullOrWhiteSpace(mod.Slug) ? mod.Id : mod.Slug;
        return mod.Source == ModSource.CurseForge
            ? $"https://www.curseforge.com/minecraft/mc-mods/{slug}"
            : $"https://modrinth.com/mod/{slug}";
    }

    private static string FormatOverview(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "(no overview)";

        var text = html.Replace("\r\n", "\n").Replace('\r', '\n');
        text = Regex.Replace(text, @"(?i)<\s*br\s*/?\s*>", "\n");
        text = Regex.Replace(text, @"(?i)</\s*(p|div|h[1-6]|li|ul|ol|blockquote|section|article)\s*>", "\n\n");
        text = Regex.Replace(text, @"(?i)<\s*li[^>]*>", "- ");
        text = Regex.Replace(text, "<[^>]+>", "");
        text = WebUtility.HtmlDecode(text).Replace('\u00a0', ' ');
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @" *\n *", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();

        return string.IsNullOrWhiteSpace(text) ? "(no overview)" : text;
    }
}

public enum ModBrowseSourceKind
{
    All,

    /// <summary>What is already in an instance's mods/ folder, read from disk and the identity caches,
    /// so it works offline. Only <see cref="ModsView"/> offers it; the explorer page is already scoped
    /// to one instance.</summary>
    Installed,

    CurseForge,
    Modrinth,
    Divider,
    CloudLauncherPersonal,
    CloudLauncherShared,
    CloudLauncherPublic,
    CloudLauncherTeam
}

public class ModSourceChipRow
{
    public string Icon { get; set; } = "";
    public string Label { get; set; } = "";
    public ModBrowseSourceKind Kind { get; set; }
    public Guid? TeamId { get; set; }
    public bool IsDivider { get; set; }
    public Brush Background { get; set; } = Brushes.Transparent;
    public Brush BorderColor { get; set; } = Brushes.Transparent;
    public Brush Foreground { get; set; } = Brushes.Black;
    public FontWeight FontWeight { get; set; } = FontWeights.Normal;
    public Visibility ChipVisibility { get; set; } = Visibility.Visible;
    public Visibility IconVisibility { get; set; } = Visibility.Visible;
    public Cursor CursorHint { get; set; } = Cursors.Hand;
}
