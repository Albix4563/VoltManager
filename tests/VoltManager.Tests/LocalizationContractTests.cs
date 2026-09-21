using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text.RegularExpressions;
using VoltManager.Localization;

namespace VoltManager.Tests;

public sealed class LocalizationContractTests
{
    private static readonly Regex PlaceholderRegex = new(@"\{\d+(?:[^}]*)\}", RegexOptions.Compiled);

    [Fact]
    public void Native_resource_catalogs_keep_key_value_and_placeholder_parity()
    {
        var manager = new ResourceManager(
            "VoltManager.Localization.NativeStrings",
            typeof(LocalizationService).Assembly);

        Dictionary<string, string> english = Read(manager, CultureInfo.GetCultureInfo("en"));
        Assert.NotEmpty(english);

        foreach (string cultureName in new[] { "it-IT", "es-ES", "zh-Hans" })
        {
            Dictionary<string, string> localized = Read(manager, CultureInfo.GetCultureInfo(cultureName));
            Assert.Equal(english.Keys.Order(), localized.Keys.Order());

            foreach ((string key, string englishValue) in english)
            {
                string value = localized[key];
                Assert.False(string.IsNullOrWhiteSpace(value), $"{cultureName}/{key} is empty");
                Assert.Equal(Placeholders(englishValue), Placeholders(value));
            }
        }
    }

    private static Dictionary<string, string> Read(ResourceManager manager, CultureInfo culture)
    {
        ResourceSet resourceSet = manager.GetResourceSet(culture, createIfNotExists: true, tryParents: true)!;
        return resourceSet.Cast<DictionaryEntry>().ToDictionary(
            entry => (string)entry.Key,
            entry => entry.Value?.ToString() ?? string.Empty,
            StringComparer.Ordinal);
    }

    private static string[] Placeholders(string value)
        => PlaceholderRegex.Matches(value).Select(match => match.Value).Order().ToArray();
}
