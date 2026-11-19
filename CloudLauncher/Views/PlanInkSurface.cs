using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Input.StylusPlugIns;
using System.Windows.Media;

namespace CloudLauncher.Views;

/// <summary>
/// The planning board's drawing surface: a layer over the whole board that takes the pen, the mouse and
/// fingers while drawing (and the back of the pen at any time), and shows the line under the pen as it
/// is drawn. What a press does with the board is up to <see cref="ModPlanView"/>; this only collects it.
/// </summary>
/// <remarks>
/// <para>The line under the pen comes from WPF's <see cref="DynamicRenderer"/>, which draws on a thread
/// of its own as the pen reports, so it keeps up with a fast pen even while the board is busy. It is a
/// stylus plug-in, so only a pen draws through it; the mouse and a finger draw on the UI thread instead
/// (<see cref="Wet"/>), where a finger can still turn into a pinch.</para>
/// <para>Hit-testable only while it is in use (see <see cref="IsHitTestVisible"/>, set by the board), so
/// outside drawing every press goes straight through to the cards.</para>
/// </remarks>
public sealed class PlanInkSurface : Decorator
{
    public PlanInkSurface()
    {
        Wet = new InkPresenter { IsHitTestVisible = false };
        Child = Wet;
        Renderer = new PenRenderer();
        // The renderer's line is a visual of this element's, placed by the presenter like a stroke of its own.
        Wet.AttachVisuals(Renderer.RootVisual, Renderer.DrawingAttributes);
        _attachedAsHighlighter = Renderer.DrawingAttributes.IsHighlighter;
        StylusPlugIns.Add(Renderer);

        // Drawing wants every movement: no press-and-hold right click, no flick gestures, no tap ripples.
        Stylus.SetIsPressAndHoldEnabled(this, false);
        Stylus.SetIsFlicksEnabled(this, false);
        Stylus.SetIsTapFeedbackEnabled(this, false);
        Stylus.SetIsTouchFeedbackEnabled(this, false);
        Focusable = false;
        IsHitTestVisible = false;
    }

    /// <summary>Lines being drawn with the mouse or a finger, in this element's coordinates (the
    /// board's screen space), and the host of the pen renderer's own line.</summary>
    public InkPresenter Wet { get; }

    public PenRenderer Renderer { get; }

    private bool _attachedAsHighlighter;

    /// <summary>The look of the next line the pen draws. Taken between strokes only: a line already
    /// under way keeps the look it started with.</summary>
    public void SetPenLook(DrawingAttributes look)
    {
        Renderer.DrawingAttributes = look;
        // The presenter keeps highlighter lines in a see-through layer of their own, so a change between
        // pen and highlighter moves the renderer's visual across.
        if (look.IsHighlighter == _attachedAsHighlighter) return;
        Wet.DetachVisuals(Renderer.RootVisual);
        Wet.AttachVisuals(Renderer.RootVisual, look);
        _attachedAsHighlighter = look.IsHighlighter;
    }

    /// <summary>The whole surface counts as hit wherever it is, not only where a line is drawn.</summary>
    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
    {
        var p = hitTestParameters.HitPoint;
        return p.X >= 0 && p.Y >= 0 && p.X <= ActualWidth && p.Y <= ActualHeight
            ? new PointHitTestResult(this, p)
            : null;
    }

    /// <summary>
    /// The pen's live line. It draws only when the board says a pen press here starts a line
    /// (<see cref="Inking"/>), only for a pen it knows (<see cref="SetPenTablets"/>), so never for a
    /// finger, and never inside the drawing palette.
    /// </summary>
    /// <remarks>These overrides run on the pen thread, before the UI thread has seen the press, so they
    /// decide from flags the UI thread keeps up to date rather than by asking the board. The pens are a
    /// list of the tablets that may draw rather than of the touch screens that may not: a tablet Windows
    /// adds later (a touch screen back from sleep under a new id) then draws nothing here, instead of a
    /// finger's line that the board, panning meanwhile, never keeps.</remarks>
    public sealed class PenRenderer : DynamicRenderer
    {
        private volatile bool _inking;
        private volatile int[] _penTablets = [];
        private volatile Rect[] _keepOut = [];
        private readonly ConcurrentDictionary<int, byte> _skipped = new();
        private readonly ConcurrentDictionary<int, byte> _drawing = new();

        /// <summary>Whether this renderer is drawing the live line of the press this pen just made. Ask
        /// from the UI thread's StylusDown: the pen thread has always seen that press first.</summary>
        public bool Drawing(int stylusDeviceId) => _drawing.ContainsKey(stylusDeviceId);

        /// <summary>Whether a pen tip pressed now draws a line: drawing is on, the tool is a pen or the
        /// highlighter, and the pen hovering here is the tip with its button up. Kept current by the
        /// board from the pen's hover, which always comes before it touches.</summary>
        public bool Inking
        {
            get => _inking;
            set => _inking = value;
        }

        /// <summary>The pen tablets: only their presses draw through the renderer.</summary>
        public void SetPenTablets(int[] ids) => _penTablets = ids;

        public bool IsPenTablet(int id) => Array.IndexOf(_penTablets, id) >= 0;

        /// <summary>Areas of the surface (the palette) where a press is meant for something on top.</summary>
        public void SetKeepOut(Rect[] areas) => _keepOut = areas;

        /// <summary>Whether a press of this tablet's, first touching at <paramref name="at"/>, gets the live line.</summary>
        public bool StartsLine(int tabletDeviceId, Point? at)
        {
            if (!_inking || !IsPenTablet(tabletDeviceId)) return false;
            if (at is not { } p) return true;
            foreach (var r in _keepOut)
                if (r.Contains(p)) return false;
            return true;
        }

        protected override void OnStylusDown(RawStylusInput rawStylusInput)
        {
            var id = rawStylusInput.StylusDeviceId;
            if (!StartsLine(rawStylusInput.TabletDeviceId, FirstPoint(rawStylusInput)))
            {
                _drawing.TryRemove(id, out _);
                _skipped[id] = 0;
                return;
            }
            _skipped.TryRemove(id, out _);
            _drawing[id] = 0;
            base.OnStylusDown(rawStylusInput);
        }

        protected override void OnStylusMove(RawStylusInput rawStylusInput)
        {
            if (_skipped.ContainsKey(rawStylusInput.StylusDeviceId)) return;
            base.OnStylusMove(rawStylusInput);
        }

        protected override void OnStylusUp(RawStylusInput rawStylusInput)
        {
            // _drawing keeps this press until the pen's next one: a quick tap can be over on the pen thread
            // before the UI thread asks about it.
            if (_skipped.TryRemove(rawStylusInput.StylusDeviceId, out _)) return;
            base.OnStylusUp(rawStylusInput);
        }

        private Point? FirstPoint(RawStylusInput input)
        {
            if (_keepOut.Length == 0) return null;
            var points = input.GetStylusPoints();
            return points.Count == 0 ? null : new Point(points[0].X, points[0].Y);
        }

        /// <summary>Takes back the line the pen is drawing, when the press turned out to be for something
        /// else. Call on the UI thread.</summary>
        public void Abort()
        {
            if (!Enabled) return;
            Enabled = false; // drops every line in progress (DynamicRenderer.OnEnabledChanged)
            Enabled = true;
        }
    }
}
