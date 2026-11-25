using System.Collections.Concurrent;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// An instance's installed mods (the instance page's Mods tab): the same <see cref="PackModInventory"/>
/// as the Windows launcher's mods list and Modpack Management, so store identities, update checks and
/// on/off states match it. Mods are switched on and off, deleted and updated from their rows; Add Mods
/// opens the mod browser for this instance.
/// </summary>
/// <remarks>The list comes in two passes, as on Windows: the folder with each jar's cached identity
/// first (so it paints at once, offline too), then the identity pass and a background update check over
/// the shared version catalog.</remarks>
public partial class InstanceModsView : UserControl
{
    private readonly PackDetail _pack;
    private List<PackMod> _rows = new();
    private int _scanGeneration;
    private CancellationTokenSource? _cts;
    private bool _scanned;
    private bool _identifying;
    private bool _checkingUpdates;
    private ModUpdateCheckProgress? _checkProgress;
    private DateTimeOffset? _lastCheckedAt;
    private bool _lastCheckWasFresh;
    private TimeSpan? _lastCheckTook;
    private int _lastCheckFailed;
    private bool _summaryQueued;
    private DispatcherTimer? _rescanTimer;
    private bool _settingLowMode;

    /// <summary>For the XAML previewer only: an empty list that does nothing.</summary>
    public InstanceModsView()
    {
        InitializeComponent();
        _pack = null!;
    }

    public InstanceModsView(PackDetail pack)
    {
        InitializeComponent();
        _pack = pack;

        // Low mode is one modpack's profile, not a general setting (LowModeService.AppliesTo); it is on
        // for that pack until the user turns it off, and the launch applies it.
        var lowModeOffered = LowModeService.AppliesTo(pack, App.State.Settings);
        LowModePanel.IsVisible = lowModeOffered;
        _settingLowMode = true;
        LowModeBox.IsChecked = lowModeOffered && LowModeService.IsEnabled(App.State.Settings, pack.Id);
        _settingLowMode = false;

        // The filter is in memory and cheap, so it runs on every keystroke.
        SearchBox.TextChanged += (_, _) => ApplyFilter();
        SourceFilterBox.SelectionChanged += (_, _) => ApplyFilter();
        StatePanel.RetryRequested += () => _ = ScanModsAsync();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);

        AttachedToVisualTree += (_, _) =>
        {
            App.State.ManualDownloads.Changed -= OnManualDownloadsChanged;
            App.State.ManualDownloads.Changed += OnManualDownloadsChanged;
            App.State.ManualDownloads.PlacedInBackground -= OnPlacedInBackground;
            App.State.ManualDownloads.PlacedInBackground += OnPlacedInBackground;
            // Tab opened, or back from the mod browser: a quiet scan.
            _ = ScanModsAsync(quiet: true);
            _ = UpdateByHandChipAsync();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            App.State.ManualDownloads.Changed -= OnManualDownloadsChanged;
            App.State.ManualDownloads.PlacedInBackground -= OnPlacedInBackground;
            _rescanTimer?.Stop();
            _cts?.Cancel();
        };
    }

    // ── scan + identify + update check ────────────────────────────────────────

    /// <param name="quiet">True when the user didn't ask for it (tab opened, back on the page): the rows
    /// already showing stay, with no "reading" line over them.</param>
    private async Task ScanModsAsync(bool forceUpdateCheck = false, bool quiet = false)
    {
        var generation = ++_scanGeneration;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _checkingUpdates = false;
        _checkProgress = null;
        _identifying = false;

        quiet = quiet && !forceUpdateCheck && _rows.Count > 0;
        if (!quiet)
        {
            if (_rows.Count == 0) StatePanel.Loading($"Reading the mods folder in {_pack.Name}...");
            SummaryLabel.Text = $"Reading the mods folder in {_pack.Name}...";
        }

        List<PackMod> mods;
        try
        {
            mods = await App.State.ModInventory.LoadAsync(_pack.Id, _pack.IsShared, ct);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            if (generation != _scanGeneration) return;
            AppLog.LogError(nameof(InstanceModsView), ex);
            if (_rows.Count == 0) StatePanel.Error("Could not scan the mods folder", "The full error is in the launcher log.");
            else Say("The mods folder could not be read. The full error is in the launcher log.", danger: true);
            SummaryLabel.Text = "";
            return;
        }
        if (generation != _scanGeneration) return;

        SetRows(mods);
        _scanned = true;
        _identifying = mods.Count > 0;
        ApplyFilter();
        if (mods.Count == 0) return;

        // Second pass: store identities (hashing and cached matching), patched into the rows in place.
        try { await App.State.ModInventory.ResolveIdentitiesAsync(_pack.Id, mods, ct); }
        catch (OperationCanceledException) { return; }
        catch { /* offline: keep the file-name view */ }
        if (generation != _scanGeneration) return;

        _identifying = false;
        // Names can change once a jar is identified, so sort again by the inventory's own rule.
        SetRows(mods.OrderByDescending(m => m.Priority)
            .ThenByDescending(m => m.ContentSize)
            .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList());
        ApplyFilter();
        await CheckUpdatesAsync(mods, generation, ct, forceUpdateCheck);
    }

    /// <summary>
    /// Background update check over every identified mod: a few bulk requests answer most of the
    /// instance at once (<see cref="ModUpdater.CheckManyAsync"/>), the rest go one at a time at the pace
    /// set in Settings. Answers reach the rows ten times a second.
    /// </summary>
    private async Task CheckUpdatesAsync(IReadOnlyList<PackMod> mods, int generation, CancellationToken ct,
        bool forceRefresh)
    {
        var mc = _pack.MinecraftVersion;
        var loader = ModUpdater.LoaderTag(_pack);
        var answers = new ConcurrentQueue<(PackMod Mod, ModVersion? Latest)>();
        var progress = new ModUpdateCheckProgress();
        bool Stale() => ct.IsCancellationRequested || generation != _scanGeneration;
        void Drain()
        {
            var stale = Stale();
            while (answers.TryDequeue(out var answer))
                if (!stale) answer.Mod.LatestVersion = answer.Latest;
            if (!stale) UpdateSummary();
        }

        var pump = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        pump.Tick += (_, _) => Drain();
        _checkingUpdates = true;
        _checkProgress = progress;
        UpdateSummary();
        pump.Start();
        ModUpdateCheckSummary? summary = null;
        try
        {
            summary = await ModUpdater.CheckManyAsync(mods, mc, loader, forceRefresh, progress,
                (mod, latest) => answers.Enqueue((mod, latest)), ct);
        }
        catch (OperationCanceledException) { /* a newer scan took over */ }
        catch (Exception ex) { AppLog.LogError(nameof(InstanceModsView), ex); }
        finally
        {
            pump.Stop();
            Drain();
        }
        if (Stale()) return;
        _checkingUpdates = false;
        _checkProgress = null;
        _lastCheckedAt = DateTimeOffset.Now;
        _lastCheckWasFresh = forceRefresh;
        _lastCheckTook = summary?.Elapsed;
        _lastCheckFailed = summary?.Failed ?? 0;
        UpdateSummary();
    }

    /// <summary>Takes a new set of rows, and follows each one's changes for the summary line.</summary>
    private void SetRows(List<PackMod> mods)
    {
        foreach (var old in _rows) old.PropertyChanged -= OnModChanged;
        _rows = mods;
        foreach (var mod in _rows) mod.PropertyChanged += OnModChanged;
    }

    /// <summary>A mod's update, identity or state changed: the summary line follows, once per beat,
    /// since a bulk pass changes hundreds of rows at once.</summary>
    private void OnModChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_summaryQueued) return;
        _summaryQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _summaryQueued = false;
            UpdateSummary();
        }, DispatcherPriority.Background);
    }

    // ── filtering + summary ──────────────────────────────────────────────────

    private void ApplyFilter()
    {
        var query = SearchBox.Text?.Trim() ?? "";
        var source = SourceFilterBox.SelectedIndex;
        var visible = _rows.Where(m => Matches(m, source, query)).ToList();
        ModList.ItemsSource = visible;

        if (_rows.Count == 0)
        {
            if (_scanned)
                StatePanel.Empty("No mods installed",
                    "Add mods from CurseForge and Modrinth with Add Mods, or put jars you already have in the mods folder.");
        }
        else if (visible.Count == 0)
            StatePanel.Empty("Nothing matched", "Every mod is still there: the search or the source filter is hiding them.", "");
        else
            StatePanel.Hide();
        UpdateSummary();
    }

    private static bool Matches(PackMod mod, int source, string query)
    {
        var sourceOk = source switch
        {
            1 => mod.PrimarySource == ModSource.CurseForge,
            2 => mod.PrimarySource == ModSource.Modrinth,
            3 => mod.IsExternal,
            _ => true
        };
        if (!sourceOk) return false;
        if (query.Length == 0) return true;
        return mod.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
               || mod.VersionLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
               || mod.SourceLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
               || mod.Folder.Contains(query, StringComparison.OrdinalIgnoreCase)
               || mod.CategoriesLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
               || mod.FileName.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The line over the list: what is in it, and where the update check stands.</summary>
    private void UpdateSummary()
    {
        var total = _rows.Count;
        var updates = _rows.Count(r => r.HasUpdate);
        UpdateAllButton.IsVisible = updates > 0;
        UpdateAllText.Text = updates == 1 ? "Update 1 Mod" : $"Update {updates} Mods";
        if (total == 0)
        {
            if (_scanned) SummaryLabel.Text = "";
            return;
        }

        var curse = _rows.Count(r => r.PrimarySource == ModSource.CurseForge);
        var modrinth = _rows.Count(r => r.PrimarySource == ModSource.Modrinth);
        var external = _rows.Count(r => r.IsExternal);
        var off = _rows.Count(r => !r.Enabled);
        var visible = (ModList.ItemsSource as ICollection<PackMod>)?.Count ?? total;

        var line = $"{ModText.Count(total, "mod", "mods")}  ·  {curse} CurseForge, {modrinth} Modrinth, {external} external";
        if (off > 0) line += $", {off} off";
        if (updates > 0) line += $"  ·  {ModText.Count(updates, "update", "updates")}";
        if (visible != total) line += $"  ·  showing {visible}";
        if (_identifying) line += "  ·  identifying...";
        else if (_checkingUpdates)
            line += _checkProgress is { Asking: false, Total: > 0 } progress
                ? $"  ·  checking for updates... {progress.Done:N0} of {progress.Total:N0}"
                : "  ·  checking for updates...";
        else if (_lastCheckedAt is { } at)
        {
            line += $"  ·  checked {TimeFormat.Time(at)}";
            line += !_lastCheckWasFresh ? " (cached)" : _lastCheckTook is { } took ? $" in {ModUpdater.Duration(took)}" : "";
            if (_lastCheckFailed > 0)
                line += $"  ·  {ModUpdater.Mods(_lastCheckFailed)} could not be checked, try again later";
        }
        SummaryLabel.Text = line;
    }

    private void Say(string text, bool danger = false)
    {
        StatusLabel.Text = text;
        StatusLabel.IsVisible = text.Length > 0;
        StatusLabel.Classes.Set("danger", danger);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            // Re-reads the folder and asks the stores again instead of the cached version lists.
            _ = ScanModsAsync(forceUpdateCheck: true);
            e.Handled = true;
        }
    }

    // ── toolbar ──────────────────────────────────────────────────────────────

    /// <summary>Re-reads the folder and asks the stores again. Version lists are cached for fifteen
    /// minutes, so opening the tab doesn't cost a store call per mod; this is the fresh check.</summary>
    private void OnCheckUpdates(object? sender, RoutedEventArgs e) => _ = ScanModsAsync(forceUpdateCheck: true);

    private void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.Combine(App.State.Packs.GameDir(_pack.Id), "mods");
            Directory.CreateDirectory(dir);
            if (!SafeLaunch.OpenFolder(dir)) Say("The mods folder could not be opened.", danger: true);
        }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(InstanceModsView), ex);
            Say("The mods folder could not be opened.", danger: true);
        }
    }

    private void OnAddMods(object? sender, RoutedEventArgs e) => MainWindow.Current.Navigate(new ModBrowserPage(_pack));

    /// <summary>Records the low-mode choice. The config files are rewritten at launch while the game is
    /// closed; edits made under a running game would be overwritten when it exits.</summary>
    private void OnLowModeToggled(object? sender, RoutedEventArgs e)
    {
        if (_settingLowMode) return;
        App.State.Settings.PackLowMode[_pack.Id] = LowModeBox.IsChecked == true;
        App.State.Settings.Save();
    }

    /// <summary>What low mode changes in this instance, as a card that scrolls (the list is long).</summary>
    private async void OnExplainLowMode(object? sender, RoutedEventArgs e)
    {
        try
        {
            string gameDir;
            try { gameDir = App.State.Packs.GameDir(_pack.Id); } catch { gameDir = ""; }
            var text = await Task.Run(() => LowModeService.Describe(gameDir));

            var done = new TaskCompletionSource<bool>();
            var title = new TextBlock { Text = "What low mode does" };
            title.Classes.Add("h2");
            var body = new SelectableTextBlock { Text = text.Trim(), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            body.Classes.Add("muted");
            var ok = new Button { Content = "OK", MinWidth = 90, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
            ok.Classes.Add("primary");
            ok.Click += (_, _) => done.TrySetResult(true);
            var scroll = new ScrollViewer { Content = body, MaxHeight = 420, Margin = new(0, 12, 0, 0) };
            ok.Margin = new(0, 18, 0, 0);
            var card = new Border { Width = 560, Child = new StackPanel { Children = { title, scroll, ok } } };
            card.Classes.Add("dialog");
            await MainWindow.Current.ShowCardAsync(card, done.Task, () => done.TrySetResult(true));
        }
        catch (Exception ex) { AppLog.LogError(nameof(InstanceModsView), ex); }
    }

    // ── files to download by hand ────────────────────────────────────────────

    private void OnManualDownloadsChanged(Guid packId)
    {
        if (packId != _pack.Id) return;
        _ = UpdateByHandChipAsync();
        // A file that landed by hand is a new jar in the folder; one scan for a burst of changes.
        _rescanTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _rescanTimer.Tick -= OnRescanTick;
        _rescanTimer.Tick += OnRescanTick;
        _rescanTimer.Stop();
        _rescanTimer.Start();
    }

    private void OnRescanTick(object? sender, EventArgs e)
    {
        _rescanTimer?.Stop();
        _ = ScanModsAsync(quiet: true);
    }

    private void OnPlacedInBackground(Guid packId, ManualDownloadEntry entry, string path)
    {
        if (packId == _pack.Id) Say($"Added {entry.FileName} from your Downloads folder.");
    }

    /// <summary>Shows the chip while files of this instance wait to be downloaded by hand, with how
    /// many.</summary>
    private async Task UpdateByHandChipAsync()
    {
        int waiting;
        try { waiting = await Task.Run(() => App.State.ManualDownloads.Waiting(_pack.Id).Count); }
        catch (Exception ex)
        {
            AppLog.LogError("manual-downloads", ex);
            waiting = 0;
        }
        ByHandChip.IsVisible = waiting > 0;
        ByHandText.Text = waiting == 1 ? "1 file to download by hand" : $"{waiting} files to download by hand";
    }

    private async void OnOpenManualDownloads(object? sender, RoutedEventArgs e)
    {
        try { await ManualDownloadsCard.ShowAsync(MainWindow.Current, _pack.Id, _pack.Name); }
        catch (Exception ex) { AppLog.LogError("manual-downloads", ex); }
        _ = UpdateByHandChipAsync();
    }

    // ── a mod's actions ──────────────────────────────────────────────────────

    private void OnToggleMod(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { DataContext: PackMod mod } toggle) return;
        var want = toggle.IsChecked == true;
        // Equal means the binding is catching up (a new row, a cascade), not a click.
        if (want == mod.Enabled) return;
        if (!SetModEnabled(mod, want)) toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, mod.Enabled);
    }

    /// <summary>Switches a mod on disk (<c>.jar</c> / <c>.jar.disabled</c>) with the same dependency
    /// cascade as the Windows launcher: switching one off takes what needs it along, switching one on
    /// brings its required dependencies (when the instance's Advanced settings say so).</summary>
    private bool SetModEnabled(PackMod mod, bool enabled)
    {
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
                Say($"Could not turn off {mod.DisplayName}.", danger: true);
                mod.Refresh();
                return false;
            }
            foreach (var m in also) inv.SetEnabled(m, false);
            Say(also.Count == 0
                ? $"Turned off {mod.DisplayName}."
                : $"Turned off {mod.DisplayName} and {ModText.Count(also.Count, "mod that needs it", "mods that need it")}.");
        }
        else
        {
            var deps = adv.AutoDownloadDependencies
                ? ModGraphService.PlanEnable(all, mod)
                : Array.Empty<PackMod>();
            if (!inv.SetEnabled(mod, true))
            {
                Say($"Could not turn on {mod.DisplayName}.", danger: true);
                mod.Refresh();
                return false;
            }
            foreach (var m in deps) inv.SetEnabled(m, true);
            Say(deps.Count == 0
                ? $"Turned on {mod.DisplayName}."
                : $"Turned on {mod.DisplayName} and {ModText.Count(deps.Count, "dependency", "dependencies")}.");
        }
        UpdateSummary();
        return true;
    }

    private void OnUpdateMod(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is PackMod { HasUpdate: true } mod) _ = UpdateModAsync(mod);
    }

    private void OnDeleteMod(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is PackMod mod) _ = DeleteAsync(mod);
    }

    private void OnModMenu(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: PackMod mod } anchor) return;
        var menu = new MenuFlyout();
        if (mod.PageUrl is { } url)
            menu.Items.Add(Item("Open Page", () =>
            {
                if (!SafeLaunch.OpenUrl(url)) Say("Your browser could not be opened.", danger: true);
            }));
        if (mod.HasUpdate && mod.LatestVersion is { } latest)
            menu.Items.Add(Item($"Update to {latest.VersionNumber}", () => _ = UpdateModAsync(mod)));
        menu.Items.Add(Item(mod.Enabled ? "Turn Off" : "Turn On", () => SetModEnabled(mod, !mod.Enabled)));
        menu.Items.Add(Item("Show in Folder", () =>
        {
            if (!SafeLaunch.RevealFile(mod.FilePath)) Say("The folder could not be opened.", danger: true);
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Delete", () => _ = DeleteAsync(mod)));
        menu.ShowAt(anchor);
    }

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception ex) { AppLog.LogError(nameof(InstanceModsView), ex); }
        };
        return item;
    }

    // ── updates ──────────────────────────────────────────────────────────────

    private async Task UpdateModAsync(PackMod mod)
    {
        if (mod.LatestVersion is not { } version) { Say($"{mod.DisplayName} is up to date."); return; }
        if (!await ConfirmUpdateGuardsAsync(mod)) return;

        Say($"Updating {mod.DisplayName}...");
        try
        {
            // Keeps a switched-off mod off, writes only a plain file name, and hands a file CurseForge
            // will not give out to the download-by-hand card (which says so by throwing).
            if (await ModUpdater.InstallVersionAsync(mod, version))
            {
                Say($"Updated {mod.DisplayName}.");
                await ScanModsAsync();
            }
            else Say("No downloadable file.");
        }
        catch (ManualDownloadQueuedException ex) { Say(ex.Message); }
        catch (Exception ex) { Say("Update failed: " + ex.Message, danger: true); }
    }

    /// <summary>Every mod with an update, but the ones locked to their version (the Windows launcher's
    /// bulk update rule: a lone locked mod asks first).</summary>
    private async void OnUpdateAll(object? sender, RoutedEventArgs e)
    {
        var updatable = _rows.Where(m => m.HasUpdate && m.LatestVersion is not null).ToList();
        if (updatable.Count == 0) { Say("Every mod is up to date."); return; }

        var held = updatable.Where(m => m.Meta.UpdateLocked).ToList();
        var run = updatable.Where(m => !m.Meta.UpdateLocked).ToList();
        if (run.Count == 0)
        {
            if (held.Count > 1)
            {
                Say($"All {held.Count} mods with updates are locked: unlock them to update.");
                return;
            }
            if (!await ConfirmLockedAsync(held[0])) return;
            run = held;
            held = new List<PackMod>();
        }

        UpdateAllButton.IsEnabled = false;
        var done = 0;
        var byHand = 0;
        try
        {
            foreach (var m in run)
            {
                Say($"Updating {m.DisplayName}... ({done + byHand + 1} of {run.Count})");
                try { if (await ModUpdater.InstallVersionAsync(m, m.LatestVersion!)) done++; }
                catch (ManualDownloadQueuedException) { byHand++; }
                catch (Exception ex)
                {
                    AppLog.LogError(nameof(InstanceModsView), ex);
                    Say($"Updating {m.DisplayName} failed: {ex.Message}", danger: true);
                }
            }
            Say($"Updated {ModText.Count(done, "mod", "mods")}" + (held.Count > 0 ? $", skipped {held.Count} locked." : ".")
                + (byHand == 0 ? "" : $" {byHand} to download by hand from CurseForge's website: see the window that opened."));
        }
        finally { UpdateAllButton.IsEnabled = true; }
        await ScanModsAsync();
    }

    /// <summary>The lock and the update-incompatible flag are set in the Windows launcher's Modpack
    /// Management; this list honours them and lets the user override once.</summary>
    private async Task<bool> ConfirmUpdateGuardsAsync(PackMod mod)
    {
        if (mod.Meta.UpdateLocked && !await ConfirmLockedAsync(mod)) return false;
        if (mod.Meta.UpdateIncompatible && App.State.ModMetadata.Advanced(_pack.Id).WarnOnUpdateIncompatible
            && !await MainWindow.Current.ConfirmAsync("Update warning",
                $"{mod.DisplayName} is marked as update-incompatible: updating it may break your setup.\n\nUpdate anyway?",
                "Update", "Cancel", danger: true))
            return false;
        return true;
    }

    private Task<bool> ConfirmLockedAsync(PackMod mod) =>
        MainWindow.Current.ConfirmAsync("Mod is locked",
            $"{mod.DisplayName} is locked to its current version, so bulk updates skip it.\n\n"
            + "Update it anyway? It stays locked afterwards.",
            "Update Anyway", "Keep Locked");

    // ── delete ───────────────────────────────────────────────────────────────

    private async Task DeleteAsync(PackMod mod)
    {
        if (!await MainWindow.Current.ConfirmAsync("Delete mod",
                $"Delete {Path.GetFileName(mod.FilePath)}?", "Delete", "Cancel", danger: true))
            return;

        // What needs the mod goes off with it, as on Windows, so the instance still starts.
        var adv = App.State.ModMetadata.Advanced(_pack.Id);
        if (adv.CascadeDisableDependents)
            foreach (var dep in ModGraphService.PlanDisable(_rows, mod, adv.CascadeDisableLibraries).AlsoDisable)
                App.State.ModInventory.SetEnabled(dep, false);
        try
        {
            File.Delete(mod.FilePath);
            Say($"Deleted {mod.DisplayName}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Say($"{mod.DisplayName} could not be deleted: {ex.Message}", danger: true);
        }
        await ScanModsAsync();
    }
}
