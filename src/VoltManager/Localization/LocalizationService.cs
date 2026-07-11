using System.Globalization;
using System.Resources;
using System.Reflection;
using VoltManager.Models;

namespace VoltManager.Localization;

public class LocalizationService
{
    private readonly ResourceManager _rm;
    private string _currentLanguage = "it";
    private CultureInfo _currentCulture;

    public string CurrentLanguage => _currentLanguage;
    public CultureInfo CurrentCulture => _currentCulture;
    public event Action<string, CultureInfo>? LanguageChanged;

    public LocalizationService()
    {
        _rm = new ResourceManager("VoltManager.Localization.NativeStrings", Assembly.GetExecutingAssembly());
        _currentCulture = LanguageResolver.GetCulture("it");
    }

    public void Initialize(AppSettings settings)
    {
        var resolved = LanguageResolver.Resolve(settings.Language);
        ApplyLanguage(resolved, persist: false);
    }

    public void SetLanguage(string code)
    {
        if (!LanguageResolver.IsSupported(code)) return;
        var normalized = LanguageResolver.Normalize(code);
        if (string.IsNullOrEmpty(normalized)) return;
        if (normalized == _currentLanguage) return; // no loop
        ApplyLanguage(normalized, persist: true);
    }

    private void ApplyLanguage(string code, bool persist)
    {
        _currentLanguage = code;
        _currentCulture = LanguageResolver.GetCulture(code);
        LanguageChanged?.Invoke(code, _currentCulture);
    }

    public string T(string key)
    {
        try
        {
            var value = _rm.GetString(key, _currentCulture);
            if (!string.IsNullOrEmpty(value)) return value;
        }
        catch { /* fallback to neutral */ }

        try
        {
            var value = _rm.GetString(key, CultureInfo.GetCultureInfo("en"));
            if (!string.IsNullOrEmpty(value)) return value;
        }
        catch { /* fallback to key */ }

        return key;
    }

    public string T(string key, params object[] args)
    {
        var template = T(key);
        try { return string.Format(_currentCulture, template, args); }
        catch { return template; }
    }
}
