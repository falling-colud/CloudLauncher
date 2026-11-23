using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

// Drawing on a board: a pen, a highlighter, an eraser and a lasso, made for a pen with an eraser end
// (a Surface pen) and usable with the mouse and a finger.
//
// - Draw (toolbar, or D) turns drawing on. The pen, the mouse and (if asked) a finger then draw with the
//   palette's tool; a finger otherwise moves the board, two fingers zoom it, and the wheel zooms as ever.
// - The pen comes first: while it is near the screen, fingers, the palm and the touchpad do nothing on
//   the board (see "who has the board" below).
// - The back of the pen erases at any time, drawing or not, and so does the pen's side button held down
//   (it selects instead while drawing, like a lasso).
// - What is drawn is stored on the board (PlanBoard.Strokes) in board units, so it pans, zooms, syncs and
//   undoes with the cards. Lines sit on top of the cards; highlighter under the pen's lines.
// - Widths are picked on screen: a line drawn zoomed out is as thick on screen as one drawn at 100%.
//
// The line under the pen is drawn live by PlanInkSurface's renderer; once lifted it becomes a stroke in
// the board's cached ink layer (a TiledCachedLayer, like the links), and only the tiles it crosses redraw.

public partial class ModPlanView
{
    private enum InkTool { Pen, Highlighter, Eraser, Select }

    private enum InkDevice { Pen, Mouse, Touch }

    private enum InkGestureKind { Draw, Erase, Lasso, Move, Pan }

    /// <summary>Line widths on screen for fine / medium / bold.</summary>
    private static readonly double[] PenWidths = [2, 4, 8];
    private static readonly double[] HighlighterWidths = [12, 20, 32];
    private static readonly double[] EraserWidths = [12, 24, 48];

    /// <summary>The ink colours: the look's text colour first (null), then the card palette.</summary>
    private static readonly (string Name, string? Hex)[] InkColors =
    [
        ("Ink", null),
        ("Red", "#D36A6A"),
        ("Orange", "#E87D3E"),
        ("Amber", "#E3B341"),
        ("Green", "#3FB950"),
        ("Teal", "#3EC8C1"),
        ("Blue", "#5B9DF9"),
        ("Purple", "#CFAEFF")
    ];

    // ── state ───────────────────────────────────────────────────────────────────

    private bool _drawMode;
    private InkTool _inkTool = InkTool.Pen;

    /// <summary>The back of the pen is hovering over the board while drawing is off: the surface takes
    /// presses so it can erase.</summary>
    private bool _eraserHover;

    /// <summary>The finished strokes, cached as bitmaps tile by tile (see <see cref="TiledCachedLayer"/>).</summary>
    private readonly TiledCachedLayer _inkLayer = new();

    /// <summary>Selected strokes, drawn outlined above everything while they are selected (they leave
    /// the cached layer meanwhile, so dragging them only moves this).</summary>
    private readonly InkSelectionVisual _inkSelectionVisual = new();

    private readonly Path _inkLasso = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Ellipse _eraserRing = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };

    /// <summary>Where the pen or the mouse will draw, while it hovers: a dot of the line's own colour and
    /// width. It stands in for a pointer (there is none while drawing), and goes the moment a line starts.</summary>
    private readonly Ellipse _penDot = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };

    /// <summary>Each stroke's WPF ink and outline, by stroke id. Strokes are immutable, so an entry stays
    /// good until its stroke is replaced.</summary>
    private readonly Dictionary<string, InkEntry> _inkCache = new(StringComparer.Ordinal);

    /// <summary>What the cached layer draws now, by id, so a change redraws only the tiles it touches.</summary>
    private Dictionary<string, PlanStroke> _inkShown = new(StringComparer.Ordinal);
    private string? _inkShownBoard;
    private Color _inkShownText;

    private readonly HashSet<string> _inkSelected = new(StringComparer.Ordinal);

    private InkGesture? _ink;

    /// <summary>Fingers the board follows itself (all of them while drawing, and palms while not), by
    /// stylus device id.</summary>
    private readonly Dictionary<int, Finger> _fingers = new();
    private Pinch? _pinch;

    /// <summary>The board as it was when fingers began moving it, and when, so a move a pen interrupts
    /// right away (a palm that landed first) can be taken back.</summary>
    private (long At, double Zoom, double PanX, double PanY)? _fingersBegan;

    /// <summary>When a pen was last seen anywhere on the page, and the last pen (for WPF's own word on
    /// whether it is still in range, while it hovers without moving).</summary>
    private long _penSeenAt = long.MinValue / 2;
    private StylusDevice? _pen;

    /// <summary>Milliseconds, swapped by the tests.</summary>
    private Func<long> _ticks = () => Environment.TickCount64;

    /// <summary>Strokes copied with Ctrl+C, and whether they were copied after the last cards were (then
    /// Ctrl+V pastes them rather than the cards).</summary>
    private static List<PlanStroke>? _inkClipboard;
    private static bool _inkClipboardNewer;

    private bool _inkRefreshQueued;
    private bool _penLookStale = true;

    private sealed class InkEntry
    {
        public required PlanStroke Model;
        public required Stroke Ink;
        public required Geometry Shape;
        public Rect Bounds;
    }

    private sealed class InkGesture
    {
        public InkGestureKind Kind;
        public InkDevice Device;
        public int DeviceId;
        public bool Highlighter;
        public string? Color;
        public double WidthPx;
        /// <summary>Draw: the points so far, on screen (this surface's coordinates).</summary>
        public StylusPointCollection? Points;
        /// <summary>Draw with the mouse or a finger (or a pen the renderer missed): the line shown meanwhile.</summary>
        public Stroke? Wet;
        public Point StartScreen;
        public Point LastBoard;
        public List<Point>? Lasso;
        public Vector Moved;
        public bool Changed;
        public double PanX, PanY;
        /// <summary>When it began (<see cref="_ticks"/>).</summary>
        public long Began;
    }

    private sealed record Pinch(int A, int B, Point A0, Point B0, double Zoom, double PanX, double PanY);

    /// <summary>A finger on the board.</summary>
    private sealed class Finger
    {
        public required int Id;
        public Point At;
        /// <summary>Taken for the hand that holds the pen (or a finger too many): followed until it lifts,
        /// so it can't start anything, and used for nothing.</summary>
        public bool Ignored;
        /// <summary>WPF's own record that the contact is still on the glass, for a finger whose lift went
        /// somewhere this page never saw.</summary>
        public Func<bool> Down = () => true;
    }

    // ── settings ────────────────────────────────────────────────────────────────

    private static AppSettings InkSettings => App.State.Settings;

    private static bool InkHidden => InkSettings.PlanInkHidden;

    private static int SizeIndex(int stored) => Math.Clamp(stored, 0, 2);

    private double PenWidthPx => PenWidths[SizeIndex(InkSettings.PlanInkPenSize)];
    private double HighlighterWidthPx => HighlighterWidths[SizeIndex(InkSettings.PlanInkHighlighterSize)];
    private double EraserWidthPx => EraserWidths[SizeIndex(InkSettings.PlanInkEraserSize)];

    private static InkTool ParseTool(string? s) => s?.ToLowerInvariant() switch
    {
        "highlighter" => InkTool.Highlighter,
        "eraser" => InkTool.Eraser,
        "select" => InkTool.Select,
        _ => InkTool.Pen
    };

    private static string ToolName(InkTool t) => t switch
    {
        InkTool.Highlighter => "highlighter",
        InkTool.Eraser => "eraser",
        InkTool.Select => "select",
        _ => "pen"
    };

    private static void SaveInkSettings()
    {
        try { InkSettings.Save(); } catch { /* a preference; the next save carries it */ }
    }

    private double Zoom => Math.Max(0.05, ZoomT.ScaleX);

    /// <summary>A point on the drawing surface (the viewport's screen space) on the board.</summary>
    private Point ToBoard(Point screen) => new((screen.X - PanT.X) / Zoom, (screen.Y - PanT.Y) / Zoom);

    // ── setup ───────────────────────────────────────────────────────────────────

    private void InitInk()
    {
        // The ink host shares the canvas's transform object, so drawings pan and zoom with the cards
        // while living outside the canvas, which every rebuild clears.
        InkHost.RenderTransform = BoardCanvas.RenderTransform;
        InkHost.Children.Add(_inkLayer);
        InkHost.Children.Add(_inkSelectionVisual);
        InkHost.Children.Add(_inkLasso);
        InkHost.Children.Add(_eraserRing);
        InkHost.Children.Add(_penDot);

        InkSurface.StylusDown += OnInkStylusDown;
        InkSurface.StylusMove += OnInkStylusMove;
        InkSurface.StylusUp += OnInkStylusUp;
        InkSurface.StylusInAirMove += OnInkStylusHover;
        InkSurface.StylusInRange += OnInkStylusHover;
        InkSurface.StylusOutOfRange += OnInkStylusOutOfRange;
        InkSurface.StylusSystemGesture += OnInkSystemGesture;
        InkSurface.LostStylusCapture += OnInkLostStylusCapture;
        InkSurface.MouseDown += OnInkMouseDown;
        InkSurface.MouseMove += OnInkMouseMove;
        InkSurface.MouseUp += OnInkMouseUp;
        InkSurface.LostMouseCapture += OnInkLostMouseCapture;
        InkSurface.MouseLeave += (_, _) =>
        {
            HidePenDot();
            if (_ink is null) HideEraserRing();
        };

        // The back of the pen erases even while drawing is off: its hover anywhere over the board hands
        // the next press to the drawing surface.
        Viewport.PreviewStylusInRange += OnViewportStylusHover;
        Viewport.PreviewStylusInAirMove += OnViewportStylusHover;
        Viewport.PreviewStylusOutOfRange += OnViewportStylusOutOfRange;
        // A quick pen stroke on the board is a stroke, never a Windows flick (a back or scroll command
        // and its icon). The drawing surface turns off the rest while drawing (PlanInkSurface).
        Stylus.SetIsFlicksEnabled(Viewport, false);

        // Two fingers zoom the board while drawing is off too (one still pans it, through the mouse).
        Viewport.PreviewTouchDown += OnViewportTouchDown;
        Viewport.PreviewTouchMove += OnViewportTouchMove;
        Viewport.PreviewTouchUp += OnViewportTouchUp;
        Viewport.LostTouchCapture += OnViewportLostTouch;

        // Where the pen is, and the fingers the board follows, seen from the whole page whoever handles
        // them: a finger that slides off the board onto the palette still lifts here.
        AddHandler(Stylus.PreviewStylusInRangeEvent, new StylusEventHandler(OnPagePenSeen), true);
        AddHandler(Stylus.PreviewStylusInAirMoveEvent, new StylusEventHandler(OnPagePenSeen), true);
        AddHandler(Stylus.PreviewStylusDownEvent, new StylusDownEventHandler(OnPageStylusDown), true);
        AddHandler(Stylus.PreviewStylusMoveEvent, new StylusEventHandler(OnPageStylusMove), true);
        AddHandler(Stylus.PreviewStylusUpEvent, new StylusEventHandler(OnPageStylusUp), true);
        AddHandler(Stylus.PreviewStylusOutOfRangeEvent, new StylusEventHandler(OnPageStylusOutOfRange), true);

        InkPalette.SizeChanged += (_, _) => UpdatePaletteKeepOut();
        InkSurface.SizeChanged += (_, _) => UpdatePaletteKeepOut();
        // The palette is ordinary UI over the board: its buttons click, and a press on its background
        // must not reach the board behind it (a pan, the board's menu).
        InkPalette.MouseDown += (_, e) => e.Handled = true;
        InkPalette.MouseUp += (_, e) => e.Handled = true;

        _inkTool = ParseTool(InkSettings.PlanInkTool);
        _inkLasso.StrokeDashArray = new DoubleCollection { 4, 3 };
        Panel.SetZIndex(_inkSelectionVisual, 1);
        Panel.SetZIndex(_inkLasso, 2);
        Panel.SetZIndex(_eraserRing, 3);
        Panel.SetZIndex(_penDot, 3);
        BuildInkPalette();
        ApplyInkVisibility();

        Loaded += (_, _) => RefreshPenTablets();
        // Woken from sleep, the pen comes back under a new id: see ModPlanView.InkLog.cs.
        WatchWake();
        Loaded += (_, _) => { Woke -= OnWoke; Woke += OnWoke; };
        Unloaded += (_, _) => Woke -= OnWoke;
    }

    /// <summary>Tells the live renderer which tablets are pens. Asked again whenever a pen the list
    /// lacks shows up (Windows can add a tablet, or bring one back under a new id, at any time).</summary>
    private void RefreshPenTablets()
    {
        try
        {
            var ids = new List<int>();
            foreach (TabletDevice t in Tablet.TabletDevices)
                if (t.Type == TabletDeviceType.Stylus) ids.Add(t.Id);
            InkSurface.Renderer.SetPenTablets(ids.ToArray());
        }
        catch { InkSurface.Renderer.SetPenTablets([]); /* no tablet stack: the board draws every line itself */ }
        NotePenTablets();
    }

    // ── draw mode ───────────────────────────────────────────────────────────────

    private void OnToggleDraw(object sender, RoutedEventArgs e) => SetDrawMode(!_drawMode);

    /// <summary>Turns drawing on or off. While it is on, presses on the board draw (or erase or select)
    /// instead of moving cards.</summary>
    private void SetDrawMode(bool on)
    {
        if (on == _drawMode) return;
        EndInkGesture(cancel: true);
        EndBoardMoves();
        if (on)
        {
            if (ArrowModeActive) SetArrowTool(false);
            ClearSelection();
            if (InkHidden)
            {
                InkSettings.PlanInkHidden = false; // drawing on hidden lines would be drawing blind
                SaveInkSettings();
            }
            RefreshPenTablets();
        }
        else
        {
            ClearInkSelection();
            HidePenDot();
            LogInkSession();
        }

        _drawMode = on;
        DrawToolButton.Style = (Style)FindResource(on ? "AccentButton" : "SubtleButton");
        DrawToolButton.Margin = new Thickness(on ? 0 : 6, 0, 0, 0);
        CardTools.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        InkPalette.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Visibility = !on && _board is { IsEmpty: true } ? Visibility.Visible : Visibility.Collapsed;
        ApplyInkVisibility();
        UpdateInkSurface();
        UpdateInkPalette();
        PlanStatus.ToolTip = null;
        PlanStatus.Text = on ? DrawPrompt : "";
        Viewport.Focus(); // off the button, so Space can't re-press it and Esc reaches the board
        Dispatcher.BeginInvoke(UpdatePaletteKeepOut, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private string DrawPrompt => _inkTool switch
    {
        _ when _inkSelected.Count > 0 =>
            $"{_inkSelected.Count} line{(_inkSelected.Count == 1 ? "" : "s")} selected  ·  drag to move  ·  Del removes",
        InkTool.Eraser => "Erasing  ·  Esc to stop",
        InkTool.Select => "Circle lines or tap one  ·  Esc to stop",
        _ => "Drawing  ·  the back of the pen erases  ·  Esc to stop"
    };

    /// <summary>Whether the surface takes presses now, and what the pen's live line would do.</summary>
    private void UpdateInkSurface()
    {
        var active = _drawMode || _ink is not null || (_eraserHover && !InkHidden);
        InkSurface.IsHitTestVisible = active;
        if (!active) InkSurface.Renderer.Inking = false;
        UpdateInkCursor(null);
    }

    private void UpdateInkCursor(Point? at)
    {
        Cursor cursor;
        if (_ink is { Kind: InkGestureKind.Move }) cursor = Cursors.SizeAll;
        // The four-way arrow for the mouse's middle-button move only: a finger has no pointer, and one
        // shown for it would sit under the pen.
        else if (_ink is { Kind: InkGestureKind.Pan, Device: InkDevice.Mouse }) cursor = Cursors.SizeAll;
        else if (at is { } p && InSelectionBox(ToBoard(p))) cursor = Cursors.SizeAll;
        else cursor = _inkTool switch
        {
            InkTool.Select => Cursors.Cross,
            // No pointer over the line being drawn: the pen's dot (or the eraser's ring) shows where it lands.
            _ => Cursors.None
        };
        if (!_drawMode) cursor = Cursors.None; // only the back of the pen gets here: the ring
        InkSurface.Cursor = cursor;
    }

    /// <summary>The look the pen's live line takes for the current tool.</summary>
    private DrawingAttributes LiveLook(bool highlighter, string? color, double widthPx) => new()
    {
        Color = InkColor(color, highlighter: false),
        Width = widthPx,
        Height = widthPx,
        FitToCurve = false,
        IgnorePressure = highlighter,
        IsHighlighter = highlighter,
        StylusTip = StylusTip.Ellipse
    };

    private (bool Highlighter, string? Color, double WidthPx) ToolLook() => _inkTool == InkTool.Highlighter
        ? (true, InkSettings.PlanInkHighlighterColor, HighlighterWidthPx)
        : (false, InkSettings.PlanInkPenColor, PenWidthPx);

    /// <summary>Hands the pen's live line the current tool's look, between strokes only.</summary>
    private void ApplyPenLook()
    {
        if (_ink is { Device: InkDevice.Pen }) { _penLookStale = true; return; }
        var (highlighter, color, width) = ToolLook();
        InkSurface.SetPenLook(LiveLook(highlighter, color, width));
        _penLookStale = false;
    }

    /// <summary>Whether a pen tip pressed where it hovers now starts a line, for the live renderer.</summary>
    private void UpdatePenInking(bool inverted, bool barrel, Point at)
    {
        if (_penLookStale) ApplyPenLook();
        InkSurface.Renderer.Inking = _drawMode && _ink is null
            && _inkTool is InkTool.Pen or InkTool.Highlighter
            && !inverted && !barrel
            && !InSelectionBox(ToBoard(at));
    }

    private static bool BarrelDown(StylusDevice device)
    {
        try
        {
            foreach (var button in device.StylusButtons)
                if (button.Guid == StylusPointProperties.BarrelButton.Id && button.StylusButtonState == StylusButtonState.Down)
                    return true;
        }
        catch { /* a pen that reports no buttons */ }
        return false;
    }

    private static InkDevice DeviceOf(StylusDevice device) =>
        device.TabletDevice?.Type == TabletDeviceType.Touch ? InkDevice.Touch : InkDevice.Pen;

    // ── who has the board ───────────────────────────────────────────────────────
    // The pen comes first. While one is near the screen (in range, or out of it for less than
    // PalmGraceMs), whatever else touches the board is taken for the hand that holds it: a finger that
    // lands then is ignored until it lifts, and so are the mouse's buttons and wheel over the board (a
    // palm on the touchpad). A pen arriving ends what fingers were doing with the board, and takes back
    // a move they only just began (a palm that landed a moment before the pen).
    //
    // The fingers the board follows are seen from the whole page, not the drawing surface only, and are
    // checked against WPF's own record of each contact: a finger whose lift never came here would
    // otherwise hold the board in a move the pen can't get into, or pinch with the next finger (the board
    // zooming as if something pressed the screen). They are never captured: in WPF every pen and finger
    // shares the mouse's capture, so a finger letting go of it would take it from a pen drawing then.

    private const int PalmGraceMs = 600;
    private const int TakeBackMs = 500;
    /// <summary>How long WPF's word that the pen is still in range counts with no news from the pen.</summary>
    private const int PenStillMs = 10_000;
    /// <summary>A finger's line this young (or this short) when a second finger lands goes, for a pinch;
    /// an older one stays a line, and the second finger is taken for a resting hand.</summary>
    private const int YoungLineMs = 300;
    private const double YoungLinePx = 24;

    /// <summary>A pen is near the screen, or was a moment ago.</summary>
    private bool PenNear
    {
        get
        {
            var since = _ticks() - _penSeenAt;
            if (since < PalmGraceMs) return true;
            if (since >= PenStillMs) return false;
            try { return _pen is { InRange: true }; }
            catch { return false; }
        }
    }

    /// <summary>A pen showed up somewhere on the page: the fingers give the board up to it.</summary>
    private void PenSeen(StylusDevice? device)
    {
        _penSeenAt = _ticks();
        if (device is not null) _pen = device;
        YieldToPen();
    }

    /// <summary>The pen left: fingers count again once <see cref="PalmGraceMs"/> have gone by. (The live
    /// renderer keeps its word for the pen's return: a quick stroke right as it comes back draws at once,
    /// and no finger draws through it whatever it says.)</summary>
    private void PenLeft()
    {
        _penSeenAt = _ticks();
        HidePenDot();
        if (_ink is null) HideEraserRing();
    }

    /// <summary>Ends what fingers are doing with the board, for the pen. Every finger still down is from
    /// then on taken for the hand holding it.</summary>
    private void YieldToPen()
    {
        var busy = _ink is { Device: InkDevice.Touch } || _pinch is not null || _viewTouches.Count > 0
                   || (_panning && _panByFinger);
        foreach (var f in _fingers.Values)
        {
            if (f.Ignored) continue;
            f.Ignored = true;
            busy = true;
        }
        if (!busy) return;

        var began = _fingersBegan;
        _fingersBegan = null;
        _viewTouches.Clear();
        _pinch = null;
        if (_ink is { Device: InkDevice.Touch }) EndInkGesture(cancel: true);
        if (_panning && _panByFinger)
        {
            // Drawing off, a finger is the mouse and was moving the board.
            _panning = false;
            if (Viewport.IsMouseCaptured) Viewport.ReleaseMouseCapture();
            Viewport.Cursor = Cursors.Arrow;
        }
        if (began is { } b && _ticks() - b.At < TakeBackMs)
        {
            // A palm that landed just before the pen: the board goes back to where it was.
            ZoomT.ScaleX = ZoomT.ScaleY = b.Zoom;
            PanT.X = b.PanX;
            PanT.Y = b.PanY;
            SyncGrid();
            RefreshInkZoomDependent();
        }
        StashViewport();
    }

    /// <summary>Ends every move of the board in progress, as drawing goes on or off; fingers still down
    /// are left alone until they lift.</summary>
    private void EndBoardMoves()
    {
        foreach (var f in _fingers.Values) f.Ignored = true;
        _fingersBegan = null;
        _viewTouches.Clear();
        if (_pinch is not null)
        {
            _pinch = null;
            StashViewport();
        }
        if (_marquee) EndMarquee();
        if (_panning)
        {
            _panning = false;
            if (Viewport.IsMouseCaptured) Viewport.ReleaseMouseCapture();
            Viewport.Cursor = Cursors.Arrow;
            StashViewport();
        }
    }

    /// <summary>Before anything new starts on the board: what fingers left behind is cleared away.</summary>
    private void ReapGestures()
    {
        ReapFingers();
        ReapViewTouches();
    }

    private static Func<bool> StillDown(StylusDevice device) => () =>
    {
        try { return !device.InAir && device.InRange; }
        catch { return false; }
    };

    /// <summary>Whether a point of the board's viewport is on the board itself (not on the palette).</summary>
    private bool OnBoard(Point inViewport)
    {
        if (inViewport.X < 0 || inViewport.Y < 0 || inViewport.X > Viewport.ActualWidth || inViewport.Y > Viewport.ActualHeight)
            return false;
        if (!InkPalette.IsVisible) return true;
        try
        {
            var palette = InkPalette.TransformToVisual(Viewport).TransformBounds(new Rect(InkPalette.RenderSize));
            return !palette.Contains(inViewport);
        }
        catch (InvalidOperationException) { return true; }
    }

    // ── the page's view of pens and fingers ─────────────────────────────────────
    // Preview handlers on the whole view that see every event, handled or not. They only watch the pen;
    // fingers the board follows they take wherever they go, so none becomes a touch or the mouse.

    private void OnPagePenSeen(object sender, StylusEventArgs e)
    {
        if (DeviceOf(e.StylusDevice) == InkDevice.Pen) PenSeen(e.StylusDevice);
    }

    private void OnPageStylusDown(object sender, StylusDownEventArgs e)
    {
        var device = e.StylusDevice;
        if (DeviceOf(device) == InkDevice.Pen)
        {
            PenSeen(device);
            return;
        }
        // While drawing, a finger on the board is the drawing surface's (OnInkStylusDown). With drawing
        // off, one landing while the pen is near is a palm, kept from becoming a touch or the mouse.
        if (_drawMode || e.Handled || !PenNear || !OnBoard(e.GetPosition(Viewport))) return;
        ReapFingers();
        _fingers[device.Id] = new Finger { Id = device.Id, At = e.GetPosition(InkSurface), Ignored = true, Down = StillDown(device) };
        e.Handled = true;
    }

    private void OnPageStylusMove(object sender, StylusEventArgs e)
    {
        var device = e.StylusDevice;
        if (DeviceOf(device) == InkDevice.Pen)
        {
            PenSeen(device);
            return;
        }
        if (!_fingers.ContainsKey(device.Id)) return;
        e.Handled = true; // the board's, wherever it is now
        FingerMove(device.Id, e.GetPosition(InkSurface), e.GetStylusPoints(InkSurface));
    }

    private void OnPageStylusUp(object sender, StylusEventArgs e)
    {
        var device = e.StylusDevice;
        if (DeviceOf(device) == InkDevice.Pen)
        {
            PenSeen(device);
            return;
        }
        if (!_fingers.ContainsKey(device.Id)) return;
        e.Handled = true;
        FingerUp(device.Id, e.GetStylusPoints(InkSurface));
    }

    private void OnPageStylusOutOfRange(object sender, StylusEventArgs e)
    {
        var device = e.StylusDevice;
        if (DeviceOf(device) == InkDevice.Pen) PenLeft();
        else FingerUp(device.Id, null);
    }

    // ── pen input ───────────────────────────────────────────────────────────────
    // Every press, move and lift on the surface is marked handled: WPF then makes no mouse event of it,
    // so a stroke can never also click, drag or pan what is under it. (Fingers are followed from the
    // page, above; only their press starts here.)

    private void OnInkStylusDown(object sender, StylusDownEventArgs e)
    {
        e.Handled = true;
        var device = e.StylusDevice;
        if (DeviceOf(device) == InkDevice.Pen) NoteInput(e.Timestamp);
        var at = e.GetPosition(InkSurface);
        if (DeviceOf(device) == InkDevice.Touch)
        {
            FingerDown(device.Id, at, e.GetStylusPoints(InkSurface), StillDown(device));
            return;
        }
        PenDown(device.Id, device.Inverted, BarrelDown(device), at, e.GetStylusPoints(InkSurface),
            InkSurface.Renderer.Drawing(device.Id));
        if (_ink is { Device: InkDevice.Pen } g && g.DeviceId == device.Id) device.Capture(InkSurface);
    }

    /// <summary>The pen touched the board: it draws (or erases, or selects), whatever else was going on.</summary>
    private void PenDown(int id, bool inverted, bool barrel, Point at, StylusPointCollection points, bool rendererDrew)
    {
        HidePenDot();
        PenSeen(null); // the fingers' move ends here, if the page didn't end it already
        ReapGestures();
        // Whatever is still under way gives way: the mouse's line, or one of this pen's whose lift never
        // reached the board.
        if (_ink is not null) EndInkGesture(cancel: false);

        var renderer = InkSurface.Renderer;
        InkTool tool;
        if (inverted) tool = InkTool.Eraser;              // the back of the pen
        else if (!_drawMode)
        {
            // Only the back of the pen gets here with drawing off; the tip after a flip just stops it.
            if (rendererDrew) renderer.Abort();
            _eraserHover = false;
            UpdateInkSurface();
            return;
        }
        else if (barrel) tool = _inkTool == InkTool.Eraser ? InkTool.Eraser : InkTool.Select;
        else tool = _inkTool;

        BeginInkGesture(tool, InkDevice.Pen, id, at, points);
        if (_ink is { Kind: InkGestureKind.Draw } g)
        {
            NoteLineStart(rendererDrew);
            // The renderer decided from the hover; if it missed this one, draw it here instead.
            if (!rendererDrew) ShowWetLine(g);
        }
        else if (rendererDrew) renderer.Abort();
    }

    private void OnInkStylusMove(object sender, StylusEventArgs e)
    {
        e.Handled = true;
        var device = e.StylusDevice;
        if (DeviceOf(device) != InkDevice.Pen) return;
        NoteInput(e.Timestamp);
        PenMove(device.Id, e.GetStylusPoints(InkSurface));
    }

    private void OnInkStylusUp(object sender, StylusEventArgs e)
    {
        e.Handled = true;
        var device = e.StylusDevice;
        if (DeviceOf(device) == InkDevice.Touch) return;
        PenUp(device.Id, e.GetStylusPoints(InkSurface));
        // Also after a stroke Esc cancelled midway: the pen kept the surface captured until now.
        if (ReferenceEquals(device.Captured, InkSurface)) device.Capture(null);
    }

    private void PenMove(int id, StylusPointCollection points)
    {
        if (_ink is { Device: InkDevice.Pen } g && g.DeviceId == id) InkGestureMove(points);
    }

    private void PenUp(int id, StylusPointCollection points)
    {
        if (_ink is not { Device: InkDevice.Pen } g || g.DeviceId != id) return;
        InkGestureMove(points);
        EndInkGesture(cancel: false);
    }

    private void OnInkStylusHover(object sender, StylusEventArgs e)
    {
        var device = e.StylusDevice;
        if (DeviceOf(device) == InkDevice.Touch)
        {
            e.Handled = true;
            return;
        }
        // While drawing, the hover goes on to WPF's mouse, which so follows the pen and takes the pointer
        // from the surface under it (none, for the pen and the highlighter). Marked handled, the mouse
        // stayed wherever it last was, and the pen showed that element's pointer instead: an arrow, a
        // hand, a card's four-way arrow. With drawing off only the back of the pen gets here, and its
        // hover must not light up the cards underneath.
        if (!_drawMode) e.Handled = true;
        if (device.TabletDevice is { } tablet && !InkSurface.Renderer.IsPenTablet(tablet.Id)) RefreshPenTablets();
        PenHover(device.Id, device.Inverted, BarrelDown(device), e.GetPosition(InkSurface));
    }

    /// <summary>The pen in the air over the board: its dot or the eraser's ring, and what a press there
    /// would do.</summary>
    private void PenHover(int id, bool inverted, bool barrel, Point at)
    {
        // In the air, the pen has lifted: a line whose lift never reached the board ends here.
        if (_ink is { Device: InkDevice.Pen } g && g.DeviceId == id) EndInkGesture(cancel: false);
        UpdatePenInking(inverted, barrel, at);
        if (_ink is null)
        {
            if (inverted || (_drawMode && _inkTool == InkTool.Eraser)) ShowEraserRing(at);
            else HideEraserRing();
            if (inverted) HidePenDot();
            else ShowPenDot(at);
            UpdateInkCursor(at);
        }
    }

    private void OnInkStylusOutOfRange(object sender, StylusEventArgs e)
    {
        if (!_drawMode) e.Handled = true; // (the page saw it go: OnPageStylusOutOfRange)
    }

    /// <summary>Taps, holds and flicks are handled here so none of them turns into a mouse click on the
    /// board; a tap with the pen's side button (its right click) opens the drawing menu.</summary>
    private void OnInkSystemGesture(object sender, StylusSystemGestureEventArgs e)
    {
        e.Handled = true;
        if (e.SystemGesture == SystemGesture.RightTap && _drawMode && _ink is null)
            ShowInkMenu(e.GetPosition(InkSurface));
    }

    private void OnInkLostStylusCapture(object sender, StylusEventArgs e)
    {
        if (_ink is { Device: InkDevice.Pen } g && g.DeviceId == e.StylusDevice.Id) EndInkGesture(cancel: false);
    }

    // ── the back of the pen, drawing off ────────────────────────────────────────

    private void OnViewportStylusHover(object sender, StylusEventArgs e)
    {
        var device = e.StylusDevice;
        if (DeviceOf(device) == InkDevice.Touch || _drawMode || _ink is not null) return;
        var hover = device.Inverted && !InkHidden && _board is { HasDrawing: true };
        if (hover == _eraserHover) return;
        _eraserHover = hover;
        if (!hover) HideEraserRing();
        UpdateInkSurface();
    }

    private void OnViewportStylusOutOfRange(object sender, StylusEventArgs e)
    {
        if (!_eraserHover || _ink is not null) return;
        _eraserHover = false;
        HideEraserRing();
        UpdateInkSurface();
    }

    // ── mouse input ─────────────────────────────────────────────────────────────
    // With the pen near the screen the mouse's buttons do nothing on the board: that is a palm on the
    // touchpad (a Surface's sits right under the screen in its studio pose).

    private void OnInkMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (e.StylusDevice is not null) return; // a pen or finger: already taken as stylus input
        if (PenNear) return;
        ReapGestures();
        HidePenDot();
        var at = e.GetPosition(InkSurface);
        if (e.ChangedButton == MouseButton.Middle && _ink is null)
        {
            BeginPan(InkDevice.Mouse, 0, at);
            InkSurface.CaptureMouse();
            return;
        }
        if (e.ChangedButton != MouseButton.Left || !_drawMode || _ink is not null) return;
        Viewport.Focus();
        BeginInkGesture(_inkTool, InkDevice.Mouse, 0, at, MousePoints(at));
        if (_ink is { Kind: InkGestureKind.Draw } g) ShowWetLine(g);
        if (_ink is not null) InkSurface.CaptureMouse();
    }

    private void OnInkMouseMove(object sender, MouseEventArgs e)
    {
        if (e.StylusDevice is not null) return;
        var at = e.GetPosition(InkSurface);
        if (_ink is { Device: InkDevice.Mouse })
        {
            if (_ink.Kind == InkGestureKind.Pan) PanTo(at);
            else InkGestureMove(MousePoints(at));
            return;
        }
        if (_ink is not null || PenNear) return;
        if (_drawMode && _inkTool == InkTool.Eraser) ShowEraserRing(at);
        ShowPenDot(at);
        UpdateInkCursor(at);
    }

    private void OnInkMouseUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (e.StylusDevice is not null) return;
        var at = e.GetPosition(InkSurface);
        if (_ink is { Device: InkDevice.Mouse } g)
        {
            if (g.Kind == InkGestureKind.Pan ? e.ChangedButton == MouseButton.Middle : e.ChangedButton == MouseButton.Left)
            {
                if (g.Kind != InkGestureKind.Pan) InkGestureMove(MousePoints(at));
                EndInkGesture(cancel: false);
                if (InkSurface.IsMouseCaptured) InkSurface.ReleaseMouseCapture();
            }
            return;
        }
        if (e.ChangedButton == MouseButton.Right && _drawMode && !PenNear) ShowInkMenu(at);
    }

    private void OnInkLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_ink is { Device: InkDevice.Mouse }) EndInkGesture(cancel: false);
    }

    private static StylusPointCollection MousePoints(Point at) => new([new StylusPoint(at.X, at.Y, 0.5f)]);

    // ── fingers, drawing ────────────────────────────────────────────────────────
    // While drawing, one finger moves the board (or draws, if asked) and two zoom it.

    private void FingerDown(int id, Point at, StylusPointCollection points, Func<bool> down)
    {
        ReapGestures();
        var finger = new Finger { Id = id, At = at, Down = down };
        _fingers[id] = finger; // an id Windows hands out again is a new finger
        if (PenNear || _ink is { Device: InkDevice.Pen or InkDevice.Mouse })
        {
            finger.Ignored = true; // the hand holding the pen
            return;
        }

        var active = _fingers.Values.Where(f => !f.Ignored).ToList();
        if (active.Count == 1)
        {
            _fingersBegan ??= (_ticks(), Zoom, PanT.X, PanT.Y);
            if (_drawMode && InkSettings.PlanInkTouchDraws)
            {
                BeginInkGesture(_inkTool, InkDevice.Touch, id, at, TouchPoints(points));
                if (_ink is { Kind: InkGestureKind.Draw } g) ShowWetLine(g);
            }
            else BeginPan(InkDevice.Touch, id, at);
            return;
        }
        if (active.Count == 2 && _pinch is null)
        {
            // Two fingers landing together pinch, and a line the first had only just begun goes. One the
            // first finger has been drawing for a while stays a line: this finger is a resting hand.
            if (_ink is { Device: InkDevice.Touch, Kind: not InkGestureKind.Pan } line && !YoungLine(line))
            {
                finger.Ignored = true;
                return;
            }
            var first = active.First(f => f.Id != id);
            EndInkGesture(cancel: true);
            _pinch = new Pinch(first.Id, id, first.At, at, Zoom, PanT.X, PanT.Y);
            return;
        }
        finger.Ignored = true; // a third finger changes nothing
    }

    private bool YoungLine(InkGesture g)
    {
        if (_ticks() - g.Began < YoungLineMs || g.Points is not { } points) return true;
        foreach (var p in points)
            if ((new Point(p.X, p.Y) - g.StartScreen).Length >= YoungLinePx) return false;
        return true;
    }

    private void FingerMove(int id, Point at, StylusPointCollection points)
    {
        if (!_fingers.TryGetValue(id, out var f)) return;
        f.At = at;
        if (f.Ignored) return;
        if (_pinch is { } p)
        {
            if ((p.A == id || p.B == id) && _fingers.TryGetValue(p.A, out var a) && _fingers.TryGetValue(p.B, out var b))
                UpdatePinch(a.At, b.At);
            return;
        }
        if (_ink is { Device: InkDevice.Touch } g && g.DeviceId == id)
        {
            if (g.Kind == InkGestureKind.Pan) PanTo(at);
            else InkGestureMove(TouchPoints(points));
        }
    }

    private void FingerUp(int id, StylusPointCollection? points)
    {
        if (!_fingers.Remove(id, out var f)) return;
        if (!f.Ignored) LetGoOf(f, points);
        if (_fingers.Count == 0 && _viewTouches.Count == 0) _fingersBegan = null;
    }

    /// <summary>What a finger that lifted (or turned out to be gone) was doing ends.</summary>
    private void LetGoOf(Finger f, StylusPointCollection? points)
    {
        if (_pinch is { } p && (p.A == f.Id || p.B == f.Id))
        {
            _pinch = null;
            StashViewport();
            // The finger still down carries on moving the board from where it is.
            var other = p.A == f.Id ? p.B : p.A;
            if (_fingers.TryGetValue(other, out var o) && !o.Ignored && _ink is null) BeginPan(InkDevice.Touch, other, o.At);
            return;
        }
        if (_ink is { Device: InkDevice.Touch } g && g.DeviceId == f.Id)
        {
            if (g.Kind != InkGestureKind.Pan && points is { Count: > 0 }) InkGestureMove(TouchPoints(points));
            EndInkGesture(cancel: false);
        }
    }

    /// <summary>Lets go of fingers that WPF says have left the glass, though their lift never came here,
    /// and of a move or pinch that has lost its finger.</summary>
    private void ReapFingers()
    {
        foreach (var f in _fingers.Values.ToList())
        {
            bool down;
            try { down = f.Down(); }
            catch { down = false; }
            if (down) continue;
            _fingers.Remove(f.Id);
            if (!f.Ignored) LetGoOf(f, null);
        }
        if (_ink is { Device: InkDevice.Touch } g && !_fingers.ContainsKey(g.DeviceId)) EndInkGesture(cancel: false);
        if (_drawMode && _pinch is { } p && (!_fingers.ContainsKey(p.A) || !_fingers.ContainsKey(p.B)))
        {
            _pinch = null;
            StashViewport();
        }
        if (_fingers.Count == 0 && _viewTouches.Count == 0) _fingersBegan = null;
    }

    /// <summary>A finger presses evenly: its points draw at the stroke's own width.</summary>
    private static StylusPointCollection TouchPoints(StylusPointCollection points)
    {
        var flat = new StylusPointCollection(points.Count);
        foreach (var p in points) flat.Add(new StylusPoint(p.X, p.Y, 0.5f));
        return flat;
    }

    /// <summary>Zooms and moves the board to follow two fingers from where the pinch began.</summary>
    private void UpdatePinch(Point a, Point b)
    {
        if (_pinch is not { } p) return;
        var c0 = new Point((p.A0.X + p.B0.X) / 2, (p.A0.Y + p.B0.Y) / 2);
        var c1 = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        var d0 = Math.Max(8, (p.A0 - p.B0).Length);
        var scale = Math.Clamp(p.Zoom * (a - b).Length / d0, 0.2, 3.0);
        // The board point that was under the fingers' midpoint stays under it.
        var bx = (c0.X - p.PanX) / p.Zoom;
        var by = (c0.Y - p.PanY) / p.Zoom;
        ZoomT.ScaleX = ZoomT.ScaleY = scale;
        PanT.X = c1.X - scale * bx;
        PanT.Y = c1.Y - scale * by;
        SyncGrid();
        RefreshInkZoomDependent();
    }

    // ── fingers, drawing off ────────────────────────────────────────────────────
    // Outside drawing a finger is the mouse (WPF promotes the first one), which pans the empty board. A
    // second finger landing while the first is on the empty board turns that into a pinch. (The first
    // finger's mouse press can arrive late, after Windows has ruled out a press-and-hold; the board's
    // mouse handlers ignore it while a pinch is on.) A palm never gets here (OnPageStylusDown).
    private readonly Dictionary<int, (TouchDevice Device, Point At)> _viewTouches = new();
    private bool _firstTouchOnBoard;

    private void OnViewportTouchDown(object? sender, TouchEventArgs e)
    {
        if (_drawMode) return;
        ReapGestures();
        if (_viewTouches.Count == 0)
        {
            _firstTouchOnBoard = ReferenceEquals(e.OriginalSource, Viewport);
            _fingersBegan ??= (_ticks(), Zoom, PanT.X, PanT.Y);
        }
        _viewTouches[e.TouchDevice.Id] = (e.TouchDevice, e.GetTouchPoint(InkSurface).Position);
        if (_viewTouches.Count != 2 || !_firstTouchOnBoard || _pinch is not null
            || _dragNode is not null || _resizeNode is not null || _linkFrom is not null || _marquee)
            return;

        if (_panning)
        {
            _panning = false;
            if (Viewport.IsMouseCaptured) Viewport.ReleaseMouseCapture();
            Viewport.Cursor = Cursors.Arrow;
        }
        var pair = _viewTouches.Take(2).ToArray();
        _pinch = new Pinch(pair[0].Key, pair[1].Key, pair[0].Value.At, pair[1].Value.At, Zoom, PanT.X, PanT.Y);
        e.TouchDevice.Capture(Viewport);
        e.Handled = true;
    }

    private void OnViewportTouchMove(object? sender, TouchEventArgs e)
    {
        if (_drawMode || !_viewTouches.TryGetValue(e.TouchDevice.Id, out var t)) return;
        _viewTouches[e.TouchDevice.Id] = (t.Device, e.GetTouchPoint(InkSurface).Position);
        if (_pinch is not { } p) return;
        if (_viewTouches.TryGetValue(p.A, out var a) && _viewTouches.TryGetValue(p.B, out var b))
            UpdatePinch(a.At, b.At);
        e.Handled = e.TouchDevice.Id != p.A; // the first finger is the mouse: leave it its own events
    }

    private void OnViewportTouchUp(object? sender, TouchEventArgs e) => ViewTouchGone(e.TouchDevice.Id);

    private void OnViewportLostTouch(object? sender, TouchEventArgs e) => ViewTouchGone(e.TouchDevice.Id);

    private void ViewTouchGone(int id)
    {
        if (!_viewTouches.Remove(id)) return;
        if (_pinch is { } p && !_drawMode && (id == p.A || id == p.B))
        {
            _pinch = null;
            StashViewport();
        }
        if (_viewTouches.Count == 0 && _fingers.Count == 0) _fingersBegan = null;
    }

    /// <summary>Lets go of touches WPF no longer has, whose lift never reached the board, and of a pinch
    /// that has lost one.</summary>
    private void ReapViewTouches()
    {
        foreach (var (id, t) in _viewTouches.ToList())
        {
            bool active;
            try { active = t.Device.IsActive; }
            catch { active = false; }
            if (!active) ViewTouchGone(id);
        }
        if (!_drawMode && _pinch is { } p && (!_viewTouches.ContainsKey(p.A) || !_viewTouches.ContainsKey(p.B)))
        {
            _pinch = null;
            StashViewport();
        }
    }

    // ── gestures ────────────────────────────────────────────────────────────────

    private void BeginInkGesture(InkTool tool, InkDevice device, int deviceId, Point at, StylusPointCollection points)
    {
        if (_board is null) return;
        var board = ToBoard(at);
        var g = new InkGesture { Device = device, DeviceId = deviceId, StartScreen = at, LastBoard = board, Began = _ticks() };

        // A press inside the selected lines moves them, whatever the tool (the eraser still rubs).
        if (tool != InkTool.Eraser && _inkSelected.Count > 0 && InSelectionBox(board))
        {
            g.Kind = InkGestureKind.Move;
            _ink = g;
            UpdateInkSurface();
            return;
        }

        switch (tool)
        {
            case InkTool.Eraser:
                g.Kind = InkGestureKind.Erase;
                _ink = g;
                ShowEraserRing(at);
                EraseAlong(board, board);
                break;
            case InkTool.Select:
                if (_inkSelected.Count > 0) ClearInkSelection();
                g.Kind = InkGestureKind.Lasso;
                g.Lasso = [board];
                _ink = g;
                ShowLasso(g.Lasso);
                break;
            default:
            {
                if (_inkSelected.Count > 0) ClearInkSelection();
                var (highlighter, color, width) = tool == InkTool.Highlighter
                    ? (true, InkSettings.PlanInkHighlighterColor, HighlighterWidthPx)
                    : (false, InkSettings.PlanInkPenColor, PenWidthPx);
                g.Kind = InkGestureKind.Draw;
                g.Highlighter = highlighter;
                g.Color = color;
                g.WidthPx = width;
                g.Points = new StylusPointCollection();
                Append(g.Points, points);
                _ink = g;
                break;
            }
        }
        UpdateInkSurface();
    }

    /// <summary>Adds input points as plain X, Y and pressure (a pen may report more: tilt, buttons).</summary>
    private static void Append(StylusPointCollection to, StylusPointCollection from)
    {
        foreach (var p in from) to.Add(new StylusPoint(p.X, p.Y, p.PressureFactor));
    }

    private void InkGestureMove(StylusPointCollection points)
    {
        if (_ink is not { } g || points.Count == 0) return;
        switch (g.Kind)
        {
            case InkGestureKind.Draw:
                Append(g.Points!, points);
                if (g.Wet is not null) Append(g.Wet.StylusPoints, points);
                break;
            case InkGestureKind.Erase:
                foreach (var p in points)
                {
                    var board = ToBoard(new Point(p.X, p.Y));
                    EraseAlong(g.LastBoard, board);
                    g.LastBoard = board;
                }
                var last = points[^1];
                ShowEraserRing(new Point(last.X, last.Y));
                break;
            case InkGestureKind.Lasso:
                foreach (var p in points)
                {
                    var board = ToBoard(new Point(p.X, p.Y));
                    if ((board - g.Lasso![^1]).Length * Zoom < 2) continue;
                    g.Lasso.Add(board);
                }
                ShowLasso(g.Lasso!);
                break;
            case InkGestureKind.Move:
            {
                var p = points[^1];
                var board = ToBoard(new Point(p.X, p.Y));
                g.Moved = board - ToBoard(g.StartScreen);
                _inkSelectionVisual.RenderTransform = new TranslateTransform(g.Moved.X, g.Moved.Y);
                break;
            }
        }
    }

    /// <summary>Finishes (or, with <paramref name="cancel"/>, drops) the gesture in progress.</summary>
    private void EndInkGesture(bool cancel)
    {
        if (_ink is not { } g) return;
        _ink = null;
        try
        {
            switch (g.Kind)
            {
                case InkGestureKind.Draw:
                    var began = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (!cancel) FinishStroke(g);
                    if (g.Wet is not null) InkSurface.Wet.Strokes.Remove(g.Wet);
                    if (cancel && g.Device == InkDevice.Pen) InkSurface.Renderer.Abort();
                    if (!cancel) NoteLineEnd(System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds);
                    break;
                case InkGestureKind.Erase:
                    // What was rubbed out is gone either way; one undo step brings it all back.
                    if (g.Changed) { FlushInkRefresh(); Commit(); }
                    break;
                case InkGestureKind.Lasso:
                    HideLasso();
                    if (!cancel) FinishLasso(g);
                    break;
                case InkGestureKind.Move:
                    _inkSelectionVisual.RenderTransform = Transform.Identity;
                    if (!cancel && g.Moved.Length * Zoom >= 1) MoveSelectedStrokes(g.Moved);
                    break;
                case InkGestureKind.Pan:
                    StashViewport();
                    break;
            }
        }
        finally
        {
            if (_penLookStale) ApplyPenLook();
            if (InkSurface.IsMouseCaptured && g.Device == InkDevice.Mouse) InkSurface.ReleaseMouseCapture();
            UpdateInkSurface();
            UpdateInkPalette();
            if (_drawMode) PlanStatus.Text = DrawPrompt;
        }
    }

    private void ShowWetLine(InkGesture g)
    {
        if (g.Points is null) return;
        g.Wet = new Stroke(g.Points.Clone(), LiveLook(g.Highlighter, g.Color, g.WidthPx));
        InkSurface.Wet.Strokes.Add(g.Wet);
    }

    // ── pan ─────────────────────────────────────────────────────────────────────

    private void BeginPan(InkDevice device, int deviceId, Point at)
    {
        _ink = new InkGesture
        {
            Kind = InkGestureKind.Pan, Device = device, DeviceId = deviceId, StartScreen = at,
            PanX = PanT.X, PanY = PanT.Y, Began = _ticks()
        };
        UpdateInkSurface();
    }

    private void PanTo(Point at)
    {
        if (_ink is not { Kind: InkGestureKind.Pan } g) return;
        PanT.X = g.PanX + (at.X - g.StartScreen.X);
        PanT.Y = g.PanY + (at.Y - g.StartScreen.Y);
        SyncGrid();
    }

    // ── strokes ─────────────────────────────────────────────────────────────────

    /// <summary>Turns the finished line into a stroke on the board: in board units, its points thinned to
    /// what the eye can tell apart, one undo step.</summary>
    private void FinishStroke(InkGesture g)
    {
        if (_board is null || g.Points is not { Count: > 0 } screen) return;
        var z = Zoom;
        var points = new List<InkPoint>(screen.Count);
        foreach (var p in screen)
            points.Add(new InkPoint((p.X - PanT.X) / z, (p.Y - PanT.Y) / z, g.Highlighter ? 0.5f : p.PressureFactor));
        // A quarter of a screen pixel off the line, or a twentieth of the pen's pressure, is invisible.
        var kept = PlanInkGeometry.Simplify(points, 0.25 / z, 0.05f);
        var stroke = new PlanStroke
        {
            Color = g.Color,
            Width = Math.Round(g.WidthPx / z, 3),
            Highlighter = g.Highlighter,
            Points = PlanInkCodec.Encode(kept)
        };
        (_board.Strokes ??= new()).Add(stroke);
        SyncInk();   // the line is in the cached layer before the live one goes (no flicker)
        Commit();
    }

    /// <summary>Rubs out what the eraser passes over between two board points: the strokes it touches
    /// whole, or only the part under it, as set.</summary>
    private void EraseAlong(Point from, Point to)
    {
        if (_board?.Strokes is not { Count: > 0 } strokes || _ink is not { } g) return;
        var d = EraserWidthPx / Zoom;
        var shape = new EllipseStylusShape(d, d);
        Point[] path = from == to ? [to] : [from, to];
        var reach = new Rect(from, to);
        reach.Inflate(d / 2 + 1, d / 2 + 1);
        var whole = InkSettings.PlanInkEraseWholeStrokes;

        List<PlanStroke>? next = null;
        for (var i = 0; i < strokes.Count; i++)
        {
            var s = strokes[i];
            var entry = Entry(s);
            if (entry is null || !entry.Bounds.IntersectsWith(reach) || !entry.Ink.HitTest(path, shape))
            {
                next?.Add(s);
                continue;
            }
            next ??= strokes.Take(i).ToList();
            if (whole) continue;
            foreach (var piece in entry.Ink.GetEraseResult(path, shape))
            {
                var model = s.WithPoints(PlanInkCodec.Encode(PointsOf(piece)), sameId: false);
                Remember(model, piece);
                next.Add(model);
            }
        }
        if (next is null) return;
        _board.Strokes = next.Count > 0 ? next : null;
        _inkSelected.RemoveWhere(id => !next.Any(s => s.Id == id));
        g.Changed = true;
        QueueInkRefresh();
    }

    private static List<InkPoint> PointsOf(Stroke stroke)
    {
        var list = new List<InkPoint>(stroke.StylusPoints.Count);
        foreach (var p in stroke.StylusPoints) list.Add(new InkPoint(p.X, p.Y, p.PressureFactor));
        return list;
    }

    /// <summary>Selects what the lasso goes round (most of a line inside it), or, for a tap, the line
    /// under it.</summary>
    private void FinishLasso(InkGesture g)
    {
        if (_board?.Strokes is not { Count: > 0 } strokes || g.Lasso is not { } lasso) { ClearInkSelection(); return; }
        var length = 0.0;
        for (var i = 1; i < lasso.Count; i++) length += (lasso[i] - lasso[i - 1]).Length;

        if (lasso.Count < 3 || length * Zoom < 12)
        {
            var hit = StrokeAt(lasso[0]);
            SetInkSelection(hit is null ? [] : [hit.Id]);
            return;
        }
        var box = new Rect(lasso[0], lasso[0]);
        foreach (var p in lasso) box.Union(p);
        var picked = new List<string>();
        // Most of a line's length inside the loop. (WPF's own lasso test weighs the stored points, and a
        // straight line is stored as its two ends, so circling its middle would never take it.)
        foreach (var s in strokes)
            if (Entry(s) is { } e && e.Bounds.IntersectsWith(box)
                && PlanInkGeometry.FractionInside(e.Ink.StylusPoints.Select(p => new Point(p.X, p.Y)).ToList(), lasso, 3 / Zoom) >= 0.6)
                picked.Add(s.Id);
        SetInkSelection(picked);
    }

    /// <summary>The topmost stroke within a few screen pixels of a board point.</summary>
    private PlanStroke? StrokeAt(Point board)
    {
        if (_board?.Strokes is not { } strokes) return null;
        var reach = 8 / Zoom;
        for (var i = strokes.Count - 1; i >= 0; i--)
        {
            var e = Entry(strokes[i]);
            if (e is null) continue;
            var r = e.Bounds;
            r.Inflate(reach, reach);
            if (r.Contains(board) && e.Ink.HitTest(board, reach * 2)) return strokes[i];
        }
        return null;
    }

    private void MoveSelectedStrokes(Vector by)
    {
        if (_board?.Strokes is not { } strokes) return;
        _board.Strokes = strokes.Select(s => _inkSelected.Contains(s.Id) ? Translated(s, by) : s).ToList();
        SyncInk();
        RefreshInkSelectionVisual();
        Commit();
    }

    private static PlanStroke Translated(PlanStroke s, Vector by) =>
        s.WithPoints(PlanInkCodec.Encode(PlanInkCodec.Decode(s.Points)
            .Select(p => p with { X = p.X + by.X, Y = p.Y + by.Y }).ToList()), sameId: true);

    // ── selection ───────────────────────────────────────────────────────────────

    private void SetInkSelection(IEnumerable<string> ids)
    {
        _inkSelected.Clear();
        foreach (var id in ids) _inkSelected.Add(id);
        SyncInk();
        RefreshInkSelectionVisual();
        UpdateInkPalette();
        if (_drawMode) PlanStatus.Text = DrawPrompt;
    }

    private void ClearInkSelection()
    {
        if (_inkSelected.Count == 0) return;
        SetInkSelection([]);
    }

    private List<PlanStroke> SelectedStrokes() =>
        _board?.Strokes?.Where(s => _inkSelected.Contains(s.Id)).ToList() ?? new();

    private Rect SelectionBounds()
    {
        var box = Rect.Empty;
        foreach (var s in SelectedStrokes())
            if (Entry(s) is { } e) box.Union(e.Bounds);
        return box;
    }

    private bool InSelectionBox(Point board)
    {
        if (_inkSelected.Count == 0) return false;
        var box = SelectionBounds();
        if (box.IsEmpty) return false;
        var pad = 8 / Zoom;
        box.Inflate(pad, pad);
        return box.Contains(board);
    }

    private void RefreshInkSelectionVisual()
    {
        var entries = SelectedStrokes().Select(Entry).OfType<InkEntry>().ToList();
        _inkSelectionVisual.Show(entries.Select(e => (e.Shape, InkBrush(e.Model))).ToList(),
            SelectionBounds(), Res("AccentBrush"), Zoom);
    }

    private void DeleteSelectedStrokes()
    {
        if (_board?.Strokes is not { } strokes || _inkSelected.Count == 0) return;
        var left = strokes.Where(s => !_inkSelected.Contains(s.Id)).ToList();
        var gone = strokes.Count - left.Count;
        _board.Strokes = left.Count > 0 ? left : null;
        _inkSelected.Clear();
        SyncInk();
        RefreshInkSelectionVisual();
        Commit();
        UpdateInkPalette();
        PlanStatus.Text = $"Deleted {gone} line{(gone == 1 ? "" : "s")}.";
    }

    /// <summary>Recolours (and with <paramref name="widthPx"/>, re-sizes) the selected lines.</summary>
    private void RestyleSelectedStrokes(string? color, bool setColor, double? widthPx)
    {
        if (_board?.Strokes is not { } strokes || _inkSelected.Count == 0) return;
        _board.Strokes = strokes.Select(s => !_inkSelected.Contains(s.Id) ? s
            : s.WithLook(setColor ? color : s.Color, widthPx is { } w ? Math.Round(w / Zoom, 3) : s.Width)).ToList();
        SyncInk();
        RefreshInkSelectionVisual();
        Commit();
    }

    private void CopySelectedStrokes()
    {
        var picked = SelectedStrokes();
        if (picked.Count == 0) return;
        _inkClipboard = picked;
        _inkClipboardNewer = true;
        PlanStatus.Text = $"Copied {picked.Count} line{(picked.Count == 1 ? "" : "s")}.";
    }

    /// <summary>Puts copied lines on this board a little down and right of where they were, selected.</summary>
    private void PasteStrokes(List<PlanStroke> strokes)
    {
        if (_board is null || strokes.Count == 0) return;
        var shift = new Vector(GridCell, GridCell);
        var pasted = strokes.Select(s =>
        {
            var moved = Translated(s, shift);
            return moved.WithPoints(moved.Points, sameId: false);
        }).ToList();
        (_board.Strokes ??= new()).AddRange(pasted);
        if (!_drawMode) SetDrawMode(true);
        SetInkSelection(pasted.Select(s => s.Id));
        Commit();
        PlanStatus.Text = $"Pasted {pasted.Count} line{(pasted.Count == 1 ? "" : "s")}.";
    }

    private void SelectAllStrokes()
    {
        if (_board?.Strokes is not { Count: > 0 } strokes) return;
        if (_inkTool is not InkTool.Select) SetInkTool(InkTool.Select);
        SetInkSelection(strokes.Select(s => s.Id));
    }

    // ── the cached layer ────────────────────────────────────────────────────────

    /// <summary>A stroke's WPF ink and outline, made on first use and kept.</summary>
    private InkEntry? Entry(PlanStroke s)
    {
        if (_inkCache.TryGetValue(s.Id, out var e))
        {
            if (ReferenceEquals(e.Model, s)) return e;
            if (SameStroke(e.Model, s)) { e.Model = s; return e; }
        }
        var points = PlanInkCodec.Decode(s.Points);
        if (points.Count == 0) return null; // nothing readable: kept on the board, not drawn
        var collection = new StylusPointCollection(points.Count);
        foreach (var p in points) collection.Add(new StylusPoint(p.X, p.Y, p.Pressure));
        return Remember(s, new Stroke(collection, DryLook(s)));
    }

    private InkEntry Remember(PlanStroke model, Stroke ink)
    {
        var shape = ink.GetGeometry();
        shape.Freeze();
        var entry = new InkEntry { Model = model, Ink = ink, Shape = shape, Bounds = shape.Bounds };
        _inkCache[model.Id] = entry;
        return entry;
    }

    private Rect BoundsOf(PlanStroke s) => Entry(s)?.Bounds ?? Rect.Empty;

    private static bool SameStroke(PlanStroke a, PlanStroke b) =>
        a.Points == b.Points && a.Color == b.Color && a.Width == b.Width && a.Highlighter == b.Highlighter;

    /// <summary>The shape a stored stroke is drawn and hit-tested with, in board units.</summary>
    private static DrawingAttributes DryLook(PlanStroke s) => new()
    {
        Color = Colors.Black, // the shape only: the layer fills it in the stroke's colour
        Width = Math.Max(0.05, s.Width),
        Height = Math.Max(0.05, s.Width),
        FitToCurve = true,
        IgnorePressure = s.Highlighter,
        StylusTip = StylusTip.Ellipse
    };

    private Color TextInk => (TryFindResource("TextPrimaryBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0xEC, 0xEA, 0xE4);

    private Color InkColor(string? hex, bool highlighter)
    {
        var c = TextInk;
        if (!string.IsNullOrWhiteSpace(hex))
            try { c = (Color)ColorConverter.ConvertFromString(hex); } catch { /* keep the text colour */ }
        c.A = highlighter ? (byte)128 : (byte)255; // WPF draws highlighter ink at half opacity
        return c;
    }

    private readonly Dictionary<(string?, bool), Brush> _inkBrushes = new();

    private Brush InkBrush(PlanStroke s)
    {
        var key = (s.Color?.ToUpperInvariant(), s.Highlighter);
        if (_inkBrushes.TryGetValue(key, out var b)) return b;
        b = new SolidColorBrush(InkColor(s.Color, s.Highlighter));
        b.Freeze();
        return _inkBrushes[key] = b;
    }

    /// <summary>Brings the cached layer in line with the board's strokes: only the tiles under strokes
    /// that came, went or changed are drawn again.</summary>
    private void SyncInk()
    {
        if (_board is null) return;
        var text = TextInk;
        var full = _inkShownBoard != _board.Id || text != _inkShownText;
        if (full) _inkBrushes.Clear();
        var strokes = _board.Strokes ?? new List<PlanStroke>();
        if (_inkSelected.Count > 0)
        {
            // Lines undone, erased or deleted elsewhere drop out of the selection.
            var ids = strokes.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            _inkSelected.RemoveWhere(id => !ids.Contains(id));
        }

        var now = new Dictionary<string, PlanStroke>(StringComparer.Ordinal);
        var dirty = Rect.Empty;
        foreach (var s in strokes)
        {
            if (_inkSelected.Contains(s.Id)) continue; // drawn by the selection instead
            now[s.Id] = s;
            if (full) continue;
            if (_inkShown.TryGetValue(s.Id, out var old) && (ReferenceEquals(old, s) || SameStroke(old, s))) continue;
            if (old is not null) dirty.Union(BoundsOf(old));
            dirty.Union(BoundsOf(s));
        }
        if (!full)
            foreach (var (id, old) in _inkShown)
                if (!now.ContainsKey(id)) dirty.Union(BoundsOf(old));

        _inkShown = now;
        _inkShownBoard = _board.Id;
        _inkShownText = text;
        _inkLayer.SetContent(InkItems(now), full ? null : dirty);
        PruneInkCache();
    }

    /// <summary>The layer's items: highlighter strokes first (under the pen's lines, as WPF's own ink
    /// presenter has it), each group in drawing order. Consecutive strokes of one look share an ink, and a
    /// new ink starts where the look changes, so overlapping strokes keep their order in every tile.</summary>
    private List<(Geometry, TiledCachedLayer.Ink)> InkItems(Dictionary<string, PlanStroke> shown)
    {
        var items = new List<(Geometry, TiledCachedLayer.Ink)>(shown.Count);
        if (_board?.Strokes is not { } strokes) return items;
        foreach (var highlighters in new[] { true, false })
        {
            TiledCachedLayer.Ink? run = null;
            Brush? runBrush = null;
            foreach (var s in strokes)
            {
                if (s.Highlighter != highlighters || !shown.ContainsKey(s.Id) || Entry(s) is not { } e) continue;
                var brush = InkBrush(s);
                if (!ReferenceEquals(brush, runBrush))
                {
                    run = new TiledCachedLayer.Ink(brush, null);
                    runBrush = brush;
                }
                items.Add((e.Shape, run!));
            }
        }
        return items;
    }

    /// <summary>Drops cached strokes the board no longer has, once the cache has grown well past it
    /// (undo may still bring some back, and then they are simply made again).</summary>
    private void PruneInkCache()
    {
        var count = _board?.Strokes?.Count ?? 0;
        if (_inkCache.Count <= count * 3 + 400) return;
        var keep = (_board?.Strokes ?? new()).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _inkCache.Keys.Where(id => !keep.Contains(id)).ToList()) _inkCache.Remove(id);
    }

    /// <summary>Erasing changes the board on every pen sample; the layer catches up once a frame.</summary>
    private void QueueInkRefresh()
    {
        if (_inkRefreshQueued) return;
        _inkRefreshQueued = true;
        CompositionTarget.Rendering += OnInkRefreshFrame;
    }

    private void OnInkRefreshFrame(object? sender, EventArgs e) => FlushInkRefresh();

    private void FlushInkRefresh()
    {
        if (!_inkRefreshQueued) return;
        _inkRefreshQueued = false;
        CompositionTarget.Rendering -= OnInkRefreshFrame;
        SyncInk();
        RefreshInkSelectionVisual();
    }

    private void ApplyInkVisibility()
    {
        var show = _drawMode || !InkHidden;
        _inkLayer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _inkSelectionVisual.Visibility = _inkLayer.Visibility;
    }

    /// <summary>Things drawn at a fixed size on screen (the eraser ring, the selection outline) follow a zoom.</summary>
    private void RefreshInkZoomDependent()
    {
        if (_inkSelected.Count > 0) RefreshInkSelectionVisual();
        if (_eraserRing.Visibility == Visibility.Visible)
        {
            _eraserRing.Width = _eraserRing.Height = EraserWidthPx / Zoom;
            _eraserRing.StrokeThickness = 1.25 / Zoom;
        }
        if (_inkLasso.Visibility == Visibility.Visible) _inkLasso.StrokeThickness = 1.5 / Zoom;
        HidePenDot(); // the next hover puts it back at the new size
    }

    /// <summary>Everything drawn on the board, in board units (empty when there is nothing).</summary>
    private Rect InkBounds()
    {
        var box = Rect.Empty;
        foreach (var s in _board?.Strokes ?? new())
            if (Entry(s) is { } e) box.Union(e.Bounds);
        return box;
    }

    // ── lasso + eraser ring ─────────────────────────────────────────────────────

    private void ShowLasso(List<Point> points)
    {
        if (points.Count == 0) return;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(points[0], false, false);
            for (var i = 1; i < points.Count; i++) c.LineTo(points[i], true, false);
        }
        g.Freeze();
        _inkLasso.Data = g;
        _inkLasso.Stroke = Res("AccentTextBrush");
        _inkLasso.StrokeThickness = 1.5 / Zoom;
        _inkLasso.StrokeDashArray = new DoubleCollection { 4, 3 };
        _inkLasso.Visibility = Visibility.Visible;
    }

    private void HideLasso()
    {
        _inkLasso.Visibility = Visibility.Collapsed;
        _inkLasso.Data = null;
    }

    private void ShowEraserRing(Point screen)
    {
        var d = EraserWidthPx / Zoom;
        var at = ToBoard(screen);
        _eraserRing.Width = _eraserRing.Height = d;
        _eraserRing.Stroke = Res("TextSecondaryBrush");
        _eraserRing.StrokeThickness = 1.25 / Zoom;
        _eraserRing.Fill = new SolidColorBrush(Color.FromArgb(40, 0x80, 0x80, 0x80));
        Canvas.SetLeft(_eraserRing, at.X - d / 2);
        Canvas.SetTop(_eraserRing, at.Y - d / 2);
        _eraserRing.Visibility = Visibility.Visible;
    }

    private void HideEraserRing() => _eraserRing.Visibility = Visibility.Collapsed;

    /// <summary>The hover dot, for the pen and the highlighter only, and not over the selected lines
    /// (a press there moves them).</summary>
    private void ShowPenDot(Point screen)
    {
        if (!_drawMode || _ink is not null || _inkTool is not (InkTool.Pen or InkTool.Highlighter) || InSelectionBox(ToBoard(screen)))
        {
            HidePenDot();
            return;
        }
        var (highlighter, color, widthPx) = ToolLook();
        var d = Math.Max(3, widthPx) / Zoom;
        var at = ToBoard(screen);
        _penDot.Width = _penDot.Height = d;
        _penDot.Fill = new SolidColorBrush(InkColor(color, highlighter));
        _penDot.Stroke = Res("TextTertiaryBrush");
        _penDot.StrokeThickness = 1 / Zoom;
        Canvas.SetLeft(_penDot, at.X - d / 2);
        Canvas.SetTop(_penDot, at.Y - d / 2);
        _penDot.Visibility = Visibility.Visible;
    }

    private void HidePenDot() => _penDot.Visibility = Visibility.Collapsed;

    // ── commands ────────────────────────────────────────────────────────────────

    private void SetInkTool(InkTool tool)
    {
        if (_ink is not null) EndInkGesture(cancel: false);
        _inkTool = tool;
        InkSettings.PlanInkTool = ToolName(tool);
        SaveInkSettings();
        if (tool is InkTool.Pen or InkTool.Highlighter or InkTool.Eraser) ClearInkSelection();
        if (tool != InkTool.Eraser) HideEraserRing();
        HidePenDot();
        ApplyPenLook();
        UpdateInkPalette();
        UpdateInkCursor(null);
        if (_drawMode) PlanStatus.Text = DrawPrompt;
    }

    /// <summary>A colour picked on the palette: for the selected lines if there are any, and for the next
    /// line either way (the highlighter's own colour while it is the tool).</summary>
    private void PickInkColor(string? hex)
    {
        if (_inkSelected.Count > 0) RestyleSelectedStrokes(hex, setColor: true, widthPx: null);
        if (_inkTool == InkTool.Highlighter) InkSettings.PlanInkHighlighterColor = hex ?? InkSettings.PlanInkHighlighterColor;
        else
        {
            InkSettings.PlanInkPenColor = hex;
            if (_inkTool is InkTool.Eraser || (_inkTool is InkTool.Select && _inkSelected.Count == 0)) SetInkTool(InkTool.Pen);
        }
        SaveInkSettings();
        ApplyPenLook();
        UpdateInkPalette();
        Viewport.Focus();
    }

    private void PickInkSize(int index)
    {
        index = SizeIndex(index);
        switch (_inkTool)
        {
            case InkTool.Highlighter: InkSettings.PlanInkHighlighterSize = index; break;
            case InkTool.Eraser: InkSettings.PlanInkEraserSize = index; break;
            default: InkSettings.PlanInkPenSize = index; break;
        }
        if (_inkSelected.Count > 0)
        {
            var highlighters = SelectedStrokes().All(s => s.Highlighter);
            RestyleSelectedStrokes(null, setColor: false, (highlighters ? HighlighterWidths : PenWidths)[index]);
        }
        SaveInkSettings();
        ApplyPenLook();
        UpdateInkPalette();
        Viewport.Focus();
    }

    private async Task ClearDrawingAsync()
    {
        if (_board is not { HasDrawing: true } board || _owner is null) return;
        if (!await AppDialog.ConfirmAsync(_owner, "Clear drawing",
                $"Rub out everything drawn on '{board.Name}'? Ctrl+Z brings it back.", "Clear", "Cancel", danger: true))
            return;
        EndInkGesture(cancel: true);
        board.Strokes = null;
        _inkSelected.Clear();
        SyncInk();
        RefreshInkSelectionVisual();
        Commit();
        UpdateInkPalette();
        PlanStatus.Text = "Drawing cleared.";
    }

    private void ToggleInkHidden()
    {
        InkSettings.PlanInkHidden = !InkSettings.PlanInkHidden;
        SaveInkSettings();
        if (InkSettings.PlanInkHidden && _drawMode) SetDrawMode(false);
        ApplyInkVisibility();
        PlanStatus.Text = InkSettings.PlanInkHidden ? "Drawings hidden on every board." : "Drawings shown.";
    }

    /// <summary>Keys while drawing, or for selected lines. True when the key was used.</summary>
    private bool HandleInkKey(KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            if (_ink is not null) { EndInkGesture(cancel: true); return true; }
            if (_inkSelected.Count > 0) { ClearInkSelection(); return true; }
            if (_drawMode) { SetDrawMode(false); return true; }
            return false;
        }
        if (_ink is not null && mods == ModifierKeys.Control && key is Key.Z or Key.Y)
            return true; // not in the middle of a line
        if (key is Key.Delete or Key.Back && _inkSelected.Count > 0) { DeleteSelectedStrokes(); return true; }
        if (mods == ModifierKeys.Control && key == Key.C && _inkSelected.Count > 0) { CopySelectedStrokes(); return true; }
        if (mods == ModifierKeys.Control && key == Key.D && _inkSelected.Count > 0) { PasteStrokes(SelectedStrokes()); return true; }
        if (mods == ModifierKeys.Control && key == Key.V && _inkClipboard is { Count: > 0 } clip
            && (_inkClipboardNewer || _clipboard is null)) { PasteStrokes(clip); return true; }
        if (mods == ModifierKeys.Control && key == Key.A && _drawMode) { SelectAllStrokes(); return true; }

        if (mods != ModifierKeys.None) return false;
        if (!_drawMode)
        {
            if (key != Key.D) return false;
            SetDrawMode(true);
            return true;
        }
        switch (key)
        {
            case Key.P: SetInkTool(InkTool.Pen); return true;
            case Key.H: SetInkTool(InkTool.Highlighter); return true;
            case Key.E: SetInkTool(InkTool.Eraser); return true;
            case Key.S: SetInkTool(InkTool.Select); return true;
            case Key.D: SetDrawMode(false); return true;
            default: return false;
        }
    }

    // ── palette ─────────────────────────────────────────────────────────────────

    private readonly Dictionary<InkTool, ToggleButton> _toolButtons = new();
    private readonly List<(ToggleButton Button, string? Hex)> _swatches = new();
    private readonly List<(ToggleButton Button, Ellipse Dot)> _sizeButtons = new();
    private Button? _undoButton, _redoButton, _deleteInkButton;

    private void BuildInkPalette()
    {
        var row = InkPaletteRow;
        row.Children.Clear();

        ToggleButton Tool(InkTool tool, string glyph, string tip)
        {
            var b = new ToggleButton
            {
                Content = glyph, Style = (Style)FindResource("ToolbarToggleButton"), Margin = new Thickness(1),
                Padding = new Thickness(7, 5, 7, 5), FontSize = 14, ToolTip = tip
            };
            b.Click += (_, _) => { SetInkTool(tool); Viewport.Focus(); };
            _toolButtons[tool] = b;
            row.Children.Add(b);
            return b;
        }

        Tool(InkTool.Pen, "", "Pen (P)\nPress harder for a thicker line.");
        Tool(InkTool.Highlighter, "", "Highlighter (H)");
        Tool(InkTool.Eraser, "", "Eraser (E)\nThe back of the pen erases too, whatever the tool.");
        Tool(InkTool.Select, "", "Select (S)\nCircle lines or tap one, then drag it, recolour it or press Delete. "
                                       + "Holding the pen's side button selects too.");
        row.Children.Add(PaletteSeparator());

        foreach (var (name, hex) in InkColors)
        {
            var dot = new Ellipse { Width = 14, Height = 14, Stroke = Res("BorderStrongBrush"), StrokeThickness = 1 };
            if (hex is null) dot.SetResourceReference(Shape.FillProperty, "TextPrimaryBrush");
            else dot.Fill = AccentPalette.Brush(hex, Res("TextPrimaryBrush"));
            var b = new ToggleButton
            {
                Content = dot, Style = (Style)FindResource("ToolbarToggleButton"), Margin = new Thickness(0),
                Padding = new Thickness(5), MinWidth = 26, MinHeight = 30, ToolTip = name
            };
            var captured = hex;
            b.Click += (_, _) => PickInkColor(captured);
            _swatches.Add((b, hex));
            row.Children.Add(b);
        }
        row.Children.Add(PaletteSeparator());

        for (var i = 0; i < 3; i++)
        {
            var dot = new Ellipse { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            dot.SetResourceReference(Shape.FillProperty, "TextPrimaryBrush");
            var b = new ToggleButton
            {
                Content = new Grid { Width = 16, Height = 16, Children = { dot } },
                Style = (Style)FindResource("ToolbarToggleButton"), Margin = new Thickness(0),
                Padding = new Thickness(5), MinWidth = 28, MinHeight = 30,
                ToolTip = i switch { 0 => "Fine", 1 => "Medium", _ => "Bold" }
            };
            var index = i;
            b.Click += (_, _) => PickInkSize(index);
            _sizeButtons.Add((b, dot));
            row.Children.Add(b);
        }
        row.Children.Add(PaletteSeparator());

        Button Icon(string glyph, string tip, Action click)
        {
            var b = new Button
            {
                Content = glyph, Style = (Style)FindResource("ToolbarIconButton"), Margin = new Thickness(1),
                Padding = new Thickness(7, 5, 7, 5), FontSize = 14, ToolTip = tip
            };
            b.Click += (_, _) => { click(); Viewport.Focus(); };
            row.Children.Add(b);
            return b;
        }

        _undoButton = Icon("", "Undo (Ctrl+Z)", Undo);
        _redoButton = Icon("", "Redo (Ctrl+Y)", Redo);
        _deleteInkButton = Icon("", "Delete the selected lines (Delete)", DeleteSelectedStrokes);
        var more = Icon("", "More", () => { });
        more.Click += (_, _) => ShowInkMenu(null, more);

        var done = new Button
        {
            Content = "Done", Style = (Style)FindResource("SubtleButton"), Padding = new Thickness(12, 5, 12, 5),
            MinHeight = 0, Margin = new Thickness(6, 1, 1, 1), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Stop drawing (Esc)"
        };
        done.Click += (_, _) => SetDrawMode(false);
        row.Children.Add(done);
        UpdateInkPalette();
    }

    private UIElement PaletteSeparator()
    {
        var line = new Border { Width = 1, Margin = new Thickness(5, 7, 5, 7) };
        line.SetResourceReference(Border.BackgroundProperty, "BorderBrush");
        return line;
    }

    /// <summary>Shows the current tool, colour and width as picked, and what undo, redo and delete can do.</summary>
    private void UpdateInkPalette()
    {
        if (_undoButton is null) return;
        foreach (var (tool, b) in _toolButtons) b.IsChecked = tool == _inkTool;

        // The swatches show the colour the selection has (when it has one), else the tool's own.
        string? shownColor;
        var sel = SelectedStrokes();
        if (sel.Count > 0) shownColor = sel.Select(s => s.Color?.ToUpperInvariant()).Distinct().Count() == 1 ? sel[0].Color : "\0mixed";
        else shownColor = _inkTool == InkTool.Highlighter ? InkSettings.PlanInkHighlighterColor : InkSettings.PlanInkPenColor;
        var colorApplies = _inkTool is not InkTool.Eraser || sel.Count > 0;
        foreach (var (b, hex) in _swatches)
            b.IsChecked = colorApplies && string.Equals(hex, shownColor, StringComparison.OrdinalIgnoreCase);

        var (widths, index) = _inkTool switch
        {
            InkTool.Highlighter => (HighlighterWidths, InkSettings.PlanInkHighlighterSize),
            InkTool.Eraser => (EraserWidths, InkSettings.PlanInkEraserSize),
            _ => (PenWidths, InkSettings.PlanInkPenSize)
        };
        index = SizeIndex(index);
        for (var i = 0; i < _sizeButtons.Count; i++)
        {
            var (b, dot) = _sizeButtons[i];
            b.IsChecked = i == index && sel.Count == 0;
            dot.Width = dot.Height = i switch { 0 => 4, 1 => 7, _ => 11 }; // fine, medium, bold at a glance
            b.ToolTip = $"{(i switch { 0 => "Fine", 1 => "Medium", _ => "Bold" })} ({widths[i]:0} px)";
        }

        _undoButton.IsEnabled = _histIndex > 0 && _ink is null;
        _redoButton!.IsEnabled = _histIndex < _history.Count - 1 && _ink is null;
        _deleteInkButton!.Visibility = sel.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Tells the pen's live renderer where the palette is, so a pen tap on a palette button
    /// never starts a line underneath it.</summary>
    private void UpdatePaletteKeepOut()
    {
        if (InkPalette.Visibility != Visibility.Visible || !InkPalette.IsLoaded || !InkSurface.IsLoaded)
        {
            InkSurface.Renderer.SetKeepOut([]);
            return;
        }
        try
        {
            var r = InkPalette.TransformToVisual(InkSurface).TransformBounds(new Rect(InkPalette.RenderSize));
            r.Inflate(4, 4);
            InkSurface.Renderer.SetKeepOut([r]);
        }
        catch (InvalidOperationException) { InkSurface.Renderer.SetKeepOut([]); }
    }

    /// <summary>The drawing menu: right click or a tap with the pen's side button while drawing, and the
    /// palette's More button.</summary>
    private void ShowInkMenu(Point? at, UIElement? anchor = null)
    {
        var menu = new ContextMenu { PlacementTarget = anchor ?? InkSurface };
        if (anchor is null && at is { } p)
        {
            menu.Placement = PlacementMode.RelativePoint;
            menu.HorizontalOffset = p.X;
            menu.VerticalOffset = p.Y;
        }

        var sel = _inkSelected.Count;
        if (sel > 0)
        {
            var what = sel == 1 ? "line" : $"{sel} lines";
            menu.Items.Add(Item($"Delete {what}", DeleteSelectedStrokes, gesture: "Del"));
            menu.Items.Add(Item($"Duplicate {what}", () => PasteStrokes(SelectedStrokes()), gesture: "Ctrl+D"));
            menu.Items.Add(Item($"Copy {what}", CopySelectedStrokes, gesture: "Ctrl+C"));
            menu.Items.Add(new Separator());
        }
        if (_inkClipboard is { Count: > 0 } clip)
        {
            menu.Items.Add(Item($"Paste {(clip.Count == 1 ? "line" : $"{clip.Count} lines")}", () => PasteStrokes(clip), gesture: "Ctrl+V"));
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(Radio("Pen", _inkTool == InkTool.Pen, () => SetInkTool(InkTool.Pen)));
        menu.Items.Add(Radio("Highlighter", _inkTool == InkTool.Highlighter, () => SetInkTool(InkTool.Highlighter)));
        menu.Items.Add(Radio("Eraser", _inkTool == InkTool.Eraser, () => SetInkTool(InkTool.Eraser)));
        menu.Items.Add(Radio("Select", _inkTool == InkTool.Select, () => SetInkTool(InkTool.Select)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Radio("Eraser takes whole lines", InkSettings.PlanInkEraseWholeStrokes, () =>
        {
            InkSettings.PlanInkEraseWholeStrokes = !InkSettings.PlanInkEraseWholeStrokes;
            SaveInkSettings();
        }));
        menu.Items.Add(Radio("Draw with a finger too", InkSettings.PlanInkTouchDraws, () =>
        {
            InkSettings.PlanInkTouchDraws = !InkSettings.PlanInkTouchDraws;
            SaveInkSettings();
            PlanStatus.Text = InkSettings.PlanInkTouchDraws
                ? "A finger draws now; two fingers still move and zoom the board."
                : "A finger moves the board again; two zoom it.";
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Select all lines", SelectAllStrokes, gesture: "Ctrl+A", enabled: _board?.HasDrawing == true));
        menu.Items.Add(Item("Hide drawings", ToggleInkHidden));
        menu.Items.Add(Item("Clear drawing...", () => _ = ClearDrawingAsync(), enabled: _board?.HasDrawing == true));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Stop drawing", () => SetDrawMode(false), gesture: "Esc"));
        menu.IsOpen = true;
    }

    // ── selection visual ────────────────────────────────────────────────────────

    /// <summary>The selected strokes, drawn over an outline in the accent colour, inside a dashed box.</summary>
    private sealed class InkSelectionVisual : FrameworkElement
    {
        private List<(Geometry Shape, Brush Fill)> _items = new();
        private Rect _box = Rect.Empty;
        private Brush _accent = Brushes.Transparent;
        private double _zoom = 1;

        public InkSelectionVisual() => IsHitTestVisible = false;

        public void Show(List<(Geometry, Brush)> items, Rect box, Brush accent, double zoom)
        {
            _items = items;
            _box = box;
            _accent = accent;
            _zoom = Math.Max(0.05, zoom);
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (_items.Count == 0 || _box.IsEmpty) return;
            var halo = new Pen(_accent, 3 / _zoom) { LineJoin = PenLineJoin.Round };
            halo.Freeze();
            foreach (var (shape, _) in _items) dc.DrawGeometry(null, halo, shape);
            foreach (var (shape, fill) in _items) dc.DrawGeometry(fill, null, shape);
            var box = _box;
            box.Inflate(6 / _zoom, 6 / _zoom);
            var dash = new Pen(_accent, 1 / _zoom) { DashStyle = new DashStyle([4, 3], 0) };
            dash.Freeze();
            dc.DrawRectangle(null, dash, box);
        }
    }
}

/// <summary>Plain geometry on drawn points, kept apart from the view so tests can reach it.</summary>
internal static class PlanInkGeometry
{
    /// <summary>Drops points the line doesn't need: Ramer-Douglas-Peucker on position, keeping any point
    /// whose pressure strays from the straight run by more than <paramref name="pressureTolerance"/>.</summary>
    public static List<InkPoint> Simplify(IReadOnlyList<InkPoint> points, double tolerance, float pressureTolerance)
    {
        if (points.Count <= 2) return points.ToList();
        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, points.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            if (b - a < 2) continue;
            var worst = -1;
            var worstScore = 1.0;
            for (var i = a + 1; i < b; i++)
            {
                var t = (double)(i - a) / (b - a);
                var off = Distance(points[i], points[a], points[b]) / Math.Max(1e-9, tolerance);
                var pressure = points[a].Pressure + (points[b].Pressure - points[a].Pressure) * t;
                var poff = Math.Abs(points[i].Pressure - pressure) / Math.Max(1e-6, pressureTolerance);
                var score = Math.Max(off, poff);
                if (score > worstScore) { worstScore = score; worst = i; }
            }
            if (worst < 0) continue;
            keep[worst] = true;
            stack.Push((a, worst));
            stack.Push((worst, b));
        }
        var result = new List<InkPoint>();
        for (var i = 0; i < points.Count; i++)
            if (keep[i]) result.Add(points[i]);
        return result;
    }

    /// <summary>How much of a line's length lies inside a closed loop (0 to 1), measured every
    /// <paramref name="step"/> along it. A single point is all in or all out.</summary>
    public static double FractionInside(IReadOnlyList<Point> line, IReadOnlyList<Point> loop, double step)
    {
        if (line.Count == 0 || loop.Count < 3) return 0;
        if (line.Count == 1) return Inside(loop, line[0]) ? 1 : 0;
        step = Math.Max(1e-3, step);
        double total = 0, inside = 0;
        for (var i = 1; i < line.Count; i++)
        {
            var a = line[i - 1];
            var d = line[i] - a;
            var len = d.Length;
            if (len <= 0) continue;
            var n = Math.Clamp((int)Math.Ceiling(len / step), 1, 10_000);
            for (var k = 0; k < n; k++)
            {
                total += len / n;
                if (Inside(loop, a + d * ((k + 0.5) / n))) inside += len / n;
            }
        }
        return total <= 0 ? (Inside(loop, line[0]) ? 1 : 0) : inside / total;
    }

    /// <summary>Even-odd point in polygon (the loop closes itself).</summary>
    public static bool Inside(IReadOnlyList<Point> loop, Point p)
    {
        var inside = false;
        for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
        {
            var a = loop[i];
            var b = loop[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    private static double Distance(InkPoint p, InkPoint a, InkPoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        var len2 = dx * dx + dy * dy;
        if (len2 < 1e-12) return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));
        var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        double x = a.X + t * dx, y = a.Y + t * dy;
        return Math.Sqrt((p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y));
    }
}
