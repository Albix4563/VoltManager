using Xunit;
using System.IO;
using System.Text.Json;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public class SettingsServiceFontTests
{
    [Fact]
    public void Load_WhenFontIsMissing_UsesDefaultInter()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var json = "{}";
            File.WriteAllText(tempFile, json);

            var settingsService = new SettingsService(tempFile);
            Assert.Equal("inter", settingsService.Current.Font);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void Load_WhenFontIsValid_PreservesValue()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var json = "{\"font\": \"segoe-ui\"}";
            File.WriteAllText(tempFile, json);

            var settingsService = new SettingsService(tempFile);
            Assert.Equal("segoe-ui", settingsService.Current.Font);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void Load_WhenFontIsInvalid_FallsBackToInter()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var json = "{\"font\": \"comic-sans\"}";
            File.WriteAllText(tempFile, json);

            var settingsService = new SettingsService(tempFile);
            Assert.Equal("inter", settingsService.Current.Font);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }
}
