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
    private const string StartupApprovedRun32KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
    private const string StartupApprovedFolderKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
    private const string ManagedPrefix = "Miliano's App - ";
    private readonly IStartupRegistryAccessor _registry;
    private readonly Func<Environment.SpecialFolder, string> _startupFolderPath;
    private readonly Func<string, bool> _fileExists;

    public StartupAppsService() : this(new WindowsStartupRegistryAccessor(), Environment.GetFolderPath, File.Exists)
    {
    }

    internal StartupAppsService(IStartupRegistryAccessor registry, Func<Environment.SpecialFolder, string> startupFolderPath)
        : this(registry, startupFolderPath, File.Exists)
    {
    }

    internal StartupAppsService(
        IStartupRegistryAccessor registry,
        Func<Environment.SpecialFolder, string> startupFolderPath,
        Func<string, bool> fileExists)
    {
        _registry = registry;
        _startupFolderPath = startupFolderPath;
        _fileExists = fileExists;
    }

    public StartupAppsSnapshot GetStartupApps()
    {
        var entries = new List<StartupAppEntry>();

        entries.AddRange(ReadRunKey(RegistryHive.CurrentUser, RegistryView.Default, "HKCU Run", StartupApprovedRunKeyPath, RegistryView.Default));
        entries.AddRange(ReadRunKey(RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM Run", StartupApprovedRunKeyPath, RegistryView.Registry64));
        entries.AddRange(ReadRunKey(RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM Run32", StartupApprovedRun32KeyPath, RegistryView.Registry64));
        entries.AddRange(ReadStartupFolder(
            _startupFolderPath(Environment.SpecialFolder.Startup),
            "Startup folder",
            RegistryHive.CurrentUser));
        entries.AddRange(ReadStartupFolder(
            _startupFolderPath(Environment.SpecialFolder.CommonStartup),
            "Common startup folder",
            RegistryHive.LocalMachine));

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
            throw new ArgumentException("Percorso mancante.");

        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (!_fileExists(path))
            throw new FileNotFoundException("File non trovato.", path);

        string ext = System.IO.Path.GetExtension(path);
        if (!IsAllowedStartupFile(ext))
            throw new ArgumentException("Sono supportati file .exe, .lnk, .bat e .cmd.");

        var runValues = _registry.ReadValues(RegistryHive.CurrentUser, RegistryView.Default, RunKeyPath);
        string valueName = CreateUniqueManagedValueName(runValues, path);
        string command = Quote(path);
        bool alreadyExists = runValues.Any(value =>
            string.Equals(value.Name, valueName, StringComparison.OrdinalIgnoreCase));
        if (alreadyExists)
        {
            StartupRegistryValue? existing = _registry.ReadStoredValue(
                RegistryHive.CurrentUser, RegistryView.Default, RunKeyPath, valueName);
            if (existing is not { Kind: RegistryValueKind.String, Value: string currentCommand } ||
                !string.Equals(currentCommand, command, StringComparison.Ordinal))
                throw new InvalidOperationException("La voce Run esistente non corrisponde al comando gestito.");
        }
        if (!alreadyExists)
        {
            // A second check in the accessor refuses an entry inserted after
            // ReadValues. RegistryKey offers no atomic create-if-absent operation.
            // If the write throws, its commit status is unknown. Never delete
            // by matching command alone: another actor may own identical bytes.
            // Propagate the error; do not claim a successful addition or rollback.
            bool created = _registry.TryWriteStringValueIfAbsent(
                RegistryHive.CurrentUser, RegistryView.Default, RunKeyPath, valueName, command);
            if (!created)
                throw new InvalidOperationException("La voce Run è stata creata da un altro processo durante l'aggiunta.");
        }

        StartupRegistryValue? originalApproval = null;
        bool approvalWriteAttempted = false;
        try
        {
            originalApproval = _registry.ReadStoredValue(
                RegistryHive.CurrentUser, RegistryView.Default, StartupApprovedRunKeyPath, valueName);
            // A repeat add must not reset Windows' existing approval metadata.
            if (!alreadyExists || !IsEnabledApproval(originalApproval))
            {
                approvalWriteAttempted = true;
                SetStartupApproval(RegistryHive.CurrentUser, RegistryView.Default,
                    StartupApprovedRunKeyPath, valueName, enabled: true);
            }

            StartupRegistryValue? actualRun = _registry.ReadStoredValue(
                RegistryHive.CurrentUser, RegistryView.Default, RunKeyPath, valueName);
            if (actualRun is not { Kind: RegistryValueKind.String, Value: string currentCommand } ||
                !string.Equals(currentCommand, command, StringComparison.Ordinal))
                throw new InvalidOperationException("La voce Run è cambiata durante l'aggiunta; l'avvio non è confermato.");

            if (!IsEnabledApproval(_registry.ReadStoredValue(
                RegistryHive.CurrentUser, RegistryView.Default, StartupApprovedRunKeyPath, valueName)))
                throw new InvalidOperationException("Lo stato StartupApproved non risulta abilitato dopo l'aggiunta.");
        }
        catch (Exception approvalError)
        {
            var rollbackErrors = new List<Exception>();
            if (approvalWriteAttempted)
            {
                try
                {
                    if (!_registry.TryRestoreApprovalIfMatches(
                        RegistryHive.CurrentUser, RegistryView.Default, StartupApprovedRunKeyPath,
                        valueName, EnabledStartupApprovalState, originalApproval))
                        throw new InvalidOperationException("StartupApproved è cambiato durante il ripristino.");
                }
                catch (Exception rollbackError) { rollbackErrors.Add(rollbackError); }
            }
            if (!alreadyExists)
            {
                try
                {
                    if (!_registry.TryDeleteStringValueIfMatches(RegistryHive.CurrentUser, RegistryView.Default, RunKeyPath, valueName, command))
                        throw new InvalidOperationException("La voce di avvio è stata modificata durante il ripristino e non è stata rimossa.");
                }
                catch (Exception rollbackError) { rollbackErrors.Add(rollbackError); }
            }

            if (rollbackErrors.Count != 0)
                throw new AggregateException("Impossibile approvare l'avvio e ripristinare completamente lo stato.",
                    new[] { approvalError }.Concat(rollbackErrors));
            throw;
        }

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

    public bool SetStartupAppEnabled(string id, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("ID mancante.");

        var entry = FindStartupAppById(id)
            ?? throw new InvalidOperationException("Applicazione di avvio non trovata.");

        string valueName = DecodeStartupValueName(entry);

        (RegistryHive hive, RegistryView view, string approvalKeyPath) = entry.Source switch
        {
            "HKCU Run" => (RegistryHive.CurrentUser, RegistryView.Default, StartupApprovedRunKeyPath),
            "HKLM Run" => (RegistryHive.LocalMachine, RegistryView.Registry64, StartupApprovedRunKeyPath),
            "HKLM Run32" => (RegistryHive.LocalMachine, RegistryView.Registry64, StartupApprovedRun32KeyPath),
            "Startup folder" => (RegistryHive.CurrentUser, RegistryView.Default, StartupApprovedFolderKeyPath),
            "Common startup folder" => (RegistryHive.LocalMachine, RegistryView.Registry64, StartupApprovedFolderKeyPath),
            _ => throw new InvalidOperationException("Origine applicazione di avvio non supportata."),
        };

        StartupRegistryValue? before = _registry.ReadStoredValue(hive, view, approvalKeyPath, valueName);
        if (before != null)
        {
            if (before is not { Kind: RegistryValueKind.Binary, Value: byte[] { Length: > 0 } raw } ||
                raw[0] is not (0x02 or 0x03 or 0x06 or 0x07))
                throw new InvalidOperationException("Formato StartupApproved sconosciuto: modifica rifiutata.");

            bool currentlyEnabled = raw[0] is 0x02 or 0x06;
            if (currentlyEnabled == enabled)
            {
                // Already in the requested state. Never discard Windows' extended
                // approval metadata or reset timestamps by rewriting generic bytes.
                StartupRegistryValue? stillPresent =
                    _registry.ReadStoredValue(hive, view, approvalKeyPath, valueName);
                if (stillPresent is not { Kind: RegistryValueKind.Binary, Value: byte[] persisted } ||
                    !persisted.SequenceEqual(raw))
                    throw new InvalidOperationException("StartupApproved è cambiato durante la verifica.");
                return true;
            }

            // 0x06/0x07 encode an extended Windows state. Their transition
            // metadata semantics are not established; never synthesize 0x02/0x03.
            if (raw[0] is 0x06 or 0x07)
                throw new InvalidOperationException(
                    "Transizione StartupApproved 0x06/0x07 non supportata senza una codifica verificata.");
        }

        byte[] expected = enabled
            ? EnabledStartupApprovalState.ToArray()
            : new byte[] { 0x03, 0, 0, 0, 0, 0, 0, 0 };
        _registry.WriteValue(hive, view, approvalKeyPath, valueName, expected, RegistryValueKind.Binary);
        StartupRegistryValue? after = _registry.ReadStoredValue(hive, view, approvalKeyPath, valueName);
        if (after is not { Kind: RegistryValueKind.Binary, Value: byte[] actual } ||
            !actual.SequenceEqual(expected))
            throw new InvalidOperationException("StartupApproved non conferma la modifica richiesta.");

        return true;
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

    private StartupAppEntry? FindStartupAppById(string id)
    {
        var snapshot = GetStartupApps();
        return snapshot.Enabled.Concat(snapshot.Disabled)
            .FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));
    }

    private static string DecodeStartupValueName(StartupAppEntry entry)
    {
        if (entry.IsManaged)
            return entry.Id;

        string prefix = entry.Source + ":";
        if (entry.Id.StartsWith(prefix, StringComparison.Ordinal))
            return entry.Id[prefix.Length..];

        return entry.Name;
    }

    private IEnumerable<StartupAppEntry> ReadRunKey(RegistryHive hive, RegistryView view, string source, string approvalKeyPath, RegistryView approvalView)
    {
        var entries = new List<StartupAppEntry>();

        try
        {
            foreach (var value in _registry.ReadValues(hive, view, RunKeyPath))
            {
                string valueName = value.Name;
                string command = value.Value?.ToString() ?? "";
                bool enabled = IsStartupApproved(hive, approvalView, approvalKeyPath, valueName, defaultEnabled: true);
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

    private IEnumerable<StartupAppEntry> ReadStartupFolder(string folderPath, string source, RegistryHive approvalHive)
    {
        var entries = new List<StartupAppEntry>();

        try
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
                return entries;

            foreach (string path in Directory.EnumerateFiles(folderPath))
            {
                string fileName = System.IO.Path.GetFileName(path);
                bool enabled = IsStartupApproved(approvalHive, RegistryView.Default, StartupApprovedFolderKeyPath, fileName, defaultEnabled: true);

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

    private bool IsStartupApproved(RegistryHive hive, RegistryView view, string keyPath, string valueName, bool defaultEnabled)
    {
        try
        {
            if (_registry.ReadValue(hive, view, keyPath, valueName) is not byte[] state || state.Length == 0)
                return defaultEnabled;

            return state[0] switch
            {
                0x02 or 0x06 => true,
                0x03 or 0x07 => false,
                _ => defaultEnabled,
            };
        }
        catch
        {
            return defaultEnabled;
        }
    }

    private void SetStartupApproval(RegistryHive hive, RegistryView view, string keyPath, string valueName, bool enabled)
    {
        _registry.WriteValue(hive, view, keyPath, valueName, enabled
            ? EnabledStartupApprovalState.ToArray()
            : new byte[] { 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 },
            RegistryValueKind.Binary);
    }

    private static readonly byte[] EnabledStartupApprovalState = { 0x02, 0, 0, 0, 0, 0, 0, 0 };

    private static bool IsEnabledApproval(StartupRegistryValue? state)
        => state is { Kind: RegistryValueKind.Binary, Value: byte[] { Length: > 0 } bytes } &&
           (bytes[0] == 0x02 || bytes[0] == 0x06);

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

    private static string CreateUniqueManagedValueName(IReadOnlyList<(string Name, object? Value)> runValues, string path)
    {
        string baseName = System.IO.Path.GetFileNameWithoutExtension(path).Trim();
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "Custom App";

        string candidate = ManagedPrefix + baseName;
        string command = Quote(path);

        if (IsValueAvailableOrSame(runValues, candidate, command))
            return candidate;

        for (int i = 2; i < 100; i++)
        {
            string numbered = $"{candidate} ({i})";
            if (IsValueAvailableOrSame(runValues, numbered, command))
                return numbered;
        }

        throw new InvalidOperationException("Sono già presenti troppe app gestite con lo stesso nome.");
    }

    private static bool IsValueAvailableOrSame(IReadOnlyList<(string Name, object? Value)> runValues, string valueName, string command)
    {
        foreach (var existing in runValues)
        {
            if (string.Equals(existing.Name, valueName, StringComparison.OrdinalIgnoreCase))
                return existing.Value is string text && string.Equals(text, command, StringComparison.OrdinalIgnoreCase);
        }

        return true;
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

internal interface IStartupRegistryAccessor
{
    IReadOnlyList<(string Name, object? Value)> ReadValues(RegistryHive hive, RegistryView view, string keyPath);
    object? ReadValue(RegistryHive hive, RegistryView view, string keyPath, string valueName);
    void WriteValue(RegistryHive hive, RegistryView view, string keyPath, string valueName, byte[] state, RegistryValueKind kind);
    StartupRegistryValue? ReadStoredValue(RegistryHive hive, RegistryView view, string keyPath, string valueName);
    bool TryWriteStringValueIfAbsent(RegistryHive hive, RegistryView view, string keyPath, string valueName, string command);
    bool TryRestoreApprovalIfMatches(RegistryHive hive, RegistryView view, string keyPath,
        string valueName, byte[] expectedState, StartupRegistryValue? original);
    bool TryDeleteStringValueIfMatches(RegistryHive hive, RegistryView view, string keyPath, string valueName, string expectedCommand);
}

internal readonly record struct StartupRegistryValue(object? Value, RegistryValueKind Kind);

internal sealed class WindowsStartupRegistryAccessor : IStartupRegistryAccessor
{
    public IReadOnlyList<(string Name, object? Value)> ReadValues(RegistryHive hive, RegistryView view, string keyPath)
    {
        using var root = RegistryKey.OpenBaseKey(hive, view);
        using var key = root.OpenSubKey(keyPath);
        return key == null
            ? Array.Empty<(string Name, object? Value)>()
            : key.GetValueNames().Select(name => (name, key.GetValue(name))).ToList();
    }

    public object? ReadValue(RegistryHive hive, RegistryView view, string keyPath, string valueName)
    {
        using var root = RegistryKey.OpenBaseKey(hive, view);
        using var key = root.OpenSubKey(keyPath);
        return key?.GetValue(valueName);
    }

    public void WriteValue(RegistryHive hive, RegistryView view, string keyPath, string valueName, byte[] state, RegistryValueKind kind)
    {
        using var root = RegistryKey.OpenBaseKey(hive, view);
        using var key = root.CreateSubKey(keyPath, writable: true)
            ?? throw new InvalidOperationException("Impossibile aprire la chiave di approvazione dell'avvio.");
        key.SetValue(valueName, state, kind);
    }

    public StartupRegistryValue? ReadStoredValue(RegistryHive hive, RegistryView view, string keyPath, string valueName)
    {
        using var root = RegistryKey.OpenBaseKey(hive, view);
        using var key = root.OpenSubKey(keyPath);
        if (key == null || !key.GetValueNames().Any(name =>
            string.Equals(name, valueName, StringComparison.OrdinalIgnoreCase)))
            return null;

        return new StartupRegistryValue(
            key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames),
            key.GetValueKind(valueName));
    }

    public bool TryWriteStringValueIfAbsent(RegistryHive hive, RegistryView view, string keyPath, string valueName, string command)
    {
        using var root = RegistryKey.OpenBaseKey(hive, view);
        using var key = root.CreateSubKey(keyPath, writable: true)
            ?? throw new InvalidOperationException("Impossibile aprire la chiave di avvio utente.");
        // Best-effort non-atomic precheck: a concurrent writer can still win
        // between GetValueNames and SetValue; verified Windows CAS is unavailable.
        if (key.GetValueNames().Any(name => string.Equals(name, valueName, StringComparison.OrdinalIgnoreCase)))
            return false;
        key.SetValue(valueName, command, RegistryValueKind.String);
        return true;
    }

    public bool TryRestoreApprovalIfMatches(RegistryHive hive, RegistryView view, string keyPath,
        string valueName, byte[] expectedState, StartupRegistryValue? original)
    {
        using var root = RegistryKey.OpenBaseKey(hive, view);
        using var key = root.OpenSubKey(keyPath, writable: true);
        if (key == null)
            return original == null;

        bool exists = key.GetValueNames().Any(name => string.Equals(name, valueName, StringComparison.OrdinalIgnoreCase));
        StartupRegistryValue? current = exists
            ? new StartupRegistryValue(key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames),
                key.GetValueKind(valueName)) : null;

        if (SameValue(current, original)) return true;
        if (current is not { Kind: RegistryValueKind.Binary, Value: byte[] bytes } ||
            !bytes.SequenceEqual(expectedState))
            return false;

        if (original == null)
            key.DeleteValue(valueName, throwOnMissingValue: false);
        else if (original.Value.Value != null)
            key.SetValue(valueName, original.Value.Value, original.Value.Kind);
        else
            return false;
        return true;
    }

    private static bool SameValue(StartupRegistryValue? actual, StartupRegistryValue? expected)
    {
        if (actual == null || expected == null) return actual == null && expected == null;
        if (actual.Value.Kind != expected.Value.Kind) return false;
        return (actual.Value.Value, expected.Value.Value) switch
        {
            (byte[] x, byte[] y) => x.SequenceEqual(y),
            (string[] x, string[] y) => x.SequenceEqual(y),
            (object x, object y) => x.Equals(y),
            (null, null) => true,
            _ => false,
        };
    }

    public bool TryDeleteStringValueIfMatches(RegistryHive hive, RegistryView view, string keyPath, string valueName, string expectedCommand)
    {
        using var root = RegistryKey.OpenBaseKey(hive, view);
        using var key = root.OpenSubKey(keyPath, writable: true);
        if (key == null)
            return true;

        if (!key.GetValueNames().Any(name => string.Equals(name, valueName, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (key.GetValueKind(valueName) != RegistryValueKind.String ||
            !string.Equals(key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
                expectedCommand, StringComparison.Ordinal))
            return false;

        key.DeleteValue(valueName, throwOnMissingValue: false);
        return true;
    }
}
