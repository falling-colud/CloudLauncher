using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// Teams, end to end: create one, invite people, decide what each of them may do, and see
/// everything the team can reach.
/// </summary>
/// <remarks>
/// <para>Hosted twice: <see cref="TeamsView"/> wraps it as the Teams side panel and
/// <c>SharingHubView</c> puts it in the "People &amp; teams" tab.</para>
/// <para>Actions the viewer's role forbids are hidden. Actions that need the server stay visible
/// but are disabled offline, with the reason on them.</para>
/// <para>Filtering is local and never refetches; the open team stays open even when the filter
/// hides its row.</para>
/// </remarks>
public partial class SharingPeoplePanel : UserControl
{
    private static readonly PageCopy TeamsCopy = new()
    {
        Glyph = "",
        FilteredGlyph = "",
        Verb = "Loading",
        Noun = "team(s)",
        LoadingLine = "Asking the server which teams you are on.",
        EmptyTitle = "You are not on any team yet",
        EmptyBody = "A team is a group you can share instances, mods, worlds and packs with in one "
                  + "go. Make one, or join with a code somebody sent you.",
        FilteredTitle = "Nothing matched",
        FilteredBody = "Every team is still there - the filter is hiding them. Clear it to see them all.",
        ErrorTitle = "Could not load your teams",
        OfflineTitle = "Showing your last known teams",
        OfflineBody = "The server is not answering ({0}). You can read the members and copy team IDs; "
                    + "anything that changes a team needs the connection back."
    };

    private readonly MainWindow _shell;
    private readonly TeamsApi _teams;
    private readonly PageState _state;

    private readonly ObservableCollection<TeamRow> _rows = new();
    private readonly ObservableCollection<TeamMemberRow> _memberRows = new();
    private readonly ObservableCollection<TeamInviteRow> _inviteRows = new();
    private readonly ObservableCollection<TeamSharedRow> _sharedRows = new();
    private readonly ObservableCollection<MyInviteRow> _myInviteRows = new();

    /// <summary>Every team the server (or the cache) gave us, unfiltered.</summary>
    private List<TeamSummary> _all = new();

    private TeamDetail? _selected;
    private TeamRole _myRole = TeamRole.Member;

    /// <summary>Guards the list's selection handler while it is being rebuilt, so restoring the
    /// previous selection after a filter or a refresh does not fetch the team again.</summary>
    private bool _suppressSelection;

    private bool _offline;
    private string _offlineReason = "the server is not answering";
    private bool _wideLayout = true;
    private int _sort;
    private CancellationTokenSource? _detailCts;

    /// <summary>Tooltips as the markup declared them, so a control that has been disabled with a
    /// reason can be given its own words back when the connection returns.</summary>
    private readonly Dictionary<FrameworkElement, object?> _ownTips = new();

    /// <summary>
    /// Set by a host that draws its own primary "New team" button, so the toolbar does not show a
    /// second one. Set it before the control is loaded.
    /// </summary>
    public bool HostProvidesPrimaryAction { get; set; }

    /// <summary>
    /// Raised whenever the host's own primary button should be switched on or off, with the reason
    /// to put on it while it is off (null while it is on).
    /// </summary>
    /// <remarks>Lets the host's accent button go grey offline along with the panel's own buttons.</remarks>
    public event Action<bool, string?>? PrimaryActionAvailabilityChanged;

    public SharingPeoplePanel(MainWindow shell)
    {
        InitializeComponent();
        _shell = shell;
        _teams = new TeamsApi(App.State.Settings, App.State.Api);

        TeamsList.ItemsSource = _rows;
        MembersList.ItemsSource = _memberRows;
        InvitesList.ItemsSource = _inviteRows;
        SharedList.ItemsSource = _sharedRows;
        MyInvitesList.ItemsSource = _myInviteRows;

        _state = new PageState(TeamsList, TeamsStateHost, nameof(SharingPeoplePanel))
            .Copy(TeamsCopy)
            .Slots(CountLabel, StatusLabel, BusyBar)
            .DisableWhileBusy(RefreshButton, SortButton);
        _state.RetryRequested += () => _ = LoadAsync();

        // Filters a list already in memory, so a short debounce is enough.
        SearchBox.DebounceMilliseconds = 200;
        SearchBox.TextChangedDebounced += (_, _) => ApplyFilter(SelectedId);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private Guid? SelectedId => (TeamsList.SelectedItem as TeamRow)?.Id ?? _selected?.Id;

    private Guid Me => App.State.Settings.UserId ?? Guid.Empty;

    // ── lifetime ──

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        NewTeamButton.Visibility = HostProvidesPrimaryAction ? Visibility.Collapsed : Visibility.Visible;

        if (Window.GetWindow(this) is { } window)
        {
            window.PreviewKeyDown -= OnShellKeyDown;
            window.PreviewKeyDown += OnShellKeyDown;
        }
        App.State.ConnectivityChanged -= OnConnectivityChanged;
        App.State.ConnectivityChanged += OnConnectivityChanged;

        ApplyLayout(force: true);
        await LoadAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window) window.PreviewKeyDown -= OnShellKeyDown;
        App.State.ConnectivityChanged -= OnConnectivityChanged;
        _detailCts?.Cancel();
    }

    private void OnConnectivityChanged()
    {
        // Coming back online reloads; going offline only redraws the gates, since the rows on screen
        // are still valid.
        if (!App.State.IsOffline && _offline)
        {
            _ = LoadAsync();
            return;
        }

        // Another request noticed we're offline first; trust it rather than waiting for one of ours
        // to fail.
        if (App.State.IsOffline && !_offline)
        {
            _offline = true;
            _offlineReason = App.State.OfflineReason ?? "the server is not answering";
        }
        ApplyGates();
    }

    /// <summary>Ctrl+F, F5 and F2, matching every other page.</summary>
    private void OnShellKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsVisible) return;
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            SearchBox.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            _ = LoadAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.F2 && TeamsList.IsKeyboardFocusWithin
                 && TeamsList.SelectedItem is TeamRow row && row.CanRename)
        {
            _ = RenameAsync(row.Id, row.Name);
            e.Handled = true;
        }
    }

    // ── layout ──

    private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout(force: false);

    /// <summary>
    /// Side by side when there is room, stacked when there is not.
    /// </summary>
    /// <remarks>The Teams side panel is a few hundred pixels wide; the sharing hub's tab is most of
    /// the window.</remarks>
    private void ApplyLayout(bool force)
    {
        var wide = ActualWidth >= 820;
        if (!force && wide == _wideLayout) return;
        _wideLayout = wide;

        if (wide)
        {
            ListColumn.Width = new GridLength(340);
            DetailColumn.Width = new GridLength(1, GridUnitType.Star);
            ListRow.Height = new GridLength(1, GridUnitType.Star);
            DetailRow.Height = new GridLength(0);

            Grid.SetColumn(ListPane, 0);
            Grid.SetRow(ListPane, 0);
            Grid.SetRowSpan(ListPane, 2);
            ListPane.Margin = new Thickness(0, 0, 12, 0);
            ListPane.MaxHeight = double.PositiveInfinity;

            Grid.SetColumn(DetailPane, 1);
            Grid.SetRow(DetailPane, 0);
            Grid.SetRowSpan(DetailPane, 2);
        }
        else
        {
            ListColumn.Width = new GridLength(1, GridUnitType.Star);
            DetailColumn.Width = new GridLength(0);
            ListRow.Height = GridLength.Auto;
            DetailRow.Height = new GridLength(1, GridUnitType.Star);

            Grid.SetColumn(ListPane, 0);
            Grid.SetRow(ListPane, 0);
            Grid.SetRowSpan(ListPane, 1);
            ListPane.Margin = new Thickness(0, 0, 0, 12);
            // Auto-height would let twelve teams push the detail off the bottom of the panel.
            ListPane.MaxHeight = 230;

            Grid.SetColumn(DetailPane, 0);
            Grid.SetRow(DetailPane, 1);
            Grid.SetRowSpan(DetailPane, 1);
        }
    }

    // ── loading the list ──

    /// <summary>Re-reads the team list. The host calls this to refresh the tab.</summary>
    public Task ReloadAsync() => LoadAsync();

    private async Task LoadAsync(Guid? selectId = null)
    {
        var keep = selectId ?? SelectedId;
        _state.Begin();
        try
        {
            var teams = await App.State.Api.ListTeamsAsync();
            _all = teams;
            TeamListCache.SaveTeams(teams);
            _offline = false;
            StaleBanner.Visibility = Visibility.Collapsed;

            ApplyFilter(keep);
            PaintState();
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            _offline = true;
            _offlineReason = ReasonFor(ex);
            var cached = TeamListCache.LoadTeams();
            _all = cached ?? new List<TeamSummary>();
            ApplyFilter(keep);

            var age = TeamListCache.AgeInWords();
            StaleText.Text = age is null
                ? $"Offline - {_offlineReason}. Teams live on the server, so nothing here can be changed until it answers again."
                : $"Offline - showing your teams as of {age}. Membership, invitations and sharing can't be changed until the server answers again.";
            StaleBanner.Visibility = Visibility.Visible;
            PaintState();
            AppLog.Log("teams", $"Team list unavailable ({_offlineReason}); showing the last known {_all.Count}.");
        }
        catch (Exception ex)
        {
            _state.Error(TeamsApi.Explain(ex, "the server would not answer for them."), ex);
        }

        ApplyGates();
        await AfterListAsync(keep);
    }

    /// <summary>Picks a team to show and refreshes the "waiting for you" strip.</summary>
    private async Task AfterListAsync(Guid? keep)
    {
        if (_rows.Count > 0 && TeamsList.SelectedItem is null)
        {
            var pick = keep is { } id ? _rows.FirstOrDefault(r => r.Id == id) : null;
            _suppressSelection = true;
            try { TeamsList.SelectedItem = pick ?? _rows[0]; }
            finally { _suppressSelection = false; }
            if (TeamsList.SelectedItem is TeamRow row) await SelectAsync(row.Id);
        }
        else if (_rows.Count == 0)
        {
            ApplyDetail(null, null);
        }

        await LoadMyInvitesAsync();
    }

    /// <summary>
    /// Writes the count slot, the status line and the overlay for whatever is true right now.
    /// </summary>
    /// <remarks>One method so the count and the state are always written together, and filtering
    /// offline can't leave a stale count.</remarks>
    private void PaintState()
    {
        if (!_offline) { ReportCount(); return; }

        // Content first so PageState knows there is data; Offline then keeps the rows and adds a note
        // instead of covering them.
        var age = TeamListCache.AgeInWords();
        if (_rows.Count > 0) _state.Content(_rows.Count, "team(s)");
        _state.Offline(_offlineReason, age);

        // The banner already explains the state, so the status row only adds how old the list is.
        _state.Note(age is null
            ? "Nothing has been cached from this server yet, so there is nothing to show."
            : $"Last answer from the server: {age}.");
    }

    private void ReportCount()
    {
        var hidden = _all.Count - _rows.Count;
        if (_rows.Count == 0 && _all.Count > 0) _state.EmptyFiltered();
        _state.Content(_rows.Count, "team(s)",
            note: hidden > 0 ? $"{hidden:N0} hidden by the filter." : null);
    }

    /// <summary>Rebuilds the visible rows from the filter box and the sort choice. It does not
    /// fetch anything, and it does not close the team that is open.</summary>
    private void ApplyFilter(Guid? selectId)
    {
        var q = SearchBox.Text?.Trim();
        IEnumerable<TeamSummary> teams = _all;
        if (!string.IsNullOrEmpty(q))
            teams = teams.Where(t =>
                t.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || t.OwnerUsername.Contains(q, StringComparison.OrdinalIgnoreCase));

        teams = _sort switch
        {
            1 => teams.OrderByDescending(t => t.MemberCount).ThenBy(t => t.Name),
            2 => teams.OrderByDescending(TeamRow.SharedTotal).ThenBy(t => t.Name),
            3 => teams.OrderByDescending(t => t.OwnerId == Me).ThenBy(t => t.Name),
            _ => teams.OrderBy(t => t.Name)
        };

        _suppressSelection = true;
        try
        {
            _rows.Clear();
            foreach (var t in teams) _rows.Add(new TeamRow(t, Me));
            TeamsList.SelectedItem = selectId is { } id ? _rows.FirstOrDefault(r => r.Id == id) : null;
        }
        finally { _suppressSelection = false; }

        // Only once a load has finished, or the page would say "0 teams" while the request is in flight.
        if (_state.Kind is PageStateKind.Content or PageStateKind.Empty or PageStateKind.Offline)
            PaintState();
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = LoadAsync();

    private void OnSortMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is not { } menu) return;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private void OnSortPicked(object sender, RoutedEventArgs e)
    {
        _sort = sender switch
        {
            var s when ReferenceEquals(s, SortByMembers) => 1,
            var s when ReferenceEquals(s, SortByShared) => 2,
            var s when ReferenceEquals(s, SortByRole) => 3,
            _ => 0
        };
        SortByName.IsChecked = _sort == 0;
        SortByMembers.IsChecked = _sort == 1;
        SortByShared.IsChecked = _sort == 2;
        SortByRole.IsChecked = _sort == 3;
        ApplyFilter(SelectedId);
    }

    // ── one team ──

    private async void OnTeamSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        if (TeamsList.SelectedItem is TeamRow row) await SelectAsync(row.Id);
    }

    private async Task SelectAsync(Guid teamId)
    {
        _detailCts?.Cancel();
        var cts = new CancellationTokenSource();
        _detailCts = cts;

        ShowDetailLoading();

        TeamDetail? detail;
        var fromCache = false;
        try
        {
            detail = await App.State.Api.GetTeamAsync(teamId, cts.Token);
            TeamListCache.SaveDetail(detail);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            detail = TeamListCache.LoadDetail(teamId);
            fromCache = detail is not null;
            if (IsTransport(ex))
            {
                _offline = true;
                _offlineReason = ReasonFor(ex);
            }
            else if (!fromCache)
            {
                ApplyDetail(null, TeamsApi.Explain(ex, "that team could not be opened."));
                return;
            }
        }

        if (cts.IsCancellationRequested) return;

        // Say why the detail is blank rather than leave the previous team's status line up.
        if (detail is null)
        {
            ApplyDetail(null, _offline
                ? $"Offline - {_offlineReason}. This team's members were never read on this PC, so there is nothing cached to show."
                : "That team could not be opened.");
            return;
        }

        ApplyDetail(detail, fromCache ? "Showing the last copy of this team from this PC." : null);

        if (detail is null || fromCache || _offline) return;
        await LoadInvitesAsync(detail, cts.Token);
        await LoadSharedAsync(detail, cts.Token);
    }

    private void ShowDetailLoading()
    {
        NoSelectionPanel.Visibility = Visibility.Collapsed;
        DetailStack.Visibility = Visibility.Visible;
        _memberRows.Clear();
        _inviteRows.Clear();
        _sharedRows.Clear();
        Note(MembersNote, "Loading the member list...");
        Note(SharedNote, "Loading what this team can reach...");
        Note(InvitesNote, null);
        MembersCount.Text = "";
        SharedCount.Text = "";
    }

    private void ApplyDetail(TeamDetail? detail, string? note)
    {
        _selected = detail;
        _memberRows.Clear();

        if (detail is null)
        {
            DetailStack.Visibility = Visibility.Collapsed;
            NoSelectionPanel.Visibility = Visibility.Visible;
            if (note is { Length: > 0 }) Fail(note);
            return;
        }

        NoSelectionPanel.Visibility = Visibility.Collapsed;
        DetailStack.Visibility = Visibility.Visible;

        var isOwner = detail.OwnerId == Me;
        // Team.OwnerId stays the source of truth: a cached detail from before a transfer would
        // otherwise let the old owner see owner-only actions that the server will refuse.
        _myRole = isOwner ? TeamRole.Owner
                : detail.MyRole == TeamRole.Owner ? TeamRole.Admin
                : detail.MyRole;

        var entries = detail.MemberDetails is { Count: > 0 }
            ? detail.MemberDetails
            : detail.Members
                .Select(m => new TeamMemberEntry(m.Id, m.Username, m.EmailConfirmed, detail.CreatedAt,
                                                 m.Id == detail.OwnerId,
                                                 m.Id == detail.OwnerId ? TeamRole.Owner : TeamRole.Member))
                .ToList();

        foreach (var entry in entries)
            _memberRows.Add(new TeamMemberRow(entry, _myRole, entry.UserId == Me, _offline, _offlineReason));

        DetailColour.Background = TeamColour(detail.Id);
        DetailName.Text = detail.Name;
        DetailRolePill.Text = RoleWord(_myRole);
        DetailSub.Text = DescribeTeam(detail, isOwner);
        MembersCount.Text = Plural(entries.Count, "member", "members");
        Note(MembersNote, entries.Count == 0
            ? "Nobody is on this team yet - invite somebody with the button above."
            : null);

        InvitesCard.Visibility = _myRole is TeamRole.Owner or TeamRole.Admin
            ? Visibility.Visible : Visibility.Collapsed;

        ApplyGates();
        if (note is { Length: > 0 }) Okay(note);
    }

    /// <summary>
    /// Who may see what, on two independent axes.
    /// </summary>
    /// <remarks>Role hides, connectivity disables. A role won't change, but being offline is temporary
    /// and hiding buttons would reshape the screen on every network blip.</remarks>
    private void ApplyGates()
    {
        var owner = _myRole == TeamRole.Owner;
        var manager = _myRole is TeamRole.Owner or TeamRole.Admin;
        var have = _selected is not null;

        RenameButton.Visibility = Vis(have && owner);
        DeleteButton.Visibility = Vis(have && owner);
        TransferButton.Visibility = Vis(have && owner);
        InviteButton.Visibility = Vis(have && manager);
        LeaveButton.Visibility = Vis(have && !owner);
        CopyIdButton.Visibility = Vis(have);

        var reason = OfflineTip(_offlineReason);
        Gate(NewTeamButton, !_offline, reason);
        Gate(JoinButton, !_offline, reason);
        Gate(InviteButton, !_offline, reason);
        Gate(TransferButton, !_offline, reason);
        Gate(RenameButton, !_offline, reason);
        Gate(LeaveButton, !_offline, reason);
        Gate(DeleteButton, !_offline, reason);
        Gate(NewLinkButton, !_offline, reason);

        // The row view-models carry the same gate, so rebuild them when it flips.
        foreach (var row in _memberRows.ToList()) row.SetOnline(!_offline, _offlineReason);
        foreach (var row in _inviteRows.ToList()) row.SetOnline(!_offline, _offlineReason);
        foreach (var row in _sharedRows.ToList()) row.SetOnline(!_offline, _offlineReason);
        RebindRows();

        PrimaryActionAvailabilityChanged?.Invoke(!_offline, _offline ? reason : null);
    }

    /// <summary>Rebinds the three row lists after their gates changed. The rows don't implement
    /// INotifyPropertyChanged, so the lists have to be rebound.</summary>
    private void RebindRows()
    {
        MembersList.Items.Refresh();
        InvitesList.Items.Refresh();
        SharedList.Items.Refresh();
    }

    private static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The one sentence every gated control says, so the reason reads the same everywhere.</summary>
    internal static string OfflineTip(string reason) => $"Offline - {reason}. This needs the server.";

    /// <summary>
    /// Enables or disables a control, leaving a reason on it when it is off.
    /// </summary>
    /// <remarks>Uses <see cref="ToolTipService.SetShowOnDisabled"/>, since WPF hides tooltips on
    /// disabled controls by default.</remarks>
    private void Gate(FrameworkElement element, bool enabled, string reason)
    {
        if (!_ownTips.ContainsKey(element)) _ownTips[element] = element.ToolTip;
        ToolTipService.SetShowOnDisabled(element, true);
        element.IsEnabled = enabled;
        element.ToolTip = enabled ? _ownTips[element] : reason;
    }

    private static string DescribeTeam(TeamDetail d, bool isOwner)
    {
        var shared = SharedParts(d.SharedPackCount, d.SharedModCount, d.SharedWorldCount,
                                 d.SharedResourcePackCount, d.SharedBundleCount);
        var whose = isOwner ? "Yours" : $"Owned by {d.OwnerUsername}";
        var created = $"created {TimeFormat.Date(d.CreatedAt)}";
        return shared.Count == 0
            ? $"{whose} · {created} · nothing is shared with it yet"
            : $"{whose} · {created} · shares {string.Join(", ", shared)}";
    }

    // ── invitations this team has sent ──

    private async Task LoadInvitesAsync(TeamDetail detail, CancellationToken ct)
    {
        if (_myRole is not (TeamRole.Owner or TeamRole.Admin)) return;
        Note(InvitesNote, "Loading invitations...");
        try
        {
            var invites = await _teams.ListInvitationsAsync(detail.Id, ct);
            if (ct.IsCancellationRequested) return;
            _inviteRows.Clear();
            // Pending first: they are the only ones anybody can still act on.
            foreach (var entry in invites.OrderBy(i => InviteState(i) != "Pending").ThenByDescending(i => i.CreatedAt))
                _inviteRows.Add(new TeamInviteRow(entry, !_offline, _offlineReason));
            Note(InvitesNote, _inviteRows.Count == 0
                ? "Nobody has been invited yet. 'Invite people' sends one by username, or mint a code anybody can use."
                : null);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            AppLog.LogError("teams", ex);
            Note(InvitesNote, "The invitation list could not be read. " + TeamsApi.Explain(ex, "The server refused it."));
        }
    }

    // ── what the team can reach ──

    private async Task LoadSharedAsync(TeamDetail detail, CancellationToken ct)
    {
        Note(SharedNote, "Loading what this team can reach...");
        try
        {
            var shared = await _teams.GetSharedAsync(detail.Id, ct);
            if (ct.IsCancellationRequested) return;
            _sharedRows.Clear();
            foreach (var item in shared.Items)
                _sharedRows.Add(new TeamSharedRow(item, !_offline, _offlineReason));
            SharedCount.Text = _sharedRows.Count == 0 ? "" : Plural(_sharedRows.Count, "item", "items");
            Note(SharedNote, _sharedRows.Count == 0
                ? "Nothing has been shared with this team yet. Share an instance, mod, world or pack with it from that thing's own sharing panel."
                : null);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            AppLog.LogError("teams", ex);
            SharedCount.Text = "";
            Note(SharedNote, "This team's shared list could not be read. " + TeamsApi.Explain(ex, "The server refused it."));
        }
    }

    // ── invitations waiting for me ──

    private async Task LoadMyInvitesAsync()
    {
        if (_offline)
        {
            MyInvitesCard.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            var mine = await _teams.ListMyInvitationsAsync();
            _myInviteRows.Clear();
            foreach (var entry in mine.OrderByDescending(i => i.CreatedAt))
                _myInviteRows.Add(new MyInviteRow(entry));
            MyInvitesHeadline.Text = _myInviteRows.Count == 1
                ? "One team is waiting for your answer"
                : $"{_myInviteRows.Count} teams are waiting for your answer";
            MyInvitesCard.Visibility = _myInviteRows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception ex)
        {
            // Not worth an error banner: the rest of the page is fine and the invitation will still be
            // there.
            AppLog.LogError("teams", ex);
            MyInvitesCard.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnAcceptMyInvite(object sender, RoutedEventArgs e)
    {
        if (RowFromSender<MyInviteRow>(sender) is not { } row) return;
        try
        {
            var joined = await _teams.AcceptAsync(row.Token);
            await LoadAsync(joined.TeamId);
            await _shell.RefreshPackTeamFoldersAsync();
            Okay($"You joined {row.TeamName}.");
        }
        catch (Exception ex) { await ReportAsync("Could not join that team", ex); }
    }

    private async void OnDeclineMyInvite(object sender, RoutedEventArgs e)
    {
        if (RowFromSender<MyInviteRow>(sender) is not { } row) return;
        if (!await AppDialog.ConfirmAsync(_shell, "Decline invitation",
                $"Turn down the invitation to '{row.TeamName}'?\n\n"
                + $"{row.InvitedBy} can send you another one if it was a mistake.",
                "Decline", "Keep it"))
            return;
        try
        {
            await _teams.DeclineAsync(row.Token);
            await LoadMyInvitesAsync();
            Okay($"Declined the invitation to {row.TeamName}.");
        }
        catch (Exception ex) { await ReportAsync("Could not decline that invitation", ex); }
    }

    // ── create / join ──

    /// <summary>The host's own primary button calls this.</summary>
    public void BeginCreateTeam() => _ = CreateTeamAsync();

    private void OnNewTeam(object sender, RoutedEventArgs e) => _ = CreateTeamAsync();

    private async Task CreateTeamAsync()
    {
        var name = await _shell.PromptAsync("New team", "Team name");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var created = await App.State.Api.CreateTeamAsync(new CreateTeamRequest(name.Trim()));
            await LoadAsync(created.Id);
            await _shell.RefreshPackTeamFoldersAsync();
            Okay($"Created {created.Name}. Invite somebody with the button on the right.");
        }
        catch (Exception ex)
        {
            // Names are unique per owner, so a clash is always with one of your own teams and the message
            // can name it without revealing anything about other people's teams.
            await ReportAsync("Could not create the team", ex);
        }
    }

    private async void OnJoinWithCode(object sender, RoutedEventArgs e)
    {
        var code = await _shell.PromptAsync("Join a team", "Invite code");
        if (string.IsNullOrWhiteSpace(code)) return;
        // A whole pasted link works as well as the bare code.
        var token = SharingHubService.ParseInviteToken(code) ?? code.Trim();
        try
        {
            var joined = await _teams.AcceptAsync(token);
            await LoadAsync(joined.TeamId);
            await _shell.RefreshPackTeamFoldersAsync();
            Okay($"You joined {joined.TeamName}.");
        }
        catch (ApiException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound)
        {
            // Instance and hosted-content links are redeemed elsewhere; say where instead of "not valid".
            await AppDialog.MessageAsync(_shell, "That isn't a team code",
                "If it's a link to an instance, a shader pack or anything else somebody shared, "
                + "use Redeem a link at the top of the Sharing page.");
        }
        catch (Exception ex) { await ReportAsync("Could not use that code", ex); }
    }

    // ── invite ──

    private void OnInvite(object sender, RoutedEventArgs e) => _ = InviteAsync();

    private void OnCtxInvite(object sender, RoutedEventArgs e)
    {
        if (RowFromSender<TeamRow>(sender) is not { } row) return;
        _ = OpenThenAsync(row.Id, InviteAsync);
    }

    private async Task InviteAsync()
    {
        if (_selected is null) return;
        var already = _memberRows.Select(m => m.Username).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pick = await TeamMemberPickerDialog.InviteAsync(
            _shell, _teams, _selected.Name, already, canPickAdmin: _myRole == TeamRole.Owner);
        if (pick is null) return;

        try
        {
            await _teams.InviteAsync(_selected.Id,
                new CreateTeamInvitationRequest(pick.Username, pick.Role, null, InviteDays));
            Okay($"Invited {pick.Username}. They decide whether to join - nobody is added without saying yes.");
            await ReloadDetailAsync();
        }
        catch (Exception ex) { await ReportAsync("Could not send that invitation", ex); }
    }

    private async void OnMintLink(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        try
        {
            var invite = await _teams.InviteAsync(_selected.Id,
                new CreateTeamInvitationRequest(null, TeamRole.Member, null, InviteDays));
            var copied = invite.Token is { Length: > 0 } && ClipboardHelper.TrySetText(invite.Token);
            Okay(copied
                ? $"Invite code copied. Anyone with it can join {_selected.Name} for the next {InviteDays} days - revoke it below to stop that."
                : $"Invite code minted for {_selected.Name}. Use Copy code on the row below.");
            await ReloadDetailAsync();
        }
        catch (Exception ex) { await ReportAsync("Could not mint an invite code", ex); }
    }

    private void OnCopyInviteCode(object sender, RoutedEventArgs e)
    {
        if (RowFromSender<TeamInviteRow>(sender) is not { } row || row.Token is not { Length: > 0 } token) return;
        Okay(ClipboardHelper.TrySetText(token)
            ? "Invite code copied. Send it however you like - the person pastes it into Join with a code."
            : "The clipboard is in use by another program.");
    }

    private async void OnResendInvite(object sender, RoutedEventArgs e)
    {
        if (RowFromSender<TeamInviteRow>(sender) is not { } row || _selected is null) return;
        if (row.Username is not { Length: > 0 } username) return;
        try
        {
            await _teams.InviteAsync(_selected.Id,
                new CreateTeamInvitationRequest(username, row.Role, null, InviteDays));
            Okay($"Sent {username} another invitation.");
            await ReloadDetailAsync();
        }
        catch (Exception ex) { await ReportAsync("Could not send that invitation again", ex); }
    }

    private async void OnRevokeInvite(object sender, RoutedEventArgs e)
    {
        if (RowFromSender<TeamInviteRow>(sender) is not { } row || _selected is null) return;
        var what = row.IsLink ? "this invite code" : $"{row.Username}'s invitation";
        if (!await AppDialog.ConfirmAsync(_shell, "Revoke invitation",
                $"Withdraw {what}?\n\n"
                + (row.IsLink
                    ? "Anyone who still has the code will no longer be able to join."
                    : "They will no longer be able to accept it. You can invite them again at any time."),
                "Revoke", "Cancel", danger: true))
            return;

        try
        {
            await _teams.RevokeInvitationAsync(_selected.Id, row.Id);
            Okay("Invitation revoked.");
            await ReloadDetailAsync();
        }
        catch (Exception ex) { await ReportAsync("Could not revoke that invitation", ex); }
    }

    // ── members ──

    private void OnMemberMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is not { } menu) return;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private void OnMakeAdmin(object sender, RoutedEventArgs e) => _ = SetRoleAsync(sender, TeamRole.Admin);

    private void OnMakeMember(object sender, RoutedEventArgs e) => _ = SetRoleAsync(sender, TeamRole.Member);

    private async Task SetRoleAsync(object sender, TeamRole role)
    {
        if (RowFromSender<TeamMemberRow>(sender) is not { } row || _selected is null) return;
        try
        {
            await _teams.SetRoleAsync(_selected.Id, row.UserId, role);
            Okay(role == TeamRole.Admin
                ? $"{row.Username} can now invite people and remove ordinary members."
                : $"{row.Username} is an ordinary member again.");
            await ReloadDetailAsync();
        }
        catch (Exception ex) { await ReportAsync("Could not change that role", ex); }
    }

    private async void OnRemoveMember(object sender, RoutedEventArgs e)
    {
        if (RowFromSender<TeamMemberRow>(sender) is not { } row || _selected is null || !row.CanRemove) return;

        var shared = SharedParts(_selected.SharedPackCount, _selected.SharedModCount,
            _selected.SharedWorldCount, _selected.SharedResourcePackCount, _selected.SharedBundleCount);
        var consequence = shared.Count == 0
            ? "This team shares nothing yet, so they lose nothing today."
            : $"They immediately lose access to {string.Join(", ", shared)}.";

        if (!await AppDialog.ConfirmAsync(_shell, "Remove member",
                $"Take {row.Username} off '{_selected.Name}'?\n\n{consequence}",
                "Remove", "Cancel", danger: true))
            return;

        try
        {
            await App.State.Api.RemoveTeamMemberAsync(_selected.Id, row.UserId);
            await ReloadDetailAsync();
            await _shell.RefreshPackTeamFoldersAsync();
            Okay($"Removed {row.Username}.");
        }
        catch (Exception ex) { await ReportAsync("Could not remove that member", ex); }
    }

    private void OnCopyUserId(object sender, RoutedEventArgs e)
    {
        if (RowFromSender<TeamMemberRow>(sender) is not { } row) return;
        Okay(ClipboardHelper.TrySetText(row.UserId.ToString())
            ? "User ID copied."
            : "The clipboard is in use by another program.");
    }

    // ── leave / rename / transfer / delete ──

    private void OnLeave(object sender, RoutedEventArgs e) => _ = LeaveAsync();

    private void OnCtxLeave(object sender, RoutedEventArgs e)
    {
        if (RowFromSender<TeamRow>(sender) is { } row) _ = OpenThenAsync(row.Id, () => LeaveAsync(row.Id, row.Name));
    }

    private Task LeaveAsync() =>
        _selected is null ? Task.CompletedTask : LeaveAsync(_selected.Id, _selected.Name);

    private async Task LeaveAsync(Guid teamId, string name)
    {
        var shared = _selected is { } open && open.Id == teamId
            ? SharedParts(open.SharedPackCount, open.SharedModCount, open.SharedWorldCount,
                          open.SharedResourcePackCount, open.SharedBundleCount)
            : new List<string>();
        var consequence = shared.Count == 0
            ? "You lose nothing today - this team shares nothing yet."
            : $"You immediately lose access to {string.Join(", ", shared)}.";

        if (!await AppDialog.ConfirmAsync(_shell, "Leave team",
                $"Leave '{name}'?\n\n{consequence} Somebody on the team has to invite you back.",
                "Leave", "Cancel", danger: true))
            return;

        try
        {
            await App.State.Api.LeaveTeamAsync(teamId);
            _selected = null;
            await LoadAsync(Guid.Empty);
            await _shell.RefreshPackTeamFoldersAsync();
            Okay($"You left {name}.");
        }
        catch (Exception ex) { await ReportAsync("Could not leave the team", ex); }
    }

    private void OnRename(object sender, RoutedEventArgs e)
    {
        if (_selected is { } team) _ = RenameAsync(team.Id, team.Name);
    }

    private void OnCtxRename(object sender, RoutedEventArgs e)
    {
        if (RowFromSender<TeamRow>(sender) is { } row) _ = RenameAsync(row.Id, row.Name);
    }

    private async Task RenameAsync(Guid teamId, string current)
    {
        var name = await _shell.PromptAsync("Rename team", "New name", current);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == current) return;
        try
        {
            await App.State.Api.RenameTeamAsync(teamId, new RenameTeamRequest(name.Trim()));
            await LoadAsync(teamId);
            // The Instances page labels its team folder chips with the team name.
            await _shell.RefreshPackTeamFoldersAsync();
            Okay($"Renamed to {name.Trim()}.");
        }
        catch (Exception ex) { await ReportAsync("Could not rename the team", ex); }
    }

    private void OnTransfer(object sender, RoutedEventArgs e) => _ = TransferAsync();

    /// <summary>A row's menu acts on that row, so a team that isn't open is opened first; otherwise
    /// the action would hit the open team.</summary>
    private void OnCtxTransfer(object sender, RoutedEventArgs e)
    {
        if (RowFromSender<TeamRow>(sender) is not { } row) return;
        _ = OpenThenAsync(row.Id, TransferAsync);
    }

    private async Task OpenThenAsync(Guid teamId, Func<Task> action)
    {
        if (_selected?.Id != teamId)
        {
            _suppressSelection = true;
            try { TeamsList.SelectedItem = _rows.FirstOrDefault(r => r.Id == teamId); }
            finally { _suppressSelection = false; }
            await SelectAsync(teamId);
        }
        if (_selected?.Id != teamId) return; // the fetch failed; don't act on the wrong team
        await action();
    }

    private async void OnMakeOwner(object sender, RoutedEventArgs e)
    {
        if (RowFromSender<TeamMemberRow>(sender) is { } row) await TransferToAsync(row);
    }

    private async Task TransferAsync()
    {
        if (_selected is null || _myRole != TeamRole.Owner) return;
        var candidates = _memberRows.Where(m => !m.IsMe && !m.IsOwner).ToList();
        if (candidates.Count == 0)
        {
            await AppDialog.MessageAsync(_shell, "Nobody to hand it to",
                "Invite somebody and wait for them to join before handing the team over.");
            return;
        }

        var pick = await TeamMemberPickerDialog.PickAsync(_shell, candidates, "Transfer ownership",
            $"Choose who takes over '{_selected.Name}'. You stay on the team as an admin.", "Hand it over");
        if (pick is null) return;
        var target = candidates.FirstOrDefault(m => m.UserId == pick.UserId);
        if (target is null) return;
        await TransferToAsync(target);
    }

    private async Task TransferToAsync(TeamMemberRow target)
    {
        if (_selected is null || target.IsMe || _myRole != TeamRole.Owner) return;

        if (!await AppDialog.ConfirmAsync(_shell, "Transfer ownership",
                $"{target.Username} will be able to rename '{_selected.Name}', change everyone's role, "
                + "and delete it. You will not.\n\nYou stay on the team as an admin.",
                "Hand it over", "Cancel", danger: true))
            return;

        try
        {
            await App.State.Api.TransferTeamOwnershipAsync(
                _selected.Id, new TransferTeamOwnershipRequest(target.UserId));
            await LoadAsync(_selected.Id);
            Okay($"{target.Username} now owns this team. You are an admin on it.");
        }
        catch (Exception ex) { await ReportAsync("Could not transfer ownership", ex); }
    }

    private void OnDelete(object sender, RoutedEventArgs e) => _ = DeleteAsync();

    private void OnCtxDelete(object sender, RoutedEventArgs e)
    {
        if (RowFromSender<TeamRow>(sender) is not { } row) return;
        _ = OpenThenAsync(row.Id, DeleteAsync);
    }

    private async Task DeleteAsync()
    {
        if (_selected is null || _myRole != TeamRole.Owner) return;

        var shared = SharedParts(_selected.SharedPackCount, _selected.SharedModCount,
            _selected.SharedWorldCount, _selected.SharedResourcePackCount, _selected.SharedBundleCount);
        var others = Math.Max(0, _memberRows.Count - 1);
        var consequence = shared.Count == 0
            ? $"{Plural(others, "member", "members")} will be removed from the team. Nothing is shared with it."
            : $"{Plural(others, "member", "members")} lose access to {string.Join(", ", shared)}.";

        if (!await AppDialog.ConfirmAsync(_shell, "Delete team",
                $"Delete '{_selected.Name}'?\n\n{consequence}\n\nThis cannot be undone.",
                "Delete", "Cancel", danger: true))
            return;

        var name = _selected.Name;
        try
        {
            await App.State.Api.DeleteTeamAsync(_selected.Id);
            _selected = null;
            await LoadAsync(Guid.Empty);
            await _shell.RefreshPackTeamFoldersAsync();
            Okay($"Deleted {name}.");
        }
        catch (Exception ex) { await ReportAsync("Could not delete the team", ex); }
    }

    // ── shared content ──

    private async void OnUnshare(object sender, RoutedEventArgs e)
    {
        if (RowFromSender<TeamSharedRow>(sender) is not { } row || _selected is null) return;

        if (!await AppDialog.ConfirmAsync(_shell, "Stop sharing",
                $"Take '{row.Name}' away from {_selected.Name}?\n\n"
                + "Everyone on the team loses the access this grant gave them. Anything you shared "
                + "with them personally is untouched.",
                "Unshare", "Cancel", danger: true))
            return;

        try
        {
            var api = App.State.Api;
            switch (row.Kind)
            {
                case TeamSharedKind.Pack: await api.RemovePackTeamAsync(row.Id, _selected.Id); break;
                case TeamSharedKind.Mod: await api.RemoveModTeamAsync(row.Id, _selected.Id); break;
                case TeamSharedKind.World: await api.RemoveSharedWorldTeamAsync(row.Id, _selected.Id); break;
                case TeamSharedKind.ResourcePack: await api.RemoveResourcePackTeamAsync(row.Id, _selected.Id); break;
                default: await _teams.RemoveBundleTeamAsync(row.Id, _selected.Id); break;
            }
            Okay($"{_selected.Name} no longer has {row.Name}.");
            await ReloadDetailAsync();
            await _shell.RefreshPackTeamFoldersAsync();
        }
        catch (Exception ex) { await ReportAsync("Could not stop sharing that", ex); }
    }

    // ── clipboard ──

    private void OnCopyTeamId(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        Okay(ClipboardHelper.TrySetText(_selected.Id.ToString())
            ? "Team ID copied."
            : "The clipboard is in use by another program.");
    }

    private void OnCtxCopyTeamId(object sender, RoutedEventArgs e)
    {
        if (RowFromSender<TeamRow>(sender) is not { } row) return;
        Okay(ClipboardHelper.TrySetText(row.Id.ToString())
            ? "Team ID copied."
            : "The clipboard is in use by another program.");
    }

    // ── status / errors ──

    private const int InviteDays = 14;

    private void Okay(string message) => _state.Note(message);

    private void Fail(string message)
    {
        _state.Note(message);
        AppLog.Log("teams", message);
    }

    /// <summary>
    /// Reports a failed action in the user's own words, on the status line and in a dialog.
    /// </summary>
    /// <remarks>The sentence comes from the server's problem body.</remarks>
    private async Task ReportAsync(string title, Exception ex)
    {
        AppLog.LogError("teams", ex);
        var sentence = TeamsApi.Explain(ex, "The server would not do that.");
        _state.Note(title + " - " + sentence);
        await AppDialog.MessageAsync(_shell, title, sentence);
    }

    private static void Note(TextBlock label, string? text)
    {
        label.Text = text ?? "";
        label.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task ReloadDetailAsync()
    {
        if (_selected is null) return;
        // Re-list too: the member and shared counts on the list rows come from the summary.
        await LoadAsync(_selected.Id);
        if (_selected is { } team) await SelectAsync(team.Id);
    }

    private static bool IsTransport(Exception ex) =>
        ex is OfflineException || Connectivity.IsTransportFailure(ex, CancellationToken.None);

    private static string ReasonFor(Exception ex) =>
        ex is OfflineException { Reason: { Length: > 0 } reason }
            ? reason
            : Connectivity.DescribeTransportFailure(ex, CancellationToken.None) ?? "the server is not answering";

    /// <summary>Resolves the row a row-button or context-menu item belongs to.</summary>
    /// <remarks>A ContextMenu in a DataTemplate only gets the row's DataContext on the menu itself, so
    /// nested items walk up to the menu and use its placement target.</remarks>
    internal static T? RowFromSender<T>(object sender) where T : class
    {
        if (sender is FrameworkElement el && el.DataContext is T direct) return direct;
        if (sender is MenuItem mi)
        {
            var parent = mi.Parent;
            while (parent is MenuItem p) parent = p.Parent;
            if (parent is ContextMenu cm && cm.PlacementTarget is FrameworkElement target
                && target.DataContext is T row) return row;
        }
        return null;
    }

    // ── shared wording ──

    /// <summary>The non-zero parts of a team's shared-content counts, as readable phrases.</summary>
    internal static List<string> SharedParts(int packs, int mods, int worlds, int resourcePacks, int bundles)
    {
        var parts = new List<string>();
        if (packs > 0) parts.Add(Plural(packs, "instance", "instances"));
        if (mods > 0) parts.Add(Plural(mods, "mod", "mods"));
        if (worlds > 0) parts.Add(Plural(worlds, "world", "worlds"));
        if (resourcePacks > 0) parts.Add(Plural(resourcePacks, "resource pack", "resource packs"));
        if (bundles > 0) parts.Add(Plural(bundles, "bundle", "bundles"));
        return parts;
    }

    internal static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    internal static string RoleWord(TeamRole role) => role switch
    {
        TeamRole.Owner => "Owner",
        TeamRole.Admin => "Admin",
        _ => "Member"
    };

    internal static string InviteState(TeamInvitationEntry entry) =>
        entry.AcceptedAt is not null ? "Accepted"
        : entry.RevokedAt is not null ? "Revoked"
        : entry.ExpiresAt is { } expires && expires <= DateTimeOffset.UtcNow ? "Expired"
        : "Pending";

    /// <summary>
    /// A stable colour per team, derived from its id.
    /// </summary>
    /// <remarks>Team names are only unique per owner, so the swatch tells same-named teams apart.
    /// Derived rather than stored so a team has the same colour on every PC.</remarks>
    internal static Brush TeamColour(Guid id)
    {
        lock (ColourGate)
        {
            if (Colours.TryGetValue(id, out var cached)) return cached;
            var bytes = id.ToByteArray();
            var index = (bytes[0] * 31 + bytes[15] * 7 + bytes[7]) % AccentPalette.Colors.Length;
            var brush = AccentPalette.Brush(AccentPalette.Colors[index].Hex, Brushes.Gray);
            Colours[id] = brush;
            return brush;
        }
    }

    private static readonly object ColourGate = new();
    private static readonly Dictionary<Guid, Brush> Colours = new();

    /// <summary>What a grant lets a team do, in words rather than four checkbox names.</summary>
    internal static string PermissionWords(PackPermissions permissions)
    {
        if (permissions.HasFlag(PackPermissions.ManageCollaborators)) return "Full access";
        if (permissions.HasFlag(PackPermissions.UploadShared)) return "Can upload";
        if (permissions.HasFlag(PackPermissions.Download)) return "Read only";
        if (permissions.HasFlag(PackPermissions.View)) return "Can see it";
        return "No access";
    }

    internal static string KindWord(TeamSharedKind kind, BundleKind? bundle) => kind switch
    {
        TeamSharedKind.Pack => "Instance",
        TeamSharedKind.Mod => "Mod",
        TeamSharedKind.World => "World",
        TeamSharedKind.ResourcePack => "Resource pack",
        _ => bundle switch
        {
            BundleKind.ShaderPack => "Shader pack",
            BundleKind.ConfigBundle => "Config bundle",
            BundleKind.KubeJsBundle => "KubeJS bundle",
            BundleKind.DataPack => "Data pack",
            _ => "Bundle"
        }
    };
}

// ═════════════════════════════════════════════════════════════════════════════
//  ROW VIEW-MODELS

/// <summary>One team in the list, already told what this viewer's role permits.</summary>
public sealed class TeamRow
{
    public TeamSummary Source { get; }

    public TeamRow(TeamSummary source, Guid me)
    {
        Source = source;
        IsMine = source.OwnerId == me;
        // Team.OwnerId is the source of truth. A cached row from before a transfer would otherwise
        // keep offering owner-only actions the server has already stopped accepting.
        Role = IsMine ? TeamRole.Owner
             : source.MyRole == TeamRole.Owner ? TeamRole.Admin
             : source.MyRole;
    }

    public Guid Id => Source.Id;
    public string Name => Source.Name;
    public bool IsMine { get; }
    public TeamRole Role { get; }

    public Brush ColourBrush => SharingPeoplePanel.TeamColour(Source.Id);
    public string RoleLabel => SharingPeoplePanel.RoleWord(Role);

    /// <summary>Owned-versus-joined, the member count and what it shares, in one line.</summary>
    public string SubLabel
    {
        get
        {
            var whose = IsMine ? "Yours" : $"by {Source.OwnerUsername}";
            var members = SharingPeoplePanel.Plural(Source.MemberCount, "member", "members");
            var total = SharedTotal(Source);
            return total == 0
                ? $"{whose} · {members}"
                : $"{whose} · {members} · {SharingPeoplePanel.Plural(total, "shared item", "shared items")}";
        }
    }

    public static int SharedTotal(TeamSummary t) =>
        t.SharedPackCount + t.SharedModCount + t.SharedWorldCount + t.SharedResourcePackCount + t.SharedBundleCount;

    public bool CanRename => Role == TeamRole.Owner;

    public Visibility RenameVisibility => Vis(Role == TeamRole.Owner);
    public Visibility DeleteVisibility => Vis(Role == TeamRole.Owner);
    public Visibility TransferVisibility => Vis(Role == TeamRole.Owner);
    public Visibility InviteVisibility => Vis(Role is TeamRole.Owner or TeamRole.Admin);
    public Visibility LeaveVisibility => Vis(Role != TeamRole.Owner);

    private static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    public string RowTooltip
    {
        get
        {
            var parts = SharingPeoplePanel.SharedParts(Source.SharedPackCount, Source.SharedModCount,
                Source.SharedWorldCount, Source.SharedResourcePackCount, Source.SharedBundleCount);
            var shared = parts.Count == 0 ? "Nothing is shared with it yet." : "Shares " + string.Join(", ", parts) + ".";
            var whose = IsMine ? "You own it" : $"Owned by {Source.OwnerUsername}";
            return $"{Source.Name}\n{whose} · you are {RoleLabel.ToLowerInvariant()} · "
                 + $"{SharingPeoplePanel.Plural(Source.MemberCount, "member", "members")}\n{shared}";
        }
    }
}

/// <summary>
/// One member row, already told what the viewer may do with it.
/// </summary>
/// <remarks>The rules (an owner can do anything, an admin can remove ordinary members, everyone can
/// leave) live here so they are written once.</remarks>
public sealed class TeamMemberRow
{
    private bool _online;
    private string _reason;

    public TeamMemberRow(TeamMemberEntry entry, TeamRole viewerRole, bool isMe, bool offline, string offlineReason)
    {
        UserId = entry.UserId;
        Username = entry.Username;
        IsOwner = entry.IsOwner || entry.Role == TeamRole.Owner;
        Role = IsOwner ? TeamRole.Owner : entry.Role;
        EmailConfirmed = entry.EmailConfirmed;
        JoinedAt = entry.JoinedAt;
        IsMe = isMe;
        ViewerRole = viewerRole;
        _online = !offline;
        _reason = offlineReason;
    }

    public Guid UserId { get; }
    public string Username { get; }
    public bool IsOwner { get; }
    public bool IsMe { get; }
    public TeamRole Role { get; }
    public TeamRole ViewerRole { get; }
    public DateTimeOffset JoinedAt { get; }
    public bool EmailConfirmed { get; }

    public void SetOnline(bool online, string reason) { _online = online; _reason = reason; }

    /// <summary>The owner may remove anybody but themselves; an admin may remove ordinary members.
    /// Nobody may remove the owner; the server refuses and says to transfer instead.</summary>
    public bool CanRemove =>
        !IsMe && !IsOwner
        && (ViewerRole == TeamRole.Owner || (ViewerRole == TeamRole.Admin && Role == TeamRole.Member));

    public string RoleLabel => SharingPeoplePanel.RoleWord(Role);
    public string JoinedLabel => $"joined {TimeFormat.Date(JoinedAt)}";

    public Visibility YouVisibility => IsMe ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UnverifiedVisibility => EmailConfirmed ? Visibility.Collapsed : Visibility.Visible;
    public Visibility RemoveVisibility => CanRemove ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LeaveVisibility => IsMe && !IsOwner ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MakeAdminVisibility =>
        ViewerRole == TeamRole.Owner && !IsMe && !IsOwner && Role == TeamRole.Member
            ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MakeMemberVisibility =>
        ViewerRole == TeamRole.Owner && !IsMe && !IsOwner && Role == TeamRole.Admin
            ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MakeOwnerVisibility =>
        ViewerRole == TeamRole.Owner && !IsMe && !IsOwner ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Copy user ID always applies, so the overflow button is always worth having.</summary>
    public Visibility MenuVisibility => Visibility.Visible;

    /// <summary>Role decides whether a button is there; the connection decides whether it works.</summary>
    public bool ActionsEnabled => _online;

    public string RemoveTip => _online
        ? "Take this person off the team"
        : SharingPeoplePanel.OfflineTip(_reason);

    public string LeaveTip => _online
        ? "Remove yourself from this team"
        : SharingPeoplePanel.OfflineTip(_reason);

    public string RowTooltip =>
        $"{Username} · {RoleLabel.ToLowerInvariant()}{(IsMe ? " · you" : "")}\n{JoinedLabel}"
        + (EmailConfirmed ? "" : "\nTheir email address has never been confirmed.")
        + (_online ? "" : $"\nOffline - {_reason}.");
}

/// <summary>One invitation a team has issued.</summary>
public sealed class TeamInviteRow
{
    private bool _online;
    private string _reason;

    public TeamInviteRow(TeamInvitationEntry entry, bool online, string offlineReason)
    {
        Source = entry;
        State = SharingPeoplePanel.InviteState(entry);
        _online = online;
        _reason = offlineReason;
    }

    public TeamInvitationEntry Source { get; }
    public Guid Id => Source.Id;
    public string? Username => Source.InvitedUsername;
    public string? Token => Source.Token;
    public bool IsLink => Source.IsLink;
    public TeamRole Role => Source.Role;
    public string State { get; }

    public void SetOnline(bool online, string reason) { _online = online; _reason = reason; }

    public string Who => IsLink
        ? "Anyone with the code"
        : Source.InvitedUsername ?? "Somebody";

    public string StateLabel => State;

    public string Detail
    {
        get
        {
            var sent = $"sent {TimeFormat.MonthDay(Source.CreatedAt)}";
            var by = $"by {Source.InvitedByUsername}";
            var role = SharingPeoplePanel.RoleWord(Source.Role).ToLowerInvariant();
            var expiry = Source.ExpiresAt is { } when
                ? State == "Pending" ? $" · expires {TimeFormat.MonthDay(when)}" : ""
                : "";
            return $"joins as {role} · {sent} {by}{expiry}";
        }
    }

    public Visibility CopyVisibility =>
        IsLink && State == "Pending" && Token is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Only for a named invitation that can no longer be accepted; resending a pending one
    /// would change nothing.</summary>
    public Visibility ResendVisibility =>
        !IsLink && State is "Revoked" or "Expired" && Username is { Length: > 0 }
            ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RevokeVisibility =>
        State == "Pending" ? Visibility.Visible : Visibility.Collapsed;

    public bool ActionsEnabled => _online;

    public string ResendTip => _online
        ? "Issue a fresh invitation to the same person"
        : SharingPeoplePanel.OfflineTip(_reason);

    public string RevokeTip => _online
        ? "Withdraw it. The code stops working immediately."
        : SharingPeoplePanel.OfflineTip(_reason);

    public string RowTooltip =>
        $"{Who} · {State.ToLowerInvariant()}\n{Detail}" + (_online ? "" : $"\nOffline - {_reason}.");
}

/// <summary>One thing a team has been granted access to.</summary>
public sealed class TeamSharedRow
{
    private bool _online;
    private string _reason;

    public TeamSharedRow(TeamSharedItem item, bool online, string offlineReason)
    {
        Source = item;
        _online = online;
        _reason = offlineReason;
    }

    public TeamSharedItem Source { get; }
    public Guid Id => Source.Id;
    public string Name => Source.Name;
    public TeamSharedKind Kind => Source.Kind;

    public void SetOnline(bool online, string reason) { _online = online; _reason = reason; }

    public string KindLabel => SharingPeoplePanel.KindWord(Source.Kind, Source.Bundle);
    public string PermissionLabel => SharingPeoplePanel.PermissionWords(Source.Permissions);

    public string Detail =>
        $"owned by {Source.OwnerUsername} · updated {TimeFormat.Date(Source.UpdatedAt)}";

    /// <summary>Only the item's own owner may drop the grant, since the route checks the item's owner
    /// rather than the team's. The server reports it in <c>CanRevoke</c>.</summary>
    public Visibility UnshareVisibility =>
        Source.CanRevoke ? Visibility.Visible : Visibility.Collapsed;

    public bool ActionsEnabled => _online;

    public string UnshareTip => _online
        ? "Drop this team's access. Anything shared with people individually is untouched."
        : SharingPeoplePanel.OfflineTip(_reason);

    public string RowTooltip =>
        $"{Source.Name} ({KindLabel.ToLowerInvariant()})\n{PermissionLabel} · {Detail}"
        + (Source.Summary is { Length: > 0 } s ? $"\n{s}" : "")
        + (Source.CanRevoke ? "" : "\nOnly its owner can take this team's access away.")
        + (_online ? "" : $"\nOffline - {_reason}.");
}

/// <summary>One invitation waiting for the signed-in user's answer.</summary>
public sealed class MyInviteRow
{
    public MyInviteRow(TeamInvitationEntry entry) => Source = entry;

    public TeamInvitationEntry Source { get; }
    public Guid TeamId => Source.TeamId;
    public string TeamName => Source.TeamName;
    public string Token => Source.Token ?? "";
    public string InvitedBy => Source.InvitedByUsername;

    public string Title => Source.TeamName;

    public string JoinTip => "Accept the invitation and join this team";
    public string DeclineTip => "Turn this invitation down. Whoever sent it can send another.";

    public string Detail
    {
        get
        {
            var role = SharingPeoplePanel.RoleWord(Source.Role).ToLowerInvariant();
            var note = Source.Message is { Length: > 0 } m ? $" - '{m}'" : "";
            var expiry = Source.ExpiresAt is { } when ? $" · expires {TimeFormat.MonthDay(when)}" : "";
            return $"{Source.InvitedByUsername} invited you as {role}{expiry}{note}";
        }
    }
}
