using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// A newer launcher on the server: its notes and Update Now, which downloads it, proves it and restarts
/// into it (<see cref="LinuxUpdater"/>). A release that does not verify gets no Update button, only the
/// website, and its notes are not shown (they came with a release that could not be proven). The Windows
/// launcher's UpdateDialog.
/// </summary>
public static class SettingsUpdateCard
{
    public static async Task ShowAsync(MainWindow shell, LauncherReleaseInfo release, UpdateVerification check)
    {
        var done = new TaskCompletionSource<bool>();
        CancellationTokenSource? download = null;
        var applying = false;

        var title = new TextBlock { Text = $"CloudLauncher {release.Version}", TextWrapping = TextWrapping.Wrap };
        title.Classes.Add("h2");
        var released = release.ReleasedAt > DateTimeOffset.UnixEpoch ? $", released {TimeFormat.Date(release.ReleasedAt)}" : "";
        var sub = new TextBlock
        {
            Text = $"A new version{released}. You have {AppVersion.CurrentString}.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new(0, 6, 0, 0)
        };
        sub.Classes.Add("muted");

        var notes = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(release.Notes) ? "No release notes came with this version." : release.Notes.Trim(),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 19
        };
        notes.Classes.Add("muted");
        var notesBox = new Border
        {
            Padding = new(14, 12),
            Margin = new(0, 16, 0, 0),
            MaxHeight = 280,
            Child = new ScrollViewer { Content = notes, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }
        };
        notesBox.Classes.Add("well");

        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Margin = new(0, 16, 0, 0), IsVisible = false };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 0), IsVisible = false };
        status.Classes.Add("caption");

        var website = new Button { Content = "Open Website", IsVisible = false, HorizontalContentAlignment = HorizontalAlignment.Center };
        var later = new Button { Content = "Later", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        var update = new Button { Content = "Update Now", MinWidth = 110, HorizontalContentAlignment = HorizontalAlignment.Center };
        update.Classes.Add("primary");

        // Progress lines are small; a message the user has to read is not.
        void Say(string text, string kind = "caption")
        {
            status.Text = text;
            status.IsVisible = text.Length > 0;
            status.Classes.Remove("caption");
            status.Classes.Remove("muted");
            status.Classes.Remove("danger");
            status.Classes.Add(kind);
        }

        // The release (or the download) could not be proven, or cannot go in here: the website instead.
        void OfferWebsite(string message)
        {
            notesBox.IsVisible = false;
            bar.IsVisible = false;
            update.IsVisible = false;
            website.IsVisible = true;
            website.Classes.Add("primary");
            later.Content = "Close";
            later.IsEnabled = true;
            Say(message, "muted");
        }

        if (!check.IsValid)
        {
            AppLog.Log("update", $"CloudLauncher {release.Version} is not offered: {check.Failure}.");
            sub.Text = $"You have {AppVersion.CurrentString}.";
            OfferWebsite("This version is out but could not be verified, so it is not installed from here. Download it from the website instead.");
        }

        website.Click += (_, _) =>
        {
            if (!SafeLaunch.OpenUrl(LinuxUpdater.WebsiteUrl)) Say($"Your browser could not be opened. The address is {LinuxUpdater.WebsiteUrl}", "muted");
        };

        later.Click += (_, _) =>
        {
            if (applying) download?.Cancel();
            else done.TrySetResult(false);
        };

        update.Click += async (_, _) =>
        {
            if (applying) return;
            applying = true;
            download = new CancellationTokenSource();
            update.IsEnabled = false;
            later.Content = "Cancel";
            bar.IsVisible = true;
            bar.IsIndeterminate = false;
            bar.Value = 0;
            Say("Downloading...");
            var downloading = true;
            var percent = new Progress<double>(p =>
            {
                if (!downloading) return;
                bar.Value = p;
                Say($"Downloading... {p * 100:0}%");
            });
            var stage = new Progress<string>(text =>
            {
                if (text == "Downloading...") return;
                downloading = false;
                bar.IsIndeterminate = true;
                Say(text);
                // Past the check the files are being swapped: nothing to cancel any more.
                if (text is "Installing..." or "Restarting...") later.IsEnabled = false;
            });
            try
            {
                await LinuxUpdater.DownloadAndApplyAsync(release, percent, download.Token, stage);
                // The new launcher starts once this one has closed, which happens next.
            }
            catch (OperationCanceledException) when (download.IsCancellationRequested)
            {
                Reset();
                Say("The download was stopped. Nothing has changed.", "muted");
            }
            catch (UpdateVerificationException ex)
            {
                applying = false;
                OfferWebsite(ex.Message);
            }
            catch (UpdateBlockedException ex)
            {
                applying = false;
                OfferWebsite(ex.Message);
            }
            catch (Exception ex)
            {
                AppLog.LogError("update", ex);
                Reset();
                Say("The update could not be installed: " + LinuxUpdater.Describe(ex), "danger");
            }
            finally
            {
                download.Dispose();
                download = null;
            }

            void Reset()
            {
                applying = false;
                bar.IsVisible = false;
                update.IsEnabled = true;
                later.Content = "Later";
                later.IsEnabled = true;
            }
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new(0, 20, 0, 0),
            Children = { later, website, update }
        };
        var card = new Border
        {
            Width = 540,
            Child = new StackPanel { Children = { title, sub, notesBox, bar, status, buttons } }
        };
        card.Classes.Add("dialog");
        SettingsLook.UseSlateDisabledLook(card);

        await shell.ShowCardAsync(card, done.Task, () =>
        {
            if (applying) download?.Cancel();
            else done.TrySetResult(false);
        });
    }
}
