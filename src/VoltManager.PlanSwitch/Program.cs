using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using VoltManager.Services;

namespace VoltManager.PlanSwitch;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string? key = RemoteCommandProtocol.ParseCommandArg(args);
        if (key == null) return 1;

        // App running: signal its command event and exit, no UAC involved.
        try
        {
            using (var evt = EventWaitHandle.OpenExisting(RemoteCommandProtocol.EventName(key)))
            {
                evt.Set();
                return 0;
            }
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // App not running, fall through and start it.
        }
        catch
        {
            return 1;
        }

        // Pinned jump list used while the app is closed: start VoltManager
        // (elevated, so Windows shows UAC once) with the command argument.
        string exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VoltManager.exe");
        if (!File.Exists(exe)) return 1;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = RemoteCommandProtocol.CommandArgName + " " + key,
                UseShellExecute = true,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
            });
        }
        catch
        {
            // User declined the UAC prompt: nothing else to do.
        }
        return 0;
    }
}
