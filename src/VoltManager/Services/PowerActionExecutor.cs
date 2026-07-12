using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using VoltManager.Models;

namespace VoltManager.Services;

public sealed class PowerActionExecutor : IPowerActionExecutor
{
    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    public void Execute(ScheduledPowerActionType action)
    {
        switch (action)
        {
            case ScheduledPowerActionType.Sleep:
                ExecuteSleep();
                break;
            case ScheduledPowerActionType.Restart:
                StartShutdownProcess("/r /t 0");
                break;
            default:
                StartShutdownProcess("/s /t 0");
                break;
        }
    }

    private static void ExecuteSleep()
    {
        bool success = SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvent: false);
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
