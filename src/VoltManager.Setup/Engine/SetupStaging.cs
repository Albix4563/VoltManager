using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace VoltManager.Setup.Engine
{
    internal static class SetupStaging
    {
        private const int MoveFileDelayUntilReboot = 0x00000004;

        internal static string DefaultUpdatesRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "VoltManager",
            "Updates");

        internal static string CreateProtectedRunDirectory(string updatesRoot)
        {
            if (string.IsNullOrWhiteSpace(updatesRoot))
                throw new ArgumentException("Updates root is required.", nameof(updatesRoot));

            // Junctions/symlinks would redirect the ACL and the extracted installer elsewhere.
            string? parent = Path.GetDirectoryName(updatesRoot);
            if (parent != null && Directory.Exists(parent))
                ThrowIfReparsePoint(parent);
            Directory.CreateDirectory(updatesRoot);
            ThrowIfReparsePoint(updatesRoot);
            ApplyProtectedAcl(updatesRoot);
            string runDirectory = Path.Combine(updatesRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(runDirectory);
            try
            {
                ThrowIfReparsePoint(runDirectory);
                ApplyProtectedAcl(runDirectory);
                return runDirectory;
            }
            catch
            {
                try { Directory.Delete(runDirectory, true); } catch { /* best-effort: stale staging cleanup must not block setup. */ }
                throw;
            }
        }

        internal static bool IsReparsePoint(string path)
            => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

        private static void ThrowIfReparsePoint(string path)
        {
            if (IsReparsePoint(path))
                throw new InvalidDataException("Updates directory '" + path + "' is a reparse point (junction/symlink).");
        }

        internal static void ApplyProtectedAcl(string directory)
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            const FileSystemRights rights = FileSystemRights.FullControl;
            const InheritanceFlags inheritance =
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                rights,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                rights,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));

            // The owner keeps implicit WRITE_DAC: a directory owned by any other account (including
            // the interactive user, who could rewrite the DACL unelevated) goes to Administrators.
            var owner = Directory.GetAccessControl(directory, AccessControlSections.Owner)
                .GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (!IsTrustedOwner(owner))
                security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));

            Directory.SetAccessControl(directory, security);
        }

        internal static bool IsTrustedOwner(SecurityIdentifier? owner)
        {
            return owner != null &&
                   (owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
                    owner.IsWellKnown(WellKnownSidType.LocalSystemSid));
        }

        internal static bool IsStagedUpdateExecutable(string executablePath, string updatesRoot)
        {
            if (string.IsNullOrWhiteSpace(executablePath) || string.IsNullOrWhiteSpace(updatesRoot))
                return false;

            try
            {
                string fullExecutable = Path.GetFullPath(executablePath);
                string fullRoot = Path.GetFullPath(updatesRoot)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string? parent = Path.GetDirectoryName(fullExecutable);
                if (string.IsNullOrWhiteSpace(parent))
                    return false;

                string? grandParent = Directory.GetParent(parent)?.FullName;
                if (string.IsNullOrWhiteSpace(grandParent))
                    return false;

                return string.Equals(
                    grandParent!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    fullRoot,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsLegacyDownloadedUpdateExecutable(string executablePath, string tempRoot)
        {
            try
            {
                string expected = Path.Combine(Path.GetFullPath(tempRoot), "VoltManagerUpdate.exe");
                return string.Equals(
                    Path.GetFullPath(executablePath),
                    Path.GetFullPath(expected),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        internal static bool ScheduleDeleteOnReboot(string path, Action<string>? logFailure = null)
        {
            if (string.IsNullOrWhiteSpace(path))
                return true;

            if (MoveFileEx(path, null, MoveFileDelayUntilReboot))
                return true;

            int error = Marshal.GetLastWin32Error();
            logFailure?.Invoke(
                "Impossibile pianificare la rimozione al riavvio di '" + path + "': " +
                new Win32Exception(error).Message + " (" + error + ").");
            return false;
        }

        internal static void DeleteRunDirectoryBestEffort(string? runDirectory, Action<string>? logFailure = null)
        {
            if (string.IsNullOrWhiteSpace(runDirectory) || !Directory.Exists(runDirectory))
                return;

            try
            {
                Directory.Delete(runDirectory, true);
                return;
            }
            catch (Exception ex)
            {
                logFailure?.Invoke(
                    "Pulizia directory Setup differita per '" + runDirectory + "': " + ex.Message);
            }

            ScheduleTreeDeleteOnReboot(runDirectory!, logFailure);
        }

        private static void ScheduleTreeDeleteOnReboot(string root, Action<string>? logFailure)
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    ScheduleDeleteOnReboot(file, logFailure);

                var directories = new List<string>(
                    Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories));
                foreach (string directory in directories.OrderByDescending(path => path.Length))
                    ScheduleDeleteOnReboot(directory, logFailure);
            }
            catch (Exception ex)
            {
                logFailure?.Invoke(
                    "Scansione cleanup differito fallita per '" + root + "': " + ex.Message);
            }

            ScheduleDeleteOnReboot(root, logFailure);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(
            string lpExistingFileName,
            string? lpNewFileName,
            int dwFlags);
    }
}
