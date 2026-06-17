using System.Runtime.InteropServices;
using VoltManager.Models;

namespace VoltManager.Services;

public record PowerSourcePlanDecision
{
    public PlanId? TargetPlan { get; init; }
    public bool BlocksLowerPriority { get; init; }
    public PowerSourcePlanState State { get; init; } = new();
}

public sealed class PowerSourcePlanService
{
    private readonly SettingsService _settings;
    private readonly Func<bool?> _powerSourceReader;
    private readonly object _lock = new();

    private bool _acSessionActive;
    private PlanId? _planBeforeAcSession;
    private PowerSourcePlanState _current = new();

    public event Action<PowerSourcePlanState>? StateChanged;

    public PowerSourcePlanService(SettingsService settings, Func<bool?>? powerSourceReader = null)
    {
        _settings = settings;
        _powerSourceReader = powerSourceReader ?? GetSystemPluggedInState;
        _current = BuildState(null, null, false, "");
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
            bool? pluggedIn = _powerSourceReader();

            if (manualOverrideActive)
                return Decision(null, false, pluggedIn, true, "manual_override");

            if (!cfg.Enabled)
            {
                if (_acSessionActive)
                {
                    var target = _planBeforeAcSession ?? PlanId.Balanced;
                    ClearSession();
                    return Decision(target, true, pluggedIn, manualOverrideActive, "disabled_restore");
                }

                return Decision(null, false, pluggedIn, manualOverrideActive, "disabled");
            }

            if (pluggedIn == null)
                return Decision(null, false, null, false, "unknown_power_source");

            if (pluggedIn.Value)
            {
                if (!_acSessionActive)
                {
                    _planBeforeAcSession = activePlan == cfg.PluggedPlan ? PlanId.Balanced : activePlan;
                    _acSessionActive = true;
                }

                var target = activePlan == cfg.PluggedPlan ? (PlanId?)null : cfg.PluggedPlan;
                return Decision(target, true, pluggedIn, false, target == null ? "plugged_active" : "plugged_switch");
            }

            if (_acSessionActive)
            {
                var target = _planBeforeAcSession ?? PlanId.Balanced;
                ClearSession();
                return Decision(activePlan == target ? null : target, true, pluggedIn, false, "unplugged_restore");
            }

            return Decision(null, false, pluggedIn, false, "unplugged_idle");
        }
    }

    public PowerSourcePlanState RefreshState(bool manualOverrideActive)
    {
        lock (_lock)
        {
            var state = BuildState(_powerSourceReader(), null, manualOverrideActive, "refresh");
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
        _acSessionActive = false;
        _planBeforeAcSession = null;
    }

    private PowerSourcePlanSettings EnsureSettings()
    {
        _settings.Current.PowerSourcePlan ??= new PowerSourcePlanSettings();
        return _settings.Current.PowerSourcePlan;
    }

    private PowerSourcePlanDecision Decision(PlanId? targetPlan, bool blocksLowerPriority, bool? pluggedIn,
        bool manualOverrideActive, string message)
    {
        var state = BuildState(pluggedIn, targetPlan, manualOverrideActive, message);
        Publish(state);
        return new PowerSourcePlanDecision
        {
            TargetPlan = targetPlan,
            BlocksLowerPriority = blocksLowerPriority,
            State = state,
        };
    }

    private PowerSourcePlanState BuildState(bool? pluggedIn, PlanId? targetPlan, bool manualOverrideActive, string message)
    {
        var cfg = EnsureSettings();
        return new PowerSourcePlanState
        {
            Enabled = cfg.Enabled,
            PowerSourceKnown = pluggedIn != null,
            PluggedIn = pluggedIn == true,
            Active = _acSessionActive,
            PluggedPlan = cfg.PluggedPlan,
            SavedPlan = _planBeforeAcSession,
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
           && a.Active == b.Active
           && a.PluggedPlan == b.PluggedPlan
           && a.SavedPlan == b.SavedPlan
           && a.TargetPlan == b.TargetPlan
           && a.ManualOverrideActive == b.ManualOverrideActive
           && a.Message == b.Message;

    private static bool? GetSystemPluggedInState()
    {
        if (!GetSystemPowerStatus(out var status))
            return null;

        return status.ACLineStatus switch
        {
            0 => false,
            1 => true,
            _ => null,
        };
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
