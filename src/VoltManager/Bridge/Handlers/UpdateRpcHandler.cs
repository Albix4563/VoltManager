using System.Text.Json;
using VoltManager.Bridge.Rpc;
using VoltManager.Localization;
using VoltManager.Services;

namespace VoltManager.Bridge.Handlers;

public sealed class UpdateRpcHandler : IBridgeRpcHandler
{
    private readonly SemaphoreSlim _installGate = new(1, 1);
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

    public static IReadOnlyCollection<string> MethodNames => RegisteredMethods;
    public IReadOnlyCollection<string> Methods => MethodNames;

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
        if (!_actions.IsDownloadUrlAllowed(url))
            throw new InvalidOperationException("URL aggiornamento non autorizzato.");

        // A second RPC must never start another installer while the first one awaits readiness.
        if (!await _installGate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("Un aggiornamento è già in preparazione.");
        try
        {
            if (_actions.IsHeavyAppSessionActive())
                return Deferred(url);

            using VerifiedUpdateDownload download = await _actions.DownloadUpdate(url, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_actions.IsHeavyAppSessionActive())
                return Deferred(url);

            await _actions.LaunchInstaller(
                download,
                $"/update --pid {Environment.ProcessId} --lang {_loc.CurrentLanguage}",
                cancellationToken);
            // The launcher has received Accepted. RPC cancellation can no longer
            // unwind the handoff: Setup is now waiting for the host to shut down.
            _actions.RequestExit();
            return new { success = true };
        }
        finally
        {
            _installGate.Release();
        }
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
