using System.IO;
using VoltManager.Services;

namespace VoltManager.Tests;

internal static class TestSettings
{
    public static SettingsService Create()
        => Create(out _);

    public static SettingsService Create(out string path)
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManager.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        path = Path.Combine(root, "settings.json");
        return new SettingsService(path);
    }
}
