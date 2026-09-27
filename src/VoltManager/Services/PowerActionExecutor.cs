using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using VoltManager.Models;

namespace VoltManager.Services;

public sealed class PowerActionExecutor : IPowerActionExecutor
{
    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool forceCritical,
        [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);

    public void Execute(ScheduledPowerActionType action)
    {
        switch (action)
        {
            case ScheduledPowerActionType.Shutdown:
                StartShutdownProcess("/s /t 0");
                break;
            case ScheduledPowerActionType.Sleep:
                ExecuteSuspend(hibernate: false);
                break;
            case ScheduledPowerActionType.Hibernate:
                ExecuteSuspend(hibernate: true);
                break;
            case ScheduledPowerActionType.Restart:
                StartShutdownProcess("/r /t 0");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported power action.");
        }
    }

    private static void ExecuteSuspend(bool hibernate)
    {
        bool success = SetSuspendState(hibernate, forceCritical: false, disableWakeEvent: false);
        if (!success)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static void StartShutdownProcess(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "shutdown.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        if (process == null)
            throw new InvalidOperationException("Could not start shutdown.exe.");
    }
}
