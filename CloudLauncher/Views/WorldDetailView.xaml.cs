using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

public partial class WorldDetailView : Page
{
    /// <summary>
    /// The server's per-world upload limit, mirrored from <c>SharedWorldsController.MaxWorldBytes</c>.
    /// </summary>
    /// <remarks>
    /// Duplicated rather than shared because the client cannot reference the server project. It is
    /// only ever used to refuse early: the server is still the authority, and a mismatch here costs a
    /// clear message instead of a silent five-minute upload that ends in "File too large".
    /// </remarks>
    private const long MaxUploadBytes = 256L * 1024 * 1024;

    private readonly MainWindow _shell;
    private string _worldKey;
    private WorldInfo? _world;
    private List<PackSummary> _allPacks = new();
    private readonly ObservableCollection<PackCompatibilityRow> _packRows = new();
    private readonly ObservableCollection<SharedWorldVersionRow> _versionRows = new();
    private readonly ObservableCollection<WorldBackupRow> _backupRows = new();
    private bool _suppress;
    private WorldEntry? _entry;

    /// <summary>Cancels the disk operation in flight (copy, zip, restore), if any.</summary>
    private CancellationTokenSource? _busyCts;

    /// <summary>Cancels an upload in flight. Kept apart from <see cref="_busyCts"/> because the zip
    /// and the upload are two stages of one action but only the second has its own Cancel button.</summary>
    private CancellationTokenSource? _uploadCts;

    public WorldDetailView(MainWindow shell, string worldKey)
    {
        InitializeComponent();
        _shell = shell;
        _worldKey = worldKey;
        PackCheckList.ItemsSource = _packRows;
        UploadedVersionsList.ItemsSource = _versionRows;
        BackupsList.ItemsSource = _backupRows;
        Loaded += async (_, _) => await ReloadAsync();
        Unloaded += (_, _) => { _busyCts?.Cancel(); _uploadCts?.Cancel(); };
    }

    // ── status and busy state ────────────────────────────────────────────────

    private enum StatusKind { Neutral, Success, Error }

    /// <summary>Writes the status line in the colour that matches what happened, by resource
    /// reference so a theme change repaints it.</summary>
    private void SetStatus(string text, StatusKind kind = StatusKind.Neutral)
    {
        StatusLabel.Text = text;
        StatusLabel.SetResourceReference(TextBlock.ForegroundProperty, kind switch
        {
            StatusKind.Error => "DangerBrush",
            StatusKind.Success => "AccentBrush",
            _ => "TextSecondaryBrush"
        });
    }

    private CancellationToken BeginBusy(string label)
    {
        _busyCts?.Cancel();
        _busyCts = new CancellationTokenSource();
        SetStatus(label);
        BusyBar.Value = 0;
        BusyBar.IsIndeterminate = true;
        BusyBar.Visibility = Visibility.Visible;
        CancelBusyButton.Visibility = Visibility.Visible;
        return _busyCts.Token;
    }

    private void EndBusy()
    {
        BusyBar.Visibility = Visibility.Collapsed;
        BusyBar.IsIndeterminate = false;
        CancelBusyButton.Visibility = Visibility.Collapsed;
        _busyCts?.Dispose();
        _busyCts = null;
    }

    private void OnCancelBusy(object sender, RoutedEventArgs e)
    {
        _busyCts?.Cancel();
        SetStatus("Cancelling…");
    }

    private void OnCancelUpload(object sender, RoutedEventArgs e)
    {
        _uploadCts?.Cancel();
        SetStatus("Cancelling the upload…");
    }

    private IProgress<double> BarProgress() => new Progress<double>(f =>
    {
        BusyBar.IsIndeterminate = false;
        BusyBar.Value = f;
    });

    // ── load ─────────────────────────────────────────────────────────────────

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
                {
                    // The scan sizes the save and decompresses level.dat — off the dispatcher, because
                    // on a multi-gigabyte modded world that is seconds of frozen window.
                    var worlds = await Task.Run(() => App.State.Worlds.ScanPack(pack));
                    _world = worlds.FirstOrDefault(w => w.Key == _worldKey);
                }
            }
            if (_world is null) { SetStatus("World no longer exists.", StatusKind.Error); return; }
            _entry = App.State.Worlds.GetOrCreate(_world.Key);

            _suppress = true;
            try
            {
                WorldNameLabel.Text = _world.DisplayName;
                NameBox.Text = _world.DisplayName;
                SourcePackLabel.Text = _world.SourcePackName.ToUpperInvariant();
                MetaLabel.Text = HeaderMeta(_world);
                WorldFolderPathLabel.Text = _world.FolderPath;
                ShowWorldInfo(_world);
                ConfigurePlayerDataBrowser();
                ConfigureOverviewEditor();
                RefreshBackups();

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
        catch (Exception ex) { SetStatus(ex.Message, StatusKind.Error); }
    }

    private static string HeaderMeta(WorldInfo world)
    {
        var info = world.Info;
        var when = info.LastPlayed is { } played
            ? $"Played {played.LocalDateTime:g}"
            : $"Modified {world.LastModified.LocalDateTime:g}";
        var mode = info.GameType is null ? null : info.GameModeLabel;
        var parts = new List<string> { when, FormatSize(world.SizeBytes) };
        if (mode is not null) parts.Insert(1, mode);
        if (info.VersionName is { Length: > 0 } v) parts.Add($"MC {v}");
        return string.Join(" · ", parts);
    }

    /// <summary>Fills the World info card from the save's level.dat.</summary>
    private void ShowWorldInfo(WorldInfo world)
    {
        var info = world.Info;
        var known = info.GameType is not null || info.HasSeed || info.VersionName is not null;

        SeedLabel.Text = info.SeedLabel;
        CopySeedButton.IsEnabled = info.HasSeed;
        GameModeLabel.Text = info.GameModeLabel;
        DifficultyLabel.Text = info.DifficultyLabel;
        HardcoreLabel.Text = info.Hardcore ? "Yes — one death ends the world" : "No";
        CheatsLabel.Text = info.AllowCommands ? "Enabled" : "Off";
        McVersionLabel.Text = info.VersionLabel;
        SizeLabel.Text = FormatSize(world.SizeBytes);
        LastPlayedLabel.Text = info.LastPlayed is { } played
            ? played.LocalDateTime.ToString("f")
            : world.LastModified.LocalDateTime.ToString("f");
        FolderNameLabel.Text = world.FolderName;
        MetaUnavailableLabel.Visibility = known ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnCopySeed(object sender, RoutedEventArgs e)
    {
        if (_world?.Info.Seed is not { } seed)
        {
            SetStatus("This world's level.dat does not record a seed.", StatusKind.Error);
            return;
        }
        var copied = ClipboardHelper.TrySetText(seed.ToString());
        SetStatus(copied ? $"Seed {seed} copied." : "The clipboard is busy — try again.",
                  copied ? StatusKind.Success : StatusKind.Error);
    }

    private void OnSaveName(object sender, RoutedEventArgs e)
    {
        if (_world is null) return;
        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) { SetStatus("Name cannot be empty.", StatusKind.Error); return; }
        App.State.Worlds.Rename(_world.Key, name);
        WorldNameLabel.Text = name;
        SetStatus("Name saved.", StatusKind.Success);
    }

    // ── world actions ────────────────────────────────────────────────────────

    private void OnWorldActionsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null) return;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = PlacementMode.Bottom;
        button.ContextMenu.IsOpen = true;
    }

    private async void OnDuplicate(object sender, RoutedEventArgs e)
    {
        if (_world is null) return;
        var ct = BeginBusy($"Duplicating {_world.DisplayName}…");
        try
        {
            var folder = await App.State.Worlds.DuplicateAsync(_world, BarProgress(), ct);
            SetStatus($"Duplicated as {folder}. It is on the Worlds page.", StatusKind.Success);
        }
        catch (OperationCanceledException) { SetStatus("Duplicate cancelled."); }
        catch (Exception ex) { SetStatus("Duplicate failed: " + ex.Message, StatusKind.Error); }
        finally { EndBusy(); }
    }

    private async void OnCopyToInstance(object sender, RoutedEventArgs e)
    {
        if (_world is null) return;
        try
        {
            var targets = _allPacks.Where(p => p.Id != _world.SourcePackId).ToList();
            if (targets.Count == 0)
            {
                SetStatus("There is no other instance to copy this world into.", StatusKind.Error);
                return;
            }
            var picker = new PackPickerDialog(targets,
                "Copy world to...",
                $"'{_world.DisplayName}' will be copied into this instance's saves folder.",
                "Copy") { Owner = _shell };
            if (picker.ShowDialog() != true || picker.SelectedPackId is not { } packId) return;
            var pack = targets.First(p => p.Id == packId);

            var ct = BeginBusy($"Copying into {pack.Name}…");
            try
            {
                var folder = await App.State.Worlds.CopyToPackAsync(_world, pack.Id, pack.Name, BarProgress(), ct);
                SetStatus($"Copied to {pack.Name} as {folder}.", StatusKind.Success);
            }
            finally { EndBusy(); }
        }
        catch (OperationCanceledException) { SetStatus("Copy cancelled."); }
        catch (Exception ex) { SetStatus("Copy failed: " + ex.Message, StatusKind.Error); }
    }

    private async void OnExportZip(object sender, RoutedEventArgs e)
    {
        if (_world is null) return;
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export world as .zip",
                Filter = "Zip archive (*.zip)|*.zip",
                FileName = WorldService.SafeFolderName(_world.DisplayName) + ".zip"
            };
            if (dialog.ShowDialog(_shell) != true) return;

            var ct = BeginBusy($"Zipping {_world.DisplayName}…");
            try
            {
                await WorldService.ExportZipAsync(_world, dialog.FileName, BarProgress(), ct);
                SetStatus($"Exported to {dialog.FileName}.", StatusKind.Success);
            }
            finally { EndBusy(); }
        }
        catch (OperationCanceledException) { SetStatus("Export cancelled."); }
        catch (Exception ex) { SetStatus("Export failed: " + ex.Message, StatusKind.Error); }
    }

    /// <summary>
    /// Renames the save folder on disk.
    /// </summary>
    /// <remarks>Separate from the display name above, which is a launcher label: this one moves the
    /// directory and re-keys every launcher-side reference to it, so the page has to reload under the
    /// new key afterwards.</remarks>
    private async void OnRenameFolder(object sender, RoutedEventArgs e)
    {
        if (_world is null) return;
        try
        {
            var dlg = new SimpleInputDialog("Rename save folder", "New folder name", _world.FolderName)
            { Owner = _shell };
            if (dlg.ShowDialog() != true) return;
            var name = (dlg.Result ?? "").Trim();
            if (string.IsNullOrEmpty(name) || name == _world.FolderName) return;

            _worldKey = await App.State.Worlds.RenameFolderAsync(_world, name);
            await ReloadAsync();
            SetStatus($"Save folder renamed to {name}.", StatusKind.Success);
        }
        catch (IOException ex)
        {
            SetStatus(ex.Message + " Close Minecraft if the world is open.", StatusKind.Error);
        }
        catch (Exception ex) { SetStatus("Rename failed: " + ex.Message, StatusKind.Error); }
    }

    private async void OnDeleteWorld(object sender, RoutedEventArgs e)
    {
        if (_world is null) return;
        try
        {
            var backups = App.State.Worlds.ListBackups(_world).Count;
            var ok = await AppDialog.ConfirmAsync(_shell, "Delete world",
                $"Delete '{_world.DisplayName}' from disk?\n\n" +
                $"{FormatSize(_world.SizeBytes)} in {_world.FolderPath}\n" +
                "This removes the save folder permanently." +
                (backups == 0
                    ? "\n\nThere are no backups of this world."
                    : $"\n\n{backups} backup{(backups == 1 ? "" : "s")} will be kept."),
                "Delete world", "Cancel", danger: true);
            if (!ok) return;

            var ct = BeginBusy("Deleting…");
            try
            {
                await App.State.Worlds.DeleteAsync(_world, ct);
                _world = null;
                SetStatus("World deleted.", StatusKind.Success);
                // Nothing left to show on this page — go back to whatever opened it.
                _shell.CloseSidePanel();
            }
            finally { EndBusy(); }
        }
        catch (IOException)
        {
            SetStatus("Close Minecraft before deleting this world — its files are in use.", StatusKind.Error);
        }
        catch (Exception ex) { SetStatus("Delete failed: " + ex.Message, StatusKind.Error); }
    }

    // ── backups ──────────────────────────────────────────────────────────────

    private void RefreshBackups()
    {
        _backupRows.Clear();
        if (_world is null) return;
        foreach (var backup in App.State.Worlds.ListBackups(_world))
            _backupRows.Add(new WorldBackupRow(backup));

        BackupsEmptyLabel.Visibility = _backupRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BackupsHintLabel.Text = _backupRows.Count == 0
            ? "Snapshots of this save, kept beside the instance."
            : $"{_backupRows.Count} snapshot{(_backupRows.Count == 1 ? "" : "s")} · " +
              $"{FormatSize(_backupRows.Sum(b => b.Source.SizeBytes))} in {App.State.Worlds.BackupsDir(_world.SourcePackId)}";
    }

    private async void OnBackupNow(object sender, RoutedEventArgs e)
    {
        if (_world is null) return;
        BackupNowButton.IsEnabled = false;
        var ct = BeginBusy("Backing up…");
        try
        {
            var zip = await App.State.Worlds.BackupAsync(_world, BarProgress(), ct);
            RefreshBackups();
            SetStatus($"Backed up to {Path.GetFileName(zip)}.", StatusKind.Success);
        }
        catch (OperationCanceledException) { SetStatus("Backup cancelled."); }
        catch (Exception ex) { SetStatus("Backup failed: " + ex.Message, StatusKind.Error); }
        finally { EndBusy(); BackupNowButton.IsEnabled = true; }
    }

    private async void OnRestoreBackup(object sender, RoutedEventArgs e)
    {
        if (_world is null || BackupFromSender(sender) is not { } backup) return;
        try
        {
            var ok = await AppDialog.ConfirmAsync(_shell, "Restore backup",
                $"Replace '{_world.DisplayName}' with the snapshot from {backup.Source.TakenAt.LocalDateTime:f}?\n\n" +
                "Everything you have done in this world since then will be gone. " +
                "A fresh backup of the current save is taken first, so this can be undone.",
                "Restore", "Cancel", danger: true);
            if (!ok) return;

            var ct = BeginBusy("Restoring…");
            try
            {
                // A non-null result means the restore succeeded but the old save could not be
                // deleted afterwards — say where it is rather than silently leaving it in saves/.
                var leftover = await App.State.Worlds.RestoreAsync(_world, backup.Source.Path, BarProgress(), ct);
                await ReloadAsync();
                SetStatus(leftover is null
                    ? $"Restored the snapshot from {backup.Source.TakenAt.LocalDateTime:f}."
                    : $"Restored the snapshot from {backup.Source.TakenAt.LocalDateTime:f}. The old save is still "
                      + $"on disk as '{Path.GetFileName(leftover)}' — delete it once Minecraft has let go of it.",
                    StatusKind.Success);
            }
            finally { EndBusy(); }
        }
        catch (OperationCanceledException) { SetStatus("Restore cancelled."); }
        catch (IOException)
        {
            SetStatus("Close Minecraft before restoring this world — its files are in use.", StatusKind.Error);
        }
        catch (Exception ex) { SetStatus("Restore failed: " + ex.Message, StatusKind.Error); }
    }

    private async void OnDeleteBackup(object sender, RoutedEventArgs e)
    {
        if (BackupFromSender(sender) is not { } backup) return;
        try
        {
            var ok = await AppDialog.ConfirmAsync(_shell, "Delete backup",
                $"Delete the snapshot from {backup.Source.TakenAt.LocalDateTime:f} ({FormatSize(backup.Source.SizeBytes)})?\n\n" +
                "The world itself is not affected.",
                "Delete backup", "Cancel", danger: true);
            if (!ok) return;

            WorldService.DeleteBackup(backup.Source.Path);
            RefreshBackups();
            SetStatus("Backup deleted.", StatusKind.Success);
        }
        catch (Exception ex) { SetStatus("Could not delete the backup: " + ex.Message, StatusKind.Error); }
    }

    private void OnRevealBackup(object sender, RoutedEventArgs e)
    {
        if (BackupFromSender(sender) is not { } backup) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{backup.Source.Path}\"")
            { UseShellExecute = true });
        }
        catch (Exception ex) { SetStatus("Could not open Explorer: " + ex.Message, StatusKind.Error); }
    }

    private void OnOpenBackupsFolder(object sender, RoutedEventArgs e)
    {
        if (_world is null) return;
        try
        {
            var dir = App.State.Worlds.BackupsDir(_world.SourcePackId);
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex) { SetStatus("Could not open the folder: " + ex.Message, StatusKind.Error); }
    }

    /// <summary>Resolves the backup a row action refers to, whether it came from the row's button
    /// (Tag) or from the row's context menu (the placement target's DataContext).</summary>
    private static WorldBackupRow? BackupFromSender(object sender) => RowFromSender<WorldBackupRow>(sender);

    private static T? RowFromSender<T>(object sender) where T : class
    {
        if (sender is FrameworkElement { Tag: T tagged }) return tagged;
        if (sender is FrameworkElement { DataContext: T bound }) return bound;
        if (sender is MenuItem mi)
        {
            var parent = mi.Parent;
            while (parent is MenuItem p) parent = p.Parent;
            if (parent is ContextMenu cm && cm.PlacementTarget is FrameworkElement target
                && target.DataContext is T row) return row;
        }
        return null;
    }

    // ── sharing ──────────────────────────────────────────────────────────────

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
        DeleteHostedWorldButton.IsEnabled = _entry.SharedWorldId is Guid;

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

    /// <summary>
    /// Turns the sharing controls on or off.
    /// </summary>
    /// <remarks>Switching this off used to hide the controls and leave the world published for
    /// everyone it had been shared with, which reads exactly like unsharing and is not. If there is a
    /// hosted world behind the toggle, say so and offer to take it down.</remarks>
    private async void OnShareWorldToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress || _world is null) return;
        try
        {
            var enabled = ShareWorldBox.IsChecked == true;

            if (!enabled && _entry?.SharedWorldId is Guid)
            {
                var takeDown = await AppDialog.ConfirmAsync(_shell, "This world is still published",
                    "Hiding the sharing controls does not unpublish the world — it stays on the server and " +
                    "everyone it was shared with keeps access.\n\nTake the hosted world down as well?",
                    "Take it down", "Just hide the controls", danger: true);
                if (takeDown)
                {
                    if (!await DeleteHostedWorldAsync()) { ShareWorldBox.IsChecked = true; return; }
                }
            }

            App.State.Worlds.SetSharingEnabled(_world.Key, enabled);
            _entry = App.State.Worlds.GetOrCreate(_world.Key);
            ConfigureSharingState();
            SetStatus(enabled
                ? "Sharing enabled. Upload a version to publish this world."
                : "Sharing controls hidden for this world.");
        }
        catch (Exception ex) { SetStatus(ex.Message, StatusKind.Error); }
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

    /// <summary>
    /// Publishes a new version of this world: zip, then upload, both cancellable and both reporting.
    /// </summary>
    /// <remarks>
    /// Previously this zipped a whole save on the dispatcher with a static "Preparing world zip…"
    /// label that never painted, then uploaded with no progress, and could fail after minutes because
    /// nothing checked the server's size cap. The zip now runs on a worker thread, the bar shows
    /// files zipped and then bytes sent, and the archive is measured against the cap before a single
    /// byte goes to the network.
    /// </remarks>
    private async void OnShareOrUploadWorld(object sender, RoutedEventArgs e)
    {
        if (_world is null) return;
        SaveOverviewToSettings();

        if (_world.SizeBytes > MaxUploadBytes)
        {
            var proceed = await AppDialog.ConfirmAsync(_shell, "This world is large",
                $"'{_world.DisplayName}' is {FormatSize(_world.SizeBytes)} on disk and the server accepts " +
                $"uploads up to {FormatSize(MaxUploadBytes)}.\n\n" +
                "Saves compress well, so the zip may still fit. Zip it and find out?",
                "Zip it and check", "Cancel");
            if (!proceed) return;
        }

        var dlg = new WorldVersionDialog(
            $"{_world.DisplayName} · {FormatSize(_world.SizeBytes)} on disk",
            DateTimeOffset.Now.ToString("yyyy.MM.dd.HHmm"))
        { Owner = _shell };
        if (dlg.ShowDialog() != true) return;

        _uploadCts?.Cancel();
        _uploadCts = new CancellationTokenSource();
        var ct = _uploadCts.Token;

        UploadSharedWorldButton.IsEnabled = false;
        CancelUploadButton.Visibility = Visibility.Visible;
        UploadBar.Visibility = Visibility.Visible;
        UploadBar.IsIndeterminate = true;
        UploadBar.Value = 0;
        SetStatus("Zipping the save…");

        string? zipPath = null;
        try
        {
            var sharedId = await EnsureSharedWorldAsync(ct);

            zipPath = await WorldService.ZipToTempAsync(_world, new Progress<double>(f =>
            {
                UploadBar.IsIndeterminate = false;
                // The zip is the first half of the job, the upload the second.
                UploadBar.Value = f * 0.5;
            }), ct);

            var zipBytes = new FileInfo(zipPath).Length;
            if (zipBytes > MaxUploadBytes)
                throw new InvalidOperationException(
                    $"The zipped world is {FormatSize(zipBytes)}, over the server's {FormatSize(MaxUploadBytes)} limit.");

            var meta = new CreateWorldVersionRequest(
                dlg.Version,
                dlg.Changelog,
                SafeZipName(_world.DisplayName),
                SourceMinecraftVersion());

            SetStatus($"Uploading {FormatSize(zipBytes)}…");
            var sent = new Progress<long>(done =>
            {
                UploadBar.IsIndeterminate = false;
                UploadBar.Value = 0.5 + Math.Min(1.0, done / (double)zipBytes) * 0.5;
            });
            await App.State.Api.UploadSharedWorldVersionAsync(sharedId, zipPath, meta, ct, sent);

            SetStatus($"Uploaded version {dlg.Version}.", StatusKind.Success);
            SharedWorldStatusLabel.Text = $"Shared as hosted world {sharedId}.";
            await LoadSharedWorldVersionsAsync();
        }
        catch (OperationCanceledException) { SetStatus("Upload cancelled."); }
        catch (Exception ex) { SetStatus("Share failed: " + ex.Message, StatusKind.Error); }
        finally
        {
            UploadSharedWorldButton.IsEnabled = true;
            CancelUploadButton.Visibility = Visibility.Collapsed;
            UploadBar.Visibility = Visibility.Collapsed;
            UploadBar.IsIndeterminate = false;
            _uploadCts?.Dispose();
            _uploadCts = null;
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
            SetStatus("Share/upload this world before managing collaborators.", StatusKind.Error);
            return;
        }

        try
        {
            var detail = await App.State.Api.GetSharedWorldAsync(sharedId);
            new PermissionsDialog(detail) { Owner = _shell }.ShowDialog();
        }
        catch (Exception ex)
        {
            SetStatus("Could not load permissions: " + ex.Message, StatusKind.Error);
        }
    }

    private async void OnDeleteHostedWorld(object sender, RoutedEventArgs e)
    {
        try { await DeleteHostedWorldAsync(); }
        catch (Exception ex) { SetStatus("Could not delete the hosted world: " + ex.Message, StatusKind.Error); }
    }

    /// <summary>Takes the published copy down from the server. Returns true if it is gone.</summary>
    /// <remarks>The local save is never touched — that is the whole point of the distinction, and the
    /// confirmation says so, because "delete world" on a page about a world on your disk is alarming.</remarks>
    private async Task<bool> DeleteHostedWorldAsync()
    {
        if (_world is null || _entry?.SharedWorldId is not Guid sharedId) return false;

        var versions = _versionRows.Count;
        var ok = await AppDialog.ConfirmAsync(_shell, "Delete hosted world",
            $"Take '{_world.DisplayName}' down from the server?\n\n" +
            (versions > 0 ? $"All {versions} uploaded version{(versions == 1 ? "" : "s")} go with it, and " : "") +
            "everyone it was shared with loses access.\n\nYour local save on this machine is not touched.",
            "Delete hosted world", "Cancel", danger: true);
        if (!ok) return false;

        DeleteHostedWorldButton.IsEnabled = false;
        try
        {
            await App.State.Api.DeleteSharedWorldAsync(sharedId);
            App.State.Worlds.UnlinkSharedWorld(_world.Key);
            _entry = App.State.Worlds.GetOrCreate(_world.Key);
            _versionRows.Clear();
            ConfigureOverviewEditor();
            SetStatus("The hosted world was deleted. Your local save is untouched.", StatusKind.Success);
            return true;
        }
        catch (Exception ex)
        {
            SetStatus("Could not delete the hosted world: " + ex.Message, StatusKind.Error);
            return false;
        }
        finally { DeleteHostedWorldButton.IsEnabled = _entry?.SharedWorldId is Guid; }
    }

    private void SaveOverviewToSettings()
    {
        if (_world is null) return;
        App.State.Worlds.UpdateOverview(_world.Key, SummaryBox.Text, DescriptionBox.Text, SelectedVisibility());
        App.State.Worlds.SetSharingEnabled(_world.Key, true);
        _entry = App.State.Worlds.GetOrCreate(_world.Key);
    }

    private async Task<Guid> EnsureSharedWorldAsync(CancellationToken ct = default)
    {
        if (_world is null) throw new InvalidOperationException("World no longer exists.");
        _entry ??= App.State.Worlds.GetOrCreate(_world.Key);

        if (_entry.SharedWorldId is Guid existingId)
        {
            await App.State.Api.UpdateSharedWorldAsync(existingId, new UpdateWorldRequest(
                _world.DisplayName,
                string.IsNullOrWhiteSpace(SummaryBox.Text) ? null : SummaryBox.Text.Trim(),
                string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text.Trim(),
                SelectedVisibility(),
                SourceMinecraftVersion()), ct);
            return existingId;
        }

        var created = await App.State.Api.CreateSharedWorldAsync(new CreateWorldRequest(
            _world.DisplayName,
            string.IsNullOrWhiteSpace(SummaryBox.Text) ? null : SummaryBox.Text.Trim(),
            string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text.Trim(),
            SelectedVisibility(),
            SourceMinecraftVersion()), ct);
        App.State.Worlds.LinkSharedWorld(_world.Key, created.Id);
        _entry = App.State.Worlds.GetOrCreate(_world.Key);
        return created.Id;
    }

    private PackVisibility SelectedVisibility()
    {
        var text = (VisibilityBox.SelectedItem as ComboBoxItem)?.Content as string ?? "Private";
        return Enum.TryParse<PackVisibility>(text, out var visibility) ? visibility : PackVisibility.Private;
    }

    private async void OnRefreshSharedWorldVersions(object sender, RoutedEventArgs e)
    {
        try { await LoadSharedWorldVersionsAsync(); }
        catch (Exception ex) { SetStatus("Could not load uploaded versions: " + ex.Message, StatusKind.Error); }
    }

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
            SetStatus("Could not load uploaded versions: " + ex.Message, StatusKind.Error);
        }
        finally
        {
            RefreshVersionsButton.IsEnabled = _entry?.SharedWorldId is Guid;
        }
    }

    // ── uploaded version row actions ─────────────────────────────────────────

    /// <summary>Downloads one uploaded version back out into an instance's saves folder.</summary>
    /// <remarks>This is how the owner recovers a world they have since lost or broken locally, which
    /// is the reason to upload it in the first place.</remarks>
    private async void OnDownloadVersion(object sender, RoutedEventArgs e)
    {
        if (_entry?.SharedWorldId is not Guid sharedId) return;
        if (RowFromSender<SharedWorldVersionRow>(sender) is not { } version) return;

        string? tmp = null;
        try
        {
            if (_allPacks.Count == 0) _allPacks = await App.State.Api.ListPacksAsync();
            if (_allPacks.Count == 0)
            {
                SetStatus("Create an instance before downloading a world.", StatusKind.Error);
                return;
            }

            var picker = new PackPickerDialog(_allPacks,
                "Download world to...",
                $"Version {version.VersionString} will land in this instance's saves folder.",
                "Download") { Owner = _shell };
            if (picker.ShowDialog() != true || picker.SelectedPackId is not { } packId) return;
            var pack = _allPacks.First(p => p.Id == packId);

            var ct = BeginBusy($"Downloading {version.VersionString}…");
            try
            {
                tmp = Path.Combine(Path.GetTempPath(), $"cl-world-{Guid.NewGuid():N}.zip");
                await using (var stream = await App.State.Api.DownloadSharedWorldVersionAsync(sharedId, version.Id, ct))
                await using (var fs = File.Create(tmp))
                    await stream.CopyToAsync(fs, ct);

                SetStatus($"Extracting into {pack.Name}…");
                var folder = await App.State.Worlds.ImportZipAsync(
                    tmp, pack.Id, pack.Name, _world?.DisplayName, BarProgress(), ct);
                SetStatus($"Downloaded into {pack.Name} as {folder}.", StatusKind.Success);
            }
            finally { EndBusy(); }
        }
        catch (OperationCanceledException) { SetStatus("Download cancelled."); }
        catch (Exception ex) { SetStatus("Download failed: " + ex.Message, StatusKind.Error); }
        finally
        {
            if (tmp is not null) { try { File.Delete(tmp); } catch { /* best effort */ } }
        }
    }

    private void OnCopyVersionLink(object sender, RoutedEventArgs e)
    {
        if (_entry?.SharedWorldId is not Guid sharedId) return;
        if (RowFromSender<SharedWorldVersionRow>(sender) is not { } version) return;

        var link = $"{App.State.Settings.ServerUrl.TrimEnd('/')}/worlds/{sharedId}/files/{version.Id}";
        var copied = ClipboardHelper.TrySetText(link);
        SetStatus(copied ? "Download link copied." : "The clipboard is busy — try again.",
                  copied ? StatusKind.Success : StatusKind.Error);
    }

    private async void OnDeleteVersion(object sender, RoutedEventArgs e)
    {
        if (_entry?.SharedWorldId is not Guid sharedId) return;
        if (RowFromSender<SharedWorldVersionRow>(sender) is not { } version) return;
        try
        {
            var ok = await AppDialog.ConfirmAsync(_shell, "Delete version",
                $"Delete version {version.VersionString} from the server?\n\n" +
                "Anyone who has not downloaded it yet will no longer be able to. " +
                "The world page and its other versions stay.",
                "Delete version", "Cancel", danger: true);
            if (!ok) return;

            await App.State.Api.DeleteSharedWorldVersionAsync(sharedId, version.Id);
            await LoadSharedWorldVersionsAsync();
            SetStatus($"Version {version.VersionString} deleted.", StatusKind.Success);
        }
        catch (Exception ex) { SetStatus("Could not delete the version: " + ex.Message, StatusKind.Error); }
    }

    private string? SourceMinecraftVersion()
    {
        if (_world is null) return null;
        // Prefer what the save itself was last written by; fall back to the instance's version.
        return _world.Info.VersionName
               ?? _allPacks.FirstOrDefault(p => p.Id == _world.SourcePackId)?.MinecraftVersion;
    }

    private static string SafeZipName(string name)
    {
        var clean = WorldService.SafeFolderName(name);
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
        try { Process.Start(new ProcessStartInfo { FileName = _world.FolderPath, UseShellExecute = true }); }
        catch (Exception ex) { SetStatus("Could not open the folder: " + ex.Message, StatusKind.Error); }
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
            SetStatus("This world does not have a playerdata folder yet.", StatusKind.Error);
            return;
        }

        Process.Start(new ProcessStartInfo { FileName = playerDataDir, UseShellExecute = true });
    }

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        if (_world is null) return;
        var compat = App.State.Worlds.CompatiblePacks(_world, _allPacks);
        if (compat.Count == 0) { SetStatus("No compatible instances.", StatusKind.Error); return; }
        var menu = new ContextMenu();
        foreach (var p in compat)
        {
            var item = new MenuItem { Header = $"Play with {p.Name}" };
            var packId = p.Id;
            item.Click += async (_, _) =>
            {
                try { await PlayAsync(packId); }
                catch (Exception ex) { SetStatus("Launch failed: " + ex.Message, StatusKind.Error); }
            };
            menu.Items.Add(item);
        }
        menu.PlacementTarget = (FrameworkElement)sender;
        menu.IsOpen = true;
    }

    /// <summary>
    /// Launches an instance with this world, copying the save into it first when it lives elsewhere.
    /// </summary>
    /// <remarks>When the target already holds a copy the user is asked which one they meant, rather
    /// than the copy being skipped in silence and a stale save loading.</remarks>
    private async Task PlayAsync(Guid packId)
    {
        if (_world is null) return;
        SetStatus("Launching…");
        try
        {
            if (App.State.MinecraftAccounts.Current is null)
            {
                _shell.OpenMcAccount();
                SetStatus("Sign in to a Minecraft account first.", StatusKind.Error);
                return;
            }
            var pack = await App.State.Api.GetPackAsync(packId);
            App.State.Packs.EnsurePackFolder(pack.Id, pack.Name, pack.IsShared);

            if (packId != _world.SourcePackId)
            {
                var target = Path.Combine(App.State.Packs.GameDir(pack.Id), "saves", _world.FolderName);
                var replace = false;
                if (Directory.Exists(target))
                {
                    var theirs = WorldService.ReadMeta(target).LastPlayed
                                 ?? (DateTimeOffset)Directory.GetLastWriteTime(target);
                    var mine = _world.Info.LastPlayed ?? _world.LastModified;
                    // Replacing is the affirmative answer so that dismissing the dialog falls through
                    // to playing the existing copy — the choice that cannot lose anyone's progress.
                    replace = await AppDialog.ConfirmAsync(_shell,
                        $"{_world.DisplayName} is already in {pack.Name}",
                        $"That instance already has a copy of this save.\n\n" +
                        $"Copy in {pack.Name}: last played {theirs.LocalDateTime:g}\n" +
                        $"This save in {_world.SourcePackName}: last played {mine.LocalDateTime:g}\n\n" +
                        $"Replacing overwrites the copy in {pack.Name} and everything done in it.",
                        "Replace it with this save", $"Play the copy in {pack.Name}", danger: true);
                }

                if (!Directory.Exists(target) || replace)
                {
                    var ct = BeginBusy($"Copying the save into {pack.Name}…");
                    try
                    {
                        // Replacing never deletes first: this token is the Cancel button's, and a
                        // copy cancelled between two files would leave the instance with a partial
                        // world and nothing to put back. The swap waits for a complete copy.
                        if (replace)
                            await WorldService.ReplaceWorldAsync(_world.FolderPath, target, BarProgress(), ct);
                        else
                            await WorldService.CopyWorldAsync(_world.FolderPath, target, BarProgress(), ct);
                    }
                    finally { EndBusy(); }
                }
            }

            var proc = await App.State.Launcher.LaunchTrackedAsync(pack, new Progress<string>(_ => { }));
            _shell.OpenMinecraftHost(pack, proc);
            SetStatus($"Launched {pack.Name}.", StatusKind.Success);
        }
        catch (OperationCanceledException) { SetStatus("Cancelled."); }
        catch (Exception ex) { SetStatus("Launch failed: " + ex.Message, StatusKind.Error); }
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
      : bytes >= 1024L * 1024 ? $"{bytes / (1024.0 * 1024):F1} MB"
      : bytes >= 1024 ? $"{bytes / 1024.0:F0} KB"
      : $"{bytes} B";

    private static string PlayerDataDir(WorldInfo world) => Path.Combine(world.FolderPath, "playerdata");
}

/// <summary>One snapshot on the Backups tab.</summary>
public sealed class WorldBackupRow(WorldBackup backup)
{
    public WorldBackup Source { get; } = backup;
    public string WhenLabel { get; } = backup.TakenAt.LocalDateTime.ToString("f");
    public string MetaLabel { get; } = $"{backup.FileName} · {FormatSize(backup.SizeBytes)}";

    private static string FormatSize(long bytes) =>
        bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
      : bytes >= 1024L * 1024 ? $"{bytes / (1024.0 * 1024):F1} MB"
      : bytes >= 1024 ? $"{bytes / 1024.0:F0} KB"
      : $"{bytes} B";
}

public sealed class SharedWorldVersionRow
{
    public Guid Id { get; init; }
    public string VersionString { get; init; } = "";
    public string MetaLabel { get; init; } = "";
    public string McVersionLabel { get; init; } = "";
    public string Changelog { get; init; } = "";
    public Visibility ChangelogVisibility => string.IsNullOrWhiteSpace(Changelog)
        ? Visibility.Collapsed : Visibility.Visible;

    public static SharedWorldVersionRow From(SharedWorldVersionInfo version) => new()
    {
        Id = version.Id,
        VersionString = version.VersionString,
        MetaLabel = $"{version.FileName} · {FormatSize(version.FileSize)} · {version.PublishedAt:yyyy-MM-dd HH:mm}",
        McVersionLabel = string.IsNullOrWhiteSpace(version.McVersion) ? "Any MC" : $"MC {version.McVersion}",
        Changelog = version.Changelog ?? ""
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
