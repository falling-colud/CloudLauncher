using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using CloudLauncher.Animations;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>
/// One popup for everything to do with planning boards — create, rename, duplicate, delete, reorder
/// (drag), and group into categories. Edits are applied live through <see cref="ModPlanService"/>;
/// closing (or double-clicking a board) returns the board the planning view should switch to.
/// </summary>
public partial class ManageBoardsDialog : UserControl
{
    // MDL2 glyphs
    private const int IcUp = 0xE70E, IcDown = 0xE70D, IcRename = 0xE70F, IcDelete = 0xE74D,
                      IcTag = 0xE8EC, IcCopy = 0xE8C8;

    private const string Uncategorized = "Uncategorized";
    private const string BoardDragFormat = "CloudLauncher.PlanBoardId";

    private readonly Guid _packId;
    private readonly MainWindow _owner;
    private readonly TaskCompletionSource<PlanBoard?> _tcs = new();
    private PlanBoard? _openBoard; // board to activate on close (double-clicked / created)

    // drag-reorder state
    private PlanBoard? _pressBoard;
    private Point _pressPoint;
    private readonly List<(FrameworkElement el, PlanBoard board)> _rowEls = new();
    private readonly List<(FrameworkElement el, string? category)> _headerEls = new();
    private string? _dropCategory;
    private PlanBoard? _dropAfter;
    private LineAdorner? _line;

    public ManageBoardsDialog(Guid packId, MainWindow owner, PlanBoard? current)
    {
        InitializeComponent();
        _packId = packId;
        _owner = owner;
        _openBoard = current;
        Loaded += (_, _) => { Animate.SlideFadeIn(this, 0, 14, 200); Rebuild(); };
    }

    public Task<PlanBoard?> Result => _tcs.Task;
    public void Cancel() => _tcs.TrySetResult(_openBoard);

    /// <summary>Shows the dialog; returns the board to switch to (the current one if nothing changed).</summary>
    public static async Task<PlanBoard?> ShowAsync(MainWindow host, Guid packId, PlanBoard? current)
    {
        var card = new ManageBoardsDialog(packId, host, current);
        await host.ShowCardAsync(card, card.Result, card.Cancel);
        return card.Result.Result;
    }

    private ModPlanService Plans => App.State.ModPlans;

    // ── render ──────────────────────────────────────────────────────────────────

    private void Rebuild()
    {
        ListHost.Children.Clear();
        _rowEls.Clear();
        _headerEls.Clear();

        var boards = Plans.Boards(_packId).ToList();
        var categories = Plans.BoardCategories(_packId).ToList();

        // A group per declared category (in order), then Uncategorized last if anything's loose.
        var groups = new List<(string? key, string label, List<PlanBoard> items)>();
        foreach (var cat in categories)
            groups.Add((cat, cat, boards.Where(b => SameCat(b.Category, cat)).ToList()));
        var loose = boards.Where(b => string.IsNullOrEmpty(b.Category)
                                      || !categories.Any(c => SameCat(c, b.Category))).ToList();
        if (loose.Count > 0 || groups.Count == 0)
            groups.Add((null, Uncategorized, loose));

        for (var g = 0; g < groups.Count; g++)
        {
            var (key, label, items) = groups[g];
            var header = BuildCategoryHeader(key, label, g, categories.Count);
            _headerEls.Add((header, key));
            ListHost.Children.Add(header);
            if (items.Count == 0)
                ListHost.Children.Add(new TextBlock
                {
                    Text = "no boards yet — drag one here, or use the move-to-category button",
                    FontSize = 11, FontStyle = FontStyles.Italic,
                    Foreground = Res("TextTertiaryBrush"), Margin = new Thickness(24, 2, 0, 8)
                });
            foreach (var b in items)
            {
                var row = BuildBoardRow(b, boards);
                _rowEls.Add((row, b));
                ListHost.Children.Add(row);
            }
        }
    }

    private FrameworkElement BuildCategoryHeader(string? key, string label, int index, int categoryCount)
    {
        var grid = new Grid { Margin = new Thickness(2, index == 0 ? 2 : 10, 2, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new TextBlock
        {
            Text = label.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold,
            Foreground = Res("TextTertiaryBrush"), VerticalAlignment = VerticalAlignment.Center
        });

        // Real categories can be reordered / renamed / removed; the synthetic "Uncategorized" can't.
        if (key is not null)
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            Grid.SetColumn(actions, 1);
            actions.Children.Add(IconBtn(IcUp, "Move category up", () => Plans.MoveBoardCategory(_packId, key, index - 1), index > 0));
            actions.Children.Add(IconBtn(IcDown, "Move category down", () => Plans.MoveBoardCategory(_packId, key, index + 2), index < categoryCount - 1));
            actions.Children.Add(IconBtn(IcRename, "Rename category", () => _ = RenameCategoryAsync(key)));
            actions.Children.Add(IconBtn(IcDelete, "Delete category (its boards become uncategorized)", () => Plans.RemoveBoardCategory(_packId, key)));
            grid.Children.Add(actions);
        }
        return grid;
    }

    private FrameworkElement BuildBoardRow(PlanBoard board, List<PlanBoard> allBoards)
    {
        var row = new Border
        {
            Background = Res("Surface2Brush"),
            BorderBrush = ReferenceEquals(board, _openBoard) ? Res("AccentBrush") : Res("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(16, 0, 0, 4),
            Padding = new Thickness(10, 6, 6, 6),
            Cursor = Cursors.SizeAll,
            ToolTip = "Drag to reorder · double-click to open"
        };
        row.MouseLeftButtonUp += (_, e) => { if (e.ClickCount == 2) { _openBoard = board; _tcs.TrySetResult(board); } };
        row.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (IsInButton(e.OriginalSource as DependencyObject)) return;
            _pressBoard = board;
            _pressPoint = e.GetPosition(this);
        };
        row.PreviewMouseMove += OnRowMouseMove;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // grip affordance (MDL2 GripperBarVertical)
        var grip = new TextBlock
        {
            Text = char.ConvertFromUtf32(0xE784), FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12,
            Foreground = Res("TextTertiaryBrush"), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        grid.Children.Add(grip);

        var name = new TextBlock
        {
            Text = board.Name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            Foreground = Res("TextPrimaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(actions, 2);
        actions.Children.Add(IconBtn(IcTag, "Move to category", () => ShowCategoryMenu(board)));
        actions.Children.Add(IconBtn(IcRename, "Rename", () => _ = RenameBoardAsync(board)));
        actions.Children.Add(IconBtn(IcCopy, "Duplicate", () => { Plans.DuplicateBoard(_packId, board); Status.Text = "Duplicated."; }));
        actions.Children.Add(IconBtn(IcDelete, "Delete", () => _ = DeleteBoardAsync(board), enabled: allBoards.Count > 1));
        grid.Children.Add(actions);

        row.Child = grid;
        return row;
    }

    // ── drag reorder ────────────────────────────────────────────────────────────

    private void OnRowMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressBoard is null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var board = _pressBoard;
        _pressBoard = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(BoardDragFormat, board.Id), DragDropEffects.Move);
        ClearInsertion();
    }

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(BoardDragFormat)) { e.Effects = DragDropEffects.None; return; }
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        ComputeDrop(e.GetPosition(ListHost).Y);
    }

    private void OnListDragLeave(object sender, DragEventArgs e) => ClearInsertion();

    private void OnListDrop(object sender, DragEventArgs e)
    {
        var cat = _dropCategory;
        var after = _dropAfter;
        ClearInsertion();

        if (e.Data.GetData(BoardDragFormat) is not string id) return;
        var board = Plans.Boards(_packId).FirstOrDefault(b => b.Id == id);
        if (board is null) return;
        e.Handled = true;

        Plans.SetBoardCategory(_packId, board, cat);        // assign + regroup
        var boards = Plans.Boards(_packId).ToList();
        int target;
        if (after is not null && !ReferenceEquals(after, board))
            target = boards.IndexOf(after) + 1;             // drop just below the anchor
        else
        {
            var firstInCat = boards.FirstOrDefault(b => SameCat(b.Category, cat) && !ReferenceEquals(b, board));
            target = firstInCat is null ? boards.IndexOf(board) : boards.IndexOf(firstInCat); // top of category
        }
        Plans.MoveBoard(_packId, board, target);
        Rebuild();
    }

    /// <summary>Works out which category the pointer is over and which board (if any) it should land
    /// after, then draws the insertion line there.</summary>
    private void ComputeDrop(double y)
    {
        // Category = the group whose header is the last one at or above the pointer.
        string? cat = _headerEls.Count > 0 ? _headerEls[0].category : null;
        FrameworkElement? catHeader = _headerEls.Count > 0 ? _headerEls[0].el : null;
        foreach (var (el, c) in _headerEls)
            if (TopIn(el) <= y) { cat = c; catHeader = el; }

        var rows = _rowEls.Where(r => SameCat(r.board.Category, cat)).ToList();
        PlanBoard? after = null;
        double lineY;
        if (rows.Count == 0)
            lineY = catHeader is null ? 0 : TopIn(catHeader) + catHeader.ActualHeight;
        else
        {
            FrameworkElement? before = null;
            foreach (var (el, board) in rows)
            {
                if (y < TopIn(el) + el.ActualHeight / 2) { before = el; break; }
                after = board;
            }
            lineY = before is not null
                ? TopIn(before) - 2
                : TopIn(rows[^1].el) + rows[^1].el.ActualHeight + 1;
        }

        _dropCategory = cat;
        _dropAfter = after;
        ShowInsertion(lineY);
    }

    private double TopIn(FrameworkElement el) => el.TranslatePoint(new Point(0, 0), ListHost).Y;

    private void ShowInsertion(double y)
    {
        var layer = AdornerLayer.GetAdornerLayer(ListHost);
        if (layer is null) return;
        if (_line is null) { _line = new LineAdorner(ListHost); layer.Add(_line); }
        _line.Y = y;
        _line.InvalidateVisual();
    }

    private void ClearInsertion()
    {
        if (_line is null) return;
        AdornerLayer.GetAdornerLayer(ListHost)?.Remove(_line);
        _line = null;
    }

    /// <summary>The accent insertion line drawn across the list while dragging a board.</summary>
    private sealed class LineAdorner : Adorner
    {
        public double Y;
        public LineAdorner(UIElement el) : base(el) { IsHitTestVisible = false; }
        /// <summary>Built per draw so it follows a colour change instead of keeping the accent
        /// the app started with.</summary>
        private static Pen BuildPen()
        {
            var p = new Pen((Brush)Application.Current.FindResource("AccentBrush"), 2);
            p.Freeze();
            return p;
        }
        protected override void OnRender(DrawingContext dc)
        {
            var w = ((FrameworkElement)AdornedElement).ActualWidth;
            dc.DrawLine(BuildPen(), new Point(6, Y), new Point(w - 6, Y));
        }
    }

    // ── actions ─────────────────────────────────────────────────────────────────

    private void OnNewBoard(object sender, RoutedEventArgs e) => _ = NewBoardAsync();

    private async Task NewBoardAsync()
    {
        var name = await _owner.PromptAsync("New board", "Board name", "Plan");
        if (string.IsNullOrWhiteSpace(name)) return;
        _openBoard = Plans.AddBoard(_packId, name!);
        Rebuild();
        Status.Text = "Board added.";
    }

    private void OnNewCategory(object sender, RoutedEventArgs e) => _ = NewCategoryAsync();

    private async Task NewCategoryAsync()
    {
        var name = await _owner.PromptAsync("New category", "Category name", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        Plans.AddBoardCategory(_packId, name!);
        Rebuild();
    }

    private async Task RenameBoardAsync(PlanBoard board)
    {
        var name = await _owner.PromptAsync("Rename board", "Board name", board.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        Plans.RenameBoard(_packId, board, name!);
        Rebuild();
    }

    private async Task RenameCategoryAsync(string category)
    {
        var name = await _owner.PromptAsync("Rename category", "Category name", category);
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name, category, StringComparison.Ordinal)) return;
        Plans.RenameBoardCategory(_packId, category, name!);
        Rebuild();
    }

    private async Task DeleteBoardAsync(PlanBoard board)
    {
        if (!await AppDialog.ConfirmAsync(_owner, "Delete board",
                $"Delete “{board.Name}” and everything on it?", "Delete", "Cancel", danger: true)) return;
        Plans.RemoveBoard(_packId, board);
        if (ReferenceEquals(_openBoard, board)) _openBoard = Plans.Boards(_packId).FirstOrDefault();
        Rebuild();
    }

    private void ShowCategoryMenu(PlanBoard board)
    {
        var menu = new ContextMenu();
        foreach (var cat in Plans.BoardCategories(_packId))
        {
            var captured = cat;
            var item = new MenuItem
            {
                Header = cat,
                InputGestureText = SameCat(board.Category, cat) ? "✓" : ""
            };
            item.Click += (_, _) => { Plans.SetBoardCategory(_packId, board, captured); Rebuild(); };
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        var none = new MenuItem { Header = "Uncategorized", InputGestureText = string.IsNullOrEmpty(board.Category) ? "✓" : "" };
        none.Click += (_, _) => { Plans.SetBoardCategory(_packId, board, null); Rebuild(); };
        menu.Items.Add(none);
        var create = new MenuItem { Header = "New category…" };
        create.Click += (_, _) => _ = NewCategoryForBoardAsync(board);
        menu.Items.Add(create);
        menu.IsOpen = true;
    }

    private async Task NewCategoryForBoardAsync(PlanBoard board)
    {
        var name = await _owner.PromptAsync("New category", "Category name", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        Plans.SetBoardCategory(_packId, board, name!);
        Rebuild();
    }

    private void OnDone(object sender, RoutedEventArgs e) => _tcs.TrySetResult(_openBoard);

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _tcs.TrySetResult(_openBoard); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static bool SameCat(string? a, string? b) =>
        (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) || string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsInButton(DependencyObject? from)
    {
        for (var d = from; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is Button) return true;
        return false;
    }

    private Button IconBtn(int glyph, string tip, Action onClick, bool enabled = true)
    {
        var btn = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Content = char.ConvertFromUtf32(glyph),
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 12, MinWidth = 28, MinHeight = 28, Padding = new Thickness(0),
            IsEnabled = enabled, ToolTip = tip
        };
        // Every action mutates via the service; re-render unless the dialog is closing.
        btn.Click += (_, _) => { onClick(); if (!_tcs.Task.IsCompleted) Rebuild(); };
        return btn;
    }

    private Brush Res(string key) => (Brush)FindResource(key);
}
