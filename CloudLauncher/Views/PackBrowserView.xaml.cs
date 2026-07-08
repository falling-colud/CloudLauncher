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
    private List<ModVersion> _allVersions = new();
    private string? _currentProjectUrl;
    private string? _cachedDescription;
    private bool _descriptionIsMarkdown;

    private string _searchText = "";
    private string? _filterMcVersion;
    private string? _filterLoader;

    private CancellationTokenSource _cts = new();
    private bool _isLoading;
    private bool _hasMore;
    private int _offset;
    private const int PageSize = 25;
    private const int MaxResults = 200;

    public PackBrowserView(MainWindow shell)
    {
        InitializeComponent();
        _shell = shell;
        SourceStrip.ItemsSource = _chips;
        ResultsList.ItemsSource = _rows;
        PackTabs.SelectionChanged += OnPackTabChanged;
        App.State.ModpackDownload.PackAdded += OnPackAdded;
        Loaded += async (_, _) => await InitAsync();
        Unloaded += (_, _) => App.State.ModpackDownload.PackAdded -= OnPackAdded;
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
        await RefreshAddedPacksAsync();
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
            DownloadSelectedButton.Visibility = Visibility.Collapsed;
            if (VersionsGrid.ItemsSource is List<PackVersionRow> rows)
                VersionsGrid.ItemsSource = rows.Select(v => v with { ActionVisibility = Visibility.Collapsed }).ToList();
        }
    }

    private async Task BuildChipsAsync()
    {
        _chips.Clear();
        _chips.Add(NewChip("", "CurseForge", SourceKind.CurseForge));
        _chips.Add(NewChip("", "Modrinth", SourceKind.Modrinth));
        _chips.Add(NewChip("", "CloudLauncher Public", SourceKind.CloudLauncherPublic));
        _chips.Add(NewChip("", "Shared with me", SourceKind.CloudLauncherShared));

        try
        {
            var teams = await App.State.Api.ListTeamsAsync();
            foreach (var t in teams)
                _chips.Add(NewChip("", t.Name, SourceKind.CloudLauncherTeam, t.Id));
        }
        catch { /* teams unavailable — proceed without */ }

        ApplyChipStyles();
    }

    private SourceChipRow NewChip(string icon, string label, SourceKind kind, Guid? teamId = null) =>
        new() { Icon = icon, Label = label, Kind = kind, TeamId = teamId };

    private void ApplyChipStyles()
    {
        foreach (var c in _chips)
        {
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
        if (sender is FrameworkElement fe && fe.DataContext is SourceChipRow row)
            await SelectChipAsync(row);
    }

    private async Task SelectChipAsync(SourceChipRow chip)
    {
        _activeChip = chip;
        ApplyChipStyles();
        await ResetAndLoadAsync();
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => _searchText = SearchBox.Text;

    private async void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await ResetAndLoadAsync();
        }
        else if (e.Key == Key.Escape)
        {
            SearchBox.Text = "";
            _searchText = "";
            await ResetAndLoadAsync();
        }
    }

    private void OnFiltersClick(object sender, RoutedEventArgs e) => FiltersPopup.IsOpen = !FiltersPopup.IsOpen;

    private async void OnApplyFilters(object sender, RoutedEventArgs e)
    {
        _filterMcVersion = string.IsNullOrWhiteSpace(FilterMcBox.Text) ? null : FilterMcBox.Text.Trim();
        var loaderItem = FilterLoaderBox.SelectedItem as ComboBoxItem;
        var loader = loaderItem?.Content as string;
        _filterLoader = (string.IsNullOrEmpty(loader) || loader == "(any)") ? null : loader;
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
        LoadingBar.Visibility = Visibility.Visible;

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
            CountLabel.Text = _rows.Count == 0 ? "" : $"{_rows.Count} result{(_rows.Count == 1 ? "" : "s")}";
            EmptyState.Visibility = (_rows.Count == 0 && !_hasMore) ? Visibility.Visible : Visibility.Collapsed;
            if (_rows.Count == 0 && !_hasMore)
            {
                EmptyTitle.Text = "No results";
                EmptyDetail.Text = _activeChip.Kind switch
                {
                    SourceKind.CloudLauncherPublic => "No public packs match your search.",
                    SourceKind.CloudLauncherShared => "Nobody has shared a pack with you yet.",
                    SourceKind.CloudLauncherTeam   => "This team has no packs yet.",
                    _ => "Try a different search or filter."
                };
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
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

    private async Task<int> LoadCloudLauncherAsync(PackBrowseSource source, Guid? teamId, CancellationToken ct)
    {
        var page = await App.State.Api.BrowsePacksAsync(source, teamId, _searchText, _offset, PageSize, ct);
        foreach (var p in page.Items) _rows.Add(RowFromInternal(p));
        _hasMore = _offset + page.Items.Count < page.Total;
        return page.Items.Count;
    }

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
            MetaLabel = $"by {p.OwnerUsername} · {FormatLoader(p.Loader, p.LoaderVersion)} · {p.MinecraftVersion ?? "—"}",
            Initial = InitialFor(p.Name),
            SourceBadge = "CloudLauncher",
            SourceBadgeBackground = (Brush)FindResource("AccentSoftBrush"),
            SourceBadgeForeground = (Brush)FindResource("AccentBrush"),
            ActionLabel = "Add"
        };
        ApplyInternalActionState(row);
        return row;
    }

    // ── detail panel ─────────────────────────────────────────────────────────

    private async void OnResultSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsList.SelectedItem is not PackBrowseRow row) return;
        await LoadRowDetailAsync(row);
    }

    private async void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsList.SelectedItem is PackBrowseRow row)
            await DownloadRowAsync(row, null);
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
        PackTabs.SelectedIndex = 0;
        OverviewBrowser.Visibility = Visibility.Visible;
        OverviewBrowser.Show("Loading…");
        ScreenshotsEmptyText.Text = "Loading screenshots…";
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
            OverviewBrowser.Show("Error: " + ex.Message);
        }
    }

    private async Task LoadExternalDetailAsync(ModSummary mod, CancellationToken ct)
    {
        _currentProjectUrl = BuildProjectUrl(mod);
        OpenProjectButton.Visibility = Visibility.Visible;
        ConfigureLinks(new ModProjectLinks(_currentProjectUrl, null, null, null, null));

        var detail = mod.Source == ModSource.CurseForge
            ? await App.State.CurseForge.GetProjectDetailAsync(int.TryParse(mod.Id, out var cid) ? cid : 0, ct)
            : await App.State.Modrinth.GetProjectDetailAsync(mod.Id, ct);

        _cachedDescription = detail.Description ?? mod.Description;
        _descriptionIsMarkdown = mod.Source == ModSource.Modrinth && !PackText.LooksLikeHtml(_cachedDescription);
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

        ScreenshotsEmptyText.Text = "Screenshots aren't available for CloudLauncher packs.";
        ScreenshotsEmptyText.Visibility = Visibility.Visible;
        ScreenshotList.ItemsSource = null;
        ScreenshotList.Visibility = Visibility.Collapsed;

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

            VersionsGrid.ItemsSource = new List<PackVersionRow>
            {
                new()
                {
                    VersionNumber = detail.MinecraftVersion ?? "Latest",
                    McVersions = detail.MinecraftVersion ?? "—",
                    LoaderList = FormatLoader(detail.Loader, detail.LoaderVersion),
                    ReleaseChannel = detail.Visibility.ToString(),
                    DateLabel = detail.UpdatedAt.LocalDateTime.ToString("yyyy-MM-dd"),
                    SizeLabel = "",
                    ActionLabel = "Add",
                    ActionVisibility = IsInternalPackAdded(detail.Id) ? Visibility.Collapsed : Visibility.Visible,
                    IsInternal = true
                }
            };
            VersionFilterNote.Text = "(CloudLauncher catalog)";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ShowOverviewDescription(
                (summary.Summary ?? summary.Description ?? "") +
                Environment.NewLine + Environment.NewLine +
                "Error loading detail: " + ex.Message,
                false);
        }
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
        await DownloadRowAsync(row, b);
    }

    private async void OnDownloadSelectedPack(object sender, RoutedEventArgs e) =>
        await DownloadCurrentAsync(sender as Button);

    private async void OnDownloadVersion(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PackVersionRow versionRow }) return;
        if (versionRow.IsInternal)
        {
            if (_currentRow is not null)
                await DownloadRowAsync(_currentRow, sender as Button);
            return;
        }

        if (_currentExternal is null || versionRow.Source is null) return;
        await DownloadExternalAsync(_currentExternal, versionRow.Source, sender as Button);
    }

    private async Task DownloadCurrentAsync(Button? button)
    {
        if (_currentRow is null) return;
        await DownloadRowAsync(_currentRow, button);
    }

    private async Task DownloadRowAsync(PackBrowseRow row, Button? button)
    {
        if (row.Kind == PackBrowseRowKind.Internal && row.Internal is { } internalPack && IsInternalPackAdded(internalPack.Id))
            return;

        if (button is not null) button.IsEnabled = false;
        try
        {
            if (row.Kind == PackBrowseRowKind.Internal && row.Internal is not null)
            {
                var pack = await App.State.ModpackDownload.SubscribeInternalPackAsync(row.Internal);
                StatusLabel.Foreground = (Brush)FindResource("AccentBrush");
                StatusLabel.Text = $"Added '{pack.Name}' to your packs.";
                DownloadStatus.Foreground = (Brush)FindResource("AccentBrush");
                DownloadStatus.Text = StatusLabel.Text;
                OnPackAdded(pack);
            }
            else if (row.External is not null)
            {
                await DownloadExternalAsync(row.External, selectedVersion: null, button);
            }
        }
        catch (Exception ex)
        {
            StatusLabel.Foreground = (Brush)FindResource("DangerBrush");
            StatusLabel.Text = "Download failed: " + ex.Message;
            DownloadStatus.Foreground = (Brush)FindResource("DangerBrush");
            DownloadStatus.Text = StatusLabel.Text;
        }
        finally
        {
            if (button is not null) button.IsEnabled = true;
        }
    }

    private async Task DownloadExternalAsync(ModSummary mod, ModVersion? selectedVersion, Button? button)
    {
        StatusLabel.Foreground = (Brush)FindResource("TextSecondaryBrush");
        StatusLabel.Text = $"Starting download for '{mod.Name}'…";
        DownloadStatus.Text = StatusLabel.Text;

        var metadata = new PackImportMetadata(
            mod.Description?.Trim(),
            null,
            _cachedDescription,
            mod.IconUrl,
            mod.Source.ToString(),
            _descriptionIsMarkdown);

        await App.State.ModpackDownload.StartExternalDownloadAsync(mod, selectedVersion, metadata);

        StatusLabel.Foreground = (Brush)FindResource("AccentBrush");
        StatusLabel.Text = $"'{mod.Name}' is downloading — check your instances.";
        DownloadStatus.Foreground = (Brush)FindResource("AccentBrush");
        DownloadStatus.Text = StatusLabel.Text;
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
        VersionFilterNote.Text = "";
        DownloadStatus.Text = "";
        OpenProjectButton.Visibility = Visibility.Collapsed;
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

    private void ApplyInternalActionState(PackBrowseRow row)
    {
        if (row.Internal is not { } pack) return;
        row.ActionVisibility = IsInternalPackAdded(pack.Id) ? Visibility.Collapsed : Visibility.Visible;
    }

    // ── view-models ──────────────────────────────────────────────────────────

    public enum SourceKind
    {
        CurseForge,
        Modrinth,
        CloudLauncherPublic,
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
    }

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
            McVersions = string.Join(", ", version.GameVersions.Take(3)) + (version.GameVersions.Length > 3 ? "…" : ""),
            LoaderList = string.Join(", ", version.Loaders),
            ReleaseChannel = version.ReleaseChannel,
            DateLabel = version.DatePublished.LocalDateTime.ToString("yyyy-MM-dd"),
            SizeLabel = version.Files.FirstOrDefault()?.Size is long s
                ? s > 1024 * 1024 ? $"{s / (1024.0 * 1024):F1} MB" : $"{s / 1024.0:F0} KB"
                : ""
        };
    }
}
