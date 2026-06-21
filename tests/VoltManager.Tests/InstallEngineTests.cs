using System.IO;
using VoltManager.Setup.Engine;

namespace VoltManager.Tests;

public class InstallEngineTests
{
    [Fact]
    public void ClearInstallDirectory_RemovesChildren_ButKeepsRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "VoltManager.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            string file = Path.Combine(dir, "old.txt");
            File.WriteAllText(file, "old");
            File.SetAttributes(file, FileAttributes.ReadOnly);

            string subdir = Path.Combine(dir, "old-folder");
            Directory.CreateDirectory(subdir);
            string nested = Path.Combine(subdir, "nested.txt");
            File.WriteAllText(nested, "nested");
            File.SetAttributes(nested, FileAttributes.ReadOnly);

            InstallEngine.ClearInstallDirectory(dir);

            Assert.True(Directory.Exists(dir));
            Assert.Empty(Directory.GetFileSystemEntries(dir));
        }
        finally
        {
            DeleteTempDirectory(dir);
        }
    }

    private static void DeleteTempDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return;

        foreach (string path in Directory.GetFileSystemEntries(dir, "*", SearchOption.AllDirectories))
        {
            FileAttributes attrs = File.GetAttributes(path);
            attrs &= ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
            File.SetAttributes(path, attrs);
        }

        Directory.Delete(dir, true);
    }
}
