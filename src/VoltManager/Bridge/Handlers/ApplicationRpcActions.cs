namespace VoltManager.Bridge.Handlers;

public sealed record ApplicationRpcActions(
    Func<object> GetGamingMode,
    Func<bool, Task<object?>>? SetGamingMode,
    Func<object> GetStartupApps,
    Func<CancellationToken, Task<string?>> PickStartupExecutable,
    Func<string, object> AddStartupApp,
    Func<string, bool, bool> SetStartupAppEnabled,
    Func<string, bool> RemoveStartupApp,
    Action<string> LogError,
    Action<string> OpenExternal,
    Action RequestExit,
    Action RequestMinimize,
    Action ShowMainWindow);
