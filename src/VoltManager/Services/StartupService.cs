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

    private readonly Func<string, int> _runSchtasks;
    private readonly Func<string?> _processPathProvider;

    public StartupService()
        : this(RunSchtasks, static () => Environment.ProcessPath)
    {
    }

    internal StartupService(Func<string, int> runSchtasks, Func<string?>? processPathProvider = null)
    {
        _runSchtasks = runSchtasks ?? throw new ArgumentNullException(nameof(runSchtasks));
        _processPathProvider = processPathProvider ?? (static () => Environment.ProcessPath);
    }

    public bool SetStartWithWindows(bool enable)
    {
        if (enable)
        {
            string exe = _processPathProvider() ?? "";
            if (string.IsNullOrEmpty(exe)) return false;

            DirectoryInfo tempDirectory = Directory.CreateTempSubdirectory();
            string xmlPath = Path.Combine(tempDirectory.FullName, "task.xml");
            bool created = false;
            try
            {
                // schtasks richiede UTF-16; CreateNew impedisce di sovrascrivere un file esistente.
                using (var stream = File.Open(xmlPath, FileMode.CreateNew, FileAccess.Write))
                {
                    created = true;
                    using var writer = new StreamWriter(stream, System.Text.Encoding.Unicode);
                    writer.Write(BuildTaskXml(exe));
                }
                return _runSchtasks($"/create /f /tn \"{TaskName}\" /xml \"{xmlPath}\"") == 0;
            }
            finally
            {
                try
                {
                    if (created) File.Delete(xmlPath);
                    tempDirectory.Delete();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Logger.Warn($"Pulizia del task XML non riuscita: {ex.Message}");
                }
            }
        }

        int deleteExit = _runSchtasks($"/delete /f /tn \"{TaskName}\"");
        if (deleteExit == 0)
            return true;
        if (deleteExit < 0)
            return false;

        int queryExit = _runSchtasks($"/query /tn \"{TaskName}\"");
        return queryExit > 0;
    }

    public bool IsEnabled()
    {
        return _runSchtasks($"/query /tn \"{TaskName}\"") == 0;
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
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"),
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            ExternalProcessResult result = ExternalProcessRunner.Run(psi, TimeSpan.FromSeconds(10));
            if (!result.Started || result.TimedOut || result.Error != null)
            {
                if (result.TimedOut)
                    Logger.Warn("schtasks timed out after 10s and was terminated.");
                else if (result.Error != null)
                    Logger.Warn("schtasks failed: " + result.Error.Message);
                return -1;
            }
            return result.ExitCode ?? -1;
        }
        catch
        {
            return -1;
        }
    }
}
