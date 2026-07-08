using System.Runtime.InteropServices;

namespace CloudLauncher.Services;

internal static class WindowWorkArea
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;

    public static bool TryHandleGetMinMaxInfo(IntPtr hwnd, int msg, IntPtr lParam, bool useFullMonitor = false)
    {
        if (msg != WmGetMinMaxInfo)
            return false;

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
            return false;

        var monitorInfo = new MonitorInfo
        {
            cbSize = Marshal.SizeOf<MonitorInfo>()
        };

        if (!GetMonitorInfo(monitor, ref monitorInfo))
            return false;

        var minMax = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var workArea = useFullMonitor ? monitorInfo.rcMonitor : monitorInfo.rcWork;
        var monitorArea = monitorInfo.rcMonitor;

        minMax.ptMaxPosition.X = workArea.Left - monitorArea.Left;
        minMax.ptMaxPosition.Y = workArea.Top - monitorArea.Top;
        minMax.ptMaxSize.X = workArea.Right - workArea.Left;
        minMax.ptMaxSize.Y = workArea.Bottom - workArea.Top;
        minMax.ptMaxTrackSize.X = minMax.ptMaxSize.X;
        minMax.ptMaxTrackSize.Y = minMax.ptMaxSize.Y;

        Marshal.StructureToPtr(minMax, lParam, false);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point ptReserved;
        public Point ptMaxSize;
        public Point ptMaxPosition;
        public Point ptMinTrackSize;
        public Point ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);
}
