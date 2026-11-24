using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using VoltManager.Models;

namespace VoltManager.Services;

public sealed partial class PowerActionExecutor : IPowerActionExecutor
{
    private readonly Func<ProcessStartInfo, Process?> _startProcess;

    public PowerActionExecutor() : this(Process.Start)
    {
    }

    internal PowerActionExecutor(Func<ProcessStartInfo, Process?> startProcess)
        => _startProcess = startProcess ?? throw new ArgumentNullException(nameof(startProcess));

    [LibraryImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool SetSuspendState(
        [MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool forceCritical,
        [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);

    public void Execute(ScheduledPowerActionType action)
        => Execute(action, shutdownDelaySeconds: 0);

    public void Execute(ScheduledPowerActionType action, int shutdownDelaySeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(shutdownDelaySeconds);

        switch (action)
        {
            case ScheduledPowerActionType.Shutdown:
                StartShutdownProcess($"/s /t {shutdownDelaySeconds}");
                break;
            case ScheduledPowerActionType.Sleep:
                ExecuteSuspend(hibernate: false);
                break;
            case ScheduledPowerActionType.Hibernate:
                ExecuteSuspend(hibernate: true);
                break;
            case ScheduledPowerActionType.Restart:
                StartShutdownProcess($"/r /t {shutdownDelaySeconds}");
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

    private void StartShutdownProcess(string arguments)
    {
        // Percorso assoluto in System32 per evitare risoluzioni tramite PATH (S4036).
        string shutdownPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "shutdown.exe");
        using var process = _startProcess(new ProcessStartInfo
        {
            FileName = shutdownPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        if (process == null)
            throw new InvalidOperationException("Could not start shutdown.exe.");
    }
}
