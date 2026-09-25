using System.IO;
using System.Windows;
using System.Windows.Controls;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>The management page for a hosted resource pack: overview, visibility and uploaded
/// versions.</summary>
/// <remarks>Each control is enabled by the grant the server checks for it: owner for the overview
/// and deletes, <see cref="PackPermissions.UploadShared"/> to upload,
/// <see cref="PackPermissions.ManageCollaborators"/> to share and
/// <see cref="PackPermissions.Download"/> to save a version. A disabled control says why.</remarks>
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
        // Non-owners get no Save button; its status line says whose pack this is and what they may do.
        if (!_isOwner) SaveStatus.Text = AccessNote();
        ApplyPermissions();

        var downloadHint = CanDownload
            ? "Download to a chosen path"
            : $"You can see this pack but not download it. Ask {_pack.OwnerUsername} for download access.";
        VersionsList.ItemsSource = _pack.Versions
            .OrderByDescending(v => v.PublishedAt)
            .Select(v => new VersionRowVm
            {
                Id = v.Id,
                VersionString = v.VersionString,
                ReleaseChannel = v.ReleaseChannel,
                FileName = v.FileName,
                // TimeFormat shows local time. PublishedAt arrives with a +00:00 offset, so formatting it
                // directly would print UTC and disagree with the tooltip.
                MetaLabel = $"{v.FileName} · {FormatSize(v.FileSize)} · {TimeFormat.Date(v.PublishedAt)}",
                Tooltip = BuildVersionTooltip(v),
                CanDownload = CanDownload,
                DownloadHint = downloadHint
            })
            .ToList();
        VersionsEmpty.Visibility = _pack.Versions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── permissions ──────────────────────────────────────────────────────────

    private PackPermissions Granted => _pack?.EffectivePermissions ?? PackPermissions.None;
    private bool CanManage => _isOwner || Granted.HasFlag(PackPermissions.ManageCollaborators);
    private bool CanUpload => _isOwner || Granted.HasFlag(PackPermissions.UploadShared);
    private bool CanDownload => _isOwner || Granted.HasFlag(PackPermissions.Download);

    /// <summary>True for someone on the pack's own collaborator list, the only access a person can
    /// give back from here.</summary>
    private bool IsDirectCollaborator =>
        !_isOwner && _pack is { } pack && App.State.Settings.UserId is { } me
        && pack.Collaborators.Any(c => c.UserId == me);

    /// <summary>Turns each action on or off by the grant it needs, and says which one when it is off.</summary>
    private void ApplyPermissions()
    {
        if (_pack is null) return;
        var owner = _pack.OwnerUsername;

        PermissionsButton.Visibility = Visibility.Visible;
        PermissionsButton.IsEnabled = CanManage;
        PermissionsButton.ToolTip = CanManage
            ? "Choose who can see, download, upload to or manage this pack"
            : $"Only {owner}, or somebody {owner} gave full access to, can change who has this pack.";

        UploadVersionButton.Visibility = Visibility.Visible;
        UploadVersionButton.IsEnabled = CanUpload;
        UploadVersionButton.ToolTip = CanUpload
            ? "Publish a new zip as the next version of this pack"
            : $"You can see this pack but not upload versions of it. Ask {owner} for upload access.";

        DeletePackButton.Visibility = Visibility.Visible;
        DeletePackButton.IsEnabled = _isOwner;
        DeletePackButton.ToolTip = _isOwner
            ? "Removes the hosted pack and every uploaded version"
            : $"Only {owner} can delete this pack.";

        LeaveButton.Visibility = IsDirectCollaborator ? Visibility.Visible : Visibility.Collapsed;
        LeaveButton.IsEnabled = true;
        LeaveButton.ToolTip = $"Give back the access {owner} gave you. Copies already installed stay where they are.";
    }

    /// <summary>One line for somebody who is not the owner: whose pack this is and what they may do.</summary>
    private string AccessNote()
    {
        if (_pack is null || _isOwner) return "";
        var can = new List<string>();
        if (CanDownload) can.Add("download it");
        if (CanUpload) can.Add("upload versions");
        if (CanManage) can.Add("choose who has it");
        var what = can.Count switch
        {
            0 => "see it",
            1 => can[0],
            _ => string.Join(", ", can.Take(can.Count - 1)) + " and " + can[^1]
        };
        return $"Owned by {_pack.OwnerUsername}. You can {what}; only the owner can change these details.";
    }

    /// <summary>Gives back the access the owner granted, after asking.</summary>
    /// <remarks>Re-reads the pack afterwards instead of closing, since a team or public visibility may
    /// still give access. The page closes only when nothing is left.</remarks>
    private async void OnLeave(object sender, RoutedEventArgs e)
    {
        if (_pack is null || App.State.Settings.UserId is not { } me || !IsDirectCollaborator) return;
        var pack = _pack;
        try
        {
            if (!await AppDialog.ConfirmAsync(_shell, "Leave resource pack",
                    $"Leave '{pack.Name}'? You lose the access {pack.OwnerUsername} gave you, and only they "
                    + "can give it back. Copies already installed in an instance stay where they are. If one "
                    + "of your teams also has this pack, you keep what the team gives.",
                    "Leave", "Cancel", danger: true))
                return;

            LeaveButton.IsEnabled = false;
            await App.State.Api.RemoveResourcePackCollaboratorAsync(pack.Id, me);
        }
        catch (Exception ex)
        {
            LeaveButton.IsEnabled = true;
            Fail("Could not leave: " + ContentBundleService.Explain(ex, ex.Message));
            return;
        }

        try
        {
            _pack = await App.State.Api.GetResourcePackAsync(_packId);
            ApplyPack();
            Okay($"You left {pack.Name}. What is left comes from a team or from it being public.");
        }
        catch (Exception ex)
        {
            // Nothing left to show: the pack was only ever shared with this person directly.
            AppLog.LogError("ResourcePackDetailView.Leave", ex);
            _shell.CloseSidePanel();
        }
    }

    /// <summary>Row tooltip with the version details and changelog, which the row has no room for.</summary>
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
                SaveStatus.Text = "Name must be 1-128 characters.";
                return;
            }

            var visText = (VisibilityBox.SelectedItem as ComboBoxItem)?.Content as string ?? "Private";
            var vis = Enum.TryParse<PackVisibility>(visText, out var v) ? v : PackVisibility.Private;
            var mc = McVersionsBox.Text.Trim();

            var req = new UpdateResourcePackRequest(
                name,
                SummaryBox.Text,
                DescriptionBox.Text,
                vis,
                mc.Length == 0 ? null : mc);

            SaveStatus.Text = "Saving...";
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
        if (_pack is null || !CanManage) return;
        new PermissionsDialog(_pack) { Owner = _shell }.ShowDialog();
        _ = ReloadAsync();
    }

    /// <summary>
    /// Deletes the hosted pack and every version uploaded to it.
    /// </summary>
    /// <remarks>Also clears the hosting link from any local zip that pointed here.</remarks>
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
        if (_pack is null || !CanUpload) return;
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
            StatusLabel.Text = "Uploading...";
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
        ?? TimeFormat.VersionStamp(DateTimeOffset.Now);

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
    /// <remarks>The blob stays: the store is content-addressed and another pack may share it.</remarks>
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
        if (!row.CanDownload) { Fail(row.DownloadHint); return; }
        try
        {
            var fileName = string.IsNullOrWhiteSpace(row.FileName) ? row.VersionString : row.FileName;
            if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) fileName += ".zip";
            // The name is the uploader's; the dialog only ever suggests a plain file name.
            if (!PathSafety.IsSafeFileName(fileName)) fileName = "resourcepack.zip";

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = fileName,
                Filter = "Zip (*.zip)|*.zip|All files|*"
            };
            if (dlg.ShowDialog(_shell) != true) return;

            StatusLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            StatusLabel.Text = "Downloading...";

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

        /// <summary>Whether this person may download the file. The server would answer 403, so the row's
        /// button is disabled with a reason instead.</summary>
        public bool CanDownload { get; set; } = true;
        public string DownloadHint { get; set; } = "Download to a chosen path";
    }
}
