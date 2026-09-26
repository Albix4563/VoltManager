using System.Text.Json.Serialization;

namespace VoltManager.Models;

public class WidgetItem
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    // Off until the user (or installer) explicitly enables a widget type.
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = false;
    [JsonPropertyName("pinned")] public bool Pinned { get; set; } = false;
    [JsonPropertyName("size")] public string Size { get; set; } = "medium";
    // null means the setting predates persisted launcher orientation and still needs migration.
    [JsonPropertyName("orientation")] public string? Orientation { get; set; }
    [JsonPropertyName("width")] public double? Width { get; set; }
    [JsonPropertyName("height")] public double? Height { get; set; }
    [JsonPropertyName("x")] public double? X { get; set; }
    [JsonPropertyName("y")] public double? Y { get; set; }
    [JsonPropertyName("monitorId")] public string? MonitorId { get; set; }
    [JsonPropertyName("monitorName")] public string? MonitorName { get; set; }
    [JsonPropertyName("monitorNumber")] public int? MonitorNumber { get; set; }
    // null = legacy item not yet migrated to anchor/offset placement.
    [JsonPropertyName("anchor")] public string? Anchor { get; set; }
    [JsonPropertyName("offsetX")] public double OffsetX { get; set; }
    [JsonPropertyName("offsetY")] public double OffsetY { get; set; }
}

public class WidgetSettings
{
    public static readonly string[] Types =
    [
        "clock", "calendar", "usage", "temps", "power", "plans",
        "launcher", "apps", "actions", "brightness", "processes", "memory",
    ];
    public static readonly string[] Sizes = ["mini", "medium", "large"];
    public static readonly string[] LauncherSizes = Sizes;
    public static readonly string[] Orientations = ["horizontal", "vertical"];
    public static readonly string[] Anchors =
    [
        "topLeft", "topCenter", "topRight",
        "middleLeft", "center", "middleRight",
        "bottomLeft", "bottomCenter", "bottomRight",
    ];

    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = false;
    [JsonPropertyName("items")] public List<WidgetItem> Items { get; set; } = DefaultItems();

    public static List<WidgetItem> DefaultItems() => Types.Select(t => new WidgetItem { Type = t }).ToList();

    public static bool IsKnownType(string? type)
        => Types.Contains(type ?? "", StringComparer.OrdinalIgnoreCase);

    public static bool IsKnownAnchor(string? anchor)
        => Anchors.Contains(anchor ?? "", StringComparer.OrdinalIgnoreCase);

    public static string NormalizeSize(string? size)
        => Sizes.FirstOrDefault(s => string.Equals(s, size, StringComparison.OrdinalIgnoreCase)) ?? "medium";

    public static string NormalizeSize(string? type, string? size)
        => Sizes.FirstOrDefault(s => string.Equals(s, size, StringComparison.OrdinalIgnoreCase)) ?? "medium";

    public static bool IsLauncherType(string? type)
        => string.Equals(type, "launcher", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "apps", StringComparison.OrdinalIgnoreCase);

    public static string NormalizeOrientation(string? orientation)
        => Orientations.FirstOrDefault(o => string.Equals(o, orientation, StringComparison.OrdinalIgnoreCase)) ?? "horizontal";

    public static double LauncherThickness(string? size) => NormalizeSize(size) switch
    {
        "mini" => 58,
        "large" => 82,
        _ => 66,
    };

    public static double LauncherIconSize(string? size) => NormalizeSize(size) switch
    {
        "mini" => 32,
        "large" => 52,
        _ => 40,
    };

    public static double LauncherSlotLength(string? size) => NormalizeSize(size) switch
    {
        "mini" => 44,
        "large" => 68,
        _ => 52,
    };

    // Along the bar axis: 34 DIP leading cap + 6+6 DIP body padding + 14 DIP resize cap + 2 DIP border.
    public const double LauncherChromeLength = 62;

    public static double LauncherMinLength(string? size) => LauncherSlotLength(size) * 3 + LauncherChromeLength;
    public static double LauncherDefaultLength(string? size) => LauncherSlotLength(size) * 6 + LauncherChromeLength;
    public static double LauncherMaxLength(string? size) => LauncherSlotLength(size) * 10 + LauncherChromeLength;

    public static string NormalizeAnchor(string? anchor)
        => Anchors.FirstOrDefault(a => string.Equals(a, anchor, StringComparison.OrdinalIgnoreCase)) ?? "topRight";

    public void Normalize()
    {
        Items ??= new List<WidgetItem>();

        var byType = new Dictionary<string, WidgetItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Items)
        {
            if (item == null || !IsKnownType(item.Type)) continue;
            item.Type = Types.First(t => string.Equals(t, item.Type, StringComparison.OrdinalIgnoreCase));
            if (IsLauncherType(item.Type))
            {
                string legacySize = item.Size ?? "medium";
                bool legacyPresetSize = Sizes.Contains(legacySize, StringComparer.OrdinalIgnoreCase);
                if (string.Equals(legacySize, "bar", StringComparison.OrdinalIgnoreCase))
                {
                    item.Orientation = "horizontal";
                    item.Size = "medium";
                }
                else if (string.Equals(legacySize, "column", StringComparison.OrdinalIgnoreCase))
                {
                    item.Orientation = "vertical";
                    item.Size = "medium";
                }
                else
                {
                    item.Size = NormalizeSize(item.Type, legacySize);
                    if (legacyPresetSize && item.Orientation == null &&
                        item.Width is double legacyWidth && double.IsFinite(legacyWidth) &&
                        item.Height is double legacyHeight && double.IsFinite(legacyHeight))
                    {
                        item.Orientation = legacyHeight > legacyWidth ? "vertical" : "horizontal";
                    }
                    else
                    {
                        item.Orientation = NormalizeOrientation(item.Orientation);
                    }
                }

                string orientation = NormalizeOrientation(item.Orientation);
                item.Orientation = orientation;
                double? length = orientation == "vertical" ? item.Height : item.Width;
                length = NormalizeDimension(length, LauncherMinLength(item.Size), LauncherMaxLength(item.Size));
                if (orientation == "vertical")
                {
                    item.Width = null;
                    item.Height = length;
                }
                else
                {
                    item.Width = length;
                    item.Height = null;
                }
            }
            else
            {
                item.Size = NormalizeSize(item.Type, item.Size);
                item.Orientation = "horizontal";
                item.Width = null;
                item.Height = null;
            }
            if (double.IsNaN(item.X ?? 0) || double.IsInfinity(item.X ?? 0)) item.X = null;
            if (double.IsNaN(item.Y ?? 0) || double.IsInfinity(item.Y ?? 0)) item.Y = null;
            if (item.Anchor != null) item.Anchor = NormalizeAnchor(item.Anchor);
            if (!double.IsFinite(item.OffsetX)) item.OffsetX = 0;
            if (!double.IsFinite(item.OffsetY)) item.OffsetY = 0;
            if (item.MonitorNumber is <= 0) item.MonitorNumber = null;
            item.MonitorId = string.IsNullOrWhiteSpace(item.MonitorId) ? null : item.MonitorId.Trim();
            item.MonitorName = string.IsNullOrWhiteSpace(item.MonitorName) ? null : item.MonitorName.Trim();
            byType.TryAdd(item.Type, item);
        }

        Items = Types.Select(t => byType.TryGetValue(t, out var item) ? item : new WidgetItem { Type = t }).ToList();
    }

    public WidgetItem GetOrAdd(string type)
    {
        Normalize();
        var item = Items.FirstOrDefault(i => string.Equals(i.Type, type, StringComparison.OrdinalIgnoreCase));
        if (item != null) return item;

        item = new WidgetItem { Type = type };
        Items.Add(item);
        Normalize();
        return Items.First(i => string.Equals(i.Type, type, StringComparison.OrdinalIgnoreCase));
    }

    private static double? NormalizeDimension(double? value, double min, double max)
    {
        if (value is not double number || !double.IsFinite(number)) return null;
        return Math.Clamp(number, min, max);
    }
}
