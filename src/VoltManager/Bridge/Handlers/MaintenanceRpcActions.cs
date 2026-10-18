using VoltManager.Models;

namespace VoltManager.Bridge.Handlers;

public sealed record MaintenanceRpcActions(
    Func<CancellationToken, Task<MaintenanceAppsResult>> GetApps,
    Func<string, string?, CancellationToken, Task<MaintenanceOperation>> StartOperation,
    Func<string, MaintenanceOperation> GetOperationStatus,
    Func<string, MaintenanceOperation> CancelOperation,
    Func<string, CancellationToken, Task<MaintenanceOperation>> VerifyUninstall,
    Func<string, IReadOnlyList<string>, CancellationToken, Task<MaintenanceOperation>> ApplyOperation,
    Func<IReadOnlyList<MaintenanceBackupSummary>> GetBackups,
    Func<string, MaintenanceBackupDetail> GetBackup,
    Func<string, IReadOnlyList<string>, CancellationToken, Task<MaintenanceOperation>> RestoreBackup,
    Func<string, CancellationToken, Task<bool>> DeleteBackup,
    Func<bool> OpenAppsSettings,
    Func<string, IReadOnlyList<string>, bool> ExcludeItems
);
