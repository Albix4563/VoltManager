using System.Text.Json;
using VoltManager.Bridge.Rpc;
using VoltManager.Models;

namespace VoltManager.Bridge.Handlers;

public sealed record WidgetRpcActions(
    Func<object> GetState,
    Func<string, bool, object> SetEnabled,
    Func<bool, object> SetMasterEnabled,
    Func<string, bool, object> SetPinned,
    Func<string, string, object> SetSize,
    Func<string, string, object> SetOrientation,
    Func<string, string, string, object> SetPlacement,
    Func<WidgetAppearance, object> SetAppearance,
    Func<string, WidgetAppearance?, object> SetAppearanceOverride,
    Func<string, object> ResetPosition,
    Action BeginDrag,
    Action BeginResize,
    Action<bool> SetTopmost,
    Action Close);

public sealed class WidgetRpcHandler : IBridgeRpcHandler
{
    private static readonly string[] RegisteredMethods =
    [
        "beginWidgetDrag", "beginWidgetResize", "setWidgetTopmost", "closeWidget",
        "getWidgetsState", "setWidgetEnabled", "setWidgetsMaster",
        "setWidgetPinned", "setWidgetSize", "setWidgetOrientation", "setWidgetPlacement",
        "setWidgetAppearance", "setWidgetAppearanceOverride",
        "resetWidgetPosition",
    ];

    private readonly WidgetRpcActions _actions;

    public WidgetRpcHandler(WidgetRpcActions actions) => _actions = actions;

    public static IReadOnlyCollection<string> MethodNames => RegisteredMethods;
    public IReadOnlyCollection<string> Methods => MethodNames;

    public Task<object?> HandleAsync(string method, JsonElement payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        object? result = method switch
        {
            "beginWidgetDrag" => Invoke(_actions.BeginDrag),
            "beginWidgetResize" => Invoke(_actions.BeginResize),
            "setWidgetTopmost" => SetTopmost(payload),
            "closeWidget" => Invoke(_actions.Close),
            "getWidgetsState" => _actions.GetState(),
            "setWidgetEnabled" => _actions.SetEnabled(
                BridgePayload.RequiredString(payload, "type", "Missing widget type"),
                BridgePayload.RequiredBoolean(payload, "enabled", "Missing enabled")),
            "setWidgetsMaster" => _actions.SetMasterEnabled(
                BridgePayload.RequiredBoolean(payload, "enabled", "Missing enabled")),
            "setWidgetPinned" => _actions.SetPinned(
                BridgePayload.RequiredString(payload, "type", "Missing widget type"),
                BridgePayload.RequiredBoolean(payload, "pinned", "Missing pinned")),
            "setWidgetSize" => _actions.SetSize(
                BridgePayload.RequiredString(payload, "type", "Missing widget type"),
                RequiredStringOrDefault(payload, "size", "medium")),
            "setWidgetOrientation" => _actions.SetOrientation(
                BridgePayload.RequiredString(payload, "type", "Missing widget type"),
                BridgePayload.RequiredString(payload, "orientation", "Missing widget orientation")),
            "setWidgetPlacement" => _actions.SetPlacement(
                BridgePayload.RequiredString(payload, "type", "Missing widget type"),
                BridgePayload.RequiredString(payload, "monitorId", "Missing monitor id"),
                BridgePayload.RequiredString(payload, "anchor", "Missing anchor")),
            "setWidgetAppearance" => _actions.SetAppearance(
                BridgePayload.Deserialize<WidgetAppearance>(payload, "Invalid widget appearance")),
            "setWidgetAppearanceOverride" => _actions.SetAppearanceOverride(
                BridgePayload.RequiredString(payload, "type", "Missing widget type"),
                ReadAppearanceOverride(payload)),
            "resetWidgetPosition" => _actions.ResetPosition(
                BridgePayload.RequiredString(payload, "type", "Missing widget type")),
            _ => throw new ArgumentException($"Handler cannot process RPC method '{method}'."),
        };
        return Task.FromResult<object?>(result);
    }

    private object SetTopmost(JsonElement payload)
    {
        bool topmost = BridgePayload.RequiredBoolean(payload, "topmost", "Missing topmost");
        _actions.SetTopmost(topmost);
        return new { success = true, topmost };
    }

    private static object Invoke(Action action)
    {
        action();
        return new { success = true };
    }

    private static string RequiredStringOrDefault(JsonElement payload, string name, string defaultValue)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"Missing {name}");
        if (!payload.TryGetProperty(name, out JsonElement value))
            return defaultValue;
        if (value.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"Invalid {name}");
        return value.GetString() ?? defaultValue;
    }

    private static WidgetAppearance? ReadAppearanceOverride(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("appearance", out JsonElement value))
            throw new ArgumentException("Missing widget appearance override");
        if (value.ValueKind == JsonValueKind.Null) return null;
        return BridgePayload.Deserialize<WidgetAppearance>(value, "Invalid widget appearance override");
    }
}
