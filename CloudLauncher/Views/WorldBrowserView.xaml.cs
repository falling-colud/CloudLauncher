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
        Loaded += async (_, _) => await InitAsync();
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
        catch (Exception ex) { StatusLabel.Text = "Error: " + ex.Message; _hasMore = false; }
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
            classId: CurseForgeService.ClassIdWorlds, ct: ct);
        foreach (var m in hits) _rows.Add(RowFromCurse(m));
        return hits.Count;
    }

    private async Task<int> LoadCloudLauncherAsync(WorldBrowseSource source, Guid? teamId, CancellationToken ct)
    {
        var page = await App.State.Api.BrowseWorldsAsync(source, teamId, _searchText, _filterMcVersion,
            offset: _offset, limit: PageSize, ct: ct);
        foreach (var w in page.Items) _rows.Add(RowFromCloud(w));
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
        await DownloadRowAsync(row, b);
    }

    private async void OnDownloadSelectedWorld(object sender, RoutedEventArgs e)
    {
        if (_currentRow is not null)
            await DownloadRowAsync(_currentRow, sender as Button);
    }

    private async void OnDownloadVersion(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: WorldVersionRow row }) return;
        if (_currentRow is null) return;
        await DownloadRowAsync(_currentRow, sender as Button, row);
    }

    private async Task DownloadRowAsync(WorldBrowseRow row, Button? button, WorldVersionRow? version = null)
    {
        var packs = await App.State.Api.ListPacksAsync();
        if (packs.Count == 0)
        {
            MessageBox.Show(_shell, "You have no instances yet. Create one before downloading a world.",
                "No instance", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var picker = new PackPickerDialog(packs,
            "Pick an instance",
            "The world will land in this instance's saves/ folder.",
            "Pick") { Owner = _shell };
        if (picker.ShowDialog() != true || picker.SelectedPackId is null) return;
        var packId = picker.SelectedPackId.Value;

        if (button is not null) button.IsEnabled = false;
        try
        {
            string? zipPath = null;
            if (row.Kind == WorldBrowseRowKind.CurseForge && row.External is { } ext)
                zipPath = await DownloadCurseZipAsync(ext, version?.ExternalVersion);
            else if (row.Kind == WorldBrowseRowKind.CloudLauncher && row.Internal is { } w)
                zipPath = await DownloadCloudZipAsync(w, version?.CloudVersion);

            if (zipPath is null) throw new InvalidOperationException("Download failed.");

            var savesDir = Path.Combine(App.State.Packs.GameDir(packId), "saves");
            Directory.CreateDirectory(savesDir);
            var folderName = SafeFolderName(row.Title);
            var targetDir = UniqueDir(savesDir, folderName);

            StatusLabel.Foreground = (Brush)FindResource("TextSecondaryBrush");
            StatusLabel.Text = "Extracting…";
            ZipFile.ExtractToDirectory(zipPath, targetDir);
            FlattenIfSingleSubdir(targetDir);
            try { File.Delete(zipPath); } catch { }
            StatusLabel.Foreground = (Brush)FindResource("AccentBrush");
            StatusLabel.Text = $"Saved to {targetDir}";
            DownloadStatus.Foreground = (Brush)FindResource("AccentBrush");
            DownloadStatus.Text = $"Saved to {targetDir}";
        }
        catch (Exception ex)
        {
            StatusLabel.Foreground = (Brush)FindResource("DangerBrush");
            StatusLabel.Text = "Download failed: " + ex.Message;
            DownloadStatus.Foreground = (Brush)FindResource("DangerBrush");
            DownloadStatus.Text = "Download failed: " + ex.Message;
        }
        finally
        {
            if (button is not null) button.IsEnabled = true;
        }
    }

    private async Task<string> DownloadCurseZipAsync(ModSummary mod, ModVersion? selectedVersion = null)
    {
        StatusLabel.Foreground = (Brush)FindResource("TextSecondaryBrush");
        StatusLabel.Text = $"Fetching '{mod.Name}'…";
        if (!int.TryParse(mod.Id, out var cfModId)) throw new InvalidOperationException("Bad CurseForge id.");
        var latest = selectedVersion;
        if (latest is null)
        {
            var versions = await App.State.CurseForge.GetVersionsAsync(cfModId);
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
        StatusLabel.Text = $"Downloading '{mod.Name}'…";
        DownloadStatus.Text = StatusLabel.Text;
        await App.State.Modrinth.DownloadFileAsync(url, tmp, null, _cts.Token);
        return tmp;
    }

    private async Task<string> DownloadCloudZipAsync(SharedWorldSummary w, SharedWorldVersionInfo? selectedVersion = null)
    {
        var detail = await App.State.Api.GetSharedWorldAsync(w.Id);
        var version = selectedVersion ?? detail.Versions.OrderByDescending(v => v.PublishedAt).FirstOrDefault()
            ?? throw new InvalidOperationException("This world has no versions yet.");
        var tmp = Path.Combine(Path.GetTempPath(), $"cl-world-{Guid.NewGuid():N}.zip");
        StatusLabel.Foreground = (Brush)FindResource("TextSecondaryBrush");
        StatusLabel.Text = $"Downloading '{w.Name}'…";
        DownloadStatus.Text = StatusLabel.Text;
        await using var stream = await App.State.Api.DownloadSharedWorldVersionAsync(w.Id, version.Id);
        await using (var fs = File.Create(tmp)) await stream.CopyToAsync(fs);
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

    private static string SafeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        var s = sb.ToString().Trim();
        if (string.IsNullOrEmpty(s)) s = "world";
        if (s.Length > 80) s = s[..80];
        return s;
    }

    private static string UniqueDir(string parent, string baseName)
    {
        var candidate = Path.Combine(parent, baseName);
        var n = 1;
        while (Directory.Exists(candidate)) candidate = Path.Combine(parent, $"{baseName} ({++n})");
        return candidate;
    }

    private static void FlattenIfSingleSubdir(string dir)
    {
        // Many world saves zip as outerName/innerLevel.dat. If the extracted dir holds a single
        // subfolder containing level.dat, hoist that subfolder's contents to the parent.
        if (File.Exists(Path.Combine(dir, "level.dat"))) return;
        var subs = Directory.GetDirectories(dir);
        var files = Directory.GetFiles(dir);
        if (subs.Length == 1 && files.Length == 0)
        {
            var sub = subs[0];
            foreach (var f in Directory.GetFiles(sub))
                File.Move(f, Path.Combine(dir, Path.GetFileName(f)));
            foreach (var d in Directory.GetDirectories(sub))
                Directory.Move(d, Path.Combine(dir, Path.GetFileName(d)));
            Directory.Delete(sub, recursive: true);
        }
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
