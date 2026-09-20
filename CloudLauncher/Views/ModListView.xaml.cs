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
/// The pack page's Mods tab: a grid over the pack's installed mods. Reads the same unified
/// <see cref="PackModInventory"/> the Modpack Management page does, so a mod that is listed on both
/// stores shows the store it was installed from (or the pack's default store) here exactly as it
/// does there, and the update check, source filter and "Update all" review share one rule set.
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

    private const double SearchCollapsedWidth = 36;
    private const double SearchExpandedWidth = 240;

    public ModListView()
    {
        InitializeComponent();
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = ModFilter;
        ModGrid.ItemsSource = _view;
    }

    public void Load(PackDetail pack, Window owner)
    {
        _pack = pack;
        _ownerWindow = owner;
        LowModeBox.IsChecked = LowModeService.IsEnabled(App.State.Settings, pack.Id);
        _ = ScanModsAsync();
    }

    /// <summary>
    /// Records the low-mode preference. The settings themselves are rewritten at launch rather than here, so the
    /// pack's config files are only ever touched while the game is closed - editing them under a running game would
    /// simply be overwritten when it exits.
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

    private async Task ScanModsAsync(bool forceUpdateCheck = false)
    {
        if (_pack is null) return;
        var generation = ++_scanGeneration;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var pack = _pack;

        StatusLabel.Text = "Scanning mods...";
        UpdateAllButton.Visibility = Visibility.Collapsed;
        List<PackMod> mods;
        try
        {
            mods = await App.State.ModInventory.LoadAsync(pack.Id, pack.IsShared, ct);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            if (generation == _scanGeneration) StatusLabel.Text = "Failed to scan mods: " + ex.Message;
            return;
        }
        if (generation != _scanGeneration) return;

        _rows.Clear();
        foreach (var m in mods) _rows.Add(m);
        if (mods.Count == 0) { StatusLabel.Text = "No mod files found in any mods/ folder."; return; }
        UpdateStatusLabel(identifying: true);

        // Second pass: store identities (hashing + cached matching), patched into the rows in place.
        try { await App.State.ModInventory.ResolveIdentitiesAsync(pack.Id, mods, ct); }
        catch (OperationCanceledException) { return; }
        catch { /* offline — keep the file-name view */ }
        if (generation != _scanGeneration) return;

        _view.Refresh();
        UpdateStatusLabel();
        await CheckUpdatesAsync(mods, generation, ct, forceUpdateCheck);
    }

    /// <summary>Background update check over every identified mod, a few at a time. The lists come
    /// from the shared version catalog, so re-opening this tab is cheap; the ApiClient paces the
    /// actual store traffic so a big pack can't trip the stores' rate limits.</summary>
    private async Task CheckUpdatesAsync(IReadOnlyList<PackMod> mods, int generation, CancellationToken ct,
        bool forceRefresh = false)
    {
        if (_pack is null) return;
        var mc = _pack.MinecraftVersion;
        var loader = ModUpdater.LoaderTag(_pack);
        _checkingUpdates = true;
        UpdateStatusLabel();
        var gate = new SemaphoreSlim(3, 3);
        try
        {
            await Task.WhenAll(mods.Select(async mod =>
            {
                if (mod.PrimaryMod is null || mod.PrimaryVersion is null) return;
                await gate.WaitAsync(ct);
                try
                {
                    var latest = await ModUpdater.FindUpdateAsync(mod, mc, loader, forceRefresh, ct);
                    if (ct.IsCancellationRequested || generation != _scanGeneration) return;
                    await Dispatcher.InvokeAsync(() => { mod.LatestVersion = latest; });
                }
                finally { gate.Release(); }
            }));
        }
        catch (OperationCanceledException) { return; }
        catch { /* individual failures are already swallowed; nothing else to do */ }
        if (generation != _scanGeneration) return;
        _checkingUpdates = false;
        _lastCheckedAt = DateTime.Now;
        _lastCheckWasFresh = forceRefresh;
        UpdateStatusLabel();
    }

    private DateTime? _lastCheckedAt;
    private bool _lastCheckWasFresh;

    private void UpdateStatusLabel(bool identifying = false)
    {
        var total = _rows.Count;
        var curse = _rows.Count(r => r.PrimarySource == ModSource.CurseForge);
        var modrinth = _rows.Count(r => r.PrimarySource == ModSource.Modrinth);
        var external = _rows.Count(r => r.IsExternal);
        var disabled = _rows.Count(r => !r.Enabled);
        var updates = _rows.Count(r => r.HasUpdate);
        var visible = _view.Cast<object>().Count();

        var summary = $"{total} mod(s) — {curse} CurseForge, {modrinth} Modrinth, {external} external";
        if (disabled > 0) summary += $", {disabled} disabled";
        if (updates > 0) summary += $", {updates} update(s)";
        if (visible != total) summary += $" — showing {visible}";
        if (identifying) summary += " — identifying…";
        else if (_checkingUpdates) summary += " — checking for updates…";
        // Say when the stores were last asked, so "did it actually check?" has an answer on screen.
        else if (_lastCheckedAt is { } at)
            summary += $" — checked {at:HH:mm}{(_lastCheckWasFresh ? "" : " (cached)")}";
        StatusLabel.Text = summary;

        UpdateAllButton.Content = $"Update all ({updates})";
        UpdateAllButton.Visibility = updates > 0 ? Visibility.Visible : Visibility.Collapsed;
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

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnSourceFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _view is null) return;
        ApplyFilter();
    }

    private void OnSearchToggle(object sender, RoutedEventArgs e)
    {
        ExpandSearch();
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnSearchPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        SearchBox.Text = "";
        CollapseSearch();
        e.Handled = true;
    }

    private void OnSearchLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is DependencyObject nextFocus && IsDescendantOf(nextFocus, CompactSearchHost))
            return;
        CollapseSearch();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            ExpandSearch();
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void ExpandSearch()
    {
        CompactSearchHost.Width = SearchExpandedWidth;
        SearchBox.Visibility = Visibility.Visible;
    }

    private void CollapseSearch()
    {
        if (!string.IsNullOrWhiteSpace(SearchBox.Text)) return;
        CompactSearchHost.Width = SearchCollapsedWidth;
        SearchBox.Visibility = Visibility.Collapsed;
        Keyboard.ClearFocus();
    }

    private static bool IsDescendantOf(DependencyObject child, DependencyObject ancestor)
    {
        for (var current = child; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor)) return true;
        }
        return false;
    }

    // When the mods grid gets narrow (e.g. inside a side panel), shed the less
    // important columns in priority order so the mod Name always keeps room and
    // wins over the Source label and the Update button.
    private void OnGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;
        var w = e.NewSize.Width;
        SourceColumn.Visibility  = w < 520 ? Visibility.Collapsed : Visibility.Visible;
        UpdateColumn.Visibility  = w < 440 ? Visibility.Collapsed : Visibility.Visible;
        VersionColumn.Visibility = w < 360 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Refresh: re-scan the folder AND ask the stores again, rather than reusing the version lists
    /// the session already has.
    /// </summary>
    /// <remarks>Version lists are cached for fifteen minutes (see <see cref="ModVersionCatalog"/>) so
    /// that opening this tab does not cost a store call per mod — which is what got the launcher's
    /// server throttled in September. The consequence is that leaving the tab and coming back shows
    /// the same answer as before, which looks like the check never ran. This button is the way to
    /// actually ask again.</remarks>
    private async void OnRefresh(object s, RoutedEventArgs e) => await ScanModsAsync(forceUpdateCheck: true);

    private void OnOpenManagement(object s, RoutedEventArgs e)
    {
        if (_pack is null) return;
        if (_ownerWindow is MainWindow main)
            main.OpenModManagementForPack(_pack);
        else if (_ownerWindow is MinecraftHostWindow host)
            host.OpenModExplorerForPack(_pack); // in-game overlay keeps the lightweight browser
        else
            new ModExplorerWindow(_pack) { Owner = _ownerWindow }.Show();
    }

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

    private async void OnModAction(object s, RoutedEventArgs e)
    {
        if (s is FrameworkElement el && el.DataContext is PackMod mod && mod.HasUpdate)
            await UpdateModAsync(mod);
    }

    private async void OnUpdateAll(object sender, RoutedEventArgs e)
    {
        if (_pack is null) return;
        var updatable = _rows.Where(m => m.HasUpdate && m.LatestVersion is not null).ToList();
        if (updatable.Count == 0) { StatusLabel.Text = "Everything is up to date."; return; }

        List<(PackMod Mod, ModVersion Target)>? chosen;
        if (_ownerWindow is MainWindow host)
        {
            chosen = await ModUpdateReviewDialog.ShowAsync(host, _pack, updatable);
        }
        else
        {
            // No in-window card host here (the in-game overlay): fall back to a plain confirm that
            // updates everything except locked mods.
            var run = updatable.Where(m => !m.Meta.UpdateLocked).ToList();
            var held = updatable.Count - run.Count;
            var ok = await AppDialog.ConfirmAsync(_ownerWindow, "Update all",
                $"Update {run.Count} mod(s)?" + (held > 0 ? $" {held} locked mod(s) will be skipped." : ""),
                "Update", "Cancel");
            chosen = ok ? run.Select(m => (m, m.LatestVersion!)).ToList() : null;
        }
        if (chosen is null || chosen.Count == 0) return;

        var summary = await RunUpdatesAsync(chosen);
        StatusLabel.Text = summary.Describe();
        await ScanModsAsync();
    }

    /// <summary>Installs a batch of updates several at a time, showing a bar per mod when there is a
    /// window to show it in (the in-game overlay has none — there the status line reports the result).</summary>
    private async Task<ModUpdateRunner.Summary> RunUpdatesAsync(IReadOnlyList<(PackMod Mod, ModVersion Target)> chosen)
    {
        if (_pack is not null && _ownerWindow is MainWindow host)
            return await ModUpdateProgressDialog.RunAsync(host, _pack, chosen);

        StatusLabel.Text = $"Updating {chosen.Count} mod(s)…";
        var jobs = chosen.Select(c => new ModUpdateRunner.Job(c.Mod, c.Target)).ToList();
        return await ModUpdateRunner.RunAsync(jobs, App.State.Settings.EffectiveModDownloadConcurrency);
    }

    private async Task UpdateModAsync(PackMod mod)
    {
        if (_pack is null || mod.LatestVersion is null) return;
        if (!await ConfirmUpdateGuardsAsync(mod)) return;

        StatusLabel.Text = $"Updating {mod.DisplayName}…";
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
                $"{mod.DisplayName} is marked as update-incompatible — updating it may break your setup.\n\nUpdate anyway?",
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

        StatusLabel.Text = $"Loading versions for {mod.DisplayName}…";
        var versions = await ModUpdater.FetchVersionsAsync(mod.PrimaryMod);
        if (versions.Count == 0) { StatusLabel.Text = $"No versions found for {mod.DisplayName}."; return; }

        var chosen = await ModVersionPickerDialog.ShowAsync(host, mod.DisplayName, versions,
            _pack.MinecraftVersion, ModUpdater.LoaderTag(_pack), mod.PrimaryVersion?.Id, mod.PrimaryVersion?.VersionNumber);
        if (chosen is null) { StatusLabel.Text = ""; return; }
        if (!await ConfirmUpdateGuardsAsync(mod)) { StatusLabel.Text = ""; return; }

        StatusLabel.Text = $"Installing {mod.DisplayName} {chosen.VersionNumber}…";
        try
        {
            if (await ModUpdater.InstallVersionAsync(mod, chosen))
            {
                StatusLabel.Text = $"Installed {mod.DisplayName} {chosen.VersionNumber}.";
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

    private void OnModContextMenuOpened(object sender, RoutedEventArgs e)
    {
        var mod = SelectedMod;
        CtxOpenPage.IsEnabled = mod is { IsExternal: false };
        CtxOpenWebsite.IsEnabled = mod?.PageUrl is not null;
        CtxUpdate.IsEnabled = mod is { HasUpdate: true };
        CtxUpdate.Header = mod is { HasUpdate: true, LatestVersion: { } v } ? $"Update to {v.VersionNumber}" : "Update to latest";
        CtxUpdateToVersion.IsEnabled = mod is { IsExternal: false } && _ownerWindow is MainWindow;
        CtxCreateHostedMod.IsEnabled = mod is { IsExternal: true };
        CtxEnableMod.Visibility = mod is { Enabled: false } ? Visibility.Visible : Visibility.Collapsed;
        CtxDisableMod.Visibility = mod is { Enabled: true } ? Visibility.Visible : Visibility.Collapsed;

        // The store submenu only means something for a jar known on both stores.
        CtxStore.Visibility = mod is { IsCrossListed: true } ? Visibility.Visible : Visibility.Collapsed;
        if (mod is { IsCrossListed: true })
        {
            var packDefault = mod.DefaultSource switch
            {
                ModSource.CurseForge => "CurseForge",
                ModSource.Modrinth => "Modrinth",
                _ => "Modrinth first"
            };
            CtxStoreAuto.Header = $"Pack default ({packDefault})";
            CtxStoreAuto.InputGestureText = mod.Meta.PreferredSource is null ? "✓" : "";
            CtxStoreCurse.InputGestureText = mod.Meta.PreferredSource == ModSource.CurseForge ? "✓" : "";
            CtxStoreModrinth.InputGestureText = mod.Meta.PreferredSource == ModSource.Modrinth ? "✓" : "";
        }
    }

    private void OnCtxOpenPage(object s, RoutedEventArgs e)
    {
        if (SelectedMod is { } mod) OpenModPage(mod);
    }

    private void OnCtxOpenWebsite(object s, RoutedEventArgs e)
    {
        if (SelectedMod?.PageUrl is not { } url) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private async void OnCtxUpdate(object s, RoutedEventArgs e)
    {
        if (SelectedMod is { HasUpdate: true } mod) await UpdateModAsync(mod);
    }

    private async void OnCtxUpdateToVersion(object s, RoutedEventArgs e)
    {
        if (SelectedMod is { } mod) await UpdateToVersionAsync(mod);
    }

    private void OnCtxStoreAuto(object s, RoutedEventArgs e) => SetPreferredSource(null);
    private void OnCtxStoreCurse(object s, RoutedEventArgs e) => SetPreferredSource(ModSource.CurseForge);
    private void OnCtxStoreModrinth(object s, RoutedEventArgs e) => SetPreferredSource(ModSource.Modrinth);

    /// <summary>Pins (or un-pins) which store's listing a cross-listed mod follows, then re-checks
    /// its update against that store's version list.</summary>
    private async void SetPreferredSource(ModSource? source)
    {
        if (_pack is null || SelectedMod is not { } mod) return;
        mod.Meta.PreferredSource = source;
        App.State.ModInventory.SaveMeta(_pack.Id, mod);
        _view.Refresh();
        UpdateStatusLabel();
        var latest = await ModUpdater.FindUpdateAsync(mod, _pack.MinecraftVersion, ModUpdater.LoaderTag(_pack));
        mod.LatestVersion = latest;
        UpdateStatusLabel();
    }

    private async void OnCtxEnable(object s, RoutedEventArgs e)
    {
        if (SelectedMod is { Enabled: false } mod) await SetModEnabledAsync(mod, true);
    }

    private async void OnCtxDisable(object s, RoutedEventArgs e)
    {
        if (SelectedMod is { Enabled: true } mod) await SetModEnabledAsync(mod, false);
    }

    private async void OnModEnabledToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggle || toggle.DataContext is not PackMod mod) return;
        var wantEnabled = toggle.IsChecked == true;
        if (wantEnabled == mod.Enabled) return;

        var ok = await SetModEnabledAsync(mod, wantEnabled);
        if (!ok) toggle.IsChecked = mod.Enabled;
    }

    /// <summary>Flips a mod on disk (<c>.jar</c> ⇄ <c>.jar.disabled</c>) and applies the same dependency
    /// cascade the Modpack Management page uses, so the two never disagree about what a disable means.</summary>
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
            var deps = ModGraphService.PlanEnable(all, mod);
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

    private async void OnCtxCreateHostedMod(object sender, RoutedEventArgs e)
    {
        if (_pack is null) return;
        if (SelectedMod is not { } mod) return;
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

    private void OnCtxReveal(object s, RoutedEventArgs e)
    {
        if (SelectedMod is not { } mod) return;
        var dir = Path.GetDirectoryName(mod.FilePath);
        if (dir is null) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true }); }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private async void OnCtxDelete(object s, RoutedEventArgs e)
    {
        if (SelectedMod is not { } mod) return;
        if (!await AppDialog.ConfirmAsync(_ownerWindow, "Delete mod",
                $"Delete {Path.GetFileName(mod.FilePath)}?", "Delete", "Cancel", danger: true))
            return;
        try
        {
            File.Delete(mod.FilePath);
            await ScanModsAsync();
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
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
