using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>A category name to the colour the user gave it, per control instance.</summary>
/// <remarks><see cref="CategoryToBrushConverter"/> keeps its lookup in a static, which breaks when
/// the per-pack hub is open over the global Mods page: the second to load would recolour the first
/// one's headings.</remarks>
public sealed class ModListCategoryBrushConverter : IValueConverter
{
    /// <summary>Resolves one category name. Set by the pane that owns this converter.</summary>
    public Func<string, Brush>? Lookup { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Lookup?.Invoke(value as string ?? "") ?? Brushes.Gray;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>What the page hosting a <see cref="ModListPane"/> does with the pane's gestures.</summary>
/// <remarks>
/// <para>A record of callbacks rather than an interface, like <see cref="ModOptionsContext"/>: every
/// entry is optional and an action left null does not appear in the menu. That lets one pane serve a
/// global list (where "delete" means every instance's copy) and a per-pack list (one file).</para>
/// <para>Nothing here is a <c>Brush</c> or a control: the pane draws, the host decides.</para>
/// </remarks>
public sealed class ModListPaneHost
{
    /// <summary>Which metadata document a flag edit writes to: an instance's id, or
    /// <see cref="ModMetadataService.GlobalScope"/>.</summary>
    public required Guid MetaScope { get; init; }

    /// <summary>The window dialogs should own.</summary>
    public MainWindow? Owner { get; init; }

    /// <summary>Called after an edit has been applied and persisted, so the host can re-render and
    /// write its own status line.</summary>
    public Action? Changed { get; init; }

    /// <summary>Re-reads the disk. Offered on the list's background menu.</summary>
    public Action? Reload { get; init; }

    /// <summary>Opens the mod's page in the launcher.</summary>
    public Action<GlobalModRow>? OpenPage { get; init; }

    /// <summary>Turns a whole selection on or off. The host owns the confirmation, because only it
    /// knows how many instances that touches.</summary>
    public Action<IReadOnlyList<GlobalModRow>, bool>? SetEnabled { get; init; }

    /// <summary>Deletes the jars. The host owns the wording, since "delete" can mean three different
    /// things on a global row.</summary>
    public Action<IReadOnlyList<GlobalModRow>>? Delete { get; init; }

    /// <summary>Shows one row's file in Explorer.</summary>
    public Action<GlobalModRow>? Reveal { get; init; }

    /// <summary>Opens the rules editor for a default.</summary>
    public Action<GlobalModRow>? EditRules { get; init; }

    /// <summary>Makes these mods defaults: into the library, with a rule.</summary>
    public Action<IReadOnlyList<GlobalModRow>>? MakeDefault { get; init; }

    /// <summary>Stops these being defaults: out of the library, instances untouched.</summary>
    public Action<IReadOnlyList<GlobalModRow>>? StopBeingDefault { get; init; }

    /// <summary>Puts these mods into instances the user picks.</summary>
    /// <remarks>Unlike <see cref="MakeDefault"/> (everywhere it fits, from now on), this is only the
    /// chosen instances. The host owns the picker, since only it knows the instances and what each
    /// runs.</remarks>
    public Action<IReadOnlyList<GlobalModRow>>? AddToInstances { get; init; }

    /// <summary>Files these mods in a folder, or takes them out of it.</summary>
    public Action<IReadOnlyList<GlobalModRow>, string, bool>? SetFolder { get; init; }

    /// <summary>The folders on offer, in display order.</summary>
    public Func<IReadOnlyList<string>>? Folders { get; init; }

    /// <summary>Whether a folder holds one mod, for the tick in the menu.</summary>
    public Func<GlobalModRow, string, bool>? InFolder { get; init; }

    /// <summary>Jars dropped onto the list.</summary>
    public Action<IReadOnlyList<string>>? DropFiles { get; init; }
}

/// <summary>The mod list (flag pills, note, enable switch and the per-pack hub's options menu) over
/// whatever set the host hands it.</summary>
/// <remarks>Shared by the global Mods page and the per-pack hub so both behave the same. The pane
/// owns filtering, sorting, grouping and the selection bar; the host owns the controls and passes a
/// predicate and a comparison. Rows are updated with <see cref="ListDiff.Apply"/> rather than by
/// replacing <see cref="ItemsSource"/>, which would lose the scroll offset, selection and focus
/// (grouping by category is the exception).</remarks>
public partial class ModListPane : UserControl
{
    private readonly ObservableCollection<GlobalModRow> _visible = new();
    private ModListPaneHost? _host;

    public ModListPane()
    {
        InitializeComponent();
        ModItems.ItemsSource = _visible;
        if (Resources["CategoryBrush"] is ModListCategoryBrushConverter converter)
            converter.Lookup = name => CategoryBrushFor(name);
    }

    /// <summary>Everything the scan found, before the pane narrows it. The page's load diffs into
    /// this collection (see <see cref="PageRefresh"/>) and then calls
    /// <see cref="ApplyFilterSort"/>.</summary>
    public ObservableCollection<GlobalModRow> All { get; } = new();

    /// <summary>The control the user is looking at, for <see cref="PageRefresh"/>'s scroll anchor.</summary>
    public ItemsControl List => ModItems;

    /// <summary>The rows actually on screen, in the order they are drawn.</summary>
    public IReadOnlyList<GlobalModRow> Visible => _visible;

    /// <summary>The rows the host's <see cref="Filter"/> admits, whatever mode the list is in.</summary>
    /// <remarks>Not the same as <see cref="Visible"/>: while <see cref="GroupByCategory"/> is on,
    /// <see cref="ApplyCategoryGrouping"/> puts a <see cref="ListCollectionView"/> in
    /// <c>ItemsSource</c> and the collection behind <see cref="Visible"/> goes stale. Re-running the
    /// predicate can't go stale, which matters because the Mods page draws its Categories board from
    /// this.</remarks>
    public IReadOnlyList<GlobalModRow> Filtered => Filter is null ? All.ToList() : All.Where(Filter).ToList();

    /// <summary>What the page's search box and filters admit. Null shows everything.</summary>
    public Func<GlobalModRow, bool>? Filter { get; set; }

    /// <summary>The page's sort. Null keeps the order the scan produced, which is by name.</summary>
    public Comparison<GlobalModRow>? Sort { get; set; }

    /// <summary>Group the list under one heading per category, in the order the categories are
    /// declared.</summary>
    public bool GroupByCategory { get; set; }

    /// <summary>Wires up the host. Call once, before the first load.</summary>
    public void Bind(ModListPaneHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        UpdateSelectionBar();
    }

    // ── filtering, sorting, grouping ─────────────────────────────────────────

    /// <summary>
    /// Narrows <see cref="All"/> into the list and returns how many rows ended up on screen.
    /// </summary>
    /// <remarks>Safe to call on every keystroke: the diff raises one notification per row that
    /// actually moved and none at all when nothing did.</remarks>
    public int ApplyFilterSort()
    {
        var rows = Filter is null ? All.ToList() : All.Where(Filter).ToList();
        if (Sort is not null) rows.Sort(Sort);

        if (GroupByCategory)
        {
            ApplyCategoryGrouping(rows);
            UpdateSelectionBar();
            return rows.Count;
        }

        // Back from grouped to flat: rebind, since grouping put a view in ItemsSource.
        if (!ReferenceEquals(ModItems.ItemsSource, _visible)) ModItems.ItemsSource = _visible;

        ListDiff.Apply(_visible, rows, r => r.Key);
        UpdateSelectionBar();
        return rows.Count;
    }

    /// <summary>One heading per category, in declared order, with "Uncategorized" last.</summary>
    /// <remarks>A grouped <see cref="ListCollectionView"/>, so WPF draws the headers and keeps
    /// virtualising (<c>IsVirtualizingWhenGrouping</c>). Groups follow their first item, so sorting by
    /// category rank orders the headings. A grouping can't be diffed, so this replaces
    /// <see cref="ItemsSource"/> and relies on the page's scroll anchor.</remarks>
    private void ApplyCategoryGrouping(List<GlobalModRow> rows)
    {
        var scope = _host?.MetaScope ?? ModMetadataService.GlobalScope;
        var declared = App.State.ModMetadata.Categories(scope).Select(c => c.Name).ToList();

        int Rank(string name)
        {
            var i = declared.FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            return i < 0 ? int.MaxValue : i;   // a category nothing declares trails the declared ones
        }

        static bool IsUncategorized(string name) =>
            string.Equals(name, PackMod.UncategorizedName, StringComparison.OrdinalIgnoreCase);

        // "Uncategorized" stays pinned to the end, even when the sort is reversed.
        var ordered = rows
            .OrderBy(r => IsUncategorized(r.PrimaryCategory) ? 1 : 0)
            .ThenBy(r => Rank(r.PrimaryCategory))
            .ThenBy(r => r.PrimaryCategory, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var view = new ListCollectionView(ordered);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(GlobalModRow.PrimaryCategory)));
        ModItems.ItemsSource = view;
    }

    /// <summary>The colour a category heading is drawn in: the one the user picked, else a neutral.</summary>
    private Brush CategoryBrushFor(string name)
    {
        var scope = _host?.MetaScope ?? ModMetadataService.GlobalScope;
        try
        {
            var hex = App.State.ModMetadata.CategoryColor(scope, name);
            if (hex is { Length: > 0 } && ColorConverter.ConvertFromString(hex) is Color color)
                return new SolidColorBrush(color);
        }
        catch { /* unparseable colour: use the neutral one */ }
        return (Brush)FindResource("TextSecondaryBrush");
    }

    // ── row gestures ─────────────────────────────────────────────────────────

    private static GlobalModRow? RowOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as GlobalModRow;

    /// <summary>Double-clicking a row opens the mod's page; a single click only ticks the box.</summary>
    private void OnCardLeftClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2 || RowOf(sender) is not { } row) return;
        _host?.OpenPage?.Invoke(row);
        e.Handled = true;
    }

    private void OnCardRightClick(object sender, MouseButtonEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        ShowFullMenu(row, sender as FrameworkElement);
        e.Handled = true;
    }

    /// <summary>
    /// Clicking a mod's name opens its store page in the browser.
    /// </summary>
    /// <remarks>Single clicks only, marked handled so the row's double-click handler doesn't also open
    /// the launcher's page behind the browser. A mod with no store page falls through to the
    /// row.</remarks>
    private void OnModNameClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1 || RowOf(sender) is not { Mod.PageUrl: { } url }) return;
        ModOptionsMenu.OpenUrl(url);
        e.Handled = true;
    }

    private void OnNoteClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row || _host is null) return;
        // Written to the host's scope like every other flag.
        ModNotePopup.Show(sender as FrameworkElement, row.Mod, _host.MetaScope,
                          App.State.ModInventory, () => _host.Changed?.Invoke());
    }

    private void OnToggleEnabled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggle || RowOf(toggle) is not { } row) return;
        var want = toggle.IsChecked == true;

        // Snap the switch back to the row's state and let the host's confirmation decide. Re-notify the
        // row instead of assigning IsChecked: an assignment removes the one-way binding, and since
        // containers are recycled the switch would stop updating for every row that reuses it.
        row.Refresh();

        if (want == row.EnabledEverywhere && !row.MixedEnabled) return;
        _host?.SetEnabled?.Invoke(TargetsFor(row), want);
    }

    /// <summary>Left-click on the "..." button: the short menu of common actions.</summary>
    private void OnOptions(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        var targets = TargetsFor(row);
        var menu = ModOptionsMenu.BuildQuick(targets.Select(r => r.Mod).ToList(), BuildContext());
        AppendGlobalItems(menu, targets);
        menu.PlacementTarget = sender as UIElement;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>Right-click on the "..." button: every option, the same menu a right-click on the row
    /// gives.</summary>
    /// <remarks>Needs <c>e.Handled</c>, otherwise the event bubbles to the row's right-click handler and
    /// a second menu opens over this one.</remarks>
    private void OnOptionsRightClick(object sender, MouseButtonEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        ShowFullMenu(row, sender as FrameworkElement);
        e.Handled = true;
    }

    private void ShowFullMenu(GlobalModRow row, FrameworkElement? anchor)
    {
        var targets = TargetsFor(row);
        var menu = ModOptionsMenu.Build(targets.Select(r => r.Mod).ToList(), BuildContext());
        AppendGlobalItems(menu, targets);
        menu.PlacementTarget = anchor;
        menu.IsOpen = true;
    }

    /// <summary>The context the shared options menu edits through.</summary>
    /// <remarks><see cref="ModOptionsContext.PackId"/> is the host's scope, so passing
    /// <see cref="ModMetadataService.GlobalScope"/> makes every flag edit write the global document. File
    /// actions are only wired where the host supports them. Update is left out: updating a linked
    /// library mod would break the link and leave the library with the old jar.</remarks>
    private ModOptionsContext BuildContext() => new()
    {
        PackId = _host?.MetaScope ?? ModMetadataService.GlobalScope,
        Inventory = App.State.ModInventory,
        Owner = _host?.Owner,
        AllMods = All.Select(r => r.Mod).ToList(),
        OnChanged = () => _host?.Changed?.Invoke(),
        OnOpenPage = _host?.OpenPage is null ? null : mod => WithRow(mod, _host.OpenPage),
        OnReveal = _host?.Reveal is null ? null : mod => WithRow(mod, _host.Reveal),
        OnSetEnabled = _host?.SetEnabled is null
            ? null
            : (mods, enabled) => _host.SetEnabled(RowsFor(mods), enabled),
        // The shared menu's "Delete file" is wrong for a row that stands for several instances;
        // AppendGlobalItems adds a delete item that says what it does.
        OnDelete = null,
    };

    /// <summary>The global items the shared menu has no way to know about: which instances should get
    /// this mod, whether it is one of the user's defaults, its rule, and which folder it is filed in.</summary>
    private void AppendGlobalItems(ContextMenu menu, IReadOnlyList<GlobalModRow> targets)
    {
        if (_host is null || targets.Count == 0) return;
        var single = targets.Count == 1 ? targets[0] : null;
        var added = false;

        void Separate()
        {
            if (added) return;
            menu.Items.Add(new Separator());
            added = true;
        }

        // First, above "make it a default", as the smaller of the two: chosen instances rather than
        // everywhere it fits.
        if (_host.AddToInstances is not null)
        {
            Separate();
            menu.Items.Add(Item(
                targets.Count > 1 ? $"Add {targets.Count} mods to instances..." : "Add to instances...",
                () => _host.AddToInstances!.Invoke(targets)));
        }

        if (_host.MakeDefault is not null && targets.Any(r => !r.InLibrary))
        {
            Separate();
            var pending = targets.Where(r => !r.InLibrary).ToList();
            menu.Items.Add(Item(
                pending.Count > 1 ? $"Make {pending.Count} mods defaults..." : "Make this a default...",
                () => _host.MakeDefault!.Invoke(pending)));
        }

        if (_host.EditRules is not null && single is { InLibrary: true })
        {
            Separate();
            menu.Items.Add(Item("Edit rules...", () => _host.EditRules!.Invoke(single)));
        }

        if (_host.StopBeingDefault is not null && targets.Any(r => r.InLibrary))
        {
            Separate();
            var defaults = targets.Where(r => r.InLibrary).ToList();
            menu.Items.Add(Item(
                defaults.Count > 1 ? $"Stop {defaults.Count} being defaults..." : "Stop being a default...",
                () => _host.StopBeingDefault!.Invoke(defaults)));
        }

        if (_host.Delete is not null && targets.Any(r => r.InstanceCount > 0))
        {
            Separate();
            var instances = targets.SelectMany(r => r.Copies).Select(c => c.Instance.Id).Distinct().Count();
            menu.Items.Add(Item(
                instances == 1 ? "Delete from that instance..." : $"Delete from {instances} instances...",
                () => _host.Delete!.Invoke(targets)));
        }

        if (_host.SetFolder is not null && _host.Folders?.Invoke() is { Count: > 0 } folders)
        {
            Separate();
            var parent = new MenuItem { Header = "Folder" };
            foreach (var name in folders)
            {
                var folder = name;
                var inIt = _host.InFolder is not null && targets.All(r => _host.InFolder(r, folder));
                var entry = new MenuItem { Header = folder, InputGestureText = inIt ? "✓" : "" };
                entry.Click += (_, _) => _host.SetFolder!.Invoke(targets, folder, !inIt);
                parent.Items.Add(entry);
            }
            menu.Items.Add(parent);
        }

        static MenuItem Item(string header, Action onClick)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => onClick();
            return item;
        }
    }

    /// <summary>Maps the representatives the options menu handed back to their rows.</summary>
    /// <remarks>By reference, because two rows can share a display name and even a file name across
    /// instances.</remarks>
    private IReadOnlyList<GlobalModRow> RowsFor(IReadOnlyList<PackMod> mods)
    {
        var wanted = new HashSet<PackMod>(mods, ReferenceEqualityComparer<PackMod>.Instance);
        return All.Where(r => wanted.Contains(r.Mod)).ToList();
    }

    private void WithRow(PackMod mod, Action<GlobalModRow>? action)
    {
        if (action is null) return;
        var row = All.FirstOrDefault(r => ReferenceEquals(r.Mod, mod));
        if (row is not null) action(row);
    }

    /// <summary>Reference identity as a comparer, for mapping a menu's targets back to rows.</summary>
    private sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T> where T : class
    {
        public static readonly ReferenceEqualityComparer<T> Instance = new();
        public bool Equals(T? a, T? b) => ReferenceEquals(a, b);
        public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    // ── the list's own background ────────────────────────────────────────────

    private void OnListBackgroundRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_host is null) return;
        var menu = new ContextMenu { PlacementTarget = sender as UIElement };
        menu.Items.Add(ModCategoryMenu.Build(_host.MetaScope, All.Select(r => r.Mod).ToList(),
                                             _host.Owner, () => _host.Changed?.Invoke()));
        menu.Items.Add(new Separator());

        // The control's own items, not _visible, which is stale in grouped mode.
        var shown = VisibleRows();
        var selected = All.Count(r => r.IsSelected);
        menu.Items.Add(Action($"Select all {shown.Count} shown", () =>
        {
            foreach (var row in shown) row.IsSelected = true;
            UpdateSelectionBar();
        }, shown.Count > 0));
        menu.Items.Add(Action("Clear selection", () =>
        {
            foreach (var row in All) row.IsSelected = false;
            UpdateSelectionBar();
        }, selected > 0));

        if (_host.Reload is not null)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Action("Re-read every instance", () => _host.Reload!.Invoke()));
        }

        menu.IsOpen = true;
        e.Handled = true;

        static MenuItem Action(string header, Action onClick, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => onClick();
            return item;
        }
    }

    /// <summary>The rows the list is actually showing: the grouped view's items when grouped, the
    /// diffed collection otherwise.</summary>
    private List<GlobalModRow> VisibleRows() =>
        ModItems.Items.OfType<GlobalModRow>().ToList();

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        e.Effects = _host?.DropFiles is not null && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnListDrop(object sender, DragEventArgs e)
    {
        if (_host?.DropFiles is null) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        e.Handled = true;
        _host.DropFiles(paths);
    }

    // ── multi-select ─────────────────────────────────────────────────────────

    /// <summary>The rows an action acts on: the whole selection when the clicked row is part of it,
    /// otherwise just that row.</summary>
    /// <remarks>Same rule as every multi-select list in the app, so right-clicking an unselected row
    /// never acts on the selection.</remarks>
    public IReadOnlyList<GlobalModRow> TargetsFor(GlobalModRow row)
    {
        var selected = All.Where(r => r.IsSelected).ToList();
        return selected.Count > 1 && selected.Contains(row) ? selected : [row];
    }

    /// <summary>The current selection, or empty.</summary>
    public IReadOnlyList<GlobalModRow> Selection => All.Where(r => r.IsSelected).ToList();

    private void OnSelectToggle(object sender, RoutedEventArgs e) => UpdateSelectionBar();

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var row in VisibleRows()) row.IsSelected = true;
        UpdateSelectionBar();
    }

    private void OnClearSelection(object sender, RoutedEventArgs e)
    {
        foreach (var row in All) row.IsSelected = false;
        UpdateSelectionBar();
    }

    private void OnBulkEnable(object sender, RoutedEventArgs e) =>
        _host?.SetEnabled?.Invoke(Selection, true);

    private void OnBulkDisable(object sender, RoutedEventArgs e) =>
        _host?.SetEnabled?.Invoke(Selection, false);

    private void OnBulkOptions(object sender, RoutedEventArgs e)
    {
        var targets = Selection;
        if (targets.Count == 0) return;
        var menu = ModOptionsMenu.Build(targets.Select(r => r.Mod).ToList(), BuildContext());
        AppendGlobalItems(menu, targets);
        menu.PlacementTarget = sender as UIElement;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>Shows or hides the bulk bar and says what is selected. Public so the host can call it
    /// after an action of its own changed the selection.</summary>
    public void UpdateSelectionBar()
    {
        var count = All.Count(r => r.IsSelected);
        SelectionBar.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (count == 0) return;
        var instances = All.Where(r => r.IsSelected).Sum(r => r.InstanceCount);
        SelectionLabel.Text = instances == count
            ? $"{count} selected"
            : $"{count} selected  ·  {instances} cop{(instances == 1 ? "y" : "ies")} across your instances";
        var canEnable = _host?.SetEnabled is not null;
        BulkEnableButton.IsEnabled = canEnable;
        BulkDisableButton.IsEnabled = canEnable;
    }
}
