using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>The built-in text editor for one instance's own files: mod configs, KubeJS scripts,
/// server lists, anything under the instance folder that is text.</summary>
/// <remarks><para><see cref="FileEditorWindow"/> hosts one of these and the File Management page
/// hosts another in its Editor tab, so both are the same editor.</para>
/// <para>A plain text editor rather than a settings UI, since mod configs have no schema the
/// launcher could know. <see cref="TextFileService"/> preserves encoding, BOM and line endings, and
/// the colouring is a display layer that never touches the bytes.</para>
/// <para>Dialogs go through <see cref="AppDialog"/> with the containing window as owner, so
/// confirmations render in the editor window or in the main window's overlay, whichever hosts the
/// control.</para></remarks>
public partial class FileEditorPanel : UserControl
{
    private Guid _packId;
    private string _packName = "";
    private readonly List<string> _roots = new();
    private readonly ObservableCollection<OpenDoc> _open = new();
    private OpenDoc? _current;
    private bool _suppressEditorEvents;
    private bool _initialized;
    private bool _built;

    private SyntaxLayer? _layer;
    private DispatcherTimer? _diskWatch;
    private CancellationTokenSource? _searchCts;

    /// <summary>How many files are reopened per instance when the editor comes back. Enough for a
    /// few configs being compared, few enough that a stale list doesn't reopen half the pack.</summary>
    private const int MaxRestoredFiles = 12;

    /// <summary>How many entries the recent list keeps in total, across every instance.</summary>
    private const int MaxRecentFiles = 40;

    /// <summary>Colouring walks the visible lines on each render, plus one whole-document pass for
    /// block-comment state. Past a few hundred KB that pass is felt on every keystroke, and files
    /// that big are generated and read rather than edited, so they get the plain foreground.</summary>
    private const int MaxHighlightChars = 400_000;

    /// <summary>Raised after a file is written, with its full path. The Files tab uses it to refresh
    /// a row whose size and date have just changed under it.</summary>
    public event Action<string>? FileSaved;

    public FileEditorPanel()
    {
        InitializeComponent();

        InputBindings.Add(new KeyBinding(new RelayCommand(_ => _ = SaveCurrentAsync()), Key.S, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => _ = SaveAllAsync()), Key.S, ModifierKeys.Control | ModifierKeys.Shift));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => ShowFind()), Key.F, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => ShowGoTo()), Key.G, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => FindStep(forward: true)), Key.F3, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => FindStep(forward: false)), Key.F3, ModifierKeys.Shift));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => _ = CloseCurrentTabAsync()), Key.W, ModifierKeys.Control));

        OpenTabs.ItemsSource = _open;
        WrapBox.IsChecked = false;

        Loaded += OnPanelLoaded;
        Unloaded += OnPanelUnloaded;
    }

    /// <summary>Convenience for a host that knows the instance up front.</summary>
    public FileEditorPanel(Guid packId, string packName) : this() => Initialize(packId, packName);

    /// <summary>
    /// Points the editor at an instance. Safe to call before the control is in the visual tree; the
    /// tree and the restored session are built on first load.
    /// </summary>
    public void Initialize(Guid packId, string packName)
    {
        if (_initialized) return;
        _initialized = true;
        _packId = packId;
        _packName = packName;
        PackLabel.Text = packName;

        // An instance whose folder has not been created yet (never launched, never downloaded) is a
        // normal state, not an error: open with an empty tree and say so.
        try
        {
            var gameDir = App.State.Packs.GameDir(packId);
            var localDir = App.State.Packs.LocalDir(packId);
            if (Directory.Exists(gameDir)) _roots.Add(gameDir);
            if (Directory.Exists(localDir)) _roots.Add(localDir);
        }
        catch (Exception ex) { AppLog.Log("editor", $"No folder for pack {packId}: {ex.Message}"); }
    }

    // ── lifetime ─────────────────────────────────────────────────────────────

    private void OnPanelLoaded(object sender, RoutedEventArgs e)
    {
        // A TabControl unloads and reloads its content every time the user switches tabs, so
        // everything here has to be either idempotent or guarded.
        App.State.Instances.StateChanged -= OnInstanceStateChanged;
        App.State.Instances.StateChanged += OnInstanceStateChanged;
        ThemeService.Changed -= OnThemeChanged;
        ThemeService.Changed += OnThemeChanged;

        _diskWatch ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2.5) };
        _diskWatch.Tick -= OnDiskWatchTick;
        _diskWatch.Tick += OnDiskWatchTick;
        _diskWatch.Start();

        if (_layer is null)
        {
            _layer = new SyntaxLayer { Source = Editor, ClipToBounds = true, IsHitTestVisible = false };
            _layer.Failed += OnHighlightFailed;
            EditorSurface.Children.Insert(0, _layer);
            Editor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnEditorScrolled));
            Editor.SizeChanged += (_, _) => _layer?.Repaint(force: true);

            // Paint on LayoutUpdated rather than on text change: the box lays out text on its own
            // schedule, so painting on text change draws the previous layout. This is only safe
            // because the layer repaints a DrawingVisual instead of calling InvalidateVisual, which
            // would dirty layout again and loop.
            Editor.LayoutUpdated += (_, _) => _layer?.Repaint();
        }

        UpdateRunningBanner();

        if (_built) return;
        _built = true;
        BuildTree();
        RestoreSession();
        RefreshStatus();
    }

    private void OnPanelUnloaded(object sender, RoutedEventArgs e)
    {
        App.State.Instances.StateChanged -= OnInstanceStateChanged;
        ThemeService.Changed -= OnThemeChanged;
        _diskWatch?.Stop();
        _searchCts?.Cancel();
        SyncCurrentFromEditor();
        SaveSession();
    }

    private void OnThemeChanged() => _layer?.ForgetBrushes();

    private void OnInstanceStateChanged(Guid packId)
    {
        if (packId == _packId) UpdateRunningBanner();
    }

    /// <summary>Shows the running-game banner. Editing a config while the game runs is allowed, but
    /// the game won't see the change and anything it writes on exit wins.</summary>
    private void UpdateRunningBanner()
    {
        var running = false;
        try { running = App.State.Instances.GetStatus(_packId) != MinecraftInstanceStatus.Idle; }
        catch (Exception ex) { AppLog.Log("editor", "Could not read the instance state: " + ex.Message); }
        RunningBanner.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── session and recent files ─────────────────────────────────────────────

    /// <summary>The open-tabs record for this instance, kept beside the instance rather than in
    /// settings.</summary>
    /// <remarks>Separate from the recent-files list (<c>AppSettings.RecentEditedFiles</c>): a
    /// session forgets closed files, a recent list remembers them. It lives next to
    /// <c>.rules.json</c> and <c>.sync-manifest.json</c> in the pack root, outside the synced
    /// <c>game/</c> tree, so it never travels to anyone else.</remarks>
    private string? SessionPath()
    {
        try { return Path.Combine(App.State.Packs.PackRoot(_packId), ".editor-session.json"); }
        catch (Exception) { return null; }
    }

    private void RestoreSession()
    {
        if (_roots.Count == 0) return;
        try
        {
            var paths = ReadSessionPaths();
            if (paths.Count == 0)
            {
                // No session file yet (older versions only kept the recent list): fall back to that
                // so the editor doesn't come back empty.
                paths = RecentsForThisInstance().Take(4).Reverse().ToList();
            }

            var opened = 0;
            foreach (var path in paths.Where(File.Exists).Take(MaxRestoredFiles))
            {
                OpenFile(path, remember: false);
                opened++;
            }
            if (opened > 0) EditorStatus.Text = $"Reopened {opened} file(s) from last time.";
        }
        catch (Exception ex) { AppLog.Log("editor", "Could not restore the open files: " + ex.Message); }
    }

    private List<string> ReadSessionPaths()
    {
        try
        {
            var path = SessionPath();
            if (path is null || !File.Exists(path)) return new List<string>();
            var session = System.Text.Json.JsonSerializer.Deserialize<SessionFile>(File.ReadAllText(path));
            return session?.Open ?? new List<string>();
        }
        catch (Exception ex)
        {
            AppLog.Log("editor", "Could not read the editor session: " + ex.Message);
            return new List<string>();
        }
    }

    private void SaveSession()
    {
        try
        {
            var path = SessionPath();
            if (path is null) return;
            var session = new SessionFile
            {
                Open = _open.Select(d => d.Path).Take(MaxRestoredFiles).ToList(),
                Active = _current?.Path
            };
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(session));
        }
        catch (Exception ex) { AppLog.Log("editor", "Could not remember the open files: " + ex.Message); }
    }

    /// <summary>Pushes a path to the front of the shared recent list, which the Recent button
    /// reads.</summary>
    private void RememberRecent(string path)
    {
        try
        {
            var settings = App.State.Settings;
            settings.RecentEditedFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            settings.RecentEditedFiles.Insert(0, path);
            if (settings.RecentEditedFiles.Count > MaxRecentFiles)
                settings.RecentEditedFiles.RemoveRange(MaxRecentFiles,
                    settings.RecentEditedFiles.Count - MaxRecentFiles);
            settings.Save();
        }
        catch (Exception ex) { AppLog.Log("editor", "Could not record the recent file: " + ex.Message); }
    }

    private List<string> RecentsForThisInstance()
    {
        try
        {
            return App.State.Settings.RecentEditedFiles
                .Where(BelongsToThisInstance)
                .Where(File.Exists)
                .ToList();
        }
        catch (Exception) { return new List<string>(); }
    }

    /// <summary>Compares on a folder boundary, not a raw prefix: two instances called "Skyblock" and
    /// "Skyblock2" would otherwise claim each other's files.</summary>
    private bool BelongsToThisInstance(string path) =>
        _roots.Any(root =>
        {
            var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        });

    private void OnRecent(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = RecentButton, Placement = PlacementMode.Bottom };
        var recents = RecentsForThisInstance().Take(15).ToList();

        if (recents.Count == 0)
        {
            menu.Items.Add(new MenuItem
            {
                Header = "Nothing yet - files you open here get listed",
                IsEnabled = false
            });
        }
        else
        {
            foreach (var path in recents)
            {
                var item = new MenuItem
                {
                    Header = Path.GetFileName(path),
                    InputGestureText = FolderLabel(path),
                    ToolTip = path
                };
                var target = path;
                item.Click += (_, _) => OpenFile(target);
                menu.Items.Add(item);
            }
        }
        // Parented to the button rather than opened loose, so it closes with the panel and does not
        // outlive it as an orphaned popup.
        RecentButton.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private string FolderLabel(string path)
    {
        foreach (var root in _roots)
        {
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            var dir = Path.GetDirectoryName(Path.GetRelativePath(root, path));
            return string.IsNullOrEmpty(dir) ? Path.GetFileName(root) : dir;
        }
        return "";
    }

    // ── file tree ────────────────────────────────────────────────────────────

    private void BuildTree()
    {
        var nodes = new List<FileNode>();
        foreach (var root in _roots)
        {
            nodes.Add(new FileNode(root, Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)),
                isFolder: true, ShowAll) { IsExpanded = true });
        }
        FileTree.ItemsSource = nodes;
        TreeStatus.Text = nodes.Count == 0
            ? "This instance has no files on disk yet - launch it once, or download it from the server."
            : "Tip: config/ holds mod settings, kubejs/ holds scripts.";
    }

    private bool ShowAll => ShowAllBox.IsChecked == true;

    private void OnShowAllToggled(object sender, RoutedEventArgs e)
    {
        if (SearchBox.Text.Trim().Length > 0) _ = RunSearchAsync();
        else BuildTree();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchBox.Text.Trim().Length == 0)
        {
            _searchCts?.Cancel();
            BuildTree();
        }
        else _ = RunSearchAsync();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        SearchBox.Text = "";
        e.Handled = true;
    }

    /// <summary>Flat search across the whole instance, for finding a config without knowing its
    /// folder.</summary>
    /// <remarks>The walk runs off the UI thread and the status line shows it is running, so a pack
    /// with a huge number of files doesn't freeze the window and an unfinished search doesn't look
    /// like one that found nothing.</remarks>
    private async Task RunSearchAsync()
    {
        const int cap = 400;
        var query = SearchBox.Text.Trim();
        var showAll = ShowAll;
        var roots = _roots.ToList();

        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var token = cts.Token;

        TreeStatus.Text = $"Searching for '{query}'...";

        try
        {
            var hits = await Task.Run(() =>
            {
                var found = new List<(string Path, string Name)>();
                foreach (var root in roots)
                {
                    foreach (var path in SafeEnumerate(root))
                    {
                        token.ThrowIfCancellationRequested();
                        if (found.Count >= cap) break;
                        var name = Path.GetFileName(path);
                        if (!name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!(showAll ? !TextFileService.IsKnownBinary(path) : TextFileService.LooksEditable(path)))
                            continue;
                        found.Add((path, name));
                    }
                }
                return found;
            }, token);

            if (token.IsCancellationRequested || !ReferenceEquals(_searchCts, cts)) return;

            FileTree.ItemsSource = hits
                .Select(hit => new FileNode(hit.Path, hit.Name, isFolder: false, showAll)
                {
                    Suffix = "· " + FolderLabel(hit.Path)
                })
                .ToList();

            TreeStatus.Text = hits.Count == 0
                ? $"Nothing matching '{query}'."
                : hits.Count >= cap ? $"{cap}+ matches - narrow the search." : $"{hits.Count} match(es).";
        }
        catch (OperationCanceledException) { /* superseded by the next keystroke */ }
        catch (Exception ex)
        {
            AppLog.Log("editor", "Search failed: " + ex.Message);
            TreeStatus.Text = "That search could not be finished.";
        }
    }

    private static IEnumerable<string> SafeEnumerate(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(dir); }
            catch { continue; }
            foreach (var entry in entries)
            {
                if (Directory.Exists(entry))
                {
                    // Worlds and the runtime are big and never hand-edited as text.
                    var name = Path.GetFileName(entry);
                    if (name is "saves" or "libraries" or "assets" or "versions" or "shaderpacks") continue;
                    stack.Push(entry);
                }
                else yield return entry;
            }
        }
    }

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FileNode { IsFolder: false } node && node.FullPath.Length > 0)
            OpenFile(node.FullPath);
    }

    // ── open documents ───────────────────────────────────────────────────────

    /// <summary>Opens <paramref name="path"/> in a tab, or brings its tab forward if it is open.</summary>
    public void OpenFile(string path) => OpenFile(path, remember: true);

    private void OpenFile(string path, bool remember)
    {
        var already = _open.FirstOrDefault(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));
        if (already is not null) { OpenTabs.SelectedItem = already; return; }

        // Ask before reading: a .png opened as text is a screen of replacement characters, and an
        // 8 MB recipe dump is a frozen window. Both get a named state instead.
        var refusal = TextFileService.WhyNotEditable(path);
        if (refusal is not null)
        {
            var binary = new OpenDoc(path, refusal);
            _open.Add(binary);
            OpenTabs.SelectedItem = binary;
            // Not recorded as recent, so a .png clicked once by accident doesn't show up as an
            // edited file. It still counts as an open tab.
            if (remember) SaveSession();
            return;
        }

        try
        {
            var file = TextFileService.Read(path);
            var doc = new OpenDoc(path, file);
            _open.Add(doc);
            OpenTabs.SelectedItem = doc;
            if (remember)
            {
                // Recorded as it happens rather than only on close, so a crash or a forced quit
                // still leaves the list of what was open.
                RememberRecent(path);
                SaveSession();
            }
        }
        catch (Exception ex)
        {
            AppLog.Log("editor", $"Could not open {path}: {ex.Message}");
            var failed = new OpenDoc(path, "This file could not be opened. The app log has the details.");
            _open.Add(failed);
            OpenTabs.SelectedItem = failed;
        }
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        // Remember where the caret was in the outgoing document so switching back lands in the
        // same place rather than at the top of a two-thousand-line config.
        SyncCurrentFromEditor();
        if (_current is not null && !_suppressEditorEvents && !_current.IsBinary)
            _current.CaretIndex = Editor.CaretIndex;

        _current = OpenTabs.SelectedItem as OpenDoc;
        _suppressEditorEvents = true;
        try
        {
            if (_current is null)
            {
                Editor.Visibility = Visibility.Collapsed;
                BinaryPanel.Visibility = Visibility.Collapsed;
                EmptyHint.Visibility = Visibility.Visible;
                Editor.Text = "";
            }
            else if (_current.IsBinary)
            {
                Editor.Visibility = Visibility.Collapsed;
                EmptyHint.Visibility = Visibility.Collapsed;
                Editor.Text = "";
                ShowBinaryPanel(_current);
            }
            else
            {
                BinaryPanel.Visibility = Visibility.Collapsed;
                EmptyHint.Visibility = Visibility.Collapsed;
                Editor.Visibility = Visibility.Visible;
                Editor.Text = _current.Text;
                Editor.CaretIndex = Math.Min(_current.CaretIndex, Editor.Text.Length);
                Editor.Focus();
            }
        }
        finally { _suppressEditorEvents = false; }

        // Find and go-to-line have nothing to act on once the tab is a file we will not open.
        if (_current is null or { IsBinary: true })
        {
            FindBar.Visibility = Visibility.Collapsed;
            GoToBar.Visibility = Visibility.Collapsed;
        }

        ApplyHighlighting();
        RefreshStatus();
        UpdateDiskBanner();
        if (FindBar.Visibility == Visibility.Visible) UpdateFindStatus();
    }

    private void ShowBinaryPanel(OpenDoc doc)
    {
        BinaryPanel.Visibility = Visibility.Visible;
        BinaryTitle.Text = "This is not a text file";
        BinaryBody.Text = doc.Refusal ?? "";
        try
        {
            var info = new FileInfo(doc.Path);
            BinaryMeta.Text = info.Exists
                // FileInfo.LastWriteTime is Kind=Local, so FromLocal; FromUtc would shift it by the
                // machine offset. Only reached when info.Exists, so the MinValue case cannot arise.
                ? $"{info.Name} · {TextFileService.FormatBytes(info.Length)} · changed {TimeFormat.DateTime(TimeFormat.FromLocal(info.LastWriteTime))}"
                : doc.Path;
            OpenExternallyButton.IsEnabled = info.Exists;
        }
        catch (Exception)
        {
            BinaryMeta.Text = doc.Path;
            OpenExternallyButton.IsEnabled = false;
        }
    }

    private void SyncCurrentFromEditor()
    {
        if (_current is null || _current.IsBinary || _suppressEditorEvents) return;
        _current.Text = Editor.Text;
    }

    private void OnEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        _layer?.NoteTextChanged();
        if (_suppressEditorEvents || _current is null || _current.IsBinary) return;
        _current.Text = Editor.Text;
        _current.IsDirty = _current.Text != _current.Original!.Text;
        RefreshStatus();
    }

    private void OnEditorSelectionChanged(object sender, RoutedEventArgs e)
    {
        var index = Editor.CaretIndex;
        var line = Editor.GetLineIndexFromCharacterIndex(index);
        var column = index - Editor.GetCharacterIndexFromLineIndex(line);
        CursorLabel.Text = $"Ln {line + 1}, Col {column + 1}";
    }

    private void OnEditorScrolled(object sender, ScrollChangedEventArgs e) => _layer?.Repaint();

    private void RefreshStatus()
    {
        var editable = _current is { IsBinary: false };
        SaveButton.IsEnabled = editable && _current!.IsDirty && !_current.IsReadOnlyOnDisk;
        SaveAllButton.IsEnabled = _open.Any(d => d is { IsBinary: false, IsDirty: true, IsReadOnlyOnDisk: false });
        RevertButton.IsEnabled = editable;
        DirtyDot.Visibility = editable && _current!.IsDirty ? Visibility.Visible : Visibility.Collapsed;

        if (_current is null)
        {
            EditorStatus.Text = "";
            SyntaxLabel.Text = "";
            CursorLabel.Text = "";
            return;
        }

        if (_current.IsBinary)
        {
            EditorStatus.Text = _current.Path;
            SyntaxLabel.Text = "read-only";
            CursorLabel.Text = "";
            return;
        }

        var parts = new List<string> { _current.Path, _current.Original!.Newline == "\n" ? "LF" : "CRLF" };
        if (_current.Original.HasBom) parts.Add("BOM");
        if (_current.IsReadOnlyOnDisk) parts.Add("read-only on disk");
        if (_current.IsDirty) parts.Add("unsaved changes");
        EditorStatus.Text = string.Join("  ·  ", parts);

        SyntaxLabel.Text = TextFileService.SyntaxLabel(_current.Syntax);
        SyntaxLabel.ClearValue(ForegroundProperty);

        // The one structural check worth doing: a JSON config with a missing comma stops the game
        // from starting, and the crash names a class rather than a line.
        if (_current.Syntax == TextFileService.TextSyntax.Json &&
            TextFileService.DescribeJsonError(_current.Text) is { } jsonError)
        {
            SyntaxLabel.Text = "JSON · " + jsonError;
            SyntaxLabel.SetResourceReference(ForegroundProperty, "WarningBrush");
        }
    }

    // ── syntax colouring ─────────────────────────────────────────────────────

    private void ApplyHighlighting()
    {
        if (_layer is null) return;
        var doc = _current;
        var on = doc is { IsBinary: false }
                 && TextFileService.IsHighlightable(doc.Syntax)
                 && doc.Text.Length <= MaxHighlightChars;

        _layer.Syntax = doc?.Syntax ?? TextFileService.TextSyntax.PlainText;
        _layer.Enabled = on;
        _layer.NoteTextChanged();
        _layer.Repaint(force: true);

        // The box still owns the caret, the selection and the hit testing; only its glyphs step
        // aside so the coloured ones underneath show through.
        if (on) Editor.Foreground = Brushes.Transparent;
        else Editor.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
    }

    private void OnHighlightFailed()
    {
        Editor.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
    }

    // ── saving ───────────────────────────────────────────────────────────────

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        try { await SaveCurrentAsync(); }
        catch (Exception ex) { Fail("Could not save that file.", ex); }
    }

    private async void OnSaveAll(object sender, RoutedEventArgs e)
    {
        try { await SaveAllAsync(); }
        catch (Exception ex) { Fail("Could not save every file.", ex); }
    }

    private async Task<bool> SaveCurrentAsync() =>
        _current is { IsBinary: false } && await SaveAsync(_current);

    private async Task SaveAllAsync()
    {
        foreach (var doc in _open.Where(d => d is { IsBinary: false, IsDirty: true }).ToList())
            if (!await SaveAsync(doc)) return;   // stop at the first refusal rather than asking N times
    }

    private async Task<bool> SaveAsync(OpenDoc doc)
    {
        if (doc.IsBinary || doc.Original is null) return true;
        if (doc == _current) doc.Text = Editor.Text;
        if (!doc.IsDirty && doc.Text == doc.Original.Text) return true;

        // Someone (the game, another editor, a sync) rewrote it while it was open here.
        if (TextFileService.ChangedOnDisk(doc.Path, doc.Original))
        {
            var overwrite = await AppDialog.ConfirmAsync(Window.GetWindow(this), "File changed on disk",
                $"{Path.GetFileName(doc.Path)} has changed on disk since you opened it. " +
                "Save anyway and overwrite those changes?",
                "Overwrite", "Cancel", danger: true);
            if (!overwrite) return false;
        }

        try
        {
            // Before the write, so a config never seen by the Config pages still has its original
            // recorded and this save shows as an edit.
            ConfigEditTracker.RememberBeforeWrite(App.State.Packs, _packId, doc.Path);
            var writtenAt = TextFileService.Write(doc.Path, doc.Text, doc.Original);
            doc.Original = doc.Original with { Text = doc.Text, WrittenAtUtc = writtenAt };
            doc.IsDirty = false;
            doc.ExternallyChanged = false;
            EditorStatus.Text = $"Saved {Path.GetFileName(doc.Path)} at {TimeFormat.TimeWithSeconds(DateTimeOffset.Now)}";
            AppLog.Log("editor", $"Saved {doc.Path}");
            RememberRecent(doc.Path);
            FileSaved?.Invoke(doc.Path);
            RefreshStatus();
            UpdateDiskBanner();
            return true;
        }
        catch (Exception ex)
        {
            Fail($"Could not save {Path.GetFileName(doc.Path)}.", ex);
            return false;
        }
    }

    private void OnRevert(object sender, RoutedEventArgs e)
    {
        if (_current is not { IsBinary: false }) return;
        ReloadCurrentFromDisk(quiet: false);
    }

    private void ReloadCurrentFromDisk(bool quiet)
    {
        if (_current is not { IsBinary: false } doc) return;
        try
        {
            var file = TextFileService.Read(doc.Path);
            doc.Original = file;
            doc.Text = file.Text;
            doc.IsDirty = false;
            doc.ExternallyChanged = false;
            _suppressEditorEvents = true;
            Editor.Text = file.Text;
            _suppressEditorEvents = false;
            ApplyHighlighting();
            RefreshStatus();
            UpdateDiskBanner();
            if (!quiet) EditorStatus.Text = $"Reloaded {Path.GetFileName(doc.Path)} from disk.";
        }
        catch (Exception ex) { Fail($"Could not reload {Path.GetFileName(doc.Path)}.", ex); }
    }

    /// <summary>Raw exception text goes to the log; the status bar gets the sentence.</summary>
    private void Fail(string sentence, Exception ex)
    {
        AppLog.LogError("editor", ex);
        EditorStatus.Text = sentence;
    }

    // ── changed on disk ──────────────────────────────────────────────────────

    private void OnDiskWatchTick(object? sender, EventArgs e)
    {
        if (_current is not { IsBinary: false } doc || doc.Original is null) return;
        if (doc.ExternallyChanged) return;
        if (!TextFileService.ChangedOnDisk(doc.Path, doc.Original)) return;
        doc.ExternallyChanged = true;
        UpdateDiskBanner();
    }

    private void UpdateDiskBanner()
    {
        var show = _current is { IsBinary: false, ExternallyChanged: true };
        DiskBanner.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        DiskBannerText.Text = _current!.IsDirty
            ? $"{Path.GetFileName(_current.Path)} was changed on disk while you were editing it. Reloading throws your changes away."
            : $"{Path.GetFileName(_current.Path)} was changed on disk - what is shown here is the old version.";
    }

    private async void OnReloadFromDisk(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_current is not { IsBinary: false } doc) return;
            if (doc.IsDirty)
            {
                var ok = await AppDialog.ConfirmAsync(Window.GetWindow(this), "Reload from disk",
                    $"Your unsaved changes to {Path.GetFileName(doc.Path)} will be thrown away and the " +
                    "version on disk loaded in their place. This cannot be undone.",
                    "Reload", "Cancel", danger: true);
                if (!ok) return;
            }
            ReloadCurrentFromDisk(quiet: false);
        }
        catch (Exception ex) { Fail("Could not reload that file.", ex); }
    }

    /// <summary>"Keep mine" takes the new timestamp but not the new contents, so the next save
    /// overwrites without asking again.</summary>
    private void OnKeepMine(object sender, RoutedEventArgs e)
    {
        if (_current is not { IsBinary: false } doc || doc.Original is null) return;
        try
        {
            doc.Original = doc.Original with { WrittenAtUtc = File.GetLastWriteTimeUtc(doc.Path) };
            doc.ExternallyChanged = false;
            UpdateDiskBanner();
            EditorStatus.Text = $"Keeping the editor's version of {Path.GetFileName(doc.Path)}.";
        }
        catch (Exception ex) { Fail("Could not keep that version.", ex); }
    }

    // ── external ─────────────────────────────────────────────────────────────

    private void OnRevealInExplorer(object sender, RoutedEventArgs e)
    {
        var target = _current?.Path ?? _roots.FirstOrDefault();
        if (target is null) return;
        if (!SafeLaunch.RevealFile(target)) EditorStatus.Text = "Could not open Explorer.";
    }

    /// <summary>Text and image files open in their default app; anything Windows might run instead
    /// (a .js script, for one) is shown in Explorer.</summary>
    private void OnOpenExternally(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        if (!SafeLaunch.OpenFile(_current.Path)) EditorStatus.Text = "Windows could not open that file.";
    }

    private void OnWrapToggled(object sender, RoutedEventArgs e)
    {
        Editor.TextWrapping = WrapBox.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap;
        _layer?.Repaint(force: true);
    }

    // ── closing tabs ─────────────────────────────────────────────────────────

    private async void OnCloseTab(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not FrameworkElement { Tag: OpenDoc doc }) return;
            await CloseTabAsync(doc);
        }
        catch (Exception ex) { Fail("Could not close that tab.", ex); }
    }

    /// <summary>Ctrl+W on the tab the user is looking at.</summary>
    private async Task CloseCurrentTabAsync()
    {
        if (_current is null) return;
        try { await CloseTabAsync(_current); }
        catch (Exception ex) { Fail("Could not close that tab.", ex); }
    }

    private async Task CloseTabAsync(OpenDoc doc)
    {
        if (!await ConfirmDiscardAsync(doc)) return;
        var wasCurrent = doc == _current;
        _open.Remove(doc);
        if (wasCurrent) OpenTabs.SelectedItem = _open.LastOrDefault();
        SaveSession();
    }

    /// <summary>Asks what to do about a file with unsaved changes. True means "carry on closing
    /// it".</summary>
    /// <remarks>Offers only Save (which closes on success) and Cancel, and points at Revert for
    /// discarding, so neither Escape nor a stray click on the backdrop can lose an edit.</remarks>
    private async Task<bool> ConfirmDiscardAsync(OpenDoc doc)
    {
        if (doc.IsBinary || !doc.IsDirty) return true;
        var save = await AppDialog.ConfirmAsync(Window.GetWindow(this), "Unsaved changes",
            $"{Path.GetFileName(doc.Path)} has changes that are not saved.\n\n" +
            "Save them now? To throw them away instead, cancel and use Revert.",
            "Save", "Cancel");
        if (!save) return false;
        return await SaveAsync(doc);
    }

    /// <summary>True when any open file has changes that are not on disk.</summary>
    public bool HasUnsavedChanges
    {
        get
        {
            SyncCurrentFromEditor();
            if (_current is { IsBinary: false } doc) doc.IsDirty = doc.Text != doc.Original!.Text;
            return _open.Any(d => d.IsDirty);
        }
    }

    /// <summary>Asks about every unsaved file and records the session. False means the user
    /// cancelled and the host should stay open.</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        SyncCurrentFromEditor();
        foreach (var doc in _open.ToList())
            if (!await ConfirmDiscardAsync(doc)) return false;
        SaveSession();
        return true;
    }

    // ── find and replace ─────────────────────────────────────────────────────

    /// <summary>Shows the find bar and seeds it with whatever is selected, the way every editor does.
    /// The box above the tree searches file <em>names</em>; this one searches the file.</summary>
    private void ShowFind()
    {
        if (_current is not { IsBinary: false }) return;
        GoToBar.Visibility = Visibility.Collapsed;
        FindBar.Visibility = Visibility.Visible;
        if (Editor.SelectionLength is > 0 and < 200 && !Editor.SelectedText.Contains('\n'))
            FindTextBox.Text = Editor.SelectedText;
        FindTextBox.Focus();
        FindTextBox.SelectAll();
        UpdateFindStatus();
    }

    private void OnCloseFind(object sender, RoutedEventArgs e) => HideFind();

    private void HideFind()
    {
        FindBar.Visibility = Visibility.Collapsed;
        if (_current is { IsBinary: false }) Editor.Focus();
    }

    private void OnToggleReplace(object sender, RoutedEventArgs e)
    {
        ReplaceRow.Visibility = ReplaceBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (ReplaceBox.IsChecked == true) ReplaceTextBox.Focus();
    }

    private void OnFindTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateFindStatus();
        // Search from where the caret already is, so typing walks forward through the file rather
        // than snapping back to the first hit on every keystroke.
        FindFrom(Editor.SelectionStart, forward: true, wrap: true);
    }

    private void OnMatchCaseToggled(object sender, RoutedEventArgs e) => UpdateFindStatus();

    private void OnFindBoxKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                HideFind();
                e.Handled = true;
                break;
            case Key.Enter:
                FindStep(forward: !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                e.Handled = true;
                break;
        }
    }

    private void OnReplaceBoxKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                HideFind();
                e.Handled = true;
                break;
            case Key.Enter:
                ReplaceOnce();
                e.Handled = true;
                break;
        }
    }

    private void OnFindNext(object sender, RoutedEventArgs e) => FindStep(forward: true);
    private void OnFindPrevious(object sender, RoutedEventArgs e) => FindStep(forward: false);

    /// <summary>F3 / Shift+F3 and the two arrow buttons. Opens the bar first if it is not showing,
    /// so F3 on a fresh editor does something sensible rather than nothing.</summary>
    private void FindStep(bool forward)
    {
        if (FindBar.Visibility != Visibility.Visible) { ShowFind(); return; }
        if (FindTextBox.Text.Length == 0) { FindTextBox.Focus(); return; }

        var from = forward
            ? Editor.SelectionStart + Math.Max(Editor.SelectionLength, 1)
            : Editor.SelectionStart - 1;
        FindFrom(from, forward, wrap: true);
    }

    private StringComparison FindComparison =>
        MatchCaseBox.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>Selects the next (or previous) match and scrolls it into view.</summary>
    private void FindFrom(int start, bool forward, bool wrap)
    {
        var needle = FindTextBox.Text;
        if (needle.Length == 0 || _current is not { IsBinary: false }) return;

        var text = Editor.Text;
        if (text.Length == 0) { FindStatus.Text = "No matches"; return; }

        start = Math.Clamp(start, 0, Math.Max(text.Length - 1, 0));
        var index = forward
            ? text.IndexOf(needle, start, FindComparison)
            : LastIndexOfSafe(text, needle, start);

        if (index < 0 && wrap)
        {
            index = forward
                ? text.IndexOf(needle, 0, FindComparison)
                : LastIndexOfSafe(text, needle, text.Length - 1);
        }

        if (index < 0)
        {
            FindStatus.Text = "No matches";
            return;
        }

        Editor.Select(index, needle.Length);
        Editor.ScrollToLine(Math.Max(Editor.GetLineIndexFromCharacterIndex(index) - 2, 0));
        UpdateFindStatus();
    }

    /// <summary>Backwards search that survives a start index past the end of the string.</summary>
    private int LastIndexOfSafe(string text, string needle, int start)
    {
        if (text.Length == 0 || start < 0) return -1;
        start = Math.Min(start, text.Length - 1);
        return text.LastIndexOf(needle, start, start + 1, FindComparison);
    }

    /// <summary>"3 of 17". The count tells you whether what you're looking for is in this file at
    /// all.</summary>
    private void UpdateFindStatus()
    {
        var needle = FindTextBox.Text;
        if (needle.Length == 0) { FindStatus.Text = ""; return; }

        var text = Editor.Text;
        var total = 0;
        var caretIndex = 0;
        var at = 0;
        while (at <= text.Length - needle.Length)
        {
            var hit = text.IndexOf(needle, at, FindComparison);
            if (hit < 0) break;
            total++;
            if (hit <= Editor.SelectionStart) caretIndex = total;
            at = hit + 1;
        }

        FindStatus.Text = total == 0 ? "No matches" : $"{Math.Max(caretIndex, 1)} of {total}";
    }

    private void OnReplace(object sender, RoutedEventArgs e) => ReplaceOnce();

    /// <summary>Replaces what is selected when it is a match, then steps on. Replace with nothing
    /// selected finds first and replaces on the second press, which is what every editor does.</summary>
    private void ReplaceOnce()
    {
        if (_current is not { IsBinary: false }) return;
        var needle = FindTextBox.Text;
        if (needle.Length == 0) { FindTextBox.Focus(); return; }

        if (Editor.SelectionLength == needle.Length &&
            string.Equals(Editor.SelectedText, needle, FindComparison))
        {
            var at = Editor.SelectionStart;
            Editor.SelectedText = ReplaceTextBox.Text;
            Editor.Select(at + ReplaceTextBox.Text.Length, 0);
        }
        FindFrom(Editor.SelectionStart, forward: true, wrap: true);
    }

    private void OnReplaceAll(object sender, RoutedEventArgs e)
    {
        if (_current is not { IsBinary: false }) return;
        var needle = FindTextBox.Text;
        if (needle.Length == 0) { FindTextBox.Focus(); return; }

        var text = Editor.Text;
        var built = new System.Text.StringBuilder(text.Length);
        var at = 0;
        var count = 0;
        while (true)
        {
            var hit = text.IndexOf(needle, at, FindComparison);
            if (hit < 0) break;
            built.Append(text, at, hit - at).Append(ReplaceTextBox.Text);
            at = hit + needle.Length;
            count++;
        }
        if (count == 0) { FindStatus.Text = "No matches"; return; }
        built.Append(text, at, text.Length - at);

        var caret = Editor.CaretIndex;
        Editor.Text = built.ToString();
        Editor.CaretIndex = Math.Min(caret, Editor.Text.Length);
        FindStatus.Text = $"Replaced {count}";
        EditorStatus.Text = $"Replaced {count} occurrence(s) - not saved yet.";
    }

    // ── go to line ───────────────────────────────────────────────────────────

    private void ShowGoTo()
    {
        if (_current is not { IsBinary: false }) return;
        FindBar.Visibility = Visibility.Collapsed;
        GoToBar.Visibility = Visibility.Visible;
        var lines = Editor.LineCount;
        GoToHint.Text = lines > 0 ? $"1 - {lines}" : "";
        GoToTextBox.Text = "";
        GoToTextBox.Focus();
    }

    private void OnCloseGoTo(object sender, RoutedEventArgs e) => HideGoTo();

    private void HideGoTo()
    {
        GoToBar.Visibility = Visibility.Collapsed;
        if (_current is { IsBinary: false }) Editor.Focus();
    }

    private void OnGoToGo(object sender, RoutedEventArgs e) => GoToLine();

    private void OnGoToKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                HideGoTo();
                e.Handled = true;
                break;
            case Key.Enter:
                GoToLine();
                e.Handled = true;
                break;
        }
    }

    private void GoToLine()
    {
        if (_current is not { IsBinary: false }) return;
        if (!int.TryParse(GoToTextBox.Text.Trim(), out var line) || line < 1)
        {
            GoToHint.Text = "Type a line number";
            return;
        }
        var last = Math.Max(Editor.LineCount, 1);
        line = Math.Min(line, last);
        var index = Editor.GetCharacterIndexFromLineIndex(line - 1);
        if (index < 0) return;
        Editor.CaretIndex = index;
        Editor.Select(index, 0);
        Editor.ScrollToLine(Math.Max(line - 3, 0));
        HideGoTo();
    }

    // ── view models ──────────────────────────────────────────────────────────

    /// <summary>A file open in the editor: what is on screen and what it takes to write it back. A
    /// binary or oversized file is also an open tab, carrying a refusal instead of text, so closing
    /// it works the same way.</summary>
    private sealed class OpenDoc : INotifyPropertyChanged
    {
        public OpenDoc(string path, TextFileService.TextFile file)
        {
            Path = path;
            Original = file;
            Text = file.Text;
            Syntax = TextFileService.DetectSyntax(path);
            IsReadOnlyOnDisk = ReadOnlyOnDisk(path);
        }

        public OpenDoc(string path, string refusal)
        {
            Path = path;
            Refusal = refusal;
            Text = "";
            Syntax = TextFileService.TextSyntax.PlainText;
        }

        public string Path { get; }
        public TextFileService.TextFile? Original { get; set; }
        public string? Refusal { get; }
        public bool IsBinary => Refusal is not null;
        public string Text { get; set; }
        public int CaretIndex { get; set; }
        public TextFileService.TextSyntax Syntax { get; }
        public bool IsReadOnlyOnDisk { get; }

        /// <summary>Set by the disk watch when the file changed under the editor.</summary>
        public bool ExternallyChanged { get; set; }

        private bool _dirty;
        public bool IsDirty
        {
            get => _dirty;
            set
            {
                if (_dirty == value) return;
                _dirty = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDirty)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TabTitle)));
            }
        }

        public string TabTitle => (IsDirty ? "• " : "") + System.IO.Path.GetFileName(Path);

        private static bool ReadOnlyOnDisk(string path)
        {
            try { return new FileInfo(path).IsReadOnly; }
            catch { return false; }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>What was open last time, per instance.</summary>
    private sealed class SessionFile
    {
        public List<string> Open { get; set; } = new();
        public string? Active { get; set; }
    }

    /// <summary>A node in the file tree. Children are read the first time a folder is expanded, so
    /// opening the editor on a pack with a large world folder costs nothing.</summary>
    private sealed class FileNode : INotifyPropertyChanged
    {
        private readonly bool _showAll;
        private bool _loaded;

        public FileNode(string fullPath, string name, bool isFolder, bool showAll)
        {
            FullPath = fullPath;
            Name = name;
            IsFolder = isFolder;
            _showAll = showAll;
            Children = new ObservableCollection<FileNode>();
            if (isFolder) Children.Add(Placeholder);
            else Suffix = SizeLabel(fullPath);
        }

        private static readonly FileNode Placeholder = new("", "...", false, false);

        public string FullPath { get; }
        public string Name { get; }
        public bool IsFolder { get; }
        public string Suffix { get; set; } = "";
        public ObservableCollection<FileNode> Children { get; }

        public string Glyph => IsFolder ? "" : "";

        public Brush GlyphBrush => (Brush)Application.Current.Resources[
            IsFolder ? "AccentBrush" : "TextTertiaryBrush"];

        public Brush NameBrush => (Brush)Application.Current.Resources[
            IsFolder ? "TextPrimaryBrush" : "TextSecondaryBrush"];

        private bool _expanded;
        public bool IsExpanded
        {
            get => _expanded;
            set
            {
                if (_expanded == value) return;
                _expanded = value;
                if (value) LoadChildren();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            }
        }

        private void LoadChildren()
        {
            if (_loaded || !IsFolder) return;
            _loaded = true;
            Children.Clear();
            try
            {
                var dirs = Directory.GetDirectories(FullPath)
                    .Where(d => !IsSkippedFolder(System.IO.Path.GetFileName(d)))
                    .OrderBy(d => Rank(System.IO.Path.GetFileName(d)))
                    .ThenBy(System.IO.Path.GetFileName, StringComparer.OrdinalIgnoreCase);
                foreach (var dir in dirs)
                    Children.Add(new FileNode(dir, System.IO.Path.GetFileName(dir), true, _showAll));

                var files = Directory.GetFiles(FullPath)
                    .Where(f => _showAll ? !TextFileService.IsKnownBinary(f) : TextFileService.LooksEditable(f))
                    .OrderBy(System.IO.Path.GetFileName, StringComparer.OrdinalIgnoreCase);
                foreach (var file in files)
                    Children.Add(new FileNode(file, System.IO.Path.GetFileName(file), false, _showAll));
            }
            catch { /* unreadable folder: show it empty rather than failing the tree */ }
        }

        /// <summary>saves/ is world data (huge, binary); the rest are the shared runtime.</summary>
        private static bool IsSkippedFolder(string name) =>
            name is "saves" or "libraries" or "assets" or "versions";

        /// <summary>The folders people edit most float to the top of each level.</summary>
        private static int Rank(string name) =>
            Array.IndexOf(TextFileService.PreferredFolders, name.ToLowerInvariant()) is var i && i >= 0 ? i : 100;

        private static string SizeLabel(string path)
        {
            try { return TextFileService.FormatBytes(new FileInfo(path).Length); }
            catch { return ""; }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>Minimal ICommand so Ctrl+S can be an InputBinding without pulling in a framework.</summary>
    private sealed class RelayCommand(Action<object?> execute) : ICommand
    {
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute(parameter);
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }

    /// <summary>Draws the coloured text underneath the editor's own transparent glyphs.</summary>
    /// <remarks>WPF has no rich text box that copes with multi-MB configs, so colour is painted
    /// rather than stored: the <see cref="TextBox"/> keeps its caret, selection, undo and IME, and
    /// this layer paints the same characters in colour. Every run is placed at the rectangle
    /// <see cref="TextBoxBase.GetRectFromCharacterIndex"/> reports for its first character, so the
    /// box's layout is the only authority and the two can't drift apart. Lines with tabs are drawn
    /// as one run per tab-free stretch for the same reason.</remarks>
    private sealed class SyntaxLayer : FrameworkElement
    {
        public TextBox? Source;
        public TextFileService.TextSyntax Syntax = TextFileService.TextSyntax.PlainText;
        public bool Enabled;

        /// <summary>Raised when colouring gave up, so the owner can put the box's glyphs back.</summary>
        public event Action? Failed;

        /// <summary>The painting is a child visual rather than this element's OnRender, so a
        /// repaint never dirties layout. That lets the owner repaint from the box's
        /// <see cref="FrameworkElement.LayoutUpdated"/> without a layout loop.</summary>
        private readonly DrawingVisual _visual = new();

        /// <summary>A screenful is tens of lines; this is the guard rail, not the target.</summary>
        private const int MaxPaintedLines = 400;

        private readonly List<TextFileService.SyntaxToken> _tokens = new();
        private int[] _lineStarts = [];
        private bool[] _blockAtLineStart = [];
        private bool _stale = true;
        private int _version;
        private Brush[]? _brushes;
        private (int Version, int First, int Last, double X, double Y, double W, double H) _painted = (-1, 0, 0, 0, 0, 0, 0);

        private static readonly string[] BrushKeys =
        [
            "TextPrimaryBrush",   // Text
            "LogMutedBrush",      // Comment
            "LogSuccessBrush",    // String
            "LogAccentBrush",     // Number
            "WarningBrush",       // Keyword
            "TagSharedFgBrush",   // Key
            "TextTertiaryBrush"   // Punctuation
        ];

        public SyntaxLayer() => AddVisualChild(_visual);

        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => _visual;

        public void NoteTextChanged()
        {
            _stale = true;
            _version++;
        }

        public void ForgetBrushes()
        {
            _brushes = null;
            Repaint(force: true);
        }

        /// <summary>Redraws the visible lines if anything about them has moved. Cheap enough to
        /// call on every layout pass because the signature check short-circuits it.</summary>
        public void Repaint(bool force = false)
        {
            try
            {
                var box = Source;
                if (!Enabled || box is null || box.Text.Length == 0) { Blank(force); return; }

                int first, lineCount;
                try
                {
                    first = box.GetFirstVisibleLineIndex();
                    lineCount = box.LineCount;
                }
                catch (Exception) { return; }
                if (first < 0 || lineCount <= 0) { Blank(force); return; }

                // Not bounded by GetLastVisibleLineIndex, which disagrees with where the box drew
                // the lines when the text is shorter than the viewport. Stop at the first line
                // placed past the bottom edge instead, with a cap for pathological layouts.
                var last = Math.Min(lineCount - 1, first + MaxPaintedLines);

                var anchor = SafeRect(box, first);
                var signature = (_version, first, last, anchor.X, anchor.Y, RenderSize.Width, RenderSize.Height);
                if (!force && signature == _painted) return;
                _painted = signature;

                Paint(box, first, last);
            }
            catch (Exception ex)
            {
                Enabled = false;
                Blank(force: true);
                AppLog.Log("editor", "Syntax colouring turned itself off: " + ex.Message);
                Failed?.Invoke();
            }
        }

        private void Blank(bool force)
        {
            if (!force && _painted.Version == -1) return;
            _painted = (-1, 0, 0, 0, 0, 0, 0);
            using var dc = _visual.RenderOpen();
        }

        private static Rect SafeRect(TextBox box, int line)
        {
            try
            {
                var index = box.GetCharacterIndexFromLineIndex(line);
                return index < 0 ? Rect.Empty : box.GetRectFromCharacterIndex(index);
            }
            catch (Exception) { return Rect.Empty; }
        }

        private void Paint(TextBox box, int first, int last)
        {
            EnsureLineState(box.Text);

            var typeface = new Typeface(box.FontFamily, box.FontStyle, box.FontWeight, box.FontStretch);
            var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var brushes = Brushes();

            var bottom = RenderSize.Height;
            using var dc = _visual.RenderOpen();
            for (var line = first; line <= last; line++)
            {
                string raw;
                int lineStart;
                try
                {
                    raw = box.GetLineText(line);
                    lineStart = box.GetCharacterIndexFromLineIndex(line);
                }
                catch (Exception) { continue; }
                if (lineStart < 0) continue;

                var start = box.GetRectFromCharacterIndex(lineStart);
                if (!start.IsEmpty && start.Y > bottom) break;

                var body = raw.TrimEnd('\r', '\n');
                if (body.Length == 0) continue;

                var block = BlockStateAt(lineStart);
                TextFileService.TokenizeLine(Syntax, body, ref block, _tokens);
                if (_tokens.Count == 0) continue;

                DrawLine(dc, box, typeface, pixelsPerDip, brushes, lineStart, body);
            }
        }

        private void DrawLine(DrawingContext dc, TextBox box, Typeface typeface, double pixelsPerDip,
            Brush[] brushes, int lineStart, string body)
        {
            var at = 0;
            while (at < body.Length)
            {
                if (body[at] == '\t') { at++; continue; }

                var end = at;
                while (end < body.Length && body[end] != '\t') end++;

                var rect = box.GetRectFromCharacterIndex(lineStart + at);
                if (rect.IsEmpty || double.IsInfinity(rect.X) || double.IsInfinity(rect.Y))
                {
                    at = end + 1;
                    continue;
                }

                var segment = body.Substring(at, end - at);
                var formatted = new FormattedText(segment, CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, box.FontSize,
                    brushes[(int)TextFileService.TokenKind.Text], pixelsPerDip);

                foreach (var token in _tokens)
                {
                    if (token.Kind == TextFileService.TokenKind.Text) continue;
                    var from = Math.Max(token.Start, at);
                    var to = Math.Min(token.Start + token.Length, end);
                    if (to <= from) continue;
                    formatted.SetForegroundBrush(brushes[(int)token.Kind], from - at, to - from);
                }

                dc.DrawText(formatted, new Point(rect.X, rect.Y));
                at = end + 1;
            }
        }

        private Brush[] Brushes()
        {
            if (_brushes is not null) return _brushes;
            var resources = Application.Current?.Resources;
            var fallback = System.Windows.Media.Brushes.Gainsboro;
            _brushes = BrushKeys
                .Select(key => resources?[key] as Brush ?? fallback)
                .ToArray();
            return _brushes;
        }

        /// <summary>Line offsets, and for the formats with block comments, whether each line starts
        /// inside one. Rebuilt at most once per frame rather than once per keystroke.</summary>
        private void EnsureLineState(string text)
        {
            if (!_stale) return;
            _stale = false;

            var starts = new List<int>(Math.Max(16, text.Length / 40)) { 0 };
            for (var i = 0; i < text.Length; i++)
                if (text[i] == '\n') starts.Add(i + 1);
            _lineStarts = starts.ToArray();

            if (!TextFileService.HasBlockComments(Syntax))
            {
                _blockAtLineStart = [];
                return;
            }

            var flags = new bool[_lineStarts.Length];
            var block = false;
            var scratch = new List<TextFileService.SyntaxToken>();
            for (var line = 0; line < _lineStarts.Length; line++)
            {
                flags[line] = block;
                var from = _lineStarts[line];
                var to = line + 1 < _lineStarts.Length ? _lineStarts[line + 1] - 1 : text.Length;
                if (to > from && text[to - 1] == '\r') to--;
                if (to <= from) continue;
                TextFileService.TokenizeLine(Syntax, text[from..to], ref block, scratch);
            }
            _blockAtLineStart = flags;
        }

        private bool BlockStateAt(int characterIndex)
        {
            if (_blockAtLineStart.Length == 0 || _lineStarts.Length == 0) return false;
            var found = Array.BinarySearch(_lineStarts, characterIndex);
            var line = found >= 0 ? found : ~found - 1;
            if (line < 0 || line >= _blockAtLineStart.Length) return false;
            return _blockAtLineStart[line];
        }
    }
}
