using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using VoltManager.Services;
using VoltManager.Setup.Engine;

namespace VoltManager.Setup.Tests
{
    public sealed class UninstallResidualTests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Running_uninstaller_is_scheduled_for_reboot_instead_of_aborting_cleanup(bool schedulingSucceeds)
        {
            string root = CreateTempDirectory();
            try
            {
                (string installDir, string zip) = CreateInstall(root);
                string uninstaller = Path.Combine(installDir, "uninstall.exe");
                File.Copy(typeof(InstallManifest).Assembly.Location, uninstaller);

                UninstallCleanupResult cleanup;
                var scheduled = new List<string>();
                // Simulate scheduling: tests must never change PendingFileRenameOperations.
                using (new FileStream(uninstaller, FileMode.Open, FileAccess.Read, FileShare.Read))
                    cleanup = InstallManifest.DeleteOwnedEntriesTolerant(installDir, zip, path =>
                    {
                        scheduled.Add(path);
                        return schedulingSucceeds;
                    });

                Assert.False(File.Exists(Path.Combine(installDir, "VoltManager.dll")));
                Assert.Empty(cleanup.Retained);
                if (schedulingSucceeds)
                {
                    Assert.Empty(cleanup.Failures);
                    Assert.Equal(new[] { uninstaller, installDir }, cleanup.PendingReboot);
                    Assert.Equal(cleanup.PendingReboot, scheduled);
                    Assert.False(File.Exists(Path.Combine(installDir, InstallManifest.FileName)));
                }
                else
                {
                    Assert.Contains(cleanup.Failures, failure => failure.StartsWith("uninstall.exe", StringComparison.Ordinal));
                    Assert.Empty(cleanup.PendingReboot);
                    Assert.Equal(new[] { uninstaller }, scheduled);
                }
            }
            finally { Cleanup(root); }
        }

        [Fact]
        public void Foreign_file_in_install_dir_is_kept_and_cleanup_continues()
        {
            string root = CreateTempDirectory();
            try
            {
                (string installDir, string zip) = CreateInstall(root);
                string personal = Path.Combine(installDir, "personal.txt");
                File.WriteAllText(personal, "keep");

                UninstallCleanupResult cleanup = InstallManifest.DeleteOwnedEntriesTolerant(installDir, zip);

                Assert.Empty(cleanup.Failures);
                Assert.Equal(new[] { "personal.txt" }, cleanup.Retained);
                Assert.Equal("keep", File.ReadAllText(personal));
                Assert.False(File.Exists(Path.Combine(installDir, "VoltManager.dll")));
            }
            finally { Cleanup(root); }
        }

        [Fact]
        public void Unlisted_hardlink_is_kept_untouched_and_listed_hardlink_aborts()
        {
            string root = CreateTempDirectory();
            try
            {
                (string installDir, string zip) = CreateInstall(root);
                string outside = Path.Combine(root, "outside.txt");
                File.WriteAllText(outside, "foreign");
                string unlisted = Path.Combine(installDir, "linked.txt");
                Assert.True(CreateHardLink(unlisted, outside, IntPtr.Zero));

                UninstallCleanupResult cleanup = InstallManifest.DeleteOwnedEntriesTolerant(installDir, zip);

                Assert.Empty(cleanup.Failures);
                Assert.Equal(new[] { "linked.txt" }, cleanup.Retained);
                Assert.Equal("foreign", File.ReadAllText(unlisted));
                Assert.Equal("foreign", File.ReadAllText(outside));

                (string second, string secondZip) = CreateInstall(Path.Combine(root, "second"));
                string dll = Path.Combine(second, "VoltManager.dll");
                string alias = Path.Combine(root, "alias.dll");
                Assert.True(CreateHardLink(alias, dll, IntPtr.Zero));

                Assert.NotEmpty(InstallManifest.DeleteOwnedEntriesTolerant(second, secondZip).Failures);
                Assert.True(File.Exists(dll));
                Assert.True(File.Exists(Path.Combine(second, InstallManifest.FileName)));
            }
            finally { Cleanup(root); }
        }

        [Fact]
        public void Swap_leftovers_require_matching_content_not_only_familiar_names()
        {
            string root = CreateTempDirectory();
            try
            {
                (string installDir, string zip) = CreateInstall(root);
                string known = Path.Combine(root, ".synthetic-install.backup-old");
                Directory.CreateDirectory(known);
                File.WriteAllText(Path.Combine(known, "VoltManager.dll"), "payload");
                string modified = Path.Combine(root, ".synthetic-install.backup-modified");
                Directory.CreateDirectory(modified);
                File.WriteAllText(Path.Combine(modified, "VoltManager.dll"), "foreign content, familiar name");
                string foreign = Path.Combine(root, ".synthetic-install.staging-foreign");
                Directory.CreateDirectory(foreign);
                File.WriteAllText(Path.Combine(foreign, "private.txt"), "keep");

                IReadOnlyList<string> retained = InstallManifest.DeleteVerifiedSwapLeftovers(
                    installDir, zip, requireProtectedLocation: false);

                Assert.False(Directory.Exists(known));
                Assert.Equal(new[] { modified, foreign }.OrderBy(path => path), retained.OrderBy(path => path));
                Assert.Equal("foreign content, familiar name", File.ReadAllText(Path.Combine(modified, "VoltManager.dll")));
                Assert.Equal("keep", File.ReadAllText(Path.Combine(foreign, "private.txt")));
            }
            finally { Cleanup(root); }
        }

        [Fact]
        public void Swap_leftovers_outside_program_files_are_never_touched_by_default()
        {
            string root = CreateTempDirectory();
            try
            {
                (string installDir, string zip) = CreateInstall(root);
                string known = Path.Combine(root, ".synthetic-install.backup-old");
                Directory.CreateDirectory(known);
                File.WriteAllText(Path.Combine(known, "VoltManager.dll"), "previous version");

                Assert.Empty(InstallManifest.DeleteVerifiedSwapLeftovers(installDir, zip));
                Assert.True(Directory.Exists(known));
            }
            finally { Cleanup(root); }
        }

        [Fact]
        public void AppData_keeps_settings_and_history_but_removes_credentials_logs_and_caches()
        {
            string root = CreateTempDirectory();
            try
            {
                string appData = Path.Combine(root, "VoltManager");
                Directory.CreateDirectory(Path.Combine(appData, "logs"));
                Directory.CreateDirectory(Path.Combine(appData, "WebView2", "Default"));
                Directory.CreateDirectory(Path.Combine(appData, "launcher-shortcuts"));
                File.WriteAllText(Path.Combine(appData, "settings.json"), "{}");
                File.WriteAllText(Path.Combine(appData, "battery-history.json"), "[]");
                File.WriteAllText(Path.Combine(appData, "remote-control-auth.json"), "secret");
                File.WriteAllText(Path.Combine(appData, "logs", "voltmanager.log"), "log");
                var result = new UninstallResult();

                IReadOnlyList<string> kept = UninstallAppData.DeleteEntries(
                    appData, HardenedInstallEngine.KeptUserDataEntries, result);

                Assert.Empty(result.Failures);
                Assert.Equal(
                    new[] { "battery-history.json", "launcher-shortcuts", "settings.json" },
                    kept.OrderBy(name => name, StringComparer.Ordinal));
                Assert.False(File.Exists(Path.Combine(appData, "remote-control-auth.json")));
                Assert.False(Directory.Exists(Path.Combine(appData, "logs")));
                Assert.False(Directory.Exists(Path.Combine(appData, "WebView2")));
                Assert.Empty(HardenedInstallEngine.RemainingAppDataEntries(appData, HardenedInstallEngine.KeptUserDataEntries));
            }
            finally { Cleanup(root); }
        }

        [Fact]
        public void AppData_purge_removes_everything_including_the_folder()
        {
            string root = CreateTempDirectory();
            try
            {
                string appData = Path.Combine(root, "VoltManager");
                Directory.CreateDirectory(appData);
                File.WriteAllText(Path.Combine(appData, "settings.json"), "{}");
                var result = new UninstallResult();

                Assert.Empty(UninstallAppData.DeleteEntries(appData, Array.Empty<string>(), result));

                Assert.Empty(result.Failures);
                Assert.False(Directory.Exists(appData));
            }
            finally { Cleanup(root); }
        }

        [Fact]
        public void AppData_junction_is_removed_without_touching_its_target()
        {
            string root = CreateTempDirectory();
            string appData = Path.Combine(root, "VoltManager");
            string link = Path.Combine(appData, "WebView2");
            string target = Path.Combine(root, "victim");
            try
            {
                Directory.CreateDirectory(appData);
                Directory.CreateDirectory(target);
                string victim = Path.Combine(target, "system.ini");
                File.WriteAllText(victim, "foreign");
                File.SetAttributes(victim, FileAttributes.ReadOnly);
                CreateJunction(link, target);
                var result = new UninstallResult();

                UninstallAppData.DeleteEntries(appData, Array.Empty<string>(), result);

                Assert.Empty(result.Failures);
                Assert.False(Directory.Exists(link));
                Assert.Equal("foreign", File.ReadAllText(victim));
                Assert.True((File.GetAttributes(victim) & FileAttributes.ReadOnly) != 0);
            }
            finally
            {
                try { if (Directory.Exists(link)) Directory.Delete(link, false); } catch { }
                try { File.SetAttributes(Path.Combine(target, "system.ini"), FileAttributes.Normal); } catch { }
                Cleanup(root);
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Locked_AppData_file_records_only_successful_reboot_scheduling(bool success)
        {
            string root = CreateTempDirectory();
            try
            {
                string file = Path.Combine(root, "remote-control-auth.json");
                File.WriteAllText(file, "credential");
                var result = new UninstallResult();
                var scheduled = new List<string>();
                using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                    UninstallAppData.DeleteEntries(root, Array.Empty<string>(), result, path =>
                    {
                        scheduled.Add(path);
                        return success;
                    });
                if (success)
                {
                    Assert.Empty(result.Failures);
                    Assert.Equal(new[] { file, root }, result.PendingReboot);
                    Assert.Single(result.Warnings);
                }
                else
                {
                    Assert.NotEmpty(result.Failures);
                    Assert.Empty(result.PendingReboot);
                    Assert.Empty(result.Warnings);
                }
            }
            finally { Cleanup(root); }
        }

        [Fact]
        public void Linked_AppData_parent_is_rejected_without_touching_target()
        {
            string root = CreateTempDirectory();
            string link = Path.Combine(root, "redirect");
            string target = Path.Combine(root, "target");
            try
            {
                string appData = Path.Combine(target, "VoltManager");
                Directory.CreateDirectory(appData);
                string sentinel = Path.Combine(appData, "settings.json");
                File.WriteAllText(sentinel, "personal");
                CreateJunction(link, target);
                var result = new UninstallResult();
                UninstallAppData.DeleteEntries(Path.Combine(link, "VoltManager"), Array.Empty<string>(), result);
                Assert.NotEmpty(result.Failures);
                Assert.Equal("personal", File.ReadAllText(sentinel));
                Assert.False(WebView2UpdateCacheCleaner.TryClear(Path.Combine(link, "VoltManager"), out _));
                Assert.Equal("personal", File.ReadAllText(sentinel));
            }
            finally
            {
                try { if (Directory.Exists(link)) Directory.Delete(link, false); } catch { }
                Cleanup(root);
            }
        }

        [Theory]
        [InlineData("aaaaaaaa-1111-2222-3333-444444444444", true)]
        [InlineData("381b4222-f694-41f0-9685-ff5bb260df2e", false)]
        [InlineData("bbbbbbbb-1111-2222-3333-444444444444", false)]
        [InlineData(null, false)]
        public void Power_plan_is_restored_only_when_a_VoltManager_duplicate_is_active(string? active, bool expected)
        {
            var managed = new[] { "aaaaaaaa-1111-2222-3333-444444444444", "381b4222-f694-41f0-9685-ff5bb260df2e" };
            Assert.Equal(expected, PowerPlanRestore.ShouldRestore(active, managed));
        }

        [Fact]
        public void Power_plan_map_is_read_from_settings_and_active_guid_from_powercfg_output()
        {
            string root = CreateTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(root, "settings.json"),
                    "{\"planGuidMap\":{\"Balanced\":\"aaaaaaaa-1111-2222-3333-444444444444\",\"Bad\":\"x\"}}");

                Assert.Equal(new[] { "aaaaaaaa-1111-2222-3333-444444444444" }, PowerPlanRestore.ReadManagedPlanGuids(root));
                Assert.Empty(PowerPlanRestore.ReadManagedPlanGuids(Path.Combine(root, "missing")));
                Assert.Equal("aaaaaaaa-1111-2222-3333-444444444444", PowerPlanRestore.ParseActiveGuid(
                    "GUID combinazione risparmio energia: AAAAAAAA-1111-2222-3333-444444444444  (VoltManager)"));
            }
            finally { Cleanup(root); }
        }

        [Fact]
        public void Interactive_profile_helpers_compare_sids_and_compose_roaming_path()
        {
            Assert.False(InteractiveUserProfile.IsDifferentUser("S-1-5-21-1", "s-1-5-21-1"));
            Assert.False(InteractiveUserProfile.IsDifferentUser(null, "S-1-5-21-1"));
            Assert.True(InteractiveUserProfile.IsDifferentUser("S-1-5-21-2", "S-1-5-21-1"));
            Assert.Equal(@"C:\Users\someone\AppData\Roaming\VoltManager",
                InteractiveUserProfile.ComposeAppData(@"C:\Users\someone"));
        }

        [Fact]
        public void Running_from_own_update_run_dir_does_not_report_Updates_as_a_failure()
        {
            string root = CreateTempDirectory();
            string? previous = Environment.GetEnvironmentVariable("VOLTMANAGER_VALIDATION_ROOT");
            try
            {
                Environment.SetEnvironmentVariable("VOLTMANAGER_VALIDATION_ROOT", root);
                string runDir = Path.Combine(VoltManagerArtifacts.UpdatesDirectory, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(runDir);
                string self = Path.Combine(runDir, "self.exe");
                File.WriteAllText(self, "running");

                string[] failures = VoltManagerArtifacts.CleanupOwnedTempArtifacts(Path.GetTempPath(), self);

                Assert.DoesNotContain(failures, failure => failure.StartsWith("Updates directory", StringComparison.Ordinal));
                Assert.True(File.Exists(self));
                Assert.DoesNotContain(VoltManagerArtifacts.UpdatesDirectory,
                    VoltManagerArtifacts.FindOwnedTempArtifacts(Path.GetTempPath(), self));
            }
            finally
            {
                Environment.SetEnvironmentVariable("VOLTMANAGER_VALIDATION_ROOT", previous);
                Cleanup(root);
            }
        }

        [Fact]
        public void Certificate_private_key_cleanup_deletes_only_the_fixture_key()
        {
            string name = "VoltManagerSetupTests-" + Guid.NewGuid().ToString("N");
            var parameters = new System.Security.Cryptography.CngKeyCreationParameters
            {
                KeyUsage = System.Security.Cryptography.CngKeyUsages.Signing,
            };
            using (var key = System.Security.Cryptography.CngKey.Create(
                System.Security.Cryptography.CngAlgorithm.Rsa, name, parameters))
            {
                try
                {
                    using (var rsa = new System.Security.Cryptography.RSACng(key))
                    {
                        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
                            "CN=VoltManager test fixture", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256,
                            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
                        using (var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1)))
                        {
                            Assert.True(System.Security.Cryptography.CngKey.Exists(name));
                            var result = new UninstallResult();
                            SystemUninstallSystemOperations.TryDeletePrivateKey(certificate, result);
                            Assert.Empty(result.Warnings);
                            Assert.False(System.Security.Cryptography.CngKey.Exists(name));
                        }
                    }
                }
                finally
                {
                    if (System.Security.Cryptography.CngKey.Exists(name)) key.Delete();
                }
            }
        }

        [Fact]
        public void Purge_flag_is_parsed_for_uninstall()
        {
            Assert.True(SetupArgs.Parse(new[] { "/uninstall", "/silent", "/purge" }).PurgeUserData);
            Assert.False(SetupArgs.Parse(new[] { "/uninstall", "/SILENT" }).PurgeUserData);
        }

        private static (string InstallDir, string Zip) CreateInstall(string root)
        {
            Directory.CreateDirectory(root);
            string installDir = Path.Combine(root, "synthetic-install");
            string source = Path.Combine(root, "synthetic-payload");
            Directory.CreateDirectory(installDir);
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "VoltManager.dll"), "payload");
            File.WriteAllText(Path.Combine(installDir, "VoltManager.dll"), "payload");
            string zip = Path.Combine(root, "payload.zip");
            ZipFile.CreateFromDirectory(source, zip);
            InstallManifest.Write(installDir, new[] { "VoltManager.dll" });
            return (installDir, zip);
        }

        private static void CreateJunction(string link, string target)
        {
            using (Process? process = Process.Start(new ProcessStartInfo(
                "cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            }))
            {
                Assert.NotNull(process);
                Assert.True(process!.WaitForExit(5000));
                Assert.Equal(0, process.ExitCode);
            }
        }

        private static string CreateTempDirectory()
        {
            string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        private static void Cleanup(string root)
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* synthetic fixture */ }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool CreateHardLink(string newFile, string existingFile, IntPtr securityAttributes);
    }
}
