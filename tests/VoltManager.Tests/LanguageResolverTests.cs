using VoltManager.Localization;

namespace VoltManager.Tests;

public class LanguageResolverTests
{
    [Theory]
    [InlineData("es", "es")]
    [InlineData("ES", "es")]
    [InlineData("es-ES", "es")]
    [InlineData("ES-es", "es")]
    [InlineData("en", "en")]
    [InlineData("en-GB", "en")]
    [InlineData("en_US", "en")]
    [InlineData("it", "it")]
    [InlineData("it-IT", "it")]
    [InlineData("zh", "zh")]
    [InlineData("zh-Hans", "zh")]
    [InlineData("zh-CN", "zh")]
    [InlineData("zh-Hant", "zh")]
    [InlineData("zh-TW", "zh")]
    [InlineData("es-MX", "es")]
    [InlineData("es-AR", "es")]
    public void Normalize_SupportedCodes_ReturnsCanonicalForm(string input, string expected)
    {
        var result = LanguageResolver.Normalize(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("fr")]
    [InlineData("fr-FR")]
    [InlineData("de")]
    [InlineData("jp")]
    public void Normalize_UnsupportedCodes_ReturnsEmpty(string? input)
    {
        var result = LanguageResolver.Normalize(input);
        Assert.Equal("", result);
    }

    [Theory]
    [InlineData("es", true)]
    [InlineData("en", true)]
    [InlineData("it", true)]
    [InlineData("zh", true)]
    [InlineData("es-ES", true)]
    [InlineData("zh-Hans", true)]
    [InlineData("fr", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSupported_ValidatesCorrectly(string? code, bool expected)
    {
        Assert.Equal(expected, LanguageResolver.IsSupported(code));
    }

    [Fact]
    public void GetCulture_Es_ReturnsEsES()
    {
        var culture = LanguageResolver.GetCulture("es");
        Assert.NotNull(culture);
        Assert.Equal("es-ES", culture.Name);
    }

    [Fact]
    public void GetCulture_En_ReturnsEnGB()
    {
        var culture = LanguageResolver.GetCulture("en");
        Assert.NotNull(culture);
        Assert.True(culture.Name == "en-GB");
    }

    [Theory]
    [InlineData("it", "")]
    [InlineData("en", "")]
    [InlineData("es", "")]
    [InlineData("zh", "")]
    public void Resolve_SettingsLangPresent_ReturnsNormalized(string expected, string? localStorage)
    {
        var result = LanguageResolver.Resolve(expected, localStorage);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("zh", "zh")]
    [InlineData("en", "en")]
    [InlineData("es", "es")]
    public void Resolve_SettingsEmpty_LocalStorageValid_ReturnsLocalStorage(string expected, string localStorage)
    {
        var result = LanguageResolver.Resolve("", localStorage);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Resolve_SettingsUnsupported_LocalStorageValid_ReturnsLocalStorage()
    {
        var result = LanguageResolver.Resolve("fr", "es");
        Assert.Equal("es", result);
    }
}
