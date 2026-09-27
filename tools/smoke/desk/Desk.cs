using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace Desk;

/// <summary>Launches the real published launcher on a separate, hidden Windows desktop and drives
/// it with UI Automation from a probe process on that desktop. Nothing appears on the visible
/// desktop, and no input goes through the system input stream (clicks are messages posted to the
/// launcher's window).
///
///   desk run   &lt;launcher.exe&gt; &lt;outDir&gt; [observe]     (from the visible desktop)
///   desk probe &lt;pid&gt; &lt;outDir&gt; [observe]              (started by run, on the hidden desktop)
///
/// "observe" presses nothing: it waits, dumps every window the launcher has open (an update prompt,
/// a message box), then ends the launcher. Use it for a window that must not be answered.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args) => args[0] switch
    {
        "run" => Run(args[1], args[2], observe: args.Length > 3 && args[3] == "observe"),
        "probe" => Probe(int.Parse(args[1]), args[2], observe: args.Length > 3 && args[3] == "observe"),
        _ => 2
    };

    // ── run ──────────────────────────────────────────────────────────────────

    private static int Run(string exe, string outDir, bool observe)
    {
        Directory.CreateDirectory(outDir);
        using var log = new StreamWriter(Path.Combine(outDir, "run.txt")) { AutoFlush = true };
        var name = "clsmoke-" + Environment.ProcessId;
        var desk = CreateDesktop(name, IntPtr.Zero, IntPtr.Zero, 0, 0x000F01FF, IntPtr.Zero);
        if (desk == IntPtr.Zero) { log.WriteLine("CreateDesktop failed " + Marshal.GetLastWin32Error()); return 3; }
        log.WriteLine("desktop " + name);

        // Inherited by both children: a throwaway profile, a dead CurseForge base, and a WebView2 folder
        // that does not exist so no page can start WebView2 in the real %LOCALAPPDATA% folder.
        Environment.SetEnvironmentVariable("CL_PROFILE", "showcase");
        Environment.SetEnvironmentVariable("CL_CURSEFORGE_BASE", "http://127.0.0.1:9");
        Environment.SetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", Path.Combine(outDir, "no-webview2-here"));

        var sw = Stopwatch.StartNew();
        var launcher = Start(exe, $"\"{exe}\"", name, Path.GetDirectoryName(exe)!, belowNormal: true);
        if (launcher.hProcess == IntPtr.Zero) { log.WriteLine("launch failed " + Marshal.GetLastWin32Error()); CloseDesktop(desk); return 4; }
        log.WriteLine($"launcher pid {launcher.dwProcessId}");

        var self = Environment.ProcessPath!;
        var probe = Start(self, $"\"{self}\" probe {launcher.dwProcessId} \"{outDir}\"{(observe ? " observe" : "")}",
            name, Path.GetDirectoryName(self)!, belowNormal: false);
        if (probe.hProcess == IntPtr.Zero) { log.WriteLine("probe start failed " + Marshal.GetLastWin32Error()); }
        else
        {
            var r = WaitForSingleObject(probe.hProcess, 300_000);
            GetExitCodeProcess(probe.hProcess, out var pcode);
            log.WriteLine($"probe finished wait={r} exit={pcode} after {sw.Elapsed.TotalSeconds:0.0}s");
        }

        // The probe ends by asking the launcher to close; give it time to save and go. An observing
        // probe asked nothing, so the launcher is ended straight away.
        var lw = WaitForSingleObject(launcher.hProcess, observe ? 0u : 20_000u);
        GetExitCodeProcess(launcher.hProcess, out var lcode);
        if (lw != 0)
        {
            log.WriteLine("launcher still running after WM_CLOSE + 20s; terminating");
            TerminateProcess(launcher.hProcess, 1);
            WaitForSingleObject(launcher.hProcess, 10_000);
        }
        else log.WriteLine($"launcher exited by itself, code {lcode} (0x{lcode:X8})");

        CloseHandle(launcher.hProcess); CloseHandle(launcher.hThread);
        if (probe.hProcess != IntPtr.Zero) { CloseHandle(probe.hProcess); CloseHandle(probe.hThread); }
        var closed = CloseDesktop(desk);
        log.WriteLine("desktop closed: " + closed);
        return 0;
    }

    private static PROCESS_INFORMATION Start(string exe, string cmd, string desktop, string cwd, bool belowNormal)
    {
        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"WinSta0\" + desktop };
        const uint CREATE_UNICODE_ENVIRONMENT = 0x400, BELOW_NORMAL = 0x4000, CREATE_NO_WINDOW = 0x08000000;
        CreateProcess(exe, new StringBuilder(cmd), IntPtr.Zero, IntPtr.Zero, false,
            CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW | (belowNormal ? BELOW_NORMAL : 0), IntPtr.Zero, cwd, ref si, out var pi);
        return pi;
    }

    // ── probe ────────────────────────────────────────────────────────────────

    private static StreamWriter _log = null!;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static void Log(string s) => _log.WriteLine($"{Clock.Elapsed.TotalSeconds,7:0.0}s {s}");

    private static int Probe(int pid, string outDir, bool observe)
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        _log = new StreamWriter(Path.Combine(outDir, "probe.txt")) { AutoFlush = true };
        try
        {
            var win = WaitForMainWindow(pid, TimeSpan.FromSeconds(90));
            if (win is null) { Log("NO MAIN WINDOW within 90 s"); Snapshot(pid, outDir, "00-nowindow"); return 5; }
            var hwnd = new IntPtr(win.Current.NativeWindowHandle);
            Log($"main window '{win.Current.Name}' hwnd=0x{hwnd:X} rect={win.Current.BoundingRectangle}");

            if (observe)
            {
                Thread.Sleep(15000);
                Step(pid, win, hwnd, outDir, "01-observed");
                return 0;
            }

            Thread.Sleep(9000);
            Log($"  page now: {PageSignature(win)}");
            Step(pid, win, hwnd, outDir, "01-start");

            foreach (var (nav, label) in new[]
                     {
                         ("NavSharing", "02-sharing"), ("NavPacks", "03-instances"), ("NavMods", "04-mods"),
                         ("NavWorlds", "05-worlds"), ("NavResourcePacks", "06-resourcepacks"), ("NavShaders", "07-shaders"),
                         ("NavServers", "08-servers"), ("NavConfigs", "09-configs"), ("NavStorage", "10-storage"),
                         ("NavSettings", "11-settings"), ("NavSharing", "12-sharing-again"),
                         // Signed in, the account panel; signed out, the sign-in page, and then back out of it.
                         ("NavAccount", "13-account"), ("NavPacks", "14-instances-again"),
                     })
            {
                if (!Alive(pid)) { Log("LAUNCHER EXITED before " + label); return 6; }
                var ok = PressByAutomationId(win, hwnd, nav);
                Log($"press {nav}: {ok}");
                Thread.Sleep(6000);
                Log($"  page now: {PageSignature(win)}");
                Step(pid, win, hwnd, outDir, label);
            }

            if (!Alive(pid)) { Log("LAUNCHER EXITED at the end"); return 6; }
            Log("asking the launcher to close");
            PostMessage(hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
            return 0;
        }
        catch (Exception ex)
        {
            Log("PROBE FAILED " + ex);
            return 7;
        }
    }

    private static bool Alive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; } catch { return false; }
    }

    private static AutomationElement? WaitForMainWindow(int pid, TimeSpan limit)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < limit)
        {
            if (!Alive(pid)) { Log("launcher exited while waiting for its window"); return null; }
            var wins = TopWindows(pid);
            var main = wins.Where(w => w.Current.ClassName != "#32770")
                           .OrderByDescending(w => w.Current.BoundingRectangle.Width * w.Current.BoundingRectangle.Height)
                           .FirstOrDefault();
            if (main is not null && main.Current.BoundingRectangle.Width > 300)
            {
                Log($"window after {sw.Elapsed.TotalSeconds:0.0}s");
                return main;
            }
            Thread.Sleep(250);
        }
        return null;
    }

    private static List<AutomationElement> TopWindows(int pid) =>
        AutomationElement.RootElement.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ProcessIdProperty, pid)).Cast<AutomationElement>().ToList();

    private static void Step(int pid, AutomationElement win, IntPtr hwnd, string outDir, string label)
    {
        // Anything else the launcher has on top (a message box from the unhandled-exception backstop).
        foreach (var w in TopWindows(pid))
        {
            if (Automation.Compare(w, win)) continue;
            Log($"EXTRA WINDOW class={w.Current.ClassName} name='{w.Current.Name}'");
            File.AppendAllText(Path.Combine(outDir, label + "-extra-windows.txt"),
                $"== class={w.Current.ClassName} name='{w.Current.Name}'\n{Dump(w)}\n");
        }
        var text = Dump(win);
        File.WriteAllText(Path.Combine(outDir, label + ".txt"), text);
        Log($"{label}: {text.Split('\n').Length} lines");
        Shoot(hwnd, Path.Combine(outDir, label + ".png"));
    }

    private static void Snapshot(int pid, string outDir, string label)
    {
        foreach (var w in TopWindows(pid))
            File.AppendAllText(Path.Combine(outDir, label + ".txt"), $"== {w.Current.ClassName} '{w.Current.Name}'\n{Dump(w)}\n");
    }

    private static string Dump(AutomationElement root)
    {
        var sb = new StringBuilder();
        var walker = TreeWalker.RawViewWalker;
        void Walk(AutomationElement e, int depth)
        {
            if (depth > 80) return;
            try
            {
                var c = e.Current;
                var type = c.ControlType.ProgrammaticName.Replace("ControlType.", "");
                if (!string.IsNullOrWhiteSpace(c.Name) || !string.IsNullOrEmpty(c.AutomationId))
                {
                    sb.Append(' ', depth).Append(type);
                    if (!string.IsNullOrEmpty(c.AutomationId)) sb.Append(" #").Append(c.AutomationId);
                    if (!string.IsNullOrWhiteSpace(c.Name)) sb.Append("  ").Append(c.Name.Replace("\r", "").Replace("\n", " / "));
                    if (c.IsOffscreen) sb.Append("  (offscreen)");
                    if (!c.IsEnabled) sb.Append("  (disabled)");
                    sb.Append('\n');
                }
            }
            catch (ElementNotAvailableException) { return; }
            var child = walker.GetFirstChild(e);
            while (child is not null)
            {
                Walk(child, depth + 1);
                try { child = walker.GetNextSibling(child); } catch (ElementNotAvailableException) { break; }
            }
        }
        Walk(root, 0);
        return sb.ToString();
    }

    /// <summary>Focuses the element through UI Automation and presses Space with messages posted to
    /// the launcher's window. Posted mouse clicks do not work on WPF buttons: mouse capture makes
    /// WPF read the real cursor (GetCursorPos) on the visible desktop, so the button never clicks.
    /// Never SendInput.</summary>
    private static bool PressByAutomationId(AutomationElement win, IntPtr hwnd, string id)
    {
        var el = win.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
        if (el is null) { Log("  no element #" + id); return false; }
        try { el.SetFocus(); }
        catch (Exception ex) { Log("  SetFocus failed: " + ex.GetType().Name + " " + ex.Message); }
        Thread.Sleep(250);
        Log($"  #{id} focused={el.Current.HasKeyboardFocus}");
        const int VK_SPACE = 0x20, SCAN = 0x39;
        PostMessage(hwnd, 0x0100 /* WM_KEYDOWN */, (IntPtr)VK_SPACE, (IntPtr)(1 | (SCAN << 16)));
        Thread.Sleep(120);
        PostMessage(hwnd, 0x0101 /* WM_KEYUP */, (IntPtr)VK_SPACE, (IntPtr)unchecked((int)(1 | (SCAN << 16) | (1 << 30) | (1u << 31))));
        Thread.Sleep(120);
        return true;
    }

    /// <summary>The first few texts on the page in the main frame: enough to tell which page is up.</summary>
    private static string PageSignature(AutomationElement win)
    {
        var frame = win.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "MainFrame"));
        if (frame is null) return "(no MainFrame)";
        var texts = new List<string>();
        var walker = TreeWalker.RawViewWalker;
        void Walk(AutomationElement e, int depth)
        {
            if (texts.Count >= 3 || depth > 40) return;
            try { var n = e.Current.Name; if (e.Current.ControlType == ControlType.Text && !string.IsNullOrWhiteSpace(n)) texts.Add(n); }
            catch { return; }
            for (var c = walker.GetFirstChild(e); c is not null && texts.Count < 3; c = walker.GetNextSibling(c)) Walk(c, depth + 1);
        }
        Walk(frame, 0);
        return string.Join(" | ", texts);
    }

    /// <summary>A mouse click made of messages posted to the launcher's window, with the button state
    /// set in the (attached) input queue so WPF sees a pressed button. Never SendInput: that goes to
    /// the visible desktop.</summary>
    private static bool ClickByAutomationId(AutomationElement win, IntPtr hwnd, string id)
    {
        var el = win.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
        if (el is null) { Log("  no element #" + id); return false; }
        var r = el.Current.BoundingRectangle;
        if (r.IsEmpty || r.Width < 1) { Log("  #" + id + " has no bounds"); return false; }
        var pt = new POINT { X = (int)(r.X + r.Width / 2), Y = (int)(r.Y + r.Height / 2) };
        ScreenToClient(hwnd, ref pt);
        var lp = (IntPtr)(((pt.Y & 0xFFFF) << 16) | (pt.X & 0xFFFF));

        var target = GetWindowThreadProcessId(hwnd, out _);
        var me = GetCurrentThreadId();
        var attached = AttachThreadInput(me, target, true);
        try
        {
            var keys = new byte[256];
            PostMessage(hwnd, 0x0200 /* WM_MOUSEMOVE */, IntPtr.Zero, lp);
            Thread.Sleep(120);
            GetKeyboardState(keys); keys[0x01] = 0x80; SetKeyboardState(keys);
            PostMessage(hwnd, 0x0201 /* WM_LBUTTONDOWN */, (IntPtr)0x0001, lp);
            Thread.Sleep(150);
            GetKeyboardState(keys); keys[0x01] = 0x00; SetKeyboardState(keys);
            PostMessage(hwnd, 0x0202 /* WM_LBUTTONUP */, IntPtr.Zero, lp);
            Thread.Sleep(120);
        }
        finally
        {
            if (attached) AttachThreadInput(me, target, false);
        }
        return true;
    }

    private static void Shoot(IntPtr hwnd, string path)
    {
        try
        {
            GetWindowRect(hwnd, out var rc);
            int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
            if (w <= 0 || h <= 0) return;
            using var bmp = new System.Drawing.Bitmap(w, h);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                var hdc = g.GetHdc();
                PrintWindow(hwnd, hdc, 2 /* PW_RENDERFULLCONTENT */);
                g.ReleaseHdc(hdc);
            }
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception ex) { Log("  shot failed: " + ex.Message); }
    }

    // ── interop ──────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr devmode, int flags, uint access, IntPtr sa);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseDesktop(IntPtr desk);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags,
        IntPtr env, string cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr h, uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] keys);
    [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] keys);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hwnd, ref POINT p);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
}
