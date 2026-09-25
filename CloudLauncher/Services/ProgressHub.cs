using System.Windows;
using System.Windows.Threading;

namespace CloudLauncher.Services;

/// <summary>Snapshot of a long-running operation against a specific pack.</summary>
public sealed class ProgressInfo
{
    public Guid PackId { get; init; }
    /// <summary>0.0 to 1.0, or -1 for indeterminate.</summary>
    public double Fraction { get; init; }
    /// <summary>Short description, e.g. "Downloading assets...".</summary>
    public string Label { get; init; } = "";
    /// <summary>0.0 to 1.0 for the current item or stage, or -1 when unknown.</summary>
    public double CurrentFraction { get; init; } = -1;
    /// <summary>Label for the current item or stage, e.g. the file being downloaded.</summary>
    public string CurrentLabel { get; init; } = "";
    public bool HasCurrentProgress => CurrentFraction >= 0 || !string.IsNullOrWhiteSpace(CurrentLabel);
}

/// <summary>
/// Global progress channel for launch and sync. Pack views subscribe to
/// <see cref="ProgressChanged"/> and <see cref="ProgressCleared"/> and update for their pack.
/// Events are always raised on the UI thread.
/// </summary>
public static class ProgressHub
{
    private static readonly object Gate = new();
    private static readonly Dictionary<Guid, ProgressInfo> Pending = new();
    private static DispatcherTimer? _coalesceTimer;
    private const int CoalesceMs = 100;

    public static event Action<ProgressInfo>? ProgressChanged;
    public static event Action<Guid>?         ProgressCleared;

    public static void Report(
        Guid packId,
        double fraction,
        string label,
        double currentFraction = -1,
        string? currentLabel = null)
    {
        var info = new ProgressInfo
        {
            PackId          = packId,
            Fraction        = NormalizeFraction(fraction),
            Label           = label,
            CurrentFraction = NormalizeFraction(currentFraction),
            CurrentLabel    = currentLabel ?? ""
        };

        // Completion should always show immediately.
        if (info.Fraction >= 1.0)
        {
            lock (Gate) Pending.Remove(packId);
            Raise(() => ProgressChanged?.Invoke(info));
            return;
        }

        lock (Gate)
        {
            Pending[packId] = info;
        }
        ScheduleCoalescedFlush();
    }

    public static void Indeterminate(Guid packId, string label) => Report(packId, -1, label);

    public static void Indeterminate(Guid packId, string label, string currentLabel) =>
        Report(packId, -1, label, -1, currentLabel);

    public static void Clear(Guid packId)
    {
        lock (Gate) Pending.Remove(packId);
        Raise(() => ProgressCleared?.Invoke(packId));
    }

    private static void ScheduleCoalescedFlush()
    {
        Raise(EnsureCoalesceTimerRunning);
    }

    private static void EnsureCoalesceTimerRunning()
    {
        if (_coalesceTimer is null)
        {
            _coalesceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CoalesceMs) };
            _coalesceTimer.Tick += (_, _) => FlushPending();
        }

        if (!_coalesceTimer.IsEnabled)
            _coalesceTimer.Start();
    }

    private static void FlushPending()
    {
        Dictionary<Guid, ProgressInfo> batch;
        lock (Gate)
        {
            if (Pending.Count == 0)
            {
                _coalesceTimer?.Stop();
                return;
            }

            batch = new Dictionary<Guid, ProgressInfo>(Pending);
            Pending.Clear();
        }

        foreach (var info in batch.Values)
            ProgressChanged?.Invoke(info);
    }

    private static void Raise(Action a)
    {
        var app = Application.Current;
        if (app is null) return;
        if (app.Dispatcher.CheckAccess()) a();
        else app.Dispatcher.BeginInvoke(a);
    }

    private static double NormalizeFraction(double fraction)
    {
        if (double.IsNaN(fraction)) return 0;
        return fraction < 0 ? -1 : Math.Min(1.0, fraction);
    }
}
