using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CloudLauncher.Views;

/// <summary>
/// Keeps a list visually still while its contents change underneath it.
/// </summary>
/// <remarks>
/// <para>Rebuilding an <see cref="ItemsControl"/>'s source keeps the pixel offset but not the item at
/// it, so hiding one row makes the view jump. This remembers the item at the top of the viewport and
/// its exact position, then puts it back.</para>
/// <para>If that item is the one that disappeared, the next surviving item from the old order takes
/// its place.</para>
/// </remarks>
internal sealed class ListScrollAnchor(ItemsControl items)
{
    private ScrollViewer? _scroll;

    /// <summary>The item that was at the top of the viewport, and how far above it had scrolled.</summary>
    internal readonly record struct Capture(object? Item, double OffsetInViewport, double RawOffset, int Index);

    /// <summary>
    /// Notes where the list is sitting. <paramref name="order"/> is the sequence currently shown, so
    /// a vanished anchor can fall forward to the next item that survives.
    /// </summary>
    public Capture Take(IReadOnlyList<object>? order)
    {
        var scroll = Scroll;
        if (scroll is null || scroll.VerticalOffset <= 0) return default;

        var top = FindTopVisible(scroll);
        var index = top.Item is null || order is null ? -1 : IndexOf(order, top.Item);
        return new Capture(top.Item, top.Offset, scroll.VerticalOffset, index);
    }

    /// <summary>Puts the list back where <see cref="Take"/> found it, against the new contents.</summary>
    public void Restore(Capture capture, IReadOnlyList<object>? previousOrder, IReadOnlyList<object>? newOrder)
    {
        var scroll = Scroll;
        if (scroll is null || capture.RawOffset <= 0) return;

        var target = SurvivingAnchor(capture, previousOrder, newOrder);
        if (target is null)
        {
            // Nothing recognisable left (a filter that emptied the list, a full re-scan): the raw
            // offset is the best guess, and the scroll viewer clamps it for us.
            scroll.ScrollToVerticalOffset(capture.RawOffset);
            return;
        }

        // Converge: each pass scrolls the anchor closer to where it was, and re-lays out so a
        // virtualised container that was not realised the first time exists on the next.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            items.UpdateLayout();
            var container = items.ItemContainerGenerator.ContainerFromItem(target) as FrameworkElement;
            if (container is null)
            {
                // Not realised yet: jump roughly to where it should be by row height and try again.
                if (newOrder is null) break;
                var row = EstimateRowHeight(scroll);
                if (row <= 0) break;
                var index = IndexOf(newOrder, target);
                if (index < 0) break;
                scroll.ScrollToVerticalOffset(Math.Max(0, index * row - capture.OffsetInViewport));
                continue;
            }

            var y = container.TransformToAncestor(scroll).Transform(new Point(0, 0)).Y;
            var correction = y - capture.OffsetInViewport;
            if (Math.Abs(correction) < 0.5) return;
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + correction);
        }
    }

    /// <summary>The captured item if it is still shown, otherwise the first item after it in the old
    /// order that is (the row that took its place on screen).</summary>
    private static object? SurvivingAnchor(Capture capture, IReadOnlyList<object>? previousOrder, IReadOnlyList<object>? newOrder)
    {
        if (capture.Item is null || newOrder is null) return null;
        if (IndexOf(newOrder, capture.Item) >= 0) return capture.Item;
        if (previousOrder is null) return null;

        var from = capture.Index >= 0 ? capture.Index : IndexOf(previousOrder, capture.Item);
        if (from < 0) return null;
        for (var i = from + 1; i < previousOrder.Count; i++)
            if (IndexOf(newOrder, previousOrder[i]) >= 0) return previousOrder[i];
        return null;
    }

    /// <summary>The first realised container that is not entirely above the viewport, and its offset
    /// from the viewport's top edge (usually slightly negative, as it is partly scrolled off).</summary>
    private (object? Item, double Offset) FindTopVisible(ScrollViewer scroll)
    {
        object? best = null;
        var bestY = double.MaxValue;

        foreach (var item in items.Items)
        {
            if (items.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container) continue;
            if (container.ActualHeight <= 0) continue;
            double y;
            try { y = container.TransformToAncestor(scroll).Transform(new Point(0, 0)).Y; }
            catch { continue; } // container detached mid-virtualisation
            if (y + container.ActualHeight <= 0) continue;   // entirely above the viewport
            if (y >= scroll.ViewportHeight) continue;        // entirely below it
            if (y >= bestY) continue;
            best = item;
            bestY = y;
        }
        return best is null ? (null, 0) : (best, bestY);
    }

    /// <summary>A representative row height, for jumping near an unrealised anchor.</summary>
    private double EstimateRowHeight(ScrollViewer scroll)
    {
        foreach (var item in items.Items)
            if (items.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement { ActualHeight: > 0 } c)
                return c.ActualHeight;
        return scroll.ViewportHeight > 0 ? scroll.ViewportHeight / 8 : 0;
    }

    private static int IndexOf(IReadOnlyList<object> list, object item)
    {
        for (var i = 0; i < list.Count; i++)
            if (ReferenceEquals(list[i], item)) return i;
        return -1;
    }

    private ScrollViewer? Scroll => _scroll ??= FindScrollViewer(items);

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        return null;
    }
}
