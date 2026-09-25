using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace CloudLauncher.Views;

/// <summary>
/// The label a drop target wants under the cursor during a drag ("Share 3 items",
/// "Move into config/"), and the pill that draws it.
/// </summary>
/// <remarks>
/// The stock move/copy/link cursors don't describe these drops. WPF raises <c>GiveFeedback</c> on the
/// drag source, so the source draws the pill; the target only sets the label on <c>DragOver</c> and
/// clears it on <c>DragLeave</c>. One static slot is enough because an OLE drag is modal.
/// </remarks>
internal static class DragFeedback
{
    /// <summary>What a drop on the element under the cursor would do, or null when it would do nothing.</summary>
    public static string? Label { get; private set; }

    public static void Set(string? label) => Label = label;

    /// <summary>The cursor, in <paramref name="relativeTo"/>'s coordinates. False when the visual
    /// is not on screen (a harness rendering off-screen, a control mid-teardown).</summary>
    public static bool TryCursorPosition(Visual relativeTo, out Point at)
    {
        at = default;
        if (!GetCursorPos(out var p)) return false;
        try
        {
            at = relativeTo.PointFromScreen(new Point(p.X, p.Y));
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}

/// <summary>An accent pill that follows the cursor during a drag and names the drop.</summary>
/// <remarks>Drawn on the window's adorner layer so it is never clipped by the pane it started in.
/// Brushes are looked up per draw rather than cached: a cached brush outlives a theme change.</remarks>
internal sealed class DragLabelAdorner : Adorner
{
    private string _text = "";
    private Point _at;
    private bool _visible;

    public DragLabelAdorner(UIElement adorned) : base(adorned)
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
    }

    public void Show(string text, Point at)
    {
        _text = text;
        _at = at;
        _visible = true;
        InvalidateVisual();
    }

    public void Hide()
    {
        if (!_visible) return;
        _visible = false;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (!_visible || _text.Length == 0) return;

        var fill = TryFindResource("AccentBrush") as Brush ?? Brushes.SteelBlue;
        var ink = TryFindResource("TextOnAccentBrush") as Brush ?? Brushes.White;
        var family = TryFindResource("UiFont") as FontFamily ?? new FontFamily("Segoe UI");
        var text = new FormattedText(_text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            12, ink, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        // Offset from the hotspot so the arrow cursor never sits on top of its own label.
        var origin = new Point(_at.X + 18, _at.Y + 20);
        var box = new Rect(origin, new Size(text.Width + 20, text.Height + 10));
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), null,
            new Rect(box.X + 1, box.Y + 2, box.Width, box.Height), 7, 7);
        dc.DrawRoundedRectangle(fill, null, box, 7, 7);
        dc.DrawText(text, new Point(origin.X + 10, origin.Y + 5));
    }
}
