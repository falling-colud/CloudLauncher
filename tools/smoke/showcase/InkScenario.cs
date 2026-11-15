using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CloudLauncher;
using CloudLauncher.Services;
using CloudLauncher.Shared;
using CloudLauncher.Views;

namespace Showcase;

/// <summary>
/// Drawing on a planning board, through the view's own code (reflection on its private gesture methods,
/// fed with stylus points the way a pen delivers them): pen strokes with pressure, the highlighter, zoom,
/// undo, erasing parts of lines and whole lines, lasso and tap selection, moving, recolouring, copy and
/// paste, keys, the saved file, merging two PCs' drawings, an older launcher's round trip, and pictures.
/// Results are PASS/FAIL lines in the log; the last line counts the failures.
/// </summary>
/// <remarks>What this cannot reach is the stylus plumbing itself (a StylusDevice can't be made without a
/// digitizer): those handlers only translate WPF's stylus events into the calls made here.</remarks>
public static class InkScenario
{
    private const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
    private static ModPlanView _view = null!;
    private static int _fails;

    private static object? Call(string m, params object?[] a)
    {
        var method = typeof(ModPlanView).GetMethods(F).Single(x => x.Name == m && x.GetParameters().Length == a.Length);
        return method.Invoke(_view, a);
    }

    private static TF Field<TF>(string f) => (TF)typeof(ModPlanView).GetField(f, F)!.GetValue(_view)!;

    private static object Kind(string type, string value) =>
        Enum.Parse(typeof(ModPlanView).GetNestedType(type, BindingFlags.NonPublic)!, value);

    private static void Check(string name, bool ok, string detail = "")
    {
        if (!ok) _fails++;
        Program.Log($"{(ok ? "PASS" : "FAIL")}  {name} {(ok ? "" : detail)}");
    }

    private static PlanBoard Board => Field<PlanBoard>("_board");
    private static List<PlanStroke> Strokes => Board.Strokes ?? new();
    private static ScaleTransform ZoomT => (ScaleTransform)_view.FindName("ZoomT");
    private static TranslateTransform PanT => (TranslateTransform)_view.FindName("PanT");
    private static double Zoom => ZoomT.ScaleX;

    private static Point Screen(double bx, double by) => new(bx * Zoom + PanT.X, by * Zoom + PanT.Y);

    private static StylusPointCollection Pts(IEnumerable<(double X, double Y, float P)> board)
    {
        var c = new StylusPointCollection();
        foreach (var (x, y, p) in board)
        {
            var s = Screen(x, y);
            c.Add(new StylusPoint(s.X, s.Y, p));
        }
        return c;
    }

    /// <summary>One gesture through board points, a few points per event as a pen sends them.</summary>
    private static void Gesture(string tool, string device, IList<(double X, double Y, float P)> path)
    {
        var first = path[0];
        Call("BeginInkGesture", Kind("InkTool", tool), Kind("InkDevice", device), 7, Screen(first.X, first.Y),
            Pts([first]));
        for (var i = 1; i < path.Count; i += 4) Call("InkGestureMove", Pts(path.Skip(i).Take(4)));
        Call("EndInkGesture", false);
    }

    private static List<(double, double, float)> Line(double x0, double y0, double x1, double y1, int n, float p = 0.5f) =>
        Enumerable.Range(0, n).Select(i => (x0 + (x1 - x0) * i / (n - 1), y0 + (y1 - y0) * i / (n - 1), p)).ToList();

    private static List<(double, double, float)> Circle(double cx, double cy, double r, int n) =>
        Enumerable.Range(0, n + 1).Select(i =>
        {
            var a = 2 * Math.PI * i / n - 0.4;
            return (cx + r * Math.Cos(a) * 1.15, cy + r * Math.Sin(a), (float)(0.45 + 0.4 * Math.Sin(i * 0.3)));
        }).ToList();

    private static List<InkPoint> Decode(PlanStroke s) => PlanInkCodec.Decode(s.Points);

    public static void Run()
    {
        var shell = Program.MakeShell(1280, 820);
        System.Threading.SynchronizationContext.SetSynchronizationContext(
            new System.Windows.Threading.DispatcherSynchronizationContext(shell.Dispatcher));
        shell.Show();
        Program.Pump(1500);

        // A small board: a section with two notes, a loose note.
        var board = new PlanBoard { Name = "Drawing", SectionMembershipSet = true };
        void Add(string id, PlanNodeKind kind, double x, double y, double w, double h, string title, string? sec = null, string body = "") =>
            board.Nodes.Add(new PlanNode { Id = id, Kind = kind, X = x, Y = y, W = w, H = h, Title = title, SectionId = sec, Body = body });
        Add("sec", PlanNodeKind.Section, 24, 24, 552, 240, "Tech mods");
        Add("n1", PlanNodeKind.Note, 48, 72, 240, 0, "Create first", "sec", "Then the addons that need it.");
        Add("n2", PlanNodeKind.Note, 312, 72, 240, 0, "Storage", "sec", "Pick one: AE2 or RS.");
        Add("n3", PlanNodeKind.Note, 648, 72, 240, 0, "Ask Mysticpasta", body: "Which shaders run on low-end laptops?");
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
        ZoomT.ScaleX = ZoomT.ScaleY = 1;
        PanT.X = PanT.Y = 0;
        Program.Pump(100);

        var settings = App.State.Settings;
        settings.PlanInkPenSize = 1;
        settings.PlanInkHighlighterSize = 1;
        settings.PlanInkEraserSize = 1;
        settings.PlanInkEraseWholeStrokes = false;
        settings.PlanInkPenColor = null;
        settings.PlanInkHighlighterColor = "#E3B341";
        settings.PlanInkHidden = false;

        var surface = (PlanInkSurface)_view.FindName("InkSurface");
        var palette = (FrameworkElement)_view.FindName("InkPalette");

        // ── draw mode ──
        Check("drawing off at first: the surface lets presses through", !surface.IsHitTestVisible);
        Call("SetDrawMode", true);
        Program.Pump(200);
        Check("drawing on: the surface takes presses and the palette shows",
            surface.IsHitTestVisible && palette.Visibility == Visibility.Visible);

        // ── a pen stroke with pressure ──
        var wave = Enumerable.Range(0, 80).Select(i =>
            (60.0 + i * 5, 330 + 18 * Math.Sin(i / 6.0), (float)(0.2 + 0.7 * i / 79.0))).ToList();
        Call("SetInkTool", Kind("InkTool", "Pen"));
        Gesture("Pen", "Pen", wave);
        Check("a pen stroke lands on the board", Strokes.Count == 1, $"count={Strokes.Count}");
        var s1 = Strokes[0];
        var p1 = Decode(s1);
        Check("its width is the medium pen at 100%", Math.Abs(s1.Width - 4) < 1e-6, $"width={s1.Width}");
        Check("default ink is the look's text colour (stored as no colour)", s1.Color is null, $"color={s1.Color}");
        Check("points thinned but the line kept", p1.Count is > 4 and < 80, $"points={p1.Count}");
        Check("pressure kept (light start, firm end)", p1.Min(p => p.Pressure) < 0.3 && p1.Max(p => p.Pressure) > 0.8,
            $"min={p1.Min(p => p.Pressure):0.00} max={p1.Max(p => p.Pressure):0.00}");
        Check("ends where the pen started and lifted", Math.Abs(p1[0].X - 60) < 0.11 && Math.Abs(p1[^1].X - 455) < 0.11,
            $"{p1[0].X},{p1[^1].X}");

        // A ruler-straight line keeps only its ends.
        Gesture("Pen", "Mouse", Line(60, 400, 500, 400, 100));
        var straight = Strokes[^1];
        Check("a straight line keeps two points", Decode(straight).Count == 2, $"points={Decode(straight).Count}");

        // ── the highlighter ──
        Call("SetInkTool", Kind("InkTool", "Highlighter"));
        Gesture("Highlighter", "Pen", Line(660, 92, 860, 92, 40, 0.9f));
        var hl = Strokes[^1];
        Check("highlighter stroke: see-through flag, its colour and width", hl.Highlighter && hl.Color == "#E3B341"
            && Math.Abs(hl.Width - 20) < 1e-6, $"hl={hl.Highlighter} color={hl.Color} width={hl.Width}");
        Check("highlighter ignores pressure (stored even)", Decode(hl).All(p => Math.Abs(p.Pressure - 0.5f) < 0.01));

        // ── zoomed in, the same pen is finer on the board ──
        Call("SetInkTool", Kind("InkTool", "Pen"));
        ZoomT.ScaleX = ZoomT.ScaleY = 2;
        Gesture("Pen", "Pen", Line(700, 200, 760, 230, 20, 0.5f));
        var zoomed = Strokes[^1];
        Check("drawn at 200%, the width halves in board units", Math.Abs(zoomed.Width - 2) < 1e-6, $"width={zoomed.Width}");
        Check("...and the points land in board units", Math.Abs(Decode(zoomed)[0].X - 700) < 0.11, $"x={Decode(zoomed)[0].X}");
        ZoomT.ScaleX = ZoomT.ScaleY = 1;
        Program.Pump(100);

        // ── undo / redo ──
        var count = Strokes.Count;
        Call("Undo");
        Check("undo takes the last stroke away", Strokes.Count == count - 1, $"count={Strokes.Count}");
        Call("Redo");
        Check("redo brings it back, the same object", Strokes.Count == count && ReferenceEquals(Strokes[^1], zoomed));
        var history = Field<System.Collections.IList>("_history");
        Check("each stroke was one undo step", history.Count >= 5, $"steps={history.Count}");

        // ── erasing part of a line ──
        var before = Strokes.Count;
        var straightId = straight.Id;
        Gesture("Eraser", "Pen", Line(280, 370, 280, 430, 12));
        var pieces = Strokes.Where(s => Decode(s).All(p => Math.Abs(p.Y - 400) < 1)).ToList();
        Check("the eraser cut the straight line in two", Strokes.Count == before + 1 && pieces.Count == 2
            && Strokes.All(s => s.Id != straightId), $"count {before}->{Strokes.Count}, pieces={pieces.Count}");
        Check("the two pieces end either side of the cut", pieces.Any(s => Decode(s).Max(p => p.X) < 280)
            && pieces.Any(s => Decode(s).Min(p => p.X) > 280),
            string.Join(" | ", pieces.Select(s => $"{Decode(s).Min(p => p.X):0}-{Decode(s).Max(p => p.X):0}")));
        Call("Undo");
        Check("one undo brings the whole line back", Strokes.Any(s => s.Id == straightId) && Strokes.Count == before);

        // ── erasing whole lines ──
        settings.PlanInkEraseWholeStrokes = true;
        Gesture("Eraser", "Mouse", Line(400, 370, 400, 430, 8));
        Check("with 'whole lines' the eraser takes the line it touches", Strokes.All(s => s.Id != straightId)
            && Strokes.Count == before - 1, $"count={Strokes.Count}");
        settings.PlanInkEraseWholeStrokes = false;
        Call("Undo");

        // ── the eraser misses what it doesn't touch ──
        var snapshot = Strokes.ToList();
        Gesture("Eraser", "Pen", Line(950, 500, 1000, 520, 6));
        Check("erasing empty board changes nothing", Strokes.SequenceEqual(snapshot));

        // ── lasso, move, recolour, copy, paste, delete ──
        Call("SetInkTool", Kind("InkTool", "Select"));
        var lassoPath = new List<(double, double, float)>();
        for (var i = 0; i <= 40; i++)
        {
            var a = 2 * Math.PI * i / 40;
            lassoPath.Add((280 + 260 * Math.Cos(a), 365 + 90 * Math.Sin(a), 0.5f));
        }
        Gesture("Select", "Pen", lassoPath);
        var selected = Field<HashSet<string>>("_inkSelected");
        var wavy = Strokes.First(s => s.Id == s1.Id);
        Check("the lasso picks the wave and the straight line, not the rest",
            selected.Contains(s1.Id) && selected.Contains(straightId) && !selected.Contains(hl.Id) && selected.Count == 2,
            $"selected={selected.Count}");

        var waveFirst = Decode(wavy)[0];
        Gesture("Select", "Pen", Line(300, 400, 350, 430, 10)); // a press inside the selection drags it
        var movedWave = Strokes.First(s => s.Id == s1.Id);
        var movedFirst = Decode(movedWave)[0];
        Check("dragging the selection moves its lines, ids kept",
            Math.Abs(movedFirst.X - waveFirst.X - 50) < 0.11 && Math.Abs(movedFirst.Y - waveFirst.Y - 30) < 0.11
            && selected.Count == 2, $"moved by {movedFirst.X - waveFirst.X:0.0},{movedFirst.Y - waveFirst.Y:0.0}");
        Check("...the pressure travels with them", Math.Abs(movedFirst.Pressure - waveFirst.Pressure) < 0.01);

        Call("PickInkColor", "#5B9DF9");
        Check("a colour picked with lines selected recolours them", selected.Count == 2
            && Strokes.Where(s => selected.Contains(s.Id)).All(s => s.Color == "#5B9DF9")
            && Strokes.First(s => s.Id == hl.Id).Color == "#E3B341");
        Call("PickInkSize", 2);
        Check("a width picked with lines selected re-sizes them", selected.Count == 2
            && Strokes.Where(s => selected.Contains(s.Id)).All(s => Math.Abs(s.Width - 8) < 1e-6));

        Call("CopySelectedStrokes");
        var beforePaste = Strokes.Count;
        var clip = (List<PlanStroke>?)typeof(ModPlanView).GetField("_inkClipboard", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) ?? new();
        Check("copy put the selected lines on the clipboard", clip.Count == 2, $"clip={clip.Count}");
        if (clip.Count > 0) Call("PasteStrokes", clip);
        var pasted = Strokes.Skip(beforePaste).ToList();
        Check("paste adds copies with new ids, a cell down and right, selected",
            pasted.Count == 2 && pasted.All(p => clip.All(c => c.Id != p.Id)) && selected.SetEquals(pasted.Select(p => p.Id))
            && Math.Abs(Decode(pasted[0])[0].X - Decode(clip[0])[0].X - 24) < 0.11);
        Call("DeleteSelectedStrokes");
        Check("delete removes the selected lines", Strokes.Count == beforePaste && selected.Count == 0);

        // A tap on a line selects just that one.
        Gesture("Select", "Mouse", [(700.0, 93.0, 0.5f)]);
        Check("a tap selects the line under it", selected.Count == 1 && selected.Contains(hl.Id), $"selected={selected.Count}");

        // ── keys ──
        bool Press(Key k)
        {
            var e = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(_view)!, 0, k) { RoutedEvent = Keyboard.KeyDownEvent };
            return (bool)Call("HandleInkKey", e)!;
        }
        Check("Esc first drops the selection", Press(Key.Escape) && selected.Count == 0 && surface.IsHitTestVisible);
        Check("E picks the eraser", Press(Key.E) && Field<object>("_inkTool").ToString() == "Eraser");
        Check("P picks the pen again", Press(Key.P) && Field<object>("_inkTool").ToString() == "Pen");

        // ── a finger moves the board; two zoom it ──
        var touches = Field<Dictionary<int, Point>>("_touches");
        Call("SetDrawMode", true);
        var z0 = Zoom;
        var c0 = new Point(400, 300);
        touches[101] = new Point(350, 300);
        touches[102] = new Point(450, 300);
        Call("StartPinch");
        var boardUnder = new Point((c0.X - PanT.X) / z0, (c0.Y - PanT.Y) / z0);
        touches[101] = new Point(300, 320);
        touches[102] = new Point(500, 320);
        Call("UpdatePinch");
        var underNow = new Point((400 - PanT.X) / Zoom, (320 - PanT.Y) / Zoom);
        Check("spreading two fingers twice as far doubles the zoom", Math.Abs(Zoom - z0 * 2) < 1e-6, $"zoom={Zoom}");
        Check("...and the point between them stays under them", (underNow - boardUnder).Length < 1e-6,
            $"{boardUnder} vs {underNow}");
        touches.Clear();
        typeof(ModPlanView).GetField("_pinch", F)!.SetValue(_view, null);
        ZoomT.ScaleX = ZoomT.ScaleY = 1;
        PanT.X = PanT.Y = 0;

        // ── the file and the sync ──
        Program.Pump(300);
        var json = System.IO.File.ReadAllText(file);
        Check("plans.json holds the strokes", json.Contains("\"Strokes\"") && json.Contains(s1.Id));
        var parsed = ModPlanService.Parse(json)!;
        var savedBoard = parsed.Boards.Single(b => b.Id == board.Id);
        Check("plans.json reads back stroke for stroke", savedBoard.Strokes!.Select(s => s.Id + s.Points + s.Color + s.Width)
            .SequenceEqual(Strokes.Select(s => s.Id + s.Points + s.Color + s.Width)));
        Check("a board without drawing still stores no Strokes field",
            !ModPlanService.Serialize(new PackPlans { Boards = { new PlanBoard() } }).Contains("Strokes"));

        // An older launcher (no Strokes property) keeps them in its extension data and writes them back.
        var boardJson = JsonSerializer.Serialize(Board);
        var old = JsonSerializer.Deserialize<OldBoard>(boardJson)!;
        var back = JsonSerializer.Serialize(old);
        var roundTrip = JsonSerializer.Deserialize<PlanBoard>(back)!;
        Check("an older launcher's round trip keeps the drawing", roundTrip.Strokes!.Select(s => s.Points)
            .SequenceEqual(Board.Strokes!.Select(s => s.Points)));

        MergeChecks();

        // ── pictures ──
        // Something to look at: a loop round a note, an arrow, a scribble, a highlight.
        Call("SetInkTool", Kind("InkTool", "Pen"));
        settings.PlanInkPenColor = "#D36A6A";
        Gesture("Pen", "Pen", Circle(430, 105, 115, 60));
        settings.PlanInkPenColor = null;
        Gesture("Pen", "Pen", Line(560, 160, 640, 120, 20, 0.7f));
        Gesture("Pen", "Pen", Line(640, 120, 622, 118, 6, 0.7f));
        Gesture("Pen", "Pen", Line(640, 120, 632, 135, 6, 0.7f));
        settings.PlanInkPenColor = "#3FB950";
        var scribble = Enumerable.Range(0, 120).Select(i => (680.0 + i * 1.6, 230 + 10 * Math.Sin(i / 3.0) + (i % 20 < 10 ? 0 : 4),
            (float)(0.3 + 0.6 * Math.Abs(Math.Sin(i / 9.0))))).ToList();
        Gesture("Pen", "Pen", scribble);
        settings.PlanInkPenColor = null;
        Program.Pump(500);
        Program.Shoot(_view, "ink-drawmode", 1);
        Program.Shoot(palette, "ink-palette-2x", 2);

        Call("SetInkTool", Kind("InkTool", "Select"));
        Gesture("Select", "Pen", Circle(430, 105, 140, 40));
        Program.Pump(300);
        Program.Shoot(_view, "ink-selection", 1);

        Call("SetDrawMode", false);
        Program.Pump(300);
        Check("drawing off again: presses reach the cards", !surface.IsHitTestVisible && palette.Visibility == Visibility.Collapsed);
        Program.Shoot(_view, "ink-board", 1);
        // At the screen's own scale the cached tiles meet on whole pixels (at 96 dpi their edges fall
        // between pixels, which shows as a hairline in a software render only).
        var dpi = VisualTreeHelper.GetDpi(_view).DpiScaleX;
        Program.Log($"device scale {dpi}");
        Program.Shoot(_view, "ink-board-devicepx", dpi);
        Check("D turns drawing on and the card tools step aside", Press(Key.D) && surface.IsHitTestVisible
            && ((FrameworkElement)_view.FindName("CardTools")).Visibility == Visibility.Collapsed);
        Program.Pump(200);
        Program.Shoot(_view, "ink-toolbar-drawing", 1);
        Check("Esc with nothing selected stops drawing", Press(Key.Escape) && !surface.IsHitTestVisible
            && ((FrameworkElement)_view.FindName("CardTools")).Visibility == Visibility.Visible);
        Call("ZoomAtCenter", 0.5);
        Program.Pump(600);
        Program.Shoot(_view, "ink-board-zoomed-out", 1);

        host.Close();
        shell.Hide();
        Program.Pump(300);
        Program.Log($"ink checks done: {_fails} failed");
    }

    /// <summary>Two PCs drawing on one board at once, merged against what they last agreed on.</summary>
    private static void MergeChecks()
    {
        PlanStroke S(string id, double x) => new()
        {
            Id = id, Width = 3,
            Points = PlanInkCodec.Encode([new InkPoint(x, 0, 0.5f), new InkPoint(x + 10, 10, 0.5f)])
        };
        PackPlans Doc(params PlanStroke[] strokes) => new()
        {
            Boards = { new PlanBoard { Id = "b", Name = "B", Nodes = { new PlanNode { Id = "n", Title = "x" } }, Strokes = strokes.ToList() } }
        };
        var baseDoc = Doc(S("a", 0), S("b", 20), S("c", 40));
        // This PC: drew d, erased a. The server: drew e, moved b.
        var local = Doc(S("b", 20), S("c", 40), S("d", 60));
        var remote = Doc(S("a", 0), S("b", 25), S("c", 40), S("e", 80));
        var merged = PlanMerge.Merge(baseDoc, local, remote).Boards[0].Strokes!.Select(s => s.Id).ToList();
        Check("merge: both PCs' new lines stay, the erased one goes", merged.Contains("d") && merged.Contains("e")
            && !merged.Contains("a") && merged.Contains("c"), string.Join(",", merged));
        var b = PlanMerge.Merge(baseDoc, local, remote).Boards[0].Strokes!.Single(s => s.Id == "b");
        Check("merge: a line moved on the other PC arrives moved", Math.Abs(PlanInkCodec.Decode(b.Points)[0].X - 25) < 0.11);

        // Both changed the same line: this PC wins, as for cards.
        var local2 = Doc(S("a", 0), S("b", 30), S("c", 40));
        var b2 = PlanMerge.Merge(baseDoc, local2, remote).Boards[0].Strokes!.Single(s => s.Id == "b");
        Check("merge: a line changed on both PCs keeps this PC's change", Math.Abs(PlanInkCodec.Decode(b2.Points)[0].X - 30) < 0.11);

        // Everything erased on one side, untouched on the other: the board ends with no drawing.
        var cleared = Doc();
        cleared.Boards[0].Strokes = null;
        var m3 = PlanMerge.Merge(baseDoc, cleared, baseDoc).Boards[0];
        Check("merge: a cleared drawing stays cleared", m3.Strokes is null, $"left={m3.Strokes?.Count}");

        // Codec edge cases.
        Check("codec: empty and junk decode to nothing", PlanInkCodec.Decode("").Count == 0
            && PlanInkCodec.Decode("not base64!").Count == 0 && PlanInkCodec.Decode("AAAA").Count == 0);
        var big = new List<InkPoint> { new(-12345.67, 98765.43, 1f), new(-12345.6, 98765.4, 0f), new(0, 0, 0.5f) };
        var round = PlanInkCodec.Decode(PlanInkCodec.Encode(big));
        Check("codec: far coordinates and the pressure ends survive", round.Count == 3
            && Math.Abs(round[0].X + 12345.7) < 0.051 && Math.Abs(round[1].Y - 98765.4) < 0.051
            && round[0].Pressure == 1f && round[1].Pressure == 0f, string.Join(" ", round));
        var cut = PlanInkCodec.Encode(big);
        var cutBytes = Convert.FromBase64String(cut);
        var truncated = Convert.ToBase64String(cutBytes.Take(cutBytes.Length - 1).ToArray());
        Check("codec: a cut-off string keeps the whole points", PlanInkCodec.Decode(truncated).Count == 2);
    }

    /// <summary>A board as a launcher before drawing reads it: no Strokes property.</summary>
    private sealed class OldBoard
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public List<JsonElement> Nodes { get; set; } = new();
        public List<JsonElement> Edges { get; set; } = new();
        [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
    }
}
