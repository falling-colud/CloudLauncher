using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>
/// The Categories sub-tab: a workbench for tagging mods in bulk.
///
/// The rest of the app only lets you toggle one mod's categories at a time from a context menu,
/// which is fine for a stray mod and miserable for organising a 400-mod pack. This is a three-pane
/// assignment view — categories, what's in the selected one, and everything else — so membership is
/// edited by multi-selecting and moving, or by dragging mods straight onto a category.
/// </summary>
public partial class ModCategoriesView : UserControl
{
    private const string ModDragFormat = "CloudLauncher.CategoryModKeys";
    private const string CategoryDragFormat = "CloudLauncher.CategoryName";

    private Guid _packId;
    private IReadOnlyList<PackMod> _mods = Array.Empty<PackMod>();
    private MainWindow? _owner;
    private Action? _onChanged;
    private string? _selectedCategory;
    private Point _dragOrigin;
    /// <summary>Which list a drag started from — the members list means "move", the candidates list
    /// means "add".</summary>
    private ListBox? _dragSource;
    private InsertionAdorner? _insertion;

    /// <summary>The line showing where a dragged category will land, drawn on the row it's nearest.</summary>
    private sealed class InsertionAdorner : System.Windows.Documents.Adorner
    {
        private readonly bool _below;

        public InsertionAdorner(UIElement row, bool below) : base(row)
        {
            _below = below;
            IsHitTestVisible = false;
        }

        /// <summary>Built per draw, not cached: a pen cached in a static field keeps whatever the
        /// accent was when the app started, and outlives a colour change.</summary>
        private static Pen BuildPen()
        {
            var pen = new Pen((Brush)Application.Current.FindResource("AccentBrush"), 2);
            pen.Freeze();
            return pen;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var row = (FrameworkElement)AdornedElement;
            var y = _below ? row.ActualHeight : 0;
            dc.DrawLine(BuildPen(), new Point(0, y), new Point(row.ActualWidth, y));
        }
    }

    /// <summary>A row in the categories list. Rebuilt wholesale on every refresh, so it needs no
    /// change notification of its own.</summary>
    private sealed class CategoryRow
    {
        public required string Name { get; init; }
        public required Brush Swatch { get; init; }
        public required int Count { get; init; }
        public required string Detail { get; init; }
        public bool Builtin { get; init; }
    }

    public ModCategoriesView()
    {
        InitializeComponent();
    }

    /// <param name="onOpenMod">Opens a mod's page in the launcher — the menu's top action.</param>
    /// <param name="onReload">Re-scans the pack's mods, after something that changes the files on
    /// disk (a delete, or installing a different version).</param>
    public void Load(Guid packId, IReadOnlyList<PackMod> mods, MainWindow? owner, Action? onChanged,
        Action<PackMod>? onOpenMod = null, Action? onReload = null)
    {
        _packId = packId;
        _mods = mods;
        _owner = owner;
        _onChanged = onChanged;
        _onOpenMod = onOpenMod;
        _onReload = onReload;
        Refresh();
    }

    private Action<PackMod>? _onOpenMod;
    private Action? _onReload;

    // ── refresh ─────────────────────────────────────────────────────────────────

    private void Refresh()
    {
        var meta = App.State.ModMetadata;

        // Stored order, not alphabetical — the list position is the user's chosen order.
        var rows = meta.Categories(_packId)
            .Select(c =>
            {
                var members = MembersOf(c.Name);
                return new CategoryRow
                {
                    Name = c.Name,
                    Swatch = AccentPalette.Brush(c.Color, (Brush)FindResource("TextTertiaryBrush")),
                    Count = members.Count,
                    Detail = DetailFor(members, c.Builtin),
                    Builtin = c.Builtin
                };
            })
            .ToList();

        // Keep the selection across a refresh; fall back to the first category.
        _selectedCategory ??= rows.FirstOrDefault()?.Name;
        CategoryList.ItemsSource = rows;
        CategoryList.SelectedItem = rows.FirstOrDefault(r =>
            string.Equals(r.Name, _selectedCategory, StringComparison.OrdinalIgnoreCase));
        if (CategoryList.SelectedItem is null) _selectedCategory = null;

        var untagged = _mods.Count(m => m.Meta.Categories.Count == 0);
        Summary.Text = $"{rows.Count} categor{(rows.Count == 1 ? "y" : "ies")}" +
                       $"   ·   {_mods.Count} mod{(_mods.Count == 1 ? "" : "s")}" +
                       (untagged > 0 ? $"   ·   {untagged} untagged" : "   ·   all tagged");

        RefreshLists();
    }

    private static string DetailFor(IReadOnlyList<PackMod> members, bool builtin)
    {
        var parts = new List<string>();
        if (builtin) parts.Add("library flag");

        var large = members.Count(m => m.ContentSize == 3);
        var medium = members.Count(m => m.ContentSize == 2);
        var small = members.Count(m => m.ContentSize == 1);
        if (large + medium + small > 0)
        {
            var sizes = new List<string>(3);
            if (large > 0) sizes.Add($"{large}L");
            if (medium > 0) sizes.Add($"{medium}M");
            if (small > 0) sizes.Add($"{small}S");
            parts.Add(string.Join(" ", sizes));
        }

        var disabled = members.Count(m => !m.Enabled);
        if (disabled > 0) parts.Add($"{disabled} off");

        return string.Join("  ·  ", parts);
    }

    private List<PackMod> MembersOf(string category) =>
        _mods.Where(m => m.Meta.Categories.Any(c => string.Equals(c, category, StringComparison.OrdinalIgnoreCase)))
             .OrderByDescending(m => m.Priority)
             .ThenByDescending(m => m.ContentSize)
             .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
             .ToList();

    private void RefreshLists()
    {
        if (_selectedCategory is null)
        {
            MembersHeader.Text = "IN THIS CATEGORY";
            MemberList.ItemsSource = null;
            CandidateList.ItemsSource = null;
            UpdateButtons();
            return;
        }

        var members = MembersOf(_selectedCategory);
        MembersHeader.Text = $"IN “{_selectedCategory.ToUpperInvariant()}”  ·  {members.Count}";
        MemberList.ItemsSource = members;

        var inCategory = new HashSet<PackMod>(members);
        IEnumerable<PackMod> candidates = _mods.Where(m => !inCategory.Contains(m));
        if (OnlyUntagged.IsChecked == true)
            candidates = candidates.Where(m => m.Meta.Categories.Count == 0);

        var query = SearchBox.Text?.Trim();
        if (!string.IsNullOrEmpty(query))
            candidates = candidates.Where(m =>
                m.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || m.CategoriesLabel.Contains(query, StringComparison.OrdinalIgnoreCase));

        CandidateList.ItemsSource = candidates
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        // Library membership is editable now (it drives the IsLibrary flag), so the only thing that
        // disables the buttons is having no category selected.
        var canEdit = _selectedCategory is not null;
        var library = _selectedCategory is not null && IsBuiltin(_selectedCategory);
        var members = MemberList.SelectedItems.Count;
        var candidates = CandidateList.SelectedItems.Count;

        AddButton.IsEnabled = canEdit && candidates > 0;
        AddButton.Content = library
            ? (candidates > 1 ? $"Mark {candidates} as library" : "Mark as library")
            : (candidates > 1 ? $"Add {candidates} to category" : "Add to category");
        RemoveButton.IsEnabled = canEdit && members > 0;
        RemoveButton.Content = library
            ? (members > 1 ? $"Unmark {members}" : "Unmark library")
            : (members > 1 ? $"Remove {members} from category" : "Remove from category");
    }

    private bool IsBuiltin(string name) =>
        App.State.ModMetadata.Categories(_packId)
            .Any(c => c.Builtin && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    // ── membership edits ────────────────────────────────────────────────────────

    /// <summary>The one place membership changes: everything else routes through here so the lists,
    /// the counts and the rest of the management page all refresh the same way.</summary>
    /// <param name="advance">List to re-select in afterwards. The moved rows have left it, so the
    /// row that slid into their place gets selected — tagging a run of mods becomes
    /// Enter-Enter-Enter instead of click-click-click.</param>
    private void Assign(IReadOnlyList<PackMod> mods, string category, bool member, ListBox? advance = null)
    {
        if (mods.Count == 0) return;

        var resumeAt = advance?.SelectedIndex ?? -1;

        var changed = App.State.ModMetadata.SetMembership(_packId, mods, category, member);
        // Editing Library membership is really toggling the IsLibrary flag — say so, since it has
        // effects (the disable cascade) beyond a plain tag.
        var isLibrary = IsBuiltin(category);
        Status.Text = changed == 0
            ? "Nothing to change."
            : isLibrary
                ? $"Marked {changed} mod{(changed == 1 ? "" : "s")} as {(member ? "library" : "not library")}."
                : $"{(member ? "Added" : "Removed")} {changed} mod{(changed == 1 ? "" : "s")} {(member ? "to" : "from")} “{category}”.";

        Refresh();
        _onChanged?.Invoke();

        if (advance is not null && resumeAt >= 0) SelectAfterMove(advance, resumeAt);
    }

    /// <summary>Re-selects at the index the moved rows vacated, clamped to the end of what's left.</summary>
    private static void SelectAfterMove(ListBox list, int index)
    {
        if (list.Items.Count == 0) return;
        list.SelectedIndex = Math.Min(index, list.Items.Count - 1);
        list.ScrollIntoView(list.SelectedItem);

        // Containers are generated after the items source is swapped, so grab focus once WPF has
        // caught up — otherwise the next Enter goes nowhere.
        list.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(list.SelectedIndex) is ListBoxItem row)
                row.Focus();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnAddSelected(object sender, RoutedEventArgs e)
    {
        if (_selectedCategory is null) return;
        Assign(CandidateList.SelectedItems.Cast<PackMod>().ToList(), _selectedCategory, true, CandidateList);
    }

    private void OnRemoveSelected(object sender, RoutedEventArgs e)
    {
        if (_selectedCategory is null) return;
        Assign(MemberList.SelectedItems.Cast<PackMod>().ToList(), _selectedCategory, false, MemberList);
    }

    private void OnCandidateDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_selectedCategory is null) return;
        if (ModUnder(e) is { } mod) Assign(new[] { mod }, _selectedCategory, true, CandidateList);
    }

    private void OnMemberDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_selectedCategory is null) return;
        if (ModUnder(e) is { } mod) Assign(new[] { mod }, _selectedCategory, false, MemberList);
    }

    /// <summary>Enter moves the selection across, so a run of mods can be tagged without the mouse.</summary>
    private void OnModListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _selectedCategory is null) return;
        e.Handled = true;
        if (ReferenceEquals(sender, CandidateList)) OnAddSelected(sender, e);
        else OnRemoveSelected(sender, e);
    }

    private static PackMod? ModUnder(MouseButtonEventArgs e) =>
        FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as PackMod;

    // ── drag mods onto a category ───────────────────────────────────────────────

    private void OnModListMouseDown(object sender, MouseButtonEventArgs e) => _dragOrigin = e.GetPosition(this);

    private void OnModListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not ListBox list) return;

        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        // Drag whatever is selected; if the press landed outside the selection, drag just that row.
        var picked = list.SelectedItems.Cast<PackMod>().ToList();
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is PackMod hit
            && !picked.Contains(hit))
            picked = new List<PackMod> { hit };
        if (picked.Count == 0) return;

        var keys = picked.Select(m => m.Key).ToArray();
        _dragSource = list;
        DragDrop.DoDragDrop(list, new DataObject(ModDragFormat, keys), DragDropEffects.Move);
        _dragSource = null;
    }

    // ── reorder categories ──────────────────────────────────────────────────────

    private void OnCategoryMouseDown(object sender, MouseButtonEventArgs e) => _dragOrigin = e.GetPosition(this);

    private void OnCategoryMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is not CategoryRow row) return;
        DragDrop.DoDragDrop(CategoryList, new DataObject(CategoryDragFormat, row.Name), DragDropEffects.Move);
        ClearInsertion();
    }

    private void OnCategoryDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(CategoryDragFormat))
        {
            // Dropping a category: show where it'll land, above or below whichever row you're over.
            e.Effects = DragDropEffects.Move;
            var (item, below) = RowUnder(e);
            ShowInsertion(item, below);
        }
        else
        {
            e.Effects = e.Data.GetDataPresent(ModDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
            ClearInsertion();
        }
        e.Handled = true;
    }

    private void OnCategoryDragLeave(object sender, DragEventArgs e) => ClearInsertion();

    /// <summary>The row under the pointer, and whether the pointer is in its lower half.</summary>
    private (ListBoxItem? item, bool below) RowUnder(DragEventArgs e)
    {
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item is null) return (null, false);
        return (item, e.GetPosition(item).Y > item.ActualHeight / 2);
    }

    private void ShowInsertion(ListBoxItem? row, bool below)
    {
        ClearInsertion();
        if (row is null) return;
        var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(row);
        if (layer is null) return;
        _insertion = new InsertionAdorner(row, below);
        layer.Add(_insertion);
    }

    private void ClearInsertion()
    {
        if (_insertion is null) return;
        System.Windows.Documents.AdornerLayer.GetAdornerLayer(_insertion.AdornedElement)?.Remove(_insertion);
        _insertion = null;
    }

    private void MoveCategory(string name, int targetIndex)
    {
        App.State.ModMetadata.MoveCategory(_packId, name, targetIndex);
        _selectedCategory = name;
        Refresh();
        _onChanged?.Invoke();
    }

    private int IndexOfCategory(string name)
    {
        var cats = App.State.ModMetadata.Categories(_packId);
        for (var i = 0; i < cats.Count; i++)
            if (string.Equals(cats[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private void OnCategoryDrop(object sender, DragEventArgs e)
    {
        var fromMembers = ReferenceEquals(_dragSource, MemberList);
        var source = _selectedCategory;
        var (row, below) = RowUnder(e);
        ClearInsertion();

        // Reordering: drop lands before or after the row depending on which half you released over.
        if (e.Data.GetData(CategoryDragFormat) is string moved)
        {
            e.Handled = true;
            if (row?.DataContext is not CategoryRow dropTarget) return;
            if (string.Equals(moved, dropTarget.Name, StringComparison.OrdinalIgnoreCase)) return;

            var index = IndexOfCategory(dropTarget.Name);
            if (index < 0) return;
            MoveCategory(moved, below ? index + 1 : index);
            return;
        }

        if (e.Data.GetData(ModDragFormat) is not string[] keys) return;
        if (row?.DataContext is not CategoryRow target) return;

        var lookup = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dropped = _mods.Where(m => m.CandidateKeys.Any(lookup.Contains)).ToList();
        if (dropped.Count == 0) return;
        e.Handled = true;

        if (fromMembers && string.Equals(target.Name, source, StringComparison.OrdinalIgnoreCase)) return; // already there

        // Out of the member list is a *move* — the mods leave the category they came from. Out of the
        // candidates list is a plain add, since they were never in one to begin with. Library is a
        // valid source or target either way now (it just toggles the IsLibrary flag).
        if (fromMembers && source is not null)
            App.State.ModMetadata.SetMembership(_packId, dropped, source, false);

        Assign(dropped, target.Name, true);
    }

    // ── categories list ─────────────────────────────────────────────────────────

    private void OnCategorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryList.SelectedItem is CategoryRow row) _selectedCategory = row.Name;
        // A one-time nudge that Library isn't an ordinary tag — adding here flips the IsLibrary flag,
        // which feeds the disable cascade.
        Status.Text = _selectedCategory is not null && IsBuiltin(_selectedCategory)
            ? "Adding a mod here marks it as a library mod (used by the dependency disable cascade)."
            : "";
        RefreshLists();
    }

    // ── mod context menu ─────────────────────────────────────────────────────

    /// <summary>
    /// Right-clicking a mod here opens the same options menu the List and Graph views use — enable,
    /// update, priority, side, library, lock, categories, notes, delete, the lot.
    /// </summary>
    /// <remarks>
    /// This screen is where a pack gets organised, so it is exactly where you notice that a mod needs
    /// its priority set or its side marked. Having to go back to the List view to do it broke the one
    /// thing this view is for. Right-clicking a mod inside the current selection keeps the selection
    /// and acts on all of it (the menu drops single-mod-only actions); right-clicking outside it
    /// selects that mod first, which is how every file list behaves.
    /// </remarks>
    private void OnModRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list) return;
        var row = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as PackMod;
        if (row is null) return;
        e.Handled = true;

        if (!list.SelectedItems.Contains(row))
        {
            list.SelectedItems.Clear();
            list.SelectedItems.Add(row);
        }

        var targets = list.SelectedItems.Cast<PackMod>().ToList();
        if (targets.Count == 0) targets.Add(row);

        var menu = ModOptionsMenu.Build(targets, BuildModOptionsContext());
        menu.PlacementTarget = list;
        menu.IsOpen = true;
    }

    private ModOptionsContext BuildModOptionsContext() => new()
    {
        PackId = _packId,
        Inventory = App.State.ModInventory,
        Owner = _owner,
        AllMods = _mods,
        // A category change made from the menu has to land in these lists straight away — this view
        // IS the category view.
        OnChanged = () => { Refresh(); _onChanged?.Invoke(); },
        OnOpenPage = OpenModPage,
        OnSetEnabled = (list, enabled) =>
        {
            foreach (var mod in list) App.State.ModInventory.SetEnabled(mod, enabled);
            Refresh();
            _onChanged?.Invoke();
        },
        OnUpdateToVersion = UpdateModToVersionAsync,
        OnDelete = DeleteModsAsync,
        OnReveal = mod =>
        {
            var dir = System.IO.Path.GetDirectoryName(mod.FilePath);
            if (dir is null) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true }); }
            catch (Exception ex) { Status.Text = "Could not open the folder: " + ex.Message; }
        }
    };

    private void OpenModPage(PackMod mod)
    {
        if (_onOpenMod is not null) { _onOpenMod(mod); return; }
        if (mod.PageUrl is null) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(mod.PageUrl) { UseShellExecute = true }); }
        catch (Exception ex) { Status.Text = "Could not open the page: " + ex.Message; }
    }

    private async void UpdateModToVersionAsync(PackMod mod)
    {
        if (mod.PrimaryMod is null || _owner is not MainWindow host) return;
        Status.Text = $"Loading versions for {mod.DisplayName}…";
        var versions = await ModUpdater.FetchVersionsAsync(mod.PrimaryMod);
        if (versions.Count == 0) { Status.Text = "No versions found."; return; }

        var mc = mod.PrimaryVersion?.GameVersions.FirstOrDefault();
        var loader = mod.PrimaryVersion?.Loaders.FirstOrDefault();
        var chosen = await ModVersionPickerDialog.ShowAsync(host, mod.DisplayName, versions, mc, loader,
            mod.PrimaryVersion?.Id, mod.PrimaryVersion?.VersionNumber);
        if (chosen is null) { Status.Text = ""; return; }

        if (mod.Meta.UpdateLocked && !await AppDialog.ConfirmAsync(host, "Mod is locked",
                $"{mod.DisplayName} is locked to its current version.\n\nChange it anyway? It stays locked afterwards.",
                "Change anyway", "Keep locked"))
        { Status.Text = ""; return; }

        Status.Text = $"Installing {chosen.VersionNumber}…";
        try
        {
            if (await ModUpdater.InstallVersionAsync(mod, chosen))
            {
                Status.Text = $"{mod.DisplayName} is now on {chosen.VersionNumber}.";
                _onReload?.Invoke();   // the jar changed on disk: re-scan rather than trust this list
            }
            else Status.Text = "That version has no downloadable file.";
        }
        catch (Exception ex) { Status.Text = "Install failed: " + ex.Message; }
    }

    private async void DeleteModsAsync(IReadOnlyList<PackMod> mods)
    {
        if (mods.Count == 0 || _owner is not MainWindow host) return;
        var message = mods.Count == 1
            ? $"Delete {System.IO.Path.GetFileName(mods[0].FilePath)}?"
            : $"Delete {mods.Count} mods?";
        if (!await AppDialog.ConfirmAsync(host, "Delete mods", message, "Delete", "Cancel", danger: true)) return;

        var failed = 0;
        foreach (var mod in mods)
        {
            try { System.IO.File.Delete(mod.FilePath); }
            catch { failed++; }
        }
        Status.Text = failed == 0
            ? $"Deleted {mods.Count} mod(s)."
            : $"Deleted {mods.Count - failed} of {mods.Count}; {failed} could not be removed (in use?).";
        _onReload?.Invoke();
    }

    private void OnCategoryRightClick(object sender, MouseButtonEventArgs e)
    {
        var row = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as CategoryRow;
        var menu = new ContextMenu { PlacementTarget = CategoryList };

        if (row is not null)
        {
            CategoryList.SelectedItem = row;
            _selectedCategory = row.Name;

            if (!row.Builtin)
            {
                menu.Items.Add(Item("Rename…", () =>
                    _ = ModCategoryMenu.RenameAsync(_packId, _owner, row.Name, () =>
                    {
                        _selectedCategory = null;   // the old name is gone; fall back to the first
                        Refresh();
                        _onChanged?.Invoke();
                    })));
            }
            menu.Items.Add(BuildColorMenu(row));

            var index = IndexOfCategory(row.Name);
            var last = App.State.ModMetadata.Categories(_packId).Count - 1;
            var move = new MenuItem { Header = "Move" };
            move.Items.Add(Item("To top", () => MoveCategory(row.Name, 0), index > 0));
            move.Items.Add(Item("Up", () => MoveCategory(row.Name, index - 1), index > 0));
            move.Items.Add(Item("Down", () => MoveCategory(row.Name, index + 2), index >= 0 && index < last));
            move.Items.Add(Item("To bottom", () => MoveCategory(row.Name, last + 1), index >= 0 && index < last));
            menu.Items.Add(move);

            menu.Items.Add(new Separator());
            menu.Items.Add(Item($"Select all {row.Count} mods", () =>
            {
                MemberList.SelectAll();
                MemberList.Focus();
            }, row.Count > 0));
            // Clearing works for Library too — it just unmarks every mod. Only rename/delete stay
            // off-limits, since the flag mirror depends on the name and the category always existing.
            menu.Items.Add(Item(row.Builtin ? $"Unmark all {row.Count}" : $"Clear all {row.Count} members",
                () => Assign(MembersOf(row.Name), row.Name, false), row.Count > 0));
            if (!row.Builtin)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Delete category", () =>
                    _ = ModCategoryMenu.DeleteAsync(_packId, _owner, _mods, row.Name, () =>
                    {
                        _selectedCategory = null;
                        Refresh();
                        _onChanged?.Invoke();
                    })));
            }
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(Item("New category…", NewCategory));
        menu.IsOpen = true;
        e.Handled = true;
    }

    private MenuItem BuildColorMenu(CategoryRow row)
    {
        var current = App.State.ModMetadata.CategoryColor(_packId, row.Name);
        var parent = new MenuItem { Header = "Colour" };

        foreach (var (name, hex) in AccentPalette.Colors)
        {
            var captured = hex;
            var item = new MenuItem
            {
                Header = name,
                InputGestureText = string.Equals(current, hex, StringComparison.OrdinalIgnoreCase) ? "✓" : "",
                Icon = new Border
                {
                    Width = 12, Height = 12, CornerRadius = new CornerRadius(3),
                    Background = AccentPalette.Brush(hex, (Brush)FindResource("BorderBrush"))
                }
            };
            item.Click += (_, _) => ApplyColor(row.Name, captured);
            parent.Items.Add(item);
        }

        parent.Items.Add(new Separator());
        parent.Items.Add(Item("Custom…", () => _ = PickColorAsync(row.Name, current)));
        parent.Items.Add(Item("No colour", () => ApplyColor(row.Name, null)));
        return parent;
    }

    /// <summary>Full HSV picker for anything the eight quick presets don't cover. Seeds its swatch
    /// row with the colours already used in this pack, so matching an existing one is a single click.</summary>
    private async Task PickColorAsync(string category, string? current)
    {
        if (_owner is null) return;

        var inUse = App.State.ModMetadata.Categories(_packId)
            .Select(c => c.Color)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!)
            .ToList();

        var picked = await ColorPickerDialog.ShowAsync(_owner, $"Colour for “{category}”", current, inUse);
        if (picked is { } choice) ApplyColor(category, choice.Hex);
    }

    private void ApplyColor(string category, string? hex)
    {
        App.State.ModMetadata.SetCategoryColor(_packId, category, hex);
        Refresh();
        _onChanged?.Invoke();
    }

    /// <summary>
    /// Copies another instance's categories into this one: the category list (names, colours, order)
    /// and which mods belong to them.
    /// </summary>
    /// <remarks>
    /// Organising four hundred mods is most of an evening, and people run the same mods across
    /// several instances — a dev copy, a friend's pack, last season's version of the same modpack.
    /// Mods are matched by their store id, so an identical mod at a different version still lines up.
    /// The import only adds: nothing already tagged here is removed or re-coloured, so it is safe to
    /// run twice.
    /// </remarks>
    private async void OnImportCategories(object sender, RoutedEventArgs e)
    {
        if (_owner is null) return;
        try
        {
            var packs = (await App.State.Api.ListPacksAsync())
                .Where(p => p.Id != _packId && !App.State.Settings.IsPackHidden(p.Id))
                .ToList();
            if (packs.Count == 0)
            {
                Status.Text = "There is no other instance to import from.";
                return;
            }

            var picker = new PackPickerDialog(packs, "Import categories from…",
                "Pick the instance to copy categories from. Mods are matched across instances by their store id, so the same mod at a different version still lines up.",
                "Preview") { Owner = _owner };
            if (picker.ShowDialog() != true || picker.SelectedPackId is not { } sourceId) return;

            var source = packs.First(p => p.Id == sourceId);
            var plan = App.State.ModMetadata.PlanCategoryImport(sourceId, _packId, _mods);
            if (plan.IsEmpty)
            {
                Status.Text = $"Nothing to import from {source.Name} — this pack already has its categories and tags.";
                return;
            }

            if (!await AppDialog.ConfirmAsync(_owner, $"Import from {source.Name}", DescribePlan(plan, source.Name),
                    "Import", "Cancel"))
                return;

            App.State.ModMetadata.ApplyCategoryImport(_packId, plan);
            Status.Text = $"Imported {plan.NewCategories.Count} categor{(plan.NewCategories.Count == 1 ? "y" : "ies")} " +
                          $"and tagged {plan.TaggedMods} mod(s) from {source.Name}.";
            Refresh();
            _onChanged?.Invoke();
        }
        catch (Exception ex) { Status.Text = "Import failed: " + ex.Message; }
    }

    private static string DescribePlan(ModMetadataService.CategoryImportPlan plan, string sourceName)
    {
        var lines = new List<string>();
        lines.Add(plan.NewCategories.Count > 0
            ? $"Add {plan.NewCategories.Count} categor{(plan.NewCategories.Count == 1 ? "y" : "ies")}: " +
              string.Join(", ", plan.NewCategories.Take(8).Select(c => c.Name)) +
              (plan.NewCategories.Count > 8 ? $" and {plan.NewCategories.Count - 8} more" : "")
            : "No new categories — this pack already has all of them.");

        if (plan.SharedCategories.Count > 0)
            lines.Add($"{plan.SharedCategories.Count} categor{(plan.SharedCategories.Count == 1 ? "y is" : "ies are")} " +
                      "already here and will be reused as they are.");

        lines.Add(plan.TaggedMods > 0
            ? $"Tag {plan.TaggedMods} mod(s) in this pack the way {sourceName} has them."
            : "No mods to tag — the ones this pack shares with it are already tagged.");

        if (plan.UnmatchedSourceMods > 0)
            lines.Add($"{plan.UnmatchedSourceMods} mod(s) {sourceName} categorises are not installed here and are skipped.");

        lines.Add("");
        lines.Add("Nothing is removed or re-coloured — this only adds.");
        return string.Join("\n", lines);
    }

    private void OnNewCategory(object sender, RoutedEventArgs e) => NewCategory();

    /// <summary>New categories select themselves, so you land straight in the pane where you'd start
    /// filling them.</summary>
    private void NewCategory() =>
        _ = ModCategoryMenu.NewAsync(_packId, _owner,
            onChanged: () => { Refresh(); _onChanged?.Invoke(); },
            onCreated: name => _selectedCategory = name);

    // ── list plumbing ───────────────────────────────────────────────────────────

    private void OnModSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();
    private void OnSearchChanged(object sender, TextChangedEventArgs e) { if (IsLoaded) RefreshLists(); }
    private void OnFilterChanged(object sender, RoutedEventArgs e) { if (IsLoaded) RefreshLists(); }

    private static MenuItem Item(string header, Action onClick, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, IsEnabled = enabled };
        mi.Click += (_, _) => onClick();
        return mi;
    }

    private static T? FindAncestor<T>(DependencyObject? from) where T : DependencyObject
    {
        for (var d = from; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is T hit) return hit;
        return null;
    }
}
