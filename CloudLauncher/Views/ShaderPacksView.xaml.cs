using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CloudLauncher.Services;
using CloudLauncher.Shared;
using Microsoft.Win32;

namespace CloudLauncher.Views;

/// <summary>
/// The Shaders page: every shader pack installed across the user's instances, which one each instance
/// is set to load, and a search over Modrinth and CurseForge to get more.
/// </summary>
/// <remarks>
/// Shaders had no home in the launcher — they are neither mods nor resource packs, so the only way to
/// add one was to find the instance folder in Explorer. This page also does the thing that actually
/// needs the launcher: switching the active pack writes the shader loader's config, so you can tell
/// what is enabled (and change it) without starting the game.
///
/// Two structural points worth knowing before editing this file:
/// <list type="bullet">
/// <item>The disk scan is separated from the filter. <see cref="ReloadAsync"/> walks every instance's
/// shaderpacks folder on a background thread and fills <see cref="_all"/>; <see cref="ApplyFilter"/>
/// only ever works in memory. Search, folder and sort changes call the second one — the page used to
/// re-walk the disk on every keystroke.</item>
/// <item>Nothing installs into a guessed instance. <see cref="ResolveTargetPack"/> asks, and the
/// answer is shown in the UI before the click that uses it.</item>
/// </list>
/// </remarks>
public partial class ShaderPacksView : Page
{
    private readonly MainWindow _shell;
    private readonly ObservableCollection<ShaderRow> _rows = new();
    private readonly ObservableCollection<ShaderStoreRow> _storeRows = new();
    private readonly ObservableCollection<ShaderFolderChip> _folderChips = new();

    /// <summary>Every shader found by the last disk scan, unfiltered. The list the user sees is a
    /// projection of this.</summary>
    private readonly List<ShaderPackInfo> _all = new();

    /// <summary>Loader per instance from the same scan, so the warning banner costs no extra IO.</summary>
    private readonly Dictionary<Guid, ShaderLoader> _loaders = new();

    private readonly List<ModVersion> _cachedVersions = new();

    private List<PackSummary> _packs = new();
    private CancellationTokenSource _searchCts = new();
    private CancellationTokenSource? _installCts;

    private ShaderSortMode _sortMode;
    private string? _activeFolder;

    /// <summary>The instance picked the last time one had to be asked for. Reused for the rest of the
    /// session so the question is asked once, not once per install — and always displayed.</summary>
    private Guid? _lastTargetPackId;

    private ShaderStoreRow? _selectedStoreRow;
    private string? _projectUrl;

    private int _off;
    private bool _modrinthDone;
    private bool _curseDone;
    private bool _busy;
    private bool _loading;
    private bool _suppressFilter;

    private const int PageSize = 25;
    private const int BrowseCap = 200;

    /// <summary>Index of the Overview tab, whose WebView2 has to be hidden when it is not showing.</summary>
    private const int OverviewTabIndex = 1;

    public ShaderPacksView(MainWindow shell)
    {
        InitializeComponent();
        _shell = shell;
        _sortMode = App.State.Settings.ShaderSortMode;
        ShaderList.ItemsSource = _rows;
        StoreResults.ItemsSource = _storeRows;
        FolderStrip.ItemsSource = _folderChips;
        DetailTabs.SelectionChanged += OnDetailTabsChanged;
        OverviewHost.Visibility = Visibility.Collapsed;
        OverviewBrowser.Visibility = Visibility.Collapsed;

        Loaded += async (_, _) =>
        {
            if (Window.GetWindow(this) is { } w) w.PreviewKeyDown += GlobalKeys;
            await LoadAsync();
        };
        Unloaded += (_, _) =>
        {
            if (Window.GetWindow(this) is { } w) w.PreviewKeyDown -= GlobalKeys;
            _searchCts.Cancel();
            _installCts?.Cancel();
        };
    }

    // ── status line ──────────────────────────────────────────────────────────

    private void Okay(string message)
    {
        StatusLabel.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        StatusLabel.Text = message;
    }

    private void Note(string message)
    {
        StatusLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        StatusLabel.Text = message;
    }

    private void Fail(string message)
    {
        StatusLabel.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        StatusLabel.Text = message;
    }

    // ── loading ──────────────────────────────────────────────────────────────

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        Note("Loading…");
        try
        {
            _packs = await App.State.Api.ListPacksAsync();
            _packs = _packs.Where(p => !App.State.Settings.IsPackHidden(p.Id)).ToList();
            RebuildPackFilter();
            await ReloadAsync();
            Note(App.State.Api.PackListStale is { Length: > 0 } why
                ? $"Showing your last known instances — the server is not answering ({why})."
                : "");
        }
        catch (Exception ex) { Fail(ex.Message); }
        finally { _loading = false; }
    }

    /// <summary>
    /// Re-walks every instance's shaderpacks folder off the UI thread, then re-applies the filter.
    /// </summary>
    /// <remarks>
    /// The scan sums the size of every file in an unpacked shader folder, which for a pack like
    /// Complementary means thousands of stats — on the UI thread that is a visible freeze, and it used
    /// to happen on every keystroke in the search box. Call this after something changed on disk;
    /// call <see cref="ApplyFilter"/> for anything else.
    /// </remarks>
    private async Task ReloadAsync()
    {
        var packs = _packs.ToList();
        try
        {
            var (found, loaders) = await Task.Run(() =>
            {
                var shaders = App.State.Shaders.ScanAll(packs);
                var map = new Dictionary<Guid, ShaderLoader>();
                foreach (var p in packs)
                {
                    try { map[p.Id] = App.State.Shaders.DetectLoader(p.Id, p.Name); }
                    catch { map[p.Id] = ShaderLoader.None; }
                }
                return (shaders, map);
            });

            _all.Clear();
            _all.AddRange(found);
            _loaders.Clear();
            foreach (var kv in loaders) _loaders[kv.Key] = kv.Value;

            RebuildFolders();
            ApplyFilter();
        }
        catch (Exception ex) { Fail("Could not read the shaderpacks folders: " + ex.Message); }
    }

    private void RebuildPackFilter()
    {
        var items = new List<PackFilterItem> { new(null, "All instances") };
        items.AddRange(_packs.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                             .Select(p => new PackFilterItem(p.Id, p.Name)));
        var previous = (PackFilterBox.SelectedItem as PackFilterItem)?.Id;
        _suppressFilter = true;
        PackFilterBox.ItemsSource = items;
        PackFilterBox.SelectedItem = items.FirstOrDefault(i => i.Id == previous) ?? items[0];
        _suppressFilter = false;
    }

    private Guid? SelectedPackId => (PackFilterBox.SelectedItem as PackFilterItem)?.Id;

    /// <summary>The instance an install would land in <em>if</em> it happened now, for display only.
    /// Null means "the user will be asked" — which is a state worth showing, not hiding.</summary>
    private Guid? DisplayTargetPackId =>
        SelectedPackId ?? (_packs.Count == 1 ? _packs[0].Id : _lastTargetPackId);

    /// <summary>
    /// The instance to install into, asking when it is not already unambiguous.
    /// </summary>
    /// <remarks>
    /// This replaces a <c>_packs.FirstOrDefault()</c> fallback that installed into whichever instance
    /// happened to sort first, and only admitted to it in a caption after the copy. The order here is:
    /// the instance the filter is on, the only instance there is, the one picked earlier this session,
    /// then ask. Whatever comes back is echoed in the browse panel and the Add button's tooltip.
    /// </remarks>
    private Guid? ResolveTargetPack(string what, string actionText = "Install")
    {
        if (SelectedPackId is { } filtered) return filtered;
        if (_packs.Count == 0)
        {
            Fail("There are no instances to install a shader into — create one first.");
            return null;
        }
        if (_packs.Count == 1) return Remember(_packs[0].Id);
        if (_lastTargetPackId is { } remembered && _packs.Any(p => p.Id == remembered))
            return remembered;

        var picker = new PackPickerDialog(_packs, "Install shader into…",
            $"Pick the instance to install {what} into.", actionText) { Owner = _shell };
        if (picker.ShowDialog() != true || picker.SelectedPackId is not { } id) return null;
        return Remember(id);

        Guid Remember(Guid id)
        {
            _lastTargetPackId = id;
            UpdateTargetLabels();
            return id;
        }
    }

    private string NameOf(Guid? packId) =>
        packId is null ? "an instance you pick" : _packs.FirstOrDefault(p => p.Id == packId)?.Name ?? "the instance";

    private string? TargetMcVersion() =>
        _packs.FirstOrDefault(p => p.Id == DisplayTargetPackId)?.MinecraftVersion;

    private void UpdateTargetLabels()
    {
        var name = NameOf(DisplayTargetPackId);
        InstallTargetLabel.Text = $"Installs into {name}";
        AddFromFileButton.ToolTip = $"Copy a shader zip or folder from this PC into {name}";
    }

    // ── filtering, sorting, folders ──────────────────────────────────────────

    private void ApplyFilter()
    {
        var query = SearchBox.Text?.Trim() ?? "";
        IEnumerable<ShaderPackInfo> seq = _all;

        if (SelectedPackId is { } id) seq = seq.Where(s => s.SourcePackId == id);

        if (!string.IsNullOrEmpty(_activeFolder)
            && App.State.Settings.ShaderFolders.TryGetValue(_activeFolder!, out var members))
            seq = seq.Where(s => members.Contains(s.Key));

        if (query.Length > 0)
            seq = seq.Where(s =>
                s.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || s.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || s.SourcePackName.Contains(query, StringComparison.OrdinalIgnoreCase));

        var list = Sorted(seq).ToList();
        _rows.Clear();
        foreach (var shader in list) _rows.Add(new ShaderRow(shader));

        var scanned = SelectedPackId is { } only
            ? _all.Count(s => s.SourcePackId == only)
            : _all.Count;

        // "Nothing here yet" and "nothing matched" want different words and different buttons.
        var filtering = query.Length > 0 || !string.IsNullOrEmpty(_activeFolder);
        EmptyState.Visibility = scanned == 0 && !filtering ? Visibility.Visible : Visibility.Collapsed;
        EmptyFilteredState.Visibility = list.Count == 0 && (scanned > 0 || filtering)
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (EmptyFilteredState.Visibility == Visibility.Visible)
        {
            EmptyFilteredTitle.Text = query.Length > 0
                ? $"Nothing matched “{query}”"
                : $"Nothing in {_activeFolder}";
            EmptyFilteredDetail.Text = query.Length > 0
                ? $"{_all.Count} shader pack(s) are installed — none of them match that."
                : "Right-click a shader and use Add to folder to put one here.";
        }

        var active = list.Count(s => s.IsActive);
        SubLabel.Text = _all.Count == 0
            ? "Shaders installed in your instances. Pick one to make it the active pack."
            : $"{_all.Count} shader pack(s) across {_packs.Count} instance(s) · {active} active here";
        CountLabel.Text = list.Count == _all.Count ? $"{list.Count} shown" : $"{list.Count} of {_all.Count} shown";

        UpdateTargetLabels();
        UpdateLoaderWarning();
    }

    private IEnumerable<ShaderPackInfo> Sorted(IEnumerable<ShaderPackInfo> rows) => _sortMode switch
    {
        ShaderSortMode.Name => rows.OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase),
        ShaderSortMode.Instance => rows.OrderBy(r => r.SourcePackName, StringComparer.CurrentCultureIgnoreCase)
                                       .ThenBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase),
        ShaderSortMode.Size => rows.OrderByDescending(r => r.SizeBytes)
                                   .ThenBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase),
        _ => rows.OrderByDescending(r => r.LastModified)
                 .ThenBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase)
    };

    /// <summary>
    /// Shows the "this instance cannot load a shader pack" banner when that is true.
    /// </summary>
    /// <remarks>
    /// Without a loader, using a shader writes a config file, reports success and changes nothing in
    /// game — the single most confusing thing this page can do. With the filter on one instance the
    /// banner is about that instance; on "All instances" it points at the first instance that has
    /// shaders sitting in it and nothing able to read them, since that is the one already wasted.
    /// </remarks>
    private void UpdateLoaderWarning()
    {
        Guid? blind = null;
        if (SelectedPackId is { } id)
        {
            if (Blind(id)) blind = id;
        }
        else
        {
            foreach (var candidate in _all.Select(s => s.SourcePackId).Distinct())
            {
                if (!Blind(candidate)) continue;
                blind = candidate;
                break;
            }
        }

        bool Blind(Guid p) => _loaders.TryGetValue(p, out var l) && l == ShaderLoader.None;

        if (blind is null)
        {
            LoaderWarning.Visibility = Visibility.Collapsed;
            LoaderWarning.Tag = null;
            return;
        }

        LoaderWarning.Tag = blind;
        LoaderWarningText.Text =
            $"{NameOf(blind)} has no shader loader installed — a shader pack there does nothing until " +
            "Iris (Fabric/NeoForge) or Oculus (Forge) is added to its mods.";
        LoaderWarning.Visibility = Visibility.Visible;
    }

    private async void OnOpenModsForLoader(object sender, RoutedEventArgs e)
    {
        try
        {
            if (LoaderWarning.Tag is not Guid packId) return;
            var detail = await App.State.Api.GetPackAsync(packId);
            _shell.OpenModExplorerForPack(detail);
        }
        catch (Exception ex) { Fail("Could not open that instance's mods: " + ex.Message); }
    }

    private void RebuildFolders()
    {
        // A folder deleted elsewhere (or in a settings file from another machine) must not leave the
        // list filtered to a folder that no longer exists.
        if (_activeFolder is { } stale && !App.State.Settings.ShaderFolders.ContainsKey(stale))
            _activeFolder = null;

        _folderChips.Clear();
        _folderChips.Add(new ShaderFolderChip("", "All", "", _all.Count, _activeFolder is null,
            "Every shader pack, in every instance"));

        foreach (var name in App.State.Settings.ShaderFolders.Keys.OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase))
        {
            var count = App.State.Settings.ShaderFolders[name].Intersect(_all.Select(s => s.Key)).Count();
            _folderChips.Add(new ShaderFolderChip(name, name, "", count, _activeFolder == name,
                $"{count} shader pack(s) in {name}"));
        }
    }

    private void OnFolderChipClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ShaderFolderChip chip }) return;
        _activeFolder = string.IsNullOrEmpty(chip.Id) ? null : chip.Id;
        RebuildFolders();
        ApplyFilter();
    }

    private void OnFolderChipRightClick(object sender, MouseButtonEventArgs e)
    {
        // "All" is not a folder — it has nothing to rename or delete.
        if (sender is FrameworkElement { DataContext: ShaderFolderChip { Id.Length: 0 } }) e.Handled = true;
    }

    private void OnCreateFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new SimpleInputDialog("Create shader folder", "Folder name", "") { Owner = _shell };
        if (dialog.ShowDialog() != true) return;
        var name = (dialog.Result ?? "").Trim();
        if (name.Length == 0) return;
        App.State.Settings.CreateShaderFolder(name);
        App.State.Settings.Save();
        _activeFolder = name;
        RebuildFolders();
        ApplyFilter();
    }

    private void OnFolderRename(object sender, RoutedEventArgs e)
    {
        if (FolderFromMenu(sender) is not { Id.Length: > 0 } chip) return;
        var dialog = new SimpleInputDialog("Rename folder", "Folder name", chip.Id) { Owner = _shell };
        if (dialog.ShowDialog() != true) return;
        var fresh = (dialog.Result ?? "").Trim();
        if (fresh.Length == 0 || fresh == chip.Id) return;

        var map = App.State.Settings.ShaderFolders;
        if (map.ContainsKey(fresh)) { Fail($"There is already a folder called {fresh}."); return; }
        map[fresh] = map[chip.Id];
        map.Remove(chip.Id);
        if (_activeFolder == chip.Id) _activeFolder = fresh;
        App.State.Settings.Save();
        RebuildFolders();
        ApplyFilter();
    }

    private async void OnFolderDelete(object sender, RoutedEventArgs e)
    {
        try
        {
            if (FolderFromMenu(sender) is not { Id.Length: > 0 } chip) return;
            if (!await AppDialog.ConfirmAsync(_shell, "Delete folder",
                    $"Delete the folder “{chip.Id}”?\n\nThe shader packs in it stay on disk — only the grouping goes.",
                    "Delete", "Cancel", danger: true))
                return;
            App.State.Settings.DeleteShaderFolder(chip.Id);
            if (_activeFolder == chip.Id) _activeFolder = null;
            App.State.Settings.Save();
            RebuildFolders();
            ApplyFilter();
            Okay($"Deleted the folder {chip.Id}.");
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private static ShaderFolderChip? FolderFromMenu(object sender)
    {
        if (sender is not MenuItem leaf) return null;
        DependencyObject? p = leaf;
        while (p is MenuItem m) p = m.Parent as MenuItem ?? m.Parent;
        return p is ContextMenu { PlacementTarget: FrameworkElement fx } && fx.DataContext is ShaderFolderChip c
            ? c
            : null;
    }

    // ── sort menu ────────────────────────────────────────────────────────────

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
        if (!Enum.TryParse<ShaderSortMode>(tag, out var parsed)) return;
        _sortMode = parsed;
        App.State.Settings.ShaderSortMode = parsed;
        App.State.Settings.Save();
        SyncSortChecks();
        ApplyFilter();
    }

    private void SyncSortChecks()
    {
        SortRecentMenuItem.IsChecked = _sortMode == ShaderSortMode.RecentlyAdded;
        SortNameMenuItem.IsChecked = _sortMode == ShaderSortMode.Name;
        SortSizeMenuItem.IsChecked = _sortMode == ShaderSortMode.Size;
        SortInstanceMenuItem.IsChecked = _sortMode == ShaderSortMode.Instance;
    }

    // ── toolbar / keyboard ───────────────────────────────────────────────────

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        try { await LoadAsync(); }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private void OnPackFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFilter) return;
        ApplyFilter();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || SearchBox.Text.Length == 0) return;
        SearchBox.Clear();
        e.Handled = true;
    }

    private void OnClearFilters(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        _activeFolder = null;
        if (PackFilterBox.Items.Count > 0) PackFilterBox.SelectedIndex = 0;
        RebuildFolders();
        ApplyFilter();
    }

    /// <summary>Selection only drives what the keyboard acts on, and the count in the status line
    /// when more than one row is picked — the row's own buttons carry the actions.</summary>
    private void OnShaderSelected(object sender, SelectionChangedEventArgs e)
    {
        var count = ShaderList.SelectedItems.Count;
        if (count > 1) Note($"{count} shader packs selected — right-click for Copy to instance… or Delete.");
    }

    private void OnShaderDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ShaderList.SelectedItem is ShaderRow row) Apply(row);
    }

    private async void GlobalKeys(object sender, KeyEventArgs e)
    {
        try
        {
            if (!IsVisible) return;

            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F5)
            {
                e.Handled = true;
                await LoadAsync();
                return;
            }
            // Only take Delete/F2/Escape when the list itself has focus: the same keys belong to the
            // search box and the store query box while the user is typing in them.
            if (!ShaderList.IsKeyboardFocusWithin) return;

            switch (e.Key)
            {
                case Key.Delete when ShaderList.SelectedItems.Count > 0:
                    e.Handled = true;
                    await DeleteRowsAsync(SelectedRows(null));
                    break;
                case Key.F2 when ShaderList.SelectedItem is ShaderRow rename:
                    e.Handled = true;
                    RenameRow(rename);
                    break;
                case Key.Enter when ShaderList.SelectedItem is ShaderRow use:
                    e.Handled = true;
                    Apply(use);
                    break;
                case Key.Escape when SearchBox.Text.Length > 0:
                    e.Handled = true;
                    SearchBox.Clear();
                    break;
            }
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    // ── installed shader actions ─────────────────────────────────────────────

    /// <summary>The rows an action applies to: the whole selection when the clicked row is part of it,
    /// otherwise just the clicked row — the convention every file manager uses.</summary>
    private List<ShaderRow> SelectedRows(ShaderRow? clicked)
    {
        var selected = ShaderList.SelectedItems.OfType<ShaderRow>().ToList();
        if (clicked is null) return selected;
        return selected.Count > 1 && selected.Contains(clicked) ? selected : new List<ShaderRow> { clicked };
    }

    private static ShaderRow? RowFromMenu(object sender)
    {
        if (sender is not MenuItem leaf) return null;
        DependencyObject? p = leaf;
        while (p is MenuItem m) p = m.Parent as MenuItem ?? m.Parent;
        return p is ContextMenu { PlacementTarget: FrameworkElement fx } && fx.DataContext is ShaderRow r
            ? r
            : null;
    }

    private void Apply(ShaderRow row)
    {
        try
        {
            var turnOff = row.Shader.IsActive;
            App.State.Shaders.SetActiveShader(row.Shader.SourcePackId, turnOff ? null : row.Shader.FileName);

            var loader = _loaders.TryGetValue(row.Shader.SourcePackId, out var l) ? l : ShaderLoader.None;
            var caveat = loader == ShaderLoader.None
                ? $" {row.Shader.SourcePackName} still has no shader loader, so nothing will change in game yet."
                : "";

            if (turnOff) Okay($"Shaders turned off for {row.Shader.SourcePackName}.");
            else Okay($"{row.Shader.DisplayName} is now the shader pack for {row.Shader.SourcePackName}. " +
                      "It applies the next time that instance starts." + caveat);
            RefreshRowsInPlace(row.Shader.SourcePackId);
        }
        catch (Exception ex) { Fail("Could not change the shader: " + ex.Message); }
    }

    /// <summary>Re-reads which pack an instance is set to load and rebuilds just those rows. Cheaper
    /// and far less jarring than a whole re-scan after a one-line config write.</summary>
    private void RefreshRowsInPlace(Guid packId)
    {
        var active = App.State.Shaders.ActiveShader(packId);
        for (var i = 0; i < _all.Count; i++)
        {
            if (_all[i].SourcePackId != packId) continue;
            _all[i] = _all[i] with
            {
                IsActive = string.Equals(active, _all[i].FileName, StringComparison.OrdinalIgnoreCase)
            };
        }
        ApplyFilter();
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ShaderRow row }) Apply(row);
    }

    private void OnCtxApply(object sender, RoutedEventArgs e)
    {
        if (RowFromMenu(sender) is { } row) Apply(row);
    }

    private void Reveal(ShaderRow row)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = row.Shader.IsFolder ? $"\"{row.Shader.FilePath}\"" : $"/select,\"{row.Shader.FilePath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private void OnReveal(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ShaderRow row }) Reveal(row);
    }

    private void OnCtxReveal(object sender, RoutedEventArgs e)
    {
        if (RowFromMenu(sender) is { } row) Reveal(row);
    }

    private void OnCtxCopyPath(object sender, RoutedEventArgs e)
    {
        if (RowFromMenu(sender) is not { } row) return;
        Okay(ClipboardHelper.TrySetText(row.Shader.FilePath)
            ? "Copied the file path."
            : "The clipboard is busy — nothing was copied.");
    }

    private void OnCtxOpenSettings(object sender, RoutedEventArgs e)
    {
        if (RowFromMenu(sender) is not { } row) return;
        if (!row.Shader.HasSettings || row.Shader.SettingsPath is not { Length: > 0 } path)
        {
            Note($"{row.Shader.DisplayName} has no saved settings yet — tune it in game once and Iris writes them here.");
            return;
        }
        FileEditorWindow.OpenFileFor(_shell, row.Shader.SourcePackId, row.Shader.SourcePackName, path);
    }

    private async void OnCtxResetSettings(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RowFromMenu(sender) is not { } row) return;
            if (!row.Shader.HasSettings)
            {
                Note($"{row.Shader.DisplayName} is already at the author's defaults.");
                return;
            }
            if (!await AppDialog.ConfirmAsync(_shell, "Reset shader settings",
                    $"Throw away the settings you tuned for {row.Shader.DisplayName} in {row.Shader.SourcePackName}?" +
                    "\n\nThe pack itself stays; it goes back to the author's defaults.",
                    "Reset", "Cancel", danger: true))
                return;

            var done = await Task.Run(() => App.State.Shaders.ResetSettings(row.Shader));
            Okay(done ? $"Reset {row.Shader.DisplayName} to its defaults." : "There were no settings to reset.");
            await ReloadAsync();
        }
        catch (Exception ex) { Fail("Could not reset the settings: " + ex.Message); }
    }

    private void RenameRow(ShaderRow row)
    {
        var dialog = new SimpleInputDialog("Rename shader pack", "Shown as", row.Shader.DisplayName)
            { Owner = _shell };
        if (dialog.ShowDialog() != true) return;
        var name = (dialog.Result ?? "").Trim();
        try
        {
            // An empty name is not an error: it means "go back to the name derived from the file".
            App.State.Shaders.Rename(row.Shader.Key, name);
            var index = _all.FindIndex(s => s.Key == row.Shader.Key);
            if (index >= 0)
                _all[index] = _all[index] with
                {
                    DisplayName = name.Length > 0 ? name : ShaderPackService.PrettyName(_all[index].FileName)
                };
            ApplyFilter();
            Okay(name.Length > 0 ? $"Renamed to {name}." : "Name restored from the file.");
        }
        catch (Exception ex) { Fail("Rename failed: " + ex.Message); }
    }

    private void OnCtxRename(object sender, RoutedEventArgs e)
    {
        if (RowFromMenu(sender) is { } row) RenameRow(row);
    }

    private async void OnCopyTo(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ShaderRow row }) await CopyRowsAsync(SelectedRows(row));
    }

    private async void OnCtxCopyTo(object sender, RoutedEventArgs e)
    {
        if (RowFromMenu(sender) is { } row) await CopyRowsAsync(SelectedRows(row));
    }

    /// <summary>Copies one or many shader packs into another instance, asking for the destination once.</summary>
    private async Task CopyRowsAsync(IReadOnlyList<ShaderRow> rows)
    {
        try
        {
            if (rows.Count == 0) return;
            var sourceIds = rows.Select(r => r.Shader.SourcePackId).Distinct().ToList();
            // Only exclude the source instance when every selected pack comes from the same one;
            // otherwise every instance is a sensible destination for at least one of them.
            var targets = sourceIds.Count == 1
                ? _packs.Where(p => p.Id != sourceIds[0]).ToList()
                : _packs.ToList();
            if (targets.Count == 0) { Fail("There is no other instance to copy into."); return; }

            var what = rows.Count == 1 ? rows[0].Shader.DisplayName : $"{rows.Count} shader packs";
            var picker = new PackPickerDialog(targets, "Copy shader to…",
                $"Pick the instance to copy {what} into.", "Copy") { Owner = _shell };
            if (picker.ShowDialog() != true || picker.SelectedPackId is not { } targetId) return;

            await CopyIntoAsync(rows, new[] { targets.First(p => p.Id == targetId) });
        }
        catch (Exception ex) { Fail("Copy failed: " + ex.Message); }
    }

    private async void OnCtxCopyToAll(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RowFromMenu(sender) is not { } clicked) return;
            var rows = SelectedRows(clicked);
            var what = rows.Count == 1 ? rows[0].Shader.DisplayName : $"{rows.Count} shader packs";
            if (_packs.Count < 2) { Fail("There is only one instance."); return; }
            if (!await AppDialog.ConfirmAsync(_shell, "Copy to every instance",
                    $"Copy {what} into all {_packs.Count} instances?",
                    "Copy", "Cancel"))
                return;
            await CopyIntoAsync(rows, _packs);
        }
        catch (Exception ex) { Fail("Copy failed: " + ex.Message); }
    }

    /// <summary>The actual copy, off the UI thread, skipping the instance a pack already lives in.</summary>
    private async Task CopyIntoAsync(IReadOnlyList<ShaderRow> rows, IReadOnlyList<PackSummary> targets)
    {
        var copied = 0;
        var skipped = 0;
        var failures = new List<string>();

        Note($"Copying {rows.Count} shader pack(s)…");
        foreach (var target in targets)
        {
            foreach (var row in rows)
            {
                if (row.Shader.SourcePackId == target.Id) { skipped++; continue; }
                try
                {
                    await App.State.Shaders.CopyToAsync(row.Shader, target.Id);
                    copied++;
                }
                catch (Exception ex) { failures.Add($"{row.Shader.DisplayName} → {target.Name}: {ex.Message}"); }
            }
        }

        await ReloadAsync();
        var tail = skipped > 0 ? $" ({skipped} already there)" : "";
        if (failures.Count > 0)
            Fail($"Copied {copied}{tail}; {failures.Count} failed — {failures[0]}");
        else
            Okay($"Copied {copied} shader pack(s){tail}.");
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ShaderRow row }) await DeleteRowsAsync(SelectedRows(row));
    }

    private async void OnCtxDelete(object sender, RoutedEventArgs e)
    {
        if (RowFromMenu(sender) is { } row) await DeleteRowsAsync(SelectedRows(row));
    }

    /// <summary>Deletes one or many shader packs after a single confirmation naming what goes.</summary>
    private async Task DeleteRowsAsync(IReadOnlyList<ShaderRow> rows)
    {
        try
        {
            if (rows.Count == 0) return;
            var message = rows.Count == 1
                ? $"Delete {rows[0].Shader.DisplayName} from {rows[0].Shader.SourcePackName}?\n\n" +
                  "This removes the file from disk, along with any settings you tuned for it."
                : $"Delete these {rows.Count} shader packs from disk?\n\n" +
                  string.Join("\n", rows.Take(8).Select(r => $"· {r.Shader.DisplayName} ({r.Shader.SourcePackName})")) +
                  (rows.Count > 8 ? $"\n· …and {rows.Count - 8} more" : "");

            if (!await AppDialog.ConfirmAsync(_shell, rows.Count == 1 ? "Delete shader pack" : "Delete shader packs",
                    message, "Delete", "Cancel", danger: true))
                return;

            var deleted = 0;
            var failures = new List<string>();
            Note($"Deleting {rows.Count} shader pack(s)…");
            foreach (var row in rows)
            {
                try { await App.State.Shaders.DeleteAsync(row.Shader); deleted++; }
                catch (Exception ex) { failures.Add($"{row.Shader.DisplayName}: {ex.Message}"); }
            }

            await ReloadAsync();
            if (failures.Count > 0) Fail($"Deleted {deleted}; {failures.Count} failed — {failures[0]}");
            else Okay($"Deleted {deleted} shader pack(s).");
        }
        catch (Exception ex) { Fail("Delete failed: " + ex.Message); }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ResolveTargetPack("shader packs", "Open") is not { } packId) return;
            var dir = App.State.Shaders.FolderFor(packId);
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    // ── adding from disk ─────────────────────────────────────────────────────

    private async void OnAddFromFile(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ResolveTargetPack("these shader packs", "Add") is not { } packId) return;
            var dialog = new OpenFileDialog
            {
                Title = "Add shader packs",
                Filter = "Shader packs (*.zip)|*.zip|All files (*.*)|*.*",
                Multiselect = true
            };
            if (dialog.ShowDialog(_shell) != true) return;
            await AddFilesAsync(packId, dialog.FileNames);
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedShaders(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnListDrop(object sender, DragEventArgs e)
    {
        try
        {
            var files = DroppedShaders(e);
            e.Handled = true;
            if (files.Count == 0) return;
            if (ResolveTargetPack(files.Count == 1 ? Path.GetFileName(files[0]) : "these shader packs", "Add")
                is not { } packId) return;
            await AddFilesAsync(packId, files);
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    /// <summary>The zips and unpacked shader folders in a drag payload. Anything else is ignored
    /// rather than copied into shaderpacks/.</summary>
    private static List<string> DroppedShaders(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return new List<string>();
        return (e.Data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>())
            .Where(f => Directory.Exists(f)
                        || (File.Exists(f) && f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>
    /// Copies shader files into an instance off the UI thread, asking before it replaces anything.
    /// </summary>
    private async Task AddFilesAsync(Guid packId, IReadOnlyList<string> files)
    {
        var added = 0;
        var failures = new List<string>();
        Note($"Adding {files.Count} shader pack(s) to {NameOf(packId)}…");

        foreach (var file in files)
        {
            try
            {
                var name = Path.GetFileName(file);
                var replace = false;
                if (App.State.Shaders.Exists(packId, name))
                {
                    replace = await AppDialog.ConfirmAsync(_shell, "Shader pack already there",
                        $"{NameOf(packId)} already has a shader pack called {name}.\n\n" +
                        "Replace it (its tuned settings stay), or keep both and add this one as a second copy?",
                        "Replace", "Keep both");
                }
                await App.State.Shaders.InstallAsync(packId, file, replace);
                added++;
            }
            catch (Exception ex) { failures.Add($"{Path.GetFileName(file)}: {ex.Message}"); }
        }

        await ReloadAsync();
        if (failures.Count > 0) Fail($"Added {added}; {failures.Count} failed — {failures[0]}");
        else Okay($"Added {added} shader pack(s) to {NameOf(packId)}.");
    }

    // ── browse the stores ────────────────────────────────────────────────────

    private void OnToggleBrowse(object sender, RoutedEventArgs e)
    {
        var opening = BrowsePanel.Visibility != Visibility.Visible;
        BrowsePanel.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
        BrowseColumn.Width = opening ? new GridLength(1.5, GridUnitType.Star) : new GridLength(0);
        BrowseButton.Content = opening ? "Hide search" : "Get shaders";
        if (!opening)
        {
            _searchCts.Cancel();
            return;
        }
        UpdateTargetLabels();
        ShowStoreEmpty("Search for a shader", "Try “complementary”, “BSL” or “rethinking voxels”.");
        StoreQueryBox.Focus();
    }

    private void OnStoreQueryKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnSearchStores(sender, e);
    }

    private async void OnAnyVersionChanged(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!IsLoaded || BrowsePanel.Visibility != Visibility.Visible) return;
            if (_storeRows.Count == 0 && string.IsNullOrWhiteSpace(StoreQueryBox.Text)) return;
            await BrowseRestartAsync();
        }
        catch (Exception ex) { BrowseStatus.Text = ex.Message; }
    }

    private async void OnSearchStores(object sender, RoutedEventArgs e)
    {
        try { await BrowseRestartAsync(); }
        catch (Exception ex) { BrowseStatus.Text = "Search failed: " + ex.Message; }
    }

    private async Task BrowseRestartAsync()
    {
        _searchCts.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        _off = 0;
        _modrinthDone = false;
        _curseDone = false;
        _storeRows.Clear();
        ClearDetail();
        StoreEmptyState.Visibility = Visibility.Collapsed;
        await DrainAsync(ct);
    }

    private async void OnStoreResultsScroll(object sender, ScrollChangedEventArgs e)
    {
        try
        {
            if (_busy || (_modrinthDone && _curseDone)) return;
            if (e.OriginalSource is ScrollViewer sv
                && sv.VerticalOffset + sv.ViewportHeight >= sv.ExtentHeight - 200)
                await DrainAsync(_searchCts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { BrowseStatus.Text = ex.Message; }
    }

    /// <summary>
    /// Pulls the next page from each store that still has one.
    /// </summary>
    /// <remarks>
    /// The two stores are awaited in their own try/catch rather than through <c>Task.WhenAll</c>:
    /// shader packs are split across them (Complementary is on both, BSL and Rethinking Voxels are
    /// effectively CurseForge-only), and a CurseForge hiccup used to throw away a perfectly good set
    /// of Modrinth results and show "Search failed". A store that errors is marked done and named in
    /// the status line; the other one keeps paging.
    /// </remarks>
    private async Task DrainAsync(CancellationToken ct)
    {
        if (_busy || (_modrinthDone && _curseDone) || _storeRows.Count >= BrowseCap) return;

        _busy = true;
        StoreLoadingBar.Visibility = Visibility.Visible;
        var query = StoreQueryBox.Text?.Trim() ?? "";
        var mc = AnyVersionBox.IsChecked == true ? null : TargetMcVersion();
        var problems = new List<string>();
        var added = 0;

        try
        {
            if (!_modrinthDone)
            {
                try
                {
                    var chunk = await App.State.Modrinth.SearchAsync(query, mc, null,
                        limit: PageSize, offset: _off, projectType: "shader", ct: ct);
                    foreach (var m in chunk) _storeRows.Add(new ShaderStoreRow(m));
                    added += chunk.Count;
                    _modrinthDone = chunk.Count < PageSize;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _modrinthDone = true; problems.Add("Modrinth: " + ex.Message); }
            }

            if (!_curseDone)
            {
                try
                {
                    var chunk = await App.State.CurseForge.SearchAsync(query, mc, null,
                        limit: PageSize, offset: _off, classId: CurseForgeService.ClassIdShaders, ct: ct);
                    foreach (var m in chunk) _storeRows.Add(new ShaderStoreRow(m));
                    added += chunk.Count;
                    _curseDone = chunk.Count < PageSize;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _curseDone = true; problems.Add("CurseForge: " + ex.Message); }
            }

            _off += PageSize;

            var versionNote = mc is { Length: > 0 } ? $" for {mc}" : "";
            if (_storeRows.Count == 0)
            {
                ShowStoreEmpty(problems.Count > 0 ? "Both stores are unreachable" : $"No shader packs matched{versionNote}",
                    problems.Count > 0
                        ? string.Join("  ·  ", problems)
                        : "Try a shorter search, or tick “Any version” if this instance is on an older Minecraft.");
            }
            else
            {
                StoreEmptyState.Visibility = Visibility.Collapsed;
                BrowseStatus.Text = $"{_storeRows.Count} result(s){versionNote}."
                                    + (problems.Count > 0 ? "  " + string.Join("  ·  ", problems) : "")
                                    + (_modrinthDone && _curseDone ? "" : "  Scroll for more.");
            }
            if (added == 0 && _storeRows.Count > 0 && _modrinthDone && _curseDone)
                BrowseStatus.Text = $"{_storeRows.Count} result(s){versionNote} — that is everything.";
        }
        catch (OperationCanceledException) { }
        finally
        {
            _busy = false;
            StoreLoadingBar.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowStoreEmpty(string title, string detail)
    {
        StoreEmptyTitle.Text = title;
        StoreEmptyDetail.Text = detail;
        StoreEmptyState.Visibility = _storeRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── store detail pane ────────────────────────────────────────────────────

    // RichDescriptionView renders into an embedded WebView2 whose HWND draws over WPF (airspace),
    // so it has to be hidden whenever its tab is not the one on screen.
    private void OnDetailTabsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != DetailTabs) return;
        var onOverview = DetailTabs.SelectedIndex == OverviewTabIndex;
        OverviewHost.Visibility = onOverview ? Visibility.Visible : Visibility.Collapsed;
        OverviewBrowser.Visibility = onOverview ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearDetail()
    {
        _selectedStoreRow = null;
        _projectUrl = null;
        _cachedVersions.Clear();
        DetailPanel.Visibility = Visibility.Collapsed;
        DetailPlaceholder.Visibility = Visibility.Visible;
        OverviewHost.Visibility = Visibility.Collapsed;
        OverviewBrowser.Visibility = Visibility.Collapsed;
    }

    private async void OnStoreResultSelected(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (StoreResults.SelectedItem is not ShaderStoreRow row) return;
            await ShowStoreDetailAsync(row);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { BrowseStatus.Text = "Could not load that pack: " + ex.Message; }
    }

    private async void OnStoreResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (StoreResults.SelectedItem is ShaderStoreRow row) await InstallFromStoreAsync(row, null);
        }
        catch (Exception ex) { BrowseStatus.Text = "Install failed: " + ex.Message; }
    }

    /// <summary>Fills the right-hand pane for a store result — screenshots first, because a shader
    /// pack is chosen on how it looks and nothing else.</summary>
    private async Task ShowStoreDetailAsync(ShaderStoreRow row)
    {
        _selectedStoreRow = row;
        DetailPanel.Visibility = Visibility.Visible;
        DetailPlaceholder.Visibility = Visibility.Collapsed;
        DetailTabs.SelectedIndex = 0;

        StoreNameLabel.Text = row.Name;
        StoreMetaLabel.Text = row.Meta;
        SelectedIconFallback.Text = row.Initial;
        SelectedIconImage.Source = null;
        if (row.IconUrl is { Length: > 0 } icon)
        {
            try { SelectedIconImage.Source = new BitmapImage(new Uri(icon)); }
            catch { /* a broken icon URL is not worth an error */ }
        }

        ScreenshotList.ItemsSource = null;
        ScreenshotsEmptyText.Text = "Loading screenshots…";
        ScreenshotsEmptyText.Visibility = Visibility.Visible;
        VersionsGrid.ItemsSource = null;
        _cachedVersions.Clear();
        OverviewBrowser.Show("Loading…");
        _projectUrl = row.ProjectUrl;

        var detail = row.Project.Source == ModSource.CurseForge
            ? await App.State.CurseForge.GetProjectDetailAsync(int.TryParse(row.Project.Id, out var cid) ? cid : 0)
            : await App.State.Modrinth.GetProjectDetailAsync(row.Project.Id);

        // The selection can move while the detail is in flight.
        if (!ReferenceEquals(_selectedStoreRow, row)) return;

        OverviewBrowser.Show(detail.Description ?? row.Project.Description, detail.IsMarkdown);

        ScreenshotList.ItemsSource = detail.Screenshots.Select(i => new MediaRow(i)).ToList();
        ScreenshotsEmptyText.Text = "This pack published no screenshots.";
        ScreenshotsEmptyText.Visibility = detail.Screenshots.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

        ToggleLink(ProjectPageButton, detail.Links.WebsiteUrl ?? row.ProjectUrl);
        ToggleLink(IssuesButton, detail.Links.IssuesUrl);
        ToggleLink(SourceButton, detail.Links.SourceUrl);
        ToggleLink(WikiButton, detail.Links.WikiUrl);
        ToggleLink(DiscordButton, detail.Links.DiscordUrl);
        LinksEmptyText.Visibility =
            new[] { ProjectPageButton, IssuesButton, SourceButton, WikiButton, DiscordButton }
                .Any(b => b.Visibility == Visibility.Visible)
                ? Visibility.Collapsed
                : Visibility.Visible;

        var versions = await App.State.ModVersions.GetVersionsAsync(row.Project);
        if (!ReferenceEquals(_selectedStoreRow, row)) return;
        _cachedVersions.Clear();
        _cachedVersions.AddRange(versions);
        VersionsGrid.ItemsSource = versions
            .OrderByDescending(v => v.DatePublished)
            .Select(v => new ShaderVersionRow(v))
            .ToList();
        InstallSelectedButton.IsEnabled = versions.Count > 0;
    }

    private static void ToggleLink(Button button, string? url)
    {
        button.Tag = url ?? "";
        button.Visibility = string.IsNullOrWhiteSpace(url) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnOpenLink(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url } && Uri.TryCreate(url, UriKind.Absolute, out _))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void OnOpenSelectedProject(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_projectUrl))
            Process.Start(new ProcessStartInfo(_projectUrl!) { UseShellExecute = true });
    }

    private void OnOpenScreenshot(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MediaRow row })
            ScreenshotPreviewWindow.ShowFor(Window.GetWindow(this), row.FullImageUrl, row.Title);
        e.Handled = true;
    }

    private static ShaderStoreRow? StoreRowFromMenu(object sender)
    {
        if (sender is not MenuItem leaf) return null;
        DependencyObject? p = leaf;
        while (p is MenuItem m) p = m.Parent as MenuItem ?? m.Parent;
        return p is ContextMenu { PlacementTarget: FrameworkElement fx } && fx.DataContext is ShaderStoreRow r
            ? r
            : null;
    }

    private void OnCtxStoreOpenPage(object sender, RoutedEventArgs e)
    {
        if (StoreRowFromMenu(sender) is { ProjectUrl: { Length: > 0 } url })
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void OnCtxStoreCopyUrl(object sender, RoutedEventArgs e)
    {
        if (StoreRowFromMenu(sender) is not { } row) return;
        BrowseStatus.Text = ClipboardHelper.TrySetText(row.ProjectUrl)
            ? "Copied the project URL."
            : "The clipboard is busy — nothing was copied.";
    }

    // ── installing from a store ──────────────────────────────────────────────

    private async void OnInstallFromStore(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement { Tag: ShaderStoreRow row }) await InstallFromStoreAsync(row, null);
        }
        catch (Exception ex) { BrowseStatus.Text = "Install failed: " + ex.Message; }
    }

    private async void OnCtxStoreInstall(object sender, RoutedEventArgs e)
    {
        try
        {
            if (StoreRowFromMenu(sender) is { } row) await InstallFromStoreAsync(row, null);
        }
        catch (Exception ex) { BrowseStatus.Text = "Install failed: " + ex.Message; }
    }

    private async void OnInstallSelected(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_selectedStoreRow is { } row) await InstallFromStoreAsync(row, null);
        }
        catch (Exception ex) { BrowseStatus.Text = "Install failed: " + ex.Message; }
    }

    private async void OnInstallVersionClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement { DataContext: ShaderVersionRow vm } && _selectedStoreRow is { } row)
                await InstallFromStoreAsync(row, vm.Version);
        }
        catch (Exception ex) { BrowseStatus.Text = "Install failed: " + ex.Message; }
    }

    /// <summary>Opens the version picker for a store result, so an older release or a beta build can
    /// be installed instead of whatever the store happens to list first.</summary>
    private async void OnCtxStoreChooseVersion(object sender, RoutedEventArgs e)
    {
        try
        {
            if (StoreRowFromMenu(sender) is not { } row) return;
            if (ResolveTargetPack(row.Name) is not { } packId) return;
            var pack = _packs.First(p => p.Id == packId);

            BrowseStatus.Text = $"Loading versions of {row.Name}…";
            var versions = (await App.State.ModVersions.GetVersionsAsync(row.Project)).ToList();
            if (versions.Count == 0) { BrowseStatus.Text = "That pack has no downloadable version."; return; }

            var pick = await ModVersionPickerDialog.ShowAsync(_shell, row.Name, versions,
                pack.MinecraftVersion, null);
            if (pick is null) { BrowseStatus.Text = ""; return; }
            await InstallFromStoreAsync(row, pick.Version, packId);
        }
        catch (Exception ex) { BrowseStatus.Text = "Install failed: " + ex.Message; }
    }

    /// <summary>
    /// Downloads a shader pack into an instance, with a real progress bar and a working Cancel.
    /// </summary>
    /// <param name="pinned">The exact version to install, or null for the newest one that matches the
    /// instance's Minecraft version.</param>
    /// <param name="knownTarget">A destination already resolved by the caller, so the version picker
    /// does not ask twice.</param>
    private async Task InstallFromStoreAsync(ShaderStoreRow row, ModVersion? pinned, Guid? knownTarget = null)
    {
        var packId = knownTarget ?? ResolveTargetPack(row.Name);
        if (packId is null) return;
        var pack = _packs.First(p => p.Id == packId);

        _installCts?.Cancel();
        _installCts = new CancellationTokenSource();
        var ct = _installCts.Token;

        BrowseStatus.Text = $"Finding {row.Name}…";
        try
        {
            ModVersion? version = pinned;
            if (version is null)
            {
                // Shaders are not loader-specific, but they are Minecraft-version specific, so the
                // instance's version is the filter that matters. Fall back to the unfiltered list so a
                // pack that simply does not tag its versions is still installable.
                var matching = await App.State.ModVersions.GetVersionsAsync(row.Project, pack.MinecraftVersion, null, ct: ct);
                version = matching.FirstOrDefault()
                          ?? (await App.State.ModVersions.GetVersionsAsync(row.Project, ct: ct)).FirstOrDefault();
            }
            if (version is null) { BrowseStatus.Text = "That pack has no downloadable version."; return; }

            var file = version.Files.FirstOrDefault(f => f.IsPrimary) ?? version.Files.FirstOrDefault();
            var url = file?.DownloadUrl;
            if (string.IsNullOrWhiteSpace(url) && version.Source == ModSource.CurseForge)
            {
                var ids = version.Id.Split(':');
                if (ids.Length == 2 && int.TryParse(ids[0], out var modId) && int.TryParse(ids[1], out var fileId))
                    url = await App.State.CurseForge.GetDownloadUrlAsync(modId, fileId);
            }
            if (string.IsNullOrWhiteSpace(url) || file is null)
            {
                BrowseStatus.Text = "No download link for that pack — install it from its site instead.";
                return;
            }

            var dir = App.State.Shaders.FolderFor(packId.Value);
            Directory.CreateDirectory(dir);
            var fileName = Path.GetFileName(file.Filename);

            var dest = Path.Combine(dir, fileName);
            if (File.Exists(dest) || Directory.Exists(dest))
            {
                var replace = await AppDialog.ConfirmAsync(_shell, "Shader pack already there",
                    $"{pack.Name} already has {fileName}.\n\n" +
                    "Replace it, or keep both and add this one as a second copy?",
                    "Replace", "Keep both");
                if (!replace) dest = ShaderPackService.NextFreePath(dir, fileName);
            }

            await DownloadAsync(url!, dest, $"Downloading {row.Name}…", ct);

            var installedName = Path.GetFileName(dest);
            App.State.Shaders.RecordProvenance(packId.Value, installedName, version.Source,
                row.Project.Id, version.Id, version.VersionNumber);

            var loader = _loaders.TryGetValue(packId.Value, out var l) ? l : ShaderLoader.None;
            BrowseStatus.Text = $"Installed {row.Name} into {pack.Name}."
                                + (loader == ShaderLoader.None
                                    ? $" {pack.Name} still needs Iris (or Oculus) before it will load."
                                    : " Use it from the list on the left.");
            await ReloadAsync();
            Okay($"Installed {row.Name} into {pack.Name}.");
        }
        catch (OperationCanceledException) { BrowseStatus.Text = "Download cancelled."; }
        catch (Exception ex) { BrowseStatus.Text = "Install failed: " + ex.Message; }
        finally { EndDownloadUi(); }
    }

    /// <summary>Downloads to <paramref name="dest"/> showing a determinate bar and a Cancel button —
    /// shader zips run 5-60 MB and the panel used to just sit there.</summary>
    private async Task DownloadAsync(string url, string dest, string label, CancellationToken ct)
    {
        BrowseStatus.Text = label;
        DownloadProgress.Value = 0;
        DownloadProgress.IsIndeterminate = true;
        DownloadProgress.Visibility = Visibility.Visible;
        CancelDownloadButton.Visibility = Visibility.Visible;

        var progress = new Progress<(long done, long total)>(p =>
        {
            if (p.total <= 0)
            {
                DownloadProgress.IsIndeterminate = true;
                return;
            }
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Value = Math.Clamp(p.done * 100.0 / p.total, 0, 100);
            BrowseStatus.Text = $"{label}  {p.done / 1024 / 1024.0:0.#} / {p.total / 1024 / 1024.0:0.#} MB";
        });

        await App.State.Modrinth.DownloadFileAsync(url, dest, progress, ct);
    }

    private void EndDownloadUi()
    {
        DownloadProgress.Visibility = Visibility.Collapsed;
        DownloadProgress.IsIndeterminate = false;
        DownloadProgress.Value = 0;
        CancelDownloadButton.Visibility = Visibility.Collapsed;
    }

    private void OnCancelDownload(object sender, RoutedEventArgs e)
    {
        _installCts?.Cancel();
        BrowseStatus.Text = "Cancelling…";
    }

    // ── updating an installed shader ─────────────────────────────────────────

    private async void OnUpdate(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement { Tag: ShaderRow row }) await UpdateAsync(row);
        }
        catch (Exception ex) { Fail("Update failed: " + ex.Message); }
    }

    private async void OnCtxUpdate(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RowFromMenu(sender) is { } row) await UpdateAsync(row);
        }
        catch (Exception ex) { Fail("Update failed: " + ex.Message); }
    }

    /// <summary>
    /// Offers the store's other versions of an installed shader, and swaps the file for the chosen one.
    /// </summary>
    /// <remarks>
    /// Only possible for a pack the launcher installed, because only then does it know which project
    /// the file on disk actually is (<see cref="AppSettings.SetShaderPackProvenance"/>). The old
    /// pack's tuned Iris settings are carried across to the new file name before the old one goes —
    /// otherwise "update" would quietly reset everything the user spent an evening adjusting.
    /// </remarks>
    private async Task UpdateAsync(ShaderRow row)
    {
        var shader = row.Shader;
        if (!shader.HasProvenance || shader.ProjectId is not { Length: > 0 } projectId
            || shader.Source is not { } source)
        {
            Fail($"{shader.DisplayName} was not installed from a store, so there is nothing to update it from. " +
                 "Install it again from Get shaders and the launcher will remember where it came from.");
            return;
        }

        var pack = _packs.FirstOrDefault(p => p.Id == shader.SourcePackId);
        Note($"Looking for newer versions of {shader.DisplayName}…");

        var project = new ModSummary(projectId, "", shader.DisplayName, null, null, 0, null, source,
            Array.Empty<string>());
        var versions = (await App.State.ModVersions.GetVersionsAsync(project)).ToList();
        if (versions.Count == 0) { Fail("That store lists no versions for this pack any more."); return; }

        var pick = await ModVersionPickerDialog.ShowAsync(_shell, shader.DisplayName, versions,
            pack?.MinecraftVersion, null, shader.VersionId, shader.VersionNumber);
        if (pick is null) { Note(""); return; }

        var file = pick.Version.Files.FirstOrDefault(f => f.IsPrimary) ?? pick.Version.Files.FirstOrDefault();
        var url = file?.DownloadUrl;
        if (string.IsNullOrWhiteSpace(url) && pick.Version.Source == ModSource.CurseForge)
        {
            var ids = pick.Version.Id.Split(':');
            if (ids.Length == 2 && int.TryParse(ids[0], out var modId) && int.TryParse(ids[1], out var fileId))
                url = await App.State.CurseForge.GetDownloadUrlAsync(modId, fileId);
        }
        if (string.IsNullOrWhiteSpace(url) || file is null)
        {
            Fail("That version has no download link — get it from the project's site instead.");
            return;
        }

        _installCts?.Cancel();
        _installCts = new CancellationTokenSource();
        var ct = _installCts.Token;

        try
        {
            var dir = App.State.Shaders.FolderFor(shader.SourcePackId);
            Directory.CreateDirectory(dir);
            var fileName = Path.GetFileName(file.Filename);
            var dest = ShaderPackService.NextFreePath(dir, fileName);
            var replacingSelf = string.Equals(fileName, shader.FileName, StringComparison.OrdinalIgnoreCase);

            await DownloadAsync(url!, dest, $"Downloading {shader.DisplayName} {pick.Version.VersionNumber}…", ct);

            var installedName = Path.GetFileName(dest);
            App.State.Shaders.RecordProvenance(shader.SourcePackId, installedName, pick.Version.Source,
                projectId, pick.Version.Id, pick.Version.VersionNumber);

            await Task.Run(() =>
            {
                // Carry the tuned settings onto the new file so the update is not a silent reset.
                if (shader.SettingsPath is { Length: > 0 } sp && File.Exists(sp))
                {
                    try { File.Copy(sp, dest + ".txt", overwrite: true); } catch { /* best effort */ }
                }
                if (!replacingSelf) App.State.Shaders.Delete(shader);
            }, ct);

            if (shader.IsActive) App.State.Shaders.SetActiveShader(shader.SourcePackId, installedName);

            await ReloadAsync();
            Okay($"Updated {shader.DisplayName} to {pick.Version.VersionNumber}" +
                 (shader.IsActive ? " and kept it active." : ".") +
                 (shader.HasSettings ? " Your tuned settings came across." : ""));
        }
        catch (OperationCanceledException) { Note("Update cancelled."); }
        finally { EndDownloadUi(); }
    }

    // ── folder membership submenu ────────────────────────────────────────────

    /// <summary>Builds the "Add to folder" submenu each time it opens, so it reflects folders created
    /// since the page loaded and ticks the ones this shader is already in.</summary>
    private void OnCtxAddToFolderOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem root || RowFromMenu(sender) is not { } row) return;
        var rows = SelectedRows(row);
        root.Items.Clear();

        foreach (var folder in App.State.Settings.ShaderFolders.Keys
                     .OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase))
        {
            var members = App.State.Settings.ShaderFolders[folder];
            var allInside = rows.All(r => members.Contains(r.Shader.Key));
            var item = new MenuItem { Header = folder, IsCheckable = true, IsChecked = allInside };
            var name = folder;
            item.Click += (_, _) =>
            {
                foreach (var r in rows)
                {
                    if (item.IsChecked) App.State.Settings.AddShaderToFolder(name, r.Shader.Key);
                    else App.State.Settings.RemoveShaderFromFolder(name, r.Shader.Key);
                }
                App.State.Settings.Save();
                RebuildFolders();
                ApplyFilter();
            };
            root.Items.Add(item);
        }

        if (root.Items.Count > 0) root.Items.Add(new Separator());
        var create = new MenuItem { Header = "New folder…" };
        create.Click += (_, _) =>
        {
            var dialog = new SimpleInputDialog("New shader folder", "Folder name", "") { Owner = _shell };
            if (dialog.ShowDialog() != true) return;
            var fresh = (dialog.Result ?? "").Trim();
            if (fresh.Length == 0) return;
            App.State.Settings.CreateShaderFolder(fresh);
            foreach (var r in rows) App.State.Settings.AddShaderToFolder(fresh, r.Shader.Key);
            App.State.Settings.Save();
            _activeFolder = fresh;
            RebuildFolders();
            ApplyFilter();
        };
        root.Items.Add(create);
    }

    private sealed record PackFilterItem(Guid? Id, string Label);
}

// ── row view models ──────────────────────────────────────────────────────────

/// <summary>One installed shader pack in the list.</summary>
/// <remarks>Deliberately holds no <see cref="System.Windows.Media.Brush"/>: a brush resolved here
/// would be the theme's colour at the moment the row was built and would survive a theme change. The
/// active row's accent border comes from a DataTrigger on <see cref="IsActive"/> instead.</remarks>
public sealed class ShaderRow(ShaderPackInfo shader)
{
    public ShaderPackInfo Shader { get; } = shader;
    public string DisplayName => Shader.DisplayName;
    public string MetaLabel => Shader.MetaLabel;
    public bool IsActive => Shader.IsActive;
    public string ApplyLabel => Shader.IsActive ? "Turn off" : "Use";

    public Visibility ActiveVisibility => Shader.IsActive ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FolderVisibility => Shader.IsFolder ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TunedVisibility => Shader.HasSettings ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SourceVisibility => Shader.HasProvenance ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UpdateVisibility => Shader.HasProvenance ? Visibility.Visible : Visibility.Collapsed;

    public string SourceLabel => Shader.Source == ModSource.CurseForge ? "CURSEFORGE" : "MODRINTH";

    public string ToolTipText =>
        $"{Shader.FileName}\n{Shader.FilePath}"
        + (Shader.ProvenanceLabel is { Length: > 0 } p ? $"\n{p}" : "")
        + (Shader.HasSettings ? "\nHas tuned Iris settings" : "");
}

/// <summary>One shader pack listing from Modrinth or CurseForge.</summary>
public sealed class ShaderStoreRow(ModSummary project)
{
    public ModSummary Project { get; } = project;
    public string Name => Project.Name;
    public string Summary => Project.Description ?? "";
    public string? IconUrl => Project.IconUrl;
    public bool IsCurseForge => Project.Source == ModSource.CurseForge;
    public string SourceBadge => IsCurseForge ? "CurseForge" : "Modrinth";

    public string Initial => string.IsNullOrWhiteSpace(Project.Name)
        ? "?"
        : char.ToUpperInvariant(Project.Name[0]).ToString();

    public string Meta =>
        $"{(Project.Author is { Length: > 0 } a ? a : "Unknown author")}" +
        $"{(Project.DownloadCount > 0 ? $"  ·  {Project.DownloadCount:N0} downloads" : "")}";

    public string ProjectUrl
    {
        get
        {
            var slug = string.IsNullOrWhiteSpace(Project.Slug) ? Project.Id : Project.Slug;
            return IsCurseForge
                ? $"https://www.curseforge.com/minecraft/shaders/{slug}"
                : $"https://modrinth.com/shader/{slug}";
        }
    }
}

/// <summary>One row of the store detail pane's Versions grid.</summary>
public sealed class ShaderVersionRow(ModVersion version)
{
    public ModVersion Version { get; } = version;
    public string VersionNumber => Version.VersionNumber;
    public string McVersions => string.Join(", ", Version.GameVersions);
    public string Channel => Version.ReleaseChannel;
    public string DateLabel => Version.DatePublished.LocalDateTime.ToString("yyyy-MM-dd");

    public string SizeLabel
    {
        get
        {
            var file = Version.Files.FirstOrDefault(f => f.IsPrimary) ?? Version.Files.FirstOrDefault();
            if (file is null || file.Size <= 0) return "";
            var kb = file.Size / 1024.0;
            return kb < 1024 ? $"{kb:0.#} KB" : $"{kb / 1024.0:0.#} MB";
        }
    }
}

/// <summary>One chip in the shader folder strip.</summary>
public sealed class ShaderFolderChip(string id, string label, string icon, int count, bool isSelected, string toolTip)
{
    public string Id { get; } = id;
    public string Label { get; } = label;
    public string Icon { get; } = icon;
    public string CountLabel { get; } = count > 0 ? $"({count})" : "";
    public bool IsSelected { get; } = isSelected;
    public string ToolTipText { get; } = toolTip;
    public FontWeight LabelWeight => IsSelected ? FontWeights.SemiBold : FontWeights.Normal;
}
