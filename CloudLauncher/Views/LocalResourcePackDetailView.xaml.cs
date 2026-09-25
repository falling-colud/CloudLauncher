using System.Collections.ObjectModel;
using System.ComponentModel;
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

    /// <summary>One instance's copy of the pack: <c>&lt;instanceGuid&gt;:&lt;fileName&gt;</c>. Null
    /// when the page was opened for the library item instead.</summary>
    private readonly string? _packKey;

    /// <summary>The library item this page is about, by <see cref="LibraryItem.Key"/>. Null on the
    /// installed-key route.</summary>
    /// <remarks>Re-resolved on every reload: <see cref="ContentPlacement.SetTicked"/> rewrites the item's
    /// policy, so a snapshot from page open goes stale after the first tick.</remarks>
    private readonly string? _libraryKey;

    private ResourcePackInfo? _pack;
    private List<PackSummary> _allPacks = new();
    private readonly ObservableCollection<PackCompatibilityRow> _packRows = new();
    private readonly ObservableCollection<HostedRpVersionDisplayRow> _versionRows = new();
    private bool _suppress;
    private ResourcePackEntry? _entry;

    /// <summary>The library's copy of this pack, if any. When null, the Instances tab says there is
    /// nothing to hand out instead of showing checkboxes.</summary>
    private LibraryItem? _item;

    /// <summary>Which instances hold a copy of <see cref="_item"/> right now, by instance id. Null means
    /// unknown (not asked, or it failed), not "none"; readers then say nothing.</summary>
    private Dictionary<Guid, LibraryTargetState>? _placement;

    /// <summary>The hosted pack this copy is linked to, as the server last described it. Null until it
    /// has been read, and when it could not be.</summary>
    /// <remarks>Linked doesn't mean owned: installing someone else's hosted pack links it too. Manage,
    /// Upload and Delete version all check the permissions in this record.</remarks>
    private HostedResourcePackDetail? _hosted;

    /// <summary>Why <see cref="_hosted"/> could not be read, in words, or null.</summary>
    private string? _hostedProblem;

    /// <summary>Set once the user types in the overview, so the server's copy no longer overwrites the
    /// boxes.</summary>
    private bool _overviewEdited;

    /// <summary>True while an upload is running, so neither upload button can start a second one.</summary>
    private bool _uploading;

    /// <summary>The page for one instance's copy of a pack.</summary>
    public LocalResourcePackDetailView(MainWindow shell, string packKey) : this(shell) =>
        _packKey = packKey;

    /// <summary>
    /// The same page for a pack the launcher holds, whether or not any instance has a copy yet.
    /// </summary>
    /// <remarks>Nothing is placed until each instance's next launch
    /// (<see cref="ContentDefaultsService.ReconcileForLaunchAsync"/>), so a newly imported pack needs a
    /// page before any instance has a copy.</remarks>
    public LocalResourcePackDetailView(MainWindow shell, LibraryItem item) : this(shell) =>
        _libraryKey = item.Key;

    private LocalResourcePackDetailView(MainWindow shell)
    {
        InitializeComponent();
        _shell = shell;
        PackCheckList.ItemsSource = _packRows;
        HostedVersionsList.ItemsSource = _versionRows;
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        try
        {
            _allPacks = await App.State.Api.ListPacksAsync();
            if (_libraryKey is { } libraryKey) await ReloadFromLibraryAsync(libraryKey);
            else if (_packKey is { } packKey) await ReloadFromInstanceAsync(packKey);
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private async Task ReloadFromInstanceAsync(string packKey)
    {
        _pack = null;
        var keyParts = packKey.Split(':', 2);
        if (keyParts.Length == 2 && Guid.TryParseExact(keyParts[0], "N", out var sourceId))
        {
            var pack = _allPacks.FirstOrDefault(p => p.Id == sourceId);
            if (pack is not null)
                _pack = App.State.ResourcePacks.ScanPack(pack).FirstOrDefault(w => w.Key == packKey);
        }

        if (_pack is null) { StatusLabel.Text = "Resource pack file no longer exists."; return; }
        // A pack may be an unpacked folder rather than a zip; Minecraft loads both.
        if (!File.Exists(_pack.FilePath) && !Directory.Exists(_pack.FilePath))
        {
            StatusLabel.Text = "This resource pack was deleted on disk.";
            return;
        }

        _entry = App.State.ResourcePacks.GetOrCreate(_pack.Key);
        _item = FindLibraryItem(_pack);
        await LoadPlacementAsync();

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
            FillInstances();
        }
        finally { _suppress = false; }
    }

    /// <summary>
    /// The page built from the launcher's own copy, with no instance in hand.
    /// </summary>
    /// <remarks>The hero reads the library file (size, <c>pack.mcmeta</c>, icon). Per-instance facts,
    /// like load-order position and the hosted-publishing link, aren't available here, and those
    /// sections say so rather than showing zero.</remarks>
    private async Task ReloadFromLibraryAsync(string libraryKey)
    {
        _pack = null;
        _entry = null;
        _item = App.State.Library.Scan(LibraryKind.ResourcePack)
            .FirstOrDefault(i => string.Equals(i.Key, libraryKey, StringComparison.OrdinalIgnoreCase));

        if (_item is null)
        {
            StatusLabel.Text = "The launcher no longer has that resource pack.";
            PackListWrap.Visibility = Visibility.Collapsed;
            InstanceStateNote.Text = "The launcher no longer has a copy of this pack, so there is "
                                   + "nothing to hand out and nothing to tick here.";
            return;
        }

        await LoadPlacementAsync();

        _suppress = true;
        try
        {
            var item = _item;
            PackNameLabel.Text = item.DisplayName;
            NameBox.Text = item.DisplayName;
            // Same wording as the content pages and the Resources tab. The installed-key route shows the
            // instance name here instead.
            SourcePackLabel.Text = "SHARED COPY";
            MetaLabel.Text = $"Modified {item.LastModified.LocalDateTime:g} · {FormatSize(item.SizeBytes)}";
            FilePathLabel.Text = item.Path;
            FileScopeNote.Text = "This is the shared copy - one file, linked into each instance that "
                               + "gets it. \"Reveal in Explorer\" opens it here. " + HoldingSentence();
            FileScopeNote.Visibility = Visibility.Visible;
            ApplyLibraryMeta(item);
            ConfigureLibraryOverview();
            FillInstances();
        }
        finally { _suppress = false; }
    }

    /// <summary>
    /// Fills in what the pack itself declares (icon, description, pack format), plus whether the
    /// instance currently has it turned on.
    /// </summary>
    /// <remarks>Runs on the UI thread: it's one pack, and <see cref="ResourcePackService.ReadMeta"/>
    /// caches by path and timestamp.</remarks>
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

        PlacementPill.Visibility = Visibility.Collapsed;
    }

    /// <summary>The same hero, read from the library file rather than an instance's.</summary>
    /// <remarks>The library copy is the same bytes every instance gets, so icon, description and format
    /// are accurate. The ON pill is replaced by one showing how many instances hold the file, since
    /// stack position is per instance (<c>options.txt</c>).</remarks>
    private void ApplyLibraryMeta(LibraryItem item)
    {
        PackInitialLabel.Text = string.IsNullOrWhiteSpace(item.DisplayName)
            ? "?"
            : item.DisplayName.Trim()[..1].ToUpperInvariant();

        var meta = ResourcePackService.ReadMeta(item.Path, item.IsFolder);
        PackIconImage.Source = meta.Icon;

        var hasDescription = !string.IsNullOrWhiteSpace(meta.Description);
        PackDescriptionCaption.Visibility = hasDescription ? Visibility.Visible : Visibility.Collapsed;
        PackDescriptionLabel.Visibility = hasDescription ? Visibility.Visible : Visibility.Collapsed;
        PackDescriptionLabel.Text = meta.Description ?? "";

        FormatPill.Visibility = meta.PackFormat is null ? Visibility.Collapsed : Visibility.Visible;
        FormatPillText.Text = meta.PackFormat is int pf ? $"pack_format {pf}" : "";

        EnabledPill.Visibility = Visibility.Collapsed;

        // No pill when the count is unknown; the Local file card's note already says the instances
        // couldn't be read.
        var holding = HoldingCount();
        PlacementPill.Visibility = holding is null ? Visibility.Collapsed : Visibility.Visible;
        PlacementPillText.Text = holding switch
        {
            null => "",
            0 => "IN NO INSTANCE YET",
            1 => "IN 1 INSTANCE",
            _ => $"IN {holding} INSTANCES"
        };
        PlacementPill.ToolTip = HoldingSentence();

        KindPill.Visibility = item.IsFolder ? Visibility.Visible : Visibility.Collapsed;
        KindPillText.Text = item.IsFolder ? "PACK FOLDER" : "";
    }

    /// <summary>
    /// The Overview and Versions tabs with no instance copy to publish from.
    /// </summary>
    /// <remarks>A hosted link is stored on the settings entry for one instance's copy
    /// (<see cref="ResourcePackService.GetOrCreate"/>). Keying it by the library item too would give
    /// the pack two publishing records, so the card explains what it needs instead of showing live
    /// controls.</remarks>
    private void ConfigureLibraryOverview()
    {
        SharingUnavailableNote.Text =
            "Publishing is recorded against an instance's own copy of this zip, and no instance has "
            + "one yet. " + HoldingSentence() + " Open the pack from an instance that holds it - its "
            + "Resources tab, or this page's row once a copy is there - to publish it.";
        SharingUnavailableHeading.Visibility = Visibility.Visible;
        SharingUnavailableNote.Visibility = Visibility.Visible;
        SharePackBox.Visibility = Visibility.Collapsed;
        SharingDisabledHint.Visibility = Visibility.Collapsed;
        SharingSettingsPanel.Visibility = Visibility.Collapsed;

        VersionsDisabledText.Text =
            "Hosted versions belong to an instance's copy of this zip, and no instance has one yet. "
            + HoldingSentence();
        VersionsDisabledPanel.Visibility = Visibility.Visible;
        VersionsPanel.Visibility = Visibility.Collapsed;
        _versionRows.Clear();
        VersionsEmptyLabel.Visibility = Visibility.Collapsed;
        OverviewSaveStatus.Text = "";
    }

    private void ConfigureOverviewEditor()
    {
        if (_pack is null || _entry is null) return;
        var wasSuppressed = _suppress;
        _suppress = true;
        try
        {
            SummaryBox.Text = _entry.Summary ?? "";
            DescriptionBox.Text = _entry.Description ?? "";
            VisibilityBox.SelectedIndex = (int)_entry.Visibility;
        }
        finally { _suppress = wasSuppressed; }
        // A linked copy's boxes are refilled from the hosted pack once it is read; until then they show
        // what this PC last had.
        _overviewEdited = false;
        HostedStatusLabel.Text = _entry.HostedResourcePackId is Guid
            ? "Linked to a hosted resource pack - reading it from the server..."
            : "Not published yet - save overview, then upload a zip to host it.";
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

    // ── the hosted pack's permissions ────────────────────────────────────────

    private bool IsLinked => _entry?.HostedResourcePackId is Guid;

    /// <summary><see cref="_hosted"/>, but only while it is still the pack this copy is linked to.</summary>
    private HostedResourcePackDetail? LinkedHosted =>
        _hosted is { } h && _entry?.HostedResourcePackId == h.Id ? h : null;

    private bool IsHostedOwner => LinkedHosted is { } h && App.State.Settings.UserId is { } me && h.OwnerId == me;
    private PackPermissions HostedGrant => LinkedHosted?.EffectivePermissions ?? PackPermissions.None;
    private bool CanManageHosted => IsHostedOwner || HostedGrant.HasFlag(PackPermissions.ManageCollaborators);
    private bool CanUploadHosted => IsHostedOwner || HostedGrant.HasFlag(PackPermissions.UploadShared);
    private bool CanDownloadHosted => IsHostedOwner || HostedGrant.HasFlag(PackPermissions.Download);

    /// <summary>True for someone on the linked pack's own collaborator list, the only access that can
    /// be given back from here.</summary>
    private bool IsHostedDirectCollaborator =>
        !IsHostedOwner && LinkedHosted is { } h && App.State.Settings.UserId is { } me
        && h.Collaborators.Any(c => c.UserId == me);

    /// <summary>
    /// Turns the hosted-pack controls on or off by what the server says this person may do, and
    /// says why when one is off.
    /// </summary>
    /// <remarks>An unlinked copy keeps everything live, since its first upload creates a pack the user
    /// owns. A linked copy follows the linked pack's grant, and write controls wait while it's
    /// unknown.</remarks>
    private void ApplyHostedPermissions()
    {
        if (_entry is null) return;
        var linked = IsLinked;
        var hosted = LinkedHosted;
        var owner = hosted?.OwnerUsername ?? "its owner";
        var unknown = linked && hosted is null;

        PackPermissionsButton.IsEnabled = linked && hosted is not null && CanManageHosted;
        PackPermissionsButton.ToolTip = !linked
            ? "Publish this pack first - there is nobody to share it with until it is hosted."
            : unknown ? HostedUnknownSentence()
            : CanManageHosted ? "Choose who can see, download, upload to or manage the hosted pack"
            : $"Only {owner}, or somebody {owner} gave full access to, can change who has this pack.";

        var canUpload = !linked || (hosted is not null && CanUploadHosted);
        var uploadHint = !linked
            ? "Publish this zip as a hosted pack you own"
            : unknown ? HostedUnknownSentence()
            : CanUploadHosted ? $"Upload this zip as a new version of {hosted!.Name}"
            : $"You can download {hosted!.Name} but not upload to it. Ask {owner} for upload access.";
        UploadHostedButton.IsEnabled = VersionsUploadButton.IsEnabled = canUpload && !_uploading;
        UploadHostedButton.ToolTip = VersionsUploadButton.ToolTip = uploadHint;

        // The overview becomes the hosted pack's details on upload, and the server only accepts those
        // from the owner.
        var canEditOverview = !linked || IsHostedOwner;
        SummaryBox.IsReadOnly = DescriptionBox.IsReadOnly = !canEditOverview;
        VisibilityBox.IsEnabled = SaveOverviewButton.IsEnabled = canEditOverview;
        var overviewHint = canEditOverview
            ? null
            : unknown ? HostedUnknownSentence()
            : $"These are {owner}'s details for the hosted pack. Only {owner} can change them.";
        VisibilityBox.ToolTip = SaveOverviewButton.ToolTip = overviewHint;

        LeaveHostedButton.Visibility = IsHostedDirectCollaborator ? Visibility.Visible : Visibility.Collapsed;
        LeaveHostedButton.ToolTip = $"Give back the access {owner} gave you. This copy stays on your PC.";

        if (hosted is not null)
            HostedStatusLabel.Text = IsHostedOwner
                ? $"Published as {hosted.Name} · {hosted.Versions.Count} zip version(s). Uploading adds a new version."
                : CanUploadHosted
                    ? $"From {hosted.Name}, shared by {owner}. You can upload new versions to it."
                    : $"From {hosted.Name}, shared by {owner}. You can download it, not upload to it.";
        else if (unknown && _hostedProblem is not null)
            HostedStatusLabel.Text = HostedUnknownSentence();
    }

    private string HostedUnknownSentence() => _hostedProblem is { } why
        ? $"This copy is linked to a hosted pack you cannot open just now - {why}."
        : "Reading the hosted pack from the server...";

    /// <summary>Why the linked pack could not be read, as the end of that sentence.</summary>
    private static string DescribeHostedFailure(Exception ex) => ex switch
    {
        ApiException { Status: System.Net.HttpStatusCode.NotFound } => "it has been deleted from the server",
        ApiException { Status: System.Net.HttpStatusCode.Forbidden } => "your access to it has been taken away",
        _ => ContentBundleService.Explain(ex, ex.Message).TrimEnd('.')
    };

    /// <summary>Shows the hosted pack's own details in the overview, unless the user has started
    /// editing them here.</summary>
    /// <remarks>Starting from the server's values means any difference at upload time is a real edit,
    /// not a stale local copy that would reset the hosted pack.</remarks>
    private void ShowHostedOverview(HostedResourcePackDetail hosted)
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

    /// <summary>The overview fields the owner changed on this page, as a patch, or null when there is
    /// nothing to send.</summary>
    /// <remarks>Never includes the name; the hosted pack's name is changed on its own page.</remarks>
    private UpdateResourcePackRequest? OverviewPatch(HostedResourcePackDetail hosted)
    {
        var summary = string.IsNullOrWhiteSpace(SummaryBox.Text) ? null : SummaryBox.Text.Trim();
        var description = string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text.Trim();
        var visibility = SelectedVisibility();

        var newSummary = summary is not null && summary != (hosted.Summary ?? "").Trim() ? summary : null;
        var newDescription = description is not null && description != (hosted.Description ?? "").Trim() ? description : null;
        PackVisibility? newVisibility = visibility != hosted.Visibility ? visibility : null;
        return newSummary is null && newDescription is null && newVisibility is null
            ? null
            : new UpdateResourcePackRequest(null, newSummary, newDescription, newVisibility);
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
        ApplyHostedPermissions();
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
        StatusLabel.Text = enabled ? "Sharing enabled - upload to publish." : "Sharing controls hidden.";
    }

    private void OnSaveName(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) { StatusLabel.Text = "Name cannot be empty."; return; }

        // The name belongs to whichever copy the page was opened for. ResourcePacks entries are keyed
        // by an instance's copy, so the library route renames the library item instead.
        if (_pack is not null) App.State.ResourcePacks.Rename(_pack.Key, name);
        else if (_item is not null) App.State.Library.Rename(_item.Key, name);
        else return;

        PackNameLabel.Text = name;
        StatusLabel.Text = "Name saved.";
    }

    private async void OnSaveOverview(object sender, RoutedEventArgs e)
    {
        if (_pack is null) return;
        try
        {
            var visibility = SelectedVisibility();
            App.State.ResourcePacks.UpdateOverview(_pack.Key, SummaryBox.Text, DescriptionBox.Text, visibility);
            App.State.ResourcePacks.SetSharingEnabled(_pack.Key, SharePackBox.IsChecked == true);
            _entry = App.State.ResourcePacks.GetOrCreate(_pack.Key);

            // A linked pack's overview is the hosted pack's details, so the owner's Save goes straight to
            // the server, with only the changed fields.
            if (LinkedHosted is { } hosted && IsHostedOwner && OverviewPatch(hosted) is { } patch)
            {
                await App.State.Api.UpdateResourcePackAsync(hosted.Id, patch);
                _overviewEdited = false;
                OverviewSaveStatus.Text = "Overview saved to the hosted pack.";
                await LoadHostedVersionsAsync();
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

    /// <summary>
    /// Publishes the zip as a new hosted version.
    /// </summary>
    /// <remarks>Zips only: an unpacked folder has no single file to post, and zipping it automatically
    /// would upload something the user never checked.</remarks>
    private async void OnShareOrUploadZip(object sender, RoutedEventArgs e)
    {
        if (_pack is null) return;
        if (_pack.IsFolder)
        {
            StatusLabel.Text = "This pack is an unpacked folder - zip it first, then upload the zip.";
            return;
        }
        if (!File.Exists(_pack.FilePath)) { StatusLabel.Text = "The zip is no longer on disk."; return; }
        // The button is disabled here with a reason; this covers keyboard users too.
        if (IsLinked && LinkedHosted is not null && !CanUploadHosted)
        {
            StatusLabel.Text = UploadHostedButton.ToolTip as string ?? "You cannot upload to this pack.";
            return;
        }

        SaveOverviewToSettings();

        var versionDlg = new UploadResourcePackVersionDialog(
            _pack.FilePath,
            TimeFormat.VersionStamp(DateTimeOffset.Now),
            SourceMinecraftVersion())
        { Owner = _shell };
        if (versionDlg.ShowDialog() != true || versionDlg.Result is null) return;

        _uploading = true;
        ApplyHostedPermissions();
        StatusLabel.Text = "Uploading hosted version...";
        try
        {
            var hostedId = await EnsureHostedResourcePackAsync();
            await App.State.Api.UploadResourcePackVersionAsync(hostedId, _pack.FilePath, versionDlg.Result);
            StatusLabel.Text = "Uploaded.";
            await LoadHostedVersionsAsync();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Upload failed: " + ContentBundleService.Explain(ex, ex.Message);
        }
        finally
        {
            _uploading = false;
            ApplyHostedPermissions();
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

    /// <summary>Hides what this person may not do with the hosted pack before the menu shows.</summary>
    /// <remarks>On the server, deleting a version is owner-only and saving one needs Download.</remarks>
    private void OnVersionMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        foreach (var item in menu.Items.OfType<FrameworkElement>())
        {
            if (item.Name is "VersionCtxDelete" or "VersionCtxDeleteSeparator")
                item.Visibility = IsHostedOwner ? Visibility.Visible : Visibility.Collapsed;
            else if (item.Name is "VersionCtxSave")
            {
                item.IsEnabled = CanDownloadHosted;
                item.ToolTip = CanDownloadHosted ? null : "You can see this pack but not download it.";
                ToolTipService.SetShowOnDisabled(item, true);
            }
        }
    }

    private async void OnCtxSaveVersion(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_entry?.HostedResourcePackId is not Guid hid) return;
            if (VersionFrom(sender) is not HostedRpVersionDisplayRow row) return;
            if (!CanDownloadHosted) { StatusLabel.Text = "You can see this pack but not download it."; return; }

            // The suggested name comes from the server, so only a plain file name is offered; a path
            // in the name box would save somewhere other than the folder the dialog shows.
            var suggested = string.IsNullOrWhiteSpace(row.FileName) ? row.VersionString + ".zip" : row.FileName;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = PathSafety.IsSafeFileName(suggested) ? suggested : "resourcepack.zip",
                Filter = "Zip (*.zip)|*.zip|All files|*"
            };
            if (dlg.ShowDialog(_shell) != true) return;

            StatusLabel.Text = "Downloading...";
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

    /// <summary>Deletes one uploaded version from the hosted pack.</summary>
    private async void OnCtxDeleteVersion(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_entry?.HostedResourcePackId is not Guid hid) return;
            if (VersionFrom(sender) is not HostedRpVersionDisplayRow row) return;
            if (!IsHostedOwner) { StatusLabel.Text = "Only the pack's owner can delete a version."; return; }

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
            _hosted = detail;
            if (!CanManageHosted)
            {
                // The grant can have changed since the page drew the button.
                ApplyHostedPermissions();
                StatusLabel.Text = PackPermissionsButton.ToolTip as string ?? "You cannot change who has this pack.";
                return;
            }
            new PermissionsDialog(detail) { Owner = _shell }.ShowDialog();
            await LoadHostedVersionsAsync();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Could not load permissions: " + ContentBundleService.Explain(ex, ex.Message);
        }
    }

    /// <summary>Gives back the access the linked pack's owner granted, after asking.</summary>
    /// <remarks>This copy stays on the PC. If a team or public visibility still grants access, the link
    /// stays too; otherwise every copy on this PC linked to the pack is unlinked, as when a hosted pack
    /// is deleted.</remarks>
    private async void OnLeaveHosted(object sender, RoutedEventArgs e)
    {
        if (LinkedHosted is not { } hosted || App.State.Settings.UserId is not { } me || !IsHostedDirectCollaborator)
            return;
        try
        {
            if (!await AppDialog.ConfirmAsync(_shell, "Leave resource pack",
                    $"Leave '{hosted.Name}'? You lose the access {hosted.OwnerUsername} gave you, and only they "
                    + "can give it back. This copy stays on your PC. If one of your teams also has the pack, "
                    + "you keep what the team gives.",
                    "Leave", "Cancel", danger: true))
                return;

            LeaveHostedButton.IsEnabled = false;
            await App.State.Api.RemoveResourcePackCollaboratorAsync(hosted.Id, me);
        }
        catch (Exception ex)
        {
            LeaveHostedButton.IsEnabled = true;
            StatusLabel.Text = "Could not leave: " + ContentBundleService.Explain(ex, ex.Message);
            return;
        }

        LeaveHostedButton.IsEnabled = true;
        try
        {
            _hosted = await App.State.Api.GetResourcePackAsync(hosted.Id);
            StatusLabel.Text = $"You left {hosted.Name}. What is left comes from a team or from it being public.";
            await LoadHostedVersionsAsync();
        }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(LocalResourcePackDetailView), ex);
            _hosted = null;
            var changed = false;
            foreach (var entry in App.State.Settings.ResourcePacks.Values.Where(v => v.HostedResourcePackId == hosted.Id))
            {
                entry.HostedResourcePackId = null;
                changed = true;
            }
            if (changed) App.State.Settings.Save();
            StatusLabel.Text = $"You left {hosted.Name}. This copy stays on your PC, no longer linked to it.";
            await ReloadAsync();
        }
    }

    private void SaveOverviewToSettings()
    {
        if (_pack is null) return;
        App.State.ResourcePacks.UpdateOverview(_pack.Key, SummaryBox.Text, DescriptionBox.Text, SelectedVisibility());
        App.State.ResourcePacks.SetSharingEnabled(_pack.Key, true);
        _entry = App.State.ResourcePacks.GetOrCreate(_pack.Key);
    }

    /// <summary>The hosted pack this upload goes to: the linked one, or a new one this user owns.</summary>
    /// <remarks>A linked copy only ever uploads to its linked pack, and only if the server allows it;
    /// the grant is re-read here since the owner may have changed it. The overview is sent only by the
    /// owner and only for changed fields.</remarks>
    private async Task<Guid> EnsureHostedResourcePackAsync()
    {
        if (_pack is null) throw new InvalidOperationException("Resource pack missing.");
        _entry ??= App.State.ResourcePacks.GetOrCreate(_pack.Key);

        if (_entry.HostedResourcePackId is Guid existing)
        {
            var hosted = await App.State.Api.GetResourcePackAsync(existing);
            _hosted = hosted;
            if (!CanUploadHosted)
                throw new InvalidOperationException(
                    $"You can download {hosted.Name} but not upload to it. Ask {hosted.OwnerUsername} for upload access.");

            if (IsHostedOwner && OverviewPatch(hosted) is { } patch)
                await App.State.Api.UpdateResourcePackAsync(existing, patch);
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
            _hosted = detail;
            _hostedProblem = null;
            ShowHostedOverview(detail);
            _versionRows.Clear();
            foreach (var v in detail.Versions.OrderByDescending(v => v.PublishedAt))
                _versionRows.Add(HostedRpVersionDisplayRow.From(v));

            VersionsEmptyLabel.Text = "No hosted versions yet.";
            VersionsEmptyLabel.Visibility = _versionRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            _hosted = null;
            _hostedProblem = DescribeHostedFailure(ex);
            StatusLabel.Text = "Could not refresh versions: " + ContentBundleService.Explain(ex, ex.Message);
        }
        finally
        {
            RefreshVersionsButton.IsEnabled = _entry?.HostedResourcePackId is Guid;
            ApplyHostedPermissions();
        }
    }

    private string? SourceMinecraftVersion() =>
        _pack is null
            ? null
            : _allPacks.FirstOrDefault(p => p.Id == _pack.SourcePackId)?.MinecraftVersion;

    // ── the Instances tab ────────────────────────────────────────────────────

    /// <summary>
    /// Which instances hold a copy of the library item, asked once per reload.
    /// </summary>
    /// <remarks>Uses <see cref="ContentLibraryService.InspectAsync"/> on a worker thread. "Goes into
    /// this instance" and "this instance has it" differ until each instance launches again, so both are
    /// shown. Left null on failure rather than guessing.</remarks>
    private async Task LoadPlacementAsync()
    {
        _placement = null;
        if (_item is null) return;
        try
        {
            var states = await App.State.Library.InspectAsync(_item, _allPacks);
            _placement = states.ToDictionary(s => s.PackId);
        }
        catch (Exception ex) { AppLog.LogError(nameof(LocalResourcePackDetailView), ex); }
    }

    /// <inheritdoc cref="_placement"/>
    private int? HoldingCount() => _placement?.Values.Count(s => s.Applied);

    /// <summary>One sentence about how much of the fleet holds the file, and when that changes.</summary>
    private string HoldingSentence() => HoldingCount() switch
    {
        null => "The launcher could not read your instances just now, so it cannot say which of them "
                + "hold a copy.",
        0 => "No instance has taken a copy yet - every instance ticked below takes one the next time "
             + "it starts.",
        1 => "One instance holds a copy; the rest that are ticked below take one the next time they "
             + "start.",
        var n when n == _allPacks.Count => $"All {n} of your instances hold a copy.",
        var n => $"{n} of your {_allPacks.Count} instances hold a copy; the rest that are ticked "
                 + "below take one the next time they start."
    };

    /// <summary>The difference between "will go into this instance" and "is in this instance".</summary>
    /// <remarks>Nothing is placed until an instance's next launch, so this says which lines are still
    /// pending. Empty when the pack isn't going into the instance and isn't there.</remarks>
    private string PlacementClause(PackSummary instance, bool goesIn)
    {
        if (_placement is null || !_placement.TryGetValue(instance.Id, out var state)) return "";
        if (state.HasOwnCopy)
            return $" {instance.Name} has its own file of that name, which wins at launch, so the "
                 + "shared copy sits there unused.";
        if (state.Applied)
            return goesIn
                ? $" {instance.Name} has a copy already."
                : $" {instance.Name} has a copy already, and it stays there.";
        return goesIn ? $" {instance.Name} takes its copy the next time it starts." : "";
    }

    /// <summary>The library's copy of this pack, or null when the launcher has none.</summary>
    /// <remarks>Matched by file name and identity, like the Resource packs list: an instance's own
    /// same-named pack is a different file.</remarks>
    private static LibraryItem? FindLibraryItem(ResourcePackInfo pack)
    {
        try
        {
            return App.State.Library.Scan(LibraryKind.ResourcePack).FirstOrDefault(i =>
                string.Equals(i.FileName, pack.FileName, StringComparison.OrdinalIgnoreCase)
                && PackFolderService.EntriesReferToSameContent(i.Path, pack.FilePath));
        }
        catch (Exception ex) { AppLog.LogError(nameof(LocalResourcePackDetailView), ex); return null; }
    }

    /// <summary>
    /// Fills the per-instance list: every instance ticked, minus the ones this pack is narrowed away
    /// from.
    /// </summary>
    /// <remarks>Ticks write the item's own narrowing (<see cref="ContentPlacement.SetTicked"/>), which
    /// the engine, the row count and this page all read. Whether anything is placed at all is the
    /// switch on the Resource packs list row.</remarks>
    private void FillInstances()
    {
        _packRows.Clear();

        // Only on the installed-key route: a pack in an instance that the library doesn't hold.
        if (_item is null)
        {
            PackListWrap.Visibility = Visibility.Collapsed;
            InstanceStateNote.Text =
                "This pack is only inside " + (_pack?.SourcePackName ?? "one instance") + ", so there is "
                + "nothing to hand out yet. Switch it on on the Resource packs page and the launcher "
                + "takes its own copy - then this list decides which instances get it.";
            return;
        }

        PackListWrap.Visibility = Visibility.Visible;
        InstanceStateNote.Text = _item.AutoApply
            ? "Every instance is ticked to begin with. Untick one and the launcher leaves it alone - "
              + "anything already there stays where it is. A ticked instance that has no copy yet "
              + "takes one the next time it starts, so \"will get it\" and \"has it\" are two "
              + "different answers and each line below says which it is."
            : "Every instance is ticked to begin with. This pack is switched OFF on the Resource packs "
              + "page, so nothing is placed anywhere yet - these ticks are what will happen when you "
              + "switch it on.";

        foreach (var pack in _allPacks.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            InstanceContentOverrides? overrides = null;
            try { overrides = App.State.ContentDefaults.OverridesFor(pack.Id); }
            catch (Exception ex) { AppLog.LogError(nameof(LocalResourcePackDetailView), ex); }

            _packRows.Add(new PackCompatibilityRow
            {
                PackId = pack.Id,
                PackName = pack.Name,
                VersionLabel = pack.IsEmpty ? "EMPTY"
                    : pack.Loader == LoaderKind.None ? $"MC {pack.MinecraftVersion}"
                    : $"MC {pack.MinecraftVersion} · {pack.Loader}",
                StateLine = Describe(pack, overrides),
                IsCompatible = ContentPlacement.IsTicked(_item, pack, overrides),
                IsEditable = true
            });
        }
    }

    /// <summary>One sentence about this instance, from the same call the list's rows use, plus
    /// whether that instance has the file yet.</summary>
    private string Describe(PackSummary pack, InstanceContentOverrides? overrides)
    {
        if (_item is null) return "";
        if (overrides?.OptOutOfAllDefaults == true)
            return $"{pack.Name} takes nothing the launcher hands out, whatever is ticked here.";
        var goesIn = ContentPlacement.GoesInto(_item, pack, overrides, ShaderLoader.Iris, out var why);
        return why + PlacementClause(pack, goesIn);
    }

    /// <summary>
    /// A box was ticked or unticked: the whole list is written back.
    /// </summary>
    /// <remarks>Whole rather than one box, for the reason <see cref="ContentPlacement.SetTicked"/>
    /// gives. Re-read afterwards so rows whose answer changed update.</remarks>
    private void OnPackCompatibilityChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress || _item is null) return;
        try
        {
            var ticked = _packRows.Where(r => r.IsCompatible).Select(r => r.PackId).ToHashSet();
            ContentPlacement.SetTicked(_item, _allPacks, ticked);
            StatusLabel.Text = ticked.Count == _allPacks.Count
                ? "Every instance gets this pack."
                : $"{ticked.Count} of {_allPacks.Count} instance(s) get this pack. The rest are left "
                  + "alone - anything already in them stays.";
            _ = ReloadAsync();
        }
        catch (Exception ex) { StatusLabel.Text = "That could not be saved: " + ex.Message; }
    }

    /// <summary>Reveals whichever copy this page is about: the instance's, or the library's.</summary>
    /// <remarks>The Local file card says which one it will open.</remarks>
    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var path = _pack?.FilePath ?? _item?.Path;
        if (path is not { Length: > 0 }) { StatusLabel.Text = "There is no file to reveal."; return; }
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            StatusLabel.Text = "That file is no longer on disk.";
            return;
        }
        if (!SafeLaunch.RevealFile(path)) StatusLabel.Text = "Explorer could not be opened at that file.";
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

    /// <summary>The changelog and channel, which don't fit on the row.</summary>
    public string Tooltip { get; init; } = "";

    public static HostedRpVersionDisplayRow From(HostedResourcePackVersionInfo v) => new()
    {
        Id = v.Id,
        VersionString = v.VersionString,
        FileName = v.FileName,
        // PublishedAt is UTC (+00:00); TimeFormat shows it in local time, matching the tooltip.
        MetaLabel = $"{v.FileName} · {FormatSize(v.FileSize)} · {TimeFormat.DateTime(v.PublishedAt)}",
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
