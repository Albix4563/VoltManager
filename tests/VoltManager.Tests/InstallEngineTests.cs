using System.IO;
using VoltManager.Setup.Engine;

namespace VoltManager.Tests;

public class InstallEngineTests
{
    [Fact]
    public void GetDefaultInstallDir_IsUnderProgramFiles_AndNamedVoltManager()
    {
        string dir = InstallOptions.GetDefaultInstallDir();

        Assert.False(string.IsNullOrWhiteSpace(dir));
        Assert.Equal("VoltManager", Path.GetFileName(dir));
        Assert.True(Path.IsPathRooted(dir));

        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.StartsWith(Path.GetFullPath(pf).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstallOptions_DefaultInstallDir_IsPopulated()
    {
        var opts = new InstallOptions();
        Assert.False(string.IsNullOrWhiteSpace(opts.InstallDir));
        Assert.Equal(InstallOptions.GetDefaultInstallDir(), opts.InstallDir);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeInstallDir_Empty_FallsBackToDefault(string? input)
    {
        Assert.Equal(InstallOptions.GetDefaultInstallDir(), InstallOptions.NormalizeInstallDir(input));
    }

    [Fact]
    public void NormalizeInstallDir_CustomPath_IsNormalized()
    {
        string custom = Path.Combine(Path.GetTempPath(), "VoltManagerCustom");
        string result = InstallOptions.NormalizeInstallDir(custom + Path.DirectorySeparatorChar);
        Assert.Equal(Path.GetFullPath(custom), result);
    }

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

    [Fact]
    public void TryDeleteDirectoryTree_RemovesReadonlyTree()
    {
        string dir = Path.Combine(Path.GetTempPath(), "VoltManager.Tests.Del." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            string file = Path.Combine(dir, "locked.txt");
            File.WriteAllText(file, "x");
            File.SetAttributes(file, FileAttributes.ReadOnly);

            string sub = Path.Combine(dir, "sub");
            Directory.CreateDirectory(sub);
            string nested = Path.Combine(sub, "n.txt");
            File.WriteAllText(nested, "y");
            File.SetAttributes(nested, FileAttributes.ReadOnly);

            bool ok = InstallEngine.TryDeleteDirectoryTree(dir, out string error);

            Assert.True(ok, error);
            Assert.False(Directory.Exists(dir));
        }
        finally
        {
            DeleteTempDirectory(dir);
        }
    }

    [Fact]
    public void UninstallResult_SuccessOnlyWhenNoFailures()
    {
        var r = new UninstallResult();
        Assert.True(r.Success);
        Assert.Equal("", r.Summary);

        r.Add("still present");
        Assert.False(r.Success);
        Assert.Equal("still present", r.Summary);
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
