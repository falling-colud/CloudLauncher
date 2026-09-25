using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CloudLauncher.Services;

namespace CloudLauncher.Controls;

/// <summary>What a <see cref="SlateBorder"/> is, for the Vanilla skin, which draws buttons as
/// bevelled stone and everything else flat.</summary>
public enum SlateRole
{
    Surface,
    Button,
}

/// <summary>
/// A <see cref="Border"/> that draws Slate's shapes while the Slate style is on: pixel-stepped
/// corners, a hard two-pixel drop shadow, and the Vanilla skin's bevelled buttons. In the Classic
/// style <see cref="OnRender"/> defers to <see cref="Border"/>.
/// </summary>
/// <remarks>
/// <para>A subclass, so every template, style and trigger that targets Border keeps working; only
/// the painting changes.</para>
/// <para>Corners follow Slate's SlateDraw.pixelRound/outline: radius r is a staircase of r
/// one-pixel steps. Authored radii map onto the user's step count (radii under 4 get at most two
/// steps), and square elements with half-size corners become pixel circles. Steps are whole
/// device pixels with guidelines, so they stay sharp at any scale.</para>
/// <para>The look is not a render-affecting property, so <see cref="ThemeService"/> redraws open
/// windows after a change, and elements that were off screen redraw on <c>Loaded</c> when
/// <see cref="LookState.Version"/> has moved on.</para>
/// </remarks>
public class SlateBorder : Border
{
    // Attached rather than plain properties so Border-targeted styles (Card, HoverCard) and
    // templates can set them; WPF rejects style setters for properties the target type does not own.

    public static readonly DependencyProperty ShadowProperty = DependencyProperty.RegisterAttached(
        "Shadow", typeof(bool), typeof(SlateBorder),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static bool GetShadow(DependencyObject o) => (bool)o.GetValue(ShadowProperty);
    public static void SetShadow(DependencyObject o, bool value) => o.SetValue(ShadowProperty, value);

    /// <summary>Draw Slate's hard two-pixel shadow under this element (Slate style, shadows on).</summary>
    public bool Shadow
    {
        get => (bool)GetValue(ShadowProperty);
        set => SetValue(ShadowProperty, value);
    }

    public static readonly DependencyProperty RoleProperty = DependencyProperty.RegisterAttached(
        "Role", typeof(SlateRole), typeof(SlateBorder),
        new FrameworkPropertyMetadata(SlateRole.Surface, FrameworkPropertyMetadataOptions.AffectsRender));

    public static SlateRole GetRole(DependencyObject o) => (SlateRole)o.GetValue(RoleProperty);
    public static void SetRole(DependencyObject o, SlateRole value) => o.SetValue(RoleProperty, value);

    /// <summary>A button is bevelled on the Vanilla skin; everything else is flat.</summary>
    public SlateRole Role
    {
        get => (SlateRole)GetValue(RoleProperty);
        set => SetValue(RoleProperty, value);
    }

    private int _drawnVersion = -1;

    public SlateBorder()
    {
        Loaded += RedrawIfStale;
    }

    private static void RedrawIfStale(object sender, RoutedEventArgs e)
    {
        var border = (SlateBorder)sender;
        if (border._drawnVersion != LookState.Current.Version) border.InvalidateVisual();
    }

    /// <summary>Redraws every SlateBorder under <paramref name="root"/>.</summary>
    /// <remarks>Called after a look change.</remarks>
    public static void RedrawTree(DependencyObject root)
    {
        if (root is SlateBorder b) b.InvalidateVisual();
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++) RedrawTree(VisualTreeHelper.GetChild(root, i));
    }

    protected override void OnRender(DrawingContext dc)
    {
        var look = LookState.Current;
        _drawnVersion = look.Version;
        if (!look.IsSlate)
        {
            base.OnRender(dc);
            return;
        }

        var w = RenderSize.Width;
        var h = RenderSize.Height;
        if (w <= 0 || h <= 0) return;

        var bt = BorderThickness;
        var uniform = bt.Left == bt.Top && bt.Top == bt.Right && bt.Right == bt.Bottom;
        var cr = CornerRadius;
        var rounded = cr.TopLeft > 0 || cr.TopRight > 0 || cr.BottomRight > 0 || cr.BottomLeft > 0;

        // One step = a whole number of device pixels, about one DIP.
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (scale <= 0) scale = 1;
        var u = Math.Max(1, Math.Round(scale)) / scale;

        var shadow = Shadow && look.Shadows && Background is not null;
        var circle = rounded && IsCircle(w, h, cr);

        // Square, or a side-only border (a divider): nothing to step, so Border draws it, plus the
        // shadow if any. A rounded border with zero steps (radius 0, or the square Vanilla skin)
        // still takes the path below; Border would draw the smooth Classic arcs.
        if (!rounded || !uniform)
        {
            if (shadow) dc.DrawGeometry(look.ShadowBrush, null, ShadowOf(SquareShape(w, h), u));
            base.OnRender(dc);
            if (look.IsVanilla && Role == SlateRole.Button) DrawBevel(dc, w, h, uniform ? bt.Left : 0, u);
            return;
        }

        var t = bt.Left <= 0 ? 0 : Math.Max(1, Math.Round(bt.Left / u));
        var wu = (int)Math.Floor(w / u);
        var hu = (int)Math.Floor(h / u);

        int[] tl, tr, br, bl, itl, itr, ibr, ibl;
        if (circle)
        {
            var d = Math.Min(wu, hu);
            tl = tr = br = bl = CircleProfile(d);
            itl = itr = ibr = ibl = CircleProfile(Math.Max(0, d - 2 * (int)t));
        }
        else
        {
            var max = Math.Min(wu, hu) / 2;
            tl = Staircase(Math.Min(Steps(cr.TopLeft, look.Radius), max));
            tr = Staircase(Math.Min(Steps(cr.TopRight, look.Radius), max));
            br = Staircase(Math.Min(Steps(cr.BottomRight, look.Radius), max));
            bl = Staircase(Math.Min(Steps(cr.BottomLeft, look.Radius), max));
            // Slate's outline is one diagonal pixel per row, which is what the inner edge one step
            // tighter than the outer edge produces.
            itl = Staircase(Math.Max(0, tl.Length - (int)t));
            itr = Staircase(Math.Max(0, tr.Length - (int)t));
            ibr = Staircase(Math.Max(0, br.Length - (int)t));
            ibl = Staircase(Math.Max(0, bl.Length - (int)t));
        }

        var outer = Stepped(0, 0, w, h, tl, tr, br, bl, u);
        var tDip = t * u;
        var inner = t > 0 && w > 2 * tDip && h > 2 * tDip
            ? Stepped(tDip, tDip, w - tDip, h - tDip, itl, itr, ibr, ibl, u)
            : null;

        dc.PushGuidelineSet(Guidelines(w, h, u));
        try
        {
            if (shadow)
            {
                var shifted = outer.Clone();
                shifted.Transform = new TranslateTransform(2 * u, 2 * u);
                dc.DrawGeometry(look.ShadowBrush, null,
                    new CombinedGeometry(GeometryCombineMode.Exclude, shifted, outer));
            }

            if (t > 0 && BorderBrush is { } stroke)
            {
                if (inner is null)
                {
                    dc.DrawGeometry(stroke, null, outer);
                }
                else
                {
                    // Outer and inner as one even-odd geometry gives just the ring, so a translucent
                    // background never shows the border colour through it.
                    var ring = new GeometryGroup { FillRule = FillRule.EvenOdd };
                    ring.Children.Add(outer);
                    ring.Children.Add(inner);
                    dc.DrawGeometry(stroke, null, ring);
                }
            }

            // A shape too small to have an inside is all border; only fill it when there is none.
            if (Background is { } fill && (inner is not null || t == 0 || BorderBrush is null))
                dc.DrawGeometry(fill, null, inner ?? outer);

            if (look.IsVanilla && Role == SlateRole.Button) DrawBevel(dc, w, h, tDip, u);
        }
        finally
        {
            dc.Pop();
        }
    }

    // ── shapes ───────────────────────────────────────────────────────────────

    /// <summary>Square, with every corner at least half the side: a dot, an avatar, a radio.</summary>
    private static bool IsCircle(double w, double h, CornerRadius cr)
    {
        if (Math.Abs(w - h) > 1.5) return false;
        var half = Math.Min(w, h) / 2 - 1;
        return cr.TopLeft >= half && cr.TopRight >= half && cr.BottomRight >= half && cr.BottomLeft >= half;
    }

    /// <summary>Slate uses one radius everywhere. A small authored radius (a thin bar, a tiny chip)
    /// keeps at most two steps so the corners never eat the shape.</summary>
    private static int Steps(double authored, int lookRadius) =>
        authored <= 0.01 ? 0 : authored < 4 ? Math.Min(lookRadius, 2) : lookRadius;

    /// <summary>Row insets for a staircase: row i is inset <paramref name="steps"/> - i.</summary>
    private static int[] Staircase(int steps)
    {
        if (steps <= 0) return [];
        var rows = new int[steps];
        for (var i = 0; i < steps; i++) rows[i] = steps - i;
        return rows;
    }

    private static readonly Dictionary<int, int[]> CircleCache = new();

    /// <summary>Row insets for one quarter of a pixel circle of the given diameter, sampled at each
    /// row's centre so the rows are symmetric and the silhouette has no single-pixel spikes.</summary>
    private static int[] CircleProfile(int diameter)
    {
        if (diameter <= 2) return [];
        lock (CircleCache)
        {
            if (CircleCache.TryGetValue(diameter, out var cached)) return cached;
            var r = diameter / 2.0;
            var rows = new List<int>();
            for (var i = 0; i < (int)Math.Ceiling(r); i++)
            {
                var dy = r - (i + 0.5);
                var inset = (int)Math.Round(r - Math.Sqrt(Math.Max(0, r * r - dy * dy)));
                if (inset <= 0) break;
                rows.Add(inset);
            }
            var result = rows.ToArray();
            CircleCache[diameter] = result;
            return result;
        }
    }

    private static int At(int[] rows, int i) => i < rows.Length ? rows[i] : 0;

    /// <summary>
    /// The outline of a rectangle whose corners follow the given row insets, as one closed figure,
    /// clockwise from the top-left. Each array lists, from the corner's outermost row inward, how
    /// far that row is pulled in from the side, in steps of <paramref name="u"/>.
    /// </summary>
    private static StreamGeometry Stepped(double x0, double y0, double x1, double y1,
        int[] tl, int[] tr, int[] br, int[] bl, double u)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            var last = new Point(x0 + At(tl, 0) * u, y0);
            ctx.BeginFigure(last, isFilled: true, isClosed: true);

            void To(double x, double y)
            {
                if (Math.Abs(x - last.X) < 1e-9 && Math.Abs(y - last.Y) < 1e-9) return;
                last = new Point(x, y);
                ctx.LineTo(last, isStroked: false, isSmoothJoin: false);
            }

            To(x1 - At(tr, 0) * u, y0);
            for (var i = 0; i < tr.Length; i++)
            {
                To(x1 - tr[i] * u, y0 + (i + 1) * u);
                To(x1 - At(tr, i + 1) * u, y0 + (i + 1) * u);
            }

            To(x1, y1 - br.Length * u);
            for (var k = br.Length - 1; k >= 0; k--)
            {
                To(x1 - br[k] * u, y1 - (k + 1) * u);
                To(x1 - br[k] * u, y1 - k * u);
            }

            To(x0 + At(bl, 0) * u, y1);
            for (var k = 0; k < bl.Length; k++)
            {
                To(x0 + bl[k] * u, y1 - (k + 1) * u);
                To(x0 + At(bl, k + 1) * u, y1 - (k + 1) * u);
            }

            To(x0, y0 + tl.Length * u);
            for (var k = tl.Length - 1; k >= 0; k--)
            {
                To(x0 + tl[k] * u, y0 + (k + 1) * u);
                To(x0 + tl[k] * u, y0 + k * u);
            }
        }
        g.Freeze();
        return g;
    }

    private static StreamGeometry SquareShape(double w, double h) =>
        Stepped(0, 0, w, h, [], [], [], [], 1);

    /// <summary>The L-shaped band a two-pixel hard shadow shows below and right of a square shape.</summary>
    private static Geometry ShadowOf(Geometry shape, double u)
    {
        var shifted = shape.Clone();
        shifted.Transform = new TranslateTransform(2 * u, 2 * u);
        return new CombinedGeometry(GeometryCombineMode.Exclude, shifted, shape);
    }

    /// <summary>Guidelines on each step boundary near the edges so steps hit whole pixels.</summary>
    private static GuidelineSet Guidelines(double w, double h, double u)
    {
        var xs = new List<double> { 0, w };
        var ys = new List<double> { 0, h };
        for (var i = 1; i <= LookSettings.MaxRadius + 2; i++)
        {
            xs.Add(i * u); xs.Add(w - i * u);
            ys.Add(i * u); ys.Add(h - i * u);
        }
        return new GuidelineSet(xs.ToArray(), ys.ToArray());
    }

    // ── vanilla bevel ────────────────────────────────────────────────────────

    private static readonly Brush BevelLight = Frozen(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
    private static readonly Brush BevelDark = Frozen(Color.FromArgb(0x66, 0x00, 0x00, 0x00));

    /// <summary>The stone button's bevel inside the black outline: a light top and left edge, a dark
    /// bottom and right edge two pixels deep, the way the vanilla button sprite is drawn.</summary>
    private static void DrawBevel(DrawingContext dc, double w, double h, double t, double u)
    {
        var iw = w - 2 * t;
        var ih = h - 2 * t;
        if (iw <= 3 * u || ih <= 4 * u) return;
        dc.DrawRectangle(BevelLight, null, new Rect(t, t, iw, u));
        dc.DrawRectangle(BevelLight, null, new Rect(t, t + u, u, ih - 3 * u));
        dc.DrawRectangle(BevelDark, null, new Rect(t, h - t - 2 * u, iw, 2 * u));
        dc.DrawRectangle(BevelDark, null, new Rect(w - t - u, t + u, u, ih - 3 * u));
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
