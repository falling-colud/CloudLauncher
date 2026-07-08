using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

public partial class PackListView : Page
{
    private readonly MainWindow _shell;
    private readonly ObservableCollection<PackRow> _rows = new();
    private readonly ObservableCollection<FolderChipRow> _folderChips = new();
    private readonly Dictionary<Guid, ProgressInfo> _activeProgress = new();
    private PackSortMode _sortMode;

    // Active folder. null = "All". "team:<teamId>" = team folder. Otherwise user folder name.
    private string? _activeFolder;
    private List<TeamSummary> _teams = new();

    // Reference to the UniformGrid hosting the cards so we can adjust its Columns count.
    private UniformGrid? _packGrid;

    // Card minimum width (used as the divisor for column count). Cards stretch to fill cells.
    private const double CardMinWidth = 220;
    private const double SearchCollapsedWidth = 36;
    private const double SearchExpandedWidth = 260;

    public PackListView(MainWindow shell)
    {
        InitializeComponent();
        _shell = shell;
        _sortMode = App.State.Settings.PackSortMode;
        PackList.ItemsSource = _rows;
        FolderStrip.ItemsSource = _folderChips;
        UpdateSortMenuState();
        ProgressHub.ProgressChanged += OnProgressChanged;
        ProgressHub.ProgressCleared += OnProgressCleared;
        App.State.Instances.StateChanged += OnInstanceStateChanged;
        App.State.ModpackDownload.PackAdded += OnPackAdded;
        Loaded += async (_, _) =>
        {
            await RefreshAsync();
            Window.GetWindow(this)!.PreviewKeyDown += OnShellKeyDown;
        };
        Unloaded += (_, _) =>
        {
            ProgressHub.ProgressChanged -= OnProgressChanged;
            ProgressHub.ProgressCleared -= OnProgressCleared;
            App.State.Instances.StateChanged -= OnInstanceStateChanged;
            App.State.ModpackDownload.PackAdded -= OnPackAdded;
            if (Window.GetWindow(this) is Window w) w.PreviewKeyDown -= OnShellKeyDown;
        };
    }

    private void OnProgressChanged(ProgressInfo info)
    {
        _activeProgress[info.PackId] = info;
        var row = _rows.FirstOrDefault(r => r.Source.Id == info.PackId);
        row?.SetProgress(info.Fraction, info.Label);
        UpdateBottomProgress();
    }

    private void OnProgressCleared(Guid packId)
    {
        _activeProgress.Remove(packId);
        var row = _rows.FirstOrDefault(r => r.Source.Id == packId);
        row?.ClearProgress();
        UpdateBottomProgress();
    }

    private void OnInstanceStateChanged(Guid packId)
    {
        var row = _rows.FirstOrDefault(r => r.Source.Id == packId);
        row?.SetInstanceStatus(App.State.Instances.GetStatus(packId));
    }

    private void OnPackAdded(PackSummary pack) => AddOrUpdatePack(pack);

    /// <summary>Called when a pack is created or updated elsewhere (e.g. modpack download).</summary>
    public void AddOrUpdatePack(PackSummary pack)
    {
        App.State.Settings.UnhidePack(pack.Id);

        var existing = _rows.FirstOrDefault(r => r.Source.Id == pack.Id);
        if (existing is not null)
        {
            var index = _rows.IndexOf(existing);
            var row = new PackRow(pack);
            row.SetInstanceStatus(App.State.Instances.GetStatus(pack.Id));
            if (_activeProgress.TryGetValue(pack.Id, out var progress))
                row.SetProgress(progress.Fraction, progress.Label);
            _rows[index] = row;
        }
        else
        {
            var row = new PackRow(pack);
            row.SetInstanceStatus(App.State.Instances.GetStatus(pack.Id));
            if (_activeProgress.TryGetValue(pack.Id, out var progress))
                row.SetProgress(progress.Fraction, progress.Label);
            _rows.Add(row);
            EmptyState.Visibility = Visibility.Collapsed;
        }

        RefreshFolderChips();
        ApplyFilter();
    }

    private void UpdateBottomProgress()
    {
        if (_activeProgress.Count == 0)
        {
            BottomProgressArea.Visibility = Visibility.Collapsed;
            BottomProgressBar.IsIndeterminate = false;
            BottomProgressBar.Value = 0;
            BottomCurrentProgressArea.Visibility = Visibility.Collapsed;
            BottomCurrentProgressBar.IsIndeterminate = false;
            BottomCurrentProgressBar.Value = 0;
            BottomProgressLabel.Text = "";
            BottomProgressPercent.Text = "";
            BottomCurrentProgressLabel.Text = "";
            BottomCurrentProgressPercent.Text = "";
            return;
        }

        var info = _activeProgress.Values.Last();
        BottomProgressArea.Visibility = Visibility.Visible;
        BottomProgressLabel.Text = info.Label;
        if (info.Fraction < 0)
        {
            BottomProgressBar.IsIndeterminate = true;
            BottomProgressPercent.Text = "";
        }
        else
        {
            BottomProgressBar.IsIndeterminate = false;
            BottomProgressBar.Value = info.Fraction * 100;
            BottomProgressPercent.Text = $"{(int)(info.Fraction * 100)}%";
        }
        UpdateBottomCurrentProgress(info);
    }

    private void UpdateBottomCurrentProgress(ProgressInfo info)
    {
        if (!info.HasCurrentProgress)
        {
            BottomCurrentProgressArea.Visibility = Visibility.Collapsed;
            BottomCurrentProgressBar.IsIndeterminate = false;
            BottomCurrentProgressBar.Value = 0;
            BottomCurrentProgressLabel.Text = "";
            BottomCurrentProgressPercent.Text = "";
            return;
        }

        BottomCurrentProgressArea.Visibility = Visibility.Visible;
        BottomCurrentProgressLabel.Text = string.IsNullOrWhiteSpace(info.CurrentLabel)
            ? "Current stage"
            : info.CurrentLabel;

        if (info.CurrentFraction < 0)
        {
            BottomCurrentProgressBar.IsIndeterminate = true;
            BottomCurrentProgressPercent.Text = "";
        }
        else
        {
            BottomCurrentProgressBar.IsIndeterminate = false;
            BottomCurrentProgressBar.Value = info.CurrentFraction * 100;
            BottomCurrentProgressPercent.Text = $"{(int)(info.CurrentFraction * 100)}%";
        }
    }

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

    // ── responsive card grid ─────────────────────────────────────────────────

    private void OnPackGridLoaded(object sender, RoutedEventArgs e)
    {
        _packGrid = (UniformGrid)sender;
        UpdateColumns();
    }

    private void OnPackListContainerSizeChanged(object sender, SizeChangedEventArgs e) => UpdateColumns();

    /// <summary>
    /// Choose how many columns of cards fit at the current width, then assign that
    /// to the UniformGrid so rows are always full and cards left-align with no ragged edge.
    /// </summary>
    private void UpdateColumns()
    {
        if (_packGrid is null) return;
        var width = _packGrid.ActualWidth;
        if (width <= 0) width = ActualWidth - 80;        // fallback before measured
        if (width <= 0) return;
        var cols = Math.Max(1, (int)Math.Floor(width / CardMinWidth));
        if (_packGrid.Columns != cols) _packGrid.Columns = cols;
    }

    private async Task RefreshAsync()
    {
        StatusLabel.Text = "Loading…";
        EmptyState.Visibility = Visibility.Collapsed;
        try
        {
            var packs = await App.State.Api.ListPacksAsync();
            _rows.Clear();
            foreach (var p in packs.Where(p => !App.State.Settings.IsPackHidden(p.Id)))
            {
                var row = new PackRow(p);
                row.SetInstanceStatus(App.State.Instances.GetStatus(p.Id));
                if (_activeProgress.TryGetValue(p.Id, out var progress))
                    row.SetProgress(progress.Fraction, progress.Label);
                _rows.Add(row);
            }

            // Pull teams for team-folder display
            await ReloadTeamsAsync();

            // Build the folder chip strip
            RefreshFolderChips();
            ApplyFilter();

            StatusLabel.Text = "";
            EmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            StatusLabel.Text = ex.Message;
        }
    }

    private void RefreshFolderChips()
    {
        _folderChips.Clear();
        var rs = Application.Current.Resources;
        var accent      = (Brush)rs["AccentBrush"];
        var surface     = (Brush)rs["Surface3Brush"];
        var border      = (Brush)rs["BorderBrush"];
        var textPrimary = (Brush)rs["TextPrimaryBrush"];
        var textSecond  = (Brush)rs["TextSecondaryBrush"];

        FolderChipRow chip(string id, string label, int count, string icon, bool isTeam = false)
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
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                IsTeam = isTeam
            };
        }

        // All — pseudo-folder
        _folderChips.Add(chip(null!, "All", _rows.Count, ""));  // FilterAll-ish

        // Team folders
        foreach (var t in _teams.OrderBy(t => t.Name))
        {
            var teamId = TeamFolderId(t.Id);
            // Team instances aren't directly listed from PackSummary, so for now show 0 count.
            // User can still drill in to see manually-added instances (since we treat team:xxx as a folder key).
            var count = PacksInFolder(teamId).Count;
            _folderChips.Add(chip(teamId, t.Name, count, "", isTeam: true));
        }

        // User folders
        foreach (var fname in App.State.Settings.PackFolders.Keys.Where(k => !IsTeamFolderId(k)).OrderBy(s => s))
        {
            var count = PacksInFolder(fname).Count;
            _folderChips.Add(chip(fname, fname, count, ""));
        }
    }

    public async Task RefreshTeamFoldersAsync()
    {
        await ReloadTeamsAsync();
        RefreshFolderChips();
        ApplyFilter();
    }

    private async Task ReloadTeamsAsync()
    {
        try { _teams = await App.State.Api.ListTeamsAsync(); }
        catch { _teams = new(); }
    }

    private List<PackSummary> PacksInFolder(string folderId)
    {
        if (string.IsNullOrEmpty(folderId)) return _rows.Select(r => r.Source).ToList();
        if (App.State.Settings.PackFolders.TryGetValue(folderId, out var ids))
            return _rows.Where(r => ids.Contains(r.Source.Id)).Select(r => r.Source).ToList();
        return new();
    }

    private void ApplyFilter()
    {
        var filter = SearchBox.Text.Trim().ToLowerInvariant();

        IEnumerable<PackRow> source = _rows;

        // Folder filter
        if (!string.IsNullOrEmpty(_activeFolder))
        {
            if (App.State.Settings.PackFolders.TryGetValue(_activeFolder, out var ids))
                source = source.Where(r => ids.Contains(r.Source.Id));
            else source = Array.Empty<PackRow>();
        }

        if (!string.IsNullOrEmpty(filter))
            source = source.Where(r => r.Name.ToLowerInvariant().Contains(filter)
                                    || (r.Summary ?? "").ToLowerInvariant().Contains(filter)
                                    || (r.Description ?? "").ToLowerInvariant().Contains(filter));

        var filtered = SortRows(source).ToList();
        PackList.ItemsSource = filtered;

        // Toggle empty states
        if (_rows.Count == 0)
        {
            EmptyState.Visibility       = Visibility.Visible;
            EmptyFolderState.Visibility = Visibility.Collapsed;
        }
        else if (filtered.Count == 0 && !string.IsNullOrEmpty(_activeFolder))
        {
            EmptyState.Visibility       = Visibility.Collapsed;
            EmptyFolderState.Visibility = Visibility.Visible;
            var label = IsTeamFolderId(_activeFolder)
                ? _teams.FirstOrDefault(t => TeamFolderId(t.Id).Equals(_activeFolder, StringComparison.OrdinalIgnoreCase))?.Name ?? "Team"
                : _activeFolder;
            EmptyFolderTitle.Text = $"Nothing in {label} yet";
        }
        else
        {
            EmptyState.Visibility       = Visibility.Collapsed;
            EmptyFolderState.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void OnCreatePack(object sender, RoutedEventArgs e)
    {
        var dlg = new CreatePackDialog { Owner = _shell };
        if (dlg.ShowDialog() == true)
        {
            // If a folder is active, link the new instance into it for the creator.
            if (!string.IsNullOrEmpty(_activeFolder) && dlg.CreatedPack is { } created)
            {
                App.State.Settings.AddPackToFolder(_activeFolder, created.Id);
                App.State.Settings.Save();
            }
            await RefreshAsync();
        }
    }

    private void OnImportModpack(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select modpack file",
            Filter = "Modpack files|*.mrpack;*.zip|Modrinth pack (*.mrpack)|*.mrpack|CurseForge pack (*.zip)|*.zip"
        };
        if (dlg.ShowDialog(_shell) != true) return;
        var name = Path.GetFileNameWithoutExtension(dlg.FileName);
        _ = App.State.ModpackDownload.StartLocalFileImportAsync(dlg.FileName, name);
    }

    private void OnDownloadPack(object sender, RoutedEventArgs e) => _shell.OpenPackBrowser();

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    // ── sorting ──────────────────────────────────────────────────────────────

    private IEnumerable<PackRow> SortRows(IEnumerable<PackRow> rows)
    {
        IOrderedEnumerable<PackRow> ordered = rows.OrderByDescending(r => r.IsPinned);
        ordered = _sortMode switch
        {
            PackSortMode.Version => ordered
                .ThenByDescending(r => VersionSortValue(r.Source.MinecraftVersion))
                .ThenByDescending(r => r.Source.MinecraftVersion ?? ""),
            PackSortMode.PlayCount => ordered.ThenByDescending(r => r.PlayCount),
            PackSortMode.TimePlayed => ordered.ThenByDescending(r => r.TotalPlayTimeSeconds),
            _ => ordered
                .ThenByDescending(r => r.LastPlayedAt ?? DateTimeOffset.MinValue)
                .ThenByDescending(r => r.Source.UpdatedAt)
        };

        return ordered.ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase);
    }

    private static long VersionSortValue(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return 0;
        var parts = version.Split('.', '-', '_');
        long value = 0;
        for (var i = 0; i < 4; i++)
        {
            value *= 1000;
            if (i >= parts.Length) continue;
            if (!int.TryParse(parts[i], out var part)) break;
            value += Math.Clamp(part, 0, 999);
        }
        return value;
    }

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
        if (!Enum.TryParse<PackSortMode>(tag, out var mode)) return;
        _sortMode = mode;
        App.State.Settings.PackSortMode = mode;
        App.State.Settings.Save();
        UpdateSortMenuState();
        ApplyFilter();
    }

    private void UpdateSortMenuState()
    {
        SortLastPlayedMenuItem.IsChecked = _sortMode == PackSortMode.LastPlayed;
        SortVersionMenuItem.IsChecked = _sortMode == PackSortMode.Version;
        SortPlayCountMenuItem.IsChecked = _sortMode == PackSortMode.PlayCount;
        SortTimePlayedMenuItem.IsChecked = _sortMode == PackSortMode.TimePlayed;
        SortButton.ToolTip = $"Sort instances: {SortModeLabel(_sortMode)}";
    }

    private static string SortModeLabel(PackSortMode mode) => mode switch
    {
        PackSortMode.Version => "Version",
        PackSortMode.PlayCount => "Times played",
        PackSortMode.TimePlayed => "Time played",
        _ => "Last played"
    };

    // ── folder strip ─────────────────────────────────────────────────────────

    private void OnCreateFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new SimpleInputDialog("Create folder", "Folder name", "") { Owner = _shell };
        if (dlg.ShowDialog() != true) return;
        var name = (dlg.Result ?? "").Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (IsTeamFolderId(name))
        {
            StatusLabel.Text = "Names starting with 'team:' are reserved.";
            return;
        }
        App.State.Settings.CreatePackFolder(name);
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
        // Suppress context menu for the All chip
        if (sender is FrameworkElement el && el.DataContext is FolderChipRow chip
            && string.IsNullOrEmpty(chip.Id))
        {
            e.Handled = true;
        }
    }

    private void OnFolderRename(object sender, RoutedEventArgs e)
    {
        if (FolderFromMenu(sender) is not { } chip || string.IsNullOrEmpty(chip.Id) || chip.IsTeam) return;
        var dlg = new SimpleInputDialog("Rename folder", "New name", chip.Id) { Owner = _shell };
        if (dlg.ShowDialog() != true) return;
        var newName = (dlg.Result ?? "").Trim();
        if (string.IsNullOrEmpty(newName) || newName == chip.Id) return;
        if (App.State.Settings.PackFolders.ContainsKey(newName))
        {
            StatusLabel.Text = "A folder with that name already exists.";
            return;
        }
        var members = App.State.Settings.PackFolders[chip.Id];
        App.State.Settings.PackFolders.Remove(chip.Id);
        App.State.Settings.PackFolders[newName] = members;
        if (_activeFolder == chip.Id) _activeFolder = newName;
        App.State.Settings.Save();
        RefreshFolderChips();
        ApplyFilter();
    }

    private void OnFolderDelete(object sender, RoutedEventArgs e)
    {
        if (FolderFromMenu(sender) is not { } chip || string.IsNullOrEmpty(chip.Id) || chip.IsTeam) return;
        if (MessageBox.Show(_shell, $"Delete folder '{chip.Id}'? The instances themselves stay.",
                "Delete folder", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        App.State.Settings.DeletePackFolder(chip.Id);
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

    // ── card click / play / update ───────────────────────────────────────────

    private void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement el && IsInsideButton(el)) return;
        if (sender is FrameworkElement fe && fe.DataContext is PackRow row)
            _shell.OpenPackDetail(row.Source.Id, row.Source.Name);
    }

    private void OnCardRightClick(object sender, MouseButtonEventArgs e) { /* let ContextMenu open */ }

    private static bool IsInsideButton(DependencyObject? d)
    {
        while (d is not null)
        {
            if (d is Button) return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    private async void OnCardPlay(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Guid id })
            await QuickPlayAsync(id);
    }

    private void OnCardKill(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id }) return;
        try
        {
            App.State.Instances.Stop(id);
            StatusLabel.Text = "Stopping Minecraft instance...";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Stop failed: " + ex.Message;
        }
    }

    private async void OnCardUpdate(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Guid id })
            await QuickUpdateAsync(id);
    }

    private async Task QuickPlayAsync(Guid id)
    {
        StatusLabel.Text = "Preparing to launch…";
        try
        {
            var pack = await App.State.Api.GetPackAsync(id);
            if (pack.IsEmpty)
            {
                StatusLabel.Text = "This instance is empty - open it to configure first.";
                return;
            }
            if (App.State.MinecraftAccounts.Current is null)
            {
                StatusLabel.Text = "Sign in to a Minecraft account first.";
                _shell.OpenMcAccount();
                return;
            }
            App.State.Packs.EnsurePackFolder(pack.Id, pack.Name, pack.IsShared);
            var proc = await App.State.Launcher.LaunchTrackedAsync(pack, new Progress<string>(_ => { }));
            _shell.OpenMinecraftHost(pack, proc);
            StatusLabel.Text = $"Minecraft started (PID {proc.Id}).";
            RefreshUsageState(id);
        }
        catch (OperationCanceledException) { StatusLabel.Text = "Launch cancelled."; }
        catch (Exception ex) { StatusLabel.Text = "Launch failed: " + ex.Message; }
    }

    private async Task QuickUpdateAsync(Guid id)
    {
        StatusLabel.Text = "Updating from server…";
        try
        {
            var pack = await App.State.Api.GetPackAsync(id);
            if (!pack.IsShared) { StatusLabel.Text = "Instance is not shared."; return; }
            App.State.Packs.EnsurePackFolder(pack.Id, pack.Name, pack.IsShared);
            await App.State.Packs.DownloadSharedAsync(pack.Id, new Progress<string>(_ => { }));
            StatusLabel.Text = "Updated.";
        }
        catch (Exception ex) { StatusLabel.Text = "Update failed: " + ex.Message; }
    }

    // ── context-menu actions ─────────────────────────────────────────────────

    private PackRow? RowFromMenuSender(object sender)
    {
        if (sender is FrameworkElement el && el.DataContext is PackRow row) return row;
        if (sender is MenuItem mi)
        {
            var parent = mi.Parent;
            while (parent is MenuItem p) parent = p.Parent;
            if (parent is ContextMenu cm && cm.PlacementTarget is FrameworkElement target
                && target.DataContext is PackRow r) return r;
        }
        return null;
    }

    private async void OnCtxPlay(object sender, RoutedEventArgs e)
    {
        if (RowFromMenuSender(sender) is { } row) await QuickPlayAsync(row.Source.Id);
    }

    private void OnPinToggle(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Guid id } && _rows.FirstOrDefault(r => r.Source.Id == id) is { } row)
        {
            TogglePinned(row);
            e.Handled = true;
        }
    }

    private void OnCtxTogglePin(object sender, RoutedEventArgs e)
    {
        if (RowFromMenuSender(sender) is { } row) TogglePinned(row);
    }

    private void TogglePinned(PackRow row)
    {
        App.State.Settings.SetPackPinned(row.Source.Id, !row.IsPinned);
        App.State.Settings.Save();
        row.RefreshUsage();
        ApplyFilter();
    }

    private void RefreshUsageState(Guid packId)
    {
        _rows.FirstOrDefault(r => r.Source.Id == packId)?.RefreshUsage();
        ApplyFilter();
    }

    /// <summary>Re-read a single card's cover image from disk (after it changed elsewhere).</summary>
    public void RefreshPackCover(Guid packId)
        => _rows.FirstOrDefault(r => r.Source.Id == packId)?.RefreshCover();

    private void OnCtxOpen(object sender, RoutedEventArgs e)
    {
        if (RowFromMenuSender(sender) is { } row) _shell.OpenPackDetail(row.Source.Id, row.Source.Name);
    }

    private async void OnCtxRename(object sender, RoutedEventArgs e)
    {
        if (RowFromMenuSender(sender) is not { } row) return;
        if (App.State.Settings.UserId != row.Source.OwnerId)
        {
            StatusLabel.Text = "Only the instance owner can rename it.";
            return;
        }

        var dlg = new SimpleInputDialog("Rename instance", "New name", row.Source.Name) { Owner = _shell };
        if (dlg.ShowDialog() != true) return;

        var name = (dlg.Result ?? "").Trim();
        if (string.IsNullOrEmpty(name) || string.Equals(name, row.Source.Name, StringComparison.Ordinal))
            return;

        StatusLabel.Text = "Renaming instance...";
        try
        {
            var updated = await App.State.Api.UpdatePackAsync(
                row.Source.Id,
                new UpdatePackRequest(name, null, null, null, null, null, null, null));
            App.State.Packs.TryRenameFolder(row.Source.Id, name); // keep the on-disk folder in step
            AddOrUpdatePack(updated);
            StatusLabel.Text = "Instance renamed.";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Rename failed: " + ex.Message;
        }
    }

    private void OnCtxChangeImage(object sender, RoutedEventArgs e)
    {
        if (RowFromMenuSender(sender) is not { } row) return;
        if (App.State.Settings.UserId != row.Source.OwnerId)
        {
            StatusLabel.Text = "Only the instance owner can change its image.";
            return;
        }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose instance image",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp"
                     + "|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(_shell) != true) return;

        try
        {
            App.State.PackAssets.SaveIconFromFile(row.Source.Id, dlg.FileName);
            row.RefreshCover();
            _shell.RefreshPackDetailHero(row.Source.Id);
            StatusLabel.Text = "Image updated.";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Couldn't set image: " + ex.Message;
        }
    }

    private void OnCtxOpenGameFolder(object sender, RoutedEventArgs e)
    {
        if (RowFromMenuSender(sender) is { } row)
        {
            var dir = App.State.Packs.GameDir(row.Source.Id, row.Source.Name);
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
    }

    private void OnCtxPackManagement(object sender, RoutedEventArgs e)
    {
        if (RowFromMenuSender(sender) is { } row) _shell.OpenPackDetail(row.Source.Id, row.Source.Name);
    }

    private async void OnCtxBrowseMods(object sender, RoutedEventArgs e)
    {
        if (RowFromMenuSender(sender) is not { } row) return;
        try
        {
            var detail = await App.State.Api.GetPackAsync(row.Source.Id);
            _shell.OpenModExplorerForPack(detail);
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private void OnCtxCopyId(object sender, RoutedEventArgs e)
    {
        if (RowFromMenuSender(sender) is { } row)
        {
            try { Clipboard.SetText(row.Source.Id.ToString()); StatusLabel.Text = "Instance ID copied."; }
            catch { /* clipboard locked */ }
        }
    }

    private async void OnCtxDelete(object sender, RoutedEventArgs e)
    {
        if (RowFromMenuSender(sender) is not { } row) return;
        var me = App.State.Settings.UserId;
        var isOwner = me.HasValue && row.Source.OwnerId == me.Value;
        var confirm = new ConfirmDeleteDialog(row.Name, isOwner) { Owner = _shell };
        if (confirm.ShowDialog() != true) return;

        var packId = row.Source.Id;
        foreach (var f in App.State.Settings.PackFolders.Values) f.Remove(packId);

        string? syncWarning = null;
        try
        {
            if (isOwner)
            {
                await App.State.Api.DeletePackAsync(packId);
                App.State.Settings.UnhidePack(packId);
            }
            else
            {
                App.State.Settings.HidePack(packId);
                try
                {
                    await App.State.Api.UnsubscribePackAsync(packId);
                }
                catch (Exception ex)
                {
                    syncWarning = ex.Message;
                }
            }

            App.State.Settings.Save();
            _shell.CloseSidePanelForPack(packId);
            await RefreshAsync();

            StatusLabel.Text = syncWarning is null
                ? isOwner ? "Instance deleted." : "Instance removed."
                : "Removed from your list. Server sync failed: " + syncWarning;
        }
        catch (Exception ex)
        {
            StatusLabel.Text = (isOwner ? "Delete" : "Remove") + " failed: " + ex.Message;
        }
    }

    // ── add-to-folder submenu ───────────────────────────────────────────────

    private void OnCtxAddToFolderOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi) return;
        if (RowFromMenuSender(sender) is not { } row) return;

        mi.Items.Clear();
        // User folders
        var userFolders = App.State.Settings.PackFolders.Keys.Where(k => !IsTeamFolderId(k)).OrderBy(s => s);
        var hasAny = false;
        foreach (var folder in userFolders)
        {
            hasAny = true;
            var isIn = App.State.Settings.PackFolders[folder].Contains(row.Source.Id);
            var item = new MenuItem
            {
                Header = folder,
                IsCheckable = true,
                IsChecked = isIn
            };
            var packId = row.Source.Id;
            var fname = folder;
            item.Click += (_, _) =>
            {
                if (item.IsChecked) App.State.Settings.AddPackToFolder(fname, packId);
                else App.State.Settings.RemovePackFromFolder(fname, packId);
                App.State.Settings.Save();
                RefreshFolderChips();
                ApplyFilter();
            };
            mi.Items.Add(item);
        }
        // Team folders
        if (_teams.Count > 0)
        {
            if (hasAny) mi.Items.Add(new Separator());
            foreach (var team in _teams.OrderBy(t => t.Name))
            {
                var teamKey = TeamFolderId(team.Id);
                var inIt = App.State.Settings.PackFolders.TryGetValue(teamKey, out var l) && l.Contains(row.Source.Id);
                var item = new MenuItem
                {
                    Header = team.Name + " (team)",
                    IsCheckable = true,
                    IsChecked = inIt
                };
                var packId = row.Source.Id;
                var key = teamKey;
                item.Click += (_, _) =>
                {
                    if (item.IsChecked) App.State.Settings.AddPackToFolder(key, packId);
                    else App.State.Settings.RemovePackFromFolder(key, packId);
                    App.State.Settings.Save();
                    RefreshFolderChips();
                    ApplyFilter();
                };
                mi.Items.Add(item);
            }
            hasAny = true;
        }
        if (!hasAny)
        {
            var none = new MenuItem { Header = "(no folders — create one first)", IsEnabled = false };
            mi.Items.Add(none);
        }
        mi.Items.Add(new Separator());
        var create = new MenuItem { Header = "Create new folder…" };
        create.Click += (_, _) =>
        {
            var dlg = new SimpleInputDialog("Create folder", "Folder name", "") { Owner = _shell };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Result))
            {
                var fname = dlg.Result!.Trim();
                if (IsTeamFolderId(fname))
                {
                    StatusLabel.Text = "Names starting with 'team:' are reserved.";
                    return;
                }
                App.State.Settings.CreatePackFolder(fname);
                App.State.Settings.AddPackToFolder(fname, row.Source.Id);
                App.State.Settings.Save();
                RefreshFolderChips();
                ApplyFilter();
            }
        };
        mi.Items.Add(create);
    }

    private static string TeamFolderId(Guid teamId) => $"team:{teamId:N}";

    private static bool IsTeamFolderId(string? folderId) =>
        !string.IsNullOrEmpty(folderId)
        && folderId.StartsWith("team:", StringComparison.OrdinalIgnoreCase);
}

public sealed class PackRow : INotifyPropertyChanged
{
    public PackRow(PackSummary src) { Source = src; }

    public PackSummary Source { get; }
    public string Name => Source.Name;
    public string? Summary => Source.Summary;
    public string? Description => Source.Description;
    public string OwnerLabel => $"by {Source.OwnerUsername} · updated {Source.UpdatedAt.LocalDateTime:g}";
    public string OwnerShortLabel => $"by {Source.OwnerUsername}";
    public bool IsPlayable => !Source.IsEmpty;
    public bool IsShared => Source.IsShared;
    public bool IsPinned => Usage.IsPinned;
    public int PlayCount => Usage.PlayCount;
    public long TotalPlayTimeSeconds => Usage.TotalPlayTimeSeconds;
    public DateTimeOffset? LastPlayedAt => Usage.LastPlayedAt;
    public string PinToolTip => IsPinned ? "Unpin instance" : "Pin instance";
    public string PinMenuHeader => IsPinned ? "Unpin instance" : "Pin instance";
    public Visibility UpdateButtonVisibility => Source.IsShared && !_isWorking && !IsInstanceBusy
        ? Visibility.Visible : Visibility.Collapsed;

    private PackUsageStats Usage => App.State.Settings.GetPackUsage(Source.Id);

    // ── progress ─────────────────────────────────────────────────────────────
    private bool _isWorking;
    private MinecraftInstanceStatus _instanceStatus = MinecraftInstanceStatus.Idle;
    private double _progressPercent;
    private bool _isIndeterminate;
    private string _progressLabel = "";

    public bool IsWorking => _isWorking;
    public bool IsInstanceBusy => _instanceStatus != MinecraftInstanceStatus.Idle;
    public double ProgressPercent => _progressPercent;
    public bool IsIndeterminate => _isIndeterminate;
    public string ProgressLabel => _progressLabel;
    public string ProgressPercentLabel => _isIndeterminate ? "..." : $"{_progressPercent:0}%";
    public string ProgressSummaryLabel => string.IsNullOrWhiteSpace(_progressLabel) ? "Working..." : _progressLabel;
    public Visibility PlayVisibility     => !_isWorking && !IsInstanceBusy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility KillVisibility     => IsInstanceBusy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProgressVisibility => _isWorking && !IsInstanceBusy ? Visibility.Visible : Visibility.Collapsed;

    public void SetInstanceStatus(MinecraftInstanceStatus status)
    {
        if (_instanceStatus == status) return;
        _instanceStatus = status;
        OnAllProgressChanged();
    }

    public void SetProgress(double fraction, string label)
    {
        _isWorking = true;
        if (fraction < 0)
        {
            _isIndeterminate = true;
            _progressPercent = 0;
        }
        else
        {
            _isIndeterminate = false;
            _progressPercent = Math.Round(fraction * 100, 0);
        }
        _progressLabel = label ?? "";
        OnAllProgressChanged();
    }

    public void ClearProgress()
    {
        _isWorking = false;
        _progressPercent = 0;
        _isIndeterminate = false;
        _progressLabel = "";
        OnAllProgressChanged();
    }

    private void OnAllProgressChanged()
    {
        OnPropertyChanged(nameof(IsWorking));
        OnPropertyChanged(nameof(IsInstanceBusy));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(IsIndeterminate));
        OnPropertyChanged(nameof(ProgressLabel));
        OnPropertyChanged(nameof(ProgressPercentLabel));
        OnPropertyChanged(nameof(ProgressSummaryLabel));
        OnPropertyChanged(nameof(PlayVisibility));
        OnPropertyChanged(nameof(KillVisibility));
        OnPropertyChanged(nameof(ProgressVisibility));
        OnPropertyChanged(nameof(UpdateButtonVisibility));
    }

    public void RefreshUsage()
    {
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(PlayCount));
        OnPropertyChanged(nameof(TotalPlayTimeSeconds));
        OnPropertyChanged(nameof(LastPlayedAt));
        OnPropertyChanged(nameof(PinToolTip));
        OnPropertyChanged(nameof(PinMenuHeader));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>The instance's first letter, used as the cover-art glyph.</summary>
    public string Initial
    {
        get
        {
            var s = Source.Name?.TrimStart() ?? "";
            return s.Length > 0 ? s.Substring(0, 1).ToUpperInvariant() : "?";
        }
    }

    public ImageSource? CoverImage => _coverImage ??= App.State.PackAssets.TryLoadIconImage(Source.Id);

    public Visibility CoverImageVisibility => CoverImage is not null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility InitialVisibility => CoverImage is null ? Visibility.Visible : Visibility.Collapsed;

    private ImageSource? _coverImage;

    public void RefreshCover()
    {
        _coverImage = null;
        OnPropertyChanged(nameof(CoverImage));
        OnPropertyChanged(nameof(CoverImageVisibility));
        OnPropertyChanged(nameof(InitialVisibility));
    }

    /// <summary>Mod-loader name shown in the bottom-left badge on the cover.</summary>
    public string LoaderBadge => Source.IsEmpty ? "EMPTY"
        : Source.Loader switch
        {
            LoaderKind.Fabric   => "FABRIC",
            LoaderKind.Forge    => "FORGE",
            LoaderKind.NeoForge => "NEOFORGE",
            _                   => "VANILLA"
        };

    public Visibility LoaderBadgeVisibility => Visibility.Visible;

    /// <summary>Per-instance gradient brush picked deterministically from the instance name.</summary>
    public Brush CoverBackground
    {
        get
        {
            // 8-colour palette so each instance gets a stable, distinct cover.
            var palette = new (Color from, Color to)[]
            {
                (Color.FromRgb(0x7A, 0x16, 0x16), Color.FromRgb(0x3E, 0x0A, 0x0A)), // red
                (Color.FromRgb(0x4F, 0x9C, 0xF9), Color.FromRgb(0x1E, 0x4F, 0x8C)), // blue
                (Color.FromRgb(0x10, 0xB9, 0x81), Color.FromRgb(0x0A, 0x6B, 0x4A)), // green
                (Color.FromRgb(0xA8, 0x55, 0xF7), Color.FromRgb(0x5F, 0x2C, 0x9A)), // purple
                (Color.FromRgb(0xE3, 0xB3, 0x41), Color.FromRgb(0x8E, 0x6A, 0x18)), // gold
                (Color.FromRgb(0x14, 0xB8, 0xA6), Color.FromRgb(0x0B, 0x6E, 0x65)), // teal
                (Color.FromRgb(0xEC, 0x4F, 0x88), Color.FromRgb(0x8E, 0x25, 0x4E)), // pink
                (Color.FromRgb(0xF9, 0x73, 0x16), Color.FromRgb(0x96, 0x42, 0x0B)), // orange
            };
            // Stable hash of the instance name (case-insensitive, ordinal).
            var h = 0u;
            foreach (var c in Source.Name ?? "") h = h * 31 + (uint)char.ToLowerInvariant(c);
            var (a, b) = palette[h % (uint)palette.Length];
            var gradient = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint   = new Point(1, 1),
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

    public string VersionLabel =>
        Source.IsEmpty
            ? "Empty instance"
            : Source.Loader == LoaderKind.None
                ? $"MC {Source.MinecraftVersion}"
                : $"MC {Source.MinecraftVersion} · {Source.Loader} {Source.LoaderVersion}";

    public string TagText => Source.IsEmpty ? "EMPTY"
        : Source.IsShared ? Source.Visibility switch
            {
                PackVisibility.Public => "PUBLIC",
                PackVisibility.Team   => "TEAM",
                _                     => "SHARED"
            }
        : "LOCAL";

    public Brush TagBackground => Source.IsEmpty ? (Brush)App.Current.FindResource("TagEmptyBgBrush")
        : Source.IsShared ? Source.Visibility switch
            {
                PackVisibility.Public => (Brush)App.Current.FindResource("TagPublicBgBrush"),
                PackVisibility.Team   => (Brush)App.Current.FindResource("TagTeamBgBrush"),
                _                     => (Brush)App.Current.FindResource("TagSharedBgBrush")
            }
        : (Brush)App.Current.FindResource("TagEmptyBgBrush");

    public Brush TagForeground => Source.IsEmpty ? (Brush)App.Current.FindResource("TagEmptyFgBrush")
        : Source.IsShared ? Source.Visibility switch
            {
                PackVisibility.Public => (Brush)App.Current.FindResource("TagPublicFgBrush"),
                PackVisibility.Team   => (Brush)App.Current.FindResource("TagTeamFgBrush"),
                _                     => (Brush)App.Current.FindResource("TagSharedFgBrush")
            }
        : (Brush)App.Current.FindResource("TagEmptyFgBrush");
}

public sealed class FolderChipRow
{
    public string Id { get; set; } = "";        // null/empty = "All". "team:..." for team folder. Else user-folder name.
    public string Label { get; set; } = "";
    public string Icon { get; set; } = "";
    public string CountLabel { get; set; } = "";
    public Brush Background { get; set; } = Brushes.Transparent;
    public Brush BorderColor { get; set; } = Brushes.Transparent;
    public Brush Foreground { get; set; } = Brushes.White;
    public Brush CountForeground { get; set; } = Brushes.Gray;
    public FontWeight FontWeight { get; set; } = FontWeights.Normal;
    public bool IsTeam { get; set; }
}
