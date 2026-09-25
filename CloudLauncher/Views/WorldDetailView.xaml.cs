using System.Collections.ObjectModel;
using System.ComponentModel;
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
    /// <summary>The server's per-world upload limit, copied from
    /// <c>SharedWorldsController.MaxWorldBytes</c>.</summary>
    /// <remarks>The client can't reference the server project. This is only for an early warning; the
    /// server still enforces its own limit.</remarks>
    private const long MaxUploadBytes = 256L * 1024 * 1024;

    private readonly MainWindow _shell;
    private string _worldKey;
    private WorldInfo? _world;
    private List<PackSummary> _allPacks = new();
    private readonly ObservableCollection<WorldInstanceRow> _packRows = new();

    /// <summary>Which instances hold a copy of this save, by instance id. Re-read on every
    /// reload.</summary>
    private Dictionary<Guid, InstanceCopy> _copies = new();

    /// <summary>The library copy of this save, if any. Not shown on the page.</summary>
    /// <remarks>Kept as a lookup key: placement records
    /// (<see cref="InstanceContentOverrides.WorldFolderFor"/>) are keyed on it, which lets
    /// <see cref="ReadCopiesAsync"/> recognise a copy that was renamed.</remarks>
    private LibraryItem? _template;
    private readonly ObservableCollection<SharedWorldVersionRow> _versionRows = new();
    private readonly ObservableCollection<WorldBackupRow> _backupRows = new();
    private bool _suppress;
    private WorldEntry? _entry;

    /// <summary>Cancels the disk operation in flight (copy, zip, restore), if any.</summary>
    private CancellationTokenSource? _busyCts;

    /// <summary>Cancels an upload in flight. Separate from <see cref="_busyCts"/> because only the
    /// upload stage has its own Cancel button.</summary>
    private CancellationTokenSource? _uploadCts;

    /// <summary>The hosted world this save is linked to, as the server last described it. Null until
    /// read, or if it could not be read.</summary>
    /// <remarks>A save downloaded from someone else's hosted world is linked to it too, so the controls
    /// follow what the server lets this person do instead of assuming ownership.</remarks>
    private SharedWorldDetail? _hosted;

    /// <summary>Why <see cref="_hosted"/> could not be read, in words, or null.</summary>
    private string? _hostedProblem;

    /// <summary>Set once the user edits the overview here, so the server's copy no longer overwrites
    /// the boxes.</summary>
    private bool _overviewEdited;

    public WorldDetailView(MainWindow shell, string worldKey)
    {
        InitializeComponent();
        _shell = shell;
        _worldKey = worldKey;
        InstanceStateList.ItemsSource = _packRows;
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
        SetStatus("Cancelling...");
    }

    private void OnCancelUpload(object sender, RoutedEventArgs e)
    {
        _uploadCts?.Cancel();
        SetStatus("Cancelling the upload...");
    }

    private IProgress<double> BarProgress() => new Progress<double>(f =>
    {
        BusyBar.IsIndeterminate = false;
        BusyBar.Value = f;
    });

    /// <summary>True, with a status message, when Minecraft has this instance open.</summary>
    /// <remarks>The game holds session.lock and keeps rewriting region files, so anything that writes
    /// inside a save checks here first. Read-only paths (export, backup) skip the lock file and work
    /// while the game runs.</remarks>
    private bool InUse(Guid packId, string what)
    {
        if (!App.State.Instances.IsBusy(packId)) return false;
        var name = _allPacks.FirstOrDefault(p => p.Id == packId)?.Name ?? "That instance";
        SetStatus($"{name} is running - close Minecraft before {what}.", StatusKind.Error);
        return true;
    }

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
                    // Off the dispatcher: sizing the save and decompressing level.dat takes
                    // seconds on a big modded world.
                    var worlds = await Task.Run(() => App.State.Worlds.ScanPack(pack));
                    _world = worlds.FirstOrDefault(w => w.Key == _worldKey);
                }
            }
            if (_world is null) { SetStatus("World no longer exists.", StatusKind.Error); return; }
            _entry = App.State.Worlds.GetOrCreate(_world.Key);

            // Needed before the Instances tab is filled, since its ticks come from this.
            _template = FindTemplate(_world);
            _copies = await ReadCopiesAsync(_world, _template?.Key);

            _suppress = true;
            try
            {
                WorldNameLabel.Text = _world.DisplayName;
                ShowHeroIcon(_world);
                NameBox.Text = _world.DisplayName;
                SourcePackLabel.Text = _world.SourcePackName.ToUpperInvariant();
                MetaLabel.Text = HeaderMeta(_world, _copies.Count);
                WorldFolderPathLabel.Text = _world.FolderPath;
                ShowWorldInfo(_world);
                ConfigurePlayerDataBrowser();
                ConfigureOverviewEditor();
                RefreshBackups();

                RefreshInstancesTab();
            }
            finally { _suppress = false; }
        }
        catch (Exception ex) { SetStatus(ex.Message, StatusKind.Error); }
    }

    /// <summary>The line under the world's name. Played, size and version are this save's own.</summary>
    /// <param name="elsewhere">How many other instances hold a copy of this world. Shown because the
    /// Worlds page lists a world once however many copies it has.</param>
    private static string HeaderMeta(WorldInfo world, int elsewhere)
    {
        var info = world.Info;
        var when = info.LastPlayed is { } played
            ? $"Played {played.LocalDateTime:g}"
            : $"Modified {world.LastModified.LocalDateTime:g}";
        var mode = info.GameType is null ? null : info.GameModeLabel;
        var parts = new List<string> { when, FormatSize(world.SizeBytes) };
        if (mode is not null) parts.Insert(1, mode);
        if (info.VersionName is { Length: > 0 } v) parts.Add($"MC {v}");
        if (elsewhere > 0)
            parts.Add($"{elsewhere} other instance{(elsewhere == 1 ? " has" : "s have")} a save of "
                    + $"this name, {(elsewhere == 1 ? "a separate save" : "separate saves")}");
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
        HardcoreLabel.Text = info.Hardcore ? "Yes - one death ends the world" : "No";
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
        SetStatus(copied ? $"Seed {seed} copied." : "The clipboard is busy - try again.",
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

    /// <summary>Fills the hero tile from the same sources as the grid card, so it shows the same
    /// picture.</summary>
    private void ShowHeroIcon(WorldInfo world)
    {
        var name = (world.DisplayName ?? "").TrimStart();
        HeroIconFallback.Text = name.Length > 0 ? name[..1].ToUpperInvariant() : "?";
        HeroIconTile.Background = WorldRow.CoverFor(world.DisplayName);
        HeroIconImage.Source = WorldRow.LoadWorldIcon(world.FolderPath);
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
        if (InUse(_world.SourcePackId, "duplicating a world in it")) return;
        var ct = BeginBusy($"Duplicating {_world.DisplayName}...");
        try
        {
            var folder = await App.State.Worlds.DuplicateAsync(_world, BarProgress(), ct);
            SetStatus($"Duplicated as {folder}. It is on the Worlds page.", StatusKind.Success);
        }
        catch (OperationCanceledException) { SetStatus("Duplicate cancelled."); }
        catch (Exception ex) { SetStatus("Duplicate failed: " + ex.Message, StatusKind.Error); }
        finally { EndBusy(); }
    }

    /// <summary>The actions menu's "Copy to instance...": pick an instance, then do the same copy as
    /// the Instances tab's tick.</summary>
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
                $"'{_world.DisplayName}' will be copied into this instance's saves folder. The two "
                + "copies are separate worlds from then on.",
                "Copy") { Owner = _shell };
            if (picker.ShowDialog() != true || picker.SelectedPackId is not { } packId) return;
            await CopyIntoAsync(targets.First(p => p.Id == packId));
        }
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

            var ct = BeginBusy($"Zipping {_world.DisplayName}...");
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

    /// <summary>Renames the save folder on disk.</summary>
    /// <remarks>Unlike the display name (a launcher label), this moves the directory and re-keys every
    /// launcher reference to it, so the page reloads under the new key.</remarks>
    private async void OnRenameFolder(object sender, RoutedEventArgs e)
    {
        if (_world is null) return;
        try
        {
            if (InUse(_world.SourcePackId, "renaming a save folder in it")) return;
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
            if (InUse(_world.SourcePackId, "deleting this world")) return;
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

            var ct = BeginBusy("Deleting...");
            try
            {
                await App.State.Worlds.DeleteAsync(_world, ct);
                _world = null;
                SetStatus("World deleted.", StatusKind.Success);
                // Nothing left to show on this page.
                _shell.CloseSidePanel();
            }
            finally { EndBusy(); }
        }
        catch (IOException)
        {
            SetStatus("Close Minecraft before deleting this world - its files are in use.", StatusKind.Error);
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
        var ct = BeginBusy("Backing up...");
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
            if (InUse(_world.SourcePackId, "restoring over this world")) return;
            var ok = await AppDialog.ConfirmAsync(_shell, "Restore backup",
                $"Replace '{_world.DisplayName}' with the snapshot from {backup.Source.TakenAt.LocalDateTime:f}?\n\n" +
                "Everything you have done in this world since then will be gone. " +
                "A fresh backup of the current save is taken first, so this can be undone.",
                "Restore", "Cancel", danger: true);
            if (!ok) return;

            var ct = BeginBusy("Restoring...");
            try
            {
                // Non-null means the restore worked but the old save could not be deleted; tell the user
                // where it is.
                var leftover = await App.State.Worlds.RestoreAsync(_world, backup.Source.Path, BarProgress(), ct);
                await ReloadAsync();
                SetStatus(leftover is null
                    ? $"Restored the snapshot from {backup.Source.TakenAt.LocalDateTime:f}."
                    : $"Restored the snapshot from {backup.Source.TakenAt.LocalDateTime:f}. The old save is still "
                      + $"on disk as '{Path.GetFileName(leftover)}' - delete it once Minecraft has let go of it.",
                    StatusKind.Success);
            }
            finally { EndBusy(); }
        }
        catch (OperationCanceledException) { SetStatus("Restore cancelled."); }
        catch (IOException)
        {
            SetStatus("Close Minecraft before restoring this world - its files are in use.", StatusKind.Error);
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
        if (!SafeLaunch.RevealFile(backup.Source.Path)) SetStatus("Could not open Explorer.", StatusKind.Error);
    }

    private void OnOpenBackupsFolder(object sender, RoutedEventArgs e)
    {
        if (_world is null) return;
        try
        {
            var dir = App.State.Worlds.BackupsDir(_world.SourcePackId);
            Directory.CreateDirectory(dir);
            if (!SafeLaunch.OpenFolder(dir)) SetStatus("Could not open the folder.", StatusKind.Error);
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
        var wasSuppressed = _suppress;
        _suppress = true;
        try
        {
            SummaryBox.Text = _entry.Summary ?? "";
            DescriptionBox.Text = _entry.Description ?? "";
            VisibilityBox.SelectedIndex = (int)_entry.Visibility;
        }
        finally { _suppress = wasSuppressed; }
        // A linked save's boxes show the local copy until the hosted world is read and refills them,
        // so nothing here counts as an edit yet.
        _overviewEdited = false;
        SharedWorldStatusLabel.Text = _entry.SharedWorldId is Guid
            ? "Linked to a hosted world - reading it from the server..."
            : "Not shared yet. Save an overview, then upload a version to host this world for others.";
        ConfigureSharingState();
        OverviewSaveStatus.Text = "";
    }

    private void OnOverviewEdited(object sender, TextChangedEventArgs e)
    {
        if (!_suppress) _overviewEdited = true;
    }

    private void OnOverviewSelectionEdited(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppress) _overviewEdited = true;
    }

    // ── the hosted world's permissions ───────────────────────────────────────

    private bool IsLinked => _entry?.SharedWorldId is Guid;

    /// <summary><see cref="_hosted"/>, but only while it is still the world this save is linked to.</summary>
    private SharedWorldDetail? LinkedHosted => _hosted is { } h && _entry?.SharedWorldId == h.Id ? h : null;

    private bool IsHostedOwner => LinkedHosted is { } h && App.State.Settings.UserId is { } me && h.OwnerId == me;
    private PackPermissions HostedGrant => LinkedHosted?.EffectivePermissions ?? PackPermissions.None;
    private bool CanManageHosted => IsHostedOwner || HostedGrant.HasFlag(PackPermissions.ManageCollaborators);
    private bool CanUploadHosted => IsHostedOwner || HostedGrant.HasFlag(PackPermissions.UploadShared);
    private bool CanDownloadHosted => IsHostedOwner || HostedGrant.HasFlag(PackPermissions.Download);

    /// <summary>True when this person is on the linked world's collaborator list, the only access they
    /// can give back from here.</summary>
    private bool IsHostedDirectCollaborator =>
        !IsHostedOwner && LinkedHosted is { } h && App.State.Settings.UserId is { } me
        && h.Collaborators.Any(c => c.UserId == me);

    /// <summary>Enables the hosted-world controls based on what the server lets this person do, with a
    /// tooltip saying why when one is off.</summary>
    /// <remarks>An unlinked save can always be uploaded (that creates a hosted world the user owns). A
    /// linked save follows the world's grant, and write controls stay off until it is known.</remarks>
    private void ApplyHostedPermissions()
    {
        if (_entry is null) return;
        var linked = IsLinked;
        var hosted = LinkedHosted;
        var owner = hosted?.OwnerUsername ?? "its owner";
        var unknown = linked && hosted is null;

        WorldPermissionsButton.IsEnabled = linked && hosted is not null && CanManageHosted;
        WorldPermissionsButton.ToolTip = !linked
            ? "Upload this world first - there is nobody to share it with until it is hosted."
            : unknown ? HostedUnknownSentence()
            : CanManageHosted ? "Choose who can see, download, upload to or manage the hosted world"
            : $"Only {owner}, or somebody {owner} gave full access to, can change who has this world.";

        DeleteHostedWorldButton.IsEnabled = linked && IsHostedOwner;
        DeleteHostedWorldButton.ToolTip = !linked || unknown || IsHostedOwner
            ? "Take this world down from the server. Your local save is untouched."
            : $"Only {owner} can take this world down.";
        // A collaborator's copy offers Leave where the owner's offers Delete.
        DeleteHostedWorldButton.Visibility = linked && !unknown && !IsHostedOwner
            ? Visibility.Collapsed
            : Visibility.Visible;

        var canUpload = !linked || (hosted is not null && CanUploadHosted);
        var uploadHint = !linked
            ? "Zip this save and publish it as a hosted world you own"
            : unknown ? HostedUnknownSentence()
            : CanUploadHosted ? $"Zip this save and upload it as a new version of {hosted!.Name}"
            : $"You can download {hosted!.Name} but not upload to it. Ask {owner} for upload access.";
        UploadSharedWorldButton.IsEnabled = VersionsUploadButton.IsEnabled = canUpload && _uploadCts is null;
        UploadSharedWorldButton.ToolTip = VersionsUploadButton.ToolTip = uploadHint;

        // On upload the overview becomes the hosted world's details, which only the owner can change.
        var canEditOverview = !linked || IsHostedOwner;
        SummaryBox.IsReadOnly = DescriptionBox.IsReadOnly = !canEditOverview;
        VisibilityBox.IsEnabled = SaveOverviewButton.IsEnabled = canEditOverview;
        var overviewHint = canEditOverview
            ? null
            : unknown ? HostedUnknownSentence()
            : $"These are {owner}'s details for the hosted world. Only {owner} can change them.";
        VisibilityBox.ToolTip = SaveOverviewButton.ToolTip = overviewHint;

        LeaveHostedWorldButton.Visibility = IsHostedDirectCollaborator ? Visibility.Visible : Visibility.Collapsed;
        LeaveHostedWorldButton.ToolTip = $"Give back the access {owner} gave you. Your save stays on this PC.";

        if (hosted is not null)
            SharedWorldStatusLabel.Text = IsHostedOwner
                ? $"Published as {hosted.Name} · {hosted.Versions.Count} version(s) uploaded. Uploading adds a new version."
                : CanUploadHosted
                    ? $"A copy of {hosted.Name}, shared by {owner}. You can upload new versions of it."
                    : $"A copy of {hosted.Name}, shared by {owner}. You can download it, not upload to it.";
        else if (unknown && _hostedProblem is not null)
            SharedWorldStatusLabel.Text = HostedUnknownSentence();
    }

    private string HostedUnknownSentence() => _hostedProblem is { } why
        ? $"This save is linked to a hosted world you cannot open just now - {why}."
        : "Reading the hosted world from the server...";

    /// <summary>Why the linked world could not be read, as the end of that sentence.</summary>
    private static string DescribeHostedFailure(Exception ex) => ex switch
    {
        ApiException { Status: System.Net.HttpStatusCode.NotFound } => "it has been deleted from the server",
        ApiException { Status: System.Net.HttpStatusCode.Forbidden } => "your access to it has been taken away",
        _ => ContentBundleService.Explain(ex, ex.Message).TrimEnd('.')
    };

    /// <summary>Shows the hosted world's details in the overview, unless the user has started editing
    /// them here.</summary>
    /// <remarks>Starting from the server's values means any difference at upload time is a real edit,
    /// not a stale local copy that would reset the hosted world.</remarks>
    private void ShowHostedOverview(SharedWorldDetail hosted)
    {
        if (_overviewEdited) return;
        var wasSuppressed = _suppress;
        _suppress = true;
        try
        {
            SummaryBox.Text = hosted.Summary ?? "";
            DescriptionBox.Text = hosted.Description ?? "";
            VisibilityBox.SelectedIndex = (int)hosted.Visibility;
        }
        finally { _suppress = wasSuppressed; }
    }

    /// <summary>The overview fields the owner changed on this page as a patch, or null when there is
    /// nothing to send.</summary>
    /// <remarks>Never includes the name (that would rename the hosted world after the local folder) or
    /// the Minecraft version, which the upload records itself.</remarks>
    private UpdateWorldRequest? OverviewPatch(SharedWorldDetail hosted)
    {
        var summary = string.IsNullOrWhiteSpace(SummaryBox.Text) ? null : SummaryBox.Text.Trim();
        var description = string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text.Trim();
        var visibility = SelectedVisibility();

        var newSummary = summary is not null && summary != (hosted.Summary ?? "").Trim() ? summary : null;
        var newDescription = description is not null && description != (hosted.Description ?? "").Trim() ? description : null;
        PackVisibility? newVisibility = visibility != hosted.Visibility ? visibility : null;
        return newSummary is null && newDescription is null && newVisibility is null
            ? null
            : new UpdateWorldRequest(null, newSummary, newDescription, newVisibility);
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
        RefreshVersionsButton.IsEnabled = _entry.SharedWorldId is Guid;
        ApplyHostedPermissions();

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

    /// <summary>Turns the sharing controls on or off.</summary>
    /// <remarks>Hiding the controls does not unpublish anything, so when a hosted world exists the
    /// owner is told and offered to take it down.</remarks>
    private async void OnShareWorldToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress || _world is null) return;
        try
        {
            var enabled = ShareWorldBox.IsChecked == true;

            // Only the owner can take a hosted world down. For someone else's world this switch just
            // hides the controls.
            if (!enabled && _entry?.SharedWorldId is Guid && IsHostedOwner)
            {
                var takeDown = await AppDialog.ConfirmAsync(_shell, "This world is still published",
                    "Hiding the sharing controls does not unpublish the world - it stays on the server and " +
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

    /// <summary>The actions menu's "Host on CloudLauncher...": switches sharing on if needed, then runs
    /// the Overview tab's upload (<see cref="OnShareOrUploadWorld"/>).</summary>
    private void OnHostWorld(object sender, RoutedEventArgs e)
    {
        if (_world is null) return;
        try
        {
            if (!IsSharingEnabled())
            {
                App.State.Worlds.SetSharingEnabled(_world.Key, true);
                _entry = App.State.Worlds.GetOrCreate(_world.Key);
                // Under the guard: ConfigureSharingState assigns ShareWorldBox.IsChecked, which raises
                // the toggle handler, which would ask about taking a hosted world down.
                _suppress = true;
                try { ConfigureSharingState(); } finally { _suppress = false; }
            }
            OnShareOrUploadWorld(sender, e);
        }
        catch (Exception ex) { SetStatus(ex.Message, StatusKind.Error); }
    }

    private async void OnSaveOverview(object sender, RoutedEventArgs e)
    {
        if (_world is null) return;
        try
        {
            var visibility = SelectedVisibility();
            App.State.Worlds.UpdateOverview(_world.Key, SummaryBox.Text, DescriptionBox.Text, visibility);
            _entry = App.State.Worlds.GetOrCreate(_world.Key);

            // A linked save's overview is the hosted world's details, so the owner's Save goes
            // straight to the server. Only changed fields are sent.
            if (LinkedHosted is { } hosted && IsHostedOwner && OverviewPatch(hosted) is { } patch)
            {
                await App.State.Api.UpdateSharedWorldAsync(hosted.Id, patch);
                _overviewEdited = false;
                OverviewSaveStatus.Text = "Overview saved to the hosted world.";
                await LoadSharedWorldVersionsAsync();
                return;
            }

            ConfigureOverviewEditor();
            OverviewSaveStatus.Text = "Overview saved.";
        }
        catch (Exception ex)
        {
            OverviewSaveStatus.Text = "The overview could not be saved: " + ContentBundleService.Explain(ex, ex.Message);
        }
    }

    /// <summary>Publishes a new version of this world: zip, then upload, both cancellable and both
    /// reporting progress.</summary>
    /// <remarks>The zip runs off the dispatcher and is checked against the server's size cap before
    /// anything is sent.</remarks>
    private async void OnShareOrUploadWorld(object sender, RoutedEventArgs e)
    {
        if (_world is null) return;
        // The button is disabled in these cases, but the actions menu's "Host on CloudLauncher..."
        // gets here without it.
        if (IsLinked && (LinkedHosted is null || !CanUploadHosted))
        {
            SetStatus(UploadSharedWorldButton.ToolTip as string ?? "You cannot upload to this world.", StatusKind.Error);
            return;
        }
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
            TimeFormat.VersionStamp(DateTimeOffset.Now))
        { Owner = _shell };
        if (dlg.ShowDialog() != true) return;

        _uploadCts?.Cancel();
        _uploadCts = new CancellationTokenSource();
        var ct = _uploadCts.Token;

        // Disables both upload buttons, so the Versions tab can't start a second upload.
        ApplyHostedPermissions();
        CancelUploadButton.Visibility = Visibility.Visible;
        UploadBar.Visibility = Visibility.Visible;
        UploadBar.IsIndeterminate = true;
        UploadBar.Value = 0;
        SetStatus("Zipping the save...");

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

            SetStatus($"Uploading {FormatSize(zipBytes)}...");
            var sent = new Progress<long>(done =>
            {
                UploadBar.IsIndeterminate = false;
                UploadBar.Value = 0.5 + Math.Min(1.0, done / (double)zipBytes) * 0.5;
            });
            await App.State.Api.UploadSharedWorldVersionAsync(sharedId, zipPath, meta, ct, sent);

            SetStatus($"Uploaded version {dlg.Version}.", StatusKind.Success);
            await LoadSharedWorldVersionsAsync();
        }
        catch (OperationCanceledException) { SetStatus("Upload cancelled."); }
        catch (Exception ex) { SetStatus("Share failed: " + ContentBundleService.Explain(ex, ex.Message), StatusKind.Error); }
        finally
        {
            CancelUploadButton.Visibility = Visibility.Collapsed;
            UploadBar.Visibility = Visibility.Collapsed;
            UploadBar.IsIndeterminate = false;
            _uploadCts?.Dispose();
            _uploadCts = null;
            ApplyHostedPermissions();
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
            _hosted = detail;
            if (!CanManageHosted)
            {
                // The grant can have changed since the page drew the button.
                ApplyHostedPermissions();
                SetStatus(WorldPermissionsButton.ToolTip as string ?? "You cannot change who has this world.",
                          StatusKind.Error);
                return;
            }
            new PermissionsDialog(detail) { Owner = _shell }.ShowDialog();
            await LoadSharedWorldVersionsAsync();
        }
        catch (Exception ex)
        {
            SetStatus("Could not load permissions: " + ContentBundleService.Explain(ex, ex.Message), StatusKind.Error);
        }
    }

    /// <summary>Gives back the access the linked world's owner granted, after asking.</summary>
    /// <remarks>The save stays on this PC. If a team or public visibility still grants access the link
    /// stays; otherwise the save is unlinked and becomes an ordinary local world.</remarks>
    private async void OnLeaveHostedWorld(object sender, RoutedEventArgs e)
    {
        if (_world is null || LinkedHosted is not { } hosted || App.State.Settings.UserId is not { } me
            || !IsHostedDirectCollaborator)
            return;
        try
        {
            if (!await AppDialog.ConfirmAsync(_shell, "Leave world",
                    $"Leave '{hosted.Name}'? You lose the access {hosted.OwnerUsername} gave you, and only they "
                    + "can give it back. Your save stays on this PC. If one of your teams also has the world, "
                    + "you keep what the team gives.",
                    "Leave", "Cancel", danger: true))
                return;

            LeaveHostedWorldButton.IsEnabled = false;
            await App.State.Api.RemoveSharedWorldCollaboratorAsync(hosted.Id, me);
        }
        catch (Exception ex)
        {
            LeaveHostedWorldButton.IsEnabled = true;
            SetStatus("Could not leave: " + ContentBundleService.Explain(ex, ex.Message), StatusKind.Error);
            return;
        }

        LeaveHostedWorldButton.IsEnabled = true;
        try
        {
            _hosted = await App.State.Api.GetSharedWorldAsync(hosted.Id);
            SetStatus($"You left {hosted.Name}. What is left comes from a team or from it being public.",
                      StatusKind.Success);
            await LoadSharedWorldVersionsAsync();
        }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(WorldDetailView), ex);
            _hosted = null;
            App.State.Worlds.UnlinkSharedWorld(_world.Key);
            App.State.Worlds.SetSharingEnabled(_world.Key, false);
            _entry = App.State.Worlds.GetOrCreate(_world.Key);
            _versionRows.Clear();
            _suppress = true;
            try { ConfigureOverviewEditor(); } finally { _suppress = false; }
            SetStatus($"You left {hosted.Name}. Your save stays on this PC as an ordinary local world.",
                      StatusKind.Success);
        }
    }

    private async void OnDeleteHostedWorld(object sender, RoutedEventArgs e)
    {
        try { await DeleteHostedWorldAsync(); }
        catch (Exception ex) { SetStatus("Could not delete the hosted world: " + ex.Message, StatusKind.Error); }
    }

    /// <summary>Takes the published copy down from the server. Returns true if it is gone.</summary>
    /// <remarks>The local save is never touched, and the confirmation says so.</remarks>
    private async Task<bool> DeleteHostedWorldAsync()
    {
        if (_world is null || _entry?.SharedWorldId is not Guid sharedId) return false;
        if (!IsHostedOwner)
        {
            SetStatus(DeleteHostedWorldButton.ToolTip as string ?? "Only the owner can take this world down.",
                      StatusKind.Error);
            return false;
        }

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
            _hosted = null;
            _versionRows.Clear();
            ConfigureOverviewEditor();
            SetStatus("The hosted world was deleted. Your local save is untouched.", StatusKind.Success);
            return true;
        }
        catch (Exception ex)
        {
            SetStatus("Could not delete the hosted world: " + ContentBundleService.Explain(ex, ex.Message), StatusKind.Error);
            return false;
        }
        finally { ApplyHostedPermissions(); }
    }

    private void SaveOverviewToSettings()
    {
        if (_world is null) return;
        App.State.Worlds.UpdateOverview(_world.Key, SummaryBox.Text, DescriptionBox.Text, SelectedVisibility());
        App.State.Worlds.SetSharingEnabled(_world.Key, true);
        _entry = App.State.Worlds.GetOrCreate(_world.Key);
    }

    /// <summary>The hosted world this upload goes to: the linked one, or a new one this user
    /// owns.</summary>
    /// <remarks>The grant is re-read here because the owner may have changed it since the page
    /// opened. Only the owner sends overview changes, and only the fields that differ.</remarks>
    private async Task<Guid> EnsureSharedWorldAsync(CancellationToken ct = default)
    {
        if (_world is null) throw new InvalidOperationException("World no longer exists.");
        _entry ??= App.State.Worlds.GetOrCreate(_world.Key);

        if (_entry.SharedWorldId is Guid existingId)
        {
            var hosted = await App.State.Api.GetSharedWorldAsync(existingId, ct);
            _hosted = hosted;
            if (!CanUploadHosted)
                throw new InvalidOperationException(
                    $"You can download {hosted.Name} but not upload to it. Ask {hosted.OwnerUsername} for upload access.");

            if (IsHostedOwner && OverviewPatch(hosted) is { } patch)
                await App.State.Api.UpdateSharedWorldAsync(existingId, patch, ct);
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
            _hosted = detail;
            _hostedProblem = null;
            ShowHostedOverview(detail);

            var downloadHint = CanDownloadHosted
                ? "Download this version into an instance"
                : $"You can see this world but not download it. Ask {detail.OwnerUsername} for download access.";
            _versionRows.Clear();
            foreach (var version in detail.Versions.OrderByDescending(v => v.PublishedAt))
                _versionRows.Add(SharedWorldVersionRow.From(version, IsHostedOwner, CanDownloadHosted, downloadHint));

            VersionsEmptyLabel.Text = "No hosted versions uploaded yet.";
            VersionsEmptyLabel.Visibility = _versionRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            _hosted = null;
            _hostedProblem = DescribeHostedFailure(ex);
            SetStatus("Could not load uploaded versions: " + ContentBundleService.Explain(ex, ex.Message), StatusKind.Error);
        }
        finally
        {
            RefreshVersionsButton.IsEnabled = _entry?.SharedWorldId is Guid;
            ApplyHostedPermissions();
        }
    }

    // ── uploaded version row actions ─────────────────────────────────────────

    /// <summary>Downloads one uploaded version into an instance's saves folder.</summary>
    private async void OnDownloadVersion(object sender, RoutedEventArgs e)
    {
        if (_entry?.SharedWorldId is not Guid sharedId) return;
        if (RowFromSender<SharedWorldVersionRow>(sender) is not { } version) return;
        if (!version.CanDownload) { SetStatus(version.DownloadHint, StatusKind.Error); return; }

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

            var ct = BeginBusy($"Downloading {version.VersionString}...");
            try
            {
                tmp = Path.Combine(Path.GetTempPath(), $"cl-world-{Guid.NewGuid():N}.zip");
                await using (var stream = await App.State.Api.DownloadSharedWorldVersionAsync(sharedId, version.Id, ct))
                await using (var fs = File.Create(tmp))
                    await stream.CopyToAsync(fs, ct);

                SetStatus($"Extracting into {pack.Name}...");
                var folder = await App.State.Worlds.ImportZipAsync(
                    tmp, pack.Id, pack.Name, _world?.DisplayName, BarProgress(), ct);
                // Link the new save to the same hosted world, so its page publishes there
                // instead of hosting a second one.
                App.State.Worlds.LinkSharedWorld(WorldService.Key(pack.Id, folder), sharedId);
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
        SetStatus(copied ? "Download link copied." : "The clipboard is busy - try again.",
                  copied ? StatusKind.Success : StatusKind.Error);
    }

    private async void OnDeleteVersion(object sender, RoutedEventArgs e)
    {
        if (_entry?.SharedWorldId is not Guid sharedId) return;
        if (RowFromSender<SharedWorldVersionRow>(sender) is not { } version) return;
        if (!IsHostedOwner) { SetStatus("Only the world's owner can delete a version.", StatusKind.Error); return; }
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

    // ── the Instances tab ────────────────────────────────────────────────────

    /// <summary>Fills the Instances tab: which instances this world is offered to, and what each one
    /// has done with it.</summary>
    /// <remarks>No disk access: uses the copies <see cref="ReadCopiesAsync"/> found and
    /// <see cref="ContentWriteGate"/>.</remarks>
    private void RefreshInstancesTab()
    {
        if (_world is null) return;
        FillInstanceStates();
    }

    /// <summary>The library's copy of this save, matched by file name.</summary>
    /// <remarks>Checks both the folder name and the zip name, so <c>Skyblock</c> matches a library item
    /// stored as <c>Skyblock</c> or <c>Skyblock.zip</c>.</remarks>
    private static LibraryItem? FindTemplate(WorldInfo world)
    {
        try
        {
            return App.State.Library.Scan(LibraryKind.World).FirstOrDefault(i =>
                string.Equals(i.FileName, world.FolderName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileNameWithoutExtension(i.FileName), world.FolderName,
                                 StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(WorldDetailView), ex);
            return null;
        }
    }

    /// <summary>Fills the instance list: which instances have this world, and what happens to the ones
    /// that do not.</summary>
    /// <remarks>The tick means "offered to this instance", as on the resource and shader pack pages
    /// (see <see cref="ContentPlacement.SetTicked"/>). Ticking an empty box copies the world in;
    /// unticking never deletes a copy, which is that instance's own world by then. Without a library
    /// copy the boxes are shown but disabled.</remarks>
    private void FillInstanceStates()
    {
        if (_world is null) return;

        _packRows.Clear();
        const string copied =
            "A world is COPIED into an instance, never shared with it: Minecraft writes a save "
            + "continuously while it runs, so two instances on the same bytes would corrupt both. ";
        InstanceStateNote.Text = copied + (_template is null
            ? "Every instance is ticked to begin with. Nothing is being handed out from this world yet "
              + "- switch it on on the Worlds page and the launcher takes its own copy, then these "
              + "ticks decide which instances get one."
            : _template.AutoApply
                ? "Every instance is ticked to begin with. Ticking an empty one copies this world in; "
                  + "each copy is a SEPARATE world from the moment it is played. Unticking stops "
                  + "offering it and DELETES NOTHING - a copy already made stays where it is."
                : "Every instance is ticked to begin with. This world is switched OFF on the Worlds "
                  + "page, so nothing is copied anywhere yet - these ticks are what will happen when "
                  + "you switch it on.");

        // Name the instances that already have a copy, since the Worlds page lists a world only once.
        if (_copies.Count > 0)
        {
            var names = _allPacks.Where(p => _copies.ContainsKey(p.Id))
                                 .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                                 .Select(p => p.Name)
                                 .ToList();
            InstanceStateNote.Text += $"  {string.Join(", ", names)} also "
                                    + $"{(names.Count == 1 ? "has" : "have")} a save of this name - "
                                    + $"{names.Count} separate save{(names.Count == 1 ? "" : "s")} "
                                    + "with separate progress, because a save is never shared. Each "
                                    + "one is listed below with what has become of it.";
        }

        foreach (var pack in _allPacks.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var isSource = pack.Id == _world.SourcePackId;
            var copy = _copies.GetValueOrDefault(pack.Id);
            var has = isSource || copy is not null;

            InstanceContentOverrides? overrides = null;
            try { overrides = App.State.ContentDefaults.OverridesFor(pack.Id); }
            catch (Exception ex) { AppLog.LogError(nameof(WorldDetailView), ex); }

            _packRows.Add(new WorldInstanceRow
            {
                PackId = pack.Id,
                PackName = pack.Name + (isSource ? "  (this save lives here)" : ""),
                VersionLabel = pack.IsEmpty ? "EMPTY"
                    : pack.Loader == LoaderKind.None ? $"MC {pack.MinecraftVersion}"
                    : $"MC {pack.MinecraftVersion} · {pack.Loader}",
                StateLine = DescribeInstance(pack, copy),
                HasCopy = has,
                // The save's own instance is always ticked: the world lives there and was never placed.
                IsOffered = isSource || _template is null
                    || ContentPlacement.IsTicked(_template, pack, overrides),
                CanTick = !isSource && _template is not null,
                Diverged = copy is { Diverged: true },
                TickTip = isSource
                    ? "This is the instance the save lives in."
                    : _template is null
                        ? "Switch this world on on the Worlds page first - the launcher needs its own "
                          + "copy before it can hand one to anybody."
                        : copy is not null
                            ? $"{pack.Name} has its own copy at saves/{copy.Folder}. Unticking stops "
                              + "this world being offered here and leaves that copy alone - it is that "
                              + "instance's world now. To delete it, open this world's '...' menu on the "
                              + $"Worlds page and pick Delete from {pack.Name}."
                            : $"Copy this world into {pack.Name}. It becomes a separate save there: "
                              + "playing it changes nothing here, and nothing is ever copied back."
            });
        }
    }

    /// <summary>A box was ticked or unticked: writes the whole column back (see
    /// <see cref="ContentPlacement.SetTicked"/>), then offers to copy into a newly ticked
    /// instance.</summary>
    /// <remarks>Only the copy asks for confirmation, since it can be gigabytes and can't be
    /// undone.</remarks>
    private async void OnInstanceTick(object sender, RoutedEventArgs e)
    {
        if (_suppress || _world is null) return;
        if (sender is not FrameworkElement { DataContext: WorldInstanceRow row }) return;
        if (_template is not { } item)
        {
            if (sender is ToggleButton dead) dead.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            return;
        }

        try
        {
            var ticked = _packRows.Where(r => r.IsOffered).Select(r => r.PackId).ToHashSet();
            ContentPlacement.SetTicked(item, _allPacks, ticked);

            if (!row.IsOffered)
            {
                SetStatus($"{_world.DisplayName} is no longer offered to {row.PackName.Trim()}."
                        + (row.HasCopy ? " The copy already there stays exactly where it is." : ""));
                await ReloadAsync();
                return;
            }

            if (row.HasCopy) { await ReloadAsync(); return; }

            var pack = _allPacks.FirstOrDefault(p => p.Id == row.PackId);
            if (pack is null) { SetStatus("That instance is no longer in your list.", StatusKind.Error); return; }
            await CopyIntoAsync(pack);
        }
        catch (Exception ex) { SetStatus("That could not be saved: " + ex.Message, StatusKind.Error); }
    }

    /// <summary>Which instances hold a copy of this save, and whether that copy has been played
    /// since.</summary>
    /// <remarks>Kept cheap: a folder check and one file stamp per instance, off the dispatcher. A copy
    /// is recognised by the placement record (<see cref="InstanceContentOverrides.WorldFolderFor"/>,
    /// which survives renames) or by a folder of the same name. It has diverged once level.dat's write
    /// time differs: <see cref="WorldService.CopyWorldAsync"/> keeps the time, and Minecraft rewrites
    /// level.dat on every save.</remarks>
    private async Task<Dictionary<Guid, InstanceCopy>> ReadCopiesAsync(WorldInfo world, string? templateKey)
    {
        var packs = _allPacks.ToList();
        var sourceStamp = LevelStamp(world.FolderPath);

        return await Task.Run(() =>
        {
            var found = new Dictionary<Guid, InstanceCopy>();
            foreach (var pack in packs)
            {
                if (pack.Id == world.SourcePackId) continue;
                try
                {
                    var saves = App.State.Worlds.SavesDir(pack.Id, pack.Name);
                    string? folder = null;

                    if (templateKey is not null
                        && App.State.ContentDefaults.OverridesFor(pack.Id).WorldFolderFor(templateKey)
                            is { Length: > 0 } recorded
                        && Directory.Exists(Path.Combine(saves, recorded)))
                        folder = recorded;

                    if (folder is null && Directory.Exists(Path.Combine(saves, world.FolderName)))
                        folder = world.FolderName;

                    if (folder is null) continue;

                    var path = Path.Combine(saves, folder);
                    var stamp = LevelStamp(path);
                    found[pack.Id] = new InstanceCopy(folder, path, stamp,
                        Diverged: stamp != sourceStamp);
                }
                catch (Exception ex) { AppLog.LogError(nameof(WorldDetailView), ex); }
            }
            return found;
        });
    }

    /// <summary>When this save's <c>level.dat</c> was last written, or null when there is none.</summary>
    private static DateTimeOffset? LevelStamp(string worldDir)
    {
        try
        {
            var level = Path.Combine(worldDir, "level.dat");
            return File.Exists(level) ? File.GetLastWriteTimeUtc(level) : null;
        }
        catch (Exception ex) { AppLog.LogError(nameof(WorldDetailView), ex); return null; }
    }

    /// <summary>Copies this save into one instance, with confirmation.</summary>
    /// <remarks>The only copy path on this page; <see cref="OnCopyToInstance"/> and the tick both
    /// use it.</remarks>
    private async Task CopyIntoAsync(PackSummary pack)
    {
        if (_world is null) return;
        try
        {
            if (!await AppDialog.ConfirmAsync(_shell, $"Copy into {pack.Name}",
                    $"Copy '{_world.DisplayName}' ({FormatSize(_world.SizeBytes)}) into {pack.Name}'s "
                    + "saves folder?\n\n"
                    + "It becomes a SEPARATE world there. Playing it in one instance changes nothing in "
                    + "the other, and neither is ever copied over the other.\n\n"
                    + "Removing it later means deleting that instance's save - this page will not do "
                    + "that for you.",
                    "Copy it in", "Cancel"))
                return;

            if (InUse(pack.Id, "copying a world into it")) return;

            var ct = BeginBusy($"Copying into {pack.Name}...");
            try
            {
                var folder = await App.State.Worlds.CopyToPackAsync(_world, pack.Id, pack.Name,
                                                                   BarProgress(), ct);
                EndBusy();
                // The Worlds page paints from a cached scan, and its folder stamp won't
                // notice this write.
                ScanCaches.InvalidatePack(pack.Id, ScanScope.Worlds);
                await ReloadAsync();
                SetStatus($"Copied into {pack.Name} as saves/{folder}. It is that instance's own world "
                        + "from now on.", StatusKind.Success);
            }
            finally { EndBusy(); }
        }
        catch (OperationCanceledException) { SetStatus("Copy cancelled."); }
        catch (Exception ex) { SetStatus("Copy failed: " + ex.Message, StatusKind.Error); }
    }

    /// <summary>One sentence on where this world stands in one instance.</summary>
    /// <remarks>An existing copy comes first, since nothing is ever copied over it. Otherwise the write
    /// gate says whether the tick can act now.</remarks>
    private string DescribeInstance(PackSummary pack, InstanceCopy? copy)
    {
        if (pack.Id == _world!.SourcePackId)
            return "this is the save on this page - it belongs to this instance and nothing on this page "
                 + "writes over it";

        if (copy is not null)
            return copy.Diverged
                ? $"has its own copy at saves/{copy.Folder}, played since it was copied - it is a "
                  + "different world from this one now, and nothing is ever copied over it"
                : $"has its own copy at saves/{copy.Folder} - the same save as this one so far, and "
                  + "nothing is ever copied over it again";

        return ContentWriteGate.Allows(App.State.Instances, pack.Id, duringLaunch: false, out var blocked)
            ? "does not have this world - tick to copy it in"
            : blocked;
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (_world is null) return;
        if (!SafeLaunch.OpenFolder(_world.FolderPath)) SetStatus("Could not open the folder.", StatusKind.Error);
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

        if (!SafeLaunch.OpenFolder(playerDataDir)) SetStatus("Could not open the folder.", StatusKind.Error);
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

    /// <summary>Launches an instance with this world, copying the save in first when it lives
    /// elsewhere.</summary>
    /// <remarks>If the target already has a copy, the user picks which one to play.</remarks>
    private async Task PlayAsync(Guid packId)
    {
        if (_world is null) return;
        SetStatus("Launching...");
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
                    // Replace is the confirm button, so dismissing the dialog plays the
                    // existing copy and loses no progress.
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
                    var ct = BeginBusy($"Copying the save into {pack.Name}...");
                    try
                    {
                        // Replace never deletes first: a copy cancelled halfway would
                        // leave a partial world. The swap waits for a complete copy.
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

    /// <summary>The row's delete action. Owner only, as on the server.</summary>
    public Visibility DeleteVisibility { get; init; } = Visibility.Visible;

    /// <summary>Whether this person may download the save; the server answers a bare 403 otherwise.</summary>
    public bool CanDownload { get; init; } = true;
    public string DownloadHint { get; init; } = "Download this version into an instance";

    public static SharedWorldVersionRow From(SharedWorldVersionInfo version, bool canDelete = true,
                                             bool canDownload = true, string? downloadHint = null) => new()
    {
        Id = version.Id,
        VersionString = version.VersionString,
        // PublishedAt is UTC; TimeFormat shows it in local time. Sorting still uses PublishedAt.
        MetaLabel = $"{version.FileName} · {FormatSize(version.FileSize)} · {TimeFormat.DateTime(version.PublishedAt)}",
        McVersionLabel = string.IsNullOrWhiteSpace(version.McVersion) ? "Any MC" : $"MC {version.McVersion}",
        Changelog = version.Changelog ?? "",
        DeleteVisibility = canDelete ? Visibility.Visible : Visibility.Collapsed,
        CanDownload = canDownload,
        DownloadHint = downloadHint ?? "Download this version into an instance"
    };

    private static string FormatSize(long bytes) =>
        bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
      : bytes >= 1024L * 1024 ? $"{bytes / (1024.0 * 1024):F1} MB"
      : bytes >= 1024 ? $"{bytes / 1024.0:F0} KB"
      : $"{bytes} B";
}

/// <summary>One instance's copy of the save on the page: the folder, and whether it has
/// diverged.</summary>
/// <param name="Folder">The folder name inside that instance's <c>saves/</c>. Can differ from this
/// save's, since <see cref="WorldService.UniqueFolderName"/> renames rather than overwrites.</param>
/// <param name="Path">The copy on disk.</param>
/// <param name="Stamp">Its <c>level.dat</c> write time, or null when it has none.</param>
/// <param name="Diverged">True once that stamp no longer matches this save's.</param>
public sealed record InstanceCopy(string Folder, string Path, DateTimeOffset? Stamp, bool Diverged);

/// <summary>One row in the world page's Instances tab.</summary>
/// <remarks>Not notifying: the page rebuilds the whole list after any change. The tick binds
/// one-way.</remarks>
public sealed class WorldInstanceRow
{
    /// <summary>The instance this row is about.</summary>
    public Guid PackId { get; init; }

    /// <summary>Its name, with a note when it is the instance the save lives in.</summary>
    public string PackName { get; init; } = "";

    /// <summary>"MC 1.21.1 · NeoForge", or EMPTY for an instance with nothing in it yet.</summary>
    public string VersionLabel { get; init; } = "";

    /// <summary>What is true here, in one sentence the user can act on.</summary>
    public string StateLine { get; init; } = "";

    /// <summary>True when this instance has this world: its own copy, or it is the instance the save
    /// lives in. The tick handler uses it to decide whether ticking means copying.</summary>
    public bool HasCopy { get; init; }

    /// <summary>Whether this world is offered to this instance (the tick).</summary>
    /// <remarks>Settable because <c>WorldDetailView.OnInstanceTick</c> reads the whole column back.
    /// Not the same as <see cref="HasCopy"/>: an instance can keep a copy it is no longer
    /// offered.</remarks>
    public bool IsOffered { get; set; }

    /// <summary>False on the save's own instance, and on every row while the launcher has no library
    /// copy to hand out.</summary>
    public bool CanTick { get; init; }

    /// <summary>The box's tooltip: what ticking does, or why the box is disabled.</summary>
    public string TickTip { get; init; } = "";

    /// <summary>True when this instance's copy has been played since it was made.</summary>
    public bool Diverged { get; init; }

    /// <inheritdoc cref="Diverged"/>
    public Visibility DivergedVisibility => Diverged ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>One instance, and where a piece of content stands in relation to it.</summary>
/// <remarks>A checkbox row on <see cref="LocalResourcePackDetailView"/>: <see cref="IsCompatible"/>
/// is the two-way bound "may be used by" box and <see cref="IsEditable"/> greys out the source
/// instance.</remarks>
public sealed class PackCompatibilityRow : INotifyPropertyChanged
{
    private bool _isCompatible;
    private bool _isEditable;

    /// <summary>The instance this row is about.</summary>
    public Guid PackId { get; init; }

    /// <summary>Its name, with a note when it is the instance the content lives in.</summary>
    public string PackName { get; init; } = "";

    /// <summary>"MC 1.21.1 · NeoForge", or EMPTY for an instance with nothing in it yet.</summary>
    public string VersionLabel { get; init; } = "";

    /// <summary>What would happen here, in one sentence the user can act on.</summary>
    /// <remarks>Set once when the list is filled; the page rebuilds the list when it could
    /// change.</remarks>
    public string StateLine { get; init; } = "";

    /// <summary>Resource pack pages only: the two-way bound "may be used by this instance" box.</summary>
    public bool IsCompatible
    {
        get => _isCompatible;
        set { _isCompatible = value; OnPropertyChanged(); }
    }

    /// <summary>Resource pack pages only: false greys the box out (the source instance, or a pack
    /// marked compatible with everything).</summary>
    public bool IsEditable
    {
        get => _isEditable;
        set { _isEditable = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
