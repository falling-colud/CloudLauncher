using System.Windows;

namespace CloudLauncher.Views;

public partial class MinecraftAccountDialog : Window
{
    public MinecraftAccountDialog()
    {
        InitializeComponent();
        Refresh();
    }

    private void Refresh()
    {
        var acc = App.State.MinecraftAccounts.Current;
        CurrentAccountLabel.Text = acc is null
            ? "Currently: not signed in"
            : $"Currently signed in as {acc.Username} ({acc.Kind})";
        SignOutButton.IsEnabled = acc is not null;
    }

    private async void OnMicrosoftSignIn(object sender, RoutedEventArgs e)
    {
        StatusLabel.Text = "";
        MicrosoftButton.IsEnabled = false;
        try
        {
            await App.State.MinecraftAccounts.AddMicrosoftAsync();
            Refresh();
            StatusLabel.Text = "Signed in.";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Sign-in failed: " + ex.Message;
        }
        finally
        {
            MicrosoftButton.IsEnabled = true;
        }
    }

    private void OnOffline(object sender, RoutedEventArgs e)
    {
        StatusLabel.Text = "";
        try
        {
            App.State.MinecraftAccounts.AddOffline(OfflineNameBox.Text);
            Refresh();
            StatusLabel.Text = "Offline username saved.";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = ex.Message;
        }
    }

    private async void OnSignOut(object sender, RoutedEventArgs e)
    {
        await App.State.MinecraftAccounts.SignOutAllAsync();
        Refresh();
        StatusLabel.Text = "Signed out.";
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
