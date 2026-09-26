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
