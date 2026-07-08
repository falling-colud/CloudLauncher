using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

public partial class WorldDetailView : Page
{
    private readonly MainWindow _shell;
    private readonly string _worldKey;
    private WorldInfo? _world;
    private List<PackSummary> _allPacks = new();
    private readonly ObservableCollection<PackCompatibilityRow> _packRows = new();
    private readonly ObservableCollection<SharedWorldVersionRow> _versionRows = new();
    private bool _suppress;
    private WorldEntry? _entry;

    public WorldDetailView(MainWindow shell, string worldKey)
    {
        InitializeComponent();
        _shell = shell;
        _worldKey = worldKey;
        PackCheckList.ItemsSource = _packRows;
        UploadedVersionsList.ItemsSource = _versionRows;
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        try
        {
            _allPacks = await App.State.Api.ListPacksAsync();
            // Find this world via the source pack stored in the key
            _world = null;
            var keyParts = _worldKey.Split(':', 2);
            if (keyParts.Length == 2 && Guid.TryParseExact(keyParts[0], "N", out var sourceId))
            {
                var pack = _allPacks.FirstOrDefault(p => p.Id == sourceId);
                if (pack is not null)
                    _world = App.State.Worlds.ScanPack(pack).FirstOrDefault(w => w.Key == _worldKey);
            }
            if (_world is null) { StatusLabel.Text = "World no longer exists."; return; }
            _entry = App.State.Worlds.GetOrCreate(_world.Key);

            _suppress = true;
            try
            {
                WorldNameLabel.Text = _world.DisplayName;
                NameBox.Text = _world.DisplayName;
                SourcePackLabel.Text = _world.SourcePackName.ToUpperInvariant();
                MetaLabel.Text = $"Modified {_world.LastModified.LocalDateTime:g} · {FormatSize(_world.SizeBytes)}";
                WorldFolderPathLabel.Text = _world.FolderPath;
                ConfigurePlayerDataBrowser();
                ConfigureOverviewEditor();

                AllPacksBox.IsChecked = _world.CompatibleWithAll;
                PackListWrap.IsEnabled = !_world.CompatibleWithAll;

                _packRows.Clear();
                foreach (var p in _allPacks)
                {
                    var isSource = p.Id == _world.SourcePackId;
                    _packRows.Add(new PackCompatibilityRow
                    {
                        PackId = p.Id,
                        PackName = p.Name + (isSource ? " (source)" : ""),
                        VersionLabel = p.IsEmpty ? "EMPTY"
                            : p.Loader == LoaderKind.None ? $"MC {p.MinecraftVersion}"
                            : $"MC {p.MinecraftVersion} · {p.Loader}",
                        IsCompatible = _world.CompatibleWithAll || isSource || _world.CompatiblePackIds.Contains(p.Id),
                        IsEditable = !isSource && !_world.CompatibleWithAll
                    });
                }
            }
            finally { _suppress = false; }
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private void OnSaveName(object sender, RoutedEventArgs e)
    {
        if (_world is null) return;
        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) { StatusLabel.Text = "Name cannot be empty."; return; }
        App.State.Worlds.Rename(_world.Key, name);
        WorldNameLabel.Text = name;
        StatusLabel.Text = "Name saved.";
    }

    private void ConfigureOverviewEditor()
    {
        if (_world is null || _entry is null) return;
        SummaryBox.Text = _entry.Summary ?? "";
        DescriptionBox.Text = _entry.Description ?? "";
        VisibilityBox.SelectedIndex = (int)_entry.Visibility;
        SharedWorldStatusLabel.Text = _entry.SharedWorldId is Guid sharedId
            ? $"Linked to hosted world {sharedId}. Uploading creates a new downloadable version."
            : "Not shared yet. Save an overview, then share/upload to host this world for others.";
        ConfigureSharingState();
        OverviewSaveStatus.Text = "";
    }

    private bool IsSharingEnabled() => _entry?.SharingEnabled ?? _entry?.SharedWorldId is Guid;

    private void ConfigureSharingState()
    {
        if (_entry is null) return;
        var enabled = IsSharingEnabled();
        ShareWorldBox.IsChecked = enabled;
        SharingDisabledHint.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        SharingSettingsPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        VersionsDisabledPanel.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        VersionsPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        WorldPermissionsButton.IsEnabled = _entry.SharedWorldId is Guid;
        RefreshVersionsButton.IsEnabled = _entry.SharedWorldId is Guid;

        if (!enabled)
        {
            _versionRows.Clear();
            VersionsEmptyLabel.Visibility = Visibility.Collapsed;
            return;
        }

        if (_entry.SharedWorldId is Guid)
            _ = LoadSharedWorldVersionsAsync();
        else
        {
            _versionRows.Clear();
            VersionsEmptyLabel.Text = "Upload the first hosted version to publish this world.";
            VersionsEmptyLabel.Visibility = Visibility.Visible;
        }
    }

    private void OnShareWorldToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress || _world is null) return;
        var enabled = ShareWorldBox.IsChecked == true;
        App.State.Worlds.SetSharingEnabled(_world.Key, enabled);
        _entry = App.State.Worlds.GetOrCreate(_world.Key);
        ConfigureSharingState();
        StatusLabel.Text = enabled
            ? "Sharing enabled. Upload a version to publish this world."
            : "Sharing controls hidden for this world.";
    }

    private void OnSaveOverview(object sender, RoutedEventArgs e)
    {
        if (_world is null) return;
        var visibility = SelectedVisibility();
        App.State.Worlds.UpdateOverview(_world.Key, SummaryBox.Text, DescriptionBox.Text, visibility);
        _entry = App.State.Worlds.GetOrCreate(_world.Key);
        ConfigureOverviewEditor();
        OverviewSaveStatus.Text = "Overview saved.";
    }

    private async void OnShareOrUploadWorld(object sender, RoutedEventArgs e)
    {
        if (_world is null) return;
        SaveOverviewToSettings();

        var versionDlg = new SimpleInputDialog(
            "Upload world version",
            "Version string (e.g. 1.0.0 or 2026.05.24):",
            DateTimeOffset.Now.ToString("yyyy.MM.dd.HHmm"))
        { Owner = _shell };
        if (versionDlg.ShowDialog() != true || string.IsNullOrWhiteSpace(versionDlg.Result)) return;

        UploadSharedWorldButton.IsEnabled = false;
        StatusLabel.Text = "Preparing world zip...";
        string? zipPath = null;
        try
        {
            var sharedId = await EnsureSharedWorldAsync();
            zipPath = ZipWorld(_world);
            var meta = new CreateWorldVersionRequest(
                versionDlg.Result.Trim(),
                null,
                SafeZipName(_world.DisplayName),
                SourceMinecraftVersion());

            StatusLabel.Text = "Uploading world...";
            await App.State.Api.UploadSharedWorldVersionAsync(sharedId, zipPath, meta);
            StatusLabel.Text = "World uploaded.";
            SharedWorldStatusLabel.Text = $"Shared as hosted world {sharedId}.";
            await LoadSharedWorldVersionsAsync();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Share failed: " + ex.Message;
        }
        finally
        {
            UploadSharedWorldButton.IsEnabled = true;
            if (zipPath is not null)
            {
                try { File.Delete(zipPath); } catch { /* best effort */ }
            }
        }
    }

    private async void OnEditSharedWorldPermissions(object sender, RoutedEventArgs e)
    {
        if (_world is null) return;
        _entry ??= App.State.Worlds.GetOrCreate(_world.Key);
        if (_entry.SharedWorldId is not Guid sharedId)
        {
            StatusLabel.Text = "Share/upload this world before managing collaborators.";
            return;
        }

        try
        {
            var detail = await App.State.Api.GetSharedWorldAsync(sharedId);
            new PermissionsDialog(detail) { Owner = _shell }.ShowDialog();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Could not load permissions: " + ex.Message;
        }
    }

    private void SaveOverviewToSettings()
    {
        if (_world is null) return;
        App.State.Worlds.UpdateOverview(_world.Key, SummaryBox.Text, DescriptionBox.Text, SelectedVisibility());
        App.State.Worlds.SetSharingEnabled(_world.Key, true);
        _entry = App.State.Worlds.GetOrCreate(_world.Key);
    }

    private async Task<Guid> EnsureSharedWorldAsync()
    {
        if (_world is null) throw new InvalidOperationException("World no longer exists.");
        _entry ??= App.State.Worlds.GetOrCreate(_world.Key);

        if (_entry.SharedWorldId is Guid existingId)
        {
            await App.State.Api.UpdateSharedWorldAsync(existingId, new UpdateWorldRequest(
                _world.DisplayName,
                string.IsNullOrWhiteSpace(SummaryBox.Text) ? null : SummaryBox.Text.Trim(),
                string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text.Trim(),
                SelectedVisibility()));
            return existingId;
        }

        var created = await App.State.Api.CreateSharedWorldAsync(new CreateWorldRequest(
            _world.DisplayName,
            string.IsNullOrWhiteSpace(SummaryBox.Text) ? null : SummaryBox.Text.Trim(),
            string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text.Trim(),
            SelectedVisibility(),
            SourceMinecraftVersion()));
        App.State.Worlds.LinkSharedWorld(_world.Key, created.Id);
        _entry = App.State.Worlds.GetOrCreate(_world.Key);
        return created.Id;
    }

    private PackVisibility SelectedVisibility()
    {
        var text = (VisibilityBox.SelectedItem as ComboBoxItem)?.Content as string ?? "Private";
        return Enum.TryParse<PackVisibility>(text, out var visibility) ? visibility : PackVisibility.Private;
    }

    private async void OnRefreshSharedWorldVersions(object sender, RoutedEventArgs e) =>
        await LoadSharedWorldVersionsAsync();

    private async Task LoadSharedWorldVersionsAsync()
    {
        if (_entry?.SharedWorldId is not Guid sharedId)
        {
            _versionRows.Clear();
            VersionsEmptyLabel.Text = "Upload the first hosted version to publish this world.";
            VersionsEmptyLabel.Visibility = IsSharingEnabled() ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        RefreshVersionsButton.IsEnabled = false;
        try
        {
            var detail = await App.State.Api.GetSharedWorldAsync(sharedId);
            _versionRows.Clear();
            foreach (var version in detail.Versions.OrderByDescending(v => v.PublishedAt))
                _versionRows.Add(SharedWorldVersionRow.From(version));

            VersionsEmptyLabel.Text = "No hosted versions uploaded yet.";
            VersionsEmptyLabel.Visibility = _versionRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SharedWorldStatusLabel.Text = $"Linked to hosted world {sharedId}. {detail.Versions.Count} version(s) uploaded.";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Could not load uploaded versions: " + ex.Message;
        }
        finally
        {
            RefreshVersionsButton.IsEnabled = _entry?.SharedWorldId is Guid;
        }
    }

    private string? SourceMinecraftVersion() =>
        _world is null
            ? null
            : _allPacks.FirstOrDefault(p => p.Id == _world.SourcePackId)?.MinecraftVersion;

    private static string ZipWorld(WorldInfo world)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cl-world-{Guid.NewGuid():N}.zip");
        ZipFile.CreateFromDirectory(world.FolderPath, path, CompressionLevel.Fastest, includeBaseDirectory: false);
        return path;
    }

    private static string SafeZipName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string((name ?? "world").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(clean)) clean = "world";
        if (clean.Length > 80) clean = clean[..80];
        return clean.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? clean : clean + ".zip";
    }

    private void OnAllPacksToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress || _world is null) return;
        var all = AllPacksBox.IsChecked == true;
        App.State.Worlds.SetCompatibleWithAll(_world.Key, all);
        _ = ReloadAsync();
    }

    private void OnPackCompatibilityChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress || _world is null) return;
        if (sender is not CheckBox cb || cb.Tag is not Guid packId) return;
        App.State.Worlds.SetCompatible(_world.Key, packId, cb.IsChecked == true);
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (_world is null) return;
        Process.Start(new ProcessStartInfo { FileName = _world.FolderPath, UseShellExecute = true });
    }

    private void ConfigurePlayerDataBrowser()
    {
        if (_world is null) return;

        var playerDataDir = PlayerDataDir(_world);
        var exists = Directory.Exists(playerDataDir);
        PlayerDataPathLabel.Text = playerDataDir;
        OpenPlayerDataButton.IsEnabled = exists;
        PlayerDataMissingLabel.Visibility = exists ? Visibility.Collapsed : Visibility.Visible;
        PlayerDataBrowserWrap.Visibility = exists ? Visibility.Visible : Visibility.Collapsed;

        PlayerDataView.Root = playerDataDir;
        if (exists) PlayerDataView.NavigateToRoot();
    }

    private void OnOpenPlayerDataFolder(object sender, RoutedEventArgs e)
    {
        if (_world is null) return;

        var playerDataDir = PlayerDataDir(_world);
        if (!Directory.Exists(playerDataDir))
        {
            StatusLabel.Text = "This world does not have a playerdata folder yet.";
            return;
        }

        Process.Start(new ProcessStartInfo { FileName = playerDataDir, UseShellExecute = true });
    }

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        if (_world is null) return;
        var compat = App.State.Worlds.CompatiblePacks(_world, _allPacks);
        if (compat.Count == 0) { StatusLabel.Text = "No compatible instances."; return; }
        var menu = new ContextMenu();
        foreach (var p in compat)
        {
            var item = new MenuItem { Header = $"Play with {p.Name}" };
            var packId = p.Id;
            item.Click += async (_, _) => await PlayAsync(packId);
            menu.Items.Add(item);
        }
        menu.PlacementTarget = (FrameworkElement)sender;
        menu.IsOpen = true;
    }

    private async Task PlayAsync(Guid packId)
    {
        if (_world is null) return;
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
            if (packId != _world.SourcePackId)
            {
                var target = Path.Combine(App.State.Packs.GameDir(pack.Id), "saves", _world.FolderName);
                if (!Directory.Exists(target))
                {
                    StatusLabel.Text = $"Copying save into {pack.Name}…";
                    foreach (var sub in Directory.EnumerateDirectories(_world.FolderPath, "*", SearchOption.AllDirectories))
                        Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(_world.FolderPath, sub)));
                    foreach (var file in Directory.EnumerateFiles(_world.FolderPath, "*", SearchOption.AllDirectories))
                    {
                        var rel = Path.GetRelativePath(_world.FolderPath, file);
                        var dst = Path.Combine(target, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                        File.Copy(file, dst, overwrite: false);
                    }
                }
            }
            var proc = await App.State.Launcher.LaunchTrackedAsync(pack, new Progress<string>(_ => { }));
            _shell.OpenMinecraftHost(pack, proc);
            StatusLabel.Text = $"Launched {pack.Name}.";
        }
        catch (OperationCanceledException) { StatusLabel.Text = "Launch cancelled."; }
        catch (Exception ex) { StatusLabel.Text = "Launch failed: " + ex.Message; }
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
      : bytes >= 1024L * 1024 ? $"{bytes / (1024.0 * 1024):F1} MB"
      : bytes >= 1024 ? $"{bytes / 1024.0:F0} KB"
      : $"{bytes} B";

    private static string PlayerDataDir(WorldInfo world) => Path.Combine(world.FolderPath, "playerdata");
}

public sealed class SharedWorldVersionRow
{
    public Guid Id { get; init; }
    public string VersionString { get; init; } = "";
    public string MetaLabel { get; init; } = "";
    public string McVersionLabel { get; init; } = "";

    public static SharedWorldVersionRow From(SharedWorldVersionInfo version) => new()
    {
        Id = version.Id,
        VersionString = version.VersionString,
        MetaLabel = $"{version.FileName} · {FormatSize(version.FileSize)} · {version.PublishedAt:yyyy-MM-dd HH:mm}",
        McVersionLabel = string.IsNullOrWhiteSpace(version.McVersion) ? "Any MC" : $"MC {version.McVersion}"
    };

    private static string FormatSize(long bytes) =>
        bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
      : bytes >= 1024L * 1024 ? $"{bytes / (1024.0 * 1024):F1} MB"
      : bytes >= 1024 ? $"{bytes / 1024.0:F0} KB"
      : $"{bytes} B";
}

public sealed class PackCompatibilityRow : INotifyPropertyChanged
{
    private bool _isCompatible;
    private bool _isEditable;

    public Guid PackId { get; init; }
    public string PackName { get; init; } = "";
    public string VersionLabel { get; init; } = "";

    public bool IsCompatible
    {
        get => _isCompatible;
        set { _isCompatible = value; OnPropertyChanged(); }
    }

    public bool IsEditable
    {
        get => _isEditable;
        set { _isEditable = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
