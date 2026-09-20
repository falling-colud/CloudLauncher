using System.Collections.ObjectModel;
using System.IO;
using System.Diagnostics;
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

public partial class ResourcePackExplorerPage : Page
{
    private readonly MainWindow _shell;
    private readonly PackDetail _pack;
    private readonly ObservableCollection<ModResultRow> _rows = new();
    private readonly ObservableCollection<RpExplorerChipRow> _chips = new();
    private RpExplorerChipRow? _activeChip;
    private List<ModVersion> _allVersions = new();
    private ModSummary? _currentMod;
    private CancellationTokenSource _cts = new();

    private string _searchText = "";
    private string? _filterMcOverride;
    private List<string> _filterCategories = new();
    private int _categoryGeneration;

    /// <summary>0 = relevance, 1 = latest, 2 = downloads. Mapped per store when searching.</summary>
    private int _sortMode;

    private bool _isLoading;
    private bool _hasMore;
    private int _offset;
    private const int PageSize = 25;
    private const int MaxResults = 200;

    public ResourcePackExplorerPage(MainWindow shell, PackDetail pack)
    {
        InitializeComponent();
        _shell = shell;
        _pack = pack;
        HeroPackLabel.Text = $"Downloads land in this instance's resourcepacks/ folders · {PackVersionLabel()}";
        SourceStrip.ItemsSource = _chips;
        ResultsList.ItemsSource = _rows;
        ModTabs.SelectionChanged += OnModTabsChanged;

        Loaded += async (_, _) =>
        {
            await BuildChipsAsync();
            if (_activeChip is null && _chips.Count > 0)
                await SelectChipAsync(_chips[0]);
        };
    }

    // Render rich HTML/markdown descriptions in an embedded WebView2. Its HWND draws over
    // WPF (airspace), so hide it off-tab. Scrolling is native — no manual wheel routing.
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
        _chips.Add(NewChip("\uE753", "CurseForge", RpBrowseSourceKind.CurseForge));
        _chips.Add(NewChip("\uE753", "Modrinth", RpBrowseSourceKind.Modrinth));
        _chips.Add(NewDividerChip());
        _chips.Add(NewChip("\uE77B", "Personal", RpBrowseSourceKind.CloudLauncherPersonal));
        _chips.Add(NewChip("\uE8F2", "Shared", RpBrowseSourceKind.CloudLauncherShared));
        _chips.Add(NewChip("\uE7C3", "Public", RpBrowseSourceKind.CloudLauncherPublic));

        try
        {
            var teams = await App.State.Api.ListTeamsAsync();
            foreach (var t in teams)
                _chips.Add(NewChip("\uE716", t.Name, RpBrowseSourceKind.CloudLauncherTeam, t.Id));
        }
        catch { /* teams unavailable — proceed without */ }

        ApplyChipStyles();
    }

    private static RpExplorerChipRow NewChip(string icon, string label, RpBrowseSourceKind kind, Guid? teamId = null) =>
        new() { Icon = icon, Label = label, Kind = kind, TeamId = teamId };

    private static RpExplorerChipRow NewDividerChip() => new()
    {
        IsDivider = true,
        Icon = "",
        Label = "|",
        Kind = RpBrowseSourceKind.Divider,
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
        if (sender is FrameworkElement fe && fe.DataContext is RpExplorerChipRow row && !row.IsDivider)
            await SelectChipAsync(row);
    }

    private async Task SelectChipAsync(RpExplorerChipRow chip)
    {
        _activeChip = chip;
        ApplyChipStyles();
        // Categories belong to the store, so a chip change replaces the list and drops a selection
        // made against the previous one.
        _filterCategories = new();
        await RefreshCategoryFilterAsync();
        await ResetAndLoadAsync();
    }

    /// <summary>
    /// Loads the category list of whichever store the active chip points at.
    /// </summary>
    /// <remarks>CurseForge and Modrinth do not share a category vocabulary, so there is nothing to
    /// merge; the panel is simply hidden for the hosted chips, which have no categories, and for a
    /// store that answered with nothing because it is unreachable.</remarks>
    private async Task RefreshCategoryFilterAsync()
    {
        var generation = ++_categoryGeneration;
        List<ModBrowseCategory> categories;
        string sourceLabel;

        switch (_activeChip?.Kind)
        {
            case RpBrowseSourceKind.CurseForge:
                sourceLabel = "  ·  CurseForge";
                categories = await App.State.CurseForge.GetCategoriesAsync(CurseForgeService.ClassIdResourcePacks);
                break;
            case RpBrowseSourceKind.Modrinth:
                sourceLabel = "  ·  Modrinth";
                categories = await App.State.Modrinth.GetCategoriesAsync("resourcepack");
                break;
            default:
                CategoryFilterPanel.Visibility = Visibility.Collapsed;
                _filterCategories = new();
                return;
        }

        if (generation != _categoryGeneration) return; // a newer chip selection superseded this fetch

        CategorySourceLabel.Text = sourceLabel;
        FilterCategoryList.ItemsSource = categories
            .Select(c => new RpExplorerCategoryItem { Label = c.Label, Value = c.Value })
            .ToList();
        CategoryFilterPanel.Visibility = categories.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnClearCategories(object sender, RoutedEventArgs e)
    {
        if (FilterCategoryList.ItemsSource is not IEnumerable<RpExplorerCategoryItem> items) return;
        foreach (var item in items) item.IsChecked = false;
        // Plain objects with no change notification, so rebind to clear the boxes visually.
        var source = FilterCategoryList.ItemsSource;
        FilterCategoryList.ItemsSource = null;
        FilterCategoryList.ItemsSource = source;
    }

    private async void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            var mode = SortBox.SelectedIndex < 0 ? 0 : SortBox.SelectedIndex;
            if (mode == _sortMode) return;
            _sortMode = mode;
            if (_activeChip is not null) await ResetAndLoadAsync();
        }
        catch (Exception ex) { DownloadStatus.Text = "Error: " + ex.Message; }
    }

    /// <summary>CurseForge sort fields: 2 popularity, 3 last updated, 6 total downloads.</summary>
    private int CurseSortField() => _sortMode switch { 1 => 3, 2 => 6, _ => 2 };

    /// <summary>Modrinth's index values for the same three choices.</summary>
    private string ModrinthIndex() => _sortMode switch { 1 => "newest", 2 => "downloads", _ => "relevance" };

    // ── search + filters ─────────────────────────────────────────────────────

    // Search as you type (see ModExplorerPage): reload a moment after typing stops; Enter is immediate.
    private System.Windows.Threading.DispatcherTimer? _searchTimer;
    private string _lastSearched = "";

    private void OnSearchTextChanged(object s, TextChangedEventArgs e)
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

    private async void OnSearchKeyDown(object s, KeyEventArgs e)
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
            await ResetAndLoadAsync();
        }
    }

    private void OnFiltersClick(object sender, RoutedEventArgs e) => FiltersPopup.IsOpen = !FiltersPopup.IsOpen;

    private async void OnApplyFilters(object sender, RoutedEventArgs e)
    {
        try
        {
            _filterMcOverride = string.IsNullOrWhiteSpace(FilterMcBox.Text) ? null : FilterMcBox.Text.Trim();
            _filterCategories = CategoryFilterPanel.Visibility == Visibility.Visible
                                && FilterCategoryList.ItemsSource is IEnumerable<RpExplorerCategoryItem> items
                ? items.Where(i => i.IsChecked).Select(i => i.Value).ToList()
                : new();
            FiltersPopup.IsOpen = false;
            await ResetAndLoadAsync();
        }
        catch (Exception ex) { DownloadStatus.Text = "Error: " + ex.Message; }
    }

    private string EffectiveMcVersion() => _filterMcOverride ?? PackMinecraftVersion();

    // ── pagination / loading ─────────────────────────────────────────────────

    private async Task ResetAndLoadAsync()
    {
        _cts.Cancel(); _cts = new CancellationTokenSource();
        _offset = 0;
        _rows.Clear();
        _hasMore = true;
        SearchStatus.Text = "";
        ClearSelectedMod();
        await LoadMoreAsync(_cts.Token);
    }

    private async void OnResultsScroll(object sender, ScrollChangedEventArgs e)
    {
        if (_isLoading || !_hasMore || _activeChip is null) return;
        if (e.OriginalSource is ScrollViewer sv &&
            sv.VerticalOffset + sv.ViewportHeight >= sv.ExtentHeight - 80)
        {
            await LoadMoreAsync(_cts.Token);
        }
    }

    private async Task LoadMoreAsync(CancellationToken ct)
    {
        if (_activeChip is null || _isLoading || !_hasMore) return;
        if (_rows.Count >= MaxResults) { _hasMore = false; return; }

        _isLoading = true;
        SearchStatus.Text = _rows.Count == 0 ? "Searching…" : $"Loading more… ({_rows.Count})";
        try
        {
            int added;
            switch (_activeChip.Kind)
            {
                case RpBrowseSourceKind.CurseForge:
                    added = await LoadCurseForgeAsync(ct);
                    break;
                case RpBrowseSourceKind.Modrinth:
                    added = await LoadModrinthAsync(ct);
                    break;
                case RpBrowseSourceKind.CloudLauncherPersonal:
                    added = await LoadCloudLauncherAsync(ResourcePackBrowseSource.Personal, null, ct);
                    break;
                case RpBrowseSourceKind.CloudLauncherShared:
                    added = await LoadCloudLauncherAsync(ResourcePackBrowseSource.Shared, null, ct);
                    break;
                case RpBrowseSourceKind.CloudLauncherPublic:
                    added = await LoadCloudLauncherAsync(ResourcePackBrowseSource.Public, null, ct);
                    break;
                case RpBrowseSourceKind.CloudLauncherTeam:
                    added = await LoadCloudLauncherAsync(ResourcePackBrowseSource.Team, _activeChip.TeamId, ct);
                    break;
                default:
                    added = 0; _hasMore = false; break;
            }
            _offset += added;
            if (added == 0) _hasMore = false;
            SearchStatus.Text = _rows.Count == 0 && !_hasMore
                ? "No results. Try a different search or filter."
                : $"{_rows.Count} result{(_rows.Count == 1 ? "" : "s")}{(_hasMore ? " — scroll for more" : "")}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SearchStatus.Text = "Error: " + ex.Message;
            _hasMore = false;
        }
        finally
        {
            _isLoading = false;
        }
    }

    private async Task<int> LoadCurseForgeAsync(CancellationToken ct)
    {
        var mc = EffectiveMcVersion();
        var categoryIds = _filterCategories
            .Select(v => int.TryParse(v, out var id) ? id : 0)
            .Where(id => id > 0)
            .ToList();

        var hits = await App.State.CurseForge.SearchAsync(
            _searchText,
            mc.Length > 0 ? mc : null,
            loader: null,
            limit: PageSize, offset: _offset,
            classId: CurseForgeService.ClassIdResourcePacks,
            sortField: CurseSortField(),
            categoryIds: categoryIds.Count > 0 ? categoryIds : null,
            ct: ct);
        foreach (var m in hits) _rows.Add(new ModResultRow(m));
        return hits.Count;
    }

    private async Task<int> LoadModrinthAsync(CancellationToken ct)
    {
        var mc = EffectiveMcVersion();
        var hits = await App.State.Modrinth.SearchAsync(
            _searchText,
            mc.Length > 0 ? mc : null,
            loader: null,
            categories: _filterCategories.Count > 0 ? _filterCategories : null,
            limit: PageSize, offset: _offset, projectType: "resourcepack",
            index: ModrinthIndex(), ct: ct);
        foreach (var m in hits) _rows.Add(new ModResultRow(m));
        return hits.Count;
    }

    private readonly Dictionary<string, HostedResourcePackSummary> _hostedByRowId = new();

    private async Task<int> LoadCloudLauncherAsync(ResourcePackBrowseSource source, Guid? teamId, CancellationToken ct)
    {
        var mc = EffectiveMcVersion();
        var page = await App.State.Api.BrowseResourcePacksAsync(
            source, teamId, _searchText,
            mc.Length > 0 ? mc : null,
            offset: _offset, limit: PageSize, ct: ct);

        foreach (var m in page.Items)
        {
            var rowId = "cl:" + m.Id;
            _hostedByRowId[rowId] = m;
            var summary = new ModSummary(
                rowId, m.Slug, m.Name, m.OwnerUsername, m.Summary,
                m.DownloadCount, null, ModSource.External, Array.Empty<string>());
            _rows.Add(new ModResultRow(summary));
        }
        _hasMore = _offset + page.Items.Count < page.Total;
        return page.Items.Count;
    }

    private async void OnResultSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsList.SelectedItem is not ModResultRow row) return;
        if (TryGetHosted(row, out var hosted))
        {
            await LoadHostedResourcePackDetailAsync(hosted);
            return;
        }
        await LoadModDetailAsync(row.Source);
    }

    private async void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsList.SelectedItem is not ModResultRow row) return;
        if (TryGetHosted(row, out var hosted))
        {
            await LoadHostedResourcePackDetailAsync(hosted);
            return;
        }
        await LoadModDetailAsync(row.Source);
    }

    private bool TryGetHosted(ModResultRow row, out HostedResourcePackSummary hosted)
    {
        hosted = null!;
        return _hostedByRowId.TryGetValue(row.Source.Id, out hosted!);
    }

    private Guid? _currentHostedResourcePackId;

    private async Task LoadHostedResourcePackDetailAsync(HostedResourcePackSummary mod)
    {
        _cts.Cancel(); _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _currentMod = null;
        _currentHostedResourcePackId = mod.Id;
        DetailHeader.Visibility = Visibility.Visible;
        ModNameLabel.Text = mod.Name;
        ModMetaLabel.Text = $"by {mod.OwnerUsername} · CloudLauncher · {mod.Visibility}";
        SelectedIconFallback.Text = InitialFor(mod.Name);
        SetSelectedIcon(null);
        DetailPlaceholder.Visibility = Visibility.Collapsed;
        ModTabs.Visibility = Visibility.Visible;
        ModTabs.SelectedIndex = 0;
        ShowOverview(mod.Summary ?? "*No summary.*", isMarkdown: true);
        ScreenshotsEmptyText.Text = "Screenshots are not supported for hosted resource packs yet.";
        ScreenshotsEmptyText.Visibility = Visibility.Visible;
        ScreenshotList.ItemsSource = null;
        ScreenshotList.Visibility = Visibility.Collapsed;
        ConfigureLinks(new ModProjectLinks(null, null, null, null, null));
        try
        {
            var detail = await App.State.Api.GetResourcePackAsync(mod.Id, ct);
            ShowOverview(detail.Description ?? detail.Summary, isMarkdown: true);
            var hostedVersions = detail.Versions.Select(v => new VersionRow(new ModVersion(
                v.Id.ToString(),
                v.VersionString, v.VersionString,
                v.McVersionsCsv?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>(),
                Array.Empty<string>(),
                v.ReleaseChannel, v.PublishedAt, v.FileSize, v.Changelog, ModSource.External,
                new[] { new ModVersionFile(v.FileName, "", v.FileSize, null, true) },
                Array.Empty<ModDependency>()
            ))).ToList();
            VersionsGrid.ItemsSource = hostedVersions;
            VersionFilterNote.Text = $"{hostedVersions.Count} version(s)";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowOverview("Error: " + ex.Message); }
    }

    private async Task LoadModDetailAsync(ModSummary mod)
    {
        _cts.Cancel(); _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _currentMod = mod;
        _currentHostedResourcePackId = null;
        DetailHeader.Visibility = Visibility.Visible;
        ModNameLabel.Text = mod.Name;
        ModMetaLabel.Text = $"by {mod.Author ?? "unknown"} · {FormatNumber(mod.DownloadCount)} downloads · {mod.Source}";
        SelectedIconFallback.Text = InitialFor(mod.Name);
        SetSelectedIcon(mod.IconUrl);
        DetailPlaceholder.Visibility = Visibility.Collapsed;
        ModTabs.Visibility = Visibility.Visible;
        ModTabs.SelectedIndex = 0;
        ShowOverview("Loading…");
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

            _allVersions = await LoadVersionsForModAsync(mod, applyFilters: true, ct);

            ShowVersions(ShowAllVersionsBox.IsChecked == true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowOverview("Error: " + ex.Message); }
    }

    private void ShowVersions(bool all)
    {
        var mc = PackMinecraftVersion();
        var filtered = all ? _allVersions : _allVersions.Where(v => MatchesFilters(v, mc)).ToList();
        VersionFilterNote.Text = all ? $"(all {_allVersions.Count})" : $"(filtered {filtered.Count}/{_allVersions.Count})";
        VersionsGrid.ItemsSource = filtered.Select(v => new VersionRow(v)).ToList();
    }

    private void OnShowAllVersionsChanged(object s, RoutedEventArgs e) =>
        ShowVersions(ShowAllVersionsBox.IsChecked == true);

    private async void OnQuickDownload(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.DataContext is not ModResultRow row) return;
        await QuickDownloadAsync(row.Source);
    }

    private async void OnDownloadVersion(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.DataContext is not VersionRow row) return;

        if (_currentHostedResourcePackId is Guid hostedId)
        {
            await DownloadHostedVersionAsync(hostedId, row.Source);
            return;
        }

        if (_currentMod is null) return;
        await DownloadVersionAsync(_currentMod, row.Source);
    }

    private async Task QuickDownloadAsync(ModSummary mod)
    {
        DownloadStatus.Text = $"Finding latest compatible file for {mod.Name}...";
        try
        {
            if (!CanInstallResourcePacksToPack())
            {
                DownloadStatus.Text = "Pick a non-empty Minecraft version on this profile before installing resource packs.";
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

    private async Task DownloadVersionAsync(ModSummary mod, ModVersion version)
    {
        if (!CanInstallResourcePacksToPack())
        {
            DownloadStatus.Text = "Set a Minecraft version on this profile before installing resource packs.";
            return;
        }

        if (!MatchesFilters(version, PackMinecraftVersion()))
        {
            DownloadStatus.Text = $"{version.VersionNumber} does not support {PackVersionLabel()}.";
            return;
        }

        var folder = TargetFolder();
        Directory.CreateDirectory(folder);

        var file = version.Files.FirstOrDefault(f => f.IsPrimary) ?? version.Files.FirstOrDefault();
        if (file is null)
        {
            DownloadStatus.Text = "No downloadable file found.";
            return;
        }

        file = await ResolveDownloadFileAsync(mod, version, file) ?? file;
        if (string.IsNullOrWhiteSpace(file.DownloadUrl))
        {
            DownloadStatus.Text = "No download URL available.";
            return;
        }

        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.IsIndeterminate = true;
        DownloadStatus.Text = $"Downloading {file.Filename}...";
        try
        {
            var dest = UniqueZipPath(folder, file.Filename);
            await App.State.Modrinth.DownloadFileAsync(file.DownloadUrl, dest);

            // Which listing this file came from, recorded now: a resource pack carries no id of its
            // own once it is on disk, so without this it can never be matched back to the store and
            // offered an update.
            var key = ResourcePackService.Key(_pack.Id, Path.GetFileName(dest), OriginOfTarget());
            App.State.ResourcePacks.SetProvenance(key, mod.Source, mod.Id, version.Id, version.VersionNumber);
            App.State.ResourcePacks.Rename(key, mod.Name);

            DownloadStatus.Text = $"Saved to {dest}";
        }
        catch (Exception ex) { DownloadStatus.Text = "Download failed: " + ex.Message; }
        finally
        {
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async Task DownloadHostedVersionAsync(Guid hostedId, ModVersion version)
    {
        if (!Guid.TryParse(version.Id, out var versionId))
        {
            DownloadStatus.Text = "Invalid hosted version id.";
            return;
        }

        var folder = TargetFolder();
        Directory.CreateDirectory(folder);
        var fileName = version.Files.FirstOrDefault()?.Filename ?? "resourcepack.zip";

        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.IsIndeterminate = true;
        DownloadStatus.Text = $"Downloading {fileName}...";
        try
        {
            var dest = UniqueZipPath(folder, fileName);
            await using (var stream = await App.State.Api.DownloadResourcePackVersionAsync(hostedId, versionId))
            await using (var fs = File.Create(dest))
                await stream.CopyToAsync(fs);

            // The hosting link is this pack's provenance: it is what lets the launcher recognise the
            // zip on disk as a copy of that hosted pack later.
            App.State.ResourcePacks.LinkHostedResourcePack(
                ResourcePackService.Key(_pack.Id, Path.GetFileName(dest), OriginOfTarget()), hostedId);

            DownloadStatus.Text = $"Saved to {dest}";
        }
        catch (Exception ex) { DownloadStatus.Text = "Download failed: " + ex.Message; }
        finally
        {
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Which of the instance's two resourcepacks/ folders the download target combo points
    /// at — the settings key differs between them, so a pack in local/ is not confused with a
    /// same-named one in game/.</summary>
    private ResourcePackOrigin OriginOfTarget() =>
        (DownloadTargetBox.SelectedItem as ComboBoxItem)?.Content as string == "local/resourcepacks/"
            ? ResourcePackOrigin.Local
            : ResourcePackOrigin.Game;

    private string TargetFolder()
    {
        var target = (DownloadTargetBox.SelectedItem as ComboBoxItem)?.Content as string ?? "game/resourcepacks/";
        return target switch
        {
            "local/resourcepacks/" => Path.Combine(App.State.Packs.LocalDir(_pack.Id), "resourcepacks"),
            _ => Path.Combine(App.State.Packs.GameDir(_pack.Id), "resourcepacks")
        };
    }

    private static string UniqueZipPath(string folder, string fileName)
    {
        var clean = string.IsNullOrWhiteSpace(fileName) ? "resourcepack.zip" : fileName;
        if (!clean.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) clean += ".zip";
        var candidate = Path.Combine(folder, clean);
        var stem = Path.GetFileNameWithoutExtension(clean);
        var n = 2;
        while (File.Exists(candidate))
            candidate = Path.Combine(folder, $"{stem}-{n++}.zip");
        return candidate;
    }

    private async Task<List<ModVersion>> LoadVersionsForModAsync(ModSummary mod, bool applyFilters, CancellationToken ct)
    {
        var mcFilter = applyFilters ? PackMinecraftVersion() : "";
        return mod.Source == ModSource.CurseForge
            ? await App.State.CurseForge.GetVersionsAsync(int.TryParse(mod.Id, out var cid) ? cid : 0, ct)
            : await App.State.Modrinth.GetVersionsAsync(
                mod.Id,
                mcFilter.Length > 0 ? mcFilter : null,
                loader: null,
                ct);
    }

    private async Task<ModVersionFile?> ResolveDownloadFileAsync(ModSummary mod, ModVersion version, ModVersionFile selected)
    {
        if (!string.IsNullOrWhiteSpace(selected.DownloadUrl)) return selected;

        if (version.Source != ModSource.CurseForge) return selected;

        var ids = version.Id.Split(':', 2);
        var modId = ids.Length == 2 && int.TryParse(ids[0], out var parsedModId)
            ? parsedModId
            : int.TryParse(mod.Id, out var fallbackModId) ? fallbackModId : 0;
        var fileId = ids.Length == 2 && int.TryParse(ids[1], out var parsedFileId) ? parsedFileId : 0;
        if (modId == 0 || fileId == 0) return selected;

        var url = await App.State.CurseForge.GetDownloadUrlAsync(modId, fileId);
        return string.IsNullOrWhiteSpace(url) ? selected : selected with { DownloadUrl = url };
    }

    private ModVersion? PickBestVersion(IReadOnlyList<ModVersion> versions)
    {
        if (!CanInstallResourcePacksToPack()) return null;

        var mc = PackMinecraftVersion();
        var compatible = versions.Where(v => MatchesFilters(v, mc)).ToList();
        return compatible.FirstOrDefault(v => string.Equals(v.ReleaseChannel, "release", StringComparison.OrdinalIgnoreCase))
            ?? compatible.FirstOrDefault();
    }

    private static bool MatchesFilters(ModVersion version, string mc) =>
        mc.Length == 0 || version.GameVersions.Contains(mc, StringComparer.OrdinalIgnoreCase);

    private string PackMinecraftVersion() => _pack.MinecraftVersion?.Trim() ?? "";

    private bool CanInstallResourcePacksToPack() =>
        !_pack.IsEmpty && !string.IsNullOrWhiteSpace(_pack.MinecraftVersion);

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
        ScreenshotsEmptyText.Text = "No screenshots are available for this resource pack.";
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

    private void ClearSelectedMod()
    {
        _currentMod = null;
        _currentHostedResourcePackId = null;
        _allVersions.Clear();
        DetailHeader.Visibility = Visibility.Collapsed;
        DetailPlaceholder.Visibility = Visibility.Visible;
        ModTabs.Visibility = Visibility.Collapsed;
        ModNameLabel.Text = "";
        ModMetaLabel.Text = "";
        SelectedIconFallback.Text = "";
        SelectedIconImage.Source = null;
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
            ? $"https://www.curseforge.com/minecraft/texture-packs/{slug}"
            : $"https://modrinth.com/resourcepack/{slug}";
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

public enum RpBrowseSourceKind
{
    CurseForge,
    Modrinth,
    Divider,
    CloudLauncherPersonal,
    CloudLauncherShared,
    CloudLauncherPublic,
    CloudLauncherTeam
}

public sealed class RpExplorerChipRow
{
    public string Icon { get; set; } = "";
    public string Label { get; set; } = "";
    public RpBrowseSourceKind Kind { get; set; }
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

/// <summary>One checkable store category in the resource pack explorer's filter popup.</summary>
public sealed class RpExplorerCategoryItem
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public bool IsChecked { get; set; }
}
