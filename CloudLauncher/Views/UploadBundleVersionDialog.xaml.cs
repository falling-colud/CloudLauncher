using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CloudLauncher.Animations;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>What the dialog hands back: a zip to upload and the metadata to file it under.</summary>
/// <param name="TemporaryZip">True when the launcher built the zip itself, so the caller deletes it
/// once the upload is done.</param>
public sealed record BundleVersionDraft(string ZipPath, CreateBundleVersionRequest Meta, bool TemporaryZip);

/// <summary>One candidate file while composing a bundle out of an instance.</summary>
/// <param name="RelativePath">Game-relative, e.g. <c>config/jei/jei-client.ini</c>.</param>
/// <param name="EntryPath">The path inside the zip, relative to the bundle's target folder
/// (<c>jei/jei-client.ini</c>). This is what the list shows.</param>
public sealed class ComposeFileRow(string relativePath, string entryPath, long size)
{
    public string RelativePath { get; } = relativePath;
    public string EntryPath { get; } = entryPath;
    public long Size { get; } = size;
    public string SizeLabel { get; } = ContentBundleService.FormatSize(size);
    public bool IsChecked { get; set; }
}

/// <summary>
/// Publishes a version of a content bundle, either from a zip on disk or by building one out of an
/// instance's own files.
/// </summary>
/// <remarks>
/// A built zip's entries are relative to the bundle's target folder, so installing it puts each file
/// back at the same path in the receiving instance. Shown as an in-window card through
/// <see cref="MainWindow.ShowCardAsync"/>.
/// </remarks>
public partial class UploadBundleVersionDialog : UserControl
{
    /// <summary>Cap on listed files; the note under the list says how many were left out.</summary>
    private const int MaxListedFiles = 4000;

    private readonly TaskCompletionSource<BundleVersionDraft?> _tcs = new();
    private readonly BundleKind _kind;
    private readonly string _targetRoot;
    private readonly Reentrancy _filling = new();

    private List<PackSummary> _packs = [];
    private List<ComposeFileRow> _allFiles = [];
    private string _fileQuery = "";
    private string? _zipPath;
    private CancellationTokenSource? _scanCts;
    private bool _working;

    private UploadBundleVersionDialog(BundleKind kind, string targetRoot, string suggestedVersion, string? mcVersionsCsv)
    {
        InitializeComponent();
        _kind = kind;
        _targetRoot = (targetRoot ?? "").Trim('/');

        SubtitleLabel.Text = _targetRoot.Length == 0
            ? $"{ContentBundleService.KindLabel(kind)} · unpacks into the instance folder"
            : $"{ContentBundleService.KindLabel(kind)} · unpacks into {_targetRoot}/ inside each instance";

        VersionBox.Text = suggestedVersion;
        McVersionsBox.Text = mcVersionsCsv ?? "";

        FileSearch.DebounceMilliseconds = 200; // filters a list already in memory
        FileSearch.TextChangedDebounced += (_, text) => { _fileQuery = text ?? ""; RenderFiles(); };

        ToolTipService.SetShowOnDisabled(PublishButton, true);
        ToolTipService.SetShowOnDisabled(ComposeModeRadio, true);

        Loaded += (_, _) =>
        {
            Animate.SlideFadeIn(this, 0, 14, 200);
            VersionBox.Focus();
            VersionBox.SelectAll();
            _ = LoadInstancesAsync();
            UpdatePublishButton();
        };
    }

    public Task<BundleVersionDraft?> Result => _tcs.Task;

    public void Cancel()
    {
        _scanCts?.Cancel();
        _tcs.TrySetResult(null);
    }

    /// <param name="targetRoot">The bundle's stored target folder. It decides what the compose list
    /// offers and what zip entries are relative to.</param>
    public static async Task<BundleVersionDraft?> ShowAsync(
        MainWindow host, BundleKind kind, string targetRoot, string suggestedVersion, string? mcVersionsCsv)
    {
        var card = new UploadBundleVersionDialog(kind, targetRoot, suggestedVersion, mcVersionsCsv);
        await host.ShowCardAsync(card, card.Result, card.Cancel);
        return card.Result.Result;
    }

    // ── mode ──

    private bool ComposeMode => ComposeModeRadio.IsChecked == true;

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (ZipPanel is null || ComposePanel is null) return; // raised once during InitializeComponent
        ZipPanel.Visibility = ComposeMode ? Visibility.Collapsed : Visibility.Visible;
        ComposePanel.Visibility = ComposeMode ? Visibility.Visible : Visibility.Collapsed;
        // Scan lazily, so opening the dialog to upload a zip doesn't walk an instance's config tree.
        if (ComposeMode && _allFiles.Count == 0 && _packs.Count > 0) _ = ScanSelectedInstanceAsync();
        UpdatePublishButton();
    }

    /// <summary>The tick state lives on the row object, so the counter has to be told.</summary>
    private void OnFileTicked(object sender, RoutedEventArgs e) => UpdatePicked();

    // ── a zip on disk ──

    private void OnBrowseZip(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Pick the zip to publish",
            Filter = "Zip archive (*.zip)|*.zip|Every file (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        _zipPath = dialog.FileName;
        long size = 0;
        try { size = new FileInfo(_zipPath).Length; } catch { /* the label is not worth an error */ }

        ZipLabel.Text = size > 0
            ? $"{Path.GetFileName(_zipPath)} · {ContentBundleService.FormatSize(size)}"
            : Path.GetFileName(_zipPath);

        if (size > ContentBundleService.MaxBundleBytes)
            Status($"That zip is {ContentBundleService.FormatSize(size)}. The server accepts up to "
                 + $"{ContentBundleService.FormatSize(ContentBundleService.MaxBundleBytes)}.", danger: true);
        else
            Status(null, danger: false);

        UpdatePublishButton();
    }

    // ── building one out of an instance ──

    private async Task LoadInstancesAsync()
    {
        try
        {
            _packs = await App.State.Api.ListPacksAsync();
        }
        catch (Exception ex)
        {
            // Rare, since ListPacksAsync is cached. Compose mode stays unavailable and says why.
            AppLog.LogError(nameof(UploadBundleVersionDialog), ex);
            _packs = [];
        }

        using (_filling.Hold())
        {
            InstanceBox.Items.Clear();
            foreach (var pack in _packs.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
                InstanceBox.Items.Add(new ComboBoxItem { Content = pack.Name, Tag = pack });
            if (InstanceBox.Items.Count > 0) InstanceBox.SelectedIndex = 0;
        }

        if (_packs.Count == 0)
        {
            ComposeModeRadio.IsEnabled = false;
            ComposeModeRadio.ToolTip = "You have no instances to take files from yet.";
        }
        else
        {
            ComposeModeRadio.ToolTip = _targetRoot.Length == 0
                ? "Tick files from one of your instances and the launcher zips them for you"
                : $"Tick files under {_targetRoot}/ in one of your instances and the launcher zips them for you";
            if (ComposeMode) await ScanSelectedInstanceAsync();
        }
    }

    private void OnInstanceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling.Busy) return;
        _ = ScanSelectedInstanceAsync();
    }

    private PackSummary? SelectedPack => (InstanceBox.SelectedItem as ComboBoxItem)?.Tag as PackSummary;

    private async Task ScanSelectedInstanceAsync()
    {
        if (SelectedPack is not { } pack) return;

        _scanCts?.Cancel();
        var cts = new CancellationTokenSource();
        _scanCts = cts;
        var ct = cts.Token;

        _allFiles = [];
        FileList.ItemsSource = null;

        // The target folder comes from the server, so validate it before listing anything under it.
        if (_targetRoot.Length > 0 && !PathSafety.IsSafeRelativePath(_targetRoot))
        {
            AppLog.Log(nameof(UploadBundleVersionDialog), $"Not listing files: the bundle's target folder is not a path inside an instance: {_targetRoot}");
            ShowListNote("This bundle's target folder is not a folder inside an instance, so there is nothing to pick from.");
            UpdatePicked();
            return;
        }

        ShowListNote($"Reading {pack.Name}...");

        try
        {
            var files = await Task.Run(() => ScanInstance(pack, ct), ct);
            if (ct.IsCancellationRequested) return;
            _allFiles = files;
            RenderFiles();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(UploadBundleVersionDialog), ex);
            ShowListNote($"{pack.Name} could not be read. The details are in the launcher log.");
        }
    }

    /// <summary>
    /// Every file under the bundle's target folder in one instance.
    /// </summary>
    /// <remarks>
    /// <c>config</c>, <c>kubejs</c> and <c>defaultconfigs</c> go through <see cref="ConfigHubService.Scan"/>
    /// to reuse its cache and skip list (KubeJS <c>probe_dumps</c> alone is tens of thousands of
    /// generated files). Other roots are walked directly.
    /// </remarks>
    private List<ComposeFileRow> ScanInstance(PackSummary pack, CancellationToken ct)
    {
        var gameDir = App.State.Packs.GameDir(pack.Id);
        var prefix = _targetRoot.Length == 0 ? "" : _targetRoot + "/";
        var rows = new List<ComposeFileRow>();

        var viaConfigHub = _targetRoot.StartsWith("config", StringComparison.OrdinalIgnoreCase)
                        || _targetRoot.StartsWith("kubejs", StringComparison.OrdinalIgnoreCase)
                        || _targetRoot.StartsWith("defaultconfigs", StringComparison.OrdinalIgnoreCase);

        if (viaConfigHub)
        {
            foreach (var entry in ConfigHubService.Scan([pack], App.State.Packs, App.State.Settings, false, null, ct))
            {
                ct.ThrowIfCancellationRequested();
                if (prefix.Length > 0 && !entry.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (ConfigHubService.IsBackup(entry.FileName)) continue; // never publish our own .bak files
                Add(entry.RelativePath, entry.Size);
            }
        }
        else
        {
            var root = prefix.Length == 0 ? gameDir : PathSafety.ResolveInside(gameDir, _targetRoot);
            if (root is not null && Directory.Exists(root))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(gameDir, file).Replace('\\', '/');
                    long size = 0;
                    try { size = new FileInfo(file).Length; } catch { /* a size is a label */ }
                    if (ConfigHubService.IsBackup(Path.GetFileName(file))) continue;
                    Add(relative, size);
                    if (rows.Count >= MaxListedFiles) break;
                }
            }
        }

        rows.Sort((a, b) => string.Compare(a.EntryPath, b.EntryPath, StringComparison.OrdinalIgnoreCase));
        return rows;

        void Add(string relative, long size)
        {
            if (rows.Count >= MaxListedFiles) return;
            var entryPath = prefix.Length == 0 ? relative : relative[prefix.Length..];
            if (entryPath.Length == 0) return;
            // Apply the install-side rules now, so files the server or installer would refuse
            // (mods/ under the instance root, for example) can't be ticked.
            if (BundleSafePath.ValidateEntryUnder(_targetRoot, entryPath) is not null) return;
            rows.Add(new ComposeFileRow(relative, entryPath, size));
        }
    }

    private void RenderFiles()
    {
        var query = _fileQuery.Trim();
        var visible = query.Length == 0
            ? _allFiles
            : _allFiles.Where(f => f.EntryPath.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        FileList.ItemsSource = visible;

        if (_allFiles.Count == 0)
            ShowListNote(_targetRoot.Length == 0
                ? "That instance has no files to take."
                : $"That instance has no {_targetRoot}/ folder yet. It appears once the instance has "
                  + "been launched or synced once.");
        else if (visible.Count == 0)
            ShowListNote("Nothing in this instance matched that filter.");
        else
            ShowListNote(null);

        UpdatePicked();
    }

    private void ShowListNote(string? text)
    {
        FileListNote.Text = text ?? "";
        FileListNote.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnSelectAllFiles(object sender, RoutedEventArgs e) => SetAllFiles(true);
    private void OnSelectNoFiles(object sender, RoutedEventArgs e) => SetAllFiles(false);

    private void SetAllFiles(bool value)
    {
        // Only the rows the filter shows, so Select all never ticks files the user can't see.
        if (FileList.ItemsSource is not IEnumerable<ComposeFileRow> visible) return;
        var rows = visible.ToList();
        foreach (var row in rows) row.IsChecked = value;
        FileList.ItemsSource = null;
        FileList.ItemsSource = rows;
        UpdatePicked();
    }

    private void UpdatePicked()
    {
        var picked = _allFiles.Where(f => f.IsChecked).ToList();
        var bytes = picked.Sum(f => f.Size);
        PickedLabel.Text = picked.Count == 0
            ? _allFiles.Count == 0 ? "" : $"{_allFiles.Count:N0} file(s) · none ticked"
            : $"{picked.Count:N0} of {_allFiles.Count:N0} ticked · {ContentBundleService.FormatSize(bytes)}";
        UpdatePublishButton();
    }

    // ── publish ──

    private void OnFormChanged(object sender, TextChangedEventArgs e) => UpdatePublishButton();

    private void UpdatePublishButton()
    {
        if (PublishButton is null) return;

        var version = VersionBox.Text.Trim();
        string? why = null;

        if (_working) why = "Building the zip...";
        else if (version.Length == 0) why = "Give the version a name first, e.g. 1.2.0.";
        else if (ComposeMode)
        {
            // The tick state lives on the row objects, so this is the moment to re-read it.
            if (_allFiles.Count(f => f.IsChecked) == 0) why = "Tick at least one file to put in the bundle.";
        }
        else if (_zipPath is null) why = "Choose the zip to publish first.";

        PublishButton.IsEnabled = why is null;
        PublishButton.ToolTip = why ?? "Upload this version";
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Cancel();

    private void OnPublish(object sender, RoutedEventArgs e) => _ = PublishAsync();

    private async Task PublishAsync()
    {
        var version = VersionBox.Text.Trim();
        if (version.Length == 0) { Status("A version needs a name.", danger: true); return; }

        string zipPath;
        bool temporary;

        if (ComposeMode)
        {
            var picked = _allFiles.Where(f => f.IsChecked).Select(f => f.RelativePath).ToList();
            if (picked.Count == 0) { Status("Tick at least one file.", danger: true); return; }
            if (SelectedPack is not { } pack) { Status("Pick the instance to take the files from.", danger: true); return; }

            _working = true;
            UpdatePublishButton();
            Status($"Building a zip out of {picked.Count:N0} file(s)...", danger: false);
            try
            {
                var gameDir = App.State.Packs.GameDir(pack.Id);
                var result = await Task.Run(() =>
                    ContentBundleService.Compose(gameDir, _targetRoot, picked, null, CancellationToken.None));

                if (result.Files == 0)
                {
                    _working = false;
                    UpdatePublishButton();
                    Status("None of those files could be read, so there is nothing to publish.", danger: true);
                    return;
                }

                var built = new FileInfo(result.ZipPath).Length;
                if (built > ContentBundleService.MaxBundleBytes)
                {
                    try { File.Delete(result.ZipPath); } catch { /* nothing to do */ }
                    _working = false;
                    UpdatePublishButton();
                    Status($"That comes to {ContentBundleService.FormatSize(built)}, and the server accepts up "
                         + $"to {ContentBundleService.FormatSize(ContentBundleService.MaxBundleBytes)}. Untick some files.",
                           danger: true);
                    return;
                }

                if (result.Skipped.Count > 0)
                    AppLog.Log("bundles", $"{result.Skipped.Count} file(s) were left out of the bundle: {result.Skipped[0]}...");

                zipPath = result.ZipPath;
                temporary = true;
            }
            catch (Exception ex)
            {
                AppLog.LogError(nameof(UploadBundleVersionDialog), ex);
                _working = false;
                UpdatePublishButton();
                Status("The zip could not be built. The details are in the launcher log.", danger: true);
                return;
            }
            finally { _working = false; }
        }
        else
        {
            if (_zipPath is null) { Status("Choose a zip first.", danger: true); return; }
            zipPath = _zipPath;
            temporary = false;
        }

        var meta = new CreateBundleVersionRequest(
            version,
            ChangelogBox.Text.Trim() is { Length: > 0 } log ? log : null,
            (ChannelBox.SelectedItem as ComboBoxItem)?.Content as string ?? "release",
            Path.GetFileName(zipPath),
            McVersionsBox.Text.Trim() is { Length: > 0 } mc ? mc : null,
            null);

        _tcs.TrySetResult(new BundleVersionDraft(zipPath, meta, temporary));
    }

    private void Status(string? text, bool danger)
    {
        StatusLabel.Text = text ?? "";
        StatusLabel.SetResourceReference(TextBlock.ForegroundProperty,
                                         danger ? "DangerBrush" : "TextSecondaryBrush");
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Cancel(); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }
}
