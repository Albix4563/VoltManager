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
    [JsonPropertyName("hive")] public string? Hive { get; init; }
    [JsonPropertyName("view")] public string? View { get; init; }

    private string? _registryView;
    [JsonPropertyName("registryView")]
    public string? RegistryView
    {
        get => _registryView ?? View;
        init => _registryView = value;
    }

    [JsonPropertyName("stateKnown")] public bool StateKnown { get; init; } = true;
}

public class StartupAppsService : IDisposable
{
    // ponytail: Task Scheduler / Services / Winlogon startup locations skipped per spec; add when startup scope expands beyond Run keys and folders.
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string StartupApprovedRun32KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
    private const string StartupApprovedFolderKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
    private const string ManagedPrefix = "Miliano's App - ";
    private const int MaxEntriesPerLocation = 500;

    private readonly RegistryKey _currentUserRoot;
    private readonly RegistryKey _localMachine64Root;
    private readonly RegistryKey? _localMachine32Root;
    private readonly string _userStartupFolderPath;
    private readonly string _commonStartupFolderPath;
    private readonly bool _ownsRoots;

    public StartupAppsService()
        : this(
            Registry.CurrentUser,
            RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default),
            Environment.Is64BitOperatingSystem ? RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32) : null,
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
            ownsRoots: true)
    {
    }

    internal StartupAppsService(
        RegistryKey currentUserRoot,
        RegistryKey localMachine64Root,
        RegistryKey? localMachine32Root,
        string? userStartupFolderPath = null,
        string? commonStartupFolderPath = null,
        bool ownsRoots = false)
    {
        _currentUserRoot = currentUserRoot ?? throw new ArgumentNullException(nameof(currentUserRoot));
        _localMachine64Root = localMachine64Root ?? throw new ArgumentNullException(nameof(localMachine64Root));
        _localMachine32Root = localMachine32Root;
        _userStartupFolderPath = userStartupFolderPath ?? "";
        _commonStartupFolderPath = commonStartupFolderPath ?? "";
        _ownsRoots = ownsRoots;
    }

    public void Dispose()
    {
        if (_ownsRoots)
        {
            _localMachine64Root.Dispose();
            if (_localMachine32Root != null && !ReferenceEquals(_localMachine64Root, _localMachine32Root))
            {
                _localMachine32Root.Dispose();
            }
        }
    }

    public StartupAppsSnapshot GetStartupApps()
    {
        var entries = new List<StartupAppEntry>();

        // 1. HKCU Run (shared view on 64-bit Windows; read once to prevent duplication)
        entries.AddRange(ReadRunKey(_currentUserRoot, "HKCU", "Shared", "HKCU Run", StartupApprovedRunKeyPath));

        // 2. HKLM Run 64-bit (or Default on 32-bit OS)
        string hklm64Source = _localMachine32Root != null ? "HKLM Run (64-bit)" : "HKLM Run";
        string hklm64View = _localMachine32Root != null ? "Registry64" : "Registry32";
        entries.AddRange(ReadRunKey(_localMachine64Root, "HKLM", hklm64View, hklm64Source, StartupApprovedRunKeyPath));

        // 3. HKLM Run 32-bit (only on 64-bit OS / when separate 32-bit root is present)
        if (_localMachine32Root != null && !ReferenceEquals(_localMachine64Root, _localMachine32Root))
        {
            entries.AddRange(ReadRunKey(_localMachine32Root, "HKLM", "Registry32", "HKLM Run (32-bit)", StartupApprovedRun32KeyPath));
        }

        // 4. User Startup folder
        entries.AddRange(ReadStartupFolder(
            _userStartupFolderPath,
            "Startup folder",
            "HKCU",
            StartupApprovedFolderKeyPath));

        // 5. Common Startup folder
        entries.AddRange(ReadStartupFolder(
            _commonStartupFolderPath,
            "Common startup folder",
            "HKLM",
            StartupApprovedFolderKeyPath));

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

    public StartupAppEntry AddManagedStartupApp(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Percorso mancante.", nameof(path));

        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (!File.Exists(path))
            throw new FileNotFoundException("File non trovato.", path);

        string ext = System.IO.Path.GetExtension(path);
        if (!IsAllowedStartupFile(ext))
            throw new ArgumentException("Sono supportati file .exe, .lnk, .bat e .cmd.");

        using var runKey = _currentUserRoot.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Impossibile aprire la chiave di avvio utente.");

        string valueName = CreateUniqueManagedValueName(runKey, path);
        string command = Quote(path);
        runKey.SetValue(valueName, command, RegistryValueKind.String);

        // If Windows previously tracked the same startup value as disabled, mark it enabled.
        SetStartupApproved(_currentUserRoot, StartupApprovedRunKeyPath, valueName, enabled: true);

        return new StartupAppEntry
        {
            Id = valueName,
            Name = valueName,
            Command = command,
            Path = path,
            Source = "HKCU Run",
            Enabled = true,
            IsManaged = true,
            Hive = "HKCU",
            View = "Shared",
            StateKnown = true,
        };
    }

    public bool SetStartupAppEnabled(string id, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("ID mancante.", nameof(id));

        var entry = FindStartupAppById(id)
            ?? throw new InvalidOperationException($"Applicazione di avvio con ID '{id}' non trovata.");

        string valueName = DecodeStartupValueName(entry);

        RegistryKey targetRoot;
        string approvalSubKey;
        RegistryKey? secondaryRoot = null;
        bool isHklm = false;

        if (entry.IsManaged || entry.Source == "HKCU Run")
        {
            targetRoot = _currentUserRoot;
            approvalSubKey = StartupApprovedRunKeyPath;
        }
        else if (entry.Source == "HKLM Run (32-bit)")
        {
            targetRoot = _localMachine64Root;
            approvalSubKey = StartupApprovedRun32KeyPath;
            secondaryRoot = _currentUserRoot;
            isHklm = true;
        }
        else if (entry.Source is "HKLM Run" or "HKLM Run (64-bit)")
        {
            targetRoot = _localMachine64Root;
            approvalSubKey = StartupApprovedRunKeyPath;
            secondaryRoot = _currentUserRoot;
            isHklm = true;
        }
        else if (entry.Source == "Startup folder")
        {
            targetRoot = _currentUserRoot;
            approvalSubKey = StartupApprovedFolderKeyPath;
        }
        else if (entry.Source == "Common startup folder")
        {
            targetRoot = _localMachine64Root;
            approvalSubKey = StartupApprovedFolderKeyPath;
            secondaryRoot = _currentUserRoot;
            isHklm = true;
        }
        else
        {
            throw new InvalidOperationException($"Origine applicazione di avvio '{entry.Source}' non supportata.");
        }

        // 1. Write the approval setting to the primary target.
        // If non-elevated and targeting machine root, fall back to writing per-user override in HKCU.
        bool wrotePrimary = false;
        try
        {
            SetStartupApproved(targetRoot, approvalSubKey, valueName, enabled);
            wrotePrimary = true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (secondaryRoot != null)
            {
                SetStartupApproved(secondaryRoot, approvalSubKey, valueName, enabled);
            }
            else
            {
                throw;
            }
        }

        // 2. If primary write succeeded and secondary root has an existing override for this value, sync it
        if (wrotePrimary && secondaryRoot != null)
        {
            TrySyncSecondaryApproval(secondaryRoot, approvalSubKey, valueName, enabled);
        }

        // 3. Re-read and verify that the setting took effect according to Windows resolution logic
        var (verifiedEnabled, verifiedKnown) = ResolveApproval(approvalSubKey, valueName, isHklm);

        if (!verifiedKnown || verifiedEnabled != enabled)
        {
            throw new InvalidOperationException(
                $"Verifica dello stato di avvio per '{valueName}' in '{approvalSubKey}' non riuscita. Valore atteso: {enabled}.");
        }

        return true;
    }

    public bool RemoveManagedStartupApp(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !id.StartsWith(ManagedPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("È possibile rimuovere solo app gestite da Miliano's App.");

        using var runKey = _currentUserRoot.OpenSubKey(RunKeyPath, writable: true);
        if (runKey == null) return false;

        if (runKey.GetValueNames().Any(n => string.Equals(n, id, StringComparison.Ordinal)))
        {
            runKey.DeleteValue(id, throwOnMissingValue: false);
            DeleteStartupApproved(_currentUserRoot, StartupApprovedRunKeyPath, id);
            return true;
        }

        return false;
    }

    private StartupAppEntry? FindStartupAppById(string id)
    {
        var snapshot = GetStartupApps();
        var all = snapshot.Enabled.Concat(snapshot.Disabled);

        var match = all.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
        if (match != null) return match;

        // Legacy format fallback: "HKLM Run:AppName"
        if (id.StartsWith("HKLM Run:", StringComparison.OrdinalIgnoreCase))
        {
            string legacyName = id["HKLM Run:".Length..];
            match = all.FirstOrDefault(e =>
                string.Equals(e.Hive, "HKLM", StringComparison.OrdinalIgnoreCase)
                && string.Equals(e.Name, legacyName, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }

        // Unique name fallback
        var matchesByName = all.Where(e => string.Equals(e.Name, id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matchesByName.Count == 1)
            return matchesByName[0];

        return null;
    }

    private static string DecodeStartupValueName(StartupAppEntry entry)
    {
        if (entry.IsManaged)
            return entry.Id;

        if (entry.Source is "Startup folder" or "Common startup folder")
        {
            if (!string.IsNullOrWhiteSpace(entry.Path))
                return System.IO.Path.GetFileName(entry.Path);

            string prefix = entry.Source + ":";
            if (entry.Id.StartsWith(prefix, StringComparison.Ordinal))
                return entry.Id[prefix.Length..];
        }

        return entry.Name;
    }

    private IEnumerable<StartupAppEntry> ReadRunKey(
        RegistryKey root,
        string hive,
        string view,
        string source,
        string approvalSubKey)
    {
        var entries = new List<StartupAppEntry>();

        try
        {
            using var key = root.OpenSubKey(RunKeyPath, writable: false);
            if (key == null) return entries;

            string[] valueNames = key.GetValueNames();
            int count = Math.Min(valueNames.Length, MaxEntriesPerLocation);

            for (int i = 0; i < count; i++)
            {
                string valueName = valueNames[i];
                if (string.IsNullOrEmpty(valueName)) continue;

                string command = key.GetValue(valueName)?.ToString() ?? "";
                bool isManaged = hive == "HKCU" && valueName.StartsWith(ManagedPrefix, StringComparison.Ordinal);
                bool isHklm = hive == "HKLM";

                var (enabled, stateKnown) = ResolveApproval(approvalSubKey, valueName, isHklm);

                string id = isManaged
                    ? valueName
                    : $"{source}:{valueName}";

                entries.Add(new StartupAppEntry
                {
                    Id = id,
                    Name = valueName,
                    Command = command,
                    Path = ExtractExecutablePath(command),
                    Source = source,
                    Enabled = enabled,
                    IsManaged = isManaged,
                    Hive = hive,
                    View = view,
                    StateKnown = stateKnown,
                });
            }
        }
        catch
        {
            // Startup inventory is best-effort; unreadable locations are skipped.
        }

        return entries;
    }

    private IEnumerable<StartupAppEntry> ReadStartupFolder(
        string folderPath,
        string source,
        string hive,
        string approvalSubKey)
    {
        var entries = new List<StartupAppEntry>();

        try
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
                return entries;

            int count = 0;
            foreach (string path in Directory.EnumerateFiles(folderPath))
            {
                if (++count > MaxEntriesPerLocation) break;

                string fileName = System.IO.Path.GetFileName(path);
                if (string.IsNullOrEmpty(fileName)) continue;

                bool isHklm = hive == "HKLM";
                var (enabled, stateKnown) = ResolveApproval(approvalSubKey, fileName, isHklm);

                entries.Add(new StartupAppEntry
                {
                    Id = $"{source}:{fileName}",
                    Name = System.IO.Path.GetFileNameWithoutExtension(path),
                    Command = path,
                    Path = path,
                    Source = source,
                    Enabled = enabled,
                    IsManaged = false,
                    Hive = hive,
                    View = null,
                    StateKnown = stateKnown,
                });
            }
        }
        catch
        {
            // Startup inventory is best-effort; unreadable locations are skipped.
        }

        return entries;
    }

    private (bool Enabled, bool Known) ResolveApproval(
        string approvalSubKey,
        string valueName,
        bool isHklm)
    {
        // 1. Check HKCU approval first (user preference overrides machine setting in Windows)
        byte[]? userRaw = ReadApprovalBytes(_currentUserRoot, approvalSubKey, valueName);
        if (userRaw != null && userRaw.Length > 0 && userRaw[0] != 0x00)
        {
            var userState = DetermineApprovalState(userRaw);
            if (!userState.Enabled)
                return userState;

            if (!isHklm)
                return userState;
        }

        // 2. For HKLM items, check HKLM approval
        if (isHklm)
        {
            byte[]? machineRaw = ReadApprovalBytes(_localMachine64Root, approvalSubKey, valueName);
            if (machineRaw != null && machineRaw.Length > 0 && machineRaw[0] != 0x00)
            {
                var machineState = DetermineApprovalState(machineRaw);
                if (!machineState.Enabled)
                    return machineState;

                return machineState;
            }

            if (userRaw != null && userRaw.Length > 0 && userRaw[0] != 0x00)
            {
                return DetermineApprovalState(userRaw);
            }
        }

        // Windows defaults Run entries to enabled when no explicit approval entry exists
        return (true, false);
    }

    private static byte[]? ReadApprovalBytes(RegistryKey root, string subKey, string valueName)
    {
        try
        {
            using var key = root.OpenSubKey(subKey, writable: false);
            return key?.GetValue(valueName) as byte[];
        }
        catch
        {
            return null;
        }
    }

    private static (bool Enabled, bool Known) DetermineApprovalState(byte[]? raw)
    {
        if (raw == null || raw.Length == 0)
            return (true, false);

        byte b = raw[0];
        if (b == 0x02 || b == 0x06) return (true, true);
        if (b == 0x03 || b == 0x07) return (false, true);
        if (b == 0x00) return (true, false);

        return ((b & 1) == 0, true);
    }

    private static void SetStartupApproved(RegistryKey root, string keyPath, string valueName, bool enabled)
    {
        using var key = root.CreateSubKey(keyPath, writable: true)
            ?? throw new InvalidOperationException($"Impossibile aprire o creare la chiave di registro '{keyPath}'.");

        byte[] existing = (key.GetValue(valueName) as byte[]) ?? Array.Empty<byte>();
        byte[] payload = BuildStartupApprovedPayload(existing, enabled);
        key.SetValue(valueName, payload, RegistryValueKind.Binary);
    }

    private static void TrySyncSecondaryApproval(RegistryKey secondaryRoot, string subKey, string valueName, bool enabled)
    {
        try
        {
            using var readCheck = secondaryRoot.OpenSubKey(subKey, writable: false);
            if (readCheck?.GetValue(valueName) != null)
            {
                using var writableKey = secondaryRoot.CreateSubKey(subKey, writable: true);
                if (writableKey != null)
                {
                    byte[] existing = (writableKey.GetValue(valueName) as byte[]) ?? Array.Empty<byte>();
                    byte[] payload = BuildStartupApprovedPayload(existing, enabled);
                    writableKey.SetValue(valueName, payload, RegistryValueKind.Binary);
                }
            }
        }
        catch
        {
            // Secondary sync is best-effort to avoid shadowing the primary machine setting
        }
    }

    private static byte[] BuildStartupApprovedPayload(byte[] existing, bool enabled)
    {
        byte[] payload = new byte[Math.Max(12, existing.Length)];
        if (existing.Length > 0)
        {
            Array.Copy(existing, payload, existing.Length);
        }

        payload[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (existing.Length < 4)
        {
            payload[1] = 0x00;
            payload[2] = 0x00;
            payload[3] = 0x00;
        }

        byte[] timestamp = BitConverter.GetBytes(DateTime.UtcNow.ToFileTime());
        Array.Copy(timestamp, 0, payload, 4, 8);

        return payload;
    }

    private static void DeleteStartupApproved(RegistryKey root, string keyPath, string valueName)
    {
        try
        {
            using var key = root.OpenSubKey(keyPath, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }
        catch
        {
            // Non-critical cleanup
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
            int endQuote = command.IndexOf('"', 1);
            return endQuote > 1 ? command.Substring(1, endQuote - 1) : command.Trim('"');
        }

        int exeIndex = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex >= 0)
            return command.Substring(0, exeIndex + 4);

        int firstSpace = command.IndexOf(' ');
        return firstSpace > 0 ? command.Substring(0, firstSpace) : command;
    }
}
