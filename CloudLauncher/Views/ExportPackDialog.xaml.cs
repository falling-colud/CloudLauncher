using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CloudLauncher.Animations;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>One row of the export card's file tree: a file or folder of the instance, at its
/// depth.</summary>
/// <remarks>Public and top-level because WPF binding can't reflect over a private type. Holds no
/// brush, so theme changes repaint it. The tick is read from the card's
/// <see cref="ExportSelection"/> by <see cref="Refresh"/> rather than stored, so folders, contents
/// and parents always agree.</remarks>
public sealed class ExportTreeRow(ExportTreeNode node, int depth, ExportCandidate? candidate) : INotifyPropertyChanged
{
    public ExportTreeNode Node { get; } = node;
    public int Depth { get; } = depth;

    /// <summary>What the folder listing said about a top-level entry (whether it starts ticked, and
    /// why). Null below the top level.</summary>
    public ExportCandidate? Candidate { get; } = candidate;

    public string Name => Node.Name;
    public Thickness Indent => new(Depth * 18, 0, 0, 0);

    /// <summary>Hidden rather than collapsed on a file, so every name at one depth lines up.</summary>
    public Visibility CaretVisibility => Node.IsFolder ? Visibility.Visible : Visibility.Hidden;

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Caret));
            OnPropertyChanged(nameof(CaretTip));
            OnPropertyChanged(nameof(Glyph));
        }
    }

    private bool _isOpening;

    /// <summary>Asked to open before the folder was read; the arrow shows "..." until it is.</summary>
    public bool IsOpening
    {
        get => _isOpening;
        set { if (_isOpening != value) { _isOpening = value; OnPropertyChanged(nameof(Caret)); } }
    }

    /// <summary>Chevron right or down, or "..." while the folder is being read (Segoe MDL2
    /// Assets).</summary>
    public string Caret => IsOpening ? "" : IsExpanded ? "" : "";
    public string CaretTip => IsExpanded ? "Close this folder" : "Open to choose what inside it goes in";

    /// <summary>Folder, open folder or document.</summary>
    public string Glyph => !Node.IsFolder ? "" : IsExpanded ? "" : "";

    private bool? _checkState;

    /// <summary>Ticked, not ticked, or null for a folder with only part of it ticked.</summary>
    public bool? CheckState
    {
        get => _checkState;
        private set { if (_checkState != value) { _checkState = value; OnPropertyChanged(); } }
    }

    /// <summary>A private file never leaves the PC, so there is nothing to decide about it.</summary>
    public bool CanTick => !Node.IsPrivate;
    public Visibility PrivateVisibility => Node.IsPrivate ? Visibility.Visible : Visibility.Collapsed;

    private string _sizeLabel = "...";
    public string SizeLabel
    {
        get => _sizeLabel;
        private set { if (_sizeLabel != value) { _sizeLabel = value; OnPropertyChanged(); } }
    }

    /// <summary>Re-reads the tick from the selection and the size from the node (which a walk may just
    /// have filled in).</summary>
    public void Refresh(ExportSelection selection)
    {
        CheckState = Node.IsPrivate ? false : selection.StateOf(Node.RelativePath);
        SizeLabel = !Node.IsFolder ? ContentBundleService.FormatSize(Node.Bytes)
            : !Node.Walked ? "..."
            : Node.Files == 0 ? "empty"
            : $"{Node.Files:N0} {(Node.Files == 1 ? "file" : "files")} · {ContentBundleService.FormatSize(Node.Bytes)}";
        // A walk can turn out a folder with nothing in it but private files.
        OnPropertyChanged(nameof(CanTick));
        OnPropertyChanged(nameof(PrivateVisibility));
        OnPropertyChanged(nameof(Hint));
    }

    /// <summary>Tooltip: why a top-level entry is ticked or not. Below the top level, the full path,
    /// since long names may be cut short.</summary>
    public string? Hint => Node.IsPrivate
        ? "Marked private, so it never leaves this PC, whatever is ticked."
        : Candidate is not { } top ? Node.RelativePath
        : top.Recommended ? "Part of what a modpack is made of, so it is ticked to begin with."
        : top.Name.ToLowerInvariant() switch
        {
            "saves" => "Your worlds. Everyone who installs the pack would get a copy of them.",
            "logs" or "crash-reports" or "debug" => "Game logs. No use to anyone installing the pack.",
            "screenshots" => "Your screenshots.",
            "options.txt" or "optionsof.txt" or "optionsshaders.txt" =>
                "Your own video, sound and key settings. They would replace the settings of whoever installs the pack.",
            "servers.dat" or "servers.dat_old" => "Your server list.",
            ".cloudlauncher" => "This launcher's own notes about the instance: categories, flags and the like.",
            "xaero" or "xaeroworldmap" or "xaerowaypoints" or "journeymap" => "Map data from your own worlds.",
            _ => "Not ticked to begin with. Tick it if it belongs in the pack."
        };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>The tree's rows as one flat list, with a run of rows inserted or removed in one go.</summary>
/// <remarks>Thousands of Insert notifications shift the whole list once per row; one Reset lets the
/// virtualizing list rebuild only the visible rows. Small runs still go one by one, which keeps the
/// selection and focus.</remarks>
internal sealed class TreeRowCollection : ObservableCollection<ExportTreeRow>
{
    private const int BulkFrom = 64;

    public void InsertRange(int index, IReadOnlyList<ExportTreeRow> rows)
    {
        if (rows.Count < BulkFrom)
        {
            foreach (var row in rows) Insert(index++, row);
            return;
        }
        CheckReentrancy();
        ((List<ExportTreeRow>)Items).InsertRange(index, rows);
        RaiseReset();
    }

    public void RemoveRange(int index, int count)
    {
        if (count < BulkFrom)
        {
            for (var i = 0; i < count; i++) RemoveAt(index);
            return;
        }
        CheckReentrancy();
        ((List<ExportTreeRow>)Items).RemoveRange(index, count);
        RaiseReset();
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(
            System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
    }
}

/// <summary>The "Export as a modpack" card: format, name and version, which mods and folders go in,
/// then the export itself and a receipt. The writing is done by
/// <see cref="ModpackExportService"/>.</summary>
/// <remarks>The running summary is only a preview; the export resolves everything again when it
/// runs. It runs as a <see cref="PackJobKind.Export"/> job so updates and syncs wait, but it only
/// reads, so Minecraft can still start (the receipt then warns that a config may have been caught
/// half-written). The backdrop only closes the card when nothing is running.</remarks>
public partial class ExportPackDialog : UserControl
{
    private enum Stage { Options, Working, Asking, Finished }

    private readonly TaskCompletionSource<bool> _closed = new();
    private readonly PackDetail _pack;
    private readonly ModpackExportService _exporter;
    private readonly Func<bool> _gameRunning;

    /// <summary>The top level of <c>game/</c>, <c>mods</c> left out (jars are decided per jar).</summary>
    private readonly List<ExportTreeNode> _roots = new();

    /// <summary>The rows on screen: the top level plus every opened folder's contents, in tree order.</summary>
    private readonly TreeRowCollection _tree = new();

    /// <summary>What goes into overrides/. The rows only read it
    /// (<see cref="ExportTreeRow.Refresh"/>).</summary>
    private readonly ExportSelection _selection = new();

    /// <summary>One read per top-level folder, shared by the background pass and by opening it
    /// early.</summary>
    private readonly Dictionary<ExportTreeNode, Task> _walks = new();

    private IReadOnlyList<string> _privatePatterns = [];

    /// <summary>Cancels the preview work (the store lookups, the folder walks) when the card goes.</summary>
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>Null when this PC has no folder for the instance, which leaves nothing to export.</summary>
    private readonly string? _gameDir;

    private Stage _stage = Stage.Options;
    private bool _started;
    private ExportPlan? _plan;
    private bool _planFailed;

    /// <summary>From the quick jar scan; null until it has run.</summary>
    private int? _disabledCount;

    /// <summary>The store gap the options screen is showing, if any.</summary>
    private ExportIdentityGap? _warnedGap;

    /// <summary><see cref="_warnedGap"/> as it stood when Export was pressed. Pressing Export with the
    /// warning on screen answers it, so the export doesn't ask again. Captured at the press, since a
    /// warning that appears later hasn't been seen.</summary>
    private ExportIdentityGap? _acknowledgedGap;

    private CancellationTokenSource? _exportCts;
    private TaskCompletionSource<bool>? _gapAnswer;
    private bool _gameRanDuringExport;
    private bool _finishedOk;
    private string _endedLabel = "Done";
    private string? _savedPath;

    /// <param name="gameRunning">Whether Minecraft is running this instance right now. A delegate, so the
    /// card needs no instance tracker of its own and can be built without the app around it.</param>
    /// <param name="preferredStore">The store this instance's mods follow, which picks the format the
    /// card opens on. Null opens on Modrinth.</param>
    internal ExportPackDialog(PackDetail pack, ModpackExportService exporter, Func<bool> gameRunning,
                              ModSource? preferredStore = null)
    {
        InitializeComponent();
        _pack = pack;
        _exporter = exporter;
        _gameRunning = gameRunning;
        _gameDir = exporter.TryGameDir(pack.Id);
        Focusable = true;

        SubtitleLabel.Text = Describe(pack);
        NameBox.Text = pack.Name;
        // A date rather than 1.0.0: every export gets a different, sortable version without anybody
        // having to remember what the last one was called.
        VersionBox.Text = TimeFormat.VersionStampDate(DateTimeOffset.Now);
        AuthorBox.Text = pack.OwnerUsername;
        SummaryBox.Text = FirstLine(pack.Summary);
        EntryTree.ItemsSource = _tree;
        ModSummaryLabel.Text = "Checking which mods are on Modrinth and CurseForge...";

        if (preferredStore == ModSource.CurseForge) CurseForgeRadio.IsChecked = true;
        else ModrinthRadio.IsChecked = true;

        Loaded += (_, _) =>
        {
            if (_started) return;
            _started = true;
            Animate.SlideFadeIn(this, 0, 14, 200);
            Focus();
            _ = BeginAsync();
        };
        Unloaded += (_, _) =>
        {
            _lifetime.Cancel();
            try { _exportCts?.Cancel(); } catch (ObjectDisposedException) { /* already finished */ }
        };
        UpdateChrome();
    }

    /// <summary>Completes when the card should close.</summary>
    public Task Result => _closed.Task;

    /// <summary>Shows the export card over <paramref name="shell"/> for <paramref name="pack"/> and
    /// returns when it is closed. Everything, including the export itself, happens inside it.</summary>
    public static Task ShowAsync(MainWindow shell, PackDetail pack)
    {
        var state = App.State;
        var exporter = new ModpackExportService(state.Packs, state.ModFingerprints, state.Modrinth,
            state.CurseForge, state.ModMetadata, state.Settings);
        var preferred = state.ModMetadata.Advanced(pack.Id).PreferredSource ?? state.ModInventory.InferredSource(pack.Id);
        var card = new ExportPackDialog(pack, exporter,
            () => state.Instances.GetStatus(pack.Id) != MinecraftInstanceStatus.Idle, preferred);
        return shell.ShowCardAsync(card, card.Result, card.RequestClose,
            new ResizableCardSpec("export-pack", 640, 640, MinWidth: 520, MinHeight: 460));
    }

    /// <summary>The backdrop's close. Only when nothing is running: an export in flight is stopped with
    /// its own Cancel button, never by a click that missed the card.</summary>
    public void RequestClose()
    {
        if (_stage is Stage.Options or Stage.Finished) Close();
    }

    private void Close()
    {
        _lifetime.Cancel();
        _closed.TrySetResult(true);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // Escape means what Cancel means on each step, except that a finished card just closes.
            if (_stage == Stage.Finished) Close();
            else OnSecondary(this, e);
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    // ── filling the options ──────────────────────────────────────────────────

    private async Task BeginAsync()
    {
        GameRunningNote.Visibility = _gameRunning() ? Visibility.Visible : Visibility.Collapsed;
        if (_gameDir is null)
        {
            ModSummaryLabel.Text = "This instance has no folder on this PC yet, so there is nothing to export. "
                                 + "Download it first.";
            PlanBar.Visibility = Visibility.Collapsed;
            EntryListEmpty.Visibility = Visibility.Visible;
            ApplyJarCounts(0, 0);
            UpdateChrome();
            return;
        }

        var ct = _lifetime.Token;
        _privatePatterns = _exporter.PrivatePatterns();
        try
        {
            // Disk only, and quick: the top of the folder and the jar counts the two options are labelled
            // with. The folders themselves are read after, one at a time (WalkAllAsync).
            var patterns = _privatePatterns;
            var (top, mods) = await Task.Run(() =>
            {
                var nodes = new List<(ExportTreeNode Node, ExportCandidate Candidate)>();
                foreach (var c in _exporter.ListCandidates(_pack.Id))
                {
                    if (!c.IsFolder && ExportTree.IsPartial(c.Name)) continue;
                    nodes.Add((c.IsFolder
                        ? ExportTreeNode.ForFolder(c.Name, c.Name, c.FullPath)
                        : ExportTreeNode.ForFile(c.Name, c.Name, c.FullPath, SizeOf(c.FullPath),
                                                 ExportTree.IsPrivate(c.Name, patterns)), c));
                }
                return (nodes, _exporter.ScanMods(_pack.Id));
            }, ct);

            foreach (var (node, candidate) in top)
            {
                _roots.Add(node);
                if (candidate.Recommended) _selection.Set(node.RelativePath, true);
                _tree.Add(new ExportTreeRow(node, 0, candidate));
            }
            RefreshRows();
            ApplyJarCounts(mods.Count(m => !m.Enabled), mods.Count(m => m.IsLocal));
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(ExportPackDialog), ex);
            ApplyJarCounts(0, 0);
        }

        EntryListEmpty.Visibility = _roots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateEntryNote();
        _ = WalkAllAsync(ct);
        await PlanAsync(ct);
    }

    private static long SizeOf(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    /// <summary>Asks both stores about every jar and pack, for the running summary.</summary>
    private async Task PlanAsync(CancellationToken ct)
    {
        PlanBar.Visibility = Visibility.Visible;
        try
        {
            _plan = await _exporter.PlanAsync(_pack, ct);
            _planFailed = false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            // The preview is a convenience. The export checks everything again and reports properly.
            AppLog.LogError(nameof(ExportPackDialog), ex);
            _planFailed = true;
        }

        PlanBar.Visibility = Visibility.Collapsed;
        UpdateModSummary();
        UpdateEntryNote();   // which ticked packs download instead is only known now
    }

    // ── the file tree ────────────────────────────────────────────────────────

    /// <summary>Reads the top-level folders one at a time, for their sizes and entries. Sequential
    /// since they share one disk. A folder the user opens jumps the queue.</summary>
    private async Task WalkAllAsync(CancellationToken ct)
    {
        foreach (var root in _roots.Where(r => r.IsFolder).ToList())
        {
            try { await EnsureWalkedAsync(root, ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { AppLog.Log("export", $"Could not read {root.Name}: {ex.Message}"); }
        }
    }

    /// <summary>The whole of one top-level folder, read once. Everything below the top is read along with
    /// its top-level folder, so a folder the tree can show is always a folder already read.</summary>
    private Task EnsureWalkedAsync(ExportTreeNode root, CancellationToken ct)
    {
        if (root.Walked) return Task.CompletedTask;
        if (!_walks.TryGetValue(root, out var walk))
            _walks[root] = walk = WalkOneAsync(root, ct);
        return walk;
    }

    private async Task WalkOneAsync(ExportTreeNode root, CancellationToken ct)
    {
        var patterns = _privatePatterns;
        var walked = await Task.Run(() => ExportTree.Walk(root.FullPath, root.RelativePath, patterns, ct), ct);
        root.Adopt(walked);
        foreach (var row in _tree)
            if (ReferenceEquals(row.Node, root)) row.Refresh(_selection);
        UpdateEntryNote();
    }

    private void RefreshRows()
    {
        foreach (var row in _tree) row.Refresh(_selection);
    }

    private async void OnToggleExpand(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ExportTreeRow row) await ToggleAsync(row);
    }

    /// <summary>A double-click on a folder's row opens or closes it, except on its box or arrow,
    /// which have their own click handling.</summary>
    private async void OnTreeRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: ExportTreeRow row } item || !row.Node.IsFolder) return;
        for (var d = e.OriginalSource as DependencyObject; d is not null && !ReferenceEquals(d, item);
             d = VisualTreeHelper.GetParent(d))
            if (d is System.Windows.Controls.Primitives.ButtonBase) return;
        e.Handled = true;
        await ToggleAsync(row);
    }

    /// <summary>Space ticks, Right and Enter open, Left closes, on the selected row (the boxes never
    /// take focus).</summary>
    private async void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (EntryTree.SelectedItem is not ExportTreeRow row) return;
        switch (e.Key)
        {
            case Key.Space when row.CanTick:
                e.Handled = true;
                Tick(row, row.CheckState != true);
                break;
            case Key.Right or Key.Enter when row.Node.IsFolder && !row.IsExpanded:
                e.Handled = true;
                await ToggleAsync(row);
                break;
            case Key.Left or Key.Enter when row.IsExpanded:
                e.Handled = true;
                Collapse(row);
                break;
        }
    }

    private async Task ToggleAsync(ExportTreeRow row)
    {
        if (!row.Node.IsFolder || _stage != Stage.Options) return;
        if (row.IsExpanded) { Collapse(row); return; }
        if (!row.Node.Walked)
        {
            row.IsOpening = true;
            try { await EnsureWalkedAsync(row.Node, _lifetime.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                AppLog.Log("export", $"Could not read {row.Name}: {ex.Message}");
                Status($"{row.Name} could not be read.", danger: true);
                return;
            }
            finally { row.IsOpening = false; }
        }
        Expand(row);
    }

    private void Expand(ExportTreeRow row)
    {
        var index = _tree.IndexOf(row);
        if (index < 0 || row.IsExpanded) return;
        row.IsExpanded = true;
        var children = row.Node.Children.Select(child => new ExportTreeRow(child, row.Depth + 1, null)).ToList();
        foreach (var child in children) child.Refresh(_selection);
        _tree.InsertRange(index + 1, children);
    }

    private void Collapse(ExportTreeRow row)
    {
        var index = _tree.IndexOf(row);
        if (index < 0 || !row.IsExpanded) return;
        row.IsExpanded = false;
        var end = index + 1;
        while (end < _tree.Count && _tree[end].Depth > row.Depth) end++;
        _tree.RemoveRange(index + 1, end - index - 1);
    }

    /// <summary>A box was clicked: a partly ticked or unticked row takes everything in it, a ticked one
    /// lets everything go.</summary>
    private void OnRowCheckClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ExportTreeRow row || !row.CanTick) return;
        Tick(row, row.CheckState != true);
    }

    private void Tick(ExportTreeRow row, bool include)
    {
        if (_stage != Stage.Options) return;
        _selection.Set(row.Node.RelativePath, include);
        Consolidate(row.Node.RelativePath);
        RefreshRows();
        UpdateEntryNote();
    }

    /// <summary>After a tick, a folder whose entries now all agree takes that state itself, so ticking
    /// the last unticked file ticks the folder. Walks up as far as that holds.</summary>
    /// <remarks>Private entries don't count, since they never go anywhere.</remarks>
    private void Consolidate(string path)
    {
        for (var parent = ExportSelection.ParentOf(path); parent is not null; parent = ExportSelection.ParentOf(parent))
        {
            if (FindNode(parent) is not { Walked: true } folder) return;
            var votes = folder.Children
                .Where(c => !c.IsPrivate)
                .Select(c => _selection.StateOf(c.RelativePath))
                .Distinct()
                .ToList();
            if (votes.Count != 1 || votes[0] is not { } agreed) return;
            if (_selection.StateOf(parent) == agreed) return;
            _selection.Set(parent, agreed);
        }
    }

    private ExportTreeNode? FindNode(string path)
    {
        IReadOnlyList<ExportTreeNode> level = _roots;
        ExportTreeNode? node = null;
        foreach (var part in path.Split('/'))
        {
            node = level.FirstOrDefault(n => string.Equals(n.Name, part, StringComparison.OrdinalIgnoreCase));
            if (node is null) return null;
            level = node.Children;
        }
        return node;
    }

    private void ApplyJarCounts(int disabled, int local)
    {
        _disabledCount = disabled;
        DisabledBox.Content = disabled == 0 ? "Include disabled mods" : $"Include disabled mods ({disabled})";
        DisabledBox.IsEnabled = disabled > 0;
        LocalBox.Content = local == 0
            ? "Include this PC's own mods (local/mods)"
            : $"Include this PC's own mods from local/mods ({local})";
        LocalBox.IsEnabled = local > 0;
        LocalBox.ToolTip = local == 0
            ? "This instance has no mods of its own in local/mods."
            : "Mods only this PC has, kept out of the shared instance. They go in as ordinary mods.";
        ApplyDisabledHint();
    }

    /// <summary>What happens to a disabled mod depends on the format, so the option says which.</summary>
    private void ApplyDisabledHint()
    {
        DisabledBox.ToolTip = _disabledCount == 0
            ? "No mod is switched off in this instance."
            : Format == ModpackExportFormat.Modrinth
                ? "Packed into the file under their .disabled name, so they arrive switched off."
                : "Listed as optional, so they arrive switched off (Prism asks which ones to take first).";
    }

    // ── reacting to the options ──────────────────────────────────────────────

    private ModpackExportFormat Format =>
        CurseForgeRadio.IsChecked == true ? ModpackExportFormat.CurseForge : ModpackExportFormat.Modrinth;

    private void OnCurseForgeTileClick(object sender, MouseButtonEventArgs e)
    {
        if (_stage == Stage.Options) CurseForgeRadio.IsChecked = true;
    }

    private void OnModrinthTileClick(object sender, MouseButtonEventArgs e)
    {
        if (_stage == Stage.Options) ModrinthRadio.IsChecked = true;
    }

    private void OnFormatChanged(object sender, RoutedEventArgs e)
    {
        if (StatusLabel is null) return;   // raised while the template is still being built
        var curseForge = Format == ModpackExportFormat.CurseForge;
        CurseForgeTile.SetResourceReference(Border.BorderBrushProperty, curseForge ? "AccentBrush" : "BorderBrush");
        ModrinthTile.SetResourceReference(Border.BorderBrushProperty, curseForge ? "BorderBrush" : "AccentBrush");
        AuthorPanel.Visibility = curseForge ? Visibility.Visible : Visibility.Collapsed;
        SummaryPanel.Visibility = curseForge ? Visibility.Collapsed : Visibility.Visible;
        ApplyDisabledHint();
        UpdateModSummary();
        UpdateEntryNote();
        UpdateChrome();
    }

    private void OnFieldChanged(object sender, TextChangedEventArgs e) => UpdateChrome();
    private void OnModOptionChanged(object sender, RoutedEventArgs e) => UpdateModSummary();

    /// <summary>Resets to the recommended folders, dropping any choices made inside folders.</summary>
    private void OnRecommendedEntries(object sender, RoutedEventArgs e)
    {
        _selection.Clear();
        foreach (var row in _tree.Where(r => r.Depth == 0 && r.Candidate is { Recommended: true }))
            _selection.Set(row.Node.RelativePath, true);
        RefreshRows();
        UpdateEntryNote();
    }

    private void OnNoEntries(object sender, RoutedEventArgs e)
    {
        _selection.Clear();
        RefreshRows();
        UpdateEntryNote();
    }

    /// <summary>The line under the mod options: where the mods will come from in the chosen format.</summary>
    private void UpdateModSummary()
    {
        if (ModSummaryLabel is null || DisabledBox is null) return;
        var format = Format;
        var store = ModpackExportService.StoreName(format);

        if (_plan is null)
        {
            if (_planFailed)
                ModSummaryLabel.Text = "The mods could not be checked just now. The export checks them again when it runs.";
            ModWarningLabel.Visibility = Visibility.Collapsed;
            LicenceNote.Visibility = Visibility.Collapsed;
            _warnedGap = null;
            return;
        }

        var counts = _plan.Count(format, DisabledBox.IsChecked == true, LocalBox.IsChecked == true);
        ModSummaryLabel.Text = SummaryLine(counts, store);

        if (counts.Unchecked > 0)
        {
            _warnedGap = new ExportIdentityGap(store, counts.Unchecked);
            ModWarningLabel.Text = $"{store} could not be reached, so {Plural(counts.Unchecked, "mod")} could not "
                                 + $"be checked. They will be packed into the file instead of downloading from {store}. "
                                 + "Export again later to fix that.";
            ModWarningLabel.Visibility = Visibility.Visible;
        }
        else
        {
            _warnedGap = null;
            ModWarningLabel.Visibility = Visibility.Collapsed;
        }

        LicenceNote.Visibility = counts.PackedIn > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string SummaryLine(ExportCounts c, string store)
    {
        if (c.Downloaded + c.PackedIn == 0)
        {
            var none = "No mods will go in";
            if (c.DisabledLeftOut > 0) none += $" · {c.DisabledLeftOut} disabled left out";
            if (c.LocalLeftOut > 0) none += $" · {c.LocalLeftOut} of this PC's own left out";
            return none;
        }

        var parts = new List<string>
        {
            (c.Downloaded == 1 ? $"1 mod downloads from {store}" : $"{c.Downloaded} mods download from {store}")
            + ListedNotes(c, store),
            c.PackedIn == 0 ? "none packed in" : $"{c.PackedIn} packed into the file"
        };
        if (c.DisabledLeftOut > 0) parts.Add($"{c.DisabledLeftOut} disabled left out");
        if (c.LocalLeftOut > 0) parts.Add($"{c.LocalLeftOut} of this PC's own left out");
        return string.Join(" · ", parts);
    }

    /// <summary>" (2 as optional, 1 as CurseForge's own build of the same file)", or nothing.</summary>
    private static string ListedNotes(ExportCounts c, string store)
    {
        var notes = new List<string>();
        if (c.DisabledListed > 0) notes.Add($"{c.DisabledListed} as optional");
        if (c.ListedByName > 0) notes.Add($"{c.ListedByName} as {store}'s own build of the same file");
        return notes.Count == 0 ? "" : $" ({string.Join(", ", notes)})";
    }

    /// <summary>What the ticked entries add up to, once the folders under them have been read.</summary>
    private void UpdateEntryNote()
    {
        if (EntryNote is null) return;
        var ticked = _roots.Where(r => !r.IsPrivate && _selection.StateOf(r.RelativePath) != false).ToList();
        if (ticked.Count == 0)
        {
            EntryNote.Text = _roots.Count == 0 ? "" : "Nothing else is ticked, so the pack will be its mods only.";
            return;
        }

        var folders = ticked.Count(r => r.IsFolder);
        var files = ticked.Count - folders;
        var what = string.Join(", ", new[]
        {
            folders == 0 ? null : folders == 1 ? "1 folder" : $"{folders} folders",
            files == 0 ? null : files == 1 ? "1 file" : $"{files} files"
        }.Where(s => s is not null));
        var partly = ticked.Count(r => _selection.StateOf(r.RelativePath) is null);
        if (partly > 0) what += partly == 1 ? " (one of them in part)" : $" ({partly} of them in part)";

        var (totalFiles, totalBytes, complete) = ExportTree.Included(_roots, _selection);
        EntryNote.Text = complete
            ? $"Ticked: {what} · {totalFiles:N0} {(totalFiles == 1 ? "file" : "files")} in all · "
              + ContentBundleService.FormatSize(totalBytes) + "."
            : $"Ticked: {what}.";

        // The ticked resource and shader packs the store has travel as downloads, like mods do.
        if (_plan?.CountPacks(Format, _selection) is { Listed: > 0 } packs)
            EntryNote.Text += $" Of these, {Plural(packs.Listed, "resource or shader pack")} "
                            + $"{(packs.Listed == 1 ? "downloads" : "download")} from {ModpackExportService.StoreName(Format)} "
                            + "instead of going in as files.";
    }

    /// <summary>Why Export cannot be pressed yet, or null when it can.</summary>
    private string? BlockedReason()
    {
        if (_gameDir is null) return "This instance has no folder on this PC yet. Download it first.";
        if (_pack.IsEmpty || string.IsNullOrWhiteSpace(_pack.MinecraftVersion))
            return "This instance has no Minecraft version yet, so there is nothing to export.";
        if (_pack.Loader != LoaderKind.None && string.IsNullOrWhiteSpace(_pack.LoaderVersion))
            return $"This instance's {_pack.Loader} version is not known yet, and both formats have to name it.";
        if (NameBox.Text.Trim().Length == 0) return "Give the modpack a name.";
        if (VersionBox.Text.Trim().Length == 0) return "Give the modpack a version, e.g. 1.0.0.";
        return null;
    }

    // ── the buttons ──────────────────────────────────────────────────────────

    private void OnPrimary(object sender, RoutedEventArgs e)
    {
        switch (_stage)
        {
            case Stage.Options: _ = StartExportAsync(); break;
            case Stage.Asking: _gapAnswer?.TrySetResult(true); break;
            case Stage.Finished: Close(); break;
        }
    }

    private void OnSecondary(object sender, RoutedEventArgs e)
    {
        switch (_stage)
        {
            case Stage.Options: Close(); break;
            case Stage.Working:
                Status("Stopping...");
                try { _exportCts?.Cancel(); } catch (ObjectDisposedException) { /* already finished */ }
                SecondaryButton.IsEnabled = false;
                break;
            case Stage.Asking: _gapAnswer?.TrySetResult(false); break;
            case Stage.Finished: GoTo(Stage.Options); break;   // "Back", after a stop or a failure
        }
    }

    private void GoTo(Stage stage)
    {
        _stage = stage;
        UpdateChrome();
    }

    /// <summary>Shows the step the card is on and relabels the two buttons for it, putting the reason
    /// for a disabled Export on the button itself.</summary>
    private void UpdateChrome()
    {
        if (PrimaryButton is null || OptionsStep is null) return;

        OptionsStep.Visibility = _stage == Stage.Options ? Visibility.Visible : Visibility.Collapsed;
        WorkStep.Visibility = _stage is Stage.Working or Stage.Asking ? Visibility.Visible : Visibility.Collapsed;
        GapPanel.Visibility = _stage == Stage.Asking ? Visibility.Visible : Visibility.Collapsed;
        // No progress bar while waiting for an answer.
        WorkProgressPanel.Visibility = _stage == Stage.Asking ? Visibility.Collapsed : Visibility.Visible;
        DoneStep.Visibility = _stage == Stage.Finished ? Visibility.Visible : Visibility.Collapsed;
        SecondaryButton.Visibility = Visibility.Visible;
        PrimaryButton.Visibility = Visibility.Visible;

        switch (_stage)
        {
            case Stage.Options:
                StageLabel.Text = "Options";
                SecondaryButton.Content = "Cancel";
                SecondaryButton.IsEnabled = true;
                PrimaryButton.Content = "Export...";
                var blocked = BlockedReason();
                PrimaryButton.IsEnabled = blocked is null;
                PrimaryButton.ToolTip = blocked
                    ?? $"Choose where to save the {(Format == ModpackExportFormat.Modrinth ? ".mrpack" : ".zip")} file";
                break;

            case Stage.Working:
                StageLabel.Text = "Exporting";
                SecondaryButton.Content = "Cancel";
                SecondaryButton.IsEnabled = _exportCts is { IsCancellationRequested: false };
                PrimaryButton.Visibility = Visibility.Collapsed;
                break;

            case Stage.Asking:
                StageLabel.Text = "Exporting";
                SecondaryButton.Content = "Cancel";
                SecondaryButton.IsEnabled = true;
                PrimaryButton.Content = "Export anyway";
                PrimaryButton.IsEnabled = true;
                PrimaryButton.ToolTip = "Pack the unchecked mods into the file as they are";
                break;

            case Stage.Finished:
                StageLabel.Text = _endedLabel;
                SecondaryButton.Content = "Back";
                SecondaryButton.IsEnabled = true;
                SecondaryButton.Visibility = _finishedOk ? Visibility.Collapsed : Visibility.Visible;
                PrimaryButton.Content = _finishedOk ? "Done" : "Close";
                PrimaryButton.IsEnabled = true;
                PrimaryButton.ToolTip = null;
                break;
        }
    }

    private void Status(string? text, bool danger = false)
    {
        StatusLabel.Text = text ?? "";
        StatusLabel.SetResourceReference(TextBlock.ForegroundProperty, danger ? "DangerBrush" : "TextSecondaryBrush");
    }

    // ── the export ───────────────────────────────────────────────────────────

    private async Task StartExportAsync()
    {
        if (_stage != Stage.Options) return;
        if (BlockedReason() is { } why) { Status(why, danger: true); return; }
        if (BusyReason() is { } busy) { Status(busy, danger: true); return; }

        var format = Format;
        var extension = format == ModpackExportFormat.Modrinth ? ".mrpack" : ".zip";
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save the modpack",
            FileName = SafeFileName($"{NameBox.Text.Trim()}-{VersionBox.Text.Trim()}") + extension,
            DefaultExt = extension,
            AddExtension = true,
            Filter = format == ModpackExportFormat.Modrinth
                ? "Modrinth modpack (*.mrpack)|*.mrpack"
                : "CurseForge modpack (*.zip)|*.zip"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        await RunExportAsync(dialog.FileName, format);
    }

    /// <summary>Why another job on this instance rules an export out right now, or null.</summary>
    /// <remarks>Checked again after the save dialog. A download or update would be rewriting the files
    /// the export reads, and <see cref="PackJobs.Start"/> would hand that job's Pause and Stop to the
    /// export.</remarks>
    private string? BusyReason() => PackJobs.For(_pack.Id)?.Kind switch
    {
        null => null,
        PackJobKind.Export => "This instance is already being exported.",
        PackJobKind.Download => "This instance is still downloading. Export it once that has finished.",
        PackJobKind.Upload => "This instance is uploading. Export it once that has finished.",
        // Sync also covers the file browser's long operations, which borrow that kind.
        _ => "This instance is busy updating or moving files. Export it once that has finished."
    };

    private async Task RunExportAsync(string path, ModpackExportFormat format)
    {
        if (BusyReason() is { } busy) { Status(busy, danger: true); return; }

        var options = new ExportOptions(
            _pack,
            format,
            NameBox.Text.Trim(),
            VersionBox.Text.Trim(),
            format == ModpackExportFormat.CurseForge ? AuthorBox.Text.Trim() : null,
            format == ModpackExportFormat.Modrinth ? SummaryBox.Text.Trim() : null,
            // A copy: the export reads it on another thread while the card is still on screen.
            _selection.Clone(),
            DisabledBox.IsChecked == true,
            LocalBox.IsChecked == true,
            path);

        _acknowledgedGap = _warnedGap;
        var cts = new CancellationTokenSource();
        _exportCts = cts;
        var job = PackJobs.Start(_pack.Id, PackJobKind.Export, _pack.Name);
        // Stop pressed anywhere else the job shows up stops this too.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, job.Token);

        _gameRanDuringExport = _gameRunning();
        _savedPath = null;
        Status(null);
        WorkStepLabel.Text = "Starting...";
        WorkDetail.Text = "";
        WorkPercent.Text = "";
        WorkBar.IsIndeterminate = true;
        WorkTarget.Text = $"Saving {Path.GetFileName(path)} to {Path.GetDirectoryName(path)}";
        GoTo(Stage.Working);

        try
        {
            var result = await _exporter.ExportAsync(options, new Progress<ExportProgress>(OnExportProgress),
                linked.Token, ConfirmGapAsync);
            _gameRanDuringExport |= _gameRunning();
            ShowSaved(result, options);
        }
        catch (OperationCanceledException)
        {
            ShowEnded("\uE711", "TextSecondaryBrush", "Cancelled", "Export cancelled", "Nothing was saved.", null);
        }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(ExportPackDialog), ex);
            ShowEnded("\uE783", "DangerBrush", "Failed", "The export did not finish", Explain(ex), "Nothing was saved.");
        }
        finally
        {
            PackJobs.Finish(job);
            _exportCts = null;
            cts.Dispose();
            _gapAnswer = null;
            UpdateChrome();
        }
    }

    private void OnExportProgress(ExportProgress p)
    {
        // A Progress made on the UI thread already posts here; this covers one that was not.
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnExportProgress(p));
            return;
        }
        if (_stage != Stage.Working) return;
        _gameRanDuringExport |= _gameRunning();
        WorkStepLabel.Text = p.Step;
        WorkDetail.Text = p.Detail ?? "";
        if (p.Fraction < 0)
        {
            WorkBar.IsIndeterminate = true;
            WorkPercent.Text = "";
        }
        else
        {
            WorkBar.IsIndeterminate = false;
            WorkBar.Value = Math.Clamp(p.Fraction, 0, 1);
            WorkPercent.Text = $"{(int)(Math.Clamp(p.Fraction, 0, 1) * 100)}%";
        }
    }

    /// <summary>The export's question when the chosen store did not answer: pack the unchecked jars in,
    /// or stop. Asked before anything is written.</summary>
    private async Task<bool> ConfirmGapAsync(ExportIdentityGap gap, CancellationToken ct)
    {
        if (!Dispatcher.CheckAccess())
            return await Dispatcher.InvokeAsync(() => ConfirmGapAsync(gap, ct)).Task.Unwrap();

        // The options screen was already saying this when Export was pressed.
        if (_acknowledgedGap is { } seen && seen.Store == gap.Store && gap.Unchecked <= seen.Unchecked)
            return true;

        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _gapAnswer = answer;
        using var registration = ct.Register(() => answer.TrySetCanceled(ct));

        WorkStepLabel.Text = "Nothing has been written yet";
        GapTitle.Text = $"{gap.Store} did not answer";
        GapText.Text = $"{Plural(gap.Unchecked, "mod")} could not be checked with {gap.Store}, so there is no "
                     + $"telling whether {gap.Store} has {(gap.Unchecked == 1 ? "it" : "them")}. Export anyway and "
                     + $"{(gap.Unchecked == 1 ? "it goes" : "they go")} into the file as {(gap.Unchecked == 1 ? "it is" : "they are")}, "
                     + "or cancel and try again later.";
        GoTo(Stage.Asking);
        try
        {
            return await answer.Task;
        }
        finally
        {
            _gapAnswer = null;
            if (_stage == Stage.Asking) GoTo(Stage.Working);
        }
    }

    private void ShowSaved(ExportResult result, ExportOptions options)
    {
        _finishedOk = true;
        _endedLabel = "Done";
        _savedPath = result.Path;
        var store = ModpackExportService.StoreName(result.Format);
        var c = result.Mods;

        DoneGlyph.Text = "\uE73E";
        DoneGlyph.SetResourceReference(TextBlock.ForegroundProperty, "SuccessBrush");
        DoneTitle.Text = "Saved " + Path.GetFileName(result.Path);
        DoneTitle.ToolTip = result.Path;

        var mods = (c.Downloaded == 1 ? "1 mod downloads" : $"{c.Downloaded} mods download") + $" from {store}"
                 + ListedNotes(c, store)
                 + (c.PackedIn == 0 ? "." : $", and {Plural(c.PackedIn, "mod")} {(c.PackedIn == 1 ? "is" : "are")} packed in.");
        var files = result.OverrideFiles > 0
            ? $"{Plural(result.OverrideFiles, "other file")} came along from {NameList(options.Overrides.TopLevelWithAnything(), 4)}."
            : "Nothing else from the instance folder went in.";
        DoneBody.Text = $"{ContentBundleService.FormatSize(result.Bytes)}. {mods}{Environment.NewLine}{files}";

        var extra = new List<string>();
        if (c.Unchecked > 0)
            extra.Add($"{Plural(c.Unchecked, "mod")} could not be checked with {store} and went in as files. "
                    + $"Export again when {store} answers to list them instead.");
        if (result.BundledMods.Count > 0)
            extra.Add("Packed in: " + NameList(result.BundledMods, 5));
        if (result.ListedPacks is { Count: > 0 } packs)
            extra.Add($"{Plural(packs.Count, "resource or shader pack")} {(packs.Count == 1 ? "downloads" : "download")} "
                    + $"from {store} too: " + NameList(packs.Select(p => p[(p.IndexOf('/') + 1)..]).ToList(), 4));
        if (result.ListedByName is { Count: > 0 } byName)
            extra.Add($"Listed as {store}'s own build of the same file (the copy here was built for Modrinth): "
                    + NameList(byName, 4));
        if (result.CurseForgeAppOnly is { Count: > 0 } appOnly)
            extra.Add($"{NameList(appOnly, 4)} {(appOnly.Count == 1 ? "installs" : "install")} by itself only in the "
                    + "CurseForge app: the author turned off third-party downloads, so Prism and other launchers "
                    + "(this one too) ask for it to be downloaded by hand.");
        if (result.Format == ModpackExportFormat.CurseForge && c.Downloaded + (result.ListedPacks?.Count ?? 0) > 0)
            extra.Add("modlist.html in the zip lists what downloads from CurseForge, with links, as the CurseForge app's own exports do.");
        if (result.PrivateHeldBack > 0)
            extra.Add($"{Plural(result.PrivateHeldBack, "private file")} stayed on this PC.");
        if (result.Unreadable.Count > 0)
            extra.Add($"{Plural(result.Unreadable.Count, "file")} could not be read and "
                    + $"{(result.Unreadable.Count == 1 ? "was" : "were")} left out: {NameList(result.Unreadable, 3)}");
        if (_gameRanDuringExport)
            extra.Add("Minecraft was running during the export, so a config it saved at that moment may be caught "
                    + "half-written. Export again with the game closed to be sure.");

        DoneExtra.Text = string.Join(Environment.NewLine, extra);
        DoneExtra.Visibility = extra.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DoneExtra.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        DoneExtra.ToolTip = result.BundledMods.Count > 5 ? string.Join(Environment.NewLine, result.BundledMods) : null;
        ShowInFolderButton.Visibility = Visibility.Visible;
        GoTo(Stage.Finished);
    }

    private void ShowEnded(string glyph, string brushKey, string stageLabel, string title, string body, string? extra)
    {
        _finishedOk = false;
        _endedLabel = stageLabel;
        DoneGlyph.Text = glyph;
        DoneGlyph.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        DoneTitle.Text = title;
        DoneTitle.ToolTip = null;
        DoneBody.Text = body;
        DoneExtra.Text = extra ?? "";
        DoneExtra.Visibility = extra is null ? Visibility.Collapsed : Visibility.Visible;
        DoneExtra.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        DoneExtra.ToolTip = null;
        ShowInFolderButton.Visibility = Visibility.Collapsed;
        GoTo(Stage.Finished);
    }

    private void OnShowInFolder(object sender, RoutedEventArgs e)
    {
        if (_savedPath is not { } path) return;
        // Selects the file when it is there, otherwise opens the folder it was saved to.
        if (!SafeLaunch.RevealFile(path)) Status("Could not open Explorer.", danger: true);
    }

    // ── words ────────────────────────────────────────────────────────────────

    private static string Explain(Exception ex) => ex switch
    {
        // The service's own refusals and the file system's complaints already name the problem.
        InvalidOperationException or IOException or UnauthorizedAccessException => ex.Message,
        _ => ContentBundleService.Explain(ex, "Something went wrong. The details are in the launcher log.")
    };

    private static string Describe(PackDetail pack)
    {
        var text = pack.Name;
        if (!string.IsNullOrWhiteSpace(pack.MinecraftVersion)) text += $" · Minecraft {pack.MinecraftVersion}";
        if (pack.Loader != LoaderKind.None) text += $" · {pack.Loader} {pack.LoaderVersion}".TrimEnd();
        return text;
    }

    private static string FirstLine(string? text)
    {
        var line = (text ?? "").Trim().Split('\n')[0].TrimEnd('\r').Trim();
        return line.Length > 250 ? line[..250].TrimEnd() : line;
    }

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n:N0} {noun}s";

    private static string NameList(IReadOnlyList<string> names, int max) =>
        names.Count <= max
            ? string.Join(", ", names)
            : string.Join(", ", names.Take(max)) + $" and {names.Count - max} more";

    /// <summary>The characters Windows will not take in a file name, replaced, so the suggested name
    /// never makes the save dialog refuse its own default.</summary>
    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "modpack" : cleaned;
    }
}
