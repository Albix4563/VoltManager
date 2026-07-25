using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoltManager.Models;

public static class AppThemeColorExtensions
{
    public static AppThemeColor Normalize(this AppThemeColor value)
        => Enum.IsDefined(value) ? value : AppThemeColor.Blue;

    public static string ToKey(this AppThemeColor value)
        => AppThemeColorPalette.Get(value.Normalize()).Key;

    public static bool TryParse(string? value, out AppThemeColor themeColor)
        => AppThemeColorPalette.TryParseKey(value ?? string.Empty, out themeColor);
}

public sealed class AppThemeColorJsonConverter : JsonConverter<AppThemeColor>
{
    public override AppThemeColor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && AppThemeColorExtensions.TryParse(reader.GetString(), out var value))
            return value;

        return AppThemeColor.Blue;
    }

    public override void Write(Utf8JsonWriter writer, AppThemeColor value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToKey());
}
