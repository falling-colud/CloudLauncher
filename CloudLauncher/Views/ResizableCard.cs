using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace CloudLauncher.Views;

/// <summary>How big one resizable dialog card starts, how small it may get, and what to remember it as.</summary>
/// <param name="Key">Settings key the dragged size is stored under (see <see cref="Services.AppSettings.DialogSizes"/>).</param>
/// <param name="DefaultWidth">Size the card opens at the first time, and returns to on a double-click of the grip.</param>
/// <param name="MinWidth">Below this the card's own content starts being clipped rather than scrolled.</param>
public sealed record ResizableCardSpec(
    string Key,
    double DefaultWidth,
    double DefaultHeight,
    double MinWidth = 420,
    double MinHeight = 300);

/// <summary>
/// Makes one of the launcher's in-window dialog cards draggable-to-size, and remembers the size.
/// </summary>
/// <remarks>
/// <para>The cards are UserControls shown by <c>MainWindow.ShowCardAsync</c>, so there is no window
/// ResizeMode, and an adorner would need an AdornerDecorator the custom chrome doesn't guarantee.
/// Wrapping the card in a Grid with a grip needs neither.</para>
/// <para>The card is centred, so each edge moves by half of a size change; the size grows by twice
/// the drag to keep the corner under the cursor.</para>
/// <para>The live size is clamped to the layer (on restore, on every drag and when the layer
/// resizes), but the saved size is not, so it comes back when there is room again.</para>
/// </remarks>
public static class ResizableCard
{
    /// <summary>How much of the layer is kept clear around a fully-grown card.</summary>
    private const double EdgeMargin = 48;

    /// <summary>
    /// Puts <paramref name="card"/> in a container that gives it a resize grip, restoring whatever
    /// size the user last dragged it to.
    /// </summary>
    /// <returns>The element to add to the dialog layer in place of the card.</returns>
    public static FrameworkElement Wrap(FrameworkElement card, ResizableCardSpec spec)
    {
        var holder = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        card.HorizontalAlignment = HorizontalAlignment.Center;
        card.VerticalAlignment = VerticalAlignment.Center;
        holder.Children.Add(card);

        var grip = BuildGrip();
        holder.Children.Add(grip);

        // The requested size, which can be larger than the size on screen. Kept separate so a card
        // squeezed into a small window grows back when the window is widened again.
        var desired = App.State.Settings.GetDialogSize(spec.Key, spec.DefaultWidth, spec.DefaultHeight);
        var state = new DragState { Desired = desired };

        // The layer's size is unknown until the holder is in the tree, so the first clamp waits for Loaded.
        holder.Loaded += (_, _) => Apply(card, holder, spec, state.Desired.Width, state.Desired.Height);
        holder.SizeChanged += (_, _) => Apply(card, holder, spec, state.Desired.Width, state.Desired.Height);

        HookDrag(card, holder, grip, spec, state);
        return holder;
    }

    /// <summary>Sets the card's size, clamped to what the layer can show.</summary>
    private static void Apply(
        FrameworkElement card, FrameworkElement holder, ResizableCardSpec spec,
        double width, double height, bool remember = false)
    {
        var layer = LayerSize(holder);
        var maxWidth = Math.Max(spec.MinWidth, layer.Width - EdgeMargin);
        var maxHeight = Math.Max(spec.MinHeight, layer.Height - EdgeMargin);

        card.Width = Math.Clamp(width, spec.MinWidth, maxWidth);
        card.Height = Math.Clamp(height, spec.MinHeight, maxHeight);

        if (remember) App.State.Settings.SetDialogSize(spec.Key, card.Width, card.Height);
    }

    /// <summary>
    /// The space a card may grow into.
    /// </summary>
    /// <remarks>Uses the parent dialog layer, since the centred holder is only as big as the card. Falls
    /// back to the window, then to a conservative guess before layout.</remarks>
    private static Size LayerSize(FrameworkElement holder)
    {
        if (holder.Parent is FrameworkElement { ActualWidth: > 0, ActualHeight: > 0 } parent)
            return new Size(parent.ActualWidth, parent.ActualHeight);

        var window = Window.GetWindow(holder);
        if (window is { ActualWidth: > 0, ActualHeight: > 0 })
            return new Size(window.ActualWidth, window.ActualHeight);

        return new Size(1280, 800);
    }

    /// <summary>The grip: three diagonal strokes in the bottom-right corner, like a Windows resize
    /// corner.</summary>
    private static FrameworkElement BuildGrip()
    {
        var strokes = new GeometryGroup();
        // Drawn inside a 14x14 box, shortest stroke at the outside corner.
        for (var i = 0; i < 3; i++)
        {
            var offset = 4 + i * 4;
            strokes.Children.Add(new LineGeometry(new Point(14, offset), new Point(offset, 14)));
        }

        var glyph = new Path
        {
            Data = strokes,
            StrokeThickness = 1.4,
            SnapsToDevicePixels = true,
            Stroke = (Brush)Application.Current.Resources["TextTertiaryBrush"],
        };

        // Transparent padding around the strokes gives a bigger hit area than the 14px drawing.
        var grip = new CloudLauncher.Controls.SlateBorder
        {
            Width = 22,
            Height = 22,
            Margin = new Thickness(0, 0, 4, 4),
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = Cursors.SizeNWSE,
            ToolTip = "Drag to resize · double-click to reset",
            Child = glyph,
        };

        grip.MouseEnter += (_, _) => glyph.Stroke = (Brush)Application.Current.Resources["TextPrimaryBrush"];
        grip.MouseLeave += (_, _) => glyph.Stroke = (Brush)Application.Current.Resources["TextTertiaryBrush"];
        return grip;
    }

    /// <summary>What one drag needs to remember, and the size the user actually asked for.</summary>
    private sealed class DragState
    {
        public (double Width, double Height) Desired;
        public bool Dragging;
        public Point Start;
        public double StartWidth;
        public double StartHeight;
    }

    private static void HookDrag(
        FrameworkElement card, FrameworkElement holder, FrameworkElement grip,
        ResizableCardSpec spec, DragState state)
    {
        // Measure against the dialog layer, which stays put. The holder and card re-centre as they grow,
        // so using them would feed the card's own movement back into the delta.
        IInputElement Reference() => (IInputElement?)holder.Parent ?? holder;

        grip.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                // Double-click resets. Handled here so it can't also start a drag.
                state.Desired = (spec.DefaultWidth, spec.DefaultHeight);
                Apply(card, holder, spec, spec.DefaultWidth, spec.DefaultHeight, remember: true);
                e.Handled = true;
                return;
            }

            state.Dragging = grip.CaptureMouse();
            state.Start = e.GetPosition(Reference());
            state.StartWidth = card.ActualWidth;
            state.StartHeight = card.ActualHeight;
            e.Handled = true;
        };

        grip.MouseMove += (_, e) =>
        {
            if (!state.Dragging) return;
            var now = e.GetPosition(Reference());
            state.Desired = (
                state.StartWidth + (now.X - state.Start.X) * 2,
                state.StartHeight + (now.Y - state.Start.Y) * 2);
            Apply(card, holder, spec, state.Desired.Width, state.Desired.Height);
        };

        grip.MouseLeftButtonUp += (_, e) =>
        {
            if (!state.Dragging) return;
            grip.ReleaseMouseCapture();   // raises LostMouseCapture, which is where the save happens
            e.Handled = true;
        };

        // The size is saved here. Losing capture (alt-tab, another dialog taking focus) ends a drag just
        // like releasing the button.
        grip.LostMouseCapture += (_, _) =>
        {
            if (!state.Dragging) return;
            state.Dragging = false;
            App.State.Settings.SetDialogSize(spec.Key, card.Width, card.Height);
        };
    }
}
