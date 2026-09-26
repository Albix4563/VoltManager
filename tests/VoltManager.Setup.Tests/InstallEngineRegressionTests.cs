using System.Reflection;
using VoltManager.Setup.Engine;

namespace VoltManager.Setup.Tests;

public sealed class InstallEngineRegressionTests
{
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
            Task task = (Task)method.Invoke(null, new object?[] { dest, CancellationToken.None, zip })!;

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
    public void Replace_swaps_contents_and_removes_backup()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
        string dest = Path.Combine(root, "VoltManager");
        string staging = Path.Combine(root, "staging");
        string backup = Path.Combine(root, "backup");
        Directory.CreateDirectory(dest);
        Directory.CreateDirectory(Path.Combine(staging, "wwwroot"));
        File.WriteAllText(Path.Combine(dest, "old.txt"), "old");
        File.WriteAllText(Path.Combine(staging, "new.txt"), "new");
        File.WriteAllText(Path.Combine(staging, "wwwroot", "index.html"), "<html>");

        try
        {
            InstallEngine.ReplaceInstallDirectoryContents(dest, staging, backup);

            Assert.False(File.Exists(Path.Combine(dest, "old.txt")));
            Assert.Equal("new", File.ReadAllText(Path.Combine(dest, "new.txt")));
            Assert.True(File.Exists(Path.Combine(dest, "wwwroot", "index.html")));
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

        using (InstallEngine.InstallDirectoryLock.Acquire(dest, TimeSpan.FromSeconds(1)))
        {
            // Mutexes are re-entrant per thread: contend from another thread.
            await Assert.ThrowsAsync<IOException>(() => Task.Run(() =>
            {
                using (InstallEngine.InstallDirectoryLock.Acquire(dest, TimeSpan.FromMilliseconds(200))) { }
            }));
        }

        await Task.Run(() =>
        {
            using (InstallEngine.InstallDirectoryLock.Acquire(dest, TimeSpan.FromSeconds(1))) { }
        });
    }

    [Fact]
    public void Leftover_backup_and_staging_dirs_are_removed()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
        string backup = Path.Combine(root, ".VoltManager.backup-abc");
        string staging = Path.Combine(root, ".VoltManager.staging-def");
        string unrelated = Path.Combine(root, ".Other.backup-xyz");
        foreach (string dir in new[] { backup, staging, unrelated })
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "file.txt"), "x");
        }

        try
        {
            InstallEngine.DeleteLeftoverSwapDirectories(root, "VoltManager");

            Assert.False(Directory.Exists(backup));
            Assert.False(Directory.Exists(staging));
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
        File.WriteAllText(Path.Combine(staging, "a.txt"), "a");
        string locked = Path.Combine(staging, "z-locked.txt");
        File.WriteAllText(locked, "z");

        try
        {
            // An open handle without FILE_SHARE_DELETE makes moving the staged file fail.
            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsAny<IOException>(() =>
                    InstallEngine.ReplaceInstallDirectoryContents(dest, staging, backup));
            }

            Assert.Equal("old", File.ReadAllText(Path.Combine(dest, "old.txt")));
            Assert.False(File.Exists(Path.Combine(dest, "a.txt")));
            Assert.False(File.Exists(Path.Combine(dest, "z-locked.txt")));
            Assert.False(Directory.Exists(backup));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
