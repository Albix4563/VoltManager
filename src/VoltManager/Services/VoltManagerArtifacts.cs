using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace VoltManager.Services
{
    /// <summary>
    /// Single source of truth for artifacts owned by VoltManager that must be
    /// removed during a full uninstall. Shared with VoltManager.Setup.
    /// </summary>
    public static class VoltManagerArtifacts
    {
        public const string AppName = "VoltManager";
        public const string StartupTaskName = "VoltManagerAutostart";
        private const string UninstallShutdownEvent = "VoltManager_Uninstall_Shutdown_Event";
        public const string UninstallRegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VoltManager";
        public const string LegacyUninstallRegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{B7E64C0A-52D1-4E2B-9C0F-VOLTMGR00001}_is1";

        private static readonly string[] OwnedProcessNames =
        {
            "VoltManagerPlanSwitch",
            "VoltManager.Supervisor",
            "VoltManager",
            "VoltManager.HardwareService",
        };

        private static readonly string[] OwnedTempArtifactFileNames =
        {
            "VoltManagerUninstall.exe",
            "VoltManagerUpdate.exe",
            "VoltManagerPayload.zip",
            "MicrosoftEdgeWebview2Setup.exe",
            "vmgr_update_cleanup.bat",
            "vmgr_uninstall_cleanup.bat",
        };

        public static string[] ProcessNames => (string[])OwnedProcessNames.Clone();
        public static string[] TempArtifactFileNames => (string[])OwnedTempArtifactFileNames.Clone();
        public static string UninstallShutdownEventName => UninstallShutdownEvent;

        public static string AppDataDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppName);

        public static string StartMenuDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            AppName);

        public static string DesktopShortcutPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            AppName + ".lnk");

        /// <summary>
        /// Signals the running application to execute its normal ExitApp teardown.
        /// Returns false when no listener exists yet/already.
        /// </summary>
        public static bool TrySignalApplicationShutdown()
        {
            try
            {
                using (EventWaitHandle shutdownEvent = EventWaitHandle.OpenExisting(UninstallShutdownEventName))
                {
                    return shutdownEvent.Set();
                }
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Deletes only exact, known VoltManager temp artifacts. The executable
        /// currently running the uninstall is deliberately skipped and is deleted
        /// by the setup's exit-time self-cleanup helper.
        /// </summary>
        public static string[] CleanupOwnedTempArtifacts(string tempDirectory, string currentExecutablePath)
        {
            var failures = new List<string>();
            string root;
            try
            {
                root = Path.GetFullPath(tempDirectory ?? string.Empty);
            }
            catch (Exception ex)
            {
                return new[] { "Temp directory: " + ex.Message };
            }

            foreach (string fileName in OwnedTempArtifactFileNames)
            {
                string path = Path.Combine(root, fileName);
                if (SamePath(path, currentExecutablePath))
                    continue;

                if (!TryDeleteFile(path, out string error))
                    failures.Add(fileName + ": " + error);
            }

            return failures.ToArray();
        }

        public static string[] FindOwnedTempArtifacts(string tempDirectory, string currentExecutablePath)
        {
            var remaining = new List<string>();
            string root;
            try
            {
                root = Path.GetFullPath(tempDirectory ?? string.Empty);
            }
            catch
            {
                return remaining.ToArray();
            }

            foreach (string fileName in OwnedTempArtifactFileNames)
            {
                string path = Path.Combine(root, fileName);
                if (SamePath(path, currentExecutablePath))
                    continue;
                try
                {
                    if (File.Exists(path)) remaining.Add(path);
                }
                catch { }
            }

            return remaining.ToArray();
        }

        private static bool TryDeleteFile(string path, out string error)
        {
            error = string.Empty;
            if (!File.Exists(path)) return true;

            for (int attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    if (!File.Exists(path)) return true;
                    FileAttributes attributes = File.GetAttributes(path);
                    if ((attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0)
                    {
                        File.SetAttributes(path, attributes & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System));
                    }
                    File.Delete(path);
                    if (!File.Exists(path)) return true;
                    error = "file still exists after delete";
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                if (attempt < 5)
                    Thread.Sleep(100 * attempt);
            }

            return !File.Exists(path);
        }

        private static bool SamePath(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;
            try
            {
                return string.Equals(
                    Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}