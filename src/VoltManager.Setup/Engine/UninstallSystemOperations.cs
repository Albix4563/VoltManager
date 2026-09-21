using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VoltManager.Services;

namespace VoltManager.Setup.Engine
{
    internal interface IUninstallSystemOperations
    {
        string AppDataDirectory { get; }
        Task<bool> StopRunningInstalledProcessesAsync(string installDir, CancellationToken cancellationToken);
        bool DirectoryExists(string path);
        bool TryDeleteDirectoryTree(string path, out string error);
        void DeleteStartupTask(UninstallResult result);
        void RemoveShortcuts(UninstallResult result);
        void RemoveRegistryEntries(UninstallResult result);
        IEnumerable<string> CleanupOwnedTempArtifacts(string currentExecutable);
        IReadOnlyList<string> FindResidualArtifacts(string installDir, string appData, string currentExecutable);
    }

    internal sealed class SystemUninstallSystemOperations : IUninstallSystemOperations
    {
        public string AppDataDirectory => VoltManagerArtifacts.AppDataDirectory;

        public Task<bool> StopRunningInstalledProcessesAsync(string installDir, CancellationToken cancellationToken)
            => HardenedInstallEngine.StopRunningInstalledProcessesAsync(installDir, cancellationToken);

        public bool DirectoryExists(string path) => Directory.Exists(path);

        public bool TryDeleteDirectoryTree(string path, out string error)
            => InstallEngine.TryDeleteDirectoryTree(path, out error);

        public void DeleteStartupTask(UninstallResult result)
            => HardenedInstallEngine.DeleteStartupTask(result);

        public void RemoveShortcuts(UninstallResult result)
            => HardenedInstallEngine.RemoveShortcuts(result);

        public void RemoveRegistryEntries(UninstallResult result)
            => HardenedInstallEngine.RemoveRegistryEntries(result);

        public IEnumerable<string> CleanupOwnedTempArtifacts(string currentExecutable)
            => VoltManagerArtifacts.CleanupOwnedTempArtifacts(Path.GetTempPath(), currentExecutable);

        public IReadOnlyList<string> FindResidualArtifacts(string installDir, string appData, string currentExecutable)
            => HardenedInstallEngine.FindResidualArtifacts(installDir, appData, currentExecutable);
    }
}
