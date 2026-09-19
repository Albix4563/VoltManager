using System.Text.Json;
using VoltManager.Bridge.Rpc;
using VoltManager.Localization;

namespace VoltManager.Bridge.Handlers;

public sealed class UpdateRpcHandler : IBridgeRpcHandler
{
    private static readonly string[] RegisteredMethods =
    [
        "checkForUpdates", "getReleaseHistory", "downloadUpdate",
    ];

    private readonly LocalizationService _loc;
    private readonly UpdateRpcActions _actions;

    public UpdateRpcHandler(LocalizationService loc, UpdateRpcActions actions)
    {
        _loc = loc;
        _actions = actions;
    }

    public IReadOnlyCollection<string> Methods => RegisteredMethods;

    public async Task<object?> HandleAsync(string method, JsonElement payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return method switch
        {
            "checkForUpdates" => await _actions.CheckForUpdates(),
            "getReleaseHistory" => await _actions.GetReleaseHistory(),
            "downloadUpdate" => await DownloadUpdateAsync(
                BridgePayload.RequiredString(payload, "url", "URL mancante"),
                cancellationToken),
            _ => throw new ArgumentException($"Handler cannot process RPC method '{method}'."),
        };
    }

    private async Task<object> DownloadUpdateAsync(string url, CancellationToken cancellationToken)
    {
        if (_actions.IsHeavyAppSessionActive())
            return Deferred(url);

        string path = await _actions.DownloadUpdate(url);
        cancellationToken.ThrowIfCancellationRequested();
        if (_actions.IsHeavyAppSessionActive())
            return Deferred(url);

        _actions.LaunchInstaller(
            path,
            $"/update --pid {Environment.ProcessId} --lang {_loc.CurrentLanguage}");
        _actions.RequestExit();
        return new { success = true };
    }

    private object Deferred(string url)
    {
        _actions.DeferUpdateUntilGameEnds(url);
        return new
        {
            success = false,
            deferred = true,
            reason = "heavyAppActive",
            message = _loc.T("Dialog_UpdateDeferredGame"),
        };
    }
}
