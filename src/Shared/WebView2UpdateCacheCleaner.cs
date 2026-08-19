using System;
using System.IO;
using System.Threading;

namespace VoltManager.Services
{
    /// <summary>
    /// Removes only VoltManager's disposable WebView2 user-data profile.
    /// User settings and other application data live alongside this directory
    /// and are intentionally left untouched.
    /// </summary>
    public static class WebView2UpdateCacheCleaner
    {
        private const int MaxAttempts = 8;
        private const int BaseRetryDelayMs = 200;

        public static string DefaultUserDataDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VoltManager",
            "WebView2");

        public static bool TryClearDefault(out string error)
            => TryClear(DefaultUserDataDirectory, out error);

        public static bool TryClear(string userDataDirectory, out string error)
        {
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(userDataDirectory))
            {
                error = "WebView2 user-data directory is empty.";
                return false;
            }

            if (!Directory.Exists(userDataDirectory))
                return true;

            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                if (!Directory.Exists(userDataDirectory))
                    return true;

                try
                {
                    MakeWritableTree(userDataDirectory);
                    Directory.Delete(userDataDirectory, recursive: true);
                    if (!Directory.Exists(userDataDirectory))
                        return true;

                    error = "WebView2 user-data directory still exists after deletion.";
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    error = ex.Message;
                }

                if (attempt < MaxAttempts)
                    Thread.Sleep(BaseRetryDelayMs * attempt);
            }

            return !Directory.Exists(userDataDirectory);
        }

        private static void MakeWritableTree(string directory)
        {
            foreach (string file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
                MakeWritable(file);

            foreach (string childDirectory in Directory.GetDirectories(directory, "*", SearchOption.AllDirectories))
                MakeWritable(childDirectory);

            MakeWritable(directory);
        }

        private static void MakeWritable(string path)
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }
}
