using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>The Modpack planning sub-tab: a zoomable board of cards you arrange and connect.</summary>
/// <remarks>Four kinds of card: a mod (one specific jar), a group (a live query like "everything
/// tagged Magic" or "priority P3", re-evaluated on every rebuild), a note (free text), and a
/// section (a titled backdrop that carries the cards on it when dragged). Any two cards can be
/// joined by a labelled arrow. Everything is stored in <c>game/.cloudlauncher/plans.json</c> via
/// <see cref="ModPlanService"/>, so boards sync with the pack like mod flags do.</remarks>
public partial class ModPlanView : UserControl
{
    // Card geometry. Sizes are explicit rather than measured so edge endpoints can be computed
    // before layout runs, as in the graph view. Every default is a whole number of grid cells, so a
    // card whose corner snaps to the grid has all four edges on it.
    private const double ModW = 216, ModH = 72, ModNoteH = 96;             // 9 x 3 cells, 9 x 4 with a note line
    private const double GroupW = 240, GroupHeaderH = 50, GroupRowH = 20;  // 10 cells wide; the height rounds up to whole cells
    private const int GroupMaxRows = 8;
    private const double NoteW = 240, NoteH = 144;                         // 10 x 6 cells
    // A note's progress bar and task list sit below its text at fixed heights, so its size is known
    // before layout like every other card's.
    private const double NoteProgressH = 26, NoteTasksHeaderH = 28, NoteTaskRowH = 22, NoteAddTaskRowH = 24, NoteTasksPad = 6;
    private const double NoteCornerW = 26;   // one top-right corner control plus its gap
    private const double NoteBodyLineH = 16; // one line of description: shorter than this under the title and only the title shows
    private const double MinCardW = 144;     // the narrowest a note resizes to: six cells
    private const double SectionW = 552, SectionH = 216; // two columns (23 x 24 wide), 9 x 24 tall
    private const double MinSectionH = 144;

    // Inside a section, cards drop into fixed columns instead of free space, which keeps sections
    // tidy without hand-aligning. One column fits the widest card kind; the default section fits
    // two. All measures are whole cells so a section on the grid keeps its cards on it: a cell of
    // padding at the sides and bottom, cards starting two cells down (under the title strip), and
    // 10-cell columns a cell apart, making a section cols x 264 + 24 wide.
    private const double SectionHeaderH = 30, SectionContentTop = 48, SectionPad = 24;
    private const double SlotColW = 240, SlotGap = 24;
    private const double SlotPitch = SlotColW + SlotGap;
    private const double MinSectionW = SlotColW + SectionPad * 2; // one column

    /// <summary>Slightly translucent at rest so the grid reads faintly through it; fully opaque while
    /// a card is being dragged over it.</summary>
    private const double SectionRestOpacity = 0.93;

    /// <summary>Size of one square on the ruled backdrop. While the grid is on, every position and
    /// size on the board is a whole number of these, so cards line up with the grid and with each
    /// other.</summary>
    private const double GridCell = 24;

    /// <summary>Every this-many grid lines, one is drawn a shade stronger.</summary>
    private const int GridMajorEvery = 4;

    private const string ModDragFormat = "CloudLauncher.PlanModKey";

    /// <summary>How near a card's outline (screen px, outside / inside it) an arrow end pins to that exact
    /// spot instead of attaching automatically.</summary>
    private const double OutlineOuterPx = 9, OutlineInnerPx = 5;

    /// <summary>Tags elements whose own press handling beats outline pinning (the link dot, resize
    /// grips).</summary>
    private static readonly object NoAnchorTag = new();

    private static readonly Brush EdgeBrush = Frozen(Color.FromArgb(170, 0x6B, 0x76, 0x88));
    private static readonly Brush ArrowBrush = Frozen(Color.FromArgb(230, 0x93, 0x9E, 0xB2));
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private Guid _packId;
    private IReadOnlyList<PackMod> _mods = Array.Empty<PackMod>();
    private MainWindow? _owner;
    private Action<PackMod>? _onOpenMod;
    private Action? _onModsChanged;
    private Action? _onReload;
    private Action<IReadOnlyList<PackMod>>? _onUpdate;
    private Action<IReadOnlyList<PackMod>>? _onRecheckUpdates;
    private PlanBoard? _board;
    private bool _loaded;

    private readonly Dictionary<string, FrameworkElement> _nodeEls = new();
    private readonly Dictionary<string, Brush> _baseBorder = new();
    private readonly Dictionary<string, Rect> _rect = new();
    private readonly Dictionary<string, Border> _sectionBoxes = new();
    private readonly Dictionary<string, TextBlock> _sectionCounts = new();
    private string? _hoverSectionId;
    private readonly List<UIElement> _edgeEls = new();
    private readonly GridLines _gridLines = new();
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
    private bool _arrowTool;        // toolbar toggle; stays on so a whole map can be connected in one go
    private PlanNode? _arrowFrom;   // start card picked, waiting for the target click
    private PlanAnchor? _arrowFromAnchor;
    private Path? _arrowPreview;

    // outline points: near a card's outline, an arrow end pins to the exact spot under the pointer
    private Ellipse? _anchorMarker;
    private bool _anchorHover;      // the pointer is on an outline, so pressing here draws a pinned arrow
    private bool _swallowArrowUp;   // the release of a press the arrow tool used mustn't click the card too

    // palette drag
    private Point _paletteDown;

    // ── rail state ──────────────────────────────────────────────────────────────

    /// <summary>Board rows in drawing order, each with the container it was drawn into. Drag
    /// calculations use these rather than whatever the pointer hit, since a row's name TextBlock is
    /// a different rectangle from the row.</summary>
    private readonly List<(FrameworkElement Row, PlanBoard Board)> _railRows = new();
    private readonly List<(FrameworkElement Row, string? Category)> _railHeaders = new();

    /// <summary>Categories the user has folded, by name rather than index, so a rename or reorder
    /// doesn't fold a different one.</summary>
    private readonly HashSet<string> _railCollapsed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True while an inline name box is open in the rail, which then owns it until the edit
    /// finishes: a canvas rebuild redrawing the rail underneath would take the box with it.</summary>
    private bool _railEditing;

    // rail drag-reorder
    private PlanBoard? _railPressBoard;
    private FrameworkElement? _railPressRow;
    private Point _railPressAt;
    private RailInsertion? _railLine;
    private string? _railDropCategory;
    private PlanBoard? _railDropAfter;

    public ModPlanView()
    {
        InitializeComponent();
        GridBackdrop.Child = _gridLines;
        GridToggle.IsChecked = GridOn;
        Viewport.SizeChanged += (_, _) => SyncGrid();
        // The grid takes its colour from the theme, so it follows a change of look. Subscribed only while
        // loaded: the event is static, and a handler added here would keep this view alive for the life
        // of the process.
        Loaded += (_, _) =>
        {
            ThemeService.Changed -= OnThemeChanged;
            ThemeService.Changed += OnThemeChanged;
            OnThemeChanged();
        };
        Unloaded += (_, _) => ThemeService.Changed -= OnThemeChanged;
        // Leaving the board mid-hover mustn't strand the outline marker or the crosshair.
        Viewport.MouseLeave += (_, _) =>
        {
            if (Mouse.Captured is not null) return; // a drag in progress still owns them
            HideAnchorMarker();
            if (_anchorHover) { _anchorHover = false; UpdateArrowCursor(); }
        };
    }

    /// <summary>Binds the board to a pack's live mod list. Safe to call repeatedly; later calls
    /// re-resolve the cards against the refreshed inventory.</summary>
    /// <param name="onReload">Re-scans the pack's mods after something changed the files on disk (a
    /// delete, or installing a different version).</param>
    /// <param name="onUpdate">Installs the newest version of the given mods, using the List view's
    /// implementation so "Update to newest" behaves the same here.</param>
    /// <param name="onRecheckUpdates">Re-runs the update check for the given mods after something
    /// changed what counts as an update for them (their channel, or the store they follow).</param>
    public void Load(Guid packId, IReadOnlyList<PackMod> mods, MainWindow? owner,
        Action<PackMod>? onOpenMod = null, Action? onModsChanged = null, Action? onReload = null,
        Action<IReadOnlyList<PackMod>>? onUpdate = null, Action<IReadOnlyList<PackMod>>? onRecheckUpdates = null)
    {
        _packId = packId;
        _mods = mods;
        _owner = owner;
        _onOpenMod = onOpenMod;
        _onModsChanged = onModsChanged;
        _onReload = onReload;
        _onUpdate = onUpdate;
        _onRecheckUpdates = onRecheckUpdates;

        var first = !_loaded;
        _loaded = true;
        _board = App.State.ModPlans.CurrentBoard(packId);
        // The Grid box is one setting for every board, so another pack's board may have changed it.
        GridToggle.IsChecked = GridOn;

        RefreshBoardRail();
        RefreshPalette();
        if (first) RestoreViewport();
        SyncGrid();
        Rebuild();
        HintIfOffGrid();
    }

    /// <summary>Points at Tools > Snap everything to the grid when the open board has cards between
    /// grid lines (laid out with the grid off, or before the grid used whole cells).</summary>
    /// <remarks>Only a hint: moving cards on a board the whole pack shares is left to whoever wants
    /// it, as one undoable step.</remarks>
    private void HintIfOffGrid()
    {
        if (!GridOn || _board is null) return;
        static bool Off(double v) => Math.Abs(v - Math.Round(v / GridCell) * GridCell) > 0.5;
        var off = _rect.Values.Count(r => Off(r.X) || Off(r.Y));
        if (off == 0) return;
        PlanStatus.Text = "Some cards sit between grid lines - Tools ▾ Snap everything";
        PlanStatus.ToolTip = $"{off} card(s) on this board sit between the grid lines, from before the grid "
                             + "snapped to whole cells. Tools ▾ Snap everything to the grid lines them up in one "
                             + "step, and Ctrl+Z puts them back.";
    }

    private ModPlanService Plans => App.State.ModPlans;

    /// <summary>Whether cards snap to the grid, which is then drawn. A personal editing preference that
    /// holds for every board, so it is a launcher setting rather than something saved on the (shared)
    /// board: with it off, positions and sizes are free to the pixel.</summary>
    private static bool GridOn => App.State.Settings.PlanGridEnabled;
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
    // Snapshot history of the board's nodes and edges (JSON, so ids and edges survive intact). Pan,
    // zoom and section auto-height aren't part of it; they're view or derived state.

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

    /// <summary>Starts the undo history fresh at the board's current state (called when a board
    /// opens).</summary>
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

    // ── the board rail ──────────────────────────────────────────────────────────
    //
    // The left rail is board management: every board, grouped under its category, with the open one
    // highlighted and its sections listed beneath as jump targets. Creating, renaming, reordering,
    // regrouping and deleting all happen in place.

    /// <summary>The rail's width when it is showing. Also what the toolbar's toggle restores.</summary>
    private const double RailWidth = 230;

    /// <summary>Gap under each rail row. Named because the insertion line is drawn into it.</summary>
    private const double RailRowGap = 2;

    private const string BoardDragFormat = "CloudLauncher.PlanBoardId";

    /// <summary>Redraws the rail from the service.</summary>
    /// <remarks>Guarded as a whole because the rail is rebuilt on every canvas rebuild, and one
    /// board that can't draw its row mustn't take the canvas down with it.</remarks>
    private void RefreshBoardRail()
    {
        if (!IsInitialized || _railEditing) return; // an open name box owns the rail until it commits
        try { BuildBoardRail(); }
        catch (Exception ex) { AppLog.LogError(nameof(ModPlanView), ex); }
    }

    private void BuildBoardRail()
    {
        RailList.Children.Clear();
        _railRows.Clear();
        _railHeaders.Clear();

        var boards = Plans.Boards(_packId).ToList();
        var categories = Plans.BoardCategories(_packId).ToList();

        BoardTitle.Text = _board?.Name ?? "";
        RailSummary.Text = boards.Count == 1 ? "1 board" : $"{boards.Count} boards";

        // Grouping only appears once there is something to group by. With no categories the rail is
        // a plain list of boards rather than one "Uncategorized" heading over everything.
        if (categories.Count == 0)
        {
            foreach (var b in boards) AddBoardRow(b, boards.Count, indent: 0);
        }
        else
        {
            foreach (var cat in categories)
                AddGroup(cat, cat, boards.Where(b => SameCat(b.Category, cat)).ToList());

            var loose = boards.Where(b => !categories.Any(c => SameCat(c, b.Category))).ToList();
            if (loose.Count > 0) AddGroup(null, "Uncategorized", loose);
        }

        RailNewChips.Content = NewChips();

        void AddGroup(string? key, string label, List<PlanBoard> items)
        {
            var header = CategoryHeader(key, label, items.Count, categories);
            _railHeaders.Add((header, key));
            RailList.Children.Add(header);

            if (key is not null && _railCollapsed.Contains(key) && !HoldsCurrentBoard(key)) return;
            if (items.Count == 0)
            {
                RailList.Children.Add(new TextBlock
                {
                    Text = "no boards yet - drag one here",
                    FontSize = 10.5, FontStyle = FontStyles.Italic, TextWrapping = TextWrapping.Wrap,
                    Foreground = Res("TextTertiaryBrush"), Margin = new Thickness(14, 0, 0, 6)
                });
                return;
            }
            foreach (var b in items) AddBoardRow(b, boards.Count, indent: 10);
        }
    }

    /// <summary>A folded category still opens when the current board is inside it, since the rail's
    /// first job is showing where you are.</summary>
    private bool HoldsCurrentBoard(string category) => _board is not null && SameCat(_board.Category, category);

    private void AddBoardRow(PlanBoard board, int boardCount, double indent)
    {
        var row = BoardRow(board, boardCount, indent);
        _railRows.Add((row, board));
        RailList.Children.Add(row);
        // The open board's sections hang off it, so the rail reads as "here, and here is what is on it".
        if (ReferenceEquals(board, _board)) AddSectionRows(indent);
    }

    /// <summary>One board. The accent edge and filled background are the main "which board am I on"
    /// cue, so they are kept strong.</summary>
    private FrameworkElement BoardRow(PlanBoard board, int boardCount, double indent)
    {
        var current = ReferenceEquals(board, _board);
        var rest = current ? Res("Surface3Brush") : Brushes.Transparent;

        var row = new CloudLauncher.Controls.SlateBorder
        {
            Background = rest,
            BorderBrush = current ? Res("AccentBrush") : Brushes.Transparent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(indent, 0, 0, RailRowGap),
            Padding = new Thickness(0, 0, 7, 0),
            Cursor = Cursors.Hand,
            ToolTip = Tip(current
                ? $"{board.Name} - double-click to rename, drag to reorder, right-click for everything else"
                : $"{board.Name} - click to open, drag to reorder, right-click for everything else")
        };
        HoverFill(row, rest);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new CloudLauncher.Controls.SlateBorder
        {
            Background = current ? Res("AccentBrush") : Brushes.Transparent,
            CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 5, 8, 5)
        });

        var name = new TextBlock
        {
            Text = board.Name, FontSize = 12.5,
            FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = Res(current ? "TextPrimaryBrush" : "TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 6, 6, 6)
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        // How much is on a board at a glance, like the status line shows for the open one.
        var count = new TextBlock
        {
            Text = board.Nodes.Count == 0 ? "" : board.Nodes.Count.ToString(),
            FontSize = 10.5, Foreground = Res("TextTertiaryBrush"), VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(count, 2);
        grid.Children.Add(count);

        row.Child = grid;

        row.MouseLeftButtonUp += (_, e) =>
        {
            // Rename is a double-click on the board you're already on. Switching redraws the rail
            // and removes this row, so a double-click on another board's row would never arrive
            // here as ClickCount 2.
            if (e.ClickCount == 2 && ReferenceEquals(board, _board)) { e.Handled = true; BeginRenameBoard(board); return; }
            SwitchToBoard(board);
        };
        row.MouseRightButtonUp += (_, e) => { e.Handled = true; ShowBoardMenu(board, row, boardCount); };

        row.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _railPressBoard = board;
            _railPressRow = row;
            _railPressAt = e.GetPosition(this);
        };
        row.PreviewMouseLeftButtonUp += (_, _) => ForgetRailPress();
        row.MouseLeave += (_, _) => ForgetRailPress();
        row.PreviewMouseMove += OnRailRowMove;
        return row;
    }

    private FrameworkElement CategoryHeader(string? key, string label, int count, List<string> categories)
    {
        var folded = key is not null && _railCollapsed.Contains(key) && !HoldsCurrentBoard(key);

        var row = new CloudLauncher.Controls.SlateBorder
        {
            Background = Brushes.Transparent,
            Margin = new Thickness(0, _railHeaders.Count == 0 ? 0 : 9, 0, 3),
            Padding = new Thickness(2, 2, 5, 2),
            Cursor = key is null ? Cursors.Arrow : Cursors.Hand,
            ToolTip = Tip(key is null
                ? "Boards that aren't in a category - drop one here to take it out of the one it is in"
                : $"{label} - click to fold, right-click to rename, reorder or delete")
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // No chevron on "Uncategorized": it's a synthetic group for whatever is left over, not a
        // real category. The slot keeps its width so that heading lines up with the others.
        grid.Children.Add(new TextBlock
        {
            Text = key is null ? "" : char.ConvertFromUtf32(folded ? 0xE76C : 0xE70D), // ChevronRight / ChevronDown
            FontFamily = IconFont, FontSize = 9, Width = 14,
            Foreground = Res("TextTertiaryBrush"), VerticalAlignment = VerticalAlignment.Center
        });

        var title = new TextBlock
        {
            Text = label.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeights.SemiBold,
            Foreground = Res("TextTertiaryBrush"), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);

        var tally = new TextBlock
        {
            Text = count.ToString(), FontSize = 10.5, Foreground = Res("TextTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0)
        };
        Grid.SetColumn(tally, 2);
        grid.Children.Add(tally);

        row.Child = grid;

        if (key is not null)
        {
            HoverFill(row, Brushes.Transparent);
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (!_railCollapsed.Remove(key)) _railCollapsed.Add(key);
                RefreshBoardRail();
            };
            row.MouseRightButtonUp += (_, e) => { e.Handled = true; ShowCategoryMenu(key, row, categories); };
        }
        return row;
    }

    /// <summary>The open board's sections, indented under its row. Sections are the named things on
    /// a board, so the rail offers them as jump targets.</summary>
    private void AddSectionRows(double indent)
    {
        if (_board is null) return;
        foreach (var section in _board.Nodes.Where(n => n.Kind == PlanNodeKind.Section)
                     .OrderBy(n => n.Y).ThenBy(n => n.X).ToList())
            RailList.Children.Add(SectionRow(section, indent));
    }

    private FrameworkElement SectionRow(PlanNode section, double indent)
    {
        var title = section.Title.Length > 0 ? section.Title : "Section";
        var cards = ContainedNodes(section).Count;

        var row = new CloudLauncher.Controls.SlateBorder
        {
            Background = Brushes.Transparent,
            Margin = new Thickness(indent + 13, 0, 0, RailRowGap),
            Padding = new Thickness(0, 2, 7, 2),
            CornerRadius = new CornerRadius(5),
            Cursor = Cursors.Hand,
            ToolTip = Tip($"{title} - take the board to this section")
        };
        HoverFill(row, Brushes.Transparent);
        row.MouseLeftButtonUp += (_, e) => { e.Handled = true; JumpToSection(section); };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // The section's own colour, so the rail row and the region on the canvas are recognisably the
        // same thing. A swatch rather than a glyph: sections are told apart by colour on the board.
        grid.Children.Add(new CloudLauncher.Controls.SlateBorder
        {
            Width = 7, Height = 7, CornerRadius = new CornerRadius(2),
            Background = ColorOf(section, Res("BorderStrongBrush")),
            Margin = new Thickness(2, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center
        });

        var label = new TextBlock
        {
            Text = title, FontSize = 11.5, Foreground = Res("TextTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        var tally = new TextBlock
        {
            Text = cards == 0 ? "" : cards.ToString(), FontSize = 10,
            Foreground = Res("TextTertiaryBrush"), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0)
        };
        Grid.SetColumn(tally, 2);
        grid.Children.Add(tally);

        row.Child = grid;
        return row;
    }

    /// <summary>Centres the canvas on a section at the current zoom and selects it so it's clear
    /// where you landed. Zoom is left alone: Fit changes scale, and a jump that also rescaled would
    /// lose your working scale.</summary>
    private void JumpToSection(PlanNode section)
    {
        if (!_rect.TryGetValue(section.Id, out var r)) return; // not rendered, nothing to aim at
        var s = ZoomT.ScaleX;
        PanT.X = Viewport.ActualWidth / 2 - (r.X + r.Width / 2) * s;
        PanT.Y = Viewport.ActualHeight / 2 - (r.Y + r.Height / 2) * s;
        SyncGrid();
        StashViewport();
        SelectOnly(section.Id);
        PlanStatus.Text = $"Jumped to {(section.Title.Length > 0 ? section.Title : "the section")}.";
    }

    /// <summary>The two create buttons, pinned under the list, like the "New folder" chip at the
    /// end of each content page's strip.</summary>
    /// <remarks>Not the shared <c>FolderChipBorder</c> style: its new-action trigger draws a bare
    /// plus in a circle, which would give two identical unlabelled circles here. A
    /// <see cref="WrapPanel"/> so the pair sits on one line in a wide rail and stacks in a narrow
    /// one instead of clipping.</remarks>
    private FrameworkElement NewChips()
    {
        var wrap = new WrapPanel();
        wrap.Children.Add(NewChip("New board", "Add a board and open it - it joins the category you are in",
            () => BeginNewBoard()));
        wrap.Children.Add(NewChip("New category", "Add a heading to group boards under in this rail",
            BeginNewCategory));
        return wrap;
    }

    private FrameworkElement NewChip(string label, string tip, Action click)
    {
        var chip = new CloudLauncher.Controls.SlateBorder
        {
            Background = Res("Surface3Brush"),
            BorderBrush = Res("BorderStrongBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            // Tuned by eye: at these metrics the pair fits on one line in the 230px rail, which
            // saves 31px of list height compared to wrapping.
            Padding = new Thickness(8, 3, 10, 3),
            Margin = new Thickness(0, 0, 5, 5),
            Cursor = Cursors.Hand,
            ToolTip = Tip(tip)
        };
        chip.MouseEnter += (_, _) => chip.BorderBrush = Res("TextPrimaryBrush");
        chip.MouseLeave += (_, _) => chip.BorderBrush = Res("BorderStrongBrush");
        chip.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };

        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock
        {
            Text = char.ConvertFromUtf32(0xE710), // Add
            FontFamily = IconFont, FontSize = 10, Foreground = Res("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0)
        });
        sp.Children.Add(new TextBlock
        {
            Text = label, FontSize = 11, Foreground = Res("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        chip.Child = sp;
        return chip;
    }

    // ── rail: inline editing ────────────────────────────────────────────────────

    /// <summary>Opens a name box in the rail at <paramref name="at"/>.</summary>
    /// <remarks>Same rules for every one: Enter commits, Esc cancels, and clicking away commits
    /// whatever is in the box (empty commits nothing), as note cards on the canvas do.</remarks>
    private void BeginRailEdit(int at, string caption, string initial, Action<string> commit)
    {
        if (_railEditing) return;
        _railEditing = true;

        var box = new TextBox { Text = initial, FontSize = 12.5, Padding = new Thickness(6, 3, 6, 3) };
        var host = new StackPanel { Margin = new Thickness(0, 1, 0, RailRowGap) };
        host.Children.Add(new TextBlock
        {
            Text = caption, FontSize = 10, Foreground = Res("TextTertiaryBrush"),
            Margin = new Thickness(2, 0, 0, 3)
        });
        host.Children.Add(box);
        RailList.Children.Insert(Math.Clamp(at, 0, RailList.Children.Count), host);
        // A new board's box goes at the end of a list that may be scrolled, so bring it into view.
        RailScroll.UpdateLayout();
        host.BringIntoView();

        var done = false;
        void Finish(bool keep)
        {
            if (done) return;
            done = true;
            _railEditing = false;
            var text = box.Text.Trim();
            try { if (keep && text.Length > 0) commit(text); }
            catch (Exception ex) { AppLog.LogError(nameof(ModPlanView), ex); }
            RefreshBoardRail(); // whatever happened, the rail is redrawn from the service
        }

        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Finish(true); }
            else if (e.Key == Key.Escape) { e.Handled = true; Finish(false); }
        };
        box.LostKeyboardFocus += (_, _) => Finish(true);

        Dispatcher.BeginInvoke(new Action(() => { box.Focus(); box.SelectAll(); }),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Adds a board without leaving the rail.</summary>
    /// <param name="category">The category it joins, or null for the open board's category, so a
    /// board made while working in "Magic" lands in "Magic".</param>
    private void BeginNewBoard(string? category = null)
    {
        var home = category ?? _board?.Category;
        BeginRailEdit(RailList.Children.Count, "New board", "Plan", name =>
        {
            var created = Plans.AddBoard(_packId, name);
            if (!string.IsNullOrEmpty(home)) Plans.SetBoardCategory(_packId, created, home);
            SwitchToBoard(created); // you made it to use it
            PlanStatus.Text = $"Added board '{created.Name}'.";
        });
    }

    private void BeginNewCategory() =>
        BeginRailEdit(RailList.Children.Count, "New category", "", name =>
        {
            Plans.AddBoardCategory(_packId, name);
            PlanStatus.Text = $"Added category '{name}'.";
        });

    /// <summary>New category, made from a board's own menu, which then moves into it.</summary>
    private void BeginNewCategoryFor(PlanBoard board) =>
        BeginRailEdit(RailList.Children.Count, "New category", "", name =>
            Plans.SetBoardCategory(_packId, board, name));

    private void BeginRenameBoard(PlanBoard board)
    {
        if (_railEditing) return;
        // The box takes the row's place rather than sitting above it, so the list does not jump.
        var at = RailList.Children.Count;
        if (RailRowOf(board) is { } row && RailList.Children.IndexOf(row) is var i && i >= 0)
        {
            at = i;
            RailList.Children.RemoveAt(i);
        }
        BeginRailEdit(at, "Rename board", board.Name, name =>
        {
            Plans.RenameBoard(_packId, board, name);
            BoardTitle.Text = _board?.Name ?? "";
        });
    }

    private void BeginRenameCategory(string category)
    {
        if (_railEditing) return;
        var at = RailList.Children.Count;
        foreach (var (header, key) in _railHeaders)
        {
            if (!SameCat(key, category)) continue;
            var i = RailList.Children.IndexOf(header);
            if (i >= 0) { at = i; RailList.Children.RemoveAt(i); }
            break;
        }
        BeginRailEdit(at, "Rename category", category, name =>
        {
            Plans.RenameBoardCategory(_packId, category, name);
            if (_railCollapsed.Remove(category)) _railCollapsed.Add(name); // the fold follows the name
        });
    }

    // ── rail: menus ─────────────────────────────────────────────────────────────

    private void ShowBoardMenu(PlanBoard board, FrameworkElement anchor, int boardCount)
    {
        var siblings = Plans.Boards(_packId).Where(b => SameCat(b.Category, board.Category)).ToList();
        var pos = siblings.IndexOf(board);

        var menu = new ContextMenu { PlacementTarget = anchor };
        menu.Items.Add(Item("Open", () => SwitchToBoard(board), enabled: !ReferenceEquals(board, _board)));
        menu.Items.Add(Item("Rename", () => BeginRenameBoard(board)));
        menu.Items.Add(Item("Duplicate", () => SwitchToBoard(Plans.DuplicateBoard(_packId, board))));
        menu.Items.Add(MoveToCategoryMenu(board));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Move up", () => MoveBoardBy(board, -1), enabled: pos > 0));
        menu.Items.Add(Item("Move down", () => MoveBoardBy(board, 1), enabled: pos >= 0 && pos < siblings.Count - 1));
        menu.Items.Add(new Separator());
        // The last board can't be deleted: the service would create a replacement right away, so
        // the entry is greyed out instead.
        menu.Items.Add(Item("Delete board", () => _ = DeleteBoardAsync(board), enabled: boardCount > 1));
        menu.IsOpen = true;
    }

    private MenuItem MoveToCategoryMenu(PlanBoard board)
    {
        var menu = new MenuItem { Header = "Move to category" };
        foreach (var cat in Plans.BoardCategories(_packId))
        {
            var captured = cat;
            menu.Items.Add(Radio(cat, SameCat(board.Category, captured),
                () => { Plans.SetBoardCategory(_packId, board, captured); RefreshBoardRail(); }));
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        menu.Items.Add(Radio("Uncategorized", string.IsNullOrEmpty(board.Category),
            () => { Plans.SetBoardCategory(_packId, board, null); RefreshBoardRail(); }));
        menu.Items.Add(Item("New category...", () => BeginNewCategoryFor(board)));
        return menu;
    }

    private void ShowCategoryMenu(string category, FrameworkElement anchor, List<string> categories)
    {
        var index = categories.FindIndex(c => SameCat(c, category));

        var menu = new ContextMenu { PlacementTarget = anchor };
        menu.Items.Add(Item("New board here", () => BeginNewBoard(category)));
        menu.Items.Add(Item("Rename category", () => BeginRenameCategory(category)));
        menu.Items.Add(new Separator());
        // MoveBoardCategory takes a position in the list as it stands, so "down" is two places on.
        menu.Items.Add(Item("Move up", () => { Plans.MoveBoardCategory(_packId, category, index - 1); RefreshBoardRail(); },
            enabled: index > 0));
        menu.Items.Add(Item("Move down", () => { Plans.MoveBoardCategory(_packId, category, index + 2); RefreshBoardRail(); },
            enabled: index >= 0 && index < categories.Count - 1));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Delete category (boards stay)", () =>
        {
            Plans.RemoveBoardCategory(_packId, category);
            _railCollapsed.Remove(category);
            RefreshBoardRail();
        }));
        menu.IsOpen = true;
    }

    /// <summary>Nudges a board one place within its own category.</summary>
    /// <remarks>The index passed to the service is a position in the current list, and
    /// <see cref="ModPlanService.MoveBoard"/> accounts for the removal itself, so moving down
    /// passes the neighbour's index plus one and moving up passes it unchanged.</remarks>
    private void MoveBoardBy(PlanBoard board, int direction)
    {
        var boards = Plans.Boards(_packId).ToList();
        var siblings = boards.Where(b => SameCat(b.Category, board.Category)).ToList();
        var pos = siblings.IndexOf(board);
        var swap = pos + direction;
        if (pos < 0 || swap < 0 || swap >= siblings.Count) return;

        var neighbour = boards.IndexOf(siblings[swap]);
        Plans.MoveBoard(_packId, board, direction < 0 ? neighbour : neighbour + 1);
        RefreshBoardRail();
    }

    private async Task DeleteBoardAsync(PlanBoard board)
    {
        if (!await AppDialog.ConfirmAsync(_owner, "Delete board",
                $"Delete '{board.Name}' and everything on it?", "Delete", "Cancel", danger: true)) return;

        var wasCurrent = ReferenceEquals(board, _board);
        Plans.RemoveBoard(_packId, board);
        if (!wasCurrent) { RefreshBoardRail(); return; }

        // CurrentBoard() creates a fresh board if the last one just went, so the canvas never
        // points at a board that no longer exists. _board is cleared first so the switch happens
        // even when the replacement is the same object.
        _board = null;
        SwitchToBoard(Plans.CurrentBoard(_packId));
    }

    // ── rail: drag to reorder ───────────────────────────────────────────────────
    //
    // Same rules as the content pages' row drag: the drag is anchored to the row container rather than
    // whatever the pointer hit, the insert point comes from geometry rather than a hit test (the gaps
    // between rows and the space under the last one count), the insertion line isn't hit-testable, and
    // a drag needs vertical intent, since these rows switch board on click and a sideways twitch
    // shouldn't lift a board out of the list.

    private void ForgetRailPress()
    {
        _railPressBoard = null;
        _railPressRow = null;
    }

    private void OnRailRowMove(object sender, MouseEventArgs e)
    {
        if (_railPressBoard is not { } board || _railPressRow is not { } row) return;
        if (e.LeftButton != MouseButtonState.Pressed) { ForgetRailPress(); return; }

        var now = e.GetPosition(this);
        if (Math.Abs(now.Y - _railPressAt.Y) < Math.Max(6, SystemParameters.MinimumVerticalDragDistance)) return;

        // The press has to have landed on the row that is about to travel: a rebuild can finish under
        // a held button and slide a different board under the cursor.
        var origin = TranslatePoint(_railPressAt, row);
        if (origin.Y < 0 || origin.Y > row.ActualHeight) { ForgetRailPress(); return; }

        // Cleared before the blocking DoDragDrop, which pumps messages: a handler running during
        // the drag would otherwise see a stale press and start a second drag.
        ForgetRailPress();
        try { DragDrop.DoDragDrop(row, new DataObject(BoardDragFormat, board.Id), DragDropEffects.Move); }
        finally { ClearRailInsertion(); }
    }

    private void OnRailDragOver(object sender, DragEventArgs e)
    {
        // A mod dragged out of the palette below is not a board: say so rather than drawing an
        // insertion line for a drop the rail would not accept.
        if (!e.Data.GetDataPresent(BoardDragFormat)) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        ComputeRailDrop(RailContentY(e.GetPosition(RailScroll).Y));
    }

    /// <summary>The pointer left the rail, but only if it really did.</summary>
    /// <remarks>DragLeave is raised on the element being left, so moving from one row to the next
    /// arrives here too. Clearing on each of those causes flicker.</remarks>
    private void OnRailDragLeave(object sender, DragEventArgs e)
    {
        var at = e.GetPosition(RailScroll);
        if (at.X >= 0 && at.Y >= 0 && at.X <= RailScroll.ActualWidth && at.Y <= RailScroll.ActualHeight) return;
        ClearRailInsertion();
    }

    private void OnRailDrop(object sender, DragEventArgs e)
    {
        var cat = _railDropCategory;
        var after = _railDropAfter;
        ClearRailInsertion();

        if (e.Data.GetData(BoardDragFormat) is not string id) return;
        var board = Plans.Boards(_packId).FirstOrDefault(b => b.Id == id);
        if (board is null) return;
        e.Handled = true;

        try
        {
            Plans.SetBoardCategory(_packId, board, cat); // assign + regroup

            // Re-read: the service rebuilds the flat list when it regroups, so an index taken before
            // that call points at the wrong board.
            var boards = Plans.Boards(_packId).ToList();
            int target;
            if (after is not null && !ReferenceEquals(after, board))
                target = boards.IndexOf(after) + 1; // just under the row it was dropped below
            else
            {
                var first = boards.FirstOrDefault(b => SameCat(b.Category, cat) && !ReferenceEquals(b, board));
                target = boards.IndexOf(first ?? board); // top of the category
            }
            Plans.MoveBoard(_packId, board, target);
        }
        catch (Exception ex) { AppLog.LogError(nameof(ModPlanView), ex); }
        RefreshBoardRail();
    }

    /// <summary>A Y reported against the scroller, in the row host's own coordinates.</summary>
    /// <remarks>Through the visual tree rather than by adding <c>VerticalOffset</c>: the pointer is
    /// reported against the scroller and the rows are measured against the content, and those two
    /// only agree while the rail is scrolled to the top.</remarks>
    private double RailContentY(double scrollerY) =>
        RailScroll.TranslatePoint(new Point(0, scrollerY), RailList).Y;

    private double RailTop(FrameworkElement el) => el.TranslatePoint(new Point(0, 0), RailList).Y;

    /// <summary>Works out which category the pointer is in and which board a dragged one would land
    /// after, then draws the line there.</summary>
    private void ComputeRailDrop(double y)
    {
        // Category = the group whose header is the last one at or above the pointer. With no headers
        // at all every board is uncategorized and the whole rail is one group.
        string? cat = _railHeaders.Count > 0 ? _railHeaders[0].Category : null;
        FrameworkElement? header = _railHeaders.Count > 0 ? _railHeaders[0].Row : null;
        foreach (var (el, key) in _railHeaders)
            if (RailTop(el) <= y) { cat = key; header = el; }

        // A folded category draws none of its rows, so this is empty and the drop lands at the top
        // of it, the same as for an empty category.
        var rows = _railRows.Where(r => SameCat(r.Board.Category, cat)).ToList();
        PlanBoard? after = null;
        double lineY;
        if (rows.Count == 0)
            lineY = header is null ? 0 : RailTop(header) + header.ActualHeight;
        else
        {
            FrameworkElement? before = null;
            foreach (var (el, b) in rows)
            {
                if (y < RailTop(el) + el.ActualHeight / 2) { before = el; break; }
                after = b;
            }
            lineY = before is not null
                ? RailTop(before) - RailRowGap / 2
                : RailTop(rows[^1].Row) + rows[^1].Row.ActualHeight + RailRowGap / 2;
        }

        _railDropCategory = cat;
        _railDropAfter = after;
        ShowRailInsertion(lineY);
    }

    private void ShowRailInsertion(double y)
    {
        // The row host's own adorner layer, so the line scrolls with the rows it is pointing between.
        var layer = AdornerLayer.GetAdornerLayer(RailList);
        if (layer is null) return;
        if (_railLine is null) { _railLine = new RailInsertion(RailList); layer.Add(_railLine); }
        _railLine.MoveTo(y);
    }

    private void ClearRailInsertion()
    {
        _railDropCategory = null;
        _railDropAfter = null;
        if (_railLine is null) return;
        AdornerLayer.GetAdornerLayer(_railLine.AdornedElement)?.Remove(_railLine);
        _railLine = null;
    }

    /// <summary>The accent line showing where a dragged board would land. Not hit-testable: an adorner
    /// that swallows the pointer stops the drag underneath it reporting where it is.</summary>
    private sealed class RailInsertion : Adorner
    {
        private double _y;

        public RailInsertion(FrameworkElement adorned) : base(adorned) => IsHitTestVisible = false;

        public void MoveTo(double y)
        {
            if (Math.Abs(_y - y) < 0.5) return;
            _y = y;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            // Built per draw so it follows theme changes; ThemeService hands out fresh brush objects.
            var brush = Application.Current.Resources["AccentBrush"] as Brush ?? Brushes.DodgerBlue;
            var pen = new Pen(brush, 2);
            pen.Freeze();
            dc.DrawLine(pen, new Point(2, _y), new Point(Math.Max(2, AdornedElement.RenderSize.Width - 2), _y));
        }
    }

    // ── rail: small helpers ─────────────────────────────────────────────────────

    private FrameworkElement? RailRowOf(PlanBoard board)
    {
        foreach (var (row, b) in _railRows)
            if (ReferenceEquals(b, board)) return row;
        return null;
    }

    /// <summary>Hover fill for a hand-built rail row. Handlers rather than a Style trigger, because
    /// these rows are built in code and their resting fill differs per row (the open board keeps
    /// its own).</summary>
    private void HoverFill(Border row, Brush rest)
    {
        row.MouseEnter += (_, _) => row.Background = Res("SurfaceHoverBrush");
        row.MouseLeave += (_, _) => row.Background = rest;
    }

    private static bool SameCat(string? a, string? b) =>
        (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) || string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Switches the active board, saving the current viewport and restoring the
    /// target's.</summary>
    private void SwitchToBoard(PlanBoard board)
    {
        if (ReferenceEquals(board, _board)) return;
        StashViewport();
        _board = board;
        Plans.SetCurrentBoard(_packId, board);
        _selected.Clear();
        RestoreViewport();
        Rebuild(); // redraws the canvas, and the rail with it
        HintIfOffGrid();
    }

    // ── viewport persistence ────────────────────────────────────────────────────

    private void StashViewport()
    {
        if (_board is null) return;
        _board.PanX = PanT.X;
        _board.PanY = PanT.Y;
        _board.Zoom = ZoomT.ScaleX;
        Save(); // pan/zoom isn't undoable content, so persist without an undo checkpoint
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

        // Opening a different board starts a fresh undo history (a cheap id check on every rebuild
        // that only does work when the board changed).
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
        PlanStatus.ToolTip = null; // only HintIfOffGrid's line carries one
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

        // Last, and guarded inside: the rail carries live card counts and the open board's sections,
        // so it is redrawn with the canvas rather than only when a board is switched.
        RefreshBoardRail();
    }

    /// <summary>A card's size as the board shows it. With the grid on every size is whole cells:
    /// stored widths show at the nearest whole cell (a section's at the nearest whole column) and
    /// content-driven heights round up. Nothing is written back just for display, so turning the
    /// grid off shows the stored geometry again; only moving or resizing a card saves the snapped
    /// values. Section heights are the exception (see <see cref="AutoSizeSections"/>).</summary>
    private (double w, double h) SizeOf(PlanNode n) => SizeOf(n, GridOn);

    private (double w, double h) SizeOf(PlanNode n, bool grid)
    {
        switch (n.Kind)
        {
            case PlanNodeKind.Mod:
            {
                var mod = Plans.ResolveMod(_packId, n, _mods);
                return (CardW(n, ModW, grid), mod?.HasNote == true ? ModNoteH : ModH);
            }
            case PlanNodeKind.Group:
            {
                var count = ModPlanService.Resolve(n.Query, _mods).Count;
                // The trailing row is "+N more" when capped and "Show less" when expanded, so it's
                // present either way once the group is over the cap.
                var rows = n.Collapsed ? 0 : Math.Max(1, GroupRowsShown(n, count) + (count > GroupMaxRows ? 1 : 0));
                var h = GroupHeaderH + rows * GroupRowH + (rows > 0 ? 10 : 4);
                return (CardW(n, GroupW, grid), grid ? CeilToGrid(h) : h);
            }
            case PlanNodeKind.Note:
            {
                var w = CardW(n, NoteW, grid);
                return (w, NoteCardH(n, w, grid));
            }
            default:
            {
                // Only the width: AutoSizeSections owns a section's height, keeping it wrapped
                // around the cards (in whole cells on the grid), and saves it as derived layout.
                var w = n.W > 0 ? n.W : SectionW;
                return (grid ? SnapToColumns(w) : w, n.H > 0 ? n.H : SectionH);
            }
        }
    }

    /// <summary>A card's width: the one it was given, else its kind's default, rounded to whole
    /// cells on the grid.</summary>
    private static double CardW(PlanNode n, double natural, bool grid) =>
        n.W <= 0 ? natural : grid ? Math.Max(MinCardW, SnapToGrid(n.W)) : n.W;

    /// <summary>A note's full height at a given width (text area, progress bar and task list),
    /// rounded up to whole cells on the grid. A note showing only its title hugs the title, with no
    /// empty band or cut-off line under it.</summary>
    private double NoteCardH(PlanNode n, double w, bool grid)
    {
        var h = NoteTextH(n, w) + NoteExtraH(n);
        return grid && !NoteShowsTitleOnly(n, w) ? CeilToGrid(h) : h;
    }

    /// <summary>How many member rows a group card lists: everything when expanded, otherwise
    /// capped.</summary>
    private static int GroupRowsShown(PlanNode node, int memberCount) =>
        node.Expanded ? memberCount : Math.Min(memberCount, GroupMaxRows);

    /// <summary>Height of a note's text area. When only the title shows (folded by its arrow, or
    /// resized too short for any description) it hugs the title; otherwise it's the resized height,
    /// else the default. A note with no description but a checkbox, progress bar or task list also
    /// hugs its title rather than opening with a tall empty box.</summary>
    private double NoteTextH(PlanNode n, double w)
    {
        if (NoteShowsTitleOnly(n, w)) return NoteTitleOnlyH(n, w);
        if (n.H > 0) return n.H;
        var tracked = n.Tracker != PlanNoteTracker.None || n.Tasks is { Count: > 0 };
        return tracked && n.Body.Length == 0 ? NoteTitleOnlyH(n, w) : NoteH;
    }

    /// <summary>A note shows only its title when its description is folded by its arrow, or when it
    /// has been resized too short for one line under the title (the description, or the prompt to
    /// write one).</summary>
    private bool NoteShowsTitleOnly(PlanNode n, double w) =>
        (n.Body.Length > 0 && n.Collapsed) || (n.H > 0 && n.H < NoteTitleOnlyH(n, w) + NoteBodyLineH);

    /// <summary>Folded by its arrow: its height then follows the title, so a resize only changes
    /// the width.</summary>
    private static bool NoteFolded(PlanNode n) => n.Kind == PlanNodeKind.Note && n.Collapsed && n.Body.Length > 0;

    /// <summary>How many small controls sit in a note's top-right corner: the description arrow,
    /// the tick box.</summary>
    private static int NoteCornerControls(PlanNode n) =>
        (n.Body.Length > 0 ? 1 : 0) + (n.Tracker == PlanNoteTracker.Checkbox ? 1 : 0);

    /// <summary>What a note shows when only its title shows: the title, or the first line of the
    /// description when it has no title.</summary>
    private static string NoteHeadline(PlanNode n) =>
        n.Title.Length > 0 ? n.Title : n.Body.Split('\n', 2)[0].Trim();

    /// <summary>Height of a note's text area holding only its headline, measured with the card's
    /// insets (12 each side, room for the corner controls, the border) and never shorter than those
    /// controls.</summary>
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
            // Border is a FrameworkElement, not a Control, so there's no MouseDoubleClick event;
            // handle the second click here before the drag starts.
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
        var card = new CloudLauncher.Controls.SlateBorder
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

        var icon = new CloudLauncher.Controls.SlateBorder
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

        // Grid rather than a StackPanel so a long name trims instead of pushing the flag pills past
        // the card's edge; the Border doesn't clip.
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
                Foreground = Res("AccentTextBrush"), VerticalAlignment = VerticalAlignment.Center,
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

    /// <summary>A mod card whose jar is no longer installed. Kept so the plan survives a mod being
    /// removed for a while, with a one-click way to clear it.</summary>
    private Border CreateMissingCard(PlanNode node)
    {
        var card = new CloudLauncher.Controls.SlateBorder
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
        // No update marker here: the board is for planning, and updates live in the List view.

        foreach (var f in flags.Take(2))
            yield return new CloudLauncher.Controls.SlateBorder
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

        var card = new CloudLauncher.Controls.SlateBorder
        {
            CornerRadius = new CornerRadius(9),
            Background = Res("Surface2Brush"),
            BorderBrush = accent,
            BorderThickness = new Thickness(1.4),
            Cursor = Cursors.SizeAll,
            ToolTip = Tip(members.Count == 0
                ? "Nothing matches this yet - it fills in as you tag mods."
                : string.Join("\n", members.Select(m => m.DisplayName)))
        };

        var root = new Grid();
        var stack = new StackPanel();

        // Header. A folded card is only this, and on the grid it's rounded up to whole cells: the
        // extra room is split above and below the header rather than left as a gap under the rule.
        var slack = node.Collapsed && _rect.TryGetValue(node.Id, out var box) ? box.Height - SizeOf(node, grid: false).h : 0;
        var header = new Grid { Margin = new Thickness(11, 9 + slack / 2, 8, 0) };
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
        stack.Children.Add(new CloudLauncher.Controls.SlateBorder
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

    /// <summary>"2L 3M 1S": how much content a group carries, not just how many mods. Twelve tiny
    /// tweaks and three pack-defining mods look the same by count. Empty when nothing is
    /// sized.</summary>
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
        // Glyph and label as separate runs: the glyph needs the icon font, the label doesn't.
        // Neither sets Foreground, so both inherit the link colour from the template, hover state
        // included.
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
                Foreground = Res("AccentTextBrush"), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 2, 0)
            });
        Grid.SetColumn(markers, 2);
        row.Children.Add(markers);

        row.ToolTip = Tip(mod.TooltipText);
        // Members are clickable: the group card doubles as a working list. The up-click is not
        // marked handled because it has to bubble to OnCardUp to release the card's drag. A click
        // that moved the card opens nothing.
        row.MouseLeftButtonUp += (_, _) => { if (!_dragMoved) _onOpenMod?.Invoke(mod); };
        row.MouseRightButtonUp += (_, e) => { e.Handled = true; ShowModOptions(mod, row); };
        return row;
    }

    // ── note + section cards ────────────────────────────────────────────────────

    private Border CreateNoteCard(PlanNode node)
    {
        var accent = ColorOf(node, Res("BorderStrongBrush"));
        var card = new CloudLauncher.Controls.SlateBorder
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
        // The description, unless only the title shows. With a tracker or task list an empty one is
        // left out, since the "write something" prompt would just be clutter.
        if (!titleOnly && (hasBody || (!hasTasks && node.Tracker == PlanNoteTracker.None)))
        {
            // Whole lines only: the space under the title is cut to a number of lines and the last one
            // that fits ends in an ellipsis, so a resized card never shows half a line.
            var bodyRoom = node.Title.Length > 0
                ? NoteTextH(node, w) - NoteTitleOnlyH(node, w) - 5
                : NoteTextH(node, w) - 20;
            var lines = Math.Max(1, (int)Math.Floor(bodyRoom / NoteBodyLineH));
            stack.Children.Add(new TextBlock
            {
                Text = hasBody ? node.Body : "Double-click to write...",
                FontSize = 12,
                Foreground = hasBody ? Res("TextSecondaryBrush") : Res("TextTertiaryBrush"),
                FontStyle = hasBody ? FontStyles.Normal : FontStyles.Italic,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                LineHeight = NoteBodyLineH,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                MaxHeight = lines * NoteBodyLineH,
                Margin = new Thickness(0, 0, node.Title.Length > 0 ? 0 : cornerRoom, 0) // untitled: clear the corner
            });
        }

        // Not a ScrollViewer: it focuses itself on mouse-down and marks the event handled, which
        // stops the card from being dragged. A clipping, non-hit-testable Border keeps the text
        // inside and lets clicks through; notes are resizable, so overflow is fixed by resizing.
        root.Children.Add(new CloudLauncher.Controls.SlateBorder { ClipToBounds = true, IsHitTestVisible = false, Child = stack });

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

        // Top-right corner: the arrow that folds the description away (only when there is one),
        // then the tick box.
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
                    node.Done ? "Done - click to untick" : "Tick when this is done");
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

    /// <summary>The arrow in a note's corner that folds its description away, leaving only the
    /// title, and unfolds it again (also undoing a resize that squeezed the description out).</summary>
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
                // If a resize squeezed it down to its title, go back to the default height so the
                // text shows.
                if (_rect.TryGetValue(node.Id, out var r) && NoteShowsTitleOnly(node, r.Width)) node.H = 0;
            }
            else node.Collapsed = true;
            Commit();
            Rebuild();
        };
        return button;
    }

    // ── note trackers + tasks ───────────────────────────────────────────────────

    /// <summary>A small tick box drawn to match the board. It takes its own press, so ticking never
    /// drags the card, and toggles on release.</summary>
    private Border TickBox(bool done, double size, Action toggle, string tip)
    {
        var box = new CloudLauncher.Controls.SlateBorder
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

    /// <summary>A note's progress bar: the percentage, then a track you click or drag along to set
    /// it in 5% steps (the right-click menu has presets). Only the track takes presses; the label
    /// still drags the card.</summary>
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
        var groove = new CloudLauncher.Controls.SlateBorder
        {
            CornerRadius = new CornerRadius(3.5), Background = Res("Surface3Brush"),
            BorderBrush = Res("BorderBrush"), BorderThickness = new Thickness(1)
        };
        Grid.SetColumnSpan(groove, 2);
        track.Children.Add(groove);
        var fill = new CloudLauncher.Controls.SlateBorder { CornerRadius = new CornerRadius(3.5), Background = ColorOf(node, Res("AccentBrush")) };
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

    /// <summary>A note's task list: a "Tasks n/m" heading whose arrow folds the list away, a row
    /// per task (tick box + text), and an "Add task" row at the foot.</summary>
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
            Foreground = done == tasks.Count ? Res("AccentTextBrush") : Res("TextTertiaryBrush")
        });

        // The rule on top separates the list from the note's text; the whole strip toggles the fold.
        var header = new CloudLauncher.Controls.SlateBorder
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

    /// <summary>Prompts for a task and adds it to a note, at the end or right after
    /// <paramref name="after"/>, unfolding the list so the new task shows.</summary>
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
        menu.Items.Add(Item("Rename...", () => _ = RenameTaskAsync(task)));
        menu.Items.Add(Item("Add task below...", () => _ = AddTaskAsync(node, task)));
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

    /// <summary>The note menu's tracking block: one checkbox or one progress bar (adding one
    /// replaces the other), ticking or presets for whichever is shown, and tasks for the
    /// collapsible list.</summary>
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

        items.Add(Item("Add task...", () => _ = AddTaskAsync(node)));
        if (node.Tasks is { Count: > 0 })
            items.Add(Item(node.TasksCollapsed ? "Show tasks" : "Hide tasks",
                () => Apply(() => node.TasksCollapsed = !node.TasksCollapsed)));
        items.Add(new Separator());
    }

    private void AddSection(PlanNode node)
    {
        var accent = ColorOf(node, Res("BorderStrongBrush"));
        var box = new CloudLauncher.Controls.SlateBorder
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

        // Membership is positional: a card belongs to the section it sits on, with no explicit bind
        // step. The header's live count makes that visible.
        var titleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(SectionPad, 0, 14, 0) // the title starts in line with the cards below it
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
        var header = new CloudLauncher.Controls.SlateBorder
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

        // A section connects by its title strip (its body belongs to its cards), so the dot sits there.
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

    /// <summary>Sizes every section's height to wrap the cards on it, with the same padding below
    /// the lowest card as at the sides, and never below the default height. Height is fully
    /// automatic (the resize grip only sets width), so a section can't be too small for its
    /// contents.</summary>
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
                // with the side padding below it. On the grid the round-up only matters under a
                // card that isn't on the grid yet, and it keeps the section's bottom on a grid line.
                var wanted = cards.Count == 0
                    ? Math.Max(sr.Height, SectionH)
                    : Math.Max(MinSectionH, cards.Max(r => r.Bottom) + SectionPad - sr.Y); // bottom = side padding
                if (GridOn) wanted = CeilToGrid(wanted);

                if (Math.Abs(wanted - sr.Height) < 0.5) continue;
                section.H = wanted;
                _rect[section.Id] = new Rect(sr.X, sr.Y, sr.Width, wanted);
                changed = true;
            }
        }

        if (changed) Save(); // derived layout during Rebuild, not a separate undo step
    }

    /// <summary>Packs a section's cards into its columns, each going to the currently shortest
    /// column (a masonry fill, so mixed heights leave no ragged gaps), a cell under the card above
    /// and as wide as the columns it spans. An explicit command, so it works whether the grid is on
    /// or not. Grows the section if needed so nothing ends up half outside and unbound.</summary>
    private void ArrangeSection(PlanNode section)
    {
        if (_board is null || !_rect.TryGetValue(section.Id, out var sr)) return;

        var cards = ContainedNodes(section)
            .OrderBy(n => Math.Round(_rect[n.Id].Y / 48))   // band into rough rows first...
            .ThenBy(n => _rect[n.Id].X)                     // ...then left-to-right within a row
            .ToList();
        if (cards.Count == 0) { PlanStatus.Text = "That section has no cards in it yet."; return; }

        var cols = ColumnCount(sr);
        var y0 = ContentTop(sr);
        var columnBottom = Enumerable.Repeat(y0, cols).ToArray();

        foreach (var card in cards)
        {
            // A card spanning more than one column goes where all the columns it covers are free
            // highest up.
            var span = SpanOf(_rect[card.Id].Width, cols);
            var col = Enumerable.Range(0, cols - span + 1)
                .MinBy(c => columnBottom.Skip(c).Take(span).Max());
            var top = columnBottom.Skip(col).Take(span).Max();
            card.X = ContentLeft(sr) + col * SlotPitch;
            card.Y = GridOn ? y0 + CeilToGrid(top - y0) : Math.Round(top);
            card.W = span * SlotPitch - SlotGap;
            var bottom = card.Y + SizeOf(card).h + SlotGap;
            for (var c = col; c < col + span; c++) columnBottom[c] = bottom;
        }

        // No height maths here; AutoSizeSections wraps the section around the result on rebuild.
        Commit();
        Rebuild();
        PlanStatus.Text = $"Arranged {cards.Count} card(s).";
    }

    /// <summary>Shrinks a section to the width its cards use (on the grid, the whole columns they
    /// sit in). Height needs no equivalent: <see cref="AutoSizeSections"/> keeps it wrapped.</summary>
    private void ShrinkSection(PlanNode section)
    {
        if (_board is null || !_rect.TryGetValue(section.Id, out var sr)) return;

        var cards = ContainedNodes(section).Select(n => _rect[n.Id]).ToList();
        if (cards.Count == 0) { PlanStatus.Text = "That section has no cards in it yet."; return; }

        // The right-most card's edge plus the side padding. On the grid the cards are shown fitted
        // to their columns, so this is the columns they occupy.
        var used = Math.Max(MinSectionW, cards.Max(r => r.Right) + SectionPad - sr.X);
        section.W = GridOn ? SnapToColumns(used) : Math.Round(used);

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

    /// <summary>The dot on a card's right edge: drag it onto another card to draw an arrow, or
    /// click it and then click the target. Hidden until the card is hovered. Hover is tracked on
    /// <paramref name="card"/> itself, not its content grid, which only counts as hovered over its
    /// text.</summary>
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
        var handle = new CloudLauncher.Controls.SlateBorder
        {
            Width = pad, Height = pad,
            Tag = NoAnchorTag,
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = centreY is null ? VerticalAlignment.Center : VerticalAlignment.Top,
            Margin = new Thickness(0, centreY is { } y ? y - pad / 2 : 0, -pad / 2, 0),
            Cursor = Cursors.Cross,
            Visibility = Visibility.Hidden,
            ToolTip = new ToolTip { Content = "Drag onto another card to draw an arrow - or click, then click the card it points to" },
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
        var grip = new CloudLauncher.Controls.SlateBorder
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
                    ? GridOn
                        ? "Drag to add or remove a column - height follows the contents"
                        : "Drag to resize the width - height follows the contents"
                    : NoteFolded(node) ? "Drag to resize the width - unfold the note to change its height" : "Drag to resize"
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
            if (ReferenceEquals(_resizeNode, node)) ResizeTo(node, e.GetPosition(BoardCanvas) - _resizeStart);
        };
        grip.MouseLeftButtonUp += (_, e) =>
        {
            if (!ReferenceEquals(_resizeNode, node)) return;
            grip.ReleaseMouseCapture();
            _resizeNode = null;
            e.Handled = true;
            Commit(); // nothing left to round: the size snapped while it was dragged
            Rebuild();
        };
    }

    /// <summary>Applies a resize grip dragged <paramref name="by"/> from where it was pressed,
    /// live. On the grid the size moves in whole steps (a note a cell at a time, or a column at a
    /// time inside a section; a section a column at a time), so what you see while dragging is what
    /// you get on release. Off the grid it follows the pointer.</summary>
    private void ResizeTo(PlanNode node, Vector by)
    {
        var shown = _rect[node.Id];
        double w, h;
        if (node.Kind == PlanNodeKind.Section)
        {
            // Sections come in whole columns, so two sections sized by eye still match and their
            // cards stay aligned. Their height belongs to AutoSizeSections; dragging it shorter
            // than the contents is what auto-sizing prevents. (A new section's H is 0 until it's
            // filled.)
            var raw = _resizeOrigW + by.X;
            w = GridOn ? SnapToColumns(raw) : Math.Max(MinSectionW, Math.Round(raw));
            h = _resizeOrigH;
        }
        else
        {
            w = NoteWidthFor(node, _resizeOrigW + by.X);
            // A folded note keeps its title height, so only its width follows the drag; any other note
            // can be squeezed right down to its title, at which point it shows nothing but the title.
            if (NoteFolded(node)) h = _resizeOrigH;
            else
            {
                // A note's H is only its text area: its progress bar and task list keep their own fixed
                // heights below it.
                var extra = NoteExtraH(node);
                var titleOnly = NoteTitleOnlyH(node, w);
                if (GridOn)
                {
                    // Whole cells while there is room for a line under the title; below that the
                    // card hugs its title, which is the smallest a note gets.
                    var least = CeilToGrid(titleOnly + NoteBodyLineH + extra);
                    var snapped = SnapToGrid(_resizeOrigH + by.Y);
                    if (snapped >= least)
                    {
                        h = snapped;
                        node.H = h - extra;
                    }
                    else
                    {
                        h = titleOnly + extra;
                        node.H = titleOnly;
                    }
                }
                else
                {
                    node.H = Math.Max(titleOnly, Math.Round(_resizeOrigH - extra + by.Y));
                    h = node.H + extra;
                }
            }
        }
        node.W = w;
        var el = _nodeEls[node.Id];
        el.Width = w;
        el.Height = h;
        _rect[node.Id] = new Rect(shown.X, shown.Y, w, h);
        RedrawEdges();
    }

    /// <summary>The width a note's resize asks for: whole cells on the grid (whole columns inside a
    /// section, never more than fit to its right), else the pixel.</summary>
    private double NoteWidthFor(PlanNode note, double raw)
    {
        if (!GridOn) return Math.Max(MinCardW, Math.Round(raw));
        if (_board?.Node(note.SectionId) is not { Kind: PlanNodeKind.Section } section
            || !_rect.TryGetValue(section.Id, out var sr))
            return Math.Max(MinCardW, SnapToGrid(raw));

        var cols = ColumnCount(sr);
        var col = ColumnAt(sr, _rect[note.Id].X, 1);
        return Math.Min(SpanOf(raw, cols), cols - col) * SlotPitch - SlotGap;
    }

    // ── card drag ───────────────────────────────────────────────────────────────

    private void OnCardDown(PlanNode node, FrameworkElement el, MouseButtonEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            // Shift+click only selects, with no drag, so building a multi-selection can't shove a
            // card across the board.
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
        // Everything moves from where it's shown, which on the grid isn't always where it's stored
        // (a section's cards are shown fitted to its columns). Starting from the stored spot would
        // make the card jump on the first move.
        (_dragOrigX, _dragOrigY) = ShownAt(node);

        // A section carries whatever is sitting on it; any other card carries the rest of the
        // selection, so a multi-selection moves as one block.
        _dragCarried = (node.Kind == PlanNodeKind.Section
                ? ContainedNodes(node)
                : SelectedNodes().Where(n => n.Id != node.Id).ToList())
            .Select(n => { var (x, y) = ShownAt(n); return (n, x, y); })
            .ToList();

        // Capture on whatever was hit (often a child TextBlock) and remember it, so the release in
        // OnCardUp targets the same element the capture was taken on.
        _dragCapture = e.Source as UIElement;
        _dragCapture?.CaptureMouse();
        e.Handled = true;
    }

    /// <summary>Where a card is shown: where it's stored, unless the grid has fitted it somewhere
    /// else.</summary>
    private (double x, double y) ShownAt(PlanNode n) =>
        _rect.TryGetValue(n.Id, out var r) ? (r.X, r.Y) : (n.X, n.Y);

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

    /// <summary>Sets a card's section membership from where it sits: the section its centre is
    /// over, or none. Called when a card is dropped.</summary>
    private void AssignSectionFromPosition(PlanNode card)
    {
        if (card.Kind == PlanNodeKind.Section || !_rect.TryGetValue(card.Id, out var r)) return;
        var section = SectionOver(new Point(r.X + r.Width / 2, r.Y + r.Height / 2));
        card.SectionId = section?.Id;
    }

    /// <summary>One-time migration from position-based membership to explicit SectionId, so boards
    /// saved before SectionId existed keep their groupings. Runs on the board's first rebuild.</summary>
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
        if (_dragNode is not null && _dragEl is not null) DragTo(e.GetPosition(BoardCanvas));
    }

    /// <summary>Moves the card being dragged (and whatever it carries) for the pointer at
    /// <paramref name="p"/>.</summary>
    private void DragTo(Point p)
    {
        if (_dragNode is null) return;
        double dx = p.X - _dragStart.X, dy = p.Y - _dragStart.Y;
        if (!_dragMoved && Math.Abs(dx) + Math.Abs(dy) < 4) return;
        _dragMoved = true;

        // One snap and nothing else. An extra pull toward the neighbours' edges would fight the
        // grid snap; with every edge on the grid there is nothing left to align, and with the grid
        // off nothing should pull.
        var (nx, ny) = SnapFor(_dragNode, _dragOrigX + dx, _dragOrigY + dy);
        MoveNode(_dragNode, nx, ny);
        // Move carried cards by the dragged node's applied delta, not an independently snapped
        // position, or the section and its cards would round to the grid separately and drift apart.
        double appliedDx = nx - _dragOrigX, appliedDy = ny - _dragOrigY;
        foreach (var (n, ox, oy) in _dragCarried) MoveNode(n, ox + appliedDx, oy + appliedDy);
        UpdateSectionHover();
        RedrawEdges();
    }

    /// <summary>Where a dragged node lands for the spot the pointer asks for. On the grid, a section
    /// and a loose card snap to whole cells, and a card over a section snaps into that section's
    /// columns instead, so anything dropped inside lines up with what's already there. Off the grid
    /// nothing snaps: it goes where it's put, to the pixel.</summary>
    private (double x, double y) SnapFor(PlanNode node, double wantX, double wantY) =>
        node.Kind != PlanNodeKind.Section && _rect.TryGetValue(node.Id, out var r)
            ? SnapAt(r.Width, r.Height, wantX, wantY)
            : (Place(wantX), Place(wantY));

    /// <summary>The nearest whole grid cell. An exact half always rounds up, so cards that all sit
    /// half a cell off move together; banker's rounding would send 12 down and 36 up and pull cards
    /// a cell apart.</summary>
    private static double SnapToGrid(double v) => Math.Floor(v / GridCell + 0.5) * GridCell;

    /// <summary>Up to the next whole grid cell, for heights that follow their content and must never
    /// cut it short. (A hundredth of a pixel over doesn't cost a whole cell.)</summary>
    private static double CeilToGrid(double v) => Math.Ceiling((v - 0.01) / GridCell) * GridCell;

    /// <summary>Where a position lands: on the grid when it's on, else on the nearest whole
    /// pixel.</summary>
    private static double Place(double v) => GridOn ? SnapToGrid(v) : Math.Round(v);

    /// <summary>Snaps a card of the given size to the board grid, or into the columns of whichever
    /// section its centre lands in. Used while dragging and when a card is first placed. Off the
    /// grid a card keeps its own spot, even over a section.</summary>
    private (double x, double y) SnapAt(double w, double h, double wantX, double wantY)
    {
        if (!GridOn) return (Place(wantX), Place(wantY));
        if (SectionOver(new Point(wantX + w / 2, wantY + h / 2)) is not { } section
            || !_rect.TryGetValue(section.Id, out var sr))
            return (SnapToGrid(wantX), SnapToGrid(wantY));

        // Rows count from the section's own content top, so its cards keep to its grid even if the
        // section itself was placed with the grid off.
        var y0 = ContentTop(sr);
        var col = ColumnAt(sr, wantX, SpanOf(w, ColumnCount(sr)));
        return (ContentLeft(sr) + col * SlotPitch, y0 + Math.Max(0, SnapToGrid(wantY - y0)));
    }

    private static double ContentLeft(Rect section) => section.X + SectionPad;
    private static double ContentTop(Rect section) => section.Y + SectionContentTop;

    /// <summary>How many whole columns fit across a section. Columns have a fixed pitch (a column
    /// plus its gutter, 11 cells), so every column line is on the grid whenever the section
    /// is.</summary>
    private static int ColumnCount(Rect section) =>
        Math.Max(1, (int)Math.Floor((section.Width - SectionPad * 2 + SlotGap) / SlotPitch));

    /// <summary>How many columns a card this wide spans in a section of <paramref name="cols"/>
    /// columns.</summary>
    private static int SpanOf(double width, int cols) =>
        Math.Clamp((int)Math.Round((width + SlotGap) / SlotPitch), 1, cols);

    /// <summary>The column nearest to <paramref name="x"/>, kept far enough left for a card spanning
    /// <paramref name="span"/> columns to fit.</summary>
    private static int ColumnAt(Rect section, double x, int span) =>
        Math.Clamp((int)Math.Round((x - ContentLeft(section)) / SlotPitch), 0, ColumnCount(section) - span);

    /// <summary>Shows every section card in its section's columns (X on a column line, width a
    /// whole number of columns) so cards in a row are the same width and tile flush. Grid only; off
    /// it a card keeps the X and width it was given. Display only, like the rounding in
    /// <see cref="SizeOf(PlanNode)"/>: moving a card is what saves its fitted spot.</summary>
    private void ApplySectionColumnWidths()
    {
        if (_board is null || !GridOn) return;
        foreach (var card in _board.Nodes.Where(n => n.Kind != PlanNodeKind.Section && n.SectionId is not null))
        {
            if (_board.Node(card.SectionId) is not { } sec || !_rect.TryGetValue(sec.Id, out var sr)) continue;
            if (!_rect.TryGetValue(card.Id, out var cr)) continue;

            var span = SpanOf(cr.Width, ColumnCount(sr));
            var w = span * SlotPitch - SlotGap;
            // A note's title wraps to its width, so its height is worked out again at the column's.
            var h = card.Kind == PlanNodeKind.Note ? NoteCardH(card, w, grid: true) : cr.Height;
            _rect[card.Id] = new Rect(ContentLeft(sr) + ColumnAt(sr, cr.X, span) * SlotPitch, cr.Y, w, h);
        }
    }

    /// <summary>The width of a section holding the nearest whole number of columns, at least one:
    /// cols x 264 + 24, which puts both edges and every column line on the grid.</summary>
    private static double SnapToColumns(double width) =>
        Math.Max(1, Math.Floor((width - SectionPad) / SlotPitch + 0.5)) * SlotPitch + SectionPad;

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

        // Assign section membership from where things landed. Dragging a section keeps its own
        // cards (they were carried); dragging a card, and any carried selection, re-homes each into
        // the section it was dropped on, or none. That stops a section from taking cards it merely
        // overlaps.
        if (dropped.Kind != PlanNodeKind.Section)
        {
            AssignSectionFromPosition(dropped);
            foreach (var n in carried) AssignSectionFromPosition(n);
        }

        Commit();
        // Full rebuild, not just a count refresh: the section the card landed in (or left) has to
        // re-wrap around its new contents.
        Rebuild();

        // After Rebuild, which writes the card/link count into the same label.
        if (landedOn is not null && _board?.Node(landedOn) is { } section)
            PlanStatus.Text = $"Added to '{(section.Title.Length > 0 ? section.Title : "Section")}'.";
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

    // ── linking ─────────────────────────────────────────────────────────────────

    // Two ways to draw an arrow, both ending in TryAddLink: drag a card's edge dot onto another
    // card, or pick a start card and then click the target (the toolbar's Arrow tool, which stays
    // on for a whole map, "Draw arrow from here" on a card's menu, or a plain click on the dot).

    /// <summary>Starts dragging out an arrow from the dot (<paramref name="anchor"/> null, so the
    /// start attaches automatically) or from a point on the outline (the start is pinned there).
    /// The mouse is captured on <paramref name="capture"/>: the dot itself, or the board for an
    /// outline press.</summary>
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
        PlanStatus.Text = "Drop on a card - near its outline to pin the end there...";
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
        // Released without dragging. On the dot: continue as a click-then-click arrow. On an
        // outline it was just a click, which selects the card when it landed on it.
        if (fromAnchor is null) BeginPendingArrow(from);
        else if (_linkPressedInside) { SelectOnly(from.Id); ReportSelection(); }
        else ClearSelection();
    }

    /// <summary>Draws an arrow between two cards, each end pinned to its anchor or (null)
    /// automatic. If an arrow already runs between them the same way and this one pins an end, that
    /// arrow is re-pinned instead, which is how an existing arrow's ends are moved. Otherwise a
    /// pair links only once.</summary>
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
        // After Rebuild, which writes the card/link count into the same label.
        PlanStatus.Text = "Arrow added  ·  click it to give it a label";
    }

    /// <summary>What an arrow end at a board point attaches to. Well inside a card: that card,
    /// attached automatically (at the outline point facing the other end). On or just around its
    /// outline: that card, pinned to the nearest outline point. A section counts by its outline,
    /// plus its title strip for an automatic end; its body belongs to the cards on it, so a
    /// near-miss on one of those doesn't link the whole section.</summary>
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

    /// <summary>The point of a card's outline nearest to <paramref name="p"/>, as an anchor. Kept
    /// clear of the rounded corners so a pinned arrow always leaves a straight side square to it,
    /// and snapped to the middle of a side when close, which is where tidy diagrams want their
    /// arrows.</summary>
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

    /// <summary>Where a pinned arrow end sits on a card at its current size, and which way that
    /// side of the outline faces. An anchor that somehow isn't on the outline is pushed out to its
    /// nearest side.</summary>
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

    /// <summary>The dashed preview of the arrow being drawn, shaped like the final arrow: from the
    /// start card (at its pinned point, if any) to the pointer, or, over a card it can connect to,
    /// to the spot it will attach to.</summary>
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

    /// <summary>Picks the card an arrow starts from, pinned to <paramref name="anchor"/> on its
    /// outline or automatic; the next card clicked becomes its target. A dashed preview follows the
    /// pointer.</summary>
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

    /// <summary>A card clicked while drawing arrows: the first click picks the start, the second
    /// the target, each pinned where it landed on an outline or automatic when well inside the
    /// card. Clicking the start card again puts it back down.</summary>
    private void OnArrowCardClick(PlanNode node, PlanAnchor? anchor)
    {
        if (_arrowFrom is null) { BeginPendingArrow(node, anchor); return; }

        var from = _arrowFrom;
        var fromAnchor = _arrowFromAnchor;
        CancelPendingArrow();
        if (from.Id == node.Id) { PlanStatus.Text = _arrowTool ? ArrowPrompt : ""; return; }
        TryAddLink(from, fromAnchor, node, anchor);
    }

    /// <summary>Presses that start or finish an arrow, caught while tunnelling so they beat the
    /// cards' own handlers. While drawing arrows, pressing anywhere on a card (buttons and member
    /// rows included) picks that card instead of dragging, editing or opening it. Otherwise a press
    /// on a card's outline (not on the dot, a grip or a button) drags out an arrow pinned to that
    /// point, and a press anywhere else still drags the card.</summary>
    private void OnViewportPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _swallowArrowUp = false;
        var target = ArrowTargetAt(e.GetPosition(BoardCanvas));

        if (ArrowModeActive)
        {
            // Empty canvas falls through to OnViewportMouseDown, which pans (or backs out on a
            // plain click).
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

    /// <summary>Eats the release of a press the arrow tool used, so it can't also click whatever is
    /// under the pointer (on a group card's member row that would open the mod's page).</summary>
    private void OnViewportPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_swallowArrowUp) return;
        _swallowArrowUp = false;
        e.Handled = true;
    }

    /// <summary>A crosshair while drawing arrows, or while the pointer is on an outline where a
    /// press would start one. Forced over the cards too, whose own move and hand cursors would
    /// suggest a click drags or opens them.</summary>
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
            // An endpoint card was deleted: drop the dangling link.
            _board.Edges.Remove(edge);
            Commit();
            return;
        }

        // A pinned end sits on its chosen outline point. An automatic end sits wherever the outline
        // faces the other end: aimed at that end's pinned point if it has one, else at its card's
        // centre.
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
        // Handle the press too: left to bubble, it reaches the canvas, which starts a pan and
        // captures the mouse, so the release would never come back here to open the label.
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
            var chip = new CloudLauncher.Controls.SlateBorder
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

    /// <summary>The curve an arrow follows between its two ends, the direction it arrives in (for
    /// the arrowhead) and its midpoint (for the label). With both ends automatic it's a gentle
    /// single bend. A pinned end leaves or meets its card square to that side of the outline, so
    /// the arrow visibly comes out of the chosen spot instead of cutting back across the card.</summary>
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
        menu.Items.Add(Item(edge.Label is null ? "Add label..." : "Edit label...", () => _ = EditEdgeLabelAsync(edge)));

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
        // Board-scoped actions go at the top rather than under a dozen mod options; index 2 is
        // right after the menu's header and separator. "Remove card from board" only removes the
        // card; the jar is untouched.
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

    /// <summary>The mod options menu for a card, with every callback the List view's own menu
    /// has.</summary>
    /// <remarks><see cref="ModOptionsMenu"/> hides items whose callback is null, so every callback
    /// is set here to match the list. "Delete file" deletes the jar; removing the card is a
    /// separate item inserted above.</remarks>
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
        },
        OnUpdate = list =>
        {
            if (_onUpdate is null) return;
            PlanStatus.Text = list.Count == 1 ? $"Updating {list[0].DisplayName}..." : $"Updating {list.Count} mod(s)...";
            _onUpdate(list);
        },
        OnRecheckUpdates = list =>
        {
            if (_onRecheckUpdates is null) return;
            PlanStatus.Text = "Re-checking for updates...";
            _onRecheckUpdates(list);
        },
        OnUpdateToVersion = UpdateModToVersionAsync,
        OnDelete = DeleteModFilesAsync,
        OnReveal = mod =>
        {
            var dir = System.IO.Path.GetDirectoryName(mod.FilePath);
            if (dir is null) return;
            if (!SafeLaunch.OpenFolder(dir)) PlanStatus.Text = "Could not open the folder.";
        }
    };

    /// <summary>Pick a specific version for a mod on the board and install it.</summary>
    private async void UpdateModToVersionAsync(PackMod mod)
    {
        try
        {
            if (mod.PrimaryMod is null || _owner is not { } host)
            {
                PlanStatus.Text = $"{mod.DisplayName} isn't identified yet.";
                return;
            }
            PlanStatus.Text = $"Loading versions for {mod.DisplayName}...";
            var versions = await ModUpdater.FetchVersionsAsync(mod.PrimaryMod);
            if (versions.Count == 0) { PlanStatus.Text = "No versions found."; return; }

            var mc = mod.PrimaryVersion?.GameVersions.FirstOrDefault();
            var loader = mod.PrimaryVersion?.Loaders.FirstOrDefault();
            var chosen = await ModVersionPickerDialog.ShowAsync(host, mod.DisplayName, versions, mc, loader,
                mod.PrimaryVersion?.Id, mod.PrimaryVersion?.VersionNumber, mod.Meta.UpdateLocked);
            if (chosen is null) { PlanStatus.Text = ""; return; }

            if (mod.Meta.UpdateLocked && !chosen.KeepVersion && !await AppDialog.ConfirmAsync(host, "Mod is locked",
                    $"{mod.DisplayName} is locked to its current version.\n\nChange it anyway? It stays locked afterwards.",
                    "Change anyway", "Keep locked"))
            { PlanStatus.Text = ""; return; }

            PlanStatus.Text = $"Installing {chosen.Version.VersionNumber}...";
            if (await ModUpdater.InstallVersionAsync(mod, chosen.Version))
            {
                ModVersionPickerDialog.ApplyKeepVersion(_packId, mod, chosen);
                PlanStatus.Text = $"{mod.DisplayName} is now on {chosen.Version.VersionNumber}.";
                _onReload?.Invoke();   // the jar changed on disk: re-scan rather than trust this board
            }
            else PlanStatus.Text = "That version has no downloadable file.";
        }
        catch (Exception ex) { PlanStatus.Text = "Install failed: " + ex.Message; }
    }

    /// <summary>Deletes the jars behind the given cards. The cards themselves stay until the re-scan
    /// comes back and finds the mods gone.</summary>
    private async void DeleteModFilesAsync(IReadOnlyList<PackMod> mods)
    {
        try
        {
            if (mods.Count == 0 || _owner is not { } host) return;
            var message = mods.Count == 1
                ? $"Delete {System.IO.Path.GetFileName(mods[0].FilePath)}?\n\nThis removes the jar from the pack, not just its card."
                : $"Delete {mods.Count} mod files?\n\nThis removes the jars from the pack, not just their cards.";
            if (!await AppDialog.ConfirmAsync(host, "Delete mods", message, "Delete", "Cancel", danger: true)) return;

            var failed = 0;
            foreach (var mod in mods)
            {
                try { System.IO.File.Delete(mod.FilePath); }
                catch { failed++; }
            }
            PlanStatus.Text = failed == 0
                ? $"Deleted {mods.Count} mod(s)."
                : $"Deleted {mods.Count - failed} of {mods.Count}; {failed} could not be removed (in use?).";
            _onReload?.Invoke();
        }
        catch (Exception ex) { PlanStatus.Text = "Delete failed: " + ex.Message; }
    }

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
        menu.Items.Add(Item("Rename heading...", () => _ = RenameNodeAsync(node, "Card heading")));
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
        menu.Items.Add(Item(what == "note" ? "Edit..." : "Rename...", () =>
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
            menu.Items.Add(Item("Add mod card to section...", () => _ = AddModCardAsync(SectionAnchor(node))));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Arrange cards", () => ArrangeSection(node)));
            menu.Items.Add(Item("Fit width to cards", () => ShrinkSection(node)));
        }
        menu.Items.Add(ColorMenu(node.Color, hex => { node.Color = hex; Commit(); Rebuild(); }));
        menu.Items.Add(Item("Duplicate", () =>
        {
            if (_board is null) return;
            var copy = node.Clone();
            var (x, y) = ShownAt(node);
            copy.X = Place(x + GridCell);
            copy.Y = Place(y + GridCell);
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

    /// <summary>Deletes a section along with everything on it. Plain "Delete section" leaves the
    /// cards behind as loose ones, so this is the only path that can lose several at once, hence
    /// the confirm.</summary>
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
                Icon = new CloudLauncher.Controls.SlateBorder
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

    /// <summary>Turns a group card into one card per member, each linked back to the group: a quick
    /// way from "these 6 mods are my Magic set" to planning around them.</summary>
    private void ExplodeGroup(PlanNode group, IReadOnlyList<PackMod> members)
    {
        if (_board is null || members.Count == 0) return;

        // A column four cells clear of the group, leaving room for the arrows fanning out, with a
        // cell between cards, measured per card since a mod with a note is a row taller.
        var gr = _rect[group.Id];
        var x = Place(gr.Right + 4 * GridCell);
        var y = Place(gr.Y);
        var added = 0;

        foreach (var mod in members)
        {
            if (_board.Nodes.Any(n => n.Kind == PlanNodeKind.Mod && n.ModKey is not null
                                      && mod.CandidateKeys.Contains(n.ModKey, StringComparer.OrdinalIgnoreCase)))
                continue;

            var node = new PlanNode { Kind = PlanNodeKind.Mod, ModKey = mod.Key, X = x, Y = y };
            _board.Nodes.Add(node);
            _board.Edges.Add(new PlanEdge { FromId = group.Id, ToId = node.Id });
            y += (mod.HasNote ? ModNoteH : ModH) + GridCell;
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

    /// <summary>Deletes every selected card (and any links touching them). Asks first when more
    /// than five are selected.</summary>
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
    /// <paramref name="at"/> (the view centre from the toolbar, the click point from the canvas
    /// menu).</summary>
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
    /// this pack. Shared by the toolbar's Group menu and the canvas right-click menu, which differ
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

    /// <summary>Category management inline on the board, so tagging a plan doesn't mean switching
    /// to the List view. A category created here also gets a group card at the click point, since a
    /// new category matches nothing and there'd be nothing else to show for it. Deleting a category
    /// leaves its card in place showing "nothing matches yet"; recreating the category brings it
    /// back.</summary>
    private MenuItem BuildCategoriesMenu(Point at) =>
        ModCategoryMenu.Build(_packId, _mods, _owner,
            onChanged: () => { Rebuild(); _onModsChanged?.Invoke(); },
            onCreated: name =>
            {
                AddGroupNode(new PlanGroupQuery { Property = PlanGroupProperty.Category, Value = name }, at);
                PlanStatus.Text = $"Created '{name}' - right-click any mod to tag it in.";
            });

    private void OnAddNoteCard(object sender, RoutedEventArgs e) => AddNoteNode(ViewCenter(), edit: true);

    private void OnAddSection(object sender, RoutedEventArgs e)
    {
        if (_board is null) return;
        var p = FreeSpot(ViewCenter(), SectionW, SectionH);
        var node = new PlanNode { Kind = PlanNodeKind.Section, Title = "Section", X = p.X, Y = p.Y };
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

    /// <summary>Where a new card should land for a requested point. Inside a section (the view is
    /// centred on one, or you right-clicked in it), the card drops into that section below its
    /// existing cards. Otherwise it's nudged clear of any overlap near the point.</summary>
    private (double x, double y) ResolveNewCardPosition(Point at, double w, double h, out string? sectionId)
    {
        sectionId = null;
        var section = SectionAt(at);
        if (section is not null && _rect.TryGetValue(section.Id, out var sr))
        {
            // Drop into the shortest column, a cell under its last card, so gaps fill before the
            // section grows. Then grow the section so the new card is inside it; a tightly-hugging
            // section wouldn't recognise a card placed at or just past its bottom edge.
            var cols = ColumnCount(sr);
            var y0 = ContentTop(sr);
            var bottoms = Enumerable.Repeat(y0, cols).ToArray();
            foreach (var r in ContainedNodes(section).Select(n => _rect[n.Id]))
            {
                var span = SpanOf(r.Width, cols);
                var first = ColumnAt(sr, r.X, span);
                for (var col = first; col < first + span; col++)
                    bottoms[col] = Math.Max(bottoms[col], r.Bottom + SlotGap);
            }
            var targetCol = Array.IndexOf(bottoms, bottoms.Min());
            var x = ContentLeft(sr) + targetCol * SlotPitch; // exact column position
            var y = GridOn ? y0 + CeilToGrid(bottoms[targetCol] - y0) : Math.Round(bottoms[targetCol]);

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

    /// <summary>Nudges a new card diagonally, a cell at a time, until it isn't on or against an
    /// existing one, but never so far that it leaves the viewport. Stays on the grid when the grid
    /// is on.</summary>
    private Point FreeSpot(Point start, double w, double h)
    {
        var p = new Point(Place(start.X - w / 2), Place(start.Y - h / 2));
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
            p.X += GridCell;
            p.Y += GridCell;
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
        menu.Items.Add(Item("Snap everything to the grid", SnapEverythingToGrid));
        menu.Items.Add(Item("Clear this board", () => _ = ClearBoardAsync()));
        menu.IsOpen = true;
    }

    /// <summary>Drops a group card for every value of a property that isn't on the board yet,
    /// flowing them across the canvas. The fastest way to start a plan from scratch.</summary>
    private void AutoFill(PlanGroupProperty property)
    {
        if (_board is null) return;
        var options = Plans.GroupOptions(_packId, _mods, App.State.ModMetadata)
            .SelectMany(g => g.Options)
            .Where(q => q.Property == property)
            .ToList();
        if (options.Count == 0) { PlanStatus.Text = "Nothing to lay out for that property yet."; return; }

        var added = new List<PlanNode>();
        foreach (var q in options)
        {
            if (_board.Nodes.Concat(added).Any(n => n.Kind == PlanNodeKind.Group && n.Query is { } e2
                                                    && e2.Property == q.Property
                                                    && string.Equals(e2.Value, q.Value, StringComparison.OrdinalIgnoreCase)))
                continue;
            added.Add(new PlanNode { Kind = PlanNodeKind.Group, Query = q.Clone() });
        }
        LayOutInRows(added);
        _board.Nodes.AddRange(added);

        Commit();
        Rebuild();
        PlanStatus.Text = added.Count == 0 ? "Those cards are already on the board." : $"Added {added.Count} card(s).";
    }

    private void AddModCardsFor(IReadOnlyList<PackMod> mods, string what)
    {
        if (_board is null) return;
        if (mods.Count == 0) { PlanStatus.Text = $"No mods {what}."; return; }

        var added = new List<PlanNode>();
        foreach (var mod in mods)
        {
            if (_board.Nodes.Concat(added).Any(n => n.Kind == PlanNodeKind.Mod && n.ModKey is not null
                                                    && mod.CandidateKeys.Contains(n.ModKey, StringComparer.OrdinalIgnoreCase)))
                continue;
            added.Add(new PlanNode { Kind = PlanNodeKind.Mod, ModKey = mod.Key });
        }
        LayOutInRows(added);
        _board.Nodes.AddRange(added);

        Commit();
        Rebuild();
        PlanStatus.Text = added.Count == 0 ? "Those mods are already on the board." : $"Added {added.Count} card(s).";
    }

    /// <summary>Lays cards out in rows centred on the view, about as many across as down, a cell
    /// apart both ways. Every column is as wide as its widest card and every row as tall as its
    /// tallest, so mixed sizes can't overlap. On the grid, every spot is on it.</summary>
    private void LayOutInRows(IReadOnlyList<PlanNode> cards)
    {
        if (cards.Count == 0) return;
        var sizes = cards.Select(n => SizeOf(n)).ToList();
        var cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(cards.Count)));
        var pitch = sizes.Max(s => s.w) + GridCell;
        var rowHeights = sizes.Chunk(cols).Select(row => row.Max(s => s.h)).ToList();

        var origin = ViewCenter();
        var left = Place(origin.X - (cols * pitch - GridCell) / 2);
        var y = Place(origin.Y - (rowHeights.Sum() + (rowHeights.Count - 1) * GridCell) / 2);
        for (var i = 0; i < cards.Count; i++)
        {
            if (i > 0 && i % cols == 0) y += rowHeights[i / cols - 1] + GridCell;
            cards[i].X = left + (i % cols) * pitch;
            cards[i].Y = y;
        }
    }

    /// <summary>Re-grids only the cards that aren't inside a section, leaving section layouts
    /// alone.</summary>
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

        LayOutInRows(loose);
        Commit();
        Rebuild();
        PlanStatus.Text = $"Tidied {loose.Count} card(s).";
    }

    /// <summary>Puts the whole board on the grid in one undoable step, for a board laid out with
    /// the grid off or before the grid used whole cells. Sections go to whole cells and whole
    /// columns, carrying their cards; a section's cards are then fitted into its columns a cell
    /// apart; loose cards go to the nearest cell. Hand-set sizes go to whole cells too.</summary>
    private void SnapEverythingToGrid()
    {
        if (_board is null || _board.IsEmpty) { PlanStatus.Text = "There's nothing on this board to snap."; return; }
        var before = System.Text.Json.JsonSerializer.Serialize(_board.Nodes, HistoryJson);

        // Everything is worked out from where it's shown; a section's move is carried to its cards here
        // too, before they're fitted to it.
        var at = new Dictionary<string, Rect>(_rect);
        var sections = _board.Nodes.Where(n => n.Kind == PlanNodeKind.Section && at.ContainsKey(n.Id)).ToList();
        foreach (var section in sections)
        {
            var r = at[section.Id];
            section.X = SnapToGrid(r.X);
            section.Y = SnapToGrid(r.Y);
            section.W = SnapToColumns(r.Width);
            at[section.Id] = new Rect(section.X, section.Y, section.W, r.Height);
            foreach (var card in ContainedNodes(section).Where(c => at.ContainsKey(c.Id)))
                at[card.Id] = Rect.Offset(at[card.Id], section.X - r.X, section.Y - r.Y);
        }

        foreach (var card in _board.Nodes.Where(n => n.Kind != PlanNodeKind.Section && at.ContainsKey(n.Id)))
        {
            if (sections.Any(s => s.Id == card.SectionId)) continue; // settled with its section below
            if (card.W > 0) card.W = SizeOf(card, grid: true).w;
            card.X = SnapToGrid(at[card.Id].X);
            card.Y = SnapToGrid(at[card.Id].Y);
            SnapNoteHeight(card);
        }
        foreach (var section in sections) SettleInColumns(section, at);

        if (System.Text.Json.JsonSerializer.Serialize(_board.Nodes, HistoryJson) == before)
        {
            PlanStatus.Text = "Everything on this board is already on the grid.";
            return;
        }
        Commit();
        Rebuild();
        PlanStatus.Text = "Snapped everything to the grid.";
    }

    /// <summary>Fits a section's cards into its columns (X on a column line, width whole columns, Y
    /// whole cells down from the content top), keeping their order down each column with at least a
    /// cell between cards. Rounding alone isn't enough for older boards: cards are now a little
    /// taller and start lower in a section, so two cards a few pixels apart could end up
    /// touching.</summary>
    private void SettleInColumns(PlanNode section, Dictionary<string, Rect> at)
    {
        var sr = at[section.Id];
        var cols = ColumnCount(sr);
        var y0 = ContentTop(sr);
        var above = new List<(int Col, int Span, double Bottom)>();
        foreach (var card in ContainedNodes(section).Where(c => at.ContainsKey(c.Id))
                     .OrderBy(c => at[c.Id].Y).ThenBy(c => at[c.Id].X))
        {
            var r = at[card.Id];
            var span = SpanOf(r.Width, cols);
            var col = ColumnAt(sr, r.X, span);
            var y = y0 + Math.Max(0, SnapToGrid(r.Y - y0));
            foreach (var a in above.Where(a => a.Col < col + span && col < a.Col + a.Span))
                y = Math.Max(y, a.Bottom + SlotGap);

            card.X = ContentLeft(sr) + col * SlotPitch;
            card.Y = y;
            card.W = span * SlotPitch - SlotGap;
            SnapNoteHeight(card);
            above.Add((col, span, y + SizeOf(card, grid: true).h));
        }
    }

    /// <summary>Gives a note whose text height was set by hand the height that makes the whole card
    /// whole cells (what the grid shows it at). A note squeezed down to its title is left alone:
    /// its height follows the title anyway, and rounding up could bring a line of the description
    /// back.</summary>
    private void SnapNoteHeight(PlanNode note)
    {
        if (note.Kind != PlanNodeKind.Note || note.H <= 0 || NoteShowsTitleOnly(note, CardW(note, NoteW, grid: true))) return;
        var extra = NoteExtraH(note);
        note.H = CeilToGrid(note.H + extra) - extra;
    }

    private async Task ClearBoardAsync()
    {
        if (_board is null || _owner is null || _board.IsEmpty) return;
        if (!await AppDialog.ConfirmAsync(_owner, "Clear board",
                $"Remove all {_board.Nodes.Count} card(s) from '{_board.Name}'?", "Clear", "Cancel", danger: true))
            return;
        _board.Nodes.Clear();
        _board.Edges.Clear();
        _selected.Clear();
        Commit();
        Rebuild();
    }

    // ── palette ─────────────────────────────────────────────────────────────────
    // The mods list shares the rail with the boards tree above it. Mods get onto a board by dragging
    // one onto the canvas or double-clicking it.

    private void RefreshPalette()
    {
        var q = PaletteSearch.Text?.Trim();
        IEnumerable<PackMod> list = _mods;
        if (!string.IsNullOrEmpty(q))
            list = list.Where(m => m.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                                   || m.CategoriesLabel.Contains(q, StringComparison.OrdinalIgnoreCase));
        var shown = list.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        PaletteList.ItemsSource = shown;
        PaletteHint.Visibility = string.IsNullOrEmpty(PaletteSearch.Text) ? Visibility.Visible : Visibility.Collapsed;
        // The count tells a search that found nothing apart from a pack that really is that small,
        // which the short list in the rail can't show by itself.
        PaletteSummary.Text = shown.Count == _mods.Count ? $"{shown.Count}" : $"{shown.Count} of {_mods.Count}";
    }

    private void OnPaletteSearch(object sender, TextChangedEventArgs e) { if (IsLoaded) RefreshPalette(); }

    private void OnToggleRail(object sender, RoutedEventArgs e)
    {
        var show = RailColumn.Width.Value == 0;
        RailColumn.Width = show ? new GridLength(RailWidth) : new GridLength(0);
        RailHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
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
        // Drop under the cursor, not centred on it; FreeSpot() recentres by half the card.
        AddModNode(mod, new Point(drop.X + ModW / 2, drop.Y + ModH / 2));
        e.Handled = true;
    }

    // ── pan / zoom / background ─────────────────────────────────────────────────

    private void OnViewportMouseDown(object sender, MouseButtonEventArgs e)
    {
        Viewport.Focus();
        if (ArrowModeActive)
        {
            // While drawing arrows, empty canvas only pans (so a far-off target can still be
            // reached), and a click that doesn't move backs out instead (OnViewportMouseUp). No
            // note or selection box.
            BeginPan(e);
            return;
        }
        if (e.ClickCount == 2) { AddNoteNode(e.GetPosition(BoardCanvas), edit: true); e.Handled = true; return; }

        // Shift on empty canvas draws a selection box instead of panning and adds to the existing
        // selection, so Shift means "extend the selection" for both clicks and sweeps. Without
        // Shift, empty canvas pans and clears the selection.
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
        // A half-drawn arrow follows the pointer, over cards too, since their mouse moves bubble up
        // here.
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
        menu.Items.Add(Item("Add mod card...", () => _ = AddModCardAsync(at)));
        var groupParent = new MenuItem { Header = "Add group card" };
        AddGroupOptionsTo(groupParent.Items, at);
        menu.Items.Add(groupParent);
        menu.Items.Add(Item("Add note here", () => AddNoteNode(at, edit: true)));
        menu.Items.Add(Item("Add section here", () =>
        {
            if (_board is null) return;
            var node = new PlanNode { Kind = PlanNodeKind.Section, Title = "Section", X = Place(at.X), Y = Place(at.Y) };
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

    /// <summary>Keeps the grid locked to the board so panning and zooming feel physical, and hides
    /// it while the grid is off.</summary>
    private void SyncGrid()
    {
        _gridLines.Visibility = GridOn ? Visibility.Visible : Visibility.Collapsed;
        _gridLines.Follow(PanT.X, PanT.Y, ZoomT.ScaleX);
    }

    /// <summary>The toolbar's Grid box. Saved as a launcher setting (see <see cref="GridOn"/>), and the
    /// board is redrawn at once: sizes shown in whole cells and section cards in their columns, or,
    /// off, the geometry as stored.</summary>
    private void OnGridToggled(object sender, RoutedEventArgs e)
    {
        App.State.Settings.PlanGridEnabled = GridToggle.IsChecked == true;
        App.State.Settings.Save();
        SyncGrid();
        Rebuild();
        PlanStatus.Text = GridOn ? "Grid on - cards snap to it." : "Grid off - cards go exactly where you put them.";
    }

    /// <summary>The grid's lines use the theme's tertiary text colour, faint, so they suit
    /// whichever look is on (Slate or Classic, dark or light).</summary>
    private void OnThemeChanged() =>
        _gridLines.Ink = (TryFindResource("TextTertiaryBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0x8A, 0x94, 0xA6);

    /// <summary>The ruled backdrop, drawn line by line in screen space rather than as a tile the
    /// canvas transform scales. A 1 px pen in a scaled tile blurs unevenly at any zoom but 100%;
    /// here every line is one device pixel on a whole pixel at any zoom, every fourth is a shade
    /// stronger, and minor lines fade out as cells shrink, before they get dense enough to
    /// shimmer.</summary>
    private sealed class GridLines : FrameworkElement
    {
        private const double MinorAlpha = 0.11, MajorAlpha = 0.2;
        private double _panX, _panY, _zoom = 1;
        private Color _ink = Color.FromRgb(0x8A, 0x94, 0xA6);

        public GridLines()
        {
            IsHitTestVisible = false;
            RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        }

        public Color Ink
        {
            get => _ink;
            set { _ink = value; InvalidateVisual(); }
        }

        /// <summary>Follows the canvas: the board's origin is at (<paramref name="panX"/>,
        /// <paramref name="panY"/>) on screen, at <paramref name="zoom"/>.</summary>
        public void Follow(double panX, double panY, double zoom)
        {
            _panX = panX;
            _panY = panY;
            _zoom = Math.Max(0.05, zoom);
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight, cell = GridCell * _zoom;
            if (w <= 0 || h <= 0) return;

            // Minor lines at full strength until a cell is 14 px across on screen, gone by 6.
            var minorFade = Math.Clamp((cell - 6) / 8, 0, 1);
            var minor = new StreamGeometry();
            var major = new StreamGeometry();
            using (var minorLines = minor.Open())
            using (var majorLines = major.Open())
            {
                // Board line k is at pan + k x cell on screen, across and down.
                for (var k = (int)Math.Ceiling(-_panX / cell); _panX + k * cell <= w; k++)
                    Line(k, new Point(_panX + k * cell, 0), new Point(_panX + k * cell, h));
                for (var k = (int)Math.Ceiling(-_panY / cell); _panY + k * cell <= h; k++)
                    Line(k, new Point(0, _panY + k * cell), new Point(w, _panY + k * cell));

                void Line(int k, Point from, Point to)
                {
                    var isMajor = k % GridMajorEvery == 0;
                    if (!isMajor && minorFade <= 0) return;
                    var lines = isMajor ? majorLines : minorLines;
                    lines.BeginFigure(from, false, false);
                    lines.LineTo(to, true, false);
                }
            }

            var px = 1 / VisualTreeHelper.GetDpi(this).DpiScaleX; // one device pixel, in DIPs
            if (minorFade > 0) dc.DrawGeometry(null, Stroke(MinorAlpha * minorFade, px), minor);
            dc.DrawGeometry(null, Stroke(MajorAlpha, px), major);
        }

        private Pen Stroke(double alpha, double thickness)
        {
            var pen = new Pen(Frozen(Color.FromArgb((byte)Math.Round(alpha * 255), _ink.R, _ink.G, _ink.B)), thickness);
            pen.Freeze();
            return pen;
        }
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

        var idMap = new Dictionary<string, string>();
        var added = new List<PlanNode>();
        foreach (var n in snap.Nodes)
        {
            var c = n.Clone();      // fresh id
            idMap[n.Id] = c.Id;
            // A cell across and down so the paste doesn't sit on top of the source. Place() puts it
            // on the grid when the grid is on, wherever the copied cards were.
            c.X = Place(c.X + GridCell);
            c.Y = Place(c.Y + GridCell);
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
        // Shared, downscaled and cached across rebuilds (see ModIconCache). The mod card icon is 30px.
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
