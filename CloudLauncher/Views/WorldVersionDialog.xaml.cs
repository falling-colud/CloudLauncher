using System.Windows;

namespace CloudLauncher.Views;

/// <summary>Collects the version string and changelog for a hosted world upload.</summary>
public partial class WorldVersionDialog : Window
{
    public string Version { get; private set; } = "";
    public string? Changelog { get; private set; }

    /// <param name="subtitle">Names the world and the upload size, so the person can back out
    /// before a long upload starts.</param>
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
            return; // keep the dialog open rather than guess a version
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
