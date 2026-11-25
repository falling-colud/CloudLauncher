using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// Finding mods for one instance on Modrinth and CurseForge, for its Minecraft version and loader, and
/// putting them in with their required dependencies: the Windows launcher's ModExplorerPage, including
/// files CurseForge only hands to its own website and app, which go to the download-by-hand card.
/// </summary>
public partial class ModBrowserPage : UserControl, IShellPage, IRefreshable
{
    private const int PageSize = 30;
    /// <summary>Safety stop so a runaway pager can't fill memory; not a browsing limit.</summary>
    private const int MaxResults = 2000;

    private readonly PackDetail _pack;
    private readonly ModCatalog _catalog;
    private readonly ObservableCollection<ModBrowseRow> _rows = new();
    private readonly HashSet<string> _installedModKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _allSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(450) };

    private ModSource? _store;          // null: both stores
    private ModSort _sort = ModSort.Relevance;
    private string _searchText = "";
    private string _lastSearched = "";
    private CancellationTokenSource _cts = new();
    private CancellationTokenSource _detailCts = new();
    private Task? _loading;
    private bool _isLoading;
    private bool _hasMore;
    private int _offset;
    private bool _started;

    private ModSummary? _currentMod;
    // When the open mod is on both stores, these hold it on each, so the toggle can switch which one is
    // shown and downloaded; null = not on that store.
    private ModSummary? _detailModrinth;
    private ModSummary? _detailCurseForge;
    private int _detailGeneration;
    private List<ModVersion> _allVersions = new();
    private bool _versionsUnfiltered;
    private bool _installing;
    private int _indexGeneration;

    /// <summary>For the XAML previewer only: the page's layout, with nothing loading.</summary>
    public ModBrowserPage()
    {
        InitializeComponent();
        _pack = null!;
        _catalog = ModCatalog.Stores;
    }

    public ModBrowserPage(PackDetail pack) : this(pack, ModCatalog.Stores) { }

    /// <param name="catalog">Where results come from (the stores, or a preview's own).</param>
    public ModBrowserPage(PackDetail pack, ModCatalog catalog)
    {
        InitializeComponent();
        _pack = pack;
        _catalog = catalog;
        BodyGrid.SizeChanged += (_, e) => LayoutBody(e.NewSize.Width);
        PackLabel.Text = $"Into {pack.Name}  ·  {PackVersionLabel()}";
        ResultsList.ItemsSource = _rows;
        ResultsList.SelectionChanged += OnResultSelected;
        ResultsList.AddHandler(ScrollViewer.ScrollChangedEvent, OnResultsScroll);
        ResultsState.RetryRequested += () => _ = ResetAndLoadAsync();

        // Searches go to the stores, so typing settles for a moment first; Enter searches at once.
        SearchBox.TextChanged += (_, _) =>
        {
            _searchDebounce.Stop();
            _searchDebounce.Start();
        };
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
        SortBox.SelectionChanged += (_, _) =>
        {
            var sort = (ModSort)Math.Max(0, SortBox.SelectedIndex);
            if (sort == _sort) return;
            _sort = sort;
            if (_started) _ = ResetAndLoadAsync();
        };
        AddHandler(KeyDownEvent, OnPageKeyDown, RoutingStrategies.Tunnel);

        AttachedToVisualTree += (_, _) =>
        {
            App.State.ManualDownloads.Changed -= OnManualDownloadsChanged;
            App.State.ManualDownloads.Changed += OnManualDownloadsChanged;
            if (_started) return;
            _started = true;
            _ = StartAsync();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            App.State.ManualDownloads.Changed -= OnManualDownloadsChanged;
            _searchDebounce.Stop();
        };
    }

    public ShellNav Nav => ShellNav.Instances;

    public Task RefreshAsync() => ResetAndLoadAsync();

    private async Task StartAsync()
    {
        try
        {
            // The cache-only pass first, so mods already known are badged at once; the identity pass
            // across both stores refines it in the background.
            await SeedInstalledFromCacheAsync();
            await ResetAndLoadAsync();
            _ = RefreshInstalledModIndexAsync();
        }
        catch (Exception ex) { AppLog.LogError(nameof(ModBrowserPage), ex); }
    }

    private void OnPageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void OnBack(object? sender, RoutedEventArgs e) => MainWindow.Current.GoBack();

    /// <summary>Opens the instance's mods folder; works even with both stores down.</summary>
    private void OnOpenModsFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.Combine(App.State.Packs.GameDir(_pack.Id), "mods");
            Directory.CreateDirectory(dir);
            if (!SafeLaunch.OpenFolder(dir)) Say("Could not open the mods folder.", danger: true);
        }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(ModBrowserPage), ex);
            Say("Could not open the mods folder.", danger: true);
        }
    }

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

    // ── stores and searching ─────────────────────────────────────────────────

    private void OnStoreChip(object? sender, RoutedEventArgs e)
    {
        ModSource? store = sender == ModrinthChip ? ModSource.Modrinth
            : sender == CurseForgeChip ? ModSource.CurseForge
            : null;
        if (store == _store) return;
        _store = store;
        AllChip.Classes.Set("active", store is null);
        ModrinthChip.Classes.Set("active", store == ModSource.Modrinth);
        CurseForgeChip.Classes.Set("active", store == ModSource.CurseForge);
        _ = ResetAndLoadAsync();
    }

    private async Task ResetAndLoadAsync()
    {
        try
        {
            _lastSearched = _searchText.Trim();
            _cts.Cancel();
            // Wait for the cancelled load: until it unwinds, the new one would stop at its guard and
            // leave the list empty.
            if (_loading is { } running)
            {
                try { await running; } catch { /* reported by the load itself */ }
            }
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _offset = 0;
            _rows.Clear();
            _allSeen.Clear();
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
        catch (Exception ex) { AppLog.LogError(nameof(ModBrowserPage), ex); }
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
            var mc = PackMinecraftVersion();
            var loader = PackLoaderTag();
            string? mcArg = mc.Length > 0 ? mc : null, loaderArg = loader.Length > 0 ? loader : null;
            int added;
            if (_store is { } store)
            {
                var hits = await _catalog.SearchAsync(store, _searchText, mcArg, loaderArg, _sort, _offset, PageSize, false, ct);
                foreach (var m in hits) _rows.Add(CreateRow(m));
                added = hits.Count;
                if (hits.Count < PageSize) _hasMore = false;
            }
            else added = await LoadBothAsync(mcArg, loaderArg, ct);

            _offset += added;
            if (added == 0) _hasMore = false;
            if (ct.IsCancellationRequested) return;
            if (_rows.Count > 0)
            {
                ResultsState.Hide();
                CountLabel.Text = ModText.Count(_rows.Count, "result", "results") + (_hasMore ? ", scroll for more" : "");
            }
            else if (!_hasMore)
                ResultsState.Empty("No results", "Try a different search, or another store.", "");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _hasMore = false;
            ReportLoadFailure(ex, ct);
        }
        finally { _isLoading = false; }
    }

    /// <summary>Both stores in one list, deduplicated by name: by downloads when sorted so (the counts
    /// compare across stores), otherwise each store's ranked page interleaved, Modrinth first. One store
    /// failing doesn't sink the other; both failing is reported like one.</summary>
    private async Task<int> LoadBothAsync(string? mc, string? loader, CancellationToken ct)
    {
        var modrinthTask = Try(() => _catalog.SearchAsync(ModSource.Modrinth, _searchText, mc, loader, _sort, _offset, PageSize, false, ct));
        var curseTask = Try(() => _catalog.SearchAsync(ModSource.CurseForge, _searchText, mc, loader, _sort, _offset, PageSize, false, ct));
        var results = await Task.WhenAll(modrinthTask, curseTask);
        ct.ThrowIfCancellationRequested();
        var (modrinth, modrinthError) = results[0];
        var (curse, curseError) = results[1];
        if (modrinthError is not null && curseError is not null) throw modrinthError;
        if (_offset == 0 && (modrinthError is not null || curseError is not null))
            ListStatus.Text = modrinthError is not null
                ? "Modrinth did not answer: showing CurseForge only."
                : "CurseForge did not answer: showing Modrinth only.";

        IEnumerable<ModSummary> merged = _sort == ModSort.Downloads
            ? modrinth.Concat(curse).OrderByDescending(m => m.DownloadCount)
            : Interleave(modrinth, curse);
        foreach (var m in merged)
            if (_allSeen.Add(DedupeKey(m)))
                _rows.Add(CreateRow(m));

        if (modrinth.Count < PageSize && curse.Count < PageSize) _hasMore = false;
        return modrinth.Count + curse.Count == 0 ? 0 : PageSize; // both stores' offsets move together

        static async Task<(List<ModSummary>, Exception?)> Try(Func<Task<List<ModSummary>>> search)
        {
            try { return (await search(), null); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return (new List<ModSummary>(), ex); }
        }
    }

    private static IEnumerable<ModSummary> Interleave(IReadOnlyList<ModSummary> a, IReadOnlyList<ModSummary> b)
    {
        for (var i = 0; i < a.Count || i < b.Count; i++)
        {
            if (i < a.Count) yield return a[i];
            if (i < b.Count) yield return b[i];
        }
    }

    /// <summary>Cross-store dedupe key: same-named mods on CurseForge and Modrinth merge.</summary>
    private static string DedupeKey(ModSummary m)
    {
        var s = new string((m.Name ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return s.Length > 0 ? s : (m.Slug.Length > 0 ? m.Slug : m.Id);
    }

    /// <summary>Tells being offline from a store error: offline gets its own words, since nothing is
    /// broken and the same search works once the network is back.</summary>
    private void ReportLoadFailure(Exception ex, CancellationToken ct)
    {
        var why = ex is OfflineException offline
            ? offline.Reason ?? App.State.OfflineReason ?? "the connection failed"
            : Connectivity.DescribeTransportFailure(ex, ct);
        if (why is null) AppLog.LogError(nameof(ModBrowserPage), ex);
        if (_rows.Count > 0)
        {
            ListStatus.Text = why is not null
                ? $"The next page could not be loaded ({why})."
                : StoreRequestException.PlainFor(ex) ?? "The next page could not be loaded.";
            return;
        }
        if (why is not null)
            ResultsState.Offline("You are offline",
                $"The store cannot be searched ({why}). What you have already downloaded is still on the Instances page.");
        else
            ResultsState.Error("Could not search", StoreRequestException.PlainFor(ex) ?? "The store answered with an error.");
    }

    private ModBrowseRow CreateRow(ModSummary mod) => new(mod, IsModInstalled(mod));

    // ── the picked mod ───────────────────────────────────────────────────────

    private void OnResultSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (ResultsList.SelectedItem is ModBrowseRow row) _ = LoadModDetailAsync(row.Source);
    }

    private async Task LoadModDetailAsync(ModSummary mod, bool resolveCounterpart = true)
    {
        _detailCts.Cancel();
        _detailCts = new CancellationTokenSource();
        var ct = _detailCts.Token;
        _currentMod = mod;
        if (resolveCounterpart)
        {
            // A fresh detail: this store's mod is known; the other store's is looked up below.
            _detailModrinth = mod.Source == ModSource.Modrinth ? mod : null;
            _detailCurseForge = mod.Source == ModSource.CurseForge ? mod : null;
            UpdateSourceToggle();
        }
        var gen = ++_detailGeneration;
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
        _allVersions = new List<ModVersion>();
        _versionsUnfiltered = false;
        UpdateInstallButton();
        _ = LoadOverviewAsync(mod, ct);

        try
        {
            var showAll = ShowAllBox.IsChecked == true;
            var versions = await LoadVersionsForModAsync(mod, applyFilters: !showAll, ct);
            if (ct.IsCancellationRequested || !ReferenceEquals(_currentMod, mod)) return;
            _allVersions = versions;
            _versionsUnfiltered = showAll;
            ShowVersions(showAll);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_currentMod, mod)) return;
            AppLog.Log(nameof(ModBrowserPage), $"Versions of {mod.Name} could not be loaded: {ex.Message}");
            VersionNote.Text = StoreRequestException.PlainFor(ex) ?? "The versions could not be loaded.";
        }

        if (resolveCounterpart) _ = ResolveDetailCounterpartAsync(mod, gen, ct);
    }

    private async Task LoadOverviewAsync(ModSummary mod, CancellationToken ct)
    {
        try
        {
            var detail = await _catalog.DetailAsync(mod, ct);
            if (ct.IsCancellationRequested || !ReferenceEquals(_currentMod, mod)) return;
            var text = ModText.Plain(detail.Description ?? mod.Description, detail.IsMarkdown);
            OverviewText.Text = text.Length > 0 ? text : "This project has no description.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_currentMod, mod)) return;
            AppLog.Log(nameof(ModBrowserPage), $"The page of {mod.Name} could not be loaded: {ex.Message}");
            OverviewText.Text = string.IsNullOrWhiteSpace(mod.Description)
                ? "The overview could not be loaded."
                : mod.Description;
        }
    }

    /// <summary>Looks up the same mod on the other store in the background and, if found, shows the
    /// source toggle.</summary>
    private async Task ResolveDetailCounterpartAsync(ModSummary mod, int gen, CancellationToken ct)
    {
        try
        {
            var counterpart = await _catalog.CounterpartAsync(mod, ct);
            if (ct.IsCancellationRequested || gen != _detailGeneration || counterpart is null) return;
            if (counterpart.Source == ModSource.Modrinth) _detailModrinth = counterpart;
            else if (counterpart.Source == ModSource.CurseForge) _detailCurseForge = counterpart;
            UpdateSourceToggle();
        }
        catch { /* offline or no counterpart: the toggle stays hidden */ }
    }

    private void UpdateSourceToggle()
    {
        var both = _detailModrinth is not null && _detailCurseForge is not null;
        SourceToggle.IsVisible = both;
        if (!both) return;
        var active = _currentMod?.Source ?? ModSource.Modrinth;
        SourceModrinth.Classes.Set("active", active == ModSource.Modrinth);
        SourceCurseForge.Classes.Set("active", active == ModSource.CurseForge);
    }

    private async void OnPickSource(object? sender, RoutedEventArgs e)
    {
        var store = sender == SourceCurseForge ? ModSource.CurseForge : ModSource.Modrinth;
        var target = store == ModSource.Modrinth ? _detailModrinth : _detailCurseForge;
        if (target is null || _currentMod?.Source == store) return;
        // The pair is known; only the detail and versions are read again for the chosen store.
        await LoadModDetailAsync(target, resolveCounterpart: false);
        UpdateSourceToggle();
    }

    private void ShowVersions(bool all)
    {
        var mc = PackMinecraftVersion();
        var loader = PackLoaderTag();
        var filtered = all ? _allVersions : _allVersions.Where(v => MatchesFilters(v, mc, loader)).ToList();
        VersionNote.Text = all
            ? $"All {ModText.Count(_allVersions.Count, "version", "versions")}"
            : filtered.Count == 0
                ? $"No version for {PackVersionLabel()}"
                : $"{ModText.Count(filtered.Count, "version", "versions")} for {PackVersionLabel()}";
        var installed = _currentMod is not null && IsModInstalled(_currentMod);
        VersionsList.ItemsSource = filtered
            .OrderByDescending(v => v.DatePublished)
            .Select(v => new ModBrowseVersionRow(v, installed, MatchesFilters(v, mc, loader), PackVersionLabel()))
            .ToList();
    }

    private async void OnShowAllChanged(object? sender, RoutedEventArgs e)
    {
        var showAll = ShowAllBox.IsChecked == true;
        // The list is fetched filtered to the instance; "Show All" needs the rest, fetched once.
        if (showAll && !_versionsUnfiltered && _currentMod is { } mod)
        {
            VersionNote.Text = "Loading every version...";
            try
            {
                var all = await LoadVersionsForModAsync(mod, applyFilters: false, _detailCts.Token);
                if (!ReferenceEquals(_currentMod, mod)) return;
                _allVersions = all;
                _versionsUnfiltered = true;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                VersionNote.Text = StoreRequestException.PlainFor(ex) ?? "Every version could not be loaded.";
                return;
            }
        }
        if (_currentMod is not null) ShowVersions(showAll);
    }

    private void UpdateInstallButton()
    {
        var mod = _currentMod;
        var installed = mod is not null && IsModInstalled(mod);
        InstallText.Text = installed ? "Installed" : "Install";
        InstallButton.IsEnabled = mod is not null && !installed && !_installing;
        ToolTip.SetTip(InstallButton, installed
            ? "Already in this instance"
            : $"The newest version for {PackVersionLabel()}, with what it needs");
    }

    private void ClearDetail()
    {
        _detailCts.Cancel();
        _currentMod = null;
        _detailModrinth = _detailCurseForge = null;
        _allVersions = new List<ModVersion>();
        _versionsUnfiltered = false;
        DetailContent.IsVisible = false;
        DetailPlaceholder.IsVisible = true;
        SourceToggle.IsVisible = false;
        VersionsList.ItemsSource = null;
        ModIconLoader.SetUrl(DetailIcon, null);
    }

    // ── installing ───────────────────────────────────────────────────────────

    private void OnInstallBest(object? sender, RoutedEventArgs e)
    {
        if (_currentMod is { } mod) _ = RunInstallAsync(() => QuickDownloadAsync(mod));
    }

    private void OnQuickInstall(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ModBrowseRow row) return;
        if (row.IsInstalled)
        {
            Say($"{row.Name} is already in this instance.");
            return;
        }
        ResultsList.SelectedItem = row;
        _ = RunInstallAsync(() => QuickDownloadAsync(row.Source));
    }

    private void OnInstallVersion(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ModBrowseVersionRow row || _currentMod is not { } mod) return;
        if (row.IsInstalled) { Say($"{mod.Name} is already in this instance."); return; }
        if (!row.IsCompatible) { Say($"{row.VersionNumber} does not support {PackVersionLabel()}."); return; }
        _ = RunInstallAsync(() => DownloadVersionAsync(mod, row.Source));
    }

    /// <summary>One install at a time: a double click must not start a second dependency resolution
    /// behind the first.</summary>
    private async Task RunInstallAsync(Func<Task> install)
    {
        if (_installing) return;
        _installing = true;
        UpdateInstallButton();
        try { await install(); }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(ModBrowserPage), ex);
            Say("Download failed: " + ex.Message, danger: true);
        }
        finally
        {
            _installing = false;
            UpdateInstallButton();
        }
    }

    private async Task QuickDownloadAsync(ModSummary mod)
    {
        if (IsModInstalled(mod))
        {
            Say($"{mod.Name} is already in this instance.");
            RefreshDownloadedState();
            return;
        }
        if (!CanInstallModsToPack())
        {
            Say(NeedsLoaderSentence, danger: true);
            return;
        }

        Say($"Finding the newest file for {mod.Name}...");
        try
        {
            var versions = await LoadVersionsForModAsync(mod, applyFilters: true, CancellationToken.None);
            var version = PickBestVersion(versions);
            if (version is null)
            {
                Say($"No version of {mod.Name} for {PackVersionLabel()}.");
                return;
            }
            await DownloadVersionAsync(mod, version);
        }
        catch (Exception ex)
        {
            Say("Download failed: " + (StoreRequestException.PlainFor(ex) ?? ex.Message), danger: true);
        }
    }

    private const string NeedsLoaderSentence =
        "This instance needs a Minecraft version and a Fabric, Forge or NeoForge loader before mods can go in.";

    /// <summary>
    /// Puts one version in with every required dependency (when the instance's Advanced settings say so),
    /// dependencies the instance already has left out. Files CurseForge will not hand over are listed to
    /// download by hand, and the card comes up for them while the rest downloads.
    /// </summary>
    private async Task DownloadVersionAsync(ModSummary mod, ModVersion version, Action<ModMeta>? applyToRoot = null)
    {
        if (IsModInstalled(mod))
        {
            Say($"{mod.Name} is already in this instance.");
            RefreshDownloadedState();
            return;
        }
        if (!CanInstallModsToPack())
        {
            Say(NeedsLoaderSentence, danger: true);
            return;
        }
        if (!MatchesFilters(version, PackMinecraftVersion(), PackLoaderTag()))
        {
            Say($"{version.VersionNumber} does not support {PackVersionLabel()}.");
            return;
        }

        var target = TargetBox.SelectedIndex == 1 ? "local/mods/" : "game/mods/";
        var folder = target == "local/mods/"
            ? Path.Combine(App.State.Packs.LocalDir(_pack.Id), "mods")
            : Path.Combine(App.State.Packs.GameDir(_pack.Id), "mods");
        Directory.CreateDirectory(folder);

        DownloadProgress.IsVisible = true;
        DownloadProgress.IsIndeterminate = true;
        DownloadProgress.Value = 0;
        Say($"Resolving dependencies for {mod.Name}...");
        try
        {
            var progress = new Progress<(long done, long total)>(p =>
            {
                if (p.total <= 0) return;
                DownloadProgress.IsIndeterminate = false;
                DownloadProgress.Value = (double)p.done / p.total * 100;
            });

            var downloads = await ModDependencyResolver.ResolveRequiredDownloadsAsync(
                mod, version, PackMinecraftVersion(), PackLoaderTag(),
                App.State.Modrinth, App.State.CurseForge, PackChannel(), App.State.ModVersions);
            if (downloads.Count == 0)
            {
                Say("No downloadable file found.");
                return;
            }

            // "Auto-download and enable required dependencies" (the instance's Advanced settings): when
            // off, only the mod itself.
            var autoDeps = App.State.ModMetadata.Advanced(_pack.Id).AutoDownloadDependencies;

            // What needs fetching first, so the progress count covers real downloads only.
            var pending = new List<ModDownloadItem>();
            var byHand = new List<ModDownloadItem>();
            var refused = new List<string>();
            var skipped = 0;
            foreach (var item in downloads)
            {
                if (item.IsDependency && !autoDeps) { skipped++; continue; }
                // The store supplies the file name; anything but a plain name could escape the folder.
                if (PathSafety.ResolveFileName(folder, item.File.Filename) is not { } dest)
                {
                    AppLog.Log(nameof(ModBrowserPage), $"Skipped {item.Mod.Name}: the store's file name is not a plain file name: {item.File.Filename}");
                    refused.Add($"{item.Mod.Name} (the store gave an unusable file name)");
                    continue;
                }
                if (item.IsDependency && (File.Exists(dest) || await IsDependencyAlreadyInstalledAsync(item.Mod)))
                {
                    skipped++;
                    continue;
                }
                if (item.ByHand) { byHand.Add(item); continue; }
                pending.Add(item);
            }

            // Files CurseForge only hands to its own website and app: listed for the user to fetch with
            // the browser (the card pops up now) and moved in once they land in Downloads. The rest
            // downloads meanwhile.
            var byHandNote = byHand.Count == 0 ? "" : await QueueByHandAsync(byHand, target, applyToRoot);

            var saved = 0;
            var installedPaths = new List<string>();
            var concurrency = App.State.Settings.EffectiveModDownloadConcurrency;
            using (var gate = new SemaphoreSlim(concurrency, concurrency))
            {
                var completed = 0;
                void ReportBatch(string? current = null)
                {
                    DownloadProgress.IsIndeterminate = pending.Count == 0;
                    DownloadProgress.Value = pending.Count == 0 ? 0 : completed * 100.0 / pending.Count;
                    if (pending.Count > 0)
                        Say(pending.Count == 1
                            ? $"Downloading {current ?? pending[0].File.Filename}..."
                            : $"Downloading {pending.Count} files, {completed} done...");
                }
                ReportBatch();

                var failures = new List<string>(refused);
                await Task.WhenAll(pending.Select(async item =>
                {
                    var dest = PathSafety.ResolveFileName(folder, item.File.Filename)!;
                    await gate.WaitAsync();
                    try
                    {
                        await App.State.Modrinth.DownloadFileAsync(item.File.DownloadUrl, dest, pending.Count == 1 ? progress : null);
                        await RememberInstalledModAsync(item, dest);
                        lock (installedPaths) installedPaths.Add(dest);

                        // The mod itself records the store it came from, so a jar listed on both stores
                        // keeps this store's identity (label, page, updates).
                        if (!item.IsDependency)
                        {
                            var store = item.Mod.Source is ModSource.CurseForge or ModSource.Modrinth ? item.Mod.Source : (ModSource?)null;
                            if (store is not null || applyToRoot is not null)
                                SetDownloadedMetaFlag(item.Mod, item.File.Filename, m =>
                                {
                                    if (store is not null) m.PreferredSource = store;
                                    applyToRoot?.Invoke(m);
                                }, save: false);
                        }
                        else
                        {
                            // A jar pulled in as a dependency is a library mod; only new, untouched
                            // metadata is flagged, never a mod the user classified.
                            SetDownloadedMetaFlag(item.Mod, item.File.Filename, m =>
                            {
                                if (m.IsDefault) m.IsLibrary = true;
                            }, save: false);
                        }
                        Interlocked.Increment(ref saved);
                    }
                    catch (Exception ex) { lock (failures) failures.Add($"{item.File.Filename} ({ex.Message})"); }
                    finally
                    {
                        gate.Release();
                        Interlocked.Increment(ref completed);
                        ReportBatch(item.File.Filename);
                    }
                }));

                // One save for the whole batch, before the throw, so jars that did download keep their
                // flags when a sibling fails.
                if (saved > 0) App.State.ModMetadata.Save(_pack.Id);
                if (failures.Count > 0) throw new InvalidOperationException(string.Join("; ", failures));
            }

            DownloadProgress.Value = 100;
            var skippedText = skipped > 0 ? $" ({skipped} already there)" : "";
            Say(pending.Count == 0 && byHand.Count > 0
                ? byHandNote.Trim()
                : (saved == 1 && downloads.Count == 1
                    ? $"Saved to {target}{downloads[0].File.Filename}"
                    : $"Saved {ModText.Count(saved, "file", "files")} to {target}{skippedText}") + byHandNote);
            App.State.ModFingerprints.Flush();
            RefreshDownloadedState();
            // Resolve the new jars on the other store too, so they stop showing as installable there.
            _ = RefreshInstalledModIndexAsync(installedPaths);
        }
        catch (Exception ex)
        {
            Say("Download failed: " + (StoreRequestException.PlainFor(ex) ?? ex.Message), danger: true);
        }
        finally
        {
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Value = 0;
            DownloadProgress.IsVisible = false;
        }
    }

    /// <summary>True if a required dependency is already in the instance, by its own identity or its
    /// counterpart on the other store (Create from CurseForge counts for Create from Modrinth).</summary>
    private async Task<bool> IsDependencyAlreadyInstalledAsync(ModSummary depMod)
    {
        if (IsModInstalled(depMod)) return true;
        try
        {
            var otherStore = depMod.Source == ModSource.Modrinth ? ModSource.CurseForge : ModSource.Modrinth;
            var counterpartKey = await ResolveCounterpartKeyAsync(otherStore, depMod);
            return counterpartKey is not null && _installedModKeys.Contains(counterpartKey);
        }
        catch { return false; }
    }

    // ── files CurseForge only hands to its own site and app ─────────────────────

    /// <summary>Files handed to the download-by-hand card from this page, by CurseForge file id, so the
    /// badges and the status line follow them once they land.</summary>
    private readonly Dictionary<int, ModDownloadItem> _byHand = new();

    /// <summary>
    /// Lists files the store will not hand over for the user to download with the browser, and brings up
    /// the card for them (through <see cref="ManualDownloadService.Needed"/>, which the main window
    /// answers). Returns the sentence for the status line, starting with a space.
    /// </summary>
    private async Task<string> QueueByHandAsync(List<ModDownloadItem> items, string target, Action<ModMeta>? applyToRoot)
    {
        // The flags a downloaded jar gets, set now so they are in place when the file lands. Keyed by
        // project, so they hold whatever the file ends up being called.
        foreach (var item in items)
        {
            SetDownloadedMetaFlag(item.Mod, item.File.Filename, m =>
            {
                if (item.IsDependency)
                {
                    if (m.IsDefault) m.IsLibrary = true;
                    return;
                }
                m.PreferredSource = ModSource.CurseForge;
                applyToRoot?.Invoke(m);
            }, save: false);
        }
        App.State.ModMetadata.Save(_pack.Id);

        IReadOnlyList<ManualDownloadEntry> queued;
        try
        {
            queued = await App.State.ManualDownloads.QueueAsync(_pack.Id, _pack.Name, App.State.CurseForge,
                items.Select(i => new ManualDownloadWant(i.Mod, i.Version, i.File, "mods", Local: target == "local/mods/")).ToList());
        }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(ModBrowserPage), ex);
            return $" {ModText.NameList(items.Select(i => i.Mod.Name))} could not be listed for a download by hand: {ex.Message}";
        }

        foreach (var entry in queued)
        {
            var item = items.FirstOrDefault(i =>
                CurseForgeService.TryParseFileIds(i.Mod, i.Version, out _, out var fileId) && fileId == entry.FileId);
            if (item is not null) _byHand[entry.FileId] = item;
        }
        if (queued.Count == 0)
            return $" {ModText.NameList(items.Select(i => i.Mod.Name))} can't be downloaded by the launcher, and CurseForge gave no file to list.";
        var names = ModText.NameList(queued.Select(e => e.Name));
        return queued.Count == 1
            ? $" {names} has to come from CurseForge's website: click Download in the window that opened."
            : $" {names} have to come from CurseForge's website: click Download in the window that opened.";
    }

    /// <summary>A file handed to the card landed (the card or the background watch put it in, or the user
    /// dropped it in the folder): badge its mod, and resolve the jar like any other download.</summary>
    private async void OnManualDownloadsChanged(Guid packId)
    {
        if (packId != _pack.Id || _byHand.Count == 0) return;
        try
        {
            var list = await Task.Run(() => App.State.ManualDownloads.Load(_pack.Id));
            var landed = list?.Files
                .Where(f => f.State == ManualDownloadState.Added && _byHand.ContainsKey(f.FileId))
                .ToList() ?? [];
            if (landed.Count == 0) return;

            var paths = new List<string>();
            foreach (var entry in landed)
            {
                var item = _byHand[entry.FileId];
                _byHand.Remove(entry.FileId);
                _installedModKeys.Add(ModKey(item.Mod));
                var path = Path.Combine(App.State.ManualDownloads.FolderOf(_pack.Id, entry), entry.TargetFileName);
                if (File.Exists(path))
                {
                    await RememberInstalledModAsync(item, path);
                    paths.Add(path);
                }
            }
            Say(landed.Count == 1
                ? $"Added {landed[0].FileName} to {(landed[0].Local ? "local" : "game")}/{landed[0].Folder}/"
                : $"Added {landed.Count} files from CurseForge's website.");
            App.State.ModFingerprints.Flush();
            RefreshDownloadedState();
            _ = RefreshInstalledModIndexAsync(paths);
        }
        catch (Exception ex) { AppLog.LogError(nameof(ModBrowserPage), ex); }
    }

    /// <summary>Applies a flag to a just-downloaded jar's saved metadata, keyed by its project id (so it
    /// survives version updates) with a file-name fallback.</summary>
    /// <param name="save">False inside a batch, which saves once at the end.</param>
    private void SetDownloadedMetaFlag(ModSummary mod, string filename, Action<ModMeta> apply, bool save = true)
    {
        var keys = ModMetadataService.CandidateKeys(
            mod.Source == ModSource.Modrinth ? mod : null,
            mod.Source == ModSource.CurseForge ? mod : null,
            filename);
        var meta = App.State.ModMetadata.GetMeta(_pack.Id, keys);
        apply(meta);
        if (save) App.State.ModMetadata.SetMeta(_pack.Id, keys, meta);
        else App.State.ModMetadata.StoreMeta(_pack.Id, keys, meta);
    }

    // ── what the instance already has ────────────────────────────────────────

    private bool IsModInstalled(ModSummary mod) => _installedModKeys.Contains(ModKey(mod));

    private static string ModKey(ModSummary mod) => $"{mod.Source}:{mod.Id}";

    private void RefreshDownloadedState()
    {
        foreach (var row in _rows) row.IsInstalled = IsModInstalled(row.Source);
        if (_currentMod is not null && VersionsList.ItemsSource is not null) ShowVersions(ShowAllBox.IsChecked == true);
        UpdateInstallButton();
    }

    /// <summary>The cheap pass: mods the fingerprint cache already knows, no hashing or network.</summary>
    private async Task SeedInstalledFromCacheAsync()
    {
        try
        {
            var keys = await Task.Run(() =>
            {
                var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in EnumeratePackModFiles())
                {
                    if (App.State.ModFingerprints.TryGetCachedMatches(path, out var modrinth, out var curse))
                    {
                        if (modrinth is { } m) installed.Add(ModKey(m.Mod));
                        if (curse is { } c) installed.Add(ModKey(c.Mod));
                    }
                    else if (App.State.ModFingerprints.TryGetCachedMatch(path, out var mod, out _))
                        installed.Add(ModKey(mod));
                }
                return installed;
            });
            _installedModKeys.Clear();
            foreach (var key in keys) _installedModKeys.Add(key);
        }
        catch { _installedModKeys.Clear(); }
        RefreshDownloadedState();
    }

    /// <summary>Resolves every installed jar to its Modrinth and CurseForge identity, so a mod from one
    /// store is recognised while browsing the other. <paramref name="onlyPaths"/> narrows the pass to new
    /// jars and never clears what is known.</summary>
    private async Task RefreshInstalledModIndexAsync(IReadOnlyCollection<string>? onlyPaths = null)
    {
        var generation = ++_indexGeneration;
        try
        {
            if (onlyPaths is { Count: 0 }) return;
            var jarPaths = onlyPaths ?? await Task.Run(() => EnumeratePackModFiles().ToList());
            var index = await InstalledModResolver.ResolveAsync(jarPaths, App.State.ModFingerprints,
                App.State.Modrinth, App.State.CurseForge);
            if (generation != _indexGeneration) return;

            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var identity in index.Identities)
            {
                if (identity.Modrinth is { } modrinth) keys.Add(ModKey(modrinth.Mod));
                if (identity.CurseForge is { } curseForge) keys.Add(ModKey(curseForge.Mod));
                // Jars fingerprinting can't place keep their cached identity, so they don't flip back
                // to installable.
                if (identity.Modrinth is null && identity.CurseForge is null
                    && App.State.ModFingerprints.TryGetCachedMatch(identity.Path, out var cached, out _))
                    keys.Add(ModKey(cached));
            }

            // Replace the set only when both stores answered; otherwise merge, so a failed lookup can't
            // make installed mods look installable again.
            if (index.Complete && onlyPaths is null) _installedModKeys.Clear();
            foreach (var key in keys) _installedModKeys.Add(key);
            RefreshDownloadedState();

            // Hashes only link the stores when both host the identical jar; for jars found on one store
            // only, look up the other store's project by slug and name.
            await AddCrossStoreCounterpartKeysAsync(index.Identities, generation);
        }
        catch
        {
            // Keep what is recognised; a passing failure shouldn't make installed mods look installable.
        }
    }

    private readonly Dictionary<string, string?> _counterpartKeyCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many counterparts one pass asks the stores about; the rest are looked up on later
    /// visits (the answers are cached on disk for days).</summary>
    private const int CounterpartLookupsPerPass = 40;

    private async Task AddCrossStoreCounterpartKeysAsync(IReadOnlyList<InstalledModIdentity> identities, int generation)
    {
        var added = false;
        var lookups = 0;
        var deferred = 0;
        foreach (var identity in identities)
        {
            if (generation != _indexGeneration) return;
            var source = identity switch
            {
                { Modrinth: null, CurseForge: { } cf } => (Target: ModSource.Modrinth, Mod: cf.Mod),
                { CurseForge: null, Modrinth: { } mr } => (Target: ModSource.CurseForge, Mod: mr.Mod),
                _ => (Target: ModSource.External, Mod: (ModSummary?)null)
            };
            if (source.Mod is null) continue;
            if (!HasCounterpartAnswer(source.Target, source.Mod))
            {
                if (lookups >= CounterpartLookupsPerPass) { deferred++; continue; }
                lookups++;
            }
            var counterpartKey = await ResolveCounterpartKeyAsync(source.Target, source.Mod);
            if (generation != _indexGeneration) return;
            if (counterpartKey is not null && _installedModKeys.Add(counterpartKey)) added = true;
        }
        App.State.ModCounterparts.Flush();
        if (deferred > 0)
            AppLog.Log(nameof(ModBrowserPage), $"Looked up {lookups} cross-store counterpart(s); {deferred} left for a later visit.");
        if (added && generation == _indexGeneration) RefreshDownloadedState();
    }

    private bool HasCounterpartAnswer(ModSource targetStore, ModSummary sourceMod) =>
        _counterpartKeyCache.ContainsKey($"{targetStore}:{ModKey(sourceMod)}")
        || App.State.ModCounterparts.TryGet(targetStore, sourceMod, out _);

    private async Task<string?> ResolveCounterpartKeyAsync(ModSource targetStore, ModSummary sourceMod)
    {
        var memoKey = $"{targetStore}:{ModKey(sourceMod)}";
        if (_counterpartKeyCache.TryGetValue(memoKey, out var cached)) return cached;
        if (App.State.ModCounterparts.TryGet(targetStore, sourceMod, out var remembered))
        {
            var rememberedKey = remembered is not null ? ModKey(remembered) : null;
            _counterpartKeyCache[memoKey] = rememberedKey;
            return rememberedKey;
        }
        try
        {
            var counterpart = targetStore == ModSource.Modrinth
                ? await App.State.Modrinth.FindCounterpartAsync(sourceMod.Slug, sourceMod.Name)
                : await App.State.CurseForge.FindCounterpartAsync(sourceMod.Slug, sourceMod.Name);
            var key = counterpart is not null ? ModKey(counterpart) : null;
            _counterpartKeyCache[memoKey] = key;
            App.State.ModCounterparts.Remember(targetStore, sourceMod, counterpart);
            return key;
        }
        catch { return null; /* offline: not remembered, so a later pass asks again */ }
    }

    private IEnumerable<string> EnumeratePackModFiles()
    {
        var dirs = new[]
        {
            Path.Combine(App.State.Packs.GameDir(_pack.Id), "mods"),
            Path.Combine(App.State.Packs.LocalDir(_pack.Id), "mods")
        }.Distinct();
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            List<string> files;
            try { files = Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly).ToList(); }
            catch { continue; }
            foreach (var file in files)
                if (file.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                    || file.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
                    yield return file;
        }
    }

    /// <summary>Records a jar that just landed: its key for the badges, and its two store hashes for the
    /// fingerprint cache (hashed off the UI thread).</summary>
    private async Task RememberInstalledModAsync(ModDownloadItem item, string path)
    {
        _installedModKeys.Add(ModKey(item.Mod));
        try
        {
            var (sha512, curseForgeFingerprint) = await Task.Run(() => ModFingerprintCache.ComputeHashes(path));
            App.State.ModFingerprints.Store(path, sha512, curseForgeFingerprint, new CachedModMatch
            {
                Mod = item.Mod,
                Version = item.Version
            });
        }
        catch { /* a locked file: the badge still shows */ }
    }

    // ── the instance's version and loader ────────────────────────────────────

    /// <summary>The mod's versions from the shared catalog: with <paramref name="applyFilters"/>, only
    /// those for this instance's Minecraft version and loader, filtered by the store.</summary>
    private async Task<List<ModVersion>> LoadVersionsForModAsync(ModSummary mod, bool applyFilters, CancellationToken ct)
    {
        if (!applyFilters) return (await _catalog.VersionsAsync(mod, null, null, ct)).ToList();
        var mc = PackMinecraftVersion();
        var loader = PackLoaderTag();
        return (await _catalog.VersionsAsync(mod, mc.Length > 0 ? mc : null, loader.Length > 0 ? loader : null, ct)).ToList();
    }

    private ModVersion? PickBestVersion(IReadOnlyList<ModVersion> versions)
    {
        if (!CanInstallModsToPack()) return null;
        var mc = PackMinecraftVersion();
        var loader = PackLoaderTag();
        return ModUpdateChannel.PickNewest(versions.Where(v => MatchesFilters(v, mc, loader)), PackChannel(),
            v => v.ReleaseChannel, v => v.DatePublished);
    }

    /// <summary>The release channel this instance's downloads follow.</summary>
    private string PackChannel() => App.State.ModMetadata.EffectiveUpdateChannel(_pack.Id);

    private static bool MatchesFilters(ModVersion version, string mc, string loader) =>
        (mc.Length == 0 || version.GameVersions.Contains(mc, StringComparer.OrdinalIgnoreCase)) &&
        (loader.Length == 0 || version.Loaders.Contains(loader, StringComparer.OrdinalIgnoreCase));

    private string PackMinecraftVersion() => _pack.MinecraftVersion?.Trim() ?? "";

    private string PackLoaderTag() => _pack.Loader == LoaderKind.None ? "" : _pack.Loader.ToString().ToLowerInvariant();

    private bool CanInstallModsToPack() =>
        !_pack.IsEmpty && !string.IsNullOrWhiteSpace(_pack.MinecraftVersion) && _pack.Loader != LoaderKind.None;

    private string PackVersionLabel()
    {
        var mc = string.IsNullOrWhiteSpace(_pack.MinecraftVersion) ? "no Minecraft version" : $"Minecraft {_pack.MinecraftVersion}";
        return _pack.Loader == LoaderKind.None ? mc : $"{mc} · {_pack.Loader}";
    }

    private void Say(string text, bool danger = false)
    {
        DownloadStatus.Text = text;
        DownloadStatus.Classes.Set("danger", danger);
        if (!DetailContent.IsVisible)
        {
            ListStatus.Text = text;
            ListStatus.Classes.Set("danger", danger);
        }
    }

    // ── for previews ─────────────────────────────────────────────────────────

    /// <summary>Marks mods as already in the instance, as the identity pass would (for previews, whose
    /// jars no store knows).</summary>
    public void MarkInstalled(IEnumerable<ModSummary> mods)
    {
        foreach (var mod in mods) _installedModKeys.Add(ModKey(mod));
        RefreshDownloadedState();
    }
}

/// <summary>One search result in <see cref="ModBrowserPage"/>.</summary>
public sealed class ModBrowseRow(ModSummary source, bool installed) : INotifyPropertyChanged
{
    private bool _installed = installed;

    public ModSummary Source { get; } = source;
    public string Name => Source.Name;
    public string Initial => ModText.Initial(Source.Name);
    public string? IconUrl => Source.IconUrl;
    public string Summary => Source.Description?.Trim() ?? "";

    public string MetaLabel =>
        $"by {Source.Author ?? "unknown"}  ·  {ModText.Number(Source.DownloadCount)} downloads  ·  {ModText.Store(Source.Source)}";

    public bool IsInstalled
    {
        get => _installed;
        set
        {
            if (_installed == value) return;
            _installed = value;
            Changed();
            Changed(nameof(CanInstall));
            Changed(nameof(InstallTip));
        }
    }

    public bool CanInstall => !_installed;
    public string InstallTip => _installed ? "Already in this instance" : "Install the newest version";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One version in <see cref="ModBrowserPage"/>'s list.</summary>
public sealed class ModBrowseVersionRow(ModVersion source, bool installed, bool compatible, string packLabel)
{
    public ModVersion Source { get; } = source;
    public bool IsInstalled { get; } = installed;
    public bool IsCompatible { get; } = compatible;
    public bool CanInstall => !IsInstalled && IsCompatible;

    public string InstallTip =>
        IsInstalled ? "Already in this instance"
        : !IsCompatible ? $"Not for {packLabel}"
        : "Install this version";

    public string VersionNumber => string.IsNullOrWhiteSpace(Source.VersionNumber) ? Source.Name : Source.VersionNumber;

    public string Channel => Source.ReleaseChannel?.ToLowerInvariant() switch
    {
        "beta" => "Beta",
        "alpha" => "Alpha",
        _ => "Release"
    };

    public bool IsPreview => Channel != "Release";

    public string MetaLabel
    {
        get
        {
            var parts = new List<string>();
            if (Source.GameVersions.Length > 0)
                parts.Add(string.Join(", ", Source.GameVersions.Take(3)) + (Source.GameVersions.Length > 3 ? "..." : ""));
            if (Source.Loaders.Length > 0) parts.Add(string.Join(", ", Source.Loaders));
            parts.Add(TimeFormat.Date(Source.DatePublished));
            var size = Source.Files.FirstOrDefault(f => f.IsPrimary)?.Size ?? Source.Files.FirstOrDefault()?.Size ?? 0;
            if (size > 0) parts.Add(StorageUsageService.Size(size));
            return string.Join("  ·  ", parts);
        }
    }
}
