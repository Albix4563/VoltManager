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

        /// <summary>
        /// An ARP UninstallString points at a setup executable which can subsequently
        /// be launched elevated. Never register that executable in an arbitrary
        /// user-writable custom install root. This is a conservative boundary, not
        /// an atomic guarantee against concurrent filesystem replacement.
        /// </summary>
        internal static void RequireProtectedElevatedUninstallerLocation(string installDir)
        {
            if (!IsUnderProgramFiles(installDir))
                throw new InvalidOperationException(
                    "Cannot register an elevated uninstall.exe outside Program Files without verified protected ancestry.");

            // Existing parent directories must not redirect the path into a
            // user-controlled tree. Remaining identity races require handle-based
            // verification and are intentionally not claimed solved here.
            string primary = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string secondary = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string protectedBase = IsSameOrUnder(installDir, primary)
                ? InstallTargetValidator.Normalize(primary)
                : InstallTargetValidator.Normalize(secondary);
            string? current = Path.GetFullPath(installDir);
            while (!string.IsNullOrWhiteSpace(current))
            {
                if (Directory.Exists(current) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException(
                        "Elevated uninstaller path has a linked directory: " + current);
                if (Directory.Exists(current) &&
                    !HasProtectedElevationAcl(Directory.GetAccessControl(current)))
                    throw new InvalidOperationException(
                        "Elevated uninstaller path has an unverified owner or writable permissions: " + current);
                // Evaluate the protected Program Files anchor and all descendants.
                // A drive root may legitimately grant CreateDirectories for new
                // siblings without granting replacement rights over Program Files.
                if (string.Equals(InstallTargetValidator.Normalize(current), protectedBase,
                    StringComparison.OrdinalIgnoreCase)) break;
                string? parent = Path.GetDirectoryName(current);
                if (string.Equals(current, parent, StringComparison.OrdinalIgnoreCase)) break;
                current = parent;
            }

            // L'inventario installato dipende dall'uninstaller: anche il file stesso
            // deve avere owner privilegiato e nessun permesso di scrittura per utenti.
            string uninstaller = Path.Combine(installDir, "uninstall.exe");
            if (File.Exists(uninstaller) &&
                ((File.GetAttributes(uninstaller) & FileAttributes.ReparsePoint) != 0 ||
                 !HasProtectedElevationAcl(File.GetAccessControl(uninstaller))))
                throw new InvalidOperationException(
                    "Installed uninstaller has an unverified owner or writable permissions: " + uninstaller);
        }

        /// <summary>
        /// A conservative DACL/owner gate, not an effective-access computation or
        /// identity-bound race guarantee. Reject unknown owners and any explicit
        /// or inherited write grant to a non-system principal.
        /// </summary>
        internal static bool HasProtectedElevationAcl(FileSystemSecurity security)
        {
            if (security == null) return false;
            SecurityIdentifier? owner;
            try { owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier; }
            catch { return false; }
            if (owner == null || !IsTrustedElevatedPrincipal(owner)) return false;

            // Do not use the composite Write/Modify/FullControl masks here:
            // they include ReadPermissions/Synchronize, which ReadAndExecute
            // shares and would falsely classify read-only users as writers.
            const FileSystemRights writes = FileSystemRights.WriteData | FileSystemRights.AppendData |
                FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes |
                FileSystemRights.Delete |
                FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions |
                FileSystemRights.TakeOwnership;

            try
            {
                AuthorizationRuleCollection rules = security.GetAccessRules(
                    includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier));
                foreach (FileSystemAccessRule rule in rules)
                {
                    if (rule.AccessControlType != AccessControlType.Allow ||
                        (rule.FileSystemRights & writes) == 0) continue;
                    if (rule.IdentityReference is not SecurityIdentifier sid ||
                        !IsTrustedElevatedPrincipal(sid)) return false;
                }
                return true;
            }
            catch { return false; }
        }

        private static bool IsTrustedElevatedPrincipal(SecurityIdentifier sid)
            => sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
               sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
               // Windows Modules Installer / TrustedInstaller.
               string.Equals(sid.Value,
                   "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464",
                   StringComparison.Ordinal);

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
