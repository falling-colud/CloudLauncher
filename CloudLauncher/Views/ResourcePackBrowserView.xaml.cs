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
        Loaded += async (_, _) => await InitAsync();
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

    private void ShowOverview(string? content) => OverviewBrowser.Show(content);

    private async Task InitAsync()
    {
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
        await ResetAndLoadAsync();
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => _searchText = SearchBox.Text;

    private async void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await ResetAndLoadAsync(); }
        else if (e.Key == Key.Escape) { SearchBox.Text = ""; _searchText = ""; await ResetAndLoadAsync(); }
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

    private async Task<int> LoadCurseForgeAsync(CancellationToken ct)
    {
        var hits = await App.State.CurseForge.SearchAsync(
            _searchText, _filterMcVersion, loader: null,
            limit: PageSize, offset: _offset,
            classId: CurseForgeService.ClassIdResourcePacks, ct: ct);
        foreach (var m in hits) _rows.Add(RowFromCurseExternal(m));
        return hits.Count;
    }

    private async Task<int> LoadModrinthAsync(CancellationToken ct)
    {
        var hits = await App.State.Modrinth.SearchAsync(
            _searchText, _filterMcVersion, loader: null,
            limit: PageSize, offset: _offset, projectType: "resourcepack", ct: ct);
        foreach (var m in hits) _rows.Add(RowFromMrExternal(m));
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
            MessageBox.Show(_shell, "Create an instance first.",
                "No instance", MessageBoxButton.OK, MessageBoxImage.Information);
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

            var dir = ResourcePacksDir(pack);
            var dest = UniqueZipPath(dir, $"{SafeFileBase(row.Title)}.zip");

            StatusLabel.Foreground = (Brush)FindResource("TextSecondaryBrush");
            StatusLabel.Text = "Installing…";
            File.Copy(zipPath, dest, overwrite: false);
            try { File.Delete(zipPath); } catch { }

            StatusLabel.Foreground = (Brush)FindResource("AccentBrush");
            StatusLabel.Text = $"Installed to {dest}";
            DownloadStatus.Foreground = (Brush)FindResource("AccentBrush");
            DownloadStatus.Text = $"Installed to {dest}";
        }
        catch (Exception ex)
        {
            StatusLabel.Foreground = (Brush)FindResource("DangerBrush");
            StatusLabel.Text = "Install failed: " + ex.Message;
            DownloadStatus.Foreground = (Brush)FindResource("DangerBrush");
            DownloadStatus.Text = "Install failed: " + ex.Message;
        }
        finally
        {
            if (button is not null) button.IsEnabled = true;
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

            ShowOverview(detail.Description ?? mod.Description);
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
            ShowOverview(detail.Description ?? detail.Summary);
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
