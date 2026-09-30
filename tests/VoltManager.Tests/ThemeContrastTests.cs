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

    [Theory]
    [InlineData("#abc", "#AABBCC")]
    [InlineData(" #123456 ", "#123456")]
    [InlineData("#F008", "#8D080F")]
    public void Custom_theme_color_is_normalized_to_opaque_rrggbb(string input, string expected)
    {
        Assert.True(ThemeService.TryNormalizeCustomColor(input, out string normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("#12")]
    [InlineData("#12345")]
    [InlineData("#GGG")]
    [InlineData("rgb(1,2,3)")]
    public void Invalid_custom_theme_color_is_rejected(string input)
    {
        Assert.False(ThemeService.TryNormalizeCustomColor(input, out _));
    }

    [Theory]
    [InlineData("#000000")]
    [InlineData("#777777")]
    [InlineData("#FFFFFF")]
    [InlineData("#0F0")]
    public void Custom_theme_accent_and_hover_keep_accessible_on_accent_text(string input)
    {
        var palette = ThemeService.GetPalette(AppThemeColor.Blue, input);
        AssertContrast(AppThemeColor.Blue, "Custom Primary/OnPrimary", palette.Primary, palette.OnPrimary);
        AssertContrast(AppThemeColor.Blue, "Custom Hover/OnPrimary", palette.Hover, palette.OnPrimary);
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
            ".segmented-control-bg {",
            ".toggle-label-large {",
            ".mini-toggle {",
            ".desktop-widget {",
            ".startup-summary-card {",
            ".startup-card {",
            ".app-profile-panel {",
            "#power-plan-conflict-toast {",
            ".adv-col-dc {",
            ".processes-card {",
            ".process-row {",
            ".process-rank {",
            ".process-meter {",
            "#update-status.ok {",
            "#lang-select,",
            "#font-select,",
            "#welcome-lang-select {",
        };

        foreach (string selector in expectedSelectors)
            Assert.Contains(selector, css);
    }

    [Fact]
    public void Sub_navigation_surfaces_resolve_through_theme_owned_variables()
    {
        string theme = LocateWebAsset("css", "theme-colors.css");
        string redesign = LocateWebAsset("css", "redesign.css");
        string reorganization = LocateWebAsset("css", "ui-reorganization.css");

        // The sub-navigations read palette-derived variables defined by the theme
        // instead of being re-coloured by !important overrides.
        foreach (string variable in new[]
                 {
                     "--vm-control-shell-bg:", "--vm-control-shell-border:",
                     "--vm-pm-subnav-bg:", "--vm-pm-subnav-border:",
                     "--vm-control-active-bg:", "--vm-control-active-border:",
                 })
            Assert.Contains(variable, theme);

        Assert.Matches(@"\.vm-subnav \{[^}]*background: var\(--vm-control-shell-bg\)", reorganization);
        Assert.Matches(@"\.pm-subnav\{[^}]*background: var\(--vm-pm-subnav-bg\)", redesign);
        Assert.Matches(@"\.pm-seg\.active\{[^}]*background: var\(--vm-control-active-bg\)", redesign);
    }

    [Fact]
    public void Widget_power_plan_override_does_not_use_prototype_navy_surfaces()
    {
        string css = LocateWebAsset("css", "widget-plan-override.css");

        Assert.Contains("var(--vm-bg)", css);
        Assert.Contains("var(--vm-surface-high)", css);
        Assert.Contains("var(--vm-surface)", css);
        Assert.DoesNotContain("rgba(34, 50, 86", css);
        Assert.DoesNotContain("rgba(14, 26, 46", css);
        Assert.DoesNotContain("rgba(5, 12, 24", css);
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
