using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using VoltManager.Models;

namespace VoltManager.Services;

public sealed record ThemePalette(
    AppThemeColor ThemeColor,
    Color Background,
    Color Surface,
    Color SurfaceElevated,
    Color Primary,
    Color Secondary,
    Color Hover,
    Color Text,
    Color MutedText,
    Color Border,
    Color OnPrimary)
{
    public string Key => ThemeColor.ToKey();
}

public sealed record ThemeWebPalette(
    [property: JsonPropertyName("background")] string Background,
    [property: JsonPropertyName("surface")] string Surface,
    [property: JsonPropertyName("surfaceElevated")] string SurfaceElevated,
    [property: JsonPropertyName("primary")] string Primary,
    [property: JsonPropertyName("secondary")] string Secondary,
    [property: JsonPropertyName("hover")] string Hover,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("mutedText")] string MutedText,
    [property: JsonPropertyName("border")] string Border,
    [property: JsonPropertyName("onPrimary")] string OnPrimary);

public sealed record ThemeWebState(
    [property: JsonPropertyName("themeColor")] string ThemeColor,
    [property: JsonPropertyName("palette")] ThemeWebPalette Palette,
    [property: JsonPropertyName("customColor")] string? CustomColor);

/// <summary>
/// Applies the selected color theme to WPF resources and exposes the same
/// centralized palette to WebView-based UI surfaces.
/// </summary>
public sealed class ThemeService
{
    private const double MinimumTextContrast = 4.5;
    private const double BackgroundTintWeight = 0.08;
    private const double SurfaceTintWeight = 0.12;
    private const double ElevatedSurfaceTintWeight = 0.16;

    private static readonly Color BaseBackground = Color.FromRgb(11, 17, 32);
    private static readonly Color BaseSurface = Color.FromRgb(17, 24, 39);
    private static readonly Color BaseSurfaceElevated = Color.FromRgb(30, 41, 59);
    private static readonly Color PrimaryText = Color.FromRgb(248, 250, 252);
    private static readonly Color MutedText = Color.FromRgb(203, 213, 225);

    public AppThemeColor CurrentTheme { get; private set; } = AppThemeColor.Blue;
    public ThemePalette CurrentPalette { get; private set; } = GetPalette(AppThemeColor.Blue);
    public string? CurrentCustomColor { get; private set; }

    public event Action<AppThemeColor>? ThemeChanged;

    public ThemeService()
    {
        ApplyResources(CurrentPalette);
    }

    public void SetTheme(AppThemeColor themeColor, string? customColor = null)
    {
        var normalized = themeColor.Normalize();
        string? normalizedCustom = TryNormalizeCustomColor(customColor, out string parsedCustom)
            ? parsedCustom
            : null;
        bool changed = normalized != CurrentTheme
            || !string.Equals(normalizedCustom, CurrentCustomColor, StringComparison.OrdinalIgnoreCase);

        CurrentTheme = normalized;
        CurrentCustomColor = normalizedCustom;
        CurrentPalette = normalizedCustom is null
            ? GetPalette(normalized)
            : CreateCustomPalette(normalized, normalizedCustom);
        ApplyResources(CurrentPalette);

        if (changed)
            ThemeChanged?.Invoke(CurrentTheme);
    }

    public ThemeWebState GetWebTheme()
        => ToWebState(CurrentPalette, CurrentCustomColor);

    public static ThemeWebState CreateWebTheme(AppThemeColor themeColor, string? customColor = null)
    {
        var normalizedTheme = themeColor.Normalize();
        string? normalizedCustom = TryNormalizeCustomColor(customColor, out string parsedCustom)
            ? parsedCustom
            : null;
        var palette = normalizedCustom is null
            ? GetPalette(normalizedTheme)
            : CreateCustomPalette(normalizedTheme, normalizedCustom);
        return ToWebState(palette, normalizedCustom);
    }

    public IReadOnlyDictionary<string, ThemeWebPalette> GetWebThemeCatalog()
        => Enum.GetValues<AppThemeColor>()
            .ToDictionary(
                color => color.ToKey(),
                color => ToWebState(GetPalette(color), null).Palette,
                StringComparer.OrdinalIgnoreCase);

    public static ThemePalette GetPalette(AppThemeColor themeColor, string? customColor)
        => TryNormalizeCustomColor(customColor, out string normalizedCustom)
            ? CreateCustomPalette(themeColor.Normalize(), normalizedCustom)
            : GetPalette(themeColor);

    public static ThemePalette GetPalette(AppThemeColor themeColor)
    {
        var normalized = themeColor.Normalize();
        var accent = AppThemeColorPalette.Get(normalized);
        return Create(
            accent.ThemeColor,
            ParseHexColor(accent.Primary),
            ParseHexColor(accent.Secondary),
            ParseHexColor(accent.Hover));
    }

    public static bool TryNormalizeCustomColor(string? value, out string normalized)
    {
        normalized = string.Empty;
        string input = (value ?? string.Empty).Trim();
        if (input.Length is not (4 or 5 or 7) || input[0] != '#')
            return false;

        ReadOnlySpan<char> hex = input.AsSpan(1);
        foreach (char c in hex)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }

        byte r;
        byte g;
        byte b;
        byte a = 255;
        if (hex.Length is 3 or 4)
        {
            r = ExpandNibble(hex[0]);
            g = ExpandNibble(hex[1]);
            b = ExpandNibble(hex[2]);
            if (hex.Length == 4)
                a = ExpandNibble(hex[3]);
        }
        else
        {
            r = Convert.ToByte(input.Substring(1, 2), 16);
            g = Convert.ToByte(input.Substring(3, 2), 16);
            b = Convert.ToByte(input.Substring(5, 2), 16);
        }

        if (a < 255)
        {
            // CSS #RGBA carries alpha. Theme palettes are intentionally opaque,
            // so composite against the same dark base used to build app surfaces.
            r = CompositeChannel(r, BaseBackground.R, a);
            g = CompositeChannel(g, BaseBackground.G, a);
            b = CompositeChannel(b, BaseBackground.B, a);
        }

        normalized = $"#{r:X2}{g:X2}{b:X2}";
        return true;
    }

    private static byte ExpandNibble(char value)
    {
        int nibble = value <= '9'
            ? value - '0'
            : char.ToUpperInvariant(value) - 'A' + 10;
        return (byte)(nibble * 17);
    }

    private static byte CompositeChannel(byte foreground, byte background, byte alpha)
    {
        double weight = alpha / 255d;
        return (byte)Math.Round(
            foreground * weight + background * (1d - weight),
            MidpointRounding.AwayFromZero);
    }

    private static ThemePalette CreateCustomPalette(AppThemeColor themeColor, string normalizedColor)
    {
        var primary = ParseHexColor(normalizedColor);
        var onPrimary = BestContrastingText(primary);
        var secondary = Blend(primary, Colors.White, RelativeLuminance(primary) > 0.72 ? 0.12 : 0.24);
        var hoverTarget = RelativeLuminance(onPrimary) > 0.5 ? Colors.Black : Colors.White;
        var hover = Blend(primary, hoverTarget, 0.16);
        return Create(themeColor, primary, secondary, hover);
    }

    private static ThemePalette Create(
        AppThemeColor themeColor,
        Color primary,
        Color secondary,
        Color hover)
    {
        // Keep the established dark visual language, but tint each semantic surface
        // with the selected accent. Increasing tint with elevation gives the whole
        // application a theme identity without sacrificing text readability.
        var background = Blend(BaseBackground, primary, BackgroundTintWeight);
        var surface = Blend(BaseSurface, primary, SurfaceTintWeight);
        var surfaceElevated = Blend(BaseSurfaceElevated, primary, ElevatedSurfaceTintWeight);

        var onPrimary = BestContrastingText(primary);
        var accessibleHover = ResolveAccessibleHover(hover, secondary, onPrimary);

        return new ThemePalette(
            themeColor,
            background,
            surface,
            surfaceElevated,
            primary,
            secondary,
            accessibleHover,
            PrimaryText,
            MutedText,
            Blend(surfaceElevated, primary, 0.48),
            onPrimary);
    }

    private static Color ResolveAccessibleHover(Color preferredHover, Color secondary, Color foreground)
    {
        if (ContrastRatio(preferredHover, foreground) >= MinimumTextContrast)
            return preferredHover;

        // The existing secondary colors are intentionally lighter companions to
        // the primary accent and are a better hover fallback than changing text
        // color between pointer states.
        if (ContrastRatio(secondary, foreground) >= MinimumTextContrast)
            return secondary;

        // Defensive fallback for future/custom palettes: move the candidate toward
        // the luminance extreme that increases contrast while preserving as much hue
        // as possible.
        var target = RelativeLuminance(foreground) > 0.5 ? Colors.Black : Colors.White;
        for (double weight = 0.10; weight <= 1.0; weight += 0.10)
        {
            var candidate = Blend(preferredHover, target, weight);
            if (ContrastRatio(candidate, foreground) >= MinimumTextContrast)
                return candidate;
        }

        return target;
    }

    private static Color BestContrastingText(Color background)
    {
        var light = Colors.White;
        var dark = Color.FromRgb(15, 23, 42);
        double lightContrast = ContrastRatio(background, light);
        double darkContrast = ContrastRatio(background, dark);
        if (Math.Max(lightContrast, darkContrast) >= MinimumTextContrast)
            return lightContrast >= darkContrast ? light : dark;

        // Mid-luminance custom colors can sit in the narrow range where neither
        // white nor the brand navy reaches AA. Pure black is the safe fallback.
        return lightContrast >= ContrastRatio(background, Colors.Black) ? light : Colors.Black;
    }

    private static double ContrastRatio(Color first, Color second)
    {
        double lighter = Math.Max(RelativeLuminance(first), RelativeLuminance(second));
        double darker = Math.Min(RelativeLuminance(first), RelativeLuminance(second));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color color)
        => 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);

    private static double Linear(byte channel)
    {
        double value = channel / 255d;
        return value <= 0.04045
            ? value / 12.92
            : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static Color ParseHexColor(string value)
    {
        if (value.Length != 7 || value[0] != '#')
            return Color.FromRgb(59, 130, 246);

        return Color.FromRgb(
            Convert.ToByte(value.Substring(1, 2), 16),
            Convert.ToByte(value.Substring(3, 2), 16),
            Convert.ToByte(value.Substring(5, 2), 16));
    }

    private static Color Blend(Color baseColor, Color accent, double accentWeight)
    {
        double baseWeight = 1d - accentWeight;
        return Color.FromRgb(
            (byte)Math.Round(baseColor.R * baseWeight + accent.R * accentWeight),
            (byte)Math.Round(baseColor.G * baseWeight + accent.G * accentWeight),
            (byte)Math.Round(baseColor.B * baseWeight + accent.B * accentWeight));
    }

    private static void ApplyResources(ThemePalette palette)
    {
        var resources = Application.Current?.Resources;
        if (resources == null)
            return;

        resources["ThemeBackgroundBrush"] = CreateBrush(palette.Background);
        resources["ThemeSurfaceBrush"] = CreateBrush(palette.Surface);
        resources["ThemeSurfaceElevatedBrush"] = CreateBrush(palette.SurfaceElevated);
        resources["ThemePrimaryBrush"] = CreateBrush(palette.Primary);
        resources["ThemeSecondaryBrush"] = CreateBrush(palette.Secondary);
        resources["ThemeHoverBrush"] = CreateBrush(palette.Hover);
        resources["ThemeTextBrush"] = CreateBrush(palette.Text);
        resources["ThemeMutedTextBrush"] = CreateBrush(palette.MutedText);
        resources["ThemeBorderBrush"] = CreateBrush(palette.Border);
        resources["ThemeOnPrimaryBrush"] = CreateBrush(palette.OnPrimary);
    }

    private static ThemeWebState ToWebState(ThemePalette palette, string? customColor)
        => new(
            customColor is null ? palette.Key : "custom",
            new ThemeWebPalette(
                ToHex(palette.Background),
                ToHex(palette.Surface),
                ToHex(palette.SurfaceElevated),
                ToHex(palette.Primary),
                ToHex(palette.Secondary),
                ToHex(palette.Hover),
                ToHex(palette.Text),
                ToHex(palette.MutedText),
                ToHex(palette.Border),
                ToHex(palette.OnPrimary)),
            customColor);

    private static SolidColorBrush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
