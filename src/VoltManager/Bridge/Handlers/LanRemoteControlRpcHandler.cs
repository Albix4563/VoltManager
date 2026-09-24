using System.Text.Json;
using VoltManager.Bridge.Rpc;
using VoltManager.Models;
using VoltManager.Services.LanRemote;

namespace VoltManager.Bridge.Handlers;

public sealed record LanRemoteControlRpcActions(
    Func<LanRemoteControlState> GetState,
    Func<bool, CancellationToken, Task<LanRemoteEnableResult>> SetEnabled,
    Func<bool, bool, bool, LanRemoteControlState> SetPermissions,
    Func<string> GeneratePin,
    Func<string, LanRemoteControlState> SetPin);

public sealed class LanRemoteControlRpcHandler : IBridgeRpcHandler
{
    public static readonly string[] MethodNames =
    [
        "getLanRemoteControlState",
        "setLanRemoteControlEnabled",
        "setLanRemoteControlPermissions",
        "generateLanRemoteControlPin",
        "setLanRemoteControlPin",
    ];

    private readonly LanRemoteControlRpcActions _actions;

    public LanRemoteControlRpcHandler(LanRemoteControlRpcActions actions)
        => _actions = actions;

    public IReadOnlyCollection<string> Methods => MethodNames;

    public async Task<object?> HandleAsync(string method, JsonElement payload, CancellationToken cancellationToken)
        => method switch
        {
            "getLanRemoteControlState" => _actions.GetState(),
            "setLanRemoteControlEnabled" => await _actions.SetEnabled(
                BridgePayload.RequiredBoolean(payload, "enabled", "Invalid LAN remote-control enabled flag."),
                cancellationToken).ConfigureAwait(false),
            "setLanRemoteControlPermissions" => _actions.SetPermissions(
                BridgePayload.RequiredBoolean(payload, "allowPlanChange", "Invalid plan-change permission."),
                BridgePayload.RequiredBoolean(payload, "allowShutdown", "Invalid shutdown permission."),
                BridgePayload.RequiredBoolean(payload, "allowRestart", "Invalid restart permission.")),
            "generateLanRemoteControlPin" => new { pin = _actions.GeneratePin(), state = _actions.GetState() },
            "setLanRemoteControlPin" => _actions.SetPin(
                BridgePayload.RequiredString(payload, "pin", "Invalid LAN remote-control PIN.")),
            _ => throw new InvalidOperationException("Unsupported LAN remote-control RPC method: " + method),
        };
}
