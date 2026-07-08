using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

public partial class MinecraftHostWindow : Window
{
    internal const double DefaultRightPanelWidth = 640;
    private const double ExpandedBottomBarHeight = 30;
    private const double CollapsedStripThickness = 10;
    private const double TitleBarHeight = 36;
    private const double RightRailWidth = 28;

    private const int GwlStyle = -16;
    private const int GwlpHwndParent = -8;
    private const int WmActivate = 0x0006;
    private const int WmSetFocus = 0x0007;
    private const int WmHotkey = 0x0312;
    private const int WmClose = 0x0010;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmMouseActivate = 0x0021;
    private const int WmParentNotify = 0x0210;
    private const int WmWindowPosChanging = 0x0046;
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMButtonDown = 0x0207;
    private const int WmXButtonDown = 0x020B;
    private const int WaInactive = 0;
    private const int CursorShowing = 0x00000001;
    private const uint ModNoRepeat = 0x4000;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoRedraw = 0x0008;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpFrameChanged = 0x0020;
    // Causes SetWindowPos to post the position change to the target window's queue
    // instead of sending it synchronously. This prevents the UI thread from blocking
    // when Minecraft's message loop is temporarily frozen (GC pause, chunk load, etc.).
    private const uint SwpAsyncWindowPos = 0x4000;
    private const int WhKeyboardLl = 13;
    private const int VkTab = 0x09;
    // LLKHF_ALTDOWN — set in the low-level hook flags while Alt is held.
    private const int LlkhfAltDown = 0x20;
    private const long WsChild = 0x40000000L;
    private const long WsVisible = 0x10000000L;
    private const long WsPopup = 0x80000000L;
    private const long WsCaption = 0x00C00000L;
    private const long WsThickFrame = 0x00040000L;
    private const uint Th32CsSnapProcess = 0x00000002;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotopmost = new(-2);

    private readonly MainWindow _shell;
    private readonly PackDetail _pack;
    private readonly Stack<Page> _packNavStack = new();
    private readonly Process _process;
    private readonly HashSet<int> _trackedProcessIds = new();
    private readonly DateTimeOffset _startedAt;
    private Process? _attachedProcess;
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _attachTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _cursorCenterTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly int _collapseHotkeyId;
    private readonly int _fullscreenHotkeyId;

    // Background task that refreshes the Toolhelp32 process-tree snapshot.
    // Using a cached task avoids running CreateToolhelp32Snapshot on the UI thread.
    private Task<List<int>>? _descendantScanTask;

    private HwndSource? _source;
    private LowLevelKeyboardCallback? _keyboardHookProc;
    private IntPtr _minecraftHwnd;
    private IntPtr _keyboardHook;
    private nint _originalStyle;
    private double _rightPanelWidth = DefaultRightPanelWidth;
    private bool _rightCollapsed;
    private bool _bottomCollapsed;
    private bool _collapseHotkeyRegistered;
    private bool _fullscreenHotkeyRegistered;
    private bool _collapseUsesKeyboardHook;
    private bool _fullscreenUsesKeyboardHook;
    private bool _collapseShortcutAvailable;
    private bool _fullscreenShortcutAvailable;
    private bool _collapseHookKeyDown;
    private bool _fullscreenHookKeyDown;
    private bool _altTabHookKeyDown;
    private bool _isBorderlessFullscreen;
    private bool _hasRestoreBounds;
    private bool _cursorClipActive;
    private bool _isMovingOrSizing;
    private bool _hasDragGameOffset;
    private int _dragGameOffsetX;
    private int _dragGameOffsetY;
    private int _dragGameWidth;
    private int _dragGameHeight;
    private int _collapseVirtualKey;
    private int _fullscreenVirtualKey;
    private WindowState _restoreWindowState = WindowState.Normal;
    private Rect _restoreBounds;

    public MinecraftHostWindow(MainWindow shell, PackDetail pack, Process process)
    {
        InitializeComponent();
        _shell = shell;
        _pack = pack;
        _process = process;
        _startedAt = TryGetProcessStartTime(process) ?? DateTimeOffset.Now;
        _collapseHotkeyId = unchecked((int)(0xC10D0000 | (uint)process.Id));
        _fullscreenHotkeyId = unchecked((int)(0xC10E0000 | (uint)process.Id));

        Title = $"{pack.Name} - Minecraft";
        TitleLabel.Text = pack.Name;
        var packView = new PackDetailView(_shell, pack.Id, hostedInMinecraftWindow: true);
        packView.HostPanelWidthRequested += OnHostPanelWidthRequested;
        PackFrame.Navigate(packView);

        _clockTimer.Tick += (_, _) => UpdateClock();
        _attachTimer.Tick += (_, _) => AttachMinecraftWindow();
        _cursorCenterTimer.Tick += (_, _) => CenterCursorDuringMinecraftCameraLook();

        _process.EnableRaisingEvents = true;
        _process.Exited += OnLaunchProcessExited;
        _trackedProcessIds.Add(_process.Id);

        RefreshHotkeyLabels();
    }

    public void OpenModExplorerForPack(PackDetail pack) =>
        PushPackPage(new ModExplorerPage(_shell, pack), $"Browse mods · {pack.Name}");

    public void OpenResourcePackExplorerForPack(PackDetail pack) =>
        PushPackPage(new ResourcePackExplorerPage(_shell, pack), $"Browse resource packs · {pack.Name}");

    public void OpenLocalResourcePackDetail(string key, string title) =>
        PushPackPage(new LocalResourcePackDetailView(_shell, key), title);

    public void OpenModDetail(Guid modId, string title) =>
        PushPackPage(new ModDetailView(_shell, modId), title);

    public void OpenResourcePackDetail(Guid packId, string title) =>
        PushPackPage(new ResourcePackDetailView(_shell, packId), title);

    private void PushPackPage(Page page, string title)
    {
        if (PackFrame.Content is Page current)
            _packNavStack.Push(current);

        RightPanelTitle.Text = title;
        PackPanelBackButton.Visibility = Visibility.Visible;
        PackFrame.Navigate(page);

        if (_rightCollapsed)
        {
            _rightCollapsed = false;
            ApplyChromeState();
        }
    }

    private void PopPackPage()
    {
        if (_packNavStack.Count == 0)
            return;

        var prev = _packNavStack.Pop();
        RightPanelTitle.Text = prev is PackDetailView ? "Instance" : prev.Title ?? "Instance";
        PackPanelBackButton.Visibility = _packNavStack.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PackFrame.Navigate(prev);
    }

    private void OnPackPanelBack(object sender, RoutedEventArgs e) => PopPackPage();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _source?.AddHook(WndProc);

        ApplyInitialWindowState();
        RegisterHotkeys();
        UpdateClock();
        _clockTimer.Start();
        _attachTimer.Start();
        _cursorCenterTimer.Start();
        BringHostToFront();
        AttachMinecraftWindow();
        Dispatcher.BeginInvoke((Action)BringHostToFront, DispatcherPriority.ApplicationIdle);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!IsGameRunning())
            return;

        try
        {
            App.State.Instances.Stop(_pack.Id);
        }
        catch (Exception ex)
        {
            AppLog.LogError("close-minecraft-host", ex);
            var fallbackHandle = _minecraftHwnd != IntPtr.Zero
                ? _minecraftHwnd
                : TryGetProcessMainWindowHandle(_attachedProcess ?? _process);
            if (fallbackHandle != IntPtr.Zero)
                PostMessage(fallbackHandle, WmClose, 0, 0);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _clockTimer.Stop();
        _attachTimer.Stop();
        _cursorCenterTimer.Stop();
        ReleaseCursorClip();
        _process.Exited -= OnLaunchProcessExited;
        if (_attachedProcess is not null)
            _attachedProcess.Exited -= OnAttachedProcessExited;
        UnregisterHotkeys();
        _source?.RemoveHook(WndProc);
        DetachMinecraftWindow();
    }

    private void OnLaunchProcessExited(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(TryCloseHostIfFinished);

    private void OnAttachedProcessExited(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(TryCloseHostIfFinished);

    private void TryCloseHostIfFinished()
    {
        if (_minecraftHwnd != IntPtr.Zero && IsWindow(_minecraftHwnd))
            return;

        if (FindMinecraftWindow() != IntPtr.Zero)
        {
            AttachMinecraftWindow();
            return;
        }

        if (!IsLaunchProcessAlive() && IsVisible)
            Close();
    }

    private void AttachMinecraftWindow()
    {
        if (_minecraftHwnd != IntPtr.Zero)
        {
            if (IsWindow(_minecraftHwnd))
            {
                // The owned MC window is hidden while the host is minimized; that is
                // expected and must not trigger detach/re-attach (which restores the host).
                if (WindowState == WindowState.Minimized)
                    return;

                if (IsWindowVisible(_minecraftHwnd))
                    return;
            }

            DetachMinecraftWindow();
        }

        if (WindowState == WindowState.Minimized)
            return;

        var handle = FindMinecraftWindow();
        if (handle == IntPtr.Zero)
        {
            if (!IsLaunchProcessAlive())
                TryCloseHostIfFinished();
            return;
        }

        var wrapperHandle = new WindowInteropHelper(this).Handle;
        if (wrapperHandle == IntPtr.Zero)
            return;

        _minecraftHwnd = handle;
        _originalStyle = GetWindowLongPtr(_minecraftHwnd, GwlStyle);

        // Keep MC as its own top-level window (WS_POPUP, not WS_CHILD). Reparenting
        // it as a child made MC's process non-foreground, so GLFW's disabled-cursor
        // mode (SetCursor(NULL) + ClipCursor) silently failed and the cursor stayed
        // visible and escaped the window. As an owned popup, MC is still tied to the
        // host (minimizes/closes with it, stays above it in z-order) but acts as its
        // own foreground window — GLFW's cursor handling works natively.
        var style = _originalStyle.ToInt64();
        style &= ~WsChild;
        style &= ~WsCaption;
        style &= ~WsThickFrame;
        style |= WsPopup | WsVisible;

        SetWindowLongPtr(_minecraftHwnd, GwlStyle, new nint(style));
        SetWindowLongPtr(_minecraftHwnd, GwlpHwndParent, wrapperHandle);
        SetWindowPos(_minecraftHwnd, IntPtr.Zero, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged | SwpShowWindow | SwpAsyncWindowPos);

        TrackAttachedProcess(handle);
        AttachStatusLabel.Visibility = Visibility.Collapsed;
        MoveMinecraftWindow();
        BringHostToFront();
        QueueMinecraftFocus();
    }

    private void TrackAttachedProcess(IntPtr handle)
    {
        GetWindowThreadProcessId(handle, out var ownerPid);
        _trackedProcessIds.Add(ownerPid);
        if (ownerPid == _process.Id || _attachedProcess?.Id == ownerPid)
            return;

        Process? gameProcess = null;
        try
        {
            gameProcess = Process.GetProcessById(ownerPid);
            gameProcess.EnableRaisingEvents = true;
        }
        catch
        {
            return;
        }

        if (_attachedProcess is not null)
            _attachedProcess.Exited -= OnAttachedProcessExited;

        _attachedProcess = gameProcess;
        _attachedProcess.Exited += OnAttachedProcessExited;
        App.State.Instances.TryHandoffRunningProcess(_pack.Id, _process, gameProcess);
    }

    private void DetachMinecraftWindow()
    {
        if (_minecraftHwnd == IntPtr.Zero || !IsWindow(_minecraftHwnd))
            return;

        SetWindowLongPtr(_minecraftHwnd, GwlpHwndParent, IntPtr.Zero);
        if (_originalStyle != 0)
            SetWindowLongPtr(_minecraftHwnd, GwlStyle, _originalStyle);

        SetWindowPos(_minecraftHwnd, IntPtr.Zero, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged | SwpShowWindow | SwpAsyncWindowPos);
        _minecraftHwnd = IntPtr.Zero;
    }

    private void MoveMinecraftWindow(bool repaint = true)
    {
        if (_minecraftHwnd == IntPtr.Zero || !IsLoaded || WindowState == WindowState.Minimized)
            return;

        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null)
            return;

        var deviceTopLeft = GameSurface.PointToScreen(new Point(0, 0));
        var deviceBottomRight = GameSurface.PointToScreen(
            new Point(GameSurface.ActualWidth, GameSurface.ActualHeight));

        var x = (int)Math.Round(deviceTopLeft.X);
        var y = (int)Math.Round(deviceTopLeft.Y);
        var width = Math.Max(1, (int)Math.Round(deviceBottomRight.X - deviceTopLeft.X));
        var height = Math.Max(1, (int)Math.Round(deviceBottomRight.Y - deviceTopLeft.Y));

        // If a borderless WS_POPUP top-level window's bounds exactly match the
        // monitor, Windows engages "fullscreen optimization" and the app behaves
        // like exclusive fullscreen (taskbar lost, DWM bypass, mode flicker).
        // Shrink by one invisible pixel so MC stays in true borderless-windowed
        // mode covering the taskbar.
        if (TryGetMonitorBounds(new WindowInteropHelper(this).Handle, out var monitor) &&
            x <= monitor.Left &&
            y <= monitor.Top &&
            x + width >= monitor.Right &&
            y + height >= monitor.Bottom)
        {
            height -= 1;
        }

        var moveFlags = SwpNoZOrder | SwpNoActivate | SwpAsyncWindowPos;
        if (!repaint) moveFlags |= SwpNoRedraw;
        SetWindowPos(_minecraftHwnd, IntPtr.Zero, x, y, width, height, moveFlags);
    }

    private void CacheDragGameGeometry()
    {
        _hasDragGameOffset = false;
        if (_minecraftHwnd == IntPtr.Zero || !IsLoaded)
            return;

        var wrapperHandle = new WindowInteropHelper(this).Handle;
        if (wrapperHandle == IntPtr.Zero ||
            !GetWindowRect(wrapperHandle, out var hostRect) ||
            GameSurface.ActualWidth <= 0 ||
            GameSurface.ActualHeight <= 0)
        {
            return;
        }

        var topLeft = GameSurface.PointToScreen(new Point(0, 0));
        var bottomRight = GameSurface.PointToScreen(
            new Point(GameSurface.ActualWidth, GameSurface.ActualHeight));

        _dragGameOffsetX = (int)Math.Round(topLeft.X - hostRect.Left);
        _dragGameOffsetY = (int)Math.Round(topLeft.Y - hostRect.Top);
        _dragGameWidth = Math.Max(1, (int)Math.Round(bottomRight.X - topLeft.X));
        _dragGameHeight = Math.Max(1, (int)Math.Round(bottomRight.Y - topLeft.Y));
        _hasDragGameOffset = true;
    }

    private void MoveMinecraftWindowDuringHostDrag(int hostX, int hostY)
    {
        if (_minecraftHwnd == IntPtr.Zero || !_hasDragGameOffset)
            return;

        SetWindowPos(
            _minecraftHwnd,
            IntPtr.Zero,
            hostX + _dragGameOffsetX,
            hostY + _dragGameOffsetY,
            _dragGameWidth,
            _dragGameHeight,
            SwpNoZOrder | SwpNoActivate | SwpNoRedraw | SwpAsyncWindowPos);
    }

    public void RunMinecraftCommand(string command)
    {
        if (_minecraftHwnd == IntPtr.Zero || string.IsNullOrWhiteSpace(command))
            return;

        // Don't steal foreground or move the cursor. The sender posts everything to the
        // Minecraft window's message queue (open chat → type → submit → reopen chat),
        // which works while the launcher panel keeps focus and the cursor stays put.
        _ = SendMinecraftCommandAsync(command);
    }

    private async Task SendMinecraftCommandAsync(string command)
    {
        try
        {
            if (_minecraftHwnd == IntPtr.Zero || !IsGameRunning())
                return;

            await MinecraftCommandSender.SendCommandAsync(_minecraftHwnd, command, ResolveChatVirtualKey());
        }
        catch (Exception ex)
        {
            AppLog.LogError("minecraft-command", ex);
        }
    }

    // Resolve the player's "Open Chat" bind (Minecraft options.txt key id, e.g.
    // "key.keyboard.t") to a Windows virtual-key code so we open the right chat key.
    private static int ResolveChatVirtualKey()
    {
        const int vkDefaultChat = 0x54; // 'T'
        var keyId = App.State.Settings.McDefaults.KeyChat;
        if (string.IsNullOrWhiteSpace(keyId))
            return vkDefaultChat;

        const string prefix = "key.keyboard.";
        var name = keyId.Trim();
        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            name = name[prefix.Length..];
        name = name.ToLowerInvariant();

        if (name.Length == 1)
        {
            var c = name[0];
            if (c is >= 'a' and <= 'z')
                return 'A' + (c - 'a');
            if (c is >= '0' and <= '9')
                return c;
        }

        return name switch
        {
            "slash" => 0xBF,        // VK_OEM_2
            "semicolon" => 0xBA,    // VK_OEM_1
            "period" => 0xBE,       // VK_OEM_PERIOD
            "comma" => 0xBC,        // VK_OEM_COMMA
            "apostrophe" => 0xDE,   // VK_OEM_7
            "grave.accent" => 0xC0, // VK_OEM_3
            "minus" => 0xBD,        // VK_OEM_MINUS
            "equal" => 0xBB,        // VK_OEM_PLUS
            "left.bracket" => 0xDB, // VK_OEM_4
            "right.bracket" => 0xDD,// VK_OEM_6
            "backslash" => 0xDC,    // VK_OEM_5
            _ => vkDefaultChat
        };
    }

    private void QueueMinecraftFocus(
        bool requireCursorOverGameSurface = false,
        bool onlyWhenWpfDoesNotHaveFocus = false,
        bool allowHostActivation = false)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (requireCursorOverGameSurface && !IsCursorOverGameSurface())
                return;

            // Activation-triggered focus (WM_ACTIVATE / WM_SETFOCUS) must never yank
            // focus to Minecraft while the user is interacting with the launcher chrome
            // (right instance panel, title bar, etc.). The cursor being off the game
            // surface is the reliable signal for that. IsKeyboardFocusWithin is NOT
            // reliable here: the description WebBrowser holds raw Win32 focus in its own
            // hosted HWND, which WPF does not report as keyboard focus — so the old check
            // let the game steal focus mid-click, eating the click before IE saw it and
            // making command links require repeated/spam clicking.
            if (onlyWhenWpfDoesNotHaveFocus && (IsKeyboardFocusWithin || !IsCursorOverGameSurface()))
                return;

            FocusMinecraftWindow(allowHostActivation);
        }, DispatcherPriority.Input);
    }

    private void FocusMinecraftWindow(bool allowHostActivation = false)
    {
        if (_minecraftHwnd == IntPtr.Zero || !IsWindow(_minecraftHwnd) || !IsGameRunning())
            return;

        var wrapperHandle = new WindowInteropHelper(this).Handle;
        var foregroundWindow = GetForegroundWindow();
        if (wrapperHandle != IntPtr.Zero &&
            foregroundWindow != wrapperHandle &&
            foregroundWindow != _minecraftHwnd)
        {
            if (!allowHostActivation)
                return;

            SetWindowPos(wrapperHandle, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpShowWindow);
            SetForegroundWindow(wrapperHandle);
        }

        SetFocusAcrossThreads(_minecraftHwnd);
        CenterCursorDuringMinecraftCameraLook();
    }

    private void BringHostToFront()
    {
        if (!IsVisible || WindowState == WindowState.Minimized)
            return;

        var wrapperHandle = new WindowInteropHelper(this).Handle;
        if (wrapperHandle != IntPtr.Zero)
            SetWindowPos(wrapperHandle, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpShowWindow);

        Activate();

        if (_isBorderlessFullscreen)
        {
            ApplyBorderlessZOrder();
            return;
        }

        // Java/GLFW can grab foreground during startup; briefly pulsing topmost
        // makes the host visible before focus is handed to the embedded HWND.
        Topmost = true;
        Topmost = false;
    }

    private bool IsCursorOverGameSurface()
    {
        if (!IsLoaded || GameSurface.ActualWidth <= 0 || GameSurface.ActualHeight <= 0)
            return false;

        if (!GetCursorPos(out var cursor))
            return false;

        var point = GameSurface.PointFromScreen(new Point(cursor.X, cursor.Y));
        return point.X >= 0 &&
            point.Y >= 0 &&
            point.X < GameSurface.ActualWidth &&
            point.Y < GameSurface.ActualHeight;
    }

    private void RegisterHotkeys()
    {
        RegisterCollapseHotkey();
        RegisterFullscreenHotkey();
        EnsureKeyboardHook();
        RefreshHotkeyLabels(showRegistrationState: true);
    }

    private void RegisterCollapseHotkey()
    {
        _collapseVirtualKey = 0;
        _collapseHotkeyRegistered = false;
        _collapseUsesKeyboardHook = false;
        _collapseShortcutAvailable = false;

        if (_source is null)
            return;

        var key = App.State.Settings.MinecraftWindowToggleKey;
        if (!LauncherKeybinds.TryGetVirtualKey(key, out var virtualKey))
            return;

        _collapseVirtualKey = virtualKey;
        _collapseHotkeyRegistered = RegisterHotKey(_source.Handle, _collapseHotkeyId, ModNoRepeat, (uint)virtualKey);
        _collapseUsesKeyboardHook = !_collapseHotkeyRegistered;
        _collapseShortcutAvailable = true;
    }

    private void RegisterFullscreenHotkey()
    {
        _fullscreenVirtualKey = 0;
        _fullscreenHotkeyRegistered = false;
        _fullscreenUsesKeyboardHook = false;
        _fullscreenShortcutAvailable = false;

        if (_source is null)
            return;

        var key = App.State.Settings.MinecraftWindowFullscreenKey;
        if (!LauncherKeybinds.TryGetVirtualKey(key, out var virtualKey))
            return;

        _fullscreenVirtualKey = virtualKey;
        _fullscreenHotkeyRegistered = RegisterHotKey(_source.Handle, _fullscreenHotkeyId, ModNoRepeat, (uint)virtualKey);
        _fullscreenUsesKeyboardHook = !_fullscreenHotkeyRegistered;
        _fullscreenShortcutAvailable = true;
    }

    private void EnsureKeyboardHook()
    {
        // The hook is always installed: Alt+Tab is reserved by the OS and cannot be
        // captured via RegisterHotKey, so a low-level keyboard hook is the only way to
        // intercept it. It also doubles as the F8/F11 fallback when RegisterHotKey fails.
        _keyboardHookProc ??= LowLevelKeyboardProc;
        var moduleName = Process.GetCurrentProcess().MainModule?.ModuleName;
        var moduleHandle = moduleName is null ? IntPtr.Zero : GetModuleHandle(moduleName);
        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardHookProc, moduleHandle, 0);

        if (_keyboardHook != IntPtr.Zero)
            return;

        if (_collapseUsesKeyboardHook)
            _collapseShortcutAvailable = false;
        if (_fullscreenUsesKeyboardHook)
            _fullscreenShortcutAvailable = false;
        _collapseUsesKeyboardHook = false;
        _fullscreenUsesKeyboardHook = false;
    }

    private void UnregisterHotkeys()
    {
        if (_source is null)
            return;

        if (_collapseHotkeyRegistered)
            UnregisterHotKey(_source.Handle, _collapseHotkeyId);
        if (_fullscreenHotkeyRegistered)
            UnregisterHotKey(_source.Handle, _fullscreenHotkeyId);
        _collapseHotkeyRegistered = false;
        _fullscreenHotkeyRegistered = false;

        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        _keyboardHookProc = null;
        _collapseUsesKeyboardHook = false;
        _fullscreenUsesKeyboardHook = false;
        _collapseShortcutAvailable = false;
        _fullscreenShortcutAvailable = false;
        _collapseHookKeyDown = false;
        _fullscreenHookKeyDown = false;
        _altTabHookKeyDown = false;
    }

    private void RefreshHotkeyLabels(bool showRegistrationState = false)
    {
        var collapseKeyName = LauncherKeybinds.PrettyName(App.State.Settings.MinecraftWindowToggleKey);
        var fullscreenKeyName = LauncherKeybinds.PrettyName(App.State.Settings.MinecraftWindowFullscreenKey);

        var collapseText = showRegistrationState && !_collapseShortcutAvailable
            ? $"{collapseKeyName}: unavailable"
            : $"{collapseKeyName}: collapse bars";
        var fullscreenText = showRegistrationState && !_fullscreenShortcutAvailable
            ? $"{fullscreenKeyName}: unavailable"
            : $"{fullscreenKeyName}: fullscreen";

        HotkeyLabel.Text = $"{collapseText} • {fullscreenText}";
        OverlayHintLabel.Text = $"{collapseKeyName} collapses bars • {fullscreenKeyName} toggles borderless fullscreen";
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (WindowWorkArea.TryHandleGetMinMaxInfo(hwnd, msg, lParam, _isBorderlessFullscreen))
        {
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == WmHotkey && wParam.ToInt32() == _collapseHotkeyId)
        {
            ToggleLauncherBars();
            handled = true;
        }
        else if (msg == WmHotkey && wParam.ToInt32() == _fullscreenHotkeyId)
        {
            ToggleBorderlessFullscreen();
            handled = true;
        }
        else if (msg == WmEnterSizeMove)
        {
            _isMovingOrSizing = true;
            ReleaseCursorClip();
            CacheDragGameGeometry();
        }
        else if (msg == WmWindowPosChanging && _isMovingOrSizing && _minecraftHwnd != IntPtr.Zero)
        {
            var pos = Marshal.PtrToStructure<NativeWindowPos>(lParam);
            if ((pos.Flags & (SwpNoMove | SwpNoSize)) == (SwpNoMove | SwpNoSize))
                return IntPtr.Zero;

            if ((pos.Flags & SwpNoMove) == 0 &&
                (pos.Flags & SwpNoSize) != 0 &&
                _hasDragGameOffset)
            {
                MoveMinecraftWindowDuringHostDrag(pos.X, pos.Y);
            }
            else if ((pos.Flags & SwpNoSize) == 0)
            {
                MoveMinecraftWindow(repaint: false);
            }
        }
        else if (msg == WmExitSizeMove)
        {
            _isMovingOrSizing = false;
            _hasDragGameOffset = false;
            MoveMinecraftWindow();
        }
        else if (msg == WmActivate)
        {
            if (LowWord(wParam) != WaInactive)
                QueueMinecraftFocus(onlyWhenWpfDoesNotHaveFocus: true);
        }
        else if (msg == WmSetFocus)
        {
            QueueMinecraftFocus(onlyWhenWpfDoesNotHaveFocus: true);
        }
        else if (msg == WmMouseActivate ||
            (msg == WmParentNotify && IsMouseButtonMessage(LowWord(wParam))))
        {
            QueueMinecraftFocus(requireCursorOverGameSurface: true, allowHostActivation: true);
        }

        return IntPtr.Zero;
    }

    private void ApplyInitialWindowState()
    {
        // Match the F8 toggle: from a default state, pressing F8 collapses both
        // the right instance page and the bottom bar (fully hiding launcher chrome).
        // The "start collapsed" setting should produce the same end state.
        var collapsed = App.State.Settings.GetMinecraftWindowPackPageCollapsedByDefault(_pack.Id);
        _rightCollapsed = collapsed;
        _bottomCollapsed = collapsed;
        ApplyChromeState();

        if (App.State.Settings.GetMinecraftWindowBorderlessFullscreenByDefault(_pack.Id))
            SetBorderlessFullscreen(true, rememberRestore: true);
    }

    private void ToggleLauncherBars()
    {
        if (_rightCollapsed && !_bottomCollapsed)
        {
            _rightCollapsed = false;
            ApplyChromeState();
            return;
        }

        var collapse = !_bottomCollapsed || !_rightCollapsed;
        _bottomCollapsed = collapse;
        _rightCollapsed = collapse;
        ApplyChromeState();
    }

    private void OnHostPanelWidthRequested(object? sender, double width)
    {
        if (Math.Abs(_rightPanelWidth - width) < 0.5)
            return;

        _rightPanelWidth = width;
        ApplyChromeState();
    }

    private void ApplyChromeState()
    {
        var fullyHidden = _rightCollapsed && _bottomCollapsed;

        TopRow.Height = new GridLength(fullyHidden ? 0 : TitleBarHeight);
        TitleBar.Visibility = fullyHidden ? Visibility.Collapsed : Visibility.Visible;
        var chrome = WindowChrome.GetWindowChrome(this);
        if (chrome is not null)
            chrome.CaptionHeight = fullyHidden ? 0 : TitleBarHeight;
        RootBorder.BorderThickness = fullyHidden ? new Thickness(0) : new Thickness(1);

        var bottomHeight = _bottomCollapsed
            ? (fullyHidden ? 0 : CollapsedStripThickness)
            : ExpandedBottomBarHeight;
        BottomRow.Height = new GridLength(bottomHeight);
        BottomBar.Visibility = _bottomCollapsed ? Visibility.Collapsed : Visibility.Visible;
        BottomCollapsedStrip.Visibility = _bottomCollapsed && !fullyHidden ? Visibility.Visible : Visibility.Collapsed;

        var showCollapsedRightRail = _rightCollapsed && !fullyHidden;
        RightToggleColumn.Width = new GridLength(showCollapsedRightRail ? RightRailWidth : 0);
        RightCollapseRail.Visibility = showCollapsedRightRail ? Visibility.Visible : Visibility.Collapsed;
        RightColumn.Width = new GridLength(_rightCollapsed ? 0 : _rightPanelWidth);
        RightPanel.Visibility = _rightCollapsed ? Visibility.Collapsed : Visibility.Visible;
        ExpandedRightCollapseButton.Visibility = _rightCollapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapsedRightCollapseButton.Content = char.ConvertFromUtf32(0xE76B);
        CollapsedRightCollapseButton.ToolTip = "Show instance page";
        ExpandedRightCollapseButton.Content = char.ConvertFromUtf32(0xE76C);
        ExpandedRightCollapseButton.ToolTip = "Hide instance page";

        Dispatcher.BeginInvoke(() => MoveMinecraftWindow(), DispatcherPriority.Loaded);
    }

    private void ToggleBorderlessFullscreen() =>
        SetBorderlessFullscreen(!_isBorderlessFullscreen, rememberRestore: true);

    private void SetBorderlessFullscreen(bool enabled, bool rememberRestore)
    {
        if (enabled == _isBorderlessFullscreen)
            return;

        var wrapperHandle = new WindowInteropHelper(this).Handle;

        if (enabled)
        {
            if (rememberRestore || !_hasRestoreBounds)
            {
                _restoreWindowState = WindowState;
                _restoreBounds = new Rect(Left, Top, Width, Height);
                _hasRestoreBounds = true;
            }

            _isBorderlessFullscreen = true;
            ApplyBorderlessZOrder();

            if (WindowState != WindowState.Normal)
                WindowState = WindowState.Normal;

            if (wrapperHandle != IntPtr.Zero && TryGetMonitorBounds(wrapperHandle, out var monitor))
            {
                SetWindowPos(wrapperHandle, HwndTopmost,
                    monitor.Left, monitor.Top,
                    monitor.Right - monitor.Left, monitor.Bottom - monitor.Top,
                    SwpFrameChanged | SwpShowWindow);
            }
        }
        else
        {
            _isBorderlessFullscreen = false;
            ApplyBorderlessZOrder();
            if (wrapperHandle != IntPtr.Zero)
            {
                SetWindowPos(wrapperHandle, HwndNotopmost, 0, 0, 0, 0,
                    SwpNoMove | SwpNoSize | SwpFrameChanged | SwpShowWindow);
            }

            if (_restoreWindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Maximized;
            }
            else
            {
                WindowState = WindowState.Normal;
                if (_hasRestoreBounds)
                {
                    Left = _restoreBounds.Left;
                    Top = _restoreBounds.Top;
                    Width = _restoreBounds.Width;
                    Height = _restoreBounds.Height;
                }
            }
        }

        UpdateLayout();
        MoveMinecraftWindow();
        Dispatcher.BeginInvoke(() => MoveMinecraftWindow(), DispatcherPriority.Loaded);
    }

    private void ApplyBorderlessZOrder()
    {
        var wrapperHandle = new WindowInteropHelper(this).Handle;
        if (wrapperHandle == IntPtr.Zero)
            return;

        // Stay topmost for the entire borderless session, even when MC owns
        // foreground — otherwise the taskbar pops back above the host.
        var keepTopmost = _isBorderlessFullscreen;
        Topmost = keepTopmost;
        SetWindowPos(wrapperHandle,
            keepTopmost ? HwndTopmost : HwndNotopmost,
            0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpShowWindow);
    }

    private static bool TryGetMonitorBounds(IntPtr hwnd, out NativeRect bounds)
    {
        bounds = default;
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
            return false;

        var info = new NativeMonitorInfo { CbSize = Marshal.SizeOf<NativeMonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
            return false;

        bounds = info.RcMonitor;
        return true;
    }

    private void CenterCursorDuringMinecraftCameraLook()
    {
        if (!ShouldCenterMinecraftCursor() || !TryGetMinecraftWindowRect(out var rect))
        {
            ReleaseCursorClip();
            return;
        }

        ApplyCursorClip(rect);

        var centerX = rect.Left + ((rect.Right - rect.Left) / 2);
        var centerY = rect.Top + ((rect.Bottom - rect.Top) / 2);
        if (GetCursorPos(out var cursor) &&
            Math.Abs(cursor.X - centerX) <= 1 &&
            Math.Abs(cursor.Y - centerY) <= 1)
        {
            return;
        }

        SetCursorPos(centerX, centerY);
    }

    private void ApplyCursorClip(NativeRect rect)
    {
        if (ClipCursor(ref rect))
            _cursorClipActive = true;
    }

    private void ReleaseCursorClip()
    {
        if (!_cursorClipActive)
            return;

        ClipCursor(IntPtr.Zero);
        _cursorClipActive = false;
    }

    private bool ShouldCenterMinecraftCursor()
    {
        if (_isMovingOrSizing ||
            _minecraftHwnd == IntPtr.Zero ||
            !IsWindow(_minecraftHwnd) ||
            !IsGameRunning())
            return false;

        var wrapperHandle = new WindowInteropHelper(this).Handle;
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow != wrapperHandle && foregroundWindow != _minecraftHwnd)
            return false;

        return WindowState != WindowState.Minimized && IsMinecraftInMouseLook();
    }

    private bool IsMinecraftInMouseLook()
    {
        // GLFW's disabled-cursor mode (what Minecraft uses for camera look) does
        // two things on Windows: it sets the cursor image to NULL via SetCursor,
        // and it confines the cursor with ClipCursor(window-rect). Neither the
        // CURSOR_SHOWING flag nor CURSORINFO.hCursor reflects SetCursor(NULL)
        // reliably from another process, so detect the clip instead.
        //
        // When MC opens a menu it calls ClipCursor(NULL), which also releases
        // any clip we previously applied — that's how we know to stop clipping.
        if (!GetClipCursor(out var clip))
            return false;

        var virtualWidth = GetSystemMetrics(SmCxVirtualScreen);
        var virtualHeight = GetSystemMetrics(SmCyVirtualScreen);
        if (virtualWidth <= 0 || virtualHeight <= 0)
            return false;

        // Allow a small margin to avoid false positives from DPI rounding.
        return (clip.Right - clip.Left) + 32 < virtualWidth
            || (clip.Bottom - clip.Top) + 32 < virtualHeight;
    }

    private IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = wParam.ToInt32();
            if (message is WmKeyDown or WmSysKeyDown or WmKeyUp or WmSysKeyUp)
            {
                var hookStruct = Marshal.PtrToStructure<KeyboardHookStruct>(lParam);
                var virtualKey = hookStruct.VkCode;
                var isKeyDown = message is WmKeyDown or WmSysKeyDown;

                if (HandleAltTabShortcut(virtualKey, hookStruct.Flags, isKeyDown))
                    return 1;

                if (HandleFallbackShortcut(virtualKey, isKeyDown))
                    return 1;
            }
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private bool HandleFallbackShortcut(int virtualKey, bool isKeyDown)
    {
        if (!isKeyDown)
        {
            var wasFallbackKey = false;
            if (_collapseUsesKeyboardHook && virtualKey == _collapseVirtualKey)
            {
                _collapseHookKeyDown = false;
                wasFallbackKey = true;
            }

            if (_fullscreenUsesKeyboardHook && virtualKey == _fullscreenVirtualKey)
            {
                _fullscreenHookKeyDown = false;
                wasFallbackKey = true;
            }

            return wasFallbackKey && IsHostOrMinecraftForeground();
        }

        if (!IsHostOrMinecraftForeground())
            return false;

        if (_collapseUsesKeyboardHook && virtualKey == _collapseVirtualKey)
        {
            if (!_collapseHookKeyDown)
            {
                _collapseHookKeyDown = true;
                Dispatcher.BeginInvoke((Action)ToggleLauncherBars, DispatcherPriority.Input);
            }

            return true;
        }

        if (_fullscreenUsesKeyboardHook && virtualKey == _fullscreenVirtualKey)
        {
            if (!_fullscreenHookKeyDown)
            {
                _fullscreenHookKeyDown = true;
                Dispatcher.BeginInvoke((Action)ToggleBorderlessFullscreen, DispatcherPriority.Input);
            }

            return true;
        }

        return false;
    }

    private bool HandleAltTabShortcut(int virtualKey, int hookFlags, bool isKeyDown)
    {
        if (virtualKey != VkTab)
            return false;

        // Alt+Tab surfaces as WM_SYSKEYDOWN for VK_TAB with the ALT-down flag set.
        // Only override it while the host or the embedded game owns the foreground —
        // otherwise the normal task switcher must keep working for the rest of Windows.
        var altDown = (hookFlags & LlkhfAltDown) != 0;
        if (!altDown || !IsHostOrMinecraftForeground())
        {
            _altTabHookKeyDown = false;
            return false;
        }

        if (!isKeyDown)
        {
            _altTabHookKeyDown = false;
            return true;
        }

        if (!_altTabHookKeyDown)
        {
            _altTabHookKeyDown = true;
            Dispatcher.BeginInvoke((Action)MinimizeFromAltTab, DispatcherPriority.Input);
        }

        return true;
    }

    private void MinimizeFromAltTab()
    {
        if (!IsGameRunning() || WindowState == WindowState.Minimized)
            return;

        // Minimizing the host hides the owned MC popup with it (see AttachMinecraftWindow).
        // Drop the borderless topmost flag first so the host can't linger above the
        // taskbar / other apps once it is restored from the task bar.
        if (_isBorderlessFullscreen)
        {
            Topmost = false;
            var wrapperHandle = new WindowInteropHelper(this).Handle;
            if (wrapperHandle != IntPtr.Zero)
                SetWindowPos(wrapperHandle, HwndNotopmost, 0, 0, 0, 0,
                    SwpNoMove | SwpNoSize | SwpNoActivate);
        }

        ReleaseCursorClip();
        WindowState = WindowState.Minimized;
    }

    private bool IsHostOrMinecraftForeground()
    {
        var wrapperHandle = new WindowInteropHelper(this).Handle;
        var foregroundWindow = GetForegroundWindow();
        return foregroundWindow != IntPtr.Zero &&
            (foregroundWindow == wrapperHandle || foregroundWindow == _minecraftHwnd);
    }

    private bool TryGetMinecraftWindowRect(out NativeRect rect)
    {
        rect = default;
        if (_minecraftHwnd == IntPtr.Zero ||
            !IsWindow(_minecraftHwnd) ||
            !IsGameRunning() ||
            WindowState == WindowState.Minimized)
        {
            return false;
        }

        return GetWindowRect(_minecraftHwnd, out rect) &&
            rect.Right > rect.Left &&
            rect.Bottom > rect.Top;
    }

    private void UpdateClock()
    {
        var now = DateTimeOffset.Now;
        ClockLabel.Text = now.ToString("HH:mm:ss");
        ElapsedLabel.Text = FormatElapsed(now - _startedAt);
    }

    private static string FormatElapsed(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
            value = TimeSpan.Zero;
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes:00}:{value.Seconds:00}";
    }

    private static DateTimeOffset? TryGetProcessStartTime(Process process)
    {
        try { return process.StartTime; }
        catch { return null; }
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch { return true; }
    }

    private IntPtr FindMinecraftWindow()
    {
        RefreshTrackedProcessIds();

        var hostHandle = IsLoaded
            ? new WindowInteropHelper(this).Handle
            : IntPtr.Zero;

        IntPtr bestHandle = IntPtr.Zero;
        var bestScore = 0;

        EnumWindows((handle, _) =>
        {
            if (handle == hostHandle || !IsUsableTopLevelWindow(handle))
                return true;

            GetWindowThreadProcessId(handle, out var pid);
            var title = GetWindowTitle(handle);
            var inTree = _trackedProcessIds.Contains(pid);
            var fallbackCandidate = !inTree &&
                                    LooksLikeGameWindowTitle(title) &&
                                    LooksLikeJavaGameProcess(pid);

            if (!inTree && !fallbackCandidate)
                return true;

            var score = ScoreGameWindow(handle, title, inTree);
            if (score <= bestScore)
                return true;

            bestScore = score;
            bestHandle = handle;
            return true;
        }, IntPtr.Zero);

        return bestHandle;
    }

    private void RefreshTrackedProcessIds()
    {
        _trackedProcessIds.Add(_process.Id);

        // Merge results from the most recently completed background scan.
        // _trackedProcessIds is accumulative so stale results are harmless.
        if (_descendantScanTask is { IsCompletedSuccessfully: true })
            foreach (var pid in _descendantScanTask.Result)
                _trackedProcessIds.Add(pid);

        // Kick off a fresh scan on the thread pool so the next timer tick
        // benefits from an up-to-date process tree without stalling the UI.
        if (_descendantScanTask is null || _descendantScanTask.IsCompleted)
        {
            var rootPid = _process.Id;
            _descendantScanTask = Task.Run(() => GetDescendantProcessIds(rootPid).ToList());
        }

        if (_attachedProcess is { HasExited: false })
            _trackedProcessIds.Add(_attachedProcess.Id);
    }

    private bool IsLaunchProcessAlive()
    {
        if (!HasExited(_process))
            return true;

        RefreshTrackedProcessIds();
        foreach (var pid in _trackedProcessIds)
        {
            if (pid == _process.Id)
                continue;

            try
            {
                using var process = Process.GetProcessById(pid);
                if (!process.HasExited)
                    return true;
            }
            catch { }
        }

        return false;
    }

    private bool IsGameRunning()
    {
        if (_minecraftHwnd != IntPtr.Zero && IsWindow(_minecraftHwnd))
            return true;

        return IsLaunchProcessAlive();
    }

    private bool LooksLikeJavaGameProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
                return false;

            var name = process.ProcessName;
            if (!name.Equals("java", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("javaw", StringComparison.OrdinalIgnoreCase))
                return false;

            var startTime = TryGetProcessStartTime(process);
            return startTime is null || startTime >= _startedAt.AddSeconds(-10);
        }
        catch
        {
            return false;
        }
    }

    private static int ScoreGameWindow(IntPtr handle, string title, bool inTree)
    {
        if (!GetWindowRect(handle, out var rect))
            return 0;

        var width = Math.Max(0, rect.Right - rect.Left);
        var height = Math.Max(0, rect.Bottom - rect.Top);
        if (width < 200 || height < 200)
            return 0;

        var score = width * height / 1000;
        if (LooksLikeGameWindowTitle(title))
            score += 500_000;
        if (inTree)
            score += 100_000;

        return score;
    }

    private static bool LooksLikeGameWindowTitle(string title) =>
        title.Contains("Minecraft", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("Cleanroom", StringComparison.OrdinalIgnoreCase);

    private static string GetWindowTitle(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0)
            return string.Empty;

        var builder = new StringBuilder(length + 1);
        _ = GetWindowText(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    private static IEnumerable<int> GetDescendantProcessIds(int rootPid)
    {
        var snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            yield break;

        try
        {
            var childrenByParent = new Dictionary<int, List<int>>();
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry))
                yield break;

            do
            {
                var parentId = (int)entry.th32ParentProcessID;
                var processId = (int)entry.th32ProcessID;
                if (!childrenByParent.TryGetValue(parentId, out var children))
                {
                    children = new List<int>();
                    childrenByParent[parentId] = children;
                }

                children.Add(processId);
            } while (Process32Next(snapshot, ref entry));

            var queue = new Queue<int>();
            queue.Enqueue(rootPid);
            var seen = new HashSet<int> { rootPid };

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!childrenByParent.TryGetValue(current, out var children))
                    continue;

                foreach (var child in children)
                {
                    if (!seen.Add(child))
                        continue;

                    yield return child;
                    queue.Enqueue(child);
                }
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private static IntPtr TryGetProcessMainWindowHandle(Process? process)
    {
        if (process is null)
            return IntPtr.Zero;

        try
        {
            process.Refresh();
            return process.MainWindowHandle;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static bool IsUsableTopLevelWindow(IntPtr handle) =>
        handle != IntPtr.Zero && IsWindowVisible(handle) && GetParent(handle) == IntPtr.Zero;

    private static bool IsMouseButtonMessage(int message) =>
        message is WmLButtonDown or WmRButtonDown or WmMButtonDown or WmXButtonDown;

    private static int LowWord(IntPtr value) => value.ToInt32() & 0xFFFF;

    private static void SetFocusAcrossThreads(IntPtr hwnd)
    {
        var currentThreadId = GetCurrentThreadId();
        var targetThreadId = GetWindowThreadProcessId(hwnd, out _);
        var foregroundWindow = GetForegroundWindow();
        var foregroundThreadId = foregroundWindow != IntPtr.Zero
            ? GetWindowThreadProcessId(foregroundWindow, out _)
            : 0;

        var attachedToTarget = false;
        var attachedToForeground = false;

        try
        {
            if (targetThreadId != 0 && targetThreadId != currentThreadId)
                attachedToTarget = AttachThreadInput(currentThreadId, targetThreadId, true);

            if (foregroundThreadId != 0 &&
                foregroundThreadId != currentThreadId &&
                foregroundThreadId != targetThreadId)
            {
                attachedToForeground = AttachThreadInput(currentThreadId, foregroundThreadId, true);
            }

            SetFocus(hwnd);
        }
        finally
        {
            if (attachedToForeground)
                AttachThreadInput(currentThreadId, foregroundThreadId, false);
            if (attachedToTarget)
                AttachThreadInput(currentThreadId, targetThreadId, false);
        }
    }

    private void OnHostSizeChanged(object sender, SizeChangedEventArgs e) =>
        Dispatcher.BeginInvoke(() => MoveMinecraftWindow(), DispatcherPriority.Loaded);

    private void OnGameSurfaceSizeChanged(object sender, SizeChangedEventArgs e) =>
        Dispatcher.BeginInvoke(() => MoveMinecraftWindow(), DispatcherPriority.Loaded);

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        if (_isMovingOrSizing)
            return;

        Dispatcher.BeginInvoke(() => MoveMinecraftWindow(), DispatcherPriority.Loaded);
    }

    private void OnBottomStripMouseDown(object sender, MouseButtonEventArgs e)
    {
        _bottomCollapsed = false;
        ApplyChromeState();
    }

    private void OnRightCollapseClick(object sender, RoutedEventArgs e)
    {
        _rightCollapsed = !_rightCollapsed;
        ApplyChromeState();
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        if (_isBorderlessFullscreen)
        {
            SetBorderlessFullscreen(false, rememberRestore: false);
            return;
        }

        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnStateChanged(object? sender, EventArgs e)
    {
        MaximizeButton.Content = WindowState == WindowState.Maximized
            ? char.ConvertFromUtf32(0xE923)
            : char.ConvertFromUtf32(0xE922);
        MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
        RootBorder.Padding = WindowState == WindowState.Maximized && !_isBorderlessFullscreen
            ? new Thickness(7)
            : new Thickness(0);

        // Restoring from an Alt+Tab minimize drops back into borderless fullscreen, so
        // re-assert the topmost z-order that MinimizeFromAltTab cleared on the way down.
        if (WindowState != WindowState.Minimized && _isBorderlessFullscreen)
            ApplyBorderlessZOrder();

        Dispatcher.BeginInvoke(() => MoveMinecraftWindow(), DispatcherPriority.Loaded);
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    private delegate IntPtr LowLevelKeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32First(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ClipCursor(ref NativeRect lpRect);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "ClipCursor")]
    private static extern bool ClipCursor(IntPtr lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClipCursor(out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref NativeMonitorInfo lpmi);

    private const uint MonitorDefaultToNearest = 0x00000002;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorInfo(ref CursorInfo pci);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool repaint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelKeyboardCallback lpfn,
        IntPtr hMod,
        uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(IntPtr hWnd, int nIndex, nint dwNewLong);

    private static nint GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new nint(GetWindowLong32(hWnd, nIndex));

    private static nint SetWindowLongPtr(IntPtr hWnd, int nIndex, nint dwNewLong) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : new nint(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nuint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint
    {
        public readonly int X;
        public readonly int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int CbSize;
        public int Flags;
        public IntPtr HCursor;
        public NativePoint PtScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHookStruct
    {
        public int VkCode;
        public int ScanCode;
        public int Flags;
        public int Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeWindowPos
    {
        public IntPtr Hwnd;
        public IntPtr HwndInsertAfter;
        public int X;
        public int Y;
        public int Cx;
        public int Cy;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo
    {
        public int CbSize;
        public NativeRect RcMonitor;
        public NativeRect RcWork;
        public uint DwFlags;
    }
}
