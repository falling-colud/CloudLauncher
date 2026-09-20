using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CloudLauncher.Services;
using CloudLauncher.Shared;
using Microsoft.Win32;

namespace CloudLauncher.Views;

public partial class ResourcePacksView : Page
{
    private readonly MainWindow _shell;
    private readonly ObservableCollection<ResourcePackChipVm> _chips = new();
    private readonly ObservableCollection<RpBrowseRowVm> _browseRows = new();
    private readonly ObservableCollection<ResourcePackScannerRowVm> _scanRows = new();
    private readonly ObservableCollection<FolderChipRow> _folderChips = new();
    private readonly List<ModVersion> _cachedExternalVers = new();

    private ResourcePackChipVm? _activeChipVm;
    private ResourcePackChipVm? _installedChipVm;
    private CancellationTokenSource _cts = new();

    private List<PackSummary> _packs = new();
    private UniformGrid? _rpUniform;
    private ResourcePackSortMode _sortMode;
    private string? _activeFolder;

    private ResourcePackScannerRowVm? _focusedRow;
    private HashSet<string> _installedNames = new(StringComparer.OrdinalIgnoreCase);

    private ModSummary? _selExternal;
    private HostedResourcePackDetail? _selHostedDetail;
    private string? _projectUrl;

    private string _search = "";
    private string? _filterMc;
    private int _off;
    private bool _busy;
    private bool _more = true;

    private const int PageSize = 25;
    private const int BrowseCap = 200;
    private const double CardW = 220;
    private const double SearchTiny = 36;
    private const double SearchWide = 260;

    public ResourcePacksView(MainWindow shell)
    {
        InitializeComponent();
        _shell = shell;
        _sortMode = App.State.Settings.ResourcePackSortMode;
        SourceStrip.ItemsSource = _chips;
        ResultsList.ItemsSource = _browseRows;
        RpList.ItemsSource = _scanRows;
        FolderStrip.ItemsSource = _folderChips;
        ModTabs.SelectionChanged += OnModTabsChanged;

        Loaded += async (_, _) =>
        {
            await InitializeChipsAsync();
            Window.GetWindow(this)!.PreviewKeyDown += GlobalKeys;
            // The chips and folder chips are painted in code, so they would keep the old accent after
            // a theme change until the screen was navigated away from and back.
            ThemeService.Changed += OnThemeChanged;
        };
        Unloaded += (_, _) =>
        {
            if (Window.GetWindow(this) is Window w)
                w.PreviewKeyDown -= GlobalKeys;
            ThemeService.Changed -= OnThemeChanged;
            _cts.Cancel();
        };
    }

    // Render rich HTML/markdown descriptions in an embedded WebView2. Its HWND draws over
    // WPF (airspace), so hide it off-tab. Scrolling is native — no manual wheel routing.
    private void OnModTabsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != ModTabs) return;
        var onOverview = ModTabs.SelectedIndex == 0;
        OverviewHost.Visibility = onOverview ? Visibility.Visible : Visibility.Collapsed;
        OverviewBrowser.Visibility = onOverview ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowOverview(string? content, bool isMarkdown = false) => OverviewBrowser.Show(content, isMarkdown);

    private async Task InitializeChipsAsync()
    {
        await Task.Yield(); // silence analyzer
        _chips.Clear();
        _installedChipVm = Vm("\uEDE3", "Installed", BrowseKind.Installed);
        _chips.Add(_installedChipVm);
        _chips.Add(DividerVm());
        _chips.Add(Vm("\uE753", "CurseForge", BrowseKind.CurseForge));
        _chips.Add(Vm("\uE753", "Modrinth", BrowseKind.Modrinth));
        _chips.Add(Vm("\uE7C3", "CloudLauncher", BrowseKind.CloudPublic));
        _chips.Add(DividerVm());
        _chips.Add(Vm("\uE77B", "Personal", BrowseKind.CloudPersonal));
        _chips.Add(Vm("\uE8F2", "Shared", BrowseKind.CloudShared));
        _chips.Add(Vm("\uE716", "Teams", BrowseKind.CloudTeam));

        try
        {
            foreach (var tm in (await App.State.Api.ListTeamsAsync()).OrderBy(t => t.Name))
                _chips.Add(Vm("\uE716", tm.Name, BrowseKind.CloudTeam, tm.Id));
        }
        catch { /* browsing without teams */ }

        ChipPaint();
        if (_installedChipVm is not null)
            await ActivateChipAsync(_installedChipVm);
    }

    private static ResourcePackChipVm Vm(string icon, string label, BrowseKind kind, Guid? team = null) => new()
    {
        Icon = icon,
        Label = label,
        Kind = kind,
        TeamId = team,
        CursorHint = Cursors.Hand,
        ChipVisibility = Visibility.Visible,
        IconVisibility = Visibility.Visible
    };

    private ResourcePackChipVm DividerVm() => new()
    {
        Label = "|",
        IsDivider = true,
        Kind = BrowseKind.Splitter,
        IconVisibility = Visibility.Collapsed,
        CursorHint = Cursors.Arrow
    };

    private void ChipPaint()
    {
        foreach (var c in _chips)
        {
            if (c.IsDivider)
            {
                c.Background = Brushes.Transparent;
                c.BorderColor = Brushes.Transparent;
                c.Foreground = (Brush)FindResource("TextTertiaryBrush");
                c.FontWeight = FontWeights.Normal;
                continue;
            }
            var on = ReferenceEquals(c, _activeChipVm);
            c.Background = (Brush)FindResource(on ? "AccentBrush" : "Surface2Brush");
            c.BorderColor = (Brush)FindResource(on ? "AccentBrush" : "BorderBrush");
            c.Foreground = (Brush)FindResource(on ? "TextOnAccentBrush" : "TextSecondaryBrush");
            c.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
        SourceStrip.Items.Refresh();
    }

    private async void OnSourceChipClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ResourcePackChipVm r } && !r.IsDivider)
            await ActivateChipAsync(r);
    }

    private async Task ActivateChipAsync(ResourcePackChipVm vm)
    {
        _activeChipVm = vm;
        ChipPaint();
        bool lib = vm.Kind == BrowseKind.Installed;

        InstalledPane.Visibility = lib ? Visibility.Visible : Visibility.Collapsed;
        BrowsePane.Visibility = lib ? Visibility.Collapsed : Visibility.Visible;
        BrowseExtrasHost.Visibility = lib ? Visibility.Collapsed : Visibility.Visible;

        if (lib)
        {
            _cts.Cancel();
            await RefreshInstalledAsync();
        }
        else
            await BrowseRestartAsync();
    }

    // Toolbar -----------------------------------------------------------------

    private async void OnCreateResourcePack(object sender, RoutedEventArgs e)
    {
        var dlg = new CreateResourcePackDialog { Owner = _shell };
        if (dlg.ShowDialog() != true || dlg.Created is null) return;
        var chip = _chips.FirstOrDefault(z => z.Kind == BrowseKind.CloudPersonal);
        if (chip != null) await ActivateChipAsync(chip);

        var created = dlg.Created;
        _shell.OpenResourcePackDetail(created.Id, created.Name);
    }

    private async void OnImportZip(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_packs.Any()) _packs = await App.State.Api.ListPacksAsync();
            if (!_packs.Any())
            {
                Fail("Need an instance first.");
                return;
            }
            var ofd = new OpenFileDialog { Filter = "Zip|*.zip|All|*", Title = "Resource pack zip" };
            if (ofd.ShowDialog(_shell) != true) return;
            var dlg = new PackPickerDialog(_packs, "Import", "Chooses destination resourcepacks/", "Pick")
                { Owner = _shell };
            if (dlg.ShowDialog() != true || dlg.SelectedPackId is not Guid gid) return;
            var target = _packs.First(z => z.Id == gid);

            Okay($"Copying into {target.Name}…");
            var source = ofd.FileName;
            var dest = await Task.Run(() =>
            {
                var dir = RpFolder(target);
                var path = Bump(dir, Path.GetFileName(source));
                File.Copy(source, path, overwrite: false);
                return path;
            });
            await RefreshInstalledAsync();
            Okay($"Copied {Path.GetFileName(dest)} into {target.Name} — turn it on from that instance's Resources tab.");
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private void OnOpenDownloadBrowser(object sender, RoutedEventArgs e) =>
        _shell.OpenResourcePackBrowser();

    private void OnCreateFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new SimpleInputDialog("Create folder", "Folder name", "") { Owner = _shell };
        if (dlg.ShowDialog() != true) return;
        var name = (dlg.Result ?? "").Trim();
        if (string.IsNullOrEmpty(name)) return;
        App.State.Settings.CreateResourcePackFolder(name);
        App.State.Settings.Save();
        _activeFolder = name;
        RebuildFolders();
        FilterInstalledUi();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (_activeChipVm?.Kind == BrowseKind.Installed)
            await RefreshInstalledAsync();
        else
            await BrowseRestartAsync();
    }

    private void OnFiltersClick(object sender, RoutedEventArgs e) => FiltersPopup.IsOpen = true;

    private async void OnApplyFilters(object sender, RoutedEventArgs e)
    {
        _filterMc = string.IsNullOrWhiteSpace(FilterMcBox.Text) ? null : FilterMcBox.Text.Trim();
        FiltersPopup.IsOpen = false;
        await BrowseRestartAsync();
    }

    /// <summary>Success/idle text. The brush is a resource REFERENCE, not a copy, so recolouring the
    /// accent in Settings repaints the line that is already on screen.</summary>
    private void Okay(string m)
    {
        StatusLabel.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        StatusLabel.Text = m;
    }

    private void Fail(string m)
    {
        StatusLabel.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        StatusLabel.Text = m;
    }

    private void OnThemeChanged()
    {
        ChipPaint();
        RebuildFolders();
    }

    // Compact search ------------------------------------------------------------

    private void OnSearchToggle(object sender, RoutedEventArgs e)
    {
        CompactSearchHost.Width = SearchWide;
        SearchBox.Visibility = Visibility.Visible;
        SearchBox.Focus();
    }

    private void CollapseSearch()
    {
        CompactSearchHost.Width = SearchTiny;
        SearchBox.Visibility = Visibility.Collapsed;
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => _search = SearchBox.Text;

    private async void OnSearchPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await FireSearchAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SearchBox.Clear(); _search = "";
            CollapseSearch();
            await FireSearchAsync();
            e.Handled = true;
        }
    }

    private void OnSearchLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is DependencyObject d && CompactSearchHosts(d, CompactSearchHost)) return;
        CollapseSearch();
    }

    private static bool CompactSearchHosts(DependencyObject leaf, DependencyObject host)
    {
        for (var cur = leaf; cur != null; cur = VisualTreeHelper.GetParent(cur))
            if (ReferenceEquals(cur, host)) return true;
        return false;
    }

    private async Task FireSearchAsync()
    {
        if (_activeChipVm?.Kind == BrowseKind.Installed)
            FilterInstalledUi();
        else
            await BrowseRestartAsync();
    }

    private async void GlobalKeys(object sender, KeyEventArgs e)
    {
        if (!IsVisible) return;

        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OnSearchToggle(sender, new RoutedEventArgs());
            SearchBox.Focus();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F5)
        {
            e.Handled = true;
            if (_activeChipVm?.Kind == BrowseKind.Installed)
                await RefreshInstalledAsync();
            else
                await BrowseRestartAsync();
            return;
        }

        // The card grid has no keyboard focus of its own, so these act on the last card the user
        // clicked or right-clicked — which is the card they are looking at.
        if (_activeChipVm?.Kind != BrowseKind.Installed || _focusedRow is null) return;
        if (SearchBox.IsKeyboardFocusWithin) return;

        try
        {
            switch (e.Key)
            {
                case Key.Delete:
                    e.Handled = true;
                    await DeleteRowAsync(_focusedRow);
                    break;
                case Key.F2:
                    e.Handled = true;
                    RenameDisplayName(_focusedRow);
                    break;
                case Key.Enter:
                    e.Handled = true;
                    _shell.OpenLocalResourcePackDetail(_focusedRow.Key, _focusedRow.DisplayName);
                    break;
            }
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    // Installed library ---------------------------------------------------------

    private async Task RefreshInstalledAsync()
    {
        try
        {
            _packs = await App.State.Api.ListPacksAsync();

            // Scanning walks every instance's resourcepacks/ folder and opens each zip for its
            // pack.png — seconds of work on a big library, and none of it belongs on the UI thread.
            var packs = _packs;
            var discovered = await Task.Run(() =>
                App.State.ResourcePacks.ScanAll(packs)
                    .Select(info => (info, meta: ResourcePackService.ReadMeta(info.FilePath, info.IsFolder)))
                    .ToList());

            _scanRows.Clear();
            foreach (var (info, meta) in discovered)
                _scanRows.Add(new ResourcePackScannerRowVm(info, meta));

            // What the browse side needs to know to say "Installed" instead of offering a duplicate.
            _installedNames = discovered
                .SelectMany(d => new[]
                {
                    d.info.DisplayName,
                    System.IO.Path.GetFileNameWithoutExtension(d.info.FileName)
                })
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            MarkInstalledBrowseRows();

            if (_focusedRow is not null)
                _focusedRow = _scanRows.FirstOrDefault(r => r.Key == _focusedRow.Key);

            RebuildFolders();
            FilterInstalledUi();
            Okay("");
            CountLabel.Text = $"{discovered.Count} scanned";
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    /// <summary>Re-evaluates the "already installed" flag on whatever browse results are loaded.
    /// Run after a scan and after every install, so the button on the row the user just used flips
    /// without a re-search.</summary>
    private void MarkInstalledBrowseRows()
    {
        var changed = false;
        foreach (var row in _browseRows)
            changed |= row.ApplyInstalled(_installedNames.Contains(row.Name));
        if (changed) ResultsList.Items.Refresh();
    }

    private void RebuildFolders()
    {
        _folderChips.Clear();

        Brush accent = (Brush)FindResource("AccentBrush"),
            surf = (Brush)FindResource("Surface3Brush"),
            borderBr = (Brush)FindResource("BorderBrush"),
            txtPri = (Brush)FindResource("TextPrimaryBrush"),
            txtSec = (Brush)FindResource("TextSecondaryBrush");

        FolderChipRow Mk(string key, string label, int count, string icon)
        {
            var sel = (_activeFolder ?? "") == key;
            return new FolderChipRow
            {
                Id = key,
                Label = label,
                Icon = icon,
                CountLabel = count > 0 ? $"({count})" : "",
                Background = surf,
                BorderColor = sel ? accent : borderBr,
                Foreground = txtPri,
                CountForeground = txtSec,
                FontWeight = sel ? FontWeights.SemiBold : FontWeights.Normal
            };
        }

        _folderChips.Add(Mk("", "All", _scanRows.Count, "\uEDE3"));

        foreach (var name in App.State.Settings.ResourcePackFolders.Keys.OrderBy(s => s))
        {
            var count =
                App.State.Settings.ResourcePackFolders[name].Intersect(_scanRows.Select(r => r.Key)).Count();
            _folderChips.Add(Mk(name, name, count, "\uEDE4"));
        }
    }

    private void FilterInstalledUi()
    {
        IEnumerable<ResourcePackScannerRowVm> seq = _scanRows;

        if (!string.IsNullOrEmpty(_activeFolder)
            && App.State.Settings.ResourcePackFolders.TryGetValue(_activeFolder!, out var members))
            seq = seq.Where(r => members.Contains(r.Key));

        var q = SearchBox.Text.Trim().ToLowerInvariant();
        if (q.Length > 0)
        {
            // "on" / "off" / "local" are the three questions this screen gets asked most: which packs
            // are actually loading, which are dead weight, and which are not shared with the instance.
            seq = q switch
            {
                "on" => seq.Where(r => r.Enabled),
                "off" => seq.Where(r => !r.Enabled),
                "local" => seq.Where(r => r.IsLocal),
                _ => seq.Where(r =>
                    r.DisplayName.ToLowerInvariant().Contains(q)
                    || r.SourcePack.ToLowerInvariant().Contains(q)
                    || r.FileName.ToLowerInvariant().Contains(q)
                    || (r.PackDescription?.ToLowerInvariant().Contains(q) ?? false))
            };
        }

        seq = Sorted(seq);
        var list = seq.ToList();
        RpList.ItemsSource = list;
        CountLabel.Text = $"{list.Count} shown";

        if (!_scanRows.Any())
        {
            RpEmptyState.Visibility = Visibility.Visible;
            RpEmptyFolderState.Visibility = Visibility.Collapsed;
        }
        else if (!list.Any() && !string.IsNullOrEmpty(_activeFolder))
        {
            RpEmptyFolderTitle.Text = $"Nothing in {_activeFolder}";
            RpEmptyState.Visibility = Visibility.Collapsed;
            RpEmptyFolderState.Visibility = Visibility.Visible;
        }
        else
        {
            RpEmptyState.Visibility = Visibility.Collapsed;
            RpEmptyFolderState.Visibility = Visibility.Collapsed;
        }
    }

    private IEnumerable<ResourcePackScannerRowVm> Sorted(IEnumerable<ResourcePackScannerRowVm> rows) =>
        _sortMode switch
        {
            ResourcePackSortMode.Name => rows.OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenByDescending(r => r.Modified),
            ResourcePackSortMode.Size => rows.OrderByDescending(r => r.Bytes).ThenBy(r => r.DisplayName),
            ResourcePackSortMode.Pack => rows.OrderBy(r => r.SourcePack).ThenBy(r => r.DisplayName),
            _ => rows.OrderByDescending(r => r.Modified).ThenBy(r => r.DisplayName)
        };

    private void OnRpGridLoaded(object sender, RoutedEventArgs e)
    {
        _rpUniform = (UniformGrid)sender;
        TuneUniform();
    }

    private void OnRpListContainerSizeChanged(object sender, SizeChangedEventArgs e) => TuneUniform();

    private void TuneUniform()
    {
        if (_rpUniform is null) return;
        var span = Math.Max(200d, _rpUniform.ActualWidth);
        var cols = Math.Max(1, (int)Math.Floor(span / CardW));
        if (_rpUniform.Columns != cols) _rpUniform.Columns = cols;
    }

    private void OnSortMenuOpened(object sender, RoutedEventArgs e) => SyncSortChecks();

    private void OnSortButtonClick(object sender, RoutedEventArgs e)
    {
        SortButton.ContextMenu!.PlacementTarget = SortButton;
        SortButton.ContextMenu.Placement = PlacementMode.Bottom;
        SyncSortChecks();
        SortButton.ContextMenu.IsOpen = true;
    }

    private void OnSortMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string t }) return;
        if (!Enum.TryParse<ResourcePackSortMode>(t, out var parsed)) return;
        _sortMode = parsed;
        App.State.Settings.ResourcePackSortMode = parsed;
        App.State.Settings.Save();
        SyncSortChecks();
        FilterInstalledUi();
    }

    private void SyncSortChecks()
    {
        SortModifiedMenuItem.IsChecked = _sortMode == ResourcePackSortMode.Modified;
        SortNameMenuItem.IsChecked = _sortMode == ResourcePackSortMode.Name;
        SortSizeMenuItem.IsChecked = _sortMode == ResourcePackSortMode.Size;
        SortPackMenuItem.IsChecked = _sortMode == ResourcePackSortMode.Pack;
    }

    private void OnFolderChipClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FolderChipRow fc }) return;
        _activeFolder = string.IsNullOrEmpty(fc.Id) ? null : fc.Id;
        RebuildFolders();
        FilterInstalledUi();
    }

    private void OnFolderChipRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FolderChipRow fc } && fc.Id.Length == 0)
            e.Handled = true;
    }

    private FolderChipRow? FolderCtx(object src)
    {
        if (src is MenuItem leaf)
        {
            DependencyObject? p = leaf;
            while (p is MenuItem m) p = m.Parent as MenuItem ?? m.Parent;
            if (p is ContextMenu { PlacementTarget: FrameworkElement fx } &&
                fx.DataContext is FolderChipRow row)
                return row;
        }
        return null;
    }

    private void OnFolderRename(object sender, RoutedEventArgs e)
    {
        var row = FolderCtx(sender); if (row is null || string.IsNullOrEmpty(row.Id)) return;
        var ask = new SimpleInputDialog("Rename", "Folder", row.Id) { Owner = _shell };
        if (ask.ShowDialog() != true) return;
        var fresh = ask.Result!.Trim();
        if (fresh.Length == 0 || fresh == row.Id) return;
        var map = App.State.Settings.ResourcePackFolders;
        if (map.ContainsKey(fresh))
        {
            Fail("Duplicate folder name."); return;
        }
        map[fresh] = map[row.Id];
        map.Remove(row.Id);
        if (_activeFolder == row.Id) _activeFolder = fresh;
        App.State.Settings.Save();
        RebuildFolders(); FilterInstalledUi();
    }

    private async void OnFolderDelete(object sender, RoutedEventArgs e)
    {
        try
        {
            var row = FolderCtx(sender);
            if (row == null || string.IsNullOrEmpty(row.Id)) return;

            var count = App.State.Settings.ResourcePackFolders.TryGetValue(row.Id, out var members) ? members.Count : 0;
            if (!await AppDialog.ConfirmAsync(_shell,
                    "Delete folder",
                    $"Delete the folder '{row.Id}'?"
                    + (count > 0 ? $"\n\nThe {count} pack(s) filed in it stay on disk — only the grouping goes." : ""),
                    "Delete folder", "Cancel", danger: true))
                return;

            App.State.Settings.DeleteResourcePackFolder(row.Id);
            if (_activeFolder == row.Id) _activeFolder = null;
            App.State.Settings.Save();

            RebuildFolders();
            FilterInstalledUi();
            Okay($"Deleted folder '{row.Id}'.");
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private ResourcePackScannerRowVm? ScannerFromCtx(object sender) =>
        sender switch
        {
            MenuItem leaf => WalkMenu(leaf),
            _ => null
        };

    private static ResourcePackScannerRowVm? WalkMenu(MenuItem leaf)
    {
        DependencyObject? p = leaf;
        while (p is MenuItem m) p = m.Parent as MenuItem ?? m.Parent;
        return p is ContextMenu cm && cm.PlacementTarget is FrameworkElement fx && fx.DataContext is ResourcePackScannerRowVm rr
            ? rr
            : null;
    }

    private void OnRpCardClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ResourcePackScannerRowVm rr }) return;
        _focusedRow = rr;
        _shell.OpenLocalResourcePackDetail(rr.Key, rr.DisplayName);
    }

    /// <summary>Remembers which card a context menu belongs to, so the Delete and F2 keys act on the
    /// card the user last touched rather than on nothing.</summary>
    private void OnCardMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu { PlacementTarget: FrameworkElement { DataContext: ResourcePackScannerRowVm row } })
            _focusedRow = row;
    }

    private void OnCtxEdit(object sender, RoutedEventArgs e)
    {
        if (ScannerFromCtx(sender) is ResourcePackScannerRowVm r)
            _shell.OpenLocalResourcePackDetail(r.Key, r.DisplayName);
    }

    private void OnCtxReveal(object sender, RoutedEventArgs e)
    {
        if (ScannerFromCtx(sender) is not ResourcePackScannerRowVm row) return;
        if (!File.Exists(row.Path) && !Directory.Exists(row.Path)) { Fail("That file is no longer there."); return; }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + row.Path + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    /// <summary>
    /// Renames the label the launcher shows for a pack.
    /// </summary>
    /// <remarks>This used to open the detail page instead, so nothing in the app could rename a pack
    /// from the grid at all. The file on disk keeps its name — "Rename file on disk…" is the other,
    /// riskier half, and it is deliberately a separate command.</remarks>
    private void OnCtxRename(object sender, RoutedEventArgs e)
    {
        var row = ScannerFromCtx(sender) ?? _focusedRow;
        if (row is not null) RenameDisplayName(row);
    }

    private void RenameDisplayName(ResourcePackScannerRowVm row)
    {
        var dlg = new SimpleInputDialog("Rename resource pack", "Display name", row.DisplayName) { Owner = _shell };
        if (dlg.ShowDialog() != true) return;

        var name = (dlg.Result ?? "").Trim();
        if (name.Length == 0) { Fail("Name cannot be empty."); return; }

        App.State.ResourcePacks.Rename(row.Key, name);
        _ = RefreshInstalledAsync();
        Okay("Renamed to " + name + ".");
    }

    /// <summary>
    /// Renames the zip (or pack folder) itself, carrying the launcher's entry and the instance's
    /// options.txt line across with it.
    /// </summary>
    /// <remarks>The settings key contains the file name, so without the re-key the pack would come
    /// back as a stranger: no display name, no folder membership, no hosting link. And because
    /// options.txt lists packs by file name, a pack that was enabled has to be renamed there too or
    /// it silently switches itself off.</remarks>
    private async void OnCtxRenameFile(object sender, RoutedEventArgs e)
    {
        try
        {
            var row = ScannerFromCtx(sender) ?? _focusedRow;
            if (row is null) return;

            var dlg = new SimpleInputDialog("Rename file on disk", "File name", row.FileName) { Owner = _shell };
            if (dlg.ShowDialog() != true) return;

            var wanted = ResourcePackService.SanitizeFileName(dlg.Result ?? "", row.IsFolder);
            if (string.Equals(wanted, row.FileName, StringComparison.Ordinal)) return;
            if (row.Enabled && !await ConfirmStackWriteAsync(row)) return;

            var info = row.Info;
            await Task.Run(() => App.State.ResourcePacks.RenameFileOnDisk(info, wanted));
            await RefreshInstalledAsync();
            Okay("Renamed to " + wanted + ".");
        }
        catch (Exception ex) { Fail("Rename failed: " + ex.Message); }
    }

    /// <summary>
    /// Turns a pack on or off for the instance it is installed in, by rewriting that instance's
    /// options.txt resource pack stack.
    /// </summary>
    /// <remarks>A newly enabled pack goes on top of the stack — the position the game itself gives
    /// one you select, and the only one where a freshly installed texture pack is actually visible.
    /// Ordering within the stack is done on the instance's own Resources tab, which has the room for
    /// it; this is the one-click "is it on" the card grid needs.</remarks>
    private async void OnCtxToggleEnabled(object sender, RoutedEventArgs e)
    {
        try
        {
            var row = ScannerFromCtx(sender) ?? _focusedRow;
            if (row is null) return;
            if (!await ConfirmStackWriteAsync(row)) return;

            var turnOn = !row.Enabled;
            App.State.ResourcePacks.SetEnabled(row.Info.SourcePackId, row.Info.SourcePackName, row.FileName, turnOn);
            await RefreshInstalledAsync();
            Okay(turnOn
                ? row.DisplayName + " is on — top of " + row.SourcePackName + "'s stack."
                : row.DisplayName + " is off in " + row.SourcePackName + ".");
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    /// <summary>Minecraft reads options.txt at startup and rewrites it from memory on exit, so a
    /// change made under a running instance is thrown away when that instance closes.</summary>
    private async Task<bool> ConfirmStackWriteAsync(ResourcePackScannerRowVm row)
    {
        if (!App.State.Instances.IsBusy(row.Info.SourcePackId)) return true;
        return await AppDialog.ConfirmAsync(_shell,
            "Minecraft is running",
            row.SourcePackName + " is open. Minecraft rewrites options.txt when it closes, so this "
            + "change would be lost. Do it anyway?",
            "Do it anyway", "Cancel", danger: true);
    }

    private async void OnCardCopyTo(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement { DataContext: ResourcePackScannerRowVm row })
                await CopyToInstanceAsync(row);
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private async void OnCtxCopyInstance(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ScannerFromCtx(sender) is ResourcePackScannerRowVm row)
                await CopyToInstanceAsync(row);
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private async Task CopyToInstanceAsync(ResourcePackScannerRowVm row)
    {
        if (!_packs.Any()) _packs = await App.State.Api.ListPacksAsync();

        var allow = App.State.ResourcePacks.CompatiblePacks(row.Info, _packs)
            .Where(p => p.Id != row.Info.SourcePackId)
            .ToList();
        if (!allow.Any())
        {
            Fail("No other instance is flagged compatible — set that on the pack's Compatibility tab.");
            return;
        }

        var pick = new PackPickerDialog(allow, "Copy resource pack", row.DisplayName, "Copy") { Owner = _shell };
        if (pick.ShowDialog() != true || pick.SelectedPackId is not Guid gid) return;

        var target = allow.First(pk => pk.Id == gid);
        Okay("Copying to " + target.Name + "…");
        try
        {
            var sourcePath = row.Path;
            var fileName = row.FileName;
            var isFolder = row.IsFolder;
            await Task.Run(() =>
            {
                var dir = RpFolder(target);
                var dest = BumpPath(dir, fileName, isFolder);
                if (isFolder) CopyDirectory(sourcePath, dest);
                else File.Copy(sourcePath, dest, overwrite: false);
            });

            // The copy is a new pack in another instance, and this grid shows every instance — so it
            // has to be rebuilt. Saying "Copied" and showing nothing new was the old behaviour here.
            await RefreshInstalledAsync();
            Okay("Copied to " + target.Name);
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private void OnCtxCopyKey(object sender, RoutedEventArgs e)
    {
        if (ScannerFromCtx(sender)?.Key is string ky && Services.ClipboardHelper.TrySetText(ky))
            Okay("Copied key.");
    }

    private async void OnCtxDelete(object sender, RoutedEventArgs e)
    {
        try
        {
            var row = ScannerFromCtx(sender) ?? _focusedRow;
            if (row is not null) await DeleteRowAsync(row);
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private async Task DeleteRowAsync(ResourcePackScannerRowVm row)
    {
        var what = row.IsFolder ? "pack folder" : "zip";
        if (!await AppDialog.ConfirmAsync(_shell,
                "Delete resource pack",
                "Permanently delete the " + what + " '" + row.FileName + "' from " + row.SourcePackName + "?"
                + (row.Enabled ? "\n\nIt is turned on, so it will be removed from that instance's stack too." : ""),
                "Delete", "Cancel", danger: true))
            return;

        try
        {
            var info = row.Info;
            await Task.Run(() => App.State.ResourcePacks.DeleteFromDisk(info));
            App.State.Settings.Save();
            await RefreshInstalledAsync();
            Okay("Deleted " + row.FileName + ".");
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private void OnCtxAddToFolderOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem root || ScannerFromCtx(sender) is not ResourcePackScannerRowVm row) return;
        root.Items.Clear();

        foreach (var folder in App.State.Settings.ResourcePackFolders.Keys.OrderBy(s => s))
        {
            var inside = App.State.Settings.ResourcePackFolders[folder].Contains(row.Key);
            var mi = new MenuItem { Header = folder, IsCheckable = true, IsChecked = inside };
            var locked = folder;
            mi.Click += (_, _) =>
            {
                if (mi.IsChecked) App.State.Settings.AddResourcePackToFolder(locked, row.Key);
                else App.State.Settings.RemoveResourcePackFromFolder(locked, row.Key);
                App.State.Settings.Save();
                RebuildFolders(); FilterInstalledUi();
            };
            root.Items.Add(mi);
        }
        root.Items.Add(new Separator());
        var neu = new MenuItem { Header = "Create folder…" };
        neu.Click += (_, _) =>
        {
            var dlg = new SimpleInputDialog("New folder", "Name", "") { Owner = _shell };
            if (dlg.ShowDialog() != true) return;
            var name = dlg.Result!.Trim();
            if (name.Length == 0) return;
            App.State.Settings.CreateResourcePackFolder(name);
            App.State.Settings.AddResourcePackToFolder(name, row.Key);
            App.State.Settings.Save();
            _activeFolder = name;
            RebuildFolders(); FilterInstalledUi();
        };
        root.Items.Add(neu);
    }

    private static string RpFolder(PackSummary p)
    {
        App.State.Packs.EnsurePackFolder(p.Id, p.Name, p.IsShared);
        var dir = Path.Combine(App.State.Packs.GameDir(p.Id, p.Name), "resourcepacks");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Bump(string dir, string name) => BumpPath(dir, name, isFolder: false);

    /// <summary>A destination path in <paramref name="dir"/> that collides with nothing, for a zip or
    /// for an unpacked pack folder.</summary>
    private static string BumpPath(string dir, string name, bool isFolder)
    {
        var clean = ResourcePackService.SanitizeFileName(Path.GetFileName(name), isFolder);
        var candidate = Path.Combine(dir, clean);
        var stem = isFolder ? clean : Path.GetFileNameWithoutExtension(clean);
        var suffix = isFolder ? "" : ".zip";

        var n = 2;
        while (File.Exists(candidate) || Directory.Exists(candidate))
            candidate = Path.Combine(dir, $"{stem}-{n++}{suffix}");
        return candidate;
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(source, dest, StringComparison.Ordinal));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, dest, StringComparison.Ordinal), overwrite: true);
    }

    // ── drag & drop onto the installed grid ──────────────────────────────────

    private void OnInstalledDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedPacks(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// Dropping pack zips here asks which instance they belong to, then copies them in.
    /// </summary>
    /// <remarks>This grid spans every instance, so unlike the per-instance Resources tab it cannot
    /// guess a destination — the picker is the drop target's missing half, not an extra step.</remarks>
    private async void OnInstalledDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        try
        {
            var files = DroppedPacks(e);
            if (files.Count == 0) return;

            if (!_packs.Any()) _packs = await App.State.Api.ListPacksAsync();
            if (!_packs.Any()) { Fail("Create an instance first."); return; }

            var dlg = new PackPickerDialog(_packs, "Install resource packs",
                files.Count == 1 ? Path.GetFileName(files[0]) : $"{files.Count} packs", "Copy")
            { Owner = _shell };
            if (dlg.ShowDialog() != true || dlg.SelectedPackId is not Guid gid) return;

            var target = _packs.First(z => z.Id == gid);
            Okay($"Copying into {target.Name}…");
            await Task.Run(() =>
            {
                var dir = RpFolder(target);
                foreach (var source in files)
                {
                    var isFolder = Directory.Exists(source);
                    var dest = BumpPath(dir, Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar)), isFolder);
                    if (isFolder) CopyDirectory(source, dest);
                    else File.Copy(source, dest);
                }
            });

            await RefreshInstalledAsync();
            Okay($"Copied {files.Count} pack(s) into {target.Name}.");
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    /// <summary>The resource packs in a drag payload: zips, and folders holding a pack.mcmeta.
    /// Anything else is ignored rather than copied into resourcepacks/.</summary>
    private static List<string> DroppedPacks(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return new();
        return (e.Data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>())
            .Where(f => (File.Exists(f) && f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        || (Directory.Exists(f) && File.Exists(Path.Combine(f, "pack.mcmeta"))))
            .ToList();
    }

    // ── browse-row context menu ──────────────────────────────────────────────

    private static RpBrowseRowVm? BrowseRowFrom(object sender)
    {
        DependencyObject? p = sender as DependencyObject;
        while (p is MenuItem m) p = m.Parent as MenuItem ?? m.Parent;
        return p is ContextMenu { PlacementTarget: FrameworkElement fx } && fx.DataContext is RpBrowseRowVm row
            ? row
            : null;
    }

    /// <summary>Shows only what this row can actually do: hosting actions for a hosted pack, and
    /// delete only for a pack this user owns.</summary>
    private void OnBrowseMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        var row = (menu.PlacementTarget as FrameworkElement)?.DataContext as RpBrowseRowVm;
        if (row is null) { menu.IsOpen = false; return; }

        var hosted = row.Hosted;
        var isOwner = hosted is not null && hosted.OwnerId == App.State.Settings.UserId;

        foreach (var item in menu.Items.OfType<FrameworkElement>())
        {
            var visible = item.Name switch
            {
                "BrowseCtxManage" => isOwner,
                "BrowseCtxOpenPage" => row.External is not null,
                "BrowseCtxCopyUrl" => row.External is not null,
                "BrowseCtxDelete" or "BrowseCtxDeleteSeparator" => isOwner,
                _ => true
            };
            item.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private async void OnCtxBrowseInstall(object sender, RoutedEventArgs e)
    {
        try
        {
            if (BrowseRowFrom(sender) is RpBrowseRowVm row) await InstallRowAsync(row);
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private void OnCtxBrowseManage(object sender, RoutedEventArgs e)
    {
        if (BrowseRowFrom(sender)?.Hosted is HostedResourcePackSummary hs)
            _shell.OpenResourcePackDetail(hs.Id, hs.Name);
    }

    private void OnCtxBrowseOpenPage(object sender, RoutedEventArgs e)
    {
        if (BrowseRowFrom(sender)?.External is ModSummary m)
            Process.Start(new ProcessStartInfo(BuildUrl(m)) { UseShellExecute = true });
    }

    private void OnCtxBrowseCopyUrl(object sender, RoutedEventArgs e)
    {
        if (BrowseRowFrom(sender)?.External is ModSummary m && Services.ClipboardHelper.TrySetText(BuildUrl(m)))
            Okay("Project URL copied.");
    }

    /// <summary>
    /// Deletes a hosted resource pack the signed-in user owns.
    /// </summary>
    /// <remarks>Also clears the link from any installed zip that pointed at it, so the local pack
    /// does not keep claiming to be published to something that no longer exists.</remarks>
    private async void OnCtxBrowseDelete(object sender, RoutedEventArgs e)
    {
        try
        {
            if (BrowseRowFrom(sender)?.Hosted is not HostedResourcePackSummary hs) return;
            if (hs.OwnerId != App.State.Settings.UserId) return;

            if (!await AppDialog.ConfirmAsync(_shell,
                    "Delete hosted resource pack",
                    $"Delete '{hs.Name}' and all of its uploaded versions from hosting?\n\n"
                    + "Anyone you shared it with loses access. Copies already installed in an instance are untouched.",
                    "Delete", "Cancel", danger: true))
                return;

            await App.State.Api.DeleteResourcePackAsync(hs.Id);

            var relinked = false;
            foreach (var entry in App.State.Settings.ResourcePacks.Values.Where(v => v.HostedResourcePackId == hs.Id))
            {
                entry.HostedResourcePackId = null;
                relinked = true;
            }
            if (relinked) App.State.Settings.Save();

            await BrowseRestartAsync();
            Okay($"Deleted '{hs.Name}'.");
        }
        catch (Exception ex) { Fail("Delete failed: " + ex.Message); }
    }

    // Browse --------------------------------------------------------------------

    private async Task BrowseRestartAsync()
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var tk = _cts.Token;
        _off = 0;
        _more = true;
        _browseRows.Clear();
        DetailPanel.Visibility = Visibility.Collapsed;
        DetailPlaceholder.Visibility = Visibility.Visible;

        LoadingBar.Visibility = Visibility.Visible;
        await DrainAsync(tk);
        LoadingBar.Visibility = Visibility.Collapsed;

        BrowseEmptyBanner();
        CountLabel.Text = $"{_browseRows.Count}";
    }

    private async void OnResultsScroll(object sender, ScrollChangedEventArgs e)
    {
        if (_busy || !_more || _activeChipVm?.Kind == BrowseKind.Installed) return;
        if (e.OriginalSource is ScrollViewer sv
            && sv.VerticalOffset + sv.ViewportHeight >= sv.ExtentHeight - 200)
            await DrainAsync(_cts.Token);
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        if (_activeChipVm == null ||
            _activeChipVm.Kind == BrowseKind.Installed ||
            !_more ||
            _browseRows.Count >= BrowseCap)
            return;

        LoadingBar.Visibility = Visibility.Visible;
        _busy = true;
        var added = 0;

        try
        {
            added = await (_activeChipVm.Kind switch
            {
                BrowseKind.CurseForge => AppendCf(ct),
                BrowseKind.Modrinth => AppendMr(ct),
                BrowseKind.CloudPublic => AppendHosted(ResourcePackBrowseSource.Public, null, ct),
                BrowseKind.CloudPersonal => AppendHosted(ResourcePackBrowseSource.Personal, null, ct),
                BrowseKind.CloudShared => AppendHosted(ResourcePackBrowseSource.Shared, null, ct),
                BrowseKind.CloudTeam => AppendHosted(ResourcePackBrowseSource.Team, _activeChipVm.TeamId, ct),
                _ => Task.FromResult(0)
            });

            // Advance the paging offset, or every scroll-to-bottom re-requests page one and the list
            // fills with duplicates of the first 25 results.
            _off += added;

            // Newly-arrived rows have to be checked against the installed library too, or only the
            // first page ever says "Installed".
            MarkInstalledBrowseRows();
            BrowseEmptyBanner();
        }
        catch (OperationCanceledException)
        {
            /* cancelled */
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
            added = 0;
            _more = false;
        }
        finally { _busy = false; LoadingBar.Visibility = Visibility.Collapsed; }
    }

    private async Task<int> AppendCf(CancellationToken ct)
    {
        var chunk = await App.State.CurseForge.SearchAsync(_search, _filterMc, null, PageSize, _off,
            CurseForgeService.ClassIdResourcePacks, ct: ct);
        foreach (var m in chunk) _browseRows.Add(RpBrowseRowVm.FromExternal(m, CfBrush()));
        _more = chunk.Count == PageSize;
        return chunk.Count;
    }

    private async Task<int> AppendMr(CancellationToken ct)
    {
        var chunk = await App.State.Modrinth.SearchAsync(_search, _filterMc, null,
            limit: PageSize, offset: _off, projectType: "resourcepack", ct: ct);
        foreach (var m in chunk) _browseRows.Add(RpBrowseRowVm.FromExternal(m, MrBrush()));
        _more = chunk.Count == PageSize;
        return chunk.Count;
    }

    private async Task<int> AppendHosted(ResourcePackBrowseSource src, Guid? team, CancellationToken ct)
    {
        var pg = await App.State.Api.BrowseResourcePacksAsync(src, team, _search, _filterMc,
            offset: _off, limit: PageSize, ct: ct);

        Brush soft = (Brush)FindResource("AccentSoftBrush"), fore = (Brush)FindResource("AccentBrush");

        foreach (var item in pg.Items)
            _browseRows.Add(RpBrowseRowVm.FromHosted(item, soft, fore));

        var next = pg.Offset + pg.Items.Count;
        _more = next < pg.Total;

        return pg.Items.Count;
    }

    private void BrowseEmptyBanner() =>
        EmptyState.Visibility = !_browseRows.Any() && !_more ? Visibility.Visible : Visibility.Collapsed;

    private static Brush CfBrush() => new SolidColorBrush(Color.FromRgb(0xF1, 0x65, 0x36));

    private static Brush MrBrush() => new SolidColorBrush(Color.FromRgb(0x1B, 0xD9, 0x6A));

    private async void OnResultSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsList.SelectedItem is not RpBrowseRowVm row) return;

        DetailPanel.Visibility = Visibility.Visible;
        DetailPlaceholder.Visibility = Visibility.Collapsed;

        ManageHostedButton.Visibility = Visibility.Collapsed;
        OpenProjectButton.Visibility = Visibility.Collapsed;
        ShowOverview("Loading…");
        ScreenshotsEmptyText.Text = "No previews yet.";
        ScreenshotList.ItemsSource = null;
        ScreenshotsEmptyText.Visibility = Visibility.Visible;
        VersionsGrid.ItemsSource = null;

        ModNameLabel.Text = row.Name;
        ModMetaLabel.Text = row.MetaLabel;
        SelectedIconFallback.Text = row.Initial;
        Thumb(row.IconUrl);
        ManageHostedButton.IsEnabled = true;

        try
        {
            if (row.External is ModSummary ext)
                await FillExternal(ext);
            else if (row.Hosted is HostedResourcePackSummary hz)
                await FillHostedAsync(hz);
        }
        catch (Exception ex) { ShowOverview(ex.Message); }
        ModTabs.SelectedIndex = 0;
    }

    private async Task FillExternal(ModSummary mod)
    {
        _selHostedDetail = null;
        _selExternal = mod;

        ShowAllVersionsBox.Visibility = Visibility.Visible;

        var detail = mod.Source == ModSource.CurseForge
            ? await App.State.CurseForge.GetProjectDetailAsync(int.TryParse(mod.Id, out var cid) ? cid : 0)
            : await App.State.Modrinth.GetProjectDetailAsync(mod.Id);

        ShowOverview(detail.Description ?? mod.Description, detail.IsMarkdown);
        WireScreens(detail.Screenshots);
        _projectUrl = BuildUrl(mod);

        ToggleLink(ProjectPageButton, detail.Links.WebsiteUrl ?? _projectUrl);
        ToggleLink(IssuesButton, detail.Links.IssuesUrl);
        ToggleLink(SourceButton, detail.Links.SourceUrl);
        ToggleLink(WikiButton, detail.Links.WikiUrl);
        ToggleLink(DiscordButton, detail.Links.DiscordUrl);

        ToggleLinksEmpty();

        OpenProjectButton.Visibility = Visibility.Visible;

        IEnumerable<ModVersion> vers =
            mod.Source == ModSource.CurseForge && int.TryParse(mod.Id, out var cfMod)
                ? await App.State.CurseForge.GetVersionsAsync(cfMod)
                : await App.State.Modrinth.GetVersionsAsync(mod.Id, mcVersion: null, loader: null);

        _cachedExternalVers.Clear(); _cachedExternalVers.AddRange(vers);
        ReloadExternalVersionsGrid();
        InstallSelectedButton.IsEnabled = VersionsGrid.ItemsSource != null;
    }

    private async Task FillHostedAsync(HostedResourcePackSummary pack)
    {
        _selExternal = null;

        ShowAllVersionsBox.Visibility = Visibility.Collapsed;

        var detail = await App.State.Api.GetResourcePackAsync(pack.Id);

        _selHostedDetail = detail;

        ShowOverview(detail.Description ?? detail.Summary, isMarkdown: true);
        ScreenshotsEmptyText.Text = "Hosted packs do not expose gallery screenshots here.";
        ScreenshotList.ItemsSource = Array.Empty<object>();
        ScreenshotsEmptyText.Visibility = Visibility.Visible;

        foreach (var btn in new[] { ProjectPageButton, IssuesButton, SourceButton, WikiButton, DiscordButton })
        {
            btn.Visibility = Visibility.Collapsed; btn.Tag = "";
        }

        VersionsGrid.ItemsSource = detail.Versions
            .OrderByDescending(z => z.PublishedAt)
            .Select(VersionVm.FromHosted)
            .ToList();

        ManageHostedButton.Visibility =
            App.State.Settings.UserId == detail.OwnerId ? Visibility.Visible : Visibility.Collapsed;

        InstallSelectedButton.IsEnabled = detail.Versions.Any();
    }

    private void WireScreens(IReadOnlyList<ModMediaItem> imgs)
    {
        ScreenshotList.ItemsSource = imgs.Select(i => new MediaRow(i)).ToList();
        ScreenshotsEmptyText.Visibility = imgs.Any() ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ToggleLink(Button btn, string? url)
    {
        btn.Tag = url ?? "";
        btn.Visibility = string.IsNullOrWhiteSpace(url) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ToggleLinksEmpty()
    {
        LinksEmptyText.Visibility =
            new[] { ProjectPageButton, IssuesButton, SourceButton, WikiButton, DiscordButton }.Any(z => z.Visibility == Visibility.Visible)
                ? Visibility.Collapsed
                : Visibility.Visible;
    }

    private void Thumb(string? url)
    {
        SelectedIconImage.Source = null;
        if (string.IsNullOrWhiteSpace(url)) return;
        try { SelectedIconImage.Source = new BitmapImage(new Uri(url)); }
        catch { /* ignore */ }
    }

    private void ReloadExternalVersionsGrid()
    {
        IEnumerable<ModVersion> filtered = _cachedExternalVers;
        if (ShowAllVersionsBox.IsChecked != true && !string.IsNullOrWhiteSpace(_filterMc))
            filtered = filtered.Where(v => v.GameVersions.Any(g =>
                string.Equals(g, _filterMc, StringComparison.OrdinalIgnoreCase)));

        VersionFilterNote.Text = $"{filtered.Count()} row(s)";
        VersionsGrid.ItemsSource = filtered.OrderByDescending(v => v.DatePublished).Select(VersionVm.FromExternal).ToList();
    }

    private void OnShowAllVersionsChanged(object sender, RoutedEventArgs e)
    {
        if (_selExternal is null)
            return;
        ReloadExternalVersionsGrid();
    }

    private async void OnQuickInstallRp(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RpBrowseRowVm rr })
            await InstallRowAsync(rr);
    }

    private async void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsList.SelectedItem is RpBrowseRowVm row)
            await InstallRowAsync(row);
    }

    private async void OnInstallSelectedRp(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is RpBrowseRowVm row)
            await InstallRowAsync(row);
    }

    private async Task InstallRowAsync(RpBrowseRowVm row)
    {
        try
        {
            if (row.External is ModSummary mx)
                await InstallExternal(mx, null);
            else if (row.Hosted is HostedResourcePackSummary hz)
                await InstallHosted(hz.Id, null);
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private async void OnDownloadVersionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fx || fx.DataContext is not VersionVm vm)
            return;
        try
        {
            if (vm.External is ModVersion mv && _selExternal is ModSummary mz)
                await InstallExternal(mz, mv);

            else if (vm.HostedInfo is HostedResourcePackVersionInfo hv && ResultsList.SelectedItem is RpBrowseRowVm { Hosted: HostedResourcePackSummary hs })
                await InstallHosted(hs.Id, hv);
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private async Task InstallHosted(Guid packId, HostedResourcePackVersionInfo? pinned)
    {
        if (!_packs.Any())
            _packs = await App.State.Api.ListPacksAsync();

        var detail = await App.State.Api.GetResourcePackAsync(packId);

        HostedResourcePackVersionInfo ver = pinned
            ?? detail.Versions.OrderByDescending(z => z.PublishedAt).FirstOrDefault()
            ?? throw new InvalidOperationException("No hosted versions.");

        var candidates = HostedMcMatches(_packs, ver.McVersionsCsv ?? detail.McVersionsCsv).ToList();
        if (!candidates.Any())
            throw new InvalidOperationException("Profile MC version mismatch.");

        var pick = new PackPickerDialog(candidates, "Pick instance", "Installs zipped pack", "") { Owner = _shell };

        if (pick.ShowDialog() != true || pick.SelectedPackId is not Guid gid) return;
        var target = candidates.First(p => p.Id == gid);

        DownloadProgress.Value = 0;
        DownloadProgress.IsIndeterminate = true;
        DownloadProgress.Visibility = Visibility.Visible;

        var tmp = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        await using (var stm = await App.State.Api.DownloadResourcePackVersionAsync(packId, ver.Id))
        await using (var fs = File.Create(tmp))
            await stm.CopyToAsync(fs);

        DownloadProgress.Visibility = Visibility.Collapsed;

        var dest = Bump(RpFolder(target), ver.FileName);
        File.Copy(tmp, dest, overwrite: false);
        try { File.Delete(tmp); } catch { /* best effort */ }

        RecordHostedProvenance(target.Id, Path.GetFileName(dest), detail);

        await RefreshInstalledAsync();
        Okay($"Installed into {target.Name}");
    }

    /// <summary>
    /// Remembers that this zip is a copy of a hosted pack, and names it after that pack.
    /// </summary>
    /// <remarks>A resource pack carries no identity of its own once it is on disk, so unless this is
    /// written at install time the launcher can never tell that the file and the hosted pack are the
    /// same thing — which is what an update check needs.</remarks>
    private static void RecordHostedProvenance(Guid targetPackId, string fileName, HostedResourcePackDetail detail)
    {
        var key = ResourcePackService.Key(targetPackId, fileName);
        App.State.ResourcePacks.LinkHostedResourcePack(key, detail.Id);
        App.State.ResourcePacks.Rename(key, detail.Name);
    }

    private static IEnumerable<PackSummary> HostedMcMatches(List<PackSummary> packs, string? csvOrNull)
    {
        foreach (var p in packs)
        {
            if (p.IsEmpty || string.IsNullOrWhiteSpace(p.MinecraftVersion)) continue;
            if (string.IsNullOrWhiteSpace(csvOrNull)) { yield return p; continue; }
            foreach (var token in csvOrNull.Split(',', StringSplitOptions.TrimEntries))
                if (string.Equals(token, p.MinecraftVersion, StringComparison.OrdinalIgnoreCase))
                {
                    yield return p;
                    break;
                }
        }
    }

    private async Task InstallExternal(ModSummary mod, ModVersion? pinned)
    {
        if (!_packs.Any())
            _packs = await App.State.Api.ListPacksAsync();

        List<ModVersion> vers = pinned is ModVersion dv
            ? new List<ModVersion> { dv }
            : (mod.Source == ModSource.CurseForge && int.TryParse(mod.Id, out var cfmod)
                  ? await App.State.CurseForge.GetVersionsAsync(cfmod)
                  : await App.State.Modrinth.GetVersionsAsync(mod.Id, mcVersion: null, loader: null)).ToList();

        ModVersion choice = pinned
            ?? vers.OrderByDescending(v => v.DatePublished).FirstOrDefault(v => IsRel(v.ReleaseChannel))
            ?? vers.OrderByDescending(v => v.DatePublished).First();

        IEnumerable<PackSummary> cand = PickPacksMc(choice.GameVersions.ToArray()).ToList();
        if (!cand.Any())
            cand = _packs.Where(z => !z.IsEmpty && !string.IsNullOrWhiteSpace(z.MinecraftVersion));
        cand = cand.ToList();
        if (!cand.Any())
            throw new InvalidOperationException("No instances.");

        var pick = new PackPickerDialog((List<PackSummary>)cand!, "Pick", "resourcepacks/", "") { Owner = _shell };

        if (pick.ShowDialog() != true || pick.SelectedPackId is not Guid gid) return;
        var target = cand.First(p => p.Id == gid);

        var filePrimary = choice.Files.First(f => f.IsPrimary) ?? choice.Files.First();
        DownloadProgress.IsIndeterminate = true;
        DownloadProgress.Visibility = Visibility.Visible;

        var tmpPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");

        if (choice.Source != ModSource.CurseForge || !string.IsNullOrWhiteSpace(filePrimary.DownloadUrl))
        {
            await App.State.Modrinth.DownloadFileAsync(filePrimary.DownloadUrl ?? "", tmpPath);
        }
        else
        {
            var splits = choice.Id.Split(':', StringSplitOptions.RemoveEmptyEntries);
            var fileId = splits.Length >= 2 && int.TryParse(splits[1], out var fidParsed) ? fidParsed : throw new InvalidOperationException("Malformed file id.");

            var modIdParsed = splits.Length >= 1 && int.TryParse(splits[0], out var cid0) ? cid0 :
                int.TryParse(mod.Id, out var alt) ? alt : throw new InvalidOperationException("Malformed mod id.");

            var urlMagic = await App.State.CurseForge.GetDownloadUrlAsync(modIdParsed, fileId);

            await App.State.Modrinth.DownloadFileAsync(urlMagic ?? "", tmpPath);
        }

        DownloadProgress.Visibility = Visibility.Collapsed;

        var dest = Bump(RpFolder(target), filePrimary.Filename ?? "rp.zip");
        File.Copy(tmpPath, dest, overwrite: false);
        try { File.Delete(tmpPath); } catch { /* */ }

        // Which listing this file came from, recorded now because nothing inside the zip says so
        // later. Without it an installed pack can never be offered an update.
        var key = ResourcePackService.Key(target.Id, Path.GetFileName(dest));
        App.State.ResourcePacks.SetProvenance(key, mod.Source, mod.Id, choice.Id, choice.VersionNumber);
        App.State.ResourcePacks.Rename(key, mod.Name);

        await RefreshInstalledAsync();
        Okay($"Installed {filePrimary.Filename} → {target.Name}");
    }

    private IEnumerable<PackSummary> PickPacksMc(string[] gvers)
    {
        if (gvers.Length == 0) yield break;

        foreach (var p in _packs.Where(z => !z.IsEmpty))
        {
            if (string.IsNullOrWhiteSpace(p.MinecraftVersion)) continue;
            if (gvers.Any(g => string.Equals(g, p.MinecraftVersion!, StringComparison.OrdinalIgnoreCase)))
                yield return p;
        }
    }

    private static bool IsRel(string ch) =>
        string.Equals(ch, "release", StringComparison.OrdinalIgnoreCase);

    private void OnManageHostedRp(object sender, RoutedEventArgs e)
    {
        if (_selHostedDetail != null)
            _shell.OpenResourcePackDetail(_selHostedDetail.Id, _selHostedDetail.Name);
        else if (ResultsList.SelectedItem is RpBrowseRowVm { Hosted: HostedResourcePackSummary summary })
            _shell.OpenResourcePackDetail(summary.Id, summary.Name);
    }

    private void OnOpenSelectedProject(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_projectUrl))
            Process.Start(new ProcessStartInfo(_projectUrl!) { UseShellExecute = true });
    }

    private void OnOpenLink(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string z } btn && Uri.TryCreate(z, UriKind.Absolute, out _))
            Process.Start(new ProcessStartInfo(z) { UseShellExecute = true });
    }

    private void OnOpenScreenshot(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fx && fx.DataContext is MediaRow row)
            ScreenshotPreviewWindow.ShowFor(Window.GetWindow(this), row.FullImageUrl, row.Title);

        e.Handled = true;
    }

    private static string BuildUrl(ModSummary m)
    {
        var slug = string.IsNullOrWhiteSpace(m.Slug) ? m.Id : m.Slug!;
        return m.Source == ModSource.CurseForge
            ? $"https://www.curseforge.com/minecraft/texture-packs/{slug}"
            : $"https://modrinth.com/resourcepack/{slug}";
    }

    private static string Strip(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        html = Regex.Replace(html, "(?i)<br\\s*/?>", "\n");
        html = Regex.Replace(html, "<[^>]*>", "");

        html = WebUtility.HtmlDecode(html);
        return Regex.Replace(html, "\\s{2,}", " ").Trim();
    }
}

// View models ---------------------------------------------------------------

public enum BrowseKind
{
    Installed = 0,
    Splitter,
    CurseForge,
    Modrinth,
    CloudPublic,
    CloudPersonal,
    CloudShared,
    CloudTeam,
}

public sealed class ResourcePackChipVm
{
    public string Icon { get; init; } = "";
    public string Label { get; init; } = "";
    public BrowseKind Kind { get; init; }
    public Guid? TeamId { get; init; }
    public bool IsDivider { get; init; }
    public Brush Background { get; set; } = Brushes.DarkSeaGreen;
    public Brush BorderColor { get; set; } = Brushes.DarkGoldenrod;
    public Brush Foreground { get; set; } = Brushes.White;
    public FontWeight FontWeight { get; set; } = FontWeights.Normal;
    public Visibility ChipVisibility { get; init; } = Visibility.Visible;
    public Visibility IconVisibility { get; init; } = Visibility.Visible;
    public Cursor CursorHint { get; init; } = Cursors.Hand;
}

/// <summary>One installed resource pack on the card grid.</summary>
/// <remarks>Holds no brushes from the theme dictionary: the only brush here is the cover gradient,
/// which is generated from the pack's own name and so survives a theme change unchanged.</remarks>
public sealed class ResourcePackScannerRowVm
{
    private readonly string _compatSummary;

    public ResourcePackScannerRowVm(ResourcePackInfo rp, ResourcePackMeta? meta = null)
    {
        Info = rp;
        Displays = rp.DisplayName;
        SourcePack = rp.SourcePackName;
        Modified = rp.LastModified;
        Bytes = rp.SizeBytes;

        meta ??= ResourcePackMeta.Empty;
        IconSource = meta.Icon;
        PackDescription = meta.Description;
        PackFormat = meta.PackFormat;

        _compatSummary = rp.CompatibleWithAll
            ? "All instances enabled"
            : $"{Math.Max(1, rp.CompatiblePackIds.Count)} instance(s)";
    }

    public ResourcePackInfo Info { get; }
    public string Key => Info.Key;
    public string Path => Info.FilePath;
    public string FileName => Info.FileName;
    public bool IsFolder => Info.IsFolder;
    public bool IsLocal => Info.IsLocal;
    public DateTimeOffset Modified { get; }
    public string DisplayName => Displays;

    readonly string Displays;
    public readonly string SourcePack;

    public long Bytes { get; }

    /// <summary>pack.png, or null for a pack that ships none — the card falls back to its letter.</summary>
    public ImageSource? IconSource { get; }

    /// <summary>The description from pack.mcmeta, flattened to plain text.</summary>
    public string? PackDescription { get; }

    public int? PackFormat { get; }

    /// <summary>True when this pack is listed in its instance's options.txt, i.e. the game loads it.</summary>
    public bool Enabled => Info.Enabled;

    public string EnabledLabel => Info.Priority > 0 ? $"ON · #{Info.Priority}" : "ON";

    public Visibility EnabledVisibility => Enabled ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>"FOLDER" for an unpacked pack, "LOCAL" for one in the unsynced folder, else the
    /// plain label — the cover has room for exactly one of these.</summary>
    public string KindLabel => IsFolder ? "PACK FOLDER" : IsLocal ? "LOCAL" : "RESOURCE PACK";

    public string ToggleMenuHeader => Enabled
        ? $"Turn off in {SourcePack}"
        : $"Turn on in {SourcePack}";

    public string MetaLabel =>
        $"{Modified.LocalDateTime:g} • {PrettySize()}";

    public string CompatibilityLabel => _compatSummary;

    public string SourcePackName => SourcePack;

    public string SizeLabel => PrettySize();

    /// <summary>Everything the card cannot fit: what the pack says about itself, and where it sits.</summary>
    public string CardTooltip
    {
        get
        {
            var lines = new List<string> { DisplayName, FileName };
            lines.Add(Enabled
                ? $"On in {SourcePack} · priority {Info.Priority} (higher overrides lower)"
                : $"Off in {SourcePack} — the game is not loading it");
            if (PackFormat is int pf) lines.Add($"pack_format {pf}");
            if (!string.IsNullOrWhiteSpace(PackDescription)) lines.Add(PackDescription!);
            return string.Join("\n", lines);
        }
    }

    string PrettySize()
    {
        double b = Bytes;
        string unit = "B";

        if (b > 1024) { b /= 1024; unit = "KB"; }

        if (b > 1024) { b /= 1024; unit = "MB"; }

        return $"{b:0.#} {unit}";
    }

    public Brush CoverBackground
    {
        get
        {
            static uint Mix(string txt)
            {
                uint hash = 0;
                foreach (var ch in txt) hash = hash * 31 + char.ToLowerInvariant(ch);
                return hash;
            }

            (Color start, Color end)[] combos =
            {
                (Color.FromRgb(0x6C, 0x5C, 0xE7), Color.FromRgb(0x3B, 0x3B, 0x73)),
                (Color.FromRgb(0x0E, 0xA5, 0xE9), Color.FromRgb(0x06, 0x4F, 0x6D)),
                (Color.FromRgb(0xF9, 0x73, 0x16), Color.FromRgb(0x93, 0x45, 0x0F)),
                (Color.FromRgb(0x14, 0xB8, 0xA6), Color.FromRgb(0x0B, 0x6F, 0x65)),
                (Color.FromRgb(0xEC, 0x48, 0x91), Color.FromRgb(0x8E, 0x29, 0x5A)),
                (Color.FromRgb(0x8B, 0x5C, 0xF6), Color.FromRgb(0x4F, 0x2C, 0x93)),
                (Color.FromRgb(0xF5, 0x9E, 0x0B), Color.FromRgb(0xAE, 0x6F, 0x0B)),
                (Color.FromRgb(0x10, 0xB9, 0x81), Color.FromRgb(0x06, 0x6F, 0x5B)),
            };

            var pair = combos[Mix(DisplayName) % combos.Length];

            var linear = new LinearGradientBrush(pair.start, pair.end, new Point(0, 0), new Point(1, 1));
            linear.Freeze();
            return linear;
        }
    }

    public string Initial
    {
        get
        {
            var s = (DisplayName ?? "?").Trim();
            return string.IsNullOrEmpty(s) ? "?" : char.ToUpperInvariant(s[0]).ToString();
        }
    }
}

public sealed class RpBrowseRowVm
{
    private RpBrowseRowVm() { }

    public ModSummary? External { get; private init; }
    public HostedResourcePackSummary? Hosted { get; private init; }

    public string Name { get; init; } = "";
    public string Summary { get; init; } = "";
    public string MetaLabel { get; init; } = "";

    public string Initial { get; init; } = "?";
    public string? IconUrl { get; init; }
    public string SourceBadge { get; init; } = "";

    public Brush SourceBadgeBackground { get; init; } = Brushes.Black;
    public Brush SourceBadgeForeground { get; init; } = Brushes.White;

    /// <summary>True once a pack of this name is already sitting in one of the instances.</summary>
    /// <remarks>Clicking Install a second time used to write Name-2.zip and leave two copies of the
    /// same pack fighting in one stack, with nothing on screen to warn anybody.</remarks>
    public bool IsInstalled { get; private set; }

    /// <summary>Installing again is still allowed — that is how you replace a bad download — so the
    /// button changes what it says rather than switching itself off.</summary>
    public bool IsInstallable => true;

    public string InstallLabel => IsInstalled ? "Installed" : "Install";

    public string InstallTooltip => IsInstalled
        ? "Already installed — click to install another copy"
        : "Install into one of your instances";

    public string RowTooltip => string.Join("\n", new[] { Name, Summary, MetaLabel }
        .Where(x => !string.IsNullOrWhiteSpace(x)));

    /// <summary>Returns true when the flag actually changed, so the caller only refreshes the list
    /// when there is something new to paint.</summary>
    public bool ApplyInstalled(bool installed)
    {
        if (IsInstalled == installed) return false;
        IsInstalled = installed;
        return true;
    }

    public static RpBrowseRowVm FromExternal(ModSummary m, Brush badgeBackground)
        => new()
        {
            External = m,
            Name = m.Name,
            Summary = m.Description ?? "",
            MetaLabel = $"{m.Author} • {m.Source}",
            IconUrl = m.IconUrl,

            Initial = string.IsNullOrWhiteSpace(m.Name) ? "?" : char.ToUpperInvariant(m.Name[0]).ToString(),
            SourceBadge = m.Source == ModSource.CurseForge ? "CurseForge" : "Modrinth",

            SourceBadgeBackground = badgeBackground,

            SourceBadgeForeground = Brushes.White
        };

    public static RpBrowseRowVm FromHosted(HostedResourcePackSummary p, Brush soft, Brush fore)
        => new()
        {
            Hosted = p,
            Name = p.Name,

            Summary = p.Summary ?? "",
            IconUrl = null,
            MetaLabel = $"Hosted • {p.OwnerUsername}",

            Initial = string.IsNullOrWhiteSpace(p.Name) ? "?" : char.ToUpperInvariant(p.Name[0]).ToString(),
            SourceBadge = p.Visibility.ToString(),

            SourceBadgeBackground = soft,
            SourceBadgeForeground = fore
        };
}

public sealed class VersionVm
{
    public string VersionNumber { get; init; } = "";
    public string McVersions { get; init; } = "";
    public string ReleaseChannel { get; init; } = "";
    public string DateLabel { get; init; } = "";
    public string SizeLabel { get; init; } = "";

    public ModVersion? External { get; private init; }
    public HostedResourcePackVersionInfo? HostedInfo { get; private init; }

    private static string PrettyBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        var kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:0.#} KB";
        return $"{kb / 1024.0:0.#} MB";
    }

    public static VersionVm FromHosted(HostedResourcePackVersionInfo hz) => new()
    {
        HostedInfo = hz,
        VersionNumber = hz.VersionString,
        McVersions = hz.McVersionsCsv ?? "",
        ReleaseChannel = hz.ReleaseChannel,
        DateLabel = hz.PublishedAt.ToString("yyyy-MM-dd"),
        SizeLabel = PrettyBytes(hz.FileSize)
    };

    public static VersionVm FromExternal(ModVersion v)
    {
        var file = v.Files.FirstOrDefault(f => f.IsPrimary) ?? v.Files.FirstOrDefault();
        var sizeTxt = file switch
        {
            null => "",
            { Size: > 0 } f => PrettyBytes(f.Size),
            { } f => f.Filename
        };

        return new()
        {
            External = v,
            VersionNumber = v.VersionNumber,
            McVersions = string.Join(", ", v.GameVersions),
            ReleaseChannel = v.ReleaseChannel,
            DateLabel = v.DatePublished.LocalDateTime.ToString("yyyy-MM-dd"),
            SizeLabel = sizeTxt
        };
    }
}
