using System;

namespace VoltManager.Models
{
    public sealed class AppThemeColorPaletteDefinition
    {
        internal AppThemeColorPaletteDefinition(
            AppThemeColor themeColor,
            string key,
            string primary,
            string secondary,
            string hover)
        {
            ThemeColor = themeColor;
            Key = key;
            Primary = primary;
            Secondary = secondary;
            Hover = hover;
        }

        public AppThemeColor ThemeColor { get; }
        public string Key { get; }
        public string Primary { get; }
        public string Secondary { get; }
        public string Hover { get; }
    }

    public static class AppThemeColorPalette
    {
        private static readonly AppThemeColorPaletteDefinition Blue =
            new AppThemeColorPaletteDefinition(AppThemeColor.Blue, "blue", "#3B82F6", "#60A5FA", "#2563EB");
        private static readonly AppThemeColorPaletteDefinition Red =
            new AppThemeColorPaletteDefinition(AppThemeColor.Red, "red", "#EF4444", "#F87171", "#DC2626");
        private static readonly AppThemeColorPaletteDefinition Green =
            new AppThemeColorPaletteDefinition(AppThemeColor.Green, "green", "#22C55E", "#4ADE80", "#16A34A");
        private static readonly AppThemeColorPaletteDefinition Orange =
            new AppThemeColorPaletteDefinition(AppThemeColor.Orange, "orange", "#F97316", "#FB923C", "#EA580C");
        private static readonly AppThemeColorPaletteDefinition Purple =
            new AppThemeColorPaletteDefinition(AppThemeColor.Purple, "purple", "#A855F7", "#C084FC", "#9333EA");
        private static readonly AppThemeColorPaletteDefinition Pink =
            new AppThemeColorPaletteDefinition(AppThemeColor.Pink, "pink", "#EC4899", "#F472B6", "#DB2777");
        private static readonly AppThemeColorPaletteDefinition Gray =
            new AppThemeColorPaletteDefinition(AppThemeColor.Gray, "gray", "#94A3B8", "#CBD5E1", "#64748B");

        public static bool TryParseKey(string value, out AppThemeColor themeColor)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "blue":
                    themeColor = AppThemeColor.Blue;
                    return true;
                case "red":
                    themeColor = AppThemeColor.Red;
                    return true;
                case "green":
                    themeColor = AppThemeColor.Green;
                    return true;
                case "orange":
                    themeColor = AppThemeColor.Orange;
                    return true;
                case "purple":
                    themeColor = AppThemeColor.Purple;
                    return true;
                case "pink":
                    themeColor = AppThemeColor.Pink;
                    return true;
                case "gray":
                    themeColor = AppThemeColor.Gray;
                    return true;
                default:
                    themeColor = AppThemeColor.Blue;
                    return false;
            }
        }

        public static AppThemeColorPaletteDefinition Get(AppThemeColor themeColor)
        {
            switch (themeColor)
            {
                case AppThemeColor.Red:
                    return Red;
                case AppThemeColor.Green:
                    return Green;
                case AppThemeColor.Orange:
                    return Orange;
                case AppThemeColor.Purple:
                    return Purple;
                case AppThemeColor.Pink:
                    return Pink;
                case AppThemeColor.Gray:
                    return Gray;
                default:
                    return Blue;
            }
        }
    }
}
