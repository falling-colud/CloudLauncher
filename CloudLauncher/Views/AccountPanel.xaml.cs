using System.Windows;
using System.Windows.Controls;

namespace CloudLauncher.Views;

public partial class AccountPanel : Page
{
    private readonly MainWindow _shell;

    public AccountPanel(MainWindow shell)
    {
        InitializeComponent();
        _shell = shell;
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        var s = App.State.Settings;
        var username = s.Username ?? "Signed out";
        UsernameLabel.Text = username;
        ProfileInitial.Text = !string.IsNullOrEmpty(s.Username) ? char.ToUpper(s.Username[0]).ToString() : "?";

        var acc = App.State.MinecraftAccounts.Current;
        McAccountLine.Text = acc is null ? "Not signed in" : acc.Username;
        McAccountKind.Text = acc is null ? "Click Manage to sign in" : $"{acc.Kind} account";
    }

    private void OnManageMcAccount(object sender, RoutedEventArgs e)
    {
        _shell.OpenMcAccount();
        Refresh();
    }

    private void OnSignOut(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(_shell, "Sign out of CloudLauncher?", "Confirm",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        App.State.Api.ClearTokens();
        _shell.NavigateToLogin();
    }
}
