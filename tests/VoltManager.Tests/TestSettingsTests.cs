using System.IO;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class TestSettingsTests
{
    [Fact]
    public void Create_UsesUniqueTemporarySettingsPath()
    {
        var settings = TestSettings.Create(out string path);
        string production = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VoltManager", "settings.json"));

        settings.Current.Language = "en";
        settings.Save();

        Assert.True(File.Exists(path));
        Assert.NotEqual(production, Path.GetFullPath(path));
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);

        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }
}

