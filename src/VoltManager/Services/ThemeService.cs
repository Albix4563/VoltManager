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
    [property: JsonPropertyName("palette")] ThemeWebPalette Palette);

/// <summary>
/// Applies the selected color theme to WPF resources and exposes the same
/// centralized palette to WebView-based UI surfaces.
/// </summary>
public sealed class ThemeService
{
    public AppThemeColor CurrentTheme { get; private set; } = AppThemeColor.Blue;
    public ThemePalette CurrentPalette { get; private set; } = GetPalette(AppThemeColor.Blue);

    public event Action<AppThemeColor>? ThemeChanged;

    public ThemeService()
    {
        ApplyResources(CurrentPalette);
    }

    public void SetTheme(AppThemeColor themeColor)
    {
        var normalized = themeColor.Normalize();
        bool changed = normalized != CurrentTheme;

        CurrentTheme = normalized;
        CurrentPalette = GetPalette(normalized);
        ApplyResources(CurrentPalette);

        if (changed)
            ThemeChanged?.Invoke(CurrentTheme);
    }

    public ThemeWebState GetWebTheme()
        => ToWebState(CurrentPalette);

    public IReadOnlyDictionary<string, ThemeWebPalette> GetWebThemeCatalog()
        => Enum.GetValues<AppThemeColor>()
            .ToDictionary(
                color => color.ToKey(),
                color => ToWebState(GetPalette(color)).Palette,
                StringComparer.OrdinalIgnoreCase);

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

    private static ThemePalette Create(
        AppThemeColor themeColor,
        Color primary,
        Color secondary,
        Color hover)
    {
        var background = Color.FromRgb(11, 17, 32);
        var surface = Color.FromRgb(17, 24, 39);
        var surfaceElevated = Color.FromRgb(30, 41, 59);

        return new ThemePalette(
            themeColor,
            background,
            surface,
            surfaceElevated,
            primary,
            secondary,
            hover,
            Color.FromRgb(248, 250, 252),
            Color.FromRgb(203, 213, 225),
            Blend(surfaceElevated, primary, 0.48),
            Color.FromRgb(15, 23, 42));
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

    private static ThemeWebState ToWebState(ThemePalette palette)
        => new(
            palette.Key,
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
                ToHex(palette.OnPrimary)));

    private static SolidColorBrush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
