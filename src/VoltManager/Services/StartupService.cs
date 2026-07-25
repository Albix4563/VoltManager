using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;

namespace VoltManager.Services;

/// <summary>
/// Autostart for an elevated app: HKCU Run is blocked by UAC for requireAdministrator
/// binaries, so use a scheduled task at logon. The task is registered via XML because
/// the plain schtasks CLI applies defaults that break autostart on laptops
/// (don't start on battery, stop on battery, 72h execution limit).
/// </summary>
public class StartupService
{
    private const string TaskName = "VoltManagerAutostart";

    /// <summary>
    /// Bump when BuildTaskXml settings that must re-register on existing installs change.
    /// Schema 1: Priority 5 (NORMAL) instead of 7 (BELOW_NORMAL).
    /// </summary>
    public const int CurrentTaskSchemaVersion = 1;

    public bool SetStartWithWindows(bool enable)
    {
        if (enable)
        {
            string exe = Environment.ProcessPath ?? "";
            if (string.IsNullOrEmpty(exe)) return false;

            string xmlPath = Path.GetTempFileName();
            try
            {
                // schtasks expects task XML files in UTF-16.
                File.WriteAllText(xmlPath, BuildTaskXml(exe), System.Text.Encoding.Unicode);
                return RunSchtasks($"/create /f /tn \"{TaskName}\" /xml \"{xmlPath}\"") == 0;
            }
            finally
            {
                try { File.Delete(xmlPath); } catch { }
            }
        }
        RunSchtasks($"/delete /f /tn \"{TaskName}\"");
        return true;
    }

    public bool IsEnabled()
    {
        return RunSchtasks($"/query /tn \"{TaskName}\"") == 0;
    }

    /// <summary>Task Scheduler XML for autostart (public for self-check / diagnostics).</summary>
    public static string BuildTaskXml(string exePath)
    {
        string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        string exe = SecurityElement.Escape(exePath);

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{exe}</Command>
                  <Arguments>--minimized</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
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
