using System.Windows.Media;

namespace VoltManager;

internal sealed record PromptPalette(
    SolidColorBrush Background,
    SolidColorBrush Surface,
    SolidColorBrush Text,
    SolidColorBrush Muted,
    SolidColorBrush Border,
    SolidColorBrush Accent,
    SolidColorBrush OnAccent,
    SolidColorBrush SubtleButton)
{
    public static PromptPalette For(string? theme)
    {
        bool light = string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase);
        return light
            ? new PromptPalette(
                Brush(246, 249, 252),
                Brush(255, 255, 255),
                Brush(16, 32, 51),
                Brush(82, 103, 125),
                Brush(194, 210, 222),
                Brush(0, 174, 187),
                Brush(0, 63, 70),
                Brush(238, 245, 248))
            : new PromptPalette(
                Brush(10, 17, 40),
                Brush(15, 26, 54),
                Brush(226, 232, 240),
                Brush(148, 163, 184),
                Brush(71, 85, 105),
                Brush(0, 241, 254),
                Brush(3, 7, 18),
                Brush(30, 42, 74));
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b)
        => new(Color.FromRgb(r, g, b));
}
