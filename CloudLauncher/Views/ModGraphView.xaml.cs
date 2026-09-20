using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>
/// The Graph sub-tab. Lays installed mods out on a zoomable, pannable canvas — clustered by
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

    /// <summary>How many nodes get their contents built per turn of the dispatcher, and how many
    /// dependency lines. Sized so a chunk is a few milliseconds of work on a slow machine: small
    /// enough that input and rendering still get in between, big enough that a 1900-mod pack is not
    /// a hundred round-trips through the dispatcher queue.</summary>
    private const int NodeChunk = 100, EdgeChunk = 250;

    private Guid _packId;
    private IReadOnlyList<PackMod> _mods = Array.Empty<PackMod>();
    private Window? _owner;
    private Action<PackMod>? _onOpenMod;
    private Action? _onReload;
    private Action? _onModsChanged; // lightweight "flags changed" signal (marks other views stale)
    private Action<IReadOnlyList<PackMod>>? _onUpdate;
    private Action<IReadOnlyList<PackMod>>? _onRecheckUpdates;

    private readonly Dictionary<PackMod, Rect> _rect = new();
    private readonly Dictionary<PackMod, Border> _nodeEls = new();
    private readonly List<UIElement> _lineEls = new();
    private ModGraph? _graph;
    private bool _userInteracted; // once true, we stop auto-fitting so the user's zoom/pan sticks
    private bool _everLaidOut;

    /// <summary>Bumped by every <see cref="Rebuild"/>. A build in flight compares against it after
    /// each chunk and abandons itself the moment a newer one starts.</summary>
    private int _buildGen;

    /// <summary>True while a chunked build is still filling nodes or drawing lines. Dragging is held
    /// off until it clears: a drag redraws every edge, and doing that against a half-built canvas
    /// would fight the build for the same children collection.</summary>
    private bool _building;

    /// <summary>Nodes placed by the layout whose contents have not been built yet. Replaced (never
    /// cleared in place) on each rebuild, so an abandoned build keeps iterating its own list safely.</summary>
    private List<(PackMod mod, Border host)> _pendingNodes = new();

    /// <summary>Current text in the find box, lower-cased; empty means "no find is active".</summary>
    private string _findQuery = "";

    // interaction state
    private bool _panning;
    private Point _panStart;
    private double _panOrigX, _panOrigY;
    private Border? _dragNode;
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
        public readonly List<(PackMod mod, Border node)> Nodes = new();
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

    /// <param name="onUpdate">Installs the newest version of the given mods — the List view's own
    /// implementation, so "Update to newest" behaves identically here.</param>
    /// <param name="onRecheckUpdates">Re-runs the update check for the given mods after something
    /// changed what counts as an update for them (their channel, or the store they follow).</param>
    public void Load(Guid packId, IReadOnlyList<PackMod> mods, Window? owner,
        Action<PackMod>? onOpenMod = null, Action? onReload = null, Action? onModsChanged = null,
        Action<IReadOnlyList<PackMod>>? onUpdate = null, Action<IReadOnlyList<PackMod>>? onRecheckUpdates = null)
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
        Rebuild(refit: first);
    }

    /// <summary>
    /// Puts the toolbar back the way this launcher last left it, falling back to the pack's own
    /// defaults.
    /// </summary>
    /// <remarks>
    /// The pack's <see cref="ModAdvancedSettings"/> are written into its <c>mods.json</c> and synced
    /// to collaborators, so they are the pack's defaults, not this person's view preference —
    /// turning the lines off to read one crowded graph must not change what everyone else opens.
    /// The per-launcher <see cref="AppSettings.GraphShowDependencyLines"/> /
    /// <see cref="AppSettings.GraphClusterMode"/> hold the view state; the pack supplies the value
    /// the first time, and an unrecognised stored cluster mode (written by a newer build) falls back
    /// to the pack's too. Set while <c>_suppressToolbar</c> is up so restoring the controls does not
    /// fire their handlers and save the values straight back.
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

    // The ComboBox lists the modes in reading order (categories, layers, circles, free), which is not
    // the order ModClusterMode happens to declare them in — so the mapping is spelled out both ways
    // rather than cast through the index.
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

    /// <summary>Called by the hub the instant the Graph tab is selected, before the build is even
    /// scheduled, so the tab has something honest on it rather than the previous pack's canvas or
    /// an empty box.</summary>
    public void ShowBuilding()
    {
        if (!IsInitialized) return;
        GraphStatus.Text = "Laying out…";
    }

    // ── rebuild / layout ────────────────────────────────────────────────────────

    /// <summary>
    /// Re-lays the graph out. The positions are computed and the node frames placed synchronously —
    /// that part is arithmetic and a rectangle each — and everything expensive (each node's icon,
    /// labels, markers and tooltip, then every dependency line) is filled in afterwards in chunks by
    /// <see cref="FinishBuildAsync"/>, yielding the UI thread between them.
    /// </summary>
    /// <remarks>
    /// Building all of it inline is what made opening this tab on a large pack look like the
    /// launcher had hung: ~1900 nodes is tens of thousands of visuals, and the thread does not come
    /// back until the last one exists. Splitting it this way means the canvas appears immediately as
    /// a field of node-shaped frames that fill in over the next few hundred milliseconds, and the
    /// window stays responsive throughout.
    /// </remarks>
    private void Rebuild(bool refit = true)
    {
        if (!IsInitialized) return;
        var gen = ++_buildGen;   // abandons any build still running for the previous layout
        GraphCanvas.Children.Clear();
        _rect.Clear();
        _nodeEls.Clear();
        _lineEls.Clear();
        _clusters.Clear();
        _pendingNodes = new List<(PackMod, Border)>();

        HintLabel.Text = IsCustomMode
            ? "Drag nodes to arrange · scroll to zoom · drag background to pan"
            : IsCategoryMode
                ? "Drag a category header to move it · right-click it to rename · scroll to zoom"
                : IsCirclesMode
                    ? "Each dependency sits in the middle of the mods that need it · scroll to zoom · drag background to pan"
                    : "Scroll to zoom · drag background to pan";

        if (_mods.Count == 0)
        {
            GraphStatus.Text = "No mods to graph.";
            GraphCanvas.Width = GraphCanvas.Height = 0;
            _building = false;
            return;
        }

        var size = ClusterBox.SelectedIndex switch
        {
            1 => LayoutDependencies(),
            2 => LayoutCircles(),
            3 => LayoutCustom(),
            _ => LayoutCategories()
        };

        _graph = ModGraph.Build(_mods);
        GraphCanvas.Width = Math.Max(size.w, 50);
        GraphCanvas.Height = Math.Max(size.h, 50);

        RefreshCollapseButton();

        // Only recentre on a full (re)layout — never on a mod edit, so the user's view is preserved.
        // Fit from the frames, before the contents exist: the extent is already final.
        if (refit) { _userInteracted = false; TryFit(); }

        _ = FinishBuildAsync(gen, _pendingNodes);
    }

    /// <summary>Fills in the placed nodes and then the dependency lines, a chunk per turn of the
    /// dispatcher. Abandons itself as soon as a newer <see cref="Rebuild"/> has started.</summary>
    private async Task FinishBuildAsync(int gen, List<(PackMod mod, Border host)> pending)
    {
        _building = true;
        try
        {
            for (var i = 0; i < pending.Count; i += NodeChunk)
            {
                var end = Math.Min(i + NodeChunk, pending.Count);
                for (var k = i; k < end; k++) FillNode(pending[k].mod, pending[k].host);

                if (end < pending.Count)
                {
                    GraphStatus.Text = $"Drawing {end} of {pending.Count} mods…";
                    // Background priority: input, layout and render all outrank an empty callback
                    // queued there, so awaiting one hands the thread back to the window and resumes
                    // only once it has caught up.
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                    if (gen != _buildGen) return;
                }
            }

            var edges = CollectEdges();
            var visible = ShowLines.IsChecked == true;
            for (var i = 0; i < edges.Count; i += EdgeChunk)
            {
                var end = Math.Min(i + EdgeChunk, edges.Count);
                for (var k = i; k < end; k++) AddEdge(edges[k].from, edges[k].to, visible);

                if (end < edges.Count)
                {
                    GraphStatus.Text = $"Drawing {end} of {edges.Count} dependency lines…";
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                    if (gen != _buildGen) return;
                }
            }

            // The checkbox may have been flipped between chunks, so settle every line on the state it
            // is in now rather than the one each chunk captured.
            SetLinesVisible(ShowLines.IsChecked == true);

            GraphStatus.Text = $"{_mods.Count} mod(s)" + (edges.Count > 0 ? $"  ·  {edges.Count} link(s)" : "");
            // A find typed while the graph was building still applies to it. Only when one is active:
            // ApplyFind writes its own status line, and on a fresh build with no query every node is
            // already in its base state.
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

            // Inside a cluster the mods are split into content-size bands — largest on the left —
            // each band its own block of columns with a vertical rule between them. The bands are
            // self-identifying from the meter on each node, so no extra labels are needed.
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
                    cv.Nodes.Add((band[i], Place(band[i], nx, ny)));
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
        var graph = ModGraph.Build(_mods);
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

        // Barycenter ordering: a few sweeps pulling each node next to its connected neighbours,
        // which keeps dependency lines short and stops them slicing across unrelated mods.
        var index = new Dictionary<PackMod, int>();
        void Reindex() { foreach (var lay in order.Values) for (var i = 0; i < lay.Count; i++) index[lay[i]] = i; }
        Reindex();
        for (var iter = 0; iter < 4; iter++)
        {
            for (var d = 0; d <= maxLayer; d++)
            {
                var lay = order[d];
                if (lay.Count < 2) continue;
                lay.Sort((a, b) => Bary(a).CompareTo(Bary(b)));
                Reindex();
            }
        }

        double Bary(PackMod m)
        {
            double sum = 0; var count = 0;
            foreach (var nb in graph.DependenciesOf(m).Concat(graph.DependentsOf(m)))
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
    /// Radial dependency clusters. Every mod that something else needs becomes the hub of a wheel,
    /// with the mods that need it on rings around it — the dependency group sits right next to its
    /// dependents instead of being strung out along the layered rows of the Dependencies layout. A
    /// mod that needs several hubs lives in the wheel of the one with the most dependents; its
    /// other lines simply cross wheels. Hubs never sit on someone else's ring (they have a wheel of
    /// their own, however small), and mods with no dependency relationship at all are gathered in a
    /// plain block at the end. Wheels are shelf-packed into a roughly square area so the whole thing
    /// fits the viewport at a readable zoom.
    /// </summary>
    private (double w, double h) LayoutCircles()
    {
        const double slot = NodeW + 16;      // arc length reserved per node on a ring
        const double ringGap = NodeW + 20;   // rings must clear a node's WIDTH, since nodes are wide and axis-aligned
        // A ring node near the horizontal sits beside the hub at the same height, so the first ring has to
        // clear a full node WIDTH (184) plus a margin, not just a height — at 170 the nodes at 3 and 9
        // o'clock overlapped the hub.
        const double firstRadius = 205;
        const double wheelPad = 26;
        const double wheelGap = 46;

        var graph = ModGraph.Build(_mods);
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
    private void AddWheelDisc(double cx, double cy, double radius, bool hasRings)
    {
        var disc = new Ellipse
        {
            Width = radius * 2, Height = radius * 2,
            Fill = Res("Surface2Brush"), Stroke = Res("BorderSubtleBrush"), StrokeThickness = 1,
            Opacity = hasRings ? 0.6 : 0.35, IsHitTestVisible = false
        };
        Canvas.SetLeft(disc, cx - radius);
        Canvas.SetTop(disc, cy - radius);
        Panel.SetZIndex(disc, -1);
        GraphCanvas.Children.Add(disc);
    }

    /// <summary>A thin guide circle through a ring's node centres.</summary>
    private void AddWheelRing(double cx, double cy, double radius)
    {
        var ring = new Ellipse
        {
            Width = radius * 2, Height = radius * 2,
            Stroke = Res("BorderStrongBrush"), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 6 },
            Opacity = 0.5, IsHitTestVisible = false
        };
        Canvas.SetLeft(ring, cx - radius);
        Canvas.SetTop(ring, cy - radius);
        Panel.SetZIndex(ring, 0);
        GraphCanvas.Children.Add(ring);
    }

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

    // ── edges ──────────────────────────────────────────────────────────────────

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
                // Arrow points FROM the dependency TO the mod that needs it (dep → dependent).
                pairs.Add((rb, ra));
            }
        }
        return pairs;
    }

    private void DrawEdges()
    {
        var visible = ShowLines.IsChecked == true;
        foreach (var (from, to) in CollectEdges()) AddEdge(from, to, visible);
    }

    /// <param name="visible">Whether the line starts out shown. Passed in rather than read per edge
    /// so a chunked build cannot leave half the lines in the wrong state if the checkbox is flipped
    /// mid-build — <see cref="OnToggleLines"/> sweeps the whole collection afterwards anyway.</param>
    private void AddEdge(Rect from, Rect to, bool visible = true)
    {
        var ca = new Point(from.X + from.Width / 2, from.Y + from.Height / 2);
        var cb = new Point(to.X + to.Width / 2, to.Y + to.Height / 2);
        var start = BorderPoint(from, cb);
        var end = BorderPoint(to, ca);

        var dir = new Vector(end.X - start.X, end.Y - start.Y);
        var len = dir.Length;
        if (len < 2) return; // nodes overlapping — an edge here would just be noise

        var perp = new Vector(-dir.Y, dir.X);
        if (perp.Length > 0) perp.Normalize();
        // Straighten short edges — a big bow between two close nodes looks awful; bow longer ones gently.
        var offset = len < 90 ? 0 : Math.Min(34, len * 0.14);
        var ctrl = new Point((start.X + end.X) / 2 + perp.X * offset,
                             (start.Y + end.Y) / 2 + perp.Y * offset);

        var fig = new PathFigure { StartPoint = start, IsFilled = false };
        fig.Segments.Add(new QuadraticBezierSegment(ctrl, end, true));
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        var path = new Path
        {
            Data = geo, Stroke = EdgeBrush, StrokeThickness = 1.5, IsHitTestVisible = false,
            Visibility = visible ? Visibility.Visible : Visibility.Collapsed
        };
        Panel.SetZIndex(path, 0);
        GraphCanvas.Children.Add(path);
        _lineEls.Add(path);

        // Small, sleek arrowhead at the target end, scaled down on short edges.
        var arrowLen = Math.Clamp(len * 0.3, 4, 8);
        var arrowW = arrowLen * 0.5;
        var tan = offset == 0 ? dir : new Vector(end.X - ctrl.X, end.Y - ctrl.Y);
        if (tan.Length > 0) tan.Normalize();
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
            Fill = ArrowBrush,
            IsHitTestVisible = false,
            Visibility = visible ? Visibility.Visible : Visibility.Collapsed
        };
        Panel.SetZIndex(arrow, 0);
        GraphCanvas.Children.Add(arrow);
        _lineEls.Add(arrow);
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

    /// <summary>Re-draws every dependency line after a drag moved a node or a cluster. Synchronous
    /// on purpose — it has to keep up with the mouse — and only reachable once the chunked build has
    /// finished, since dragging is blocked while <c>_building</c> is set.</summary>
    private void RedrawEdges()
    {
        foreach (var e in _lineEls) GraphCanvas.Children.Remove(e);
        _lineEls.Clear();
        DrawEdges();
    }

    private void SetLinesVisible(bool visible)
    {
        foreach (var l in _lineEls) l.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── node + cluster visuals ──────────────────────────────────────────────────

    private Border AddClusterBox(double x, double y, double w, double h)
    {
        var box = new Border
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

    /// <summary>A category cluster's header strip — the grab handle and the fold control: the caret
    /// collapses the category, left-drag moves the whole cluster, double-click folds it, right-click
    /// renames it.</summary>
    private Border AddHeader(string text, double x, double y, double w, double headerH, double pad,
        ClusterVisual cv, Brush? tint = null, int memberCount = 0, bool isCollapsed = false)
    {
        var foreground = tint ?? Res("TextSecondaryBrush");

        // ▸ when folded, ▾ when open — the same affordance as every tree in the app.
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
        // Handle the press, not just the click: the header underneath starts a drag on mouse-down,
        // and a caret that moved the cluster instead of folding it would be maddening.
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

        var header = new Border
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
    private Border Place(PackMod m, double x, double y)
    {
        var node = CreateNodeFrame(m);
        Canvas.SetLeft(node, x);
        Canvas.SetTop(node, y);
        Panel.SetZIndex(node, 1);
        GraphCanvas.Children.Add(node);
        _rect[m] = new Rect(x, y, NodeW, NodeH);
        _nodeEls[m] = node;
        _pendingNodes.Add((m, node));
        return node;
    }

    private Border CreateNodeFrame(PackMod m)
    {
        var emphasized = PriorityPalette.IsEmphasized(m.Priority);
        var border = new Border
        {
            Width = NodeW, Height = NodeH, CornerRadius = new CornerRadius(8),
            Background = Res("Surface3Brush"),
            BorderBrush = BaseBorderFor(m),
            BorderThickness = new Thickness(emphasized ? 2 : 1),
            Cursor = Cursors.Hand,
            Opacity = BaseOpacityFor(m)
        };
        border.MouseLeftButtonDown += (_, e) => OnNodeDown(border, m, e);
        border.MouseMove += OnNodeMove;
        border.MouseLeftButtonUp += OnNodeUp;
        border.MouseRightButtonUp += (_, e) => OnNodeRightClick(border, m, e);
        return border;
    }

    /// <summary>The node's border when no find is running: its priority colour, or the plain one.</summary>
    private Brush BaseBorderFor(PackMod m) =>
        PriorityPalette.IsEmphasized(m.Priority) ? PriorityPalette.BorderFor(m.Priority) : Res("BorderStrongBrush");

    /// <summary>Disabled mods are drawn back, which is what a node's opacity means before the find
    /// box gets an opinion about it.</summary>
    private static double BaseOpacityFor(PackMod m) => m.Enabled ? 1.0 : 0.45;

    /// <summary>Builds a placed node's contents — the expensive half, run in chunks off the layout
    /// so a big pack's graph appears rather than arriving all at once several seconds late.</summary>
    private void FillNode(PackMod m, Border border)
    {
        border.ToolTip = new ToolTip
        {
            Content = new TextBlock { Text = m.TooltipText, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 }
        };

        var grid = new Grid { Margin = new Thickness(8, 0, 8, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconHost = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(6),
            Background = Res("Surface2Brush"), BorderBrush = Res("BorderBrush"), BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true
        };
        var iconGrid = new Grid();
        iconGrid.Children.Add(new TextBlock
        {
            Text = m.Initial, FontWeight = FontWeights.SemiBold, FontSize = 12,
            Foreground = Res("TextSecondaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
        if (ModIconCache.Image(m.IconUrl, 28) is { } icon) iconGrid.Children.Add(icon);
        iconHost.Child = iconGrid;
        Grid.SetColumn(iconHost, 0);
        grid.Children.Add(iconHost);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = m.DisplayName, FontWeight = FontWeights.SemiBold, FontSize = 13,
            Foreground = Res("TextPrimaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis
        });
        text.Children.Add(new TextBlock
        {
            Text = m.VersionLabel, FontSize = 11, Foreground = Res("TextSecondaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        // Same markers as the list and the planning board, so a noted or sized mod is recognisable
        // anywhere. Both share one column so they can't overlap.
        if (m.HasNote || m.ContentSize > 0)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var markers = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(6, 9, 0, 0)
            };
            markers.Children.Add(new ContentSizeMeter { Size = m.ContentSize, VerticalAlignment = VerticalAlignment.Center });
            if (m.HasNote)
                markers.Children.Add(new TextBlock
                {
                    Text = char.ConvertFromUtf32(0xE70B),
                    FontFamily = (FontFamily)FindResource("IconFont"),
                    FontSize = 11,
                    Foreground = Res("AccentBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(m.ContentSize > 0 ? 5 : 0, 0, 0, 0)
                });
            Grid.SetColumn(markers, 2);
            grid.Children.Add(markers);
        }

        border.Child = grid;
    }

    // ── node drag ────────────────────────────────────────────────────────────────

    private void OnNodeDown(Border node, PackMod mod, MouseButtonEventArgs e)
    {
        if (_building) return;   // a drag redraws every edge; let the build finish owning the canvas
        _dragNode = node;
        _dragMod = mod;
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
        // The graph can be rebuilt out from under a drag — a rebuild clears _rect, _nodeEls and the
        // canvas — and then this mod is no longer on it. That is not hypothetical: the node's own
        // context menu takes the mouse capture away, so the left-button-up never reaches OnNodeUp and
        // the drag fields stay set while "Delete" or "Disable" rebuilds the graph without this node.
        // Indexing _rect here would throw KeyNotFoundException out of a WPF mouse handler, which
        // closes the launcher rather than misdrawing one node. Leave this guard in place.
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

        // A click (not a drag) intentionally does nothing — the page is opened from the
        // right-click options menu ("Open page") instead.
        if (!dragged) return;
        if (IsCustomMode && _rect.TryGetValue(mod, out var r))
            App.State.ModMetadata.SetNodePosition(_packId, mod.Key, r.X, r.Y);
    }

    /// <summary>
    /// Right-click on a node: ends any drag still in flight, then opens the options menu for the node
    /// actually under the cursor.
    /// </summary>
    /// <remarks>
    /// <para>Two things go wrong if this simply calls <see cref="ShowOptions"/>. While a drag is
    /// running the dragged node holds the mouse capture, so a right-click <i>anywhere</i> on the graph
    /// is routed to that node — the menu would act on the mod being dragged rather than the one
    /// clicked. And opening the menu takes the capture for itself, so the matching left-button-up
    /// never reaches <see cref="OnNodeUp"/>: the drag fields stay set, and a menu item that rebuilds
    /// the graph (delete, disable, re-scan) leaves them pointing at a mod that is no longer a key in
    /// <c>_rect</c>, which the next mouse move used to turn into a crash.</para>
    /// <para>The try/catch is not decoration: an exception escaping a WPF event handler is unhandled
    /// and takes the whole launcher down, and this one is reached by an ordinary right-click.</para>
    /// </remarks>
    private void OnNodeRightClick(Border node, PackMod mod, MouseButtonEventArgs e)
    {
        e.Handled = true;
        try
        {
            var position = e.GetPosition(GraphCanvas);
            CancelNodeDrag();
            var target = NodeUnder(position) ?? (mod, node);
            ShowOptions(target.Item1, target.Item2);
        }
        catch (Exception ex) { GraphStatus.Text = "Could not open the menu: " + ex.Message; }
    }

    /// <summary>The node at a canvas point, or null when the point is not over one.</summary>
    /// <remarks>Used instead of the clicked element because during a drag every mouse event is routed
    /// to the captured node, whatever the cursor is actually over.</remarks>
    private (PackMod, Border)? NodeUnder(Point canvasPoint)
    {
        var hit = GraphCanvas.InputHitTest(canvasPoint) as DependencyObject;
        for (; hit is not null; hit = VisualTreeHelper.GetParent(hit))
        {
            if (hit is not Border border) continue;
            foreach (var pair in _nodeEls)
                if (ReferenceEquals(pair.Value, border)) return (pair.Key, border);
        }
        return null;
    }

    /// <summary>
    /// Ends a drag in flight, keeping wherever the node was dragged to, and drops every reference the
    /// drag was holding.
    /// </summary>
    /// <remarks>Clearing <c>_dragMod</c> is the point: it is a key into <c>_rect</c>, and a rebuild
    /// can retire it while the drag is still notionally running.</remarks>
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

    // ── category-cluster drag (Categories mode) ──────────────────────────────────

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
        foreach (var (mod, node) in cv.Nodes)
        {
            double nl = Canvas.GetLeft(node) + dx, nt = Canvas.GetTop(node) + dy;
            Canvas.SetLeft(node, nl); Canvas.SetTop(node, nt);
            _rect[mod] = new Rect(nl, nt, NodeW, NodeH);
        }
        // grow the canvas so a cluster dragged past the edge isn't clipped (and Fit still includes it)
        GraphCanvas.Width = Math.Max(GraphCanvas.Width, nx + cv.W + Origin);
        GraphCanvas.Height = Math.Max(GraphCanvas.Height, ny + cv.H + Origin);
    }

    // ── folding categories (Categories mode) ─────────────────────────────────

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
            GraphStatus.Text = "“Uncategorized” isn't a real category.";
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

    // ── pan / zoom ────────────────────────────────────────────────────────────────

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
    }

    /// <summary>Right-click on empty graph background — the pack-level actions, plus the toolbar
    /// controls so they're reachable without travelling back up to the top of the page. Right-clicks
    /// on a node or a cluster header are handled there and never reach this.</summary>
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
    }

    private void OnFit(object sender, RoutedEventArgs e)
    {
        _userInteracted = false; // re-enable auto-fit, then fit now
        FitToView();
    }

    // Auto-fit/centre until the user first pans, zooms or drags — this also makes the initial
    // centring robust against the tab-transition (it re-fits as the viewport settles its size).
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
        ZoomT.ScaleX = ZoomT.ScaleY = s;
        PanT.X = (vw - GraphCanvas.Width * s) / 2;
        PanT.Y = (vh - GraphCanvas.Height * s) / 2;
    }

    // ── interactions ────────────────────────────────────────────────────────────

    private void OpenPage(PackMod m)
    {
        if (_onOpenMod is not null) { _onOpenMod(m); return; }
        if (m.PageUrl is null) return;
        try { Process.Start(new ProcessStartInfo(m.PageUrl) { UseShellExecute = true }); } catch { }
    }

    private async void UpdateNodeToVersion(PackMod mod)
    {
        if (mod.PrimaryMod is null || _owner is not MainWindow host) return;
        GraphStatus.Text = $"Loading versions for {mod.DisplayName}…";
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

        GraphStatus.Text = $"Installing {chosen.Version.VersionNumber}…";
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
    /// The mod options menu, with every callback the List view's own menu has — so right-clicking a
    /// mod offers the same actions whichever tab it is clicked in.
    /// </summary>
    /// <remarks>
    /// "Update to newest" and the update re-check used to be absent here simply because this context
    /// left their callbacks null, and <see cref="ModOptionsMenu"/> hides an item whose callback is
    /// missing — so the same mod quietly offered a different menu depending on the tab. Updating and
    /// re-checking are handed up to the hub rather than re-implemented, because the lock rules and
    /// the re-check behaviour must not be able to drift between tabs.
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
                GraphStatus.Text = list.Count == 1 ? $"Updating {list[0].DisplayName}…" : $"Updating {list.Count} mod(s)…";
                _onUpdate(list);   // re-scans when it finishes, which rebuilds this graph
            },
            OnUpdateToVersion = UpdateNodeToVersion,
            OnRecheckUpdates = list =>
            {
                if (_onRecheckUpdates is null) return;
                GraphStatus.Text = "Re-checking for updates…";
                _onRecheckUpdates(list);
            },
            OnDelete = DeleteNodesFromGraph,
            OnReveal = mm =>
            {
                var dir = System.IO.Path.GetDirectoryName(mm.FilePath);
                if (dir is null) return;
                try { Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true }); }
                catch (Exception ex) { GraphStatus.Text = "Could not open the folder: " + ex.Message; }
            }
        };
        var menu = ModOptionsMenu.Build(m, ctx);
        menu.PlacementTarget = anchor;
        menu.IsOpen = true;
    }

    private Brush Res(string key) => (Brush)FindResource(key);

    // ── toolbar ──────────────────────────────────────────────────────────────────

    private void OnClusterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressToolbar) return;
        // Remember how this person likes to look at a graph (see RestoreToolbarState for why this is
        // a launcher setting rather than the pack's).
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

    // ── find ─────────────────────────────────────────────────────────────────────

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
    /// Dimming rather than hiding, because a node's position is the information here: pulling the
    /// non-matches out would rearrange the very clusters the user is reading the graph for. A single
    /// match is scrolled to as well, since on a 1900-mod pack the one lit node is very likely off
    /// screen at the zoom the graph opens at.
    /// </remarks>
    private void ApplyFind()
    {
        if (_nodeEls.Count == 0) return;

        if (_findQuery.Length == 0)
        {
            foreach (var (mod, node) in _nodeEls)
            {
                node.Opacity = BaseOpacityFor(mod);
                node.BorderBrush = BaseBorderFor(mod);
            }
            GraphStatus.Text = $"{_mods.Count} mod(s)";
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
            node.BorderBrush = hit ? accent : BaseBorderFor(mod);
        }

        GraphStatus.Text = hits switch
        {
            0 => $"No mod matches “{_findQuery}”",
            1 => $"1 match  ·  {_mods.Count} mod(s)",
            _ => $"{hits} matches  ·  {_mods.Count} mod(s)"
        };

        if (hits == 1 && only is not null) CenterOn(only);
    }

    private bool Matches(PackMod m) =>
        m.DisplayName.Contains(_findQuery, StringComparison.OrdinalIgnoreCase)
        || m.FileName.Contains(_findQuery, StringComparison.OrdinalIgnoreCase);

    /// <summary>Pans (without zooming) so a node sits in the middle of the viewport.</summary>
    private void CenterOn(PackMod mod)
    {
        if (!_rect.TryGetValue(mod, out var r) || Viewport.ActualWidth < 10) return;
        _userInteracted = true;   // a deliberate move: don't let a later auto-fit undo it
        var s = ZoomT.ScaleX;
        PanT.X = Viewport.ActualWidth / 2 - (r.X + r.Width / 2) * s;
        PanT.Y = Viewport.ActualHeight / 2 - (r.Y + r.Height / 2) * s;
    }
}
