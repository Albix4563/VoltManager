using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace VoltManager.Setup.Engine
{
    internal static class InstallDirectorySecurity
    {
        internal static bool IsUnderProgramFiles(string path)
            => IsUnderProgramFiles(
                path,
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));

        internal static bool IsUnderProgramFiles(string path, string? programFiles, string? programFilesX86)
            => IsSameOrUnder(path, programFiles) || IsSameOrUnder(path, programFilesX86);

        internal static void ApplyRestrictiveAcl(string installDir)
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                FileSystemRights.ReadAndExecute,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
            Directory.SetAccessControl(installDir, security);
        }

        private static bool IsSameOrUnder(string path, string? directory)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory)) return false;
            try
            {
                string fullPath = InstallTargetValidator.Normalize(path);
                string fullDirectory = InstallTargetValidator.Normalize(directory!);
                return string.Equals(fullPath, fullDirectory, StringComparison.OrdinalIgnoreCase) ||
                       fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
