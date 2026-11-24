using System;
using System.IO;
using System.Security;
using System.Text;

namespace VoltManager.Setup.Engine
{
    internal sealed class SetupLogReader
    {
        private const int MaxTailBytes = 32 * 1024;

        private static readonly string AppLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VoltManager", "logs", "voltmanager.log");

        private readonly string _updatePath;
        private readonly string _appPath;

        internal SetupLogReader(string? updatePath = null, string? appPath = null)
        {
            _updatePath = updatePath ?? SetupUpdateLog.FilePath;
            _appPath = appPath ?? AppLogPath;
        }

        // ponytail: legge soltanto la coda del file corrente; per consultare lo storico serve un selettore separato.
        internal string Read(bool applicationLog)
        {
            try
            {
                using var stream = new FileStream(applicationLog ? _appPath : _updatePath,
                    FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                long length = stream.Length;
                bool truncated = length > MaxTailBytes;
                if (truncated)
                    stream.Position = length - MaxTailBytes;
                using var reader = new BinaryReader(stream);
                byte[] bytes = reader.ReadBytes((int)Math.Min(length, MaxTailBytes));
                int start = 0;
                if (truncated)
                {
                    int newline = Array.IndexOf(bytes, (byte)'\n');
                    if (newline >= 0 && newline < bytes.Length - 1)
                        start = newline + 1;
                    else
                    {
                        while (start < bytes.Length && (bytes[start] & 0xC0) == 0x80)
                            start++;
                    }
                }
                else if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                {
                    start = 3;
                }

                string text = Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
                if (string.IsNullOrWhiteSpace(text))
                    return I18n.T("update_log_empty");
                return truncated ? "…\n" + text : text;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return I18n.T("update_log_missing");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                return I18n.T("update_log_unreadable");
            }
        }
    }
}
