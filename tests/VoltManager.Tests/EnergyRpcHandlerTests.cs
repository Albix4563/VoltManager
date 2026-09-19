using System.Text.Json;
using VoltManager.Bridge;
using VoltManager.Bridge.Handlers;
using VoltManager.Bridge.Rpc;
using VoltManager.Localization;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public class EnergyRpcHandlerTests
{
    private sealed class FakeDialogs : IBridgeFileDialogService
    {
        public Task<string?> SaveFileAsync(BridgeSaveFileRequest request, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);
        public Task<string?> OpenFileAsync(BridgeOpenFileRequest request, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);
    }

    private static JsonElement Payload(object value)
        => JsonSerializer.SerializeToElement(value, BridgeRpc.JsonOpts);

    [Fact]
    public void Methods_match_energy_contract()
    {
        var handler = Create();
        string[] expected =
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
        Assert.Equal(expected.OrderBy(x => x), handler.Methods.OrderBy(x => x));
    }

    [Fact]
    public async Task GetBatteryHistory_defaults_to_48_hour_window()
    {
        DateTime now = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
        long inside = new DateTimeOffset(now.AddHours(-47), TimeSpan.Zero).ToUnixTimeSeconds();
        long outside = new DateTimeOffset(now.AddHours(-49), TimeSpan.Zero).ToUnixTimeSeconds();
        var handler = Create(
            now: () => now,
            getHistory: () =>
            [
                new BatteryHistorySample { T = outside, Pct = 20 },
                new BatteryHistorySample { T = inside, Pct = 30 },
            ]);

        object? result = await handler.HandleAsync("getBatteryHistory", default, CancellationToken.None);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result, BridgeRpc.JsonOpts));
        JsonElement samples = doc.RootElement.GetProperty("samples");
        Assert.Single(samples.EnumerateArray());
        Assert.Equal(30, samples[0].GetProperty("pct").GetInt32());
    }

    [Fact]
    public async Task SetKeepAwakeSafety_preserves_omitted_battery_option()
    {
        SettingsService settings = TestSettings.Create();
        settings.Update(s =>
        {
            s.KeepAwake.AutoDisableOnBattery = true;
            s.KeepAwake.MaxMinutes = 90;
        });
        (bool battery, int minutes)? received = null;
        var handler = Create(settings: settings, setKeepAwakeSafety: (battery, minutes) =>
        {
            received = (battery, minutes);
            return new KeepAwakeState { AutoDisableOnBattery = battery, MaxMinutes = minutes };
        });

        await handler.HandleAsync("setKeepAwakeSafety", Payload(new { maxMinutes = 30 }), CancellationToken.None);

        Assert.Equal((true, 30), received);
    }

    [Fact]
    public async Task SchedulePowerAction_relative_passes_delay_and_action()
    {
        (TimeSpan delay, ScheduledPowerActionType action)? received = null;
        var handler = Create(scheduleAfter: (delay, action) =>
        {
            received = (delay, action);
            return new ScheduledPowerActionState { Enabled = true, Action = action };
        });

        await handler.HandleAsync("schedulePowerAction",
            Payload(new { mode = "relative", action = "shutdown", delayMinutes = 45 }),
            CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(45), received?.delay);
        Assert.Equal(ScheduledPowerActionType.Shutdown, received?.action);
    }

    [Fact]
    public async Task SetManualOverride_rejects_unknown_plan_before_callback()
    {
        int calls = 0;
        var handler = Create(setManualOverride: (_, _) =>
        {
            calls++;
            return true;
        });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.HandleAsync("setManualOverride", Payload(new { plan = "not-a-plan" }), CancellationToken.None));
        Assert.Equal(0, calls);
    }

    private static EnergyRpcHandler Create(
        SettingsService? settings = null,
        Func<DateTime>? now = null,
        Func<IReadOnlyList<BatteryHistorySample>>? getHistory = null,
        Func<bool, int, object>? setKeepAwakeSafety = null,
        Func<TimeSpan, ScheduledPowerActionType, object>? scheduleAfter = null,
        Func<PlanId, TimeSpan?, bool>? setManualOverride = null)
    {
        settings ??= TestSettings.Create();
        var actions = CreateActions(settings, now, getHistory, setKeepAwakeSafety, scheduleAfter, setManualOverride);
        return new EnergyRpcHandler(settings, new LocalizationService(), actions, new FakeDialogs());
    }

    private static EnergyRpcActions CreateActions(
        SettingsService settings,
        Func<DateTime>? now,
        Func<IReadOnlyList<BatteryHistorySample>>? getHistory,
        Func<bool, int, object>? setKeepAwakeSafety,
        Func<TimeSpan, ScheduledPowerActionType, object>? scheduleAfter,
        Func<PlanId, TimeSpan?, bool>? setManualOverride)
        => new(
            GetBatteryHealth: () => new BatteryHealthState { Available = true },
            GetBatteryPower: () => new BatteryPowerState { Available = true },
            GetBatteryHistory: getHistory ?? (() => Array.Empty<BatteryHistorySample>()),
            CheckDefaultPlans: () => (true, new List<PlanId>()),
            RestoreDefaultPlans: () => true,
            GetActivePlan: () => new { name = "Balanced" },
            GetActivePlanReason: () => new { reason = "manual" },
            GetPlanHistory: () => new { revision = 1L },
            ClearPlanHistory: () => 2L,
            ListPowerPlans: () => Array.Empty<object>(),
            GetKeepAwakeState: () => new KeepAwakeState(),
            SetKeepAwake: enabled => new KeepAwakeState { Enabled = enabled },
            SetKeepAwakeSafety: setKeepAwakeSafety ?? ((battery, minutes) =>
                new KeepAwakeState { AutoDisableOnBattery = battery, MaxMinutes = minutes }),
            GetCpuAutomationState: () => new { enabled = true },
            SetManualOverride: setManualOverride ?? ((_, _) => true),
            ClearManualOverride: () => { },
            GetManualOverride: () => settings.Current.Override,
            GetPowerSourcePlanState: () => new { enabled = true },
            SetPowerSourcePlanSwitch: enabled => new { enabled },
            GetThermalGuardState: () => new { enabled = false },
            SetThermalGuardEnabled: enabled => new { enabled },
            SetThermalGuardSettings: raw => raw,
            GetIdlePowerGuardState: () => new { enabled = false },
            SetIdlePowerGuardEnabled: enabled => new { enabled },
            SetIdlePowerGuardSettings: raw => raw,
            GetScheduledPowerAction: () => new ScheduledPowerActionState(),
            ExecutePowerAction: _ => { },
            ScheduleAfter: scheduleAfter ?? ((delay, action) =>
                new ScheduledPowerActionState { Enabled = true, Action = action, DelayMinutes = (int)delay.TotalMinutes }),
            ScheduleDaily: (time, action) =>
                new ScheduledPowerActionState { Enabled = true, Action = action, DailyTime = time.ToString("HH:mm") },
            CancelScheduledPowerAction: () => new ScheduledPowerActionState(),
            GetPlanTimeouts: _ => new { },
            GetPlanParameters: _ => new { },
            SetPlanParameter: (_, _, _, _) => true,
            UtcNow: now ?? (() => new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc)));
}
