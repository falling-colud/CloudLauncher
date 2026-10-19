using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CloudLauncher.Animations;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>
/// Walks the user through the files a CurseForge modpack import could not download: a Download button
/// per file that opens its page on curseforge.com, and a watch on the Downloads folder that moves each
/// file into the instance as soon as it lands there, recognised by its fingerprint.
/// </summary>
/// <remarks>
/// <para>Opened by the import when it finishes, from the instance page's notice, and before Play while
/// files are still missing. In that last case it also offers to play anyway, and closing it means
/// "don't launch".</para>
/// <para>Files can also be picked with Add files or dropped on the card, for anyone whose browser saves
/// somewhere else or who got them from the CurseForge app.</para>
/// </remarks>
public partial class ManualDownloadsCard : UserControl
{
    /// <summary>Instances with a card on screen, so the import finishing and a Play click don't stack
    /// two over each other.</summary>
    private static readonly HashSet<Guid> Open = [];

    private readonly Guid _packId;
    private readonly string _packName;
    private readonly bool _forLaunch;
    private readonly ManualDownloadService _service = App.State.ManualDownloads;
    private readonly ObservableCollection<ManualDownloadRow> _rows = [];
    private readonly TaskCompletionSource<bool> _result = new();
    private readonly HashSet<int> _placing = [];
    private ManualDownloadWatcher? _watcher;
    private bool _suppressEvents;

    private ManualDownloadsCard(Guid packId, string packName, ManualDownloadList list, bool forLaunch)
    {
        InitializeComponent();
        _packId = packId;
        _packName = packName;
        _forLaunch = forLaunch;

        foreach (var file in list.Files)
            _rows.Add(new ManualDownloadRow(file));
        FileList.ItemsSource = _rows;

        PackLabel.Text = string.IsNullOrWhiteSpace(list.Source) ? packName : $"{packName}  ·  from {list.Source}";
        if (forLaunch)
        {
            PlayButton.Visibility = Visibility.Visible;
            CloseButton.Content = "Cancel";
        }

        _suppressEvents = true;
        RemoveBox.IsChecked = _service.RemoveAfterAdding;
        _suppressEvents = false;

        Loaded += (_, _) =>
        {
            Animate.SlideFadeIn(this, 0, 14, 200);
            StartWatching(_service.WatchFolder);
        };
        Unloaded += (_, _) => StopWatching();
        UpdateChrome();
    }

    /// <summary>
    /// Shows the card for <paramref name="packId"/> if it has files still waiting, and returns when it
    /// closes. True only when the user chose to play (<paramref name="forLaunch"/>); false when closed
    /// another way, when there was nothing to show, or when a card for this instance is already open.
    /// </summary>
    public static async Task<bool> ShowAsync(MainWindow shell, Guid packId, string packName, bool forLaunch = false)
    {
        var list = await Task.Run(() => App.State.ManualDownloads.Reconcile(packId));
        if (list is null || list.Files.Count == 0) return false;
        if (forLaunch && list.Files.All(f => f.State != ManualDownloadState.Waiting)) return true;
        if (!Open.Add(packId)) return false;
        try
        {
            var card = new ManualDownloadsCard(packId, packName, list, forLaunch);
            await shell.ShowCardAsync(card, card._result.Task, card.Cancel,
                new ResizableCardSpec("manual-downloads", 700, 600, MinWidth: 520, MinHeight: 400));
            return card._result.Task.Result;
        }
        finally { Open.Remove(packId); }
    }

    /// <summary>
    /// The check before a launch: true to go ahead. Files still waiting bring up the card, where the
    /// user can get them or play anyway; an instance with nothing waiting passes straight through.
    /// </summary>
    public static async Task<bool> ConfirmLaunchAsync(Window? owner, Guid packId, string packName)
    {
        IReadOnlyList<ManualDownloadEntry> waiting;
        try { waiting = await Task.Run(() => App.State.ManualDownloads.Waiting(packId)); }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(ManualDownloadsCard), ex);
            return true;
        }
        if (waiting.Count == 0) return true;
        var shell = owner as MainWindow ?? Application.Current?.MainWindow as MainWindow;
        // Without the main window there is nowhere to show the card; the launch is not held up for it.
        if (shell is null) return true;
        return await ShowAsync(shell, packId, packName, forLaunch: true);
    }

    private void Cancel() => Finish(false);

    private void Finish(bool play)
    {
        StopWatching();
        _result.TrySetResult(play);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Finish(false);
    private void OnPlay(object sender, RoutedEventArgs e) => Finish(true);

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Finish(false); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }

    // ── watching ─────────────────────────────────────────────────────────────

    private void StartWatching(string folder)
    {
        StopWatching();
        WatchLabel.Text = $"Looking for the files in {folder}";
        WatchLabel.ToolTip = folder;
        if (!_rows.Any(r => r.IsWaiting)) return;
        if (!Directory.Exists(folder))
        {
            WatchLabel.Text = $"{folder} does not exist. Pick the folder your browser saves to.";
            return;
        }
        _watcher = new ManualDownloadWatcher(folder, WaitingEntries,
            (entry, path) => Dispatcher.BeginInvoke(() => _ = PlaceAsync(entry.FileId, path, fromWatch: true)));
        _ = _watcher.ScanAsync();
    }

    private void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    /// <summary>The entries still wanted, read on the watcher's thread. A snapshot of the rows, so the
    /// watcher never walks the collection the UI is changing.</summary>
    private IReadOnlyList<ManualDownloadEntry> WaitingEntries()
    {
        if (Dispatcher.CheckAccess()) return Snapshot();
        try { return Dispatcher.Invoke(Snapshot); }
        catch (TaskCanceledException) { return []; }

        List<ManualDownloadEntry> Snapshot() =>
            _rows.Where(r => r.IsWaiting && !_placing.Contains(r.Entry.FileId)).Select(r => r.Entry).ToList();
    }

    private void OnOpenWatchFolder(object sender, RoutedEventArgs e)
    {
        var folder = _watcher?.Folder ?? _service.WatchFolder;
        if (!SafeLaunch.OpenFolder(folder)) Say($"Explorer could not open {folder}.", danger: true);
    }

    private void OnChangeWatchFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Folder your browser saves downloads to",
            InitialDirectory = _service.WatchFolder
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        _service.SetWatchFolder(dialog.FolderName);
        StartWatching(_service.WatchFolder);
    }

    private void OnRemoveChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _service.RemoveAfterAdding = RemoveBox.IsChecked == true;
    }

    // ── downloading ──────────────────────────────────────────────────────────

    private void OnDownloadRow(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ManualDownloadRow row) return;
        OpenDownloadPage(row);
    }

    private void OpenDownloadPage(ManualDownloadRow row)
    {
        if (!SafeLaunch.OpenUrl(row.Entry.DownloadPageUrl))
        {
            Say($"Your browser could not be opened for {row.Name}. Copy the link from the row's menu instead.", danger: true);
            return;
        }
        row.Opened = true;
        Say($"Opened {row.Name}'s download page. It starts by itself after a few seconds.");
    }

    /// <summary>Opens every waiting file's page, a moment apart, so neither the browser nor CurseForge
    /// gets them all in the same instant.</summary>
    private async void OnOpenAll(object sender, RoutedEventArgs e)
    {
        var waiting = _rows.Where(r => r.IsWaiting).ToList();
        if (waiting.Count == 0) return;
        if (waiting.Count > 10 && !await AppDialog.ConfirmAsync(Window.GetWindow(this),
                $"Open {waiting.Count} download pages?",
                $"Your browser gets {waiting.Count} new tabs, one per file. Each starts its download by itself, "
                + "and the files are added here as they arrive.", "Open them"))
            return;

        OpenAllButton.IsEnabled = false;
        try
        {
            for (var i = 0; i < waiting.Count; i++)
            {
                if (_result.Task.IsCompleted) return;
                if (!waiting[i].IsWaiting) continue;
                if (!SafeLaunch.OpenUrl(waiting[i].Entry.DownloadPageUrl))
                {
                    Say("Your browser could not be opened. Copy the links from the rows' menus instead.", danger: true);
                    return;
                }
                waiting[i].Opened = true;
                Say($"Opening download pages... {i + 1} of {waiting.Count}");
                if (i < waiting.Count - 1) await Task.Delay(900);
            }
            Say("All download pages are open. The files are added here as they finish downloading.");
        }
        finally { UpdateChrome(); }
    }

    // ── adding files ─────────────────────────────────────────────────────────

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Files you downloaded for " + _packName,
            Filter = "Mods and packs (*.jar;*.zip)|*.jar;*.zip|All files (*.*)|*.*",
            Multiselect = true,
            InitialDirectory = _service.WatchFolder
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        _ = AddFilesAsync(dialog.FileNames);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
            _ = AddFilesAsync(paths.Where(File.Exists).ToArray());
        e.Handled = true;
    }

    /// <summary>Matches each picked file to the entry it is, puts the matches in place and says what
    /// happened to the rest.</summary>
    private async Task AddFilesAsync(IReadOnlyList<string> paths)
    {
        var added = 0;
        var problems = new List<string>();
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            (string Sha1, long Fingerprint) hashes;
            long length;
            try
            {
                length = new FileInfo(path).Length;
                hashes = await Task.Run(() => ManualDownloadService.Hash(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{name} could not be read: {ex.Message}");
                continue;
            }

            var row = _rows.FirstOrDefault(r => r.State != ManualDownloadState.Added
                && ManualDownloadService.Matches(r.Entry, name, length, hashes.Sha1, hashes.Fingerprint));
            if (row is null)
            {
                var already = _rows.FirstOrDefault(r =>
                    ManualDownloadService.Matches(r.Entry, name, length, hashes.Sha1, hashes.Fingerprint));
                var sameName = _rows.FirstOrDefault(r =>
                    string.Equals(r.Entry.FileName, name, StringComparison.OrdinalIgnoreCase));
                problems.Add(already is not null
                    ? $"{name} is already in the instance."
                    : sameName is not null
                        ? $"{name} is not the same file CurseForge lists for {sameName.Name}. Download it again with its Download button."
                        : $"{name} is not one of the files this pack is missing.");
                continue;
            }
            if (await PlaceAsync(row.Entry.FileId, path, fromWatch: false)) added++;
        }

        if (problems.Count > 0)
            Say((added > 0 ? $"Added {added}. " : "") + string.Join(" ", problems), danger: true);
    }

    /// <summary>Puts a recognised file in the instance and updates its row. False when it could not be
    /// placed (the reason is on the status line).</summary>
    private async Task<bool> PlaceAsync(int fileId, string source, bool fromWatch)
    {
        var row = _rows.FirstOrDefault(r => r.Entry.FileId == fileId);
        if (row is null || row.State == ManualDownloadState.Added || !_placing.Add(fileId)) return false;
        row.Busy = true;
        // Only a file the card found in the watched folder is ever removed, and only when asked: a file
        // the user picked or dropped could be the only copy they have.
        var remove = fromWatch && RemoveBox.IsChecked == true;
        try
        {
            await Task.Run(() => _service.Place(_packId, row.Entry, source, remove));
            row.State = ManualDownloadState.Added;
            Say($"Added {row.Entry.FileName} to {row.Entry.Folder}.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Say($"{row.Entry.FileName} could not be added: {ex.Message}", danger: true);
            return false;
        }
        finally
        {
            row.Busy = false;
            _placing.Remove(fileId);
            UpdateChrome();
        }
    }

    // ── row menu ─────────────────────────────────────────────────────────────

    private void OnRowMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ManualDownloadRow row } anchor) return;
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        menu.Items.Add(Item("Open the project page", () =>
        {
            if (!SafeLaunch.OpenUrl(row.Entry.PageUrl)) Say("Your browser could not be opened.", danger: true);
        }));
        menu.Items.Add(Item("Copy the download link", () =>
            Say(ClipboardHelper.TrySetText(row.Entry.DownloadPageUrl)
                ? $"Copied the link for {row.Name}."
                : "The clipboard is busy. Try again in a moment.", danger: false)));
        menu.Items.Add(Item("Copy every waiting link", CopyAllLinks));
        menu.Items.Add(new Separator());
        if (row.State == ManualDownloadState.Waiting)
            menu.Items.Add(Item("Skip this file", () => SetState(row, ManualDownloadState.Skipped)));
        else if (row.State == ManualDownloadState.Skipped)
            menu.Items.Add(Item("Need this file after all", () => SetState(row, ManualDownloadState.Waiting)));
        menu.IsOpen = true;
    }

    private void CopyAllLinks()
    {
        var lines = _rows.Where(r => r.IsWaiting)
            .Select(r => $"{r.Name}: {r.Entry.DownloadPageUrl}").ToList();
        if (lines.Count == 0) { Say("Nothing is waiting."); return; }
        Say(ClipboardHelper.TrySetText(string.Join(Environment.NewLine, lines))
            ? $"Copied {lines.Count} link(s)."
            : "The clipboard is busy. Try again in a moment.");
    }

    private void SetState(ManualDownloadRow row, ManualDownloadState state)
    {
        try { _service.SetState(_packId, row.Entry.FileId, state); }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(ManualDownloadsCard), ex);
            Say("That could not be saved: " + ex.Message, danger: true);
            return;
        }
        row.State = state;
        Say(state == ManualDownloadState.Skipped
            ? $"Skipped {row.Name}. The instance won't ask for it again."
            : $"{row.Name} is wanted again.");
        if (state == ManualDownloadState.Waiting && _watcher is null) StartWatching(_service.WatchFolder);
        UpdateChrome();
    }

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception ex) { AppLog.LogError(nameof(ManualDownloadsCard), ex); }
        };
        return item;
    }

    // ── chrome ───────────────────────────────────────────────────────────────

    private void UpdateChrome()
    {
        var waiting = _rows.Count(r => r.IsWaiting);
        var added = _rows.Count(r => r.State == ManualDownloadState.Added);
        TitleText.Text = waiting switch
        {
            0 => "All set",
            1 => "1 file to download by hand",
            _ => $"{waiting} files to download by hand"
        };
        CountLabel.Text = $"{added} of {_rows.Count} added";
        OpenAllButton.IsEnabled = waiting > 0;
        OpenAllButton.Visibility = waiting > 1 ? Visibility.Visible : Visibility.Collapsed;
        AddFilesButton.IsEnabled = waiting > 0;

        if (_forLaunch)
        {
            PlayButton.Content = waiting == 0 ? "Play" : "Play anyway";
            PlayButton.ToolTip = waiting == 0
                ? null
                : "Launch without them. The game may crash or be missing what they add.";
        }
        else
        {
            CloseButton.Content = waiting == 0 ? "Done" : "Close";
            // Cleared rather than set to null: a null local Style would also shut out the theme's own.
            if (waiting == 0) CloseButton.Style = (Style)FindResource("AccentButton");
            else CloseButton.ClearValue(StyleProperty);
        }

        if (waiting == 0)
        {
            StopWatching();
            if (string.IsNullOrEmpty(StatusLabel.Text) || StatusLabel.Text.StartsWith("Added", StringComparison.Ordinal))
                Say(added == _rows.Count ? "Every file is in place." : "Every file is in place or skipped.");
        }
    }

    private void Say(string text, bool danger = false)
    {
        StatusLabel.Text = text;
        StatusLabel.SetResourceReference(TextBlock.ForegroundProperty, danger ? "DangerBrush" : "TextSecondaryBrush");
    }
}

/// <summary>One file in <see cref="ManualDownloadsCard"/>.</summary>
public sealed class ManualDownloadRow(ManualDownloadEntry entry) : INotifyPropertyChanged
{
    private ManualDownloadState _state = entry.State;
    private bool _opened;
    private bool _busy;

    public ManualDownloadEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public string? IconUrl => Entry.IconUrl;
    public string Initial => string.IsNullOrEmpty(Entry.Name) ? "?" : Entry.Name[..1].ToUpperInvariant();

    public string Meta
    {
        get
        {
            var parts = new List<string> { Entry.FileName };
            if (Entry.Size > 0) parts.Add(StorageUsageService.Size(Entry.Size));
            parts.Add(Entry.Folder);
            if (Entry.Disabled) parts.Add("switched off in the pack");
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>Why the launcher could not fetch it, when that was not the author's opt-out.</summary>
    public string? Note => Entry.OptedOut || string.IsNullOrWhiteSpace(Entry.Reason)
        ? null
        : "The launcher's download failed: " + Entry.Reason;

    public Visibility NoteVisibility => Note is null ? Visibility.Collapsed : Visibility.Visible;

    public ManualDownloadState State
    {
        get => _state;
        set { if (_state == value) return; _state = value; Changed(); ChangedStatus(); }
    }

    public bool IsWaiting => _state == ManualDownloadState.Waiting;

    /// <summary>Its download page has been opened from this card.</summary>
    public bool Opened
    {
        get => _opened;
        set { if (_opened == value) return; _opened = value; ChangedStatus(); }
    }

    /// <summary>Being copied into the instance right now.</summary>
    public bool Busy
    {
        get => _busy;
        set { if (_busy == value) return; _busy = value; ChangedStatus(); }
    }

    public string StatusText => _busy ? "Adding..." : _state switch
    {
        ManualDownloadState.Added => "Added",
        ManualDownloadState.Skipped => "Skipped",
        _ => _opened ? "Waiting for the file" : "Missing"
    };

    public Brush? PillBackground => Find(_state switch
    {
        ManualDownloadState.Added => "SuccessBrush",
        ManualDownloadState.Waiting when !_busy && !_opened => "WarningBrush",
        _ => "Surface4Brush"
    });

    public Brush? PillForeground => Find(_state == ManualDownloadState.Added
                                         || (_state == ManualDownloadState.Waiting && !_busy && !_opened)
        ? "TextOnAccentBrush"
        : "TextSecondaryBrush");

    public Visibility DownloadVisibility => IsWaiting && !_busy ? Visibility.Visible : Visibility.Collapsed;

    public string DownloadTip => $"Opens {Entry.DownloadPageUrl}";

    private static Brush? Find(string key) => Application.Current?.TryFindResource(key) as Brush;

    private void ChangedStatus()
    {
        Changed(nameof(StatusText));
        Changed(nameof(PillBackground));
        Changed(nameof(PillForeground));
        Changed(nameof(DownloadVisibility));
        Changed(nameof(IsWaiting));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
