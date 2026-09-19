using System.IO;
using System.Text.Json;
using VoltManager.Bridge.Rpc;

namespace VoltManager.Bridge.Handlers;

public sealed class MonitoringRpcHandler : IBridgeRpcHandler
{
    private static readonly string[] RegisteredMethods =
    [
        "getSystemInfo", "getHeavyAppStatus", "refreshHeavyAppDetection",
        "getAppPowerProfileStatus", "pickAppPowerProfileExecutable",
        "getTopProcesses", "getMemoryStatus", "purgeStandbyList",
        "openLogFolder", "exportDiagnostics",
    ];

    private readonly MonitoringRpcActions _actions;
    private readonly IBridgeFileDialogService _dialogs;

    public MonitoringRpcHandler(MonitoringRpcActions actions, IBridgeFileDialogService dialogs)
    {
        _actions = actions;
        _dialogs = dialogs;
    }

    public IReadOnlyCollection<string> Methods => RegisteredMethods;

    public async Task<object?> HandleAsync(string method, JsonElement payload, CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "getSystemInfo":
                return await Task.Run(_actions.GetSystemInfo, cancellationToken);
            case "getHeavyAppStatus":
                return await Task.Run(_actions.GetHeavyAppStatus, cancellationToken);
            case "refreshHeavyAppDetection":
                return await Task.Run(_actions.RefreshHeavyAppDetection, cancellationToken);
            case "getAppPowerProfileStatus":
                return await Task.Run(_actions.GetAppPowerProfileStatus, cancellationToken);
            case "pickAppPowerProfileExecutable":
                return new { path = await _actions.PickAppPowerProfileExecutable(cancellationToken) };
            case "getTopProcesses":
            {
                int count = BridgePayload.OptionalInt32(payload, "count", 8);
                return await Task.Run(() => _actions.GetTopProcesses(count), cancellationToken);
            }
            case "getMemoryStatus":
                return await Task.Run(_actions.GetMemoryStatus, cancellationToken);
            case "purgeStandbyList":
            {
                bool purged = await Task.Run(_actions.PurgeStandbyList, cancellationToken);
                object memory = await Task.Run(_actions.GetMemoryStatus, cancellationToken);
                return new { success = purged, memory };
            }
            case "openLogFolder":
            {
                var state = _actions.OpenLogFolder();
                return new { success = state.success, path = state.path, error = state.error };
            }
            case "exportDiagnostics":
                return await ExportDiagnosticsAsync(cancellationToken);
            default:
                throw new ArgumentException($"Handler cannot process RPC method '{method}'.");
        }
    }

    private async Task<object> ExportDiagnosticsAsync(CancellationToken cancellationToken)
    {
        string? path = await _dialogs.SaveFileAsync(
            new BridgeSaveFileRequest(
                "Export diagnostics",
                "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                $"voltmanager-diagnostics-{_actions.LocalNow():yyyyMMdd-HHmm}.txt"),
            cancellationToken);
        if (path == null)
            return new { success = false, cancelled = true };

        string report = await Task.Run(_actions.BuildDiagnosticsReport, cancellationToken);
        await File.WriteAllTextAsync(path, report, cancellationToken);
        return new { success = true, path, bytes = report.Length };
    }
}
