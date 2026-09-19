using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using VoltManager.Bridge.Rpc;
using VoltManager.Localization;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Bridge.Handlers;

public sealed class EnergyRpcHandler : IBridgeRpcHandler
{
    private static readonly string[] RegisteredMethods =
    [
        "getBatteryHealth", "getBatteryPower", "getBatteryHistory", "exportBatteryHistory",
        "checkDefaultPlans", "restoreDefaultPlans", "getActivePlan", "getActivePlanReason",
        "getPlanHistory", "clearPlanHistory", "listPowerPlans", "getKeepAwakeState",
        "setKeepAwake", "setKeepAwakeSafety", "getCpuAutomationState", "setManualOverride",
        "clearManualOverride", "getPowerSourcePlanState", "setPowerSourcePlanSwitch",
        "getThermalGuardState", "setThermalGuardEnabled", "setThermalGuardSettings",
        "getIdlePowerGuardState", "setIdlePowerGuardEnabled", "setIdlePowerGuardSettings",
        "getScheduledPowerAction", "executePowerAction", "schedulePowerAction",
        "cancelScheduledPowerAction", "getPlanTimeouts", "getPlanParameters", "setPlanParameter",
    ];

    private readonly SettingsService _settings;
    private readonly LocalizationService _loc;
    private readonly EnergyRpcActions _actions;
    private readonly IBridgeFileDialogService _dialogs;

    public EnergyRpcHandler(
        SettingsService settings,
        LocalizationService loc,
        EnergyRpcActions actions,
        IBridgeFileDialogService dialogs)
    {
        _settings = settings;
        _loc = loc;
        _actions = actions;
        _dialogs = dialogs;
    }

    public IReadOnlyCollection<string> Methods => RegisteredMethods;

    public async Task<object?> HandleAsync(string method, JsonElement payload, CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "getBatteryHealth":
                return await Task.Run(_actions.GetBatteryHealth, cancellationToken);
            case "getBatteryPower":
                return await Task.Run(_actions.GetBatteryPower, cancellationToken);
            case "getBatteryHistory":
                return await GetBatteryHistoryAsync(payload, cancellationToken);
            case "exportBatteryHistory":
                return await ExportBatteryHistoryAsync(cancellationToken);
            case "checkDefaultPlans":
            {
                var state = await Task.Run(_actions.CheckDefaultPlans, cancellationToken);
                return new { allPresent = state.allPresent, missing = state.missing.Select(m => m.ToString()).ToList() };
            }
            case "restoreDefaultPlans":
                return new { success = await Task.Run(_actions.RestoreDefaultPlans, cancellationToken) };
            case "getActivePlan":
                return await Task.Run(_actions.GetActivePlan, cancellationToken);
            case "getActivePlanReason":
                return _actions.GetActivePlanReason();
            case "getPlanHistory":
                return _actions.GetPlanHistory();
            case "clearPlanHistory":
                return new { revision = _actions.ClearPlanHistory() };
            case "listPowerPlans":
                return await Task.Run(_actions.ListPowerPlans, cancellationToken);
            case "getKeepAwakeState":
                return _actions.GetKeepAwakeState();
            case "setKeepAwake":
                return _actions.SetKeepAwake(
                    BridgePayload.RequiredBoolean(payload, "enabled", "Missing enabled"));
            case "setKeepAwakeSafety":
                return await SetKeepAwakeSafetyAsync(payload, cancellationToken);
            case "getCpuAutomationState":
                return _actions.GetCpuAutomationState();
            case "setManualOverride":
                return await SetManualOverrideAsync(payload, cancellationToken);
            case "clearManualOverride":
                await Task.Run(_actions.ClearManualOverride, cancellationToken);
                return new { success = true, @override = _actions.GetManualOverride() };
            case "getPowerSourcePlanState":
                return await Task.Run(_actions.GetPowerSourcePlanState, cancellationToken);
            case "setPowerSourcePlanSwitch":
            {
                bool enabled = BridgePayload.RequiredBoolean(payload, "enabled", "Missing enabled");
                return await Task.Run(() => _actions.SetPowerSourcePlanSwitch(enabled), cancellationToken);
            }
            case "getThermalGuardState":
                return await Task.Run(_actions.GetThermalGuardState, cancellationToken);
            case "setThermalGuardEnabled":
            {
                bool enabled = BridgePayload.RequiredBoolean(payload, "enabled", "Missing enabled");
                return await Task.Run(() => _actions.SetThermalGuardEnabled(enabled), cancellationToken);
            }
            case "setThermalGuardSettings":
            {
                ThermalGuardSettings raw = BridgePayload.Deserialize<ThermalGuardSettings>(
                    payload, "Invalid thermal guard settings");
                return await Task.Run(() => _actions.SetThermalGuardSettings(raw), cancellationToken);
            }
            case "getIdlePowerGuardState":
                return await Task.Run(_actions.GetIdlePowerGuardState, cancellationToken);
            case "setIdlePowerGuardEnabled":
            {
                bool enabled = BridgePayload.RequiredBoolean(payload, "enabled", "Missing enabled");
                return await Task.Run(() => _actions.SetIdlePowerGuardEnabled(enabled), cancellationToken);
            }
            case "setIdlePowerGuardSettings":
            {
                IdlePowerGuardSettings raw = BridgePayload.Deserialize<IdlePowerGuardSettings>(
                    payload, "Invalid idle power guard settings");
                return await Task.Run(() => _actions.SetIdlePowerGuardSettings(raw), cancellationToken);
            }
            case "getScheduledPowerAction":
                return _actions.GetScheduledPowerAction();
            case "executePowerAction":
                return ExecutePowerAction(payload);
            case "schedulePowerAction":
                return SchedulePowerAction(payload);
            case "cancelScheduledPowerAction":
                return _actions.CancelScheduledPowerAction();
            case "getPlanTimeouts":
                return await Task.Run(() => _actions.GetPlanTimeouts(OptionalString(payload, "planGuid")), cancellationToken);
            case "getPlanParameters":
                return await Task.Run(() => _actions.GetPlanParameters(OptionalString(payload, "planGuid")), cancellationToken);
            case "setPlanParameter":
                return await SetPlanParameterAsync(payload, cancellationToken);
            default:
                throw new ArgumentException($"Handler cannot process RPC method '{method}'.");
        }
    }

    private async Task<object> GetBatteryHistoryAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        int hours = BridgePayload.OptionalInt32(payload, "hours", 48);
        IReadOnlyList<BatteryHistorySample> all =
            await Task.Run(_actions.GetBatteryHistory, cancellationToken);
        return new { samples = BatteryHistoryService.SelectWindow(all, _actions.UtcNow(), hours) };
    }

    private async Task<object> SetKeepAwakeSafetyAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        KeepAwakeSettings current = _settings.Current.KeepAwake ?? new KeepAwakeSettings();
        bool autoOffBattery = current.AutoDisableOnBattery;
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("autoDisableOnBattery", out JsonElement battery)
            && battery.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            autoOffBattery = battery.GetBoolean();
        }

        int maxMinutes = current.MaxMinutes;
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("maxMinutes", out JsonElement max)
            && max.TryGetInt32(out int parsed))
        {
            maxMinutes = parsed;
        }

        return await Task.Run(() => _actions.SetKeepAwakeSafety(autoOffBattery, maxMinutes), cancellationToken);
    }

    private async Task<object> SetManualOverrideAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        string planText = BridgePayload.RequiredString(
            payload, "plan", _loc.T("Error_UnknownPlan", ""));
        if (!Enum.TryParse(planText, true, out PlanId plan))
            throw new ArgumentException(_loc.T("Error_UnknownPlan", planText));

        TimeSpan? duration = null;
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("hours", out JsonElement hours)
            && hours.ValueKind == JsonValueKind.Number
            && hours.TryGetDouble(out double parsed))
        {
            duration = TimeSpan.FromHours(parsed);
        }

        bool success = await Task.Run(
            () => _actions.SetManualOverride(plan, duration), cancellationToken);
        return new { success, @override = _actions.GetManualOverride() };
    }

    private object ExecutePowerAction(JsonElement payload)
    {
        string actionText = BridgePayload.RequiredString(
            payload, "action", _loc.T("Error_InvalidPowerAction"));
        if (!Enum.TryParse(actionText, true, out ScheduledPowerActionType action)
            || action is not (ScheduledPowerActionType.Shutdown or ScheduledPowerActionType.Restart))
        {
            throw new ArgumentException(_loc.T("Error_InvalidPowerAction"));
        }

        _actions.ExecutePowerAction(action);
        return new { success = true };
    }

    private object SchedulePowerAction(JsonElement payload)
    {
        string modeText = BridgePayload.RequiredString(
            payload, "mode", _loc.T("Error_InvalidScheduleMode"));
        string actionText = BridgePayload.RequiredString(
            payload, "action", _loc.T("Error_InvalidPowerAction"));
        if (!Enum.TryParse(actionText, true, out ScheduledPowerActionType action))
            throw new ArgumentException(_loc.T("Error_InvalidPowerAction"));

        if (string.Equals(modeText, "relative", StringComparison.OrdinalIgnoreCase))
        {
            int delayMinutes = BridgePayload.RequiredInt32(
                payload, "delayMinutes", _loc.T("Error_InvalidScheduleMode"));
            return _actions.ScheduleAfter(TimeSpan.FromMinutes(delayMinutes), action);
        }

        if (string.Equals(modeText, "daily", StringComparison.OrdinalIgnoreCase))
        {
            string timeText = BridgePayload.RequiredString(
                payload, "time", _loc.T("Error_InvalidPowerTime"));
            if (!TimeOnly.TryParseExact(
                    timeText, "HH:mm", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out TimeOnly time))
            {
                throw new ArgumentException(_loc.T("Error_InvalidPowerTime"));
            }

            return _actions.ScheduleDaily(time, action);
        }

        throw new ArgumentException(_loc.T("Error_InvalidScheduleMode"));
    }

    private async Task<object> SetPlanParameterAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        string planGuid = BridgePayload.RequiredString(
            payload, "planGuid", _loc.T("Error_MissingPlanGuid"));
        string settingKey = BridgePayload.RequiredString(
            payload, "settingKey", _loc.T("Error_MissingSettingKey"));
        int acValue = BridgePayload.RequiredInt32(payload, "acValue", "Missing acValue");
        int dcValue = BridgePayload.RequiredInt32(payload, "dcValue", "Missing dcValue");
        bool success = await Task.Run(
            () => _actions.SetPlanParameter(planGuid, settingKey, acValue, dcValue),
            cancellationToken);
        return new { success };
    }

    private async Task<object> ExportBatteryHistoryAsync(CancellationToken cancellationToken)
    {
        DateTime localNow = _actions.UtcNow().ToLocalTime();
        string? path = await _dialogs.SaveFileAsync(
            new BridgeSaveFileRequest(
                "Export battery history",
                "CSV (*.csv)|*.csv|All files (*.*)|*.*",
                $"voltmanager-battery-history-{localNow:yyyyMMdd-HHmm}.csv"),
            cancellationToken);
        if (path == null)
            return new { success = false, cancelled = true };

        IReadOnlyList<BatteryHistorySample> history =
            await Task.Run(_actions.GetBatteryHistory, cancellationToken);
        string csv = BatteryHistoryService.ToCsv(history);
        await File.WriteAllTextAsync(path, csv, Encoding.UTF8, cancellationToken);
        return new { success = true, path };
    }

    private static string? OptionalString(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }
}
