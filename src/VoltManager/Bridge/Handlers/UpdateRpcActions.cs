using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Bridge.Handlers;

public sealed record UpdateRpcActions(
    Func<Task<UpdateInfo>> CheckForUpdates,
    Func<Task<ReleaseHistory>> GetReleaseHistory,
    Func<string, bool> IsDownloadUrlAllowed,
    Func<string, CancellationToken, Task<VerifiedUpdateDownload>> DownloadUpdate,
    Func<bool> IsHeavyAppSessionActive,
    Action<string> DeferUpdateUntilGameEnds,
    Action<VerifiedUpdateDownload, string> LaunchInstaller,
    Action RequestExit);
