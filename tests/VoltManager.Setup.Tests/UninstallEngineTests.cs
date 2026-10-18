using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VoltManager.Setup.Engine;

namespace VoltManager.Setup.Tests
{
    public sealed class UninstallEngineTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Firewall_removal_access_denied_must_be_an_uninstall_failure(bool comInvocationWrapped)
        {
            var result = new UninstallResult();
            var denied = new COMException("synthetic firewall access denied", unchecked((int)0x80070005));

            SystemUninstallSystemOperations.TryRemoveFirewallRule(result, () =>
            {
                if (comInvocationWrapped) throw new TargetInvocationException(denied);
                throw denied;
            });

            Assert.False(result.Success);
            Assert.Contains(result.Failures, failure =>
                failure.Contains("LAN remote firewall cleanup") && failure.Contains("synthetic firewall access denied"));
            Assert.Equal(3, App.GetSilentUninstallExitCode(result, Array.Empty<string>()));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Firewall_removal_missing_rule_is_idempotent(bool comInvocationWrapped)
        {
            var result = new UninstallResult();
            var notFound = new COMException("synthetic firewall rule missing", unchecked((int)0x80070002));

            SystemUninstallSystemOperations.TryRemoveFirewallRule(result, () =>
            {
                if (comInvocationWrapped) throw new TargetInvocationException(notFound);
                throw notFound;
            });

            Assert.True(result.Success);
        }

        [Fact]
        public void Failed_startup_task_delete_propagates_to_uninstall_result()
        {
            var result = new UninstallResult();

            HardenedInstallEngine.DeleteStartupTask(result,
                () => new SchtasksResult(exitCode: -1, timedOut: true, output: "synthetic timeout"));

            Assert.False(result.Success);
            Assert.Contains(result.Failures, failure => failure.Contains("Startup task"));
            Assert.Equal(3, App.GetSilentUninstallExitCode(result, Array.Empty<string>()));
        }

        [Fact]
        public void Missing_startup_task_does_not_report_a_failure()
        {
            var result = new UninstallResult();

            HardenedInstallEngine.DeleteStartupTask(result,
                () => new SchtasksResult(exitCode: 1, timedOut: false, output: "Cannot find the task"));

            Assert.True(result.Success);
        }

        [Fact]
        public async Task Failed_LAN_security_cleanup_and_unremoved_artifacts_reach_result_and_silent_exit_code()
        {
            string root = CreateTempDirectory();
            try
            {
                (string installDir, string trustedZip) = CreateVerifiedSyntheticInstall(root);
                var operations = new FakeUninstallOperations
                {
                    LanRemoteCleanupError = "synthetic certificate removal rejected",
                    Residuals = new[] { "synthetic LAN credential remains" },
                };
                var engine = new HardenedInstallEngine(
                    operations, () => Path.Combine(root, "synthetic-uninstaller.exe"), trustedZip);

                UninstallResult result = await engine.UninstallAsync(installDir);

                Assert.Equal(1, operations.RemoveLanRemoteControlArtifactsCalls);
                Assert.Contains(result.Failures, failure => failure.Contains("synthetic certificate removal rejected"));
                Assert.Contains("synthetic LAN credential remains", result.Residuals);
                Assert.False(result.Success);
                Assert.Contains("synthetic LAN credential remains", result.Summary);
                Assert.Equal(3, App.GetSilentUninstallExitCode(result, Array.Empty<string>()));
            }
            finally
            {
                Cleanup(root);
            }
        }

        [Fact]
        public async Task Failed_owned_file_deletion_must_not_continue_into_appdata_or_registry_cleanup()
        {
            string root = CreateTempDirectory();
            string owned = Path.Combine(root, "VoltManager.exe");
            File.WriteAllText(owned, "synthetic locked executable");
            try
            {
                var operations = new FakeUninstallOperations
                {
                    Residuals = new[] { "synthetic install file remains" },
                };
                var engine = new HardenedInstallEngine(
                    operations, () => Path.Combine(root, "..", "synthetic-uninstaller.exe"));

                using (new FileStream(owned, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    UninstallResult result = await engine.UninstallAsync(root);

                    Assert.False(result.Success);
                    Assert.Contains(result.Failures, failure => failure.Contains("Install directory:"));
                    Assert.Contains("synthetic install file remains", result.Residuals);
                    Assert.Equal(0, operations.DeleteDirectoryCalls);
                    Assert.Equal(0, operations.RemoveLanRemoteControlArtifactsCalls);
                    Assert.Equal(0, operations.DeleteStartupTaskCalls);
                    Assert.Equal(0, operations.RemoveRegistryEntriesCalls);
                }
                Assert.Equal("synthetic locked executable", File.ReadAllText(owned));
            }
            finally
            {
                Cleanup(root);
            }
        }

        [Fact]
        public async Task Unverified_install_must_not_remove_unrelated_LAN_artifacts_or_user_data()
        {
            string root = CreateTempDirectory();
            string foreign = Path.Combine(root, "personal.txt");
            string manifest = Path.Combine(root, InstallManifest.FileName);
            File.WriteAllText(foreign, "user data must survive");
            File.WriteAllText(manifest, "{\"entries\":[\"personal.txt\"]}");
            try
            {
                var operations = new FakeUninstallOperations();
                var engine = new HardenedInstallEngine(operations,
                    () => Path.Combine(root, "..", "synthetic-uninstaller.exe"));

                UninstallResult result = await engine.UninstallAsync(root);

                Assert.False(result.Success);
                Assert.Contains(result.Failures, failure => failure.Contains("Install directory:"));
                Assert.Equal(0, operations.RemoveLanRemoteControlArtifactsCalls);
                Assert.Equal(0, operations.DeleteStartupTaskCalls);
                Assert.Equal(0, operations.RemoveRegistryEntriesCalls);
                Assert.Equal("user data must survive", File.ReadAllText(foreign));
                Assert.Equal("{\"entries\":[\"personal.txt\"]}", File.ReadAllText(manifest));
            }
            finally { Cleanup(root); }
        }

        [Fact]
        public async Task Missing_install_root_must_not_authorize_global_LAN_or_registry_cleanup()
        {
            string root = CreateTempDirectory();
            try
            {
                var operations = new FakeUninstallOperations { DirectoryExistsResult = false };
                var engine = new HardenedInstallEngine(operations,
                    () => Path.Combine(root, "..", "synthetic-uninstaller.exe"));
                UninstallResult result = await engine.UninstallAsync(root);

                Assert.False(result.Success);
                Assert.Equal(0, operations.RemoveLanRemoteControlArtifactsCalls);
                Assert.Equal(0, operations.DeleteStartupTaskCalls);
                Assert.Equal(0, operations.RemoveRegistryEntriesCalls);
            }
            finally { Cleanup(root); }
        }

        [Fact]
        public async Task Forged_app_named_file_must_not_even_stop_processes_before_ownership_preflight()
        {
            string root = CreateTempDirectory();
            try
            {
                (string installDir, string trustedZip) = CreateVerifiedSyntheticInstall(root);
                string claimedDll = Path.Combine(installDir, "VoltManager.dll");
                File.WriteAllText(claimedDll, "foreign personal data with a recognized app filename");
                string manifest = Path.Combine(installDir, InstallManifest.FileName);
                byte[] originalDll = File.ReadAllBytes(claimedDll);
                byte[] originalManifest = File.ReadAllBytes(manifest);
                var operations = new FakeUninstallOperations();
                var engine = new HardenedInstallEngine(operations,
                    () => Path.Combine(root, "synthetic-uninstaller.exe"), trustedZip);

                UninstallResult result = await engine.UninstallAsync(installDir);

                Assert.False(result.Success);
                Assert.Contains(result.Failures, failure => failure.IndexOf("ownership", StringComparison.OrdinalIgnoreCase) >= 0);
                Assert.Equal(3, App.GetSilentUninstallExitCode(result, Array.Empty<string>()));
                Assert.Equal(0, operations.StopProcessesCalls);
                Assert.Equal(0, operations.RemoveLanRemoteControlArtifactsCalls);
                Assert.Equal(0, operations.DeleteDirectoryCalls);
                Assert.Equal(0, operations.DeleteStartupTaskCalls);
                Assert.Equal(0, operations.RemoveRegistryEntriesCalls);
                Assert.Equal(originalDll, File.ReadAllBytes(claimedDll));
                Assert.Equal(originalManifest, File.ReadAllBytes(manifest));
            }
            finally { Cleanup(root); }
        }

        [Fact]
        public async Task Install_root_disappearing_before_manifest_verification_must_not_trigger_global_cleanup()
        {
            string root = CreateTempDirectory();
            try
            {
                string missingInstall = Path.Combine(root, "not-an-installation");
                var operations = new FakeUninstallOperations
                {
                    // Simulate a stale DirectoryExists read just before manifest preflight.
                    DirectoryExistsResult = true,
                };
                var engine = new HardenedInstallEngine(operations,
                    () => Path.Combine(root, "synthetic-uninstaller.exe"));

                UninstallResult result = await engine.UninstallAsync(missingInstall);

                Assert.False(result.Success);
                Assert.Contains(result.Failures, failure => failure.IndexOf("ownership", StringComparison.OrdinalIgnoreCase) >= 0);
                Assert.Equal(3, App.GetSilentUninstallExitCode(result, Array.Empty<string>()));
                Assert.Equal(0, operations.StopProcessesCalls);
                Assert.Equal(0, operations.RemoveLanRemoteControlArtifactsCalls);
                Assert.Equal(0, operations.DeleteDirectoryCalls);
                Assert.Equal(0, operations.DeleteStartupTaskCalls);
                Assert.Equal(0, operations.RemoveRegistryEntriesCalls);
                Assert.False(Directory.Exists(missingInstall));
            }
            finally { Cleanup(root); }
        }

        [Fact]
        public async Task Manifest_alone_without_any_verified_install_file_must_not_authorize_uninstall()
        {
            string root = CreateTempDirectory();
            try
            {
                (string installDir, string trustedZip) = CreateVerifiedSyntheticInstall(root);
                File.Delete(Path.Combine(installDir, "VoltManager.dll"));
                string marker = Path.Combine(installDir, InstallManifest.FileName);
                byte[] originalMarker = File.ReadAllBytes(marker);
                var operations = new FakeUninstallOperations();
                var engine = new HardenedInstallEngine(operations,
                    () => Path.Combine(root, "synthetic-uninstaller.exe"), trustedZip);

                UninstallResult result = await engine.UninstallAsync(installDir);

                Assert.False(result.Success);
                Assert.Contains(result.Failures, failure => failure.IndexOf("ownership", StringComparison.OrdinalIgnoreCase) >= 0);
                Assert.Equal(3, App.GetSilentUninstallExitCode(result, Array.Empty<string>()));
                Assert.Equal(0, operations.StopProcessesCalls);
                Assert.Equal(0, operations.RemoveLanRemoteControlArtifactsCalls);
                Assert.Equal(0, operations.DeleteDirectoryCalls);
                Assert.Equal(0, operations.DeleteStartupTaskCalls);
                Assert.Equal(0, operations.RemoveRegistryEntriesCalls);
                Assert.Equal(originalMarker, File.ReadAllBytes(marker));
                Assert.True(Directory.Exists(installDir));
            }
            finally { Cleanup(root); }
        }

        [Fact]
        public async Task Verified_install_moved_away_during_shutdown_must_not_trigger_global_cleanup()
        {
            string root = CreateTempDirectory();
            try
            {
                (string installDir, string trustedZip) = CreateVerifiedSyntheticInstall(root);
                string relocated = Path.Combine(root, "unrelated-relocated-tree");
                byte[] originalDll = File.ReadAllBytes(Path.Combine(installDir, "VoltManager.dll"));
                byte[] originalManifest = File.ReadAllBytes(Path.Combine(installDir, InstallManifest.FileName));
                var operations = new FakeUninstallOperations
                {
                    // Synthetic, non-destructive directory rename after successful
                    // ownership preflight and before the later deletion phase.
                    BeforeStopReturn = () => Directory.Move(installDir, relocated),
                    DirectoryExistsResult = true,
                };
                var engine = new HardenedInstallEngine(operations,
                    () => Path.Combine(root, "synthetic-uninstaller.exe"), trustedZip);

                UninstallResult result = await engine.UninstallAsync(installDir);

                Assert.False(result.Success);
                Assert.Contains(result.Failures, failure =>
                    failure.IndexOf("ownership", StringComparison.OrdinalIgnoreCase) >= 0);
                Assert.Equal(3, App.GetSilentUninstallExitCode(result, Array.Empty<string>()));
                Assert.Equal(1, operations.StopProcessesCalls);
                Assert.Equal(0, operations.RemoveLanRemoteControlArtifactsCalls);
                Assert.Equal(0, operations.DeleteDirectoryCalls);
                Assert.Equal(0, operations.DeleteStartupTaskCalls);
                Assert.Equal(0, operations.RemoveRegistryEntriesCalls);
                Assert.False(Directory.Exists(installDir));
                Assert.Equal(originalDll, File.ReadAllBytes(Path.Combine(relocated, "VoltManager.dll")));
                Assert.Equal(originalManifest, File.ReadAllBytes(Path.Combine(relocated, InstallManifest.FileName)));
            }
            finally { Cleanup(root); }
        }

        [Fact]
        public async Task Process_failure_and_post_verification_residuals_are_reported_separately()
        {
            string root = CreateTempDirectory();
            try
            {
                (string installDir, string trustedZip) = CreateVerifiedSyntheticInstall(root);
                var operations = new FakeUninstallOperations
                {
                    StopProcessesResult = false,
                    Residuals = new[] { "Owned VoltManager process remains after uninstall" },
                };
                var engine = new HardenedInstallEngine(operations,
                    () => Path.Combine(root, "synthetic-uninstaller.exe"), trustedZip);

                UninstallResult result = await engine.UninstallAsync(installDir);

                Assert.False(result.Success);
                Assert.Contains(result.Failures, value => value.Contains("process still running"));
                Assert.Equal(new[] { "Owned VoltManager process remains after uninstall" }, result.Residuals);
                Assert.True(result.HasResiduals);
                Assert.Equal(1, operations.StopProcessesCalls);
                Assert.Equal(0, operations.RemoveLanRemoteControlArtifactsCalls);
            }
            finally
            {
                Cleanup(root);
            }
        }

        [Fact]
        public async Task Failed_process_shutdown_preserves_owned_install_files_and_skips_cleanup()
        {
            string root = CreateTempDirectory();
            try
            {
                (string installDir, string trustedZip) = CreateVerifiedSyntheticInstall(root);
                string ownedFile = Path.Combine(installDir, "VoltManager.dll");
                var operations = new FakeUninstallOperations
                {
                    StopProcessesResult = false,
                    Residuals = new[] { "Owned process still active" },
                };
                var engine = new HardenedInstallEngine(
                    operations, () => Path.Combine(root, "synthetic-uninstaller.exe"), trustedZip);

                UninstallResult result = await engine.UninstallAsync(installDir);

                Assert.False(result.Success);
                Assert.Contains(result.Failures, failure => failure.Contains("process still running"));
                Assert.Contains("Owned process still active", result.Residuals);
                Assert.Equal("synthetic verified DLL", File.ReadAllText(ownedFile));
                Assert.Equal(1, operations.StopProcessesCalls);
                Assert.Equal(0, operations.DeleteDirectoryCalls);
                Assert.Equal(0, operations.RemoveLanRemoteControlArtifactsCalls);
            }
            finally
            {
                Cleanup(root);
            }
        }

        [Fact]
        public async Task Cancellation_during_failed_process_shutdown_takes_priority_over_failure_result()
        {
            string root = CreateTempDirectory();
            try
            {
                (string installDir, string trustedZip) = CreateVerifiedSyntheticInstall(root);
                using var cts = new CancellationTokenSource();
                var operations = new FakeUninstallOperations
                {
                    StopProcessesResult = false,
                    BeforeStopReturn = cts.Cancel,
                };
                var engine = new HardenedInstallEngine(
                    operations, () => Path.Combine(root, "synthetic-uninstaller.exe"), trustedZip);

                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => engine.UninstallAsync(installDir, cts.Token));

                Assert.Equal(1, operations.StopProcessesCalls);
                Assert.Equal(0, operations.RemoveLanRemoteControlArtifactsCalls);
                Assert.Equal(0, operations.DeleteDirectoryCalls);
            }
            finally
            {
                Cleanup(root);
            }
        }

        [Fact]
        public async Task Uninstaller_inside_install_directory_preserves_user_state_and_skips_cleanup()
        {
            string root = CreateTempDirectory();
            try
            {
                (string installDir, string trustedZip) = CreateVerifiedSyntheticInstall(root);
                var operations = new FakeUninstallOperations
                {
                    // The target is verified, but an uninstaller executing from
                    // inside it can never safely complete. Even process stop is
                    // an avoidable side effect in this rejected operation.
                    StopProcessesResult = false,
                };
                var engine = new HardenedInstallEngine(
                    operations, () => Path.Combine(installDir, "uninstall.exe"), trustedZip);

                UninstallResult result = await engine.UninstallAsync(installDir);

                Assert.False(result.Success);
                Assert.Contains(result.Failures,
                    failure => failure.Contains("Uninstaller is still running from the install directory"));
                Assert.Equal(3, App.GetSilentUninstallExitCode(result, Array.Empty<string>()));
                Assert.Equal("synthetic verified DLL", File.ReadAllText(Path.Combine(installDir, "VoltManager.dll")));
                Assert.Equal(0, operations.StopProcessesCalls);
                Assert.Equal(0, operations.DeleteDirectoryCalls);
                Assert.Equal(0, operations.RemoveLanRemoteControlArtifactsCalls);
                Assert.Equal(0, operations.DeleteStartupTaskCalls);
                Assert.Equal(0, operations.RemoveRegistryEntriesCalls);
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
        public async Task Missing_install_root_is_conservatively_rejected_on_repeated_uninstall()
        {
            string root = CreateTempDirectory();
            var operations = new FakeUninstallOperations { DirectoryExistsResult = false };
            var engine = new HardenedInstallEngine(operations, () => Path.Combine(root, "uninstaller.exe"));
            try
            {
                UninstallResult first = await engine.UninstallAsync(root);
                UninstallResult second = await engine.UninstallAsync(root);

                Assert.False(first.Success);
                Assert.False(second.Success);
                Assert.Equal(0, operations.RemoveLanRemoteControlArtifactsCalls);
                Assert.Equal(0, operations.RemoveRegistryEntriesCalls);
            }
            finally
            {
                Cleanup(root);
            }
        }

        [Fact]
        public async Task Uninstall_removes_LAN_remote_security_artifacts()
        {
            string root = CreateTempDirectory();
            try
            {
                (string installDir, string trustedZip) = CreateVerifiedSyntheticInstall(root);
                var operations = new FakeUninstallOperations();
                var engine = new HardenedInstallEngine(operations,
                    () => Path.Combine(root, "synthetic-uninstaller.exe"), trustedZip);
                UninstallResult result = await engine.UninstallAsync(installDir);

                Assert.True(result.Success);
                Assert.Equal(1, operations.RemoveLanRemoteControlArtifactsCalls);
                Assert.Equal(1, operations.DeleteDirectoryCalls);
                Assert.Equal(1, operations.DeleteStartupTaskCalls);
                Assert.Equal(1, operations.RemoveRegistryEntriesCalls);
                Assert.Equal(0, App.GetSilentUninstallExitCode(result, Array.Empty<string>()));
                Assert.False(Directory.Exists(installDir));
            }
            finally
            {
                Cleanup(root);
            }
        }

        private static (string InstallDirectory, string TrustedZip) CreateVerifiedSyntheticInstall(string root)
        {
            string installDir = Path.Combine(root, "synthetic-install");
            string sourceDir = Path.Combine(root, "synthetic-payload");
            Directory.CreateDirectory(installDir);
            Directory.CreateDirectory(sourceDir);
            File.WriteAllText(Path.Combine(sourceDir, "VoltManager.dll"), "synthetic verified DLL");
            File.WriteAllText(Path.Combine(installDir, "VoltManager.dll"), "synthetic verified DLL");
            string zip = Path.Combine(root, "verified-payload.zip");
            ZipFile.CreateFromDirectory(sourceDir, zip);
            InstallManifest.Write(installDir, new[] { "VoltManager.dll" });
            return (installDir, zip);
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
            public int StopProcessesCalls { get; private set; }
            public Action? BeforeStopReturn { get; set; }
            public bool DirectoryExistsResult { get; set; } = true;
            public int DeleteDirectoryCalls { get; private set; }
            public int RemoveLanRemoteControlArtifactsCalls { get; private set; }
            public int DeleteStartupTaskCalls { get; private set; }
            public int RemoveRegistryEntriesCalls { get; private set; }
            public string? LanRemoteCleanupError { get; set; }
            public IReadOnlyList<string> Residuals { get; set; } = Array.Empty<string>();
            public string AppDataDirectory => Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests-AppData-NotReal");

            public Task<bool> StopRunningInstalledProcessesAsync(string installDir, CancellationToken cancellationToken)
            {
                StopProcessesCalls++;
                cancellationToken.ThrowIfCancellationRequested();
                BeforeStopReturn?.Invoke();
                return Task.FromResult(StopProcessesResult);
            }

            public bool DirectoryExists(string path) => DirectoryExistsResult;

            public bool TryDeleteDirectoryTree(string path, out string error)
            {
                DeleteDirectoryCalls++;
                error = "";
                return true;
            }

            public void DeleteStartupTask(UninstallResult result) => DeleteStartupTaskCalls++;
            public void RemoveShortcuts(UninstallResult result) { }
            public void RemoveRegistryEntries(UninstallResult result) => RemoveRegistryEntriesCalls++;
            public void RemoveLanRemoteControlArtifacts(UninstallResult result)
            {
                RemoveLanRemoteControlArtifactsCalls++;
                if (LanRemoteCleanupError != null)
                    result.Add("LAN remote certificate cleanup: " + LanRemoteCleanupError);
            }
            public IEnumerable<string> CleanupOwnedTempArtifacts(string currentExecutable) => Array.Empty<string>();

            public IReadOnlyList<string> FindResidualArtifacts(string installDir, string appData, string currentExecutable)
                => Residuals;
        }
    }
}
