using Xunit;
using System.IO;
using System.Text.Json;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public class SettingsServiceFontTests
{
    private static readonly string[] AllowedFonts =
    {
        "inter",
        "segoe-ui",
        "arial",
        "calibri",
        "verdana",
        "tahoma",
        "trebuchet-ms",
        "georgia",
        "times-new-roman",
        "consolas",
    };

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

    [Theory]
    [InlineData("inter")]
    [InlineData("segoe-ui")]
    [InlineData("arial")]
    [InlineData("calibri")]
    [InlineData("verdana")]
    [InlineData("tahoma")]
    [InlineData("trebuchet-ms")]
    [InlineData("georgia")]
    [InlineData("times-new-roman")]
    [InlineData("consolas")]
    [InlineData(" SEGOE-UI ")]
    [InlineData("Times-New-Roman")]
    public void Load_WhenFontIsAllowlisted_NormalizesAndPreserves(string raw)
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var json = JsonSerializer.Serialize(new { font = raw });
            File.WriteAllText(tempFile, json);

            var settingsService = new SettingsService(tempFile);
            var expected = raw.Trim().ToLowerInvariant();
            Assert.Equal(expected, settingsService.Current.Font);
            Assert.Contains(settingsService.Current.Font, AllowedFonts);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Theory]
    [InlineData("segoe-ui")]
    [InlineData("consolas")]
    [InlineData("times-new-roman")]
    public void Save_WhenFontIsValid_RoundTripsThroughDisk(string font)
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "{}");
            var settingsService = new SettingsService(tempFile);
            settingsService.Current.Font = font;
            settingsService.Save();

            var reloaded = new SettingsService(tempFile);
            Assert.Equal(font, reloaded.Current.Font);

            using var doc = JsonDocument.Parse(File.ReadAllText(tempFile));
            Assert.Equal(font, doc.RootElement.GetProperty("font").GetString());
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void Update_WhenFontIsInvalid_NormalizesToInter()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "{}");
            var settingsService = new SettingsService(tempFile);
            var next = new AppSettings { Font = "not-a-real-font" };
            settingsService.Update(next);
            Assert.Equal("inter", settingsService.Current.Font);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }
}
