using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>
/// Walks the user through files the launcher may not download (a CurseForge author's opt-out): a
/// Download button per file that opens its page on curseforge.com, and a watch on the Downloads folder
/// that moves each file into the instance as soon as it lands there, recognised by size and fingerprint.
/// The Windows launcher's ManualDownloadsCard.
/// </summary>
/// <remarks>
/// <para>Opened when a modpack import finishes with such files, when the mod browser, the Mods list or an
/// update hands one over (<see cref="ManualDownloadService.Needed"/>, which <see cref="MainWindow"/>
/// answers), from the instance's chip, and before Play while files are still missing. In that last case
/// it also offers to play anyway, and closing it means "don't launch".</para>
/// <para>Files can also be picked with Add Files or dropped on the card, for anyone whose browser saves
/// somewhere else or who got them from the CurseForge app.</para>
/// </remarks>
public partial class ManualDownloadsCard : UserControl
{
    /// <summary>The card on screen per instance, so the import finishing, a Play click and a mod picked
    /// in the browser don't stack two over each other: a later request adds its files to the open one.</summary>
    private static readonly Dictionary<Guid, ManualDownloadsCard> Open = [];

    private readonly MainWindow _shell;
    private readonly Guid _packId;
    private readonly string _packName;
    private readonly bool _forLaunch;
    private readonly ManualDownloadService _service = App.State.ManualDownloads;
    private readonly ObservableCollection<ManualDownloadRow> _rows = [];
    private readonly TaskCompletionSource<bool> _result = new();
    private readonly HashSet<int> _placing = [];

    /// <summary>The files this card is about, when a page asked for some (the mod browser, an update);
    /// null shows every file the instance is missing, as after an import.</summary>
    private readonly HashSet<int>? _focus;
    private ManualDownloadWatcher? _watcher;
    private bool _suppressEvents;
    private bool _onScreen;

    /// <summary>What the watcher looks for, read on its own thread: a copy of the rows, taken on the UI
    /// thread whenever they change, so the watcher never walks the collection the UI is changing.</summary>
    private volatile ManualDownloadEntry[] _waitingSnapshot = [];

    /// <summary>For the XAML previewer only.</summary>
    public ManualDownloadsCard()
    {
        InitializeComponent();
        _shell = MainWindow.Current;
        _packName = "";
    }

    private ManualDownloadsCard(MainWindow shell, Guid packId, string packName, ManualDownloadList list,
        bool forLaunch, HashSet<int>? focus)
    {
        InitializeComponent();
        _shell = shell;
        _packId = packId;
        _packName = packName;
        _forLaunch = forLaunch;
        _focus = focus;

        foreach (var file in list.Files.Where(Shows))
            _rows.Add(new ManualDownloadRow(file));
        FileList.ItemsSource = _rows;

        PackLabel.Text = string.IsNullOrWhiteSpace(list.Source) || focus is not null
            ? packName
            : $"{packName}  ·  from {list.Source}";
        if (forLaunch)
        {
            PlayButton.IsVisible = true;
            CloseButton.Content = "Cancel";
        }
        if (focus is not null) IntroText.Text = PickedIntro(_rows.Count);

        _suppressEvents = true;
        RemoveBox.IsChecked = _service.RemoveAfterAdding;
        _suppressEvents = false;

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // A card stacked over this one (a question) takes it off screen for a while: the watch stops
        // and starts with it, and the card stays open.
        AttachedToVisualTree += (_, _) =>
        {
            _onScreen = true;
            if (_result.Task.IsCompleted) return;
            StartWatching(_service.WatchFolder);
            _service.Changed -= OnListChanged;
            _service.Changed += OnListChanged;
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _onScreen = false;
            _service.Changed -= OnListChanged;
            StopWatching();
        };
        UpdateChrome();
    }

    /// <summary>The intro for files picked in the browser or an update, rather than a pack's.</summary>
    private static string PickedIntro(int count) => count == 1
        ? "Its author only lets CurseForge's own website and app download it. Click Download: the page opens "
          + "in your browser and starts the download by itself, and the launcher moves the file into the "
          + "instance as soon as it lands in your Downloads folder."
        : "Their authors only let CurseForge's own website and app download these. Click Download on each one: "
          + "the page opens in your browser and starts the download by itself, and the launcher moves each "
          + "file into the instance as soon as it lands in your Downloads folder.";

    private bool Shows(ManualDownloadEntry file) => _focus is null || _focus.Contains(file.FileId);

    /// <summary>
    /// Shows the card for <paramref name="packId"/> if it has files still waiting, and returns when it
    /// closes. True only when the user chose to play (<paramref name="forLaunch"/>); false when closed
    /// another way, when there was nothing to show, or when a card for this instance is already open
    /// (which then takes on <paramref name="focus"/>'s files as well).
    /// </summary>
    /// <param name="focus">Only these files (a page's request); null for every file the instance is
    /// missing.</param>
    public static async Task<bool> ShowAsync(MainWindow shell, Guid packId, string packName, bool forLaunch = false,
        IReadOnlyCollection<int>? focus = null)
    {
        if (Open.TryGetValue(packId, out var open))
        {
            open.Include(focus);
            return false;
        }

        // Files the launcher was still looking for after an earlier card closed come back onto this one.
        var carried = App.State.ManualDownloads.StopBackgroundWatch(packId);
        HashSet<int>? shown = focus is null ? null : [.. focus, .. carried];
        var list = await Task.Run(() => App.State.ManualDownloads.Reconcile(packId));
        if (list is null || !list.Files.Any(f => shown is null || shown.Contains(f.FileId)))
        {
            // Nothing to show after all; anything still looked for keeps being looked for.
            if (carried.Count > 0) App.State.ManualDownloads.WatchInBackground(packId, carried);
            return false;
        }
        if (forLaunch && list.Files.All(f => f.State != ManualDownloadState.Waiting)) return true;
        if (Open.TryGetValue(packId, out open))
        {
            open.Include(shown);
            return false;
        }

        var card = new ManualDownloadsCard(shell, packId, packName, list, forLaunch, shown);
        Open[packId] = card;
        try
        {
            return await shell.ShowCardAsync(card, card._result.Task, card.Cancel);
        }
        finally { Open.Remove(packId); }
    }

    /// <summary>
    /// The check before a launch: true to go ahead. Files still waiting bring up the card, where the
    /// user can get them or play anyway; an instance with nothing waiting passes straight through.
    /// </summary>
    public static async Task<bool> ConfirmLaunchAsync(MainWindow shell, Guid packId, string packName)
    {
        IReadOnlyList<ManualDownloadEntry> waiting;
        try { waiting = await Task.Run(() => App.State.ManualDownloads.Waiting(packId)); }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(ManualDownloadsCard), ex);
            return true;
        }
        if (waiting.Count == 0) return true;
        return await ShowAsync(shell, packId, packName, forLaunch: true);
    }

    /// <summary>Takes on more files while open: a second mod picked in the browser, or an import's whole
    /// list (null).</summary>
    private void Include(IReadOnlyCollection<int>? fileIds)
    {
        if (_focus is not null)
        {
            if (fileIds is null) return; // the import's own card comes up once this one closes
            foreach (var id in fileIds) _focus.Add(id);
        }
        _ = RefreshRowsAsync();
    }

    private void OnListChanged(Guid packId)
    {
        if (packId == _packId) _ = RefreshRowsAsync();
    }

    /// <summary>Adds rows for files that joined the list (and the card's files) since it opened, and
    /// picks up the states of the rest: a file dropped into its folder by hand counts as added.</summary>
    private async Task RefreshRowsAsync()
    {
        ManualDownloadList? list;
        try { list = await Task.Run(() => _service.Load(_packId)); }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(ManualDownloadsCard), ex);
            return;
        }
        if (list is null || _result.Task.IsCompleted) return;

        var added = false;
        foreach (var file in list.Files.Where(Shows))
        {
            var row = _rows.FirstOrDefault(r => r.Entry.FileId == file.FileId);
            if (row is null)
            {
                _rows.Add(new ManualDownloadRow(file));
                added = true;
            }
            else if (!row.Busy && row.State != file.State)
            {
                row.State = file.State;
            }
        }
        if (_focus is not null) IntroText.Text = PickedIntro(_rows.Count);
        if (added && _watcher is null && _onScreen) StartWatching(_service.WatchFolder);
        UpdateChrome();
    }

    private void Cancel() => Finish(false);

    private void Finish(bool play)
    {
        if (_result.Task.IsCompleted) return;
        StopWatching();
        _service.Changed -= OnListChanged;
        // A download page opened from here may still be counting down or downloading: closing the card
        // right after clicking Download is natural, so the launcher keeps looking for those files a while.
        var opened = _rows.Where(r => r.IsWaiting && r.Opened && !_placing.Contains(r.Entry.FileId))
            .Select(r => r.Entry.FileId).ToList();
        if (opened.Count > 0) _service.WatchInBackground(_packId, opened);
        _result.TrySetResult(play);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Finish(false);
    private void OnPlay(object? sender, RoutedEventArgs e) => Finish(true);

    // ── watching ─────────────────────────────────────────────────────────────

    private void StartWatching(string folder)
    {
        StopWatching();
        WatchLabel.Text = $"Looking for the files in {folder}";
        ToolTip.SetTip(WatchLabel, folder);
        if (!_rows.Any(r => r.IsWaiting)) return;
        if (!Directory.Exists(folder))
        {
            WatchLabel.Text = $"{folder} does not exist. Pick the folder your browser saves to.";
            return;
        }
        RefreshSnapshot();
        _watcher = new ManualDownloadWatcher(folder, () => _waitingSnapshot,
            (entry, path) => Dispatcher.UIThread.Post(() => _ = PlaceAsync(entry.FileId, path, fromWatch: true)));
        _ = _watcher.ScanAsync();
    }

    private void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    /// <summary>The entries still wanted, for the watcher: every waiting row not being placed now.</summary>
    private void RefreshSnapshot() =>
        _waitingSnapshot = _rows.Where(r => r.IsWaiting && !_placing.Contains(r.Entry.FileId)).Select(r => r.Entry).ToArray();

    private void OnOpenWatchFolder(object? sender, RoutedEventArgs e)
    {
        var folder = _watcher?.Folder ?? _service.WatchFolder;
        if (!SafeLaunch.OpenFolder(folder)) Say($"The file manager could not open {folder}.", danger: true);
    }

    private async void OnChangeWatchFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } storage) return;
            var start = await TryFolderAsync(storage, _service.WatchFolder);
            var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Folder your browser saves downloads to",
                SuggestedStartLocation = start,
                AllowMultiple = false
            });
            if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } folder) return;
            _service.SetWatchFolder(folder);
            StartWatching(_service.WatchFolder);
        }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(ManualDownloadsCard), ex);
            Say("The folder picker could not be opened.", danger: true);
        }
    }

    private void OnRemoveChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _service.RemoveAfterAdding = RemoveBox.IsChecked == true;
    }

    // ── downloading ──────────────────────────────────────────────────────────

    private void OnDownloadRow(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ManualDownloadRow row) return;
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
    private async void OnOpenAll(object? sender, RoutedEventArgs e)
    {
        var waiting = _rows.Where(r => r.IsWaiting).ToList();
        if (waiting.Count == 0) return;
        if (waiting.Count > 10 && !await _shell.ConfirmAsync(
                $"Open {waiting.Count} download pages?",
                $"Your browser gets {waiting.Count} new tabs, one per file. Each starts its download by itself, "
                + "and the files are added here as they arrive.", "Open Them"))
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

    private async void OnAddFiles(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage) return;
            var start = await TryFolderAsync(storage, _service.WatchFolder);
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Files you downloaded for " + _packName,
                AllowMultiple = true,
                SuggestedStartLocation = start,
                FileTypeFilter =
                [
                    new FilePickerFileType("Mods and packs") { Patterns = ["*.jar", "*.zip"] },
                    FilePickerFileTypes.All
                ]
            });
            var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            if (paths.Count > 0) await AddFilesAsync(paths);
        }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(ManualDownloadsCard), ex);
            Say("The file picker could not be opened.", danger: true);
        }
    }

    private static async Task<IStorageFolder?> TryFolderAsync(IStorageProvider storage, string path)
    {
        try { return Directory.Exists(path) ? await storage.TryGetFolderFromPathAsync(path) : null; }
        catch { return null; }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = files ? DragDropEffects.Copy : DragDropEffects.None;
        CardBorder.Classes.Set("dropTarget", files);
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => CardBorder.Classes.Set("dropTarget", false);

    private void OnDrop(object? sender, DragEventArgs e)
    {
        CardBorder.Classes.Set("dropTarget", false);
        var paths = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>()
            .Where(File.Exists).ToList() ?? [];
        if (paths.Count > 0) _ = AddFilesAsync(paths);
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
        RefreshSnapshot();
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

    private void OnRowMenu(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ManualDownloadRow row } anchor) return;
        var menu = new MenuFlyout();
        menu.Items.Add(Item("Open Project Page", () =>
        {
            if (!SafeLaunch.OpenUrl(row.Entry.PageUrl)) Say("Your browser could not be opened.", danger: true);
        }));
        menu.Items.Add(Item("Copy Download Link", () =>
            _ = CopyAsync(row.Entry.DownloadPageUrl, $"Copied the link for {row.Name}.")));
        menu.Items.Add(Item("Copy All Links", CopyAllLinks));
        if (row.State != ManualDownloadState.Added) menu.Items.Add(new Separator());
        if (row.State == ManualDownloadState.Waiting)
            menu.Items.Add(Item("Skip This File", () => SetState(row, ManualDownloadState.Skipped)));
        else if (row.State == ManualDownloadState.Skipped)
            menu.Items.Add(Item("Need It Again", () => SetState(row, ManualDownloadState.Waiting)));
        menu.ShowAt(anchor);
    }

    private void CopyAllLinks()
    {
        var lines = _rows.Where(r => r.IsWaiting)
            .Select(r => $"{r.Name}: {r.Entry.DownloadPageUrl}").ToList();
        if (lines.Count == 0) { Say("Nothing is waiting."); return; }
        _ = CopyAsync(string.Join(Environment.NewLine, lines), $"Copied {lines.Count} link(s).");
    }

    private async Task CopyAsync(string text, string done)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) throw new InvalidOperationException("No clipboard.");
            await clipboard.SetTextAsync(text);
            Say(done);
        }
        catch (Exception ex)
        {
            AppLog.Log("manual-downloads", "Could not copy: " + ex.Message);
            Say("The clipboard is busy. Try again in a moment.");
        }
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
        if (state == ManualDownloadState.Waiting && _watcher is null && _onScreen) StartWatching(_service.WatchFolder);
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
        OpenAllButton.IsVisible = waiting > 1;
        AddFilesButton.IsEnabled = waiting > 0;

        // One accent at a time: Play before a launch; otherwise the one Download there is to click,
        // Download All when there are several, and Done once nothing is left.
        foreach (var row in _rows) row.DownloadAccent = !_forLaunch && waiting == 1;
        OpenAllButton.Classes.Set("primary", !_forLaunch && waiting > 1);
        if (_forLaunch)
        {
            PlayButton.Content = waiting == 0 ? "Play" : "Play Anyway";
            ToolTip.SetTip(PlayButton, waiting == 0 ? null : "Launch without them. The game may crash or be missing what they add.");
        }
        else
        {
            CloseButton.Content = waiting == 0 ? "Done" : "Close";
            CloseButton.Classes.Set("primary", waiting == 0);
        }

        if (waiting == 0)
        {
            StopWatching();
            if (string.IsNullOrEmpty(StatusLabel.Text) || StatusLabel.Text.StartsWith("Added", StringComparison.Ordinal))
                Say(added == _rows.Count ? "Every file is in place." : "Every file is in place or skipped.");
        }
        RefreshSnapshot();
    }

    private void Say(string text, bool danger = false)
    {
        StatusLabel.Text = text;
        StatusLabel.Classes.Set("danger", danger);
    }
}

/// <summary>One file in <see cref="ManualDownloadsCard"/>.</summary>
public sealed class ManualDownloadRow(ManualDownloadEntry entry) : INotifyPropertyChanged
{
    private ManualDownloadState _state = entry.State;
    private bool _opened;
    private bool _busy;
    private bool _accent;

    public ManualDownloadEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public string? IconUrl => Entry.IconUrl;
    public string Initial => ModText.Initial(Entry.Name);

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

    public bool HasNote => Note is not null;

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

    /// <summary>Its Download button is the card's accent (the only file left to get).</summary>
    public bool DownloadAccent
    {
        get => _accent;
        set { if (_accent == value) return; _accent = value; Changed(); }
    }

    public string StatusText => _busy ? "Adding..." : _state switch
    {
        ManualDownloadState.Added => "Added",
        ManualDownloadState.Skipped => "Skipped",
        _ => _opened ? "Waiting for the file" : "Missing"
    };

    public bool PillWarning => _state == ManualDownloadState.Waiting && !_busy && !_opened;
    public bool PillSuccess => _state == ManualDownloadState.Added;
    public bool DownloadVisible => IsWaiting && !_busy;
    public string DownloadTip => $"Opens {Entry.DownloadPageUrl}";

    private void ChangedStatus()
    {
        Changed(nameof(StatusText));
        Changed(nameof(PillWarning));
        Changed(nameof(PillSuccess));
        Changed(nameof(DownloadVisible));
        Changed(nameof(IsWaiting));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
