using System.Windows;

namespace CloudLauncher.Views;

/// <summary>
/// Collects the version string and changelog for a hosted world upload.
/// </summary>
/// <remarks>
/// The upload flow used to ask for a version string through <see cref="SimpleInputDialog"/> and pass
/// <c>null</c> as the changelog, so every published version arrived with an empty one even though the
/// server stores it and the Versions list shows it. One dialog with both fields is the smallest thing
/// that fixes that.
/// </remarks>
public partial class WorldVersionDialog : Window
{
    public string Version { get; private set; } = "";
    public string? Changelog { get; private set; }

    /// <param name="subtitle">One line naming the world and how large the upload will be, so the
    /// person can back out before a long upload rather than during it.</param>
    public WorldVersionDialog(string subtitle, string suggestedVersion)
    {
        InitializeComponent();
        SubtitleLabel.Text = subtitle;
        VersionBox.Text = suggestedVersion;
        Loaded += (_, _) => { VersionBox.Focus(); VersionBox.SelectAll(); };
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var version = VersionBox.Text.Trim();
        if (string.IsNullOrEmpty(version))
        {
            VersionBox.Focus();
            return; // nothing to publish under — leave the dialog open rather than guessing a name
        }
        Version = version;
        Changelog = string.IsNullOrWhiteSpace(ChangelogBox.Text) ? null : ChangelogBox.Text.Trim();
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
