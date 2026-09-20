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

namespace CloudLauncher.Views;

public partial class ResourcePackBrowserView : Page
{
    private readonly MainWindow _shell;
    private readonly ObservableCollection<RpSourceChipRow> _chips = new();
    private readonly ObservableCollection<RpBrowseRow> _rows = new();
    private RpSourceChipRow? _activeChip;
    private RpBrowseRow? _currentRow;
    private string? _currentProjectUrl;
    private string _searchText = "";
    private string? _filterMcVersion;
    private List<string> _filterCategories = new();
    private int _categoryGeneration;

    /// <summary>0 = relevance, 1 = latest, 2 = downloads. Mapped per store when searching.</summary>
    private int _sortMode;

    /// <summary>Display names and file stems of every pack already installed anywhere, so a row can
    /// say "Installed" instead of quietly producing a second copy.</summary>
    private HashSet<string> _installedNames = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource _cts = new();
    private bool _isLoading;
    private bool _hasMore;
    private int _offset;
    private const int PageSize = 25;
    private const int MaxResults = 200;

    public ResourcePackBrowserView(MainWindow shell)
    {
        InitializeComponent();
        _shell = shell;
        SourceStrip.ItemsSource = _chips;
        ResultsList.ItemsSource = _rows;
        PackTabs.SelectionChanged += OnPackTabsChanged;
        Loaded += async (_, _) =>
        {
            if (Window.GetWindow(this) is Window w) w.PreviewKeyDown += OnShellKeyDown;
            await InitAsync();
        };
        Unloaded += (_, _) =>
        {
            if (Window.GetWindow(this) is Window w) w.PreviewKeyDown -= OnShellKeyDown;
            _cts.Cancel();
        };
    }

    /// <summary>Ctrl+F focuses search and F5 re-runs it, the same two keys every other list in the
    /// app answers to.</summary>
    private async void OnShellKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsVisible) return;
        try
        {
            if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.F5)
            {
                e.Handled = true;
                await ResetAndLoadAsync();
            }
        }
        catch (Exception ex) { Fail("Refresh failed: " + ex.Message); }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshInstalledIndexAsync();
            await ResetAndLoadAsync();
        }
        catch (Exception ex) { Fail("Refresh failed: " + ex.Message); }
    }

    private void Note(string text)
    {
        StatusLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        StatusLabel.Text = text;
    }

    private void Okay(string text)
    {
        StatusLabel.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        StatusLabel.Text = text;
    }

    private void Fail(string text)
    {
        StatusLabel.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        StatusLabel.Text = text;
    }

    /// <summary>
    /// Builds the set of pack names already installed across every instance.
    /// </summary>
    /// <remarks>One scan, reused for every result row. It runs off the UI thread because it walks
    /// each instance's resourcepacks/ folder, and a failure is not worth surfacing: the worst case is
    /// that a row does not say "Installed".</remarks>
    private async Task RefreshInstalledIndexAsync()
    {
        try
        {
            var packs = await App.State.Api.ListPacksAsync();
            _installedNames = await Task.Run(() => App.State.ResourcePacks.ScanAll(packs)
                .SelectMany(i => new[] { i.DisplayName, Path.GetFileNameWithoutExtension(i.FileName) })
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToHashSet(StringComparer.OrdinalIgnoreCase));
            MarkInstalledRows();
        }
        catch { /* the marker is a nicety, never a blocker */ }
    }

    private void MarkInstalledRows()
    {
        var changed = false;
        foreach (var row in _rows) changed |= row.ApplyInstalled(_installedNames.Contains(row.Title));
        if (changed) ResultsList.Items.Refresh();
    }

    // Render rich HTML/markdown descriptions in an embedded WebView2. Its HWND draws over
    // WPF (airspace), so hide it off-tab. Scrolling is native — no manual wheel routing.
    private void OnPackTabsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != PackTabs) return;
        var onOverview = PackTabs.SelectedIndex == 0;
        OverviewHost.Visibility = onOverview ? Visibility.Visible : Visibility.Collapsed;
        OverviewBrowser.Visibility = onOverview ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowOverview(string? content, bool isMarkdown = false) => OverviewBrowser.Show(content, isMarkdown);

    private async Task InitAsync()
    {
        await RefreshInstalledIndexAsync();
        await BuildChipsAsync();
        if (_activeChip is null && _chips.Count > 0)
            await SelectChipAsync(_chips.First(c => !c.IsDivider));
    }

    private async Task BuildChipsAsync()
    {
        _chips.Clear();
        _chips.Add(NewChip("CurseForge", RpBrowseSourceKind.CurseForge));
        _chips.Add(NewDivider());
        _chips.Add(NewChip("Modrinth", RpBrowseSourceKind.Modrinth));
        _chips.Add(NewDivider());
        _chips.Add(NewChip("CloudLauncher", RpBrowseSourceKind.CloudLauncherPublic));
        _chips.Add(NewChip("Personal packs", RpBrowseSourceKind.CloudLauncherPersonal));
        _chips.Add(NewChip("Shared with me", RpBrowseSourceKind.CloudLauncherShared));
        _chips.Add(NewChip("All teams", RpBrowseSourceKind.CloudLauncherTeam));

        try
        {
            var teams = await App.State.Api.ListTeamsAsync();
            foreach (var t in teams.OrderBy(t => t.Name))
                _chips.Add(NewChip(t.Name, RpBrowseSourceKind.CloudLauncherTeam, t.Id));
        }
        catch { /* ignore */ }

        ApplyChipStyles();
    }

    private static RpSourceChipRow NewChip(string label, RpBrowseSourceKind kind, Guid? teamId = null) =>
        new() { Label = label, Kind = kind, TeamId = teamId };

    private static RpSourceChipRow NewDivider() => new()
    {
        IsDivider = true, Label = "|", ChipVisibility = Visibility.Visible,
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
                continue;
            }
            var isActive = c == _activeChip;
            c.Background = (Brush)FindResource(isActive ? "AccentBrush" : "Surface2Brush");
            c.BorderColor = (Brush)FindResource(isActive ? "AccentBrush" : "BorderBrush");
            c.Foreground = (Brush)FindResource(isActive ? "TextOnAccentBrush" : "TextSecondaryBrush");
            c.FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal;
        }
        SourceStrip.Items.Refresh();
    }

    private async void OnSourceChipClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is RpSourceChipRow row && !row.IsDivider)
            await SelectChipAsync(row);
    }

    private async Task SelectChipAsync(RpSourceChipRow chip)
    {
        _activeChip = chip;
        ApplyChipStyles();
        // Categories belong to the store, so switching chip replaces the list and drops any
        // selection made against the previous store.
        _filterCategories = new();
        await RefreshCategoryFilterAsync();
        await ResetAndLoadAsync();
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

    private async void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; _searchTimer?.Stop(); _lastSearched = _searchText.Trim(); await ResetAndLoadAsync(); }
        else if (e.Key == Key.Escape) { SearchBox.Text = ""; _searchText = ""; _searchTimer?.Stop(); _lastSearched = ""; await ResetAndLoadAsync(); }
    }

    private void OnFiltersClick(object sender, RoutedEventArgs e) => FiltersPopup.IsOpen = !FiltersPopup.IsOpen;

    private async void OnApplyFilters(object sender, RoutedEventArgs e)
    {
        try
        {
            _filterMcVersion = string.IsNullOrWhiteSpace(FilterMcBox.Text) ? null : FilterMcBox.Text.Trim();
            _filterCategories = CategoryFilterPanel.Visibility == Visibility.Visible
                                && FilterCategoryList.ItemsSource is IEnumerable<RpCategoryFilterItem> items
                ? items.Where(i => i.IsChecked).Select(i => i.Value).ToList()
                : new();
            FiltersPopup.IsOpen = false;
            await ResetAndLoadAsync();
        }
        catch (Exception ex) { Fail("Error: " + ex.Message); }
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
        catch (Exception ex) { Fail("Error: " + ex.Message); }
    }

    private void OnClearCategories(object sender, RoutedEventArgs e)
    {
        if (FilterCategoryList.ItemsSource is not IEnumerable<RpCategoryFilterItem> items) return;
        foreach (var item in items) item.IsChecked = false;
        // The items are plain objects with no change notification, so rebind to clear the boxes.
        var source = FilterCategoryList.ItemsSource;
        FilterCategoryList.ItemsSource = null;
        FilterCategoryList.ItemsSource = source;
    }

    /// <summary>
    /// Loads the category list belonging to whichever store the active chip points at.
    /// </summary>
    /// <remarks>The two stores' category vocabularies do not line up, so there is no merged list to
    /// show — the panel is hidden entirely for the hosted chips, which have no categories at all, and
    /// for a store that returned nothing because it is unreachable.</remarks>
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
            .Select(c => new RpCategoryFilterItem { Label = c.Label, Value = c.Value })
            .ToList();
        CategoryFilterPanel.Visibility = categories.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task ResetAndLoadAsync()
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        _offset = 0;
        _rows.Clear();
        _hasMore = true;
        StatusLabel.Text = "";
        EmptyState.Visibility = Visibility.Collapsed;
        CountLabel.Text = "";
        ClearDetail();
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
                case RpBrowseSourceKind.CloudLauncherPublic:
                    added = await LoadHostedAsync(ResourcePackBrowseSource.Public, null, ct);
                    break;
                case RpBrowseSourceKind.CloudLauncherPersonal:
                    added = await LoadHostedAsync(ResourcePackBrowseSource.Personal, null, ct);
                    break;
                case RpBrowseSourceKind.CloudLauncherShared:
                    added = await LoadHostedAsync(ResourcePackBrowseSource.Shared, null, ct);
                    break;
                case RpBrowseSourceKind.CloudLauncherTeam:
                    added = await LoadHostedAsync(ResourcePackBrowseSource.Team, _activeChip.TeamId, ct);
                    break;
                default:
                    added = 0;
                    _hasMore = false;
                    break;
            }
            _offset += added;
            if (added == 0) _hasMore = false;
            CountLabel.Text = _rows.Count == 0 ? "" : $"{_rows.Count} result{(_rows.Count == 1 ? "" : "s")}";
            UpdateEmpty();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusLabel.Text = "Error: " + ex.Message; _hasMore = false; }
        finally { _isLoading = false; }
    }

    private void UpdateEmpty()
    {
        if (_rows.Count > 0 || _hasMore || _isLoading) { EmptyState.Visibility = Visibility.Collapsed; return; }
        EmptyState.Visibility = Visibility.Visible;
        EmptyTitle.Text = "No resource packs found";
        EmptyDetail.Text = _activeChip?.Kind switch
        {
            RpBrowseSourceKind.CloudLauncherShared => "Nobody has shared a hosted pack with you yet.",
            RpBrowseSourceKind.CloudLauncherPersonal => "Create a hosted pack from CloudLauncher to browse it here.",
            RpBrowseSourceKind.CloudLauncherTeam => _activeChip?.TeamId is null ? "Pick a specific team chip to browse hosted packs." : "This team has no hosted packs yet.",
            _ => "Try another search chip or widen the Minecraft filter."
        };
    }

    /// <summary>CurseForge sort fields: 2 popularity, 3 last updated, 6 total downloads.</summary>
    private int CurseSortField() => _sortMode switch { 1 => 3, 2 => 6, _ => 2 };

    /// <summary>Modrinth's index values for the same three choices.</summary>
    private string ModrinthIndex() => _sortMode switch { 1 => "newest", 2 => "downloads", _ => "relevance" };

    private async Task<int> LoadCurseForgeAsync(CancellationToken ct)
    {
        var categoryIds = _filterCategories
            .Select(v => int.TryParse(v, out var id) ? id : 0)
            .Where(id => id > 0)
            .ToList();

        var hits = await App.State.CurseForge.SearchAsync(
            _searchText, _filterMcVersion, loader: null,
            limit: PageSize, offset: _offset,
            classId: CurseForgeService.ClassIdResourcePacks,
            sortField: CurseSortField(),
            categoryIds: categoryIds.Count > 0 ? categoryIds : null,
            ct: ct);
        foreach (var m in hits) _rows.Add(RowFromCurseExternal(m));
        MarkInstalledRows();
        return hits.Count;
    }

    private async Task<int> LoadModrinthAsync(CancellationToken ct)
    {
        var hits = await App.State.Modrinth.SearchAsync(
            _searchText, _filterMcVersion, loader: null,
            categories: _filterCategories.Count > 0 ? _filterCategories : null,
            limit: PageSize, offset: _offset, projectType: "resourcepack",
            index: ModrinthIndex(), ct: ct);
        foreach (var m in hits) _rows.Add(RowFromMrExternal(m));
        MarkInstalledRows();
        return hits.Count;
    }

    private async Task<int> LoadHostedAsync(ResourcePackBrowseSource source, Guid? teamId, CancellationToken ct)
    {
        var page = await App.State.Api.BrowseResourcePacksAsync(
            source, teamId, _searchText, _filterMcVersion,
            offset: _offset, limit: PageSize, ct: ct);
        foreach (var w in page.Items) _rows.Add(RowFromHosted(w));
        _hasMore = _offset + page.Items.Count < page.Total;
        return page.Items.Count;
    }

    private static RpBrowseRow RowFromCurseExternal(ModSummary m) => new()
    {
        Kind = RpBrowseRowKind.External,
        External = m,
        Title = m.Name,
        Subtitle = m.Description ?? "",
        MetaLabel = $"by {m.Author ?? "unknown"} · {FormatNumber(m.DownloadCount)} downloads",
        IconUrl = m.IconUrl,
        Initial = InitialFor(m.Name),
        SourceBadge = "CurseForge",
        SourceBadgeBackground = new SolidColorBrush(Color.FromRgb(0xF1, 0x65, 0x36)),
        SourceBadgeForeground = Brushes.White
    };

    private static RpBrowseRow RowFromMrExternal(ModSummary m) => new()
    {
        Kind = RpBrowseRowKind.External,
        External = m,
        Title = m.Name,
        Subtitle = m.Description ?? "",
        MetaLabel = $"by {m.Author ?? "unknown"} · {FormatNumber(m.DownloadCount)} downloads",
        IconUrl = m.IconUrl,
        Initial = InitialFor(m.Name),
        SourceBadge = "Modrinth",
        SourceBadgeBackground = new SolidColorBrush(Color.FromRgb(0x1B, 0xD9, 0x6A)),
        SourceBadgeForeground = Brushes.White
    };

    private RpBrowseRow RowFromHosted(HostedResourcePackSummary w) => new()
    {
        Kind = RpBrowseRowKind.CloudLauncher,
        Hosted = w,
        Title = w.Name,
        Subtitle = w.Summary ?? "",
        MetaLabel = $"by {w.OwnerUsername}"
                   + (string.IsNullOrWhiteSpace(w.McVersionsCsv)
                       ? ""
                       : $" · MC {w.McVersionsCsv.Split(',').FirstOrDefault()?.Trim()}"),
        Initial = InitialFor(w.Name),
        SourceBadge = w.Visibility.ToString(),
        SourceBadgeBackground = (Brush)FindResource("AccentSoftBrush"),
        SourceBadgeForeground = (Brush)FindResource("AccentBrush")
    };

    private async void OnDownloadRow(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not RpBrowseRow row) return;
        await DownloadRowAsync(row, b);
    }

    private async void OnDownloadSelectedWorld(object sender, RoutedEventArgs e)
    {
        if (_currentRow is not null)
            await DownloadRowAsync(_currentRow, sender as Button);
    }

    private async void OnDownloadVersion(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RpVersionRow row }) return;
        if (_currentRow is null) return;
        await DownloadRowAsync(_currentRow, sender as Button, row);
    }

    private async Task DownloadRowAsync(RpBrowseRow row, Button? button, RpVersionRow? version = null)
    {
        var packs = await App.State.Api.ListPacksAsync();
        if (packs.Count == 0)
        {
            // A status line rather than a modal: the user is mid-browse and nothing here needs an
            // acknowledgement, it just needs to say why nothing happened.
            Fail("Create an instance first — a resource pack is installed into one.");
            return;
        }
        var picker = new PackPickerDialog(packs,
            "Pick an instance",
            "The downloaded .zip is copied into resourcepacks/.",
            "Pick") { Owner = _shell };
        if (picker.ShowDialog() != true || picker.SelectedPackId is null) return;

        var pack = packs.First(p => p.Id == picker.SelectedPackId.Value);
        if (button is not null) button.IsEnabled = false;
        try
        {
            string? zipPath = null;
            if (row.Kind == RpBrowseRowKind.External && row.External is { } ext)
                zipPath = await DownloadExternalZipAsync(ext, version?.ExternalVersion);
            else if (row.Kind == RpBrowseRowKind.CloudLauncher && row.Hosted is { } rp)
                zipPath = await DownloadHostedZipAsync(rp, version?.HostedVersion);

            if (zipPath is null) throw new InvalidOperationException("Download failed.");

            Note("Installing…");
            var source = zipPath;
            var dest = await Task.Run(() =>
            {
                var dir = ResourcePacksDir(pack);
                var target = UniqueZipPath(dir, $"{SafeFileBase(row.Title)}.zip");
                File.Copy(source, target, overwrite: false);
                try { File.Delete(source); } catch { /* temp file, best effort */ }
                return target;
            });

            RecordProvenance(pack, dest, row, version);
            await RefreshInstalledIndexAsync();

            Okay($"Installed to {dest}");
            DownloadStatus.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            DownloadStatus.Text = $"Installed to {dest}";
        }
        catch (Exception ex)
        {
            Fail("Install failed: " + ex.Message);
            DownloadStatus.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            DownloadStatus.Text = "Install failed: " + ex.Message;
        }
        finally
        {
            if (button is not null) button.IsEnabled = true;
        }
    }

    /// <summary>
    /// Records which store listing this zip came from, and names it after the project.
    /// </summary>
    /// <remarks>A resource pack has no identity of its own once it is on disk — no manifest id, no
    /// version. Unless this is written at install time the launcher can never match the file back to
    /// the listing, which is exactly what checking for an update needs. A hosted pack gets its
    /// hosting link instead, which serves the same purpose.</remarks>
    private static void RecordProvenance(PackSummary pack, string destPath, RpBrowseRow row, RpVersionRow? version)
    {
        var key = ResourcePackService.Key(pack.Id, Path.GetFileName(destPath));

        if (row.Kind == RpBrowseRowKind.External && row.External is { } ext)
        {
            var chosen = version?.ExternalVersion;
            App.State.ResourcePacks.SetProvenance(key, ext.Source, ext.Id, chosen?.Id, chosen?.VersionNumber);
            App.State.ResourcePacks.Rename(key, ext.Name);
        }
        else if (row.Kind == RpBrowseRowKind.CloudLauncher && row.Hosted is { } hosted)
        {
            App.State.ResourcePacks.LinkHostedResourcePack(key, hosted.Id);
            App.State.ResourcePacks.Rename(key, hosted.Name);
        }
    }

    private static string ResourcePacksDir(PackSummary pack)
    {
        App.State.Packs.EnsurePackFolder(pack.Id, pack.Name, pack.IsShared);
        var folder = Path.Combine(App.State.Packs.GameDir(pack.Id, pack.Name), "resourcepacks");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private async Task<string?> DownloadExternalZipAsync(ModSummary mod, ModVersion? selectedVersion = null)
    {
        StatusLabel.Foreground = (Brush)FindResource("TextSecondaryBrush");
        StatusLabel.Text = $"Fetching '{mod.Name}'…";

        ModVersion latest = selectedVersion
            ?? await ResolveLatestCompatibleVersionAsync(mod)
            ?? throw new InvalidOperationException("No downloadable version matched the filter.");

        var file = latest.Files.FirstOrDefault(f => f.IsPrimary) ?? latest.Files.FirstOrDefault()
                   ?? throw new InvalidOperationException("No file attachment.");
        file = await ResolveDownloadFileAsync(mod, latest, file);
        if (string.IsNullOrWhiteSpace(file.DownloadUrl))
            throw new InvalidOperationException("No download URL.");

        var tmp = Path.Combine(Path.GetTempPath(), $"cl rp-{Guid.NewGuid():N}.zip");
        DownloadStatus.Text = StatusLabel.Text;
        await App.State.Modrinth.DownloadFileAsync(file.DownloadUrl, tmp, null, _cts.Token);
        return tmp;
    }

    private async Task<ModVersion?> ResolveLatestCompatibleVersionAsync(ModSummary mod)
    {
        var versions = await LoadVersionsRawAsync(mod, CancellationToken.None);
        var ordered = versions.OrderByDescending(v => v.DatePublished).ToList();
        if (_filterMcVersion is null || string.IsNullOrWhiteSpace(_filterMcVersion))
            return ordered.FirstOrDefault(v => IsReleaseish(v.ReleaseChannel));

        var filtered = ordered
            .Where(v => v.GameVersions.Any(vg => string.Equals(vg, _filterMcVersion, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var pick = filtered.FirstOrDefault(v => IsReleaseish(v.ReleaseChannel)) ?? filtered.FirstOrDefault()
            ?? ordered.FirstOrDefault(v => IsReleaseish(v.ReleaseChannel))
            ?? ordered.FirstOrDefault();
        return pick;
    }

    private static bool IsReleaseish(string channel) =>
        string.Equals(channel, "release", StringComparison.OrdinalIgnoreCase);

    private async Task<List<ModVersion>> LoadVersionsRawAsync(ModSummary mod, CancellationToken ct) =>
        mod.Source == ModSource.CurseForge && int.TryParse(mod.Id, out var curseForgeId)
            ? await App.State.CurseForge.GetVersionsAsync(curseForgeId, ct)
            : await App.State.Modrinth.GetVersionsAsync(mod.Id, mcVersion: null, loader: null, ct);

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

    private async Task<string?> DownloadHostedZipAsync(HostedResourcePackSummary summary, HostedResourcePackVersionInfo? picked = null)
    {
        var detail = await App.State.Api.GetResourcePackAsync(summary.Id);
        var version = picked ?? detail.Versions.OrderByDescending(v => v.PublishedAt).FirstOrDefault()
            ?? throw new InvalidOperationException("This hosted pack has no uploads yet.");

        var tmp = Path.Combine(Path.GetTempPath(), $"cl-hosted-rp-{Guid.NewGuid():N}.zip");
        StatusLabel.Foreground = (Brush)FindResource("TextSecondaryBrush");
        StatusLabel.Text = $"Downloading '{summary.Name}'…";
        DownloadStatus.Text = StatusLabel.Text;
        await using var stream = await App.State.Api.DownloadResourcePackVersionAsync(summary.Id, version.Id);
        await using (var fs = File.Create(tmp)) await stream.CopyToAsync(fs);
        return tmp;
    }

    private async void OnResultSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsList.SelectedItem is not RpBrowseRow row) return;

        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _currentRow = row;

        DetailPlaceholder.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
        ShowDetailShell(row);
        ShowOverview("Loading…");
        ScreenshotsEmptyText.Text = "Loading screenshots…";
        ScreenshotsEmptyText.Visibility = Visibility.Visible;
        ScreenshotList.ItemsSource = null;
        ScreenshotList.Visibility = Visibility.Collapsed;
        VersionsGrid.ItemsSource = null;

        try
        {
            if (row.External is ModSummary external && row.Kind == RpBrowseRowKind.External)
                await LoadExternalDetailAsync(external, ct);
            else if (row.Hosted is HostedResourcePackSummary hosted && row.Kind == RpBrowseRowKind.CloudLauncher)
                await LoadHostedDetailAsync(hosted, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ShowOverview("Error: " + ex.Message);
        }
    }

    private async void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsList.SelectedItem is RpBrowseRow row)
            await DownloadRowAsync(row, null);
    }

    private async Task LoadExternalDetailAsync(ModSummary mod, CancellationToken ct)
    {
        _currentProjectUrl = BuildExternalProjectUrl(mod);
        OpenProjectButton.Visibility = Visibility.Visible;
        ConfigureLinks(new ModProjectLinks(_currentProjectUrl, null, null, null, null));

        try
        {
            var detail = mod.Source == ModSource.CurseForge
                ? await App.State.CurseForge.GetProjectDetailAsync(int.TryParse(mod.Id, out var cid) ? cid : 0, ct)
                : await App.State.Modrinth.GetProjectDetailAsync(mod.Id, ct);

            ShowOverview(detail.Description ?? mod.Description, detail.IsMarkdown);
            ShowScreenshots(detail.Screenshots);
            ConfigureLinks(detail.Links with { WebsiteUrl = detail.Links.WebsiteUrl ?? _currentProjectUrl });

            WorldNameLabel.Text = mod.Name;
            WorldMetaLabel.Text = $"{mod.Author ?? "unknown"} · {mod.Source}";
            SelectedIconFallback.Text = InitialFor(mod.Name);
            SetSelectedIcon(mod.IconUrl);

            var allVersions = await LoadVersionsRawAsync(mod, ct);
            IEnumerable<ModVersion> list = string.IsNullOrWhiteSpace(_filterMcVersion)
                ? allVersions
                : allVersions.Where(v =>
                    v.GameVersions.Any(vg => string.Equals(vg, _filterMcVersion!, StringComparison.OrdinalIgnoreCase)));

            VersionsGrid.ItemsSource = list.OrderByDescending(v => v.DatePublished)
                .Select(RpVersionRow.FromExternal).ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ShowOverview("Error: " + ex.Message);
        }

        PackTabs.SelectedIndex = 0;
    }

    private async Task LoadHostedDetailAsync(HostedResourcePackSummary hosted, CancellationToken ct)
    {
        _currentProjectUrl = null;
        OpenProjectButton.Visibility = Visibility.Collapsed;
        ConfigureLinks(new ModProjectLinks(null, null, null, null, null));

        ScreenshotsEmptyText.Text = "Hosted thumbnails are uploaded from CloudLauncher.";
        ScreenshotsEmptyText.Visibility = Visibility.Visible;
        ScreenshotList.ItemsSource = null;

        try
        {
            var detail = await App.State.Api.GetResourcePackAsync(hosted.Id, ct);
            ShowOverview(detail.Description ?? detail.Summary, isMarkdown: true);
            VersionsGrid.ItemsSource = detail.Versions
                .OrderByDescending(v => v.PublishedAt)
                .Select(RpVersionRow.FromHosted).ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ShowOverview("Error: " + ex.Message);
        }

        PackTabs.SelectedIndex = 0;
    }

    private void ShowDetailShell(RpBrowseRow row)
    {
        WorldNameLabel.Text = row.Title;
        WorldMetaLabel.Text = row.MetaLabel;
        SelectedIconFallback.Text = row.Initial;
        SetSelectedIcon(row.IconUrl);
        DownloadStatus.Text = "";
        PackTabs.SelectedIndex = 0;
    }

    private void ClearDetail()
    {
        _currentRow = null;
        DetailPanel.Visibility = Visibility.Collapsed;
        DetailPlaceholder.Visibility = Visibility.Visible;

        _currentProjectUrl = null;
        WorldNameLabel.Text = "";
        WorldMetaLabel.Text = "";
        SelectedIconFallback.Text = "";
        SelectedIconImage.Source = null;
        ShowOverview("");
        ScreenshotList.ItemsSource = null;
        VersionsGrid.ItemsSource = null;
        DownloadStatus.Text = "";
        OpenProjectButton.Visibility = Visibility.Collapsed;
    }

    private void ShowScreenshots(IReadOnlyList<ModMediaItem> screenshots)
    {
        var rows = screenshots.Select(s => new MediaRow(s)).ToList();
        ScreenshotList.ItemsSource = rows;
        ScreenshotList.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ScreenshotsEmptyText.Text = "No screenshots.";
        ScreenshotsEmptyText.Visibility = rows.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ConfigureLinks(ModProjectLinks links)
    {
        ConfigureLinkButton(ProjectPageButton, links.WebsiteUrl);
        ConfigureLinkButton(IssuesButton, links.IssuesUrl);
        ConfigureLinkButton(SourceButton, links.SourceUrl);
        ConfigureLinkButton(WikiButton, links.WikiUrl);

        LinksEmptyText.Visibility =
            ProjectPageButton.Visibility == Visibility.Visible ||
            IssuesButton.Visibility == Visibility.Visible ||
            SourceButton.Visibility == Visibility.Visible ||
            WikiButton.Visibility == Visibility.Visible
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
            OpenUrl(row.FullImageUrl);
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

        try { SelectedIconImage.Source = new BitmapImage(new Uri(iconUrl, UriKind.Absolute)); }
        catch { SelectedIconImage.Source = null; }
    }

    private static string BuildExternalProjectUrl(ModSummary mod)
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

    private static string SafeFileBase(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        var s = sb.ToString().Trim();
        if (string.IsNullOrEmpty(s)) s = "resource-pack";
        if (s.Length > 96) s = s[..96];
        return string.IsNullOrWhiteSpace(Path.GetExtension(s)) ? s : Path.GetFileNameWithoutExtension(s);
    }

    private static string UniqueZipPath(string folder, string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string((fileName ?? "pack.zip").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        if (!clean.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) clean += ".zip";

        var candidate = Path.Combine(folder, clean);
        var stem = Path.GetFileNameWithoutExtension(clean);
        var ext = ".zip";
        var n = 2;
        while (File.Exists(candidate))
            candidate = Path.Combine(folder, $"{stem}-{n++}{ext}");
        return candidate;
    }

    private static string InitialFor(string name) =>
        string.IsNullOrWhiteSpace(name) ? "?" : char.ToUpperInvariant(name.Trim()[0]).ToString();

    private static string FormatNumber(long n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:0.#}M",
        >= 1_000 => $"{n / 1_000.0:0.#}K",
        _ => n.ToString()
    };

    public enum RpBrowseSourceKind
    {
        CurseForge,
        Modrinth,
        CloudLauncherPublic,
        CloudLauncherPersonal,
        CloudLauncherShared,
        CloudLauncherTeam
    }

    public enum RpBrowseRowKind { External, CloudLauncher }

    public sealed class RpSourceChipRow
    {
        public string Label { get; set; } = "";
        public RpBrowseSourceKind Kind { get; set; }
        public Guid? TeamId { get; set; }
        public bool IsDivider { get; set; }
        public Brush Background { get; set; } = Brushes.Transparent;
        public Brush BorderColor { get; set; } = Brushes.Transparent;
        public Brush Foreground { get; set; } = Brushes.Black;
        public FontWeight FontWeight { get; set; } = FontWeights.Normal;
        public Visibility ChipVisibility { get; set; } = Visibility.Visible;
        public Cursor CursorHint { get; set; } = Cursors.Hand;
    }

    public sealed class RpBrowseRow
    {
        public RpBrowseRowKind Kind { get; set; }
        public ModSummary? External { get; set; }
        public HostedResourcePackSummary? Hosted { get; set; }
        public string Title { get; set; } = "";
        public string Subtitle { get; set; } = "";
        public string MetaLabel { get; set; } = "";
        public string? IconUrl { get; set; }
        public string Initial { get; set; } = "?";
        public string SourceBadge { get; set; } = "";
        public Brush SourceBadgeBackground { get; set; } = Brushes.Transparent;
        public Brush SourceBadgeForeground { get; set; } = Brushes.White;

        /// <summary>True when a pack of this name is already installed in one of the instances.</summary>
        public bool IsInstalled { get; private set; }

        public string DownloadLabel => IsInstalled ? "Installed" : "Download";

        public string DownloadTooltip => IsInstalled
            ? "Already installed — downloading again adds a second copy"
            : "Download into one of your instances";

        /// <summary>Returns true only when the flag changed, so the caller refreshes the list once
        /// rather than on every row.</summary>
        public bool ApplyInstalled(bool installed)
        {
            if (IsInstalled == installed) return false;
            IsInstalled = installed;
            return true;
        }
    }

    /// <summary>One checkable store category in the filter popup.</summary>
    public sealed class RpCategoryFilterItem
    {
        public string Label { get; init; } = "";
        public string Value { get; init; } = "";
        public bool IsChecked { get; set; }
    }

    public sealed class RpVersionRow
    {
        public ModVersion? ExternalVersion { get; init; }
        public HostedResourcePackVersionInfo? HostedVersion { get; init; }
        public string VersionNumber { get; init; } = "";
        public string McVersion { get; init; } = "";
        public string DateLabel { get; init; } = "";
        public string SizeLabel { get; init; } = "";

        public static RpVersionRow FromExternal(ModVersion version) => new()
        {
            ExternalVersion = version,
            VersionNumber = version.VersionNumber,
            McVersion = string.Join(", ", version.GameVersions.Take(2)) + (version.GameVersions.Length > 2 ? "…" : ""),
            DateLabel = version.DatePublished.LocalDateTime.ToString("yyyy-MM-dd"),
            SizeLabel = version.Files.FirstOrDefault()?.Size is long size ? FormatSize(size) : ""
        };

        public static RpVersionRow FromHosted(HostedResourcePackVersionInfo v) => new()
        {
            HostedVersion = v,
            VersionNumber = v.VersionString,
            McVersion = v.McVersionsCsv ?? "",
            DateLabel = v.PublishedAt.LocalDateTime.ToString("yyyy-MM-dd"),
            SizeLabel = FormatSize(v.FileSize)
        };

        private static string FormatSize(long bytes) => bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
            _ => $"{bytes / (1024.0 * 1024):0.#} MB"
        };
    }
}
