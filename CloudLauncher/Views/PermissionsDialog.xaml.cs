using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

public partial class PermissionsDialog : Window
{
    private readonly PermissionTarget _target;
    private readonly ObservableCollection<CollaboratorRow> _collaborators = new();
    private readonly ObservableCollection<TeamPermRow> _teams = new();
    private List<TeamSummary> _availableTeams = new();

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
        foreach (var c in target.Collaborators)
            _collaborators.Add(new CollaboratorRow(c.UserId, c.Username, c.Permissions));
        foreach (var t in target.Teams)
            _teams.Add(new TeamPermRow(t.TeamId, t.TeamName, t.Permissions));
        CollaboratorsGrid.ItemsSource = _collaborators;
        TeamsGrid.ItemsSource = _teams;
        Loaded += async (_, _) => await LoadTeamsAsync();
    }

    private async Task LoadTeamsAsync()
    {
        try
        {
            _availableTeams = await App.State.Api.ListTeamsAsync();
            var taken = new HashSet<Guid>(_teams.Select(t => t.TeamId));
            AvailableTeamsBox.ItemsSource = _availableTeams.Where(t => !taken.Contains(t.Id)).ToList();
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private async void OnAddCollaborator(object sender, RoutedEventArgs e)
    {
        var name = NewCollaboratorBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;
        try
        {
            var added = await _target.AddCollaboratorAsync(name, PackPermissions.ReadOnly);
            _collaborators.Add(new CollaboratorRow(added.UserId, added.Username, added.Permissions));
            NewCollaboratorBox.Text = "";
            StatusLabel.Text = "";
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private async void OnRemoveCollaborator(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is CollaboratorRow row)
        {
            try
            {
                await _target.RemoveCollaboratorAsync(row.UserId);
                _collaborators.Remove(row);
            }
            catch (Exception ex) { StatusLabel.Text = ex.Message; }
        }
    }

    private async void OnAddTeam(object sender, RoutedEventArgs e)
    {
        if (AvailableTeamsBox.SelectedItem is not TeamSummary t) return;
        try
        {
            var added = await _target.AddTeamAsync(t.Id, PackPermissions.ReadOnly);
            _teams.Add(new TeamPermRow(added.TeamId, added.TeamName, added.Permissions));
            await LoadTeamsAsync();
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private async void OnRemoveTeam(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is TeamPermRow row)
        {
            try
            {
                await _target.RemoveTeamAsync(row.TeamId);
                _teams.Remove(row);
                await LoadTeamsAsync();
            }
            catch (Exception ex) { StatusLabel.Text = ex.Message; }
        }
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        StatusLabel.Text = "Saving...";
        try
        {
            foreach (var c in _collaborators)
                await _target.UpdateCollaboratorAsync(c.UserId, c.Effective);
            foreach (var t in _teams)
                await _target.UpdateTeamAsync(t.TeamId, t.Effective);
            StatusLabel.Text = "Saved.";
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

public sealed class CollaboratorRow : PermRowBase
{
    public Guid UserId { get; }
    public string Username { get; }
    public CollaboratorRow(Guid id, string name, PackPermissions p) : base(p) { UserId = id; Username = name; }
}

public sealed class TeamPermRow : PermRowBase
{
    public Guid TeamId { get; }
    public string TeamName { get; }
    public TeamPermRow(Guid id, string name, PackPermissions p) : base(p) { TeamId = id; TeamName = name; }
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

public abstract class PermRowBase : INotifyPropertyChanged
{
    private bool _view, _download, _upload, _manage;

    protected PermRowBase(PackPermissions p)
    {
        _view = p.HasFlag(PackPermissions.View);
        _download = p.HasFlag(PackPermissions.Download);
        _upload = p.HasFlag(PackPermissions.UploadShared);
        _manage = p.HasFlag(PackPermissions.ManageCollaborators);
    }

    public bool View { get => _view; set => Set(ref _view, value); }
    public bool Download { get => _download; set => Set(ref _download, value); }
    public bool UploadShared { get => _upload; set => Set(ref _upload, value); }
    public bool ManageCollaborators { get => _manage; set => Set(ref _manage, value); }

    public PackPermissions Effective =>
        (View ? PackPermissions.View : 0)
      | (Download ? PackPermissions.Download : 0)
      | (UploadShared ? PackPermissions.UploadShared : 0)
      | (ManageCollaborators ? PackPermissions.ManageCollaborators : 0);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
