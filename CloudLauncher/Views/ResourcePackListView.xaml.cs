using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
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

/// <summary>The Resources tab inside an instance: every resource pack the instance has, which are
/// turned on, and in what order they override each other.</summary>
/// <remarks>A pack in <c>resourcepacks/</c> also has to be listed in options.txt's ordered
/// <c>resourcePacks</c> stack to load; this view manages both. The stack is shown highest priority
/// first, like the game's screen. The file stores it reversed, and
/// <see cref="ResourcePackService"/> handles the flip.</remarks>
public partial class ResourcePackListView : UserControl
{
    private PackDetail? _pack;
    private Window? _ownerWindow;
    private readonly ObservableCollection<ResourcePackRow> _rows = new();
    private readonly ObservableCollection<RpFolderChip> _folderChips = new();
    private readonly ListCollectionView _view;
    private int _scanGeneration;
    private string _searchText = "";
    private string? _activeFolder;
    private ResourcePackSortMode _sortMode;

    /// <summary>Shown once per visit: the game rewrites options.txt on exit, so changes made while it
    /// runs are lost. Warning on every toggle would be too much.</summary>
    private bool _warnedAboutRunningGame;

    public ResourcePackListView()
    {
        InitializeComponent();
        _sortMode = App.State.Settings.ResourcePackSortMode;
        SearchBox.TextChanged += OnSearchTextChanged;
        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = RowFilter;
        _view.CustomSort = new StackOrderComparer(() => _sortMode);
        ResourcePackGrid.ItemsSource = _view;
        FolderStrip.ItemsSource = _folderChips;
    }

    public void Load(PackDetail pack, Window owner)
    {
        _pack = pack;
        _ownerWindow = owner;
        _warnedAboutRunningGame = false;
        _ = ScanAsync();
    }

    // ── scanning ─────────────────────────────────────────────────────────────

    private async Task ScanAsync()
    {
        if (_pack is null) return;
        var generation = ++_scanGeneration;
        StatusLabel.Text = "Scanning resource packs...";
        BusyBar.Visibility = Visibility.Visible;

        var pack = _pack;
        try
        {
            // Directory walks, zip opens and an options.txt parse. An unpacked pack can be thousands of
            // files, so keep it off the UI thread.
            var scanned = await Task.Run(() =>
            {
                // Always include local/, even for unshared instances: library items applied to a
                // private instance land there, and would otherwise be invisible until the first launch.
                var found = App.State.ResourcePacks.ScanPack(pack.Id, pack.Name, includeLocal: true);
                var game = found.Where(r => !r.IsLocal).ToList();

                // After a launch the overlay has hard-linked local/ packs into game/, so one file shows
                // up twice. An instance's own same-named pack is a different file and keeps its row, so
                // the collision is visible.
                found = found.Where(r =>
                {
                    if (!r.IsLocal) return true;
                    var twin = game.FirstOrDefault(g =>
                        string.Equals(g.FileName, r.FileName, StringComparison.OrdinalIgnoreCase));
                    return twin is null || !PackFolderService.EntriesReferToSameContent(r.FilePath, twin.FilePath);
                }).ToList();

                var library = SafeLibraryItems();
                return found
                    .Select(info =>
                    {
                        var item = library.FirstOrDefault(i =>
                            string.Equals(i.FileName, info.FileName, StringComparison.OrdinalIgnoreCase));
                        var same = item is not null
                                   && PackFolderService.EntriesReferToSameContent(item.Path, info.FilePath);
                        return (info,
                                meta: ResourcePackService.ReadMeta(info.FilePath, info.IsFolder),
                                fromLibrary: same,
                                shadows: item is not null && !same);
                    })
                    .ToList();
            });

            if (generation != _scanGeneration) return;

            var previousSelection = ResourcePackGrid.SelectedItems
                .OfType<ResourcePackRow>()
                .Select(r => r.Key)
                .ToHashSet(StringComparer.Ordinal);

            _rows.Clear();
            foreach (var (info, meta, fromLibrary, shadows) in scanned)
                _rows.Add(new ResourcePackRow(info, meta, fromLibrary, shadows));

            RebuildFolderChips();
            _view.Refresh();
            RestoreSelection(previousSelection);
            UpdateStatus();
        }
        catch (Exception ex)
        {
            if (generation == _scanGeneration) StatusLabel.Text = "Scan failed: " + ex.Message;
        }
        finally
        {
            if (generation == _scanGeneration) BusyBar.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>The library's resource packs, or none when the folder can't be read, so a library
    /// problem never fails the instance scan.</summary>
    private static List<LibraryItem> SafeLibraryItems()
    {
        try { return App.State.Library.Scan(LibraryKind.ResourcePack); }
        catch (Exception ex) { AppLog.LogError(nameof(ResourcePackListView), ex); return []; }
    }

    /// <summary>Re-reads only the enabled stack and repaints the rows from it.</summary>
    /// <remarks>Used after a toggle or move instead of a full rescan, which keeps the selection and
    /// scroll position.</remarks>
    private void RefreshStackState()
    {
        if (_pack is null) return;
        var active = App.State.ResourcePacks.ActiveFor(_pack.Id, _pack.Name);
        foreach (var row in _rows)
            row.ApplyStackIndex(active.FindIndex(n => string.Equals(n, row.FileName, StringComparison.OrdinalIgnoreCase)));
        _view.Refresh();
        UpdateStatus();
    }

    private void RestoreSelection(IReadOnlySet<string> keys)
    {
        if (keys.Count == 0) return;
        ResourcePackGrid.SelectedItems.Clear();
        foreach (var row in _rows.Where(r => keys.Contains(r.Key)))
            ResourcePackGrid.SelectedItems.Add(row);
    }

    private void UpdateStatus()
    {
        var shown = _view.Count;
        var on = _rows.Count(r => r.Enabled);

        EmptyState.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (shown == 0)
        {
            var filtered = _rows.Count > 0;
            EmptyTitle.Text = filtered ? "Nothing matches" : "No resource packs yet";
            EmptyDetail.Text = filtered
                ? "Clear the search or pick a different folder chip."
                : "Drop a .zip here, use Import, or browse the catalog to install one.";
        }

        StatusLabel.Text = _rows.Count == 0
            ? "No resource packs in this instance."
            : $"{_rows.Count} pack{(_rows.Count == 1 ? "" : "s")} · {on} on"
              + (shown == _rows.Count ? "" : $" · {shown} shown");
    }

    private bool RowFilter(object item)
    {
        if (item is not ResourcePackRow row) return false;

        if (!string.IsNullOrEmpty(_activeFolder)
            && App.State.Settings.ResourcePackFolders.TryGetValue(_activeFolder, out var members)
            && !members.Contains(row.Key))
            return false;

        if (string.IsNullOrWhiteSpace(_searchText)) return true;
        var needle = _searchText.Trim().ToLowerInvariant();

        // "on" and "off" filter by enabled state.
        if (needle == "on") return row.Enabled;
        if (needle == "off") return !row.Enabled;

        return row.DisplayName.ToLowerInvariant().Contains(needle)
            || row.FileName.ToLowerInvariant().Contains(needle)
            || row.Folder.ToLowerInvariant().Contains(needle)
            || (row.PackDescription?.ToLowerInvariant().Contains(needle) ?? false);
    }

    private void RebuildFolderChips()
    {
        var previous = _activeFolder;
        _folderChips.Clear();

        var keys = _rows.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        _folderChips.Add(new RpFolderChip
        {
            Id = "",
            Label = "All",
            Icon = "",
            CountLabel = _rows.Count > 0 ? $"({_rows.Count})" : "",
            IsActive = string.IsNullOrEmpty(previous)
        });

        // Only folders holding something from this instance; the global folders span every instance.
        foreach (var (name, members) in App.State.Settings.ResourcePackFolders.OrderBy(k => k.Key))
        {
            var count = members.Count(keys.Contains);
            if (count == 0 && !string.Equals(name, previous, StringComparison.Ordinal)) continue;
            _folderChips.Add(new RpFolderChip
            {
                Id = name,
                Label = name,
                Icon = "",
                CountLabel = count > 0 ? $"({count})" : "",
                IsActive = string.Equals(name, previous, StringComparison.Ordinal)
            });
        }
    }

    private void OnFolderChipClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RpFolderChip chip }) return;
        _activeFolder = string.IsNullOrEmpty(chip.Id) ? null : chip.Id;
        RebuildFolderChips();
        _view.Refresh();
        UpdateStatus();
    }

    // ── keyboard + search ────────────────────────────────────────────────────

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                SearchBox.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.F5)
            {
                e.Handled = true;
                await ScanAsync();
            }
            else if (e.Key == Key.Delete && ResourcePackGrid.IsKeyboardFocusWithin && Selected().Count > 0)
            {
                e.Handled = true;
                await DeleteAsync(Selected());
            }
            else if (e.Key == Key.F2 && Selected().Count == 1)
            {
                e.Handled = true;
                RenameDisplayName(Selected()[0]);
            }
            else if (e.Key == Key.Enter && ResourcePackGrid.IsKeyboardFocusWithin && Selected().Count == 1)
            {
                e.Handled = true;
                OpenDetail(Selected()[0]);
            }
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    /// <summary>Filters on every keystroke. It's an in-memory pass over one instance's packs, so no
    /// debounce is needed.</summary>
    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = SearchBox.Text;
        _view.Refresh();
        UpdateStatus();
    }

    // ── sort ─────────────────────────────────────────────────────────────────

    private void OnSortButtonClick(object sender, RoutedEventArgs e)
    {
        SortButton.ContextMenu!.PlacementTarget = SortButton;
        SortButton.ContextMenu.Placement = PlacementMode.Bottom;
        SyncSortChecks();
        SortButton.ContextMenu.IsOpen = true;
    }

    private void OnSortMenuOpened(object sender, RoutedEventArgs e) => SyncSortChecks();

    private void OnSortMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag }) return;
        if (!Enum.TryParse<ResourcePackSortMode>(tag, out var parsed)) return;

        // Shared with the global Resource packs screen: one preference for both lists.
        _sortMode = parsed;
        App.State.Settings.ResourcePackSortMode = parsed;
        App.State.Settings.Save();
        SyncSortChecks();
        _view.Refresh();
    }

    private void SyncSortChecks()
    {
        SortModifiedMenuItem.IsChecked = _sortMode == ResourcePackSortMode.Modified;
        SortNameMenuItem.IsChecked = _sortMode == ResourcePackSortMode.Name;
        SortSizeMenuItem.IsChecked = _sortMode == ResourcePackSortMode.Size;
        SortPackMenuItem.IsChecked = _sortMode == ResourcePackSortMode.Pack;
    }

    // ── selection ────────────────────────────────────────────────────────────

    private List<ResourcePackRow> Selected() =>
        ResourcePackGrid.SelectedItems.OfType<ResourcePackRow>().ToList();

    private void OnGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var count = ResourcePackGrid.SelectedItems.Count;
        SelectionBar.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        SelectionLabel.Text = $"{count} selected";
        BulkDeleteButton.Content = $"Delete {count} files";
        BulkCopyButton.Content = $"Copy {count} to...";
    }

    private void OnSelectAll(object sender, RoutedEventArgs e) => ResourcePackGrid.SelectAll();

    private void OnClearSelection(object sender, RoutedEventArgs e) => ResourcePackGrid.UnselectAll();

    private void OnGridPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Right-clicking outside the current selection retargets it; right-clicking inside a
        // multi-row selection keeps it, so "Delete 5 files" means what it says.
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is not { } row) return;
        if (row.IsSelected) return;
        ResourcePackGrid.SelectedItems.Clear();
        ResourcePackGrid.SelectedItem = row.Item;
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected() is [var row]) OpenDetail(row);
    }

    private void OpenDetail(ResourcePackRow row)
    {
        if (_ownerWindow is MinecraftHostWindow host)
            host.OpenLocalResourcePackDetail(row.Key, row.DisplayName);
        else if (_ownerWindow is MainWindow main)
            main.OpenLocalResourcePackDetail(row.Key, row.DisplayName);
    }

    // ── enable / order ───────────────────────────────────────────────────────

    /// <summary>Warns once when the instance is running: Minecraft rewrites options.txt from memory on
    /// exit, so a change made now would be lost.</summary>
    private async Task<bool> ConfirmStackWriteAsync()
    {
        if (_pack is null) return false;
        if (_warnedAboutRunningGame || !App.State.Instances.IsBusy(_pack.Id)) return true;

        var go = await AppDialog.ConfirmAsync(_ownerWindow,
            "Minecraft is running",
            $"{_pack.Name} is open. Minecraft rewrites options.txt when it closes, so resource pack "
            + "changes made now will be lost. Change it anyway?",
            "Change anyway", "Cancel", danger: true);

        if (go) _warnedAboutRunningGame = true;
        return go;
    }

    private async void OnEnabledToggle(object sender, RoutedEventArgs e)
    {
        try
        {
            // The toggle is bound one-way, so its state comes from the rescan below rather than the
            // click, and never shows "on" for a pack the file says is off.
            if (sender is not FrameworkElement { DataContext: ResourcePackRow row }) return;
            if (sender is ToggleButton tb) tb.IsChecked = row.Enabled;
            await SetEnabledAsync(new[] { row }, !row.Enabled);
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private async void OnCtxToggle(object sender, RoutedEventArgs e)
    {
        try
        {
            var rows = Selected();
            if (rows.Count == 0) return;
            await SetEnabledAsync(rows, !rows.All(r => r.Enabled));
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private async void OnBulkEnable(object sender, RoutedEventArgs e)
    {
        try { await SetEnabledAsync(Selected(), true); }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private async void OnBulkDisable(object sender, RoutedEventArgs e)
    {
        try { await SetEnabledAsync(Selected(), false); }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private async Task SetEnabledAsync(IReadOnlyList<ResourcePackRow> rows, bool enabled)
    {
        if (_pack is null || rows.Count == 0) return;
        if (!await ConfirmStackWriteAsync()) return;

        var stack = App.State.ResourcePacks.ActiveFor(_pack.Id, _pack.Name);
        var names = rows.Select(r => r.FileName).ToList();
        stack.RemoveAll(n => names.Any(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase)));

        // Newly enabled packs go on top in selection order. InsertRange rather than inserting each at
        // index 0, which would reverse them (and this list is highest priority first).
        if (enabled) stack.InsertRange(0, names);

        // The instance's version decides how entries are spelled when options.txt has none to copy
        // from yet: "file/Name.zip" from 1.13 on, the bare name before that.
        App.State.ResourcePacks.SetActive(_pack.Id, _pack.Name, stack, _pack.MinecraftVersion);
        RefreshStackState();
        StatusLabel.Text = rows.Count == 1
            ? $"{rows[0].DisplayName} turned {(enabled ? "on" : "off")}."
            : $"{rows.Count} packs turned {(enabled ? "on" : "off")}.";
    }

    private async void OnMoveUp(object sender, RoutedEventArgs e) => await MoveAsync(sender, -1);

    private async void OnMoveDown(object sender, RoutedEventArgs e) => await MoveAsync(sender, +1);

    private async Task MoveAsync(object sender, int delta)
    {
        try
        {
            if (_pack is null) return;
            if (sender is not FrameworkElement { DataContext: ResourcePackRow row } || !row.Enabled) return;
            if (!await ConfirmStackWriteAsync()) return;

            App.State.ResourcePacks.MoveInStack(_pack.Id, _pack.Name, row.FileName, delta);
            RefreshStackState();
            ResourcePackGrid.SelectedItem = row;
            StatusLabel.Text = $"{row.DisplayName} is now #{row.Priority} in the stack.";
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private async void OnCtxMoveTop(object sender, RoutedEventArgs e) => await MoveToEndAsync(toTop: true);

    private async void OnCtxMoveBottom(object sender, RoutedEventArgs e) => await MoveToEndAsync(toTop: false);

    private async Task MoveToEndAsync(bool toTop)
    {
        try
        {
            if (_pack is null) return;
            var rows = Selected().Where(r => r.Enabled).ToList();
            if (rows.Count == 0) return;
            if (!await ConfirmStackWriteAsync()) return;

            var stack = App.State.ResourcePacks.ActiveFor(_pack.Id, _pack.Name);
            var names = rows.Select(r => r.FileName).ToList();
            stack.RemoveAll(n => names.Any(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase)));
            if (toTop) stack.InsertRange(0, names);
            else stack.AddRange(names);

            App.State.ResourcePacks.SetActive(_pack.Id, _pack.Name, stack, _pack.MinecraftVersion);
            RefreshStackState();
            StatusLabel.Text = toTop ? "Moved to the top of the stack." : "Moved to the bottom of the stack.";
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    // ── import / drag & drop ─────────────────────────────────────────────────

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        try { await ScanAsync(); }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private void OnBrowseResourcePacks(object sender, RoutedEventArgs e)
    {
        if (_pack is null) return;
        if (_ownerWindow is MinecraftHostWindow host)
            host.OpenResourcePackExplorerForPack(_pack);
        else if (_ownerWindow is MainWindow main)
            main.OpenResourcePackExplorerForPack(_pack);
    }

    private async void OnImportZip(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_pack is null) return;
            var dlg = new OpenFileDialog
            {
                Title = "Select resource pack zips",
                Filter = "Resource pack zip (*.zip)|*.zip|All files|*",
                Multiselect = true
            };
            if (dlg.ShowDialog(_ownerWindow) != true) return;
            await ImportAsync(dlg.FileNames);
        }
        catch (Exception ex) { StatusLabel.Text = "Import failed: " + ex.Message; }
    }

    private void OnGridDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedPacks(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnGridDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        try
        {
            var files = DroppedPacks(e);
            if (files.Count > 0) await ImportAsync(files);
        }
        catch (Exception ex) { StatusLabel.Text = "Import failed: " + ex.Message; }
    }

    /// <summary>The resource packs in a drag payload: zips, and folders that look like an unpacked
    /// pack. Dropping anything else is ignored rather than copied into resourcepacks/.</summary>
    private static List<string> DroppedPacks(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return new();
        return (e.Data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>())
            .Where(p => (File.Exists(p) && p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        || (Directory.Exists(p) && File.Exists(Path.Combine(p, "pack.mcmeta"))))
            .ToList();
    }

    private async Task ImportAsync(IReadOnlyList<string> sources)
    {
        if (_pack is null || sources.Count == 0) return;

        BusyBar.Visibility = Visibility.Visible;
        StatusLabel.Text = sources.Count == 1 ? "Importing..." : $"Importing {sources.Count} packs...";
        var destDir = Path.Combine(App.State.Packs.GameDir(_pack.Id), "resourcepacks");

        try
        {
            var copied = await Task.Run(() =>
            {
                Directory.CreateDirectory(destDir);
                var names = new List<string>();
                foreach (var source in sources)
                {
                    if (Directory.Exists(source))
                    {
                        var dest = UniquePath(destDir, Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar)), isFolder: true);
                        CopyDirectory(source, dest);
                        names.Add(Path.GetFileName(dest));
                    }
                    else
                    {
                        var dest = UniquePath(destDir, Path.GetFileName(source), isFolder: false);
                        File.Copy(source, dest);
                        names.Add(Path.GetFileName(dest));
                    }
                }
                return names;
            });

            await ScanAsync();
            StatusLabel.Text = copied.Count == 1
                ? $"Imported {copied[0]}. Turn it on with the switch on the right."
                : $"Imported {copied.Count} packs.";
        }
        finally { BusyBar.Visibility = Visibility.Collapsed; }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(source, dest, StringComparison.Ordinal));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, dest, StringComparison.Ordinal), overwrite: true);
    }

    // ── row actions ──────────────────────────────────────────────────────────

    /// <summary>Clicking a pack's name opens it, like clicking a mod's name.</summary>
    /// <remarks>Selects the row first, since the grid's commands act on <see cref="Selected"/> and
    /// would otherwise open the previously selected pack.</remarks>
    private void OnRowNameClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ResourcePackRow row }) return;
        e.Handled = true;
        Select(row);
        OpenDetail(row);
    }

    /// <summary>Left-click on the "...": the short menu.</summary>
    /// <remarks>Built on demand from <see cref="ContentMenu"/>'s helpers so it matches the mods menu and
    /// the global Resource packs page. The full menu is the grid's own <c>ContextMenu</c>.</remarks>
    private void OnRowOptions(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ResourcePackRow row } button) return;
        Select(row);

        var menu = ContentMenu.New(row.DisplayName,
            row.Enabled ? $"on · priority {row.Priority}" : "off - the game is not loading it");
        menu.Items.Add(ContentMenu.Item("Open pack", () => OpenDetail(row), ContentMenu.Glyph(0xE7C3)));
        menu.Items.Add(ContentMenu.Item(row.Enabled ? "Turn off" : "Turn on",
            () => _ = SetEnabledAsync([row], !row.Enabled), ContentMenu.Glyph(0xE768)));
        if (row.Enabled)
            menu.Items.Add(ContentMenu.Item("Move to top of stack",
                () => _ = MoveToEndAsync(toTop: true), ContentMenu.Glyph(0xE74A)));
        menu.Items.Add(ContentMenu.Item("Reveal in Explorer", () => Reveal(row), ContentMenu.Glyph(0xE8DA)));
        menu.Items.Add(ContentMenu.Item("Delete file", () => _ = DeleteAsync([row]), ContentMenu.Glyph(0xE74D)));
        menu.Items.Add(new Separator());
        menu.Items.Add(ContentMenu.Item("More options...",
            () => ContentMenu.ShowInstead(menu, () => ResourcePackGrid.ContextMenu),
            ContentMenu.Glyph(0xE712)));

        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>Right-click on the "...": the full menu the grid already carries.</summary>
    /// <remarks><c>e.Handled</c> stops the event reaching the grid, which would open a second
    /// menu.</remarks>
    private void OnRowOptionsRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ResourcePackRow row } button) return;
        e.Handled = true;
        Select(row);

        var menu = ResourcePackGrid.ContextMenu;
        if (menu is null) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>Makes one row the selection, unless it is already part of a multi-row one.</summary>
    /// <remarks>The same rule <see cref="OnGridPreviewMouseRightButtonDown"/> applies: acting inside an
    /// existing selection keeps it, so "Delete 5 files" means what it says.</remarks>
    private void Select(ResourcePackRow row)
    {
        if (ResourcePackGrid.SelectedItems.Contains(row)) return;
        ResourcePackGrid.SelectedItems.Clear();
        ResourcePackGrid.SelectedItem = row;
    }

    private async void OnCtxCopyTo(object sender, RoutedEventArgs e)
    {
        try { await CopyToInstanceAsync(Selected()); }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private async void OnBulkCopyTo(object sender, RoutedEventArgs e)
    {
        try { await CopyToInstanceAsync(Selected()); }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private async Task CopyToInstanceAsync(IReadOnlyList<ResourcePackRow> rows)
    {
        if (_pack is null || rows.Count == 0) return;

        var packs = await App.State.Api.ListPacksAsync();
        var targets = packs.Where(p => p.Id != _pack.Id).ToList();
        if (targets.Count == 0)
        {
            StatusLabel.Text = "There is no other instance to copy into.";
            return;
        }

        var picker = new PackPickerDialog(targets, "Copy resource packs",
            rows.Count == 1 ? rows[0].DisplayName : $"{rows.Count} packs", "Copy")
        { Owner = _ownerWindow };
        if (picker.ShowDialog() != true || picker.SelectedPackId is not Guid targetId) return;

        var target = targets.First(p => p.Id == targetId);
        BusyBar.Visibility = Visibility.Visible;
        StatusLabel.Text = $"Copying to {target.Name}...";
        try
        {
            var sources = rows.Select(r => (r.FilePath, r.FileName, r.IsFolder)).ToList();
            var count = await Task.Run(() =>
            {
                App.State.Packs.EnsurePackFolder(target.Id, target.Name, target.IsShared);
                var dir = Path.Combine(App.State.Packs.GameDir(target.Id, target.Name), "resourcepacks");
                Directory.CreateDirectory(dir);

                var done = 0;
                foreach (var (path, name, isFolder) in sources)
                {
                    var dest = UniquePath(dir, name, isFolder);
                    if (isFolder) CopyDirectory(path, dest);
                    else File.Copy(path, dest);
                    done++;
                }
                return done;
            });

            StatusLabel.Text = $"Copied {count} pack{(count == 1 ? "" : "s")} to {target.Name}.";
        }
        finally { BusyBar.Visibility = Visibility.Collapsed; }
    }

    private void OnCtxReveal(object sender, RoutedEventArgs e)
    {
        foreach (var row in Selected().Take(5)) Reveal(row);
    }

    private void Reveal(ResourcePackRow row)
    {
        if (!SafeLaunch.RevealFile(row.FilePath)) StatusLabel.Text = "Explorer could not be opened.";
    }

    private void OnCtxCopyPath(object sender, RoutedEventArgs e)
    {
        if (Selected() is not [var row]) return;
        if (ClipboardHelper.TrySetText(row.FilePath)) StatusLabel.Text = "Path copied.";
    }

    private async void OnCtxDelete(object sender, RoutedEventArgs e)
    {
        try { await DeleteAsync(Selected()); }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private async void OnBulkDelete(object sender, RoutedEventArgs e)
    {
        try { await DeleteAsync(Selected()); }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private async Task DeleteAsync(IReadOnlyList<ResourcePackRow> rows)
    {
        if (_pack is null || rows.Count == 0) return;

        var message = rows.Count == 1
            ? $"Delete {rows[0].FileName} from {_pack.Name}? This removes the file from disk."
            : $"Delete these {rows.Count} resource packs from {_pack.Name}?\n\n"
              + string.Join("\n", rows.Take(8).Select(r => "• " + r.FileName))
              + (rows.Count > 8 ? $"\n...and {rows.Count - 8} more" : "");

        if (!await AppDialog.ConfirmAsync(_ownerWindow, "Delete resource packs", message,
                rows.Count == 1 ? "Delete" : $"Delete {rows.Count} files", "Cancel", danger: true))
            return;

        BusyBar.Visibility = Visibility.Visible;
        StatusLabel.Text = "Deleting...";
        var failures = new List<string>();
        try
        {
            var infos = rows.Select(r => r.Info).ToList();
            await Task.Run(() =>
            {
                foreach (var info in infos)
                {
                    try { App.State.ResourcePacks.DeleteFromDisk(info); }
                    catch (Exception ex) { lock (failures) failures.Add($"{info.FileName}: {ex.Message}"); }
                }
            });
            App.State.Settings.Save();

            await ScanAsync();
            StatusLabel.Text = failures.Count == 0
                ? $"Deleted {rows.Count - failures.Count} pack{(rows.Count - failures.Count == 1 ? "" : "s")}."
                : $"Deleted {rows.Count - failures.Count}, failed {failures.Count}: {failures[0]}";
        }
        finally { BusyBar.Visibility = Visibility.Collapsed; }
    }

    // ── rename ───────────────────────────────────────────────────────────────

    private void OnCtxRename(object sender, RoutedEventArgs e)
    {
        if (Selected() is [var row]) RenameDisplayName(row);
    }

    /// <summary>Renames the label the launcher shows. The file keeps its name; "Rename file on
    /// disk..." does that.</summary>
    private void RenameDisplayName(ResourcePackRow row)
    {
        var dlg = new SimpleInputDialog("Rename resource pack", "Display name", row.DisplayName)
        { Owner = _ownerWindow };
        if (dlg.ShowDialog() != true) return;

        var name = (dlg.Result ?? "").Trim();
        if (name.Length == 0) { StatusLabel.Text = "Name cannot be empty."; return; }

        App.State.ResourcePacks.Rename(row.Key, name);
        row.ApplyDisplayName(name);
        _view.Refresh();
        StatusLabel.Text = $"Renamed to {name}.";
    }

    private async void OnCtxRenameFile(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_pack is null || Selected() is not [var row]) return;

            var dlg = new SimpleInputDialog("Rename file on disk", "File name", row.FileName)
            { Owner = _ownerWindow };
            if (dlg.ShowDialog() != true) return;

            var wanted = ResourcePackService.SanitizeFileName(dlg.Result ?? "", row.IsFolder);
            if (string.Equals(wanted, row.FileName, StringComparison.Ordinal)) return;
            if (!PathSafety.IsSafeFileName(wanted))
            {
                StatusLabel.Text = $"Rename failed: '{wanted}' cannot be used as a file name.";
                return;
            }

            // Renaming rewrites the options.txt entry too, so a pack that was on stays on.
            if (row.Enabled && !await ConfirmStackWriteAsync()) return;

            App.State.ResourcePacks.RenameFileOnDisk(row.Info, wanted);
            await ScanAsync();
            StatusLabel.Text = $"Renamed to {wanted}.";
        }
        catch (Exception ex) { StatusLabel.Text = "Rename failed: " + ex.Message; }
    }

    // ── folders ──────────────────────────────────────────────────────────────

    private void OnCtxAddToFolderOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem root) return;
        var rows = Selected();
        root.Items.Clear();
        if (rows.Count == 0) return;

        foreach (var folder in App.State.Settings.ResourcePackFolders.Keys.OrderBy(s => s))
        {
            var members = App.State.Settings.ResourcePackFolders[folder];
            var allInside = rows.All(r => members.Contains(r.Key));
            var item = new MenuItem { Header = folder, IsCheckable = true, IsChecked = allInside };
            var captured = folder;
            item.Click += (_, _) =>
            {
                foreach (var row in rows)
                {
                    if (item.IsChecked) App.State.Settings.AddResourcePackToFolder(captured, row.Key);
                    else App.State.Settings.RemoveResourcePackFromFolder(captured, row.Key);
                }
                App.State.Settings.Save();
                RebuildFolderChips();
                _view.Refresh();
                UpdateStatus();
            };
            root.Items.Add(item);
        }

        if (root.Items.Count > 0) root.Items.Add(new Separator());
        var create = new MenuItem { Header = "Create folder..." };
        create.Click += (_, _) =>
        {
            var dlg = new SimpleInputDialog("New folder", "Name", "") { Owner = _ownerWindow };
            if (dlg.ShowDialog() != true) return;
            var name = (dlg.Result ?? "").Trim();
            if (name.Length == 0) return;
            App.State.Settings.CreateResourcePackFolder(name);
            foreach (var row in rows) App.State.Settings.AddResourcePackToFolder(name, row.Key);
            App.State.Settings.Save();
            _activeFolder = name;
            RebuildFolderChips();
            _view.Refresh();
            UpdateStatus();
        };
        root.Items.Add(create);
    }

    // ── context menu ─────────────────────────────────────────────────────────

    private void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        var rows = Selected();
        if (rows.Count == 0)
        {
            if (sender is ContextMenu cm) cm.IsOpen = false;
            return;
        }

        var single = rows.Count == 1;
        var allOn = rows.All(r => r.Enabled);
        var anyOn = rows.Any(r => r.Enabled);

        CtxToggle.Header = allOn
            ? single ? "Turn off" : $"Turn off {rows.Count} packs"
            : single ? "Turn on" : $"Turn on {rows.Count} packs";
        CtxMoveTop.IsEnabled = anyOn;
        CtxMoveBottom.IsEnabled = anyOn;

        CtxEdit.Visibility = single ? Visibility.Visible : Visibility.Collapsed;
        CtxRename.Visibility = single ? Visibility.Visible : Visibility.Collapsed;
        CtxRenameFile.Visibility = single ? Visibility.Visible : Visibility.Collapsed;

        // Hosting uploads a single zip; an unpacked folder has nothing to upload.
        CtxCreateHosted.Visibility = single && !rows[0].IsFolder && File.Exists(rows[0].FilePath)
            ? Visibility.Visible
            : Visibility.Collapsed;

        CtxCopyTo.Header = single ? "Copy to instance..." : $"Copy {rows.Count} to instance...";
        CtxDelete.Header = single ? "Delete file" : $"Delete {rows.Count} files";
    }

    private void OnCtxEdit(object sender, RoutedEventArgs e)
    {
        if (Selected() is [var row]) OpenDetail(row);
    }

    private async void OnCtxCreateHosted(object sender, RoutedEventArgs e)
    {
        try { await CreateHostedAsync(); }
        catch (Exception ex) { StatusLabel.Text = "Create failed: " + ex.Message; }
    }

    private async Task CreateHostedAsync()
    {
        if (_pack is null || Selected() is not [var row]) return;
        if (!File.Exists(row.FilePath))
        {
            StatusLabel.Text = "Zip file no longer exists.";
            return;
        }

        var nameDialog = new SimpleInputDialog("Create hosted resource pack", "Name", row.DisplayName)
        { Owner = _ownerWindow };
        if (nameDialog.ShowDialog() != true) return;

        var name = (nameDialog.Result ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusLabel.Text = "Name cannot be empty.";
            return;
        }

        var versionDialog = new SimpleInputDialog("Upload first version", "Version string", "1.0.0")
        { Owner = _ownerWindow };
        if (versionDialog.ShowDialog() != true) return;

        var version = (versionDialog.Result ?? "").Trim();
        if (string.IsNullOrWhiteSpace(version))
        {
            StatusLabel.Text = "Version string cannot be empty.";
            return;
        }

        StatusLabel.Text = $"Creating hosted resource pack for {row.FileName}...";
        BusyBar.Visibility = Visibility.Visible;
        try
        {
            var created = await App.State.Api.CreateResourcePackAsync(new CreateResourcePackRequest(
                name,
                $"Created from {row.FileName}",
                null,
                PackVisibility.Private,
                _pack.MinecraftVersion));

            await App.State.Api.UploadResourcePackVersionAsync(created.Id, row.FilePath,
                new CreateResourcePackVersionRequest(
                    version,
                    null,
                    "release",
                    row.FileName,
                    _pack.MinecraftVersion));

            App.State.ResourcePacks.LinkHostedResourcePack(row.Key, created.Id);
            StatusLabel.Text = $"Created hosted resource pack '{created.Name}'.";
            await ScanAsync();
            if (_ownerWindow is MinecraftHostWindow host)
                host.OpenResourcePackDetail(created.Id, created.Name);
            else if (_ownerWindow is MainWindow main)
                main.OpenResourcePackDetail(created.Id, created.Name);
        }
        catch (ApiException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound)
        {
            StatusLabel.Text = "Hosted resource pack support is not deployed on the server yet.";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Create failed: " + ex.Message;
        }
        finally { BusyBar.Visibility = Visibility.Collapsed; }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>A destination path that does not collide, for a zip or an unpacked pack folder.</summary>
    private static string UniquePath(string folder, string fileName, bool isFolder)
    {
        var clean = ResourcePackService.SanitizeFileName(fileName, isFolder);
        var candidate = Path.Combine(folder, clean);
        var stem = isFolder ? clean : Path.GetFileNameWithoutExtension(clean);
        var suffix = isFolder ? "" : ".zip";

        var n = 2;
        while (File.Exists(candidate) || Directory.Exists(candidate))
            candidate = Path.Combine(folder, $"{stem}-{n++}{suffix}");
        return candidate;
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

    /// <summary>Orders the grid: the enabled stack first, in priority order, then everything else by
    /// the user's chosen sort.</summary>
    /// <remarks>Column sorting is off so the stack order, which is data, always shows as it
    /// is.</remarks>
    private sealed class StackOrderComparer(Func<ResourcePackSortMode> mode) : System.Collections.IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not ResourcePackRow a || y is not ResourcePackRow b) return 0;
            if (a.Enabled != b.Enabled) return a.Enabled ? -1 : 1;
            if (a.Enabled && b.Enabled) return a.StackIndex.CompareTo(b.StackIndex);

            return mode() switch
            {
                ResourcePackSortMode.Name => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase),
                ResourcePackSortMode.Size => b.SizeBytes.CompareTo(a.SizeBytes),
                ResourcePackSortMode.Pack => string.Compare(a.Folder, b.Folder, StringComparison.Ordinal) is var f and not 0
                    ? f
                    : string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase),
                _ => b.Modified.CompareTo(a.Modified)
            };
        }
    }
}

/// <summary>A folder chip above the grid. Holds no brushes: the template triggers on the selected
/// state, so a theme change repaints it.</summary>
public sealed class RpFolderChip
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string Icon { get; init; } = "";
    public string CountLabel { get; init; } = "";
    public bool IsActive { get; init; }
}

/// <summary>One installed resource pack, as the grid shows it.</summary>
/// <remarks>Enabled state and stack position change without a rescan (which would lose the
/// selection and scroll position), so they are the only mutable, notifying properties.</remarks>
public sealed class ResourcePackRow : INotifyPropertyChanged
{
    public ResourcePackRow(ResourcePackInfo info, ResourcePackMeta meta,
                           bool fromLibrary = false, bool shadowsLibrary = false)
    {
        Info = info;
        DisplayName = info.DisplayName;
        StackIndex = info.StackIndex;
        IconSource = meta.Icon;
        PackDescription = meta.Description;
        PackFormat = meta.PackFormat;
        IsFromLibrary = fromLibrary;
        ShadowsLibrary = shadowsLibrary;
    }

    /// <summary>This file is the one copy kept in the launcher's library, hard-linked in here.</summary>
    public bool IsFromLibrary { get; }

    /// <summary>A same-named library item exists but this is a different file. The launch overlay never
    /// overwrites, so this copy is the one the game loads and the library version is inert here.</summary>
    public bool ShadowsLibrary { get; }

    public Visibility LibraryPillVisibility => IsFromLibrary ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ShadowPillVisibility => ShadowsLibrary ? Visibility.Visible : Visibility.Collapsed;

    public ResourcePackInfo Info { get; private set; }

    public string Key => Info.Key;
    public string FilePath => Info.FilePath;
    public string FileName => Info.FileName;
    public bool IsFolder => Info.IsFolder;
    public long SizeBytes => Info.SizeBytes;
    public DateTimeOffset Modified => Info.LastModified;

    /// <summary>"game" or "local": which of the instance's two resourcepacks/ folders this is
    /// in.</summary>
    public string Folder => Info.IsLocal ? "local" : "game";

    public string DisplayName { get; private set; }

    public ImageSource? IconSource { get; }
    public string? PackDescription { get; }
    public int? PackFormat { get; }
    public bool HasDescription => !string.IsNullOrWhiteSpace(PackDescription);

    public int StackIndex { get; private set; }
    public bool Enabled => StackIndex >= 0;
    public int Priority => StackIndex >= 0 ? StackIndex + 1 : 0;

    /// <summary>Rank in the stack, or a dash when the pack is off.</summary>
    public string PriorityLabel => Enabled ? Priority.ToString() : "-";

    public string Initial => string.IsNullOrWhiteSpace(DisplayName) ? "?"
        : DisplayName.Trim()[..1].ToUpperInvariant();

    public string SizeLabel => SizeBytes switch
    {
        >= 1024L * 1024 => $"{SizeBytes / (1024.0 * 1024):F1} MB",
        >= 1024 => $"{SizeBytes / 1024.0:F0} KB",
        _ => $"{SizeBytes} B"
    };

    public string RowTooltip
    {
        get
        {
            var lines = new List<string>
            {
                DisplayName,
                Enabled ? $"On · priority {Priority} (higher rows override lower ones)" : "Off - not loaded by the game",
                $"{FileName} · {SizeLabel} · modified {Modified.LocalDateTime:g}"
            };
            if (IsFromLibrary) lines.Add("The shared copy - one file, used by every instance you put it in");
            if (ShadowsLibrary) lines.Add("This instance has its own copy of this file; the shared one is not in use here");
            if (PackFormat is int pf) lines.Add($"pack_format {pf}");
            if (HasDescription) lines.Add(PackDescription!);
            return string.Join("\n", lines);
        }
    }

    public void ApplyStackIndex(int stackIndex)
    {
        if (StackIndex == stackIndex) return;
        StackIndex = stackIndex;
        Raise(nameof(StackIndex));
        Raise(nameof(Enabled));
        Raise(nameof(Priority));
        Raise(nameof(PriorityLabel));
        Raise(nameof(RowTooltip));
    }

    public void ApplyDisplayName(string name)
    {
        DisplayName = name;
        Raise(nameof(DisplayName));
        Raise(nameof(Initial));
        Raise(nameof(RowTooltip));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
