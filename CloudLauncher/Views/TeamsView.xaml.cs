using System.Windows;
using System.Windows.Controls;

namespace CloudLauncher.Views;

/// <summary>
/// The Teams side panel: page chrome around <see cref="SharingPeoplePanel"/>.
/// </summary>
/// <remarks>
/// The screen itself lives in the panel so the sharing hub can host it too. This page draws the
/// primary action in its header, so it sets <see cref="SharingPeoplePanel.HostProvidesPrimaryAction"/>.
/// </remarks>
public partial class TeamsView : Page
{
    private readonly SharingPeoplePanel _panel;

    public TeamsView(MainWindow shell)
    {
        InitializeComponent();
        _panel = new SharingPeoplePanel(shell) { HostProvidesPrimaryAction = true };
        _panel.PrimaryActionAvailabilityChanged += OnPrimaryAvailability;
        PanelHost.Children.Add(_panel);
    }

    /// <summary>
    /// Keeps the header's accent button in step with the panel's own gates.
    /// </summary>
    /// <remarks>Disabled rather than hidden, with the reason as its tooltip.
    /// <see cref="ToolTipService.SetShowOnDisabled"/> is needed for a disabled control's tooltip to
    /// show.</remarks>
    private void OnPrimaryAvailability(bool available, string? reason)
    {
        _ownTip ??= NewTeamButton.ToolTip;
        ToolTipService.SetShowOnDisabled(NewTeamButton, true);
        NewTeamButton.IsEnabled = available;
        NewTeamButton.ToolTip = available ? _ownTip : reason;
    }

    private object? _ownTip;

    private void OnNewTeam(object sender, RoutedEventArgs e) => _panel.BeginCreateTeam();

    /// <summary>
    /// Forwards to <see cref="SharingPeoplePanel"/>; the sharing dialogs call it by this name.
    /// </summary>
    public static string Plural(int n, string one, string many) =>
        SharingPeoplePanel.Plural(n, one, many);
}
