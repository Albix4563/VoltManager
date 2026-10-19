using System;
using System.Collections.Generic;
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

            // A linked parent can redirect an otherwise ordinary profile directory.
            string? ancestor = Path.GetFullPath(userDataDirectory);
            while (!string.IsNullOrEmpty(ancestor))
            {
                if (Directory.Exists(ancestor) &&
                    (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                {
                    error = "WebView2 user-data directory or parent is a reparse point.";
                    return false;
                }
                ancestor = Path.GetDirectoryName(ancestor);
            }

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
            // ponytail: iterative non-reparse attribute clearing; skipped: transactional rollback of file attributes, add when attribute restore on cancel is required.
            var pending = new Stack<string>();
            pending.Push(directory);
            while (pending.Count > 0)
            {
                string current = pending.Pop();
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(current);
                }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    continue;

                MakeWritable(current);

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    // Deliberately avoid SearchOption.AllDirectories: it follows junctions.
                    try
                    {
                        foreach (string entry in Directory.EnumerateFileSystemEntries(current))
                            pending.Push(entry);
                    }
                    catch (DirectoryNotFoundException) { /* Entry removed concurrently. */ }
                    catch (UnauthorizedAccessException) { /* Locked or restricted entry. */ }
                }
            }
        }

        private static void MakeWritable(string path)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
            catch (FileNotFoundException) { /* Entry removed concurrently. */ }
            catch (DirectoryNotFoundException) { /* Entry removed concurrently. */ }
            catch (UnauthorizedAccessException) { /* Insufficient permissions to modify attributes. */ }
        }
    }
}
