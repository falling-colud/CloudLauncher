using System.Windows;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

public partial class UpdateDialog : Window
{
    private readonly LauncherReleaseInfo _release;
    private bool _applying;

    public UpdateDialog(LauncherReleaseInfo release)
    {
        _release = release;
        InitializeComponent();

        VersionLabel.Text =
            $"A new version of CloudLauncher is available.\n" +
            $"Installed: {AppVersion.CurrentString}   →   Latest: {release.Version}";

        NotesLabel.Text = string.IsNullOrWhiteSpace(release.Notes)
            ? "No release notes were provided for this update."
            : release.Notes;
    }

    private async void OnUpdate(object sender, RoutedEventArgs e)
    {
        if (_applying) return;
        _applying = true;

        UpdateButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        ProgressArea.Visibility = Visibility.Visible;
        ProgressLabel.Text = "Downloading update…";

        var progress = new Progress<double>(p =>
        {
            DownloadBar.Value = p;
            ProgressLabel.Text = $"Downloading update… {p * 100:0}%";
        });

        try
        {
            await App.State.Update.DownloadAndApplyAsync(progress);
            ProgressLabel.Text = "Restarting to finish the update…";
            // The external updater is now waiting for this process to exit.
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _applying = false;
            ProgressArea.Visibility = Visibility.Collapsed;
            UpdateButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
            MessageBox.Show(this,
                "The update could not be installed:\n\n" + ex.Message,
                "CloudLauncher", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnLater(object sender, RoutedEventArgs e)
    {
        if (_applying) return;
        DialogResult = false;
        Close();
    }
}
