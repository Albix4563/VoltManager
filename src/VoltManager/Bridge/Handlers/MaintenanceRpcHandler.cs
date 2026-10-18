using System.Text.Json;
using VoltManager.Bridge.Rpc;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Bridge.Handlers;

public sealed class MaintenanceRpcHandler : IBridgeRpcHandler
{
    private const int MaxArrayItems = 1000;

    private static readonly string[] RegisteredMethods =
    [
        "maintenance.getApps",
        "maintenance.start",
        "maintenance.status",
        "maintenance.cancel",
        "maintenance.verifyUninstall",
        "maintenance.apply",
        "maintenance.getBackups",
        "maintenance.getBackup",
        "maintenance.restore",
        "maintenance.deleteBackup",
        "maintenance.openAppsSettings",
        "maintenance.exclude",
    ];

    private readonly MaintenanceRpcActions _actions;

    public MaintenanceRpcHandler(MaintenanceRpcActions actions)
    {
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
    }

    public MaintenanceRpcHandler(MaintenanceService service)
        : this(CreateActionsFromService(service))
    {
    }

    private static MaintenanceRpcActions CreateActionsFromService(MaintenanceService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return new MaintenanceRpcActions(
            service.GetAppsAsync,
            service.StartOperationAsync,
            service.GetOperationStatus,
            service.CancelOperation,
            service.VerifyUninstallAsync,
            service.ApplyOperationAsync,
            service.GetBackups,
            service.GetBackup,
            service.RestoreBackupAsync,
            service.DeleteBackupAsync,
            service.OpenAppsSettings,
            service.ExcludeItems);
    }

    public static IReadOnlyCollection<string> MethodNames => RegisteredMethods;
    public IReadOnlyCollection<string> Methods => MethodNames;

    public async Task<object?> HandleAsync(string method, JsonElement payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return method switch
        {
            "maintenance.getApps" => await _actions.GetApps(cancellationToken),

            "maintenance.start" => await _actions.StartOperation(
                BridgePayload.RequiredString(payload, "kind", "Missing kind"),
                payload.TryGetProperty("appId", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null,
                cancellationToken),

            "maintenance.status" => _actions.GetOperationStatus(
                BridgePayload.RequiredString(payload, "operationId", "Missing operationId")),

            "maintenance.cancel" => _actions.CancelOperation(
                BridgePayload.RequiredString(payload, "operationId", "Missing operationId")),

            "maintenance.verifyUninstall" => await _actions.VerifyUninstall(
                BridgePayload.RequiredString(payload, "operationId", "Missing operationId"),
                cancellationToken),

            "maintenance.apply" => await _actions.ApplyOperation(
                BridgePayload.RequiredString(payload, "operationId", "Missing operationId"),
                ExtractStringArray(payload, "itemIds"),
                cancellationToken),

            "maintenance.getBackups" => new
            {
                backups = _actions.GetBackups(),
            },

            "maintenance.getBackup" => _actions.GetBackup(
                BridgePayload.RequiredString(payload, "backupId", "Missing backupId")),

            "maintenance.restore" => await _actions.RestoreBackup(
                BridgePayload.RequiredString(payload, "backupId", "Missing backupId"),
                ExtractStringArray(payload, "itemIds"),
                cancellationToken),

            "maintenance.deleteBackup" => new
            {
                success = await _actions.DeleteBackup(
                    BridgePayload.RequiredString(payload, "backupId", "Missing backupId"),
                    cancellationToken),
            },

            "maintenance.openAppsSettings" => new
            {
                success = _actions.OpenAppsSettings(),
            },

            "maintenance.exclude" => new
            {
                success = _actions.ExcludeItems(
                    BridgePayload.RequiredString(payload, "operationId", "Missing operationId"),
                    ExtractStringArray(payload, "itemIds")),
            },

            _ => throw new ArgumentException($"Handler cannot process RPC method '{method}'."),
        };
    }

    internal static IReadOnlyList<string> ExtractStringArray(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(propertyName, out JsonElement arrayElement)
            || arrayElement.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException($"Missing or invalid array property '{propertyName}'.");
        }

        int count = arrayElement.GetArrayLength();
        if (count > MaxArrayItems)
        {
            throw new ArgumentException($"Array property '{propertyName}' exceeds maximum allowed limit of {MaxArrayItems} items.");
        }

        var unique = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>(count);

        foreach (JsonElement item in arrayElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException($"Array property '{propertyName}' contains invalid element of kind '{item.ValueKind}'.");
            }

            string? val = item.GetString();
            if (string.IsNullOrWhiteSpace(val))
            {
                throw new ArgumentException($"Array property '{propertyName}' contains empty or whitespace string.");
            }

            if (unique.Add(val))
            {
                list.Add(val);
            }
        }

        return list;
    }
}
