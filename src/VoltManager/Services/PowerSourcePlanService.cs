using System.Runtime.InteropServices;
using VoltManager.Models;

namespace VoltManager.Services;

public record PowerSourceSnapshot(bool? PluggedIn, int? BatteryPercent);

public record PowerSourcePlanDecision
{
    public PlanId? TargetPlan { get; init; }
    public bool BlocksLowerPriority { get; init; }
    public PowerSourcePlanState State { get; init; } = new();
}

public sealed class PowerSourcePlanService
{
    private const int LowBatteryThresholdPercent = 20;

    private readonly SettingsService _settings;
    private readonly Func<PowerSourceSnapshot?> _powerSourceReader;
    private readonly object _lock = new();

    private bool _acSessionActive;
    private PlanId? _planBeforeAcSession;
    private bool _lowBatterySessionActive;
    private PlanId? _planBeforeLowBatterySession;
    private PowerSourcePlanState _current = new();

    public event Action<PowerSourcePlanState>? StateChanged;

    public PowerSourcePlanService(SettingsService settings, Func<PowerSourceSnapshot?>? powerSourceReader = null)
    {
        _settings = settings;
        _powerSourceReader = powerSourceReader ?? GetSystemPowerSourceSnapshot;
        _current = BuildState(new PowerSourceSnapshot(null, null), null, false, "");
    }

    public PowerSourcePlanState Current
    {
        get { lock (_lock) return _current; }
    }

    public PowerSourcePlanDecision Evaluate(PlanId? activePlan, bool manualOverrideActive)
    {
        lock (_lock)
        {
            var cfg = EnsureSettings();
            var source = NormalizeSnapshot(_powerSourceReader());

            if (IsLowBatteryOnDc(source))
                return KeepLowBatterySaver(activePlan, source, manualOverrideActive);

            var planBeforeLowBattery = EndLowBatterySessionIfNeeded(source);
            if (_lowBatterySessionActive)
                return KeepLowBatterySaver(activePlan, source, manualOverrideActive);

            if (manualOverrideActive)
                return Decision(null, false, source, manualOverrideActive, "manual_override");

            if (!cfg.Enabled)
            {
                if (_acSessionActive || planBeforeLowBattery != null)
                {
                    var target = _planBeforeAcSession ?? planBeforeLowBattery ?? PlanId.Balanced;
                    ClearAcSession();
                    return Decision(activePlan == target ? null : target, true, source, manualOverrideActive, "disabled_restore");
                }

                return Decision(null, false, source, manualOverrideActive, "disabled");
            }

            if (source.PluggedIn == null)
                return Decision(null, false, source, false, "unknown_power_source");

            if (source.PluggedIn.Value)
            {
                if (!_acSessionActive)
                {
                    _planBeforeAcSession = planBeforeLowBattery ?? (activePlan == cfg.PluggedPlan ? PlanId.Balanced : activePlan);
                    _acSessionActive = true;
                }

                var target = activePlan == cfg.PluggedPlan ? (PlanId?)null : cfg.PluggedPlan;
                return Decision(target, true, source, false, target == null ? "plugged_active" : "plugged_switch");
            }

            if (_acSessionActive)
            {
                var target = _planBeforeAcSession ?? PlanId.Balanced;
                ClearAcSession();
                return Decision(activePlan == target ? null : target, true, source, false, "unplugged_restore");
            }

            if (planBeforeLowBattery != null)
                return Decision(activePlan == planBeforeLowBattery ? null : planBeforeLowBattery, true, source, false, "low_battery_restore");

            return Decision(null, false, source, false, "unplugged_idle");
        }
    }

    public PowerSourcePlanState RefreshState(bool manualOverrideActive)
    {
        lock (_lock)
        {
            var state = BuildState(NormalizeSnapshot(_powerSourceReader()), null, manualOverrideActive, "refresh");
            Publish(state);
            return state;
        }
    }

    public PowerSourcePlanState SetEnabled(bool enabled, bool manualOverrideActive)
    {
        _settings.Current.PowerSourcePlan ??= new PowerSourcePlanSettings();
        _settings.Current.PowerSourcePlan.Enabled = enabled;
        _settings.Save();
        return RefreshState(manualOverrideActive);
    }

    public void ClearSession()
    {
        ClearAcSession();
        ClearLowBatterySession();
    }

    private PowerSourcePlanSettings EnsureSettings()
    {
        _settings.Current.PowerSourcePlan ??= new PowerSourcePlanSettings();
        return _settings.Current.PowerSourcePlan;
    }

    private PowerSourcePlanDecision KeepLowBatterySaver(PlanId? activePlan, PowerSourceSnapshot source, bool manualOverrideActive)
    {
        if (!_lowBatterySessionActive)
        {
            _planBeforeLowBatterySession = activePlan == PlanId.PowerSaver ? PlanId.Balanced : activePlan;
            _lowBatterySessionActive = true;
            ClearAcSession();
        }

        var target = activePlan == PlanId.PowerSaver ? (PlanId?)null : PlanId.PowerSaver;
        return Decision(target, true, source, manualOverrideActive, target == null ? "low_battery_active" : "low_battery_switch");
    }

    private PlanId? EndLowBatterySessionIfNeeded(PowerSourceSnapshot source)
    {
        if (!_lowBatterySessionActive)
            return null;

        bool shouldEnd = source.PluggedIn == true
            || (source.PluggedIn == false && source.BatteryPercent is >= LowBatteryThresholdPercent);
        if (!shouldEnd)
            return null;

        var previous = _planBeforeLowBatterySession;
        ClearLowBatterySession();
        return previous;
    }

    private void ClearAcSession()
    {
        _acSessionActive = false;
        _planBeforeAcSession = null;
    }

    private void ClearLowBatterySession()
    {
        _lowBatterySessionActive = false;
        _planBeforeLowBatterySession = null;
    }

    private PowerSourcePlanDecision Decision(PlanId? targetPlan, bool blocksLowerPriority, PowerSourceSnapshot source,
        bool manualOverrideActive, string message)
    {
        var state = BuildState(source, targetPlan, manualOverrideActive, message);
        Publish(state);
        return new PowerSourcePlanDecision
        {
            TargetPlan = targetPlan,
            BlocksLowerPriority = blocksLowerPriority,
            State = state,
        };
    }

    private PowerSourcePlanState BuildState(PowerSourceSnapshot source, PlanId? targetPlan, bool manualOverrideActive, string message)
    {
        var cfg = EnsureSettings();
        return new PowerSourcePlanState
        {
            Enabled = cfg.Enabled,
            PowerSourceKnown = source.PluggedIn != null,
            PluggedIn = source.PluggedIn == true,
            BatteryPercent = source.BatteryPercent,
            LowBatteryActive = _lowBatterySessionActive,
            Active = _acSessionActive || _lowBatterySessionActive,
            PluggedPlan = cfg.PluggedPlan,
            SavedPlan = _planBeforeAcSession ?? _planBeforeLowBatterySession,
            TargetPlan = targetPlan,
            ManualOverrideActive = manualOverrideActive,
            Message = message,
        };
    }

    private void Publish(PowerSourcePlanState state)
    {
        var changed = !StatesEqual(_current, state);
        _current = state;
        if (changed)
            StateChanged?.Invoke(state);
    }

    private static bool StatesEqual(PowerSourcePlanState a, PowerSourcePlanState b)
        => a.Enabled == b.Enabled
           && a.PowerSourceKnown == b.PowerSourceKnown
           && a.PluggedIn == b.PluggedIn
           && a.BatteryPercent == b.BatteryPercent
           && a.LowBatteryActive == b.LowBatteryActive
           && a.Active == b.Active
           && a.PluggedPlan == b.PluggedPlan
           && a.SavedPlan == b.SavedPlan
           && a.TargetPlan == b.TargetPlan
           && a.ManualOverrideActive == b.ManualOverrideActive
           && a.Message == b.Message;

    private static bool IsLowBatteryOnDc(PowerSourceSnapshot source)
        => source.PluggedIn == false && source.BatteryPercent is < LowBatteryThresholdPercent;

    private static PowerSourceSnapshot NormalizeSnapshot(PowerSourceSnapshot? source)
        => new(source?.PluggedIn, NormalizeBatteryPercent(source?.BatteryPercent));

    private static int? NormalizeBatteryPercent(int? value)
        => value is >= 0 and <= 100 ? value.Value : null;

    private static PowerSourceSnapshot? GetSystemPowerSourceSnapshot()
    {
        if (!GetSystemPowerStatus(out var status))
            return null;

        bool? pluggedIn = status.ACLineStatus switch
        {
            0 => false,
            1 => true,
            _ => null,
        };

        int? batteryPercent = status.BatteryLifePercent == 255 ? null : status.BatteryLifePercent;
        return new PowerSourceSnapshot(pluggedIn, batteryPercent);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }
}
