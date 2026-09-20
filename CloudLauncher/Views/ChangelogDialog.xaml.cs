using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CloudLauncher.Animations;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// The launcher's changelog: every release the server has published, newest first, with the one
/// you are running marked.
/// </summary>
/// <remarks>
/// The release notes were already written for every version — the updater showed them once, when an
/// update was offered, and then they were gone. "What changed in the version I just got?" and "what
/// am I missing by not updating?" are the same question, and neither had an answer inside the app.
/// </remarks>
public partial class ChangelogDialog : UserControl
{
    private readonly TaskCompletionSource<bool> _tcs = new();

    public ChangelogDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => Animate.SlideFadeIn(this, 0, 14, 200);
    }

    public Task<bool> Result => _tcs.Task;
    public void Cancel() => _tcs.TrySetResult(true);

    /// <summary>Shows the changelog over the main window and waits for it to be dismissed.</summary>
    public static async Task ShowAsync(MainWindow host)
    {
        var card = new ChangelogDialog();
        _ = card.LoadAsync();
        await host.ShowCardAsync(card, card.Result, card.Cancel);
    }

    private async Task LoadAsync()
    {
        try
        {
            var releases = await App.State.Api.GetLauncherReleasesAsync();
            var installed = AppVersion.Current;

            var rows = releases
                .OrderByDescending(r => ParseVersion(r.Version))
                .ThenByDescending(r => r.ReleasedAt)
                .Select(r => new ReleaseRow(r, installed))
                .ToList();

            ReleaseList.ItemsSource = rows;
            StatusLabel.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (rows.Count == 0)
            {
                StatusLabel.Text = "The server has not published any release notes yet.";
                return;
            }

            SubLabel.Text = $"{rows.Count} release(s) published. You are running {AppVersion.CurrentString}.";
            var newer = rows.Count(r => r.IsNewer);
            FooterNote.Text = newer == 0
                ? "You are on the latest version."
                : $"{newer} newer release(s) — use Check for updates to install.";
        }
        catch (Exception ex)
        {
            StatusLabel.Visibility = Visibility.Visible;
            StatusLabel.Text = "Could not load the changelog: " + ex.Message;
        }
    }

    /// <summary>A version that does not parse sorts last rather than throwing — the notes are still
    /// worth showing.</summary>
    private static Version ParseVersion(string? value) =>
        Version.TryParse(value, out var parsed) ? parsed : new Version(0, 0);

    private void OnClose(object sender, RoutedEventArgs e) => _tcs.TrySetResult(true);

    private sealed class ReleaseRow
    {
        private readonly LauncherReleaseInfo _info;
        private readonly Version _version;
        private readonly Version _installed;

        public ReleaseRow(LauncherReleaseInfo info, Version installed)
        {
            _info = info;
            _installed = installed;
            _version = ParseVersion(info.Version);
        }

        public string VersionLabel => $"Version {_info.Version}";
        public string DateLabel => _info.ReleasedAt.ToLocalTime().ToString("d MMM yyyy");
        public string Notes => string.IsNullOrWhiteSpace(_info.Notes) ? "(no notes for this release)" : _info.Notes!.Trim();

        public bool IsInstalled => _version == _installed;
        public bool IsNewer => _version > _installed;

        public Visibility InstalledVisibility => IsInstalled ? Visibility.Visible : Visibility.Collapsed;
        public Visibility NewerVisibility => IsNewer ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>The release you are on is outlined in the accent so it is findable in a long list.</summary>
        public Brush BorderBrushForState => (Brush)Application.Current.Resources[
            IsInstalled ? "AccentBrush" : "BorderSubtleBrush"];
    }
}
