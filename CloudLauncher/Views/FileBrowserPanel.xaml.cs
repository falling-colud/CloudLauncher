using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// The file manager for one instance: both panes, every file operation, and the filters that show
/// what syncs.
/// </summary>
/// <remarks>
/// <para>Self-contained: the host builds it and calls <see cref="Load"/> once.</para>
/// <para>Both panes show <c>game/</c>: the left is the folder tree, the right is what Shared rules
/// cover. Moving a file "to shared" writes a rule and never moves bytes.</para>
/// <para>The instance is read once into an <see cref="InstanceFileIndex"/> that both panes list
/// from. Rule changes re-classify it in memory (<see cref="ReclassifyAsync"/>), file operations
/// re-read the folders they touched (<see cref="RescanAsync"/>), and outside changes come in through
/// a watcher, held while the game runs. Only Refresh walks the whole tree.</para>
/// <para>Deletes move files to <c>&lt;packRoot&gt;/.trash/&lt;timestamp&gt;/</c>, the launcher's only
/// undo. <c>.trash</c> is outside <c>game/</c>, so nothing in it is launched, listed or uploaded.</para>
/// </remarks>
public partial class FileBrowserPanel : UserControl
{
    private MainWindow _shell = null!;
    private PackDetail _pack = null!;
    private Guid _packId;
    private bool _isOwner;

    /// <summary>False for a collaborator who may read the instance but not change it. Everything
    /// that writes is disabled rather than hidden, with the reason on the tooltip.</summary>
    private bool _canWrite;

    // One file operation at a time; concurrent bulk moves over one tree can corrupt the instance.
    private bool _working;
    private CancellationTokenSource? _workCts;

    // The Game pane can be re-pointed at <packRoot>/server, the server-override folder.
    private bool _serverMode;

    // Which pane a new file, a new folder or an import belongs to.
    private FolderView _activePane = null!;

    // The in-memory read of game/ (and of the server-override folder while that is showing). The
    // Shared pane is a filtered view of the same index. Null until the first read has finished.
    private InstanceFileIndex? _gameIndex;
    private InstanceFileIndex? _serverIndex;
    private int _refreshPass;
    private int _readsInFlight;

    // Outside changes (an editor saving, Explorer, the game) arrive through the watcher: noted per
    // folder on its thread, drained to the UI thread once, then applied as shallow re-reads.
    private FileSystemWatcher? _watcher;
    private readonly HashSet<string> _pendingDirs = new(StringComparer.OrdinalIgnoreCase);
    private int _pendingPosted;
    private readonly HashSet<string> _dirtyDirs = new(StringComparer.OrdinalIgnoreCase);
    private bool _dirtyAll;
    private bool _heldWhileRunning;
    private readonly System.Windows.Threading.DispatcherTimer _watchDebounce =
        new() { Interval = TimeSpan.FromMilliseconds(1500) };

    private FolderEntryKind? _kindFilter;
    private SyncFilter _syncFilter = SyncFilter.Any;
    private FolderSortField _sortField = FolderSortField.Name;
    private bool _sortDescending;

    private readonly System.Windows.Threading.DispatcherTimer _searchDebounce =
        new() { Interval = TimeSpan.FromMilliseconds(180) };

    /// <summary>State for the content-search results list (searching, found, nothing found, failed).
    /// The file panes use their own empty labels instead.</summary>
    private PageState _searchState = null!;

    /// <summary>Content search stops after this many hits; past that, a better query helps more than a
    /// longer list.</summary>
    private const int MaxContentHits = 400;

    public FileBrowserPanel()
    {
        InitializeComponent();
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            ApplySearchText();
        };
        _watchDebounce.Tick += (_, _) =>
        {
            _watchDebounce.Stop();
            ApplyWatchedChanges();
        };
    }

    // ── entry point ──────────────────────────────────────────────────────────

    /// <summary>Points the panel at one instance and does the first read. Call once.</summary>
    public void Load(MainWindow shell, PackDetail pack)
    {
        _shell = shell;
        _pack = pack;
        _packId = pack.Id;
        _isOwner = App.State.OwnsPack(pack.Id, pack.OwnerId);
        _canWrite = _isOwner || pack.EffectivePermissions.HasFlag(PackPermissions.UploadShared);
        _activePane = GameView;

        GameView.FilesDropped += OnFilesDropped;
        SharedView.FilesDropped += OnFilesDropped;
        // Dragging between panes changes a rule: onto Shared shares, onto Game unshares. Only a drop
        // onto a folder within the Game pane moves files.
        GameView.PaneDropped += (source, paths) => OnPaneDropped(source, GameView, paths);
        SharedView.PaneDropped += (source, paths) => OnPaneDropped(source, SharedView, paths);
        GameView.MoveIntoFolderRequested += (paths, folder) => _ = MoveEntriesAsync(GameView, paths, folder);
        GameView.FileActivated += OpenInEditor;
        SharedView.FileActivated += OpenInEditor;
        GameView.ExternalFilesDropped += (root, dir, files) => OnExternalFilesDropped(GameView, root, dir, files);
        SharedView.ExternalFilesDropped += (root, dir, files) => OnExternalFilesDropped(SharedView, root, dir, files);
        GameView.DeleteRequested += () => { _activePane = GameView; OnBulkDelete(this, new RoutedEventArgs()); };
        SharedView.DeleteRequested += () => { _activePane = SharedView; OnBulkDelete(this, new RoutedEventArgs()); };
        GameView.RenameRequested += () => _ = RenameSelectedAsync(GameView);
        SharedView.RenameRequested += () => _ = RenameSelectedAsync(SharedView);
        // The active pane follows the pointer, not the selection: a refresh re-selects rows in both
        // panes, which would make the last pane to finish reading the target of New file or Delete.
        GameView.PreviewMouseDown += (_, _) => { _activePane = GameView; UpdateSelectionBar(); };
        SharedView.PreviewMouseDown += (_, _) => { _activePane = SharedView; UpdateSelectionBar(); };
        GameView.SelectionChanged += (_, _) => UpdateSelectionBar();
        SharedView.SelectionChanged += (_, _) => UpdateSelectionBar();
        GameView.Navigated += _ => { BuildShortcutStrip(); UpdateSelectionBar(); };

        // One search box serves both panes, so hide each pane's own.
        GameView.ShowSearchBox = false;
        SharedView.ShowSearchBox = false;
        GameView.MeasureFolderSizes = true;
        SharedView.MeasureFolderSizes = true;
        // The left pane shows ignored files too (dimmed and badged); otherwise the logs/, saves/ and
        // screenshots/ shortcuts would open folders the default rules hide.
        GameView.ShowIgnored = true;
        GameView.EntryFilter = PassesFilters;
        SharedView.EntryFilter = PassesFilters;

        // The search has its own busy bar and Cancel, so PageState only gets the count slot.
        _searchState = new PageState(SearchResultsList, SearchStateHost, nameof(FileBrowserPanel))
            .Copy(PageCopy.Results)
            .EmptyCopy("Nothing contains that",
                       "No text file in this instance has that in it. Binary files - jars, images, "
                       + "region files - are never read.")
            .Slots(SearchResultsSub);
        _searchState.CancelRequested += OnStopWorkRequested;

        BuildFilterStrip();
        ApplyPermissions();
        UpdateSortMenu();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        // If the host attached the panel before calling Load, Loaded has already fired.
        if (IsLoaded) OnLoaded(this, new RoutedEventArgs());

        _ = RefreshAsync();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // The page can be open in the side panel while focus is elsewhere, so the shortcuts hang on
        // the window and check IsVisible rather than focus.
        if (Window.GetWindow(this) is { } window)
        {
            window.PreviewKeyDown -= OnShellKeyDown;
            window.PreviewKeyDown += OnShellKeyDown;
        }
        StartWatching();
        App.State.Instances.StateChanged -= OnInstanceStateChanged;
        App.State.Instances.StateChanged += OnInstanceStateChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window) window.PreviewKeyDown -= OnShellKeyDown;
        StopWatching();
        App.State.Instances.StateChanged -= OnInstanceStateChanged;
        _workCts?.Cancel();
    }

    private void OnShellKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsVisible) return;

        if (e.Key == Key.F5) { _ = RefreshAsync(); e.Handled = true; return; }

        if (e.Key == Key.F && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            if (ContentSearchBar.Visibility != Visibility.Visible) OnToggleContentSearch(this, new RoutedEventArgs());
            ContentQueryBox.Focus();
            ContentQueryBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && (SearchResultsPanel.Visibility == Visibility.Visible ||
                                    TrashPanel.Visibility == Visibility.Visible))
        {
            ShowPanes();
            e.Handled = true;
        }
    }

    // ── permissions ──────────────────────────────────────────────────────────

    /// <summary>
    /// Disables everything that writes when this instance is someone else's and the user has no
    /// upload rights. Disabled rather than hidden, with the reason on the tooltip.
    /// </summary>
    private void ApplyPermissions()
    {
        if (_canWrite) return;

        const string why = "This instance belongs to someone else and you have read-only access.";
        foreach (var control in new FrameworkElement[]
                 {
                     ImportButton, NewButton, ApplyRulesButton, ShareButton,
                     BulkDeleteButton, BulkMoveButton, BulkZipButton,
                     NewFileMenuItem, NewFolderMenuItem,
                     GameNewFileItem, GameNewFolderItem, GameRenameItem, GameDeleteItem,
                     GameMoveItem, GameUnzipItem, GameShareItem,
                     GameRuleSharedItem, GameRuleIgnoredItem, GameRuleFolderItem,
                     SharedRenameItem, SharedDeleteItem, SharedUnshareItem,
                     TrashRestoreButton, TrashPurgeButton
                 })
        {
            control.IsEnabled = false;
            ToolTipService.SetShowOnDisabled(control, true);
            control.ToolTip = why;
        }
    }

    // ── refresh ──────────────────────────────────────────────────────────────

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    /// <summary>Re-reads the rules and the whole instance folder, then re-lists both panes.</summary>
    /// <remarks>Only for the first open, Refresh/F5, and fallbacks when there is no index to patch.
    /// Everything else goes through <see cref="ReclassifyAsync"/> or <see cref="RescanAsync"/>. Rows
    /// on screen stay under the hairline; only an empty pane shows a reading state.</remarks>
    public async Task RefreshAsync()
    {
        var pass = ++_refreshPass;
        var packRoot = App.State.Packs.PackRoot(_packId);
        var gameDir = App.State.Packs.GameDir(_packId);
        var rules = App.State.Rules.Load(packRoot);

        PackRootHint.Text = "Instance root: " + packRoot;
        ConfigurePanes(rules);
        BuildShortcutStrip();
        UpdateTrashButton();

        if (GameView.VisibleEntries.Count == 0) GameView.BeginRead();
        if (SharedView.VisibleEntries.Count == 0) SharedView.BeginRead();

        BeginReading();
        try
        {
            var started = Stopwatch.GetTimestamp();
            var gameTask = InstanceFileIndexes.GetAsync(gameDir, rules, fresh: true);
            var serverTask = _serverMode ? InstanceFileIndexes.GetAsync(ServerOverrideDir(), null, fresh: true) : null;
            var game = await gameTask;
            var server = serverTask is null ? null : await serverTask;
            if (pass != _refreshPass) return;

            _gameIndex = game;
            _serverIndex = server;
            // The walk already covers anything the watcher noted meanwhile.
            _dirtyDirs.Clear();
            _dirtyAll = false;
            AttachIndexes();
            await Task.WhenAll(GameView.RefreshAsync(), SharedView.RefreshAsync());
            if (pass != _refreshPass) return;

            var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            Status($"Read {game.FileCount:N0} file(s) in {seconds:0.0} s · {game.SharedFileCount:N0} shared.");
        }
        catch (Exception ex)
        {
            AppLog.LogError("files.refresh", ex);
            Status("Could not read the instance folder - see the Logs tab.");
        }
        finally { EndReading(); }
        UpdateSelectionBar();
    }

    /// <summary>Points both panes at the right folders and rules. Cheap; sets no index.</summary>
    private void ConfigurePanes(List<PackRule> rules)
    {
        var gameDir = App.State.Packs.GameDir(_packId);
        GameView.Root = _serverMode ? ServerOverrideDir() : gameDir;
        // The server-override folder isn't part of the sync tree, so game/'s rules don't apply.
        GameView.Rules = _serverMode ? null : rules;
        // The left pane shows everything on disk, including shared files the right pane also lists.
        // Hiding them would leave folders like mods/ empty on a hosted instance.
        GameView.HideShared = false;
        GameView.ShowIgnored = !_serverMode;

        SharedView.Root = gameDir;
        SharedView.Rules = rules;
        SharedView.ShowOnlyShared = true;
    }

    /// <summary>Hands each pane the index for the folder it is showing.</summary>
    private void AttachIndexes()
    {
        GameView.Index = _serverMode ? _serverIndex : _gameIndex;
        SharedView.Index = _gameIndex;
    }

    /// <summary>Re-lists both panes from what is in memory. Selection, scroll and sort stay put.</summary>
    private void RelistPanes()
    {
        GameView.Relist();
        SharedView.Relist();
        UpdateSelectionBar();
    }

    /// <summary>
    /// Applies a changed rule set to the files already in memory. No disk is touched: the index is
    /// re-classified on a worker thread and both panes re-list from it.
    /// </summary>
    private async Task ReclassifyAsync(List<PackRule> rules)
    {
        GameView.Rules = _serverMode ? null : rules;
        SharedView.Rules = rules;
        var index = _gameIndex;
        if (index is null) { await RefreshAsync(); return; }

        try { await Task.Run(() => index.Reclassify(rules)); }
        catch (Exception ex) { AppLog.LogError("files.rules", ex); }
        RelistPanes();
    }

    /// <summary>
    /// Re-reads the folders a file operation touched (one level each) and re-lists both panes.
    /// Falls back to a full refresh when there is no index yet to patch.
    /// </summary>
    /// <param name="view">The pane the operation happened in, which says which index it changed:
    /// the Game pane can be showing the server-override folder.</param>
    private async Task RescanAsync(FolderView view, IEnumerable<string> dirRels)
    {
        var index = view.Index;
        if (index is null) { await RefreshAsync(); return; }

        var dirs = dirRels.Select(InstanceFileIndex.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        BeginReading();
        try { await Task.Run(() => index.RescanDirectories(dirs)); }
        catch (Exception ex) { AppLog.LogError("files.rescan", ex); }
        finally { EndReading(); }
        RelistPanes();
    }

    /// <summary>The hairline over the panes while the disk is being read. Counted, because a re-read
    /// of one folder can overlap a full refresh.</summary>
    private void BeginReading()
    {
        _readsInFlight++;
        ScanBar.Visibility = Visibility.Visible;
    }

    private void EndReading()
    {
        _readsInFlight = Math.Max(0, _readsInFlight - 1);
        if (_readsInFlight == 0) ScanBar.Visibility = Visibility.Collapsed;
    }

    // ── watching the folder ──────────────────────────────────────────────────

    /// <summary>
    /// Picks up changes made outside this page: a config saved in an editor, a jar dropped in through
    /// Explorer, the game writing its own files.
    /// </summary>
    /// <remarks>
    /// Events are coalesced per folder and applied after a pause as shallow re-reads. While the game
    /// runs they are only noted, and applied when it stops or on Refresh. A buffer overflow loses
    /// events, so it triggers a full refresh. If the folder can't be watched, Refresh still works.
    /// </remarks>
    private void StartWatching()
    {
        StopWatching();
        string gameDir;
        try { gameDir = GameDir(); }
        catch { return; }
        if (!Directory.Exists(gameDir)) return;

        try
        {
            var watcher = new FileSystemWatcher(gameDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                               NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024
            };
            watcher.Created += (_, e) => NoteChange(gameDir, e.FullPath);
            watcher.Deleted += (_, e) => NoteChange(gameDir, e.FullPath);
            watcher.Changed += (_, e) => NoteChange(gameDir, e.FullPath);
            watcher.Renamed += (_, e) => { NoteChange(gameDir, e.OldFullPath); NoteChange(gameDir, e.FullPath); };
            watcher.Error += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                _dirtyAll = true;
                _watchDebounce.Stop();
                _watchDebounce.Start();
            });
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex)
        {
            AppLog.LogError("files.watch", ex);
        }
    }

    private void StopWatching()
    {
        _watchDebounce.Stop();
        if (_watcher is null) return;
        try
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }
        catch { /* already gone */ }
        _watcher = null;
    }

    /// <summary>Runs on the watcher's thread. Notes the path's folder (and the path itself if it is a
    /// folder), and posts one drain to the UI thread however many events arrive before it runs.</summary>
    private void NoteChange(string gameDir, string fullPath)
    {
        string rel;
        try { rel = InstanceFileIndex.Normalize(Path.GetRelativePath(gameDir, fullPath)); }
        catch { return; }
        if (rel.Length == 0 || rel.StartsWith("..", StringComparison.Ordinal)) return;

        var parent = InstanceFileIndex.ParentOf(rel);
        var self = Directory.Exists(fullPath) ? rel : null;
        lock (_pendingDirs)
        {
            _pendingDirs.Add(parent);
            if (self is not null) _pendingDirs.Add(self);
        }
        if (Interlocked.Exchange(ref _pendingPosted, 1) == 0)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, DrainPendingChanges);
    }

    private void DrainPendingChanges()
    {
        Interlocked.Exchange(ref _pendingPosted, 0);
        lock (_pendingDirs)
        {
            _dirtyDirs.UnionWith(_pendingDirs);
            _pendingDirs.Clear();
        }
        _watchDebounce.Stop();
        _watchDebounce.Start();
    }

    private void ApplyWatchedChanges()
    {
        if (_dirtyDirs.Count == 0 && !_dirtyAll) return;
        // The first read is still running and will see whatever these were.
        if (_gameIndex is null) { _dirtyDirs.Clear(); _dirtyAll = false; return; }
        // A file operation on this page re-reads its own folders when it finishes; wait for it.
        if (_working) { _watchDebounce.Start(); return; }

        if (App.State.Instances.GetStatus(_packId) != MinecraftInstanceStatus.Idle)
        {
            if (!_heldWhileRunning)
                Status("Minecraft is running - the file list catches up when it stops, or press Refresh.");
            _heldWhileRunning = true;
            return;
        }
        _heldWhileRunning = false;

        if (_dirtyAll)
        {
            _dirtyAll = false;
            _dirtyDirs.Clear();
            _ = RefreshAsync();
            return;
        }

        var dirs = _dirtyDirs.ToList();
        _dirtyDirs.Clear();
        // The Shared pane always holds the game index, whatever the Game pane is showing.
        _ = RescanAsync(SharedView, dirs);
    }

    private void OnInstanceStateChanged(Guid packId)
    {
        if (packId != _packId || !_heldWhileRunning) return;
        if (App.State.Instances.GetStatus(packId) != MinecraftInstanceStatus.Idle) return;
        _watchDebounce.Stop();
        _watchDebounce.Start();
    }

    private string GameDir() => App.State.Packs.GameDir(_packId);
    private string PackRoot() => App.State.Packs.PackRoot(_packId);
    private string ServerOverrideDir() => Path.Combine(PackRoot(), "server");
    private string TrashDir() => Path.Combine(PackRoot(), ".trash");

    /// <summary>Absolute path of a row in a pane, or null when the relative path would not stay inside
    /// the pane's folder. Both panes are views onto game/, except while the left one is showing the
    /// server-override folder.</summary>
    private string? AbsolutePathFor(FolderView view, string relativePath)
    {
        var root = view.Root ?? GameDir();
        return PathSafety.ResolveInside(root, relativePath);
    }

    /// <summary>A folder of the pane's tree by its relative path, "" being the root itself. Null when
    /// the path would leave the root.</summary>
    private static string? FolderInside(string root, string relativeDir) =>
        relativeDir.Length == 0 ? root : PathSafety.ResolveInside(root, relativeDir);

    private void Status(string message) => StatusLabel.Text = message;

    private void SetBusy(bool busy, string? line = null, bool cancellable = false)
    {
        _working = busy;
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyCancelButton.Visibility = busy && cancellable ? Visibility.Visible : Visibility.Collapsed;
        ContentSearchStopButton.Visibility = busy && cancellable ? Visibility.Visible : Visibility.Collapsed;
        ContentSearchGoButton.IsEnabled = !busy;
        if (line is not null) Status(line);
    }

    private void OnStopWork(object sender, RoutedEventArgs e) => OnStopWorkRequested();

    private void OnStopWorkRequested()
    {
        _workCts?.Cancel();
        PackJobs.For(_packId)?.Stop();
        Status("Stopping...");
    }

    // ── shortcut chips ───────────────────────────────────────────────────────

    /// <summary>One chip in the shortcut strip.</summary>
    private sealed class ShortcutChip
    {
        public string Label { get; init; } = "";
        public string Hint { get; init; } = "";
        /// <summary>Folder relative to game/, or "" for the root.</summary>
        public string Path { get; init; } = "";
        public bool IsServer { get; init; }
        public bool IsSelected { get; init; }
    }

    /// <summary>The folders people open most, in order. Only folders that exist get a chip.</summary>
    private static readonly (string Folder, string Hint)[] ShortcutFolders =
    [
        ("mods",          "The jars this instance loads"),
        ("config",        "Per-mod settings"),
        ("kubejs",        "KubeJS scripts"),
        ("defaultconfigs","What a new world copies its config from"),
        ("saves",         "Worlds"),
        ("screenshots",   "Screenshots"),
        ("logs",          "Game logs"),
        ("crash-reports", "Crash reports"),
        ("resourcepacks", "Resource packs"),
        ("shaderpacks",   "Shader packs"),
    ];

    private void BuildShortcutStrip()
    {
        var gameDir = GameDir();

        var chips = new List<ShortcutChip>
        {
            new()
            {
                Label = "Instance root", Hint = "Everything this instance launches with",
                Path = "", IsSelected = !_serverMode && GameView.CurrentRelativeDir.Length == 0
            }
        };

        foreach (var (folder, hint) in ShortcutFolders)
        {
            if (!Directory.Exists(Path.Combine(gameDir, folder))) continue;
            chips.Add(new ShortcutChip
            {
                Label = folder,
                Hint = hint,
                Path = folder,
                IsSelected = !_serverMode && string.Equals(GameView.CurrentRelativeDir, folder, StringComparison.OrdinalIgnoreCase)
            });
        }

        chips.Add(new ShortcutChip
        {
            Label = "server override",
            Hint = "Files laid over the mirror when you host a server from this instance - " +
                   "server-only mods, server.properties, ops.json.",
            IsServer = true,
            IsSelected = _serverMode
        });

        ShortcutStrip.ItemsSource = chips;
    }

    private void OnShortcutClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ShortcutChip chip }) return;

        if (chip.IsServer)
        {
            var dir = ServerOverrideDir();
            Directory.CreateDirectory(dir);
            _serverMode = true;
            GameHeaderTitle.Text = "Server override";
            GameHeaderSub.Text = "Laid over the server mirror at launch";
            GameHeaderGlyph.Text = "";
            GameView.NavigateToRoot();
            _ = RefreshAsync();
            Status("Showing " + dir);
            return;
        }

        if (_serverMode)
        {
            _serverMode = false;
            GameHeaderTitle.Text = "Game";
            GameHeaderSub.Text = "Every file in this instance";
            GameHeaderGlyph.Text = "";
            // Refresh first: until then the pane is still on the server-override index, which doesn't
            // have this folder.
            _ = LeaveServerModeAsync(chip.Path);
            return;
        }

        GameView.NavigateTo(chip.Path);
        BuildShortcutStrip();
    }

    private async Task LeaveServerModeAsync(string folder)
    {
        await RefreshAsync();
        if (_serverMode) return;   // the user went straight back to the server folder
        GameView.NavigateTo(folder);
        BuildShortcutStrip();
    }

    // ── filter chips ─────────────────────────────────────────────────────────

    /// <summary>Which sync classification a row must have to be listed.</summary>
    private enum SyncFilter { Any, Shared, Local, Ignored, Private, Unruled }

    private sealed class FilterChip
    {
        public string Label { get; init; } = "";
        public string Hint { get; init; } = "";
        public FolderEntryKind? Kind { get; init; }
        public SyncFilter? Sync { get; init; }
        public bool IsSelected { get; init; }
    }

    private void BuildFilterStrip()
    {
        var chips = new List<FilterChip>
        {
            new() { Label = "all", Hint = "Every kind of file", Kind = null, IsSelected = _kindFilter is null },
            new() { Label = "text", Hint = "Configs, scripts, anything the built-in editor opens", Kind = FolderEntryKind.Text, IsSelected = _kindFilter == FolderEntryKind.Text },
            new() { Label = "jars", Hint = "Mods and archives", Kind = FolderEntryKind.Jar, IsSelected = _kindFilter == FolderEntryKind.Jar },
            new() { Label = "images", Hint = "Textures and screenshots", Kind = FolderEntryKind.Image, IsSelected = _kindFilter == FolderEntryKind.Image },
            new() { Label = "world data", Hint = "Region files, level data, NBT", Kind = FolderEntryKind.WorldData, IsSelected = _kindFilter == FolderEntryKind.WorldData },
            new() { Label = "logs", Hint = "Logs and crash reports", Kind = FolderEntryKind.Log, IsSelected = _kindFilter == FolderEntryKind.Log },
            new() { Label = "other", Hint = "Everything else", Kind = FolderEntryKind.Other, IsSelected = _kindFilter == FolderEntryKind.Other },

            new() { Label = "any state", Hint = "No sync filter", Sync = SyncFilter.Any, IsSelected = _syncFilter == SyncFilter.Any },
            new() { Label = "shared", Hint = "Uploaded with this instance", Sync = SyncFilter.Shared, IsSelected = _syncFilter == SyncFilter.Shared },
            new() { Label = "local", Hint = "A rule keeps these on this PC", Sync = SyncFilter.Local, IsSelected = _syncFilter == SyncFilter.Local },
            new() { Label = "ignored", Hint = "Never uploaded, never downloaded", Sync = SyncFilter.Ignored, IsSelected = _syncFilter == SyncFilter.Ignored },
            new() { Label = "private", Hint = "Held back from upload by policy, whatever the rules say", Sync = SyncFilter.Private, IsSelected = _syncFilter == SyncFilter.Private },
            new() { Label = "unruled", Hint = "No rule matches these at all", Sync = SyncFilter.Unruled, IsSelected = _syncFilter == SyncFilter.Unruled },
        };

        FilterStrip.ItemsSource = chips;
    }

    private void OnFilterChipClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: FilterChip chip }) return;

        if (chip.Sync is { } sync) _syncFilter = sync;
        else _kindFilter = chip.Kind;

        BuildFilterStrip();
        // A chip only filters what is shown: re-list, don't re-read.
        RelistPanes();
    }

    private bool PassesFilters(FolderEntry entry)
    {
        if (_kindFilter is { } kind && entry.Kind != kind) return false;
        return _syncFilter switch
        {
            SyncFilter.Shared  => entry.IsAutoShared,
            SyncFilter.Local   => entry.IsAutoLocal,
            SyncFilter.Ignored => entry.IsIgnored,
            SyncFilter.Private => entry.IsPrivate,
            SyncFilter.Unruled => !entry.IsAutoShared && !entry.IsAutoLocal && !entry.IsIgnored,
            _                  => true
        };
    }

    // ── sorting ──────────────────────────────────────────────────────────────

    private void OnSortMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private void OnSortMenuOpened(object sender, RoutedEventArgs e) => UpdateSortMenu();

    private void UpdateSortMenu()
    {
        SortNameItem.IsChecked = _sortField == FolderSortField.Name;
        SortModifiedItem.IsChecked = _sortField == FolderSortField.Date;
        SortSizeItem.IsChecked = _sortField == FolderSortField.Size;
        SortDescendingItem.IsChecked = _sortDescending;
    }

    private void OnSortPicked(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag }) return;
        _sortField = tag switch
        {
            "Date" => FolderSortField.Date,
            "Size" => FolderSortField.Size,
            _      => FolderSortField.Name
        };
        // Biggest and newest first is what someone switching to those columns is asking for.
        _sortDescending = _sortField is FolderSortField.Date or FolderSortField.Size;
        ApplySort();
    }

    private void OnSortDirectionPicked(object sender, RoutedEventArgs e)
    {
        _sortDescending = SortDescendingItem.IsChecked;
        ApplySort();
    }

    private void ApplySort()
    {
        GameView.SetSort(_sortField, _sortDescending);
        SharedView.SetSort(_sortField, _sortDescending);
        UpdateSortMenu();
    }

    // ── search box ───────────────────────────────────────────────────────────

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void ApplySearchText()
    {
        var text = SearchBox.Text.Trim();
        GameView.SetFilterText(text);
        SharedView.SetFilterText(text);
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SearchBox.Clear();
            ApplySearchText();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            _searchDebounce.Stop();
            ApplySearchText();
            e.Handled = true;
        }
    }

    // ── content search (grep) ────────────────────────────────────────────────

    private sealed class ContentHitRow
    {
        public string RelativePath { get; init; } = "";
        public string FullPath { get; init; } = "";
        public string Line { get; init; } = "";
        public int LineNumber { get; init; }
        public string LineLabel => $"line {LineNumber}";
    }

    private void OnToggleContentSearch(object sender, RoutedEventArgs e)
    {
        var showing = ContentSearchBar.Visibility == Visibility.Visible;
        ContentSearchBar.Visibility = showing ? Visibility.Collapsed : Visibility.Visible;
        if (showing) { ShowPanes(); return; }
        ContentQueryBox.Focus();
    }

    private void OnContentQueryKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OnRunContentSearch(sender, e); e.Handled = true; }
        else if (e.Key == Key.Escape) { OnToggleContentSearch(sender, new RoutedEventArgs()); e.Handled = true; }
    }

    private async void OnRunContentSearch(object sender, RoutedEventArgs e)
    {
        var query = ContentQueryBox.Text.Trim();
        if (query.Length < 2) { Status("Type at least two characters to search inside files."); return; }
        if (_working) { Status("Something else is still running."); return; }

        _workCts?.Cancel();
        _workCts = new CancellationTokenSource();
        var ct = _workCts.Token;

        // Clear old hits right away and show the loading state; old results don't answer a new query.
        SearchResultsList.ItemsSource = null;
        ShowSearchResults();
        _searchState.Begin($"Reading every text file in {_pack.Name} for '{query}'.", refreshing: false);
        SetBusy(true, $"Searching every file for '{query}'...", cancellable: true);

        try
        {
            var gameDir = GameDir();
            var packName = _pack.Name;
            var packId = _packId;

            var result = await Task.Run(() =>
            {
                var entries = CollectSearchableFiles(gameDir, packId, packName, ct);
                return (Files: entries.Count,
                        Hits: ConfigHubService.SearchContents(entries, query, MaxContentHits, null, ct));
            }, ct);

            var rows = result.Hits.Hits.Select(h => new ContentHitRow
            {
                RelativePath = h.File.RelativePath,
                FullPath = h.File.FullPath,
                Line = h.Line,
                LineNumber = h.LineNumber
            }).ToList();

            SearchResultsList.ItemsSource = rows;
            _searchState.Content(rows.Count, countText:
                $"{rows.Count} hit(s) for '{query}' in {result.Hits.FilesRead} file(s)" +
                (result.Hits.Skipped > 0 ? $" · {result.Hits.Skipped} skipped (too large or unreadable)" : ""));
            Status($"{rows.Count} hit(s) across {result.Files} searchable file(s).");
            AppLog.Log("files", $"Content search for '{query}' in {_pack.Name}: {rows.Count} hit(s).");
        }
        catch (OperationCanceledException)
        {
            _searchState.Cancelled("Search stopped.");
            Status("Search stopped.");
        }
        catch (Exception ex)
        {
            _searchState.Error("The instance could not be read to the end.", ex, "Could not finish the search");
            Status("The search could not finish - see the Logs tab.");
        }
        finally { SetBusy(false); }
    }

    /// <summary>
    /// Every file in the instance worth searching, as Config hub entries so its searcher works
    /// unchanged.
    /// </summary>
    /// <remarks>
    /// Binary files are dropped up front; grepping a 40 MB region file finds nothing and takes ages.
    /// <c>FileKind</c> is only used for the Config hub's labels, so folders outside its four roots
    /// just get the nearest one.
    /// </remarks>
    private static List<ConfigHubService.Entry> CollectSearchableFiles(
        string gameDir, Guid packId, string packName, CancellationToken ct)
    {
        var entries = new List<ConfigHubService.Entry>();
        if (!Directory.Exists(gameDir)) return entries;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(gameDir, "*", SearchOption.AllDirectories); }
        catch { return entries; }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            string rel;
            try { rel = Path.GetRelativePath(gameDir, file).Replace('\\', '/'); }
            catch { continue; }

            if (FolderEntry.KindOf(rel) != FolderEntryKind.Text) continue;

            var top = rel.Split('/')[0];
            if (top is "libraries" or "versions" or "assets" or ".mixin.out" or "node_modules") continue;

            FileInfo fi;
            try { fi = new FileInfo(file); } catch { continue; }
            if (fi.Length > ConfigHubService.MaxInspectBytes) continue;

            var kind = top.ToLowerInvariant() switch
            {
                "kubejs" => ConfigHubService.FileKind.KubeJs,
                "defaultconfigs" => ConfigHubService.FileKind.DefaultConfigs,
                "logs" => ConfigHubService.FileKind.KubeJsLog,
                _ => ConfigHubService.FileKind.Config
            };
            entries.Add(new ConfigHubService.Entry(packId, packName, rel, file, fi.Length, fi.LastWriteTimeUtc, kind));
        }

        return entries;
    }

    private void OnSearchResultActivated(object sender, MouseButtonEventArgs e)
    {
        if (SearchResultsList.SelectedItem is not ContentHitRow row) return;
        OpenInEditor(row.RelativePath);
    }

    private void ShowSearchResults()
    {
        PaneGrid.Visibility = Visibility.Collapsed;
        TrashPanel.Visibility = Visibility.Collapsed;
        SearchResultsPanel.Visibility = Visibility.Visible;
    }

    private void ShowPanes()
    {
        SearchResultsPanel.Visibility = Visibility.Collapsed;
        TrashPanel.Visibility = Visibility.Collapsed;
        PaneGrid.Visibility = Visibility.Visible;
    }

    // ── selection bar ────────────────────────────────────────────────────────

    private void UpdateSelectionBar()
    {
        var entries = _activePane.GetSelectedEntries();
        if (entries.Count == 0)
        {
            SelectionBar.Visibility = Visibility.Collapsed;
            return;
        }

        var files = entries.Count(en => !en.IsFolder);
        var folders = entries.Count - files;
        var bytes = entries.Sum(en => en.Size);

        var parts = new List<string>();
        if (files > 0) parts.Add($"{files} file(s)");
        if (folders > 0) parts.Add($"{folders} folder(s)");
        // Only show a byte total once every selected row has a size; a partial sum would mislead.
        var sizeKnown = entries.All(en => !en.IsFolder || en.Size > 0);
        var label = string.Join(" and ", parts) + " selected";
        if (sizeKnown && bytes > 0) label += $" · {ConfigHubService.FormatSize(bytes)}";

        SelectionLabel.Text = label;
        UpdateShareButton(entries);
        SelectionBar.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Share (accent) while anything selected is unshared; once everything is shared the button reads
    /// Unshare in the plain style. In the Shared pane it is always Unshare.
    /// </summary>
    private void UpdateShareButton(IReadOnlyList<FolderEntry> entries)
    {
        if (_serverMode)
        {
            // Rules do not reach the server-override folder, so neither does this button.
            ShareButton.Visibility = Visibility.Collapsed;
            return;
        }

        var unshare = ReferenceEquals(_activePane, SharedView) || entries.All(en => en.IsAutoShared);
        var n = entries.Count;
        ShareButton.Content = (unshare ? "Unshare" : "Share") + (n == 1 ? "" : $" {n} items");
        ShareButton.Style = (Style)FindResource(unshare ? "SubtleButton" : "AccentButton");
        if (_canWrite)
            ShareButton.ToolTip = unshare
                ? "Stop sharing the selection. The files stay on this PC."
                : "Mark the selection shared - it uploads with this instance. Nothing moves.";
        ShareButton.Visibility = Visibility.Visible;
    }

    /// <summary>The selection bar's Share / Unshare: writes the same rule as the context menu.</summary>
    private void OnShareButton(object sender, RoutedEventArgs e)
    {
        if (!RequireWrite()) return;
        if (ReferenceEquals(_activePane, SharedView)) { OnMoveSharedToGame(sender, e); return; }

        var entries = GameView.GetSelectedEntries();
        if (entries.Count == 0) { Status("Select a file first."); return; }
        if (entries.All(en => en.IsAutoShared))
            WriteRules(entries.Select(en => en.RelativePath).ToList(), RuleAction.Local,
                $"Removed {entries.Count} item(s) from sync. They stay on this PC.");
        else
            AddQuickRule(RuleAction.Shared, folderMode: false);
    }

    private void OnSelectAll(object sender, RoutedEventArgs e) => _activePane.SelectAll();
    private void OnClearSelection(object sender, RoutedEventArgs e) => _activePane.ClearSelection();

    /// <summary>The pane a context menu was opened on, so its items act on what was right-clicked.</summary>
    private void OnGameMenuOpened(object sender, RoutedEventArgs e)
    {
        _activePane = GameView;
        var selection = GameView.GetSelectedEntries();
        var single = selection.Count == 1 ? selection[0] : null;
        GameUnzipItem.Visibility = single is { IsFolder: false } &&
                                   single.RelativePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelectionBar();
    }

    private void OnSharedMenuOpened(object sender, RoutedEventArgs e)
    {
        _activePane = SharedView;
        UpdateSelectionBar();
    }

    // ── opening things ───────────────────────────────────────────────────────

    private void OnEditSelectedGameFile(object sender, RoutedEventArgs e) => EditFirstSelected(GameView);
    private void OnEditSelectedSharedFile(object sender, RoutedEventArgs e) => EditFirstSelected(SharedView);

    private void EditFirstSelected(FolderView view)
    {
        var selected = view.GetSelectedFiles().FirstOrDefault();
        if (selected is null) { Status("Select a file to edit."); return; }
        OpenInEditor(selected, view);
    }

    private void OpenInEditor(string relativePath) => OpenInEditor(relativePath, _activePane);

    private void OpenInEditor(string relativePath, FolderView view)
    {
        try
        {
            var full = AbsolutePathFor(view, relativePath);
            if (full is null) { Status("That path is not inside this instance."); return; }
            if (!File.Exists(full)) { Status("That file is no longer there."); return; }
            if (TextFileService.IsKnownBinary(full))
            {
                // Not editable here: images open in their viewer, anything runnable is shown in Explorer.
                if (!SafeLaunch.OpenFile(full)) Status("Windows would not open that.");
                return;
            }
            FileEditorWindow.OpenFileFor(Window.GetWindow(this), _packId, _pack.Name, full);
        }
        catch (Exception ex)
        {
            AppLog.LogError("files.open", ex);
            Status("Could not open that file - see the Logs tab.");
        }
    }

    private void OnOpenWithShell(object sender, RoutedEventArgs e)
    {
        var entry = _activePane.SelectedEntry;
        if (entry is null) { Status("Select a file first."); return; }
        // Text and images open in their default app; anything else, jars and scripts included, is
        // shown in Explorer rather than run.
        var full = AbsolutePathFor(_activePane, entry.RelativePath);
        if (full is null || !SafeLaunch.OpenFile(full)) Status("Windows would not open that.");
    }

    private void OnOpenGameFolder(object sender, RoutedEventArgs e)
    {
        // The right pane has no folder of its own, so its button opens the instance folder; the left
        // one opens the folder it is showing.
        var dir = ReferenceEquals(sender, OpenSharedButton)
            ? GameDir()
            : GameView.CurrentAbsoluteDir ?? GameDir();
        OpenInExplorer(dir);
    }

    private void OnRevealGame(object sender, RoutedEventArgs e) => RevealSelected(GameView);
    private void OnRevealShared(object sender, RoutedEventArgs e) => RevealSelected(SharedView);

    /// <summary>Opens Explorer with the file itself selected, not just its folder.</summary>
    private void RevealSelected(FolderView view)
    {
        var entry = view.SelectedEntry;
        if (entry is null) { OpenInExplorer(view.CurrentAbsoluteDir ?? GameDir()); return; }
        var full = AbsolutePathFor(view, entry.RelativePath);
        if (full is null || !SafeLaunch.RevealFile(full)) Status("Could not open Explorer there.");
    }

    private void OpenInExplorer(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            if (!SafeLaunch.OpenFolder(path)) Status("Could not open that folder.");
        }
        catch (Exception ex)
        {
            AppLog.LogError("files.explorer", ex);
            Status("Could not open that folder.");
        }
    }

    private void OnGameCopyPath(object sender, RoutedEventArgs e) => CopySelectedPath(GameView);
    private void OnSharedCopyPath(object sender, RoutedEventArgs e) => CopySelectedPath(SharedView);

    private void CopySelectedPath(FolderView view)
    {
        var entry = view.SelectedEntry;
        if (entry is null) { Status("Select a file first."); return; }
        var full = AbsolutePathFor(view, entry.RelativePath);
        if (full is null) { Status("That path is not inside this instance."); return; }
        Status(ClipboardHelper.TrySetText(full) ? "Path copied." : "Could not reach the clipboard.");
    }

    // ── rules ────────────────────────────────────────────────────────────────

    private void OnEditRules(object sender, RoutedEventArgs e)
    {
        var dlg = new PackRulesDialog(PackRoot(), _pack.Name,
            packId: _pack.IsShared ? _packId : null, isOwner: _isOwner)
        { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
        // The dialog may have rewritten the rules; the files did not change.
        _ = ReclassifyAsync(App.State.Rules.Load(PackRoot()));
    }

    private async void OnApplyRules(object sender, RoutedEventArgs e)
    {
        var rules = App.State.Rules.Load(PackRoot());
        SetBusy(true, "Checking every file against the rules...");
        try
        {
            await ReclassifyAsync(rules);
            var matched = _gameIndex?.SharedFileCount ?? 0;
            Status(matched > 0
                ? $"{matched} file(s) match a shared rule and go up with the next upload."
                : "No file matches a shared rule, so an upload would send nothing.");
        }
        catch (Exception ex)
        {
            AppLog.LogError("files.apply-rules", ex);
            Status("Could not read the instance - see the Logs tab.");
        }
        finally { SetBusy(false); }
    }

    private void OnQuickRuleShared(object sender, RoutedEventArgs e) => AddQuickRule(RuleAction.Shared, folderMode: false);
    private void OnQuickRuleIgnored(object sender, RoutedEventArgs e) => AddQuickRule(RuleAction.Ignored, folderMode: false);
    private void OnQuickRuleFolder(object sender, RoutedEventArgs e) => AddQuickRule(RuleAction.Shared, folderMode: true);
    private void OnMoveGameToShared(object sender, RoutedEventArgs e) => AddQuickRule(RuleAction.Shared, folderMode: false);

    private void OnMoveSharedToGame(object sender, RoutedEventArgs e)
    {
        var paths = SharedView.GetSelectedEntries().Select(en => en.RelativePath).ToList();
        if (paths.Count == 0) { Status("Select a file first."); return; }
        WriteRules(paths, RuleAction.Local, $"Removed {paths.Count} item(s) from sync.");
    }

    private void AddQuickRule(RuleAction action, bool folderMode)
    {
        if (_serverMode) { Status("The server-override folder is outside the sync tree - rules do not apply to it."); return; }

        var entries = GameView.GetSelectedEntries();
        if (entries.Count == 0) { Status("Select a file first."); return; }

        var patterns = new List<string>();
        foreach (var entry in entries)
        {
            var path = entry.RelativePath;
            if (folderMode)
            {
                var parts = path.Split('/');
                patterns.Add(parts.Length > 1 ? parts[0] + "/" : path);
            }
            // A folder rule needs a trailing '/' so the matcher expands it to '/**' and covers the folder's
            // contents; without it only the folder name itself matches.
            else patterns.Add(entry.IsFolder ? path.TrimEnd('/') + "/" : path);
        }

        var what = action switch
        {
            RuleAction.Shared => $"Marked {entries.Count} item(s) as shared.",
            RuleAction.Ignored => $"Marked {entries.Count} item(s) as ignored.",
            _ => $"Added a rule for {entries.Count} item(s)."
        };
        WriteRules(patterns, action, what);
    }

    /// <summary>Writes one rule per path, replacing any rule already covering it.</summary>
    /// <remarks>Replacing avoids leaving a folder pattern and its slash-less twin side by side in the
    /// rules editor (the matcher picks the most specific rule; front insertion only breaks ties).
    /// Nothing on disk changed, so the panes just re-classify.</remarks>
    private void WriteRules(IReadOnlyList<string> paths, RuleAction action, string message)
    {
        try
        {
            var packRoot = PackRoot();
            var gameDir = GameDir();
            var rules = App.State.Rules.Load(packRoot);
            foreach (var rel in paths)
            {
                // Rules travel with a shared instance, so only paths inside it become rules.
                if (PathSafety.ResolveInside(gameDir, rel.TrimEnd('/')) is not { } full)
                {
                    AppLog.Log("files", $"Skipped a rule for '{rel}': it is not a path inside the instance.");
                    continue;
                }
                var isDir = Directory.Exists(full);
                var pattern = isDir ? rel.TrimEnd('/') + "/" : rel;
                rules.RemoveAll(r => string.Equals(r.Pattern.TrimEnd('/'), pattern.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
                rules.Insert(0, new PackRule { Pattern = pattern, Action = action });
            }
            App.State.Rules.Save(packRoot, rules);
            Status(message);
            _ = ReclassifyAsync(rules);
        }
        catch (Exception ex)
        {
            AppLog.LogError("files.rules", ex);
            Status("Could not save the rules - see the Logs tab.");
        }
    }

    // ── create ───────────────────────────────────────────────────────────────

    private void OnNewMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    /// <summary>
    /// Creates a file in the open folder and opens it in the editor.
    /// </summary>
    /// <remarks>Accepts a relative path as well as a name (<c>jei/jei-client.ini</c>). Paths that would
    /// leave the instance, such as <c>..</c>, are refused.</remarks>
    private async void OnNewFile(object sender, RoutedEventArgs e)
    {
        if (!RequireWrite()) return;
        var pane = _activePane;
        var root = pane.Root ?? GameDir();
        var parentRel = pane.CurrentRelativeDir;

        var name = await _shell.PromptAsync("New file",
            parentRel.Length == 0 ? "File name" : $"File name, inside {parentRel}/", "");
        if (string.IsNullOrWhiteSpace(name)) return;

        name = name.Trim().Replace('\\', '/');
        if (name.Split('/').Any(part => part == ".."))
        {
            Status("A path with '..' in it would land outside this instance.");
            return;
        }

        var rel = parentRel.Length == 0 ? name : $"{parentRel}/{name}";
        // Also refuses a drive or a rooted path, and names Windows reserves such as CON or "name.".
        if (PathSafety.ResolveInside(root, rel.TrimEnd('/')) is not { } full)
        {
            Status("That is not a file name or path Windows allows inside this instance.");
            return;
        }

        try
        {
            if (name.EndsWith('/'))
            {
                Directory.CreateDirectory(full);
                Status($"Created {rel}");
            }
            else
            {
                if (File.Exists(full)) { Status($"'{name}' already exists."); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, "");
                Status($"Created {rel}");
                FileEditorWindow.OpenFileFor(Window.GetWindow(this), _packId, _pack.Name, full);
            }
            await RescanAsync(pane, [parentRel]);
        }
        catch (Exception ex)
        {
            AppLog.LogError("files.new", ex);
            Status("Could not create that file - see the Logs tab.");
        }
    }

    private async void OnNewFolder(object sender, RoutedEventArgs e)
    {
        if (!RequireWrite()) return;
        var pane = _activePane;
        var root = pane.Root ?? GameDir();
        var parentRel = pane.CurrentRelativeDir;

        var name = await _shell.PromptAsync("New folder",
            parentRel.Length == 0 ? "Folder name" : $"Folder name, inside {parentRel}/", "");
        if (string.IsNullOrWhiteSpace(name)) return;

        name = name.Trim();
        if (name.Any(c => Path.GetInvalidFileNameChars().Contains(c)))
        {
            Status("That name has characters Windows will not allow in a folder name.");
            return;
        }
        // "." and "..", names ending in a dot or a space, and reserved names such as CON.
        if (!PathSafety.IsSafeFileName(name))
        {
            Status("Windows will not allow that as a folder name.");
            return;
        }

        try
        {
            var target = FolderInside(root, parentRel) is { } parent ? PathSafety.ResolveFileName(parent, name) : null;
            if (target is null) { Status("That folder would not be inside this instance."); return; }
            if (Directory.Exists(target)) { Status("That folder already exists."); return; }
            Directory.CreateDirectory(target);
            Status($"Created {name}/");
            await RescanAsync(pane, [parentRel]);
        }
        catch (Exception ex)
        {
            AppLog.LogError("files.new-folder", ex);
            Status("Could not create that folder - see the Logs tab.");
        }
    }

    // ── rename ───────────────────────────────────────────────────────────────

    private void OnGameRename(object sender, RoutedEventArgs e) => _ = RenameSelectedAsync(GameView);
    private void OnSharedRename(object sender, RoutedEventArgs e) => _ = RenameSelectedAsync(SharedView);

    /// <summary>Renames one file or folder in place. Single selection only; bulk rename is a different
    /// feature.</summary>
    private async Task RenameSelectedAsync(FolderView view)
    {
        if (!RequireWrite()) return;
        try
        {
            var entries = view.GetSelectedEntries();
            if (entries.Count == 0) { Status("Select a file or folder to rename."); return; }
            if (entries.Count > 1) { Status("Rename works on one item at a time."); return; }

            var entry = entries[0];
            var source = AbsolutePathFor(view, entry.RelativePath);
            if (source is null) { Status("That path is not inside this instance."); return; }
            var currentName = Path.GetFileName(source);

            var name = await _shell.PromptAsync("Rename", entry.IsFolder ? "New folder name" : "New file name", currentName);
            if (name is null) return;
            name = name.Trim();
            if (name.Length == 0 || string.Equals(name, currentName, StringComparison.Ordinal)) return;
            if (name.Any(c => Path.GetInvalidFileNameChars().Contains(c)))
            {
                Status("That name has characters Windows will not allow in a file name.");
                return;
            }
            // "." and "..", names ending in a dot or a space, and reserved names such as CON.
            if (PathSafety.ResolveFileName(Path.GetDirectoryName(source)!, name) is not { } target)
            {
                Status(entry.IsFolder
                    ? "Windows will not allow that as a folder name."
                    : "Windows will not allow that as a file name.");
                return;
            }

            if (File.Exists(target) || Directory.Exists(target))
            {
                Status($"'{name}' already exists in that folder.");
                return;
            }

            // Rules match on the path, so renaming a shared file unshares it. Tell the user before the
            // next upload drops it from the server.
            var wasShared = entry.IsAutoShared;

            if (entry.IsFolder) Directory.Move(source, target);
            else File.Move(source, target);

            Status(wasShared
                ? $"Renamed to {name}. It no longer matches its shared rule - re-mark it if it should still sync."
                : $"Renamed to {name}.");
            await RescanAsync(view, [InstanceFileIndex.ParentOf(entry.RelativePath)]);
        }
        catch (Exception ex)
        {
            AppLog.LogError("files.rename", ex);
            Status("Could not rename that - see the Logs tab.");
        }
    }

    // ── delete (to .trash) ───────────────────────────────────────────────────

    private async void OnBulkDelete(object sender, RoutedEventArgs e)
    {
        if (!RequireWrite() || !RequireIdle()) return;

        var view = _activePane;
        var entries = view.GetSelectedEntries();
        if (entries.Count == 0) { Status("Select something to delete."); return; }

        var sharedCount = entries.Count(en => en.IsAutoShared);
        var what = entries.Count == 1
            ? $"'{entries[0].DisplayName.TrimEnd('/')}'"
            : $"{entries.Count} items";

        var message = $"Move {what} out of this instance?";
        if (entries.Any(en => en.IsFolder))
            message += "\n\nFolders go with everything inside them.";
        if (sharedCount > 0)
            message += $"\n\n{(sharedCount == entries.Count ? "They are" : $"{sharedCount} of them are")} marked shared - the next upload removes them from the server for everyone.";
        message += "\n\nThey go to this instance's .trash folder, so 'Recently deleted' can put them back until you empty it.";

        if (!await AppDialog.ConfirmAsync(Window.GetWindow(this), "Delete from instance", message, "Delete", "Cancel", danger: true))
            return;

        var root = view.Root ?? GameDir();
        var paths = entries.Select(en => en.RelativePath).ToList();
        // Invariant: ReadTrash parses this folder name with InvariantCulture to date the batch; a
        // non-Gregorian current culture would produce names it can't read.
        var batch = Path.Combine(TrashDir(), TimeFormat.StampNow());

        SetBusy(true, "Deleting...");
        try
        {
            var (moved, error) = await Task.Run(() => MoveToTrash(root, paths, batch));
            Status(error is not null
                ? $"Moved {moved} item(s) to .trash, then failed - see the Logs tab."
                : $"Moved {moved} item(s) to .trash. Use 'Recently deleted' to put them back.");
            if (error is not null) AppLog.Log("files", "Delete failed: " + error);
            await RescanAsync(view, paths.Select(InstanceFileIndex.ParentOf));
        }
        catch (Exception ex)
        {
            AppLog.LogError("files.delete", ex);
            Status("Delete failed - see the Logs tab.");
        }
        finally { SetBusy(false); }
    }

    /// <summary>
    /// Moves entries into a timestamped trash batch, keeping their path under the root so a restore
    /// knows where each one came from.
    /// </summary>
    /// <remarks>A move on the same volume is a rename, so trashing a large <c>saves/</c> folder is
    /// instant and can't half-finish.</remarks>
    private static (int moved, string? error) MoveToTrash(string root, IReadOnlyList<string> relativePaths, string batchDir)
    {
        var moved = 0;
        try
        {
            foreach (var rel in relativePaths)
            {
                var source = PathSafety.ResolveInside(root, rel);
                var target = PathSafety.ResolveInside(batchDir, rel);
                if (source is null || target is null)
                {
                    AppLog.Log("files", $"Left '{rel}' alone: it is not a path inside the instance.");
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                if (Directory.Exists(source)) { Directory.Move(source, target); moved++; }
                else if (File.Exists(source)) { File.Move(source, target, overwrite: true); moved++; }
            }
            return (moved, null);
        }
        catch (Exception ex) { return (moved, ex.Message); }
    }

    // ── recently deleted ─────────────────────────────────────────────────────

    private sealed class TrashBatch
    {
        public string Directory { get; init; } = "";
        public DateTime TakenLocal { get; init; }
        public int FileCount { get; init; }
        public long Bytes { get; init; }
        public string Summary { get; init; } = "";
        public string WhenLabel => TakenLocal == default ? "" : TakenLocal.ToString("g");
        public string SizeLabel => ConfigHubService.FormatSize(Bytes);
    }

    private void UpdateTrashButton()
    {
        var count = 0;
        try
        {
            var dir = TrashDir();
            if (Directory.Exists(dir)) count = Directory.EnumerateDirectories(dir).Count();
        }
        catch { /* an unreadable .trash is not worth a message */ }

        // Show a real count or none, never a placeholder zero.
        TrashButtonLabel.Text = count > 0 ? $"Recently deleted ({count})" : "Recently deleted";
    }

    private void OnToggleTrash(object sender, RoutedEventArgs e)
    {
        if (TrashPanel.Visibility == Visibility.Visible) { ShowPanes(); return; }
        _ = ShowTrashAsync();
    }

    private async Task ShowTrashAsync()
    {
        TrashList.ItemsSource = null;
        TrashSub.Text = "Reading...";
        TrashEmpty.Visibility = Visibility.Collapsed;
        PaneGrid.Visibility = Visibility.Collapsed;
        SearchResultsPanel.Visibility = Visibility.Collapsed;
        TrashPanel.Visibility = Visibility.Visible;

        var dir = TrashDir();
        var batches = await Task.Run(() => ReadTrash(dir));

        TrashList.ItemsSource = batches;
        TrashSub.Text = batches.Count == 0
            ? "Deleting from this page puts files here first."
            : $"{batches.Count} batch(es) · {ConfigHubService.FormatSize(batches.Sum(b => b.Bytes))} still on disk";
        TrashEmpty.Visibility = batches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TrashEmpty.Text = batches.Count == 0
            ? "Nothing has been deleted from this instance yet. Anything you delete here lands in .trash and can be put back until you empty it."
            : "";
        UpdateTrashSelectionState();
    }

    /// <summary>
    /// Reads the trash batches, newest first.
    /// </summary>
    /// <remarks>The time comes from the folder name: moved files keep their own last-write times, so
    /// timestamps say nothing about when they were deleted. Same for <c>.bak-</c> backups.</remarks>
    private static List<TrashBatch> ReadTrash(string trashDir)
    {
        var batches = new List<TrashBatch>();
        if (!Directory.Exists(trashDir)) return batches;

        foreach (var dir in Directory.EnumerateDirectories(trashDir))
        {
            var name = Path.GetFileName(dir);
            DateTime taken = default;
            DateTime.TryParseExact(name, "yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out taken);

            var files = new List<string>();
            long bytes = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    files.Add(Path.GetRelativePath(dir, file).Replace('\\', '/'));
                    try { bytes += new FileInfo(file).Length; } catch { /* vanished */ }
                }
            }
            catch { continue; }

            var summary = files.Count switch
            {
                0 => "(empty)",
                1 => files[0],
                _ => $"{files[0]} and {files.Count - 1} more"
            };

            batches.Add(new TrashBatch
            {
                Directory = dir, TakenLocal = taken, FileCount = files.Count, Bytes = bytes, Summary = summary
            });
        }

        return batches.OrderByDescending(b => b.TakenLocal).ThenByDescending(b => b.Directory, StringComparer.Ordinal).ToList();
    }

    private void OnTrashSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateTrashSelectionState();

    private void UpdateTrashSelectionState()
    {
        var any = TrashList.SelectedItems.Count > 0;
        if (!_canWrite) return;   // already disabled with a reason
        TrashRestoreButton.IsEnabled = any;
        TrashPurgeButton.IsEnabled = any;
        ToolTipService.SetShowOnDisabled(TrashRestoreButton, true);
        ToolTipService.SetShowOnDisabled(TrashPurgeButton, true);
        if (!any)
        {
            TrashRestoreButton.ToolTip = "Pick a batch to put back.";
            TrashPurgeButton.ToolTip = "Pick a batch to delete for good.";
        }
        else
        {
            TrashRestoreButton.ToolTip = "Move these files back where they came from.";
            TrashPurgeButton.ToolTip = "Delete these files from the disk. There is no undo after this.";
        }
    }

    private async void OnRestoreFromTrash(object sender, RoutedEventArgs e)
    {
        if (!RequireWrite() || !RequireIdle()) return;
        var batches = TrashList.SelectedItems.Cast<TrashBatch>().ToList();
        if (batches.Count == 0) { Status("Pick a batch to put back."); return; }

        var gameDir = GameDir();
        SetBusy(true, "Putting files back...");
        try
        {
            var (restored, skipped) = await Task.Run(() =>
            {
                var back = 0;
                var kept = 0;
                foreach (var batch in batches)
                {
                    foreach (var file in Directory.EnumerateFiles(batch.Directory, "*", SearchOption.AllDirectories))
                    {
                        var rel = Path.GetRelativePath(batch.Directory, file);
                        if (PathSafety.ResolveInside(gameDir, rel) is not { } target) { kept++; continue; }
                        // Don't restore over a file that now exists; that is the copy the user has been using.
                        if (File.Exists(target)) { kept++; continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        try { File.Move(file, target); back++; } catch { kept++; }
                    }
                    try { if (!Directory.EnumerateFileSystemEntries(batch.Directory).Any()) Directory.Delete(batch.Directory, true); }
                    catch { /* leave the husk; the purge sweeps it */ }
                }
                return (back, kept);
            });

            Status(skipped > 0
                ? $"Put {restored} file(s) back · {skipped} left in .trash because a file of that name is there now."
                : $"Put {restored} file(s) back.");
            AppLog.Log("files", $"Restored {restored} file(s) from .trash in {_pack.Name}.");
            await RefreshAsync();
            await ShowTrashAsync();
        }
        catch (Exception ex)
        {
            AppLog.LogError("files.restore", ex);
            Status("Could not put those back - see the Logs tab.");
        }
        finally { SetBusy(false); }
    }

    private async void OnPurgeTrash(object sender, RoutedEventArgs e)
    {
        if (!RequireWrite() || !RequireIdle()) return;
        var batches = TrashList.SelectedItems.Cast<TrashBatch>().ToList();
        if (batches.Count == 0) { Status("Pick a batch to delete for good."); return; }

        var files = batches.Sum(b => b.FileCount);
        var bytes = batches.Sum(b => b.Bytes);
        if (!await AppDialog.ConfirmAsync(Window.GetWindow(this), "Delete for good",
                $"Permanently delete {files} file(s) ({ConfigHubService.FormatSize(bytes)}) from this instance's .trash?\n\n" +
                "This is the last copy. Nothing puts them back afterwards.",
                "Delete for good", "Cancel", danger: true))
            return;

        SetBusy(true, "Emptying...");
        try
        {
            await Task.Run(() =>
            {
                foreach (var batch in batches)
                {
                    try { Directory.Delete(batch.Directory, recursive: true); }
                    catch (Exception ex) { AppLog.Log("files", $"Could not empty {batch.Directory}: {ex.Message}"); }
                }
            });
            Status($"Deleted {files} file(s) for good.");
            UpdateTrashButton();
            await ShowTrashAsync();
        }
        catch (Exception ex)
        {
            AppLog.LogError("files.purge", ex);
            Status("Could not empty that - see the Logs tab.");
        }
        finally { SetBusy(false); }
    }

    // ── import, drag-drop ────────────────────────────────────────────────────

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        if (!RequireWrite() || !RequireIdle()) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Copy files into this instance",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;

        var pane = _activePane;
        var root = pane.Root ?? GameDir();
        var destDir = FolderInside(root, pane.CurrentRelativeDir);
        if (destDir is null) { Status("That folder is not inside this instance."); return; }

        await CopyInAsync(pane, root, destDir, pane.CurrentRelativeDir, dialog.FileNames);
    }

    private async void OnExternalFilesDropped(FolderView view, string destRoot, string destRelativeDir, IReadOnlyList<string> sources)
    {
        if (!RequireWrite() || !RequireIdle()) return;
        var destDir = FolderInside(destRoot, destRelativeDir);
        if (destDir is null) { Status("That folder is not inside this instance."); return; }
        await CopyInAsync(view, destRoot, destDir, destRelativeDir, sources);
    }

    private async Task CopyInAsync(FolderView view, string destRoot, string destDir, string destRelativeDir, IReadOnlyList<string> sources)
    {
        SetBusy(true, $"Copying {sources.Count} item(s) in...");
        try
        {
            var paths = sources.ToList();
            var (copied, skipped, relatives, error) = await Task.Run(() => CopyExternalEntries(destRoot, destDir, paths));

            Status(error is not null
                ? $"Copied {copied} item(s), then failed - see the Logs tab."
                : $"Copied {copied} item(s) into {(destRelativeDir.Length == 0 ? "the instance root" : destRelativeDir + "/")}."
                  + (skipped > 0 ? $" {skipped} left out: Windows will not allow their names here. See the Logs tab." : ""));
            if (error is not null) AppLog.Log("files", "Import failed: " + error);

            // The folder they landed in is re-read first, so the rule below classifies files the
            // index already knows about.
            await RescanAsync(view, [destRelativeDir]);

            // Only the sync-preview pane means "share this"; a drop on the left is just a copy.
            if (view.ShowOnlyShared && relatives.Count > 0)
                WriteRules(relatives, RuleAction.Shared, StatusLabel.Text + " Marked shared.");
        }
        catch (Exception ex)
        {
            AppLog.LogError("files.import", ex);
            Status("Could not copy those files in - see the Logs tab.");
        }
        finally { SetBusy(false); }
    }

    /// <summary>Copies dropped files and folders into <paramref name="destDir"/>, returning their
    /// paths relative to <paramref name="destRoot"/> so the caller can write rules for them.</summary>
    /// <remarks>Every name is checked, since a drop can come from anywhere and nothing may land outside
    /// <paramref name="destDir"/>. What can't be placed is counted, logged and skipped.</remarks>
    private static (int copied, int skipped, List<string> relatives, string? error) CopyExternalEntries(
        string destRoot, string destDir, IReadOnlyList<string> sources)
    {
        var copied = 0;
        var skipped = 0;
        var relatives = new List<string>();
        try
        {
            Directory.CreateDirectory(destDir);
            foreach (var source in sources)
            {
                if (Directory.Exists(source))
                {
                    var folderName = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (PathSafety.ResolveFileName(destDir, folderName) is not { } targetFolder)
                    {
                        AppLog.Log("files", $"Left {source} out: its name cannot be used inside the instance.");
                        skipped++;
                        continue;
                    }
                    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                    {
                        if (PathSafety.ResolveInside(targetFolder, Path.GetRelativePath(source, file)) is not { } target)
                        {
                            AppLog.Log("files", $"Left {file} out: its name cannot be used inside the instance.");
                            skipped++;
                            continue;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(file, target, overwrite: true);
                        relatives.Add(Path.GetRelativePath(destRoot, target).Replace('\\', '/'));
                        copied++;
                    }
                }
                else if (File.Exists(source))
                {
                    if (PathSafety.ResolveFileName(destDir, Path.GetFileName(source)) is not { } target)
                    {
                        AppLog.Log("files", $"Left {source} out: its name cannot be used inside the instance.");
                        skipped++;
                        continue;
                    }
                    File.Copy(source, target, overwrite: true);
                    relatives.Add(Path.GetRelativePath(destRoot, target).Replace('\\', '/'));
                    copied++;
                }
            }
            return (copied, skipped, relatives, null);
        }
        catch (Exception ex) { return (copied, skipped, relatives, ex.Message); }
    }

    /// <summary>
    /// A drop from one pane onto the other. Both panes are views of <c>game/</c>, so nothing moves:
    /// dropping onto the Shared pane means "mark this shared", which is a rule.
    /// </summary>
    private void OnFilesDropped(string sourceRoot, string destRoot, IReadOnlyList<string> relativePaths)
    {
        if (!RequireWrite()) return;
        if (!string.Equals(sourceRoot, destRoot, StringComparison.OrdinalIgnoreCase))
        {
            // Dragging between game/ and the server-override folder isn't supported.
            Status("Use 'Move to folder...' to move files between the instance and its server folder.");
            return;
        }
        WriteRules(relativePaths, RuleAction.Shared, $"Marked {relativePaths.Count} file(s) as shared.");
    }

    /// <summary>
    /// A drop from one pane onto the other. Both panes are views of <c>game/</c>, so nothing moves:
    /// onto Shared is "share these", onto Game is "stop sharing these", the same rules the Share
    /// button and the context menu write.
    /// </summary>
    private void OnPaneDropped(FolderView source, FolderView target, IReadOnlyList<string> relativePaths)
    {
        if (!RequireWrite()) return;
        if (relativePaths.Count == 0) return;
        if (ReferenceEquals(target, SharedView) && !ReferenceEquals(source, SharedView))
            WriteRules(relativePaths, RuleAction.Shared,
                $"Marked {relativePaths.Count} item(s) as shared. Nothing moved - they upload from where they are.");
        else if (ReferenceEquals(target, GameView) && ReferenceEquals(source, SharedView))
            WriteRules(relativePaths, RuleAction.Local,
                $"Removed {relativePaths.Count} item(s) from sync. They stay on this PC.");
    }

    // ── move ─────────────────────────────────────────────────────────────────

    private async void OnBulkMove(object sender, RoutedEventArgs e)
    {
        if (!RequireWrite() || !RequireIdle()) return;

        var view = _activePane;
        var entries = view.GetSelectedEntries();
        if (entries.Count == 0) { Status("Select something to move."); return; }

        var target = await _shell.PromptAsync("Move to folder",
            "Folder, relative to this instance (e.g. mods/disabled)", view.CurrentRelativeDir);
        if (string.IsNullOrWhiteSpace(target)) return;

        var rel = target.Trim().Replace('\\', '/').Trim('/');
        if (rel.Split('/').Any(p => p == ".."))
        {
            Status("A path with '..' in it would land outside this instance.");
            return;
        }

        await MoveEntriesAsync(view, entries.Select(en => en.RelativePath).ToList(), rel);
    }

    /// <summary>Moves paths into a folder under the same root. The Move... prompt and a drop onto a
    /// folder row both end here; afterwards only the folders they left and the one they entered
    /// are re-read.</summary>
    private async Task MoveEntriesAsync(FolderView view, IReadOnlyList<string> paths, string destRel)
    {
        if (!RequireWrite() || !RequireIdle()) return;
        if (paths.Count == 0) return;

        var root = view.Root ?? GameDir();
        var rel = destRel.Trim().Replace('\\', '/').Trim('/');
        // Typed or dropped: never move into a drive, a rooted path or a reserved name.
        var destDir = FolderInside(root, rel);
        if (destDir is null) { Status("That folder is not inside this instance."); return; }
        SetBusy(true, "Moving...");
        try
        {
            var (moved, skipped, error) = await Task.Run(() =>
            {
                var done = 0;
                var kept = 0;
                try
                {
                    Directory.CreateDirectory(destDir);
                    foreach (var path in paths)
                    {
                        var source = PathSafety.ResolveInside(root, path);
                        var dest = source is null ? null : PathSafety.ResolveFileName(destDir, Path.GetFileName(source));
                        if (source is null || dest is null)
                        {
                            AppLog.Log("files", $"Left '{path}' where it is: it is not a path inside the instance.");
                            kept++;
                            continue;
                        }
                        if (string.Equals(source, dest, StringComparison.OrdinalIgnoreCase)) { kept++; continue; }
                        if (File.Exists(dest) || Directory.Exists(dest)) { kept++; continue; }
                        if (Directory.Exists(source)) Directory.Move(source, dest);
                        else if (File.Exists(source)) File.Move(source, dest);
                        done++;
                    }
                    return (done, kept, (string?)null);
                }
                catch (Exception ex) { return (done, kept, ex.Message); }
            });

            if (error is not null) AppLog.Log("files", "Move failed: " + error);
            Status(error is not null
                ? $"Moved {moved} item(s), then failed - see the Logs tab."
                : skipped > 0
                    ? $"Moved {moved} item(s) · {skipped} skipped, something of that name is already there."
                    : $"Moved {moved} item(s) into {(rel.Length == 0 ? "the instance root" : rel + "/")}.");
            await RescanAsync(view, paths.Select(InstanceFileIndex.ParentOf).Append(rel));
        }
        catch (Exception ex)
        {
            AppLog.LogError("files.move", ex);
            Status("Could not move those - see the Logs tab.");
        }
        finally { SetBusy(false); }
    }

    // ── zip / unzip ──────────────────────────────────────────────────────────

    private async void OnBulkZip(object sender, RoutedEventArgs e)
    {
        if (!RequireIdle()) return;

        var view = _activePane;
        var entries = view.GetSelectedEntries();
        if (entries.Count == 0) { Status("Select what should go in the zip."); return; }

        var root = view.Root ?? GameDir();
        var suggested = entries.Count == 1
            ? Path.GetFileNameWithoutExtension(entries[0].DisplayName.TrimEnd('/')) + ".zip"
            : $"{Slug(_pack.Name)}-files.zip";

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save the selection as a zip",
            FileName = suggested,
            DefaultExt = ".zip",
            Filter = "Zip archive (*.zip)|*.zip",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };
        if (dialog.ShowDialog() != true) return;

        var destination = dialog.FileName;
        var paths = entries.Select(en => en.RelativePath).ToList();

        await RunLongAsync($"Zipping {paths.Count} item(s)", async (job, report) =>
        {
            var written = await Task.Run(() => WriteZip(root, paths, destination, report, job.Token), job.Token);
            Status($"Wrote {written} file(s) to {Path.GetFileName(destination)}.");
            AppLog.Log("files", $"Zipped {written} file(s) from {_pack.Name} to {destination}.");
        });
    }

    private static int WriteZip(string root, IReadOnlyList<string> relativePaths, string destination,
        IProgress<string> report, CancellationToken ct)
    {
        // Every file the selection expands to, with the path it should carry inside the archive.
        var items = new List<(string Full, string Entry)>();
        foreach (var rel in relativePaths)
        {
            if (PathSafety.ResolveInside(root, rel) is not { } abs)
            {
                report.Report($"skipped {rel}: it is not a path inside the instance");
                continue;
            }
            if (Directory.Exists(abs))
            {
                foreach (var file in Directory.EnumerateFiles(abs, "*", SearchOption.AllDirectories))
                    items.Add((file, Path.GetRelativePath(root, file).Replace('\\', '/')));
            }
            else if (File.Exists(abs))
            {
                items.Add((abs, rel));
            }
        }

        using var stream = new FileStream(destination, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        var done = 0;
        foreach (var (full, entryName) in items)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                archive.CreateEntryFromFile(full, entryName, CompressionLevel.Optimal);
                done++;
            }
            catch (Exception ex) { report.Report($"skipped {entryName}: {ex.Message}"); continue; }
            if (done % 25 == 0) report.Report($"{done} of {items.Count} file(s)");
        }
        return done;
    }

    private async void OnExtractHere(object sender, RoutedEventArgs e)
    {
        if (!RequireWrite() || !RequireIdle()) return;

        var view = _activePane;
        var entry = view.GetSelectedEntries().FirstOrDefault(en => !en.IsFolder &&
            en.RelativePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        if (entry is null) { Status("Select a .zip to extract."); return; }

        var root = view.Root ?? GameDir();
        var zipPath = AbsolutePathFor(view, entry.RelativePath);
        // The folder is named after the zip, so a zip called "...zip" would otherwise name the
        // folder above it.
        var targetDir = zipPath is null
            ? null
            : PathSafety.ResolveFileName(Path.GetDirectoryName(zipPath)!, Path.GetFileNameWithoutExtension(zipPath));
        if (zipPath is null || targetDir is null)
        {
            Status("That zip's name does not make a folder name Windows allows inside this instance.");
            return;
        }
        var targetRel = Path.GetRelativePath(root, targetDir).Replace('\\', '/');

        if (!await AppDialog.ConfirmAsync(Window.GetWindow(this), "Extract here",
                $"Extract {Path.GetFileName(zipPath)} into {targetRel}/ inside this instance?\n\n" +
                "Files already there with the same names are overwritten.",
                "Extract", "Cancel"))
            return;

        await RunLongAsync($"Extracting {Path.GetFileName(zipPath)}", async (job, report) =>
        {
            var (count, skipped) = await Task.Run(() =>
            {
                using var archive = ZipFile.OpenRead(zipPath);
                // Refused before anything is written: more entries, or more unpacked bytes, than a
                // real archive holds.
                if (SafeZip.CheckLimits(archive.Entries) is { } tooBig)
                    throw new InvalidDataException($"Refused to unpack {Path.GetFileName(zipPath)}: {tooBig}.");

                Directory.CreateDirectory(targetDir);
                var done = 0;
                var left = new List<string>();
                foreach (var archiveEntry in archive.Entries)
                {
                    job.Token.ThrowIfCancellationRequested();
                    var rel = archiveEntry.FullName.Replace('\\', '/');
                    // Some zip tools write a leading "./", which names the same place.
                    while (rel.StartsWith("./", StringComparison.Ordinal)) rel = rel[2..];
                    if (rel.Length == 0 || rel.EndsWith('/')) continue;

                    // Entry names can contain "../", a drive or a reserved name (zip slip). Skip anything that
                    // leaves the target or claims an impossible size.
                    var target = PathSafety.ResolveInside(targetDir, rel);
                    var problem = target is null ? "it points outside the folder" : SafeZip.CheckEntry(archiveEntry);
                    if (target is not null && problem is null)
                    {
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                            SafeZip.ExtractToFile(archiveEntry, target, overwrite: true);
                            done++;
                            if (done % 25 == 0) report.Report($"{done} file(s)");
                            continue;
                        }
                        catch (InvalidDataException ex) { problem = ex.Message; }
                    }
                    report.Report($"refused {archiveEntry.FullName}: {problem}");
                    left.Add($"{archiveEntry.FullName} ({problem})");
                }
                return (done, left);
            }, job.Token);

            Status($"Extracted {count} file(s) into {targetRel}/."
                   + (skipped.Count > 0 ? $" {skipped.Count} left out, see the Logs tab." : ""));
            AppLog.Log("files", $"Extracted {zipPath} into {targetDir} ({count} file(s)).");
            if (skipped.Count > 0)
                AppLog.Log("files", $"Left {skipped.Count} entr(ies) of {zipPath} out: "
                                    + string.Join(", ", skipped.Take(10)) + (skipped.Count > 10 ? ", ..." : ""));
            await RescanAsync(view, [InstanceFileIndex.ParentOf(entry.RelativePath)]);
        });
    }

    // ── copy to other instances ──────────────────────────────────────────────

    private async void OnBulkCopyToInstances(object sender, RoutedEventArgs e)
    {
        if (!RequireIdle()) return;
        if (_serverMode) { Status("Files in the server-override folder have no matching place in another instance."); return; }

        var view = _activePane;
        var entries = view.GetSelectedEntries();
        if (entries.Count == 0) { Status("Select what to copy."); return; }

        var root = view.Root ?? GameDir();
        var paths = new List<string>();
        foreach (var entry in entries)
        {
            var abs = AbsolutePathFor(view, entry.RelativePath);
            if (abs is null) continue;
            if (entry.IsFolder)
            {
                if (!Directory.Exists(abs)) continue;
                foreach (var file in Directory.EnumerateFiles(abs, "*", SearchOption.AllDirectories))
                    paths.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
            }
            else paths.Add(entry.RelativePath);
        }
        paths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Count == 0) { Status("Nothing in that selection is a file."); return; }

        SetBusy(true, "Reading the other instances...");
        List<PackSummary> targets;
        try
        {
            targets = (await App.State.Api.ListPacksAsync())
                .Where(p => p.Id != _packId && !App.State.Settings.IsPackHidden(p.Id))
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            AppLog.LogError("files.copy-targets", ex);
            Status("Could not read the instance list - see the Logs tab.");
            return;
        }
        finally { SetBusy(false); }

        if (targets.Count == 0)
        {
            await AppDialog.MessageAsync(Window.GetWindow(this), "Nowhere to copy to",
                "There is only one instance, so there is no other instance to copy these files into.");
            return;
        }

        var card = new ConfigCopyCard(_pack.Name, paths, targets, _shell);
        await _shell.ShowCardAsync(card, card.Completion, card.Close);
        if (card.ChosenTargets is not { Count: > 0 } chosen) return;

        var preview = card.Preview;
        var shared = preview?.SharedPacks.Where(name => chosen.Any(t => t.Name == name)).ToList() ?? [];
        var overwriting = preview?.Items.Count(i => i.Exists && chosen.Any(t => t.Id == i.PackId)) ?? 0;

        var message =
            $"Copy {paths.Count} file(s) from {_pack.Name} into {chosen.Count} other instance(s).\n\n" +
            (overwriting > 0
                ? $"{overwriting} existing file(s) will be replaced. Each one is backed up next to itself as .bak-<timestamp> first.\n\n"
                : "Nothing existing will be replaced.\n\n") +
            (shared.Count > 0
                ? $"Careful: in {string.Join(", ", shared)} these paths are marked Shared, so the change will travel to everyone else on those packs the next time they sync.\n\n"
                : "") +
            "Continue?";

        if (!await AppDialog.ConfirmAsync(Window.GetWindow(this), "Copy across instances", message, "Copy", "Cancel",
                danger: shared.Count > 0))
            return;

        var sourceGameDir = GameDir();
        await RunLongAsync($"Copying {paths.Count} file(s) to {chosen.Count} instance(s)", async (job, report) =>
        {
            var result = await Task.Run(() => ConfigHubService.Copy(
                sourceGameDir, paths, chosen, App.State.Packs, report, job.Token), job.Token);

            Status($"Copied {result.Copied} file(s) into {chosen.Count} instance(s)" +
                   (result.BackedUp > 0 ? $" · {result.BackedUp} replaced file(s) backed up" : "") +
                   (result.Failures.Count > 0 ? $" · {result.Failures.Count} failed" : "") + ".");

            if (result.Failures.Count > 0)
                await AppDialog.MessageAsync(Window.GetWindow(this), "Some files did not copy",
                    string.Join("\n", result.Failures.Take(12)) +
                    (result.Failures.Count > 12 ? $"\n... and {result.Failures.Count - 12} more." : ""));
        });
    }

    // ── plumbing ─────────────────────────────────────────────────────────────

    private bool RequireWrite()
    {
        if (_canWrite) return true;
        Status("You have read-only access to this instance.");
        return false;
    }

    private bool RequireIdle()
    {
        if (_working) { Status("Something else is still running here."); return false; }
        if (PackJobs.IsRunning(_packId))
        {
            Status("This instance is busy with a transfer - wait for it to finish.");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Runs one long file operation as a pack job, so it shows up and can be stopped wherever
    /// transfers do, and reports through <see cref="ProgressHub"/>.
    /// </summary>
    /// <remarks>Reuses the closest existing <see cref="PackJobKind"/>, so other views label it as a
    /// transfer; this page's status line has the exact wording.</remarks>
    private async Task RunLongAsync(string label, Func<PackJob, IProgress<string>, Task> work)
    {
        var job = PackJobs.Start(_packId, PackJobKind.Sync, label);
        SetBusy(true, label + "...", cancellable: true);
        ProgressHub.Indeterminate(_packId, label);

        var report = new Progress<string>(line =>
        {
            Status($"{label} · {line}");
            ProgressHub.Indeterminate(_packId, label, line);
        });

        try
        {
            await work(job, report);
        }
        catch (OperationCanceledException)
        {
            Status(label + " - stopped. Anything already written was left in place.");
        }
        catch (Exception ex)
        {
            AppLog.LogError("files.job", ex);
            Status(label + " failed - see the Logs tab.");
        }
        finally
        {
            PackJobs.Finish(job);
            ProgressHub.Clear(_packId);
            SetBusy(false);
        }
    }

    private static string Slug(string name)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray();
        return new string(chars).Trim('-');
    }
}
