using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// The shader store as a side-panel page: search CurseForge and Modrinth, look at the screenshots,
/// and download a pack into any number of instances, or none.
/// </summary>
/// <remarks>
/// <para>Both stores are paged side by side, each in its own try/catch rather than
/// <c>Task.WhenAll</c>: shader packs are split across the two, and one store failing shouldn't throw
/// away the other's results. A failed store is marked done and named in the status line. The source
/// chips narrow the search to one store.</para>
/// <para>The destination is asked after the download, by <see cref="ImportContentCard"/>, with the
/// instance this page was opened for pre-ticked and "library only" in the same list.
/// <see cref="DisplayTargetPackId"/> is only that default tick.</para>
/// </remarks>
public partial class ShaderBrowserView : Page
{
    private readonly MainWindow _shell;
    private readonly List<PackSummary> _packs;
    private readonly ObservableCollection<ShaderStoreRow> _rows = new();
    private readonly ObservableCollection<StoreSourceChip> _sourceChips = new();
    private readonly List<ModVersion> _cachedVersions = new();

    private readonly PageState _state;
    private readonly InfiniteScroll.Pager _pager;

    private CancellationTokenSource _searchCts = new();
    private CancellationTokenSource? _installCts;

    private ShaderStoreRow? _selectedRow;
    private string? _projectUrl;

    private StoreSource _source = StoreSource.Both;
    private int _off;
    private bool _modrinthDone;
    private bool _curseDone;
    private bool _busy;

    private const int PageSize = 50;   // both stores cap a page at 50

    /// <summary>A stop against a runaway pager, not a browsing limit. The other browse lists use 2000
    /// too.</summary>
    private const int BrowseCap = 2000;

    /// <summary>Index of the Overview tab, whose WebView2 has to be hidden when it is not showing.</summary>
    private const int OverviewTabIndex = 1;

    /// <param name="preferredTarget">The instance the Shaders page was filtered to. It starts ticked
    /// and sets the Minecraft version filter.</param>
    public ShaderBrowserView(MainWindow shell, IReadOnlyList<PackSummary> packs, Guid? preferredTarget)
    {
        InitializeComponent();
        _shell = shell;
        _packs = packs.ToList();
        LastTargetPackId = preferredTarget is { } id && _packs.Any(p => p.Id == id) ? id : null;

        ResultsList.ItemsSource = _rows;
        SourceStrip.ItemsSource = _sourceChips;
        DetailTabs.SelectionChanged += OnDetailTabsChanged;
        OverviewHost.Visibility = Visibility.Collapsed;
        OverviewBrowser.Visibility = Visibility.Collapsed;

        _state = new PageState(ResultsList, PageStateHost, nameof(ShaderBrowserView))
            .Copy(PageCopy.Results)
            .Slots(CountLabel, StatusLabel, BusyBar)
            .DisableWhileBusy(RefreshButton, FiltersButton);
        _state.EmptyCopy("No shader packs matched",
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

    /// <summary>Raised after a pack lands on disk, with the instance it landed in, so the Shaders
    /// page re-scans rather than waiting for the user to hit Refresh.</summary>
    public event Action<Guid>? Installed;

    /// <summary>The instance the user last chose here, so the page that opened this does not ask the
    /// same question again.</summary>
    public Guid? LastTargetPackId { get; private set; }

    // ── target instance ──────────────────────────────────────────────────────

    /// <summary>The instance a download starts out ticked for, and whose Minecraft version filters
    /// the search. Null when nothing is in scope; the target label says so.</summary>
    private Guid? DisplayTargetPackId =>
        LastTargetPackId ?? (_packs.Count == 1 ? _packs[0].Id : null);

    private string NameOf(Guid? packId) =>
        packId is null ? "an instance you pick" : _packs.FirstOrDefault(p => p.Id == packId)?.Name ?? "the instance";

    private string? TargetMcVersion() =>
        _packs.FirstOrDefault(p => p.Id == DisplayTargetPackId)?.MinecraftVersion;

    private void UpdateTargetLabel()
    {
        InstallTargetLabel.Text = DisplayTargetPackId is null
            ? "You choose the instances after the download"
            : $"{NameOf(DisplayTargetPackId)} is ticked by default";
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
        _sourceChips.Add(new StoreSourceChip("Modrinth", _source == StoreSource.Modrinth,
            "Modrinth only"));
        _sourceChips.Add(new StoreSourceChip("CurseForge", _source == StoreSource.CurseForge,
            "CurseForge only - where BSL and Rethinking Voxels live"));
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

    // Search on Enter only, no search-as-you-type: a live search over two rate-limited stores would
    // abandon a request per word typed.
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

    /// <summary>The X in the search box emptied it: search again at once, as Escape does, since this
    /// page otherwise only searches on Enter.</summary>
    private async void OnQueryCleared(object? sender, EventArgs e)
    {
        try { await RestartAsync(); }
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
        // _busy is cleared in the old drain's finally, and until that runs the fresh drain below
        // returns at its guard and the list stays empty.
        await _pager.DrainAsync();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        _off = 0;
        // A store the chips excluded counts as done before the first page, so the paging loop skips it.
        _modrinthDone = _source == StoreSource.CurseForge;
        _curseDone = _source == StoreSource.Modrinth;
        _rows.Clear();
        _pager.Reset();
        ClearDetail();

        // New query: don't keep the old results on screen.
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
                        limit: PageSize, offset: _off, projectType: "shader", ct: ct);
                    foreach (var m in chunk) _rows.Add(new ShaderStoreRow(m));
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
                        limit: PageSize, offset: _off, classId: CurseForgeService.ClassIdShaders, ct: ct);
                    foreach (var m in chunk) _rows.Add(new ShaderStoreRow(m));
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
            AppLog.LogError("shader-browser", ex);
            // Both searches go through the launcher's API proxy (ApiClient.ProxyAsync), so an
            // unreachable proxy arrives as OfflineException, which DescribeTransportFailure doesn't
            // classify. Report it as offline rather than as an error with a Retry.
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
                // Both stores unreachable means offline, not an error: installed packs still work.
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
                _state.EmptyNext($"No shader packs matched{versionNote}",
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

    // RichDescriptionView renders into an embedded WebView2 whose HWND draws over WPF (airspace),
    // so it has to be hidden whenever its tab is not the one on screen.
    private void OnDetailTabsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != DetailTabs) return;
        var onOverview = DetailTabs.SelectedIndex == OverviewTabIndex;
        OverviewHost.Visibility = onOverview ? Visibility.Visible : Visibility.Collapsed;
        OverviewBrowser.Visibility = onOverview ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearDetail()
    {
        _selectedRow = null;
        _projectUrl = null;
        _cachedVersions.Clear();
        DetailPanel.Visibility = Visibility.Collapsed;
        DetailPlaceholder.Visibility = Visibility.Visible;
        OverviewHost.Visibility = Visibility.Collapsed;
        OverviewBrowser.Visibility = Visibility.Collapsed;
    }

    private async void OnResultSelected(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (ResultsList.SelectedItem is not ShaderStoreRow row) return;
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
            if (ResultsList.SelectedItem is ShaderStoreRow row) await InstallAsync(row, null);
        }
        catch (Exception ex) { _state.Error("that pack could not be installed.", ex); }
    }

    /// <summary>Fills the right-hand pane, screenshots first since packs are picked on looks.</summary>
    private async Task ShowDetailAsync(ShaderStoreRow row)
    {
        _selectedRow = row;
        DetailPanel.Visibility = Visibility.Visible;
        DetailPlaceholder.Visibility = Visibility.Collapsed;
        DetailTabs.SelectedIndex = 0;

        StoreNameLabel.Text = row.Name;
        StoreMetaLabel.Text = row.Meta;
        SelectedIconFallback.Text = row.Initial;
        IconLoader.SetUrl(SelectedIconImage, row.IconUrl);

        ScreenshotList.ItemsSource = null;
        ScreenshotsEmptyText.Text = "Loading screenshots...";
        ScreenshotsEmptyText.Visibility = Visibility.Visible;
        VersionsGrid.ItemsSource = null;
        _cachedVersions.Clear();
        OverviewBrowser.Show("Loading...");
        _projectUrl = row.ProjectUrl;

        var detail = row.Project.Source == ModSource.CurseForge
            ? await App.State.CurseForge.GetProjectDetailAsync(int.TryParse(row.Project.Id, out var cid) ? cid : 0)
            : await App.State.Modrinth.GetProjectDetailAsync(row.Project.Id);

        // The selection can move while the detail is in flight.
        if (!ReferenceEquals(_selectedRow, row)) return;

        OverviewBrowser.Show(detail.Description ?? row.Project.Description, detail.IsMarkdown);

        ScreenshotList.ItemsSource = detail.Screenshots.Select(i => new MediaRow(i)).ToList();
        ScreenshotsEmptyText.Text = "This pack published no screenshots.";
        ScreenshotsEmptyText.Visibility = detail.Screenshots.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

        ToggleLink(ProjectPageButton, detail.Links.WebsiteUrl ?? row.ProjectUrl);
        ToggleLink(IssuesButton, detail.Links.IssuesUrl);
        ToggleLink(SourceButton, detail.Links.SourceUrl);
        ToggleLink(WikiButton, detail.Links.WikiUrl);
        ToggleLink(DiscordButton, detail.Links.DiscordUrl);
        LinksEmptyText.Visibility =
            new[] { ProjectPageButton, IssuesButton, SourceButton, WikiButton, DiscordButton }
                .Any(b => b.Visibility == Visibility.Visible)
                ? Visibility.Collapsed
                : Visibility.Visible;

        var versions = await App.State.ModVersions.GetVersionsAsync(row.Project);
        if (!ReferenceEquals(_selectedRow, row)) return;
        _cachedVersions.Clear();
        _cachedVersions.AddRange(versions);
        VersionsGrid.ItemsSource = versions
            .OrderByDescending(v => v.DatePublished)
            .Select(v => new ShaderVersionRow(v))
            .ToList();
        InstallSelectedButton.IsEnabled = versions.Count > 0;
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

    private static ShaderStoreRow? RowFromMenu(object sender)
    {
        if (sender is not MenuItem leaf) return null;
        DependencyObject? p = leaf;
        while (p is MenuItem m) p = m.Parent as MenuItem ?? m.Parent;
        return p is ContextMenu { PlacementTarget: FrameworkElement fx } && fx.DataContext is ShaderStoreRow r
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
            if (sender is FrameworkElement { Tag: ShaderStoreRow row }) await InstallAsync(row, null);
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

    /// <summary>Opens the version picker, so an older release or a beta can be installed instead of
    /// whatever the store lists first.</summary>
    /// <remarks>Doesn't ask for an instance first: the picker only needs a Minecraft version to
    /// highlight, which <see cref="TargetMcVersion"/> provides.</remarks>
    private async void OnCtxChooseVersion(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RowFromMenu(sender) is not { } row) return;

            _state.Note($"Loading versions of {row.Name}...");
            var versions = (await App.State.ModVersions.GetVersionsAsync(row.Project)).ToList();
            if (versions.Count == 0) { _state.Note("That pack has no downloadable version."); return; }

            var pick = await ModVersionPickerDialog.ShowAsync(_shell, row.Name, versions,
                TargetMcVersion(), null);
            if (pick is null) { _state.Note(""); return; }
            await InstallAsync(row, pick.Version);
        }
        catch (Exception ex) { _state.Error("that version could not be installed.", ex); }
    }

    /// <summary>
    /// Downloads a shader pack (with a real progress bar and a working Cancel), then asks
    /// <see cref="ImportContentCard"/> which instances get it; none is a valid answer.
    /// </summary>
    /// <remarks>
    /// The instance in scope arrives pre-ticked, so installing into it is one extra click. The card never
    /// overwrites an instance's own same-named pack and says so in its receipt instead. Only
    /// re-downloading the same build can collide, since a new release has a new file name.
    /// </remarks>
    /// <param name="pinned">The exact version to install, or null for the newest one that matches the
    /// Minecraft version of whatever instance is in scope.</param>
    private async Task InstallAsync(ShaderStoreRow row, ModVersion? pinned)
    {
        _installCts?.Cancel();
        _installCts = new CancellationTokenSource();
        var ct = _installCts.Token;

        _state.Note($"Finding {row.Name}...");
        try
        {
            ModVersion? version = pinned;
            if (version is null)
            {
                // Shaders depend on the Minecraft version, not the loader. Fall back to the unfiltered
                // list so a pack that doesn't tag its versions can still be installed.
                var matching = await App.State.ModVersions.GetVersionsAsync(row.Project, TargetMcVersion(), null, ct: ct);
                version = matching.FirstOrDefault()
                          ?? (await App.State.ModVersions.GetVersionsAsync(row.Project, ct: ct)).FirstOrDefault();
            }
            if (version is null) { _state.Note("That pack has no downloadable version."); return; }

            var file = version.Files.FirstOrDefault(f => f.IsPrimary) ?? version.Files.FirstOrDefault();
            var url = file?.DownloadUrl;
            if (string.IsNullOrWhiteSpace(url) && version.Source == ModSource.CurseForge)
            {
                var ids = version.Id.Split(':');
                if (ids.Length == 2 && int.TryParse(ids[0], out var modId) && int.TryParse(ids[1], out var fileId))
                    url = await App.State.CurseForge.GetDownloadUrlAsync(modId, fileId);
            }
            if (string.IsNullOrWhiteSpace(url) || file is null)
            {
                _state.Note("No download link for that pack - install it from its site instead.");
                return;
            }

            // Keep the store's file name: Iris writes it into iris.properties and the Shaders page
            // lists it, and "ComplementaryUnbound_r5.3.zip" says more there than the listing title.
            using var staged = StagedDownload.For(LibraryKind.ShaderPack, Path.GetFileName(file.Filename));
            if (!PathSafety.IsSafeFileName(staged.FileName))
            {
                AppLog.Log(nameof(ShaderBrowserView), $"Skipped {row.Name}: its file name is not a plain name: {file.Filename}");
                _state.Note("Download failed: the store gave a file name that cannot be used.");
                return;
            }
            await DownloadAsync(url!, staged.FilePath, $"Downloading {row.Name}...", ct);
            // The card takes over the window, so drop this page's transfer UI first; a Cancel button
            // behind a backdrop can't be pressed.
            EndDownloadUi();

            // No DisplayName: the library derives the name from the file, which is what Iris and the
            // Shaders page use. A listing title would rename the pack in every instance.
            var outcome = await ImportContentCard.ShowAsync(_shell, new ImportRequest(
                ImportContentKind.ShaderPack, _packs,
                PreferredPackId: DisplayTargetPackId, PrePickedPath: staged.FilePath,
                SourceIsFixed: true,
                OriginSource: version.Source, OriginProjectId: row.Project.Id,
                OriginVersionId: version.Id, OriginVersionNumber: version.VersionNumber));
            if (outcome is null)
            {
                _state.Note("Nothing was installed - the download was thrown away.");
                return;
            }

            RecordProvenanceIfNotInLibrary(outcome, staged.FileName, row, version);

            // Whatever the user ticked becomes the default tick and the label above it, so the next
            // download starts where the last one went.
            if (outcome.Targets.Count > 0)
            {
                LastTargetPackId = outcome.Targets[0];
                UpdateTargetLabel();
            }

            _state.Note(outcome.Summary + LoaderWarning(outcome.Targets));
            // Per instance: the Shaders page invalidates that instance's cached scan and re-reads. Its
            // LoadAsync cancels the previous pass, so the loop costs one repaint.
            foreach (var target in outcome.Targets) Installed?.Invoke(target);
        }
        catch (OperationCanceledException) { _state.Note("Download cancelled."); }
        catch (Exception ex) { _state.Error($"{row.Name} could not be installed.", ex); }
        finally { EndDownloadUi(); }
    }

    /// <summary>
    /// Records which store listing this pack came from, in the one case the library can't.
    /// </summary>
    /// <remarks>
    /// Normally the provenance travels in the import request and the library copies it to every
    /// instance the pack reaches. Without a library copy (the library rejected the file or the copy
    /// failed), the pack still lands in each ticked instance's <c>game/shaderpacks/</c> with nothing
    /// carrying the provenance, and the Shaders page then can't offer updates.
    /// <see cref="ImportOutcome.AddedToLibrary"/> says which case ran.
    /// </remarks>
    private static void RecordProvenanceIfNotInLibrary(ImportOutcome outcome, string fileName,
                                                       ShaderStoreRow row, ModVersion version)
    {
        if (outcome.AddedToLibrary) return;

        foreach (var packId in outcome.Targets)
            App.State.Shaders.RecordProvenance(packId, fileName, version.Source,
                row.Project.Id, version.Id, version.VersionNumber);
    }

    /// <summary>The "this will not load yet" tail for the receipt: the ticked instances with nothing
    /// in <c>mods/</c> that can read a shader pack.</summary>
    /// <remarks>The card already flags this per row before the tick; this is the reminder afterwards.
    /// Empty for a library-only import.</remarks>
    private string LoaderWarning(IReadOnlyList<Guid> targets)
    {
        if (targets.Count == 0) return "";
        var without = targets.Where(id => SafeLoader(id, NameOf(id)) == ShaderLoader.None)
            .Select(id => NameOf(id))
            .ToList();
        return without.Count == 0
            ? "  ·  Use it from the Shaders page."
            : $"  ·  {string.Join(", ", without)} still needs Iris (or Oculus) before a shader will load.";
    }

    private static ShaderLoader SafeLoader(Guid packId, string packName)
    {
        try { return App.State.Shaders.DetectLoader(packId, packName); }
        catch { return ShaderLoader.Iris; }   // unknown is not "none": never warn on a guess
    }

    /// <summary>Downloads to <paramref name="dest"/> with a determinate bar and a Cancel button; shader
    /// zips run 5-60 MB.</summary>
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
            _state.Note($"{label}  {p.done / 1024 / 1024.0:0.#} / {p.total / 1024 / 1024.0:0.#} MB");
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

// ── row view models ──────────────────────────────────────────────────────────

/// <summary>One chip in the browser's store strip.</summary>
/// <remarks>Holds no <see cref="System.Windows.Media.Brush"/>: a brush resolved here would be the
/// theme's colour at the moment the chip was built and would survive a theme change. The selected
/// chip's accent border comes from a DataTrigger on <see cref="IsSelected"/> instead.</remarks>
public sealed class StoreSourceChip(string label, bool isSelected, string toolTip)
{
    public string Label { get; } = label;
    public bool IsSelected { get; } = isSelected;
    public string ToolTipText { get; } = toolTip;
    public FontWeight LabelWeight => IsSelected ? FontWeights.SemiBold : FontWeights.Normal;
}

/// <summary>One shader pack listing from Modrinth or CurseForge.</summary>
public sealed class ShaderStoreRow(ModSummary project)
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
                ? $"https://www.curseforge.com/minecraft/shaders/{slug}"
                : $"https://modrinth.com/shader/{slug}";
        }
    }
}

/// <summary>One row of the store detail pane's Versions grid.</summary>
public sealed class ShaderVersionRow(ModVersion version)
{
    public ModVersion Version { get; } = version;
    public string VersionNumber => Version.VersionNumber;
    public string McVersions => string.Join(", ", Version.GameVersions);
    public string Channel => Version.ReleaseChannel;
    public string DateLabel => TimeFormat.Date(Version.DatePublished);

    public string SizeLabel
    {
        get
        {
            var file = Version.Files.FirstOrDefault(f => f.IsPrimary) ?? Version.Files.FirstOrDefault();
            if (file is null || file.Size <= 0) return "";
            var kb = file.Size / 1024.0;
            return kb < 1024 ? $"{kb:0.#} KB" : $"{kb / 1024.0:0.#} MB";
        }
    }
}
