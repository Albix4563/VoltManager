using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VoltManager.Setup.Engine
{
    internal static class UninstallAppData
    {
        /// <summary>
        /// Deletes every top-level AppData entry except <paramref name="keep"/>. Never follows
        /// reparse points (InstallEngine.TryDeleteDirectoryTree is junction-safe). An entry that
        /// stays locked is scheduled for deletion at reboot. Returns the kept names present.
        /// </summary>
        internal static IReadOnlyList<string> DeleteEntries(
            string appData, IReadOnlyCollection<string> keep, UninstallResult result,
            Func<string, bool>? scheduleDelete = null)
        {
            scheduleDelete ??= path => SetupStaging.ScheduleDeleteOnReboot(path, SetupUpdateLog.Warn);
            var kept = new List<string>();
            if (!Directory.Exists(appData)) return kept;
            if (HasLinkedAncestor(appData))
            {
                result.Add("AppData: linked directory or parent retained: " + appData);
                return kept;
            }

            var keepSet = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
            foreach (string entry in Directory.GetFileSystemEntries(appData))
            {
                string name = Path.GetFileName(entry);
                if (keepSet.Contains(name))
                {
                    kept.Add(name);
                    continue;
                }
                if (TryDelete(entry, out string error)) continue;
                if (ScheduleAtReboot(entry, scheduleDelete))
                {
                    result.PendingReboot.Add(entry);
                    result.AddWarning(string.Format(I18n.T("uninst_reboot"), entry));
                }
                else
                    result.Add("AppData: " + name + ": " + error);
            }

            if (kept.Count == 0)
            {
                string[] remaining = Directory.GetFileSystemEntries(appData);
                if (remaining.Length == 0)
                {
                    try { Directory.Delete(appData, recursive: false); }
                    catch (Exception ex) { result.Add("AppData: " + ex.Message); }
                }
                else if (remaining.All(entry => result.PendingReboot.Contains(entry)))
                {
                    if (scheduleDelete(appData)) result.PendingReboot.Add(appData);
                    else result.Add("AppData directory could not be scheduled for removal: " + appData);
                }
            }
            return kept;
        }

        private static bool TryDelete(string entry, out string error)
        {
            error = "";
            try
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    // A link is removed as a link: its target is never traversed.
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        Directory.Delete(entry, recursive: false);
                    else
                    {
                        InstallManifest.ThrowIfLinkedFileInTree(entry);
                        return InstallEngine.TryDeleteDirectoryTree(entry, out error);
                    }
                }
                else
                {
                    if ((attributes & FileAttributes.ReparsePoint) == 0)
                        InstallManifest.ThrowIfLinkedFileInTree(entry);
                    if ((attributes & (FileAttributes.ReadOnly | FileAttributes.ReparsePoint)) == FileAttributes.ReadOnly)
                        File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                    File.Delete(entry);
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        internal static bool HasLinkedAncestor(string path)
        {
            string? current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                if (Directory.Exists(current) && SetupStaging.IsReparsePoint(current)) return true;
                current = Path.GetDirectoryName(current);
            }
            return false;
        }

        private static bool ScheduleAtReboot(string entry, Func<string, bool> scheduleDelete)
        {
            try
            {
                if (HasLinkedAncestor(entry)) return false;
                if (File.Exists(entry))
                    return scheduleDelete(entry);
                if (!Directory.Exists(entry)) return true;
                if (SetupStaging.ContainsReparsePointInTree(entry)) return false;
                bool success = true;
                foreach (string file in Directory.GetFiles(entry, "*", SearchOption.AllDirectories))
                    success &= scheduleDelete(file);
                foreach (string dir in Directory.GetDirectories(entry, "*", SearchOption.AllDirectories)
                             .OrderByDescending(path => path.Length))
                    success &= scheduleDelete(dir);
                return scheduleDelete(entry) && success;
            }
            catch (Exception ex)
            {
                SetupUpdateLog.Warn("Cannot schedule AppData removal: " + ex.Message);
                return false;
            }
        }
    }

    internal static class PowerPlanRestore
    {
        // Windows' own schemes are never "VoltManager plans", even if mapped in settings.
        private static readonly HashSet<string> CanonicalPlanGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a1841308-3541-4fab-bc81-f71556f20b4a",
            "381b4222-f694-41f0-9685-ff5bb260df2e",
            "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
            "e9a42b02-d5df-448d-aa00-03f14749eb61",
        };

        private static readonly Regex GuidRegex = new Regex(
            "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");

        internal static string? ParseActiveGuid(string powercfgOutput)
        {
            Match match = GuidRegex.Match(powercfgOutput ?? "");
            return match.Success ? match.Value.ToLowerInvariant() : null;
        }

        internal static bool ShouldRestore(string? activeGuid, IReadOnlyCollection<string> managedGuids)
            => !string.IsNullOrEmpty(activeGuid) &&
               !CanonicalPlanGuids.Contains(activeGuid!) &&
               managedGuids.Contains(activeGuid!, StringComparer.OrdinalIgnoreCase);

        internal static IReadOnlyCollection<string> ReadManagedPlanGuids(string appData)
        {
            try
            {
                string path = Path.Combine(appData, "settings.json");
                if (!File.Exists(path)) return Array.Empty<string>();
                Dictionary<string, object?> root = SettingsJsonUpdater.ParseRootOrEmpty(File.ReadAllText(path), out _);
                if (!root.TryGetValue("planGuidMap", out object? map) || !(map is Dictionary<string, object?> plans))
                    return Array.Empty<string>();
                return plans.Values.OfType<string>().Where(value => GuidRegex.IsMatch(value)).ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }

    /// <summary>
    /// The uninstaller runs elevated; with over-the-shoulder elevation its own profile is the
    /// administrator's, not the signed-in user's. Resolve the desktop owner via explorer.exe.
    /// </summary>
    internal static class InteractiveUserProfile
    {
        internal static string ResolveAppDataDirectory(string elevatedAppData, out bool differs)
        {
            differs = false;
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VOLTMANAGER_VALIDATION_ROOT")))
                return elevatedAppData;
            try
            {
                string? interactive = FindInteractiveUserSid();
                string? current;
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                    current = identity.User?.Value;
                if (!IsDifferentUser(interactive, current)) return elevatedAppData;
                differs = true;
                string? profile = Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + interactive,
                    "ProfileImagePath", null) as string;
                if (string.IsNullOrWhiteSpace(profile))
                    throw new InvalidOperationException("Cannot resolve the signed-in user's profile; administrator AppData was not touched.");
                // ponytail: assumes the default Roaming location; folder redirection is not resolved.
                return ComposeAppData(Environment.ExpandEnvironmentVariables(profile!));
            }
            catch (Exception ex) when (!differs && (ex is Win32Exception || ex is InvalidOperationException ||
                                       ex is UnauthorizedAccessException || ex is System.Security.SecurityException))
            {
                return elevatedAppData;
            }
        }

        internal static bool IsDifferentUser(string? interactiveSid, string? currentSid)
            => !string.IsNullOrWhiteSpace(interactiveSid) && !string.IsNullOrWhiteSpace(currentSid) &&
               !string.Equals(interactiveSid, currentSid, StringComparison.OrdinalIgnoreCase);

        internal static string ComposeAppData(string profileDirectory)
            => Path.Combine(profileDirectory, "AppData", "Roaming", "VoltManager");

        private static string? FindInteractiveUserSid()
        {
            int session = Process.GetCurrentProcess().SessionId;
            foreach (Process explorer in Process.GetProcessesByName("explorer"))
            {
                using (explorer)
                {
                    if (explorer.SessionId != session) continue;
                    string? sid = TryGetProcessUserSid(explorer);
                    if (sid != null) return sid;
                }
            }
            return null;
        }

        private static string? TryGetProcessUserSid(Process process)
        {
            IntPtr token = IntPtr.Zero;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(process.Handle, TokenQuery, out token)) return null;
                GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out int length);
                if (length <= 0) return null;
                buffer = Marshal.AllocHGlobal(length);
                if (!GetTokenInformation(token, TokenUser, buffer, length, out _)) return null;
                IntPtr sid = Marshal.ReadIntPtr(buffer);
                return new SecurityIdentifier(sid).Value;
            }
            catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException)
            {
                return null;
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                if (token != IntPtr.Zero) CloseHandle(token);
            }
        }

        private const uint TokenQuery = 0x0008;
        private const int TokenUser = 1;

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTokenInformation(
            IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
