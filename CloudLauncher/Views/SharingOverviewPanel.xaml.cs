using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// The sharing hub's Overview tab: everything shared, both ways (your instances, mods, worlds,
/// resource packs and shader packs that others can reach, and theirs you have been let into), in
/// one list, with pending invitations in either direction on top.
/// </summary>
/// <remarks>
/// <para>A reload rebinds the rows already on screen (<see cref="Canonicalise"/>), so the list diffs
/// instead of resetting and instances keep their sync pill while it is recomputed.</para>
/// <para>Sync pills are filled in after the rows are drawn, since each needs a manifest read and a
/// folder walk. "In sync" only shows when something was actually checked.</para>
/// <para>Offline, rows come from <see cref="SharingHubCache"/> and every action is disabled with the
/// reason in its tooltip.</para>
/// </remarks>
public partial class SharingOverviewPanel : UserControl, INotifyPropertyChanged, ISharingHubTab
{
    /// <summary>How many instances get a sync state per pass. Rows past this are listed without a
    /// pill, so a large library doesn't stall the page reading folders.</summary>
    private const int SyncBudget = 40;

    private enum Direction { Both, Mine, Theirs }

    private enum KindFilter
    {
        Everything, Instances, Mods, Worlds, ResourcePacks, ShaderPacks, ConfigsAndScripts, DataPacks
    }

    private readonly MainWindow _shell;
    private readonly SharingHubService _sharing;
    private readonly PageState _state;
    private readonly Reentrancy _filling = new();

    private readonly ObservableCollection<SharingRow> _rows = [];
    private readonly ObservableCollection<PendingInvite> _incoming = [];
    private readonly ObservableCollection<PendingInvite> _outgoing = [];

    /// <summary>The row object for each shared thing, for the life of the panel.</summary>
    private readonly Dictionary<(SharedFamily, Guid), SharingRow> _canonical = new();

    private CancellationTokenSource? _work;
    private SharingSnapshot? _snapshot;
    private string _query = "";

    public SharingOverviewPanel(MainWindow shell, SharingHubService sharing)
    {
        InitializeComponent();
        _shell = shell;
        _sharing = sharing;

        RowList.ItemsSource = _rows;
        IncomingList.ItemsSource = _incoming;
        OutgoingList.ItemsSource = _outgoing;

        _state = new PageState(ContentScroller, PageStateHost, nameof(SharingOverviewPanel))
            .Copy(OverviewCopy)
            .Slots(SubLabel, StatusLabel, BusyBar);
        _state.RetryRequested += () => _ = LoadAsync(force: true);

        using (_filling.Hold())
        {
            FillCombo(DirectionBox,
            [
                ("Everything shared", Direction.Both),
                ("Shared by you", Direction.Mine),
                ("Shared with you", Direction.Theirs)
            ]);
            FillCombo(KindBox,
            [
                ("Every kind", KindFilter.Everything),
                ("Instances", KindFilter.Instances),
                ("Mods", KindFilter.Mods),
                ("Worlds", KindFilter.Worlds),
                ("Resource packs", KindFilter.ResourcePacks),
                ("Shader packs", KindFilter.ShaderPacks),
                ("Configs & scripts", KindFilter.ConfigsAndScripts),
                ("Data packs", KindFilter.DataPacks)
            ]);
        }

        SearchBox.DebounceMilliseconds = 200; // filters rows already in memory, no request behind it
        SearchBox.TextChangedDebounced += (_, text) => { _query = text ?? ""; Render(); };

        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            App.State.ConnectivityChanged -= OnConnectivityChanged;
            _work?.Cancel();
        };
    }

    /// <summary>The house strings for this tab.</summary>
    private static readonly PageCopy OverviewCopy = new()
    {
        // E8F2 and E721 are the app's existing "shared with me" and magnifier glyphs.
        Glyph = "",
        FilteredGlyph = "",
        Verb = "Loading",
        Noun = "shared thing(s)",
        LoadingLine = "Reading what you share and what is shared with you.",
        EmptyTitle = "Nothing is shared yet",
        EmptyBody = "Share an instance with Share... above, or host a mod, world, resource pack or shader "
                  + "pack from its own page and add people to it. Whatever somebody shares with you "
                  + "shows up here too.",
        FilteredTitle = "Nothing matched",
        FilteredBody = "Everything is still there - the search or the two filters above are hiding it.",
        ErrorTitle = "Could not read your sharing",
        OfflineTitle = "Showing what you last saw",
        OfflineBody = "The server is not answering ({0}). Sharing lives on the server, so this is a "
                    + "read-only picture and nothing here can be changed until it answers."
    };

    // ── offline gating (bound from the row templates) ──

    /// <summary>False while the server is unreachable: every verb on this tab is a live write.</summary>
    public bool CanAct => !App.State.IsOffline;

    /// <summary>
    /// Why a button is disabled. Null while online so enabled buttons get no tooltip.
    /// </summary>
    /// <remarks>Bind it together with <c>ToolTipService.ShowOnDisabled="True"</c>; otherwise WPF
    /// doesn't show tooltips on disabled controls.</remarks>
    public string? ActionTip => App.State.IsOffline
        ? "The server is not answering" +
          (App.State.OfflineReason is { Length: > 0 } r ? " (" + r + ")" : "") +
          ", and sharing changes have to reach it."
        : null;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void OnConnectivityChanged()
    {
        Raise(nameof(CanAct));
        Raise(nameof(ActionTip));
    }

    // ── loading ──

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.State.ConnectivityChanged -= OnConnectivityChanged;
        App.State.ConnectivityChanged += OnConnectivityChanged;
        OnConnectivityChanged();
        if (_state.Kind == PageStateKind.Idle) await LoadAsync(force: false);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F5) { _ = LoadAsync(force: true); e.Handled = true; }
        else if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            SearchBox.Expand();
            SearchBox.Focus();
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    /// <summary>The hub's Refresh button, without losing what is on screen.</summary>
    public Task RefreshAsync() => LoadAsync(force: true);

    /// <summary>Coming back to Sharing: ask again, but say nothing unless it takes a while.</summary>
    public Task ReopenAsync() => LoadAsync(force: true, quiet: true);

    /// <summary>Rebuilds the whole tab. Safe to call while a load is running; the earlier one is
    /// cancelled.</summary>
    /// <param name="force">Ask the server again rather than reuse a snapshot another tab loaded in
    /// the last half minute.</param>
    /// <param name="quiet">A background refresh (the page reopening). Nothing on screen changes unless
    /// it runs past <see cref="PageState.QuietRefreshDelay"/> or the data changed.</param>
    public async Task LoadAsync(bool force, bool quiet = false)
    {
        _work?.Cancel();
        _work = new CancellationTokenSource();
        var ct = _work.Token;

        OnUi(() => _state.Begin("Reading what you share and what is shared with you.",
                                refreshing: null, quiet: quiet));
        try
        {
            var snap = await _sharing.LoadAsync(force, ct);
            if (ct.IsCancellationRequested) return;
            OnUi(() =>
            {
                _snapshot = Canonicalise(snap);
                Render();
            });
            await ApplySyncStatesAsync(ct);
        }
        catch (OperationCanceledException) { /* a newer load is already running */ }
        catch (OfflineException ex)
        {
            OnUi(() => _state.Offline(ex.Reason ?? "the server is unreachable", SharingHubCache.AgeInWords()));
        }
        catch (Exception ex)
        {
            OnUi(() => _state.Error("your sharing could not be read", ex));
        }
    }

    /// <summary>
    /// Runs UI work on the UI thread, whichever thread the continuation came back on.
    /// </summary>
    /// <remarks>
    /// A load started outside a dispatcher callback (the shell's offline Retry, the hub's Refresh) can
    /// resume on the thread pool after an await, where touching a control throws.
    /// </remarks>
    private void OnUi(Action work)
    {
        if (Dispatcher.CheckAccess()) work();
        else Dispatcher.Invoke(work);
    }

    /// <summary>
    /// Swaps each fresh row for the object already standing for that thing, rebound to the fresh
    /// values, and forgets things that are no longer shared.
    /// </summary>
    /// <remarks>The snapshot's lists are rewritten in place, so the service's cached snapshot shares
    /// these row objects.</remarks>
    private SharingSnapshot Canonicalise(SharingSnapshot snap)
    {
        SharingRow Take(SharingRow fresh)
        {
            var key = (fresh.Family, fresh.Id);
            if (_canonical.TryGetValue(key, out var existing) && !ReferenceEquals(existing, fresh))
            {
                existing.Rebind(fresh);
                return existing;
            }
            _canonical[key] = fresh;
            return fresh;
        }

        snap.SharedByMe = snap.SharedByMe.Select(Take).ToList();
        snap.SharedWithMe = snap.SharedWithMe.Select(Take).ToList();

        var live = snap.SharedByMe.Concat(snap.SharedWithMe).Select(r => (r.Family, r.Id)).ToHashSet();
        foreach (var gone in _canonical.Keys.Where(k => !live.Contains(k)).ToList())
            _canonical.Remove(gone);
        return snap;
    }

    // ── rendering ──

    /// <summary>Draws the snapshot through the filters. No network: filters and search only narrow
    /// what the last load returned.</summary>
    private void Render()
    {
        if (_snapshot is not { } snap) return;

        RenderInvitations(snap);

        var direction = Selected<Direction>(DirectionBox);
        var kind = Selected<KindFilter>(KindBox);
        var query = _query.Trim();

        IEnumerable<SharingRow> rows = direction switch
        {
            Direction.Mine => snap.SharedByMe,
            Direction.Theirs => snap.SharedWithMe,
            _ => snap.SharedByMe.Concat(snap.SharedWithMe)
        };
        var all = rows.ToList();
        var visible = all
            .Where(r => Matches(r, kind))
            .Where(r => query.Length == 0 || Matches(r, query))
            .OrderByDescending(r => r.UpdatedAt)
            .ToList();

        ListDiff.Apply(_rows, visible, r => (r.Family, r.Id));

        var total = snap.SharedByMe.Count + snap.SharedWithMe.Count;
        var invitations = _incoming.Count + _outgoing.Count;
        var filtered = visible.Count < all.Count || direction != Direction.Both;

        // With invitations showing, an empty list still shows its note instead of a blank card.
        ListCard.Visibility = visible.Count > 0 || invitations > 0 ? Visibility.Visible : Visibility.Collapsed;
        SetNote(ListNote, visible.Count > 0
            ? null
            : total == 0
                ? "Nothing is shared yet. Share an instance with Share... above, or host a mod, world, resource pack or shader pack from its own page."
                : "Nothing here matches the filters above.");

        if (visible.Count == 0 && invitations == 0 && total > 0) _state.EmptyFiltered();
        _state.Content(visible.Count + invitations,
                       countText: ComposeCount(snap, visible.Count, filtered),
                       note: ComposeNote(snap));

        if (snap.FromCache && visible.Count + invitations > 0)
            _state.Note((ComposeNote(snap) is { Length: > 0 } n ? n + " " : "") + "Read-only until the server answers.");
    }

    private void RenderInvitations(SharingSnapshot snap)
    {
        // Invitations are plain records without change notification, so everything a row shows is in
        // its key; a changed expiry or link makes a new row.
        ListDiff.Apply(_incoming, snap.Incoming, i => (i.Kind, i.Id, i.ExpiresAt, i.Token));
        ListDiff.Apply(_outgoing, snap.Outgoing, i => (i.Kind, i.Id, i.ExpiresAt, i.Token));

        IncomingHeader.Visibility = _incoming.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        OutgoingHeader.Visibility = _outgoing.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        OutgoingHeaderText.Text = $"Sent by you ({_outgoing.Count:N0})";
        OutgoingList.Visibility = _outgoingOpen && _outgoing.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        OutgoingCaret.Text = _outgoingOpen ? "" : "";
        InvitesCard.Visibility = _incoming.Count + _outgoing.Count > 0 || snap.Invites.Unsupported
            ? Visibility.Visible : Visibility.Collapsed;
        InvitesCountLabel.Text = snap.Invites.Ok
            ? $"{_incoming.Count} waiting · {_outgoing.Count} sent"
            : snap.Invites.Problem ?? "";
    }

    private static bool Matches(SharingRow row, KindFilter kind) => kind switch
    {
        KindFilter.Instances => row.Family == SharedFamily.Instance,
        KindFilter.Mods => row.Family == SharedFamily.Mod,
        KindFilter.Worlds => row.Family == SharedFamily.World,
        KindFilter.ResourcePacks => row.Family == SharedFamily.ResourcePack,
        KindFilter.ShaderPacks => row.Family == SharedFamily.Bundle && row.BundleKind == BundleKind.ShaderPack,
        KindFilter.ConfigsAndScripts => row.Family == SharedFamily.Bundle
                                        && row.BundleKind is BundleKind.ConfigBundle or BundleKind.KubeJsBundle,
        KindFilter.DataPacks => row.Family == SharedFamily.Bundle && row.BundleKind == BundleKind.DataPack,
        _ => true
    };

    /// <summary>A search matches the name, owner, kind, where the access came from, and the people
    /// and teams on it, so searching for a person finds what was shared with them.</summary>
    private static bool Matches(SharingRow row, string query) =>
        row.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || row.OwnerUsername.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || row.Kind.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || row.GrantLabel.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || row.Access.Any(a => a.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase));

    private static string ComposeCount(SharingSnapshot snap, int shown, bool filtered)
    {
        var bits = new List<string>();
        if (filtered) bits.Add($"{shown:N0} shown");
        bits.Add($"{snap.SharedByMe.Count:N0} shared by you");
        bits.Add($"{snap.SharedWithMe.Count:N0} with you");
        if (snap.Incoming.Count > 0) bits.Add($"{snap.Incoming.Count:N0} waiting on you");
        return string.Join(" · ", bits);
    }

    /// <summary>
    /// The status line: where the picture came from, then what could not be read.
    /// </summary>
    /// <remarks>
    /// Lists two problems at most, then a count, so the line stays readable.
    /// </remarks>
    private static string? ComposeNote(SharingSnapshot snap)
    {
        var bits = new List<string>();
        if (snap.FromCache)
            bits.Add("Last known picture" + (SharingHubCache.AgeInWords() is { } age ? $" from {age}" : "") + ".");

        var problems = snap.Problems;
        foreach (var problem in problems.Take(2))
            bits.Add(char.ToUpperInvariant(problem[0]) + problem[1..] + ".");
        if (problems.Count > 2) bits.Add($"{problems.Count - 2} more part(s) could not be read.");

        return bits.Count == 0 ? null : string.Join(" ", bits);
    }

    private static void SetNote(TextBlock target, string? text)
    {
        target.Text = text ?? "";
        target.Visibility = text is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── instance sync state ──

    /// <summary>
    /// Fills in each instance row's state pill and one-click verb after the rows are on screen.
    /// </summary>
    /// <remarks>
    /// Three at a time, since each instance costs a manifest call and a folder walk. Rows past
    /// <see cref="SyncBudget"/> get no pill, and hosted things never do (there is no local folder to
    /// compare).
    /// </remarks>
    private async Task ApplySyncStatesAsync(CancellationToken ct)
    {
        if (_snapshot is not { } snap) return;
        var rows = snap.SharedByMe.Concat(snap.SharedWithMe)
                                  .Where(r => r.Family == SharedFamily.Instance)
                                  .ToList();
        var looked = 0;
        var ui = new List<Action>();

        using var gate = new SemaphoreSlim(3);
        var jobs = new List<Task>();

        foreach (var row in rows)
        {
            var known = row;
            if (row.Grant == SharingGrant.Revoked)
            {
                ui.Add(() =>
                {
                    known.Sync = SharingSyncState.AccessRevoked;
                    known.SyncLabel = "Access revoked";
                    known.ActionLabel = "Remove";
                });
                continue;
            }
            if (!row.InLibrary)
            {
                ui.Add(() =>
                {
                    known.Sync = SharingSyncState.NotOnThisPc;
                    known.SyncLabel = "Not in your instances";
                    known.ActionLabel = "Add";
                });
                continue;
            }
            if (!snap.Summaries.TryGetValue(row.Id, out var pack)) continue;
            if (looked++ >= SyncBudget) continue;

            jobs.Add(FillOneAsync(row, pack, gate, ct));
        }

        OnUi(() => { foreach (var write in ui) write(); });

        if (jobs.Count == 0) return;
        try { await Task.WhenAll(jobs); }
        catch (OperationCanceledException) { }
    }

    private async Task FillOneAsync(SharingRow row, PackSummary pack, SemaphoreSlim gate, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var facts = await _sharing.SyncFactsAsync(pack, ct);
            OnUi(() =>
            {
                row.Sync = facts.State;
                row.SyncLabel = facts.Label;
                row.ActionLabel = VerbFor(facts, pack);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Log("sharing", $"Sync state for {pack.Name} could not be worked out: {SharingHubService.Explain(ex)}");
        }
        finally { gate.Release(); }
    }

    /// <summary>The row's one verb, or none, and only one the user's permissions allow. Never "Open",
    /// which every row already has.</summary>
    private static string VerbFor(InstanceSyncFacts facts, PackSummary pack) => facts.State switch
    {
        SharingSyncState.AccessRevoked => "Remove",
        SharingSyncState.UpdateAvailable when pack.EffectivePermissions.HasFlag(PackPermissions.Download) => "Update",
        SharingSyncState.NotOnThisPc when pack.IsShared
            && pack.EffectivePermissions.HasFlag(PackPermissions.Download) => "Download",
        _ => ""
    };

    // ── row actions ──

    private void OnOpenRow(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is SharingRow row) SharingActions.Open(_shell, row);
    }

    private async void OnManageAccess(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not SharingRow row) return;
        try
        {
            if (await SharingActions.ManageAccessAsync(_shell, row, _snapshot))
                await LoadAsync(force: true);
        }
        catch (Exception ex)
        {
            AppLog.LogError("sharing.access", ex);
            _state.Note($"Who can reach '{row.Name}' could not be opened: {SharingHubService.Explain(ex)}");
        }
    }

    private async void OnRowAction(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not SharingRow row) return;
        var snap = _snapshot;
        if (snap is null) return;

        try
        {
            switch (row.ActionLabel)
            {
                case "Remove":
                {
                    var ok = await AppDialog.ConfirmAsync(_shell, "Remove from your instances",
                        $"'{row.Name}' is still listed but {row.OwnerUsername} has withdrawn your access. "
                        + "The server never removes it for you. Removing it here does not delete anything on this PC.",
                        "Remove", "Keep it", danger: true);
                    if (!ok) return;
                    await _sharing.RemoveFromLibraryAsync(row.Id, CancellationToken.None);
                    _shell.RefreshPacks();
                    await LoadAsync(force: true);
                    return;
                }

                case "Add":
                {
                    if (!snap.Summaries.TryGetValue(row.Id, out var summary)) return;
                    _state.Note($"Adding {row.Name} to your instances...");
                    await App.State.ModpackDownload.SubscribeInternalPackAsync(summary, CancellationToken.None);
                    _shell.RefreshPacks();
                    await LoadAsync(force: true);
                    return;
                }

                case "Update":
                case "Download":
                {
                    _state.Note($"Downloading the latest files for {row.Name}...");
                    var progress = new Progress<string>(line => _state.Note(line));
                    await App.State.Packs.DownloadSharedAsync(row.Id, progress, CancellationToken.None);
                    _sharing.InvalidateSync(row.Id);
                    _state.Note($"{row.Name} is up to date.");
                    if (snap.Summaries.TryGetValue(row.Id, out var pack))
                    {
                        var facts = await _sharing.SyncFactsAsync(pack, CancellationToken.None);
                        OnUi(() =>
                        {
                            row.Sync = facts.State;
                            row.SyncLabel = facts.Label;
                            row.ActionLabel = VerbFor(facts, pack);
                        });
                    }
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.LogError("sharing.rowaction", ex);
            _state.Error($"'{row.Name}' could not be updated: {SharingHubService.Explain(ex)}");
        }
    }

    // ── invitations ──

    /// <summary>Whether "Sent by you" is expanded. Kept for the life of the tab so a reload doesn't
    /// collapse it.</summary>
    private bool _outgoingOpen;

    private void OnToggleOutgoing(object sender, RoutedEventArgs e)
    {
        _outgoingOpen = !_outgoingOpen;
        if (_snapshot is { } snap) RenderInvitations(snap);
    }

    private async void OnAcceptInvite(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not PendingInvite invite) return;
        try
        {
            _state.Note($"Accepting {invite.SubjectName}...");
            await _sharing.AcceptInvitationAsync(invite, CancellationToken.None);
            _shell.RefreshPacks();
            if (invite.Kind == InviteKind.Team) await _shell.RefreshPackTeamFoldersAsync();
            await LoadAsync(force: true);
        }
        catch (Exception ex)
        {
            AppLog.LogError("sharing.accept", ex);
            _state.Error($"That invitation could not be accepted: {SharingHubService.Explain(ex)}");
        }
    }

    private async void OnDeclineInvite(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not PendingInvite invite) return;

        var ok = await AppDialog.ConfirmAsync(_shell, "Decline this invitation",
            $"{invite.InvitedByUsername} invited you to '{invite.SubjectName}'. Declining is final for "
            + "this invitation - they can send another one.",
            "Decline", "Cancel");
        if (!ok) return;

        try
        {
            await _sharing.DeclineInvitationAsync(invite, CancellationToken.None);
            await LoadAsync(force: true);
        }
        catch (Exception ex)
        {
            AppLog.LogError("sharing.decline", ex);
            _state.Error($"That invitation could not be declined: {SharingHubService.Explain(ex)}");
        }
    }

    private async void OnRevokeInvite(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not PendingInvite invite) return;

        var who = invite.IsLink ? "anybody holding the link" : invite.InvitedUsername;
        var ok = await AppDialog.ConfirmAsync(_shell, "Revoke this invitation",
            $"{who} will no longer be able to use it to reach '{invite.SubjectName}'. "
            + "Anybody who already accepted keeps the access they were given.",
            "Revoke", "Cancel", danger: true);
        if (!ok) return;

        try
        {
            await _sharing.RevokeInvitationAsync(invite, CancellationToken.None);
            await LoadAsync(force: true);
        }
        catch (Exception ex)
        {
            AppLog.LogError("sharing.revoke", ex);
            _state.Error($"That invitation could not be revoked: {SharingHubService.Explain(ex)}");
        }
    }

    private void OnCopyInviteLink(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not PendingInvite invite) return;
        if (invite.Token is not { Length: > 0 } token)
        {
            _state.Note("That invitation has no link to copy - it was addressed to somebody by name.");
            return;
        }
        var url = _sharing.ShareLinkUrl(token);
        _state.Note(ClipboardHelper.TrySetText(url)
            ? "Invitation link copied. Whoever gets it pastes it into Sharing > Redeem a link."
            : "The clipboard is busy - the link could not be copied.");
    }

    // ── filters ──

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling.Busy) return;
        Render();
    }

    private static void FillCombo<T>(ComboBox box, (string Label, T Value)[] items) where T : Enum
    {
        box.Items.Clear();
        foreach (var (label, value) in items)
            box.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        box.SelectedIndex = 0;
    }

    private static T Selected<T>(ComboBox box) where T : struct, Enum =>
        (box.SelectedItem as ComboBoxItem)?.Tag is T value ? value : default;
}
