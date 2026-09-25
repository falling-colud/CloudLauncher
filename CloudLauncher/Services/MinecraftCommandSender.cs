using System.Runtime.InteropServices;

namespace CloudLauncher.Services;

/// <summary>
/// Send a chat command to a Minecraft Java Edition window.
/// </summary>
/// <remarks>
/// <para>Everything goes through <see cref="PostMessage"/>. We never call
/// <c>SetForegroundWindow</c>/<c>SetFocus</c> or move the cursor, so the launcher keeps the OS focus
/// and the player's cursor stays put.</para>
/// <para>GLFW pumps its message queue every frame with <c>TranslateMessage</c> and
/// <c>DispatchMessage</c>, so posted <c>WM_KEYDOWN</c>/<c>WM_KEYUP</c> drive keybinds like "Open Chat"
/// and <c>WM_CHAR</c> types text, even when the window is not in the foreground.</para>
/// <para>Because of <c>TranslateMessage</c>, posting the chat key also types that character (e.g.
/// <c>t</c>) into the chat field, so one backspace removes it before the command is typed.</para>
/// <para>Pausing is avoided by turning off Minecraft's <c>pauseOnLostFocus</c> at launch (see
/// <see cref="OptionsTxtService"/>), so closing the chat screen doesn't pause the unfocused game.</para>
/// </remarks>
public static class MinecraftCommandSender
{
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmChar = 0x0102;
    private const int VkReturn = 0x0D;
    private const int VkBack = 0x08;
    private const int VkDefaultChat = 0x54; // 'T'
    private const uint MapvkVkToVsc = 0;

    [Obsolete("Pass the chat virtual key; this overload assumes the default chat key 'T'.")]
    public static Task SendCommandAsync(IntPtr minecraftHwnd, string command, string chatMcKey, CancellationToken ct = default)
    {
        _ = chatMcKey;
        return SendCommandAsync(minecraftHwnd, command, VkDefaultChat, ct);
    }

    public static Task SendCommandAsync(IntPtr minecraftHwnd, string command, CancellationToken ct = default)
        => SendCommandAsync(minecraftHwnd, command, VkDefaultChat, ct);

    /// <summary>
    /// Open chat, type <paramref name="command"/> and submit it.
    /// <paramref name="chatVirtualKey"/> is the Windows virtual-key code of the player's
    /// "Open Chat" bind (defaults to <c>T</c>).
    /// </summary>
    public static async Task SendCommandAsync(IntPtr minecraftHwnd, string command, int chatVirtualKey, CancellationToken ct = default)
    {
        if (minecraftHwnd == IntPtr.Zero)
            return;

        command = CloudLauncher.Shared.PackText.NormalizeCommand(command);
        if (command.Length <= 1)
            return;

        if (chatVirtualKey <= 0)
            chatVirtualKey = VkDefaultChat;

        // 1. Open chat. If a screen is open already this is a no-op keybind, otherwise it
        //    fires the "Open Chat" bind. Either way GLFW's TranslateMessage turns the key
        //    into a character that lands in the chat field.
        PostKey(minecraftHwnd, chatVirtualKey);
        await Task.Delay(60, ct);

        // 2. Delete the stray character so it doesn't prefix the command.
        PostKey(minecraftHwnd, VkBack);
        await Task.Delay(20, ct);

        // 3. Type the command text, one UTF-16 code unit at a time.
        foreach (var ch in command)
            PostMessage(minecraftHwnd, WmChar, (IntPtr)ch, IntPtr.Zero);

        // Give Minecraft a couple of frames to ingest the characters before submitting,
        // so a long command does not race the Enter key.
        await Task.Delay(40, ct);

        // 4. Submit. Enter closes the chat screen; with pauseOnLostFocus disabled the
        //    unfocused game keeps running and does not grab the cursor, so it stays put.
        PostKey(minecraftHwnd, VkReturn);
    }

    private static void PostKey(IntPtr hwnd, int virtualKey)
    {
        var scanCode = MapVirtualKey((uint)virtualKey, MapvkVkToVsc);
        var downLParam = MakeKeyLParam(scanCode, extended: false, keyUp: false);
        var upLParam = MakeKeyLParam(scanCode, extended: false, keyUp: true);
        PostMessage(hwnd, WmKeyDown, (IntPtr)virtualKey, downLParam);
        PostMessage(hwnd, WmKeyUp, (IntPtr)virtualKey, upLParam);
    }

    private static IntPtr MakeKeyLParam(uint scanCode, bool extended, bool keyUp)
    {
        uint l = 0x00000001u; // repeat count = 1
        l |= (scanCode & 0xFFu) << 16;
        if (extended) l |= 0x01000000u;
        if (keyUp) l |= 0xC0000000u; // previous state + transition state
        return unchecked((IntPtr)(int)l);
    }

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "PostMessageW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
}
