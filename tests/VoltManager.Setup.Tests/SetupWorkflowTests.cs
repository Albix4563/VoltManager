using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using VoltManager.Setup.Engine;

namespace VoltManager.Setup.Tests
{
    public sealed class SetupWorkflowTests
    {
        [Fact]
        public async Task Runner_stops_at_first_failure_and_records_diagnostic()
        {
            var calls = new List<string>();
            var runner = new SetupWorkflowRunner();
            var steps = new[]
            {
                new SetupWorkflowStep("first", _ => { calls.Add("first"); return Task.CompletedTask; }),
                new SetupWorkflowStep("broken", _ => throw new InvalidOperationException("boom")),
                new SetupWorkflowStep("never", _ => { calls.Add("never"); return Task.CompletedTask; }),
            };

            SetupWorkflowResult result = await runner.RunAsync(steps, CancellationToken.None);

            Assert.False(result.Success);
            Assert.False(result.Cancelled);
            Assert.Equal("broken", result.FailedStep);
            Assert.Contains("boom", result.Summary);
            Assert.Equal(new[] { "first" }, calls);
        }

        [Fact]
        public async Task Runner_records_cancellation_without_executing_later_steps()
        {
            var cts = new CancellationTokenSource();
            var runner = new SetupWorkflowRunner();
            var calls = new List<string>();
            var steps = new[]
            {
                new SetupWorkflowStep("cancel", token =>
                {
                    cts.Cancel();
                    token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                }),
                new SetupWorkflowStep("never", _ => { calls.Add("never"); return Task.CompletedTask; }),
            };

            SetupWorkflowResult result = await runner.RunAsync(steps, cts.Token);

            Assert.True(result.Cancelled);
            Assert.False(result.Success);
            Assert.Equal("cancel", result.FailedStep);
            Assert.Empty(calls);
        }

        [Fact]
        public async Task Install_workflow_reports_processes_still_active_before_destructive_steps()
        {
            var processOperations = new FakeProcessOperations { StopForInstallResult = false };
            var engine = new InstallEngine(processOperations);
            var options = new InstallOptions { InstallDir = NewAbsentInstallTarget() };

            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.InstallAsync(options, "1.2.3"));

            Assert.Contains("stop-processes", error.Message);
            Assert.NotNull(engine.LastOperationResult);
            Assert.Equal("stop-processes", engine.LastOperationResult!.FailedStep);
            Assert.Equal(1, processOperations.StopForInstallCalls);
        }

        [Fact]
        public async Task Unverified_existing_install_is_rejected_before_process_or_legacy_side_effects()
        {
            string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
            string install = Path.Combine(root, "VoltManager");
            string package = Path.Combine(root, "package");
            Directory.CreateDirectory(install);
            Directory.CreateDirectory(package);
            string foreign = Path.Combine(install, "VoltManager.exe");
            File.WriteAllText(foreign, "foreign executable bytes");
            InstallManifest.Write(install, new[] { "VoltManager.exe" });
            File.WriteAllText(Path.Combine(package, "VoltManager.exe"), "independent expected bytes");
            string trustedZip = Path.Combine(root, "trusted.zip");
            ZipFile.CreateFromDirectory(package, trustedZip);
            var process = new FakeProcessOperations();
            int legacyRunCalls = 0;
            int legacyFindCalls = 0;
            var preview = new FakePreviewReleaseClient();
            var engine = new InstallEngine(process, preview,
                () => { legacyFindCalls++; return null; },
                (_, _) => { legacyRunCalls++; throw new InvalidOperationException("Must not execute legacy."); },
                () => install, _ => { }, () => trustedZip);

            try
            {
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.InstallAsync(PreviewOptions(install), "2.0.0"));
                Assert.Contains("validate-install-ownership", failure.Message);
                Assert.Equal("validate-install-ownership", engine.LastOperationResult?.FailedStep);
                Assert.Equal(0, process.StopForInstallCalls);
                Assert.Equal(0, legacyRunCalls);
                Assert.Equal(0, legacyFindCalls);
                Assert.Equal(0, preview.Calls);
                Assert.Equal("foreign executable bytes", File.ReadAllText(foreign));
                Assert.False(Directory.GetDirectories(root, "backup*").Any());
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        [Fact]
        public async Task Empty_fresh_install_passes_preflight_before_fake_process_stop()
        {
            string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
            string fresh = Path.Combine(root, "not-created-yet");
            var process = new FakeProcessOperations { StopForInstallResult = false };
            int legacyRunCalls = 0;
            var engine = new InstallEngine(process, new FakePreviewReleaseClient(),
                () => null,
                (_, _) => { legacyRunCalls++; throw new InvalidOperationException("Must not execute legacy."); },
                () => fresh, _ => { }, () => null);

            try
            {
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.InstallAsync(new InstallOptions { InstallDir = fresh }, "2.0.0"));
                Assert.Contains("stop-processes", failure.Message);
                Assert.Equal("stop-processes", engine.LastOperationResult?.FailedStep);
                Assert.Equal(1, process.StopForInstallCalls);
                Assert.Equal(0, legacyRunCalls);
                Assert.False(Directory.Exists(fresh));
            }
            finally
            {
                if (Directory.Exists(root))
                    try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        [Fact]
        public async Task Update_rejects_foreign_installed_payload_before_wait_and_stop()
        {
            string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
            string install = Path.Combine(root, "VoltManager");
            string package = Path.Combine(root, "package");
            Directory.CreateDirectory(install);
            Directory.CreateDirectory(package);
            string foreign = Path.Combine(install, "VoltManager.exe");
            File.WriteAllText(foreign, "foreign bytes");
            InstallManifest.Write(install, new[] { "VoltManager.exe" });
            File.WriteAllText(Path.Combine(package, "VoltManager.exe"), "trusted release bytes");
            string trustedZip = Path.Combine(root, "trusted.zip");
            ZipFile.CreateFromDirectory(package, trustedZip);
            var process = new FakeProcessOperations();
            var engine = new InstallEngine(process, new FakePreviewReleaseClient(),
                () => null, (_, _) => throw new InvalidOperationException("Must not execute legacy."),
                () => install, _ => { }, () => trustedZip);

            try
            {
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.UpdateAsync(123, "2.0.0"));
                Assert.Contains("validate-install-ownership", failure.Message);
                Assert.Equal("validate-install-ownership", engine.LastOperationResult?.FailedStep);
                Assert.Equal(0, process.StopInstalledCalls);
                Assert.Equal(0, process.WaitForExitCalls);
                Assert.Equal("foreign bytes", File.ReadAllText(foreign));
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        [Fact]
        public async Task Verified_existing_install_passes_preflight_then_fake_stop_prevents_any_payload_action()
        {
            string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
            string install = Path.Combine(root, "VoltManager");
            string package = Path.Combine(root, "package");
            Directory.CreateDirectory(install);
            Directory.CreateDirectory(package);
            string existing = Path.Combine(install, "VoltManager.exe");
            File.WriteAllText(existing, "same independently inventoried bytes");
            File.WriteAllText(Path.Combine(package, "VoltManager.exe"), "same independently inventoried bytes");
            InstallManifest.Write(install, new[] { "VoltManager.exe" });
            string trustedZip = Path.Combine(root, "trusted.zip");
            ZipFile.CreateFromDirectory(package, trustedZip);
            var process = new FakeProcessOperations { StopForInstallResult = false };
            var preview = new FakePreviewReleaseClient();
            var engine = new InstallEngine(process, preview,
                () => null, (_, _) => throw new InvalidOperationException("No legacy execution."),
                () => install, _ => { }, () => trustedZip);
            try
            {
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.InstallAsync(PreviewOptions(install), "2.0.0"));
                Assert.Contains("stop-processes", failure.Message);
                Assert.Equal("stop-processes", engine.LastOperationResult?.FailedStep);
                Assert.Equal(1, process.StopForInstallCalls);
                Assert.Equal(0, preview.Calls);
                Assert.Equal("same independently inventoried bytes", File.ReadAllText(existing));
                Assert.False(Directory.GetDirectories(root, "backup*").Any());
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        [Fact]
        public async Task Production_ownership_gate_accepts_different_installed_release_via_its_uninstaller()
        {
            string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                (string install, _) = InstallManifestLifecycleTests.InstallReleaseA(root);
                var process = new FakeProcessOperations { StopForInstallResult = false };
                var guarded = new List<string>();
                // Nessun inventario iniettato: come in produzione, deriva dall'uninstaller di A.
                var engine = new InstallEngine(process, new FakePreviewReleaseClient(),
                    () => null, (_, _) => throw new InvalidOperationException("Must not execute legacy."),
                    () => install, guarded.Add, () => null);

                engine.ValidateUpdateTargetBeforeSideEffects();
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.InstallAsync(new InstallOptions { InstallDir = install }, "2.0.0"));

                Assert.Equal("stop-processes", engine.LastOperationResult?.FailedStep);
                Assert.Equal(1, process.StopForInstallCalls);
                Assert.All(guarded, path => Assert.Equal(install, path));
                Assert.Equal("version A", File.ReadAllText(Path.Combine(install, "VoltManager.dll")));
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        [Theory]
        [InlineData(false, 0)]
        [InlineData(true, 17)]
        [InlineData(true, 0)]
        public async Task Unauthenticated_legacy_uninstaller_is_never_run_regardless_of_its_hypothetical_exit_code(
            bool exited, int exitCode)
        {
            var preview = new FakePreviewReleaseClient();
            var process = new FakeProcessOperations();
            int runCalls = 0;
            var engine = new InstallEngine(process, preview, () => @"C:\SyntheticLegacy\unins000.exe", (_, _) =>
            {
                runCalls++;
                return Task.FromResult<(bool Exited, int ExitCode)?>((exited, exitCode));
            });
            string target = NewAbsentInstallTarget();

            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.InstallAsync(PreviewOptions(target), "1.2.3"));

            Assert.Contains("validate-install-ownership", exception.Message);
            Assert.Contains("Legacy Inno", exception.Message);
            Assert.Equal("validate-install-ownership", engine.LastOperationResult!.FailedStep);
            Assert.Equal(0, runCalls);
            Assert.Equal(0, process.StopForInstallCalls);
            Assert.Equal(0, preview.Calls);
            Assert.False(Directory.Exists(target));
        }

        [Fact]
        public async Task Legacy_uninstaller_that_would_not_start_is_not_attempted()
        {
            var preview = new FakePreviewReleaseClient();
            int calls = 0;
            var engine = CreateEngine(preview, (_, _) =>
            {
                calls++;
                return Task.FromResult<(bool Exited, int ExitCode)?>(null);
            });
            string target = NewAbsentInstallTarget();

            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.InstallAsync(PreviewOptions(target), "1.2.3"));

            Assert.Contains("validate-install-ownership", exception.Message);
            Assert.Equal("validate-install-ownership", engine.LastOperationResult!.FailedStep);
            Assert.Equal(0, calls);
            Assert.Equal(0, preview.Calls);
            Assert.False(Directory.Exists(target));
        }

        [Fact]
        public async Task Legacy_uninstaller_that_would_throw_is_not_attempted()
        {
            var preview = new FakePreviewReleaseClient();
            int calls = 0;
            var engine = CreateEngine(preview, (_, _) =>
            {
                calls++;
                throw new InvalidOperationException("synthetic uninstaller start failure");
            });
            string target = NewAbsentInstallTarget();

            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.InstallAsync(PreviewOptions(target), "1.2.3"));

            Assert.Contains("validate-install-ownership", exception.Message);
            Assert.Equal("validate-install-ownership", engine.LastOperationResult!.FailedStep);
            Assert.Equal(0, calls);
            Assert.Equal(0, preview.Calls);
            Assert.False(Directory.Exists(target));
        }

        [Fact]
        public async Task Pre_cancelled_legacy_install_exits_without_process_or_payload_side_effects()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var preview = new FakePreviewReleaseClient();
            int calls = 0;
            var engine = CreateEngine(preview, (_, _) =>
            {
                calls++;
                return Task.FromCanceled<(bool Exited, int ExitCode)?>(cts.Token);
            });
            string target = NewAbsentInstallTarget();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => engine.InstallAsync(PreviewOptions(target), "1.2.3", cts.Token));

            Assert.True(engine.LastOperationResult!.Cancelled);
            Assert.Equal(0, calls);
            Assert.Equal(0, preview.Calls);
            Assert.False(Directory.Exists(target));
        }

        [Fact]
        public async Task Would_be_successful_legacy_uninstaller_must_not_advance_to_preview()
        {
            var preview = new FakePreviewReleaseClient();
            int calls = 0;
            var engine = CreateEngine(preview, (_, _) =>
            {
                calls++;
                return Task.FromResult<(bool Exited, int ExitCode)?>((true, 0));
            });
            string target = NewAbsentInstallTarget();

            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.InstallAsync(PreviewOptions(target), "1.2.3"));

            Assert.Contains("validate-install-ownership", exception.Message);
            Assert.Equal("validate-install-ownership", engine.LastOperationResult!.FailedStep);
            Assert.Equal(0, preview.Calls);
            Assert.Equal(0, calls);
            Assert.False(Directory.Exists(target));
        }

        private static InstallEngine CreateEngine(
            FakePreviewReleaseClient preview,
            Func<string, CancellationToken, Task<(bool Exited, int ExitCode)?>> legacyRun)
            => new InstallEngine(new FakeProcessOperations(), preview,
                () => @"C:\SyntheticLegacy\unins000.exe", legacyRun);

        private static InstallOptions PreviewOptions(string installDir)
            => new InstallOptions { InstallDir = installDir, UpdateChannel = "preview" };

        private static string NewAbsentInstallTarget()
            // A synthetic path beneath the protected anchor, never created or
            // written to: the fake process/legacy/preview steps always fail first.
            => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "VoltManagerLegacyWorkflowTests-" + Guid.NewGuid().ToString("N"));

        private sealed class FakePreviewReleaseClient : IPreviewReleaseClient
        {
            public int Calls { get; private set; }

            public Task<PreviewReleaseDownload> DownloadLatestAsync(Action<double> onProgress, CancellationToken cancellationToken)
            {
                Calls++;
                throw new InvalidOperationException("synthetic preview reached");
            }
        }

        private sealed class FakeProcessOperations : IInstallProcessOperations
        {
            public bool StopForInstallResult { get; set; } = true;
            public int StopForInstallCalls { get; private set; }
            public int StopInstalledCalls { get; private set; }
            public int WaitForExitCalls { get; private set; }

            public bool StopForInstall(string installDir)
            {
                StopForInstallCalls++;
                return StopForInstallResult;
            }

            public bool StopInstalled(string installDir)
            {
                StopInstalledCalls++;
                return true;
            }

            public Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken)
            {
                WaitForExitCalls++;
                return Task.FromResult(true);
            }

            public void Start(string fileName, string arguments) { }
        }
    }
}
