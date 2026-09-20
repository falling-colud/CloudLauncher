using System.Windows.Controls;

namespace CloudLauncher.Views;

/// <summary>
/// The Servers page: the multiplayer servers across every instance, their live status, and a console
/// for the ones the user administers.
/// </summary>
public partial class ServersView : Page
{
    private readonly MainWindow _shell;

    public ServersView(MainWindow shell)
    {
        InitializeComponent();
        _shell = shell;
    }
}
