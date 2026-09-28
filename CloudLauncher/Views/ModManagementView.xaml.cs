using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// The mod management page (List, Browse, Graph, Categories and Planning sub-tabs). Opened from the
/// Mod view's "Mods Management" button. All sub-tabs read the same <see cref="PackModInventory"/>.
/// </summary>
public partial class ModManagementView : Page, ISidePanelBackHandler
{
    // Sub-tab indices, named so adding a tab can't shift the lazy-load and back-navigation logic.
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
            // Hooked on the window, as on the Instances screen, so the shortcuts work wherever focus is.
            if (Window.GetWindow(this) is { } w) { w.PreviewKeyDown -= OnShellKeyDown; w.PreviewKeyDown += OnShellKeyDown; }
        };
        Unloaded += (_, _) =>
        {
            UiScale.Changed -= ApplyModScale;
            if (Window.GetWindow(this) is { } w) w.PreviewKeyDown -= OnShellKeyDown;
        };
    }

    // ── keyboard shortcuts ────────────────────────────────────────────────────────

    /// <summary>
    /// Same shortcuts as the Instances screen: Ctrl+F search, F5 refresh, Esc backs out of a search or
    /// selection, Delete removes the selected jars, Ctrl+A selects everything the filters show.
    /// </summary>
    /// <remarks>
    /// Checks <see cref="UIElement.IsVisible"/> because the handler is on the window and the hub may be
    /// in the side panel while another page has focus. Delete and Ctrl+A only apply on the List tab;
    /// the other tabs handle them themselves.
    /// </remarks>
    private void OnShellKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsVisible) return;
        // A card (version picker, changelog) is open over the page and handles its own keys.
        if (Window.GetWindow(this) is MainWindow { DialogLayer.Visibility: Visibility.Visible }) return;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var onList = Tabs.SelectedIndex == TabList;
        var typing = SearchBox.IsKeyboardFocusWithin;

        if (ctrl && e.Key == Key.F && onList)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            OnRefresh(RefreshButton, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && onList)
        {
            // One step back per press: clear the search first, then the selection.
            if (!string.IsNullOrEmpty(SearchBox.Text)) { SearchBox.Text = ""; e.Handled = true; }
            else if (_all.Any(m => m.IsSelected)) { OnClearSelection(this, e); e.Handled = true; }
        }
        else if (e.Key == Key.Delete && onList && !typing)
        {
            var selected = _all.Where(m => m.IsSelected).ToList();
            if (selected.Count == 0) return;
            _ = DeleteManyAsync(selected);   // asks before it deletes anything
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.A && onList && !typing)
        {
            OnSelectAll(this, e);
            e.Handled = true;
        }
    }

    private void ApplyModScale() => UiScale.ApplyModListScale(ModItems);

    // ── sticky list-view preferences ──────────────────────────────────────────────

    /// <summary>Puts the toolbar back the way it was left last session. Runs before the first
    /// <see cref="ApplyFilterSort"/>, and the handlers all no-op until IsLoaded, so restoring the
    /// controls cannot itself trigger a half-built re-sort.</summary>
    private void RestoreListPreferences()
    {
        var s = App.State.Settings;
        // Clamped: Selector throws on an out-of-range index, and settings.json may come from a newer
        // build or be hand-edited.
        SortBox.SelectedIndex = Math.Clamp((int)s.ModListSortMode, 0, SortBox.Items.Count - 1);
        _reverseSort = s.ModListSortReversed;
        SortDirButton.Content = _reverseSort ? "" : "";
        HideDisabled.IsChecked = s.ModListHideDisabled;
        SourceFilterBox.SelectedIndex = Math.Clamp((int)s.ModListSourceFilter, 0, SourceFilterBox.Items.Count - 1);
        // "Only updates" isn't restored: it's a triage filter, and a pack that opens looking empty is
        // alarming.
        Resources["ModRowContentWidth"] = s.EffectiveModRowContentWidth;
        Resources["ModRowActionsAlign"] = s.ModRowActionsAtRight
            ? HorizontalAlignment.Right
            : HorizontalAlignment.Left;
    }

    /// <summary>Re-reads the launcher-wide mod preferences (Settings -> Mods) whenever this page is
    /// shown. Leaves the channel ComboBox alone: that is the pack's own setting, and setting it here
    /// would fire its handler and save a channel nobody chose.</summary>
    private void SyncLauncherDefaults()
    {
        Resources["ModRowContentWidth"] = App.State.Settings.EffectiveModRowContentWidth;
        Resources["ModRowActionsAlign"] = App.State.Settings.ModRowActionsAtRight
            ? HorizontalAlignment.Right
            : HorizontalAlignment.Left;
        AdvChannelDefaultItem.Content = $"Launcher default ({ModUpdateChannel.Label(App.State.ModMetadata.LauncherUpdateChannel)})";
        if (_all.Count == 0) return;

        // The channel decides what counts as an update, so a pack that follows the launcher default
        // re-checks when that default changes.
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
            MinecraftInstanceStatus.Launching => "Launching...",
            _ => "Launch"
        };
    }

    private async void OnLaunch(object sender, RoutedEventArgs e)
    {
        if (App.State.Instances.IsBusy(_pack.Id)) { App.State.Instances.Stop(_pack.Id); UpdateLaunchButton(); return; }

        if (App.State.MinecraftAccounts.Current is null)
        {
            await AppDialog.MessageAsync(_shell, "Launch",
                "Set up a Minecraft account first - click the account chip in the title bar.");
            _shell.OpenMcAccount();
            return;
        }

        LaunchButton.IsEnabled = false;
        ListStatus.Text = "Checking Java...";
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

    /// <summary>The mods currently loaded.</summary>
    internal IReadOnlyList<PackMod> Mods => _all;

    // ── load ───────────────────────────────────────────────────────────────────

    /// <summary>The background enrich pass started by the latest <see cref="ReloadAsync"/>.
    /// <see cref="OnRefresh"/> awaits it so the button stays busy until the store lookups, the slow
    /// part, have finished.</summary>
    private Task _enrichTask = Task.CompletedTask;

    private async Task ReloadAsync(bool forceUpdateCheck = false)
    {
        var gen = ++_gen;
        // Undo any leftover test-scope from a launcher that was killed mid Run-as-Test
        // (only when nothing is running, so we never reshuffle a live game's mods).
        if (App.State.Instances.GetStatus(_pack.Id) == MinecraftInstanceStatus.Idle)
            App.State.TestScope.RestoreIfPending(_pack.Id);

        ListStatus.Text = "Scanning mods...";
        try
        {
            // Always include local/: library defaults land in local/mods for every instance and only reach
            // game/ through the launch overlay, so without it they wouldn't show until the next launch.
            var mods = await App.State.ModInventory.LoadAsync(_pack.Id, includeLocal: true);
            if (gen != _gen) return;
            _all = mods;
            App.State.ModMetadata.SyncManagedCategories(_pack.Id); // library mods -> managed "Library" category
            PackModInventory.ResolveConflicts(_all);               // recorded clashes -> the CONFLICT pill
            _identifying = true;
            ApplyFilterSort();                       // instant: file names + saved flags
            InvalidateSecondaryViews();              // graph/plan/categories refresh lazily on view
            _enrichTask = EnrichAsync(gen, forceUpdateCheck);  // background: identities, icons, updates
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
        catch { /* offline: keep the file-name view */ }
        if (gen != _gen) return;
        _identifying = false;
        UpdateStoreAutoLabel();
        ApplyFilterSort();
        InvalidateSecondaryViews(); // identities and icons resolved: refresh graph/plan/categories when shown
        await CheckUpdatesAsync(gen, forceUpdateCheck);
    }

    /// <summary>Marks the graph, planning and categories views stale so each rebuilds the next time it
    /// is shown, and refreshes whichever one is on screen. Only the visible view is rebuilt, and only
    /// when its data changed.</summary>
    private void InvalidateSecondaryViews()
    {
        _graphDirty = _planDirty = _categoriesDirty = true;
        switch (Tabs.SelectedIndex)
        {
            case TabGraph when _graph is not null: _graphDirty = false; Defer(LoadGraph); break;
            case TabPlan when _plan is not null: _planDirty = false; Defer(LoadPlan); break;
            case TabCategories when _categories is not null: _categoriesDirty = false; Defer(LoadCategories); break;
        }
    }

    // ── building the heavy sub-tabs ──────────────────────────────────────────────

    /// <summary>
    /// Runs a heavy view build on a later turn of the dispatcher instead of inline.
    /// </summary>
    /// <remarks>
    /// A large pack means tens of thousands of visuals. At <see cref="DispatcherPriority.Background"/>
    /// the tab switch and its "building..." label render first, so the click visibly does something.
    /// Exceptions are caught here because nothing awaits this.
    /// </remarks>
    private void Defer(Action build) =>
        Dispatcher.InvokeAsync(() =>
        {
            try { build(); }
            catch (Exception ex) { ListStatus.Text = "Could not build that view: " + ex.Message; }
        }, DispatcherPriority.Background);

    // All secondary views get the same callbacks, so a mod's right-click menu is the same on every
    // tab. They reuse the List view's UpdateManyAsync / RecheckUpdatesAsync, so update rules match.
    private void LoadGraph() => _graph?.Load(_pack.Id, _all, _shell, OpenModPage, RequestReload,
        NoteSecondaryViewEdit, UpdateMany, RecheckUpdates);

    private void LoadCategories() => _categories?.Load(_pack.Id, _all, _shell, NoteSecondaryViewEdit,
        OpenModPage, RequestReload, UpdateMany, RecheckUpdates);

    private void LoadPlan() => _plan?.Load(_pack.Id, _all, _shell, OpenModPage, NoteSecondaryViewEdit,
        RequestReload, UpdateMany, RecheckUpdates);

    private void UpdateMany(IReadOnlyList<PackMod> mods) => _ = UpdateManyAsync(mods);
    private void RecheckUpdates(IReadOnlyList<PackMod> mods) => _ = RecheckUpdatesAsync(mods);

    /// <summary>An edit made inside the graph, planning or categories view. That view has already
    /// re-rendered, so this marks the other two stale and refreshes the list model.</summary>
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

    /// <summary>Keeps the viewport still across a rebuild (see <see cref="ListScrollAnchor"/>).</summary>
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
                || (m.ContentSize > 0 && m.ContentSizeLabel.Contains(query, StringComparison.OrdinalIgnoreCase))
                // Match words in the mod's note too.
                || (m.Note is { } note && note.Contains(query, StringComparison.OrdinalIgnoreCase))
                // ...and the jar's name, which is often all a crash log gives you.
                || m.FileName.Contains(query, StringComparison.OrdinalIgnoreCase));

        if (HideDisabled.IsChecked == true) q = q.Where(m => m.Enabled);
        if (OnlyUpdates.IsChecked == true) q = q.Where(m => m.HasUpdate);
        q = SourceFilterBox.SelectedIndex switch
        {
            1 => q.Where(m => m.PrimarySource == ModSource.CurseForge),
            2 => q.Where(m => m.PrimarySource == ModSource.Modrinth),
            3 => q.Where(m => m.IsExternal),
            _ => q
        };
        // The same flags the planning board can query.
        q = FlagFilterBox.SelectedIndex switch
        {
            1 => q.Where(m => m.IsLibrary),
            2 => q.Where(m => m.IsTesting),
            3 => q.Where(m => m.IsExtra),
            4 => q.Where(m => m.IsUpdateLocked),
            5 => q.Where(m => m.HasNote),
            6 => q.Where(m => m.HasConflict),
            7 => q.Where(m => m.Side == ModSide.Client),
            8 => q.Where(m => m.Side == ModSide.Server),
            _ => q
        };

        var cmp = StringComparer.OrdinalIgnoreCase;
        var rev = _reverseSort;
        // Primary key by sort mode (natural direction, flipped when reversed), then priority, then
        // name. Sorting by category groups the list instead, in the Categories page's order, with the
        // group headers drawn from that.
        if (SortBox.SelectedIndex == (int)ModListSortMode.Category)
        {
            ApplyCategoryGrouping(q, rev);
            return;
        }

        IOrderedEnumerable<PackMod> ordered = SortBox.SelectedIndex switch
        {
            // Name: A-Z by default
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
        // Each rebuild replaces the source, so remember which row was at the top and scroll back to
        // it afterwards.
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

        UpdateEmptyStates(list.Count);

        if (_all.Count == 0) { ListStatus.Text = "No mods installed."; return; }
        var disabled = _all.Count(m => !m.Enabled);
        var status = $"{_all.Count} mod(s)";
        if (disabled > 0) status += $"  ·  {disabled} disabled";
        if (updates > 0) status += $"  ·  {updates} update(s)";
        if (list.Count != _all.Count) status += $"  ·  showing {list.Count}";
        if (_identifying) status += "  ·  identifying...";
        ListStatus.Text = status;
    }

    /// <summary>
    /// The category sort: one heading per category, in the Categories page's order, with
    /// "Uncategorized" last.
    /// </summary>
    /// <remarks>
    /// A grouped <see cref="ListCollectionView"/>, so WPF draws the headers and keeps virtualizing
    /// (IsVirtualizingWhenGrouping) while the items stay plain PackMods. Groups appear in the order of
    /// their first item, so sorting items by category rank orders the headings.
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
        // "Uncategorized" stays last in both directions; reversing only re-orders the real categories.
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

        UpdateEmptyStates(list.Count);

        var groups = list.Select(m => m.PrimaryCategory).Distinct(cmp).Count();
        var status = $"{_all.Count} mod(s)  ·  {groups} categor{(groups == 1 ? "y" : "ies")}";
        if (list.Count != _all.Count) status += $"  ·  showing {list.Count}";
        if (updates > 0) status += $"  ·  {updates} update(s)";
        if (_identifying) status += "  ·  identifying...";
        ListStatus.Text = status;
    }

    private bool _rebuildingFilters;

    // ── empty states ─────────────────────────────────────────────────────────────

    /// <summary>Shows whichever empty state applies (an empty pack, or filters hiding everything) and
    /// names the filters doing the hiding.</summary>
    private void UpdateEmptyStates(int shown)
    {
        if (EmptyState is null || EmptyFilterState is null) return;

        var packEmpty = _all.Count == 0;
        var filteredOut = !packEmpty && shown == 0;
        EmptyState.Visibility = packEmpty ? Visibility.Visible : Visibility.Collapsed;
        EmptyFilterState.Visibility = filteredOut ? Visibility.Visible : Visibility.Collapsed;
        if (!filteredOut) return;

        var active = new List<string>();
        if (!string.IsNullOrWhiteSpace(SearchBox.Text)) active.Add($"the search '{SearchBox.Text!.Trim()}'");
        if (HideDisabled.IsChecked == true) active.Add("'Hide disabled'");
        if (OnlyUpdates.IsChecked == true) active.Add("'Only updates'");
        if (SourceFilterBox.SelectedIndex > 0)
            active.Add($"the {(SourceFilterBox.SelectedItem as ComboBoxItem)?.Content} filter");
        if (FlagFilterBox.SelectedIndex > 0)
            active.Add($"the {(FlagFilterBox.SelectedItem as ComboBoxItem)?.Content} filter");

        EmptyFilterDetail.Text = active.Count == 0
            ? $"None of this pack's {_all.Count} mods are showing."
            : $"All {_all.Count} mods are hidden by {Join(active)}.";

        static string Join(List<string> parts) => parts.Count switch
        {
            1 => parts[0],
            2 => parts[0] + " and " + parts[1],
            _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1]
        };
    }

    /// <summary>"Clear filters" on the filtered-out empty state: resets every toolbar filter at once.</summary>
    private void OnClearFilters(object sender, RoutedEventArgs e)
    {
        _rebuildingFilters = true;   // one re-sort at the end, not one per control
        try
        {
            SearchBox.Text = "";
            HideDisabled.IsChecked = false;
            OnlyUpdates.IsChecked = false;
            SourceFilterBox.SelectedIndex = 0;
            FlagFilterBox.SelectedIndex = 0;
        }
        finally { _rebuildingFilters = false; }
        SaveListPreferences();
        ApplyFilterSort();
    }

    /// <summary>"Browse for mods" on the empty-pack state: switches to the Mod browsing sub-tab.</summary>
    private void OnEmptyBrowse(object sender, RoutedEventArgs e) => Tabs.SelectedIndex = TabBrowse;

    private static Brush? AccentPaletteBrushFor(string? hex) =>
        hex is null ? null : AccentPalette.Brush(hex, (Brush)Application.Current.Resources["TextSecondaryBrush"]);

    /// <summary>The token of the update check the latest reload started. A newer reload cancels the
    /// older check, whose answers would be discarded anyway and which could still have minutes of
    /// paced store requests queued.</summary>
    private CancellationTokenSource? _checkCts;

    private async Task CheckUpdatesAsync(int gen, bool forceRefresh = false)
    {
        if (forceRefresh) ListStatus.Text = $"Re-checking {_all.Count} mod(s) with the stores...";
        _checkCts?.Cancel();
        var cts = _checkCts = new CancellationTokenSource();
        var summary = await RunUpdateCheckAsync(_all.ToList(), () => gen != _gen, forceRefresh, report: forceRefresh, cts.Token);
        if (gen != _gen) return;
        // Update results are in; refresh the "has update" group cards and the list.
        ListEdited();
        AppendCheckNote(summary, withTiming: forceRefresh);
    }

    /// <summary>Re-checks just the given mods, after their update channel or followed store changed.</summary>
    private async Task RecheckUpdatesAsync(IReadOnlyList<PackMod> mods)
    {
        var gen = _gen;
        ListStatus.Text = mods.Count == 1 ? $"Re-checking {mods[0].DisplayName}..." : $"Re-checking {mods.Count} mod(s)...";
        var summary = await RunUpdateCheckAsync(mods.ToList(), () => gen != _gen, forceRefresh: true);
        if (gen != _gen) return;
        ListEdited();
        AppendCheckNote(summary, withTiming: false);
    }

    /// <summary>The update check. <see cref="ModUpdater.CheckManyAsync"/> answers most mods with a few
    /// bulk requests, then goes through the rest one at a time at the pace set in Settings. The update
    /// rule is <see cref="ModUpdater.PickUpdate"/> and answers come from the shared catalog, so the
    /// Mods tab and this page never fetch the same thing twice.</summary>
    /// <param name="report">Write a running "n of N" count into the status strip, for user-triggered
    /// refreshes where the one-at-a-time part can take a while.</param>
    /// <returns>What the check found, or null when it was cancelled or could not run at all.</returns>
    private async Task<ModUpdateCheckSummary?> RunUpdateCheckAsync(List<PackMod> mods, Func<bool> stale,
        bool forceRefresh = false, bool report = false, CancellationToken ct = default)
    {
        var mc = _pack.MinecraftVersion;
        var loader = ModUpdater.LoaderTag(_pack);
        var pump = new ModUpdateCheckPump(Dispatcher, stale, report ? p => ListStatus.Text = p.Describe() : null);
        try
        {
            return await ModUpdater.CheckManyAsync(mods, mc, loader, forceRefresh, pump.Progress, pump.Post, ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(ModManagementView), ex);
            return null;
        }
        finally { pump.Stop(); }
    }

    /// <summary>Appends the check's results to the summary line <see cref="ListEdited"/> just wrote:
    /// how long a refresh took, and how many mods couldn't be checked, so a throttled check doesn't
    /// look like an up-to-date pack.</summary>
    private void AppendCheckNote(ModUpdateCheckSummary? summary, bool withTiming)
    {
        if (summary is null || _all.Count == 0) return;
        var parts = new List<string>();
        if (withTiming) parts.Add($"checked in {ModUpdater.Duration(summary.Elapsed)}");
        if (summary.CouldNotCheck is { } failed) parts.Add(failed + " - try again later");
        if (parts.Count > 0) ListStatus.Text += "  ·  " + string.Join("  ·  ", parts);
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

        // Graph, Planning and Categories are heavy to build, so they are built on first view and
        // rebuilt only after InvalidateSecondaryViews() marks them dirty, not on every tab switch.
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
                _graphDirty = false;
                _graph.ShowBuilding();   // paints before the build starts, so the tab is never blank
                Defer(LoadGraph);
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
                _categoriesDirty = false;
                Defer(LoadCategories);
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
                _planDirty = false;
                Defer(LoadPlan);
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

        // Coming back from the browser: mods may have been downloaded, so re-scan.
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

    /// <summary>Opens a mod's page inside the launcher: switches to the Mod browsing sub-tab and shows
    /// the mod in its detail panel.</summary>
    private void OpenModPage(PackMod mod) => _ = OpenModPageAsync(mod);

    private async Task OpenModPageAsync(PackMod mod)
    {
        var summary = mod.PrimaryMod;
        if (summary is null)
        {
            ListStatus.Text = $"{mod.DisplayName} isn't identified yet - try again in a moment.";
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
        if (IsLoaded && !_rebuildingFilters) ApplyFilterSort();
    }

    private void OnSearchFocusChanged(object sender, KeyboardFocusChangedEventArgs e) => UpdateSearchPlaceholder();

    // ── one-time entrance animation ────────────────────────────────────────────────
    // Stagger the cards in only on the first build. Later edits and filter/sort changes update in
    // place without re-animating.

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
        if (!IsLoaded || _rebuildingFilters) return;
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
    /// <summary>Guards <see cref="OnRefresh"/> against a second click (or F5) while one is running.</summary>
    private int _refreshing;

    /// <summary>
    /// Refresh: re-scan the mods folder and ask the stores again instead of reusing this session's
    /// cached version lists (see <see cref="ModVersionCatalog"/>). Reopening the page reuses the
    /// cache; this button forces a real re-check.
    /// </summary>
    /// <remarks>
    /// The whole cycle (scan, identify, update check) runs under one busy flag. The scan returns
    /// almost at once, and a second press during the slow part would send the same store traffic
    /// again.
    /// </remarks>
    private async void OnRefresh(object s, RoutedEventArgs e)
    {
        if (Interlocked.Exchange(ref _refreshing, 1) != 0) return;
        BeginRefreshFeedback();
        try
        {
            await ReloadAsync(forceUpdateCheck: true);
            await _enrichTask;   // identities + the forced update check
        }
        catch (Exception ex)
        {
            ListStatus.Text = "Refresh failed: " + ex.Message;
        }
        finally
        {
            EndRefreshFeedback();
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    /// <summary>Disables the refresh button and spins its glyph while the refresh runs, so it reads
    /// as working rather than broken.</summary>
    private void BeginRefreshFeedback()
    {
        RefreshButton.IsEnabled = false;
        var spin = new RotateTransform();
        RefreshButton.RenderTransform = spin;
        spin.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    private void EndRefreshFeedback()
    {
        if (RefreshButton.RenderTransform is RotateTransform spin)
        {
            spin.BeginAnimation(RotateTransform.AngleProperty, null);
            spin.Angle = 0;
        }
        RefreshButton.RenderTransform = Transform.Identity;
        RefreshButton.IsEnabled = true;
    }

    // ── adding a jar by hand ──────────────────────────────────────────────────────

    /// <summary>"Add file": copies mod jars picked on this PC into the pack's mods folder, for jars
    /// that aren't on either store (private builds, GitHub releases).</summary>
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

    /// <summary>The jars in a drag payload; anything else (a folder, a screenshot) is ignored.</summary>
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

    /// <summary>Copies the given jars into <c>game/mods</c> and re-scans. Existing files are only
    /// replaced if the user confirms.</summary>
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

                // Picked the pack's own jar: nothing to do. Checked before the replace prompt, since
                // replacing would delete the source file.
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

        // Re-scan first: ReloadAsync writes its own summary to the status strip and would overwrite
        // ours.
        if (added > 0) await ReloadAsync();

        var parts = new List<string>();
        if (added > 0) parts.Add($"Added {added} mod(s)");
        if (skipped > 0) parts.Add($"skipped {skipped}");
        if (ignored > 0) parts.Add($"ignored {ignored} non-jar file(s)");
        if (failed.Count > 0) parts.Add("failed: " + string.Join(", ", failed));
        ListStatus.Text = parts.Count == 0 ? "Nothing to add." : string.Join(" · ", parts) + ".";

        // Failures get a dialog, because the status strip is overwritten when the background identify
        // pass finishes. A successful add shows up in the list.
        if (failed.Count > 0)
            await AppDialog.MessageAsync(_shell, "Add file",
                "These files could not be added:" + Environment.NewLine + Environment.NewLine +
                string.Join(Environment.NewLine, failed));
        else if (ignored > 0 && added == 0)
            await AppDialog.MessageAsync(_shell, "Add file",
                "Nothing was added - only .jar files belong in a pack's mods folder.");
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
            ReportLaunchStatus("Launching test set...");
            var proc = await App.State.Launcher.LaunchTrackedAsync(_pack, new Progress<string>(ReportLaunchStatus));

            void RestoreAfterTest()
            {
                App.State.TestScope.Restore(_pack.Id);
                RunTestButton.IsEnabled = true;
                ListStatus.Text = "Test run finished - full mod set restored.";
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

    // ── advanced settings ─────────────────────────────────────────────────────────

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

    // ── Files-tab mod tools ───────────────────────────────────────────────────────

    private void OnSideTools(object s, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = s as UIElement, Placement = PlacementMode.Bottom };

        MenuItem Add(string header, Action action, bool enabled = true, string? tip = null)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            if (tip is not null) mi.ToolTip = tip;
            // A disabled MenuItem swallows its own tooltip, so the reason has to survive the disable.
            if (tip is not null) ToolTipService.SetShowOnDisabled(mi, true);
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
            return mi;
        }

        var toLocal = _all.Count(m => m.Side == ModSide.Server && !m.IsLocal);
        var toGame = _all.Count(m => m.IsLocal);
        Add(toLocal == 0 ? "Route server-only mods to local/" : $"Route {toLocal} server-only mod(s) to local/",
            () => _ = RouteServerToLocalAsync(), toLocal > 0,
            "Move server-marked jars out of the synced game/mods folder so they never ship to clients.");
        Add(toGame == 0 ? "Bring local mods back to game/" : $"Bring {toGame} local mod(s) back to game/",
            () => _ = RouteLocalToGameAsync(), toGame > 0,
            "Move jars in local/mods back into the synced game/mods folder.");

        menu.Items.Add(new Separator());
        Add("Enable all 'extra' mods", () => SetExtras(true));
        Add("Disable all 'extra' mods", () => SetExtras(false));

        menu.Items.Add(new Separator());
        Add("Import mod settings from another instance...", () => _ = ImportModSettingsAsync(), _all.Count > 0,
            "Copy categories, priorities, sizes, sides, notes and locks from another instance onto the mods this pack shares with it.");

        menu.Items.Add(new Separator());
        Add("Copy mod list", CopyModList, _all.Count > 0);
        Add("Export mod list...", () => _ = ExportModListAsync(), _all.Count > 0);
        Add("Export as a modpack...", () => _ = ExportPackDialog.ShowAsync(_shell, _pack));

        menu.Items.Add(new Separator());
        Add("Export server + client mod folder", ExportSideFolder, _pack.IsShared,
            _pack.IsShared ? null : "Needs 'Host this instance on the server' - the split only means something for a pack collaborators download.");
        menu.IsOpen = true;
    }

    /// <summary>Moves server-only mods out of the synced game/ folder into per-user local/ so they
    /// don't ship to clients.</summary>
    /// <remarks>Asks first with the count, since collaborators stop receiving these mods. Failures
    /// (often a jar the running game has open) are collected and reported.</remarks>
    private async Task RouteServerToLocalAsync()
    {
        var targets = _all.Where(m => m.Side == ModSide.Server && !m.IsLocal).ToList();
        if (targets.Count == 0) { ListStatus.Text = "No server-only mods in game/ to route."; return; }

        var go = await AppDialog.ConfirmAsync(_shell, "Route server-only mods",
            $"Move {targets.Count} server-only mod(s) out of game/mods into local/mods?\n\n" +
            "They stop syncing to collaborators. You can move them back from this menu.",
            "Move", "Cancel");
        if (!go) return;

        await MoveModsAsync(targets, Path.Combine(App.State.Packs.LocalDir(_pack.Id), "mods"), "local/");
    }

    /// <summary>The reverse trip: put per-user local/ jars back into the synced game/ folder.</summary>
    private async Task RouteLocalToGameAsync()
    {
        var targets = _all.Where(m => m.IsLocal).ToList();
        if (targets.Count == 0) { ListStatus.Text = "No mods in local/ to move."; return; }

        var go = await AppDialog.ConfirmAsync(_shell, "Bring local mods back",
            $"Move {targets.Count} mod(s) from local/mods back into game/mods?\n\n" +
            "They start syncing to collaborators again.",
            "Move", "Cancel");
        if (!go) return;

        await MoveModsAsync(targets, Path.Combine(App.State.Packs.GameDir(_pack.Id), "mods"), "game/");
    }

    private async Task MoveModsAsync(IReadOnlyList<PackMod> mods, string destDir, string label)
    {
        var failed = new List<string>();
        var moved = 0;
        try
        {
            Directory.CreateDirectory(destDir);
            foreach (var m in mods)
            {
                var name = Path.GetFileName(m.FilePath);
                try
                {
                    var dest = Path.Combine(destDir, name);
                    if (File.Exists(dest)) File.Delete(dest);
                    File.Move(m.FilePath, dest);
                    moved++;
                }
                catch (Exception ex) { failed.Add($"{name} ({ex.Message})"); }
            }
        }
        catch (Exception ex)
        {
            ListStatus.Text = "Could not move mods: " + ex.Message;
            return;
        }

        await ReloadAsync();   // writes its own summary, so report after it
        ListStatus.Text = $"Moved {moved} mod(s) to {label}" +
                          (failed.Count > 0 ? $" · {failed.Count} could not be moved." : ".");
        if (failed.Count > 0)
            await AppDialog.MessageAsync(_shell, "Move mods",
                "These files could not be moved - they may be open in a running game:" +
                Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, failed));
    }

    /// <summary>
    /// Copies another instance's categories and per-mod settings onto the mods this pack shares with
    /// it.
    /// </summary>
    /// <remarks>
    /// Same flow as on the Categories tab. Mods match by store id, so different versions still line
    /// up, and the import only ever adds.
    /// </remarks>
    private async Task ImportModSettingsAsync()
    {
        try
        {
            var packs = (await App.State.Api.ListPacksAsync())
                .Where(p => p.Id != _pack.Id && !App.State.Settings.IsPackHidden(p.Id))
                .ToList();
            if (packs.Count == 0)
            {
                ListStatus.Text = "There is no other instance to import from.";
                return;
            }

            var picker = new PackPickerDialog(packs, "Import mod settings from...",
                "Pick the instance to copy categories and per-mod settings from. Mods are matched across instances by their store id, so the same mod at a different version still lines up.",
                "Preview") { Owner = _shell };
            if (picker.ShowDialog() != true || picker.SelectedPackId is not { } sourceId) return;

            var source = packs.First(p => p.Id == sourceId);
            var plan = App.State.ModMetadata.PlanCategoryImport(sourceId, _pack.Id, _all);
            if (plan.IsEmpty)
            {
                ListStatus.Text = $"Nothing to import from {source.Name} - this pack already has its categories and settings.";
                return;
            }

            if (!await AppDialog.ConfirmAsync(_shell, $"Import from {source.Name}",
                    ModCategoriesView.DescribePlan(plan, source.Name), "Import", "Cancel"))
                return;

            App.State.ModMetadata.ApplyCategoryImport(_pack.Id, plan);
            ListStatus.Text = $"Imported {plan.NewCategories.Count} categor{(plan.NewCategories.Count == 1 ? "y" : "ies")}, " +
                              $"tagged {plan.TaggedMods} mod(s)" +
                              (plan.FlaggedMods > 0 ? $" and copied settings onto {plan.FlaggedMods}" : "") +
                              $" from {source.Name}.";
            ListEdited();
        }
        catch (Exception ex) { ListStatus.Text = "Import failed: " + ex.Message; }
    }

    // ── exporting the mod set ────────────────────────────────────────────────────

    /// <summary>
    /// The pack's mod set as text: one line per mod with its version, store, state and page link.
    /// </summary>
    /// <remarks>
    /// Markdown, since it usually gets pasted into Discord, a forum or a GitHub issue. Grouped by
    /// category when the List view is sorted that way, to match what is on screen.
    /// </remarks>
    private string BuildModListText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# {_pack.Name}");
        sb.AppendLine($"{_pack.MinecraftVersion} · {_pack.Loader} · {_all.Count} mod(s), " +
                      $"{_all.Count(m => m.Enabled)} enabled");
        sb.AppendLine();

        void Write(PackMod m)
        {
            var bits = new List<string> { m.VersionLabel, m.SourceLabel };
            if (!m.Enabled) bits.Add("disabled");
            if (m.IsLibrary) bits.Add("library");
            if (m.IsTesting) bits.Add("testing");
            if (m.IsExtra) bits.Add("extra");
            if (m.IsSideRestricted) bits.Add(m.Side == ModSide.Client ? "client-only" : "server-only");
            var line = $"- {m.DisplayName} ({string.Join(", ", bits)})";
            if (m.PageUrl is { } url) line += $" - {url}";
            sb.AppendLine(line);
        }

        var cmp = StringComparer.OrdinalIgnoreCase;
        if (SortBox.SelectedIndex == (int)ModListSortMode.Category)
        {
            foreach (var group in _all.GroupBy(m => m.PrimaryCategory, cmp)
                                      .OrderBy(g => g.Key, cmp))
            {
                sb.AppendLine($"## {group.Key}");
                foreach (var m in group.OrderBy(m => m.DisplayName, cmp)) Write(m);
                sb.AppendLine();
            }
        }
        else
        {
            foreach (var m in _all.OrderBy(m => m.DisplayName, cmp)) Write(m);
        }
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private void CopyModList()
    {
        if (_all.Count == 0) { ListStatus.Text = "Nothing to copy."; return; }
        ListStatus.Text = ClipboardHelper.TrySetText(BuildModListText())
            ? $"Copied {_all.Count} mod(s) to the clipboard."
            : "Could not reach the clipboard - try again.";
    }

    private async Task ExportModListAsync()
    {
        if (_all.Count == 0) { ListStatus.Text = "Nothing to export."; return; }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export mod list",
            FileName = SafeFileName(_pack.Name) + " mods.md",
            Filter = "Markdown (*.md)|*.md|Text file (*.txt)|*.txt|CSV (*.csv)|*.csv",
            AddExtension = true
        };
        if (dlg.ShowDialog(_shell) != true) return;

        var csv = dlg.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
        var text = csv ? BuildModListCsv() : BuildModListText();
        try
        {
            await File.WriteAllTextAsync(dlg.FileName, text);
            ListStatus.Text = $"Exported {_all.Count} mod(s) to {Path.GetFileName(dlg.FileName)}.";
        }
        catch (Exception ex)
        {
            ListStatus.Text = "Export failed: " + ex.Message;
            await AppDialog.MessageAsync(_shell, "Export mod list", "Could not write that file:\n\n" + ex.Message);
        }
    }

    /// <summary>The same set as a spreadsheet, for anyone diffing two packs.</summary>
    private string BuildModListCsv()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Name,Version,Source,Enabled,Categories,Priority,Content size,Side,File,Page");
        static string Q(string? v) => "\"" + (v ?? "").Replace("\"", "\"\"") + "\"";
        foreach (var m in _all.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase))
            sb.AppendLine(string.Join(",",
                Q(m.DisplayName), Q(m.VersionLabel), Q(m.SourceLabel), Q(m.Enabled ? "yes" : "no"),
                Q(m.CategoriesLabel), Q(m.Priority.ToString()), Q(m.ContentSizeLabel),
                Q(m.Side.ToString()), Q(m.FileName), Q(m.PageUrl)));
        return sb.ToString();
    }

    private static string SafeFileName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();

    private async void SetExtras(bool enabled)
    {
        var n = 0;
        foreach (var m in _all.Where(m => m.IsExtra && m.Enabled != enabled))
        {
            App.State.ModInventory.SetEnabled(m, enabled);
            n++;
        }
        ListStatus.Text = n == 0 ? "No 'extra' mods to change." : $"{(enabled ? "Enabled" : "Disabled")} {n} extra mod(s).";
        await ReloadAsync();
    }

    /// <summary>Copies every side-marked (client/server) mod into a side-mods/ folder split by side.
    /// Only when sharing is enabled.</summary>
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
        SafeLaunch.OpenFolder(root);
    }

    // ── card / row handlers ─────────────────────────────────────────────────────

    private static PackMod? ModOf(object sender) => (sender as FrameworkElement)?.DataContext as PackMod;

    /// <summary>
    /// Double-clicking a card opens the mod's page; a single click only ticks the checkbox.
    /// </summary>
    /// <remarks>The row's own buttons handle their clicks before this sees them.</remarks>
    private void OnCardLeftClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2 || ModOf(sender) is not { } mod) return;
        OpenModPage(mod);
        e.Handled = true;
    }

    private void OnCardRightClick(object sender, MouseButtonEventArgs e)
    {
        if (ModOf(sender) is not { } mod) return;
        ShowOptions(mod, sender as FrameworkElement);
        e.Handled = true;
    }

    /// <summary>
    /// Clicking a mod's name opens its store page in the browser.
    /// </summary>
    /// <remarks>
    /// Marked handled so <see cref="OnCardLeftClick"/> doesn't also open the detail page. A mod with no
    /// store page (an external jar) falls through to the normal row click.
    /// </remarks>
    private void OnModNameClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1 || ModOf(sender) is not { PageUrl: { } url }) return;
        ModOptionsMenu.OpenUrl(url);
        e.Handled = true;
    }

    /// <summary>Left-click on the "...": the short menu of common actions.</summary>
    private void OnOptions(object sender, RoutedEventArgs e)
    {
        if (ModOf(sender) is not { } mod) return;
        var menu = ModOptionsMenu.BuildQuick(TargetsFor(mod), BuildCtx());
        menu.PlacementTarget = sender as UIElement;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>Right-click on the "...": every option, the same menu a right-click on the row gives.</summary>
    /// <remarks>Must set <c>e.Handled</c>, or the event bubbles to the row's
    /// <see cref="OnCardRightClick"/> and opens a second menu.</remarks>
    private void OnOptionsRightClick(object sender, MouseButtonEventArgs e)
    {
        if (ModOf(sender) is not { } mod) return;
        ShowOptions(mod, sender as FrameworkElement);
        e.Handled = true;
    }

    /// <summary>Right-click on the empty part of the list: the same pack-level actions the planning
    /// board offers on empty canvas. Right-clicks on cards are handled there.</summary>
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
        if (ModOf(sender) is { } mod)
            ModOptionsMenu.RunUpdateButton(mod, m => _ = UpdateModAsync(m), m => _ = UpdateToVersionAsync(m));
    }

    /// <summary>Right-click on the Update button: the other kind of update. Handled, or it bubbles to
    /// the row and opens the full menu instead.</summary>
    private void OnUpdateRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement button && ModOf(sender) is { } mod)
            e.Handled = ModOptionsMenu.OpenUpdateButtonMenu(button, mod, m => _ = UpdateModAsync(m), m => _ = UpdateToVersionAsync(m));
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

    /// <summary>The last checkbox clicked without Shift: the anchor a Shift-click extends from.</summary>
    private PackMod? _selectionAnchor;

    /// <summary>
    /// Ticking a row's checkbox. Shift-click gives every row from the last plain click to this one
    /// this checkbox's new state.
    /// </summary>
    /// <remarks>No Ctrl handling: checkbox clicks are already additive. The anchor stays put on a
    /// Shift-click, so the next Shift-click redraws the range from the same start.</remarks>
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
        var selected = _all.Where(m => m.IsSelected).ToList();
        var n = selected.Count;
        SelectionBar.Visibility = n == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (n == 0) return;

        SelectionLabel.Text = $"{n} selected - Shift-click to select a range · Ctrl+A all · Esc clear · Del delete";

        // "Update" shows its count like the toolbar's Update all, and goes flat when nothing selected
        // has an update.
        var updatable = selected.Count(m => m.HasUpdate);
        BulkUpdateButton.Content = updatable > 0 ? $"Update ({updatable})" : "Update";
        BulkUpdateButton.IsEnabled = updatable > 0;
        BulkEnableButton.IsEnabled = selected.Any(m => !m.Enabled);
        BulkDisableButton.IsEnabled = selected.Any(m => m.Enabled);
    }

    // ── selection-bar bulk actions ───────────────────────────────────────────────
    // Each makes the same call as the right-click menu, so the dependency and conflict rules live
    // in one place.

    private IReadOnlyList<PackMod> Selected() => _all.Where(m => m.IsSelected).ToList();

    private async void OnBulkEnable(object sender, RoutedEventArgs e)
    {
        try { await SetEnabledManyAsync(Selected(), true); }
        catch (Exception ex) { ListStatus.Text = "Could not enable: " + ex.Message; }
    }

    private async void OnBulkDisable(object sender, RoutedEventArgs e)
    {
        try { await SetEnabledManyAsync(Selected(), false); }
        catch (Exception ex) { ListStatus.Text = "Could not disable: " + ex.Message; }
    }

    private async void OnBulkUpdate(object sender, RoutedEventArgs e)
    {
        try { await UpdateManyAsync(Selected()); }
        catch (Exception ex) { ListStatus.Text = "Update failed: " + ex.Message; }
    }

    private async void OnBulkDelete(object sender, RoutedEventArgs e)
    {
        try { await DeleteManyAsync(Selected()); }
        catch (Exception ex) { ListStatus.Text = "Delete failed: " + ex.Message; }
    }

    private void OnBulkOptions(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count == 0) return;
        var menu = ModOptionsMenu.Build(targets, BuildCtx());
        menu.PlacementTarget = sender as UIElement;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private async Task SetEnabledManyAsync(IReadOnlyList<PackMod> mods, bool enabled)
    {
        var list = mods.ToList();
        // Ask once up front, not once per mod part-way through the run.
        if (enabled)
        {
            var clashing = list.Where(m => EnabledConflictsFor(m).Count > 0).ToList();
            if (clashing.Count > 0 && !await AppDialog.ConfirmAsync(_shell, "Marked incompatible",
                    $"{NameList(clashing)} {(clashing.Count == 1 ? "is" : "are")} marked incompatible with " +
                    "mods that are already enabled.\n\nEnable the whole selection anyway?",
                    "Enable anyway", "Cancel", danger: true))
                return;
        }

        foreach (var m in list) await SetEnabledAsync(m, enabled, confirmConflicts: false);
        ApplyFilterSort();
    }

    private async Task UpdateManyAsync(IReadOnlyList<PackMod> mods)
    {
        var updatable = mods.Where(m => m.HasUpdate && m.LatestVersion is not null).ToList();
        if (updatable.Count == 0) { ListStatus.Text = "No updates available for the selection."; return; }

        // Update-locked mods sit out a bulk run. Updating a single locked mod offers the override.
        var held = updatable.Where(m => m.Meta.UpdateLocked).ToList();
        var run  = updatable.Where(m => !m.Meta.UpdateLocked).ToList();
        if (run.Count == 0)
        {
            if (held.Count > 1)
            {
                ListStatus.Text = $"All {held.Count} mods with updates are locked - unlock them to update.";
                return;
            }
            if (!await ConfirmLockedUpdateAsync(held[0])) { ListStatus.Text = ""; return; }
            run = held;
            held = new List<PackMod>();
        }

        var done = 0;
        foreach (var m in run)
        {
            ListStatus.Text = $"Updating {m.DisplayName}...";
            try { if (await ModUpdater.InstallVersionAsync(m, m.LatestVersion!)) done++; }
            catch (Exception ex) { ListStatus.Text = "Update failed: " + ex.Message; }
        }
        ListStatus.Text = $"Updated {done} mod(s)" + (held.Count > 0 ? $", skipped {held.Count} locked." : ".");
        await ReloadAsync();
    }

    /// <summary>Asks before updating past a lock the user set. Returns true to go ahead. The lock
    /// stays on afterwards.</summary>
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
        if (!SafeLaunch.OpenFolder(dir)) ListStatus.Text = "Could not open the folder.";
    }

    /// <param name="confirmConflicts">Ask before enabling a mod recorded as incompatible with something
    /// already on. Off for bulk runs, which report the count in the status strip instead.</param>
    private async Task SetEnabledAsync(PackMod mod, bool enabled, bool confirmConflicts = true)
    {
        var adv = App.State.ModMetadata.Advanced(_pack.Id);
        var inv = App.State.ModInventory;

        if (enabled && confirmConflicts && EnabledConflictsFor(mod) is { Count: > 0 } clashes)
        {
            var go = await AppDialog.ConfirmAsync(_shell, "Marked incompatible",
                $"{mod.DisplayName} is marked incompatible with {NameList(clashes)}, " +
                $"which {(clashes.Count == 1 ? "is" : "are")} enabled.\n\nEnable it anyway?",
                "Enable anyway", "Cancel", danger: true);
            if (!go) { mod.Refresh(); return; }   // Refresh puts the toggle switch back
        }

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
                return;
            }
            foreach (var m in also) inv.SetEnabled(m, false);
            ListStatus.Text = also.Count == 0
                ? $"Disabled {mod.DisplayName}."
                : $"Disabled {mod.DisplayName} and {also.Count} dependent(s).";
        }
        else
        {
            // Enabling a mod also enables its required dependencies, unless the Advanced tab's
            // "Auto-download and enable required dependencies" is off.
            var deps = adv.AutoDownloadDependencies
                ? ModGraphService.PlanEnable(_all, mod)
                : Array.Empty<PackMod>();
            if (!inv.SetEnabled(mod, true))
            {
                ListStatus.Text = $"Could not enable {mod.DisplayName}.";
                mod.Refresh();
                return;
            }
            foreach (var m in deps) inv.SetEnabled(m, true);
            ListStatus.Text = deps.Count == 0
                ? $"Enabled {mod.DisplayName}."
                : $"Enabled {mod.DisplayName} and {deps.Count} dependency(ies).";
        }

        ListEdited();
    }

    /// <summary>The enabled mods that <paramref name="mod"/> is recorded as incompatible with, in
    /// either direction, since metadata from older builds may only record one side.</summary>
    private List<PackMod> EnabledConflictsFor(PackMod mod)
    {
        var keys = new HashSet<string>(mod.CandidateKeys, StringComparer.OrdinalIgnoreCase);
        var mine = new HashSet<string>(mod.Meta.IncompatibleWith, StringComparer.OrdinalIgnoreCase);
        return _all
            .Where(o => !ReferenceEquals(o, mod) && o.Enabled
                        && (o.Meta.IncompatibleWith.Any(keys.Contains) || o.CandidateKeys.Any(mine.Contains)))
            .ToList();
    }

    /// <summary>"A", "A and B", "A, B and C", and "A, B and 4 more" past three.</summary>
    private static string NameList(IReadOnlyList<PackMod> mods)
    {
        var names = mods.Select(m => m.DisplayName).ToList();
        if (names.Count > 3) return string.Join(", ", names.Take(2)) + $" and {names.Count - 2} more";
        return names.Count switch
        {
            0 => "",
            1 => names[0],
            2 => names[0] + " and " + names[1],
            _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1]
        };
    }

    /// <summary>Re-renders the list and marks the heavy secondary views stale. Used wherever a
    /// List-view action changes mod data (flags, categories, enable/disable, notes, updates).</summary>
    private void ListEdited()
    {
        // A flag edit can be the one that creates (or clears) a conflict, and the pill has to follow.
        PackModInventory.ResolveConflicts(_all);
        ApplyFilterSort();
        InvalidateSecondaryViews();
    }

    private async Task UpdateModAsync(PackMod mod)
    {
        if (mod.LatestVersion is null) { ListStatus.Text = $"{mod.DisplayName} is up to date."; return; }

        if (mod.Meta.UpdateLocked && !await ConfirmLockedUpdateAsync(mod)) { ListStatus.Text = ""; return; }

        // Warn before updating a mod the user flagged as update-incompatible.
        if (mod.Meta.UpdateIncompatible && App.State.ModMetadata.Advanced(_pack.Id).WarnOnUpdateIncompatible)
        {
            var go = await AppDialog.ConfirmAsync(_shell, "Update warning",
                $"{mod.DisplayName} is marked as update-incompatible - updating it may break your setup.\n\nUpdate anyway?",
                "Update", "Cancel", danger: true);
            if (!go) return;
        }

        ListStatus.Text = $"Updating {mod.DisplayName}...";
        try
        {
            var file = mod.LatestVersion.Files.FirstOrDefault(f => f.IsPrimary) ?? mod.LatestVersion.Files.FirstOrDefault();
            if (file is null) { ListStatus.Text = "No downloadable file."; return; }
            file = await EnsureDownloadableAsync(mod.LatestVersion, file);
            if (string.IsNullOrWhiteSpace(file.DownloadUrl)) { ListStatus.Text = "No downloadable file."; return; }

            // Keep a disabled mod disabled after update. The file name comes from the store, so only
            // a plain name is written (see ModUpdater.InstallVersionAsync).
            var fileName = mod.Enabled ? file.Filename : file.Filename + ".disabled";
            if (!PathSafety.IsSafeFileName(file.Filename)
                || PathSafety.ResolveFileName(Path.GetDirectoryName(mod.FilePath)!, fileName) is not { } dest)
            {
                AppLog.Log(nameof(ModManagementView), $"Did not update {mod.DisplayName}: the store's file name is not a plain file name: {file.Filename}");
                ListStatus.Text = "Update failed: the store gave a file name that cannot be used.";
                return;
            }
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

        ListStatus.Text = $"Loading versions for {mod.DisplayName}...";
        var versions = await ModUpdater.FetchVersionsAsync(mod.PrimaryMod);
        if (versions.Count == 0) { ListStatus.Text = $"No versions found for {mod.DisplayName}."; return; }

        var loader = _pack.Loader == LoaderKind.None ? null : _pack.Loader.ToString().ToLowerInvariant();
        var chosen = await ModVersionPickerDialog.ShowAsync(_shell, mod.DisplayName, versions, _pack.MinecraftVersion, loader,
            mod.PrimaryVersion?.Id, mod.PrimaryVersion?.VersionNumber, mod.Meta.UpdateLocked);
        if (chosen is null) { ListStatus.Text = ""; return; }

        // A lock holds the mod at its version in either direction, unless the picker's "Keep this
        // version" box is ticked, which already says where the lock should go.
        if (mod.Meta.UpdateLocked && !chosen.KeepVersion && !await ConfirmLockedUpdateAsync(mod))
        { ListStatus.Text = ""; return; }

        // Update-incompatible warning still applies when the user picks a version manually.
        if (mod.Meta.UpdateIncompatible && App.State.ModMetadata.Advanced(_pack.Id).WarnOnUpdateIncompatible)
        {
            var go = await AppDialog.ConfirmAsync(_shell, "Update warning",
                $"{mod.DisplayName} is marked as update-incompatible - changing its version may break your setup.\n\nContinue?",
                "Install", "Cancel", danger: true);
            if (!go) return;
        }

        ListStatus.Text = $"Installing {mod.DisplayName} {chosen.Version.VersionNumber}...";
        try
        {
            if (await ModUpdater.InstallVersionAsync(mod, chosen.Version))
            {
                // After the install, so a download that failed never leaves the mod pinned to a
                // version it is not on.
                ModVersionPickerDialog.ApplyKeepVersion(_pack.Id, mod, chosen);
                ListStatus.Text = $"Installed {mod.DisplayName} {chosen.Version.VersionNumber}" +
                                  (chosen.KeepVersion ? " and locked it there." : ".");
                await ReloadAsync();
            }
            else ListStatus.Text = "That version has no downloadable file.";
        }
        catch (Exception ex) { ListStatus.Text = "Install failed: " + ex.Message; }
    }

}
