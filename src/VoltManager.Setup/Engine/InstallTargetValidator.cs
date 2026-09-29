using System;
using System.Collections.Generic;
using System.IO;

namespace VoltManager.Setup.Engine
{
    internal sealed class InstallTargetValidationResult
    {
        internal InstallTargetValidationResult(bool ok, string reason, string? suggestedPath)
        {
            Ok = ok;
            Reason = reason ?? "";
            SuggestedPath = suggestedPath;
        }

        public bool Ok { get; }
        public string Reason { get; }
        public string? SuggestedPath { get; }
    }

    internal static class InstallTargetValidator
    {
        private const string MarkerFileName = ".voltmanager-install";
        private const string AppExeName = "VoltManager.exe";

        public static InstallTargetValidationResult ValidateInstallTarget(string? path)
            => Validate(path, requireOwnedInstall: false, allowSuggestion: true);

        public static InstallTargetValidationResult ValidateUninstallTarget(string? path)
            => Validate(path, requireOwnedInstall: true, allowSuggestion: false);

        private static InstallTargetValidationResult Validate(string? path, bool requireOwnedInstall, bool allowSuggestion)
        {
            if (!TryNormalize(path, out string fullPath))
                return new InstallTargetValidationResult(false, "The selected path is invalid.", null);

            string? protectedReason = GetProtectedPathReason(fullPath);
            if (protectedReason != null)
                return Reject(fullPath, protectedReason, allowSuggestion);

            if (!Directory.Exists(fullPath))
            {
                if (requireOwnedInstall)
                    return new InstallTargetValidationResult(false, "The uninstall target does not exist.", null);
                return new InstallTargetValidationResult(true, "", null);
            }

            bool hasExe = File.Exists(Path.Combine(fullPath, AppExeName));
            bool hasMarker = File.Exists(Path.Combine(fullPath, MarkerFileName));
            if (requireOwnedInstall)
            {
                return hasExe || hasMarker
                    ? new InstallTargetValidationResult(true, "", null)
                    : new InstallTargetValidationResult(false, "The uninstall target is not a VoltManager installation.", null);
            }

            try
            {
                if (Directory.GetFileSystemEntries(fullPath).Length == 0 || hasExe || hasMarker)
                    return new InstallTargetValidationResult(true, "", null);
            }
            catch (Exception ex)
            {
                return Reject(fullPath, "The selected folder cannot be inspected safely: " + ex.Message, allowSuggestion);
            }

            return Reject(fullPath, "The selected folder contains files that are not owned by VoltManager.", allowSuggestion);
        }

        private static InstallTargetValidationResult Reject(string fullPath, string reason, bool allowSuggestion)
        {
            string? suggested = null;
            if (allowSuggestion)
            {
                try
                {
                    string candidate = Normalize(Path.Combine(fullPath, "VoltManager"));
                    InstallTargetValidationResult candidateResult = Validate(candidate, requireOwnedInstall: false, allowSuggestion: false);
                    if (candidateResult.Ok)
                        suggested = candidate;
                }
                catch
                {
                    suggested = null;
                }
            }
            return new InstallTargetValidationResult(false, reason, suggested);
        }

        private static string? GetProtectedPathReason(string fullPath)
        {
            string root = NormalizeRoot(Path.GetPathRoot(fullPath));
            if (!string.IsNullOrEmpty(root) && PathEquals(fullPath, root))
                return "Drive roots and network share roots cannot be used as an install target.";

            string windows = GetFolder(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(windows) && IsSameOrUnder(fullPath, windows))
                return "The Windows directory and its subfolders cannot be used as an install target.";

            foreach (string protectedExact in GetProtectedExactPaths())
            {
                if (PathEquals(fullPath, protectedExact))
                    return "This Windows or user profile folder cannot be used directly as an install target.";
            }
            return null;
        }

        private static IEnumerable<string> GetProtectedExactPaths()
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Add(paths, GetFolder(Environment.SpecialFolder.ProgramFiles));
            Add(paths, GetFolder(Environment.SpecialFolder.ProgramFilesX86));
            Add(paths, GetFolder(Environment.SpecialFolder.CommonApplicationData));

            string profile = GetFolder(Environment.SpecialFolder.UserProfile);
            Add(paths, profile);
            Add(paths, GetFolder(Environment.SpecialFolder.DesktopDirectory));
            Add(paths, GetFolder(Environment.SpecialFolder.MyDocuments));
            Add(paths, GetFolder(Environment.SpecialFolder.MyPictures));
            Add(paths, GetFolder(Environment.SpecialFolder.MyMusic));
            Add(paths, GetFolder(Environment.SpecialFolder.MyVideos));
            Add(paths, GetFolder(Environment.SpecialFolder.ApplicationData));
            Add(paths, GetFolder(Environment.SpecialFolder.LocalApplicationData));
            if (!string.IsNullOrEmpty(profile))
            {
                Add(paths, Path.Combine(profile, "Downloads"));
                Add(paths, Path.Combine(profile, "AppData"));
                Add(paths, Path.Combine(profile, "AppData", "Local"));
                Add(paths, Path.Combine(profile, "AppData", "Roaming"));
            }

            Add(paths, Path.GetTempPath());
            string systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            Add(paths, Path.Combine(systemRoot, "Users"));
            return paths;
        }

        private static void Add(ISet<string> paths, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            try { paths.Add(Normalize(value!)); } catch { /* best-effort: malformed optional path candidates are skipped. */ }
        }

        private static string GetFolder(Environment.SpecialFolder folder)
        {
            string value = Environment.GetFolderPath(folder);
            return string.IsNullOrWhiteSpace(value) ? "" : Normalize(value);
        }

        internal static string Normalize(string path)
        {
            string full = Path.GetFullPath(path.Trim());
            string root = Path.GetPathRoot(full) ?? "";
            if (!string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
                full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return full;
        }

        private static bool TryNormalize(string? path, out string fullPath)
        {
            fullPath = "";
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                fullPath = Normalize(path!);
                return !string.IsNullOrWhiteSpace(fullPath);
            }
            catch
            {
                return false;
            }
        }

        private static string NormalizeRoot(string? root)
        {
            if (string.IsNullOrWhiteSpace(root)) return "";
            return Path.GetFullPath(root!);
        }

        private static bool PathEquals(string left, string right)
            => string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

        private static bool IsSameOrUnder(string path, string directory)
        {
            string fullPath = Normalize(path);
            string fullDirectory = Normalize(directory);
            if (string.Equals(fullPath, fullDirectory, StringComparison.OrdinalIgnoreCase)) return true;
            return fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
    }
}
