using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace CloudLauncher.Views;

/// <summary>
/// Static vector drawing spread over a big zoomable canvas (dependency lines and arrowheads, plan
/// links, wheel discs), handed to the GPU as cached bitmaps tile by tile instead of being drawn again
/// every frame.
/// </summary>
/// <remarks>
/// <para>WPF re-tessellates every visible geometry on every frame a pan or zoom changes the canvas
/// transform, and thin anti-aliased curves are among the most expensive things it draws. Measured on a
/// 1,500-mod graph with 3,700 dependency lines: 10 fps while panning with the lines drawn live, against
/// 120 fps with them hidden. Cached, the lines are drawn once per zoom level and a frame only places
/// textures.</para>
/// <para>Only the tiles on screen (and one tile around them) exist, so memory follows the viewport, not
/// the canvas. Once a zoom has settled, every tile is exactly <see cref="TilePx"/> device pixels across,
/// clipped inside its own drawing: tile edges then fall on pixel boundaries, so a line crossing from
/// one tile into the next shows no seam. Until the zoom settles the existing bitmaps are scaled.</para>
/// <para>A long curve would be tessellated again for every tile it crosses, so callers split long
/// curves into short pieces (<see cref="AddQuadratic"/>, <see cref="AddCubic"/>). Pieces of one curve
/// in the same tile are drawn as one geometry, which joins them without a mark.</para>
/// <para>Hand it frozen geometry. Items are grouped by their <see cref="Ink"/> instance, so reuse one
/// instance per look.</para>
/// </remarks>
internal sealed class TiledCachedLayer : FrameworkElement
{
    /// <summary>How an item is drawn: its fill and its outline, faded by <c>opacity</c>.</summary>
    /// <remarks>The fade goes into copies of the brushes rather than an opacity pushed around the
    /// drawing: an opacity group is rendered through a surface of its own, and one per tile made a
    /// canvas full of wheel discs take seconds to draw. Where a group's outline covers its own fill the
    /// fill shows through a little; on these faint shapes that doesn't show.</remarks>
    public sealed class Ink
    {
        public Ink(Brush? fill, Pen? pen, double opacity = 1)
        {
            Fill = Faded(fill, opacity);
            if (pen is not null && opacity < 1)
            {
                pen = pen.Clone();
                pen.Brush = Faded(pen.Brush, opacity);
                pen.Freeze();
            }
            Pen = pen;
        }

        public Brush? Fill { get; }
        public Pen? Pen { get; }

        private static Brush? Faded(Brush? brush, double opacity)
        {
            if (brush is null || opacity >= 1) return brush;
            var copy = brush.Clone();
            copy.Opacity *= opacity;
            copy.Freeze();
            return copy;
        }
    }

    /// <summary>A tile's side in device pixels once the zoom has settled. Small enough that the tiles
    /// just off screen cost little memory, big enough that a screenful is tens of tiles.</summary>
    private const double TilePx = 512;

    /// <summary>More tiles than this in view means the zoom has moved far from the tiling (zoomed a long
    /// way out): the tiling is redone at once rather than when the zoom settles.</summary>
    private const int MaxLiveTiles = 120;

    /// <summary>The longest piece <see cref="AddQuadratic"/> and <see cref="AddCubic"/> cut a curve
    /// into, in canvas units.</summary>
    private const double PieceLength = 240;

    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(160);

    private readonly VisualCollection _visuals;
    /// <summary>The tiles near the view; null for one nothing crosses, so it isn't looked at again.</summary>
    private readonly Dictionary<(int Col, int Row), DrawingVisual?> _tiles = new();

    // The content: one geometry, ink and bounds per item.
    private Geometry[] _geos = [];
    private Ink[] _inks = [];
    private Rect[] _bounds = [];
    private Ink[] _inkOrder = [];   // distinct inks, in the order they were first given
    private Rect _extent = Rect.Empty;

    /// <summary>Side of a square of the item index, in canvas units.</summary>
    private const double IndexCell = 512;

    /// <summary>Which items touch each index square, so a tile looks at its neighbourhood rather than at
    /// every item.</summary>
    private readonly Dictionary<(int, int), List<int>> _index = new();

    /// <summary>Per item, the last tile build that took it, so an item in several squares is taken once.</summary>
    private int[] _taken = [];
    private int _stamp;

    // The view.
    private Rect _view = Rect.Empty;
    private double _zoom = 1;
    private double _dpi = 1;

    // The tiling: the zoom it was made for and its tile side in canvas units (0 until the first view).
    private double _tileZoom;
    private double _tile;

    private readonly DispatcherTimer _settle;

    public TiledCachedLayer()
    {
        _visuals = new VisualCollection(this);
        IsHitTestVisible = false;
        _settle = new DispatcherTimer(DispatcherPriority.Background) { Interval = SettleDelay };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            Settle();
        };
        Unloaded += (_, _) => _settle.Stop();
        // Hidden, it keeps no tiles; shown again, it makes the ones in view.
        IsVisibleChanged += (_, _) => { if (IsVisible) Realize(); else ClearTiles(); };
    }

    protected override int VisualChildrenCount => _visuals.Count;

    protected override Visual GetVisualChild(int index) => _visuals[index];

    /// <summary>Nothing here is clickable, and the content spans the canvas, so bounds never reject a
    /// hit test on their own.</summary>
    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) => null;

    protected override GeometryHitTestResult? HitTestCore(GeometryHitTestParameters hitTestParameters) => null;

    // ── content ─────────────────────────────────────────────────────────────

    /// <summary>Replaces what the layer draws. Tiles in view are redrawn straight away.</summary>
    public void SetContent(IReadOnlyList<(Geometry Geometry, Ink Ink)> items)
    {
        _geos = new Geometry[items.Count];
        _inks = new Ink[items.Count];
        _bounds = new Rect[items.Count];
        var order = new List<Ink>();
        var seen = new HashSet<Ink>(ReferenceEqualityComparer.Instance);
        var extent = Rect.Empty;
        for (var i = 0; i < items.Count; i++)
        {
            var (geo, ink) = items[i];
            _geos[i] = geo;
            _inks[i] = ink;
            var b = geo.Bounds;
            if (b.IsEmpty) { _bounds[i] = Rect.Empty; continue; }
            // Half the pen plus a pixel of anti-aliasing, so no stroke is left out of a tile it touches.
            var pad = (ink.Pen?.Thickness ?? 0) / 2 + 1;
            b.Inflate(pad, pad);
            _bounds[i] = b;
            extent.Union(b);
            if (seen.Add(ink)) order.Add(ink);
        }
        _inkOrder = order.ToArray();
        _extent = extent;

        _index.Clear();
        _taken = new int[items.Count];
        _stamp = 0;
        for (var i = 0; i < _bounds.Length; i++)
        {
            var b = _bounds[i];
            if (b.IsEmpty) continue;
            int x0 = (int)Math.Floor(b.Left / IndexCell), x1 = (int)Math.Floor(b.Right / IndexCell);
            int y0 = (int)Math.Floor(b.Top / IndexCell), y1 = (int)Math.Floor(b.Bottom / IndexCell);
            for (var y = y0; y <= y1; y++)
                for (var x = x0; x <= x1; x++)
                {
                    if (!_index.TryGetValue((x, y), out var list)) _index[(x, y)] = list = new List<int>();
                    list.Add(i);
                }
        }

        ClearTiles();
        Realize();
    }

    /// <summary>Adds a quadratic Bézier as pieces no longer than <see cref="PieceLength"/>.</summary>
    public static void AddQuadratic(List<(Geometry, Ink)> items, Point p0, Point c, Point p1, Ink ink)
    {
        var n = Pieces(((c - p0).Length + (p1 - c).Length + (p1 - p0).Length) / 2);
        for (var i = 0; i < n; i++)
        {
            double t0 = (double)i / n, t1 = (double)(i + 1) / n;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(Q(t0, t0), isFilled: false, isClosed: false);
                ctx.QuadraticBezierTo(Q(t0, t1), Q(t1, t1), isStroked: true, isSmoothJoin: true);
            }
            g.Freeze();
            items.Add((g, ink));
        }

        // The curve's blossom: its values give the control points of the part between two parameters.
        Point Q(double u, double v) => new(
            (1 - u) * (1 - v) * p0.X + ((1 - u) * v + u * (1 - v)) * c.X + u * v * p1.X,
            (1 - u) * (1 - v) * p0.Y + ((1 - u) * v + u * (1 - v)) * c.Y + u * v * p1.Y);
    }

    /// <summary>Adds a cubic Bézier as pieces no longer than <see cref="PieceLength"/>.</summary>
    public static void AddCubic(List<(Geometry, Ink)> items, Point p0, Point c1, Point c2, Point p1, Ink ink)
    {
        var n = Pieces(((c1 - p0).Length + (c2 - c1).Length + (p1 - c2).Length + (p1 - p0).Length) / 2);
        for (var i = 0; i < n; i++)
        {
            double t0 = (double)i / n, t1 = (double)(i + 1) / n;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(Q(t0, t0, t0), isFilled: false, isClosed: false);
                ctx.BezierTo(Q(t0, t0, t1), Q(t0, t1, t1), Q(t1, t1, t1), isStroked: true, isSmoothJoin: true);
            }
            g.Freeze();
            items.Add((g, ink));
        }

        // The cubic's blossom: de Casteljau with a different parameter at each level.
        Point Q(double a, double b, double t)
        {
            static Point L(Point x, Point y, double s) => new(x.X + (y.X - x.X) * s, x.Y + (y.Y - x.Y) * s);
            Point m0 = L(p0, c1, a), m1 = L(c1, c2, a), m2 = L(c2, p1, a);
            Point n0 = L(m0, m1, b), n1 = L(m1, m2, b);
            return L(n0, n1, t);
        }
    }

    /// <summary>Adds a dashed circle as its dashes: short straight strokes for a solid pen, a few dozen
    /// to a piece. A dashed pen would have every dash of the whole circle worked out again in every tile
    /// the circle crosses.</summary>
    /// <param name="dash">Length of a dash, in canvas units.</param>
    /// <param name="gap">Length of the gap after it.</param>
    public static void AddDashedCircle(List<(Geometry, Ink)> items, Point centre, double radius, double dash, double gap, Ink ink)
    {
        if (radius <= 0 || dash <= 0) return;
        // A whole number of dashes, so the pattern closes on itself.
        var count = Math.Max(1, (int)Math.Round(2 * Math.PI * radius / (dash + gap)));
        var step = 2 * Math.PI / count;
        var on = step * dash / (dash + gap);
        var perPiece = Math.Max(1, (int)(PieceLength / (dash + gap)));
        for (var first = 0; first < count; first += perPiece)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
                for (var i = first; i < Math.Min(count, first + perPiece); i++)
                {
                    double a0 = i * step, a1 = a0 + on;
                    ctx.BeginFigure(new Point(centre.X + radius * Math.Cos(a0), centre.Y + radius * Math.Sin(a0)), false, false);
                    ctx.LineTo(new Point(centre.X + radius * Math.Cos(a1), centre.Y + radius * Math.Sin(a1)), true, false);
                }
            g.Freeze();
            items.Add((g, ink));
        }
    }

    private static int Pieces(double length) => Math.Clamp((int)Math.Ceiling(length / PieceLength), 1, 400);

    // ── view ────────────────────────────────────────────────────────────────

    /// <summary>Tells the layer which part of the canvas is on screen (in canvas units) and at what
    /// zoom. Cheap enough for every pan step.</summary>
    public void SetView(Rect view, double zoom)
    {
        if (view.IsEmpty || zoom <= 0) return;
        var zoomChanged = Math.Abs(zoom - _zoom) > 1e-9;
        _view = view;
        _zoom = zoom;
        _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;

        if (_tile <= 0) Retile();
        else if (zoomChanged)
        {
            // Zoomed a long way out, the old tiling would put hundreds of small tiles on screen: redo it
            // now. Otherwise the bitmaps are scaled until the zoom settles.
            if (CountInView(_tile) > MaxLiveTiles) Retile();
            _settle.Stop();
            _settle.Start();
        }
        Realize();
    }

    /// <summary>Once a zoom has stopped: tiles of exactly <see cref="TilePx"/> at this zoom.</summary>
    private void Settle()
    {
        if (Math.Abs(_zoom - _tileZoom) > 1e-9) Retile();
        Realize();
    }

    private void Retile()
    {
        _tileZoom = _zoom;
        _tile = TilePx / (_zoom * Math.Max(0.5, _dpi));
        ClearTiles();
    }

    private void ClearTiles()
    {
        _visuals.Clear();
        _tiles.Clear();
    }

    /// <summary>The on-screen area plus a tile around it, as a range of tile indices.</summary>
    private bool Range(double tile, out int c0, out int r0, out int c1, out int r1)
    {
        c0 = r0 = c1 = r1 = 0;
        if (_view.IsEmpty || _extent.IsEmpty || tile <= 0) return false;
        var area = _view;
        area.Inflate(tile, tile);
        area.Intersect(_extent);
        if (area.IsEmpty) return false;
        c0 = (int)Math.Floor(area.Left / tile);
        r0 = (int)Math.Floor(area.Top / tile);
        c1 = (int)Math.Floor(area.Right / tile);
        r1 = (int)Math.Floor(area.Bottom / tile);
        return true;
    }

    private int CountInView(double tile) =>
        Range(tile, out var c0, out var r0, out var c1, out var r1) ? (c1 - c0 + 1) * (r1 - r0 + 1) : 0;

    /// <summary>Makes the tiles near the view and drops the rest.</summary>
    private void Realize()
    {
        if (!IsVisible || !Range(_tile, out var c0, out var r0, out var c1, out var r1))
        {
            ClearTiles();
            return;
        }

        foreach (var key in _tiles.Keys.Where(k => k.Col < c0 || k.Col > c1 || k.Row < r0 || k.Row > r1).ToList())
        {
            if (_tiles[key] is { } gone) _visuals.Remove(gone);
            _tiles.Remove(key);
        }

        for (var r = r0; r <= r1; r++)
            for (var c = c0; c <= c1; c++)
            {
                if (_tiles.ContainsKey((c, r))) continue;
                var visual = BuildTile(c, r);
                _tiles[(c, r)] = visual;
                if (visual is not null) _visuals.Add(visual);
            }
    }

    /// <summary>One tile: the items overlapping it, clipped to it, grouped by ink. Null when nothing
    /// crosses it.</summary>
    /// <remarks>The clip is pushed inside the drawing rather than set on the visual: a visual's own
    /// clip is applied after its cache, which would cache everything the tile's items cover.</remarks>
    private DrawingVisual? BuildTile(int col, int row)
    {
        var rect = new Rect(col * _tile, row * _tile, _tile, _tile);
        Dictionary<Ink, List<int>>? picked = null;
        var stamp = ++_stamp;
        int x0 = (int)Math.Floor(rect.Left / IndexCell), x1 = (int)Math.Floor(rect.Right / IndexCell);
        int y0 = (int)Math.Floor(rect.Top / IndexCell), y1 = (int)Math.Floor(rect.Bottom / IndexCell);
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                if (!_index.TryGetValue((x, y), out var list)) continue;
                foreach (var i in list)
                {
                    if (_taken[i] == stamp || !_bounds[i].IntersectsWith(rect)) continue;
                    _taken[i] = stamp;
                    picked ??= new Dictionary<Ink, List<int>>(ReferenceEqualityComparer.Instance);
                    if (!picked.TryGetValue(_inks[i], out var ids)) picked[_inks[i]] = ids = new List<int>();
                    ids.Add(i);
                }
            }
        if (picked is null) return null;

        // In the order the items were given, so what overlaps draws the same way from tile to tile.
        var groups = new Dictionary<Ink, GeometryGroup>(ReferenceEqualityComparer.Instance);
        foreach (var (ink, ids) in picked)
        {
            ids.Sort();
            var group = new GeometryGroup { FillRule = FillRule.Nonzero };
            foreach (var i in ids) group.Children.Add(_geos[i]);
            groups[ink] = group;
        }

        var clip = new RectangleGeometry(rect);
        clip.Freeze();
        var cache = new BitmapCache { RenderAtScale = _tileZoom, SnapsToDevicePixels = true, EnableClearType = false };
        cache.Freeze();
        var visual = new DrawingVisual { CacheMode = cache };
        using (var dc = visual.RenderOpen())
        {
            dc.PushClip(clip);
            foreach (var ink in _inkOrder)
            {
                if (!groups.TryGetValue(ink, out var group)) continue;
                group.Freeze();
                dc.DrawGeometry(ink.Fill, ink.Pen, group);
            }
            dc.Pop();
        }
        return visual;
    }
}
