using System.Windows.Input;

namespace CloudLauncher.Services;

public static class LauncherKeybinds
{
    public static string PrettyName(string mcKey)
    {
        if (string.IsNullOrEmpty(mcKey)) return "-";
        if (mcKey.StartsWith("key.keyboard.", StringComparison.Ordinal))
        {
            var part = mcKey["key.keyboard.".Length..];
            return part switch
            {
                "left.shift" => "Left Shift",
                "right.shift" => "Right Shift",
                "left.control" => "Left Ctrl",
                "right.control" => "Right Ctrl",
                "left.alt" => "Left Alt",
                "right.alt" => "Right Alt",
                "space" => "Space",
                "enter" => "Enter",
                "tab" => "Tab",
                "escape" => "Esc",
                "backspace" => "Backspace",
                "caps.lock" => "Caps Lock",
                "page.up" => "Page Up",
                "page.down" => "Page Down",
                "grave.accent" => "`",
                "left.bracket" => "[",
                "right.bracket" => "]",
                "keypad.0" => "Numpad 0",
                "keypad.1" => "Numpad 1",
                "keypad.2" => "Numpad 2",
                "keypad.3" => "Numpad 3",
                "keypad.4" => "Numpad 4",
                "keypad.5" => "Numpad 5",
                "keypad.6" => "Numpad 6",
                "keypad.7" => "Numpad 7",
                "keypad.8" => "Numpad 8",
                "keypad.9" => "Numpad 9",
                // Single letters, and function keys ("f5" → "F5"), read better capitalised.
                _ => part.Length == 1 || (part.Length <= 3 && part[0] == 'f' && part[1..].All(char.IsDigit))
                    ? part.ToUpperInvariant()
                    : part
            };
        }

        if (mcKey.StartsWith("key.mouse.", StringComparison.Ordinal))
        {
            return mcKey switch
            {
                "key.mouse.left" => "Mouse Left",
                "key.mouse.right" => "Mouse Right",
                "key.mouse.middle" => "Mouse Middle",
                "key.mouse.4" => "Mouse 4",
                "key.mouse.5" => "Mouse 5",
                _ => mcKey
            };
        }

        return mcKey;
    }

    public static string? WpfKeyToMinecraft(Key k)
    {
        if (k >= Key.A && k <= Key.Z)
            return "key.keyboard." + k.ToString().ToLowerInvariant();
        if (k >= Key.D0 && k <= Key.D9)
            return "key.keyboard." + k.ToString()[1..];
        if (k >= Key.NumPad0 && k <= Key.NumPad9)
            return "key.keyboard.keypad." + k.ToString()[6..];
        if (k >= Key.F1 && k <= Key.F12)
            return "key.keyboard." + k.ToString().ToLowerInvariant();

        return k switch
        {
            Key.Space => "key.keyboard.space",
            Key.Enter => "key.keyboard.enter",
            Key.Tab => "key.keyboard.tab",
            Key.Back => "key.keyboard.backspace",
            Key.LeftShift => "key.keyboard.left.shift",
            Key.RightShift => "key.keyboard.right.shift",
            Key.LeftCtrl => "key.keyboard.left.control",
            Key.RightCtrl => "key.keyboard.right.control",
            Key.LeftAlt => "key.keyboard.left.alt",
            Key.RightAlt => "key.keyboard.right.alt",
            Key.CapsLock => "key.keyboard.caps.lock",
            Key.OemPeriod => "key.keyboard.period",
            Key.OemComma => "key.keyboard.comma",
            Key.OemMinus => "key.keyboard.minus",
            Key.OemPlus => "key.keyboard.equal",
            Key.OemSemicolon => "key.keyboard.semicolon",
            Key.OemQuotes => "key.keyboard.apostrophe",
            Key.OemQuestion => "key.keyboard.slash",
            Key.OemPipe => "key.keyboard.backslash",
            Key.OemOpenBrackets => "key.keyboard.left.bracket",
            Key.OemCloseBrackets => "key.keyboard.right.bracket",
            Key.OemTilde => "key.keyboard.grave.accent",
            Key.Up => "key.keyboard.up",
            Key.Down => "key.keyboard.down",
            Key.Left => "key.keyboard.left",
            Key.Right => "key.keyboard.right",
            Key.Home => "key.keyboard.home",
            Key.End => "key.keyboard.end",
            Key.PageUp => "key.keyboard.page.up",
            Key.PageDown => "key.keyboard.page.down",
            Key.Insert => "key.keyboard.insert",
            Key.Delete => "key.keyboard.delete",
            _ => null
        };
    }

    public static bool TryGetVirtualKey(string mcKey, out int virtualKey)
    {
        virtualKey = 0;
        if (MinecraftToWpfKey(mcKey) is not { } key)
            return false;

        virtualKey = KeyInterop.VirtualKeyFromKey(key);
        return virtualKey > 0;
    }

    private static Key? MinecraftToWpfKey(string mcKey)
    {
        if (!mcKey.StartsWith("key.keyboard.", StringComparison.Ordinal))
            return null;

        var part = mcKey["key.keyboard.".Length..];
        if (part.Length == 1)
        {
            var c = char.ToUpperInvariant(part[0]);
            if (c is >= 'A' and <= 'Z') return (Key)((int)Key.A + (c - 'A'));
            if (c is >= '0' and <= '9') return (Key)((int)Key.D0 + (c - '0'));
        }

        if (part.StartsWith("keypad.", StringComparison.Ordinal) &&
            part.Length == "keypad.0".Length &&
            part[^1] is >= '0' and <= '9')
            return (Key)((int)Key.NumPad0 + (part[^1] - '0'));

        if (part.Length is 2 or 3 && part[0] == 'f' &&
            int.TryParse(part[1..], out var function) &&
            function is >= 1 and <= 12)
            return (Key)((int)Key.F1 + (function - 1));

        return part switch
        {
            "space" => Key.Space,
            "enter" => Key.Enter,
            "tab" => Key.Tab,
            "backspace" => Key.Back,
            "left.shift" => Key.LeftShift,
            "right.shift" => Key.RightShift,
            "left.control" => Key.LeftCtrl,
            "right.control" => Key.RightCtrl,
            "left.alt" => Key.LeftAlt,
            "right.alt" => Key.RightAlt,
            "caps.lock" => Key.CapsLock,
            "period" => Key.OemPeriod,
            "comma" => Key.OemComma,
            "minus" => Key.OemMinus,
            "equal" => Key.OemPlus,
            "semicolon" => Key.OemSemicolon,
            "apostrophe" => Key.OemQuotes,
            "slash" => Key.OemQuestion,
            "backslash" => Key.OemPipe,
            "left.bracket" => Key.OemOpenBrackets,
            "right.bracket" => Key.OemCloseBrackets,
            "grave.accent" => Key.OemTilde,
            "up" => Key.Up,
            "down" => Key.Down,
            "left" => Key.Left,
            "right" => Key.Right,
            "home" => Key.Home,
            "end" => Key.End,
            "page.up" => Key.PageUp,
            "page.down" => Key.PageDown,
            "insert" => Key.Insert,
            "delete" => Key.Delete,
            _ => null
        };
    }
}
