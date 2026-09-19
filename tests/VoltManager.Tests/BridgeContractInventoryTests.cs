using VoltManager.Bridge;
using VoltManager.Bridge.Handlers;

namespace VoltManager.Tests;

public sealed class BridgeContractInventoryTests
{
    [Fact]
    public void RpcHandlers_RegisterExactPublicContract()
    {
        string[] expected =
        [
            "getSystemInfo", "getBatteryHealth", "getBatteryPower", "getBatteryHistory",
            "beginWidgetDrag", "setWidgetTopmost", "closeWidget", "getWidgetsState",
            "setWidgetEnabled", "setWidgetsMaster", "setWidgetPinned", "setWidgetSize",
            "setWidgetPlacement", "resetWidgetPosition", "checkDefaultPlans", "restoreDefaultPlans",
            "getActivePlan", "getActivePlanReason", "getPlanHistory", "clearPlanHistory",
            "listPowerPlans", "getKeepAwakeState", "setKeepAwake", "setKeepAwakeSafety",
            "getCpuAutomationState", "setManualOverride", "clearManualOverride", "getGamingMode",
            "setGamingMode", "getSettings", "setThemeColor", "saveSettings", "setLanguage",
            "setStartWithWindows", "setCloseToTray", "setAutoUpdateChecks", "setSilentAutoUpdates",
            "setUpdateChannel", "snoozeUpdate", "skipUpdateVersion", "getHeavyAppStatus",
            "refreshHeavyAppDetection", "getAppPowerProfileStatus", "getPowerSourcePlanState",
            "setPowerSourcePlanSwitch", "getThermalGuardState", "setThermalGuardEnabled",
            "setThermalGuardSettings", "getIdlePowerGuardState", "setIdlePowerGuardEnabled",
            "setIdlePowerGuardSettings", "pickAppPowerProfileExecutable", "getTopProcesses",
            "getStartupApps", "pickStartupExecutable", "addStartupApp", "setStartupAppEnabled",
            "removeStartupApp", "checkForUpdates", "getReleaseHistory", "downloadUpdate",
            "logError", "openExternal", "exitApp", "minimizeToTray", "getScheduledPowerAction",
            "executePowerAction", "schedulePowerAction", "cancelScheduledPowerAction",
            "getPlanTimeouts", "getPlanParameters", "setPlanParameter", "getMemoryStatus",
            "purgeStandbyList", "getStandbyAutoCleanSettings", "setStandbyAutoCleanSettings",
            "exportSettings", "exportBatteryHistory", "openLogFolder", "exportDiagnostics",
            "importSettings",
        ];

        string[] actual =
        [
            .. SettingsRpcHandler.MethodNames,
            .. WidgetRpcHandler.MethodNames,
            .. EnergyRpcHandler.MethodNames,
            .. MonitoringRpcHandler.MethodNames,
            .. UpdateRpcHandler.MethodNames,
            .. ApplicationRpcHandler.MethodNames,
        ];

        Assert.Equal(81, actual.Length);
        Assert.Equal(actual.Length, actual.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Events_ExposeExactPublicContract()
    {
        string[] expected =
        [
            "activePlanChanged", "activePlanReasonChanged", "appPowerProfileActivityChanged",
            "appUpdated", "automationStateChanged", "cpuAutomationStateChanged", "fontChanged",
            "gamingModeChanged", "globalHotkeysChanged", "heavyAppActivityChanged",
            "idlePowerGuardChanged", "keepAwakeChanged", "languageChanged", "manualOverrideChanged",
            "metrics", "planHistoryChanged", "powerPlanConflictDetected", "powerSourcePlanChanged",
            "resourceProfileChanged", "scheduledPowerActionChanged", "standbyAutoCleaned",
            "themeChanged", "thermalGuardChanged", "updateAvailable", "updateDownloadProgress",
            "widgetsStateChanged", "widgetTopmostChanged",
        ];

        Assert.Equal(27, BridgeEventNames.All.Count);
        Assert.Equal(expected.Order(StringComparer.Ordinal), BridgeEventNames.All.Order(StringComparer.Ordinal));
    }
}
