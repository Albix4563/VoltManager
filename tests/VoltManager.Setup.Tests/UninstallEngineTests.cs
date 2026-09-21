using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VoltManager.Setup.Engine;

namespace VoltManager.Setup.Tests
{
    public sealed class UninstallEngineTests
    {
        [Fact]
        public async Task Process_failure_and_post_verification_residuals_are_reported_separately()
        {
            string root = CreateTempDirectory();
            try
            {
                var operations = new FakeUninstallOperations
                {
                    StopProcessesResult = false,
                    Residuals = new[] { "Owned VoltManager process remains after uninstall" },
                };
                var engine = new HardenedInstallEngine(operations, () => Path.Combine(root, "uninstaller.exe"));

                UninstallResult result = await engine.UninstallAsync(root);

                Assert.False(result.Success);
                Assert.Contains(result.Failures, value => value.Contains("process still running"));
                Assert.Equal(new[] { "Owned VoltManager process remains after uninstall" }, result.Residuals);
                Assert.True(result.HasResiduals);
            }
            finally
            {
                Cleanup(root);
            }
        }

        [Fact]
        public async Task Cancellation_propagates_before_destructive_file_steps()
        {
            string root = CreateTempDirectory();
            try
            {
                var operations = new FakeUninstallOperations();
                using var cts = new CancellationTokenSource();
                cts.Cancel();
                var engine = new HardenedInstallEngine(operations, () => Path.Combine(root, "uninstaller.exe"));

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.UninstallAsync(root, cts.Token));

                Assert.Equal(0, operations.DeleteDirectoryCalls);
            }
            finally
            {
                Cleanup(root);
            }
        }

        [Fact]
        public async Task Repeat_uninstall_with_no_owned_artifacts_is_idempotent()
        {
            string root = CreateTempDirectory();
            var operations = new FakeUninstallOperations { DirectoryExistsResult = false };
            var engine = new HardenedInstallEngine(operations, () => Path.Combine(root, "uninstaller.exe"));
            try
            {
                UninstallResult first = await engine.UninstallAsync(root);
                UninstallResult second = await engine.UninstallAsync(root);

                Assert.True(first.Success);
                Assert.True(second.Success);
            }
            finally
            {
                Cleanup(root);
            }
        }

        [Fact]
        public void Locked_file_prevents_directory_delete_then_retry_and_repeat_succeed()
        {
            string root = CreateTempDirectory();
            string locked = Path.Combine(root, "locked.bin");
            File.WriteAllText(locked, "locked");

            try
            {
                using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    Assert.False(InstallEngine.TryDeleteDirectoryTree(root, out string error));
                    Assert.False(string.IsNullOrWhiteSpace(error));
                }

                Assert.True(InstallEngine.TryDeleteDirectoryTree(root, out string afterUnlockError), afterUnlockError);
                Assert.True(InstallEngine.TryDeleteDirectoryTree(root, out string repeatError), repeatError);
            }
            finally
            {
                Cleanup(root);
            }
        }

        private static string CreateTempDirectory()
        {
            string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        private static void Cleanup(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }

        private sealed class FakeUninstallOperations : IUninstallSystemOperations
        {
            public bool StopProcessesResult { get; set; } = true;
            public bool DirectoryExistsResult { get; set; } = true;
            public int DeleteDirectoryCalls { get; private set; }
            public IReadOnlyList<string> Residuals { get; set; } = Array.Empty<string>();
            public string AppDataDirectory => Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests-AppData-NotReal");

            public Task<bool> StopRunningInstalledProcessesAsync(string installDir, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(StopProcessesResult);
            }

            public bool DirectoryExists(string path) => DirectoryExistsResult;

            public bool TryDeleteDirectoryTree(string path, out string error)
            {
                DeleteDirectoryCalls++;
                error = "";
                return true;
            }

            public void DeleteStartupTask(UninstallResult result) { }
            public void RemoveShortcuts(UninstallResult result) { }
            public void RemoveRegistryEntries(UninstallResult result) { }
            public IEnumerable<string> CleanupOwnedTempArtifacts(string currentExecutable) => Array.Empty<string>();

            public IReadOnlyList<string> FindResidualArtifacts(string installDir, string appData, string currentExecutable)
                => Residuals;
        }
    }
}
