using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>
/// The right-click menu on a row of a mod's version list, plus the helpers every such list needs:
/// select the row under the mouse first, and find which row a click landed on.
/// </summary>
/// <remarks>
/// <para>Every version list builds the same menu in the same order: View changelog, then the page's
/// own actions (download, install), then Copy version number. It is built on demand instead of per
/// row in XAML, since a grid realises a container per visible row and few rows ever open a menu.</para>
/// <para>A DataGrid doesn't select on right-click by itself, so the row is selected first to keep
/// the highlight and the menu in agreement.</para>
/// </remarks>
public static class VersionRowMenu
{
    /// <summary>"Page", the same glyph as the Mods page's changelog button.</summary>
    public const int ChangelogGlyph = 0xE7C3;
    public const int DownloadGlyph = 0xE896;
    public const int CopyGlyph = 0xE8C8;

    /// <summary>An empty menu that opens at the mouse over <paramref name="target"/>.</summary>
    public static ContextMenu Create(UIElement target) => new() { PlacementTarget = target, MinWidth = 200 };

    /// <summary>Adds one item. <paramref name="toolTip"/> is shown on a disabled item too, so it can
    /// say why the item is off.</summary>
    public static MenuItem Add(ContextMenu menu, string header, int glyph, Action onClick,
        bool enabled = true, string? toolTip = null)
    {
        var item = new MenuItem { Header = header, Icon = Glyph(glyph), IsEnabled = enabled };
        if (toolTip is not null)
        {
            item.ToolTip = toolTip;
            ToolTipService.SetShowOnDisabled(item, true);
        }
        item.Click += (_, _) => onClick();
        menu.Items.Add(item);
        return item;
    }

    /// <summary>The first item on every version menu.</summary>
    public static MenuItem AddChangelog(ContextMenu menu, Action open) =>
        Add(menu, "View changelog", ChangelogGlyph, open);

    /// <summary>The last item on every version menu. <paramref name="copied"/> is told whether the
    /// clipboard write worked, so the page can report it in its status line.</summary>
    public static MenuItem AddCopyVersion(ContextMenu menu, string versionNumber, Action<bool>? copied = null) =>
        Add(menu, "Copy version number", CopyGlyph, () =>
        {
            var ok = ClipboardHelper.TrySetText(versionNumber);
            copied?.Invoke(ok);
        });

    public static void Open(ContextMenu menu) => menu.IsOpen = true;

    /// <summary>Selects the grid row under a right-button press, so the highlight and the menu
    /// that follows agree about which version is meant.</summary>
    public static void SelectRowUnder(DataGrid grid, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is not { } row) return;
        grid.SelectedItem = row.Item;
    }

    /// <summary>The item of the grid row a click landed on, or null for the header or the empty
    /// space below the last row.</summary>
    public static T? RowAt<T>(MouseButtonEventArgs e) where T : class =>
        FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item as T;

    /// <summary>The nearest <typeparamref name="T"/> at or above <paramref name="from"/>.</summary>
    /// <remarks>Walks the logical parent for a content element (a Run inside a TextBlock), whose
    /// visual parent cannot be asked for.</remarks>
    public static T? FindAncestor<T>(DependencyObject? from) where T : DependencyObject
    {
        for (var node = from; node is not null; node = ParentOf(node))
            if (node is T match) return match;
        return null;
    }

    private static DependencyObject? ParentOf(DependencyObject node) =>
        node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    /// <summary>A menu glyph from the same MDL2 set as <see cref="ModOptionsMenu"/>. Uses resource
    /// references because a menu can outlive a theme change.</summary>
    private static TextBlock Glyph(int codepoint)
    {
        var icon = new TextBlock
        {
            Text = char.ConvertFromUtf32(codepoint),
            Width = 16,
            TextAlignment = TextAlignment.Center,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        icon.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        return icon;
    }
}
