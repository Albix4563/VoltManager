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

        internal static IReadOnlyCollection<string> GetOwnedNamesForUninstall(
            string installDir, string? trustedPayloadZipPath = null)
            => GetOwnedNamesForUninstallWithInventory(installDir, LoadTrustedPayload(trustedPayloadZipPath));

        private static IReadOnlyCollection<string> GetOwnedNamesForUninstallWithInventory(
            string installDir, PayloadInventory? inventory)
        {
            ValidateInstallRoot(installDir);
            IReadOnlyCollection<string> listed = ValidateManifestOwnership(installDir, inventory);
            ValidateCandidateFiles(installDir);
            var owned = new HashSet<string>(listed, StringComparer.OrdinalIgnoreCase);
            if (File.Exists(Path.Combine(installDir, "uninstall.exe")))
                owned.Add("uninstall.exe");
            foreach (string entry in Directory.GetFileSystemEntries(installDir))
            {
                string name = Path.GetFileName(entry);
                if (!owned.Contains(name) &&
                    !string.Equals(name, FileName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Unverifiable residual install entry: " + name);
            }
            ValidateManagedEntries(installDir, owned, inventory);
            return owned;
        }

        internal static IReadOnlyList<string> DeleteOwnedEntries(
            string installDir, string? trustedPayloadZipPath = null)
        {
            var failures = new List<string>();
            if (!Directory.Exists(installDir)) return failures;
            IReadOnlyCollection<string> names;
            PayloadInventory? inventory;
            try
            {
                inventory = LoadTrustedPayload(trustedPayloadZipPath);
                names = GetOwnedNamesForUninstallWithInventory(installDir, inventory);
            }
            catch (Exception ex)
            {
                failures.Add("Install ownership verification failed: " + ex.Message);
                return failures;
            }
            foreach (string name in names)
            {
                string path = Path.Combine(installDir, name);
                try
                {
                    // Revalidate immediately before each removal; a directory is always
                    // traversed leaf by leaf and is never recursively deleted by name.
                    ValidateInstallRoot(installDir);
                    ValidateManagedEntry(path, name, inventory, requireContentHash: true);
                    if (File.Exists(path)) File.Delete(path);
                    else if (Directory.Exists(path)) DeleteVerifiedDirectory(path, name, inventory);
                }
                catch (Exception ex)
                {
                    failures.Add(name + ": " + ex.Message);
                }
            }

            if (failures.Count == 0)
            {
                try
                {
                    string marker = Path.Combine(installDir, FileName);
                    if (Directory.GetFileSystemEntries(installDir).Any(entry =>
                        !string.Equals(Path.GetFileName(entry), FileName, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("Install directory still contains entries after verified cleanup.");
                    if (File.Exists(marker))
                        File.Delete(marker);
                }
                catch (Exception ex)
                {
                    failures.Add(FileName + ": " + ex.Message);
                }
            }

            if (failures.Count == 0)
            {
                try
                {
                    if (Directory.Exists(installDir) && Directory.GetFileSystemEntries(installDir).Length == 0)
                        Directory.Delete(installDir, false);
                }
                catch (Exception ex)
                {
                    failures.Add("Install directory: " + ex.Message);
                }
            }
            return failures;
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
            byte[] image = File.ReadAllBytes(uninstaller);
            PayloadInventory inventory;
            try
            {
                byte[]? payload = InstallEngine.ReadEmbeddedPayloadWithoutExecution(image);
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
            using (var sha = SHA256.Create())
                inventory.FileHashes.Add("uninstall.exe", BitConverter.ToString(sha.ComputeHash(image)));
            return inventory;
        }

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
            Stream? source;
            if (!string.IsNullOrEmpty(trustedPayloadZipPath))
            {
                // The caller must supply an independently verified, immutable payload
                // source. Never derive this path from the install manifest or directory.
                source = File.OpenRead(trustedPayloadZipPath);
            }
            else
            {
                Assembly setup = Assembly.GetExecutingAssembly();
                string? resource = setup.GetManifestResourceNames().FirstOrDefault(
                    name => name.EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase));
                if (resource == null) return null; // Development/legacy builds.
                source = setup.GetManifestResourceStream(resource)
                    ?? throw new InvalidDataException("Embedded trusted payload is unavailable.");
            }

            using (source)
                return ReadInventory(source);
        }

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

        private static void DeleteVerifiedDirectory(string path, string relative, PayloadInventory? inventory)
        {
            if (inventory == null || !inventory.DirectoryPaths.Contains(relative))
                throw new InvalidDataException("Unverified directory during cleanup: " + relative);
            ThrowIfReparsePoint(path);
            foreach (string child in Directory.GetFileSystemEntries(path))
            {
                string childRelative = relative + "/" + Path.GetFileName(child);
                if (Directory.Exists(child)) DeleteVerifiedDirectory(child, childRelative, inventory);
                else if (File.Exists(child))
                {
                    ValidateManagedEntry(child, childRelative, inventory, requireContentHash: true);
                    File.Delete(child);
                }
                else throw new InvalidDataException("Install entry changed during cleanup: " + childRelative);
            }
            Directory.Delete(path, recursive: false);
        }

        private static void ValidateSingleFile(string path)
        {
            ThrowIfReparsePoint(path);
            using (var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (!GetFileInformationByHandle(file.SafeFileHandle, out ByHandleFileInformation info))
                    throw new InvalidDataException("Cannot verify file link count: " + path);
                if (info.NumberOfLinks != 1)
                    throw new InvalidDataException("Hardlinked file cannot establish install ownership: " + path);
            }
        }

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
                throw new InvalidDataException("Reparse point cannot establish install ownership: " + path);
        }
    }
}
