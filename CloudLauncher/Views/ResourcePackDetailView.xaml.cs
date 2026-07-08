using System.IO;
using System.Windows;
using System.Windows.Controls;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

public partial class ResourcePackDetailView : Page
{
    private readonly MainWindow _shell;
    private readonly Guid _packId;
    private HostedResourcePackDetail? _pack;

    public ResourcePackDetailView(MainWindow shell, Guid packId)
    {
        InitializeComponent();
        _shell = shell;
        _packId = packId;
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        try
        {
            _pack = await App.State.Api.GetResourcePackAsync(_packId);
            ApplyPack();
        }
        catch (Exception ex) { StatusLabel.Text = "Failed to load: " + ex.Message; }
    }

    private void ApplyPack()
    {
        if (_pack is null) return;
        PackNameLabel.Text = _pack.Name;
        VisibilityLabel.Text = _pack.Visibility.ToString();
        MetaLabel.Text = $"by {_pack.OwnerUsername}"
            + (string.IsNullOrWhiteSpace(_pack.McVersionsCsv) ? ""
                : " · MC " + _pack.McVersionsCsv.Split(',').FirstOrDefault());

        var isOwner = App.State.Settings.UserId == _pack.OwnerId;
        SummaryBox.Text = _pack.Summary ?? "";
        DescriptionBox.Text = _pack.Description ?? "";
        VisibilityBox.SelectedIndex = (int)_pack.Visibility;
        SummaryBox.IsReadOnly = !isOwner;
        DescriptionBox.IsReadOnly = !isOwner;
        VisibilityBox.IsEnabled = isOwner;
        SaveButton.Visibility = isOwner ? Visibility.Visible : Visibility.Collapsed;
        PermissionsButton.Visibility = isOwner ? Visibility.Visible : Visibility.Collapsed;
        UploadVersionButton.Visibility = isOwner ? Visibility.Visible : Visibility.Collapsed;

        VersionsList.ItemsSource = _pack.Versions.Select(v => new VersionRowVm
        {
            Id = v.Id,
            VersionString = v.VersionString,
            ReleaseChannel = v.ReleaseChannel,
            MetaLabel = $"{v.FileName} · {FormatSize(v.FileSize)} · {v.PublishedAt:yyyy-MM-dd}"
        }).ToList();
        VersionsEmpty.Visibility = _pack.Versions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_pack is null) return;
        var visText = (VisibilityBox.SelectedItem as ComboBoxItem)?.Content as string ?? "Private";
        var vis = Enum.TryParse<PackVisibility>(visText, out var v) ? v : PackVisibility.Private;

        var req = new UpdateResourcePackRequest(
            null,
            SummaryBox.Text,
            DescriptionBox.Text,
            vis);

        SaveStatus.Text = "Saving…";
        SaveButton.IsEnabled = false;
        try
        {
            await App.State.Api.UpdateResourcePackAsync(_pack.Id, req);
            SaveStatus.Text = "Saved.";
            await ReloadAsync();
        }
        catch (Exception ex) { SaveStatus.Text = "Error: " + ex.Message; }
        finally { SaveButton.IsEnabled = true; }
    }

    private void OnEditPermissions(object sender, RoutedEventArgs e)
    {
        if (_pack is null) return;
        new PermissionsDialog(_pack) { Owner = _shell }.ShowDialog();
        _ = ReloadAsync();
    }

    private async void OnUploadVersion(object sender, RoutedEventArgs e)
    {
        if (_pack is null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Pick resource pack .zip",
            Filter = "Resource packs (*.zip)|*.zip|All files|*"
        };
        if (dlg.ShowDialog(_shell) != true) return;

        var versionDlg = new SimpleInputDialog("Upload version", "Version string (e.g. 1.0.0):", "1.0.0") { Owner = _shell };
        if (versionDlg.ShowDialog() != true || string.IsNullOrWhiteSpace(versionDlg.Result)) return;

        StatusLabel.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
        StatusLabel.Text = "Uploading…";
        UploadVersionButton.IsEnabled = false;
        try
        {
            var meta = new CreateResourcePackVersionRequest(
                versionDlg.Result.Trim(),
                null,
                "release",
                Path.GetFileName(dlg.FileName),
                _pack.McVersionsCsv);
            await App.State.Api.UploadResourcePackVersionAsync(_pack.Id, dlg.FileName, meta);
            StatusLabel.Text = "Uploaded.";
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            StatusLabel.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            StatusLabel.Text = "Upload failed: " + ex.Message;
        }
        finally { UploadVersionButton.IsEnabled = true; }
    }

    private async void OnDownloadVersionTemp(object sender, RoutedEventArgs e)
    {
        if (_pack is null) return;
        if (sender is not Button b || b.Tag is not VersionRowVm row) return;
        b.IsEnabled = false;
        try
        {
            var fileName = row.VersionString;
            if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                fileName += ".zip";
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = fileName,
                Filter = "Zip (*.zip)|*.zip|All files|*"
            };
            if (dlg.ShowDialog(_shell) != true) { b.IsEnabled = true; return; }

            await using var stream = await App.State.Api.DownloadResourcePackVersionAsync(_pack.Id, row.Id);
            await using var fs = File.Create(dlg.FileName);
            await stream.CopyToAsync(fs);
            StatusLabel.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");
            StatusLabel.Text = "Saved to " + dlg.FileName;
        }
        catch (Exception ex)
        {
            StatusLabel.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            StatusLabel.Text = "Download failed: " + ex.Message;
        }
        finally { b.IsEnabled = true; }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB"
    };

    public sealed class VersionRowVm
    {
        public Guid Id { get; set; }
        public string VersionString { get; set; } = "";
        public string ReleaseChannel { get; set; } = "release";
        public string MetaLabel { get; set; } = "";
    }
}
