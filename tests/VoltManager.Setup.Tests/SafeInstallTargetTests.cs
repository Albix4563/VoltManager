using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using VoltManager.Setup.Engine;

namespace VoltManager.Setup.Tests
{
    public sealed class SafeInstallTargetTests
    {
        [Fact]
        public void Non_empty_unowned_directory_is_rejected_with_safe_suggestion()
        {
            string root = CreateTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(root, "personal.txt"), "keep");

                InstallTargetValidationResult result = InstallTargetValidator.ValidateInstallTarget(root);

                Assert.False(result.Ok);
                Assert.False(string.IsNullOrWhiteSpace(result.Reason));
                Assert.Equal(Path.Combine(root, "VoltManager"), result.SuggestedPath);
            }
            finally
            {
                Cleanup(root);
            }
        }

        [Fact]
        public void Empty_and_nonexistent_directories_are_accepted()
        {
            string root = CreateTempDirectory();
            string missing = Path.Combine(root, "missing");
            try
            {
                Assert.True(InstallTargetValidator.ValidateInstallTarget(root).Ok);
                Assert.True(InstallTargetValidator.ValidateInstallTarget(missing).Ok);
            }
            finally
            {
                Cleanup(root);
            }
        }

        [Fact]
        public void Legacy_manifest_cannot_replace_unverified_known_app_file_even_if_unrelated_data_is_preserved()
        {
            string root = CreateTempDirectory();
            string staging = CreateTempDirectory();
            string backupRoot = CreateTempDirectory();
            string backup = Path.Combine(backupRoot, "backup-not-yet-created");
            string trustedZip = Path.Combine(backupRoot, "new-payload.zip");
            try
            {
                File.WriteAllText(Path.Combine(root, "VoltManager.exe"), "old");
                File.WriteAllText(Path.Combine(root, "personal.txt"), "keep");
                InstallManifest.Write(root, new[] { "VoltManager.exe" });
                File.WriteAllText(Path.Combine(staging, "VoltManager.exe"), "new");
                File.WriteAllText(Path.Combine(staging, "new-app.txt"), "new payload");
                ZipFile.CreateFromDirectory(staging, trustedZip);

                Assert.True(InstallTargetValidator.ValidateInstallTarget(root).Ok);

                string marker = Path.Combine(root, InstallManifest.FileName);
                byte[] originalMarker = File.ReadAllBytes(marker);
                Assert.Throws<InvalidDataException>(() =>
                    InstallEngine.ReplaceInstallDirectoryContents(root, staging, backup, trustedZip));

                Assert.Equal("old", File.ReadAllText(Path.Combine(root, "VoltManager.exe")));
                Assert.False(File.Exists(Path.Combine(root, "new-app.txt")));
                Assert.Equal("keep", File.ReadAllText(Path.Combine(root, "personal.txt")));
                string[] manifest = InstallManifest.ReadOwnedEntries(root).OrderBy(value => value).ToArray();
                Assert.Equal(new[] { "VoltManager.exe" }, manifest);
                Assert.Equal(originalMarker, File.ReadAllBytes(marker));
                Assert.Equal("new", File.ReadAllText(Path.Combine(staging, "VoltManager.exe")));
                Assert.False(Directory.Exists(backup));
            }
            finally
            {
                Cleanup(root);
                Cleanup(staging);
                Cleanup(backupRoot);
            }
        }

        [Theory]
        [InlineData("VoltManager.exe")]
        [InlineData("VoltManager.dll")]
        public void Forged_legacy_marker_must_not_replace_foreign_app_named_bytes_using_new_payload_zip(string name)
        {
            string root = CreateTempDirectory();
            string staging = CreateTempDirectory();
            string backupRoot = CreateTempDirectory();
            string backup = Path.Combine(backupRoot, "new-backup");
            string zip = Path.Combine(backupRoot, "trusted-new.zip");
            try
            {
                string foreign = Path.Combine(root, name);
                string marker = Path.Combine(root, InstallManifest.FileName);
                File.WriteAllText(foreign, "unrelated preexisting personal bytes");
                File.WriteAllText(Path.Combine(root, "family.txt"), "family data");
                // This old-format marker is untrusted and may be created by anyone.
                File.WriteAllText(marker, "{\"entries\":[\"" + name + "\"]}");
                byte[] beforeMarker = File.ReadAllBytes(marker);
                File.WriteAllText(Path.Combine(staging, name), "signed new release payload");
                ZipFile.CreateFromDirectory(staging, zip);

                Assert.Throws<InvalidDataException>(() => InstallEngine.ReplaceInstallDirectoryContents(
                    root, staging, backup, zip));

                Assert.Equal("unrelated preexisting personal bytes", File.ReadAllText(foreign));
                Assert.Equal("family data", File.ReadAllText(Path.Combine(root, "family.txt")));
                Assert.Equal(beforeMarker, File.ReadAllBytes(marker));
                Assert.Equal("signed new release payload", File.ReadAllText(Path.Combine(staging, name)));
                Assert.False(Directory.Exists(backup));
                Assert.False(File.Exists(Path.Combine(backup, name)));
            }
            finally
            {
                Cleanup(root);
                Cleanup(staging);
                Cleanup(backupRoot);
            }
        }

        [Fact]
        public void Previous_release_can_be_verified_against_independent_old_archive_not_the_new_archive()
        {
            string root = CreateTempDirectory();
            string oldPayload = CreateTempDirectory();
            string newPayload = CreateTempDirectory();
            string archiveRoot = CreateTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(oldPayload, "VoltManager.dll"), "signed old release contents");
                File.WriteAllText(Path.Combine(newPayload, "VoltManager.dll"), "signed new release contents");
                File.WriteAllText(Path.Combine(root, "VoltManager.dll"), "signed old release contents");
                InstallManifest.Write(root, new[] { "VoltManager.dll" });
                string oldArchive = Path.Combine(archiveRoot, "old.zip");
                string newArchive = Path.Combine(archiveRoot, "new.zip");
                ZipFile.CreateFromDirectory(oldPayload, oldArchive);
                ZipFile.CreateFromDirectory(newPayload, newArchive);

                Assert.Throws<InvalidDataException>(() => InstallManifest.GetOwnedNamesForReplacement(
                    root, new[] { "VoltManager.dll" }, newArchive));

                var verifiedNames = InstallManifest.GetOwnedNamesForReplacement(
                    root, new[] { "VoltManager.dll" }, newArchive,
                    trustedInstalledPayloadZipPath: oldArchive);

                Assert.Contains("VoltManager.dll", verifiedNames);
                Assert.Equal("signed old release contents", File.ReadAllText(Path.Combine(root, "VoltManager.dll")));
                Assert.True(File.Exists(Path.Combine(root, InstallManifest.FileName)));
            }
            finally
            {
                Cleanup(root);
                Cleanup(oldPayload);
                Cleanup(newPayload);
                Cleanup(archiveRoot);
            }
        }

        [Theory]
        [InlineData("VoltManager.exe")]
        [InlineData("VoltManager.dll")]
        public void Forged_legacy_marker_cannot_authorize_uninstall_of_app_named_foreign_bytes(string name)
        {
            string root = CreateTempDirectory();
            string payload = CreateTempDirectory();
            string reference = CreateTempDirectory();
            try
            {
                string foreign = Path.Combine(root, name);
                string marker = Path.Combine(root, InstallManifest.FileName);
                File.WriteAllText(foreign, "private same-named bytes");
                File.WriteAllText(marker, "{\"entries\":[\"" + name + "\"]}");
                byte[] originalMarker = File.ReadAllBytes(marker);
                File.WriteAllText(Path.Combine(payload, name), "published release bytes");
                string zip = Path.Combine(reference, "trusted-installed.zip");
                ZipFile.CreateFromDirectory(payload, zip);

                Assert.Throws<InvalidDataException>(() => InstallManifest.GetOwnedNamesForUninstall(root, zip));
                Assert.NotEmpty(InstallManifest.DeleteOwnedEntries(root, zip));
                Assert.Equal("private same-named bytes", File.ReadAllText(foreign));
                Assert.Equal(originalMarker, File.ReadAllBytes(marker));
            }
            finally
            {
                Cleanup(root);
                Cleanup(payload);
                Cleanup(reference);
            }
        }

        [Fact]
        public void Unlisted_known_name_is_not_claimed_solely_by_a_matching_payload_hash()
        {
            string root = CreateTempDirectory();
            string payload = CreateTempDirectory();
            string reference = CreateTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(root, "VoltManager.dll"), "owned candidate bytes");
                File.WriteAllText(Path.Combine(root, "VoltManager.exe"), "foreign but byte-identical to release");
                File.WriteAllText(Path.Combine(payload, "VoltManager.dll"), "owned candidate bytes");
                File.WriteAllText(Path.Combine(payload, "VoltManager.exe"), "foreign but byte-identical to release");
                string zip = Path.Combine(reference, "trusted.zip");
                ZipFile.CreateFromDirectory(payload, zip);
                InstallManifest.Write(root, new[] { "VoltManager.dll" });

                Assert.Throws<InvalidDataException>(() => InstallManifest.GetOwnedNamesForReplacement(
                    root, new[] { "VoltManager.dll", "VoltManager.exe" }, zip));
                Assert.NotEmpty(InstallManifest.DeleteOwnedEntries(root, zip));
                Assert.Equal("foreign but byte-identical to release",
                    File.ReadAllText(Path.Combine(root, "VoltManager.exe")));
                Assert.True(File.Exists(Path.Combine(root, InstallManifest.FileName)));
            }
            finally
            {
                Cleanup(root);
                Cleanup(payload);
                Cleanup(reference);
            }
        }

        [Fact]
        public void Marker_only_claims_cannot_authorize_replacement_without_real_verified_payload_bytes()
        {
            string root = CreateTempDirectory();
            string payload = CreateTempDirectory();
            string reference = CreateTempDirectory();
            try
            {
                // Every byte of the alleged old install is a self-authored marker:
                // even the independently trusted ZIP does not prove a file exists.
                File.WriteAllText(Path.Combine(payload, "VoltManager.dll"), "published bytes");
                string zip = Path.Combine(reference, "trusted.zip");
                ZipFile.CreateFromDirectory(payload, zip);
                InstallManifest.Write(root, new[] { "VoltManager.dll" });
                byte[] marker = File.ReadAllBytes(Path.Combine(root, InstallManifest.FileName));

                Assert.Throws<InvalidDataException>(() => InstallManifest.GetOwnedNamesForReplacement(
                    root, new[] { "VoltManager.dll" }, zip));
                Assert.Equal(marker, File.ReadAllBytes(Path.Combine(root, InstallManifest.FileName)));
                Assert.False(File.Exists(Path.Combine(root, "VoltManager.dll")));
            }
            finally { Cleanup(root); Cleanup(payload); Cleanup(reference); }
        }

        [Fact]
        public void Missing_manifest_listed_payload_file_blocks_replacement_even_if_another_file_matches()
        {
            string root = CreateTempDirectory();
            string payload = CreateTempDirectory();
            string reference = CreateTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(payload, "VoltManager.dll"), "verified dll");
                File.WriteAllText(Path.Combine(payload, "VoltManager.exe"), "verified exe");
                File.WriteAllText(Path.Combine(root, "VoltManager.dll"), "verified dll");
                InstallManifest.Write(root, new[] { "VoltManager.dll", "VoltManager.exe" });
                string zip = Path.Combine(reference, "trusted.zip");
                ZipFile.CreateFromDirectory(payload, zip);

                Assert.Throws<InvalidDataException>(() => InstallManifest.GetOwnedNamesForReplacement(
                    root, new[] { "VoltManager.dll", "VoltManager.exe" }, zip));
                Assert.Equal("verified dll", File.ReadAllText(Path.Combine(root, "VoltManager.dll")));
                Assert.False(File.Exists(Path.Combine(root, "VoltManager.exe")));
            }
            finally { Cleanup(root); Cleanup(payload); Cleanup(reference); }
        }

        [Fact]
        public void Pre_side_effect_ownership_gate_allows_only_empty_or_missing_new_destinations()
        {
            string root = CreateTempDirectory();
            string empty = Path.Combine(root, "empty");
            string missing = Path.Combine(root, "missing");
            Directory.CreateDirectory(empty);
            try
            {
                InstallManifest.ValidateExistingInstallBeforeSideEffects(empty);
                InstallManifest.ValidateExistingInstallBeforeSideEffects(missing);
                // A new target has no installed release to compare; a missing
                // prior-version ZIP must not prevent this read-only classification.
                string missingOldZip = Path.Combine(root, "no-such-old-version.zip");
                InstallManifest.ValidateExistingInstallBeforeSideEffects(empty, missingOldZip);
                InstallManifest.ValidateExistingInstallBeforeSideEffects(missing, missingOldZip);

                File.WriteAllText(Path.Combine(empty, "family.txt"), "private data");
                Assert.Throws<InvalidDataException>(() =>
                    InstallManifest.ValidateExistingInstallBeforeSideEffects(empty));
                Assert.Equal("private data", File.ReadAllText(Path.Combine(empty, "family.txt")));
                Assert.False(Directory.Exists(missing));
            }
            finally { Cleanup(root); }
        }

        [Fact]
        public void Pre_side_effect_ownership_gate_accepts_verified_old_payload_without_touching_unrelated_data()
        {
            string root = CreateTempDirectory();
            string payload = CreateTempDirectory();
            string reference = CreateTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(payload, "VoltManager.dll"), "authenticated old dll");
                File.WriteAllText(Path.Combine(root, "VoltManager.dll"), "authenticated old dll");
                File.WriteAllText(Path.Combine(root, "family.txt"), "must remain user data");
                InstallManifest.Write(root, new[] { "VoltManager.dll" });
                string zip = Path.Combine(reference, "trusted-installed.zip");
                ZipFile.CreateFromDirectory(payload, zip);
                byte[] markerBefore = File.ReadAllBytes(Path.Combine(root, InstallManifest.FileName));

                InstallManifest.ValidateExistingInstallBeforeSideEffects(root, zip);

                Assert.Equal("authenticated old dll", File.ReadAllText(Path.Combine(root, "VoltManager.dll")));
                Assert.Equal("must remain user data", File.ReadAllText(Path.Combine(root, "family.txt")));
                Assert.Equal(markerBefore, File.ReadAllBytes(Path.Combine(root, InstallManifest.FileName)));
            }
            finally { Cleanup(root); Cleanup(payload); Cleanup(reference); }
        }

        [Theory]
        [InlineData("marker-only")]
        [InlineData("missing-listed")]
        [InlineData("orphan-trusted-file")]
        [InlineData("orphan-voltmanager-dll")]
        public void Pre_side_effect_ownership_gate_rejects_partial_or_orphan_existing_installations(string variant)
        {
            string root = CreateTempDirectory();
            string payload = CreateTempDirectory();
            string reference = CreateTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(payload, "VoltManager.dll"), "trusted original");
                File.WriteAllText(Path.Combine(payload, "VoltManager.exe"), "trusted executable");
                if (variant != "marker-only")
                    File.WriteAllText(Path.Combine(root, "VoltManager.dll"), "trusted original");
                if (variant == "orphan-trusted-file")
                    File.WriteAllText(Path.Combine(root, "VoltManager.exe"), "trusted executable");
                if (variant == "orphan-voltmanager-dll")
                    File.WriteAllText(Path.Combine(root, "VoltManager.Orphan.dll"), "foreign binary");
                File.WriteAllText(Path.Combine(root, "personal.txt"), "private data");
                InstallManifest.Write(root, variant == "missing-listed"
                    ? new[] { "VoltManager.dll", "VoltManager.exe" }
                    : new[] { "VoltManager.dll" });
                string zip = Path.Combine(reference, "trusted-old.zip");
                ZipFile.CreateFromDirectory(payload, zip);
                byte[] markerBefore = File.ReadAllBytes(Path.Combine(root, InstallManifest.FileName));

                Assert.Throws<InvalidDataException>(() =>
                    InstallManifest.ValidateExistingInstallBeforeSideEffects(root, zip));

                Assert.Equal("private data", File.ReadAllText(Path.Combine(root, "personal.txt")));
                Assert.Equal(markerBefore, File.ReadAllBytes(Path.Combine(root, InstallManifest.FileName)));
                if (variant == "orphan-trusted-file")
                    Assert.Equal("trusted executable", File.ReadAllText(Path.Combine(root, "VoltManager.exe")));
                if (variant == "orphan-voltmanager-dll")
                    Assert.Equal("foreign binary", File.ReadAllText(Path.Combine(root, "VoltManager.Orphan.dll")));
            }
            finally { Cleanup(root); Cleanup(payload); Cleanup(reference); }
        }

        [Fact]
        public void Pre_side_effect_ownership_gate_requires_old_inventory_not_an_unrelated_new_release()
        {
            string root = CreateTempDirectory();
            string oldPayload = CreateTempDirectory();
            string newPayload = CreateTempDirectory();
            string reference = CreateTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(oldPayload, "VoltManager.dll"), "prior version");
                File.WriteAllText(Path.Combine(newPayload, "VoltManager.dll"), "upcoming version");
                File.WriteAllText(Path.Combine(root, "VoltManager.dll"), "prior version");
                InstallManifest.Write(root, new[] { "VoltManager.dll" });
                string oldZip = Path.Combine(reference, "old.zip");
                string newZip = Path.Combine(reference, "new.zip");
                ZipFile.CreateFromDirectory(oldPayload, oldZip);
                ZipFile.CreateFromDirectory(newPayload, newZip);

                Assert.Throws<InvalidDataException>(() =>
                    InstallManifest.ValidateExistingInstallBeforeSideEffects(root, newZip));
                InstallManifest.ValidateExistingInstallBeforeSideEffects(root, oldZip);
                Assert.Equal("prior version", File.ReadAllText(Path.Combine(root, "VoltManager.dll")));
            }
            finally { Cleanup(root); Cleanup(oldPayload); Cleanup(newPayload); Cleanup(reference); }
        }

        [Fact]
        public void Stable_staging_verification_does_not_use_a_mutable_temp_zip_as_implicit_trust()
        {
            string staging = CreateTempDirectory();
            string reference = CreateTempDirectory();
            try
            {
                string uniqueName = "arbitrary-staged-file-" + Guid.NewGuid().ToString("N") + ".dll";
                File.WriteAllText(Path.Combine(staging, uniqueName), "unverified bytes");
                string untrustedTempZip = Path.Combine(reference, "rewritten-payload.zip");
                ZipFile.CreateFromDirectory(staging, untrustedTempZip);

                // A caller-provided synthetic archive can verify a test fixture,
                // but stable production must request the running setup's embedded
                // inventory and never infer trust from this writable ZIP.
                InstallManifest.ValidateStagedPayload(staging, untrustedTempZip);
                Assert.Throws<InvalidDataException>(() =>
                    InstallManifest.ValidateStagedPayload(staging));
                Assert.Equal("unverified bytes", File.ReadAllText(Path.Combine(staging, uniqueName)));
                Assert.True(File.Exists(untrustedTempZip));
            }
            finally { Cleanup(staging); Cleanup(reference); }
        }

        [Fact]
        public void Protected_system_and_profile_locations_are_rejected()
        {
            string driveRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            Assert.False(InstallTargetValidator.ValidateInstallTarget(driveRoot).Ok);
            Assert.False(InstallTargetValidator.ValidateInstallTarget(windows).Ok);
            Assert.False(InstallTargetValidator.ValidateInstallTarget(profile).Ok);
            Assert.False(InstallTargetValidator.ValidateInstallTarget(documents).Ok);
        }

        [Fact]
        public void Explicit_uninstall_target_rejects_root_profile_and_unowned_directory()
        {
            string root = CreateTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(root, "personal.txt"), "keep");
                string driveRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                Assert.False(InstallTargetValidator.ValidateUninstallTarget(driveRoot).Ok);
                Assert.False(InstallTargetValidator.ValidateUninstallTarget(profile).Ok);
                Assert.False(InstallTargetValidator.ValidateUninstallTarget(root).Ok);

                File.WriteAllText(Path.Combine(root, "VoltManager.exe"), "");
                Assert.True(InstallTargetValidator.ValidateUninstallTarget(root).Ok);
            }
            finally
            {
                Cleanup(root);
            }
        }

        [Fact]
        public void Uninstall_with_unverifiable_legacy_entry_preserves_files_and_marker()
        {
            string root = CreateTempDirectory();
            string payload = CreateTempDirectory();
            string reference = CreateTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(root, "app.dat"), "owned");
                File.WriteAllText(Path.Combine(root, "VoltManager.exe"), "owned known file");
                File.WriteAllText(Path.Combine(root, "personal.txt"), "keep");
                File.WriteAllText(Path.Combine(payload, "VoltManager.exe"), "owned known file");
                string trustedZip = Path.Combine(reference, "payload.zip");
                ZipFile.CreateFromDirectory(payload, trustedZip);
                InstallManifest.Write(root, new[] { "app.dat" });

                var failures = InstallManifest.DeleteOwnedEntries(root, trustedZip);

                Assert.NotEmpty(failures);
                Assert.Contains(failures, failure => failure.Contains("app.dat"));
                Assert.Equal("owned", File.ReadAllText(Path.Combine(root, "app.dat")));
                Assert.True(File.Exists(Path.Combine(root, "VoltManager.exe")));
                Assert.True(File.Exists(Path.Combine(root, InstallManifest.FileName)));
                Assert.Equal("keep", File.ReadAllText(Path.Combine(root, "personal.txt")));
                Assert.True(Directory.Exists(root));
            }
            finally
            {
                Cleanup(root);
                Cleanup(payload);
                Cleanup(reference);
            }
        }

        [Fact]
        public void Uninstall_cleanup_of_recognized_app_file_leaves_user_files()
        {
            string root = CreateTempDirectory();
            string payload = CreateTempDirectory();
            string zipDir = CreateTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(root, "VoltManager.exe"), "application");
                File.WriteAllText(Path.Combine(payload, "VoltManager.exe"), "application");
                string zip = Path.Combine(zipDir, "payload.zip");
                ZipFile.CreateFromDirectory(payload, zip);
                InstallManifest.Write(root, new[] { "VoltManager.exe" });

                Assert.Empty(InstallManifest.DeleteOwnedEntries(root, zip));

                Assert.False(File.Exists(Path.Combine(root, "VoltManager.exe")));
                Assert.False(File.Exists(Path.Combine(root, InstallManifest.FileName)));
                Assert.False(Directory.Exists(root));
            }
            finally
            {
                Cleanup(root);
                Cleanup(payload);
                Cleanup(zipDir);
            }
        }

        [Fact]
        public void Known_only_manifest_with_residual_tree_cannot_claim_success()
        {
            string root = CreateTempDirectory();
            string wwwroot = Path.Combine(root, "wwwroot");
            try
            {
                Directory.CreateDirectory(wwwroot);
                File.WriteAllText(Path.Combine(root, "VoltManager.exe"), "application");
                File.WriteAllText(Path.Combine(wwwroot, "index.html"), "keep web content");
                InstallManifest.Write(root, new[] { "VoltManager.exe" });

                var failures = InstallManifest.DeleteOwnedEntries(root);

                Assert.NotEmpty(failures);
                Assert.Equal("keep web content", File.ReadAllText(Path.Combine(wwwroot, "index.html")));
                Assert.True(File.Exists(Path.Combine(root, InstallManifest.FileName)));
                Assert.True(InstallManifest.HasOwnedArtifacts(root));
            }
            finally { Cleanup(root); }
        }

        [Fact]
        public void Verified_payload_inventory_allows_real_managed_files_and_nested_web_assets()
        {
            string root = CreateTempDirectory();
            string payload = CreateTempDirectory();
            string zipDir = CreateTempDirectory();
            string zip = Path.Combine(zipDir, "payload.zip");
            try
            {
                File.WriteAllText(Path.Combine(payload, "VoltManager.dll"), "binary payload");
                File.WriteAllText(Path.Combine(payload, "VoltManager.runtimeconfig.json"), "{}");
                Directory.CreateDirectory(Path.Combine(payload, "wwwroot", "js"));
                File.WriteAllText(Path.Combine(payload, "wwwroot", "index.html"), "<html></html>");
                File.WriteAllText(Path.Combine(payload, "wwwroot", "js", "app.js"), "// bundled asset");
                ZipFile.CreateFromDirectory(payload, zip);

                File.Copy(Path.Combine(payload, "VoltManager.dll"), Path.Combine(root, "VoltManager.dll"));
                File.Copy(Path.Combine(payload, "VoltManager.runtimeconfig.json"), Path.Combine(root, "VoltManager.runtimeconfig.json"));
                Directory.CreateDirectory(Path.Combine(root, "wwwroot", "js"));
                File.Copy(Path.Combine(payload, "wwwroot", "index.html"), Path.Combine(root, "wwwroot", "index.html"));
                File.Copy(Path.Combine(payload, "wwwroot", "js", "app.js"), Path.Combine(root, "wwwroot", "js", "app.js"));
                InstallManifest.Write(root, new[] { "VoltManager.dll", "VoltManager.runtimeconfig.json", "wwwroot" });

                var replacement = InstallManifest.GetOwnedNamesForReplacement(root,
                    new[] { "VoltManager.dll", "VoltManager.runtimeconfig.json", "wwwroot" }, zip);
                Assert.Contains("wwwroot", replacement);
                Assert.Contains("VoltManager.dll", replacement);
                Assert.Empty(InstallManifest.DeleteOwnedEntries(root, zip));
                Assert.False(Directory.Exists(root));
            }
            finally
            {
                Cleanup(root);
                Cleanup(payload);
                Cleanup(zipDir);
            }
        }

        [Fact]
        public void Payload_inventory_does_not_claim_unlisted_nested_file_or_foreign_manifest_entry()
        {
            string root = CreateTempDirectory();
            string payload = CreateTempDirectory();
            string zipDir = CreateTempDirectory();
            string zip = Path.Combine(zipDir, "payload.zip");
            try
            {
                Directory.CreateDirectory(Path.Combine(payload, "wwwroot"));
                File.WriteAllText(Path.Combine(payload, "wwwroot", "index.html"), "web payload");
                ZipFile.CreateFromDirectory(payload, zip);
                Directory.CreateDirectory(Path.Combine(root, "wwwroot"));
                File.WriteAllText(Path.Combine(root, "wwwroot", "index.html"), "web payload");
                string foreign = Path.Combine(root, "wwwroot", "family.txt");
                File.WriteAllText(foreign, "private nested data");
                InstallManifest.Write(root, new[] { "wwwroot" });

                Assert.NotEmpty(InstallManifest.DeleteOwnedEntries(root, zip));
                Assert.Equal("private nested data", File.ReadAllText(foreign));
                Assert.True(File.Exists(Path.Combine(root, InstallManifest.FileName)));

                File.WriteAllText(Path.Combine(root, InstallManifest.FileName),
                    "{\"entries\":[\"wwwroot\",\"family.txt\"]}");
                Assert.NotEmpty(InstallManifest.DeleteOwnedEntries(root, zip));
                Assert.Equal("private nested data", File.ReadAllText(foreign));
            }
            finally
            {
                Cleanup(root);
                Cleanup(payload);
                Cleanup(zipDir);
            }
        }

        [Fact]
        public void Foreign_app_named_file_without_manifest_is_not_uninstalled()
        {
            string root = CreateTempDirectory();
            try
            {
                string foreign = Path.Combine(root, "VoltManager.exe");
                File.WriteAllText(foreign, "private app-named file");

                Assert.NotEmpty(InstallManifest.DeleteOwnedEntries(root));
                Assert.Equal("private app-named file", File.ReadAllText(foreign));
            }
            finally { Cleanup(root); }
        }

        [Fact]
        public void Forged_manifest_cannot_claim_foreign_known_executable_without_authoritative_payload()
        {
            string root = CreateTempDirectory();
            try
            {
                string foreign = Path.Combine(root, "VoltManager.exe");
                File.WriteAllText(foreign, "foreign executable contents");
                InstallManifest.Write(root, new[] { "VoltManager.exe" });

                Assert.NotEmpty(InstallManifest.DeleteOwnedEntries(root));
                Assert.Throws<InvalidDataException>(() =>
                    InstallManifest.GetOwnedNamesForReplacement(root, new[] { "VoltManager.exe" }));
                Assert.Equal("foreign executable contents", File.ReadAllText(foreign));
                Assert.True(File.Exists(Path.Combine(root, InstallManifest.FileName)));
            }
            finally { Cleanup(root); }
        }

        [Fact]
        public void Initial_install_into_an_empty_directory_requires_no_prior_manifest()
        {
            string root = CreateTempDirectory();
            try
            {
                var owned = InstallManifest.GetOwnedNamesForReplacement(root, new[] { "VoltManager.exe" });
                Assert.Contains("VoltManager.exe", owned);
                Assert.Contains(InstallManifest.FileName, owned);
            }
            finally { Cleanup(root); }
        }

        [Fact]
        public void Hardlinked_payload_must_not_be_removed_even_when_bytes_match_trusted_archive()
        {
            string root = CreateTempDirectory();
            string payload = CreateTempDirectory();
            string reference = CreateTempDirectory();
            string zip = Path.Combine(reference, "payload.zip");
            try
            {
                string source = Path.Combine(reference, "shared.dll");
                File.WriteAllText(source, "valid payload bytes");
                File.WriteAllText(Path.Combine(payload, "VoltManager.dll"), "valid payload bytes");
                ZipFile.CreateFromDirectory(payload, zip);
                Assert.True(CreateHardLink(Path.Combine(root, "VoltManager.dll"), source, IntPtr.Zero));
                InstallManifest.Write(root, new[] { "VoltManager.dll" });

                Assert.NotEmpty(InstallManifest.DeleteOwnedEntries(root, zip));
                Assert.Equal("valid payload bytes", File.ReadAllText(source));
                Assert.True(File.Exists(Path.Combine(root, "VoltManager.dll")));
                Assert.True(File.Exists(Path.Combine(root, InstallManifest.FileName)));
            }
            finally
            {
                Cleanup(root);
                Cleanup(payload);
                Cleanup(reference);
            }
        }

        [Fact]
        public void Altered_file_with_trusted_name_is_preserved_on_uninstall()
        {
            string root = CreateTempDirectory();
            string payload = CreateTempDirectory();
            string reference = CreateTempDirectory();
            string zip = Path.Combine(reference, "payload.zip");
            try
            {
                File.WriteAllText(Path.Combine(payload, "VoltManager.dll"), "authentic published dll");
                ZipFile.CreateFromDirectory(payload, zip);
                File.WriteAllText(Path.Combine(root, "VoltManager.dll"), "unrecognized local contents");
                InstallManifest.Write(root, new[] { "VoltManager.dll" });

                Assert.NotEmpty(InstallManifest.DeleteOwnedEntries(root, zip));
                Assert.Equal("unrecognized local contents", File.ReadAllText(Path.Combine(root, "VoltManager.dll")));
                Assert.True(File.Exists(Path.Combine(root, InstallManifest.FileName)));
            }
            finally
            {
                Cleanup(root);
                Cleanup(payload);
                Cleanup(reference);
            }
        }

        [Fact]
        public void Verified_staging_inventory_rejects_modified_and_unlisted_nested_assets()
        {
            string stage = CreateTempDirectory();
            string reference = CreateTempDirectory();
            string zip = Path.Combine(reference, "payload.zip");
            try
            {
                Directory.CreateDirectory(Path.Combine(stage, "wwwroot", "js"));
                string dll = Path.Combine(stage, "VoltManager.dll");
                string script = Path.Combine(stage, "wwwroot", "js", "main.js");
                File.WriteAllText(dll, "trusted DLL");
                File.WriteAllText(script, "trusted JS");
                ZipFile.CreateFromDirectory(stage, zip);

                InstallManifest.ValidateStagedPayload(stage, zip);
                File.WriteAllText(dll, "untrusted DLL");
                Assert.Throws<InvalidDataException>(() => InstallManifest.ValidateStagedPayload(stage, zip));

                File.WriteAllText(dll, "trusted DLL");
                string unlisted = Path.Combine(stage, "wwwroot", "js", "private.txt");
                File.WriteAllText(unlisted, "foreign nested file");
                Assert.Throws<InvalidDataException>(() => InstallManifest.ValidateStagedPayload(stage, zip));
                Assert.Equal("foreign nested file", File.ReadAllText(unlisted));
            }
            finally
            {
                Cleanup(stage);
                Cleanup(reference);
            }
        }

        [Fact]
        public void Installed_uninstaller_must_match_running_setup_even_with_matching_preview_payload()
        {
            string root = CreateTempDirectory();
            string payload = CreateTempDirectory();
            string reference = CreateTempDirectory();
            string zip = Path.Combine(reference, "preview-payload.zip");
            try
            {
                string installedDll = Path.Combine(root, "VoltManager.dll");
                File.WriteAllText(installedDll, "preview-payload-v2");
                File.WriteAllText(Path.Combine(payload, "VoltManager.dll"), "preview-payload-v2");
                ZipFile.CreateFromDirectory(payload, zip);
                InstallManifest.Write(root, new[] { "VoltManager.dll" });

                string copiedUninstaller = Path.Combine(root, "uninstall.exe");
                File.Copy(typeof(InstallManifest).Assembly.Location, copiedUninstaller);
                Assert.Contains("uninstall.exe", InstallManifest.GetOwnedNamesForUninstall(root, zip));

                File.WriteAllText(copiedUninstaller, "wrong version of the uninstaller");
                Assert.NotEmpty(InstallManifest.DeleteOwnedEntries(root, zip));
                Assert.Equal("preview-payload-v2", File.ReadAllText(installedDll));
                Assert.True(File.Exists(Path.Combine(root, InstallManifest.FileName)));

                File.Copy(typeof(InstallManifest).Assembly.Location, copiedUninstaller, overwrite: true);
                Assert.Empty(InstallManifest.DeleteOwnedEntries(root, zip));
                Assert.False(Directory.Exists(root));
            }
            finally
            {
                Cleanup(root);
                Cleanup(payload);
                Cleanup(reference);
            }
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLink(string newFile, string existingFile, IntPtr securityAttributes);

        [Fact]
        public void Untrusted_manifest_cannot_authorize_removal_of_foreign_files_or_directories()
        {
            string root = CreateTempDirectory();
            string foreignFile = Path.Combine(root, "personal.txt");
            string foreignDirectory = Path.Combine(root, "Documents");
            try
            {
                Directory.CreateDirectory(foreignDirectory);
                File.WriteAllText(foreignFile, "keep personal file");
                File.WriteAllText(Path.Combine(foreignDirectory, "family.txt"), "keep nested file");
                File.WriteAllText(Path.Combine(root, "VoltManager.exe"), "app-looking payload");
                File.WriteAllText(Path.Combine(root, InstallManifest.FileName),
                    "{\"entries\":[\"personal.txt\",\"Documents\"]}");

                var failures = InstallManifest.DeleteOwnedEntries(root);

                Assert.NotEmpty(failures);
                Assert.Equal("keep personal file", File.ReadAllText(foreignFile));
                Assert.Equal("keep nested file", File.ReadAllText(Path.Combine(foreignDirectory, "family.txt")));
                Assert.True(File.Exists(Path.Combine(root, InstallManifest.FileName)));
            }
            finally { Cleanup(root); }
        }

        [Fact]
        public void Corrupt_manifest_does_not_turn_uninstall_into_silent_partial_success()
        {
            string root = CreateTempDirectory();
            string exe = Path.Combine(root, "VoltManager.exe");
            try
            {
                File.WriteAllText(exe, "application");
                File.WriteAllText(Path.Combine(root, InstallManifest.FileName), "{\"entries\": [");

                var failures = InstallManifest.DeleteOwnedEntries(root);

                Assert.NotEmpty(failures);
                Assert.Equal("application", File.ReadAllText(exe));
                Assert.True(File.Exists(Path.Combine(root, InstallManifest.FileName)));
            }
            finally { Cleanup(root); }
        }

        [Fact]
        public void Manifest_with_invalid_relative_path_must_fail_closed()
        {
            string root = CreateTempDirectory();
            string exe = Path.Combine(root, "VoltManager.exe");
            try
            {
                File.WriteAllText(exe, "application");
                File.WriteAllText(Path.Combine(root, InstallManifest.FileName),
                    "{\"entries\":[\"../outside\",\"VoltManager.exe\"]}");

                var failures = InstallManifest.DeleteOwnedEntries(root);

                Assert.NotEmpty(failures);
                Assert.Equal("application", File.ReadAllText(exe));
                Assert.True(File.Exists(Path.Combine(root, InstallManifest.FileName)));
            }
            finally { Cleanup(root); }
        }

        [Theory]
        [InlineData("COM\u00B9")]
        [InlineData("COM\u00B2.txt")]
        [InlineData("LPT\u00B3")]
        [InlineData("CONIN$")]
        [InlineData("CONOUT$.txt")]
        public void Manifest_rejects_win32_reserved_device_aliases_not_caught_by_path_characters(string unsafeName)
        {
            string root = CreateTempDirectory();
            string payload = CreateTempDirectory();
            string reference = CreateTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(root, "VoltManager.exe"), "legitimate app payload");
                File.WriteAllText(Path.Combine(payload, "VoltManager.exe"), "legitimate app payload");
                string zip = Path.Combine(reference, "trusted.zip");
                ZipFile.CreateFromDirectory(payload, zip);
                string marker = Path.Combine(root, InstallManifest.FileName);
                File.WriteAllText(marker, "{\"entries\":[\"VoltManager.exe\",\"" + unsafeName + "\"]}");

                Assert.Throws<InvalidDataException>(() => InstallManifest.ReadOwnedEntries(root));
                Assert.NotEmpty(InstallManifest.DeleteOwnedEntries(root, zip));
                Assert.Equal("legitimate app payload", File.ReadAllText(Path.Combine(root, "VoltManager.exe")));
                Assert.True(File.Exists(marker));
            }
            finally
            {
                Cleanup(root);
                Cleanup(payload);
                Cleanup(reference);
            }
        }

        [Theory]
        [InlineData("../outside")]
        [InlineData("..\\outside")]
        [InlineData("/Windows/system.ini")]
        [InlineData("C:\\Windows\\system.ini")]
        [InlineData("VoltManager.exe:foreign-stream")]
        [InlineData("VoltManager*.dll")]
        [InlineData("LPT9.log")]
        [InlineData("AUX.txt")]
        [InlineData("trailing. ")]
        public void Trusted_zip_cannot_authorize_win32_unsafe_manifest_entry(string unsafeName)
        {
            string root = CreateTempDirectory();
            string payload = CreateTempDirectory();
            string reference = CreateTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(root, "VoltManager.exe"), "trusted bytes");
                File.WriteAllText(Path.Combine(payload, "VoltManager.exe"), "trusted bytes");
                string zip = Path.Combine(reference, "trusted.zip");
                ZipFile.CreateFromDirectory(payload, zip);
                string marker = Path.Combine(root, InstallManifest.FileName);
                File.WriteAllText(marker, "{\"entries\":[\"VoltManager.exe\"," +
                    "\"" + unsafeName.Replace("\\", "\\\\") + "\"]}");

                Assert.Throws<InvalidDataException>(() => InstallManifest.ReadOwnedEntries(root));
                Assert.NotEmpty(InstallManifest.DeleteOwnedEntries(root, zip));
                Assert.Throws<InvalidDataException>(() => InstallManifest.GetOwnedNamesForReplacement(
                    root, new[] { "VoltManager.exe" }, zip));
                Assert.Equal("trusted bytes", File.ReadAllText(Path.Combine(root, "VoltManager.exe")));
                Assert.True(File.Exists(marker));
            }
            finally
            {
                Cleanup(root);
                Cleanup(payload);
                Cleanup(reference);
            }
        }

        [Theory]
        [InlineData("{\"entries\":\"VoltManager.exe\"}")]
        [InlineData("{\"entries\":[\"VoltManager.exe\",123]}")]
        [InlineData("{\"entries\":[\"VoltManager.exe\",null]}")]
        [InlineData("{\"entries\":{\"VoltManager.exe\":true}}")]
        public void Valid_json_with_invalid_manifest_schema_fails_closed_with_trusted_zip(string invalidManifest)
        {
            string root = CreateTempDirectory();
            string payload = CreateTempDirectory();
            string reference = CreateTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(root, "VoltManager.exe"), "trusted bytes");
                File.WriteAllText(Path.Combine(payload, "VoltManager.exe"), "trusted bytes");
                string zip = Path.Combine(reference, "trusted.zip");
                ZipFile.CreateFromDirectory(payload, zip);
                string marker = Path.Combine(root, InstallManifest.FileName);
                File.WriteAllText(marker, invalidManifest);

                Assert.NotEmpty(InstallManifest.DeleteOwnedEntries(root, zip));
                Assert.Throws<InvalidDataException>(() => InstallManifest.GetOwnedNamesForReplacement(
                    root, new[] { "VoltManager.exe" }, zip));
                Assert.Equal("trusted bytes", File.ReadAllText(Path.Combine(root, "VoltManager.exe")));
                Assert.True(File.Exists(marker));
            }
            finally
            {
                Cleanup(root);
                Cleanup(payload);
                Cleanup(reference);
            }
        }

        [Fact]
        public void Unknown_legacy_manifest_directory_is_preserved_even_with_explicit_trusted_zip()
        {
            string root = CreateTempDirectory();
            string payload = CreateTempDirectory();
            string reference = CreateTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(root, "VoltManager.exe"), "trusted bytes");
                File.WriteAllText(Path.Combine(payload, "VoltManager.exe"), "trusted bytes");
                string zip = Path.Combine(reference, "trusted.zip");
                ZipFile.CreateFromDirectory(payload, zip);
                string foreign = Path.Combine(root, "Documents");
                Directory.CreateDirectory(foreign);
                string privateFile = Path.Combine(foreign, "private.txt");
                File.WriteAllText(privateFile, "foreign data");
                File.WriteAllText(Path.Combine(root, InstallManifest.FileName),
                    "{\"entries\":[\"VoltManager.exe\",\"Documents\"]}");

                Assert.NotEmpty(InstallManifest.DeleteOwnedEntries(root, zip));
                Assert.Throws<InvalidDataException>(() => InstallManifest.GetOwnedNamesForReplacement(
                    root, new[] { "VoltManager.exe" }, zip));
                Assert.Equal("foreign data", File.ReadAllText(privateFile));
                Assert.True(File.Exists(Path.Combine(root, "VoltManager.exe")));
                Assert.True(File.Exists(Path.Combine(root, InstallManifest.FileName)));
            }
            finally
            {
                Cleanup(root);
                Cleanup(payload);
                Cleanup(reference);
            }
        }

        [Fact]
        public void Nested_reparse_point_is_preserved_with_explicit_trusted_zip()
        {
            string root = CreateTempDirectory();
            string payload = CreateTempDirectory();
            string reference = CreateTempDirectory();
            string web = Path.Combine(root, "wwwroot");
            string foreign = Path.Combine(reference, "foreign-web-content");
            try
            {
                Directory.CreateDirectory(foreign);
                File.WriteAllText(Path.Combine(foreign, "index.html"), "private content");
                Directory.CreateDirectory(Path.Combine(payload, "wwwroot"));
                File.WriteAllText(Path.Combine(payload, "wwwroot", "index.html"), "private content");
                string zip = Path.Combine(reference, "trusted.zip");
                ZipFile.CreateFromDirectory(payload, zip);
                using (Process? process = Process.Start(new ProcessStartInfo(
                    "cmd.exe", "/c mklink /J \"" + web + "\" \"" + foreign + "\"")
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
                Assert.True((File.GetAttributes(web) & FileAttributes.ReparsePoint) != 0);
                InstallManifest.Write(root, new[] { "wwwroot" });

                Assert.NotEmpty(InstallManifest.DeleteOwnedEntries(root, zip));
                Assert.Equal("private content", File.ReadAllText(Path.Combine(foreign, "index.html")));
                Assert.True(File.Exists(Path.Combine(root, InstallManifest.FileName)));
            }
            finally
            {
                try { if (Directory.Exists(web)) Directory.Delete(web, recursive: false); } catch { }
                Cleanup(root);
                Cleanup(payload);
                Cleanup(reference);
            }
        }

        [Fact]
        public void App_named_directory_is_not_recursively_deleted_without_provenance()
        {
            string root = CreateTempDirectory();
            string foreignDirectory = Path.Combine(root, "VoltManager.exe");
            try
            {
                Directory.CreateDirectory(foreignDirectory);
                File.WriteAllText(Path.Combine(foreignDirectory, "private.txt"), "keep");

                var failures = InstallManifest.DeleteOwnedEntries(root);

                Assert.NotEmpty(failures);
                Assert.Equal("keep", File.ReadAllText(Path.Combine(foreignDirectory, "private.txt")));
            }
            finally { Cleanup(root); }
        }

        [Fact]
        public void Junction_install_root_is_not_used_for_uninstall_cleanup()
        {
            string root = CreateTempDirectory();
            string actual = Path.Combine(root, "actual-install");
            string junction = Path.Combine(root, "junction-install");
            try
            {
                Directory.CreateDirectory(actual);
                File.WriteAllText(Path.Combine(actual, "VoltManager.exe"), "synthetic app");
                using (Process? process = Process.Start(new ProcessStartInfo(
                    "cmd.exe", "/c mklink /J \"" + junction + "\" \"" + actual + "\"")
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
                Assert.True((File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0);

                var failures = InstallManifest.DeleteOwnedEntries(junction);

                Assert.NotEmpty(failures);
                Assert.Equal("synthetic app", File.ReadAllText(Path.Combine(actual, "VoltManager.exe")));
            }
            finally
            {
                try { if (Directory.Exists(junction)) Directory.Delete(junction, false); } catch { }
                Cleanup(root);
            }
        }

        [Fact]
        public void Untrusted_manifest_cannot_authorize_update_replacement_of_foreign_file()
        {
            string root = CreateTempDirectory();
            string foreignFile = Path.Combine(root, "personal.txt");
            try
            {
                File.WriteAllText(foreignFile, "keep");
                File.WriteAllText(Path.Combine(root, InstallManifest.FileName),
                    "{\"entries\":[\"personal.txt\"]}");

                Assert.ThrowsAny<InvalidDataException>(() =>
                    InstallManifest.GetOwnedNamesForReplacement(root, new[] { "VoltManager.exe" }));
                Assert.Equal("keep", File.ReadAllText(foreignFile));
            }
            finally { Cleanup(root); }
        }

        [Fact]
        public void Program_files_decision_uses_directory_boundaries()
        {
            Assert.True(InstallDirectorySecurity.IsUnderProgramFiles(
                @"C:\Program Files\VoltManager",
                @"C:\Program Files",
                @"C:\Program Files (x86)"));
            Assert.True(InstallDirectorySecurity.IsUnderProgramFiles(
                @"C:\Program Files (x86)\VoltManager",
                @"C:\Program Files",
                @"C:\Program Files (x86)"));
            Assert.False(InstallDirectorySecurity.IsUnderProgramFiles(
                @"C:\Program Files Extra\VoltManager",
                @"C:\Program Files",
                @"C:\Program Files (x86)"));
            Assert.False(InstallDirectorySecurity.IsUnderProgramFiles(
                @"D:\Apps\VoltManager",
                @"C:\Program Files",
                @"C:\Program Files (x86)"));
        }

        [Fact]
        public void Leftover_backup_and_empty_unverified_backup_are_both_preserved()
        {
            string root = CreateTempDirectory();
            string foreignBackup = Path.Combine(root, ".VoltManager.backup-old");
            string emptyBackup = Path.Combine(root, ".VoltManager.backup-empty");
            Directory.CreateDirectory(foreignBackup);
            Directory.CreateDirectory(emptyBackup);
            File.WriteAllText(Path.Combine(foreignBackup, "thesis.docx"), "user data");
            try
            {
                InstallEngine.DeleteLeftoverSwapDirectories(root, "VoltManager");

                Assert.True(File.Exists(Path.Combine(foreignBackup, "thesis.docx")));
                // A predictable swap-directory name does not prove ownership,
                // even when the directory is currently empty.
                Assert.True(Directory.Exists(emptyBackup));
            }
            finally
            {
                Cleanup(root);
            }
        }

        [Fact]
        public void Leftover_staging_with_foreign_files_is_kept()
        {
            string root = CreateTempDirectory();
            string foreignStaging = Path.Combine(root, ".VoltManager.staging-foreign");
            Directory.CreateDirectory(foreignStaging);
            File.WriteAllText(Path.Combine(foreignStaging, "family-photos.txt"), "private data");
            try
            {
                InstallEngine.DeleteLeftoverSwapDirectories(root, "VoltManager");

                Assert.Equal("private data", File.ReadAllText(Path.Combine(foreignStaging, "family-photos.txt")));
            }
            finally
            {
                Cleanup(root);
            }
        }

        [Fact]
        public void Leftover_backup_with_app_named_and_foreign_files_is_kept()
        {
            string root = CreateTempDirectory();
            string mixedBackup = Path.Combine(root, ".VoltManager.backup-mixed");
            Directory.CreateDirectory(mixedBackup);
            File.WriteAllText(Path.Combine(mixedBackup, "VoltManager.exe"), "app-looking file");
            File.WriteAllText(Path.Combine(mixedBackup, "diary.txt"), "personal data");
            try
            {
                InstallEngine.DeleteLeftoverSwapDirectories(root, "VoltManager");

                Assert.Equal("app-looking file", File.ReadAllText(Path.Combine(mixedBackup, "VoltManager.exe")));
                Assert.Equal("personal data", File.ReadAllText(Path.Combine(mixedBackup, "diary.txt")));
            }
            finally
            {
                Cleanup(root);
            }
        }

        private static string CreateTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void Cleanup(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }
    }
}
