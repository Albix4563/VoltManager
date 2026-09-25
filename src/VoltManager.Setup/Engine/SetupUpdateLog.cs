using System;
using System.IO;

namespace VoltManager.Setup.Engine
{
    /// <summary>
    /// Append-only log for the unattended <c>/update</c> path, which has no window of its
    /// own: failures must stay diagnosable after the setup process has exited. Stored
    /// next to the app log in <c>%APPDATA%\VoltManager\logs\setup-update.log</c>.
    /// </summary>
    internal static class SetupUpdateLog
    {
        private static readonly object Gate = new object();

        public static string FilePath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VoltManager", "logs", "setup-update.log");

        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARN", message);
        public static void Error(string message) => Write("ERROR", message);

        private static void Write(string level, string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    File.AppendAllText(FilePath,
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // Logging must never turn a recoverable update into a failed one.
            }
        }
    }
}
