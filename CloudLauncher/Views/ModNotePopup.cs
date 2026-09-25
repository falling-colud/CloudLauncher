using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>The small note popup: free text attached to one mod, saved to the modpack
/// (<see cref="ModMeta.Note"/> in <c>game/.cloudlauncher/mods.json</c>) so it syncs with everything
/// else. Opened from the options menu, a card's note chip and the planning board.</summary>
/// <remarks>Saves on close (click-away, Esc or the close button) rather than on every keystroke.</remarks>
public static class ModNotePopup
{
    private static Popup? _open;

    /// <summary>Opens the note editor for <paramref name="mod"/>, under <paramref name="anchor"/>
    /// when given, otherwise at the mouse (context menus). <paramref name="onSaved"/> fires only
    /// when the text changed.</summary>
    public static void Show(FrameworkElement? anchor, PackMod mod, Guid packId,
        PackModInventory inventory, Action? onSaved = null)
    {
        // Only one note is open at a time; opening another closes (and saves) the first.
        if (_open is not null) _open.IsOpen = false;

        var original = mod.Meta.Note ?? "";

        var box = new TextBox
        {
            Text = original,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = 132,
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(8, 6, 8, 6),
            VerticalContentAlignment = VerticalAlignment.Top
        };

        var placeholder = new TextBlock
        {
            Text = "Why is this mod here? What still needs testing?",
            Foreground = Res("TextTertiaryBrush"),
            IsHitTestVisible = false,
            Margin = new Thickness(10, 8, 10, 0),
            VerticalAlignment = VerticalAlignment.Top,
            TextWrapping = TextWrapping.Wrap
        };
        void SyncPlaceholder() =>
            placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => SyncPlaceholder();
        SyncPlaceholder();

        var popup = new Popup
        {
            AllowsTransparency = true,
            StaysOpen = false,
            PopupAnimation = PopupAnimation.Fade,
            HorizontalOffset = 0,
            VerticalOffset = 6,
            // Reposition with the anchor instead of hanging in place if the list scrolls underneath.
            Placement = anchor is not null ? PlacementMode.Bottom : PlacementMode.Mouse,
            PlacementTarget = anchor
        };

        var clear = new Button
        {
            Content = "Clear",
            Style = (Style)Application.Current.FindResource("LinkButton"),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        clear.Click += (_, _) => { box.Clear(); box.Focus(); };

        var close = new Button
        {
            Content = char.ConvertFromUtf32(0xE711), // MDL2 "Cancel"
            FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
            FontSize = 11,
            Style = (Style)Application.Current.FindResource("IconButton"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            ToolTip = "Save and close"
        };
        close.Click += (_, _) => popup.IsOpen = false;

        popup.Child = BuildCard(mod, box, placeholder, clear, close);

        box.PreviewKeyDown += (_, e) =>
        {
            // Esc and Ctrl+Enter both commit. Plain Enter inserts a newline.
            if (e.Key == Key.Escape || (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control))
            {
                popup.IsOpen = false;
                e.Handled = true;
            }
        };

        popup.Opened += (_, _) => { box.Focus(); box.CaretIndex = box.Text.Length; };
        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_open, popup)) _open = null;

            var text = box.Text.Trim();
            if (string.Equals(text, original.Trim(), StringComparison.Ordinal)) return;

            mod.Meta.Note = text.Length == 0 ? null : text;
            inventory.SaveMeta(packId, mod);
            onSaved?.Invoke();
        };

        _open = popup;
        popup.IsOpen = true;
    }

    private static Border BuildCard(PackMod mod, TextBox box, UIElement placeholder, UIElement clear, UIElement close)
    {
        var header = new StackPanel { Margin = new Thickness(0, 0, 24, 8) };
        header.Children.Add(new TextBlock
        {
            Text = mod.DisplayName,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = Res("TextPrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        header.Children.Add(new TextBlock
        {
            Text = "Note · saved to the modpack",
            FontSize = 10,
            Foreground = Res("TextTertiaryBrush")
        });

        // The text box and its placeholder share a cell so the hint sits inside the field.
        var field = new Grid();
        field.Children.Add(box);
        field.Children.Add(placeholder);

        var footer = new Grid();
        footer.Children.Add(clear);
        footer.Children.Add(new TextBlock
        {
            Text = "Esc to save",
            FontSize = 10,
            Foreground = Res("TextTertiaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        });

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(field);
        stack.Children.Add(footer);

        var body = new Grid();
        body.Children.Add(stack);
        body.Children.Add(close);

        return new CloudLauncher.Controls.SlateBorder
        {
            Width = 320,
            Background = Res("Surface2Brush"),
            BorderBrush = Res("BorderStrongBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12),
            Margin = new Thickness(10),           // room for the shadow to render inside the popup
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.45, Direction = 270 },
            Child = body
        };
    }

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
}
