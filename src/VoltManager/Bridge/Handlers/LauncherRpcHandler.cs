using System.Text.Json;
using VoltManager.Bridge.Rpc;
using VoltManager.Models;

namespace VoltManager.Bridge.Handlers;

public sealed record LauncherRpcActions(
    Func<bool, CancellationToken, Task<IReadOnlyList<LauncherEntry>>> GetLaunchers,
    Func<string, CancellationToken, Task<LaunchResult>> Launch,
    Func<CancellationToken, Task<string?>> PickExecutable,
    Func<string, string, LauncherEntry> AddCustom,
    Func<string, bool> RemoveCustom,
    Func<string, bool, bool> SetHidden,
    Func<string, CancellationToken, Task<bool>> ConfirmRemoval);

public sealed class LauncherRpcHandler : IBridgeRpcHandler
{
    private static readonly string[] RegisteredMethods =
    [
        "getLaunchers", "refreshLaunchers", "launchApp",
        "addCustomLauncher", "removeCustomLauncher", "setLauncherHidden", "confirmLauncherRemoval",
    ];

    private readonly LauncherRpcActions _actions;

    public LauncherRpcHandler(LauncherRpcActions actions) => _actions = actions;

    public static IReadOnlyCollection<string> MethodNames => RegisteredMethods;
    public IReadOnlyCollection<string> Methods => MethodNames;

    public async Task<object?> HandleAsync(string method, JsonElement payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return method switch
        {
            "getLaunchers" => await _actions.GetLaunchers(false, cancellationToken),
            "refreshLaunchers" => await _actions.GetLaunchers(true, cancellationToken),
            "launchApp" => await _actions.Launch(
                BridgePayload.RequiredString(payload, "id", "Missing launcher id"),
                cancellationToken),
            "addCustomLauncher" => await AddPickedAsync(payload, cancellationToken),
            "removeCustomLauncher" => new
            {
                success = _actions.RemoveCustom(BridgePayload.RequiredString(payload, "id", "Missing launcher id")),
            },
            "setLauncherHidden" => new
            {
                success = _actions.SetHidden(
                    BridgePayload.RequiredString(payload, "id", "Missing launcher id"),
                    BridgePayload.RequiredBoolean(payload, "hidden", "Missing hidden")),
            },
            "confirmLauncherRemoval" => await ConfirmRemovalAsync(payload, cancellationToken),
            _ => throw new ArgumentException($"Handler cannot process RPC method '{method}'."),
        };
    }

    private async Task<object> ConfirmRemovalAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        string id = BridgePayload.RequiredString(payload, "id", "Missing launcher id");
        string message = BridgePayload.RequiredString(payload, "message", "Missing confirmation message");
        if (message.Length > 512)
            throw new ArgumentException("Confirmation message is too long.");

        IReadOnlyList<LauncherEntry> launchers = await _actions.GetLaunchers(false, cancellationToken);
        LauncherEntry? entry = launchers.FirstOrDefault(item =>
            string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase) && !item.Hidden);
        if (entry == null || entry.Source is not ("custom" or "detected"))
            return new { confirmed = false, success = false };

        bool confirmed = await _actions.ConfirmRemoval(message, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!confirmed)
            return new { confirmed = false, success = false };

        bool success = string.Equals(entry.Source, "custom", StringComparison.Ordinal)
            ? _actions.RemoveCustom(id)
            : _actions.SetHidden(id, true);
        return new { confirmed = true, success };
    }

    // The path always comes from the host file dialog, never from the page.
    private async Task<object> AddPickedAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        string? path = await _actions.PickExecutable(cancellationToken);
        if (string.IsNullOrWhiteSpace(path))
            return new { added = false };
        string category = "games";
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("category", out var categoryElement) &&
            categoryElement.ValueKind == JsonValueKind.String)
        {
            category = LauncherSettings.NormalizeCategory(categoryElement.GetString());
        }
        var entry = _actions.AddCustom(path, category);
        return new { added = true, category = entry.Category, entry };
    }
}
