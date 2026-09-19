namespace VoltManager.Bridge.Handlers;

public sealed record MonitoringRpcActions(
    Func<object> GetSystemInfo,
    Func<object> GetHeavyAppStatus,
    Func<object> RefreshHeavyAppDetection,
    Func<object> GetAppPowerProfileStatus,
    Func<CancellationToken, Task<string?>> PickAppPowerProfileExecutable,
    Func<int, object> GetTopProcesses,
    Func<object> GetMemoryStatus,
    Func<bool> PurgeStandbyList,
    Func<(bool success, string? path, string? error)> OpenLogFolder,
    Func<string> BuildDiagnosticsReport,
    Func<DateTime> LocalNow);
