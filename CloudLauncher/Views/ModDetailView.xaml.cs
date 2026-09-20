using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// The "Manage" page for a mod hosted on this CloudLauncher server: its editable overview and
/// compatibility, its collaborators, and the list of uploaded versions with per-version actions.
/// </summary>
/// <remarks>
/// Everything an owner can do to a hosted mod lives here, so the page guards against losing work:
/// edits mark the page dirty, Save is only enabled when there is something to save, Ctrl+S saves,
/// and <see cref="TryHandleBack"/> intercepts the side panel's Back button while dirty.
/// </remarks>
public partial class ModDetailView : Page, ISidePanelBackHandler
{
    private readonly MainWindow _shell;
    private readonly Guid _modId;
    private HostedModDetail? _mod;

    /// <summary>Set while <see cref="ApplyMod"/> writes the controls, so seeding them does not
    /// count as the user editing them.</summary>
    private bool _suppressDirty;

    private bool _dirty;
    private bool _isOwner;

    /// <summary>Guards the per-version install/download actions so two clicks cannot race each
    /// other into the same instance folder.</summary>
    private bool _busy;

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
        catch (Exception ex) { SetStatus("Failed to load: " + ex.Message, error: true); }
    }

    private void ApplyMod()
    {
        if (_mod is null) return;
        _suppressDirty = true;
        try
        {
            ModNameLabel.Text = _mod.Name;
            VisibilityLabel.Text = _mod.Visibility.ToString();
            MetaLabel.Text =
                $"by {_mod.OwnerUsername} · {_mod.Versions.Count} version{(_mod.Versions.Count == 1 ? "" : "s")} · updated {_mod.UpdatedAt.LocalDateTime:d}";
            SlugLabel.Text = _mod.Slug;

            _isOwner = App.State.Settings.UserId == _mod.OwnerId;
            NameBox.Text = _mod.Name;
            SummaryBox.Text = _mod.Summary ?? "";
            DescriptionBox.Text = _mod.Description ?? "";
            VisibilityBox.SelectedIndex = (int)_mod.Visibility;
            McVersionsBox.Text = _mod.McVersionsCsv ?? "";
            SetLoaderBoxes(_mod.LoadersCsv);

            NameBox.IsReadOnly = !_isOwner;
            SummaryBox.IsReadOnly = !_isOwner;
            DescriptionBox.IsReadOnly = !_isOwner;
            McVersionsBox.IsReadOnly = !_isOwner;
            VisibilityBox.IsEnabled = _isOwner;
            LoaderFabric.IsEnabled = _isOwner;
            LoaderForge.IsEnabled = _isOwner;
            LoaderNeoForge.IsEnabled = _isOwner;
            LoaderQuilt.IsEnabled = _isOwner;

            var ownerOnly = _isOwner ? Visibility.Visible : Visibility.Collapsed;
            SaveButton.Visibility = ownerOnly;
            PermissionsButton.Visibility = ownerOnly;
            UploadVersionButton.Visibility = ownerOnly;
            DeleteModButton.Visibility = ownerOnly;

            VersionsHeader.Text = _mod.Versions.Count == 0
                ? "Versions"
                : $"Versions ({_mod.Versions.Count})";
            VersionsList.ItemsSource = _mod.Versions
                .OrderByDescending(v => v.PublishedAt)
                .Select(v => new VersionRowVm(v, _isOwner))
                .ToList();
            VersionsEmpty.Visibility = _mod.Versions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            VersionsEmptyHint.Visibility = _isOwner ? Visibility.Visible : Visibility.Collapsed;

            IconFallback.Text = string.IsNullOrWhiteSpace(_mod.Name)
                ? "?"
                : _mod.Name.Trim()[0].ToString().ToUpperInvariant();
            IconActions.Visibility = ownerOnly;
            RemoveIconButton.IsEnabled = _mod.IconBlobHash is not null;
            // Fetched off the seed path: the icon is decoration, and a slow or missing one must not
            // hold up the fields the owner came here to edit.
            _ = LoadIconAsync(_mod.Id, _mod.IconBlobHash);

            MarkClean();
        }
        finally { _suppressDirty = false; }
    }

    // ── icon ─────────────────────────────────────────────────────────────────

    /// <summary>Shows the mod's icon, falling back to the letter tile.</summary>
    /// <remarks>
    /// The bytes come through the API client rather than being handed to the &lt;Image&gt; as a URL:
    /// the icon route is authorised for anything that is not public, and an image binding sends no
    /// token. The client writes the file to a content-addressed cache, so this is a download exactly
    /// once per distinct icon and a file read every time after.
    /// <para>The mod id is re-checked before the image is assigned. The page can be reloaded while a
    /// fetch is in the air, and an icon arriving for a mod that is no longer on screen must not paint
    /// over the one that is.</para>
    /// </remarks>
    private async Task LoadIconAsync(Guid modId, string? iconBlobHash)
    {
        IconImage.Source = null;
        if (iconBlobHash is null) return;
        try
        {
            var path = await App.State.Api.GetModIconFileAsync(modId, iconBlobHash);
            if (path is null || _mod?.Id != modId) return;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // decode now, then let go of the file
            bmp.DecodePixelWidth = 128;                 // 64 px well on a high-DPI screen
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            if (bmp.CanFreeze) bmp.Freeze();
            IconImage.Source = bmp;
        }
        catch (Exception ex)
        {
            // A missing or corrupt icon leaves the letter tile showing, which is a fine answer.
            AppLog.LogError("ModDetailView.LoadIcon", ex);
        }
    }

    private async void OnChangeIcon(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Pick an icon for this mod",
                Filter = "Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg|All files|*"
            };
            if (dlg.ShowDialog(_shell) != true) return;
            await UploadIconAsync(dlg.FileName);
        }
        catch (Exception ex) { SetStatus("Could not set the icon: " + ex.Message, error: true); }
    }

    private void OnIconDragOver(object sender, DragEventArgs e)
    {
        e.Effects = _isOwner && DroppedImage(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnIconDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        try
        {
            if (!_isOwner) return;
            if (DroppedImage(e) is { } path) await UploadIconAsync(path);
        }
        catch (Exception ex) { SetStatus("Could not set the icon: " + ex.Message, error: true); }
    }

    /// <summary>The single image in a drag payload. Several files at once is ignored rather than
    /// guessed at — a mod has exactly one icon.</summary>
    private static string? DroppedImage(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        var files = e.Data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>();
        if (files.Length != 1 || !File.Exists(files[0])) return null;
        var ext = Path.GetExtension(files[0]);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            ? files[0]
            : null;
    }

    /// <remarks>The size is checked here as well as on the server so dropping a 4K screenshot fails
    /// instantly with the reason, instead of after however long it takes to send it.</remarks>
    private async Task UploadIconAsync(string filePath)
    {
        if (_mod is null || !_isOwner) return;

        var info = new FileInfo(filePath);
        if (info.Length > MaxIconBytes)
        {
            SetStatus($"That image is {info.Length / 1024} KB. Icons have to be 1024 KB or smaller.", error: true);
            return;
        }

        SetBusy(true, "Uploading icon…");
        ChangeIconButton.IsEnabled = false;
        try
        {
            await App.State.Api.UploadModIconAsync(_mod.Id, filePath);
            await ReloadAsync();
            SetStatus("Icon updated.", error: false);
        }
        catch (Exception ex) { SetStatus("Icon upload failed: " + ex.Message, error: true); }
        finally
        {
            SetBusy(false, null);
            ChangeIconButton.IsEnabled = true;
        }
    }

    private async void OnRemoveIcon(object sender, RoutedEventArgs e)
    {
        if (_mod is null || !_isOwner) return;
        try
        {
            var ok = await AppDialog.ConfirmAsync(_shell, "Remove icon",
                $"Go back to the letter tile for {_mod.Name}?",
                "Remove", "Cancel", danger: false);
            if (!ok) return;

            RemoveIconButton.IsEnabled = false;
            await App.State.Api.DeleteModIconAsync(_mod.Id);
            await ReloadAsync();
            SetStatus("Icon removed.", error: false);
        }
        catch (Exception ex)
        {
            RemoveIconButton.IsEnabled = true;
            SetStatus("Could not remove the icon: " + ex.Message, error: true);
        }
    }

    /// <summary>Mirrors the server's cap so an oversized file is refused before it is sent.</summary>
    private const long MaxIconBytes = 1024 * 1024;

    // ── dirty tracking ───────────────────────────────────────────────────────

    private void OnFieldChanged(object sender, TextChangedEventArgs e) => MarkDirty();
    private void OnFieldSelectionChanged(object sender, SelectionChangedEventArgs e) => MarkDirty();
    private void OnFieldChecked(object sender, RoutedEventArgs e) => MarkDirty();

    private void MarkDirty()
    {
        if (_suppressDirty || !_isOwner) return;
        if (!_dirty)
        {
            _dirty = true;
            SaveButton.IsEnabled = true;
        }
        // "Saved." belongs to the previous save, not to what is on screen now.
        SaveStatus.Text = "Unsaved changes";
    }

    private void MarkClean()
    {
        _dirty = false;
        SaveButton.IsEnabled = false;
    }

    /// <summary>Side-panel Back: an owner with unsaved edits is asked first. The confirm is async
    /// and Back is not, so we consume this press and close the panel ourselves once they answer.</summary>
    public bool TryHandleBack()
    {
        if (!_dirty) return false;
        _ = ConfirmDiscardThenCloseAsync();
        return true;
    }

    private async Task ConfirmDiscardThenCloseAsync()
    {
        try
        {
            var discard = await AppDialog.ConfirmAsync(_shell, "Unsaved changes",
                $"Discard your changes to {_mod?.Name ?? "this mod"}?", "Discard", "Keep editing", danger: true);
            if (!discard) return;
            MarkClean();
            _shell.CloseSidePanel();
        }
        catch (Exception ex) { SetStatus(ex.Message, error: true); }
    }

    private void OnPageKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && SaveButton.IsEnabled)
        {
            OnSave(SaveButton, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    // ── overview ─────────────────────────────────────────────────────────────

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_mod is null) return;
        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            SaveStatus.Text = "A mod needs a name.";
            NameBox.Focus();
            return;
        }

        var visText = (VisibilityBox.SelectedItem as ComboBoxItem)?.Content as string ?? "Private";
        var vis = Enum.TryParse<PackVisibility>(visText, out var v) ? v : PackVisibility.Private;

        var req = new UpdateModRequest(
            name,
            SummaryBox.Text,
            DescriptionBox.Text,
            vis,
            NormalizeCsv(McVersionsBox.Text),
            LoadersCsvFromBoxes());

        SaveStatus.Text = "Saving…";
        SaveButton.IsEnabled = false;
        try
        {
            await App.State.Api.UpdateModAsync(_mod.Id, req);
            await ReloadAsync();
            SaveStatus.Text = "Saved.";
            SetStatus("", error: false);
        }
        catch (Exception ex)
        {
            SaveStatus.Text = "Error: " + ex.Message;
            SaveButton.IsEnabled = true;
        }
    }

    private void OnEditPermissions(object sender, RoutedEventArgs e)
    {
        if (_mod is null) return;
        new PermissionsDialog(_mod) { Owner = _shell }.ShowDialog();
        _ = ReloadAsync();
    }

    private async void OnDeleteMod(object sender, RoutedEventArgs e)
    {
        if (_mod is null || !_isOwner) return;
        try
        {
            var versions = _mod.Versions.Count;
            var consequence = versions == 0
                ? "It has no uploaded versions."
                : $"Its {versions} uploaded version{(versions == 1 ? "" : "s")} will stop being downloadable for everyone it is shared with.";
            var ok = await AppDialog.ConfirmAsync(_shell, "Delete mod",
                $"Delete {_mod.Name}? {consequence} This cannot be undone.",
                "Delete", "Cancel", danger: true);
            if (!ok) return;

            DeleteModButton.IsEnabled = false;
            await App.State.Api.DeleteModAsync(_mod.Id);
            // Nothing left for this page to show — drop back to whatever opened it.
            MarkClean();
            _shell.CloseSidePanel();
        }
        catch (Exception ex)
        {
            DeleteModButton.IsEnabled = true;
            SetStatus("Delete failed: " + ex.Message, error: true);
        }
    }

    // ── versions ─────────────────────────────────────────────────────────────

    private async void OnUploadVersion(object sender, RoutedEventArgs e)
    {
        if (_mod is null || !_isOwner) return;
        try
        {
            var created = await UploadModVersionDialog.ShowAsync(_shell, _mod);
            if (created is null) return;
            SetStatus($"Uploaded {created.VersionString}.", error: false);
            await ReloadAsync();
        }
        catch (Exception ex) { SetStatus("Upload failed: " + ex.Message, error: true); }
    }

    private void OnToggleChangelog(object sender, RoutedEventArgs e)
    {
        if (RowFrom(sender) is { } row) row.IsExpanded = !row.IsExpanded;
    }

    /// <summary>The "…" button opens the row's own context menu, so the mouse and the keyboard
    /// reach exactly the same set of actions.</summary>
    private void OnVersionOptions(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        for (DependencyObject? o = fe; o is not null; o = VisualTreeHelper.GetParent(o))
        {
            if (o is FrameworkElement { ContextMenu: { } menu } host)
            {
                menu.PlacementTarget = host;
                menu.Placement = PlacementMode.Bottom;
                menu.IsOpen = true;
                return;
            }
        }
    }

    private async void OnInstallVersion(object sender, RoutedEventArgs e) => await InstallAsync(RowFrom(sender));
    private async void OnCtxInstallVersion(object sender, RoutedEventArgs e) => await InstallAsync(RowFrom(sender));

    private async Task InstallAsync(VersionRowVm? row)
    {
        if (row is null || _mod is null || _busy) return;
        _busy = true;
        try
        {
            var pack = await PickTargetPackAsync(row.Source);
            if (pack is null) return;

            var folder = ModsFolderFor(pack);
            var dest = UniqueFilePath(folder, row.Source.FileName);
            SetBusy(true, $"Installing {row.Source.FileName} to {pack.Name}…");
            await DownloadToFileAsync(row, dest, $"Installing {row.Source.FileName} to {pack.Name}");
            SetStatus($"Installed {Path.GetFileName(dest)} to {pack.Name}.", error: false);
        }
        catch (Exception ex) { SetStatus("Install failed: " + ex.Message, error: true); }
        finally
        {
            SetBusy(false, null);
            _busy = false;
        }
    }

    private async void OnCtxSaveVersionAs(object sender, RoutedEventArgs e)
    {
        if (RowFrom(sender) is not { } row || _mod is null || _busy) return;
        _busy = true;
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = row.Source.FileName,
                Filter = "Mod jars (*.jar)|*.jar|All files|*"
            };
            if (dlg.ShowDialog(_shell) != true) return;

            SetBusy(true, $"Saving {row.Source.FileName}…");
            await DownloadToFileAsync(row, dlg.FileName, $"Saving {row.Source.FileName}");
            SetStatus("Saved to " + dlg.FileName, error: false);
        }
        catch (Exception ex) { SetStatus("Download failed: " + ex.Message, error: true); }
        finally
        {
            SetBusy(false, null);
            _busy = false;
        }
    }

    private void OnCtxCopyVersionString(object sender, RoutedEventArgs e)
    {
        if (RowFrom(sender) is not { } row) return;
        SetStatus(ClipboardHelper.TrySetText(row.VersionString)
            ? $"Copied {row.VersionString}."
            : "The clipboard is in use by another program.", error: false);
    }

    private void OnCtxCopyHash(object sender, RoutedEventArgs e)
    {
        if (RowFrom(sender) is not { } row) return;
        SetStatus(ClipboardHelper.TrySetText(row.Source.BlobHash)
            ? "Copied the file's SHA-256."
            : "The clipboard is in use by another program.", error: false);
    }

    /// <summary>Corrects an uploaded version's details without replacing its file.</summary>
    /// <remarks>
    /// Reuses the upload card in edit mode rather than growing a second form: the fields are the
    /// same four, and keeping one card means the rules about what a publishable version looks like
    /// are stated in exactly one place.
    /// </remarks>
    private async void OnCtxEditVersion(object sender, RoutedEventArgs e)
    {
        if (RowFrom(sender) is not { } row || _mod is null || !_isOwner) return;
        try
        {
            var updated = await UploadModVersionDialog.ShowEditAsync(_shell, _mod, row.Source);
            if (updated is null) return;
            SetStatus($"Updated {updated.VersionString}.", error: false);
            await ReloadAsync();
        }
        catch (Exception ex) { SetStatus("Could not save the version: " + ex.Message, error: true); }
    }

    private async void OnCtxDeleteVersion(object sender, RoutedEventArgs e)
    {
        if (RowFrom(sender) is not { } row || _mod is null || !_isOwner) return;
        try
        {
            var ok = await AppDialog.ConfirmAsync(_shell, "Delete version",
                $"Delete {row.VersionString} of {_mod.Name}? Anyone who has not installed it yet will no longer be able to.",
                "Delete", "Cancel", danger: true);
            if (!ok) return;

            await App.State.Api.DeleteModVersionAsync(_mod.Id, row.Id);
            SetStatus($"Deleted {row.VersionString}.", error: false);
            await ReloadAsync();
        }
        catch (Exception ex) { SetStatus("Delete failed: " + ex.Message, error: true); }
    }

    // ── install helpers ──────────────────────────────────────────────────────

    /// <summary>Streams one version to <paramref name="destination"/>, driving the page's bar.</summary>
    /// <remarks>
    /// <para>The bar is determinate whenever the response carries a Content-Length, which it does for
    /// every version the blob store serves; it drops back to the spinner only if a proxy chunked the
    /// body and the total is unknown. A 30 MB jar over a slow link used to be an indeterminate
    /// barber's pole that said nothing about whether it was nearly done.</para>
    /// <para>The reporter writes to the UI from <see cref="Progress{T}"/>'s captured context, and is
    /// throttled to whole percent: a 64 KB copy buffer means thousands of callbacks per file, and
    /// re-rendering the bar for each of them costs more than the download.</para>
    /// </remarks>
    private async Task DownloadToFileAsync(VersionRowVm row, string destination, string what)
    {
        if (_mod is null) return;

        var lastPercent = -1;
        var progress = new Progress<(long done, long total)>(p =>
        {
            if (p.total <= 0)
            {
                BusyBar.IsIndeterminate = true;
                SetStatus($"{what}… {FormatSize(p.done)}", error: false);
                return;
            }
            var percent = (int)(p.done * 100 / p.total);
            if (percent == lastPercent) return;
            lastPercent = percent;
            BusyBar.IsIndeterminate = false;
            BusyBar.Value = percent;
            SetStatus($"{what}… {FormatSize(p.done)} of {FormatSize(p.total)}", error: false);
        });

        try
        {
            await using var stream = await App.State.Api.DownloadModVersionAsync(
                _mod.Id, row.Id, progress: progress);
            await using var fs = File.Create(destination);
            await stream.CopyToAsync(fs);
        }
        finally
        {
            // Left as it was found, so the next indeterminate use of the bar still spins.
            BusyBar.IsIndeterminate = true;
            BusyBar.Value = 0;
        }
    }


    /// <summary>Asks which instance a jar should land in, offering only instances this build can
    /// actually run in — a modded profile on a Minecraft version and loader the build advertises.</summary>
    private async Task<PackSummary?> PickTargetPackAsync(HostedModVersionInfo version)
    {
        var packs = await App.State.Api.ListPacksAsync();
        if (packs.Count == 0)
        {
            await AppDialog.MessageAsync(_shell, "No instance",
                "Create an instance before installing mods.");
            return null;
        }

        var candidates = packs.Where(p => MatchesPack(version, p)).ToList();
        if (candidates.Count == 0)
        {
            await AppDialog.MessageAsync(_shell, "No compatible instance",
                $"No instance matches {version.VersionString}. It needs a non-empty Fabric, Forge, NeoForge or Quilt instance on a Minecraft version this build supports.");
            return null;
        }

        var picker = new PackPickerDialog(candidates, "Install mod to...",
            "Only instances matching this version's Minecraft version and loader are shown.", "Install")
        { Owner = _shell };
        return picker.ShowDialog() == true && picker.SelectedPackId is { } id
            ? candidates.First(p => p.Id == id)
            : null;
    }

    /// <summary>A version's own compatibility wins; where it says nothing, the mod's own CSVs apply
    /// — the same fallback the Mods screen uses, so both places offer the same instances.</summary>
    private bool MatchesPack(HostedModVersionInfo version, PackSummary pack)
    {
        if (pack.IsEmpty || string.IsNullOrWhiteSpace(pack.MinecraftVersion) || pack.Loader == LoaderKind.None)
            return false;

        var mcCsv = string.IsNullOrWhiteSpace(version.McVersionsCsv) ? _mod?.McVersionsCsv : version.McVersionsCsv;
        var loadersCsv = string.IsNullOrWhiteSpace(version.LoadersCsv) ? _mod?.LoadersCsv : version.LoadersCsv;
        return CsvContains(mcCsv, pack.MinecraftVersion!)
            && CsvContains(loadersCsv, pack.Loader.ToString().ToLowerInvariant());
    }

    private static bool CsvContains(string? csv, string expected) =>
        !string.IsNullOrWhiteSpace(csv)
        && csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(v => string.Equals(v, expected, StringComparison.OrdinalIgnoreCase));

    private static string ModsFolderFor(PackSummary pack)
    {
        App.State.Packs.EnsurePackFolder(pack.Id, pack.Name, pack.IsShared);
        var folder = Path.Combine(App.State.Packs.GameDir(pack.Id, pack.Name), "mods");
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>A writable path in <paramref name="folder"/> for <paramref name="fileName"/>, never
    /// overwriting a jar that is already installed.</summary>
    private static string UniqueFilePath(string folder, string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string((fileName ?? "mod.jar").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(clean)) clean = "mod.jar";
        if (!clean.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) clean += ".jar";

        var candidate = Path.Combine(folder, clean);
        var stem = Path.GetFileNameWithoutExtension(clean);
        var ext = Path.GetExtension(clean);
        var n = 2;
        while (File.Exists(candidate))
            candidate = Path.Combine(folder, $"{stem}-{n++}{ext}");
        return candidate;
    }

    // ── small helpers ────────────────────────────────────────────────────────

    /// <summary>Resolves the version a click came from: a button carries it in Tag, a menu item
    /// through the context menu's placement target.</summary>
    private static VersionRowVm? RowFrom(object sender)
    {
        if (sender is FrameworkElement { Tag: VersionRowVm tagged }) return tagged;
        if (sender is MenuItem mi)
        {
            var parent = mi.Parent;
            while (parent is MenuItem p) parent = p.Parent;
            if (parent is ContextMenu cm && cm.PlacementTarget is FrameworkElement target
                && target.DataContext is VersionRowVm fromMenu) return fromMenu;
        }
        return sender is FrameworkElement { DataContext: VersionRowVm row } ? row : null;
    }

    private void SetStatus(string message, bool error)
    {
        StatusLabel.Text = message;
        // SetResourceReference, not a cached brush: ThemeService swaps the brush objects on every
        // theme change and an assigned one would keep the old palette.
        StatusLabel.SetResourceReference(ForegroundProperty, error ? "DangerBrush" : "TextSecondaryBrush");
    }

    private void SetBusy(bool busy, string? message)
    {
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (message is not null) SetStatus(message, error: false);
    }

    private void SetLoaderBoxes(string? csv)
    {
        var set = (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant())
            .ToHashSet();
        LoaderFabric.IsChecked = set.Contains("fabric");
        LoaderForge.IsChecked = set.Contains("forge");
        LoaderNeoForge.IsChecked = set.Contains("neoforge");
        LoaderQuilt.IsChecked = set.Contains("quilt");
    }

    private string? LoadersCsvFromBoxes()
    {
        var loaders = new List<string>(4);
        if (LoaderFabric.IsChecked == true) loaders.Add("fabric");
        if (LoaderForge.IsChecked == true) loaders.Add("forge");
        if (LoaderNeoForge.IsChecked == true) loaders.Add("neoforge");
        if (LoaderQuilt.IsChecked == true) loaders.Add("quilt");
        return loaders.Count == 0 ? null : string.Join(',', loaders);
    }

    private static string? NormalizeCsv(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return parts.Count == 0 ? null : string.Join(',', parts);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB"
    };

    /// <summary>One uploaded version. Notifies because the changelog panel expands in place.</summary>
    public sealed class VersionRowVm : INotifyPropertyChanged
    {
        private bool _isExpanded;

        public VersionRowVm(HostedModVersionInfo source, bool isOwner)
        {
            Source = source;
            var compat = string.Join(" · ", new[] { source.McVersionsCsv, source.LoadersCsv }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            MetaLabel = $"{source.FileName} · {FormatSize(source.FileSize)} · {source.PublishedAt.LocalDateTime:yyyy-MM-dd}"
                + (compat.Length == 0 ? "" : " · " + compat);
            OwnerActionVisibility = isOwner ? Visibility.Visible : Visibility.Collapsed;
        }

        public HostedModVersionInfo Source { get; }
        public Guid Id => Source.Id;
        public string VersionString => Source.VersionString;
        public string ReleaseChannel => Source.ReleaseChannel;
        public string MetaLabel { get; }

        /// <summary>Hides owner-only menu items (delete) from someone who only has access to the mod.</summary>
        public Visibility OwnerActionVisibility { get; }

        public string Changelog => string.IsNullOrWhiteSpace(Source.Changelog)
            ? "(no changelog for this version)"
            : Source.Changelog!;

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value) return;
                _isExpanded = value;
                Raise();
                Raise(nameof(ChangelogVisibility));
                Raise(nameof(ExpandGlyph));
            }
        }

        public Visibility ChangelogVisibility => _isExpanded ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>ChevronDown / ChevronUp.</summary>
        public string ExpandGlyph => _isExpanded ? "" : "";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
