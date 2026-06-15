#pragma warning disable CA1416
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VoltManager.Services;

public record InstalledApplicationsSnapshot
{
    [JsonPropertyName("applications")] public List<InstalledApplicationEntry> Applications { get; init; } = new();
    [JsonPropertyName("total")] public int Total => Applications.Count;
    [JsonPropertyName("withUninstaller")] public int WithUninstaller => Applications.Count(a => a.CanUninstall);
}

public record InstalledApplicationEntry
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("publisher")] public string Publisher { get; init; } = "";
    [JsonPropertyName("version")] public string Version { get; init; } = "";
    [JsonPropertyName("installDate")] public string InstallDate { get; init; } = "";
    [JsonPropertyName("installLocation")] public string InstallLocation { get; init; } = "";
    [JsonPropertyName("estimatedSizeMb")] public int? EstimatedSizeMb { get; init; }
    [JsonPropertyName("source")] public string Source { get; init; } = "";
    [JsonPropertyName("canUninstall")] public bool CanUninstall => !string.IsNullOrWhiteSpace(UninstallString) || !string.IsNullOrWhiteSpace(QuietUninstallString);

    [JsonIgnore] public string RegistryPath { get; init; } = "";
    [JsonIgnore] public string UninstallString { get; init; } = "";
    [JsonIgnore] public string QuietUninstallString { get; init; } = "";
}

public record UninstallLaunchResult
{
    [JsonPropertyName("success")] public bool Success { get; init; }
    [JsonPropertyName("message")] public string Message { get; init; } = "";
    [JsonPropertyName("processId")] public int? ProcessId { get; init; }
}

public class InstalledApplicationsService
{
    private const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public InstalledApplicationsSnapshot GetInstalledApplications()
    {
        var entries = new List<InstalledApplicationEntry>();

        entries.AddRange(ReadRegistryView(RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM 64-bit"));
        entries.AddRange(ReadRegistryView(RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM 32-bit"));
        entries.AddRange(ReadRegistryView(RegistryHive.CurrentUser, RegistryView.Registry64, "HKCU 64-bit"));
        entries.AddRange(ReadRegistryView(RegistryHive.CurrentUser, RegistryView.Registry32, "HKCU 32-bit"));

        var applications = entries
            .GroupBy(e => DeduplicateKey(e), StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderByDescending(e => e.CanUninstall)
                .ThenByDescending(e => !string.IsNullOrWhiteSpace(e.InstallLocation))
                .First())
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new InstalledApplicationsSnapshot { Applications = applications };
    }

    public UninstallLaunchResult UninstallApplication(string id, bool preferQuiet = false)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("ID applicazione mancante.");

        var app = GetInstalledApplications().Applications
            .FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal));

        if (app == null)
            throw new InvalidOperationException("Applicazione non trovata. Aggiorna l'elenco e riprova.");

        string command = preferQuiet && !string.IsNullOrWhiteSpace(app.QuietUninstallString)
            ? app.QuietUninstallString
            : app.UninstallString;

        if (string.IsNullOrWhiteSpace(command) && !string.IsNullOrWhiteSpace(app.QuietUninstallString))
            command = app.QuietUninstallString;

        if (string.IsNullOrWhiteSpace(command))
            throw new InvalidOperationException("Questa applicazione non espone un comando di disinstallazione valido.");

        var (fileName, arguments) = SplitCommandLine(command);
        if (string.IsNullOrWhiteSpace(fileName))
            throw new InvalidOperationException("Comando di disinstallazione non valido.");

        if (IsMsiexec(fileName))
            arguments = NormalizeMsiArguments(arguments);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = true,
        };

        string? workingDirectory = TryGetWorkingDirectory(fileName);
        if (!string.IsNullOrWhiteSpace(workingDirectory))
            psi.WorkingDirectory = workingDirectory;

        var process = Process.Start(psi);
        return new UninstallLaunchResult
        {
            Success = true,
            ProcessId = process?.Id,
            Message = "Procedura di disinstallazione avviata.",
        };
    }

    private static IEnumerable<InstalledApplicationEntry> ReadRegistryView(RegistryHive hive, RegistryView view, string source)
    {
        var entries = new List<InstalledApplicationEntry>();

        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstallKey = root.OpenSubKey(UninstallKeyPath);
            if (uninstallKey == null) return entries;

            foreach (string subKeyName in uninstallKey.GetSubKeyNames())
            {
                try
                {
                    using var appKey = uninstallKey.OpenSubKey(subKeyName);
                    if (appKey == null) continue;

                    var entry = ReadApplicationEntry(appKey, source, subKeyName);
                    if (entry != null)
                        entries.Add(entry);
                }
                catch
                {
                    // Individual uninstall entries can be malformed or unreadable.
                }
            }
        }
        catch
        {
            // Registry view not available or inaccessible; inventory remains best-effort.
        }

        return entries;
    }

    private static InstalledApplicationEntry? ReadApplicationEntry(RegistryKey key, string source, string subKeyName)
    {
        string displayName = GetString(key, "DisplayName");
        if (string.IsNullOrWhiteSpace(displayName)) return null;

        if (GetDword(key, "SystemComponent") == 1 || IsServicingEntry(key, displayName))
            return null;

        string registryPath = source + @"\" + subKeyName;
        int? estimatedSizeMb = GetEstimatedSizeMb(key);

        return new InstalledApplicationEntry
        {
            Id = StableId(registryPath),
            Name = displayName.Trim(),
            Publisher = GetString(key, "Publisher").Trim(),
            Version = GetString(key, "DisplayVersion").Trim(),
            InstallDate = NormalizeInstallDate(GetString(key, "InstallDate")),
            InstallLocation = Environment.ExpandEnvironmentVariables(GetString(key, "InstallLocation").Trim()),
            EstimatedSizeMb = estimatedSizeMb,
            Source = source,
            RegistryPath = registryPath,
            UninstallString = GetString(key, "UninstallString").Trim(),
            QuietUninstallString = GetString(key, "QuietUninstallString").Trim(),
        };
    }

    private static bool IsServicingEntry(RegistryKey key, string displayName)
    {
        string releaseType = GetString(key, "ReleaseType");
        if (!string.IsNullOrWhiteSpace(releaseType) &&
            (releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase)
             || releaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)
             || releaseType.Contains("Security", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(GetString(key, "ParentKeyName")))
            return true;

        return displayName.StartsWith("Update for ", StringComparison.OrdinalIgnoreCase)
            || displayName.StartsWith("Security Update for ", StringComparison.OrdinalIgnoreCase)
            || displayName.StartsWith("Hotfix for ", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetString(RegistryKey key, string name)
    {
        object? value = key.GetValue(name);
        return value?.ToString() ?? "";
    }

    private static int? GetDword(RegistryKey key, string name)
    {
        object? value = key.GetValue(name);
        if (value is int i) return i;
        if (value is string s && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            return parsed;
        return null;
    }

    private static int? GetEstimatedSizeMb(RegistryKey key)
    {
        int? sizeKb = GetDword(key, "EstimatedSize");
        if (sizeKb == null || sizeKb <= 0) return null;
        return Math.Max(1, (int)Math.Round(sizeKb.Value / 1024d, MidpointRounding.AwayFromZero));
    }

    private static string NormalizeInstallDate(string value)
    {
        value = value.Trim();
        if (DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return value;
    }

    private static string DeduplicateKey(InstalledApplicationEntry entry)
    {
        string name = NormalizeKeyPart(entry.Name);
        string publisher = NormalizeKeyPart(entry.Publisher);
        string version = NormalizeKeyPart(entry.Version);
        return name + "|" + publisher + "|" + version;
    }

    private static string NormalizeKeyPart(string value)
        => Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", " ");

    private static string StableId(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static (string FileName, string Arguments) SplitCommandLine(string command)
    {
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        if (command.Length == 0) return ("", "");

        if (command.StartsWith("\"", StringComparison.Ordinal))
        {
            int endQuote = command.IndexOf('"', 1);
            if (endQuote > 1)
            {
                string file = command.Substring(1, endQuote - 1);
                string args = command[(endQuote + 1)..].TrimStart();
                return (file, args);
            }
        }

        int executableEnd = FindExecutableBoundary(command);
        if (executableEnd > 0)
            return (command[..executableEnd].Trim(), command[executableEnd..].TrimStart());

        int firstSpace = command.IndexOf(' ');
        if (firstSpace > 0)
            return (command[..firstSpace].Trim(), command[(firstSpace + 1)..].TrimStart());

        return (command, "");
    }

    private static int FindExecutableBoundary(string command)
    {
        foreach (string extension in new[] { ".exe", ".msi", ".bat", ".cmd", ".com" })
        {
            int index = command.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) return index + extension.Length;
        }

        return -1;
    }

    private static bool IsMsiexec(string fileName)
    {
        string name = Path.GetFileNameWithoutExtension(fileName);
        return name.Equals("msiexec", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeMsiArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return arguments;
        return Regex.Replace(arguments, @"(?i)(^|\s)/(i)(?=\s*\{)", "$1/x").TrimStart();
    }

    private static string? TryGetWorkingDirectory(string fileName)
    {
        try
        {
            if (File.Exists(fileName))
                return Path.GetDirectoryName(fileName);
        }
        catch
        {
            // Leave working directory unset for shell-resolved executables.
        }

        return null;
    }
}
