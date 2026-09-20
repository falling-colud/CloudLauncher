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
            // A pack may be an unpacked folder rather than a zip — Minecraft loads both.
            if (!File.Exists(_pack.FilePath) && !Directory.Exists(_pack.FilePath))
            {
                StatusLabel.Text = "This resource pack was deleted on disk.";
                return;
            }

            _entry = App.State.ResourcePacks.GetOrCreate(_pack.Key);

            _suppress = true;
            try
            {
                PackNameLabel.Text = _pack.DisplayName;
                NameBox.Text = _pack.DisplayName;
                SourcePackLabel.Text = _pack.SourcePackName.ToUpperInvariant();
                MetaLabel.Text = $"Modified {_pack.LastModified.LocalDateTime:g} · {FormatSize(_pack.SizeBytes)}";
                FilePathLabel.Text = _pack.FilePath;
                ApplyPackMeta(_pack);
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

    /// <summary>
    /// Fills in what the pack itself declares — its icon, description and pack format — plus whether
    /// the instance currently has it turned on.
    /// </summary>
    /// <remarks>Read off the UI thread is not worth it here: this page shows exactly one pack, and
    /// <see cref="ResourcePackService.ReadMeta"/> caches by path and timestamp, so the grid the user
    /// arrived from has usually already paid for it.</remarks>
    private void ApplyPackMeta(ResourcePackInfo pack)
    {
        PackInitialLabel.Text = string.IsNullOrWhiteSpace(pack.DisplayName)
            ? "?"
            : pack.DisplayName.Trim()[..1].ToUpperInvariant();

        var meta = ResourcePackService.ReadMeta(pack.FilePath, pack.IsFolder);
        PackIconImage.Source = meta.Icon;

        var hasDescription = !string.IsNullOrWhiteSpace(meta.Description);
        PackDescriptionCaption.Visibility = hasDescription ? Visibility.Visible : Visibility.Collapsed;
        PackDescriptionLabel.Visibility = hasDescription ? Visibility.Visible : Visibility.Collapsed;
        PackDescriptionLabel.Text = meta.Description ?? "";

        FormatPill.Visibility = meta.PackFormat is null ? Visibility.Collapsed : Visibility.Visible;
        FormatPillText.Text = meta.PackFormat is int pf ? $"pack_format {pf}" : "";

        EnabledPill.Visibility = pack.Enabled ? Visibility.Visible : Visibility.Collapsed;
        EnabledPillText.Text = pack.Priority > 0 ? $"ON · #{pack.Priority}" : "ON";

        var kind = pack.IsFolder ? "PACK FOLDER" : pack.IsLocal ? "LOCAL ONLY" : null;
        KindPill.Visibility = kind is null ? Visibility.Collapsed : Visibility.Visible;
        KindPillText.Text = kind ?? "";
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

    /// <summary>
    /// Publishes the zip as a new hosted version.
    /// </summary>
    /// <remarks>Only a zip can be uploaded: an unpacked pack folder has no single file to post, and
    /// zipping it behind the user's back would upload something they never checked.</remarks>
    private async void OnShareOrUploadZip(object sender, RoutedEventArgs e)
    {
        if (_pack is null) return;
        if (_pack.IsFolder)
        {
            StatusLabel.Text = "This pack is an unpacked folder — zip it first, then upload the zip.";
            return;
        }
        if (!File.Exists(_pack.FilePath)) { StatusLabel.Text = "The zip is no longer on disk."; return; }

        SaveOverviewToSettings();

        var versionDlg = new UploadResourcePackVersionDialog(
            _pack.FilePath,
            DateTimeOffset.Now.ToString("yyyy.MM.dd.HHmm"),
            SourceMinecraftVersion())
        { Owner = _shell };
        if (versionDlg.ShowDialog() != true || versionDlg.Result is null) return;

        UploadHostedButton.IsEnabled = false;
        StatusLabel.Text = "Uploading hosted version…";
        try
        {
            var hostedId = await EnsureHostedResourcePackAsync();
            await App.State.Api.UploadResourcePackVersionAsync(hostedId, _pack.FilePath, versionDlg.Result);
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

    // ── per-version actions ──────────────────────────────────────────────────

    private static HostedRpVersionDisplayRow? VersionFrom(object sender)
    {
        DependencyObject? p = sender as DependencyObject;
        while (p is MenuItem m) p = m.Parent as MenuItem ?? m.Parent;
        return p is ContextMenu { PlacementTarget: FrameworkElement fx }
            ? fx.DataContext as HostedRpVersionDisplayRow
            : null;
    }

    private async void OnCtxSaveVersion(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_entry?.HostedResourcePackId is not Guid hid) return;
            if (VersionFrom(sender) is not HostedRpVersionDisplayRow row) return;

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = string.IsNullOrWhiteSpace(row.FileName) ? row.VersionString + ".zip" : row.FileName,
                Filter = "Zip (*.zip)|*.zip|All files|*"
            };
            if (dlg.ShowDialog(_shell) != true) return;

            StatusLabel.Text = "Downloading…";
            await using var stream = await App.State.Api.DownloadResourcePackVersionAsync(hid, row.Id);
            await using var fs = File.Create(dlg.FileName);
            await stream.CopyToAsync(fs);
            StatusLabel.Text = "Saved to " + dlg.FileName;
        }
        catch (Exception ex) { StatusLabel.Text = "Download failed: " + ex.Message; }
    }

    private void OnCtxCopyVersionLink(object sender, RoutedEventArgs e)
    {
        if (_entry?.HostedResourcePackId is not Guid hid) return;
        if (VersionFrom(sender) is not HostedRpVersionDisplayRow row) return;
        var url = $"{App.State.Settings.ServerUrl.TrimEnd('/')}/resourcepacks/{hid}/files/{row.Id}";
        if (ClipboardHelper.TrySetText(url)) StatusLabel.Text = "Download link copied.";
    }

    /// <summary>Takes one uploaded version back down. Uploading the wrong zip used to be permanent —
    /// nothing in the app could remove a version once it was published.</summary>
    private async void OnCtxDeleteVersion(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_entry?.HostedResourcePackId is not Guid hid) return;
            if (VersionFrom(sender) is not HostedRpVersionDisplayRow row) return;

            if (!await AppDialog.ConfirmAsync(_shell,
                    "Delete version",
                    $"Delete version {row.VersionString}?\n\n"
                    + "Anyone who has not downloaded it yet will no longer be able to.",
                    "Delete version", "Cancel", danger: true))
                return;

            await App.State.Api.DeleteResourcePackVersionAsync(hid, row.Id);
            StatusLabel.Text = $"Deleted version {row.VersionString}.";
            await LoadHostedVersionsAsync();
        }
        catch (Exception ex) { StatusLabel.Text = "Delete failed: " + ex.Message; }
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
        if (_pack is null) return;
        if (!File.Exists(_pack.FilePath) && !Directory.Exists(_pack.FilePath)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + _pack.FilePath + "\"")
            { UseShellExecute = true });
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
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
    public string FileName { get; init; } = "";
    public string MetaLabel { get; init; } = "";
    public string McVersionLabel { get; init; } = "";

    /// <summary>Carries the changelog and channel, which the row itself has no space for and which
    /// are the only things that say whether a version is worth taking.</summary>
    public string Tooltip { get; init; } = "";

    public static HostedRpVersionDisplayRow From(HostedResourcePackVersionInfo v) => new()
    {
        Id = v.Id,
        VersionString = v.VersionString,
        FileName = v.FileName,
        MetaLabel = $"{v.FileName} · {FormatSize(v.FileSize)} · {v.PublishedAt:g}",
        McVersionLabel = string.IsNullOrWhiteSpace(v.McVersionsCsv) ? "Any MC" : $"MC {v.McVersionsCsv.Split(',').FirstOrDefault()?.Trim()}",
        Tooltip = string.Join("\n", new[]
        {
            $"{v.VersionString} · {v.ReleaseChannel}",
            $"{v.FileName} · {FormatSize(v.FileSize)}",
            $"Published {v.PublishedAt.LocalDateTime:g}",
            string.IsNullOrWhiteSpace(v.Changelog) ? "" : "\n" + v.Changelog!.Trim()
        }.Where(x => x.Length > 0))
    };

    private static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024):0.#} MB"
      : $"{bytes / 1024.0:0.#} KB";
}
