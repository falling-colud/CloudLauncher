using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// The unified mod management page (List / Graph / Mod browsing sub-tabs). Opened from the Mod
/// view's "Mods Management" button. All three sub-tabs read the same <see cref="PackModInventory"/>.
/// </summary>
public partial class ModManagementView : Page, ISidePanelBackHandler
{
    private readonly MainWindow _shell;
    private readonly PackDetail _pack;
    private List<PackMod> _all = new();
    private int _gen;
    private bool _loaded;
    private bool _graphLoaded;
    private bool _identifying;
    private ModGraphView? _graph;
    private ModExplorerPage? _browse;

    // Most recent launch-log lines, capped so the bottom status strip shows at most 3 at a time.
    private readonly LinkedList<string> _statusTail = new();

    public ModManagementView(MainWindow shell, PackDetail pack)
    {
        InitializeComponent();
        _shell = shell;
        _pack = pack;
        PackNameLabel.Text = pack.Name;
        LoadAdvancedSettings();

        GraphHost.Children.Add(new TextBlock
        {
            Text = "Open this tab to view the dependency / category graph.",
            Style = (Style)FindResource("Muted"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });

        App.State.Instances.StateChanged += OnInstanceStateChanged;
        Unloaded += (_, _) => App.State.Instances.StateChanged -= OnInstanceStateChanged;
        UpdateLaunchButton();

        Loaded += (_, _) =>
        {
            if (!_loaded) { _loaded = true; _ = ReloadAsync(); }
            UiScale.Changed -= ApplyModScale;   // re-apply the mod-list zoom live when the slider moves
            UiScale.Changed += ApplyModScale;
            ApplyModScale();
        };
        Unloaded += (_, _) => UiScale.Changed -= ApplyModScale;
    }

    private void ApplyModScale() => UiScale.ApplyModListScale(ModItems);

    // â”€â”€ launch the pack â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void OnInstanceStateChanged(Guid packId)
    {
        if (packId == _pack.Id) Dispatcher.Invoke(UpdateLaunchButton);
    }

    private void UpdateLaunchButton()
    {
        var status = App.State.Instances.GetStatus(_pack.Id);
        LaunchButton.IsEnabled = status != MinecraftInstanceStatus.Launching;
        LaunchButton.Content = status switch
        {
            MinecraftInstanceStatus.Running => "Stop",
            MinecraftInstanceStatus.Launching => "Launchingâ€¦",
            _ => "Launch"
        };
    }

    private async void OnLaunch(object sender, RoutedEventArgs e)
    {
        if (App.State.Instances.IsBusy(_pack.Id)) { App.State.Instances.Stop(_pack.Id); UpdateLaunchButton(); return; }

        if (App.State.MinecraftAccounts.Current is null)
        {
            await AppDialog.MessageAsync(_shell, "Launch",
                "Set up a Minecraft account first â€” click the account chip in the title bar.");
            _shell.OpenMcAccount();
            return;
        }

        LaunchButton.IsEnabled = false;
        ListStatus.Text = "Checking Javaâ€¦";
        var (javaOk, javaMsg) = await LaunchService.CheckJavaAsync(_pack.MinecraftVersion);
        if (!javaOk)
        {
            UpdateLaunchButton();
            ListStatus.Text = javaMsg;
            await AppDialog.MessageAsync(_shell, "Java check", javaMsg);
            return;
        }

        try
        {
            _statusTail.Clear();
            var proc = await App.State.Launcher.LaunchTrackedAsync(_pack, new Progress<string>(ReportLaunchStatus));
            ReportLaunchStatus($"Running (PID {proc.Id}).");
            _shell.OpenMinecraftHost(_pack, proc);
        }
        catch (OperationCanceledException) { ListStatus.Text = "Launch cancelled."; ProgressHub.Clear(_pack.Id); }
        catch (Exception ex) { ListStatus.Text = "Launch failed: " + ex.Message; }
        finally { UpdateLaunchButton(); }
    }

    /// <summary>Shows launch progress in the bottom strip, keeping only the most recent 3 lines. The
    /// game's output arrives as 150 ms-buffered multi-line blobs, so we split and roll them.</summary>
    private void ReportLaunchStatus(string blob)
    {
        foreach (var raw in blob.Split('\n'))
        {
            var line = raw.TrimEnd('\r', ' ');
            if (line.Length == 0) continue;
            _statusTail.AddLast(line);
            while (_statusTail.Count > 3) _statusTail.RemoveFirst();
        }
        ListStatus.Text = string.Join(Environment.NewLine, _statusTail);
    }

    /// <summary>The mods currently loaded (used by the Graph view in a later phase).</summary>
    internal IReadOnlyList<PackMod> Mods => _all;

    // â”€â”€ load â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private async Task ReloadAsync()
    {
        var gen = ++_gen;
        // Undo any leftover test-scope from a launcher that was killed mid Run-as-Test
        // (only when nothing is running, so we never reshuffle a live game's mods).
        if (App.State.Instances.GetStatus(_pack.Id) == MinecraftInstanceStatus.Idle)
            App.State.TestScope.RestoreIfPending(_pack.Id);

        ListStatus.Text = "Scanning modsâ€¦";
        try
        {
            var mods = await App.State.ModInventory.LoadAsync(_pack.Id, _pack.IsShared);
            if (gen != _gen) return;
            _all = mods;
            App.State.ModMetadata.SyncManagedCategories(_pack.Id); // library mods â†’ managed "Library" category
            _identifying = true;
            ApplyFilterSort();                       // instant: file names + saved flags
            _graph?.Load(_pack.Id, _all, _shell, OpenModPage, RequestReload);
            _ = EnrichAsync(gen);                    // background: identities, icons, updates
        }
        catch (Exception ex)
        {
            ListStatus.Text = "Failed to scan mods: " + ex.Message;
        }
    }

    /// <summary>Background second pass: resolve store identities (hashing + matching), then the
    /// update check. The list is already on screen, so this just enriches it in place.</summary>
    private async Task EnrichAsync(int gen)
    {
        try { await App.State.ModInventory.ResolveIdentitiesAsync(_pack.Id, _all); }
        catch { /* offline â€” keep the file-name view */ }
        if (gen != _gen) return;
        _identifying = false;
        ApplyFilterSort();
        _graph?.Load(_pack.Id, _all, _shell);
        await CheckUpdatesAsync(gen);
    }

    private void ApplyFilterSort()
    {
        IEnumerable<PackMod> q = _all;

        var query = SearchBox.Text?.Trim();
        if (!string.IsNullOrEmpty(query))
            q = q.Where(m =>
                m.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || m.VersionLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
                || m.SourceLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
                || m.CategoriesLabel.Contains(query, StringComparison.OrdinalIgnoreCase));

        if (HideDisabled.IsChecked == true) q = q.Where(m => m.Enabled);
        if (OnlyUpdates.IsChecked == true) q = q.Where(m => m.HasUpdate);

        var cmp = StringComparer.OrdinalIgnoreCase;
        var rev = _reverseSort;
        // Primary key by sort mode (with its natural direction, flipped when reversed).
        // Priority is always the secondary tiebreaker so e.g. within each Status group the
        // highest-priority mods float to the top; name is the final tiebreaker.
        IOrderedEnumerable<PackMod> ordered = SortBox.SelectedIndex switch
        {
            // Name: Aâ†’Z by default
            1 => rev ? q.OrderByDescending(m => m.DisplayName, cmp) : q.OrderBy(m => m.DisplayName, cmp),
            // Add date: newest first by default
            2 => rev ? q.OrderBy(m => m.AddedAt) : q.OrderByDescending(m => m.AddedAt),
            // Update date: mods with an available update first by default
            3 => rev ? q.OrderBy(m => m.HasUpdate) : q.OrderByDescending(m => m.HasUpdate),
            // Status: disabled first by default (Enabled ascending)
            4 => rev ? q.OrderByDescending(m => m.Enabled) : q.OrderBy(m => m.Enabled),
            // Priority: highest first by default
            _ => rev ? q.OrderBy(m => m.Priority) : q.OrderByDescending(m => m.Priority),
        };
        q = ordered.ThenByDescending(m => m.Priority).ThenBy(m => m.DisplayName, cmp);

        var list = q.ToList();
        ModItems.ItemsSource = list;
        PlayEntranceOnce();
        UpdateSelectionStatus();
        ModCountLabel.Text = $"  Â·  {_all.Count} mod{(_all.Count == 1 ? "" : "s")}";

        if (_all.Count == 0) { ListStatus.Text = "No mods installed."; return; }
        var disabled = _all.Count(m => !m.Enabled);
        var updates = _all.Count(m => m.HasUpdate);
        var status = $"{_all.Count} mod(s)";
        if (disabled > 0) status += $"  Â·  {disabled} disabled";
        if (updates > 0) status += $"  Â·  {updates} update(s)";
        if (list.Count != _all.Count) status += $"  Â·  showing {list.Count}";
        if (_identifying) status += "  Â·  identifyingâ€¦";
        ListStatus.Text = status;
    }

    private async Task CheckUpdatesAsync(int gen)
    {
        foreach (var mod in _all.ToList())
        {
            if (gen != _gen) return;
            if (mod.PrimaryMod is null || mod.PrimaryVersion is null) continue;
            var latest = await TryGetLatestCompatibleAsync(mod.PrimaryMod, mod.PrimaryVersion);
            if (gen != _gen) return;
            if (latest is not null) mod.LatestVersion = latest;
        }
        if (gen == _gen) ApplyFilterSort();
    }

    private async Task<ModVersion?> TryGetLatestCompatibleAsync(ModSummary mod, ModVersion installed)
    {
        try
        {
            var mc = _pack.MinecraftVersion;
            var loader = _pack.Loader == LoaderKind.None ? null : _pack.Loader.ToString().ToLowerInvariant();
            var versions = mod.Source == ModSource.CurseForge && int.TryParse(mod.Id, out var cfId)
                ? (await App.State.CurseForge.GetVersionsAsync(cfId)).Where(v => IsCompatible(v, mc, loader)).ToList()
                : await App.State.Modrinth.GetVersionsAsync(mod.Id, mc, loader);

            // Only flag a version that is strictly NEWER than the installed file and at least as
            // stable a channel â€” never a downgrade, and never a "more stable but older" version.
            static int Rank(string? ch) => ch?.ToLowerInvariant() switch { "release" => 2, "beta" => 1, _ => 0 };
            var installedRank = Rank(installed.ReleaseChannel);
            return versions
                .Where(v => v.Id != installed.Id
                            && v.DatePublished > installed.DatePublished
                            && Rank(v.ReleaseChannel) >= installedRank)
                .OrderByDescending(v => v.DatePublished)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private static bool IsCompatible(ModVersion v, string? mc, string? loader)
    {
        var okMc = string.IsNullOrEmpty(mc) || v.GameVersions.Any(g => string.Equals(g, mc, StringComparison.OrdinalIgnoreCase));
        var okLoader = string.IsNullOrEmpty(loader) || v.Loaders.Any(l => string.Equals(l, loader, StringComparison.OrdinalIgnoreCase));
        return okMc && okLoader;
    }

    // â”€â”€ tab lazy-load â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private int _lastTab;

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource is not TabControl) return; // ignore inner ComboBox bubbling

        var from = _lastTab;
        var to = Tabs.SelectedIndex;
        _lastTab = to;

        if (to == 1)
        {
            if (!_graphLoaded)
            {
                _graphLoaded = true;
                _graph = new ModGraphView();
                GraphHost.Children.Clear();
                GraphHost.Children.Add(_graph);
                _graph.Load(_pack.Id, _all, _shell, OpenModPage, RequestReload);
            }
            else
            {
                // Re-render so edits made in the List view (flags, categories, enable/disable) show up.
                _graph?.Load(_pack.Id, _all, _shell, OpenModPage, RequestReload);
            }
        }
        else if (to == 0)
        {
            // Pick up edits made in the Graph view (same mod instances, just re-render the list).
            ApplyFilterSort();
        }
        else if (to == 2)
        {
            EnsureBrowse();
        }

        // Coming back from the browser to List/Graph: mods may have been downloaded â€” re-scan.
        if (from == 2 && (to == 0 || to == 1))
            _ = ReloadAsync();
    }

    private void RequestReload() => _ = ReloadAsync();

    // â”€â”€ back navigation from the in-launcher mod page â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private int _returnTab = -1;

    /// <summary>Side-panel back button: if we're showing a mod page reached by clicking a mod,
    /// go back to the tab we came from instead of closing the whole management panel.</summary>
    public bool TryHandleBack()
    {
        if (Tabs.SelectedIndex == 2 && _returnTab >= 0)
        {
            Tabs.SelectedIndex = _returnTab;
            _returnTab = -1;
            return true;
        }
        return false;
    }

    private ModExplorerPage EnsureBrowse()
    {
        if (_browse is null)
        {
            _browse = new ModExplorerPage(_shell, _pack);
            BrowseFrame.Navigate(_browse);
        }
        return _browse;
    }

    /// <summary>Opens a mod's page inside the launcher â€” switches to the Mod browsing sub-tab and
    /// shows the mod in its detail panel (not the external website).</summary>
    private void OpenModPage(PackMod mod) => _ = OpenModPageAsync(mod);

    private async Task OpenModPageAsync(PackMod mod)
    {
        var summary = mod.PrimaryMod;
        if (summary is null)
        {
            ListStatus.Text = $"{mod.DisplayName} isn't identified yet â€” try again in a moment.";
            return;
        }
        var browse = EnsureBrowse();
        if (Tabs.SelectedIndex != 2) _returnTab = Tabs.SelectedIndex; // remember where to return on Back
        Tabs.SelectedIndex = 2;
        try { await browse.ShowModAsync(summary); }
        catch (Exception ex) { ListStatus.Text = ex.Message; }
    }

    // â”€â”€ toolbar handlers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void OnSearchChanged(object s, TextChangedEventArgs e)
    {
        UpdateSearchPlaceholder();
        if (IsLoaded) ApplyFilterSort();
    }

    private void OnSearchFocusChanged(object sender, KeyboardFocusChangedEventArgs e) => UpdateSearchPlaceholder();

    // â”€â”€ one-time entrance animation â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // Stagger the cards in only on the first build of the list. Later edits (enable/disable,
    // flag changes) and filter/sort changes just update in place â€” they never re-animate.

    private bool _entrancePlayed;

    private void PlayEntranceOnce()
    {
        if (_entrancePlayed) return;
        var gen = ModItems.ItemContainerGenerator;
        if (gen.Status != GeneratorStatus.ContainersGenerated)
        {
            void Wait(object? s, EventArgs e)
            {
                if (gen.Status != GeneratorStatus.ContainersGenerated) return;
                gen.StatusChanged -= Wait;
                PlayEntranceOnce();
            }
            gen.StatusChanged += Wait;
            return;
        }
        if (ModItems.Items.Count == 0) return; // nothing to animate yet; play on a later populate

        _entrancePlayed = true;
        var order = 0;
        for (var i = 0; i < ModItems.Items.Count; i++)
            if (gen.ContainerFromIndex(i) is FrameworkElement c)
                Stagger(c, order++);
    }

    private static void Stagger(FrameworkElement c, int order)
    {
        var begin = TimeSpan.FromMilliseconds(Math.Min(order, 14) * 22);
        var dur = TimeSpan.FromMilliseconds(280);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var tt = new TranslateTransform(0, 12);
        c.RenderTransform = tt;
        c.Opacity = 0;

        var fade = new DoubleAnimation(0, 1, dur) { BeginTime = begin, EasingFunction = ease };
        fade.Completed += (_, _) => { c.BeginAnimation(UIElement.OpacityProperty, null); c.Opacity = 1; };
        c.BeginAnimation(UIElement.OpacityProperty, fade);

        var rise = new DoubleAnimation(12, 0, dur) { BeginTime = begin, EasingFunction = ease };
        rise.Completed += (_, _) => { tt.BeginAnimation(TranslateTransform.YProperty, null); tt.Y = 0; };
        tt.BeginAnimation(TranslateTransform.YProperty, rise);
    }

    // Hide the placeholder while the box is focused so the caret doesn't sit on top of the text.
    private void UpdateSearchPlaceholder()
    {
        if (SearchPlaceholder is null) return;
        SearchPlaceholder.Visibility =
            string.IsNullOrEmpty(SearchBox.Text) && !SearchBox.IsKeyboardFocused
                ? Visibility.Visible : Visibility.Collapsed;
    }
    private void OnSortChanged(object s, SelectionChangedEventArgs e) { if (IsLoaded) ApplyFilterSort(); }

    private bool _reverseSort;

    private void OnToggleSortDir(object s, RoutedEventArgs e)
    {
        _reverseSort = !_reverseSort;
        // Chevron points the way the list runs: down = natural order, up = reversed.
        SortDirButton.Content = _reverseSort ? "" : "";
        if (IsLoaded) ApplyFilterSort();
    }
    private void OnFilterToggle(object s, RoutedEventArgs e) { if (IsLoaded) ApplyFilterSort(); }
    private async void OnRefresh(object s, RoutedEventArgs e) => await ReloadAsync();

    private async void OnRunTest(object s, RoutedEventArgs e)
    {
        var testMods = _all.Where(m => m.IsTesting).ToList();
        if (testMods.Count == 0)
        {
            await AppDialog.MessageAsync(_shell, "Run as Test",
                "No mods are marked as testing. Right-click a mod and choose Testing to add it to the test set.");
            return;
        }

        if (App.State.Instances.GetStatus(_pack.Id) != MinecraftInstanceStatus.Idle)
        {
            await AppDialog.MessageAsync(_shell, "Run as Test", "Close the running instance before starting a test launch.");
            return;
        }

        // Test closure = the test-marked mods plus everything they depend on.
        var closure = ModGraph.Build(_all).Closure(testMods);
        var testNames = new HashSet<string>(closure.Select(m => m.FileName), StringComparer.OrdinalIgnoreCase);
        var depCount = closure.Count - testMods.Count;

        var confirm = await AppDialog.ConfirmAsync(_shell, "Run as Test",
            $"Launch with only {testMods.Count} test mod(s)" + (depCount > 0 ? $" and {depCount} dependency(ies)" : "") +
            "?\n\nYour full mod set is restored automatically when the game closes.",
            "Launch", "Cancel");
        if (!confirm) return;

        RunTestButton.IsEnabled = false;
        try
        {
            App.State.TestScope.Apply(_pack.Id, testNames);
            _statusTail.Clear();
            ReportLaunchStatus("Launching test setâ€¦");
            var proc = await App.State.Launcher.LaunchTrackedAsync(_pack, new Progress<string>(ReportLaunchStatus));

            void RestoreAfterTest()
            {
                App.State.TestScope.Restore(_pack.Id);
                RunTestButton.IsEnabled = true;
                ListStatus.Text = "Test run finished â€” full mod set restored.";
                _ = ReloadAsync();
            }

            try { proc.EnableRaisingEvents = true; } catch { }
            proc.Exited += (_, _) => Dispatcher.Invoke(RestoreAfterTest);
            if (proc.HasExited) Dispatcher.Invoke(RestoreAfterTest); // exited during setup
        }
        catch (Exception ex)
        {
            App.State.TestScope.Restore(_pack.Id);
            RunTestButton.IsEnabled = true;
            ListStatus.Text = "Test launch failed: " + ex.Message;
        }
    }

    // â”€â”€ advanced settings (Â§7) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void LoadAdvancedSettings()
    {
        var adv = App.State.ModMetadata.Advanced(_pack.Id);
        AdvAutoDeps.IsChecked = adv.AutoDownloadDependencies;
        AdvCascadeDependents.IsChecked = adv.CascadeDisableDependents;
        AdvCascadeLibraries.IsChecked = adv.CascadeDisableLibraries;
        AdvWarnUpdate.IsChecked = adv.WarnOnUpdateIncompatible;
        AdvShowLines.IsChecked = adv.ShowDependencyLines;
    }

    private void OnAdvChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var adv = App.State.ModMetadata.Advanced(_pack.Id);
        adv.AutoDownloadDependencies = AdvAutoDeps.IsChecked == true;
        adv.CascadeDisableDependents = AdvCascadeDependents.IsChecked == true;
        adv.CascadeDisableLibraries = AdvCascadeLibraries.IsChecked == true;
        adv.WarnOnUpdateIncompatible = AdvWarnUpdate.IsChecked == true;
        adv.ShowDependencyLines = AdvShowLines.IsChecked == true;
        App.State.ModMetadata.SaveAdvanced(_pack.Id);
    }

    // â”€â”€ Files-tab mod tools (Â§6) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void OnSideTools(object s, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = s as UIElement };

        void Add(string header, Action action, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }

        Add("Route server-only mods to local/", RouteServerToLocal);
        menu.Items.Add(new Separator());
        Add("Enable all â€œextraâ€ mods", () => SetExtras(true));
        Add("Disable all â€œextraâ€ mods", () => SetExtras(false));
        menu.Items.Add(new Separator());
        Add("Export server + client mod folder", ExportSideFolder, _pack.IsShared);
        menu.IsOpen = true;
    }

    /// <summary>Move server-only mods out of the synced game/ folder into per-user local/ so they
    /// don't ship to clients. (Â§6: server-marked mods can be set to local.)</summary>
    private async void RouteServerToLocal()
    {
        var localMods = Path.Combine(App.State.Packs.LocalDir(_pack.Id), "mods");
        Directory.CreateDirectory(localMods);
        var moved = 0;
        foreach (var m in _all.Where(m => m.Side == ModSide.Server && !m.IsLocal))
        {
            try
            {
                var dest = Path.Combine(localMods, Path.GetFileName(m.FilePath));
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(m.FilePath, dest);
                moved++;
            }
            catch { /* skip locked */ }
        }
        ListStatus.Text = moved == 0 ? "No server-only mods in game/ to route." : $"Moved {moved} server-only mod(s) to local/.";
        await ReloadAsync();
    }

    private async void SetExtras(bool enabled)
    {
        var n = 0;
        foreach (var m in _all.Where(m => m.IsExtra && m.Enabled != enabled))
        {
            App.State.ModInventory.SetEnabled(m, enabled);
            n++;
        }
        ListStatus.Text = n == 0 ? "No â€œextraâ€ mods to change." : $"{(enabled ? "Enabled" : "Disabled")} {n} extra mod(s).";
        await ReloadAsync();
    }

    /// <summary>Copy every side-marked (client/server) mod into a side-mods/ folder split by side â€”
    /// only when sharing is enabled (Â§6).</summary>
    private void ExportSideFolder()
    {
        if (!_pack.IsShared)
        {
            ListStatus.Text = "Enable sharing for this pack before exporting side folders.";
            return;
        }
        var root = Path.Combine(App.State.Packs.PackRoot(_pack.Id), "side-mods");
        var serverDir = Path.Combine(root, "server");
        var clientDir = Path.Combine(root, "client");
        Directory.CreateDirectory(serverDir);
        Directory.CreateDirectory(clientDir);

        var n = 0;
        foreach (var m in _all.Where(m => m.IsSideRestricted && m.Enabled))
        {
            try
            {
                var dir = m.Side == ModSide.Server ? serverDir : clientDir;
                File.Copy(m.FilePath, Path.Combine(dir, Path.GetFileName(m.FilePath)), overwrite: true);
                n++;
            }
            catch { /* skip locked */ }
        }
        ListStatus.Text = $"Exported {n} side-marked mod(s) to side-mods/.";
        try { Process.Start(new ProcessStartInfo(root) { UseShellExecute = true }); } catch { }
    }

    // â”€â”€ card / row handlers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static PackMod? ModOf(object sender) => (sender as FrameworkElement)?.DataContext as PackMod;

    // Left-clicking a card intentionally does nothing â€” the mod page is opened from the
    // right-click options menu ("Open page", the top item) instead.

    private void OnCardRightClick(object sender, MouseButtonEventArgs e)
    {
        if (ModOf(sender) is not { } mod) return;
        ShowOptions(mod, sender as FrameworkElement);
        e.Handled = true;
    }

    private void OnOptions(object sender, RoutedEventArgs e)
    {
        if (ModOf(sender) is { } mod) ShowOptions(mod, sender as FrameworkElement);
    }

    private void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        if (ModOf(sender) is { } mod) _ = UpdateModAsync(mod);
    }

    private void OnToggleEnabled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb || ModOf(tb) is not { } mod) return;
        var want = tb.IsChecked == true;
        if (want == mod.Enabled) return;
        _ = SetEnabledAsync(mod, want);
    }

    private void ShowOptions(PackMod mod, FrameworkElement? anchor)
    {
        var menu = ModOptionsMenu.Build(TargetsFor(mod), BuildCtx());
        menu.PlacementTarget = anchor;
        menu.IsOpen = true;
    }

    private ModOptionsContext BuildCtx() => new()
    {
        PackId = _pack.Id,
        Inventory = App.State.ModInventory,
        Owner = _shell,
        AllMods = _all,
        OnChanged = ApplyFilterSort,
        OnOpenPage = OpenModPage,
        OnUpdate = list => _ = UpdateManyAsync(list),
        OnUpdateToVersion = m => _ = UpdateToVersionAsync(m),
        OnDelete = list => _ = DeleteManyAsync(list),
        OnReveal = Reveal,
        OnSetEnabled = (list, en) => _ = SetEnabledManyAsync(list, en),
    };

    // â”€â”€ multi-select â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>The mods a right-click acts on: the whole selection when the clicked mod is part of
    /// it (Explorer-style), otherwise just the clicked mod.</summary>
    private IReadOnlyList<PackMod> TargetsFor(PackMod mod)
    {
        var selected = _all.Where(m => m.IsSelected).ToList();
        return selected.Count > 0 && mod.IsSelected ? selected : new List<PackMod> { mod };
    }

    private void OnSelectToggle(object sender, RoutedEventArgs e) => UpdateSelectionStatus();

    private void OnClearSelection(object sender, RoutedEventArgs e)
    {
        foreach (var m in _all) m.IsSelected = false;
        UpdateSelectionStatus();
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        var shown = ModItems.ItemsSource as IEnumerable<PackMod> ?? _all;
        foreach (var m in shown) m.IsSelected = true;
        UpdateSelectionStatus();
    }

    private void UpdateSelectionStatus()
    {
        if (SelectionBar is null) return;
        var n = _all.Count(m => m.IsSelected);
        SelectionBar.Visibility = n == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (n > 0) SelectionLabel.Text = $"{n} selected â€” right-click a selected mod to change all";
    }

    private async Task SetEnabledManyAsync(IReadOnlyList<PackMod> mods, bool enabled)
    {
        foreach (var m in mods.ToList()) await SetEnabledAsync(m, enabled);
        ApplyFilterSort();
    }

    private async Task UpdateManyAsync(IReadOnlyList<PackMod> mods)
    {
        var updatable = mods.Where(m => m.HasUpdate && m.LatestVersion is not null).ToList();
        if (updatable.Count == 0) { ListStatus.Text = "No updates available for the selection."; return; }
        var done = 0;
        foreach (var m in updatable)
        {
            ListStatus.Text = $"Updating {m.DisplayName}â€¦";
            try { if (await ModUpdater.InstallVersionAsync(m, m.LatestVersion!)) done++; }
            catch (Exception ex) { ListStatus.Text = "Update failed: " + ex.Message; }
        }
        ListStatus.Text = $"Updated {done} mod(s).";
        await ReloadAsync();
    }

    private async Task DeleteManyAsync(IReadOnlyList<PackMod> mods)
    {
        if (mods.Count == 0) return;
        var msg = mods.Count == 1
            ? $"Delete {Path.GetFileName(mods[0].FilePath)}?"
            : $"Delete {mods.Count} mods?";
        if (!await AppDialog.ConfirmAsync(_shell, "Delete mods", msg, "Delete", "Cancel", danger: true)) return;

        var adv = App.State.ModMetadata.Advanced(_pack.Id);
        foreach (var m in mods)
        {
            if (adv.CascadeDisableDependents)
                foreach (var dep in ModGraphService.PlanDisable(_all, m, adv.CascadeDisableLibraries).AlsoDisable)
                    App.State.ModInventory.SetEnabled(dep, false);
            try { File.Delete(m.FilePath); } catch { }
        }
        await ReloadAsync();
    }

    // â”€â”€ actions â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void Reveal(PackMod mod)
    {
        var dir = Path.GetDirectoryName(mod.FilePath);
        if (dir is null) return;
        try { Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true }); }
        catch (Exception ex) { ListStatus.Text = ex.Message; }
    }

    private Task SetEnabledAsync(PackMod mod, bool enabled)
    {
        var adv = App.State.ModMetadata.Advanced(_pack.Id);
        var inv = App.State.ModInventory;

        if (!enabled)
        {
            // Disable the mod and cascade to everything that depends on it (recursively),
            // plus any library dependencies that nothing else still needs.
            var also = adv.CascadeDisableDependents
                ? ModGraphService.PlanDisable(_all, mod, adv.CascadeDisableLibraries).AlsoDisable
                : Array.Empty<PackMod>();

            if (!inv.SetEnabled(mod, false))
            {
                ListStatus.Text = $"Could not disable {mod.DisplayName}.";
                mod.Refresh();
                return Task.CompletedTask;
            }
            foreach (var m in also) inv.SetEnabled(m, false);
            ListStatus.Text = also.Count == 0
                ? $"Disabled {mod.DisplayName}."
                : $"Disabled {mod.DisplayName} and {also.Count} dependent(s).";
        }
        else
        {
            // Enabling a mod pulls its required dependencies back on so it never runs half-installed.
            var deps = ModGraphService.PlanEnable(_all, mod);
            if (!inv.SetEnabled(mod, true))
            {
                ListStatus.Text = $"Could not enable {mod.DisplayName}.";
                mod.Refresh();
                return Task.CompletedTask;
            }
            foreach (var m in deps) inv.SetEnabled(m, true);
            ListStatus.Text = deps.Count == 0
                ? $"Enabled {mod.DisplayName}."
                : $"Enabled {mod.DisplayName} and {deps.Count} dependency(ies).";
        }

        ApplyFilterSort();
        return Task.CompletedTask;
    }

    private async Task UpdateModAsync(PackMod mod)
    {
        if (mod.LatestVersion is null) { ListStatus.Text = $"{mod.DisplayName} is up to date."; return; }

        // Warn before updating a mod the user flagged as update-incompatible (Â§7).
        if (mod.Meta.UpdateIncompatible && App.State.ModMetadata.Advanced(_pack.Id).WarnOnUpdateIncompatible)
        {
            var go = await AppDialog.ConfirmAsync(_shell, "Update warning",
                $"{mod.DisplayName} is marked as update-incompatible â€” updating it may break your setup.\n\nUpdate anyway?",
                "Update", "Cancel", danger: true);
            if (!go) return;
        }

        ListStatus.Text = $"Updating {mod.DisplayName}â€¦";
        try
        {
            var file = mod.LatestVersion.Files.FirstOrDefault(f => f.IsPrimary) ?? mod.LatestVersion.Files.FirstOrDefault();
            if (file is null) { ListStatus.Text = "No downloadable file."; return; }
            file = await EnsureDownloadableAsync(mod.LatestVersion, file);
            if (string.IsNullOrWhiteSpace(file.DownloadUrl)) { ListStatus.Text = "No downloadable file."; return; }

            var dest = Path.Combine(Path.GetDirectoryName(mod.FilePath)!, file.Filename);
            await App.State.Modrinth.DownloadFileAsync(file.DownloadUrl, dest);
            if (!string.Equals(mod.FilePath, dest, StringComparison.OrdinalIgnoreCase) && File.Exists(mod.FilePath))
                File.Delete(mod.FilePath);
            ListStatus.Text = $"Updated {mod.DisplayName}.";
            await ReloadAsync();
        }
        catch (Exception ex) { ListStatus.Text = "Update failed: " + ex.Message; }
    }

    private static async Task<ModVersionFile> EnsureDownloadableAsync(ModVersion version, ModVersionFile file)
    {
        if (version.Source != ModSource.CurseForge || !string.IsNullOrWhiteSpace(file.DownloadUrl)) return file;
        var ids = version.Id.Split(':');
        if (ids.Length != 2 || !int.TryParse(ids[0], out var modId) || !int.TryParse(ids[1], out var fileId)) return file;
        var url = await App.State.CurseForge.GetDownloadUrlAsync(modId, fileId);
        return string.IsNullOrWhiteSpace(url) ? file : file with { DownloadUrl = url };
    }

    /// <summary>Pick a specific version (any channel incl. beta/alpha) and install it.</summary>
    private async Task UpdateToVersionAsync(PackMod mod)
    {
        if (mod.PrimaryMod is null) { ListStatus.Text = $"{mod.DisplayName} isn't identified yet."; return; }

        ListStatus.Text = $"Loading versions for {mod.DisplayName}â€¦";
        var versions = await ModUpdater.FetchVersionsAsync(mod.PrimaryMod);
        if (versions.Count == 0) { ListStatus.Text = $"No versions found for {mod.DisplayName}."; return; }

        var loader = _pack.Loader == LoaderKind.None ? null : _pack.Loader.ToString().ToLowerInvariant();
        var chosen = await ModVersionPickerDialog.ShowAsync(_shell, mod.DisplayName, versions, _pack.MinecraftVersion, loader,
            mod.PrimaryVersion?.Id, mod.PrimaryVersion?.VersionNumber);
        if (chosen is null) { ListStatus.Text = ""; return; }

        // Update-incompatible warning still applies when the user picks a version manually.
        if (mod.Meta.UpdateIncompatible && App.State.ModMetadata.Advanced(_pack.Id).WarnOnUpdateIncompatible)
        {
            var go = await AppDialog.ConfirmAsync(_shell, "Update warning",
                $"{mod.DisplayName} is marked as update-incompatible â€” changing its version may break your setup.\n\nContinue?",
                "Install", "Cancel", danger: true);
            if (!go) return;
        }

        ListStatus.Text = $"Installing {mod.DisplayName} {chosen.VersionNumber}â€¦";
        try
        {
            if (await ModUpdater.InstallVersionAsync(mod, chosen))
            {
                ListStatus.Text = $"Installed {mod.DisplayName} {chosen.VersionNumber}.";
                await ReloadAsync();
            }
            else ListStatus.Text = "That version has no downloadable file.";
        }
        catch (Exception ex) { ListStatus.Text = "Install failed: " + ex.Message; }
    }

}
