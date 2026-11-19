using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CloudLauncher;
using CloudLauncher.Services;
using CloudLauncher.Views;

namespace Showcase;

/// <summary>
/// The board with a pen, fingers, a palm and the touchpad, through the view's own code: reflection on the
/// methods its stylus handlers hand each event to, with a clock the test turns, and fake WPF touch devices
/// (real touch routing) for the board with drawing off. Leon's four reports from his Surface (11-19) one by
/// one, and the stale states behind them. Results are PASS/FAIL lines; the last line counts the failures.
/// </summary>
/// <remarks>What this can't reach is WPF's routing of real stylus events (there is no digitizer here): which
/// element a lift goes to, and that a hover left unhandled moves WPF's mouse. Those come from the decompiled
/// PresentationCore 10.0 (WispLogic.PromoteMainToOther, WispStylusDevice.ChangeStylusCapture).</remarks>
public static class PenTouchScenario
{
    private const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
    private static ModPlanView _view = null!;
    private static int _fails;
    private static long _now = 5_000_000;

    private static object? Call(string m, params object?[] a)
    {
        var method = typeof(ModPlanView).GetMethods(F).Single(x => x.Name == m && x.GetParameters().Length == a.Length);
        return method.Invoke(_view, a);
    }

    private static object? Get(string f) => typeof(ModPlanView).GetField(f, F)!.GetValue(_view);
    private static void Set(string f, object? v) => typeof(ModPlanView).GetField(f, F)!.SetValue(_view, v);

    private static void Check(string name, bool ok, string detail = "")
    {
        if (!ok) _fails++;
        Program.Log($"{(ok ? "PASS" : "FAIL")}  {name} {(ok ? "" : detail)}");
    }

    private static ScaleTransform ZoomT => (ScaleTransform)_view.FindName("ZoomT");
    private static TranslateTransform PanT => (TranslateTransform)_view.FindName("PanT");
    private static double Zoom => ZoomT.ScaleX;
    private static string View => $"zoom {Zoom:0.###} pan {PanT.X:0.#},{PanT.Y:0.#}";
    private static PlanBoard Board => (PlanBoard)Get("_board")!;
    private static int Strokes => Board.Strokes?.Count ?? 0;

    private static object? Ink => Get("_ink");
    private static string InkKind => Ink is { } g
        ? $"{g.GetType().GetField("Kind")!.GetValue(g)}/{g.GetType().GetField("Device")!.GetValue(g)}"
        : "none";
    private static bool Pinching => Get("_pinch") is not null;
    private static IDictionary Fingers => (IDictionary)Get("_fingers")!;
    private static bool Ignored(int id) => Fingers[id] is { } f && (bool)f.GetType().GetField("Ignored")!.GetValue(f)!;

    private static void ResetView()
    {
        ZoomT.ScaleX = ZoomT.ScaleY = 1;
        PanT.X = PanT.Y = 0;
    }

    private static bool At(double zoom, double x, double y) =>
        Math.Abs(Zoom - zoom) < 1e-9 && Math.Abs(PanT.X - x) < 1e-9 && Math.Abs(PanT.Y - y) < 1e-9;

    private static StylusPointCollection Pt(double x, double y) => new([new StylusPoint(x, y, 0.5f)]);

    // The calls the view's stylus handlers make, with what they read off the event.
    private static void PenNear(double x = 500, double y = 300)
    {
        Call("PenSeen", new object?[] { null });   // the page saw it
        Call("PenHover", 9, false, false, new Point(x, y)); // and the drawing surface
    }
    private static void PenGone() => Call("PenLeft");
    private static void PenDown(double x, double y) => Call("PenDown", 9, false, false, new Point(x, y), Pt(x, y), false);
    private static void PenMove(double x, double y) => Call("PenMove", 9, Pt(x, y));
    private static void PenUp(double x, double y) => Call("PenUp", 9, Pt(x, y));
    private static void PenStroke(double x, double y)
    {
        PenDown(x, y);
        PenMove(x + 40, y + 5);
        PenMove(x + 80, y + 15);
        PenUp(x + 100, y + 20);
    }
    private static void FingerDown(int id, double x, double y, Func<bool>? down = null) =>
        Call("FingerDown", id, new Point(x, y), Pt(x, y), down ?? (() => true));
    private static void FingerMove(int id, double x, double y) => Call("FingerMove", id, new Point(x, y), Pt(x, y));
    private static void FingerUp(int id, double x, double y) => Call("FingerUp", id, Pt(x, y));

    /// <summary>Time for the pen to count as gone (its grace after leaving range is 600 ms).</summary>
    private static void PenAway()
    {
        PenGone();
        _now += 700;
    }

    /// <summary>A finger WPF itself routes: its touch events go wherever WPF's hit test sends them.</summary>
    private sealed class FakeTouch(int id, Window window) : TouchDevice(id)
    {
        private Point _at;

        public void Down(Point at)
        {
            _at = at;
            SetActiveSource(PresentationSource.FromVisual(window));
            Activate();
            ReportDown();
        }

        public void Move(Point at)
        {
            _at = at;
            ReportMove();
        }

        public void Up()
        {
            ReportUp();
            Deactivate();
        }

        /// <summary>Gone without a lift this page ever sees.</summary>
        public void Vanish() => Deactivate();

        public override TouchPoint GetTouchPoint(IInputElement? relativeTo)
        {
            var p = relativeTo is Visual v && !ReferenceEquals(v, window) ? window.TransformToDescendant(v).Transform(_at) : _at;
            return new TouchPoint(this, p, new Rect(p, new Size(1, 1)), TouchAction.Move);
        }

        public override TouchPointCollection GetIntermediateTouchPoints(IInputElement relativeTo) => new();
    }

    public static void Run()
    {
        var shell = Program.MakeShell(1280, 820);
        System.Threading.SynchronizationContext.SetSynchronizationContext(
            new System.Windows.Threading.DispatcherSynchronizationContext(shell.Dispatcher));
        shell.Show();
        Program.Pump(1500);

        var board = new PlanBoard { Name = "Pen and fingers", SectionMembershipSet = true };
        board.Nodes.Add(new PlanNode { Id = "n1", Kind = PlanNodeKind.Note, X = 24, Y = 24, W = 160, H = 0, Title = "Corner note" });
        var plans = new PackPlans { Boards = { board }, LastBoardId = board.Id };
        var file = System.IO.Path.Combine(App.State.Packs.GameDir(FakeWorld.Hearthstone), ".cloudlauncher", "plans.json");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        System.IO.File.WriteAllText(file, JsonSerializer.Serialize(plans));
        App.State.ModPlans.Invalidate(FakeWorld.Hearthstone);

        _view = new ModPlanView();
        var host = new Window
        {
            Left = -24000, Top = -24000, Width = 1100, Height = 640, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Content = _view,
        };
        host.Show();
        Program.Pump(800);
        _view.Load(FakeWorld.Hearthstone, Array.Empty<PackMod>(), shell);
        Program.Pump(1200);
        ResetView();
        Set("_ticks", (Func<long>)(() => _now));

        var settings = App.State.Settings;
        settings.PlanInkTouchDraws = false;
        settings.PlanInkHidden = false;
        settings.PlanInkTool = "pen";
        var surface = (PlanInkSurface)_view.FindName("InkSurface");
        var viewport = (FrameworkElement)_view.FindName("Viewport");
        Call("SetDrawMode", true);
        Call("SetInkTool", Enum.Parse(typeof(ModPlanView).GetNestedType("InkTool", BindingFlags.NonPublic)!, "Pen"));
        Program.Pump(200);

        // ── 1. "like something is pressed on the screen and it zooms": the hand holding the pen ──
        PenNear();
        FingerDown(201, 300, 420);
        FingerDown(202, 340, 430);
        FingerMove(202, 420, 470);
        FingerMove(201, 260, 400);
        Check("a palm landing while the pen hovers moves nothing: no zoom, no move", At(1, 0, 0) && !Pinching && Ink is null, View);
        Check("...its contacts are followed as the hand's, until they lift", Ignored(201) && Ignored(202));
        var before = Strokes;
        PenStroke(500, 300);
        Check("the pen draws with the palm down", Strokes == before + 1, $"{before} -> {Strokes}");
        FingerUp(201, 260, 400);
        FingerUp(202, 420, 470);
        Check("...and the palm lifting leaves nothing behind", Fingers.Count == 0 && Ink is null && !Pinching);

        // ── 2. a finger whose lift went somewhere else no longer pinches with the next one ──
        PenAway();
        ResetView();
        var down301 = true;
        FingerDown(301, 300, 300, () => down301);
        Check("with the pen away one finger moves the board", InkKind == "Pan/Touch", InkKind);
        FingerMove(301, 330, 300);
        down301 = false; // lifted over the palette: the lift went to the palette, WPF knows it's up
        _now += 50;
        FingerDown(302, 500, 300);
        FingerMove(302, 540, 300);
        Check("a finger that lifted elsewhere doesn't pinch with the next one: no zoom", !Pinching && Math.Abs(Zoom - 1) < 1e-9, View);
        Check("...the next finger moves the board from where it is", InkKind == "Pan/Touch" && At(1, 70, 0), $"{InkKind} {View}");
        FingerUp(302, 540, 300);
        Check("...and nothing of either is left", Fingers.Count == 0 && Ink is null);

        // ── 3. "it shows a movement icon and won't draw": a finger's move that never ended ──
        _now += 1000;
        ResetView();
        FingerDown(311, 300, 300); // WPF still says it's down: the worst case, a lift that never came at all
        FingerMove(311, 320, 310);
        Check("a finger moving the board leaves the pointer alone (no four-way arrow under the pen)",
            !ReferenceEquals(surface.Cursor, Cursors.SizeAll), surface.Cursor?.ToString() ?? "null");
        _now += 900;
        PenNear();
        Check("a pen arriving ends the finger's move, the finger is the hand's from then on", Ink is null && Ignored(311), InkKind);
        Check("...a move older than half a second stays where it got to", At(1, 20, 10), View);
        before = Strokes;
        PenStroke(500, 300);
        Check("...and the pen draws", Strokes == before + 1, $"{before} -> {Strokes}");
        FingerMove(311, 400, 400);
        Check("the finger moving on moves nothing", At(1, 20, 10), View);
        FingerUp(311, 400, 400);

        // ── 4. a palm landing just before the pen is taken back ──
        PenAway();
        ResetView();
        FingerDown(321, 300, 420);
        FingerDown(322, 330, 430);
        Check("two fingers pinch", Pinching);
        FingerMove(322, 390, 460);
        var palmZoom = Zoom;
        _now += 150;
        PenNear();
        Check("the palm's two contacts had zoomed the board", palmZoom > 1.5, $"zoom {palmZoom}");
        Check("...and the pen arriving a moment later puts it back", At(1, 0, 0) && !Pinching && Ink is null, View);
        FingerUp(321, 300, 420);
        FingerUp(322, 390, 460);

        // ── 5. the touchpad under the screen: its wheel and clicks while the pen is near ──
        ResetView();
        PenNear();
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = UIElement.MouseWheelEvent };
        Call("OnViewportWheel", viewport, wheel);
        Check("a wheel with the pen near doesn't zoom", At(1, 0, 0), View);
        var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseDownEvent };
        Call("OnInkMouseDown", surface, click);
        Check("a click with the pen near doesn't draw", Ink is null, InkKind);
        PenAway();
        Call("OnViewportWheel", viewport, new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = UIElement.MouseWheelEvent });
        Check("with the pen gone the wheel zooms again", Zoom > 1.05, View);
        Call("OnInkMouseDown", surface, new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseDownEvent });
        Check("...and the mouse draws again", InkKind == "Draw/Mouse", InkKind);
        Call("EndInkGesture", true);
        Call("BeginPan", Enum.Parse(typeof(ModPlanView).GetNestedType("InkDevice", BindingFlags.NonPublic)!, "Mouse"), 0, new Point(400, 300));
        Check("the mouse's middle-button move does show the four-way arrow", ReferenceEquals(surface.Cursor, Cursors.SizeAll),
            surface.Cursor?.ToString() ?? "null");
        Call("EndInkGesture", false);

        // ── 6. "it draws when I move with my fingers, then it disappears" (with a finger drawing) ──
        settings.PlanInkTouchDraws = true;
        ResetView();
        before = Strokes;
        FingerDown(331, 200, 500);
        Check("with 'Draw with a finger too' on, a finger draws", InkKind == "Draw/Touch", InkKind);
        for (var i = 1; i <= 10; i++)
        {
            _now += 40;
            FingerMove(331, 200 + i * 10, 500);
        }
        FingerDown(332, 700, 520);
        Check("a second finger landing on a line drawn for a while is a resting hand: the line goes on",
            InkKind == "Draw/Touch" && !Pinching && Ignored(332), InkKind);
        FingerMove(331, 320, 505);
        FingerUp(331, 330, 505);
        Check("...and the line is kept", Strokes == before + 1, $"{before} -> {Strokes}");
        FingerUp(332, 700, 520);
        FingerDown(333, 200, 560);
        _now += 60;
        FingerDown(334, 260, 560);
        Check("two fingers landing together pinch, and the line the first had only begun goes",
            Pinching && Ink is null && Strokes == before + 1, $"{InkKind} {Strokes}");
        FingerUp(333, 200, 560);
        FingerUp(334, 260, 560);
        FingerDown(335, 200, 600);
        FingerMove(335, 260, 600);
        _now += 100;
        PenNear();
        Check("a pen arriving while a finger draws takes that line back", Ink is null && Strokes == before + 1, $"{InkKind} {Strokes}");
        FingerUp(335, 260, 600);
        settings.PlanInkTouchDraws = false;

        // ── 7. the live line under the pen: only for a pen it knows ──
        var renderer = surface.Renderer;
        renderer.SetPenTablets([5]);
        renderer.Inking = true;
        Check("the live line starts for a pen it knows", renderer.StartsLine(5, new Point(500, 400)));
        Check("...never for another tablet, such as a touch screen", !renderer.StartsLine(6, new Point(500, 400)));
        renderer.SetKeepOut([new Rect(0, 0, 100, 100)]);
        Check("...nor on the palette", !renderer.StartsLine(5, new Point(50, 50)));
        renderer.SetKeepOut([]);
        PenGone();
        Check("...nor for a finger after the pen has left, the line still on for the pen's return",
            renderer.Inking && !renderer.StartsLine(6, new Point(500, 400)) && renderer.StartsLine(5, new Point(500, 400)));
        renderer.SetPenTablets([]);
        renderer.Inking = false;
        _now += 700;

        // ── 8. a pen line whose lift went missing ──
        PenNear();
        before = Strokes;
        PenDown(500, 300);
        PenMove(540, 300);
        PenMove(580, 310);
        Call("PenHover", 9, false, false, new Point(600, 320)); // in the air: it lifted, the lift went elsewhere
        Check("a line whose lift never reached the board ends once the pen is in the air", Ink is null && Strokes == before + 1,
            $"{InkKind} {before} -> {Strokes}");
        PenDown(500, 360);
        PenMove(560, 360);
        PenDown(500, 400); // pressed again with no lift in between
        Check("...or at its next press, which starts a line of its own", InkKind == "Draw/Pen" && Strokes == before + 2,
            $"{InkKind} {before} -> {Strokes}");
        PenUp(520, 400);
        Check("...which it then finishes", Ink is null && Strokes == before + 3, $"{before} -> {Strokes}");

        // ── 9. what the board was left holding with drawing off doesn't follow it into drawing ──
        PenAway();
        Call("SetDrawMode", false);
        var pinchType = typeof(ModPlanView).GetNestedType("Pinch", BindingFlags.NonPublic)!;
        Set("_pinch", Activator.CreateInstance(pinchType, 901, 902, new Point(0, 0), new Point(10, 0), 1.0, 0.0, 0.0));
        Call("ReapGestures");
        Check("with drawing off, a pinch whose fingers WPF no longer has is let go", !Pinching);
        Set("_pinch", Activator.CreateInstance(pinchType, 901, 902, new Point(0, 0), new Point(10, 0), 1.0, 0.0, 0.0));
        Set("_panning", true);
        Call("SetDrawMode", true);
        Check("drawing on lets go of a pinch and a move of the board left behind", !Pinching && !(bool)Get("_panning")!);
        PenNear();
        before = Strokes;
        PenStroke(500, 300);
        Check("...and the pen draws", Strokes == before + 1, $"{before} -> {Strokes}");

        // ── 10. drawing off, through WPF's own touch routing ──
        PenAway();
        Call("SetDrawMode", false);
        Program.Pump(100);
        ResetView();
        var origin = viewport.TransformToAncestor(host).Transform(new Point(0, 0));
        Point On(double x, double y) => new(origin.X + x, origin.Y + y);
        var t1 = new FakeTouch(701, host);
        var t2 = new FakeTouch(702, host);
        t1.Down(On(400, 420));
        t2.Down(On(430, 430));
        Check("with drawing off, two fingers on the empty board pinch", Pinching);
        t2.Move(On(500, 470));
        var offZoom = Zoom;
        _now += 150;
        PenNear();
        Check("...a pen arriving a moment later puts the board back", offZoom > 1.5 && At(1, 0, 0) && !Pinching, $"from {offZoom}: {View}");
        t2.Move(On(560, 500));
        Check("...and those fingers move nothing after", At(1, 0, 0), View);
        t1.Up();
        t2.Up();
        PenAway();
        var t3 = new FakeTouch(703, host);
        var t4 = new FakeTouch(704, host);
        t3.Down(On(400, 420));
        t3.Vanish(); // its lift never reaches the board
        t4.Down(On(460, 430));
        t4.Move(On(560, 470));
        Check("a touch WPF has dropped doesn't pinch with the next one", !Pinching && Math.Abs(Zoom - 1) < 1e-9, View);
        t4.Up();
        Check("...and none is left", ((IDictionary)Get("_viewTouches")!).Count == 0);

        Program.Log($"pen-touch checks done, {_fails} failed");
        host.Close();
        shell.Hide();
        Program.Pump(300);
    }
}
