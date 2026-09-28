using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using CloudLauncher.Services;
using CloudLauncher.Shared;
using Microsoft.Win32;

namespace CloudLauncher.Views;

/// <summary>
/// The pack page's Mods tab: a grid over the pack's installed mods. Uses the same
/// <see cref="PackModInventory"/> as Modpack Management, so store attribution, update checks and
/// the source filter match it.
/// <para>Bulk updates live in Modpack Management; here a mod is updated from its row button or
/// menu. The hub button is on the pack page's Overview tab (<see cref="PackDetailView"/>), not
/// here.</para>
/// </summary>
public partial class ModListView : UserControl
{
    private PackDetail? _pack;
    private Window? _ownerWindow;
    private readonly ObservableCollection<PackMod> _rows = new();
    private readonly ICollectionView _view;
    private int _scanGeneration;
    private CancellationTokenSource? _cts;
    private string _searchText = "";
    private bool _checkingUpdates;

    /// <summary>Loading, empty and error states for the grid area.</summary>
    private readonly PageState _state;

    public ModListView()
    {
        InitializeComponent();
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = ModFilter;
        ModGrid.ItemsSource = _view;

        // The filter is in memory and cheap, so it also runs on every keystroke, not only after the
        // box's debounce.
        SearchBox.TextChanged += (_, _) => ApplyFilter();

        // StatusLabel is a status line, not a count: it shows phases ("identifying...", "checked
        // 14:02") as well as numbers. This tab has no count label, so PageState gets none.
        _state = new PageState(ModGrid, PageStateHost, nameof(ModListView))
            .Copy(PageCopy.Mods)
            .Slots(null, StatusLabel)
            .DisableWhileBusy(RefreshButton, SourceFilterBox);
        _state.RetryRequested += () => _ = ScanModsAsync();
    }

    public void Load(PackDetail pack, Window owner)
    {
        _pack = pack;
        _ownerWindow = owner;
        // Low mode is one modpack's profile, not a general setting; see LowModeService.AppliesTo.
        var lowModeOffered = LowModeService.AppliesTo(pack, App.State.Settings);
        LowModePanel.Visibility = lowModeOffered ? Visibility.Visible : Visibility.Collapsed;
        LowModeBox.IsChecked = lowModeOffered && LowModeService.IsEnabled(App.State.Settings, pack.Id);
        // Tab opened or page came back into view: a quiet scan.
        _ = ScanModsAsync(quiet: true);
    }

    /// <summary>
    /// Records the low-mode preference. The config files are rewritten at launch while the game is
    /// closed; edits made under a running game would be overwritten when it exits.
    /// </summary>
    private void OnLowModeToggled(object sender, RoutedEventArgs e)
    {
        if (_pack is null) return;
        App.State.Settings.PackLowMode[_pack.Id] = LowModeBox.IsChecked == true;
        App.State.Settings.Save();
    }

    private async void OnExplainLowMode(object sender, RoutedEventArgs e)
    {
        if (_pack is null) return;
        string gameDir;
        try { gameDir = App.State.Packs.GameDir(_pack.Id); } catch { gameDir = ""; }
        var text = await Task.Run(() => LowModeService.Describe(gameDir));
        await AppDialog.MessageAsync(_ownerWindow, "What low mode does", text, "Got it");
    }

    // ── scan + identify + update check ────────────────────────────────────────

    /// <param name="quiet">True when the user didn't ask for it (tab opened, or returning to the
    /// page). The status line then stays as it was unless the work runs past
    /// <see cref="PageState.QuietRefreshDelay"/>.</param>
    private async Task ScanModsAsync(bool forceUpdateCheck = false, bool quiet = false)
    {
        if (_pack is null) return;
        var generation = ++_scanGeneration;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        // The cancelled scan's check never got to clear these, and its frozen "n of N" would show
        // until this scan's own check starts.
        _checkingUpdates = false;
        _checkProgress = null;
        _identifying = false;
        var ct = _cts.Token;
        var pack = _pack;

        var scanning = $"Reading the mods folder in {pack.Name}...";
        quiet = quiet && !forceUpdateCheck && _rows.Count > 0 && _state.HasData;
        HoldStatusQuiet(quiet);
        if (quiet)
            _state.Begin(scanning, null, quiet: true, onReveal: () => _state.Note(scanning));
        else
        {
            _state.Begin(scanning);
            _state.Note(scanning);
        }
        List<PackMod> mods;
        try
        {
            mods = await App.State.ModInventory.LoadAsync(pack.Id, pack.IsShared, ct);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            // Show a plain message with Retry; the exception goes to the launcher log.
            if (generation == _scanGeneration)
                _state.Error("The mods folder could not be read.", ex);
            return;
        }
        if (generation != _scanGeneration) return;

        _rows.Clear();
        foreach (var m in mods) _rows.Add(m);
        _identifying = mods.Count > 0;
        UpdateStatusLabel();
        if (mods.Count == 0) return;

        // Second pass: store identities (hashing + cached matching), patched into the rows in place.
        try { await App.State.ModInventory.ResolveIdentitiesAsync(pack.Id, mods, ct); }
        catch (OperationCanceledException) { return; }
        catch { /* offline: keep the file-name view */ }
        if (generation != _scanGeneration) return;

        _identifying = false;
        _view.Refresh();
        UpdateStatusLabel();
        await CheckUpdatesAsync(mods, generation, ct, forceUpdateCheck);
    }

    /// <summary>Background update check over every identified mod: a few bulk requests answer most
    /// of the pack at once (see <see cref="ModUpdater.CheckManyAsync"/>), and the rest go one at a time
    /// at the pace set in Settings. The answers come from the shared version catalog, so re-opening
    /// this tab is cheap.</summary>
    private async Task CheckUpdatesAsync(IReadOnlyList<PackMod> mods, int generation, CancellationToken ct,
        bool forceRefresh = false)
    {
        if (_pack is null) return;
        var mc = _pack.MinecraftVersion;
        var loader = ModUpdater.LoaderTag(_pack);
        var pump = new ModUpdateCheckPump(Dispatcher,
            () => ct.IsCancellationRequested || generation != _scanGeneration,
            _ => UpdateStatusLabel());
        _checkingUpdates = true;
        _checkProgress = pump.Progress;
        UpdateStatusLabel();
        ModUpdateCheckSummary? summary = null;
        try
        {
            summary = await ModUpdater.CheckManyAsync(mods, mc, loader, forceRefresh, pump.Progress, pump.Post, ct);
        }
        catch (OperationCanceledException) { /* a newer scan took over */ }
        catch (Exception ex) { AppLog.LogError(nameof(ModListView), ex); }
        finally { pump.Stop(); }
        if (ct.IsCancellationRequested || generation != _scanGeneration) return;
        _checkingUpdates = false;
        _checkProgress = null;
        _lastCheckedAt = DateTimeOffset.Now;
        _lastCheckWasFresh = forceRefresh;
        _lastCheckTook = summary?.Elapsed;
        _lastCheckFailed = summary?.Failed ?? 0;
        UpdateStatusLabel();
    }

    /// <summary>When the stores were last asked. A <see cref="DateTimeOffset"/> so it can go to
    /// <see cref="TimeFormat"/>, which localises the "checked 14:02" stamp.</summary>
    private DateTimeOffset? _lastCheckedAt;
    private bool _lastCheckWasFresh;

    /// <summary>The running check's counters, for the "n of N" in the status line.</summary>
    private ModUpdateCheckProgress? _checkProgress;

    /// <summary>How long the last check took, shown after a refresh.</summary>
    private TimeSpan? _lastCheckTook;

    /// <summary>Mods the last check could not ask about, shown on the status line so they aren't
    /// taken for up to date.</summary>
    private int _lastCheckFailed;

    /// <summary>Between the rows landing and their store identities being resolved.</summary>
    private bool _identifying;

    /// <summary>Until this time a quiet reload keeps the last "checked ..." line instead of showing
    /// the identify pass and update check, which usually answer from cache.</summary>
    private DateTime _statusQuietUntil;
    private System.Windows.Threading.DispatcherTimer? _statusQuietTimer;

    private bool StatusQuiet => DateTime.UtcNow < _statusQuietUntil && _lastCheckedAt is not null;

    /// <summary>Starts (or cancels) the quiet window, and repaints the line when it runs out so work that
    /// is still going by then says so.</summary>
    private void HoldStatusQuiet(bool quiet)
    {
        _statusQuietTimer?.Stop();
        _statusQuietUntil = quiet ? DateTime.UtcNow + PageState.QuietRefreshDelay : DateTime.MinValue;
        if (!quiet) return;
        _statusQuietTimer ??= new System.Windows.Threading.DispatcherTimer();
        _statusQuietTimer.Interval = PageState.QuietRefreshDelay + TimeSpan.FromMilliseconds(20);
        _statusQuietTimer.Tick -= OnStatusQuietOver;
        _statusQuietTimer.Tick += OnStatusQuietOver;
        _statusQuietTimer.Start();
    }

    private void OnStatusQuietOver(object? sender, EventArgs e)
    {
        _statusQuietTimer?.Stop();
        if (_identifying || _checkingUpdates) UpdateStatusLabel();
    }

    /// <summary>
    /// Writes the summary line through <see cref="PageState"/>, which owns the label. With no visible
    /// rows it shows the empty panel, worded differently for no mods and for a filter with no matches.
    /// </summary>
    private void UpdateStatusLabel()
    {
        // During a quiet reload keep showing when the stores were last asked.
        var quiet = StatusQuiet;
        var identifying = _identifying && !quiet;
        var checking = _checkingUpdates && !quiet;
        var total = _rows.Count;
        var curse = _rows.Count(r => r.PrimarySource == ModSource.CurseForge);
        var modrinth = _rows.Count(r => r.PrimarySource == ModSource.Modrinth);
        var external = _rows.Count(r => r.IsExternal);
        var disabled = _rows.Count(r => !r.Enabled);
        var updates = _rows.Count(r => r.HasUpdate);
        var visible = _view.Cast<object>().Count();

        var summary = $"{total} mod(s) - {curse} CurseForge, {modrinth} Modrinth, {external} external";
        if (disabled > 0) summary += $", {disabled} disabled";
        if (updates > 0) summary += $", {updates} update(s)";
        if (visible != total) summary += $" - showing {visible}";
        if (identifying) summary += " - identifying...";
        else if (checking)
            summary += _checkProgress is { Asking: false, Total: > 0 } progress
                ? $" - checking for updates... {progress.Done:N0} of {progress.Total:N0}"
                : " - checking for updates...";
        // Show when the stores were last asked.
        else if (_lastCheckedAt is { } at)
        {
            summary += $" - checked {TimeFormat.Time(at)}";
            summary += !_lastCheckWasFresh ? " (cached)"
                : _lastCheckTook is { } took ? $" in {ModUpdater.Duration(took)}" : "";
            if (_lastCheckFailed > 0)
                summary += $" - {ModUpdater.Mods(_lastCheckFailed)} could not be checked, try again later";
        }

        if (visible == 0 && total > 0)
            _state.EmptyNext("Nothing matched",
                "Every mod is still there - the search box or the source filter is hiding them.",
                "\uE721");

        // An empty instance gets the empty panel and no summary line.
        _state.Content(visible, note: total == 0 ? null : summary);
    }

    // ── filtering ─────────────────────────────────────────────────────────────

    private bool ModFilter(object item)
    {
        if (item is not PackMod mod) return false;

        var sourceOk = SourceFilterBox.SelectedIndex switch
        {
            1 => mod.PrimarySource == ModSource.CurseForge,
            2 => mod.PrimarySource == ModSource.Modrinth,
            3 => mod.IsExternal,
            _ => true
        };
        if (!sourceOk) return false;

        var query = _searchText.Trim();
        if (string.IsNullOrEmpty(query)) return true;
        return mod.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
               || mod.VersionLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
               || mod.SourceLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
               || mod.Folder.Contains(query, StringComparison.OrdinalIgnoreCase)
               || mod.CategoriesLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
               || mod.FileName.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyFilter()
    {
        _searchText = SearchBox.Text;
        _view.Refresh();
        UpdateStatusLabel();
    }

    private void OnSourceFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _view is null) return;
        ApplyFilter();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            SearchBox.Focus();
            e.Handled = true;
        }
        // F5 re-scans and re-checks the stores, like the resource-pack list and Modpack Management.
        else if (e.Key == Key.F5)
        {
            _ = ScanModsAsync(forceUpdateCheck: true);
            e.Handled = true;
        }
    }

    /// <summary>Opens the instance's mods folder. Works with both stores unreachable, which is why it
    /// sits beside Refresh.</summary>
    private void OnOpenModsFolder(object sender, RoutedEventArgs e)
    {
        if (_pack is null) return;
        try
        {
            var dir = Path.Combine(App.State.Packs.GameDir(_pack.Id), "mods");
            Directory.CreateDirectory(dir);
            if (!SafeLaunch.OpenFolder(dir)) _state.Note("The mods folder could not be opened.");
        }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(ModListView), ex);
            _state.Note("The mods folder could not be opened.");
        }
    }

    // When the mods grid gets narrow (e.g. inside a side panel), shed the less
    // important columns in priority order so the mod Name always keeps room and
    // wins over the Source label and the Update button.
    private void OnGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;
        var w = e.NewSize.Width;
        SourceColumn.Visibility  = w < 520 ? Visibility.Collapsed : Visibility.Visible;
        // This column is the tab's only one-click update, and the tab often opens in a ~420 px side
        // panel, so it outlasts the version text.
        UpdateColumn.Visibility  = w < 330 ? Visibility.Collapsed : Visibility.Visible;
        VersionColumn.Visibility = w < 360 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Refresh: re-scans the folder and asks the stores again instead of using cached version lists.
    /// </summary>
    /// <remarks>Version lists are cached for fifteen minutes (see <see cref="ModVersionCatalog"/>) so
    /// opening this tab doesn't cost a store call per mod, which gets the server throttled. This is how
    /// to force a fresh check.</remarks>
    private async void OnRefresh(object s, RoutedEventArgs e) => await ScanModsAsync(forceUpdateCheck: true);

    private async void OnImportModpack(object s, RoutedEventArgs e)
    {
        if (_pack is null) return;
        var dlg = new OpenFileDialog
        {
            Title = "Select modpack file",
            Filter = "Modpack files|*.mrpack;*.zip|Modrinth pack (*.mrpack)|*.mrpack|CurseForge pack (*.zip)|*.zip"
        };
        if (dlg.ShowDialog(_ownerWindow) != true) return;

        var path = dlg.FileName;
        var name = Path.GetFileNameWithoutExtension(path);
        await App.State.ModpackDownload.StartLocalFileImportAsync(path, name);
    }

    // ── updates ───────────────────────────────────────────────────────────────

    private void OnModAction(object s, RoutedEventArgs e)
    {
        if (s is FrameworkElement el && el.DataContext is PackMod mod && mod.HasUpdate)
            ModOptionsMenu.RunUpdateButton(mod, m => _ = UpdateModAsync(m), VersionPickerFor(mod));
    }

    /// <summary>Right-click on a row's Update button: the other kind of update. Handled, so the grid's
    /// own menu does not open over it.</summary>
    private void OnModActionRightClick(object s, MouseButtonEventArgs e)
    {
        if (s is FrameworkElement el && el.DataContext is PackMod mod && mod.HasUpdate)
            e.Handled = ModOptionsMenu.OpenUpdateButtonMenu(el, mod, m => _ = UpdateModAsync(m), VersionPickerFor(mod));
    }

    /// <summary>"Update to version...", or null where this list cannot show the picker (see
    /// <see cref="BuildModOptionsContext"/>).</summary>
    private Action<PackMod>? VersionPickerFor(PackMod mod) =>
        mod.IsExternal || _ownerWindow is not MainWindow ? null : m => _ = UpdateToVersionAsync(m);

    private async Task UpdateModAsync(PackMod mod)
    {
        if (_pack is null || mod.LatestVersion is null) return;
        if (!await ConfirmUpdateGuardsAsync(mod)) return;

        StatusLabel.Text = $"Updating {mod.DisplayName}...";
        try
        {
            if (await ModUpdater.InstallVersionAsync(mod, mod.LatestVersion))
            {
                StatusLabel.Text = $"Updated {mod.DisplayName}.";
                await ScanModsAsync();
            }
            else StatusLabel.Text = "No downloadable file.";
        }
        catch (Exception ex) { StatusLabel.Text = "Update failed: " + ex.Message; }
    }

    /// <summary>The lock and the update-incompatible flag are set in Modpack Management; this view
    /// honours them and lets the user override once.</summary>
    private async Task<bool> ConfirmUpdateGuardsAsync(PackMod mod)
    {
        if (_pack is null) return false;
        if (mod.Meta.UpdateLocked &&
            !await AppDialog.ConfirmAsync(_ownerWindow, "Mod is locked",
                $"{mod.DisplayName} is locked to its current version.\n\nUpdate it anyway? It stays locked afterwards.",
                "Update anyway", "Keep locked"))
            return false;

        if (mod.Meta.UpdateIncompatible && App.State.ModMetadata.Advanced(_pack.Id).WarnOnUpdateIncompatible &&
            !await AppDialog.ConfirmAsync(_ownerWindow, "Update warning",
                $"{mod.DisplayName} is marked as update-incompatible - updating it may break your setup.\n\nUpdate anyway?",
                "Update", "Cancel", danger: true))
            return false;
        return true;
    }

    private async Task UpdateToVersionAsync(PackMod mod)
    {
        if (_pack is null || mod.PrimaryMod is null || _ownerWindow is not MainWindow host)
        {
            StatusLabel.Text = mod.PrimaryMod is null ? $"{mod.DisplayName} isn't identified yet." : "";
            return;
        }

        StatusLabel.Text = $"Loading versions for {mod.DisplayName}...";
        var versions = await ModUpdater.FetchVersionsAsync(mod.PrimaryMod);
        if (versions.Count == 0) { StatusLabel.Text = $"No versions found for {mod.DisplayName}."; return; }

        var chosen = await ModVersionPickerDialog.ShowAsync(host, mod.DisplayName, versions,
            _pack.MinecraftVersion, ModUpdater.LoaderTag(_pack), mod.PrimaryVersion?.Id,
            mod.PrimaryVersion?.VersionNumber, mod.Meta.UpdateLocked);
        if (chosen is null) { StatusLabel.Text = ""; return; }
        if (!await ConfirmUpdateGuardsAsync(mod)) { StatusLabel.Text = ""; return; }

        StatusLabel.Text = $"Installing {mod.DisplayName} {chosen.Version.VersionNumber}...";
        try
        {
            if (await ModUpdater.InstallVersionAsync(mod, chosen.Version))
            {
                // The picker's "Keep this version" box pins a downgrade so a later
                // "Update all" doesn't undo it.
                ModVersionPickerDialog.ApplyKeepVersion(_pack.Id, mod, chosen);
                StatusLabel.Text = $"Installed {mod.DisplayName} {chosen.Version.VersionNumber}.";
                await ScanModsAsync();
            }
            else StatusLabel.Text = "That version has no downloadable file.";
        }
        catch (Exception ex) { StatusLabel.Text = "Install failed: " + ex.Message; }
    }

    // ── open ──────────────────────────────────────────────────────────────────

    private void OnModDoubleClick(object s, MouseButtonEventArgs e)
    {
        if (ModGrid.SelectedItem is PackMod mod) OpenModPage(mod);
    }

    private void OpenModPage(PackMod mod)
    {
        if (_pack is null) return;
        if (_ownerWindow is MinecraftHostWindow host)
            host.OpenModExplorerForPack(_pack);
        else if (_ownerWindow is MainWindow main)
            main.OpenModExplorerForPack(_pack, mod.PrimaryMod);
        else
            new ModExplorerWindow(_pack) { Owner = _ownerWindow }.Show();
    }

    // ── context-menu actions ─────────────────────────────────────────────────

    private PackMod? SelectedMod => ModGrid.SelectedItem as PackMod;

    private void OnModGridPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is { } row)
        {
            row.IsSelected = true;
            ModGrid.SelectedItem = row.Item;
        }
    }

    /// <summary>
    /// Right-clicking a row opens the shared <see cref="ModOptionsMenu"/>, as in Modpack Management,
    /// the planning board and the Categories tab.
    /// </summary>
    /// <remarks>Only a click on a row opens it, so the empty space below the last row can't act on a
    /// stale selection.</remarks>
    private void OnModGridRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_pack is null) return;
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is null) return;
        if (SelectedMod is not { } mod) return;
        var menu = ModOptionsMenu.Build(mod, BuildModOptionsContext(mod));
        menu.PlacementTarget = ModGrid;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>Clicking a mod's name opens its store page in the browser.</summary>
    /// <remarks>Single clicks only, so the grid's double-click-opens-the-mod still works.</remarks>
    private void OnModNameClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1) return;
        if ((sender as FrameworkElement)?.DataContext is not PackMod { PageUrl: { } url }) return;
        ModOptionsMenu.OpenUrl(url);
        e.Handled = true;
    }

    /// <summary>
    /// Callbacks for the shared menu, built per right-click. A null callback hides that item.
    /// </summary>
    /// <remarks>"Open page" and "Update to version..." are left out for a jar no store knows (the
    /// latter also when no picker dialog can be shown). "Create hosted mod from jar" is always set;
    /// the menu greys it out for identified mods.</remarks>
    private ModOptionsContext BuildModOptionsContext(PackMod mod) => new()
    {
        PackId = _pack!.Id,
        Inventory = App.State.ModInventory,
        Owner = _ownerWindow,
        AllMods = _rows,
        OnChanged = () => { _view.Refresh(); UpdateStatusLabel(); },
        OnOpenPage = mod.IsExternal ? null : OpenModPage,
        OnUpdate = list => _ = UpdateManyAsync(list),
        OnUpdateToVersion = VersionPickerFor(mod),
        OnDelete = list => _ = DeleteFilesAsync(list),
        OnReveal = Reveal,
        OnSetEnabled = (list, enabled) => _ = SetManyEnabledAsync(list, enabled),
        OnRecheckUpdates = list => _ = RecheckUpdatesAsync(list),
        OnCreateHostedMod = m => _ = CreateHostedModAsync(m),
    };

    private async Task UpdateManyAsync(IReadOnlyList<PackMod> mods)
    {
        foreach (var mod in mods.Where(m => m.HasUpdate)) await UpdateModAsync(mod);
    }

    private async Task SetManyEnabledAsync(IReadOnlyList<PackMod> mods, bool enabled)
    {
        foreach (var mod in mods.Where(m => m.Enabled != enabled)) await SetModEnabledAsync(mod, enabled);
    }

    /// <summary>Re-runs the update check for mods whose store or release channel just changed.</summary>
    private async Task RecheckUpdatesAsync(IReadOnlyList<PackMod> mods)
    {
        if (_pack is null) return;
        _view.Refresh();
        UpdateStatusLabel();
        var mc = _pack.MinecraftVersion;
        var loader = ModUpdater.LoaderTag(_pack);
        foreach (var mod in mods)
        {
            // If the store can't be asked, keep what the mod showed before.
            var result = await ModUpdater.CheckUpdateAsync(mod, mc, loader);
            if (!result.Failed) mod.LatestVersion = result.Update;
        }
        UpdateStatusLabel();
    }

    private void Reveal(PackMod mod)
    {
        var dir = Path.GetDirectoryName(mod.FilePath);
        if (dir is null) return;
        if (!SafeLaunch.OpenFolder(dir)) StatusLabel.Text = "Could not open the folder.";
    }

    private async Task DeleteFilesAsync(IReadOnlyList<PackMod> mods)
    {
        if (mods.Count == 0) return;
        var what = mods.Count == 1 ? Path.GetFileName(mods[0].FilePath) : $"{mods.Count} mod files";
        if (!await AppDialog.ConfirmAsync(_ownerWindow, "Delete mod",
                $"Delete {what}?", "Delete", "Cancel", danger: true))
            return;
        try
        {
            foreach (var mod in mods) File.Delete(mod.FilePath);
            await ScanModsAsync();
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private async void OnModEnabledToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggle || toggle.DataContext is not PackMod mod) return;
        var wantEnabled = toggle.IsChecked == true;
        if (wantEnabled == mod.Enabled) return;

        var ok = await SetModEnabledAsync(mod, wantEnabled);
        if (!ok) toggle.IsChecked = mod.Enabled;
    }

    /// <summary>Toggles a mod on disk (<c>.jar</c> / <c>.jar.disabled</c>) with the same dependency
    /// cascade as the Modpack Management page.</summary>
    private Task<bool> SetModEnabledAsync(PackMod mod, bool enabled)
    {
        if (_pack is null) return Task.FromResult(false);
        var adv = App.State.ModMetadata.Advanced(_pack.Id);
        var inv = App.State.ModInventory;
        var all = _rows.ToList();

        if (!enabled)
        {
            var also = adv.CascadeDisableDependents
                ? ModGraphService.PlanDisable(all, mod, adv.CascadeDisableLibraries).AlsoDisable
                : Array.Empty<PackMod>();
            if (!inv.SetEnabled(mod, false))
            {
                StatusLabel.Text = $"Could not disable {mod.DisplayName}.";
                mod.Refresh();
                return Task.FromResult(false);
            }
            foreach (var m in also) inv.SetEnabled(m, false);
            StatusLabel.Text = also.Count == 0
                ? $"Disabled {mod.DisplayName}."
                : $"Disabled {mod.DisplayName} and {also.Count} dependent(s).";
        }
        else
        {
            // Honours the pack's "Auto-download and enable required dependencies" switch, like the Modpack
            // Management page.
            var deps = adv.AutoDownloadDependencies
                ? ModGraphService.PlanEnable(all, mod)
                : Array.Empty<PackMod>();
            if (!inv.SetEnabled(mod, true))
            {
                StatusLabel.Text = $"Could not enable {mod.DisplayName}.";
                mod.Refresh();
                return Task.FromResult(false);
            }
            foreach (var m in deps) inv.SetEnabled(m, true);
            StatusLabel.Text = deps.Count == 0
                ? $"Enabled {mod.DisplayName}."
                : $"Enabled {mod.DisplayName} and {deps.Count} dependency(ies).";
        }
        _view.Refresh();
        return Task.FromResult(true);
    }

    /// <summary>Publishes a jar no store recognises as a hosted mod on the launcher's own server.</summary>
    private async Task CreateHostedModAsync(PackMod mod)
    {
        if (_pack is null) return;
        if (!mod.IsExternal)
        {
            StatusLabel.Text = "This jar is already linked to a known mod.";
            return;
        }
        if (!File.Exists(mod.FilePath))
        {
            StatusLabel.Text = "Jar file no longer exists.";
            return;
        }

        var defaultName = CleanModName(Path.GetFileNameWithoutExtension(mod.FileName));
        var nameDialog = new SimpleInputDialog("Create hosted mod", "Mod name", defaultName)
        {
            Owner = _ownerWindow
        };
        if (nameDialog.ShowDialog() != true) return;

        var modName = (nameDialog.Result ?? "").Trim();
        if (string.IsNullOrWhiteSpace(modName))
        {
            StatusLabel.Text = "Mod name cannot be empty.";
            return;
        }

        var defaultVersion = GuessVersion(mod.FileName);
        var versionDialog = new SimpleInputDialog("Upload first version", "Version string", defaultVersion)
        {
            Owner = _ownerWindow
        };
        if (versionDialog.ShowDialog() != true) return;

        var version = (versionDialog.Result ?? "").Trim();
        if (string.IsNullOrWhiteSpace(version))
        {
            StatusLabel.Text = "Version string cannot be empty.";
            return;
        }

        StatusLabel.Text = $"Creating hosted mod for {mod.FileName}...";
        try
        {
            var loadersCsv = PackLoaderCsv(_pack);
            var created = await App.State.Api.CreateModAsync(new CreateModRequest(
                modName,
                $"Created from {mod.FileName}",
                null,
                PackVisibility.Private,
                _pack.MinecraftVersion,
                loadersCsv));

            StatusLabel.Text = "Uploading first version...";
            await App.State.Api.UploadModVersionAsync(created.Id, mod.FilePath, new CreateModVersionRequest(
                version,
                null,
                "release",
                Path.GetFileName(mod.FilePath),
                _pack.MinecraftVersion,
                loadersCsv));

            StatusLabel.Text = $"Created hosted mod '{created.Name}'.";
            await ScanModsAsync();
            if (_ownerWindow is MinecraftHostWindow host)
                host.OpenModDetail(created.Id, created.Name);
            else if (_ownerWindow is MainWindow main)
                main.OpenModDetail(created.Id, created.Name);
        }
        catch (ApiException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound)
        {
            StatusLabel.Text = "Hosted mod support is not deployed on the server yet.";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Create hosted mod failed: " + ex.Message;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static string PackLoaderCsv(PackDetail pack) =>
        pack.Loader == LoaderKind.None ? "" : pack.Loader.ToString().ToLowerInvariant();

    private static string CleanModName(string? fileStem)
    {
        var stem = string.IsNullOrWhiteSpace(fileStem) ? "New mod" : fileStem.Trim();
        stem = System.Text.RegularExpressions.Regex.Replace(stem, @"[-_]+", " ");
        stem = System.Text.RegularExpressions.Regex.Replace(stem, @"\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(stem) ? "New mod" : stem;
    }

    private static string GuessVersion(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var match = System.Text.RegularExpressions.Regex.Match(stem, @"(?<!\d)(\d+\.\d+(?:\.\d+)?(?:[-+][A-Za-z0-9_.-]+)?)(?!\d)");
        return match.Success ? match.Groups[1].Value : "1.0.0";
    }
}
