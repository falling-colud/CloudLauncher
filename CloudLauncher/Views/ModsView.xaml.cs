using System.Collections.ObjectModel;
using System.Diagnostics;
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
using Microsoft.Win32;

namespace CloudLauncher.Views;

public partial class ModsView : Page
{
    private readonly MainWindow _shell;
    private readonly ObservableCollection<ModBrowseRow> _rows = new();
    private readonly ObservableCollection<ModSourceChipRow> _chips = new();
    private readonly List<ModVersion> _allExternalVersions = new();

    private ModSourceChipRow? _activeChip;
    private CancellationTokenSource _cts = new();
    private ModSummary? _currentExternalMod;
    private HostedModDetail? _currentHostedMod;
    private string? _currentProjectUrl;

    private string _searchText = "";
    private string? _filterMcVersion;
    private string? _filterLoader;

    /// <summary>How the CloudLauncher sources are ordered. One of <see cref="ModBrowseSort"/>.</summary>
    /// <remarks>
    /// Not persisted, unlike the Instances screen's sort: this one is a way to find something in a
    /// list you are browsing right now, not a standing preference about your own library. It also
    /// applies to the CloudLauncher chips only — CurseForge and Modrinth answer in the store's own
    /// relevance order and re-sorting one page of their results would be a lie about the rest.
    /// </remarks>
    private string _sort = ModBrowseSort.Updated;
    private int _offset;
    private bool _isLoading;
    private bool _hasMore;

    private const int PageSize = 25;
    private const int MaxResults = 200;
    private const double SearchCollapsedWidth = 36;
    private const double SearchExpandedWidth = 260;

    public ModsView(MainWindow shell)
    {
        InitializeComponent();
        _shell = shell;
        ResultsList.ItemsSource = _rows;
        SourceStrip.ItemsSource = _chips;
        ModTabs.SelectionChanged += OnModTabsChanged;
        Loaded += async (_, _) =>
        {
            await BuildChipsAsync();
            if (_activeChip is null && _chips.Count > 0)
                await SelectChipAsync(_chips.First(c => !c.IsDivider));

            if (Window.GetWindow(this) is Window w)
                w.PreviewKeyDown += OnShellKeyDown;
            ThemeService.Changed += OnThemeChanged;
        };
        Unloaded += (_, _) =>
        {
            if (Window.GetWindow(this) is Window w)
                w.PreviewKeyDown -= OnShellKeyDown;
            ThemeService.Changed -= OnThemeChanged;
            _cts.Cancel();
        };
    }

    /// <summary>The chip and badge colours are assigned brushes, not DynamicResource bindings —
    /// ThemeService hands out brand-new brush objects on every Apply, so a page that is already open
    /// has to repaint itself or it keeps the previous palette until you navigate away and back.</summary>
    private void OnThemeChanged()
    {
        ApplyChipStyles();
        RepaintHostedBadges();
    }

    private void RepaintHostedBadges()
    {
        var background = (Brush)FindResource("AccentSoftBrush");
        var foreground = (Brush)FindResource("AccentBrush");
        var touched = false;
        foreach (var row in _rows.Where(r => r.Hosted is not null))
        {
            row.SourceBadgeBackground = background;
            row.SourceBadgeForeground = foreground;
            touched = true;
        }
        if (touched) ResultsList.Items.Refresh();
    }

    // ── source chips ─────────────────────────────────────────────────────────

    private async Task BuildChipsAsync()
    {
        _chips.Clear();
        _chips.Add(NewChip("\uE753", "CurseForge", ModBrowseSourceKind.CurseForge));
        _chips.Add(NewChip("\uE753", "Modrinth", ModBrowseSourceKind.Modrinth));
        _chips.Add(NewChip("\uE7C3", "CloudLauncher", ModBrowseSourceKind.CloudLauncherPublic));
        _chips.Add(NewDividerChip());
        _chips.Add(NewChip("\uE77B", "Personal mods", ModBrowseSourceKind.CloudLauncherPersonal));
        _chips.Add(NewChip("\uE8F2", "Shared with me", ModBrowseSourceKind.CloudLauncherShared));
        _chips.Add(NewChip("\uE716", "All teams", ModBrowseSourceKind.CloudLauncherTeam));

        try
        {
            var teams = await App.State.Api.ListTeamsAsync();
            foreach (var team in teams.OrderBy(t => t.Name))
                _chips.Add(NewChip("\uE716", team.Name, ModBrowseSourceKind.CloudLauncherTeam, team.Id));
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
        UpdateSortButtonVisibility();
        await ResetAndLoadAsync();
    }

    // ── search / filters ─────────────────────────────────────────────────────

    private void OnShellKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsVisible) return;
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            ExpandSearch();
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            _ = ResetAndLoadAsync();
            e.Handled = true;
        }
        // Delete / F2 act on the selected row, but only on your own hosted mods and never while
        // the user is typing a search — those keys belong to the text box then.
        else if (e.Key is Key.Delete or Key.F2 && SearchBox.Visibility != Visibility.Visible)
        {
            if (ResultsList.SelectedItem is not ModBrowseRow { IsOwnedByMe: true } row) return;
            e.Handled = true;
            if (e.Key == Key.Delete) _ = DeleteHostedModAsync(row);
            else _ = RenameHostedModAsync(row);
        }
    }

    private void OnSearchToggle(object sender, RoutedEventArgs e)
    {
        ExpandSearch();
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void ExpandSearch()
    {
        CompactSearchHost.Width = SearchExpandedWidth;
        SearchBox.Visibility = Visibility.Visible;
    }

    private void CollapseSearch()
    {
        CompactSearchHost.Width = SearchCollapsedWidth;
        SearchBox.Visibility = Visibility.Collapsed;
        Keyboard.ClearFocus();
    }

    // Search as you type (see ModExplorerPage): reload a moment after typing stops; Enter is immediate.
    private System.Windows.Threading.DispatcherTimer? _searchTimer;
    private string _lastSearched = "";

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = SearchBox.Text;
        if (_searchTimer is null)
        {
            _searchTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _searchTimer.Tick += async (_, _) =>
            {
                _searchTimer.Stop();
                if (_activeChip is null || string.Equals(_searchText.Trim(), _lastSearched, StringComparison.Ordinal)) return;
                _lastSearched = _searchText.Trim();
                await ResetAndLoadAsync();
            };
        }
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private async void OnSearchPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _searchTimer?.Stop();
            _lastSearched = _searchText.Trim();
            await ResetAndLoadAsync();
        }
        else if (e.Key == Key.Escape)
        {
            SearchBox.Text = "";
            _searchText = "";
            _searchTimer?.Stop();
            _lastSearched = "";
            CollapseSearch();
            await ResetAndLoadAsync();
            e.Handled = true;
        }
    }

    private void OnSearchLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is DependencyObject nextFocus && IsDescendantOf(nextFocus, CompactSearchHost))
            return;
        CollapseSearch();
    }

    private static bool IsDescendantOf(DependencyObject child, DependencyObject ancestor)
    {
        for (var current = child; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor)) return true;
        }
        return false;
    }

    private void OnFiltersClick(object sender, RoutedEventArgs e) => FiltersPopup.IsOpen = !FiltersPopup.IsOpen;

    private async void OnApplyFilters(object sender, RoutedEventArgs e)
    {
        try
        {
            _filterMcVersion = string.IsNullOrWhiteSpace(FilterMcBox.Text) ? null : FilterMcBox.Text.Trim();
            var loaderItem = FilterLoaderBox.SelectedItem as ComboBoxItem;
            var loader = loaderItem?.Content as string;
            _filterLoader = string.IsNullOrWhiteSpace(loader) || loader == "(any)" ? null : loader;
            FiltersPopup.IsOpen = false;
            await ResetAndLoadAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetDownloadError("Couldn't apply the filters: " + ex.Message); }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        try { await ResetAndLoadAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetDownloadError("Refresh failed: " + ex.Message); }
    }

    // ── loading ──────────────────────────────────────────────────────────────

    private async Task ResetAndLoadAsync()
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        _offset = 0;
        _rows.Clear();
        _hasMore = true;
        StatusLabel.Text = "";
        CountLabel.Text = "";
        EmptyState.Visibility = Visibility.Collapsed;
        ClearSelectedMod();
        await LoadMoreAsync(_cts.Token);
    }

    private async void OnResultsScroll(object sender, ScrollChangedEventArgs e)
    {
        if (_isLoading || !_hasMore || _activeChip is null) return;
        try
        {
            if (e.OriginalSource is ScrollViewer sv &&
                sv.VerticalOffset + sv.ViewportHeight >= sv.ExtentHeight - 200)
            {
                await LoadMoreAsync(_cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetDownloadError("Couldn't load more results: " + ex.Message); }
    }

    private async Task LoadMoreAsync(CancellationToken ct)
    {
        if (_activeChip is null || _isLoading || !_hasMore) return;
        if (_rows.Count >= MaxResults) { _hasMore = false; return; }

        _isLoading = true;
        LoadingBar.Visibility = Visibility.Visible;
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
            CountLabel.Text = _rows.Count == 0 ? "" : $"{_rows.Count} result{(_rows.Count == 1 ? "" : "s")}";
            UpdateEmptyState();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetListError("Error: " + ex.Message);
            _hasMore = false;
        }
        finally
        {
            _isLoading = false;
            LoadingBar.Visibility = Visibility.Collapsed;
        }
    }

    private async Task<int> LoadCurseForgeAsync(CancellationToken ct)
    {
        var hits = await App.State.CurseForge.SearchAsync(
            _searchText, _filterMcVersion, _filterLoader,
            limit: PageSize, offset: _offset,
            classId: CurseForgeService.ClassIdMods, ct: ct);
        foreach (var mod in hits) _rows.Add(ModBrowseRow.FromExternal(mod, "CurseForge", CurseForgeBrush(), Brushes.White));
        return hits.Count;
    }

    private async Task<int> LoadModrinthAsync(CancellationToken ct)
    {
        var hits = await App.State.Modrinth.SearchAsync(
            _searchText, _filterMcVersion, _filterLoader,
            limit: PageSize, offset: _offset, projectType: "mod", ct: ct);
        foreach (var mod in hits) _rows.Add(ModBrowseRow.FromExternal(mod, "Modrinth", ModrinthBrush(), Brushes.White));
        return hits.Count;
    }

    private async Task<int> LoadCloudLauncherAsync(ModBrowseSource source, Guid? teamId, CancellationToken ct)
    {
        var page = await App.State.Api.BrowseModsAsync(
            source, teamId, _searchText, _filterMcVersion, _filterLoader,
            offset: _offset, limit: PageSize, ct: ct, sort: _sort);

        // Icons are resolved before the rows are built so each row is complete when it appears,
        // rather than a grey tile that pops into a picture a moment later. They are fetched together
        // rather than one after another because a page is 25 mods, and the cache means this is a
        // request only for icons this launcher has never seen.
        var icons = await ResolveHostedIconsAsync(page.Items, ct);
        if (ct.IsCancellationRequested) return 0;

        foreach (var mod in page.Items)
            _rows.Add(ModBrowseRow.FromHosted(
                mod, (Brush)FindResource("AccentSoftBrush"), (Brush)FindResource("AccentBrush"),
                icons.GetValueOrDefault(mod.Id)));
        _hasMore = _offset + page.Items.Count < page.Total;
        CountLabel.Text = page.Total == 0 ? "" : $"{Math.Min(_offset + page.Items.Count, page.Total)} of {page.Total}";
        return page.Items.Count;
    }

    /// <summary>Local file paths for the icons of a page of hosted mods, keyed by mod id.</summary>
    /// <remarks>
    /// A hosted mod's icon route is authorised for anything not public, and a WPF image binding
    /// sends no token — so the bytes come through the API client, which caches them by content hash.
    /// Mods with no icon are simply absent from the dictionary and fall back to the letter tile, and
    /// so is any icon that could not be fetched: a picture is never worth failing a list over.
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

    private void UpdateEmptyState()
    {
        if (_rows.Count > 0 || _hasMore || _isLoading)
        {
            EmptyState.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyState.Visibility = Visibility.Visible;
        EmptyTitle.Text = _activeChip?.Kind switch
        {
            ModBrowseSourceKind.CloudLauncherPersonal => "No personal mods yet",
            ModBrowseSourceKind.CloudLauncherShared => "No shared mods yet",
            ModBrowseSourceKind.CloudLauncherTeam => _activeChip.TeamId is null ? "No team mods yet" : "This team has no mods yet",
            ModBrowseSourceKind.CloudLauncherPublic => "No CloudLauncher mods found",
            _ => "No mods found"
        };
        EmptyDetail.Text = _activeChip?.Kind == ModBrowseSourceKind.CloudLauncherPersonal
            ? "Create a mod to host versions, edit its overview, and publish it later."
            : "Try a different search, filter, or source.";
    }

    // ── detail panel ─────────────────────────────────────────────────────────

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

    // The Overview tab renders rich HTML/markdown in an embedded WebView2. Its HWND draws
    // over WPF (airspace), so hide it whenever another tab is selected. Scrolling is native.
    private void OnModTabsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != ModTabs) return;
        var onOverview = ModTabs.SelectedIndex == 0;
        OverviewHost.Visibility = onOverview ? Visibility.Visible : Visibility.Collapsed;
        OverviewBrowser.Visibility = onOverview ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowOverview(string? content, bool isMarkdown = false) => OverviewBrowser.Show(content, isMarkdown);

    /// <summary>Screenshots and Links exist only for CurseForge/Modrinth projects. Collapsing the
    /// tabs (rather than showing an apologetic sentence) keeps the hosted pane honest.</summary>
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
        ShowOverview("Loading…");
        try
        {
            var detail = await App.State.Api.GetModAsync(modId, ct);
            _currentHostedMod = detail;
            _currentExternalMod = null;
            _currentProjectUrl = null;

            ModNameLabel.Text = detail.Name;
            ModMetaLabel.Text =
                $"by {detail.OwnerUsername} · {detail.Versions.Count} version{(detail.Versions.Count == 1 ? "" : "s")}"
                + $" · updated {detail.UpdatedAt.LocalDateTime:d} · {detail.Visibility} · {detail.Slug}";
            SelectedIconFallback.Text = InitialFor(detail.Name);
            SetSelectedIcon(null);
            // Fetched after the panel is painted: the letter tile is already correct, and the icon
            // replacing it a moment later is better than holding the whole panel for a picture.
            _ = ApplyHostedIconAsync(detail, ct);
            OpenProjectButton.Visibility = Visibility.Collapsed;
            ManageHostedButton.Visibility = detail.OwnerId == App.State.Settings.UserId
                ? Visibility.Visible : Visibility.Collapsed;
            InstallSelectedButton.Visibility = Visibility.Visible;
            InstallSelectedButton.IsEnabled = detail.Versions.Count > 0;
            InstallSelectedButton.ToolTip = detail.Versions.Count > 0
                ? "Install the newest compatible version into one of your instances"
                : "Nothing to install yet — upload a version from Manage first";
            ToolTipService.SetShowOnDisabled(InstallSelectedButton, true);

            ModTabs.SelectedIndex = 0;
            ShowOverview(detail.Description ?? detail.Summary, isMarkdown: true);
            // A hosted mod has neither screenshots nor project links, so those two tabs could only
            // ever show a placebo sentence: hide them instead of leaving two dead tabs on the pane.
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
        InstallSelectedButton.ToolTip = "Install the newest compatible version, with its required dependencies";
        ModTabs.SelectedIndex = 0;
        ShowOverview("Loading…");
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

            _allExternalVersions.Clear();
            _allExternalVersions.AddRange(await LoadVersionsForModAsync(mod, applyFilters: false, ct));
            ShowExternalVersions(ShowAllVersionsBox.IsChecked == true);
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
        var filtered = all ? _allExternalVersions : _allExternalVersions.Where(v => MatchesFilters(v, _filterMcVersion, _filterLoader)).ToList();
        VersionFilterNote.Text = all
            ? $"(all {_allExternalVersions.Count})"
            : $"(filtered {filtered.Count}/{_allExternalVersions.Count})";
        VersionsGrid.ItemsSource = filtered.Select(VersionDisplayRow.FromExternal).ToList();
    }

    private void OnShowAllVersionsChanged(object sender, RoutedEventArgs e)
    {
        if (_currentExternalMod is not null)
            ShowExternalVersions(ShowAllVersionsBox.IsChecked == true);
    }

    private void ClearSelectedMod(bool keepPanelVisible = false)
    {
        _currentExternalMod = null;
        _currentHostedMod = null;
        _currentProjectUrl = null;
        _allExternalVersions.Clear();
        ModNameLabel.Text = "";
        ModMetaLabel.Text = "";
        SelectedIconFallback.Text = "";
        SelectedIconImage.Source = null;
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

    // ── create / manage / import ─────────────────────────────────────────────

    private async void OnCreateMod(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new CreateModDialog { Owner = _shell };
            if (dlg.ShowDialog() != true) return;

            // A jar picked in the create dialog can only be uploaded once the mod exists, so the
            // upload card opens here, pre-loaded, before the list is refreshed and the page opened.
            if (dlg.Created is { } newMod && dlg.FirstJarPath is { } jar)
            {
                try
                {
                    var detail = await App.State.Api.GetModAsync(newMod.Id);
                    await UploadModVersionDialog.ShowAsync(_shell, detail, jar);
                }
                catch (Exception ex) { SetDownloadError("The mod was created, but the upload failed: " + ex.Message); }
            }

            var personalChip = _chips.FirstOrDefault(c => c.Kind == ModBrowseSourceKind.CloudLauncherPersonal);
            if (personalChip is not null) _activeChip = personalChip;
            ApplyChipStyles();
            await ResetAndLoadAsync();

            if (dlg.Created is { } created)
            {
                var row = _rows.FirstOrDefault(r => r.Hosted?.Id == created.Id);
                if (row is not null) ResultsList.SelectedItem = row;
                _shell.OpenModDetail(created.Id, created.Name);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetDownloadError("Create failed: " + ex.Message); }
    }

    private async void OnImportMod(object sender, RoutedEventArgs e)
    {
        try
        {
            var pack = await PickTargetPackAsync("Import mod to...", "Pick the instance whose mods folder should receive this jar.", "Import");
            if (pack is null) return;

            var dlg = new OpenFileDialog
            {
                Title = "Select mod jar",
                Filter = "Mod jars (*.jar)|*.jar|All files|*",
                Multiselect = true
            };
            if (dlg.ShowDialog(_shell) != true) return;

            var folder = ModsFolderFor(pack);
            StatusLabel.SetResourceReference(ForegroundProperty, "TextSecondaryBrush");
            StatusLabel.Text = $"Importing {dlg.FileNames.Length} file(s) to {pack.Name}…";

            // Jars can be tens of megabytes and the source is often a network drive: copying on the
            // UI thread would freeze the launcher and the status line above would never paint.
            var files = dlg.FileNames;
            var copied = await Task.Run(() =>
            {
                var n = 0;
                foreach (var file in files)
                {
                    File.Copy(file, UniqueFilePath(folder, Path.GetFileName(file)));
                    n++;
                }
                return n;
            });

            StatusLabel.SetResourceReference(ForegroundProperty, "AccentBrush");
            StatusLabel.Text = copied == 1
                ? $"Imported {Path.GetFileName(dlg.FileNames[0])} to {pack.Name}."
                : $"Imported {copied} mods to {pack.Name}.";
        }
        catch (Exception ex)
        {
            SetListError("Import failed: " + ex.Message);
        }
    }

    private void OnManageHostedMod(object sender, RoutedEventArgs e)
    {
        if (_currentHostedMod is not null)
            _shell.OpenModDetail(_currentHostedMod.Id, _currentHostedMod.Name);
    }

    // ── row context menu ─────────────────────────────────────────────────────

    /// <summary>Right-click selects the row first, so the menu, the detail pane and the keyboard
    /// shortcuts are always talking about the same mod.</summary>
    private void OnRowRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ModBrowseRow row })
            ResultsList.SelectedItem = row;
    }

    /// <summary>Shows only the items that apply: owner actions for your own hosted mods, the store
    /// page for CurseForge/Modrinth rows, and nothing that would only ever return 403.</summary>
    private void OnRowMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        var row = menu.PlacementTarget is FrameworkElement { DataContext: ModBrowseRow r } ? r : null;
        if (row is null) return;

        var hosted = row.Hosted is not null;
        var owner = row.IsOwnedByMe;

        ApplyMenuGlyphs(menu);
        Show(menu, "CtxManage", hosted);
        Show(menu, "CtxInstall", row.IsInstallable);
        Show(menu, "CtxUploadVersion", owner);
        Show(menu, "CtxOwnerSeparator", owner);
        Show(menu, "CtxRename", owner);
        Show(menu, "CtxVisibility", owner);
        Show(menu, "CtxPermissions", owner);
        Show(menu, "CtxOpenPage", !hosted);
        Show(menu, "CtxCopyId", hosted);
        Show(menu, "CtxCopySlug", hosted);
        Show(menu, "CtxDeleteSeparator", owner);
        Show(menu, "CtxDelete", owner);

        // Tick the mod's current visibility so the submenu reads as state, not just as actions.
        // The app's MenuItem template draws no check box, so the tick goes in the gesture column —
        // the same convention ModOptionsMenu uses for its toggles.
        if (Find(menu, "CtxVisibility") is MenuItem visibility && row.Hosted is { } summary)
        {
            foreach (var item in visibility.Items.OfType<MenuItem>())
                item.InputGestureText = (item.Tag as string) == summary.Visibility.ToString() ? "✓" : "";
        }
    }

    /// <summary>Glyph codes for the row menu, in the same MDL2 vocabulary as ModOptionsMenu.</summary>
    private static readonly (string Name, int Glyph)[] MenuGlyphs =
    {
        ("CtxManage", 0xE7C3), ("CtxInstall", 0xE896), ("CtxUploadVersion", 0xE898),
        ("CtxRename", 0xE8AC), ("CtxVisibility", 0xE7B3), ("CtxPermissions", 0xE716),
        ("CtxOpenPage", 0xE774), ("CtxCopyId", 0xE8C8), ("CtxCopySlug", 0xE8C8),
        ("CtxDelete", 0xE74D)
    };

    /// <summary>Fills the menu's icon gutter once per menu instance (the items live in a
    /// DataTemplate, so each row gets its own copy the first time it is opened).</summary>
    private static void ApplyMenuGlyphs(ContextMenu menu)
    {
        foreach (var (name, glyph) in MenuGlyphs)
        {
            if (Find(menu, name) is not MenuItem { Icon: null } item) continue;
            var icon = new TextBlock
            {
                Text = char.ConvertFromUtf32(glyph),
                Width = 16,
                TextAlignment = TextAlignment.Center,
                FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            };
            // A resource reference, not an assigned brush: the menu outlives a theme change.
            icon.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            item.Icon = icon;
        }
    }

    private static FrameworkElement? Find(ContextMenu menu, string name) =>
        menu.Items.OfType<FrameworkElement>().FirstOrDefault(i => i.Name == name);

    private static void Show(ContextMenu menu, string name, bool visible)
    {
        if (Find(menu, name) is { } item)
            item.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The row a menu item belongs to, via the context menu's placement target.</summary>
    private static ModBrowseRow? RowFromMenuSender(object sender)
    {
        if (sender is not MenuItem mi) return (sender as FrameworkElement)?.DataContext as ModBrowseRow;
        var parent = mi.Parent;
        while (parent is MenuItem p) parent = p.Parent;
        return parent is ContextMenu { PlacementTarget: FrameworkElement { DataContext: ModBrowseRow row } }
            ? row
            : null;
    }

    private void OnCtxManage(object sender, RoutedEventArgs e)
    {
        if (RowFromMenuSender(sender)?.Hosted is { } mod) _shell.OpenModDetail(mod.Id, mod.Name);
    }

    private async void OnCtxInstall(object sender, RoutedEventArgs e)
    {
        if (_installing || RowFromMenuSender(sender) is not { } row) return;
        _installing = true;
        try { await InstallRowAsync(row); }
        finally { _installing = false; }
    }

    private async void OnCtxUploadVersion(object sender, RoutedEventArgs e)
    {
        if (RowFromMenuSender(sender)?.Hosted is not { } summary) return;
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

    private async void OnCtxRename(object sender, RoutedEventArgs e)
    {
        if (RowFromMenuSender(sender) is { } row) await RenameHostedModAsync(row);
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

    private async void OnCtxSetVisibility(object sender, RoutedEventArgs e)
    {
        if (RowFromMenuSender(sender)?.Hosted is not { } mod) return;
        if (sender is not MenuItem { Tag: string tag } || !Enum.TryParse<PackVisibility>(tag, out var visibility)) return;
        if (visibility == mod.Visibility) return;
        try
        {
            await App.State.Api.UpdateModAsync(mod.Id, new UpdateModRequest(null, null, null, visibility));
            SetDownloadSuccess($"{mod.Name} is now {visibility}.");
            await ResetAndLoadAsync();
        }
        catch (Exception ex) { SetDownloadError("Couldn't change visibility: " + ex.Message); }
    }

    private async void OnCtxPermissions(object sender, RoutedEventArgs e)
    {
        if (RowFromMenuSender(sender)?.Hosted is not { } mod) return;
        try
        {
            // PermissionsDialog needs the full detail (collaborators + teams), which the browse
            // summary does not carry.
            var detail = await App.State.Api.GetModAsync(mod.Id);
            new PermissionsDialog(detail) { Owner = _shell }.ShowDialog();
        }
        catch (Exception ex) { SetDownloadError("Couldn't open collaborators: " + ex.Message); }
    }

    private void OnCtxOpenPage(object sender, RoutedEventArgs e)
    {
        if (RowFromMenuSender(sender)?.External is { } mod) OpenUrl(BuildProjectUrl(mod));
    }

    private void OnCtxCopyId(object sender, RoutedEventArgs e)
    {
        if (RowFromMenuSender(sender)?.Hosted is not { } mod) return;
        SetDownloadSuccess(ClipboardHelper.TrySetText(mod.Id.ToString())
            ? "Copied the mod ID."
            : "The clipboard is in use by another program.");
    }

    private void OnCtxCopySlug(object sender, RoutedEventArgs e)
    {
        if (RowFromMenuSender(sender)?.Hosted is not { } mod) return;
        SetDownloadSuccess(ClipboardHelper.TrySetText(mod.Slug)
            ? $"Copied {mod.Slug}."
            : "The clipboard is in use by another program.");
    }

    private async void OnCtxDelete(object sender, RoutedEventArgs e)
    {
        if (RowFromMenuSender(sender) is { } row) await DeleteHostedModAsync(row);
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
            CountLabel.Text = _rows.Count == 0 ? "" : $"{_rows.Count} result{(_rows.Count == 1 ? "" : "s")}";
            UpdateEmptyState();
            SetDownloadSuccess($"Deleted {mod.Name}.");
        }
        catch (Exception ex) { SetDownloadError("Delete failed: " + ex.Message); }
    }

    /// <summary>Opens a version's stored changelog in the app's own dialog layer — the only place
    /// in the client that ever showed one was the launcher's own release notes.</summary>
    private async void OnShowChangelog(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: VersionDisplayRow row } || !row.HasChangelog) return;
        try
        {
            await AppDialog.MessageAsync(_shell, $"Changelog · {row.VersionNumber}", row.Changelog!);
        }
        catch (Exception ex) { SetDownloadError(ex.Message); }
    }

    // ── install/download ─────────────────────────────────────────────────────

    // Guards the three install entry points below so a download in flight can't be started
    // again (quick-install, install-selected, and per-version download could otherwise race
    // each other and drop two copies of the same jar in the mods folder).
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
        if (_installing) return;
        if (sender is not FrameworkElement { DataContext: VersionDisplayRow row }) return;
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

    private async Task InstallRowAsync(ModBrowseRow row)
    {
        try
        {
            if (row.External is not null)
                await InstallLatestExternalAsync(row.External);
            else if (row.Hosted is not null)
                await InstallLatestHostedAsync(row.Hosted.Id);
        }
        catch (Exception ex)
        {
            SetDownloadError("Install failed: " + ex.Message);
        }
    }

    private async Task InstallLatestExternalAsync(ModSummary mod)
    {
        DownloadStatus.Text = $"Finding compatible files for {mod.Name}...";
        var versions = await LoadVersionsForModAsync(mod, applyFilters: false, CancellationToken.None);
        if (versions.Count == 0)
            throw new InvalidOperationException("No downloadable versions found.");

        var pack = await PickTargetPackAsync(
            "Install mod to...",
            "Only profiles matching this mod's Minecraft version and loader are shown.",
            "Install",
            p => versions.Any(v => MatchesPack(v, p)),
            "No matching profile found. Create or edit a non-empty Fabric, Forge, or NeoForge profile with a Minecraft version supported by this mod.");
        if (pack is null) return;

        var version = PickBestVersion(versions, pack)
            ?? throw new InvalidOperationException($"No compatible version found for {PackCompatLabel(pack)}.");
        await InstallExternalVersionAsync(mod, version, pack);
    }

    private async Task InstallLatestHostedAsync(Guid modId)
    {
        var detail = _currentHostedMod?.Id == modId
            ? _currentHostedMod
            : await App.State.Api.GetModAsync(modId);
        if (detail.Versions.Count == 0)
            throw new InvalidOperationException("This hosted mod has no uploaded versions yet.");

        var pack = await PickTargetPackAsync(
            "Install mod to...",
            "Only profiles matching this hosted mod's Minecraft version and loader are shown.",
            "Install",
            p => detail.Versions.Any(v => MatchesPack(v, p, detail)),
            "No matching profile found. Create or edit a non-empty Fabric, Forge, or NeoForge profile supported by one of this hosted mod's versions.");
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
    /// The Mods screen used to drop the single chosen jar into mods/ and stop there, which left
    /// anything built on a library (Sodium, JEI, an Architectury mod) with an instance that refuses
    /// to start. This mirrors what the per-instance browser does: resolve the required graph first,
    /// skip files already present, and only then fetch. Dependencies are never flagged, and the root
    /// mod records the store it came from so later update checks follow the right project.
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
                App.State.ModMetadata.EffectiveUpdateChannel(pack.Id));

            if (downloads.Count == 0)
                throw new InvalidOperationException("No download URL available for this file.");

            // Count what is really missing first, so the progress below is not "3 of 12" where
            // nine were already in the folder.
            var pending = downloads
                .Where(item => !(item.IsDependency && File.Exists(Path.Combine(folder, item.File.Filename))))
                .ToList();
            var skipped = downloads.Count - pending.Count;

            var saved = 0;
            var rootFileName = "";
            for (var i = 0; i < pending.Count; i++)
            {
                var item = pending[i];
                var dest = UniqueFilePath(folder, item.File.Filename);
                DownloadStatus.Text = pending.Count == 1
                    ? $"Downloading {item.File.Filename}..."
                    : $"Downloading {item.File.Filename} ({i + 1} of {pending.Count})...";

                var index = i;
                var progress = new Progress<(long done, long total)>(p =>
                {
                    if (p.total <= 0) return;
                    DownloadProgress.IsIndeterminate = false;
                    // Each file owns its slice of the bar, so the bar never restarts mid-install.
                    DownloadProgress.Value = (index + (double)p.done / p.total) / pending.Count * 100;
                });
                await App.State.Modrinth.DownloadFileAsync(item.File.DownloadUrl, dest, progress);
                saved++;

                if (item.IsDependency) continue;
                rootFileName = item.File.Filename;
                RememberInstallSource(pack.Id, item);
            }

            DownloadProgress.Value = 100;
            var dependencies = saved - (string.IsNullOrEmpty(rootFileName) ? 0 : 1);
            var extras = new List<string>(2);
            if (dependencies > 0) extras.Add($"{dependencies} dependenc{(dependencies == 1 ? "y" : "ies")}");
            if (skipped > 0) extras.Add($"{skipped} already present");
            SetDownloadSuccess(
                $"Installed {(string.IsNullOrEmpty(rootFileName) ? mod.Name : rootFileName)} to {pack.Name}"
                + (extras.Count == 0 ? "." : $" ({string.Join(", ", extras)})."));
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
            // Provenance is a nicety — a metadata write failure must not fail the install.
        }
    }

    private async Task InstallHostedVersionAsync(HostedModDetail mod, HostedModVersionInfo version)
    {
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
        var dest = UniqueFilePath(folder, version.FileName);
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.IsIndeterminate = true;
        DownloadStatus.Text = $"Downloading {version.FileName}...";
        try
        {
            await using var stream = await App.State.Api.DownloadModVersionAsync(mod.Id, version.Id);
            await using var fs = File.Create(dest);
            await stream.CopyToAsync(fs);
            SetDownloadSuccess($"Installed {version.FileName} to {pack.Name}.");
        }
        finally
        {
            DownloadProgress.Visibility = Visibility.Collapsed;
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Value = 0;
        }
    }

    private async Task<PackSummary?> PickTargetPackAsync(
        string title,
        string description,
        string actionText,
        Func<PackSummary, bool>? canPick = null,
        string? noMatchMessage = null)
    {
        var packs = await App.State.Api.ListPacksAsync();
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

    private static string UniqueFilePath(string folder, string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string((fileName ?? "mod.jar").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(clean)) clean = "mod.jar";
        if (!clean.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) clean += ".jar";

        var candidate = Path.Combine(folder, clean);
        var stem = Path.GetFileNameWithoutExtension(clean);
        var ext = Path.GetExtension(clean);
        var n = 2;
        while (File.Exists(candidate))
            candidate = Path.Combine(folder, $"{stem}-{n++}{ext}");
        return candidate;
    }

    /// <summary>An error about the list itself (loading, importing) rather than about a download,
    /// so it does not overwrite what the detail pane is saying.</summary>
    private void SetListError(string message)
    {
        StatusLabel.Text = message;
        StatusLabel.SetResourceReference(ForegroundProperty, "DangerBrush");
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

    // ── external mod details helpers ─────────────────────────────────────────

    private async Task<List<ModVersion>> LoadVersionsForModAsync(ModSummary mod, bool applyFilters, CancellationToken ct)
    {
        var mc = applyFilters ? _filterMcVersion : null;
        var loader = applyFilters ? _filterLoader : null;
        return mod.Source == ModSource.CurseForge && int.TryParse(mod.Id, out var curseForgeId)
            ? (await App.State.CurseForge.GetVersionsAsync(curseForgeId, ct))
                .Where(v => !applyFilters || MatchesFilters(v, mc, loader))
                .ToList()
            : await App.State.Modrinth.GetVersionsAsync(mod.Id, mc, loader, ct);
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

    private void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { DownloadStatus.Text = ex.Message; }
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

    private void SetSelectedIcon(string? iconUrl)
    {
        SelectedIconImage.Source = null;
        if (string.IsNullOrWhiteSpace(iconUrl)) return;

        try
        {
            SelectedIconImage.Source = new BitmapImage(new Uri(iconUrl, UriKind.Absolute));
        }
        catch
        {
            SelectedIconImage.Source = null;
        }
    }

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

    // ── sorting ──────────────────────────────────────────────────────────────

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
            if (tag == _sort) { UpdateSortMenuState(); return; }
            _sort = tag;
            UpdateSortMenuState();
            // The order is the server's, so a re-sort is a fresh first page rather than a shuffle of
            // what is already loaded — otherwise "by name" would only sort the rows scrolled so far.
            await ResetAndLoadAsync();
        }
        catch (Exception ex) { AppLog.LogError("ModsView.Sort", ex); }
    }

    private void UpdateSortMenuState()
    {
        SortUpdatedMenuItem.IsChecked = _sort == ModBrowseSort.Updated;
        SortCreatedMenuItem.IsChecked = _sort == ModBrowseSort.Created;
        SortNameMenuItem.IsChecked = _sort == ModBrowseSort.Name;
        SortButton.ToolTip = $"Sort hosted mods: {SortLabel(_sort)}";
    }

    private static string SortLabel(string sort) => sort switch
    {
        ModBrowseSort.Created => "Newest",
        ModBrowseSort.Name => "Name",
        _ => "Recently updated"
    };

    /// <summary>Hides the sort control on the store chips.</summary>
    /// <remarks>CurseForge and Modrinth return their own relevance order for the page they were
    /// asked for; re-ordering those 25 rows would claim to sort a result set we only hold a slice
    /// of. The control comes back the moment a CloudLauncher chip is selected.</remarks>
    private void UpdateSortButtonVisibility()
    {
        var hosted = _activeChip?.Kind is ModBrowseSourceKind.CloudLauncherPublic
            or ModBrowseSourceKind.CloudLauncherPersonal
            or ModBrowseSourceKind.CloudLauncherShared
            or ModBrowseSourceKind.CloudLauncherTeam;
        SortButton.Visibility = hosted ? Visibility.Visible : Visibility.Collapsed;
        if (hosted) UpdateSortMenuState();
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

    // ── view models ──────────────────────────────────────────────────────────

    public sealed class ModBrowseRow
    {
        public ModSummary? External { get; init; }
        public HostedModSummary? Hosted { get; init; }
        public string Name { get; init; } = "";
        public string Summary { get; init; } = "";
        public string MetaLabel { get; init; } = "";
        public string? IconUrl { get; init; }
        public string Initial { get; init; } = "?";
        public string SourceBadge { get; init; } = "";
        public Brush SourceBadgeBackground { get; set; } = Brushes.Transparent;
        public Brush SourceBadgeForeground { get; set; } = Brushes.White;
        public bool IsInstallable { get; init; } = true;

        /// <summary>Why Install is greyed out, or what it will do. Shown on the disabled button too,
        /// so an empty hosted mod explains itself instead of looking broken.</summary>
        public string InstallToolTip { get; init; } = "Install the newest compatible version";

        /// <summary>Hover text for the whole row: the summary plus the compatibility line.</summary>
        public string RowToolTip { get; init; } = "";

        /// <summary>True when this is one of the signed-in user's own hosted mods — the owner-only
        /// half of the row's context menu.</summary>
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
            // A mod page with no uploaded jar has nothing to install: say so on the row instead of
            // letting the user pick an instance and only then meeting an error.
            var hasVersions = mod.VersionCount > 0;
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
                IsInstallable = hasVersions,
                InstallToolTip = hasVersions
                    ? "Install the newest compatible version into one of your instances"
                    : "Nothing to install yet — upload a version from Manage first",
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

        /// <summary>The version's own notes. Hosted mods store one per upload; the stores' own
        /// per-version notes are not part of <see cref="ModVersion"/>, so those rows have none.</summary>
        public string? Changelog { get; init; }

        public bool HasChangelog => !string.IsNullOrWhiteSpace(Changelog);

        public Visibility ChangelogButtonVisibility =>
            HasChangelog ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Row hover text — the first few lines of the changelog, so the grid answers
        /// "what changed?" without a click.</summary>
        public string? ChangelogToolTip => HasChangelog ? Truncate(Changelog!, 400) : null;

        private static string Truncate(string text, int max) =>
            text.Length <= max ? text : text[..max].TrimEnd() + "…";

        public static VersionDisplayRow FromExternal(ModVersion version) => new()
        {
            ExternalVersion = version,
            VersionNumber = version.VersionNumber,
            McVersions = string.Join(", ", version.GameVersions.Take(3)) + (version.GameVersions.Length > 3 ? "..." : ""),
            LoaderList = string.Join(", ", version.Loaders.Take(3)) + (version.Loaders.Length > 3 ? "..." : ""),
            ReleaseChannel = version.ReleaseChannel,
            DateLabel = version.DatePublished.LocalDateTime.ToString("yyyy-MM-dd"),
            SizeLabel = version.Files.FirstOrDefault()?.Size is long size ? FormatSize(size) : ""
        };

        public static VersionDisplayRow FromHosted(HostedModVersionInfo version) => new()
        {
            HostedVersion = version,
            VersionNumber = version.VersionString,
            McVersions = version.McVersionsCsv ?? "",
            LoaderList = version.LoadersCsv ?? "",
            ReleaseChannel = version.ReleaseChannel,
            DateLabel = version.PublishedAt.LocalDateTime.ToString("yyyy-MM-dd"),
            SizeLabel = FormatSize(version.FileSize),
            Changelog = version.Changelog
        };
    }
}
