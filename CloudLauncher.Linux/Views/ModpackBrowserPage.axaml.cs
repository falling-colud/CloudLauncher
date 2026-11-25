using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>
/// Modpacks on CurseForge and Modrinth, each installed as a new instance: the store half of the Windows
/// launcher's pack browser. Installing starts the import in the background
/// (<see cref="ModpackDownloadService.StartExternalDownloadAsync"/>) and goes to Instances, where its
/// progress shows on the new instance.
/// </summary>
public partial class ModpackBrowserPage : UserControl, IShellPage, IRefreshable
{
    private const int PageSize = 30;
    private const int MaxResults = 2000;

    private readonly ModCatalog _catalog;
    private readonly ObservableCollection<ModpackBrowseRow> _rows = new();
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(450) };

    private ModSource _store = ModSource.CurseForge;
    private string _searchText = "";
    private string _lastSearched = "";
    private string? _filterMc;
    private string? _filterLoader;
    private CancellationTokenSource _cts = new();
    private CancellationTokenSource _detailCts = new();
    private Task? _loading;
    private bool _isLoading;
    private bool _hasMore;
    private int _offset;
    private bool _started;
    private bool _installing;

    private ModSummary? _current;
    private string? _description;
    private bool _descriptionIsMarkdown;

    public ModpackBrowserPage() : this(ModCatalog.Stores) { }

    /// <param name="catalog">Where results come from (the stores, or a preview's own).</param>
    public ModpackBrowserPage(ModCatalog catalog)
    {
        InitializeComponent();
        _catalog = catalog;
        BodyGrid.SizeChanged += (_, e) => LayoutBody(e.NewSize.Width);
        ResultsList.ItemsSource = _rows;
        ResultsList.SelectionChanged += (_, _) =>
        {
            if (ResultsList.SelectedItem is ModpackBrowseRow row) _ = LoadDetailAsync(row.Source);
        };
        ResultsList.DoubleTapped += (_, _) =>
        {
            if (ResultsList.SelectedItem is ModpackBrowseRow row) _ = InstallAsync(row.Source, null);
        };
        ResultsList.AddHandler(ScrollViewer.ScrollChangedEvent, OnResultsScroll);
        ResultsState.RetryRequested += () => _ = ResetAndLoadAsync();

        SearchBox.TextChanged += (_, _) => { _searchDebounce.Stop(); _searchDebounce.Start(); };
        SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            _searchDebounce.Stop();
            _searchText = SearchBox.Text?.Trim() ?? "";
            _ = ResetAndLoadAsync();
            e.Handled = true;
        };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            _searchText = SearchBox.Text?.Trim() ?? "";
            if (!string.Equals(_searchText, _lastSearched, StringComparison.Ordinal)) _ = ResetAndLoadAsync();
        };
        McFilterBox.LostFocus += (_, _) => ApplyFilters();
        McFilterBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            ApplyFilters();
            e.Handled = true;
        };
        LoaderFilterBox.SelectionChanged += (_, _) => ApplyFilters();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.F || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);

        AttachedToVisualTree += (_, _) =>
        {
            if (_started) return;
            _started = true;
            _ = ResetAndLoadAsync();
        };
        DetachedFromVisualTree += (_, _) => _searchDebounce.Stop();
    }

    public ShellNav Nav => ShellNav.Modpacks;

    public Task RefreshAsync() => ResetAndLoadAsync();

    /// <summary>Below this width the picked item sits under the results instead of beside them.</summary>
    private const double NarrowWidth = 700;

    /// <summary>Results and detail side by side when both columns fit, stacked otherwise.</summary>
    private void LayoutBody(double width)
    {
        var narrow = width < NarrowWidth;
        BodyGrid.ColumnDefinitions[0].Width = new GridLength(narrow ? 1 : 5, GridUnitType.Star);
        BodyGrid.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 16);
        BodyGrid.ColumnDefinitions[2].Width = narrow ? new GridLength(0) : new GridLength(4, GridUnitType.Star);
        BodyGrid.RowDefinitions[1].Height = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(DetailPane, narrow ? 0 : 2);
        Grid.SetRow(DetailPane, narrow ? 1 : 0);
        DetailPane.Margin = narrow ? new Thickness(0, 14, 0, 0) : new Thickness(0);
        DetailScroll.VerticalScrollBarVisibility = narrow ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        DetailContent.MinHeight = narrow ? 460 : 0;
        // The stores and filters go under the search box rather than squeeze it.
        Grid.SetRow(ToolbarRight, narrow ? 1 : 0);
        Grid.SetColumn(ToolbarRight, narrow ? 0 : 1);
        Grid.SetColumnSpan(ToolbarRight, narrow ? 2 : 1);
        ToolbarRight.Margin = narrow ? new Thickness(0, 8, 0, 0) : new Thickness(16, 0, 0, 0);
    }

    // ── searching ────────────────────────────────────────────────────────────

    private void OnStoreChip(object? sender, RoutedEventArgs e)
    {
        var store = sender == ModrinthChip ? ModSource.Modrinth : ModSource.CurseForge;
        if (store == _store) return;
        _store = store;
        CurseForgeChip.Classes.Set("active", store == ModSource.CurseForge);
        ModrinthChip.Classes.Set("active", store == ModSource.Modrinth);
        _ = ResetAndLoadAsync();
    }

    /// <summary>The Minecraft version and loader boxes narrow the search (the stores filter).</summary>
    private void ApplyFilters()
    {
        var mc = string.IsNullOrWhiteSpace(McFilterBox.Text) ? null : McFilterBox.Text.Trim();
        var loader = LoaderFilterBox.SelectedIndex switch
        {
            1 => "fabric",
            2 => "forge",
            3 => "neoforge",
            4 => "quilt",
            _ => null
        };
        if (mc == _filterMc && loader == _filterLoader) return;
        _filterMc = mc;
        _filterLoader = loader;
        if (_started) _ = ResetAndLoadAsync();
    }

    private bool HasFilters => _filterMc is not null || _filterLoader is not null;

    private async Task ResetAndLoadAsync()
    {
        try
        {
            _lastSearched = _searchText.Trim();
            _cts.Cancel();
            if (_loading is { } running)
            {
                try { await running; } catch { /* reported by the load itself */ }
            }
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _offset = 0;
            _rows.Clear();
            _hasMore = true;
            CountLabel.Text = "";
            ListStatus.Text = "";
            ResultsState.Loading("Searching...");
            ClearDetail();
            await LoadPageAsync(ct);
            if (ct.IsCancellationRequested) return;
            if (_rows.Count > 0 && ResultsList.SelectedItem is null) ResultsList.SelectedIndex = 0;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.LogError(nameof(ModpackBrowserPage), ex); }
    }

    private Task LoadPageAsync(CancellationToken ct)
    {
        if (_isLoading || !_hasMore || _rows.Count >= MaxResults) return Task.CompletedTask;
        return _loading = LoadMoreAsync(ct);
    }

    private void OnResultsScroll(object? sender, ScrollChangedEventArgs e)
    {
        if (e.Source is not ScrollViewer sv) return;
        if (sv.Offset.Y + sv.Viewport.Height < sv.Extent.Height - 300) return;
        _ = LoadPageAsync(_cts.Token);
    }

    private async Task LoadMoreAsync(CancellationToken ct)
    {
        _isLoading = true;
        try
        {
            var hits = await _catalog.SearchAsync(_store, _searchText, _filterMc, _filterLoader, ModSort.Relevance,
                _offset, PageSize, modpacks: true, ct);
            if (ct.IsCancellationRequested) return;
            foreach (var m in hits) _rows.Add(new ModpackBrowseRow(m));
            _offset += hits.Count;
            if (hits.Count < PageSize) _hasMore = false;
            if (_rows.Count > 0)
            {
                ResultsState.Hide();
                CountLabel.Text = ModText.Count(_rows.Count, "result", "results") + (HasFilters ? " (filtered)" : "");
            }
            else if (!_hasMore)
                ResultsState.Empty("No results", "Try a different search or filter.", "");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _hasMore = false;
            var why = ex is OfflineException offline
                ? offline.Reason ?? App.State.OfflineReason ?? "the connection failed"
                : Connectivity.DescribeTransportFailure(ex, ct);
            if (why is null) AppLog.LogError(nameof(ModpackBrowserPage), ex);
            if (_rows.Count > 0)
                ListStatus.Text = why is not null ? $"The next page could not be loaded ({why})." : "The next page could not be loaded.";
            else if (why is not null)
                ResultsState.Offline("You are offline",
                    $"The store cannot be searched ({why}). Your instances are still on the Instances page.");
            else
                ResultsState.Error("Could not search", StoreRequestException.PlainFor(ex) ?? $"{ModText.Store(_store)} did not answer.");
        }
        finally { _isLoading = false; }
    }

    // ── the picked modpack ───────────────────────────────────────────────────

    private async Task LoadDetailAsync(ModSummary mod)
    {
        _detailCts.Cancel();
        _detailCts = new CancellationTokenSource();
        var ct = _detailCts.Token;
        _current = mod;
        _description = null;
        _descriptionIsMarkdown = false;
        DetailPlaceholder.IsVisible = false;
        DetailContent.IsVisible = true;
        DetailName.Text = mod.Name;
        DetailMeta.Text = $"by {mod.Author ?? "unknown"}  ·  {ModText.Number(mod.DownloadCount)} downloads  ·  {ModText.Store(mod.Source)}";
        DetailInitial.Text = ModText.Initial(mod.Name);
        ModIconLoader.SetUrl(DetailIcon, mod.IconUrl);
        DetailSummary.Text = mod.Description ?? "";
        DetailSummary.IsVisible = !string.IsNullOrWhiteSpace(mod.Description);
        OverviewText.Text = "Loading...";
        VersionsList.ItemsSource = null;
        VersionNote.Text = "Loading versions...";
        InstallButton.IsEnabled = !_installing;
        _ = LoadOverviewAsync(mod, ct);

        try
        {
            var versions = (await _catalog.VersionsAsync(mod, null, null, ct)).OrderByDescending(v => v.DatePublished).ToList();
            if (ct.IsCancellationRequested || !ReferenceEquals(_current, mod)) return;
            // The Minecraft version filter narrows the list when it leaves something.
            var shown = _filterMc is { } mc && versions.Any(v => v.GameVersions.Contains(mc, StringComparer.OrdinalIgnoreCase))
                ? versions.Where(v => v.GameVersions.Contains(mc, StringComparer.OrdinalIgnoreCase)).ToList()
                : versions;
            VersionNote.Text = shown.Count == versions.Count
                ? ModText.Count(versions.Count, "version", "versions")
                : $"{ModText.Count(shown.Count, "version", "versions")} for Minecraft {_filterMc}, of {versions.Count}";
            VersionsList.ItemsSource = shown.Select(v => new ModBrowseVersionRow(v, false, true, "")).ToList();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_current, mod)) return;
            AppLog.Log(nameof(ModpackBrowserPage), $"Versions of {mod.Name} could not be loaded: {ex.Message}");
            VersionNote.Text = StoreRequestException.PlainFor(ex) ?? "The versions could not be loaded. Install still takes the newest.";
        }
    }

    private async Task LoadOverviewAsync(ModSummary mod, CancellationToken ct)
    {
        try
        {
            var detail = await _catalog.DetailAsync(mod, ct);
            if (ct.IsCancellationRequested || !ReferenceEquals(_current, mod)) return;
            _description = detail.Description ?? mod.Description;
            // Trust the declared format: Modrinth Markdown can embed HTML tags.
            _descriptionIsMarkdown = detail.IsMarkdown;
            var text = ModText.Plain(_description, detail.IsMarkdown);
            OverviewText.Text = text.Length > 0 ? text : "This modpack has no description.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_current, mod)) return;
            AppLog.Log(nameof(ModpackBrowserPage), $"The page of {mod.Name} could not be loaded: {ex.Message}");
            OverviewText.Text = string.IsNullOrWhiteSpace(mod.Description) ? "The overview could not be loaded." : mod.Description;
        }
    }

    private void ClearDetail()
    {
        _detailCts.Cancel();
        _current = null;
        DetailContent.IsVisible = false;
        DetailPlaceholder.IsVisible = true;
        VersionsList.ItemsSource = null;
        DownloadStatus.Text = "";
        ModIconLoader.SetUrl(DetailIcon, null);
    }

    private void OnOpenProject(object? sender, RoutedEventArgs e)
    {
        if (_current is not { } mod) return;
        var slug = string.IsNullOrWhiteSpace(mod.Slug) || mod.Slug.Any(char.IsWhiteSpace) ? mod.Id : mod.Slug;
        var url = mod.Source == ModSource.CurseForge
            ? $"https://www.curseforge.com/minecraft/modpacks/{slug}"
            : $"https://modrinth.com/modpack/{slug}";
        if (!SafeLaunch.OpenUrl(url)) Say("Your browser could not be opened.", danger: true);
    }

    // ── installing ───────────────────────────────────────────────────────────

    private void OnInstallLatest(object? sender, RoutedEventArgs e)
    {
        if (_current is { } mod) _ = InstallAsync(mod, null);
    }

    private void OnQuickInstall(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ModpackBrowseRow row) _ = InstallAsync(row.Source, null);
    }

    private void OnInstallVersion(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ModBrowseVersionRow row && _current is { } mod)
            _ = InstallAsync(mod, row.Source);
    }

    /// <summary>Makes the new instance and starts the import behind it (the newest version unless one is
    /// picked), then shows Instances, where the import's progress is.</summary>
    private async Task InstallAsync(ModSummary mod, ModVersion? version)
    {
        if (_installing) return;
        _installing = true;
        InstallButton.IsEnabled = false;
        Say($"Starting the download of {mod.Name}...");
        try
        {
            // The page's own description when it is the open one, so the instance gets its text.
            var current = ReferenceEquals(_current, mod);
            var metadata = new PackImportMetadata(
                mod.Description?.Trim(),
                null,
                current ? _description : null,
                mod.IconUrl,
                mod.Source.ToString(),
                current && _descriptionIsMarkdown);
            await App.State.ModpackDownload.StartExternalDownloadAsync(mod, version, metadata);
            MainWindow.Current.NavigateToInstances();
            MainWindow.Current.Toast($"{mod.Name} is downloading into a new instance.");
        }
        catch (ApiException ex)
        {
            AppLog.LogError(nameof(ModpackBrowserPage), ex);
            Say(ApiClient.ServerSentence(ex) ?? "The new instance could not be made.", danger: true);
        }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(ModpackBrowserPage), ex);
            Say("That download could not be started. The full error is in the launcher log.", danger: true);
        }
        finally
        {
            _installing = false;
            InstallButton.IsEnabled = true;
        }
    }

    private void Say(string text, bool danger = false)
    {
        var target = DetailContent.IsVisible ? DownloadStatus : ListStatus;
        target.Text = text;
        target.Classes.Set("danger", danger);
    }
}

/// <summary>One modpack in <see cref="ModpackBrowserPage"/>'s results.</summary>
public sealed class ModpackBrowseRow(ModSummary source)
{
    public ModSummary Source { get; } = source;
    public string Name => Source.Name;
    public string Initial => ModText.Initial(Source.Name);
    public string? IconUrl => Source.IconUrl;
    public string Summary => Source.Description?.Trim() ?? "";
    public string StoreLabel => ModText.Store(Source.Source);
    public string MetaLabel => $"by {Source.Author ?? "unknown"}  ·  {ModText.Number(Source.DownloadCount)} downloads";
}
