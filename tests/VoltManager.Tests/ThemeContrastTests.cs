using System.IO;
using System.Text.RegularExpressions;
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
        string css = LocateWebAsset("css", "tokens.css");

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
    public void Theme_runtime_updates_palette_sources_consumed_by_legacy_material_aliases()
    {
        string js = LocateWebAsset("js", "style-controller.js");
        string css = LocateWebAsset("css", "tokens.css");

        string[] expectedRuntimeTokens =
        {
            "'--vm-bg': 'background'",
            "'--vm-surface': 'surface'",
            "'--vm-surface-high': 'surfaceElevated'",
            "'--vm-text': 'text'",
            "'--vm-muted': 'mutedText'",
            "'--vm-border': 'border'",
            "'--vm-accent': 'primary'",
            "'--vm-on-accent': 'onPrimary'",
        };

        foreach (string token in expectedRuntimeTokens)
            Assert.Contains(token, js);

        Assert.Contains("--md-sys-color-background: var(--vm-bg);", css);
        Assert.Contains("--md-sys-color-surface-container-high: var(--vm-surface-high);", css);
        Assert.Contains("--md-sys-color-on-surface: var(--vm-text);", css);
        Assert.Contains("--md-sys-color-secondary-container: var(--vm-accent);", css);
        Assert.Contains("--md-sys-color-on-secondary-container: var(--vm-on-accent);", css);
    }

    [Theory]
    [InlineData(AppThemeColor.Blue)]
    [InlineData(AppThemeColor.Red)]
    [InlineData(AppThemeColor.Green)]
    [InlineData(AppThemeColor.Orange)]
    [InlineData(AppThemeColor.Purple)]
    [InlineData(AppThemeColor.Pink)]
    [InlineData(AppThemeColor.Gray)]
    public void Preset_css_tokens_and_semantic_aliases_match_native_palette(AppThemeColor theme)
    {
        string css = LocateWebAsset("css", "tokens.css");
        var palette = ThemeService.GetPalette(theme);
        var declarations = ParseCssDeclarations(ExtractCssBlock(css, ":root {"));

        foreach ((string name, string value) in ParseCssDeclarations(
                     ExtractCssBlock(css, $":root[data-theme=\"{theme.ToKey()}\"]")))
            declarations[name] = value;

        var expected = new Dictionary<string, string>
        {
            ["--vm-bg"] = ToCssHex(palette.Background),
            ["--vm-bg-deep"] = ToCssHex(palette.Background),
            ["--vm-surface"] = ToCssHex(palette.Surface),
            ["--vm-surface-low"] = ToCssHex(palette.Surface),
            ["--vm-panel"] = ToCssHex(palette.Surface),
            ["--vm-surface-high"] = ToCssHex(palette.SurfaceElevated),
            ["--vm-card"] = ToCssHex(palette.SurfaceElevated),
            ["--vm-accent"] = ToCssHex(palette.Primary),
            ["--vm-accent-dim"] = ToCssHex(palette.Secondary),
            ["--vm-border-strong"] = ToCssHex(palette.Secondary),
            ["--vm-accent-hover"] = ToCssHex(palette.Hover),
            ["--vm-text"] = ToCssHex(palette.Text),
            ["--vm-muted"] = ToCssHex(palette.MutedText),
            ["--vm-muted-soft"] = ToCssHex(palette.MutedText),
            ["--vm-border"] = ToCssHex(palette.Border),
            ["--vm-on-accent"] = ToCssHex(palette.OnPrimary),
            ["--vm-accent-text"] = ToCssHex(palette.OnPrimary),
            ["--md-sys-color-background"] = ToCssHex(palette.Background),
            ["--md-sys-color-surface"] = ToCssHex(palette.Surface),
            ["--md-sys-color-surface-container-lowest"] = ToCssHex(palette.Background),
            ["--md-sys-color-surface-container-low"] = ToCssHex(palette.Surface),
            ["--md-sys-color-surface-container"] = ToCssHex(palette.Surface),
            ["--md-sys-color-surface-container-high"] = ToCssHex(palette.SurfaceElevated),
            ["--md-sys-color-surface-container-highest"] = ToCssHex(palette.SurfaceElevated),
            ["--md-sys-color-on-surface"] = ToCssHex(palette.Text),
            ["--md-sys-color-on-surface-variant"] = ToCssHex(palette.MutedText),
            ["--md-sys-color-outline"] = ToCssHex(palette.Border),
            ["--md-sys-color-outline-variant"] = ToCssHex(palette.Border),
            ["--md-sys-color-secondary"] = ToCssHex(palette.Secondary),
            ["--md-sys-color-secondary-container"] = ToCssHex(palette.Primary),
            ["--md-sys-color-on-secondary-container"] = ToCssHex(palette.OnPrimary),
        };

        foreach ((string token, string expectedValue) in expected)
        {
            string actualValue = ResolveSimpleCssAlias(declarations, token);
            Assert.Equal(expectedValue, actualValue);
        }
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
        string theme = LocateWebAsset("css", "tokens.css");
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

    private static string ExtractCssBlock(string css, string selector)
    {
        int selectorIndex = css.IndexOf(selector, StringComparison.Ordinal);
        Assert.True(selectorIndex >= 0, $"CSS selector not found: {selector}");
        int blockStart = css.IndexOf('{', selectorIndex);
        Assert.True(blockStart >= 0, $"CSS block start not found: {selector}");
        int blockEnd = css.IndexOf('}', blockStart + 1);
        Assert.True(blockEnd >= 0, $"CSS block end not found: {selector}");
        return css[(blockStart + 1)..blockEnd];
    }

    private static Dictionary<string, string> ParseCssDeclarations(string block)
        => Regex.Matches(block, @"(?<name>--[A-Za-z0-9-]+)\s*:\s*(?<value>[^;]+);")
            .Cast<Match>()
            .ToDictionary(
                match => match.Groups["name"].Value,
                match => match.Groups["value"].Value.Trim(),
                StringComparer.Ordinal);

    private static string ResolveSimpleCssAlias(IReadOnlyDictionary<string, string> declarations, string token)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string current = token;
        while (visited.Add(current))
        {
            Assert.True(declarations.TryGetValue(current, out string? value), $"CSS token not found: {current}");
            Match alias = Regex.Match(value!, @"^var\((?<name>--[A-Za-z0-9-]+)\)$");
            if (!alias.Success)
                return value!.ToUpperInvariant();

            current = alias.Groups["name"].Value;
        }

        throw new InvalidOperationException($"Circular CSS alias detected from {token}");
    }

    private static string ToCssHex(Color color)
        => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

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
