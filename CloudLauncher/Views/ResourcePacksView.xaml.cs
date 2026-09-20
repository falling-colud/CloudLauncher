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
        };
        Unloaded += (_, _) =>
        {
            if (Window.GetWindow(this) is Window w)
                w.PreviewKeyDown -= GlobalKeys;
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

            Directory.CreateDirectory(RpFolder(target));
            var dest = Bump(RpFolder(target), Path.GetFileName(ofd.FileName));
            File.Copy(ofd.FileName, dest, overwrite: false);
            Okay($"Copied into {target.Name}");
            await RefreshInstalledAsync();
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

    private void Okay(string m)
    {
        StatusLabel.Foreground = (Brush)FindResource("AccentBrush");
        StatusLabel.Text = m;
    }

    private void Fail(string m)
    {
        StatusLabel.Foreground = (Brush)FindResource("DangerBrush");
        StatusLabel.Text = m;
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

        if (e.Key != Key.F5) return;

        e.Handled = true;
        if (_activeChipVm?.Kind == BrowseKind.Installed)
            await RefreshInstalledAsync();
        else
            await BrowseRestartAsync();
    }

    // Installed library ---------------------------------------------------------

    private async Task RefreshInstalledAsync()
    {
        try
        {
            _packs = await App.State.Api.ListPacksAsync();

            var discovered = App.State.ResourcePacks.ScanAll(_packs);
            _scanRows.Clear();
            foreach (var pack in discovered)
                _scanRows.Add(new ResourcePackScannerRowVm(pack, _packs));

            RebuildFolders();
            FilterInstalledUi();
            Okay("");
            CountLabel.Text = $"{discovered.Count} scanned";
        }
        catch (Exception ex) { Fail(ex.Message); }
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
            seq = seq.Where(r =>
                r.DisplayName.ToLowerInvariant().Contains(q)
                || r.SourcePack.ToLowerInvariant().Contains(q));

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

    private void OnFolderDelete(object sender, RoutedEventArgs e)
    {
        var row = FolderCtx(sender); if (row == null || string.IsNullOrEmpty(row.Id)) return;

        if (MessageBox.Show(_shell, $"Delete folder '{row.Id}'?",
                "Delete folder", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;

        App.State.Settings.DeleteResourcePackFolder(row.Id);
        if (_activeFolder == row.Id) _activeFolder = null;
        App.State.Settings.Save();

        RebuildFolders(); FilterInstalledUi();
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
        if (sender is FrameworkElement { DataContext: ResourcePackScannerRowVm rr })
            _shell.OpenLocalResourcePackDetail(rr.Key, rr.DisplayName);
    }

    private void OnCtxEdit(object sender, RoutedEventArgs e)
    {
        if (ScannerFromCtx(sender) is ResourcePackScannerRowVm r)
            _shell.OpenLocalResourcePackDetail(r.Key, r.DisplayName);
    }

    private void OnCtxReveal(object sender, RoutedEventArgs e)
    {
        if (ScannerFromCtx(sender)?.Path is string p && File.Exists(p))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,{p}") { UseShellExecute = true });
    }

    private void OnCtxRename(object sender, RoutedEventArgs e) => OnCtxEdit(sender, e);

    private void OnCtxCopyInstance(object sender, RoutedEventArgs e)
    {
        var row = ScannerFromCtx(sender); if (row == null) return;
        var allow = App.State.ResourcePacks.CompatiblePacks(row.Info, _packs);
        if (!allow.Any()) { Fail("No compatible packs flagged."); return; }

        var pick = new PackPickerDialog(allow, "Destination", "", "Pick") { Owner = _shell };
        if (pick.ShowDialog() != true || pick.SelectedPackId is not Guid gid) return;
        try
        {
            var tgt = allow.First(pk => pk.Id == gid);
            File.Copy(row.Path!, Bump(RpFolder(tgt), Path.GetFileName(row.Path)!), overwrite: false);
            Okay($"Copied to {tgt.Name}");
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private void OnCtxCopyKey(object sender, RoutedEventArgs e)
    {
        if (ScannerFromCtx(sender)?.Key is string ky)
        {
            if (Services.ClipboardHelper.TrySetText(ky))
                Okay("Copied key.");
        }
    }

    private async void OnCtxDelete(object sender, RoutedEventArgs e)
    {
        var row = ScannerFromCtx(sender); if (row == null) return;
        if (MessageBox.Show(_shell, $"Permanently delete {row.DisplayName}?", "Delete resource pack", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;
        try
        {
            File.Delete(row.Path!);
            App.State.Settings.ResourcePacks.Remove(row.Key);
            foreach (var set in App.State.Settings.ResourcePackFolders.Values) set.Remove(row.Key);
            App.State.Settings.Save();
            await RefreshInstalledAsync();
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

    private static string Bump(string dir, string name)
    {
        var clean = string.IsNullOrWhiteSpace(Path.GetExtension(name)) ? name + ".zip" : Path.GetFileName(name);
        var candidate = Path.Combine(dir, Path.GetFileName(clean));
        var stem = Path.GetFileNameWithoutExtension(candidate);
        var suffix = ".zip";
        var n = 2;
        while (File.Exists(candidate)) candidate = Path.Combine(dir, $"{stem}-{n++}{suffix}");
        return candidate;
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

        Directory.CreateDirectory(RpFolder(target));

        File.Copy(tmp, Bump(RpFolder(target), ver.FileName), overwrite: false);
        try { File.Delete(tmp); } catch { /* best effort */ }
        Okay($"Installed into {target.Name}");
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

        Directory.CreateDirectory(RpFolder(target));

        File.Copy(tmpPath, Bump(RpFolder(target), filePrimary.Filename ?? "rp.zip"), overwrite: false);
        try { File.Delete(tmpPath); } catch { /* */ }

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

public sealed class ResourcePackScannerRowVm
{
    private readonly string _compatSummary;

    public ResourcePackScannerRowVm(ResourcePackInfo rp, IEnumerable<PackSummary>? _ = null)
    {
        Info = rp;
        Displays = rp.DisplayName;
        SourcePack = rp.SourcePackName;
        Modified = rp.LastModified;
        Bytes = rp.SizeBytes;

        _compatSummary = rp.CompatibleWithAll
            ? "All instances enabled"
            : $"{Math.Max(1, rp.CompatiblePackIds.Count)} instance(s)";
    }

    public ResourcePackInfo Info { get; }
    public string Key => Info.Key;
    public string Path => Info.FilePath;
    public DateTimeOffset Modified { get; }
    public string DisplayName => Displays;

    readonly string Displays;
    public readonly string SourcePack;

    public long Bytes { get; }

    public string MetaLabel =>
        $"{Modified.LocalDateTime:g} • {PrettySize()}";

    public string CompatibilityLabel => _compatSummary;

    public string SourcePackName => SourcePack;

    public string SizeLabel => PrettySize();

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

    public bool IsInstallable => true;

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
