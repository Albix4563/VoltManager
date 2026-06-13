#pragma warning disable CA1416
using System.IO;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace VoltManager.Services;

public record StartupAppsSnapshot
{
    [JsonPropertyName("enabled")] public List<StartupAppEntry> Enabled { get; init; } = new();
    [JsonPropertyName("disabled")] public List<StartupAppEntry> Disabled { get; init; } = new();
}

public record StartupAppEntry
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("command")] public string Command { get; init; } = "";
    [JsonPropertyName("path")] public string Path { get; init; } = "";
    [JsonPropertyName("source")] public string Source { get; init; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("isManaged")] public bool IsManaged { get; init; }
}

public class StartupAppsService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string StartupApprovedFolderKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
    private const string ManagedPrefix = "Miliano's App - ";

    public StartupAppsSnapshot GetStartupApps()
    {
        var entries = new List<StartupAppEntry>();

        entries.AddRange(ReadRunKey(Registry.CurrentUser, "HKCU Run", StartupApprovedRunKeyPath));
        entries.AddRange(ReadRunKey(Registry.LocalMachine, "HKLM Run", StartupApprovedRunKeyPath));
        entries.AddRange(ReadStartupFolder(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            "Startup folder",
            Registry.CurrentUser));
        entries.AddRange(ReadStartupFolder(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
            "Common startup folder",
            Registry.LocalMachine));

        var distinct = entries
            .GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new StartupAppsSnapshot
        {
            Enabled = distinct.Where(e => e.Enabled).ToList(),
            Disabled = distinct.Where(e => !e.Enabled).ToList(),
        };
    }

    public string? PickExecutablePath()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Seleziona applicazione da avviare con Windows",
            Filter = "Applicazioni (*.exe)|*.exe|Collegamenti (*.lnk)|*.lnk|Script avviabili (*.bat;*.cmd)|*.bat;*.cmd|Tutti i file (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public StartupAppEntry AddManagedStartupApp(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Percorso mancante.");

        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (!File.Exists(path))
            throw new FileNotFoundException("File non trovato.", path);

        string ext = System.IO.Path.GetExtension(path);
        if (!IsAllowedStartupFile(ext))
            throw new ArgumentException("Sono supportati file .exe, .lnk, .bat e .cmd.");

        using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Impossibile aprire la chiave di avvio utente.");

        string valueName = CreateUniqueManagedValueName(runKey, path);
        string command = Quote(path);
        runKey.SetValue(valueName, command, RegistryValueKind.String);

        // If Windows previously tracked the same startup value as disabled, mark it enabled.
        TrySetStartupApproved(Registry.CurrentUser, StartupApprovedRunKeyPath, valueName, enabled: true);

        return new StartupAppEntry
        {
            Id = valueName,
            Name = valueName,
            Command = command,
            Path = path,
            Source = "HKCU Run",
            Enabled = true,
            IsManaged = true,
        };
    }

    public bool RemoveManagedStartupApp(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !id.StartsWith(ManagedPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("È possibile rimuovere solo app gestite da Miliano's App.");

        using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (runKey == null) return false;

        if (runKey.GetValueNames().Any(n => string.Equals(n, id, StringComparison.Ordinal)))
        {
            runKey.DeleteValue(id, throwOnMissingValue: false);
            TryDeleteStartupApproved(Registry.CurrentUser, StartupApprovedRunKeyPath, id);
            return true;
        }

        return false;
    }

    private static IEnumerable<StartupAppEntry> ReadRunKey(RegistryKey root, string source, string approvalKeyPath)
    {
        var entries = new List<StartupAppEntry>();

        try
        {
            using var key = root.OpenSubKey(RunKeyPath);
            if (key == null) return entries;

            foreach (string valueName in key.GetValueNames())
            {
                string command = key.GetValue(valueName)?.ToString() ?? "";
                bool enabled = IsStartupApproved(root, approvalKeyPath, valueName, defaultEnabled: true);
                bool isManaged = source == "HKCU Run" && valueName.StartsWith(ManagedPrefix, StringComparison.Ordinal);
                entries.Add(new StartupAppEntry
                {
                    Id = isManaged ? valueName : source + ":" + valueName,
                    Name = valueName,
                    Command = command,
                    Path = ExtractExecutablePath(command),
                    Source = source,
                    Enabled = enabled,
                    IsManaged = isManaged,
                });
            }
        }
        catch
        {
            // Startup inventory is best-effort; unreadable locations are skipped.
        }

        return entries;
    }

    private static IEnumerable<StartupAppEntry> ReadStartupFolder(string folderPath, string source, RegistryKey approvalRoot)
    {
        var entries = new List<StartupAppEntry>();

        try
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
                return entries;

            foreach (string path in Directory.EnumerateFiles(folderPath))
            {
                string fileName = System.IO.Path.GetFileName(path);
                bool enabled = IsStartupApproved(approvalRoot, StartupApprovedFolderKeyPath, fileName, defaultEnabled: true);

                entries.Add(new StartupAppEntry
                {
                    Id = source + ":" + fileName,
                    Name = System.IO.Path.GetFileNameWithoutExtension(path),
                    Command = path,
                    Path = path,
                    Source = source,
                    Enabled = enabled,
                    IsManaged = false,
                });
            }
        }
        catch
        {
            // Startup inventory is best-effort; unreadable locations are skipped.
        }

        return entries;
    }

    private static bool IsStartupApproved(RegistryKey root, string keyPath, string valueName, bool defaultEnabled)
    {
        try
        {
            using var key = root.OpenSubKey(keyPath);
            if (key?.GetValue(valueName) is not byte[] state || state.Length == 0)
                return defaultEnabled;

            return state[0] switch
            {
                0x02 => true,
                0x03 => false,
                _ => defaultEnabled,
            };
        }
        catch
        {
            return defaultEnabled;
        }
    }

    private static void TrySetStartupApproved(RegistryKey root, string keyPath, string valueName, bool enabled)
    {
        try
        {
            using var key = root.CreateSubKey(keyPath, writable: true);
            key?.SetValue(valueName, enabled
                ? new byte[] { 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }
                : new byte[] { 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 },
                RegistryValueKind.Binary);
        }
        catch
        {
            // Non-critical; Windows will infer enabled state if no StartupApproved value exists.
        }
    }

    private static void TryDeleteStartupApproved(RegistryKey root, string keyPath, string valueName)
    {
        try
        {
            using var key = root.OpenSubKey(keyPath, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }
        catch
        {
            // Non-critical cleanup.
        }
    }

    private static string CreateUniqueManagedValueName(RegistryKey runKey, string path)
    {
        string baseName = System.IO.Path.GetFileNameWithoutExtension(path).Trim();
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "Custom App";

        string candidate = ManagedPrefix + baseName;
        string command = Quote(path);

        if (IsValueAvailableOrSame(runKey, candidate, command))
            return candidate;

        for (int i = 2; i < 100; i++)
        {
            string numbered = $"{candidate} ({i})";
            if (IsValueAvailableOrSame(runKey, numbered, command))
                return numbered;
        }

        throw new InvalidOperationException("Sono già presenti troppe app gestite con lo stesso nome.");
    }

    private static bool IsValueAvailableOrSame(RegistryKey runKey, string valueName, string command)
    {
        string? existing = runKey.GetValue(valueName)?.ToString();
        return existing == null || string.Equals(existing, command, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAllowedStartupFile(string extension)
    {
        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase);
    }

    private static string Quote(string value)
    {
        return "\"" + value.Trim().Trim('"') + "\"";
    }

    private static string ExtractExecutablePath(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "";

        command = Environment.ExpandEnvironmentVariables(command.Trim());
        if (command.StartsWith("\"", StringComparison.Ordinal))
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command.Substring(1, end - 1) : command.Trim('"');
        }

        int exeIndex = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex >= 0)
            return command.Substring(0, exeIndex + 4);

        int firstSpace = command.IndexOf(' ');
        return firstSpace > 0 ? command.Substring(0, firstSpace) : command;
    }
}