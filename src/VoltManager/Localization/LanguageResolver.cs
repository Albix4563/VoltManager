using System.Globalization;

namespace VoltManager.Localization;

public static class LanguageResolver
{
    public static readonly string[] SupportedCodes = ["it", "en", "zh", "es"];

    private static readonly Dictionary<string, string> CultureToCode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["it"] = "it", ["it-IT"] = "it", ["it-CH"] = "it",
        ["en"] = "en", ["en-GB"] = "en", ["en-US"] = "en",
        ["zh"] = "zh", ["zh-CN"] = "zh", ["zh-Hans"] = "zh", ["zh-Hant"] = "zh", ["zh-TW"] = "zh",
        ["es"] = "es", ["es-ES"] = "es", ["es-MX"] = "es", ["es-AR"] = "es",
    };

    private static readonly Dictionary<string, CultureInfo> CodeToCulture = new(StringComparer.OrdinalIgnoreCase)
    {
        ["it"] = new CultureInfo("it-IT"),
        ["en"] = new CultureInfo("en-GB"),
        ["zh"] = new CultureInfo("zh-CN"),
        ["es"] = new CultureInfo("es-ES"),
    };

    public static bool IsSupported(string? code)
        => !string.IsNullOrEmpty(code) && SupportedCodes.Contains(Normalize(code!));

    public static string Normalize(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "";
        var trimmed = code.Trim().Replace('_', '-');
        if (CultureToCode.TryGetValue(trimmed, out var mapped)) return mapped;
        // Try two-letter fallback
        var twoLetter = trimmed.Length >= 2 ? trimmed[..2] : "";
        if (CultureToCode.TryGetValue(twoLetter, out mapped)) return mapped;
        return "";
    }

    public static CultureInfo GetCulture(string code)
    {
        var normalized = Normalize(code);
        return CodeToCulture.TryGetValue(normalized, out var culture)
            ? culture
            : CodeToCulture["en"];
    }

    public static string ResolveFromOs()
    {
        try
        {
            var osCulture = CultureInfo.CurrentUICulture;
            var normalized = Normalize(osCulture.Name);
            if (!string.IsNullOrEmpty(normalized)) return normalized;
        }
        catch { /* fall through */ }
        return "en";
    }

    public static string Resolve(string? settingsLanguage, string? localStorageLang = null)
    {
        // 1. settings.language if valid
        if (!string.IsNullOrEmpty(settingsLanguage))
        {
            var n = Normalize(settingsLanguage);
            if (!string.IsNullOrEmpty(n)) return n;
        }

        // 2. localStorage.volt_lang if valid and settings.language is absent/empty
        if (!string.IsNullOrEmpty(localStorageLang))
        {
            var n = Normalize(localStorageLang);
            if (!string.IsNullOrEmpty(n)) return n;
        }

        // 3. OS culture
        return ResolveFromOs();
    }
}
