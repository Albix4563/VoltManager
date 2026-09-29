using System.IO;
using System.Security.Cryptography;
using VoltManager.Services;

namespace VoltManager.Tests;

internal sealed class TestUpdateFileSystem : IUpdateFileSystem
{
    private readonly Dictionary<string, DateTime> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MemoryStream> _files = new(StringComparer.OrdinalIgnoreCase);

    internal TestUpdateFileSystem(string? basePath = null)
        => UpdatesBasePath = basePath ?? @"C:\ProgramData\VoltManager\Updates";

    public string UpdatesBasePath { get; }
    internal bool FailAcl { get; set; }
    internal List<string> ProtectedDirectories { get; } = new();
    internal List<string> DeletedDirectories { get; } = new();
    internal HashSet<string> ReparsePoints { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal string? LastInstallerPath { get; private set; }
    internal bool HasRunDirectories => _directories.Keys.Any(path =>
        !string.Equals(path, UpdatesBasePath, StringComparison.OrdinalIgnoreCase));

    public bool DirectoryExists(string path) => _directories.ContainsKey(path);

    public void CreateDirectory(string path)
        => _directories[path] = DateTime.UtcNow;

    public void ApplyProtectedDirectoryAcl(string path)
    {
        if (FailAcl)
            throw new UnauthorizedAccessException("synthetic ACL failure");
        ProtectedDirectories.Add(path);
    }

    public IEnumerable<string> EnumerateDirectories(string path)
        => _directories.Keys
            .Where(candidate =>
                !string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase) &&
                candidate.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    public DateTime GetDirectoryCreationTimeUtc(string path) => _directories[path];

    public bool IsReparsePoint(string path) => ReparsePoints.Contains(path);

    public Stream CreateInstallerFile(string path)
    {
        var stream = new MemoryStream();
        _files[path] = stream;
        LastInstallerPath = path;
        return stream;
    }

    public void DeleteDirectory(string path, bool recursive)
    {
        DeletedDirectories.Add(path);
        foreach (string file in _files.Keys.Where(file => IsWithin(file, path)).ToArray())
        {
            _files[file].Dispose();
            _files.Remove(file);
        }
        foreach (string directory in _directories.Keys.Where(directory => IsWithin(directory, path)).ToArray())
            _directories.Remove(directory);
    }

    internal MemoryStream GetInstallerStream()
        => LastInstallerPath is not null && _files.TryGetValue(LastInstallerPath, out MemoryStream? stream)
            ? stream
            : throw new InvalidOperationException("No installer stream exists.");

    internal void SetDirectoryCreationTimeUtc(string path, DateTime value)
        => _directories[path] = value;

    private static bool IsWithin(string candidate, string root)
        => candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
           candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

internal static class UpdateTestHelpers
{
    internal static VerifiedUpdateDownload CreateVerifiedDownload(
        TestUpdateFileSystem? fileSystem = null,
        byte[]? payload = null,
        string assetName = "VoltManagerSetup.exe")
    {
        fileSystem ??= new TestUpdateFileSystem();
        payload ??= [1, 2, 3, 4];
        fileSystem.CreateDirectory(fileSystem.UpdatesBasePath);
        string runDirectory = Path.Combine(fileSystem.UpdatesBasePath, Guid.NewGuid().ToString("N"));
        fileSystem.CreateDirectory(runDirectory);
        string path = Path.Combine(runDirectory, $"VoltManagerSetup-{Guid.NewGuid():N}"[..33] + ".exe");
        Stream stream = fileSystem.CreateInstallerFile(path);
        stream.Write(payload);
        stream.Position = 0;
        string hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        return new VerifiedUpdateDownload(path, runDirectory, assetName, hash, stream, fileSystem, _ => { });
    }
}
