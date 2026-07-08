using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using CloudLauncher.Services;
using CloudLauncher.Shared;
using Microsoft.Win32;

namespace CloudLauncher.Views;

public partial class ResourcePackListView : UserControl
{
    private PackDetail? _pack;
    private Window? _ownerWindow;
    private readonly ObservableCollection<ResourcePackRow> _rows = new();
    private readonly ICollectionView _view;
    private int _scanGeneration;
    private string _searchText = "";

    private const double SearchCollapsedWidth = 36;
    private const double SearchExpandedWidth = 240;

    public ResourcePackListView()
    {
        InitializeComponent();
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = RowFilter;
        ResourcePackGrid.ItemsSource = _view;
    }

    public void Load(PackDetail pack, Window owner)
    {
        _pack = pack;
        _ownerWindow = owner;
        _ = ScanAsync();
    }

    private async Task ScanAsync()
    {
        if (_pack is null) return;
        var generation = ++_scanGeneration;
        StatusLabel.Text = "Scanning resource packs...";
        _rows.Clear();

        var pack = _pack;
        var files = await Task.Run(() =>
        {
            var found = new List<(string path, string folder)>();
            void AddFolder(string dir, string label)
            {
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.EnumerateFiles(dir, "*.zip", SearchOption.TopDirectoryOnly))
                    found.Add((f, label));
            }

            AddFolder(Path.Combine(App.State.Packs.GameDir(pack.Id), "resourcepacks"), "game");
            if (pack.IsShared)
            {
                AddFolder(Path.Combine(App.State.Packs.LocalDir(pack.Id), "resourcepacks"), "local");
            }
            return found;
        });

        if (generation != _scanGeneration) return;

        _rows.Clear();
        foreach (var (path, folder) in files)
        {
            var key = ResourcePackService.Key(pack.Id, Path.GetFileName(path));
            var entry = App.State.Settings.ResourcePacks.TryGetValue(key, out var e) ? e : null;
            var displayName = !string.IsNullOrWhiteSpace(entry?.DisplayName)
                ? entry.DisplayName
                : Path.GetFileNameWithoutExtension(path);
            var size = new FileInfo(path).Length;
            _rows.Add(new ResourcePackRow(key, path, Path.GetFileName(path), folder, displayName, size));
        }

        StatusLabel.Text = _rows.Count == 0
            ? "No resource pack zips found in any resourcepacks/ folder."
            : $"{_rows.Count} resource pack{(_rows.Count == 1 ? "" : "s")}";
    }

    private bool RowFilter(object item)
    {
        if (item is not ResourcePackRow row) return false;
        if (string.IsNullOrWhiteSpace(_searchText)) return true;
        var needle = _searchText.Trim().ToLowerInvariant();
        return row.DisplayName.ToLowerInvariant().Contains(needle)
            || row.FileName.ToLowerInvariant().Contains(needle)
            || row.Folder.ToLowerInvariant().Contains(needle);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
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
            _ = ScanAsync();
            e.Handled = true;
        }
    }

    private void OnSearchToggle(object sender, RoutedEventArgs e)
    {
        ExpandSearch();
        SearchBox.Focus();
        SearchBox.SelectAll();
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

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = SearchBox.Text;
        _view.Refresh();
    }

    private void OnSearchPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SearchBox.Text = "";
            _searchText = "";
            CollapseSearch();
            _view.Refresh();
            e.Handled = true;
        }
    }

    private void OnSearchLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is DependencyObject next && IsDescendantOf(next, CompactSearchHost))
            return;
        CollapseSearch();
    }

    private static bool IsDescendantOf(DependencyObject child, DependencyObject ancestor)
    {
        for (var current = child; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor)) return true;
        }
        return false;
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await ScanAsync();

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
        if (_pack is null) return;
        var dlg = new OpenFileDialog
        {
            Title = "Select resource pack zip",
            Filter = "Resource pack zip (*.zip)|*.zip|All files|*"
        };
        if (dlg.ShowDialog(_ownerWindow) != true) return;

        try
        {
            var destDir = Path.Combine(App.State.Packs.GameDir(_pack.Id), "resourcepacks");
            Directory.CreateDirectory(destDir);
            var dest = UniquePath(destDir, Path.GetFileName(dlg.FileName));
            File.Copy(dlg.FileName, dest);
            StatusLabel.Text = $"Imported {Path.GetFileName(dest)}.";
            await ScanAsync();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Import failed: " + ex.Message;
        }
    }

    private void OnGridPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is { } row
            && !row.IsSelected)
        {
            ResourcePackGrid.SelectedItem = row.Item;
        }
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResourcePackGrid.SelectedItem is not ResourcePackRow row) return;
        if (_ownerWindow is MinecraftHostWindow host)
            host.OpenLocalResourcePackDetail(row.Key, row.DisplayName);
        else if (_ownerWindow is MainWindow main)
            main.OpenLocalResourcePackDetail(row.Key, row.DisplayName);
    }

    private void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (ResourcePackGrid.SelectedItem is not ResourcePackRow row)
        {
            if (sender is ContextMenu cm) cm.IsOpen = false;
            return;
        }
        CtxCreateHosted.Visibility = File.Exists(row.FilePath) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCtxEdit(object sender, RoutedEventArgs e)
    {
        if (ResourcePackGrid.SelectedItem is not ResourcePackRow row) return;
        if (_ownerWindow is MinecraftHostWindow host)
            host.OpenLocalResourcePackDetail(row.Key, row.DisplayName);
        else if (_ownerWindow is MainWindow main)
            main.OpenLocalResourcePackDetail(row.Key, row.DisplayName);
    }

    private async void OnCtxCreateHosted(object sender, RoutedEventArgs e)
    {
        if (_pack is null) return;
        if (ResourcePackGrid.SelectedItem is not ResourcePackRow row) return;
        if (!File.Exists(row.FilePath))
        {
            StatusLabel.Text = "Zip file no longer exists.";
            return;
        }

        var nameDialog = new SimpleInputDialog("Create hosted resource pack", "Name", row.DisplayName)
        {
            Owner = _ownerWindow
        };
        if (nameDialog.ShowDialog() != true) return;

        var name = (nameDialog.Result ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusLabel.Text = "Name cannot be empty.";
            return;
        }

        var versionDialog = new SimpleInputDialog("Upload first version", "Version string", "1.0.0")
        {
            Owner = _ownerWindow
        };
        if (versionDialog.ShowDialog() != true) return;

        var version = (versionDialog.Result ?? "").Trim();
        if (string.IsNullOrWhiteSpace(version))
        {
            StatusLabel.Text = "Version string cannot be empty.";
            return;
        }

        StatusLabel.Text = $"Creating hosted resource pack for {row.FileName}...";
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
    }

    private void OnCtxReveal(object sender, RoutedEventArgs e)
    {
        if (ResourcePackGrid.SelectedItem is not ResourcePackRow row) return;
        var dir = Path.GetDirectoryName(row.FilePath);
        if (dir is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private async void OnCtxDelete(object sender, RoutedEventArgs e)
    {
        if (ResourcePackGrid.SelectedItem is not ResourcePackRow row) return;
        if (MessageBox.Show(_ownerWindow,
                $"Delete {row.FileName}?",
                "Confirm delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            File.Delete(row.FilePath);
            App.State.Settings.ResourcePacks.Remove(row.Key);
            foreach (var folder in App.State.Settings.ResourcePackFolders.Values)
                folder.Remove(row.Key);
            App.State.Settings.Save();
            await ScanAsync();
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private static string UniquePath(string folder, string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string((fileName ?? "pack.zip").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(clean)) clean = "pack.zip";
        if (!clean.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) clean += ".zip";

        var candidate = Path.Combine(folder, clean);
        var stem = Path.GetFileNameWithoutExtension(clean);
        var n = 2;
        while (File.Exists(candidate))
            candidate = Path.Combine(folder, $"{stem}-{n++}.zip");
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
}

public sealed class ResourcePackRow
{
    public string Key { get; }
    public string FilePath { get; }
    public string FileName { get; }
    public string Folder { get; }
    public string DisplayName { get; }
    public long SizeBytes { get; }

    public string Initial => string.IsNullOrWhiteSpace(DisplayName) ? "?"
        : DisplayName.Trim()[..1].ToUpperInvariant();

    public string SizeLabel => SizeBytes switch
    {
        >= 1024L * 1024 => $"{SizeBytes / (1024.0 * 1024):F1} MB",
        >= 1024 => $"{SizeBytes / 1024.0:F0} KB",
        _ => $"{SizeBytes} B"
    };

    public ResourcePackRow(string key, string path, string fileName, string folder, string displayName, long sizeBytes)
    {
        Key = key;
        FilePath = path;
        FileName = fileName;
        Folder = folder;
        DisplayName = displayName;
        SizeBytes = sizeBytes;
    }
}
