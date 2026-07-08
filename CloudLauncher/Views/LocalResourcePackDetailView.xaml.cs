using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

public partial class LocalResourcePackDetailView : Page
{
    private readonly MainWindow _shell;
    private readonly string _packKey;
    private ResourcePackInfo? _pack;
    private List<PackSummary> _allPacks = new();
    private readonly ObservableCollection<PackCompatibilityRow> _packRows = new();
    private readonly ObservableCollection<HostedRpVersionDisplayRow> _versionRows = new();
    private bool _suppress;
    private ResourcePackEntry? _entry;

    public LocalResourcePackDetailView(MainWindow shell, string packKey)
    {
        InitializeComponent();
        _shell = shell;
        _packKey = packKey;
        PackCheckList.ItemsSource = _packRows;
        HostedVersionsList.ItemsSource = _versionRows;
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        try
        {
            _allPacks = await App.State.Api.ListPacksAsync();
            _pack = null;
            var keyParts = _packKey.Split(':', 2);
            if (keyParts.Length == 2 && Guid.TryParseExact(keyParts[0], "N", out var sourceId))
            {
                var pack = _allPacks.FirstOrDefault(p => p.Id == sourceId);
                if (pack is not null)
                    _pack = App.State.ResourcePacks.ScanPack(pack).FirstOrDefault(w => w.Key == _packKey);
            }

            if (_pack is null) { StatusLabel.Text = "Resource pack file no longer exists."; return; }
            if (!File.Exists(_pack.FilePath)) { StatusLabel.Text = "The resource pack zip was deleted on disk."; return; }

            _entry = App.State.ResourcePacks.GetOrCreate(_pack.Key);

            _suppress = true;
            try
            {
                PackNameLabel.Text = _pack.DisplayName;
                NameBox.Text = _pack.DisplayName;
                SourcePackLabel.Text = _pack.SourcePackName.ToUpperInvariant();
                MetaLabel.Text = $"Modified {_pack.LastModified.LocalDateTime:g} · {FormatSize(_pack.SizeBytes)}";
                FilePathLabel.Text = _pack.FilePath;
                ConfigureOverviewEditor();

                AllPacksBox.IsChecked = _pack.CompatibleWithAll;
                PackListWrap.IsEnabled = !_pack.CompatibleWithAll;

                _packRows.Clear();
                foreach (var p in _allPacks)
                {
                    var isSource = p.Id == _pack.SourcePackId;
                    _packRows.Add(new PackCompatibilityRow
                    {
                        PackId = p.Id,
                        PackName = p.Name + (isSource ? " (source)" : ""),
                        VersionLabel = p.IsEmpty ? "EMPTY"
                            : p.Loader == LoaderKind.None ? $"MC {p.MinecraftVersion}"
                            : $"MC {p.MinecraftVersion} · {p.Loader}",
                        IsCompatible = _pack.CompatibleWithAll || isSource || _pack.CompatiblePackIds.Contains(p.Id),
                        IsEditable = !isSource && !_pack.CompatibleWithAll
                    });
                }
            }
            finally { _suppress = false; }
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private void ConfigureOverviewEditor()
    {
        if (_pack is null || _entry is null) return;
        SummaryBox.Text = _entry.Summary ?? "";
        DescriptionBox.Text = _entry.Description ?? "";
        VisibilityBox.SelectedIndex = (int)_entry.Visibility;
        HostedStatusLabel.Text = _entry.HostedResourcePackId is Guid hid
            ? $"Linked to hosted resource pack {hid}. Upload creates a new downloadable zip version."
            : "Not published yet — save overview, then upload a zip to host it.";
        ConfigureSharingState();
        OverviewSaveStatus.Text = "";
    }

    private bool IsSharingEnabled() => _entry?.SharingEnabled ?? _entry?.HostedResourcePackId is Guid;

    private void ConfigureSharingState()
    {
        if (_entry is null) return;
        var enabled = IsSharingEnabled();
        SharePackBox.IsChecked = enabled;
        SharingDisabledHint.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        SharingSettingsPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        VersionsDisabledPanel.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        VersionsPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        PackPermissionsButton.IsEnabled = _entry.HostedResourcePackId is Guid;
        RefreshVersionsButton.IsEnabled = _entry.HostedResourcePackId is Guid;

        if (!enabled)
        {
            _versionRows.Clear();
            VersionsEmptyLabel.Visibility = Visibility.Collapsed;
            return;
        }

        if (_entry.HostedResourcePackId is Guid)
            _ = LoadHostedVersionsAsync();
        else
        {
            _versionRows.Clear();
            VersionsEmptyLabel.Text = "Upload your first hosted version to publish this pack.";
            VersionsEmptyLabel.Visibility = Visibility.Visible;
        }
    }

    private void OnSharePackToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress || _pack is null) return;
        var enabled = SharePackBox.IsChecked == true;
        App.State.ResourcePacks.SetSharingEnabled(_pack.Key, enabled);
        _entry = App.State.ResourcePacks.GetOrCreate(_pack.Key);
        ConfigureSharingState();
        StatusLabel.Text = enabled ? "Sharing enabled — upload to publish." : "Sharing controls hidden.";
    }

    private void OnSaveName(object sender, RoutedEventArgs e)
    {
        if (_pack is null) return;
        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) { StatusLabel.Text = "Name cannot be empty."; return; }
        App.State.ResourcePacks.Rename(_pack.Key, name);
        PackNameLabel.Text = name;
        StatusLabel.Text = "Name saved.";
    }

    private void OnSaveOverview(object sender, RoutedEventArgs e)
    {
        if (_pack is null) return;
        var visibility = SelectedVisibility();
        App.State.ResourcePacks.UpdateOverview(_pack.Key, SummaryBox.Text, DescriptionBox.Text, visibility);
        App.State.ResourcePacks.SetSharingEnabled(_pack.Key, SharePackBox.IsChecked == true);
        _entry = App.State.ResourcePacks.GetOrCreate(_pack.Key);
        ConfigureOverviewEditor();
        OverviewSaveStatus.Text = "Overview saved.";
    }

    private async void OnShareOrUploadZip(object sender, RoutedEventArgs e)
    {
        if (_pack is null || !File.Exists(_pack.FilePath)) return;
        SaveOverviewToSettings();

        var versionDlg = new SimpleInputDialog(
            "Upload resource pack version",
            "Version string (e.g. 1.0.0):",
            DateTimeOffset.Now.ToString("yyyy.MM.dd.HHmm"))
        { Owner = _shell };
        if (versionDlg.ShowDialog() != true || string.IsNullOrWhiteSpace(versionDlg.Result)) return;

        UploadHostedButton.IsEnabled = false;
        StatusLabel.Text = "Uploading hosted version...";
        try
        {
            var hostedId = await EnsureHostedResourcePackAsync();

            var meta = new CreateResourcePackVersionRequest(
                versionDlg.Result.Trim(),
                null,
                "release",
                Path.GetFileName(_pack.FilePath),
                SourceMinecraftVersion());

            await App.State.Api.UploadResourcePackVersionAsync(hostedId, _pack.FilePath, meta);
            StatusLabel.Text = "Uploaded.";
            HostedStatusLabel.Text = $"Linked to hosted pack {hostedId}.";
            await LoadHostedVersionsAsync();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Upload failed: " + ex.Message;
        }
        finally
        {
            UploadHostedButton.IsEnabled = true;
        }
    }

    private async void OnEditHostedPermissions(object sender, RoutedEventArgs e)
    {
        if (_pack is null) return;
        _entry ??= App.State.ResourcePacks.GetOrCreate(_pack.Key);
        if (_entry.HostedResourcePackId is not Guid hid)
        {
            StatusLabel.Text = "Publish this pack before managing collaborators.";
            return;
        }

        try
        {
            var detail = await App.State.Api.GetResourcePackAsync(hid);
            new PermissionsDialog(detail) { Owner = _shell }.ShowDialog();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Could not load permissions: " + ex.Message;
        }
    }

    private void SaveOverviewToSettings()
    {
        if (_pack is null) return;
        App.State.ResourcePacks.UpdateOverview(_pack.Key, SummaryBox.Text, DescriptionBox.Text, SelectedVisibility());
        App.State.ResourcePacks.SetSharingEnabled(_pack.Key, true);
        _entry = App.State.ResourcePacks.GetOrCreate(_pack.Key);
    }

    private async Task<Guid> EnsureHostedResourcePackAsync()
    {
        if (_pack is null) throw new InvalidOperationException("Resource pack missing.");
        _entry ??= App.State.ResourcePacks.GetOrCreate(_pack.Key);

        if (_entry.HostedResourcePackId is Guid existing)
        {
            await App.State.Api.UpdateResourcePackAsync(existing, new UpdateResourcePackRequest(
                _pack.DisplayName,
                string.IsNullOrWhiteSpace(SummaryBox.Text) ? null : SummaryBox.Text.Trim(),
                string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text.Trim(),
                SelectedVisibility()));
            return existing;
        }

        var created = await App.State.Api.CreateResourcePackAsync(new CreateResourcePackRequest(
            _pack.DisplayName,
            string.IsNullOrWhiteSpace(SummaryBox.Text) ? null : SummaryBox.Text.Trim(),
            string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text.Trim(),
            SelectedVisibility(),
            SourceMinecraftVersion()));
        App.State.ResourcePacks.LinkHostedResourcePack(_pack.Key, created.Id);
        _entry = App.State.ResourcePacks.GetOrCreate(_pack.Key);
        return created.Id;
    }

    private PackVisibility SelectedVisibility()
    {
        var text = (VisibilityBox.SelectedItem as ComboBoxItem)?.Content as string ?? "Private";
        return Enum.TryParse<PackVisibility>(text, out var visibility) ? visibility : PackVisibility.Private;
    }

    private async void OnRefreshHostedVersions(object sender, RoutedEventArgs e) =>
        await LoadHostedVersionsAsync();

    private async Task LoadHostedVersionsAsync()
    {
        if (_entry?.HostedResourcePackId is not Guid hid)
        {
            _versionRows.Clear();
            VersionsEmptyLabel.Text = "Upload the first hosted version to publish.";
            VersionsEmptyLabel.Visibility = IsSharingEnabled() ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        RefreshVersionsButton.IsEnabled = false;
        try
        {
            var detail = await App.State.Api.GetResourcePackAsync(hid);
            _versionRows.Clear();
            foreach (var v in detail.Versions.OrderByDescending(v => v.PublishedAt))
                _versionRows.Add(HostedRpVersionDisplayRow.From(v));

            VersionsEmptyLabel.Text = "No hosted versions yet.";
            VersionsEmptyLabel.Visibility = _versionRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            HostedStatusLabel.Text = $"Linked · {detail.Versions.Count} zip version(s).";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Could not refresh versions: " + ex.Message;
        }
        finally
        {
            RefreshVersionsButton.IsEnabled = _entry?.HostedResourcePackId is Guid;
        }
    }

    private string? SourceMinecraftVersion() =>
        _pack is null
            ? null
            : _allPacks.FirstOrDefault(p => p.Id == _pack.SourcePackId)?.MinecraftVersion;

    private void OnAllPacksToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress || _pack is null) return;
        var all = AllPacksBox.IsChecked == true;
        App.State.ResourcePacks.SetCompatibleWithAll(_pack.Key, all);
        _ = ReloadAsync();
    }

    private void OnPackCompatibilityChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress || _pack is null) return;
        if (sender is not CheckBox cb || cb.Tag is not Guid packId) return;
        App.State.ResourcePacks.SetCompatible(_pack.Key, packId, cb.IsChecked == true);
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (_pack is null || !File.Exists(_pack.FilePath)) return;
        Process.Start(new ProcessStartInfo { FileName = Path.GetDirectoryName(_pack.FilePath) ?? "", UseShellExecute = true });
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
      : bytes >= 1024L * 1024 ? $"{bytes / (1024.0 * 1024):F1} MB"
      : bytes >= 1024 ? $"{bytes / 1024.0:F0} KB"
      : $"{bytes} B";
}

public sealed class HostedRpVersionDisplayRow
{
    public Guid Id { get; init; }
    public string VersionString { get; init; } = "";
    public string MetaLabel { get; init; } = "";
    public string McVersionLabel { get; init; } = "";

    public static HostedRpVersionDisplayRow From(HostedResourcePackVersionInfo v) => new()
    {
        Id = v.Id,
        VersionString = v.VersionString,
        MetaLabel = $"{v.FileName} · {FormatSize(v.FileSize)} · {v.PublishedAt:g}",
        McVersionLabel = string.IsNullOrWhiteSpace(v.McVersionsCsv) ? "Any MC" : $"MC {v.McVersionsCsv.Split(',').FirstOrDefault()?.Trim()}"
    };

    private static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024):0.#} MB"
      : $"{bytes / 1024.0:0.#} KB";
}
