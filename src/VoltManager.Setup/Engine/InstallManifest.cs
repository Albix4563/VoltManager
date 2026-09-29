using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        internal static IReadOnlyCollection<string> ReadOwnedEntries(string installDir)
        {
            string marker = Path.Combine(installDir, FileName);
            if (!File.Exists(marker)) return Array.Empty<string>();
            try
            {
                var root = SettingsJsonUpdater.CreateSerializer().DeserializeObject(File.ReadAllText(marker)) as Dictionary<string, object?>;
                if (root == null || !root.TryGetValue("entries", out object? value) || value is not object[] entries)
                    return Array.Empty<string>();
                return entries
                    .Select(item => item as string)
                    .Where(IsSafeTopLevelName)
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception ex)
            {
                SetupUpdateLog.Warn("Could not read install manifest: " + ex.Message);
                return Array.Empty<string>();
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

        internal static HashSet<string> GetOwnedNamesForReplacement(string destDir, IEnumerable<string> stagedNames)
        {
            var owned = new HashSet<string>(ReadOwnedEntries(destDir), StringComparer.OrdinalIgnoreCase);
            foreach (string name in stagedNames.Where(IsSafeTopLevelName)) owned.Add(name);
            owned.Add(FileName);
            return owned;
        }

        internal static IReadOnlyCollection<string> GetOwnedNamesForUninstall(string installDir)
        {
            var owned = new HashSet<string>(ReadOwnedEntries(installDir), StringComparer.OrdinalIgnoreCase);
            foreach (string name in KnownAppEntries) owned.Add(name);
            return owned;
        }

        internal static IReadOnlyList<string> DeleteOwnedEntries(string installDir)
        {
            var failures = new List<string>();
            if (!Directory.Exists(installDir)) return failures;
            foreach (string name in GetOwnedNamesForUninstall(installDir))
            {
                string path = Path.Combine(installDir, name);
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    else if (Directory.Exists(path)) Directory.Delete(path, true);
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

        private static bool IsSafeTopLevelName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (name == "." || name == "..") return false;
            if (name!.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            return string.Equals(Path.GetFileName(name!), name, StringComparison.Ordinal) &&
                   name!.IndexOf(Path.DirectorySeparatorChar) < 0 &&
                   name.IndexOf(Path.AltDirectorySeparatorChar) < 0;
        }
    }
}
