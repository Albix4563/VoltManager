using System.IO;
using System.Security.Cryptography;

namespace VoltManager.Services;

internal sealed class LauncherShortcutStore
{
    private readonly string _root;

    public LauncherShortcutStore(string? root = null)
    {
        _root = root ?? Path.Combine(
            ValidationEnvironment.ApplicationDataRoot,
            "VoltManager",
            "launcher-shortcuts");
    }

    public static bool IsTransientPath(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return false;

        string normalized;
        string tempRoot;
        try
        {
            normalized = Path.GetFullPath(fullPath.Trim());
            tempRoot = Path.GetFullPath(Path.GetTempPath())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return false;
        }

        string tempPrefix = tempRoot + Path.DirectorySeparatorChar;
        if (normalized.StartsWith(tempPrefix, StringComparison.OrdinalIgnoreCase))
            return true;

        string? directory = Path.GetDirectoryName(normalized);
        if (string.IsNullOrEmpty(directory))
            return false;

        return directory
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment.StartsWith("chrome_drag", StringComparison.OrdinalIgnoreCase));
    }

    public string? PersistDropped(string fullPath)
    {
        if (!IsTransientPath(fullPath))
            return fullPath;

        string extension = Path.GetExtension(fullPath);
        if (!string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".url", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            byte[] contents = File.ReadAllBytes(fullPath);
            string hash = Convert.ToHexString(SHA256.HashData(contents))[..12].ToLowerInvariant();
            string directory = Path.Combine(_root, hash);
            string destination = Path.Combine(directory, Path.GetFileName(fullPath));
            Directory.CreateDirectory(directory);
            if (!File.Exists(destination))
                File.Copy(fullPath, destination, overwrite: false);
            return destination;
        }
        catch
        {
            return null;
        }
    }
}
