using System.Text.Json;

namespace VoltManager.Bridge.Rpc;

public static class BridgePayload
{
    public static string RequiredString(JsonElement payload, string propertyName, string errorMessage)
    {
        JsonElement value = RequiredProperty(payload, propertyName, errorMessage);
        if (value.ValueKind != JsonValueKind.String)
            throw new ArgumentException(errorMessage);
        return value.GetString() ?? throw new ArgumentException(errorMessage);
    }

    public static bool RequiredBoolean(JsonElement payload, string propertyName, string errorMessage)
    {
        JsonElement value = RequiredProperty(payload, propertyName, errorMessage);
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException(errorMessage);
        return value.GetBoolean();
    }

    public static int RequiredInt32(JsonElement payload, string propertyName, string errorMessage)
    {
        JsonElement value = RequiredProperty(payload, propertyName, errorMessage);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result))
            throw new ArgumentException(errorMessage);
        return result;
    }

    public static int OptionalInt32(JsonElement payload, string propertyName, int defaultValue)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out int result))
        {
            return defaultValue;
        }

        return result;
    }

    public static T Deserialize<T>(JsonElement payload, string errorMessage)
    {
        try
        {
            return payload.Deserialize<T>(BridgeRpc.JsonOpts)
                ?? throw new ArgumentException(errorMessage);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException(errorMessage, ex);
        }
    }

    private static JsonElement RequiredProperty(JsonElement payload, string propertyName, string errorMessage)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(propertyName, out JsonElement value))
        {
            throw new ArgumentException(errorMessage);
        }

        return value;
    }
}
