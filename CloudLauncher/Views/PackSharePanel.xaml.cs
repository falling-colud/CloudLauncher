using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>The Share &amp; sync tab of the file-management page: who this instance is shared with,
/// what is uploaded when it syncs, and the upload and download buttons.</summary>
/// <remarks><para>The access list offers four roles over the server's <see cref="PackPermissions"/>
/// bits; the per-flag grid is still available behind "Advanced..."
/// (<see cref="PermissionsDialog"/>) for unusual combinations.</para>
/// <para>Every change here is a server call, so there is no offline write queue. Offline, the tab
/// renders read-only from <see cref="PackDetailCache"/> (which <see cref="ApiClient.GetPackAsync"/>
/// falls back to), says so in the status bar, and disables network controls with tooltips that
/// still show.</para>
/// <para>Invitations, share links, ownership transfer, user search and the activity feed have no
/// <see cref="ApiClient"/> methods yet; <see cref="ShareApi"/> at the bottom of this file calls
/// them directly.</para></remarks>
public partial class PackSharePanel : UserControl
{
    /// <summary>How many files of the outgoing set are listed before the rest become a count.</summary>
    private const int FileRowCap = 25;

    /// <summary>How many conflicting paths are listed before the rest become a count.</summary>
    private const int ConflictRowCap = 20;

    private const int ActivityRows = 12;

    private readonly PageState _state;

    private MainWindow? _shell;
    private PackDetail? _pack;
    private Guid _packId;

    private bool _isOwner;
    private bool _canManage;
    private bool _canUpload;
    private bool _canDownload;

    private List<TeamSummary> _teams = new();
    private List<ShareAccessRow> _accessRows = new();

    /// <summary>The user picked from the search results, if any. A typed name with nothing picked
    /// is still sent as is; the server's "User not found" is a better error than refusing to
    /// try.</summary>
    private UserSummary? _invitee;

    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _transferCts;
    private bool _busy;

    /// <summary>The refused upload, kept so the conflict stays on screen until it is dealt
    /// with.</summary>
    private ManifestConflict? _conflict;

    public PackSharePanel()
    {
        InitializeComponent();

        _state = new PageState(ContentScroller, PageStateHost, nameof(PackSharePanel))
            .Copy(ShareCopy)
            .Slots(SubLabel, StatusLabel, BusyBar, BusyCancelButton)
            .DisableWhileBusy(RefreshButton);
        _state.RetryRequested += () => _ = LoadAsync();
        _state.CancelRequested += () =>
        {
            try { _transferCts?.Cancel(); } catch (ObjectDisposedException) { /* already finished */ }
            try { _loadCts?.Cancel(); } catch (ObjectDisposedException) { /* already finished */ }
        };

        // The user search hits the server on every settled keystroke, so it gets the network
        // debounce rather than the 200 ms one a local filter uses.
        UserSearch.DebounceMilliseconds = 450;
        UserSearch.TextChangedDebounced += (_, q) => _ = SearchUsersAsync(q);
        UserSearch.SearchSubmitted += (_, _) => _ = SearchUsersAsync(UserSearch.Text.Trim());

        InviteRoleBox.ItemsSource = ShareRole.All;
        InviteRoleBox.SelectedIndex = 1;                 // view and download
        TeamRoleBox.ItemsSource = ShareRole.All;
        TeamRoleBox.SelectedIndex = 1;
        InviteExpiryBox.ItemsSource = ExpiryChoice.All;
        InviteExpiryBox.SelectedIndex = 0;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>The house strings for this tab.</summary>
    /// <remarks>Empty can't happen here (the owner row is always in the list), but the copy is
    /// written anyway in case it ever does.</remarks>
    private static readonly PageCopy ShareCopy = new()
    {
        Glyph = "",
        Verb = "Reading",
        Noun = "person or team with access",
        LoadingLine = "Asking the server who this instance is shared with.",
        EmptyTitle = "Not shared with anyone",
        EmptyBody = "Only you can see this instance. Invite someone below to change that.",
        FilteredTitle = "Nothing matched",
        FilteredBody = "No person or team matched that.",
        ErrorTitle = "Could not read the sharing state",
        OfflineTitle = "Showing what the launcher last knew",
        OfflineBody = "The server is not answering ({0}). Who has access is shown from the last "
                    + "answer it gave; changing any of it needs the server."
    };

    /// <summary>Points the panel at an instance. Safe to call before the control is on screen.</summary>
    /// <remarks>The file-management page builds tabs lazily and passes the pack in afterwards, so
    /// the first load waits for whichever of <see cref="Load"/> and <c>Loaded</c> happens
    /// second.</remarks>
    public void Load(MainWindow shell, PackDetail pack)
    {
        _shell = shell;
        _pack = pack;
        _packId = pack.Id;
        ApplyPack(pack);                 // something true and immediate, before the round trip
        // Queued rather than started here: the caller may not be inside a dispatcher operation, and
        // an async method started outside one continues on a pool thread, where touching a control
        // throws. It also lets the tab draw before the first round trip.
        if (IsLoaded) Dispatcher.BeginInvoke(new Action(() => _ = LoadAsync()));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.State.ConnectivityChanged -= OnConnectivityChanged;
        App.State.ConnectivityChanged += OnConnectivityChanged;
        // Start with the search field open: typing a name is the point of this card, and a
        // collapsed magnifier hides that.
        UserSearch.Expand();
        if (_packId != Guid.Empty && _state.Kind == PageStateKind.Idle) _ = LoadAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.State.ConnectivityChanged -= OnConnectivityChanged;
        try { _loadCts?.Cancel(); } catch (ObjectDisposedException) { /* already finished */ }
        try { _searchCts?.Cancel(); } catch (ObjectDisposedException) { /* already finished */ }
    }

    private void OnConnectivityChanged()
    {
        // Already on the UI thread: AppState marshals this event.
        ApplyGates();
        if (!App.State.IsOffline && _pack is not null) _ = LoadAsync();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F5) { _ = LoadAsync(); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }

    // ══ loading ══════════════════════════════════════════════════════════════

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = LoadAsync();

    private async Task LoadAsync()
    {
        if (_packId == Guid.Empty || _busy) return;
        _busy = true;
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        _state.Begin();
        try
        {
            // GetPackAsync answers from PackDetailCache when the server is unreachable, so this is
            // also the offline path; the difference is reported afterwards.
            var pack = await App.State.Api.GetPackAsync(_packId, cts.Token);
            if (cts.IsCancellationRequested) return;
            _pack = pack;
            ApplyPack(pack);

            await LoadTeamsAsync(cts.Token);
            _state.Content(_accessRows.Count, countText: AccessSummary());
            AccessCountLabel.Text = AccessSummary();

            if (App.State.IsOffline)
                _state.Offline(App.State.OfflineReason ?? "it is not answering",
                    PackListCache.Describe(PackDetailCache.CachedAt));

            // Everything below is extra detail: each part reports what it couldn't find out instead
            // of failing the whole tab.
            await ScanOutgoingFilesAsync(cts.Token);
            await LoadSyncStateAsync(cts.Token);
            await LoadActivityAsync(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            _state.Cancelled();
        }
        catch (OfflineException ex)
        {
            AppLog.Log("share", "Sharing state unavailable: " + ex.Message);
            _state.Offline(ex.Reason ?? "it is not answering", PackListCache.Describe(PackDetailCache.CachedAt));
        }
        catch (Exception ex)
        {
            _state.Error("the server would not say who this instance is shared with", ex);
        }
        finally
        {
            _busy = false;
            _loadCts = null;
            cts.Dispose();
            ApplyGates();
        }
    }

    private async Task LoadTeamsAsync(CancellationToken ct)
    {
        try
        {
            _teams = await App.State.Api.ListTeamsAsync(ct);
        }
        catch (Exception ex)
        {
            AppLog.LogError("share.teams", ex);
            _teams = new List<TeamSummary>();
        }

        var granted = new HashSet<Guid>(_pack?.Teams.Select(t => t.TeamId) ?? []);
        AvailableTeamsBox.ItemsSource = _teams.Where(t => !granted.Contains(t.Id)).ToList();
        if (AvailableTeamsBox.Items.Count > 0) AvailableTeamsBox.SelectedIndex = 0;

        TeamsHintLabel.Text = _teams.Count switch
        {
            0 => "You are not on a team yet. Teams live on Sharing's \"People & teams\" tab; a team "
               + "is the way to share several instances with the same group of people at once.",
            _ when AvailableTeamsBox.Items.Count == 0 =>
                $"Every one of your {TeamsView.Plural(_teams.Count, "team", "teams")} already has access.",
            _ => $"You are on {TeamsView.Plural(_teams.Count, "team", "teams")}. Everyone on the team "
               + "you pick gets the role beside it, including people who join it later."
        };
    }

    // ══ rendering ════════════════════════════════════════════════════════════

    private void ApplyPack(PackDetail pack)
    {
        var me = App.State.Settings.UserId;
        _isOwner = me is not null && pack.OwnerId == me;
        _canManage = _isOwner || pack.EffectivePermissions.HasFlag(PackPermissions.ManageCollaborators);
        _canUpload = _isOwner || pack.EffectivePermissions.HasFlag(PackPermissions.UploadShared);
        _canDownload = _isOwner || pack.EffectivePermissions.HasFlag(PackPermissions.Download);

        PublishBox.IsChecked = pack.IsShared;
        PublishPill.Text = pack.IsShared ? "Published" : "Not published";
        PublishEffectLabel.Text = pack.IsShared
            ? "Turning this off stops the syncing. The people below keep the copy already on their "
            + "PC - nothing is deleted from anyone's machine - but they stop receiving your changes "
            + "and cannot download it again."
            : pack.Visibility == PackVisibility.Public
                ? "Until this is on, this instance has no files on CloudLauncher. It is listed for "
                + "everyone, but anyone who adds it gets an empty instance."
                : "Until this is on, this instance has no files on CloudLauncher. People you invite can "
                + "see that it exists and nothing else.";

        // Published and Private is a valid choice (sharing by invitation), but it is also what the
        // Publish box leaves behind for someone who expected it to make the instance public. The
        // note explains which; only its button changes the visibility.
        UnlistedNote.Visibility = pack.IsShared && pack.Visibility == PackVisibility.Private && _canManage
            ? Visibility.Visible : Visibility.Collapsed;

        VisPrivateBox.IsChecked = pack.Visibility == PackVisibility.Private;
        VisTeamBox.IsChecked = pack.Visibility == PackVisibility.Team;
        VisPublicBox.IsChecked = pack.Visibility == PackVisibility.Public;
        TeamVisibilityWarning.Visibility = pack.Visibility == PackVisibility.Team && pack.Teams.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;

        BuildAccessRows(pack);
        BuildInviteRows(pack);
        BuildShareLink(pack);
        BuildTransferTargets(pack);

        LastUploadLabel.Text = pack.LastUploadedAt is { } when
            ? $"Last upload: {pack.LastUploadedByUsername ?? "somebody"}, {Ago(when)}."
            : pack.IsShared
                ? "Nothing has been uploaded yet - the shared copy is empty until you press Upload."
                : "Nothing has been uploaded: this instance is not published.";

        SyncExplainLabel.Text = _isOwner
            ? "The file rules decide this. Everything under .cloudlauncher/ always goes; everything "
            + "else goes when a rule says shared and no private-file rule holds it back."
            : "The file rules are the owner's and are the same for everyone who has this instance. "
            + "You can read them here; only the owner can change them.";

        UpdatePrimaryButton();
    }

    private void BuildAccessRows(PackDetail pack)
    {
        var offline = App.State.IsOffline;
        var rows = new List<ShareAccessRow>();
        var me = App.State.Settings.UserId;

        rows.Add(ShareAccessRow.ForOwner(
            pack.OwnerUsername + (me is not null && pack.OwnerId == me ? " (you)" : ""),
            "Owns this instance. Can do everything, including deleting it."));

        foreach (var c in pack.Collaborators.OrderBy(c => c.Username, StringComparer.OrdinalIgnoreCase))
            rows.Add(ShareAccessRow.ForPerson(c, _canManage, offline, me));

        foreach (var t in pack.Teams.OrderBy(t => t.TeamName, StringComparer.OrdinalIgnoreCase))
            rows.Add(ShareAccessRow.ForTeam(t, _canManage, offline));

        if (pack.Visibility == PackVisibility.Public)
            rows.Add(ShareAccessRow.ForPublic());

        foreach (var row in rows) { row.RoleEdited += OnRoleEdited; row.Activate(); }

        _accessRows = rows;
        AccessList.ItemsSource = rows;
        AccessEmptyLabel.Visibility = rows.Count <= 1 ? Visibility.Visible : Visibility.Collapsed;
        AccessCountLabel.Text = AccessSummary();
    }

    private string AccessSummary()
    {
        if (_pack is null) return "";
        var people = _pack.Collaborators.Count;
        var teams = _pack.Teams.Count;
        var bits = new List<string>();
        if (people > 0) bits.Add(TeamsView.Plural(people, "person", "people"));
        if (teams > 0) bits.Add(TeamsView.Plural(teams, "team", "teams"));
        if (_pack.Visibility == PackVisibility.Public) bits.Add("everyone on this server");
        return bits.Count == 0 ? "nobody else yet" : "shared with " + string.Join(" and ", bits);
    }

    private void BuildInviteRows(PackDetail pack)
    {
        var offline = App.State.IsOffline;
        var pending = (pack.PendingInvitations ?? [])
            .Where(i => !i.IsLink && i.AcceptedAt is null && i.RevokedAt is null)
            .Where(i => i.ExpiresAt is null || i.ExpiresAt > DateTimeOffset.UtcNow)
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => ShareInviteRow.From(i, _canManage, offline))
            .ToList();

        InviteList.ItemsSource = pending;
        InviteEmptyLabel.Visibility = pending.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        InviteEmptyLabel.Text = _canManage
            ? "No invitation is waiting for an answer."
            : "Only someone who can manage sharing sees the pending invitations.";
    }

    private void BuildShareLink(PackDetail pack)
    {
        var link = (pack.PendingInvitations ?? [])
            .FirstOrDefault(i => i.IsLink && i.RevokedAt is null && pack.ShareToken is { Length: > 0 }
                                 && i.Token == pack.ShareToken);

        if (pack.ShareToken is { Length: > 0 })
        {
            var role = link is null ? null : ShareRole.Describe(link.Permissions);
            var expiry = link?.ExpiresAt is { } at ? $" It stops working {Ago(at)}." : "";
            ShareLinkLabel.Text = role is null
                ? "A share link is live. Anyone who has it can redeem it in their launcher." + expiry
                : $"A share link is live: whoever redeems it can {role}." + expiry;
        }
        else
        {
            ShareLinkLabel.Text = _canManage
                ? "No link yet. Copying one mints it with the role picked above, and it works for "
                + "anybody who has it until you revoke it."
                : "Only someone who can manage sharing can mint a link.";
        }
        RevokeLinkButton.Visibility = pack.ShareToken is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildTransferTargets(PackDetail pack)
    {
        var targets = pack.Collaborators.OrderBy(c => c.Username, StringComparer.OrdinalIgnoreCase).ToList();
        TransferTargetBox.ItemsSource = targets;
        if (targets.Count > 0) TransferTargetBox.SelectedIndex = 0;
    }

    private void UpdatePrimaryButton()
    {
        if (_pack is null) return;
        var running = _transferCts is not null;
        if (running)
        {
            PrimaryGlyph.Text = "";
            PrimaryLabel.Text = "Stop";
            return;
        }
        if (!_pack.IsShared)
        {
            PrimaryGlyph.Text = "";
            PrimaryLabel.Text = "Publish this instance";
            return;
        }
        PrimaryGlyph.Text = "";
        PrimaryLabel.Text = "Upload changes";
    }

    // ══ what will sync ═══════════════════════════════════════════════════════

    /// <summary>The file set the next upload would carry, plus the files held back.</summary>
    /// <remarks>The same walk <c>PackFolderService.UploadSharedAsync</c> does, run ahead of time so
    /// files withheld under <see cref="PrivateAssetPolicy"/> are shown next to the file list rather
    /// than only in a log.</remarks>
    private async Task ScanOutgoingFilesAsync(CancellationToken ct)
    {
        SyncTotalLabel.Text = "Measuring...";
        try
        {
            var gameDir = App.State.Packs.GameDir(_packId);
            var rules = App.State.Rules.Load(App.State.Packs.PackRoot(_packId));
            var settings = App.State.Settings;

            var scan = await Task.Run(() =>
            {
                var shared = new List<(string Path, long Size)>();
                var held = new List<(string Path, long Size)>();
                if (!Directory.Exists(gameDir)) return (shared, held);

                foreach (var abs in Directory.EnumerateFiles(gameDir, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    var rel = Path.GetRelativePath(gameDir, abs).Replace('\\', '/');
                    long size;
                    try { size = new FileInfo(abs).Length; } catch { size = 0; }

                    if (rel.StartsWith(".cloudlauncher/", StringComparison.OrdinalIgnoreCase))
                    { shared.Add((rel, size)); continue; }

                    if (!App.State.Rules.Match(rel, rules).IsAutoShared) continue;
                    if (PrivateAssetPolicy.IsPrivate(rel, settings)) held.Add((rel, size));
                    else shared.Add((rel, size));
                }
                return (shared, held);
            }, ct);

            if (ct.IsCancellationRequested) return;

            var (sharedFiles, heldFiles) = scan;
            var bytes = sharedFiles.Sum(f => f.Size);
            SyncTotalLabel.Text = sharedFiles.Count == 0
                ? "nothing to upload yet"
                : $"{sharedFiles.Count:N0} file(s) · {ConfigHubService.FormatSize(bytes)}";

            SharedFilesList.ItemsSource = sharedFiles
                .OrderByDescending(f => f.Size).Take(FileRowCap)
                .Select(f => new ShareFileRow(f.Path, ConfigHubService.FormatSize(f.Size))).ToList();
            SharedFilesMoreLabel.Text = $"...and {sharedFiles.Count - FileRowCap:N0} more, biggest first.";
            SharedFilesMoreLabel.Visibility = sharedFiles.Count > FileRowCap ? Visibility.Visible : Visibility.Collapsed;

            if (heldFiles.Count > 0)
            {
                var describe = PrivateAssetPolicy.Describe(heldFiles.Select(f => f.Path).ToList(), App.State.Settings);
                PrivateLabel.Text = $"Held back on this PC and never uploaded: {describe} "
                                  + $"({ConfigHubService.FormatSize(heldFiles.Sum(f => f.Size))}). "
                                  + "Private paths are set in Settings; a sync can neither upload nor overwrite them.";
                PrivateList.ItemsSource = heldFiles
                    .OrderByDescending(f => f.Size).Take(10)
                    .Select(f => new ShareFileRow(f.Path, ConfigHubService.FormatSize(f.Size))).ToList();
                PrivatePanel.Visibility = Visibility.Visible;
            }
            else PrivatePanel.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { SyncTotalLabel.Text = ""; }
        catch (Exception ex)
        {
            AppLog.LogError("share.scan", ex);
            SyncTotalLabel.Text = "";
            _state.Note("The instance folder could not be measured, so what will sync is not known.");
        }
    }

    /// <summary>Where this PC stands against the shared copy: the version, and who moved it last.</summary>
    private async Task LoadSyncStateAsync(CancellationToken ct)
    {
        if (_pack is null) return;
        if (!_pack.IsShared)
        {
            SyncStateLabel.Text = "";
            return;
        }

        var local = App.State.Settings.PackSyncedVersion.TryGetValue(_packId, out var v) ? v : 0;
        try
        {
            var manifest = await App.State.Api.GetManifestAsync(_packId, ct);
            SyncStateLabel.Text = manifest.Version <= local
                ? $"You have the shared copy as it stands (version {manifest.Version:N0}, "
                + $"{TeamsView.Plural(manifest.Entries.Count, "file", "files")})."
                : $"The server is on version {manifest.Version:N0}; you last synced {local:N0}. "
                + "Download before you upload, or the server will refuse the upload to protect "
                + "whoever made those changes.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.LogError("share.manifest", ex);
            SyncStateLabel.Text = local > 0
                ? $"Last synced version {local:N0} on this PC. The server's current version could not be read."
                : "The server's current version could not be read.";
        }
    }

    // ══ activity ═════════════════════════════════════════════════════════════

    private async Task LoadActivityAsync(CancellationToken ct)
    {
        try
        {
            var page = await ShareApi.ActivityAsync(ActivitySubjectType.Pack, _packId, ActivityRows, ct);
            var rows = page.Items.Select(ShareActivityRow.From).ToList();
            ActivityList.ItemsSource = rows;
            ActivityEmptyLabel.Text = "Nothing has happened to this instance's sharing yet.";
            ActivityEmptyLabel.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.LogError("share.activity", ex);
            ActivityList.ItemsSource = null;
            ActivityEmptyLabel.Text = App.State.IsOffline
                ? "The activity feed is only on the server, so it is not available offline."
                : "The activity feed could not be read. " + ShareText.Explain(ex);
            ActivityEmptyLabel.Visibility = Visibility.Visible;
        }
    }

    // ══ enablement ═══════════════════════════════════════════════════════════

    /// <summary>Switches off what can't work right now and says why on the control itself.</summary>
    /// <remarks>Disabled rather than hidden, with <see cref="ToolTipService.SetShowOnDisabled"/> on
    /// each: a control that vanishes offline looks like a missing feature, and without that call
    /// the tooltip never shows on a disabled control.</remarks>
    private void ApplyGates()
    {
        var offline = App.State.IsOffline;
        var offlineWhy = "The server is not answering"
                       + (App.State.OfflineReason is { Length: > 0 } r ? $" ({r})" : "")
                       + ", so sharing can be read here but not changed.";

        OfflineLabel.Text = offline ? "Offline - read-only" : "";
        OfflineLabel.Visibility = offline ? Visibility.Visible : Visibility.Collapsed;

        var shared = _pack?.IsShared == true;
        var transferring = _transferCts is not null;

        Gate(PublishBox, _isOwner && !offline && !transferring,
            offline ? offlineWhy
            : transferring ? "Wait for the transfer to finish."
            : "Only the owner can publish this instance or take it down.");

        foreach (var radio in new[] { VisPrivateBox, VisTeamBox, VisPublicBox })
            Gate(radio, _canManage && !offline,
                offline ? offlineWhy : "You need permission to manage sharing to change this.");
        Gate(ListPubliclyButton, _canManage && !offline,
            offline ? offlineWhy : "You need permission to manage sharing to change this.",
            "Everyone on this server can then find it in Browse");

        Gate(AvailableTeamsBox, _canManage && !offline && AvailableTeamsBox.Items.Count > 0,
            offline ? offlineWhy
            : !_canManage ? "You need permission to manage sharing to add a team."
            : "Every team you are on already has access.");
        Gate(TeamRoleBox, _canManage && !offline,
            offline ? offlineWhy : "You need permission to manage sharing to add a team.",
            "What every member of the team will be able to do");
        Gate(AddTeamButton, _canManage && !offline && AvailableTeamsBox.Items.Count > 0,
            offline ? offlineWhy
            : !_canManage ? "You need permission to manage sharing to add a team."
            : "Every team you are on already has access.");

        Gate(UserSearch, !offline, offlineWhy, "Find someone by username");
        Gate(InviteRoleBox, _canManage && !offline,
            offline ? offlineWhy : "You need permission to manage sharing to invite people.",
            "What they will be able to do");
        Gate(InviteExpiryBox, _canManage && !offline,
            offline ? offlineWhy : "You need permission to manage sharing to invite people.",
            "How long the invitation stays open");
        Gate(SendInviteButton, _canManage && !offline,
            offline ? offlineWhy : "You need permission to manage sharing to invite people.");
        Gate(CopyLinkButton, _canManage && !offline,
            offline ? offlineWhy : "You need permission to manage sharing to mint a link.");
        Gate(RevokeLinkButton, _canManage && !offline,
            offline ? offlineWhy : "You need permission to manage sharing to revoke the link.");

        Gate(AdvancedPermsButton, _pack is not null,
            "Open the instance first.",
            "The per-permission grid: view, download, upload and manage, one tick box each");

        Gate(RulesButton, _isOwner,
            "The rules are the owner's and are shared with everyone who has this instance.",
            "Which files are uploaded, and which stay on this PC");

        Gate(PrimaryButton, !offline && (transferring || (_pack is not null && (_isOwner || _canUpload))),
            offline ? offlineWhy
            : !shared ? "Only the owner can publish this instance."
            : "You have read-only access to this instance, so you cannot upload changes to it.");
        Gate(DownloadButton, !offline && shared && _canDownload && !transferring,
            offline ? offlineWhy
            : !shared ? "This instance is not published, so there is nothing to download."
            : transferring ? "Wait for the transfer to finish."
            : "You do not have permission to download this instance's files.");
        Gate(ConflictDownloadButton, !offline && _canDownload && !transferring,
            offline ? offlineWhy : "You do not have permission to download this instance's files.");

        var canTransfer = _isOwner && !offline && TransferTargetBox.Items.Count > 0;
        Gate(TransferTargetBox, canTransfer,
            offline ? offlineWhy
            : !_isOwner ? "Only the owner can hand this instance to somebody else."
            : "Add the person as a collaborator first - an instance can only be handed to someone who already has access.");
        Gate(TransferButton, canTransfer,
            offline ? offlineWhy
            : !_isOwner ? "Only the owner can hand this instance to somebody else."
            : "Add the person as a collaborator first - an instance can only be handed to someone who already has access.");

        // The rows carry their own hints, and offline changes all of them.
        if (_pack is not null) BuildAccessRows(_pack);
    }

    private static void Gate(FrameworkElement element, bool enabled, string reason, string? enabledTip = null)
    {
        element.IsEnabled = enabled;
        element.ToolTip = enabled ? enabledTip : reason;
        ToolTipService.SetShowOnDisabled(element, true);
    }

    // ══ publishing and visibility ════════════════════════════════════════════

    private async void OnPublishToggled(object sender, RoutedEventArgs e)
    {
        if (_pack is null || _shell is null) return;
        var wanted = PublishBox.IsChecked == true;

        if (!wanted)
        {
            var ok = await AppDialog.ConfirmAsync(_shell, "Stop publishing this instance",
                $"'{_pack.Name}' stops syncing with CloudLauncher.\n\n"
                + "Everyone you shared it with keeps the copy already on their PC - nothing is "
                + "deleted from anybody's machine - but they stop getting your changes and cannot "
                + "download it again until you publish it once more.",
                "Stop publishing", "Keep it published", danger: true);
            if (!ok) { PublishBox.IsChecked = true; return; }
        }

        await MutateAsync(
            wanted ? "Publishing..." : "Taking it down...",
            async ct =>
            {
                if (wanted) App.State.PackAssets.MirrorToSharedFolder(_packId);
                await App.State.Api.UpdatePackAsync(_packId,
                    new UpdatePackRequest(null, null, null, IsShared: wanted, null, null, null, null), ct);
            },
            wanted
                ? "Published. Nothing has been uploaded yet - press Upload changes when you are ready."
                : "Taken down. Nobody new can download it.",
            () => PublishBox.IsChecked = !wanted);
    }

    private async void OnVisibilityPicked(object sender, RoutedEventArgs e)
    {
        if (_pack is null) return;
        var wanted = VisPublicBox.IsChecked == true ? PackVisibility.Public
                   : VisTeamBox.IsChecked == true ? PackVisibility.Team
                   : PackVisibility.Private;
        if (wanted == _pack.Visibility) return;

        var previous = _pack.Visibility;
        await MutateAsync("Changing who can find it...",
            ct => App.State.Api.UpdatePackAsync(_packId,
                new UpdatePackRequest(null, null, Visibility: wanted, null, null, null, null, null), ct),
            wanted switch
            {
                PackVisibility.Public => "Anyone on this server can now find and download it.",
                PackVisibility.Team => "Everyone on the teams below can now find and download it.",
                _ => "Only the people and teams listed below can see it now."
            },
            () =>
            {
                VisPrivateBox.IsChecked = previous == PackVisibility.Private;
                VisTeamBox.IsChecked = previous == PackVisibility.Team;
                VisPublicBox.IsChecked = previous == PackVisibility.Public;
            });
    }

    /// <summary>The "Published, but not listed" note's button: list it for everyone.</summary>
    /// <remarks>Publishing alone never changes who can find an instance, or a pack shared by
    /// invitation would go public behind its owner's back.</remarks>
    private async void OnListPublicly(object sender, RoutedEventArgs e)
    {
        if (_pack is null) return;
        await MutateAsync("Listing it for everyone...",
            ct => App.State.Api.UpdatePackAsync(_packId,
                new UpdatePackRequest(null, null, Visibility: PackVisibility.Public, null, null, null, null, null), ct),
            "Listed. Everyone on this server can now find it in Browse.");
    }

    // ══ access list ══════════════════════════════════════════════════════════

    private async void OnRoleEdited(ShareAccessRow row)
    {
        if (row.SelectedRole is not { } role) return;
        var previous = row.SavedRole;
        await MutateAsync($"Changing what {row.Title} can do...",
            async ct =>
            {
                if (row.Kind == ShareAccessRow.RowKind.Team)
                    await App.State.Api.UpdatePackTeamAsync(_packId, row.Id, new UpdatePackTeamRequest(role.Value), ct);
                else
                    await App.State.Api.UpdateCollaboratorAsync(_packId, row.Id, new UpdateCollaboratorRequest(role.Value), ct);
                row.MarkSaved(role);
            },
            $"{row.Title} can now {ShareRole.Describe(role.Value)}.",
            () => row.RevertRole(previous));
    }

    private async void OnRemoveAccess(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ShareAccessRow row } || _shell is null) return;

        var body = row.Kind == ShareAccessRow.RowKind.Team
            ? $"Everyone on {row.Title} loses access to this instance.\n\n"
            + "Anybody who was also invited by name keeps the access they were given individually."
            : $"{row.Title} loses access to this instance.\n\n"
            + "The copy already on their PC stays where it is; they stop receiving changes and "
            + "cannot download it again.";

        if (!await AppDialog.ConfirmAsync(_shell, "Remove access", body, "Remove", "Cancel", danger: true))
            return;

        await MutateAsync($"Removing {row.Title}...",
            ct => row.Kind == ShareAccessRow.RowKind.Team
                ? App.State.Api.RemovePackTeamAsync(_packId, row.Id, ct)
                : App.State.Api.RemoveCollaboratorAsync(_packId, row.Id, ct),
            $"{row.Title} no longer has access.");
    }

    private async void OnAddTeam(object sender, RoutedEventArgs e)
    {
        if (AvailableTeamsBox.SelectedItem is not TeamSummary team) return;
        var role = SelectedRole(TeamRoleBox);
        await MutateAsync($"Sharing with {team.Name}...",
            ct => App.State.Api.AddPackTeamAsync(_packId, new AddPackTeamRequest(team.Id, role.Value), ct),
            $"{team.Name} ({TeamsView.Plural(team.MemberCount, "member", "members")}) can now {ShareRole.Describe(role.Value)}.");
    }

    private void OnAdvancedPermissions(object sender, RoutedEventArgs e)
    {
        if (_pack is null) return;
        try
        {
            new PermissionsDialog(_pack) { Owner = _shell }.ShowDialog();
        }
        catch (Exception ex)
        {
            AppLog.LogError("share.permissions", ex);
            _state.Note("The permission grid could not be opened. The details are in the launcher log.");
            return;
        }
        _ = LoadAsync();
    }

    // ══ invitations and the share link ═══════════════════════════════════════

    private async Task SearchUsersAsync(string query)
    {
        try { _searchCts?.Cancel(); } catch (ObjectDisposedException) { /* already finished */ }
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        if (query.Length < 2)
        {
            UserResults.ItemsSource = null;
            UserResultsCard.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            var page = await ShareApi.SearchUsersAsync(query, cts.Token);
            if (cts.IsCancellationRequested) return;
            var items = page.Items.Where(u => u.Id != App.State.Settings.UserId).ToList();
            UserResults.ItemsSource = items;
            UserResultsCard.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            InvitePickLabel.Text = items.Count == 0
                ? $"Nobody on this server has a name starting with '{query}'."
                : InvitePickLabel.Text;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.LogError("share.usersearch", ex);
            UserResultsCard.Visibility = Visibility.Collapsed;
            InvitePickLabel.Text = "Could not search for people. " + ShareText.Explain(ex);
        }
        finally { cts.Dispose(); }
    }

    private void OnUserPicked(object sender, SelectionChangedEventArgs e)
    {
        if (UserResults.SelectedItem is not UserSummary user) return;
        _invitee = user;
        UserSearch.Text = user.Username;
        InvitePickLabel.Text = $"Inviting {user.Username} - pick what they may do, then send it.";
    }

    private async void OnSendInvite(object sender, RoutedEventArgs e)
    {
        var name = (_invitee?.Username ?? UserSearch.Text).Trim();
        if (name.Length == 0)
        {
            InvitePickLabel.Text = "Type the username of the person you want to invite.";
            return;
        }

        var role = SelectedRole(InviteRoleBox);
        var expiry = InviteExpiryBox.SelectedItem as ExpiryChoice ?? ExpiryChoice.All[0];
        await MutateAsync($"Inviting {name}...",
            async ct =>
            {
                var invite = await ShareApi.InviteAsync(_packId,
                    new CreatePackInvitationRequest(name, role.Value, null, expiry.Days), ct);
                if (invite.Token is { Length: > 0 }) ClipboardHelper.TrySetText(SharingHubService.LinkFor(invite.Token));
            },
            $"Invited {name} - they can {ShareRole.Describe(role.Value)} once they accept. "
            + "The invitation's link is on your clipboard if you would rather send it to them yourself.",
            () => { });

        _invitee = null;
        UserSearch.Clear();
        // Clearing collapses the box back to its magnifier, which on this card reads as a stray
        // glyph rather than "type the next name here".
        UserSearch.Expand();
        UserResultsCard.Visibility = Visibility.Collapsed;
    }

    private async void OnResendInvite(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ShareInviteRow row }) return;
        if (row.Username is not { Length: > 0 } name) return;
        var expiry = InviteExpiryBox.SelectedItem as ExpiryChoice ?? ExpiryChoice.All[0];

        // Re-inviting the same person replaces the live invitation server-side, so there is never a
        // second token offering something different.
        await MutateAsync($"Re-sending to {name}...",
            async ct =>
            {
                var invite = await ShareApi.InviteAsync(_packId,
                    new CreatePackInvitationRequest(name, row.Permissions, null, expiry.Days), ct);
                if (invite.Token is { Length: > 0 }) ClipboardHelper.TrySetText(SharingHubService.LinkFor(invite.Token));
            },
            $"Sent again to {name}. The new link is on your clipboard; the old one no longer works.");
    }

    private async void OnRevokeInvite(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ShareInviteRow row }) return;
        await MutateAsync("Withdrawing the invitation...",
            ct => ShareApi.RevokeInviteAsync(_packId, row.Id, ct),
            row.Username is { Length: > 0 } name
                ? $"{name}'s invitation has been withdrawn."
                : "That invitation has been withdrawn.");
    }

    private void OnCopyInviteLink(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ShareInviteRow row }) return;
        if (row.Token is not { Length: > 0 } token)
        {
            _state.Note("That invitation's link is not shown to you.");
            return;
        }
        _state.Note(ClipboardHelper.TrySetText(SharingHubService.LinkFor(token))
            ? "Invitation link copied. They paste it into Sharing > Redeem a link and get the access on this row."
            : "The clipboard is busy - try the copy again in a moment.");
    }

    private async void OnCopyShareLink(object sender, RoutedEventArgs e)
    {
        if (_pack is null) return;

        if (_pack.ShareToken is { Length: > 0 } existing)
        {
            _state.Note(ClipboardHelper.TrySetText(SharingHubService.LinkFor(existing))
                ? "Share link copied. It keeps working until you revoke it."
                : "The clipboard is busy - try the copy again in a moment.");
            return;
        }

        var role = SelectedRole(InviteRoleBox);
        var expiry = InviteExpiryBox.SelectedItem as ExpiryChoice ?? ExpiryChoice.All[0];
        await MutateAsync("Making a share link...",
            async ct =>
            {
                var link = await ShareApi.CreateShareLinkAsync(_packId,
                    new CreateShareLinkRequest(role.Value, expiry.Days), ct);
                ClipboardHelper.TrySetText(SharingHubService.LinkFor(link.Token));
            },
            $"Share link copied. Anyone who redeems it can {ShareRole.Describe(role.Value)}.");
    }

    private async void OnRevokeShareLink(object sender, RoutedEventArgs e)
    {
        if (_pack is null || _shell is null) return;
        if (!await AppDialog.ConfirmAsync(_shell, "Revoke the share link",
                "The link stops working for everybody who has it.\n\n"
                + "People who already redeemed it keep the access it gave them - remove them from "
                + "the access list if that is what you meant.",
                "Revoke", "Cancel", danger: true))
            return;

        await MutateAsync("Revoking the link...",
            ct => ShareApi.RevokeShareLinkAsync(_packId, ct),
            "The share link no longer works.");
    }

    // ══ ownership ════════════════════════════════════════════════════════════

    private async void OnTransferOwnership(object sender, RoutedEventArgs e)
    {
        if (_pack is null || _shell is null) return;
        if (TransferTargetBox.SelectedItem is not PackCollaboratorEntry target) return;

        if (!await AppDialog.ConfirmAsync(_shell, "Hand this instance over",
                $"{target.Username} becomes the owner of '{_pack.Name}'.\n\n"
                + "Its files start counting against their storage, they decide who may see it from "
                + "then on, and only they can delete it. You stay on the list with full access, but "
                + "you cannot take it back on your own.",
                "Transfer", "Cancel", danger: true))
            return;

        await MutateAsync($"Handing it to {target.Username}...",
            ct => ShareApi.TransferPackAsync(_packId, new TransferPackOwnershipRequest(target.UserId), ct),
            $"{target.Username} owns this instance now. You are still on it with full access.");
    }

    // ══ upload and download ══════════════════════════════════════════════════

    private void OnPrimary(object sender, RoutedEventArgs e)
    {
        if (_pack is null) return;
        if (_transferCts is not null) { CancelTransfer("Stopping..."); return; }
        if (!_pack.IsShared) { PublishBox.IsChecked = true; OnPublishToggled(PublishBox, e); return; }
        _ = UploadAsync();
    }

    private void OnDownload(object sender, RoutedEventArgs e) => _ = DownloadAsync();

    private void CancelTransfer(string note)
    {
        _state.Note(note);
        try { _transferCts?.Cancel(); } catch (ObjectDisposedException) { /* already finished */ }
    }

    private async Task UploadAsync()
    {
        if (_pack is null || _transferCts is not null) return;
        if (PackJobs.IsRunning(_packId))
        {
            _state.Note("A transfer is already running for this instance - use Pause or Stop on it first.");
            return;
        }

        var cts = new CancellationTokenSource();
        _transferCts = cts;
        BeginTransferUi("Uploading the shared files.");
        try
        {
            App.State.PackAssets.MirrorToSharedFolder(_packId);
            var gameDir = App.State.Packs.GameDir(_packId);
            var rules = App.State.Rules.Load(App.State.Packs.PackRoot(_packId));
            var paths = await Task.Run(() => CollectSharedPaths(gameDir, rules), cts.Token);

            // The base version is the one this PC last synced, never the server's current one:
            // sending the current version defeats the server's concurrency check, and a stale
            // client's file set would then replace the whole manifest and delete a collaborator's
            // work for every subscriber.
            var baseVersion = App.State.Settings.PackSyncedVersion.TryGetValue(_packId, out var v) ? v : 0;
            var newVersion = await App.State.Packs.UploadSharedAsync(_packId, baseVersion, paths, TransferLog(), cts.Token);

            App.State.Settings.PackSyncedVersion[_packId] = newVersion;
            App.State.Settings.Save();
            HideConflict();
            _state.Note($"Uploaded. Everyone with access gets {TeamsView.Plural(paths.Count, "file", "files")} "
                      + $"at version {newVersion:N0} the next time they open this instance.");
        }
        catch (ApiException ex) when (ex.Status == HttpStatusCode.Conflict)
        {
            AppLog.LogError("share.upload", ex);
            ShowConflict(ShareText.ParseConflict(ex));
        }
        catch (OperationCanceledException)
        {
            // Nothing is committed until the whole set lands, so a stopped upload leaves the server
            // unchanged.
            ProgressHub.Clear(_packId);
            _state.Cancelled("Upload stopped - nothing on the server changed.");
        }
        catch (Exception ex)
        {
            AppLog.LogError("share.upload", ex);
            _state.Error("the upload did not finish - " + ShareText.Explain(ex), ex);
        }
        finally
        {
            _transferCts = null;
            cts.Dispose();
            EndTransferUi();
            await LoadAsync();
        }
    }

    private async Task DownloadAsync()
    {
        if (_pack is null || _transferCts is not null) return;
        if (PackJobs.IsRunning(_packId))
        {
            _state.Note("A transfer is already running for this instance - use Pause or Stop on it first.");
            return;
        }

        var job = PackJobs.Start(_packId, PackJobKind.Sync, _pack.Name);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(job.Token);
        _transferCts = cts;
        BeginTransferUi("Downloading the shared files.");
        try
        {
            await App.State.Packs.DownloadSharedAsync(_packId, TransferLog(), cts.Token, job);
            HideConflict();
            _state.Note("This instance now matches the shared copy.");
        }
        catch (OperationCanceledException)
        {
            var removed = job.RollbackCreatedFiles();
            ProgressHub.Clear(_packId);
            _state.Cancelled(removed > 0
                ? $"Download stopped - the {removed} file(s) it had added were removed."
                : "Download stopped.");
        }
        catch (Exception ex)
        {
            AppLog.LogError("share.download", ex);
            _state.Error("the download did not finish - " + ShareText.Explain(ex), ex);
        }
        finally
        {
            PackJobs.Finish(job);
            _transferCts = null;
            cts.Dispose();
            EndTransferUi();
            await LoadAsync();
        }
    }

    /// <summary>Every file the next upload would carry: the rule-shared set plus the pack's own
    /// assets.</summary>
    /// <remarks>Private paths are dropped here as well as in
    /// <c>PackFolderService.UploadSharedAsync</c>. The service is the real boundary; this keeps the
    /// count on screen equal to what gets sent.</remarks>
    private static List<string> CollectSharedPaths(string gameDir, List<PackRule> rules)
    {
        var result = new List<string>();
        if (!Directory.Exists(gameDir)) return result;

        foreach (var abs in Directory.EnumerateFiles(gameDir, "*", SearchOption.AllDirectories)
                     .OrderBy(s => s, StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(gameDir, abs).Replace('\\', '/');
            if (rel.StartsWith(".cloudlauncher/", StringComparison.OrdinalIgnoreCase)) { result.Add(rel); continue; }
            if (App.State.Rules.Match(rel, rules).IsAutoShared && !PrivateAssetPolicy.IsPrivate(rel, App.State.Settings))
                result.Add(rel);
        }
        return result;
    }

    private IProgress<string> TransferLog() => new Progress<string>(line =>
    {
        _state.Progress(line);
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    });

    private void BeginTransferUi(string what)
    {
        LogBox.Clear();
        LogExpander.Visibility = Visibility.Visible;
        _state.Begin(what, refreshing: true);
        UpdatePrimaryButton();
        ApplyGates();
    }

    private void EndTransferUi()
    {
        UpdatePrimaryButton();
        ApplyGates();
    }

    // ══ the 409 ══════════════════════════════════════════════════════════════

    /// <summary>Renders the divergence the server refused the upload over.</summary>
    /// <remarks>The server sends its current version, who changed it and the differing paths, so
    /// this shows the list and the one safe move: pull first, then upload again.</remarks>
    private void ShowConflict(ManifestConflict? conflict)
    {
        _conflict = conflict;
        if (conflict is null)
        {
            ConflictSummaryLabel.Text =
                "Somebody uploaded to this instance after you last synced, so the server refused "
                + "your upload rather than overwriting their work. It did not say which files "
                + "differ. Download the shared copy, then upload again.";
            ConflictList.ItemsSource = null;
            ConflictMoreLabel.Visibility = Visibility.Collapsed;
            ConflictAdviceLabel.Text = "Nothing was uploaded and nothing on the server changed.";
            ConflictPanel.Visibility = Visibility.Visible;
            return;
        }

        var who = conflict.LastUploadedByUsername is { Length: > 0 } name
            ? $"{name} uploaded {Ago(conflict.LastUploadedAt ?? conflict.ServerUpdatedAt)}"
            : $"somebody uploaded {Ago(conflict.ServerUpdatedAt)}";
        ConflictSummaryLabel.Text =
            $"{who}, taking it to version {conflict.CurrentVersion:N0}; you were working from "
            + $"{conflict.BaseVersion:N0}. Nothing was uploaded and nothing on the server changed. "
            + $"{conflict.TotalDifferences:N0} path(s) differ between the shared copy and what you were sending.";

        ConflictList.ItemsSource = conflict.Paths.Take(ConflictRowCap)
            .Select(p => new ShareConflictRow(p.RelativePath, p.Change switch
            {
                ManifestConflictChange.OnlyOnServer => "only on the server",
                ManifestConflictChange.OnlyInUpload => "only in your copy",
                _ => "different contents"
            })).ToList();
        ConflictMoreLabel.Text = $"...and {Math.Max(0, conflict.TotalDifferences - ConflictRowCap):N0} more.";
        ConflictMoreLabel.Visibility = conflict.TotalDifferences > ConflictRowCap || conflict.PathsTruncated
            ? Visibility.Visible : Visibility.Collapsed;

        var overwrite = conflict.Paths.Count(p => p.Change == ManifestConflictChange.HashDiffers);
        ConflictAdviceLabel.Text = overwrite > 0
            ? $"Downloading replaces your copy of the {overwrite} file(s) marked 'different contents' "
            + "with the server's. If you changed one of them on purpose, copy it somewhere else first "
            + "- the launcher does not keep a backup of a file a sync replaces."
            : "Downloading is safe here: none of the differing files exists on both sides with "
            + "different contents.";
        ConflictPanel.Visibility = Visibility.Visible;
    }

    private void HideConflict()
    {
        _conflict = null;
        ConflictPanel.Visibility = Visibility.Collapsed;
    }

    private void OnDismissConflict(object sender, RoutedEventArgs e) => HideConflict();

    private async void OnResolveConflict(object sender, RoutedEventArgs e)
    {
        await DownloadAsync();
        if (_conflict is not null && _transferCts is null)
            _state.Note("Downloaded. Check the files you had changed, then press Upload changes again.");
    }

    // ══ rules ════════════════════════════════════════════════════════════════

    private void OnEditRules(object sender, RoutedEventArgs e)
    {
        if (_pack is null) return;
        try
        {
            new PackRulesDialog(App.State.Packs.PackRoot(_packId), _pack.Name, _packId, _isOwner)
                { Owner = _shell }.ShowDialog();
        }
        catch (Exception ex)
        {
            AppLog.LogError("share.rules", ex);
            _state.Note("The rules editor could not be opened. The details are in the launcher log.");
            return;
        }
        _ = LoadAsync();
    }

    // ══ one shape for every mutation ═════════════════════════════════════════

    /// <summary>Runs one server-side change: busy line, plain-words failure, undo of the control
    /// that moved, and a reload so the tab matches the server again.</summary>
    /// <remarks><paramref name="undo"/> exists because every control here changes before the server
    /// agrees (a box is already ticked, a combo already shows the new role), so a refusal has to
    /// put it back.</remarks>
    private async Task MutateAsync(string busy, Func<CancellationToken, Task> work, string done, Action? undo = null)
    {
        if (_busy) { _state.Note("One change at a time - wait for the last one to finish."); undo?.Invoke(); return; }
        _busy = true;
        _state.Begin(busy, refreshing: true);
        var cts = new CancellationTokenSource();
        try
        {
            await work(cts.Token);
            _busy = false;
            await LoadAsync();
            _state.Note(done);
        }
        catch (Exception ex)
        {
            AppLog.LogError("share", ex);
            undo?.Invoke();
            _busy = false;
            _state.Error(ShareText.Explain(ex), ex);
        }
        finally
        {
            _busy = false;
            cts.Dispose();
        }
    }

    // ══ helpers ══════════════════════════════════════════════════════════════

    private static ShareRole SelectedRole(ComboBox box) =>
        box.SelectedItem as ShareRole ?? ShareRole.All[1];

    /// <summary>"3 hours ago", or the date once that stops being useful. Both come from
    /// <see cref="TimeFormat"/>, which follows the Windows date format.</summary>
    private static string Ago(DateTimeOffset when) =>
        TimeFormat.Ago(when) ?? TimeFormat.Date(when);
}

// ══ view rows ════════════════════════════════════════════════════════════════

/// <summary>One of the four answers to "what should this person be able to do?".</summary>
/// <remarks>The server's <see cref="PackPermissions"/> bits, named for what they let someone do. Of
/// the sixteen combinations, these four cover what people mean; the rest are under
/// "Advanced...".</remarks>
public sealed record ShareRole(string Label, PackPermissions Value)
{
    public static readonly IReadOnlyList<ShareRole> All =
    [
        new("Can view", PackPermissions.View),
        new("Can view and download", PackPermissions.ReadOnly),
        new("Can upload changes", PackPermissions.Contributor),
        new("Can manage sharing", PackPermissions.Full)
    ];

    /// <summary>The role for a permission set, or a "Custom" entry for combinations the four roles
    /// don't name, so a row never rounds someone's access up or down to fit the picker.</summary>
    public static ShareRole For(PackPermissions value) =>
        All.FirstOrDefault(r => r.Value == value) ?? new ShareRole("Custom: " + Describe(value), value);

    /// <summary>The permission set in the same words the tick boxes use.</summary>
    public static string Describe(PackPermissions p)
    {
        var parts = new List<string>();
        if (p.HasFlag(PackPermissions.View)) parts.Add("view");
        if (p.HasFlag(PackPermissions.Download)) parts.Add("download");
        if (p.HasFlag(PackPermissions.UploadShared)) parts.Add("upload");
        if (p.HasFlag(PackPermissions.ManageCollaborators)) parts.Add("manage sharing");
        return parts.Count == 0 ? "do nothing" : string.Join(", ", parts);
    }
}

/// <summary>How long an invitation or a share link stays open.</summary>
public sealed record ExpiryChoice(string Label, int? Days)
{
    public static readonly IReadOnlyList<ExpiryChoice> All =
    [
        new("Never expires", null),
        new("Expires in 7 days", 7),
        new("Expires in 30 days", 30),
        new("Expires in 90 days", 90)
    ];
}

/// <summary>
/// One row of the merged access list: the owner, a person, a team, or everyone on the server.
/// </summary>
/// <remarks>
/// The role box binds two-way to <see cref="SelectedRole"/> and the row raises
/// <see cref="RoleEdited"/> itself, so filling the list cannot fire a save the way a
/// <c>SelectionChanged</c> handler on a templated ComboBox would. <see cref="SavedRole"/> is what
/// the server last accepted, which is what a refused change is put back to.
/// </remarks>
public sealed class ShareAccessRow : INotifyPropertyChanged
{
    public enum RowKind { Owner, Person, Team, Public }

    private ShareRole? _selected;
    private bool _live;

    private ShareAccessRow(RowKind kind) { Kind = kind; }

    public RowKind Kind { get; }
    public Guid Id { get; private init; }
    public string Glyph { get; private init; } = "";
    public string Title { get; private init; } = "";
    public string Subtitle { get; private init; } = "";
    public bool CanEdit { get; private init; }
    public string RoleHint { get; private init; } = "";
    public string RemoveHint { get; private init; } = "";
    public string FixedRoleLabel { get; private init; } = "";
    public IReadOnlyList<ShareRole> Roles { get; private init; } = ShareRole.All;

    public Visibility RoleVisibility => Kind is RowKind.Person or RowKind.Team ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FixedRoleVisibility => RoleVisibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    public Visibility RemoveVisibility => RoleVisibility;

    /// <summary>The permissions the server last accepted for this row.</summary>
    public ShareRole? SavedRole { get; private set; }

    public ShareRole? SelectedRole
    {
        get => _selected;
        set
        {
            if (Equals(_selected, value)) return;
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedRole)));
            if (_live && value is not null) RoleEdited?.Invoke(this);
        }
    }

    /// <summary>Raised when the user picks a different role, never while the row is being built or
    /// reverted.</summary>
    public event Action<ShareAccessRow>? RoleEdited;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Starts raising <see cref="RoleEdited"/>. Called once the row is in the list.</summary>
    public void Activate() => _live = true;

    public void MarkSaved(ShareRole role) => SavedRole = role;

    /// <summary>Puts the picker back to what the server still holds, without raising an edit.</summary>
    public void RevertRole(ShareRole? saved)
    {
        _live = false;
        try { SelectedRole = saved; }
        finally { _live = true; }
    }

    public static ShareAccessRow ForOwner(string username, string note) => new(RowKind.Owner)
    {
        Glyph = "",
        Title = username,
        Subtitle = note,
        FixedRoleLabel = "Owner",
        CanEdit = false
    };

    public static ShareAccessRow ForPerson(PackCollaboratorEntry entry, bool canManage, bool offline, Guid? me)
    {
        var role = ShareRole.For(entry.Permissions);
        var row = new ShareAccessRow(RowKind.Person)
        {
            Id = entry.UserId,
            Glyph = "",
            Title = entry.Username + (me is not null && entry.UserId == me ? " (you)" : ""),
            Subtitle = "Invited by name · can " + ShareRole.Describe(entry.Permissions),
            Roles = RolesFor(role),
            CanEdit = canManage && !offline,
            RoleHint = canManage && !offline
                ? $"What {entry.Username} may do with this instance"
                : Hint(canManage, offline, "change what somebody may do"),
            RemoveHint = canManage && !offline
                ? $"Remove {entry.Username}'s access"
                : Hint(canManage, offline, "remove somebody")
        };
        row.SavedRole = role;
        row.SelectedRole = role;
        return row;
    }

    public static ShareAccessRow ForTeam(PackTeamEntry entry, bool canManage, bool offline)
    {
        var role = ShareRole.For(entry.Permissions);
        var members = entry.MemberCount > 0
            ? TeamsView.Plural(entry.MemberCount, "member", "members")
            : "members";
        var names = entry.MemberUsernames is { Count: > 0 } list
            ? " · " + string.Join(", ", list) + (entry.MemberCount > list.Count ? ", ..." : "")
            : "";
        var row = new ShareAccessRow(RowKind.Team)
        {
            Id = entry.TeamId,
            Glyph = "",
            Title = entry.TeamName,
            Subtitle = $"Team · {members}{names}",
            Roles = RolesFor(role),
            CanEdit = canManage && !offline,
            RoleHint = canManage && !offline
                ? $"What every member of {entry.TeamName} may do with this instance"
                : Hint(canManage, offline, "change what a team may do"),
            RemoveHint = canManage && !offline
                ? $"Take {entry.TeamName}'s access away"
                : Hint(canManage, offline, "remove a team")
        };
        row.SavedRole = role;
        row.SelectedRole = role;
        return row;
    }

    public static ShareAccessRow ForPublic() => new(RowKind.Public)
    {
        Glyph = "",
        Title = "Everyone on this server",
        Subtitle = "Anyone with an account can find this instance and download its files.",
        FixedRoleLabel = "Can view and download",
        CanEdit = false
    };

    private static IReadOnlyList<ShareRole> RolesFor(ShareRole current) =>
        ShareRole.All.Contains(current) ? ShareRole.All : [.. ShareRole.All, current];

    private static string Hint(bool canManage, bool offline, string what) => offline
        ? "The server is not answering, so sharing can be read here but not changed."
        : canManage ? "This row can be changed here." : $"You need permission to manage sharing to {what}.";
}

/// <summary>One invitation that has not been answered yet.</summary>
public sealed class ShareInviteRow
{
    public Guid Id { get; private init; }
    public string? Username { get; private init; }
    public string? Token { get; private init; }
    public PackPermissions Permissions { get; private init; }
    public string Glyph { get; private init; } = "";
    public string Title { get; private init; } = "";
    public string Subtitle { get; private init; } = "";
    public bool CanAct { get; private init; }
    public string ActHint { get; private init; } = "";
    public Visibility ResendVisibility { get; private init; } = Visibility.Visible;

    public static ShareInviteRow From(PackInvitationEntry entry, bool canManage, bool offline)
    {
        var expiry = entry.ExpiresAt is { } at
            ? $" · expires {TimeFormat.Date(at)}"
            : " · does not expire";
        return new ShareInviteRow
        {
            Id = entry.Id,
            Username = entry.InvitedUsername,
            Token = entry.Token,
            Permissions = entry.Permissions,
            Title = entry.InvitedUsername ?? "Share link",
            Subtitle = $"Invited by {entry.InvitedByUsername} · would be able to "
                     + $"{ShareRole.Describe(entry.Permissions)}{expiry}",
            CanAct = canManage && !offline,
            // Never the empty string: WPF happily shows an empty tooltip box for one.
            ActHint = offline
                ? "The server is not answering, so invitations can be read here but not changed."
                : canManage
                    ? "Re-send replaces this invitation with a fresh one; Revoke withdraws it."
                    : "You need permission to manage sharing to change an invitation.",
            ResendVisibility = entry.InvitedUsername is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed
        };
    }
}

public sealed record ShareFileRow(string Path, string SizeLabel);

public sealed record ShareConflictRow(string Path, string ChangeLabel);

/// <summary>One line of the activity feed, already turned into a sentence.</summary>
public sealed record ShareActivityRow(string Text, string When)
{
    public static ShareActivityRow From(ActivityFeedEntry e)
    {
        var who = e.ActorUsername ?? "somebody";
        var target = e.TargetTeamName ?? e.TargetUsername;
        var text = e.Kind switch
        {
            ActivityKind.Shared => target is null ? $"{who} shared this instance" : $"{who} shared this with {target}",
            ActivityKind.Unshared => target is null ? $"{who} stopped sharing this" : $"{who} took {target}'s access away",
            ActivityKind.PermissionsChanged => target is null
                ? $"{who} changed what somebody may do"
                : $"{who} changed what {target} may do" + Detail(e.Detail),
            ActivityKind.VisibilityChanged => $"{who} changed who can find this" + Detail(e.Detail),
            ActivityKind.Uploaded => $"{who} uploaded changes" + Detail(e.Detail),
            ActivityKind.VersionPublished => $"{who} published a version" + Detail(e.Detail),
            ActivityKind.MemberAdded => $"{who} added {target ?? "somebody"}",
            ActivityKind.MemberRemoved => $"{who} removed {target ?? "somebody"}",
            ActivityKind.RoleChanged => $"{who} changed {target ?? "somebody"}'s role" + Detail(e.Detail),
            ActivityKind.InviteSent => target is null
                ? $"{who} made a share link"
                : $"{who} invited {target}",
            ActivityKind.InviteAccepted => $"{target ?? who} accepted an invitation",
            ActivityKind.OwnershipTransferred => $"{who} handed this to {target ?? "somebody else"}",
            _ => $"{who} changed something"
        };
        return new ShareActivityRow(text, TimeFormat.Ago(e.CreatedAt) ?? TimeFormat.MonthDay(e.CreatedAt));
    }

    private static string Detail(string? detail) =>
        detail is { Length: > 0 } d ? $" ({d.ToLowerInvariant()})" : "";
}

// ══ the endpoints this tab needs and ApiClient does not have yet ═════════════

/// <summary>The sharing endpoints (invitations, share links, ownership transfer, user search and
/// the activity feed), called directly.</summary>
/// <remarks><para><see cref="ApiClient"/> has no methods for these yet; everything it does have is
/// still called through it. Keeping them in one class makes moving them over later a body swap per
/// method.</para>
/// <para>The access token is read from <see cref="AppSettings"/> on every call so a refresh
/// <see cref="ApiClient"/> just did is picked up. Refreshing is left to <see cref="ApiClient"/>:
/// two callers racing to spend a rotating refresh token would sign the user out.</para></remarks>
internal static class ShareApi
{
    private static readonly HttpClient Http = ApiClient.WithUserAgent(new() { Timeout = TimeSpan.FromSeconds(30) });
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Task<UserSearchPage> SearchUsersAsync(string query, CancellationToken ct) =>
        SendAsync<UserSearchPage>(HttpMethod.Get, $"users?q={Uri.EscapeDataString(query)}", null, ct);

    public static Task<PackInvitationEntry> InviteAsync(Guid packId, CreatePackInvitationRequest req, CancellationToken ct) =>
        SendAsync<PackInvitationEntry>(HttpMethod.Post, $"packs/{packId}/invitations", req, ct);

    public static Task RevokeInviteAsync(Guid packId, Guid invitationId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"packs/{packId}/invitations/{invitationId}", null, ct);

    public static Task<ShareLinkInfo> CreateShareLinkAsync(Guid packId, CreateShareLinkRequest req, CancellationToken ct) =>
        SendAsync<ShareLinkInfo>(HttpMethod.Post, $"packs/{packId}/share-link", req, ct);

    public static Task RevokeShareLinkAsync(Guid packId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"packs/{packId}/share-link", null, ct);

    public static Task TransferPackAsync(Guid packId, TransferPackOwnershipRequest req, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"packs/{packId}/transfer", req, ct);

    public static Task<ActivityFeedPage> ActivityAsync(ActivitySubjectType type, Guid subjectId, int limit, CancellationToken ct) =>
        SendAsync<ActivityFeedPage>(HttpMethod.Get,
            $"activity?subjectType={type}&subjectId={subjectId}&limit={limit}", null, ct);

    private static async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var resp = await RawAsync(method, path, body, ct);
        await ThrowIfRefusedAsync(resp, ct);
        var value = await resp.Content.ReadFromJsonAsync<T>(Json, ct);
        return value ?? throw new ApiException("The server's answer was empty.", resp.StatusCode);
    }

    private static async Task SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var resp = await RawAsync(method, path, body, ct);
        await ThrowIfRefusedAsync(resp, ct);
    }

    private static async Task<HttpResponseMessage> RawAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var baseUrl = App.State.Settings.ServerUrl.TrimEnd('/') + "/";
        using var req = new HttpRequestMessage(method, new Uri(new Uri(baseUrl), path));
        if (App.State.Settings.AccessToken is { Length: > 0 } token)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) req.Content = JsonContent.Create(body, options: Json);

        try
        {
            return await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (Connectivity.DescribeTransportFailure(ex, ct) is { } why)
        {
            // A failure to ask is not an answer: same distinction ApiClient makes, so a caller can
            // tell "you are offline" from "the server said no".
            throw new OfflineException(why);
        }
    }

    private static async Task ThrowIfRefusedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new SessionExpiredException();
        var body = await resp.Content.ReadAsStringAsync(ct);
        throw new ApiException(ShareText.Sentence(body, resp.StatusCode), resp.StatusCode);
    }
}

/// <summary>Turns what the server said into something a person can act on.</summary>
/// <remarks>Most refusals come back as <c>{ "error": "&lt;sentence&gt;" }</c> and that sentence is
/// shown. Where there is no body (a bare <c>Forbid()</c> in <c>PacksController</c>, for example)
/// the status code is turned into a sentence here.</remarks>
internal static class ShareText
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Explain(Exception ex) => ex switch
    {
        OfflineException offline => offline.Message,
        SessionExpiredException => "Your session has expired - sign in again.",
        ApiException api => FromApi(api),
        OperationCanceledException => "That was stopped before it finished.",
        _ => "Something went wrong. The details are in the launcher log."
    };

    /// <summary>The sentence for one refusal, from its body when there is one.</summary>
    public static string Sentence(string? body, HttpStatusCode status) =>
        FromBody(body) ?? FromStatus(status);

    /// <summary>The 409 body out of an <see cref="ApiException"/> raised inside
    /// <see cref="ApiClient"/>.</summary>
    /// <remarks><see cref="ApiClient"/> formats failures as <c>"409 Conflict: {json}"</c> and keeps
    /// no reference to the response, so the body is parsed back out of the message. Ugly, but it
    /// saves a second manifest round trip.</remarks>
    public static ManifestConflict? ParseConflict(ApiException ex)
    {
        var brace = ex.Message.IndexOf('{');
        if (brace < 0) return null;
        try { return JsonSerializer.Deserialize<ManifestConflict>(ex.Message[brace..], Json); }
        catch { return null; }
    }

    private static string FromApi(ApiException api)
    {
        var message = api.Message ?? "";
        // "<code> <reason>: <body>" is ApiClient's own shape; ShareApi throws the sentence directly.
        var split = message.IndexOf(": ", StringComparison.Ordinal);
        var looksPrefixed = split > 0 && message.Length > 3 && char.IsDigit(message[0]);
        var body = looksPrefixed ? message[(split + 2)..] : message;
        return FromBody(body) ?? (looksPrefixed ? FromStatus(api.Status) : message);
    }

    private static string? FromBody(string? body)
    {
        if (body is not { Length: > 0 }) return null;
        var text = body.Trim();
        if (text.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                foreach (var name in new[] { "error", "detail", "title", "message" })
                    if (doc.RootElement.TryGetProperty(name, out var value)
                        && value.ValueKind == JsonValueKind.String
                        && value.GetString() is { Length: > 0 } sentence)
                        return sentence;
            }
            catch (JsonException) { /* not JSON after all, fall through */ }
            return null;
        }
        // A plain-text body short enough to be a sentence rather than an HTML error page.
        return text.Length is > 0 and < 300 && !text.StartsWith('<') ? text : null;
    }

    private static string FromStatus(HttpStatusCode status) => status switch
    {
        // Worded for every family: the permissions dialog for mods, worlds and resource packs lands
        // here too.
        HttpStatusCode.Forbidden => "The server would not allow that. You may not have permission to "
                                  + "change how this is shared.",
        HttpStatusCode.NotFound => "The server does not know about that any more. Refresh and try again.",
        HttpStatusCode.Conflict => "Somebody changed this on the server first.",
        HttpStatusCode.TooManyRequests => "That is more requests than the server will take just now - "
                                        + "wait a moment and try again.",
        HttpStatusCode.BadRequest => "The server refused that as invalid.",
        _ => $"The server answered {(int)status} and the launcher could not make a sentence of it."
    };
}
