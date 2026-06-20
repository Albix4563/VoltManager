using Microsoft.Win32;

namespace VoltManager.Services;

/// <summary>
/// Detects the Windows system theme (light/dark) by reading the registry and
/// listens for live changes so the host window and WebView can follow the OS
/// automatically when the user selects the "auto" theme.
/// </summary>
public sealed class ThemeService : IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private readonly SystemEventsHandler _systemEventsHandler;
    private bool _disposed;

    /// <summary>
    /// The raw theme preference stored in settings: "dark", "light", "black", or "auto".
    /// </summary>
    public string Preference { get; private set; } = "dark";

    /// <summary>
    /// The concrete theme resolved from <see cref="Preference"/>. When the
    /// preference is "auto" this reflects the current Windows system theme
    /// ("dark" or "light"); otherwise it mirrors <see cref="Preference"/>.
    /// </summary>
    public string ResolvedTheme { get; private set; } = "dark";

    /// <summary>
    /// Raised whenever <see cref="ResolvedTheme"/> changes — either because
    /// the user switched preference or because Windows changed its system
    /// theme while "auto" was active. Fires on a thread-pool thread; callers
    /// must marshal to the UI thread.
    /// </summary>
    public event Action<string>? ThemeChanged;

    public ThemeService()
    {
        _systemEventsHandler = new SystemEventsHandler(OnSystemThemeChanged);
    }

    /// <summary>
    /// Sets the user's theme preference and re-evaluates <see cref="ResolvedTheme"/>.
    /// </summary>
    /// <param name="preference">One of "dark", "light", "black", "auto".</param>
    public void SetPreference(string? preference)
    {
        Preference = NormalizePreference(preference);
        UpdateResolvedTheme();
    }

    /// <summary>
    /// Re-reads the Windows system theme and updates <see cref="ResolvedTheme"/>
    /// if the preference is "auto". Call this at startup or after a settings
    /// reload to ensure the resolved theme is fresh.
    /// </summary>
    public void Refresh()
    {
        UpdateResolvedTheme();
    }

    /// <summary>
    /// Returns the concrete theme that should be applied right now for the
    /// given preference string. When preference is "auto", reads the Windows
    /// registry to determine light/dark.
    /// </summary>
    public static string Resolve(string? preference)
    {
        string pref = NormalizePreference(preference);
        if (pref != "auto") return pref;
        return IsSystemLightTheme() ? "light" : "dark";
    }

    private static string NormalizePreference(string? preference)
    {
        return preference?.Trim().ToLowerInvariant() switch
        {
            "light" => "light",
            "black" => "black",
            "auto" => "auto",
            _ => "dark",
        };
    }

    private void UpdateResolvedTheme()
    {
        string newResolved = Resolve(Preference);
        if (newResolved == ResolvedTheme) return;

        ResolvedTheme = newResolved;
        ThemeChanged?.Invoke(ResolvedTheme);
    }

    private void OnSystemThemeChanged()
    {
        // Only react to system changes when the user is in "auto" mode.
        if (Preference != "auto") return;
        UpdateResolvedTheme();
    }

    /// <summary>
    /// Reads the Windows registry to determine whether the system is currently
    /// using a light theme. Returns false (dark) if the value cannot be read.
    /// </summary>
    private static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            if (key?.GetValue("AppsUseLightTheme") is int value)
                return value == 1;
        }
        catch
        {
            // Registry inaccessible (non-Windows, permissions, etc.) — default to dark.
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _systemEventsHandler.Dispose();
    }

    /// <summary>
    /// Wraps <see cref="SystemEvents.UserPreferenceChanged"/> subscription so
    /// the handler can be cleanly removed on dispose. SystemEvents fires on a
    /// thread-pool thread, so callers must marshal to the UI thread.
    /// </summary>
    private sealed class SystemEventsHandler : IDisposable
    {
        private readonly UserPreferenceChangedEventHandler _handler;
        private bool _disposed;

        public SystemEventsHandler(Action onChanged)
        {
            _handler = (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General)
                    onChanged();
            };
            SystemEvents.UserPreferenceChanged += _handler;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            SystemEvents.UserPreferenceChanged -= _handler;
        }
    }
}
