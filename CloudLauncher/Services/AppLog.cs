using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace CloudLauncher.Services;

/// <summary>
/// In-memory, app-wide log capture. Every call adds a timestamped line to a buffer
/// and fires <see cref="MessageAppended"/> on the UI thread. The Logs tab subscribes
/// so users can see what the launcher is doing while it tries to start the game.
/// </summary>
public static class AppLog
{
    private static readonly StringBuilder _buffer = new();
    private static readonly object _lock = new();

    /// <summary>Fired after a line is appended. Carries the formatted line (with timestamp).</summary>
    public static event Action<string>? MessageAppended;

    /// <summary>Snapshot of every line captured so far, joined by newlines.</summary>
    public static string Buffer { get { lock (_lock) return _buffer.ToString(); } }

    public static void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        lock (_lock) _buffer.AppendLine(line);
        var app = Application.Current;
        if (app is null) return;
        if (app.Dispatcher.CheckAccess()) MessageAppended?.Invoke(line);
        else app.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() => MessageAppended?.Invoke(line)));
    }

    public static void Log(string source, string message) => Log($"[{source}] {message}");

    /// <summary>Convenience for logging exceptions with stack.</summary>
    public static void LogError(string context, Exception ex)
    {
        Log($"ERROR in {context}: {ex.GetType().Name}: {ex.Message}");
        var inner = ex.InnerException;
        var depth = 1;
        while (inner is not null && depth < 5)
        {
            Log($"  caused by: {inner.GetType().Name}: {inner.Message}");
            inner = inner.InnerException;
            depth++;
        }
        if (ex.StackTrace is { Length: > 0 } trace)
            Log("  trace:\n" + trace);
    }
}
