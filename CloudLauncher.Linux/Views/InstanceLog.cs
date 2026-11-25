using System.Text;
using Avalonia.Threading;

namespace CloudLauncher.Views;

/// <summary>
/// What the launcher printed for each instance in this session: the launch steps, the game's own
/// output while it runs, and upload and download lines. The Windows instance page keeps this in a
/// hidden text box on the page; it lives here so it outlives the page (the game keeps writing after
/// you leave the instance and come back).
/// </summary>
/// <remarks>Kept on the UI thread (calls from elsewhere are posted to it). <see cref="ProgressFor"/>
/// hands the services an <see cref="IProgress{T}"/> made on the UI thread, which posts every line back
/// to it.</remarks>
public static class InstanceLog
{
    /// <summary>Kept per instance; the oldest lines go past this, as on Windows (MaxLogChars).</summary>
    private const int MaxChars = 200_000;

    private static readonly Dictionary<Guid, StringBuilder> Logs = new();

    /// <summary>Lines were added to an instance's log (the text added, without a trailing newline).</summary>
    public static event Action<Guid, string>? Appended;

    /// <summary>An instance's log was emptied (a new launch or transfer started).</summary>
    public static event Action<Guid>? Cleared;

    public static string Text(Guid packId) => Logs.TryGetValue(packId, out var sb) ? sb.ToString() : "";

    public static bool IsEmpty(Guid packId) => !Logs.TryGetValue(packId, out var sb) || sb.Length == 0;

    public static void Clear(Guid packId)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Clear(packId));
            return;
        }
        if (Logs.TryGetValue(packId, out var sb)) sb.Clear();
        Cleared?.Invoke(packId);
    }

    public static void Append(Guid packId, string text)
    {
        // A sink made off the UI thread reports from wherever the service runs: hop over, never throw.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Append(packId, text));
            return;
        }
        if (string.IsNullOrEmpty(text)) return;
        if (!Logs.TryGetValue(packId, out var sb)) Logs[packId] = sb = new StringBuilder();
        sb.Append(text).Append('\n');
        if (sb.Length > MaxChars)
        {
            // Drop a chunk at a time, so a long session doesn't re-trim on every line.
            var keep = sb.ToString(sb.Length - MaxChars * 9 / 10, MaxChars * 9 / 10);
            sb.Clear().Append("[...older lines trimmed...]\n").Append(keep);
        }
        Appended?.Invoke(packId, text);
    }

    /// <summary>A sink for the services' log lines. Call on the UI thread.</summary>
    public static IProgress<string> ProgressFor(Guid packId) =>
        new Progress<string>(line => Append(packId, line));
}
