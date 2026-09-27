using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

public partial class PackListView : Page, IReusablePage, IRefreshablePage
{
    private readonly MainWindow _shell;
    /// <summary>Every instance the scan found. Not what the list is bound to; see
    /// <see cref="_shown"/>.</summary>
    private readonly ObservableCollection<PackRow> _rows = new();

    /// <summary>The rows on screen after the folder, search and sort are applied.</summary>
    /// <remarks>Bound once and updated with <see cref="ListDiff"/>. Replacing ItemsSource instead would
    /// reset the scroll position and selection on every keystroke.</remarks>
    private readonly ObservableCollection<PackRow> _shown = new();
    private readonly ObservableCollection<FolderChipRow> _folderChips = new();
    private readonly Dictionary<Guid, ProgressInfo> _activeProgress = new();
    private PackSortMode _sortMode;
    private PackListLayout _layout;

    // Active folder. null = "All". "team:<teamId>" = team folder. Otherwise user folder name.
    private string? _activeFolder;
    private List<TeamSummary> _teams = new();

    // Reference to the UniformGrid hosting the cards so we can adjust its Columns count.
    private UniformGrid? _packGrid;

    // Card minimum width (used as the divisor for column count). Cards stretch to fill cells.
    private const double CardMinWidth = 220;

    /// <summary>Owns the loading / empty / error / offline answer for this page. Nothing else
    /// writes the count slot or shows an empty panel, so the page never claims zero before it has
    /// looked.</summary>
    private readonly PageState _state;

    public PackListView(MainWindow shell)
    {
        InitializeComponent();
        _shell = shell;
        _sortMode = App.State.Settings.PackSortMode;
        PackList.ItemsSource = _shown;   // bound once; ApplyFilter reconciles it, never replaces it
        FolderStrip.ItemsSource = _folderChips;
        UpdateSortMenuState();
        ApplyLayout(App.State.Settings.PackListLayout, save: false);

        _state = new PageState(PackList, PageStateHost, nameof(PackListView))
            .Copy(PageCopy.Instances)
            .Slots(CountLabel, StatusLabel)
            .DisableWhileBusy(RefreshButton, SortButton);
        // The empty panel offers one action, "create", which is what someone with no instances needs.
        // Import stays in the header.
        // Signed out, an account's instances are not listed, so say where they went. The page is rebuilt
        // on every sign-in and sign-out (MainWindow.OnSignedIn/OnSignedOut), so this is read once.
        var emptyBody = App.State.Api.IsSignedIn
            ? PageCopy.Instances.EmptyBody
            : PageCopy.Instances.EmptyBody + " Instances on a CloudLauncher account show here once you sign in.";
        _state.EmptyCopy(PageCopy.Instances.EmptyTitle, emptyBody,
                         "Create instance", () => _ = CreatePackAsync());
        _state.RetryRequested += () => _ = RefreshAsync();

        // Local, in-memory filter: react to every keystroke rather than the debounced event.
        SearchBox.TextChanged += OnSearchTextChanged;

        // Subscribed on Loaded and removed on Unloaded. The page is reused (IReusablePage), so
        // subscribing in the constructor would leave these dead after the first navigation.
        Loaded += async (_, _) =>
        {
            ProgressHub.ProgressChanged += OnProgressChanged;
            ProgressHub.ProgressCleared += OnProgressCleared;
            PackJobs.Changed += OnPackJobChanged;
            App.State.Instances.StateChanged += OnInstanceStateChanged;
            App.State.ModpackDownload.PackAdded += OnPackAdded;
            App.State.Duplicates.Finished += OnDuplicateFinished;
            Window.GetWindow(this)!.PreviewKeyDown += OnShellKeyDown;

            // Opening or reopening the page: nobody asked, so the refresh stays quiet unless it
            // runs long or the server's list differs.
            await RefreshCoreAsync(quiet: true);
        };
        Unloaded += (_, _) =>
        {
            ProgressHub.ProgressChanged -= OnProgressChanged;
            ProgressHub.ProgressCleared -= OnProgressCleared;
            PackJobs.Changed -= OnPackJobChanged;
            App.State.Instances.StateChanged -= OnInstanceStateChanged;
            App.State.ModpackDownload.PackAdded -= OnPackAdded;
            App.State.Duplicates.Finished -= OnDuplicateFinished;
            if (Window.GetWindow(this) is Window w) w.PreviewKeyDown -= OnShellKeyDown;
        };
    }

    private void OnProgressChanged(ProgressInfo info)
    {
        _activeProgress[info.PackId] = info;
        var row = _rows.FirstOrDefault(r => r.Source.Id == info.PackId);
        row?.SetProgress(info.Fraction, info.Label);
        UpdateBottomProgress();
    }

    private void OnProgressCleared(Guid packId)
    {
        _activeProgress.Remove(packId);
        var row = _rows.FirstOrDefault(r => r.Source.Id == packId);
        row?.ClearProgress();
        UpdateBottomProgress();
    }

    /// <summary>A transfer started, paused, resumed or ended: the card's hover controls follow
    /// it.</summary>
    private void OnPackJobChanged(Guid packId)
    {
        var row = _rows.FirstOrDefault(r => r.Source.Id == packId);
        row?.RefreshTransferState();
        UpdateBottomProgress();
    }

    private void OnInstanceStateChanged(Guid packId)
    {
        var row = _rows.FirstOrDefault(r => r.Source.Id == packId);
        row?.SetInstanceStatus(App.State.Instances.GetStatus(packId));
    }

    private void OnPackAdded(PackSummary pack) => AddOrUpdatePack(pack);

    /// <summary>Called when a pack is created or updated elsewhere (e.g. modpack download).</summary>
    public void AddOrUpdatePack(PackSummary pack)
    {
        App.State.Settings.UnhidePack(pack.Id);

        var existing = _rows.FirstOrDefault(r => r.Source.Id == pack.Id);
        if (existing is not null)
        {
            // Rebind rather than replace, so a running transfer stays on the row and the object on screen
            // stays the one in the list.
            existing.Rebind(pack);
            existing.SetInstanceStatus(App.State.Instances.GetStatus(pack.Id));
            if (_activeProgress.TryGetValue(pack.Id, out var progress))
                existing.SetProgress(progress.Fraction, progress.Label);
        }
        else
        {
            var row = new PackRow(pack);
            row.SetInstanceStatus(App.State.Instances.GetStatus(pack.Id));
            if (_activeProgress.TryGetValue(pack.Id, out var progress))
                row.SetProgress(progress.Fraction, progress.Label);
            _rows.Add(row);
        }

        RefreshFolderChips();
        ApplyFilter();   // re-decides the count and the empty panel from the rows we now have
    }

    private void UpdateBottomProgress()
    {
        if (_activeProgress.Count == 0)
        {
            BottomProgressArea.Visibility = Visibility.Collapsed;
            BottomProgressBar.IsIndeterminate = false;
            BottomProgressBar.Value = 0;
            BottomCurrentProgressArea.Visibility = Visibility.Collapsed;
            BottomCurrentProgressBar.IsIndeterminate = false;
            BottomCurrentProgressBar.Value = 0;
            BottomProgressLabel.Text = "";
            BottomProgressPercent.Text = "";
            BottomCurrentProgressLabel.Text = "";
            BottomCurrentProgressPercent.Text = "";
            return;
        }

        var info = _activeProgress.Values.Last();
        BottomProgressArea.Visibility = Visibility.Visible;
        BottomProgressLabel.Text = info.Label;
        if (info.Fraction < 0)
        {
            BottomProgressBar.IsIndeterminate = true;
            BottomProgressPercent.Text = "";
        }
        else
        {
            BottomProgressBar.IsIndeterminate = false;
            BottomProgressBar.Value = info.Fraction * 100;
            BottomProgressPercent.Text = $"{(int)(info.Fraction * 100)}%";
        }
        UpdateBottomCurrentProgress(info);
    }

    private void UpdateBottomCurrentProgress(ProgressInfo info)
    {
        if (!info.HasCurrentProgress)
        {
            BottomCurrentProgressArea.Visibility = Visibility.Collapsed;
            BottomCurrentProgressBar.IsIndeterminate = false;
            BottomCurrentProgressBar.Value = 0;
            BottomCurrentProgressLabel.Text = "";
            BottomCurrentProgressPercent.Text = "";
            return;
        }

        BottomCurrentProgressArea.Visibility = Visibility.Visible;
        BottomCurrentProgressLabel.Text = string.IsNullOrWhiteSpace(info.CurrentLabel)
            ? "Current stage"
            : info.CurrentLabel;

        if (info.CurrentFraction < 0)
        {
            BottomCurrentProgressBar.IsIndeterminate = true;
            BottomCurrentProgressPercent.Text = "";
        }
        else
        {
            BottomCurrentProgressBar.IsIndeterminate = false;
            BottomCurrentProgressBar.Value = info.CurrentFraction * 100;
            BottomCurrentProgressPercent.Text = $"{(int)(info.CurrentFraction * 100)}%";
        }
    }

    private void OnShellKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsVisible) return;
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            SearchBox.Focus();   // expands, focuses and selects; the control handles all three
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            _ = RefreshAsync();
            e.Handled = true;
        }
    }

    // ── responsive card grid ─────────────────────────────────────────────────

    private void OnPackGridLoaded(object sender, RoutedEventArgs e)
    {
        _packGrid = (UniformGrid)sender;
        UpdateColumns();
    }

    private void OnPackListContainerSizeChanged(object sender, SizeChangedEventArgs e) => UpdateColumns();

    /// <summary>
    /// Choose how many columns of cards fit at the current width, then assign that
    /// to the UniformGrid so rows are always full and cards left-align with no ragged edge.
    /// </summary>
    private void UpdateColumns()
    {
        if (_packGrid is null || _layout != PackListLayout.Cards) return;
        var width = _packGrid.ActualWidth;
        if (width <= 0) width = ActualWidth - 80;        // fallback before measured
        if (width <= 0) return;
        var cols = Math.Max(1, (int)Math.Floor(width / CardMinWidth));
        if (_packGrid.Columns != cols) _packGrid.Columns = cols;
    }

    private void OnLayoutCardsClick(object sender, RoutedEventArgs e) => ApplyLayout(PackListLayout.Cards);

    private void OnLayoutListClick(object sender, RoutedEventArgs e) => ApplyLayout(PackListLayout.List);

    /// <summary>Switches between the card grid and the compact list and remembers the choice.</summary>
    /// <remarks>Only the panel and the item template change; the bound collection stays the same, so
    /// the filter, sort, selection and any running transfer carry over. The old UniformGrid goes away
    /// with the panel, which is why the reference is dropped here and picked up again by
    /// <see cref="OnPackGridLoaded"/> when the cards come back.</remarks>
    private void ApplyLayout(PackListLayout layout, bool save = true)
    {
        var list = layout == PackListLayout.List;
        LayoutCardsButton.IsChecked = !list;
        LayoutListButton.IsChecked = list;

        _layout = layout;
        if (list) _packGrid = null;
        // Assigning the template already in use is a no-op, so clicking the checked button costs nothing.
        PackList.ItemsPanel = (ItemsPanelTemplate)Resources[list ? "PackRowsPanel" : "PackGridPanel"];
        PackList.ItemTemplate = (DataTemplate)Resources[list ? "PackRowTemplate" : "PackCardTemplate"];

        if (!save || App.State.Settings.PackListLayout == layout) return;
        App.State.Settings.PackListLayout = layout;
        App.State.Settings.Save();
    }

    /// <summary>The page's one message line. Accent for success, danger for failure; set by resource
    /// reference so a live theme change recolours it.</summary>
    /// <remarks>Pass a sentence, not raw exception text, and give the exception to
    /// <see cref="AppLog.LogError"/> (or <see cref="PageState.Error"/>, which does both).</remarks>
    private void SetStatus(string message, bool error = false)
    {
        StatusLabel.SetResourceReference(TextBlock.ForegroundProperty, error ? "DangerBrush" : "AccentBrush");
        StatusLabel.Text = message;
    }

    /// <summary>Re-reads the instance list in place (the offline banner's Retry, a page reopen).
    /// Public as this page's <see cref="IRefreshablePage"/> implementation. <see cref="PageState"/>
    /// keeps the cards on screen with a thin refresh bar rather than blanking them.</summary>
    public Task RefreshAsync() => RefreshCoreAsync(quiet: false);

    /// <param name="quiet">True when nobody asked: the page opening, reopening, or retrying on its own
    /// after the server failed. See <see cref="PageState.Begin(string?, bool?, bool, Action?)"/>.</param>
    private async Task RefreshCoreAsync(bool quiet)
    {
        // First showing: paint the list the server gave last time straight away, then reconcile the
        // server's answer onto it, so the first screen doesn't visibly load twice.
        if (!_state.HasData && _rows.Count == 0) PaintRemembered();

        _state.Begin(null, null, quiet);
        try
        {
            var packs = await App.State.Api.ListPacksAsync();

            // Reconciled, not rebuilt: there must be one PackRow per instance for the page's lifetime.
            // OnProgressChanged, OnInstanceStateChanged and the transfer buttons look rows up in _rows and
            // mutate them, so fresh objects would leave progress going to rows no longer on screen.
            // ApplyFilter then narrows these same objects into _shown.
            var fresh = packs
                .Where(p => !App.State.Settings.IsPackHidden(p.Id))
                .Select(p =>
                {
                    var row = new PackRow(p);
                    row.SetInstanceStatus(App.State.Instances.GetStatus(p.Id));
                    if (_activeProgress.TryGetValue(p.Id, out var progress))
                        row.SetProgress(progress.Fraction, progress.Label);
                    return row;
                })
                .ToList();

            ListDiff.Apply(_rows, fresh, r => r.Source.Id, update: (kept, scanned) =>
            {
                kept.Rebind(scanned.Source);
                kept.SetInstanceStatus(App.State.Instances.GetStatus(scanned.Source.Id));
            });

            // Pull teams for team-folder display
            await ReloadTeamsAsync();

            // Guarded: the chip strip is decoration, so a failure building it loses the strip, not the
            // instance list, and is reported in the status line. Unguarded it would reach the catch below
            // and show as "no list to show".
            try
            {
                RefreshFolderChips();
            }
            catch (Exception ex)
            {
                AppLog.LogError("packs.folder-chips", ex);
                _folderChips.Clear();
                SetStatus("Folders could not be shown -- the instance list below is unaffected.", error: true);
            }

            ApplyFilter();   // the only place that writes the count and the empty panel

            // A list served from the cache is still a list: say where it came from and retry shortly.
            // Offline is not an error, since the instances on this PC are still playable.
            if (App.State.Api.PackListStale is { Length: > 0 } why)
            {
                _state.Offline(why);
                ScheduleStaleRetry();
            }
        }
        catch (OfflineException ex)
        {
            _state.Offline(ex.Reason ?? "the server is unreachable", PackListCache.AgeInWords());
            ScheduleStaleRetry();
        }
        catch (Exception ex)
        {
            // This follows the panel's title, so it adds a fact: ListPacksAsync has already tried the
            // server, the saved list and the folders on disk by the time it throws.
            _state.Error("Neither the server nor this PC had a list to show.", ex);
        }
    }

    /// <summary>Draws the instance list and teams this PC last got from the server, before asking
    /// again.</summary>
    /// <remarks><see cref="RefreshCoreAsync"/> reconciles the server's answer onto these rows right
    /// after. The caches are cleared when the signed-in account changes (<c>ApiClient.ClearTokens</c>),
    /// so one account's instances are never shown to another.</remarks>
    private void PaintRemembered()
    {
        var remembered = App.State.Api.PeekPacks();
        if (remembered is not { Count: > 0 }) return;

        var rows = remembered
            .Where(p => !App.State.Settings.IsPackHidden(p.Id))
            .Select(p =>
            {
                var row = new PackRow(p);
                row.SetInstanceStatus(App.State.Instances.GetStatus(p.Id));
                if (_activeProgress.TryGetValue(p.Id, out var progress))
                    row.SetProgress(progress.Fraction, progress.Label);
                return row;
            })
            .ToList();
        if (rows.Count == 0) return;

        ListDiff.Apply(_rows, rows, r => r.Source.Id, update: (kept, scanned) => kept.Rebind(scanned.Source));
        if (TeamListCache.LoadTeams() is { } teams) _teams = teams;
        try { RefreshFolderChips(); }
        catch (Exception ex) { AppLog.LogError("packs.folder-chips", ex); }
        ApplyFilter();
    }

    private DispatcherTimer? _staleRetry;

    /// <summary>One delayed re-fetch after the server failed us. Not a poll: it stops as soon as a
    /// real answer arrives, and every later refresh (navigating back, pulling down) reschedules it.</summary>
    private void ScheduleStaleRetry()
    {
        _staleRetry ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _staleRetry.Tick -= OnStaleRetryTick;
        _staleRetry.Tick += OnStaleRetryTick;
        _staleRetry.Start();
    }

    private async void OnStaleRetryTick(object? sender, EventArgs e)
    {
        _staleRetry?.Stop();
        if (!IsLoaded) return;
        await RefreshCoreAsync(quiet: true);
    }

    /// <summary>Id of the trailing "New folder" chip. Colon-prefixed like the other pages' sentinels,
    /// so it can't collide with a real folder name.</summary>
    private const string NewFolderChipId = ":new";

    /// <summary>Why this folder name cannot be used, or null when it can.</summary>
    /// <remarks><c>team:</c> keys computed team folders, so a user folder with that prefix would be read
    /// as one. A leading colon keys every page's pseudo-chips, including this strip's "New folder"
    /// action. Checked on rename as well as on create.</remarks>
    private static string? ReservedFolderReason(string name) =>
        IsTeamFolderId(name) ? "Names starting with 'team:' are reserved for team folders."
        : name.StartsWith(':') ? "Names starting with ':' are reserved by the launcher."
        : null;

    private void RefreshFolderChips()
    {
        // Built aside and compared first: the strip is rebuilt on every refresh, and replacing chips
        // that did not change regenerates every container in it for nothing.
        var next = new List<FolderChipRow>();

        // The All chip's id is "", never null, while _activeFolder uses null for "no folder". The two
        // are converted here, as in the other folder strips, since Id is non-nullable.
        FolderChipRow chip(string id, string label, int count, string icon, bool isTeam = false)
        {
            return new FolderChipRow
            {
                Id = id,
                Label = label,
                Icon = icon,
                CountLabel = count > 0 ? $"({count})" : "",
                IsActive = string.IsNullOrEmpty(id)
                    ? string.IsNullOrEmpty(_activeFolder)   // "All" is active when no folder is
                    : _activeFolder == id,
                IsTeam = isTeam,
                ToolTipText = isTeam
                    ? $"Instances shared with the {label} team"
                    : string.IsNullOrEmpty(id) ? "Every instance" : $"Instances filed under {label}"
            };
        }

        // All (pseudo-folder)
        next.Add(chip("", "All", _rows.Count, ""));

        // Team folders
        foreach (var t in _teams.OrderBy(t => t.Name))
        {
            var teamId = TeamFolderId(t.Id);
            // Team instances aren't listed via PackSummary, so this only counts instances filed under the
            // team:xxx key by hand.
            var count = PacksInFolder(teamId).Count;
            next.Add(chip(teamId, t.Name, count, "", isTeam: true));
        }

        // User folders
        foreach (var fname in App.State.Settings.PackFolders.Keys.Where(k => !IsTeamFolderId(k)).OrderBy(s => s))
        {
            var count = PacksInFolder(fname).Count;
            next.Add(chip(fname, fname, count, ""));
        }

        // Always last: the chip that creates a folder. A plus rather than a folder glyph, and outlined
        // by the shared FolderChipBorder style, because it is an action in a row of filters.
        next.Add(new FolderChipRow
        {
            Id = NewFolderChipId,
            Label = "New folder",
            Icon = "",
            ToolTipText = "Make a folder to file instances under",
            IsNewAction = true
        });

        if (next.Count == _folderChips.Count && next.Zip(_folderChips).All(p => SameChip(p.First, p.Second)))
            return;
        _folderChips.Clear();
        foreach (var c in next) _folderChips.Add(c);
    }

    private static bool SameChip(FolderChipRow a, FolderChipRow b) =>
        a.Id == b.Id && a.Label == b.Label && a.Icon == b.Icon && a.CountLabel == b.CountLabel
        && a.ToolTipText == b.ToolTipText && a.IsTeam == b.IsTeam && a.IsActive == b.IsActive
        && a.IsNewAction == b.IsNewAction;

    public async Task RefreshTeamFoldersAsync()
    {
        await ReloadTeamsAsync();
        RefreshFolderChips();
        ApplyFilter();
    }

    private async Task ReloadTeamsAsync()
    {
        try { _teams = await App.State.Api.ListTeamsAsync(); }
        catch { _teams = new(); }
    }

    private List<PackSummary> PacksInFolder(string folderId)
    {
        if (string.IsNullOrEmpty(folderId)) return _rows.Select(r => r.Source).ToList();
        if (App.State.Settings.PackFolders.TryGetValue(folderId, out var ids))
            return _rows.Where(r => ids.Contains(r.Source.Id)).Select(r => r.Source).ToList();
        return new();
    }

    private void ApplyFilter()
    {
        var filter = SearchBox.Text.Trim().ToLowerInvariant();

        IEnumerable<PackRow> source = _rows;

        // Folder filter
        if (!string.IsNullOrEmpty(_activeFolder))
        {
            if (App.State.Settings.PackFolders.TryGetValue(_activeFolder, out var ids))
                source = source.Where(r => ids.Contains(r.Source.Id));
            else source = Array.Empty<PackRow>();
        }

        if (!string.IsNullOrEmpty(filter))
            source = source.Where(r => r.Name.ToLowerInvariant().Contains(filter)
                                    || (r.Summary ?? "").ToLowerInvariant().Contains(filter)
                                    || (r.Description ?? "").ToLowerInvariant().Contains(filter));

        var filtered = SortRows(source).ToList();

        // Keyed by pack id, with update: a refresh builds new PackRow objects, so without it the row on
        // screen would keep showing the previous scan's values. Rebinding also keeps its live transfer
        // progress.
        ListDiff.Apply(_shown, filtered, r => r.Source.Id,
                       update: (onScreen, fresh) => onScreen.Rebind(fresh.Source));

        // Which "nothing here" to show depends on what is hiding the rows.
        if (_rows.Count > 0 && filtered.Count == 0)
        {
            if (!string.IsNullOrEmpty(filter))
                _state.EmptyFiltered();
            else if (!string.IsNullOrEmpty(_activeFolder))
                _state.EmptyNext($"Nothing in {ActiveFolderLabel()} yet",
                    "Right-click an instance on the All page and pick 'Add to folder' to link it here.",
                    glyph: "");
        }

        // Content(0) is the only door into the empty panel, so it can only ever mean "we looked".
        _state.Content(filtered.Count,
            countText: _rows.Count == filtered.Count
                ? $"{filtered.Count:N0} instance(s)"
                : $"{filtered.Count:N0} of {_rows.Count:N0} instance(s)");
    }

    /// <summary>The active folder's display name. A team chip shows the team's name, not its id.</summary>
    private string ActiveFolderLabel() =>
        IsTeamFolderId(_activeFolder)
            ? _teams.FirstOrDefault(t => TeamFolderId(t.Id).Equals(_activeFolder, StringComparison.OrdinalIgnoreCase))?.Name ?? "Team"
            : _activeFolder ?? "";

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void OnCreatePack(object sender, RoutedEventArgs e) => await CreatePackAsync();

    /// <summary>Shared by the header button and the empty panel's action, which offer the same move.</summary>
    private async Task CreatePackAsync()
    {
        var dlg = new CreatePackDialog { Owner = _shell };
        if (dlg.ShowDialog() == true)
        {
            // If a folder is active, link the new instance into it for the creator.
            if (!string.IsNullOrEmpty(_activeFolder) && dlg.CreatedPack is { } created)
            {
                App.State.Settings.AddPackToFolder(_activeFolder, created.Id);
                App.State.Settings.Save();
            }

            // Apply the shared defaults to a new instance so the user doesn't have to add their resource
            // pack and shader by hand. Fire-and-forget: a failure here shouldn't fail creation, and the
            // launch-time reconcile will catch up.
            if (dlg.CreatedPack is { } fresh)
                _ = ApplyDefaultsToNewInstanceAsync(fresh);

            await RefreshAsync();
        }
    }

    /// <summary>Puts the user's shared defaults into an instance that has just been created.</summary>
    /// <remarks>Includes activation: a default resource pack should be switched on in a new instance,
    /// not just sit in its folder. A no-op for anyone who has set no defaults.</remarks>
    private async Task ApplyDefaultsToNewInstanceAsync(PackSummary pack)
    {
        try
        {
            var result = await App.State.ContentDefaults.ReconcileAsync(pack);
            // Silent when there was nothing to do, as for anyone without defaults.
            if (result.DidAnything) SetStatus($"{pack.Name}: {result.Summary()}");
        }
        catch (Exception ex)
        {
            AppLog.LogError("content-defaults", ex);
        }
    }

    private void OnImportModpack(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select modpack file",
            Filter = "Modpack files|*.mrpack;*.zip|Modrinth pack (*.mrpack)|*.mrpack|CurseForge pack (*.zip)|*.zip"
        };
        if (dlg.ShowDialog(_shell) != true) return;
        var name = Path.GetFileNameWithoutExtension(dlg.FileName);
        _ = App.State.ModpackDownload.StartLocalFileImportAsync(dlg.FileName, name);
    }

    private void OnDownloadPack(object sender, RoutedEventArgs e) => _shell.OpenPackBrowser();

    /// <summary>Opens the folder every instance lives in.</summary>
    /// <remarks>Creates it first so opening can't fail on a packs root nobody has written to yet, like
    /// the card's "Open game folder".</remarks>
    private void OnOpenInstancesFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = App.State.Settings.PacksRoot;
            Directory.CreateDirectory(dir);
            if (!SafeLaunch.OpenFolder(dir))
                SetStatus("That folder could not be opened. The full error is in the launcher log.", error: true);
        }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(PackListView), ex);
            SetStatus("That folder could not be opened. The full error is in the launcher log.", error: true);
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    // ── sorting ──────────────────────────────────────────────────────────────

    private IEnumerable<PackRow> SortRows(IEnumerable<PackRow> rows)
    {
        IOrderedEnumerable<PackRow> ordered = rows.OrderByDescending(r => r.IsPinned);
        ordered = _sortMode switch
        {
            PackSortMode.Version => ordered
                .ThenByDescending(r => VersionSortValue(r.Source.MinecraftVersion))
                .ThenByDescending(r => r.Source.MinecraftVersion ?? ""),
            PackSortMode.PlayCount => ordered.ThenByDescending(r => r.PlayCount),
            PackSortMode.TimePlayed => ordered.ThenByDescending(r => r.TotalPlayTimeSeconds),
            _ => ordered
                .ThenByDescending(r => r.LastPlayedAt ?? DateTimeOffset.MinValue)
                .ThenByDescending(r => r.Source.UpdatedAt)
        };

        return ordered.ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase);
    }

    private static long VersionSortValue(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return 0;
        var parts = version.Split('.', '-', '_');
        long value = 0;
        for (var i = 0; i < 4; i++)
        {
            value *= 1000;
            if (i >= parts.Length) continue;
            if (!int.TryParse(parts[i], out var part)) break;
            value += Math.Clamp(part, 0, 999);
        }
        return value;
    }

    private void OnSortButtonClick(object sender, RoutedEventArgs e)
    {
        if (SortButton.ContextMenu is null) return;
        UpdateSortMenuState();
        SortButton.ContextMenu.PlacementTarget = SortButton;
        SortButton.ContextMenu.Placement = PlacementMode.Bottom;
        SortButton.ContextMenu.IsOpen = true;
    }

    private void OnSortMenuOpened(object sender, RoutedEventArgs e) => UpdateSortMenuState();

    private void OnSortMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag }) return;
        if (!Enum.TryParse<PackSortMode>(tag, out var mode)) return;
        _sortMode = mode;
        App.State.Settings.PackSortMode = mode;
        App.State.Settings.Save();
        UpdateSortMenuState();
        ApplyFilter();
    }

    private void UpdateSortMenuState()
    {
        SortLastPlayedMenuItem.IsChecked = _sortMode == PackSortMode.LastPlayed;
        SortVersionMenuItem.IsChecked = _sortMode == PackSortMode.Version;
        SortPlayCountMenuItem.IsChecked = _sortMode == PackSortMode.PlayCount;
        SortTimePlayedMenuItem.IsChecked = _sortMode == PackSortMode.TimePlayed;
        SortButton.ToolTip = $"Sort instances: {SortModeLabel(_sortMode)}";
    }

    private static string SortModeLabel(PackSortMode mode) => mode switch
    {
        PackSortMode.Version => "Version",
        PackSortMode.PlayCount => "Times played",
        PackSortMode.TimePlayed => "Time played",
        _ => "Last played"
    };

    // ── folder strip ─────────────────────────────────────────────────────────

    /// <summary>Makes a folder and selects it. Reached from the strip's trailing chip.</summary>
    private void CreateFolder()
    {
        var dlg = new SimpleInputDialog("Create folder", "Folder name", "") { Owner = _shell };
        if (dlg.ShowDialog() != true) return;
        var name = (dlg.Result ?? "").Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (ReservedFolderReason(name) is { } why)
        {
            SetStatus(why, error: true);
            return;
        }
        App.State.Settings.CreatePackFolder(name);
        App.State.Settings.Save();
        _activeFolder = name;
        RefreshFolderChips();
        ApplyFilter();
    }

    private void OnFolderChipClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FolderChipRow chip }) return;

        // The trailing chip is an action, not a filter: it must never become the active folder.
        if (chip.IsNewAction) { CreateFolder(); return; }

        _activeFolder = string.IsNullOrEmpty(chip.Id) ? null : chip.Id;
        RefreshFolderChips();
        ApplyFilter();
    }

    private void OnFolderChipRightClick(object sender, MouseButtonEventArgs e)
    {
        // No context menu on the two chips that are not folders: All and the trailing New-folder chip.
        // Rename/Delete have their own guards, but a menu that refuses itself is worse than none.
        if (sender is FrameworkElement el && el.DataContext is FolderChipRow chip
            && (string.IsNullOrEmpty(chip.Id) || chip.IsNewAction))
        {
            e.Handled = true;
        }
    }

    private void OnFolderRename(object sender, RoutedEventArgs e)
    {
        // The "New folder" chip has a sentinel id (":new") that PackFolders never holds, so it is
        // excluded along with All and team chips.
        if (FolderFromMenu(sender) is not { } chip || string.IsNullOrEmpty(chip.Id)
            || chip.IsNewAction || chip.IsTeam) return;
        var dlg = new SimpleInputDialog("Rename folder", "New name", chip.Id) { Owner = _shell };
        if (dlg.ShowDialog() != true) return;
        var newName = (dlg.Result ?? "").Trim();
        if (string.IsNullOrEmpty(newName) || newName == chip.Id) return;
        if (ReservedFolderReason(newName) is { } why)
        {
            SetStatus(why, error: true);
            return;
        }
        if (App.State.Settings.PackFolders.ContainsKey(newName))
        {
            SetStatus("A folder with that name already exists.", error: true);
            return;
        }
        var members = App.State.Settings.PackFolders[chip.Id];
        App.State.Settings.PackFolders.Remove(chip.Id);
        App.State.Settings.PackFolders[newName] = members;
        if (_activeFolder == chip.Id) _activeFolder = newName;
        App.State.Settings.Save();
        RefreshFolderChips();
        ApplyFilter();
    }

    private async void OnFolderDelete(object sender, RoutedEventArgs e)
    {
        // The "New folder" chip has a sentinel id (":new") that PackFolders never holds, so it is
        // excluded along with All and team chips.
        if (FolderFromMenu(sender) is not { } chip || string.IsNullOrEmpty(chip.Id)
            || chip.IsNewAction || chip.IsTeam) return;
        var confirmed = await AppDialog.ConfirmAsync(_shell, "Delete folder",
            $"Delete the folder '{chip.Id}'?\n\nThe instances themselves stay - they just stop being linked here.",
            "Delete folder", "Cancel", danger: true);
        if (!confirmed) return;
        App.State.Settings.DeletePackFolder(chip.Id);
        App.State.Settings.Save();
        if (_activeFolder == chip.Id) _activeFolder = null;
        RefreshFolderChips();
        ApplyFilter();
    }

    private FolderChipRow? FolderFromMenu(object sender)
    {
        if (sender is MenuItem mi)
        {
            var parent = mi.Parent;
            while (parent is MenuItem p) parent = p.Parent;
            if (parent is ContextMenu cm && cm.PlacementTarget is FrameworkElement target
                && target.DataContext is FolderChipRow row) return row;
        }
        return null;
    }

    // ── card click / play / update ───────────────────────────────────────────

    /// <summary>One click opens the instance; two go straight to its Modpack Management.</summary>
    /// <remarks>The card root is a Border, which has no <c>MouseDoubleClick</c>, so this uses the click
    /// count. The second click must return early: calling <c>OpenPackDetail</c> again would reopen the
    /// side panel and replace the hub just pushed. The first click is not suppressed, so Back from the
    /// hub lands on the instance page.</remarks>
    private void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        // A click on Play, Kill, Pin or a transfer control belongs to that control and must not open
        // anything, even on a double-click.
        if (e.OriginalSource is FrameworkElement el && IsInsideButton(el)) return;
        if (sender is not FrameworkElement fe || fe.DataContext is not PackRow row) return;

        if (e.ClickCount >= 2)
        {
            e.Handled = true;
            _ = OpenModHubAsync(row);
            return;
        }

        _shell.OpenPackDetail(row.Source.Id, row.Source.Name);
    }

    /// <summary>Opens one instance's Modpack Management.</summary>
    /// <remarks>The hub needs a full <c>PackDetail</c>. The first click already opened the detail page,
    /// which fetched and cached it, so the cache is tried first to keep the double-click quick; the fetch
    /// is the fallback.</remarks>
    private async Task OpenModHubAsync(PackRow row)
    {
        try
        {
            var detail = PackDetailCache.Load(row.Source.Id)
                         ?? await App.State.Api.GetPackAsync(row.Source.Id);
            _shell.OpenModManagementForPack(detail);
        }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(PackListView), ex);
            SetStatus("Modpack management could not be opened for that instance. "
                    + "The full error is in the launcher log.", error: true);
        }
    }

    private void OnCardRightClick(object sender, MouseButtonEventArgs e) { /* let ContextMenu open */ }

    private static bool IsInsideButton(DependencyObject? d)
    {
        while (d is not null)
        {
            if (d is Button) return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    private async void OnCardPlay(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Guid id })
            await QuickPlayAsync(id);
    }

    private void OnCardKill(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id }) return;
        try
        {
            App.State.Instances.Stop(id);
            SetStatus("Stopping Minecraft instance...");
        }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(PackListView), ex);
            SetStatus("That instance could not be stopped. The full error is in the launcher log.", error: true);
        }
    }

    private async void OnCardUpdate(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Guid id })
            await QuickUpdateAsync(id);
    }

    /// <summary>The pause / resume control that the card's progress bar reveals on hover.</summary>
    private void OnCardPauseJob(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id }) return;
        var job = PackJobs.For(id);
        if (job is null) return;
        // Stop was already requested; pausing now would do nothing, so don't claim "Paused".
        if (job.IsStopping) { SetStatus($"The {job.KindLabel} is already stopping..."); return; }

        if (job.IsPaused)
        {
            job.Resume();
            SetStatus($"Resumed the {job.KindLabel}.");
        }
        else
        {
            job.Pause();
            SetStatus($"Paused - the {job.KindLabel} is holding where it is.");
        }
    }

    private void OnCardStopJob(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id }) return;
        var job = PackJobs.For(id);
        if (job is null) return;
        SetStatus(job.Kind == PackJobKind.Copy
            ? "Stopping the copy - the unfinished instance is being removed..."
            : $"Stopping the {job.KindLabel} - the files it downloaded are being removed...");
        job.Stop();
    }

    private async Task QuickPlayAsync(Guid id)
    {
        SetStatus("Preparing to launch...");
        try
        {
            var pack = await App.State.Api.GetPackAsync(id);
            if (pack.IsEmpty)
            {
                SetStatus("This instance is empty - open it to configure it first.", error: true);
                return;
            }
            if (App.State.MinecraftAccounts.Current is null)
            {
                SetStatus("Sign in to a Minecraft account first.", error: true);
                _shell.OpenMcAccount();
                return;
            }
            App.State.Packs.EnsurePackFolder(pack.Id, pack.Name, pack.IsShared);
            var proc = await App.State.Launcher.LaunchTrackedAsync(pack, new Progress<string>(_ => { }));
            _shell.OpenMinecraftHost(pack, proc);
            // Re-filter first: ApplyFilter clears the status line on its way past, so the message is
            // written after it.
            RefreshUsageState(id);
            SetStatus($"Minecraft started (PID {proc.Id}).");
        }
        catch (OperationCanceledException) { SetStatus("Launch cancelled."); }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(PackListView), ex);
            SetStatus("That instance could not be launched. The full error is in the launcher log.", error: true);
        }
    }

    private async Task QuickUpdateAsync(Guid id)
    {
        if (PackJobs.For(id) is { } running)
        {
            // An export has no bar on the card to hover (it reports in its own card), so it gets its own words.
            SetStatus(running.Kind == PackJobKind.Export
                ? "An export is reading that instance. Update it once that has finished."
                : "Already transferring - hover the bar to pause or stop it.");
            return;
        }

        SetStatus("Updating from server...");
        // Registered as a job so the bar this update draws can be paused and stopped from the
        // same hover controls a modpack download gets.
        var job = PackJobs.Start(id, PackJobKind.Sync);
        try
        {
            var pack = await App.State.Api.GetPackAsync(id, job.Token);
            if (!pack.IsShared) { SetStatus("That instance is not hosted on the server, so there is nothing to update from.", error: true); return; }
            App.State.Packs.EnsurePackFolder(pack.Id, pack.Name, pack.IsShared);
            await App.State.Packs.DownloadSharedAsync(pack.Id, new Progress<string>(_ => { }), job.Token, job);
            SetStatus("Updated.");
        }
        catch (OperationCanceledException)
        {
            var removed = job.RollbackCreatedFiles();
            SetStatus(removed > 0
                ? $"Update stopped - the {removed} file(s) it had downloaded were removed."
                : "Update stopped.");
            ProgressHub.Clear(id);
        }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(PackListView), ex);
            SetStatus("The update did not finish. The full error is in the launcher log.", error: true);
        }
        finally { PackJobs.Finish(job); }
    }

    // ── context-menu actions ─────────────────────────────────────────────────

    private PackRow? RowFromMenuSender(object sender)
    {
        if (sender is FrameworkElement el && el.DataContext is PackRow row) return row;
        if (sender is MenuItem mi)
        {
            var parent = mi.Parent;
            while (parent is MenuItem p) parent = p.Parent;
            if (parent is ContextMenu cm && cm.PlacementTarget is FrameworkElement target
                && target.DataContext is PackRow r) return r;
        }
        return null;
    }

    private async void OnCtxPlay(object sender, RoutedEventArgs e)
    {
        if (RowFromMenuSender(sender) is { } row) await QuickPlayAsync(row.Source.Id);
    }

    private void OnPinToggle(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Guid id } && _rows.FirstOrDefault(r => r.Source.Id == id) is { } row)
        {
            TogglePinned(row);
            e.Handled = true;
        }
    }

    private void OnCtxTogglePin(object sender, RoutedEventArgs e)
    {
        if (RowFromMenuSender(sender) is { } row) TogglePinned(row);
    }

    private void TogglePinned(PackRow row)
    {
        App.State.Settings.SetPackPinned(row.Source.Id, !row.IsPinned);
        App.State.Settings.Save();
        row.RefreshUsage();
        ApplyFilter();
    }

    private void RefreshUsageState(Guid packId)
    {
        _rows.FirstOrDefault(r => r.Source.Id == packId)?.RefreshUsage();
        ApplyFilter();
    }

    /// <summary>Re-read a single card's cover image from disk (after it changed elsewhere).</summary>
    public void RefreshPackCover(Guid packId)
        => _rows.FirstOrDefault(r => r.Source.Id == packId)?.RefreshCover();

    private void OnCtxOpen(object sender, RoutedEventArgs e)
    {
        if (RowFromMenuSender(sender) is { } row) _shell.OpenPackDetail(row.Source.Id, row.Source.Name);
    }

    private async void OnCtxRename(object sender, RoutedEventArgs e)
    {
        if (RowFromMenuSender(sender) is not { } row) return;
        if (App.State.Settings.UserId != row.Source.OwnerId)
        {
            SetStatus("Only the instance owner can rename it.", error: true);
            return;
        }

        var dlg = new SimpleInputDialog("Rename instance", "New name", row.Source.Name) { Owner = _shell };
        if (dlg.ShowDialog() != true) return;

        var name = (dlg.Result ?? "").Trim();
        if (string.IsNullOrEmpty(name) || string.Equals(name, row.Source.Name, StringComparison.Ordinal))
            return;

        SetStatus("Renaming instance...");
        try
        {
            var updated = await App.State.Api.UpdatePackAsync(
                row.Source.Id,
                new UpdatePackRequest(name, null, null, null, null, null, null, null));
            App.State.Packs.TryRenameFolder(row.Source.Id, name); // keep the on-disk folder in step
            AddOrUpdatePack(updated);
            SetStatus("Instance renamed.");
        }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(PackListView), ex);
            SetStatus("That instance could not be renamed. The full error is in the launcher log.", error: true);
        }
    }

    private void OnCtxChangeImage(object sender, RoutedEventArgs e)
    {
        if (RowFromMenuSender(sender) is not { } row) return;
        if (App.State.Settings.UserId != row.Source.OwnerId)
        {
            SetStatus("Only the instance owner can change its image.", error: true);
            return;
        }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose instance image",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp"
                     + "|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(_shell) != true) return;

        try
        {
            App.State.PackAssets.SaveIconFromFile(row.Source.Id, dlg.FileName);
            row.RefreshCover();
            _shell.RefreshPackDetailHero(row.Source.Id);
            SetStatus("Image updated.");
        }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(PackListView), ex);
            SetStatus("That image could not be used. The full error is in the launcher log.", error: true);
        }
    }

    private void OnCtxOpenGameFolder(object sender, RoutedEventArgs e)
    {
        if (RowFromMenuSender(sender) is { } row)
        {
            var dir = App.State.Packs.GameDir(row.Source.Id, row.Source.Name);
            Directory.CreateDirectory(dir);
            if (!SafeLaunch.OpenFolder(dir))
                SetStatus("That folder could not be opened. The full error is in the launcher log.", error: true);
        }
    }

    /// <summary>The card menu's route to Modpack Management, matching the double-click.</summary>
    private void OnCtxPackManagement(object sender, RoutedEventArgs e)
    {
        if (RowFromMenuSender(sender) is { } row) _ = OpenModHubAsync(row);
    }

    private async void OnCtxBrowseMods(object sender, RoutedEventArgs e)
    {
        if (RowFromMenuSender(sender) is not { } row) return;
        try
        {
            var detail = await App.State.Api.GetPackAsync(row.Source.Id);
            _shell.OpenModExplorerForPack(detail);
        }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(PackListView), ex);
            SetStatus("The mod browser could not be opened for that instance. The full error is in the launcher log.", error: true);
        }
    }

    /// <summary>Copies the instance into a new one (see <see cref="InstanceDuplicateService"/>). The
    /// new card appears at once and fills in as the files are copied.</summary>
    private async void OnCtxDuplicate(object sender, RoutedEventArgs e)
    {
        if (RowFromMenuSender(sender) is not { } row) return;
        var id = row.Source.Id;
        if (App.State.Instances.GetStatus(id) != MinecraftInstanceStatus.Idle)
        {
            SetStatus("Minecraft is running from that instance. Close it first, so its worlds and configs are copied as they are on disk.", error: true);
            return;
        }
        if (PackJobs.For(id) is { } running)
        {
            SetStatus($"That instance is busy ({running.KindLabel}). Duplicate it once that has finished.", error: true);
            return;
        }

        var dlg = new DuplicateInstanceDialog(row.Name, row.Source.IsShared) { Owner = _shell };
        if (dlg.ShowDialog() != true || dlg.Result is not { } options) return;

        SetStatus($"Duplicating '{row.Name}'...");
        try
        {
            var detail = await App.State.Api.GetPackAsync(id);
            var copy = await App.State.Duplicates.StartAsync(detail, options);
            AddOrUpdatePack(copy);
            SetStatus($"Copying '{row.Name}' into '{copy.Name}'. Hover its bar to pause or stop.");
        }
        catch (InvalidOperationException ex)
        {
            SetStatus(ex.Message, error: true);
        }
        catch (ApiException ex)
        {
            AppLog.LogError(nameof(PackListView), ex);
            SetStatus($"'{row.Name}' could not be duplicated: {ApiClient.ServerSentence(ex) ?? ex.Message}", error: true);
        }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(PackListView), ex);
            SetStatus($"'{row.Name}' could not be duplicated. The full error is in the launcher log.", error: true);
        }
    }

    /// <summary>A copy finished, or was stopped and taken back.</summary>
    private async void OnDuplicateFinished(Guid packId, bool ok)
    {
        if (!IsLoaded) return;
        var name = _rows.FirstOrDefault(r => r.Source.Id == packId)?.Name ?? "The copy";
        await RefreshAsync();
        SetStatus(ok ? $"'{name}' is ready." : "The copy did not finish, so the unfinished instance was removed. The launcher log says why.", error: !ok);
    }

    /// <summary>The card menu's route to <see cref="ExportPackDialog"/>, the same card the instance
    /// page's Export button opens.</summary>
    /// <remarks>Fetches the full record rather than trusting the cached one: both formats are written
    /// around the Minecraft and loader versions, which may have changed since.
    /// <see cref="ApiClient.GetPackAsync"/> still falls back to the cache when the server can't be
    /// reached.</remarks>
    private async void OnCtxExport(object sender, RoutedEventArgs e)
    {
        if (RowFromMenuSender(sender) is not { } row) return;
        if (row.Source.IsEmpty)
        {
            SetStatus("That instance is empty, so there is nothing to export yet.", error: true);
            return;
        }
        try
        {
            var detail = await App.State.Api.GetPackAsync(row.Source.Id);
            await ExportPackDialog.ShowAsync(_shell, detail);
        }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(PackListView), ex);
            SetStatus("That instance could not be exported. The full error is in the launcher log.", error: true);
        }
    }

    private void OnCtxCopyId(object sender, RoutedEventArgs e)
    {
        if (RowFromMenuSender(sender) is { } row)
        {
            var copied = ClipboardHelper.TrySetText(row.Source.Id.ToString());
            SetStatus(copied
                ? "Instance ID copied."
                : "Couldn't copy - the clipboard is in use by another program.",
                error: !copied);
        }
    }

    /// <summary>Moves an instance made without an account onto the signed-in one (see
    /// <see cref="ApiClient.AddLocalPackToAccountAsync"/>). Its files stay where they are.</summary>
    private async void OnCtxAddToAccount(object sender, RoutedEventArgs e)
    {
        if (RowFromMenuSender(sender) is not { } row) return;
        var ok = await AppDialog.ConfirmAsync(_shell, "Add to my account",
            $"Add '{row.Name}' to your CloudLauncher account ({App.State.Settings.Username})?\n\n"
            + "It is listed on every PC you sign in on, and can be shared from its Share tab. Its files stay "
            + "on this PC until you choose to host them.",
            "Add to my account", "Cancel");
        if (!ok) return;

        SetStatus($"Adding '{row.Name}' to your account...");
        try
        {
            var added = await App.State.Api.AddLocalPackToAccountAsync(row.Source.Id);
            await RefreshAsync();
            SetStatus($"'{added.Name}' is on your account now.");
        }
        catch (ApiException ex)
        {
            AppLog.LogError(nameof(PackListView), ex);
            SetStatus($"'{row.Name}' could not be added to your account: {ApiClient.ServerSentence(ex) ?? ex.Message}", error: true);
        }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(PackListView), ex);
            SetStatus($"'{row.Name}' could not be added to your account. The full error is in the launcher log.", error: true);
        }
    }

    private async void OnCtxDelete(object sender, RoutedEventArgs e)
    {
        if (RowFromMenuSender(sender) is not { } row) return;
        var packId = row.Source.Id;
        var isLocal = App.State.Api.IsLocalPack(packId);
        var isOwner = App.State.OwnsPack(packId, row.Source.OwnerId);

        // Minecraft holds the instance's files open, and deleting under it would pull files out of a
        // running game. Stopping it is a decision for the player, not a side effect of a delete.
        if (App.State.Instances.GetStatus(packId) == MinecraftInstanceStatus.Running)
        {
            SetStatus("Minecraft is running from that instance. Close the game first, then delete it.", error: true);
            return;
        }

        var confirm = new ConfirmDeleteDialog(row.Name, isOwner, isLocal) { Owner = _shell };
        if (confirm.ShowDialog() != true) return;

        // Stop anything still downloading into it (a modpack install, an update, a launch fetching the
        // game) before it goes, and wait for the stop to roll its files back. Otherwise the transfer
        // keeps writing into a folder that is no longer listed.
        // A copy that is stopped takes its unfinished instance back by itself, which is the delete.
        if (PackJobs.For(packId) is { Kind: PackJobKind.Copy })
        {
            SetStatus($"Stopping the copy into '{row.Name}'...");
            if (!await PackJobs.StopAndWaitAsync(packId, TimeSpan.FromSeconds(30)))
            {
                SetStatus($"The copy into '{row.Name}' has not stopped yet. Try again in a moment.", error: true);
                return;
            }
            _shell.CloseSidePanelForPack(packId);
            await RefreshAsync();
            SetStatus($"Stopped the copy; '{row.Name}' was removed.");
            return;
        }

        if (PackJobs.IsRunning(packId) || App.State.Instances.IsBusy(packId))
        {
            SetStatus($"Stopping what '{row.Name}' was downloading...");
            App.State.Instances.Stop(packId);
            if (!await PackJobs.StopAndWaitAsync(packId, TimeSpan.FromSeconds(30)))
            {
                SetStatus($"The download into '{row.Name}' has not stopped yet, so it was not deleted. Try again in a moment.", error: true);
                return;
            }
            ProgressHub.Clear(packId);
        }

        // A local delete takes the folder with it, so the instance's own page (file watchers, the
        // description view) is closed first and given a turn to let go of its files.
        if (isLocal)
        {
            _shell.CloseSidePanelForPack(packId);
            await Dispatcher.Yield(DispatcherPriority.Background);
        }

        // Set when the local hide worked but the server was not told: a partial success with its own
        // message.
        var syncWarning = false;
        try
        {
            if (isOwner)
            {
                await App.State.Api.DeletePackAsync(packId);
                App.State.Settings.UnhidePack(packId);
            }
            else
            {
                App.State.Settings.HidePack(packId);
                try
                {
                    await App.State.Api.UnsubscribePackAsync(packId);
                }
                catch (Exception ex)
                {
                    AppLog.LogError(nameof(PackListView), ex);
                    syncWarning = true;
                }
            }

            // Only once it is really gone: a delete that failed keeps its place in the user's folders.
            foreach (var f in App.State.Settings.PackFolders.Values) f.Remove(packId);
            App.State.Settings.Save();
            _shell.CloseSidePanelForPack(packId);
            await RefreshAsync();

            SetStatus(syncWarning
                ? "Removed from your list, but the server was not told - it may come back on the next refresh."
                : isLocal ? "Instance deleted. Its folder is in the Recycle Bin."
                : isOwner ? "Instance deleted." : "Instance removed.",
                error: syncWarning);
        }
        catch (OperationCanceledException)
        {
            // Windows asked whether to delete a folder too big for the Recycle Bin, and the answer was no.
            SetStatus("The instance was not deleted.");
        }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(PackListView), ex);
            SetStatus(ex is System.IO.IOException && isLocal ? ex.Message
                : isOwner ? "That instance could not be deleted. The full error is in the launcher log."
                : "That instance could not be removed. The full error is in the launcher log.",
                error: true);
        }
    }

    // ── add-to-folder submenu ───────────────────────────────────────────────

    private void OnCtxAddToFolderOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi) return;
        if (RowFromMenuSender(sender) is not { } row) return;

        mi.Items.Clear();
        // User folders
        var userFolders = App.State.Settings.PackFolders.Keys.Where(k => !IsTeamFolderId(k)).OrderBy(s => s);
        var hasAny = false;
        foreach (var folder in userFolders)
        {
            hasAny = true;
            var isIn = App.State.Settings.PackFolders[folder].Contains(row.Source.Id);
            var item = new MenuItem
            {
                Header = folder,
                IsCheckable = true,
                IsChecked = isIn
            };
            var packId = row.Source.Id;
            var fname = folder;
            item.Click += (_, _) =>
            {
                if (item.IsChecked) App.State.Settings.AddPackToFolder(fname, packId);
                else App.State.Settings.RemovePackFromFolder(fname, packId);
                App.State.Settings.Save();
                RefreshFolderChips();
                ApplyFilter();
            };
            mi.Items.Add(item);
        }
        // Team folders
        if (_teams.Count > 0)
        {
            if (hasAny) mi.Items.Add(new Separator());
            foreach (var team in _teams.OrderBy(t => t.Name))
            {
                var teamKey = TeamFolderId(team.Id);
                var inIt = App.State.Settings.PackFolders.TryGetValue(teamKey, out var l) && l.Contains(row.Source.Id);
                var item = new MenuItem
                {
                    Header = team.Name + " (team)",
                    IsCheckable = true,
                    IsChecked = inIt
                };
                var packId = row.Source.Id;
                var key = teamKey;
                item.Click += (_, _) =>
                {
                    if (item.IsChecked) App.State.Settings.AddPackToFolder(key, packId);
                    else App.State.Settings.RemovePackFromFolder(key, packId);
                    App.State.Settings.Save();
                    RefreshFolderChips();
                    ApplyFilter();
                };
                mi.Items.Add(item);
            }
            hasAny = true;
        }
        if (!hasAny)
        {
            var none = new MenuItem { Header = "(no folders - create one first)", IsEnabled = false };
            mi.Items.Add(none);
        }
        mi.Items.Add(new Separator());
        var create = new MenuItem { Header = "Create new folder..." };
        create.Click += (_, _) =>
        {
            var dlg = new SimpleInputDialog("Create folder", "Folder name", "") { Owner = _shell };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Result))
            {
                var fname = dlg.Result!.Trim();
                // Same validator as the chip strip's create path, so this menu can't make a folder
                // the strip would refuse (such as ":new").
                if (ReservedFolderReason(fname) is { } why)
                {
                    SetStatus(why, error: true);
                    return;
                }
                App.State.Settings.CreatePackFolder(fname);
                App.State.Settings.AddPackToFolder(fname, row.Source.Id);
                App.State.Settings.Save();
                RefreshFolderChips();
                ApplyFilter();
            }
        };
        mi.Items.Add(create);
    }

    private static string TeamFolderId(Guid teamId) => $"team:{teamId:N}";

    private static bool IsTeamFolderId(string? folderId) =>
        !string.IsNullOrEmpty(folderId)
        && folderId.StartsWith("team:", StringComparison.OrdinalIgnoreCase);
}

public sealed class PackRow : INotifyPropertyChanged
{
    public PackRow(PackSummary src) { Source = src; }

    public PackSummary Source { get; private set; }

    /// <summary>Points this row at a freshly fetched summary of the same instance.</summary>
    /// <remarks>Every display property is computed from <see cref="Source"/>, so one null-named change
    /// notification re-reads them all (as <c>PackMod.Refresh</c> does). Updating the row in place rather
    /// than replacing it keeps the scroll position, the selection and any running transfer.</remarks>
    public void Rebind(PackSummary src)
    {
        if (ReferenceEquals(Source, src)) return;
        Source = src;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
    public string Name => Source.Name;
    public string? Summary => Source.Summary;
    public string? Description => Source.Description;
    /// <summary>True when the instance is on this PC only, not on a CloudLauncher account.</summary>
    public bool IsLocal => App.State.Api.IsLocalPack(Source.Id);
    public string OwnerLabel => $"{OwnerShortLabel} · updated {Source.UpdatedAt.LocalDateTime:g}";
    public string OwnerShortLabel => IsLocal ? "on this PC" : $"by {Source.OwnerUsername}";

    /// <summary>The card menu's "Add to my account": a local instance, with someone signed in.</summary>
    public Visibility AddToAccountVisibility =>
        IsLocal && App.State.Api.IsSignedIn ? Visibility.Visible : Visibility.Collapsed;
    public bool IsPlayable => !Source.IsEmpty;
    public bool IsShared => Source.IsShared;
    public bool IsPinned => Usage.IsPinned;
    public int PlayCount => Usage.PlayCount;
    public long TotalPlayTimeSeconds => Usage.TotalPlayTimeSeconds;
    public DateTimeOffset? LastPlayedAt => Usage.LastPlayedAt;
    public string PinToolTip => IsPinned ? "Unpin instance" : "Pin instance";
    public string PinMenuHeader => IsPinned ? "Unpin instance" : "Pin instance";
    public Visibility PinnedVisibility => IsPinned ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Second line of a list row: version and loader, then the owner.</summary>
    public string RowSubtitle => $"{VersionLabel} · {OwnerShortLabel}";

    public string LastPlayedLabel => LastPlayedAt is { } at && TimeFormat.Ago(at) is { } ago
        ? $"Played {ago}"
        : "Not played yet";
    public Visibility UpdateButtonVisibility => Source.IsShared && !_isWorking && !IsInstanceBusy
        ? Visibility.Visible : Visibility.Collapsed;

    private PackUsageStats Usage => App.State.Settings.GetPackUsage(Source.Id);

    // ── progress ─────────────────────────────────────────────────────────────
    private bool _isWorking;
    private MinecraftInstanceStatus _instanceStatus = MinecraftInstanceStatus.Idle;
    private double _progressPercent;
    private bool _isIndeterminate;
    private string _progressLabel = "";

    public bool IsWorking => _isWorking;
    public bool IsInstanceBusy => _instanceStatus != MinecraftInstanceStatus.Idle;
    public double ProgressPercent => _progressPercent;
    public bool IsIndeterminate => _isIndeterminate;
    public string ProgressLabel => _progressLabel;
    public string ProgressPercentLabel =>
        // A paused transfer with a frozen percentage reads as a stuck one, so the bar says so itself.
        Job is { IsPaused: true } ? "Paused" : _isIndeterminate ? "..." : $"{_progressPercent:0}%";
    public string ProgressSummaryLabel => string.IsNullOrWhiteSpace(_progressLabel) ? "Working..." : _progressLabel;
    public Visibility PlayVisibility     => !_isWorking && !IsInstanceBusy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility KillVisibility     => IsInstanceBusy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProgressVisibility => _isWorking && !IsInstanceBusy ? Visibility.Visible : Visibility.Collapsed;

    // ── transfer controls ────────────────────────────────────────────────────

    /// <summary>The pausable transfer behind the bar, when there is one.</summary>
    private PackJob? Job => PackJobs.For(Source.Id);

    /// <summary>
    /// The hover controls only exist for a transfer this launcher is running. Progress can also
    /// come from a launch, and there is nothing to pause about that.
    /// </summary>
    public Visibility TransferControlsVisibility =>
        ProgressVisibility == Visibility.Visible && Job is not null ? Visibility.Visible : Visibility.Collapsed;

    public string PauseGlyph => Job is { IsPaused: true } ? "" : "";   // play / pause

    public string PauseToolTip => Job is { } job
        ? job.IsPaused
            ? $"Resume the {job.KindLabel}"
            : $"Pause the {job.KindLabel} - nothing already downloaded is lost"
        : "";

    public bool CanStopJob => Job is { IsStopping: false };

    /// <summary>A job on its way out cannot be paused, so the button stops offering it.</summary>
    public bool CanPauseJob => Job is { IsStopping: false };

    public string StopToolTip => Job is { } job
        ? $"Stop the {job.KindLabel} and delete the files it downloaded"
        : "";

    public string ProgressToolTip
    {
        get
        {
            var label = string.IsNullOrWhiteSpace(_progressLabel) ? "Working..." : _progressLabel;
            return Job is { IsPaused: true } ? $"Paused - {label}" : label;
        }
    }

    /// <summary>Re-reads the job state after it started, paused, resumed or ended.</summary>
    public void RefreshTransferState()
    {
        OnPropertyChanged(nameof(TransferControlsVisibility));
        OnPropertyChanged(nameof(PauseGlyph));
        OnPropertyChanged(nameof(PauseToolTip));
        OnPropertyChanged(nameof(CanStopJob));
        OnPropertyChanged(nameof(CanPauseJob));
        OnPropertyChanged(nameof(StopToolTip));
        OnPropertyChanged(nameof(ProgressToolTip));
        OnPropertyChanged(nameof(ProgressPercentLabel));
    }

    public void SetInstanceStatus(MinecraftInstanceStatus status)
    {
        if (_instanceStatus == status) return;
        _instanceStatus = status;
        OnAllProgressChanged();
    }

    public void SetProgress(double fraction, string label)
    {
        _isWorking = true;
        if (fraction < 0)
        {
            _isIndeterminate = true;
            _progressPercent = 0;
        }
        else
        {
            _isIndeterminate = false;
            _progressPercent = Math.Round(fraction * 100, 0);
        }
        _progressLabel = label ?? "";
        OnAllProgressChanged();
    }

    public void ClearProgress()
    {
        _isWorking = false;
        _progressPercent = 0;
        _isIndeterminate = false;
        _progressLabel = "";
        OnAllProgressChanged();
    }

    private void OnAllProgressChanged()
    {
        OnPropertyChanged(nameof(IsWorking));
        OnPropertyChanged(nameof(IsInstanceBusy));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(IsIndeterminate));
        OnPropertyChanged(nameof(ProgressLabel));
        OnPropertyChanged(nameof(ProgressPercentLabel));
        OnPropertyChanged(nameof(ProgressSummaryLabel));
        OnPropertyChanged(nameof(PlayVisibility));
        OnPropertyChanged(nameof(KillVisibility));
        OnPropertyChanged(nameof(ProgressVisibility));
        OnPropertyChanged(nameof(UpdateButtonVisibility));
        RefreshTransferState();
    }

    public void RefreshUsage()
    {
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(PlayCount));
        OnPropertyChanged(nameof(TotalPlayTimeSeconds));
        OnPropertyChanged(nameof(LastPlayedAt));
        OnPropertyChanged(nameof(LastPlayedLabel));
        OnPropertyChanged(nameof(PinToolTip));
        OnPropertyChanged(nameof(PinMenuHeader));
        OnPropertyChanged(nameof(PinnedVisibility));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>The instance's first letter, used as the cover-art glyph.</summary>
    public string Initial
    {
        get
        {
            var s = Source.Name?.TrimStart() ?? "";
            return s.Length > 0 ? s.Substring(0, 1).ToUpperInvariant() : "?";
        }
    }

    public ImageSource? CoverImage => _coverImage ??= App.State.PackAssets.TryLoadIconImage(Source.Id);

    public Visibility CoverImageVisibility => CoverImage is not null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility InitialVisibility => CoverImage is null ? Visibility.Visible : Visibility.Collapsed;

    private ImageSource? _coverImage;

    public void RefreshCover()
    {
        _coverImage = null;
        OnPropertyChanged(nameof(CoverImage));
        OnPropertyChanged(nameof(CoverImageVisibility));
        OnPropertyChanged(nameof(InitialVisibility));
    }

    /// <summary>Mod-loader name shown in the bottom-left badge on the cover.</summary>
    public string LoaderBadge => Source.IsEmpty ? "EMPTY"
        : Source.Loader switch
        {
            LoaderKind.Fabric   => "FABRIC",
            LoaderKind.Forge    => "FORGE",
            LoaderKind.NeoForge => "NEOFORGE",
            _                   => "VANILLA"
        };

    public Visibility LoaderBadgeVisibility => Visibility.Visible;

    /// <summary>Per-instance gradient brush picked deterministically from the instance name.</summary>
    public Brush CoverBackground
    {
        get
        {
            // 8-colour palette so each instance gets a stable, distinct cover.
            var palette = new (Color from, Color to)[]
            {
                (Color.FromRgb(0x7A, 0x16, 0x16), Color.FromRgb(0x3E, 0x0A, 0x0A)), // red
                (Color.FromRgb(0x4F, 0x9C, 0xF9), Color.FromRgb(0x1E, 0x4F, 0x8C)), // blue
                (Color.FromRgb(0x10, 0xB9, 0x81), Color.FromRgb(0x0A, 0x6B, 0x4A)), // green
                (Color.FromRgb(0xA8, 0x55, 0xF7), Color.FromRgb(0x5F, 0x2C, 0x9A)), // purple
                (Color.FromRgb(0xE3, 0xB3, 0x41), Color.FromRgb(0x8E, 0x6A, 0x18)), // gold
                (Color.FromRgb(0x14, 0xB8, 0xA6), Color.FromRgb(0x0B, 0x6E, 0x65)), // teal
                (Color.FromRgb(0xEC, 0x4F, 0x88), Color.FromRgb(0x8E, 0x25, 0x4E)), // pink
                (Color.FromRgb(0xF9, 0x73, 0x16), Color.FromRgb(0x96, 0x42, 0x0B)), // orange
            };
            // Stable hash of the instance name (case-insensitive, ordinal).
            var h = 0u;
            foreach (var c in Source.Name ?? "") h = h * 31 + (uint)char.ToLowerInvariant(c);
            var (a, b) = palette[h % (uint)palette.Length];
            var gradient = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint   = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop(a, 0.0),
                    new GradientStop(b, 1.0)
                }
            };
            gradient.Freeze();
            return gradient;
        }
    }

    public string VersionLabel =>
        Source.IsEmpty
            ? "Empty instance"
            : Source.Loader == LoaderKind.None
                ? $"MC {Source.MinecraftVersion}"
                : $"MC {Source.MinecraftVersion} · {Source.Loader} {Source.LoaderVersion}";

    public string TagText => Source.IsEmpty ? "EMPTY"
        : Source.IsShared ? Source.Visibility switch
            {
                PackVisibility.Public => "PUBLIC",
                PackVisibility.Team   => "TEAM",
                _                     => "SHARED"
            }
        : "LOCAL";

    public Brush TagBackground => Source.IsEmpty ? (Brush)App.Current.FindResource("TagEmptyBgBrush")
        : Source.IsShared ? Source.Visibility switch
            {
                PackVisibility.Public => (Brush)App.Current.FindResource("TagPublicBgBrush"),
                PackVisibility.Team   => (Brush)App.Current.FindResource("TagTeamBgBrush"),
                _                     => (Brush)App.Current.FindResource("TagSharedBgBrush")
            }
        : (Brush)App.Current.FindResource("TagEmptyBgBrush");

    public Brush TagForeground => Source.IsEmpty ? (Brush)App.Current.FindResource("TagEmptyFgBrush")
        : Source.IsShared ? Source.Visibility switch
            {
                PackVisibility.Public => (Brush)App.Current.FindResource("TagPublicFgBrush"),
                PackVisibility.Team   => (Brush)App.Current.FindResource("TagTeamFgBrush"),
                _                     => (Brush)App.Current.FindResource("TagSharedFgBrush")
            }
        : (Brush)App.Current.FindResource("TagEmptyFgBrush");
}

/// <summary>One chip on the folder strip.</summary>
/// <remarks>Flags rather than brushes: ThemeService creates new brush objects on every apply, so
/// brushes baked into the chip would keep the old accent. The shared FolderChip* styles in
/// Themes/Controls.xaml paint from these flags with DynamicResource.</remarks>
public sealed class FolderChipRow
{
    /// <summary>Empty for "All"; <c>team:{id:N}</c> for a computed team folder; otherwise the
    /// user folder's own name, which is also its key in settings.</summary>
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Icon { get; set; } = "";
    public string CountLabel { get; set; } = "";
    public string ToolTipText { get; set; } = "";
    public bool IsTeam { get; set; }

    /// <summary>The chip the strip is currently filtered by. A bool the style triggers on.</summary>
    public bool IsActive { get; set; }

    /// <summary>True for the trailing "New folder" chip, which makes a folder rather than
    /// selecting one. The shared style draws it as an outline so it cannot be mistaken for a
    /// folder you can select.</summary>
    public bool IsNewAction { get; set; }
}
