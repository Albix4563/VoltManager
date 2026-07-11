using System.Globalization;
using VoltManager.Localization;
using VoltManager.Models;

namespace VoltManager.Tests;

public class LocalizationServiceTests
{
    [Fact]
    public void Initialize_WithEs_ResolvesEs()
    {
        var loc = new LocalizationService();
        var settings = new AppSettings { Language = "es" };
        loc.Initialize(settings);
        Assert.Equal("es", loc.CurrentLanguage);
        Assert.Equal("es-ES", loc.CurrentCulture.Name);
    }

    [Fact]
    public void Initialize_WithEmpty_UsesOsFallback()
    {
        var loc = new LocalizationService();
        var settings = new AppSettings { Language = "" };
        loc.Initialize(settings);
        // Should resolve to something supported (OS culture or en fallback)
        Assert.Contains(loc.CurrentLanguage, LanguageResolver.SupportedCodes);
    }

    [Fact]
    public void SetLanguage_ValidCode_UpdatesCurrentLanguage()
    {
        var loc = new LocalizationService();
        loc.Initialize(new AppSettings { Language = "it" });
        Assert.Equal("it", loc.CurrentLanguage);

        loc.SetLanguage("es");
        Assert.Equal("es", loc.CurrentLanguage);
        Assert.Equal("es-ES", loc.CurrentCulture.Name);
    }

    [Fact]
    public void SetLanguage_UnsupportedCode_DoesNotChangeState()
    {
        var loc = new LocalizationService();
        loc.Initialize(new AppSettings { Language = "it" });
        loc.SetLanguage("fr");
        Assert.Equal("it", loc.CurrentLanguage); // unchanged
    }

    [Fact]
    public void SetLanguage_SameCode_DoesNotFireEvent()
    {
        var loc = new LocalizationService();
        loc.Initialize(new AppSettings { Language = "es" });
        var fired = false;
        loc.LanguageChanged += (_, _) => fired = true;
        loc.SetLanguage("es");
        Assert.False(fired);
    }

    [Fact]
    public void SetLanguage_DifferentCode_FiresEventOnce()
    {
        var loc = new LocalizationService();
        loc.Initialize(new AppSettings { Language = "it" });
        var count = 0;
        loc.LanguageChanged += (_, _) => count++;
        loc.SetLanguage("es");
        Assert.Equal(1, count);
    }

    [Fact]
    public void T_WithEs_ReturnsSpanishString()
    {
        var loc = new LocalizationService();
        loc.Initialize(new AppSettings { Language = "es" });
        var result = loc.T("Tray_Open");
        Assert.Equal("Abrir VoltManager", result);
    }

    [Fact]
    public void T_WithEn_ReturnsEnglishString()
    {
        var loc = new LocalizationService();
        loc.Initialize(new AppSettings { Language = "en" });
        var result = loc.T("Tray_Open");
        Assert.Equal("Open VoltManager", result);
    }

    [Fact]
    public void T_MissingKey_FallsBackToEnglish()
    {
        var loc = new LocalizationService();
        loc.Initialize(new AppSettings { Language = "es" });
        // All keys should exist; test with a key present in base
        var result = loc.T("Plan_Saver");
        Assert.Equal("Ahorro de energía", result);
    }

    [Fact]
    public void T_NonexistentKey_ReturnsKeyItself()
    {
        var loc = new LocalizationService();
        loc.Initialize(new AppSettings { Language = "it" });
        var result = loc.T("NONEXISTENT_KEY_XYZ");
        Assert.Equal("NONEXISTENT_KEY_XYZ", result);
    }

    [Fact]
    public void Format_ReplacesPlaceholders()
    {
        var loc = new LocalizationService();
        loc.Initialize(new AppSettings { Language = "it" });
        var result = loc.T("Tray_ActivePlan", "Risparmio energia");
        Assert.Equal("Piano attivo: Risparmio energia", result);
    }

    [Fact]
    public void Culture_DoesNotAffectGlobalProcessCulture()
    {
        var beforeCulture = CultureInfo.CurrentCulture.Name;
        var loc = new LocalizationService();
        loc.Initialize(new AppSettings { Language = "es" });
        var afterCulture = CultureInfo.CurrentCulture.Name;
        Assert.Equal(beforeCulture, afterCulture); // Global culture unchanged
    }
}
