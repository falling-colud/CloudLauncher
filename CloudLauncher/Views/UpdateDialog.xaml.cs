using System.Diagnostics;
using System.Windows;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

public partial class UpdateDialog : Window
{
    private readonly LauncherReleaseInfo _release;
    private readonly bool _verified;
    private bool _applying;

    public UpdateDialog(LauncherReleaseInfo release)
    {
        _release = release;
        InitializeComponent();

        VersionLabel.Text =
            $"A new version of CloudLauncher is available.\n" +
            $"Installed: {AppVersion.CurrentString}   >   Latest: {release.Version}";

        NotesLabel.Text = string.IsNullOrWhiteSpace(release.Notes)
            ? "No release notes were provided for this update."
            : release.Notes;

        // Settings opens this for any newer version the server names, signed or not, so the check
        // that decides whether there is an Update button at all happens here.
        var check = App.State.Update.Verify(release);
        _verified = check.IsValid;
        if (!_verified)
        {
            AppLog.Log("update", $"Launcher {release.Version} is not offered: {check.Failure}.");
            ShowUnverified("An update is available but could not be verified. Download it from the website instead.");
        }
        else if (App.State.Update.LastUpdateFailed)
        {
            VersionLabel.Text += "\nThe last update did not finish installing. The Logs tab has the details.";
        }

        RestoreSize();
    }

    /// <summary>Replaces the offer with a pointer to the website. The notes go too: they came with a
    /// release that could not be verified.</summary>
    private void ShowUnverified(string message)
    {
        VersionLabel.Text = message;
        NotesCard.Visibility = Visibility.Collapsed;
        ProgressArea.Visibility = Visibility.Collapsed;
        UpdateButton.IsDefault = false;
        UpdateButton.IsEnabled = false;
        UpdateButton.Visibility = Visibility.Collapsed;
        WebsiteButton.Visibility = Visibility.Visible;
        LaterButton.Content = "Close";
        LaterButton.IsEnabled = true;
    }

    private void OnOpenWebsite(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(UpdateService.WebsiteUrl) { UseShellExecute = true }); }
        catch (Exception ex) { AppLog.LogError("update", ex); }
    }

    /// <summary>Settings key this window's dragged size is remembered under.</summary>
    private const string SizeKey = "update-available";

    /// <summary>Opens at whatever size this dialog was last left at.</summary>
    /// <remarks>Release notes can be long, so the window is resizable and remembers its size. The saved
    /// size is clamped to the work area, so one saved on a larger monitor can't put the buttons off
    /// screen; the saved value itself is kept for when there is room again.</remarks>
    private void RestoreSize()
    {
        var (width, height) = App.State.Settings.GetDialogSize(SizeKey, Width, Height);
        var work = SystemParameters.WorkArea;
        Width  = Math.Clamp(width,  MinWidth,  Math.Max(MinWidth,  work.Width  - 80));
        Height = Math.Clamp(height, MinHeight, Math.Max(MinHeight, work.Height - 80));

        // On close rather than on every SizeChanged: the size worth keeping is the one it was left
        // at, and a drag raises that event for every pixel crossed.
        Closed += (_, _) =>
        {
            if (WindowState == WindowState.Normal)
                App.State.Settings.SetDialogSize(SizeKey, Width, Height);
        };
    }

    private async void OnUpdate(object sender, RoutedEventArgs e)
    {
        if (_applying || !_verified) return;
        _applying = true;

        UpdateButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        ProgressArea.Visibility = Visibility.Visible;
        ProgressLabel.Text = "Downloading update...";

        var progress = new Progress<double>(p =>
        {
            DownloadBar.Value = p;
            ProgressLabel.Text = $"Downloading update... {p * 100:0}%";
        });

        try
        {
            await App.State.Update.DownloadAndApplyAsync(_release, progress);
            ProgressLabel.Text = "Restarting to finish the update...";
            // The external updater is now waiting for this process to exit.
            Application.Current.Shutdown();
        }
        catch (UpdateVerificationException ex)
        {
            // Nothing was installed and the download is gone; there is no retry that could help.
            _applying = false;
            ShowUnverified(ex.Message);
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
