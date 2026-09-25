using System.Collections.ObjectModel;
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
    /// The paging state machine: what may load, how much is loaded, and the page load in flight.
    /// </summary>
    private readonly InfiniteScroll.Pager _pager;

    /// <summary>
    /// Cancels a download in flight. Separate from <see cref="_cts"/> so selecting another row does
    /// not cancel the download.
    /// </summary>
    private CancellationTokenSource? _downloadCts;

    /// <summary>0 popularity, 1 downloads, 2 last updated, 3 name. Mirrors the SortBox order.</summary>
    private int _sortMode;

    /// <summary>The hosted world the page was asked to open on, until it has done so.</summary>
    private Guid? _focusWorldId;

    /// <summary>The selected hosted world as the server describes it (its access list and what this
    /// person may do), or null while nothing hosted is selected.</summary>
    private SharedWorldDetail? _currentHosted;

    /// <summary>The teams this person is on, for deciding which list a hosted world
    /// belongs in.</summary>
    private HashSet<Guid> _myTeamIds = new();

    private bool _isLoading;
    private bool _hasMore;
    private int _offset;
    private const int PageSize = 50;   // both stores cap a page at 50
    /// <summary>A safety stop so a runaway pager cannot fill memory, not a browsing limit.</summary>
    private const int MaxResults = 2000;

    public WorldBrowserView(MainWindow shell)
    {
        InitializeComponent();
        _shell = shell;
        SourceStrip.ItemsSource = _chips;
        ResultsList.ItemsSource = _rows;
        WorldTabs.SelectionChanged += OnWorldTabsChanged;
        _pager = new InfiniteScroll.Pager(
            () => !_isLoading && _hasMore && _activeChip is not null && _rows.Count < MaxResults,
            () => _rows.Count,
            LoadMoreAsync);
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

    /// <summary>The browser, opened straight onto one hosted world.</summary>
    /// <remarks>
    /// Picks the list the world belongs in (Personal worlds, then Shared with me, then All teams,
    /// then the public list), selects its row and shows its detail. A world not on that list's
    /// first page is inserted at the top instead of paged for.
    /// </remarks>
    public WorldBrowserView(MainWindow shell, Guid focusWorldId) : this(shell)
    {
        _focusWorldId = focusWorldId;
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
    /// <remarks>Set by resource reference so a theme or accent change repaints it.</remarks>
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

    // Rich HTML/markdown descriptions render in an embedded WebView2. Its HWND draws over WPF
    // (airspace), so hide it off-tab.
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
        // Once only: Loaded fires again whenever the page is navigated back to.
        if (_focusWorldId is { } focus)
        {
            _focusWorldId = null;
            if (await FocusWorldAsync(focus)) return;
        }
        if (_activeChip is null && _chips.Count > 0)
            await SelectChipAsync(_chips.First(c => !c.IsDivider));
    }

    /// <summary>Shows one hosted world: its list, its row, its detail. False when it
    /// could not.</summary>
    private async Task<bool> FocusWorldAsync(Guid worldId)
    {
        SharedWorldDetail detail;
        try { detail = await App.State.Api.GetSharedWorldAsync(worldId); }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(WorldBrowserView), ex);
            SetStatus("That world could not be opened: " + ContentBundleService.Explain(ex, ex.Message), danger: true);
            return false;
        }

        if (ChipFor(detail) is not { } chip) return false;
        await SelectChipAsync(chip);

        var row = _rows.FirstOrDefault(r => r.Internal?.Id == worldId);
        if (row is null)
        {
            row = RowFromCloud(SummaryOf(detail));
            _rows.Insert(0, row);
            EmptyState.Visibility = Visibility.Collapsed;
        }
        ResultsList.SelectedItem = row;
        ResultsList.ScrollIntoView(row);
        return true;
    }

    /// <summary>Which list a hosted world belongs in, for this person.</summary>
    /// <remarks>Decided from the world's access list. Older servers send that list only to the world's
    /// managers; a world that cannot be placed is then most likely shared with this person.</remarks>
    private WorldSourceChipRow? ChipFor(SharedWorldDetail detail)
    {
        var me = App.State.Settings.UserId;
        var kind =
            me is { } owner && detail.OwnerId == owner ? WorldSourceKind.CloudLauncherPersonal
            : me is { } person && detail.Collaborators.Any(c => c.UserId == person) ? WorldSourceKind.CloudLauncherShared
            : detail.Teams.Any(t => _myTeamIds.Contains(t.TeamId)) ? WorldSourceKind.CloudLauncherTeam
            : detail.Visibility == PackVisibility.Public ? WorldSourceKind.CloudLauncherPublic
            : WorldSourceKind.CloudLauncherShared;
        return _chips.FirstOrDefault(c => !c.IsDivider && c.Kind == kind && c.TeamId is null);
    }

    /// <summary>The list row's shape of a world the page only has the detail of.</summary>
    private static SharedWorldSummary SummaryOf(SharedWorldDetail d) => new(
        d.Id, d.Slug, d.Name, d.Summary, d.OwnerId, d.OwnerUsername, d.Visibility, d.IconBlobHash,
        d.McVersion, d.CreatedAt, d.UpdatedAt, d.EffectivePermissions, d.Versions.Count);

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
            _myTeamIds = teams.Select(t => t.Id).ToHashSet();
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
        // Wait for the cancelled load before clearing anything. Its finally resets the in-progress
        // flag, and until then the fresh load below would return at the guard and leave the list
        // empty. It also stops a late page from appending to the list we are about to clear.
        await _pager.DrainAsync();
        _offset = 0;
        _rows.Clear();
        _pager.Reset();
        _hasMore = true;
        SetStatus("");
        EmptyState.Visibility = Visibility.Collapsed;
        CountLabel.Text = "";
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
        catch (Exception) { /* LoadMoreAsync reports its own failures */ }
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
        catch (Exception ex)
        {
            SetStatus("Error: " + ex.Message, danger: true);
            _hasMore = false;
            // Show the failure in the list too, not only in the status bar.
            _isLoading = false;
            UpdateEmpty();
        }
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
        // CurseForge has no name sort, so sort the page we were given. With endless scrolling that
        // is per page, not the whole result set.
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
        // The hosted catalog has no sort parameter, so order the page here.
        IEnumerable<SharedWorldSummary> items = page.Items;
        items = _sortMode switch
        {
            2 => items.OrderByDescending(w => w.UpdatedAt),
            3 => items.OrderBy(w => w.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => items
        };
        // A world the page was opened on may already sit at the top of the list; its own page would
        // otherwise list it a second time.
        foreach (var w in items)
            if (!_rows.Any(r => r.Internal?.Id == w.Id))
                _rows.Add(RowFromCloud(w));
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
    /// Downloads a world, then asks <see cref="ImportContentCard"/> which instances (possibly
    /// none) should get a copy.
    /// </summary>
    /// <remarks>
    /// Each ticked instance gets its own copy. This page's bar covers the transfer, and the card
    /// extracts each copy off the dispatcher via <see cref="WorldService.ImportZipAsync"/>.
    /// </remarks>
    private async Task DownloadRowAsync(WorldBrowseRow row, Button? button, WorldVersionRow? version = null)
    {
        if (!row.CanDownload)
        {
            await AppDialog.MessageAsync(_shell,
                row.HasFile ? "Download not available" : "Nothing to download", row.DownloadToolTip);
            return;
        }

        // No instances is fine: the card can keep the save as a library template only.
        var packs = await App.State.Api.ListPacksAsync();

        // What each instance's saves/ held before the card writes anything, so the copies it makes
        // can be told apart from existing saves (see LinkCopies).
        var savesBefore = row.Internal is not null
            ? await Task.Run(() => SaveFoldersByInstance(packs))
            : null;

        // Named after the listing, since the library template and each save folder take this
        // name. Staged before the button is disabled and the bar shown, so a failure here
        // leaves neither stuck.
        using var staged = StagedDownload.For(LibraryKind.World, ZipNameFor(row.Title));

        // Each download owns its source. The field only points at the newest one, so a download
        // that finishes late must not dispose or clear whatever the field holds by then.
        var cts = new CancellationTokenSource();
        _downloadCts?.Cancel();
        _downloadCts = cts;
        var ct = cts.Token;

        if (button is not null) button.IsEnabled = false;
        ShowDownloadProgress(true);
        try
        {
            // Null for a hosted world: a shared world is not a store listing, and ModSource has no
            // CloudLauncher member to file one under.
            ModVersion? fetched = null;
            if (row.Kind == WorldBrowseRowKind.CurseForge && row.External is { } ext)
                fetched = await DownloadCurseZipAsync(ext, version?.ExternalVersion, staged.FilePath, ct);
            else if (row.Kind == WorldBrowseRowKind.CloudLauncher && row.Internal is { } w)
                await DownloadCloudZipAsync(w, version?.CloudVersion, staged.FilePath, ct);
            else throw new InvalidOperationException("Download failed.");

            // The card owns the window from here, so hide this page's transfer controls first.
            ShowDownloadProgress(false);
            // No PreferredPackId: this page is not scoped to an instance, and the card pre-ticks
            // the only instance when there is one. The origin fields go on the library template,
            // which outlives the copies, so the save can be matched back to its listing.
            var outcome = await ImportContentCard.ShowAsync(_shell, new ImportRequest(
                ImportContentKind.World, packs, PrePickedPath: staged.FilePath,
                SourceIsFixed: true,
                DisplayName: row.Title,
                OriginSource: row.External?.Source, OriginProjectId: row.External?.Id,
                OriginVersionId: fetched?.Id, OriginVersionNumber: fetched?.VersionNumber));

            if (outcome is null)
            {
                SetStatus("Nothing was imported - the download was thrown away.");
                SetDownloadStatus("Nothing was imported - the download was thrown away.");
                return;
            }

            if (row.Internal is { } hostedWorld && savesBefore is not null)
                await LinkCopiesAsync(hostedWorld.Id, outcome.Targets, savesBefore);

            // The card invalidates the worlds scan for every instance it wrote to, so the Worlds page
            // re-reads from disk; its receipt already names what happened per instance.
            SetStatus(outcome.Summary, success: true);
            SetDownloadStatus(outcome.Summary, success: true);
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
            ShowDownloadProgress(false);
            if (button is not null) button.IsEnabled = true;
            if (ReferenceEquals(_downloadCts, cts)) _downloadCts = null;
            cts.Dispose();
        }
    }

    /// <summary>The zip name a listing's title gives the download, or a plain stand-in when
    /// the title would not make a usable file name, such as one that is a device name (CON,
    /// NUL, COM1...).</summary>
    private static string ZipNameFor(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string((title ?? "").Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray()).Trim();
        if (PathSafety.IsSafeFileName(cleaned + ".zip")) return $"{title}.zip";
        AppLog.Log(nameof(WorldBrowserView), $"A listing title does not make a usable file name, so the download is named world.zip: {title}");
        return "world.zip";
    }

    /// <summary>The save folders each instance holds right now, by instance id.</summary>
    /// <remarks>Read without creating anything: an instance with no folder on this PC yet has no
    /// saves, and resolving it by name would make one.</remarks>
    private static Dictionary<Guid, HashSet<string>> SaveFoldersByInstance(IEnumerable<PackSummary> packs)
    {
        var map = new Dictionary<Guid, HashSet<string>>();
        foreach (var pack in packs) map[pack.Id] = SaveFolders(pack.Id);
        return map;
    }

    private static HashSet<string> SaveFolders(Guid packId)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var dir = App.State.Worlds.SavesDir(packId);
            if (Directory.Exists(dir))
                foreach (var sub in Directory.EnumerateDirectories(dir))
                    names.Add(Path.GetFileName(sub));
        }
        catch (Exception) { /* no folder for this instance yet, so no saves in it */ }
        return names;
    }

    /// <summary>
    /// Links every save the import just made to the hosted world it was downloaded from.
    /// </summary>
    /// <remarks>
    /// Once linked, the save's page publishes new versions to that world and manages its sharing
    /// instead of uploading a separate copy. The card does not report which folders it made, so
    /// they are found by diffing each instance's saves against the earlier snapshot.
    /// </remarks>
    private static async Task LinkCopiesAsync(
        Guid worldId, IReadOnlyList<Guid> targets, Dictionary<Guid, HashSet<string>> before)
    {
        var made = await Task.Run(() => targets
            .Distinct()
            .SelectMany(packId => SaveFolders(packId)
                .Where(folder => !before.TryGetValue(packId, out var had) || !had.Contains(folder))
                .Select(folder => (packId, folder)))
            .ToList());

        foreach (var (packId, folder) in made)
            App.State.Worlds.LinkSharedWorld(WorldService.Key(packId, folder), worldId);
        if (made.Count > 0)
            AppLog.Log("worlds", $"Linked {made.Count} downloaded save(s) to hosted world {worldId}.");
    }

    /// <summary>Fetches the save and returns the version it actually took.</summary>
    /// <remarks>The row's Download button pins no version, so the newest is resolved here, and the
    /// provenance must name that one.</remarks>
    private async Task<ModVersion> DownloadCurseZipAsync(ModSummary mod, ModVersion? selectedVersion, string destPath,
                                                         CancellationToken ct)
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

        SetDownloadStatus($"Downloading '{mod.Name}'...");
        await App.State.Modrinth.DownloadFileAsync(url, destPath, ByteProgress($"Downloading '{mod.Name}'"), ct);
        return latest;
    }

    private async Task DownloadCloudZipAsync(SharedWorldSummary w, SharedWorldVersionInfo? selectedVersion,
                                             string destPath, CancellationToken ct)
    {
        var detail = await App.State.Api.GetSharedWorldAsync(w.Id, ct);
        var version = selectedVersion ?? detail.Versions.OrderByDescending(v => v.PublishedAt).FirstOrDefault()
            ?? throw new InvalidOperationException("This world has no versions yet.");
        SetDownloadStatus($"Downloading '{w.Name}'...");

        // Copied in chunks rather than with CopyToAsync so the bar can move: the version record
        // already carries the total, so this is determinate from the first byte.
        var progress = ByteProgress($"Downloading '{w.Name}'");
        await using var stream = await App.State.Api.DownloadSharedWorldVersionAsync(w.Id, version.Id, ct);
        await using (var fs = File.Create(destPath))
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
        ShowOverview("Loading...");
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
        ShowOverview("Loading...");
        ScreenshotsEmptyText.Text = "CloudLauncher world screenshots are not supported yet.";
        ScreenshotsEmptyText.Visibility = Visibility.Visible;
        ScreenshotList.ItemsSource = null;
        ScreenshotList.Visibility = Visibility.Collapsed;
        VersionsGrid.ItemsSource = null;

        try
        {
            var detail = await App.State.Api.GetSharedWorldAsync(world.Id, ct);
            if (ct.IsCancellationRequested) return;
            ShowOverview(detail.Description ?? detail.Summary, isMarkdown: true);

            var canDownload = detail.EffectivePermissions.HasFlag(PackPermissions.Download);
            var hint = canDownload
                ? "Download this version"
                : $"You can see this world but not download it. Ask {detail.OwnerUsername} for download access.";
            VersionsGrid.ItemsSource = detail.Versions
                .OrderByDescending(v => v.PublishedAt)
                .Select(v => WorldVersionRow.FromCloud(v, canDownload, hint))
                .ToList();
            ShowHostedActions(detail);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ShowOverview("Error: " + ex.Message);
        }
    }

    // -- managing a hosted world ----------------------------------------------

    /// <summary>Offers Manage access to whoever may manage the selected world, and Leave to somebody
    /// it was shared with directly.</summary>
    private void ShowHostedActions(SharedWorldDetail detail)
    {
        _currentHosted = detail;
        var me = App.State.Settings.UserId;
        var isOwner = me is { } owner && detail.OwnerId == owner;
        var canManage = isOwner || detail.EffectivePermissions.HasFlag(PackPermissions.ManageCollaborators);
        var direct = !isOwner && me is { } person && detail.Collaborators.Any(c => c.UserId == person);

        ManageAccessButton.Visibility = canManage ? Visibility.Visible : Visibility.Collapsed;
        LeaveWorldButton.Visibility = direct ? Visibility.Visible : Visibility.Collapsed;
        LeaveWorldButton.ToolTip =
            $"Give back the access {detail.OwnerUsername} gave you. Saves you already downloaded stay where they are.";
    }

    private void HideHostedActions()
    {
        _currentHosted = null;
        ManageAccessButton.Visibility = Visibility.Collapsed;
        LeaveWorldButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>Opens the selected world's access list (the same dialog its own page opens).</summary>
    private async void OnManageAccess(object sender, RoutedEventArgs e)
    {
        if (_currentHosted is not { } shown) return;
        try
        {
            // Read again first: the dialog edits the list it is handed, and the one on screen may be
            // minutes old.
            var detail = await App.State.Api.GetSharedWorldAsync(shown.Id);
            new PermissionsDialog(detail) { Owner = _shell }.ShowDialog();
            if (_currentRow is { Internal: { } w } row && w.Id == shown.Id) await LoadCloudDetailAsync(row);
        }
        catch (Exception ex)
        {
            SetDownloadStatus("Its access list could not be opened: " + ContentBundleService.Explain(ex, ex.Message), danger: true);
        }
    }

    /// <summary>Gives back the access the owner granted, after asking.</summary>
    /// <remarks>If this person can no longer see the world at all, saves on this PC linked to
    /// it are unlinked so their pages stop offering controls for it. The saves themselves are
    /// not touched.</remarks>
    private async void OnLeaveWorld(object sender, RoutedEventArgs e)
    {
        if (_currentHosted is not { } detail || App.State.Settings.UserId is not { } me) return;
        try
        {
            if (!await AppDialog.ConfirmAsync(_shell, "Leave world",
                    $"Leave '{detail.Name}'? You lose the access {detail.OwnerUsername} gave you, and only they "
                    + "can give it back. Saves you already downloaded stay on this PC. If one of your teams "
                    + "also has the world, you keep what the team gives.",
                    "Leave", "Cancel", danger: true))
                return;

            LeaveWorldButton.IsEnabled = false;
            await App.State.Api.RemoveSharedWorldCollaboratorAsync(detail.Id, me);
        }
        catch (Exception ex)
        {
            SetDownloadStatus("Could not leave: " + ContentBundleService.Explain(ex, ex.Message), danger: true);
            return;
        }
        finally { LeaveWorldButton.IsEnabled = true; }

        var stillVisible = true;
        try { await App.State.Api.GetSharedWorldAsync(detail.Id); }
        catch (Exception) { stillVisible = false; }

        if (!stillVisible)
        {
            foreach (var key in App.State.Settings.Worlds
                         .Where(kv => kv.Value.SharedWorldId == detail.Id)
                         .Select(kv => kv.Key)
                         .ToList())
            {
                App.State.Worlds.UnlinkSharedWorld(key);
                App.State.Worlds.SetSharingEnabled(key, false);
            }
        }

        SetStatus(stillVisible
            ? $"You left {detail.Name}. What is left comes from a team or from it being public."
            : $"You left {detail.Name}. Saves you downloaded stay on this PC as ordinary local worlds.",
            success: true);
        await ResetAndLoadAsync();
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
        DownloadSelectedButton.IsEnabled = row.CanDownload;
        DownloadSelectedButton.ToolTip = row.DownloadToolTip;
        // Until the hosted detail arrives nothing says what this person may do with it.
        HideHostedActions();
    }

    private void ClearDetail()
    {
        _currentRow = null;
        _currentProjectUrl = null;
        _externalVersions.Clear();
        HideHostedActions();
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
        if (!SafeLaunch.OpenUrl(url)) DownloadStatus.Text = "That link could not be opened.";
    }

    /// <summary>
    /// Puts the selected world's icon in the detail header.
    /// </summary>
    /// <remarks>Uses the shared cache, since the list has already decoded this URL. A late result is
    /// checked against the URL still selected, so a slow icon cannot overwrite a newer one.</remarks>
    private async void SetSelectedIcon(string? iconUrl)
    {
        SelectedIconImage.Source = null;
        if (string.IsNullOrWhiteSpace(iconUrl)) return;

        try
        {
            var wanted = iconUrl;
            var image = await ModIconCache.LoadAsync(wanted, 44);
            if (_currentRow?.IconUrl == wanted) SelectedIconImage.Source = image;
        }
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
        /// False for a hosted world that has no uploaded version yet (a world can be created on the
        /// server before anything is uploaded).
        /// </summary>
        public bool HasFile => Internal is null || Internal.VersionCount > 0;

        /// <summary>False for a hosted world shared with this person as "can view" only; the server
        /// refuses those downloads.</summary>
        public bool MayDownload => Internal is null || Internal.EffectivePermissions.HasFlag(PackPermissions.Download);

        public bool CanDownload => HasFile && MayDownload;

        public string DownloadLabel => HasFile ? "Download" : "No file";

        public string DownloadToolTip =>
            !HasFile ? "The owner has published this world's page but has not uploaded a save to it yet."
            : !MayDownload ? $"You can see this world but not download it. Ask {Internal!.OwnerUsername} for download access."
            : "Download it, then tick the instances that should get a copy - or none, and keep it as a "
            + "template for new instances";

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

        /// <summary>Whether this person may download this version; a hosted world shared as "can view"
        /// may not.</summary>
        public bool CanDownload { get; init; } = true;
        public string DownloadHint { get; init; } = "Download this version";

        public static WorldVersionRow FromExternal(ModVersion version) => new()
        {
            ExternalVersion = version,
            VersionNumber = version.VersionNumber,
            McVersion = string.Join(", ", version.GameVersions.Take(2)) + (version.GameVersions.Length > 2 ? "..." : ""),
            DateLabel = TimeFormat.Date(version.DatePublished),
            SizeLabel = version.Files.FirstOrDefault()?.Size is long size ? FormatSize(size) : ""
        };

        public static WorldVersionRow FromCloud(SharedWorldVersionInfo version, bool canDownload, string hint) => new()
        {
            CloudVersion = version,
            VersionNumber = version.VersionString,
            McVersion = version.McVersion ?? "",
            DateLabel = TimeFormat.Date(version.PublishedAt),
            SizeLabel = FormatSize(version.FileSize),
            CanDownload = canDownload,
            DownloadHint = hint
        };
    }
}
