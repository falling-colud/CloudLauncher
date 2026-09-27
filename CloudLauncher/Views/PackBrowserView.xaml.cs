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

public partial class PackBrowserView : Page
{
    private readonly MainWindow _shell;
    private readonly ObservableCollection<SourceChipRow> _chips = new();
    private readonly ObservableCollection<PackBrowseRow> _rows = new();
    private SourceChipRow? _activeChip;
    private PackBrowseRow? _currentRow;
    private ModSummary? _currentExternal;
    private PackSummary? _currentInternal;
    private readonly HashSet<Guid> _addedPackIds = new();

    /// <summary>The hosted packs already in the list, so no page can add one a second time.</summary>
    /// <remarks>Duplicates come from your own public packs (pinned at the top, then returned again
    /// further down) and from packs updated while you scroll, which shift the page boundaries.</remarks>
    private readonly HashSet<Guid> _listedPackIds = new();
    private List<ModVersion> _allVersions = new();
    private string? _currentProjectUrl;
    private string? _cachedDescription;
    private bool _descriptionIsMarkdown;

    private string _searchText = "";
    private string? _filterMcVersion;
    private string? _filterLoader;

    private CancellationTokenSource _cts = new();
    /// <summary>Owns the in-flight page task, the throttle and the re-entrancy guard, so a reset
    /// waits for the load it just cancelled rather than racing it.</summary>
    private readonly InfiniteScroll.Pager _pager;
    /// <summary>Searching / no results / failed / offline. Nothing else writes the count slot.</summary>
    private readonly PageState _state;
    private bool _isLoading;
    private bool _hasMore;
    private int _offset;
    private const int PageSize = 50;   // both stores cap a page at 50
    /// <summary>A safety stop so a runaway pager can't fill memory, not a browsing limit.</summary>
    private const int MaxResults = 2000;

    public PackBrowserView(MainWindow shell)
    {
        InitializeComponent();
        _shell = shell;
        SourceStrip.ItemsSource = _chips;
        ResultsList.ItemsSource = _rows;
        PackTabs.SelectionChanged += OnPackTabChanged;
        App.State.ModpackDownload.PackAdded += OnPackAdded;

        _pager = new InfiniteScroll.Pager(
            () => !_isLoading && _hasMore && _activeChip is not null && _rows.Count < MaxResults,
            () => _rows.Count,
            LoadMoreAsync);

        _state = new PageState(ResultsList, PageStateHost, nameof(PackBrowserView))
            .Copy(PageCopy.Results)
            .Slots(CountLabel, StatusLabel);
        _state.RetryRequested += () => _ = RefreshEverythingAsync();

        // This searches the store, so act on the debounced event; Enter searches immediately.
        SearchBox.TextChangedDebounced += OnSearchSettled;
        SearchBox.SearchSubmitted += OnSearchSubmitted;

        Loaded += async (_, _) =>
        {
            // Chip and badge colours are painted in code (their state is per row), and ThemeService
            // creates new brushes on every Apply, so repaint when the theme changes.
            ThemeService.Changed += OnThemeChanged;
            if (Window.GetWindow(this) is { } w) w.PreviewKeyDown += OnShellKeyDown;
            // Search is the main job here, so the box starts open. It still collapses on blur when empty.
            SearchBox.Expand();
            try { await InitAsync(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _state.Error("This page could not be set up.", ex); }
        };
        Unloaded += (_, _) =>
        {
            App.State.ModpackDownload.PackAdded -= OnPackAdded;
            ThemeService.Changed -= OnThemeChanged;
            if (Window.GetWindow(this) is { } w) w.PreviewKeyDown -= OnShellKeyDown;
        };
    }

    /// <summary>Repaints everything this page colours from code so live theming works here too.</summary>
    private void OnThemeChanged()
    {
        ApplyChipStyles();
        foreach (var row in _rows.Where(r => r.Kind == PackBrowseRowKind.Internal))
        {
            row.SourceBadgeBackground = (Brush)FindResource("AccentSoftBrush");
            row.SourceBadgeForeground = (Brush)FindResource("AccentBrush");
        }
        ResultsList.Items.Refresh();
    }

    private void OnShellKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsVisible) return;
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            SearchBox.Focus();   // the control expands, focuses and selects
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            _ = RefreshEverythingAsync();
            e.Handled = true;
        }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshEverythingAsync();

    /// <summary>
    /// Re-runs the current chip's query, and re-reads which packs you already have, since every
    /// CloudLauncher row's Add/Open button depends on it.
    /// </summary>
    private async Task RefreshEverythingAsync()
    {
        RefreshButton.IsEnabled = false;
        try
        {
            await RefreshAddedPacksAsync();
            await ResetAndLoadAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _state.Error("Nothing answered.", ex, title: "Could not refresh");
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void OnPackTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != PackTabs) return;
        var onOverview = PackTabs.SelectedIndex == 0;
        OverviewHost.Visibility = onOverview ? Visibility.Visible : Visibility.Collapsed;
        OverviewBrowser.Visibility = onOverview ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task InitAsync()
    {
        // Show the loading state before the first request, so a dead server doesn't leave a blank pane.
        _state.Begin(refreshing: false);

        // This only decides Add vs Open, so a failure here must not stop the catalogs loading.
        try { await RefreshAddedPacksAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.LogError(nameof(PackBrowserView), ex); }

        await BuildChipsAsync();
        if (_activeChip is null && _chips.Count > 0)
            await SelectChipAsync(_chips[0]);
    }

    private async Task RefreshAddedPacksAsync()
    {
        try
        {
            var packs = await App.State.Api.ListPacksAsync(_cts.Token);
            _addedPackIds.Clear();
            foreach (var pack in packs)
                _addedPackIds.Add(pack.Id);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            _addedPackIds.Clear();
        }
    }

    private void OnPackAdded(PackSummary pack)
    {
        _addedPackIds.Add(pack.Id);
        RefreshInternalActionState(pack.Id);
    }

    private void RefreshInternalActionState(Guid packId)
    {
        foreach (var row in _rows.Where(r => r.Internal?.Id == packId))
            ApplyInternalActionState(row);

        ResultsList.Items.Refresh();

        if (_currentRow?.Internal?.Id == packId)
        {
            DownloadSelectedButton.Content = _currentRow.ActionLabel;
            DownloadSelectedButton.Visibility = _currentRow.ActionVisibility;
            AddedHint.Text = _currentRow.StateHint;
            if (VersionsGrid.ItemsSource is List<PackVersionRow> rows)
                VersionsGrid.ItemsSource = rows.Select(v => v with { ActionVisibility = Visibility.Collapsed }).ToList();
        }
    }

    /// <summary>
    /// Builds the source strip like the Mods and Resource pack browsers: third-party catalogs, a
    /// divider, then this server's sources, starting with your own packs.
    /// </summary>
    private async Task BuildChipsAsync()
    {
        _chips.Clear();
        _chips.Add(NewChip("", "CurseForge", SourceKind.CurseForge));
        _chips.Add(NewChip("", "Modrinth", SourceKind.Modrinth));

        // This server's packs are only for accounts: signed out, the stores are the catalog.
        if (!App.State.Api.IsSignedIn)
        {
            ApplyChipStyles();
            return;
        }

        _chips.Add(NewChip("", "CloudLauncher Public", SourceKind.CloudLauncherPublic,
            toolTip: "Packs anyone on this server can find, yours included"));
        _chips.Add(NewDividerChip());
        _chips.Add(NewChip("", "My packs", SourceKind.CloudLauncherPersonal,
            toolTip: "Every instance you own, private ones included"));
        _chips.Add(NewChip("", "Shared with me", SourceKind.CloudLauncherShared));
        _chips.Add(NewChip("", "All teams", SourceKind.CloudLauncherTeam,
            toolTip: "Packs shared with every team you are in"));

        try
        {
            var teams = await App.State.Api.ListTeamsAsync();
            foreach (var t in teams)
                _chips.Add(NewChip("", t.Name, SourceKind.CloudLauncherTeam, t.Id));
        }
        catch { /* teams unavailable: proceed without */ }

        ApplyChipStyles();
    }

    private static SourceChipRow NewChip(string icon, string label, SourceKind kind, Guid? teamId = null,
        string? toolTip = null) =>
        new() { Icon = icon, Label = label, Kind = kind, TeamId = teamId, ToolTipText = toolTip };

    /// <summary>A non-clickable separator between the external catalogs and this server's sources.</summary>
    private static SourceChipRow NewDividerChip() => new()
    {
        IsDivider = true,
        Label = "|",
        IconVisibility = Visibility.Collapsed,
        CursorHint = Cursors.Arrow
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
        if (sender is not FrameworkElement fe || fe.DataContext is not SourceChipRow row || row.IsDivider)
            return;
        try
        {
            await SelectChipAsync(row);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _state.Error($"{row.Label} could not be read.", ex);
        }
    }

    private async Task SelectChipAsync(SourceChipRow chip)
    {
        _activeChip = chip;
        ApplyChipStyles();
        await ResetAndLoadAsync();
    }

    /// <summary>Typing has settled. The box debounces at 450ms and only raises this when the query
    /// changed.</summary>
    private async void OnSearchSettled(object? sender, string text)
    {
        _searchText = text;
        if (_activeChip is null) return;
        try { await ResetAndLoadAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _state.Error("That search could not be run.", ex); }
    }

    /// <summary>Enter: search now, without waiting out the debounce.</summary>
    private async void OnSearchSubmitted(object? sender, EventArgs e)
    {
        _searchText = SearchBox.Text.Trim();
        if (_activeChip is null) return;
        try { await ResetAndLoadAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _state.Error("That search could not be run.", ex); }
    }

    private void OnFiltersClick(object sender, RoutedEventArgs e) => FiltersPopup.IsOpen = !FiltersPopup.IsOpen;

    private async void OnApplyFilters(object sender, RoutedEventArgs e)
    {
        _filterMcVersion = string.IsNullOrWhiteSpace(FilterMcBox.Text) ? null : FilterMcBox.Text.Trim();
        var loaderItem = FilterLoaderBox.SelectedItem as ComboBoxItem;
        var loader = loaderItem?.Content as string;
        _filterLoader = (string.IsNullOrEmpty(loader) || loader == "(any)") ? null : loader;
        FiltersPopup.IsOpen = false;
        try { await ResetAndLoadAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _state.Error("Those filters could not be applied.", ex); }
    }

    private async Task ResetAndLoadAsync()
    {
        _cts.Cancel(); _cts = new CancellationTokenSource();
        // Wait for the cancelled load to unwind before clearing: otherwise the new load stops at the
        // in-progress guard and leaves the list empty, or a late page appends into the cleared list.
        await _pager.DrainAsync();
        _offset = 0;
        _rows.Clear();
        _listedPackIds.Clear();
        _pager.Reset();          // the old throttle timestamp belongs to the previous result set
        _hasMore = true;
        // A new query: old results aren't worth keeping, so this is Loading, not Refreshing.
        _state.Begin(refreshing: false);
        _state.Note(null);       // whatever the last query said about itself is no longer true
        ClearDetail();
        await _pager.LoadPageAsync(_cts.Token);
    }

    private async void OnResultsScroll(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer sv) return;
        try
        {
            await _pager.FillAheadAsync(sv, _cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _state.Error("The next page could not be loaded.", ex); }
    }

    private async Task LoadMoreAsync(CancellationToken ct)
    {
        if (_activeChip is null || _isLoading || !_hasMore) return;
        if (_rows.Count >= MaxResults) { _hasMore = false; return; }

        _isLoading = true;
        // Topping up visible results shows the Refreshing hairline; the first page of a new query
        // already entered Loading in ResetAndLoadAsync.
        if (_rows.Count > 0) _state.Begin(refreshing: true);

        try
        {
            int added;
            switch (_activeChip.Kind)
            {
                case SourceKind.CurseForge:
                    added = await LoadCurseForgeAsync(ct);
                    break;
                case SourceKind.Modrinth:
                    added = await LoadModrinthAsync(ct);
                    break;
                case SourceKind.CloudLauncherPublic:
                    added = await LoadCloudLauncherAsync(PackBrowseSource.Public, null, ct);
                    break;
                case SourceKind.CloudLauncherPersonal:
                    added = await LoadCloudLauncherAsync(PackBrowseSource.Owned, null, ct);
                    break;
                case SourceKind.CloudLauncherShared:
                    added = await LoadCloudLauncherAsync(PackBrowseSource.Shared, null, ct);
                    break;
                case SourceKind.CloudLauncherTeam:
                    added = await LoadCloudLauncherAsync(PackBrowseSource.Team, _activeChip.TeamId, ct);
                    break;
                default:
                    added = 0; _hasMore = false; break;
            }

            _offset += added;
            if (added == 0) _hasMore = false;

            // Stay in Loading while more pages are coming, or "No results" would flash mid-search.
            if (_rows.Count > 0 || !_hasMore)
            {
                // The empty message depends on the source and filters. EmptyNext only applies to this
                // Content call.
                var (emptyTitle, emptyBody) = EmptyAnswer(_activeChip);
                _state.EmptyNext(emptyTitle, emptyBody);

                _state.Content(_rows.Count,
                    countText: $"{_rows.Count:N0} result{(_rows.Count == 1 ? "" : "s")}"
                             + (HasFilters ? " (filtered)" : ""));
            }
        }
        catch (OperationCanceledException)
        {
            // Usually a newer query cancelled us, but the token is shared with the detail pane, so a row
            // click can cancel too. Nothing else would clear the bar then, so show the rows we have.
            if (_state.HasData) _state.Cancelled();
        }
        catch (Exception ex)
        {
            // Name the catalog that failed, with the store's own calm sentence when it gave one.
            _state.Error(StoreRequestException.PlainFor(ex) ?? $"{_activeChip.Label} did not answer.", ex);
            _hasMore = false;
        }
        finally
        {
            _isLoading = false;
        }
    }

    private async Task<int> LoadCurseForgeAsync(CancellationToken ct)
    {
        var hits = await App.State.CurseForge.SearchAsync(
            _searchText, _filterMcVersion, _filterLoader,
            limit: PageSize, offset: _offset,
            classId: CurseForgeService.ClassIdModpacks, ct: ct);
        foreach (var m in hits) _rows.Add(RowFromExternal(m, "CurseForge"));
        return hits.Count;
    }

    private async Task<int> LoadModrinthAsync(CancellationToken ct)
    {
        var hits = await App.State.Modrinth.SearchAsync(
            _searchText, _filterMcVersion, _filterLoader,
            limit: PageSize, offset: _offset, projectType: "modpack", ct: ct);
        foreach (var m in hits) _rows.Add(RowFromExternal(m, "Modrinth"));
        return hits.Count;
    }

    /// <summary>
    /// Loads one screenful from this server's catalog, applying the Minecraft version and loader
    /// filters.
    /// </summary>
    /// <remarks>
    /// The browse endpoint can't filter by version or loader, so filtering happens here and a page
    /// can end up empty. Keep pulling pages until something matches or the server runs out, up to a
    /// cap; scrolling continues from there.
    /// </remarks>
    private async Task<int> LoadCloudLauncherAsync(PackBrowseSource source, Guid? teamId, CancellationToken ct)
    {
        const int maxPagesPerCall = 8;
        var scanned = 0;
        var matched = 0;

        // Your own public packs lead the first Public page, since older servers leave them out of
        // Public. Fetched alongside the first page to avoid a second round trip.
        var ownPublic = source == PackBrowseSource.Public && _offset == 0
            ? OwnPublicPacks.FetchAsync(App.State.Api, _searchText, ct)
            : null;

        for (var page = 0; page < maxPagesPerCall; page++)
        {
            var result = await App.State.Api.BrowsePacksAsync(
                source, teamId, _searchText, _offset + scanned, PageSize, ct);
            scanned += result.Items.Count;

            if (ownPublic is not null)
            {
                PinOwnPublicPacks(await ownPublic);
                ownPublic = null;
            }

            foreach (var p in result.Items.Where(MatchesPackFilters))
            {
                if (!_listedPackIds.Add(p.Id)) continue;   // pinned above, or a shifted page boundary
                _rows.Add(RowFromInternal(p));
                matched++;
            }

            _hasMore = _offset + scanned < result.Total;
            if (matched > 0 || !_hasMore || result.Items.Count == 0) break;
        }

        // The paging cursor counts rows the server returned, not the filtered or pinned ones.
        return scanned;
    }

    /// <summary>Puts your own public packs at the top of the Public list, marked as yours.</summary>
    /// <remarks>Pinned instead of sorted in by date, which would need every later page to know where
    /// they fall. Later copies from the server are dropped by <see cref="_listedPackIds"/>.</remarks>
    private void PinOwnPublicPacks(IReadOnlyList<PackSummary> own)
    {
        var at = 0;
        foreach (var p in own.Where(MatchesPackFilters))
        {
            if (!_listedPackIds.Add(p.Id)) continue;
            _rows.Insert(at++, RowFromInternal(p));
        }
    }

    /// <summary>What an empty list means for this source, worded for whatever is narrowing it.</summary>
    private (string Title, string Body) EmptyAnswer(SourceChipRow chip)
    {
        var narrowing = (!string.IsNullOrWhiteSpace(_searchText), HasFilters) switch
        {
            (true, true) => "your search and filters",
            (true, false) => "your search",
            (false, true) => "your filters",
            _ => null
        };

        if (narrowing is not null)
            return ("No results", chip.Kind switch
            {
                SourceKind.CloudLauncherPublic => $"No public packs match {narrowing}.",
                SourceKind.CloudLauncherPersonal => $"None of your packs match {narrowing}.",
                SourceKind.CloudLauncherShared => $"No pack shared with you matches {narrowing}.",
                SourceKind.CloudLauncherTeam => $"No team pack matches {narrowing}.",
                _ => "Try a different search or filter."
            });

        return chip.Kind switch
        {
            SourceKind.CloudLauncherPublic => ("No public packs yet",
                "Packs set to 'Everyone on this CloudLauncher server' show up here, yours included. "
                + "To list one of yours, choose that on its Share & sync tab."),
            SourceKind.CloudLauncherPersonal => ("No packs yet",
                "You don't own any instances yet. Every one you create shows up here, private ones too."),
            SourceKind.CloudLauncherShared => ("No results", "Nobody has shared a pack with you yet."),
            SourceKind.CloudLauncherTeam when chip.TeamId is null =>
                ("No results", "None of your teams has a pack shared with it yet."),
            SourceKind.CloudLauncherTeam => ("No results", "This team has no packs yet."),
            _ => ("No results", "Try a different search or filter.")
        };
    }

    /// <summary>True when the Filters popup is narrowing the results.</summary>
    private bool HasFilters =>
        !string.IsNullOrWhiteSpace(_filterMcVersion) || !string.IsNullOrWhiteSpace(_filterLoader);

    /// <summary>True when a pack passes the Filters popup. An unset filter matches everything.</summary>
    private bool MatchesPackFilters(PackSummary p) =>
        (string.IsNullOrWhiteSpace(_filterMcVersion)
            || string.Equals(p.MinecraftVersion, _filterMcVersion, StringComparison.OrdinalIgnoreCase))
        && (string.IsNullOrWhiteSpace(_filterLoader)
            || string.Equals(p.Loader.ToString(), _filterLoader, StringComparison.OrdinalIgnoreCase));

    private static PackBrowseRow RowFromExternal(ModSummary m, string platform) => new()
    {
        Kind = PackBrowseRowKind.External,
        External = m,
        Title = m.Name,
        Subtitle = m.Description ?? "",
        MetaLabel = $"by {m.Author ?? "unknown"} · {FormatNumber(m.DownloadCount)} downloads",
        IconUrl = m.IconUrl,
        Initial = InitialFor(m.Name),
        SourceBadge = platform,
        SourceBadgeBackground = platform == "CurseForge"
            ? new SolidColorBrush(Color.FromRgb(0xF1, 0x65, 0x36))
            : new SolidColorBrush(Color.FromRgb(0x1B, 0xD9, 0x6A)),
        SourceBadgeForeground = Brushes.White,
        ActionLabel = "Download"
    };

    private PackBrowseRow RowFromInternal(PackSummary p)
    {
        var row = new PackBrowseRow
        {
            Kind = PackBrowseRowKind.Internal,
            Internal = p,
            Title = p.Name,
            Subtitle = p.Summary ?? p.Description ?? "",
            MetaLabel = $"by {p.OwnerUsername} · {FormatLoader(p.Loader, p.LoaderVersion)} · {p.MinecraftVersion ?? "-"}",
            Initial = InitialFor(p.Name),
            SourceBadge = "CloudLauncher",
            SourceBadgeBackground = (Brush)FindResource("AccentSoftBrush"),
            SourceBadgeForeground = (Brush)FindResource("AccentBrush"),
            ActionLabel = "Add",
            // Hosted packs have no icon URL, but packs you have carry artwork in their synced assets.
            IconImage = TryLoadLocalIcon(p.Id),
            // Every row under My packs is yours, so the marker would only be noise there.
            YoursVisibility = IsMine(p) && _activeChip?.Kind != SourceKind.CloudLauncherPersonal
                ? Visibility.Visible
                : Visibility.Collapsed
        };
        ApplyInternalActionState(row);
        return row;
    }

    /// <summary>The locally synced cover art for a pack you already have, or null.</summary>
    private static System.Windows.Media.ImageSource? TryLoadLocalIcon(Guid packId)
    {
        try { return App.State.PackAssets.TryLoadIconImage(packId); }
        catch { return null; } // missing or locked instance folder: fall back to the letter tile
    }

    // ── detail panel ─────────────────────────────────────────────────────────

    private async void OnResultSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsList.SelectedItem is not PackBrowseRow row) return;
        try { await LoadRowDetailAsync(row); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Fail("That pack's details could not be loaded.", ex); }
    }

    private async void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsList.SelectedItem is not PackBrowseRow row) return;
        try { await DownloadRowAsync(row, null); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Fail("That pack could not be added.", ex); }
    }

    private async Task LoadRowDetailAsync(PackBrowseRow row)
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _currentRow = row;
        _currentExternal = row.External;
        _currentInternal = row.Internal;
        _cachedDescription = null;
        _descriptionIsMarkdown = false;

        DetailPlaceholder.Visibility = Visibility.Collapsed;
        DetailHeader.Visibility = Visibility.Visible;
        PackTabs.Visibility = Visibility.Visible;
        PackNameLabel.Text = row.Title;
        PackMetaLabel.Text = string.IsNullOrWhiteSpace(row.Subtitle)
            ? row.MetaLabel
            : $"{row.Subtitle}\n{row.MetaLabel}";
        SelectedIconFallback.Text = row.Initial;
        SetSelectedIcon(row.IconUrl);
        DownloadStatus.Text = "";
        DownloadSelectedButton.Content = row.ActionLabel;
        DownloadSelectedButton.Visibility = row.ActionVisibility;
        DownloadSelectedButton.ToolTip = string.IsNullOrWhiteSpace(row.StateHint) ? null : row.StateHint;
        AddedHint.Text = row.StateHint;
        PackTabs.SelectedIndex = 0;
        OverviewBrowser.Visibility = Visibility.Visible;
        OverviewBrowser.Show("Loading...");
        ScreenshotsEmptyText.Text = "Loading screenshots...";
        ScreenshotsEmptyText.Visibility = Visibility.Visible;
        ScreenshotList.ItemsSource = null;
        ScreenshotList.Visibility = Visibility.Collapsed;
        VersionsGrid.ItemsSource = null;
        VersionFilterNote.Text = "";

        try
        {
            if (row.Kind == PackBrowseRowKind.External && row.External is { } ext)
                await LoadExternalDetailAsync(ext, ct);
            else if (row.Kind == PackBrowseRowKind.Internal && row.Internal is { } internalPack)
                await LoadInternalDetailAsync(internalPack, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(PackBrowserView), ex);
            OverviewBrowser.Show("This pack's page could not be loaded. The full error is in the launcher log.");
        }
    }

    private async Task LoadExternalDetailAsync(ModSummary mod, CancellationToken ct)
    {
        ShowExternalTabs(true);
        _currentProjectUrl = BuildProjectUrl(mod);
        OpenProjectButton.Visibility = Visibility.Visible;
        ConfigureLinks(new ModProjectLinks(_currentProjectUrl, null, null, null, null));

        var detail = mod.Source == ModSource.CurseForge
            ? await App.State.CurseForge.GetProjectDetailAsync(int.TryParse(mod.Id, out var cid) ? cid : 0, ct)
            : await App.State.Modrinth.GetProjectDetailAsync(mod.Id, ct);

        _cachedDescription = detail.Description ?? mod.Description;
        // Trust the declared format instead of sniffing: Modrinth Markdown can embed HTML tags.
        _descriptionIsMarkdown = detail.IsMarkdown;
        ShowOverviewDescription(_cachedDescription, _descriptionIsMarkdown);
        ShowScreenshots(detail.Screenshots);
        ConfigureLinks(detail.Links with { WebsiteUrl = detail.Links.WebsiteUrl ?? _currentProjectUrl });

        _allVersions = await LoadVersionsForModAsync(mod, ct);
        ShowVersions(ShowAllVersionsBox.IsChecked == true);
    }

    private async Task LoadInternalDetailAsync(PackSummary summary, CancellationToken ct)
    {
        _currentProjectUrl = null;
        OpenProjectButton.Visibility = Visibility.Collapsed;
        ConfigureLinks(new ModProjectLinks(null, null, null, null, null));
        ShowExternalTabs(false);

        try
        {
            var detail = await App.State.Api.GetPackAsync(summary.Id, ct);
            if (TryShowLocalPackDescription(summary.Id))
            {
                /* rich body from synced .cloudlauncher assets */
            }
            else
            {
                _cachedDescription = detail.Description ?? detail.Summary ?? summary.Summary;
                var isMarkdown = !string.IsNullOrWhiteSpace(_cachedDescription)
                    && !PackText.LooksLikeHtml(_cachedDescription);
                ShowOverviewDescription(_cachedDescription, isMarkdown);
            }

            ShowInternalDetails(detail);
        }
        catch (OperationCanceledException) { throw; }
        catch (ApiException api) when (api.Status == HttpStatusCode.Forbidden)
        {
            // The owner made the pack Private or removed you after the list loaded. Show that instead of
            // the raw 403 body.
            ShowOverviewDescription(
                $"You no longer have access to '{summary.Name}'.\n\n"
                + "Its owner stopped sharing it, so it can't be downloaded or played. "
                + "You can remove it from your instance list from the right-click menu on the row.",
                false);
            InternalDetailsList.ItemsSource = null;
            if (_currentRow is { } row) await ReportLostAccessAsync(row);
        }
        catch (ApiException api) when (api.Status == HttpStatusCode.NotFound)
        {
            ShowOverviewDescription($"'{summary.Name}' no longer exists - its owner deleted it.", false);
            InternalDetailsList.ItemsSource = null;
        }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(PackBrowserView), ex);
            ShowOverviewDescription(
                (summary.Summary ?? summary.Description ?? "") +
                Environment.NewLine + Environment.NewLine +
                "The rest of this pack's details could not be read. The full error is in the launcher log.",
                false);
        }
    }

    /// <summary>
    /// Fills the Details tab for a CloudLauncher pack.
    /// </summary>
    /// <remarks>Hosted packs have no release history; the server keeps one live copy of the
    /// instance.</remarks>
    private void ShowInternalDetails(PackDetail detail)
    {
        var facts = new List<PackFactRow>
        {
            new("Minecraft version", detail.MinecraftVersion ?? "-"),
            new("Loader", FormatLoader(detail.Loader, detail.LoaderVersion)),
            new("Visibility", detail.Visibility switch
            {
                PackVisibility.Public => "Public - anyone on this server can find it",
                PackVisibility.Team   => "Team - visible to the teams it is shared with",
                _                     => "Private - only you and its collaborators"
            }),
            new("Hosted on the server",
                detail.IsShared ? "Yes - its files can be downloaded" : "No - there are no files to download",
                detail.IsShared
                    ? null
                    : App.State.Settings.UserId is { } me && detail.OwnerId == me
                        ? "Tick 'Publish this instance to CloudLauncher' on its Share & sync tab so "
                          + "people who add it get its files."
                        : "Its owner has not published its files yet, so adding it gives you an empty instance."),
            new("Owner", detail.OwnerUsername),
            new("Shared with",
                $"{Plural(detail.Collaborators.Count, "collaborator")}, {Plural(detail.Teams.Count, "team")}"),
            new("Created", TimeFormat.Date(detail.CreatedAt)),
            new("Last updated", TimeFormat.DateTime(detail.UpdatedAt))
        };

        InternalDetailsList.ItemsSource = facts;
        VersionsGrid.ItemsSource = null;
    }

    private static string Plural(int count, string noun) =>
        $"{count} {noun}{(count == 1 ? "" : "s")}";

    /// <summary>
    /// Shows or hides the two tabs only a CurseForge or Modrinth project can fill. Hosted packs have
    /// no screenshots or project links.
    /// </summary>
    private void ShowExternalTabs(bool external)
    {
        ScreenshotsTab.Visibility = external ? Visibility.Visible : Visibility.Collapsed;
        LinksTab.Visibility = external ? Visibility.Visible : Visibility.Collapsed;
        VersionsTab.Header = external ? "Versions" : "Details";
        VersionsToolbar.Visibility = external ? Visibility.Visible : Visibility.Collapsed;
        VersionsGridHost.Visibility = external ? Visibility.Visible : Visibility.Collapsed;
        InternalDetailsHost.Visibility = external ? Visibility.Collapsed : Visibility.Visible;

        if (external) return;
        ScreenshotList.ItemsSource = null;
        ScreenshotList.Visibility = Visibility.Collapsed;
        ScreenshotsEmptyText.Visibility = Visibility.Collapsed;
    }

    private void ShowVersions(bool all)
    {
        var mc = _filterMcVersion ?? "";
        var loader = _filterLoader ?? "";
        var filtered = all
            ? _allVersions
            : _allVersions.Where(v => MatchesFilters(v, mc, loader)).ToList();

        VersionFilterNote.Text = all
            ? $"(all {_allVersions.Count})"
            : $"(filtered {filtered.Count}/{_allVersions.Count})";

        VersionsGrid.ItemsSource = filtered
            .OrderByDescending(v => v.DatePublished)
            .Select(v => PackVersionRow.FromExternal(v))
            .ToList();
    }

    private void OnShowAllVersionsChanged(object sender, RoutedEventArgs e) =>
        ShowVersions(ShowAllVersionsBox.IsChecked == true);

    private async void OnDownloadRow(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not PackBrowseRow row) return;
        try { await DownloadRowAsync(row, b); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Fail("That pack could not be added.", ex); }
    }

    private async void OnDownloadSelectedPack(object sender, RoutedEventArgs e)
    {
        try { await DownloadCurrentAsync(sender as Button); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Fail("That pack could not be added.", ex); }
    }

    private async void OnDownloadVersion(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PackVersionRow versionRow }) return;
        var button = sender as Button;
        try
        {
            if (versionRow.IsInternal)
            {
                if (_currentRow is not null)
                    await DownloadRowAsync(_currentRow, button);
                return;
            }

            if (_currentExternal is null || versionRow.Source is null) return;
            if (button is not null) button.IsEnabled = false;
            try { await DownloadExternalAsync(_currentExternal, versionRow.Source, button); }
            finally { if (button is not null) button.IsEnabled = true; }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Fail("That version could not be downloaded.", ex); }
    }

    private async Task DownloadCurrentAsync(Button? button)
    {
        if (_currentRow is null) return;
        await DownloadRowAsync(_currentRow, button);
    }

    private async Task DownloadRowAsync(PackBrowseRow row, Button? button)
    {
        if (row.Kind == PackBrowseRowKind.Internal && row.Internal is { } internalPack
            && (IsInternalPackAdded(internalPack.Id) || IsMine(internalPack)))
        {
            // Already yours: take them to it, where Play and Download live.
            _shell.OpenPackDetail(internalPack.Id, internalPack.Name);
            return;
        }

        if (button is not null) button.IsEnabled = false;
        try
        {
            if (row.Kind == PackBrowseRowKind.Internal && row.Internal is not null)
            {
                var pack = await App.State.ModpackDownload.SubscribeInternalPackAsync(row.Internal);
                SetStatus($"Added '{pack.Name}' to your packs.", error: false);
                OnPackAdded(pack);
            }
            else if (row.External is not null)
            {
                await DownloadExternalAsync(row.External, selectedVersion: null, button);
            }
        }
        catch (ApiException api) when (api.Status == HttpStatusCode.Forbidden)
        {
            await ReportLostAccessAsync(row);
        }
        catch (ApiException api) when (api.Status == HttpStatusCode.NotFound)
        {
            SetStatus("That pack no longer exists - its owner deleted it.", error: true);
        }
        catch (Exception ex)
        {
            Fail("That download could not be started.", ex);
        }
        finally
        {
            if (button is not null) button.IsEnabled = true;
        }
    }

    /// <summary>
    /// Explains a 403 on a pack (the owner stopped sharing it) and offers to drop it from your list
    /// if it is still there.
    /// </summary>
    private async Task ReportLostAccessAsync(PackBrowseRow row)
    {
        var name = row.Title;
        SetStatus($"You no longer have access to '{name}' - its owner stopped sharing it.", error: true);

        if (row.Internal is not { } pack || !IsInternalPackAdded(pack.Id)) return;

        var remove = await AppDialog.ConfirmAsync(_shell, "Access removed",
            $"'{name}' is no longer shared with you, so it can't be downloaded or played.\n\n"
            + "Remove it from your instance list? The files already on this PC are left alone.",
            "Remove from my list", "Keep it", danger: true);
        if (remove) await RemoveFromMyListAsync(pack);
    }

    /// <summary>Unsubscribes from a hosted pack and stops listing it locally.</summary>
    private async Task RemoveFromMyListAsync(PackSummary pack)
    {
        // A download still running into it would carry on into a folder nothing lists.
        await PackJobs.StopAndWaitAsync(pack.Id, TimeSpan.FromSeconds(30));
        try
        {
            await App.State.Api.UnsubscribePackAsync(pack.Id);
        }
        catch (ApiException api) when (api.Status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            // Already gone server-side; hiding it locally is still the right outcome.
        }

        App.State.Settings.HidePack(pack.Id);
        _addedPackIds.Remove(pack.Id);
        RefreshInternalActionState(pack.Id);
        SetStatus($"Removed '{pack.Name}' from your list.", error: false);
    }

    /// <summary>
    /// Writes to both status lines, the footer and the one under the detail panel.
    /// </summary>
    /// <remarks>Colour is set by resource reference: ThemeService replaces brushes on every theme
    /// change, so an assigned brush would keep the old colour.</remarks>
    private void SetStatus(string message, bool error)
    {
        var key = error ? "DangerBrush" : "AccentBrush";
        StatusLabel.SetResourceReference(TextBlock.ForegroundProperty, key);
        DownloadStatus.SetResourceReference(TextBlock.ForegroundProperty, key);
        StatusLabel.Text = message;
        DownloadStatus.Text = message;
    }

    /// <summary>An action failed: one sentence the user can act on, and the exception to the log.</summary>
    /// <remarks>Same split as <see cref="PageState.Error"/>. Raw exception text is for the log, not
    /// the user.</remarks>
    private void Fail(string plain, Exception ex)
    {
        AppLog.LogError(nameof(PackBrowserView), ex);
        SetStatus(plain + " The full error is in the launcher log.", error: true);
    }

    private async Task DownloadExternalAsync(ModSummary mod, ModVersion? selectedVersion, Button? button)
    {
        StatusLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        DownloadStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        StatusLabel.Text = $"Starting download for '{mod.Name}'...";
        DownloadStatus.Text = StatusLabel.Text;

        var metadata = new PackImportMetadata(
            mod.Description?.Trim(),
            null,
            _cachedDescription,
            mod.IconUrl,
            mod.Source.ToString(),
            _descriptionIsMarkdown);

        await App.State.ModpackDownload.StartExternalDownloadAsync(mod, selectedVersion, metadata);

        SetStatus($"'{mod.Name}' is downloading - check your instances.", error: false);
    }

    private async Task<List<ModVersion>> LoadVersionsForModAsync(ModSummary mod, CancellationToken ct)
    {
        if (mod.Source == ModSource.CurseForge && int.TryParse(mod.Id, out var cfId))
            return await App.State.CurseForge.GetVersionsAsync(cfId, ct);

        return await App.State.Modrinth.GetVersionsAsync(
            mod.Id,
            string.IsNullOrWhiteSpace(_filterMcVersion) ? null : _filterMcVersion,
            string.IsNullOrWhiteSpace(_filterLoader) ? null : _filterLoader,
            ct);
    }

    private static bool MatchesFilters(ModVersion version, string mc, string loader) =>
        (mc.Length == 0 || version.GameVersions.Contains(mc, StringComparer.OrdinalIgnoreCase)) &&
        (loader.Length == 0 || version.Loaders.Contains(loader, StringComparer.OrdinalIgnoreCase));

    private void ClearDetail()
    {
        _currentRow = null;
        _currentExternal = null;
        _currentInternal = null;
        _allVersions.Clear();
        _currentProjectUrl = null;
        _cachedDescription = null;
        _descriptionIsMarkdown = false;

        DetailHeader.Visibility = Visibility.Collapsed;
        PackTabs.Visibility = Visibility.Collapsed;
        DetailPlaceholder.Visibility = Visibility.Visible;
        PackNameLabel.Text = "";
        PackMetaLabel.Text = "";
        SelectedIconFallback.Text = "";
        SelectedIconImage.Source = null;
        OverviewBrowser.Show("");
        ScreenshotList.ItemsSource = null;
        VersionsGrid.ItemsSource = null;
        InternalDetailsList.ItemsSource = null;
        VersionFilterNote.Text = "";
        DownloadStatus.Text = "";
        AddedHint.Text = "";
        OpenProjectButton.Visibility = Visibility.Collapsed;
        // Restore the tabs a CloudLauncher pack may have hidden.
        ShowExternalTabs(true);
    }

    private bool TryShowLocalPackDescription(Guid packId)
    {
        try
        {
            App.State.PackAssets.ApplyFromSharedFolder(packId);
            if (!App.State.PackAssets.TryReadDescriptionForDisplay(packId, out var content, out var isMarkdown))
                return false;

            _cachedDescription = content;
            _descriptionIsMarkdown = isMarkdown;
            ShowOverviewDescription(content, isMarkdown);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void ShowOverviewDescription(string? content, bool isMarkdown)
        => OverviewBrowser.Show(content, isMarkdown);

    private void ShowScreenshots(IReadOnlyList<ModMediaItem> screenshots)
    {
        var rows = screenshots.Select(s => new MediaRow(s)).ToList();
        ScreenshotList.ItemsSource = rows;
        ScreenshotList.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ScreenshotsEmptyText.Text = "No screenshots are available for this modpack.";
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

    // ── row context menu ─────────────────────────────────────────────────────

    /// <summary>Resolves the row a context-menu item belongs to, the way PackListView does.</summary>
    private static PackBrowseRow? RowFromMenuSender(object sender)
    {
        if (sender is FrameworkElement el && el.DataContext is PackBrowseRow direct) return direct;
        if (sender is MenuItem mi)
        {
            var parent = mi.Parent;
            while (parent is MenuItem p) parent = p.Parent;
            if (parent is ContextMenu cm && cm.PlacementTarget is FrameworkElement target
                && target.DataContext is PackBrowseRow row) return row;
        }
        return null;
    }

    private async void OnCtxPrimaryAction(object sender, RoutedEventArgs e)
    {
        if (RowFromMenuSender(sender) is not { } row) return;
        try { await DownloadRowAsync(row, null); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Fail("That pack could not be added.", ex); }
    }

    private void OnCtxOpenInstance(object sender, RoutedEventArgs e)
    {
        if (RowFromMenuSender(sender)?.Internal is { } pack)
            _shell.OpenPackDetail(pack.Id, pack.Name);
    }

    private void OnCtxOpenProjectPage(object sender, RoutedEventArgs e)
    {
        if (RowFromMenuSender(sender)?.External is { } mod)
            OpenUrl(BuildProjectUrl(mod));
    }

    private void OnCtxCopyId(object sender, RoutedEventArgs e)
    {
        if (RowFromMenuSender(sender)?.Internal is not { } pack) return;
        SetStatus(ClipboardHelper.TrySetText(pack.Id.ToString())
            ? "Pack ID copied."
            : "Couldn't copy - the clipboard is in use by another program.",
            error: false);
    }

    private void OnCtxCopyUrl(object sender, RoutedEventArgs e)
    {
        if (RowFromMenuSender(sender)?.External is not { } mod) return;
        SetStatus(ClipboardHelper.TrySetText(BuildProjectUrl(mod))
            ? "Project URL copied."
            : "Couldn't copy - the clipboard is in use by another program.",
            error: false);
    }

    private async void OnCtxRemoveFromList(object sender, RoutedEventArgs e)
    {
        if (RowFromMenuSender(sender)?.Internal is not { } pack) return;
        try
        {
            var mine = IsMine(pack);
            var confirmed = await AppDialog.ConfirmAsync(_shell, "Remove from my list",
                mine
                    ? $"'{pack.Name}' is yours - removing it here only hides it from your instance "
                      + "list. It stays on the server and anyone you shared it with keeps it.\n\nHide it?"
                    : $"Stop listing '{pack.Name}' as one of your instances?\n\n"
                      + "You can add it again from this browser while it is still shared with you. "
                      + "Files already downloaded to this PC are left alone.",
                mine ? "Hide" : "Remove", "Cancel", danger: true);
            if (!confirmed) return;

            await RemoveFromMyListAsync(pack);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Fail("That pack could not be removed from your list.", ex);
        }
    }

    private void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!SafeLaunch.OpenUrl(url))
            SetStatus("That link could not be opened. The full error is in the launcher log.", error: true);
    }

    /// <summary>The detail header's 42px tile, through the same cache the rows use.</summary>
    /// <remarks>A fresh BitmapImage per selection would decode full-size icons on the UI thread on
    /// every arrow-key press.</remarks>
    private void SetSelectedIcon(string? iconUrl)
    {
        IconLoader.SetDecodeWidth(SelectedIconImage, 42);
        IconLoader.SetUrl(SelectedIconImage, string.IsNullOrWhiteSpace(iconUrl) ? null : iconUrl);
    }

    private static string BuildProjectUrl(ModSummary mod)
    {
        var slug = string.IsNullOrWhiteSpace(mod.Slug) ? mod.Id : mod.Slug;
        return mod.Source == ModSource.CurseForge
            ? $"https://www.curseforge.com/minecraft/modpacks/{slug}"
            : $"https://modrinth.com/modpack/{slug}";
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string InitialFor(string name)
    {
        var t = name?.Trim();
        if (string.IsNullOrEmpty(t)) return "?";
        return char.ToUpperInvariant(t[0]).ToString();
    }

    private static string FormatNumber(long n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:0.#}M",
        >= 1_000 => $"{n / 1_000.0:0.#}K",
        _ => n.ToString()
    };

    private static string FormatLoader(LoaderKind k, string? v) =>
        k == LoaderKind.None ? "(empty)" : $"{k}{(v is null ? "" : $" {v}")}";

    private bool IsInternalPackAdded(Guid packId) => _addedPackIds.Contains(packId);

    /// <summary>
    /// Sets the button on a CloudLauncher pack. A pack already in your instances gets Open, which goes
    /// to its instance page where its files are downloaded.
    /// </summary>
    /// <remarks>Shared packs are in your list as soon as the owner adds you. Packs you own never get
    /// Add: the server refuses <c>subscribe</c> for them with 400 "You own this pack".</remarks>
    private void ApplyInternalActionState(PackBrowseRow row)
    {
        if (row.Internal is not { } pack) return;
        var mine = IsMine(pack);
        var added = IsInternalPackAdded(pack.Id) || mine;
        row.ActionVisibility = Visibility.Visible;
        row.ActionLabel = added ? "Open" : "Add";

        // Listing and hosting are separate switches, so a listed pack may have no files on the server.
        // Warn before anyone adds it, and tell the owner where the switch is.
        var notHosted = !pack.IsShared;
        row.NotHostedVisibility = notHosted ? Visibility.Visible : Visibility.Collapsed;
        row.NotHostedHint = mine
            ? "Its files are not on the server yet, so anyone who adds it gets an empty instance. "
              + "Publish it from its Share & sync tab."
            : "This pack has no files on the server yet - adding it gives you an empty instance.";
        row.StateHint = mine
            ? notHosted
                ? "Yours, but its files are not on the server yet - open it to publish them"
                : "Yours - open it to change it or who can find it"
            : added ? "Already in your instances - open it to download its files"
            : notHosted ? "Not hosted on the server - this pack has no files to download yet"
            : "";

        row.AddedMenuVisibility = IsInternalPackAdded(pack.Id) ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool IsMine(PackSummary pack) =>
        App.State.Settings.UserId is { } me && pack.OwnerId == me;

    // ── view-models ──────────────────────────────────────────────────────────

    public enum SourceKind
    {
        CurseForge,
        Modrinth,
        CloudLauncherPublic,

        /// <summary>Every pack you own, whatever its visibility.</summary>
        CloudLauncherPersonal,
        CloudLauncherShared,
        CloudLauncherTeam
    }

    public enum PackBrowseRowKind { External, Internal }

    public class SourceChipRow
    {
        public string Icon { get; set; } = "";
        public string Label { get; set; } = "";
        public SourceKind Kind { get; set; }
        public Guid? TeamId { get; set; }
        public string? ToolTipText { get; set; }

        /// <summary>A visual separator between chip groups; not selectable.</summary>
        public bool IsDivider { get; set; }
        public Visibility IconVisibility { get; set; } = Visibility.Visible;
        public Cursor CursorHint { get; set; } = Cursors.Hand;
        public Brush Background { get; set; } = Brushes.Transparent;
        public Brush BorderColor { get; set; } = Brushes.Transparent;
        public Brush Foreground { get; set; } = Brushes.Black;
        public FontWeight FontWeight { get; set; } = FontWeights.Normal;
        public Visibility ChipVisibility { get; set; } = Visibility.Visible;
    }

    public class PackBrowseRow
    {
        public PackBrowseRowKind Kind { get; set; }
        public ModSummary? External { get; set; }
        public PackSummary? Internal { get; set; }
        public string Title { get; set; } = "";
        public string Subtitle { get; set; } = "";
        public string MetaLabel { get; set; } = "";
        public string? IconUrl { get; set; }
        public string Initial { get; set; } = "?";
        public string SourceBadge { get; set; } = "";
        public Brush SourceBadgeBackground { get; set; } = Brushes.Transparent;
        public Brush SourceBadgeForeground { get; set; } = Brushes.White;
        public string ActionLabel { get; set; } = "Download";
        public Visibility ActionVisibility { get; set; } = Visibility.Visible;

        /// <summary>Cover art held locally, for a hosted pack that has no icon URL of its own.</summary>
        public System.Windows.Media.ImageSource? IconImage { get; set; }

        /// <summary>Why the button says what it says, for the detail panel.</summary>
        public string StateHint { get; set; } = "";

        /// <summary>Shown on a listed pack whose files were never uploaded to the server.</summary>
        public Visibility NotHostedVisibility { get; set; } = Visibility.Collapsed;

        /// <summary>Why "Not hosted" matters, worded for whoever is looking at it.</summary>
        public string NotHostedHint { get; set; } = "";

        /// <summary>The "Yours" marker: your own pack, in any list but My packs.</summary>
        public Visibility YoursVisibility { get; set; } = Visibility.Collapsed;

        public Visibility InternalMenuVisibility =>
            Kind == PackBrowseRowKind.Internal ? Visibility.Visible : Visibility.Collapsed;

        public Visibility ExternalMenuVisibility =>
            Kind == PackBrowseRowKind.External ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Items that only make sense once a hosted pack is in your instance list.</summary>
        public Visibility AddedMenuVisibility { get; set; } = Visibility.Collapsed;

        /// <summary>The row's own hover text: the full summary plus why its button says what it says.</summary>
        public string RowToolTip =>
            string.Join("\n", new[] { Title, Subtitle, MetaLabel, StateHint }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    /// <summary>One label/value line on a CloudLauncher pack's Details tab.</summary>
    public sealed record PackFactRow(string Label, string Value, string? Hint = null);

    public sealed record PackVersionRow
    {
        public ModVersion? Source { get; init; }
        public bool IsInternal { get; init; }
        public string VersionNumber { get; init; } = "";
        public string McVersions { get; init; } = "";
        public string LoaderList { get; init; } = "";
        public string ReleaseChannel { get; init; } = "";
        public string DateLabel { get; init; } = "";
        public string SizeLabel { get; init; } = "";
        public string ActionLabel { get; init; } = "Download";
        public Visibility ActionVisibility { get; init; } = Visibility.Visible;

        public static PackVersionRow FromExternal(ModVersion version) => new()
        {
            Source = version,
            VersionNumber = version.VersionNumber,
            McVersions = string.Join(", ", version.GameVersions.Take(3)) + (version.GameVersions.Length > 3 ? "..." : ""),
            LoaderList = string.Join(", ", version.Loaders),
            ReleaseChannel = version.ReleaseChannel,
            DateLabel = TimeFormat.Date(version.DatePublished),
            SizeLabel = version.Files.FirstOrDefault()?.Size is long s
                ? s > 1024 * 1024 ? $"{s / (1024.0 * 1024):F1} MB" : $"{s / 1024.0:F0} KB"
                : ""
        };
    }
}
