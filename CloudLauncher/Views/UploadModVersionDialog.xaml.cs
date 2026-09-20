using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CloudLauncher.Animations;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// In-window card for publishing a new version of a hosted mod: pick (or drop) the jar, then fill in
/// the version string, release channel, changelog and the compatibility this particular build has.
/// </summary>
/// <remarks>
/// The card owns the upload rather than handing the values back to its caller, because the four
/// things a long upload needs — a determinate bar, a byte counter, a working Cancel, and a place to
/// put the error when it fails — all live here. Callers get the finished
/// <see cref="HostedModVersionInfo"/> (or null when the user backed out) and simply refresh.
/// <para>
/// All four metadata fields matter: before this dialog existed the client sent a hard-coded
/// "release" channel, a null changelog and a copy of the mod's own compatibility CSVs, so a beta
/// build was indistinguishable from a release and per-build compatibility could never differ from
/// the mod's.
/// </para>
/// </remarks>
public partial class UploadModVersionDialog : UserControl
{
    private readonly Guid _modId;
    private readonly TaskCompletionSource<HostedModVersionInfo?> _tcs = new();

    /// <summary>The version being corrected, or null when this is a fresh upload.</summary>
    /// <remarks>
    /// Edit mode reuses this card rather than growing a second form: the fields are the same four,
    /// and one card means the rules about what a publishable version looks like are stated once. The
    /// difference is entirely in what the button does — a PATCH instead of a multipart POST — plus
    /// hiding the file picker, because editing the details deliberately leaves the jar alone.
    /// </remarks>
    private readonly HostedModVersionInfo? _editing;

    /// <summary>The chosen jar. Null until the user browses or drops one; Upload stays disabled.</summary>
    private string? _filePath;

    /// <summary>Non-null only while bytes are in flight — also the flag that turns Cancel from
    /// "close the card" into "abort the upload".</summary>
    private CancellationTokenSource? _uploadCts;

    /// <param name="initialFilePath">A jar already chosen elsewhere (the create-mod dialog), so the
    /// card opens ready to upload instead of asking for the same file twice.</param>
    /// <param name="editing">When given, the card edits that version's details instead of
    /// publishing a new one, and its file picker is hidden.</param>
    public UploadModVersionDialog(HostedModDetail mod, string? initialFilePath = null,
        HostedModVersionInfo? editing = null)
    {
        InitializeComponent();
        _modId = mod.Id;
        _editing = editing;

        if (editing is null)
        {
            SubLabel.Text = $"Publishing to {mod.Name}";
            // Seed compatibility from the mod so the common case (this build supports what the mod
            // says it supports) is one click, while still being editable per build.
            McVersionsBox.Text = mod.McVersionsCsv ?? "";
            SetLoaders(mod.LoadersCsv);
            if (initialFilePath is not null && File.Exists(initialFilePath)) SetFile(initialFilePath);
        }
        else
        {
            TitleLabel.Text = "Edit version";
            SubLabel.Text = $"{editing.VersionString} of {mod.Name} · {editing.FileName}";
            UploadButton.Content = "Save changes";
            // No drop zone: this is the one path that must not touch the file. Replacing a jar is
            // still "upload a version", which keeps its own publish date and changelog.
            DropZone.Visibility = Visibility.Collapsed;

            VersionBox.Text = editing.VersionString;
            ChangelogBox.Text = editing.Changelog ?? "";
            McVersionsBox.Text = editing.McVersionsCsv ?? mod.McVersionsCsv ?? "";
            SetLoaders(editing.LoadersCsv ?? mod.LoadersCsv);
            SelectChannel(editing.ReleaseChannel);
        }

        Loaded += (_, _) => { Animate.SlideFadeIn(this, 0, 14, 200); Focus(); };
        Focusable = true;
    }

    /// <summary>Picks the channel combo entry matching a stored channel string.</summary>
    /// <remarks>A channel this build has never heard of leaves "release" selected rather than
    /// clearing the box and quietly making the user re-pick something they never changed.</remarks>
    private void SelectChannel(string channel)
    {
        foreach (var item in ChannelBox.Items.OfType<ComboBoxItem>())
        {
            if (!string.Equals(item.Content as string, channel, StringComparison.OrdinalIgnoreCase)) continue;
            ChannelBox.SelectedItem = item;
            return;
        }
    }

    /// <summary>Completes with the uploaded version, or null if the user cancelled.</summary>
    public Task<HostedModVersionInfo?> Result => _tcs.Task;

    /// <summary>Backdrop/Escape cancel. Ignored while an upload is running so a stray click on the
    /// scrim cannot leave a half-sent jar behind with no UI attached to it.</summary>
    public void Cancel()
    {
        if (_uploadCts is not null) return;
        _tcs.TrySetResult(null);
    }

    public static async Task<HostedModVersionInfo?> ShowAsync(MainWindow host, HostedModDetail mod,
        string? initialFilePath = null)
    {
        var card = new UploadModVersionDialog(mod, initialFilePath);
        await host.ShowCardAsync(card, card.Result, card.Cancel);
        return card.Result.Result;
    }

    /// <summary>Opens the card to correct an existing version's details. The file is untouched.</summary>
    /// <returns>The version as the server now holds it, or null if the user backed out.</returns>
    public static async Task<HostedModVersionInfo?> ShowEditAsync(MainWindow host, HostedModDetail mod,
        HostedModVersionInfo version)
    {
        var card = new UploadModVersionDialog(mod, initialFilePath: null, editing: version);
        await host.ShowCardAsync(card, card.Result, card.Cancel);
        return card.Result.Result;
    }

    // ── file selection ───────────────────────────────────────────────────────

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Pick mod jar to upload",
            Filter = "Mod jars (*.jar)|*.jar|All files|*"
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) SetFile(dlg.FileName);
    }

    private void OnDropZoneDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedJar(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDropZoneDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DroppedJar(e) is { } path) SetFile(path);
    }

    /// <summary>The single jar in a drag payload — dropping a folder, a zip or several files at once
    /// is ignored rather than guessed at, since a version is exactly one file.</summary>
    private static string? DroppedJar(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        var files = e.Data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>();
        return files.Length == 1
               && File.Exists(files[0])
               && files[0].EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
            ? files[0]
            : null;
    }

    private void SetFile(string path)
    {
        _filePath = path;
        var info = new FileInfo(path);
        FileNameLabel.Text = info.Name;
        FileMetaLabel.Text = FormatSize(info.Length);

        if (string.IsNullOrWhiteSpace(VersionBox.Text))
            VersionBox.Text = GuessVersion(info.Name);

        SetStatus("", error: false);
        UpdateUploadButton();
    }

    /// <summary>Best-effort version string from a jar's file name: everything from the first
    /// digit-led token onwards, which covers "sodium-fabric-0.5.8+mc1.20.1.jar" and
    /// "JEI-1.20.1-15.2.0.27.jar" alike. The user can always correct it — it only saves typing.</summary>
    private static string GuessVersion(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var match = Regex.Match(stem, @"[-_](\d[^-_]*(?:[-_]\d[^-_]*)*)$");
        return match.Success ? match.Groups[1].Value : "";
    }

    // ── form state ───────────────────────────────────────────────────────────

    private void OnFormChanged(object sender, TextChangedEventArgs e) => UpdateUploadButton();
    private void OnLoaderChanged(object sender, RoutedEventArgs e) => UpdateUploadButton();

    private void UpdateUploadButton()
    {
        if (UploadButton is null) return;
        UploadButton.IsEnabled =
            _uploadCts is null
            // Edit mode has no file to pick: the version already has one and is keeping it.
            && (_editing is not null || _filePath is not null)
            && !string.IsNullOrWhiteSpace(VersionBox.Text)
            && !string.IsNullOrWhiteSpace(McVersionsBox.Text)
            && LoadersCsv() is not null;
    }

    private void SetLoaders(string? csv)
    {
        var set = (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant())
            .ToHashSet();
        LoaderFabric.IsChecked = set.Contains("fabric");
        LoaderForge.IsChecked = set.Contains("forge");
        LoaderNeoForge.IsChecked = set.Contains("neoforge");
        LoaderQuilt.IsChecked = set.Contains("quilt");
    }

    /// <summary>The ticked loaders as the server's CSV, or null when none are ticked (which is not a
    /// publishable state — a build with no loader matches no instance).</summary>
    private string? LoadersCsv()
    {
        var loaders = new List<string>(4);
        if (LoaderFabric.IsChecked == true) loaders.Add("fabric");
        if (LoaderForge.IsChecked == true) loaders.Add("forge");
        if (LoaderNeoForge.IsChecked == true) loaders.Add("neoforge");
        if (LoaderQuilt.IsChecked == true) loaders.Add("quilt");
        return loaders.Count == 0 ? null : string.Join(',', loaders);
    }

    // ── upload ───────────────────────────────────────────────────────────────

    private async void OnUpload(object sender, RoutedEventArgs e)
    {
        if (_uploadCts is not null) return;
        if (_editing is not null) { await SaveEditAsync(); return; }
        if (_filePath is null) return;

        var file = new FileInfo(_filePath);
        if (!file.Exists)
        {
            SetStatus("That file is no longer there — pick it again.", error: true);
            return;
        }

        var meta = new CreateModVersionRequest(
            VersionBox.Text.Trim(),
            string.IsNullOrWhiteSpace(ChangelogBox.Text) ? null : ChangelogBox.Text.Trim(),
            (ChannelBox.SelectedItem as ComboBoxItem)?.Content as string ?? "release",
            file.Name,
            NormalizeCsv(McVersionsBox.Text),
            LoadersCsv());

        _uploadCts = new CancellationTokenSource();
        SetBusy(true);
        var total = file.Length;
        // Marshalled back to the UI thread by Progress<T>'s captured context, so the bar can be
        // written to directly from the reporter.
        var progress = new Progress<long>(sent =>
        {
            UploadProgress.Value = total <= 0 ? 0 : Math.Min(100, sent * 100.0 / total);
            SetStatus($"{FormatSize(sent)} of {FormatSize(total)}", error: false);
        });

        try
        {
            var created = await App.State.Api.UploadModVersionAsync(
                _modId, file.FullName, meta, _uploadCts.Token, progress);
            SetBusy(false);
            _tcs.TrySetResult(created);
        }
        catch (OperationCanceledException)
        {
            SetBusy(false);
            SetStatus("Upload cancelled.", error: false);
            UploadProgress.Value = 0;
        }
        catch (Exception ex)
        {
            SetBusy(false);
            SetStatus("Upload failed: " + ex.Message, error: true);
            UploadProgress.Value = 0;
        }
    }

    /// <summary>Sends the edited details as a patch and closes with what the server stored.</summary>
    /// <remarks>
    /// All four fields are sent, not only the ones that changed: the card was seeded with the
    /// current values and the user has had the chance to alter any of them, so what is on screen is
    /// the intended state of all of them. An empty changelog goes as an empty string, which is how
    /// the server is told to clear one — null there means "leave it alone".
    /// </remarks>
    private async Task SaveEditAsync()
    {
        if (_editing is null) return;

        var req = new UpdateModVersionRequest(
            VersionBox.Text.Trim(),
            ChangelogBox.Text.Trim(),
            (ChannelBox.SelectedItem as ComboBoxItem)?.Content as string ?? "release",
            NormalizeCsv(McVersionsBox.Text) ?? "",
            LoadersCsv() ?? "");

        UploadButton.IsEnabled = false;
        SetStatus("Saving…", error: false);
        try
        {
            var saved = await App.State.Api.UpdateModVersionAsync(_modId, _editing.Id, req);
            _tcs.TrySetResult(saved);
        }
        catch (Exception ex)
        {
            SetStatus("Could not save: " + ex.Message, error: true);
            UpdateUploadButton();
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        // Mid-upload the same button aborts the transfer and leaves the card open, so the user can
        // fix whatever made them stop and try again without re-entering everything.
        if (_uploadCts is { } cts) { cts.Cancel(); return; }
        _tcs.TrySetResult(null);
    }

    private void SetBusy(bool busy)
    {
        if (!busy)
        {
            _uploadCts?.Dispose();
            _uploadCts = null;
        }
        UploadProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Content = busy ? "Cancel upload" : "Cancel";
        BrowseButton.IsEnabled = !busy;
        DropZone.AllowDrop = !busy;
        VersionBox.IsEnabled = !busy;
        ChannelBox.IsEnabled = !busy;
        ChangelogBox.IsEnabled = !busy;
        McVersionsBox.IsEnabled = !busy;
        UpdateUploadButton();
    }

    private void SetStatus(string message, bool error)
    {
        StatusLabel.Text = message;
        StatusLabel.SetResourceReference(ForegroundProperty, error ? "DangerBrush" : "TextSecondaryBrush");
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Cancel(); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }

    /// <summary>Trims and de-duplicates the typed Minecraft versions into the CSV the server stores.</summary>
    private static string? NormalizeCsv(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return parts.Count == 0 ? null : string.Join(',', parts);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB"
    };
}
