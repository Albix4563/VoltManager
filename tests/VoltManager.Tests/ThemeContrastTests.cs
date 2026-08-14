using System.IO;
using System.Windows.Media;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public class ThemeContrastTests
{
    private const double MinimumTextContrast = 4.5;

    [Fact]
    public void Tray_menu_uses_complete_native_style_instead_of_partial_theme_overrides()
    {
        string appXaml = LocateAppXaml();

        Assert.DoesNotContain("<Style TargetType=\"{x:Type ContextMenu}\">", appXaml);
        Assert.DoesNotContain("<Style TargetType=\"{x:Type MenuItem}\">", appXaml);
        Assert.DoesNotContain("<Style TargetType=\"{x:Type Separator}\">", appXaml);
    }

    [Fact]
    public void Accent_text_meets_contrast_for_primary_and_hover_in_every_theme()
    {
        foreach (var theme in Enum.GetValues<AppThemeColor>())
        {
            var palette = ThemeService.GetPalette(theme);

            AssertContrast(theme, "Primary/OnPrimary", palette.Primary, palette.OnPrimary);
            AssertContrast(theme, "Hover/OnPrimary", palette.Hover, palette.OnPrimary);
        }
    }

    [Fact]
    public void Surface_text_meets_contrast_in_every_theme()
    {
        foreach (var theme in Enum.GetValues<AppThemeColor>())
        {
            var palette = ThemeService.GetPalette(theme);

            AssertContrast(theme, "Background/Text", palette.Background, palette.Text);
            AssertContrast(theme, "Surface/Text", palette.Surface, palette.Text);
            AssertContrast(theme, "SurfaceElevated/Text", palette.SurfaceElevated, palette.Text);
            AssertContrast(theme, "Background/MutedText", palette.Background, palette.MutedText);
            AssertContrast(theme, "Surface/MutedText", palette.Surface, palette.MutedText);
            AssertContrast(theme, "SurfaceElevated/MutedText", palette.SurfaceElevated, palette.MutedText);
        }
    }

    [Fact]
    public void Every_theme_has_distinct_tinted_surfaces()
    {
        var palettes = Enum.GetValues<AppThemeColor>()
            .Select(ThemeService.GetPalette)
            .ToArray();

        Assert.Equal(palettes.Length, palettes.Select(p => p.Background).Distinct().Count());
        Assert.Equal(palettes.Length, palettes.Select(p => p.Surface).Distinct().Count());
        Assert.Equal(palettes.Length, palettes.Select(p => p.SurfaceElevated).Distinct().Count());
    }

    [Fact]
    public void Web_theme_bridges_legacy_material_tokens_to_the_active_palette()
    {
        string css = LocateWebAsset("css", "theme-colors.css");

        string[] expectedMappings =
        {
            "--md-sys-color-background: var(--vm-bg);",
            "--md-sys-color-surface: var(--vm-surface);",
            "--md-sys-color-surface-container-low: var(--vm-surface-low);",
            "--md-sys-color-surface-container-high: var(--vm-surface-high);",
            "--md-sys-color-on-surface: var(--vm-text);",
            "--md-sys-color-on-surface-variant: var(--vm-muted);",
            "--md-sys-color-outline: var(--vm-border);",
            "--md-sys-color-secondary-container: var(--vm-accent);",
            "--md-sys-color-on-secondary-container: var(--vm-on-accent);",
        };

        foreach (string mapping in expectedMappings)
            Assert.Contains(mapping, css);
    }

    [Fact]
    public void Theme_runtime_updates_legacy_material_tokens_with_each_palette()
    {
        string js = LocateWebAsset("js", "theme.js");

        string[] expectedRuntimeTokens =
        {
            "root.setProperty('--md-sys-color-background', palette.background);",
            "root.setProperty('--md-sys-color-surface-container-low', palette.surface);",
            "root.setProperty('--md-sys-color-surface-container-high', palette.surfaceElevated);",
            "root.setProperty('--md-sys-color-on-surface', palette.text);",
            "root.setProperty('--md-sys-color-on-surface-variant', palette.mutedText);",
            "root.setProperty('--md-sys-color-outline', palette.border);",
            "root.setProperty('--md-sys-color-secondary-container', palette.primary);",
            "root.setProperty('--md-sys-color-on-secondary-container', palette.onPrimary);",
        };

        foreach (string token in expectedRuntimeTokens)
            Assert.Contains(token, js);
    }

    [Fact]
    public void Theme_css_owns_every_known_legacy_blue_surface()
    {
        string css = LocateWebAsset("css", "theme-colors.css");

        // These selectors cover the reorganized navigation shown in the reported
        // regression plus legacy/dynamic surfaces that previously kept navy/cyan
        // fills after switching away from the Blue theme.
        string[] expectedSelectors =
        {
            ".vm-subnav,",
            ".pm-subnav {",
            ".pm-seg.active {",
            ".desktop-widget {",
            ".startup-summary-card {",
            ".startup-card {",
            ".app-profile-panel,",
            "#power-plan-conflict-toast {",
            ".adv-col-dc {",
            "#lang-select,",
            "#font-select,",
            "#welcome-lang-select {",
        };

        foreach (string selector in expectedSelectors)
            Assert.Contains(selector, css);
    }

    private static void AssertContrast(
        AppThemeColor theme,
        string pair,
        Color background,
        Color foreground)
    {
        double ratio = ContrastRatio(background, foreground);
        Assert.True(
            ratio >= MinimumTextContrast,
            $"{theme}: contrasto {ratio:F2}:1 per {pair}; minimo richiesto {MinimumTextContrast:F1}:1");
    }

    private static string LocateAppXaml()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null)
        {
            string candidate = Path.Combine(directory, "src", "VoltManager", "App.xaml");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new FileNotFoundException("Could not locate src/VoltManager/App.xaml");
    }

    private static string LocateWebAsset(params string[] pathParts)
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null)
        {
            string candidate = Path.Combine(
                new[] { directory, "src", "VoltManager", "wwwroot" }.Concat(pathParts).ToArray());
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new FileNotFoundException(
            "Could not locate src/VoltManager/wwwroot/" + string.Join('/', pathParts));
    }

    private static double ContrastRatio(Color first, Color second)
    {
        double lighter = Math.Max(Luminance(first), Luminance(second));
        double darker = Math.Min(Luminance(first), Luminance(second));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Luminance(Color color)
        => 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);

    private static double Linear(byte channel)
    {
        double value = channel / 255d;
        return value <= 0.04045
            ? value / 12.92
            : Math.Pow((value + 0.055) / 1.055, 2.4);
    }
}
