using System.Security.Cryptography;
using System.Text.Json;

namespace VoltManager.Supervisor;

public sealed class SupervisorPaths
{
    public required string StateFile { get; init; }
    public required string EventLogFile { get; init; }

    public static SupervisorPaths CreateDefault()
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VoltManager");

        return new SupervisorPaths
        {
            StateFile = Path.Combine(root, "supervisor", "state.json"),
            EventLogFile = Path.Combine(root, "logs", "supervisor-events.jsonl"),
        };
    }
}

public sealed class JsonSupervisorEventSink : ISupervisorEventSink
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly string _path;

    public JsonSupervisorEventSink(string path) => _path = path;

    public void Write(string eventName, object? fields = null)
    {
        try
        {
            var payload = new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                eventName,
                processId = Environment.ProcessId,
                fields,
            };

            string line = JsonSerializer.Serialize(payload) + Environment.NewLine;
            lock (_gate)
            {
                string? directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                RotateIfNeeded();
                File.AppendAllText(_path, line);
            }
        }
        catch
        {
            // The supervisor must never fail because diagnostics are unavailable.
        }
    }

    private void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length < MaxBytes)
                return;

            string rolled = Path.ChangeExtension(_path, ".1.jsonl");
            if (File.Exists(rolled))
                File.Delete(rolled);
            File.Move(_path, rolled);
        }
        catch
        {
            // Best effort: continue appending to the active file.
        }
    }
}

public sealed class FileSupervisorStateStore : ISupervisorStateStore
{
    private readonly string _path;
    private readonly ISupervisorEventSink _events;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public FileSupervisorStateStore(string path, ISupervisorEventSink events)
    {
        _path = path;
        _events = events;
    }

    public SupervisorState Load()
    {
        try
        {
            if (!File.Exists(_path))
                return new SupervisorState();

            string json = File.ReadAllText(_path);
            var state = JsonSerializer.Deserialize<SupervisorState>(json) ?? new SupervisorState();
            state.CrashTimesUtc ??= new List<DateTimeOffset>();
            return state;
        }
        catch (Exception ex)
        {
            QuarantineCorruptState(ex.GetType().Name);
            return new SupervisorState();
        }
    }

    public void Save(SupervisorState state)
    {
        string temporary = _path + ".tmp." + Environment.ProcessId;
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            string backup = _path + ".bak";
            string json = JsonSerializer.Serialize(state, _jsonOptions);

            File.WriteAllText(temporary, json);
            if (File.Exists(_path))
            {
                try
                {
                    File.Replace(temporary, _path, backup, ignoreMetadataErrors: true);
                    TryDelete(backup);
                }
                catch
                {
                    File.Copy(temporary, _path, overwrite: true);
                }
            }
            else
            {
                File.Move(temporary, _path);
            }
        }
        catch (Exception ex)
        {
            // Keep the in-memory restart budget active. Disk failures must not turn
            // a child crash into a supervisor crash or an uncontrolled immediate loop.
            _events.Write("state_save_failed", new { exceptionType = ex.GetType().FullName });
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public void Reset()
    {
        TryDelete(_path);
        TryDelete(_path + ".bak");
        foreach (string temporary in Directory.Exists(Path.GetDirectoryName(_path))
                     ? Directory.GetFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + ".tmp.*")
                     : Array.Empty<string>())
        {
            TryDelete(temporary);
        }
    }

    private void QuarantineCorruptState(string errorType)
    {
        try
        {
            if (!File.Exists(_path))
                return;

            string quarantine = _path + ".corrupt." + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
            File.Move(_path, quarantine);
            _events.Write("state_corrupt_quarantined", new { errorType, quarantineFile = Path.GetFileName(quarantine) });
        }
        catch
        {
            _events.Write("state_corrupt_unreadable", new { errorType });
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }
}

public sealed class CryptoJitterSource : IJitterSource
{
    public double NextUnit() => RandomNumberGenerator.GetInt32(0, 1_000_001) / 1_000_000d;
}
