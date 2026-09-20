using System.Windows.Controls;

namespace CloudLauncher.Views;

/// <summary>
/// The Config and scripts page: one place to read, edit, compare and copy the config files and KubeJS
/// scripts that live inside each instance, instead of opening each instance's folder in turn.
/// </summary>
public partial class ConfigHubView : Page
{
    private readonly MainWindow _shell;

    public ConfigHubView(MainWindow shell)
    {
        InitializeComponent();
        _shell = shell;
    }
}
