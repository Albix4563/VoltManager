using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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
            "VoltManagerPreviewSetup.exe",
            "VoltManagerPreviewPayload.zip",
            "VoltManagerPayload.zip",
            "vmgr_update_cleanup.bat",
            "vmgr_uninstall_cleanup.bat",
        };

        public static string[] ProcessNames => (string[])OwnedProcessNames.Clone();
        public static string[] TempArtifactFileNames => (string[])OwnedTempArtifactFileNames.Clone();
        public static string UninstallShutdownEventName => ValidationNamedObject(UninstallShutdownEvent);

        public static string AppDataDirectory => Path.Combine(
            ValidationApplicationDataRoot,
            AppName);

        public static string UpdatesDirectory => Path.Combine(
            ValidationProgramDataRoot,
            AppName,
            "Updates");

        private static string ValidationProgramDataRoot
        {
            get
            {
                var validationRoot = Environment.GetEnvironmentVariable("VOLTMANAGER_VALIDATION_ROOT");
                return string.IsNullOrWhiteSpace(validationRoot)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
                    : Path.Combine(Path.GetFullPath(validationRoot), "ProgramData");
            }
        }

        // This file is compile-linked into VoltManager.Setup, so keep the private
        // validation redirect self-contained instead of depending on app-only types.
        // Production/setup behavior is unchanged unless the repository harness sets
        // VOLTMANAGER_VALIDATION_ROOT explicitly.
        private static string ValidationApplicationDataRoot
        {
            get
            {
                var validationRoot = Environment.GetEnvironmentVariable("VOLTMANAGER_VALIDATION_ROOT");
                return string.IsNullOrWhiteSpace(validationRoot)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                    : Path.GetFullPath(validationRoot);
            }
        }

        private static string ValidationNamedObject(string productionName)
        {
            var validationRoot = Environment.GetEnvironmentVariable("VOLTMANAGER_VALIDATION_ROOT");
            if (string.IsNullOrWhiteSpace(validationRoot)) return productionName;
            byte[] digest;
            using (SHA256 sha = SHA256.Create())
                digest = sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(validationRoot).ToUpperInvariant()));
            string suffix = BitConverter.ToString(digest, 0, 6).Replace("-", string.Empty);
            return productionName + "_Validation_" + suffix;
        }

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
            => TrySignalShutdownEvent(UninstallShutdownEventName);

        internal static bool TrySignalShutdownEvent(string eventName)
        {
            try
            {
                using (EventWaitHandle shutdownEvent = EventWaitHandle.OpenExisting(eventName))
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
        /// Reports legacy temp artifact candidates without deleting them: a file
        /// name alone cannot establish provenance and may identify someone else's
        /// data. The running uninstaller is handled by its own exit-time helper.
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

                // Historical cleanup filenames are not an ownership record. In
                // particular never reset attributes or remove arbitrary files
                // that another process could have placed at these names.
                if (File.Exists(path) || Directory.Exists(path))
                    failures.Add(fileName + ": unverified temp artifact retained; filename does not prove ownership.");
            }

            if (SamePath(root, Path.GetFullPath(Path.GetTempPath())) &&
                !TryDeleteDirectoryTree(UpdatesDirectory, out string updatesError))
            {
                failures.Add("Updates directory: " + updatesError);
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
                catch { /* best-effort: artifact probing skips inaccessible entries. */ }
            }

            if (SamePath(root, Path.GetFullPath(Path.GetTempPath())))
            {
                try
                {
                    if (Directory.Exists(UpdatesDirectory)) remaining.Add(UpdatesDirectory);
                }
                catch { /* best-effort: artifact probing skips inaccessible entries. */ }
            }

            return remaining.ToArray();
        }

        private static bool TryDeleteDirectoryTree(string path, out string error)
        {
            error = string.Empty;
            try
            {
                string fullPath = Path.GetFullPath(path);
                // Never recursively remove a tree based only on a known folder
                // name. Only this application's exact update path is eligible
                // for removal, and then only if the directory is already empty.
                if (!SamePath(fullPath, UpdatesDirectory))
                {
                    error = "unverified directory retained: expected the configured updates directory.";
                    return false;
                }

                if (!Directory.Exists(fullPath)) return true;
                string? current = fullPath;
                while (!string.IsNullOrEmpty(current))
                {
                    if (Directory.Exists(current) &&
                        (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    {
                        error = "unverified directory retained: path or parent is a reparse point.";
                        return false;
                    }
                    string? parent = Path.GetDirectoryName(current);
                    if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
                    current = parent;
                }
                if (Directory.GetFileSystemEntries(fullPath).Length != 0)
                {
                    error = "unverified nonempty updates directory retained for manual review.";
                    return false;
                }
                Directory.Delete(fullPath, recursive: false);
                return true;
            }
            catch (Exception ex)
            {
                error = "unverified updates directory retained: " + ex.Message;
                return false;
            }
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
