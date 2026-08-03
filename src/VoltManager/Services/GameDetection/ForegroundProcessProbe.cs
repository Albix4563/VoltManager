using System.Runtime.InteropServices;

namespace VoltManager.Services.GameDetection;

/// <summary>
/// Cheap presentation-window → process id probe used as a runtime game-start signal.
/// Covers normal foreground ownership and exclusive/borderless fullscreen top-level
/// windows (where GetForegroundWindow may return null or a shell helper).
/// No injection; user32 only.
/// </summary>
public static class ForegroundProcessProbe
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint MonitorDefaultToNearest = 2;
    private const int DefaultFullscreenTolerancePx = 16;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int CbSize;
        public Rect RcMonitor;
        public Rect RcWork;
        public uint DwFlags;
    }

    /// <summary>
    /// Returns the PID that currently owns the foreground window, or null when none.
    /// </summary>
    public static int? TryGetForegroundProcessId()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return null;

        _ = GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == 0 ? null : unchecked((int)pid);
    }

    /// <summary>
    /// PIDs that own either the foreground window or a visible near-fullscreen top-level
    /// window on their nearest monitor. Used as sticky game-start presentation signal.
    /// </summary>
    public static IReadOnlySet<int> TryGetPresentationProcessIds()
    {
        var pids = new HashSet<int>();
        int? foreground = TryGetForegroundProcessId();
        if (foreground != null)
            pids.Add(foreground.Value);

        try
        {
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd) || IsIconic(hWnd))
                    return true;

                int exStyle = GetWindowLong(hWnd, GwlExStyle);
                if ((exStyle & WsExToolWindow) != 0 || (exStyle & WsExNoActivate) != 0)
                    return true;

                if (!GetWindowRect(hWnd, out var windowRect))
                    return true;

                IntPtr monitor = MonitorFromWindow(hWnd, MonitorDefaultToNearest);
                if (monitor == IntPtr.Zero)
                    return true;

                var info = new MonitorInfo { CbSize = Marshal.SizeOf<MonitorInfo>() };
                if (!GetMonitorInfo(monitor, ref info))
                    return true;

                if (!IsNearFullscreenRect(
                        windowRect.Left, windowRect.Top, windowRect.Right, windowRect.Bottom,
                        info.RcMonitor.Left, info.RcMonitor.Top, info.RcMonitor.Right, info.RcMonitor.Bottom,
                        DefaultFullscreenTolerancePx))
                    return true;

                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid != 0)
                    pids.Add(unchecked((int)pid));
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // Presentation probe is best-effort; foreground PID alone still works.
        }

        return pids;
    }

    /// <summary>
    /// True when the window rect covers the monitor rect within <paramref name="tolerancePx"/>.
    /// Pure geometry helper for exclusive/borderless fullscreen detection.
    /// </summary>
    public static bool IsNearFullscreenRect(
        int windowLeft, int windowTop, int windowRight, int windowBottom,
        int monitorLeft, int monitorTop, int monitorRight, int monitorBottom,
        int tolerancePx = DefaultFullscreenTolerancePx)
    {
        int monitorWidth = monitorRight - monitorLeft;
        int monitorHeight = monitorBottom - monitorTop;
        if (monitorWidth <= 0 || monitorHeight <= 0)
            return false;

        int windowWidth = windowRight - windowLeft;
        int windowHeight = windowBottom - windowTop;
        if (windowWidth <= 0 || windowHeight <= 0)
            return false;

        // Cover almost the full monitor; allow a few px of overscan/undock chrome.
        if (windowWidth < monitorWidth - 2 * tolerancePx)
            return false;
        if (windowHeight < monitorHeight - 2 * tolerancePx)
            return false;

        return windowLeft <= monitorLeft + tolerancePx &&
               windowTop <= monitorTop + tolerancePx &&
               windowRight >= monitorRight - tolerancePx &&
               windowBottom >= monitorBottom - tolerancePx;
    }
}
