using System.IO;
using System.Windows;
using System.Windows.Controls;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// The management page for a hosted resource pack: its overview, who can see it, and the zip
/// versions uploaded against it.
/// </summary>
/// <remarks>
/// Everything editable here is owner-only, and the owner check is the single <c>isOwner</c> flag
/// computed in <see cref="ApplyPack"/> — a collaborator sees the same page read-only rather than a
/// different one.
/// </remarks>
public partial class ResourcePackDetailView : Page
{
    private readonly MainWindow _shell;
    private readonly Guid _packId;
    private HostedResourcePackDetail? _pack;
    private bool _isOwner;

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
        catch (Exception ex) { Fail("Failed to load: " + ex.Message); }
    }

    private void ApplyPack()
    {
        if (_pack is null) return;
        PackNameLabel.Text = _pack.Name;
        VisibilityLabel.Text = _pack.Visibility.ToString();
        MetaLabel.Text = $"by {_pack.OwnerUsername}"
            + (string.IsNullOrWhiteSpace(_pack.McVersionsCsv) ? ""
                : " · MC " + _pack.McVersionsCsv.Split(',').FirstOrDefault());

        _isOwner = App.State.Settings.UserId == _pack.OwnerId;
        NameBox.Text = _pack.Name;
        SummaryBox.Text = _pack.Summary ?? "";
        DescriptionBox.Text = _pack.Description ?? "";
        McVersionsBox.Text = _pack.McVersionsCsv ?? "";
        VisibilityBox.SelectedIndex = (int)_pack.Visibility;

        NameBox.IsReadOnly = !_isOwner;
        SummaryBox.IsReadOnly = !_isOwner;
        DescriptionBox.IsReadOnly = !_isOwner;
        McVersionsBox.IsReadOnly = !_isOwner;
        VisibilityBox.IsEnabled = _isOwner;
        SaveButton.Visibility = _isOwner ? Visibility.Visible : Visibility.Collapsed;
        PermissionsButton.Visibility = _isOwner ? Visibility.Visible : Visibility.Collapsed;
        DeletePackButton.Visibility = _isOwner ? Visibility.Visible : Visibility.Collapsed;
        UploadVersionButton.Visibility = _isOwner ? Visibility.Visible : Visibility.Collapsed;

        VersionsList.ItemsSource = _pack.Versions
            .OrderByDescending(v => v.PublishedAt)
            .Select(v => new VersionRowVm
            {
                Id = v.Id,
                VersionString = v.VersionString,
                ReleaseChannel = v.ReleaseChannel,
                FileName = v.FileName,
                MetaLabel = $"{v.FileName} · {FormatSize(v.FileSize)} · {v.PublishedAt:yyyy-MM-dd}",
                Tooltip = BuildVersionTooltip(v)
            })
            .ToList();
        VersionsEmpty.Visibility = _pack.Versions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The changelog belongs in the tooltip: it is the one thing that tells a collaborator
    /// whether this version is worth taking, and the row has no space for it.</summary>
    private static string BuildVersionTooltip(HostedResourcePackVersionInfo v)
    {
        var lines = new List<string>
        {
            $"{v.VersionString} · {v.ReleaseChannel}",
            $"{v.FileName} · {FormatSize(v.FileSize)}",
            $"Published {v.PublishedAt.LocalDateTime:g}"
        };
        if (!string.IsNullOrWhiteSpace(v.McVersionsCsv)) lines.Add("MC " + v.McVersionsCsv);
        if (!string.IsNullOrWhiteSpace(v.Changelog)) lines.Add("\n" + v.Changelog!.Trim());
        return string.Join("\n", lines);
    }

    private void Okay(string text)
    {
        StatusLabel.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        StatusLabel.Text = text;
    }

    private void Fail(string text)
    {
        StatusLabel.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        StatusLabel.Text = text;
    }

    // ── overview ─────────────────────────────────────────────────────────────

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_pack is null) return;
        try
        {
            var name = NameBox.Text.Trim();
            if (name.Length is 0 or > 128)
            {
                SaveStatus.Text = "Name must be 1–128 characters.";
                return;
            }

            var visText = (VisibilityBox.SelectedItem as ComboBoxItem)?.Content as string ?? "Private";
            var vis = Enum.TryParse<PackVisibility>(visText, out var v) ? v : PackVisibility.Private;
            var mc = McVersionsBox.Text.Trim();

            // The name and the MC versions used to be passed as null here, silently discarding any
            // edit — a misnamed pack was permanent even though the server has always accepted both.
            var req = new UpdateResourcePackRequest(
                name,
                SummaryBox.Text,
                DescriptionBox.Text,
                vis,
                mc.Length == 0 ? null : mc);

            SaveStatus.Text = "Saving…";
            SaveButton.IsEnabled = false;
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

    /// <summary>
    /// Deletes the hosted pack and every version uploaded to it.
    /// </summary>
    /// <remarks>Also clears the hosting link from any locally installed zip that pointed here, so a
    /// pack on disk cannot go on claiming to be published to something that no longer exists.</remarks>
    private async void OnDeletePack(object sender, RoutedEventArgs e)
    {
        if (_pack is null || !_isOwner) return;
        try
        {
            if (!await AppDialog.ConfirmAsync(_shell,
                    "Delete resource pack",
                    $"Delete '{_pack.Name}' and its {_pack.Versions.Count} uploaded version(s) from hosting?\n\n"
                    + "Everyone you shared it with loses access. Copies already installed in an instance are untouched.",
                    "Delete", "Cancel", danger: true))
                return;

            DeletePackButton.IsEnabled = false;
            await App.State.Api.DeleteResourcePackAsync(_pack.Id);

            var changed = false;
            foreach (var entry in App.State.Settings.ResourcePacks.Values
                         .Where(v => v.HostedResourcePackId == _pack.Id))
            {
                entry.HostedResourcePackId = null;
                changed = true;
            }
            if (changed) App.State.Settings.Save();

            _shell.CloseSidePanel();
        }
        catch (Exception ex)
        {
            Fail("Delete failed: " + ex.Message);
            DeletePackButton.IsEnabled = true;
        }
    }

    // ── versions ─────────────────────────────────────────────────────────────

    private async void OnUploadVersion(object sender, RoutedEventArgs e)
    {
        if (_pack is null) return;
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Pick resource pack .zip",
                Filter = "Resource packs (*.zip)|*.zip|All files|*"
            };
            if (dlg.ShowDialog(_shell) != true) return;

            var meta = new UploadResourcePackVersionDialog(dlg.FileName, NextVersionSuggestion(), _pack.McVersionsCsv)
            { Owner = _shell };
            if (meta.ShowDialog() != true || meta.Result is null) return;

            StatusLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            StatusLabel.Text = "Uploading…";
            UploadVersionButton.IsEnabled = false;

            await App.State.Api.UploadResourcePackVersionAsync(_pack.Id, dlg.FileName, meta.Result);
            Okay("Uploaded.");
            await ReloadAsync();
        }
        catch (Exception ex) { Fail("Upload failed: " + ex.Message); }
        finally { UploadVersionButton.IsEnabled = true; }
    }

    /// <summary>A date stamp, or the newest version string already uploaded so the user edits rather
    /// than retypes.</summary>
    private string NextVersionSuggestion() =>
        _pack?.Versions.OrderByDescending(v => v.PublishedAt).FirstOrDefault()?.VersionString
        ?? DateTimeOffset.Now.ToString("yyyy.MM.dd.HHmm");

    private void OnVersionMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        foreach (var item in menu.Items.OfType<FrameworkElement>())
            if (item.Name is "VersionCtxDelete" or "VersionCtxDeleteSeparator")
                item.Visibility = _isOwner ? Visibility.Visible : Visibility.Collapsed;
    }

    private static VersionRowVm? RowFrom(object sender)
    {
        DependencyObject? p = sender as DependencyObject;
        while (p is MenuItem m) p = m.Parent as MenuItem ?? m.Parent;
        return p is ContextMenu { PlacementTarget: FrameworkElement fx } ? fx.DataContext as VersionRowVm : null;
    }

    private async void OnCtxSaveVersion(object sender, RoutedEventArgs e)
    {
        if (RowFrom(sender) is VersionRowVm row) await SaveVersionAsync(row, null);
    }

    private void OnCtxCopyVersionLink(object sender, RoutedEventArgs e)
    {
        if (_pack is null || RowFrom(sender) is not VersionRowVm row) return;
        var url = $"{App.State.Settings.ServerUrl.TrimEnd('/')}/resourcepacks/{_pack.Id}/files/{row.Id}";
        if (ClipboardHelper.TrySetText(url)) Okay("Download link copied.");
    }

    private void OnCtxCopyVersionString(object sender, RoutedEventArgs e)
    {
        if (RowFrom(sender) is VersionRowVm row && ClipboardHelper.TrySetText(row.VersionString))
            Okay("Version string copied.");
    }

    /// <summary>
    /// Deletes one uploaded version.
    /// </summary>
    /// <remarks>Uploading the wrong zip used to be permanent — nothing in the UI could take a version
    /// back down. The blob itself stays: the store is content-addressed and another pack may share
    /// it.</remarks>
    private async void OnCtxDeleteVersion(object sender, RoutedEventArgs e)
    {
        if (_pack is null || !_isOwner) return;
        try
        {
            if (RowFrom(sender) is not VersionRowVm row) return;
            if (!await AppDialog.ConfirmAsync(_shell,
                    "Delete version",
                    $"Delete version {row.VersionString} ({row.FileName})?\n\n"
                    + "Anyone who has not downloaded it yet will no longer be able to.",
                    "Delete version", "Cancel", danger: true))
                return;

            await App.State.Api.DeleteResourcePackVersionAsync(_pack.Id, row.Id);
            Okay($"Deleted version {row.VersionString}.");
            await ReloadAsync();
        }
        catch (Exception ex) { Fail("Delete failed: " + ex.Message); }
    }

    private async void OnDownloadVersionTemp(object sender, RoutedEventArgs e)
    {
        if (_pack is null) return;
        if (sender is not Button b || b.Tag is not VersionRowVm row) return;
        b.IsEnabled = false;
        try { await SaveVersionAsync(row, b); }
        finally { b.IsEnabled = true; }
    }

    private async Task SaveVersionAsync(VersionRowVm row, Button? button)
    {
        if (_pack is null) return;
        try
        {
            var fileName = string.IsNullOrWhiteSpace(row.FileName) ? row.VersionString : row.FileName;
            if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) fileName += ".zip";

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = fileName,
                Filter = "Zip (*.zip)|*.zip|All files|*"
            };
            if (dlg.ShowDialog(_shell) != true) return;

            StatusLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            StatusLabel.Text = "Downloading…";

            await using var stream = await App.State.Api.DownloadResourcePackVersionAsync(_pack.Id, row.Id);
            await using var fs = File.Create(dlg.FileName);
            await stream.CopyToAsync(fs);
            Okay("Saved to " + dlg.FileName);
        }
        catch (Exception ex) { Fail("Download failed: " + ex.Message); }
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
        public string FileName { get; set; } = "";
        public string MetaLabel { get; set; } = "";
        public string Tooltip { get; set; } = "";
    }
}
