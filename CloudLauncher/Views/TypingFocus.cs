using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace CloudLauncher.Views;

/// <summary>Whether the keyboard is currently in a text field, on any page.</summary>
/// <remarks>Pages handle Delete, F2 and Enter window-wide, and with the side panel open another
/// page's text box can have focus while the page behind it is visible, so checking only a page's
/// own search box isn't enough. A PasswordBox is not a TextBox, hence the separate check.</remarks>
internal static class TypingFocus
{
    public static bool IsTyping => Keyboard.FocusedElement is TextBoxBase or PasswordBox;
}
