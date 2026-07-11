using VoltManager.Setup.Engine;

namespace VoltManager.Tests;

/// <summary>
/// Verifies parity across installer I18n catalogues and --lang parsing.
/// </summary>
public class InstallerI18nTests
{
    [Fact]
    public void AllLanguages_HaveSameKeys()
    {
        // Use reflection to access private dictionaries
        var type = typeof(I18n);
        var itField = type.GetField("It", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var enField = type.GetField("En", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var zhField = type.GetField("Zh", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var esField = type.GetField("Es", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(itField);
        Assert.NotNull(enField);
        Assert.NotNull(zhField);
        Assert.NotNull(esField);

        var it = itField!.GetValue(null) as System.Collections.Generic.Dictionary<string, string>;
        var en = enField!.GetValue(null) as System.Collections.Generic.Dictionary<string, string>;
        var zh = zhField!.GetValue(null) as System.Collections.Generic.Dictionary<string, string>;
        var es = esField!.GetValue(null) as System.Collections.Generic.Dictionary<string, string>;

        Assert.NotNull(it);
        Assert.NotNull(en);
        Assert.NotNull(zh);
        Assert.NotNull(es);

        var enKeys = en!.Keys.OrderBy(k => k).ToList();
        Assert.NotEmpty(enKeys);

        Assert.Equal(enKeys, it!.Keys.OrderBy(k => k).ToList());
        Assert.Equal(enKeys, zh!.Keys.OrderBy(k => k).ToList());
        Assert.Equal(enKeys, es!.Keys.OrderBy(k => k).ToList());
    }

    [Fact]
    public void EsValues_AreNonEmpty()
    {
        var type = typeof(I18n);
        var esField = type.GetField("Es", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var es = esField!.GetValue(null) as System.Collections.Generic.Dictionary<string, string>;
        Assert.NotNull(es);

        foreach (var kv in es!)
        {
            Assert.False(string.IsNullOrWhiteSpace(kv.Value),
                $"Key '{kv.Key}' has empty/null value in Spanish");
            Assert.False(kv.Value == kv.Key,
                $"Key '{kv.Key}' has its own name as value in Spanish");
        }
    }

    [Theory]
    [InlineData("--lang", "es")]
    [InlineData("--lang", "ES")]
    [InlineData("--lang", "zh")]
    [InlineData("--lang", "en")]
    public void SetupArgs_ParsesLangFlag(string flag, string value)
    {
        var args = SetupArgs.Parse([flag, value]);
        Assert.NotEqual("", args.Language);
    }

    [Fact]
    public void SetupArgs_NoLangFlag_LanguageIsEmpty()
    {
        var args = SetupArgs.Parse([]);
        Assert.Equal("", args.Language);
    }

    [Fact]
    public void SetupArgs_UpdateWithLang_ParsesCorrectly()
    {
        var args = SetupArgs.Parse(["/update", "--pid", "42", "--lang", "es"]);
        Assert.Equal(SetupMode.Update, args.Mode);
        Assert.Equal(42, args.WaitPid);
        Assert.Equal("es", args.Language);
    }

    [Fact]
    public void SetupArgs_UninstallWithLang_ParsesCorrectly()
    {
        var args = SetupArgs.Parse(["/uninstall", "--lang", "es"]);
        Assert.Equal(SetupMode.Uninstall, args.Mode);
        Assert.Equal("es", args.Language);
    }
}
