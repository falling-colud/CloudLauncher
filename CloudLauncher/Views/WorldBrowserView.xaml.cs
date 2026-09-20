using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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

public partial class WorldBrowserView : Page
{
    private readonly MainWindow _shell;
    private readonly ObservableCollection<WorldSourceChipRow> _chips = new();
    private readonly ObservableCollection<WorldBrowseRow> _rows = new();
    private readonly List<ModVersion> _externalVersions = new();
    private WorldSourceChipRow? _activeChip;
    private WorldBrowseRow? _currentRow;
    private string? _currentProjectUrl;
    private string _searchText = "";
    private string? _filterMcVersion;

    private CancellationTokenSource _cts = new();

    /// <summary>
    /// Cancels a download in flight, separately from <see cref="_cts"/>.
    /// </summary>
    /// <remarks>Downloads used to run on the search token, so clicking another row mid-download
    /// cancelled the download along with the detail fetch — with no message, because the cancellation
    /// was swallowed as a normal navigation.</remarks>
    private CancellationTokenSource? _downloadCts;

    /// <summary>0 popularity, 1 downloads, 2 last updated, 3 name. Mirrors the SortBox order.</summary>
    private int _sortMode;

    private bool _isLoading;
    private bool _hasMore;
    private int _offset;
    private const int PageSize = 25;
    private const int MaxResults = 200;

    public WorldBrowserView(MainWindow shell)
    {
        InitializeComponent();
        _shell = shell;
        SourceStrip.ItemsSource = _chips;
        ResultsList.ItemsSource = _rows;
        WorldTabs.SelectionChanged += OnWorldTabsChanged;
        Loaded += async (_, _) =>
        {
            await InitAsync();
            Window.GetWindow(this)!.PreviewKeyDown += OnShellKeyDown;
        };
        Unloaded += (_, _) =>
        {
            if (Window.GetWindow(this) is Window w) w.PreviewKeyDown -= OnShellKeyDown;
            _downloadCts?.Cancel();
        };
    }

    /// <summary>F5 re-runs the current search, matching every other list screen.</summary>
    private async void OnShellKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsVisible || e.Key != Key.F5) return;
        e.Handled = true;
        try { await ResetAndLoadAsync(); }
        catch (Exception ex) { SetStatus("Refresh failed: " + ex.Message, danger: true); }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        try { await ResetAndLoadAsync(); }
        catch (Exception ex) { SetStatus("Refresh failed: " + ex.Message, danger: true); }
    }

    private async void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _sortMode = Math.Max(0, SortBox.SelectedIndex);
        try { await ResetAndLoadAsync(); }
        catch (Exception ex) { SetStatus("Could not re-sort: " + ex.Message, danger: true); }
    }

    /// <summary>CurseForge's numeric sortField for the chosen mode. Name has no server-side
    /// equivalent, so it falls back to popularity and is applied client-side instead.</summary>
    private int CurseSortField() => _sortMode switch
    {
        1 => 6,  // TotalDownloads
        2 => 3,  // LastUpdated
        _ => 2   // Popularity
    };

    /// <summary>
    /// Writes the status line in the colour that matches the message.
    /// </summary>
    /// <remarks>Set by resource reference rather than assignment so a theme or accent change
    /// repaints it — an assigned brush is frozen at the moment it was resolved.</remarks>
    private void SetStatus(string text, bool danger = false, bool success = false)
    {
        StatusLabel.Text = text;
        StatusLabel.SetResourceReference(TextBlock.ForegroundProperty,
            danger ? "DangerBrush" : success ? "AccentBrush" : "TextSecondaryBrush");
    }

    private void SetDownloadStatus(string text, bool danger = false, bool success = false)
    {
        DownloadStatus.Text = text;
        DownloadStatus.SetResourceReference(TextBlock.ForegroundProperty,
            danger ? "DangerBrush" : success ? "AccentBrush" : "TextSecondaryBrush");
    }

    // Render rich HTML/markdown descriptions in an embedded WebView2. Its HWND draws over
    // WPF (airspace), so hide it off-tab. Scrolling is native — no manual wheel routing.
    private void OnWorldTabsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != WorldTabs) return;
        var onOverview = WorldTabs.SelectedIndex == 0;
        OverviewHost.Visibility = onOverview ? Visibility.Visible : Visibility.Collapsed;
        OverviewBrowser.Visibility = onOverview ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowOverview(string? content, bool isMarkdown = false) => OverviewBrowser.Show(content, isMarkdown);

    private async Task InitAsync()
    {
        await BuildChipsAsync();
        if (_activeChip is null && _chips.Count > 0)
            await SelectChipAsync(_chips.First(c => !c.IsDivider));
    }

    private async Task BuildChipsAsync()
    {
        _chips.Clear();
        _chips.Add(NewChip("CurseForge", WorldSourceKind.CurseForge));
        _chips.Add(NewDivider());
        _chips.Add(NewChip("CloudLauncher", WorldSourceKind.CloudLauncherPublic));
        _chips.Add(NewChip("Personal worlds", WorldSourceKind.CloudLauncherPersonal));
        _chips.Add(NewChip("Shared with me", WorldSourceKind.CloudLauncherShared));
        _chips.Add(NewChip("All teams", WorldSourceKind.CloudLauncherTeam));

        try
        {
            var teams = await App.State.Api.ListTeamsAsync();
            foreach (var t in teams.OrderBy(t => t.Name))
                _chips.Add(NewChip(t.Name, WorldSourceKind.CloudLauncherTeam, t.Id));
        }
        catch { /* ignore */ }
        ApplyChipStyles();
    }

    private static WorldSourceChipRow NewChip(string label, WorldSourceKind kind, Guid? teamId = null) =>
        new() { Label = label, Kind = kind, TeamId = teamId };

    private static WorldSourceChipRow NewDivider() => new()
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
        if (sender is FrameworkElement fe && fe.DataContext is WorldSourceChipRow row && !row.IsDivider)
            await SelectChipAsync(row);
    }

    private async Task SelectChipAsync(WorldSourceChipRow chip)
    {
        _activeChip = chip;
        ApplyChipStyles();
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
        _filterMcVersion = string.IsNullOrWhiteSpace(FilterMcBox.Text) ? null : FilterMcBox.Text.Trim();
        FiltersPopup.IsOpen = false;
        await ResetAndLoadAsync();
    }

    private async Task ResetAndLoadAsync()
    {
        _cts.Cancel(); _cts = new CancellationTokenSource();
        _offset = 0;
        _rows.Clear();
        _hasMore = true;
        SetStatus("");
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
                case WorldSourceKind.CurseForge:
                    added = await LoadCurseForgeAsync(ct);
                    break;
                case WorldSourceKind.CloudLauncherPublic:
                    added = await LoadCloudLauncherAsync(WorldBrowseSource.Public, null, ct);
                    break;
                case WorldSourceKind.CloudLauncherPersonal:
                    added = await LoadCloudLauncherAsync(WorldBrowseSource.Personal, null, ct);
                    break;
                case WorldSourceKind.CloudLauncherShared:
                    added = await LoadCloudLauncherAsync(WorldBrowseSource.Shared, null, ct);
                    break;
                case WorldSourceKind.CloudLauncherTeam:
                    added = await LoadCloudLauncherAsync(WorldBrowseSource.Team, _activeChip.TeamId, ct);
                    break;
                default:
                    added = 0; _hasMore = false; break;
            }
            _offset += added;
            if (added == 0) _hasMore = false;
            CountLabel.Text = _rows.Count == 0 ? "" : $"{_rows.Count} result{(_rows.Count == 1 ? "" : "s")}";
            UpdateEmpty();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetStatus("Error: " + ex.Message, danger: true); _hasMore = false; }
        finally { _isLoading = false; }
    }

    private void UpdateEmpty()
    {
        if (_rows.Count > 0 || _hasMore || _isLoading) { EmptyState.Visibility = Visibility.Collapsed; return; }
        EmptyState.Visibility = Visibility.Visible;
        EmptyTitle.Text = "No worlds found";
        EmptyDetail.Text = _activeChip?.Kind switch
        {
            WorldSourceKind.CloudLauncherShared => "Nobody has shared a world with you yet.",
            WorldSourceKind.CloudLauncherPersonal => "You haven't shared any worlds yet.",
            WorldSourceKind.CloudLauncherTeam   => _activeChip?.TeamId is null ? "No team worlds are available yet." : "This team has no shared worlds yet.",
            _ => "Try a different search or pick another source."
        };
    }

    private async Task<int> LoadCurseForgeAsync(CancellationToken ct)
    {
        var hits = await App.State.CurseForge.SearchAsync(
            _searchText, _filterMcVersion, loader: null,
            limit: PageSize, offset: _offset,
            classId: CurseForgeService.ClassIdWorlds,
            sortField: CurseSortField(), ct: ct);
        // CurseForge has no name sort, so apply it to the page we were given. Sorting a page rather
        // than the whole result set is the honest limit of an endlessly-scrolling list.
        var ordered = _sortMode == 3
            ? hits.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList()
            : hits;
        foreach (var m in ordered) _rows.Add(RowFromCurse(m));
        return hits.Count;
    }

    private async Task<int> LoadCloudLauncherAsync(WorldBrowseSource source, Guid? teamId, CancellationToken ct)
    {
        var page = await App.State.Api.BrowseWorldsAsync(source, teamId, _searchText, _filterMcVersion,
            offset: _offset, limit: PageSize, ct: ct);
        // The hosted catalog has no sort parameter, so order the page here — otherwise the sort box
        // would silently do nothing on every CloudLauncher source.
        IEnumerable<SharedWorldSummary> items = page.Items;
        items = _sortMode switch
        {
            2 => items.OrderByDescending(w => w.UpdatedAt),
            3 => items.OrderBy(w => w.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => items
        };
        foreach (var w in items) _rows.Add(RowFromCloud(w));
        _hasMore = _offset + page.Items.Count < page.Total;
        return page.Items.Count;
    }

    private static WorldBrowseRow RowFromCurse(ModSummary m) => new()
    {
        Kind = WorldBrowseRowKind.CurseForge,
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

    private WorldBrowseRow RowFromCloud(SharedWorldSummary w) => new()
    {
        Kind = WorldBrowseRowKind.CloudLauncher,
        Internal = w,
        Title = w.Name,
        Subtitle = w.Summary ?? "",
        MetaLabel = $"by {w.OwnerUsername}{(w.McVersion is null ? "" : " · MC " + w.McVersion)}",
        Initial = InitialFor(w.Name),
        SourceBadge = "CloudLauncher",
        SourceBadgeBackground = (Brush)FindResource("AccentSoftBrush"),
        SourceBadgeForeground = (Brush)FindResource("AccentBrush")
    };

    private async void OnDownloadRow(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not WorldBrowseRow row) return;
        try { await DownloadRowAsync(row, b); }
        catch (Exception ex) { SetDownloadStatus("Download failed: " + ex.Message, danger: true); }
    }

    private async void OnDownloadSelectedWorld(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_currentRow is not null)
                await DownloadRowAsync(_currentRow, sender as Button);
        }
        catch (Exception ex) { SetDownloadStatus("Download failed: " + ex.Message, danger: true); }
    }

    private async void OnDownloadVersion(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not FrameworkElement { DataContext: WorldVersionRow row }) return;
            if (_currentRow is null) return;
            await DownloadRowAsync(_currentRow, sender as Button, row);
        }
        catch (Exception ex) { SetDownloadStatus("Download failed: " + ex.Message, danger: true); }
    }

    // -- row context menu -----------------------------------------------------

    private static WorldBrowseRow? RowFromMenu(object sender)
    {
        if (sender is not MenuItem mi) return null;
        var parent = mi.Parent;
        while (parent is MenuItem p) parent = p.Parent;
        return parent is ContextMenu { PlacementTarget: FrameworkElement { DataContext: WorldBrowseRow row } }
            ? row : null;
    }

    private async void OnCtxDownload(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RowFromMenu(sender) is { } row) await DownloadRowAsync(row, null);
        }
        catch (Exception ex) { SetDownloadStatus("Download failed: " + ex.Message, danger: true); }
    }

    private void OnCtxOpenPage(object sender, RoutedEventArgs e)
    {
        if (RowFromMenu(sender)?.External is { } world) OpenUrl(BuildProjectUrl(world));
        else SetStatus("CloudLauncher worlds have no external page.", danger: true);
    }

    private void OnCtxCopyLink(object sender, RoutedEventArgs e)
    {
        if (RowFromMenu(sender) is not { } row) return;
        var url = row.External is { } world
            ? BuildProjectUrl(world)
            : row.Internal is { } hosted
                ? $"{App.State.Settings.ServerUrl.TrimEnd('/')}/worlds/{hosted.Id}"
                : null;
        if (url is null) { SetStatus("This row has no link.", danger: true); return; }
        var copied = Services.ClipboardHelper.TrySetText(url);
        SetStatus(copied ? "Link copied." : "The clipboard is busy - try again.",
                  danger: !copied, success: copied);
    }

    // -- downloading ----------------------------------------------------------

    private void OnCancelDownload(object sender, RoutedEventArgs e)
    {
        _downloadCts?.Cancel();
        SetDownloadStatus("Cancelling...");
    }

    private void ShowDownloadProgress(bool visible)
    {
        DownloadProgressRow.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        DownloadProgress.Value = 0;
        DownloadProgress.IsIndeterminate = visible;
    }

    /// <summary>A sink for byte counts from a transfer whose total may not be known up front.</summary>
    private IProgress<(long done, long total)> ByteProgress(string what) =>
        new Progress<(long done, long total)>(p =>
        {
            if (p.total > 0)
            {
                DownloadProgress.IsIndeterminate = false;
                DownloadProgress.Value = Math.Min(1.0, p.done / (double)p.total);
                SetDownloadStatus($"{what} - {FormatSize(p.done)} of {FormatSize(p.total)}");
            }
            else SetDownloadStatus($"{what} - {FormatSize(p.done)}");
        });

    /// <summary>
    /// Downloads a world and extracts it into the instance the user picks.
    /// </summary>
    /// <remarks>
    /// Both halves report progress: the transfer drives a determinate bar off the known length, and
    /// the extract runs on a worker thread through <see cref="WorldService.ImportZipAsync"/>, which
    /// also flattens the single wrapper folder most world zips carry and refuses entries that point
    /// outside the save. Before this, extraction ran on the dispatcher and a 300 MB adventure map
    /// looked like a hang.
    /// </remarks>
    private async Task DownloadRowAsync(WorldBrowseRow row, Button? button, WorldVersionRow? version = null)
    {
        if (!row.CanDownload)
        {
            await AppDialog.MessageAsync(_shell, "Nothing to download", row.DownloadToolTip);
            return;
        }

        var packs = await App.State.Api.ListPacksAsync();
        if (packs.Count == 0)
        {
            await AppDialog.MessageAsync(_shell, "No instance",
                "You have no instances yet. Create one before downloading a world.");
            return;
        }
        var picker = new PackPickerDialog(packs,
            "Pick an instance",
            "The world will land in this instance's saves/ folder.",
            "Pick") { Owner = _shell };
        if (picker.ShowDialog() != true || picker.SelectedPackId is null) return;
        var packId = picker.SelectedPackId.Value;
        var pack = packs.First(p => p.Id == packId);

        _downloadCts?.Cancel();
        _downloadCts = new CancellationTokenSource();
        var ct = _downloadCts.Token;

        if (button is not null) button.IsEnabled = false;
        ShowDownloadProgress(true);
        string? zipPath = null;
        try
        {
            if (row.Kind == WorldBrowseRowKind.CurseForge && row.External is { } ext)
                zipPath = await DownloadCurseZipAsync(ext, version?.ExternalVersion, ct);
            else if (row.Kind == WorldBrowseRowKind.CloudLauncher && row.Internal is { } w)
                zipPath = await DownloadCloudZipAsync(w, version?.CloudVersion, ct);

            if (zipPath is null) throw new InvalidOperationException("Download failed.");

            SetDownloadStatus($"Extracting into {pack.Name}...");
            DownloadProgress.IsIndeterminate = false;
            var extract = new Progress<double>(f => DownloadProgress.Value = f);
            var folderName = await App.State.Worlds.ImportZipAsync(
                zipPath, pack.Id, pack.Name, row.Title, extract, ct);

            var landed = Path.Combine(App.State.Worlds.SavesDir(pack.Id, pack.Name), folderName);
            SetStatus($"Saved to {landed}", success: true);
            SetDownloadStatus($"Saved to {landed}", success: true);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Download cancelled.");
            SetDownloadStatus("Download cancelled.");
        }
        catch (Exception ex)
        {
            SetStatus("Download failed: " + ex.Message, danger: true);
            SetDownloadStatus("Download failed: " + ex.Message, danger: true);
        }
        finally
        {
            if (zipPath is not null) { try { File.Delete(zipPath); } catch { /* best effort */ } }
            ShowDownloadProgress(false);
            if (button is not null) button.IsEnabled = true;
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    private async Task<string> DownloadCurseZipAsync(ModSummary mod, ModVersion? selectedVersion, CancellationToken ct)
    {
        SetDownloadStatus($"Fetching '{mod.Name}'...");
        if (!int.TryParse(mod.Id, out var cfModId)) throw new InvalidOperationException("Bad CurseForge id.");
        var latest = selectedVersion;
        if (latest is null)
        {
            var versions = await App.State.CurseForge.GetVersionsAsync(cfModId, ct);
            latest = versions.OrderByDescending(v => v.DatePublished).FirstOrDefault()
                ?? throw new InvalidOperationException("No versions available.");
        }
        var primary = latest.Files.FirstOrDefault(f => f.IsPrimary) ?? latest.Files.FirstOrDefault()
            ?? throw new InvalidOperationException("No file in latest version.");
        var parts = latest.Id.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var cfFileId))
            throw new InvalidOperationException("Unexpected CurseForge file id format.");
        var url = await App.State.CurseForge.GetDownloadUrlAsync(cfModId, cfFileId);
        if (string.IsNullOrEmpty(url)) throw new InvalidOperationException("No download URL.");

        var tmp = Path.Combine(Path.GetTempPath(), $"cl-world-{Guid.NewGuid():N}.zip");
        SetDownloadStatus($"Downloading '{mod.Name}'...");
        await App.State.Modrinth.DownloadFileAsync(url, tmp, ByteProgress($"Downloading '{mod.Name}'"), ct);
        return tmp;
    }

    private async Task<string> DownloadCloudZipAsync(SharedWorldSummary w, SharedWorldVersionInfo? selectedVersion,
                                                     CancellationToken ct)
    {
        var detail = await App.State.Api.GetSharedWorldAsync(w.Id, ct);
        var version = selectedVersion ?? detail.Versions.OrderByDescending(v => v.PublishedAt).FirstOrDefault()
            ?? throw new InvalidOperationException("This world has no versions yet.");
        var tmp = Path.Combine(Path.GetTempPath(), $"cl-world-{Guid.NewGuid():N}.zip");
        SetDownloadStatus($"Downloading '{w.Name}'...");

        // Copied in chunks rather than with CopyToAsync so the bar can move: the version record
        // already carries the total, so this is determinate from the first byte.
        var progress = ByteProgress($"Downloading '{w.Name}'");
        await using var stream = await App.State.Api.DownloadSharedWorldVersionAsync(w.Id, version.Id, ct);
        await using (var fs = File.Create(tmp))
        {
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                progress.Report((done, version.FileSize));
            }
        }
        return tmp;
    }

    private async void OnResultSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsList.SelectedItem is not WorldBrowseRow row) return;
        _currentRow = row;
        if (row.External is not null) await LoadCurseForgeDetailAsync(row);
        else if (row.Internal is not null) await LoadCloudDetailAsync(row);
    }

    private async void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsList.SelectedItem is WorldBrowseRow row)
            await DownloadRowAsync(row, null);
    }

    private async Task LoadCurseForgeDetailAsync(WorldBrowseRow row)
    {
        if (row.External is not { } world) return;
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        ShowDetailShell(row);
        _currentProjectUrl = BuildProjectUrl(world);
        OpenProjectButton.Visibility = Visibility.Visible;
        ConfigureLinks(new ModProjectLinks(_currentProjectUrl, null, null, null, null));
        ShowOverview("Loading…");
        ScreenshotsEmptyText.Text = "Loading screenshots...";
        VersionsGrid.ItemsSource = null;
        _externalVersions.Clear();

        try
        {
            var cfId = int.TryParse(world.Id, out var parsed) ? parsed : 0;
            var detail = await App.State.CurseForge.GetProjectDetailAsync(cfId, ct);
            ShowOverview(detail.Description ?? world.Description, detail.IsMarkdown);
            ShowScreenshots(detail.Screenshots);
            ConfigureLinks(detail.Links with { WebsiteUrl = detail.Links.WebsiteUrl ?? _currentProjectUrl });

            _externalVersions.AddRange(await App.State.CurseForge.GetVersionsAsync(cfId, ct));
            VersionsGrid.ItemsSource = _externalVersions
                .OrderByDescending(v => v.DatePublished)
                .Select(WorldVersionRow.FromExternal)
                .ToList();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ShowOverview("Error: " + ex.Message);
        }
    }

    private async Task LoadCloudDetailAsync(WorldBrowseRow row)
    {
        if (row.Internal is not { } world) return;
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        ShowDetailShell(row);
        _currentProjectUrl = null;
        OpenProjectButton.Visibility = Visibility.Collapsed;
        ConfigureLinks(new ModProjectLinks(null, null, null, null, null));
        ShowOverview("Loading…");
        ScreenshotsEmptyText.Text = "CloudLauncher world screenshots are not supported yet.";
        ScreenshotsEmptyText.Visibility = Visibility.Visible;
        ScreenshotList.ItemsSource = null;
        ScreenshotList.Visibility = Visibility.Collapsed;
        VersionsGrid.ItemsSource = null;

        try
        {
            var detail = await App.State.Api.GetSharedWorldAsync(world.Id, ct);
            ShowOverview(detail.Description ?? detail.Summary, isMarkdown: true);
            VersionsGrid.ItemsSource = detail.Versions
                .OrderByDescending(v => v.PublishedAt)
                .Select(WorldVersionRow.FromCloud)
                .ToList();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ShowOverview("Error: " + ex.Message);
        }
    }

    private void ShowDetailShell(WorldBrowseRow row)
    {
        DetailPlaceholder.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
        WorldTabs.SelectedIndex = 0;
        WorldNameLabel.Text = row.Title;
        WorldMetaLabel.Text = row.MetaLabel;
        SelectedIconFallback.Text = row.Initial;
        SetSelectedIcon(row.IconUrl);
        DownloadStatus.Text = "";
    }

    private void ClearDetail()
    {
        _currentRow = null;
        _currentProjectUrl = null;
        _externalVersions.Clear();
        DetailPanel.Visibility = Visibility.Collapsed;
        DetailPlaceholder.Visibility = Visibility.Visible;
        WorldNameLabel.Text = "";
        WorldMetaLabel.Text = "";
        SelectedIconFallback.Text = "";
        SelectedIconImage.Source = null;
        ShowOverview("");
        ScreenshotList.ItemsSource = null;
        VersionsGrid.ItemsSource = null;
        DownloadStatus.Text = "";
    }

    private void ShowScreenshots(IReadOnlyList<ModMediaItem> screenshots)
    {
        var rows = screenshots.Select(s => new MediaRow(s)).ToList();
        ScreenshotList.ItemsSource = rows;
        ScreenshotList.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ScreenshotsEmptyText.Text = "No screenshots are available for this world.";
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

    private static string BuildProjectUrl(ModSummary world)
    {
        var slug = string.IsNullOrWhiteSpace(world.Slug) ? world.Id : world.Slug;
        return world.Source == ModSource.CurseForge
            ? $"https://www.curseforge.com/minecraft/worlds/{slug}"
            : $"https://modrinth.com/datapack/{slug}";
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
        string.IsNullOrWhiteSpace(name) ? "?" : char.ToUpperInvariant(name.Trim()[0]).ToString();

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

    public enum WorldSourceKind
    {
        CurseForge,
        CloudLauncherPublic,
        CloudLauncherPersonal,
        CloudLauncherShared,
        CloudLauncherTeam
    }

    public enum WorldBrowseRowKind { CurseForge, CloudLauncher }

    public class WorldSourceChipRow
    {
        public string Label { get; set; } = "";
        public WorldSourceKind Kind { get; set; }
        public Guid? TeamId { get; set; }
        public bool IsDivider { get; set; }
        public Brush Background { get; set; } = Brushes.Transparent;
        public Brush BorderColor { get; set; } = Brushes.Transparent;
        public Brush Foreground { get; set; } = Brushes.Black;
        public FontWeight FontWeight { get; set; } = FontWeights.Normal;
        public Visibility ChipVisibility { get; set; } = Visibility.Visible;
        public Cursor CursorHint { get; set; } = Cursors.Hand;
    }

    public class WorldBrowseRow
    {
        public WorldBrowseRowKind Kind { get; set; }
        public ModSummary? External { get; set; }
        public SharedWorldSummary? Internal { get; set; }
        public string Title { get; set; } = "";
        public string Subtitle { get; set; } = "";
        public string MetaLabel { get; set; } = "";
        public string? IconUrl { get; set; }
        public string Initial { get; set; } = "?";
        public string SourceBadge { get; set; } = "";
        public Brush SourceBadgeBackground { get; set; } = Brushes.Transparent;
        public Brush SourceBadgeForeground { get; set; } = Brushes.White;

        /// <summary>
        /// False for a hosted world page that has no uploaded version behind it.
        /// </summary>
        /// <remarks>A world can be created on the server before anything is uploaded to it, and the
        /// Download button used to look identical on those rows and fail with "This world has no
        /// versions yet" only after the instance picker had been filled in.</remarks>
        public bool CanDownload => Internal is null || Internal.VersionCount > 0;

        public string DownloadLabel => CanDownload ? "Download" : "No file";

        public string DownloadToolTip => CanDownload
            ? "Download this world into one of your instances"
            : "The owner has published this world's page but has not uploaded a save to it yet.";

        /// <summary>The whole row, for the cases where the description was trimmed away.</summary>
        public string RowToolTip
        {
            get
            {
                var lines = new List<string> { Title, MetaLabel };
                if (!string.IsNullOrWhiteSpace(Subtitle)) lines.Insert(1, Subtitle);
                if (Internal is { } hosted)
                    lines.Add($"{hosted.VersionCount} version{(hosted.VersionCount == 1 ? "" : "s")} uploaded");
                return string.Join("\n", lines);
            }
        }
    }

    public sealed class WorldVersionRow
    {
        public ModVersion? ExternalVersion { get; init; }
        public SharedWorldVersionInfo? CloudVersion { get; init; }
        public string VersionNumber { get; init; } = "";
        public string McVersion { get; init; } = "";
        public string DateLabel { get; init; } = "";
        public string SizeLabel { get; init; } = "";

        public static WorldVersionRow FromExternal(ModVersion version) => new()
        {
            ExternalVersion = version,
            VersionNumber = version.VersionNumber,
            McVersion = string.Join(", ", version.GameVersions.Take(2)) + (version.GameVersions.Length > 2 ? "..." : ""),
            DateLabel = version.DatePublished.LocalDateTime.ToString("yyyy-MM-dd"),
            SizeLabel = version.Files.FirstOrDefault()?.Size is long size ? FormatSize(size) : ""
        };

        public static WorldVersionRow FromCloud(SharedWorldVersionInfo version) => new()
        {
            CloudVersion = version,
            VersionNumber = version.VersionString,
            McVersion = version.McVersion ?? "",
            DateLabel = version.PublishedAt.LocalDateTime.ToString("yyyy-MM-dd"),
            SizeLabel = FormatSize(version.FileSize)
        };
    }
}
