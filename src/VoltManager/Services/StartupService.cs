using System.Diagnostics;

namespace VoltManager.Services;

/// <summary>
/// Autostart for an elevated app: HKCU Run is blocked by UAC for requireAdministrator
/// binaries, so use a scheduled task with /rl HIGHEST at logon.
/// </summary>
public class StartupService
{
    private const string TaskName = "VoltManagerAutostart";

    public bool SetStartWithWindows(bool enable)
    {
        if (enable)
        {
            string exe = Environment.ProcessPath ?? "";
            if (string.IsNullOrEmpty(exe)) return false;
            return RunSchtasks($"/create /f /tn \"{TaskName}\" /tr \"\\\"{exe}\\\" --minimized\" /sc onlogon /rl HIGHEST") == 0;
        }
        RunSchtasks($"/delete /f /tn \"{TaskName}\"");
        return true;
    }

    public bool IsEnabled()
    {
        return RunSchtasks($"/query /tn \"{TaskName}\"") == 0;
    }

    private static int RunSchtasks(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(10000);
            return p.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}
