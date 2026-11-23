using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

// How drawing went, as one line in launcher.log when drawing stops (or the board closes): how many
// lines the pen drew and how many of them the pen's own renderer drew live (the rest the UI thread drew,
// which a busy board shows as lag), how late the pen's input reached the board, what finishing a line
// cost, the garbage collector's pauses (they stop the pen's live line too), and the PC's power state and
// time since it woke. Also notes waking from sleep, and the pen tablets whenever Windows changes them.
// What a "the drawing lags" report needs, without asking for a recording.

public partial class ModPlanView
{
    private sealed class InkSession
    {
        public long StartedAt = Environment.TickCount64;
        public TimeSpan GcPaused0 = GC.GetTotalPauseDuration();
        public int Gen0 = GC.CollectionCount(0), Gen1 = GC.CollectionCount(1), Gen2 = GC.CollectionCount(2);
        public int PenLines, RendererLines;
        public int Inputs, InputsLate30, InputsLate100;
        public int InputLagMax;
        public readonly List<int> InputLags = new();
        public readonly List<double> LineEnds = new();
    }

    private InkSession? _inkSession;
    private string _penTabletsSeen = "";
    private static DateTime _lastWake = DateTime.MinValue;
    private static bool _wakeWatched;

    private InkSession Session => _inkSession ??= new InkSession();

    /// <summary>A pen line began: whether the pen's renderer is drawing it live.</summary>
    private void NoteLineStart(bool rendererDrew)
    {
        var s = Session;
        s.PenLines++;
        if (rendererDrew) s.RendererLines++;
    }

    /// <summary>Pen input reached the board: how long after the pen sent it (WPF stamps each report
    /// with the tick it came in on).</summary>
    private void NoteInput(int timestamp)
    {
        var lag = Math.Max(0, Environment.TickCount - timestamp);
        if (lag > 60_000) return; // a stamp from another clock
        var s = Session;
        s.Inputs++;
        if (lag > 30) s.InputsLate30++;
        if (lag > 100) s.InputsLate100++;
        if (lag > s.InputLagMax) s.InputLagMax = lag;
        if (s.InputLags.Count < 20_000) s.InputLags.Add(lag);
    }

    private void NoteLineEnd(double ms)
    {
        var s = Session;
        if (s.LineEnds.Count < 20_000) s.LineEnds.Add(ms);
    }

    /// <summary>Writes the session's line to the log, if anything was drawn, and starts a new one.</summary>
    private void LogInkSession()
    {
        var s = _inkSession;
        _inkSession = null;
        if (s is null || s.LineEnds.Count == 0) return;
        try
        {
            static double Pct(List<double> v, double p)
            {
                var sorted = v.OrderBy(x => x).ToList();
                return sorted[Math.Min(sorted.Count - 1, (int)(p * sorted.Count))];
            }
            var lags = s.InputLags.Select(x => (double)x).ToList();
            var took = TimeSpan.FromMilliseconds(Environment.TickCount64 - s.StartedAt);
            var gc = GC.GetTotalPauseDuration() - s.GcPaused0;
            var fileKb = _packId == Guid.Empty ? 0 : Plans.CurrentJson(_packId).Length / 1024;
            var line =
                $"Drew {s.LineEnds.Count} line(s) in {Span(took)} on \"{_board?.Name}\" ({_board?.Strokes?.Count ?? 0} lines, boards {fileKb:N0} KB). "
                + $"Pen lines drawn live by the pen's renderer: {s.RendererLines}/{s.PenLines}. "
                + (lags.Count > 0
                    ? $"Pen input reached the board after {Pct(lags, 0.5):F0} ms typically, {Pct(lags, 0.95):F0} ms at the 95th, {s.InputLagMax} ms at worst ({s.InputsLate30} over 30 ms, {s.InputsLate100} over 100 ms, of {s.Inputs}). "
                    : "No pen input. ")
                + $"Finishing a line took {Pct(s.LineEnds, 0.5):F1} ms typically, {s.LineEnds.Max():F1} at worst. "
                + $"GC paused {gc.TotalMilliseconds:F0} ms (gen0 {GC.CollectionCount(0) - s.Gen0}, gen1 {GC.CollectionCount(1) - s.Gen1}, gen2 {GC.CollectionCount(2) - s.Gen2}). "
                + $"{PowerWords()}; render tier {RenderCapability.Tier >> 16}; up {Span(DateTime.Now - Process.GetCurrentProcess().StartTime)}"
                + (_lastWake > DateTime.MinValue ? $", woke {Span(DateTime.Now - _lastWake)} ago." : ".");
            AppLog.Log("ink", line);
        }
        catch { /* a diagnostic: never in the way of drawing */ }
    }

    private static string Span(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:00}m" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m{t.Seconds:00}s" : $"{t.TotalSeconds:F0}s";

    // ── sleep and the pen tablets ───────────────────────────────────────────

    /// <summary>Notes when Windows wakes from sleep, once per launcher run (a static event, so this view
    /// isn't held by it).</summary>
    private static void WatchWake()
    {
        if (_wakeWatched) return;
        _wakeWatched = true;
        try
        {
            SystemEvents.PowerModeChanged += (_, e) =>
            {
                if (e.Mode != PowerModes.Resume) return;
                _lastWake = DateTime.Now;
                AppLog.Log("ink", "Woke from sleep: " + PowerWords());
                Woke?.Invoke();
            };
        }
        catch { /* no system events here (a test host) */ }
    }

    /// <summary>Raised on the system events thread when Windows wakes.</summary>
    private static event Action? Woke;

    private void OnWoke() => Dispatcher.BeginInvoke(() =>
    {
        // Windows brings the pen and touch screen back under new ids; the renderer's list of pens is
        // made again now rather than at the pen's first hover over the board.
        if (IsLoaded) RefreshPenTablets();
    });

    /// <summary>Logs the tablets WPF knows when they differ from the last ones logged.</summary>
    private void NotePenTablets()
    {
        try
        {
            var all = string.Join(", ", Tablet.TabletDevices.Cast<TabletDevice>().Select(t => $"{t.Name} #{t.Id} ({t.Type})"));
            if (all == _penTabletsSeen) return;
            _penTabletsSeen = all;
            AppLog.Log("ink", "Tablets: " + (all.Length == 0 ? "none" : all));
        }
        catch { /* no tablet stack */ }
    }

    // ── power ───────────────────────────────────────────────────────────────

    private static string PowerWords()
    {
        var words = new List<string>();
        if (GetSystemPowerStatus(out var p))
        {
            words.Add(p.ACLineStatus == 1 ? "plugged in" : p.ACLineStatus == 0 ? "on battery" : "power unknown");
            if (p.BatteryLifePercent <= 100) words.Add($"battery {p.BatteryLifePercent}%");
            if (p.SystemStatusFlag == 1) words.Add("battery saver on");
        }
        try
        {
            if (PowerGetEffectiveOverlayScheme(out var overlay) == 0)
                words.Add(overlay.ToString() switch
                {
                    "961cc777-2547-4f9d-8174-7d86181b8a7a" => "power mode best efficiency",
                    "ded574b5-45a0-4f42-8737-46345c09c238" => "power mode best performance",
                    "00000000-0000-0000-0000-000000000000" => "power mode balanced",
                    var other => "power mode " + other
                });
        }
        catch { /* older Windows */ }
        return words.Count == 0 ? "power unknown" : string.Join(", ", words);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);
}
