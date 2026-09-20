using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>
/// The Modpack planning sub-tab: a zoomable board of cards you arrange and connect.
///
/// Four kinds of card — a <b>mod</b> (one specific jar), a <b>group</b> (a live query like
/// "everything tagged Magic" or "priority P3", which re-evaluates itself every rebuild so the board
/// never goes stale), a <b>note</b> (free-text planning prose), and a <b>section</b> (a titled
/// backdrop that carries the cards sitting on it when dragged). Any two cards can be joined by a
/// labelled arrow, which is what makes the board a concept map rather than a list.
///
/// Everything lives in <c>game/.cloudlauncher/plans.json</c> via <see cref="ModPlanService"/>, so
/// boards sync with the pack exactly like mod flags do.
/// </summary>
public partial class ModPlanView : UserControl
{
    // Card geometry. Sizes are explicit (not measured) so edge endpoints can be computed before
    // layout runs — the same trick the graph view uses.
    private const double ModW = 212, ModH = 68, ModNoteH = 88;
    private const double GroupW = 244, GroupHeaderH = 50, GroupRowH = 20;
    private const int GroupMaxRows = 8;
    private const double NoteW = 250, NoteH = 156;
    // A note's progress bar and task list sit below its text at fixed heights, so its size is known
    // before layout like every other card's.
    private const double NoteProgressH = 26, NoteTasksHeaderH = 28, NoteTaskRowH = 22, NoteAddTaskRowH = 24, NoteTasksPad = 6;
    private const double NoteCornerW = 26;   // one top-right corner control plus its gap
    private const double NoteBodyLineH = 16; // one line of description: shorter than this under the title and only the title shows
    private const double SectionW = 552, SectionH = 216; // grid-aligned (23 × 24 wide, 9 × 24 tall)
    private const double MinSectionH = 130;

    // Inside a section, cards drop into fixed columns instead of free space — that's what keeps a
    // section looking deliberate without hand-aligning everything. One column is wide enough for
    // the widest card kind; the default section fits two.
    private const double SectionHeaderH = 30, SectionPad = 14;
    private const double SlotColW = 252, SlotGap = 16;
    private const double SlotPitch = SlotColW + SlotGap;

    /// <summary>Empty strip kept below the lowest card in a section, so an auto-fitted section always
    /// still has somewhere to drop the next card.</summary>

    /// <summary>Slightly translucent at rest so the grid reads faintly through it; fully opaque while
    /// a card is being dragged over it.</summary>
    private const double SectionRestOpacity = 0.93;

    /// <summary>Size of one square on the ruled backdrop. Cards snap to half a cell, so they line up
    /// with the visible grid instead of floating between it.</summary>
    private const double GridCell = 24;
    private const double Snap = GridCell / 2;

    private const string ModDragFormat = "CloudLauncher.PlanModKey";

    /// <summary>How near a card's outline (screen px, outside / inside it) an arrow end pins to that exact
    /// spot instead of attaching automatically.</summary>
    private const double OutlineOuterPx = 9, OutlineInnerPx = 5;

    /// <summary>Tags elements whose own press handling beats outline pinning (the link dot, resize grips).</summary>
    private static readonly object NoAnchorTag = new();

    private static readonly Brush EdgeBrush = Frozen(Color.FromArgb(170, 0x6B, 0x76, 0x88));
    private static readonly Brush ArrowBrush = Frozen(Color.FromArgb(230, 0x93, 0x9E, 0xB2));
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private Guid _packId;
    private IReadOnlyList<PackMod> _mods = Array.Empty<PackMod>();
    private MainWindow? _owner;
    private Action<PackMod>? _onOpenMod;
    private Action? _onModsChanged;
    private PlanBoard? _board;
    private bool _loaded;
    private bool _suppressBoardChange;

    private readonly Dictionary<string, FrameworkElement> _nodeEls = new();
    private readonly Dictionary<string, Brush> _baseBorder = new();
    private readonly Dictionary<string, Rect> _rect = new();
    private readonly Dictionary<string, Border> _sectionBoxes = new();
    private readonly Dictionary<string, TextBlock> _sectionCounts = new();
    private string? _hoverSectionId;
    private readonly List<UIElement> _edgeEls = new();
    private readonly DrawingBrush _grid = BuildGridBrush();
    /// <summary>Ids of the currently selected cards. Shift+click adds to or removes from this set;
    /// a plain click replaces it.</summary>
    private readonly HashSet<string> _selected = new();

    /// <summary>Set when a plain click lands on a card that is already part of a multi-selection: the
    /// selection is kept so the group can be dragged, and collapses to that one card only if the
    /// click turns out not to be a drag (Explorer behaviour).</summary>
    private bool _collapseSelectionOnUp;

    // rubber-band selection (shift + drag on empty canvas)
    private bool _marquee;
    private Point _marqueeStart;
    private Rectangle? _marqueeEl;
    private HashSet<string> _marqueeBase = new();

    // pan
    private bool _panning;
    private Point _panStart;
    private double _panOrigX, _panOrigY;

    // card drag
    private PlanNode? _dragNode;
    private FrameworkElement? _dragEl;
    private Point _dragStart;
    private double _dragOrigX, _dragOrigY;
    private bool _dragMoved;
    private UIElement? _dragCapture;
    private List<(PlanNode node, double x, double y)> _dragCarried = new();

    // resize
    private PlanNode? _resizeNode;
    private Point _resizeStart;
    private double _resizeOrigW, _resizeOrigH;

    // link drag
    private PlanNode? _linkFrom;
    private PlanAnchor? _linkFromAnchor;   // the outline point the drag began on; null when it began on the dot
    private bool _linkPressedInside;       // an outline press that never drags is just a click on that card
    private Path? _linkPreview;
    private Point _linkDownAt;

    // arrow tool: click the card an arrow starts from, then the card it points to
    private bool _arrowTool;        // toolbar toggle — stays on so a whole map can be connected in one go
    private PlanNode? _arrowFrom;   // start card picked, waiting for the target click
    private PlanAnchor? _arrowFromAnchor;
    private Path? _arrowPreview;

    // outline points: near a card's outline, an arrow end pins to the exact spot under the pointer
    private Ellipse? _anchorMarker;
    private bool _anchorHover;      // the pointer is on an outline, so pressing here draws a pinned arrow
    private bool _swallowArrowUp;   // the release of a press the arrow tool used mustn't click the card too

    // palette drag
    private Point _paletteDown;

    public ModPlanView()
    {
        InitializeComponent();
        GridBackdrop.Fill = _grid;
        Viewport.SizeChanged += (_, _) => SyncGrid();
        // Leaving the board mid-hover mustn't strand the outline marker or the crosshair.
        Viewport.MouseLeave += (_, _) =>
        {
            if (Mouse.Captured is not null) return; // a drag in progress still owns them
            HideAnchorMarker();
            if (_anchorHover) { _anchorHover = false; UpdateArrowCursor(); }
        };
    }

    /// <summary>Binds the board to a pack's live mod list. Safe to call repeatedly — later calls
    /// just re-resolve the cards against the refreshed inventory.</summary>
    public void Load(Guid packId, IReadOnlyList<PackMod> mods, MainWindow? owner,
        Action<PackMod>? onOpenMod = null, Action? onModsChanged = null)
    {
        _packId = packId;
        _mods = mods;
        _owner = owner;
        _onOpenMod = onOpenMod;
        _onModsChanged = onModsChanged;

        var first = !_loaded;
        _loaded = true;
        _board = App.State.ModPlans.CurrentBoard(packId);

        RefreshBoardList();
        RefreshPalette();
        if (first) RestoreViewport();
        Rebuild();
    }

    private ModPlanService Plans => App.State.ModPlans;
    private void Save() => Plans.Save(_packId);

    /// <summary>Persist a content change and record an undo checkpoint. Every board mutation goes
    /// through here; pan/zoom and derived auto-sizing use the raw <see cref="Save"/> so they don't
    /// clutter the undo stack.</summary>
    private void Commit()
    {
        Save();
        RecordHistory();
    }

    // ── undo / redo ─────────────────────────────────────────────────────────────
    // Snapshot history of the board's nodes + edges (JSON, so ids and edges survive intact). Pan,
    // zoom and section auto-height aren't part of it — they're view/derived state.

    private readonly List<string> _history = new();
    private int _histIndex = -1;
    private bool _restoring;
    private string? _historyBoardId;
    private const int HistoryLimit = 120;

    private static readonly System.Text.Json.JsonSerializerOptions HistoryJson = new();

    private sealed record BoardSnapshot(List<PlanNode> Nodes, List<PlanEdge> Edges);

    private void RecordHistory()
    {
        if (_board is null || _restoring) return;
        var json = System.Text.Json.JsonSerializer.Serialize(new BoardSnapshot(_board.Nodes, _board.Edges), HistoryJson);
        if (_histIndex >= 0 && _histIndex < _history.Count && _history[_histIndex] == json) return; // nothing changed

        // Adding a new state after some undos throws away the redo tail.
        if (_histIndex < _history.Count - 1)
            _history.RemoveRange(_histIndex + 1, _history.Count - 1 - _histIndex);
        _history.Add(json);
        if (_history.Count > HistoryLimit) _history.RemoveAt(0);
        _histIndex = _history.Count - 1;
    }

    /// <summary>Starts the undo history fresh at the board's current state (called when a board opens).</summary>
    private void ResetHistory()
    {
        _history.Clear();
        _histIndex = -1;
        RecordHistory();
    }

    private void Undo()
    {
        if (_histIndex <= 0) return;
        _histIndex--;
        RestoreSnapshot(_history[_histIndex]);
    }

    private void Redo()
    {
        if (_histIndex >= _history.Count - 1) return;
        _histIndex++;
        RestoreSnapshot(_history[_histIndex]);
    }

    private void RestoreSnapshot(string json)
    {
        if (_board is null) return;
        var snap = System.Text.Json.JsonSerializer.Deserialize<BoardSnapshot>(json, HistoryJson);
        if (snap is null) return;

        _restoring = true;
        _board.Nodes = snap.Nodes;
        _board.Edges = snap.Edges;
        _selected.RemoveWhere(id => _board.Node(id) is null);
        _restoring = false;

        Save();      // persist the restored state without recording a new checkpoint
        Rebuild();
        PlanStatus.Text = "";
    }

    // ── boards ──────────────────────────────────────────────────────────────────

    private void RefreshBoardList()
    {
        _suppressBoardChange = true;
        // Flat list of boards (already grouped-by-category in stored order). An ItemTemplate shows the
        // category as a small prefix so grouping is still visible without the ComboBox-grouping quirks
        // that hid the board rows.
        BoardBox.ItemsSource = Plans.Boards(_packId).ToList();
        BoardBox.SelectedItem = _board;
        _suppressBoardChange = false;
    }

    private void OnBoardChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressBoardChange || BoardBox.SelectedItem is not PlanBoard board || ReferenceEquals(board, _board)) return;
        SwitchToBoard(board);
    }

    /// <summary>Switches the active board, saving the current viewport and restoring the target's.</summary>
    private void SwitchToBoard(PlanBoard board)
    {
        if (ReferenceEquals(board, _board)) return;
        StashViewport();
        _board = board;
        Plans.SetCurrentBoard(_packId, board);
        _selected.Clear();
        RestoreViewport();
        Rebuild();
    }

    private async void OnManageBoards(object sender, RoutedEventArgs e)
    {
        if (_owner is null) return;
        StashViewport();
        var chosen = await ManageBoardsDialog.ShowAsync(_owner, _packId, _board);

        // The dialog edited boards/categories/order live; re-sync the switcher and the active board.
        var boards = Plans.Boards(_packId).ToList();
        var oldBoard = _board;
        var target = chosen is not null && boards.Contains(chosen) ? chosen
                   : oldBoard is not null && boards.Contains(oldBoard) ? oldBoard
                   : Plans.CurrentBoard(_packId);

        _board = target;
        Plans.SetCurrentBoard(_packId, target);
        RefreshBoardList();
        if (!ReferenceEquals(target, oldBoard))
        {
            _selected.Clear();
            RestoreViewport();
            Rebuild(); // active board changed — re-render the canvas
        }
    }

    // ── viewport persistence ────────────────────────────────────────────────────

    private void StashViewport()
    {
        if (_board is null) return;
        _board.PanX = PanT.X;
        _board.PanY = PanT.Y;
        _board.Zoom = ZoomT.ScaleX;
        Save(); // pan/zoom isn't undoable content — persist without an undo checkpoint
    }

    private void RestoreViewport()
    {
        if (_board is null) return;
        ZoomT.ScaleX = ZoomT.ScaleY = _board.Zoom > 0.05 ? _board.Zoom : 1;
        PanT.X = _board.PanX;
        PanT.Y = _board.PanY;
        SyncGrid();
    }

    // ── rebuild ─────────────────────────────────────────────────────────────────

    private void Rebuild()
    {
        if (!IsInitialized || _board is null) return;

        // Opening a different board starts a fresh undo history (cheap id check on every rebuild;
        // only does work when the board actually changed).
        if (_historyBoardId != _board.Id)
        {
            _historyBoardId = _board.Id;
            ResetHistory();
        }

        BoardCanvas.Children.Clear();
        _nodeEls.Clear();
        _baseBorder.Clear();
        _rect.Clear();
        _sectionBoxes.Clear();
        _sectionCounts.Clear();
        _hoverSectionId = null;
        _edgeEls.Clear();
        // Drop ids for cards that no longer exist, so a stale selection can't resurrect one.
        _selected.RemoveWhere(id => _board.Node(id) is null);

        // Sizes first: edges need every endpoint rect before anything is added to the tree.
        foreach (var n in _board.Nodes)
        {
            var (w, h) = SizeOf(n);
            _rect[n.Id] = new Rect(n.X, n.Y, w, h);
        }
        MigrateMembership();        // one-time: derive explicit SectionId from positions on legacy boards
        ApplySectionColumnWidths(); // fit section cards to even columns so they line up
        AutoSizeSections();

        foreach (var n in _board.Nodes.Where(n => n.Kind == PlanNodeKind.Section)) AddSection(n);
        foreach (var edge in _board.Edges.ToList()) AddEdge(edge);
        foreach (var n in _board.Nodes.Where(n => n.Kind != PlanNodeKind.Section)) AddCard(n);

        ApplySelectionVisual();
        RefreshSectionCounts();

        EmptyHint.Visibility = _board.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        PlanStatus.Text = _board.IsEmpty
            ? ""
            : $"{_board.Nodes.Count} card{(_board.Nodes.Count == 1 ? "" : "s")}" +
              (_board.Edges.Count > 0 ? $"  ·  {_board.Edges.Count} link{(_board.Edges.Count == 1 ? "" : "s")}" : "");

        // Clearing the canvas took a half-drawn arrow's dashed line with it: put it back, unless its start
        // card is gone (deleted, undone away, or on a board that's no longer showing).
        if (_arrowFrom is not null)
        {
            if (_board.Node(_arrowFrom.Id) is { } start)
            {
                _arrowFrom = start; // undo swaps in fresh node objects
                if (_arrowPreview is not null) BoardCanvas.Children.Add(_arrowPreview);
            }
            else CancelPendingArrow();
        }
        if (ArrowModeActive) PlanStatus.Text = ArrowPrompt;
    }

    private (double w, double h) SizeOf(PlanNode n)
    {
        switch (n.Kind)
        {
            case PlanNodeKind.Mod:
            {
                var mod = Plans.ResolveMod(_packId, n, _mods);
                return (n.W > 0 ? n.W : ModW, mod?.HasNote == true ? ModNoteH : ModH);
            }
            case PlanNodeKind.Group:
            {
                var count = ModPlanService.Resolve(n.Query, _mods).Count;
                // The trailing row is "+N more" when capped and "Show less" when expanded, so it's
                // present either way once the group is over the cap.
                var rows = n.Collapsed ? 0 : Math.Max(1, GroupRowsShown(n, count) + (count > GroupMaxRows ? 1 : 0));
                return (n.W > 0 ? n.W : GroupW, GroupHeaderH + rows * GroupRowH + (rows > 0 ? 10 : 4));
            }
            case PlanNodeKind.Note:
            {
                var w = n.W > 0 ? n.W : NoteW;
                return (w, NoteTextH(n, w) + NoteExtraH(n));
            }
            default:
                // Grid-align the width even for sections saved before this, so their right edge lands
                // on a grid line without needing a resize.
                var sw = Math.Ceiling((n.W > 0 ? n.W : SectionW) / GridCell) * GridCell;
                return (sw, n.H > 0 ? n.H : SectionH);
        }
    }

    /// <summary>How many member rows a group card lists: everything when expanded, otherwise capped.</summary>
    private static int GroupRowsShown(PlanNode node, int memberCount) =>
        node.Expanded ? memberCount : Math.Min(memberCount, GroupMaxRows);

    /// <summary>Height of a note's text area. Showing only its title (folded by its arrow, or resized too short
    /// for any description) it hugs the title; otherwise it's what it was resized to, else the default —
    /// except a note with no description but a checkbox, progress bar or task list, which also hugs its
    /// title rather than opening with a tall empty box above the things it tracks.</summary>
    private double NoteTextH(PlanNode n, double w)
    {
        if (NoteShowsTitleOnly(n, w)) return NoteTitleOnlyH(n, w);
        if (n.H > 0) return n.H;
        var tracked = n.Tracker != PlanNoteTracker.None || n.Tasks is { Count: > 0 };
        return tracked && n.Body.Length == 0 ? NoteTitleOnlyH(n, w) : NoteH;
    }

    /// <summary>A note with a description shows only its title when folded by its arrow, or when it has been
    /// resized too short to fit even one line of the description under the title.</summary>
    private bool NoteShowsTitleOnly(PlanNode n, double w) =>
        n.Body.Length > 0 && (n.Collapsed || (n.H > 0 && n.H < NoteTitleOnlyH(n, w) + NoteBodyLineH));

    /// <summary>Folded by its arrow — its height then follows the title, so a resize only changes the width.</summary>
    private static bool NoteFolded(PlanNode n) => n.Kind == PlanNodeKind.Note && n.Collapsed && n.Body.Length > 0;

    /// <summary>How many small controls sit in a note's top-right corner: the description arrow, the tick box.</summary>
    private static int NoteCornerControls(PlanNode n) =>
        (n.Body.Length > 0 ? 1 : 0) + (n.Tracker == PlanNoteTracker.Checkbox ? 1 : 0);

    /// <summary>What a note shows when only its title shows: the title, or the first line of the
    /// description when it has no title.</summary>
    private static string NoteHeadline(PlanNode n) =>
        n.Title.Length > 0 ? n.Title : n.Body.Split('\n', 2)[0].Trim();

    /// <summary>Height of a note's text area holding only its headline — measured with the card's insets (12
    /// each side, room for the corner controls, the border) and never shorter than those controls.</summary>
    private double NoteTitleOnlyH(PlanNode n, double w)
    {
        var corner = NoteCornerControls(n);
        var minH = corner > 0 ? 38.0 : 12;
        var headline = NoteHeadline(n);
        if (headline.Length == 0) return minH;

        var titled = n.Title.Length > 0;
        var text = new TextBlock
        {
            Text = headline, FontFamily = FontFamily, FontSize = titled ? 13.5 : 12,
            FontWeight = titled ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = titled ? TextWrapping.Wrap : TextWrapping.NoWrap
        };
        text.Measure(new Size(Math.Max(20, w - 3 - 24 - corner * NoteCornerW), double.PositiveInfinity));
        return Math.Max(minH, Math.Ceiling(text.DesiredSize.Height + 20));
    }

    /// <summary>Height a note's progress bar and task list add below its text area.</summary>
    private static double NoteExtraH(PlanNode n)
    {
        var h = n.Tracker == PlanNoteTracker.Progress ? NoteProgressH + 2 : 0; // + the bar row's bottom margin
        if (n.Tasks is { Count: > 0 } tasks)
            h += NoteTasksHeaderH + NoteTasksPad + (n.TasksCollapsed ? 0 : tasks.Count * NoteTaskRowH + NoteAddTaskRowH);
        return h;
    }

    private void Register(PlanNode node, FrameworkElement el, int z)
    {
        var r = _rect[node.Id];
        el.Width = r.Width;
        el.Height = r.Height;
        Canvas.SetLeft(el, r.X);
        Canvas.SetTop(el, r.Y);
        Panel.SetZIndex(el, z);
        BoardCanvas.Children.Add(el);
        _nodeEls[node.Id] = el;
    }

    // ── mod card ────────────────────────────────────────────────────────────────

    private void AddCard(PlanNode node)
    {
        var card = node.Kind switch
        {
            PlanNodeKind.Mod   => CreateModCard(node),
            PlanNodeKind.Group => CreateGroupCard(node),
            _                  => CreateNoteCard(node)
        };

        _baseBorder[node.Id] = card.BorderBrush;
        card.MouseLeftButtonDown += (_, e) =>
        {
            // Border is a FrameworkElement, not a Control, so there's no MouseDoubleClick event —
            // fold the second click in here before the drag would otherwise start.
            if (e.ClickCount == 2) { e.Handled = true; OnCardDoubleClick(node, card); return; }
            OnCardDown(node, card, e);
        };
        card.MouseMove += OnCardMove;
        card.MouseLeftButtonUp += OnCardUp;
        Register(node, card, 2);
    }

    /// <summary>Double-click does the obvious thing per card: edit a note, open a mod's note,
    /// fold a group.</summary>
    private void OnCardDoubleClick(PlanNode node, Border card)
    {
        switch (node.Kind)
        {
            case PlanNodeKind.Note:
                BeginEditNote(node, card);
                break;
            case PlanNodeKind.Group:
                node.Collapsed = !node.Collapsed;
                Commit();
                Rebuild();
                break;
            case PlanNodeKind.Mod when Plans.ResolveMod(_packId, node, _mods) is { } mod:
                ModNotePopup.Show(card, mod, _packId, App.State.ModInventory, ModsChanged);
                break;
        }
    }

    private Border CreateModCard(PlanNode node)
    {
        var mod = Plans.ResolveMod(_packId, node, _mods);
        if (mod is null) return CreateMissingCard(node);

        var emphasized = PriorityPalette.IsEmphasized(mod.Priority);
        var card = new Border
        {
            CornerRadius = new CornerRadius(9),
            Background = Res("Surface3Brush"),
            BorderBrush = emphasized ? PriorityPalette.BorderFor(mod.Priority) : Res("BorderStrongBrush"),
            BorderThickness = new Thickness(emphasized ? 2 : 1),
            Cursor = Cursors.SizeAll,
            Opacity = mod.Enabled ? 1.0 : 0.5,
            ToolTip = Tip(mod.TooltipText)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(7),
            Background = Res("Surface2Brush"), BorderBrush = Res("BorderBrush"), BorderThickness = new Thickness(1),
            Margin = new Thickness(9, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true
        };
        var iconGrid = new Grid();
        iconGrid.Children.Add(new TextBlock
        {
            Text = mod.Initial, FontWeight = FontWeights.SemiBold, FontSize = 12,
            Foreground = Res("TextSecondaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
        TryAddIcon(iconGrid, mod.IconUrl);
        icon.Child = iconGrid;
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };

        // Grid (not a StackPanel) so a long name trims instead of pushing the flag pills out past
        // the card's edge — the Border doesn't clip, so overflow would visibly spill.
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = mod.DisplayName, FontWeight = FontWeights.SemiBold, FontSize = 13,
            Foreground = Res("TextPrimaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(name, 0);
        titleRow.Children.Add(name);

        var pills = new StackPanel { Orientation = Orientation.Horizontal };
        pills.Children.Add(new ContentSizeMeter { Size = mod.ContentSize, Margin = new Thickness(7, 1, 0, 0) });
        foreach (var pill in FlagPills(mod)) pills.Children.Add(pill);
        Grid.SetColumn(pills, 1);
        titleRow.Children.Add(pills);
        text.Children.Add(titleRow);

        text.Children.Add(new TextBlock
        {
            Text = mod.MetaLine + (mod.Enabled ? "" : "   ·   off"),
            FontSize = 11, Foreground = Res("TextSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis
        });

        if (mod.HasNote)
        {
            var noteRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
            noteRow.Children.Add(new TextBlock
            {
                Text = char.ConvertFromUtf32(0xE70B), FontFamily = IconFont, FontSize = 10,
                Foreground = Res("AccentBrush"), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            });
            noteRow.Children.Add(new TextBlock
            {
                Text = mod.NotePreview, FontSize = 11, FontStyle = FontStyles.Italic,
                Foreground = Res("TextSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis
            });
            text.Children.Add(noteRow);
        }

        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        AddConnector(grid, card, node, 1);
        card.Child = grid;

        card.MouseRightButtonUp += (_, e) => { e.Handled = true; SelectForMenu(node); ShowModCardMenu(node, mod, card); };
        return card;
    }

    /// <summary>A mod card whose jar is no longer installed — kept (not silently dropped) so the plan
    /// survives a mod being temporarily removed, with a one-click way to clear it.</summary>
    private Border CreateMissingCard(PlanNode node)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(9),
            Background = Res("Surface2Brush"),
            BorderBrush = Res("BorderBrush"),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.SizeAll,
            Opacity = 0.75,
            ToolTip = Tip($"Not installed right now.\nKey: {node.ModKey}")
        };
        var grid = new Grid();
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
        stack.Children.Add(new TextBlock
        {
            Text = ShortKey(node.ModKey), FontWeight = FontWeights.SemiBold, FontSize = 13,
            Foreground = Res("TextSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis
        });
        stack.Children.Add(new TextBlock
        {
            Text = "not installed", FontSize = 11, Foreground = Res("TextTertiaryBrush")
        });
        grid.Children.Add(stack);
        AddConnector(grid, card, node);
        card.Child = grid;

        card.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            var menu = new ContextMenu { PlacementTarget = card };
            menu.Items.Add(Item("Draw arrow from here", () => BeginPendingArrow(node)));
            menu.Items.Add(Item("Remove card", () => RemoveNode(node)));
            menu.IsOpen = true;
        };
        return card;
    }

    private static string ShortKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "Unknown mod";
        var i = key.IndexOf(':');
        return i >= 0 && i < key.Length - 1 ? key[(i + 1)..] : key;
    }

    private IEnumerable<Border> FlagPills(PackMod mod)
    {
        var flags = new List<string>();
        if (mod.IsLibrary) flags.Add("LIB");
        if (mod.IsTesting) flags.Add("TEST");
        if (mod.Side == ModSide.Client) flags.Add("CLIENT");
        else if (mod.Side == ModSide.Server) flags.Add("SERVER");
        // No update marker here on purpose — the board is for planning, not for chasing updates.
        // Updates live in the List view.

        foreach (var f in flags.Take(2))
            yield return new Border
            {
                Style = (Style)FindResource("Pill"),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = f, Style = (Style)FindResource("PillText") }
            };
    }

    // ── group card ──────────────────────────────────────────────────────────────

    private Border CreateGroupCard(PlanNode node)
    {
        var members = ModPlanService.Resolve(node.Query, _mods);
        // An explicit card colour wins; otherwise a category card inherits that category's colour
        // from the Categories page, so the same category reads the same everywhere.
        var accent = node.Color is null && node.Query is { Property: PlanGroupProperty.Category } q
            ? AccentPalette.Brush(App.State.ModMetadata.CategoryColor(_packId, q.Value), Res("AccentBrush"))
            : ColorOf(node, Res("AccentBrush"));

        var card = new Border
        {
            CornerRadius = new CornerRadius(9),
            Background = Res("Surface2Brush"),
            BorderBrush = accent,
            BorderThickness = new Thickness(1.4),
            Cursor = Cursors.SizeAll,
            ToolTip = Tip(members.Count == 0
                ? "Nothing matches this yet — it fills in as you tag mods."
                : string.Join("\n", members.Select(m => m.DisplayName)))
        };

        var root = new Grid();
        var stack = new StackPanel();

        // header
        var header = new Grid { Margin = new Thickness(11, 9, 8, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titles = new StackPanel();
        titles.Children.Add(new TextBlock
        {
            Text = node.Title.Length > 0 ? node.Title : node.Query?.Title ?? "Group",
            FontWeight = FontWeights.SemiBold, FontSize = 13,
            Foreground = Res("TextPrimaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis
        });
        titles.Children.Add(new TextBlock
        {
            Text = $"{node.Query?.PropertyLabel ?? "Group"}  ·  {members.Count} mod{(members.Count == 1 ? "" : "s")}"
                   + ContentSummary(members),
            FontSize = 10, Foreground = Res("TextTertiaryBrush")
        });
        Grid.SetColumn(titles, 0);
        header.Children.Add(titles);

        var chevron = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Content = char.ConvertFromUtf32(node.Collapsed ? 0xE70D : 0xE70E), // chevron down / up
            FontFamily = IconFont, FontSize = 10, MinWidth = 22, MinHeight = 22, Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top,
            ToolTip = node.Collapsed ? "Show members" : "Hide members"
        };
        chevron.Click += (_, _) => { node.Collapsed = !node.Collapsed; Commit(); Rebuild(); };
        Grid.SetColumn(chevron, 1);
        header.Children.Add(chevron);
        stack.Children.Add(header);

        // accent rule under the header
        stack.Children.Add(new Border
        {
            Height = 2, Margin = new Thickness(11, 7, 11, 0), CornerRadius = new CornerRadius(1),
            Background = accent, Opacity = 0.7
        });

        // members
        if (!node.Collapsed)
        {
            var list = new StackPanel { Margin = new Thickness(9, 5, 9, 0) };
            if (members.Count == 0)
            {
                list.Children.Add(new TextBlock
                {
                    Text = "nothing matches yet", FontSize = 11, FontStyle = FontStyles.Italic,
                    Foreground = Res("TextTertiaryBrush"), Height = GroupRowH, Margin = new Thickness(2, 0, 0, 0)
                });
            }
            else
            {
                var shown = GroupRowsShown(node, members.Count);
                foreach (var m in members.Take(shown)) list.Children.Add(MemberRow(m));
                if (members.Count > GroupMaxRows)
                    list.Children.Add(ExpandToggleRow(node, members.Count - shown));
            }
            stack.Children.Add(list);
        }

        root.Children.Add(stack);
        AddConnector(root, card, node);
        card.Child = root;

        card.MouseRightButtonUp += (_, e) => { e.Handled = true; SelectForMenu(node); ShowGroupCardMenu(node, members, card); };
        return card;
    }

    /// <summary>"· 2L 3M 1S" — how much content a group actually carries, not just how many mods.
    /// A category of twelve tiny tweaks and a category of three pack-defining mods look identical by
    /// count; this is what makes them read differently while planning. Empty when nobody's sized.</summary>
    private static string ContentSummary(IReadOnlyList<PackMod> members)
    {
        var large = members.Count(m => m.ContentSize == 3);
        var medium = members.Count(m => m.ContentSize == 2);
        var small = members.Count(m => m.ContentSize == 1);
        if (large + medium + small == 0) return "";

        var parts = new List<string>(3);
        if (large > 0) parts.Add($"{large}L");
        if (medium > 0) parts.Add($"{medium}M");
        if (small > 0) parts.Add($"{small}S");
        return "  ·  " + string.Join(" ", parts);
    }

    /// <summary>The "+N more" / "Show less" row at the foot of a capped group card. A Button rather
    /// than a TextBlock so the click can't fall through and start dragging the card, and so it gets
    /// the theme's focus and hover handling for free.</summary>
    private Button ExpandToggleRow(PlanNode node, int hidden)
    {
        // Glyph + label as separate runs: the glyph needs the icon font, the label doesn't. Neither
        // sets Foreground, so both inherit the link colour from the template — including its hover state.
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock
        {
            Text = char.ConvertFromUtf32(node.Expanded ? 0xE70E : 0xE70D),  // chevron up / down
            FontFamily = IconFont,
            FontSize = 9,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 1, 6, 0)
        });
        content.Children.Add(new TextBlock
        {
            Text = node.Expanded ? "Show less" : $"{hidden} more",
            VerticalAlignment = VerticalAlignment.Center
        });

        var button = new Button
        {
            Style = (Style)FindResource("LinkButton"),
            Content = content,
            FontSize = 11,
            Height = GroupRowH,
            MinHeight = 0,
            MinWidth = 0,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(2, 0, 0, 0),
            Cursor = Cursors.Hand,
            ToolTip = node.Expanded ? "Collapse back to the first few" : "Show every mod in this group"
        };
        button.Click += (_, _) =>
        {
            node.Expanded = !node.Expanded;
            Commit();
            Rebuild();
        };
        return button;
    }

    private FrameworkElement MemberRow(PackMod mod)
    {
        var row = new Grid { Height = GroupRowH, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Ellipse
        {
            Width = 6, Height = 6, Margin = new Thickness(2, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = PriorityPalette.IsEmphasized(mod.Priority)
                ? PriorityPalette.BorderFor(mod.Priority)
                : Res("TextTertiaryBrush")
        };
        Grid.SetColumn(dot, 0);
        row.Children.Add(dot);

        var name = new TextBlock
        {
            Text = mod.DisplayName, FontSize = 11.5,
            Foreground = mod.Enabled ? Res("TextSecondaryBrush") : Res("TextTertiaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(name, 1);
        row.Children.Add(name);

        // Trailing markers share one column so the meter and the note glyph can't land on top of
        // each other.
        var markers = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        markers.Children.Add(new ContentSizeMeter { Size = mod.ContentSize, Margin = new Thickness(6, 0, 0, 0) });
        if (mod.HasNote)
            markers.Children.Add(new TextBlock
            {
                Text = char.ConvertFromUtf32(0xE70B), FontFamily = IconFont, FontSize = 9,
                Foreground = Res("AccentBrush"), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 2, 0)
            });
        Grid.SetColumn(markers, 2);
        row.Children.Add(markers);

        row.ToolTip = Tip(mod.TooltipText);
        // Members are interactive: the group card doubles as a working list, not just a label.
        // Deliberately NOT marking the up-click handled — it has to keep bubbling to OnCardUp or the
        // card's drag would never be released. A click that moved the card opens nothing.
        row.MouseLeftButtonUp += (_, _) => { if (!_dragMoved) _onOpenMod?.Invoke(mod); };
        row.MouseRightButtonUp += (_, e) => { e.Handled = true; ShowModOptions(mod, row); };
        return row;
    }

    // ── note + section cards ────────────────────────────────────────────────────

    private Border CreateNoteCard(PlanNode node)
    {
        var accent = ColorOf(node, Res("BorderStrongBrush"));
        var card = new Border
        {
            CornerRadius = new CornerRadius(9),
            Background = Res("Surface2Brush"),
            BorderBrush = accent,
            BorderThickness = new Thickness(1.4),
            Cursor = Cursors.SizeAll,
            ToolTip = Tip("Double-click to edit  ·  right-click to add a checkbox, progress bar or tasks")
        };

        var w = _rect.TryGetValue(node.Id, out var rect) ? rect.Width : NoteW;
        var titleOnly = NoteShowsTitleOnly(node, w);
        var hasBody = node.Body.Length > 0;
        var checkbox = node.Tracker == PlanNoteTracker.Checkbox;
        var ticked = checkbox && node.Done;
        var hasTasks = node.Tasks is { Count: > 0 };
        var cornerRoom = NoteCornerControls(node) * NoteCornerW; // kept clear at the right of the first line

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // text
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // progress bar
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // task list

        var stack = new StackPanel { Margin = new Thickness(12, 10, 12, 10) };
        var headline = titleOnly ? NoteHeadline(node) : node.Title;
        if (headline.Length > 0)
        {
            var titled = node.Title.Length > 0;
            stack.Children.Add(new TextBlock
            {
                Text = headline, FontSize = titled ? 13.5 : 12,
                FontWeight = titled ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = ticked || !titled ? Res("TextSecondaryBrush") : Res("TextPrimaryBrush"),
                TextDecorations = ticked ? TextDecorations.Strikethrough : null,
                TextWrapping = titled ? TextWrapping.Wrap : TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, cornerRoom, titleOnly ? 0 : 5)
            });
        }
        // The description, unless only the title shows. With a tracker or task list an empty one is simply
        // left out — the "write something" prompt would only be clutter there.
        if (!titleOnly && (hasBody || (!hasTasks && node.Tracker == PlanNoteTracker.None)))
            stack.Children.Add(new TextBlock
            {
                Text = hasBody ? node.Body : "Double-click to write…",
                FontSize = 12,
                Foreground = hasBody ? Res("TextSecondaryBrush") : Res("TextTertiaryBrush"),
                FontStyle = hasBody ? FontStyles.Normal : FontStyles.Italic,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, node.Title.Length > 0 ? 0 : cornerRoom, 0) // untitled: clear the corner
            });

        // Deliberately NOT a ScrollViewer: it focuses itself on mouse-down and marks the event
        // handled, which swallowed the drag and left note cards unmovable. A clipping, non-hit-test
        // Border keeps the text inside the card and lets every click through to the drag handler —
        // notes are resizable, so overflow is fixed by pulling the corner rather than scrolling.
        root.Children.Add(new Border { ClipToBounds = true, IsHitTestVisible = false, Child = stack });

        if (node.Tracker == PlanNoteTracker.Progress)
        {
            var bar = NoteProgressRow(node);
            Grid.SetRow(bar, 1);
            root.Children.Add(bar);
        }
        if (hasTasks)
        {
            var tasks = NoteTasksSection(node);
            Grid.SetRow(tasks, 2);
            root.Children.Add(tasks);
        }

        // Top-right corner: the arrow that folds the description away (only when there is one), then the tick box.
        if (hasBody || checkbox)
        {
            var corner = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 7, 8, 0)
            };
            if (hasBody) corner.Children.Add(NoteFoldButton(node, titleOnly));
            if (checkbox)
            {
                var box = TickBox(node.Done, 16, () => { node.Done = !node.Done; Commit(); Rebuild(); },
                    node.Done ? "Done — click to untick" : "Tick when this is done");
                box.Margin = new Thickness(5, 3, 1, 0);
                corner.Children.Add(box);
            }
            Grid.SetRowSpan(corner, 3);
            Panel.SetZIndex(corner, 4);
            root.Children.Add(corner);
        }

        AddConnector(root, card, node);
        AddResizeGrip(root, node);
        card.Child = root;

        card.MouseRightButtonUp += (_, e) => { e.Handled = true; SelectForMenu(node); ShowPlainCardMenu(node, card, "note"); };
        return card;
    }

    /// <summary>The arrow in a note's corner that folds its description away, leaving only the title — and
    /// unfolds it again, undoing a resize that had squeezed the description out as well.</summary>
    private Button NoteFoldButton(PlanNode node, bool folded)
    {
        var button = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Content = char.ConvertFromUtf32(folded ? 0xE70D : 0xE70E), // chevron down / up, as on group cards
            FontFamily = IconFont, FontSize = 10, MinWidth = 22, MinHeight = 22, Width = 22, Height = 22,
            Padding = new Thickness(0),
            ToolTip = folded ? "Show the description" : "Hide the description"
        };
        button.Click += (_, _) =>
        {
            if (folded)
            {
                node.Collapsed = false;
                // Still squeezed down to its title by a resize? Back to the default height so the text shows.
                if (_rect.TryGetValue(node.Id, out var r) && NoteShowsTitleOnly(node, r.Width)) node.H = 0;
            }
            else node.Collapsed = true;
            Commit();
            Rebuild();
        };
        return button;
    }

    // ── note trackers + tasks ───────────────────────────────────────────────────

    /// <summary>A small tick box drawn to match the board. It takes its own press, so ticking never drags the
    /// card, and toggles on release.</summary>
    private Border TickBox(bool done, double size, Action toggle, string tip)
    {
        var box = new Border
        {
            Width = size, Height = size,
            CornerRadius = new CornerRadius(size / 4),
            Background = done ? Res("AccentBrush") : Res("Surface3Brush"),
            BorderBrush = done ? Res("AccentBrush") : Res("BorderStrongBrush"),
            BorderThickness = new Thickness(1.2),
            Cursor = Cursors.Hand,
            Tag = NoAnchorTag,
            ToolTip = new ToolTip { Content = tip },
            Child = done
                ? new TextBlock
                {
                    Text = char.ConvertFromUtf32(0xE73E), FontFamily = IconFont, FontSize = size * 0.6, // check mark
                    Foreground = Res("TextOnAccentBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
                : null
        };
        if (!done)
        {
            box.MouseEnter += (_, _) => box.BorderBrush = Res("AccentBrush");
            box.MouseLeave += (_, _) => box.BorderBrush = Res("BorderStrongBrush");
        }
        box.MouseLeftButtonDown += (_, e) => e.Handled = true;
        box.MouseLeftButtonUp += (_, e) => { e.Handled = true; toggle(); };
        return box;
    }

    /// <summary>A note's progress bar: the percentage, then a track you click or drag along to set it in 5%
    /// steps (the right-click menu has presets). Only the track takes presses; the label still drags the card.</summary>
    private FrameworkElement NoteProgressRow(PlanNode node)
    {
        var label = new TextBlock
        {
            Width = 38, FontSize = 11, FontWeight = FontWeights.SemiBold,
            Foreground = Res("TextSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center
        };
        var filled = new ColumnDefinition();
        var rest = new ColumnDefinition();
        var track = new Grid { Height = 7, VerticalAlignment = VerticalAlignment.Center };
        track.ColumnDefinitions.Add(filled);
        track.ColumnDefinitions.Add(rest);
        var groove = new Border
        {
            CornerRadius = new CornerRadius(3.5), Background = Res("Surface3Brush"),
            BorderBrush = Res("BorderBrush"), BorderThickness = new Thickness(1)
        };
        Grid.SetColumnSpan(groove, 2);
        track.Children.Add(groove);
        var fill = new Border { CornerRadius = new CornerRadius(3.5), Background = ColorOf(node, Res("AccentBrush")) };
        track.Children.Add(fill);

        void Show(int pct)
        {
            // Star widths split the track without needing its measured width.
            filled.Width = new GridLength(Math.Max(pct, 0.001), GridUnitType.Star);
            rest.Width = new GridLength(Math.Max(100 - pct, 0.001), GridUnitType.Star);
            fill.Visibility = pct > 0 ? Visibility.Visible : Visibility.Collapsed;
            label.Text = $"{pct}%";
        }
        Show(Math.Clamp(node.Progress, 0, 100));

        var hit = new Grid
        {
            Background = Brushes.Transparent, Cursor = Cursors.Hand, Tag = NoAnchorTag,
            ToolTip = new ToolTip { Content = "Click or drag to set the progress" }
        };
        hit.Children.Add(track);
        int ValueAt(MouseEventArgs e) =>
            (int)Math.Round(Math.Clamp(e.GetPosition(track).X / Math.Max(1, track.ActualWidth), 0, 1) * 20) * 5;

        var dragging = false;
        hit.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            dragging = hit.CaptureMouse();
            node.Progress = ValueAt(e);
            Show(node.Progress);
        };
        hit.MouseMove += (_, e) =>
        {
            if (!dragging) return;
            node.Progress = ValueAt(e);
            Show(node.Progress);
        };
        hit.MouseLeftButtonUp += (_, e) =>
        {
            if (!dragging) return;
            e.Handled = true;
            dragging = false; // before the release, so LostMouseCapture doesn't commit it twice
            hit.ReleaseMouseCapture();
            Commit();         // one undo step for the whole drag
        };
        hit.LostMouseCapture += (_, _) => { if (dragging) { dragging = false; Commit(); } };

        var row = new Grid { Height = NoteProgressH, Margin = new Thickness(12, 0, 26, 2) }; // right: clear of the grip
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(label);
        Grid.SetColumn(hit, 1);
        row.Children.Add(hit);
        return row;
    }

    /// <summary>A note's task list: a "Tasks n/m" heading whose arrow folds the list away, a row per task (tick box
    /// + text), and an "Add task" row at the foot.</summary>
    private FrameworkElement NoteTasksSection(PlanNode node)
    {
        var tasks = node.Tasks ?? new List<PlanTask>();
        var done = tasks.Count(t => t.Done);
        var panel = new StackPanel { Margin = new Thickness(8, 0, 8, NoteTasksPad) };

        var heading = new StackPanel
        {
            Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0)
        };
        heading.Children.Add(new TextBlock
        {
            Text = char.ConvertFromUtf32(node.TasksCollapsed ? 0xE76C : 0xE70D), // chevron right (folded) / down
            FontFamily = IconFont, FontSize = 9, Foreground = Res("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 7, 0)
        });
        heading.Children.Add(new TextBlock
        {
            Text = "Tasks", FontSize = 11.5, FontWeight = FontWeights.SemiBold,
            Foreground = Res("TextPrimaryBrush"), VerticalAlignment = VerticalAlignment.Center
        });
        heading.Children.Add(new TextBlock
        {
            Text = $"   {done}/{tasks.Count}", FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Foreground = done == tasks.Count ? Res("AccentBrush") : Res("TextTertiaryBrush")
        });

        // The rule on top separates the list from the note's text; the whole strip toggles the fold.
        var header = new Border
        {
            Height = NoteTasksHeaderH, Background = Brushes.Transparent, Cursor = Cursors.Hand, Tag = NoAnchorTag,
            BorderBrush = Res("BorderBrush"), BorderThickness = new Thickness(0, 1, 0, 0),
            ToolTip = new ToolTip { Content = node.TasksCollapsed ? "Show the tasks" : "Hide the tasks" },
            Child = heading
        };
        header.MouseLeftButtonDown += (_, e) => e.Handled = true;
        header.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            node.TasksCollapsed = !node.TasksCollapsed;
            Commit();
            Rebuild();
        };
        panel.Children.Add(header);

        if (!node.TasksCollapsed)
        {
            foreach (var task in tasks) panel.Children.Add(TaskRow(node, task));
            panel.Children.Add(AddTaskRow(node));
        }
        return panel;
    }

    private FrameworkElement TaskRow(PlanNode node, PlanTask task)
    {
        var row = new Grid
        {
            Height = NoteTaskRowH, Background = Brushes.Transparent,
            ToolTip = new ToolTip { Content = "Double-click to rename  ·  right-click for more" }
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var box = TickBox(task.Done, 14, () => { task.Done = !task.Done; Commit(); Rebuild(); },
            task.Done ? "Untick" : "Tick off");
        box.Margin = new Thickness(4, 0, 8, 0);
        row.Children.Add(box);

        var text = new TextBlock
        {
            Text = task.Text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = task.Done ? Res("TextTertiaryBrush") : Res("TextSecondaryBrush"),
            TextDecorations = task.Done ? TextDecorations.Strikethrough : null
        };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        // A single press still drags the card; only a double-click belongs to the row.
        row.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 2) return;
            e.Handled = true;
            _ = RenameTaskAsync(task);
        };
        row.MouseRightButtonUp += (_, e) => { e.Handled = true; ShowTaskMenu(node, task, row); };
        return row;
    }

    private Button AddTaskRow(PlanNode node)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock
        {
            Text = char.ConvertFromUtf32(0xE710), FontFamily = IconFont, FontSize = 9, // plus
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 7, 0)
        });
        content.Children.Add(new TextBlock { Text = "Add task", VerticalAlignment = VerticalAlignment.Center });

        var button = new Button
        {
            Style = (Style)FindResource("LinkButton"), Content = content, FontSize = 11,
            Height = NoteAddTaskRowH, MinHeight = 0, MinWidth = 0, Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(6, 0, 0, 0), Cursor = Cursors.Hand
        };
        button.Click += (_, _) => _ = AddTaskAsync(node);
        return button;
    }

    /// <summary>Prompts for a task and adds it to a note — at the end, or straight after <paramref name="after"/> —
    /// unfolding the list so the new task shows.</summary>
    private async Task AddTaskAsync(PlanNode node, PlanTask? after = null)
    {
        var text = await PromptAsync("Add task", "Task", "");
        if (string.IsNullOrWhiteSpace(text)) return;
        var tasks = node.Tasks ??= new List<PlanTask>();
        var task = new PlanTask { Text = text.Trim() };
        var at = after is null ? -1 : tasks.IndexOf(after);
        if (at < 0) tasks.Add(task);
        else tasks.Insert(at + 1, task);
        node.TasksCollapsed = false;
        Commit();
        Rebuild();
    }

    private async Task RenameTaskAsync(PlanTask task)
    {
        var text = await PromptAsync("Rename task", "Task", task.Text);
        if (string.IsNullOrWhiteSpace(text)) return;
        task.Text = text.Trim();
        Commit();
        Rebuild();
    }

    private void MoveTask(PlanNode node, PlanTask task, int by)
    {
        if (node.Tasks is not { } tasks) return;
        int i = tasks.IndexOf(task), j = i + by;
        if (i < 0 || j < 0 || j >= tasks.Count) return;
        (tasks[i], tasks[j]) = (tasks[j], tasks[i]);
        Commit();
        Rebuild();
    }

    private void ShowTaskMenu(PlanNode node, PlanTask task, FrameworkElement anchor)
    {
        if (node.Tasks is not { } tasks) return;
        var i = tasks.IndexOf(task);
        var menu = new ContextMenu { PlacementTarget = anchor };
        menu.Items.Add(Item(task.Done ? "Untick" : "Tick off", () => { task.Done = !task.Done; Commit(); Rebuild(); }));
        menu.Items.Add(Item("Rename…", () => _ = RenameTaskAsync(task)));
        menu.Items.Add(Item("Add task below…", () => _ = AddTaskAsync(node, task)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Move up", () => MoveTask(node, task, -1), enabled: i > 0));
        menu.Items.Add(Item("Move down", () => MoveTask(node, task, 1), enabled: i < tasks.Count - 1));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Delete task", () =>
        {
            tasks.Remove(task);
            if (tasks.Count == 0) node.Tasks = null; // the heading goes with the last task
            Commit();
            Rebuild();
        }));
        menu.IsOpen = true;
    }

    /// <summary>The note menu's tracking block: one checkbox or one progress bar (adding one replaces the other),
    /// ticking or presets for whichever is shown, and tasks for the collapsible list.</summary>
    private void AddNoteTrackingItems(ItemCollection items, PlanNode node)
    {
        void Apply(Action change) { change(); Commit(); Rebuild(); }

        items.Add(new Separator());
        if (node.Tracker == PlanNoteTracker.Checkbox)
        {
            items.Add(Item(node.Done ? "Untick" : "Tick off", () => Apply(() => node.Done = !node.Done)));
            items.Add(Item("Remove checkbox", () => Apply(() => node.Tracker = PlanNoteTracker.None)));
        }
        else items.Add(Item("Add checkbox", () => Apply(() => node.Tracker = PlanNoteTracker.Checkbox)));

        if (node.Tracker == PlanNoteTracker.Progress)
        {
            var presets = new MenuItem { Header = "Set progress" };
            foreach (var pct in new[] { 0, 25, 50, 75, 100 })
                presets.Items.Add(Radio($"{pct}%", node.Progress == pct, () => Apply(() => node.Progress = pct)));
            items.Add(presets);
            items.Add(Item("Remove progress bar", () => Apply(() => node.Tracker = PlanNoteTracker.None)));
        }
        else items.Add(Item("Add progress bar", () => Apply(() => node.Tracker = PlanNoteTracker.Progress)));

        items.Add(Item("Add task…", () => _ = AddTaskAsync(node)));
        if (node.Tasks is { Count: > 0 })
            items.Add(Item(node.TasksCollapsed ? "Show tasks" : "Hide tasks",
                () => Apply(() => node.TasksCollapsed = !node.TasksCollapsed)));
        items.Add(new Separator());
    }

    private void AddSection(PlanNode node)
    {
        var accent = ColorOf(node, Res("BorderStrongBrush"));
        var box = new Border
        {
            CornerRadius = new CornerRadius(12),
            // Darker than the canvas (Surface0 vs Surface1) so a section reads as a recessed well
            // you drop things into, not another card floating on top.
            Background = Res("Surface0Brush"),
            BorderBrush = accent,
            BorderThickness = new Thickness(1.4),
            Opacity = SectionRestOpacity
        };

        var root = new Grid();
        root.Children.Add(box);

        // Membership is positional — a card belongs to the section it's sitting on, no explicit
        // "bind" step. The header's live count is what makes that legible.
        var titleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 14, 0)
        };
        titleRow.Children.Add(new TextBlock
        {
            Text = node.Title.Length > 0 ? node.Title : "Section",
            FontWeight = FontWeights.SemiBold, FontSize = 12.5, Foreground = accent,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var countLabel = new TextBlock
        {
            FontSize = 10.5, Foreground = Res("TextTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 1, 0, 0)
        };
        titleRow.Children.Add(countLabel);
        _sectionCounts[node.Id] = countLabel;

        // Only the header strip is grabbable, so clicking inside the section still reaches the
        // cards sitting on it.
        var header = new Border
        {
            Height = SectionHeaderH, VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent, Cursor = Cursors.SizeAll,
            ToolTip = new ToolTip { Content = "Drag to move the section and every card sitting on it" },
            Child = titleRow
        };
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { e.Handled = true; _ = RenameNodeAsync(node, "Section title"); return; }
            OnCardDown(node, _nodeEls[node.Id], e);
        };
        header.MouseMove += OnCardMove;
        header.MouseLeftButtonUp += OnCardUp;
        header.MouseRightButtonUp += (_, e) => { e.Handled = true; SelectForMenu(node); ShowPlainCardMenu(node, header, "section"); };
        root.Children.Add(header);

        // A section is an arrow end by its title strip (its body belongs to its cards), so the dot sits there.
        AddConnector(root, root, node, centreY: SectionHeaderH / 2);
        AddResizeGrip(root, node);

        _baseBorder[node.Id] = box.BorderBrush;
        _sectionBoxes[node.Id] = box;
        Register(node, root, -2);
    }

    // ── section membership (positional) ─────────────────────────────────────────

    /// <summary>Live "3 cards" counter in each section header, so it's obvious a card dropped inside
    /// now belongs to it. Recomputed after every rebuild and every card drop.</summary>
    private void RefreshSectionCounts()
    {
        foreach (var (id, label) in _sectionCounts)
        {
            var section = _board?.Node(id);
            if (section is null) continue;
            var n = ContainedNodes(section).Count;
            label.Text = n == 0 ? "· drop cards here" : $"· {n} card{(n == 1 ? "" : "s")}";
        }
    }

    /// <summary>
    /// Sizes every section's height to wrap the cards sitting on it, with the same padding below the
    /// lowest card as on the sides. Never goes below the default height. Height is fully automatic —
    /// the resize grip on a section only sets its width — so a section can't be left too small for
    /// its own contents. Dropping a card past the bottom edge just grows the section to fit.
    /// </summary>
    private void AutoSizeSections()
    {
        if (_board is null) return;

        var changed = false;
        // Two passes: growing one section can pull in a card that sits just past its old edge, which
        // may in turn need a little more height.
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var section in _board.Nodes.Where(n => n.Kind == PlanNodeKind.Section))
            {
                if (!_rect.TryGetValue(section.Id, out var sr)) continue;

                var cards = ContainedNodes(section).Select(n => _rect[n.Id]).ToList();
                // An empty section keeps a comfortable drop area; a filled one hugs its lowest card
                // with exactly the side padding below it. No snap-rounding here — that only ever
                // rounded the gap up, which is what left the bottom looking slightly deeper than the sides.
                var wanted = cards.Count == 0
                    ? Math.Max(sr.Height, SectionH)
                    : Math.Max(MinSectionH, cards.Max(r => r.Bottom) + SectionPad - sr.Y); // bottom = side padding

                if (Math.Abs(wanted - sr.Height) < 0.5) continue;
                section.H = wanted;
                _rect[section.Id] = new Rect(sr.X, sr.Y, sr.Width, wanted);
                changed = true;
            }
        }

        if (changed) Save(); // derived layout during Rebuild — not a separate undo step
    }

    /// <summary>Packs a section's cards into its columns, each card going to whichever column is
    /// currently shortest — a masonry fill, so mixed card heights leave no ragged gaps. Grows the
    /// section if the result would overflow, so nothing ends up half outside and silently unbound.</summary>
    private void ArrangeSection(PlanNode section)
    {
        if (_board is null || !_rect.TryGetValue(section.Id, out var sr)) return;

        var cards = ContainedNodes(section)
            .OrderBy(n => Math.Round(_rect[n.Id].Y / 48))   // band into rough rows first…
            .ThenBy(n => _rect[n.Id].X)                     // …then left-to-right within a row
            .ToList();
        if (cards.Count == 0) { PlanStatus.Text = "That section has no cards in it yet."; return; }

        var x0 = ContentLeft(sr);
        var y0 = ContentTop(sr);
        var step = ColStep(sr);
        var columnBottom = Enumerable.Repeat(y0, ColumnCount(sr)).ToArray();

        foreach (var card in cards)
        {
            var col = Array.IndexOf(columnBottom, columnBottom.Min());
            card.X = x0 + col * step;
            card.Y = SnapTo(columnBottom[col]);
            columnBottom[col] = card.Y + _rect[card.Id].Height + SlotGap;
        }

        // No height maths here — AutoSizeSections wraps the section around the result on rebuild.
        Commit();
        Rebuild();
        PlanStatus.Text = $"Arranged {cards.Count} card(s).";
    }

    /// <summary>Drops a section back to just the columns its cards actually occupy. Height needs no
    /// equivalent — <see cref="AutoSizeSections"/> already keeps it wrapped.</summary>
    private void ShrinkSection(PlanNode section)
    {
        if (_board is null || !_rect.TryGetValue(section.Id, out var sr)) return;

        var cards = ContainedNodes(section).Select(n => _rect[n.Id]).ToList();
        if (cards.Count == 0) { PlanStatus.Text = "That section has no cards in it yet."; return; }

        var usedCols = cards.Max(r => (int)Math.Round((r.X - ContentLeft(sr)) / ColStep(sr))) + 1;
        section.W = SnapToColumns(Math.Max(1, usedCols) * SlotPitch - SlotGap + SectionPad * 2);

        Commit();
        Rebuild();
    }

    /// <summary>Glows the section a dragged card is currently over, so the drop target is obvious
    /// before you let go.</summary>
    private void UpdateSectionHover()
    {
        if (_dragNode is null || _dragNode.Kind == PlanNodeKind.Section || _board is null)
        {
            SetSectionHover(null);
            return;
        }
        if (!_rect.TryGetValue(_dragNode.Id, out var r)) return;

        var centre = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        var over = _board.Nodes.FirstOrDefault(n => n.Kind == PlanNodeKind.Section
                                                    && _rect.TryGetValue(n.Id, out var s) && s.Contains(centre));
        SetSectionHover(over?.Id);
    }

    private void SetSectionHover(string? id)
    {
        if (_hoverSectionId == id) return;

        if (_hoverSectionId is not null && _sectionBoxes.TryGetValue(_hoverSectionId, out var prev))
        {
            prev.BorderBrush = _baseBorder.TryGetValue(_hoverSectionId, out var b) ? b : prev.BorderBrush;
            prev.BorderThickness = new Thickness(1.4);
            prev.Opacity = SectionRestOpacity;
        }

        _hoverSectionId = id;

        if (id is not null && _sectionBoxes.TryGetValue(id, out var box))
        {
            box.BorderBrush = Res("AccentBrush");
            box.BorderThickness = new Thickness(2.4);
            box.Opacity = 1.0;
        }
    }

    /// <summary>Swaps a note card into edit mode in place. Commits on Esc or when focus leaves.</summary>
    private void BeginEditNote(PlanNode node, Border card)
    {
        // A note showing only its title is far too short to edit in; give the editor room until it commits.
        if (card.Height < NoteH) card.Height = NoteH;

        var title = new TextBox
        {
            Text = node.Title, FontWeight = FontWeights.SemiBold, FontSize = 13,
            Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(6, 4, 6, 4)
        };
        var body = new TextBox
        {
            Text = node.Body, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(6, 4, 6, 4), VerticalContentAlignment = VerticalAlignment.Top
        };

        var dock = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(title, Dock.Top);
        dock.Children.Add(title);
        dock.Children.Add(body);
        card.Child = dock;
        card.Cursor = Cursors.Arrow;

        var committed = false;
        void CommitNote()
        {
            if (committed) return;
            committed = true;
            node.Title = title.Text.Trim();
            node.Body = body.Text.TrimEnd();
            Commit();   // persist + undo checkpoint
            Rebuild();
        }

        card.IsKeyboardFocusWithinChanged += (_, e) => { if (!(bool)e.NewValue) CommitNote(); };
        void OnKey(object s, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            CommitNote();
            Viewport.Focus();
        }
        title.PreviewKeyDown += OnKey;
        body.PreviewKeyDown += OnKey;

        // Tab out of the title into the body rather than out of the card entirely.
        title.PreviewKeyDown += (_, e) => { if (e.Key == Key.Tab) { e.Handled = true; body.Focus(); } };

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (node.Title.Length == 0) { title.Focus(); title.SelectAll(); }
            else { body.Focus(); body.CaretIndex = body.Text.Length; }
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    // ── connectors + resize grips ───────────────────────────────────────────────

    /// <summary>The dot on a card's right edge: drag it onto another card to draw an arrow, or click it
    /// and then click the target. Hidden until the card is hovered so a resting board stays clean.
    /// Hover is tracked on the <paramref name="card"/> itself, not its content grid — a grid only counts
    /// as hovered over its text, which hid the dot again on the way to it and never showed it on a note.</summary>
    private void AddConnector(Grid host, FrameworkElement card, PlanNode node, int column = 0, double? centreY = null)
    {
        const double pad = 20; // grab area around the dot, centred on the card's edge

        var dot = new Ellipse
        {
            Width = 13, Height = 13,
            Fill = Res("AccentBrush"),
            Stroke = Res("Surface1Brush"),
            StrokeThickness = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var handle = new Border
        {
            Width = pad, Height = pad,
            Tag = NoAnchorTag,
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = centreY is null ? VerticalAlignment.Center : VerticalAlignment.Top,
            Margin = new Thickness(0, centreY is { } y ? y - pad / 2 : 0, -pad / 2, 0),
            Cursor = Cursors.Cross,
            Visibility = Visibility.Hidden,
            ToolTip = new ToolTip { Content = "Drag onto another card to draw an arrow — or click, then click the card it points to" },
            Child = dot
        };
        if (host.ColumnDefinitions.Count > column) Grid.SetColumn(handle, column);
        if (host.RowDefinitions.Count > 1) Grid.SetRowSpan(handle, host.RowDefinitions.Count); // centred on the whole card
        Panel.SetZIndex(handle, 5);
        host.Children.Add(handle);

        card.MouseEnter += (_, _) => { if (_linkFrom is null && !ArrowModeActive) handle.Visibility = Visibility.Visible; };
        card.MouseLeave += (_, _) => { if (!ReferenceEquals(_linkFrom, node)) handle.Visibility = Visibility.Hidden; };
        handle.MouseEnter += (_, _) => dot.Width = dot.Height = 16;
        handle.MouseLeave += (_, _) => dot.Width = dot.Height = 13;

        handle.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            BeginLink(node, null, handle);
        };
    }

    private void AddResizeGrip(Grid host, PlanNode node)
    {
        var grip = new Border
        {
            Width = 14, Height = 14,
            Tag = NoAnchorTag,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 6, 6), // inset from the corner rather than flush against it
            Background = Brushes.Transparent,
            Cursor = node.Kind == PlanNodeKind.Section || NoteFolded(node) ? Cursors.SizeWE : Cursors.SizeNWSE,
            ToolTip = new ToolTip
            {
                Content = node.Kind == PlanNodeKind.Section
                    ? "Drag to add or remove a column — height follows the contents"
                    : NoteFolded(node) ? "Drag to resize the width — unfold the note to change its height" : "Drag to resize"
            },
            Child = new Path
            {
                Data = Geometry.Parse("M 11,3 L 11,11 L 3,11"),
                Stroke = Res("TextTertiaryBrush"),
                StrokeThickness = 1.4,
                Stretch = Stretch.None
            }
        };
        Panel.SetZIndex(grip, 5);
        if (host.RowDefinitions.Count > 1) Grid.SetRowSpan(grip, host.RowDefinitions.Count); // the card's real corner
        host.Children.Add(grip);

        grip.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _resizeNode = node;
            _resizeStart = e.GetPosition(BoardCanvas);
            var r = _rect[node.Id];
            _resizeOrigW = r.Width;
            _resizeOrigH = r.Height;
            grip.CaptureMouse();
        };
        grip.MouseMove += (_, e) =>
        {
            if (!ReferenceEquals(_resizeNode, node)) return;
            var p = e.GetPosition(BoardCanvas);
            var isSection = node.Kind == PlanNodeKind.Section;
            node.W = Math.Max(isSection ? SlotColW + SectionPad * 2 : 150, _resizeOrigW + (p.X - _resizeStart.X));
            // A section's height belongs to AutoSizeSections — letting it be dragged shorter than its
            // own contents is exactly the state auto-sizing exists to prevent. A note's H is only its text
            // area: its progress bar and task list keep their own fixed heights below it.
            var extra = node.Kind == PlanNodeKind.Note ? NoteExtraH(node) : 0;
            // A folded note keeps its title height, so only its width follows the drag; any other note can be
            // squeezed right down to its title, at which point it shows nothing but the title.
            var folded = NoteFolded(node);
            if (!isSection && !folded)
                node.H = Math.Max(NoteTitleOnlyH(node, node.W), _resizeOrigH - extra + (p.Y - _resizeStart.Y));
            var height = isSection || folded ? _resizeOrigH : node.H + extra; // (a new section's H is still 0 until it's filled)
            var el = _nodeEls[node.Id];
            el.Width = node.W;
            el.Height = height;
            _rect[node.Id] = new Rect(node.X, node.Y, node.W, height);
            RedrawEdges();
        };
        grip.MouseLeftButtonUp += (_, e) =>
        {
            if (!ReferenceEquals(_resizeNode, node)) return;
            grip.ReleaseMouseCapture();
            _resizeNode = null;
            e.Handled = true;
            // Sections settle on a whole number of columns, so two sections sized by eye still end up
            // the same width and their cards stay aligned across both.
            node.W = node.Kind == PlanNodeKind.Section
                ? SnapToColumns(node.W)
                : Math.Round(node.W / Snap) * Snap;
            node.H = Math.Round(node.H / Snap) * Snap;
            Commit();
            Rebuild();
        };
    }

    // ── card drag ───────────────────────────────────────────────────────────────

    private void OnCardDown(PlanNode node, FrameworkElement el, MouseButtonEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            // Shift+click is purely a selection gesture — no drag, so building up a multi-selection
            // can't accidentally shove a card across the board.
            ToggleSelected(node.Id);
            Viewport.Focus();
            ReportSelection();
            e.Handled = true;
            return;
        }

        // Clicking a card that's already part of a multi-selection keeps the whole selection, so the
        // group can be dragged as one; it collapses to this card on mouse-up if nothing moved.
        _collapseSelectionOnUp = _selected.Count > 1 && _selected.Contains(node.Id);
        if (!_collapseSelectionOnUp) SelectOnly(node.Id);

        _dragNode = node;
        _dragEl = el;
        _dragMoved = false;
        _dragStart = e.GetPosition(BoardCanvas);
        _dragOrigX = node.X;
        _dragOrigY = node.Y;

        // A section carries whatever is sitting on it; any other card carries the rest of the
        // selection, so a multi-selection moves as one block.
        _dragCarried = node.Kind == PlanNodeKind.Section
            ? ContainedNodes(node).Select(n => (n, n.X, n.Y)).ToList()
            : SelectedNodes().Where(n => n.Id != node.Id).Select(n => (n, n.X, n.Y)).ToList();

        // Capture on whatever was actually hit (often a child TextBlock) and remember it, so the
        // release in OnCardUp always targets the same element the capture was taken on.
        _dragCapture = e.Source as UIElement;
        _dragCapture?.CaptureMouse();
        e.Handled = true;
    }

    /// <summary>The cards that explicitly belong to a section (by <see cref="PlanNode.SectionId"/>).
    /// Explicit membership means moving or overlapping sections never steal each other's cards.</summary>
    private List<PlanNode> ContainedNodes(PlanNode section) =>
        _board is null
            ? new()
            : _board.Nodes.Where(n => n.Kind != PlanNodeKind.Section
                                      && string.Equals(n.SectionId, section.Id, StringComparison.Ordinal)).ToList();

    /// <summary>Positional check used only for the drop target: which section a point sits over.</summary>
    private PlanNode? SectionOver(Point p) => _board?.Nodes.FirstOrDefault(n =>
        n.Kind == PlanNodeKind.Section && _rect.TryGetValue(n.Id, out var r) && r.Contains(p));

    /// <summary>Sets a card's section membership from where it currently sits — the section its centre
    /// is over, or loose. Called when a card is dropped.</summary>
    private void AssignSectionFromPosition(PlanNode card)
    {
        if (card.Kind == PlanNodeKind.Section || !_rect.TryGetValue(card.Id, out var r)) return;
        var section = SectionOver(new Point(r.X + r.Width / 2, r.Y + r.Height / 2));
        card.SectionId = section?.Id;
    }

    /// <summary>One-time move from the old position-based membership to explicit SectionId — so
    /// existing boards keep their groupings. Runs on the board's first rebuild after the update.</summary>
    private void MigrateMembership()
    {
        if (_board is null || _board.SectionMembershipSet) return;
        foreach (var card in _board.Nodes.Where(n => n.Kind != PlanNodeKind.Section))
            if (card.SectionId is null && _rect.TryGetValue(card.Id, out var r))
                card.SectionId = SectionOver(new Point(r.X + r.Width / 2, r.Y + r.Height / 2))?.Id;
        _board.SectionMembershipSet = true;
        Save();
    }

    private void OnCardMove(object sender, MouseEventArgs e)
    {
        if (_dragNode is null || _dragEl is null) return;
        var p = e.GetPosition(BoardCanvas);
        double dx = p.X - _dragStart.X, dy = p.Y - _dragStart.Y;
        if (!_dragMoved && Math.Abs(dx) + Math.Abs(dy) < 4) return;
        _dragMoved = true;

        var (nx, ny) = SnapFor(_dragNode, _dragOrigX + dx, _dragOrigY + dy);
        (nx, ny) = ApplyAlignmentSnap(_dragNode, nx, ny); // pull edges into line with nearby cards
        MoveNode(_dragNode, nx, ny);
        // Move carried cards by the dragged node's *actual* applied delta, not an independently
        // snapped position — otherwise the section and its cards round to the grid separately and
        // drift apart as you drag. This keeps everything locked in its original relative layout.
        double appliedDx = nx - _dragOrigX, appliedDy = ny - _dragOrigY;
        foreach (var (n, ox, oy) in _dragCarried) MoveNode(n, ox + appliedDx, oy + appliedDy);
        UpdateSectionHover();
        RedrawEdges();
    }

    /// <summary>Nudges a dragged node so its edges line up with a nearby card, section, or the content
    /// edge of the section it's in — the "snap to align" behaviour that makes rows and columns of
    /// cards sit flush instead of a few pixels off. Only bites within a few pixels, so it never
    /// fights a deliberate free placement.</summary>
    private (double x, double y) ApplyAlignmentSnap(PlanNode dragged, double x, double y)
    {
        if (_board is null || !_rect.TryGetValue(dragged.Id, out var dr)) return (x, y);
        const double threshold = 7;
        double w = dr.Width, h = dr.Height;

        var skip = new HashSet<string>(_dragCarried.Select(c => c.node.Id)) { dragged.Id };
        var sectionIds = new HashSet<string>(_board.Nodes.Where(n => n.Kind == PlanNodeKind.Section).Select(n => n.Id));

        double bestX = x, distX = threshold, bestY = y, distY = threshold;
        void ConsiderX(double candidate) { var d = Math.Abs(candidate - x); if (d < distX) { distX = d; bestX = candidate; } }
        void ConsiderY(double candidate) { var d = Math.Abs(candidate - y); if (d < distY) { distY = d; bestY = candidate; } }

        // The content box of the section the dragged card sits over — align to its inner edges too.
        var overSection = SectionOver(new Point(x + w / 2, y + h / 2));

        // Align to the host section's inner content edges.
        if (overSection is not null && _rect.TryGetValue(overSection.Id, out var host))
        {
            ConsiderX(ContentLeft(host));                 // flush to the left padding
            ConsiderX(host.Right - SectionPad - w);       // flush to the right padding
            ConsiderY(ContentTop(host));                  // flush under the header
        }

        foreach (var (id, r) in _rect)
        {
            if (skip.Contains(id) || sectionIds.Contains(id)) continue; // cards only, not section boxes
            {
                ConsiderX(r.X);                       // left ↔ left
                ConsiderX(r.Right - w);               // right ↔ right
                ConsiderX(r.X + r.Width / 2 - w / 2); // centre ↔ centre
                ConsiderX(r.Right + SlotGap);         // sit just right of it
                ConsiderX(r.X - SlotGap - w);         // sit just left of it
                ConsiderY(r.Y);                       // top ↔ top
                ConsiderY(r.Bottom - h);              // bottom ↔ bottom
                ConsiderY(r.Y + r.Height / 2 - h / 2);
                ConsiderY(r.Bottom + SlotGap);        // sit just below it
                ConsiderY(r.Y - SlotGap - h);         // sit just above it
            }
        }
        return (bestX, bestY);
    }

    /// <summary>Free cards snap to the board grid; a card over a section snaps into that section's
    /// column layout instead, so anything dropped inside lines up with what's already there.
    /// Sections snap to the full grid cell so their edges land on the visible grid lines.</summary>
    private (double x, double y) SnapFor(PlanNode node, double wantX, double wantY) =>
        node.Kind == PlanNodeKind.Section
            ? (SnapToGrid(wantX), SnapToGrid(wantY))
            : _rect.TryGetValue(node.Id, out var r)
                ? SnapAt(r.Width, r.Height, wantX, wantY)
                : (SnapTo(wantX), SnapTo(wantY));

    /// <summary>Snaps to a full grid cell (24 px) rather than the half-cell cards use — so a section's
    /// edges line up with the grid lines drawn behind the board.</summary>
    private static double SnapToGrid(double v) => Math.Round(v / GridCell) * GridCell;

    /// <summary>Snaps a card of the given size to the board grid, or into the columns of whichever
    /// section its centre lands in. Used both while dragging and when a card is first placed.</summary>
    private (double x, double y) SnapAt(double w, double h, double wantX, double wantY)
    {
        if (_board is null) return (SnapTo(wantX), SnapTo(wantY));

        var centre = new Point(wantX + w / 2, wantY + h / 2);
        var section = _board.Nodes.FirstOrDefault(n => n.Kind == PlanNodeKind.Section
                                                       && _rect.TryGetValue(n.Id, out var s) && s.Contains(centre));
        if (section is null || !_rect.TryGetValue(section.Id, out var sr))
            return (SnapTo(wantX), SnapTo(wantY));

        var x0 = ContentLeft(sr);
        var y0 = ContentTop(sr);
        var step = ColStep(sr);
        var col = Math.Clamp((int)Math.Round((wantX - x0) / step), 0, LastColumn(sr));
        return (x0 + col * step, Math.Max(y0, y0 + Math.Round((wantY - y0) / Snap) * Snap));
    }

    private static double ContentLeft(Rect section) => section.X + SectionPad;
    private static double ContentTop(Rect section) => section.Y + SectionHeaderH + SectionPad / 2;

    private static int ColumnCount(Rect section) =>
        Math.Max(1, (int)Math.Floor((section.Width - SectionPad * 2 + SlotGap) / SlotPitch));

    /// <summary>The pitch of one column+gap. Columns are *adaptive* — they divide the section's content
    /// width evenly — so cards snapped to them fill the section edge-to-edge and line up perfectly,
    /// instead of leaving ragged slack against a fixed column width.</summary>
    private static double ColStep(Rect section)
    {
        var cols = ColumnCount(section);
        return (section.Width - SectionPad * 2 + SlotGap) / cols;
    }

    private static double ColWidth(Rect section) => ColStep(section) - SlotGap;
    private static int LastColumn(Rect section) => ColumnCount(section) - 1;

    /// <summary>Snaps every section card to its section's column grid — position to a column and width
    /// to a whole number of columns — so cards in a row are the same width and tile flush. Runs on
    /// each rebuild (idempotent); persists the fitted geometry so it's stable.</summary>
    private void ApplySectionColumnWidths()
    {
        if (_board is null) return;
        var changed = false;
        foreach (var card in _board.Nodes.Where(n => n.Kind != PlanNodeKind.Section && n.SectionId is not null))
        {
            if (_board.Node(card.SectionId) is not { } sec || !_rect.TryGetValue(sec.Id, out var sr)) continue;
            if (!_rect.TryGetValue(card.Id, out var cr)) continue;

            var step = ColStep(sr);
            var cols = ColumnCount(sr);
            var x0 = ContentLeft(sr);
            var span = Math.Clamp((int)Math.Round((cr.Width + SlotGap) / step), 1, cols);
            var col = Math.Clamp((int)Math.Round((cr.X - x0) / step), 0, cols - span);
            var newX = x0 + col * step;
            var newW = span * step - SlotGap;

            if (Math.Abs(newX - card.X) > 0.5 || Math.Abs(newW - card.W) > 0.5)
            {
                card.X = newX;
                card.W = newW;
                changed = true;
            }
            _rect[card.Id] = new Rect(newX, cr.Y, newW, cr.Height);
        }
        if (changed) Save();
    }

    /// <summary>Rounds a section width to a whole number of card columns, then up to the next grid
    /// cell so the section's right edge also lands on a grid line (the extra pixels become right padding).</summary>
    private static double SnapToColumns(double width)
    {
        var cols = Math.Max(1, (int)Math.Round((width - SectionPad * 2 + SlotGap) / SlotPitch));
        var w = cols * SlotPitch - SlotGap + SectionPad * 2;
        return Math.Ceiling(w / GridCell) * GridCell;
    }

    private void OnCardUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragNode is null) return;
        _dragCapture?.ReleaseMouseCapture();
        _dragCapture = null;
        var moved = _dragMoved;
        var landedOn = _hoverSectionId;
        var dropped = _dragNode;
        var carried = _dragCarried.Select(c => c.node).ToList(); // the cards that moved alongside
        SetSectionHover(null);
        _dragNode = null;
        _dragEl = null;
        _dragCarried.Clear();
        e.Handled = true;
        if (!moved)
        {
            // Plain click on an already-multi-selected card: it only meant "pick this one".
            if (_collapseSelectionOnUp) { SelectOnly(dropped.Id); ReportSelection(); }
            _collapseSelectionOnUp = false;
            return;
        }
        _collapseSelectionOnUp = false;

        // Assign section membership from where things landed. Dragging a *section* keeps its own
        // cards (they were carried and stay members); dragging a *card* (and any carried selection)
        // re-homes each into whatever section it was dropped on, or loose. This is what stops a
        // section from stealing cards it merely overlaps.
        if (dropped.Kind != PlanNodeKind.Section)
        {
            AssignSectionFromPosition(dropped);
            foreach (var n in carried) AssignSectionFromPosition(n);
        }

        Commit();
        // Full rebuild, not just a count refresh: the section the card landed in (or left) has to
        // re-wrap around its new contents.
        Rebuild();

        // After Rebuild — it writes the card/link count into the same label.
        if (landedOn is not null && _board?.Node(landedOn) is { } section)
            PlanStatus.Text = $"Added to “{(section.Title.Length > 0 ? section.Title : "Section")}”.";
        else if (dropped.Kind == PlanNodeKind.Section)
            PlanStatus.Text = "";
    }

    private void MoveNode(PlanNode node, double x, double y)
    {
        node.X = x;
        node.Y = y;
        if (_nodeEls.TryGetValue(node.Id, out var el))
        {
            Canvas.SetLeft(el, x);
            Canvas.SetTop(el, y);
        }
        if (_rect.TryGetValue(node.Id, out var r)) _rect[node.Id] = new Rect(x, y, r.Width, r.Height);
    }

    private static double SnapTo(double v) => Math.Round(v / Snap) * Snap;

    // ── linking ─────────────────────────────────────────────────────────────────

    // Two ways to draw an arrow, both ending in TryAddLink: drag a card's edge dot onto another card, or
    // pick a start card and then click its target — the toolbar's Arrow tool (which stays on for a whole
    // map), "Draw arrow from here" on a card's menu, or a plain click on the dot.

    /// <summary>Starts dragging out an arrow: from the dot (<paramref name="anchor"/> null, so the start
    /// attaches automatically) or from a point on the outline (the start is pinned there). The mouse is
    /// captured on <paramref name="capture"/> — the dot itself, or the board for an outline press.</summary>
    private void BeginLink(PlanNode from, PlanAnchor? anchor, UIElement capture)
    {
        _linkFrom = from;
        _linkFromAnchor = anchor;
        _linkDownAt = Mouse.GetPosition(BoardCanvas);
        _linkPressedInside = _rect.TryGetValue(from.Id, out var r) && r.Contains(_linkDownAt);
        _linkPreview = CreateLinkPreview();
        BoardCanvas.Children.Add(_linkPreview);
        capture.CaptureMouse();
        Mouse.AddMouseMoveHandler(capture, OnLinkMove);
        Mouse.AddMouseUpHandler(capture, OnLinkUp);
        PlanStatus.Text = "Drop on a card — near its outline to pin the end there…";
    }

    private void OnLinkMove(object sender, MouseEventArgs e)
    {
        if (_linkFrom is null || _linkPreview is null) return;
        var p = e.GetPosition(BoardCanvas);
        if (_linkPreview.Data is null && (p - _linkDownAt).Length < 5) return; // not a drag yet
        _linkPreview.Data = LinkPreviewGeometry(_linkFrom, _linkFromAnchor, p);
    }

    private void OnLinkUp(object sender, MouseButtonEventArgs e)
    {
        if (_linkFrom is null) return;
        var from = _linkFrom;
        var fromAnchor = _linkFromAnchor;
        _linkFrom = null; // before the release, so the MouseLeave it raises is free to hide the dot
        _linkFromAnchor = null;
        var capture = (UIElement)sender;
        capture.ReleaseMouseCapture();
        Mouse.RemoveMouseMoveHandler(capture, OnLinkMove);
        Mouse.RemoveMouseUpHandler(capture, OnLinkUp);
        if (_linkPreview is not null) { BoardCanvas.Children.Remove(_linkPreview); _linkPreview = null; }

        var drop = e.GetPosition(BoardCanvas);
        var dragged = (drop - _linkDownAt).Length >= 5;
        if (dragged && ArrowTargetAt(drop) is { } target && target.Node.Id != from.Id)
        {
            TryAddLink(from, fromAnchor, target.Node, target.Anchor);
            return;
        }

        Rebuild(); // resets the status line and the dot this drag left showing
        if (dragged) return;
        // Let go without dragging. On the dot: carry on as a click-then-click arrow. On an outline it was
        // only a click — it selects the card when it landed on it, as a click there always has.
        if (fromAnchor is null) BeginPendingArrow(from);
        else if (_linkPressedInside) { SelectOnly(from.Id); ReportSelection(); }
        else ClearSelection();
    }

    /// <summary>Draws an arrow between two cards, each end pinned to its anchor or (null) automatic. When an
    /// arrow already runs between them the same way round and this one pins an end, that arrow is re-pinned
    /// instead — which is how an existing arrow's ends get moved. Otherwise a pair links only once.</summary>
    private void TryAddLink(PlanNode from, PlanAnchor? fromAnchor, PlanNode to, PlanAnchor? toAnchor)
    {
        if (_board is null || from.Id == to.Id) return;

        var same = _board.Edges.FirstOrDefault(x => x.FromId == from.Id && x.ToId == to.Id);
        if (same is not null && (fromAnchor is not null || toAnchor is not null))
        {
            same.FromAnchor = fromAnchor;
            same.ToAnchor = toAnchor;
            Commit();
            Rebuild();
            PlanStatus.Text = "Arrow re-pinned to the new points.";
            return;
        }
        if (same is not null || _board.Edges.Any(x => x.FromId == to.Id && x.ToId == from.Id))
        {
            PlanStatus.Text = "Those cards are already linked.";
            return;
        }

        _board.Edges.Add(new PlanEdge { FromId = from.Id, ToId = to.Id, FromAnchor = fromAnchor, ToAnchor = toAnchor });
        Commit();
        Rebuild();
        // After Rebuild — it writes the card/link count into the same label.
        PlanStatus.Text = "Arrow added  ·  click it to give it a label";
    }

    /// <summary>What an arrow end at a board point attaches to. Well inside a card: that card, attached
    /// automatically (at whichever outline point faces the other end). On or just around its outline:
    /// that card, pinned to the nearest point of the outline. A section counts by its outline, plus its
    /// title strip for an automatic end — its body belongs to the cards on it, so a near-miss on one of
    /// those doesn't quietly link the whole section instead.</summary>
    private (PlanNode Node, PlanAnchor? Anchor)? ArrowTargetAt(Point p)
    {
        if (_board is null) return null;
        var zoom = Math.Max(0.05, ZoomT.ScaleX);
        double outer = OutlineOuterPx / zoom, inner = OutlineInnerPx / zoom;
        PlanNode? near = null;
        var nearDist = double.MaxValue;

        // Cards, topmost first (later in the list is drawn on top). Inside a card decides it outright;
        // just outside one only makes it a candidate, as the point may be nearer a neighbour's outline.
        for (var i = _board.Nodes.Count - 1; i >= 0; i--)
        {
            var n = _board.Nodes[i];
            if (n.Kind == PlanNodeKind.Section || !_rect.TryGetValue(n.Id, out var r)) continue;
            if (r.Contains(p)) return (n, DistanceToOutline(r, p) <= inner ? NearestAnchor(r, p) : null);
            Consider(n, r, outer);
        }
        // Sections sit under every card, and only their outline (from either side) is a target.
        foreach (var n in _board.Nodes.Where(n => n.Kind == PlanNodeKind.Section))
            if (_rect.TryGetValue(n.Id, out var r)) Consider(n, r, r.Contains(p) ? inner : outer);

        if (near is not null) return (near, NearestAnchor(_rect[near.Id], p));

        var strip = _board.Nodes.LastOrDefault(n => n.Kind == PlanNodeKind.Section
            && _rect.TryGetValue(n.Id, out var r) && new Rect(r.X, r.Y, r.Width, SectionHeaderH).Contains(p));
        if (strip is null) return null;
        return (strip, null);

        void Consider(PlanNode n, Rect r, double reach)
        {
            var d = DistanceToOutline(r, p);
            if (d <= reach && d < nearDist) { near = n; nearDist = d; }
        }
    }

    /// <summary>How far a point is from a rectangle's outline, from either side of it.</summary>
    private static double DistanceToOutline(Rect r, Point p)
    {
        if (r.Contains(p)) return Math.Min(Math.Min(p.X - r.Left, r.Right - p.X), Math.Min(p.Y - r.Top, r.Bottom - p.Y));
        var dx = Math.Max(Math.Max(r.Left - p.X, 0), p.X - r.Right);
        var dy = Math.Max(Math.Max(r.Top - p.Y, 0), p.Y - r.Bottom);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>The point of a card's outline nearest to <paramref name="p"/>, as an anchor. Kept clear of
    /// the rounded corners, so a pinned arrow always leaves a straight side square to it, and snapped to
    /// the middle of a side when it's close — where tidy diagrams want their arrows.</summary>
    private PlanAnchor NearestAnchor(Rect r, Point p)
    {
        const double corner = 12;
        var snap = 8 / Math.Max(0.05, ZoomT.ScaleX);
        double x = Math.Clamp(p.X, r.Left, r.Right), y = Math.Clamp(p.Y, r.Top, r.Bottom);
        double left = x - r.Left, right = r.Right - x, top = y - r.Top, bottom = r.Bottom - y;
        var side = Math.Min(Math.Min(left, right), Math.Min(top, bottom));

        if (side == left || side == right)
        {
            x = side == left ? r.Left : r.Right;
            var mid = r.Top + r.Height / 2;
            var inset = Math.Min(corner, r.Height / 2);
            y = Math.Abs(y - mid) <= snap ? mid : Math.Clamp(y, r.Top + inset, r.Bottom - inset);
        }
        else
        {
            y = side == top ? r.Top : r.Bottom;
            var mid = r.Left + r.Width / 2;
            var inset = Math.Min(corner, r.Width / 2);
            x = Math.Abs(x - mid) <= snap ? mid : Math.Clamp(x, r.Left + inset, r.Right - inset);
        }
        return new PlanAnchor { X = Math.Round((x - r.X) / r.Width, 4), Y = Math.Round((y - r.Y) / r.Height, 4) };
    }

    /// <summary>Where a pinned arrow end sits on a card at its current size, and which way that side of
    /// the outline faces. An anchor that somehow isn't on the outline is pushed out to its nearest side.</summary>
    private static (Point Point, Vector Normal) AnchorOnCard(Rect r, PlanAnchor a)
    {
        var q = new Point(r.X + Math.Clamp(a.X, 0, 1) * r.Width, r.Y + Math.Clamp(a.Y, 0, 1) * r.Height);
        double left = q.X - r.Left, right = r.Right - q.X, top = q.Y - r.Top, bottom = r.Bottom - q.Y;
        var side = Math.Min(Math.Min(left, right), Math.Min(top, bottom));
        var normal = side == left ? new Vector(-1, 0) : side == right ? new Vector(1, 0)
                   : side == top ? new Vector(0, -1) : new Vector(0, 1);
        if (normal.X != 0) q.X = normal.X < 0 ? r.Left : r.Right;
        else q.Y = normal.Y < 0 ? r.Top : r.Bottom;
        // A few pixels off the outline, like an automatic end, so the arrowhead doesn't sit on the border.
        return (q + normal * 3, normal);
    }

    private Path CreateLinkPreview()
    {
        var preview = new Path
        {
            Stroke = Res("AccentBrush"),
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            IsHitTestVisible = false
        };
        Panel.SetZIndex(preview, 4);
        return preview;
    }

    /// <summary>The dashed preview of the arrow being drawn, shaped like the arrow it will become: from the
    /// start card (at its pinned point, if any) to the pointer — or, over a card it can connect to, onto
    /// the exact spot it will attach to there.</summary>
    private Geometry? LinkPreviewGeometry(PlanNode from, PlanAnchor? fromAnchor, Point pointer)
    {
        if (!_rect.TryGetValue(from.Id, out var fr)) return null;
        (Point Point, Vector Normal)? pa = fromAnchor is null ? null : AnchorOnCard(fr, fromAnchor);
        (Point Point, Vector Normal)? pb = null;
        var end = pointer;
        if (ArrowTargetAt(pointer) is { } t && t.Node.Id != from.Id && _rect.TryGetValue(t.Node.Id, out var tr))
        {
            pb = t.Anchor is { } ta ? AnchorOnCard(tr, ta) : null;
            end = pb?.Point ?? BorderPoint(tr, pa?.Point ?? Centre(fr));
        }
        var start = pa?.Point ?? BorderPoint(fr, end);
        return (end - start).Length < 2 ? null : EdgeCurve(start, pa?.Normal, end, pb?.Normal).Geometry;
    }

    // ── arrow tool ──────────────────────────────────────────────────────────────

    private bool ArrowModeActive => _arrowTool || _arrowFrom is not null;

    private string ArrowPrompt => _arrowFrom is null
        ? "Click the card an arrow starts from  ·  Esc to stop"
        : "Now click the card it points to  ·  Esc to cancel";

    private void OnToggleArrowTool(object sender, RoutedEventArgs e) => SetArrowTool(!_arrowTool);

    /// <summary>The toolbar's Arrow tool. While it's on, clicking cards draws arrows (start card, then
    /// target) instead of dragging or opening them, and it stays on so a whole map can be connected in
    /// one go.</summary>
    private void SetArrowTool(bool on)
    {
        _arrowTool = on;
        if (!on) CancelPendingArrow();
        else if (_arrowFrom is null) ClearSelection(); // a selection outline would read as a picked start card
        ArrowToolButton.Style = (Style)FindResource(on ? "AccentButton" : "SubtleButton");
        UpdateArrowCursor();
        PlanStatus.Text = on ? ArrowPrompt : "";
        Viewport.Focus(); // off the button, so Space can't re-press it and Esc reaches the board
    }

    /// <summary>Picks the card an arrow starts from — pinned to <paramref name="anchor"/> on its outline, or
    /// automatic — and the next card clicked becomes its target. A dashed preview follows the pointer.</summary>
    private void BeginPendingArrow(PlanNode from, PlanAnchor? anchor = null)
    {
        if (_board is null) return;
        CancelPendingArrow();
        _arrowFrom = from;
        _arrowFromAnchor = anchor;
        SelectOnly(from.Id); // outlines the start card
        _arrowPreview = CreateLinkPreview();
        _arrowPreview.Data = LinkPreviewGeometry(from, anchor, Mouse.GetPosition(BoardCanvas));
        BoardCanvas.Children.Add(_arrowPreview);
        UpdateArrowCursor();
        PlanStatus.Text = ArrowPrompt;
        Viewport.Focus();
    }

    private void CancelPendingArrow()
    {
        if (_arrowPreview is not null) { BoardCanvas.Children.Remove(_arrowPreview); _arrowPreview = null; }
        _arrowFromAnchor = null;
        if (_arrowFrom is null) return;
        _arrowFrom = null;
        ClearSelection();
        UpdateArrowCursor();
    }

    /// <summary>Esc, or a click on empty canvas: puts down a half-drawn arrow first, and only then leaves
    /// the tool.</summary>
    private void BackOutOfArrowMode()
    {
        if (_arrowFrom is not null)
        {
            CancelPendingArrow();
            PlanStatus.Text = _arrowTool ? ArrowPrompt : "";
        }
        else if (_arrowTool)
        {
            SetArrowTool(false);
        }
    }

    /// <summary>A card clicked while drawing arrows: the first click picks the start, the second the
    /// target — each pinned where it landed on an outline, automatic when well inside the card. Clicking
    /// the start card again puts it back down.</summary>
    private void OnArrowCardClick(PlanNode node, PlanAnchor? anchor)
    {
        if (_arrowFrom is null) { BeginPendingArrow(node, anchor); return; }

        var from = _arrowFrom;
        var fromAnchor = _arrowFromAnchor;
        CancelPendingArrow();
        if (from.Id == node.Id) { PlanStatus.Text = _arrowTool ? ArrowPrompt : ""; return; }
        TryAddLink(from, fromAnchor, node, anchor);
    }

    /// <summary>Presses that start or finish an arrow, caught tunnelling so they beat the cards' own
    /// handlers. While drawing arrows, pressing anywhere on a card — buttons and member rows included —
    /// picks that card instead of dragging, editing or opening it. Otherwise a press on a card's outline
    /// (not on the dot, a grip or a button) drags out an arrow pinned to that point, and a press anywhere
    /// else on the card still drags the card.</summary>
    private void OnViewportPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _swallowArrowUp = false;
        var target = ArrowTargetAt(e.GetPosition(BoardCanvas));

        if (ArrowModeActive)
        {
            // Empty canvas falls through to OnViewportMouseDown, which pans (or backs out on a plain click).
            if (target is not { } t) return;
            e.Handled = true;
            _swallowArrowUp = true;
            Viewport.Focus();
            OnArrowCardClick(t.Node, t.Anchor);
            return;
        }

        if (target is { } hit && hit.Anchor is { } anchor && !OwnsPress(e.OriginalSource as DependencyObject))
        {
            e.Handled = true;
            Viewport.Focus();
            HideAnchorMarker();
            BeginLink(hit.Node, anchor, Viewport);
        }
    }

    /// <summary>Eats the release of a press the arrow tool used, so it can't also click whatever is under
    /// the pointer — on a group card's member row that would open the mod's page.</summary>
    private void OnViewportPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_swallowArrowUp) return;
        _swallowArrowUp = false;
        e.Handled = true;
    }

    /// <summary>A crosshair while drawing arrows, or while the pointer is on an outline where a press would
    /// start one — forced over the cards as well, whose own move and hand cursors would suggest a click
    /// drags or opens them.</summary>
    private void UpdateArrowCursor()
    {
        if (_panning) return; // a pan owns the cursor until it ends
        var on = ArrowModeActive || _anchorHover;
        Viewport.Cursor = on ? Cursors.Cross : Cursors.Arrow;
        Viewport.ForceCursor = on;
    }

    /// <summary>Whether a press on this element belongs to the element rather than to outline pinning: the
    /// link dot, a resize grip, a button or a text field.</summary>
    private static bool OwnsPress(DependencyObject? d)
    {
        for (; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D
                 ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is FrameworkElement { Tag: var tag } && ReferenceEquals(tag, NoAnchorTag)) return true;
            if (d is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.TextBoxBase) return true;
        }
        return false;
    }

    /// <summary>Marks where an arrow end would pin: the outline point under the pointer when idle, or the
    /// point on the card an arrow being drawn is over. Hidden while panning, dragging or resizing a card,
    /// sweeping a selection, and over elements that take their own presses.</summary>
    private void UpdateAnchorHover(Point p, DependencyObject? over)
    {
        var drawing = _linkFrom ?? _arrowFrom;
        var idle = drawing is null && !ArrowModeActive;
        Point? at = null;
        if (!_panning && !_marquee && _dragNode is null && _resizeNode is null
            && ArrowTargetAt(p) is { } t && t.Anchor is { } a && t.Node.Id != drawing?.Id
            && !(idle && OwnsPress(over)) && _rect.TryGetValue(t.Node.Id, out var r))
        {
            var (point, normal) = AnchorOnCard(r, a);
            at = point - normal * 3; // on the outline itself, not the arrow's few-pixel gap
        }

        if (at is { } spot) ShowAnchorMarker(spot);
        else HideAnchorMarker();

        var hover = idle && at is not null;
        if (hover == _anchorHover) return;
        _anchorHover = hover;
        UpdateArrowCursor();
    }

    private void ShowAnchorMarker(Point at)
    {
        _anchorMarker ??= new Ellipse { Fill = Res("AccentBrush"), Stroke = Res("Surface1Brush"), IsHitTestVisible = false };
        if (_anchorMarker.Parent is null)
        {
            Panel.SetZIndex(_anchorMarker, 7);
            BoardCanvas.Children.Add(_anchorMarker); // (re)added: a rebuild clears the canvas
        }
        var size = Math.Clamp(11 / Math.Max(0.05, ZoomT.ScaleX), 7, 30); // about the same size on screen at any zoom
        _anchorMarker.Width = _anchorMarker.Height = size;
        _anchorMarker.StrokeThickness = size / 5.5;
        Canvas.SetLeft(_anchorMarker, at.X - size / 2);
        Canvas.SetTop(_anchorMarker, at.Y - size / 2);
    }

    private void HideAnchorMarker()
    {
        if (_anchorMarker?.Parent is Panel host) host.Children.Remove(_anchorMarker);
    }

    // ── edges ───────────────────────────────────────────────────────────────────

    private void AddEdge(PlanEdge edge)
    {
        if (_board is null) return;
        if (!_rect.TryGetValue(edge.FromId, out var from) || !_rect.TryGetValue(edge.ToId, out var to))
        {
            // An endpoint card was deleted — drop the dangling link rather than drawing nothing forever.
            _board.Edges.Remove(edge);
            Commit();
            return;
        }

        // A pinned end sits on its chosen point of the outline. An automatic end sits wherever the outline
        // faces the other end — aimed at that end's pinned point when it has one, else at its card's centre.
        (Point Point, Vector Normal)? pa = edge.FromAnchor is { } fa ? AnchorOnCard(from, fa) : null;
        (Point Point, Vector Normal)? pb = edge.ToAnchor is { } ta ? AnchorOnCard(to, ta) : null;
        var start = pa?.Point ?? BorderPoint(from, pb?.Point ?? Centre(to));
        var end = pb?.Point ?? BorderPoint(to, pa?.Point ?? Centre(from));

        var len = (end - start).Length;
        if (len < 2) return;

        var stroke = ColorOf(edge.Color, EdgeBrush);
        var (geo, tan, mid) = EdgeCurve(start, pa?.Normal, end, pb?.Normal);

        var path = new Path
        {
            Data = geo, Stroke = stroke, StrokeThickness = 1.8, IsHitTestVisible = false,
            StrokeDashArray = edge.Style == PlanEdgeStyle.Dashed ? new DoubleCollection { 5, 4 } : null
        };
        Panel.SetZIndex(path, 0);
        BoardCanvas.Children.Add(path);
        _edgeEls.Add(path);

        // A fat transparent copy makes a 2 px line comfortably clickable.
        var hit = new Path
        {
            Data = geo, Stroke = Brushes.Transparent, StrokeThickness = 12, Cursor = Cursors.Hand,
            ToolTip = new ToolTip { Content = "Click to label  ·  right-click to restyle, reverse or remove" }
        };
        // The press is handled too: left to bubble, it reaches the canvas, which starts a pan and captures
        // the mouse — so the release never came back here and a click never opened the label.
        hit.MouseLeftButtonDown += (_, e) => e.Handled = true;
        hit.MouseRightButtonUp += (_, e) => { e.Handled = true; ShowEdgeMenu(edge, hit); };
        hit.MouseLeftButtonUp += (_, e) => { e.Handled = true; _ = EditEdgeLabelAsync(edge); };
        Panel.SetZIndex(hit, 1);
        BoardCanvas.Children.Add(hit);
        _edgeEls.Add(hit);

        if (edge.Style == PlanEdgeStyle.Arrow)
        {
            var arrowLen = Math.Clamp(len * 0.3, 5, 10);
            var arrowW = arrowLen * 0.52;
            var back = new Point(end.X - tan.X * arrowLen, end.Y - tan.Y * arrowLen);
            var ap = new Vector(-tan.Y, tan.X);
            var arrow = new Polygon
            {
                Points = new PointCollection
                {
                    end,
                    new Point(back.X + ap.X * arrowW, back.Y + ap.Y * arrowW),
                    new Point(back.X - ap.X * arrowW, back.Y - ap.Y * arrowW)
                },
                Fill = edge.Color is null ? ArrowBrush : stroke,
                IsHitTestVisible = false
            };
            Panel.SetZIndex(arrow, 0);
            BoardCanvas.Children.Add(arrow);
            _edgeEls.Add(arrow);
        }

        if (!string.IsNullOrWhiteSpace(edge.Label))
        {
            var chip = new Border
            {
                Background = Res("Surface3Brush"),
                BorderBrush = stroke,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(6, 2, 6, 2),
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = edge.Label, FontSize = 10.5, Foreground = Res("TextSecondaryBrush"),
                    MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis
                }
            };
            chip.MouseLeftButtonDown += (_, e) => e.Handled = true; // same reason as the line's hit path
            chip.MouseLeftButtonUp += (_, e) => { e.Handled = true; _ = EditEdgeLabelAsync(edge); };
            chip.MouseRightButtonUp += (_, e) => { e.Handled = true; ShowEdgeMenu(edge, chip); };
            // Measured so the chip can be centred on the curve rather than hanging off it.
            chip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(chip, mid.X - chip.DesiredSize.Width / 2);
            Canvas.SetTop(chip, mid.Y - chip.DesiredSize.Height / 2);
            Panel.SetZIndex(chip, 1);
            BoardCanvas.Children.Add(chip);
            _edgeEls.Add(chip);
        }
    }

    private void RedrawEdges()
    {
        foreach (var el in _edgeEls) BoardCanvas.Children.Remove(el);
        _edgeEls.Clear();
        if (_board is null) return;
        foreach (var edge in _board.Edges.ToList()) AddEdge(edge);
    }

    private static Point Centre(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    /// <summary>The curve an arrow follows between its two ends, the direction it arrives in (for the
    /// arrowhead) and its midpoint (for the label). With both ends automatic it's the gentle single bend
    /// arrows have always had. A pinned end leaves or meets its card square to that side of the outline,
    /// so the arrow visibly comes out of the chosen spot instead of cutting back across the card.</summary>
    private static (PathGeometry Geometry, Vector EndTangent, Point Mid) EdgeCurve(
        Point start, Vector? startNormal, Point end, Vector? endNormal)
    {
        var dir = end - start;
        var len = dir.Length;
        var fig = new PathFigure { StartPoint = start, IsFilled = false };
        Vector tangent;
        Point mid;

        if (startNormal is null && endNormal is null)
        {
            var perp = new Vector(-dir.Y, dir.X);
            if (perp.Length > 0) perp.Normalize();
            var offset = len < 110 ? 0 : Math.Min(38, len * 0.13);
            var ctrl = new Point((start.X + end.X) / 2 + perp.X * offset, (start.Y + end.Y) / 2 + perp.Y * offset);
            fig.Segments.Add(new QuadraticBezierSegment(ctrl, end, true));
            tangent = offset == 0 ? dir : end - ctrl;
            mid = new Point(0.25 * start.X + 0.5 * ctrl.X + 0.25 * end.X, 0.25 * start.Y + 0.5 * ctrl.Y + 0.25 * end.Y);
        }
        else
        {
            var reach = Math.Clamp(len * 0.4, 24, 120);
            var c1 = startNormal is { } n1 ? start + n1 * reach : start + dir / 3;
            var c2 = endNormal is { } n2 ? end + n2 * reach : end - dir / 3;
            fig.Segments.Add(new BezierSegment(c1, c2, end, true));
            tangent = end - c2;
            mid = new Point(0.125 * start.X + 0.375 * (c1.X + c2.X) + 0.125 * end.X,
                            0.125 * start.Y + 0.375 * (c1.Y + c2.Y) + 0.125 * end.Y);
        }

        if (tangent.Length < 0.001) tangent = dir;
        if (tangent.Length > 0) tangent.Normalize();
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        return (geo, tangent, mid);
    }

    private static Point BorderPoint(Rect r, Point toward)
    {
        var c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        double dx = toward.X - c.X, dy = toward.Y - c.Y;
        if (dx == 0 && dy == 0) return c;
        var tx = dx != 0 ? (r.Width / 2 + 3) / Math.Abs(dx) : double.MaxValue;
        var ty = dy != 0 ? (r.Height / 2 + 3) / Math.Abs(dy) : double.MaxValue;
        var t = Math.Min(tx, ty);
        return new Point(c.X + dx * t, c.Y + dy * t);
    }

    private async Task EditEdgeLabelAsync(PlanEdge edge)
    {
        var text = await PromptAsync("Link label", "What does this connection mean?", edge.Label ?? "");
        if (text is null) return;
        edge.Label = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        Commit();
        Rebuild();
    }

    private void ShowEdgeMenu(PlanEdge edge, FrameworkElement anchor)
    {
        if (_board is null) return;
        var menu = new ContextMenu { PlacementTarget = anchor };
        menu.Items.Add(Item(edge.Label is null ? "Add label…" : "Edit label…", () => _ = EditEdgeLabelAsync(edge)));

        var style = new MenuItem { Header = "Style" };
        void StyleItem(string header, PlanEdgeStyle s) =>
            style.Items.Add(Radio(header, edge.Style == s, () => { edge.Style = s; Commit(); Rebuild(); }));
        StyleItem("Arrow", PlanEdgeStyle.Arrow);
        StyleItem("Plain line", PlanEdgeStyle.Line);
        StyleItem("Dashed", PlanEdgeStyle.Dashed);
        menu.Items.Add(style);

        menu.Items.Add(ColorMenu(edge.Color, hex => { edge.Color = hex; Commit(); Rebuild(); }));
        menu.Items.Add(Item("Reverse direction", () =>
        {
            (edge.FromId, edge.ToId) = (edge.ToId, edge.FromId);
            (edge.FromAnchor, edge.ToAnchor) = (edge.ToAnchor, edge.FromAnchor); // each pinned point stays on its own card
            Commit();
            Rebuild();
        }));
        if (edge.FromAnchor is not null || edge.ToAnchor is not null)
            menu.Items.Add(Item("Unpin ends", () =>
            {
                edge.FromAnchor = edge.ToAnchor = null; // back to attaching wherever faces the other card
                Commit();
                Rebuild();
            }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Remove link", () => { _board.Edges.Remove(edge); Commit(); Rebuild(); }));
        menu.IsOpen = true;
    }

    /// <summary>Right-clicking a card selects it first, so the menu acts on something visibly
    /// highlighted and the Del key works straight afterwards without a separate left-click.</summary>
    private void SelectForMenu(PlanNode node)
    {
        // Keep an existing multi-selection when right-clicking one of its members, so the menu can
        // offer to act on all of them.
        if (!_selected.Contains(node.Id)) SelectOnly(node.Id);
        Viewport.Focus();
    }

    // ── card context menus ──────────────────────────────────────────────────────

    private void ShowModCardMenu(PlanNode node, PackMod mod, FrameworkElement anchor)
    {
        var menu = ModOptionsMenu.Build(mod, BuildModContext());
        // Board-scoped actions, so they go at the top rather than buried under a dozen mod options —
        // index 2 is straight after the menu's header + separator. Note removing takes away the *card*;
        // the jar is untouched, which is why the board never offers "Delete file".
        var at = 2;
        menu.Items.Insert(at++, Item("Draw arrow from here", () => BeginPendingArrow(node)));
        menu.Items.Insert(at++, Item("Remove card from board", () => RemoveNode(node),
            gesture: _selected.Count > 1 ? null : "Del"));
        if (MultiDeleteItem(node) is { } multi) menu.Items.Insert(at++, multi);
        menu.Items.Insert(at, new Separator());
        menu.PlacementTarget = anchor;
        menu.IsOpen = true;
    }

    private void ShowModOptions(PackMod mod, FrameworkElement anchor)
    {
        var menu = ModOptionsMenu.Build(mod, BuildModContext());
        menu.PlacementTarget = anchor;
        menu.IsOpen = true;
    }

    private ModOptionsContext BuildModContext() => new()
    {
        PackId = _packId,
        Inventory = App.State.ModInventory,
        Owner = _owner,
        AllMods = _mods,
        // A flag edit can change which group cards a mod falls into, so the board re-resolves too.
        OnChanged = ModsChanged,
        OnOpenPage = m => _onOpenMod?.Invoke(m),
        OnSetEnabled = (list, en) =>
        {
            foreach (var m in list) App.State.ModInventory.SetEnabled(m, en);
            ModsChanged();
        }
    };

    private void ModsChanged()
    {
        Rebuild();
        _onModsChanged?.Invoke();
    }

    private void ShowGroupCardMenu(PlanNode node, IReadOnlyList<PackMod> members, FrameworkElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor };
        menu.Items.Add(Item(node.Collapsed ? "Show members" : "Hide members",
            () => { node.Collapsed = !node.Collapsed; Commit(); Rebuild(); }));
        if (!node.Collapsed && members.Count > GroupMaxRows)
            menu.Items.Add(Item(node.Expanded ? "Show only the first few" : $"Show all {members.Count}",
                () => { node.Expanded = !node.Expanded; Commit(); Rebuild(); }));
        menu.Items.Add(Item("Rename heading…", () => _ = RenameNodeAsync(node, "Card heading")));
        menu.Items.Add(ColorMenu(node.Color, hex => { node.Color = hex; Commit(); Rebuild(); }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Draw arrow from here", () => BeginPendingArrow(node)));
        menu.Items.Add(Item($"Add a card for each of these {members.Count} mods",
            () => ExplodeGroup(node, members), enabled: members.Count is > 0 and <= 40));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Remove card from board", () => RemoveNode(node),
            gesture: _selected.Count > 1 ? null : "Del"));
        if (MultiDeleteItem(node) is { } multi) menu.Items.Add(multi);
        menu.IsOpen = true;
    }

    private void ShowPlainCardMenu(PlanNode node, FrameworkElement anchor, string what)
    {
        var menu = new ContextMenu { PlacementTarget = anchor };
        menu.Items.Add(Item(what == "note" ? "Edit…" : "Rename…", () =>
        {
            if (what == "note" && _nodeEls.TryGetValue(node.Id, out var el) && el is Border card) BeginEditNote(node, card);
            else _ = RenameNodeAsync(node, "Section title");
        }));
        menu.Items.Add(Item("Draw arrow from here", () => BeginPendingArrow(node)));
        if (what == "note") AddNoteTrackingItems(menu.Items, node);
        if (what == "section")
        {
            // A full section leaves no empty body to right-click, so give it a reliable "add" here.
            menu.Items.Add(Item("Add note to section", () => AddNoteNode(SectionAnchor(node), edit: true)));
            menu.Items.Add(Item("Add mod card to section…", () => _ = AddModCardAsync(SectionAnchor(node))));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Arrange cards", () => ArrangeSection(node)));
            menu.Items.Add(Item("Fit width to cards", () => ShrinkSection(node)));
        }
        menu.Items.Add(ColorMenu(node.Color, hex => { node.Color = hex; Commit(); Rebuild(); }));
        menu.Items.Add(Item("Duplicate", () =>
        {
            if (_board is null) return;
            var copy = node.Clone();
            copy.X += 24;
            copy.Y += 24;
            _board.Nodes.Add(copy);
            Commit();
            Rebuild();
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item($"Delete {what}", () => RemoveNode(node),
            gesture: _selected.Count > 1 ? null : "Del"));
        if (what == "section")
            menu.Items.Add(Item("Delete section and its cards", () => _ = RemoveSectionWithCardsAsync(node)));
        if (MultiDeleteItem(node) is { } multi) menu.Items.Add(multi);
        menu.IsOpen = true;
    }

    /// <summary>Deletes a section along with everything sitting on it. Plain "Delete section" leaves
    /// the cards behind as loose ones, so this is the only path that can lose several at once —
    /// hence the confirm.</summary>
    private async Task RemoveSectionWithCardsAsync(PlanNode section)
    {
        if (_board is null) return;
        var inside = ContainedNodes(section);

        if (inside.Count > 0 && _owner is not null)
        {
            var title = section.Title.Length > 0 ? section.Title : "this section";
            if (!await AppDialog.ConfirmAsync(_owner, "Delete section",
                    $"Delete {title} and the {inside.Count} card(s) on it?", "Delete", "Cancel", danger: true))
                return;
        }

        foreach (var n in inside)
        {
            _board.Nodes.Remove(n);
            _board.Edges.RemoveAll(x => x.FromId == n.Id || x.ToId == n.Id);
        }
        RemoveNode(section); // saves + rebuilds
    }

    private async Task RenameNodeAsync(PlanNode node, string label)
    {
        var name = await PromptAsync(label, label, node.Title);
        if (name is null) return;
        node.Title = name.Trim();
        Commit();
        Rebuild();
    }

    private MenuItem ColorMenu(string? current, Action<string?> apply)
    {
        var parent = new MenuItem { Header = "Colour" };
        parent.Items.Add(Radio("Default", current is null, () => apply(null)));
        parent.Items.Add(new Separator());
        foreach (var (name, hex) in AccentPalette.Colors)
        {
            var item = new MenuItem
            {
                Header = name,
                InputGestureText = string.Equals(current, hex, StringComparison.OrdinalIgnoreCase) ? "✓" : "",
                Icon = new Border
                {
                    Width = 12, Height = 12, CornerRadius = new CornerRadius(3),
                    Background = ColorOf(hex, Res("BorderBrush"))
                }
            };
            var captured = hex;
            item.Click += (_, _) => apply(captured);
            parent.Items.Add(item);
        }
        return parent;
    }

    /// <summary>Turns a group card into one card per member, each linked back to the group — the
    /// fastest way to go from "these 6 mods are my Magic set" to actually planning around them.</summary>
    private void ExplodeGroup(PlanNode group, IReadOnlyList<PackMod> members)
    {
        if (_board is null || members.Count == 0) return;

        var gr = _rect[group.Id];
        var startX = gr.X + gr.Width + 90;
        var startY = gr.Y;
        var added = 0;

        foreach (var mod in members)
        {
            if (_board.Nodes.Any(n => n.Kind == PlanNodeKind.Mod && n.ModKey is not null
                                      && mod.CandidateKeys.Contains(n.ModKey, StringComparer.OrdinalIgnoreCase)))
                continue;

            var node = new PlanNode
            {
                Kind = PlanNodeKind.Mod,
                ModKey = mod.Key,
                X = SnapTo(startX),
                Y = SnapTo(startY + added * (ModH + 16))
            };
            _board.Nodes.Add(node);
            _board.Edges.Add(new PlanEdge { FromId = group.Id, ToId = node.Id });
            added++;
        }

        Commit();
        Rebuild();
        PlanStatus.Text = added == 0 ? "Every mod in that group is already on the board." : $"Added {added} card(s).";
    }

    private void RemoveNode(PlanNode node)
    {
        if (_board is null) return;
        _board.Nodes.Remove(node);
        _board.Edges.RemoveAll(e => e.FromId == node.Id || e.ToId == node.Id);
        // Deleting a section leaves its cards behind as loose ones (not orphaned pointing at it).
        if (node.Kind == PlanNodeKind.Section)
            foreach (var card in _board.Nodes)
                if (string.Equals(card.SectionId, node.Id, StringComparison.Ordinal)) card.SectionId = null;
        _selected.Remove(node.Id);
        Commit();
        Rebuild();
    }

    /// <summary>Deletes every selected card (and any links touching them). Confirms past a handful,
    /// since the board has no undo.</summary>
    private async Task DeleteSelectedAsync()
    {
        if (_board is null) return;
        var nodes = SelectedNodes();
        if (nodes.Count == 0) return;

        if (nodes.Count > 5 && _owner is not null &&
            !await AppDialog.ConfirmAsync(_owner, "Delete cards",
                $"Delete {nodes.Count} selected cards?", "Delete", "Cancel", danger: true))
            return;

        foreach (var n in nodes)
        {
            _board.Nodes.Remove(n);
            _board.Edges.RemoveAll(x => x.FromId == n.Id || x.ToId == n.Id);
        }
        _selected.Clear();
        Commit();
        Rebuild();
        if (nodes.Count > 1) PlanStatus.Text = $"Deleted {nodes.Count} cards.";
    }

    /// <summary>Echoes the selection size, so shift-clicking a stack of cards gives feedback even
    /// where the accent outlines are hard to count.</summary>
    private void ReportSelection() =>
        PlanStatus.Text = _selected.Count > 1 ? $"{_selected.Count} cards selected" : "";

    /// <summary>"Delete the whole selection" entry, offered only when the right-clicked card is part
    /// of a multi-selection.</summary>
    private MenuItem? MultiDeleteItem(PlanNode node) =>
        _selected.Count > 1 && _selected.Contains(node.Id)
            ? Item($"Delete {_selected.Count} selected cards", () => _ = DeleteSelectedAsync(), gesture: "Del")
            : null;

    // ── selection ───────────────────────────────────────────────────────────────

    /// <summary>Replaces the selection with a single card (or clears it when null).</summary>
    private void SelectOnly(string? id)
    {
        if (id is not null && _selected.Count == 1 && _selected.Contains(id)) return;
        _selected.Clear();
        if (id is not null) _selected.Add(id);
        ApplySelectionVisual();
    }

    /// <summary>Shift+click: adds the card to the selection, or drops it if it was already in.</summary>
    private void ToggleSelected(string id)
    {
        if (!_selected.Remove(id)) _selected.Add(id);
        ApplySelectionVisual();
    }

    private void ClearSelection()
    {
        if (_selected.Count == 0) return;
        _selected.Clear();
        ApplySelectionVisual();
    }

    private List<PlanNode> SelectedNodes() =>
        _board is null
            ? new List<PlanNode>()
            : _selected.Select(id => _board.Node(id)).OfType<PlanNode>().ToList();

    private void ApplySelectionVisual()
    {
        foreach (var (id, el) in _nodeEls)
        {
            var border = el as Border ?? (el as Grid)?.Children.OfType<Border>().FirstOrDefault();
            if (border is null) continue;
            border.BorderBrush = _selected.Contains(id)
                ? Res("AccentBrush")
                : _baseBorder.TryGetValue(id, out var b) ? b : border.BorderBrush;
        }
    }

    // ── toolbar: adding cards ───────────────────────────────────────────────────

    private void OnAddModCard(object sender, RoutedEventArgs e) => _ = AddModCardAsync(ViewCenter());

    /// <summary>Picker over the mods not already on this board, dropping the choice at
    /// <paramref name="at"/> — the view centre from the toolbar, the click point from the canvas menu.</summary>
    private async Task AddModCardAsync(Point at)
    {
        if (_owner is null || _board is null) return;
        var onBoard = _board.Nodes.Where(n => n.Kind == PlanNodeKind.Mod && n.ModKey is not null)
            .Select(n => n.ModKey!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = _mods.Where(m => !m.CandidateKeys.Any(onBoard.Contains)).ToList();
        if (candidates.Count == 0) { PlanStatus.Text = "Every installed mod is already on this board."; return; }

        var pick = await ModPickerDialog.ShowAsync(_owner, "Add a mod card", candidates);
        if (pick is not null) AddModNode(pick, at);
    }

    private void OnAddGroupCard(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = sender as UIElement };
        AddGroupOptionsTo(menu.Items, ViewCenter());
        menu.IsOpen = true;
    }

    /// <summary>Fills a menu with one submenu per mod property, each listing the values present in
    /// this pack. Shared by the toolbar's "Group ▾" and the canvas right-click menu, which differ
    /// only in where the card lands.</summary>
    private void AddGroupOptionsTo(ItemCollection items, Point at)
    {
        foreach (var group in Plans.GroupOptions(_packId, _mods, App.State.ModMetadata))
        {
            if (group.Options.Count == 1 && group.Header == "Everything")
            {
                items.Add(new Separator());
                items.Add(Item("All mods", () => AddGroupNode(group.Options[0], at)));
                continue;
            }
            var parent = new MenuItem { Header = group.Header };
            foreach (var q in group.Options)
            {
                var count = ModPlanService.Resolve(q, _mods).Count;
                var captured = q;
                parent.Items.Add(Item(q.Title, () => AddGroupNode(captured, at), gesture: count.ToString()));
            }
            items.Add(parent);
        }
    }

    // ── categories, managed from the board ──────────────────────────────────────

    /// <summary>Category management inline on the board, so tagging a plan doesn't mean bouncing to
    /// the List view and back. A category created here also gets a group card at the click point —
    /// a brand-new category matches nothing, so otherwise there'd be nothing to show for it.
    /// (Deleting one leaves its card in place showing "nothing matches yet": recreating the category
    /// lights it back up, whereas deleting cards outright couldn't be undone.)</summary>
    private MenuItem BuildCategoriesMenu(Point at) =>
        ModCategoryMenu.Build(_packId, _mods, _owner,
            onChanged: () => { Rebuild(); _onModsChanged?.Invoke(); },
            onCreated: name =>
            {
                AddGroupNode(new PlanGroupQuery { Property = PlanGroupProperty.Category, Value = name }, at);
                PlanStatus.Text = $"Created “{name}” — right-click any mod to tag it in.";
            });

    private void OnAddNoteCard(object sender, RoutedEventArgs e) => AddNoteNode(ViewCenter(), edit: true);

    private void OnAddSection(object sender, RoutedEventArgs e)
    {
        if (_board is null) return;
        var p = FreeSpot(ViewCenter(), SectionW, SectionH);
        var node = new PlanNode { Kind = PlanNodeKind.Section, Title = "Section", X = SnapToGrid(p.X), Y = SnapToGrid(p.Y) };
        _board.Nodes.Add(node);
        Commit();
        Rebuild();
        _ = RenameNodeAsync(node, "Section title");
    }

    private PlanNode? AddModNode(PackMod mod, Point at)
    {
        if (_board is null) return null;
        var (x, y) = ResolveNewCardPosition(at, ModW, ModH, out var sectionId);
        var node = new PlanNode { Kind = PlanNodeKind.Mod, ModKey = mod.Key, X = x, Y = y, SectionId = sectionId };
        _board.Nodes.Add(node);
        Commit();
        Rebuild();
        SelectOnly(node.Id);
        return node;
    }

    private void AddGroupNode(PlanGroupQuery query, Point at)
    {
        if (_board is null) return;
        var (x, y) = ResolveNewCardPosition(at, GroupW, GroupHeaderH + 4 * GroupRowH, out var sectionId);
        var node = new PlanNode { Kind = PlanNodeKind.Group, Query = query.Clone(), X = x, Y = y, SectionId = sectionId };
        _board.Nodes.Add(node);
        Commit();
        Rebuild();
        SelectOnly(node.Id);
    }

    private void AddNoteNode(Point at, bool edit)
    {
        if (_board is null) return;
        var (x, y) = ResolveNewCardPosition(at, NoteW, NoteH, out var sectionId);
        var node = new PlanNode { Kind = PlanNodeKind.Note, X = x, Y = y, SectionId = sectionId };
        _board.Nodes.Add(node);
        Commit();
        Rebuild();
        SelectOnly(node.Id);
        if (edit && _nodeEls.TryGetValue(node.Id, out var el) && el is Border card) BeginEditNote(node, card);
    }

    /// <summary>Where a new card should land for a requested point. If that point is inside a section
    /// (e.g. the view is centred on one, or you right-clicked in it), the card drops into that
    /// section below its existing cards — so new cards land inside the section you're looking at
    /// instead of escaping it. Otherwise it's nudged clear of any overlap near the point.</summary>
    private (double x, double y) ResolveNewCardPosition(Point at, double w, double h, out string? sectionId)
    {
        sectionId = null;
        var section = SectionAt(at);
        if (section is not null && _rect.TryGetValue(section.Id, out var sr))
        {
            // Drop into the shortest column so the card fills a gap before extending the section
            // downward. Then grow the section so the new card is actually inside it — a tightly-hugging
            // section wouldn't otherwise recognise a card placed at (or just past) its bottom edge and
            // would orphan it below the box.
            var cols = ColumnCount(sr);
            var x0 = ContentLeft(sr);
            var step = ColStep(sr);
            var bottoms = Enumerable.Repeat(ContentTop(sr), cols).ToArray();
            foreach (var r in ContainedNodes(section).Select(n => _rect[n.Id]))
            {
                var col = Math.Clamp((int)Math.Round((r.X - x0) / step), 0, cols - 1);
                bottoms[col] = Math.Max(bottoms[col], r.Bottom + SlotGap);
            }
            var targetCol = Array.IndexOf(bottoms, bottoms.Min());
            var x = x0 + targetCol * step; // exact column position (ApplySectionColumnWidths keeps it snapped)
            var y = SnapTo(bottoms[targetCol]);

            var neededHeight = y + h + SectionPad - section.Y;
            if (neededHeight > section.H) section.H = neededHeight;
            sectionId = section.Id;
            return (x, y);
        }
        var p = FreeSpot(at, w, h);
        return SnapAt(w, h, p.X, p.Y);
    }

    /// <summary>The section whose box contains a canvas point, if any.</summary>
    private PlanNode? SectionAt(Point p) => _board?.Nodes.FirstOrDefault(n =>
        n.Kind == PlanNodeKind.Section && _rect.TryGetValue(n.Id, out var r) && r.Contains(p));

    /// <summary>A point guaranteed inside a section (just below its header), so an add routed through
    /// it lands in the section's columns.</summary>
    private Point SectionAnchor(PlanNode section) =>
        _rect.TryGetValue(section.Id, out var r)
            ? new Point(r.X + r.Width / 2, r.Y + SectionHeaderH + 4)
            : new Point(section.X + 20, section.Y + SectionHeaderH + 4);

    /// <summary>Nudges a new card diagonally until it isn't sitting on top of an existing one, but
    /// never so far that it leaves the visible viewport — so a new card always lands on screen.</summary>
    private Point FreeSpot(Point start, double w, double h)
    {
        var p = new Point(start.X - w / 2, start.Y - h / 2);
        var viewBottomRight = new Point(
            (Viewport.ActualWidth - PanT.X) / ZoomT.ScaleX,
            (Viewport.ActualHeight - PanT.Y) / ZoomT.ScaleY);
        for (var i = 0; i < 60; i++)
        {
            var candidate = new Rect(p.X, p.Y, w, h);
            if (!_rect.Values.Any(r => r.IntersectsWith(candidate))) break;
            // Stop nudging once the card would spill past the visible edge; better a slight overlap
            // on screen than a card placed somewhere the user can't see.
            if (p.X + w > viewBottomRight.X || p.Y + h > viewBottomRight.Y) break;
            p.X += 28;
            p.Y += 26;
        }
        return p;
    }

    private Point ViewCenter() => new(
        (Viewport.ActualWidth / 2 - PanT.X) / ZoomT.ScaleX,
        (Viewport.ActualHeight / 2 - PanT.Y) / ZoomT.ScaleY);

    // ── toolbar: tools ──────────────────────────────────────────────────────────

    private void OnTools(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = sender as UIElement };
        menu.Items.Add(Item("Lay out a card per category", () => AutoFill(PlanGroupProperty.Category)));
        menu.Items.Add(Item("Lay out a card per priority", () => AutoFill(PlanGroupProperty.Priority)));
        menu.Items.Add(Item("Lay out a card per flag", () => AutoFill(PlanGroupProperty.Flag)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Add cards for mods with a note",
            () => AddModCardsFor(_mods.Where(m => m.HasNote).ToList(), "with a note")));
        menu.Items.Add(Item("Add cards for mods with updates",
            () => AddModCardsFor(_mods.Where(m => m.HasUpdate).ToList(), "with updates")));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Tidy up (grid the loose cards)", TidyLooseCards));
        menu.Items.Add(Item("Clear this board", () => _ = ClearBoardAsync()));
        menu.IsOpen = true;
    }

    /// <summary>Drops a group card for every value of a property that isn't on the board yet,
    /// flowing them across the canvas — the fastest way to start a plan from scratch.</summary>
    private void AutoFill(PlanGroupProperty property)
    {
        if (_board is null) return;
        var options = Plans.GroupOptions(_packId, _mods, App.State.ModMetadata)
            .SelectMany(g => g.Options)
            .Where(q => q.Property == property)
            .ToList();
        if (options.Count == 0) { PlanStatus.Text = "Nothing to lay out for that property yet."; return; }

        var origin = ViewCenter();
        var cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(options.Count)));
        var startX = origin.X - cols * (GroupW + 40) / 2;
        var startY = origin.Y - 140;
        var added = 0;

        foreach (var q in options)
        {
            if (_board.Nodes.Any(n => n.Kind == PlanNodeKind.Group && n.Query is { } e2
                                      && e2.Property == q.Property
                                      && string.Equals(e2.Value, q.Value, StringComparison.OrdinalIgnoreCase)))
                continue;

            var col = added % cols;
            var row = added / cols;
            _board.Nodes.Add(new PlanNode
            {
                Kind = PlanNodeKind.Group,
                Query = q.Clone(),
                X = SnapTo(startX + col * (GroupW + 40)),
                Y = SnapTo(startY + row * 230)
            });
            added++;
        }

        Commit();
        Rebuild();
        PlanStatus.Text = added == 0 ? "Those cards are already on the board." : $"Added {added} card(s).";
    }

    private void AddModCardsFor(IReadOnlyList<PackMod> mods, string what)
    {
        if (_board is null) return;
        if (mods.Count == 0) { PlanStatus.Text = $"No mods {what}."; return; }

        var origin = ViewCenter();
        var cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(mods.Count)));
        var startX = origin.X - cols * (ModW + 26) / 2;
        var startY = origin.Y - 100;
        var added = 0;

        foreach (var mod in mods)
        {
            if (_board.Nodes.Any(n => n.Kind == PlanNodeKind.Mod && n.ModKey is not null
                                      && mod.CandidateKeys.Contains(n.ModKey, StringComparer.OrdinalIgnoreCase)))
                continue;
            var col = added % cols;
            var row = added / cols;
            _board.Nodes.Add(new PlanNode
            {
                Kind = PlanNodeKind.Mod,
                ModKey = mod.Key,
                X = SnapTo(startX + col * (ModW + 26)),
                Y = SnapTo(startY + row * (ModH + 22))
            });
            added++;
        }

        Commit();
        Rebuild();
        PlanStatus.Text = added == 0 ? "Those mods are already on the board." : $"Added {added} card(s).";
    }

    /// <summary>Re-grids only the cards that aren't inside a section, leaving deliberate arrangements alone.</summary>
    private void TidyLooseCards()
    {
        if (_board is null) return;
        var sections = _board.Nodes.Where(n => n.Kind == PlanNodeKind.Section)
            .Select(n => _rect.TryGetValue(n.Id, out var r) ? r : Rect.Empty).ToList();

        var loose = _board.Nodes
            .Where(n => n.Kind != PlanNodeKind.Section)
            .Where(n => !_rect.TryGetValue(n.Id, out var r)
                        || !sections.Any(s => s.Contains(new Point(r.X + r.Width / 2, r.Y + r.Height / 2))))
            .OrderBy(n => n.Kind)
            .ThenBy(n => n.Y)
            .ThenBy(n => n.X)
            .ToList();
        if (loose.Count == 0) { PlanStatus.Text = "Nothing loose to tidy."; return; }

        var origin = ViewCenter();
        var cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(loose.Count)));
        var startX = origin.X - cols * (GroupW + 34) / 2;
        var startY = origin.Y - 160;
        for (var i = 0; i < loose.Count; i++)
        {
            loose[i].X = SnapTo(startX + (i % cols) * (GroupW + 34));
            loose[i].Y = SnapTo(startY + (i / cols) * 240);
        }

        Commit();
        Rebuild();
        PlanStatus.Text = $"Tidied {loose.Count} card(s).";
    }

    private async Task ClearBoardAsync()
    {
        if (_board is null || _owner is null || _board.IsEmpty) return;
        if (!await AppDialog.ConfirmAsync(_owner, "Clear board",
                $"Remove all {_board.Nodes.Count} card(s) from “{_board.Name}”?", "Clear", "Cancel", danger: true))
            return;
        _board.Nodes.Clear();
        _board.Edges.Clear();
        _selected.Clear();
        Commit();
        Rebuild();
    }

    // ── palette ─────────────────────────────────────────────────────────────────

    private void RefreshPalette()
    {
        var q = PaletteSearch.Text?.Trim();
        IEnumerable<PackMod> list = _mods;
        if (!string.IsNullOrEmpty(q))
            list = list.Where(m => m.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                                   || m.CategoriesLabel.Contains(q, StringComparison.OrdinalIgnoreCase));
        PaletteList.ItemsSource = list.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        PaletteHint.Visibility = string.IsNullOrEmpty(PaletteSearch.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPaletteSearch(object sender, TextChangedEventArgs e) { if (IsLoaded) RefreshPalette(); }

    private void OnTogglePalette(object sender, RoutedEventArgs e)
    {
        var show = PaletteColumn.Width.Value == 0;
        PaletteColumn.Width = show ? new GridLength(212) : new GridLength(0);
        PaletteHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPaletteMouseDown(object sender, MouseButtonEventArgs e) => _paletteDown = e.GetPosition(this);

    private void OnPaletteMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _paletteDown.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _paletteDown.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is not PackMod mod) return;
        DragDrop.DoDragDrop(PaletteList, new DataObject(ModDragFormat, mod.Key), DragDropEffects.Copy);
    }

    private void OnPaletteDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is PackMod mod)
            AddModNode(mod, ViewCenter());
    }

    private void OnViewportDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(ModDragFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnViewportDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(ModDragFormat) is not string key) return;
        var mod = _mods.FirstOrDefault(m => m.CandidateKeys.Contains(key, StringComparer.OrdinalIgnoreCase));
        if (mod is null) return;

        var drop = e.GetPosition(BoardCanvas);
        if (_board?.Nodes.Any(n => n.Kind == PlanNodeKind.Mod && n.ModKey is not null
                                   && mod.CandidateKeys.Contains(n.ModKey, StringComparer.OrdinalIgnoreCase)) == true)
        {
            PlanStatus.Text = $"{mod.DisplayName} is already on this board.";
            return;
        }
        // Drop under the cursor, not centred on it — FreeSpot() recentres by half the card.
        AddModNode(mod, new Point(drop.X + ModW / 2, drop.Y + ModH / 2));
        e.Handled = true;
    }

    // ── pan / zoom / background ─────────────────────────────────────────────────

    private void OnViewportMouseDown(object sender, MouseButtonEventArgs e)
    {
        Viewport.Focus();
        if (ArrowModeActive)
        {
            // Drawing arrows, empty canvas only pans — so a far-off target can still be reached — and a
            // click that doesn't move backs out instead (OnViewportMouseUp). No note or selection box.
            BeginPan(e);
            return;
        }
        if (e.ClickCount == 2) { AddNoteNode(e.GetPosition(BoardCanvas), edit: true); e.Handled = true; return; }

        // Shift on empty canvas draws a selection box instead of panning, and adds to whatever was
        // already selected — so shift is "extend the selection" whether you click a card or sweep
        // an area. Without shift, empty canvas still pans and clears the selection.
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            BeginMarquee(e.GetPosition(BoardCanvas));
            e.Handled = true;
            return;
        }

        ClearSelection();
        PlanStatus.Text = "";
        BeginPan(e);
    }

    private void BeginPan(MouseButtonEventArgs e)
    {
        _panning = true;
        _panStart = e.GetPosition(Viewport);
        _panOrigX = PanT.X;
        _panOrigY = PanT.Y;
        Viewport.CaptureMouse();
        Viewport.Cursor = Cursors.SizeAll;
    }

    private void OnViewportMouseMove(object sender, MouseEventArgs e)
    {
        if (_marquee) { UpdateMarquee(e.GetPosition(BoardCanvas)); return; }
        if (_panning)
        {
            var p = e.GetPosition(Viewport);
            PanT.X = _panOrigX + (p.X - _panStart.X);
            PanT.Y = _panOrigY + (p.Y - _panStart.Y);
            SyncGrid();
        }
        // A half-drawn arrow follows the pointer — over cards too, since their mouse moves bubble up here.
        var at = e.GetPosition(BoardCanvas);
        if (_arrowFrom is not null && _arrowPreview is not null)
            _arrowPreview.Data = LinkPreviewGeometry(_arrowFrom, _arrowFromAnchor, at);
        UpdateAnchorHover(at, e.OriginalSource as DependencyObject);
    }

    private void OnViewportMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_marquee) { EndMarquee(); return; }
        if (!_panning) return;
        _panning = false;
        Viewport.ReleaseMouseCapture();
        UpdateArrowCursor();
        StashViewport();
        // Drawing arrows, a click on empty canvas that didn't pan backs out, the same as Esc.
        if (ArrowModeActive && (e.GetPosition(Viewport) - _panStart).Length < 4) BackOutOfArrowMode();
    }

    // ── rubber-band selection ───────────────────────────────────────────────────

    private void BeginMarquee(Point start)
    {
        _marquee = true;
        _marqueeStart = start;
        // Snapshot the selection so the box is additive and stays live-reversible: shrinking it back
        // off a card deselects that card again without forgetting what was selected beforehand.
        _marqueeBase = new HashSet<string>(_selected);

        var accent = ((SolidColorBrush)Res("AccentBrush")).Color;
        _marqueeEl = new Rectangle
        {
            Stroke = Res("AccentBrush"),
            StrokeThickness = 1 / Math.Max(0.2, ZoomT.ScaleX),  // stays hairline at any zoom
            StrokeDashArray = new DoubleCollection { 3, 2 },
            Fill = new SolidColorBrush(Color.FromArgb(38, accent.R, accent.G, accent.B)),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(_marqueeEl, start.X);
        Canvas.SetTop(_marqueeEl, start.Y);
        Panel.SetZIndex(_marqueeEl, 6);
        BoardCanvas.Children.Add(_marqueeEl);

        Viewport.CaptureMouse();
        Viewport.Cursor = Cursors.Cross;
    }

    private void UpdateMarquee(Point current)
    {
        if (_marqueeEl is null) return;

        var box = new Rect(_marqueeStart, current); // normalises whichever way you drag
        Canvas.SetLeft(_marqueeEl, box.X);
        Canvas.SetTop(_marqueeEl, box.Y);
        _marqueeEl.Width = box.Width;
        _marqueeEl.Height = box.Height;

        _selected.Clear();
        foreach (var id in _marqueeBase) _selected.Add(id);
        if (_board is not null)
            foreach (var node in _board.Nodes)
            {
                // Sections are skipped: sweeping an area would otherwise pick up the backdrop under
                // the cards, and dragging the result would move the section and double-move its cards.
                if (node.Kind == PlanNodeKind.Section) continue;
                if (_rect.TryGetValue(node.Id, out var r) && r.IntersectsWith(box)) _selected.Add(node.Id);
            }

        ApplySelectionVisual();
        ReportSelection();
    }

    private void EndMarquee()
    {
        _marquee = false;
        if (_marqueeEl is not null) { BoardCanvas.Children.Remove(_marqueeEl); _marqueeEl = null; }
        Viewport.ReleaseMouseCapture();
        Viewport.Cursor = Cursors.Arrow;
        ReportSelection();
    }

    private void OnViewportRightClick(object sender, MouseButtonEventArgs e)
    {
        var at = e.GetPosition(BoardCanvas);
        var menu = new ContextMenu { PlacementTarget = Viewport };

        // Right-clicking anywhere inside a section reaches that section, not only its header strip.
        var section = _board?.Nodes.FirstOrDefault(n => n.Kind == PlanNodeKind.Section
                                                        && _rect.TryGetValue(n.Id, out var r) && r.Contains(at));
        if (section is not null)
        {
            var title = section.Title.Length > 0 ? section.Title : "section";
            menu.Items.Add(Item($"Arrange cards in {title}", () => ArrangeSection(section)));
            menu.Items.Add(Item($"Delete {title}", () => RemoveNode(section)));
            menu.Items.Add(Item($"Delete {title} and its cards", () => _ = RemoveSectionWithCardsAsync(section)));
            menu.Items.Add(new Separator());
        }

        // Everything you can put on a board, placed where you clicked rather than at the view centre.
        menu.Items.Add(Item("Add mod card…", () => _ = AddModCardAsync(at)));
        var groupParent = new MenuItem { Header = "Add group card" };
        AddGroupOptionsTo(groupParent.Items, at);
        menu.Items.Add(groupParent);
        menu.Items.Add(Item("Add note here", () => AddNoteNode(at, edit: true)));
        menu.Items.Add(Item("Add section here", () =>
        {
            if (_board is null) return;
            var node = new PlanNode { Kind = PlanNodeKind.Section, Title = "Section", X = SnapToGrid(at.X), Y = SnapToGrid(at.Y) };
            _board.Nodes.Add(node);
            Commit();
            Rebuild();
        }));

        menu.Items.Add(new Separator());
        menu.Items.Add(BuildCategoriesMenu(at));
        menu.Items.Add(Item("Tidy up loose cards", TidyLooseCards));

        var selection = SelectedNodes().Where(n => n.Id != section?.Id).ToList();
        if (selection.Count > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(
                selection.Count == 1 ? "Delete selected card" : $"Delete {selection.Count} selected cards",
                () => _ = DeleteSelectedAsync(), gesture: "Del"));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Fit to view", FitToView));
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OnViewportWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        var m = e.GetPosition(Viewport);
        var oldS = ZoomT.ScaleX;
        var newS = Math.Clamp(oldS * (e.Delta > 0 ? 1.12 : 1 / 1.12), 0.2, 3.0);
        var cx = (m.X - PanT.X) / oldS;
        var cy = (m.Y - PanT.Y) / oldS;
        ZoomT.ScaleX = ZoomT.ScaleY = newS;
        PanT.X = m.X - newS * cx;
        PanT.Y = m.Y - newS * cy;
        SyncGrid();
        StashViewport();
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => ZoomAtCenter(1.2);
    private void OnZoomOut(object sender, RoutedEventArgs e) => ZoomAtCenter(1 / 1.2);

    private void ZoomAtCenter(double factor)
    {
        var m = new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2);
        var oldS = ZoomT.ScaleX;
        var newS = Math.Clamp(oldS * factor, 0.2, 3.0);
        var cx = (m.X - PanT.X) / oldS;
        var cy = (m.Y - PanT.Y) / oldS;
        ZoomT.ScaleX = ZoomT.ScaleY = newS;
        PanT.X = m.X - newS * cx;
        PanT.Y = m.Y - newS * cy;
        SyncGrid();
        StashViewport();
    }

    private void OnFit(object sender, RoutedEventArgs e) => FitToView();

    private void FitToView()
    {
        if (_rect.Count == 0)
        {
            ZoomT.ScaleX = ZoomT.ScaleY = 1;
            PanT.X = PanT.Y = 0;
            SyncGrid();
            StashViewport();
            return;
        }

        double minX = _rect.Values.Min(r => r.X), minY = _rect.Values.Min(r => r.Y);
        double maxX = _rect.Values.Max(r => r.Right), maxY = _rect.Values.Max(r => r.Bottom);
        double w = Math.Max(40, maxX - minX), h = Math.Max(40, maxY - minY);
        double vw = Math.Max(40, Viewport.ActualWidth), vh = Math.Max(40, Viewport.ActualHeight);

        var s = Math.Clamp(Math.Min((vw - 60) / w, (vh - 60) / h), 0.2, 1.5);
        ZoomT.ScaleX = ZoomT.ScaleY = s;
        PanT.X = (vw - w * s) / 2 - minX * s;
        PanT.Y = (vh - h * s) / 2 - minY * s;
        SyncGrid();
        StashViewport();
    }

    /// <summary>Ruled graph-paper grid: one cell per <see cref="GridCell"/> px, drawn as the top and
    /// left edge of each tile.</summary>
    private static DrawingBrush BuildGridBrush()
    {
        var pen = new Pen(Frozen(Color.FromArgb(34, 0x8A, 0x94, 0xA6)), 1);
        pen.Freeze();

        var lines = new GeometryGroup();
        lines.Children.Add(new LineGeometry(new Point(0, 0), new Point(0, GridCell)));
        lines.Children.Add(new LineGeometry(new Point(0, 0), new Point(GridCell, 0)));
        lines.Freeze();

        return new DrawingBrush
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, GridCell, GridCell),
            ViewportUnits = BrushMappingMode.Absolute,
            Drawing = new GeometryDrawing { Pen = pen, Geometry = lines }
        };
    }

    /// <summary>Keeps the grid locked to the board so panning and zooming feel physical.</summary>
    private void SyncGrid()
    {
        var g = new TransformGroup();
        g.Children.Add(new ScaleTransform(ZoomT.ScaleX, ZoomT.ScaleY));
        g.Children.Add(new TranslateTransform(PanT.X, PanT.Y));
        _grid.Transform = g;
    }

    // ── keyboard ────────────────────────────────────────────────────────────────

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Never eat keys while a note card (or any text field) is being edited.
        if (Keyboard.FocusedElement is TextBox) { base.OnPreviewKeyDown(e); return; }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.Z)
        {
            Undo();
            e.Handled = true;
        }
        // Ctrl+Y or Ctrl+Shift+Z both redo (Windows and editor conventions).
        else if ((Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.Y)
                 || (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key is Key.Z))
        {
            Redo();
            e.Handled = true;
        }
        else if (e.Key is Key.Delete or Key.Back && _selected.Count > 0)
        {
            _ = DeleteSelectedAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && ArrowModeActive)
        {
            BackOutOfArrowMode();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _selected.Count > 0)
        {
            ClearSelection();
            PlanStatus.Text = "";
            e.Handled = true;
        }
        else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control && _board is not null)
        {
            _selected.Clear();
            foreach (var n in _board.Nodes) _selected.Add(n.Id);
            ApplySelectionVisual();
            ReportSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && _selected.Count > 0)
        {
            CopySelection();
            e.Handled = true;
        }
        else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && _clipboard is not null)
        {
            PasteClipboard();
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    // ── copy / paste ──────────────────────────────────────────────────────────────
    // A session-wide clipboard (works across boards) holding a snapshot of the copied cards + the
    // links between them.

    private static string? _clipboard;

    private void CopySelection()
    {
        if (_board is null) return;
        // Copying a section brings its member cards along, so paste reproduces the whole group.
        var picked = new HashSet<PlanNode>(SelectedNodes());
        foreach (var n in SelectedNodes().Where(n => n.Kind == PlanNodeKind.Section))
            foreach (var m in ContainedNodes(n)) picked.Add(m);
        if (picked.Count == 0) return;

        var ids = picked.Select(n => n.Id).ToHashSet();
        var edges = _board.Edges.Where(e => ids.Contains(e.FromId) && ids.Contains(e.ToId)).ToList();
        _clipboard = System.Text.Json.JsonSerializer.Serialize(
            new BoardSnapshot(picked.ToList(), edges), HistoryJson);
        PlanStatus.Text = $"Copied {picked.Count} card(s).";
    }

    private void PasteClipboard()
    {
        if (_clipboard is null || _board is null) return;
        var snap = System.Text.Json.JsonSerializer.Deserialize<BoardSnapshot>(_clipboard, HistoryJson);
        if (snap is null || snap.Nodes.Count == 0) return;

        const double offset = GridCell; // nudge so the paste doesn't sit exactly on the source
        var idMap = new Dictionary<string, string>();
        var added = new List<PlanNode>();
        foreach (var n in snap.Nodes)
        {
            var c = n.Clone();      // fresh id
            idMap[n.Id] = c.Id;
            c.X += offset;
            c.Y += offset;
            added.Add(c);
        }
        // Keep membership only when the section was copied too; otherwise the card pastes loose.
        foreach (var c in added)
            c.SectionId = c.SectionId is not null && idMap.TryGetValue(c.SectionId, out var s) ? s : null;
        foreach (var e in snap.Edges)
            if (idMap.TryGetValue(e.FromId, out var f) && idMap.TryGetValue(e.ToId, out var t))
                _board.Edges.Add(e.CopyBetween(f, t));
        _board.Nodes.AddRange(added);

        _selected.Clear();
        foreach (var c in added) _selected.Add(c.Id);

        Commit();
        Rebuild();
        PlanStatus.Text = $"Pasted {added.Count} card(s).";
    }

    // ── small helpers ───────────────────────────────────────────────────────────

    private FontFamily IconFont => (FontFamily)FindResource("IconFont");

    private Brush Res(string key) => (Brush)FindResource(key);

    private Brush ColorOf(PlanNode node, Brush fallback) => ColorOf(node.Color, fallback);

    private static Brush ColorOf(string? hex, Brush fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
        catch { return fallback; }
    }

    private static ToolTip Tip(string text) => new()
    {
        Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 }
    };

    private static void TryAddIcon(Panel host, string? url)
    {
        // Shared, downscaled, cached across rebuilds — see ModIconCache. The mod card icon is 30px.
        if (ModIconCache.Image(url, 30) is { } icon) host.Children.Add(icon);
    }

    private async Task<string?> PromptAsync(string title, string label, string initial)
    {
        if (_owner is not null) return await _owner.PromptAsync(title, label, initial);
        var dlg = new SimpleInputDialog(title, label, initial);
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }

    private static MenuItem Item(string header, Action onClick, string? gesture = null, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, IsEnabled = enabled };
        if (gesture is not null) mi.InputGestureText = gesture;
        mi.Click += (_, _) => onClick();
        return mi;
    }

    private static MenuItem Radio(string header, bool active, Action onClick)
    {
        var mi = new MenuItem { Header = header, InputGestureText = active ? "✓" : "" };
        mi.Click += (_, _) => onClick();
        return mi;
    }

    private static T? FindAncestor<T>(DependencyObject? from) where T : DependencyObject
    {
        for (var d = from; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is T hit) return hit;
        return null;
    }
}
