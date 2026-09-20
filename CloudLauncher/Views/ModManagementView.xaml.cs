using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
    // Sub-tab indices — named so adding a tab can't silently shift the lazy-load / back-navigation
    // logic underneath them.
    private const int TabList = 0, TabBrowse = 1, TabGraph = 2, TabCategories = 3, TabPlan = 4;

    private readonly MainWindow _shell;
    private readonly PackDetail _pack;
    private List<PackMod> _all = new();
    private int _gen;
    private bool _loaded;
    private bool _graphLoaded;
    private bool _planLoaded;
    private bool _categoriesLoaded;
    // The secondary views (graph / planning / categories) rebuild only when their data is stale.
    private bool _graphDirty = true, _planDirty = true, _categoriesDirty = true;
    private bool _identifying;
    private ModGraphView? _graph;
    private ModPlanView? _plan;
    private ModCategoriesView? _categories;
    private ModExplorerPage? _browse;

    // Most recent launch-log lines, capped so the bottom status strip shows at most 3 at a time.
    private readonly LinkedList<string> _statusTail = new();

    public ModManagementView(MainWindow shell, PackDetail pack)
    {
        InitializeComponent();
        _shell = shell;
        _pack = pack;
        PackNameLabel.Text = pack.Name;
        RestoreListPreferences();
        LoadAdvancedSettings();

        GraphHost.Children.Add(new TextBlock
        {
            Text = "Open this tab to view the dependency / category graph.",
            Style = (Style)FindResource("Muted"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        CategoriesHost.Children.Add(new TextBlock
        {
            Text = "Open this tab to organise categories.",
            Style = (Style)FindResource("Muted"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        PlanHost.Children.Add(new TextBlock
        {
            Text = "Open this tab to plan the modpack on a board.",
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
            SyncLauncherDefaults();
            UiScale.Changed -= ApplyModScale;   // re-apply the mod-list zoom live when the slider moves
            UiScale.Changed += ApplyModScale;
            ApplyModScale();
        };
        Unloaded += (_, _) => UiScale.Changed -= ApplyModScale;
    }

    private void ApplyModScale() => UiScale.ApplyModListScale(ModItems);

    // ── sticky list-view preferences ──────────────────────────────────────────────

    /// <summary>Puts the toolbar back the way it was left last session. Runs before the first
    /// <see cref="ApplyFilterSort"/>, and the handlers all no-op until IsLoaded, so restoring the
    /// controls cannot itself trigger a half-built re-sort.</summary>
    private void RestoreListPreferences()
    {
        var s = App.State.Settings;
        // Clamped: Selector throws on an out-of-range index, and a settings.json written by a newer
        // build (or by hand) must not take the whole page down on open.
        SortBox.SelectedIndex = Math.Clamp((int)s.ModListSortMode, 0, SortBox.Items.Count - 1);
        _reverseSort = s.ModListSortReversed;
        SortDirButton.Content = _reverseSort ? "" : "";
        HideDisabled.IsChecked = s.ModListHideDisabled;
        SourceFilterBox.SelectedIndex = Math.Clamp((int)s.ModListSourceFilter, 0, SourceFilterBox.Items.Count - 1);
        // "Only updates" is deliberately not restored: it is a triage filter, and opening the
        // launcher to a pack that looks empty is alarming rather than helpful.
        Resources["ModRowContentWidth"] = s.EffectiveModRowContentWidth;
    }

    /// <summary>Re-reads the launcher-wide mod preferences (Settings → Mods) every time this page is
    /// shown, so changing them and coming back takes effect without reopening the pack. Deliberately
    /// does not touch the channel ComboBox selection: that is the pack's own setting, and writing it
    /// here would fire its handler and save the pack a channel nobody chose.</summary>
    private void SyncLauncherDefaults()
    {
        Resources["ModRowContentWidth"] = App.State.Settings.EffectiveModRowContentWidth;
        AdvChannelDefaultItem.Content = $"Launcher default ({ModUpdateChannel.Label(App.State.ModMetadata.LauncherUpdateChannel)})";
        if (_all.Count == 0) return;

        // The channel decides what counts as an update, so a pack that follows the launcher has to
        // re-check when the launcher moved — otherwise "Update all" would still be answering the
        // question the old channel asked.
        var before = _all[0].DefaultUpdateChannel;
        App.State.ModInventory.ApplyPackDefaults(_pack.Id, _all);
        if (!string.Equals(before, _all[0].DefaultUpdateChannel, StringComparison.OrdinalIgnoreCase))
            _ = RecheckUpdatesAsync(_all.Where(m => m.Meta.UpdateChannel is null).ToList());
    }

    private void SaveListPreferences()
    {
        var s = App.State.Settings;
        var sort = (ModListSortMode)Math.Max(0, SortBox.SelectedIndex);
        var source = (ModListSourceFilter)Math.Max(0, SourceFilterBox.SelectedIndex);
        var hide = HideDisabled.IsChecked == true;
        if (s.ModListSortMode == sort && s.ModListSortReversed == _reverseSort
            && s.ModListSourceFilter == source && s.ModListHideDisabled == hide) return;

        s.ModListSortMode = sort;
        s.ModListSortReversed = _reverseSort;
        s.ModListSourceFilter = source;
        s.ModListHideDisabled = hide;
        s.Save();
    }

    // ── launch the pack ──────────────────────────────────────────────────────────

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
            MinecraftInstanceStatus.Launching => "Launching…",
            _ => "Launch"
        };
    }

    private async void OnLaunch(object sender, RoutedEventArgs e)
    {
        if (App.State.Instances.IsBusy(_pack.Id)) { App.State.Instances.Stop(_pack.Id); UpdateLaunchButton(); return; }

        if (App.State.MinecraftAccounts.Current is null)
        {
            await AppDialog.MessageAsync(_shell, "Launch",
                "Set up a Minecraft account first — click the account chip in the title bar.");
            _shell.OpenMcAccount();
            return;
        }

        LaunchButton.IsEnabled = false;
        ListStatus.Text = "Checking Java…";
        var (javaOk, javaMsg) = await LaunchService.CheckJavaAsync(_pack.MinecraftVersion, App.State.Settings.GetJavaPathFor(_pack.Id));
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

    // ── load ───────────────────────────────────────────────────────────────────

    private async Task ReloadAsync(bool forceUpdateCheck = false)
    {
        var gen = ++_gen;
        // Undo any leftover test-scope from a launcher that was killed mid Run-as-Test
        // (only when nothing is running, so we never reshuffle a live game's mods).
        if (App.State.Instances.GetStatus(_pack.Id) == MinecraftInstanceStatus.Idle)
            App.State.TestScope.RestoreIfPending(_pack.Id);

        ListStatus.Text = "Scanning mods…";
        try
        {
            var mods = await App.State.ModInventory.LoadAsync(_pack.Id, _pack.IsShared);
            if (gen != _gen) return;
            _all = mods;
            App.State.ModMetadata.SyncManagedCategories(_pack.Id); // library mods → managed "Library" category
            _identifying = true;
            ApplyFilterSort();                       // instant: file names + saved flags
            InvalidateSecondaryViews();              // graph/plan/categories refresh lazily on view
            _ = EnrichAsync(gen, forceUpdateCheck);  // background: identities, icons, updates
        }
        catch (Exception ex)
        {
            ListStatus.Text = "Failed to scan mods: " + ex.Message;
        }
    }

    /// <summary>Background second pass: resolve store identities (hashing + matching), then the
    /// update check. The list is already on screen, so this just enriches it in place.</summary>
    private async Task EnrichAsync(int gen, bool forceUpdateCheck = false)
    {
        try { await App.State.ModInventory.ResolveIdentitiesAsync(_pack.Id, _all); }
        catch { /* offline — keep the file-name view */ }
        if (gen != _gen) return;
        _identifying = false;
        UpdateStoreAutoLabel();
        ApplyFilterSort();
        InvalidateSecondaryViews(); // identities/icons resolved — refresh graph/plan/categories when shown
        await CheckUpdatesAsync(gen, forceUpdateCheck);
    }

    /// <summary>Marks the graph / planning / categories views stale so each rebuilds the next time
    /// it's shown, and refreshes whichever one is on screen right now. This is what keeps switching
    /// between tabs cheap — only the visible heavy view is built, and only when its data changed.</summary>
    private void InvalidateSecondaryViews()
    {
        _graphDirty = _planDirty = _categoriesDirty = true;
        switch (Tabs.SelectedIndex)
        {
            case TabGraph when _graph is not null:
                _graph.Load(_pack.Id, _all, _shell, OpenModPage, RequestReload, NoteSecondaryViewEdit); _graphDirty = false; break;
            case TabPlan when _plan is not null:
                _plan.Load(_pack.Id, _all, _shell, OpenModPage, NoteSecondaryViewEdit); _planDirty = false; break;
            case TabCategories when _categories is not null:
                _categories.Load(_pack.Id, _all, _shell, NoteSecondaryViewEdit, OpenModPage, RequestReload); _categoriesDirty = false; break;
        }
    }

    /// <summary>An edit made from *within* the graph / planning / categories view: that view has
    /// already re-rendered itself, so this just marks the other two stale (and refreshes the list
    /// model). Keeps cross-view edits consistent without the eager all-views rebuild.</summary>
    private void NoteSecondaryViewEdit()
    {
        _graphDirty = _planDirty = _categoriesDirty = true;
        switch (Tabs.SelectedIndex) // the editing view is on screen and already current
        {
            case TabGraph: _graphDirty = false; break;
            case TabPlan: _planDirty = false; break;
            case TabCategories: _categoriesDirty = false; break;
        }
        ApplyFilterSort(); // keep the (hidden) list model in sync for when it's next shown
    }

    /// <summary>Keeps the viewport still across a rebuild — see <see cref="ListScrollAnchor"/>.</summary>
    private ListScrollAnchor? _scrollAnchor;

    /// <summary>The order currently on screen, so the anchor can fall forward to the next surviving
    /// row when the one it was holding is filtered out.</summary>
    private IReadOnlyList<object> _rendered = Array.Empty<object>();

    private ListScrollAnchor Anchor => _scrollAnchor ??= new ListScrollAnchor(ModItems);

    private void ApplyFilterSort()
    {
        IEnumerable<PackMod> q = _all;

        var query = SearchBox.Text?.Trim();
        if (!string.IsNullOrEmpty(query))
            q = q.Where(m =>
                m.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || m.VersionLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
                || m.SourceLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
                || m.CategoriesLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
                // so typing "large" narrows to the pack-defining mods
                || (m.ContentSize > 0 && m.ContentSizeLabel.Contains(query, StringComparison.OrdinalIgnoreCase)));

        if (HideDisabled.IsChecked == true) q = q.Where(m => m.Enabled);
        if (OnlyUpdates.IsChecked == true) q = q.Where(m => m.HasUpdate);
        q = SourceFilterBox.SelectedIndex switch
        {
            1 => q.Where(m => m.PrimarySource == ModSource.CurseForge),
            2 => q.Where(m => m.PrimarySource == ModSource.Modrinth),
            3 => q.Where(m => m.IsExternal),
            _ => q
        };

        var cmp = StringComparer.OrdinalIgnoreCase;
        var rev = _reverseSort;
        // Primary key by sort mode (with its natural direction, flipped when reversed).
        // Priority is always the secondary tiebreaker so e.g. within each Status group the
        // highest-priority mods float to the top; name is the final tiebreaker.
        // Sorting by category groups the list instead: the order below puts the categories in the
        // order the Categories page lists them, and the group headers are drawn from that.
        if (SortBox.SelectedIndex == (int)ModListSortMode.Category)
        {
            ApplyCategoryGrouping(q, rev);
            return;
        }

        IOrderedEnumerable<PackMod> ordered = SortBox.SelectedIndex switch
        {
            // Name: A→Z by default
            1 => rev ? q.OrderByDescending(m => m.DisplayName, cmp) : q.OrderBy(m => m.DisplayName, cmp),
            // Add date: newest first by default
            2 => rev ? q.OrderBy(m => m.AddedAt) : q.OrderByDescending(m => m.AddedAt),
            // Update date: mods with an available update first by default
            3 => rev ? q.OrderBy(m => m.HasUpdate) : q.OrderByDescending(m => m.HasUpdate),
            // Status: disabled first by default (Enabled ascending)
            4 => rev ? q.OrderByDescending(m => m.Enabled) : q.OrderBy(m => m.Enabled),
            // Content size: biggest first by default
            5 => rev ? q.OrderBy(m => m.ContentSize) : q.OrderByDescending(m => m.ContentSize),
            // Priority: highest first by default
            _ => rev ? q.OrderBy(m => m.Priority) : q.OrderByDescending(m => m.Priority),
        };
        // Content size ranks below priority as a tiebreaker, and above the name.
        q = ordered.ThenByDescending(m => m.Priority)
                   .ThenByDescending(m => m.ContentSize)
                   .ThenBy(m => m.DisplayName, cmp);

        var list = q.ToList();
        // Every rebuild replaces the source, which leaves the scroll viewer holding a pixel offset
        // that no longer points at the same row — the list appears to jump somewhere else. Note
        // where it was sitting, then put it back.
        var capture = Anchor.Take(_rendered);
        var previous = _rendered;

        ModItems.ItemsSource = list;      // a plain list: no groups, no headers
        _rendered = list;
        Anchor.Restore(capture, previous, list);
        PlayEntranceOnce();
        UpdateSelectionStatus();
        ModCountLabel.Text = $"  ·  {_all.Count} mod{(_all.Count == 1 ? "" : "s")}";

        var updates = _all.Count(m => m.HasUpdate);
        UpdateAllButton.Content = $"Update all ({updates})";
        UpdateAllButton.Visibility = updates > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_all.Count == 0) { ListStatus.Text = "No mods installed."; return; }
        var disabled = _all.Count(m => !m.Enabled);
        var status = $"{_all.Count} mod(s)";
        if (disabled > 0) status += $"  ·  {disabled} disabled";
        if (updates > 0) status += $"  ·  {updates} update(s)";
        if (list.Count != _all.Count) status += $"  ·  showing {list.Count}";
        if (_identifying) status += "  ·  identifying…";
        ListStatus.Text = status;
    }

    /// <summary>
    /// The category sort: one heading per category, in the order the Categories page lists them,
    /// with "Uncategorized" last.
    /// </summary>
    /// <remarks>
    /// This is a grouped <see cref="ListCollectionView"/> rather than a sorted list with fake header
    /// rows: WPF then owns the headers (and keeps virtualisation working through
    /// IsVirtualizingWhenGrouping), and every other part of the view still sees plain PackMod items.
    /// Groups appear in the order their first item does, so ordering the items by category rank is
    /// what orders the headings.
    /// </remarks>
    private void ApplyCategoryGrouping(IEnumerable<PackMod> filtered, bool reverse)
    {
        var declared = App.State.ModMetadata.Categories(_pack.Id).Select(c => c.Name).ToList();
        int Rank(string name)
        {
            var i = declared.FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            return i < 0 ? int.MaxValue : i;   // a category with no entry on the page trails the declared ones
        }

        static bool IsUncategorized(string name) =>
            string.Equals(name, PackMod.UncategorizedName, StringComparison.OrdinalIgnoreCase);

        var cmp = StringComparer.OrdinalIgnoreCase;
        // "Uncategorized" is the leftovers bin, not a category: it is pinned to the end in both
        // directions, so reversing re-orders the real categories rather than burying them under it.
        var byCategory = filtered.OrderBy(m => IsUncategorized(m.PrimaryCategory) ? 1 : 0);
        byCategory = reverse
            ? byCategory.ThenByDescending(m => Rank(m.PrimaryCategory)).ThenByDescending(m => m.PrimaryCategory, cmp)
            : byCategory.ThenBy(m => Rank(m.PrimaryCategory)).ThenBy(m => m.PrimaryCategory, cmp);

        var list = byCategory
            .ThenByDescending(m => m.Priority)
            .ThenByDescending(m => m.ContentSize)
            .ThenBy(m => m.DisplayName, cmp)
            .ToList();

        // The headers take their colour from the pack's categories.
        CategoryToBrushConverter.Lookup = name =>
            AccentPaletteBrushFor(App.State.ModMetadata.CategoryColor(_pack.Id, name));

        var capture = Anchor.Take(_rendered);
        var previous = _rendered;

        var view = new ListCollectionView(list);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PackMod.PrimaryCategory)));
        ModItems.ItemsSource = view;
        _rendered = list;
        Anchor.Restore(capture, previous, list);

        PlayEntranceOnce();
        UpdateSelectionStatus();
        ModCountLabel.Text = $"  ·  {_all.Count} mod{(_all.Count == 1 ? "" : "s")}";

        var updates = _all.Count(m => m.HasUpdate);
        UpdateAllButton.Content = $"Update all ({updates})";
        UpdateAllButton.Visibility = updates > 0 ? Visibility.Visible : Visibility.Collapsed;

        var groups = list.Select(m => m.PrimaryCategory).Distinct(cmp).Count();
        var status = $"{_all.Count} mod(s)  ·  {groups} categor{(groups == 1 ? "y" : "ies")}";
        if (list.Count != _all.Count) status += $"  ·  showing {list.Count}";
        if (updates > 0) status += $"  ·  {updates} update(s)";
        if (_identifying) status += "  ·  identifying…";
        ListStatus.Text = status;
    }

    private static Brush? AccentPaletteBrushFor(string? hex) =>
        hex is null ? null : AccentPalette.Brush(hex, (Brush)Application.Current.Resources["TextSecondaryBrush"]);

    private async Task CheckUpdatesAsync(int gen, bool forceRefresh = false)
    {
        if (forceRefresh) ListStatus.Text = $"Re-checking {_all.Count} mod(s) with the stores…";
        await RunUpdateCheckAsync(_all.ToList(), () => gen != _gen, forceRefresh);
        // Updates landed — the "has update" group cards and list should reflect them.
        if (gen == _gen) ListEdited();
    }

    /// <summary>Re-checks just the given mods — after their update channel or followed store changed,
    /// which changes what counts as an update for them.</summary>
    private async Task RecheckUpdatesAsync(IReadOnlyList<PackMod> mods)
    {
        var gen = _gen;
        ListStatus.Text = mods.Count == 1 ? $"Re-checking {mods[0].DisplayName}…" : $"Re-checking {mods.Count} mod(s)…";
        await RunUpdateCheckAsync(mods.ToList(), () => gen != _gen, forceRefresh: true);
        if (gen == _gen) ListEdited();
    }

    /// <summary>The update check, a few mods at a time. One rule for every surface lives in
    /// <see cref="ModUpdater.FindUpdateAsync"/>; version lists come from the shared catalog, so the
    /// Mods tab and this page never fetch the same list twice, and the ApiClient paces what does go
    /// out so a big pack cannot trip the stores' rate limits.</summary>
    private async Task RunUpdateCheckAsync(List<PackMod> mods, Func<bool> stale, bool forceRefresh = false)
    {
        var mc = _pack.MinecraftVersion;
        var loader = ModUpdater.LoaderTag(_pack);
        var gate = new SemaphoreSlim(3, 3);
        try
        {
            await Task.WhenAll(mods.Select(async mod =>
            {
                if (mod.PrimaryMod is null || mod.PrimaryVersion is null) return;
                await gate.WaitAsync();
                try
                {
                    var latest = await ModUpdater.FindUpdateAsync(mod, mc, loader, forceRefresh);
                    if (stale()) return;
                    await Dispatcher.InvokeAsync(() => { mod.LatestVersion = latest; });
                }
                finally { gate.Release(); }
            }));
        }
        catch { /* per-mod failures are swallowed inside FindUpdateAsync */ }
    }

    private async void OnUpdateAll(object sender, RoutedEventArgs e)
    {
        var updatable = _all.Where(m => m.HasUpdate && m.LatestVersion is not null).ToList();
        if (updatable.Count == 0) { ListStatus.Text = "Everything is up to date."; return; }

        var chosen = await ModUpdateReviewDialog.ShowAsync(_shell, _pack, updatable);
        if (chosen is null || chosen.Count == 0) return;

        var summary = await ModUpdateProgressDialog.RunAsync(_shell, _pack, chosen);
        ListStatus.Text = summary.Describe();
        await ReloadAsync();
    }

    // ── tab lazy-load ────────────────────────────────────────────────────────────

    private int _lastTab;

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource is not TabControl) return; // ignore inner ComboBox bubbling

        var from = _lastTab;
        var to = Tabs.SelectedIndex;
        _lastTab = to;

        // Graph / Planning / Categories are heavy to build (a whole canvas of nodes) and only need
        // rebuilding when the mod data actually changed. They're rebuilt on their first view and
        // whenever InvalidateSecondaryViews() has marked them dirty — not on every tab switch, which
        // was the source of the switch-back lag.
        if (to == TabGraph)
        {
            if (!_graphLoaded)
            {
                _graphLoaded = true;
                _graph = new ModGraphView();
                GraphHost.Children.Clear();
                GraphHost.Children.Add(_graph);
                _graphDirty = true;
            }
            if (_graphDirty && _graph is not null)
            {
                _graph.Load(_pack.Id, _all, _shell, OpenModPage, RequestReload, NoteSecondaryViewEdit);
                _graphDirty = false;
            }
        }
        else if (to == TabCategories)
        {
            if (!_categoriesLoaded)
            {
                _categoriesLoaded = true;
                _categories = new ModCategoriesView();
                CategoriesHost.Children.Clear();
                CategoriesHost.Children.Add(_categories);
                _categoriesDirty = true;
            }
            if (_categoriesDirty && _categories is not null)
            {
                _categories.Load(_pack.Id, _all, _shell, NoteSecondaryViewEdit, OpenModPage, RequestReload);
                _categoriesDirty = false;
            }
        }
        else if (to == TabPlan)
        {
            if (!_planLoaded)
            {
                _planLoaded = true;
                _plan = new ModPlanView();
                PlanHost.Children.Clear();
                PlanHost.Children.Add(_plan);
                _planDirty = true;
            }
            // Group cards are live queries, so re-resolve them whenever the flags they read changed.
            if (_planDirty && _plan is not null)
            {
                _plan.Load(_pack.Id, _all, _shell, OpenModPage, NoteSecondaryViewEdit);
                _planDirty = false;
            }
        }
        else if (to == TabList)
        {
            // Pick up edits made in the Graph / Planning views (same mod instances, just re-render).
            ApplyFilterSort();
        }
        else if (to == TabBrowse)
        {
            EnsureBrowse();
        }

        // Coming back from the browser to List/Graph: mods may have been downloaded — re-scan.
        if (from == TabBrowse && to != TabBrowse)
            _ = ReloadAsync();
    }

    private void RequestReload() => _ = ReloadAsync();

    // ── back navigation from the in-launcher mod page ────────────────────────────
    private int _returnTab = -1;

    /// <summary>Side-panel back button: if we're showing a mod page reached by clicking a mod,
    /// go back to the tab we came from instead of closing the whole management panel.</summary>
    public bool TryHandleBack()
    {
        if (Tabs.SelectedIndex == TabBrowse && _returnTab >= 0)
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

    /// <summary>Opens a mod's page inside the launcher — switches to the Mod browsing sub-tab and
    /// shows the mod in its detail panel (not the external website).</summary>
    private void OpenModPage(PackMod mod) => _ = OpenModPageAsync(mod);

    private async Task OpenModPageAsync(PackMod mod)
    {
        var summary = mod.PrimaryMod;
        if (summary is null)
        {
            ListStatus.Text = $"{mod.DisplayName} isn't identified yet — try again in a moment.";
            return;
        }
        var browse = EnsureBrowse();
        if (Tabs.SelectedIndex != TabBrowse) _returnTab = Tabs.SelectedIndex; // remember where to return on Back
        Tabs.SelectedIndex = TabBrowse;
        try { await browse.ShowModAsync(summary); }
        catch (Exception ex) { ListStatus.Text = ex.Message; }
    }

    // ── toolbar handlers ──────────────────────────────────────────────────────────

    private void OnSearchChanged(object s, TextChangedEventArgs e)
    {
        UpdateSearchPlaceholder();
        if (IsLoaded) ApplyFilterSort();
    }

    private void OnSearchFocusChanged(object sender, KeyboardFocusChangedEventArgs e) => UpdateSearchPlaceholder();

    // ── one-time entrance animation ────────────────────────────────────────────────
    // Stagger the cards in only on the first build of the list. Later edits (enable/disable,
    // flag changes) and filter/sort changes just update in place — they never re-animate.

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
    private void OnSortChanged(object s, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        SaveListPreferences();
        ApplyFilterSort();
    }

    private bool _reverseSort;

    private void OnToggleSortDir(object s, RoutedEventArgs e)
    {
        _reverseSort = !_reverseSort;
        // Chevron points the way the list runs: down = natural order, up = reversed.
        SortDirButton.Content = _reverseSort ? "" : "";
        if (!IsLoaded) return;
        SaveListPreferences();
        ApplyFilterSort();
    }
    private void OnFilterToggle(object s, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        SaveListPreferences();
        ApplyFilterSort();
    }
    /// <summary>
    /// Refresh: re-scan the mods folder AND ask the stores again instead of reusing this session's
    /// cached version lists — see <see cref="ModVersionCatalog"/> for why they are cached at all.
    /// Leaving this page and coming back re-uses them (cheap, and easy to mistake for "it did not
    /// check"); this button is what forces a real re-check.
    /// </summary>
    private async void OnRefresh(object s, RoutedEventArgs e) => await ReloadAsync(forceUpdateCheck: true);

    // ── adding a jar by hand ──────────────────────────────────────────────────────

    /// <summary>"Add file": copies mod jars the user picked on this PC into the pack's mods folder.
    /// The counterpart to the browse tab for a jar that is not on either store — a private build, a
    /// friend's mod, something pulled from a GitHub release.</summary>
    private async void OnAddFile(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add mods to " + _pack.Name,
            Filter = "Mod jars (*.jar;*.jar.disabled)|*.jar;*.jar.disabled|All files|*",
            Multiselect = true
        };
        if (dlg.ShowDialog(_shell) != true) return;
        await AddFilesAsync(dlg.FileNames);
    }

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedJars(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnListDrop(object sender, DragEventArgs e)
    {
        var files = DroppedJars(e);
        e.Handled = true;
        if (files.Count > 0) await AddFilesAsync(files);
    }

    /// <summary>The jars in a drag payload — dropping anything else (a folder, a screenshot) is
    /// ignored rather than copied into mods/.</summary>
    private static List<string> DroppedJars(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return new List<string>();
        return (e.Data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>())
            .Where(f => File.Exists(f) && IsJar(f))
            .ToList();
    }

    private static bool IsJar(string path) =>
        path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase);

    /// <summary>Copies the given jars into <c>game/mods</c> and re-scans. A file that is already
    /// there is replaced only when the user says so, so "add" can never silently overwrite a mod
    /// they are running.</summary>
    private async Task AddFilesAsync(IReadOnlyList<string> files)
    {
        var jars = files.Where(IsJar).ToList();
        var ignored = files.Count - jars.Count;
        if (jars.Count == 0)
        {
            ListStatus.Text = ignored > 0 ? "Only .jar files can be added to mods/." : "Nothing to add.";
            return;
        }

        var added = 0;
        var skipped = 0;
        var failed = new List<string>();
        try
        {
            // Inside the try: a pack whose folder has never been materialised throws from here.
            var folder = Path.Combine(App.State.Packs.GameDir(_pack.Id), "mods");
            Directory.CreateDirectory(folder);
            foreach (var source in jars)
            {
                var name = Path.GetFileName(source);
                var dest = Path.Combine(folder, name);

                // Picked the pack's own jar: nothing to do. Checked before the replace prompt,
                // because answering "Replace" there would delete the file we are about to copy
                // from and leave the pack without the mod.
                if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    continue;
                }

                // Both spellings of the same jar count as already present: dropping "x.jar" next to
                // a disabled "x.jar.disabled" would leave the pack holding the mod twice.
                var enabled = dest.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                    ? dest[..^".disabled".Length]
                    : dest;
                var existing = File.Exists(enabled) ? enabled
                    : File.Exists(enabled + ".disabled") ? enabled + ".disabled"
                    : null;
                if (existing is not null)
                {
                    var replace = await AppDialog.ConfirmAsync(_shell, "Add file",
                        Path.GetFileName(existing) + " is already in this pack." +
                        Environment.NewLine + Environment.NewLine + "Replace it with the file you picked?",
                        "Replace", "Skip");
                    if (!replace) { skipped++; continue; }
                    try { File.Delete(existing); }
                    catch (Exception ex) { failed.Add($"{name} ({ex.Message})"); continue; }
                }

                try { File.Copy(source, dest, overwrite: true); added++; }
                catch (Exception ex) { failed.Add($"{name} ({ex.Message})"); }
            }
        }
        catch (Exception ex)
        {
            ListStatus.Text = "Could not add files: " + ex.Message;
            return;
        }

        // Re-scan first: ReloadAsync writes its own summary to the status strip, so reporting
        // before it would flash the result and lose it.
        if (added > 0) await ReloadAsync();

        var parts = new List<string>();
        if (added > 0) parts.Add($"Added {added} mod(s)");
        if (skipped > 0) parts.Add($"skipped {skipped}");
        if (ignored > 0) parts.Add($"ignored {ignored} non-jar file(s)");
        if (failed.Count > 0) parts.Add("failed: " + string.Join(", ", failed));
        ListStatus.Text = parts.Count == 0 ? "Nothing to add." : string.Join(" · ", parts) + ".";

        // A successful add shows itself — the mod is in the list. Anything the user did not ask for
        // (a copy that failed, a file that was not a jar) gets a dialog, because the status strip is
        // rewritten a moment later when the background identify pass finishes.
        if (failed.Count > 0)
            await AppDialog.MessageAsync(_shell, "Add file",
                "These files could not be added:" + Environment.NewLine + Environment.NewLine +
                string.Join(Environment.NewLine, failed));
        else if (ignored > 0 && added == 0)
            await AppDialog.MessageAsync(_shell, "Add file",
                "Nothing was added — only .jar files belong in a pack's mods folder.");
    }

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
            ReportLaunchStatus("Launching test set…");
            var proc = await App.State.Launcher.LaunchTrackedAsync(_pack, new Progress<string>(ReportLaunchStatus));

            void RestoreAfterTest()
            {
                App.State.TestScope.Restore(_pack.Id);
                RunTestButton.IsEnabled = true;
                ListStatus.Text = "Test run finished — full mod set restored.";
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

    // ── advanced settings (§7) ────────────────────────────────────────────────────

    private void LoadAdvancedSettings()
    {
        var adv = App.State.ModMetadata.Advanced(_pack.Id);
        AdvAutoDeps.IsChecked = adv.AutoDownloadDependencies;
        AdvCascadeDependents.IsChecked = adv.CascadeDisableDependents;
        AdvCascadeLibraries.IsChecked = adv.CascadeDisableLibraries;
        AdvWarnUpdate.IsChecked = adv.WarnOnUpdateIncompatible;
        AdvShowLines.IsChecked = adv.ShowDependencyLines;
        AdvStoreBox.SelectedIndex = adv.PreferredSource switch
        {
            ModSource.CurseForge => 1,
            ModSource.Modrinth => 2,
            _ => 0
        };
        AdvChannelDefaultItem.Content = $"Launcher default ({ModUpdateChannel.Label(App.State.ModMetadata.LauncherUpdateChannel)})";
        AdvChannelBox.SelectedIndex = ModUpdateChannel.Normalize(adv.UpdateChannel) switch
        {
            ModUpdateChannel.Release => 1,
            ModUpdateChannel.Beta => 2,
            ModUpdateChannel.Alpha => 3,
            _ => 0   // null = follow the launcher-wide channel
        };
    }

    /// <summary>Spells out what "Automatic" currently means for this pack, with the counts behind it.</summary>
    private void UpdateStoreAutoLabel()
    {
        var (cfOnly, mrOnly) = App.State.ModInventory.ExclusiveCounts(_all);
        var inferred = App.State.ModInventory.InferredSource(_pack.Id);
        AdvStoreAutoItem.Content = inferred switch
        {
            ModSource.CurseForge => "Automatic (CurseForge)",
            ModSource.Modrinth => "Automatic (Modrinth)",
            _ => "Automatic (Modrinth first)"
        };
        AdvStoreAutoNote.Text = inferred switch
        {
            ModSource.CurseForge => $"Automatic picks CurseForge for this pack: {cfOnly} of its mods are only on CurseForge, {mrOnly} only on Modrinth.",
            ModSource.Modrinth => $"Automatic picks Modrinth for this pack: {mrOnly} of its mods are only on Modrinth, {cfOnly} only on CurseForge.",
            _ => "Automatic falls back to Modrinth first: this pack has no clear home store yet."
        };
    }

    /// <summary>Pack-wide default store changed: re-stamp it on the loaded mods (labels and links
    /// follow at once) and re-check updates for the cross-listed ones, whose version identity moved.</summary>
    private void OnAdvStoreChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        var adv = App.State.ModMetadata.Advanced(_pack.Id);
        adv.PreferredSource = AdvStoreBox.SelectedIndex switch
        {
            1 => ModSource.CurseForge,
            2 => ModSource.Modrinth,
            _ => null
        };
        App.State.ModMetadata.SaveAdvanced(_pack.Id);
        App.State.ModInventory.ApplyPackDefaults(_pack.Id, _all);
        ListEdited();
        _ = RecheckUpdatesAsync(_all.Where(m => m.IsCrossListed && m.Meta.PreferredSource is null).ToList());
    }

    private void OnAdvChannelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        var adv = App.State.ModMetadata.Advanced(_pack.Id);
        adv.UpdateChannel = AdvChannelBox.SelectedIndex switch
        {
            1 => ModUpdateChannel.Release,
            2 => ModUpdateChannel.Beta,
            3 => ModUpdateChannel.Alpha,
            _ => null   // follow the launcher-wide channel
        };
        App.State.ModMetadata.SaveAdvanced(_pack.Id);
        App.State.ModInventory.ApplyPackDefaults(_pack.Id, _all);
        _ = RecheckUpdatesAsync(_all.Where(m => m.Meta.UpdateChannel is null).ToList());
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

    // ── Files-tab mod tools (§6) ──────────────────────────────────────────────────

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
        Add("Enable all “extra” mods", () => SetExtras(true));
        Add("Disable all “extra” mods", () => SetExtras(false));
        menu.Items.Add(new Separator());
        Add("Export server + client mod folder", ExportSideFolder, _pack.IsShared);
        menu.IsOpen = true;
    }

    /// <summary>Move server-only mods out of the synced game/ folder into per-user local/ so they
    /// don't ship to clients. (§6: server-marked mods can be set to local.)</summary>
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
        ListStatus.Text = n == 0 ? "No “extra” mods to change." : $"{(enabled ? "Enabled" : "Disabled")} {n} extra mod(s).";
        await ReloadAsync();
    }

    /// <summary>Copy every side-marked (client/server) mod into a side-mods/ folder split by side —
    /// only when sharing is enabled (§6).</summary>
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

    // ── card / row handlers ─────────────────────────────────────────────────────

    private static PackMod? ModOf(object sender) => (sender as FrameworkElement)?.DataContext as PackMod;

    // Left-clicking a card intentionally does nothing — the mod page is opened from the
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

    /// <summary>Right-click on the empty part of the list (below or between cards) — the same
    /// pack-level actions the planning board offers on empty canvas. A right-click that lands on a
    /// card is handled there, so it never reaches this.</summary>
    private void OnListBackgroundRightClick(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = sender as UIElement };
        menu.Items.Add(ModCategoryMenu.Build(_pack.Id, _all, _shell, ApplyFilterSort));
        menu.Items.Add(new Separator());

        var shown = (ModItems.ItemsSource as IEnumerable<PackMod>)?.ToList() ?? _all;
        var selected = _all.Count(m => m.IsSelected);
        menu.Items.Add(MenuAction($"Select all {shown.Count} shown", () =>
        {
            foreach (var m in shown) m.IsSelected = true;
            UpdateSelectionStatus();
        }, shown.Count > 0));
        menu.Items.Add(MenuAction("Clear selection", () =>
        {
            foreach (var m in _all) m.IsSelected = false;
            UpdateSelectionStatus();
        }, selected > 0));

        menu.Items.Add(new Separator());
        menu.Items.Add(MenuAction("Re-scan mods folder", () => _ = ReloadAsync()));
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static MenuItem MenuAction(string header, Action onClick, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, IsEnabled = enabled };
        mi.Click += (_, _) => onClick();
        return mi;
    }

    private void OnNoteClick(object sender, RoutedEventArgs e)
    {
        if (ModOf(sender) is not { } mod) return;
        ModNotePopup.Show(sender as FrameworkElement, mod, _pack.Id, App.State.ModInventory, ListEdited);
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
        OnChanged = ListEdited,
        OnOpenPage = OpenModPage,
        OnUpdate = list => _ = UpdateManyAsync(list),
        OnUpdateToVersion = m => _ = UpdateToVersionAsync(m),
        OnDelete = list => _ = DeleteManyAsync(list),
        OnReveal = Reveal,
        OnSetEnabled = (list, en) => _ = SetEnabledManyAsync(list, en),
        OnRecheckUpdates = list => _ = RecheckUpdatesAsync(list),
    };

    // ── multi-select ─────────────────────────────────────────────────────────────

    /// <summary>The mods a right-click acts on: the whole selection when the clicked mod is part of
    /// it (Explorer-style), otherwise just the clicked mod.</summary>
    private IReadOnlyList<PackMod> TargetsFor(PackMod mod)
    {
        var selected = _all.Where(m => m.IsSelected).ToList();
        return selected.Count > 0 && mod.IsSelected ? selected : new List<PackMod> { mod };
    }

    /// <summary>The last checkbox clicked without Shift — the anchor a Shift-click extends from.</summary>
    private PackMod? _selectionAnchor;

    /// <summary>
    /// Ticking a row's checkbox. Shift-click extends from the last plain click to this row and gives
    /// every row between them this checkbox's new state — the range select every file list has.
    /// </summary>
    /// <remarks>Ctrl is not handled because a checkbox list is already additive: each click toggles
    /// one row and leaves the rest alone. The anchor deliberately stays put on a Shift-click, so
    /// shift-clicking again re-draws the range from the same start rather than walking away.</remarks>
    private void OnSelectToggle(object sender, RoutedEventArgs e) =>
        ApplySelectionClick((sender as FrameworkElement)?.DataContext as PackMod,
            Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));

    /// <summary>The logic behind <see cref="OnSelectToggle"/>, separated from the mouse so it can be
    /// exercised without one.</summary>
    private void ApplySelectionClick(PackMod? clicked, bool extend)
    {
        if (clicked is not null && extend && _selectionAnchor is not null && _selectionAnchor != clicked)
        {
            var order = _rendered.OfType<PackMod>().ToList();
            var from = order.IndexOf(_selectionAnchor);
            var to = order.IndexOf(clicked);
            if (from >= 0 && to >= 0)
            {
                var state = clicked.IsSelected;
                for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++)
                    order[i].IsSelected = state;
            }
        }
        else if (clicked is not null)
        {
            _selectionAnchor = clicked;
        }

        UpdateSelectionStatus();
    }

    private void OnClearSelection(object sender, RoutedEventArgs e)
    {
        foreach (var m in _all) m.IsSelected = false;
        _selectionAnchor = null;
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
        if (n > 0) SelectionLabel.Text = $"{n} selected — right-click a selected mod to change all · Shift-click to select a range";
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

        // Update-locked mods sit out a bulk run — that's what the lock is for. Aiming at a single
        // locked mod is a deliberate act though, so that one offers the override instead.
        var held = updatable.Where(m => m.Meta.UpdateLocked).ToList();
        var run  = updatable.Where(m => !m.Meta.UpdateLocked).ToList();
        if (run.Count == 0)
        {
            if (held.Count > 1)
            {
                ListStatus.Text = $"All {held.Count} mods with updates are locked — unlock them to update.";
                return;
            }
            if (!await ConfirmLockedUpdateAsync(held[0])) { ListStatus.Text = ""; return; }
            run = held;
            held = new List<PackMod>();
        }

        var done = 0;
        foreach (var m in run)
        {
            ListStatus.Text = $"Updating {m.DisplayName}…";
            try { if (await ModUpdater.InstallVersionAsync(m, m.LatestVersion!)) done++; }
            catch (Exception ex) { ListStatus.Text = "Update failed: " + ex.Message; }
        }
        ListStatus.Text = $"Updated {done} mod(s)" + (held.Count > 0 ? $", skipped {held.Count} locked." : ".");
        await ReloadAsync();
    }

    /// <summary>Asks before pushing an update past a lock the user set. Returns true to go ahead.
    /// The lock stays on — overriding it once isn't the same as wanting it gone.</summary>
    private Task<bool> ConfirmLockedUpdateAsync(PackMod mod) =>
        AppDialog.ConfirmAsync(_shell, "Mod is locked",
            $"{mod.DisplayName} is locked to its current version, so bulk updates skip it.\n\n" +
            "Update it anyway? It stays locked afterwards.",
            "Update anyway", "Keep locked");

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

    // ── actions ──────────────────────────────────────────────────────────────────

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

        ListEdited();
        return Task.CompletedTask;
    }

    /// <summary>Re-render the list and mark the heavy secondary views stale — used wherever a List-view
    /// action actually changes mod data (flags, categories, enable/disable, notes, updates).</summary>
    private void ListEdited()
    {
        ApplyFilterSort();
        InvalidateSecondaryViews();
    }

    private async Task UpdateModAsync(PackMod mod)
    {
        if (mod.LatestVersion is null) { ListStatus.Text = $"{mod.DisplayName} is up to date."; return; }

        if (mod.Meta.UpdateLocked && !await ConfirmLockedUpdateAsync(mod)) { ListStatus.Text = ""; return; }

        // Warn before updating a mod the user flagged as update-incompatible (§7).
        if (mod.Meta.UpdateIncompatible && App.State.ModMetadata.Advanced(_pack.Id).WarnOnUpdateIncompatible)
        {
            var go = await AppDialog.ConfirmAsync(_shell, "Update warning",
                $"{mod.DisplayName} is marked as update-incompatible — updating it may break your setup.\n\nUpdate anyway?",
                "Update", "Cancel", danger: true);
            if (!go) return;
        }

        ListStatus.Text = $"Updating {mod.DisplayName}…";
        try
        {
            var file = mod.LatestVersion.Files.FirstOrDefault(f => f.IsPrimary) ?? mod.LatestVersion.Files.FirstOrDefault();
            if (file is null) { ListStatus.Text = "No downloadable file."; return; }
            file = await EnsureDownloadableAsync(mod.LatestVersion, file);
            if (string.IsNullOrWhiteSpace(file.DownloadUrl)) { ListStatus.Text = "No downloadable file."; return; }

            // Keep a disabled mod disabled after update, and strip any path components from the
            // store-supplied filename (see ModUpdater.InstallVersionAsync).
            var safeName = Path.GetFileName(file.Filename);
            var fileName = mod.Enabled ? safeName : safeName + ".disabled";
            var dest = Path.Combine(Path.GetDirectoryName(mod.FilePath)!, fileName);
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

        ListStatus.Text = $"Loading versions for {mod.DisplayName}…";
        var versions = await ModUpdater.FetchVersionsAsync(mod.PrimaryMod);
        if (versions.Count == 0) { ListStatus.Text = $"No versions found for {mod.DisplayName}."; return; }

        var loader = _pack.Loader == LoaderKind.None ? null : _pack.Loader.ToString().ToLowerInvariant();
        var chosen = await ModVersionPickerDialog.ShowAsync(_shell, mod.DisplayName, versions, _pack.MinecraftVersion, loader,
            mod.PrimaryVersion?.Id, mod.PrimaryVersion?.VersionNumber);
        if (chosen is null) { ListStatus.Text = ""; return; }

        // A lock holds the mod at its version whichever direction the change comes from.
        if (mod.Meta.UpdateLocked && !await ConfirmLockedUpdateAsync(mod)) { ListStatus.Text = ""; return; }

        // Update-incompatible warning still applies when the user picks a version manually.
        if (mod.Meta.UpdateIncompatible && App.State.ModMetadata.Advanced(_pack.Id).WarnOnUpdateIncompatible)
        {
            var go = await AppDialog.ConfirmAsync(_shell, "Update warning",
                $"{mod.DisplayName} is marked as update-incompatible — changing its version may break your setup.\n\nContinue?",
                "Install", "Cancel", danger: true);
            if (!go) return;
        }

        ListStatus.Text = $"Installing {mod.DisplayName} {chosen.VersionNumber}…";
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
