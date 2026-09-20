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
public partial class FileEditorWindow : Window
{
    private readonly Guid _packId;
    private readonly List<string> _roots = new();
    private readonly ObservableCollection<OpenDoc> _open = new();
    private OpenDoc? _current;
    private bool _suppressEditorEvents;

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

        InputBindings.Add(new KeyBinding(new RelayCommand(_ => SaveCurrent()), Key.S, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => SaveAll()), Key.S, ModifierKeys.Control | ModifierKeys.Shift));

        Loaded += (_, _) =>
        {
            BuildTree();
            WarnIfRunning();
            RefreshStatus();   // nothing open yet: Save and Save all start disabled
        };
        Closing += OnClosingWindow;
    }

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

    private void OnSave(object sender, RoutedEventArgs e) => SaveCurrent();
    private void OnSaveAll(object sender, RoutedEventArgs e) => SaveAll();

    private bool SaveCurrent() => _current is not null && Save(_current);

    private void SaveAll()
    {
        foreach (var doc in _open.Where(d => d.IsDirty).ToList()) Save(doc);
    }

    private bool Save(OpenDoc doc)
    {
        if (doc == _current) doc.Text = Editor.Text;
        if (!doc.IsDirty && doc.Text == doc.Original.Text) return true;

        // Someone (the game, another editor, a sync) rewrote it while it was open here.
        if (TextFileService.ChangedOnDisk(doc.Path, doc.Original))
        {
            var overwrite = MessageBox.Show(this,
                $"{Path.GetFileName(doc.Path)} has changed on disk since you opened it.\n\n" +
                "Save anyway and overwrite those changes?",
                "File changed on disk", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (overwrite != MessageBoxResult.Yes) return false;
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

    private void OnCloseTab(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: OpenDoc doc }) return;
        if (!ConfirmDiscard(doc)) return;
        var wasCurrent = doc == _current;
        _open.Remove(doc);
        if (wasCurrent) OpenTabs.SelectedItem = _open.LastOrDefault();
    }

    private bool ConfirmDiscard(OpenDoc doc)
    {
        if (!doc.IsDirty) return true;
        var answer = MessageBox.Show(this,
            $"Save changes to {Path.GetFileName(doc.Path)}?",
            "Unsaved changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer switch
        {
            MessageBoxResult.Yes => Save(doc),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    private void OnClosingWindow(object? sender, CancelEventArgs e)
    {
        if (_current is not null) _current.Text = Editor.Text;
        foreach (var doc in _open.ToList())
            if (!ConfirmDiscard(doc)) { e.Cancel = true; return; }
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
