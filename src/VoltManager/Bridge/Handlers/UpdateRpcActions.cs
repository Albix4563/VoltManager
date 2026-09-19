using VoltManager.Models;

namespace VoltManager.Bridge.Handlers;

public sealed record UpdateRpcActions(
    Func<Task<UpdateInfo>> CheckForUpdates,
    Func<Task<ReleaseHistory>> GetReleaseHistory,
    Func<string, Task<string>> DownloadUpdate,
    Func<bool> IsHeavyAppSessionActive,
    Action<string> DeferUpdateUntilGameEnds,
    Action<string, string> LaunchInstaller,
    Action RequestExit);
