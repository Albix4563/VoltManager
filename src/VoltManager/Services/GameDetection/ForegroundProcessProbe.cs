using System.Runtime.InteropServices;

namespace VoltManager.Services.GameDetection;

/// <summary>
/// Cheap presentation-window → process id probe used as a runtime game-start signal.
/// Covers normal foreground ownership and exclusive/borderless fullscreen top-level
/// windows (where GetForegroundWindow may return null or a shell helper).
/// No injection; user32 only.
/// </summary>
public static partial class ForegroundProcessProbe
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint MonitorDefaultToNearest = 2;
    private const int DefaultFullscreenTolerancePx = 16;

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(IntPtr lpEnumFunc, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static partial int GetWindowLong(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out int state);

    private const int QunsRunningD3DFullScreen = 3;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

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
            EnumWindowsProc proc = (hWnd, _) =>
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

                if (GetWindowThreadProcessId(hWnd, out uint pid) != 0 && pid != 0)
                    pids.Add(unchecked((int)pid));
                return true;
            };
            try
            {
                EnumWindows(Marshal.GetFunctionPointerForDelegate(proc), IntPtr.Zero);
            }
            finally
            {
                GC.KeepAlive(proc);
            }
        }
        catch
        {
            // Presentation probe is best-effort; foreground PID alone still works.
        }

        return pids;
    }

    /// <summary>
    /// True when the shell reports an exclusive/borderless D3D fullscreen session anywhere.
    /// The flag is system-wide: it never says which process owns it.
    /// </summary>
    public static bool IsD3dFullscreenActive()
    {
        try
        {
            // S_OK only; on failure the out value is undefined.
            return SHQueryUserNotificationState(out int state) == 0 && state == QunsRunningD3DFullScreen;
        }
        catch
        {
            // ponytail: shell32 missing this export only on non-desktop SKUs — no signal, no crash.
            return false;
        }
    }

    /// <summary>
    /// Conservative attribution of the system-wide D3D fullscreen flag to a single process:
    /// the PID must own the foreground window *and* a near-fullscreen window.
    /// </summary>
    public static bool ShouldAttributeD3dFullscreen(
        bool d3dFullscreenActive, int pid, int? foregroundPid, IReadOnlySet<int> presentationPids)
        => d3dFullscreenActive
           && foregroundPid == pid
           && presentationPids.Contains(pid);

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
