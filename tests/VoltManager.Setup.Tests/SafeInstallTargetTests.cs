using System;
using System.IO;
using System.Linq;
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
        public void Marker_allows_replacement_while_unknown_user_file_survives()
        {
            string root = CreateTempDirectory();
            string staging = CreateTempDirectory();
            string backup = CreateTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(root, "app.txt"), "old");
                File.WriteAllText(Path.Combine(root, "personal.txt"), "keep");
                InstallManifest.Write(root, new[] { "app.txt" });
                File.WriteAllText(Path.Combine(staging, "app.txt"), "new");
                File.WriteAllText(Path.Combine(staging, "new-app.txt"), "new payload");

                Assert.True(InstallTargetValidator.ValidateInstallTarget(root).Ok);

                InstallEngine.ReplaceInstallDirectoryContents(root, staging, backup);

                Assert.Equal("new", File.ReadAllText(Path.Combine(root, "app.txt")));
                Assert.Equal("new payload", File.ReadAllText(Path.Combine(root, "new-app.txt")));
                Assert.Equal("keep", File.ReadAllText(Path.Combine(root, "personal.txt")));
                string[] manifest = InstallManifest.ReadOwnedEntries(root).OrderBy(value => value).ToArray();
                Assert.Equal(new[] { "app.txt", "new-app.txt" }, manifest);
            }
            finally
            {
                Cleanup(root);
                Cleanup(staging);
                Cleanup(backup);
            }
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
        public void Uninstall_cleanup_removes_only_owned_entries_and_keeps_user_files()
        {
            string root = CreateTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(root, "app.dat"), "owned");
                File.WriteAllText(Path.Combine(root, "VoltManager.exe"), "owned known file");
                File.WriteAllText(Path.Combine(root, "personal.txt"), "keep");
                InstallManifest.Write(root, new[] { "app.dat" });

                Assert.Empty(InstallManifest.DeleteOwnedEntries(root));

                Assert.False(File.Exists(Path.Combine(root, "app.dat")));
                Assert.False(File.Exists(Path.Combine(root, "VoltManager.exe")));
                Assert.False(File.Exists(Path.Combine(root, InstallManifest.FileName)));
                Assert.Equal("keep", File.ReadAllText(Path.Combine(root, "personal.txt")));
                Assert.True(Directory.Exists(root));
            }
            finally
            {
                Cleanup(root);
            }
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
        public void Leftover_backup_with_only_unknown_files_is_kept()
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
                Assert.False(Directory.Exists(emptyBackup));
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
