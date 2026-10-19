using System.IO.Compression;
using System.Reflection;
using VoltManager.Setup.Engine;

namespace VoltManager.Setup.Tests;

public sealed class InstallEngineRegressionTests
{
    [Fact]
    public void Malformed_settings_backup_never_overwrites_preexisting_recovery_data()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string settings = Path.Combine(root, "settings.json");
        string previousBackup = settings + ".setup-corrupt";
        byte[] corruptContent = { 0x00, 0xFE, 0x44, 0x32, 0xF0 };
        byte[] userBackup = { 0xAB, 0x12, 0x57, 0xE1 };
        File.WriteAllBytes(settings, corruptContent);
        File.WriteAllBytes(previousBackup, userBackup);
        try
        {
            MethodInfo? backup = typeof(InstallEngine).GetMethod(
                "BackupMalformedSettingsSafely", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(backup);
            string created = Assert.IsType<string>(backup.Invoke(null, new object?[] { settings }));
            Assert.NotEqual(previousBackup, created);
            Assert.Equal(userBackup, File.ReadAllBytes(previousBackup));
            Assert.Equal(corruptContent, File.ReadAllBytes(created));
            Assert.Equal(corruptContent, File.ReadAllBytes(settings));
            Assert.Equal(3, Directory.GetFiles(root).Length);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Cleanup_never_recursively_removes_unverified_random_temp_directory_contents()
    {
        string root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        string nested = Path.Combine(root, "private", "nested");
        Directory.CreateDirectory(nested);
        string sentinel = Path.Combine(nested, "foreign.txt");
        File.WriteAllText(sentinel, "preserve third-party bytes");
        try
        {
            MethodInfo? cleanup = typeof(InstallEngine).GetMethod(
                "TryDeleteTempDirectory", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(cleanup);
            cleanup.Invoke(null, new object?[] { root });
            Assert.Equal("preserve third-party bytes", File.ReadAllText(sentinel));
            Assert.True(Directory.Exists(nested));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Preview_payload_helper_can_be_created_from_versioned_setup_path()
    {
        Assembly setupAssembly = typeof(InstallEngine).Assembly;
        string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
        string versionedSetup = Path.Combine(root, $"VoltManagerSetup-{setupAssembly.GetName().Version}.exe");
        AppDomain? domain = null;
        Directory.CreateDirectory(root);
        File.Copy(setupAssembly.Location, versionedSetup);

        try
        {
            domain = AppDomain.CreateDomain(
                "VoltManagerSetupVersionedPath-" + Guid.NewGuid().ToString("N"),
                null,
                new AppDomainSetup { ApplicationBase = root });

            var helper = (InstallEngine.PreviewPayloadExtractor)domain.CreateInstanceFromAndUnwrap(
                versionedSetup,
                typeof(InstallEngine.PreviewPayloadExtractor).FullName);

            Assert.NotNull(helper);
        }
        finally
        {
            if (domain != null)
                AppDomain.Unload(domain);
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Preview_payload_extraction_does_not_load_downloaded_setup_into_current_appdomain()
    {
        Assembly setupAssembly = typeof(InstallEngine).Assembly;
        string assemblyName = setupAssembly.GetName().Name!;
        int loadedBefore = AppDomain.CurrentDomain.GetAssemblies()
            .Count(a => string.Equals(a.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));
        string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
        string previewSetup = Path.Combine(root, "VoltManagerSetup-preview.exe");
        string? extractedZip = null;
        Directory.CreateDirectory(root);
        File.Copy(setupAssembly.Location, previewSetup);

        try
        {
            MethodInfo method = typeof(InstallEngine).GetMethod(
                "ExtractPreviewPayloadZip", BindingFlags.NonPublic | BindingFlags.Static)!;
            bool hasPayload = setupAssembly.GetManifestResourceNames()
                .Any(n => n.EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase));

            Exception? error = Record.Exception(() =>
                extractedZip = (string)method.Invoke(null, new object?[] { previewSetup, CancellationToken.None })!);

            if (hasPayload)
            {
                Assert.Null(error);
                Assert.NotNull(extractedZip);
                Assert.True(File.Exists(extractedZip));
            }
            else
            {
                var invocationError = Assert.IsType<TargetInvocationException>(error);
                Assert.IsType<InvalidOperationException>(invocationError.InnerException);
            }

            int loadedAfter = AppDomain.CurrentDomain.GetAssemblies()
                .Count(a => string.Equals(a.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(loadedBefore, loadedAfter);
        }
        finally
        {
            if (extractedZip != null)
            {
                try { Directory.Delete(Path.GetDirectoryName(extractedZip)!, true); } catch { }
            }
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Preview_uninstaller_uses_verified_download_source_and_cleanup_preserves_foreign_files()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
        string runDir = Path.Combine(root, "preview-stage");
        string dest = Path.Combine(root, "VoltManager");
        Directory.CreateDirectory(runDir);
        Directory.CreateDirectory(dest);
        string downloadedExe = Path.Combine(runDir, "downloaded-preview.exe");
        string extractedZip = Path.Combine(runDir, "VoltManagerPreviewPayload.zip");
        string foreign = Path.Combine(runDir, "user-document.txt");
        File.WriteAllText(downloadedExe, "verified preview setup bytes");
        File.WriteAllText(extractedZip, "matched preview ZIP bytes");
        File.WriteAllText(foreign, "private data");
        var held = new FileStream(downloadedExe, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = new PreviewReleaseDownload(downloadedExe, "2.0.0-beta", runDir, held);

        try
        {
            Assert.Equal(downloadedExe, InstallEngine.ResolveUninstallerSource(true, release));
            Assert.Equal(typeof(InstallEngine).Assembly.Location,
                InstallEngine.ResolveUninstallerSource(false, null));
            Assert.Throws<InvalidOperationException>(() => InstallEngine.ResolveUninstallerSource(true, null));

            MethodInfo copy = typeof(InstallEngine).GetMethod(
                "CopyUninstallerFromSource", BindingFlags.NonPublic | BindingFlags.Static)!;
            var warnings = new List<string>();
            copy.Invoke(null, new object[] { dest, warnings, InstallEngine.ResolveUninstallerSource(true, release) });
            Assert.Equal("verified preview setup bytes", File.ReadAllText(Path.Combine(dest, "uninstall.exe")));
            Assert.Empty(warnings);

            MethodInfo cleanup = typeof(InstallEngine).GetMethod(
                "CleanupPreviewRelease", BindingFlags.NonPublic | BindingFlags.Static)!;
            cleanup.Invoke(null, new object?[] { release, extractedZip });
            Assert.False(File.Exists(downloadedExe));
            Assert.False(File.Exists(extractedZip));
            Assert.Equal("private data", File.ReadAllText(foreign));
            Assert.True(Directory.Exists(runDir));
        }
        finally
        {
            release.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Preview_zip_extraction_observes_pre_cancelled_token()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        MethodInfo extract = typeof(InstallEngine).GetMethod(
            "ExtractPreviewPayloadZipIntoDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
        var failure = Assert.Throws<TargetInvocationException>(() => extract.Invoke(null,
            new object[] { typeof(InstallEngine).Assembly.Location, Path.GetTempPath(), cancellation.Token }));
        Assert.IsAssignableFrom<OperationCanceledException>(failure.InnerException);
    }

    [Fact]
    public async Task Corrupt_payload_does_not_destroy_existing_installation()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
        string dest = Path.Combine(root, "VoltManager");
        string zip = Path.Combine(root, "payload.zip");
        Directory.CreateDirectory(dest);
        string existing = Path.Combine(dest, "existing.txt");
        File.WriteAllText(existing, "keep");
        File.WriteAllBytes(zip, [1, 2, 3, 4]);

        try
        {
            MethodInfo method = typeof(InstallEngine).GetMethod(
                "ExtractPayloadAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            Task task = (Task)method.Invoke(null, new object?[] { dest, CancellationToken.None, zip, null })!;

            await Assert.ThrowsAnyAsync<Exception>(async () => await task);
            Assert.True(File.Exists(existing));
            Assert.Equal("keep", File.ReadAllText(existing));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task Missing_embedded_payload_fails_closed_instead_of_reporting_install_success()
    {
        Assembly setupAssembly = typeof(InstallEngine).Assembly;
        // Packaged builds with an embedded ZIP take the ordinary install path;
        // this regression exercises the non-packaged setup used by the test build.
        if (setupAssembly.GetManifestResourceNames().Any(name =>
            name.EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase)))
            return;

        string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
        string dest = Path.Combine(root, "VoltManager");
        try
        {
            MethodInfo extract = typeof(InstallEngine).GetMethod(
                "ExtractPayloadAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            Task<string?> operation = (Task<string?>)extract.Invoke(null,
                new object?[] { dest, CancellationToken.None, null, null })!;

            var failure = await Assert.ThrowsAsync<InvalidDataException>(() => operation);
            Assert.Contains("payload", failure.Message.ToLowerInvariant());
            Assert.False(File.Exists(Path.Combine(dest, "VoltManager.exe")));
            Assert.False(File.Exists(Path.Combine(dest, InstallManifest.FileName)));
            if (Directory.Exists(dest)) Assert.Empty(Directory.GetFileSystemEntries(dest));
        }
        finally
        {
            if (Directory.Exists(root) && root.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
                try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Failed_extract_reports_retained_staging_path_and_preserves_foreign_destination()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
        string dest = Path.Combine(root, "VoltManager");
        string payload = Path.Combine(root, "source");
        Directory.CreateDirectory(dest);
        Directory.CreateDirectory(payload);
        string foreign = Path.Combine(dest, "personal-notes.txt");
        File.WriteAllText(foreign, "private untouched data");
        File.WriteAllText(Path.Combine(payload, "VoltManager.exe"), "new executable");
        string zip = CreateSyntheticPayloadZip(root, payload);

        try
        {
            MethodInfo method = typeof(InstallEngine).GetMethod(
                "ExtractPayloadAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            Task work = (Task)method.Invoke(null, new object?[] { dest, CancellationToken.None, zip, null })!;
            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await work);

            string staging = Assert.Single(Directory.GetDirectories(root, ".VoltManager.staging-*"));
            Assert.Contains(staging, failure.Message);
            Assert.Equal("new executable", File.ReadAllText(Path.Combine(staging, "VoltManager.exe")));
            Assert.Equal("private untouched data", File.ReadAllText(foreign));
            Assert.False(File.Exists(Path.Combine(dest, "VoltManager.exe")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Replace_swaps_contents_and_reports_retained_previous_files()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
        string dest = Path.Combine(root, "VoltManager");
        string staging = Path.Combine(root, "staging");
        string backup = Path.Combine(root, "backup");
        Directory.CreateDirectory(dest);
        Directory.CreateDirectory(Path.Combine(staging, "wwwroot"));
        // The current trusted archive is not evidence of different old-version
        // bytes. Keep the old managed content identical so ownership preflight
        // succeeds; this test targets the backup/rename mechanics only.
        File.WriteAllText(Path.Combine(dest, "VoltManager.exe"), "verified");
        InstallManifest.Write(dest, new[] { "VoltManager.exe" });
        File.WriteAllText(Path.Combine(staging, "VoltManager.exe"), "verified");
        File.WriteAllText(Path.Combine(staging, "VoltManager.Supervisor.exe"), "new payload");
        File.WriteAllText(Path.Combine(staging, "wwwroot", "index.html"), "<html>");
        string zip = CreateSyntheticPayloadZip(root, staging);

        try
        {
            string? backupWarning = InstallEngine.ReplaceInstallDirectoryContents(dest, staging, backup, zip);

            Assert.Equal("verified", File.ReadAllText(Path.Combine(dest, "VoltManager.exe")));
            Assert.Equal("new payload", File.ReadAllText(Path.Combine(dest, "VoltManager.Supervisor.exe")));
            Assert.True(File.Exists(Path.Combine(dest, "wwwroot", "index.html")));
            Assert.Null(backupWarning);
            Assert.False(Directory.Exists(backup));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Replace_rejects_nonempty_preexisting_backup_without_modifying_either_installation()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
        string dest = Path.Combine(root, "VoltManager");
        string staging = Path.Combine(root, "staging");
        string backup = Path.Combine(root, "backup");
        Directory.CreateDirectory(dest);
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(dest, "app.txt"), "old app");
        File.WriteAllText(Path.Combine(staging, "app.txt"), "new app");
        File.WriteAllText(Path.Combine(backup, "private.txt"), "keep private");

        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                InstallEngine.ReplaceInstallDirectoryContents(dest, staging, backup));

            Assert.Equal("old app", File.ReadAllText(Path.Combine(dest, "app.txt")));
            Assert.Equal("new app", File.ReadAllText(Path.Combine(staging, "app.txt")));
            Assert.Equal("keep private", File.ReadAllText(Path.Combine(backup, "private.txt")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Replace_preserves_unattributed_files_nested_under_manifest_owned_directory()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
        string dest = Path.Combine(root, "VoltManager");
        string staging = Path.Combine(root, "staging");
        string backup = Path.Combine(root, "backup");
        Directory.CreateDirectory(Path.Combine(dest, "wwwroot"));
        Directory.CreateDirectory(Path.Combine(staging, "wwwroot"));
        File.WriteAllText(Path.Combine(dest, "wwwroot", "index.html"), "old app");
        File.WriteAllText(Path.Combine(dest, "wwwroot", "personal-notes.txt"), "user data");
        File.WriteAllText(Path.Combine(staging, "wwwroot", "index.html"), "new app");
        InstallManifest.Write(dest, new[] { "wwwroot" });
        string zip = CreateSyntheticPayloadZip(root, staging);

        try
        {
            Exception? error = Record.Exception(() =>
                InstallEngine.ReplaceInstallDirectoryContents(dest, staging, backup, zip));

            Assert.True(error is InvalidOperationException or InvalidDataException);
            Assert.Equal("old app", File.ReadAllText(Path.Combine(dest, "wwwroot", "index.html")));
            Assert.Equal("user data", File.ReadAllText(Path.Combine(dest, "wwwroot", "personal-notes.txt")));
            Assert.Equal("new app", File.ReadAllText(Path.Combine(staging, "wwwroot", "index.html")));
            Assert.False(Directory.Exists(backup));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Install_lock_name_ignores_case_and_trailing_separator()
    {
        string a = InstallEngine.InstallDirectoryLock.MutexName(@"C:\Program Files\VoltManager");
        string b = InstallEngine.InstallDirectoryLock.MutexName(@"c:\program files\voltmanager\");
        string other = InstallEngine.InstallDirectoryLock.MutexName(@"C:\Program Files\Other");

        Assert.Equal(a, b);
        Assert.NotEqual(a, other);
        Assert.StartsWith(@"Global\VoltManagerSetup_Install_", a);
    }

    [Fact]
    public async Task Second_installer_for_same_folder_waits_for_the_lock()
    {
        string dest = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
        using var releaseOwner = new ManualResetEventSlim(false);
        var ownerReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task owner = Task.Run(() =>
        {
            // Il mutex deve essere rilasciato dallo stesso thread che lo acquisisce.
            using (InstallEngine.InstallDirectoryLock.Acquire(dest, TimeSpan.FromSeconds(1)))
            {
                ownerReady.SetResult(true);
                releaseOwner.Wait();
            }
        });

        await ownerReady.Task;
        try
        {
            Exception? contended = await Record.ExceptionAsync(() => Task.Run(() =>
            {
                using (InstallEngine.InstallDirectoryLock.Acquire(dest, TimeSpan.FromMilliseconds(200))) { }
            }));
            Assert.IsType<IOException>(contended);

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Exception? cancelled = await Record.ExceptionAsync(() => Task.Run(() =>
            {
                using (InstallEngine.InstallDirectoryLock.Acquire(dest, TimeSpan.FromMinutes(1), cts.Token)) { }
            }));
            Assert.IsAssignableFrom<OperationCanceledException>(cancelled);
        }
        finally
        {
            releaseOwner.Set();
            await owner;
        }

        await Task.Run(() =>
        {
            using (InstallEngine.InstallDirectoryLock.Acquire(dest, TimeSpan.FromSeconds(1))) { }
        });
    }

    [Fact]
    public void Leftover_backup_and_staging_are_preserved_even_when_empty_and_same_named()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
        string backup = Path.Combine(root, ".VoltManager.backup-abc");
        string staging = Path.Combine(root, ".VoltManager.staging-def");
        string emptyBackup = Path.Combine(root, ".VoltManager.backup-empty");
        string emptyStaging = Path.Combine(root, ".VoltManager.staging-empty");
        string unrelated = Path.Combine(root, ".Other.backup-xyz");
        foreach (string dir in new[] { backup, staging, unrelated })
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "file.txt"), "x");
        }
        Directory.CreateDirectory(emptyBackup);
        Directory.CreateDirectory(emptyStaging);
        File.WriteAllText(Path.Combine(backup, "VoltManager.exe"), "x");

        try
        {
            InstallEngine.DeleteLeftoverSwapDirectories(root, "VoltManager");

            Assert.Equal("x", File.ReadAllText(Path.Combine(backup, "file.txt")));
            Assert.Equal("x", File.ReadAllText(Path.Combine(staging, "file.txt")));
            Assert.True(Directory.Exists(emptyBackup));
            Assert.True(Directory.Exists(emptyStaging));
            Assert.True(Directory.Exists(unrelated));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Failed_move_rolls_back_to_previous_installation()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
        string dest = Path.Combine(root, "VoltManager");
        string staging = Path.Combine(root, "staging");
        string backup = Path.Combine(root, "backup");
        Directory.CreateDirectory(dest);
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(dest, "old.txt"), "old");
        File.WriteAllText(Path.Combine(dest, "VoltManager.exe"), "old exe");
        InstallManifest.Write(dest, new[] { "VoltManager.exe" });
        File.WriteAllText(Path.Combine(staging, "a.txt"), "a");
        File.WriteAllText(Path.Combine(staging, "VoltManager.exe"), "old exe");
        string locked = Path.Combine(staging, "z-locked.txt");
        File.WriteAllText(locked, "z");
        string zip = CreateSyntheticPayloadZip(root, staging);

        try
        {
            // Reads (including SHA-256 staging preflight) remain allowed, while
            // FILE_SHARE_DELETE is denied so the actual staged rename must fail.
            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                InstallManifest.ValidateStagedPayload(staging, zip);
                Assert.IsType<IOException>(Record.Exception(() =>
                    InstallEngine.ReplaceInstallDirectoryContents(dest, staging, backup, zip)));
            }

            Assert.Equal("old", File.ReadAllText(Path.Combine(dest, "old.txt")));
            Assert.Equal("old exe", File.ReadAllText(Path.Combine(dest, "VoltManager.exe")));
            Assert.False(File.Exists(Path.Combine(dest, "a.txt")));
            Assert.False(File.Exists(Path.Combine(dest, "z-locked.txt")));
            Assert.False(Directory.Exists(backup));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Failed_staged_move_does_not_recursively_delete_unverified_staged_directory()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
        string dest = Path.Combine(root, "VoltManager");
        string staging = Path.Combine(root, "staging");
        string backup = Path.Combine(root, "backup");
        Directory.CreateDirectory(dest);
        Directory.CreateDirectory(Path.Combine(staging, "wwwroot"));
        File.WriteAllText(Path.Combine(dest, "VoltManager.exe"), "old");
        InstallManifest.Write(dest, new[] { "VoltManager.exe" });
        File.WriteAllText(Path.Combine(staging, "VoltManager.exe"), "old");
        File.WriteAllText(Path.Combine(staging, "wwwroot", "unverified.txt"), "keep");
        string locked = Path.Combine(staging, "z-locked.txt");
        File.WriteAllText(locked, "locked");
        string zip = CreateSyntheticPayloadZip(root, staging);

        try
        {
            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                InstallManifest.ValidateStagedPayload(staging, zip);
                Assert.IsType<IOException>(Record.Exception(() =>
                    InstallEngine.ReplaceInstallDirectoryContents(dest, staging, backup, zip)));
            }

            Assert.Equal("old", File.ReadAllText(Path.Combine(dest, "VoltManager.exe")));
            Assert.True(File.Exists(Path.Combine(dest, "wwwroot", "unverified.txt")) ||
                        File.Exists(Path.Combine(staging, "wwwroot", "unverified.txt")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Failed_staged_copy_restores_previous_executable_and_removes_new_web_assets_without_touching_foreign_files()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
        string dest = Path.Combine(root, "VoltManager");
        string staging = Path.Combine(root, "staging");
        string backup = Path.Combine(root, "backup");
        Directory.CreateDirectory(dest);
        Directory.CreateDirectory(staging);
        string previousExe = Path.Combine(dest, "VoltManager.exe");
        string foreign = Path.Combine(dest, "private-user-file.txt");
        File.WriteAllText(previousExe, "old-executable");
        File.WriteAllText(foreign, "private-content");
        InstallManifest.Write(dest, new[] { "VoltManager.exe" });
        string previousManifest = File.ReadAllText(Path.Combine(dest, InstallManifest.FileName));

        // The installer moves staged entries in name order. The locked last file
        // fails after both the replacement EXE and a nonempty wwwroot were installed.
        File.WriteAllText(Path.Combine(staging, "VoltManager.exe"), "old-executable");
        string stagedWeb = Path.Combine(staging, "wwwroot");
        Directory.CreateDirectory(stagedWeb);
        File.WriteAllText(Path.Combine(stagedWeb, "index.html"), "<html>new</html>");
        Directory.CreateDirectory(Path.Combine(stagedWeb, "js"));
        File.WriteAllText(Path.Combine(stagedWeb, "js", "new.js"), "new asset");
        string locked = Path.Combine(staging, "z-locked.dat");
        File.WriteAllText(locked, "blocked");
        string zip = CreateSyntheticPayloadZip(root, staging);

        try
        {
            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                InstallManifest.ValidateStagedPayload(staging, zip);
                Assert.IsType<IOException>(Record.Exception(() =>
                    InstallEngine.ReplaceInstallDirectoryContents(dest, staging, backup, zip)));
            }

            Assert.Equal("old-executable", File.ReadAllText(previousExe));
            Assert.Equal(previousManifest, File.ReadAllText(Path.Combine(dest, InstallManifest.FileName)));
            Assert.Equal("private-content", File.ReadAllText(foreign));
            Assert.False(Directory.Exists(Path.Combine(dest, "wwwroot")));
            Assert.False(File.Exists(Path.Combine(dest, "z-locked.dat")));
            Assert.Equal("<html>new</html>", File.ReadAllText(Path.Combine(stagedWeb, "index.html")));
            Assert.Equal("new asset", File.ReadAllText(Path.Combine(stagedWeb, "js", "new.js")));
            Assert.False(Directory.Exists(backup));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Failed_restore_reports_compound_failure_and_keeps_original_dll_in_backup()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
        string dest = Path.Combine(root, "VoltManager");
        string staging = Path.Combine(root, "staging");
        string backup = Path.Combine(root, "backup");
        Directory.CreateDirectory(dest);
        Directory.CreateDirectory(Path.Combine(staging, "wwwroot", "js"));
        File.WriteAllText(Path.Combine(dest, "VoltManager.exe"), "old exe");
        File.WriteAllText(Path.Combine(dest, "VoltManager.dll"), "old dll");
        File.WriteAllText(Path.Combine(dest, "personal.txt"), "foreign user data");
        InstallManifest.Write(dest, new[] { "VoltManager.exe", "VoltManager.dll" });
        string oldManifest = File.ReadAllText(Path.Combine(dest, InstallManifest.FileName));

        File.WriteAllText(Path.Combine(staging, "VoltManager.exe"), "old exe");
        File.WriteAllText(Path.Combine(staging, "VoltManager.dll"), "old dll");
        File.WriteAllText(Path.Combine(staging, "wwwroot", "js", "new.js"), "new web asset");
        string locked = Path.Combine(staging, "z-locked.dat");
        File.WriteAllText(locked, "late blocked file");
        string zip = CreateSyntheticPayloadZip(root, staging);
        bool rejectedRestoration = false;

        // Use real same-volume file and directory renames throughout the transaction.
        // Intercept only the return of the original DLL from the synthetic backup.
        Action<string, string> move = (source, target) =>
        {
            if (string.Equals(source, Path.Combine(backup, "VoltManager.dll"), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(target, Path.Combine(dest, "VoltManager.dll"), StringComparison.OrdinalIgnoreCase))
            {
                rejectedRestoration = true;
                throw new IOException("Synthetic restore failure for the original DLL.");
            }
            if (File.Exists(source)) File.Move(source, target);
            else Directory.Move(source, target);
        };

        try
        {
            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                // SHA-256 verification must succeed; only the late rename is blocked.
                InstallManifest.ValidateStagedPayload(staging, zip);
                MethodInfo? core = typeof(InstallEngine).GetMethod(
                    "ReplaceInstallDirectoryContentsCore", BindingFlags.NonPublic | BindingFlags.Static);
                Assert.NotNull(core);
                var reflectionError = Assert.Throws<TargetInvocationException>(() =>
                    core.Invoke(null, new object?[] { dest, staging, backup, zip, move, null }));
                var failure = Assert.IsType<InvalidOperationException>(reflectionError.InnerException);
                Assert.Contains("rollback is incomplete", failure.Message);
                Assert.Contains(backup, failure.Message);
                Assert.Contains("Could not restore previous entry VoltManager.dll", failure.Message);
                var aggregate = Assert.IsType<AggregateException>(failure.InnerException);
                Assert.Equal(2, aggregate.InnerExceptions.Count);
                Assert.IsType<IOException>(aggregate.InnerExceptions[0]);
                Assert.IsType<IOException>(aggregate.InnerExceptions[1]);
            }

            Assert.True(rejectedRestoration);
            Assert.Equal("old dll", File.ReadAllText(Path.Combine(backup, "VoltManager.dll")));
            Assert.False(File.Exists(Path.Combine(dest, "VoltManager.dll")));
            Assert.Equal("old exe", File.ReadAllText(Path.Combine(dest, "VoltManager.exe")));
            Assert.Equal(oldManifest, File.ReadAllText(Path.Combine(dest, InstallManifest.FileName)));
            Assert.Equal("foreign user data", File.ReadAllText(Path.Combine(dest, "personal.txt")));
            Assert.False(Directory.Exists(Path.Combine(dest, "wwwroot")));
            Assert.Equal("old dll", File.ReadAllText(Path.Combine(staging, "VoltManager.dll")));
            Assert.Equal("new web asset", File.ReadAllText(Path.Combine(staging, "wwwroot", "js", "new.js")));
            Assert.True(Directory.Exists(backup));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Altered_staged_asset_is_rejected_before_previous_installation_is_moved()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
        string dest = Path.Combine(root, "VoltManager");
        string staging = Path.Combine(root, "staging");
        string backup = Path.Combine(root, "backup");
        Directory.CreateDirectory(dest);
        Directory.CreateDirectory(Path.Combine(staging, "wwwroot", "js"));
        File.WriteAllText(Path.Combine(dest, "VoltManager.exe"), "original executable");
        File.WriteAllText(Path.Combine(dest, "user-document.txt"), "private content");
        InstallManifest.Write(dest, new[] { "VoltManager.exe" });
        string originalManifest = File.ReadAllText(Path.Combine(dest, InstallManifest.FileName));
        File.WriteAllText(Path.Combine(staging, "VoltManager.exe"), "new executable");
        string stagedJs = Path.Combine(staging, "wwwroot", "js", "app.js");
        File.WriteAllText(stagedJs, "verified bundled script");
        string zip = CreateSyntheticPayloadZip(root, staging);

        try
        {
            // The archive is the expected payload; staging content was modified after extraction.
            File.WriteAllText(stagedJs, "injected script");

            Assert.Throws<InvalidDataException>(() =>
                InstallEngine.ReplaceInstallDirectoryContents(dest, staging, backup, zip));

            Assert.Equal("original executable", File.ReadAllText(Path.Combine(dest, "VoltManager.exe")));
            Assert.Equal(originalManifest, File.ReadAllText(Path.Combine(dest, InstallManifest.FileName)));
            Assert.Equal("private content", File.ReadAllText(Path.Combine(dest, "user-document.txt")));
            Assert.False(Directory.Exists(Path.Combine(dest, "wwwroot")));
            Assert.False(Directory.Exists(backup));
            Assert.Equal("injected script", File.ReadAllText(stagedJs));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Removed_previous_version_asset_fails_closed_and_repair_preserves_foreign_files()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
        string dest = Path.Combine(root, "VoltManager");
        string first = Path.Combine(root, "version-one");
        string second = Path.Combine(root, "version-two");
        string repair = Path.Combine(root, "repair");
        Directory.CreateDirectory(Path.Combine(first, "wwwroot"));
        File.WriteAllText(Path.Combine(first, "VoltManager.exe"), "version one");
        File.WriteAllText(Path.Combine(first, "wwwroot", "index.html"), "original asset");
        File.WriteAllText(Path.Combine(first, "wwwroot", "removed.js"), "old bundled asset");
        string firstZip = Path.Combine(root, "version-one.zip");
        ZipFile.CreateFromDirectory(first, firstZip);

        try
        {
            InstallEngine.ReplaceInstallDirectoryContents(dest, first,
                Path.Combine(root, "backup-first"), firstZip);
            string oldManifest = File.ReadAllText(Path.Combine(dest, InstallManifest.FileName));
            string foreign = Path.Combine(dest, "personal.txt");
            File.WriteAllText(foreign, "private content");

            Directory.CreateDirectory(Path.Combine(second, "wwwroot"));
            File.WriteAllText(Path.Combine(second, "VoltManager.exe"), "version two");
            File.WriteAllText(Path.Combine(second, "wwwroot", "index.html"), "updated asset");
            string secondZip = Path.Combine(root, "version-two.zip");
            ZipFile.CreateFromDirectory(second, secondZip);

            Assert.Throws<InvalidDataException>(() => InstallEngine.ReplaceInstallDirectoryContents(
                dest, second, Path.Combine(root, "backup-version-two"), secondZip));
            Assert.Equal("version one", File.ReadAllText(Path.Combine(dest, "VoltManager.exe")));
            Assert.Equal("old bundled asset", File.ReadAllText(Path.Combine(dest, "wwwroot", "removed.js")));
            Assert.Equal(oldManifest, File.ReadAllText(Path.Combine(dest, InstallManifest.FileName)));
            Assert.Equal("private content", File.ReadAllText(foreign));
            Assert.False(Directory.Exists(Path.Combine(root, "backup-version-two")));

            Directory.CreateDirectory(Path.Combine(repair, "wwwroot"));
            File.WriteAllText(Path.Combine(repair, "VoltManager.exe"), "version one");
            File.WriteAllText(Path.Combine(repair, "wwwroot", "index.html"), "original asset");
            File.WriteAllText(Path.Combine(repair, "wwwroot", "removed.js"), "old bundled asset");
            string repairZip = Path.Combine(root, "repair.zip");
            ZipFile.CreateFromDirectory(repair, repairZip);

            string? warning = InstallEngine.ReplaceInstallDirectoryContents(
                dest, repair, Path.Combine(root, "backup-repair"), repairZip);
            Assert.Null(warning);
            Assert.False(Directory.Exists(Path.Combine(root, "backup-repair")));
            Assert.Equal("private content", File.ReadAllText(foreign));
            Assert.Equal("old bundled asset", File.ReadAllText(Path.Combine(dest, "wwwroot", "removed.js")));

            UninstallCleanupResult guardedUninstall = InstallManifest.DeleteOwnedEntriesTolerant(dest, firstZip);
            Assert.Empty(guardedUninstall.Failures);
            Assert.Equal(new[] { "personal.txt" }, guardedUninstall.Retained);
            Assert.Equal("private content", File.ReadAllText(foreign));
            Assert.False(File.Exists(Path.Combine(dest, "VoltManager.exe")));
            Assert.Equal(new[] { foreign }, Directory.GetFileSystemEntries(dest));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Post_swap_uninstaller_copy_failure_does_not_register_ARP_or_overwrite_foreign_file()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
        string dest = Path.Combine(root, "VoltManager");
        string staging = Path.Combine(root, "staging");
        string backup = Path.Combine(root, "backup");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "VoltManager.exe"), "synthetic bundled program");
        string zip = CreateSyntheticPayloadZip(root, staging);

        try
        {
            // Complete a genuine synthetic payload swap before introducing a
            // conflicting, independently unverified uninstaller candidate.
            Assert.Null(InstallEngine.ReplaceInstallDirectoryContents(dest, staging, backup, zip));
            string priorManifest = File.ReadAllText(Path.Combine(dest, InstallManifest.FileName));
            string foreign = Path.Combine(dest, "uninstall.exe");
            File.WriteAllText(foreign, "foreign uninstaller - private data");
            bool arpRegistered = false;

            MethodInfo? register = typeof(InstallEngine).GetMethod(
                "RegisterUninstallerAndArp", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(register);
            var failure = Assert.Throws<TargetInvocationException>(() =>
                register.Invoke(null, new object?[]
                {
                    dest, new List<string>(), typeof(InstallEngine).Assembly.Location,
                    (Action)(() => arpRegistered = true),
                }));

            Assert.IsType<InvalidOperationException>(failure.InnerException);
            Assert.False(arpRegistered);
            Assert.Equal("foreign uninstaller - private data", File.ReadAllText(foreign));
            Assert.Equal(priorManifest, File.ReadAllText(Path.Combine(dest, InstallManifest.FileName)));
            Assert.Equal("synthetic bundled program", File.ReadAllText(Path.Combine(dest, "VoltManager.exe")));
            Assert.False(Directory.Exists(backup));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Post_swap_arp_failure_keeps_safely_copied_uninstaller_for_manual_recovery()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
        string dest = Path.Combine(root, "VoltManager");
        string staging = Path.Combine(root, "staging");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "VoltManager.exe"), "synthetic bundled program");
        string zip = CreateSyntheticPayloadZip(root, staging);
        try
        {
            InstallEngine.ReplaceInstallDirectoryContents(dest, staging, Path.Combine(root, "backup"), zip);
            bool arpAttempted = false;
            MethodInfo? register = typeof(InstallEngine).GetMethod(
                "RegisterUninstallerAndArp", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(register);
            var failure = Assert.Throws<TargetInvocationException>(() =>
                register.Invoke(null, new object?[]
                {
                    dest, new List<string>(), typeof(InstallEngine).Assembly.Location,
                    (Action)(() =>
                    {
                        arpAttempted = true;
                        throw new IOException("synthetic ARP registration failure");
                    }),
                }));
            Assert.True(arpAttempted);
            Assert.IsType<IOException>(failure.InnerException);
            Assert.Equal("synthetic bundled program", File.ReadAllText(Path.Combine(dest, "VoltManager.exe")));
            string uninstaller = Path.Combine(dest, "uninstall.exe");
            Assert.True(File.Exists(uninstaller));
            Assert.Equal(new FileInfo(typeof(InstallEngine).Assembly.Location).Length,
                new FileInfo(uninstaller).Length);
            Assert.True(File.Exists(Path.Combine(dest, InstallManifest.FileName)));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Copy_uninstaller_refuses_to_overwrite_an_existing_foreign_file()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string uninstaller = Path.Combine(root, "uninstall.exe");
        File.WriteAllText(uninstaller, "private data must remain unchanged");

        try
        {
            MethodInfo method = typeof(InstallEngine).GetMethod(
                "CopyUninstaller", BindingFlags.Static | BindingFlags.NonPublic)!;
            var warnings = new List<string>();
            var error = Assert.Throws<TargetInvocationException>(() =>
                method.Invoke(null, new object[] { root, warnings }));

            Assert.IsType<InvalidOperationException>(error.InnerException);
            Assert.Equal("private data must remain unchanged", File.ReadAllText(uninstaller));

            File.Delete(uninstaller);
            method.Invoke(null, new object[] { root, warnings });
            Assert.True(File.Exists(uninstaller));
            Assert.Equal(new FileInfo(typeof(InstallEngine).Assembly.Location).Length,
                new FileInfo(uninstaller).Length);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Replace_retains_backup_when_unexpected_extra_entry_is_present()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
        string dest = Path.Combine(root, "VoltManager");
        string staging = Path.Combine(root, "staging");
        string backup = Path.Combine(root, "backup");
        Directory.CreateDirectory(dest);
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(dest, "VoltManager.exe"), "verified");
        InstallManifest.Write(dest, new[] { "VoltManager.exe" });
        File.WriteAllText(Path.Combine(staging, "VoltManager.exe"), "verified");
        string zip = CreateSyntheticPayloadZip(root, staging);

        try
        {
            string? backupWarning = InstallEngine.ReplaceInstallDirectoryContentsCore(
                dest, staging, backup, zip,
                (source, target) =>
                {
                    File.Move(source, target);
                    File.WriteAllText(Path.Combine(backup, "unexpected.txt"), "foreign");
                });

            Assert.NotNull(backupWarning);
            Assert.Contains(backup, backupWarning);
            Assert.True(Directory.Exists(backup));
            Assert.True(File.Exists(Path.Combine(backup, "VoltManager.exe")));
            Assert.True(File.Exists(Path.Combine(backup, "unexpected.txt")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Replace_retains_backup_when_a_verified_file_changes_after_it_was_moved()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
        string dest = Path.Combine(root, "VoltManager");
        string staging = Path.Combine(root, "staging");
        string backup = Path.Combine(root, "backup");
        Directory.CreateDirectory(dest);
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(dest, "VoltManager.exe"), "verified");
        InstallManifest.Write(dest, new[] { "VoltManager.exe" });
        File.WriteAllText(Path.Combine(staging, "VoltManager.exe"), "verified");
        string zip = CreateSyntheticPayloadZip(root, staging);
        try
        {
            string? warning = InstallEngine.ReplaceInstallDirectoryContentsCore(dest, staging, backup, zip,
                (source, target) =>
                {
                    File.Move(source, target);
                    if (string.Equals(target, Path.Combine(backup, "VoltManager.exe"), StringComparison.OrdinalIgnoreCase))
                        File.WriteAllText(target, "foreign replacement");
                });

            Assert.NotNull(warning);
            Assert.Equal("foreign replacement", File.ReadAllText(Path.Combine(backup, "VoltManager.exe")));
            Assert.Equal("verified", File.ReadAllText(Path.Combine(dest, "VoltManager.exe")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* synthetic fixture cleanup */ }
        }
    }

    [Fact]
    public void Replace_retains_backup_and_preserves_target_when_backup_contains_junction()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
        string dest = Path.Combine(root, "VoltManager");
        string staging = Path.Combine(root, "staging");
        string backup = Path.Combine(root, "backup");
        string outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(dest);
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(outside);
        string outsideFile = Path.Combine(outside, "target.txt");
        File.WriteAllText(outsideFile, "survive");
        File.WriteAllText(Path.Combine(dest, "VoltManager.exe"), "verified");
        InstallManifest.Write(dest, new[] { "VoltManager.exe" });
        File.WriteAllText(Path.Combine(staging, "VoltManager.exe"), "verified");
        string zip = CreateSyntheticPayloadZip(root, staging);

        string junction = Path.Combine(backup, "linked");
        try
        {
            string? backupWarning = InstallEngine.ReplaceInstallDirectoryContentsCore(
                dest, staging, backup, zip,
                (source, target) =>
                {
                    File.Move(source, target);
                    if (!Directory.Exists(junction))
                    {
                        using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                            "cmd.exe", "/c mklink /J \"" + junction + "\" \"" + outside + "\"")
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        });
                        mklink!.WaitForExit();
                        Assert.Equal(0, mklink.ExitCode);
                    }
                });

            Assert.NotNull(backupWarning);
            Assert.Contains(backup, backupWarning);
            Assert.True(Directory.Exists(backup));
            Assert.True(File.Exists(outsideFile));
            Assert.Equal("survive", File.ReadAllText(outsideFile));
        }
        finally
        {
            try { if (Directory.Exists(junction)) Directory.Delete(junction, false); } catch { /* best-effort */ }
            try { Directory.Delete(root, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void TryDeleteDirectoryTree_does_not_modify_or_delete_junction_target_with_readonly_file()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
        string tree = Path.Combine(root, "tree");
        string outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(tree);
        Directory.CreateDirectory(outside);
        string outsideFile = Path.Combine(outside, "important.txt");
        File.WriteAllText(outsideFile, "protect me");
        File.SetAttributes(outsideFile, FileAttributes.ReadOnly);

        string internalFile = Path.Combine(tree, "internal.txt");
        File.WriteAllText(internalFile, "delete me");
        File.SetAttributes(internalFile, FileAttributes.ReadOnly);

        string junction = Path.Combine(tree, "junction");
        try
        {
            using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "cmd.exe", "/c mklink /J \"" + junction + "\" \"" + outside + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            }))
            {
                mklink!.WaitForExit();
                Assert.Equal(0, mklink.ExitCode);
            }

            bool deleted = InstallEngine.TryDeleteDirectoryTree(tree, out string error);

            Assert.True(deleted, "Expected tree deletion to succeed: " + error);
            Assert.False(Directory.Exists(tree));
            Assert.True(Directory.Exists(outside));
            Assert.True(File.Exists(outsideFile));
            Assert.Equal("protect me", File.ReadAllText(outsideFile));
            FileAttributes outsideAttrs = File.GetAttributes(outsideFile);
            Assert.True((outsideAttrs & FileAttributes.ReadOnly) != 0, "Expected ReadOnly attribute to be preserved on junction target file.");
        }
        finally
        {
            try { File.SetAttributes(outsideFile, FileAttributes.Normal); } catch { /* best-effort */ }
            try { if (Directory.Exists(junction)) Directory.Delete(junction, false); } catch { /* best-effort */ }
            try { Directory.Delete(root, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void DeleteOwnedTempFile_removes_owned_zip_allowing_temp_dir_removal_and_preserves_unowned_or_foreign_files()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        string zip = Path.Combine(tempDir, "VoltManagerPayload.zip");
        File.WriteAllText(zip, "fake payload zip");

        try
        {
            // When ownedTempDir is null, external file is preserved
            string externalDir = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(externalDir);
            string externalZip = Path.Combine(externalDir, "external.zip");
            File.WriteAllText(externalZip, "keep external");
            try
            {
                InstallEngine.DeleteOwnedTempFile(externalZip, null);
                Assert.True(File.Exists(externalZip));
            }
            finally
            {
                try { Directory.Delete(externalDir, true); } catch { /* best-effort cleanup */ }
            }

            // Normal owned cleanup deletes the zip and allows TryDeleteTempDirectory to remove tempDir
            InstallEngine.DeleteOwnedTempFile(zip, tempDir);
            Assert.False(File.Exists(zip));

            MethodInfo tryDelete = typeof(InstallEngine).GetMethod(
                "TryDeleteTempDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
            tryDelete.Invoke(null, new object?[] { tempDir });
            Assert.False(Directory.Exists(tempDir));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void DeleteOwnedTempFile_with_foreign_file_leaves_temp_dir_retained()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        string zip = Path.Combine(tempDir, "VoltManagerPayload.zip");
        string foreign = Path.Combine(tempDir, "foreign.txt");
        File.WriteAllText(zip, "payload");
        File.WriteAllText(foreign, "foreign");

        try
        {
            InstallEngine.DeleteOwnedTempFile(zip, tempDir);
            Assert.False(File.Exists(zip));
            Assert.True(File.Exists(foreign));

            MethodInfo tryDelete = typeof(InstallEngine).GetMethod(
                "TryDeleteTempDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
            tryDelete.Invoke(null, new object?[] { tempDir });
            Assert.True(Directory.Exists(tempDir));
            Assert.True(File.Exists(foreign));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    private static string CreateSyntheticPayloadZip(string root, string staging)
    {
        string zip = Path.Combine(root, "payload.zip");
        ZipFile.CreateFromDirectory(staging, zip);
        return zip;
    }
}
