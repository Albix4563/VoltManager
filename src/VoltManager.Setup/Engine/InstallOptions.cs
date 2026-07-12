using System;
using System.Collections.Generic;
using System.IO;

namespace VoltManager.Setup.Engine
{
    public class InstallOptions
    {
        public string InstallDir { get; set; } = GetDefaultInstallDir();
        public bool CreateDesktopShortcut { get; set; } = true;
        public bool StartWithWindows { get; set; } = false;
        public bool EnableWidgets { get; set; } = false;
        // Empty until the user picks in OptionsPage — do not auto-start all widgets.
        public HashSet<string> EnabledWidgetTypes { get; set; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool LaunchAfterInstall { get; set; } = true;

        /// <summary>
        /// Default install location shown in the options page and used when the
        /// path field is left blank: %ProgramFiles%\VoltManager.
        /// </summary>
        public static string GetDefaultInstallDir()
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (string.IsNullOrWhiteSpace(pf))
                pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (string.IsNullOrWhiteSpace(pf))
                pf = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "Program Files");
            return Path.GetFullPath(Path.Combine(pf.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), "VoltManager"));
        }

        /// <summary>
        /// Returns a usable install directory: trims input and falls back to the default when empty.
        /// </summary>
        public static string NormalizeInstallDir(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return GetDefaultInstallDir();
            try
            {
                string trimmed = path!.Trim();
                string full = Path.GetFullPath(trimmed);
                // Drop trailing separators except for drive roots (e.g. "C:\").
                string root = Path.GetPathRoot(full) ?? "";
                if (!string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
                    full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return full;
            }
            catch
            {
                return GetDefaultInstallDir();
            }
        }
    }
}
