using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

public partial class WorldsView : Page
{
    private readonly MainWindow _shell;
    private readonly ObservableCollection<WorldRow> _rows = new();
    private readonly ObservableCollection<FolderChipRow> _folderChips = new();
    private List<PackSummary> _allPacks = new();
    private UniformGrid? _worldGrid;
    private WorldSortMode _sortMode;
    private string? _activeFolder;

    /// <summary>The card the keyboard is aimed at, so Delete/F2/Enter have a target.</summary>
    private WorldRow? _focusedRow;

    /// <summary>
    /// Cancels whichever long-running disk operation is in flight, if any.
    /// </summary>
    /// <remarks>Copies, zips and restores of a multi-gigabyte save are minute-scale, so every one of
    /// them runs under this and the status bar grows a Cancel button for its duration.</remarks>
    private CancellationTokenSource? _busyCts;

    private const double CardMinWidth = 220;
    private const double SearchCollapsedWidth = 36;
    private const double SearchExpandedWidth = 260;

    public WorldsView(MainWindow shell)
    {
        InitializeComponent();
        _shell = shell;
        _sortMode = App.State.Settings.WorldSortMode;
        WorldList.ItemsSource = _rows;
        FolderStrip.ItemsSource = _folderChips;
        UpdateSortMenuState();
        Loaded += async (_, _) =>
        {
            await RefreshAsync();
            Window.GetWindow(this)!.PreviewKeyDown += OnShellKeyDown;
        };
        Unloaded += (_, _) =>
        {
            if (Window.GetWindow(this) is Window w) w.PreviewKeyDown -= OnShellKeyDown;
            _busyCts?.Cancel();
        };
    }

    // ── status line ──────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the status line in the colour that matches what happened.
    /// </summary>
    /// <remarks>
    /// The label used to be painted <c>DangerBrush</c> in XAML while the code-behind wrote successes
    /// to it, so "Imported X to Y" read as a failure. The brush is set by resource reference rather
    /// than assignment so that changing the accent in Settings repaints it live.
    /// </remarks>
    private void SetStatus(string text, StatusKind kind = StatusKind.Neutral)
    {
        StatusLabel.Text = text;
        StatusLabel.SetResourceReference(TextBlock.ForegroundProperty, kind switch
        {
            StatusKind.Error => "DangerBrush",
            StatusKind.Success => "AccentBrush",
            _ => "TextSecondaryBrush"
        });
    }

    private enum StatusKind { Neutral, Success, Error }

    // ── busy state ───────────────────────────────────────────────────────────

    /// <summary>Puts the page into its working state and hands back the token the work runs under.</summary>
    private CancellationToken BeginBusy(string label)
    {
        _busyCts?.Cancel();
        _busyCts = new CancellationTokenSource();
        SetStatus(label);
        BusyBar.Value = 0;
        BusyBar.IsIndeterminate = true;
        BusyBar.Visibility = Visibility.Visible;
        CancelBusyButton.Visibility = Visibility.Visible;
        RefreshButton.IsEnabled = false;
        return _busyCts.Token;
    }

    private void EndBusy()
    {
        BusyBar.Visibility = Visibility.Collapsed;
        BusyBar.IsIndeterminate = false;
        CancelBusyButton.Visibility = Visibility.Collapsed;
        RefreshButton.IsEnabled = true;
        _busyCts?.Dispose();
        _busyCts = null;
    }

    private void OnCancelBusy(object sender, RoutedEventArgs e)
    {
        _busyCts?.Cancel();
        SetStatus("Cancelling…");
    }

    /// <summary>A progress sink that drives the status-bar bar from a background thread.</summary>
    private IProgress<double> BarProgress() => new Progress<double>(f =>
    {
        BusyBar.IsIndeterminate = false;
        BusyBar.Value = f;
    });

    // ── refresh ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds the world list.
    /// </summary>
    /// <remarks>
    /// The scan walks every file of every save to size it and gzip-decompresses every level.dat, so
    /// it runs on a worker thread — on the dispatcher it froze the whole window for seconds on a
    /// modded save, which is exactly what PackDetailView.RefreshWorlds already avoids.
    /// </remarks>
    private async Task RefreshAsync()
    {
        RefreshButton.IsEnabled = false;
        SetStatus("Scanning saves…");
        try
        {
            _allPacks = await App.State.Api.ListPacksAsync();
            var packs = _allPacks;
            var worlds = await Task.Run(() => App.State.Worlds.ScanAll(packs));

            _rows.Clear();
            foreach (var w in worlds) _rows.Add(new WorldRow(w, _allPacks, worlds));
            CountLabel.Text = $"{_rows.Count} world{(_rows.Count == 1 ? "" : "s")}";
            RefreshFolderChips();
            ApplyFilter();

            // A cached instance list is still a list — say where it came from rather than silently
            // showing worlds for instances that may no longer exist.
            if (App.State.Api.PackListStale is { Length: > 0 } why)
                SetStatus($"Showing your last known instances — the server is not answering ({why}).");
            else
                SetStatus("");
        }
        catch (Exception ex) { SetStatus(ex.Message, StatusKind.Error); }
        finally { RefreshButton.IsEnabled = true; }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        try { await RefreshAsync(); }
        catch (Exception ex) { SetStatus("Refresh failed: " + ex.Message, StatusKind.Error); }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private async void OnShellKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsVisible) return;
        try
        {
            if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                ExpandSearch();
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.F5)
            {
                e.Handled = true;
                await RefreshAsync();
            }
        }
        catch (Exception ex) { SetStatus(ex.Message, StatusKind.Error); }
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

    private void ExpandSearch()
    {
        CompactSearchHost.Width = SearchExpandedWidth;
        SearchBox.Visibility = Visibility.Visible;
    }

    private void CollapseSearch()
    {
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

    private void OnWorldGridLoaded(object sender, RoutedEventArgs e)
    {
        _worldGrid = (UniformGrid)sender;
        UpdateColumns();
    }

    private void OnWorldListContainerSizeChanged(object sender, SizeChangedEventArgs e) => UpdateColumns();

    private void UpdateColumns()
    {
        if (_worldGrid is null) return;
        var width = _worldGrid.ActualWidth;
        if (width <= 0) width = ActualWidth - 80;
        if (width <= 0) return;
        var cols = Math.Max(1, (int)Math.Floor(width / CardMinWidth));
        if (_worldGrid.Columns != cols) _worldGrid.Columns = cols;
    }

    private void ApplyFilter()
    {
        var filter = SearchBox.Text.Trim().ToLowerInvariant();

        IEnumerable<WorldRow> source = _rows;

        if (!string.IsNullOrEmpty(_activeFolder))
        {
            if (App.State.Settings.WorldFolders.TryGetValue(_activeFolder, out var keys))
                source = source.Where(r => keys.Contains(r.Source.Key));
            else
                source = Array.Empty<WorldRow>();
        }

        if (!string.IsNullOrEmpty(filter))
            source = source.Where(r => r.DisplayName.ToLowerInvariant().Contains(filter)
                                    || r.SourcePackName.ToLowerInvariant().Contains(filter)
                                    || r.Source.FolderName.ToLowerInvariant().Contains(filter));

        var filtered = SortRows(source).ToList();
        WorldList.ItemsSource = filtered;
        CountLabel.Text = $"{filtered.Count} world{(filtered.Count == 1 ? "" : "s")}";

        if (_rows.Count == 0)
        {
            EmptyState.Visibility = Visibility.Visible;
            EmptyFolderState.Visibility = Visibility.Collapsed;
        }
        else if (filtered.Count == 0 && !string.IsNullOrEmpty(_activeFolder))
        {
            EmptyState.Visibility = Visibility.Collapsed;
            EmptyFolderState.Visibility = Visibility.Visible;
            EmptyFolderTitle.Text = $"Nothing in {_activeFolder} yet";
        }
        else
        {
            EmptyState.Visibility = Visibility.Collapsed;
            EmptyFolderState.Visibility = Visibility.Collapsed;
        }
    }

    private void RefreshFolderChips()
    {
        _folderChips.Clear();
        var rs = Application.Current.Resources;
        var accent = (Brush)rs["AccentBrush"];
        var surface = (Brush)rs["Surface3Brush"];
        var border = (Brush)rs["BorderBrush"];
        var textPrimary = (Brush)rs["TextPrimaryBrush"];
        var textSecond = (Brush)rs["TextSecondaryBrush"];

        FolderChipRow chip(string id, string label, int count, string icon)
        {
            var active = _activeFolder == id;
            return new FolderChipRow
            {
                Id = id,
                Label = label,
                Icon = icon,
                CountLabel = count > 0 ? $"({count})" : "",
                Background = surface,
                BorderColor = active ? accent : border,
                Foreground = textPrimary,
                CountForeground = textSecond,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal
            };
        }

        _folderChips.Add(chip(null!, "All", _rows.Count, ""));
        foreach (var folder in App.State.Settings.WorldFolders.Keys.OrderBy(s => s))
        {
            var count = WorldsInFolder(folder).Count;
            _folderChips.Add(chip(folder, folder, count, ""));
        }
    }

    private List<WorldRow> WorldsInFolder(string folder)
    {
        if (string.IsNullOrEmpty(folder)) return _rows.ToList();
        return App.State.Settings.WorldFolders.TryGetValue(folder, out var keys)
            ? _rows.Where(r => keys.Contains(r.Source.Key)).ToList()
            : new List<WorldRow>();
    }

    private IEnumerable<WorldRow> SortRows(IEnumerable<WorldRow> rows) =>
        _sortMode switch
        {
            WorldSortMode.Name => rows
                .OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenByDescending(r => r.Source.LastModified),
            WorldSortMode.Size => rows
                .OrderByDescending(r => r.Source.SizeBytes)
                .ThenBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            WorldSortMode.Pack => rows
                .OrderBy(r => r.SourcePackName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            _ => rows
                .OrderByDescending(r => r.Source.LastModified)
                .ThenBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        };

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
        if (!Enum.TryParse<WorldSortMode>(tag, out var mode)) return;
        _sortMode = mode;
        App.State.Settings.WorldSortMode = mode;
        App.State.Settings.Save();
        UpdateSortMenuState();
        ApplyFilter();
    }

    private void UpdateSortMenuState()
    {
        SortModifiedMenuItem.IsChecked = _sortMode == WorldSortMode.Modified;
        SortNameMenuItem.IsChecked = _sortMode == WorldSortMode.Name;
        SortSizeMenuItem.IsChecked = _sortMode == WorldSortMode.Size;
        SortPackMenuItem.IsChecked = _sortMode == WorldSortMode.Pack;
        SortButton.ToolTip = $"Sort worlds: {SortModeLabel(_sortMode)}";
    }

    private static string SortModeLabel(WorldSortMode mode) => mode switch
    {
        WorldSortMode.Name => "Name",
        WorldSortMode.Size => "Size",
        WorldSortMode.Pack => "Source instance",
        _ => "Last played"
    };

    private void OnCreateFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new SimpleInputDialog("Create folder", "Folder name", "") { Owner = _shell };
        if (dlg.ShowDialog() != true) return;
        var name = (dlg.Result ?? "").Trim();
        if (string.IsNullOrEmpty(name)) return;
        App.State.Settings.CreateWorldFolder(name);
        App.State.Settings.Save();
        _activeFolder = name;
        RefreshFolderChips();
        ApplyFilter();
    }

    private void OnFolderChipClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is FolderChipRow chip)
        {
            _activeFolder = chip.Id;
            RefreshFolderChips();
            ApplyFilter();
        }
    }

    private void OnFolderChipRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is FolderChipRow chip
            && string.IsNullOrEmpty(chip.Id))
        {
            e.Handled = true;
        }
    }

    private void OnFolderRename(object sender, RoutedEventArgs e)
    {
        if (FolderFromMenu(sender) is not { } chip || string.IsNullOrEmpty(chip.Id)) return;
        var dlg = new SimpleInputDialog("Rename folder", "New name", chip.Id) { Owner = _shell };
        if (dlg.ShowDialog() != true) return;
        var newName = (dlg.Result ?? "").Trim();
        if (string.IsNullOrEmpty(newName) || newName == chip.Id) return;
        if (App.State.Settings.WorldFolders.ContainsKey(newName))
        {
            SetStatus("A folder with that name already exists.", StatusKind.Error);
            return;
        }

        var members = App.State.Settings.WorldFolders[chip.Id];
        App.State.Settings.WorldFolders.Remove(chip.Id);
        App.State.Settings.WorldFolders[newName] = members;
        if (_activeFolder == chip.Id) _activeFolder = newName;
        App.State.Settings.Save();
        RefreshFolderChips();
        ApplyFilter();
    }

    private async void OnFolderDelete(object sender, RoutedEventArgs e)
    {
        try
        {
            if (FolderFromMenu(sender) is not { } chip || string.IsNullOrEmpty(chip.Id)) return;
            var ok = await AppDialog.ConfirmAsync(_shell, "Delete folder",
                $"Delete the folder '{chip.Id}'?\n\nThe worlds filed into it stay exactly where they are — only the grouping goes.",
                "Delete folder", "Cancel", danger: true);
            if (!ok) return;

            App.State.Settings.DeleteWorldFolder(chip.Id);
            App.State.Settings.Save();
            if (_activeFolder == chip.Id) _activeFolder = null;
            RefreshFolderChips();
            ApplyFilter();
            SetStatus($"Folder '{chip.Id}' deleted.", StatusKind.Success);
        }
        catch (Exception ex) { SetStatus("Could not delete the folder: " + ex.Message, StatusKind.Error); }
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

    private void OnDownloadWorld(object sender, RoutedEventArgs e) => _shell.OpenWorldBrowser();

    // ── import ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the import menu — a save folder or a .zip.
    /// </summary>
    /// <remarks>
    /// Every world distributed on the internet arrives as a .zip, and this button used to open a
    /// folder picker only, so a downloaded world could not be imported at all without unzipping it in
    /// Explorer first.
    /// </remarks>
    private void OnImportButtonClick(object sender, RoutedEventArgs e)
    {
        // The empty-state button has no menu of its own, so it borrows the toolbar button's and
        // drops it under itself — both entry points then offer the same two choices.
        var anchor = sender as Button ?? ImportButton;
        var menu = anchor.ContextMenu ?? ImportButton.ContextMenu;
        if (menu is null) return;
        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private async void OnImportWorldFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select Minecraft world folder",
                Multiselect = false
            };
            if (dialog.ShowDialog(_shell) != true) return;

            if (!WorldService.IsWorldFolder(dialog.FolderName))
            {
                SetStatus("That folder is not a Minecraft world — it has no level.dat inside.", StatusKind.Error);
                return;
            }
            await ImportAsync(dialog.FolderName, isZip: false);
        }
        catch (Exception ex) { SetStatus("Import failed: " + ex.Message, StatusKind.Error); }
    }

    private async void OnImportWorldZip(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select a Minecraft world .zip",
                Filter = "Minecraft world (*.zip)|*.zip|All files (*.*)|*.*",
                Multiselect = false
            };
            if (dialog.ShowDialog(_shell) != true) return;
            await ImportAsync(dialog.FileName, isZip: true);
        }
        catch (Exception ex) { SetStatus("Import failed: " + ex.Message, StatusKind.Error); }
    }

    /// <summary>Asks which instance should receive the world, then copies or extracts it there.</summary>
    private async Task ImportAsync(string sourcePath, bool isZip)
    {
        if (_allPacks.Count == 0)
            _allPacks = await App.State.Api.ListPacksAsync();
        if (_allPacks.Count == 0)
        {
            SetStatus("Create an instance before importing a world.", StatusKind.Error);
            return;
        }

        if (isZip && !WorldService.ZipContainsWorld(sourcePath))
        {
            var proceed = await AppDialog.ConfirmAsync(_shell, "Import world",
                $"No level.dat was found near the top of {Path.GetFileName(sourcePath)}, so this may not be a world archive.\n\nImport it anyway?",
                "Import anyway");
            if (!proceed) return;
        }

        var picker = new PackPickerDialog(
            _allPacks,
            "Import world to...",
            "Pick the instance whose saves folder should receive this world.",
            "Import")
        { Owner = _shell };
        if (picker.ShowDialog() != true || picker.SelectedPackId is not { } packId) return;

        var pack = _allPacks.First(p => p.Id == packId);
        var label = Path.GetFileNameWithoutExtension(sourcePath.TrimEnd(Path.DirectorySeparatorChar));
        var ct = BeginBusy($"Importing {label} into {pack.Name}…");
        try
        {
            var folderName = isZip
                ? await App.State.Worlds.ImportZipAsync(sourcePath, pack.Id, pack.Name, label, BarProgress(), ct)
                : await App.State.Worlds.ImportFolderAsync(sourcePath, pack.Id, pack.Name, BarProgress(), ct);

            if (!string.IsNullOrEmpty(_activeFolder))
                App.State.Settings.AddWorldToFolder(_activeFolder, WorldService.Key(pack.Id, folderName));
            App.State.Settings.Save();

            EndBusy();
            await RefreshAsync();
            SetStatus($"Imported {folderName} into {pack.Name}.", StatusKind.Success);
        }
        catch (OperationCanceledException) { SetStatus("Import cancelled."); }
        catch (Exception ex) { SetStatus("Import failed: " + ex.Message, StatusKind.Error); }
        finally { EndBusy(); }
    }

    // ── drag and drop ────────────────────────────────────────────────────────

    /// <summary>Accepts a dropped save folder or world .zip, so importing does not have to start at
    /// a file dialog.</summary>
    private void OnWorldListDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedWorldPaths(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnWorldListDrop(object sender, DragEventArgs e)
    {
        try
        {
            var paths = DroppedWorldPaths(e);
            e.Handled = true;
            if (paths.Count == 0) return;
            if (paths.Count > 1)
                SetStatus($"Importing the first of {paths.Count} dropped items — drop one world at a time.");
            await ImportAsync(paths[0], isZip: File.Exists(paths[0]));
        }
        catch (Exception ex) { SetStatus("Import failed: " + ex.Message, StatusKind.Error); }
    }

    /// <summary>The dropped paths that could plausibly be a world: a folder with a level.dat, or a .zip.</summary>
    private static List<string> DroppedWorldPaths(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return new();
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return new();
        return paths.Where(p =>
            (Directory.Exists(p) && WorldService.IsWorldFolder(p)) ||
            (File.Exists(p) && Path.GetExtension(p).Equals(".zip", StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    // ── card actions ─────────────────────────────────────────────────────────

    /// <summary>Focus follows the click so the keyboard shortcuts have an unambiguous target, including
    /// on a right-click that is about to open the context menu.</summary>
    private void OnWorldCardPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: WorldRow row } fe) return;
        _focusedRow = row;
        fe.Focus();
    }

    private async void OnWorldCardKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: WorldRow row }) return;
        try
        {
            switch (e.Key)
            {
                case Key.Delete:
                    e.Handled = true;
                    await DeleteWorldAsync(row);
                    break;
                case Key.Enter:
                    e.Handled = true;
                    _shell.OpenWorldDetail(row.Source.Key, row.DisplayName);
                    break;
                case Key.F2:
                    e.Handled = true;
                    await RenameWorldAsync(row);
                    break;
            }
        }
        catch (Exception ex) { SetStatus(ex.Message, StatusKind.Error); }
    }

    private void OnWorldCardClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement el && IsInsideButton(el)) return;
        if (sender is FrameworkElement fe && fe.DataContext is WorldRow row)
            _shell.OpenWorldDetail(row.Source.Key, row.DisplayName);
    }

    private static bool IsInsideButton(DependencyObject? d)
    {
        while (d is not null)
        {
            if (d is Button) return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is WorldRow row)
            ShowPlayMenu(row, el);
    }

    private void ShowPlayMenu(WorldRow row, FrameworkElement anchor)
    {
        var compatible = App.State.Worlds.CompatiblePacks(row.Source, _allPacks);
        if (compatible.Count == 0)
        {
            SetStatus("No compatible instances. Add one in the compatibility editor.", StatusKind.Error);
            return;
        }
        var menu = new ContextMenu();
        foreach (var p in compatible)
        {
            var item = new MenuItem { Header = $"Play with {p.Name}" };
            var packId = p.Id;
            item.Click += async (_, _) =>
            {
                try { await PlayWithAsync(row, packId); }
                catch (Exception ex) { SetStatus("Launch failed: " + ex.Message, StatusKind.Error); }
            };
            menu.Items.Add(item);
        }
        menu.PlacementTarget = anchor;
        menu.IsOpen = true;
    }

    /// <summary>
    /// Launches an instance with this world, copying the save into that instance first if it is not
    /// the one the save lives in.
    /// </summary>
    /// <remarks>
    /// The copy used to be skipped without a word whenever the target folder already existed, so the
    /// second session with another instance silently loaded a stale copy of the world. When both
    /// exist the user is now shown when each was last written and asked which one they meant.
    /// </remarks>
    private async Task PlayWithAsync(WorldRow row, Guid packId)
    {
        SetStatus("Launching…");
        try
        {
            if (App.State.MinecraftAccounts.Current is null)
            {
                _shell.OpenMcAccount();
                SetStatus("Sign in to a Minecraft account first.", StatusKind.Error);
                return;
            }
            var pack = await App.State.Api.GetPackAsync(packId);
            App.State.Packs.EnsurePackFolder(pack.Id, pack.Name, pack.IsShared);

            if (packId != row.Source.SourcePackId)
            {
                var targetSaves = Path.Combine(App.State.Packs.GameDir(pack.Id), "saves", row.Source.FolderName);
                if (!Directory.Exists(targetSaves))
                {
                    var ct = BeginBusy($"Copying the save into {pack.Name}…");
                    try { await WorldService.CopyWorldAsync(row.Source.FolderPath, targetSaves, BarProgress(), ct); }
                    finally { EndBusy(); }
                }
                else if (await ConfirmReplaceExistingCopyAsync(row, pack.Name, targetSaves))
                {
                    var ct = BeginBusy($"Replacing the copy in {pack.Name}…");
                    try
                    {
                        // Not a delete followed by a copy: this token is the Cancel button's, and
                        // cancelling mid-copy would leave the user with neither world. The swap
                        // only happens once the new copy is complete.
                        await WorldService.ReplaceWorldAsync(row.Source.FolderPath, targetSaves, BarProgress(), ct);
                    }
                    finally { EndBusy(); }
                }
            }

            var proc = await App.State.Launcher.LaunchTrackedAsync(pack, new Progress<string>(_ => { }));
            _shell.OpenMinecraftHost(pack, proc);
            SetStatus($"Launched {pack.Name}.", StatusKind.Success);
        }
        catch (OperationCanceledException) { SetStatus("Cancelled."); }
        catch (Exception ex) { SetStatus("Launch failed: " + ex.Message, StatusKind.Error); }
    }

    /// <summary>
    /// True to overwrite the copy already sitting in the target instance, false to play it as it is.
    /// </summary>
    /// <remarks>
    /// Replacing is the affirmative answer on purpose: dismissing the dialog then falls through to
    /// playing the existing copy, which is the choice that cannot lose anyone's progress. Both
    /// timestamps are shown because "which of these two is the one I was playing" is the actual
    /// question, and neither folder name answers it.
    /// </remarks>
    private async Task<bool> ConfirmReplaceExistingCopyAsync(WorldRow row, string packName, string existingPath)
    {
        var mine = WorldService.ReadMeta(row.Source.FolderPath).LastPlayed ?? row.Source.LastModified;
        var theirs = WorldService.ReadMeta(existingPath).LastPlayed
                     ?? (DateTimeOffset)Directory.GetLastWriteTime(existingPath);

        return await AppDialog.ConfirmAsync(_shell,
            $"{row.DisplayName} is already in {packName}",
            $"That instance already has a copy of this save.\n\n" +
            $"Copy in {packName}: last played {theirs.LocalDateTime:g}\n" +
            $"This save in {row.SourcePackName}: last played {mine.LocalDateTime:g}\n\n" +
            $"Replacing overwrites the copy in {packName} and everything done in it.",
            "Replace it with this save", $"Play the copy in {packName}", danger: true);
    }

    // ── context-menu actions ─────────────────────────────────────────────────

    private WorldRow? RowFromMenu(object sender)
    {
        if (sender is MenuItem mi)
        {
            var parent = mi.Parent;
            while (parent is MenuItem p) parent = p.Parent;
            if (parent is ContextMenu cm && cm.PlacementTarget is FrameworkElement target
                && target.DataContext is WorldRow r) return r;
        }
        return _focusedRow;
    }

    private void OnCtxEdit(object sender, RoutedEventArgs e)
    {
        if (RowFromMenu(sender) is { } row) _shell.OpenWorldDetail(row.Source.Key, row.DisplayName);
    }

    private void OnCtxLaunch(object sender, RoutedEventArgs e)
    {
        if (RowFromMenu(sender) is { } row && sender is FrameworkElement el)
            ShowPlayMenu(row, el);
    }

    private void OnCtxReveal(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RowFromMenu(sender) is { } row)
                Process.Start(new ProcessStartInfo { FileName = row.Source.FolderPath, UseShellExecute = true });
        }
        catch (Exception ex) { SetStatus("Could not open the folder: " + ex.Message, StatusKind.Error); }
    }

    private async void OnCtxRename(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RowFromMenu(sender) is { } row) await RenameWorldAsync(row);
        }
        catch (Exception ex) { SetStatus("Rename failed: " + ex.Message, StatusKind.Error); }
    }

    /// <summary>
    /// Renames a world for real: the launcher label and the <c>LevelName</c> Minecraft shows.
    /// </summary>
    /// <remarks>
    /// "Rename" used to open the detail page, where the only editable name was a launcher-side label
    /// — so the world kept its old name in the game's own world list forever.
    /// </remarks>
    private async Task RenameWorldAsync(WorldRow row)
    {
        var dlg = new SimpleInputDialog("Rename world", "New name", row.DisplayName) { Owner = _shell };
        if (dlg.ShowDialog() != true) return;
        var name = (dlg.Result ?? "").Trim();
        if (string.IsNullOrEmpty(name) || name == row.DisplayName) return;

        try
        {
            await Task.Run(() => WorldService.SetLevelName(row.Source, name));
            App.State.Worlds.Rename(row.Source.Key, name);
            await RefreshAsync();
            SetStatus($"Renamed to {name}.", StatusKind.Success);
        }
        catch (IOException)
        {
            // Only the in-game name failed; keeping the launcher label in step would be a lie.
            SetStatus("Close Minecraft before renaming this world — its level.dat is in use.", StatusKind.Error);
        }
    }

    private async void OnCtxDuplicate(object sender, RoutedEventArgs e)
    {
        if (RowFromMenu(sender) is not { } row) return;
        var ct = BeginBusy($"Duplicating {row.DisplayName}…");
        try
        {
            var folder = await App.State.Worlds.DuplicateAsync(row.Source, BarProgress(), ct);
            EndBusy();
            await RefreshAsync();
            SetStatus($"Duplicated as {folder}.", StatusKind.Success);
        }
        catch (OperationCanceledException) { SetStatus("Duplicate cancelled."); }
        catch (Exception ex) { SetStatus("Duplicate failed: " + ex.Message, StatusKind.Error); }
        finally { EndBusy(); }
    }

    private async void OnCtxCopyToInstance(object sender, RoutedEventArgs e)
    {
        if (RowFromMenu(sender) is not { } row) return;
        try
        {
            var targets = _allPacks.Where(p => p.Id != row.Source.SourcePackId).ToList();
            if (targets.Count == 0)
            {
                SetStatus("There is no other instance to copy this world into.", StatusKind.Error);
                return;
            }

            var picker = new PackPickerDialog(targets,
                "Copy world to...",
                $"'{row.DisplayName}' will be copied into this instance's saves folder.",
                "Copy") { Owner = _shell };
            if (picker.ShowDialog() != true || picker.SelectedPackId is not { } packId) return;
            var pack = targets.First(p => p.Id == packId);

            var ct = BeginBusy($"Copying {row.DisplayName} into {pack.Name}…");
            try
            {
                var folder = await App.State.Worlds.CopyToPackAsync(row.Source, pack.Id, pack.Name, BarProgress(), ct);
                EndBusy();
                await RefreshAsync();
                SetStatus($"Copied to {pack.Name} as {folder}.", StatusKind.Success);
            }
            finally { EndBusy(); }
        }
        catch (OperationCanceledException) { SetStatus("Copy cancelled."); }
        catch (Exception ex) { SetStatus("Copy failed: " + ex.Message, StatusKind.Error); }
    }

    private async void OnCtxExportZip(object sender, RoutedEventArgs e)
    {
        if (RowFromMenu(sender) is not { } row) return;
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export world as .zip",
                Filter = "Zip archive (*.zip)|*.zip",
                FileName = WorldService.SafeFolderName(row.DisplayName) + ".zip"
            };
            if (dialog.ShowDialog(_shell) != true) return;

            var ct = BeginBusy($"Zipping {row.DisplayName}…");
            try
            {
                await WorldService.ExportZipAsync(row.Source, dialog.FileName, BarProgress(), ct);
                SetStatus($"Exported to {dialog.FileName}.", StatusKind.Success);
            }
            finally { EndBusy(); }
        }
        catch (OperationCanceledException) { SetStatus("Export cancelled."); }
        catch (Exception ex) { SetStatus("Export failed: " + ex.Message, StatusKind.Error); }
    }

    private async void OnCtxBackup(object sender, RoutedEventArgs e)
    {
        if (RowFromMenu(sender) is not { } row) return;
        var ct = BeginBusy($"Backing up {row.DisplayName}…");
        try
        {
            var zip = await App.State.Worlds.BackupAsync(row.Source, BarProgress(), ct);
            SetStatus($"Backed up to {Path.GetFileName(zip)}.", StatusKind.Success);
        }
        catch (OperationCanceledException) { SetStatus("Backup cancelled."); }
        catch (Exception ex) { SetStatus("Backup failed: " + ex.Message, StatusKind.Error); }
        finally { EndBusy(); }
    }

    /// <summary>Restoring is destructive, so it goes through the world's own detail screen where the
    /// backups are listed with their dates rather than being guessed at from a menu.</summary>
    private void OnCtxRestore(object sender, RoutedEventArgs e)
    {
        if (RowFromMenu(sender) is not { } row) return;
        _shell.OpenWorldDetail(row.Source.Key, row.DisplayName);
        SetStatus("Pick a snapshot on the Backups tab.");
    }

    private void OnCtxCopyKey(object sender, RoutedEventArgs e)
    {
        if (RowFromMenu(sender) is not { } row) return;
        var copied = ClipboardHelper.TrySetText(row.Source.Key);
        SetStatus(copied ? "World key copied." : "The clipboard is busy — try again.",
                  copied ? StatusKind.Success : StatusKind.Error);
    }

    private void OnCtxCopySeed(object sender, RoutedEventArgs e)
    {
        if (RowFromMenu(sender) is not { } row) return;
        if (row.Source.Info.Seed is not { } seed)
        {
            SetStatus("This world's level.dat does not record a seed.", StatusKind.Error);
            return;
        }
        var copied = ClipboardHelper.TrySetText(seed.ToString());
        SetStatus(copied ? $"Seed {seed} copied." : "The clipboard is busy — try again.",
                  copied ? StatusKind.Success : StatusKind.Error);
    }

    private async void OnCtxDelete(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RowFromMenu(sender) is { } row) await DeleteWorldAsync(row);
        }
        catch (Exception ex) { SetStatus("Delete failed: " + ex.Message, StatusKind.Error); }
    }

    /// <summary>
    /// Deletes a save from disk after a themed confirmation that says how much is about to go.
    /// </summary>
    /// <remarks>
    /// This used to be a bare Win32 message box. The size is in the prompt because "12.4 GB" is what
    /// tells someone whether they are deleting the world they meant to.
    /// </remarks>
    private async Task DeleteWorldAsync(WorldRow row)
    {
        var backups = App.State.Worlds.ListBackups(row.Source).Count;
        var backupNote = backups == 0
            ? "\n\nThere are no backups of this world."
            : $"\n\n{backups} backup{(backups == 1 ? "" : "s")} of it will be kept in the instance's backups folder.";

        var ok = await AppDialog.ConfirmAsync(_shell, "Delete world",
            $"Delete '{row.DisplayName}' from disk?\n\n" +
            $"{row.SizeLabel} in {row.Source.FolderPath}\n" +
            "This removes the save folder permanently." + backupNote,
            "Delete world", "Cancel", danger: true);
        if (!ok) return;

        var ct = BeginBusy($"Deleting {row.DisplayName}…");
        try
        {
            await App.State.Worlds.DeleteAsync(row.Source, ct);
            EndBusy();
            await RefreshAsync();
            SetStatus($"Deleted {row.DisplayName}.", StatusKind.Success);
        }
        catch (OperationCanceledException) { SetStatus("Delete cancelled."); }
        catch (IOException)
        {
            SetStatus("Close Minecraft before deleting this world — its files are in use.", StatusKind.Error);
        }
        catch (Exception ex) { SetStatus("Delete failed: " + ex.Message, StatusKind.Error); }
        finally { EndBusy(); }
    }

    private void OnCtxAddToFolderOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi) return;
        if (RowFromMenu(sender) is not { } row) return;

        mi.Items.Clear();
        var hasAny = false;
        foreach (var folder in App.State.Settings.WorldFolders.Keys.OrderBy(s => s))
        {
            hasAny = true;
            var isIn = App.State.Settings.WorldFolders[folder].Contains(row.Source.Key);
            var item = new MenuItem
            {
                Header = folder,
                IsCheckable = true,
                IsChecked = isIn
            };
            var worldKey = row.Source.Key;
            var fname = folder;
            item.Click += (_, _) =>
            {
                if (item.IsChecked) App.State.Settings.AddWorldToFolder(fname, worldKey);
                else App.State.Settings.RemoveWorldFromFolder(fname, worldKey);
                App.State.Settings.Save();
                RefreshFolderChips();
                ApplyFilter();
            };
            mi.Items.Add(item);
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
                App.State.Settings.CreateWorldFolder(fname);
                App.State.Settings.AddWorldToFolder(fname, row.Source.Key);
                App.State.Settings.Save();
                RefreshFolderChips();
                ApplyFilter();
            }
        };
        mi.Items.Add(create);
    }
}

public sealed class WorldRow
{
    private readonly ImageSource? _iconImage;

    /// <summary>Instances that hold a save folder of the same name — i.e. copies made by "Play with…".</summary>
    private readonly IReadOnlyList<string> _copiesIn;

    public WorldInfo Source { get; }
    public IReadOnlyList<PackSummary> AllPacks { get; }
    public string DisplayName => Source.DisplayName;
    public string SourcePackName => Source.SourcePackName;
    public string SizeLabel => FormatSize(Source.SizeBytes);
    public ImageSource? IconImage => _iconImage;
    public Visibility IconImageVisibility => _iconImage is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility FallbackVisibility => _iconImage is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>What the badge over the cover says: the game mode, which is the single most useful
    /// thing to be able to read off a grid of saves.</summary>
    public string GameModeBadge => Source.Info.GameType is null ? "WORLD" : Source.Info.GameModeLabel.ToUpperInvariant();
    public Visibility HardcoreVisibility => Source.Info.Hardcore ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CheatsVisibility => Source.Info.AllowCommands && !Source.Info.Hardcore
        ? Visibility.Visible : Visibility.Collapsed;
    public bool HasSeed => Source.Info.HasSeed;

    /// <summary>The line under the world's name: when it was last played, and what wrote it.</summary>
    public string MetaLabel
    {
        get
        {
            var when = Source.Info.LastPlayed is { } played
                ? $"Played {played.LocalDateTime:g}"
                : $"Modified {Source.LastModified.LocalDateTime:g}";
            return Source.Info.VersionName is { Length: > 0 } v ? $"{when} · MC {v}" : when;
        }
    }

    public string CompatibilityLabel
    {
        get
        {
            var line = Source.CompatibleWithAll
                ? "Compatible with all instances"
                : Source.CompatiblePackIds.Count <= 1
                    ? $"Only {Source.SourcePackName}"
                    : $"{Source.CompatiblePackIds.Count} instances";
            // Two cards with the same name and different source packs are otherwise indistinguishable.
            return _copiesIn.Count == 0 ? line : $"{line} · copy in {string.Join(", ", _copiesIn)}";
        }
    }

    /// <summary>Everything about the world that does not fit on the card.</summary>
    public string ToolTipText
    {
        get
        {
            var info = Source.Info;
            var lines = new List<string>
            {
                Source.DisplayName,
                $"Folder: {Source.FolderPath}",
                $"Instance: {Source.SourcePackName}",
                $"Size: {FormatSize(Source.SizeBytes)}",
                $"Mode: {info.GameModeLabel} · {info.DifficultyLabel}" +
                    (info.Hardcore ? " · hardcore" : "") + (info.AllowCommands ? " · cheats on" : ""),
                $"Minecraft: {info.VersionLabel}",
                $"Seed: {info.SeedLabel}"
            };
            lines.Add(info.LastPlayed is { } played
                ? $"Last played: {played.LocalDateTime:F}"
                : $"Last modified: {Source.LastModified.LocalDateTime:F}");
            if (_copiesIn.Count > 0) lines.Add($"Copies live in: {string.Join(", ", _copiesIn)}");
            return string.Join("\n", lines);
        }
    }

    public WorldRow(WorldInfo info, IReadOnlyList<PackSummary> allPacks, IReadOnlyList<WorldInfo>? allWorlds = null)
    {
        Source = info;
        AllPacks = allPacks;
        _iconImage = LoadWorldIcon(info.FolderPath);
        _copiesIn = allWorlds is null
            ? Array.Empty<string>()
            : allWorlds
                .Where(w => w.SourcePackId != info.SourcePackId
                            && string.Equals(w.FolderName, info.FolderName, StringComparison.OrdinalIgnoreCase))
                .Select(w => w.SourcePackName)
                .Distinct()
                .ToList();
    }

    public string Initial
    {
        get
        {
            var s = Source.DisplayName?.TrimStart() ?? "";
            return s.Length > 0 ? s[..1].ToUpperInvariant() : "?";
        }
    }

    public Brush CoverBackground
    {
        get
        {
            var palette = new (Color from, Color to)[]
            {
                (Color.FromRgb(0xC0, 0x30, 0x30), Color.FromRgb(0x7A, 0x1E, 0x1E)),
                (Color.FromRgb(0x4F, 0x9C, 0xF9), Color.FromRgb(0x1E, 0x4F, 0x8C)),
                (Color.FromRgb(0x10, 0xB9, 0x81), Color.FromRgb(0x0A, 0x6B, 0x4A)),
                (Color.FromRgb(0xA8, 0x55, 0xF7), Color.FromRgb(0x5F, 0x2C, 0x9A)),
                (Color.FromRgb(0xE3, 0xB3, 0x41), Color.FromRgb(0x8E, 0x6A, 0x18)),
                (Color.FromRgb(0x14, 0xB8, 0xA6), Color.FromRgb(0x0B, 0x6E, 0x65)),
                (Color.FromRgb(0xEC, 0x4F, 0x88), Color.FromRgb(0x8E, 0x25, 0x4E)),
                (Color.FromRgb(0xF9, 0x73, 0x16), Color.FromRgb(0x96, 0x42, 0x0B)),
            };

            var h = 0u;
            foreach (var c in Source.DisplayName ?? "") h = h * 31 + char.ToLowerInvariant(c);
            var (a, b) = palette[h % (uint)palette.Length];
            var gradient = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
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

    private static ImageSource? LoadWorldIcon(string folderPath)
    {
        var iconPath = Path.Combine(folderPath, "icon.png");
        if (!File.Exists(iconPath)) return null;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.UriSource = new Uri(iconPath, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
      : bytes >= 1024L * 1024 ? $"{bytes / (1024.0 * 1024):F1} MB"
      : bytes >= 1024 ? $"{bytes / 1024.0:F0} KB"
      : $"{bytes} B";
}
