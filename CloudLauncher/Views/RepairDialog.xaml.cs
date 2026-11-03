using System.Diagnostics;
using System.IO;
using System.Windows;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>Offers to put back the launcher's own files when some are missing from the folder it runs
/// from (see <see cref="InstallCheck"/>), by downloading this version again.</summary>
public partial class RepairDialog : Window
{
    /// <summary>One offer per run: one that was put off waits for the next start, which checks again.</summary>
    private static bool _offered;

    /// <summary>True once a repair has started and the launcher is closing for it.</summary>
    public static bool RepairStarted { get; private set; }

    private bool _repairing;

    private RepairDialog(IReadOnlyList<string> missing)
    {
        InitializeComponent();

        var version = AppVersion.CurrentString;
        MessageLabel.Text =
            (missing.Count == 1
                ? $"One of the files CloudLauncher {version} came with is missing from {InstallCheck.InstallDir}. "
                : $"{missing.Count} of the files CloudLauncher {version} came with are missing from {InstallCheck.InstallDir}. ")
            + "Without them it can close without warning the moment it needs one. This usually means the folder "
            + "it runs from was cleaned up.\n\n"
            + "Repair downloads this version again and puts back only what is missing. Nothing else in the folder "
            + "is touched, and your instances, settings and account stay as they are.";
        if (App.State.Update.LastRepairFailed)
            MessageLabel.Text += "\n\nThe last repair did not finish. The Logs tab has the details.";
        FilesLabel.Text = string.Join("\n", missing);

        // Closing mid-download would leave the launcher running while the repair it started still
        // shuts it down.
        Closing += (_, e) => e.Cancel = _repairing && !RepairStarted;
    }

    /// <summary>Shows the offer over <paramref name="owner"/> when <paramref name="missing"/> is not
    /// empty, once per run. True when it was shown.</summary>
    public static bool Offer(Window? owner, IReadOnlyList<string> missing)
    {
        if (_offered || missing.Count == 0) return false;
        _offered = true;
        var dialog = new RepairDialog(missing);
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
        return true;
    }

    /// <summary>For the crash backstop: when <paramref name="ex"/> comes from a file that could not be
    /// found and the launcher's folder is missing files now (they can go while it runs), shows the offer
    /// in place of the general error message. True when it did.</summary>
    public static bool OfferAfter(Exception ex)
    {
        try
        {
            if (_offered || !IsMissingFile(ex)) return false;
            return Offer(Application.Current?.MainWindow, InstallCheck.Run());
        }
        catch (Exception offerFailed)
        {
            AppLog.LogError("repair", offerFailed);
            return false;
        }
    }

    private static bool IsMissingFile(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex is FileNotFoundException or FileLoadException) return true;
        return false;
    }

    /// <summary>The short name of the file <paramref name="ex"/> says is missing, or null.</summary>
    /// <remarks>Such an exception has no message of its own when the runtime's message file has gone
    /// too, so this is all there is to show.</remarks>
    public static string? MissingFileName(Exception ex) =>
        ex is FileNotFoundException { FileName.Length: > 0 } missing ? missing.FileName.Split(',')[0] : null;

    private async void OnRepair(object sender, RoutedEventArgs e)
    {
        if (_repairing) return;
        _repairing = true;

        RepairButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        ProgressArea.Visibility = Visibility.Visible;
        var downloading = $"Downloading CloudLauncher {AppVersion.CurrentString}...";
        ProgressLabel.Text = downloading;

        var progress = new Progress<double>(p =>
        {
            DownloadBar.Value = p;
            ProgressLabel.Text = $"{downloading} {p * 100:0}%";
        });

        try
        {
            await App.State.Update.RepairAsync(progress);
            RepairStarted = true;
            ProgressLabel.Text = "Restarting to put the files back...";
            // The repairer is now waiting for this process to exit.
            Application.Current.Shutdown();
        }
        catch (UpdateVerificationException)
        {
            // Nothing was changed and the download is gone; the reason is in the log.
            ShowFailed("The download could not be verified, so nothing was changed. Download CloudLauncher "
                       + "again from the website instead.");
        }
        catch (Exception ex)
        {
            AppLog.LogError("repair", ex);
            var why = MissingFileName(ex) is { } file ? $"a file it needs for this is missing as well ({file})." : ex.Message;
            ShowFailed($"The repair could not start: {why}\n\nDownload CloudLauncher again from the website "
                       + "instead, or try again the next time it opens.");
        }
    }

    /// <summary>Replaces the offer with a pointer to the website.</summary>
    private void ShowFailed(string message)
    {
        _repairing = false;
        MessageLabel.Text = message;
        ProgressArea.Visibility = Visibility.Collapsed;
        RepairButton.IsDefault = false;
        RepairButton.Visibility = Visibility.Collapsed;
        WebsiteButton.Visibility = Visibility.Visible;
        LaterButton.Content = "Close";
        LaterButton.IsEnabled = true;
    }

    private void OnOpenWebsite(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(UpdateService.WebsiteUrl) { UseShellExecute = true }); }
        catch (Exception ex) { AppLog.LogError("repair", ex); }
    }

    private void OnLater(object sender, RoutedEventArgs e)
    {
        if (_repairing) return;
        Close();
    }
}
