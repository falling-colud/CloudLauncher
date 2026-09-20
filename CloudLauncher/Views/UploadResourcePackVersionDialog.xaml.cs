using System.IO;
using System.Windows;
using System.Windows.Controls;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// Collects everything a hosted resource pack version carries: the version string, the release
/// channel, the Minecraft versions it is for, and a changelog.
/// </summary>
/// <remarks>
/// Both upload paths used to chain a single input box for the version string and then hard-code
/// <c>"release"</c> with a null changelog — so every upload was filed as a release nobody could tell
/// apart, even though the server stores and returns both fields. One dialog covers both callers.
/// </remarks>
public partial class UploadResourcePackVersionDialog : Window
{
    /// <summary>The request to post, or null if the dialog was cancelled.</summary>
    public CreateResourcePackVersionRequest? Result { get; private set; }

    private readonly string _fileName;

    /// <param name="filePath">The zip being uploaded; its name is shown and sent as the file name.</param>
    /// <param name="suggestedVersion">Pre-filled version string — a date stamp or the next number up.</param>
    /// <param name="mcVersionsCsv">Pre-filled Minecraft versions, normally the source instance's.</param>
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
