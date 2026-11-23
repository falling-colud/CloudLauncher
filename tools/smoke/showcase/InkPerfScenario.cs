using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CloudLauncher;
using CloudLauncher.Services;
using CloudLauncher.Views;

namespace Showcase;

/// <summary>
/// How a big drawn board answers a pen: a real boards file (CL_PERF_PLANS) is opened on its board with
/// the most strokes, and a hand "writes" on it, words of short strokes with pauses between them, the way
/// a pen delivers them: as input on the UI thread's queue, at the pen's own pace, whether the thread is
/// free or not. The live sync runs against the fake server meanwhile, as it does at home.
/// </summary>
/// <remarks>
/// <para>What is measured is the UI thread: how late it takes each piece of pen input (a watchdog posts
/// input-priority work every few milliseconds and times how long it waits), what one finished stroke
/// costs it, and the garbage collector's pauses, which stop every thread, the pen's live line included.</para>
/// <para>The pen's live line itself (WPF's DynamicRenderer, on its own thread) can't be driven without a
/// digitizer; with CL_PERF_WET=1 the strokes go the way of a pen the renderer missed, drawn by the UI
/// thread instead.</para>
/// <para>Results are PERF lines in the log, and a CHECK line per bound when CL_PERF_BOUNDS=1.</para>
/// </remarks>
public static class InkPerfScenario
{
    private const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
    private static ModPlanView _view = null!;

    private static object? Call(string m, params object?[] a)
    {
        var method = typeof(ModPlanView).GetMethods(F).Single(x => x.Name == m && x.GetParameters().Length == a.Length);
        return method.Invoke(_view, a);
    }

    private static TF Field<TF>(string f) => (TF)typeof(ModPlanView).GetField(f, F)!.GetValue(_view)!;

    public static void Run()
    {
        var source = Environment.GetEnvironmentVariable("CL_PERF_PLANS")
                     ?? throw new InvalidOperationException("CL_PERF_PLANS must name a plans.json");
        var wet = Environment.GetEnvironmentVariable("CL_PERF_WET") == "1";
        var words = int.TryParse(Environment.GetEnvironmentVariable("CL_PERF_WORDS"), out var w) ? w : 8;

        var shell = Program.MakeShell(1280, 820);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(shell.Dispatcher));
        shell.Show();
        Program.Pump(1500);

        // The real boards, opened on the one with the most strokes; the server already holds the same copy,
        // as it does for a PC that has been in step all along.
        var json = File.ReadAllText(source);
        var doc = ModPlanService.Parse(json) ?? throw new InvalidOperationException("not a boards file");
        var biggest = doc.Boards.OrderByDescending(b => b.Strokes?.Count ?? 0).First();
        doc.LastBoardId = biggest.Id;
        json = ModPlanService.Serialize(doc);
        var file = Path.Combine(App.State.Packs.GameDir(FakeWorld.Hearthstone), ".cloudlauncher", "plans.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, json);
        var baseFile = Path.Combine(App.State.Packs.PackRoot(FakeWorld.Hearthstone), "plans-sync.json");
        File.WriteAllText(baseFile, JsonSerializer.Serialize(new { Revision = 1L, Plans = json }));
        FakeWorld.SeedPlans(FakeWorld.Hearthstone, 1, json);
        App.State.ModPlans.Invalidate(FakeWorld.Hearthstone);
        Program.Log($"PERF board \"{biggest.Name}\": {biggest.Strokes?.Count ?? 0} strokes; the file {json.Length:N0} chars, "
                    + $"{doc.Boards.Sum(b => b.Strokes?.Count ?? 0):N0} strokes on {doc.Boards.Count} boards");

        _view = new ModPlanView();
        var host = new Window
        {
            Left = -24000, Top = -24000, Width = 1100, Height = 640, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Content = _view,
        };
        host.Show();
        Program.Pump(800);
        _view.Load(FakeWorld.Hearthstone, Array.Empty<PackMod>(), shell);
        Program.Pump(4000); // the sync's first pull, and the board's first tiles
        var board = Field<PlanBoard>("_board");
        Program.Log($"PERF open board: \"{board.Name}\", {board.Strokes?.Count ?? 0} strokes, sync {App.State.PlanSync.StateOf(FakeWorld.Hearthstone)}");

        App.State.Settings.PlanInkTool = "pen";
        // The board at rest, before and after drawing goes on: what does the UI thread do with no input?
        Program.Pump(1500);
        var idle0 = GC.GetAllocatedBytesForCurrentThread();
        var idleGc0 = GC.GetTotalPauseDuration();
        Program.Pump(3000);
        Program.Log($"PERF at rest, drawing off: {(GC.GetAllocatedBytesForCurrentThread() - idle0) / 3.0 / 1048576:F1} MB/s on the UI thread, GC {(GC.GetTotalPauseDuration() - idleGc0).TotalMilliseconds:F0} ms paused in 3 s");
        Call("SetDrawMode", true);
        Program.Pump(1500);
        idle0 = GC.GetAllocatedBytesForCurrentThread();
        idleGc0 = GC.GetTotalPauseDuration();
        Program.Pump(3000);
        Program.Log($"PERF at rest, drawing on: {(GC.GetAllocatedBytesForCurrentThread() - idle0) / 3.0 / 1048576:F1} MB/s on the UI thread, GC {(GC.GetTotalPauseDuration() - idleGc0).TotalMilliseconds:F0} ms paused in 3 s");
        FakeWorld.ResetPlanCounters();

        // ── the watchdog: how late does input-priority work run? ──
        var dispatcher = _view.Dispatcher;
        var latencies = new List<(double At, double Ms)>();
        var clock = Stopwatch.StartNew();
        var running = true;
        var watchdog = new Thread(() =>
        {
            while (Volatile.Read(ref running))
            {
                var posted = clock.Elapsed.TotalMilliseconds;
                var done = new ManualResetEventSlim();
                dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                {
                    var ran = clock.Elapsed.TotalMilliseconds;
                    lock (latencies) latencies.Add((posted, ran - posted));
                    done.Set();
                });
                done.Wait(5000);
                Thread.Sleep(4);
            }
        }) { IsBackground = true };

        // ── the hand: words of short strokes ──
        var endCosts = new List<double>();
        var penAlloc = new long[3]; // bytes the UI thread allocated in pen down / move / up
        var strokeLag = new List<double>(); // how late each stroke's first point reached the board
        var rnd = new Random(7);
        const int strokesPerWord = 7, pointsPerStroke = 24, msPerMove = 8, gapMs = 110, pauseMs = 1700;
        var plan = new List<(double At, Action Act)>();
        double t = 300;
        var surface = (FrameworkElement)_view.FindName("InkSurface");
        double x0 = 140, y0 = 180;
        for (var wi = 0; wi < words; wi++)
        {
            for (var si = 0; si < strokesPerWord; si++)
            {
                var ox = x0 + si * 26 + rnd.NextDouble() * 6;
                var oy = y0 + rnd.NextDouble() * 8;
                var pts = Enumerable.Range(0, pointsPerStroke).Select(i =>
                {
                    var a = i / (double)(pointsPerStroke - 1);
                    return new StylusPoint(ox + 18 * a + 5 * Math.Sin(a * 9), oy + 22 * Math.Sin(a * Math.PI) - 6 * a, 0.55f);
                }).ToList();
                var planned = t;
                plan.Add((t, () =>
                {
                    strokeLag.Add(clock.Elapsed.TotalMilliseconds - planned);
                    var first = new StylusPointCollection { pts[0] };
                    var a0 = GC.GetAllocatedBytesForCurrentThread();
                    Call("PenDown", 11, false, false, new Point(pts[0].X, pts[0].Y), first, !wet);
                    penAlloc[0] += GC.GetAllocatedBytesForCurrentThread() - a0;
                }));
                for (var i = 1; i < pointsPerStroke; i += 2)
                {
                    var chunk = new StylusPointCollection(pts.Skip(i).Take(2));
                    t += msPerMove;
                    plan.Add((t, () =>
                    {
                        var a0 = GC.GetAllocatedBytesForCurrentThread();
                        Call("PenMove", 11, chunk);
                        penAlloc[1] += GC.GetAllocatedBytesForCurrentThread() - a0;
                    }));
                }
                t += msPerMove;
                var last = new StylusPointCollection { pts[^1] };
                plan.Add((t, () =>
                {
                    var a0 = GC.GetAllocatedBytesForCurrentThread();
                    var sw = Stopwatch.StartNew();
                    Call("PenUp", 11, last);
                    endCosts.Add(sw.Elapsed.TotalMilliseconds);
                    penAlloc[2] += GC.GetAllocatedBytesForCurrentThread() - a0;
                }));
                t += gapMs;
            }
            t += pauseMs;
            y0 += 40;
            if (y0 > 480) { y0 = 180; x0 += 220; }
        }
        var totalMs = t + 2500;

        var gc0 = GC.GetTotalPauseDuration();
        var c0 = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        var alloc0 = GC.GetTotalAllocatedBytes();
        var uiAlloc0 = GC.GetAllocatedBytesForCurrentThread();
        var feeder = new Thread(() =>
        {
            foreach (var (at, act) in plan)
            {
                var wait = at - clock.Elapsed.TotalMilliseconds;
                if (wait > 1) Thread.Sleep((int)wait);
                dispatcher.BeginInvoke(DispatcherPriority.Input, act);
            }
        }) { IsBackground = true };
        clock.Restart();
        watchdog.Start();
        feeder.Start();
        Program.Pump((int)totalMs);
        Volatile.Write(ref running, false);
        watchdog.Join(6000);
        feeder.Join(6000);

        var uiAlloc = GC.GetAllocatedBytesForCurrentThread() - uiAlloc0;
        var gcMs = (GC.GetTotalPauseDuration() - gc0).TotalMilliseconds;
        var allocMb = (GC.GetTotalAllocatedBytes() - alloc0) / (double)(1 << 20);
        var c1 = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

        static double Pct(List<double> v, double p) { if (v.Count == 0) return 0; var s = v.OrderBy(x => x).ToList(); return s[Math.Min(s.Count - 1, (int)(p * s.Count))]; }
        List<double> lat;
        lock (latencies) lat = latencies.Select(l => l.Ms).ToList();
        var strokes = endCosts.Count;
        Program.Log($"PERF mode: {(wet ? "pen the live renderer missed (UI thread draws the line)" : "pen with its live renderer")}; "
                    + $"{words} words x {strokesPerWord} strokes, {pauseMs} ms pauses");
        Program.Log($"PERF stroke end (UI ms): median {Pct(endCosts, 0.5):F1}, p90 {Pct(endCosts, 0.9):F1}, max {endCosts.DefaultIfEmpty().Max():F1} over {strokes} strokes");
        Program.Log($"PERF stroke start late (ms): median {Pct(strokeLag, 0.5):F1}, p90 {Pct(strokeLag, 0.9):F1}, max {strokeLag.DefaultIfEmpty().Max():F1}");
        Program.Log($"PERF input wait (ms): p50 {Pct(lat, 0.5):F1}, p95 {Pct(lat, 0.95):F1}, p99 {Pct(lat, 0.99):F1}, max {lat.DefaultIfEmpty().Max():F1}; "
                    + $"waits over 30 ms: {lat.Count(x => x > 30)}, over 60 ms: {lat.Count(x => x > 60)}, over 120 ms: {lat.Count(x => x > 120)} (of {lat.Count})");
        Program.Log($"PERF allocated by the pen's own handlers (MB): down {penAlloc[0] / 1048576.0:F1}, moves {penAlloc[1] / 1048576.0:F1}, up {penAlloc[2] / 1048576.0:F1}; UI thread in all {uiAlloc / 1048576.0:F0}");
        Program.Log($"PERF GC: {gcMs:F0} ms paused, collections gen0 {c1.Item1 - c0.Item1} gen1 {c1.Item2 - c0.Item2} gen2 {c1.Item3 - c0.Item3}, {allocMb:F0} MB allocated");
        var (puts, gets200, getBytes, gets204, delivered, deliveredBytes) = FakeWorld.PlanCounters();
        Program.Log($"PERF server: {puts} stores, {gets200} waits ended with the whole document ({getBytes / 1024.0 / 1024.0:F1} MB), "
                    + $"{delivered} of them taken in full by the launcher ({deliveredBytes / 1024.0 / 1024.0:F1} MB), {gets204} empty answers");
        var after = Field<PlanBoard>("_board");
        Program.Log($"PERF board now {after.Strokes?.Count ?? 0} strokes (drew {strokes}); sync {App.State.PlanSync.StateOf(FakeWorld.Hearthstone)}");

        // Let the last sends finish, then check the server holds every stroke.
        Program.Pump(3000);
        var stored = FakeWorld.StoredPlans(FakeWorld.Hearthstone);
        var storedDoc = stored is null ? null : ModPlanService.Parse(stored);
        var storedCount = storedDoc?.Boards.FirstOrDefault(b => b.Id == after.Id)?.Strokes?.Count ?? -1;
        var onDisk = ModPlanService.Parse(File.ReadAllText(file))?.Boards.FirstOrDefault(b => b.Id == after.Id)?.Strokes?.Count ?? -1;
        Program.Log($"{(storedCount == after.Strokes?.Count ? "PASS" : "FAIL")}  the server holds every stroke ({storedCount} of {after.Strokes?.Count})");
        _view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        // A launcher that holds saves back writes them out when the board closes (or on exit).
        typeof(ModPlanService).GetMethod("Flush", Type.EmptyTypes)?.Invoke(App.State.ModPlans, null);
        Program.Pump(300);
        onDisk = ModPlanService.Parse(File.ReadAllText(file))?.Boards.FirstOrDefault(b => b.Id == after.Id)?.Strokes?.Count ?? -1;
        Program.Log($"{(onDisk == after.Strokes?.Count ? "PASS" : "FAIL")}  plans.json on disk holds every stroke ({onDisk} of {after.Strokes?.Count})");
        host.Close();
    }
}
