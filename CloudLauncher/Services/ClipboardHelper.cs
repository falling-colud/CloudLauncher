using System.Windows;

namespace CloudLauncher.Services;

/// <summary>Clipboard access that tolerates the OS clipboard being locked by another
/// process — a common, transient Windows condition that makes <see cref="Clipboard.SetText(string)"/>
/// throw <see cref="System.Runtime.InteropServices.COMException"/>. Use this everywhere instead
/// of calling Clipboard.SetText directly so a copy action never crashes the launcher.</summary>
public static class ClipboardHelper
{
    /// <summary>Attempts to put <paramref name="text"/> on the clipboard. Returns true on
    /// success, false if the clipboard was locked/unavailable.</summary>
    public static bool TrySetText(string? text)
    {
        try
        {
            Clipboard.SetText(text ?? "");
            return true;
        }
        catch
        {
            return false; // clipboard locked by another process — ignore
        }
    }
}
