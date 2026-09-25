using System.IO;
using System.Windows;
using System.Windows.Controls;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>Collects what a hosted resource pack version carries: the version string, release
/// channel, Minecraft versions and changelog. Used by both upload paths.</summary>
public partial class UploadResourcePackVersionDialog : Window
{
    /// <summary>The request to post, or null if the dialog was cancelled.</summary>
    public CreateResourcePackVersionRequest? Result { get; private set; }

    private readonly string _fileName;

    /// <param name="filePath">The zip being uploaded; its name is shown and sent as the file name.</param>
    /// <param name="suggestedVersion">Pre-filled version: a date stamp or the next number up.</param>
    /// <param name="mcVersionsCsv">Pre-filled Minecraft versions, usually the source instance's.</param>
    public UploadResourcePackVersionDialog(string filePath, string suggestedVersion, string? mcVersionsCsv)
    {
        InitializeComponent();
        _fileName = Path.GetFileName(filePath);

        var size = TryFileSize(filePath);
        FileLabel.Text = size is long bytes ? $"{_fileName} · {FormatSize(bytes)}" : _fileName;

        VersionBox.Text = suggestedVersion;
        McVersionsBox.Text = mcVersionsCsv ?? "";

        Loaded += (_, _) =>
        {
            VersionBox.Focus();
            VersionBox.SelectAll();
            UpdateUploadButton();
        };
    }

    private static long? TryFileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return null; }
    }

    private void OnFormTextChanged(object sender, TextChangedEventArgs e) => UpdateUploadButton();

    private void UpdateUploadButton()
    {
        if (UploadButton is null) return;
        UploadButton.IsEnabled = !string.IsNullOrWhiteSpace(VersionBox.Text);
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnUpload(object sender, RoutedEventArgs e)
    {
        var version = VersionBox.Text.Trim();
        if (version.Length == 0)
        {
            StatusLabel.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            StatusLabel.Text = "A version string is required.";
            return;
        }

        var channel = (ChannelBox.SelectedItem as ComboBoxItem)?.Content as string ?? "release";
        var mc = McVersionsBox.Text.Trim();
        var changelog = ChangelogBox.Text.Trim();

        Result = new CreateResourcePackVersionRequest(
            version,
            changelog.Length == 0 ? null : changelog,
            channel,
            _fileName,
            mc.Length == 0 ? null : mc);

        DialogResult = true;
        Close();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB"
    };
}
