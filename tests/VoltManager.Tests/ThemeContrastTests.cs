using System.IO;
using System.Windows.Media;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public class ThemeContrastTests
{
    [Fact]
    public void Tray_menu_uses_complete_native_style_instead_of_partial_theme_overrides()
    {
        string appXaml = LocateAppXaml();

        Assert.DoesNotContain("<Style TargetType=\"{x:Type ContextMenu}\">", appXaml);
        Assert.DoesNotContain("<Style TargetType=\"{x:Type MenuItem}\">", appXaml);
        Assert.DoesNotContain("<Style TargetType=\"{x:Type Separator}\">", appXaml);
    }

    [Fact]
    public void Highlighted_menu_text_meets_contrast_for_every_theme()
    {
        foreach (var theme in Enum.GetValues<AppThemeColor>())
        {
            var palette = ThemeService.GetPalette(theme);
            double ratio = ContrastRatio(palette.Hover, palette.OnPrimary);

            Assert.True(ratio >= 4.5,
                $"{theme}: contrasto {ratio:F2}:1 tra Hover e OnPrimary");
        }
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
