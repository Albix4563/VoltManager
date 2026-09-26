using System.Text.Json.Serialization;

namespace VoltManager.Models;

public class WidgetItem
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    // Off until the user (or installer) explicitly enables a widget type.
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = false;
    [JsonPropertyName("pinned")] public bool Pinned { get; set; } = false;
    [JsonPropertyName("size")] public string Size { get; set; } = "medium";
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
    public static readonly string[] LauncherSizes = ["mini", "medium", "large", "bar", "column"];
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
    {
        string[] allowed = IsLauncherType(type) ? LauncherSizes : Sizes;
        return allowed.FirstOrDefault(s => string.Equals(s, size, StringComparison.OrdinalIgnoreCase)) ?? "medium";
    }

    public static bool IsLauncherType(string? type)
        => string.Equals(type, "launcher", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "apps", StringComparison.OrdinalIgnoreCase);

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
            item.Size = NormalizeSize(item.Type, item.Size);
            if (IsLauncherType(item.Type))
            {
                item.Width = NormalizeDimension(item.Width, 56, 1920);
                item.Height = NormalizeDimension(item.Height, 56, 1080);
            }
            else
            {
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
