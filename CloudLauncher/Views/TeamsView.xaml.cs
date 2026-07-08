using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

public partial class TeamsView : Page
{
    private readonly MainWindow _shell;
    private readonly ObservableCollection<TeamRow> _rows = new();
    private TeamDetail? _selected;

    public TeamsView(MainWindow shell)
    {
        InitializeComponent();
        _shell = shell;
        TeamsList.ItemsSource = _rows;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        StatusLabel.Text = "";
        try
        {
            var teams = await App.State.Api.ListTeamsAsync();
            _rows.Clear();
            foreach (var t in teams.OrderBy(t => t.Name)) _rows.Add(new TeamRow(t));
            TeamsEmptyLabel.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ApplyDetail(null);
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();

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
            await App.State.Api.CreateTeamAsync(new CreateTeamRequest(name));
            NewTeamBox.Text = "";
            await RefreshAsync();
            await _shell.RefreshPackTeamFoldersAsync();
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private async void OnTeamSelected(object sender, SelectionChangedEventArgs e)
    {
        if (TeamsList.SelectedItem is TeamRow row)
        {
            try { ApplyDetail(await App.State.Api.GetTeamAsync(row.Source.Id)); }
            catch (Exception ex) { StatusLabel.Text = ex.Message; }
        }
        else ApplyDetail(null);
    }

    private void ApplyDetail(TeamDetail? detail)
    {
        _selected = detail;
        if (detail is null)
        {
            DetailHeader.Text = "Select a team";
            MembersList.ItemsSource = null;
            AddMemberButton.IsEnabled = false;
            DeleteTeamButton.IsEnabled = false;
            return;
        }
        DetailHeader.Text = $"{detail.Name} — {detail.Members.Count} member(s)";
        MembersList.ItemsSource = detail.Members;
        var isOwner = detail.OwnerId == App.State.Settings.UserId;
        AddMemberButton.IsEnabled = isOwner;
        DeleteTeamButton.IsEnabled = isOwner;
    }

    private async void OnAddMember(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var name = NewMemberBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;
        try
        {
            await App.State.Api.AddTeamMemberAsync(_selected.Id, new AddTeamMemberRequest(name));
            NewMemberBox.Text = "";
            ApplyDetail(await App.State.Api.GetTeamAsync(_selected.Id));
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private async void OnRemoveMember(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        if (sender is FrameworkElement el && el.DataContext is UserSummary u)
        {
            if (MessageBox.Show(_shell, $"Remove {u.Username} from team '{_selected.Name}'?",
                    "Remove member", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            try
            {
                await App.State.Api.RemoveTeamMemberAsync(_selected.Id, u.Id);
                ApplyDetail(await App.State.Api.GetTeamAsync(_selected.Id));
            }
            catch (Exception ex) { StatusLabel.Text = ex.Message; }
        }
    }

    private async void OnDeleteTeam(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        if (MessageBox.Show(_shell, $"Delete team '{_selected.Name}'?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            await App.State.Api.DeleteTeamAsync(_selected.Id);
            await RefreshAsync();
            await _shell.RefreshPackTeamFoldersAsync();
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }
}

public sealed class TeamRow(TeamSummary src)
{
    public TeamSummary Source { get; } = src;
    public string Name => Source.Name;
    public string OwnerLabel => $"by {Source.OwnerUsername}";
    public string MemberCountLabel => $"{Source.MemberCount} member(s)";
}
