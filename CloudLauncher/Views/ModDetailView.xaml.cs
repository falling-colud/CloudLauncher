using System.IO;
using System.Windows;
using System.Windows.Controls;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

public partial class ModDetailView : Page
{
    private readonly MainWindow _shell;
    private readonly Guid _modId;
    private HostedModDetail? _mod;

    public ModDetailView(MainWindow shell, Guid modId)
    {
        InitializeComponent();
        _shell = shell;
        _modId = modId;
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        try
        {
            _mod = await App.State.Api.GetModAsync(_modId);
            ApplyMod();
        }
        catch (Exception ex) { StatusLabel.Text = "Failed to load: " + ex.Message; }
    }

    private void ApplyMod()
    {
        if (_mod is null) return;
        ModNameLabel.Text = _mod.Name;
        VisibilityLabel.Text = _mod.Visibility.ToString();
        MetaLabel.Text = $"by {_mod.OwnerUsername} · {(_mod.LoadersCsv ?? "(no loader)")}";

        var isOwner = App.State.Settings.UserId == _mod.OwnerId;
        SummaryBox.Text = _mod.Summary ?? "";
        DescriptionBox.Text = _mod.Description ?? "";
        VisibilityBox.SelectedIndex = (int)_mod.Visibility;
        SummaryBox.IsReadOnly = !isOwner;
        DescriptionBox.IsReadOnly = !isOwner;
        VisibilityBox.IsEnabled = isOwner;
        SaveButton.Visibility = isOwner ? Visibility.Visible : Visibility.Collapsed;
        PermissionsButton.Visibility = isOwner ? Visibility.Visible : Visibility.Collapsed;
        UploadVersionButton.Visibility = isOwner ? Visibility.Visible : Visibility.Collapsed;

        VersionsList.ItemsSource = _mod.Versions.Select(v => new VersionRowVm
        {
            Id = v.Id,
            VersionString = v.VersionString,
            ReleaseChannel = v.ReleaseChannel,
            MetaLabel = $"{v.FileName} · {FormatSize(v.FileSize)} · {v.PublishedAt:yyyy-MM-dd}"
        }).ToList();
        VersionsEmpty.Visibility = _mod.Versions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_mod is null) return;
        var visText = (VisibilityBox.SelectedItem as ComboBoxItem)?.Content as string ?? "Private";
        var vis = Enum.TryParse<PackVisibility>(visText, out var v) ? v : PackVisibility.Private;

        var req = new UpdateModRequest(
            null,
            SummaryBox.Text,
            DescriptionBox.Text,
            vis);

        SaveStatus.Text = "Saving…";
        SaveButton.IsEnabled = false;
        try
        {
            await App.State.Api.UpdateModAsync(_mod.Id, req);
            SaveStatus.Text = "Saved.";
            await ReloadAsync();
        }
        catch (Exception ex) { SaveStatus.Text = "Error: " + ex.Message; }
        finally { SaveButton.IsEnabled = true; }
    }

    private void OnEditPermissions(object sender, RoutedEventArgs e)
    {
        if (_mod is null) return;
        new PermissionsDialog(_mod) { Owner = _shell }.ShowDialog();
        _ = ReloadAsync();
    }

    private async void OnUploadVersion(object sender, RoutedEventArgs e)
    {
        if (_mod is null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Pick mod jar to upload",
            Filter = "Mod jars (*.jar)|*.jar|All files|*"
        };
        if (dlg.ShowDialog(_shell) != true) return;

        var versionDlg = new SimpleInputDialog("Upload version", "Version string (e.g. 1.0.0):", "1.0.0") { Owner = _shell };
        if (versionDlg.ShowDialog() != true || string.IsNullOrWhiteSpace(versionDlg.Result)) return;
        var versionString = versionDlg.Result;

        StatusLabel.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
        StatusLabel.Text = "Uploading…";
        UploadVersionButton.IsEnabled = false;
        try
        {
            var meta = new CreateModVersionRequest(
                versionString.Trim(),
                null,
                "release",
                Path.GetFileName(dlg.FileName),
                _mod.McVersionsCsv,
                _mod.LoadersCsv);
            await App.State.Api.UploadModVersionAsync(_mod.Id, dlg.FileName, meta);
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
        if (_mod is null) return;
        if (sender is not Button b || b.Tag is not VersionRowVm row) return;
        b.IsEnabled = false;
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = row.VersionString + ".jar",
                Filter = "Mod jars (*.jar)|*.jar|All files|*"
            };
            if (dlg.ShowDialog(_shell) != true) { b.IsEnabled = true; return; }

            await using var stream = await App.State.Api.DownloadModVersionAsync(_mod.Id, row.Id);
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

    public class VersionRowVm
    {
        public Guid Id { get; set; }
        public string VersionString { get; set; } = "";
        public string ReleaseChannel { get; set; } = "release";
        public string MetaLabel { get; set; } = "";
    }
}
