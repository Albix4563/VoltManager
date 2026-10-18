using System.IO;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class MaintenanceBackupSecurityTests
{
    [Fact]
    public void BackupFailsIfSourceChangesImmediatelyAfterPayloadCopy()
    {
        var env = new TestEnvironment();
        string source = Path.Combine(Path.GetTempPath(), "vm_backup_source_" + Guid.NewGuid().ToString("N"), "plugin.dll");
        env.WriteFileBytes(source, [1, 2, 3]);
        env.AfterCopy = (_, _) => env.WriteFileBytes(source, [4, 5, 6]);
        var manager = new MaintenanceBackupManager(NewBackupRoot(), env);

        Assert.Throws<InvalidDataException>(() => manager.CreateBackup("leftovers", "snapshot", [FileItem(source)]));
        Assert.DoesNotContain(env.Files.Keys, path => path.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BackupFailsIfCopiedPayloadIsCorrupt()
    {
        var env = new TestEnvironment();
        string source = Path.Combine(Path.GetTempPath(), "vm_backup_source_" + Guid.NewGuid().ToString("N"), "plugin.dll");
        env.WriteFileBytes(source, [1, 2, 3]);
        env.AfterCopy = (_, destination) => env.WriteFileBytes(destination, [9, 9, 9]);
        var manager = new MaintenanceBackupManager(NewBackupRoot(), env);

        Assert.Throws<InvalidDataException>(() => manager.CreateBackup("leftovers", "snapshot", [FileItem(source)]));
    }

    [Fact]
    public void RestoreDoesNotReplaceFileRecreatedDuringCopy()
    {
        var env = new TestEnvironment();
        string source = Path.Combine(Path.GetTempPath(), "vm_backup_source_" + Guid.NewGuid().ToString("N"), "plugin.dll");
        env.WriteFileBytes(source, [1, 2, 3]);
        var manager = new MaintenanceBackupManager(NewBackupRoot(), env);
        string backupId = manager.CreateBackup("leftovers", "snapshot", [FileItem(source)]);
        env.DeleteFile(source);
        env.BeforeCopy = (_, destination) =>
        {
            if (destination == source)
                env.WriteFileBytes(source, [7, 8, 9]);
        };

        var operation = manager.RestoreBackup(backupId, []);

        Assert.Equal(0, operation.Processed);
        Assert.Equal("conflict", Assert.Single(operation.Items).Status);
        Assert.Equal(new byte[] { 7, 8, 9 }, env.ReadFileBytes(source));
    }

    [Fact]
    public void RestoreSkipsAnAlreadyRecreatedFile()
    {
        var env = new TestEnvironment();
        string source = Path.Combine(Path.GetTempPath(), "vm_backup_source_" + Guid.NewGuid().ToString("N"), "plugin.dll");
        env.WriteFileBytes(source, [1, 2, 3]);
        var manager = new MaintenanceBackupManager(NewBackupRoot(), env);
        string backupId = manager.CreateBackup("leftovers", "snapshot", [FileItem(source)]);
        env.WriteFileBytes(source, [8, 9]);

        var operation = manager.RestoreBackup(backupId, []);

        Assert.Equal(0, operation.Processed);
        Assert.Equal("conflict", Assert.Single(operation.Items).Status);
        Assert.Equal(new byte[] { 8, 9 }, env.ReadFileBytes(source));
    }

    [Fact]
    public void RestoreSkipsAnAlreadyRecreatedRegistryValue()
    {
        var env = new TestEnvironment();
        var original = new RawRegistryValueRecord(
            "CurrentUser", "Registry64", @"Software\VoltManagerTests", "Application", "String", "old", null);
        env.RegistryValue = original;
        var item = new MaintenanceOperationItem(
            "registry:application", "registry", @"Software\VoltManagerTests", "test", true, false)
        {
            RegistryValue = original
        };
        var manager = new MaintenanceBackupManager(NewBackupRoot(), env);
        string backupId = manager.CreateBackup("registry", "snapshot", [item]);
        env.RegistryValue = original with { ValueData = "new" };

        var operation = manager.RestoreBackup(backupId, []);

        Assert.Equal(0, operation.Processed);
        Assert.Equal("conflict", Assert.Single(operation.Items).Status);
        Assert.Equal("new", env.RegistryValue?.ValueData);
    }

    [Fact]
    public void RestoreDoesNotOverwriteRegistryValueRecreatedDuringWrite()
    {
        var env = new TestEnvironment();
        var original = new RawRegistryValueRecord(
            "CurrentUser", "Registry64", @"Software\VoltManagerTests", "Application", "String", "old", null);
        env.RegistryValue = original;
        var item = new MaintenanceOperationItem(
            "registry:application", "registry", @"Software\VoltManagerTests", "test", true, false)
        {
            RegistryValue = original
        };
        var manager = new MaintenanceBackupManager(NewBackupRoot(), env);
        string backupId = manager.CreateBackup("registry", "snapshot", [item]);
        env.RegistryValue = null;
        env.BeforeSetRegistry = () => env.RegistryValue = original with { ValueData = "new" };

        var operation = manager.RestoreBackup(backupId, []);

        Assert.Equal(0, operation.Processed);
        Assert.Equal("conflict", Assert.Single(operation.Items).Status);
        Assert.Equal("new", env.RegistryValue?.ValueData);
    }

    private static string NewBackupRoot() =>
        Path.Combine(Path.GetTempPath(), "vm_backup_tests_" + Guid.NewGuid().ToString("N"));

    private static MaintenanceOperationItem FileItem(string path) =>
        new("file:plugin", "file", path, "test", true, false);

    private sealed class TestEnvironment : IMaintenanceEnvironment
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
        public RawRegistryValueRecord? RegistryValue { get; set; }
        public Action<string, string>? AfterCopy { get; set; }
        public Action<string, string>? BeforeCopy { get; set; }
        public Action? BeforeSetRegistry { get; set; }

        public IReadOnlyList<RawUninstallRecord> GetUninstallRecords() => [];
        public IReadOnlyList<RawRegistryValueRecord> GetStartupRunValues() => [];
        public IReadOnlyList<RawAppPathRecord> GetAppPathsRecords() => [];
        public bool IsUninstallRegistrationPresent(string hive, string view, string subKeyName) => false;
        public bool DeleteRegistryValue(string hive, string view, string subKey, string valueName)
        {
            RegistryValue = null;
            return true;
        }

        public bool DeleteRegistryValueIfUnchanged(RawRegistryValueRecord expected)
        {
            if (RegistryValue is null || !MaintenanceBackupManager.RegistryEquals(RegistryValue, expected))
                return false;
            RegistryValue = null;
            return true;
        }

        public bool SetRegistryValue(string hive, string view, string subKey, string valueName, string valueKind, string valueDataBase64)
        {
            BeforeSetRegistry?.Invoke();
            RegistryValue = new RawRegistryValueRecord(hive, view, subKey, valueName, valueKind,
                WindowsMaintenanceEnvironment.DeserializeRegistryValue(Convert.FromBase64String(valueDataBase64),
                    Enum.Parse<Microsoft.Win32.RegistryValueKind>(valueKind)), null);
            return true;
        }

        public bool TrySetRegistryValueIfAbsent(string hive, string view, string subKey, string valueName, string valueKind, string valueDataBase64)
        {
            BeforeSetRegistry?.Invoke();
            if (RegistryValue != null)
                return false;
            return SetRegistryValue(hive, view, subKey, valueName, valueKind, valueDataBase64);
        }

        public RawRegistryValueRecord? ReadRegistryValue(string hive, string view, string subKey, string valueName)
            => RegistryValue;
        public bool CheckFileIsTrulyMissing(string rawPath, out string normalizedPath, out string reason)
        {
            normalizedPath = rawPath;
            reason = "";
            return false;
        }
        public IReadOnlyList<string> FindLeftoverFiles(string installLocation, int maxResults) => [];
        public bool IsProtectedSystemPath(string path) => false;
        public bool IsSafeLocalPath(string path) => true;
        public void SecureBackupDirectory(string path) { }
        public bool ContainsUserData(string path) => false;
        public Task<ProcessExecutionResult> RunUninstallerAsync(string fileName, string arguments, CancellationToken cancellationToken)
            => Task.FromResult(new ProcessExecutionResult(0, true, null));
        public bool OpenAppsSettings() => false;

        public string ReadAllText(string path) => System.Text.Encoding.UTF8.GetString(ReadFileBytes(path));
        public void WriteAllText(string path, string text) => WriteFileBytes(path, System.Text.Encoding.UTF8.GetBytes(text));
        public bool FileExists(string path) => Files.ContainsKey(path);
        public bool DirectoryExists(string path) => _directories.Contains(path);
        public void CreateDirectory(string path) => _directories.Add(path);
        public void DeleteDirectory(string path, bool recursive)
        {
            foreach (var file in Files.Keys.Where(file => file.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToArray())
                Files.Remove(file);
            _directories.RemoveWhere(directory => directory.StartsWith(path, StringComparison.OrdinalIgnoreCase));
        }
        public void DeleteFile(string path) => Files.Remove(path);
        public void CopyFile(string source, string destination, bool overwrite)
        {
            BeforeCopy?.Invoke(source, destination);
            if (!overwrite && FileExists(destination))
                throw new IOException("Destination already exists.");
            WriteFileBytes(destination, ReadFileBytes(source));
            AfterCopy?.Invoke(source, destination);
        }
        public byte[] ReadFileBytes(string path) => Files[path].ToArray();
        public void WriteFileBytes(string path, byte[] bytes) => Files[path] = bytes.ToArray();
    }
}
