using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CloudLauncher;
using CloudLauncher.Services;
using CloudLauncher.Views;

namespace Showcase;

/// <summary>
/// Dragging in the planning page's board rail (ModPlanView.xaml.cs, "rail: drag to reorder"): a
/// category heading lifts its whole category and drops before or after another one, "Uncategorized"
/// stays last and can't be lifted, boards still drag between categories, and a held drag near the
/// rail's edge scrolls it. The drag-and-drop itself is OLE, which a hidden desktop can't drive, so the
/// view's own handlers are called with drag events built for points in the rail. Results are PASS/FAIL
/// lines in the log; the last line counts the failures.
/// </summary>
public static class RailDragScenario
{
    private const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string CategoryFormat = "CloudLauncher.PlanBoardCategory";
    private const string BoardFormat = "CloudLauncher.PlanBoardId";
    private static ModPlanView _view = null!;
    private static int _fails;

    private static object? Call(string m, params object?[] a) =>
        typeof(ModPlanView).GetMethods(F).Single(x => x.Name == m && x.GetParameters().Length == a.Length).Invoke(_view, a);

    private static T Field<T>(string f) => (T)typeof(ModPlanView).GetField(f, F)!.GetValue(_view)!;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (!ok) _fails++;
        Program.Log($"{(ok ? "PASS" : "FAIL")}  {name} {(ok ? "" : detail)}");
    }

    private static Guid Pack => FakeWorld.Hearthstone;
    private static StackPanel RailList => (StackPanel)_view.FindName("RailList");
    private static ScrollViewer RailScroll => (ScrollViewer)_view.FindName("RailScroll");
    private static string Cats() => string.Join(",", App.State.ModPlans.BoardCategories(Pack));
    private static string Order() => string.Join(",", App.State.ModPlans.Boards(Pack).Select(b => b.Id));

    private static List<(FrameworkElement Row, string? Category)> Headers() =>
        Field<List<(FrameworkElement Row, string? Category)>>("_railHeaders");

    private static FrameworkElement Header(string? category) => Headers().Single(h => h.Category == category).Row;

    private static FrameworkElement BoardRow(string id) =>
        Field<List<(FrameworkElement Row, PlanBoard Board)>>("_railRows").Single(r => r.Board.Id == id).Row;

    private static string HeaderOrder() => string.Join(",", Headers().Select(h => h.Category ?? "-"));

    private static double Top(FrameworkElement e) => e.TranslatePoint(new Point(0, 0), RailList).Y;

    private static string TipText(FrameworkElement e) =>
        e.ToolTip is ToolTip { Content: TextBlock t } ? t.Text : e.ToolTip?.ToString() ?? "";

    /// <summary>A drag event at a height in the rail's row host, the way OLE would deliver it.</summary>
    private static DragEventArgs DragAt(IDataObject data, double y, RoutedEvent routed)
    {
        var ctor = typeof(DragEventArgs).GetConstructors(F).Single(c => c.GetParameters().Length == 5);
        var e = (DragEventArgs)ctor.Invoke(new object[] { data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Move, RailList, new Point(40, y) });
        e.RoutedEvent = routed;
        return e;
    }

    private static DragEventArgs Over(IDataObject data, double y)
    {
        var e = DragAt(data, y, DragDrop.DragOverEvent);
        Call("OnRailDragOver", RailScroll, e);
        return e;
    }

    /// <summary>Drags over a height, then drops there, as a real drag ends.</summary>
    private static void DropAt(IDataObject data, double y)
    {
        Over(data, y);
        Call("OnRailDrop", RailScroll, DragAt(data, y, DragDrop.DropEvent));
        Program.Pump(150);
    }

    private static DataObject CategoryData(string name) => new(CategoryFormat, name);

    private static double? LineY()
    {
        var line = typeof(ModPlanView).GetField("_railLine", F)!.GetValue(_view);
        return line is null ? null : (double)line.GetType().GetField("_y", F)!.GetValue(line)!;
    }

    /// <summary>The middle of a heading's top or bottom half, where a pointer would be.</summary>
    private static double Upper(FrameworkElement e) => Top(e) + e.ActualHeight * 0.25;
    private static double Lower(FrameworkElement e) => Top(e) + e.ActualHeight * 0.75;

    public static void Run()
    {
        var shell = Program.MakeShell(1280, 820);
        System.Threading.SynchronizationContext.SetSynchronizationContext(
            new System.Windows.Threading.DispatcherSynchronizationContext(shell.Dispatcher));
        shell.Show();
        Program.Pump(1500);

        PlanBoard Make(string id, string name, string? category) => new()
        {
            Id = id, Name = name, Category = category, SectionMembershipSet = true,
            Nodes = { new PlanNode { Kind = PlanNodeKind.Note, X = 48, Y = 48, W = 240, H = 96, Title = name } }
        };
        var plans = new PackPlans
        {
            Boards =
            {
                Make("a1", "Launch plan", "Alpha"), Make("a2", "Mod list", "Alpha"),
                Make("b1", "Ports", "Beta"),
                Make("g1", "Shaders", "Gamma"), Make("g2", "Clouds", "Gamma"),
                Make("u1", "Loose ideas", null)
            },
            BoardCategories = { "Alpha", "Beta", "Gamma" },
            LastBoardId = "a1"
        };
        var file = Path.Combine(App.State.Packs.GameDir(Pack), ".cloudlauncher", "plans.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(plans));
        App.State.ModPlans.Invalidate(Pack);

        _view = new ModPlanView();
        var host = new Window
        {
            Left = -24000, Top = -24000, Width = 1100, Height = 640, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Content = _view,
        };
        host.Show();
        Program.Pump(800);
        _view.Load(Pack, Array.Empty<PackMod>(), shell);
        Program.Pump(1500);

        Check("(seeded) categories and headings in order", Cats() == "Alpha,Beta,Gamma" && HeaderOrder() == "Alpha,Beta,Gamma,-",
            $"{Cats()} / {HeaderOrder()}");
        Check("a category heading says it drags", TipText(Header("Beta")).Contains("drag to reorder"), TipText(Header("Beta")));

        // ── a press on a heading becomes a category drag, on a board row a board drag ──
        // As real input arrives: the general mouse-down tunnels from the window, and every element on
        // the way re-raises it as its own (direct) left-button event, the page before the row.
        void Press(FrameworkElement row)
        {
            var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = Mouse.PreviewMouseDownEvent };
            row.RaiseEvent(down);
        }
        Press(Header("Beta"));
        Check("pressing a heading holds that category", Field<object?>("_railPressCategory") as string == "Beta"
            && Field<object?>("_railPressBoard") is null, $"{Field<object?>("_railPressCategory")}");
        Press(Header(null));
        Check("pressing Uncategorized holds nothing (it can't be moved)", Field<object?>("_railPressCategory") is null
            && Field<object?>("_railPressRow") is null, $"{Field<object?>("_railPressCategory")} {Field<object?>("_railPressRow")}");
        Press(BoardRow("b1"));
        Check("pressing a board holds that board", Field<object?>("_railPressBoard") is PlanBoard { Id: "b1" }
            && Field<object?>("_railPressCategory") is null);
        var release = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = Mouse.PreviewMouseUpEvent };
        ((FrameworkElement)_view.FindName("BoardTitle")).RaiseEvent(release);
        Check("a release anywhere in the page lets go", Field<object?>("_railPressRow") is null);

        object? Take(Point p) => Call("TakeRailDrag", p);
        (FrameworkElement Row, DataObject Data)? Lift(FrameworkElement row, PlanBoard? board, string? category, double dy)
        {
            var at = row.TranslatePoint(new Point(20, row.ActualHeight / 2), _view);
            Call("PressRail", row, board, category, at);
            var r = Take(new Point(at.X, at.Y + dy));
            return r is null ? null : ((FrameworkElement Row, DataObject Data))r;
        }
        var beta = Header("Beta");
        Check("a heading doesn't lift on a 3 px wobble", Lift(beta, null, "Beta", 3) is null);
        var lifted = Lift(beta, null, "Beta", 12);
        Check("a heading lifts after 12 px, carrying its category",
            lifted is { } l && ReferenceEquals(l.Row, beta) && l.Data.GetData(CategoryFormat) as string == "Beta",
            lifted is { } l2 ? string.Join(",", l2.Data.GetFormats()) : "(nothing)");
        Check("...and the press is spent", Take(new Point(20, 400)) is null);
        var board = App.State.ModPlans.Boards(Pack).Single(b => b.Id == "b1");
        lifted = Lift(BoardRow("b1"), board, null, -12);
        Check("a board row still lifts as a board", lifted is { } lb && lb.Data.GetData(BoardFormat) as string == "b1");

        // ── where a heading would land ──
        var gammaData = CategoryData("Gamma");
        Over(gammaData, 2);
        Check("over the top of the rail: first", Field<int>("_railDropCategoryAt") == 0 && LineY() is { } y0 && y0 <= 3,
            $"at {Field<int>("_railDropCategoryAt")} line {LineY()}");
        Check("a heading drag never sets up a board drop", Field<object?>("_railDropCategory") is null && Field<object?>("_railDropAfter") is null);
        Over(gammaData, Upper(Header("Beta")));
        Check("top half of Beta's block: before Beta", Field<int>("_railDropCategoryAt") == 1, $"{Field<int>("_railDropCategoryAt")}");
        var aboveBeta = LineY();
        var betaTop = Top(Header("Beta"));
        var alphaEnd = Top(BoardRow("a2")) + BoardRow("a2").ActualHeight;
        Check("...the line sits in the gap above Beta's heading", aboveBeta is { } ab && ab > alphaEnd && ab < betaTop,
            $"line {aboveBeta:0.0}, Alpha ends {alphaEnd:0.0}, Beta starts {betaTop:0.0}");
        // Beta's block is its heading and one board: the board row is its lower half.
        Over(gammaData, Top(BoardRow("b1")) + BoardRow("b1").ActualHeight - 2);
        Check("bottom half of Beta's block: after Beta", Field<int>("_railDropCategoryAt") == 2, $"{Field<int>("_railDropCategoryAt")}");
        Over(gammaData, Upper(Header(null)));
        Check("over Uncategorized: last", Field<int>("_railDropCategoryAt") == 3, $"{Field<int>("_railDropCategoryAt")}");
        var aboveLoose = LineY();
        Check("...the line sits above Uncategorized", aboveLoose is { } al && al < Top(Header(null))
            && al > Top(BoardRow("g2")) + BoardRow("g2").ActualHeight, $"{aboveLoose}");
        // The picture: Alpha carried down under Beta. The line draws in the next layout pass, as it
        // does mid-drag.
        Over(CategoryData("Alpha"), Top(BoardRow("b1")) + BoardRow("b1").ActualHeight - 2);
        Program.Pump(150);
        Program.Shoot(_view, "planning-rail-drag-line", 2);

        // ── drops ──
        DropAt(gammaData, 2);
        Check("Gamma dropped at the top", Cats() == "Gamma,Alpha,Beta", Cats());
        Check("...its boards come with it", Order() == "g1,g2,a1,a2,b1,u1", Order());
        Check("...the rail redraws in the new order", HeaderOrder() == "Gamma,Alpha,Beta,-", HeaderOrder());
        Check("...the line is gone", LineY() is null);
        App.State.ModPlans.Flush(Pack); // the file follows a burst of changes a moment later
        var saved = ModPlanService.Parse(File.ReadAllText(file));
        Check("...saved to plans.json", saved is not null && string.Join(",", saved.BoardCategories) == "Gamma,Alpha,Beta",
            saved is null ? "(unreadable)" : string.Join(",", saved.BoardCategories));

        DropAt(CategoryData("Gamma"), Top(BoardRow("b1")) + BoardRow("b1").ActualHeight - 2);
        Check("Gamma dropped under Beta: back to the end", Cats() == "Alpha,Beta,Gamma", Cats());
        Check("...boards follow", Order() == "a1,a2,b1,g1,g2,u1", Order());

        DropAt(CategoryData("Alpha"), Upper(Header(null)));
        Check("Alpha dropped on Uncategorized: last of the categories", Cats() == "Beta,Gamma,Alpha" && HeaderOrder() == "Beta,Gamma,Alpha,-",
            $"{Cats()} / {HeaderOrder()}");

        var below = Top(BoardRow("u1")) + BoardRow("u1").ActualHeight + 30;
        DropAt(CategoryData("Beta"), RailList.ActualHeight + 40);
        Check("Beta dropped in the space under everything: last", Cats() == "Gamma,Alpha,Beta", $"{Cats()} (dropped at {below:0})");

        DropAt(CategoryData("Alpha"), Upper(Header("Alpha")));
        Check("dropped back onto itself: nothing changes", Cats() == "Gamma,Alpha,Beta", Cats());

        // ── a folded category is one heading tall, and still a drop target ──
        Header("Gamma").RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        Program.Pump(150);
        var folded = Field<HashSet<string>>("_railCollapsed").Contains("Gamma");
        Check("(Gamma folded by a click on its heading)", folded, string.Join(",", Field<HashSet<string>>("_railCollapsed")));
        DropAt(CategoryData("Beta"), Lower(Header("Gamma")));
        Check("dropped on the lower half of folded Gamma: after it", Cats() == "Gamma,Beta,Alpha", Cats());

        // ── boards still drag between categories ──
        var boardData = new DataObject(BoardFormat, "u1");
        Over(boardData, Top(BoardRow("a1")) + BoardRow("a1").ActualHeight - 3);
        Check("a board drag never sets up a heading drop", Field<int>("_railDropCategoryAt") == -1, $"{Field<int>("_railDropCategoryAt")}");
        DropAt(boardData, Top(BoardRow("a1")) + BoardRow("a1").ActualHeight - 3);
        var u1 = App.State.ModPlans.Boards(Pack).Single(b => b.Id == "u1");
        Check("a board dropped under Launch plan joins Alpha there", u1.Category == "Alpha"
            && Order().Contains("a1,u1,a2"), $"{u1.Category} {Order()}");
        Check("...and with nothing left uncategorized that heading goes", !HeaderOrder().EndsWith(",-"), HeaderOrder());

        // ── something else dragged over the rail ──
        var other = Over(new DataObject(DataFormats.Text, "a mod"), Upper(Header("Beta")));
        Check("a mod dragged over the rail is refused", other.Effects == DragDropEffects.None && LineY() is null, $"{other.Effects}");

        // ── the server gets the new order, and keeps it ──
        var sent = false;
        string? stored = null;
        for (var i = 0; i < 60 && !sent; i++)
        {
            Program.Pump(100);
            stored = FakeWorld.StoredPlans(Pack);
            var m = stored is null ? null : Regex.Match(stored, "\"BoardCategories\"\\s*:\\s*\\[([^\\]]*)\\]");
            sent = m is { Success: true } && Regex.Replace(m.Groups[1].Value, "[\\s\"]", "") == "Gamma,Beta,Alpha";
        }
        Check("the new order is sent to the server", sent, stored ?? "(nothing stored)");
        Program.Pump(1500);
        Check("...and still the order here after the sync", Cats() == "Gamma,Beta,Alpha" && HeaderOrder().StartsWith("Gamma,Beta,Alpha"),
            $"{Cats()} / {HeaderOrder()}");

        // ── held near the edge, the rail scrolls ──
        for (var i = 0; i < 18; i++) App.State.ModPlans.SetBoardCategory(Pack, App.State.ModPlans.AddBoard(Pack, $"Extra {i}"), "Beta");
        Call("RefreshBoardRail");
        host.Height = 420;
        Program.Pump(500);
        var scroll = RailScroll;
        Check("(the rail now scrolls)", scroll.ScrollableHeight > 40, $"{scroll.ScrollableHeight}");
        scroll.ScrollToVerticalOffset(0);
        Program.Pump(100);
        // A height against the scroller, as the pointer is, turned into one in the row host.
        void OverScroller(IDataObject data, double scrollerY) =>
            Over(data, scrollerY - RailList.TranslatePoint(new Point(0, 0), scroll).Y);
        for (var i = 0; i < 4; i++) { OverScroller(CategoryData("Alpha"), scroll.ActualHeight - 6); Program.Pump(40); }
        var down = scroll.VerticalOffset;
        Check("held near the bottom: the rail scrolls down", down > 0, $"{down}");
        for (var i = 0; i < 2; i++) { OverScroller(new DataObject(BoardFormat, "a1"), 6); Program.Pump(40); }
        Check("held near the top: it scrolls back up", scroll.VerticalOffset < down, $"{scroll.VerticalOffset} (was {down})");
        Call("ClearRailInsertion");
        Program.Pump(100);
        Program.Shoot(_view, "planning-rail-drag-after", 1);

        Program.Log($"rail drag checks done: {_fails} failed");
        host.Close();
        shell.Hide();
        Program.Pump(300);
    }
}
