using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>
/// The built-in text editor for an instance's own files: mod configs, KubeJS scripts, server lists —
/// anything under the instance folder that is text.
/// </summary>
/// <remarks>
/// <para>Requested by a user who wanted to stop alt-tabbing to Explorer, four levels into
/// <c>%APPDATA%</c>, to change one number in a config. It is deliberately a plain editor rather than
/// a settings UI: mod configs have no schema the launcher could know, and a text box that saves the
/// file back byte-identical apart from the edit is both simpler and impossible to be wrong about.</para>
/// <para>Its own window, not a panel, so it can sit beside the launcher (or the game) while the pack
/// is open. Encoding, BOM and line endings are preserved by <see cref="TextFileService"/>.</para>
/// </remarks>
public partial class FileEditorWindow : Window, IDialogHost
{
    private readonly Guid _packId;
    private readonly List<string> _roots = new();
    private readonly ObservableCollection<OpenDoc> _open = new();
    private OpenDoc? _current;
    private bool _suppressEditorEvents;

    /// <summary>Set once the unsaved-changes question has been answered, so the second Closing pass
    /// (the one we raise ourselves by calling <see cref="Window.Close"/>) goes straight through.</summary>
    private bool _forceClose;

    /// <summary>How many files the editor remembers per instance. Enough to cover "I had the four
    /// configs I was comparing open"; not so many that a stale list reopens half the pack.</summary>
    private const int MaxRestoredFiles = 12;

    /// <summary>One editor window per instance: opening it twice would let two buffers of the same
    /// file overwrite each other.</summary>
    private static readonly Dictionary<Guid, FileEditorWindow> Instances = new();

    public static void Open(Window? owner, Guid packId, string packName)
    {
        if (Instances.TryGetValue(packId, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        var window = new FileEditorWindow(packId, packName) { Owner = owner };
        Instances[packId] = window;
        window.Closed += (_, _) => Instances.Remove(packId);
        window.Show();
    }

    /// <summary>Opens the editor and brings <paramref name="fullPath"/> up in it — the entry point
    /// for double-clicking a file on the instance's Files tab.</summary>
    public static void OpenFileFor(Window? owner, Guid packId, string packName, string fullPath)
    {
        Open(owner, packId, packName);
        if (Instances.TryGetValue(packId, out var window)) window.OpenFile(fullPath);
    }

    public FileEditorWindow(Guid packId, string packName)
    {
        InitializeComponent();
        _packId = packId;
        Title = $"Edit files · {packName}";
        PackLabel.Text = packName;
        OpenTabs.ItemsSource = _open;
        WrapBox.IsChecked = false;

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

        InputBindings.Add(new KeyBinding(new RelayCommand(_ => _ = SaveCurrentAsync()), Key.S, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => _ = SaveAllAsync()), Key.S, ModifierKeys.Control | ModifierKeys.Shift));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => ShowFind()), Key.F, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => FindStep(forward: true)), Key.F3, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => FindStep(forward: false)), Key.F3, ModifierKeys.Shift));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => _ = CloseCurrentTabAsync()), Key.W, ModifierKeys.Control));

        Loaded += (_, _) =>
        {
            BuildTree();
            WarnIfRunning();
            RestoreSession();
            RefreshStatus();   // nothing open yet: Save and Save all start disabled
        };
        Closing += OnClosingWindow;
    }

    // ── session ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Reopens the files that were open when this instance's editor was last closed.
    /// </summary>
    /// <remarks>
    /// <para>Closing the editor mid-edit on three configs and getting nothing back is the sort of
    /// thing that teaches people not to use it. <c>AppSettings.RecentEditedFiles</c> existed for
    /// exactly this and had never been written or read by anything.</para>
    /// <para>The list is global but the paths are absolute, so filtering it by this instance's roots
    /// is what makes it per-instance without a settings change. Files that have since been deleted
    /// (or moved out by a sync) are skipped rather than reported — they are not an error.</para>
    /// </remarks>
    private void RestoreSession()
    {
        if (_roots.Count == 0) return;
        try
        {
            var mine = App.State.Settings.RecentEditedFiles
                .Where(BelongsToThisInstance)
                .Where(File.Exists)
                .Take(MaxRestoredFiles)
                .Reverse()   // newest last, so the newest ends up the selected tab
                .ToList();
            foreach (var path in mine) OpenFile(path);
            if (mine.Count > 0)
                EditorStatus.Text = $"Reopened {mine.Count} file(s) from last time.";
        }
        catch (Exception ex) { AppLog.Log("editor", "Could not restore the open files: " + ex.Message); }
    }

    /// <summary>Records what is open now, newest first, dropping this instance's previous entries so
    /// a file that was closed does not come back.</summary>
    private void SaveSession()
    {
        try
        {
            var settings = App.State.Settings;
            var others = settings.RecentEditedFiles.Where(p => !BelongsToThisInstance(p)).ToList();
            var mine = _open.Select(d => d.Path).Reverse().Take(MaxRestoredFiles).ToList();
            settings.RecentEditedFiles.Clear();
            settings.RecentEditedFiles.AddRange(mine);
            settings.RecentEditedFiles.AddRange(others);
            settings.Save();
        }
        catch (Exception ex) { AppLog.Log("editor", "Could not remember the open files: " + ex.Message); }
    }

    /// <summary>Compares on a folder boundary, not a raw prefix: two instances called "Skyblock" and
    /// "Skyblock2" would otherwise claim each other's files.</summary>
    private bool BelongsToThisInstance(string path) =>
        _roots.Any(root =>
        {
            var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        });

    /// <summary>Editing a config under a running game is not forbidden — sometimes it is exactly what
    /// you want before a restart — but the game will not see it, and anything it writes on exit wins.
    /// Say so once rather than letting someone lose an edit and not know why.</summary>
    private void WarnIfRunning()
    {
        if (App.State.Instances.GetStatus(_packId) == MinecraftInstanceStatus.Idle) return;
        EditorStatus.Text = "This instance is running — Minecraft reads its configs at startup and may overwrite them when it exits.";
    }

    // ── file tree ────────────────────────────────────────────────────────────

    private void BuildTree()
    {
        var nodes = new List<FileNode>();
        foreach (var root in _roots)
        {
            var node = new FileNode(root, Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)), isFolder: true, ShowAll)
            {
                IsExpanded = true
            };
            nodes.Add(node);
        }
        FileTree.ItemsSource = nodes;
        TreeStatus.Text = nodes.Count == 0
            ? "This instance has no files on disk yet — launch it once, or download it from the server."
            : "Tip: config/ holds mod settings, kubejs/ holds scripts.";
    }

    private bool ShowAll => ShowAllBox.IsChecked == true;

    private void OnShowAllToggled(object sender, RoutedEventArgs e)
    {
        if (SearchBox.Text.Trim().Length > 0) RunSearch();
        else BuildTree();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchBox.Text.Trim().Length == 0) BuildTree();
        else RunSearch();
    }

    /// <summary>
    /// Flat search across the whole instance, because "where is the Create config" is a better
    /// question than "which of these twelve folders is it in". Capped so a pack with a hundred
    /// thousand generated files cannot hang the window.
    /// </summary>
    private void RunSearch()
    {
        const int cap = 400;
        var query = SearchBox.Text.Trim();
        var hits = new List<FileNode>();
        foreach (var root in _roots)
        {
            foreach (var path in SafeEnumerate(root))
            {
                if (hits.Count >= cap) break;
                var name = Path.GetFileName(path);
                if (!name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Offerable(path)) continue;
                hits.Add(new FileNode(path, name, isFolder: false, ShowAll)
                {
                    Suffix = "· " + RelativeLabel(path)
                });
            }
        }
        FileTree.ItemsSource = hits;
        TreeStatus.Text = hits.Count == 0
            ? $"Nothing matching “{query}”."
            : hits.Count >= cap ? $"{cap}+ matches — narrow the search." : $"{hits.Count} match(es).";
    }

    private bool Offerable(string path) =>
        ShowAll ? !TextFileService.IsKnownBinary(path) : TextFileService.LooksEditable(path);

    private string RelativeLabel(string path)
    {
        foreach (var root in _roots)
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return Path.GetDirectoryName(Path.GetRelativePath(root, path)) is { Length: > 0 } dir ? dir : ".";
        return "";
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
        if (e.NewValue is FileNode { IsFolder: false } node) OpenFile(node.FullPath);
    }

    // ── open documents ───────────────────────────────────────────────────────

    private void OpenFile(string path)
    {
        var already = _open.FirstOrDefault(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));
        if (already is not null) { OpenTabs.SelectedItem = already; return; }

        try
        {
            var file = TextFileService.Read(path);
            var doc = new OpenDoc(path, file);
            _open.Add(doc);
            OpenTabs.SelectedItem = doc;
            // Recorded as it happens rather than only on close, so a crash or a forced quit still
            // leaves the list of what was open.
            SaveSession();
        }
        catch (Exception ex)
        {
            EditorStatus.Text = ex.Message;
        }
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        // Remember where the caret was in the outgoing document so switching back lands in the
        // same place rather than at the top of a two-thousand-line config.
        if (_current is not null && !_suppressEditorEvents)
        {
            _current.Text = Editor.Text;
            _current.CaretIndex = Editor.CaretIndex;
        }

        _current = OpenTabs.SelectedItem as OpenDoc;
        _suppressEditorEvents = true;
        try
        {
            if (_current is null)
            {
                Editor.Visibility = Visibility.Collapsed;
                EmptyHint.Visibility = Visibility.Visible;
                Editor.Text = "";
            }
            else
            {
                Editor.Visibility = Visibility.Visible;
                EmptyHint.Visibility = Visibility.Collapsed;
                Editor.Text = _current.Text;
                Editor.CaretIndex = Math.Min(_current.CaretIndex, Editor.Text.Length);
                Editor.Focus();
            }
        }
        finally { _suppressEditorEvents = false; }
        RefreshStatus();
        if (FindBar.Visibility == Visibility.Visible) UpdateFindStatus();
    }

    private void OnEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEditorEvents || _current is null) return;
        _current.Text = Editor.Text;
        _current.IsDirty = _current.Text != _current.Original.Text;
        RefreshStatus();
    }

    private void OnEditorSelectionChanged(object sender, RoutedEventArgs e)
    {
        var index = Editor.CaretIndex;
        var line = Editor.GetLineIndexFromCharacterIndex(index);
        var column = index - Editor.GetCharacterIndexFromLineIndex(line);
        CursorLabel.Text = $"Ln {line + 1}, Col {column + 1}";
    }

    private void RefreshStatus()
    {
        SaveButton.IsEnabled = _current?.IsDirty == true;
        SaveAllButton.IsEnabled = _open.Any(d => d.IsDirty);
        if (_current is null) { EditorStatus.Text = ""; return; }
        var dirty = _current.IsDirty ? " · unsaved changes" : "";
        var newline = _current.Original.Newline == "\n" ? "LF" : "CRLF";
        EditorStatus.Text = $"{_current.Path}  ·  {newline}{(_current.Original.HasBom ? " · BOM" : "")}{dirty}";
    }

    // ── saving ───────────────────────────────────────────────────────────────

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        try { await SaveCurrentAsync(); }
        catch (Exception ex) { EditorStatus.Text = "Could not save: " + ex.Message; }
    }

    private async void OnSaveAll(object sender, RoutedEventArgs e)
    {
        try { await SaveAllAsync(); }
        catch (Exception ex) { EditorStatus.Text = "Could not save: " + ex.Message; }
    }

    private async Task<bool> SaveCurrentAsync() => _current is not null && await SaveAsync(_current);

    private async Task SaveAllAsync()
    {
        foreach (var doc in _open.Where(d => d.IsDirty).ToList())
            if (!await SaveAsync(doc)) return;   // stop at the first refusal rather than asking N times
    }

    private async Task<bool> SaveAsync(OpenDoc doc)
    {
        if (doc == _current) doc.Text = Editor.Text;
        if (!doc.IsDirty && doc.Text == doc.Original.Text) return true;

        // Someone (the game, another editor, a sync) rewrote it while it was open here.
        if (TextFileService.ChangedOnDisk(doc.Path, doc.Original))
        {
            var overwrite = await ShowConfirmAsync("File changed on disk",
                $"{Path.GetFileName(doc.Path)} has changed on disk since you opened it. " +
                "Save anyway and overwrite those changes?",
                "Overwrite", "Cancel", danger: true);
            if (!overwrite) return false;
        }

        try
        {
            var writtenAt = TextFileService.Write(doc.Path, doc.Text, doc.Original);
            doc.Original = doc.Original with { Text = doc.Text, WrittenAtUtc = writtenAt };
            doc.IsDirty = false;
            EditorStatus.Text = $"Saved {Path.GetFileName(doc.Path)} at {DateTime.Now:HH:mm:ss}";
            AppLog.Log("editor", $"Saved {doc.Path}");
            RefreshStatus();
            return true;
        }
        catch (Exception ex)
        {
            EditorStatus.Text = "Could not save: " + ex.Message;
            return false;
        }
    }

    private void OnRevert(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        try
        {
            var file = TextFileService.Read(_current.Path);
            _current.Original = file;
            _current.Text = file.Text;
            _current.IsDirty = false;
            _suppressEditorEvents = true;
            Editor.Text = file.Text;
            _suppressEditorEvents = false;
            RefreshStatus();
            EditorStatus.Text = $"Reloaded {Path.GetFileName(_current.Path)} from disk.";
        }
        catch (Exception ex) { EditorStatus.Text = "Could not reload: " + ex.Message; }
    }

    private void OnRevealInExplorer(object sender, RoutedEventArgs e)
    {
        var target = _current?.Path ?? _roots.FirstOrDefault();
        if (target is null) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = File.Exists(target) ? $"/select,\"{target}\"" : $"\"{target}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex) { EditorStatus.Text = "Could not open Explorer: " + ex.Message; }
    }

    private void OnWrapToggled(object sender, RoutedEventArgs e) =>
        Editor.TextWrapping = WrapBox.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap;

    private async void OnCloseTab(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not FrameworkElement { Tag: OpenDoc doc }) return;
            await CloseTabAsync(doc);
        }
        catch (Exception ex) { EditorStatus.Text = "Could not close that tab: " + ex.Message; }
    }

    /// <summary>Ctrl+W on the tab the user is looking at.</summary>
    private async Task CloseCurrentTabAsync()
    {
        if (_current is null) return;
        try { await CloseTabAsync(_current); }
        catch (Exception ex) { EditorStatus.Text = "Could not close that tab: " + ex.Message; }
    }

    private async Task CloseTabAsync(OpenDoc doc)
    {
        if (!await ConfirmDiscardAsync(doc)) return;
        var wasCurrent = doc == _current;
        _open.Remove(doc);
        if (wasCurrent) OpenTabs.SelectedItem = _open.LastOrDefault();
        SaveSession();
    }

    /// <summary>
    /// Asks what to do about a file with unsaved changes. True means "carry on closing it".
    /// </summary>
    /// <remarks>Two buttons, not the old three: the themed dialog is a confirm, and of the three
    /// answers the dangerous one is "discard". So this offers Save (which closes on success) or
    /// Cancel, and points at Revert — which already throws the edit away by reloading from disk —
    /// for the third. That way neither Escape nor a stray click on the backdrop can lose an edit.</remarks>
    private async Task<bool> ConfirmDiscardAsync(OpenDoc doc)
    {
        if (!doc.IsDirty) return true;
        var save = await ShowConfirmAsync("Unsaved changes",
            $"{Path.GetFileName(doc.Path)} has changes that are not saved.\n\n" +
            "Save them now? To throw them away instead, cancel and use Revert.",
            "Save", "Cancel");
        if (!save) return false;
        return await SaveAsync(doc);
    }

    private async void OnClosingWindow(object? sender, CancelEventArgs e)
    {
        if (_forceClose) return;
        try
        {
            if (_current is not null) _current.Text = Editor.Text;

            if (_open.All(d => !d.IsDirty))
            {
                SaveSession();
                return;
            }

            // The overlay cannot be awaited inside a Closing handler, so the close is called off and
            // re-raised once the question has an answer.
            e.Cancel = true;
            foreach (var doc in _open.ToList())
                if (!await ConfirmDiscardAsync(doc)) return;

            _forceClose = true;
            SaveSession();
            Close();
        }
        catch (Exception ex)
        {
            EditorStatus.Text = "Could not close the editor: " + ex.Message;
        }
    }

    // ── find in file ─────────────────────────────────────────────────────────

    /// <summary>Shows the find bar and seeds it with whatever is selected, the way every editor does.
    /// The box at the top of the window searches file <em>names</em>; this one searches the file.</summary>
    private void ShowFind()
    {
        FindBar.Visibility = Visibility.Visible;
        if (Editor.SelectionLength is > 0 and < 200 && !Editor.SelectedText.Contains('\n'))
            FindBox.Text = Editor.SelectedText;
        FindBox.Focus();
        FindBox.SelectAll();
        UpdateFindStatus();
    }

    private void OnCloseFind(object sender, RoutedEventArgs e) => HideFind();

    private void HideFind()
    {
        FindBar.Visibility = Visibility.Collapsed;
        if (_current is not null) Editor.Focus();
    }

    private void OnFindTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateFindStatus();
        // Search from where the caret already is, so typing walks forward through the file rather
        // than snapping back to the first hit on every keystroke.
        FindFrom(Editor.SelectionStart, forward: true, wrap: true, moveCaret: true);
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

    private void OnFindNext(object sender, RoutedEventArgs e) => FindStep(forward: true);
    private void OnFindPrevious(object sender, RoutedEventArgs e) => FindStep(forward: false);

    /// <summary>F3 / Shift+F3 and the two arrow buttons. Opens the bar first if it is not showing,
    /// so F3 on a fresh window does something sensible rather than nothing.</summary>
    private void FindStep(bool forward)
    {
        if (FindBar.Visibility != Visibility.Visible) { ShowFind(); return; }
        if (FindBox.Text.Length == 0) { FindBox.Focus(); return; }

        var from = forward
            ? Editor.SelectionStart + Math.Max(Editor.SelectionLength, 1)
            : Editor.SelectionStart - 1;
        FindFrom(from, forward, wrap: true, moveCaret: true);
    }

    private StringComparison FindComparison =>
        MatchCaseBox.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>Selects the next (or previous) match and scrolls it into view.</summary>
    private void FindFrom(int start, bool forward, bool wrap, bool moveCaret)
    {
        var needle = FindBox.Text;
        if (needle.Length == 0 || _current is null) return;

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

        if (!moveCaret) return;
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

    /// <summary>"3 of 17" — the count is what tells you whether the setting you are looking for is
    /// in this file at all.</summary>
    private void UpdateFindStatus()
    {
        var needle = FindBox.Text;
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

        FindStatus.Text = total == 0
            ? "No matches"
            : $"{Math.Max(caretIndex, 1)} of {total}";
    }

    // ── in-window dialogs ────────────────────────────────────────────────────

    public Task<bool> ShowConfirmAsync(string title, string message,
        string confirmText = "Yes", string cancelText = "Cancel", bool danger = false)
    {
        var overlay = new DialogOverlay();
        overlay.Configure(title, message, confirmText, cancelText, danger);
        return ShowOverlayAsync(overlay);
    }

    public Task ShowMessageAsync(string title, string message, string okText = "OK")
    {
        var overlay = new DialogOverlay();
        overlay.Configure(title, message, okText, null, false);
        return ShowOverlayAsync(overlay);
    }

    private async Task<bool> ShowOverlayAsync(DialogOverlay overlay)
    {
        DialogLayer.Children.Add(overlay);
        DialogLayer.Visibility = Visibility.Visible;
        try { return await overlay.Result; }
        finally
        {
            DialogLayer.Children.Remove(overlay);
            if (DialogLayer.Children.Count == 0) DialogLayer.Visibility = Visibility.Collapsed;
        }
    }

    // ── view models ──────────────────────────────────────────────────────────

    /// <summary>A file open in the editor: what is on screen, and what it takes to write it back.</summary>
    private sealed class OpenDoc : INotifyPropertyChanged
    {
        public OpenDoc(string path, TextFileService.TextFile file)
        {
            Path = path;
            Original = file;
            Text = file.Text;
        }

        public string Path { get; }
        public TextFileService.TextFile Original { get; set; }
        public string Text { get; set; }
        public int CaretIndex { get; set; }

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

        public event PropertyChangedEventHandler? PropertyChanged;
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

        private static readonly FileNode Placeholder = new("", "…", false, false);

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
            catch { /* unreadable folder — show it empty rather than failing the tree */ }
        }

        /// <summary>saves/ is world data (huge, binary); the rest are the shared runtime.</summary>
        private static bool IsSkippedFolder(string name) =>
            name is "saves" or "libraries" or "assets" or "versions";

        /// <summary>The folders people actually edit float to the top of each level.</summary>
        private static int Rank(string name) =>
            Array.IndexOf(TextFileService.PreferredFolders, name.ToLowerInvariant()) is var i && i >= 0 ? i : 100;

        private static string SizeLabel(string path)
        {
            try
            {
                var length = new FileInfo(path).Length;
                return length >= 1024 * 1024 ? $"{length / 1024.0 / 1024:0.#} MB"
                     : length >= 1024 ? $"{length / 1024} KB"
                     : $"{length} B";
            }
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
}
