using System.IO;
using System.Text;

namespace VoltManager.Services;

/// <summary>
/// Lightweight, thread-safe, file-based logger. Never throws: logging must
/// never become a new failure path. Writes to
/// <c>%APPDATA%\VoltManager\logs\voltmanager.log</c> with size-based rotation
/// (one prior generation kept as <c>voltmanager.1.log</c>).
/// </summary>
public static class Logger
{
    private const long MaxBytes = 1024 * 1024; // 1 MB before rotation.

    private static readonly object Lock = new();
    private static string? _path;
    private static bool _initialized;

    public static string? LogFilePath => _path;

    /// <summary>Resolves the log path and records the session header. Safe to call once at startup.</summary>
    public static void Init()
    {
        lock (Lock)
        {
            if (_initialized) return;
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "VoltManager", "logs");
                Directory.CreateDirectory(dir);
                _path = Path.Combine(dir, "voltmanager.log");
            }
            catch
            {
                _path = null; // Logging stays a no-op rather than crashing the app.
            }
            _initialized = true;
        }

        Info($"=== VoltManager session start (pid {Environment.ProcessId}) ===");
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message) => Write("WARN", message, null);

    public static void Error(string message) => Write("ERROR", message, null);

    public static void Error(string message, Exception ex) => Write("ERROR", message, ex);

    public static void Error(Exception ex) => Write("ERROR", ex.Message, ex);

    /// <summary>
    /// Logs a warning only on the FIRST failure of a streak — when
    /// <paramref name="faulted"/> is still false — so a persistently-failing hot
    /// loop leaves one trace instead of spamming the log. Returns true; store it
    /// (<c>_faulted = Logger.WarnOnce(_faulted, msg, ex);</c>) and reset the field
    /// to false on the next success for a silent recovery.
    /// </summary>
    public static bool WarnOnce(bool faulted, string message, Exception? ex = null)
    {
        if (!faulted) Write("WARN", message, ex);
        return true;
    }

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            if (!_initialized) Init();
            if (_path == null) return;

            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
              .Append(" [").Append(level).Append("] ")
              .Append(message);
            if (ex != null)
            {
                sb.AppendLine();
                sb.Append(ex);
            }
            sb.AppendLine();

            lock (Lock)
            {
                Rotate();
                File.AppendAllText(_path, sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Never let logging throw.
        }
    }

    // Caller holds Lock.
    private static void Rotate()
    {
        try
        {
            if (_path == null) return;
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length < MaxBytes) return;

            var rolled = Path.ChangeExtension(_path, ".1.log");
            if (File.Exists(rolled)) File.Delete(rolled);
            File.Move(_path, rolled);
        }
        catch
        {
            // Rotation is best-effort; keep appending to the current file.
        }
    }
}
