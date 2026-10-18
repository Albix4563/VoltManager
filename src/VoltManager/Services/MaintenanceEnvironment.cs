#pragma warning disable CA1416
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using VoltManager.Models;

namespace VoltManager.Services;

internal sealed record RawUninstallRecord(
    string Hive,
    string View,
    string SubKeyName,
    string DisplayName,
    string? DisplayVersion,
    string? Publisher,
    string? InstallLocation,
    string? UninstallString,
    string? QuietUninstallString,
    long? EstimatedSizeKb,
    int SystemComponent,
    string? ParentKeyName,
    int WindowsInstaller,
    string? PackageFullName
);

internal sealed record RawRegistryValueRecord(
    string Hive,
    string View,
    string SubKey,
    string ValueName,
    string ValueKind,
    object? ValueData,
    string? ExtractedExecutablePath
);

internal sealed record RawAppPathRecord(
    string Hive,
    string View,
    string SubKey,
    string? DefaultValue,
    string? PathValue
);

internal sealed record ProcessExecutionResult(
    int ExitCode,
    bool Completed,
    string? ErrorMessage
);

internal interface IMaintenanceEnvironment
{
    IReadOnlyList<RawUninstallRecord> GetUninstallRecords();
    bool IsUninstallRegistrationPresent(string hive, string view, string subKeyName);
    IReadOnlyList<RawRegistryValueRecord> GetStartupRunValues();
    IReadOnlyList<RawAppPathRecord> GetAppPathsRecords();
    bool DeleteRegistryValue(string hive, string view, string subKey, string valueName);
    bool DeleteRegistryValueIfUnchanged(RawRegistryValueRecord expected);
    bool SetRegistryValue(string hive, string view, string subKey, string valueName, string valueKind, string valueDataBase64);
    bool TrySetRegistryValueIfAbsent(string hive, string view, string subKey, string valueName, string valueKind, string valueDataBase64);
    RawRegistryValueRecord? ReadRegistryValue(string hive, string view, string subKey, string valueName);
    bool CheckFileIsTrulyMissing(string rawPath, out string normalizedPath, out string reason);
    IReadOnlyList<string> FindLeftoverFiles(string installLocation, int maxResults);
    bool IsProtectedSystemPath(string path);
    bool IsSafeLocalPath(string path);
    void SecureBackupDirectory(string path);
    bool ContainsUserData(string path);
    Task<ProcessExecutionResult> RunUninstallerAsync(string fileName, string arguments, CancellationToken cancellationToken);
    bool OpenAppsSettings();

    string ReadAllText(string path);
    void WriteAllText(string path, string text);
    bool FileExists(string path);
    bool DirectoryExists(string path);
    void CreateDirectory(string path);
    void DeleteDirectory(string path, bool recursive);
    void DeleteFile(string path);
    void CopyFile(string source, string destination, bool overwrite);
    byte[] ReadFileBytes(string path);
    void WriteFileBytes(string path, byte[] bytes);
}

internal sealed class WindowsMaintenanceEnvironment : IMaintenanceEnvironment
{
    private static readonly object RegistryMutationLock = new();

    private static readonly Regex GuidRegex = new(
        @"^\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}$",
        RegexOptions.Compiled);

    private static readonly HashSet<string> ProhibitedInterpreters = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe", "powershell.exe", "pwsh.exe", "cscript.exe", "wscript.exe",
        "bash.exe", "sh.exe", "rundll32.exe", "mshta.exe", "regsvr32.exe", "conhost.exe"
    };

    private static readonly HashSet<string> UserDataExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".db", ".sqlite", ".sqlite3", ".sav", ".save", ".dat", ".conf", ".config",
        ".json", ".xml", ".ini", ".doc", ".docx", ".pdf", ".txt", ".key", ".wallet"
    };

    private static readonly HashSet<string> UserDataFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "data", "userdata", "user data", "profiles", "saves", "saved games", "config", "settings"
    };

    public IReadOnlyList<RawUninstallRecord> GetUninstallRecords()
    {
        var records = new List<RawUninstallRecord>();
        const string uninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

        ScanUninstallRoot(RegistryHive.CurrentUser, RegistryView.Registry64, uninstallKey, records);
        ScanUninstallRoot(RegistryHive.CurrentUser, RegistryView.Registry32, uninstallKey, records);
        ScanUninstallRoot(RegistryHive.LocalMachine, RegistryView.Registry64, uninstallKey, records);
        ScanUninstallRoot(RegistryHive.LocalMachine, RegistryView.Registry32, uninstallKey, records);

        return records;
    }

    public bool IsUninstallRegistrationPresent(string hiveStr, string viewStr, string subKeyName)
    {
        // An unreadable key cannot prove that the application's registration disappeared.
        if (!Enum.TryParse(hiveStr, ignoreCase: true, out RegistryHive hive) ||
            !Enum.TryParse(viewStr, ignoreCase: true, out RegistryView view) ||
            string.IsNullOrWhiteSpace(subKeyName) ||
            subKeyName.Contains('\\') ||
            subKeyName.Contains('/'))
        {
            return true;
        }

        try
        {
            lock (RegistryMutationLock)
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + subKeyName,
                    writable: false);
                return key != null;
            }
        }
        catch
        {
            return true;
        }
    }

    private static void ScanUninstallRoot(
        RegistryHive hive,
        RegistryView view,
        string subKeyPath,
        List<RawUninstallRecord> records)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKeyPath, writable: false);
            if (key == null)
            {
                if (hive == RegistryHive.LocalMachine)
                    throw new IOException($"Unable to enumerate required uninstall registry root: {hive}/{view}/{subKeyPath}");
                return;
            }

            foreach (string subKeyName in key.GetSubKeyNames())
            {
                try
                {
                    using var subKey = key.OpenSubKey(subKeyName, writable: false);
                    if (subKey == null)
                        throw new IOException($"Unable to read uninstall registry subkey: {hive}/{view}/{subKeyPath}/{subKeyName}");

                    string displayName = subKey.GetValue("DisplayName")?.ToString()?.Trim() ?? "";
                    string? displayVersion = subKey.GetValue("DisplayVersion")?.ToString()?.Trim();
                    string? publisher = subKey.GetValue("Publisher")?.ToString()?.Trim();
                    string? installLocation = subKey.GetValue("InstallLocation")?.ToString()?.Trim();
                    string? uninstallString = subKey.GetValue("UninstallString")?.ToString()?.Trim();
                    string? quietUninstallString = subKey.GetValue("QuietUninstallString")?.ToString()?.Trim();

                    long? estimatedSizeKb = null;
                    object? sizeVal = subKey.GetValue("EstimatedSize");
                    if (sizeVal is int sizeInt)
                        estimatedSizeKb = sizeInt;
                    else if (sizeVal is long sizeLong)
                        estimatedSizeKb = sizeLong;

                    int sysComp = 0;
                    if (subKey.GetValue("SystemComponent") is int sc)
                        sysComp = sc;

                    string? parentKey = subKey.GetValue("ParentKeyName")?.ToString()?.Trim();

                    int winInstaller = 0;
                    if (subKey.GetValue("WindowsInstaller") is int wi)
                        winInstaller = wi;

                    string? pkgFullName = subKey.GetValue("PackageFullName")?.ToString()?.Trim();

                    records.Add(new RawUninstallRecord(
                        Hive: hive.ToString(),
                        View: view.ToString(),
                        SubKeyName: subKeyName,
                        DisplayName: displayName,
                        DisplayVersion: string.IsNullOrEmpty(displayVersion) ? null : displayVersion,
                        Publisher: string.IsNullOrEmpty(publisher) ? null : publisher,
                        InstallLocation: string.IsNullOrEmpty(installLocation) ? null : installLocation,
                        UninstallString: string.IsNullOrEmpty(uninstallString) ? null : uninstallString,
                        QuietUninstallString: string.IsNullOrEmpty(quietUninstallString) ? null : quietUninstallString,
                        EstimatedSizeKb: estimatedSizeKb,
                        SystemComponent: sysComp,
                        ParentKeyName: string.IsNullOrEmpty(parentKey) ? null : parentKey,
                        WindowsInstaller: winInstaller,
                        PackageFullName: string.IsNullOrEmpty(pkgFullName) ? null : pkgFullName));
                }
                catch
                {
                    throw; // Incomplete inventory must never verify an application's absence.
                }
            }
        }
        catch
        {
            throw; // Fail closed instead of reporting a partial inventory as complete.
        }
    }

    public IReadOnlyList<RawRegistryValueRecord> GetStartupRunValues()
    {
        var results = new List<RawRegistryValueRecord>();
        string[] runKeys =
        [
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            @"Software\Microsoft\Windows\CurrentVersion\RunOnce"
        ];

        (RegistryHive hive, RegistryView view)[] targets =
        [
            (RegistryHive.CurrentUser, RegistryView.Registry64),
            (RegistryHive.CurrentUser, RegistryView.Registry32),
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
        ];

        foreach (var (hive, view) in targets)
        {
            foreach (string runPath in runKeys)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var key = baseKey.OpenSubKey(runPath, writable: false);
                    if (key == null)
                        continue;

                    foreach (string valueName in key.GetValueNames())
                    {
                        try
                        {
                            RegistryValueKind kind = key.GetValueKind(valueName);
                            object? data = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                            string? rawStr = data?.ToString();

                            results.Add(new RawRegistryValueRecord(
                                Hive: hive.ToString(),
                                View: view.ToString(),
                                SubKey: runPath,
                                ValueName: valueName,
                                ValueKind: kind.ToString(),
                                ValueData: data,
                                ExtractedExecutablePath: ExtractExecutablePath(rawStr)));
                        }
                        catch
                        {
                            // Ignore single value read errors
                        }
                    }
                }
                catch
                {
                    // Ignore key read errors
                }
            }
        }

        return results;
    }

    public IReadOnlyList<RawAppPathRecord> GetAppPathsRecords()
    {
        var results = new List<RawAppPathRecord>();
        const string appPathsRoot = @"Software\Microsoft\Windows\CurrentVersion\App Paths";

        (RegistryHive hive, RegistryView view)[] targets =
        [
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Registry64),
            (RegistryHive.CurrentUser, RegistryView.Registry32),
        ];

        foreach (var (hive, view) in targets)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(appPathsRoot, writable: false);
                if (key == null)
                    continue;

                foreach (string subKeyName in key.GetSubKeyNames())
                {
                    try
                    {
                        using var subKey = key.OpenSubKey(subKeyName, writable: false);
                        if (subKey == null)
                            continue;

                        string? defaultVal = subKey.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
                        string? pathVal = subKey.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();

                        results.Add(new RawAppPathRecord(
                            Hive: hive.ToString(),
                            View: view.ToString(),
                            SubKey: $@"{appPathsRoot}\{subKeyName}",
                            DefaultValue: defaultVal,
                            PathValue: pathVal));
                    }
                    catch
                    {
                        // Ignore individual subkey read errors
                    }
                }
            }
            catch
            {
                // Ignore root errors
            }
        }

        return results;
    }

    public RawRegistryValueRecord? ReadRegistryValue(string hiveStr, string viewStr, string subKey, string valueName)
    {
        if (!Enum.TryParse(hiveStr, ignoreCase: true, out RegistryHive hive) ||
            !Enum.TryParse(viewStr, ignoreCase: true, out RegistryView view))
        {
            return null;
        }

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKey, writable: false);
            if (key == null)
                return null;

            if (!key.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase)) return null;
            RegistryValueKind kind = key.GetValueKind(valueName);
            object? data = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);

            return new RawRegistryValueRecord(
                Hive: hiveStr,
                View: viewStr,
                SubKey: subKey,
                ValueName: valueName,
                ValueKind: kind.ToString(),
                ValueData: data,
                ExtractedExecutablePath: ExtractExecutablePath(data?.ToString()));
        }
        catch
        {
            return null;
        }
    }

    public bool DeleteRegistryValue(string hiveStr, string viewStr, string subKey, string valueName)
    {
        if (!Enum.TryParse(hiveStr, ignoreCase: true, out RegistryHive hive) ||
            !Enum.TryParse(viewStr, ignoreCase: true, out RegistryView view))
        {
            return false;
        }

        try
        {
            lock (RegistryMutationLock)
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(subKey, writable: true);
                if (key == null)
                    return false;

                key.DeleteValue(valueName, throwOnMissingValue: false);
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    public bool DeleteRegistryValueIfUnchanged(RawRegistryValueRecord expected)
    {
        if (expected is null ||
            !Enum.TryParse(expected.Hive, ignoreCase: true, out RegistryHive hive) ||
            !Enum.TryParse(expected.View, ignoreCase: true, out RegistryView view) ||
            string.IsNullOrWhiteSpace(expected.SubKey) ||
            string.IsNullOrEmpty(expected.ValueName))
        {
            return false;
        }

        try
        {
            lock (RegistryMutationLock)
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(expected.SubKey, writable: true);
                if (key == null || !key.GetValueNames().Contains(expected.ValueName, StringComparer.OrdinalIgnoreCase))
                    return false;

                // Re-read immediately before deletion so a changed Run value is preserved.
                RegistryValueKind kind = key.GetValueKind(expected.ValueName);
                object? value = key.GetValue(expected.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                var current = expected with { ValueKind = kind.ToString(), ValueData = value };
                if (!MaintenanceBackupManager.RegistryEquals(expected, current))
                    return false;

                // A competing external process can still change the value between this
                // comparison and DeleteValue; RegistryKey has no cross-process atomic CAS.
                key.DeleteValue(expected.ValueName, throwOnMissingValue: true);
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    public bool SetRegistryValue(
        string hiveStr,
        string viewStr,
        string subKey,
        string valueName,
        string valueKindStr,
        string valueDataBase64)
    {
        if (!Enum.TryParse(hiveStr, ignoreCase: true, out RegistryHive hive) ||
            !Enum.TryParse(viewStr, ignoreCase: true, out RegistryView view) ||
            !Enum.TryParse(valueKindStr, ignoreCase: true, out RegistryValueKind kind))
        {
            return false;
        }

        try
        {
            byte[] rawBytes = Convert.FromBase64String(valueDataBase64 ?? "");
            object deserialized = DeserializeRegistryValue(rawBytes, kind);
            lock (RegistryMutationLock)
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.CreateSubKey(subKey, writable: true);
                if (key == null)
                    return false;

                key.SetValue(valueName, deserialized, kind);
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    public bool TrySetRegistryValueIfAbsent(
        string hiveStr,
        string viewStr,
        string subKey,
        string valueName,
        string valueKindStr,
        string valueDataBase64)
    {
        if (!Enum.TryParse(hiveStr, ignoreCase: true, out RegistryHive hive) ||
            !Enum.TryParse(viewStr, ignoreCase: true, out RegistryView view) ||
            !Enum.TryParse(valueKindStr, ignoreCase: true, out RegistryValueKind kind))
        {
            return false;
        }

        try
        {
            byte[] rawBytes = Convert.FromBase64String(valueDataBase64 ?? "");
            object deserialized = DeserializeRegistryValue(rawBytes, kind);
            lock (RegistryMutationLock)
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.CreateSubKey(subKey, writable: true);
                if (key == null || key.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase))
                    return false;

                key.SetValue(valueName, deserialized, kind);
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    public bool CheckFileIsTrulyMissing(string rawPath, out string normalizedPath, out string reason)
    {
        normalizedPath = "";
        reason = "";

        if (string.IsNullOrWhiteSpace(rawPath))
        {
            reason = "Percorso vuoto";
            return false;
        }

        string unquoted = rawPath.Trim();
        if (unquoted.StartsWith("\"") && unquoted.Length > 1)
        {
            int nextQuote = unquoted.IndexOf('"', 1);
            unquoted = nextQuote > 0 ? unquoted.Substring(1, nextQuote - 1) : unquoted.Trim('"');
        }

        string expanded;
        try
        {
            expanded = Environment.ExpandEnvironmentVariables(unquoted).Trim();
        }
        catch
        {
            reason = "Impossibile espandere le variabili di ambiente";
            return false;
        }

        if (!Path.IsPathRooted(expanded))
        {
            reason = "Percorso relativo o non risolvibile";
            return false;
        }

        normalizedPath = expanded;

        try
        {
            string? root = Path.GetPathRoot(expanded);
            if (string.IsNullOrWhiteSpace(root))
            {
                reason = "Percorso privo di root disco";
                return false;
            }

            if (!Directory.Exists(root))
            {
                // Unmounted drive -> not a dead leftover
                reason = $"Unità '{root}' non montata o non accessibile";
                return false;
            }

            if (!IsSafeLocalPath(expanded)) return false;
            var drive = new DriveInfo(root);
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) return false;
            try
            {
                _ = File.GetAttributes(expanded);
                reason = "Il target esiste su disco";
                return false;
            }
            catch (FileNotFoundException)
            {
                reason = "File non presente su disco";
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                reason = "Directory non presente su disco";
                return true;
            }
        }
        catch (Exception ex)
        {
            reason = $"Errore di verifica percorso: {ex.Message}";
            return false;
        }
    }

    public IReadOnlyList<string> FindLeftoverFiles(string installLocation, int maxResults)
    {
        var candidates = new List<string>();
        var visitedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(installLocation))
            return candidates;

        string expanded;
        try { expanded = Path.GetFullPath(Environment.ExpandEnvironmentVariables(installLocation).Trim()); }
        catch { return candidates; }

        if (IsSafeLocalPath(expanded) && !IsProtectedSystemPath(expanded) && Directory.Exists(expanded))
        {
            CollectDirectoryItems(expanded, candidates, visitedPaths, maxResults);
        }

        return candidates;
    }

    private static void CollectDirectoryItems(
        string dirPath,
        List<string> results,
        HashSet<string> visited,
        int maxResults)
    {
        if (results.Count >= maxResults || !visited.Add(dirPath))
            return;

        try
        {
            var dirInfo = new DirectoryInfo(dirPath);
            if ((dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                return; // Never follow reparse points

            foreach (FileInfo file in dirInfo.GetFiles())
            {
                if (results.Count >= maxResults)
                    return;

                if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;

                if (visited.Add(file.FullName))
                    results.Add(file.FullName);
            }

            foreach (DirectoryInfo subDir in dirInfo.GetDirectories())
            {
                if (results.Count >= maxResults)
                    return;

                if ((subDir.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;

                CollectDirectoryItems(subDir.FullName, results, visited, maxResults);
            }
        }
        catch
        {
            // Ignore access errors on individual folders
        }
    }

    public bool IsSafeLocalPath(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\") || path[2..].Contains(':')) return false;
            string full = Path.GetFullPath(path);
            string? current = full;
            bool missing = false;
            while (!string.IsNullOrEmpty(current))
            {
                try
                {
                    FileAttributes attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
                    if (missing && (attributes & FileAttributes.Directory) == 0) return false;
                }
                catch (FileNotFoundException) { missing = true; }
                catch (DirectoryNotFoundException) { missing = true; }
                current = Path.GetDirectoryName(current);
            }
            return new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Fixed;
        }
        catch { return false; }
    }

    public void SecureBackupDirectory(string path)
    {
        if (!IsSafeLocalPath(path)) throw new IOException("Unsafe backup directory.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    public bool IsProtectedSystemPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return true;

        string expanded;
        try { expanded = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path).Trim()); }
        catch { return true; }

        string? root = Path.GetPathRoot(expanded);
        if (string.IsNullOrWhiteSpace(root) || string.Equals(expanded, root, StringComparison.OrdinalIgnoreCase))
            return true; // Drive roots are strictly protected

        // 1. Entire subtrees that are ALWAYS protected (any file or folder within them)
        string[] protectedSubtrees =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.SystemDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.Personal),
        ];

        foreach (string st in protectedSubtrees)
        {
            if (string.IsNullOrWhiteSpace(st))
                continue;

            string fullSt = Path.GetFullPath(st).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(expanded, fullSt, StringComparison.OrdinalIgnoreCase) ||
                expanded.StartsWith(fullSt + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // 2. Direct root folders that cannot be targeted itself
        string[] protectedRootFolders =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ];

        foreach (string pr in protectedRootFolders)
        {
            if (string.IsNullOrWhiteSpace(pr))
                continue;

            string fullPr = Path.GetFullPath(pr).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(expanded, fullPr, StringComparison.OrdinalIgnoreCase))
                return true; // Direct root cannot be deleted
        }

        // 3. Shared system vendor directories under Program Files
        string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        if (IsDirectChildOf(expanded, progFiles) || IsDirectChildOf(expanded, progFilesX86))
        {
            string leaf = Path.GetFileName(expanded);
            if (string.Equals(leaf, "Common Files", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(leaf, "Microsoft", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(leaf, "Windows Defender", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(leaf, "Windows NT", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(leaf, "WindowsApps", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // 4. Self protection: VoltManager's own install / application directory
        string appBase = AppDomain.CurrentDomain.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(appBase))
        {
            string fullAppBase = Path.GetFullPath(appBase).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(expanded, fullAppBase, StringComparison.OrdinalIgnoreCase) ||
                expanded.StartsWith(fullAppBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (expanded.Contains("VoltManager", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static bool IsDirectChildOf(string path, string parent)
    {
        if (string.IsNullOrWhiteSpace(parent))
            return false;

        string? actualParent = Path.GetDirectoryName(path);
        return string.Equals(actualParent, Path.GetFullPath(parent), StringComparison.OrdinalIgnoreCase);
    }

    public bool ContainsUserData(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string ext = Path.GetExtension(path);
        if (UserDataExtensions.Contains(ext))
            return true;

        string[] segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (string seg in segments)
        {
            if (UserDataFolderNames.Contains(seg))
                return true;
        }

        return false;
    }

    public async Task<ProcessExecutionResult> RunUninstallerAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string exeName = Path.GetFileName(fileName);
        if (ProhibitedInterpreters.Contains(exeName))
        {
            return new ProcessExecutionResult(-1, false, $"Interprete di comandi non consentito: {exeName}");
        }

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = true,
            Verb = "runas",
        };

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
            {
                return new ProcessExecutionResult(-1, false, "Impossibile avviare il processo di disinstallazione.");
            }

            try
            {
                // Cancellation must not release the mutation lease while the external process is alive.
                await process.WaitForExitAsync(CancellationToken.None);
                return new ProcessExecutionResult(process.ExitCode, true, null);
            }
            catch (OperationCanceledException)
            {
                return new ProcessExecutionResult(-1, false, "Operazione annullata durante l'esecuzione del disinstallatore.");
            }
        }
        catch (Exception ex)
        {
            return new ProcessExecutionResult(-1, false, $"Errore avvio processo: {ex.Message}");
        }
    }

    public bool OpenAppsSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:appsfeatures",
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string? ExtractExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        string trimmed = command.Trim();
        if (trimmed.StartsWith("\""))
        {
            int endQuote = trimmed.IndexOf('\"', 1);
            if (endQuote > 1)
                return trimmed.Substring(1, endQuote - 1);
        }

        if (trimmed.Contains('"')) return null;
        int space = trimmed.IndexOfAny([' ', '\t']);
        string token = space > 0 ? trimmed[..space] : trimmed;
        if (!token.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return null;

        // An unquoted second executable path could be parsed as arguments to a different executable.
        if (space > 0)
        {
            string remainder = trimmed[(space + 1)..].TrimStart();
            int nextSpace = remainder.IndexOfAny([' ', '\t']);
            string firstArgument = nextSpace >= 0 ? remainder[..nextSpace] : remainder;
            if (firstArgument.Contains('\\') && firstArgument.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return null;
        }

        return token;
    }

    public static object DeserializeRegistryValue(byte[] bytes, RegistryValueKind kind)
    {
        return kind switch
        {
            RegistryValueKind.String or RegistryValueKind.ExpandString =>
                Encoding.Unicode.GetString(bytes),
            RegistryValueKind.DWord =>
                bytes.Length == 4 ? BitConverter.ToInt32(bytes, 0) : throw new InvalidDataException("Invalid DWORD length: expected 4 bytes"),
            RegistryValueKind.QWord =>
                bytes.Length == 8 ? BitConverter.ToInt64(bytes, 0) : throw new InvalidDataException("Invalid QWORD length: expected 8 bytes"),
            RegistryValueKind.Binary =>
                bytes,
            RegistryValueKind.MultiString =>
                Encoding.Unicode.GetString(bytes).Split('\0'),
            _ => Encoding.Unicode.GetString(bytes),
        };
    }

    public string ReadAllText(string path) => File.ReadAllText(path);
    public void WriteAllText(string path, string text) => File.WriteAllText(path, text);
    public bool FileExists(string path) => FileExistsFailClosed(path, File.GetAttributes);

    internal static bool FileExistsFailClosed(string path, Func<string, FileAttributes> getAttributes)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            return (getAttributes(path) & FileAttributes.Directory) == 0;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (UnauthorizedAccessException) { return true; }
        catch (SecurityException) { return true; }
        catch (IOException) { return true; }
        catch { return false; }
    }
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void DeleteDirectory(string path, bool recursive) => Directory.Delete(path, recursive);
    public void DeleteFile(string path) => File.Delete(path);
    public void CopyFile(string source, string destination, bool overwrite) => File.Copy(source, destination, overwrite);
    public byte[] ReadFileBytes(string path) => File.ReadAllBytes(path);
    public void WriteFileBytes(string path, byte[] bytes) => File.WriteAllBytes(path, bytes);
}
