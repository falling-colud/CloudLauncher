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
        };
        Unloaded += (_, _) =>
        {
            if (Window.GetWindow(this) is Window w)
                w.PreviewKeyDown -= OnShellKeyDown;
            _cts.Cancel();
        };
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
        if (sender is FrameworkElement fe && fe.DataContext is ModSourceChipRow row && !row.IsDivider)
            await SelectChipAsync(row);
    }

    private async Task SelectChipAsync(ModSourceChipRow chip)
    {
        _activeChip = chip;
        ApplyChipStyles();
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
        _filterMcVersion = string.IsNullOrWhiteSpace(FilterMcBox.Text) ? null : FilterMcBox.Text.Trim();
        var loaderItem = FilterLoaderBox.SelectedItem as ComboBoxItem;
        var loader = loaderItem?.Content as string;
        _filterLoader = string.IsNullOrWhiteSpace(loader) || loader == "(any)" ? null : loader;
        FiltersPopup.IsOpen = false;
        await ResetAndLoadAsync();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await ResetAndLoadAsync();

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
        if (e.OriginalSource is ScrollViewer sv &&
            sv.VerticalOffset + sv.ViewportHeight >= sv.ExtentHeight - 200)
        {
            await LoadMoreAsync(_cts.Token);
        }
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
            StatusLabel.Foreground = (Brush)FindResource("DangerBrush");
            StatusLabel.Text = "Error: " + ex.Message;
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
            offset: _offset, limit: PageSize, ct: ct);
        foreach (var mod in page.Items)
            _rows.Add(ModBrowseRow.FromHosted(mod, (Brush)FindResource("AccentSoftBrush"), (Brush)FindResource("AccentBrush")));
        _hasMore = _offset + page.Items.Count < page.Total;
        CountLabel.Text = page.Total == 0 ? "" : $"{Math.Min(_offset + page.Items.Count, page.Total)} of {page.Total}";
        return page.Items.Count;
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
        if (row.Hosted is not null) await LoadHostedModDetailAsync(row.Hosted.Id);
        else if (row.External is not null) await LoadExternalModDetailAsync(row.External);
    }

    private async void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsList.SelectedItem is ModBrowseRow row)
            await InstallRowAsync(row);
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
            ModMetaLabel.Text = $"by {detail.OwnerUsername} · CloudLauncher · {detail.Visibility}";
            SelectedIconFallback.Text = InitialFor(detail.Name);
            SetSelectedIcon(null);
            OpenProjectButton.Visibility = Visibility.Collapsed;
            ManageHostedButton.Visibility = detail.OwnerId == App.State.Settings.UserId
                ? Visibility.Visible : Visibility.Collapsed;
            InstallSelectedButton.Visibility = Visibility.Visible;
            InstallSelectedButton.IsEnabled = detail.Versions.Count > 0;

            ModTabs.SelectedIndex = 0;
            ShowOverview(detail.Description ?? detail.Summary, isMarkdown: true);
            ScreenshotsEmptyText.Text = "Hosted mod screenshots are managed from the mod's detail page when supported by the server.";
            ScreenshotsEmptyText.Visibility = Visibility.Visible;
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
        ModTabs.SelectedIndex = 0;
        ShowOverview("Loading…");
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
        var dlg = new CreateModDialog { Owner = _shell };
        if (dlg.ShowDialog() != true) return;

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
            var copied = 0;
            foreach (var file in dlg.FileNames)
            {
                var dest = UniqueFilePath(folder, Path.GetFileName(file));
                File.Copy(file, dest);
                copied++;
            }

            StatusLabel.Foreground = (Brush)FindResource("AccentBrush");
            StatusLabel.Text = copied == 1
                ? $"Imported {Path.GetFileName(dlg.FileNames[0])} to {pack.Name}."
                : $"Imported {copied} mods to {pack.Name}.";
        }
        catch (Exception ex)
        {
            StatusLabel.Foreground = (Brush)FindResource("DangerBrush");
            StatusLabel.Text = "Import failed: " + ex.Message;
        }
    }

    private void OnManageHostedMod(object sender, RoutedEventArgs e)
    {
        if (_currentHostedMod is not null)
            _shell.OpenModDetail(_currentHostedMod.Id, _currentHostedMod.Name);
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

    private async Task InstallExternalVersionAsync(ModSummary mod, ModVersion version, PackSummary pack)
    {
        var file = version.Files.FirstOrDefault(f => f.IsPrimary) ?? version.Files.FirstOrDefault()
            ?? throw new InvalidOperationException("No file found for this version.");
        file = await ResolveDownloadFileAsync(mod, version, file);
        if (string.IsNullOrWhiteSpace(file.DownloadUrl))
            throw new InvalidOperationException("No download URL available for this file.");

        var folder = ModsFolderFor(pack);
        var dest = UniqueFilePath(folder, file.Filename);
        await DownloadFileToAsync(file.DownloadUrl, dest);
        SetDownloadSuccess($"Installed {file.Filename} to {pack.Name}.");
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

    private async Task<ModVersionFile> ResolveDownloadFileAsync(ModSummary mod, ModVersion version, ModVersionFile file)
    {
        if (version.Source != ModSource.CurseForge || !string.IsNullOrWhiteSpace(file.DownloadUrl))
            return file;

        var ids = version.Id.Split(':', 2);
        var modId = ids.Length == 2 && int.TryParse(ids[0], out var parsedModId)
            ? parsedModId
            : int.TryParse(mod.Id, out var fallbackModId) ? fallbackModId : 0;
        var fileId = ids.Length == 2 && int.TryParse(ids[1], out var parsedFileId) ? parsedFileId : 0;
        if (modId == 0 || fileId == 0) return file;

        var url = await App.State.CurseForge.GetDownloadUrlAsync(modId, fileId);
        return string.IsNullOrWhiteSpace(url) ? file : file with { DownloadUrl = url };
    }

    private async Task DownloadFileToAsync(string url, string destPath)
    {
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.IsIndeterminate = true;
        DownloadProgress.Value = 0;
        DownloadStatus.Text = $"Downloading {Path.GetFileName(destPath)}...";

        try
        {
            var progress = new Progress<(long done, long total)>(p =>
            {
                if (p.total <= 0) return;
                DownloadProgress.IsIndeterminate = false;
                DownloadProgress.Value = (double)p.done / p.total * 100;
            });
            await App.State.Modrinth.DownloadFileAsync(url, destPath, progress);
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
            MessageBox.Show(_shell, "Create an instance before installing or importing mods.",
                "No instance", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        var candidates = canPick is null ? packs : packs.Where(canPick).ToList();
        if (candidates.Count == 0)
        {
            MessageBox.Show(_shell, noMatchMessage ?? "No compatible instance was found.",
                "No compatible instance", MessageBoxButton.OK, MessageBoxImage.Information);
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

    private void SetDownloadSuccess(string message)
    {
        DownloadStatus.Foreground = (Brush)FindResource("AccentBrush");
        DownloadStatus.Text = message;
        StatusLabel.Foreground = (Brush)FindResource("AccentBrush");
        StatusLabel.Text = message;
    }

    private void SetDownloadError(string message)
    {
        DownloadStatus.Foreground = (Brush)FindResource("DangerBrush");
        DownloadStatus.Text = message;
        StatusLabel.Foreground = (Brush)FindResource("DangerBrush");
        StatusLabel.Text = message;
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
        public Brush SourceBadgeBackground { get; init; } = Brushes.Transparent;
        public Brush SourceBadgeForeground { get; init; } = Brushes.White;
        public bool IsInstallable { get; init; } = true;

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
            SourceBadgeForeground = badgeForeground
        };

        public static ModBrowseRow FromHosted(HostedModSummary mod, Brush badgeBackground, Brush badgeForeground) => new()
        {
            Hosted = mod,
            Name = mod.Name,
            Summary = string.IsNullOrWhiteSpace(mod.Summary) ? "(no summary)" : mod.Summary!,
            MetaLabel = $"by {mod.OwnerUsername}{FormatCompat(mod.McVersionsCsv, mod.LoadersCsv)}",
            Initial = InitialFor(mod.Name),
            SourceBadge = mod.Visibility.ToString(),
            SourceBadgeBackground = badgeBackground,
            SourceBadgeForeground = badgeForeground
        };

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
            SizeLabel = FormatSize(version.FileSize)
        };
    }
}
