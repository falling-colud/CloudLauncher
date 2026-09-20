using System.IO;
using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

public partial class ModExplorerWindow : Window
{
    private readonly PackDetail _pack;
    private List<ModSummary> _results = new();
    private List<ModVersion> _allVersions = new();
    private ModSummary? _currentMod;
    private CancellationTokenSource _cts = new();
    private bool _isInitializing = true;

    public ModExplorerWindow(PackDetail pack)
    {
        InitializeComponent();
        _pack = pack;
        Title = $"Mod Explorer — {pack.Name}";
        PackContextLabel.Text = $"Adding mods to {pack.Name} · {PackVersionLabel()}";
        ModTabs.SelectionChanged += OnModTabsChanged;

        _isInitializing = false;
        Loaded += (_, _) => OnSearch(this, new RoutedEventArgs());
    }

    // The Overview tab hosts a WebView2 whose HWND draws over WPF (airspace), so hide it
    // off-tab. Scrolling is native — no manual wheel routing.
    private void OnModTabsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != ModTabs) return;
        var onOverview = ModTabs.SelectedIndex == 0;
        OverviewHost.Visibility = onOverview ? Visibility.Visible : Visibility.Collapsed;
        OverviewBrowser.Visibility = onOverview ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowOverview(string? content, bool isMarkdown = false) => OverviewBrowser.Show(content, isMarkdown);

    // ── search ────────────────────────────────────────────────────────────────

    private void OnSearchKeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnSearch(s, new RoutedEventArgs());
    }
    private void OnFilterChanged(object s, SelectionChangedEventArgs e)
    {
        if (_isInitializing || !IsLoaded) return;
        OnSearch(s, new RoutedEventArgs());
    }

    private async void OnSearch(object sender, RoutedEventArgs e)
    {
        _cts.Cancel(); _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var query = SearchBox.Text.Trim();
        var mc = PackMinecraftVersion();
        var loaderTag = PackLoaderTag();
        var source = (SourceBox.SelectedItem as ComboBoxItem)?.Content as string ?? "Modrinth";

        SearchStatus.Text = "Searching...";
        ResultsList.ItemsSource = null;
        ClearSelectedMod();
        try
        {
            _results = source == "CurseForge"
                ? await App.State.CurseForge.SearchAsync(query, mc.Length > 0 ? mc : null, loaderTag.Length > 0 ? loaderTag : null, ct: ct)
                : await App.State.Modrinth.SearchAsync(query, mc.Length > 0 ? mc : null, loaderTag.Length > 0 ? loaderTag : null, ct: ct);

            ResultsList.ItemsSource = _results.Select(m => new ModResultRow(m)).ToList();
            SearchStatus.Text = $"{_results.Count} result(s)";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SearchStatus.Text = "Error: " + ex.Message; }
    }

    private async void OnResultSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsList.SelectedItem is not ModResultRow row) return;
        await LoadModDetailAsync(row.Source);
    }

    private async void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsList.SelectedItem is not ModResultRow row) return;
        await LoadModDetailAsync(row.Source);
    }

    private async Task LoadModDetailAsync(ModSummary mod)
    {
        _cts.Cancel(); _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _currentMod = mod;
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
        var loader = PackLoaderTag();
        var filtered = all ? _allVersions : _allVersions.Where(v => MatchesFilters(v, mc, loader)).ToList();

        VersionFilterNote.Text = all ? $"(showing all {_allVersions.Count})" : $"(filtered: {filtered.Count}/{_allVersions.Count})";
        VersionsGrid.ItemsSource = filtered.Select(v => new VersionRow(v)).ToList();
    }

    private void OnShowAllVersionsChanged(object s, RoutedEventArgs e) =>
        ShowVersions(ShowAllVersionsBox.IsChecked == true);

    // ── download ──────────────────────────────────────────────────────────────

    private async void OnQuickDownloadMod(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.DataContext is not ModResultRow row) return;
        await QuickDownloadAsync(row.Source);
    }

    private async void OnDownloadVersion(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.DataContext is not VersionRow row) return;
        if (_currentMod is null) return;

        await DownloadVersionAsync(_currentMod, row.Source);
    }

    private async Task QuickDownloadAsync(ModSummary mod)
    {
        DownloadStatus.Text = $"Finding latest compatible file for {mod.Name}...";
        try
        {
            if (!CanInstallModsToPack())
            {
                DownloadStatus.Text = "Pick a non-empty Fabric, Forge, or NeoForge profile before installing mods.";
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
        if (!CanInstallModsToPack())
        {
            DownloadStatus.Text = "Pick a non-empty Fabric, Forge, or NeoForge profile before installing mods.";
            return;
        }

        if (!MatchesFilters(version, PackMinecraftVersion(), PackLoaderTag()))
        {
            DownloadStatus.Text = $"{version.VersionNumber} does not support {PackVersionLabel()}.";
            return;
        }

        var target = (DownloadTargetBox.SelectedItem as ComboBoxItem)?.Content as string ?? "game/mods/";
        var folder = target switch
        {
            "local/mods/" => Path.Combine(App.State.Packs.LocalDir(_pack.Id), "mods"),
            _             => Path.Combine(App.State.Packs.GameDir(_pack.Id), "mods")
        };
        Directory.CreateDirectory(folder);

        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.IsIndeterminate = true;
        DownloadProgress.Value = 0;
        DownloadStatus.Text = $"Resolving dependencies for {mod.Name}...";
        try
        {
            var progress = new Progress<(long done, long total)>(p =>
            {
                if (p.total > 0)
                {
                    DownloadProgress.IsIndeterminate = false;
                    DownloadProgress.Value = (double)p.done / p.total * 100;
                }
            });

            var downloads = await ModDependencyResolver.ResolveRequiredDownloadsAsync(
                mod,
                version,
                PackMinecraftVersion(),
                PackLoaderTag(),
                App.State.Modrinth,
                App.State.CurseForge,
                PackChannel());
            if (downloads.Count == 0)
            {
                DownloadStatus.Text = "No downloadable file found.";
                return;
            }

            var saved = 0;
            var skipped = 0;
            foreach (var item in downloads)
            {
                var dest = Path.Combine(folder, item.File.Filename);
                if (item.IsDependency && File.Exists(dest))
                {
                    skipped++;
                    continue;
                }

                DownloadProgress.IsIndeterminate = true;
                DownloadProgress.Value = 0;
                DownloadStatus.Text = item.IsDependency
                    ? $"Downloading dependency {item.File.Filename}..."
                    : $"Downloading {item.File.Filename}...";
                await App.State.Modrinth.DownloadFileAsync(item.File.DownloadUrl, dest, progress);
                saved++;
            }

            DownloadProgress.Value = 100;
            var skippedText = skipped > 0 ? $" ({skipped} already present)" : "";
            DownloadStatus.Text = saved == 1 && downloads.Count == 1
                ? $"Saved to {target}{downloads[0].File.Filename}"
                : $"Saved {saved} file(s) to {target}{skippedText}";
        }
        catch (Exception ex)
        {
            DownloadStatus.Text = "Download failed: " + ex.Message;
        }
        finally
        {
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Value = 0;
            DownloadProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async Task<List<ModVersion>> LoadVersionsForModAsync(ModSummary mod, bool applyFilters, CancellationToken ct)
    {
        var mcFilter = applyFilters ? PackMinecraftVersion() : "";
        var loaderFilter = applyFilters ? PackLoaderTag() : "";
        return mod.Source == ModSource.CurseForge
            ? await App.State.CurseForge.GetVersionsAsync(int.TryParse(mod.Id, out var cid) ? cid : 0, ct)
            : await App.State.Modrinth.GetVersionsAsync(
                mod.Id,
                mcFilter.Length > 0 ? mcFilter : null,
                loaderFilter.Length > 0 ? loaderFilter : null,
                ct);
    }

    private async Task<ModVersionFile?> ResolveDownloadFileAsync(ModSummary mod, ModVersion version)
    {
        var file = version.Files.FirstOrDefault(f => f.IsPrimary) ?? version.Files.FirstOrDefault();
        if (file is null || !string.IsNullOrWhiteSpace(file.DownloadUrl)) return file;

        if (version.Source != ModSource.CurseForge) return file;

        var ids = version.Id.Split(':', 2);
        var modId = ids.Length == 2 && int.TryParse(ids[0], out var parsedModId)
            ? parsedModId
            : int.TryParse(mod.Id, out var fallbackModId) ? fallbackModId : 0;
        var fileId = ids.Length == 2 && int.TryParse(ids[1], out var parsedFileId) ? parsedFileId : 0;
        if (modId == 0 || fileId == 0) return file;

        var url = await App.State.CurseForge.GetDownloadUrlAsync(modId, fileId);
        return string.IsNullOrWhiteSpace(url) ? file : file with { DownloadUrl = url };
    }

    private ModVersion? PickBestVersion(IReadOnlyList<ModVersion> versions)
    {
        if (!CanInstallModsToPack()) return null;

        var mc = PackMinecraftVersion();
        var loader = PackLoaderTag();
        var compatible = versions.Where(v => MatchesFilters(v, mc, loader));
        return ModUpdateChannel.PickNewest(compatible, PackChannel(), v => v.ReleaseChannel, v => v.DatePublished);
    }

    /// <summary>The release channel this pack's downloads follow (pack setting, else the launcher
    /// default in Settings → Mods).</summary>
    private string PackChannel() => App.State.ModMetadata.EffectiveUpdateChannel(_pack.Id);

    private static bool MatchesFilters(ModVersion version, string mc, string loader) =>
        (mc.Length == 0 || version.GameVersions.Contains(mc, StringComparer.OrdinalIgnoreCase)) &&
        (loader.Length == 0 || version.Loaders.Contains(loader, StringComparer.OrdinalIgnoreCase));

    private string PackMinecraftVersion() => _pack.MinecraftVersion?.Trim() ?? "";

    private string PackLoaderTag() =>
        _pack.Loader == LoaderKind.None ? "" : _pack.Loader.ToString().ToLowerInvariant();

    private bool CanInstallModsToPack() =>
        !_pack.IsEmpty
        && !string.IsNullOrWhiteSpace(_pack.MinecraftVersion)
        && _pack.Loader != LoaderKind.None;

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

    // ── helpers ───────────────────────────────────────────────────────────────

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
}

// ── row view-models ──────────────────────────────────────────────────────────

public sealed class ModResultRow(ModSummary src, bool isDownloaded = false)
{
    public ModSummary Source { get; } = src;
    public bool IsDownloaded { get; set; } = isDownloaded;
    public bool CanDownload => !IsDownloaded;
    public string DownloadTooltip => IsDownloaded
        ? "Already downloaded in this pack"
        : "Download the latest compatible file";
    public string Name => Source.Name;
    public string IconUrl => Source.IconUrl ?? "";
    public string Initial => string.IsNullOrWhiteSpace(Source.Name)
        ? "?"
        : Source.Name.Trim()[0].ToString().ToUpperInvariant();
    public string[] Categories => Source.Categories.Take(3).ToArray();
    public string MetaLabel =>
        $"by {Source.Author ?? "unknown"} · {FormatDownloads(Source.DownloadCount)} downloads · {Source.Source}";

    private static string FormatDownloads(long n) =>
        n >= 1_000_000 ? $"{n / 1_000_000.0:F1}M"
      : n >= 1_000     ? $"{n / 1000.0:F0}K"
      : n.ToString();
}

public sealed class VersionRow(
    ModVersion src,
    bool isDownloaded = false,
    bool isCompatible = true,
    string compatibilityLabel = "")
{
    public ModVersion Source { get; } = src;
    public bool IsDownloaded { get; set; } = isDownloaded;
    public bool IsCompatible { get; } = isCompatible;
    public bool CanDownload => !IsDownloaded && IsCompatible;
    public string DownloadTooltip =>
        IsDownloaded ? "Already downloaded in this pack"
        : !IsCompatible ? $"Not compatible with {compatibilityLabel}"
        : "Download this version";
    public string VersionNumber  => Source.VersionNumber;
    public string McVersions     => string.Join(", ", Source.GameVersions.Take(3)) + (Source.GameVersions.Length > 3 ? "…" : "");
    public string LoaderList     => string.Join(", ", Source.Loaders);
    public string ReleaseChannel => Source.ReleaseChannel;
    public string DateLabel      => Source.DatePublished.LocalDateTime.ToString("yyyy-MM-dd");
    public string SizeLabel      => Source.Files.FirstOrDefault()?.Size is long s
        ? s > 1024 * 1024 ? $"{s / (1024.0 * 1024):F1} MB" : $"{s / 1024.0:F0} KB"
        : "";
}

public sealed class MediaRow(ModMediaItem src)
{
    public string FullImageUrl => src.Url;
    public string ImageUrl => string.IsNullOrWhiteSpace(src.ThumbnailUrl) ? src.Url : src.ThumbnailUrl!;
    public string Title => string.IsNullOrWhiteSpace(src.Title)
        ? string.IsNullOrWhiteSpace(src.Description) ? "Screenshot" : src.Description!
        : src.Title!;
}
