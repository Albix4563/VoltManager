using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using VoltManager.Services;

namespace VoltManager.Setup.Engine
{
    /// <summary>
    /// Adds the verified uninstall lifecycle to the shared install/update engine.
    /// </summary>
    public sealed class HardenedInstallEngine : InstallEngine
    {
        private static readonly TimeSpan GracefulShutdownTimeout = TimeSpan.FromSeconds(6);
        private static readonly TimeSpan HardwareParentExitTimeout = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan ForcedShutdownVerificationTimeout = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan ProcessPollInterval = TimeSpan.FromMilliseconds(150);

        private readonly IUninstallSystemOperations _systemOperations;
        private readonly Func<string> _currentExecutable;

        public HardenedInstallEngine()
            : this(new SystemUninstallSystemOperations(), () => Assembly.GetExecutingAssembly().Location)
        {
        }

        internal HardenedInstallEngine(IUninstallSystemOperations systemOperations, Func<string> currentExecutable)
        {
            _systemOperations = systemOperations ?? throw new ArgumentNullException(nameof(systemOperations));
            _currentExecutable = currentExecutable ?? throw new ArgumentNullException(nameof(currentExecutable));
        }

        public async Task<UninstallResult> UninstallAsync(string? targetDir = null, CancellationToken ct = default)
        {
            var result = new UninstallResult();
            string installDir = ResolveInstallDir(targetDir);
            string currentExecutable = _currentExecutable();

            Report(I18n.T("status_uninst_kill"), 5);
            if (!await _systemOperations.StopRunningInstalledProcessesAsync(installDir, ct).ConfigureAwait(false))
                result.Add("VoltManager process still running after graceful and forced shutdown");

            ct.ThrowIfCancellationRequested();
            _systemOperations.RemoveLanRemoteControlArtifacts(result);

            ct.ThrowIfCancellationRequested();
            Report(I18n.T("status_uninst_files"), 20);
            if (!string.IsNullOrEmpty(installDir) && _systemOperations.DirectoryExists(installDir))
            {
                if (IsPathUnder(currentExecutable, installDir))
                {
                    result.Add("Uninstaller is still running from the install directory");
                }
                else if (!_systemOperations.TryDeleteDirectoryTree(installDir, out string installError))
                {
                    result.Add("Install directory: " + installError);
                }
            }

            ct.ThrowIfCancellationRequested();
            Report(I18n.T("status_uninst_files"), 42);
            string appData = _systemOperations.AppDataDirectory;
            if (_systemOperations.DirectoryExists(appData) && !_systemOperations.TryDeleteDirectoryTree(appData, out string appDataError))
                result.Add("AppData: " + appDataError);

            ct.ThrowIfCancellationRequested();
            Report(I18n.T("status_startup"), 58);
            _systemOperations.DeleteStartupTask(result);

            ct.ThrowIfCancellationRequested();
            Report(I18n.T("status_uninst_files"), 70);
            _systemOperations.RemoveShortcuts(result);

            ct.ThrowIfCancellationRequested();
            Report(I18n.T("status_uninst_reg"), 82);
            _systemOperations.RemoveRegistryEntries(result);

            ct.ThrowIfCancellationRequested();
            Report(I18n.T("status_uninst_files"), 92);
            foreach (string failure in _systemOperations.CleanupOwnedTempArtifacts(currentExecutable))
                result.Add("Temp artifact: " + failure);

            foreach (string residual in _systemOperations.FindResidualArtifacts(installDir, appData, currentExecutable))
                result.AddResidual(residual);
            Report("", 100);
            return result;
        }

        /// <summary>
        /// Schedules deletion of the temp uninstaller after the setup process exits.
        /// This is intentionally invoked from App.OnExit, not when uninstall work
        /// finishes, so a user can keep the completion page open indefinitely.
        /// </summary>
        public static void ScheduleTemporaryUninstallerSelfDeleteIfNeeded()
        {
            try
            {
                string self = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
                string tempRoot = Path.GetFullPath(Path.GetTempPath())
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!self.StartsWith(tempRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return;
                if (!string.Equals(Path.GetFileName(self), "VoltManagerUninstall.exe", StringComparison.OrdinalIgnoreCase))
                    return;

                string cleanup = Path.Combine(Path.GetTempPath(), "vmgr_uninstall_cleanup.bat");
                try { if (File.Exists(cleanup)) File.Delete(cleanup); } catch { }

                File.WriteAllText(cleanup,
                    "@echo off\r\n" +
                    "for /l %%i in (1,1,30) do (\r\n" +
                    "  del /f /q \"" + self + "\" 2>nul && goto done\r\n" +
                    "  timeout /t 1 /nobreak >nul\r\n" +
                    ")\r\n" +
                    ":done\r\n" +
                    "del /f /q \"%~f0\" 2>nul\r\n");

                Process.Start(new ProcessStartInfo("cmd.exe", "/d /c \"\"" + cleanup + "\"\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
            }
            catch
            {
                // Exit-time cleanup is best effort; synchronous verification has
                // already removed every other owned artifact.
            }
        }

        internal static async Task<bool> StopRunningInstalledProcessesAsync(string installDir, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(installDir))
                return true;

            // Re-signal during the bounded grace window. This covers the small
            // bootstrap race where the first process delegates to the supervisor
            // before the supervised child has registered its shutdown event.
            var graceful = Stopwatch.StartNew();
            while (graceful.Elapsed < GracefulShutdownTimeout)
            {
                if (!AnyOwnedProcessRunningFromDirectory(installDir))
                    return true;

                VoltManagerArtifacts.TrySignalApplicationShutdown();
                await Task.Delay(ProcessPollInterval, ct).ConfigureAwait(false);
            }

            // Fallback ordering matters: stop launch/restart sources first, then
            // the UI process. HardwareService is left alive briefly so its parent
            // watcher can observe the UI exit and terminate naturally.
            ForceStopProcess("VoltManagerPlanSwitch", installDir);
            ForceStopProcess("VoltManager.Supervisor", installDir);
            ForceStopProcess("VoltManager", installDir);

            if (await WaitForProcessToExitAsync("VoltManager.HardwareService", installDir, HardwareParentExitTimeout, ct).ConfigureAwait(false) &&
                !AnyOwnedProcessRunningFromDirectory(installDir))
            {
                return true;
            }

            ForceStopProcess("VoltManager.HardwareService", installDir);
            return await WaitForNoOwnedProcessesAsync(installDir, ForcedShutdownVerificationTimeout, ct).ConfigureAwait(false);
        }

        private static async Task<bool> WaitForProcessToExitAsync(
            string processName,
            string installDir,
            TimeSpan timeout,
            CancellationToken ct)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                if (!AnyProcessRunningFromDirectory(processName, installDir))
                    return true;
                await Task.Delay(ProcessPollInterval, ct).ConfigureAwait(false);
            }
            return !AnyProcessRunningFromDirectory(processName, installDir);
        }

        private static async Task<bool> WaitForNoOwnedProcessesAsync(
            string installDir,
            TimeSpan timeout,
            CancellationToken ct)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                if (!AnyOwnedProcessRunningFromDirectory(installDir))
                    return true;
                await Task.Delay(ProcessPollInterval, ct).ConfigureAwait(false);
            }
            return !AnyOwnedProcessRunningFromDirectory(installDir);
        }

        private static bool AnyOwnedProcessRunningFromDirectory(string installDir)
        {
            foreach (string processName in VoltManagerArtifacts.ProcessNames)
            {
                if (AnyProcessRunningFromDirectory(processName, installDir))
                    return true;
            }
            return false;
        }

        private static bool AnyProcessRunningFromDirectory(string processName, string installDir)
        {
            foreach (Process process in Process.GetProcessesByName(processName))
            {
                try
                {
                    string? path = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path) && IsPathUnder(path!, installDir))
                        return true;
                }
                catch
                {
                    // The elevated setup can inspect VoltManager's own processes;
                    // inaccessible same-name processes outside the install are ignored.
                }
                finally
                {
                    process.Dispose();
                }
            }
            return false;
        }

        private static void ForceStopProcess(string processName, string installDir)
        {
            foreach (Process process in Process.GetProcessesByName(processName))
            {
                try
                {
                    string? path = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(path) || !IsPathUnder(path!, installDir))
                        continue;
                    process.Kill();
                    process.WaitForExit(3000);
                }
                catch
                {
                    // Final verification decides whether uninstall can report success.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        private static bool IsPathUnder(string path, string directory)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                string fullDirectory = Path.GetFullPath(directory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                return fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        internal static void DeleteStartupTask(UninstallResult result)
        {
            try
            {
                RunSchtasks("/delete /f /tn \"" + VoltManagerArtifacts.StartupTaskName + "\"");
            }
            catch (Exception ex)
            {
                result.Add("Startup task delete: " + ex.Message);
            }

        }

        internal static void RemoveShortcuts(UninstallResult result)
        {
            try
            {
                if (Directory.Exists(VoltManagerArtifacts.StartMenuDirectory))
                    Directory.Delete(VoltManagerArtifacts.StartMenuDirectory, true);
                if (File.Exists(VoltManagerArtifacts.DesktopShortcutPath))
                    File.Delete(VoltManagerArtifacts.DesktopShortcutPath);
            }
            catch (Exception ex)
            {
                result.Add("Shortcuts: " + ex.Message);
            }

        }

        internal static void RemoveRegistryEntries(UninstallResult result)
        {
            TryDeleteRegistryKey(VoltManagerArtifacts.UninstallRegistryKey, result, "ARP");
            TryDeleteRegistryKey(VoltManagerArtifacts.LegacyUninstallRegistryKey, result, "legacy ARP");
        }

        private static void TryDeleteRegistryKey(string keyPath, UninstallResult result, string label)
        {
            try
            {
                Registry.LocalMachine.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
            }
            catch (Exception ex)
            {
                result.Add(label + " delete: " + ex.Message);
            }
        }

        private static bool StartupTaskExists()
        {
            try
            {
                using (Process? process = Process.Start(new ProcessStartInfo(
                    "schtasks",
                    "/query /tn \"" + VoltManagerArtifacts.StartupTaskName + "\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                }))
                {
                    if (process == null) return false;
                    if (!process.WaitForExit(10000))
                    {
                        try { process.Kill(); } catch { }
                        return true;
                    }
                    return process.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private static void RunSchtasks(string arguments)
        {
            using (Process? process = Process.Start(new ProcessStartInfo("schtasks", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            }))
            {
                if (process == null)
                    throw new InvalidOperationException("Unable to start schtasks.exe.");
                if (!process.WaitForExit(10000))
                {
                    try { process.Kill(); } catch { }
                    throw new TimeoutException("schtasks.exe did not exit within 10 seconds.");
                }
            }
        }

        private static bool RegistryKeyExists(string keyPath)
        {
            try
            {
                using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(keyPath))
                    return key != null;
            }
            catch
            {
                return true;
            }
        }

        internal static IReadOnlyList<string> FindResidualArtifacts(
            string installDir,
            string appData,
            string currentExecutable)
        {
            var residuals = new List<string>();
            if (AnyOwnedProcessRunningFromDirectory(installDir))
                residuals.Add("Owned VoltManager process remains after uninstall");
            if (!string.IsNullOrWhiteSpace(installDir) && Directory.Exists(installDir))
                residuals.Add("Install directory still exists: " + installDir);
            if (Directory.Exists(appData))
                residuals.Add("AppData still exists: " + appData);
            if (StartupTaskExists())
                residuals.Add("Startup task still present: " + VoltManagerArtifacts.StartupTaskName);
            if (Directory.Exists(VoltManagerArtifacts.StartMenuDirectory))
                residuals.Add("Start menu shortcut directory still exists");
            if (File.Exists(VoltManagerArtifacts.DesktopShortcutPath))
                residuals.Add("Desktop shortcut still exists");
            if (RegistryKeyExists(VoltManagerArtifacts.UninstallRegistryKey))
                residuals.Add("ARP registry entry still exists");
            if (RegistryKeyExists(VoltManagerArtifacts.LegacyUninstallRegistryKey))
                residuals.Add("Legacy ARP registry entry still exists");

            foreach (string path in VoltManagerArtifacts.FindOwnedTempArtifacts(Path.GetTempPath(), currentExecutable))
                residuals.Add("Temp artifact still exists: " + path);

            return residuals;
        }
    }
}
