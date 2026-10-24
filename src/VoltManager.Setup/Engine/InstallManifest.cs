using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using System.Web.Script.Serialization;

namespace VoltManager.Setup.Engine
{
    internal static class InstallManifest
    {
        internal const string FileName = ".voltmanager-install";

        private static readonly string[] KnownAppEntries =
        {
            "VoltManager.exe",
            "VoltManager.exe.config",
            "VoltManager.Supervisor.exe",
            "VoltManager.HardwareService.exe",
            "VoltManagerPlanSwitch.exe",
            "VoltManagerPlanSwitch.exe.config",
            "uninstall.exe",
        };

        private static readonly HashSet<string> KnownAppFileNames =
            new HashSet<string>(KnownAppEntries, StringComparer.OrdinalIgnoreCase);

        internal static IReadOnlyCollection<string> ReadOwnedEntries(string installDir)
        {
            string marker = Path.Combine(installDir, FileName);
            if (Directory.Exists(marker))
                throw new InvalidDataException("Install manifest path is a directory.");
            if (!File.Exists(marker)) return Array.Empty<string>();
            try
            {
                ThrowIfReparsePoint(marker);
                var root = SettingsJsonUpdater.CreateSerializer().DeserializeObject(File.ReadAllText(marker)) as Dictionary<string, object?>;
                if (root == null || !root.TryGetValue("entries", out object? value) || value is not object[] entries)
                    throw new InvalidDataException("Install manifest has no valid entries array.");
                if (entries.Any(item => item is not string name || !IsSafeTopLevelName(name)))
                    throw new InvalidDataException("Install manifest contains an invalid top-level name.");
                return entries
                    .Select(item => item as string)
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception ex)
            {
                SetupUpdateLog.Warn("Could not read install manifest: " + ex.Message);
                throw new InvalidDataException("Install manifest cannot be trusted: " + ex.Message, ex);
            }
        }

        internal static void Write(string installDir, IEnumerable<string> topLevelEntries)
        {
            string[] entries = topLevelEntries
                .Where(IsSafeTopLevelName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var root = new Dictionary<string, object?> { ["entries"] = entries };
            File.WriteAllText(Path.Combine(installDir, FileName), SettingsJsonUpdater.CreateSerializer().Serialize(root));
        }

        internal static HashSet<string> GetOwnedNamesForReplacement(
            string destDir, IEnumerable<string> stagedNames, string? trustedPayloadZipPath = null,
            string? trustedInstalledPayloadZipPath = null, Action<string>? requireProtectedInstallLocation = null)
        {
            ValidateInstallRoot(destDir);
            // Gli hash della nuova release non autenticano i byte della precedente:
            // la release installata è verificata tramite il proprio uninstaller protetto.
            PayloadInventory? stagedInventory = LoadTrustedPayload(trustedPayloadZipPath);
            PayloadInventory? installedInventory = LoadInstalledInventory(
                destDir, trustedInstalledPayloadZipPath, () => stagedInventory, requireProtectedInstallLocation);
            IReadOnlyCollection<string> previous =
                ValidateExistingInstallBeforeSideEffectsWithInventory(destDir, installedInventory);
            // Historical executable names are not ownership declarations. Only
            // the checked manifest candidates may be replaced, except for the
            // setup uninstaller, which is installed after the manifest is written
            // and must independently match the running setup executable.
            var owned = new HashSet<string>(previous, StringComparer.OrdinalIgnoreCase);
            if (File.Exists(Path.Combine(destDir, "uninstall.exe")))
                owned.Add("uninstall.exe");
            foreach (string name in stagedNames)
            {
                if (!IsSafeTopLevelName(name) || string.Equals(name, FileName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Invalid or reserved staged payload name: " + name);
                if (stagedInventory != null && !stagedInventory.TopLevelNames.Contains(name))
                    throw new InvalidDataException("Staged entry is absent from trusted payload: " + name);
                string existing = Path.Combine(destDir, name);
                if (!owned.Contains(name) && (File.Exists(existing) || Directory.Exists(existing)))
                    throw new InvalidDataException("Cannot replace existing entry without verified ownership: " + name);
                owned.Add(name);
            }
            ValidateCandidateFiles(destDir);
            // Names and mutable marker entries never establish ownership of
            // bytes about to be backed up or replaced. Only an independently
            // authenticated inventory of the INSTALLED version can verify them.
            ValidateManagedEntries(destDir, owned, installedInventory, requireContentHash: true);
            owned.Add(FileName);
            return owned;
        }

        /// <summary>
        /// Uninstall-time ownership gate, run before any side effect. Structural problems
        /// (missing or forged manifest, no trusted inventory, links) fail closed, and at
        /// least one physically present, hash-verified payload file must exist.
        /// </summary>
        internal static IReadOnlyCollection<string> RequireUninstallOwnership(
            string installDir, string? trustedPayloadZipPath = null)
            => RequireUninstallOwnership(installDir, LoadTrustedPayload(trustedPayloadZipPath));

        private static IReadOnlyCollection<string> RequireUninstallOwnership(
            string installDir, PayloadInventory? inventory)
        {
            ValidateInstallRoot(installDir);
            if (!Directory.Exists(installDir))
                throw new InvalidDataException("Install directory is missing.");
            if (SetupStaging.ContainsReparsePointInTree(installDir))
                throw LinkedEntry("Install tree contains a reparse point: " + installDir);
            IReadOnlyCollection<string> listed = ValidateManifestOwnership(installDir, inventory);
            ValidateCandidateFiles(installDir);
            foreach (string name in listed)
                ThrowIfLinkedFileInTree(Path.Combine(installDir, name));
            string uninstaller = Path.Combine(installDir, "uninstall.exe");
            if (File.Exists(uninstaller)) ValidateSingleFile(uninstaller);
            if (!listed.Any(name => HasHashVerifiedFile(Path.Combine(installDir, name), name, inventory!)))
                throw new InvalidDataException("Install tree contains no independently verified installed file.");
            return listed;
        }

        internal static void ThrowIfLinkedFileInTree(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return;
            ThrowIfReparsePoint(path);
            if (File.Exists(path))
                ValidateSingleFile(path);
            else
                foreach (string child in Directory.GetFileSystemEntries(path))
                    ThrowIfLinkedFileInTree(child);
        }

        private static bool HasHashVerifiedFile(string path, string relative, PayloadInventory inventory)
        {
            if (Directory.Exists(path))
                return inventory.DirectoryPaths.Contains(relative) &&
                       Directory.GetFileSystemEntries(path).Any(child =>
                           HasHashVerifiedFile(child, relative + "/" + Path.GetFileName(child), inventory));
            if (!File.Exists(path) || !inventory.FileHashes.ContainsKey(relative) ||
                string.Equals(relative, "uninstall.exe", StringComparison.OrdinalIgnoreCase))
                return false;
            try
            {
                ValidateManagedEntry(path, relative, inventory, requireContentHash: true);
                return true;
            }
            catch (InvalidDataException ex) when (!IsLinkedEntry(ex))
            {
                return false;
            }
        }

        /// <summary>
        /// Removes every hash-verified entry and retains (never deletes) foreign or modified
        /// ones, so a stray file cannot block the rest of the uninstall. uninstall.exe goes
        /// last: when it is the running image of a waiting parent it is deleted at reboot.
        /// </summary>
        internal static IReadOnlyCollection<string> GetOwnedNamesForUninstall(
            string installDir, string? trustedPayloadZipPath = null)
        {
            var owned = new HashSet<string>(
                RequireUninstallOwnership(installDir, trustedPayloadZipPath), StringComparer.OrdinalIgnoreCase);
            if (File.Exists(Path.Combine(installDir, "uninstall.exe")))
                owned.Add("uninstall.exe");
            return owned;
        }

        /// <summary>Failures and retained (foreign/modified) entries as one list; empty means fully removed.</summary>
        internal static IReadOnlyList<string> DeleteOwnedEntries(
            string installDir, string? trustedPayloadZipPath = null)
        {
            UninstallCleanupResult result = DeleteOwnedEntriesTolerant(installDir, trustedPayloadZipPath);
            return result.Failures
                .Concat(result.Retained.Select(name => "Unverified install entry retained: " + name))
                .ToList();
        }

        internal static UninstallCleanupResult DeleteOwnedEntriesTolerant(
            string installDir, string? trustedPayloadZipPath = null, Func<string, bool>? scheduleDelete = null)
        {
            var result = new UninstallCleanupResult();
            scheduleDelete ??= path => SetupStaging.ScheduleDeleteOnReboot(path, SetupUpdateLog.Warn);
            if (!Directory.Exists(installDir)) return result;
            PayloadInventory? inventory;
            HashSet<string> listed;
            try
            {
                inventory = LoadTrustedPayload(trustedPayloadZipPath);
                listed = new HashSet<string>(
                    RequireUninstallOwnership(installDir, inventory), StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                result.Failures.Add("Install ownership verification failed: " + ex.Message);
                return result;
            }

            string uninstaller = Path.Combine(installDir, "uninstall.exe");
            foreach (string entry in Directory.GetFileSystemEntries(installDir))
            {
                string name = Path.GetFileName(entry);
                if (string.Equals(name, FileName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "uninstall.exe", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!listed.Contains(name))
                {
                    result.Retained.Add(name);
                    continue;
                }
                try
                {
                    // Revalidate immediately before each removal; a directory is always
                    // traversed leaf by leaf and is never recursively deleted by name.
                    ValidateInstallRoot(installDir);
                    DeleteVerifiedEntry(entry, name, inventory, result);
                }
                catch (Exception ex)
                {
                    result.Failures.Add(name + ": " + ex.Message);
                }
            }
            if (result.Failures.Count > 0) return result;

            bool uninstallerPending = false;
            if (File.Exists(uninstaller))
            {
                try
                {
                    if (TryValidateRetainable(uninstaller, "uninstall.exe", inventory))
                    {
                        try { File.Delete(uninstaller); }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        {
                            if (!scheduleDelete(uninstaller))
                                throw;
                            result.PendingReboot.Add(uninstaller);
                            uninstallerPending = true;
                        }
                    }
                    else
                        result.Retained.Add("uninstall.exe");
                }
                catch (Exception ex)
                {
                    result.Failures.Add("uninstall.exe: " + ex.Message);
                    return result;
                }
            }

            // Retained foreign entries are reported, not ours: the marker goes so the
            // remaining directory no longer claims to be a VoltManager installation.
            try
            {
                string marker = Path.Combine(installDir, FileName);
                if (File.Exists(marker))
                    File.Delete(marker);
                int remaining = Directory.GetFileSystemEntries(installDir).Length;
                if (remaining == 0)
                    Directory.Delete(installDir, false);
                else if (uninstallerPending && remaining == 1)
                {
                    if (scheduleDelete(installDir)) result.PendingReboot.Add(installDir);
                    else result.Failures.Add("Install directory could not be scheduled for removal: " + installDir);
                }
            }
            catch (Exception ex)
            {
                result.Failures.Add("Install directory: " + ex.Message);
            }
            return result;
        }

        // True when the entry is verified (delete it), false when it is merely foreign
        // or modified (retain it). Links and unreadable entries throw.
        private static bool TryValidateRetainable(string path, string relative, PayloadInventory? inventory)
        {
            try
            {
                ValidateManagedEntry(path, relative, inventory, requireContentHash: true);
                return true;
            }
            catch (InvalidDataException ex) when (!IsLinkedEntry(ex))
            {
                return false;
            }
        }

        private static void DeleteVerifiedEntry(
            string path, string relative, PayloadInventory? inventory, UninstallCleanupResult result)
        {
            if (Directory.Exists(path))
            {
                if (inventory == null || !inventory.DirectoryPaths.Contains(relative))
                {
                    result.Retained.Add(relative);
                    return;
                }
                ThrowIfReparsePoint(path);
                foreach (string child in Directory.GetFileSystemEntries(path))
                    DeleteVerifiedEntry(child, relative + "/" + Path.GetFileName(child), inventory, result);
                if (Directory.GetFileSystemEntries(path).Length == 0)
                    Directory.Delete(path, recursive: false);
            }
            else if (File.Exists(path))
            {
                if (TryValidateRetainable(path, relative, inventory))
                    File.Delete(path);
                else
                    result.Retained.Add(relative);
            }
            else
                throw new InvalidDataException("Install entry changed during cleanup: " + relative);
        }

        /// <summary>
        /// Removes '.&lt;install&gt;.backup-*' / '.staging-*' leftovers of earlier updates next to
        /// the install dir. Returns the directories retained for manual review.
        /// </summary>
        internal static IReadOnlyList<string> DeleteVerifiedSwapLeftovers(
            string installDir, string? trustedPayloadZipPath = null, bool requireProtectedLocation = true)
        {
            var retained = new List<string>();
            string full = Path.GetFullPath(installDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string? parent = Path.GetDirectoryName(full);
            string name = Path.GetFileName(full);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) return retained;
            // Only an admin-only parent (Program Files) is trusted to hold nothing but our swap dirs.
            if (requireProtectedLocation && !InstallDirectorySecurity.IsUnderProgramFiles(full)) return retained;

            PayloadInventory? inventory;
            try { inventory = LoadTrustedPayload(trustedPayloadZipPath); }
            catch { inventory = null; }

            foreach (string pattern in new[] { "." + name + ".backup-*", "." + name + ".staging-*" })
            {
                foreach (string dir in Directory.GetDirectories(parent, pattern))
                {
                    try
                    {
                        // A familiar folder/name and an admin-owned parent do not prove ownership.
                        // Old releases without a matching trusted inventory stay for manual review.
                        ValidateInstallRoot(dir);
                        if (requireProtectedLocation)
                            InstallDirectorySecurity.RequireProtectedElevatedUninstallerLocation(dir);
                        if (SetupStaging.ContainsReparsePointInTree(dir))
                            throw LinkedEntry("reparse point");
                        ThrowIfLinkedFileInTree(dir);
                        foreach (string entry in Directory.GetFileSystemEntries(dir))
                        {
                            string entryName = Path.GetFileName(entry);
                            if (string.Equals(entryName, FileName, StringComparison.OrdinalIgnoreCase))
                                continue;
                            if (inventory == null)
                                throw new InvalidDataException("no trusted payload inventory");
                            ValidateManagedEntry(entry, entryName, inventory, requireContentHash: true);
                        }
                        foreach (string entry in Directory.GetFileSystemEntries(dir))
                        {
                            string entryName = Path.GetFileName(entry);
                            if (string.Equals(entryName, FileName, StringComparison.OrdinalIgnoreCase)) continue;
                            var cleanup = new UninstallCleanupResult();
                            DeleteVerifiedEntry(entry, entryName, inventory, cleanup);
                            if (cleanup.Retained.Count > 0 || cleanup.Failures.Count > 0)
                                throw new IOException("swap contents changed during cleanup");
                        }
                        string marker = Path.Combine(dir, FileName);
                        if (File.Exists(marker)) File.Delete(marker);
                        Directory.Delete(dir, recursive: false);
                    }
                    catch (Exception ex)
                    {
                        SetupUpdateLog.Warn("Install swap directory retained (" + ex.Message + "): " + dir);
                        retained.Add(dir);
                    }
                }
            }
            return retained;
        }

        // Capture only entries whose contents already passed the installed-version inventory check.
        internal static Dictionary<string, string> SnapshotBackupFiles(string installDir, IEnumerable<string> names)
        {
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in names)
                AddSnapshotFiles(Path.Combine(installDir, name), name, hashes);
            return hashes;
        }

        private static void AddSnapshotFiles(string path, string relative, Dictionary<string, string> hashes)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return;
            ThrowIfReparsePoint(path);
            if (Directory.Exists(path))
            {
                // Directory entries prevent new empty or foreign nested directories being adopted.
                hashes.Add(relative + "/", "");
                foreach (string child in Directory.GetFileSystemEntries(path))
                    AddSnapshotFiles(child, relative + "/" + Path.GetFileName(child), hashes);
            }
            else
            {
                ValidateSingleFile(path);
                using (var stream = File.OpenRead(path))
                using (var sha = SHA256.Create())
                    hashes.Add(relative, BitConverter.ToString(sha.ComputeHash(stream)));
            }
        }

        internal static void ValidateBackupSnapshot(string backupDir, IReadOnlyDictionary<string, string> expected)
        {
            ValidateInstallRoot(backupDir);
            var actual = SnapshotBackupFiles(backupDir, Directory.GetFileSystemEntries(backupDir).Select(Path.GetFileName)!);
            if (actual.Count != expected.Count || actual.Any(entry =>
                    !expected.TryGetValue(entry.Key, out string hash) || hash != entry.Value))
                throw new InvalidDataException("Backup contents changed since ownership verification.");
        }

        internal static bool HasOwnedArtifacts(string installDir)
        {
            if (!Directory.Exists(installDir)) return false;
            string marker = Path.Combine(installDir, FileName);
            if (File.Exists(marker))
                return true;
            foreach (string name in KnownAppEntries)
            {
                string path = Path.Combine(installDir, name);
                if (File.Exists(path) || Directory.Exists(path)) return true;
            }
            return false;
        }

        /// <summary>
        /// Read-only ownership gate for an installer to invoke BEFORE process
        /// termination, legacy migration, backup, registry, or file mutations.
        /// The archive must independently describe the CURRENTLY INSTALLED
        /// version (or a separately authenticated prior release), not merely
        /// the new release about to be extracted. A mutable manifest is only
        /// an index of candidates and is never sufficient by itself.
        /// A missing/empty destination is a new installation.
        /// </summary>
        internal static void ValidateExistingInstallBeforeSideEffects(
            string destDir, string? trustedPayloadZipPath = null, Action<string>? requireProtectedInstallLocation = null)
        {
            ValidateInstallRoot(destDir);
            if (File.Exists(destDir))
                throw new InvalidDataException("Install target is a file, not a directory.");
            // New installations have no previous publisher inventory to prove.
            // Do not require or open an old-version archive until one actually
            // exists; the staged NEW payload is verified by a separate gate.
            if (!Directory.Exists(destDir) ||
                Directory.GetFileSystemEntries(destDir).Length == 0)
                return;
            ValidateExistingInstallBeforeSideEffectsWithInventory(destDir, LoadInstalledInventory(
                destDir, trustedPayloadZipPath, () => LoadTrustedPayload(null), requireProtectedInstallLocation));
        }

        // L'inventario della release installata proviene dal payload del suo uninstaller
        // protetto. Manifest e registro restano indici di candidati, non fonti di fiducia.
        // L'immagine è letta come dati in reflection-only, senza eseguirne il codice.
        private static PayloadInventory? LoadInstalledInventory(
            string installDir, string? trustedInstalledPayloadZipPath,
            Func<PayloadInventory?> sameVersionFallback, Action<string>? requireProtectedInstallLocation)
        {
            if (!string.IsNullOrEmpty(trustedInstalledPayloadZipPath))
                return LoadTrustedPayload(trustedInstalledPayloadZipPath);
            string uninstaller = Path.Combine(installDir, "uninstall.exe");
            if (Directory.Exists(uninstaller))
                throw new InvalidDataException("Installed uninstaller path is a directory.");
            // Senza uninstaller resta ammesso soltanto il confronto same-version.
            if (!File.Exists(uninstaller))
                return sameVersionFallback();

            (requireProtectedInstallLocation ?? InstallDirectorySecurity.RequireProtectedElevatedUninstallerLocation)(installDir);
            ValidateSingleFile(uninstaller);
            // L'handle senza condivisione in scrittura lega hash e payload agli stessi byte.
            using (var image = new FileStream(uninstaller, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                string imageHash;
                using (var sha = SHA256.Create())
                    imageHash = BitConverter.ToString(sha.ComputeHash(image));
                // Stessi byte, stesso inventario: l'update valida più volte senza ricalcolarlo.
                lock (InstalledInventories)
                    if (InstalledInventories.TryGetValue(imageHash, out PayloadInventory cached))
                        return cached;
                PayloadInventory inventory;
                try
                {
                    byte[]? payload = InstallEngine.ReadEmbeddedPayloadWithoutExecution(uninstaller);
                    // Le build di sviluppo senza payload non provano un inventario cross-version.
                    if (payload == null)
                        return sameVersionFallback();
                    using (var zip = new MemoryStream(payload, writable: false))
                        inventory = ReadInventory(zip);
                }
                catch (Exception ex) when (!(ex is InvalidDataException))
                {
                    throw new InvalidDataException("Installed uninstaller cannot provide a trusted payload inventory: " + ex.Message, ex);
                }
                if (inventory.FileHashes.ContainsKey("uninstall.exe"))
                    throw new InvalidDataException("Installed payload inventory must not declare uninstall.exe.");
                // Anche uninstall.exe è verificato contro A, non contro il setup B in esecuzione.
                inventory.FileHashes.Add("uninstall.exe", imageHash);
                lock (InstalledInventories)
                    InstalledInventories[imageHash] = inventory;
                return inventory;
            }
        }

        private static readonly Dictionary<string, PayloadInventory> InstalledInventories =
            new Dictionary<string, PayloadInventory>(StringComparer.Ordinal);

        private static IReadOnlyCollection<string> ValidateExistingInstallBeforeSideEffectsWithInventory(
            string destDir, PayloadInventory? installedInventory)
        {
            ValidateInstallRoot(destDir);
            if (File.Exists(destDir))
                throw new InvalidDataException("Install target is a file, not a directory.");
            if (!Directory.Exists(destDir) ||
                Directory.GetFileSystemEntries(destDir).Length == 0)
                return Array.Empty<string>();

            // A populated location must never be treated as an owned install
            // because it has familiar filenames or a self-written marker.
            // This check also rejects a non-empty foreign directory when no
            // install manifest exists, without modifying its contents.
            IReadOnlyCollection<string> listed = ValidateManifestOwnership(destDir, installedInventory);
            ValidateCandidateFiles(destDir);
            var candidates = new HashSet<string>(listed, StringComparer.OrdinalIgnoreCase);
            bool hasVerifiedPayloadFile = false;
            foreach (string name in listed)
            {
                string path = Path.Combine(destDir, name);
                if (!File.Exists(path) && !Directory.Exists(path))
                    throw new InvalidDataException("Install manifest lists a missing payload entry: " + name);

                ValidateManagedEntry(path, name, installedInventory, requireContentHash: true);
                if (installedInventory != null &&
                    HasVerifiedPayloadFile(path, name, installedInventory))
                    hasVerifiedPayloadFile = true;
            }

            foreach (string entry in Directory.GetFileSystemEntries(destDir))
            {
                string name = Path.GetFileName(entry);
                if (candidates.Contains(name) ||
                    string.Equals(name, FileName, StringComparison.OrdinalIgnoreCase))
                    continue;

                // The installer creates uninstall.exe after writing the manifest.
                // Permit it only when its contents match the currently running
                // installer or an independently trusted archive.
                if (string.Equals(name, "uninstall.exe", StringComparison.OrdinalIgnoreCase))
                {
                    ValidateManagedEntry(entry, name, installedInventory, requireContentHash: true);
                    continue;
                }

                // Unrelated data may remain in the directory as data, but
                // undeclared app-named files (even matching the trusted ZIP)
                // cannot be silently adopted as managed entries.
                if (IsAppNamedEntry(name) ||
                    (installedInventory != null && installedInventory.TopLevelNames.Contains(name)))
                    throw new InvalidDataException("Unlisted app payload entry cannot establish ownership: " + name);
            }

            if (!hasVerifiedPayloadFile)
                throw new InvalidDataException(
                    "No physical content-verified application payload exists; marker-only ownership is insufficient.");
            return listed;
        }

        private static bool HasVerifiedPayloadFile(string path, string relative, PayloadInventory inventory)
        {
            if (File.Exists(path))
                return !string.Equals(relative, "uninstall.exe", StringComparison.OrdinalIgnoreCase) &&
                       inventory.FileHashes.ContainsKey(relative);
            if (!Directory.Exists(path))
                return false;
            foreach (string child in Directory.GetFileSystemEntries(path))
                if (HasVerifiedPayloadFile(child, relative + "/" + Path.GetFileName(child), inventory))
                    return true;
            return false;
        }

        private static bool IsAppNamedEntry(string name)
        {
            if (KnownAppFileNames.Contains(name)) return true;
            if (!name.StartsWith("VoltManager", StringComparison.OrdinalIgnoreCase)) return false;
            string extension = Path.GetExtension(name);
            return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".config", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSafeTopLevelName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (name == "." || name == "..") return false;
            if (name != name!.TrimEnd(' ', '.')) return false;
            if (string.Equals(name, FileName, StringComparison.OrdinalIgnoreCase)) return false;
            if (name!.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            if (!string.Equals(Path.GetFileName(name!), name, StringComparison.Ordinal) ||
                name!.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
                name.IndexOf(Path.AltDirectorySeparatorChar) >= 0) return false;
            string device = name.Split('.')[0];
            if (new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$" }
                .Contains(device, StringComparer.OrdinalIgnoreCase)) return false;
            if (device.Length == 4 && (device.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                device.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                ((device[3] >= '1' && device[3] <= '9') ||
                 device[3] == '\u00B9' || device[3] == '\u00B2' || device[3] == '\u00B3')) return false;
            return true;
        }

        // Legacy JSON can only provide candidate names. The trusted payload, embedded
        // in the running setup or supplied by the verified install workflow, is the
        // independent authority for managed files and their exact contents.
        private static IReadOnlyCollection<string> ValidateManifestOwnership(
            string installDir, PayloadInventory? inventory, bool allowEmptyNewInstall = false)
        {
            if (!File.Exists(Path.Combine(installDir, FileName)))
            {
                if (allowEmptyNewInstall &&
                    (!Directory.Exists(installDir) || Directory.GetFileSystemEntries(installDir).Length == 0))
                    return Array.Empty<string>();
                throw new InvalidDataException("Install manifest is missing; ownership cannot be established.");
            }
            if (inventory == null)
                throw new InvalidDataException("No independent trusted payload inventory is available for this installation.");
            IReadOnlyCollection<string> listed = ReadOwnedEntries(installDir);
            foreach (string name in listed)
                if (!KnownAppFileNames.Contains(name) &&
                    (inventory == null || !inventory.TopLevelNames.Contains(name)))
                    throw new InvalidDataException("Unverifiable install manifest entry: " + name);
            return listed;
        }

        private sealed class PayloadInventory
        {
            internal readonly HashSet<string> TopLevelNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal readonly HashSet<string> DirectoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string, string> FileHashes =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        // Passing null uses the immutable ZIP embedded in the running setup,
        // never a mutable copy extracted into a shared TEMP folder.
        internal static void ValidateStagedPayload(string stagingDir, string? trustedPayloadZipPath = null)
        {
            PayloadInventory inventory = LoadTrustedPayload(trustedPayloadZipPath)
                ?? throw new InvalidDataException("Staged payload has no independent trusted ZIP inventory.");
            ValidateInstallRoot(stagingDir);
            if (!Directory.Exists(stagingDir))
                throw new InvalidDataException("Staging directory is missing.");

            var observedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            VerifyStagedContents(stagingDir, "", inventory, observedFiles);
            if (!observedFiles.SetEquals(inventory.FileHashes.Keys))
                throw new InvalidDataException("Staged payload is missing files declared by its ZIP inventory.");
            foreach (string directory in inventory.DirectoryPaths)
            {
                string path = Path.Combine(stagingDir, directory.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(path))
                    throw new InvalidDataException("Staged payload is missing a declared directory: " + directory);
                ThrowIfReparsePoint(path);
            }
        }

        private static void VerifyStagedContents(
            string directory, string relative, PayloadInventory inventory, HashSet<string> observedFiles)
        {
            ThrowIfReparsePoint(directory);
            foreach (string child in Directory.GetFileSystemEntries(directory))
            {
                string name = Path.GetFileName(child);
                if (!IsSafeTopLevelName(name))
                    throw new InvalidDataException("Unsafe staged payload entry: " + name);
                string childRelative = relative.Length == 0 ? name : relative + "/" + name;
                if (Directory.Exists(child))
                {
                    if (!inventory.DirectoryPaths.Contains(childRelative))
                        throw new InvalidDataException("Unlisted staged directory: " + childRelative);
                    VerifyStagedContents(child, childRelative, inventory, observedFiles);
                }
                else if (File.Exists(child))
                {
                    if (!inventory.FileHashes.ContainsKey(childRelative))
                        throw new InvalidDataException("Unlisted staged file: " + childRelative);
                    ValidateManagedEntry(child, childRelative, inventory, requireContentHash: true);
                    observedFiles.Add(childRelative);
                }
                else
                    throw new InvalidDataException("Unverifiable staged payload entry: " + childRelative);
            }
        }

        private static PayloadInventory? LoadTrustedPayload(string? trustedPayloadZipPath)
        {
            if (string.IsNullOrEmpty(trustedPayloadZipPath))
                return EmbeddedInventory.Value;
            // The caller must supply an independently verified, immutable payload
            // source. Never derive this path from the install manifest or directory.
            using (Stream source = File.OpenRead(trustedPayloadZipPath))
                return ReadInventory(source);
        }

        // La risorsa incorporata è immutabile: un solo inflate+hash per processo.
        private static readonly Lazy<PayloadInventory?> EmbeddedInventory = new Lazy<PayloadInventory?>(() =>
        {
            Assembly setup = Assembly.GetExecutingAssembly();
            string? resource = setup.GetManifestResourceNames().FirstOrDefault(
                name => name.EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase));
            if (resource == null) return null; // Development/legacy builds.
            using (Stream source = setup.GetManifestResourceStream(resource)
                ?? throw new InvalidDataException("Embedded trusted payload is unavailable."))
                return ReadInventory(source);
        });

        private static PayloadInventory ReadInventory(Stream source)
        {
            using (var zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true))
            {
                var inventory = new PayloadInventory();
                var explicitlyListedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                long totalLength = 0;
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    if (entry.FullName.IndexOf('\\') >= 0)
                        throw new InvalidDataException("Trusted payload contains an invalid path separator.");
                    string relative = entry.FullName.TrimEnd('/');
                    string[] components = relative.Split('/');
                    if (relative.Length == 0 || components.Any(part => !IsSafeTopLevelName(part)))
                        throw new InvalidDataException("Trusted payload contains an unsafe entry: " + entry.FullName);
                    inventory.TopLevelNames.Add(components[0]);
                    for (int index = 1; index < components.Length; ++index)
                        inventory.DirectoryPaths.Add(string.Join("/", components.Take(index)));

                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
                    {
                        if (!explicitlyListedDirectories.Add(relative))
                            throw new InvalidDataException("Duplicate trusted payload directory entry: " + relative);
                        inventory.DirectoryPaths.Add(relative);
                        continue;
                    }

                    totalLength = checked(totalLength + entry.Length);
                    if (entry.Length > 1_073_741_824L || totalLength > 2_147_483_648L ||
                        inventory.FileHashes.Count >= 100_000)
                        throw new InvalidDataException("Trusted payload inventory exceeds safety limits.");
                    using (Stream stream = entry.Open())
                    using (var sha = SHA256.Create())
                    {
                        if (inventory.FileHashes.ContainsKey(relative))
                            throw new InvalidDataException("Duplicate trusted payload entry: " + relative);
                        inventory.FileHashes.Add(relative, BitConverter.ToString(sha.ComputeHash(stream)));
                    }
                }
                foreach (string directory in inventory.DirectoryPaths)
                    if (inventory.FileHashes.ContainsKey(directory))
                        throw new InvalidDataException("Trusted payload contains a directory/file collision: " + directory);
                return inventory;
            }
        }

        private static void ValidateManagedEntries(
            string installDir, IEnumerable<string> owned, PayloadInventory? inventory,
            bool requireContentHash = true)
        {
            foreach (string name in owned)
                ValidateManagedEntry(Path.Combine(installDir, name), name, inventory, requireContentHash);
        }

        private static void ValidateManagedEntry(
            string path, string relative, PayloadInventory? inventory, bool requireContentHash)
        {
            if (Directory.Exists(path))
            {
                if (inventory == null || !inventory.DirectoryPaths.Contains(relative))
                    throw new InvalidDataException("Unverified directory in install tree: " + relative);
                ThrowIfReparsePoint(path);
                foreach (string child in Directory.GetFileSystemEntries(path))
                {
                    string childName = Path.GetFileName(child);
                    if (!IsSafeTopLevelName(childName))
                        throw new InvalidDataException("Unsafe nested install entry: " + childName);
                    ValidateManagedEntry(child, relative + "/" + childName, inventory, requireContentHash);
                }
            }
            else if (File.Exists(path))
            {
                ValidateSingleFile(path);
                if (inventory != null && inventory.FileHashes.TryGetValue(relative, out string expected))
                {
                    if (requireContentHash)
                    {
                        using (var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                        using (var sha = SHA256.Create())
                            if (!string.Equals(expected, BitConverter.ToString(sha.ComputeHash(file)),
                                StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException("Installed payload differs from trusted package: " + relative);
                    }
                }
                else if (inventory != null &&
                    string.Equals(relative, "uninstall.exe", StringComparison.OrdinalIgnoreCase))
                {
                    if (requireContentHash)
                    {
                        string runningSetup = Assembly.GetExecutingAssembly().Location;
                        using (var actual = File.OpenRead(path))
                        using (var executing = File.OpenRead(runningSetup))
                        using (var sha = SHA256.Create())
                        {
                            byte[] actualHash = sha.ComputeHash(actual);
                            byte[] executingHash = sha.ComputeHash(executing);
                            if (!actualHash.SequenceEqual(executingHash))
                                throw new InvalidDataException("Installed uninstaller differs from executing setup.");
                        }
                    }
                }
                else if (inventory != null || !KnownAppFileNames.Contains(relative))
                    throw new InvalidDataException("Unverified file in install tree: " + relative);
            }
        }

        private static void ValidateSingleFile(string path)
        {
            ThrowIfReparsePoint(path);
            using (var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (!GetFileInformationByHandle(file.SafeFileHandle, out ByHandleFileInformation info))
                    throw LinkedEntry("Cannot verify file link count: " + path);
                if (info.NumberOfLinks != 1)
                    throw LinkedEntry("Hardlinked file cannot establish install ownership: " + path);
            }
        }

        // Links (junction, symlink, hardlink) always abort uninstall; they are never "foreign data".
        private const string LinkedEntryKey = "VoltManager.LinkedEntry";

        private static InvalidDataException LinkedEntry(string message)
        {
            var exception = new InvalidDataException(message);
            exception.Data[LinkedEntryKey] = true;
            return exception;
        }

        private static bool IsLinkedEntry(Exception exception) => exception.Data.Contains(LinkedEntryKey);

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            internal uint Attributes;
            internal FileTime CreationTime;
            internal FileTime LastAccessTime;
            internal FileTime LastWriteTime;
            internal uint VolumeSerialNumber;
            internal uint FileSizeHigh;
            internal uint FileSizeLow;
            internal uint NumberOfLinks;
            internal uint FileIndexHigh;
            internal uint FileIndexLow;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileTime
        {
            internal uint Low;
            internal uint High;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle handle, out ByHandleFileInformation information);

        private static void ValidateInstallRoot(string installDir)
        {
            string? current = Path.GetFullPath(installDir);
            while (!string.IsNullOrWhiteSpace(current))
            {
                if (Directory.Exists(current)) ThrowIfReparsePoint(current);
                string? parent = Path.GetDirectoryName(current);
                if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
                current = parent;
            }
        }

        private static void ValidateCandidateFiles(string installDir)
        {
            foreach (string name in KnownAppEntries)
            {
                string path = Path.Combine(installDir, name);
                if (Directory.Exists(path))
                    throw new InvalidDataException("App file path is a directory: " + name);
                if (File.Exists(path)) ValidateSingleFile(path);
            }
            string marker = Path.Combine(installDir, FileName);
            if (File.Exists(marker)) ValidateSingleFile(marker);
        }

        private static void ThrowIfReparsePoint(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw LinkedEntry("Reparse point cannot establish install ownership: " + path);
        }
    }

    internal sealed class UninstallCleanupResult
    {
        public List<string> Failures { get; } = new List<string>();
        // Foreign or modified entries left in place on purpose (relative names).
        public List<string> Retained { get; } = new List<string>();
        // Absolute paths scheduled for deletion at the next restart.
        public List<string> PendingReboot { get; } = new List<string>();
    }
}
