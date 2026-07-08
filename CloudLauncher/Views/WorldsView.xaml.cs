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
        };
    }

    private async Task RefreshAsync()
    {
        StatusLabel.Text = "Scanning…";
        try
        {
            _allPacks = await App.State.Api.ListPacksAsync();
            var worlds = App.State.Worlds.ScanAll(_allPacks);
            _rows.Clear();
            foreach (var w in worlds) _rows.Add(new WorldRow(w, _allPacks));
            StatusLabel.Text = "";
            CountLabel.Text = $"{_rows.Count} world{(_rows.Count == 1 ? "" : "s")}";
            RefreshFolderChips();
            ApplyFilter();
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnShellKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsVisible) return;
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            ExpandSearch();
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            _ = RefreshAsync();
            e.Handled = true;
        }
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
                                    || r.SourcePackName.ToLowerInvariant().Contains(filter));

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

        _folderChips.Add(chip(null!, "All", _rows.Count, ""));
        foreach (var folder in App.State.Settings.WorldFolders.Keys.OrderBy(s => s))
        {
            var count = WorldsInFolder(folder).Count;
            _folderChips.Add(chip(folder, folder, count, ""));
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
        _ => "Last modified"
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
            StatusLabel.Text = "A folder with that name already exists.";
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

    private void OnFolderDelete(object sender, RoutedEventArgs e)
    {
        if (FolderFromMenu(sender) is not { } chip || string.IsNullOrEmpty(chip.Id)) return;
        if (MessageBox.Show(_shell, $"Delete folder '{chip.Id}'? The worlds themselves stay.",
                "Delete folder", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        App.State.Settings.DeleteWorldFolder(chip.Id);
        App.State.Settings.Save();
        if (_activeFolder == chip.Id) _activeFolder = null;
        RefreshFolderChips();
        ApplyFilter();
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

    private async void OnImportWorld(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_allPacks.Count == 0)
                _allPacks = await App.State.Api.ListPacksAsync();
            if (_allPacks.Count == 0)
            {
                StatusLabel.Text = "Create an instance before importing a world.";
                return;
            }

            var folderDialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select Minecraft world folder",
                Multiselect = false
            };
            if (folderDialog.ShowDialog(_shell) != true) return;

            var sourceWorldDir = folderDialog.FolderName;
            if (!File.Exists(Path.Combine(sourceWorldDir, "level.dat")))
            {
                StatusLabel.Text = "Select a Minecraft world folder that contains level.dat.";
                return;
            }

            var picker = new PackPickerDialog(
                _allPacks,
                "Import world to...",
                "Pick the instance whose saves folder should receive this world.",
                "Import")
            { Owner = _shell };
            if (picker.ShowDialog() != true || picker.SelectedPackId is not { } packId) return;

            var pack = _allPacks.First(p => p.Id == packId);
            var savesDir = Path.Combine(App.State.Packs.GameDir(pack.Id, pack.Name), "saves");
            Directory.CreateDirectory(savesDir);

            var folderName = UniqueWorldFolderName(savesDir, Path.GetFileName(sourceWorldDir));
            var targetDir = Path.Combine(savesDir, folderName);
            StatusLabel.Text = $"Importing {folderName}...";
            CopyDirectory(sourceWorldDir, targetDir);

            if (!string.IsNullOrEmpty(_activeFolder))
                App.State.Settings.AddWorldToFolder(_activeFolder, WorldService.Key(pack.Id, folderName));
            App.State.Settings.Save();

            await RefreshAsync();
            StatusLabel.Text = $"Imported {folderName} to {pack.Name}.";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Import failed: " + ex.Message;
        }
    }

    private static string UniqueWorldFolderName(string savesDir, string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string((name ?? "world").Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim();
        if (string.IsNullOrEmpty(clean)) clean = "world";

        var candidate = clean;
        var i = 2;
        while (Directory.Exists(Path.Combine(savesDir, candidate)))
            candidate = $"{clean}-{i++}";
        return candidate;
    }

    // ── card actions ─────────────────────────────────────────────────────────

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
            StatusLabel.Text = "No compatible instances. Add one in the compatibility editor.";
            return;
        }
        var menu = new ContextMenu();
        foreach (var p in compatible)
        {
            var item = new MenuItem { Header = $"Play with {p.Name}" };
            var packId = p.Id;
            item.Click += async (_, _) => await PlayWithAsync(row, packId);
            menu.Items.Add(item);
        }
        menu.PlacementTarget = anchor;
        menu.IsOpen = true;
    }

    private async Task PlayWithAsync(WorldRow row, Guid packId)
    {
        StatusLabel.Text = "Launching…";
        try
        {
            if (App.State.MinecraftAccounts.Current is null)
            {
                _shell.OpenMcAccount();
                StatusLabel.Text = "Sign in to a Minecraft account first.";
                return;
            }
            var pack = await App.State.Api.GetPackAsync(packId);
            App.State.Packs.EnsurePackFolder(pack.Id, pack.Name, pack.IsShared);
            // If launching with a non-source instance, copy the save folder into the target's saves.
            if (packId != row.Source.SourcePackId)
            {
                var targetSaves = Path.Combine(App.State.Packs.GameDir(pack.Id), "saves", row.Source.FolderName);
                if (!Directory.Exists(targetSaves))
                {
                    StatusLabel.Text = $"Copying save into {pack.Name}…";
                    CopyDirectory(row.Source.FolderPath, targetSaves);
                }
            }
            var proc = await App.State.Launcher.LaunchTrackedAsync(pack, new Progress<string>(_ => { }));
            _shell.OpenMinecraftHost(pack, proc);
            StatusLabel.Text = $"Launched {pack.Name}.";
        }
        catch (OperationCanceledException) { StatusLabel.Text = "Launch cancelled."; }
        catch (Exception ex) { StatusLabel.Text = "Launch failed: " + ex.Message; }
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var sub in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, sub)));
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            File.Copy(file, Path.Combine(dst, rel), overwrite: false);
        }
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
        return null;
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
        if (RowFromMenu(sender) is { } row)
            Process.Start(new ProcessStartInfo { FileName = row.Source.FolderPath, UseShellExecute = true });
    }

    private void OnCtxRename(object sender, RoutedEventArgs e)
    {
        if (RowFromMenu(sender) is { } row)
            _shell.OpenWorldDetail(row.Source.Key, row.DisplayName);
    }

    private void OnCtxCopyKey(object sender, RoutedEventArgs e)
    {
        if (RowFromMenu(sender) is { } row)
        {
            try { Clipboard.SetText(row.Source.Key); StatusLabel.Text = "World key copied."; }
            catch { /* clipboard locked */ }
        }
    }

    private void OnCtxDelete(object sender, RoutedEventArgs e)
    {
        if (RowFromMenu(sender) is not { } row) return;
        if (MessageBox.Show(_shell,
                $"Delete world '{row.DisplayName}' from disk?\n\nThis removes the save folder permanently.",
                "Delete world", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            Directory.Delete(row.Source.FolderPath, recursive: true);
            App.State.Settings.Worlds.Remove(row.Source.Key);
            foreach (var f in App.State.Settings.WorldFolders.Values) f.Remove(row.Source.Key);
            App.State.Settings.Save();
            _ = RefreshAsync();
        }
        catch (Exception ex) { StatusLabel.Text = "Delete failed: " + ex.Message; }
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

    public WorldInfo Source { get; }
    public IReadOnlyList<PackSummary> AllPacks { get; }
    public string DisplayName => Source.DisplayName;
    public string SourcePackName => Source.SourcePackName;
    public string MetaLabel => $"Modified {Source.LastModified.LocalDateTime:g}";
    public string SizeLabel => FormatSize(Source.SizeBytes);
    public ImageSource? IconImage => _iconImage;
    public Visibility IconImageVisibility => _iconImage is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility FallbackVisibility => _iconImage is null ? Visibility.Visible : Visibility.Collapsed;
    public string CompatibilityLabel
    {
        get
        {
            if (Source.CompatibleWithAll) return "Compatible with all instances";
            var count = Source.CompatiblePackIds.Count;
            return count <= 1 ? $"Only {Source.SourcePackName}" : $"{count} instances";
        }
    }

    public WorldRow(WorldInfo info, IReadOnlyList<PackSummary> allPacks)
    {
        Source = info;
        AllPacks = allPacks;
        _iconImage = LoadWorldIcon(info.FolderPath);
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
