using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// The Teams screen: create a team, manage who is on it, and see how much is shared with it.
/// </summary>
/// <remarks>
/// <para>Two rules shape the interaction here. First, almost every action is owner-only on the
/// server, so the screen decides what to show from <see cref="TeamDetail.OwnerId"/> rather than
/// letting the user press a button that can only ever come back 403 — the previous version showed a
/// Remove button on every member row including the owner's own, and both were guaranteed failures.
/// Second, leaving a team and being removed from one are the same endpoint with a different meaning,
/// so the row the signed-in user is looking at decides which of the two words it shows.</para>
/// <para>Anything that changes membership or the team list also refreshes the Instances screen's team
/// folder chips, because those chips are built from this same list.</para>
/// </remarks>
public partial class TeamsView : Page
{
    /// <summary>Every team the server returned, unfiltered — the list box shows a filtered,
    /// sorted projection of this.</summary>
    private List<TeamSummary> _all = new();

    private readonly ObservableCollection<TeamRow> _rows = new();
    private readonly ObservableCollection<TeamMemberRow> _memberRows = new();
    private readonly MainWindow _shell;
    private TeamDetail? _selected;

    /// <summary>Guards the selection handler while the list is being rebuilt, so restoring the
    /// previous selection after a refresh does not fire a redundant detail fetch.</summary>
    private bool _suppressSelection;

    public TeamsView(MainWindow shell)
    {
        InitializeComponent();
        _shell = shell;
        TeamsList.ItemsSource = _rows;
        MembersList.ItemsSource = _memberRows;
        SortBox.SelectedIndex = 0;
        Loaded += async (_, _) =>
        {
            await RefreshAsync();
            Window.GetWindow(this)!.PreviewKeyDown += OnShellKeyDown;
        };
        Unloaded += (_, _) =>
        {
            if (Window.GetWindow(this) is { } w) w.PreviewKeyDown -= OnShellKeyDown;
        };
    }

    // ── status line ──────────────────────────────────────────────────────────

    /// <summary>Transient confirmation of something that worked.</summary>
    /// <remarks>Successes and failures used to share one red <c>ErrorText</c> label, so "Team
    /// created" arrived looking like a crash. The label is neutral now and only failures colour
    /// it.</remarks>
    private void Okay(string message)
    {
        StatusLabel.SetResourceReference(ForegroundProperty, "TextSecondaryBrush");
        StatusLabel.Text = message;
    }

    private void Fail(string message)
    {
        StatusLabel.SetResourceReference(ForegroundProperty, "DangerBrush");
        StatusLabel.Text = message;
    }

    private void ClearStatus() => Okay("");

    /// <summary>Reports a failed action both on the status line and as a dialog, because a failure
    /// the user provoked by clicking something deserves more than a line at the bottom of a page
    /// they may have scrolled past.</summary>
    private async Task ReportAsync(string title, Exception ex)
    {
        Fail(ex.Message);
        await AppDialog.MessageAsync(_shell, title, ex.Message);
    }

    // ── loading ──────────────────────────────────────────────────────────────

    /// <param name="selectId">Which team to end up on. Null keeps the current selection;
    /// <see cref="Guid.Empty"/> matches nothing, which is how "the team I was on is gone" is
    /// expressed after a delete or a leave.</param>
    private async Task RefreshAsync(Guid? selectId = null)
    {
        ClearStatus();
        var keep = selectId ?? (TeamsList.SelectedItem as TeamRow)?.Id;
        TeamsLoadingLabel.Visibility = Visibility.Visible;
        TeamsEmptyLabel.Visibility = Visibility.Collapsed;
        try
        {
            _all = await App.State.Api.ListTeamsAsync();
            ApplyFilter(keep);
        }
        catch (Exception ex)
        {
            Fail("Could not load your teams: " + ex.Message);
        }
        finally { TeamsLoadingLabel.Visibility = Visibility.Collapsed; }
    }

    /// <summary>Rebuilds the visible rows from the search box and sort choice, restoring the given
    /// selection when that team survived the filter.</summary>
    private void ApplyFilter(Guid? selectId)
    {
        var q = SearchBox.Text?.Trim();
        IEnumerable<TeamSummary> teams = _all;
        if (!string.IsNullOrEmpty(q))
            teams = teams.Where(t =>
                t.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || t.OwnerUsername.Contains(q, StringComparison.OrdinalIgnoreCase));

        teams = SortBox.SelectedIndex switch
        {
            1 => teams.OrderByDescending(t => t.MemberCount).ThenBy(t => t.Name),
            2 => teams.OrderByDescending(TeamRow.SharedTotal).ThenBy(t => t.Name),
            _ => teams.OrderBy(t => t.Name)
        };

        _suppressSelection = true;
        try
        {
            _rows.Clear();
            foreach (var t in teams) _rows.Add(new TeamRow(t));
            TeamsList.SelectedItem = selectId is { } id ? _rows.FirstOrDefault(r => r.Id == id) : null;
        }
        finally { _suppressSelection = false; }

        TeamsEmptyLabel.Text = _all.Count == 0
            ? "You're not on any teams yet — create one above."
            : "No team matches that search.";
        TeamsEmptyLabel.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (TeamsList.SelectedItem is TeamRow row) _ = SelectAsync(row.Id);
        else ApplyDetail(null);
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        try { await RefreshAsync(); }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) =>
        ApplyFilter((TeamsList.SelectedItem as TeamRow)?.Id);

    private void OnSearchPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || SearchBox.Text.Length == 0) return;
        SearchBox.Text = "";
        e.Handled = true;
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyFilter((TeamsList.SelectedItem as TeamRow)?.Id);
    }

    /// <summary>Ctrl+F, F5, F2 and Delete, matching the Instances screen.</summary>
    private void OnShellKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsVisible) return;
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            _ = RefreshAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.F2 && TeamsList.IsKeyboardFocusWithin && TeamsList.SelectedItem is TeamRow row)
        {
            _ = RenameAsync(row);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && TeamsList.IsKeyboardFocusWithin && _selected is not null)
        {
            _ = DeleteSelectedAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && MembersList.IsKeyboardFocusWithin
                 && MembersList.SelectedItem is TeamMemberRow member)
        {
            _ = member.IsMe ? LeaveAsync() : RemoveMemberAsync(member);
            e.Handled = true;
        }
    }

    // ── create ───────────────────────────────────────────────────────────────

    // Enter-to-submit for the inline create/add boxes (these panels have no default button).
    private void OnNewTeamKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; OnCreateTeam(sender, e); }
    }

    private void OnNewMemberKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; OnAddMember(sender, e); }
    }

    private async void OnCreateTeam(object sender, RoutedEventArgs e)
    {
        var name = NewTeamBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;
        try
        {
            // Selecting the new team and focusing the member box turns "create a team" into
            // "create a team and add someone", which is the only reason anyone creates one.
            var created = await App.State.Api.CreateTeamAsync(new CreateTeamRequest(name));
            NewTeamBox.Text = "";
            await RefreshAsync(created.Id);
            await _shell.RefreshPackTeamFoldersAsync();
            NewMemberBox.Focus();
            Okay($"Created {created.Name}. Add members below.");
        }
        catch (Exception ex) { await ReportAsync("Could not create the team", ex); }
    }

    // ── selection / detail ───────────────────────────────────────────────────

    private async void OnTeamSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        if (TeamsList.SelectedItem is TeamRow row) await SelectAsync(row.Id);
        else ApplyDetail(null);
    }

    /// <summary>Loads one team's detail, disabling the list while the request is in flight so a slow
    /// server cannot be clicked into a queue of overlapping fetches.</summary>
    private async Task SelectAsync(Guid teamId)
    {
        TeamsList.IsEnabled = false;
        MembersEmptyLabel.Text = "Loading…";
        MembersEmptyLabel.Visibility = Visibility.Visible;
        try { ApplyDetail(await App.State.Api.GetTeamAsync(teamId)); }
        catch (Exception ex)
        {
            ApplyDetail(null);
            Fail("Could not open that team: " + ex.Message);
        }
        finally { TeamsList.IsEnabled = true; }
    }

    private void ApplyDetail(TeamDetail? detail)
    {
        _selected = detail;
        _memberRows.Clear();

        if (detail is null)
        {
            DetailHeader.Text = "Select a team to manage members";
            DetailSubHeader.Text = "";
            MembersEmptyLabel.Text = "Select a team above to see who is on it.";
            MembersEmptyLabel.Visibility = Visibility.Visible;
            AddMemberButton.IsEnabled = false;
            DeleteTeamButton.IsEnabled = false;
            LeaveTeamButton.IsEnabled = false;
            NewMemberBox.IsEnabled = false;
            return;
        }

        var me = App.State.Settings.UserId;
        var isOwner = detail.OwnerId == me;

        // MemberDetails carries the owner flag and join date; Members is the old shape and is only
        // used as a fallback when an older server answers without the richer list.
        var entries = detail.MemberDetails is { Count: > 0 }
            ? detail.MemberDetails
            : detail.Members
                .Select(m => new TeamMemberEntry(m.Id, m.Username, m.EmailConfirmed,
                    detail.CreatedAt, m.Id == detail.OwnerId))
                .ToList();

        foreach (var entry in entries)
            _memberRows.Add(new TeamMemberRow(entry, viewerIsOwner: isOwner, isMe: entry.UserId == me));

        DetailHeader.Text = $"{detail.Name} — {entries.Count} member(s)";
        DetailSubHeader.Text = DescribeTeam(detail);
        MembersEmptyLabel.Text = "Nobody is on this team yet — add someone by username above.";
        MembersEmptyLabel.Visibility = _memberRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        AddMemberButton.IsEnabled = isOwner;
        NewMemberBox.IsEnabled = isOwner;
        DeleteTeamButton.IsEnabled = isOwner;
        // The owner cannot leave — the server refuses it, and "delete the team" is the real action.
        LeaveTeamButton.IsEnabled = !isOwner;
        LeaveTeamButton.ToolTip = isOwner
            ? "The owner can't leave. Transfer ownership first, or delete the team."
            : "Removes you from this team. You lose access to everything shared with it.";
    }

    /// <summary>A one-line "created X · N instances, M mods…" summary for the detail header.</summary>
    private static string DescribeTeam(TeamDetail d)
    {
        var shared = SharedParts(d.SharedPackCount, d.SharedModCount, d.SharedWorldCount, d.SharedResourcePackCount);
        var created = $"Created {d.CreatedAt.ToLocalTime():d MMM yyyy}";
        return shared.Count == 0
            ? $"{created} · owned by {d.OwnerUsername} · nothing is shared with this team yet"
            : $"{created} · owned by {d.OwnerUsername} · shares {string.Join(", ", shared)}";
    }

    /// <summary>The non-zero parts of a team's shared-content counts, as readable phrases.</summary>
    internal static List<string> SharedParts(int packs, int mods, int worlds, int resourcePacks)
    {
        var parts = new List<string>();
        if (packs > 0) parts.Add(Plural(packs, "instance", "instances"));
        if (mods > 0) parts.Add(Plural(mods, "mod", "mods"));
        if (worlds > 0) parts.Add(Plural(worlds, "world", "worlds"));
        if (resourcePacks > 0) parts.Add(Plural(resourcePacks, "resource pack", "resource packs"));
        return parts;
    }

    internal static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    // ── members ──────────────────────────────────────────────────────────────

    private async void OnAddMember(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var name = NewMemberBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;
        try
        {
            var added = await App.State.Api.AddTeamMemberAsync(_selected.Id, new AddTeamMemberRequest(name));
            NewMemberBox.Text = "";
            await ReloadSelectedAsync();
            await _shell.RefreshPackTeamFoldersAsync();
            Okay($"Added {added.Username}.");
        }
        catch (Exception ex) { await ReportAsync("Could not add that member", ex); }
    }

    private async void OnRemoveMember(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RowFromSender<TeamMemberRow>(sender) is { } row) await RemoveMemberAsync(row);
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private async Task RemoveMemberAsync(TeamMemberRow row)
    {
        if (_selected is null || !row.CanRemove) return;
        var shared = SharedParts(_selected.SharedPackCount, _selected.SharedModCount,
            _selected.SharedWorldCount, _selected.SharedResourcePackCount);
        var consequence = shared.Count == 0
            ? "They keep access to nothing — this team shares nothing yet."
            : $"They immediately lose access to {string.Join(", ", shared)}.";

        if (!await AppDialog.ConfirmAsync(_shell, "Remove member",
                $"Remove {row.Username} from '{_selected.Name}'?\n\n{consequence}",
                "Remove", "Cancel", danger: true))
            return;

        try
        {
            await App.State.Api.RemoveTeamMemberAsync(_selected.Id, row.UserId);
            await ReloadSelectedAsync();
            await _shell.RefreshPackTeamFoldersAsync();
            Okay($"Removed {row.Username}.");
        }
        catch (Exception ex) { await ReportAsync("Could not remove that member", ex); }
    }

    /// <summary>Leaving is the same endpoint as being removed — the server permits self-removal for
    /// anyone but the owner — so the only difference is the wording and that the team disappears from
    /// this screen afterwards.</summary>
    private async void OnLeaveTeam(object sender, RoutedEventArgs e)
    {
        try { await LeaveAsync(); }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private async Task LeaveAsync()
    {
        if (_selected is null) return;
        if (_selected.OwnerId == App.State.Settings.UserId)
        {
            await AppDialog.MessageAsync(_shell, "You own this team",
                "The owner can't leave a team. Transfer ownership to another member first, or delete the team.");
            return;
        }

        var shared = SharedParts(_selected.SharedPackCount, _selected.SharedModCount,
            _selected.SharedWorldCount, _selected.SharedResourcePackCount);
        var consequence = shared.Count == 0
            ? "This team shares nothing with you yet."
            : $"You immediately lose access to {string.Join(", ", shared)}.";

        if (!await AppDialog.ConfirmAsync(_shell, "Leave team",
                $"Leave '{_selected.Name}'?\n\n{consequence} Only the owner can add you back.",
                "Leave", "Cancel", danger: true))
            return;

        var name = _selected.Name;
        try
        {
            await App.State.Api.LeaveTeamAsync(_selected.Id);
            await RefreshAsync(selectId: Guid.Empty);
            await _shell.RefreshPackTeamFoldersAsync();
            Okay($"You left {name}.");
        }
        catch (Exception ex) { await ReportAsync("Could not leave the team", ex); }
    }

    // ── rename / transfer / delete ───────────────────────────────────────────

    private async void OnCtxRenameTeam(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RowFromSender<TeamRow>(sender) is { } row) await RenameAsync(row);
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private async Task RenameAsync(TeamRow row)
    {
        if (row.Source.OwnerId != App.State.Settings.UserId)
        {
            await AppDialog.MessageAsync(_shell, "Owner only",
                $"Only {row.Source.OwnerUsername} can rename '{row.Name}'.");
            return;
        }

        var name = await _shell.PromptAsync("Rename team", "New name", row.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == row.Name) return;

        try
        {
            await App.State.Api.RenameTeamAsync(row.Id, new RenameTeamRequest(name.Trim()));
            await RefreshAsync(row.Id);
            // The Instances screen's folder chips are labelled with the team name.
            await _shell.RefreshPackTeamFoldersAsync();
            Okay($"Renamed to {name.Trim()}.");
        }
        catch (Exception ex) { await ReportAsync("Could not rename the team", ex); }
    }

    private async void OnCtxTransferOwnership(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RowFromSender<TeamRow>(sender) is not { } row) return;
            if (_selected?.Id != row.Id) await SelectAsync(row.Id);
            if (_selected is null) return;

            var candidates = _memberRows.Where(m => !m.IsMe).ToList();
            if (candidates.Count == 0)
            {
                await AppDialog.MessageAsync(_shell, "Nobody to hand it to",
                    "Add another member to this team before transferring ownership.");
                return;
            }

            var picker = new TeamMemberPickerDialog(candidates, "Transfer ownership",
                $"Choose who takes over '{_selected.Name}'. You stay on the team as an ordinary member.",
                "Make owner") { Owner = _shell };
            if (picker.ShowDialog() != true || picker.Selected is not { } target) return;
            await TransferAsync(target);
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private async void OnCtxMakeOwner(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RowFromSender<TeamMemberRow>(sender) is { } row) await TransferAsync(row);
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private async Task TransferAsync(TeamMemberRow target)
    {
        if (_selected is null || target.IsMe) return;
        if (_selected.OwnerId != App.State.Settings.UserId)
        {
            await AppDialog.MessageAsync(_shell, "Owner only",
                "Only the current owner can transfer a team.");
            return;
        }

        if (!await AppDialog.ConfirmAsync(_shell, "Transfer ownership",
                $"{target.Username} will be able to rename '{_selected.Name}', add and remove members, "
                + "and delete it. You will not.\n\nYou stay on the team as an ordinary member.",
                "Transfer", "Cancel", danger: true))
            return;

        try
        {
            await App.State.Api.TransferTeamOwnershipAsync(
                _selected.Id, new TransferTeamOwnershipRequest(target.UserId));
            await RefreshAsync(_selected.Id);
            Okay($"{target.Username} now owns this team.");
        }
        catch (Exception ex) { await ReportAsync("Could not transfer ownership", ex); }
    }

    private async void OnCtxDeleteTeam(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RowFromSender<TeamRow>(sender) is not { } row) return;
            if (_selected?.Id != row.Id) await SelectAsync(row.Id);
            await DeleteSelectedAsync();
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private async void OnDeleteTeam(object sender, RoutedEventArgs e)
    {
        try { await DeleteSelectedAsync(); }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private async Task DeleteSelectedAsync()
    {
        if (_selected is null) return;
        if (_selected.OwnerId != App.State.Settings.UserId)
        {
            await AppDialog.MessageAsync(_shell, "Owner only",
                $"Only {_selected.OwnerUsername} can delete '{_selected.Name}'. You can leave it instead.");
            return;
        }

        // Spell out what disappears. Deleting a team silently strips access to everything shared with
        // it, and the counts are the only warning anyone gets.
        var shared = SharedParts(_selected.SharedPackCount, _selected.SharedModCount,
            _selected.SharedWorldCount, _selected.SharedResourcePackCount);
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
            await RefreshAsync(selectId: Guid.Empty);
            await _shell.RefreshPackTeamFoldersAsync();
            Okay($"Deleted {name}.");
        }
        catch (Exception ex) { await ReportAsync("Could not delete the team", ex); }
    }

    private async void OnCtxLeaveTeam(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RowFromSender<TeamRow>(sender) is not { } row) return;
            if (_selected?.Id != row.Id) await SelectAsync(row.Id);
            await LeaveAsync();
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    // ── clipboard ────────────────────────────────────────────────────────────

    private void OnCtxCopyTeamId(object sender, RoutedEventArgs e)
    {
        if (RowFromSender<TeamRow>(sender) is not { } row) return;
        Okay(ClipboardHelper.TrySetText(row.Id.ToString())
            ? "Team ID copied."
            : "The clipboard is in use by another program.");
    }

    private void OnCtxCopyUserId(object sender, RoutedEventArgs e)
    {
        if (RowFromSender<TeamMemberRow>(sender) is not { } row) return;
        Okay(ClipboardHelper.TrySetText(row.UserId.ToString())
            ? "User ID copied."
            : "The clipboard is in use by another program.");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>Resolves the row a row-button or context-menu item belongs to.</summary>
    /// <remarks>A context menu declared inside a DataTemplate inherits the row's DataContext, but
    /// only on the menu itself — a nested submenu item has to walk back up to the ContextMenu and
    /// ask its placement target, which is what the second branch does.</remarks>
    private static T? RowFromSender<T>(object sender) where T : class
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

    private async Task ReloadSelectedAsync()
    {
        if (_selected is null) return;
        // Re-list as well: member and shared counts on the row pills come from the summary.
        await RefreshAsync(_selected.Id);
    }
}

/// <summary>One team in the list.</summary>
public sealed class TeamRow(TeamSummary src)
{
    public TeamSummary Source { get; } = src;
    public Guid Id => Source.Id;
    public string Name => Source.Name;
    public string OwnerLabel => $"by {Source.OwnerUsername}";
    public string MemberCountLabel => TeamsView.Plural(Source.MemberCount, "member", "members");

    /// <summary>How many things of any kind this team shares — also the "sort by shared" key.</summary>
    public static int SharedTotal(TeamSummary t) =>
        t.SharedPackCount + t.SharedModCount + t.SharedWorldCount + t.SharedResourcePackCount;

    public string SharedCountLabel => TeamsView.Plural(SharedTotal(Source), "shared item", "shared items");

    /// <summary>The shared pill is hidden rather than showing "0 shared items", which would put the
    /// loudest number on the emptiest team.</summary>
    public Visibility SharedPillVisibility =>
        SharedTotal(Source) > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string RowTooltip
    {
        get
        {
            var parts = TeamsView.SharedParts(Source.SharedPackCount, Source.SharedModCount,
                Source.SharedWorldCount, Source.SharedResourcePackCount);
            var shared = parts.Count == 0 ? "Nothing is shared with it yet." : "Shares " + string.Join(", ", parts) + ".";
            return $"{Source.Name}\nOwned by {Source.OwnerUsername} · {MemberCountLabel}\n{shared}";
        }
    }
}

/// <summary>
/// One member row, already told what the viewer is allowed to do with it.
/// </summary>
/// <remarks>
/// The visibility flags are computed here rather than in the template so the rule "the owner has no
/// button, you get Leave, everyone else gets Remove and only when you are the owner" lives in exactly
/// one place — the previous screen decided it per-click and got it wrong for three of the four cases.
/// </remarks>
public sealed class TeamMemberRow
{
    public Guid UserId { get; }
    public string Username { get; }
    public bool IsOwner { get; }
    public bool IsMe { get; }
    public DateTimeOffset JoinedAt { get; }
    public bool EmailConfirmed { get; }

    /// <summary>Whether the viewer may take this person off the team.</summary>
    public bool CanRemove { get; }

    /// <summary>Whether the person looking at this row owns the team.</summary>
    public bool ViewerIsOwner { get; }

    public TeamMemberRow(TeamMemberEntry entry, bool viewerIsOwner, bool isMe)
    {
        ViewerIsOwner = viewerIsOwner;
        UserId = entry.UserId;
        Username = entry.Username;
        IsOwner = entry.IsOwner;
        EmailConfirmed = entry.EmailConfirmed;
        JoinedAt = entry.JoinedAt;
        IsMe = isMe;
        // The owner can never be removed (the server refuses it) and nobody but the owner may remove
        // anyone else; your own row offers Leave instead.
        CanRemove = viewerIsOwner && !IsOwner && !isMe;
    }

    public string JoinedLabel => $"joined {JoinedAt.ToLocalTime():d MMM yyyy}";

    public Visibility OwnerPillVisibility => IsOwner ? Visibility.Visible : Visibility.Collapsed;
    public Visibility YouVisibility => IsMe ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RemoveVisibility => CanRemove ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LeaveVisibility => IsMe && !IsOwner ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MakeOwnerVisibility =>
        ViewerIsOwner && !IsMe && !IsOwner ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>An unconfirmed email is worth showing on a team: that account cannot be reached if
    /// the team needs to contact them, and it is often a typo'd sign-up.</summary>
    public Visibility UnverifiedVisibility => EmailConfirmed ? Visibility.Collapsed : Visibility.Visible;

    public string RowTooltip =>
        $"{Username}{(IsOwner ? " · owner" : "")}{(IsMe ? " · you" : "")}\n{JoinedLabel}";
}
