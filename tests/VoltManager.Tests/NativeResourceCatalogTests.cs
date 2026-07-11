using System.Globalization;
using System.Reflection;
using System.Resources;

namespace VoltManager.Tests;

/// <summary>
/// Verifies parity across all .resx catalogues for the native WPF strings.
/// </summary>
public class NativeResourceCatalogTests
{
    private static readonly string[] Cultures = ["it-IT", "en", "es-ES", "zh-Hans"];

    private ResourceManager GetResourceManager()
    {
        return new ResourceManager("VoltManager.Localization.NativeStrings",
            Assembly.Load("VoltManager"));
    }

    [Fact]
    public void AllCultures_HaveSameKeysAsEnglish()
    {
        var rm = GetResourceManager();
        var enSet = rm.GetResourceSet(CultureInfo.GetCultureInfo("en"), true, true);
        Assert.NotNull(enSet);
        var enKeys = enSet.Cast<System.Collections.DictionaryEntry>()
            .Select(e => (string)e.Key)
            .OrderBy(k => k)
            .ToList();

        Assert.NotEmpty(enKeys);

        foreach (var cultureName in Cultures)
        {
            var culture = CultureInfo.GetCultureInfo(cultureName);
            var set = rm.GetResourceSet(culture, true, true);
            Assert.NotNull(set);
            var keys = set.Cast<System.Collections.DictionaryEntry>()
                .Select(e => (string)e.Key)
                .OrderBy(k => k)
                .ToList();

            Assert.Equal(enKeys, keys);
        }
    }

    [Theory]
    [InlineData("it-IT")]
    [InlineData("en")]
    [InlineData("es-ES")]
    [InlineData("zh-Hans")]
    public void AllValues_AreNonEmpty(string cultureName)
    {
        var rm = GetResourceManager();
        var culture = CultureInfo.GetCultureInfo(cultureName);
        var set = rm.GetResourceSet(culture, true, true);
        Assert.NotNull(set);

        foreach (System.Collections.DictionaryEntry entry in set)
        {
            var key = (string)entry.Key;
            var value = entry.Value as string;
            Assert.NotNull(value);
            Assert.NotEmpty(value);
            Assert.False(value == key, $"Key '{key}' has its own name as value in culture {cultureName}");
        }
    }
}
