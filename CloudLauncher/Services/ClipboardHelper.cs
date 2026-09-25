using System.Windows;

namespace CloudLauncher.Services;

/// <summary>Clipboard access that survives the clipboard being locked by another process, a common
/// transient Windows condition that makes <see cref="Clipboard.SetText(string)"/> throw
/// <see cref="System.Runtime.InteropServices.COMException"/>. Use it instead of Clipboard.SetText so
/// a copy action never crashes the launcher.</summary>
public static class ClipboardHelper
{
    /// <summary>Puts <paramref name="text"/> on the clipboard. Returns false if the clipboard was
    /// locked or unavailable.</summary>
    public static bool TrySetText(string? text)
    {
        try
        {
            Clipboard.SetText(text ?? "");
            return true;
        }
        catch
        {
            return false; // clipboard locked by another process
        }
    }
}
