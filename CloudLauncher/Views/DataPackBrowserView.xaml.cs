using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// The data pack store as a side-panel page: search CurseForge and Modrinth, read about a pack, and
/// download it into the worlds you pick.
/// </summary>
/// <remarks>
/// <para>A near-twin of <see cref="ShaderBrowserView"/>, which set the shape for a store-only browser:
/// both stores paged side by side, each in its own try/catch so one failing doesn't throw away the
/// other's results, and the destination asked after the download.</para>
/// <para>The destination is different in kind, though. A data pack belongs to a world, not an
/// instance, so <see cref="DataPackTargetCard"/> asks for the worlds, and offers "every world" only
/// where the instance has a mod that loads packs globally (<see cref="GlobalPackService"/>). There is
/// no launcher-kept copy for data packs: a world's pack is part of that save, the way the game treats
/// it.</para>
/// <para>Modrinth lists data packs as their own project type, and many projects ship a mod and a data
/// pack side by side, so the version list is asked for the <c>datapack</c> loader and only a
/// <c>.zip</c> file is ever installed.</para>
/// </remarks>
public partial class DataPackBrowserView : Page
{
    private readonly MainWindow _shell;
    private readonly List<PackSummary> _packs;
    private readonly ObservableCollection<DataPackStoreRow> _rows = new();
    private readonly ObservableCollection<StoreSourceChip> _sourceChips = new();

    private readonly PageState _state;
    private readonly InfiniteScroll.Pager _pager;

    private CancellationTokenSource _searchCts = new();
    private CancellationTokenSource? _installCts;

    private DataPackStoreRow? _selectedRow;
    private string? _projectUrl;

    private StoreSource _source = StoreSource.Both;
    private int _off;
    private bool _modrinthDone;
    private bool _curseDone;
    private bool _busy;

    /// <summary>The world the page was opened for, if any. It starts ticked in the target card.</summary>
    private string? _preferredWorldDir;

    private const int PageSize = 50;   // both stores cap a page at 50

    /// <summary>A stop against a runaway pager, not a browsing limit, as on the other browsers.</summary>
    private const int BrowseCap = 2000;

    /// <summary>Index of the Overview tab, whose WebView2 has to be hidden when it is not showing.
    /// First here, unlike the shader store: a data pack is picked on what it does, not on how it
    /// looks.</summary>
    private const int OverviewTabIndex = 0;

    /// <summary>Modrinth's loader name for the data pack build of a project.</summary>
    private const string DataPackLoader = "datapack";

    /// <param name="preferredTarget">The instance the page was opened from. It starts selected in the
    /// target card and sets the Minecraft version filter.</param>
    /// <param name="preferredWorldDir">The save the page was opened from (the world page's Data packs
    /// tab), or null.</param>
    public DataPackBrowserView(MainWindow shell, IReadOnlyList<PackSummary> packs, Guid? preferredTarget,
                               string? preferredWorldDir = null)
    {
        InitializeComponent();
        _shell = shell;
        _packs = packs.ToList();
        LastTargetPackId = preferredTarget is { } id && _packs.Any(p => p.Id == id) ? id : null;
        _preferredWorldDir = LastTargetPackId is null ? null : preferredWorldDir;

        ResultsList.ItemsSource = _rows;
        SourceStrip.ItemsSource = _sourceChips;
        DetailTabs.SelectionChanged += OnDetailTabsChanged;
        OverviewHost.Visibility = Visibility.Collapsed;
        OverviewBrowser.Visibility = Visibility.Collapsed;

        _state = new PageState(ResultsList, PageStateHost, nameof(DataPackBrowserView))
            .Copy(PageCopy.Results)
            .Slots(CountLabel, StatusLabel, BusyBar)
            .DisableWhileBusy(RefreshButton, FiltersButton);
        _state.EmptyCopy("No data packs matched",
            "Try a shorter search, or turn on 'Any Minecraft version' if this instance is on an older "
            + "Minecraft than the packs list.");
        _state.RetryRequested += () => _ = RestartAsync();

        _pager = new InfiniteScroll.Pager(
            () => !_busy && !(_modrinthDone && _curseDone) && _rows.Count < BrowseCap,
            () => _rows.Count,
            DrainAsync);

        RebuildSourceChips();
        UpdateTargetLabel();

        Loaded += async (_, _) =>
        {
            QueryBox.Focus();
            await RestartAsync();
        };
        Unloaded += (_, _) =>
        {
            _searchCts.Cancel();
            _installCts?.Cancel();
        };
    }

    /// <summary>Raised after a pack lands on disk, with the instance it landed in, so an open world
    /// page re-reads its list.</summary>
    public event Action<Guid>? Installed;

    /// <summary>The instance the user last installed into here.</summary>
    public Guid? LastTargetPackId { get; private set; }

    // ── target instance ──────────────────────────────────────────────────────

    private Guid? DisplayTargetPackId =>
        LastTargetPackId ?? (_packs.Count == 1 ? _packs[0].Id : null);

    private string NameOf(Guid? packId) =>
        packId is null ? "an instance you pick" : _packs.FirstOrDefault(p => p.Id == packId)?.Name ?? "the instance";

    private string? TargetMcVersion() =>
        _packs.FirstOrDefault(p => p.Id == DisplayTargetPackId)?.MinecraftVersion;

    private void UpdateTargetLabel()
    {
        InstallTargetLabel.Text = DisplayTargetPackId is null
            ? "You pick the worlds after the download"
            : _preferredWorldDir is { } dir
                ? $"{Path.GetFileName(dir)} in {NameOf(DisplayTargetPackId)} is ticked to start with"
                : $"{NameOf(DisplayTargetPackId)} is picked to start with";
        VersionHintLabel.Text = TargetMcVersion() is { Length: > 0 } mc
            ? $"Off: only packs listed for Minecraft {mc}."
            : "No target instance yet, so nothing is filtered by Minecraft version.";
    }

    // ── source chips ─────────────────────────────────────────────────────────

    private enum StoreSource { Both, Modrinth, CurseForge }

    private void RebuildSourceChips()
    {
        _sourceChips.Clear();
        _sourceChips.Add(new StoreSourceChip("Both stores", _source == StoreSource.Both,
            "Page CurseForge and Modrinth together - one store failing does not lose the other's results"));
        _sourceChips.Add(new StoreSourceChip("Modrinth", _source == StoreSource.Modrinth, "Modrinth only"));
        _sourceChips.Add(new StoreSourceChip("CurseForge", _source == StoreSource.CurseForge, "CurseForge only"));
    }

    private async void OnSourceChipClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (sender is not FrameworkElement { DataContext: StoreSourceChip chip }) return;
            var picked = chip.Label switch
            {
                "Modrinth" => StoreSource.Modrinth,
                "CurseForge" => StoreSource.CurseForge,
                _ => StoreSource.Both
            };
            if (picked == _source) return;
            _source = picked;
            RebuildSourceChips();
            await RestartAsync();
        }
        catch (Exception ex) { _state.Error("that store could not be searched.", ex); }
    }

    // ── search ───────────────────────────────────────────────────────────────

    // Enter only, as on the shader store: a live search over two rate-limited stores would abandon a
    // request per word typed.
    private async void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await RestartAsync();
            }
            else if (e.Key == Key.Escape && QueryBox.Text.Length > 0)
            {
                e.Handled = true;
                QueryBox.Clear();
                await RestartAsync();
            }
        }
        catch (Exception ex) { _state.Error("the stores could not be searched.", ex); }
    }

    protected override async void OnPreviewKeyDown(KeyEventArgs e)
    {
        try
        {
            if (e.Key == Key.F5)
            {
                e.Handled = true;
                await RestartAsync();
            }
            else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                QueryBox.Focus();
                QueryBox.SelectAll();
            }
        }
        catch (Exception ex) { _state.Error("that could not be done.", ex); }
        base.OnPreviewKeyDown(e);
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        try { await RestartAsync(); }
        catch (Exception ex) { _state.Error("the stores could not be searched.", ex); }
    }

    private void OnFiltersClick(object sender, RoutedEventArgs e)
    {
        UpdateTargetLabel();
        FiltersPopup.IsOpen = !FiltersPopup.IsOpen;
    }

    private async void OnApplyFilters(object sender, RoutedEventArgs e)
    {
        try
        {
            FiltersPopup.IsOpen = false;
            await RestartAsync();
        }
        catch (Exception ex) { _state.Error("the stores could not be searched.", ex); }
    }

    /// <summary>Starts a fresh search, waiting for the one it just cancelled rather than racing it.</summary>
    private async Task RestartAsync()
    {
        _searchCts.Cancel();
        await _pager.DrainAsync();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        _off = 0;
        _modrinthDone = _source == StoreSource.CurseForge;
        _curseDone = _source == StoreSource.Modrinth;
        _rows.Clear();
        _pager.Reset();
        ClearDetail();

        _state.Begin("Searching CurseForge and Modrinth...", refreshing: false);
        await _pager.LoadPageAsync(ct);
    }

    private async void OnResultsScroll(object sender, ScrollChangedEventArgs e)
    {
        try
        {
            if (e.OriginalSource is not ScrollViewer sv) return;
            await _pager.FillAheadAsync(sv, _searchCts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _state.Error("the next page could not be loaded.", ex); }
    }

    /// <summary>Pulls the next page from each store that still has one.</summary>
    private async Task DrainAsync(CancellationToken ct)
    {
        if (_busy || (_modrinthDone && _curseDone) || _rows.Count >= BrowseCap) return;

        _busy = true;
        var query = QueryBox.Text?.Trim() ?? "";
        var mc = AnyVersionBox.IsChecked == true ? null : TargetMcVersion();
        var problems = new List<string>();
        var offline = new List<string>();
        var added = 0;

        try
        {
            if (!_modrinthDone)
            {
                try
                {
                    var chunk = await App.State.Modrinth.SearchAsync(query, mc, null,
                        limit: PageSize, offset: _off, projectType: "datapack", ct: ct);
                    foreach (var m in chunk) _rows.Add(new DataPackStoreRow(m));
                    added += chunk.Count;
                    _modrinthDone = chunk.Count < PageSize;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _modrinthDone = true; Blame("Modrinth", ex); }
            }

            if (!_curseDone)
            {
                try
                {
                    var chunk = await App.State.CurseForge.SearchAsync(query, mc, null,
                        limit: PageSize, offset: _off, classId: CurseForgeService.ClassIdDataPacks, ct: ct);
                    foreach (var m in chunk) _rows.Add(new DataPackStoreRow(m));
                    added += chunk.Count;
                    _curseDone = chunk.Count < PageSize;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _curseDone = true; Blame("CurseForge", ex); }
            }

            _off += PageSize;
            Report();
        }
        catch (OperationCanceledException) { }
        finally { _busy = false; }

        void Blame(string store, Exception ex)
        {
            AppLog.LogError("datapack-browser", ex);
            // Both searches go through the launcher's API proxy, so an unreachable proxy arrives as
            // OfflineException: offline, not an error with a Retry.
            var why = ex is OfflineException offlineEx
                ? offlineEx.Reason ?? "the server is unreachable"
                : Connectivity.DescribeTransportFailure(ex, ct);
            if (why is { Length: > 0 }) offline.Add($"{store}: {why}");
            else problems.Add(store);
        }

        void Report()
        {
            var versionNote = mc is { Length: > 0 } ? $" for Minecraft {mc}" : "";

            if (_rows.Count == 0)
            {
                if (offline.Count > 0 && problems.Count == 0)
                {
                    _state.Offline(string.Join("  ·  ", offline));
                    return;
                }
                if (problems.Count > 0 || offline.Count > 0)
                {
                    _state.Error(problems.Count + offline.Count == 2
                        ? "neither store answered."
                        : $"{string.Join(" and ", problems.Concat(offline.Select(o => o.Split(':')[0])))} did not answer.");
                    return;
                }
                _state.EmptyNext($"No data packs matched{versionNote}",
                    "Try a shorter search, or turn on 'Any Minecraft version' in Filters if this "
                    + "instance is on an older Minecraft than the packs list.");
                _state.Content(0);
                return;
            }

            var note = new List<string>();
            if (offline.Count > 0) note.Add(string.Join("  ·  ", offline));
            if (problems.Count > 0) note.Add($"{string.Join(" and ", problems)} did not answer.");
            if (_modrinthDone && _curseDone) note.Add(added == 0 ? "That is everything." : "");
            else note.Add("Scroll for more.");

            _state.Content(_rows.Count, note: string.Join("  ", note.Where(s => s.Length > 0)),
                countText: $"{_rows.Count:N0} result(s){versionNote}");
        }
    }

    // ── detail pane ──────────────────────────────────────────────────────────

    // The description renders into an embedded WebView2 whose HWND draws over WPF (airspace), so it
    // has to be hidden whenever its tab is not the one on screen.
    private void OnDetailTabsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != DetailTabs) return;
        ShowOverviewIfSelected();
    }

    private void ShowOverviewIfSelected()
    {
        var onOverview = DetailPanel.Visibility == Visibility.Visible && DetailTabs.SelectedIndex == OverviewTabIndex;
        OverviewHost.Visibility = onOverview ? Visibility.Visible : Visibility.Collapsed;
        OverviewBrowser.Visibility = onOverview ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearDetail()
    {
        _selectedRow = null;
        _projectUrl = null;
        DetailPanel.Visibility = Visibility.Collapsed;
        DetailPlaceholder.Visibility = Visibility.Visible;
        OverviewHost.Visibility = Visibility.Collapsed;
        OverviewBrowser.Visibility = Visibility.Collapsed;
    }

    private async void OnResultSelected(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (ResultsList.SelectedItem is not DataPackStoreRow row) return;
            await ShowDetailAsync(row);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _state.Error("that pack's details could not be loaded.", ex); }
    }

    private async void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Single click inspects, double click installs, as on every browser page.
        try
        {
            if (ResultsList.SelectedItem is DataPackStoreRow row) await InstallAsync(row, null);
        }
        catch (Exception ex) { _state.Error("that pack could not be installed.", ex); }
    }

    private async Task ShowDetailAsync(DataPackStoreRow row)
    {
        _selectedRow = row;
        DetailPanel.Visibility = Visibility.Visible;
        DetailPlaceholder.Visibility = Visibility.Collapsed;
        DetailTabs.SelectedIndex = OverviewTabIndex;
        ShowOverviewIfSelected();

        StoreNameLabel.Text = row.Name;
        StoreMetaLabel.Text = row.Meta;
        SelectedIconFallback.Text = row.Initial;
        IconLoader.SetUrl(SelectedIconImage, row.IconUrl);

        ScreenshotList.ItemsSource = null;
        ScreenshotsEmptyText.Text = "Loading screenshots...";
        ScreenshotsEmptyText.Visibility = Visibility.Visible;
        VersionsGrid.ItemsSource = null;
        OverviewBrowser.Show("Loading...");
        _projectUrl = row.ProjectUrl;

        var detail = row.Project.Source == ModSource.CurseForge
            ? await App.State.CurseForge.GetProjectDetailAsync(int.TryParse(row.Project.Id, out var cid) ? cid : 0)
            : await App.State.Modrinth.GetProjectDetailAsync(row.Project.Id);

        if (!ReferenceEquals(_selectedRow, row)) return;

        OverviewBrowser.Show(detail.Description ?? row.Project.Description, detail.IsMarkdown);

        ScreenshotList.ItemsSource = detail.Screenshots.Select(i => new MediaRow(i)).ToList();
        ScreenshotsEmptyText.Text = "This pack published no screenshots.";
        ScreenshotsEmptyText.Visibility = detail.Screenshots.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

        // Modrinth's own detail link points at /mod/, which redirects; the row knows the data pack URL.
        ToggleLink(ProjectPageButton, row.ProjectUrl);
        ToggleLink(IssuesButton, detail.Links.IssuesUrl);
        ToggleLink(SourceButton, detail.Links.SourceUrl);
        ToggleLink(WikiButton, detail.Links.WikiUrl);
        ToggleLink(DiscordButton, detail.Links.DiscordUrl);
        LinksEmptyText.Visibility =
            new[] { ProjectPageButton, IssuesButton, SourceButton, WikiButton, DiscordButton }
                .Any(b => b.Visibility == Visibility.Visible)
                ? Visibility.Collapsed
                : Visibility.Visible;

        var versions = await DataPackVersionsAsync(row.Project, null, CancellationToken.None);
        if (!ReferenceEquals(_selectedRow, row)) return;
        VersionsGrid.ItemsSource = versions
            .OrderByDescending(v => v.DatePublished)
            .Select(v => new ShaderVersionRow(v))
            .ToList();
        InstallSelectedButton.IsEnabled = versions.Count > 0;
    }

    /// <summary>The project's data pack builds: Modrinth's <c>datapack</c> loader, else every version
    /// (CurseForge has no loader for data packs, and some Modrinth packs don't tag one).</summary>
    private static async Task<IReadOnlyList<ModVersion>> DataPackVersionsAsync(ModSummary project, string? mc,
                                                                            CancellationToken ct)
    {
        if (project.Source == ModSource.Modrinth)
        {
            var tagged = await App.State.ModVersions.GetVersionsAsync(project, mc, DataPackLoader, ct: ct);
            if (tagged.Count > 0) return tagged;
        }
        return await App.State.ModVersions.GetVersionsAsync(project, mc, null, ct: ct);
    }

    private static void ToggleLink(Button button, string? url)
    {
        button.Tag = url ?? "";
        button.Visibility = string.IsNullOrWhiteSpace(url) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnOpenLink(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url }) OpenLink(url);
    }

    private void OnOpenSelectedProject(object sender, RoutedEventArgs e) => OpenLink(_projectUrl);

    private void OpenLink(string? url)
    {
        if (!string.IsNullOrWhiteSpace(url) && !SafeLaunch.OpenUrl(url))
            _state.Note("That link could not be opened.");
    }

    private void OnOpenScreenshot(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MediaRow row })
            ScreenshotPreviewWindow.ShowFor(Window.GetWindow(this), row.FullImageUrl, row.Title);
        e.Handled = true;
    }

    // ── row menu ─────────────────────────────────────────────────────────────

    private static DataPackStoreRow? RowFromMenu(object sender)
    {
        if (sender is not MenuItem leaf) return null;
        DependencyObject? p = leaf;
        while (p is MenuItem m) p = m.Parent as MenuItem ?? m.Parent;
        return p is ContextMenu { PlacementTarget: FrameworkElement fx } && fx.DataContext is DataPackStoreRow r
            ? r
            : null;
    }

    private void OnCtxOpenPage(object sender, RoutedEventArgs e)
    {
        if (RowFromMenu(sender) is { ProjectUrl: { Length: > 0 } url }) OpenLink(url);
    }

    private void OnCtxCopyUrl(object sender, RoutedEventArgs e)
    {
        if (RowFromMenu(sender) is not { } row) return;
        _state.Note(ClipboardHelper.TrySetText(row.ProjectUrl)
            ? "Copied the project URL."
            : "The clipboard is busy - nothing was copied.");
    }

    // ── installing ───────────────────────────────────────────────────────────

    private async void OnInstallRow(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement { Tag: DataPackStoreRow row }) await InstallAsync(row, null);
        }
        catch (Exception ex) { _state.Error("that pack could not be installed.", ex); }
    }

    private async void OnCtxInstall(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RowFromMenu(sender) is { } row) await InstallAsync(row, null);
        }
        catch (Exception ex) { _state.Error("that pack could not be installed.", ex); }
    }

    private async void OnInstallSelected(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_selectedRow is { } row) await InstallAsync(row, null);
        }
        catch (Exception ex) { _state.Error("that pack could not be installed.", ex); }
    }

    private async void OnInstallVersionClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement { DataContext: ShaderVersionRow vm } && _selectedRow is { } row)
                await InstallAsync(row, vm.Version);
        }
        catch (Exception ex) { _state.Error("that version could not be installed.", ex); }
    }

    private async void OnCtxChooseVersion(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RowFromMenu(sender) is not { } row) return;

            _state.Note($"Loading versions of {row.Name}...");
            var versions = (await DataPackVersionsAsync(row.Project, null, CancellationToken.None)).ToList();
            if (versions.Count == 0) { _state.Note("That pack has no downloadable version."); return; }

            var pick = await ModVersionPickerDialog.ShowAsync(_shell, row.Name, versions, TargetMcVersion(), null);
            if (pick is null) { _state.Note(""); return; }
            await InstallAsync(row, pick.Version);
        }
        catch (Exception ex) { _state.Error("that version could not be installed.", ex); }
    }

    /// <summary>
    /// Downloads a data pack, then asks <see cref="DataPackTargetCard"/> which worlds get it and
    /// copies it into each.
    /// </summary>
    /// <remarks>
    /// <para>The worlds are asked after the download so the card can say what the file is, and so a
    /// cancelled card costs nothing but the temp file, which is deleted either way.</para>
    /// <para>Only a <c>.zip</c> is installed. A project that also ships a mod has a <c>.jar</c> in the
    /// same version on CurseForge, and the game would ignore a jar in <c>datapacks/</c> without a
    /// word.</para>
    /// </remarks>
    /// <param name="pinned">The exact version, or null for the newest one for the target instance's
    /// Minecraft version.</param>
    private async Task InstallAsync(DataPackStoreRow row, ModVersion? pinned)
    {
        _installCts?.Cancel();
        _installCts = new CancellationTokenSource();
        var ct = _installCts.Token;

        _state.Note($"Finding {row.Name}...");
        string? tempFolder = null;
        try
        {
            ModVersion? version = pinned;
            if (version is null)
            {
                var matching = await DataPackVersionsAsync(row.Project, TargetMcVersion(), ct);
                version = matching.FirstOrDefault(v => ZipOf(v) is not null)
                          ?? (await DataPackVersionsAsync(row.Project, null, ct)).FirstOrDefault(v => ZipOf(v) is not null);
            }
            if (version is null) { _state.Note("That pack has no downloadable data pack (.zip) version."); return; }

            var file = ZipOf(version);
            if (file is null)
            {
                _state.Note($"{version.VersionNumber} has no .zip file - it may be the mod build. Pick another version.");
                return;
            }
            var url = file.DownloadUrl;
            if (string.IsNullOrWhiteSpace(url) && version.Source == ModSource.CurseForge)
            {
                var ids = version.Id.Split(':');
                if (ids.Length == 2 && int.TryParse(ids[0], out var modId) && int.TryParse(ids[1], out var fileId))
                    url = await App.State.CurseForge.GetDownloadUrlAsync(modId, fileId);
            }
            if (string.IsNullOrWhiteSpace(url))
            {
                _state.Note("No download link for that pack - install it from its site instead.");
                return;
            }

            var fileName = Path.GetFileName(file.Filename);
            if (!PathSafety.IsSafeFileName(fileName))
            {
                AppLog.Log(nameof(DataPackBrowserView), $"Skipped {row.Name}: its file name is not a plain name: {file.Filename}");
                _state.Note("Download failed: the store gave a file name that cannot be used.");
                return;
            }

            // Keep the store's file name: it becomes the pack's id in level.dat ("file/<name>") and
            // in /datapack commands, and says which build it is.
            tempFolder = Path.Combine(Path.GetTempPath(), $"cloudlauncher-download-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempFolder);
            var staged = Path.Combine(tempFolder, fileName);
            await DownloadAsync(url!, staged, $"Downloading {row.Name}...", ct);
            EndDownloadUi();

            var target = await DataPackTargetCard.ShowAsync(_shell, new DataPackTargetRequest(
                _packs, DisplayTargetPackId, _preferredWorldDir, row.Name, staged, version.GameVersions));
            if (target is null)
            {
                _state.Note("Nothing was installed - the download was thrown away.");
                return;
            }

            var summary = await Task.Run(() => DataPackTargetCard.Place(target, staged), ct);
            LastTargetPackId = target.Pack.Id;
            UpdateTargetLabel();
            _state.Note(summary);
            Installed?.Invoke(target.Pack.Id);
        }
        catch (OperationCanceledException) { _state.Note("Download cancelled."); }
        catch (Exception ex) { _state.Error($"{row.Name} could not be installed.", ex); }
        finally
        {
            EndDownloadUi();
            if (tempFolder is not null)
            {
                try { Directory.Delete(tempFolder, recursive: true); }
                catch (Exception ex) { AppLog.LogError(nameof(DataPackBrowserView), ex); }
            }
        }
    }

    /// <summary>The version's zip: the primary file when it is one, else the first zip listed.</summary>
    private static ModVersionFile? ZipOf(ModVersion version)
    {
        static bool IsZip(ModVersionFile f) => f.Filename.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        return version.Files.FirstOrDefault(f => f.IsPrimary && IsZip(f)) ?? version.Files.FirstOrDefault(IsZip);
    }

    /// <summary>Downloads with a determinate bar and a Cancel button.</summary>
    private async Task DownloadAsync(string url, string dest, string label, CancellationToken ct)
    {
        _state.Note(label);
        DownloadProgress.Value = 0;
        DownloadProgress.IsIndeterminate = true;
        DownloadProgress.Visibility = Visibility.Visible;
        CancelDownloadButton.Visibility = Visibility.Visible;

        var progress = new Progress<(long done, long total)>(p =>
        {
            if (p.total <= 0)
            {
                DownloadProgress.IsIndeterminate = true;
                return;
            }
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Value = Math.Clamp(p.done * 100.0 / p.total, 0, 100);
        });

        await App.State.Modrinth.DownloadFileAsync(url, dest, progress, ct);
    }

    private void EndDownloadUi()
    {
        DownloadProgress.Visibility = Visibility.Collapsed;
        DownloadProgress.IsIndeterminate = false;
        DownloadProgress.Value = 0;
        CancelDownloadButton.Visibility = Visibility.Collapsed;
    }

    private void OnCancelDownload(object sender, RoutedEventArgs e)
    {
        _installCts?.Cancel();
        _state.Note("Cancelling...");
    }
}

/// <summary>One data pack listing from Modrinth or CurseForge.</summary>
public sealed class DataPackStoreRow(ModSummary project)
{
    public ModSummary Project { get; } = project;
    public string Name => Project.Name;
    public string Summary => Project.Description ?? "";
    public string? IconUrl => Project.IconUrl;
    public bool IsCurseForge => Project.Source == ModSource.CurseForge;
    public string SourceBadge => IsCurseForge ? "CurseForge" : "Modrinth";

    public string Initial => string.IsNullOrWhiteSpace(Project.Name)
        ? "?"
        : char.ToUpperInvariant(Project.Name[0]).ToString();

    public string Meta =>
        $"{(Project.Author is { Length: > 0 } a ? a : "Unknown author")}" +
        $"{(Project.DownloadCount > 0 ? $"  ·  {Project.DownloadCount:N0} downloads" : "")}";

    public string RowToolTip => $"{Name}\n{SourceBadge}  ·  {Meta}\nDouble-click to install the newest version";

    public string ProjectUrl
    {
        get
        {
            var slug = string.IsNullOrWhiteSpace(Project.Slug) ? Project.Id : Project.Slug;
            return IsCurseForge
                ? $"https://www.curseforge.com/minecraft/data-packs/{slug}"
                : $"https://modrinth.com/datapack/{slug}";
        }
    }
}
