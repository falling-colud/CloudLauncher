using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>
/// The Graph sub-tab. Lays installed mods out on a zoomable, pannable canvas: clustered by
/// category (grid boxes), by dependency layer (barycenter-ordered to reduce crossings), as
/// dependency circles (each library in the middle of a wheel of the mods that need it), or as a
/// free custom layout where nodes can be dragged and their positions are saved to the pack.
/// Dependency edges are drawn as curved, directional (arrow-headed) links behind the nodes.
/// </summary>
public partial class ModGraphView : UserControl
{
    private const double NodeW = 184;
    private const double NodeH = 58;
    private const double HGap = 30;
    private const double VGap = 22;
    private const double Origin = 24;

    /// <summary>Horizontal space between two content-size bands inside a category cluster, with the
    /// dividing rule down the middle of it.</summary>
    private const double BandDividerGap = 26;

    private static readonly Brush EdgeBrush = Frozen(Color.FromArgb(160, 0x6B, 0x76, 0x88));
    private static readonly Brush ArrowBrush = Frozen(Color.FromArgb(225, 0x93, 0x9E, 0xB2));
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// <summary>The pen every dependency line is stroked with, frozen and shared rather than
    /// allocated per edge.</summary>
    private static readonly Pen EdgePen = FrozenPen(EdgeBrush, 1.5);
    private static Pen FrozenPen(Brush b, double w) { var p = new Pen(b, w); p.Freeze(); return p; }

    /// <summary>How many nodes get their contents built per turn of the dispatcher: a few
    /// milliseconds of work on a slow machine, so input and rendering still get in between.</summary>
    private const int NodeChunk = 250;

    /// <summary>Below this zoom nodes draw as frame plus icon only: the labels would be unreadable
    /// but still cost a glyph-run rasterization per node. A note says the labels are hidden by the
    /// zoom.</summary>
    private const double LabelZoom = 0.42;

    private Guid _packId;
    private IReadOnlyList<PackMod> _mods = Array.Empty<PackMod>();
    private Window? _owner;
    private Action<PackMod>? _onOpenMod;
    private Action? _onReload;
    private Action? _onModsChanged; // lightweight "flags changed" signal (marks other views stale)
    private Action<IReadOnlyList<PackMod>>? _onUpdate;
    private Action<IReadOnlyList<PackMod>>? _onRecheckUpdates;

    private readonly Dictionary<PackMod, Rect> _rect = new();
    private readonly Dictionary<PackMod, GraphNode> _nodeEls = new();

    /// <summary>Every dependency line on the canvas, in one element.</summary>
    /// <remarks>Drawn as frozen <see cref="StreamGeometry"/> rather than shapes per edge, which would
    /// mean thousands of elements on a big pack, and <see cref="RedrawEdges"/> runs on every drag
    /// move.</remarks>
    private EdgeLayer? _edges;

    /// <summary>The wheel discs and guide rings of the Circles layout, drawn in one static layer: a
    /// pack can have hundreds of wheels and nothing moves them.</summary>
    private WheelLayer? _wheels;

    /// <summary>Theme brushes, fonts and glyph metrics resolved once per build and handed to every
    /// node, instead of each node walking the tree for the same five <c>FindResource</c> keys.</summary>
    private NodeTheme? _theme;

    private ModGraph? _graph;
    private bool _userInteracted; // once true, we stop auto-fitting so the user's zoom/pan sticks
    private bool _everLaidOut;

    /// <summary>Bumped by every <see cref="Rebuild"/>. A build in flight compares against it after
    /// each chunk and abandons itself the moment a newer one starts.</summary>
    private int _buildGen;

    /// <summary>True while a chunked build is still filling nodes or drawing lines. Dragging waits for
    /// it, since a drag redraws every edge and would fight the build over the canvas children.</summary>
    private bool _building;

    /// <summary>Nodes placed by the layout whose contents have not been built yet. Replaced (never
    /// cleared in place) on each rebuild, so an abandoned build keeps iterating its own list safely.</summary>
    private List<GraphNode> _pendingNodes = new();

    /// <summary>Current text in the find box, lower-cased; empty means "no find is active".</summary>
    private string _findQuery = "";

    // interaction state
    private bool _panning;
    private Point _panStart;
    private double _panOrigX, _panOrigY;
    private GraphNode? _dragNode;
    private PackMod? _dragMod;
    private Point _dragStart;
    private double _dragOrigLeft, _dragOrigTop;
    private bool _dragMoved;

    // category-cluster drag state (Categories mode only)
    private readonly List<ClusterVisual> _clusters = new();
    private ClusterVisual? _dragCluster;
    private Point _clusterDragStart;
    private double _clusterOrigX, _clusterOrigY;
    private bool _clusterDragMoved;

    /// <summary>One category cluster's visuals, moved together when its header is dragged.</summary>
    private sealed class ClusterVisual
    {
        public string Key = "";
        public Border Box = null!;
        public Border Header = null!;
        public readonly List<GraphNode> Nodes = new();
        /// <summary>Vertical rules between the cluster's content-size bands; they move with it.</summary>
        public readonly List<UIElement> Dividers = new();
        public double X, Y, W, H;
    }

    public ModGraphView()
    {
        InitializeComponent();
        Viewport.SizeChanged += (_, _) => TryFit();
    }

    private bool IsCategoryMode => ClusterBox.SelectedIndex == 0;
    private bool IsCirclesMode => ClusterBox.SelectedIndex == 2;
    private bool IsCustomMode => ClusterBox.SelectedIndex == 3;

    /// <param name="packId">The instance whose document holds this layout, or
    /// <see cref="ModMetadataService.GlobalScope"/> for the Mods page's "All instances" scope. There
    /// the nodes stand for a mod rather than one instance's file, so the per-file actions are dropped
    /// (see <see cref="ShowOptions"/>).</param>
    /// <param name="onUpdate">Installs the newest version of the given mods, using the List view's
    /// implementation.</param>
    /// <param name="onRecheckUpdates">Re-runs the update check for the given mods after something
    /// changed what counts as an update for them (their channel, or the store they follow).</param>
    /// <param name="refit">Fit the new layout to the viewport even if this control has drawn before,
    /// for callers that just changed which mods they show (the Mods page switching scope).</param>
    public void Load(Guid packId, IReadOnlyList<PackMod> mods, Window? owner,
        Action<PackMod>? onOpenMod = null, Action? onReload = null, Action? onModsChanged = null,
        Action<IReadOnlyList<PackMod>>? onUpdate = null, Action<IReadOnlyList<PackMod>>? onRecheckUpdates = null,
        bool refit = false)
    {
        _packId = packId;
        _mods = mods;
        _owner = owner;
        _onOpenMod = onOpenMod;
        _onReload = onReload;
        _onModsChanged = onModsChanged;
        _onUpdate = onUpdate;
        _onRecheckUpdates = onRecheckUpdates;
        RestoreToolbarState();
        // Fit only the first time we lay this pack out; later data refreshes keep the user's view.
        var first = !_everLaidOut;
        _everLaidOut = true;
        Rebuild(refit: first || refit);
    }

    /// <summary>
    /// Puts the toolbar back the way this launcher last left it, falling back to the pack's own
    /// defaults.
    /// </summary>
    /// <remarks>
    /// The pack's <see cref="ModAdvancedSettings"/> sync to collaborators through <c>mods.json</c>, so
    /// they are only defaults; <see cref="AppSettings.GraphShowDependencyLines"/> and
    /// <see cref="AppSettings.GraphClusterMode"/> hold this launcher's view. An unknown stored mode
    /// (from a newer build) falls back to the pack's. <c>_suppressToolbar</c> stops the restore from
    /// saving straight back.
    /// </remarks>
    private void RestoreToolbarState()
    {
        var adv = App.State.ModMetadata.Advanced(_packId);
        var settings = App.State.Settings;
        var mode = Enum.TryParse<ModClusterMode>(settings.GraphClusterMode, ignoreCase: true, out var parsed)
                   && Enum.IsDefined(parsed)
            ? parsed
            : adv.DefaultClusterMode;

        _suppressToolbar = true;
        try
        {
            ShowLines.IsChecked = settings.GraphShowDependencyLines;
            ClusterBox.SelectedIndex = IndexFor(mode);
        }
        finally { _suppressToolbar = false; }
    }

    /// <summary>True while the toolbar is being set from storage rather than by the user.</summary>
    private bool _suppressToolbar;

    // The ComboBox lists the modes in reading order (categories, layers, circles, free), which differs
    // from ModClusterMode's declaration order, so the mapping is spelled out both ways.
    private static int IndexFor(ModClusterMode mode) => mode switch
    {
        ModClusterMode.Dependencies => 1,
        ModClusterMode.Circles => 2,
        ModClusterMode.CustomTree => 3,
        _ => 0
    };

    private static ModClusterMode ModeFor(int index) => index switch
    {
        1 => ModClusterMode.Dependencies,
        2 => ModClusterMode.Circles,
        3 => ModClusterMode.CustomTree,
        _ => ModClusterMode.Category
    };

    /// <summary>Called by the hub as soon as the Graph tab is selected, before the build is scheduled,
    /// so the tab shows a building state instead of the previous pack's canvas.</summary>
    public void ShowBuilding()
    {
        if (!IsInitialized) return;
        SetStatus("Laying out...");
    }

    /// <summary>The one place the status line is written. The zoom note has its own label
    /// (<see cref="ZoomNote"/>) so a transient message can't replace it.</summary>
    private void SetStatus(string text) => GraphStatus.Text = text;

    // ── rebuild / layout ──

    /// <summary>
    /// Re-lays the graph out. Positions and node frames are placed synchronously; the expensive part
    /// (each node's icon, labels, markers and tooltip, then the dependency lines) is filled in
    /// afterwards in chunks by <see cref="FinishBuildAsync"/>, yielding the UI thread between them.
    /// </summary>
    /// <remarks>
    /// A large pack is tens of thousands of visuals, so building it all inline freezes the window. This
    /// way the frames appear at once and fill in over a few hundred milliseconds.
    /// </remarks>
    private void Rebuild(bool refit = true)
    {
        if (!IsInitialized) return;
        var gen = ++_buildGen;   // abandons any build still running for the previous layout
        GraphCanvas.Children.Clear();
        _rect.Clear();
        _nodeEls.Clear();
        _clusters.Clear();
        _pendingNodes = new List<GraphNode>();

        // The two drawn layers go in first. The edge layer is added even when the lines are off, so
        // the checkbox only toggles its visibility.
        var theme = _theme = new NodeTheme(this);
        theme.ShowLabels = ZoomT.ScaleX >= LabelZoom;
        var wheels = _wheels = new WheelLayer(theme);
        Panel.SetZIndex(wheels, -1);     // behind the edges and nodes
        GraphCanvas.Children.Add(wheels);
        var edges = _edges = new EdgeLayer
        {
            Visibility = ShowLines.IsChecked == true ? Visibility.Visible : Visibility.Collapsed
        };
        Panel.SetZIndex(edges, 0);       // above the cluster boxes, below the nodes
        GraphCanvas.Children.Add(edges);

        HintLabel.Text = IsCustomMode
            ? "Drag nodes to arrange · scroll to zoom · drag background to pan"
            : IsCategoryMode
                ? "Drag a category header to move it · right-click it to rename · scroll to zoom"
                : IsCirclesMode
                    ? "Each dependency sits in the middle of the mods that need it · scroll to zoom · drag background to pan"
                    : "Scroll to zoom · drag background to pan";

        if (_mods.Count == 0)
        {
            SetStatus("No mods to graph.");
            ZoomNote.Visibility = Visibility.Collapsed;   // nothing is being hidden from an empty canvas
            GraphCanvas.Width = GraphCanvas.Height = 0;
            _building = false;
            return;
        }

        // Built once, up front: two of the layouts and the edges all use it.
        _graph = ModGraph.Build(_mods);

        var size = ClusterBox.SelectedIndex switch
        {
            1 => LayoutDependencies(),
            2 => LayoutCircles(),
            3 => LayoutCustom(),
            _ => LayoutCategories()
        };

        wheels.Freeze();
        GraphCanvas.Width = Math.Max(size.w, 50);
        GraphCanvas.Height = Math.Max(size.h, 50);

        RefreshCollapseButton();

        // Only recentre on a full (re)layout, never on a mod edit, so the user's view is kept.
        // Fit from the frames, before the contents exist: the extent is already final.
        if (refit) { _userInteracted = false; TryFit(); }

        _ = FinishBuildAsync(gen, _pendingNodes);
    }

    /// <summary>Fills in the placed nodes and then the dependency lines, a chunk per turn of the
    /// dispatcher. Abandons itself as soon as a newer <see cref="Rebuild"/> has started.</summary>
    private async Task FinishBuildAsync(int gen, List<GraphNode> pending)
    {
        _building = true;
        try
        {
            // Yield before the first chunk so the empty frames are on screen before any filling
            // starts.
            if (pending.Count > NodeChunk)
            {
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                if (gen != _buildGen) return;
            }

            for (var i = 0; i < pending.Count; i += NodeChunk)
            {
                var end = Math.Min(i + NodeChunk, pending.Count);
                for (var k = i; k < end; k++) FillNode(pending[k]);

                if (end < pending.Count)
                {
                    SetStatus($"Drawing {end} of {pending.Count} mods...");
                    // Background priority: input, layout and render all outrank an empty callback
                    // queued there, so awaiting one hands the thread back to the window and resumes
                    // only once it has caught up.
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                    if (gen != _buildGen) return;
                }
            }

            // Not chunked: writing the curves into a StreamGeometry is well under a frame even at
            // the node cap.
            var links = DrawEdges();
            // The checkbox may have been flipped while the nodes were filling, so settle the lines
            // on the state it is in now rather than the one the build started with.
            SetLinesVisible(ShowLines.IsChecked == true);

            SetStatus($"{_mods.Count} mod(s)" + (links > 0 ? $"  ·  {links} link(s)" : ""));
            // Re-apply a find typed during the build. Skipped when there is none: ApplyFind writes
            // its own status line, and a fresh build is already in its base state.
            if (_findQuery.Length > 0) ApplyFind();
        }
        finally
        {
            if (gen == _buildGen) _building = false;
        }
    }

    private (double w, double h) LayoutCategories()
    {
        const double innerPad = 12, headerH = 28, clusterGap = 34;
        var groups = new Dictionary<string, List<PackMod>>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in _mods)
        {
            var cat = m.Meta.Categories.FirstOrDefault() ?? "Uncategorized";
            if (!groups.TryGetValue(cat, out var list)) groups[cat] = list = new();
            list.Add(m);
        }

        // Clusters follow the order set on the Categories page; anything not declared there trails
        // after, and "Uncategorized" always sits last.
        var declared = App.State.ModMetadata.Categories(_packId).Select(c => c.Name).ToList();
        int Rank(string key)
        {
            var i = declared.FindIndex(n => string.Equals(n, key, StringComparison.OrdinalIgnoreCase));
            return i < 0 ? int.MaxValue : i;
        }

        var ordered = groups.Keys.Where(k => !string.Equals(k, "Uncategorized", StringComparison.OrdinalIgnoreCase))
            .OrderBy(Rank).ThenBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        if (groups.Keys.Any(k => string.Equals(k, "Uncategorized", StringComparison.OrdinalIgnoreCase)))
            ordered.Add("Uncategorized");

        // Clusters the user has dragged keep their saved spot; the rest flow left-to-right.
        double flowX = Origin, maxRight = 0, maxBottom = 0;
        foreach (var key in ordered)
        {
            var n = groups[key].Count;

            // A folded category keeps its header (and its place in the flow) and nothing else. Its
            // mods are simply not placed, which also drops their dependency lines: DrawEdges only
            // draws between nodes that have a rectangle.
            if (App.State.ModMetadata.IsCategoryCollapsed(_packId, key))
            {
                var collapsedW = Math.Clamp(120 + key.Length * 7.5, 190, 340);
                var collapsedH = headerH + 10;
                double cbx, cby;
                if (App.State.ModMetadata.TryGetCategoryPosition(_packId, key, out var csx, out var csy))
                {
                    cbx = csx; cby = csy;
                }
                else
                {
                    cbx = flowX; cby = Origin;
                    flowX += collapsedW + clusterGap;
                }

                var collapsed = new ClusterVisual { Key = key, X = cbx, Y = cby, W = collapsedW, H = collapsedH };
                collapsed.Box = AddClusterBox(cbx, cby, collapsedW, collapsedH);
                var collapsedTint = AccentPalette.Brush(App.State.ModMetadata.CategoryColor(_packId, key), Res("TextSecondaryBrush"));
                collapsed.Header = AddHeader(key, cbx, cby, collapsedW, headerH, innerPad, collapsed, collapsedTint,
                    memberCount: n, isCollapsed: true);
                _clusters.Add(collapsed);
                maxRight = Math.Max(maxRight, cbx + collapsedW);
                maxBottom = Math.Max(maxBottom, cby + collapsedH);
                continue;
            }

            // Inside a cluster the mods are split into content-size bands, largest on the left, each
            // its own block of columns with a rule between. The meter on each node shows the band.
            var bands = groups[key]
                .GroupBy(z => z.ContentSize)
                .OrderByDescending(g => g.Key)
                .Select(g => g.OrderByDescending(z => z.Priority)
                              .ThenBy(z => z.DisplayName, StringComparer.OrdinalIgnoreCase).ToList())
                .ToList();

            // One shared column height across bands keeps the cluster rectangular instead of ragged.
            var rowsPerCol = Math.Clamp((int)Math.Ceiling(Math.Sqrt(n)), 1, 6);
            var bandCols = bands.Select(b => (int)Math.Ceiling(b.Count / (double)rowsPerCol)).ToList();
            var rows = bands.Max(b => Math.Min(b.Count, rowsPerCol));

            var cw = 0.0;
            for (var b = 0; b < bands.Count; b++)
            {
                cw += bandCols[b] * NodeW + (bandCols[b] - 1) * HGap;
                if (b < bands.Count - 1) cw += BandDividerGap;
            }
            var ch = rows * NodeH + (rows - 1) * VGap;
            var boxW = cw + innerPad * 2;
            var boxH = headerH + ch + innerPad * 2;

            double bx, by;
            if (App.State.ModMetadata.TryGetCategoryPosition(_packId, key, out var sx, out var sy))
            {
                bx = sx; by = sy;
            }
            else
            {
                bx = flowX; by = Origin;
                flowX += boxW + clusterGap;
            }

            var cv = new ClusterVisual { Key = key, X = bx, Y = by, W = boxW, H = boxH };
            cv.Box = AddClusterBox(bx, by, boxW, boxH);
            // The colour set on the Categories page, so a category looks the same wherever it shows up.
            var tint = AccentPalette.Brush(App.State.ModMetadata.CategoryColor(_packId, key), Res("TextSecondaryBrush"));
            cv.Header = AddHeader(key, bx, by, boxW, headerH, innerPad, cv, tint,
                memberCount: n, isCollapsed: false);

            var x = bx + innerPad;
            var top = by + headerH;
            for (var b = 0; b < bands.Count; b++)
            {
                var band = bands[b];
                for (var i = 0; i < band.Count; i++)
                {
                    int c = i / rowsPerCol, r = i % rowsPerCol;   // fill down a column, then across
                    var nx = x + c * (NodeW + HGap);
                    var ny = top + r * (NodeH + VGap);
                    cv.Nodes.Add(Place(band[i], nx, ny));
                }

                x += bandCols[b] * NodeW + (bandCols[b] - 1) * HGap;
                if (b == bands.Count - 1) continue;
                cv.Dividers.Add(AddBandDivider(x + BandDividerGap / 2, top, ch));
                x += BandDividerGap;
            }

            _clusters.Add(cv);
            maxRight = Math.Max(maxRight, bx + boxW);
            maxBottom = Math.Max(maxBottom, by + boxH);
        }
        return (maxRight + Origin, maxBottom + Origin);
    }

    private (double w, double h) LayoutDependencies()
    {
        var graph = _graph ??= ModGraph.Build(_mods);
        var depth = new Dictionary<PackMod, int>();
        int Depth(PackMod m, HashSet<PackMod> stack)
        {
            if (depth.TryGetValue(m, out var cached)) return cached;
            if (!stack.Add(m)) return 0;
            var max = 0;
            foreach (var d in graph.DependenciesOf(m)) max = Math.Max(max, 1 + Depth(d, stack));
            stack.Remove(m);
            return depth[m] = max;
        }
        foreach (var m in _mods) Depth(m, new HashSet<PackMod>());
        var maxLayer = _mods.Max(m => depth[m]);

        var order = new Dictionary<int, List<PackMod>>();
        for (var d = 0; d <= maxLayer; d++)
            order[d] = _mods.Where(m => depth[m] == d).OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

        // Barycenter ordering: a few sweeps pulling each node next to its connected neighbours, which
        // keeps dependency lines short. Each sweep scores a layer once into a scratch array, sorts
        // against that, and reindexes only the layer it moved.
        var index = new Dictionary<PackMod, int>(_mods.Count);
        foreach (var lay in order.Values) for (var i = 0; i < lay.Count; i++) index[lay[i]] = i;

        var bary = new Dictionary<PackMod, double>(_mods.Count);
        for (var iter = 0; iter < 4; iter++)
        {
            for (var d = 0; d <= maxLayer; d++)
            {
                var lay = order[d];
                if (lay.Count < 2) continue;
                foreach (var m in lay) bary[m] = Bary(m);
                lay.Sort((a, b) => bary[a].CompareTo(bary[b]));
                for (var i = 0; i < lay.Count; i++) index[lay[i]] = i;
            }
        }

        double Bary(PackMod m)
        {
            double sum = 0; var count = 0;
            foreach (var nb in graph.DependenciesOf(m))
                if (index.TryGetValue(nb, out var ix)) { sum += ix; count++; }
            foreach (var nb in graph.DependentsOf(m))
                if (index.TryGetValue(nb, out var ix)) { sum += ix; count++; }
            return count == 0 ? index[m] : sum / count;
        }

        var maxCols = order.Values.Max(l => l.Count);
        var fullW = maxCols * NodeW + (maxCols - 1) * HGap;
        var rowH = NodeH + VGap * 3;
        for (var d = 0; d <= maxLayer; d++)
        {
            var lay = order[d];
            var y = Origin + (maxLayer - d) * rowH; // roots (deepest) on top
            var layW = lay.Count * NodeW + (lay.Count - 1) * HGap;
            var startX = Origin + (fullW - layW) / 2;
            for (var i = 0; i < lay.Count; i++)
                Place(lay[i], startX + i * (NodeW + HGap), y);
        }
        return (Origin * 2 + fullW, Origin * 2 + (maxLayer + 1) * rowH);
    }

    /// <summary>
    /// Radial dependency clusters: every mod that something needs is the hub of a wheel, with the mods
    /// that need it on rings around it.
    /// </summary>
    /// <remarks>
    /// A mod needing several hubs goes in the wheel with the most dependents; its other lines cross
    /// wheels. Hubs never sit on another hub's ring, and unrelated mods go in a plain block at the end.
    /// Wheels are shelf-packed into a roughly square area so everything fits the viewport.
    /// </remarks>
    private (double w, double h) LayoutCircles()
    {
        const double slot = NodeW + 16;      // arc length reserved per node on a ring
        const double ringGap = NodeW + 20;   // rings must clear a node's width, since nodes are wide and axis-aligned
        // Ring nodes at 3 and 9 o'clock sit beside the hub, so the first ring must clear a full
        // node width (184) plus a margin.
        const double firstRadius = 205;
        const double wheelPad = 26;
        const double wheelGap = 46;

        var graph = _graph ??= ModGraph.Build(_mods);
        var hubs = _mods.Where(m => graph.DependentsOf(m).Count > 0)
            .OrderByDescending(m => graph.DependentsOf(m).Count)
            .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var hubSet = new HashSet<PackMod>(hubs);
        var assigned = new HashSet<PackMod>();

        // Build the wheels: hub + rings of members, radius growing with the member count.
        var wheels = new List<(PackMod hub, List<List<PackMod>> rings, double radius)>();
        foreach (var hub in hubs)
        {
            var members = graph.DependentsOf(hub)
                .Where(d => !hubSet.Contains(d) && !assigned.Contains(d))
                .OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var m in members) assigned.Add(m);

            var rings = new List<List<PackMod>>();
            var r = firstRadius;
            var remaining = members;
            while (remaining.Count > 0)
            {
                var cap = Math.Max(3, (int)Math.Floor(2 * Math.PI * r / slot));
                rings.Add(remaining.Take(cap).ToList());
                remaining = remaining.Skip(cap).ToList();
                r += ringGap;
            }
            var outer = rings.Count == 0 ? 0 : firstRadius + (rings.Count - 1) * ringGap;
            // Bounding radius: the outermost ring plus half a node so no node pokes out of the disc.
            var radius = rings.Count == 0 ? NodeW / 2 + 8 : outer + NodeW / 2 + wheelPad;
            wheels.Add((hub, rings, radius));
        }

        // Shelf-pack the wheels, biggest first, into a roughly square area.
        var totalArea = wheels.Sum(w => 4 * w.radius * w.radius);
        var largest = wheels.Count == 0 ? 0 : wheels.Max(w => 2 * w.radius);
        var rowLimit = Math.Max(largest, Math.Sqrt(totalArea) * 1.15);
        double x = Origin, y = Origin, rowH = 0, maxRight = 0, maxBottom = 0;
        foreach (var (hub, rings, radius) in wheels.OrderByDescending(w => w.radius))
        {
            var d = 2 * radius;
            if (x > Origin && x + d > Origin + rowLimit)
            {
                x = Origin;
                y += rowH + wheelGap;
                rowH = 0;
            }
            var cx = x + radius;
            var cy = y + radius;
            AddWheelDisc(cx, cy, radius, rings.Count > 0);
            for (var k = 0; k < rings.Count; k++)
                AddWheelRing(cx, cy, firstRadius + k * ringGap);

            // Hub in the middle, members around it; alternate rings start half a step round so
            // the spokes don't line up into a single crowded column.
            Place(hub, cx - NodeW / 2, cy - NodeH / 2);
            for (var k = 0; k < rings.Count; k++)
            {
                var ring = rings[k];
                var r = firstRadius + k * ringGap;
                var step = 2 * Math.PI / ring.Count;
                var start = -Math.PI / 2 + (k % 2 == 1 ? step / 2 : 0);
                for (var i = 0; i < ring.Count; i++)
                {
                    var a = start + i * step;
                    Place(ring[i], cx + r * Math.Cos(a) - NodeW / 2, cy + r * Math.Sin(a) - NodeH / 2);
                }
            }

            x += d + wheelGap;
            rowH = Math.Max(rowH, d);
            maxRight = Math.Max(maxRight, cx + radius);
            maxBottom = Math.Max(maxBottom, cy + radius);
        }
        if (wheels.Count > 0) y += rowH + wheelGap;

        // Everything that neither needs nor is needed by another installed mod: a plain grid block.
        var singles = _mods.Where(m => !hubSet.Contains(m) && !assigned.Contains(m))
            .OrderByDescending(m => m.Priority)
            .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (singles.Count > 0)
        {
            const double innerPad = 12, headerH = 28;
            var blockW = Math.Max(rowLimit, NodeW + innerPad * 2);
            var cols = Math.Max(1, (int)((blockW - innerPad * 2 + HGap) / (NodeW + HGap)));
            cols = Math.Min(cols, singles.Count);
            var rows = (int)Math.Ceiling(singles.Count / (double)cols);
            var boxW = cols * NodeW + (cols - 1) * HGap + innerPad * 2;
            var boxH = headerH + rows * NodeH + (rows - 1) * VGap + innerPad * 2;
            AddClusterBox(Origin, y, boxW, boxH);
            var label = new TextBlock
            {
                Text = "No dependencies", FontWeight = FontWeights.SemiBold, FontSize = 12,
                Foreground = Res("TextSecondaryBrush"), IsHitTestVisible = false
            };
            Canvas.SetLeft(label, Origin + innerPad);
            Canvas.SetTop(label, y + 6);
            Panel.SetZIndex(label, 2);
            GraphCanvas.Children.Add(label);
            for (var i = 0; i < singles.Count; i++)
            {
                int c = i % cols, rr = i / cols;
                Place(singles[i], Origin + innerPad + c * (NodeW + HGap), y + headerH + rr * (NodeH + VGap));
            }
            maxRight = Math.Max(maxRight, Origin + boxW);
            maxBottom = Math.Max(maxBottom, y + boxH);
        }

        return (maxRight + Origin, maxBottom + Origin);
    }

    /// <summary>The faint disc behind a wheel, so a dependency group reads as one shape.</summary>
    private void AddWheelDisc(double cx, double cy, double radius, bool hasRings) =>
        _wheels?.AddDisc(cx, cy, radius, hasRings);

    /// <summary>A thin guide circle through a ring's node centres.</summary>
    private void AddWheelRing(double cx, double cy, double radius) => _wheels?.AddRing(cx, cy, radius);

    private (double w, double h) LayoutCustom()
    {
        double maxX = 0, maxY = 0;
        var unsaved = new List<PackMod>();
        foreach (var m in _mods)
        {
            if (App.State.ModMetadata.TryGetNodePosition(_packId, m.Key, out var x, out var y))
            {
                Place(m, x, y);
                maxX = Math.Max(maxX, x + NodeW);
                maxY = Math.Max(maxY, y + NodeH);
            }
            else unsaved.Add(m);
        }

        var avail = Viewport.ActualWidth > 80 ? Viewport.ActualWidth : 1000;
        var cols = Math.Max(1, (int)(avail / (NodeW + HGap)));
        var gy = maxY > 0 ? maxY + VGap * 2 : Origin;
        for (var i = 0; i < unsaved.Count; i++)
        {
            int r = i / cols, c = i % cols;
            var x = Origin + c * (NodeW + HGap);
            var y = gy + r * (NodeH + VGap);
            Place(unsaved[i], x, y);
            maxX = Math.Max(maxX, x + NodeW);
            maxY = Math.Max(maxY, y + NodeH);
        }
        return (maxX + Origin, maxY + Origin);
    }

    // ── edges ──

    /// <summary>Every edge as a pair of endpoint rectangles. Cheap and allocation-only, so the
    /// chunked build can work out how much there is to draw before it starts drawing.</summary>
    private List<(Rect from, Rect to)> CollectEdges()
    {
        var graph = _graph ??= ModGraph.Build(_mods);
        var pairs = new List<(Rect, Rect)>();
        foreach (var m in _mods)
        {
            if (!_rect.TryGetValue(m, out var ra)) continue;
            foreach (var dep in graph.DependenciesOf(m))
            {
                if (!_rect.TryGetValue(dep, out var rb)) continue;
                // The arrow points from the dependency to the mod that needs it (dep -> dependent).
                pairs.Add((rb, ra));
            }
        }
        return pairs;
    }

    /// <summary>Draws every dependency line into the edge layer and returns how many there were.</summary>
    private int DrawEdges()
    {
        var pairs = CollectEdges();
        _edges?.Build(pairs, new Size(Math.Max(1, GraphCanvas.Width), Math.Max(1, GraphCanvas.Height)));
        return pairs.Count;
    }

    /// <summary>Writes one edge: its curve into <paramref name="line"/> and its arrowhead into
    /// <paramref name="head"/>.</summary>
    private static void WriteEdge(StreamGeometryContext line, StreamGeometryContext head, Rect from, Rect to)
    {
        var ca = new Point(from.X + from.Width / 2, from.Y + from.Height / 2);
        var cb = new Point(to.X + to.Width / 2, to.Y + to.Height / 2);
        var start = BorderPoint(from, cb);
        var end = BorderPoint(to, ca);

        var dir = new Vector(end.X - start.X, end.Y - start.Y);
        var len = dir.Length;
        if (len < 2) return; // nodes overlapping; an edge here would just be noise

        var perp = new Vector(-dir.Y, dir.X);
        if (perp.Length > 0) perp.Normalize();
        // Straighten short edges (a big bow between close nodes looks bad); bow longer ones gently.
        var offset = len < 90 ? 0 : Math.Min(34, len * 0.14);
        var ctrl = new Point((start.X + end.X) / 2 + perp.X * offset,
                             (start.Y + end.Y) / 2 + perp.Y * offset);

        line.BeginFigure(start, isFilled: false, isClosed: false);
        line.QuadraticBezierTo(ctrl, end, isStroked: true, isSmoothJoin: true);

        // Small, sleek arrowhead at the target end, scaled down on short edges.
        var arrowLen = Math.Clamp(len * 0.3, 4, 8);
        var arrowW = arrowLen * 0.5;
        var tan = offset == 0 ? dir : new Vector(end.X - ctrl.X, end.Y - ctrl.Y);
        if (tan.Length > 0) tan.Normalize();
        var back = new Point(end.X - tan.X * arrowLen, end.Y - tan.Y * arrowLen);
        var ap = new Vector(-tan.Y, tan.X);
        head.BeginFigure(end, isFilled: true, isClosed: true);
        head.LineTo(new Point(back.X + ap.X * arrowW, back.Y + ap.Y * arrowW), isStroked: false, isSmoothJoin: false);
        head.LineTo(new Point(back.X - ap.X * arrowW, back.Y - ap.Y * arrowW), isStroked: false, isSmoothJoin: false);
    }

    private static Point BorderPoint(Rect r, Point toward)
    {
        var c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        double dx = toward.X - c.X, dy = toward.Y - c.Y;
        if (dx == 0 && dy == 0) return c;
        var tx = dx != 0 ? (r.Width / 2 + 2) / Math.Abs(dx) : double.MaxValue;
        var ty = dy != 0 ? (r.Height / 2 + 2) / Math.Abs(dy) : double.MaxValue;
        var t = Math.Min(tx, ty);
        return new Point(c.X + dx * t, c.Y + dy * t);
    }

    /// <summary>Re-draws every dependency line after a drag moved a node or a cluster. Synchronous so
    /// it keeps up with the mouse; only reachable after the chunked build, since dragging is blocked
    /// while <c>_building</c> is set.</summary>
    private void RedrawEdges() => DrawEdges();

    private void SetLinesVisible(bool visible)
    {
        if (_edges is not null) _edges.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── node + cluster visuals ──

    private Border AddClusterBox(double x, double y, double w, double h)
    {
        var box = new CloudLauncher.Controls.SlateBorder
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(10),
            Background = Res("Surface2Brush"), BorderBrush = Res("BorderSubtleBrush"),
            BorderThickness = new Thickness(1), IsHitTestVisible = false, Opacity = 0.6
        };
        Canvas.SetLeft(box, x);
        Canvas.SetTop(box, y);
        Panel.SetZIndex(box, -1);
        GraphCanvas.Children.Add(box);
        return box;
    }

    /// <summary>The vertical rule separating two content-size bands inside a cluster.</summary>
    private Rectangle AddBandDivider(double x, double y, double h)
    {
        var rule = new Rectangle
        {
            Width = 1,
            Height = h,
            Fill = Res("BorderStrongBrush"),
            Opacity = 0.75,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(rule, x);
        Canvas.SetTop(rule, y);
        Panel.SetZIndex(rule, 0); // above the cluster box (-1), below the nodes (1)
        GraphCanvas.Children.Add(rule);
        return rule;
    }

    /// <summary>A category cluster's header strip, the grab handle and fold control: the caret
    /// collapses the category, left-drag moves the whole cluster, double-click folds it, right-click
    /// renames it.</summary>
    private Border AddHeader(string text, double x, double y, double w, double headerH, double pad,
        ClusterVisual cv, Brush? tint = null, int memberCount = 0, bool isCollapsed = false)
    {
        var foreground = tint ?? Res("TextSecondaryBrush");

        // ▸ when folded, ▾ when open, like every tree in the app.
        var caret = new TextBlock
        {
            Text = isCollapsed ? "\uE76C" : "\uE70D",
            FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
            FontSize = 9,
            Foreground = foreground,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(pad, 3, 6, 0),
            Cursor = Cursors.Hand,
            ToolTip = new ToolTip { Content = isCollapsed ? "Expand this category" : "Collapse this category" }
        };
        // Handle the press too: the header underneath starts a drag on mouse-down.
        caret.MouseLeftButtonDown += (_, e) => e.Handled = true;
        caret.MouseLeftButtonUp += (_, e) => { e.Handled = true; ToggleCategoryCollapsed(cv.Key); };

        var title = new TextBlock
        {
            Text = text, FontWeight = FontWeights.SemiBold, FontSize = 12,
            Foreground = foreground, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 6, 0)
        };

        // The count is what makes a folded category still informative.
        var count = new TextBlock
        {
            Text = memberCount > 0 ? memberCount.ToString() : "",
            FontSize = 11,
            Foreground = Res("TextTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 3, pad, 0)
        };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(caret, 0);
        Grid.SetColumn(title, 1);
        Grid.SetColumn(count, 2);
        row.Children.Add(caret);
        row.Children.Add(title);
        row.Children.Add(count);

        var header = new CloudLauncher.Controls.SlateBorder
        {
            Width = w, Height = headerH, Background = Brushes.Transparent, Cursor = Cursors.SizeAll,
            ToolTip = new ToolTip { Content = "Drag to move · double-click to fold · right-click to rename" },
            Child = row
        };
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount >= 2) { e.Handled = true; ToggleCategoryCollapsed(cv.Key); return; }
            OnClusterDown(cv, e);
        };
        header.MouseMove += OnClusterMove;
        header.MouseLeftButtonUp += OnClusterUp;
        header.MouseRightButtonUp += (_, e) => { e.Handled = true; _ = RenameCategoryAsync(cv.Key); };
        Canvas.SetLeft(header, x);
        Canvas.SetTop(header, y);
        Panel.SetZIndex(header, 2);
        GraphCanvas.Children.Add(header);
        return header;
    }

    /// <summary>Places a node's frame at (x, y) and queues its contents for the chunked fill. The
    /// frame carries the node's size, colours and mouse handlers, so the canvas is laid out, hit-
    /// testable and correctly sized from the moment the layout returns.</summary>
    private GraphNode Place(PackMod m, double x, double y)
    {
        var node = CreateNodeFrame(m);
        Canvas.SetLeft(node, x);
        Canvas.SetTop(node, y);
        Panel.SetZIndex(node, 1);
        GraphCanvas.Children.Add(node);
        _rect[m] = new Rect(x, y, NodeW, NodeH);
        _nodeEls[m] = node;
        _pendingNodes.Add(node);
        return node;
    }

    /// <summary>
    /// One node: a single element that paints its own frame and, once <see cref="FillNode"/> has run,
    /// its own icon, labels and markers.
    /// </summary>
    /// <remarks>
    /// One drawn element instead of nested Borders, Grids and TextBlocks keeps the tree a tenth the
    /// size at the node cap, and the node is the only hit-testable part. The handlers are shared method
    /// groups since each node knows its own mod.
    /// </remarks>
    private GraphNode CreateNodeFrame(PackMod m)
    {
        var node = new GraphNode(m, _theme!, BaseBorderFor(m))
        {
            Width = NodeW, Height = NodeH,
            Cursor = Cursors.Hand,
            Opacity = BaseOpacityFor(m)
        };
        node.MouseLeftButtonDown += OnNodeDown;
        node.MouseMove += OnNodeMove;
        node.MouseLeftButtonUp += OnNodeUp;
        node.MouseRightButtonUp += OnNodeRightClick;
        node.ToolTipOpening += OnNodeToolTipOpening;
        return node;
    }

    /// <summary>The node's border when no find is running: its priority colour, or the plain one.
    /// Uses the build's brush snapshot since it runs for every node on each find keystroke.</summary>
    private Brush BaseBorderFor(PackMod m) =>
        PriorityPalette.IsEmphasized(m.Priority) ? PriorityPalette.BorderFor(m.Priority)
            : _theme?.BorderStrong ?? Res("BorderStrongBrush");

    /// <summary>Disabled mods are dimmed. This is a node's opacity before any find applies.</summary>
    private static double BaseOpacityFor(PackMod m) => m.Enabled ? 1.0 : 0.45;

    /// <summary>Builds a placed node's contents: the expensive half, run in chunks after the
    /// layout.</summary>
    /// <remarks>
    /// The name and version are measured into glyph runs once here. Tooltips are not built here:
    /// <see cref="PackMod.TooltipText"/> is assembled on every read, so all nodes share one
    /// <see cref="ToolTip"/> that <see cref="OnNodeToolTipOpening"/> fills for the hovered node.
    /// </remarks>
    private void FillNode(GraphNode node)
    {
        node.ToolTip = SharedTip;
        node.Fill();
        if (node.Mod.IconUrl is { Length: > 0 } url) LoadIconAsync(node, url, _buildGen);
    }

    /// <summary>The one tooltip every node borrows. Built lazily so a launcher that never opens the
    /// Graph tab never makes one.</summary>
    private ToolTip? _sharedTip;
    private TextBlock? _sharedTipText;

    private ToolTip SharedTip
    {
        get
        {
            if (_sharedTip is not null) return _sharedTip;
            _sharedTipText = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
            return _sharedTip = new ToolTip { Content = _sharedTipText };
        }
    }

    /// <summary>Fills the shared tooltip for the node about to show it. Raised before the popup is
    /// built, so the content is correct by the time anything is on screen.</summary>
    private void OnNodeToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is GraphNode node && _sharedTipText is not null) _sharedTipText.Text = node.Mod.TooltipText;
    }

    /// <summary>
    /// Puts a mod's icon on its node, from the cache when it is already there and off the download
    /// otherwise.
    /// </summary>
    /// <remarks>Not <c>ModIconCache.Image</c>, which builds an Image element with recycling hooks for
    /// virtualized lists; a node draws the <see cref="ImageSource"/> itself. <paramref name="gen"/> is
    /// the build it belongs to, so a download landing after a re-layout is dropped.</remarks>
    private async void LoadIconAsync(GraphNode node, string url, int gen)
    {
        try
        {
            if (ModIconCache.TryGet(url, 28) is { } cached) { node.SetIcon(cached); return; }
            var loaded = await ModIconCache.LoadAsync(url, 28);   // resumes on the UI thread
            if (loaded is not null && gen == _buildGen) node.SetIcon(loaded);
        }
        catch
        {
            // ModIconCache handles its own failures; this is here because a throwing async void
            // would crash the launcher.
        }
    }

    // ── node drag ──

    private void OnNodeDown(object sender, MouseButtonEventArgs e)
    {
        if (_building || sender is not GraphNode node) return;   // a drag redraws every edge; let the build finish owning the canvas
        _dragNode = node;
        _dragMod = node.Mod;
        _dragMoved = false;
        _dragStart = e.GetPosition(GraphCanvas);
        _dragOrigLeft = Canvas.GetLeft(node);
        _dragOrigTop = Canvas.GetTop(node);
        node.CaptureMouse();
        e.Handled = true;
    }

    private void OnNodeMove(object sender, MouseEventArgs e)
    {
        if (_dragNode is null || _dragMod is null) return;
        var p = e.GetPosition(GraphCanvas);
        double dx = p.X - _dragStart.X, dy = p.Y - _dragStart.Y;
        if (!_dragMoved && Math.Abs(dx) + Math.Abs(dy) < 4) return;
        _dragMoved = true;
        if (!IsCustomMode) return; // only the custom layout is draggable

        _userInteracted = true;
        // A rebuild can remove this mod mid-drag: the node's context menu takes mouse capture, so
        // OnNodeUp never runs while Delete or Disable rebuilds the graph. Indexing _rect would then
        // throw out of a WPF mouse handler and crash the launcher.
        if (!_rect.TryGetValue(_dragMod, out var r)) { CancelNodeDrag(); return; }
        double nl = _dragOrigLeft + dx, nt = _dragOrigTop + dy;
        Canvas.SetLeft(_dragNode, nl);
        Canvas.SetTop(_dragNode, nt);
        _rect[_dragMod] = new Rect(nl, nt, r.Width, r.Height);
        RedrawEdges();
    }

    private void OnNodeUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragNode is null || _dragMod is null) return;
        _dragNode.ReleaseMouseCapture();
        var mod = _dragMod;
        var dragged = _dragMoved;
        _dragNode = null;
        _dragMod = null;
        e.Handled = true;

        // A plain click does nothing; the page opens from the right-click menu ("Open page").
        if (!dragged) return;
        if (IsCustomMode && _rect.TryGetValue(mod, out var r))
            App.State.ModMetadata.SetNodePosition(_packId, mod.Key, r.X, r.Y);
    }

    /// <summary>
    /// Right-click on a node: ends any drag still in flight, then opens the options menu for the node
    /// actually under the cursor.
    /// </summary>
    /// <remarks>
    /// <para>During a drag the dragged node holds mouse capture, so a right-click anywhere is routed to
    /// it. Opening the menu also steals capture, so <see cref="OnNodeUp"/> never runs, and a menu item
    /// that rebuilds the graph would leave the drag pointing at a mod no longer in <c>_rect</c>.</para>
    /// <para>The try/catch matters: an exception escaping a WPF event handler crashes the launcher.</para>
    /// </remarks>
    private void OnNodeRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is not GraphNode node) return;
        try
        {
            var position = e.GetPosition(GraphCanvas);
            CancelNodeDrag();
            var target = NodeUnder(position) ?? node;
            ShowOptions(target.Mod, target);
        }
        catch (Exception ex) { SetStatus("Could not open the menu: " + ex.Message); }
    }

    /// <summary>The node at a canvas point, or null when the point is not over one.</summary>
    /// <remarks>Used instead of the event source because during a drag every mouse event goes to the
    /// captured node.</remarks>
    private GraphNode? NodeUnder(Point canvasPoint)
    {
        var hit = GraphCanvas.InputHitTest(canvasPoint) as DependencyObject;
        for (; hit is not null; hit = VisualTreeHelper.GetParent(hit))
            if (hit is GraphNode node) return node;
        return null;
    }

    /// <summary>
    /// Ends a drag in flight, keeping wherever the node was dragged to, and drops every reference the
    /// drag was holding.
    /// </summary>
    /// <remarks>Clears <c>_dragMod</c>, a key into <c>_rect</c> that a rebuild can remove
    /// mid-drag.</remarks>
    private void CancelNodeDrag()
    {
        if (_dragNode is null && _dragMod is null) return;
        _dragNode?.ReleaseMouseCapture();
        var mod = _dragMod;
        var dragged = _dragMoved;
        _dragNode = null;
        _dragMod = null;
        _dragMoved = false;
        if (dragged && mod is not null && IsCustomMode && _rect.TryGetValue(mod, out var r))
            App.State.ModMetadata.SetNodePosition(_packId, mod.Key, r.X, r.Y);
    }

    // ── category-cluster drag (Categories mode) ──

    private void OnClusterDown(ClusterVisual cv, MouseButtonEventArgs e)
    {
        if (!IsCategoryMode || _building) return;
        _dragCluster = cv;
        _clusterDragMoved = false;
        _clusterDragStart = e.GetPosition(GraphCanvas);
        _clusterOrigX = cv.X;
        _clusterOrigY = cv.Y;
        cv.Header.CaptureMouse();
        e.Handled = true;
    }

    private void OnClusterMove(object sender, MouseEventArgs e)
    {
        if (_dragCluster is null) return;
        var p = e.GetPosition(GraphCanvas);
        double dx = p.X - _clusterDragStart.X, dy = p.Y - _clusterDragStart.Y;
        if (!_clusterDragMoved && Math.Abs(dx) + Math.Abs(dy) < 4) return; // ignore jitter
        _clusterDragMoved = true;
        _userInteracted = true;
        MoveCluster(_dragCluster, _clusterOrigX + dx, _clusterOrigY + dy);
        RedrawEdges();
    }

    private void OnClusterUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragCluster is null) return;
        var cv = _dragCluster;
        var moved = _clusterDragMoved;
        cv.Header.ReleaseMouseCapture();
        _dragCluster = null;
        e.Handled = true;
        if (moved) App.State.ModMetadata.SetCategoryPosition(_packId, cv.Key, cv.X, cv.Y);
    }

    private void MoveCluster(ClusterVisual cv, double nx, double ny)
    {
        double dx = nx - cv.X, dy = ny - cv.Y;
        cv.X = nx; cv.Y = ny;
        Canvas.SetLeft(cv.Box, nx); Canvas.SetTop(cv.Box, ny);
        Canvas.SetLeft(cv.Header, nx); Canvas.SetTop(cv.Header, ny);
        foreach (var rule in cv.Dividers)
        {
            Canvas.SetLeft(rule, Canvas.GetLeft(rule) + dx);
            Canvas.SetTop(rule, Canvas.GetTop(rule) + dy);
        }
        foreach (var node in cv.Nodes)
        {
            double nl = Canvas.GetLeft(node) + dx, nt = Canvas.GetTop(node) + dy;
            Canvas.SetLeft(node, nl); Canvas.SetTop(node, nt);
            _rect[node.Mod] = new Rect(nl, nt, NodeW, NodeH);
        }
        // grow the canvas so a cluster dragged past the edge isn't clipped (and Fit still includes it)
        GraphCanvas.Width = Math.Max(GraphCanvas.Width, nx + cv.W + Origin);
        GraphCanvas.Height = Math.Max(GraphCanvas.Height, ny + cv.H + Origin);
    }

    // ── folding categories (Categories mode) ──

    /// <summary>
    /// Folds or unfolds one category and relays out. The state lives with the pack, so it survives
    /// switching tabs, reopening the launcher, and syncs to whoever else works on the pack.
    /// </summary>
    private void ToggleCategoryCollapsed(string key)
    {
        // Mid-drag the header may still have the mouse; drop the drag so the relayout is not fighting it.
        _dragCluster = null;
        var collapsed = App.State.ModMetadata.IsCategoryCollapsed(_packId, key);
        App.State.ModMetadata.SetCategoryCollapsed(_packId, key, !collapsed);
        Rebuild(refit: false);
    }

    /// <summary>Folds every category at once, or opens them all when they already are folded.</summary>
    private void OnCollapseAll(object sender, RoutedEventArgs e)
    {
        var keys = CategoryKeys();
        if (keys.Count == 0) return;
        var collapse = keys.Any(k => !App.State.ModMetadata.IsCategoryCollapsed(_packId, k));
        App.State.ModMetadata.SetCategoriesCollapsed(_packId, keys, collapse);
        Rebuild(refit: false);
    }

    /// <summary>Every category the graph currently shows a cluster for, including "Uncategorized".</summary>
    private List<string> CategoryKeys() =>
        _mods.Select(m => m.Meta.Categories.FirstOrDefault() ?? "Uncategorized")
             .Distinct(StringComparer.OrdinalIgnoreCase)
             .ToList();

    /// <summary>Keeps the Collapse-all button in step with the mode and with what is folded.</summary>
    private void RefreshCollapseButton()
    {
        if (CollapseAllButton is null) return;
        CollapseAllButton.Visibility = IsCategoryMode ? Visibility.Visible : Visibility.Collapsed;
        if (!IsCategoryMode) return;
        var keys = CategoryKeys();
        var anyOpen = keys.Any(k => !App.State.ModMetadata.IsCategoryCollapsed(_packId, k));
        CollapseAllButton.Content = anyOpen ? "Collapse all" : "Expand all";
    }

    private async Task RenameCategoryAsync(string oldName)
    {
        if (string.Equals(oldName, "Uncategorized", StringComparison.OrdinalIgnoreCase))
        {
            GraphStatus.Text = "'Uncategorized' isn't a real category.";
            return;
        }
        if (string.Equals(oldName, ModMetadataService.LibraryCategory, StringComparison.OrdinalIgnoreCase))
        {
            GraphStatus.Text = "The Library category is managed automatically.";
            return;
        }
        if (_owner is not MainWindow mw) return;

        var input = await mw.PromptAsync("Rename category", "New category name", oldName);
        if (string.IsNullOrWhiteSpace(input)) return;
        var newName = input.Trim();
        if (string.Equals(newName, oldName, StringComparison.OrdinalIgnoreCase)) return;

        App.State.ModMetadata.RenameCategory(_packId, oldName, newName);
        App.State.ModPlans.RenameCategoryReferences(_packId, oldName, newName); // keep board group cards pointed at it
        Rebuild(refit: false); // List view picks the rename up on its next tab switch
    }

    // ── pan / zoom ──

    private void OnViewportMouseDown(object sender, MouseButtonEventArgs e)
    {
        _userInteracted = true;
        _panning = true;
        _panStart = e.GetPosition(Viewport);
        _panOrigX = PanT.X;
        _panOrigY = PanT.Y;
        Viewport.CaptureMouse();
        Viewport.Cursor = Cursors.SizeAll;
    }

    private void OnViewportMouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning) return;
        var p = e.GetPosition(Viewport);
        PanT.X = _panOrigX + (p.X - _panStart.X);
        PanT.Y = _panOrigY + (p.Y - _panStart.Y);
    }

    private void OnViewportMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        Viewport.ReleaseMouseCapture();
        Viewport.Cursor = Cursors.Arrow;
    }

    private void OnViewportWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        _userInteracted = true;
        var m = e.GetPosition(Viewport);
        var oldS = ZoomT.ScaleX;
        var factor = e.Delta > 0 ? 1.12 : 1 / 1.12;
        var newS = Math.Clamp(oldS * factor, 0.2, 3.0);
        var cx = (m.X - PanT.X) / oldS;
        var cy = (m.Y - PanT.Y) / oldS;
        ZoomT.ScaleX = ZoomT.ScaleY = newS;
        PanT.X = m.X - newS * cx;
        PanT.Y = m.Y - newS * cy;
        ApplyZoomDetail(oldS, newS);
    }

    /// <summary>
    /// Switches the nodes between full and reduced drawing when a zoom crosses
    /// <see cref="LabelZoom"/>, and updates the status line to say which is shown.
    /// </summary>
    /// <remarks>Only on a crossing, since flipping the flag redraws every node.</remarks>
    private void ApplyZoomDetail(double oldScale, double newScale)
    {
        var was = oldScale >= LabelZoom;
        var now = newScale >= LabelZoom;
        ZoomNote.Visibility = now ? Visibility.Collapsed : Visibility.Visible;
        if (was == now) return;
        if (_theme is not null) _theme.ShowLabels = now;
        foreach (var node in _nodeEls.Values) node.InvalidateVisual();
    }

    /// <summary>Right-click on empty graph background: the pack-level actions, plus the toolbar
    /// controls. Right-clicks on a node or a cluster header are handled there.</summary>
    private void OnViewportRightClick(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = Viewport };
        menu.Items.Add(ModCategoryMenu.Build(_packId, _mods, _owner as MainWindow,
            onChanged: () => Rebuild(refit: false)));

        menu.Items.Add(new Separator());
        var cluster = new MenuItem { Header = "Cluster by" };
        void ClusterMode(string header, int index) =>
            cluster.Items.Add(Action(header, () => ClusterBox.SelectedIndex = index,
                gesture: ClusterBox.SelectedIndex == index ? "✓" : null));
        ClusterMode("Categories", 0);
        ClusterMode("Dependencies", 1);
        ClusterMode("Dependency circles", 2);
        ClusterMode("Custom layout", 3);
        menu.Items.Add(cluster);

        menu.Items.Add(Action("Dependency lines", () =>
        {
            ShowLines.IsChecked = ShowLines.IsChecked != true;
            SetLinesVisible(ShowLines.IsChecked == true);
        }, gesture: ShowLines.IsChecked == true ? "✓" : null));

        menu.Items.Add(new Separator());
        menu.Items.Add(Action("Fit to view", () => { _userInteracted = false; FitToView(); }));
        menu.Items.Add(Action("Re-scan mods folder", () => _onReload?.Invoke(), enabled: _onReload is not null));
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static MenuItem Action(string header, Action onClick, string? gesture = null, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, IsEnabled = enabled };
        if (gesture is not null) mi.InputGestureText = gesture;
        mi.Click += (_, _) => onClick();
        return mi;
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => ZoomAtCenter(1.2);
    private void OnZoomOut(object sender, RoutedEventArgs e) => ZoomAtCenter(1 / 1.2);

    private void ZoomAtCenter(double factor)
    {
        _userInteracted = true;
        var m = new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2);
        var oldS = ZoomT.ScaleX;
        var newS = Math.Clamp(oldS * factor, 0.2, 3.0);
        var cx = (m.X - PanT.X) / oldS;
        var cy = (m.Y - PanT.Y) / oldS;
        ZoomT.ScaleX = ZoomT.ScaleY = newS;
        PanT.X = m.X - newS * cx;
        PanT.Y = m.Y - newS * cy;
        ApplyZoomDetail(oldS, newS);
    }

    private void OnFit(object sender, RoutedEventArgs e)
    {
        _userInteracted = false; // re-enable auto-fit, then fit now
        FitToView();
    }

    // Auto-fit and centre until the user first pans, zooms or drags. This also re-fits while the
    // viewport settles its size during the tab transition.
    private void TryFit()
    {
        if (!_userInteracted && Viewport.ActualWidth > 10 && GraphCanvas.Width > 0)
            FitToView();
    }

    private void FitToView()
    {
        if (GraphCanvas.Width <= 0 || GraphCanvas.Height <= 0) return;
        double vw = Math.Max(10, Viewport.ActualWidth), vh = Math.Max(10, Viewport.ActualHeight);
        var s = Math.Min((vw - 40) / GraphCanvas.Width, (vh - 40) / GraphCanvas.Height);
        s = Math.Clamp(s, 0.15, 1.4);
        var old = ZoomT.ScaleX;
        ZoomT.ScaleX = ZoomT.ScaleY = s;
        PanT.X = (vw - GraphCanvas.Width * s) / 2;
        PanT.Y = (vh - GraphCanvas.Height * s) / 2;
        ApplyZoomDetail(old, s);
    }

    // ── interactions ──

    private void OpenPage(PackMod m)
    {
        if (_onOpenMod is not null) { _onOpenMod(m); return; }
        if (m.PageUrl is null) return;
        SafeLaunch.OpenUrl(m.PageUrl);
    }

    private async void UpdateNodeToVersion(PackMod mod)
    {
        if (mod.PrimaryMod is null || _owner is not MainWindow host) return;
        GraphStatus.Text = $"Loading versions for {mod.DisplayName}...";
        var versions = await ModUpdater.FetchVersionsAsync(mod.PrimaryMod);
        if (versions.Count == 0) { GraphStatus.Text = "No versions found."; return; }

        var mc = mod.PrimaryVersion?.GameVersions.FirstOrDefault();
        var loader = mod.PrimaryVersion?.Loaders.FirstOrDefault();
        var chosen = await ModVersionPickerDialog.ShowAsync(host, mod.DisplayName, versions, mc, loader,
            mod.PrimaryVersion?.Id, mod.PrimaryVersion?.VersionNumber, mod.Meta.UpdateLocked);
        if (chosen is null) { GraphStatus.Text = ""; return; }

        // The picker's own "Keep this version" box already says where the lock should land.
        if (mod.Meta.UpdateLocked && !chosen.KeepVersion && !await AppDialog.ConfirmAsync(host, "Mod is locked",
                $"{mod.DisplayName} is locked to its current version.\n\nChange it anyway?",
                "Change anyway", "Keep locked"))
        { GraphStatus.Text = ""; return; }

        GraphStatus.Text = $"Installing {chosen.Version.VersionNumber}...";
        try
        {
            if (await ModUpdater.InstallVersionAsync(mod, chosen.Version))
            {
                ModVersionPickerDialog.ApplyKeepVersion(_packId, mod, chosen);
                _onReload?.Invoke();
            }
            else GraphStatus.Text = "That version has no downloadable file.";
        }
        catch (Exception ex) { GraphStatus.Text = ex.Message; }
    }

    private async void DeleteNodesFromGraph(IReadOnlyList<PackMod> mods)
    {
        if (mods.Count == 0 || _owner is not MainWindow host) return;
        var msg = mods.Count == 1
            ? $"Delete {System.IO.Path.GetFileName(mods[0].FilePath)}?"
            : $"Delete {mods.Count} mods?";
        if (!await AppDialog.ConfirmAsync(host, "Delete mods", msg, "Delete", "Cancel", danger: true)) return;
        foreach (var m in mods) { try { System.IO.File.Delete(m.FilePath); } catch { } }
        _onReload?.Invoke(); // re-scan so the nodes disappear from both List and Graph
    }

    /// <summary>
    /// The mod options menu, with every callback the List view's own menu has, so right-clicking a
    /// mod offers the same actions in either tab.
    /// </summary>
    /// <remarks>
    /// <para><see cref="ModOptionsMenu"/> hides items whose callback is null. Updating and re-checking
    /// are handed up to the hub so the lock rules and re-check behaviour can't drift between tabs.</para>
    /// <para>On the global scope a node stands for the same mod in several instances, so Delete and
    /// "Update to version..." are dropped, as in <see cref="ModCategoriesView"/>: they would act on one
    /// jar, and replacing a library copy would leave the library index pointing at a deleted file.</para>
    /// </remarks>
    private void ShowOptions(PackMod m, FrameworkElement anchor)
    {
        var ctx = new ModOptionsContext
        {
            PackId = _packId,
            Inventory = App.State.ModInventory,
            Owner = _owner,
            AllMods = _mods,
            OnChanged = () => { Rebuild(refit: false); _onModsChanged?.Invoke(); },
            OnOpenPage = OpenPage,
            OnSetEnabled = (list, en) => { foreach (var mm in list) App.State.ModInventory.SetEnabled(mm, en); Rebuild(refit: false); },
            OnUpdate = list =>
            {
                if (_onUpdate is null) return;
                GraphStatus.Text = list.Count == 1 ? $"Updating {list[0].DisplayName}..." : $"Updating {list.Count} mod(s)...";
                _onUpdate(list);   // re-scans when it finishes, which rebuilds this graph
            },
            OnUpdateToVersion = ModMetadataService.IsGlobalScope(_packId)
                ? null : (Action<PackMod>)UpdateNodeToVersion,
            OnRecheckUpdates = list =>
            {
                if (_onRecheckUpdates is null) return;
                GraphStatus.Text = "Re-checking for updates...";
                _onRecheckUpdates(list);
            },
            OnDelete = ModMetadataService.IsGlobalScope(_packId)
                ? null : (Action<IReadOnlyList<PackMod>>)DeleteNodesFromGraph,
            OnReveal = mm =>
            {
                var dir = System.IO.Path.GetDirectoryName(mm.FilePath);
                if (dir is null) return;
                if (!SafeLaunch.OpenFolder(dir)) GraphStatus.Text = "Could not open the folder.";
            }
        };
        var menu = ModOptionsMenu.Build(m, ctx);
        menu.PlacementTarget = anchor;
        menu.IsOpen = true;
    }

    private Brush Res(string key) => (Brush)FindResource(key);

    // ── toolbar ──

    private void OnClusterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressToolbar) return;
        // A launcher setting rather than the pack's; see RestoreToolbarState.
        App.State.Settings.GraphClusterMode = ModeFor(ClusterBox.SelectedIndex).ToString();
        App.State.Settings.Save();
        Rebuild();
    }

    private void OnToggleLines(object sender, RoutedEventArgs e)
    {
        var on = ShowLines.IsChecked == true;
        SetLinesVisible(on);
        if (_suppressToolbar) return;
        App.State.Settings.GraphShowDependencyLines = on;
        App.State.Settings.Save();
    }

    // ── find ──

    private void OnFindChanged(object sender, TextChangedEventArgs e)
    {
        _findQuery = FindBox.Text?.Trim() ?? "";
        FindPlaceholder.Visibility = _findQuery.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        FindClearButton.Visibility = _findQuery.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ApplyFind();
    }

    private void OnFindKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || FindBox.Text.Length == 0) return;
        FindBox.Text = "";
        e.Handled = true;
    }

    private void OnFindClear(object sender, RoutedEventArgs e)
    {
        FindBox.Text = "";
        FindBox.Focus();
    }

    /// <summary>
    /// Dims every node whose name does not match the find box and outlines the ones that do; an empty
    /// box puts every node back to its own state.
    /// </summary>
    /// <remarks>
    /// Dims rather than hides, so the layout the user is reading doesn't change. A single match is also
    /// scrolled into view, since on a big pack it is likely off screen.
    /// </remarks>
    private void ApplyFind()
    {
        if (_nodeEls.Count == 0) return;

        if (_findQuery.Length == 0)
        {
            foreach (var (mod, node) in _nodeEls)
            {
                node.Opacity = BaseOpacityFor(mod);
                node.Stroke = BaseBorderFor(mod);
            }
            SetStatus($"{_mods.Count} mod(s)");
            return;
        }

        var accent = Res("AccentBrush");
        PackMod? only = null;
        var hits = 0;
        foreach (var (mod, node) in _nodeEls)
        {
            var hit = Matches(mod);
            if (hit) { hits++; only = mod; }
            node.Opacity = hit ? 1.0 : 0.15;
            // Assigning the same brush is a no-op inside the node, so unchanged nodes cost
            // nothing to redraw.
            node.Stroke = hit ? accent : BaseBorderFor(mod);
        }

        SetStatus(hits switch
        {
            0 => $"No mod matches '{_findQuery}'",
            1 => $"1 match  ·  {_mods.Count} mod(s)",
            _ => $"{hits} matches  ·  {_mods.Count} mod(s)"
        });

        if (hits == 1 && only is not null) CenterOn(only);
    }

    private bool Matches(PackMod m) =>
        m.DisplayName.Contains(_findQuery, StringComparison.OrdinalIgnoreCase)
        || m.FileName.Contains(_findQuery, StringComparison.OrdinalIgnoreCase);

    /// <summary>Pans (without zooming) so a node sits in the middle of the viewport.</summary>
    private void CenterOn(PackMod mod)
    {
        if (!_rect.TryGetValue(mod, out var r) || Viewport.ActualWidth < 10) return;
        _userInteracted = true;   // an explicit move: don't let a later auto-fit undo it
        var s = ZoomT.ScaleX;
        PanT.X = Viewport.ActualWidth / 2 - (r.X + r.Width / 2) * s;
        PanT.Y = Viewport.ActualHeight / 2 - (r.Y + r.Height / 2) * s;
    }

    // ── drawn layers ──
    // Anything the user can click, drag or hover is an element; anything only looked at is drawn
    // as geometry inside one. With thousands of nodes, the element tree is the main cost.

    /// <summary>
    /// The brushes, pens, typefaces and glyph metrics every node draws with, resolved once per build.
    /// </summary>
    /// <remarks>Snapshots, so a theme change needs a rebuild (a tab switch does one).</remarks>
    private sealed class NodeTheme
    {
        public readonly Brush Surface3, Surface2, TextPrimary, TextSecondary, Accent, BorderStrong;
        public readonly Brush RingStroke, DiscFill, DiscStroke;
        public readonly Pen IconPen, DiscPen, RingPen;
        public readonly FontFamily UiFont, IconFont;
        public readonly double Dpi;

        /// <summary>The note marker: one immutable <see cref="FormattedText"/> shared by every node
        /// that has a note.</summary>
        public readonly FormattedText NoteGlyph;

        /// <summary>The icon clip, per border thickness (1 for a plain node, 2 for an emphasized one,
        /// which shifts the content in by a pixel).</summary>
        public readonly Geometry ThinIconClip, ThickIconClip;

        /// <summary>False while the canvas is zoomed out past <see cref="LabelZoom"/>. Flipped by
        /// <see cref="ApplyZoomDetail"/>, read by every node as it renders.</summary>
        public bool ShowLabels = true;

        private readonly Dictionary<Brush, Pen> _thin = new(), _thick = new();

        public NodeTheme(FrameworkElement owner)
        {
            Brush R(string key) => (Brush)owner.FindResource(key);
            Surface3 = R("Surface3Brush");
            Surface2 = R("Surface2Brush");
            TextPrimary = R("TextPrimaryBrush");
            TextSecondary = R("TextSecondaryBrush");
            Accent = R("AccentBrush");
            BorderStrong = R("BorderStrongBrush");
            DiscFill = R("Surface2Brush");
            DiscStroke = R("BorderSubtleBrush");
            RingStroke = R("BorderStrongBrush");

            IconPen = new Pen(R("BorderBrush"), 1);
            DiscPen = new Pen(DiscStroke, 1);
            // Pen.DashCap defaults to Square (a Shape's is Flat), so Flat is set explicitly for the
            // guide ring dashes.
            RingPen = new Pen(RingStroke, 1)
            {
                DashStyle = new DashStyle(new double[] { 4, 6 }, 0),
                DashCap = PenLineCap.Flat
            };

            // Inherited from the window's style rather than hardcoded, the same font a plain TextBlock
            // would get.
            UiFont = System.Windows.Documents.TextElement.GetFontFamily(owner);
            IconFont = (FontFamily)owner.FindResource("IconFont");
            Dpi = VisualTreeHelper.GetDpi(owner).PixelsPerDip;

            NoteGlyph = new FormattedText(char.ConvertFromUtf32(0xE70B), CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, new Typeface(IconFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                11, Accent, Dpi);

            ThinIconClip = Clip(1);
            ThickIconClip = Clip(2);

            static Geometry Clip(double thickness)
            {
                var g = new RectangleGeometry(new Rect(thickness + 9, (NodeH - 26) / 2, 26, 26));
                g.Freeze();
                return g;
            }
        }

        /// <summary>A pen for a node's frame, shared by every node with the same colour and weight. Not
        /// frozen: the brushes come from the theme dictionary, and freezing one would freeze it
        /// app-wide.</summary>
        public Pen PenFor(Brush brush, double thickness)
        {
            var cache = thickness > 1.5 ? _thick : _thin;
            if (cache.TryGetValue(brush, out var pen)) return pen;
            return cache[brush] = new Pen(brush, thickness);
        }

        private readonly Dictionary<string, FormattedText> _initials = new(StringComparer.Ordinal);

        /// <summary>The icon-well letter for an initial, shaped once per distinct letter.</summary>
        public FormattedText InitialFor(string initial)
        {
            if (_initials.TryGetValue(initial, out var ft)) return ft;
            return _initials[initial] = new FormattedText(initial, CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(UiFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                12, TextSecondary, Dpi);
        }
    }

    /// <summary>
    /// One mod on the canvas, drawn rather than composed. See <see cref="CreateNodeFrame"/> for why.
    /// </summary>
    private sealed class GraphNode : FrameworkElement
    {
        public readonly PackMod Mod;

        private readonly NodeTheme _t;
        private readonly double _thickness;
        private Brush _stroke;
        private Pen _pen;

        private bool _filled;
        private FormattedText? _name, _version, _initial;
        private ImageSource? _icon;
        private double _markersX;          // left edge of the marker column, 0 when there is none
        private int _size;                 // clamped content size, for the meter
        private bool _note;

        /// <param name="stroke">The frame colour. Passed in rather than set through
        /// <see cref="Stroke"/>, whose setter invalidates the visual (and so the arrange) before the node
        /// is even on the canvas.</param>
        public GraphNode(PackMod mod, NodeTheme theme, Brush stroke)
        {
            Mod = mod;
            _t = theme;
            _thickness = PriorityPalette.IsEmphasized(mod.Priority) ? 2 : 1;
            _stroke = stroke;
            _pen = theme.PenFor(stroke, _thickness);
        }

        /// <summary>The frame colour: the mod's priority colour, or the find highlight.</summary>
        public Brush Stroke
        {
            get => _stroke;
            set
            {
                // The same brush again is the common case on each find keystroke; skip the
                // redraw.
                if (ReferenceEquals(_stroke, value)) return;
                _stroke = value;
                _pen = _t.PenFor(value, _thickness);
                InvalidateVisual();
            }
        }

        public void SetIcon(ImageSource icon)
        {
            _icon = icon;
            InvalidateVisual();
        }

        /// <summary>Measures the node's text once and remembers it. Until this runs the node draws as
        /// an empty frame, which is what makes a big graph appear immediately and fill in.</summary>
        public void Fill()
        {
            if (_filled) return;
            _filled = true;

            _size = ModContentSize.Clamp(Mod.ContentSize);
            _note = Mod.HasNote;

            // Layout: [icon 28 + 8 gap][text][markers]. The marker column is the meter (13 wide
            // when set), the note glyph, and the 6px gap before them.
            var markers = (_size > 0 ? 13.0 : 0) + (_note ? (_size > 0 ? 5 : 0) + _t.NoteGlyph.Width : 0);
            var content = NodeW - 2 * _thickness - 16;
            var star = content - 36 - (markers > 0 ? markers + 6 : 0);
            _markersX = markers > 0 ? _thickness + 8 + 36 + star + 6 : 0;

            _name = Line(Mod.DisplayName, 13, FontWeights.SemiBold, _t.TextPrimary, star);
            _version = Line(Mod.VersionLabel, 11, FontWeights.Normal, _t.TextSecondary, star);
            // Shared across the canvas, since a pack only has a few dozen distinct first letters.
            _initial = _t.InitialFor(Mod.Initial);
            InvalidateVisual();
        }

        private FormattedText Line(string text, double size, FontWeight weight, Brush brush, double width)
        {
            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(_t.UiFont, FontStyles.Normal, weight, FontStretches.Normal), size, brush, _t.Dpi)
            {
                MaxTextWidth = Math.Max(1, width),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis   // FormattedText defaults to WordEllipsis
            };
            return ft;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var half = _thickness / 2;
            dc.DrawRoundedRectangle(_t.Surface3, _pen,
                new Rect(half, half, NodeW - _thickness, NodeH - _thickness), 8 - half, 8 - half);
            if (!_filled) return;

            // Content sits inside the border thickness with an 8px horizontal margin.
            double cx = _thickness + 8, cy = _thickness, ch = NodeH - 2 * _thickness;
            var iy = cy + (ch - 28) / 2;

            if (_t.ShowLabels)
            {
                // Icon well: a 1px border drawn inside a 28-square, so the stroke is inset by half.
                dc.DrawRoundedRectangle(_t.Surface2, _t.IconPen, new Rect(cx + 0.5, iy + 0.5, 27, 27), 5.5, 5.5);
                // Drawn under the artwork too, so it shows through an icon with transparency.
                if (_initial is not null)
                    dc.DrawText(_initial, new Point(cx + 14 - _initial.Width / 2, iy + 14 - _initial.Height / 2));
            }

            if (_icon is not null)
            {
                dc.PushClip(_thickness > 1.5 ? _t.ThickIconClip : _t.ThinIconClip);
                dc.DrawImage(_icon, UniformToFill(new Rect(cx + 1, iy + 1, 26, 26), _icon));
                dc.Pop();
            }

            // Below the label zoom the node is just the frame and icon.
            if (!_t.ShowLabels || _name is null) return;

            var tx = cx + 36;
            var th = _name.Height + (_version?.Height ?? 0);
            var ty = cy + (ch - th) / 2;
            dc.DrawText(_name, new Point(tx, ty));
            if (_version is not null) dc.DrawText(_version, new Point(tx, ty + _name.Height));

            if (_markersX <= 0) return;
            // The marker row sits 9px down, with its items centred vertically in it.
            var rowH = Math.Max(_size > 0 ? 10 : 0, _note ? _t.NoteGlyph.Height : 0);
            var my = cy + 9;
            var mx = _markersX;
            if (_size > 0)
            {
                // ContentSizeMeter's geometry: 3-wide bars, 2 apart, 4 tall rising by 3, lit up to the
                // mod's size. Drawn here rather than nested as a control.
                var top = my + (rowH - 10) / 2;
                for (var i = 0; i < ModContentSize.Max; i++)
                {
                    var h = 4 + i * 3;
                    dc.DrawRoundedRectangle(i < _size ? _t.Accent : _t.BorderStrong, null,
                        new Rect(mx + i * 5, top + 10 - h, 3, h), 1, 1);
                }
                mx += 13 + 5;
            }
            if (_note) dc.DrawText(_t.NoteGlyph, new Point(mx, my + (rowH - _t.NoteGlyph.Height) / 2));
        }

        /// <summary>Same as <c>Stretch="UniformToFill"</c>: cover the box, centred, cropped by the
        /// clip the icon well pushes.</summary>
        private static Rect UniformToFill(Rect box, ImageSource src)
        {
            if (src.Width <= 0 || src.Height <= 0) return box;
            var s = Math.Max(box.Width / src.Width, box.Height / src.Height);
            double w = src.Width * s, h = src.Height * s;
            return new Rect(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
        }
    }

    /// <summary>
    /// Every dependency line and arrowhead on the canvas, as frozen geometry: one pair of geometries
    /// per tile of the canvas.
    /// </summary>
    /// <remarks>
    /// Rasterizing a geometry costs roughly its bounding box times the edges crossing each scanline,
    /// so one canvas-wide geometry is slow on the huge Circles and Custom canvases. Tiling bounds both
    /// (at 1,500 nodes, Circles drops from ~950 ms to ~330 ms a frame). Layouts whose edges each span
    /// the canvas, like the unsaved Custom grid, stay slow while the lines are shown.
    /// </remarks>
    private sealed class EdgeLayer : FrameworkElement
    {
        /// <summary>Roughly how wide a tile is, in canvas units. Small enough that a tile's
        /// rasterization is bounded, big enough that a normal canvas is tens of tiles and not
        /// thousands of draw calls.</summary>
        private const double TileSize = 1200;
        private const int MaxTiles = 24;   // per axis

        private Geometry[] _lines = Array.Empty<Geometry>();
        private Geometry[] _heads = Array.Empty<Geometry>();

        public EdgeLayer() => IsHitTestVisible = false;   // edges aren't clickable

        /// <summary>Refuses the hit test outright.</summary>
        /// <remarks><see cref="UIElement.IsHitTestVisible"/> isn't enough: the content spans the whole
        /// canvas, so bounds never reject it and a mouse move would test every curve.</remarks>
        protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) => null;

        protected override GeometryHitTestResult? HitTestCore(GeometryHitTestParameters hitTestParameters) => null;

        /// <param name="extent">The canvas the edges are spread over, which decides the tiling. Edges are
        /// filed by midpoint rather than split per tile, since a clip doesn't stop the rasterizer walking
        /// the full bounding box (splitting measured two to four times slower).</param>
        public void Build(List<(Rect from, Rect to)> pairs, Size extent)
        {
            var cols = Math.Clamp((int)Math.Ceiling(extent.Width / TileSize), 1, MaxTiles);
            var rows = Math.Clamp((int)Math.Ceiling(extent.Height / TileSize), 1, MaxTiles);
            double tw = Math.Max(1, extent.Width) / cols, th = Math.Max(1, extent.Height) / rows;

            var tiles = new List<(Rect from, Rect to)>?[cols * rows];
            foreach (var pair in pairs)
            {
                var mx = (pair.from.X + pair.from.Width / 2 + pair.to.X + pair.to.Width / 2) / 2;
                var my = (pair.from.Y + pair.from.Height / 2 + pair.to.Y + pair.to.Height / 2) / 2;
                var c = Math.Clamp((int)(mx / tw), 0, cols - 1);
                var r = Math.Clamp((int)(my / th), 0, rows - 1);
                (tiles[r * cols + c] ??= new List<(Rect, Rect)>()).Add(pair);
            }

            var lines = new List<Geometry>();
            var heads = new List<Geometry>();
            foreach (var tile in tiles)
            {
                if (tile is null) continue;
                var line = new StreamGeometry();
                var head = new StreamGeometry();
                using (var lc = line.Open())
                using (var hc = head.Open())
                    foreach (var (from, to) in tile)
                        WriteEdge(lc, hc, from, to);
                // Frozen: the render thread takes a frozen geometry straight, with no per-frame
                // marshalling back to the UI thread.
                line.Freeze();
                head.Freeze();
                lines.Add(line);
                heads.Add(head);
            }

            _lines = lines.ToArray();
            _heads = heads.ToArray();
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            for (var i = 0; i < _lines.Length; i++)
            {
                dc.DrawGeometry(null, EdgePen, _lines[i]);
                dc.DrawGeometry(ArrowBrush, null, _heads[i]);
            }
        }
    }

    /// <summary>The Circles layout's wheel discs and guide rings, as three frozen geometries.</summary>
    private sealed class WheelLayer : FrameworkElement
    {
        private readonly NodeTheme _t;
        // Two disc groups because the discs come in two opacities, each drawn inside a PushOpacity.
        private readonly GeometryGroup _full = new(), _bare = new(), _rings = new();

        public WheelLayer(NodeTheme theme)
        {
            _t = theme;
            IsHitTestVisible = false;
        }

        /// <summary>Same reasoning as <see cref="EdgeLayer.HitTestCore(PointHitTestParameters)"/>:
        /// canvas-wide content, nothing on it to click.</summary>
        protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) => null;

        protected override GeometryHitTestResult? HitTestCore(GeometryHitTestParameters hitTestParameters) => null;

        public void AddDisc(double cx, double cy, double radius, bool hasRings) =>
            (hasRings ? _full : _bare).Children.Add(new EllipseGeometry(new Point(cx, cy), radius, radius));

        public void AddRing(double cx, double cy, double radius) =>
            _rings.Children.Add(new EllipseGeometry(new Point(cx, cy), radius, radius));

        /// <summary>Called once the layout has finished adding wheels.</summary>
        public void Freeze()
        {
            _full.Freeze();
            _bare.Freeze();
            _rings.Freeze();
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            Discs(_full, 0.6);
            Discs(_bare, 0.35);
            if (_rings.Children.Count == 0) return;
            dc.PushOpacity(0.5);
            dc.DrawGeometry(null, _t.RingPen, _rings);
            dc.Pop();

            void Discs(GeometryGroup g, double opacity)
            {
                if (g.Children.Count == 0) return;
                dc.PushOpacity(opacity);
                dc.DrawGeometry(_t.DiscFill, _t.DiscPen, g);
                dc.Pop();
            }
        }
    }
}
