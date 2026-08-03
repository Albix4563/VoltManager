using System.Runtime.InteropServices;

namespace VoltManager.Services.GameDetection;

/// <summary>
/// Cheap foreground-window → process id probe used as a runtime game-start signal.
/// No injection; only user32 GetForegroundWindow + GetWindowThreadProcessId.
/// </summary>
public static class ForegroundProcessProbe
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

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
}
