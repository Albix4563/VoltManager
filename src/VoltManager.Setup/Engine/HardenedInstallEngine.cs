using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        private readonly string? _trustedPayloadZipPath;

        public HardenedInstallEngine()
            : this(new SystemUninstallSystemOperations(), () => Assembly.GetExecutingAssembly().Location)
        {
        }

        internal HardenedInstallEngine(IUninstallSystemOperations systemOperations,
            Func<string> currentExecutable, string? trustedPayloadZipPath = null)
        {
            _systemOperations = systemOperations ?? throw new ArgumentNullException(nameof(systemOperations));
            _currentExecutable = currentExecutable ?? throw new ArgumentNullException(nameof(currentExecutable));
            _trustedPayloadZipPath = trustedPayloadZipPath;
        }

        public async Task<UninstallResult> UninstallAsync(string? targetDir = null, CancellationToken ct = default)
        {
            var result = new UninstallResult();
            string installDir = ResolveInstallDir(targetDir);
            string currentExecutable = _currentExecutable();

            ct.ThrowIfCancellationRequested();
            try
            {
                // Even process shutdown is a side effect: an arbitrary directory with
                // familiar executable names must not authorize stopping those processes
                // (or clearing global LAN, AppData and registry state). Verify ownership
                // against an independent trusted payload BEFORE any cleanup begins.
                // A forged legacy marker with no installed payload matches no hashes.
                // Its mere presence cannot authorize global uninstall side effects.
                if (_trustedPayloadZipPath == null)
                    InstallDirectorySecurity.RequireProtectedElevatedUninstallerLocation(installDir);
                InstallManifest.RequireUninstallOwnership(installDir, _trustedPayloadZipPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Add("Install directory: ownership verification failed: " + ex.Message);
                foreach (string residual in _systemOperations.FindResidualArtifacts(
                    installDir, _systemOperations.AppDataDirectory, currentExecutable))
                    result.AddResidual(residual);
                return result;
            }

            ct.ThrowIfCancellationRequested();
            // A self-hosted uninstaller cannot remove its own executable. Reject
            // this before stopping otherwise healthy installed processes.
            if (IsPathUnder(currentExecutable, installDir))
            {
                result.Add("Uninstaller is still running from the install directory");
                foreach (string residual in _systemOperations.FindResidualArtifacts(
                    installDir, _systemOperations.AppDataDirectory, currentExecutable))
                    result.AddResidual(residual);
                return result;
            }

            Report(I18n.T("status_uninst_kill"), 5);
            bool stopped = await _systemOperations.StopRunningInstalledProcessesAsync(installDir, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!stopped)
            {
                result.Add("VoltManager process still running after graceful and forced shutdown");
                // A running owned process may still be using installed files and user data.
                // Report the remaining artifacts without starting destructive cleanup.
                foreach (string residual in _systemOperations.FindResidualArtifacts(
                    installDir, _systemOperations.AppDataDirectory, currentExecutable))
                    result.AddResidual(residual);
                return result;
            }

            if (_systemOperations.DirectoryExists(installDir) && IsPathUnder(currentExecutable, installDir))
            {
                result.Add("Uninstaller is still running from the install directory");
                foreach (string residual in _systemOperations.FindResidualArtifacts(
                    installDir, _systemOperations.AppDataDirectory, currentExecutable))
                    result.AddResidual(residual);
                return result;
            }

            ct.ThrowIfCancellationRequested();
            Report(I18n.T("status_uninst_files"), 20);
            if (string.IsNullOrWhiteSpace(installDir) ||
                !_systemOperations.DirectoryExists(installDir) || !Directory.Exists(installDir))
            {
                result.Add("Install directory: ownership cannot be verified because the installation is missing.");
                foreach (string residual in _systemOperations.FindResidualArtifacts(
                    installDir, _systemOperations.AppDataDirectory, currentExecutable))
                    result.AddResidual(residual);
                return result;
            }

            // Shutdown can take several seconds. Repeat the ownership gate before any
            // global cleanup in case the target changed while processes were stopping.
            try
            {
                if (_trustedPayloadZipPath == null)
                    InstallDirectorySecurity.RequireProtectedElevatedUninstallerLocation(installDir);
                InstallManifest.RequireUninstallOwnership(installDir, _trustedPayloadZipPath);
            }
            catch (Exception ex)
            {
                result.Add("Install directory: ownership verification failed after shutdown: " + ex.Message);
                return result;
            }

            // Launch sources go first: a later failure must not leave a task, shortcut
            // or Apps & features entry pointing at an already deleted executable.
            ct.ThrowIfCancellationRequested();
            Report(I18n.T("status_startup"), 25);
            _systemOperations.DeleteStartupTask(result);
            _systemOperations.RemoveShortcuts(result);

            ct.ThrowIfCancellationRequested();
            Report(I18n.T("status_uninst_reg"), 35);
            _systemOperations.RemoveRegistryEntries(result);

            ct.ThrowIfCancellationRequested();
            _systemOperations.RemoveLanRemoteControlArtifacts(result);

            ct.ThrowIfCancellationRequested();
            Report(I18n.T("status_uninst_files"), 45);
            string appData = _systemOperations.AppDataDirectory;
            // Read before AppData goes away: the plan map lives in settings.json.
            _systemOperations.RestoreDefaultPowerPlan(appData, result);

            UninstallCleanupResult installCleanup = _systemOperations.DeleteInstallFiles(installDir, _trustedPayloadZipPath);
            foreach (string failure in installCleanup.Failures)
                result.Add("Install directory: " + failure);
            if (installCleanup.Retained.Count > 0)
                result.AddResidual("Kept in " + installDir + " (not installed by VoltManager or modified): " +
                                   string.Join(", ", installCleanup.Retained));
            foreach (string pending in installCleanup.PendingReboot)
                result.AddWarning(string.Format(I18n.T("uninst_reboot"), pending));
            foreach (string retained in _systemOperations.DeleteSwapLeftovers(installDir, _trustedPayloadZipPath))
                result.AddResidual("Update leftover retained for manual review: " + retained);

            ct.ThrowIfCancellationRequested();
            Report(I18n.T("status_uninst_files"), 70);
            DeleteAppData(appData, result);

            ct.ThrowIfCancellationRequested();
            Report(I18n.T("status_uninst_files"), 92);
            foreach (string failure in _systemOperations.CleanupOwnedTempArtifacts(currentExecutable))
                result.Add("Temp artifact: " + failure);

            _systemOperations.KeptAppDataEntries = PurgeUserData ? Array.Empty<string>() : KeptUserDataEntries;
            foreach (string residual in _systemOperations.FindResidualArtifacts(installDir, appData, currentExecutable))
            {
                // Report scheduled removals separately from unexpected residuals.
                if (installCleanup.PendingReboot.Count > 0 &&
                    residual == OwnedInstallResidualPrefix + installDir)
                    continue;
                if (result.PendingReboot.Any(path => residual == "AppData entry still exists: " + path))
                    continue;
                result.AddResidual(residual);
            }

            Report("", 100);
            return result;
        }

        /// <summary>When false (default), settings and history survive a reinstall; credentials, caches and logs never do.</summary>
        public bool PurgeUserData { get; set; }

        internal static readonly string[] KeptUserDataEntries =
        {
            "settings.json", "settings.json.bak", "battery-history.json", "launcher-shortcuts",
        };

        private void DeleteAppData(string appData, UninstallResult result)
        {
            if (!_systemOperations.DirectoryExists(appData)) return;
            IReadOnlyList<string> kept = _systemOperations.DeleteAppDataEntries(
                appData, PurgeUserData ? Array.Empty<string>() : KeptUserDataEntries, result);
            if (kept.Count > 0)
                result.AddWarning(string.Format(I18n.T("uninst_kept"), appData));
        }

        private const string OwnedInstallResidualPrefix = "Owned install artifacts still exist in: ";

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
                if (SetupStaging.IsStagedUpdateExecutable(self, SetupStaging.DefaultUpdatesRoot))
                {
                    // Children before parents: MoveFileEx on a directory that is not empty
                    // at boot simply fails, so the shared roots survive other run dirs.
                    string? selfDirectory = Path.GetDirectoryName(self);
                    string? updatesRoot = Path.GetDirectoryName(selfDirectory);
                    if (selfDirectory != null && SetupUpdateLog.FilePath.StartsWith(
                            selfDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        SetupStaging.ScheduleDeleteOnReboot(SetupUpdateLog.FilePath, SetupUpdateLog.Warn);
                    foreach (string? path in new[] { self, selfDirectory, updatesRoot, Path.GetDirectoryName(updatesRoot) })
                        if (!string.IsNullOrWhiteSpace(path))
                            SetupStaging.ScheduleDeleteOnReboot(path!, SetupUpdateLog.Warn);
                    return;
                }

                string tempRoot = Path.GetFullPath(Path.GetTempPath())
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string? legacyDirectory = Path.GetDirectoryName(self);
                string tempPrefix = tempRoot + Path.DirectorySeparatorChar;
                if (string.IsNullOrWhiteSpace(legacyDirectory) ||
                    !string.Equals(Path.GetFileName(self), "VoltManagerUninstall.exe", StringComparison.OrdinalIgnoreCase) ||
                    (!string.Equals(legacyDirectory, tempRoot, StringComparison.OrdinalIgnoreCase) &&
                     !legacyDirectory.StartsWith(tempPrefix, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                SetupStaging.ScheduleDeleteOnReboot(self, SetupUpdateLog.Warn);
                if (!string.Equals(legacyDirectory, tempRoot, StringComparison.OrdinalIgnoreCase))
                    SetupStaging.ScheduleDeleteOnReboot(legacyDirectory, SetupUpdateLog.Warn);
            }
            catch (Exception ex)
            {
                SetupUpdateLog.Warn("Cleanup uninstaller differito fallito: " + ex.Message);
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
            => DeleteStartupTask(result, () => SchtasksRunner.Run(
                "/delete /f /tn \"" + VoltManagerArtifacts.StartupTaskName + "\""));

        internal static void DeleteStartupTask(UninstallResult result, Func<SchtasksResult> runDelete)
        {
            SchtasksResult taskResult = runDelete();
            if (taskResult.Success || SchtasksRunner.IsTaskNotFound(taskResult))
                return;

            string detail = taskResult.TimedOut
                ? "schtasks.exe timed out."
                : "schtasks.exe exited with code " + taskResult.ExitCode + ".";
            if (!string.IsNullOrWhiteSpace(taskResult.Output))
                detail += " " + taskResult.Output;
            SetupUpdateLog.Warn("Startup task delete failed: " + detail);
            result.Add("Startup task delete: " + detail);
        }

        internal static void RemoveShortcuts(UninstallResult result)
        {
            try
            {
                if (Directory.Exists(VoltManagerArtifacts.StartMenuDirectory))
                    Directory.Delete(VoltManagerArtifacts.StartMenuDirectory, true);
            }
            catch (Exception ex)
            {
                result.Add("Start menu shortcut: " + ex.Message);
            }

            try
            {
                if (File.Exists(VoltManagerArtifacts.DesktopShortcutPath))
                    File.Delete(VoltManagerArtifacts.DesktopShortcutPath);
            }
            catch (Exception ex)
            {
                result.Add("Desktop shortcut: " + ex.Message);
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
                        try { process.Kill(); } catch { /* best-effort: process may exit between the check and kill. */ }
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
            string currentExecutable,
            IReadOnlyCollection<string> keptAppDataEntries, bool checkCurrentUserCertificate = true)
        {
            var residuals = new List<string>();
            if (AnyOwnedProcessRunningFromDirectory(installDir))
                residuals.Add("Owned VoltManager process remains after uninstall");
            if (!string.IsNullOrWhiteSpace(installDir) && InstallManifest.HasOwnedArtifacts(installDir))
                residuals.Add(OwnedInstallResidualPrefix + installDir);
            foreach (string entry in RemainingAppDataEntries(appData, keptAppDataEntries))
                residuals.Add("AppData entry still exists: " + entry);
            foreach (string swap in FindSwapLeftovers(installDir))
                residuals.Add("Update leftover still exists: " + swap);
            foreach (string stale in FindProgramDataLeftovers(currentExecutable))
                residuals.Add("ProgramData leftover still exists: " + stale);
            if (SystemUninstallSystemOperations.LanRemoteFirewallRuleExists())
                residuals.Add("LAN remote firewall rule still present");
            if (checkCurrentUserCertificate && SystemUninstallSystemOperations.LanRemoteCertificateExists())
                residuals.Add("LAN remote certificate still present");
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

        internal static IEnumerable<string> RemainingAppDataEntries(string appData, IReadOnlyCollection<string> kept)
        {
            if (!Directory.Exists(appData)) yield break;
            var keep = new HashSet<string>(kept, StringComparer.OrdinalIgnoreCase);
            foreach (string entry in Directory.GetFileSystemEntries(appData))
                if (!keep.Contains(Path.GetFileName(entry)))
                    yield return entry;
        }

        private static IEnumerable<string> FindSwapLeftovers(string installDir)
        {
            string? parent;
            string name;
            try
            {
                string full = Path.GetFullPath(installDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                parent = Path.GetDirectoryName(full);
                name = Path.GetFileName(full);
                if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) return Array.Empty<string>();
                return Directory.GetDirectories(parent, "." + name + ".backup-*")
                    .Concat(Directory.GetDirectories(parent, "." + name + ".staging-*"))
                    .ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        // The running uninstaller's own run dir is scheduled for reboot deletion, not a residual.
        private static IEnumerable<string> FindProgramDataLeftovers(string currentExecutable)
        {
            try
            {
                string updates = VoltManagerArtifacts.UpdatesDirectory;
                string? root = Path.GetDirectoryName(updates);
                if (root == null || !Directory.Exists(root)) return Array.Empty<string>();
                string? ownRunDir = Path.GetDirectoryName(Path.GetFullPath(currentExecutable));
                var leftovers = new List<string>();
                foreach (string entry in Directory.GetFileSystemEntries(root))
                {
                    if (!string.Equals(entry, updates, StringComparison.OrdinalIgnoreCase))
                        leftovers.Add(entry);
                    else if (Directory.Exists(updates))
                        leftovers.AddRange(Directory.GetFileSystemEntries(updates).Where(child =>
                            !string.Equals(child, ownRunDir, StringComparison.OrdinalIgnoreCase)));
                }
                return leftovers;
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }
}
