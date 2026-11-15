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

    /// <summary>Fingers down on the drawing surface, by stylus device id, where they are now.</summary>
    private readonly Dictionary<int, Point> _touches = new();
    private Pinch? _pinch;

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
    }

    private sealed record Pinch(int A, int B, Point A0, Point B0, double Zoom, double PanX, double PanY);

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

        // Two fingers zoom the board while drawing is off too (one still pans it, through the mouse).
        Viewport.PreviewTouchDown += OnViewportTouchDown;
        Viewport.PreviewTouchMove += OnViewportTouchMove;
        Viewport.PreviewTouchUp += OnViewportTouchUp;
        Viewport.LostTouchCapture += OnViewportLostTouch;

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

        Loaded += (_, _) => RefreshTouchTablets();
    }

    private void RefreshTouchTablets()
    {
        try
        {
            var ids = new List<int>();
            foreach (TabletDevice t in Tablet.TabletDevices)
                if (t.Type == TabletDeviceType.Touch) ids.Add(t.Id);
            InkSurface.Renderer.SetTouchTablets(ids.ToArray());
        }
        catch { /* no tablet stack: nothing draws through the renderer anyway */ }
    }

    // ── draw mode ───────────────────────────────────────────────────────────────

    private void OnToggleDraw(object sender, RoutedEventArgs e) => SetDrawMode(!_drawMode);

    /// <summary>Turns drawing on or off. While it is on, presses on the board draw (or erase or select)
    /// instead of moving cards.</summary>
    private void SetDrawMode(bool on)
    {
        if (on == _drawMode) return;
        EndInkGesture(cancel: true);
        if (on)
        {
            if (ArrowModeActive) SetArrowTool(false);
            ClearSelection();
            if (InkHidden)
            {
                InkSettings.PlanInkHidden = false; // drawing on hidden lines would be drawing blind
                SaveInkSettings();
            }
            RefreshTouchTablets();
        }
        else
        {
            ClearInkSelection();
            HidePenDot();
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
        else if (_ink is { Kind: InkGestureKind.Pan }) cursor = Cursors.SizeAll;
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
    private void UpdatePenInking(StylusDevice device, Point at)
    {
        if (_penLookStale) ApplyPenLook();
        InkSurface.Renderer.Inking = _drawMode && _ink is null
            && _inkTool is InkTool.Pen or InkTool.Highlighter
            && !device.Inverted && !BarrelDown(device)
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

    // ── pen input ───────────────────────────────────────────────────────────────
    // Every stylus event on the surface is marked handled: WPF then makes no mouse event of it, so a
    // stroke can never also click, drag or pan what is under it.

    private void OnInkStylusDown(object sender, StylusDownEventArgs e)
    {
        e.Handled = true;
        HidePenDot();
        var device = e.StylusDevice;
        var at = e.GetPosition(InkSurface);
        if (DeviceOf(device) == InkDevice.Touch)
        {
            FingerDown(device, at, e.GetStylusPoints(InkSurface));
            return;
        }

        var renderer = InkSurface.Renderer;
        var rendererDrew = renderer.Drawing(device.Id);
        if (_ink is not null || _pinch is not null)
        {
            if (rendererDrew) renderer.Abort(); // one gesture at a time
            return;
        }

        InkTool tool;
        if (device.Inverted) tool = InkTool.Eraser;              // the back of the pen
        else if (!_drawMode)
        {
            // Only the back of the pen gets here with drawing off; the tip after a flip just stops it.
            if (rendererDrew) renderer.Abort();
            _eraserHover = false;
            UpdateInkSurface();
            return;
        }
        else if (BarrelDown(device)) tool = _inkTool == InkTool.Eraser ? InkTool.Eraser : InkTool.Select;
        else tool = _inkTool;

        BeginInkGesture(tool, InkDevice.Pen, device.Id, at, e.GetStylusPoints(InkSurface));
        if (_ink is { Kind: InkGestureKind.Draw } g)
        {
            // The renderer decided from the hover; if it missed this one, draw it here instead.
            if (!rendererDrew) ShowWetLine(g);
        }
        else if (rendererDrew) renderer.Abort();
        if (_ink is not null) device.Capture(InkSurface);
    }

    private void OnInkStylusMove(object sender, StylusEventArgs e)
    {
        e.Handled = true;
        var device = e.StylusDevice;
        if (DeviceOf(device) == InkDevice.Touch)
        {
            FingerMove(device, e.GetPosition(InkSurface), e.GetStylusPoints(InkSurface));
            return;
        }
        if (_ink is { Device: InkDevice.Pen } g && g.DeviceId == device.Id)
            InkGestureMove(e.GetStylusPoints(InkSurface));
    }

    private void OnInkStylusUp(object sender, StylusEventArgs e)
    {
        e.Handled = true;
        var device = e.StylusDevice;
        if (DeviceOf(device) == InkDevice.Touch)
        {
            FingerUp(device, e.GetPosition(InkSurface), e.GetStylusPoints(InkSurface));
            return;
        }
        if (_ink is { Device: InkDevice.Pen } g && g.DeviceId == device.Id)
        {
            InkGestureMove(e.GetStylusPoints(InkSurface));
            EndInkGesture(cancel: false);
        }
        // Also after a stroke Esc cancelled midway: the pen kept the surface captured until now.
        if (ReferenceEquals(device.Captured, InkSurface)) device.Capture(null);
    }

    private void OnInkStylusHover(object sender, StylusEventArgs e)
    {
        e.Handled = true; // no mouse moves from a hovering pen: they would light up the cards underneath
        var device = e.StylusDevice;
        if (DeviceOf(device) == InkDevice.Touch) return;
        var at = e.GetPosition(InkSurface);
        UpdatePenInking(device, at);
        if (_ink is null)
        {
            if (device.Inverted || (_drawMode && _inkTool == InkTool.Eraser)) ShowEraserRing(at);
            else HideEraserRing();
            if (device.Inverted) HidePenDot();
            else ShowPenDot(at);
            UpdateInkCursor(at);
        }
    }

    private void OnInkStylusOutOfRange(object sender, StylusEventArgs e)
    {
        e.Handled = true;
        HidePenDot();
        if (_ink is null) HideEraserRing();
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
        if (_ink is { Device: InkDevice.Pen }) EndInkGesture(cancel: false);
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

    private void OnInkMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (e.StylusDevice is not null) return; // a pen or finger: already taken as stylus input
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
        if (_ink is not null) return;
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
        if (e.ChangedButton == MouseButton.Right && _drawMode) ShowInkMenu(at);
    }

    private void OnInkLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_ink is { Device: InkDevice.Mouse }) EndInkGesture(cancel: false);
    }

    private static StylusPointCollection MousePoints(Point at) => new([new StylusPoint(at.X, at.Y, 0.5f)]);

    // ── touch ───────────────────────────────────────────────────────────────────
    // While drawing, one finger moves the board (or draws, if asked) and two zoom it.

    private void FingerDown(StylusDevice device, Point at, StylusPointCollection points)
    {
        if (_ink is { Device: InkDevice.Pen or InkDevice.Mouse }) return; // a palm while the pen draws
        _touches[device.Id] = at;
        device.Capture(InkSurface);
        if (_touches.Count == 1)
        {
            if (_drawMode && InkSettings.PlanInkTouchDraws)
            {
                BeginInkGesture(_inkTool, InkDevice.Touch, device.Id, at, TouchPoints(points));
                if (_ink is { Kind: InkGestureKind.Draw } g) ShowWetLine(g);
            }
            else BeginPan(InkDevice.Touch, device.Id, at);
        }
        else if (_touches.Count == 2)
        {
            // A second finger turns whatever the first began into a pinch: a line it started is taken back.
            EndInkGesture(cancel: true);
            StartPinch();
        }
    }

    private void FingerMove(StylusDevice device, Point at, StylusPointCollection points)
    {
        if (!_touches.ContainsKey(device.Id)) return;
        _touches[device.Id] = at;
        if (_pinch is not null) UpdatePinch();
        else if (_ink is { Device: InkDevice.Touch } g && g.DeviceId == device.Id)
        {
            if (g.Kind == InkGestureKind.Pan) PanTo(at);
            else InkGestureMove(TouchPoints(points));
        }
    }

    private void FingerUp(StylusDevice device, Point at, StylusPointCollection points)
    {
        if (!_touches.Remove(device.Id)) return;
        device.Capture(null);
        if (_pinch is not null)
        {
            if (_touches.Count >= 2) return;
            _pinch = null;
            StashViewport();
            // The finger still down carries on moving the board from where it is.
            if (_touches.Count == 1)
            {
                var (id, p) = _touches.First();
                BeginPan(InkDevice.Touch, id, p);
            }
            return;
        }
        if (_ink is { Device: InkDevice.Touch } g && g.DeviceId == device.Id)
        {
            if (g.Kind != InkGestureKind.Pan) InkGestureMove(TouchPoints(points));
            EndInkGesture(cancel: false);
        }
    }

    /// <summary>A finger presses evenly: its points draw at the stroke's own width.</summary>
    private static StylusPointCollection TouchPoints(StylusPointCollection points)
    {
        var flat = new StylusPointCollection(points.Count);
        foreach (var p in points) flat.Add(new StylusPoint(p.X, p.Y, 0.5f));
        return flat;
    }

    private void StartPinch()
    {
        if (_touches.Count < 2) return;
        var pair = _touches.Take(2).ToArray();
        _pinch = new Pinch(pair[0].Key, pair[1].Key, pair[0].Value, pair[1].Value, Zoom, PanT.X, PanT.Y);
    }

    private void UpdatePinch()
    {
        if (_pinch is not { } p || !_touches.TryGetValue(p.A, out var a) || !_touches.TryGetValue(p.B, out var b)) return;
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

    // Outside drawing a finger is the mouse (WPF promotes the first one), which pans the empty board. A
    // second finger landing while the first is on the empty board turns that into a pinch. (The first
    // finger's mouse press can arrive late, after Windows has ruled out a press-and-hold; the board's
    // mouse handlers ignore it while a pinch is on.)
    private readonly Dictionary<int, Point> _viewTouches = new();
    private bool _firstTouchOnBoard;

    private void OnViewportTouchDown(object? sender, TouchEventArgs e)
    {
        if (_drawMode) return;
        if (_viewTouches.Count == 0) _firstTouchOnBoard = ReferenceEquals(e.OriginalSource, Viewport);
        _viewTouches[e.TouchDevice.Id] = e.GetTouchPoint(InkSurface).Position;
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
        _pinch = new Pinch(pair[0].Key, pair[1].Key, pair[0].Value, pair[1].Value, Zoom, PanT.X, PanT.Y);
        e.TouchDevice.Capture(Viewport);
        e.Handled = true;
    }

    private void OnViewportTouchMove(object? sender, TouchEventArgs e)
    {
        if (_drawMode || !_viewTouches.ContainsKey(e.TouchDevice.Id)) return;
        _viewTouches[e.TouchDevice.Id] = e.GetTouchPoint(InkSurface).Position;
        if (_pinch is not { } p) return;
        if (_viewTouches.TryGetValue(p.A, out var a) && _viewTouches.TryGetValue(p.B, out var b))
        {
            _touches[p.A] = a;
            _touches[p.B] = b;
            UpdatePinch();
        }
        e.Handled = e.TouchDevice.Id != p.A; // the first finger is the mouse: leave it its own events
    }

    private void OnViewportTouchUp(object? sender, TouchEventArgs e)
    {
        if (!_viewTouches.Remove(e.TouchDevice.Id)) return;
        if (_pinch is { } p && !_drawMode && (e.TouchDevice.Id == p.A || e.TouchDevice.Id == p.B))
        {
            _pinch = null;
            _touches.Clear();
            StashViewport();
        }
    }

    private void OnViewportLostTouch(object? sender, TouchEventArgs e) => _viewTouches.Remove(e.TouchDevice.Id);

    // ── gestures ────────────────────────────────────────────────────────────────

    private void BeginInkGesture(InkTool tool, InkDevice device, int deviceId, Point at, StylusPointCollection points)
    {
        if (_board is null) return;
        var board = ToBoard(at);
        var g = new InkGesture { Device = device, DeviceId = deviceId, StartScreen = at, LastBoard = board };

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
                    if (!cancel) FinishStroke(g);
                    if (g.Wet is not null) InkSurface.Wet.Strokes.Remove(g.Wet);
                    if (cancel && g.Device == InkDevice.Pen) InkSurface.Renderer.Abort();
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
            PanX = PanT.X, PanY = PanT.Y
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
