using System.Text.Json;
using VoltManager.Bridge.Rpc;
using VoltManager.Localization;

namespace VoltManager.Bridge.Handlers;

public sealed class ApplicationRpcHandler : IBridgeRpcHandler
{
    private static readonly string[] RegisteredMethods =
    [
        "getGamingMode", "setGamingMode", "getStartupApps", "pickStartupExecutable",
        "addStartupApp", "setStartupAppEnabled", "removeStartupApp", "logError",
        "openExternal", "exitApp", "minimizeToTray", "showMainWindow",
    ];

    private readonly LocalizationService _loc;
    private readonly ApplicationRpcActions _actions;

    public ApplicationRpcHandler(LocalizationService loc, ApplicationRpcActions actions)
    {
        _loc = loc;
        _actions = actions;
    }

    public static IReadOnlyCollection<string> MethodNames => RegisteredMethods;
    public IReadOnlyCollection<string> Methods => MethodNames;

    public async Task<object?> HandleAsync(string method, JsonElement payload, CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "getGamingMode":
                return _actions.GetGamingMode();
            case "setGamingMode":
            {
                bool enabled = BridgePayload.RequiredBoolean(payload, "enabled", "Missing enabled");
                if (_actions.SetGamingMode == null)
                    throw new InvalidOperationException(_loc.T("Error_GamingControlUnavailable"));
                return await _actions.SetGamingMode(enabled);
            }
            case "getStartupApps":
                return await Task.Run(_actions.GetStartupApps, cancellationToken);
            case "pickStartupExecutable":
                return new { path = await _actions.PickStartupExecutable(cancellationToken) };
            case "addStartupApp":
            {
                string path = BridgePayload.RequiredString(payload, "path", _loc.T("Error_MissingPath"));
                object entry = await Task.Run(() => _actions.AddStartupApp(path), cancellationToken);
                return new { success = true, entry };
            }
            case "setStartupAppEnabled":
            {
                string id = BridgePayload.RequiredString(payload, "id", _loc.T("Error_MissingId"));
                bool enabled = BridgePayload.RequiredBoolean(payload, "enabled", "Missing enabled");
                bool changed = await Task.Run(
                    () => _actions.SetStartupAppEnabled(id, enabled), cancellationToken);
                return new { success = changed };
            }
            case "removeStartupApp":
            {
                string id = BridgePayload.RequiredString(payload, "id", _loc.T("Error_MissingId"));
                bool removed = await Task.Run(() => _actions.RemoveStartupApp(id), cancellationToken);
                return new { success = removed };
            }
            case "logError":
            {
                string message = OptionalString(payload, "message") ?? "";
                string? stack = OptionalString(payload, "stack");
                return BridgeRpc.HandleLogError(message, stack, _actions.LogError);
            }
            case "openExternal":
            {
                string url = BridgePayload.RequiredString(payload, "url", "Missing url");
                if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                {
                    _actions.OpenExternal(url);
                }
                return new { success = true };
            }
            case "exitApp":
                _actions.RequestExit();
                return new { success = true };
            case "minimizeToTray":
                _actions.RequestMinimize();
                return new { success = true };
            case "showMainWindow":
                _actions.ShowMainWindow();
                return new { success = true };
            default:
                throw new ArgumentException($"Handler cannot process RPC method '{method}'.");
        }
    }

    private static string? OptionalString(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        return value.GetString();
    }
}
