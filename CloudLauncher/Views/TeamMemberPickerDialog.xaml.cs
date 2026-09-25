using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CloudLauncher.Animations;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>Who was chosen and, for an invitation, the role they join with.</summary>
/// <param name="UserId">Null when the name was typed rather than picked, which the server resolves.</param>
public sealed record PeoplePick(Guid? UserId, string Username, TeamRole Role);

/// <summary>One person in the picker's list.</summary>
public sealed class PeopleRow
{
    public PeopleRow(Guid? userId, string username, string detail)
    {
        UserId = userId;
        Username = username;
        Detail = detail;
    }

    public Guid? UserId { get; }
    public string Username { get; }
    public string Detail { get; }
}

/// <summary>
/// The people picker: choose somebody already on a team, or find somebody to invite.
/// </summary>
/// <remarks>
/// <para>One control for both, since only the source of names differs: team members, or a search
/// of <c>GET /users?q=</c>.</para>
/// <para>The search is debounced at 450ms because the server rate-limits it to 60 requests a minute
/// per address, and the list shows "Searching..." rather than "nobody found" while a request is in
/// flight.</para>
/// </remarks>
public partial class TeamMemberPickerDialog : UserControl
{
    private const int SearchDebounceMs = 450;

    private readonly TaskCompletionSource<PeoplePick?> _tcs = new();
    private readonly List<PeopleRow> _local = new();
    private readonly TeamsApi? _teams;
    private readonly HashSet<string> _exclude;
    private readonly bool _searchMode;

    private DispatcherTimer? _debounce;
    private CancellationTokenSource? _searchCts;
    private string _lastQuery = "";

    private TeamMemberPickerDialog(
        string title,
        string description,
        string actionText,
        string placeholder,
        IEnumerable<PeopleRow>? people,
        TeamsApi? teams,
        ISet<string>? exclude,
        bool showRole,
        bool canPickAdmin)
    {
        InitializeComponent();
        TitleLabel.Text = title;
        DescriptionLabel.Text = description;
        ApplyButton.Content = actionText;
        SearchPlaceholder.Text = placeholder;

        _teams = teams;
        _searchMode = teams is not null;
        _exclude = new HashSet<string>(exclude ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);
        if (people is not null) _local.AddRange(people);

        RoleRow.Visibility = showRole ? Visibility.Visible : Visibility.Collapsed;
        if (showRole && !canPickAdmin)
        {
            // Admins may only invite as Member (the server enforces it), so they can't get around "only the
            // owner promotes".
            ((ComboBoxItem)RoleBox.Items[1]).IsEnabled = false;
            ((ComboBoxItem)RoleBox.Items[1]).ToolTip = "Only the team's owner can make somebody an admin.";
            ToolTipService.SetShowOnDisabled((ComboBoxItem)RoleBox.Items[1], true);
        }

        Loaded += (_, _) =>
        {
            Animate.SlideFadeIn(this, 0, 14, 200);
            SearchBox.Focus();
            if (_searchMode) ShowNote("Start typing a username. Two letters is enough.");
            else ApplyLocalFilter();
        };
        Unloaded += (_, _) => { _debounce?.Stop(); _searchCts?.Cancel(); };
    }

    public Task<PeoplePick?> Result => _tcs.Task;

    public void Cancel() => _tcs.TrySetResult(null);

    // ── entry points ─────────────────────────────────────────────────────────

    /// <summary>Picks one of a team's existing members, for ownership transfer.</summary>
    public static async Task<PeoplePick?> PickAsync(
        MainWindow host, IReadOnlyList<TeamMemberRow> members,
        string title, string description, string actionText)
    {
        var rows = members
            .OrderBy(m => m.Username, StringComparer.OrdinalIgnoreCase)
            .Select(m => new PeopleRow(m.UserId, m.Username,
                $"{SharingPeoplePanel.RoleWord(m.Role).ToLowerInvariant()} · {m.JoinedLabel}"))
            .ToList();

        var card = new TeamMemberPickerDialog(title, description, actionText,
            "Filter members", rows, teams: null, exclude: null, showRole: false, canPickAdmin: false);
        await host.ShowCardAsync(card, card.Result, card.Cancel);
        return card.Result.Result;
    }

    /// <summary>Finds somebody to invite to a team, by username prefix.</summary>
    /// <param name="alreadyOn">People already on the team. Still listed, marked as already on it.</param>
    public static async Task<PeoplePick?> InviteAsync(
        MainWindow host, TeamsApi teams, string teamName, ISet<string> alreadyOn, bool canPickAdmin)
    {
        var card = new TeamMemberPickerDialog(
            $"Invite to {teamName}",
            "They get an invitation and decide for themselves. Nobody is added to a team without saying yes.",
            "Send invitation",
            "Search for a username",
            people: null, teams: teams, exclude: alreadyOn,
            showRole: true, canPickAdmin: canPickAdmin);
        await host.ShowCardAsync(card, card.Result, card.Cancel);
        return card.Result.Result;
    }

    // ── searching ────────────────────────────────────────────────────────────

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_searchMode) RestartDebounce();
        else ApplyLocalFilter();
    }

    private void RestartDebounce()
    {
        _debounce ??= NewTimer();
        _debounce.Stop();
        _debounce.Start();
    }

    private DispatcherTimer NewTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SearchDebounceMs) };
        timer.Tick += (_, _) => { timer.Stop(); _ = SearchAsync(); };
        return timer;
    }

    private void ApplyLocalFilter()
    {
        var q = SearchBox.Text.Trim();
        var rows = q.Length == 0
            ? _local
            : _local.Where(r => r.Username.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        Show(rows, q.Length == 0
            ? "Nobody else is on this team yet."
            : "Nobody on this team matches that.");
    }

    private async Task SearchAsync()
    {
        if (_teams is null) return;
        var q = SearchBox.Text.Trim();
        if (string.Equals(q, _lastQuery, StringComparison.Ordinal)) return;
        _lastQuery = q;

        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        if (q.Length < 2)
        {
            PeopleList.ItemsSource = null;
            ShowNote("Start typing a username. Two letters is enough.");
            return;
        }

        ShowNote("Searching...");
        try
        {
            var page = await _teams.SearchUsersAsync(q, cts.Token);
            if (cts.IsCancellationRequested) return;
            var rows = page.Items
                .Select(u => new PeopleRow(u.Id, u.Username,
                    _exclude.Contains(u.Username) ? "already on this team" : "CloudLauncher account"))
                .ToList();
            Show(rows, $"No account starts with '{q}'. Usernames are matched from the start, "
                       + "so try fewer letters.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            AppLog.LogError("teams", ex);
            PeopleList.ItemsSource = null;
            ShowNote("That search did not work. " + TeamsApi.Explain(ex, "The server did not answer.")
                     + " You can still type the username exactly and send the invitation.");
        }
    }

    private void Show(IReadOnlyList<PeopleRow> rows, string emptyText)
    {
        PeopleList.ItemsSource = rows;
        PeopleList.SelectedIndex = rows.Count > 0 ? 0 : -1;
        if (rows.Count == 0) ShowNote(emptyText);
        else ListNote.Visibility = Visibility.Collapsed;
    }

    private void ShowNote(string text)
    {
        ListNote.Text = text;
        ListNote.Visibility = Visibility.Visible;
    }

    // ── choosing ─────────────────────────────────────────────────────────────

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && PeopleList.Items.Count > 0)
        {
            if (PeopleList.SelectedIndex < 0) PeopleList.SelectedIndex = 0;
            PeopleList.UpdateLayout();
            (PeopleList.ItemContainerGenerator.ContainerFromIndex(PeopleList.SelectedIndex) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            // Enter must not wait out the debounce, or the first press searches and the second one
            // is the one that picks.
            _debounce?.Stop();
            if (_searchMode && PeopleList.SelectedItem is null) { _ = SearchAsync(); return; }
            Accept();
        }
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Use the row under the pointer, not the selection; otherwise a double-click on the empty
        // space below the list would pick the selection.
        for (var d = e.OriginalSource as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is ListBoxItem) { Accept(); return; }
            if (d is ListBox) return;
        }
    }

    private void OnApply(object sender, RoutedEventArgs e) => Accept();

    private void OnCancel(object sender, RoutedEventArgs e) => _tcs.TrySetResult(null);

    private void Accept()
    {
        var role = RoleRow.Visibility == Visibility.Visible && RoleBox.SelectedIndex == 1
            ? TeamRole.Admin
            : TeamRole.Member;

        if (PeopleList.SelectedItem is PeopleRow row)
        {
            _tcs.TrySetResult(new PeoplePick(row.UserId, row.Username, role));
            return;
        }

        // Nothing matched: send what was typed and let the server say whether that account exists,
        // since the prefix search may not surface every name.
        if (!_searchMode) return;
        var typed = SearchBox.Text.Trim();
        if (typed.Length == 0) return;
        _tcs.TrySetResult(new PeoplePick(null, typed, role));
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _tcs.TrySetResult(null); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }
}
