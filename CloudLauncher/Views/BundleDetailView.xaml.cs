using System.IO;
using System.Windows;
using System.Windows.Controls;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>One published version, as the versions card draws it.</summary>
public sealed class BundleVersionRow
{
    public BundleVersionRow(ContentBundleVersionInfo version, bool canDownload, bool canDelete, bool offline)
    {
        Version = version;
        VersionString = version.VersionString;
        Channel = version.ReleaseChannel;
        Changelog = version.Changelog ?? "";
        ChangelogVisibility = Changelog.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        var parts = new List<string>
        {
            ContentBundleService.FormatSize(version.FileSize),
            "published " + (PackListCache.Describe(version.PublishedAt) ?? "")
        };
        if (version.McVersionsCsv is { Length: > 0 } mc) parts.Add(mc);
        Facts = string.Join("  ·  ", parts.Where(p => p.Length > 0));

        CanDownload = canDownload && !offline;
        DownloadHint = !canDownload
            ? "You can see this but not download it."
            : offline ? "Downloading needs a connection to the server." : "Save this version as a zip";

        CanDelete = canDelete && !offline;
        DeleteHint = !canDelete
            ? "Only the owner can delete a published version."
            : offline ? "Deleting a version needs a connection to the server." : "Delete this version";
    }

    public ContentBundleVersionInfo Version { get; }
    public string VersionString { get; }
    public string Channel { get; }
    public string Changelog { get; }
    public Visibility ChangelogVisibility { get; }
    public string Facts { get; }
    public bool CanDownload { get; }
    public string DownloadHint { get; }
    public bool CanDelete { get; }
    public string DeleteHint { get; }

    /// <summary>Which version the install acts on. Two-way from the row's radio button.</summary>
    public bool IsChosen { get; set; }
}

/// <summary>One instance the bundle can be installed into.</summary>
public sealed class BundleTargetRow(PackSummary pack, string note, bool canInstall, string hint)
{
    public PackSummary Pack { get; } = pack;
    public string Name { get; } = pack.Name;
    public string Note { get; } = note;
    public bool CanInstall { get; } = canInstall;
    public string Hint { get; } = hint;
    public bool IsChecked { get; set; }
}

/// <summary>One collaborator or team on the access card.</summary>
public sealed class BundleAccessRow
{
    public required string Glyph { get; init; }
    public required string Name { get; init; }
    public required string Note { get; init; }
    public required string PermissionLabel { get; init; }
    public required bool CanRemove { get; init; }
    public required string RemoveHint { get; init; }

    /// <summary>Set for a person; <see cref="TeamId"/> is set instead for a team.</summary>
    public Guid? UserId { get; init; }
    public Guid? TeamId { get; init; }

    /// <summary>What the row grants now, as the server holds it.</summary>
    public PackPermissions Permissions { get; init; }

    /// <summary>True when the grant can be changed in place: the viewer may manage the bundle and the
    /// server is reachable.</summary>
    public bool CanEdit { get; init; }

    /// <summary>The grants the picker offers: the presets this person may hand out, plus the row's
    /// own value when it is none of them.</summary>
    public IReadOnlyList<BundleGrantChoice> Choices { get; init; } = [];
    public BundleGrantChoice? Selected => Choices.FirstOrDefault(c => c.Value == Permissions);

    public Visibility EditVisibility => CanEdit ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LabelVisibility => CanEdit ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>One grant a person or team on a bundle can be given.</summary>
public sealed record BundleGrantChoice(string Label, PackPermissions Value);

/// <summary>One invitation that has been sent and not yet answered.</summary>
public sealed class BundlePendingRow
{
    public required BundleInvitationEntry Invitation { get; init; }
    public required string Name { get; init; }
    public required string Note { get; init; }
    public required string PermissionLabel { get; init; }
    public required bool CanCopy { get; init; }
    public required string CopyHint { get; init; }
    public required bool CanRevoke { get; init; }
    public required string RevokeHint { get; init; }
}

/// <summary>
/// One content bundle: what it is, what is in it, who may touch it, and installing it into
/// instances. Also the form that creates one.
/// </summary>
/// <remarks>
/// <para>A <see cref="UserControl"/> hosted in the side panel (<c>MainWindow.OpenBundleDetail</c>
/// wraps it in a bare Page); <see cref="Closed"/> pops the panel.</para>
/// <para>Permissions mirror the server: metadata, delete and version-delete are owner-only,
/// publishing needs <see cref="PackPermissions.UploadShared"/>, and access changes need
/// <see cref="PackPermissions.ManageCollaborators"/>. Disabled controls say which one they need.</para>
/// </remarks>
public partial class BundleDetailView : UserControl
{
    private readonly MainWindow _shell;
    private readonly ContentBundleService _bundles;
    private readonly PageState _state;
    private readonly Reentrancy _filling = new();

    // Not readonly: the create form becomes the bundle page once the POST returns, and the
    // permission checks below key off this.
    private bool _isNew;
    private readonly bool _publishOnOpen;
    private Guid _bundleId;
    private BundleKind _kind;
    private ContentBundleDetail? _detail;

    /// <summary>What this is, mid-sentence: "this shader pack", "this config set". "Bundle" is only
    /// the server's term.</summary>
    private string Thing => _kind switch
    {
        BundleKind.ShaderPack => "shader pack",
        BundleKind.ConfigBundle => "config set",
        BundleKind.KubeJsBundle => "script set",
        BundleKind.DataPack => "data pack",
        _ => "upload"
    };

    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _changedAnything;
    private string? _offlineWhy;

    /// <summary>Invitations sent and not yet answered. Only read for managers, since the list carries
    /// tokens and a token is access.</summary>
    private List<BundleInvitationEntry> _pending = [];

    /// <summary>Why <see cref="_pending"/> could not be read, or null.</summary>
    private string? _pendingProblem;

    /// <summary>Raised when the user leaves this page. True means whatever listed it is stale.</summary>
    public event Action<bool>? Closed;

    private BundleDetailView(MainWindow shell, ContentBundleService bundles, BundleKind kind,
                             Guid bundleId, string name, bool isNew, bool publishOnOpen)
    {
        InitializeComponent();
        _shell = shell;
        _bundles = bundles;
        _kind = kind;
        _bundleId = bundleId;
        _isNew = isNew;
        _publishOnOpen = publishOnOpen;

        _state = new PageState(ContentScroller, PageStateHost, nameof(BundleDetailView))
            .Copy(BundleCopy)
            .Slots(null, StatusLabel, BusyBar, BusyCancelButton);
        _state.RetryRequested += () => _ = LoadAsync();
        _state.CancelRequested += () => _cts?.Cancel();

        TitleLabel.Text = isNew ? "New bundle" : name;
        // An existing bundle's kind isn't known until it loads, so leave the chip empty until then.
        KindChipText.Text = isNew ? ContentBundleService.KindLabel(kind) : "";

        using (_filling.Hold())
        {
            foreach (var k in ContentBundleService.Kinds)
                KindBox.Items.Add(new ComboBoxItem { Content = ContentBundleService.KindLabel(k), Tag = k });
            KindBox.SelectedIndex = Math.Max(0, ContentBundleService.Kinds.ToList().IndexOf(kind));

            VisibilityBox.Items.Add(new ComboBoxItem { Content = "Private - only people I add", Tag = PackVisibility.Private });
            VisibilityBox.Items.Add(new ComboBoxItem { Content = "Teams I share it with", Tag = PackVisibility.Team });
            VisibilityBox.Items.Add(new ComboBoxItem { Content = "Public - anyone signed in", Tag = PackVisibility.Public });
            VisibilityBox.SelectedIndex = 0;

            GrantBox.Items.Add(new ComboBoxItem { Content = "Can download", Tag = PackPermissions.ReadOnly });
            GrantBox.Items.Add(new ComboBoxItem { Content = "Can publish versions", Tag = PackPermissions.Contributor });
            GrantBox.Items.Add(new ComboBoxItem { Content = "Full - can also manage sharing", Tag = PackPermissions.Full });
            GrantBox.SelectedIndex = 0;
        }

        if (isNew)
        {
            // Hide the cards that describe an existing bundle until Create returns.
            TargetRootBox.Text = BundleTargets.DefaultFor(kind);
            InstallCard.Visibility = Visibility.Collapsed;
            VersionsCard.Visibility = Visibility.Collapsed;
            AccessCard.Visibility = Visibility.Collapsed;
        }

        foreach (var control in new FrameworkElement[]
                 { PrimaryButton, PublishButton, DeleteBundleButton, ReloadButton, SaveButton,
                   AddPersonButton, AddTeamButton, KindBox })
            ToolTipService.SetShowOnDisabled(control, true);

        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            App.State.ConnectivityChanged -= OnConnectivityChanged;
            _cts?.Cancel();
        };
    }

    /// <summary>The create form. Nothing exists on the server until the user presses Create.</summary>
    public static BundleDetailView ForNew(MainWindow shell, ContentBundleService bundles, BundleKind kind) =>
        new(shell, bundles, kind, Guid.Empty, "", isNew: true, publishOnOpen: false);

    /// <param name="publishOnOpen">Opens the publish dialog as soon as the bundle has loaded, for a
    /// caller whose button said "New version".</param>
    public static BundleDetailView ForExisting(MainWindow shell, ContentBundleService bundles,
                                               Guid bundleId, string name, bool publishOnOpen) =>
        new(shell, bundles, BundleKind.ShaderPack, bundleId, name, isNew: false, publishOnOpen: publishOnOpen);

    private static readonly PageCopy BundleCopy = new()
    {
        Glyph = "",
        FilteredGlyph = "",
        Verb = "Loading",
        Noun = "item",
        LoadingLine = "Reading it from the server.",
        EmptyTitle = "Nothing here",
        EmptyBody = "There is nothing to show here.",
        FilteredTitle = "Nothing matched",
        FilteredBody = "Nothing matched that.",
        ErrorTitle = "Could not open this",
        OfflineTitle = "This needs the server",
        OfflineBody = "The server is not answering ({0}). Its versions and the people on it "
                    + "are only kept server-side, so there is nothing local to show you."
    };

    // ── lifecycle ────────────────────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.State.ConnectivityChanged += OnConnectivityChanged;
        if (_state.Kind != PageStateKind.Idle) return;

        if (_isNew)
        {
            // Don't read the instance list yet: the Install card is hidden, and a late answer could
            // overwrite the list the created bundle asks for.
            ApplyPermissionsToChrome();
            _state.Content(1);
            NameBox.Focus();
        }
        else
        {
            _ = LoadAsync();
        }
    }

    private void OnConnectivityChanged()
    {
        if (!App.State.IsOffline && _offlineWhy is not null && !_isNew) { _ = LoadAsync(); return; }
        RebuildVersionRows();
        RebuildAccessRows();
        RebuildPendingRows();
        ApplyPermissionsToChrome();
    }

    private void OnBack(object sender, RoutedEventArgs e) => Closed?.Invoke(_changedAnything);

    private void OnReload(object sender, RoutedEventArgs e)
    {
        if (!_isNew) _ = LoadAsync();
    }

    // ── loading ──────────────────────────────────────────────────────────────

    private async Task LoadAsync()
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var ct = cts.Token;

        _state.Begin();
        try
        {
            var detail = await _bundles.GetAsync(_bundleId, ct);
            if (ct.IsCancellationRequested) return;

            _detail = detail;
            _kind = detail.Kind;
            _offlineWhy = null;
            FillFrom(detail);
            _state.Content(1);

            await LoadPendingAsync();
            await LoadInstancesAsync();

            if (_publishOnOpen && CanPublish && !App.State.IsOffline) await PublishVersionAsync();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (OfflineException ex)
        {
            _offlineWhy = ex.Reason ?? "the server is unreachable";
            _state.Offline(_offlineWhy);
            ApplyPermissionsToChrome();
        }
        catch (Exception ex)
        {
            _state.Error(ContentBundleService.Explain(ex, "the server refused the request"), ex);
            ApplyPermissionsToChrome();
        }
    }

    private void FillFrom(ContentBundleDetail detail)
    {
        using (_filling.Hold())
        {
            TitleLabel.Text = detail.Name;
            KindChipText.Text = SharingHubService.FamilyLabel(SharedFamily.Bundle, detail.Kind);
            NameBox.Text = detail.Name;
            SummaryBox.Text = detail.Summary ?? "";
            TargetRootBox.Text = detail.TargetPathRoot;
            SelectTag(KindBox, detail.Kind);
            SelectTag(VisibilityBox, detail.Visibility);
        }

        var facts = new List<string> { $"by {detail.OwnerUsername}" };
        if (detail.DownloadCount > 0) facts.Add($"{detail.DownloadCount:N0} download(s)");
        facts.Add(detail.TargetPathRoot.Length == 0
            ? "unpacks into the instance folder"
            : $"unpacks into {detail.TargetPathRoot}/");
        SubLabel.Text = string.Join("  ·  ", facts);

        VersionsCount.Text = detail.Versions.Count == 0
            ? ""
            : detail.Versions.Count == 1 ? "1 version" : $"{detail.Versions.Count:N0} versions";
        NoVersionsLabel.Visibility = detail.Versions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        RebuildVersionRows();
        RebuildAccessRows();
        ApplyPermissionsToChrome();
        SaveButton.IsEnabled = false;
    }

    private void RebuildVersionRows()
    {
        if (_detail is not { } detail) return;
        var chosen = (VersionList.ItemsSource as List<BundleVersionRow>)?.FirstOrDefault(r => r.IsChosen)?.Version.Id;
        var offline = App.State.IsOffline || _offlineWhy is not null;

        var rows = detail.Versions
            .OrderByDescending(v => v.PublishedAt)
            .Select(v => new BundleVersionRow(v, CanDownload, IsOwner, offline)
            {
                IsChosen = chosen is null ? v.Id == detail.Versions.OrderByDescending(x => x.PublishedAt).First().Id
                                          : v.Id == chosen
            })
            .ToList();
        VersionList.ItemsSource = rows;
    }

    private void RebuildAccessRows()
    {
        if (_detail is not { } detail) return;
        var rows = new List<BundleAccessRow>();

        foreach (var person in detail.Collaborators)
            rows.Add(new BundleAccessRow
            {
                Glyph = "",
                Name = person.Username,
                Note = IsMe(person.UserId) ? "you" : "person",
                PermissionLabel = PermissionLabel(person.Permissions),
                CanRemove = CanManage && !App.State.IsOffline,
                RemoveHint = IsMe(person.UserId) && !CanManage
                    ? "That is you. To give your access back, use Leave below."
                    : !CanManage
                        ? $"Only the owner, or somebody with full access, can change who is on this {Thing}."
                        : App.State.IsOffline ? "Changing who has access needs a connection." : "Remove their access",
                UserId = person.UserId,
                Permissions = person.Permissions,
                CanEdit = CanManage && !App.State.IsOffline,
                Choices = ChoicesFor(person.Permissions)
            });

        foreach (var team in detail.Teams)
            rows.Add(new BundleAccessRow
            {
                Glyph = "",
                Name = team.TeamName,
                Note = team.MemberCount > 0 ? $"team of {team.MemberCount}" : "team",
                PermissionLabel = PermissionLabel(team.Permissions),
                CanRemove = CanManage && !App.State.IsOffline,
                RemoveHint = !CanManage
                    ? $"Only the owner, or somebody with full access, can change who is on this {Thing}."
                    : App.State.IsOffline ? "Changing who has access needs a connection." : "Remove this team",
                TeamId = team.TeamId,
                Permissions = team.Permissions,
                CanEdit = CanManage && !App.State.IsOffline,
                Choices = ChoicesFor(team.Permissions)
            });

        AccessList.ItemsSource = rows;
        NoAccessLabel.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool IsMe(Guid userId) => App.State.Settings.UserId is { } me && me == userId;

    /// <summary>The grants the in-place picker offers, least first; the same four as the Add box and
    /// the permissions dialog.</summary>
    private static readonly IReadOnlyList<BundleGrantChoice> GrantChoices =
    [
        new("Can see it", PackPermissions.View),
        new("Can download", PackPermissions.ReadOnly),
        new("Can publish versions", PackPermissions.Contributor),
        new("Full access", PackPermissions.Full),
    ];

    /// <summary>What this person may set a row to: the presets they could hand out themselves, and
    /// the row's current value when it is not one of them.</summary>
    /// <remarks>The server refuses a non-owner manager anything beyond what they hold. The current
    /// value is always included so the picker never opens blank.</remarks>
    private List<BundleGrantChoice> ChoicesFor(PackPermissions current)
    {
        var held = _detail?.EffectivePermissions ?? PackPermissions.None;
        var list = GrantChoices.Where(c => IsOwner || (c.Value & ~held) == 0).ToList();
        if (!list.Any(c => c.Value == current))
            list.Insert(0, new BundleGrantChoice($"Can {PermissionsDialog.Describe(current)}", current));
        return list;
    }

    private void OnAccessGrantChanged(object sender, SelectionChangedEventArgs e)
    {
        // The picker's first selection is the binding filling it in, which is the row's own value.
        if (sender is not ComboBox { DataContext: BundleAccessRow row, SelectedItem: BundleGrantChoice choice }) return;
        if (!row.CanEdit || choice.Value == row.Permissions) return;
        _ = ChangeGrantAsync(row, choice.Value);
    }

    /// <summary>Changes what one person or team may do, in place, without removing their access
    /// first.</summary>
    private async Task ChangeGrantAsync(BundleAccessRow row, PackPermissions to)
    {
        if (_detail is not { } detail) return;
        Busy(true, $"Changing what {row.Name} can do...");
        try
        {
            if (row.UserId is { } userId)
                await _bundles.UpdateCollaboratorAsync(detail.Id, userId, new UpdateCollaboratorRequest(to));
            else if (row.TeamId is { } teamId)
                await _bundles.UpdateTeamAsync(detail.Id, teamId, new UpdatePackTeamRequest(to));
            _changedAnything = true;
            Busy(false, $"{row.Name}: {PermissionLabel(to).ToLowerInvariant()}.");
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Busy(false, null);
            // Put the picker back to what the server still holds before saying why.
            RebuildAccessRows();
            await FailAsync($"What {row.Name} can do could not be changed", ex);
        }
    }

    // ── invitations waiting for an answer ────────────────────────────────────

    /// <summary>Reads the invitations nobody has answered yet, for somebody who may manage them.</summary>
    private async Task LoadPendingAsync()
    {
        _pending = [];
        _pendingProblem = null;
        if (_detail is { } detail && CanManage && !App.State.IsOffline)
        {
            try
            {
                var all = await _bundles.ListInvitationsAsync(detail.Id);
                var now = DateTimeOffset.UtcNow;
                // Named, live invitations only; the share link is in the same table but shown separately.
                _pending = all
                    .Where(i => !i.IsLink && i.AcceptedAt is null && i.RevokedAt is null
                                && (i.ExpiresAt is null || i.ExpiresAt > now))
                    .OrderByDescending(i => i.CreatedAt)
                    .ToList();
            }
            catch (Exception ex)
            {
                AppLog.LogError(nameof(BundleDetailView), ex);
                _pendingProblem = ContentBundleService.Explain(ex, "the server would not list them");
            }
        }
        RebuildPendingRows();
    }

    private void RebuildPendingRows()
    {
        var offline = App.State.IsOffline || _offlineWhy is not null;
        var rows = _pending.Select(i => new BundlePendingRow
        {
            Invitation = i,
            Name = i.InvitedUsername ?? "Somebody",
            Note = $"invited by {i.InvitedByUsername} {PackListCache.Describe(i.CreatedAt) ?? ""}".TrimEnd()
                   + (i.ExpiresAt is { } until ? $" · until {TimeFormat.Date(until)}" : ""),
            PermissionLabel = PermissionLabel(i.Permissions),
            CanCopy = i.Token is { Length: > 0 },
            CopyHint = i.Token is { Length: > 0 }
                ? "Copy their invitation to send it yourself. It only works for them."
                : "The server did not send this invitation's link.",
            CanRevoke = !offline,
            RevokeHint = offline ? "Withdrawing an invitation needs a connection." : "Withdraw this invitation"
        }).ToList();

        PendingList.ItemsSource = rows;
        NoPendingLabel.Text = _pendingProblem is { } why
            ? $"The invitations could not be read - {why}"
            : "No invitation is waiting to be accepted.";
        NoPendingLabel.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCopyInviteLink(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BundlePendingRow { Invitation.Token: { Length: > 0 } token } row)
            return;
        _state.Note(ClipboardHelper.TrySetText(SharingHubService.LinkFor(token))
            ? $"{row.Name}'s invitation link is on your clipboard. Only they can accept it."
            : "The clipboard is busy - try the copy again in a moment.");
    }

    private void OnRevokeInvite(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BundlePendingRow row) _ = RevokeInviteAsync(row);
    }

    private async Task RevokeInviteAsync(BundlePendingRow row)
    {
        if (_detail is not { } detail) return;
        Busy(true, "Withdrawing the invitation...");
        try
        {
            await _bundles.RevokeInvitationAsync(detail.Id, row.Invitation.Id);
            _changedAnything = true;
            Busy(false, $"{row.Name}'s invitation has been withdrawn.");
            await LoadPendingAsync();
        }
        catch (Exception ex)
        {
            Busy(false, null);
            await FailAsync("The invitation could not be withdrawn", ex);
        }
    }

    // ── leaving ──────────────────────────────────────────────────────────────

    /// <summary>True for someone on the bundle's own collaborator list, the only access that can be
    /// given back from here (not team or public access).</summary>
    private bool IsDirectCollaborator =>
        !IsOwner && _detail is { } d && d.Collaborators.Any(c => IsMe(c.UserId));

    private void OnLeave(object sender, RoutedEventArgs e) => _ = LeaveAsync();

    /// <summary>Gives back the access the owner granted, after asking.</summary>
    /// <remarks>The page re-reads the bundle afterwards, because a team or the bundle being public may
    /// still let this person see it; when nothing is left it goes back to the list.</remarks>
    private async Task LeaveAsync()
    {
        if (_detail is not { } detail || App.State.Settings.UserId is not { } me || !IsDirectCollaborator) return;
        if (!await AppDialog.ConfirmAsync(_shell, $"Leave {detail.Name}?",
                $"You lose the access {detail.OwnerUsername} gave you, and only they can give it back. "
                + "Files already installed into your instances stay where they are. If one of your teams "
                + $"also has this {Thing}, you keep what the team gives.",
                "Leave", "Cancel", danger: true))
            return;

        Busy(true, "Leaving...");
        try
        {
            await _bundles.LeaveAsync(detail.Id, me);
            _changedAnything = true;
            AppLog.Log("bundles", $"Left the bundle \"{detail.Name}\".");
        }
        catch (Exception ex)
        {
            Busy(false, null);
            await FailAsync($"You could not leave this {Thing}", ex);
            return;
        }

        try
        {
            await _bundles.GetAsync(detail.Id);
            Busy(false, $"You left {detail.Name}. What is left comes from a team or from it being public.");
            await LoadAsync();
        }
        catch (Exception)
        {
            // Nothing left to show: the bundle was only ever shared with this person directly.
            Closed?.Invoke(true);
        }
    }

    private async Task LoadInstancesAsync()
    {
        try
        {
            var packs = await App.State.Api.ListPacksAsync();
            var root = _detail?.TargetPathRoot ?? TargetRootBox.Text.Trim();
            var rows = packs
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(p => new BundleTargetRow(
                    p,
                    Describe(p, root),
                    canInstall: true,
                    hint: "Files go into this instance, and anything they replace is backed up first."))
                .ToList();
            InstanceList.ItemsSource = rows;
            InstallHint.Text = rows.Count == 0
                ? "You have no instances yet, so there is nowhere to install this."
                : $"Pick a version above, then the instances to write it into. Anything it replaces is "
                  + $"kept as a .bak file beside the original.";
        }
        catch (Exception ex)
        {
            // The instance list has its own cache behind it, so this really is unusual.
            AppLog.LogError(nameof(BundleDetailView), ex);
            InstallHint.Text = "Your instances could not be listed, so there is nothing to install into yet.";
        }
    }

    private static string Describe(PackSummary pack, string root)
    {
        var parts = new List<string>();
        if (pack.MinecraftVersion is { Length: > 0 } mc) parts.Add(mc);
        if (pack.Loader != LoaderKind.None) parts.Add(pack.Loader.ToString());
        parts.Add(root.Length == 0 ? "into the instance folder" : $"into {root}/");
        return string.Join("  ·  ", parts);
    }

    // ── permissions ──────────────────────────────────────────────────────────

    private bool IsOwner => _detail is { } d && App.State.Settings.UserId is { } me && d.OwnerId == me;
    private bool CanManage => IsOwner || (_detail?.EffectivePermissions.HasFlag(PackPermissions.ManageCollaborators) ?? false);
    private bool CanPublish => _detail?.EffectivePermissions.HasFlag(PackPermissions.UploadShared) ?? false;
    private bool CanDownload => _detail?.EffectivePermissions.HasFlag(PackPermissions.Download) ?? false;

    private void ApplyPermissionsToChrome()
    {
        var offline = App.State.IsOffline || _offlineWhy is not null;

        if (_isNew)
        {
            PrimaryButton.Content = "Create bundle";
            PrimaryButton.IsEnabled = !offline && !_busy && NameBox.Text.Trim().Length > 0;
            PrimaryButton.ToolTip = offline
                ? "Creating a bundle writes to the server, which is not answering."
                : NameBox.Text.Trim().Length == 0 ? "Give the bundle a name first." : "Create it on the server";

            PublishButton.IsEnabled = false;
            PublishButton.ToolTip = "Create the bundle first - a version needs something to belong to.";
            DeleteBundleButton.IsEnabled = false;
            DeleteBundleButton.ToolTip = "Nothing has been created yet.";
            ReloadButton.IsEnabled = false;
            ReloadButton.ToolTip = "Nothing to reload yet.";
            SaveButton.Visibility = Visibility.Collapsed;
            KindBox.IsEnabled = true;
            OwnerNote.Text = "";
            PendingPanel.Visibility = Visibility.Collapsed;
            LeavePanel.Visibility = Visibility.Collapsed;
            return;
        }

        var hasVersions = _detail is { Versions.Count: > 0 };
        PrimaryButton.Content = "Install into instances";
        PrimaryButton.IsEnabled = !offline && !_busy && hasVersions && CanDownload;
        PrimaryButton.ToolTip = offline
            ? "Installing downloads it, which needs a connection to the server."
            : !hasVersions ? "Nothing has been published here yet, so there is nothing to install."
            : !CanDownload ? "You can see this but not download it."
            : "Write the chosen version into the ticked instances";

        PublishButton.IsEnabled = !offline && !_busy && CanPublish;
        PublishButton.ToolTip = !CanPublish
            ? "You can see this but not add versions to it."
            : offline ? "Publishing uploads a file, which needs a connection to the server."
            : "Upload a zip, or build one out of an instance's files";

        DeleteBundleButton.IsEnabled = !offline && !_busy && IsOwner;
        DeleteBundleButton.ToolTip = !IsOwner
            ? $"Only the owner can delete this {Thing}."
            : offline ? "Deleting needs a connection to the server." : $"Delete this {Thing} and every version of it";

        ReloadButton.IsEnabled = !_busy;
        ReloadButton.ToolTip = "Read it again from the server";

        KindBox.IsEnabled = false;
        KindBox.ToolTip = "The kind decides where it unpacks and where it is listed, so it is fixed once it "
                        + "exists. Host a new one instead.";

        NameBox.IsEnabled = SummaryBox.IsEnabled = TargetRootBox.IsEnabled = VisibilityBox.IsEnabled = IsOwner;
        SaveButton.Visibility = Visibility.Visible;
        SaveButton.ToolTip = !IsOwner
            ? $"Only the owner can rename this {Thing} or change where it unpacks."
            : offline ? "Saving writes to the server, which is not answering." : "Save these details";

        OwnerNote.Text = IsOwner ? "" : $"Owned by {_detail?.OwnerUsername}. You can see it, not rename it.";

        AddPersonButton.IsEnabled = AddTeamButton.IsEnabled =
            InvitePersonButton.IsEnabled = CanManage && !offline && !_busy;
        var accessHint = !CanManage
            ? $"Only the owner, or somebody with full access, can add people to this {Thing}."
            : offline ? "Adding somebody writes to the server, which is not answering."
            : null;
        AddPersonButton.ToolTip = accessHint ?? "Add somebody by username";
        AddTeamButton.ToolTip = accessHint ?? "Share this with one of your teams";
        InvitePersonButton.ToolTip = accessHint
            ?? "Offer somebody access - they choose whether to take it";

        // Only managers get the token, so only they can copy the link.
        var link = CanManage ? _detail?.ShareToken : null;
        var hasLink = link is { Length: > 0 };
        ShareLinkButton.IsEnabled = CanManage && !_busy && (hasLink || !offline);
        ShareLinkButton.Content = hasLink ? "Copy share link" : "Create a share link";
        ShareLinkButton.ToolTip = !CanManage
            ? $"Only the owner, or somebody with full access, can hand out a link to this {Thing}."
            : hasLink ? "Copy the live link to the clipboard"
            : offline ? "Creating a link writes to the server, which is not answering."
            : "Create a link that grants access to whoever redeems it";
        RevokeShareLinkButton.Visibility = hasLink ? Visibility.Visible : Visibility.Collapsed;
        RevokeShareLinkButton.IsEnabled = hasLink && !offline && !_busy;
        RevokeShareLinkButton.ToolTip =
            "Stop the link working. Anybody who already redeemed it keeps what it gave them.";
        ShareLinkLabel.Text = hasLink ? "A link is live: " + SharingHubService.LinkFor(link!) : "";

        // Pending invitations carry tokens, so only managers see them (the server returns 403 to anyone
        // else).
        PendingPanel.Visibility = CanManage ? Visibility.Visible : Visibility.Collapsed;

        var owner = _detail?.OwnerUsername ?? "The owner";
        LeavePanel.Visibility = IsDirectCollaborator ? Visibility.Visible : Visibility.Collapsed;
        LeaveButton.IsEnabled = !offline && !_busy;
        LeaveButton.ToolTip = offline
            ? "Leaving writes to the server, which is not answering."
            : $"Give back the access {owner} gave you";
        LeaveNote.Text = $"{owner} added you to this {Thing}. Leaving gives that access back.";
    }

    private static string PermissionLabel(PackPermissions permissions) =>
        permissions.HasFlag(PackPermissions.ManageCollaborators) ? "Full access"
        : permissions.HasFlag(PackPermissions.UploadShared) ? "Can publish versions"
        : permissions.HasFlag(PackPermissions.Download) ? "Can download"
        : "Can see it";

    // ── metadata ─────────────────────────────────────────────────────────────

    private void OnFormChanged(object sender, TextChangedEventArgs e)
    {
        if (_filling.Busy) return;
        MarkFormDirty();
    }

    private void OnFormSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling.Busy) return;
        MarkFormDirty();
    }

    private void OnKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling.Busy) return;
        // The default target folder follows the kind while the bundle is still being created; once
        // it exists the kind is fixed and this handler cannot fire.
        if (!_isNew) return;
        var kind = TagOf<BundleKind>(KindBox);
        _kind = kind;
        KindChipText.Text = ContentBundleService.KindLabel(kind);
        using (_filling.Hold()) TargetRootBox.Text = BundleTargets.DefaultFor(kind);
        MarkFormDirty();
    }

    private void MarkFormDirty()
    {
        if (_isNew) { ApplyPermissionsToChrome(); return; }
        SaveButton.IsEnabled = IsOwner && !App.State.IsOffline && !_busy;
    }

    private void OnPrimary(object sender, RoutedEventArgs e)
    {
        if (_isNew) _ = CreateAsync();
        else _ = InstallAsync();
    }

    private async Task CreateAsync()
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) { ShowFormError("Give the bundle a name."); return; }

        var root = TargetRootBox.Text.Trim();
        var normalised = BundleSafePath.NormalizeRoot(_kind, root, out var rootError);
        if (rootError is not null) { ShowFormError($"That folder will not work - {rootError}."); return; }

        ShowFormError(null);
        Busy(true, "Creating the bundle...");
        try
        {
            var created = await _bundles.CreateAsync(new CreateBundleRequest(
                _kind, name,
                SummaryBox.Text.Trim() is { Length: > 0 } s ? s : null,
                null,
                TagOf<PackVisibility>(VisibilityBox),
                normalised));

            _changedAnything = true;
            _bundleId = created.Id;
            AppLog.Log("bundles", $"Created {ContentBundleService.KindLabel(created.Kind).ToLowerInvariant()} \"{created.Name}\".");

            // The create form becomes the bundle page.
            _isNew = false;
            InstallCard.Visibility = Visibility.Visible;
            VersionsCard.Visibility = Visibility.Visible;
            AccessCard.Visibility = Visibility.Visible;
            Busy(false, $"Created {created.Name}.");
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Busy(false, null);
            await FailAsync("The bundle could not be created", ex);
        }
    }

    private async Task OnSaveMetadataAsync()
    {
        if (_detail is not { } detail) return;

        var name = NameBox.Text.Trim();
        if (name.Length == 0) { ShowFormError("A bundle needs a name."); return; }

        var normalised = BundleSafePath.NormalizeRoot(detail.Kind, TargetRootBox.Text.Trim(), out var rootError);
        if (rootError is not null) { ShowFormError($"That folder will not work - {rootError}."); return; }

        ShowFormError(null);
        Busy(true, "Saving...");
        try
        {
            await _bundles.UpdateAsync(detail.Id, new UpdateBundleRequest(
                name,
                SummaryBox.Text.Trim(),
                null,
                TagOf<PackVisibility>(VisibilityBox),
                normalised));
            _changedAnything = true;
            Busy(false, "Saved.");
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Busy(false, null);
            await FailAsync("The changes could not be saved", ex);
        }
    }

    private void OnSaveMetadata(object sender, RoutedEventArgs e) => _ = OnSaveMetadataAsync();

    private void ShowFormError(string? message)
    {
        FormError.Text = message ?? "";
        FormError.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ── versions ─────────────────────────────────────────────────────────────

    private void OnPublishVersion(object sender, RoutedEventArgs e) => _ = PublishVersionAsync();

    private async Task PublishVersionAsync()
    {
        if (_detail is not { } detail) return;

        var suggested = NextVersionString(detail);
        var draft = await UploadBundleVersionDialog.ShowAsync(
            _shell, detail.Kind, detail.TargetPathRoot, suggested, detail.McVersionsCsv);
        if (draft is null) return;

        Busy(true, "Uploading...");
        try
        {
            var size = new FileInfo(draft.ZipPath).Length;
            var progress = new Progress<long>(sent =>
                _state.Progress(size > 0
                    ? $"Uploading {ContentBundleService.FormatSize(sent)} of {ContentBundleService.FormatSize(size)}..."
                    : "Uploading..."));

            var version = await _bundles.PublishVersionAsync(detail.Id, draft.ZipPath, draft.Meta, progress);
            _changedAnything = true;
            AppLog.Log("bundles", $"Published {detail.Name} {version.VersionString} ({ContentBundleService.FormatSize(version.FileSize)}).");
            Busy(false, $"Published {version.VersionString}.");
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Busy(false, null);
            await FailAsync("The version could not be published", ex);
        }
        finally
        {
            if (draft.TemporaryZip)
                try { File.Delete(draft.ZipPath); } catch { /* a temp zip that outlives us is harmless */ }
        }
    }

    private static string NextVersionString(ContentBundleDetail detail)
    {
        var latest = detail.Versions.OrderByDescending(v => v.PublishedAt).FirstOrDefault();
        if (latest is null) return "1.0.0";

        // Bump the last number if the version looks like one; otherwise a date stamp, which is what
        // a bundle of config files usually wants anyway.
        var parts = latest.VersionString.Split('.');
        if (parts.Length >= 2 && int.TryParse(parts[^1], out var tail))
        {
            parts[^1] = (tail + 1).ToString();
            return string.Join('.', parts);
        }
        // Version strings are compared later, so use the invariant calendar; a Buddhist-calendar year
        // would sort above everything.
        return TimeFormat.VersionStampDate(DateTimeOffset.Now);
    }

    private void OnDownloadVersion(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BundleVersionRow row) _ = DownloadAsync(row);
    }

    private async Task DownloadAsync(BundleVersionRow row)
    {
        if (_detail is not { } detail) return;

        // The suggested name comes from the server, so only a plain file name is offered; a path in
        // the name box would save somewhere other than the folder the dialog shows.
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save this version",
            FileName = PathSafety.IsSafeFileName(row.Version.FileName) ? row.Version.FileName : "bundle.zip",
            Filter = "Zip archive (*.zip)|*.zip"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        Busy(true, "Downloading...");
        try
        {
            var progress = new Progress<double>(f => _state.Progress($"Downloading... {f:P0}"));
            var temp = await _bundles.DownloadVersionAsync(detail.Id, row.Version.Id, progress);
            File.Move(temp, dialog.FileName, overwrite: true);
            Busy(false, $"Saved to {dialog.FileName}.");
            AppLog.Log("bundles", $"Saved {detail.Name} {row.VersionString} to {dialog.FileName}.");
        }
        catch (Exception ex)
        {
            Busy(false, null);
            await FailAsync("That version could not be saved", ex);
        }
    }

    private void OnDeleteVersion(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BundleVersionRow row) _ = DeleteVersionAsync(row);
    }

    private async Task DeleteVersionAsync(BundleVersionRow row)
    {
        if (_detail is not { } detail) return;
        if (!await AppDialog.ConfirmAsync(_shell, $"Delete version {row.VersionString}?",
                $"It disappears from {detail.Name} for everybody. Instances that already installed it keep "
                + "the files they were given.", "Delete", "Cancel", danger: true))
            return;

        Busy(true, "Deleting...");
        try
        {
            await _bundles.DeleteVersionAsync(detail.Id, row.Version.Id);
            _changedAnything = true;
            Busy(false, $"Deleted {row.VersionString}.");
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Busy(false, null);
            await FailAsync("That version could not be deleted", ex);
        }
    }

    // ── install ──────────────────────────────────────────────────────────────

    private void OnSelectAllInstances(object sender, RoutedEventArgs e) => SetAllInstances(true);
    private void OnSelectNoInstances(object sender, RoutedEventArgs e) => SetAllInstances(false);

    private void SetAllInstances(bool value)
    {
        if (InstanceList.ItemsSource is not List<BundleTargetRow> rows) return;
        foreach (var row in rows) row.IsChecked = value && row.CanInstall;
        InstanceList.ItemsSource = null;
        InstanceList.ItemsSource = rows;
    }

    private async Task InstallAsync()
    {
        if (_detail is not { } detail) return;

        var version = (VersionList.ItemsSource as List<BundleVersionRow>)?.FirstOrDefault(r => r.IsChosen);
        if (version is null)
        {
            await _shell.ShowMessageAsync("Pick a version",
                "Choose which version to install with the button beside it, then try again.");
            return;
        }

        var targets = (InstanceList.ItemsSource as List<BundleTargetRow>)?
            .Where(r => r.IsChecked).Select(r => r.Pack).ToList() ?? [];
        if (targets.Count == 0)
        {
            await _shell.ShowMessageAsync("Pick at least one instance",
                $"Tick the instances this {Thing} should be written into.");
            return;
        }

        Busy(true, "Installing...");
        try
        {
            var progress = new Progress<string>(line => _state.Progress(line));
            var result = await Task.Run(() => _bundles.InstallAsync(
                detail.Id, detail.Name, detail.Kind, detail.TargetPathRoot,
                version.Version.Id, targets, progress));

            Busy(false, result.Summary());

            if (result.Refused)
            {
                await _shell.ShowMessageAsync("Nothing was installed",
                    "The archive contains a path that would write outside the instance folder, so nothing "
                    + "was written at all.\n\n" + string.Join("\n", result.Rejected.Take(5))
                    + "\n\nThe full list is in the launcher log.");
                return;
            }

            if (result.Failures.Count > 0)
                await _shell.ShowMessageAsync("Installed, with some files skipped",
                    $"{result.FilesWritten} file(s) went in. These did not:\n\n"
                    + string.Join("\n", result.Failures.Take(8)));
        }
        catch (Exception ex)
        {
            Busy(false, null);
            await FailAsync($"The {Thing} could not be installed", ex);
        }
    }

    // ── access ───────────────────────────────────────────────────────────────

    private void OnAddPerson(object sender, RoutedEventArgs e) => _ = AddPersonAsync();

    private async Task AddPersonAsync()
    {
        if (_detail is not { } detail) return;

        var username = await _shell.PromptAsync($"Add somebody to this {Thing}", "Username");
        if (string.IsNullOrWhiteSpace(username)) return;

        Busy(true, "Adding...");
        try
        {
            await _bundles.AddCollaboratorAsync(detail.Id,
                new AddCollaboratorRequest(username.Trim(), TagOf<PackPermissions>(GrantBox)));
            _changedAnything = true;
            Busy(false, $"{username.Trim()} was added.");
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Busy(false, null);
            await SuggestNamesOrFailAsync(username.Trim(), ex);
        }
    }

    private void OnInvitePerson(object sender, RoutedEventArgs e) => _ = InvitePersonAsync();

    /// <summary>
    /// Sends an invitation that waits in the other person's Sharing hub until they accept or decline,
    /// and can be withdrawn until then.
    /// </summary>
    /// <remarks><see cref="AddPersonAsync"/> adds the collaborator immediately instead; both are
    /// offered.</remarks>
    private async Task InvitePersonAsync()
    {
        if (_detail is not { } detail) return;

        var username = await _shell.PromptAsync("Invite somebody to this bundle", "Username");
        if (string.IsNullOrWhiteSpace(username)) return;

        var typed = username.Trim();
        Busy(true, "Sending the invitation...");
        try
        {
            var invite = await _bundles.InviteAsync(detail.Id, typed, TagOf<PackPermissions>(GrantBox));
            _changedAnything = true;
            var who = invite.InvitedUsername ?? typed;
            // It waits in their Sharing page; the link is for sending it yourself as well.
            var copied = invite.Token is { Length: > 0 } && ClipboardHelper.TrySetText(SharingHubService.LinkFor(invite.Token));
            Busy(false, copied
                ? who + " was invited - it waits in their Sharing page, and the link is on your clipboard."
                : who + " was invited.");
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Busy(false, null);
            await SuggestNamesOrFailAsync(typed, ex);
        }
    }

    private void OnShareLink(object sender, RoutedEventArgs e) => _ = ShareLinkAsync();

    /// <summary>Copies the live share link, or mints one when there is not one yet.</summary>
    private async Task ShareLinkAsync()
    {
        if (_detail is not { } detail) return;

        if (detail.ShareToken is { Length: > 0 } existing)
        {
            Busy(false, ClipboardHelper.TrySetText(SharingHubService.LinkFor(existing))
                ? "The share link is on your clipboard."
                : "The clipboard is busy - the link is written beside the button.");
            return;
        }

        Busy(true, "Creating the link...");
        try
        {
            var link = await _bundles.CreateShareLinkAsync(detail.Id, TagOf<PackPermissions>(GrantBox));
            _changedAnything = true;
            Busy(false, ClipboardHelper.TrySetText(SharingHubService.LinkFor(link.Token))
                ? "The share link is on your clipboard."
                : "The link was created.");
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Busy(false, null);
            await FailAsync("The share link could not be created", ex);
        }
    }

    private void OnRevokeShareLink(object sender, RoutedEventArgs e) => _ = RevokeShareLinkAsync();

    private async Task RevokeShareLinkAsync()
    {
        if (_detail is not { } detail) return;

        Busy(true, "Revoking...");
        try
        {
            await _bundles.RevokeShareLinkAsync(detail.Id);
            _changedAnything = true;
            Busy(false, "That link no longer works.");
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Busy(false, null);
            await FailAsync("The share link could not be revoked", ex);
        }
    }

    /// <summary>
    /// On an unknown username, shows similar names that do exist instead of just "User not found".
    /// </summary>
    private async Task SuggestNamesOrFailAsync(string typed, Exception ex)
    {
        try
        {
            var matches = await _bundles.SearchUsersAsync(typed);
            if (matches.Items.Count > 0)
            {
                await _shell.ShowMessageAsync("No account called that",
                    $"Nobody is registered as '{typed}'. These names start the same way:\n\n"
                    + string.Join("\n", matches.Items.Take(8).Select(u => "· " + u.Username)));
                return;
            }
        }
        catch (Exception lookup)
        {
            AppLog.LogError(nameof(BundleDetailView), lookup);
        }
        await FailAsync("They could not be added", ex);
    }

    private void OnAddTeam(object sender, RoutedEventArgs e) => _ = AddTeamAsync(sender as FrameworkElement);

    private async Task AddTeamAsync(FrameworkElement? anchor)
    {
        if (_detail is not { } detail || anchor is null) return;

        List<TeamSummary> teams;
        try { teams = await App.State.Api.ListTeamsAsync(); }
        catch (Exception ex) { await FailAsync("Your teams could not be listed", ex); return; }

        var already = detail.Teams.Select(t => t.TeamId).ToHashSet();
        var choices = teams.Where(t => !already.Contains(t.Id)).ToList();
        if (choices.Count == 0)
        {
            await _shell.ShowMessageAsync("No team to add",
                teams.Count == 0
                    ? "You are not on any team yet. Teams are created on Sharing's \"People & teams\" tab."
                    : "This bundle is already shared with every team you are on.");
            return;
        }

        // A menu rather than a dialog for a short list of the caller's own teams.
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var team in choices)
        {
            var item = new MenuItem { Header = team.Name };
            var picked = team;
            item.Click += (_, _) => _ = ShareWithTeamAsync(picked);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private async Task ShareWithTeamAsync(TeamSummary team)
    {
        if (_detail is not { } detail) return;
        Busy(true, $"Sharing with {team.Name}...");
        try
        {
            await _bundles.AddTeamAsync(detail.Id, new AddPackTeamRequest(team.Id, TagOf<PackPermissions>(GrantBox)));
            _changedAnything = true;
            Busy(false, $"Shared with {team.Name}.");
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Busy(false, null);
            await FailAsync($"{detail.Name} could not be shared with {team.Name}", ex);
        }
    }

    private void OnRemoveAccess(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BundleAccessRow row) _ = RemoveAccessAsync(row);
    }

    private async Task RemoveAccessAsync(BundleAccessRow row)
    {
        if (_detail is not { } detail) return;
        if (!await AppDialog.ConfirmAsync(_shell, $"Remove {row.Name}?",
                $"They lose access to {detail.Name}. Anything they already downloaded stays where it is.",
                "Remove", "Cancel", danger: true))
            return;

        Busy(true, "Removing...");
        try
        {
            if (row.UserId is { } userId) await _bundles.RemoveCollaboratorAsync(detail.Id, userId);
            else if (row.TeamId is { } teamId) await _bundles.RemoveTeamAsync(detail.Id, teamId);
            _changedAnything = true;
            Busy(false, $"{row.Name} was removed.");
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Busy(false, null);
            await FailAsync($"{row.Name} could not be removed", ex);
        }
    }

    // ── delete ───────────────────────────────────────────────────────────────

    private void OnDeleteBundle(object sender, RoutedEventArgs e) => _ = DeleteBundleAsync();

    private async Task DeleteBundleAsync()
    {
        if (_detail is not { } detail) return;
        var versions = detail.Versions.Count;
        if (!await AppDialog.ConfirmAsync(_shell, $"Delete {detail.Name}?",
                versions == 0
                    ? "The bundle page goes away. Nothing has been published to it yet."
                    : $"The bundle and all {versions} published version(s) go away for everybody it is shared "
                      + "with. Files already installed into instances stay where they are.",
                "Delete", "Cancel", danger: true))
            return;

        Busy(true, "Deleting...");
        try
        {
            await _bundles.DeleteAsync(detail.Id);
            AppLog.Log("bundles", $"Deleted bundle \"{detail.Name}\".");
            _changedAnything = true;
            Closed?.Invoke(true);
        }
        catch (Exception ex)
        {
            Busy(false, null);
            await FailAsync("The bundle could not be deleted", ex);
        }
    }

    // ── small helpers ────────────────────────────────────────────────────────

    private void Busy(bool busy, string? done)
    {
        _busy = busy;
        if (busy) _state.Begin(done ?? "Working...", refreshing: true);
        else if (_detail is not null || _isNew) _state.Content(1, note: done);
        ApplyPermissionsToChrome();
    }

    private async Task FailAsync(string what, Exception ex)
    {
        AppLog.LogError(nameof(BundleDetailView), ex);
        if (ex is OperationCanceledException) return;
        var sentence = ContentBundleService.Explain(ex, "the server refused the request");
        _state.Note($"{what} - {sentence}");
        await _shell.ShowMessageAsync(what, sentence);
    }

    private static void SelectTag<T>(ComboBox box, T value) where T : struct, Enum
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
            if (item.Tag is T tag && tag.Equals(value)) { box.SelectedItem = item; return; }
    }

    private static T TagOf<T>(ComboBox box) where T : struct, Enum =>
        (box.SelectedItem as ComboBoxItem)?.Tag is T value ? value : default;
}
