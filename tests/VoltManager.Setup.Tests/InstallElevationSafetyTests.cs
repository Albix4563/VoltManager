using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using VoltManager.Setup.Engine;

namespace VoltManager.Setup.Tests
{
    public sealed class InstallElevationSafetyTests
    {
        [Fact]
        public void Elevated_uninstaller_directory_requires_privileged_owner_and_no_user_write_grants()
        {
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            var secure = new DirectorySecurity();
            secure.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            secure.SetOwner(administrators);
            secure.AddAccessRule(new FileSystemAccessRule(administrators,
                FileSystemRights.FullControl, AccessControlType.Allow));
            secure.AddAccessRule(new FileSystemAccessRule(users,
                FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            Assert.True(InstallDirectorySecurity.HasProtectedElevationAcl(secure));

            secure.AddAccessRule(new FileSystemAccessRule(users,
                FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Write,
                AccessControlType.Allow));
            Assert.False(InstallDirectorySecurity.HasProtectedElevationAcl(secure));

            var foreignOwned = new DirectorySecurity();
            foreignOwned.SetAccessRuleProtection(true, false);
            foreignOwned.SetOwner(users);
            foreignOwned.AddAccessRule(new FileSystemAccessRule(administrators,
                FileSystemRights.FullControl, AccessControlType.Allow));
            Assert.False(InstallDirectorySecurity.HasProtectedElevationAcl(foreignOwned));
        }

        [Fact]
        public async Task User_writable_custom_install_target_must_fail_before_any_payload_or_ARP_operation()
        {
            string destination = Path.Combine(Path.GetTempPath(), "VoltManagerElevationTests",
                Guid.NewGuid().ToString("N"), "VoltManager");
            var processes = new HarmlessProcessOperations();
            bool attemptedLegacyLookup = false;
            var engine = new InstallEngine(processes, new NeverPreviewReleaseClient(),
                () => { attemptedLegacyLookup = true; return "synthetic-only"; },
                (_, _) => throw new InvalidOperationException("Unexpected legacy uninstall"));

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.InstallAsync(new InstallOptions { InstallDir = destination }, "synthetic-version"));

            Assert.Contains("elevated", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("validate-elevated-install-location", engine.LastOperationResult?.FailedStep);
            Assert.False(Directory.Exists(destination));
            // A rejected custom target must not stop processes as a side effect.
            Assert.False(processes.StoppedForInstall);
            Assert.False(attemptedLegacyLookup);
        }

        private sealed class HarmlessProcessOperations : IInstallProcessOperations
        {
            public bool StoppedForInstall { get; private set; }
            public bool StopForInstall(string installDir)
            {
                StoppedForInstall = true;
                return true;
            }
            public bool StopInstalled(string installDir) => true;
            public Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken token)
                => Task.FromResult(true);
            public void Start(string fileName, string arguments)
                => throw new InvalidOperationException("Unexpected app launch");
        }

        private sealed class NeverPreviewReleaseClient : IPreviewReleaseClient
        {
            public Task<PreviewReleaseDownload> DownloadLatestAsync(Action<double> progress,
                CancellationToken token) => throw new InvalidOperationException("Unexpected preview download");
        }
    }
}
