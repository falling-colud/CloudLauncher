using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// The sharing screen for an instance, a hosted mod, a shared world or a hosted resource pack.
/// </summary>
/// <remarks>
/// <para>What differs between the kinds is in a <see cref="PermissionTarget"/> built by the public
/// constructors, so a new shareable kind only needs a constructor.</para>
/// <para>Tick boxes save as soon as they change. A failed save puts the row back to the value the
/// server still holds and says so.</para>
/// <para>The window is modal, so it hosts its own <see cref="IDialogHost"/> overlay: a confirmation on
/// the main window would appear behind it.</para>
/// </remarks>
public partial class PermissionsDialog : Window, IDialogHost
{
    private readonly PermissionTarget _target;
    private readonly ObservableCollection<CollaboratorRow> _collaborators = new();
    private readonly ObservableCollection<TeamPermRow> _teams = new();
    private List<TeamSummary> _availableTeams = new();

    /// <summary>Serialises the per-row saves so two quick ticks cannot race each other into the
    /// wrong final state on the server.</summary>
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    /// <summary>Set once the close has been approved, so the Closing handler does not ask twice.</summary>
    private bool _closeApproved;

    /// <summary>Each control's own tooltip, so the offline one can be put back where it was.</summary>
    private readonly Dictionary<FrameworkElement, object?> _tips = new();

    /// <summary>Waits for typing to stop before asking the server who matches. 450 ms, like the other
    /// search boxes that hit the network.</summary>
    private DispatcherTimer? _suggestTimer;
    private CancellationTokenSource? _suggestCts;

    /// <summary>The permission presets offered when adding someone. The tick boxes cover anything
    /// finer.</summary>
    /// <remarks>Least access first, worded the same as the instance's Share tab.</remarks>
    public static IReadOnlyList<PermissionChoice> PermissionChoices { get; } =
    [
        new("Can view", PackPermissions.View),
        new("Can view and download", PackPermissions.ReadOnly),
        new("Can upload changes", PackPermissions.Contributor),
        new("Can manage sharing", PackPermissions.Full),
    ];

    /// <summary>Index of the preset a new row starts on (view and download).</summary>
    private const int DefaultChoiceIndex = 1;

    public PermissionsDialog(PackDetail pack) : this(new PermissionTarget(
        "instance",
        pack.Id,
        pack.Collaborators,
        pack.Teams,
        (username, permissions) => App.State.Api.AddCollaboratorAsync(pack.Id, new AddCollaboratorRequest(username, permissions)),
        (userId, permissions) => App.State.Api.UpdateCollaboratorAsync(pack.Id, userId, new UpdateCollaboratorRequest(permissions)),
        userId => App.State.Api.RemoveCollaboratorAsync(pack.Id, userId),
        (teamId, permissions) => App.State.Api.AddPackTeamAsync(pack.Id, new AddPackTeamRequest(teamId, permissions)),
        (teamId, permissions) => App.State.Api.UpdatePackTeamAsync(pack.Id, teamId, new UpdatePackTeamRequest(permissions)),
        teamId => App.State.Api.RemovePackTeamAsync(pack.Id, teamId)))
    {
    }

    public PermissionsDialog(HostedModDetail mod) : this(new PermissionTarget(
        "mod",
        mod.Id,
        mod.Collaborators,
        mod.Teams,
        (username, permissions) => App.State.Api.AddModCollaboratorAsync(mod.Id, new AddCollaboratorRequest(username, permissions)),
        (userId, permissions) => App.State.Api.UpdateModCollaboratorAsync(mod.Id, userId, new UpdateCollaboratorRequest(permissions)),
        userId => App.State.Api.RemoveModCollaboratorAsync(mod.Id, userId),
        (teamId, permissions) => App.State.Api.AddModTeamAsync(mod.Id, new AddPackTeamRequest(teamId, permissions)),
        (teamId, permissions) => App.State.Api.UpdateModTeamAsync(mod.Id, teamId, new UpdatePackTeamRequest(permissions)),
        teamId => App.State.Api.RemoveModTeamAsync(mod.Id, teamId)))
    {
    }

    public PermissionsDialog(SharedWorldDetail world) : this(new PermissionTarget(
        "world",
        world.Id,
        world.Collaborators,
        world.Teams,
        (username, permissions) => App.State.Api.AddSharedWorldCollaboratorAsync(world.Id, new AddCollaboratorRequest(username, permissions)),
        (userId, permissions) => App.State.Api.UpdateSharedWorldCollaboratorAsync(world.Id, userId, new UpdateCollaboratorRequest(permissions)),
        userId => App.State.Api.RemoveSharedWorldCollaboratorAsync(world.Id, userId),
        (teamId, permissions) => App.State.Api.AddSharedWorldTeamAsync(world.Id, new AddPackTeamRequest(teamId, permissions)),
        (teamId, permissions) => App.State.Api.UpdateSharedWorldTeamAsync(world.Id, teamId, new UpdatePackTeamRequest(permissions)),
        teamId => App.State.Api.RemoveSharedWorldTeamAsync(world.Id, teamId)))
    {
    }

    public PermissionsDialog(HostedResourcePackDetail resourcePack) : this(new PermissionTarget(
        "resource pack",
        resourcePack.Id,
        resourcePack.Collaborators,
        resourcePack.Teams,
        (username, permissions) => App.State.Api.AddResourcePackCollaboratorAsync(resourcePack.Id, new AddCollaboratorRequest(username, permissions)),
        (userId, permissions) => App.State.Api.UpdateResourcePackCollaboratorAsync(resourcePack.Id, userId, new UpdateCollaboratorRequest(permissions)),
        userId => App.State.Api.RemoveResourcePackCollaboratorAsync(resourcePack.Id, userId),
        (teamId, permissions) => App.State.Api.AddResourcePackTeamAsync(resourcePack.Id, new AddPackTeamRequest(teamId, permissions)),
        (teamId, permissions) => App.State.Api.UpdateResourcePackTeamAsync(resourcePack.Id, teamId, new UpdatePackTeamRequest(permissions)),
        teamId => App.State.Api.RemoveResourcePackTeamAsync(resourcePack.Id, teamId)))
    {
    }

    private PermissionsDialog(PermissionTarget target)
    {
        InitializeComponent();
        _target = target;
        Title = $"{target.DisplayName} permissions";
        NewCollaboratorPermissionBox.SelectedIndex = DefaultChoiceIndex;
        NewTeamPermissionBox.SelectedIndex = DefaultChoiceIndex;

        foreach (var c in target.Collaborators)
            Track(new CollaboratorRow(c.UserId, c.Username, c.Permissions), _collaborators);
        foreach (var t in target.Teams)
            Track(new TeamPermRow(t.TeamId, t.TeamName, t.Permissions, t.MemberCount), _teams);

        CollaboratorsGrid.ItemsSource = _collaborators;
        TeamsGrid.ItemsSource = _teams;
        _collaborators.CollectionChanged += OnRowsChanged;
        _teams.CollectionChanged += OnRowsChanged;
        UpdateEmptyStates();

        App.State.ConnectivityChanged += ApplyConnectivity;
        Closed += (_, _) =>
        {
            App.State.ConnectivityChanged -= ApplyConnectivity;
            _suggestTimer?.Stop();
            try { _suggestCts?.Cancel(); } catch (ObjectDisposedException) { /* already finished */ }
        };

        Loaded += async (_, _) =>
        {
            ApplyConnectivity();
            // Offline, the banner already explains why; don't add a second error about the team list.
            if (!App.State.IsOffline) await LoadTeamsAsync();
        };
    }

    // ── offline ──

    /// <summary>
    /// Every control here makes a live server call, so offline they are all disabled.
    /// </summary>
    /// <remarks>Disabled rather than hidden, with <see cref="ToolTipService.SetShowOnDisabled"/> so the
    /// tooltip explaining why still shows.</remarks>
    private void ApplyConnectivity()
    {
        var offline = App.State.IsOffline;
        var why = "The server is not answering"
                + (App.State.OfflineReason is { Length: > 0 } r ? $" ({r})" : "")
                + ", so permissions can be read here but not changed.";

        OfflineNoteText.Text = why + " The rows below are the last answer the server gave.";
        OfflineNote.Visibility = offline ? Visibility.Visible : Visibility.Collapsed;

        foreach (var element in new FrameworkElement[]
                 {
                     CollaboratorsGrid, TeamsGrid, NewCollaboratorBox, NewCollaboratorPermissionBox,
                     AddCollaboratorButton, AvailableTeamsBox, NewTeamPermissionBox, AddTeamButton,
                     RetryButton
                 })
        {
            // Remember the control's own tooltip so it can be put back once online again.
            if (!_tips.ContainsKey(element)) _tips[element] = element.ToolTip;
            element.IsEnabled = !offline;
            element.ToolTip = offline ? why : _tips[element];
            ToolTipService.SetShowOnDisabled(element, true);
        }

        if (offline)
        {
            SuggestionList.Visibility = Visibility.Collapsed;
            RetryButton.IsEnabled = false;
        }
        else if (_availableTeams.Count == 0)
        {
            // Back online after opening offline: the team picker never got its list.
            _ = LoadTeamsAsync();
        }
    }

    /// <summary>Adds a row to its collection and subscribes to its edits so the tick saves itself.</summary>
    private void Track<T>(T row, ObservableCollection<T> into) where T : PermRowBase
    {
        row.PermissionsEdited += OnRowEdited;
        into.Add(row);
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyStates();

    private void UpdateEmptyStates()
    {
        CollaboratorsEmptyLabel.Visibility = _collaborators.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TeamsEmptyLabel.Visibility = _teams.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── status ──

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

    /// <summary>
    /// Puts a refusal on screen in words, and the exception itself in the launcher log.
    /// </summary>
    /// <remarks>
    /// <see cref="ShareText.Explain"/> turns the server's error into a sentence, with a fallback for an
    /// empty body (a 403 from <c>Forbid()</c>, for example).
    /// </remarks>
    private void Report(string category, Exception ex, string? what)
    {
        AppLog.LogError(category, ex);
        var sentence = ShareText.Explain(ex);
        Fail(what is { Length: > 0 } w ? $"{w}: {sentence}" : sentence);
    }

    /// <summary>Rows whose last edit the server refused, so the grid and the server disagree.</summary>
    private IEnumerable<PermRowBase> Unsaved =>
        _collaborators.Cast<PermRowBase>().Concat(_teams).Where(r => r.IsDirty);

    private void RefreshRetryButton() =>
        RetryButton.Visibility = Unsaved.Any() ? Visibility.Visible : Visibility.Collapsed;

    // ── teams available to add ──

    private async Task LoadTeamsAsync()
    {
        try
        {
            _availableTeams = await App.State.Api.ListTeamsAsync();

            // Older servers send team grants without a member count. The teams list has it for any team
            // the caller is on, so fill it in from there.
            foreach (var row in _teams)
                if (_availableTeams.FirstOrDefault(t => t.Id == row.TeamId) is { } known)
                    row.SetMemberCount(known.MemberCount);

            var taken = new HashSet<Guid>(_teams.Select(t => t.TeamId));
            AvailableTeamsBox.ItemsSource = _availableTeams.Where(t => !taken.Contains(t.Id)).ToList();
            if (AvailableTeamsBox.Items.Count > 0 && AvailableTeamsBox.SelectedIndex < 0)
                AvailableTeamsBox.SelectedIndex = 0;
        }
        catch (Exception ex) { Report("permissions.teams", ex, "Could not list your teams"); }
    }

    // ── collaborators ──

    private void OnNewCollaboratorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        OnAddCollaborator(sender, e);
    }

    // ── who do you mean? ──

    /// <summary>
    /// Asks the server which accounts start with what has been typed, once typing stops.
    /// </summary>
    /// <remarks>
    /// <c>GET /users?q=</c> is rate-limited and prefix-only, so this waits for a pause in the typing and
    /// never fires on a single character.
    /// </remarks>
    private void OnNewCollaboratorTextChanged(object sender, TextChangedEventArgs e)
    {
        _suggestTimer ??= NewSuggestTimer();
        _suggestTimer.Stop();
        if (NewCollaboratorBox.Text.Trim().Length < 2)
        {
            SuggestionList.Visibility = Visibility.Collapsed;
            return;
        }
        _suggestTimer.Start();
    }

    private DispatcherTimer NewSuggestTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        timer.Tick += (_, _) => { timer.Stop(); _ = SuggestAsync(NewCollaboratorBox.Text.Trim()); };
        return timer;
    }

    private async Task SuggestAsync(string query)
    {
        if (query.Length < 2 || App.State.IsOffline) return;
        try { _suggestCts?.Cancel(); } catch (ObjectDisposedException) { /* already finished */ }
        var cts = new CancellationTokenSource();
        _suggestCts = cts;
        try
        {
            var page = await ShareApi.SearchUsersAsync(query, cts.Token);
            if (cts.IsCancellationRequested) return;

            var already = new HashSet<string>(_collaborators.Select(c => c.Username), StringComparer.OrdinalIgnoreCase);
            var matches = page.Items
                .Where(u => !already.Contains(u.Username))
                .Where(u => !string.Equals(u.Username, query, StringComparison.OrdinalIgnoreCase))
                .Take(8).ToList();

            SuggestionList.ItemsSource = matches;
            SuggestionList.Visibility = matches.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // A failed lookup must not stop somebody typing a name they already know.
            AppLog.LogError("permissions.usersearch", ex);
            SuggestionList.Visibility = Visibility.Collapsed;
        }
        finally { cts.Dispose(); }
    }

    private void OnPickSuggestion(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: UserSummary user }) return;
        NewCollaboratorBox.Text = user.Username;
        NewCollaboratorBox.CaretIndex = NewCollaboratorBox.Text.Length;
        SuggestionList.Visibility = Visibility.Collapsed;
        NewCollaboratorBox.Focus();
    }

    private async void OnAddCollaborator(object sender, RoutedEventArgs e)
    {
        var name = NewCollaboratorBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (_collaborators.Any(c => string.Equals(c.Username, name, StringComparison.OrdinalIgnoreCase)))
        {
            Fail($"{name} already has access - change the tick boxes on their row instead.");
            return;
        }

        var permissions = SelectedPermissions(NewCollaboratorPermissionBox);
        try
        {
            var added = await _target.AddCollaboratorAsync(name, permissions);
            Track(new CollaboratorRow(added.UserId, added.Username, added.Permissions), _collaborators);
            NewCollaboratorBox.Text = "";
            Okay($"{added.Username} can now {Describe(added.Permissions)}.");
        }
        catch (Exception ex) { Report("permissions.add", ex, "Could not add " + name); }
    }

    private async void OnRemoveCollaborator(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CollaboratorRow row }) return;
        try
        {
            if (!await AppDialog.ConfirmAsync(this, "Remove collaborator",
                    $"Remove {row.Username}'s access to this {_target.DisplayName}?\n\n"
                    + "They will no longer be able to see or download it.",
                    "Remove", "Cancel", danger: true))
                return;

            await _target.RemoveCollaboratorAsync(row.UserId);
            row.PermissionsEdited -= OnRowEdited;
            _collaborators.Remove(row);
            RefreshRetryButton();
            Okay($"Removed {row.Username}.");
        }
        catch (Exception ex) { Report("permissions.remove", ex, "Could not remove " + row.Username); }
    }

    // ── teams ──

    private async void OnAddTeam(object sender, RoutedEventArgs e)
    {
        if (AvailableTeamsBox.SelectedItem is not TeamSummary t)
        {
            Fail("Choose a team first.");
            return;
        }

        var permissions = SelectedPermissions(NewTeamPermissionBox);
        try
        {
            var added = await _target.AddTeamAsync(t.Id, permissions);
            Track(new TeamPermRow(added.TeamId, added.TeamName, added.Permissions,
                added.MemberCount > 0 ? added.MemberCount : t.MemberCount), _teams);
            await LoadTeamsAsync();
            Okay($"{added.TeamName} ({TeamsView.Plural(t.MemberCount, "member", "members")}) can now {Describe(added.Permissions)}.");
        }
        catch (Exception ex) { Report("permissions.addteam", ex, $"Could not share with {t.Name}"); }
    }

    private async void OnRemoveTeam(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TeamPermRow row }) return;
        try
        {
            var members = _availableTeams.FirstOrDefault(t => t.Id == row.TeamId)?.MemberCount;
            var who = members is { } n
                ? $"All {TeamsView.Plural(n, "member", "members")} of {row.TeamName} lose access"
                : $"Every member of {row.TeamName} loses access";

            if (!await AppDialog.ConfirmAsync(this, "Remove team access",
                    $"{who} to this {_target.DisplayName}.\n\n"
                    + "Anyone who was also added individually keeps that access.",
                    "Remove", "Cancel", danger: true))
                return;

            await _target.RemoveTeamAsync(row.TeamId);
            row.PermissionsEdited -= OnRowEdited;
            _teams.Remove(row);
            RefreshRetryButton();
            await LoadTeamsAsync();
            Okay($"Removed {row.TeamName}.");
        }
        catch (Exception ex) { Report("permissions.removeteam", ex, $"Could not remove {row.TeamName}"); }
    }

    // ── saving ──

    /// <summary>Pushes one row's new permissions as soon as a tick box changes it.</summary>
    /// <remarks>
    /// <see langword="async void"/> because it is an event handler, so everything inside is guarded. A
    /// refusal puts the row back to the value the server still holds.
    /// </remarks>
    private async void OnRowEdited(PermRowBase row)
    {
        // A grant without View gives no usable access, and the server refuses it. Say so before the
        // round trip and point at Remove instead.
        if (!row.View)
        {
            // Put back after the tick box's own binding update has finished: this runs inside it,
            // and a value changed back mid-update is not reliably re-read by the grid's cell.
            _ = Dispatcher.BeginInvoke(new Action(row.RevertToSaved), DispatcherPriority.Background);
            Fail($"{row.DisplayName} needs at least View to have any access. To take their access "
                 + "away, use Remove instead.");
            return;
        }

        try { await SaveRowAsync(row, revertOnFailure: true); }
        catch (Exception ex) { Report("permissions", ex, null); }
    }

    private async Task SaveRowAsync(PermRowBase row, bool revertOnFailure)
    {
        var wanted = row.Effective;
        await _saveGate.WaitAsync();
        try
        {
            switch (row)
            {
                case CollaboratorRow c: await _target.UpdateCollaboratorAsync(c.UserId, wanted); break;
                case TeamPermRow t: await _target.UpdateTeamAsync(t.TeamId, wanted); break;
            }
            row.MarkSaved(wanted);
            Okay($"{row.DisplayName} can {Describe(wanted)}.");
        }
        catch (Exception ex)
        {
            if (revertOnFailure) row.RevertToSaved();
            Report("permissions.save", ex, row.DisplayName);
        }
        finally
        {
            _saveGate.Release();
            RefreshRetryButton();
        }
    }

    /// <summary>Re-sends the rows a previous save left out of step with the server.</summary>
    private async void OnRetry(object sender, RoutedEventArgs e)
    {
        try
        {
            RetryButton.IsEnabled = false;
            // No revert here; it would undo what the user is retrying.
            foreach (var row in Unsaved.ToList()) await SaveRowAsync(row, revertOnFailure: false);
            if (!Unsaved.Any()) Okay("All changes saved.");
        }
        catch (Exception ex) { Report("permissions", ex, null); }
        finally { RetryButton.IsEnabled = true; }
    }

    // ── closing ──

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Asks before closing while the grid still shows access the server did not accept.
    /// </summary>
    /// <remarks>Cancelling the close and re-running it asynchronously is the only way to await a
    /// confirmation from <see cref="Window.Closing"/>, which cannot itself be awaited.</remarks>
    private async void OnWindowClosing(object sender, CancelEventArgs e)
    {
        if (_closeApproved) return;
        var pending = Unsaved.ToList();
        if (pending.Count == 0) return;

        e.Cancel = true;
        try
        {
            var names = string.Join(", ", pending.Select(r => r.DisplayName));
            var discard = await AppDialog.ConfirmAsync(this, "Changes not saved",
                $"The server refused the last change to {names}. Closing now leaves the permissions "
                + "as they were.\n\nUse \"Retry unsaved changes\" to send them again.",
                "Close anyway", "Keep this open", danger: true);
            if (!discard) return;
            _closeApproved = true;
            Close();
        }
        catch (Exception ex) { Report("permissions", ex, null); }
    }

    // ── helpers ──

    private static PackPermissions SelectedPermissions(ComboBox box) =>
        box.SelectedItem is PermissionChoice choice ? choice.Value : PackPermissions.ReadOnly;

    /// <summary>Puts a permission set into the words the dialog's own tick boxes use.</summary>
    internal static string Describe(PackPermissions p)
    {
        var parts = new List<string>();
        if (p.HasFlag(PackPermissions.View)) parts.Add("view");
        if (p.HasFlag(PackPermissions.Download)) parts.Add("download");
        if (p.HasFlag(PackPermissions.UploadShared)) parts.Add("upload");
        if (p.HasFlag(PackPermissions.ManageCollaborators)) parts.Add("manage sharing");
        return parts.Count == 0 ? "do nothing" : string.Join(", ", parts);
    }

    // ── in-window dialog host ──

    public Task<bool> ShowConfirmAsync(string title, string message,
        string confirmText = "Yes", string cancelText = "Cancel", bool danger = false)
    {
        var overlay = new DialogOverlay();
        overlay.Configure(title, message, confirmText, cancelText, danger);
        return ShowOverlayAsync(overlay);
    }

    public Task ShowMessageAsync(string title, string message, string okText = "OK")
    {
        var overlay = new DialogOverlay();
        overlay.Configure(title, message, okText, null, false);
        return ShowOverlayAsync(overlay);
    }

    private async Task<bool> ShowOverlayAsync(DialogOverlay overlay)
    {
        DialogLayer.Children.Add(overlay);
        DialogLayer.Visibility = Visibility.Visible;
        try { return await overlay.Result; }
        finally
        {
            DialogLayer.Children.Remove(overlay);
            if (DialogLayer.Children.Count == 0) DialogLayer.Visibility = Visibility.Collapsed;
        }
    }
}

/// <summary>One entry in the "what may they do?" picker shown beside the Add buttons.</summary>
public sealed record PermissionChoice(string Label, PackPermissions Value);

public sealed class CollaboratorRow : PermRowBase
{
    public Guid UserId { get; }
    public string Username { get; }
    public override string DisplayName => Username;
    public CollaboratorRow(Guid id, string name, PackPermissions p) : base(p) { UserId = id; Username = name; }
}

public sealed class TeamPermRow : PermRowBase
{
    public Guid TeamId { get; }
    public string TeamName { get; }
    public override string DisplayName => TeamName;

    /// <summary>How many people this row grants access to, or 0 when the server did not say.</summary>
    public int MemberCount { get; private set; }

    /// <summary>The count as the grid shows it: a dash rather than a misleading zero.</summary>
    public string MembersLabel => MemberCount > 0 ? MemberCount.ToString("N0") : "-";

    public TeamPermRow(Guid id, string name, PackPermissions p, int memberCount = 0) : base(p)
    {
        TeamId = id;
        TeamName = name;
        MemberCount = memberCount;
    }

    /// <summary>Fills in a count that arrived after the row was built (from the team list load).</summary>
    public void SetMemberCount(int count)
    {
        if (count <= 0 || count == MemberCount) return;
        MemberCount = count;
        Raise(nameof(MemberCount));
        Raise(nameof(MembersLabel));
    }
}

internal sealed record PermissionTarget(
    string DisplayName,
    Guid Id,
    IReadOnlyList<PackCollaboratorEntry> Collaborators,
    IReadOnlyList<PackTeamEntry> Teams,
    Func<string, PackPermissions, Task<PackCollaboratorEntry>> AddCollaboratorAsync,
    Func<Guid, PackPermissions, Task> UpdateCollaboratorAsync,
    Func<Guid, Task> RemoveCollaboratorAsync,
    Func<Guid, PackPermissions, Task<PackTeamEntry>> AddTeamAsync,
    Func<Guid, PackPermissions, Task> UpdateTeamAsync,
    Func<Guid, Task> RemoveTeamAsync);

/// <summary>
/// The four permission tick boxes of one grid row, plus the value the server is known to hold.
/// </summary>
/// <remarks>
/// <see cref="Saved"/> lets a refused save go back to the last state the server acknowledged.
/// <see cref="PermissionsEdited"/> doesn't fire during a revert, so a revert can't trigger another save.
/// </remarks>
public abstract class PermRowBase : INotifyPropertyChanged
{
    private bool _view, _download, _upload, _manage;
    private bool _quiet;

    protected PermRowBase(PackPermissions p)
    {
        _quiet = true;
        try { Apply(p); }
        finally { _quiet = false; }
        Saved = p;
    }

    /// <summary>The name to put in a status line or a confirmation about this row.</summary>
    public abstract string DisplayName { get; }

    /// <summary>The permissions the server last accepted for this row.</summary>
    public PackPermissions Saved { get; private set; }

    public bool IsDirty => Effective != Saved;

    /// <summary>Raised when the user changes a tick box, not when the row is rebuilt or reverted.</summary>
    public event Action<PermRowBase>? PermissionsEdited;

    public bool View { get => _view; set => Set(ref _view, value); }
    public bool Download { get => _download; set => Set(ref _download, value); }
    public bool UploadShared { get => _upload; set => Set(ref _upload, value); }
    public bool ManageCollaborators { get => _manage; set => Set(ref _manage, value); }

    public PackPermissions Effective =>
        (View ? PackPermissions.View : 0)
      | (Download ? PackPermissions.Download : 0)
      | (UploadShared ? PackPermissions.UploadShared : 0)
      | (ManageCollaborators ? PackPermissions.ManageCollaborators : 0);

    public void MarkSaved(PackPermissions value) => Saved = value;

    /// <summary>Puts the tick boxes back to <see cref="Saved"/> after a refused change.</summary>
    public void RevertToSaved()
    {
        _quiet = true;
        try { Apply(Saved); }
        finally { _quiet = false; }
    }

    private void Apply(PackPermissions p)
    {
        View = p.HasFlag(PackPermissions.View);
        Download = p.HasFlag(PackPermissions.Download);
        UploadShared = p.HasFlag(PackPermissions.UploadShared);
        ManageCollaborators = p.HasFlag(PackPermissions.ManageCollaborators);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raises the change notification for a property a subclass maintains itself.</summary>
    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (!_quiet) PermissionsEdited?.Invoke(this);
    }
}
